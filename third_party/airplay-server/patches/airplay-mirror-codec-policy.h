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
#ifdef _WIN32
static inline int iphone_mirror_diagnostic_mirror_flag_enabled(const wchar_t *variable) {
    wchar_t flag[2] = {0};
    DWORD length = GetEnvironmentVariableW(variable, flag, 2);
    return length == 1 && flag[0] == L'1';
}
static inline int iphone_mirror_mirror_diagnostic_enabled(const wchar_t *variable) {
    wchar_t mode[16] = {0};
    DWORD mode_length = GetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_MODE", mode, 16);
    return !(mode_length == 5 && !wcscmp(mode, L"media")) &&
        iphone_mirror_diagnostic_mirror_flag_enabled(variable);
}
#else
static inline int iphone_mirror_mirror_diagnostic_enabled(const char *variable) {
    const char *flag = getenv(variable);
    const char *mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
    return !(mode && !strcmp(mode, "media")) && flag && !strcmp(flag, "1");
}
#endif

static inline int iphone_mirror_diagnostic_mirror_avc_only(void) {
#ifdef _WIN32
    return iphone_mirror_mirror_diagnostic_enabled(L"IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY");
#else
    return iphone_mirror_mirror_diagnostic_enabled("IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY");
#endif
}

static inline int iphone_mirror_diagnostic_mirror_no_audio(void) {
#ifdef _WIN32
    return iphone_mirror_mirror_diagnostic_enabled(L"IPHONE_MIRROR_DIAGNOSTIC_MIRROR_NO_AUDIO");
#else
    return iphone_mirror_mirror_diagnostic_enabled("IPHONE_MIRROR_DIAGNOSTIC_MIRROR_NO_AUDIO");
#endif
}

static inline int iphone_mirror_diagnostic_video_only_capabilities(void) {
#ifdef _WIN32
    return iphone_mirror_diagnostic_mirror_no_audio() &&
        iphone_mirror_mirror_diagnostic_enabled(L"IPHONE_MIRROR_DIAGNOSTIC_VIDEO_ONLY_CAPABILITIES");
#else
    return 0;
#endif
}

/* 1 starts video-only. 2 updates capabilities. 3 also withdraws audio discovery. */
static inline int iphone_mirror_diagnostic_mirror_audio_event(void) {
#ifdef _WIN32
    if (iphone_mirror_mirror_diagnostic_enabled(L"IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AUDIO_EVENT")) {
        if (iphone_mirror_diagnostic_mirror_no_audio()) return 1;
        return iphone_mirror_mirror_diagnostic_enabled(L"IPHONE_MIRROR_DIAGNOSTIC_WITHDRAW_RAOP") ? 3 : 2;
    }
#endif
    return 0;
}

static inline unsigned int iphone_mirror_mirror_features_low(void) {
    const unsigned int features = 0x5A7FFEE6U;
    return iphone_mirror_diagnostic_mirror_no_audio() ? features & ~(1U << 9) : features;
}

static inline unsigned int iphone_mirror_mirror_features_high(void) {
    return iphone_mirror_diagnostic_mirror_avc_only() ? 0U : 0x00000400U;
}

#endif
