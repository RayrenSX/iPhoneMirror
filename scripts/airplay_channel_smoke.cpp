#include <Windows.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <vector>
#include "FgAirplayChannel.h"

#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); std::exit(1); } } while (0)

class Callback final : public IAirServerCallback {
public:
    unsigned frames{}, width{}, height{};
    void connected(const char*, const char*) override {}
    void disconnected(const char*, const char*) override {}
    void outputAudio(SFgAudioFrame*, const char*, const char*) override {}
    void outputVideo(SFgVideoFrame* frame, const char*, const char*) override {
        CHECK(frame && frame->data && frame->width && frame->height);
        ++frames; width = frame->width; height = frame->height;
    }
    void videoPlay(char*, double, double) override {}
    void videoGetPlayInfo(double*, double*, double*) override {}
    void setVolume(float, const char*, const char*) override {}
    void log(int, const char*) override {}
};
class Channel final : public FgAirplayChannel {
public:
    using FgAirplayChannel::FgAirplayChannel;
    AVCodecContext* codec() const { return m_pCodecCtx; }
    bool opened() const { return m_bCodecOpened; }
    const SFgVideoFrame& scaled() const { return m_sVideoFrameScale; }
};

static std::vector<unsigned char> first_access_unit(const char* path) {
    std::ifstream file(path, std::ios::binary); CHECK(file);
    std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(file)), {});
    const auto size = bytes.size(); CHECK(size > 0 && size < 1024 * 1024);
    bytes.resize(size + AV_INPUT_BUFFER_PADDING_SIZE);
    auto* parser = av_parser_init(AV_CODEC_ID_H264); CHECK(parser);
    auto* context = avcodec_alloc_context3(avcodec_find_decoder(AV_CODEC_ID_H264)); CHECK(context);
    unsigned char* packet{}; int packet_size{}; std::size_t offset{};
    while (!packet_size && offset < size) {
        const int used = av_parser_parse2(parser, context, &packet, &packet_size,
            bytes.data() + offset, static_cast<int>(size - offset), AV_NOPTS_VALUE, AV_NOPTS_VALUE, 0);
        CHECK(used > 0); offset += used;
    }
    if (!packet_size)
        CHECK(av_parser_parse2(parser, context, &packet, &packet_size, nullptr, 0,
            AV_NOPTS_VALUE, AV_NOPTS_VALUE, 0) >= 0);
    CHECK(packet && packet_size > 0);
    std::vector<unsigned char> result(packet, packet + packet_size);
    av_parser_close(parser); avcodec_free_context(&context);
    return result;
}

static void test_backpressure(const char* landscape, const char* portrait) {
    Callback callback; Channel channel(&callback); CHECK(channel.initFFmpeg(nullptr, 0) == 0);
    auto old = first_access_unit(landscape); auto next = first_access_unit(portrait);
    auto* packet = av_packet_alloc(); CHECK(packet && av_new_packet(packet, static_cast<int>(old.size())) == 0);
    std::memcpy(packet->data, old.data(), old.size());
    int result{}; unsigned pending{};
    do {
        result = avcodec_send_packet(channel.codec(), packet);
        if (result >= 0) ++pending;
    } while (result >= 0 && pending < 16);
    CHECK(pending > 0 && result == AVERROR(EAGAIN));
    av_packet_free(&packet);
    // Put the caller's access unit at a page boundary. The decoder must make
    // its own padded copy before any bitstream reader can access its tail.
    SYSTEM_INFO system{}; GetSystemInfo(&system);
    const std::size_t readable = (next.size() + system.dwPageSize - 1) / system.dwPageSize * system.dwPageSize;
    auto* memory = static_cast<unsigned char*>(VirtualAlloc(nullptr, readable + system.dwPageSize,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE)); CHECK(memory);
    DWORD previous{}; CHECK(VirtualProtect(memory + readable, system.dwPageSize, PAGE_NOACCESS, &previous));
    auto* data = memory + readable - next.size(); std::memcpy(data, next.data(), next.size());
    SFgH264Data frame{}; frame.data = data; frame.size = static_cast<int>(next.size()); frame.is_key = 1;
    CHECK(channel.decodeH264Data(&frame, "local", "local") == 0);
    CHECK(callback.frames == pending + 1 && callback.width == 64 && callback.height == 96);
    VirtualFree(memory, 0, MEM_RELEASE);
    std::puts("Video channel: decoder backpressure preserves the rotation packet; unpadded caller input is safe.");
}

static void test_reopen(const char* path) {
    Callback callback; Channel channel(&callback);
    auto bytes = first_access_unit(path); const auto size = bytes.size();
    bytes.resize(size + AV_INPUT_BUFFER_PADDING_SIZE);
    SFgH264Data frame{}; frame.data = bytes.data(); frame.size = static_cast<int>(size); frame.is_key = 1;
    CHECK(channel.decodeH264Data(&frame, "local", "local") == 0 && callback.frames == 1);
    channel.unInitFFmpeg();
    CHECK(!channel.opened() && !channel.codec());
    CHECK(channel.decodeH264Data(&frame, "local", "local") == 0 && callback.frames == 2);
    std::puts("Video channel: close/reopen initializes a fresh decoder and continues publishing frames.");
}

static void test_scale() {
    Channel channel(nullptr);
    unsigned char bytes[6] = {60,60,60,60,90,150};
    SFgVideoFrame source{}; source.data = bytes; source.width = source.height = 2;
    source.pitch[0] = 2; source.pitch[1] = source.pitch[2] = 1;
    source.dataLen[0] = 4; source.dataLen[1] = source.dataLen[2] = 1; source.dataTotalLen = 6;
    CHECK(channel.setScale(0.5f) == 0.5f && channel.scaleH264Data(&source) == 0);
    const auto& output = channel.scaled();
    CHECK(output.width == 1 && output.height == 1 && output.pitch[1] >= 1 && output.pitch[2] >= 1);
    CHECK(output.dataLen[0] >= output.pitch[0] && output.dataLen[1] >= output.pitch[1] &&
        output.dataLen[2] >= output.pitch[2]);
    CHECK(output.data[0] == 60 && output.data[output.dataLen[0]] == 90 &&
        output.data[output.dataLen[0] + output.dataLen[1]] == 150);
    CHECK(channel.setScale(NAN) == 0.5f && channel.setScale(-1) == 0.5f);
    source.dataTotalLen = 5; CHECK(channel.scaleH264Data(&source) < 0);
    std::puts("Video channel: odd/minimum scaled dimensions retain complete chroma planes; invalid scale/input is rejected.");
}

int main(int argc, char** argv) {
    CHECK(argc == 3 || argc == 4);
    if (argc == 3 || !std::strcmp(argv[3], "scale")) test_scale();
    if (argc == 3 || !std::strcmp(argv[3], "backpressure")) test_backpressure(argv[1], argv[2]);
    if (argc == 3 || !std::strcmp(argv[3], "reopen")) test_reopen(argv[1]);
    return 0;
}
