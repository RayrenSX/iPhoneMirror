#include "Transport/Socket.h"

#include <algorithm>
#include <array>
#include <iostream>
#include <stdexcept>

using iPhoneMirror::transport::Socket;
using iPhoneMirror::transport::SocketError;

void check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

int main() {
    try {
        iPhoneMirror::transport::ensure_winsock();
        // Reserve an ephemeral port without listening so no other test can
        // claim it between choosing the port and the refused connection.
        Socket server(::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
        check(server.valid(), "create loopback socket");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        check(bind(server.native_handle(), reinterpret_cast<sockaddr*>(&address),
            sizeof(address)) == 0, "bind loopback port");
        int size = sizeof(address);
        check(getsockname(server.native_handle(), reinterpret_cast<sockaddr*>(&address),
            &size) == 0, "read assigned port");
        const auto port = ntohs(address.sin_port);
        try {
            // Windows may retry a refused loopback SYN before surfacing the
            // error. Leave enough time for that OS policy; verify the error
            // code, without imposing a machine-dependent latency threshold.
            auto rejected = Socket::connect_loopback(port, 4000);
            check(false, "non-listening socket must reject connections");
        } catch (const SocketError& error) {
            check(error.code() == WSAECONNREFUSED,
                "refusal must report WSAECONNREFUSED rather than waiting for WSAETIMEDOUT");
        }
        check(!Socket::probe_loopback(port), "absent service is unavailable");
        for (const auto timeout : {0, -1}) {
            try {
                auto invalid = Socket::connect_loopback(port, timeout);
                check(false, "non-positive timeout must fail");
            } catch (const SocketError& error) {
                check(error.code() == WSAEINVAL, "validate timeout before connecting");
            }
        }
        check(listen(server.native_handle(), 1) == 0, "listen on reserved port");
        auto client = Socket::connect_loopback(port, 1000);
        Socket peer(accept(server.native_handle(), nullptr, nullptr));
        check(peer.valid(), "accept successful connection");
        peer.set_timeout(1000);
        const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};
        client.send_all(payload);
        const auto received = peer.receive_exact(payload.size());
        check(std::equal(payload.begin(), payload.end(), received.begin()),
            "successful connect retains blocking send/receive behavior");
        std::cout << "PASS loopback refusal, invalid timeouts and successful transfer\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
