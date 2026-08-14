#include "Capture/UsbConfigurationRestorePolicy.h"
#include "Transport/UsbInterfaceTransitionPolicy.h"

#include <array>
#include <iostream>
#include <span>

namespace {

using iPhoneMirror::capture::detail::UsbConfigurationObservation;
using iPhoneMirror::capture::detail::UsbConfigurationRestoreAction;
using iPhoneMirror::capture::detail::UsbConfigurationRestorePolicy;
using iPhoneMirror::capture::detail::UsbConfigurationCandidateEvidence;
using iPhoneMirror::capture::detail::classify_libusb0_configuration_observation;
using iPhoneMirror::capture::detail::stabilize_normal_configuration_observation;
using iPhoneMirror::capture::detail::is_libusb0_quicktime_pnp_state;
using iPhoneMirror::transport::detail::UsbInterfaceTransitionEvent;
using iPhoneMirror::transport::detail::UsbInterfaceTransitionPolicy;

int failures{};

void check(bool condition, const char* message) {
    if (condition) return;
    ++failures;
    std::cerr << "FAIL: " << message << '\n';
}

int count_disable_actions(std::span<const UsbConfigurationObservation> observations) {
    UsbConfigurationRestorePolicy policy;
    int sends{};
    for (const auto observation : observations) {
        if (policy.observe(observation) ==
            UsbConfigurationRestoreAction::DisableQuickTime) {
            ++sends;
        }
    }
    return sends;
}

} // namespace

