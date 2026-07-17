// SPDX-License-Identifier: GPL-3.0-only

#include "DlnaRenderer.h"

#include <WinSock2.h>
#include <WS2tcpip.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cctype>
#include <format>
#include <initializer_list>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace iPhoneMirror::wireless {
namespace {

constexpr std::string_view DeviceType =
    "urn:schemas-upnp-org:device:MediaRenderer:1";
constexpr std::string_view AvTransportType =
    "urn:schemas-upnp-org:service:AVTransport:1";
constexpr std::string_view ConnectionManagerType =
    "urn:schemas-upnp-org:service:ConnectionManager:1";
constexpr std::string_view RenderingControlType =
    "urn:schemas-upnp-org:service:RenderingControl:1";
constexpr std::string_view MulticastAddress = "239.255.255.250";
constexpr std::uint16_t SsdpPort = 1900;

std::string lower(std::string_view value) {
    std::string result(value);
    std::ranges::transform(result, result.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return result;
}

std::string trim(std::string_view value) {
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front())))
        value.remove_prefix(1);
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
        value.remove_suffix(1);
    return std::string(value);
}

std::string xml_escape(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto character : value) {
        switch (character) {
        case '&': result += "&amp;"; break;
        case '<': result += "&lt;"; break;
        case '>': result += "&gt;"; break;
        case '\"': result += "&quot;"; break;
        case '\'': result += "&apos;"; break;
        default: result.push_back(character); break;
        }
    }
    return result;
}

std::string xml_unescape(std::string value) {
    for (const auto& [entity, character] : std::array{
            std::pair{"&amp;", "&"}, std::pair{"&lt;", "<"},
            std::pair{"&gt;", ">"}, std::pair{"&quot;", "\""},
            std::pair{"&apos;", "'"}}) {
        std::size_t offset{};
        while ((offset = value.find(entity, offset)) != std::string::npos) {
            value.replace(offset, std::strlen(entity), character);
            offset += std::strlen(character);
        }
    }
    return value;
}

std::optional<std::string> xml_value(std::string_view xml, std::string_view name) {
    std::size_t offset{};
    while ((offset = xml.find('<', offset)) != std::string_view::npos) {
        if (offset + 1 >= xml.size() || xml[offset + 1] == '/' ||
            xml[offset + 1] == '!' || xml[offset + 1] == '?') {
            ++offset;
            continue;
        }
        const auto tag_end = xml.find('>', offset + 1);
        if (tag_end == std::string_view::npos) return std::nullopt;
        auto tag = xml.substr(offset + 1, tag_end - offset - 1);
        const auto space = tag.find_first_of(" \t\r\n");
        if (space != std::string_view::npos) tag = tag.substr(0, space);
        const auto colon = tag.rfind(':');
        const auto local = colon == std::string_view::npos ? tag : tag.substr(colon + 1);
        if (local != name) {
            offset = tag_end + 1;
            continue;
        }
        const auto close = std::format("</{}>", tag);
        const auto close_at = xml.find(close, tag_end + 1);
        if (close_at == std::string_view::npos) return std::nullopt;
        return xml_unescape(std::string(xml.substr(
            tag_end + 1, close_at - tag_end - 1)));
    }
    return std::nullopt;
}

double parse_dlna_time(std::string_view value) noexcept {
    unsigned hours{}, minutes{};
    double seconds{};
    const auto first = value.find(':');
    const auto second = first == std::string_view::npos ? first : value.find(':', first + 1);
    if (first == std::string_view::npos || second == std::string_view::npos) return 0;
    const auto parse_unsigned = [](std::string_view text, unsigned& output) {
        const auto [end, error] = std::from_chars(
            text.data(), text.data() + text.size(), output);
        return error == std::errc{} && end == text.data() + text.size();
    };
    if (!parse_unsigned(value.substr(0, first), hours) ||
        !parse_unsigned(value.substr(first + 1, second - first - 1), minutes)) return 0;
    try {
        seconds = std::stod(std::string(value.substr(second + 1)));
    } catch (...) { return 0; }
    return hours * 3600.0 + minutes * 60.0 + seconds;
}

std::string format_dlna_time(double seconds) {
    seconds = std::max(0.0, seconds);
    const auto total = static_cast<unsigned long long>(seconds);
    return std::format("{:02}:{:02}:{:02}", total / 3600,
        total / 60 % 60, total % 60);
}

struct HttpRequest {
    std::string method;
    std::string path;
    std::map<std::string, std::string, std::less<>> headers;
    std::string body;
};

bool send_all(SOCKET socket, std::string_view data) noexcept {
    while (!data.empty()) {
        const auto sent = send(socket, data.data(),
            static_cast<int>(std::min<std::size_t>(data.size(), INT_MAX)), 0);
        if (sent <= 0) return false;
        data.remove_prefix(static_cast<std::size_t>(sent));
    }
    return true;
}

