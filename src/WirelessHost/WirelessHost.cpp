// SPDX-License-Identifier: GPL-3.0-only

#include "IpcProtocol.h"
#include "DlnaRenderer.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <filesystem>
#include <format>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

constexpr char StartExport[] = "?fgServerStart@@YAPEAXQEBDIIPEAVIAirServerCallback@@@Z";
constexpr char StopExport[] = "?fgServerStop@@YAXPEAX@Z";

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

bool write_all(HANDLE pipe, const void* source, std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size, 1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr) || written == 0) return false;
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
        if (payload.size() > iPhoneMirror::wireless::MaxPayloadBytes) return false;
        try {
            header.payload_size = static_cast<std::uint32_t>(payload.size());
            const auto message_bytes = sizeof(header) + payload.size();
            if (message_bytes > MaxQueuedBytes) return false;

            std::unique_lock lock(mutex_);
            if (closing_) return false;

            if (header.type == iPhoneMirror::wireless::MessageType::Video) {
                for (auto position = queue_.begin(); position != queue_.end();) {
                    if (position->header.type == iPhoneMirror::wireless::MessageType::Video &&
                        std::strncmp(position->header.device_id, header.device_id,
                            iPhoneMirror::wireless::DeviceIdBytes) == 0) {
                        buffered_bytes_ -= position->size();
                        --buffered_messages_;
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
                if (expendable == queue_.end()) return false;
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
            buffered_bytes_ += message_bytes;
            ++buffered_messages_;
            queue_.push_back(std::move(message));
            lock.unlock();
            condition_.notify_one();
            return true;
        } catch (...) {
            return false;
        }
    }

    bool send_text(iPhoneMirror::wireless::MessageType type, const char* text) noexcept {
        const std::string_view value = text ? std::string_view(text) : std::string_view{};
        iPhoneMirror::wireless::MessageHeader header;
        header.type = type;
        return send(header, std::span(
            reinterpret_cast<const std::uint8_t*>(value.data()), value.size()));
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
        const auto length = std::min<std::size_t>(std::strlen(source), Size - 1);
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
            }

            const auto written = write_all(pipe_, &message.header, sizeof(message.header)) &&
                (message.payload.empty() ||
                    write_all(pipe_, message.payload.data(), message.payload.size()));
            std::scoped_lock lock(mutex_);
            buffered_bytes_ -= message.size();
            --buffered_messages_;
            if (!written) {
                closing_ = true;
                queue_.clear();
                buffered_bytes_ = 0;
                buffered_messages_ = 0;
                return;
            }
        }
    }

    HANDLE pipe_{};
    std::mutex mutex_;
    std::condition_variable condition_;
    std::deque<QueuedMessage> queue_;
    std::size_t buffered_bytes_{};
    std::size_t buffered_messages_{};
    std::uint64_t sequence_{};
    bool closing_{};
    std::thread worker_;
};

class AirPlayCallback final : public IAirServerCallback {
public:
    explicit AirPlayCallback(IpcWriter& writer) : writer_(writer) {}

    void connected(const char* remote_name, const char* remote_device_id) override {
        diagnostic(std::format("callback connected remote={} device={}",
            safe_text(remote_name), safe_text(remote_device_id)));
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Connected;
        writer_.send_device(header, remote_name, remote_device_id);
    }

    void disconnected(const char* remote_name, const char* remote_device_id) override {
        diagnostic(std::format("callback disconnected remote={} device={}",
            safe_text(remote_name), safe_text(remote_device_id)));
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Disconnected;
        writer_.send_device(header, remote_name, remote_device_id);
    }

