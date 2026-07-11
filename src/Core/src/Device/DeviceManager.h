#pragma once

#include "iPhoneMirror/CoreApi.h"

#include <cstdint>
#include <string>
#include <vector>

namespace iPhoneMirror::device {

struct DeviceRecord {
    std::uint32_t device_id{};
    std::uint16_t mux_port{};
    ConnectionState state{ConnectionState::Disconnected};
    bool usb_connected{};
    bool pair_record_present{};
    bool lockdown_accessible{};
    std::wstring udid;
    std::wstring name;
    std::wstring product_type;
    std::wstring os_version;
    std::wstring connection_type;
    std::wstring status;
};

struct EnvironmentRecord {
    bool service_installed{};
    bool service_running{};
    bool standard_mux{};
    bool capture_mux{};
    std::uint32_t physical_device_count{};
    bool libusb_runtime{};
    bool usbdk_backend{};
    std::uint32_t libusb_apple_devices{};
    bool libusb0_available{};
    std::uint32_t libusb0_apple_devices{};
    std::wstring libusb_version;
    std::wstring diagnostic;
};

class DeviceManager {
public:
    [[nodiscard]] EnvironmentRecord environment() const;
    [[nodiscard]] std::vector<DeviceRecord> refresh() const;
};

} // namespace iPhoneMirror::device
