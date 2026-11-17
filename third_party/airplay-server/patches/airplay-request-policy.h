/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef IPHONE_MIRROR_REQUEST_POLICY_H
#define IPHONE_MIRROR_REQUEST_POLICY_H
#include <string.h>

/* AirPlay pairing can use either HTTP or RTSP without CSeq. Session requests
 * still require CSeq; pairing authentication remains in the handlers. */
static int iphone_mirror_request_headers_valid(const char *method,
    const char *url, const char *protocol, const char *cseq, int media_control) {
    int pairing = method && url && protocol && !strcmp(method, "POST") &&
        !strcmp(protocol, "RTSP/1.0") &&
        (!strcmp(url, "/pair-setup") || !strcmp(url, "/pair-verify") ||
         !strcmp(url, "/fp-setup"));
    return method && url && protocol &&
        (cseq || media_control || pairing || !strcmp(protocol, "HTTP/1.1") ||
            !strcmp(protocol, "HTTP/1.0"));
}
#endif
