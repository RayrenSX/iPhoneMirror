"""Apply the pinned upstream patch, then replace its model-specific tier scan.

The caller supplies validated dimensions as packet side data. Decoder bounds
them again against the SPS before any reference-plane access.
"""
from pathlib import Path
import subprocess
import re
import sys

source = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[1]
patch = (root / 'third_party/rplayhub/ffmpeg-rvra.patch').read_text()
# Upstream appended Windows getenv support without updating its final hunk count.
# Preserve the original artifact and recalculate counts before applying it.
lines = patch.splitlines(keepends=True)
for index, line in enumerate(lines):
    match = re.match(r'@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)', line)
    if match:
        body = []
        for entry in lines[index + 1:]:
            if entry.startswith(('@@ ', '--- ')):
                break
            body.append(entry)
        old = sum(entry.startswith((' ', '-')) for entry in body)
        new = sum(entry.startswith((' ', '+')) for entry in body)
        lines[index] = f'@@ -{match[1]},{old} +{match[2]},{new} @@{match[3]}\n'
subprocess.run(['patch', '-p1', '--forward'], input=''.join(lines), text=True, cwd=source, check=True)
path = source / 'libavcodec/hevc/hevcdec.c'
text = path.read_text()
start = text.index('static void rvra_resample_1d(')
end = text.index('static void rvra_scale_plane(', start)
fallback = text[start:end].replace('rvra_resample_1d(', 'rvra_resample_1d_fallback(', 1)
text = text[:start] + fallback + '''/* iPhoneMirror: cache RVRA taps and write complete rows in memory order. */
#if ARCH_X86_64
#include <emmintrin.h>
#endif

typedef struct RVRAFilter {
    int taps;
    ptrdiff_t offsets[64];
    double weight[64];
    double weight_sum;
} RVRAFilter;

static inline uint8_t rvra_sample(const uint8_t *src, const RVRAFilter *filter)
{
    double acc = 0;
    for (int t = 0; t < filter->taps; t++)
        acc += filter->weight[t] * src[filter->offsets[t]];
    const double value = acc / filter->weight_sum;
#if ARCH_X86_64
    /* Same current-rounding-mode conversion as lrint, without calling into
       the C runtime for every pixel. An RVRA sample fits in a signed int. */
    return av_clip_uint8(_mm_cvtsd_si32(_mm_set_sd(value)));
#else
    return av_clip_uint8((int)lrint(value));
#endif
}

static void rvra_resample_1d(const uint8_t *src, ptrdiff_t sstride, ptrdiff_t rstride,
                             uint8_t *dst, ptrdiff_t dstride, ptrdiff_t wstride,
                             int ssize, int dsize, int lines,
                             int kernel, int antialias)
{
    const double ratio = (double)ssize / dsize;
    const double scale = antialias && ratio > 1 ? ratio : 1;
    const double radius = rvra_kernel_radius[kernel] * scale;
    RVRAFilter *filters = av_malloc_array(dsize, sizeof(*filters));
    if (!filters) {
        rvra_resample_1d_fallback(src, sstride, rstride, dst, dstride, wstride,
                                  ssize, dsize, lines, kernel, antialias);
        return;
    }

    for (int d = 0; d < dsize; d++) {
        RVRAFilter *filter = &filters[d];
        const double center = (d + 0.5) * ratio - 0.5;
        int lo = (int)ceil(center - radius - 1e-9);
        const int hi = (int)floor(center + radius + 1e-9);
        filter->taps = hi - lo + 1;
        if (filter->taps > 64) {
            lo = (int)center - 31;
            filter->taps = 64;
        }
        filter->weight_sum = 0;
        for (int t = 0; t < filter->taps; t++) {
            filter->weight[t] = rvra_kernel(kernel, (lo + t - center) / scale);
            filter->weight_sum += filter->weight[t];
            filter->offsets[t] = av_clip(lo + t, 0, ssize - 1) * rstride;
        }
    }

    if (wstride == 1) {
        for (int line = 0; line < lines; line++) {
            const uint8_t *row = src + line * sstride;
            uint8_t *out = dst + line * dstride;
            for (int d = 0; d < dsize; d++)
                if (filters[d].weight_sum > 0)
                    out[d] = rvra_sample(row, &filters[d]);
        }
    } else {
        for (int d = 0; d < dsize; d++) {
            const RVRAFilter *filter = &filters[d];
            if (filter->weight_sum <= 0)
                continue;
            for (int line = 0; line < lines; line++)
                dst[d * wstride + line * dstride] = rvra_sample(src + line * sstride, filter);
        }
    }

    av_free(filters);
}

''' + text[end:]
start = text.index('    /* RVRA: the active size rides as a trailer')
end = text.index('    sd = av_packet_get_side_data(avpkt, AV_PKT_DATA_NEW_EXTRADATA', start)
text = text[:start] + '''    /* iPhoneMirror: dimensions belong to this exact packet, not a device tier list. */
    s->rvra_pending_w = s->rvra_pending_h = 0;
    if (s->rvra_enable) {
        AVDictionary *metadata = NULL;
        size_t size = 0;
        const uint8_t *packed = av_packet_get_side_data(avpkt, AV_PKT_DATA_STRINGS_METADATA, &size);
        if (packed && av_packet_unpack_dictionary(packed, size, &metadata) >= 0) {
            const AVDictionaryEntry *w = av_dict_get(metadata, "rvra.width", NULL, 0);
            const AVDictionaryEntry *h = av_dict_get(metadata, "rvra.height", NULL, 0);
            if (w && h) {
                const long width = strtol(w->value, NULL, 10), height = strtol(h->value, NULL, 10);
                if (width >= 2 && height >= 2 && width <= 8192 && height <= 8192 && !((width | height) & 1)) {
                    s->rvra_pending_w = width;
                    s->rvra_pending_h = height;
                }
            }
        }
        av_dict_free(&metadata);
    }

''' + text[end:]
start = text.index('static void rvra_scale_plane(')
end = text.index('/* Neutral-fill a plane', start)
scale = text[start:end].replace('static void rvra_scale_plane(', 'static int rvra_scale_plane(', 1)
scale = scale.replace('        return;', '        return AVERROR(ENOMEM);', 1)
scale = scale.replace('    av_free(tmp);\n}', '    av_free(tmp);\n    return 0;\n}')
text = text[:start] + scale + text[end:]