    void outputAudio(SFgAudioFrame* data, const char* remote_name,
        const char* remote_device_id) override {
        const auto callback_index = audio_callbacks_.fetch_add(1,
            std::memory_order_relaxed) + 1;
        if (!data) {
            diagnostic("audio callback rejected: null frame");
            return;
        }
        if (!data->data || data->dataLen == 0 || data->dataLen >
            iPhoneMirror::wireless::MaxPayloadBytes) {
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
            diagnostic("video callback rejected: null frame");
            return;
        }
        if (!data->data || data->width == 0 || data->height == 0 ||
            data->width > 8192 || data->height > 8192 || data->dataTotalLen == 0 ||
            data->dataTotalLen > iPhoneMirror::wireless::MaxPayloadBytes) {
            diagnostic(std::format(
                "video callback rejected index={} size={}x{} bytes={} data={}",
                callback_index, data->width, data->height, data->dataTotalLen,
                data->data != nullptr));
            return;
        }
        const auto plane_total = static_cast<std::uint64_t>(data->dataLen[0]) +
            data->dataLen[1] + data->dataLen[2];
        if (plane_total > data->dataTotalLen || data->pitch[0] < data->width) {
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
        std::string_view location = url ? std::string_view(url) : std::string_view{};
        if (!location.empty() && location.size() <= 16U * 1024U &&
            (location.starts_with("http://") || location.starts_with("https://"))) {
            header.type = iPhoneMirror::wireless::MessageType::MediaPlay;
            header.media_position = std::max(0.0, start_position);
            header.media_volume = volume;
            {
                std::scoped_lock lock(playback_mutex_);
                header.media_command_id = ++media_command_id_;
                playback_ = {
                    .command_id = header.media_command_id,
                    .position = header.media_position,
                    .rate = 1.0,
                    .updated_at = std::chrono::steady_clock::now(),
                };
            }
            writer_.send(header, std::span(
                reinterpret_cast<const std::uint8_t*>(location.data()), location.size()));
            diagnostic(std::format("media play command={} start={:.3f} url_bytes={}",
                header.media_command_id, header.media_position, location.size()));
            return;
        }

        header.type = iPhoneMirror::wireless::MessageType::MediaStop;
        {
            std::scoped_lock lock(playback_mutex_);
            header.media_command_id = ++media_command_id_;
            playback_ = {.command_id = header.media_command_id};
        }
        writer_.send(header);
        diagnostic(std::format("media stop command={}", header.media_command_id));
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
        if (header.media_command_id != playback_.command_id) return;
        playback_.duration = std::max(0.0, header.media_duration);
        playback_.position = std::max(0.0, header.media_position);
        playback_.rate = header.media_rate;
        playback_.updated_at = std::chrono::steady_clock::now();
    }

    void setVolume(float, const char*, const char*) override {}

    void log(int level, const char* message) override {
        const auto text = safe_text(message);
        if (const auto metadata = parse_device_metadata(text)) {
            writer_.send_device_info(*metadata);
            diagnostic(std::format("device metadata device={} model={} os={}",
                metadata->device_id, metadata->product_type, metadata->os_version));
            return;
        }
        const auto log_index = log_callbacks_.fetch_add(1, std::memory_order_relaxed) + 1;
        // AirPlayServer uses syslog levels (INFO=6, DEBUG=7). Keep the opening
        // handshake in full, then retain errors only so a long stream cannot
        // flood the media pipe or the application log.
        if (log_index <= 512 || level <= 4) {
            diagnostic(std::format("airplay level={} {}", level, text));
        }
    }

private:
    static std::string_view safe_text(const char* value) noexcept {
        return value ? std::string_view(value) : std::string_view("<null>");
    }

    void diagnostic(std::string_view message) noexcept {
        writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::string(message).c_str());
    }

    IpcWriter& writer_;
    struct PlaybackState {
        std::uint64_t command_id{};
        double duration{};
        double position{};
        double rate{};
        std::chrono::steady_clock::time_point updated_at{std::chrono::steady_clock::now()};
    };
    std::mutex playback_mutex_;
    std::uint64_t media_command_id_{GetTickCount64() << 16};
    PlaybackState playback_;
    std::atomic_uint64_t audio_callbacks_{};
    std::atomic_uint64_t video_callbacks_{};
    std::atomic_uint64_t log_callbacks_{};
};

std::wstring argument_value(int argc, wchar_t** argv, std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index)
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    return {};
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
    return (width == 5120 && height == 2880 && fps == 60) ||
        (width == 1920 && height == 1080 && fps == 60) ||
        (width == 1280 && height == 720 && fps == 30) ||
        (width == 960 && height == 540 && fps == 30);
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
    constexpr std::string_view profile = "video-cast-v2";
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

