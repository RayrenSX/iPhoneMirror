#include <Windows.h>

#include <array>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>

namespace {

std::wstring quote(std::wstring_view value) {
    return L"\"" + std::wstring(value) + L"\"";
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS);

    const auto source_directory = std::filesystem::path(argv[2]).parent_path();
    const auto test_directory = std::filesystem::temp_directory_path() /
        (L"iPhoneMirror-invalid-runtime-" + std::to_wstring(GetCurrentProcessId()));
    const auto test_library = test_directory /
        std::filesystem::path(argv[2]).filename();
    try {
        std::filesystem::create_directories(test_directory);
        std::filesystem::copy_file(argv[2], test_library,
            std::filesystem::copy_options::overwrite_existing);
        for (const auto name : std::array{
                L"avcodec-58.dll", L"dnssd.dll", L"swresample-3.dll",
                L"swscale-5.dll"}) {
            std::filesystem::copy_file(source_directory / name,
                test_directory / name,
                std::filesystem::copy_options::overwrite_existing);
        }
        std::ofstream output(test_directory / L"avutil-56.dll",
            std::ios::binary | std::ios::trunc);
        output << "not a portable executable";
        if (!output) return 3;
    } catch (...) {
        std::filesystem::remove_all(test_directory);
        return 3;
    }

    auto command = quote(argv[1]) + L" --check-runtime --library " +
        quote(test_library.wstring());
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    const auto started = CreateProcessW(argv[1], command.data(), nullptr, nullptr,
        FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process) != FALSE;
    if (!started) {
        std::filesystem::remove_all(test_directory);
        return 4;
    }
    CloseHandle(process.hThread);
    const auto wait = WaitForSingleObject(process.hProcess, 5000);
    DWORD exit_code{};
    const auto read_exit = wait == WAIT_OBJECT_0 &&
        GetExitCodeProcess(process.hProcess, &exit_code) != FALSE;
    if (wait == WAIT_TIMEOUT) TerminateProcess(process.hProcess, 5);
    CloseHandle(process.hProcess);
    std::filesystem::remove_all(test_directory);
    return read_exit && exit_code == 41 ? 0 : 5;
}
