/* SPDX-License-Identifier: GPL-3.0-or-later
 * Small isolated decoder for the private CoreDevice IPC protocol. Binary stdio only.
 * Build against the pinned, RVRA-patched FFmpeg; no device or file access here.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>
#include <io.h>
#include <libavcodec/avcodec.h>
#include <libavutil/dict.h>
#include <libavutil/imgutils.h>
#include <libavutil/intreadwrite.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>

typedef struct Dimensions { uint32_t active_w, active_h, target_w, target_h; } Dimensions;

static int emit(uint32_t kind, uint32_t w, uint32_t h, int64_t pts,
                const uint8_t *data, uint32_t size) {
    uint8_t header[28] = {'I', 'M', 'C', 'D'};
    AV_WL32(header + 4, kind); AV_WL32(header + 8, w); AV_WL32(header + 12, h);
    AV_WL32(header + 16, size); AV_WL64(header + 20, pts);
    return fwrite(header, 1, sizeof(header), stdout) == sizeof(header) &&
           fwrite(data, 1, size, stdout) == size && fflush(stdout) == 0 ? 0 : -1;
}

static int output_video(AVFrame *frame, struct SwsContext **sws) {
    Dimensions d = {0};
    if (frame->opaque_ref && frame->opaque_ref->size == sizeof(d))
        memcpy(&d, frame->opaque_ref->data, sizeof(d));
    int w = d.active_w ? (int)d.active_w : frame->width;
    int h = d.active_h ? (int)d.active_h : frame->height;
    if (w < 2 || h < 2 || w > frame->width || h > frame->height || w > 8192 || h > 8192) return -1;
    int ow = d.target_w ? (int)d.target_w : w;
    int oh = d.target_h ? (int)d.target_h : h;
    if ((ow > oh) != (w > h)) { int swap = ow; ow = oh; oh = swap; }
    /* A full tier includes encoder padding; smaller tiers contain the whole screen. */
    if (d.target_w && w >= ow && h >= oh) { w = ow; h = oh; }
    uint8_t *planes[4] = {0}; int strides[4] = {0};
    int bytes = av_image_alloc(planes, strides, ow, oh, AV_PIX_FMT_YUV420P, 1);
    if (bytes < 0) return -1;
    *sws = sws_getCachedContext(*sws, w, h, frame->format, ow, oh,
                                AV_PIX_FMT_YUV420P, SWS_BILINEAR, NULL, NULL, NULL);
    int ret = *sws ? sws_scale(*sws, (const uint8_t * const *)frame->data, frame->linesize,
                              0, h, planes, strides) : -1;
    if (ret == oh) ret = emit(1, ow, oh, frame->pts, planes[0], bytes);
    else ret = -1;
    av_freep(&planes[0]);
    return ret;
}

static int output_audio(AVFrame *frame) {
    SwrContext *swr = NULL;
    AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
    if (frame->sample_rate <= 0 || frame->nb_samples > 8192 ||
        swr_alloc_set_opts2(&swr, &stereo, AV_SAMPLE_FMT_S16, 48000,
                           &frame->ch_layout, frame->format, frame->sample_rate, 0, NULL) < 0 ||
        swr_init(swr) < 0) { swr_free(&swr); return -1; }
    int count = swr_get_out_samples(swr, frame->nb_samples);
    if (count <= 0 || count > 16384) { swr_free(&swr); return -1; }
    uint8_t *data = av_malloc((size_t)count * 4);
    if (!data) { swr_free(&swr); return -1; }
    int samples = swr_convert(swr, &data, count, (const uint8_t **)frame->extended_data, frame->nb_samples);
    int ret = samples < 0 ? -1 : emit(2, 48000, 2, frame->pts, data, samples * 4);
    av_free(data); swr_free(&swr);
    return ret;
}

