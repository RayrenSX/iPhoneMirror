#pragma once

#include <cstdint>

namespace iPhoneMirror::transport::detail {

enum class UsbInterfaceTransitionEvent {
    Removal,
    Arrival,
};

class UsbInterfaceTransitionPolicy final {
public:
    void observe(UsbInterfaceTransitionEvent event) noexcept {
        if (event == UsbInterfaceTransitionEvent::Removal) {
            removed_ = true;
            arrived_ = false;
            ++generation_;
            return;
        }
        if (!removed_) return;
        arrived_ = true;
        ++generation_;
    }

    [[nodiscard]] bool removed() const noexcept { return removed_; }
    [[nodiscard]] bool arrived() const noexcept { return arrived_; }
    [[nodiscard]] bool complete() const noexcept {
        return removed_ && arrived_;
    }
    [[nodiscard]] std::uint64_t generation() const noexcept {
        return generation_;
    }

private:
    std::uint64_t generation_{};
    bool removed_{};
    bool arrived_{};
};

} // namespace iPhoneMirror::transport::detail
