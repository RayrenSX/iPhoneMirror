#include "IpcProtocol.h"

#include <WinSock2.h>
#include <WS2tcpip.h>
#include <Windows.h>
#include <iphlpapi.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <limits>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

std::wstring quote(std::wstring_view value) {
    return L"\"" + std::wstring(value) + L"\"";
}

unsigned short free_port(int socket_type, int protocol) {
    const auto socket = WSASocketW(AF_INET, socket_type, protocol,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (socket == INVALID_SOCKET) return 0;
    sockaddr_in address{AF_INET, 0, {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
    if (bind(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0) {
        closesocket(socket);
        return 0;
    }
    int address_length = sizeof(address);
    const auto success = getsockname(socket,
        reinterpret_cast<sockaddr*>(&address), &address_length) == 0;
    closesocket(socket);
    return success ? ntohs(address.sin_port) : 0;
}

bool wait_overlapped(HANDLE io, OVERLAPPED& operation, DWORD timeout,
    DWORD& transferred) {
    if (WaitForSingleObject(operation.hEvent, timeout) != WAIT_OBJECT_0) return false;
    return GetOverlappedResult(io, &operation, &transferred, FALSE) != FALSE;
}

bool read_exact(HANDLE pipe, void* destination, std::size_t size) {
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
            success = wait_overlapped(pipe, operation, INFINITE, transferred);
        CloseHandle(operation.hEvent);
        if (!success || transferred == 0) return false;
        bytes += transferred;
        size -= transferred;
    }
    return true;
}

struct PipeCapture {
    bool protocol_valid{true};
    bool environment_sync_failed{};
    bool runtime_device_id_invalid{};
};

void drain_pipe(HANDLE pipe, PipeCapture& capture) {
    while (true) {
        iPhoneMirror::wireless::MessageHeader header;
        if (!read_exact(pipe, &header, sizeof(header))) return;
        if (header.magic != iPhoneMirror::wireless::IpcMagic ||
            header.version != iPhoneMirror::wireless::IpcVersion ||
            header.payload_size > iPhoneMirror::wireless::MaxPayloadBytes) {
            capture.protocol_valid = false;
            return;
        }
        std::vector<std::uint8_t> payload(header.payload_size);
        if (!payload.empty() && !read_exact(pipe, payload.data(), payload.size())) {
            capture.protocol_valid = false;
            return;
        }
        if (header.type != iPhoneMirror::wireless::MessageType::Log) continue;
        const std::string_view log(reinterpret_cast<const char*>(payload.data()),
            payload.size());
        capture.environment_sync_failed = capture.environment_sync_failed ||
            log.find("IPHONE_MIRROR_DLL_ENVIRONMENT_SYNC failed") !=
                std::string_view::npos;
        capture.runtime_device_id_invalid = capture.runtime_device_id_invalid ||
            log.find("IPHONE_MIRROR_RUNTIME_DEVICE_ID invalid") !=
                std::string_view::npos;
    }
}

std::optional<in_addr> physical_ipv4_address() {
    ULONG size = 16U * 1024U;
    std::vector<std::uint8_t> buffer(size);
    constexpr ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST |
        GAA_FLAG_SKIP_DNS_SERVER;
    auto* adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
    auto status = GetAdaptersAddresses(AF_INET, flags, nullptr, adapters, &size);
    if (status == ERROR_BUFFER_OVERFLOW) {
        buffer.resize(size);
        adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
        status = GetAdaptersAddresses(AF_INET, flags, nullptr, adapters, &size);
    }
    if (status != NO_ERROR) return std::nullopt;

    constexpr std::array<ULONG, 2> physical_types{
        IF_TYPE_IEEE80211, IF_TYPE_ETHERNET_CSMACD};
    for (const auto type : physical_types) {
        for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
            if (adapter->OperStatus != IfOperStatusUp ||
                adapter->IfType != type)
                continue;
            for (auto* unicast = adapter->FirstUnicastAddress; unicast;
                unicast = unicast->Next) {
                if (!unicast->Address.lpSockaddr ||
                    unicast->Address.lpSockaddr->sa_family != AF_INET ||
                    unicast->DadState != IpDadStatePreferred)
                    continue;
                const auto* address = reinterpret_cast<const sockaddr_in*>(
                    unicast->Address.lpSockaddr);
                const auto host = ntohl(address->sin_addr.S_un.S_addr);
                if ((host >> 24U) == 127U || (host >> 16U) == 0xA9FEU ||
                    host == 0)
                    continue;
                return address->sin_addr;
            }
        }
    }
    return std::nullopt;
}

bool tcp_listener_matches(DWORD process_id, in_addr address,
    unsigned short port, bool wildcard) {
    ULONG size{};
    if (GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET,
            TCP_TABLE_OWNER_PID_LISTENER, 0) != ERROR_INSUFFICIENT_BUFFER)
        return false;
    std::vector<std::uint8_t> buffer(size);
    auto* table = reinterpret_cast<PMIB_TCPTABLE_OWNER_PID>(buffer.data());
    if (GetExtendedTcpTable(table, &size, FALSE, AF_INET,
            TCP_TABLE_OWNER_PID_LISTENER, 0) != NO_ERROR)
        return false;
    const auto expected_address = wildcard ? htonl(INADDR_ANY) :
        address.S_un.S_addr;
    for (DWORD index = 0; index < table->dwNumEntries; ++index) {
        const auto& row = table->table[index];
        if (row.dwOwningPid == process_id &&
            ntohs(static_cast<unsigned short>(row.dwLocalPort)) == port &&
            row.dwLocalAddr == expected_address)
            return true;
    }
    return false;
}

bool udp_endpoint_matches(DWORD process_id, in_addr address,
    unsigned short port, bool wildcard) {
    ULONG size{};
    if (GetExtendedUdpTable(nullptr, &size, FALSE, AF_INET,
            UDP_TABLE_OWNER_PID, 0) != ERROR_INSUFFICIENT_BUFFER)
        return false;
    std::vector<std::uint8_t> buffer(size);
    auto* table = reinterpret_cast<PMIB_UDPTABLE_OWNER_PID>(buffer.data());
    if (GetExtendedUdpTable(table, &size, FALSE, AF_INET,
            UDP_TABLE_OWNER_PID, 0) != NO_ERROR)
        return false;
    const auto expected_address = wildcard ? htonl(INADDR_ANY) :
        address.S_un.S_addr;
    for (DWORD index = 0; index < table->dwNumEntries; ++index) {
        const auto& row = table->table[index];
        if (row.dwOwningPid == process_id &&
            ntohs(static_cast<unsigned short>(row.dwLocalPort)) == port &&
            row.dwLocalAddr == expected_address)
            return true;
    }
    return false;
}

bool send_all(SOCKET socket, std::string_view data) {
    while (!data.empty()) {
        const auto sent = send(socket, data.data(), static_cast<int>(data.size()), 0);
        if (sent <= 0) return false;
        data.remove_prefix(static_cast<std::size_t>(sent));
    }
    return true;
}

std::size_t response_size(std::string_view response) {
    const auto header_end = response.find("\r\n\r\n");
    if (header_end == std::string_view::npos) return 0;
    auto headers = std::string(response.substr(0, header_end));
    std::ranges::transform(headers, headers.begin(), [](unsigned char value) {
        return static_cast<char>(std::tolower(value));
    });
    constexpr std::string_view name = "\r\ncontent-length:";
    const auto position = headers.find(name);
    if (position == std::string::npos) return header_end + 4;
    const auto value_start = headers.find_first_not_of(" \t", position + name.size());
    if (value_start == std::string::npos) return 0;
    const auto value_end = headers.find("\r\n", value_start);
    try {
        const auto length = std::stoull(headers.substr(
            value_start, value_end - value_start));
        return header_end + 4 + static_cast<std::size_t>(length);
    } catch (...) {
        return 0;
    }
}

std::vector<std::uint8_t> tcp_request(unsigned short port,
    std::string_view request) {
    SOCKET socket{INVALID_SOCKET};
    for (int attempt = 0; attempt < 100; ++attempt) {
        socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
            nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
        if (socket == INVALID_SOCKET) return {};
        sockaddr_in address{AF_INET, htons(port),
            {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
        if (connect(socket, reinterpret_cast<const sockaddr*>(&address),
                sizeof(address)) == 0)
            break;
        closesocket(socket);
        socket = INVALID_SOCKET;
        Sleep(50);
    }
    if (socket == INVALID_SOCKET || !send_all(socket, request)) {
        if (socket != INVALID_SOCKET) closesocket(socket);
        return {};
    }

    DWORD timeout = 5000;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    std::vector<std::uint8_t> response;
    std::array<std::uint8_t, 4096> bytes{};
    while (response.size() < 1024U * 1024U) {
        const auto count = recv(socket, reinterpret_cast<char*>(bytes.data()),
            static_cast<int>(bytes.size()), 0);
        if (count <= 0) break;
        response.insert(response.end(), bytes.begin(), bytes.begin() + count);
        const auto expected = response_size(std::string_view(
            reinterpret_cast<const char*>(response.data()), response.size()));
        if (expected != 0 && response.size() >= expected) {
            response.resize(expected);
            break;
        }
    }
    closesocket(socket);
    return response;
}

SOCKET connect_with_retry(unsigned short port, in_addr address) {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP,
            nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
        if (socket == INVALID_SOCKET) return INVALID_SOCKET;
        const sockaddr_in endpoint{AF_INET, htons(port), address};
        if (connect(socket, reinterpret_cast<const sockaddr*>(&endpoint),
                sizeof(endpoint)) == 0)
            return socket;
        closesocket(socket);
        Sleep(50);
    }
    return INVALID_SOCKET;
}

std::vector<std::uint8_t> socket_request(SOCKET socket,
    std::string_view request) {
    if (!send_all(socket, request)) return {};
    DWORD timeout = 5000;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    std::vector<std::uint8_t> response;
    std::array<std::uint8_t, 4096> bytes{};
    while (response.size() < 1024U * 1024U) {
        const auto count = recv(socket, reinterpret_cast<char*>(bytes.data()),
            static_cast<int>(bytes.size()), 0);
        if (count <= 0) break;
        response.insert(response.end(), bytes.begin(), bytes.begin() + count);
        const auto expected = response_size(std::string_view(
            reinterpret_cast<const char*>(response.data()), response.size()));
        if (expected != 0 && response.size() >= expected) {
            response.resize(expected);
            break;
        }
    }
    return response;
}

std::string rtsp_request(std::string_view method, std::string_view path,
    unsigned int sequence, std::span<const std::uint8_t> body,
    std::string_view content_type) {
    auto request = std::string(method) + " " + std::string(path) +
        " RTSP/1.0\r\nCSeq: " + std::to_string(sequence) +
        "\r\nUser-Agent: AirPlay/550.10\r\nConnection: keep-alive\r\n";
    if (!content_type.empty())
        request += "Content-Type: " + std::string(content_type) + "\r\n";
    request += "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n";
    request.append(reinterpret_cast<const char*>(body.data()), body.size());
    return request;
}

bool successful_rtsp_response(const std::vector<std::uint8_t>& response) {
    if (response.empty()) return false;
    const std::string_view text(reinterpret_cast<const char*>(response.data()),
        response.size());
    return text.starts_with("RTSP/1.0 200");
}

std::span<const std::uint8_t> response_body(
    const std::vector<std::uint8_t>& response) {
    constexpr std::array delimiter{std::uint8_t{'\r'}, std::uint8_t{'\n'},
        std::uint8_t{'\r'}, std::uint8_t{'\n'}};
    const auto body = std::search(response.begin(), response.end(),
        delimiter.begin(), delimiter.end());
    return body == response.end() ? std::span<const std::uint8_t>{} :
        std::span<const std::uint8_t>(body + delimiter.size(), response.end());
}

class BinaryPlist final {
public:
    explicit BinaryPlist(std::span<const std::uint8_t> bytes) : bytes_(bytes) {
        initialize();
    }

    bool valid() const noexcept { return valid_; }

    std::optional<std::uint64_t> root() const noexcept {
        return valid_ ? std::optional<std::uint64_t>(top_object_) :
            std::nullopt;
    }

    std::optional<std::uint64_t> dictionary_value(std::uint64_t dictionary,
        std::string_view key) const {
        const auto object = object_at(dictionary);
        if (!object || object->type != 0xD ||
            object->count > std::numeric_limits<std::size_t>::max())
            return std::nullopt;
        const auto count = static_cast<std::size_t>(object->count);
        const auto values = object->data_offset + count * ref_size_;
        for (std::size_t index = 0; index < count; ++index) {
            const auto key_ref = read_ref(object->data_offset + index * ref_size_);
            const auto value_ref = read_ref(values + index * ref_size_);
            if (!key_ref || !value_ref) return std::nullopt;
            const auto key_text = string_value(*key_ref);
            if (key_text && *key_text == key) return value_ref;
        }
        return std::nullopt;
    }

    std::optional<std::uint64_t> array_value(std::uint64_t array,
        std::size_t index) const {
        const auto object = object_at(array);
        if (!object || object->type != 0xA ||
            index >= object->count ||
            object->count > (bytes_.size() - object->data_offset) / ref_size_)
            return std::nullopt;
        return read_ref(object->data_offset + index * ref_size_);
    }

    std::optional<std::uint64_t> integer_value(std::uint64_t reference) const {
        const auto object = object_at(reference);
        if (!object || object->type != 0x1 || object->count == 0 ||
            object->count > 8)
            return std::nullopt;
        return read_be(object->data_offset,
            static_cast<std::size_t>(object->count));
    }

    std::optional<std::string> string_value(std::uint64_t reference) const {
        const auto object = object_at(reference);
        if (!object || object->count > std::numeric_limits<std::size_t>::max())
            return std::nullopt;
        const auto count = static_cast<std::size_t>(object->count);
        if (object->type == 0x5) {
            if (count > bytes_.size() - object->data_offset) return std::nullopt;
            return std::string(reinterpret_cast<const char*>(
                bytes_.data() + object->data_offset), count);
        }
        if (object->type != 0x6 || count >
                (bytes_.size() - object->data_offset) / 2U)
            return std::nullopt;
        std::string result;
        result.reserve(count);
        for (std::size_t index = 0; index < count; ++index) {
            const auto code_unit = read_be(object->data_offset + index * 2U, 2);
            if (!code_unit || *code_unit > 0x7f) return std::nullopt;
            result.push_back(static_cast<char>(*code_unit));
        }
        return result;
    }

private:
    struct Object {
        std::uint8_t type{};
        std::uint64_t count{};
        std::size_t data_offset{};
    };

    static std::optional<std::uint64_t> read_be(
        std::span<const std::uint8_t> bytes, std::size_t offset,
        std::size_t width) noexcept {
        if (width == 0 || width > 8 || offset > bytes.size() ||
            width > bytes.size() - offset)
            return std::nullopt;
        std::uint64_t value{};
        for (std::size_t index = 0; index < width; ++index)
            value = (value << 8U) | bytes[offset + index];
        return value;
    }

    std::optional<std::uint64_t> read_be(std::size_t offset,
        std::size_t width) const noexcept {
        return read_be(bytes_, offset, width);
    }

    void initialize() {
        if (bytes_.size() < 40 ||
            std::memcmp(bytes_.data(), "bplist00", 8) != 0)
            return;
        const auto trailer = bytes_.size() - 32U;
        offset_size_ = bytes_[trailer + 6U];
        ref_size_ = bytes_[trailer + 7U];
        const auto object_count = read_be(trailer + 8U, 8U);
        const auto top_object = read_be(trailer + 16U, 8U);
        const auto offset_table = read_be(trailer + 24U, 8U);
        if (!object_count || !top_object || !offset_table ||
            offset_size_ == 0 || offset_size_ > 8 || ref_size_ == 0 ||
            ref_size_ > 8 || *object_count == 0 ||
            *top_object >= *object_count || *offset_table > trailer ||
            *object_count > (trailer - static_cast<std::size_t>(*offset_table)) /
                offset_size_ || *object_count > std::numeric_limits<std::size_t>::max())
            return;
        const auto count = static_cast<std::size_t>(*object_count);
        offsets_.reserve(count);
        const auto table = static_cast<std::size_t>(*offset_table);
        for (std::size_t index = 0; index < count; ++index) {
            const auto offset = read_be(table + index * offset_size_, offset_size_);
            if (!offset || *offset >= table || *offset >= bytes_.size()) return;
            offsets_.push_back(static_cast<std::size_t>(*offset));
        }
        top_object_ = *top_object;
        valid_ = true;
    }

    std::optional<Object> object_at(std::uint64_t reference) const {
        if (!valid_ || reference >= offsets_.size()) return std::nullopt;
        const auto offset = offsets_[static_cast<std::size_t>(reference)];
        if (offset >= bytes_.size()) return std::nullopt;
        const auto marker = bytes_[offset];
        const auto type = static_cast<std::uint8_t>(marker >> 4U);
        const auto info = static_cast<std::uint8_t>(marker & 0x0fU);
        std::size_t cursor = offset + 1U;
        std::uint64_t count{};
        if (type == 0x1 || type == 0x2) {
            if (info > 3) return std::nullopt;
            count = std::uint64_t{1} << info;
        }
        else if (info == 0x0f) {
            if (cursor >= bytes_.size()) return std::nullopt;
            const auto length_marker = bytes_[cursor++];
            if ((length_marker >> 4U) != 0x1 ||
                (length_marker & 0x0fU) > 3)
                return std::nullopt;
            const auto width = std::size_t{1} << (length_marker & 0x0fU);
            const auto length = read_be(cursor, width);
            if (!length) return std::nullopt;
            cursor += width;
            count = *length;
        }
        else count = info;

        std::uint64_t unit{1};
        switch (type) {
        case 0x0:
            count = 0;
            unit = 0;
            break;
        case 0x1:
        case 0x2:
        case 0x4:
        case 0x5:
            break;
        case 0x3:
            count = 8;
            break;
        case 0x6:
            unit = 2;
            break;
        case 0x8:
            count = info + 1U;
            break;
        case 0xA:
            unit = ref_size_;
            break;
        case 0xD:
            unit = static_cast<std::uint64_t>(ref_size_) * 2U;
            break;
        default:
            return std::nullopt;
        }
        if (unit != 0 && count >
                std::numeric_limits<std::uint64_t>::max() / unit)
            return std::nullopt;
        const auto payload_size = count * unit;
        if (payload_size > std::numeric_limits<std::size_t>::max() ||
            cursor > bytes_.size() || static_cast<std::size_t>(payload_size) >
                bytes_.size() - cursor)
            return std::nullopt;
        return Object{type, count, cursor};
    }

    std::optional<std::uint64_t> read_ref(std::size_t offset) const noexcept {
        const auto value = read_be(offset, ref_size_);
        if (!value || *value >= offsets_.size()) return std::nullopt;
        return value;
    }

    std::span<const std::uint8_t> bytes_;
    std::vector<std::size_t> offsets_;
    std::uint8_t offset_size_{};
    std::uint8_t ref_size_{};
    std::uint64_t top_object_{};
    bool valid_{};
};

std::optional<std::uint64_t> plist_integer(const BinaryPlist& plist,
    std::uint64_t dictionary, std::string_view key) {
    const auto value = plist.dictionary_value(dictionary, key);
    return value ? plist.integer_value(*value) : std::nullopt;
}

std::optional<std::string> plist_string(const BinaryPlist& plist,
    std::uint64_t dictionary, std::string_view key) {
    const auto value = plist.dictionary_value(dictionary, key);
    return value ? plist.string_value(*value) : std::nullopt;
}

std::string plist_string(std::string_view xml, std::string_view key) {
    const auto key_position = xml.find("<key>" + std::string(key) + "</key>");
    if (key_position == std::string_view::npos) return {};
    constexpr std::string_view opening = "<string>";
    constexpr std::string_view closing = "</string>";
    const auto value_start = xml.find(opening, key_position);
    if (value_start == std::string_view::npos) return {};
    const auto content_start = value_start + opening.size();
    const auto value_end = xml.find(closing, content_start);
    return value_end == std::string_view::npos ? std::string{} :
        std::string(xml.substr(content_start, value_end - content_start));
}

bool valid_device_id(std::string_view value) {
    if (value.size() != 17) return false;
    for (std::size_t index = 0; index < value.size(); ++index) {
        if (index % 3 == 2) {
            if (value[index] != ':') return false;
        } else if (!std::isxdigit(static_cast<unsigned char>(value[index]))) {
            return false;
        }
    }
    return true;
}

std::vector<std::uint8_t> first_setup_plist(unsigned short timing_port) {
    // Binary plist fixture generated from the documented first mirror SETUP.
    constexpr std::uint8_t bytes[] = {
        0x62, 0x70, 0x6c, 0x69, 0x73, 0x74, 0x30, 0x30, 0xd8, 0x01, 0x02, 0x03,
        0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x10, 0x53, 0x65, 0x69, 0x76, 0x54, 0x65, 0x6b, 0x65, 0x79, 0x5a, 0x74,
        0x69, 0x6d, 0x69, 0x6e, 0x67, 0x50, 0x6f, 0x72, 0x74, 0x5f, 0x10, 0x18,
        0x69, 0x73, 0x53, 0x63, 0x72, 0x65, 0x65, 0x6e, 0x4d, 0x69, 0x72, 0x72,
        0x6f, 0x72, 0x69, 0x6e, 0x67, 0x53, 0x65, 0x73, 0x73, 0x69, 0x6f, 0x6e,
        0x54, 0x6e, 0x61, 0x6d, 0x65, 0x58, 0x64, 0x65, 0x76, 0x69, 0x63, 0x65,
        0x49, 0x44, 0x55, 0x6d, 0x6f, 0x64, 0x65, 0x6c, 0x59, 0x6f, 0x73, 0x56,
        0x65, 0x72, 0x73, 0x69, 0x6f, 0x6e, 0x4f, 0x10, 0x10, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x4f, 0x10, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x11, 0xa5, 0x5a, 0x09, 0x5f, 0x10, 0x13, 0x52,
        0x6f, 0x75, 0x74, 0x65, 0x20, 0x42, 0x69, 0x6e, 0x64, 0x69, 0x6e, 0x67,
        0x20, 0x53, 0x6d, 0x6f, 0x6b, 0x65, 0x5f, 0x10, 0x11, 0x30, 0x32, 0x3a,
        0x30, 0x30, 0x3a, 0x30, 0x30, 0x3a, 0x30, 0x30, 0x3a, 0x30, 0x30, 0x3a,
        0x30, 0x31, 0x5a, 0x69, 0x50, 0x68, 0x6f, 0x6e, 0x65, 0x31, 0x34, 0x2c,
        0x32, 0x54, 0x31, 0x38, 0x2e, 0x30, 0x00, 0x08, 0x00, 0x19, 0x00, 0x1d,
        0x00, 0x22, 0x00, 0x2d, 0x00, 0x48, 0x00, 0x4d, 0x00, 0x56, 0x00, 0x5c,
        0x00, 0x66, 0x00, 0x79, 0x00, 0xc4, 0x00, 0xc7, 0x00, 0xc8, 0x00, 0xde,
        0x00, 0xf2, 0x00, 0xfd, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x01,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02,
    };
    std::vector<std::uint8_t> result(std::begin(bytes), std::end(bytes));
    constexpr std::size_t port_marker = 196;
    if (result.size() <= port_marker + 2 || result[port_marker] != 0x11 ||
        result[port_marker + 1] != 0xa5 || result[port_marker + 2] != 0x5a)
        return {};
    result[port_marker + 1] = static_cast<std::uint8_t>(timing_port >> 8U);
    result[port_marker + 2] = static_cast<std::uint8_t>(timing_port);
    return result;
}

constexpr std::uint8_t second_setup_plist[] = {
    0x62, 0x70, 0x6c, 0x69, 0x73, 0x74, 0x30, 0x30, 0xd1, 0x01, 0x02, 0x57,
    0x73, 0x74, 0x72, 0x65, 0x61, 0x6d, 0x73, 0xa1, 0x03, 0xd2, 0x04, 0x05,
    0x06, 0x07, 0x54, 0x74, 0x79, 0x70, 0x65, 0x5f, 0x10, 0x12, 0x73, 0x74,
    0x72, 0x65, 0x61, 0x6d, 0x43, 0x6f, 0x6e, 0x6e, 0x65, 0x63, 0x74, 0x69,
    0x6f, 0x6e, 0x49, 0x44, 0x10, 0x6e, 0x13, 0x10, 0x20, 0x30, 0x40, 0x50,
    0x60, 0x70, 0x80, 0x08, 0x0b, 0x13, 0x15, 0x1a, 0x1f, 0x34, 0x36, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3f,
};

bool probe_media_route(DWORD process_id, unsigned short raop_port,
    in_addr local_address) {
    auto timing_socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP,
        nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT);
    if (timing_socket == INVALID_SOCKET) return false;
    sockaddr_in timing_endpoint{AF_INET, 0, local_address};
    if (bind(timing_socket, reinterpret_cast<const sockaddr*>(&timing_endpoint),
            sizeof(timing_endpoint)) != 0) {
        closesocket(timing_socket);
        return false;
    }
    int timing_endpoint_size = sizeof(timing_endpoint);
    if (getsockname(timing_socket, reinterpret_cast<sockaddr*>(&timing_endpoint),
            &timing_endpoint_size) != 0) {
        closesocket(timing_socket);
        return false;
    }
    const auto remote_timing_port = ntohs(timing_endpoint.sin_port);
    const auto control_socket = connect_with_retry(raop_port, local_address);
    if (control_socket == INVALID_SOCKET) {
        closesocket(timing_socket);
        return false;
    }

    std::array<std::uint8_t, 16> fairplay_setup{};
    std::memcpy(fairplay_setup.data(), "FPLY", 4);
    fairplay_setup[4] = 3;
    fairplay_setup[14] = 0;
    std::array<std::uint8_t, 164> fairplay_handshake{};
    std::memcpy(fairplay_handshake.data(), "FPLY", 4);
    fairplay_handshake[4] = 3;
    const auto setup_one = first_setup_plist(remote_timing_port);

    const auto fairplay_one_response = socket_request(control_socket,
        rtsp_request("POST", "/fp-setup", 1, fairplay_setup,
            "application/octet-stream"));
    const auto fairplay_two_response = socket_request(control_socket,
        rtsp_request("POST", "/fp-setup", 2, fairplay_handshake,
            "application/octet-stream"));
    const auto setup_one_response = socket_request(control_socket,
        rtsp_request("SETUP", "/stream", 3, setup_one,
            "application/x-apple-binary-plist"));
    const auto setup_two_response = socket_request(control_socket,
        rtsp_request("SETUP", "/stream", 4, second_setup_plist,
            "application/x-apple-binary-plist"));

    const BinaryPlist response_plist(response_body(setup_two_response));
    const auto root = response_plist.root();
    const auto streams = root ?
        response_plist.dictionary_value(*root, "streams") : std::nullopt;
    const auto stream = streams ?
        response_plist.array_value(*streams, 0) : std::nullopt;
    const auto data_port_value = stream ?
        plist_integer(response_plist, *stream, "dataPort") : std::nullopt;
    const auto timing_port_value = root ?
        plist_integer(response_plist, *root, "timingPort") : std::nullopt;
    const auto stream_type = stream ?
        plist_integer(response_plist, *stream, "type") : std::nullopt;
    const auto ports_valid = data_port_value && timing_port_value &&
        *data_port_value > 0 && *data_port_value <= 65535 &&
        *timing_port_value > 0 && *timing_port_value <= 65535 &&
        stream_type == 110;
    const auto data_port = ports_valid ?
        static_cast<unsigned short>(*data_port_value) : unsigned short{0};
    const auto timing_port = ports_valid ?
        static_cast<unsigned short>(*timing_port_value) : unsigned short{0};

    bool tcp_exact{};
    bool udp_exact{};
    bool tcp_wildcard{};
    bool udp_wildcard{};
    if (ports_valid) {
        for (int attempt = 0; attempt < 50 && (!tcp_exact || !udp_exact);
            ++attempt) {
            tcp_exact = tcp_listener_matches(process_id, local_address,
                data_port, false);
            udp_exact = udp_endpoint_matches(process_id, local_address,
                timing_port, false);
            tcp_wildcard = tcp_listener_matches(process_id, local_address,
                data_port, true);
            udp_wildcard = udp_endpoint_matches(process_id, local_address,
                timing_port, true);
            if (!tcp_exact || !udp_exact) Sleep(20);
        }
    }

    bool timing_source_valid{};
    if (ports_valid) {
        DWORD timeout = 2000;
        if (setsockopt(timing_socket, SOL_SOCKET, SO_RCVTIMEO,
                reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == 0) {
            std::array<std::uint8_t, 128> timing_packet{};
            sockaddr_in timing_source{};
            int timing_source_size = sizeof(timing_source);
            const auto timing_bytes = recvfrom(timing_socket,
                reinterpret_cast<char*>(timing_packet.data()),
                static_cast<int>(timing_packet.size()), 0,
                reinterpret_cast<sockaddr*>(&timing_source),
                &timing_source_size);
            timing_source_valid = timing_bytes == 48 &&
                timing_source.sin_addr.S_un.S_addr ==
                    local_address.S_un.S_addr &&
                ntohs(timing_source.sin_port) == timing_port;
        }
    }

    closesocket(control_socket);
    closesocket(timing_socket);
    const auto responses_valid = successful_rtsp_response(fairplay_one_response) &&
        response_body(fairplay_one_response).size() == 142 &&
        successful_rtsp_response(fairplay_two_response) &&
        response_body(fairplay_two_response).size() == 32 &&
        successful_rtsp_response(setup_one_response) &&
        successful_rtsp_response(setup_two_response) && response_plist.valid();
    const auto passed = responses_valid && ports_valid && tcp_exact && udp_exact &&
        !tcp_wildcard && !udp_wildcard && timing_source_valid;
    std::array<char, INET_ADDRSTRLEN> address_text{};
    InetNtopA(AF_INET, &local_address, address_text.data(),
        static_cast<DWORD>(address_text.size()));
    std::cout << "media_route address=" << address_text.data()
        << " data_port=" << data_port << " timing_port=" << timing_port
        << " responses=" << responses_valid << " ports=" << ports_valid
        << " tcp_exact=" << tcp_exact << " udp_exact=" << udp_exact
        << " tcp_wildcard=" << tcp_wildcard
        << " udp_wildcard=" << udp_wildcard
        << " timing_source=" << timing_source_valid
        << " passed=" << passed << '\n';
    return passed;
}

bool probe_mode(const std::filesystem::path& host, std::wstring_view mode,
    std::uint32_t features, std::optional<in_addr> route_address = std::nullopt) {
    const auto raop_port = free_port(SOCK_STREAM, IPPROTO_TCP);
    auto airplay_port = free_port(SOCK_STREAM, IPPROTO_TCP);
    for (int attempt = 0; airplay_port == raop_port && attempt < 10; ++attempt)
        airplay_port = free_port(SOCK_STREAM, IPPROTO_TCP);
    auto dlna_port = free_port(SOCK_STREAM, IPPROTO_TCP);
    for (int attempt = 0;
        (dlna_port == raop_port || dlna_port == airplay_port) && attempt < 10;
        ++attempt)
        dlna_port = free_port(SOCK_STREAM, IPPROTO_TCP);
    const auto ssdp_port = free_port(SOCK_DGRAM, IPPROTO_UDP);
    if (!raop_port || !airplay_port || !dlna_port || !ssdp_port ||
        raop_port == airplay_port || raop_port == dlna_port ||
        airplay_port == dlna_port)
        return false;

    const auto suffix = std::to_wstring(GetCurrentProcessId()) + L"-" +
        std::to_wstring(GetTickCount64()) + L"-" + std::wstring(mode);
    const auto pipe_name = L"\\\\.\\pipe\\iPhoneMirror-ProtocolSmoke-" + suffix;
    const auto stop_name = L"Local\\iPhoneMirror-ProtocolSmoke-Stop-" + suffix;
    const auto pipe = CreateNamedPipeW(pipe_name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1,
        64U * 1024U, 64U * 1024U, 0, nullptr);
    const auto stop_event = CreateEventW(nullptr, TRUE, FALSE, stop_name.c_str());
    if (pipe == INVALID_HANDLE_VALUE || !stop_event) {
        if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
        if (stop_event) CloseHandle(stop_event);
        return false;
    }

    OVERLAPPED connection{};
    connection.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!connection.hEvent) {
        CloseHandle(stop_event);
        CloseHandle(pipe);
        return false;
    }
    const auto connected_immediately = ConnectNamedPipe(pipe, &connection) != FALSE;
    const auto connect_error = connected_immediately ? ERROR_SUCCESS : GetLastError();
    auto command = quote(host.wstring()) + L" --pipe " + quote(pipe_name) +
        L" --stop-event " + quote(stop_name) + L" --name \"Protocol Smoke\"" +
        L" --parent-pid " + std::to_wstring(GetCurrentProcessId()) +
        L" --width 1280 --height 720 --fps 30 --mode " + std::wstring(mode) +
        L" --raop-port " + std::to_wstring(raop_port) +
        L" --airplay-port " + std::to_wstring(airplay_port) +
        L" --dlna-port " + std::to_wstring(dlna_port) +
        L" --dlna-ssdp-port " + std::to_wstring(ssdp_port);
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    const auto working_directory = host.parent_path().wstring();
    const auto started = CreateProcessW(host.c_str(), command.data(), nullptr,
        nullptr, FALSE, CREATE_NO_WINDOW, nullptr, working_directory.c_str(),
        &startup, &process) != FALSE;
    if (!started) {
        CancelIoEx(pipe, &connection);
        CloseHandle(connection.hEvent);
        CloseHandle(stop_event);
        CloseHandle(pipe);
        return false;
    }
    CloseHandle(process.hThread);
    DWORD connected_bytes{};
    const auto connected = connected_immediately || connect_error == ERROR_PIPE_CONNECTED ||
        (connect_error == ERROR_IO_PENDING &&
            wait_overlapped(pipe, connection, 10000, connected_bytes));
    PipeCapture pipe_capture;
    std::thread pipe_reader;
    if (connected) pipe_reader = std::thread([&] { drain_pipe(pipe, pipe_capture); });

    const auto server_info_response = tcp_request(airplay_port,
        "GET /server-info HTTP/1.1\r\nHost: 127.0.0.1\r\n"
        "Connection: close\r\n\r\n");
    const auto info_response = tcp_request(raop_port,
        "GET /info RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: AirPlay/550.10\r\n"
        "Content-Length: 0\r\nConnection: close\r\n\r\n");
    const auto server_info_body = response_body(server_info_response);
    const auto info_body = response_body(info_response);
    std::string server_info_xml;
    if (!server_info_body.empty()) {
        server_info_xml.assign(
            reinterpret_cast<const char*>(server_info_body.data()),
            server_info_body.size());
    }
    const auto device_id = plist_string(server_info_xml, "deviceid");
    const auto feature_text = "<key>features</key>\r\n<integer>" +
        std::to_string(features) + "</integer>";
    const std::string server_info_text(server_info_response.begin(),
        server_info_response.end());
    const std::string info_text(info_response.begin(), info_response.end());
    const auto server_status =
        server_info_text.find("HTTP/1.1 200") != std::string::npos;
    const auto info_status = info_text.find("RTSP/1.0 200") != std::string::npos;
    const auto server_features = server_info_xml.find(feature_text) != std::string::npos;
    const auto device_valid = valid_device_id(device_id);
    const BinaryPlist info_plist(info_body);
    const auto info_root = info_plist.root();
    const auto info_features = info_root &&
        plist_integer(info_plist, *info_root, "features") == features;
    const auto info_device = info_root &&
        plist_string(info_plist, *info_root, "deviceID") == device_id &&
        plist_string(info_plist, *info_root, "macAddress") == device_id;
    const auto info_name = info_root &&
        plist_string(info_plist, *info_root, "name") == "Protocol Smoke";
    const auto displays = info_root ?
        info_plist.dictionary_value(*info_root, "displays") : std::nullopt;
    const auto display = displays ? info_plist.array_value(*displays, 0) :
        std::nullopt;
    const auto info_display = display &&
        plist_integer(info_plist, *display, "width") == 1280 &&
        plist_integer(info_plist, *display, "height") == 720 &&
        plist_integer(info_plist, *display, "widthPixels") == 1280 &&
        plist_integer(info_plist, *display, "heightPixels") == 720 &&
        plist_integer(info_plist, *display, "maxFPS") == 30 &&
        plist_integer(info_plist, *display, "refreshRate") == 30;
    const auto media_route_passed = !route_address ||
        probe_media_route(process.dwProcessId, raop_port, *route_address);
    auto passed = connected && server_status && info_status && server_features &&
        device_valid && info_plist.valid() && info_features && info_device &&
        info_name && info_display && media_route_passed;

    SetEvent(stop_event);
    const auto exited = WaitForSingleObject(process.hProcess, 10000) == WAIT_OBJECT_0;
    if (!exited) {
        TerminateProcess(process.hProcess, 1);
        WaitForSingleObject(process.hProcess, 5000);
    }
    if (pipe_reader.joinable()) pipe_reader.join();
    DWORD exit_code{STILL_ACTIVE};
    GetExitCodeProcess(process.hProcess, &exit_code);
    passed = passed && pipe_capture.protocol_valid &&
        !pipe_capture.environment_sync_failed &&
        !pipe_capture.runtime_device_id_invalid && exited && exit_code == 0;
    std::cout << "mode=" << (mode == L"combined" ? "combined" : "mirror")
        << " features=0x" << std::hex << features << std::dec
        << " connected=" << connected
        << " server_status=" << server_status << " info_status=" << info_status
        << " server_features=" << server_features
        << " info_features=" << info_features
        << " identity_match=" << (device_valid && info_device)
        << " capability_match=" << (info_name && info_display)
        << " media_route=" << media_route_passed
        << " environment_sync=" << !pipe_capture.environment_sync_failed
        << " exited=" << exited << " exit_code=" << exit_code
        << " passed=" << passed << '\n';

    CloseHandle(process.hProcess);
    CloseHandle(connection.hEvent);
    CloseHandle(stop_event);
    CloseHandle(pipe);
    return passed;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    const auto media_route_only = argc == 3 &&
        std::wstring_view(argv[2]) == L"--media-route";
    if (argc != 2 && !media_route_only) return 2;
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 2;
    constexpr std::array variables{
        L"IPHONE_MIRROR_AIRPLAY_WIDTH", L"IPHONE_MIRROR_AIRPLAY_HEIGHT",
        L"IPHONE_MIRROR_AIRPLAY_FPS", L"IPHONE_MIRROR_AIRPLAY_MODE",
        L"IPHONE_MIRROR_AIRPLAY_NAME", L"IPHONE_MIRROR_AIRPLAY_DEVICE_ID",
        L"IPHONE_MIRROR_AIRPLAY_PAIRING_SEED", L"IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY",
    };
    bool environment_clean{true};
    for (const auto* variable : variables) {
        environment_clean = SetEnvironmentVariableW(variable, nullptr) != FALSE &&
            GetEnvironmentVariableW(variable, nullptr, 0) == 0 && environment_clean;
    }
    const auto host = std::filesystem::absolute(argv[1]);
    if (media_route_only) {
        const auto route_address = physical_ipv4_address();
        if (!route_address) {
            std::cout << "Wireless receiver media-route smoke skipped: no "
                "preferred non-loopback Wi-Fi or Ethernet IPv4 address\n";
            WSACleanup();
            return 77;
        }
        const auto passed = environment_clean &&
            probe_mode(host, L"mirror", 0x5A7FFEE6U, route_address);
        WSACleanup();
        if (!passed) {
            std::cerr << "Wireless receiver media-route smoke failed: "
                "environment_clean=" << environment_clean << '\n';
            return 1;
        }
        std::cout << "Wireless receiver media-route smoke passed\n";
        return 0;
    }
    const auto passed = environment_clean &&
        probe_mode(host, L"mirror", 0x5A7FFEE6U) &&
        probe_mode(host, L"combined", 0x5A7FFEF7U);
    WSACleanup();
    if (!passed) {
        std::cerr << "Wireless receiver protocol smoke failed: environment_clean="
            << environment_clean << '\n';
        return 1;
    }
    std::cout << "Wireless receiver protocol smoke passed\n";
    return 0;
}
