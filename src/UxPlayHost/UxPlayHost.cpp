// SPDX-License-Identifier: GPL-3.0-only

// The FDH2/UxPlay process is GPL-licensed and uses GStreamer for decoding.
// Keep it behind this host so the native core can continue to consume the
// existing, versioned named-pipe protocol used by WirelessHost.

#include "IpcProtocol.h"

#include <Windows.h>
#include <sddl.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <charconv>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <filesystem>
#include <format>
#include <functional>
#include <limits>
#include <mutex>
#include <optional>
#include <ranges>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {

constexpr std::wstring_view DefaultReceiverName = L"iPhoneMirror AirPlay";
constexpr std::size_t MaxQueuedMessages = 96;
constexpr std::size_t MaxQueuedBytes =
    iPhoneMirror::wireless::MaxPayloadBytes + 8U * 1024U * 1024U;
constexpr std::size_t RawReadBytes = 256U * 1024U;
// 10 ms of signed PCM16 stereo at 44.1 kHz.  Short packets smooth the jitter
// caused by a concurrently forwarded multi-megabyte video frame.
constexpr std::size_t AudioMessageBytes = 1764;

class LocalSecurityAttributes final {
public:
    LocalSecurityAttributes() {
        constexpr auto descriptor = L"D:P(A;;GA;;;SY)(A;;GA;;;OW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor,
                SDDL_REVISION_1, &descriptor_, nullptr)) {
            throw std::runtime_error(std::format(
                "Could not create UxPlay IPC security descriptor: {}", GetLastError()));
        }
        attributes_ = {
            .nLength = sizeof(attributes_),
            .lpSecurityDescriptor = descriptor_,
            .bInheritHandle = FALSE,
        };
    }

    ~LocalSecurityAttributes() { if (descriptor_) LocalFree(descriptor_); }
    LocalSecurityAttributes(const LocalSecurityAttributes&) = delete;
    LocalSecurityAttributes& operator=(const LocalSecurityAttributes&) = delete;

    [[nodiscard]] SECURITY_ATTRIBUTES* get() noexcept { return &attributes_; }

private:
    PSECURITY_DESCRIPTOR descriptor_{};
    SECURITY_ATTRIBUTES attributes_{};
};

[[nodiscard]] std::wstring argument_value(int argc, wchar_t** argv,
    std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index) {
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    }
    return {};
}

[[nodiscard]] bool has_argument(int argc, wchar_t** argv,
    std::wstring_view name) noexcept {
    for (int index = 1; index < argc; ++index) {
        if (std::wstring_view(argv[index]) == name) return true;
    }
    return false;
}

[[nodiscard]] unsigned int argument_uint(int argc, wchar_t** argv,
    std::wstring_view name, unsigned int fallback) noexcept {
    const auto value = argument_value(argc, argv, name);
    if (value.empty()) return fallback;
    try {
        std::size_t consumed{};
        const auto parsed = std::stoul(value, &consumed);
        return consumed == value.size() && parsed <= 65535
            ? static_cast<unsigned int>(parsed) : fallback;
    } catch (...) {
        return fallback;
    }
}

[[nodiscard]] bool supported_capability(unsigned int width, unsigned int height,
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

[[nodiscard]] std::wstring quote_argument(std::wstring_view value) {
    std::wstring quoted{L"\""};
    std::size_t slashes{};
    for (const auto character : value) {
        if (character == L'\\') {
            ++slashes;
            continue;
        }
        if (character == L'\"') {
            quoted.append(slashes * 2 + 1, L'\\');
            quoted.push_back(L'\"');
            slashes = 0;
            continue;
        }
        quoted.append(slashes, L'\\');
        slashes = 0;
        quoted.push_back(character);
    }
    quoted.append(slashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

[[nodiscard]] std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), result.data(), length, nullptr,
            nullptr) != length) {
        return {};
    }
    return result;
}

[[nodiscard]] std::filesystem::path executable_directory() {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(),
        static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size())
        throw std::runtime_error("Could not determine the UxPlay host directory");
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

[[nodiscard]] std::optional<std::filesystem::path> find_uxplay_executable(
    std::wstring_view override_path = {}) {
    try {
        const auto consider = [](std::wstring_view candidate)
            -> std::optional<std::filesystem::path> {
            if (candidate.empty()) return std::nullopt;
            const auto path = std::filesystem::absolute(std::filesystem::path(candidate));
            if (!std::filesystem::is_regular_file(path)) return std::nullopt;
            return path;
        };
        if (const auto override_result = consider(override_path)) return override_result;

        std::array<wchar_t, 32768> environment{};
        const auto length = GetEnvironmentVariableW(
            L"IPHONE_MIRROR_UXPLAY_EXECUTABLE", environment.data(),
            static_cast<DWORD>(environment.size()));
        if (length > 0 && length < environment.size()) {
            if (const auto environment_result = consider(std::wstring_view(
                    environment.data(), length))) {
                return environment_result;
            }
        }

        const auto directory = executable_directory();
        for (const auto& candidate : {
                 directory / L"UxPlay" / L"uxplay.exe",
                 directory / L"uxplay.exe"}) {
            if (std::filesystem::is_regular_file(candidate))
                return std::filesystem::absolute(candidate);
        }

        // A portable build may keep UxPlay outside the application directory.
        // SearchPathW is used only after the explicit override and bundled
        // locations, so the user still controls the fallback binary.
        std::array<wchar_t, 32768> resolved{};
        const auto found = SearchPathW(nullptr, L"uxplay.exe", nullptr,
            static_cast<DWORD>(resolved.size()), resolved.data(), nullptr);
        if (found > 0 && found < resolved.size()) {
            if (const auto path = consider(std::wstring_view(resolved.data(), found)))
                return path;
        }
    } catch (...) {
        return std::nullopt;
    }
    return std::nullopt;
}

