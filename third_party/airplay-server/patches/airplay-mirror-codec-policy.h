/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef IPHONE_MIRROR_MIRROR_CODEC_POLICY_H
#define IPHONE_MIRROR_MIRROR_CODEC_POLICY_H

#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>
#endif

/* IPHONE_MIRROR_DIAGNOSTIC_MIRROR_CODEC_POLICY: an exact process-only flag.
 * Read Win32 state directly: the host, DNS-SD shim and receiver DLL have
 * separate CRT environment caches. A dedicated URL-media receiver keeps its
 * existing capabilities even when launched from the same diagnostic app. */
static inline int iphone_mirror_diagnostic_mirror_avc_only(void) {
#ifdef _WIN32
    wchar_t flag[2] = {0}, mode[16] = {0};
    DWORD length = GetEnvironmentVariableW(
        L"IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY", flag, 2);
    DWORD mode_length = GetEnvironmentVariableW(
        L"IPHONE_MIRROR_AIRPLAY_MODE", mode, 16);
    if (mode_length == 5 && !wcscmp(mode, L"media")) return 0;
    return length == 1 && flag[0] == L'1';
#else
    const char *flag = getenv("IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY");
    const char *mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
    return !(mode && !strcmp(mode, "media")) && flag && !strcmp(flag, "1");
#endif
}

static inline unsigned int iphone_mirror_mirror_features_high(void) {
    return iphone_mirror_diagnostic_mirror_avc_only() ? 0U : 0x00000400U;
}

#endif
