// Exercise the production HTTP reader with ephemeral loopback socket pairs.
// No renderer, SSDP, WirelessHost process, or AirPlay SDK is started.
#include <WinSock2.h>
#include <algorithm>
#include <atomic>

namespace {
// Force a short first receive for the boundary test, without sleeps or TCP
// packet assumptions. All reads still use the real loopback Winsock transport.
std::atomic_int first_receive_limit{};
int segmented_recv(SOCKET socket, char* buffer, int size, int flags) {
    const auto limit = first_receive_limit.exchange(0);
    return ::recv(socket, buffer, limit > 0 ? (std::min)(size, limit) : size, flags);
}
}

#define recv segmented_recv
#include "../DlnaRenderer.cpp"
#undef recv

#include <iostream>
#include <stdexcept>

namespace {
using iPhoneMirror::wireless::HttpReadFailure;
using iPhoneMirror::wireless::HttpReadResult;

void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

class Socket final {
public:
    explicit Socket(SOCKET value = INVALID_SOCKET) : value_(value) {}
    ~Socket() { if (value_ != INVALID_SOCKET) closesocket(value_); }
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
    SOCKET get() const { return value_; }
    void reset(SOCKET value) {
        if (value_ != INVALID_SOCKET) closesocket(value_);
        value_ = value;
    }
private:
    SOCKET value_;
};

class SocketPair final {
public:
    SocketPair() {
        const Socket listener(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
        require(listener.get() != INVALID_SOCKET, "create loopback listener");
        sockaddr_in address{AF_INET, 0, {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
        require(bind(listener.get(), reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == 0 && listen(listener.get(), 1) == 0,
            "bind ephemeral loopback");
        int length = sizeof(address);
        require(getsockname(listener.get(), reinterpret_cast<sockaddr*>(&address),
            &length) == 0, "read ephemeral loopback port");
        client_.reset(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
        require(client_.get() != INVALID_SOCKET && connect(client_.get(),
            reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0,
            "connect ephemeral loopback");
        server_.reset(accept(listener.get(), nullptr, nullptr));
        require(server_.get() != INVALID_SOCKET, "accept ephemeral loopback");
        u_long nonblocking = 1;
        require(ioctlsocket(server_.get(), FIONBIO, &nonblocking) == 0,
            "set nonblocking reader");
    }

    HttpReadResult request(std::string_view bytes, bool close_request = true) {
        while (!bytes.empty()) {
            const auto sent = send(client_.get(), bytes.data(),
                static_cast<int>(bytes.size()), 0);
            require(sent > 0, "send test request");
            bytes.remove_prefix(static_cast<std::size_t>(sent));
        }
        if (close_request) shutdown(client_.get(), SD_SEND);
        return iPhoneMirror::wireless::read_request(server_.get(), stopping_);
    }
private:
    Socket client_;
    Socket server_;
    std::atomic_bool stopping_{};
};

HttpReadResult read(std::string_view wire) {
    SocketPair pair;
    return pair.request(wire);
}

void expect_failure(std::string_view wire, HttpReadFailure failure) {
    const auto result = read(wire);
    require(!result.request && result.failure == failure, "incorrect failure classification");
    require(result.received_bytes <= wire.size() &&
        (wire.empty() || result.received_bytes != 0), "incorrect rejected request byte count");
    if (failure == HttpReadFailure::EmptyEof ||
        failure == HttpReadFailure::IncompleteHeaders ||
        failure == HttpReadFailure::IncompleteBody)
        require(result.received_bytes == wire.size(), "EOF byte count lost received data");
}

void test_list_headers() {
    struct ListField {
        std::string_view name;
        std::string_view first;
        std::string_view second;
    };
    constexpr std::array<ListField, 6> fields{{
        {"Accept", "text/xml", "application/xml"},
        {"Accept-Encoding", "gzip", "identity"},
        {"Accept-Language", "en", "zh"},
        {"Cache-Control", "no-cache", "max-age=0"},
        {"Connection", "keep-alive", "close"},
        {"Pragma", "no-cache", "no-cache"}}};
    for (const auto& field : fields) {
        const auto wire = std::format("GET /dlna/device.xml HTTP/1.1\r\n"
            "Host: localhost\r\n{}: {}\r\n{}: {}\r\n\r\n", field.name,
            field.first, iPhoneMirror::wireless::lower(field.name), field.second);
        const auto result = read(wire);
        require(result.request.has_value(), "repeatable list header was rejected");
        require(result.request->headers.at(iPhoneMirror::wireless::lower(field.name)) ==
            std::format("{}, {}", field.first, field.second),
            "list header order or values were lost");
        require(result.failure == HttpReadFailure::None &&
            result.received_bytes == wire.size(), "incorrect successful request diagnostics");
    }
    for (const auto name : {"Host", "SOAPACTION", "CALLBACK", "SID", "X-Unknown"}) {
        expect_failure(std::format("GET /dlna/device.xml HTTP/1.1\r\n"
            "{}: first\r\n{}: second\r\n\r\n", name, name),
            HttpReadFailure::DuplicateHeader);
    }
}

void test_framing_and_body() {
    const auto basic = read("GET /dlna/device.xml HTTP/1.1\r\nHost: localhost\r\n\r\n");
    require(basic.request && basic.request->method == "GET" &&
        basic.request->path == "/dlna/device.xml", "basic GET did not parse");
    const auto identical = read("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Content-Length: 1\r\nContent-Length: 1\r\n\r\nx");
    require(identical.request && identical.request->body == "x",
        "identical Content-Length compatibility was lost");
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Content-Length: 1\r\nContent-Length: 2\r\n\r\nxx",
        HttpReadFailure::DuplicateHeader);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n",
        HttpReadFailure::DuplicateHeader);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Content-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n",
        HttpReadFailure::AmbiguousFraming);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Content-Length: invalid\r\n\r\n", HttpReadFailure::InvalidContentLength);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Transfer-Encoding: gzip\r\n\r\n", HttpReadFailure::InvalidTransferEncoding);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Content-Length: 4\r\n\r\nabc", HttpReadFailure::IncompleteBody);
    const auto chunked = read("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Transfer-Encoding: chunked\r\n\r\n1\r\nx\r\n0\r\nX-Checksum: ok\r\n\r\n");
    require(chunked.request && chunked.request->body == "x", "chunked trailer did not parse");
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Transfer-Encoding: chunked\r\n\r\n1\r\nx", HttpReadFailure::IncompleteBody);
    expect_failure("POST /dlna/control/avtransport HTTP/1.1\r\n"
        "Transfer-Encoding: chunked\r\n\r\nno\r\n", HttpReadFailure::InvalidChunk);
}

void test_empty_and_incomplete_connections() {
    expect_failure("", HttpReadFailure::EmptyEof);
    expect_failure("GET /dlna/device.xml HTTP/1.1\r\nHost: local",
        HttpReadFailure::IncompleteHeaders);
    expect_failure("invalid\r\n\r\n", HttpReadFailure::InvalidRequestLine);
    expect_failure("GET /dlna/device.xml HTTP/1.1\r\n: value\r\n\r\n",
        HttpReadFailure::InvalidHeader);
    std::atomic_bool stopped{true};
    const auto result = iPhoneMirror::wireless::read_request(INVALID_SOCKET, stopped);
    require(!result.request && result.failure == HttpReadFailure::Stopped &&
        result.received_bytes == 0, "shutdown was misclassified as a parser failure");
    SocketPair idle;
    const auto started = std::chrono::steady_clock::now();
    const auto timeout = idle.request("", false);
    require(!timeout.request && timeout.failure == HttpReadFailure::Timeout &&
        timeout.received_bytes == 0, "idle timeout was misclassified as empty EOF");
    require(std::chrono::steady_clock::now() - started < std::chrono::seconds(7),
        "idle request exceeded its finite deadline");
}

void test_header_limit() {
    constexpr std::size_t limit = 64U * 1024U;
    constexpr std::string_view prefix =
        "GET /dlna/device.xml HTTP/1.1\r\nHost: localhost\r\nX-Padding: ";
    const auto padded_header = [&](std::size_t size) {
        return std::string(prefix) + std::string(size - prefix.size() - 4U, 'a') +
            "\r\n\r\n";
    };
    const auto at_limit = read(padded_header(limit));
    require(at_limit.request.has_value(), "header exactly at limit was rejected");
    first_receive_limit.store(1);
    const auto over_limit = read(padded_header(limit + 1));
    require(!over_limit.request && over_limit.failure == HttpReadFailure::HeaderLimit,
        "split receive admitted header over limit");
    require(over_limit.received_bytes == limit + 1,
        "boundary test did not exercise a receive crossing the header limit");
    const std::string body(limit + 100U, 'b');
    const auto larger_body = read(std::format(
        "POST /dlna/control/avtransport HTTP/1.1\r\nContent-Length: {}\r\n\r\n{}",
        body.size(), body));
    require(larger_body.request && larger_body.request->body == body,
        "body bytes were incorrectly counted against the header limit");
}
}

int main() {
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) return 2;
    try {
        test_list_headers();
        test_framing_and_body();
        test_header_limit();
        test_empty_and_incomplete_connections();
    } catch (const std::exception& error) {
        std::cerr << "DLNA HTTP request test failed: " << error.what() << '\n';
        WSACleanup();
        return 1;
    }
    WSACleanup();
    std::cout << "DLNA HTTP list headers, framing and failure diagnostics passed\n";
    return 0;
}
