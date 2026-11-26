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
#include <windows.h>
#include <libavcodec/avcodec.h>
#include <libavutil/dict.h>
#include <libavutil/imgutils.h>
#include <libavutil/intreadwrite.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>

typedef struct Dimensions {
    uint32_t active_w, active_h, target_w, target_h, panel_w, panel_h;
    uint32_t orientation;
} Dimensions;

typedef struct VideoOutput {
    struct SwsContext *scale;
    uint8_t *planes[4];
    int strides[4];
    int width, height, bytes;
} VideoOutput;

typedef struct PreviewFrame {
    VideoOutput output;
    uint32_t kind;
    int64_t pts;
} PreviewFrame;

enum { PENDING_PREVIEW_FRAMES = 2 };

typedef struct DirectPreview {
    HANDLE thread;
    SRWLOCK lock;
    CONDITION_VARIABLE ready;
    PreviewFrame pending[PENDING_PREVIEW_FRAMES];
    unsigned int head, queued;
    int stopping, failed;
} DirectPreview;

static int emit(uint32_t kind, uint32_t w, uint32_t h, int64_t pts,
                const uint8_t *data, uint32_t size) {
    uint8_t header[28] = {'I', 'M', 'C', 'D'};
    AV_WL32(header + 4, kind); AV_WL32(header + 8, w); AV_WL32(header + 12, h);
    AV_WL32(header + 16, size); AV_WL64(header + 20, pts);
    return fwrite(header, 1, sizeof(header), stdout) == sizeof(header) &&
           fwrite(data, 1, size, stdout) == size && fflush(stdout) == 0 ? 0 : -1;
}

static DWORD WINAPI write_preview(LPVOID opaque) {
    DirectPreview *preview = opaque;
    VideoOutput frame = {0};
    for (;;) {
        AcquireSRWLockExclusive(&preview->lock);
        while (!preview->queued && !preview->stopping)
            SleepConditionVariableSRW(&preview->ready, &preview->lock, INFINITE, 0);
        if (!preview->queued) {
            ReleaseSRWLockExclusive(&preview->lock);
            break;
        }
        PreviewFrame *pending = &preview->pending[preview->head];
        VideoOutput reusable = frame;
        frame = pending->output;
        pending->output = reusable;
        const uint32_t kind = pending->kind;
        const int64_t pts = pending->pts;
        preview->head = (preview->head + 1) % PENDING_PREVIEW_FRAMES;
        --preview->queued;
        ReleaseSRWLockExclusive(&preview->lock);
        if (fputc('B', stderr) == EOF ||
            emit(kind, frame.width, frame.height, pts, frame.planes[0], frame.bytes) < 0 ||
            fputc('V', stderr) == EOF) {
            AcquireSRWLockExclusive(&preview->lock);
            preview->failed = 1;
            ReleaseSRWLockExclusive(&preview->lock);
            fputc('X', stderr);
            break;
        }
    }
    av_freep(&frame.planes[0]);
    return 0;
}

static int queue_preview(DirectPreview *preview, VideoOutput *output, uint32_t kind, int64_t pts) {
    AcquireSRWLockExclusive(&preview->lock);
    if (preview->failed) {
        ReleaseSRWLockExclusive(&preview->lock);
        return -2;
    }
    // Two pending pictures absorb a brief scheduling stall, then discard
    // the oldest picture so display cannot hold up HEVC reference decoding.
    const int dropped = preview->queued == PENDING_PREVIEW_FRAMES;
    const unsigned int tail = (preview->head + preview->queued) % PENDING_PREVIEW_FRAMES;
    PreviewFrame *pending = &preview->pending[tail];
    VideoOutput reusable = pending->output;
    struct SwsContext *scale = output->scale;
    pending->output = *output;
    pending->output.scale = NULL;
    pending->kind = kind;
    pending->pts = pts;
    if (dropped)
        preview->head = (preview->head + 1) % PENDING_PREVIEW_FRAMES;
    else
        ++preview->queued;
    *output = reusable;
    output->scale = scale;
    WakeConditionVariable(&preview->ready);
    ReleaseSRWLockExclusive(&preview->lock);
    return !dropped || fputc('D', stderr) != EOF ? 0 : -2;
}

static int gray_line(const AVFrame *frame, int x, int y, int dx, int dy, int count) {
    int gray = 0;
    for (int i = 0; i < count; ++i) {
        const uint8_t luma = frame->data[0][y * frame->linesize[0] + x];
        const uint8_t u = frame->data[1][(y / 2) * frame->linesize[1] + x / 2];
        const uint8_t v = frame->data[2][(y / 2) * frame->linesize[2] + x / 2];
        gray += luma >= 124 && luma <= 132 && u >= 124 && u <= 132 && v >= 124 && v <= 132;
        x += dx;
        y += dy;
    }
    return gray * 100 >= count * 94;
}