[[nodiscard]] bool is_code_integrity_error(DWORD error) noexcept {
    return error == ERROR_INVALID_IMAGE_HASH ||
        error == ERROR_ACCESS_DISABLED_BY_POLICY ||
        (error >= ERROR_SYSTEM_INTEGRITY_ROLLBACK_DETECTED &&
            error <= ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE) ||
        (error >= ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE &&
            error <= ERROR_SYSTEM_INTEGRITY_WHQL_NOT_SATISFIED);
}

[[nodiscard]] DWORD probe_image(const std::filesystem::path& path) noexcept {
    const auto file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    const auto image = CreateFileMappingW(file, nullptr, PAGE_READONLY | SEC_IMAGE,
        0, 0, nullptr);
    const auto error = image ? ERROR_SUCCESS : GetLastError();
    if (image) CloseHandle(image);
    CloseHandle(file);
    return error;
}

[[nodiscard]] DWORD probe_library(const std::filesystem::path& path) noexcept {
    const auto module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module) return GetLastError();
    FreeLibrary(module);
    return ERROR_SUCCESS;
}

void prepare_uxplay_environment(const std::filesystem::path& executable) noexcept;

struct UxPlayRuntimeArtifact {
    std::wstring_view relative_path;
    bool image{};
};

[[nodiscard]] constexpr auto uxplay_runtime_artifacts() noexcept {
    return std::array<UxPlayRuntimeArtifact, 23>{
        UxPlayRuntimeArtifact{L"uxplay.exe", true},
        UxPlayRuntimeArtifact{L"LICENSE", false},
        UxPlayRuntimeArtifact{L"SOURCE.md", false},
        UxPlayRuntimeArtifact{L"bin\\libgstreamer-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgstbase-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgstvideo-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgstaudio-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgstapp-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgstpbutils-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgsttag-1.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libglib-2.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgobject-2.0-0.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libplist-2.0.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstapp.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstcoreelements.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstaudioconvert.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstaudioresample.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstvideoconvertscale.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgsty4m.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstvideoparsersbad.dll", true},
        UxPlayRuntimeArtifact{L"lib\\gstreamer-1.0\\libgstlibav.dll", true},
        UxPlayRuntimeArtifact{L"bin\\libgcc_s_seh-1.dll", true},
        UxPlayRuntimeArtifact{L"bin\\dnssd.dll", true},
    };
}

[[nodiscard]] int preflight_uxplay_runtime(std::wstring_view override_path) noexcept {
    const auto uxplay = find_uxplay_executable(override_path);
    if (!uxplay) return 41;
    const auto runtime_root = uxplay->parent_path();
    prepare_uxplay_environment(*uxplay);
    for (const auto& artifact : uxplay_runtime_artifacts()) {
        const auto path = runtime_root / artifact.relative_path;
        if (!std::filesystem::is_regular_file(path)) return 41;
        if (!artifact.image) continue;
        const auto image_error = probe_image(path);
        if (image_error != ERROR_SUCCESS)
            return is_code_integrity_error(image_error) ? 40 : 41;
        if (path.extension() == L".dll") {
            const auto load_error = probe_library(path);
            if (load_error != ERROR_SUCCESS)
                return is_code_integrity_error(load_error) ? 40 : 41;
        }
    }

    auto command = quote_argument(uxplay->wstring()) + L" -v";
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    const auto directory = uxplay->parent_path().wstring();
    if (!CreateProcessW(uxplay->c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup, &process)) {
        const auto error = GetLastError();
        return is_code_integrity_error(error) ? 40 : 41;
    }
    CloseHandle(process.hThread);
    const auto waited = WaitForSingleObject(process.hProcess, 5000);
    if (waited == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 0);
        WaitForSingleObject(process.hProcess, 1000);
        CloseHandle(process.hProcess);
        return 0;
    }
    DWORD exit_code{};
    const auto exited = waited == WAIT_OBJECT_0 &&
        GetExitCodeProcess(process.hProcess, &exit_code);
    CloseHandle(process.hProcess);
    if (!exited) return 41;
    return exit_code == 0 ? 0 : 42;
}

[[nodiscard]] HANDLE connect_pipe(const std::wstring& pipe_name) noexcept {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto pipe = CreateFileW(pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        const auto error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)
            return INVALID_HANDLE_VALUE;
        WaitNamedPipeW(pipe_name.c_str(), 100);
    }
    return INVALID_HANDLE_VALUE;
}

[[nodiscard]] bool write_all(HANDLE pipe, const void* source, std::size_t size,
    DWORD* failure_reason = nullptr) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size,
            1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr) || written == 0) {
            if (failure_reason) *failure_reason = written == 0 ? ERROR_BROKEN_PIPE : GetLastError();
            return false;
        }
        bytes += written;
        size -= written;
    }
    return true;
}

class IpcWriter final {
public:
    explicit IpcWriter(HANDLE pipe) : pipe_(pipe), worker_([this] { run(); }) {}
    ~IpcWriter() { shutdown(); }
    IpcWriter(const IpcWriter&) = delete;
    IpcWriter& operator=(const IpcWriter&) = delete;

