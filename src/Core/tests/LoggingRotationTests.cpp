#include "Logging.h"

#include <Windows.h>

#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>

namespace {
constexpr std::uintmax_t MaximumLogBytes = 16U * 1024U * 1024U;

void check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

std::string read(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}

class Fixture {
public:
    const std::filesystem::path directory = std::filesystem::temp_directory_path() /
        (L"iPhoneMirror-log-rotation-" + std::to_wstring(GetCurrentProcessId()) +
            L"-" + std::to_wstring(GetTickCount64()));
    const std::filesystem::path path = directory / L"capture.log";
    const std::filesystem::path archive = directory / L"capture.log.1";

    Fixture() {
        std::wstring buffer(32768, L'\0');
        const auto length = GetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE",
            buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length != 0 && length < buffer.size()) previous_path = buffer.substr(0, length);
        std::filesystem::create_directory(directory);
        SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", path.c_str());
    }

    ~Fixture() {
        iPhoneMirror::logging::shutdown();
        SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE",
            previous_path.empty() ? nullptr : previous_path.c_str());
        std::error_code error;
        std::filesystem::remove_all(directory, error);
    }

    void check_bounds() const {
        check(std::filesystem::file_size(path) <= MaximumLogBytes,
            "active native log exceeds 16 MiB");
        check(std::filesystem::file_size(archive) <= MaximumLogBytes,
            "native log archive exceeds 16 MiB");
        check(!std::filesystem::exists(directory / L"capture.log.2"),
            "native logging retains more than one archive");
    }

private:
    std::wstring previous_path;
};

void test_continuous_rotation_and_locked_archive() {
    namespace logging = iPhoneMirror::logging;
    Fixture fixture;
    logging::initialize();
    const std::string payload(64 * 1024, 'x');
    for (int index = 0; index < 800; ++index)
        logging::write(logging::Level::Info, "rotation", payload);
    fixture.check_bounds();

    // Windows normally prevents deleting or renaming files whose readers did
    // not grant FILE_SHARE_DELETE. Filling the active log must stop writing
    // rather than append forever when the retained archive is held open.
    const auto locked = CreateFileW(fixture.archive.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    check(locked != INVALID_HANDLE_VALUE, "failed to lock native log archive");
    for (int index = 0; index < 300; ++index)
        logging::write(logging::Level::Warning, "rotation", payload);
    fixture.check_bounds();
    const auto blocked_size = std::filesystem::file_size(fixture.path);
    logging::write("blocked-rotation-must-not-be-appended");
    // A short line can fit in the remaining budget; repeat full-sized writes
    // and verify that rotation failure never increases the file beyond it.
    for (int index = 0; index < 30; ++index)
        logging::write(logging::Level::Error, "rotation", payload);
    check(std::filesystem::file_size(fixture.path) <= blocked_size + 1024,
        "rotation failure keeps appending full-size native log records");
    CloseHandle(locked);

    std::this_thread::sleep_for(std::chrono::milliseconds(1100));
    logging::write(logging::Level::Info, "rotation", payload);
    logging::write("rotation-recovered-marker");
    logging::shutdown();
    fixture.check_bounds();
    const auto recovered = read(fixture.path);
    check(recovered.find("rotation-recovered-marker") != std::string::npos &&
        recovered.find("dropped_before_recovery=") != std::string::npos &&
        recovered.find("dropped_before_recovery=0") == std::string::npos,
        "native rotation does not recover or report dropped records");
    check(recovered.find("[shutdown] session=") != std::string::npos,
        "shutdown summary is missing after native log rotation");
}

void test_shutdown_at_size_limit() {
    namespace logging = iPhoneMirror::logging;
    Fixture fixture;
    {
        std::ofstream stream(fixture.path, std::ios::binary);
        stream.seekp(MaximumLogBytes - 2049);
        stream.put('\n');
    }
    logging::initialize();
    logging::write(std::string(1650, 'x'));
    logging::shutdown();
    check(std::filesystem::file_size(fixture.path) <= MaximumLogBytes,
        "native shutdown summary exceeds the active file byte budget");
}

void test_oversized_legacy_log() {
    namespace logging = iPhoneMirror::logging;
    Fixture fixture;
    {
        std::ofstream stream(fixture.path, std::ios::binary);
        stream << "legacy-log-start-must-be-discarded\n";
        stream.seekp(MaximumLogBytes + 1024);
        stream << "legacy-log-tail-must-be-retained\n";
    }
    logging::initialize();
    logging::write("new-session-after-oversized-legacy-log");
    logging::shutdown();
    fixture.check_bounds();
    const auto archived = read(fixture.archive);
    check(archived.find("legacy-log-start-must-be-discarded") == std::string::npos &&
        archived.find("legacy-log-tail-must-be-retained") != std::string::npos,
        "oversized legacy native log does not retain a bounded recent tail");
    check(read(fixture.path).find("new-session-after-oversized-legacy-log") != std::string::npos,
        "native logging does not start after trimming an oversized legacy log");
}

void test_concurrent_rotation() {
    namespace logging = iPhoneMirror::logging;
    Fixture fixture;
    logging::initialize();
    std::array<std::thread, 4> writers;
    for (auto& writer : writers) {
        writer = std::thread([] {
            const std::string payload(32 * 1024, 'x');
            for (int index = 0; index < 200; ++index)
                logging::write(logging::Level::Info, "concurrent", payload);
        });
    }
    for (auto& writer : writers) writer.join();
    logging::write("concurrent-rotation-complete");
    logging::shutdown();
    fixture.check_bounds();
    check(read(fixture.path).find("concurrent-rotation-complete") != std::string::npos,
        "concurrent rotation loses final native log record");
}
} // namespace

int main() {
    try {
        test_continuous_rotation_and_locked_archive();
        test_shutdown_at_size_limit();
        test_oversized_legacy_log();
        test_concurrent_rotation();
        std::cout << "PASS native continuous/concurrent rotation, locked-archive recovery and shutdown bounds\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