static void compensate_gray_padding(const AVFrame *frame, int *width, int *height,
                                    int panel_w, int panel_h) {
    if (frame->format != AV_PIX_FMT_YUV420P && frame->format != AV_PIX_FMT_YUVJ420P)
        return;
    if (!frame->data[0] || !frame->data[1] || !frame->data[2] || frame->linesize[0] < *width ||
        frame->linesize[1] < (*width + 1) / 2 || frame->linesize[2] < (*width + 1) / 2)
        return;

    const int w = *width, h = *height;
    const int stride_y = h / 24 > 1 ? h / 24 : 1;
    const int stride_x = w / 24 > 1 ? w / 24 : 1;
    const int rows = (h + stride_y - 1) / stride_y;
    const int cols = (w + stride_x - 1) / stride_x;
    if (!gray_line(frame, w - 1, 0, 0, stride_y, rows) ||
        !gray_line(frame, 0, h - 1, stride_x, 0, cols))
        return;

    int cropped_w = w, cropped_h = h;
    while (cropped_w > w / 2 && gray_line(frame, cropped_w - 1, 0, 0, stride_y, rows))
        --cropped_w;
    while (cropped_h > h / 2 && gray_line(frame, 0, cropped_h - 1, stride_x, 0, cols))
        --cropped_h;
    if (cropped_w <= w / 2 || cropped_h <= h / 2 ||
        w - cropped_w < 4 || h - cropped_h < 4)
        return;

    // A small margin counts only when removing it brings a mismatched tier
    // close to the real panel aspect. A gray sidebar or footer alone is not padding.
    const int64_t before = llabs((int64_t)w * panel_h - (int64_t)h * panel_w);
    const int64_t after = llabs((int64_t)cropped_w * panel_h - (int64_t)cropped_h * panel_w);
    const int substantial = cropped_w * 100 <= w * 94 && cropped_h * 100 <= h * 94;
    const int aspect_recovered = before >= 3 * after &&
        before * 100 >= (int64_t)h * panel_w * 3 &&
        after * 100 <= (int64_t)cropped_h * panel_w * 2;
    if (!aspect_recovered && !substantial)
        return;
    *width = cropped_w & ~1;
    *height = cropped_h & ~1;
}

