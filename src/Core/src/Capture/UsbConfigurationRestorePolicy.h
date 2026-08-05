#pragma once

namespace iPhoneMirror::capture::detail {

enum class UsbConfigurationObservation {
    Missing,
    Normal,
    QuickTime,
};

enum class UsbConfigurationRestoreAction {
    Wait,
    DisableQuickTime,
    Complete,
};

class UsbConfigurationRestorePolicy final {
public:
    [[nodiscard]] UsbConfigurationRestoreAction observe(
        UsbConfigurationObservation observation) noexcept {
        if (complete_) return UsbConfigurationRestoreAction::Complete;
        if (observation == UsbConfigurationObservation::Normal) {
            complete_ = true;
            return UsbConfigurationRestoreAction::Complete;
        }
        if (observation == UsbConfigurationObservation::QuickTime &&
            !disable_requested_) {
            disable_requested_ = true;
            return UsbConfigurationRestoreAction::DisableQuickTime;
        }
        return UsbConfigurationRestoreAction::Wait;
    }

    [[nodiscard]] bool disable_requested() const noexcept {
        return disable_requested_;
    }

private:
    bool disable_requested_{};
    bool complete_{};
};

} // namespace iPhoneMirror::capture::detail
