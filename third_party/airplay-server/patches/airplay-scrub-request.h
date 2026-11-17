/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef IPHONE_MIRROR_SCRUB_REQUEST_H
#define IPHONE_MIRROR_SCRUB_REQUEST_H
#include <float.h>
#include <limits.h>
#include <stddef.h>
#include <string.h>

enum iphone_mirror_scrub_result {
    IM_SCRUB_NOT_ROUTE = 0, IM_SCRUB_READ = 1, IM_SCRUB_SEEK = 2,
    IM_SCRUB_MISSING = 3, IM_SCRUB_INVALID = 4, IM_SCRUB_DUPLICATE = 5,
    IM_SCRUB_METHOD = 6, IM_SCRUB_NOT_OWNER = 7
};

static int iphone_mirror_scrub_route(const char *url) {
    return url && !strncmp(url, "/scrub", 6) && (!url[6] || url[6] == '?');
}

static int iphone_mirror_scrub_method(const char *method) {
    return method && !strcmp(method, "GET") ? 1 :
        method && !strcmp(method, "POST") ? 2 : 3;
}

static int iphone_mirror_scrub_hex(char c) {
    return c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}

static int iphone_mirror_scrub_decode(const char *input, size_t length,
    char *output, size_t capacity) {
    size_t used = 0;
    for (size_t i = 0; i < length; ++i) {
        unsigned char c = (unsigned char)input[i];
        if (c == '%') {
            if (i + 2 >= length) return 0;
            int high = iphone_mirror_scrub_hex(input[i + 1]);
            int low = iphone_mirror_scrub_hex(input[i + 2]);
            if (high < 0 || low < 0) return 0;
            c = (unsigned char)((high << 4) | low); i += 2;
        } else if (c == '+') c = ' ';
        if (!c || used + 1 >= capacity) return 0;
        output[used++] = (char)c;
    }
    output[used] = 0;
    return 1;
}

/* Locale-independent decimal parsing. Do not let empty/garbage values become
 * a real seek to zero, or accept strtod's hex/NaN/Infinity extensions. */
static int iphone_mirror_scrub_number(const char *text, double *output) {
    const char *p = text;
    double value = 0, scale = 1;
    int digits = 0, nonzero = 0, exponent = 0, negative_exponent = 0;
    if (*p == '+') ++p;
    while (*p >= '0' && *p <= '9') {
        int digit = *p++ - '0';
        if (value > (DBL_MAX - digit) / 10) return 0;
        value = value * 10 + digit; ++digits; nonzero |= digit != 0;
    }
    if (*p == '.') {
        ++p;
        while (*p >= '0' && *p <= '9') {
            int digit = *p++ - '0';
            scale *= 0.1; value += digit * scale;
            ++digits; nonzero |= digit != 0;
        }
    }
    if (!digits) return 0;
    if (*p == 'e' || *p == 'E') {
        ++p;
        if (*p == '+' || *p == '-') negative_exponent = *p++ == '-';
        if (*p < '0' || *p > '9') return 0;
        while (*p >= '0' && *p <= '9') {
            if (exponent > 512) return 0;
            exponent = exponent * 10 + (*p++ - '0');
        }
        if (exponent > 512) return 0;
    }
    if (*p) return 0;
    while (exponent-- > 0) {
        if (!negative_exponent && value > DBL_MAX / 10) return 0;
        value = negative_exponent ? value / 10 : value * 10;
    }
    if (!(value >= 0 && value <= DBL_MAX) || (nonzero && value == 0)) return 0;
    *output = value;
    return 1;
}

static int iphone_mirror_scrub_request(const char *method, const char *url,
    double *position) {
    if (position) *position = 0;
    if (!iphone_mirror_scrub_route(url)) return IM_SCRUB_NOT_ROUTE;
    if (iphone_mirror_scrub_method(method) == 1) return IM_SCRUB_READ;
    if (iphone_mirror_scrub_method(method) != 2) return IM_SCRUB_METHOD;
    const char *query = strchr(url, '?');
    if (!query) return IM_SCRUB_MISSING;
    if (strlen(++query) > 4096) return IM_SCRUB_INVALID;
    int found = 0, valid = 0;
    double value = 0;
    while (*query) {
        const char *end = strchr(query, '&');
        if (!end) end = query + strlen(query);
        const char *equals = (const char *)memchr(query, '=', (size_t)(end - query));
        const char *key_end = equals ? equals : end;
        char key[128], decoded[128];
        if (!iphone_mirror_scrub_decode(query, (size_t)(key_end - query), key, sizeof(key)))
            return IM_SCRUB_INVALID;
        if (!strcmp(key, "position")) {
            if (found++) return IM_SCRUB_DUPLICATE;
            valid = equals && iphone_mirror_scrub_decode(equals + 1,
                (size_t)(end - equals - 1), decoded, sizeof(decoded)) &&
                iphone_mirror_scrub_number(decoded, &value);
        }
        if (!*end) break;
        query = end + 1;
    }
    if (!found) return IM_SCRUB_MISSING;
    if (!valid) return IM_SCRUB_INVALID;
    if (position) *position = value;
    return IM_SCRUB_SEEK;
}

static int iphone_mirror_scrub_milliseconds(double seconds) {
    if (!(seconds > 0)) return 0;
    return seconds >= INT_MAX / 1000.0 ? INT_MAX : (int)(seconds * 1000);
}
#endif
