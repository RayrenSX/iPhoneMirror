#pragma once

#include <WinSock2.h>

#include <cstdint>
#include <span>
#include <stdexcept>
#include <vector>

namespace iPhoneMirror::transport {

class SocketError final : public std::runtime_error {
public:
    SocketError(const char* operation, int error);
    [[nodiscard]] int code() const noexcept { return code_; }
private:
    int code_;
};

class Socket {
public:
    Socket() noexcept = default;
    explicit Socket(SOCKET handle) noexcept : handle_(handle) {}
    ~Socket();

    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
    Socket(Socket&& other) noexcept;
    Socket& operator=(Socket&& other) noexcept;

    [[nodiscard]] static Socket connect_loopback(std::uint16_t port, int timeout_ms = 750);
    [[nodiscard]] static bool probe_loopback(std::uint16_t port, int timeout_ms = 250) noexcept;

    void set_timeout(int timeout_ms);
    void send_all(std::span<const std::uint8_t> bytes);
    [[nodiscard]] std::vector<std::uint8_t> receive_exact(std::size_t length);
    [[nodiscard]] std::size_t receive(std::span<std::uint8_t> destination);

    [[nodiscard]] bool valid() const noexcept { return handle_ != INVALID_SOCKET; }
    [[nodiscard]] SOCKET native_handle() const noexcept { return handle_; }
    void close() noexcept;

private:
    SOCKET handle_{INVALID_SOCKET};
};

void ensure_winsock();

} // namespace iPhoneMirror::transport