bool receive_playback_updates(HANDLE pipe, AirPlayCallback& callback) noexcept {
    DWORD available{};
    while (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) {
        if (available < sizeof(iPhoneMirror::wireless::MessageHeader)) return true;
        iPhoneMirror::wireless::MessageHeader header;
        if (!read_all(pipe, &header, sizeof(header))) return false;
        if (header.magic != iPhoneMirror::wireless::IpcMagic ||
            header.version != iPhoneMirror::wireless::IpcVersion ||
            header.payload_size != 0) return false;
        callback.updatePlayback(header);
    }
    return false;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
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
    if (pipe_name.empty() || stop_event_name.empty()) return 2;

    const auto pipe = connect_pipe(pipe_name);
    if (pipe == INVALID_HANDLE_VALUE) return 3;
    IpcWriter writer(pipe);

    if (!supported_capability(capability_width, capability_height, capability_fps)) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "Unsupported AirPlay display capability profile");
        writer.shutdown();
        CloseHandle(pipe);
        return 7;
    }
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_WIDTH",
        std::to_wstring(capability_width).c_str());
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_HEIGHT",
        std::to_wstring(capability_height).c_str());
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_FPS",
        std::to_wstring(capability_fps).c_str());
    const auto effective_mode = receiver_mode == L"media" ? L"media" :
        receiver_mode == L"combined" ? L"combined" : L"mirror";
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_MODE", effective_mode);
    const auto effective_receiver_name = receiver_name_wide.empty()
        ? std::wstring(L"iPhoneMirror AirPlay") : receiver_name_wide;
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_NAME",
        effective_receiver_name.c_str());
    const auto device_id = stable_airplay_device_id();
    SetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_DEVICE_ID", device_id.c_str());
    const auto pairing_seed = stable_airplay_pairing_seed(device_id);
    if (pairing_seed.empty()) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "Could not create the stable AirPlay pairing identity");
        writer.shutdown();
        CloseHandle(pipe);
        return 8;
    }
    SetEnvironmentVariableW(
        L"IPHONE_MIRROR_AIRPLAY_PAIRING_SEED", pairing_seed.c_str());

    const auto directory = executable_directory();
    const auto library_path = library_override.empty()
        ? directory / L"airplay2dll.dll"
        : std::filesystem::absolute(std::filesystem::path(library_override));
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
    const auto search_cookie = AddDllDirectory(library_path.parent_path().c_str());
    const auto library = LoadLibraryExW(library_path.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 |
        LOAD_LIBRARY_SEARCH_USER_DIRS);
    if (!library) {
        const auto message = std::format("Could not load airplay2dll.dll: Win32 {}",
            GetLastError());
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            message.c_str());
        writer.shutdown();
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 4;
    }

    const auto start_server = reinterpret_cast<StartServer>(GetProcAddress(library, StartExport));
    const auto stop_server = reinterpret_cast<StopServer>(GetProcAddress(library, StopExport));
    if (!start_server || !stop_server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "AirPlay receiver exports do not match the pinned version");
        writer.shutdown();
        FreeLibrary(library);
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 5;
    }

    AirPlayCallback callback(writer);
    auto receiver_name = utf8(effective_receiver_name);
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless host starting receiver={} capability={}x{}@{} "
            "mode={} raop_port={} airplay_port={} dlna_port={} dlna_ssdp_port={}",
            receiver_name, capability_width,
            capability_height, capability_fps,
            utf8(effective_mode), raop_port, airplay_port, dlna_port,
            dlna_ssdp_port).c_str());
    const auto server = start_server(receiver_name.c_str(), raop_port, airplay_port, &callback);
    if (!server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "AirPlay receiver initialization failed");
        writer.shutdown();
        FreeLibrary(library);
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 6;
    }
    wchar_t public_key[65]{};
    const auto public_key_length = GetEnvironmentVariableW(
        L"IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", public_key,
        static_cast<DWORD>(std::size(public_key)));
    if (public_key_length != 64 && library_override.empty()) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "AirPlay receiver did not publish a valid pairing public key");
        stop_server(server);
        writer.shutdown();
        FreeLibrary(library);
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 9;
    }
    if (public_key_length == 64) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("wireless host pairing_public_key={}",
                utf8(std::wstring_view(public_key, public_key_length))).c_str());
    }

    iPhoneMirror::wireless::DlnaRenderer dlna;
    const auto dlna_enabled = std::wstring_view(effective_mode) == L"media" ||
        std::wstring_view(effective_mode) == L"combined";
    if (dlna_enabled) {
        const auto dlna_started = dlna.start(utf8(dlna_receiver_name(effective_receiver_name)),
            stable_dlna_uuid(pairing_seed), static_cast<std::uint16_t>(dlna_port),
            static_cast<std::uint16_t>(dlna_ssdp_port), {
                .play = [&callback](std::string_view url, double position) {
                    std::string mutable_url(url);
                    callback.videoPlay(mutable_url.data(), 1.0, position);
                },
                .stop = [&callback] { callback.videoPlay(nullptr, 0, 0); },
                .get_play_info = [&callback](double* duration, double* position,
                                     double* rate) {
                    callback.videoGetPlayInfo(duration, position, rate);
                },
                .log = [&writer](std::string_view message) {
                    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                        std::string(message).c_str());
                },
            });
        if (!dlna_started) {
            writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                "DLNA video receiver could not start; close other casting receivers using UDP 1900");
        }
    }
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        "wireless host receiver initialized");
    writer.send(iPhoneMirror::wireless::MessageHeader{
        .type = iPhoneMirror::wireless::MessageType::Ready});

    const auto stop_event = OpenEventW(SYNCHRONIZE, FALSE, stop_event_name.c_str());
    HANDLE parent{};
    if (!parent_text.empty()) {
        try {
            parent = OpenProcess(SYNCHRONIZE, FALSE,
                static_cast<DWORD>(std::stoul(parent_text)));
        } catch (...) {}
    }
    HANDLE waits[2] = {stop_event, parent};
    const DWORD wait_count = stop_event && parent ? 2U : 1U;
    if (stop_event) {
        while (WaitForMultipleObjects(wait_count, waits, FALSE, 50) == WAIT_TIMEOUT)
            if (!receive_playback_updates(pipe, callback)) break;
    }

    writer.send_text(iPhoneMirror::wireless::MessageType::Log, "wireless host stopping dlna");
    dlna.stop();
    writer.send_text(iPhoneMirror::wireless::MessageType::Log, "wireless host dlna stopped");
    stop_server(server);
    writer.shutdown();
    if (parent) CloseHandle(parent);
    if (stop_event) CloseHandle(stop_event);
    FreeLibrary(library);
    CloseHandle(pipe);
    if (search_cookie) RemoveDllDirectory(search_cookie);
    return 0;
}
