#include "Logging.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <format>
#include <iomanip>
#include <limits>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>

namespace iPhoneMirror::logging {
namespace {

std::mutex log_mutex;
std::mutex lifecycle_mutex;
std::ofstream log_file;
std::filesystem::path log_path;
std::string current_log_date;
constexpr std::uintmax_t MaximumLogBytes = 16U * 1024U * 1024U;
constexpr std::uintmax_t MaximumHeaderBytes = 1024;
#ifdef IPHONEMIRROR_TEST_LOG_CLOCK
constexpr auto IdleDateCheckInterval = std::chrono::milliseconds(200);
#else
constexpr auto IdleDateCheckInterval = std::chrono::minutes(1);
#endif
std::uintmax_t current_file_bytes{};
std::filesystem::path rotation_failure_path;
std::chrono::steady_clock::time_point rotation_retry_after{};
bool initialized{};
bool writes_suspended{};
bool session_header_written{};
std::size_t pending_lines{};
std::chrono::steady_clock::time_point last_flush{};
std::jthread flush_thread;
std::uint64_t sequence{};
std::uint64_t line_count{};
std::uint64_t warning_count{};
std::uint64_t error_count{};
std::uint64_t byte_count{};
std::uint64_t dropped_write_count{};
std::string session_id;
std::chrono::steady_clock::time_point session_started{};
#ifdef IPHONEMIRROR_TEST_LOG_CLOCK
std::chrono::system_clock::duration log_time_offset{};
#endif
std::once_flag fingerprint_salt_once;
std::array<unsigned char, 32> fingerprint_salt{};
bool fingerprint_salt_ready{};

void initialize_fingerprint_salt() noexcept {
    fingerprint_salt_ready = BCryptGenRandom(nullptr, fingerprint_salt.data(),
        static_cast<ULONG>(fingerprint_salt.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
}

const char* level_text(Level level) noexcept {
    switch (level) {
    case Level::Trace: return "TRACE";
    case Level::Debug: return "DEBUG";
    case Level::Info: return "INFO";
    case Level::Warning: return "WARN";
    case Level::Error: return "ERROR";
    }
    return "INFO";
}

std::string sanitize_single_line(std::string_view value) {
    constexpr std::size_t MaximumMessageBytes = 64U * 1024U;
    const auto length = std::min(value.size(), MaximumMessageBytes);
    std::string result;
    result.reserve(length);
    for (std::size_t index = 0; index < length; ++index) {
        const auto character = static_cast<unsigned char>(value[index]);
        if (character == '\r' || character == '\n' || character == '\t')
            result.push_back(' ');
        else if (character < 0x20 || character == 0x7f)
            result.push_back('?');
        else
            result.push_back(static_cast<char>(character));
    }
    if (value.size() > MaximumMessageBytes) result += "...<truncated>";
    return result;
}

std::string normalize_token(std::string_view value, std::string_view fallback) {
    constexpr std::size_t MaximumTokenBytes = 32;
    std::string result;
    result.reserve(std::min(value.size(), MaximumTokenBytes));
    for (const auto character : value) {
        if (result.size() >= MaximumTokenBytes) break;
        const auto byte = static_cast<unsigned char>(character);
        if ((byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z') ||
            (byte >= '0' && byte <= '9') || byte == '_' || byte == '-')
            result.push_back(static_cast<char>(byte));
        else
            result.push_back('_');
    }
    if (result.empty()) result.assign(fallback);
    return result;
}

std::string infer_category(std::string_view message) {
    const auto first_separator = message.find_first_of(" :");
    const auto token = message.substr(0, first_separator);
    if (token == "ui_event" || token == "action" || token == "diagnostic")
        return "ui";
    const auto underscore = token.find('_');
    return normalize_token(token.substr(0, underscore), "general");
}

Level infer_level(std::string_view message) {
    if (message.starts_with("diagnostic ")) return Level::Debug;
    if (message.starts_with("error ") || message.starts_with("ERROR ") ||
        message.find(" failed") != std::string_view::npos ||
        message.find(" exception") != std::string_view::npos)
        return Level::Error;
    if (message.find(" rejected") != std::string_view::npos ||
        message.find(" dropped") != std::string_view::npos ||
        message.find(" unavailable") != std::string_view::npos)
        return Level::Warning;
    return Level::Info;
}

std::string make_session_id() {
    const auto ticks = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    return std::format("{:08X}-{:016X}", GetCurrentProcessId(), ticks);
}

std::chrono::system_clock::time_point log_now() {
#ifdef IPHONEMIRROR_TEST_LOG_CLOCK
    return std::chrono::system_clock::now() + log_time_offset;
#else
    return std::chrono::system_clock::now();
#endif
}

std::string timestamp_text(std::chrono::system_clock::time_point now = log_now());
void initialize_session_locked();
bool ensure_session_open_locked(bool implicit, std::string_view timestamp,
    std::size_t required_bytes = 0);

void flush_pending_locked() {
    if (pending_lines == 0) return;
    const auto lines = pending_lines;
    if (!log_file.is_open() || !log_file.good()) {
        dropped_write_count += lines;
        pending_lines = 0;
        return;
    }
    log_file.flush();
    if (!log_file.good()) {
        dropped_write_count += lines;
    }
    pending_lines = 0;
    last_flush = std::chrono::steady_clock::now();
}

void write_line_locked(Level level, std::string_view category,
    std::string_view message) {
    initialize_session_locked();
    const auto sanitized_category = normalize_token(category, "general");
    const auto sanitized_message = sanitize_single_line(message);
    const auto line_number = sequence + 1;
    std::ostringstream line;
    const auto timestamp = timestamp_text();
    line << timestamp << " [tid="
        << std::hash<std::thread::id>{}(std::this_thread::get_id())
        << "] [seq=" << line_number << "] [level=" << level_text(level)
        << "] [category=" << sanitized_category << "] [session="
        << session_id << "] " << sanitized_message << '\n';
    const auto text = line.str();
    if (!ensure_session_open_locked(true, timestamp, text.size())) {
        ++dropped_write_count;
        return;
    }
    log_file << text;
    if (!log_file.good()) {
        // Do not call the logger recursively while handling a disk/handle
        // failure. Leave failbit set so the next logging call can reopen the
        // target, and carry the loss into that recovery marker.
        ++dropped_write_count;
        return;
    }
    sequence = line_number;
    ++line_count;
    if (level == Level::Warning) ++warning_count;
    if (level == Level::Error) ++error_count;
    byte_count += text.size();
    current_file_bytes += text.size();
    ++pending_lines;
    const auto now = std::chrono::steady_clock::now();
    if (pending_lines >= 64 || now - last_flush >= std::chrono::milliseconds(500)) {
        flush_pending_locked();
    }
}

void ensure_flush_thread_locked() {
    if (flush_thread.joinable()) return;
    flush_thread = std::jthread([](std::stop_token token) {
        // Keep the GUI log tail genuinely live even if the last error is the
        // final line written. Flushing on this background worker avoids disk
        // I/O in the USB/decode/render hot paths.
        auto next_date_check = std::chrono::steady_clock::now() + IdleDateCheckInterval;
        while (!token.stop_requested()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
            std::scoped_lock lock(log_mutex);
            flush_pending_locked();
            const auto now = std::chrono::steady_clock::now();
            if (now >= next_date_check) {
                next_date_check = now + IdleDateCheckInterval;
                try {
                    if (log_file.is_open() && timestamp_text().compare(0, 10, current_log_date) != 0) {
                        // Release yesterday's file so an idle session cannot
                        // keep an expired active log locked indefinitely.
                        log_file.close();
                        log_file.clear();
                    }
                } catch (...) {
                    // The flush worker must survive diagnostic failures.
                }
            }
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
    constexpr auto variable = L"IPHONE_MIRROR_LOG_FILE";
    // Environment variables can approach 32 KiB. Allocate one buffer large enough
    // for the whole value and read it in a single call to avoid the TOCTOU race
    // between querying the required size and reading the value.
    std::wstring buffer(32768, L'\0');
    const auto length = GetEnvironmentVariableW(
        variable, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return default_path();
    buffer.resize(length);
    return std::filesystem::path(buffer);
}

bool publish_archive(const std::filesystem::path& source, const std::filesystem::path& path) {
    auto previous = path;
    previous += L".1";
    auto alternate = path;
    alternate += L".2";
    if (MoveFileExW(source.c_str(), previous.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        std::error_code error;
        std::filesystem::remove(alternate, error);
        return true;
    }
    // A viewer may have the previous archive open without delete sharing.
    // Keep logging in that case, with space for only one extra bounded archive.
    return MoveFileExW(source.c_str(), alternate.c_str(), MOVEFILE_REPLACE_EXISTING) != FALSE;
}

bool archive_oversized_log(const std::filesystem::path& path, std::uintmax_t bytes,
    std::filesystem::file_time_type modified) {
    // Obtain delete access before copying. If another process has the legacy
    // file open without delete sharing, leave it and all archives untouched.
    struct FileHandle {
        HANDLE value;
        ~FileHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    };
    const FileHandle input{CreateFileW(path.c_str(), GENERIC_READ | DELETE, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr)};
    if (input.value == INVALID_HANDLE_VALUE) return false;

    auto pending = path;
    pending += L".rotation";
    struct TemporaryArchive {
        const std::filesystem::path& path;
        ~TemporaryArchive() {
            std::error_code error;
            std::filesystem::remove(path, error);
        }
    };
    const TemporaryArchive cleanup{pending};
    {
        std::ofstream archive(pending, std::ios::binary | std::ios::trunc);
        if (!archive.is_open()) return false;
        LARGE_INTEGER offset{};
        offset.QuadPart = static_cast<LONGLONG>(bytes - MaximumLogBytes);
        if (!SetFilePointerEx(input.value, offset, nullptr, FILE_BEGIN)) return false;
        std::array<char, 64 * 1024> buffer;
        auto remaining = MaximumLogBytes;
        while (remaining != 0) {
            const auto count = static_cast<DWORD>(
                std::min<std::uintmax_t>(remaining, buffer.size()));
            DWORD read{};
            if (!ReadFile(input.value, buffer.data(), count, &read, nullptr) || read != count)
                return false;
            archive.write(buffer.data(), count);
            if (!archive.good()) return false;
            remaining -= count;
        }
        archive.close();
        if (!archive.good()) return false;
    }
    std::error_code error;
    std::filesystem::last_write_time(pending, modified, error);
    if (!publish_archive(pending, path)) return false;
    FILE_DISPOSITION_INFO disposition{TRUE};
    return SetFileInformationByHandle(input.value, FileDispositionInfo, &disposition,
        sizeof(disposition)) != FALSE;
}

bool rotate_if_needed(const std::filesystem::path& path,
    std::string_view date, std::uintmax_t required_bytes, bool day_changed) {
    std::error_code error;
    const auto exists = std::filesystem::exists(path, error);
    if (error) return false;
    if (!exists) return true;
    const auto bytes = std::filesystem::file_size(path, error);
    if (error) return false;
    if (bytes == 0) return true;
    const auto modified = std::filesystem::last_write_time(path, error);
    if (error) return false;
    if (!day_changed && bytes <= MaximumLogBytes && required_bytes <= MaximumLogBytes - bytes &&
        timestamp_text(std::chrono::clock_cast<std::chrono::system_clock>(modified))
            .compare(0, 10, date) == 0)
        return true;
    return bytes > MaximumLogBytes ? archive_oversized_log(path, bytes, modified) :
        publish_archive(path, path);
}

std::string timestamp_text(std::chrono::system_clock::time_point now) {
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

void initialize_session_locked() {
    if (!initialized) {
        initialized = true;
        pending_lines = 0;
        sequence = 0;
        line_count = 0;
        warning_count = 0;
        error_count = 0;
        byte_count = 0;
        session_header_written = false;
        session_id = make_session_id();
        session_started = std::chrono::steady_clock::now();
        last_flush = session_started;
    }
}

bool ensure_session_open_locked(bool implicit, std::string_view timestamp,
    std::size_t required_bytes) {
    initialize_session_locked();
    const auto date = timestamp.substr(0, 10);
    if (log_file.is_open() && log_file.good() && current_log_date == date &&
        current_file_bytes <= MaximumLogBytes &&
        required_bytes <= MaximumLogBytes - current_file_bytes) return true;
    if (log_file.is_open()) {
        flush_pending_locked();
        log_file.clear();
        log_file.close();
    }
    log_file.clear();

    const auto custom_log_path =
        GetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", nullptr, 0) != 0;
    const auto path = configured_path();
    // Flushing yesterday's buffered lines after midnight can give yesterday's
    // file today's write time. Keep the date of the open stream across retries.
    const auto day_changed = path == log_path && !current_log_date.empty() &&
        current_log_date != date;
    if (path != log_path) current_log_date.clear();
    log_path = path;
    if (rotation_failure_path == log_path &&
        std::chrono::steady_clock::now() < rotation_retry_after)
        return false;
    if (!log_path.parent_path().empty())
        std::filesystem::create_directories(log_path.parent_path());
    if (!rotate_if_needed(log_path, date, required_bytes + MaximumHeaderBytes, day_changed)) {
        // A held archive or legacy source must not turn high-rate diagnostics
        // into a filesystem retry loop (or repeated 16 MiB tail copies).
        rotation_failure_path = log_path;
        rotation_retry_after = std::chrono::steady_clock::now() + std::chrono::seconds(1);
        return false;
    }
    rotation_failure_path.clear();
    rotation_retry_after = {};
    // Binary mode makes the byte budget independent of CRLF translation.
    log_file.open(log_path, std::ios::out | std::ios::app | std::ios::binary);
    if (!log_file.is_open() || !log_file.good()) {
        if (log_file.is_open()) log_file.close();
        log_file.clear();
        return false;
    }

    const auto dropped_before_open = dropped_write_count;
    if (!session_header_written) {
        log_file << "\n=== iPhoneMirror capture session ===\n";
        log_file << timestamp << " [startup] session=" << session_id
            << " pid=" << GetCurrentProcessId() << " arch="
#if defined(_WIN64)
            << "x64"
#else
            << "x86"
#endif
            << " implicit=" << implicit
            << " log_target=" << (custom_log_path ? "custom" : "default");
        if (dropped_before_open != 0)
            log_file << " dropped_before_start=" << dropped_before_open;
    } else {
        log_file << timestamp << " [log_recovery] session=" << session_id
            << " log_target=" << (custom_log_path ? "custom" : "default")
            << " dropped_before_recovery=" << dropped_before_open;
    }
    log_file << '\n';
    log_file.flush();
    if (!log_file.good()) {
        if (log_file.is_open()) {
            log_file.clear();
            log_file.close();
        }
        log_file.clear();
        return false;
    }

    session_header_written = true;
    current_log_date = date;
    current_file_bytes = std::filesystem::file_size(log_path);
    dropped_write_count = 0;
    last_flush = std::chrono::steady_clock::now();
    ensure_flush_thread_locked();
    return true;
}

} // namespace

#ifdef IPHONEMIRROR_TEST_LOG_CLOCK
void set_log_time_offset_for_testing(std::chrono::system_clock::duration offset) {
    std::scoped_lock lock(log_mutex);
    log_time_offset = offset;
}
#endif

std::string fingerprint(std::string_view value) noexcept {
    try {
        if (value.empty()) return "anon-empty";
        if (value.size() > std::numeric_limits<ULONG>::max()) return "anon-too-large";
        std::call_once(fingerprint_salt_once, initialize_fingerprint_salt);
        if (!fingerprint_salt_ready) return "anon-unavailable";

        BCRYPT_ALG_HANDLE algorithm{};
        BCRYPT_HASH_HANDLE hash{};
        std::array<unsigned char, 32> digest{};
        bool success = BCryptOpenAlgorithmProvider(
            &algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0;
        success = success && BCryptCreateHash(
            algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
        success = success && BCryptHashData(hash, fingerprint_salt.data(),
            static_cast<ULONG>(fingerprint_salt.size()), 0) >= 0;
        success = success && BCryptHashData(hash,
            reinterpret_cast<PUCHAR>(const_cast<char*>(value.data())),
            static_cast<ULONG>(value.size()), 0) >= 0;
        success = success && BCryptFinishHash(hash, digest.data(),
            static_cast<ULONG>(digest.size()), 0) >= 0;
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        if (!success) return "anon-unavailable";

        constexpr char hex[] = "0123456789abcdef";
        std::string result = "anon-";
        result.reserve(result.size() + 24);
        for (std::size_t index = 0; index < 12; ++index) {
            result.push_back(hex[digest[index] >> 4]);
            result.push_back(hex[digest[index] & 0x0f]);
        }
        return result;
    } catch (...) {
        return "anon-unavailable";
    }
}

void initialize() {
    std::scoped_lock lifecycle_lock(lifecycle_mutex);
    std::scoped_lock lock(log_mutex);
    writes_suspended = false;
    rotation_retry_after = {};
    try {
        (void)ensure_session_open_locked(false, timestamp_text());
    } catch (...) {
        initialized = true;
    }
}

void write(std::string_view message) noexcept {
    try {
        std::scoped_lock lock(log_mutex);
        if (writes_suspended) {
            ++dropped_write_count;
            return;
        }
        write_line_locked(infer_level(message), infer_category(message), message);
    } catch (...) {
        // Diagnostics are best-effort and must never alter product behavior.
    }
}

void write(Level level, std::string_view category, std::string_view message) noexcept {
    try {
        std::scoped_lock lock(log_mutex);
        if (writes_suspended) {
            ++dropped_write_count;
            return;
        }
        write_line_locked(level, category, message);
    } catch (...) {
        // Diagnostics are best-effort and must never alter product behavior.
    }
}

void write_event(Level level, std::string_view category, std::string_view event,
    std::string_view details) noexcept {
    try {
        if (details.empty()) {
            write(level, category, event);
            return;
        }
        write(level, category, std::format("event={} {}", event, details));
    } catch (...) {
        // Formatting failures must not escape from a logging call.
    }
}

void shutdown() {
    std::scoped_lock lifecycle_lock(lifecycle_mutex);
    std::jthread worker;
    {
        std::scoped_lock lock(log_mutex);
        // Do this before joining the flush worker. Late writes from API calls
        // already in flight must not lazily reopen the file after shutdown.
        // The next explicit initialize call re-enables writes.
        writes_suspended = true;
        if (!initialized) return;
        if (session_header_written) {
            try {
                const auto timestamp = timestamp_text();
                const auto elapsed = std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - session_started).count();
                std::ostringstream summary;
                summary << timestamp << " [shutdown] session=" << session_id
                    << " lines=" << line_count << " warnings=" << warning_count
                    << " errors=" << error_count << " bytes=" << byte_count
                    << " elapsed_seconds=" << std::fixed << std::setprecision(3)
                    << elapsed << " dropped_after_start=" << dropped_write_count << '\n';
                const auto text = summary.str();
                if (ensure_session_open_locked(false, timestamp, text.size())) {
                    log_file << text;
                    current_file_bytes += text.size();
                }
            } catch (...) {
                // Diagnostics must not interrupt core shutdown.
            }
        }
        worker = std::move(flush_thread);
    }
    if (worker.joinable()) {
        worker.request_stop();
        worker.join();
    }
    {
        std::scoped_lock lock(log_mutex);
        if (log_file.is_open()) {
            log_file.clear();
            log_file.flush();
            log_file.close();
        }
        log_file.clear();
        initialized = false;
        session_header_written = false;
        pending_lines = 0;
        current_file_bytes = 0;
        current_log_date.clear();
        session_id.clear();
        session_started = {};
    }
}

} // namespace iPhoneMirror::logging