    [[nodiscard]] bool send(iPhoneMirror::wireless::MessageHeader header,
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
                    if (position->header.type == iPhoneMirror::wireless::MessageType::Video) {
                        queued_bytes_ -= position->size();
                        position = queue_.erase(position);
                    } else {
                        ++position;
                    }
                }
            }
            while (queue_.size() >= MaxQueuedMessages ||
                queued_bytes_ + message_bytes > MaxQueuedBytes) {
                // Video frames are large and replaceable; audio is a real-time
                // stream and must remain queued ahead of video whenever the
                // bridge is under back-pressure.
                auto expendable = std::ranges::find_if(queue_, [](const auto& queued) {
                    return queued.header.type == iPhoneMirror::wireless::MessageType::Video;
                });
                if (expendable == queue_.end()) {
                    expendable = std::ranges::find_if(queue_, [](const auto& queued) {
                        return queued.header.type == iPhoneMirror::wireless::MessageType::Log;
                    });
                }
                if (expendable == queue_.end()) {
                    expendable = std::ranges::find_if(queue_, [](const auto& queued) {
                        return queued.header.type == iPhoneMirror::wireless::MessageType::Audio;
                    });
                }
                if (expendable == queue_.end()) return false;
                queued_bytes_ -= expendable->size();
                queue_.erase(expendable);
            }
            QueuedMessage message{.header = header};
            message.payload.assign(payload.begin(), payload.end());
            message.header.sequence = ++sequence_;
            queued_bytes_ += message.size();
            if (header.type == iPhoneMirror::wireless::MessageType::Audio) {
                const auto video = std::ranges::find_if(queue_, [](const auto& queued) {
                    return queued.header.type == iPhoneMirror::wireless::MessageType::Video;
                });
                queue_.insert(video, std::move(message));
            } else {
                queue_.push_back(std::move(message));
            }
            lock.unlock();
            condition_.notify_one();
            return true;
        } catch (...) {
            return false;
        }
    }

    [[nodiscard]] bool send_text(iPhoneMirror::wireless::MessageType type,
        std::string_view text) noexcept {
        if (text.size() > 4096) text = text.substr(0, 4096);
        iPhoneMirror::wireless::MessageHeader header;
        header.type = type;
        return send(header, std::span(reinterpret_cast<const std::uint8_t*>(text.data()),
            text.size()));
    }

    void shutdown() noexcept {
        std::thread worker;
        {
            std::scoped_lock lock(mutex_);
            if (shutdown_started_) return;
            shutdown_started_ = true;
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

    void run() noexcept {
        while (true) {
            QueuedMessage message;
            {
                std::unique_lock lock(mutex_);
                condition_.wait(lock, [this] { return closing_ || !queue_.empty(); });
                if (queue_.empty()) return;
                message = std::move(queue_.front());
                queue_.pop_front();
                queued_bytes_ -= message.size();
            }
            DWORD error{};
            if (!write_all(pipe_, &message.header, sizeof(message.header), &error) ||
                (!message.payload.empty() && !write_all(pipe_, message.payload.data(),
                    message.payload.size(), &error))) {
                std::scoped_lock lock(mutex_);
                closing_ = true;
                queue_.clear();
                queued_bytes_ = 0;
                return;
            }
        }
    }

    HANDLE pipe_{};
    std::mutex mutex_;
    std::condition_variable condition_;
    std::deque<QueuedMessage> queue_;
    std::size_t queued_bytes_{};
    std::uint64_t sequence_{};
    bool closing_{};
    bool shutdown_started_{};
    std::thread worker_;
};

template <std::size_t Size>
void copy_text(char (&destination)[Size], std::string_view value) noexcept {
    const auto end = value.find('\0');
    if (end != std::string_view::npos) value = value.substr(0, end);
    const auto length = std::min(value.size(), Size - 1);
    std::memcpy(destination, value.data(), length);
    destination[length] = '\0';
}

struct ClientIdentity {
    std::string device_id{"uxplay-client"};
    std::string name{"UxPlay"};
    std::string product_type;
};

class StreamForwarder final {
public:
    explicit StreamForwarder(IpcWriter& writer) : writer_(writer) {
        video_buffer_.reserve(RawReadBytes * 2U);
        audio_buffer_.reserve(AudioMessageBytes * 3U);
    }

    void observe_uxplay_line(std::string_view line) {
        if (const auto parsed = parse_client_identity(line)) {
            std::scoped_lock send_lock(send_mutex_);
            std::scoped_lock identity_lock(identity_mutex_);
            const auto previous = identity_;
            const auto id_changed = previous.device_id != parsed->device_id;
            const auto metadata_changed = id_changed ||
                previous.name != parsed->name ||
                previous.product_type != parsed->product_type;
            identity_ = std::move(*parsed);
            if (connected_ && id_changed) {
                // Raw media can arrive before UxPlay prints its connection log.
                // Replace the provisional identity as an ordered lifecycle event
                // so the hub never keeps routing video under the old device id.
                send_disconnected(previous);
                send_connected(identity_);
            } else if (connected_ && metadata_changed) {
                send_device_info(identity_);
            }
        }
        if (line.find("Connection closed on socket") != std::string_view::npos ||
            line.find("lost connection with client") != std::string_view::npos) {
            disconnect();
        }
    }

    void push_video(std::span<const std::uint8_t> bytes) {
        if (bytes.empty()) return;
        try {
            // Video and audio arrive through separate named-pipe readers. Do
            // not hold the shared lifecycle lock while copying a multi-MiB
            // video frame, otherwise it starves real-time PCM delivery.
            video_buffer_.insert(video_buffer_.end(), bytes.begin(), bytes.end());
            if (video_buffer_.size() > iPhoneMirror::wireless::MaxPayloadBytes +
                    MaxY4mHeaderBytes * 2U) {
                (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
                    "uxplay_host video_input_overflow; resynchronizing Y4M frame buffer");
                video_buffer_.clear();
                reset_video_format();
                return;
            }
            forward_y4m_frames();
        } catch (...) {
            (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
                "uxplay_host video_forward_failed");
            video_buffer_.clear();
            reset_video_format();
        }
    }

    void push_audio(std::span<const std::uint8_t> bytes) {
        if (bytes.empty()) return;
        try {
            audio_buffer_.insert(audio_buffer_.end(), bytes.begin(), bytes.end());
            const auto complete = audio_buffer_.size() / 4U * 4U;
            std::size_t consumed{};
            while (complete - consumed >= AudioMessageBytes) {
                ensure_connected();
                iPhoneMirror::wireless::MessageHeader header;
                header.type = iPhoneMirror::wireless::MessageType::Audio;
                header.sample_rate = 44100;
                header.channels = 2;
                header.bits_per_sample = 16;
                write_identity(header);
                (void)writer_.send(header, std::span(audio_buffer_.data() + consumed,
                    AudioMessageBytes));
                consumed += AudioMessageBytes;
            }
            if (consumed != 0) {
                audio_buffer_.erase(audio_buffer_.begin(), audio_buffer_.begin() +
                    static_cast<std::ptrdiff_t>(consumed));
            }
            if (audio_buffer_.size() > AudioMessageBytes * 3U) {
                const auto final_complete = audio_buffer_.size() / 4U * 4U;
                if (final_complete != 0) {
                    ensure_connected();
                    iPhoneMirror::wireless::MessageHeader header;
                    header.type = iPhoneMirror::wireless::MessageType::Audio;
                    header.sample_rate = 44100;
                    header.channels = 2;
                    header.bits_per_sample = 16;
                    write_identity(header);
                    (void)writer_.send(header,
                        std::span(audio_buffer_.data(), final_complete));
                    audio_buffer_.erase(audio_buffer_.begin(), audio_buffer_.begin() +
                        static_cast<std::ptrdiff_t>(final_complete));
                }
            }
        } catch (...) {
            (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
                "uxplay_host audio_forward_failed");
            audio_buffer_.clear();
        }
    }

    void reset_video_stream() noexcept {
        video_buffer_.clear();
        reset_video_format();
        (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
            "uxplay_host video_stream_reset");
    }

    void reset_audio_stream() noexcept {
        audio_buffer_.clear();
    }

    void disconnect() noexcept {
        std::scoped_lock send_lock(send_mutex_);
        ClientIdentity identity;
        bool should_send{};
        {
            std::scoped_lock lock(identity_mutex_);
            should_send = connected_;
            connected_ = false;
            identity = identity_;
        }
        if (!should_send) return;
        send_disconnected(identity);
    }

private:
    static constexpr std::size_t MaxY4mHeaderBytes = 4096;

    void reset_video_format() noexcept {
        video_width_ = 0;
        video_height_ = 0;
        frame_bytes_ = 0;
        awaiting_frame_payload_ = false;
    }

    [[nodiscard]] static bool parse_uint(std::string_view text, unsigned int& value) noexcept {
        unsigned int parsed{};
        const auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), parsed);
        if (error != std::errc{} || end != text.data() + text.size()) return false;
        value = parsed;
        return true;
    }

    [[nodiscard]] bool parse_y4m_header(std::string_view line) {
        if (!line.starts_with("YUV4MPEG2 ")) return false;
        unsigned int width{};
        unsigned int height{};
        for (std::size_t begin = 10; begin < line.size();) {
            const auto end = line.find(' ', begin);
            const auto token = line.substr(begin, end == std::string_view::npos
                ? std::string_view::npos : end - begin);
            if (token.size() > 1 && token.front() == 'W')
                (void)parse_uint(token.substr(1), width);
            else if (token.size() > 1 && token.front() == 'H')
                (void)parse_uint(token.substr(1), height);
            if (end == std::string_view::npos) break;
            begin = end + 1U;
        }
        if (width < 2 || height < 2 || width > 8192 || height > 8192 ||
            (width & 1U) != 0 || (height & 1U) != 0) return false;
        const auto bytes = static_cast<std::uint64_t>(width) * height * 3U / 2U;
        if (bytes == 0 || bytes > iPhoneMirror::wireless::MaxPayloadBytes) return false;
        video_width_ = width;
        video_height_ = height;
        frame_bytes_ = static_cast<std::size_t>(bytes);
        (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::format("uxplay_host native_video_format={}x{}", width, height));
        return true;
    }

    void forward_frame() {
        ensure_connected();
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Video;
        header.width = video_width_;
        header.height = video_height_;
        header.stride[0] = video_width_;
        header.stride[1] = header.stride[2] = video_width_ / 2U;
        header.plane_size[0] = video_width_ * video_height_;
        header.plane_size[1] = header.plane_size[2] =
            (video_width_ / 2U) * (video_height_ / 2U);
        write_identity(header);
        (void)writer_.send(header, std::span(video_buffer_.data(), frame_bytes_));
        video_buffer_.erase(video_buffer_.begin(),
            video_buffer_.begin() + static_cast<std::ptrdiff_t>(frame_bytes_));
    }

    void forward_y4m_frames() {
        for (;;) {
            if (awaiting_frame_payload_) {
                if (video_buffer_.size() < frame_bytes_) return;
                forward_frame();
                awaiting_frame_payload_ = false;
                continue;
            }
            const auto line_end = std::find(video_buffer_.begin(), video_buffer_.end(), '\n');
            if (line_end == video_buffer_.end()) {
                if (video_buffer_.size() > MaxY4mHeaderBytes) throw std::runtime_error("Y4M header too long");
                return;
            }
            const auto line_size = static_cast<std::size_t>(line_end - video_buffer_.begin());
            const std::string_view line(reinterpret_cast<const char*>(video_buffer_.data()), line_size);
            if (frame_bytes_ == 0 || line.starts_with("YUV4MPEG2 ")) {
                if (!parse_y4m_header(line)) throw std::runtime_error("invalid Y4M stream header");
                video_buffer_.erase(video_buffer_.begin(), line_end + 1);
                continue;
            }
            if (!line.starts_with("FRAME")) throw std::runtime_error("invalid Y4M frame header");
            video_buffer_.erase(video_buffer_.begin(), line_end + 1);
            awaiting_frame_payload_ = true;
        }
    }

    [[nodiscard]] static std::optional<ClientIdentity> parse_client_identity(
        std::string_view line) {
        constexpr std::string_view prefix = "connection request from ";
        constexpr std::string_view device_marker = ") with deviceID = ";
        if (!line.starts_with(prefix)) return std::nullopt;
        line.remove_prefix(prefix.size());
        const auto open = line.find(" (");
        if (open == std::string_view::npos) return std::nullopt;
        const auto marker = line.find(device_marker, open + 2U);
        if (marker == std::string_view::npos) return std::nullopt;
        auto name = line.substr(0, open);
        auto model = line.substr(open + 2U, marker - (open + 2U));
        auto id = line.substr(marker + device_marker.size());
        const auto trim = [](std::string_view value) {
            while (!value.empty() && (value.back() == '\r' || value.back() == '\n' ||
                value.back() == ' ' || value.back() == '\t')) value.remove_suffix(1);
            while (!value.empty() && (value.front() == ' ' || value.front() == '\t'))
                value.remove_prefix(1);
            return value;
        };
        name = trim(name);
        model = trim(model);
        id = trim(id);
        if (id.empty() || id.size() >= iPhoneMirror::wireless::DeviceIdBytes) return std::nullopt;
        if (name.empty()) name = "UxPlay";
        return ClientIdentity{
            .device_id = std::string(id),
            .name = std::string(name.substr(0, iPhoneMirror::wireless::DeviceNameBytes - 1U)),
            .product_type = std::string(model.substr(0,
                iPhoneMirror::wireless::ProductTypeBytes - 1U)),
        };
    }

    void ensure_connected() {
        ClientIdentity identity;
        bool should_send{};
        {
            std::scoped_lock lock(identity_mutex_);
            should_send = !connected_;
            connected_ = true;
            identity = identity_;
        }
        if (!should_send) return;
        send_connected(identity);
    }

    void send_disconnected(const ClientIdentity& identity) {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Disconnected;
        copy_text(header.device_id, identity.device_id);
        copy_text(header.device_name, identity.name);
        (void)writer_.send(header);
    }

    void send_connected(const ClientIdentity& identity) {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::Connected;
        copy_text(header.device_id, identity.device_id);
        copy_text(header.device_name, identity.name);
        (void)writer_.send(header);
        send_device_info(identity);
    }

    void send_device_info(const ClientIdentity& identity) {
        iPhoneMirror::wireless::MessageHeader header;
        header.type = iPhoneMirror::wireless::MessageType::DeviceInfo;
        copy_text(header.device_id, identity.device_id);
        copy_text(header.product_type, identity.product_type);
        (void)writer_.send(header);
    }

    void write_identity(iPhoneMirror::wireless::MessageHeader& header) const {
        std::scoped_lock lock(identity_mutex_);
        copy_text(header.device_id, identity_.device_id);
        copy_text(header.device_name, identity_.name);
    }

    IpcWriter& writer_;
    unsigned int video_width_{};
    unsigned int video_height_{};
    std::size_t frame_bytes_{};
    bool awaiting_frame_payload_{};
    std::vector<std::uint8_t> video_buffer_;
    std::vector<std::uint8_t> audio_buffer_;
    mutable std::mutex identity_mutex_;
    std::mutex send_mutex_;
    ClientIdentity identity_;
    bool connected_{};
};

