#include "Capture/UsbConfigurationRestorePolicy.h"

#include <array>
#include <iostream>
#include <span>

namespace {

using iPhoneMirror::capture::detail::UsbConfigurationObservation;
using iPhoneMirror::capture::detail::UsbConfigurationRestoreAction;
using iPhoneMirror::capture::detail::UsbConfigurationRestorePolicy;

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

    if (failures != 0) {
        std::cerr << failures << " test(s) failed\n";
        return 1;
    }
    std::cout << "USB configuration restore policy tests passed\n";
    return 0;
}
