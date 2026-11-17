/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef IPHONE_MIRROR_PLAY_REQUEST_H
#define IPHONE_MIRROR_PLAY_REQUEST_H
#include "plist/include/plist.h"
#include <stdlib.h>
#include <string.h>

/* Older senders use text/parameters rather than an Apple property list.
 * Copy the bounded payload before parsing: network buffers are not C strings. */
static plist_t iphone_mirror_play_request(const char *data, int length) {
    plist_t root = NULL;
    if (!data || length <= 0 || length > 1024 * 1024) return NULL;
    if ((length >= 8 && !memcmp(data, "bplist00", 8)) || data[0] == '<') {
        plist_from_memory(data, (uint32_t)length, &root);
        if (PLIST_IS_DICT(root)) return root;
        if (root) plist_free(root);
        return NULL;
    }
    if (memchr(data, 0, length)) return NULL;
    char *copy = (char *)malloc((size_t)length + 1);
    if (!copy) return NULL;
    memcpy(copy, data, length); copy[length] = 0;
    root = plist_new_dict();
    char *line = copy;
    int valid = 1;
    while (*line && valid) {
        char *next = strchr(line, '\n');
        if (next) *next++ = 0;
        char *end = line + strlen(line);
        while (end > line && (end[-1] == '\r' || end[-1] == ' ' || end[-1] == '\t')) *--end = 0;
        char *colon = strchr(line, ':');
        if (colon) {
            *colon++ = 0;
            while (*colon == ' ' || *colon == '\t') ++colon;
            if (!strcmp(line, "Content-Location")) {
                if (!*colon || plist_dict_get_item(root, line)) valid = 0;
                else plist_dict_set_item(root, line, plist_new_string(colon));
            } else if (!strcmp(line, "Start-Position") || !strcmp(line, "Start-Position-Seconds")) {
                char *number_end;
                double number = strtod(colon, &number_end);
                if (number_end == colon || *number_end || !(number >= 0 && number <= 8640000)) valid = 0;
                else plist_dict_set_item(root, line, plist_new_real(number));
            }
        }
        if (!next) break;
        line = next;
    }
    free(copy);
    if (!valid || !PLIST_IS_STRING(plist_dict_get_item(root, "Content-Location"))) {
        plist_free(root); return NULL;
    }
    return root;
}
#endif
