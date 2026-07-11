#pragma once

#include <cstdint>
#include <string>
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

[[nodiscard]] ServiceState apple_mobile_device_service_state() noexcept;
[[nodiscard]] std::vector<PhysicalAppleDevice> discover_physical_apple_usb_devices();

} // namespace iPhoneMirror::device

