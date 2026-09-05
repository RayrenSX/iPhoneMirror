// SPDX-License-Identifier: GPL-3.0-only

#include "IpcProtocol.h"
#include "DlnaRenderer.h"
#include "HttpUrl.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <deque>
#include <filesystem>
#include <format>
#include <limits>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {

constexpr char StartExport[] = "?fgServerStart@@YAPEAXQEBDIIPEAVIAirServerCallback@@@Z";
constexpr char StopExport[] = "?fgServerStop@@YAXPEAX@Z";

std::once_flag diagnostic_salt_once;
std::array<unsigned char, 32> diagnostic_salt{};
bool diagnostic_salt_ready{};

void initialize_diagnostic_salt() noexcept {
    diagnostic_salt_ready = BCryptGenRandom(nullptr, diagnostic_salt.data(),
        static_cast<ULONG>(diagnostic_salt.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
}

std::string anonymous_label(std::string_view value) noexcept {
    try {
        if (value.empty()) return "anon-empty";
        if (value.size() > std::numeric_limits<ULONG>::max()) return "anon-too-large";
        std::call_once(diagnostic_salt_once, initialize_diagnostic_salt);
        if (!diagnostic_salt_ready) return "anon-unavailable";

        BCRYPT_ALG_HANDLE algorithm{};
        BCRYPT_HASH_HANDLE hash{};
        std::array<unsigned char, 32> digest{};
        bool success = BCryptOpenAlgorithmProvider(
            &algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0;
        success = success && BCryptCreateHash(
            algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
        success = success && BCryptHashData(hash, diagnostic_salt.data(),
            static_cast<ULONG>(diagnostic_salt.size()), 0) >= 0;
        success = success && BCryptHashData(hash,
            reinterpret_cast<PUCHAR>(const_cast<char*>(value.data())),
            static_cast<ULONG>(value.size()), 0) >= 0;
        success = success && BCryptFinishHash(hash, digest.data(),
            static_cast<ULONG>(digest.size()), 0) >= 0;
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        if (!success) return "anon-unavailable";

        constexpr char hex[] = "0123456789abcdef";
        std::string result = "anon-";
        result.reserve(result.size() + 24);
        for (std::size_t index = 0; index < 12; ++index) {
            result.push_back(hex[digest[index] >> 4]);
            result.push_back(hex[digest[index] & 0x0f]);
        }
        return result;
    } catch (...) {
        return "anon-unavailable";
    }
}

struct SFgAudioFrame {
    unsigned long long pts;
    unsigned int sampleRate;
    unsigned short channels;
    unsigned short bitsPerSample;
    unsigned int dataLen;
    unsigned char* data;
};

struct SFgVideoFrame {
    unsigned long long pts;
    int isKey;
    unsigned int width;
    unsigned int height;
    unsigned int pitch[3];
    unsigned int dataLen[3];
    unsigned int dataTotalLen;
    unsigned char* data;
};

class IAirServerCallback {
public:
    virtual void connected(const char* remote_name, const char* remote_device_id) = 0;
    virtual void disconnected(const char* remote_name, const char* remote_device_id) = 0;
    virtual void outputAudio(SFgAudioFrame* data, const char* remote_name,
        const char* remote_device_id) = 0;
    virtual void outputVideo(SFgVideoFrame* data, const char* remote_name,
        const char* remote_device_id) = 0;
    virtual void videoPlay(char* url, double volume, double start_position) = 0;
    virtual void videoGetPlayInfo(double* duration, double* position, double* rate) = 0;
    virtual void setVolume(float volume, const char* remote_name,
        const char* remote_device_id) = 0;
    virtual void log(int level, const char* message) = 0;
};

using StartServer = void* (__cdecl*)(const char*, unsigned int, unsigned int,
    IAirServerCallback*);
using StopServer = void (__cdecl*)(void*);

struct DeviceMetadata {
    std::string device_id;
    std::string product_type;
    std::string os_version;
};

std::optional<DeviceMetadata> parse_device_metadata(std::string_view message) {
    constexpr std::string_view prefix = "IPHONE_MIRROR_DEVICE_INFO\t";
    if (!message.starts_with(prefix)) return std::nullopt;
    message.remove_prefix(prefix.size());

    std::array<std::string_view, 3> fields{};
    for (std::size_t index = 0; index < fields.size(); ++index) {
        auto& field = fields[index];
        const auto separator = message.find('\t');
        if (index + 1 == fields.size()) {
            if (separator != std::string_view::npos) return std::nullopt;
            field = message;
        } else {
            if (separator == std::string_view::npos) return std::nullopt;
            field = message.substr(0, separator);
            message.remove_prefix(separator + 1);
        }
    }

    const auto valid_field = [](std::string_view value, std::size_t limit,
        bool allow_empty) {
        if ((!allow_empty && value.empty()) || value.size() >= limit) return false;
        return std::ranges::all_of(value, [](unsigned char character) {
            return character >= 0x20 && character <= 0x7e;
        });
    };
    if (!valid_field(fields[0], iPhoneMirror::wireless::DeviceIdBytes, false) ||
        !valid_field(fields[1], iPhoneMirror::wireless::ProductTypeBytes, true) ||
        !valid_field(fields[2], iPhoneMirror::wireless::OsVersionBytes, true))
        return std::nullopt;
    return DeviceMetadata{
        .device_id = std::string(fields[0]),
        .product_type = std::string(fields[1]),
        .os_version = std::string(fields[2]),
    };
}

float airplay_decibels_to_linear_gain(float decibels) noexcept {
    // The bundled AirPlay SDK reports RAOP volume in dB: 0 is full scale and
    // -144 is mute. iPhone/iPad use -30..0 dB as the volume slider range,
    // while the IPC contract and WASAPI renderer use a 0..1 slider value.
    if (!std::isfinite(decibels) || decibels <= -30.0F) return 0.0F;
    return std::clamp((decibels + 30.0F) / 30.0F, 0.0F, 1.0F);
}

bool write_all(HANDLE pipe, const void* source, std::size_t size,
    DWORD* failure_reason = nullptr) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size, 1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr)) {
            if (failure_reason) *failure_reason = GetLastError();
            return false;
        }
        if (written == 0) {
            if (failure_reason) *failure_reason = ERROR_BROKEN_PIPE;
            return false;
        }
        bytes += written;
        size -= written;
    }
    return true;
}

bool read_all(HANDLE pipe, void* destination, std::size_t size) noexcept {
    auto* bytes = static_cast<std::uint8_t*>(destination);
    while (size != 0) {
        DWORD read{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size, 1024U * 1024U));
        if (!ReadFile(pipe, bytes, request, &read, nullptr) || read == 0) return false;
        bytes += read;
        size -= read;
    }
    return true;
}

class IpcWriter {
public:
    explicit IpcWriter(HANDLE pipe) : pipe_(pipe), worker_([this] { run(); }) {}
    ~IpcWriter() { shutdown(); }
    IpcWriter(const IpcWriter&) = delete;
    IpcWriter& operator=(const IpcWriter&) = delete;

