#pragma once

#include <array>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace iPhoneMirror::device {

struct ServiceState {
    bool installed{};
    bool running{};
};

struct PhysicalAppleDevice {
    std::wstring description;
    std::wstring hardware_id;
};

struct AppleNormalUsbStackEvidence {
    bool parent_started{};
    bool media_interface_started{};     // MI_00 / WPD
    bool management_interface_started{}; // MI_01 / Apple Mobile Device
};

[[nodiscard]] constexpr bool is_complete_apple_normal_usb_stack(
    AppleNormalUsbStackEvidence evidence) noexcept {
    return evidence.parent_started && evidence.media_interface_started &&
        evidence.management_interface_started;
}

class UnsafeAppleUsbFilterStackError final : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

enum class AppleUsbFilterSafety {
    Safe,
    Unsafe,
    Indeterminate,
};

struct AppleUsbFilterSafetyResult {
    AppleUsbFilterSafety safety{AppleUsbFilterSafety::Indeterminate};
    std::string diagnostic;
};

[[nodiscard]] bool is_unsafe_apple_usb_filter_combination(
    std::span<const std::wstring> upper_filters,
    std::span<const std::wstring> lower_filters) noexcept;

[[nodiscard]] ServiceState apple_mobile_device_service_state() noexcept;
[[nodiscard]] std::vector<PhysicalAppleDevice> discover_physical_apple_usb_devices();
[[nodiscard]] bool is_apple_usb_parent_instance_id(
    std::wstring_view instance_id) noexcept;
// Apple USB-C to 3.5mm Headphone Jack Adapter (05AC:110A) is surfaced by
// Apple USB services but is not an iPhone/iPad capture device.
[[nodiscard]] constexpr bool is_apple_audio_adapter_product_id(
    std::uint32_t product_id) noexcept {
    return product_id == 0x110A;
}
// Apple accessories share Apple's vendor ID. This explicit table follows
// config/apple-mobile-capture-pids.txt and Apple's iPhone USB driver entries;
// keep it in sync with DriverConstants.cs and remove_selected_iphone_drivers.ps1.
// Apple TV, Watch, and HomePod must not become capture targets.
[[nodiscard]] constexpr bool is_apple_mobile_capture_product_id(
    std::uint32_t product_id) noexcept {
    constexpr std::array<std::uint32_t, 24> mobile_capture_product_ids{
        0x1290, 0x1291, 0x1292, 0x1293, 0x1294, 0x1297, 0x1299, 0x129A, 0x129C, 0x129D, 0x129E, 0x129F, 0x12A0, 0x12A1, 0x12A2, 0x12A3, 0x12A4, 0x12A5, 0x12A6, 0x12A8, 0x12A9, 0x12AA, 0x12AB, 0x12AC};
    for (const auto allowed_product_id : mobile_capture_product_ids) {
        if (product_id == allowed_product_id) return true;
    }
    return false;
}
[[nodiscard]] bool is_apple_mobile_capture_parent_instance_id(
    std::wstring_view instance_id) noexcept;
[[nodiscard]] bool apple_usb_parent_instance_matches_serial(
    std::wstring_view instance_id, std::string_view serial) noexcept;
// libusb-win32 publishes one device interface for the filtered Apple parent.
// The interface path contains the exact VID, PID and serial, unlike a generic
// parent STARTED notification which can precede the filter's usable handle.
[[nodiscard]] bool libusb0_apple_interface_path_matches(
    std::wstring_view symbolic_link, std::uint16_t product_id,
    std::string_view serial) noexcept;
[[nodiscard]] bool is_apple_usb_parent_present(
    std::string_view serial) noexcept;
// Read-only PnP evidence that the selected parent has restarted its normal
// Apple management interfaces. This never opens a USB device handle.
[[nodiscard]] AppleNormalUsbStackEvidence inspect_apple_normal_usb_stack(
    std::string_view serial) noexcept;
[[nodiscard]] bool is_apple_normal_usb_stack_present(
    std::string_view serial) noexcept;

// The Apple composite stack can combine the legacy libusb0 upper filter with
// Apple's lower/KMDF filters.  That combination has produced kernel WDF/UCX
// lifetime bugchecks during QuickTime configuration changes on affected
// Windows installations. This read-only check is used as a native fail-safe
// before loading a USB backend or sending a configuration/cancellation IRP.
// An indeterminate result must be treated the same as unsafe by callers.
[[nodiscard]] AppleUsbFilterSafetyResult inspect_apple_usb_filter_stack(
    std::string_view serial) noexcept;

} // namespace iPhoneMirror::device
