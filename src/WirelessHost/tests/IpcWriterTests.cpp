// Exercise the production writer with anonymous-pipe backpressure. No receiver
// service, socket, or AirPlay SDK is started by these tests.
#define wmain wireless_host_entry_not_called
#include "../WirelessHost.cpp"
#undef wmain

#include <cstdio>
#include <future>
#include <memory>
#include <stdexcept>

namespace {

void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

class PipeFixture {
public:
    PipeFixture() {
        require(CreatePipe(&reader_, &writer_pipe_, nullptr, 4096) != FALSE,
            "create anonymous pipe");
        writer = std::make_unique<IpcWriter>(writer_pipe_);
    }
    ~PipeFixture() {
        // Unblock any write even if a test failed before draining the pipe.
        if (reader_) CloseHandle(reader_);
        writer.reset();
        if (writer_pipe_) CloseHandle(writer_pipe_);
    }

    void wait_for_write() const {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
        while (available() == 0) {
            require(std::chrono::steady_clock::now() < deadline,
                "writer did not enter the pipe");
            Sleep(1);
        }
    }

    struct Message {
        iPhoneMirror::wireless::MessageHeader header;
        std::vector<std::uint8_t> payload;
    };

    Message receive() const {
        Message message;
        read(&message.header, sizeof(message.header));
        require(message.header.magic == iPhoneMirror::wireless::IpcMagic &&
            message.header.payload_size <= iPhoneMirror::wireless::MaxPayloadBytes,
            "invalid IPC header");
        message.payload.resize(message.header.payload_size);
        read(message.payload.data(), message.payload.size());
        return message;
    }

    std::unique_ptr<IpcWriter> writer;

private:
    DWORD available() const {
        DWORD count{};
        require(PeekNamedPipe(reader_, nullptr, 0, nullptr, &count, nullptr) != FALSE,
            "peek anonymous pipe");
        return count;
    }

    void read(void* destination, std::size_t size) const {
        auto* bytes = static_cast<std::uint8_t*>(destination);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
        while (size != 0) {
            require(std::chrono::steady_clock::now() < deadline,
                "timed out waiting for the final IPC frame");
            const DWORD count = static_cast<DWORD>((std::min)(size,
                static_cast<std::size_t>(available())));
            if (count == 0) {
                Sleep(1);
                continue;
            }
            DWORD received{};
            require(ReadFile(reader_, bytes, count, &received, nullptr) != FALSE &&
                received != 0, "read anonymous pipe");
            bytes += received;
            size -= received;
        }
    }