class RawPipeReader final {
public:
    RawPipeReader(HANDLE pipe, std::string label,
        std::function<void(std::span<const std::uint8_t>)> on_bytes,
        std::function<void()> on_disconnect,
        IpcWriter& writer)
        : pipe_(pipe), label_(std::move(label)), on_bytes_(std::move(on_bytes)),
          on_disconnect_(std::move(on_disconnect)), writer_(writer) {}

    ~RawPipeReader() { stop(); }
    RawPipeReader(const RawPipeReader&) = delete;
    RawPipeReader& operator=(const RawPipeReader&) = delete;

    void start() { worker_ = std::thread([this] { run(); }); }

    void stop() noexcept {
        stopping_.store(true, std::memory_order_release);
        if (pipe_ && pipe_ != INVALID_HANDLE_VALUE) CancelIoEx(pipe_, nullptr);
        if (worker_.joinable()) worker_.join();
    }

private:
    void run() noexcept {
        std::vector<std::uint8_t> buffer(RawReadBytes);
        while (!stopping_.load(std::memory_order_acquire)) {
            const auto connected = ConnectNamedPipe(pipe_, nullptr) != FALSE ||
                GetLastError() == ERROR_PIPE_CONNECTED;
            if (!connected) {
                const auto error = GetLastError();
                if (stopping_.load(std::memory_order_acquire) ||
                    error == ERROR_OPERATION_ABORTED || error == ERROR_INVALID_HANDLE) {
                    return;
                }
                (void)writer_.send_text(iPhoneMirror::wireless::MessageType::Log,
                    std::format("uxplay_host {}_pipe_connect_failed win32={}", label_, error));
                Sleep(50);
                continue;
            }
            while (!stopping_.load(std::memory_order_acquire)) {
                DWORD received{};
                if (!ReadFile(pipe_, buffer.data(), static_cast<DWORD>(buffer.size()),
                        &received, nullptr) || received == 0) {
                    break;
                }
                on_bytes_(std::span(buffer.data(), received));
            }
            DisconnectNamedPipe(pipe_);
            if (on_disconnect_) on_disconnect_();
        }
    }

