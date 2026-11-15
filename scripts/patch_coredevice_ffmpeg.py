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
needle = '        if (s->rvra_pending_w)\n            rvra_resample_refs(s, l);'
assert needle in text
text = text.replace(needle, '''        if (s->rvra_pending_w > sps->width || s->rvra_pending_h > sps->height) {
            ret = AVERROR_INVALIDDATA;
            goto fail;
        }
        if (s->rvra_pending_w)
            rvra_resample_refs(s, l);''')
path.write_text(text, newline='\n')