std::optional<HttpRequest> read_request(SOCKET socket) {
    std::string bytes;
    std::array<char, 4096> buffer{};
    while (bytes.find("\r\n\r\n") == std::string::npos && bytes.size() < 64U * 1024U) {
        const auto count = recv(socket, buffer.data(), static_cast<int>(buffer.size()), 0);
        if (count <= 0) return std::nullopt;
        bytes.append(buffer.data(), static_cast<std::size_t>(count));
    }
    const auto header_end = bytes.find("\r\n\r\n");
    if (header_end == std::string::npos) return std::nullopt;

    HttpRequest request;
    const auto first_end = bytes.find("\r\n");
    if (first_end == std::string::npos) return std::nullopt;
    const auto first = std::string_view(bytes).substr(0, first_end);
    const auto method_end = first.find(' ');
    const auto path_end = method_end == std::string_view::npos ? method_end :
        first.find(' ', method_end + 1);
    if (method_end == std::string_view::npos || path_end == std::string_view::npos)
        return std::nullopt;
    request.method = std::string(first.substr(0, method_end));
    request.path = std::string(first.substr(method_end + 1, path_end - method_end - 1));

    std::size_t line_at = first_end + 2;
    while (line_at < header_end) {
        const auto line_end = bytes.find("\r\n", line_at);
        if (line_end == std::string::npos || line_end > header_end) break;
        const auto line = std::string_view(bytes).substr(line_at, line_end - line_at);
        const auto separator = line.find(':');
        if (separator != std::string_view::npos) {
            request.headers.emplace(lower(trim(line.substr(0, separator))),
                trim(line.substr(separator + 1)));
        }
        line_at = line_end + 2;
    }

    std::size_t content_length{};
    if (const auto length = request.headers.find("content-length");
        length != request.headers.end()) {
        const auto [end, error] = std::from_chars(length->second.data(),
            length->second.data() + length->second.size(), content_length);
        if (error != std::errc{} || end != length->second.data() + length->second.size() ||
            content_length > 1024U * 1024U) return std::nullopt;
    }
    const auto body_at = header_end + 4;
    if (bytes.size() - body_at < content_length) {
        const auto expect = request.headers.find("expect");
        if (expect != request.headers.end() &&
            lower(expect->second).find("100-continue") != std::string::npos &&
            !send_all(socket, "HTTP/1.1 100 Continue\r\n\r\n")) return std::nullopt;
    }
    while (bytes.size() - body_at < content_length) {
        const auto count = recv(socket, buffer.data(), static_cast<int>(buffer.size()), 0);
        if (count <= 0) return std::nullopt;
        bytes.append(buffer.data(), static_cast<std::size_t>(count));
    }
    request.body.assign(bytes.data() + body_at, content_length);
    return request;
}

std::string http_response(int status, std::string_view reason,
    std::string_view content_type, std::string_view body,
    std::string_view extra_headers = {}) {
    return std::format("HTTP/1.1 {} {}\r\n"
        "Server: Windows/10.0 UPnP/1.0 iPhoneMirror/1.0\r\n"
        "Content-Type: {}\r\nContent-Length: {}\r\n"
        "Connection: close\r\n{}\r\n{}", status, reason, content_type,
        body.size(), extra_headers, body);
}

std::string soap_envelope(std::string_view service, std::string_view action,
    std::string_view fields) {
    return std::format("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
        "<s:Body><u:{}Response xmlns:u=\"{}\">{}</u:{}Response></s:Body>"
        "</s:Envelope>", action, service, fields, action);
}

std::string soap_error(int code, std::string_view description) {
    return std::format("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
        "<s:Body><s:Fault><faultcode>s:Client</faultcode>"
        "<faultstring>UPnPError</faultstring><detail>"
        "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">"
        "<errorCode>{}</errorCode><errorDescription>{}</errorDescription>"
        "</UPnPError></detail></s:Fault></s:Body></s:Envelope>",
        code, xml_escape(description));
}

struct ScpdArgument {
    std::string_view name;
    std::string_view direction;
    std::string_view state;
};

std::string scpd_action(std::string_view name,
    std::initializer_list<ScpdArgument> arguments) {
    auto result = std::format("<action><name>{}</name>", name);
    if (!arguments.size()) return result + "</action>";
    result += "<argumentList>";
    for (const auto& argument : arguments)
        result += std::format("<argument><name>{}</name><direction>{}</direction>"
            "<relatedStateVariable>{}</relatedStateVariable></argument>",
            argument.name, argument.direction, argument.state);
    return result + "</argumentList></action>";
}

std::string scpd_state(std::string_view name, std::string_view type,
    bool events = false, std::string_view constraints = {}) {
    return std::format("<stateVariable sendEvents=\"{}\"><name>{}</name>"
        "<dataType>{}</dataType>{}</stateVariable>",
        events ? "yes" : "no", name, type, constraints);
}

std::string scpd_document(std::initializer_list<std::string> actions,
    std::initializer_list<std::string> states) {
    std::string result = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">"
        "<specVersion><major>1</major><minor>0</minor></specVersion><actionList>";
    for (const auto& action : actions) result += action;
    result += "</actionList><serviceStateTable>";
    for (const auto& state : states) result += state;
    return result + "</serviceStateTable></scpd>";
}

std::vector<std::string> local_ipv4_addresses() {
    std::vector<std::string> result;
    char host[256]{};
    if (gethostname(host, sizeof(host)) != 0) return result;
    addrinfo hints{};
    hints.ai_family = AF_INET;
    addrinfo* addresses{};
    if (getaddrinfo(host, nullptr, &hints, &addresses) != 0) return result;
    for (auto* entry = addresses; entry; entry = entry->ai_next) {
        char text[INET_ADDRSTRLEN]{};
        const auto* address = reinterpret_cast<const sockaddr_in*>(entry->ai_addr);
        if (!inet_ntop(AF_INET, &address->sin_addr, text, sizeof(text))) continue;
        std::string value(text);
        if (value != "127.0.0.1" &&
            std::ranges::find(result, value) == result.end()) result.push_back(std::move(value));
    }
    freeaddrinfo(addresses);
    return result;
}