    HANDLE pipe_{};
    std::string label_;
    std::function<void(std::span<const std::uint8_t>)> on_bytes_;
    std::function<void()> on_disconnect_;
    IpcWriter& writer_;
    std::atomic_bool stopping_{};
    std::thread worker_;
};

[[nodiscard]] HANDLE create_raw_pipe(const std::wstring& name,
    LocalSecurityAttributes& security) {
    const auto pipe = CreateNamedPipeW(name.c_str(),
        PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 64U * 1024U, static_cast<DWORD>(RawReadBytes), 0, security.get());
    if (pipe == INVALID_HANDLE_VALUE) {
        throw std::runtime_error(std::format("CreateNamedPipe for UxPlay raw stream failed: {}",
            GetLastError()));
    }
    return pipe;
}

struct ChildProcess {
    HANDLE process{};
    HANDLE output{};
};

[[nodiscard]] ChildProcess launch_uxplay(const std::filesystem::path& executable,
    std::wstring command) {
    SECURITY_ATTRIBUTES inheritable{.nLength = sizeof(inheritable),
        .lpSecurityDescriptor = nullptr, .bInheritHandle = TRUE};
    HANDLE output_read{};
    HANDLE output_write{};
    if (!CreatePipe(&output_read, &output_write, &inheritable, 0)) {
        throw std::runtime_error(std::format("CreatePipe for UxPlay output failed: {}",
            GetLastError()));
    }
    if (!SetHandleInformation(output_read, HANDLE_FLAG_INHERIT, 0)) {
        const auto error = GetLastError();
        CloseHandle(output_write);
        CloseHandle(output_read);
        throw std::runtime_error(std::format("Could not secure UxPlay output pipe: {}", error));
    }

    STARTUPINFOW startup{.cb = sizeof(startup), .dwFlags = STARTF_USESTDHANDLES,
        .hStdInput = GetStdHandle(STD_INPUT_HANDLE), .hStdOutput = output_write,
        .hStdError = output_write};
    if (!startup.hStdInput || startup.hStdInput == INVALID_HANDLE_VALUE)
        startup.hStdInput = output_write;
    PROCESS_INFORMATION process{};
    const auto directory = executable.parent_path().wstring();
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup, &process)) {
        const auto error = GetLastError();
        CloseHandle(output_write);
        CloseHandle(output_read);
        throw std::runtime_error(std::format("Could not start FDH2/UxPlay: {}", error));
    }
    CloseHandle(process.hThread);
    CloseHandle(output_write);
    return {.process = process.hProcess, .output = output_read};
}