    bool send(iPhoneMirror::wireless::MessageHeader header,
        std::span<const std::uint8_t> payload = {}) noexcept {
        if (payload.size() > iPhoneMirror::wireless::MaxPayloadBytes) {
            rejected_payloads_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        try {
            header.payload_size = static_cast<std::uint32_t>(payload.size());
            const auto message_bytes = sizeof(header) + payload.size();
            if (message_bytes > MaxQueuedBytes) {
                rejected_capacity_.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            std::unique_lock lock(mutex_);
            if (closing_) {
                rejected_closing_.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            if (header.type == iPhoneMirror::wireless::MessageType::Video) {
                // Original-quality AirPlay frames are commonly 5-8 MB.  If
                // the pipe writer is still busy with the previous frame, do
                // not copy another identical frame into the queue: that copy
                // happens on AirPlay's decoder callback thread and can stall
                // the sender long enough to look like an orientation freeze.
                // A geometry change is always retained so a rotation can
                // replace the old frame as soon as it arrives.
                const auto same_device = [&header](const QueuedMessage& queued) {
                    return queued.header.type ==
                            iPhoneMirror::wireless::MessageType::Video &&
                        std::strncmp(queued.header.device_id, header.device_id,
                            iPhoneMirror::wireless::DeviceIdBytes) == 0;
                };
                const auto queued_video = std::ranges::find_if(queue_, same_device);
                const auto writing_same_device =
                    writing_video_device_ == std::string_view(header.device_id);
                const auto writing_same_geometry = writing_same_device &&
                    writing_video_width_ == header.width &&
                    writing_video_height_ == header.height;
                const auto queued_same_geometry = queued_video != queue_.end() &&
                    queued_video->header.width == header.width &&
                    queued_video->header.height == header.height;
                if ((queued_same_geometry || writing_same_geometry) &&
                    (queued_video != queue_.end() || writing_same_device)) {
                    dropped_video_.fetch_add(1, std::memory_order_relaxed);
                    return true;
                }
                for (auto position = queue_.begin(); position != queue_.end();) {
                    if (position->header.type == iPhoneMirror::wireless::MessageType::Video &&
                        std::strncmp(position->header.device_id, header.device_id,
                            iPhoneMirror::wireless::DeviceIdBytes) == 0) {
                        buffered_bytes_ -= position->size();
                        --buffered_messages_;
                        replaced_video_frames_.fetch_add(1, std::memory_order_relaxed);
                        position = queue_.erase(position);
                    }
                    else ++position;
                }
            }

            while (buffered_messages_ >= MaxQueuedMessages ||
                buffered_bytes_ + message_bytes > MaxQueuedBytes) {
                auto expendable = std::ranges::find_if(queue_, [](const QueuedMessage& queued) {
                    return queued.header.type == iPhoneMirror::wireless::MessageType::Audio;
                });
                if (expendable == queue_.end())
                    expendable = std::ranges::find_if(queue_, [](const QueuedMessage& queued) {
                        return is_media(queued.header.type);
                    });
                if (expendable == queue_.end())
                    expendable = std::ranges::find_if(queue_, [](const QueuedMessage& queued) {
                        return queued.header.type == iPhoneMirror::wireless::MessageType::Log;
                    });
                if (expendable == queue_.end()) {
                    rejected_capacity_.fetch_add(1, std::memory_order_relaxed);
                    return false;
                }
                switch (expendable->header.type) {
                case iPhoneMirror::wireless::MessageType::Audio:
                    dropped_audio_.fetch_add(1, std::memory_order_relaxed);
                    break;
                case iPhoneMirror::wireless::MessageType::Video:
                    dropped_video_.fetch_add(1, std::memory_order_relaxed);
                    break;
                case iPhoneMirror::wireless::MessageType::Log:
                    dropped_logs_.fetch_add(1, std::memory_order_relaxed);
                    break;
                default:
                    dropped_other_.fetch_add(1, std::memory_order_relaxed);
                    break;
                }
                buffered_bytes_ -= expendable->size();
                --buffered_messages_;
                queue_.erase(expendable);
            }

            // Copy only after capacity has been reserved. This keeps both the
            // queued and currently-writing media inside one hard memory bound,
            // even when several SDK callback threads publish concurrently.
            QueuedMessage message{.header = header};
            message.payload.assign(payload.begin(), payload.end());
            message.header.sequence = ++sequence_;
            queue_.push_back(std::move(message));
            buffered_bytes_ += message_bytes;
            ++buffered_messages_;
            lock.unlock();
            condition_.notify_one();
            enqueued_messages_.fetch_add(1, std::memory_order_relaxed);
            return true;
        } catch (...) {
            allocation_failures_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
    }

    bool send_text(iPhoneMirror::wireless::MessageType type,
        std::string_view text) noexcept {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = type;
        return send(header, std::span(
            reinterpret_cast<const std::uint8_t*>(text.data()), text.size()));
    }

    bool send_text(iPhoneMirror::wireless::MessageType type, const char* text) noexcept {
        return send_text(type, text ? std::string_view(text) : std::string_view{});
    }

    bool send_device(iPhoneMirror::wireless::MessageHeader header,
        const char* remote_name, const char* remote_device_id,
        std::span<const std::uint8_t> payload = {}) noexcept {
        copy_text(header.device_id, remote_device_id);
        copy_text(header.device_name, remote_name);
        return send(header, payload);
    }

    bool send_device_info(const DeviceMetadata& metadata) noexcept {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::DeviceInfo;
        copy_text(header.device_id, metadata.device_id.c_str());
        copy_text(header.product_type, metadata.product_type.c_str());
        copy_text(header.os_version, metadata.os_version.c_str());
        return send(header);
    }

    [[nodiscard]] std::string summary() const {
        std::scoped_lock lock(mutex_);
        return std::format(
            "ipc_summary enqueued={} written={} queued={} queued_bytes={} "
            "written_bytes={} "
            "replaced_video={} dropped_audio={} dropped_video={} dropped_log={} "
            "dropped_other={} rejected_payload={} rejected_capacity={} "
            "rejected_closing={} allocation_failures={} write_failures={} "
            "last_write_error={} write_dropped_messages={}",
            enqueued_messages_.load(std::memory_order_relaxed),
            written_messages_.load(std::memory_order_relaxed), buffered_messages_,
            buffered_bytes_, written_bytes_.load(std::memory_order_relaxed),
            replaced_video_frames_.load(std::memory_order_relaxed),
            dropped_audio_.load(std::memory_order_relaxed),
            dropped_video_.load(std::memory_order_relaxed),
            dropped_logs_.load(std::memory_order_relaxed),
            dropped_other_.load(std::memory_order_relaxed),
            rejected_payloads_.load(std::memory_order_relaxed),
            rejected_capacity_.load(std::memory_order_relaxed),
            rejected_closing_.load(std::memory_order_relaxed),
            allocation_failures_.load(std::memory_order_relaxed),
            write_failures_.load(std::memory_order_relaxed),
            last_write_error_.load(std::memory_order_relaxed),
            write_dropped_messages_.load(std::memory_order_relaxed));
    }

    void shutdown() noexcept {
        std::thread worker;
        {
            std::scoped_lock lock(mutex_);
            closing_ = true;
            worker = std::move(worker_);
        }
        condition_.notify_all();
        if (worker.joinable()) worker.join();
    }

private:
    struct QueuedMessage {
        iPhoneMirror::wireless::MessageHeader header;
        std::vector<std::uint8_t> payload;

        [[nodiscard]] std::size_t size() const noexcept {
            return sizeof(header) + payload.size();
        }
    };

    static constexpr std::size_t MaxQueuedMessages = 96;
    static constexpr std::size_t MaxQueuedBytes =
        iPhoneMirror::wireless::MaxPayloadBytes + 8U * 1024U * 1024U;

    static bool is_media(iPhoneMirror::wireless::MessageType type) noexcept {
        return type == iPhoneMirror::wireless::MessageType::Video ||
            type == iPhoneMirror::wireless::MessageType::Audio;
    }

    template <std::size_t Size>
    static void copy_text(char (&destination)[Size], const char* source) noexcept {
        if (!source) return;
        // SDK callback strings are expected to be NUL terminated, but bound
        // the probe so a malformed callback cannot scan arbitrary memory.
        const auto length = strnlen_s(source, Size - 1);
        std::memcpy(destination, source, length);
        destination[length] = '\0';
    }

    void run() noexcept {
        while (true) {
            QueuedMessage message;
            {
                std::unique_lock lock(mutex_);
                condition_.wait(lock, [this] { return closing_ || !queue_.empty(); });
                if (queue_.empty()) {
                    if (closing_) return;
                    continue;
                }
                message = std::move(queue_.front());
                queue_.pop_front();
                if (message.header.type ==
                    iPhoneMirror::wireless::MessageType::Video)
                {
                    writing_video_device_ = message.header.device_id;
                    writing_video_width_ = message.header.width;
                    writing_video_height_ = message.header.height;
                } else {
                    writing_video_device_.clear();
                    writing_video_width_ = writing_video_height_ = 0;
                }
            }

            // A large original-quality frame may still be in the writer's
            // local buffer when iOS rotates. If a newer frame for the same
            // device is already queued, discard this stale frame before
            // entering WriteFile so rotation is not hidden behind an old
            // multi-megabyte payload.
            if (message.header.type == iPhoneMirror::wireless::MessageType::Video) {
                std::scoped_lock lock(mutex_);
                const auto newer = std::ranges::any_of(queue_,
                    [&message](const QueuedMessage& queued) {
                        return queued.header.type ==
                                iPhoneMirror::wireless::MessageType::Video &&
                            std::strncmp(queued.header.device_id,
                                message.header.device_id,
                                iPhoneMirror::wireless::DeviceIdBytes) == 0 &&
                            queued.header.sequence > message.header.sequence;
                    });
                if (newer) {
                    buffered_bytes_ -= message.size();
                    --buffered_messages_;
                    dropped_video_.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }
            }

            DWORD write_error = ERROR_SUCCESS;
            const auto written = write_all(pipe_, &message.header, sizeof(message.header),
                &write_error) &&
                (message.payload.empty() ||
                    write_all(pipe_, message.payload.data(), message.payload.size(),
                        &write_error));
            std::scoped_lock lock(mutex_);
            buffered_bytes_ -= message.size();
            --buffered_messages_;
            if (message.header.type == iPhoneMirror::wireless::MessageType::Video)
            {
                writing_video_device_.clear();
                writing_video_width_ = writing_video_height_ = 0;
            }
            if (!written) {
                write_failures_.fetch_add(1, std::memory_order_relaxed);
                last_write_error_.store(write_error, std::memory_order_relaxed);
                write_dropped_messages_.fetch_add(
                    static_cast<std::uint64_t>(buffered_messages_),
                    std::memory_order_relaxed);
                closing_ = true;
                queue_.clear();
                buffered_bytes_ = 0;
                buffered_messages_ = 0;
                return;
            }
            written_messages_.fetch_add(1, std::memory_order_relaxed);
            written_bytes_.fetch_add(static_cast<std::uint64_t>(message.size()),
                std::memory_order_relaxed);
        }
    }

    HANDLE pipe_{};
    mutable std::mutex mutex_;
    std::condition_variable condition_;
    std::deque<QueuedMessage> queue_;
    std::string writing_video_device_;
    std::uint32_t writing_video_width_{};
    std::uint32_t writing_video_height_{};
    std::size_t buffered_bytes_{};
    std::size_t buffered_messages_{};
    std::uint64_t sequence_{};
    bool closing_{};
    std::thread worker_;
    std::atomic_uint64_t enqueued_messages_{};
    std::atomic_uint64_t written_messages_{};
    std::atomic_uint64_t written_bytes_{};
    std::atomic_uint64_t replaced_video_frames_{};
    std::atomic_uint64_t dropped_audio_{};
    std::atomic_uint64_t dropped_video_{};
    std::atomic_uint64_t dropped_logs_{};
    std::atomic_uint64_t dropped_other_{};
    std::atomic_uint64_t rejected_payloads_{};
    std::atomic_uint64_t rejected_capacity_{};
    std::atomic_uint64_t rejected_closing_{};
    std::atomic_uint64_t allocation_failures_{};
    std::atomic_uint64_t write_failures_{};
    std::atomic_uint32_t last_write_error_{};
    std::atomic_uint64_t write_dropped_messages_{};
};

class AirPlayCallback final : public IAirServerCallback {
public:
    explicit AirPlayCallback(IpcWriter& writer) : writer_(writer) {}

    void connected(const char* remote_name, const char* remote_device_id) override {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Connected;
        const auto sent = writer_.send_device(header, remote_name, remote_device_id);
        connected_callbacks_.fetch_add(1, std::memory_order_relaxed);
        diagnostic(std::format("callback connected remote_fp={} device_fp={} sent={}",
            anonymous_label(safe_text(remote_name)),
            anonymous_label(safe_text(remote_device_id)), sent));
    }

    void disconnected(const char* remote_name, const char* remote_device_id) override {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Disconnected;
        const auto sent = writer_.send_device(header, remote_name, remote_device_id);
        disconnected_callbacks_.fetch_add(1, std::memory_order_relaxed);
        diagnostic(std::format("callback disconnected remote_fp={} device_fp={} sent={}",
            anonymous_label(safe_text(remote_name)),
            anonymous_label(safe_text(remote_device_id)), sent));
    }

    void outputAudio(SFgAudioFrame* data, const char* remote_name,
        const char* remote_device_id) override {
        const auto callback_index = audio_callbacks_.fetch_add(1,
            std::memory_order_relaxed) + 1;
        if (!data) {
            rejected_audio_.fetch_add(1, std::memory_order_relaxed);
            diagnostic("audio callback rejected: null frame");
            return;
        }
        if (!data->data || data->dataLen == 0 || data->dataLen >
            iPhoneMirror::wireless::MaxPayloadBytes) {
            rejected_audio_.fetch_add(1, std::memory_order_relaxed);
            diagnostic(std::format(
                "audio callback rejected index={} bytes={} rate={} channels={} bits={}",
                callback_index, data->dataLen, data->sampleRate, data->channels,
                data->bitsPerSample));
            return;
        }
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Audio;
        header.sample_rate = data->sampleRate;
        header.channels = data->channels;
        header.bits_per_sample = data->bitsPerSample;
        const auto sent = writer_.send_device(header, remote_name, remote_device_id,
            std::span(data->data, data->dataLen));
        if (!sent) send_failures_.fetch_add(1, std::memory_order_relaxed);
        if (callback_index == 1 || callback_index % 500 == 0) {
            diagnostic(std::format(
                "audio callback index={} bytes={} rate={} channels={} bits={} sent={}",
                callback_index, data->dataLen, data->sampleRate, data->channels,
                data->bitsPerSample, sent));
        }
    }

    void outputVideo(SFgVideoFrame* data, const char* remote_name,
        const char* remote_device_id) override {
        const auto callback_index = video_callbacks_.fetch_add(1,
            std::memory_order_relaxed) + 1;
        if (!data) {
            rejected_video_.fetch_add(1, std::memory_order_relaxed);
            diagnostic("video callback rejected: null frame");
            return;
        }
        if (!data->data || data->width == 0 || data->height == 0 ||
            data->width > 8192 || data->height > 8192 || data->dataTotalLen == 0 ||
            data->dataTotalLen > iPhoneMirror::wireless::MaxPayloadBytes) {
            rejected_video_.fetch_add(1, std::memory_order_relaxed);
            diagnostic(std::format(
                "video callback rejected index={} size={}x{} bytes={} data={}",
                callback_index, data->width, data->height, data->dataTotalLen,
                data->data != nullptr));
            return;
        }
        const auto plane_total = static_cast<std::uint64_t>(data->dataLen[0]) +
            data->dataLen[1] + data->dataLen[2];
        if (plane_total > data->dataTotalLen || data->pitch[0] < data->width) {
            rejected_video_.fetch_add(1, std::memory_order_relaxed);
            diagnostic(std::format(
                "video callback rejected index={} size={}x{} pitches={}/{}/{} "
                "planes={}/{}/{} total={}", callback_index, data->width, data->height,
                data->pitch[0], data->pitch[1], data->pitch[2], data->dataLen[0],
                data->dataLen[1], data->dataLen[2], data->dataTotalLen));
            return;
        }

        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Video;
        header.width = data->width;
        header.height = data->height;
        for (int index = 0; index < 3; ++index) {
            header.stride[index] = data->pitch[index];
            header.plane_size[index] = data->dataLen[index];
        }
        const auto sent = writer_.send_device(header, remote_name, remote_device_id,
            std::span(data->data, data->dataTotalLen));
        if (!sent) send_failures_.fetch_add(1, std::memory_order_relaxed);
        if (callback_index == 1 || callback_index % 300 == 0) {
            diagnostic(std::format(
                "video callback index={} size={}x{} pitches={}/{}/{} "
                "planes={}/{}/{} total={} sent={}", callback_index, data->width,
                data->height, data->pitch[0], data->pitch[1], data->pitch[2],
                data->dataLen[0], data->dataLen[1], data->dataLen[2],
                data->dataTotalLen, sent));
        }
    }

    void videoPlay(char* url, double volume, double start_position) override {
        iPhoneMirror::wireless::MessageHeader header;
        // Keep URL inspection bounded. A malformed SDK pointer or an
        // unterminated URL must be rejected rather than read without limit.
        std::string_view location = url
            ? safe_text(url, 16U * 1024U + 1U) : std::string_view{};
        if (location == "iphonemirror://pause") {
            diagnostic("media control request action=pause source=airplay_url");
            mediaPause();
            return;
        }
        if (location == "iphonemirror://resume") {
            diagnostic("media control request action=resume source=airplay_url");
            mediaResume();
            return;
        }
        if (location == "iphonemirror://seek") {
            diagnostic(std::format(
                "media control request action=seek source=airplay_url position={:.3f}",
                start_position));
            mediaSeek(start_position);
            return;
        }
        if (sendMediaPlay(location, volume, start_position, std::nullopt)) return;

        header.type = iPhoneMirror::wireless::MessageType::MediaStop;
        bool sent{};
        {
            std::scoped_lock lock(playback_mutex_);
            header.media_command_id = media_command_id_ + 1;
            const PlaybackState next_playback{
                .command_id = header.media_command_id,
            };
            sent = writer_.send(header);
            if (sent) {
                media_command_id_ = header.media_command_id;
                playback_ = next_playback;
            }
        }
        if (!sent) media_send_failures_.fetch_add(1, std::memory_order_relaxed);
        diagnostic(std::format("media stop command={} reason=invalid_or_unsupported_url "
            "url_bytes={} sent={}", header.media_command_id, location.size(), sent));
    }

    void playDlnaMedia(std::string_view url, double duration, double volume,
        double start_position, bool muted) {
        if (!sendMediaPlay(url, volume, start_position, muted, duration))
            diagnostic("dlna media play rejected: URL is not HTTP(S)");
    }

    void mediaPause() noexcept {
        send_media_control(iPhoneMirror::wireless::MessageType::MediaPause, 0);
    }

    void mediaResume() noexcept {
        send_media_control(iPhoneMirror::wireless::MessageType::MediaResume, 0);
    }

    void mediaSeek(double position) noexcept {
        if (!std::isfinite(position) || position < 0) {
            diagnostic("media seek rejected: position is not finite or is negative");
            return;
        }
        send_media_control(iPhoneMirror::wireless::MessageType::MediaSeek,
            position);
    }

    void videoGetPlayInfo(double* duration, double* position, double* rate) override {
        std::scoped_lock lock(playback_mutex_);
        auto current_position = playback_.position;
        if (playback_.rate != 0) {
            current_position += std::chrono::duration<double>(
                std::chrono::steady_clock::now() - playback_.updated_at).count() *
                playback_.rate;
        }
        if (playback_.duration > 0)
            current_position = std::clamp(current_position, 0.0, playback_.duration);
        if (duration) *duration = playback_.duration;
        if (position) *position = current_position;
        if (rate) *rate = playback_.rate;
    }

    void updatePlayback(const iPhoneMirror::wireless::MessageHeader& header) noexcept {
        if (header.type != iPhoneMirror::wireless::MessageType::PlaybackState) return;
        std::scoped_lock lock(playback_mutex_);
        if (header.media_command_id != playback_.command_id || playback_.stopped) {
            const auto ignored = ignored_playback_updates_.fetch_add(
                1, std::memory_order_relaxed) + 1;
            if (ignored == 1 || ignored % 100 == 0) {
                diagnosticf(
                    "playback update ignored command={} expected={} stopped={} index={}",
                    header.media_command_id, playback_.command_id, playback_.stopped,
                    ignored);
            }
            return;
        }
        playback_.duration = std::max(0.0, header.media_duration);
        playback_.position = std::max(0.0, header.media_position);
        playback_.rate = header.media_rate;
        playback_.updated_at = std::chrono::steady_clock::now();
    }

    bool stopPlaybackFromController(std::uint64_t command_id) noexcept {
        {
            std::scoped_lock lock(playback_mutex_);
            if (command_id == 0 || command_id != playback_.command_id) {
                diagnosticf("media stop request rejected command={} expected={}",
                    command_id, playback_.command_id);
                return false;
            }
            if (playback_.rate != 0) {
                playback_.position += std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - playback_.updated_at).count() *
                    playback_.rate;
                if (playback_.duration > 0)
                    playback_.position = std::clamp(
                        playback_.position, 0.0, playback_.duration);
            }
            playback_.rate = 0;
            playback_.stopped = true;
            playback_.updated_at = std::chrono::steady_clock::now();
        }
        diagnosticf("media stop requested by controller command={}", command_id);
        return true;
    }

    void setVolume(float volume, const char* remote_name,
        const char* remote_device_id) override {
        const auto valid = std::isfinite(volume);
        const auto normalized = valid
            ? airplay_decibels_to_linear_gain(volume) : 0.0F;
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::MediaVolume;
        header.reserved = static_cast<std::uint32_t>(
            iPhoneMirror::wireless::MediaVolumeTarget::MirroredStream);
        header.media_volume = normalized;
        header.media_command_id = mirror_volume_command_id_.fetch_add(
            1, std::memory_order_relaxed) + 1;
        const auto has_device = remote_device_id != nullptr &&
            strnlen_s(remote_device_id,
                iPhoneMirror::wireless::DeviceIdBytes) != 0;
        bool sent{};
        if (valid && has_device)
            sent = writer_.send_device(header, remote_name, remote_device_id);
        if (valid && has_device && !sent)
            media_send_failures_.fetch_add(1, std::memory_order_relaxed);
        diagnostic(std::format(
            "callback volume remote_fp={} device_fp={} db={:.3f} gain={:.3f} "
            "valid={} has_device={} sent={}",
            anonymous_label(safe_text(remote_name)),
            anonymous_label(safe_text(remote_device_id)),
            volume, normalized, valid, has_device, sent));
    }

    void mediaVolume(double volume,
        std::optional<bool> muted = std::nullopt) noexcept {
        if (!std::isfinite(volume)) {
            diagnostic("media volume rejected: value is not finite");
            return;
        }
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::MediaVolume;
        header.reserved = static_cast<std::uint32_t>(
            iPhoneMirror::wireless::MediaVolumeTarget::MediaCast);
        if (muted.has_value()) {
            header.reserved |= iPhoneMirror::wireless::MediaVolumeMuteSpecified;
            if (*muted)
                header.reserved |= iPhoneMirror::wireless::MediaVolumeMuted;
        }
        header.media_volume = std::clamp(volume, 0.0, 1.0);
        bool sent{};
        {
            std::scoped_lock lock(playback_mutex_);
            header.media_command_id = media_command_id_ + 1;
            auto next_playback = playback_;
            if (next_playback.rate != 0) {
                next_playback.position += std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - next_playback.updated_at).count() *
                    next_playback.rate;
                if (next_playback.duration > 0)
                    next_playback.position = std::clamp(
                        next_playback.position, 0.0, next_playback.duration);
            }
            next_playback.command_id = header.media_command_id;
            next_playback.updated_at = std::chrono::steady_clock::now();
            sent = writer_.send(header);
            if (sent) {
                media_command_id_ = header.media_command_id;
                playback_ = next_playback;
            }
        }
        if (!sent) media_send_failures_.fetch_add(1, std::memory_order_relaxed);
        diagnosticf(
            "media volume command={} value={:.3f} mute_specified={} muted={} sent={}",
            header.media_command_id, header.media_volume, muted.has_value(),
            muted.value_or(false), sent);
    }

    void log(int level, const char* message) override {
        const auto text = safe_text(message);
        if (const auto metadata = parse_device_metadata(text)) {
            const auto sent = writer_.send_device_info(*metadata);
            diagnostic(std::format("device metadata device_fp={} model={} os={} sent={}",
                anonymous_label(metadata->device_id), metadata->product_type,
                metadata->os_version,
                sent));
            if (!sent) send_failures_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        const auto log_index = log_callbacks_.fetch_add(1, std::memory_order_relaxed) + 1;
        // AirPlayServer uses syslog levels (INFO=6, DEBUG=7). Keep the opening
        // handshake in full, then retain errors only so a long stream cannot
        // flood the media pipe or the application log.
        if (log_index <= 512 || level <= 4) {
            diagnostic(std::format("airplay level={} message_bytes={} message_fp={}",
                level, text.size(), anonymous_label(text)));
        } else {
            suppressed_log_callbacks_.fetch_add(1, std::memory_order_relaxed);
        }
    }

private:
    struct PlaybackState {
        std::uint64_t command_id{};
        double duration{};
        double position{};
        double rate{};
        bool stopped{};
        std::chrono::steady_clock::time_point updated_at{std::chrono::steady_clock::now()};
    };

    bool sendMediaPlay(std::string_view location, double volume,
        double start_position, std::optional<bool> muted,
        double duration = 0) {
        if (!iPhoneMirror::wireless::is_valid_http_url(location)) return false;

        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::MediaPlay;
        header.media_position = std::isfinite(start_position)
            ? std::max(0.0, start_position) : 0.0;
        header.media_duration = std::isfinite(duration) && duration > 0
            ? duration : 0.0;
        header.media_volume = std::isfinite(volume)
            ? std::clamp(volume, 0.0, 1.0) : 1.0;
        if (muted.has_value()) {
            header.reserved = iPhoneMirror::wireless::MediaVolumeMuteSpecified;
            if (*muted)
                header.reserved |= iPhoneMirror::wireless::MediaVolumeMuted;
        }
        bool sent{};
        {
            std::scoped_lock lock(playback_mutex_);
            header.media_command_id = media_command_id_ + 1;
            const PlaybackState next_playback{
                .command_id = header.media_command_id,
                .position = header.media_position,
                .rate = 1.0,
                .updated_at = std::chrono::steady_clock::now(),
            };
            sent = writer_.send(header, std::span(
                reinterpret_cast<const std::uint8_t*>(location.data()),
                location.size()));
            if (sent) {
                media_command_id_ = header.media_command_id;
                playback_ = next_playback;
            }
        }
        if (!sent) media_send_failures_.fetch_add(1, std::memory_order_relaxed);
        diagnosticf(
            "media play command={} start={:.3f} volume={:.3f} mute_specified={} "
            "muted={} url_bytes={} sent={}", header.media_command_id,
            header.media_position, header.media_volume, muted.has_value(),
            muted.value_or(false), location.size(), sent);
        return true;
    }

    static std::string_view safe_text(const char* value,
        std::size_t maximum = 64U * 1024U) noexcept {
        return value ? std::string_view(value, strnlen_s(value, maximum))
                     : std::string_view("<null>");
    }

    template <typename... Args>
    void diagnosticf(std::format_string<Args...> pattern, Args&&... args) noexcept {
        try {
            const auto message = std::format(pattern, std::forward<Args>(args)...);
            diagnostic(message);
        } catch (...) {
            // Diagnostics must never terminate an SDK callback on allocation
            // or formatting failure.
        }
    }

    void diagnostic(std::string_view message) noexcept {
        writer_.send_text(iPhoneMirror::wireless::MessageType::Log, message);
    }

    void send_media_control(iPhoneMirror::wireless::MessageType type,
        double position) noexcept {
        if (!std::isfinite(position) || position < 0) {
            diagnostic("media control rejected: invalid position");
            return;
        }
        iPhoneMirror::wireless::MessageHeader header;
        header.type = type;
        bool sent{};
        {
            std::scoped_lock lock(playback_mutex_);
            header.media_command_id = media_command_id_ + 1;
            auto next_playback = playback_;
            if (type == iPhoneMirror::wireless::MessageType::MediaSeek) {
                next_playback.position = position;
            } else {
                auto current = next_playback.position;
                if (next_playback.rate != 0) {
                    current += std::chrono::duration<double>(
                        std::chrono::steady_clock::now() - next_playback.updated_at).count() *
                        next_playback.rate;
                }
                next_playback.position = current;
                next_playback.rate = type == iPhoneMirror::wireless::MessageType::MediaPause
                    ? 0.0 : 1.0;
            }
            next_playback.command_id = header.media_command_id;
            next_playback.updated_at = std::chrono::steady_clock::now();
            header.media_position = next_playback.position;
            sent = writer_.send(header);
            if (sent) {
                media_command_id_ = header.media_command_id;
                playback_ = next_playback;
            }
        }
        if (!sent) media_send_failures_.fetch_add(1, std::memory_order_relaxed);
        diagnosticf("media control type={} command={} position={:.3f} sent={}",
            static_cast<unsigned>(type), header.media_command_id,
            header.media_position, sent);
    }

public:
    [[nodiscard]] std::string summary() const {
        return std::format(
            "callback_summary connected={} disconnected={} audio={} video={} "
            "audio_rejected={} video_rejected={} send_failures={} media_send_failures={} "
            "ignored_playback_updates={} airplay_logs={} suppressed_airplay_logs={}",
            connected_callbacks_.load(std::memory_order_relaxed),
            disconnected_callbacks_.load(std::memory_order_relaxed),
            audio_callbacks_.load(std::memory_order_relaxed),
            video_callbacks_.load(std::memory_order_relaxed),
            rejected_audio_.load(std::memory_order_relaxed),
            rejected_video_.load(std::memory_order_relaxed),
            send_failures_.load(std::memory_order_relaxed),
            media_send_failures_.load(std::memory_order_relaxed),
            ignored_playback_updates_.load(std::memory_order_relaxed),
            log_callbacks_.load(std::memory_order_relaxed),
            suppressed_log_callbacks_.load(std::memory_order_relaxed));
    }

private:
    IpcWriter& writer_;
    std::mutex playback_mutex_;
    std::uint64_t media_command_id_{GetTickCount64() << 16};
    std::atomic_uint64_t mirror_volume_command_id_{GetTickCount64() << 16};
    PlaybackState playback_;
    std::atomic_uint64_t audio_callbacks_{};
    std::atomic_uint64_t video_callbacks_{};
    std::atomic_uint64_t log_callbacks_{};
    std::atomic_uint64_t connected_callbacks_{};
    std::atomic_uint64_t disconnected_callbacks_{};
    std::atomic_uint64_t rejected_audio_{};
    std::atomic_uint64_t rejected_video_{};
    std::atomic_uint64_t send_failures_{};
    std::atomic_uint64_t media_send_failures_{};
    std::atomic_uint64_t ignored_playback_updates_{};
    std::atomic_uint64_t suppressed_log_callbacks_{};
};

std::wstring argument_value(int argc, wchar_t** argv, std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index)
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    return {};
}

bool has_argument(int argc, wchar_t** argv, std::wstring_view name) noexcept {
    for (int index = 1; index < argc; ++index)
        if (std::wstring_view(argv[index]) == name) return true;
    return false;
}

unsigned int argument_uint(int argc, wchar_t** argv, std::wstring_view name,
    unsigned int fallback) noexcept {
    const auto value = argument_value(argc, argv, name);
    if (value.empty()) return fallback;
    try {
        std::size_t consumed{};
        const auto parsed = std::stoul(value, &consumed);
        return consumed == value.size() && parsed <= 65535 ?
            static_cast<unsigned int>(parsed) : fallback;
    } catch (...) {
        return fallback;
    }
}

bool supported_capability(unsigned int width, unsigned int height,
    unsigned int fps) noexcept {
    const auto matches = [width, height](unsigned int long_edge,
        unsigned int short_edge) {
        return (width == long_edge && height == short_edge) ||
            (width == short_edge && height == long_edge);
    };
    return (matches(5120, 2880) && fps == 60) ||
        (matches(1920, 1080) && fps == 60) ||
        (matches(1280, 720) && fps == 30) ||
        (matches(960, 540) && fps == 30);
}

std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto length = WideCharToMultiByte(CP_UTF8, 0, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
        result.data(), length, nullptr, nullptr);
    return result;
}

bool set_airplay_environment(std::wstring_view name,
    std::wstring_view value) {
    const std::wstring wide_name(name);
    const std::wstring wide_value(value);
    // The receiver DLL synchronizes these Win32 values into its own CRT before
    // initializing AirPlayServer; the DNS-SD shim reads them directly.
    return SetEnvironmentVariableW(wide_name.c_str(), wide_value.c_str()) != FALSE;
}

std::wstring stable_airplay_device_id() {
    wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1]{};
    DWORD length = static_cast<DWORD>(std::size(computer));
    if (!GetComputerNameW(computer, &length)) {
        constexpr wchar_t fallback[] = L"iPhoneMirror";
        std::copy(std::begin(fallback), std::end(fallback), computer);
        length = static_cast<DWORD>(std::size(fallback) - 1);
    }
    std::uint64_t hash = 1469598103934665603ULL;
    for (DWORD index = 0; index < length; ++index) {
        hash ^= static_cast<std::uint8_t>(computer[index]);
        hash *= 1099511628211ULL;
    }
    constexpr std::string_view profile = "airplay-compat-v3";
    for (const auto byte : profile) {
        hash ^= static_cast<std::uint8_t>(byte);
        hash *= 1099511628211ULL;
    }
    return std::format(L"02:{:02X}:{:02X}:{:02X}:{:02X}:{:02X}",
        static_cast<std::uint8_t>(hash), static_cast<std::uint8_t>(hash >> 8),
        static_cast<std::uint8_t>(hash >> 16), static_cast<std::uint8_t>(hash >> 24),
        static_cast<std::uint8_t>(hash >> 32));
}