static int output_video(AVFrame *frame, VideoOutput *output, DirectPreview *preview) {
    Dimensions d = {0};
    if (frame->opaque_ref && frame->opaque_ref->size == sizeof(d))
        memcpy(&d, frame->opaque_ref->data, sizeof(d));
    const AVDictionaryEntry *active_width = av_dict_get(frame->metadata, "rvra.width", NULL, 0);
    const AVDictionaryEntry *active_height = av_dict_get(frame->metadata, "rvra.height", NULL, 0);
    if (active_width && active_height) {
        d.active_w = (uint32_t)strtoul(active_width->value, NULL, 10);
        d.active_h = (uint32_t)strtoul(active_height->value, NULL, 10);
    }
    int w = d.active_w ? (int)d.active_w : frame->width;
    int h = d.active_h ? (int)d.active_h : frame->height;
    if (w < 2 || h < 2 || w > frame->width || h > frame->height || w > 8192 || h > 8192) return -1;
    int ow = d.target_w ? (int)d.target_w : w;
    int oh = d.target_h ? (int)d.target_h : h;
    if ((ow > oh) != (w > h)) { int swap = ow; ow = oh; oh = swap; }
    int pw = d.panel_w ? (int)d.panel_w : (int)d.target_w;
    int ph = d.panel_h ? (int)d.panel_h : (int)d.target_h;
    if ((pw > ph) != (w > h)) { int swap = pw; pw = ph; ph = swap; }
    const int canvas_w = frame->width, canvas_h = frame->height;
    if (pw >= 2 && ph >= 2 && canvas_w >= pw && canvas_h >= ph &&
        canvas_w - pw < 64 && canvas_h - ph < 64) {
        // Preserve the native preview's framing. RVRA rescales the whole
        // canvas, so each active rectangle needs the same normalized crop.
        // Round to the nearest even pixel for I420 chroma alignment.
        const int crop_w = 2 * ((w * pw + canvas_w) / (2 * canvas_w));
        const int crop_h = 2 * ((h * ph + canvas_h) / (2 * canvas_h));
        if (crop_w >= 2 && crop_h >= 2) {
            w = crop_w;
            h = crop_h;
        }
    } else if (d.panel_w && !d.active_w) {
        compensate_gray_padding(frame, &w, &h, pw, ph);
    }

    if (output->width != ow || output->height != oh) {
        av_freep(&output->planes[0]);
        output->bytes = av_image_alloc(output->planes, output->strides, ow, oh, AV_PIX_FMT_YUV420P, 1);
        if (output->bytes < 0) return -1;
        output->width = ow;
        output->height = oh;
    }
    const uint8_t *source[4] = {frame->data[0], frame->data[1], frame->data[2], frame->data[3]};
    if (w == ow && h == oh && frame->format == AV_PIX_FMT_YUV420P) {
        av_image_copy(output->planes, output->strides, source, frame->linesize, AV_PIX_FMT_YUV420P, w, h);
    } else {
        output->scale = sws_getCachedContext(output->scale, w, h, frame->format, ow, oh,
                                           AV_PIX_FMT_YUV420P, SWS_BILINEAR, NULL, NULL, NULL);
        if (!output->scale || sws_scale(output->scale, source, frame->linesize,
                                        0, h, output->planes, output->strides) != oh)
            return -1;
    }
    const uint32_t kind = d.orientation ? 4 + d.orientation : 1;
    if (preview)
        return queue_preview(preview, output, kind, frame->pts);
    return emit(kind, ow, oh, frame->pts, output->planes[0], output->bytes);
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
    const int direct = argc == 2 && !strcmp(argv[1], "--direct-video");
    if (argc > 1 && !audio && !direct) return 2;
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    if (direct) _setmode(_fileno(stderr), _O_BINARY);
    av_log_set_level(direct ? AV_LOG_QUIET : AV_LOG_ERROR);
    const AVCodec *codec = avcodec_find_decoder(audio ? AV_CODEC_ID_AAC : AV_CODEC_ID_HEVC);
    AVCodecContext *ctx = avcodec_alloc_context3(codec);
    AVPacket *packet = av_packet_alloc(); AVFrame *frame = av_frame_alloc();
    VideoOutput output = {0};
    DirectPreview preview = {0};
    int result = 1, input_packets = 0, output_frames = 0;
    if (!ctx || !packet || !frame) goto done;
    // Only parallelize work within a packet. RVRA mutates reference frames
    // before decoding the next packet, which requires sequential frames.
    ctx->thread_type = FF_THREAD_SLICE;
    ctx->thread_count = audio ? 1 : 4;
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
    if (direct) {
        InitializeSRWLock(&preview.lock);
        InitializeConditionVariable(&preview.ready);
        preview.thread = CreateThread(NULL, 0, write_preview, &preview, 0, NULL);
        if (!preview.thread) goto done;
    }
    for (;;) {
        uint8_t header[32];
        size_t count = fread(header, 1, sizeof(header), stdin);
        int eof = count == 0 && feof(stdin);
        if (!eof && (count != sizeof(header) || memcmp(header, "IMPK", 4))) goto done;
        av_packet_unref(packet);
        if (!eof) {
            input_packets++;
            uint32_t size = AV_RL32(header + 4);
            Dimensions d = {
                .active_w = AV_RL32(header + 16), .active_h = AV_RL32(header + 20),
                .target_w = AV_RL32(header + 24), .target_h = AV_RL32(header + 28)
            };
            if (direct) {
                d.orientation = d.active_w >> 28;
                d.active_w &= UINT32_C(0x0FFFFFFF);
                if (d.orientation > 4) goto done;
            }
            /* A private live header encodes the physical panel separately
               from the output size in the high half of the target fields. */
            d.panel_w = d.target_w >> 16; d.panel_h = d.target_h >> 16;
            d.target_w &= UINT16_MAX; d.target_h &= UINT16_MAX;
            if (!size || size > 16 * 1024 * 1024 || (d.active_w | d.active_h) & 1 ||
                d.active_w > 8192 || d.active_h > 8192 || d.target_w > 8192 || d.target_h > 8192 ||
                d.panel_w > 8192 || d.panel_h > 8192 || d.panel_w == 1 || d.panel_h == 1 ||
                d.active_w == 1 || d.active_h == 1 || d.target_w == 1 || d.target_h == 1 ||
                !!d.active_w != !!d.active_h || !!d.target_w != !!d.target_h ||
                !!d.panel_w != !!d.panel_h) goto done;
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
            if (!audio && ((frame->flags & AV_FRAME_FLAG_CORRUPT) || frame->decode_error_flags))
                goto done;
            int out = audio ? output_audio(frame) : output_video(frame, &output, direct ? &preview : NULL);
            output_frames++;
            av_frame_unref(frame);
            if (out < 0) {
                if (out == -2) result = 3;
                goto done;
            }
        }
        if (ret != AVERROR(EAGAIN) && ret != AVERROR_EOF) goto done;
        if (eof) { result = !audio && input_packets && !output_frames ? 1 : 0; break; }
    }
done:
    if (preview.thread) {
        AcquireSRWLockExclusive(&preview.lock);
        preview.stopping = 1;
        WakeConditionVariable(&preview.ready);
        ReleaseSRWLockExclusive(&preview.lock);
        WaitForSingleObject(preview.thread, INFINITE);
        CloseHandle(preview.thread);
        if (preview.failed) result = 3;
        for (unsigned int i = 0; i < PENDING_PREVIEW_FRAMES; ++i)
            av_freep(&preview.pending[i].output.planes[0]);
    }
    sws_freeContext(output.scale);
    av_freep(&output.planes[0]);
    av_frame_free(&frame); av_packet_free(&packet); avcodec_free_context(&ctx);
    return result;
}