std::string routed_local_address(const sockaddr_in& remote) {
    const auto socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (socket == INVALID_SOCKET) return {};
    sockaddr_in destination = remote;
    destination.sin_port = htons(9);
    std::string result;
    if (connect(socket, reinterpret_cast<const sockaddr*>(&destination),
            sizeof(destination)) == 0) {
        sockaddr_in local{};
        int length = sizeof(local);
        char text[INET_ADDRSTRLEN]{};
        if (getsockname(socket, reinterpret_cast<sockaddr*>(&local), &length) == 0 &&
            inet_ntop(AF_INET, &local.sin_addr, text, sizeof(text))) result = text;
    }
    closesocket(socket);
    return result;
}

} // namespace

struct DlnaRenderer::Impl {
    std::string name;
    std::string uuid;
    std::uint16_t http_port{};
    std::uint16_t ssdp_port{SsdpPort};
    Callbacks callbacks;
    SOCKET http_socket{INVALID_SOCKET};
    SOCKET ssdp_socket{INVALID_SOCKET};
    std::atomic_bool stopping{};
    std::thread http_thread;
    std::thread ssdp_thread;
    bool winsock_started{};
    std::mutex state_mutex;
    std::string media_uri;
    double media_start{};
    float volume{1.0F};
    bool muted{};
    std::string transport_state{"STOPPED"};

    void log(std::string_view message) const {
        if (callbacks.log) callbacks.log(message);
    }

    std::string description() const {
        return std::format("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\" "
            "xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\">"
            "<specVersion><major>1</major><minor>0</minor></specVersion><device>"
            "<deviceType>{}</deviceType><friendlyName>{}</friendlyName>"
            "<manufacturer>iPhoneMirror</manufacturer>"
            "<manufacturerURL>https://github.com/</manufacturerURL>"
            "<modelDescription>iPhoneMirror video application receiver</modelDescription>"
            "<modelName>iPhoneMirror DLNA Renderer</modelName>"
            "<modelNumber>1</modelNumber><serialNumber>1</serialNumber>"
            "<UDN>{}</UDN><dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>"
            "<serviceList>"
            "<service><serviceType>{}</serviceType>"
            "<serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>"
            "<SCPDURL>/dlna/avtransport.xml</SCPDURL>"
            "<controlURL>/dlna/control/avtransport</controlURL>"
            "<eventSubURL>/dlna/event/avtransport</eventSubURL></service>"
            "<service><serviceType>{}</serviceType>"
            "<serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>"
            "<SCPDURL>/dlna/connectionmanager.xml</SCPDURL>"
            "<controlURL>/dlna/control/connectionmanager</controlURL>"
            "<eventSubURL>/dlna/event/connectionmanager</eventSubURL></service>"
            "<service><serviceType>{}</serviceType>"
            "<serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>"
            "<SCPDURL>/dlna/renderingcontrol.xml</SCPDURL>"
            "<controlURL>/dlna/control/renderingcontrol</controlURL>"
            "<eventSubURL>/dlna/event/renderingcontrol</eventSubURL></service>"
            "</serviceList></device></root>", DeviceType, xml_escape(name), uuid,
            AvTransportType, ConnectionManagerType, RenderingControlType);
    }