start = text.index('static void rvra_resample_refs(')
end = text.index('static int hevc_frame_start(', start)
text = text[:start] + '''typedef struct RVRAPlaneJob {
    uint8_t *data;
    ptrdiff_t stride;
    int sw, sh, dw, dh, plane_w, plane_h;
    int kernel, antialias;
} RVRAPlaneJob;

static int rvra_scale_job(AVCodecContext *avctx, void *opaque, int index, int thread)
{
    RVRAPlaneJob *job = &((RVRAPlaneJob *)opaque)[index];
    int ret = rvra_scale_plane(job->data, job->stride, job->sw, job->sh, job->dw, job->dh,
                               job->kernel, job->antialias);
    if (ret < 0)
        return ret;
    rvra_fill_outside(job->data, job->stride, job->plane_w, job->plane_h, job->dw, job->dh);
    return 0;
}

static int rvra_resample_refs(HEVCContext *s, HEVCLayerContext *l)
{
    const HEVCSPS *sps = l->sps;
    const int dw = s->rvra_pending_w, dh = s->rvra_pending_h;
    RVRAPlaneJob jobs[FF_ARRAY_ELEMS(l->DPB) * 3];
    HEVCFrame *changed[FF_ARRAY_ELEMS(l->DPB)];
    int results[FF_ARRAY_ELEMS(jobs)];
    int count = 0, changed_count = 0;

    for (int i = 0; i < FF_ARRAY_ELEMS(l->DPB); i++) {
        HEVCFrame *ref = &l->DPB[i];
        if (ref == l->cur_frame || !ref->f || !ref->f->data[0] ||
            !(ref->flags & (HEVC_FRAME_FLAG_SHORT_REF | HEVC_FRAME_FLAG_LONG_REF)))
            continue;
        const int sw = ref->rvra_w ? ref->rvra_w : sps->width;
        const int sh = ref->rvra_h ? ref->rvra_h : sps->height;
        if (sw == dw && sh == dh)
            continue;
        changed[changed_count++] = ref;
        for (int plane = 0; plane < 3; plane++) {
            const int shift = plane != 0;
            jobs[count++] = (RVRAPlaneJob){
                ref->f->data[plane], ref->f->linesize[plane],
                AV_CEIL_RSHIFT(sw, shift), AV_CEIL_RSHIFT(sh, shift),
                AV_CEIL_RSHIFT(dw, shift), AV_CEIL_RSHIFT(dh, shift),
                AV_CEIL_RSHIFT(sps->width, shift), AV_CEIL_RSHIFT(sps->height, shift),
                s->rvra_kernel, s->rvra_antialias
            };
        }
    }
    if (!count)
        return 0;

    // Planes and reference buffers are disjoint. The slice pool joins all
    // writes here before the current frame can read any reference.
    int ret = s->avctx->execute2(s->avctx, rvra_scale_job, jobs, results, count);
    if (ret < 0)
        return ret;
    for (int i = 0; i < count; i++)
        if (results[i] < 0)
            return results[i];
    for (int i = 0; i < changed_count; i++) {
        changed[i]->rvra_w = dw;
        changed[i]->rvra_h = dh;
    }
    return 0;
}

''' + text[end:]
needle = '        if (s->rvra_pending_w)\n            rvra_resample_refs(s, l);'
assert needle in text
text = text.replace(needle, '''        if (s->rvra_pending_w > sps->width || s->rvra_pending_h > sps->height) {
            ret = AVERROR_INVALIDDATA;
            goto fail;
        }
        /* A delta without a trailer keeps the last known size on its
         * coded canvas. IRAP and SPS changes start a new reference chain. */
        if (!s->rvra_pending_w && !IS_IRAP(s) &&
            s->rvra_canvas_w == sps->width && s->rvra_canvas_h == sps->height) {
            s->rvra_pending_w = s->rvra_last_w;
            s->rvra_pending_h = s->rvra_last_h;
        }
        s->rvra_canvas_w = sps->width;
        s->rvra_canvas_h = sps->height;
        s->rvra_last_w = s->rvra_pending_w;
        s->rvra_last_h = s->rvra_pending_h;
        if (s->rvra_pending_w) {
            ret = rvra_resample_refs(s, l);
            if (ret < 0)
                goto fail;
            ret = av_dict_set_int(&l->cur_frame->f->metadata, "rvra.width", s->rvra_pending_w, 0);
            if (ret < 0)
                goto fail;
            ret = av_dict_set_int(&l->cur_frame->f->metadata, "rvra.height", s->rvra_pending_h, 0);
            if (ret < 0)
                goto fail;
        }''')
# Slice workers join before changing frames. Frame workers would race with
# in-place reference resampling and remain incompatible with RVRA.
needle = 'if (s->rvra_enable && avctx->thread_count != 1)'
assert needle in text
text = text.replace(needle, 'if (s->rvra_enable && (avctx->active_thread_type & FF_THREAD_FRAME))')
path.write_text(text, newline='\n')
header = path.with_suffix('.h')
header_text = header.read_text()
needle = 'int rvra_pending_w, rvra_pending_h;'
assert needle in header_text
header.write_text(header_text.replace(needle, needle + '''
    int rvra_canvas_w, rvra_canvas_h;
    int rvra_last_w, rvra_last_h;'''), newline='\n')