void read_uxplay_output(HANDLE output, StreamForwarder& forwarder,
    IpcWriter& writer, std::atomic_bool& stopping) noexcept {
    std::array<char, 2048> chunk{};
    std::string pending;
    std::uint32_t forwarded_lines{};
    try {
        while (!stopping.load(std::memory_order_acquire)) {
            DWORD received{};
            if (!ReadFile(output, chunk.data(), static_cast<DWORD>(chunk.size()),
                    &received, nullptr) || received == 0) {
                break;
            }
            pending.append(chunk.data(), received);
            while (true) {
                const auto newline = pending.find('\n');
                if (newline == std::string::npos) break;
                auto line = std::string_view(pending.data(), newline);
                while (!line.empty() && line.back() == '\r') line.remove_suffix(1);
                forwarder.observe_uxplay_line(line);
                if (++forwarded_lines <= 120 || line.find("ERROR") != std::string_view::npos ||
                    line.find("error") != std::string_view::npos ||
                    line.find("connection request") != std::string_view::npos) {
                    (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                        std::string("uxplay: ").append(line));
                }
                pending.erase(0, newline + 1U);
            }
            if (pending.size() > 8192) pending.clear();
        }
    } catch (...) {
        (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "uxplay_host output_reader_failed");
    }
}

[[nodiscard]] std::wstring uxplay_command(const std::filesystem::path& executable,
    std::wstring_view receiver_name, unsigned int width, unsigned int height,
    unsigned int fps, std::wstring_view video_pipe, std::wstring_view audio_pipe) {
    const auto gst_quote_location = [](std::wstring_view value) {
        std::wstring escaped;
        escaped.reserve(value.size() + 2);
        escaped.push_back(L'"');
        for (const auto character : value) {
            if (character == L'\\' || character == L'"')
                escaped.push_back(L'\\');
            escaped.push_back(character);
        }
        escaped.push_back(L'"');
        return escaped;
    };
    auto configured_name = receiver_name.empty()
        ? std::wstring(DefaultReceiverName) : std::wstring(receiver_name);
    // UxPlay's Windows option parser tokenizes the process command line a
    // second time and does not consistently preserve quoted whitespace in
    // the -n value. Use a DNS-SD-safe single token so a display name such as
    // "iPhoneMirror AirPlay" cannot be misread as an extra CLI option.
    for (auto& character : configured_name) {
        if (character == L' ' || character == L'\t') character = L'-';
    }
    // The original receiver and UxPlay use separate Bonjour stacks. When the
    // user switches backends, Windows can retain the old RAOP registration for
    // a few seconds after its process exits. Advertising UxPlay under the same
    // name then fails with DNSServiceErr_NameConflict (-65563) and UxPlay exits.
    // Keep the configured name recognizable while giving this backend its own
    // registration identity.
    const auto name = configured_name + L"-UxPlay-" +
        std::to_wstring(GetCurrentProcessId());
    const auto identity_seed = GetTickCount64() ^
        (static_cast<unsigned long long>(GetCurrentProcessId()) << 24U);
    const auto device_id = std::format(L"02:{:02x}:{:02x}:{:02x}:{:02x}:{:02x}",
        (identity_seed >> 32U) & 0xffU, (identity_seed >> 24U) & 0xffU,
        (identity_seed >> 16U) & 0xffU, (identity_seed >> 8U) & 0xffU,
        identity_seed & 0xffU);
    // Keep UxPlay on its documented legacy ports. Random ports are explicitly
    // unsupported behind a firewall, which is the normal Windows deployment;
    // the advertised mDNS service can be visible while the AirPlay session
    // itself fails to connect when the port changes on every launch.
    const auto tcp_ports = std::wstring(L"7100,7000,7001");
    const auto udp_ports = std::wstring(L"6000,6001,7011");
    // UxPlay appends its own videoscale after -vc. Keep y4menc in the sink
    // pipeline so it receives raw frames after that pass-through scaler;
    // putting it in -vc makes GStreamer try to link Y4M bytes back to videoscale.
    const auto converter = std::wstring(L"videoconvert ! video/x-raw,format=I420");
    // Reinsert cached parameter sets at every IDR so the software decoder can
    // recover cleanly after packet loss or a transport reset. Force parsing so
    // the stream is normalized even when h264parse would otherwise pass it through.
    const auto parser = std::wstring(
        L"h264parse config-interval=-1 disable-passthrough=true");
    // AirPlay transport loss can leave libav with a decodable but visibly
    // corrupted reference chain. Do not publish those frames; discard input
    // until the next sync point instead of forwarding a persistent green image.
    const auto decoder = std::wstring(
        L"avdec_h264 output-corrupt=false discard-corrupted-frames=true "
        L"automatic-request-sync-points=true");
    // GStreamer parses the sink property as a launch string. Escape and quote
    // Windows named-pipe paths so backslashes remain part of one location token.
    // Keep the Y4M dimensions equal to the actual AirPlay frame. UxPlay emits
    // a fresh Y4M header when iOS rotates; the pipe reader accepts those
    // headers and resets its frame size before consuming the next payload.
    // Locking the sink to the negotiated canvas (for example 5120x2880)
    // hides rotation from the UI and forces an expensive full-frame scale.
    const auto video_sink =
        std::wstring(L"videoconvert ! video/x-raw,format=I420 ! y4menc ! filesink location=") +
        gst_quote_location(video_pipe);
    const auto audio_sink = std::wstring(
        L"audioconvert ! audio/x-raw,format=S16LE,rate=44100,channels=2 ! filesink sync=false location=") +
        gst_quote_location(audio_pipe);
    // UxPlay's -s option is the AirPlay client negotiation request.
    const auto resolution = std::format(L"{}x{}@{}", width, height, fps);
    return quote_argument(executable.wstring()) + L" -n " + quote_argument(name) +
        L" -m " + device_id +
        L" -p tcp " + tcp_ports + L" -p udp " + udp_ports +
        L" -nh -s " + quote_argument(resolution) + L" -fps " + std::to_wstring(fps) +
        L" -vp " + quote_argument(parser) + L" -vd " + quote_argument(decoder) +
        L" -vc " + quote_argument(converter) + L" -vs " +
        quote_argument(video_sink) + L" -as " + quote_argument(audio_sink) +
        L" -vsync no -nofreeze -nohold";
}

