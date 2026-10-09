// Exercise the production writer with anonymous-pipe backpressure. No receiver
// service, socket, or AirPlay SDK is started by these tests.
#define wmain wireless_host_entry_not_called
#include "../WirelessHost.cpp"
#undef wmain

#include <cstdio>
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

} // namespace

int main() {
    try {
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
        std::puts("IPC writer tests passed.");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s\n", error.what());
        return 1;
    }
}
