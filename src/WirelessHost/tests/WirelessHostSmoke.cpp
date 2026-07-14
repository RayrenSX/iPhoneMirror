#include "IpcProtocol.h"

#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace {

std::wstring quote(std::wstring_view value) {
    return L"\"" + std::wstring(value) + L"\"";
}

bool wait_overlapped(HANDLE io, OVERLAPPED& operation, DWORD timeout,
    DWORD& transferred) {
    if (WaitForSingleObject(operation.hEvent, timeout) != WAIT_OBJECT_0) return false;
    return GetOverlappedResult(io, &operation, &transferred, FALSE) != FALSE;
}

bool read_exact(HANDLE pipe, void* destination, std::size_t size, DWORD timeout) {
    auto* bytes = static_cast<std::uint8_t*>(destination);
    while (size != 0) {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return false;
        DWORD transferred{};
        const auto request = static_cast<DWORD>(
            std::min<std::size_t>(size, 1024U * 1024U));
        auto success = ReadFile(pipe, bytes, request, &transferred, &operation) != FALSE;
        if (!success && GetLastError() == ERROR_IO_PENDING)
            success = wait_overlapped(pipe, operation, timeout, transferred);
        CloseHandle(operation.hEvent);
        if (!success || transferred == 0) return false;
        bytes += transferred;
        size -= transferred;
    }
    return true;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    const auto suffix = std::to_wstring(GetCurrentProcessId()) + L"-" +
        std::to_wstring(GetTickCount64());
    const auto pipe_name = L"\\\\.\\pipe\\iPhoneMirror-HostSmoke-" + suffix;
    const auto stop_name = L"Local\\iPhoneMirror-HostSmoke-Stop-" + suffix;

    const auto pipe = CreateNamedPipeW(pipe_name.c_str(),
        PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1, 0, 4096, 0, nullptr);
    const auto stop_event = CreateEventW(nullptr, TRUE, FALSE, stop_name.c_str());
    if (pipe == INVALID_HANDLE_VALUE || !stop_event) return 3;

    OVERLAPPED connect{};
    connect.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const auto connect_result = ConnectNamedPipe(pipe, &connect);
    const auto connect_error = connect_result ? ERROR_SUCCESS : GetLastError();

    auto command = quote(argv[1]) + L" --pipe " + quote(pipe_name) +
        L" --stop-event " + quote(stop_name) + L" --name \"Smoke Test\"" +
        L" --parent-pid " + std::to_wstring(GetCurrentProcessId()) +
        L" --width 1280 --height 720 --fps 30 --library " + quote(argv[2]);
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(argv[1], command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) return 4;
    CloseHandle(process.hThread);

    DWORD connected_bytes{};
    const auto connected = connect_result || connect_error == ERROR_PIPE_CONNECTED ||
        (connect_error == ERROR_IO_PENDING &&
            wait_overlapped(pipe, connect, 5000, connected_bytes));
    bool ready{};
    bool callback_log{};
    bool capability_log{};
    bool callback_metadata{};
    bool callback_connected{};
    bool callback_video{};
    bool callback_audio{};
    bool second_connected{};
    bool second_video{};
    bool second_audio{};
    bool second_disconnected{};
    bool protocol_valid = connected;
    int message_count{};
    auto last_type = iPhoneMirror::wireless::MessageType::Ready;
    for (int message = 0; protocol_valid && !ready && message < 64; ++message) {
        iPhoneMirror::wireless::MessageHeader header;
        protocol_valid = read_exact(pipe, &header, sizeof(header), 5000) &&
            header.magic == iPhoneMirror::wireless::IpcMagic &&
            header.version == iPhoneMirror::wireless::IpcVersion &&
            header.payload_size <= iPhoneMirror::wireless::MaxPayloadBytes;
        if (!protocol_valid) break;
        std::vector<std::uint8_t> payload(header.payload_size);
        protocol_valid = payload.empty() ||
            read_exact(pipe, payload.data(), payload.size(), 5000);
        ++message_count;
        last_type = header.type;
        if (protocol_valid && header.type == iPhoneMirror::wireless::MessageType::Log &&
            !payload.empty()) {
            const std::string text(reinterpret_cast<const char*>(payload.data()),
                payload.size());
            callback_log = callback_log || text.find("stub protocol log") != std::string::npos;
            capability_log = capability_log ||
                text.find("capability=1280x720@30") != std::string::npos;
            std::cerr << "host: " << text << '\n';
        }
        const std::string device_id(header.device_id);
        const std::string device_name(header.device_name);
        const std::string product_type(header.product_type);
        const std::string os_version(header.os_version);
        callback_metadata = callback_metadata ||
            (header.type == iPhoneMirror::wireless::MessageType::DeviceInfo &&
                device_id == "00:11:22:33:44:55" &&
                product_type == "iPhone9,1" && os_version == "17.5.1");
        callback_connected = callback_connected ||
            (header.type == iPhoneMirror::wireless::MessageType::Connected &&
                device_id == "00:11:22:33:44:55" && device_name == "Stub iPhone");
        callback_video = callback_video ||
            (header.type == iPhoneMirror::wireless::MessageType::Video &&
                device_id == "00:11:22:33:44:55" &&
                header.width == 4 && header.height == 2 && header.stride[0] == 4 &&
                header.plane_size[0] == 8 && payload.size() == 12);
        callback_audio = callback_audio ||
            (header.type == iPhoneMirror::wireless::MessageType::Audio &&
                device_id == "00:11:22:33:44:55" &&
                header.sample_rate == 48000 && header.channels == 2 &&
                header.bits_per_sample == 16 && payload.size() == 8);
        second_connected = second_connected ||
            (header.type == iPhoneMirror::wireless::MessageType::Connected &&
                device_id == "66:77:88:99:AA:BB" && device_name == "Second iPhone");
        second_video = second_video ||
            (header.type == iPhoneMirror::wireless::MessageType::Video &&
                device_id == "66:77:88:99:AA:BB" && header.width == 4 &&
                header.height == 2 && payload.size() == 12);
        second_audio = second_audio ||
            (header.type == iPhoneMirror::wireless::MessageType::Audio &&
                device_id == "66:77:88:99:AA:BB" && payload.size() == 8);
        second_disconnected = second_disconnected ||
            (header.type == iPhoneMirror::wireless::MessageType::Disconnected &&
                device_id == "66:77:88:99:AA:BB");
        ready = protocol_valid &&
            header.type == iPhoneMirror::wireless::MessageType::Ready;
    }
    SetEvent(stop_event);
    const auto exited = WaitForSingleObject(process.hProcess, 5000) == WAIT_OBJECT_0;
    if (!exited) TerminateProcess(process.hProcess, 1);
    DWORD exit_code{STILL_ACTIVE};
    GetExitCodeProcess(process.hProcess, &exit_code);

    CloseHandle(connect.hEvent);
    CloseHandle(process.hProcess);
    CloseHandle(stop_event);
    CloseHandle(pipe);
    if (!protocol_valid || !ready || !callback_log || !capability_log || !callback_metadata ||
        !callback_connected ||
        !callback_video || !callback_audio || !second_connected || !second_video ||
        !second_audio || !second_disconnected || !exited) {
        std::cerr << "wireless host IPC smoke failed: connected=" << connected
            << " protocol=" << protocol_valid << " ready=" << ready
            << " callback_log=" << callback_log
            << " capability_log=" << capability_log
            << " callback_metadata=" << callback_metadata
            << " callback_connected=" << callback_connected
            << " callback_video=" << callback_video
            << " callback_audio=" << callback_audio
            << " second_connected=" << second_connected
            << " second_video=" << second_video
            << " second_audio=" << second_audio
            << " second_disconnected=" << second_disconnected
            << " messages=" << message_count
            << " last_type=" << static_cast<unsigned>(last_type)
            << " exited=" << exited << " exit_code=" << exit_code << '\n';
        return 1;
    }
    std::cout << "Wireless host IPC smoke passed\n";
    return 0;
}