    HANDLE reader_{};
    HANDLE writer_pipe_{};
};

iPhoneMirror::wireless::MessageHeader video_header(const char* device) {
    iPhoneMirror::wireless::MessageHeader header;
    header.type = iPhoneMirror::wireless::MessageType::Video;
    header.width = 128;
    header.height = 128;
    strcpy_s(header.device_id, device);
    return header;
}

void check_frame(const PipeFixture::Message& message, const char* device,
    const std::vector<std::uint8_t>& payload) {
    require(message.header.type == iPhoneMirror::wireless::MessageType::Video &&
        std::strcmp(message.header.device_id, device) == 0,
        "preserve the frame device");
    require(message.payload == payload, "preserve all pixels of the final frame");
}

void test_final_frame_while_writing() {
    PipeFixture fixture;
    const auto header = video_header("device-a");
    const std::vector<std::uint8_t> first(128U * 128U * 3U / 2U, 1);
    const std::vector<std::uint8_t> final(first.size(), 2);
    require(fixture.writer->send(header, first), "send first video");
    fixture.wait_for_write();
    require(fixture.writer->send(header, final), "send final same-size video");
    require(fixture.writer->send_text(iPhoneMirror::wireless::MessageType::Log,
        "drained"), "send drain marker");

    check_frame(fixture.receive(), "device-a", first);
    check_frame(fixture.receive(), "device-a", final);
    require(fixture.receive().header.type == iPhoneMirror::wireless::MessageType::Log,
        "all video frames precede the drain marker");
}

void test_latest_pending_frame_per_device() {
    PipeFixture fixture;
    auto header = video_header("device-a");
    const std::vector<std::uint8_t> first(128U * 128U * 3U / 2U, 1);
    std::vector<std::uint8_t> final(first.size());
    const std::vector<std::uint8_t> other(first.size(), 222);
    require(fixture.writer->send(header, first), "send first video");
    fixture.wait_for_write();
    require(fixture.writer->send(video_header("device-b"), other),
        "queue another device");
    for (std::uint8_t index = 2; index <= 64; ++index) {
        // Include geometry changes while the original frame is blocked.
        header.width = index % 2 == 0 ? 128U : 64U;
        header.height = index % 2 == 0 ? 128U : 256U;
        std::ranges::fill(final, index);
        require(fixture.writer->send(header, final), "replace pending video");
    }
    require(fixture.writer->send_text(iPhoneMirror::wireless::MessageType::Log,
        "drained"), "send drain marker");
    check_frame(fixture.receive(), "device-a", first);
    check_frame(fixture.receive(), "device-b", other);
    const auto received = fixture.receive();
    check_frame(received, "device-a", final);
    require(received.header.width == header.width && received.header.height == header.height,
        "preserve final geometry");
    require(fixture.receive().header.type == iPhoneMirror::wireless::MessageType::Log,
        "coalesce obsolete frames without delaying control messages");
}

void test_explicit_playback_state_and_item_identity() {
    using namespace iPhoneMirror::wireless;
    PipeFixture fixture;
    AirPlayCallback callback(*fixture.writer);
    char url[]="https://example.invalid/item.m3u8";
    callback.videoPlay(url,1,0);
    const auto receive_command=[&](MessageType type) {
        for (;;) {
            const auto message=fixture.receive();
            if (message.header.type==type) return message.header.media_command_id;
        }
    };
    const auto first=receive_command(MessageType::MediaPlay);
    unsigned long long item{};
    require(callback.videoGetPlayState(&item)==3 && item==first,
        "new item starts loading with an explicit item identity");
    MessageHeader state;
    state.type=MessageType::PlaybackState;state.media_command_id=first;
    state.media_duration=6;state.media_position=6;state.reserved=3;
    callback.updatePlayback(state);
    require(callback.videoGetPlayState(&item)==3,
        "zero rate at the duration remains loading, never inferred completion");
    state.reserved=6;callback.updatePlayback(state);
    require(callback.videoGetPlayState(&item)==6,"renderer explicitly completes the item without stopping the session");
    callback.mediaVolume(0.5);
    const auto volume=receive_command(MessageType::MediaVolume);
    require(volume!=first && callback.videoGetPlayState(&item)==6 && item==first,
        "volume changes command identity without repeating item completion");
    state.media_command_id=first;state.reserved=1;state.media_rate=1;
    callback.updatePlayback(state);
    require(callback.videoGetPlayState(&item)==6,"late state for old command is ignored");
    callback.videoPlay(url,1,0);
    const auto second=receive_command(MessageType::MediaPlay);
    require(callback.videoGetPlayState(&item)==3 && item==second && second!=first,
        "next item gets a distinct identity even for the same source URL");
    callback.mediaPause();
    receive_command(MessageType::MediaPause);
    require(callback.videoGetPlayState(&item)==2 && item==second,
        "user pause is explicit and preserves the item identity");
    callback.mediaResume();
    receive_command(MessageType::MediaResume);
    require(callback.videoGetPlayState(&item)==3 && item==second,
        "resume waits for decoder playback confirmation without changing item");
}

void test_dlna_completion_routing() {
    using namespace iPhoneMirror::wireless;
    PipeFixture fixture;
    AirPlayCallback callback(*fixture.writer);
    const auto receive_play = [&] {
        for (;;) {
            const auto message = fixture.receive();
            if (message.header.type == MessageType::MediaPlay)
                return message.header.media_command_id;
        }
    };
    MessageHeader state;
    state.type = MessageType::PlaybackState;
    state.reserved = static_cast<std::uint32_t>(MediaPlaybackState::Completed);
    state.media_duration = state.media_position = 60;
    char url[] = "https://example.invalid/episode.mp4";
    callback.videoPlay(url, 1, 0);
    state.media_command_id = receive_play();
    require(callback.updatePlayback(state) == 0,
        "AirPlay completion must not change a DLNA transport");

    callback.playDlnaMedia(url, 60, 1, 0, false, 41);
    const auto first = receive_play();
    require(callback.updatePlayback(state) == 0,
        "completion from a replaced AirPlay item must be ignored");
    state.media_command_id = first;
    require(callback.updatePlayback(state) == 41 &&
        callback.updatePlayback(state) == 0,
        "a current DLNA item completes its generation exactly once");
    callback.mediaVolume(0.5);
    fixture.receive();
    require(callback.updatePlayback(state) == 0,
        "late completion after a newer control must be ignored");

    callback.playDlnaMedia(url, 60, 1, 0, false, 42);
    state.media_command_id = receive_play();
    require(callback.stopPlaybackFromController(state.media_command_id),
        "stop current DLNA item");
    require(callback.updatePlayback(state) == 0,
        "completion after an explicit stop must not be reported");
    callback.videoPlay(url, 1, 0);
    state.media_command_id = receive_play();
    require(callback.updatePlayback(state) == 0,
        "an AirPlay replacement must clear the prior DLNA generation");

    callback.playDlnaMedia(url, 60, 1, 0, false, 43);
    state.media_command_id = receive_play();
    state.reserved = static_cast<std::uint32_t>(MediaPlaybackState::Failed);
    require(callback.updatePlayback(state) == 43 &&
        callback.updatePlayback(state) == 0,
        "a current DLNA failure stops its transport exactly once");
    callback.videoPlay(url, 1, 0);
    state.media_command_id = receive_play();
    require(callback.updatePlayback(state) == 0,
        "AirPlay failure must not change a DLNA transport");
}

void test_terminal_pause_does_not_reopen_item() {
    using namespace iPhoneMirror::wireless;
    for (const auto terminal : {MediaPlaybackState::Completed, MediaPlaybackState::Failed}) {
        PipeFixture fixture;
        AirPlayCallback callback(*fixture.writer);
        char url[] = "https://example.invalid/episode.mp4";
        callback.videoPlay(url, 1, 0);
        std::uint64_t command{};
        while (!command) {
            const auto message = fixture.receive();
            if (message.header.type == MessageType::MediaPlay)
                command = message.header.media_command_id;
        }
        MessageHeader state;
        state.type = MessageType::PlaybackState;
        state.media_command_id = command;
        state.reserved = static_cast<std::uint32_t>(terminal);
        callback.updatePlayback(state);
        callback.mediaPause();
        unsigned long long item{};
        require(callback.videoGetPlayState(&item) == static_cast<int>(terminal) &&
            item == command,
            "Pause after a terminal item must not emit a paused/ended transition again");
        std::uint64_t pause{};
        while (!pause) {
            const auto message = fixture.receive();
            if (message.header.type == MessageType::MediaPause)
                pause = message.header.media_command_id;
        }
        state.media_command_id = pause;
        require(pause != command && callback.updatePlayback(state) == 0,
            "Pause advances the command without reopening the terminal item");
        callback.mediaResume();
        std::uint64_t resume{};
        while (!resume) {
            const auto message = fixture.receive();
            if (message.header.type == MessageType::MediaResume)
                resume = message.header.media_command_id;
        }
        require(callback.videoGetPlayState(&item) ==
            static_cast<int>(MediaPlaybackState::Loading) && item == command,
            "Resume explicitly starts a new attempt of the same item");
        state.media_command_id = resume;
        callback.updatePlayback(state);
        callback.mediaSeek(10);
        require(callback.videoGetPlayState(&item) ==
            static_cast<int>(MediaPlaybackState::Loading) && item == command,
            "Seek explicitly starts a new attempt of the terminal item");
    }
}

void test_transport_commit_is_atomic_with_controls() {
    using namespace iPhoneMirror::wireless;
    PipeFixture fixture;
    AirPlayCallback callback(*fixture.writer);
    callback.playDlnaMedia("https://example.invalid/episode.mp4", 60, 1, 0, false, 41);
    std::uint64_t command{};
    while (!command) {
        const auto message = fixture.receive();
        if (message.header.type == MessageType::MediaPlay)
            command = message.header.media_command_id;
    }
    MessageHeader state;
    state.type = MessageType::PlaybackState;
    state.media_command_id = command;
    state.reserved = static_cast<std::uint32_t>(MediaPlaybackState::Completed);
    std::promise<void> commit_entered;
    std::promise<void> release_commit;
    auto release = release_commit.get_future();
    auto report = std::async(std::launch::async, [&] {
        return callback.updatePlayback(state, [&](std::uint64_t, auto, auto) {
            commit_entered.set_value();
            release.wait();
            return true;
        });
    });
    commit_entered.get_future().wait();
    auto seek = std::async(std::launch::async, [&] { callback.mediaSeek(10, 42); });
    const bool seek_blocked = seek.wait_for(std::chrono::milliseconds(100)) ==
        std::future_status::timeout;
    release_commit.set_value();
    const auto completed_generation = report.get();
    seek.get();
    require(seek_blocked && completed_generation == 41,
        "a newer control cannot be accepted between state validation and commit");
    std::uint64_t seek_command{};
    while (!seek_command) {
        const auto message = fixture.receive();
        if (message.header.type == MessageType::MediaSeek)
            seek_command = message.header.media_command_id;
    }
    bool stale_committed{};
    callback.updatePlayback(state, [&](std::uint64_t, auto, auto) {
        stale_committed = true;
        return true;
    });
    require(!stale_committed, "a stale completion never enters the transport commit");
    state.media_command_id = seek_command;
    state.reserved = static_cast<std::uint32_t>(MediaPlaybackState::Playing);
    const auto resumed_generation = callback.updatePlayback(state,
        [](std::uint64_t generation, auto, auto) { return generation == 42; });
    require(resumed_generation == 42,
        "the accepted control binds subsequent reports to its new DLNA revision");
}

void test_controller_stop_commits_only_its_current_source() {
    using namespace iPhoneMirror::wireless;
    PipeFixture fixture;
    AirPlayCallback callback(*fixture.writer);
    const auto receive_play = [&] {
        for (;;) {
            const auto message = fixture.receive();
            if (message.header.type == MessageType::MediaPlay)
                return message.header.media_command_id;
        }
    };
    char url[] = "https://example.invalid/episode.mp4";
    callback.playDlnaMedia(url, 60, 1, 0, false, 41);
    const auto command = receive_play();
    std::promise<void> commit_entered;
    std::promise<void> release_commit;
    auto release = release_commit.get_future();
    std::uint64_t stopped_generation{};
    auto stop = std::async(std::launch::async, [&] {
        return callback.stopPlaybackFromController(command,
            [&](std::uint64_t generation) {
                commit_entered.set_value();
                release.wait();
                stopped_generation = generation;
            });
    });
    commit_entered.get_future().wait();
    auto next_play = std::async(std::launch::async,
        [&] { callback.playDlnaMedia(url, 60, 1, 0, false, 42); });
    const auto replacement_waited = next_play.wait_for(std::chrono::milliseconds(100)) ==
        std::future_status::timeout;
    release_commit.set_value();
    const auto stopped = stop.get();
    next_play.get();
    const auto replacement = receive_play();
    bool stale_committed{};
    const auto stale_stopped = callback.stopPlaybackFromController(command,
        [&](std::uint64_t) { stale_committed = true; });
    require(stopped && stopped_generation == 41 && replacement_waited &&
        !stale_stopped && !stale_committed && replacement != command,
        "a controller stop commits its source before a replacement can be accepted");

    callback.videoPlay(url, 1, 0);
    const auto airplay = receive_play();
    callback.mediaSeek(20, 43); // A preempted DLNA callback arrives after AirPlay.
    unsigned long long item{};
    require(callback.videoGetPlayState(&item) == 3 && item == airplay,
        "a late DLNA control cannot take back a newer AirPlay item");
    callback.stopDlnaMedia(43);
    require(callback.videoGetPlayState(&item) == 3 && item == airplay,
        "a delayed DLNA Stop/SetURI callback must not stop a newer AirPlay item");
    bool airplay_changed_dlna{};
    require(callback.stopPlaybackFromController(airplay,
        [&](std::uint64_t) { airplay_changed_dlna = true; }) && !airplay_changed_dlna,
        "an accepted AirPlay stop must not change the DLNA transport");

    callback.playDlnaMedia(url, 60, 1, 0, false, 44);
    const auto dlna = receive_play();
    callback.stopDlnaMedia(45);
    require(callback.videoGetPlayState(&item) == 4 && item == dlna,
        "the current DLNA source must still accept its normal Stop callback");
    bool stop_sent{};
    while (!stop_sent) {
        const auto message = fixture.receive();
        stop_sent = message.header.type == MessageType::MediaStop;
    }
    callback.playDlnaMedia(url, 60, 1, 0, false, 46);
    const auto replaced_dlna = receive_play();
    callback.stopDlnaMedia(45);
    require(callback.videoGetPlayState(&item) == 3 && item == replaced_dlna,
        "an older DLNA Stop callback must not stop a replacement DLNA item");
}

} // namespace

