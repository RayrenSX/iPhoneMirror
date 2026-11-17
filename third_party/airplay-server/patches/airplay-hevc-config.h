/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef IPHONE_MIRROR_HEVC_CONFIG_H
#define IPHONE_MIRROR_HEVC_CONFIG_H
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static uint32_t iphone_mirror_be32(const unsigned char *p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
        ((uint32_t)p[2] << 8) | p[3];
}

/* AirPlay HEVC configuration is an ISO BMFF hvc1 visual sample entry.
 * Walk its child boxes, then the bounded hvcC NAL arrays. Never assume the
 * fixed VPS/SPS/PPS lengths/order observed on one particular phone. */
static unsigned char *iphone_mirror_hevc_parameter_sets(
    const unsigned char *data, size_t size, int *length) {
    size_t box, end, cursor, used = 0;
    unsigned int arrays, mask = 0;
    unsigned char *output;
    *length = 0;
    if (!data || size < 86 || size > 64U * 1024U * 1024U ||
        memcmp(data + 4, "hvc1", 4) != 0) return NULL;
    end = iphone_mirror_be32(data);
    if (end < 86 || end > size) return NULL;
    for (box = 86; box + 8 <= end;) {
        size_t bytes = iphone_mirror_be32(data + box);
        if (bytes < 8 || bytes > end - box) return NULL;
        if (memcmp(data + box + 4, "hvcC", 4) == 0) {
            end = box + bytes;
            break;
        }
        box += bytes;
    }
    if (box + 8 + 23 > end || data[box + 8] != 1 ||
        (data[box + 8 + 21] & 3) != 3) return NULL;
    arrays = data[box + 8 + 22];
    cursor = box + 8 + 23;
    /* Each input NAL has a two-byte length, replaced by a four-byte start
     * code. Twice the input size is a safe upper bound for nonempty NALs. */
    output = (unsigned char *)malloc(size * 2);
    if (!output) return NULL;
    for (unsigned int a = 0; a < arrays; ++a) {
        unsigned int type, count;
        if (end - cursor < 3) goto invalid;
        type = data[cursor++] & 63;
        count = ((unsigned int)data[cursor] << 8) | data[cursor + 1];
        cursor += 2;
        for (unsigned int n = 0; n < count; ++n) {
            size_t bytes;
            if (end - cursor < 2) goto invalid;
            bytes = ((size_t)data[cursor] << 8) | data[cursor + 1];
            cursor += 2;
            if (bytes < 2 || bytes > end - cursor) goto invalid;
            if (((data[cursor] >> 1) & 63) != type) goto invalid;
            if (type >= 32 && type <= 34) {
                if (used + 4 + bytes > size * 2) goto invalid;
                memcpy(output + used, "\0\0\0\1", 4); used += 4;
                memcpy(output + used, data + cursor, bytes); used += bytes;
                mask |= 1U << (type - 32);
            }
            cursor += bytes;
        }
    }
    if (mask != 7 || cursor != end) goto invalid;
    *length = (int)used;
    return output;
invalid:
    free(output);
    return NULL;
}
#endif
