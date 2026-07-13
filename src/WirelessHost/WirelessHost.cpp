// SPDX-License-Identifier: GPL-3.0-only

#include "IpcProtocol.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <format>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
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

class IpcWriter {
public:
    explicit IpcWriter(HANDLE pipe) : pipe_(pipe) {}

    bool send(iPhoneMirror::wireless::MessageHeader header,
        std::span<const std::uint8_t> payload = {}) noexcept {
        if (payload.size() > iPhoneMirror::wireless::MaxPayloadBytes) return false;
        header.payload_size = static_cast<std::uint32_t>(payload.size());
        header.sequence = sequence_.fetch_add(1, std::memory_order_relaxed) + 1;
        std::scoped_lock lock(mutex_);
        if (!available_.load(std::memory_order_relaxed)) return false;
        if (!write_all(pipe_, &header, sizeof(header)) ||
            (!payload.empty() && !write_all(pipe_, payload.data(), payload.size()))) {
            available_.store(false, std::memory_order_relaxed);
            return false;
        }
        return true;
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

private:
    template <std::size_t Size>
    static void copy_text(char (&destination)[Size], const char* source) noexcept {
        if (!source) return;
        const auto length = std::min<std::size_t>(std::strlen(source), Size - 1);
        std::memcpy(destination, source, length);
        destination[length] = '\0';
    }

    HANDLE pipe_{};
    std::mutex mutex_;
    std::atomic_bool available_{true};
    std::atomic_uint64_t sequence_{};
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

    void videoPlay(char*, double, double) override {}

    void videoGetPlayInfo(double* duration, double* position, double* rate) override {
        if (duration) *duration = 0;
        if (position) *position = 0;
        if (rate) *rate = 0;
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
    std::atomic_uint64_t audio_callbacks_{};
    std::atomic_uint64_t video_callbacks_{};
    std::atomic_uint64_t log_callbacks_{};
};

std::wstring argument_value(int argc, wchar_t** argv, std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index)
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    return {};
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

std::filesystem::path executable_directory() {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(),
        static_cast<DWORD>(path.size()));
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

HANDLE connect_pipe(const std::wstring& pipe_name) {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto pipe = CreateFileW(pipe_name.c_str(), GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        if (GetLastError() != ERROR_PIPE_BUSY && GetLastError() != ERROR_FILE_NOT_FOUND)
            return INVALID_HANDLE_VALUE;
        WaitNamedPipeW(pipe_name.c_str(), 100);
    }
    return INVALID_HANDLE_VALUE;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    const auto pipe_name = argument_value(argc, argv, L"--pipe");
    const auto stop_event_name = argument_value(argc, argv, L"--stop-event");
    const auto receiver_name_wide = argument_value(argc, argv, L"--name");
    const auto parent_text = argument_value(argc, argv, L"--parent-pid");
    const auto library_override = argument_value(argc, argv, L"--library");
    if (pipe_name.empty() || stop_event_name.empty()) return 2;

    const auto pipe = connect_pipe(pipe_name);
    if (pipe == INVALID_HANDLE_VALUE) return 3;
    IpcWriter writer(pipe);

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
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 4;
    }

    const auto start_server = reinterpret_cast<StartServer>(GetProcAddress(library, StartExport));
    const auto stop_server = reinterpret_cast<StopServer>(GetProcAddress(library, StopExport));
    if (!start_server || !stop_server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "AirPlay receiver exports do not match the pinned version");
        FreeLibrary(library);
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 5;
    }

    AirPlayCallback callback(writer);
    auto receiver_name = utf8(receiver_name_wide);
    if (receiver_name.empty()) receiver_name = "iPhoneMirror AirPlay";
    writer.send_text(iPhoneMirror::wireless::MessageType::Log,
        std::format("wireless host starting receiver={} raop_port=5001 "
            "airplay_port=7001", receiver_name).c_str());
    const auto server = start_server(receiver_name.c_str(), 5001, 7001, &callback);
    if (!server) {
        writer.send_text(iPhoneMirror::wireless::MessageType::Log,
            "AirPlay receiver initialization failed");
        FreeLibrary(library);
        CloseHandle(pipe);
        if (search_cookie) RemoveDllDirectory(search_cookie);
        return 6;
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
    if (stop_event) WaitForMultipleObjects(wait_count, waits, FALSE, INFINITE);

    stop_server(server);
    if (parent) CloseHandle(parent);
    if (stop_event) CloseHandle(stop_event);
    FreeLibrary(library);
    CloseHandle(pipe);
    if (search_cookie) RemoveDllDirectory(search_cookie);
    return 0;
}
