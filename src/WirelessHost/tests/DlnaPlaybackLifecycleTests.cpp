// Test the real renderer state and publication boundaries without starting
// the receiver or its HTTP/SSDP listeners.
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <string_view>
#define private public
#include "../DlnaRenderer.h"
#undef private
#include "../DlnaRenderer.cpp"

#include <cstdio>
#include <future>
#include <stdexcept>

namespace {
using namespace iPhoneMirror::wireless;

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

HttpRequest action(std::string_view name, std::string body = {}) {
    HttpRequest request;
    request.method = "POST";
    request.path = "/dlna/control/avtransport";
    request.headers.emplace("soapaction", std::string(AvTransportType) + "#" + std::string(name));
    request.body = std::move(body);
    return request;
}

void test_control_invalidates_terminal_before_callback() {
    for (const auto control : {"Seek", "Pause", "Resume"}) {
        DlnaRenderer renderer;
        auto& impl = *renderer.impl_;
        impl.media_uri = "https://example.invalid/episode.mp4";
        impl.transport_generation = 10;
        impl.transport_state = std::string_view(control) == "Resume"
            ? "PAUSED_PLAYBACK" : "PLAYING";
        std::promise<void> callback_entered;
        std::promise<void> release_callback;
        auto release = release_callback.get_future();
        std::uint64_t control_generation{};
        const auto block = [&](std::uint64_t generation) {
            control_generation = generation;
            callback_entered.set_value();
            release.wait();
        };
        impl.callbacks.pause = block;
        impl.callbacks.resume = block;
        impl.callbacks.seek = [&](double, std::uint64_t generation) { block(generation); };
        auto request = std::string_view(control) == "Seek"
            ? action("Seek", "<Target>00:00:10</Target>")
            : action(std::string_view(control) == "Resume" ? "Play" : control);
        auto incoming_control = std::async(std::launch::async,
            [&] { impl.handle_soap(request); });
        callback_entered.get_future().wait();
        // The renderer has changed state, but its callback has deliberately
        // not reached the Host command lock. An already accepted EOF is old.
        const auto stale_applied = renderer.update_transport_playback(10, "STOPPED");
        const auto expected = std::string_view(control) == "Pause"
            ? "PAUSED_PLAYBACK" : "PLAYING";
        const auto state_preserved = impl.transport_state == expected;
        release_callback.set_value();
        incoming_control.get();
        require(control_generation != 10 && !stale_applied && state_preserved,
            "a control must invalidate an older EOF before waiting for its Host callback");
    }
}

void test_control_callbacks_are_ordered() {
    DlnaRenderer renderer;
    auto& impl = *renderer.impl_;
    impl.media_uri = "https://example.invalid/episode.mp4";
    impl.transport_generation = 10;
    impl.transport_state = "PLAYING";
    std::promise<void> old_callback_entered;
    std::promise<void> release_old_callback;
    auto release = release_old_callback.get_future();
    std::atomic_uint64_t host_generation{};
    impl.callbacks.seek = [&](double, std::uint64_t generation) {
        old_callback_entered.set_value();
        release.wait();
        host_generation = generation;
    };
    impl.callbacks.play = [&](std::string_view, double, double, double, bool,
        std::uint64_t generation) { host_generation = generation; };
    auto old_seek = std::async(std::launch::async, [&] {
        impl.handle_soap(action("Seek", "<Target>00:00:10</Target>"));
    });
    old_callback_entered.get_future().wait();
    auto next_play = std::async(std::launch::async,
        [&] { impl.handle_soap(action("Play")); });
    const auto next_play_waited = next_play.wait_for(std::chrono::milliseconds(100)) ==
        std::future_status::timeout;
    release_old_callback.set_value();
    old_seek.get();
    next_play.get();
    require(next_play_waited && host_generation == impl.transport_generation,
        "an older SOAP callback cannot overwrite a newer Play generation in the Host");
}

void test_local_replay_and_pause_after_terminal() {
    DlnaRenderer renderer;
    auto& impl = *renderer.impl_;
    impl.transport_generation = 10;
    impl.transport_state = "PLAYING";
    require(renderer.update_transport_playback(10, "STOPPED"), "finish first item");
    impl.handle_soap(action("Pause"));
    require(impl.transport_state == "STOPPED",
        "Pause after a terminal item must leave the DLNA transport stopped");
    const auto generation = impl.transport_generation;
    require(renderer.update_transport_playback(generation, "PLAYING") &&
        renderer.update_transport_playback(generation, "STOPPED"),
        "a same-command local replay must revive the transport and finish again");
}

void test_stop_callbacks_carry_the_invalidating_revision() {
    for (const auto control : {"Stop", "SetAVTransportURI"}) {
        DlnaRenderer renderer;
        auto& impl = *renderer.impl_;
        impl.transport_generation = 10;
        impl.transport_state = "PLAYING";
        std::uint64_t stopped_generation{};
        impl.callbacks.stop = [&](std::uint64_t generation) {
            stopped_generation = generation;
        };
        impl.handle_soap(action(control, std::string_view(control) == "SetAVTransportURI"
            ? "<CurrentURI>https://example.invalid/next.mp4</CurrentURI>" : ""));
        require(stopped_generation == 11 && impl.transport_generation == 11 &&
            impl.transport_state == "STOPPED",
            "Stop and SetURI must carry their invalidating revision to the Host");
    }
}

class EventListener {
public:
    EventListener() {
        socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        require(socket_ != INVALID_SOCKET, "create event listener");
        sockaddr_in address{AF_INET, 0, {.S_un = {.S_addr = htonl(INADDR_LOOPBACK)}}};
        int size = sizeof(address);
        require(bind(socket_, reinterpret_cast<sockaddr*>(&address), size) == 0 &&
            listen(socket_, 4) == 0 &&
            getsockname(socket_, reinterpret_cast<sockaddr*>(&address), &size) == 0,
            "bind event listener");
        port = ntohs(address.sin_port);
    }
    ~EventListener() { if (socket_ != INVALID_SOCKET) closesocket(socket_); }
    bool pending(int timeout) const {
        WSAPOLLFD descriptor{.fd = socket_, .events = POLLRDNORM};
        return WSAPoll(&descriptor, 1, timeout) > 0;
    }
    std::string receive() const {
        require(pending(3000), "notification never arrived");
        const auto client = accept(socket_, nullptr, nullptr);
        require(client != INVALID_SOCKET, "accept event callback");
        DWORD timeout = 3000;
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO,
            reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        std::string message;
        std::array<char, 4096> bytes{};
        for (;;) {
            const auto count = recv(client, bytes.data(), static_cast<int>(bytes.size()), 0);
            if (count <= 0) break;
            message.append(bytes.data(), count);
        }
        closesocket(client);
        return message;
    }
    std::uint16_t port{};
private:
    SOCKET socket_{INVALID_SOCKET};
};

void test_publication_snapshots_after_serialization() {
    DlnaRenderer renderer;
    auto& impl = *renderer.impl_;
    EventListener listener;
    impl.transport_generation = 10;
    impl.transport_state = "STOPPED";
    impl.subscriptions.push_back({"test-event", "avtransport",
        {"127.0.0.1", listener.port, "/events"}, 1,
        std::chrono::steady_clock::now() + std::chrono::minutes(1)});

    // Hold a preceding publication at the boundary. A delayed EOF cannot
    // snapshot STOPPED before taking this lock and later use a newer SEQ.
    std::unique_lock publication(impl.event_publication_mutex);
    std::promise<void> waiting;
    auto old_eof = std::async(std::launch::async, [&] {
        waiting.set_value();
        renderer.notify_transport_changed();
    });
    waiting.get_future().wait();
    const auto published_while_locked = listener.pending(100);
    {
        std::scoped_lock state(impl.state_mutex);
        ++impl.transport_generation;
        impl.transport_state = "PLAYING";
    }
    auto next_play = std::async(std::launch::async,
        [&] { renderer.notify_transport_changed(); });
    publication.unlock();
    const auto first = listener.receive();
    const auto second = listener.receive();
    old_eof.get();
    next_play.get();
    require(!published_while_locked &&
        first.find("SEQ: 1\r\n") != std::string::npos &&
        second.find("SEQ: 2\r\n") != std::string::npos &&
        first.find("TransportState val=&quot;PLAYING&quot;") != std::string::npos &&
        second.find("TransportState val=&quot;PLAYING&quot;") != std::string::npos,
        "publication must snapshot current state and send in the same order as its SEQ");
}
}

int main() {
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 1;
    try {
        test_control_invalidates_terminal_before_callback();
        test_control_callbacks_are_ordered();
        test_local_replay_and_pause_after_terminal();
        test_stop_callbacks_carry_the_invalidating_revision();
        test_publication_snapshots_after_serialization();
        WSACleanup();
        std::puts("DLNA playback lifecycle tests passed.");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s\n", error.what());
        WSACleanup();
        return 1;
    }
}
