#include "IpcProtocol.h"
#include "HttpUrl.h"

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

bool write_exact(HANDLE pipe, const void* source, std::size_t size, DWORD timeout) {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return false;
        DWORD transferred{};
        const auto request = static_cast<DWORD>(
            std::min<std::size_t>(size, 1024U * 1024U));
        auto success = WriteFile(pipe, bytes, request, &transferred, &operation) != FALSE;
        if (!success && GetLastError() == ERROR_IO_PENDING)
            success = wait_overlapped(pipe, operation, timeout, transferred);
        CloseHandle(operation.hEvent);
        if (!success || transferred == 0) return false;
        bytes += transferred;
        size -= transferred;
    }
    return true;
}

bool send_exact(SOCKET socket, std::string_view data) {
    while (!data.empty()) {
        const auto sent = send(socket, data.data(),
            static_cast<int>(std::min<std::size_t>(data.size(), INT_MAX)), 0);
        if (sent <= 0) return false;
        data.remove_prefix(static_cast<std::size_t>(sent));
    }
    return true;
}

std::string receive_headers(SOCKET socket, std::size_t limit = 1024) {
    std::string response;
    std::array<char, 128> bytes{};
    while (response.size() < limit &&
        response.find("\r\n\r\n") == std::string::npos) {
        const auto count = recv(socket, bytes.data(), static_cast<int>(bytes.size()), 0);
        if (count <= 0) break;
        response.append(bytes.data(), static_cast<std::size_t>(count));
    }
    return response;
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
    bool startup_log{};
    bool wait_end_log{};
    bool ipc_summary_log{};
    bool callback_summary_log{};
    bool dlna_http_log{};
    bool dlna_soap_log{};
    bool raw_sensitive_log{};
    bool callback_metadata{};
    bool callback_connected{};
    bool callback_video{};
    bool callback_audio{};
    bool second_connected{};
    bool second_video{};
    bool second_audio{};
    bool second_disconnected{};
    bool media_play{};
    bool invalid_media_values_normalized{};
    std::atomic_bool dlna_media_play{};
    std::atomic_bool dlna_media_pause{};
    std::atomic_bool dlna_media_seek{};
    std::atomic_bool dlna_media_resume{};
    std::atomic_uint64_t latest_media_command_id{};
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
                callback_log = callback_log ||
                    (text.find("airplay level=6 message_bytes=") != std::string::npos &&
                        text.find("message_fp=anon-") != std::string::npos);
                capability_log = capability_log ||
                    text.find("capability=1280x720@30") != std::string::npos;
                startup_log = startup_log ||
                    text.find("wireless_host startup pid=") != std::string::npos;
                wait_end_log = wait_end_log ||
                    text.find("wireless_host wait_end reason=") != std::string::npos;
                ipc_summary_log = ipc_summary_log ||
                    text.find("ipc_summary enqueued=") != std::string::npos;
                callback_summary_log = callback_summary_log ||
                    text.find("callback_summary connected=") != std::string::npos;
                dlna_http_log = dlna_http_log ||
                    text.find("dlna http request=") != std::string::npos &&
                    text.find("status=") != std::string::npos;
                dlna_soap_log = dlna_soap_log ||
                    text.find("dlna soap request=") != std::string::npos;
                raw_sensitive_log = raw_sensitive_log ||
                    text.find("stub protocol log") != std::string::npos ||
                    text.find("00:11:22:33:44:55") != std::string::npos ||
                    text.find("66:77:88:99:AA:BB") != std::string::npos ||
                    text.find("Stub iPhone") != std::string::npos ||
                    text.find("Second iPhone") != std::string::npos ||
                    text.find("0123456789abcdef0123456789abcdef") != std::string::npos;
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
            invalid_media_values_normalized = invalid_media_values_normalized ||
                (header.type == iPhoneMirror::wireless::MessageType::MediaPlay &&
                    header.media_position == 0 && header.media_volume == 1 &&
                    std::string(reinterpret_cast<const char*>(payload.data()), payload.size()) ==
                        "https://example.test/invalid-values.mp4");
            if (header.type == iPhoneMirror::wireless::MessageType::MediaPlay &&
                std::string(reinterpret_cast<const char*>(payload.data()), payload.size()) ==
                    "https://example.test/dlna.m3u8?x=1&y=2") {
                dlna_media_play.store(true, std::memory_order_release);
                latest_media_command_id.store(
                    header.media_command_id, std::memory_order_release);
            }
            if (header.type == iPhoneMirror::wireless::MessageType::MediaPause) {
                dlna_media_pause.store(true, std::memory_order_release);
                latest_media_command_id.store(
                    header.media_command_id, std::memory_order_release);
            }
            if (header.type == iPhoneMirror::wireless::MessageType::MediaSeek &&
                header.media_position == 42.0) {
                dlna_media_seek.store(true, std::memory_order_release);
                latest_media_command_id.store(
                    header.media_command_id, std::memory_order_release);
            }
            if (header.type == iPhoneMirror::wireless::MessageType::MediaResume) {
                dlna_media_resume.store(true, std::memory_order_release);
                latest_media_command_id.store(
                    header.media_command_id, std::memory_order_release);
            }
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
    const auto url_validation =
        iPhoneMirror::wireless::is_valid_http_url("HTTP://example.test/video.mp4") &&
        iPhoneMirror::wireless::is_valid_http_url("https://[::1]/live.m3u8") &&
        !iPhoneMirror::wireless::is_valid_http_url("http://[") &&
        !iPhoneMirror::wireless::is_valid_http_url("https:///missing-host") &&
        !iPhoneMirror::wireless::is_valid_http_url("https://example.test/%zz");
    const std::string invalid_uri_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID><CurrentURI>http://[</CurrentURI>"
        "<CurrentURIMetaData></CurrentURIMetaData>"
        "</u:SetAVTransportURI></s:Body></s:Envelope>";
    const auto invalid_uri_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        invalid_uri_body.size(), invalid_uri_body);
    const auto invalid_uri_rejected = tcp_request(18090, invalid_uri_request)
        .find("500 Internal Server Error") != std::string::npos;
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
    const std::string pause_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:Pause xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID></u:Pause></s:Body></s:Envelope>";
    const auto pause_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#Pause\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        pause_body.size(), pause_body);
    const auto dlna_pause_ok = tcp_request(18090, pause_request).find("200 OK") !=
        std::string::npos;
    const std::string seek_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:Seek xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>00:00:42</Target>"
        "</u:Seek></s:Body></s:Envelope>";
    const auto seek_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#Seek\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        seek_body.size(), seek_body);
    const auto dlna_seek_ok = tcp_request(18090, seek_request).find("200 OK") !=
        std::string::npos;
    const auto dlna_resume_ok = tcp_request(18090, play_request).find("200 OK") !=
        std::string::npos;
    const auto dlna_discovery = dlna_ssdp_discover(1900);
    for (int attempt = 0; attempt < 40 &&
            (!dlna_media_play.load(std::memory_order_acquire) ||
             !dlna_media_pause.load(std::memory_order_acquire) ||
             !dlna_media_seek.load(std::memory_order_acquire) ||
             !dlna_media_resume.load(std::memory_order_acquire)); ++attempt) Sleep(50);
    const auto command_before_invalid_seek = latest_media_command_id.load(
        std::memory_order_acquire);
    const std::string invalid_seek_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:Seek xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>00:00:1e309</Target>"
        "</u:Seek></s:Body></s:Envelope>";
    const auto invalid_seek_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#Seek\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        invalid_seek_body.size(), invalid_seek_body);
    const auto invalid_seek_rejected = tcp_request(18090, invalid_seek_request)
        .find("500 Internal Server Error") != std::string::npos;
    Sleep(100);
    const auto invalid_seek_not_forwarded = command_before_invalid_seek != 0 &&
        latest_media_command_id.load(std::memory_order_acquire) == command_before_invalid_seek;
    const auto transport_state = [&] {
        const std::string body =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
            "<s:Body><u:GetTransportInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
            "<InstanceID>0</InstanceID></u:GetTransportInfo></s:Body></s:Envelope>";
        const auto request = std::format(
            "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
            "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo\"\r\n"
            "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            body.size(), body);
        return tcp_request(18090, request);
    };
    const auto current_command_id = latest_media_command_id.load(
        std::memory_order_acquire);
    iPhoneMirror::wireless::MessageHeader stale_stop_request;
    stale_stop_request.type = iPhoneMirror::wireless::MessageType::MediaStopRequest;
    stale_stop_request.media_command_id = current_command_id > 1
        ? current_command_id - 1 : current_command_id + 1;
    const auto stale_stop_write = current_command_id != 0 &&
        write_exact(pipe, &stale_stop_request, sizeof(stale_stop_request), 5000);
    Sleep(100);
    const auto stale_stop_ignored = stale_stop_write &&
        transport_state().find(
            "<CurrentTransportState>PLAYING</CurrentTransportState>") !=
            std::string::npos;

    iPhoneMirror::wireless::MessageHeader stop_request;
    stop_request.type = iPhoneMirror::wireless::MessageType::MediaStopRequest;
    stop_request.media_command_id = current_command_id;
    const auto controller_stop_write = stop_request.media_command_id != 0 &&
        write_exact(pipe, &stop_request, sizeof(stop_request), 5000);
    bool dlna_controller_stop{};
    for (int attempt = 0; controller_stop_write && attempt < 40; ++attempt) {
        dlna_controller_stop = transport_state()
            .find("<CurrentTransportState>STOPPED</CurrentTransportState>") !=
            std::string::npos;
        if (dlna_controller_stop) break;
        Sleep(50);
    }
    iPhoneMirror::wireless::MessageHeader late_playback;
    late_playback.type = iPhoneMirror::wireless::MessageType::PlaybackState;
    late_playback.media_command_id = current_command_id;
    late_playback.media_duration = 300;
    late_playback.media_position = 99;
    late_playback.media_rate = 1;
    const auto late_playback_write = dlna_controller_stop &&
        write_exact(pipe, &late_playback, sizeof(late_playback), 5000);
    Sleep(100);
    const std::string position_body =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        "<s:Body><u:GetPositionInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
        "<InstanceID>0</InstanceID></u:GetPositionInfo></s:Body></s:Envelope>";
    const auto position_request = std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nHost: 127.0.0.1:18090\r\n"
        "SOAPACTION: \"urn:schemas-upnp-org:service:AVTransport:1#GetPositionInfo\"\r\n"
        "Content-Type: text/xml\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        position_body.size(), position_body);
    const auto late_playback_ignored = late_playback_write &&
        tcp_request(18090, position_request).find("00:01:39") == std::string::npos;
    const auto slow_client = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    sockaddr_in slow_address{AF_INET, htons(18090),
        {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
    DWORD slow_timeout = 2000;
    if (slow_client != INVALID_SOCKET)
        setsockopt(slow_client, SOL_SOCKET, SO_RCVTIMEO,
            reinterpret_cast<const char*>(&slow_timeout), sizeof(slow_timeout));
    constexpr std::string_view slow_headers =
        "POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Host: 127.0.0.1:18090\r\nContent-Length: 1\r\n"
        "Expect: 100-continue\r\nConnection: close\r\n\r\n";
    const auto slow_connected = slow_client != INVALID_SOCKET &&
        ::connect(slow_client, reinterpret_cast<const sockaddr*>(&slow_address),
            sizeof(slow_address)) == 0 &&
        send_exact(slow_client, slow_headers);
    const auto continue_response = slow_connected
        ? receive_headers(slow_client) : std::string{};
    const auto slow_client_active =
        continue_response.find("100 Continue") != std::string::npos;
    const auto stop_started = GetTickCount64();
    SetEvent(stop_event);
    const auto exited = WaitForSingleObject(process.hProcess, 3000) == WAIT_OBJECT_0;
    const auto prompt_slow_client_shutdown = slow_client_active && exited &&
        GetTickCount64() - stop_started < 2500;
    if (slow_client != INVALID_SOCKET) closesocket(slow_client);
    if (!exited) TerminateProcess(process.hProcess, 1);
    pipe_reader.join();
    DWORD exit_code{STILL_ACTIVE};
    GetExitCodeProcess(process.hProcess, &exit_code);

    CloseHandle(connect.hEvent);
    CloseHandle(process.hProcess);
    CloseHandle(stop_event);
    CloseHandle(pipe);
    WSACleanup();
    if (!protocol_valid || !ready || !callback_log || !capability_log || !startup_log ||
        !wait_end_log || !ipc_summary_log || !callback_summary_log || !dlna_http_log ||
        !dlna_soap_log || raw_sensitive_log || !callback_metadata ||
        !callback_connected ||
        !callback_video || !callback_audio || !second_connected || !second_video ||
        !second_audio || !second_disconnected || !media_play ||
        !invalid_media_values_normalized || !dlna_media_play ||
        !dlna_media_pause || !dlna_media_seek || !dlna_media_resume ||
        !dlna_description || !dlna_scpd || !url_validation ||
        !invalid_uri_rejected || !set_uri_ok || !dlna_play_ok ||
        !dlna_pause_ok || !dlna_seek_ok || !dlna_resume_ok ||
        !invalid_seek_rejected || !invalid_seek_not_forwarded ||
        !dlna_discovery || !stale_stop_write || !stale_stop_ignored ||
        !controller_stop_write ||
        !dlna_controller_stop || !late_playback_write ||
        !late_playback_ignored || !prompt_slow_client_shutdown) {
        std::cerr << "wireless host IPC smoke failed: connected=" << connected
            << " protocol=" << protocol_valid << " ready=" << ready
            << " callback_log=" << callback_log
            << " capability_log=" << capability_log
            << " startup_log=" << startup_log
            << " wait_end_log=" << wait_end_log
            << " ipc_summary_log=" << ipc_summary_log
            << " callback_summary_log=" << callback_summary_log
            << " dlna_http_log=" << dlna_http_log
            << " dlna_soap_log=" << dlna_soap_log
            << " raw_sensitive_log=" << raw_sensitive_log
            << " callback_metadata=" << callback_metadata
            << " callback_connected=" << callback_connected
            << " callback_video=" << callback_video
            << " callback_audio=" << callback_audio
            << " second_connected=" << second_connected
            << " second_video=" << second_video
            << " second_audio=" << second_audio
            << " second_disconnected=" << second_disconnected
            << " media_play=" << media_play
            << " invalid_media_values_normalized=" << invalid_media_values_normalized
            << " dlna_media_play=" << dlna_media_play
            << " dlna_media_pause=" << dlna_media_pause
            << " dlna_media_seek=" << dlna_media_seek
            << " dlna_media_resume=" << dlna_media_resume
            << " dlna_description=" << dlna_description
            << " dlna_scpd=" << dlna_scpd
            << " set_uri_ok=" << set_uri_ok
            << " url_validation=" << url_validation
            << " invalid_uri_rejected=" << invalid_uri_rejected
            << " dlna_play_ok=" << dlna_play_ok
            << " dlna_pause_ok=" << dlna_pause_ok
            << " dlna_seek_ok=" << dlna_seek_ok
            << " dlna_resume_ok=" << dlna_resume_ok
            << " invalid_seek_rejected=" << invalid_seek_rejected
            << " invalid_seek_not_forwarded=" << invalid_seek_not_forwarded
            << " dlna_discovery=" << dlna_discovery
            << " stale_stop_write=" << stale_stop_write
            << " stale_stop_ignored=" << stale_stop_ignored
            << " controller_stop_write=" << controller_stop_write
            << " dlna_controller_stop=" << dlna_controller_stop
            << " late_playback_write=" << late_playback_write
            << " late_playback_ignored=" << late_playback_ignored
            << " slow_connected=" << slow_connected
            << " slow_client_active=" << slow_client_active
            << " prompt_slow_client_shutdown=" << prompt_slow_client_shutdown
            << " messages=" << message_count
            << " last_type=" << static_cast<unsigned>(last_type)
            << " exited=" << exited << " exit_code=" << exit_code << '\n';
        return 1;
    }
    std::cout << "Wireless host IPC smoke passed\n";
    return 0;
}