    static std::string_view avtransport_scpd() {
        static const auto document = scpd_document({
            scpd_action("SetAVTransportURI", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"CurrentURI", "in", "AVTransportURI"},
                {"CurrentURIMetaData", "in", "AVTransportURIMetaData"}}),
            scpd_action("Play", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Speed", "in", "TransportPlaySpeed"}}),
            scpd_action("Pause", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
            scpd_action("Stop", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
            scpd_action("Seek", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Unit", "in", "A_ARG_TYPE_SeekMode"},
                {"Target", "in", "A_ARG_TYPE_SeekTarget"}}),
            scpd_action("GetTransportInfo", {
                {"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"CurrentTransportState", "out", "TransportState"},
                {"CurrentTransportStatus", "out", "TransportStatus"},
                {"CurrentSpeed", "out", "TransportPlaySpeed"}}),
            scpd_action("GetPositionInfo", {
                {"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Track", "out", "CurrentTrack"},
                {"TrackDuration", "out", "CurrentTrackDuration"},
                {"TrackMetaData", "out", "CurrentTrackMetaData"},
                {"TrackURI", "out", "CurrentTrackURI"},
                {"RelTime", "out", "RelativeTimePosition"},
                {"AbsTime", "out", "AbsoluteTimePosition"},
                {"RelCount", "out", "RelativeCounterPosition"},
                {"AbsCount", "out", "AbsoluteCounterPosition"}}),
            scpd_action("GetMediaInfo", {
                {"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"NrTracks", "out", "NumberOfTracks"},
                {"MediaDuration", "out", "CurrentMediaDuration"},
                {"CurrentURI", "out", "AVTransportURI"},
                {"CurrentURIMetaData", "out", "AVTransportURIMetaData"},
                {"NextURI", "out", "NextAVTransportURI"},
                {"NextURIMetaData", "out", "NextAVTransportURIMetaData"},
                {"PlayMedium", "out", "PlaybackStorageMedium"},
                {"RecordMedium", "out", "RecordStorageMedium"},
                {"WriteStatus", "out", "RecordMediumWriteStatus"}}),
            scpd_action("GetCurrentTransportActions", {
                {"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Actions", "out", "CurrentTransportActions"}}),
        }, {
            scpd_state("A_ARG_TYPE_InstanceID", "ui4"),
            scpd_state("AVTransportURI", "uri"),
            scpd_state("AVTransportURIMetaData", "string"),
            scpd_state("TransportPlaySpeed", "string", false,
                "<allowedValueList><allowedValue>1</allowedValue></allowedValueList>"),
            scpd_state("A_ARG_TYPE_SeekMode", "string", false,
                "<allowedValueList><allowedValue>REL_TIME</allowedValue>"
                "<allowedValue>TRACK_NR</allowedValue></allowedValueList>"),
            scpd_state("A_ARG_TYPE_SeekTarget", "string"),
            scpd_state("TransportState", "string", false,
                "<allowedValueList><allowedValue>STOPPED</allowedValue>"
                "<allowedValue>PAUSED_PLAYBACK</allowedValue><allowedValue>PLAYING</allowedValue>"
                "<allowedValue>TRANSITIONING</allowedValue>"
                "<allowedValue>NO_MEDIA_PRESENT</allowedValue></allowedValueList>"),
            scpd_state("TransportStatus", "string", false,
                "<allowedValueList><allowedValue>OK</allowedValue>"
                "<allowedValue>ERROR_OCCURRED</allowedValue></allowedValueList>"),
            scpd_state("CurrentTrack", "ui4"),
            scpd_state("CurrentTrackDuration", "string"),
            scpd_state("CurrentTrackMetaData", "string"),
            scpd_state("CurrentTrackURI", "uri"),
            scpd_state("RelativeTimePosition", "string"),
            scpd_state("AbsoluteTimePosition", "string"),
            scpd_state("RelativeCounterPosition", "i4"),
            scpd_state("AbsoluteCounterPosition", "i4"),
            scpd_state("NumberOfTracks", "ui4"),
            scpd_state("CurrentMediaDuration", "string"),
            scpd_state("NextAVTransportURI", "uri"),
            scpd_state("NextAVTransportURIMetaData", "string"),
            scpd_state("PlaybackStorageMedium", "string"),
            scpd_state("RecordStorageMedium", "string"),
            scpd_state("RecordMediumWriteStatus", "string"),
            scpd_state("CurrentTransportActions", "string"),
            scpd_state("LastChange", "string", true),
        });
        return document;
    }

    static std::string_view connectionmanager_scpd() {
        static const auto document = scpd_document({
            scpd_action("GetProtocolInfo", {{"Source", "out", "SourceProtocolInfo"},
                {"Sink", "out", "SinkProtocolInfo"}}),
            scpd_action("GetCurrentConnectionIDs",
                {{"ConnectionIDs", "out", "CurrentConnectionIDs"}}),
            scpd_action("GetCurrentConnectionInfo", {
                {"ConnectionID", "in", "A_ARG_TYPE_ConnectionID"},
                {"RcsID", "out", "A_ARG_TYPE_RcsID"},
                {"AVTransportID", "out", "A_ARG_TYPE_AVTransportID"},
                {"ProtocolInfo", "out", "A_ARG_TYPE_ProtocolInfo"},
                {"PeerConnectionManager", "out", "A_ARG_TYPE_ConnectionManager"},
                {"PeerConnectionID", "out", "A_ARG_TYPE_ConnectionID"},
                {"Direction", "out", "A_ARG_TYPE_Direction"},
                {"Status", "out", "A_ARG_TYPE_ConnectionStatus"}}),
        }, {
            scpd_state("SourceProtocolInfo", "string", true),
            scpd_state("SinkProtocolInfo", "string", true),
            scpd_state("CurrentConnectionIDs", "string", true),
            scpd_state("A_ARG_TYPE_ConnectionID", "i4"),
            scpd_state("A_ARG_TYPE_RcsID", "i4"),
            scpd_state("A_ARG_TYPE_AVTransportID", "i4"),
            scpd_state("A_ARG_TYPE_ProtocolInfo", "string"),
            scpd_state("A_ARG_TYPE_ConnectionManager", "string"),
            scpd_state("A_ARG_TYPE_Direction", "string", false,
                "<allowedValueList><allowedValue>Input</allowedValue>"
                "<allowedValue>Output</allowedValue></allowedValueList>"),
            scpd_state("A_ARG_TYPE_ConnectionStatus", "string", false,
                "<allowedValueList><allowedValue>OK</allowedValue>"
                "<allowedValue>ContentFormatMismatch</allowedValue>"
                "<allowedValue>InsufficientBandwidth</allowedValue>"
                "<allowedValue>UnreliableChannel</allowedValue>"
                "<allowedValue>Unknown</allowedValue></allowedValueList>"),
        });
        return document;
    }

    static std::string_view renderingcontrol_scpd() {
        static const auto document = scpd_document({
            scpd_action("GetVolume", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Channel", "in", "A_ARG_TYPE_Channel"},
                {"CurrentVolume", "out", "Volume"}}),
            scpd_action("SetVolume", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Channel", "in", "A_ARG_TYPE_Channel"},
                {"DesiredVolume", "in", "Volume"}}),
            scpd_action("GetMute", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Channel", "in", "A_ARG_TYPE_Channel"},
                {"CurrentMute", "out", "Mute"}}),
            scpd_action("SetMute", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Channel", "in", "A_ARG_TYPE_Channel"},
                {"DesiredMute", "in", "Mute"}}),
        }, {
            scpd_state("A_ARG_TYPE_InstanceID", "ui4"),
            scpd_state("A_ARG_TYPE_Channel", "string", false,
                "<allowedValueList><allowedValue>Master</allowedValue></allowedValueList>"),
            scpd_state("Volume", "ui2", false,
                "<allowedValueRange><minimum>0</minimum><maximum>100</maximum>"
                "<step>1</step></allowedValueRange>"),
            scpd_state("Mute", "boolean"),
            scpd_state("LastChange", "string", true),
        });
        return document;
    }

    std::pair<std::string_view, std::string> handle_soap(const HttpRequest& request) {
        const auto soap_header = request.headers.find("soapaction");
        std::string action;
        if (soap_header != request.headers.end()) {
            auto value = soap_header->second;
            if (!value.empty() && value.front() == '\"') value.erase(value.begin());
            if (!value.empty() && value.back() == '\"') value.pop_back();
            const auto separator = value.rfind('#');
            if (separator != std::string::npos) action = value.substr(separator + 1);
        }
        if (action.empty()) {
            for (const auto candidate : {"SetAVTransportURI", "Play", "Pause", "Stop",
                    "Seek", "GetTransportInfo", "GetPositionInfo", "GetMediaInfo",
                    "GetCurrentTransportActions",
                    "GetProtocolInfo", "GetCurrentConnectionIDs",
                    "GetCurrentConnectionInfo", "GetVolume", "SetVolume",
                    "GetMute", "SetMute"}) {
                if (request.body.find(std::format(":{}", candidate)) != std::string::npos ||
                    request.body.find(std::format("<{}", candidate)) != std::string::npos) {
                    action = candidate;
                    break;
                }
            }
        }

        const auto avtransport = request.path.find("avtransport") != std::string::npos;
        const auto connection = request.path.find("connectionmanager") != std::string::npos;
        const auto rendering = request.path.find("renderingcontrol") != std::string::npos;
        const auto service = avtransport ? AvTransportType :
            connection ? ConnectionManagerType : RenderingControlType;

        if (action == "SetAVTransportURI" && avtransport) {
            const auto uri = xml_value(request.body, "CurrentURI");
            if (!uri || uri->size() > 16U * 1024U ||
                (!uri->starts_with("http://") && !uri->starts_with("https://")))
                return {service, soap_error(714, "Illegal MIME-type")};
            {
                std::scoped_lock lock(state_mutex);
                media_uri = *uri;
                media_start = 0;
                transport_state = "STOPPED";
            }
            log(std::format("dlna SetAVTransportURI url_bytes={}", uri->size()));
        }
        else if (action == "Play" && avtransport) {
            std::string uri;
            double start{};
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
                start = media_start;
                transport_state = "PLAYING";
            }
            if (uri.empty()) return {service, soap_error(701, "Transition not available")};
            if (callbacks.play) callbacks.play(uri, start);
            log(std::format("dlna Play start={:.3f}", start));
        }
        else if (action == "Stop" && avtransport) {
            {
                std::scoped_lock lock(state_mutex);
                transport_state = "STOPPED";
            }
            if (callbacks.stop) callbacks.stop();
            log("dlna Stop");
        }
        else if (action == "Pause" && avtransport) {
            std::scoped_lock lock(state_mutex);
            transport_state = "PAUSED_PLAYBACK";
        }
        else if (action == "Seek" && avtransport) {
            const auto target = xml_value(request.body, "Target");
            std::string uri;
            if (target) {
                std::scoped_lock lock(state_mutex);
                media_start = parse_dlna_time(*target);
                uri = media_uri;
            }
            if (!uri.empty() && callbacks.play) callbacks.play(uri, media_start);
        }
        else if (action == "GetTransportInfo" && avtransport) {
            std::string state;
            {
                std::scoped_lock lock(state_mutex);
                state = transport_state;
            }
            return {service, soap_envelope(service, action,
                std::format("<CurrentTransportState>{}</CurrentTransportState>"
                    "<CurrentTransportStatus>OK</CurrentTransportStatus>"
                    "<CurrentSpeed>1</CurrentSpeed>", state))};
        }
        else if (action == "GetPositionInfo" && avtransport) {
            double duration{}, position{}, rate{};
            if (callbacks.get_play_info) callbacks.get_play_info(&duration, &position, &rate);
            std::string uri;
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
            }
            return {service, soap_envelope(service, action, std::format(
                "<Track>1</Track><TrackDuration>{}</TrackDuration>"
                "<TrackMetaData></TrackMetaData><TrackURI>{}</TrackURI>"
                "<RelTime>{}</RelTime><AbsTime>{}</AbsTime>"
                "<RelCount>2147483647</RelCount><AbsCount>2147483647</AbsCount>",
                format_dlna_time(duration), xml_escape(uri), format_dlna_time(position),
                format_dlna_time(position)))};
        }
        else if (action == "GetMediaInfo" && avtransport) {
            double duration{};
            if (callbacks.get_play_info) callbacks.get_play_info(&duration, nullptr, nullptr);
            std::string uri;
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
            }
            return {service, soap_envelope(service, action, std::format(
                "<NrTracks>1</NrTracks><MediaDuration>{}</MediaDuration>"
                "<CurrentURI>{}</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>"
                "<NextURI></NextURI><NextURIMetaData></NextURIMetaData>"
                "<PlayMedium>NETWORK</PlayMedium><RecordMedium>NOT_IMPLEMENTED</RecordMedium>"
                "<WriteStatus>NOT_IMPLEMENTED</WriteStatus>",
                format_dlna_time(duration), xml_escape(uri)))};
        }
        else if (action == "GetCurrentTransportActions" && avtransport) {
            return {service, soap_envelope(service, action,
                "<Actions>Play,Pause,Stop,Seek</Actions>")};
        }
        else if (action == "GetProtocolInfo" && connection) {
            constexpr std::string_view sink =
                "http-get:*:video/mp4:*,http-get:*:video/mpeg:*,"
                "http-get:*:video/x-matroska:*,http-get:*:application/vnd.apple.mpegurl:*,"
                "http-get:*:application/x-mpegURL:*,http-get:*:audio/mpeg:*,http-get:*:*:*";
            return {service, soap_envelope(service, action,
                std::format("<Source></Source><Sink>{}</Sink>", sink))};
        }
        else if (action == "GetCurrentConnectionIDs" && connection) {
            return {service, soap_envelope(service, action,
                "<ConnectionIDs>0</ConnectionIDs>")};
        }
        else if (action == "GetCurrentConnectionInfo" && connection) {
            return {service, soap_envelope(service, action,
                "<RcsID>0</RcsID><AVTransportID>0</AVTransportID>"
                "<ProtocolInfo>http-get:*:*:*</ProtocolInfo><PeerConnectionManager></PeerConnectionManager>"
                "<PeerConnectionID>-1</PeerConnectionID><Direction>Input</Direction>"
                "<Status>OK</Status>")};
        }
        else if (action == "SetVolume" && rendering) {
            if (const auto desired = xml_value(request.body, "DesiredVolume")) {
                unsigned value{};
                const auto [end, error] = std::from_chars(
                    desired->data(), desired->data() + desired->size(), value);
                if (error == std::errc{} && end == desired->data() + desired->size()) {
                    std::scoped_lock lock(state_mutex);
                    volume = std::min(value, 100U) / 100.0F;
                }
            }
        }
        else if (action == "GetVolume" && rendering) {
            std::scoped_lock lock(state_mutex);
            return {service, soap_envelope(service, action,
                std::format("<CurrentVolume>{}</CurrentVolume>",
                    static_cast<unsigned>(volume * 100)))};
        }
        else if (action == "SetMute" && rendering) {
            if (const auto desired = xml_value(request.body, "DesiredMute")) {
                std::scoped_lock lock(state_mutex);
                muted = *desired == "1" || lower(*desired) == "true";
            }
        }
        else if (action == "GetMute" && rendering) {
            std::scoped_lock lock(state_mutex);
            return {service, soap_envelope(service, action,
                std::format("<CurrentMute>{}</CurrentMute>", muted ? 1 : 0))};
        }
        else {
            return {service, soap_error(401, "Invalid Action")};
        }
        return {service, soap_envelope(service, action, "")};
    }

    void handle_http(SOCKET client) {
        const auto request = read_request(client);
        if (!request) return;
        log(std::format("dlna http method={} path={}", request->method, request->path));
        std::string response;
        if (request->method == "GET" &&
            (request->path == "/dlna/device.xml" || request->path == "/device.xml")) {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"", description());
        }
        else if (request->method == "GET" && request->path == "/dlna/avtransport.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                avtransport_scpd());
        }
        else if (request->method == "GET" &&
            request->path == "/dlna/connectionmanager.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                connectionmanager_scpd());
        }
        else if (request->method == "GET" &&
            request->path == "/dlna/renderingcontrol.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                renderingcontrol_scpd());
        }
        else if (request->method == "POST" &&
            request->path.starts_with("/dlna/control/")) {
            const auto [_, body] = handle_soap(*request);
            const auto error = body.find("<s:Fault>") != std::string::npos;
            response = http_response(error ? 500 : 200, error ? "Internal Server Error" : "OK",
                "text/xml; charset=\"utf-8\"", body,
                "EXT:\r\n");
        }
        else if (request->method == "SUBSCRIBE" &&
            request->path.starts_with("/dlna/event/")) {
            response = http_response(200, "OK", "text/plain", "",
                std::format("SID: {}\r\nTIMEOUT: Second-1800\r\n", uuid));
        }
        else if (request->method == "UNSUBSCRIBE" &&
            request->path.starts_with("/dlna/event/")) {
            response = http_response(200, "OK", "text/plain", "");
        }
        else response = http_response(404, "Not Found", "text/plain", "Not Found");
        send_all(client, response);
    }

    void http_loop() {
        while (!stopping.load(std::memory_order_acquire)) {
            sockaddr_storage remote{};
            int length = sizeof(remote);
            const auto client = accept(http_socket, reinterpret_cast<sockaddr*>(&remote), &length);
            if (client == INVALID_SOCKET) {
                if (!stopping.load(std::memory_order_acquire))
                    std::this_thread::sleep_for(std::chrono::milliseconds(20));
                continue;
            }
            // A socket accepted from a nonblocking listener can inherit that
            // mode on Windows. SOAP clients are allowed to split headers and
            // bodies across TCP packets, so switch the connected socket back
            // to blocking I/O before the bounded request reader starts.
            u_long blocking = 0;
            if (ioctlsocket(client, FIONBIO, &blocking) != 0) {
                closesocket(client);
                continue;
            }
            DWORD timeout = 5000;
            setsockopt(client, SOL_SOCKET, SO_RCVTIMEO,
                reinterpret_cast<const char*>(&timeout), sizeof(timeout));
            handle_http(client);
            shutdown(client, SD_BOTH);
            closesocket(client);
        }
    }

    std::string location(std::string_view address) const {
        return std::format("http://{}:{}/dlna/device.xml", address, http_port);
    }

    std::vector<std::pair<std::string, std::string>> advertised_services() const {
        return {{"upnp:rootdevice", std::format("{}::upnp:rootdevice", uuid)},
            {uuid, uuid}, {std::string(DeviceType), std::format("{}::{}", uuid, DeviceType)},
            {std::string(AvTransportType), std::format("{}::{}", uuid, AvTransportType)},
            {std::string(ConnectionManagerType),
                std::format("{}::{}", uuid, ConnectionManagerType)},
            {std::string(RenderingControlType),
                std::format("{}::{}", uuid, RenderingControlType)}};
    }

    void send_search_response(const sockaddr_in& remote, std::string_view st,
        std::string_view usn) {
        auto address = routed_local_address(remote);
        if (address.empty()) return;
        const auto message = std::format("HTTP/1.1 200 OK\r\n"
            "CACHE-CONTROL: max-age=1800\r\nDATE:\r\nEXT:\r\n"
            "LOCATION: {}\r\nSERVER: Windows/10.0 UPnP/1.0 iPhoneMirror/1.0\r\n"
            "ST: {}\r\nUSN: {}\r\nBOOTID.UPNP.ORG: 1\r\n"
            "CONFIGID.UPNP.ORG: 1\r\n\r\n", location(address), st, usn);
        sendto(ssdp_socket, message.data(), static_cast<int>(message.size()), 0,
            reinterpret_cast<const sockaddr*>(&remote), sizeof(remote));
    }

    void send_notify(bool alive) {
        sockaddr_in target{};
        target.sin_family = AF_INET;
        target.sin_port = htons(ssdp_port);
        if (inet_pton(AF_INET, MulticastAddress.data(), &target.sin_addr) != 1) return;
        for (const auto& address : local_ipv4_addresses()) {
            in_addr local{};
            if (inet_pton(AF_INET, address.c_str(), &local) != 1) continue;
            setsockopt(ssdp_socket, IPPROTO_IP, IP_MULTICAST_IF,
                reinterpret_cast<const char*>(&local), sizeof(local));
            for (const auto& [nt, usn] : advertised_services()) {
                const auto message = alive
                    ? std::format("NOTIFY * HTTP/1.1\r\nHOST: {}:{}\r\n"
                        "CACHE-CONTROL: max-age=1800\r\nLOCATION: {}\r\n"
                        "NT: {}\r\nNTS: ssdp:alive\r\nSERVER: Windows/10.0 UPnP/1.0 iPhoneMirror/1.0\r\n"
                        "USN: {}\r\nBOOTID.UPNP.ORG: 1\r\nCONFIGID.UPNP.ORG: 1\r\n\r\n",
                        MulticastAddress, ssdp_port, location(address), nt, usn)
                    : std::format("NOTIFY * HTTP/1.1\r\nHOST: {}:{}\r\n"
                        "NT: {}\r\nNTS: ssdp:byebye\r\nUSN: {}\r\n"
                        "BOOTID.UPNP.ORG: 1\r\nCONFIGID.UPNP.ORG: 1\r\n\r\n",
                        MulticastAddress, ssdp_port, nt, usn);
                sendto(ssdp_socket, message.data(), static_cast<int>(message.size()), 0,
                    reinterpret_cast<const sockaddr*>(&target), sizeof(target));
            }
        }
    }

    void ssdp_loop() {
        send_notify(true);
        auto next_announce = std::chrono::steady_clock::now() + std::chrono::minutes(5);
        while (!stopping.load(std::memory_order_acquire)) {
            sockaddr_in remote{};
            int remote_length = sizeof(remote);
            std::array<char, 8192> bytes{};
            const auto received = recvfrom(ssdp_socket, bytes.data(),
                static_cast<int>(bytes.size() - 1), 0,
                reinterpret_cast<sockaddr*>(&remote), &remote_length);
            if (received > 0) {
                const std::string_view message(bytes.data(), static_cast<std::size_t>(received));
                if (lower(message.substr(0, std::min<std::size_t>(message.size(), 16)))
                        .starts_with("m-search ") &&
                    lower(message).find("ssdp:discover") != std::string::npos) {
                    std::string st = "ssdp:all";
                    std::size_t at{};
                    while ((at = message.find('\n', at)) != std::string_view::npos) {
                        ++at;
                        if (lower(message.substr(at, std::min<std::size_t>(3,
                                message.size() - at))) == "st:") {
                            const auto end = message.find('\n', at);
                            st = trim(message.substr(at + 3,
                                (end == std::string_view::npos ? message.size() : end) - at - 3));
                            break;
                        }
                    }
                    for (const auto& [type, usn] : advertised_services()) {
                        if (lower(st) == "ssdp:all" || lower(st) == lower(type))
                            send_search_response(remote, type, usn);
                    }
                    log(std::format("dlna ssdp search st={} responses_sent", st));
                }
            }
            else if (WSAGetLastError() == WSAEWOULDBLOCK) {
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
            if (std::chrono::steady_clock::now() >= next_announce) {
                send_notify(true);
                next_announce = std::chrono::steady_clock::now() + std::chrono::minutes(5);
            }
        }
        send_notify(false);
    }
};

