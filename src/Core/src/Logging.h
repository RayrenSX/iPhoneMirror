#pragma once

#include <string>
#include <string_view>

namespace iPhoneMirror::logging {

// Log levels and categories are kept at the native boundary so every
// subsystem can add structured context without duplicating timestamp/thread
// formatting. The legacy write(message) overload remains the default path.
enum class Level {
    Trace,
    Debug,
    Info,
    Warning,
    Error,
};

void initialize();
void write(std::string_view message) noexcept;
void write(Level level, std::string_view category, std::string_view message) noexcept;
void write_event(Level level, std::string_view category, std::string_view event,
    std::string_view details = {}) noexcept;
// Produces a process-stable, salted SHA-256 label. The random salt changes on
// every launch so identifiers can be correlated within one diagnostic session
// without making logs a cross-session device tracking record.
[[nodiscard]] std::string fingerprint(std::string_view value) noexcept;
void shutdown();

} // namespace iPhoneMirror::logging
