#include "Transport/Socket.h"

#include <WS2tcpip.h>

#include <algorithm>
#include <format>
#include <mutex>

namespace iPhoneMirror::transport {
namespace {

std::once_flag winsock_once;
int winsock_error{};

} // namespace

SocketError::SocketError(const char* operation, int error)
    : std::runtime_error(std::format("{} failed (Winsock {})", operation, error)), code_(error) {}

void ensure_winsock() {
    std::call_once(winsock_once, [] {
        WSADATA data{};
        winsock_error = WSAStartup(MAKEWORD(2, 2), &data);
    });
    if (winsock_error != 0) throw SocketError("WSAStartup", winsock_error);
}

Socket::~Socket() { close(); }

Socket::Socket(Socket&& other) noexcept : handle_(other.handle_) { other.handle_ = INVALID_SOCKET; }

Socket& Socket::operator=(Socket&& other) noexcept {
    if (this != &other) {
        close();
        handle_ = other.handle_;
        other.handle_ = INVALID_SOCKET;
    }
    return *this;
}

Socket Socket::connect_loopback(std::uint16_t port, int timeout_ms) {
    ensure_winsock();
    const SOCKET handle = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (handle == INVALID_SOCKET) throw SocketError("socket", WSAGetLastError());
    Socket result(handle);

    u_long nonblocking = 1;
    if (ioctlsocket(handle, FIONBIO, &nonblocking) == SOCKET_ERROR) {
        throw SocketError("ioctlsocket", WSAGetLastError());
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    int status = ::connect(handle, reinterpret_cast<const sockaddr*>(&address), sizeof(address));
    if (status == SOCKET_ERROR) {
        const int error = WSAGetLastError();
        if (error != WSAEWOULDBLOCK && error != WSAEINPROGRESS) throw SocketError("connect", error);

        fd_set write_set;
        FD_ZERO(&write_set);
        FD_SET(handle, &write_set);
        timeval timeout{ timeout_ms / 1000, (timeout_ms % 1000) * 1000 };
        status = select(0, nullptr, &write_set, nullptr, &timeout);
        if (status == 0) throw SocketError("connect timeout", WSAETIMEDOUT);
        if (status == SOCKET_ERROR) throw SocketError("select", WSAGetLastError());
        int socket_error{};
        int size = sizeof(socket_error);
        if (getsockopt(handle, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&socket_error), &size) == SOCKET_ERROR) {
            throw SocketError("getsockopt", WSAGetLastError());
        }
        if (socket_error != 0) throw SocketError("connect", socket_error);
    }

    nonblocking = 0;
    if (ioctlsocket(handle, FIONBIO, &nonblocking) == SOCKET_ERROR) {
        throw SocketError("ioctlsocket", WSAGetLastError());
    }
    result.set_timeout(timeout_ms);
    return result;
}

bool Socket::probe_loopback(std::uint16_t port, int timeout_ms) noexcept {
    try {
        auto socket = connect_loopback(port, timeout_ms);
        return socket.valid();
    } catch (...) {
        return false;
    }
}

void Socket::set_timeout(int timeout_ms) {
    if (setsockopt(handle_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms)) == SOCKET_ERROR ||
        setsockopt(handle_, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms)) == SOCKET_ERROR) {
        throw SocketError("setsockopt", WSAGetLastError());
    }
}

void Socket::send_all(std::span<const std::uint8_t> bytes) {
    std::size_t offset{};
    while (offset < bytes.size()) {
        const int amount = static_cast<int>(std::min<std::size_t>(bytes.size() - offset, 1U << 20));
        const int sent = ::send(handle_, reinterpret_cast<const char*>(bytes.data() + offset), amount, 0);
        if (sent == SOCKET_ERROR) throw SocketError("send", WSAGetLastError());
        if (sent == 0) throw SocketError("send closed", WSAECONNRESET);
        offset += static_cast<std::size_t>(sent);
    }
}

std::vector<std::uint8_t> Socket::receive_exact(std::size_t length) {
    std::vector<std::uint8_t> result(length);
    std::size_t offset{};
    while (offset < length) {
        const auto received = receive(std::span(result).subspan(offset));
        if (received == 0) throw SocketError("receive closed", WSAECONNRESET);
        offset += received;
    }
    return result;
}

std::size_t Socket::receive(std::span<std::uint8_t> destination) {
    if (destination.empty()) return 0;
    const int amount = static_cast<int>(std::min<std::size_t>(destination.size(), 1U << 20));
    const int received = ::recv(handle_, reinterpret_cast<char*>(destination.data()), amount, 0);
    if (received == SOCKET_ERROR) throw SocketError("recv", WSAGetLastError());
    return static_cast<std::size_t>(received);
}

void Socket::close() noexcept {
    if (handle_ != INVALID_SOCKET) {
        closesocket(handle_);
        handle_ = INVALID_SOCKET;
    }
}

} // namespace iPhoneMirror::transport

