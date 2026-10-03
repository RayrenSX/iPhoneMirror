/* Exercise the actual receiver ABI using generated H.264 and ALAC packets. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/imgutils.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>

static void check(int ok, const char *message) {
    if (!ok) { fprintf(stderr, "%s\n", message); exit(1); }
}
static uint8_t *read_file(const char *path, int *length) {
    FILE *f = fopen(path, "rb");
    check(f != NULL, path);
    fseek(f, 0, SEEK_END);
    *length = (int)ftell(f);
    rewind(f);
    uint8_t *data = av_mallocz(*length + AV_INPUT_BUFFER_PADDING_SIZE);
    check(data && fread(data, 1, *length, f) == (size_t)*length, "read fixture");
    fclose(f);
    return data;
}
static int video_frames;
static void receive_video(AVCodecContext *ctx, FILE *out, int width, int height) {
    AVFrame *frame = av_frame_alloc();
    int result;
    while ((result = avcodec_receive_frame(ctx, frame)) >= 0) {
        check(frame->width == width && frame->height == height &&
            frame->format == AV_PIX_FMT_YUV420P, "video dimensions/pixel format");
        for (int plane = 0; plane < 3; ++plane) {
            int w = plane ? width / 2 : width, h = plane ? height / 2 : height;
            for (int y = 0; y < h; ++y)
                check(fwrite(frame->data[plane] + y * frame->linesize[plane], 1, w, out) == (size_t)w,
                    "write decoded video");
        }
        struct SwsContext *scale = sws_getContext(width, height, frame->format,
            32, 32, AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
        uint8_t pixels[32 * 32 * 4];
        uint8_t *dest[] = {pixels}; int strides[] = {32 * 4};
        check(scale && sws_scale(scale, (const uint8_t *const *)frame->data,
            frame->linesize, 0, height, dest, strides) == 32, "BGRA scale");
        sws_freeContext(scale);
        ++video_frames;
        av_frame_unref(frame);
    }
    check(result == AVERROR(EAGAIN) || result == AVERROR_EOF, "receive video");
    av_frame_free(&frame);
}
static void decode_video(AVCodecContext *ctx, const char *path, const char *output,
        int width, int height) {
    int size; uint8_t *input = read_file(path, &size), *cursor = input;
    AVCodecParserContext *parser = av_parser_init(AV_CODEC_ID_H264);
    AVPacket packet; av_init_packet(&packet);
    FILE *out = fopen(output, "wb");
    check(parser && out, "video test setup");
    while (size > 0) {
        int used = av_parser_parse2(parser, ctx, &packet.data, &packet.size,
            cursor, size, AV_NOPTS_VALUE, AV_NOPTS_VALUE, 0);
        check(used >= 0 && (used || packet.size), "parse H.264");
        cursor += used; size -= used;
        if (packet.size) {
            check(avcodec_send_packet(ctx, &packet) >= 0, "send H.264");
            receive_video(ctx, out, width, height);
        }
    }
    av_parser_parse2(parser, ctx, &packet.data, &packet.size, NULL, 0,
        AV_NOPTS_VALUE, AV_NOPTS_VALUE, 0);
    if (packet.size) {
        check(avcodec_send_packet(ctx, &packet) >= 0, "send final H.264");
        receive_video(ctx, out, width, height);
    }
    check(avcodec_send_packet(ctx, NULL) >= 0, "flush H.264");
    receive_video(ctx, out, width, height);
    avcodec_flush_buffers(ctx);
    fclose(out); av_parser_close(parser); av_free(input);
}
int main(int argc, char **argv) {
    check(argc == 8, "expected landscape, portrait, ALAC extradata, packet and three output paths");
    check(avcodec_version() == AV_VERSION_INT(58,137,100), "receiver avcodec ABI changed");
    AVCodecContext *video = avcodec_alloc_context3(avcodec_find_decoder(AV_CODEC_ID_H264));
    check(video && avcodec_open2(video, NULL, NULL) >= 0, "open H.264 decoder");
    decode_video(video, argv[1], argv[5], 96, 64);
    decode_video(video, argv[2], argv[6], 64, 96);
    check(video_frames == 12, "decoded H.264 frame count including orientation change");
    avcodec_free_context(&video);
    AVCodecContext *audio = avcodec_alloc_context3(avcodec_find_decoder(AV_CODEC_ID_ALAC));
    check(audio != NULL, "ALAC decoder exists");
    audio->extradata = read_file(argv[3], &audio->extradata_size);
    check(avcodec_open2(audio, NULL, NULL) >= 0, "open ALAC decoder");
    AVPacket packet; av_init_packet(&packet);
    packet.data = read_file(argv[4], &packet.size);
    AVFrame *frame = av_frame_alloc();
    check(avcodec_send_packet(audio, &packet) >= 0 &&
        avcodec_receive_frame(audio, frame) >= 0, "decode ALAC packet");
    check(frame->nb_samples == 4096 && frame->channels == 2 &&
        frame->sample_rate == 44100, "ALAC sample count and negotiated format");
    struct SwrContext *swr = swr_alloc_set_opts(NULL, AV_CH_LAYOUT_STEREO,
        AV_SAMPLE_FMT_S16, 44100, AV_CH_LAYOUT_STEREO, frame->format, 44100, 0, NULL);
    check(swr && swr_init(swr) >= 0, "initialize audio conversion");
    uint8_t pcm[4096 * 4]; uint8_t *dest[] = {pcm};
    check(swr_convert(swr, dest, 4096, (const uint8_t **)frame->extended_data,
        frame->nb_samples) == 4096, "convert ALAC to interleaved PCM");
    FILE *out = fopen(argv[7], "wb");
    check(out && fwrite(pcm, 1, sizeof(pcm), out) == sizeof(pcm), "write decoded audio");
    fclose(out); swr_free(&swr); av_free(packet.data);
    av_frame_free(&frame); avcodec_free_context(&audio);
    puts("AirPlay H.264 orientation decode, BGRA scaling, ALAC and PCM conversion passed.");
    return 0;
}