int main() {
    try {
        require(iPhoneMirror::host_common::supported_capability(2880, 2880, 60) &&
            iPhoneMirror::host_common::supported_capability(2360, 2360, 60) &&
            iPhoneMirror::host_common::supported_capability(2732, 2732, 60) &&
            !iPhoneMirror::host_common::supported_capability(3840, 3840, 60),
            "iPad original capability is accepted separately from the verified iPhone envelope");
        for (const auto& display : iPhoneMirror::devices::AppleDisplays)
            require(iPhoneMirror::host_common::supported_capability(display.height, display.height, 60) &&
                !iPhoneMirror::host_common::supported_capability(display.height, display.height, 61),
                "every catalog long edge has a symmetric, bounded receiver capability");
        using namespace iPhoneMirror::wireless;
        const auto pause = parse_mirror_event("IPHONE_MIRROR_VIDEO_STATE\tdevice-a\t1");
        require(pause && pause->type == MessageType::MirrorState && pause->reserved == 1,
            "parse explicit sender pause");
        const auto geometry = parse_mirror_event("IPHONE_MIRROR_VIDEO_GEOMETRY\tdevice-a\t1920\t1080\t1280\t720");
        require(geometry && geometry->width == 1280 && geometry->stride[0] == 1920,
            "preserve source and output geometry separately");
        require(!parse_mirror_event("IPHONE_MIRROR_VIDEO_STATE\tdevice-a\t3") &&
            !parse_mirror_event("IPHONE_MIRROR_VIDEO_STATE\tdevice-a\t1\textra") &&
            !parse_mirror_event("IPHONE_MIRROR_VIDEO_GEOMETRY\tdevice-a\t0\t1080\t1280\t720") &&
            !parse_mirror_event("IPHONE_MIRROR_VIDEO_GEOMETRY\tdevice-a\tnan\t1080\t1280\t720"),
            "reject malformed state and geometry events");
        test_final_frame_while_writing();
        test_latest_pending_frame_per_device();
        test_explicit_playback_state_and_item_identity();
        test_dlna_completion_routing();
        test_terminal_pause_does_not_reopen_item();
        test_transport_commit_is_atomic_with_controls();
        test_controller_stop_commits_only_its_current_source();
        std::puts("IPC writer tests passed.");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s\n", error.what());
        return 1;
    }
}