int main(int argc, char **argv) {
    const int audio = argc == 2 && !strcmp(argv[1], "--audio");
    if (argc > 1 && !audio) return 2;
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    av_log_set_level(AV_LOG_ERROR);
    const AVCodec *codec = avcodec_find_decoder(audio ? AV_CODEC_ID_AAC : AV_CODEC_ID_HEVC);
    AVCodecContext *ctx = avcodec_alloc_context3(codec);
    AVPacket *packet = av_packet_alloc(); AVFrame *frame = av_frame_alloc();
    struct SwsContext *sws = NULL;
    int result = 1, input_packets = 0, output_frames = 0;
    if (!ctx || !packet || !frame) goto done;
    ctx->thread_count = 1;
    ctx->flags |= AV_CODEC_FLAG_COPY_OPAQUE;
    ctx->pkt_timebase = (AVRational){1, 10000000};
    ctx->max_pixels = 8192LL * 8192;
    ctx->err_recognition = AV_EF_EXPLODE;
    if (audio) {
        const uint8_t asc[] = {0xf8, 0xe6, 0x50, 0x00};
        ctx->extradata = av_mallocz(sizeof(asc) + AV_INPUT_BUFFER_PADDING_SIZE);
        if (!ctx->extradata) goto done;
        memcpy(ctx->extradata, asc, sizeof(asc)); ctx->extradata_size = sizeof(asc);
    }
    if (avcodec_open2(ctx, codec, NULL) < 0) goto done;
    for (;;) {
        uint8_t header[32];
        size_t count = fread(header, 1, sizeof(header), stdin);
        int eof = count == 0 && feof(stdin);
        if (!eof && (count != sizeof(header) || memcmp(header, "IMPK", 4))) goto done;
        av_packet_unref(packet);
        if (!eof) {
            input_packets++;
            uint32_t size = AV_RL32(header + 4);
            Dimensions d = {AV_RL32(header + 16), AV_RL32(header + 20), AV_RL32(header + 24), AV_RL32(header + 28)};
            if (!size || size > 16 * 1024 * 1024 || (d.active_w | d.active_h) & 1 ||
                d.active_w > 8192 || d.active_h > 8192 || d.target_w > 8192 || d.target_h > 8192 ||
                d.active_w == 1 || d.active_h == 1 || d.target_w == 1 || d.target_h == 1 ||
                !!d.active_w != !!d.active_h || !!d.target_w != !!d.target_h) goto done;
            if (av_new_packet(packet, size) < 0 || fread(packet->data, 1, size, stdin) != size) goto done;
            packet->pts = packet->dts = AV_RL64(header + 8);
            packet->opaque_ref = av_buffer_alloc(sizeof(d));
            if (!packet->opaque_ref) goto done;
            memcpy(packet->opaque_ref->data, &d, sizeof(d));
            if (!audio) {
                AVDictionary *metadata = NULL;
                char value[32];
                snprintf(value, sizeof(value), "%u", d.active_w); av_dict_set(&metadata, "rvra.width", value, 0);
                snprintf(value, sizeof(value), "%u", d.active_h); av_dict_set(&metadata, "rvra.height", value, 0);
                size_t length = 0;
                uint8_t *packed = av_packet_pack_dictionary(metadata, &length);
                av_dict_free(&metadata);
                if (!packed || av_packet_add_side_data(packet, AV_PKT_DATA_STRINGS_METADATA, packed, length) < 0) {
                    av_free(packed); goto done;
                }
            }
        }
        int ret = avcodec_send_packet(ctx, eof ? NULL : packet);
        if (ret < 0 && ret != AVERROR_EOF) {
            /* Lost audio does not invalidate the video decoder. Video errors are explicit. */
            if (audio && !eof) continue;
            goto done;
        }
        while ((ret = avcodec_receive_frame(ctx, frame)) >= 0) {
            int out = audio ? output_audio(frame) : output_video(frame, &sws);
            output_frames++;
            av_frame_unref(frame);
            if (out < 0) goto done;
        }
        if (ret != AVERROR(EAGAIN) && ret != AVERROR_EOF) goto done;
        if (eof) { result = !audio && input_packets && !output_frames ? 1 : 0; break; }
    }
done:
    sws_freeContext(sws); av_frame_free(&frame); av_packet_free(&packet); avcodec_free_context(&ctx);
    return result;
}
