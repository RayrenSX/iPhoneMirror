#include "IpcProtocol.h"

#include <WinSock2.h>
#include <WS2tcpip.h>
#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <format>
#include <iostream>
#include <string>
#include <string_view>
#include <thread>
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

std::string tcp_request(unsigned short port, std::string_view request) {
    SOCKET socket{INVALID_SOCKET};
    for (int attempt = 0; attempt < 50; ++attempt) {
        socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
            nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
        if (socket == INVALID_SOCKET) return {};
        sockaddr_in address{AF_INET, htons(port), {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
        if (connect(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0)
            break;
        closesocket(socket);
        socket = INVALID_SOCKET;
        Sleep(50);
    }
    if (socket == INVALID_SOCKET) return {};
    DWORD timeout = 5000;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    const auto sent = send(socket, request.data(), static_cast<int>(request.size()), 0);
    if (sent < 0 || static_cast<std::size_t>(sent) != request.size()) {
        closesocket(socket);
        return {};
    }
    shutdown(socket, SD_SEND);
    std::string response;
    std::array<char, 4096> bytes{};
    while (true) {
        const auto count = recv(socket, bytes.data(), static_cast<int>(bytes.size()), 0);
        if (count <= 0) break;
        response.append(bytes.data(), static_cast<std::size_t>(count));
    }
    closesocket(socket);
    return response;
}

bool dlna_ssdp_discover(unsigned short port) {
    const auto socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (socket == INVALID_SOCKET) return false;
    DWORD timeout = 500;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeout), sizeof(timeout));

    in_addr interface_address{.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}};
    char hostname[256]{};
    addrinfo hints{.ai_family = AF_INET};
    addrinfo* addresses{};
    if (gethostname(hostname, sizeof(hostname)) == 0 &&
        getaddrinfo(hostname, nullptr, &hints, &addresses) == 0) {
        for (auto* entry = addresses; entry; entry = entry->ai_next) {
            const auto candidate = reinterpret_cast<const sockaddr_in*>(entry->ai_addr)->sin_addr;
            const auto host_order = ntohl(candidate.S_un.S_addr);
            if ((host_order >> 24) != 127 && (host_order >> 16) != 0xA9FE) {
                interface_address = candidate;
                break;
            }
        }
        freeaddrinfo(addresses);
    }
    sockaddr_in local{AF_INET, 0, interface_address};
    sockaddr_in destination{AF_INET, htons(port)};
    inet_pton(AF_INET, "239.255.255.250", &destination.sin_addr);
    const auto request = std::string(
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n"
        "MAN: \"ssdp:discover\"\r\nMX: 1\r\n"
        "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
    auto success = bind(socket, reinterpret_cast<const sockaddr*>(&local), sizeof(local)) == 0 &&
        setsockopt(socket, IPPROTO_IP, IP_MULTICAST_IF,
            reinterpret_cast<const char*>(&interface_address), sizeof(interface_address)) == 0 &&
        sendto(socket, request.data(), static_cast<int>(request.size()), 0,
            reinterpret_cast<const sockaddr*>(&destination), sizeof(destination)) > 0;
    std::array<char, 4096> response{};
    bool discovered{};
    for (int attempt = 0; success && !discovered && attempt < 8; ++attempt) {
        sockaddr_in source{};
        int source_length = sizeof(source);
        const auto count = recvfrom(socket, response.data(),
            static_cast<int>(response.size()), 0,
            reinterpret_cast<sockaddr*>(&source), &source_length);
        discovered = count > 0 &&
            std::string_view(response.data(), static_cast<std::size_t>(count))
                .find("iPhoneMirror/1.0") != std::string_view::npos;
    }
    closesocket(socket);
    return discovered;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 2;
    const auto suffix = std::to_wstring(GetCurrentProcessId()) + L"-" +
        std::to_wstring(GetTickCount64());
    const auto pipe_name = L"\\\\.\\pipe\\iPhoneMirror-HostSmoke-" + suffix;
    const auto stop_name = L"Local\\iPhoneMirror-HostSmoke-Stop-" + suffix;

    const auto pipe = CreateNamedPipeW(pipe_name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1, 0, 64U * 1024U, 0, nullptr);
    const auto stop_event = CreateEventW(nullptr, TRUE, FALSE, stop_name.c_str());
    if (pipe == INVALID_HANDLE_VALUE || !stop_event) return 3;

    OVERLAPPED connect{};
    connect.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const auto connect_result = ConnectNamedPipe(pipe, &connect);
    const auto connect_error = connect_result ? ERROR_SUCCESS : GetLastError();

    auto command = quote(argv[1]) + L" --pipe " + quote(pipe_name) +
        L" --stop-event " + quote(stop_name) + L" --name \"Smoke Test\"" +
        L" --parent-pid " + std::to_wstring(GetCurrentProcessId()) +
        L" --width 1280 --height 720 --fps 30 --mode combined" +
        L" --raop-port 5001 --airplay-port 7001 --dlna-port 18090" +
        L" --dlna-ssdp-port 1900 --library " + quote(argv[2]);
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(argv[1], command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) return 4;
    CloseHandle(process.hThread);

    DWORD connected_bytes{};
    const auto connected = connect_result || connect_error == ERROR_PIPE_CONNECTED ||
        (connect_error == ERROR_IO_PENDING &&
            wait_overlapped(pipe, connect, 5000, connected_bytes));
    std::atomic_bool ready{};
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
    bool media_play{};
    std::atomic_bool dlna_media_play{};
    bool protocol_valid = connected;
    int message_count{};
    auto last_type = iPhoneMirror::wireless::MessageType::Ready;
    std::thread pipe_reader([&] {
        for (int message = 0; protocol_valid && message < 128; ++message) {
            iPhoneMirror::wireless::MessageHeader header;
            const auto header_read = read_exact(pipe, &header, sizeof(header), 5000);
            if (!header_read && ready.load(std::memory_order_acquire)) break;
            protocol_valid = header_read &&
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
            media_play = media_play ||
                (header.type == iPhoneMirror::wireless::MessageType::MediaPlay &&
                    header.media_command_id != 0 && header.media_position == 12.5 &&
                    header.media_volume == 0.75 &&
                    std::string(reinterpret_cast<const char*>(payload.data()), payload.size()) ==
                        "https://example.test/video.m3u8");
            if (header.type == iPhoneMirror::wireless::MessageType::MediaPlay &&
                std::string(reinterpret_cast<const char*>(payload.data()), payload.size()) ==
                    "https://example.test/dlna.m3u8?x=1&y=2")
                dlna_media_play.store(true, std::memory_order_release);
            if (header.type == iPhoneMirror::wireless::MessageType::Ready)
                ready.store(true, std::memory_order_release);
        }
    });
    const auto description = tcp_request(18090,
        "GET /dlna/device.xml HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "Connection: close\r\n\r\n");
    const auto dlna_description = description.find("Smoke Test Video") != std::string::npos &&
        description.find("urn:schemas-upnp-org:device:MediaRenderer:1") != std::string::npos;
    const auto avtransport = tcp_request(18090,
        "GET /dlna/avtransport.xml HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "Connection: close\r\n\r\n");
    const auto connection_manager = tcp_request(18090,
        "GET /dlna/connectionmanager.xml HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "Connection: close\r\n\r\n");
    const auto rendering_control = tcp_request(18090,
        "GET /dlna/renderingcontrol.xml HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "Connection: close\r\n\r\n");
    const auto dlna_scpd =
        avtransport.find("<name>Speed</name>") != std::string::npos &&
        avtransport.find("<name>LastChange</name>") != std::string::npos &&
        connection_manager.find("<name>SinkProtocolInfo</name>") != std::string::npos &&
        rendering_control.find("<name>DesiredVolume</name>") != std::string::npos;
    const std::string set_uri_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID>"
        "<CurrentURI>https://example.test/dlna.m3u8?x=1&amp;y=2</CurrentURI>"
        "<CurrentURIMetaData></CurrentURIMetaData>"
        "</u:SetAVTransportURI></s:Body></s:Envelope>";
    const auto set_uri_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        set_uri_body.size(), set_uri_body);
    const auto set_uri_ok = tcp_request(18090, set_uri_request).find("200 OK") !=
        std::string::npos;
    const std::string play_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:Play xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID><Speed>1</Speed></u:Play></s:Body></s:Envelope>";
    const auto play_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#Play\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        play_body.size(), play_body);
    const auto dlna_play_ok = tcp_request(18090, play_request).find("200 OK") !=
        std::string::npos;
    const auto dlna_discovery = dlna_ssdp_discover(1900);
    for (int attempt = 0; attempt < 40 &&
            !dlna_media_play.load(std::memory_order_acquire); ++attempt) Sleep(50);
    SetEvent(stop_event);
    const auto exited = WaitForSingleObject(process.hProcess, 5000) == WAIT_OBJECT_0;
    if (!exited) TerminateProcess(process.hProcess, 1);
    pipe_reader.join();
    DWORD exit_code{STILL_ACTIVE};
    GetExitCodeProcess(process.hProcess, &exit_code);

    CloseHandle(connect.hEvent);
    CloseHandle(process.hProcess);
    CloseHandle(stop_event);
    CloseHandle(pipe);
    WSACleanup();
    if (!protocol_valid || !ready || !callback_log || !capability_log || !callback_metadata ||
        !callback_connected ||
        !callback_video || !callback_audio || !second_connected || !second_video ||
        !second_audio || !second_disconnected || !media_play || !dlna_media_play ||
        !dlna_description || !dlna_scpd || !set_uri_ok || !dlna_play_ok ||
        !dlna_discovery || !exited) {
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
            << " media_play=" << media_play
            << " dlna_media_play=" << dlna_media_play
            << " dlna_description=" << dlna_description
            << " dlna_scpd=" << dlna_scpd
            << " set_uri_ok=" << set_uri_ok
            << " dlna_play_ok=" << dlna_play_ok
            << " dlna_discovery=" << dlna_discovery
            << " messages=" << message_count
            << " last_type=" << static_cast<unsigned>(last_type)
            << " exited=" << exited << " exit_code=" << exit_code << '\n';
        return 1;
    }
    std::cout << "Wireless host IPC smoke passed\n";
    return 0;
}
