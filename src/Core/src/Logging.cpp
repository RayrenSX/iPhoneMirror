#include "Logging.h"

#include <Windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

namespace iPhoneMirror::logging {
namespace {

std::mutex log_mutex;
std::ofstream log_file;
std::filesystem::path log_path;
bool initialized{};
std::size_t pending_lines{};
std::chrono::steady_clock::time_point last_flush{};
std::jthread flush_thread;

void flush_pending_locked() {
    if (!log_file || pending_lines == 0) return;
    log_file.flush();
    pending_lines = 0;
    last_flush = std::chrono::steady_clock::now();
}

void ensure_flush_thread_locked() {
    if (flush_thread.joinable()) return;
    flush_thread = std::jthread([](std::stop_token token) {
        // Keep the GUI log tail genuinely live even if the last error is the
        // final line written. Flushing on this background worker avoids disk
        // I/O in the USB/decode/render hot paths.
        while (!token.stop_requested()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
            std::scoped_lock lock(log_mutex);
            flush_pending_locked();
        }
        std::scoped_lock lock(log_mutex);
        flush_pending_locked();
    });
}

std::filesystem::path default_path() {
    // TEMP is writable for both a normal desktop launch and a packaged EXE;
    // unlike the application directory it does not require elevation.
    return std::filesystem::temp_directory_path() / L"iPhoneMirror-capture.log";
}

std::filesystem::path configured_path() {
    wchar_t buffer[32768]{};
    const auto length = GetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", buffer, 32768);
    if (length > 0 && length < 32768) return std::filesystem::path(buffer);
    return default_path();
}

void rotate_if_needed(const std::filesystem::path& path) {
    constexpr std::uintmax_t MaxLogBytes = 16U * 1024U * 1024U;
    std::error_code error;
    if (!std::filesystem::exists(path, error) || error ||
        std::filesystem::file_size(path, error) <= MaxLogBytes || error) return;
    auto previous = path;
    previous += L".1";
    std::filesystem::remove(previous, error);
    error.clear();
    std::filesystem::rename(path, previous, error);
}

std::string now_text() {
    const auto now = std::chrono::system_clock::now();
    const auto time = std::chrono::system_clock::to_time_t(now);
    std::tm local{};
    localtime_s(&local, &time);
    const auto millis = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()).count() % 1000;
    std::ostringstream stream;
    stream << std::put_time(&local, "%Y-%m-%d %H:%M:%S") << '.'
        << std::setfill('0') << std::setw(3) << millis;
    return stream.str();
}

} // namespace

void initialize() {
    std::scoped_lock lock(log_mutex);
    if (initialized) return;
    try {
        log_path = configured_path();
        if (!log_path.parent_path().empty())
            std::filesystem::create_directories(log_path.parent_path());
        rotate_if_needed(log_path);
        log_file.open(log_path, std::ios::out | std::ios::app);
        initialized = true;
        pending_lines = 0;
        last_flush = std::chrono::steady_clock::now();
        if (log_file) {
            log_file << "\n=== iPhoneMirror capture session ===\n";
            log_file << now_text() << " [startup] log=" << log_path.string() << "\n";
            log_file.flush();
            ensure_flush_thread_locked();
        }
    } catch (...) {
        initialized = true;
    }
}

void write(std::string_view message) {
    std::scoped_lock lock(log_mutex);
    if (!initialized) {
        // Logging is deliberately best-effort and safe before im_initialize.
        try { log_path = configured_path(); log_file.open(log_path, std::ios::out | std::ios::app); }
        catch (...) {}
        initialized = true;
        if (log_file) ensure_flush_thread_locked();
    }
    if (!log_file) return;
    log_file << now_text() << " [tid=" << std::hash<std::thread::id>{}(std::this_thread::get_id())
        << "] " << message << '\n';
    ++pending_lines;
    const auto now = std::chrono::steady_clock::now();
    if (pending_lines >= 64 || now - last_flush >= std::chrono::milliseconds(500)) {
        log_file.flush();
        pending_lines = 0;
        last_flush = now;
    }
}

void shutdown() {
    std::jthread worker;
    {
        std::scoped_lock lock(log_mutex);
        if (!initialized) return;
        worker = std::move(flush_thread);
    }
    if (worker.joinable()) {
        worker.request_stop();
        worker.join();
    }
    {
        std::scoped_lock lock(log_mutex);
        if (log_file) {
            log_file << now_text() << " [shutdown]\n";
            log_file.flush();
            log_file.close();
        }
        initialized = false;
    }
}

} // namespace iPhoneMirror::logging