std::wstring stable_airplay_pairing_seed(std::wstring_view device_id) {
    constexpr std::string_view salt = "iPhoneMirror AirPlay pairing identity v1";
    const auto identity = utf8(device_id);
    std::array<unsigned char, 32> digest{};
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    const auto opened = BCryptOpenAlgorithmProvider(
        &algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    const auto created = opened >= 0 && BCryptCreateHash(
        algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    const auto hashed_identity = created && BCryptHashData(hash,
        reinterpret_cast<PUCHAR>(const_cast<char*>(identity.data())),
        static_cast<ULONG>(identity.size()), 0) >= 0;
    const auto hashed_salt = hashed_identity && BCryptHashData(hash,
        reinterpret_cast<PUCHAR>(const_cast<char*>(salt.data())),
        static_cast<ULONG>(salt.size()), 0) >= 0;
    const auto finished = hashed_salt && BCryptFinishHash(
        hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!finished) return {};

    std::wstring result;
    result.reserve(digest.size() * 2);
    constexpr wchar_t hex[] = L"0123456789abcdef";
    for (const auto byte : digest) {
        result.push_back(hex[byte >> 4]);
        result.push_back(hex[byte & 0x0f]);
    }
    return result;
}

std::string stable_dlna_uuid(std::wstring_view pairing_seed) {
    auto value = utf8(pairing_seed);
    if (value.size() != 64) return "uuid:7f1e0000-0000-4000-8000-000000000001";
    value[12] = '4';
    value[16] = "89ab"[static_cast<unsigned>(value[16]) % 4];
    return std::format("uuid:{}-{}-{}-{}-{}", value.substr(0, 8),
        value.substr(8, 4), value.substr(12, 4), value.substr(16, 4),
        value.substr(20, 12));
}

std::wstring dlna_receiver_name(std::wstring name) {
    constexpr std::wstring_view suffix = L" AirPlay";
    if (name.ends_with(suffix)) name.resize(name.size() - suffix.size());
    name += L" Video";
    return name;
}

std::filesystem::path executable_directory() {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(),
        static_cast<DWORD>(path.size()));
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

struct AirPlayLibraryLoad {
    HMODULE library{};
    DLL_DIRECTORY_COOKIE search_cookie{};
    DWORD error{};
};

AirPlayLibraryLoad load_airplay_library(const std::filesystem::path& path) noexcept {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
    const auto search_cookie = AddDllDirectory(path.parent_path().c_str());
    const auto library = LoadLibraryExW(path.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 |
        LOAD_LIBRARY_SEARCH_USER_DIRS);
    return {
        .library = library,
        .search_cookie = search_cookie,
        .error = library ? ERROR_SUCCESS : GetLastError(),
    };
}

void close_airplay_library(const AirPlayLibraryLoad& loaded) noexcept {
    if (loaded.library) FreeLibrary(loaded.library);
    if (loaded.search_cookie) RemoveDllDirectory(loaded.search_cookie);
}

bool is_code_integrity_error(DWORD error) noexcept {
    return error == ERROR_INVALID_IMAGE_HASH ||
        error == ERROR_ACCESS_DISABLED_BY_POLICY ||
        (error >= ERROR_SYSTEM_INTEGRITY_ROLLBACK_DETECTED &&
            error <= ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE) ||
        (error >= ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE &&
            error <= ERROR_SYSTEM_INTEGRITY_WHQL_NOT_SATISFIED);
}

DWORD probe_image(const std::filesystem::path& path) noexcept {
    const auto file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    const auto image = CreateFileMappingW(file, nullptr,
        PAGE_READONLY | SEC_IMAGE, 0, 0, nullptr);
    const auto error = image ? ERROR_SUCCESS : GetLastError();
    if (image) CloseHandle(image);
    CloseHandle(file);
    return error;
}

DWORD probe_runtime_images(const std::filesystem::path& library_path) noexcept {
    try {
        const auto directory = library_path.parent_path();
        const std::array paths{
            library_path,
            directory / L"avcodec-58.dll",
            directory / L"avutil-56.dll",
            directory / L"dnssd.dll",
            directory / L"swresample-3.dll",
            directory / L"swscale-5.dll",
        };
        for (const auto& path : paths) {
            const auto error = probe_image(path);
            if (error != ERROR_SUCCESS) return error;
        }
        return ERROR_SUCCESS;
    } catch (...) {
        return ERROR_NOT_ENOUGH_MEMORY;
    }
}

int preflight_airplay_runtime(const std::filesystem::path& path) noexcept {
    const auto image_error = probe_runtime_images(path);
    if (image_error != ERROR_SUCCESS)
        return is_code_integrity_error(image_error) ? 40 : 41;
    const auto loaded = load_airplay_library(path);
    if (!loaded.library) {
        close_airplay_library(loaded);
        return is_code_integrity_error(loaded.error) ? 40 : 41;
    }
    const auto exports_available =
        GetProcAddress(loaded.library, StartExport) != nullptr &&
        GetProcAddress(loaded.library, StopExport) != nullptr;
    close_airplay_library(loaded);
    return exports_available ? 0 : 42;
}

HANDLE connect_pipe(const std::wstring& pipe_name) {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto pipe = CreateFileW(pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        if (GetLastError() != ERROR_PIPE_BUSY && GetLastError() != ERROR_FILE_NOT_FOUND)
            return INVALID_HANDLE_VALUE;
        WaitNamedPipeW(pipe_name.c_str(), 100);
    }
    return INVALID_HANDLE_VALUE;
}

bool receive_playback_updates(HANDLE pipe, AirPlayCallback& callback,
    iPhoneMirror::wireless::DlnaRenderer& dlna, DWORD* failure_reason = nullptr) noexcept {
    if (failure_reason) *failure_reason = ERROR_SUCCESS;
    DWORD available{};
    while (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) {
        if (available < sizeof(iPhoneMirror::wireless::MessageHeader)) return true;
        iPhoneMirror::wireless::MessageHeader header;
        if (!read_all(pipe, &header, sizeof(header))) {
            if (failure_reason) *failure_reason = GetLastError();
            return false;
        }
        if (header.magic != iPhoneMirror::wireless::IpcMagic ||
            header.version != iPhoneMirror::wireless::IpcVersion ||
            header.payload_size != 0) {
            if (failure_reason) *failure_reason = ERROR_INVALID_DATA;
            return false;
        }
        if (header.type == iPhoneMirror::wireless::MessageType::PlaybackState) {
            callback.updatePlayback(header);
        } else if (header.type ==
            iPhoneMirror::wireless::MessageType::MediaStopRequest) {
            if (callback.stopPlaybackFromController(header.media_command_id))
                dlna.set_transport_stopped();
        } else {
            if (failure_reason) *failure_reason = ERROR_INVALID_DATA;
            return false;
        }
    }
    if (failure_reason) *failure_reason = GetLastError();
    return false;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    // LoadLibrary failures must be reported through IPC/preflight instead of a
    // system "Bad Image" dialog that incorrectly describes policy blocks as corruption.
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS);
    SetThreadErrorMode(SEM_FAILCRITICALERRORS, nullptr);
    const auto pipe_name = argument_value(argc, argv, L"--pipe");
    const auto stop_event_name = argument_value(argc, argv, L"--stop-event");
    const auto receiver_name_wide = argument_value(argc, argv, L"--name");
    const auto receiver_mode = argument_value(argc, argv, L"--mode");
    const auto parent_text = argument_value(argc, argv, L"--parent-pid");
    const auto library_override = argument_value(argc, argv, L"--library");
    const auto capability_width = argument_uint(argc, argv, L"--width", 5120);
    const auto capability_height = argument_uint(argc, argv, L"--height", 2880);
    const auto capability_fps = argument_uint(argc, argv, L"--fps", 60);
    const auto raop_port = argument_uint(argc, argv, L"--raop-port", 5001);
    const auto airplay_port = argument_uint(argc, argv, L"--airplay-port", 7001);
    const auto dlna_port = argument_uint(argc, argv, L"--dlna-port", 8090);
    const auto dlna_ssdp_port = argument_uint(argc, argv, L"--dlna-ssdp-port", 1900);
    const auto directory = executable_directory();
    const auto library_path = library_override.empty()
        ? directory / L"airplay2dll.dll"
        : std::filesystem::absolute(std::filesystem::path(library_override));
    if (has_argument(argc, argv, L"--check-runtime"))
        return preflight_airplay_runtime(library_path);
    if (pipe_name.empty() || stop_event_name.empty()) return 2;

    const auto pipe = connect_pipe(pipe_name);
    if (pipe == INVALID_HANDLE_VALUE) return 3;
    IpcWriter writer(pipe);
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host startup pid={} parent_arg={} mode_arg={} "
            "capability={}x{}@{} raop_port={} airplay_port={} dlna_port={} ssdp_port={}",
            GetCurrentProcessId(), utf8(parent_text), utf8(receiver_mode),
            capability_width, capability_height, capability_fps, raop_port,
            airplay_port, dlna_port, dlna_ssdp_port));

    if (!supported_capability(capability_width, capability_height, capability_fps)) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("wireless_host capability_rejected width={} height={} fps={}",
                capability_width, capability_height, capability_fps));
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        CloseHandle(pipe);
        return 7;
    }
    const auto effective_mode = receiver_mode == L"media" ? L"media" :
        receiver_mode == L"combined" ? L"combined" : L"mirror";
    const auto effective_receiver_name = receiver_name_wide.empty()
        ? std::wstring(L"iPhoneMirror AirPlay") : receiver_name_wide;
    const auto device_id = stable_airplay_device_id();
    const auto pairing_seed = stable_airplay_pairing_seed(device_id);
    if (pairing_seed.empty()) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "wireless_host pairing_identity_failed");
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        CloseHandle(pipe);
        return 8;
    }
    const auto capability_width_text = std::to_wstring(capability_width);
    const auto capability_height_text = std::to_wstring(capability_height);
    const auto capability_fps_text = std::to_wstring(capability_fps);
    const auto environment_ready = set_airplay_environment(
            L"IPHONE_MIRROR_AIRPLAY_WIDTH", capability_width_text) &&
        set_airplay_environment(
            L"IPHONE_MIRROR_AIRPLAY_HEIGHT", capability_height_text) &&
        set_airplay_environment(L"IPHONE_MIRROR_AIRPLAY_FPS", capability_fps_text) &&
        set_airplay_environment(L"IPHONE_MIRROR_AIRPLAY_MODE", effective_mode) &&
        set_airplay_environment(
            L"IPHONE_MIRROR_AIRPLAY_NAME", effective_receiver_name) &&
        set_airplay_environment(L"IPHONE_MIRROR_AIRPLAY_DEVICE_ID", device_id) &&
        set_airplay_environment(
            L"IPHONE_MIRROR_AIRPLAY_PAIRING_SEED", pairing_seed);
    if (!environment_ready) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "wireless_host airplay_environment_sync_failed");
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        CloseHandle(pipe);
        return 10;
    }

    const auto loaded_library = load_airplay_library(library_path);
    const auto library = loaded_library.library;
    if (!library) {
        const auto error = loaded_library.error;
        const auto message = std::format(
            "wireless_host airplay_library_failed file={} custom_path={} win32={} "
            "code_integrity={}", library_path.filename().string(),
            !library_override.empty(), error, is_code_integrity_error(error));
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            message);
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        CloseHandle(pipe);
        close_airplay_library(loaded_library);
        return 4;
    }

    const auto start_server = reinterpret_cast<StartServer>(GetProcAddress(library, StartExport));
    const auto stop_server = reinterpret_cast<StopServer>(GetProcAddress(library, StopExport));
    if (!start_server || !stop_server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "wireless_host airplay_exports_missing");
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        close_airplay_library(loaded_library);
        CloseHandle(pipe);
        return 5;
    }

    AirPlayCallback callback(writer);
    auto receiver_name = utf8(effective_receiver_name);
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host server_start receiver_fp={} capability={}x{}@{} "
            "mode={} raop_port={} airplay_port={} dlna_port={} dlna_ssdp_port={}",
            anonymous_label(receiver_name), capability_width,
            capability_height, capability_fps,
            utf8(effective_mode), raop_port, airplay_port, dlna_port,
            dlna_ssdp_port).c_str());
    const auto server = start_server(receiver_name.c_str(), raop_port, airplay_port, &callback);
    if (!server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "wireless_host airplay_server_start_failed");
        writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
        writer.shutdown();
        close_airplay_library(loaded_library);
        CloseHandle(pipe);
        return 6;
    }
    wchar_t public_key[65]{};
    const auto public_key_length = GetEnvironmentVariableW(
        L"IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", public_key,
        static_cast<DWORD>(std::size(public_key)));
    if (public_key_length != 64 && library_override.empty()) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("wireless_host pairing_public_key_invalid length={}",
                public_key_length));
        stop_server(server);
        writer.shutdown();
        close_airplay_library(loaded_library);
        CloseHandle(pipe);
        return 9;
    }
    if (public_key_length == 64) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("wireless_host pairing_public_key_valid=true bytes={}",
                public_key_length));
    }

    iPhoneMirror::wireless::DlnaRenderer dlna;
    const auto dlna_enabled = std::wstring_view(effective_mode) == L"media" ||
        std::wstring_view(effective_mode) == L"combined";
    if (dlna_enabled) {
        const auto dlna_started = dlna.start(utf8(dlna_receiver_name(effective_receiver_name)),
            stable_dlna_uuid(pairing_seed), static_cast<std::uint16_t>(dlna_port),
            static_cast<std::uint16_t>(dlna_ssdp_port), {
                .play = [&callback](std::string_view url, double duration,
                            double position, double volume, bool muted) {
                    callback.playDlnaMedia(url, duration, volume, position, muted);
                },
                .stop = [&callback] { callback.videoPlay(nullptr, 0, 0); },
                .pause = [&callback] { callback.mediaPause(); },
                .resume = [&callback] { callback.mediaResume(); },
                .seek = [&callback](double position) { callback.mediaSeek(position); },
                .set_volume = [&callback](double volume) {
                    callback.mediaVolume(volume);
                },
                .set_mute = [&callback](bool muted, double volume) {
                    callback.mediaVolume(volume, muted);
                },
                .get_play_info = [&callback](double* duration, double* position,
                                     double* rate) {
                    callback.videoGetPlayInfo(duration, position, rate);
                },
                .log = [&writer](std::string_view message) {
                    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                        message);
                },
            });
        if (!dlna_started) {
            writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                "wireless_host dlna_start_failed; udp_ports_may_be_in_use");
        }
    }
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host receiver_ready mode={} dlna_enabled={}",
            utf8(effective_mode), dlna_enabled));
    const auto ready_sent = writer.send(iPhoneMirror::wireless::MessageHeader{
        .type = iPhoneMirror::wireless::MessageType::Ready});
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host ready_ipc sent={}", ready_sent));

    const auto stop_event = OpenEventW(SYNCHRONIZE, FALSE, stop_event_name.c_str());
    const auto stop_event_error = stop_event ? ERROR_SUCCESS : GetLastError();
    HANDLE parent{};
    DWORD parent_error = ERROR_SUCCESS;
    if (!parent_text.empty()) {
        try {
            parent = OpenProcess(SYNCHRONIZE, FALSE,
                static_cast<DWORD>(std::stoul(parent_text)));
            if (!parent) parent_error = GetLastError();
        } catch (...) {
            parent_error = ERROR_INVALID_PARAMETER;
        }
    }
    HANDLE waits[2]{};
    DWORD wait_count{};
    if (stop_event) waits[wait_count++] = stop_event;
    if (parent) waits[wait_count++] = parent;
    std::string wait_reason = wait_count == 0 ? "no_wait_handles" : "unknown";
    DWORD wait_error = ERROR_SUCCESS;
    const auto wait_started = std::chrono::steady_clock::now();
    if (stop_event_error != ERROR_SUCCESS || parent_error != ERROR_SUCCESS) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("wireless_host wait_handles stop_event={} stop_error={} "
                "parent={} parent_error={}", stop_event != nullptr, stop_event_error,
                parent != nullptr, parent_error));
    }
    while (wait_count != 0) {
        const auto result = WaitForMultipleObjects(wait_count, waits, FALSE, 50);
        if (result == WAIT_TIMEOUT) {
            DWORD receive_error{};
            if (!receive_playback_updates(pipe, callback, dlna, &receive_error)) {
                wait_reason = "ipc_disconnected_or_protocol_error";
                wait_error = receive_error;
                break;
            }
            continue;
        }
        if (result >= WAIT_OBJECT_0 && result < WAIT_OBJECT_0 + wait_count) {
            const auto index = result - WAIT_OBJECT_0;
            wait_reason = (waits[index] == stop_event) ? "stop_event" : "parent_exit";
        } else {
            wait_reason = "wait_failed";
            wait_error = GetLastError();
        }
        break;
    }
    const auto wait_duration = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - wait_started).count();
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host wait_end reason={} error={} duration_ms={}",
            wait_reason, wait_error, wait_duration));

    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        "wireless_host shutdown_begin");
    const auto dlna_stop_started = std::chrono::steady_clock::now();
    dlna.stop();
    const auto dlna_stop_duration = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - dlna_stop_started).count();
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host dlna_stopped duration_ms={}", dlna_stop_duration));
    const auto server_stop_started = std::chrono::steady_clock::now();
    stop_server(server);
    const auto server_stop_duration = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - server_stop_started).count();
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless_host airplay_stopped duration_ms={}", server_stop_duration));
    writer.send_text(iPhoneMirror::wireless::MessageType::Log, callback.summary());
    writer.send_text(iPhoneMirror::wireless::MessageType::Log, writer.summary());
    writer.shutdown();
    if (parent) CloseHandle(parent);
    if (stop_event) CloseHandle(stop_event);
    close_airplay_library(loaded_library);
    CloseHandle(pipe);
    return 0;
}
