#pragma once

#include <Windows.h>
#include <winhttp.h>

#include <cctype>
#include <limits>
#include <string>
#include <string_view>

namespace iPhoneMirror::wireless {

inline bool has_valid_percent_encoding(std::wstring_view value) noexcept {
    const auto hex = [](wchar_t character) {
        return (character >= L'0' && character <= L'9') ||
            (character >= L'a' && character <= L'f') ||
            (character >= L'A' && character <= L'F');
    };
    for (std::size_t index = 0; index < value.size(); ++index) {
        if (value[index] != L'%') continue;
        if (index + 2 >= value.size() || !hex(value[index + 1]) ||
            !hex(value[index + 2])) return false;
        index += 2;
    }
    return true;
}

inline bool is_valid_http_url(std::wstring_view value) noexcept {
    if (value.empty() || value.size() > 16U * 1024U ||
        value.size() > std::numeric_limits<DWORD>::max() ||
        value.find(L'\0') != std::wstring_view::npos ||
        !has_valid_percent_encoding(value)) return false;
    for (const auto character : value) {
        if (character <= 0x20 || character == 0x7f || character == L'\\')
            return false;
    }

    URL_COMPONENTS components{};
    components.dwStructSize = sizeof(components);
    components.dwSchemeLength = static_cast<DWORD>(-1);
    components.dwHostNameLength = static_cast<DWORD>(-1);
    components.dwUrlPathLength = static_cast<DWORD>(-1);
    components.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(value.data(), static_cast<DWORD>(value.size()), 0,
            &components)) return false;
    return (components.nScheme == INTERNET_SCHEME_HTTP ||
            components.nScheme == INTERNET_SCHEME_HTTPS) &&
        components.lpszHostName && components.dwHostNameLength != 0;
}

inline bool is_valid_http_url(std::string_view value) noexcept {
    if (value.empty() || value.size() > 16U * 1024U ||
        value.size() > static_cast<std::size_t>(std::numeric_limits<int>::max()) ||
        value.find('\0') != std::string_view::npos) return false;
    try {
        const auto length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
            value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (length <= 0) return false;
        std::wstring wide(static_cast<std::size_t>(length), L'\0');
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                static_cast<int>(value.size()), wide.data(), length) != length)
            return false;
        return is_valid_http_url(wide);
    } catch (...) {
        return false;
    }
}

} // namespace iPhoneMirror::wireless
