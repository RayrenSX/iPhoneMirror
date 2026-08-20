// SPDX-License-Identifier: GPL-3.0-only

#include "DlnaRenderer.h"
#include "HttpUrl.h"

#include <WinSock2.h>
#include <WS2tcpip.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cctype>
#include <cstring>
#include <ctime>
#include <format>
#include <initializer_list>
#include <memory>
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
    const auto is_trim_character = [](unsigned char character) {
        // Some phone DLNA stacks leave a NUL or another C0 terminator inside
        // the XML text node. Treat it like surrounding whitespace so the
        // actual HH:MM:SS payload can still be parsed.
        return character == '\0' || std::isspace(character);
    };
    while (!value.empty() && is_trim_character(
               static_cast<unsigned char>(value.front())))
        value.remove_prefix(1);
    while (!value.empty() && is_trim_character(
               static_cast<unsigned char>(value.back())))
        value.remove_suffix(1);
    return std::string(value);
}

std::string log_token(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto character : value) {
        const auto byte = static_cast<unsigned char>(character);
        if (byte >= 0x20 && byte < 0x7f && character != '\\') {
            result.push_back(character);
        } else {
            result += std::format("\\x{:02X}", byte);
        }
    }
    return result;
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

std::optional<std::string> xml_attribute_value(std::string_view xml,
    std::string_view element, std::string_view attribute) {
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
        const auto tag_name_end = tag.find_first_of(" \t\r\n/");
        const auto tag_name = tag.substr(0,
            tag_name_end == std::string_view::npos ? tag.size() : tag_name_end);
        const auto colon = tag_name.rfind(':');
        const auto local_name = colon == std::string_view::npos
            ? tag_name : tag_name.substr(colon + 1);
        if (local_name != element) {
            offset = tag_end + 1;
            continue;
        }
        const auto attributes = tag.substr(tag_name.size());
        std::size_t attribute_at{};
        while ((attribute_at = attributes.find(attribute, attribute_at)) !=
            std::string_view::npos) {
            const auto before = attribute_at == 0 ? '\0' : attributes[attribute_at - 1];
            const auto after = attribute_at + attribute.size() < attributes.size()
                ? attributes[attribute_at + attribute.size()] : '\0';
            if ((before == '\0' || std::isspace(static_cast<unsigned char>(before))) &&
                after == '=') {
                auto value = attributes.substr(attribute_at + attribute.size() + 1);
                while (!value.empty() && std::isspace(
                        static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
                if (value.empty()) return std::nullopt;
                const auto quote = value.front();
                if (quote != '\"' && quote != '\'') return std::nullopt;
                value.remove_prefix(1);
                const auto end = value.find(quote);
                if (end == std::string_view::npos) return std::nullopt;
                return xml_unescape(std::string(value.substr(0, end)));
            }
            attribute_at += attribute.size();
        }
        offset = tag_end + 1;
    }
    return std::nullopt;
}

std::optional<double> parse_dlna_time(std::string_view value) noexcept;

std::optional<double> metadata_duration(std::string_view metadata) noexcept {
    try {
        if (const auto value = xml_attribute_value(metadata, "res", "duration")) {
            if (const auto duration = parse_dlna_time(*value)) return duration;
        }
        // Some senders put the duration in a DIDL child element instead of
        // the standard res attribute. Accept both forms so the controller
        // receives the programme duration even when the media backend only
        // exposes one HLS segment.
        if (const auto value = xml_value(metadata, "duration")) {
            if (const auto duration = parse_dlna_time(*value)) return duration;
            try {
                std::size_t consumed{};
                const auto seconds = std::stod(*value, &consumed);
                if (consumed == value->size() && std::isfinite(seconds) &&
                    seconds > 0) return seconds;
            } catch (...) { }
        }
        return std::nullopt;
    } catch (...) {
        return std::nullopt;
    }
}

bool is_hls_uri(std::string_view uri) noexcept {
    const auto normalized = lower(uri);
    const auto query = normalized.find('?');
    const auto path = std::string_view(normalized).substr(0, query);
    return path.ends_with(".m3u8") || path.ends_with(".m3u") ||
        (query != std::string_view::npos &&
            std::string_view(normalized).substr(query).find("m3u8") !=
                std::string_view::npos);
}

double reported_media_duration(std::string_view uri, std::string_view metadata,
    double callback_duration) noexcept {
    if (const auto duration = metadata_duration(metadata)) return *duration;
    // A MediaElement-backed HLS renderer may only know the active segment.
    // Platinum reports zero for this case; never expose that segment length as
    // the duration of the program to the controller.
    if (is_hls_uri(uri)) return 0;
    return std::isfinite(callback_duration) && callback_duration > 0
        ? callback_duration : 0;
}

std::optional<double> parse_dlna_time(std::string_view value) noexcept {
    // Controllers are inconsistent about whitespace and occasionally include
    // an explicit positive sign. Normalize those harmless variations before
    // parsing the DLNA HH:MM:SS value.
    const auto normalized = trim(value);
    value = normalized;
    if (value.starts_with('+')) value.remove_prefix(1);
    if (value.empty() || value.starts_with('-')) return std::nullopt;
    unsigned hours{}, minutes{};
    double seconds{};
    const auto first = value.find(':');
    const auto second = first == std::string_view::npos ? first : value.find(':', first + 1);
    if (first == std::string_view::npos || second == std::string_view::npos)
        return std::nullopt;
    const auto parse_unsigned = [](std::string_view text, unsigned& output) {
        const auto [end, error] = std::from_chars(
            text.data(), text.data() + text.size(), output);
        return error == std::errc{} && end == text.data() + text.size();
    };
    if (!parse_unsigned(value.substr(0, first), hours) ||
        !parse_unsigned(value.substr(first + 1, second - first - 1), minutes))
        return std::nullopt;
    try {
        auto seconds_text = std::string(value.substr(second + 1));
        // A few locale-aware DLNA controllers serialize the fractional part
        // with a comma even though REL_TIME is otherwise ASCII.
        std::ranges::replace(seconds_text, ',', '.');
        std::size_t consumed{};
        seconds = std::stod(seconds_text, &consumed);
        if (consumed != seconds_text.size()) return std::nullopt;
    } catch (...) { return std::nullopt; }
    if (!std::isfinite(seconds) || seconds < 0)
        return std::nullopt;
    // iQIYI serializes REL_TIME as 00:00:<total-seconds> instead of carrying
    // values above 59 into the minute/hour fields. Normalize that legal-in-
    // practice variant together with ordinary HH:MM:SS values.
    const auto result = static_cast<double>(hours) * 3600.0 +
        static_cast<double>(minutes) * 60.0 + seconds;
    if (!std::isfinite(result) || result > 7.0 * 24.0 * 60.0 * 60.0)
        return std::nullopt;
    return result;
}

std::string format_dlna_time(double seconds) {
    constexpr double maximum = 7.0 * 24.0 * 60.0 * 60.0;
    seconds = std::isfinite(seconds) ? std::clamp(seconds, 0.0, maximum) : 0.0;
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

struct EventEndpoint {
    std::string host;
    std::uint16_t port{80};
    std::string path{"/"};
};

std::optional<EventEndpoint> parse_event_callback(std::string_view header) {
    const auto open = header.find('<');
    const auto close = open == std::string_view::npos
        ? open : header.find('>', open + 1);
    if (open == std::string_view::npos || close == std::string_view::npos)
        return std::nullopt;
    auto url = header.substr(open + 1, close - open - 1);
    if (!lower(url.substr(0, (std::min<std::size_t>)(url.size(), 7)))
            .starts_with("http://")) return std::nullopt;
    url.remove_prefix(7);
    const auto path_at = url.find_first_of("/?");
    auto authority = url.substr(0, path_at);
    if (authority.empty()) return std::nullopt;

    EventEndpoint result;
    if (authority.front() == '[') {
        const auto bracket = authority.find(']');
        if (bracket == std::string_view::npos || bracket == 1)
            return std::nullopt;
        result.host = std::string(authority.substr(1, bracket - 1));
        if (bracket + 1 < authority.size()) {
            if (authority[bracket + 1] != ':') return std::nullopt;
            unsigned port{};
            const auto port_text = authority.substr(bracket + 2);
            const auto [end, error] = std::from_chars(port_text.data(),
                port_text.data() + port_text.size(), port);
            if (error != std::errc{} || end != port_text.data() + port_text.size() ||
                port == 0 || port > 65535) return std::nullopt;
            result.port = static_cast<std::uint16_t>(port);
        }
    } else {
        const auto colon = authority.rfind(':');
        if (colon != std::string_view::npos) {
            unsigned port{};
            const auto port_text = authority.substr(colon + 1);
            const auto [end, error] = std::from_chars(port_text.data(),
                port_text.data() + port_text.size(), port);
            if (error != std::errc{} || end != port_text.data() + port_text.size() ||
                port == 0 || port > 65535) return std::nullopt;
            result.port = static_cast<std::uint16_t>(port);
            authority = authority.substr(0, colon);
        }
        if (authority.empty()) return std::nullopt;
        result.host = std::string(authority);
    }
    if (result.host.find_first_of("\r\n\t ") != std::string::npos)
        return std::nullopt;
    if (path_at != std::string_view::npos)
        result.path = url[path_at] == '?' ? std::string("/") + std::string(url.substr(path_at))
                                          : std::string(url.substr(path_at));
    return result;
}

std::chrono::seconds event_timeout(const HttpRequest& request) noexcept {
    const auto found = request.headers.find("timeout");
    if (found == request.headers.end()) return std::chrono::seconds(1800);
    const auto value = lower(trim(found->second));
    if (value == "second-infinite") return std::chrono::hours(24);
    constexpr std::string_view prefix = "second-";
    if (!value.starts_with(prefix)) return std::chrono::seconds(1800);
    unsigned seconds{};
    const auto text = std::string_view(value).substr(prefix.size());
    const auto [end, error] = std::from_chars(
        text.data(), text.data() + text.size(), seconds);
    if (error != std::errc{} || end != text.data() + text.size())
        return std::chrono::seconds(1800);
    return std::chrono::seconds(std::clamp(seconds, 60U, 86400U));
}

std::string http_date() {
    std::time_t now = std::time(nullptr);
    std::tm utc{};
    if (gmtime_s(&utc, &now) != 0) return "Thu, 01 Jan 1970 00:00:00 GMT";
    char buffer[64]{};
    if (std::strftime(buffer, sizeof(buffer),
            "%a, %d %b %Y %H:%M:%S GMT", &utc) == 0)
        return "Thu, 01 Jan 1970 00:00:00 GMT";
    return buffer;
}

bool wait_socket(SOCKET socket, short events, const std::atomic_bool& stopping,
    std::chrono::steady_clock::time_point deadline) noexcept {
    while (!stopping.load(std::memory_order_acquire) &&
        std::chrono::steady_clock::now() < deadline) {
        WSAPOLLFD descriptor{.fd = socket, .events = events};
        const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(
            deadline - std::chrono::steady_clock::now());
        const auto timeout = static_cast<int>(std::clamp<std::int64_t>(
            remaining.count(), 1, 100));
        const auto result = WSAPoll(&descriptor, 1, timeout);
        if (result > 0) return descriptor.revents != 0;
        if (result == SOCKET_ERROR && WSAGetLastError() != WSAEINTR) return false;
    }
    return false;
}

bool send_all(SOCKET socket, std::string_view data,
    const std::atomic_bool& stopping) noexcept {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!data.empty() && wait_socket(socket, POLLWRNORM, stopping, deadline)) {
        const auto sent = send(socket, data.data(),
            static_cast<int>(std::min<std::size_t>(data.size(), INT_MAX)), 0);
        if (sent == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) continue;
        if (sent <= 0) return false;
        data.remove_prefix(static_cast<std::size_t>(sent));
    }
    return data.empty();
}

std::optional<HttpRequest> read_request(SOCKET socket,
    const std::atomic_bool& stopping) {
    std::string bytes;
    std::array<char, 4096> buffer{};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    const auto receive = [&](char* destination, int capacity) {
        while (!stopping.load(std::memory_order_acquire) &&
            std::chrono::steady_clock::now() < deadline) {
            if (!wait_socket(socket, POLLRDNORM, stopping, deadline))
                return SOCKET_ERROR;
            const auto count = recv(socket, destination, capacity, 0);
            if (count >= 0) return count;
            const auto error = WSAGetLastError();
            if (error != WSAEWOULDBLOCK) return SOCKET_ERROR;
        }
        return SOCKET_ERROR;
    };
    while (bytes.find("\r\n\r\n") == std::string::npos && bytes.size() < 64U * 1024U) {
        const auto count = receive(buffer.data(), static_cast<int>(buffer.size()));
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
            const auto name = lower(trim(line.substr(0, separator)));
            const auto value = trim(line.substr(separator + 1));
            if (name.empty()) return std::nullopt;
            if (const auto existing = request.headers.find(name);
                existing != request.headers.end()) {
                // A request with conflicting framing headers is ambiguous and
                // must never be interpreted differently by the parser and a
                // controller or proxy. Identical Content-Length fields are
                // safe and occur with a few older DLNA controllers; repeated
                // Transfer-Encoding is rejected because list semantics would
                // otherwise be lost by the single-value header map.
                if (name == "content-length" && existing->second == value) {
                    line_at = line_end + 2;
                    continue;
                }
                return std::nullopt;
            }
            request.headers.emplace(name, value);
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
    const auto transfer = request.headers.find("transfer-encoding");
    const auto transfer_value = transfer == request.headers.end()
        ? std::string{} : lower(transfer->second);
    const bool chunked = transfer_value == "chunked";
    if (!transfer_value.empty() && !chunked) return std::nullopt;
    if (chunked && request.headers.contains("content-length"))
        return std::nullopt;
    const auto body_at = header_end + 4;
    const auto receive_more = [&] {
        const auto count = receive(buffer.data(), static_cast<int>(buffer.size()));
        if (count <= 0) return false;
        bytes.append(buffer.data(), static_cast<std::size_t>(count));
        return true;
    };
    const auto expect = request.headers.find("expect");
    const auto expect_continue = expect != request.headers.end() &&
        lower(expect->second).find("100-continue") != std::string::npos;
    if (expect_continue && bytes.size() == body_at &&
        (chunked || content_length != 0) &&
        !send_all(socket, "HTTP/1.1 100 Continue\r\n\r\n", stopping))
        return std::nullopt;
    if (chunked) {
        std::string body;
        std::size_t cursor = body_at;
        while (true) {
            std::size_t line_end{};
            while ((line_end = bytes.find("\r\n", cursor)) == std::string::npos) {
                if (bytes.size() > body_at + 1024U * 1024U || !receive_more())
                    return std::nullopt;
            }
            auto size_text = trim(std::string_view(bytes).substr(cursor,
                line_end - cursor));
            const auto extension = size_text.find(';');
            if (extension != std::string_view::npos)
                size_text.resize(extension);
            size_text = trim(size_text);
            std::uint64_t chunk_size{};
            const auto [size_end, size_error] = std::from_chars(
                size_text.data(), size_text.data() + size_text.size(),
                chunk_size, 16);
            if (size_error != std::errc{} ||
                size_end != size_text.data() + size_text.size() ||
                chunk_size > 1024U * 1024U ||
                body.size() > 1024U * 1024U - static_cast<std::size_t>(chunk_size))
                return std::nullopt;
            cursor = line_end + 2;
            if (chunk_size == 0) {
                // The last-chunk grammar ends after its size line. What
                // follows is either the final CRLF or one or more trailer
                // fields terminated by an empty line; there is no chunk-data
                // CRLF before the first trailer.
                while (bytes.size() - cursor < 2U) {
                    if (!receive_more()) return std::nullopt;
                }
                if (bytes.compare(cursor, 2, "\r\n") == 0) {
                    request.body = std::move(body);
                    return request;
                }
                while (bytes.find("\r\n\r\n", cursor) == std::string::npos) {
                    if (bytes.size() > body_at + 1024U * 1024U || !receive_more())
                        return std::nullopt;
                }
                request.body = std::move(body);
                return request;
            }
            const auto required = static_cast<std::size_t>(chunk_size) + 2U;
            while (bytes.size() - cursor < required) {
                if (!receive_more()) return std::nullopt;
            }
            body.append(bytes.data() + cursor,
                static_cast<std::size_t>(chunk_size));
            cursor += static_cast<std::size_t>(chunk_size);
            if (bytes.compare(cursor, 2, "\r\n") != 0) return std::nullopt;
            cursor += 2;
        }
    }
    while (bytes.size() - body_at < content_length) {
        if (!receive_more()) return std::nullopt;
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
    struct EventSubscription {
        std::string sid;
        std::string service;
        EventEndpoint callback;
        std::uint32_t sequence{};
        std::chrono::steady_clock::time_point expires;
    };

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
    std::vector<std::thread> client_threads;
    bool winsock_started{};
    std::mutex state_mutex;
    std::mutex http_listener_mutex;
    std::mutex client_mutex;
    std::mutex subscription_mutex;
    std::vector<EventSubscription> subscriptions;
    std::atomic_uint64_t next_subscription{};
    std::string media_uri;
    std::string media_metadata;
    std::string next_media_uri;
    std::string next_media_metadata;
    std::vector<std::string> interfaces;
    double media_start{};
    float volume{1.0F};
    bool muted{};
    std::string transport_state{"STOPPED"};
    std::atomic_uint64_t http_requests{};
    std::atomic_uint64_t http_parse_failures{};
    std::atomic_uint64_t http_responses{};
    std::atomic_uint64_t http_send_failures{};
    std::atomic_uint64_t soap_requests{};
    std::atomic_uint64_t ssdp_searches{};

    void log(std::string_view message) const noexcept {
        try {
            if (callbacks.log) callbacks.log(message);
        } catch (...) {
            // Diagnostics must not terminate the HTTP or SSDP worker.
        }
    }

    static std::string_view event_service(std::string_view path) noexcept {
        if (path.find("avtransport") != std::string_view::npos)
            return "avtransport";
        if (path.find("renderingcontrol") != std::string_view::npos)
            return "renderingcontrol";
        if (path.find("connectionmanager") != std::string_view::npos)
            return "connectionmanager";
        return {};
    }

    std::string event_body(std::string_view service) {
        if (service == "connectionmanager") {
            constexpr std::string_view sink =
                "http-get:*:video/mp4:*,http-get:*:video/mpeg:*,"
                "http-get:*:video/x-ms-wmv:*,http-get:*:application/vnd.apple.mpegurl:*,"
                "http-get:*:application/x-mpegURL:*,http-get:*:audio/mpeg:*,"
                "http-get:*:audio/mp4:*,http-get:*:video/x-matroska:*,http-get:*:*:*";
            return std::format("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">"
                "<e:property><SourceProtocolInfo></SourceProtocolInfo></e:property>"
                "<e:property><SinkProtocolInfo>{}</SinkProtocolInfo></e:property>"
                "<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>"
                "</e:propertyset>", sink);
        }

        std::string state;
        std::string uri;
        std::string metadata;
        std::string next_uri;
        std::string next_metadata;
        double start{};
        float current_volume{};
        bool current_muted{};
        {
            std::scoped_lock lock(state_mutex);
            state = transport_state;
            uri = media_uri;
            metadata = media_metadata;
            next_uri = next_media_uri;
            next_metadata = next_media_metadata;
            start = media_start;
            current_volume = volume;
            current_muted = muted;
        }
        const auto change = service == "avtransport"
            ? std::format("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\">"
                "<InstanceID val=\"0\"><TransportState val=\"{}\"/>"
                "<TransportStatus val=\"OK\"/><TransportPlaySpeed val=\"1\"/>"
                "<AVTransportURI val=\"{}\"/><AVTransportURIMetaData val=\"{}\"/>"
                "<CurrentTrackURI val=\"{}\"/><CurrentTrackMetaData val=\"{}\"/>"
                "<NextAVTransportURI val=\"{}\"/>"
                "<NextAVTransportURIMetaData val=\"{}\"/>"
                "<CurrentTransportActions val=\"Play,Pause,Stop,Seek,Next,Previous\"/>"
                "<RelativeTimePosition val=\"{}\"/></InstanceID></Event>",
                xml_escape(state), xml_escape(uri), xml_escape(metadata),
                xml_escape(uri), xml_escape(metadata), xml_escape(next_uri),
                xml_escape(next_metadata), format_dlna_time(start))
            : std::format("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\">"
                "<InstanceID val=\"0\"><Volume channel=\"Master\" val=\"{}\"/>"
                "<Mute channel=\"Master\" val=\"{}\"/></InstanceID></Event>",
                static_cast<unsigned>(std::clamp(current_volume, 0.0F, 1.0F) * 100),
                current_muted ? 1 : 0);
        return std::format("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">"
            "<e:property><LastChange>{}</LastChange></e:property>"
            "</e:propertyset>", xml_escape(change));
    }

    bool send_event(const EventSubscription& subscription,
        std::string_view body) noexcept {
        addrinfo hints{};
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;
        addrinfo* addresses{};
        const auto port = std::to_string(subscription.callback.port);
        if (getaddrinfo(subscription.callback.host.c_str(), port.c_str(),
                &hints, &addresses) != 0) return false;
        std::unique_ptr<addrinfo, decltype(&freeaddrinfo)> address_guard(
            addresses, &freeaddrinfo);
        SOCKET client{INVALID_SOCKET};
        for (auto* address = addresses; address; address = address->ai_next) {
            client = WSASocketW(address->ai_family, address->ai_socktype,
                address->ai_protocol, nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
            if (client == INVALID_SOCKET) continue;
            u_long nonblocking = 1;
            if (ioctlsocket(client, FIONBIO, &nonblocking) != 0) {
                closesocket(client);
                client = INVALID_SOCKET;
                continue;
            }
            const auto connected = connect(client, address->ai_addr,
                static_cast<int>(address->ai_addrlen));
            const auto connect_error = connected == 0 ? 0 : WSAGetLastError();
            const auto deadline = std::chrono::steady_clock::now() +
                std::chrono::seconds(2);
            if (connected != 0 && connect_error != WSAEWOULDBLOCK &&
                connect_error != WSAEINPROGRESS && connect_error != WSAEINVAL) {
                closesocket(client);
                client = INVALID_SOCKET;
                continue;
            }
            if (connected != 0 && !wait_socket(client, POLLWRNORM,
                    stopping, deadline)) {
                closesocket(client);
                client = INVALID_SOCKET;
                continue;
            }
            int socket_error{};
            int error_length = sizeof(socket_error);
            if (getsockopt(client, SOL_SOCKET, SO_ERROR,
                    reinterpret_cast<char*>(&socket_error), &error_length) != 0 ||
                socket_error != 0) {
                closesocket(client);
                client = INVALID_SOCKET;
                continue;
            }
            break;
        }
        if (client == INVALID_SOCKET) return false;
        const auto host = subscription.callback.host.find(':') == std::string::npos
            ? subscription.callback.host
            : std::format("[{}]", subscription.callback.host);
        const auto request = std::format("NOTIFY {} HTTP/1.1\r\n"
            "HOST: {}:{}\r\nCONTENT-TYPE: text/xml; charset=\"utf-8\"\r\n"
            "NT: upnp:event\r\nNTS: upnp:propchange\r\nSID: {}\r\nSEQ: {}\r\n"
            "CONTENT-LENGTH: {}\r\nCONNECTION: close\r\n\r\n{}",
            subscription.callback.path, host, subscription.callback.port,
            subscription.sid, subscription.sequence, body.size(), body);
        const auto sent = send_all(client, request, stopping);
        shutdown(client, SD_BOTH);
        closesocket(client);
        return sent;
    }

    void notify_event(std::string_view service) {
        const auto body = event_body(service);
        std::vector<EventSubscription> targets;
        const auto now = std::chrono::steady_clock::now();
        {
            std::scoped_lock lock(subscription_mutex);
            std::erase_if(subscriptions, [&](const auto& subscription) {
                return subscription.expires <= now;
            });
            for (auto& subscription : subscriptions) {
                if (subscription.service != service) continue;
                targets.push_back(subscription);
                ++subscription.sequence;
            }
        }
        for (const auto& target : targets) {
            if (!send_event(target, body)) {
                std::scoped_lock lock(subscription_mutex);
                std::erase_if(subscriptions, [&](const auto& subscription) {
                    return subscription.sid == target.sid;
                });
                log(std::format("dlna event delivery failed sid={}", target.sid));
            }
        }
    }

    std::optional<EventSubscription> subscribe(const HttpRequest& request) {
        const auto service = event_service(request.path);
        if (service.empty()) return std::nullopt;
        const auto timeout = event_timeout(request);
        const auto now = std::chrono::steady_clock::now();
        if (const auto sid = request.headers.find("sid"); sid != request.headers.end()) {
            if (request.headers.contains("callback") || request.headers.contains("nt"))
                return std::nullopt;
            std::scoped_lock lock(subscription_mutex);
            const auto found = std::ranges::find_if(subscriptions,
                [&](const auto& subscription) {
                    return subscription.sid == trim(sid->second) &&
                        subscription.service == service;
                });
            if (found == subscriptions.end()) return std::nullopt;
            found->expires = now + timeout;
            return *found;
        }
        const auto callback = request.headers.find("callback");
        const auto nt = request.headers.find("nt");
        if (callback == request.headers.end() || nt == request.headers.end() ||
            lower(trim(nt->second)) != "upnp:event") return std::nullopt;
        const auto endpoint = parse_event_callback(callback->second);
        if (!endpoint) return std::nullopt;
        EventSubscription subscription{
            .sid = std::format("{}-event-{}", uuid,
                next_subscription.fetch_add(1, std::memory_order_relaxed) + 1),
            .service = std::string(service),
            .callback = *endpoint,
            .sequence = 0,
            .expires = now + timeout,
        };
        {
            std::scoped_lock lock(subscription_mutex);
            subscriptions.push_back(subscription);
        }
        return subscription;
    }

    bool unsubscribe(const HttpRequest& request) {
        const auto sid = request.headers.find("sid");
        const auto service = event_service(request.path);
        if (sid == request.headers.end() || service.empty() ||
            request.headers.contains("callback") || request.headers.contains("nt"))
            return false;
        const auto value = trim(sid->second);
        std::scoped_lock lock(subscription_mutex);
        const auto previous = subscriptions.size();
        std::erase_if(subscriptions, [&](const auto& subscription) {
            return subscription.sid == value && subscription.service == service;
        });
        return subscriptions.size() != previous;
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
            scpd_action("SetNextAVTransportURI", {
                {"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"NextURI", "in", "NextAVTransportURI"},
                {"NextURIMetaData", "in", "NextAVTransportURIMetaData"}}),
            scpd_action("Play", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"},
                {"Speed", "in", "TransportPlaySpeed"}}),
            scpd_action("Pause", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
            scpd_action("Stop", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
            scpd_action("Next", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
            scpd_action("Previous", {{"InstanceID", "in", "A_ARG_TYPE_InstanceID"}}),
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
            for (const auto candidate : {"SetNextAVTransportURI", "SetAVTransportURI",
                    "Play", "Pause", "Stop", "Next", "Previous", "Seek",
                    "GetTransportInfo", "GetPositionInfo", "GetMediaInfo",
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
        const auto soap_index = soap_requests.fetch_add(1, std::memory_order_relaxed) + 1;
        log(std::format("dlna soap request={} action={} service={} body_bytes={}",
            soap_index, action.empty() ? "<unknown>" : action, service,
            request.body.size()));
        std::string_view changed_service;

        if (action == "SetAVTransportURI" && avtransport) {
            const auto uri = xml_value(request.body, "CurrentURI");
            const auto metadata = xml_value(request.body, "CurrentURIMetaData");
            if (!uri || !iPhoneMirror::wireless::is_valid_http_url(*uri))
                return {service, soap_error(714, "Illegal MIME-type")};
            bool stop_active_media{};
            {
                std::scoped_lock lock(state_mutex);
                stop_active_media = transport_state != "STOPPED";
                media_uri = *uri;
                media_metadata = metadata.value_or("");
                next_media_uri.clear();
                next_media_metadata.clear();
                media_start = 0;
                transport_state = "STOPPED";
            }
            if (stop_active_media && callbacks.stop) callbacks.stop();
            changed_service = "avtransport";
            log(std::format("dlna SetAVTransportURI url_bytes={}", uri->size()));
        }
        else if (action == "SetNextAVTransportURI" && avtransport) {
            const auto uri = xml_value(request.body, "NextURI");
            const auto metadata = xml_value(request.body, "NextURIMetaData");
            if (!uri || !metadata ||
                (!uri->empty() && !iPhoneMirror::wireless::is_valid_http_url(*uri)))
                return {service, soap_error(714, "Illegal MIME-type")};
            {
                std::scoped_lock lock(state_mutex);
                next_media_uri = *uri;
                next_media_metadata = *metadata;
            }
            changed_service = "avtransport";
            log(std::format("dlna SetNextAVTransportURI url_bytes={}", uri->size()));
        }
        else if (action == "Play" && avtransport) {
            std::string uri;
            double start{};
            double base_volume{1.0};
            bool play_muted{};
            bool resuming{};
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
                start = media_start;
                base_volume = volume;
                play_muted = muted;
                resuming = transport_state == "PAUSED_PLAYBACK";
                if (!uri.empty()) transport_state = "PLAYING";
            }
            if (uri.empty()) return {service, soap_error(701, "Transition not available")};
            if (resuming) {
                if (callbacks.resume) callbacks.resume();
                log("dlna Resume");
            } else {
                double duration{};
                {
                    std::scoped_lock lock(state_mutex);
                    duration = reported_media_duration(uri, media_metadata, 0);
                }
                if (callbacks.play)
                    callbacks.play(uri, duration, start, base_volume, play_muted);
                log(std::format("dlna Play start={:.3f}", start));
            }
            changed_service = "avtransport";
        }
        else if (action == "Stop" && avtransport) {
            {
                std::scoped_lock lock(state_mutex);
                transport_state = "STOPPED";
            }
            if (callbacks.stop) callbacks.stop();
            changed_service = "avtransport";
            log("dlna Stop");
        }
        else if (action == "Next" && avtransport) {
            std::string uri;
            double base_volume{1.0};
            bool play_muted{};
            {
                std::scoped_lock lock(state_mutex);
                if (!next_media_uri.empty()) {
                    media_uri = std::move(next_media_uri);
                    media_metadata = std::move(next_media_metadata);
                    next_media_uri.clear();
                    next_media_metadata.clear();
                    media_start = 0;
                    transport_state = "PLAYING";
                    uri = media_uri;
                    base_volume = volume;
                    play_muted = muted;
                }
            }
            if (!uri.empty()) {
                double duration{};
                {
                    std::scoped_lock lock(state_mutex);
                    duration = reported_media_duration(uri, media_metadata, 0);
                }
                if (callbacks.play)
                    callbacks.play(uri, duration, 0, base_volume, play_muted);
                changed_service = "avtransport";
            }
            log(std::format("dlna Next queued_uri={}", !uri.empty()));
        }
        else if (action == "Previous" && avtransport) {
            // Platinum-based receivers advertise and accept Previous even when
            // the controller owns the playlist and follows with a new URI.
            log("dlna Previous delegated_to_controller=true");
        }
        else if (action == "Pause" && avtransport) {
            {
                std::scoped_lock lock(state_mutex);
                transport_state = "PAUSED_PLAYBACK";
            }
            if (callbacks.pause) callbacks.pause();
            changed_service = "avtransport";
            log("dlna Pause");
        }
        else if (action == "Seek" && avtransport) {
            const auto target = xml_value(request.body, "Target");
            const auto position = target ? parse_dlna_time(*target) : std::nullopt;
            if (!position) {
                const auto unit = xml_value(request.body, "Unit").value_or("");
                log(std::format(
                    "dlna Seek rejected target_present={} target_bytes={} "
                    "target_raw={} unit={}", target.has_value(),
                    target ? target->size() : 0,
                    target ? log_token(*target) : "<missing>", trim(unit)));
                return {service, soap_error(402, "Invalid Args")};
            }
            {
                std::scoped_lock lock(state_mutex);
                media_start = *position;
            }
            if (callbacks.seek) callbacks.seek(*position);
            changed_service = "avtransport";
            log(std::format("dlna Seek position={:.3f}", *position));
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
            std::string metadata;
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
                metadata = media_metadata;
            }
            duration = reported_media_duration(uri, metadata, duration);
            return {service, soap_envelope(service, action, std::format(
                "<Track>1</Track><TrackDuration>{}</TrackDuration>"
                "<TrackMetaData>{}</TrackMetaData><TrackURI>{}</TrackURI>"
                "<RelTime>{}</RelTime><AbsTime>{}</AbsTime>"
                "<RelCount>2147483647</RelCount><AbsCount>2147483647</AbsCount>",
                format_dlna_time(duration), xml_escape(metadata), xml_escape(uri),
                format_dlna_time(position), format_dlna_time(position)))};
        }
        else if (action == "GetMediaInfo" && avtransport) {
            double duration{};
            if (callbacks.get_play_info) callbacks.get_play_info(&duration, nullptr, nullptr);
            std::string uri;
            std::string metadata;
            std::string next_uri;
            std::string next_metadata;
            {
                std::scoped_lock lock(state_mutex);
                uri = media_uri;
                metadata = media_metadata;
                next_uri = next_media_uri;
                next_metadata = next_media_metadata;
            }
            duration = reported_media_duration(uri, metadata, duration);
            return {service, soap_envelope(service, action, std::format(
                "<NrTracks>1</NrTracks><MediaDuration>{}</MediaDuration>"
                "<CurrentURI>{}</CurrentURI><CurrentURIMetaData>{}</CurrentURIMetaData>"
                "<NextURI>{}</NextURI><NextURIMetaData>{}</NextURIMetaData>"
                "<PlayMedium>NETWORK</PlayMedium><RecordMedium>NOT_IMPLEMENTED</RecordMedium>"
                "<WriteStatus>NOT_IMPLEMENTED</WriteStatus>",
                format_dlna_time(duration), xml_escape(uri), xml_escape(metadata),
                xml_escape(next_uri), xml_escape(next_metadata)))};
        }
        else if (action == "GetCurrentTransportActions" && avtransport) {
            return {service, soap_envelope(service, action,
                "<Actions>Play,Pause,Stop,Seek,Next,Previous</Actions>")};
        }
        else if (action == "GetProtocolInfo" && connection) {
            constexpr std::string_view sink =
                "http-get:*:video/mp4:*,http-get:*:video/mpeg:*,"
                "http-get:*:video/x-ms-wmv:*,http-get:*:application/vnd.apple.mpegurl:*,"
                "http-get:*:application/x-mpegURL:*,http-get:*:audio/mpeg:*,"
                "http-get:*:audio/mp4:*,http-get:*:video/x-matroska:*,http-get:*:*:*";
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
            const auto desired = xml_value(request.body, "DesiredVolume");
            unsigned value{};
            if (!desired) return {service, soap_error(402, "Invalid Args")};
            const auto [end, error] = std::from_chars(
                desired->data(), desired->data() + desired->size(), value);
            if (error != std::errc{} || end != desired->data() + desired->size() ||
                value > 100)
                return {service, soap_error(402, "Invalid Args")};
            double base_volume{};
            {
                std::scoped_lock lock(state_mutex);
                volume = value / 100.0F;
                base_volume = volume;
            }
            if (callbacks.set_volume) callbacks.set_volume(base_volume);
            changed_service = "renderingcontrol";
        }
        else if (action == "GetVolume" && rendering) {
            std::scoped_lock lock(state_mutex);
            return {service, soap_envelope(service, action,
                std::format("<CurrentVolume>{}</CurrentVolume>",
                    static_cast<unsigned>(volume * 100)))};
        }
        else if (action == "SetMute" && rendering) {
            const auto desired = xml_value(request.body, "DesiredMute");
            if (!desired) return {service, soap_error(402, "Invalid Args")};
            const auto normalized = lower(*desired);
            if (normalized != "0" && normalized != "1" &&
                normalized != "false" && normalized != "true")
                return {service, soap_error(402, "Invalid Args")};
            std::pair<bool, double> mute_change;
            {
                std::scoped_lock lock(state_mutex);
                muted = normalized == "1" || normalized == "true";
                mute_change = {muted, static_cast<double>(volume)};
            }
            if (callbacks.set_mute)
                callbacks.set_mute(mute_change.first, mute_change.second);
            changed_service = "renderingcontrol";
        }
        else if (action == "GetMute" && rendering) {
            std::scoped_lock lock(state_mutex);
            return {service, soap_envelope(service, action,
                std::format("<CurrentMute>{}</CurrentMute>", muted ? 1 : 0))};
        }
        else {
            return {service, soap_error(401, "Invalid Action")};
        }
        if (!changed_service.empty()) notify_event(changed_service);
        return {service, soap_envelope(service, action, "")};
    }

    void handle_http(SOCKET client) {
        const auto request_index = http_requests.fetch_add(1, std::memory_order_relaxed) + 1;
        const auto started = std::chrono::steady_clock::now();
        const auto request = read_request(client, stopping);
        if (!request) {
            http_parse_failures.fetch_add(1, std::memory_order_relaxed);
            log(std::format("dlna http request={} rejected stopping={} duration_ms={}",
                request_index, stopping.load(std::memory_order_acquire),
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - started).count()));
            return;
        }
        std::string response;
        int status = 404;
        std::string_view route = "not_found";
        if (request->method == "GET" &&
            (request->path == "/dlna/device.xml" || request->path == "/device.xml")) {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"", description());
            status = 200;
            route = "device_description";
        }
        else if (request->method == "GET" && request->path == "/dlna/avtransport.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                avtransport_scpd());
            status = 200;
            route = "avtransport_scpd";
        }
        else if (request->method == "GET" &&
            request->path == "/dlna/connectionmanager.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                connectionmanager_scpd());
            status = 200;
            route = "connectionmanager_scpd";
        }
        else if (request->method == "GET" &&
            request->path == "/dlna/renderingcontrol.xml") {
            response = http_response(200, "OK", "text/xml; charset=\"utf-8\"",
                renderingcontrol_scpd());
            status = 200;
            route = "renderingcontrol_scpd";
        }
        else if (request->method == "POST" &&
            request->path.starts_with("/dlna/control/")) {
            const auto [_, body] = handle_soap(*request);
            const auto error = body.find("<s:Fault>") != std::string::npos;
            status = error ? 500 : 200;
            route = error ? "soap_fault" : "soap";
            response = http_response(status, error ? "Internal Server Error" : "OK",
                "text/xml; charset=\"utf-8\"", body,
                "EXT:\r\n");
        }
        else if (request->method == "SUBSCRIBE" &&
            request->path.starts_with("/dlna/event/")) {
            const auto renewal = request->headers.contains("sid");
            const auto subscription = subscribe(*request);
            if (!subscription) {
                response = http_response(412, "Precondition Failed", "text/plain",
                    "Invalid event subscription");
                status = 412;
                route = "subscribe_rejected";
            } else if (!renewal &&
                !send_event(*subscription, event_body(subscription->service))) {
                std::scoped_lock lock(subscription_mutex);
                std::erase_if(subscriptions, [&](const auto& candidate) {
                    return candidate.sid == subscription->sid;
                });
                response = http_response(412, "Precondition Failed", "text/plain",
                    "Event callback is unreachable");
                status = 412;
                route = "subscribe_callback_unreachable";
            } else {
                if (!renewal) {
                    std::scoped_lock lock(subscription_mutex);
                    for (auto& candidate : subscriptions) {
                        if (candidate.sid == subscription->sid) {
                            candidate.sequence = 1;
                            break;
                        }
                    }
                }
                response = http_response(200, "OK", "text/plain", "",
                    std::format("SID: {}\r\nTIMEOUT: Second-{}\r\n",
                        subscription->sid, event_timeout(*request).count()));
                status = 200;
                route = renewal ? "subscribe_renew" : "subscribe";
            }
        }
        else if (request->method == "UNSUBSCRIBE" &&
            request->path.starts_with("/dlna/event/")) {
            if (unsubscribe(*request)) {
                response = http_response(200, "OK", "text/plain", "");
                status = 200;
                route = "unsubscribe";
            } else {
                response = http_response(412, "Precondition Failed", "text/plain",
                    "Unknown event subscription");
                status = 412;
                route = "unsubscribe_rejected";
            }
        }
        else response = http_response(404, "Not Found", "text/plain", "Not Found");
        const auto sent = send_all(client, response, stopping);
        if (!sent) http_send_failures.fetch_add(1, std::memory_order_relaxed);
        http_responses.fetch_add(1, std::memory_order_relaxed);
        const auto query_at = request->path.find('?');
        const auto logged_path = std::string_view(request->path).substr(
            0, query_at == std::string::npos ? request->path.size() : query_at);
        const auto query_bytes = query_at == std::string::npos
            ? std::size_t{}
            : request->path.size() - query_at - 1;
        log(std::format("dlna http request={} method={} path={} route={} status={} "
            "query_bytes={} body_bytes={} response_bytes={} sent={} duration_ms={}",
            request_index, request->method, logged_path, route, status, query_bytes,
            request->body.size(), response.size(), sent,
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - started).count()));
    }

    void http_loop() {
        while (!stopping.load(std::memory_order_acquire)) {
            sockaddr_storage remote{};
            int length = sizeof(remote);
            SOCKET client{INVALID_SOCKET};
            {
                // Keep the listener value stable across accept so stop cannot
                // close it and let Windows reuse the numeric SOCKET first.
                std::scoped_lock lock(http_listener_mutex);
                if (stopping.load(std::memory_order_acquire) ||
                    http_socket == INVALID_SOCKET) break;
                client = accept(http_socket,
                    reinterpret_cast<sockaddr*>(&remote), &length);
            }
            if (client == INVALID_SOCKET) {
                if (!stopping.load(std::memory_order_acquire))
                    std::this_thread::sleep_for(std::chrono::milliseconds(20));
                continue;
            }
            // Explicit nonblocking I/O plus bounded WSAPoll slices lets stop
            // join this worker without closing an accepted socket cross-thread.
            u_long nonblocking = 1;
            if (ioctlsocket(client, FIONBIO, &nonblocking) != 0) {
                closesocket(client);
                continue;
            }
            if (stopping.load(std::memory_order_acquire)) {
                closesocket(client);
                break;
            }
            try {
                std::thread worker([this, client] {
                    try {
                        handle_http(client);
                    } catch (...) {
                        http_parse_failures.fetch_add(1, std::memory_order_relaxed);
                        log("dlna http handler failed with an exception");
                    }
                    closesocket(client);
                });
                std::scoped_lock lock(client_mutex);
                client_threads.push_back(std::move(worker));
            } catch (...) {
                closesocket(client);
                http_parse_failures.fetch_add(1, std::memory_order_relaxed);
                log("dlna http client worker creation failed");
            }
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
            "CACHE-CONTROL: max-age=1800\r\nDATE: {}\r\nEXT:\r\n"
            "LOCATION: {}\r\nSERVER: Windows/10.0 UPnP/1.0 iPhoneMirror/1.0\r\n"
            "ST: {}\r\nUSN: {}\r\nBOOTID.UPNP.ORG: 1\r\n"
            "CONFIGID.UPNP.ORG: 1\r\n\r\n", http_date(), location(address), st, usn);
        sendto(ssdp_socket, message.data(), static_cast<int>(message.size()), 0,
            reinterpret_cast<const sockaddr*>(&remote), sizeof(remote));
    }

    void send_notify(bool alive) {
        log(std::format("dlna ssdp notify state={} interfaces={}",
            alive ? "alive" : "byebye", interfaces.size()));
        sockaddr_in target{};
        target.sin_family = AF_INET;
        target.sin_port = htons(ssdp_port);
        if (inet_pton(AF_INET, MulticastAddress.data(), &target.sin_addr) != 1) return;
        for (const auto& address : interfaces) {
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
                    unsigned mx{};
                    std::size_t at{};
                    while ((at = message.find('\n', at)) != std::string_view::npos) {
                        ++at;
                        const auto end = message.find('\n', at);
                        const auto line = message.substr(at,
                            (end == std::string_view::npos ? message.size() : end) - at);
                        const auto separator = line.find(':');
                        if (separator == std::string_view::npos) continue;
                        const auto header_name = lower(trim(line.substr(0, separator)));
                        const auto value = trim(line.substr(separator + 1));
                        if (header_name == "st") st = value;
                        else if (header_name == "mx") {
                            const auto [mx_end, mx_error] = std::from_chars(
                                value.data(), value.data() + value.size(), mx);
                            if (mx_error != std::errc{} ||
                                mx_end != value.data() + value.size()) mx = 0;
                        }
                    }
                    mx = std::clamp(mx, 0U, 5U);
                    if (mx != 0) {
                        const auto delay = static_cast<unsigned>(
                            GetTickCount64() % (static_cast<std::uint64_t>(mx) * 1000U + 1U));
                        std::this_thread::sleep_for(std::chrono::milliseconds(delay));
                    }
                    for (const auto& [type, usn] : advertised_services()) {
                        if (lower(st) == "ssdp:all" || lower(st) == lower(type))
                            send_search_response(remote, type, usn);
                    }
                    const auto search_index = ssdp_searches.fetch_add(
                        1, std::memory_order_relaxed) + 1;
                    log(std::format(
                        "dlna ssdp search={} st={} responses_sent", search_index, st));
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
    impl_->callbacks = std::move(callbacks);
    WSADATA winsock{};
    const auto winsock_error = WSAStartup(MAKEWORD(2, 2), &winsock);
    if (winsock_error != 0) {
        impl_->log(std::format("dlna startup failed stage=winsock_startup winsock={}",
            winsock_error));
        return false;
    }
    impl_->winsock_started = true;
    impl_->name = std::move(friendly_name);
    impl_->uuid = std::move(uuid);
    impl_->http_port = http_port;
    impl_->ssdp_port = ssdp_port;
    {
        std::scoped_lock lock(impl_->state_mutex);
        impl_->media_uri.clear();
        impl_->media_metadata.clear();
        impl_->next_media_uri.clear();
        impl_->next_media_metadata.clear();
        impl_->media_start = 0;
        impl_->volume = 1.0F;
        impl_->muted = false;
        impl_->transport_state = "STOPPED";
    }
    impl_->stopping.store(false, std::memory_order_release);
    impl_->http_requests.store(0, std::memory_order_relaxed);
    impl_->http_parse_failures.store(0, std::memory_order_relaxed);
    impl_->http_responses.store(0, std::memory_order_relaxed);
    impl_->http_send_failures.store(0, std::memory_order_relaxed);
    impl_->soap_requests.store(0, std::memory_order_relaxed);
    impl_->ssdp_searches.store(0, std::memory_order_relaxed);

    impl_->http_socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (impl_->http_socket == INVALID_SOCKET) {
        impl_->log(std::format("dlna startup failed stage=http_socket winsock={}",
            WSAGetLastError()));
        stop();
        return false;
    }
    BOOL exclusive = TRUE;
    setsockopt(impl_->http_socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
        reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
    sockaddr_in http_address{AF_INET, htons(http_port), {.S_un = {.S_addr = INADDR_ANY}}};
    if (bind(impl_->http_socket, reinterpret_cast<const sockaddr*>(&http_address),
            sizeof(http_address)) != 0 || listen(impl_->http_socket, 16) != 0) {
        impl_->log(std::format(
            "dlna startup failed stage=http_bind_or_listen port={} winsock={}",
            http_port, WSAGetLastError()));
        stop();
        return false;
    }
    u_long nonblocking = 1;
    if (ioctlsocket(impl_->http_socket, FIONBIO, &nonblocking) != 0) {
        impl_->log(std::format(
            "dlna startup failed stage=http_nonblocking winsock={}",
            WSAGetLastError()));
        stop();
        return false;
    }

    impl_->ssdp_socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (impl_->ssdp_socket == INVALID_SOCKET) {
        impl_->log(std::format("dlna startup failed stage=ssdp_socket winsock={}",
            WSAGetLastError()));
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
        impl_->log("dlna startup failed stage=multicast_address");
        stop();
        return false;
    }
    impl_->interfaces = local_ipv4_addresses();
    impl_->interfaces.emplace_back("127.0.0.1");
    bool joined{};
    std::size_t interface_index{};
    for (const auto& address : impl_->interfaces) {
        ++interface_index;
        ip_mreq membership{.imr_multiaddr = multicast};
        if (inet_pton(AF_INET, address.c_str(), &membership.imr_interface) != 1)
            continue;
        if (setsockopt(impl_->ssdp_socket, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                reinterpret_cast<const char*>(&membership), sizeof(membership)) == 0) {
            joined = true;
            impl_->log(std::format("dlna multicast joined interface_index={}",
                interface_index));
        }
        else impl_->log(std::format(
            "dlna multicast join failed interface_index={} winsock={}",
            interface_index, WSAGetLastError()));
    }
    if (!joined) {
        impl_->log(std::format(
            "dlna startup failed stage=multicast_join interfaces={}",
            impl_->interfaces.size()));
        stop();
        return false;
    }
    u_long ssdp_nonblocking = 1;
    if (ioctlsocket(impl_->ssdp_socket, FIONBIO, &ssdp_nonblocking) != 0) {
        impl_->log(std::format(
            "dlna startup failed stage=ssdp_nonblocking winsock={}",
            WSAGetLastError()));
        stop();
        return false;
    }
    try {
        impl_->http_thread = std::thread([this] { impl_->http_loop(); });
        impl_->ssdp_thread = std::thread([this] { impl_->ssdp_loop(); });
    } catch (const std::exception& error) {
        impl_->log(std::format("dlna worker startup failed error={}", error.what()));
        stop();
        return false;
    }
    impl_->log(std::format(
        "dlna renderer ready name_bytes={} uuid_bytes={} http_port={} ssdp_port={}",
        impl_->name.size(), impl_->uuid.size(), impl_->http_port, impl_->ssdp_port));
    return true;
}

void DlnaRenderer::stop() noexcept {
    if (!impl_) return;
    const auto was_started = impl_->winsock_started;
    impl_->stopping.store(true, std::memory_order_release);
    {
        // Close the listener before joining so the worker cannot return to an
        // accept path after its active client has been interrupted.
        std::scoped_lock lock(impl_->http_listener_mutex);
        if (impl_->http_socket != INVALID_SOCKET) {
            shutdown(impl_->http_socket, SD_BOTH);
            closesocket(impl_->http_socket);
            impl_->http_socket = INVALID_SOCKET;
        }
    }
    if (impl_->http_thread.joinable()) impl_->http_thread.join();
    {
        std::scoped_lock lock(impl_->client_mutex);
        for (auto& client : impl_->client_threads) {
            if (client.joinable()) client.join();
        }
        impl_->client_threads.clear();
    }

    // On Windows, closesocket can block while another thread is in recvfrom.
    // The UDP socket is nonblocking, so let the receive loop observe stopping,
    // publish ssdp:byebye, and exit before closing it here.
    if (impl_->ssdp_thread.joinable()) impl_->ssdp_thread.join();
    if (impl_->ssdp_socket != INVALID_SOCKET) {
        closesocket(impl_->ssdp_socket);
        impl_->ssdp_socket = INVALID_SOCKET;
    }
    if (was_started) {
        try {
            impl_->log(std::format(
                "dlna summary http_requests={} responses={} parse_failures={} "
                "send_failures={} soap_requests={} ssdp_searches={}",
                impl_->http_requests.load(std::memory_order_relaxed),
                impl_->http_responses.load(std::memory_order_relaxed),
                impl_->http_parse_failures.load(std::memory_order_relaxed),
                impl_->http_send_failures.load(std::memory_order_relaxed),
                impl_->soap_requests.load(std::memory_order_relaxed),
                impl_->ssdp_searches.load(std::memory_order_relaxed)));
        } catch (...) {
            // Shutdown diagnostics are best-effort.
        }
    }
    if (impl_->winsock_started) {
        WSACleanup();
        impl_->winsock_started = false;
    }
    {
        std::scoped_lock lock(impl_->subscription_mutex);
        impl_->subscriptions.clear();
    }
}

void DlnaRenderer::set_transport_stopped() noexcept {
    if (!impl_) return;
    {
        std::scoped_lock lock(impl_->state_mutex);
        impl_->transport_state = "STOPPED";
        impl_->media_start = 0;
    }
    impl_->notify_event("avtransport");
}

} // namespace iPhoneMirror::wireless
