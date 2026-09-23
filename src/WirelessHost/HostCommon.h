// SPDX-License-Identifier: GPL-3.0-only

// Header-only helpers shared by WirelessHost.cpp and UxPlayHost.cpp.
// Each function is marked inline so the header can be included by both
// translation units without violating the one-definition rule.

#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <string_view>

#include <Windows.h>

namespace iPhoneMirror::host_common {

// Returns the value of the first occurrence of `name` in `argv`, or an empty
// string when the argument is absent or has no following value.
[[nodiscard]] inline std::wstring argument_value(int argc, wchar_t** argv,
    std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index) {
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    }
    return {};
}

// Returns true when `name` appears anywhere in `argv` after the program name.
[[nodiscard]] inline bool has_argument(int argc, wchar_t** argv,
    std::wstring_view name) noexcept {
    for (int index = 1; index < argc; ++index) {
        if (std::wstring_view(argv[index]) == name) return true;
    }
    return false;
}

// Whitelist of (width, height, fps) capabilities advertised to the host.
// Orientation swaps are accepted so portrait and landscape both match.
[[nodiscard]] inline bool supported_capability(unsigned int width,
    unsigned int height, unsigned int fps) noexcept {
    const auto matches = [width, height](unsigned int long_edge,
        unsigned int short_edge) {
        return (width == long_edge && height == short_edge) ||
            (width == short_edge && height == long_edge);
    };
    return (matches(5120, 2880) && fps == 60) ||
        (matches(1920, 1080) && fps == 60) ||
        (matches(1280, 720) && fps == 30) ||
        (matches(960, 540) && fps == 30);
}

// Converts a UTF-16 view to UTF-8. Invalid UTF-16 is rejected via
// WC_ERR_INVALID_CHARS and surfaces as an empty string instead of
// silently substituting replacement characters.
[[nodiscard]] inline std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), result.data(), length, nullptr,
            nullptr) != length) {
        return {};
    }
    return result;
}

// Returns the directory that contains the current executable.
// Throws std::runtime_error when the path cannot be determined.
[[nodiscard]] inline std::filesystem::path executable_directory() {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(),
        static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size())
        throw std::runtime_error("Could not determine the host executable directory");
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

// Returns true when `error` indicates a code-integrity / image-hash failure
// rather than an ordinary load or policy failure.
[[nodiscard]] inline bool is_code_integrity_error(DWORD error) noexcept {
    return error == ERROR_INVALID_IMAGE_HASH ||
        error == ERROR_ACCESS_DISABLED_BY_POLICY ||
        (error >= ERROR_SYSTEM_INTEGRITY_ROLLBACK_DETECTED &&
            error <= ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE) ||
        (error >= ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE &&
            error <= ERROR_SYSTEM_INTEGRITY_WHQL_NOT_SATISFIED);
}

// Opens `pipe_name` with a short retry loop so a host that is still starting
// up can be reached. Returns INVALID_HANDLE_VALUE on failure.
[[nodiscard]] inline HANDLE connect_pipe(const std::wstring& pipe_name) noexcept {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto pipe = CreateFileW(pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        const auto error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)
            return INVALID_HANDLE_VALUE;
        WaitNamedPipeW(pipe_name.c_str(), 100);
    }
    return INVALID_HANDLE_VALUE;
}

// Reads exactly `size` bytes from `pipe` in bounded chunks. Returns false on
// failure or end-of-pipe (ReadFile fails or returns zero bytes mid-stream).
[[nodiscard]] inline bool read_all(HANDLE pipe, void* destination,
    std::size_t size) noexcept {
    auto* bytes = static_cast<std::uint8_t*>(destination);
    while (size != 0) {
        DWORD read{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size,
            1024U * 1024U));
        if (!ReadFile(pipe, bytes, request, &read, nullptr) || read == 0)
            return false;
        bytes += read;
        size -= read;
    }
    return true;
}

// Writes `size` bytes from `source` to `pipe` in bounded chunks. Returns false
// on failure; when `failure_reason` is non-null it receives the Win32 error
// code (ERROR_BROKEN_PIPE when the pipe closes mid-write).
[[nodiscard]] inline bool write_all(HANDLE pipe, const void* source,
    std::size_t size, DWORD* failure_reason = nullptr) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size,
            1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr)) {
            if (failure_reason) *failure_reason = GetLastError();
            return false;
        }
        if (written == 0) {
            if (failure_reason) *failure_reason = ERROR_BROKEN_PIPE;
            return false;
        }
        bytes += written;
        size -= written;
    }
    return true;
}

} // namespace iPhoneMirror::host_common