DlnaRenderer::DlnaRenderer() : impl_(std::make_unique<Impl>()) {}
DlnaRenderer::~DlnaRenderer() { stop(); }

bool DlnaRenderer::start(std::string friendly_name, std::string uuid,
    std::uint16_t http_port, std::uint16_t ssdp_port, Callbacks callbacks) {
    stop();
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return false;
    impl_->winsock_started = true;
    impl_->name = std::move(friendly_name);
    impl_->uuid = std::move(uuid);
    impl_->http_port = http_port;
    impl_->ssdp_port = ssdp_port;
    impl_->callbacks = std::move(callbacks);
    impl_->stopping.store(false, std::memory_order_release);

    impl_->http_socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (impl_->http_socket == INVALID_SOCKET) return false;
    BOOL exclusive = TRUE;
    setsockopt(impl_->http_socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
        reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
    sockaddr_in http_address{AF_INET, htons(http_port), {.S_un = {.S_addr = INADDR_ANY}}};
    if (bind(impl_->http_socket, reinterpret_cast<const sockaddr*>(&http_address),
            sizeof(http_address)) != 0 || listen(impl_->http_socket, 16) != 0) {
        stop();
        return false;
    }
    u_long nonblocking = 1;
    if (ioctlsocket(impl_->http_socket, FIONBIO, &nonblocking) != 0) {
        stop();
        return false;
    }

    impl_->ssdp_socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (impl_->ssdp_socket == INVALID_SOCKET) {
        stop();
        return false;
    }
    BOOL reuse = TRUE;
    setsockopt(impl_->ssdp_socket, SOL_SOCKET, SO_REUSEADDR,
        reinterpret_cast<const char*>(&reuse), sizeof(reuse));
    sockaddr_in ssdp_address{AF_INET, htons(ssdp_port), {.S_un = {.S_addr = INADDR_ANY}}};
    if (bind(impl_->ssdp_socket, reinterpret_cast<const sockaddr*>(&ssdp_address),
            sizeof(ssdp_address)) != 0) {
        impl_->log(std::format("dlna ssdp bind failed winsock={}", WSAGetLastError()));
        stop();
        return false;
    }
    in_addr multicast{};
    if (inet_pton(AF_INET, MulticastAddress.data(), &multicast) != 1) {
        stop();
        return false;
    }
    auto interfaces = local_ipv4_addresses();
    interfaces.emplace_back("127.0.0.1");
    bool joined{};
    for (const auto& address : interfaces) {
        ip_mreq membership{.imr_multiaddr = multicast};
        if (inet_pton(AF_INET, address.c_str(), &membership.imr_interface) != 1)
            continue;
        if (setsockopt(impl_->ssdp_socket, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                reinterpret_cast<const char*>(&membership), sizeof(membership)) == 0) {
            joined = true;
            impl_->log(std::format("dlna multicast joined interface={}", address));
        }
        else impl_->log(std::format("dlna multicast join failed interface={} winsock={}",
            address, WSAGetLastError()));
    }
    if (!joined) {
        stop();
        return false;
    }
    u_long ssdp_nonblocking = 1;
    if (ioctlsocket(impl_->ssdp_socket, FIONBIO, &ssdp_nonblocking) != 0) {
        stop();
        return false;
    }
    impl_->http_thread = std::thread([this] { impl_->http_loop(); });
    impl_->ssdp_thread = std::thread([this] { impl_->ssdp_loop(); });
    impl_->log(std::format("dlna renderer ready name={} uuid={} http_port={} ssdp_port={}",
        impl_->name, impl_->uuid, impl_->http_port, impl_->ssdp_port));
    return true;
}

void DlnaRenderer::stop() noexcept {
    if (!impl_) return;
    impl_->stopping.store(true, std::memory_order_release);
    if (impl_->http_socket != INVALID_SOCKET) {
        shutdown(impl_->http_socket, SD_BOTH);
        closesocket(impl_->http_socket);
        impl_->http_socket = INVALID_SOCKET;
    }
    if (impl_->http_thread.joinable()) impl_->http_thread.join();

    // On Windows, closesocket can block while another thread is in recvfrom.
    // The UDP socket is nonblocking, so let the receive loop observe stopping,
    // publish ssdp:byebye, and exit before closing it here.
    if (impl_->ssdp_thread.joinable()) impl_->ssdp_thread.join();
    if (impl_->ssdp_socket != INVALID_SOCKET) {
        closesocket(impl_->ssdp_socket);
        impl_->ssdp_socket = INVALID_SOCKET;
    }
    if (impl_->winsock_started) {
        WSACleanup();
        impl_->winsock_started = false;
    }
}

} // namespace iPhoneMirror::wireless
