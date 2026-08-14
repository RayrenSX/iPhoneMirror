#pragma once

#include <span>

namespace iPhoneMirror::capture::detail {

enum class UsbConfigurationObservation {
    Missing,
    Normal,
    VerifiedNormal,
    QuickTime,
};

enum class UsbConfigurationRestoreAction {
    Wait,
    DisableQuickTime,
    Complete,
};

[[nodiscard]] inline UsbConfigurationObservation
stabilize_normal_configuration_observation(
    bool normal, unsigned int consecutive_normal_observations) noexcept {
    return normal && consecutive_normal_observations >= 2
        ? UsbConfigurationObservation::VerifiedNormal
        : UsbConfigurationObservation::Missing;
}

[[nodiscard]] inline bool is_libusb0_quicktime_pnp_state(
    bool exact_management_device_present, bool normal_stack_present,
    bool parent_present, bool transition_seen) noexcept {
    // The parent must already be started again. A missing parent is merely an
    // in-flight PnP transition and opening or restoring through libusb0 in
    // that window is precisely the unsafe race this state machine prevents.
    // ListDevices may retain a stale row after the management interface has
    // gone away, so it is diagnostic only for this negative classification.
    // It remains authoritative positive evidence when paired with a started
    // normal management child in the restore path.
    (void)exact_management_device_present;
    return transition_seen && !normal_stack_present && parent_present;
}

struct UsbConfigurationCandidateEvidence {
    bool serial_matches{};
    bool serial_available{};
    bool quicktime{};
};

[[nodiscard]] inline UsbConfigurationObservation
classify_libusb0_configuration_observation(
    std::span<const UsbConfigurationCandidateEvidence> candidates,
    bool exact_management_device_present) noexcept {
    bool exact_normal{};
    bool unknown_quicktime{};
    for (const auto& candidate : candidates) {
        if (candidate.serial_matches) {
            if (candidate.quicktime)
                return UsbConfigurationObservation::QuickTime;
            exact_normal = true;
        } else if (!candidate.serial_available && candidate.quicktime) {
            unknown_quicktime = true;
        }
    }
    if (exact_normal) return UsbConfigurationObservation::Normal;
    // USBMux exposes the exact UDID only after Apple Mobile Device has opened
    // the normal management interface again. This is stronger than a started
    // PnP parent (which remains present in QuickTime mode) and does not require
    // opening libusb0 handles while AppleUsbFilter is re-enumerating.
    if (exact_management_device_present)
        return UsbConfigurationObservation::Normal;
    if (unknown_quicktime) return UsbConfigurationObservation::Missing;
    return UsbConfigurationObservation::Missing;
}

class UsbConfigurationRestorePolicy final {
public:
    explicit UsbConfigurationRestorePolicy(bool primary_request_sent = false) noexcept
        : transition_requested_(primary_request_sent) {}

    [[nodiscard]] UsbConfigurationRestoreAction observe(
        UsbConfigurationObservation observation) noexcept {
        if (complete_) return UsbConfigurationRestoreAction::Complete;
        if (observation == UsbConfigurationObservation::VerifiedNormal) {
            complete_ = true;
            return UsbConfigurationRestoreAction::Complete;
        }
        if (observation == UsbConfigurationObservation::Normal) {
            // A disconnecting 0x52 request is not complete merely because the
            // old USBMux/PnP node is still visible. Require the exact target to
            // disappear once and then return before releasing the restore
            // lease to another session.
            if (transition_requested_ && !missing_after_request_)
                return UsbConfigurationRestoreAction::Wait;
            complete_ = true;
            return UsbConfigurationRestoreAction::Complete;
        }
        if (observation == UsbConfigurationObservation::Missing) {
            if (transition_requested_) missing_after_request_ = true;
            return UsbConfigurationRestoreAction::Wait;
        }
        if (observation == UsbConfigurationObservation::QuickTime &&
            !disable_requested_ &&
            (!transition_requested_ || missing_after_request_)) {
            disable_requested_ = true;
            transition_requested_ = true;
            missing_after_request_ = false;
            return UsbConfigurationRestoreAction::DisableQuickTime;
        }
        return UsbConfigurationRestoreAction::Wait;
    }

    [[nodiscard]] bool disable_requested() const noexcept {
        return disable_requested_;
    }

private:
    bool transition_requested_{};
    bool missing_after_request_{};
    bool disable_requested_{};
    bool complete_{};
};

} // namespace iPhoneMirror::capture::detail