int main() {
    constexpr std::array normal_exact{
        UsbConfigurationCandidateEvidence{true, true, false},
        UsbConfigurationCandidateEvidence{false, true, true},
    };
    check(classify_libusb0_configuration_observation(normal_exact, true) ==
            UsbConfigurationObservation::Normal,
        "an exact normal descriptor is authoritative even while another known phone streams");

    constexpr std::array quicktime_exact{
        UsbConfigurationCandidateEvidence{true, true, true},
        UsbConfigurationCandidateEvidence{true, true, false},
    };
    check(classify_libusb0_configuration_observation(quicktime_exact, true) ==
            UsbConfigurationObservation::QuickTime,
        "an exact QuickTime descriptor vetoes normal evidence");

    constexpr std::array unreadable_normal{
        UsbConfigurationCandidateEvidence{false, false, false},
        UsbConfigurationCandidateEvidence{false, true, false},
    };
    check(classify_libusb0_configuration_observation(unreadable_normal, true) ==
            UsbConfigurationObservation::Normal,
        "exact USBMux presence confirms normal management configuration without descriptor opens");

    constexpr std::array unreadable_quicktime{
        UsbConfigurationCandidateEvidence{false, false, true},
        UsbConfigurationCandidateEvidence{false, false, false},
    };
    check(classify_libusb0_configuration_observation(unreadable_quicktime, true) ==
            UsbConfigurationObservation::Normal,
        "exact USBMux presence outranks anonymous QuickTime nodes from another phone");
    constexpr std::array exact_normal_with_unknown_quicktime{
        UsbConfigurationCandidateEvidence{true, true, false},
        UsbConfigurationCandidateEvidence{false, false, true},
    };
    check(classify_libusb0_configuration_observation(
            exact_normal_with_unknown_quicktime, true) ==
            UsbConfigurationObservation::Normal,
        "an exact target normal descriptor outranks another phone's unreadable QuickTime node");
    check(classify_libusb0_configuration_observation(
            unreadable_quicktime, false) ==
            UsbConfigurationObservation::Missing,
        "anonymous QuickTime candidates cannot identify the target without USBMux evidence");
    check(classify_libusb0_configuration_observation(unreadable_normal, false) ==
            UsbConfigurationObservation::Missing,
        "normal-looking candidates without exact serial or USBMux identity remain unconfirmed");
    check(classify_libusb0_configuration_observation({}, true) ==
            UsbConfigurationObservation::Normal,
        "USBMux presence confirms restore even when libusb0 has not refreshed its device list");
    constexpr std::array other_known_phone{
        UsbConfigurationCandidateEvidence{false, true, false},
    };
    check(classify_libusb0_configuration_observation(other_known_phone, false) ==
            UsbConfigurationObservation::Missing,
        "another known Apple device cannot satisfy the target without exact USBMux evidence");

    UsbConfigurationRestorePolicy normal;
    check(normal.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Complete &&
            normal.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::Complete &&
            !normal.disable_requested(),
        "normal configuration is terminal and sends no disable request");

    constexpr std::array missing{
        UsbConfigurationObservation::Missing,
        UsbConfigurationObservation::Missing,
        UsbConfigurationObservation::Missing,
    };
    check(count_disable_actions(missing) == 0,
        "missing devices are only observed");

    constexpr std::array repeated_quicktime{
        UsbConfigurationObservation::QuickTime,
        UsbConfigurationObservation::QuickTime,
        UsbConfigurationObservation::Missing,
        UsbConfigurationObservation::QuickTime,
        UsbConfigurationObservation::Normal,
    };
    check(count_disable_actions(repeated_quicktime) == 1,
        "a restore attempt sends at most one disable request");

    UsbConfigurationRestorePolicy failed_transport;
    int simulated_sends{};
    try {
        if (failed_transport.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::DisableQuickTime) {
            ++simulated_sends;
            throw 1;
        }
    } catch (...) {
    }
    for (int index{}; index < 4; ++index) {
        if (failed_transport.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::DisableQuickTime) {
            ++simulated_sends;
        }
    }
    check(simulated_sends == 1,
        "a transport error after disable never permits a retry");
    check(failed_transport.observe(UsbConfigurationObservation::Missing) ==
            UsbConfigurationRestoreAction::Wait &&
            failed_transport.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Complete,
        "after disable the policy only waits for normal configuration");

    UsbConfigurationRestorePolicy active_handle_restore(true);
    check(active_handle_restore.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::Wait &&
            active_handle_restore.observe(UsbConfigurationObservation::Missing) ==
            UsbConfigurationRestoreAction::Wait &&
            active_handle_restore.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::DisableQuickTime &&
            active_handle_restore.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Wait &&
            active_handle_restore.observe(UsbConfigurationObservation::Missing) ==
            UsbConfigurationRestoreAction::Wait &&
            active_handle_restore.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::Wait &&
            active_handle_restore.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Complete,
        "active-handle restore permits one fallback only after disconnect and explicit QuickTime reappearance");

    UsbConfigurationRestorePolicy active_handle_missing(true);
    for (int index{}; index < 8; ++index) {
        check(active_handle_missing.observe(UsbConfigurationObservation::Missing) ==
                UsbConfigurationRestoreAction::Wait,
            "a missing device after active-handle restore is observation-only");
    }
    check(!active_handle_missing.disable_requested(),
        "missing observations never open a fallback control handle");

    UsbConfigurationRestorePolicy separate_control_restore;
    check(separate_control_restore.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::DisableQuickTime &&
            separate_control_restore.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Wait &&
            separate_control_restore.observe(UsbConfigurationObservation::Missing) ==
            UsbConfigurationRestoreAction::Wait &&
            separate_control_restore.observe(UsbConfigurationObservation::Normal) ==
            UsbConfigurationRestoreAction::Complete,
        "a separate control restore completes only after target disappearance and return");

    UsbConfigurationRestorePolicy exact_configuration_restore;
    check(exact_configuration_restore.observe(UsbConfigurationObservation::QuickTime) ==
            UsbConfigurationRestoreAction::DisableQuickTime &&
            exact_configuration_restore.observe(
                UsbConfigurationObservation::VerifiedNormal) ==
            UsbConfigurationRestoreAction::Complete &&
            exact_configuration_restore.disable_requested(),
        "independent exact PnP and USBMux evidence completes without requiring a cached USBMux disappearance");

    UsbConfigurationRestorePolicy missed_fast_transition;
    check(missed_fast_transition.observe(
            UsbConfigurationObservation::VerifiedNormal) ==
            UsbConfigurationRestoreAction::Complete &&
            !missed_fast_transition.disable_requested(),
        "stable exact normal evidence completes when polling missed a fast disappearance");

    check(stabilize_normal_configuration_observation(true, 1) ==
            UsbConfigurationObservation::Missing &&
            stabilize_normal_configuration_observation(false, 0) ==
            UsbConfigurationObservation::Missing &&
            stabilize_normal_configuration_observation(true, 2) ==
            UsbConfigurationObservation::VerifiedNormal,
        "PnP and USBMux normal evidence must remain stable for two consecutive observations");
    check(is_libusb0_quicktime_pnp_state(false, false, true, true),
        "a restarted parent without USBMux or normal children is QuickTime-ready");
    check(!is_libusb0_quicktime_pnp_state(false, false, false, true),
        "a missing parent remains an in-flight PnP transition");
    check(!is_libusb0_quicktime_pnp_state(false, true, true, true),
        "a recovered normal management child is not QuickTime-ready");
    check(is_libusb0_quicktime_pnp_state(true, false, true, true),
        "a stale USBMux row cannot veto stable QuickTime PnP evidence");
    check(!is_libusb0_quicktime_pnp_state(false, false, true, false),
        "QuickTime readiness requires an observed configuration transition");

    UsbInterfaceTransitionPolicy interface_transition;
    interface_transition.observe(UsbInterfaceTransitionEvent::Arrival);
    check(!interface_transition.removed() && !interface_transition.arrived() &&
            !interface_transition.complete() &&
            interface_transition.generation() == 0,
        "an arrival before target removal cannot complete a configuration switch");
    interface_transition.observe(UsbInterfaceTransitionEvent::Removal);
    check(interface_transition.removed() && !interface_transition.arrived() &&
            !interface_transition.complete() &&
            interface_transition.generation() == 1,
        "target removal begins but does not complete the interface transition");
    interface_transition.observe(UsbInterfaceTransitionEvent::Arrival);
    check(interface_transition.complete() &&
            interface_transition.generation() == 2,
        "the exact target completes only after ordered removal and arrival");
    interface_transition.observe(UsbInterfaceTransitionEvent::Removal);
    check(interface_transition.removed() && !interface_transition.arrived() &&
            !interface_transition.complete() &&
            interface_transition.generation() == 3,
        "a later removal invalidates a previously completed stable transition");

    if (failures != 0) {
        std::cerr << failures << " test(s) failed\n";
        return 1;
    }
    std::cout << "USB configuration restore policy tests passed\n";
    return 0;
}
