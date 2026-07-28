#include <iPhoneMirror/VirtualCameraApi.h>

#include <windows.h>
#include <objbase.h>

#include <cstdio>
#include <filesystem>
#include <string>

namespace {

void print_hresult(const wchar_t* operation, HRESULT result) {
    wchar_t* message{};
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER |
                       FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, static_cast<DWORD>(result), 0,
                   reinterpret_cast<LPWSTR>(&message), 0, nullptr);
    std::fwprintf(stderr, L"%ls failed (0x%08X): %ls\n", operation,
                  static_cast<unsigned>(result),
                  message == nullptr ? L"Unknown error" : message);
    if (message != nullptr) LocalFree(message);
}

HRESULT installed_media_source_path(std::filesystem::path& path) {
    wchar_t program_files[32768]{};
    DWORD length = GetEnvironmentVariableW(
        L"ProgramW6432", program_files, static_cast<DWORD>(std::size(program_files)));
    if (length == 0 || length >= std::size(program_files)) {
        length = GetEnvironmentVariableW(
            L"ProgramFiles", program_files,
            static_cast<DWORD>(std::size(program_files)));
    }
    if (length == 0 || length >= std::size(program_files))
        return HRESULT_FROM_WIN32(GetLastError() == ERROR_SUCCESS
            ? ERROR_PATH_NOT_FOUND : GetLastError());
    path = std::filesystem::path(program_files) / L"iPhoneMirror" /
           L"VirtualCamera" / L"iPhoneMirror.VirtualCamera.dll";
    return S_OK;
}

HRESULT versioned_media_source_path(const std::filesystem::path& directory,
                                    std::filesystem::path& path) {
    GUID identifier{};
    HRESULT result = CoCreateGuid(&identifier);
    if (FAILED(result)) return result;
    wchar_t identifier_text[40]{};
    if (StringFromGUID2(identifier, identifier_text,
                        static_cast<int>(std::size(identifier_text))) == 0)
        return E_UNEXPECTED;
    const std::wstring token(identifier_text + 1, 36);
    path = directory /
        (std::wstring(L"iPhoneMirror.VirtualCamera-") + token + L".dll");
    return S_OK;
}

bool is_installed_media_source(const std::filesystem::path& path) {
    constexpr wchar_t prefix[] = L"iPhoneMirror.VirtualCamera";
    const std::wstring filename = path.filename().native();
    return filename.size() > std::size(prefix) - 1 &&
        _wcsnicmp(filename.c_str(), prefix, std::size(prefix) - 1) == 0 &&
        _wcsicmp(path.extension().c_str(), L".dll") == 0;
}

HRESULT remove_stale_media_sources(const std::filesystem::path& directory,
                                   const std::filesystem::path* keep) {
    std::error_code iterator_error;
    const bool exists = std::filesystem::exists(directory, iterator_error);
    if (iterator_error) {
        return HRESULT_FROM_WIN32(
            iterator_error.value() == 0 ? ERROR_GEN_FAILURE
                                        : iterator_error.value());
    }
    if (!exists) return S_OK;
    HRESULT first_failure = S_OK;
    for (std::filesystem::directory_iterator iterator(
             directory,
             std::filesystem::directory_options::skip_permission_denied,
             iterator_error), end;
         !iterator_error && iterator != end;
         iterator.increment(iterator_error)) {
        const auto candidate = iterator->path();
        if (!iterator->is_regular_file(iterator_error) || iterator_error ||
            !is_installed_media_source(candidate) ||
            (keep != nullptr && candidate == *keep))
            continue;
        if (DeleteFileW(candidate.c_str())) continue;
        const DWORD delete_error = GetLastError();
        if (MoveFileExW(candidate.c_str(), nullptr,
                        MOVEFILE_DELAY_UNTIL_REBOOT))
            continue;
        if (SUCCEEDED(first_failure)) {
            const DWORD move_error = GetLastError();
            first_failure = HRESULT_FROM_WIN32(
                move_error == ERROR_SUCCESS ? delete_error : move_error);
        }
    }
    if (iterator_error && SUCCEEDED(first_failure)) {
        first_failure = HRESULT_FROM_WIN32(
            iterator_error.value() == 0 ? ERROR_GEN_FAILURE
                                        : iterator_error.value());
    }
    return first_failure;
}

HRESULT install_media_source(const wchar_t* source_path) {
    if (source_path == nullptr || *source_path == L'\0') return E_INVALIDARG;
    std::filesystem::path legacy_destination;
    HRESULT result = installed_media_source_path(legacy_destination);
    if (FAILED(result)) return result;
    std::filesystem::path destination;
    try {
        const std::filesystem::path source =
            std::filesystem::weakly_canonical(source_path);
        if (!source.is_absolute() || !std::filesystem::is_regular_file(source) ||
            _wcsicmp(source.extension().c_str(), L".dll") != 0)
            return E_INVALIDARG;
        const auto directory = legacy_destination.parent_path();
        std::filesystem::create_directories(directory);
        if (FAILED(result = versioned_media_source_path(directory, destination)))
            return result;
        if (!CopyFileW(source.c_str(), destination.c_str(), TRUE))
            return HRESULT_FROM_WIN32(GetLastError());
    } catch (const std::filesystem::filesystem_error& error) {
        return HRESULT_FROM_WIN32(error.code().value() == 0
            ? ERROR_INVALID_DATA : static_cast<DWORD>(error.code().value()));
    }
    result = im_vcam_register_media_source(destination.c_str());
    if (FAILED(result)) DeleteFileW(destination.c_str());
    else remove_stale_media_sources(destination.parent_path(), &destination);
    return result;
}

HRESULT uninstall_media_source() {
    HRESULT result = im_vcam_unregister_media_source();
    if (FAILED(result)) return result;
    std::filesystem::path installed_path;
    const HRESULT path_result = installed_media_source_path(installed_path);
    if (FAILED(path_result)) return path_result;
    return remove_stale_media_sources(installed_path.parent_path(), nullptr);
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) {
        std::fwprintf(stderr,
            L"Usage: iPhoneMirror.VirtualCamera.Admin.exe install <absolute-dll-path>\n"
            L"       iPhoneMirror.VirtualCamera.Admin.exe uninstall\n");
        return 2;
    }

    HRESULT result = E_INVALIDARG;
    const std::wstring command(argv[1]);
    if (_wcsicmp(command.c_str(), L"install") == 0 && argc == 3) {
        result = install_media_source(argv[2]);
    } else if (_wcsicmp(command.c_str(), L"uninstall") == 0 && argc == 2) {
        result = uninstall_media_source();
    } else {
        std::fwprintf(stderr, L"Invalid command.\n");
        return 2;
    }
    if (FAILED(result)) {
        print_hresult(command.c_str(), result);
        return static_cast<int>(HRESULT_CODE(result) == 0 ? 1
                                                         : HRESULT_CODE(result));
    }
    std::wprintf(L"Virtual camera %ls completed.\n", command.c_str());
    return 0;
}