void prepend_environment_path(std::wstring_view name,
    const std::filesystem::path& first) noexcept {
    try {
        std::array<wchar_t, 32768> current{};
        const auto length = GetEnvironmentVariableW(name.data(), current.data(),
            static_cast<DWORD>(current.size()));
        const auto prefix = first.wstring();
        const auto value = length > 0 && length < current.size()
            ? prefix + L";" + std::wstring(current.data(), length)
            : prefix;
        SetEnvironmentVariableW(std::wstring(name).c_str(), value.c_str());
    } catch (...) {
    }
}

void prepare_uxplay_environment(const std::filesystem::path& executable) noexcept {
    const auto root = executable.parent_path();
    const auto bin = root / L"bin";
    prepend_environment_path(L"PATH", bin);
    prepend_environment_path(L"PATH", root);

    for (const auto& plugin_root : {
             root / L"lib" / L"gstreamer-1.0",
             root.parent_path() / L"lib" / L"gstreamer-1.0"}) {
        if (!std::filesystem::is_directory(plugin_root)) continue;
        SetEnvironmentVariableW(L"GST_PLUGIN_PATH_1_0", plugin_root.c_str());
        SetEnvironmentVariableW(L"GST_PLUGIN_PATH", plugin_root.c_str());
        SetEnvironmentVariableW(L"GST_PLUGIN_SYSTEM_PATH_1_0", plugin_root.c_str());
        SetEnvironmentVariableW(L"GST_PLUGIN_SYSTEM_PATH", plugin_root.c_str());
        break;
    }
    SetEnvironmentVariableW(L"GST_REGISTRY", (root / L"gst-registry.bin").c_str());
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS);
    SetThreadErrorMode(SEM_FAILCRITICALERRORS, nullptr);

    const auto pipe_name = argument_value(argc, argv, L"--pipe");
    const auto stop_event_name = argument_value(argc, argv, L"--stop-event");
    const auto receiver_name = argument_value(argc, argv, L"--name");
    const auto parent_text = argument_value(argc, argv, L"--parent-pid");
    const auto uxplay_override = argument_value(argc, argv, L"--uxplay");
    const auto width = argument_uint(argc, argv, L"--width", 1920);
    const auto height = argument_uint(argc, argv, L"--height", 1080);
    const auto fps = argument_uint(argc, argv, L"--fps", 60);

    if (has_argument(argc, argv, L"--check-runtime"))
        return preflight_uxplay_runtime(uxplay_override);
    if (pipe_name.empty() || stop_event_name.empty() ||
        !supported_capability(width, height, fps)) {
        return 2;
    }

    const auto uxplay = find_uxplay_executable(uxplay_override);
    if (!uxplay) return 3;
    const auto pipe = connect_pipe(pipe_name);
    if (pipe == INVALID_HANDLE_VALUE) return 4;
    IpcWriter writer(pipe);
    (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log, std::format(
        "uxplay_host startup pid={} receiver_capability={}x{}@{} native_video=true executable={}",
        GetCurrentProcessId(), width, height, fps,
        utf8(uxplay->filename().wstring())));

    HANDLE stop_event{};
    HANDLE parent{};
    HANDLE video_pipe{INVALID_HANDLE_VALUE};
    HANDLE audio_pipe{INVALID_HANDLE_VALUE};
    ChildProcess child;
    std::atomic_bool stopping{};
    std::thread output_reader;
    try {
        LocalSecurityAttributes security;
        const auto suffix = std::format(L"{}-{}", GetCurrentProcessId(), GetTickCount64());
        const auto video_pipe_name = L"\\\\.\\pipe\\iPhoneMirror-UxPlay-Video-" + suffix;
        const auto audio_pipe_name = L"\\\\.\\pipe\\iPhoneMirror-UxPlay-Audio-" + suffix;
        video_pipe = create_raw_pipe(video_pipe_name, security);
        audio_pipe = create_raw_pipe(audio_pipe_name, security);

        stop_event = OpenEventW(SYNCHRONIZE, FALSE, stop_event_name.c_str());
        if (!parent_text.empty()) {
            try {
                parent = OpenProcess(SYNCHRONIZE, FALSE,
                    static_cast<DWORD>(std::stoul(parent_text)));
            } catch (...) {
                parent = nullptr;
            }
        }

        StreamForwarder forwarder(writer);
        RawPipeReader video_reader(video_pipe, "video",
            [&forwarder](std::span<const std::uint8_t> bytes) { forwarder.push_video(bytes); },
            [&forwarder] { forwarder.reset_video_stream(); },
            writer);
        RawPipeReader audio_reader(audio_pipe, "audio",
            [&forwarder](std::span<const std::uint8_t> bytes) { forwarder.push_audio(bytes); },
            [&forwarder] { forwarder.reset_audio_stream(); },
            writer);
        video_reader.start();
        audio_reader.start();
        prepare_uxplay_environment(*uxplay);
        child = launch_uxplay(*uxplay, uxplay_command(*uxplay, receiver_name, width,
            height, fps, video_pipe_name, audio_pipe_name));
        output_reader = std::thread(read_uxplay_output, child.output, std::ref(forwarder),
            std::ref(writer), std::ref(stopping));

        (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "uxplay_host receiver_ready raw_i420=true raw_pcm=true media_cast=false");
        (void)writer.send(iPhoneMirror::wireless::MessageHeader{
            .type = iPhoneMirror::wireless::MessageType::Ready});

        HANDLE waits[3]{};
        DWORD count{};
        if (stop_event) waits[count++] = stop_event;
        if (parent) waits[count++] = parent;
        waits[count++] = child.process;
        const auto wait_result = WaitForMultipleObjects(count, waits, FALSE, INFINITE);
        if (wait_result == WAIT_OBJECT_0 + count - 1) {
            DWORD exit_code{};
            GetExitCodeProcess(child.process, &exit_code);
            (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                std::format("uxplay_host process_exited code={}", exit_code));
        } else if (wait_result == WAIT_FAILED) {
            (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
                std::format("uxplay_host wait_failed win32={}", GetLastError()));
        }

        stopping.store(true, std::memory_order_release);
        if (child.process && WaitForSingleObject(child.process, 0) == WAIT_TIMEOUT) {
            TerminateProcess(child.process, 0);
            WaitForSingleObject(child.process, 2000);
        }
        if (child.output) CancelIoEx(child.output, nullptr);
        if (output_reader.joinable()) output_reader.join();
        video_reader.stop();
        audio_reader.stop();
        forwarder.disconnect();
    } catch (const std::exception& error) {
        (void)writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            std::string("uxplay_host startup_failed: ").append(error.what()));
        stopping.store(true, std::memory_order_release);
        if (child.process) TerminateProcess(child.process, 1);
        if (child.output) CancelIoEx(child.output, nullptr);
        if (output_reader.joinable()) output_reader.join();
    }

    if (child.output) CloseHandle(child.output);
    if (child.process) CloseHandle(child.process);
    if (video_pipe != INVALID_HANDLE_VALUE) CloseHandle(video_pipe);
    if (audio_pipe != INVALID_HANDLE_VALUE) CloseHandle(audio_pipe);
    if (parent) CloseHandle(parent);
    if (stop_event) CloseHandle(stop_event);
    writer.shutdown();
    CloseHandle(pipe);
    return 0;
}
