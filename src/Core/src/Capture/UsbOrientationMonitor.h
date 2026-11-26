#pragma once

#include <chrono>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <stop_token>
#include <string_view>

namespace iPhoneMirror::capture {
namespace detail {
[[nodiscard]] constexpr std::optional<unsigned> springboard_rotation_turns(unsigned value) noexcept {
    switch (value) {
    case 1: return 0;
    case 2: return 2;
    case 3: return 3;
    case 4: return 1;
    default: return std::nullopt;
    }
}

class OrientationTimeline {
public:
    using Clock = std::chrono::steady_clock;
    void observe(unsigned orientation, Clock::time_point at);
    void clear() noexcept;
    [[nodiscard]] std::optional<unsigned> at(Clock::time_point time) const;
private:
    mutable std::mutex mutex_;
    std::deque<std::pair<Clock::time_point, unsigned>> samples_;
};
}

// Owns a read-only SpringBoard helper and its shared capture-usbmux broker.
// Closing stdin requests orderly service/interface release before USB restore.
class UsbOrientationMonitor {
public:
    UsbOrientationMonitor();
    ~UsbOrientationMonitor();
    UsbOrientationMonitor(const UsbOrientationMonitor&) = delete;
    UsbOrientationMonitor& operator=(const UsbOrientationMonitor&) = delete;
    [[nodiscard]] bool start(std::string_view serial, std::stop_token stop, bool required = true);
    void stop() noexcept;
    [[nodiscard]] std::optional<unsigned> at(detail::OrientationTimeline::Clock::time_point time) const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
