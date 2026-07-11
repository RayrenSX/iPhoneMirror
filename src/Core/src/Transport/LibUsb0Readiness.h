#pragma once

#include <string_view>

namespace iPhoneMirror::transport {

// Apple exposes the same physical-device serial as either a 24-character USB
// descriptor value or a 25-character usbmux UDID with a hyphen after byte 8.
// Keep readiness checks and the capture open path on one comparison rule.
[[nodiscard]] bool apple_usb_serial_equal(
    std::string_view left, std::string_view right) noexcept;

// Read-only exact-device probe. This only enumerates libusb0 devices and opens
// their descriptors to read serial strings; it never changes USB state.
[[nodiscard]] bool is_libusb0_device_available(std::string_view serial);

} // namespace iPhoneMirror::transport
