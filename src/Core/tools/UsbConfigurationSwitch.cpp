#include <Windows.h>
#include <cfgmgr32.h>

#include "Transport/UsbInterfaceTransitionPolicy.h"

#include <lusb0_usb.h>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <cwctype>
#include <cstdint>
#include <format>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>

namespace {

constexpr std::uint16_t AppleVendorId = 0x05ac;
constexpr std::uint8_t QuickTimeSubclass = 0x2a;
constexpr GUID LibUsb0DeviceInterfaceGuid{
    0xf9f3ff14, 0xae21, 0x48a0,
    {0x8a, 0x25, 0x80, 0x11, 0xa7, 0xa9, 0x31, 0xd9}};
constexpr auto ReenumerationTimeout = std::chrono::seconds(15);
constexpr auto StableInterfaceWindow = std::chrono::milliseconds(750);
constexpr auto PresenceProbeInterval = std::chrono::milliseconds(100);

std::string narrow_ascii(std::wstring_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto ch : value) {
        if (ch > 0x7f) return {};
        result.push_back(static_cast<char>(ch));
    }
    return result;
}

std::string normalize(std::string_view value) {
    std::string result(value);
    const auto embedded_null = result.find('\0');
    if (embedded_null != std::string::npos) {
        if (!std::all_of(result.begin() + static_cast<std::ptrdiff_t>(embedded_null),
                result.end(), [](char ch) { return ch == '\0'; }))
            return {};
        result.resize(embedded_null);
    }
    result.erase(std::remove_if(result.begin(), result.end(),
        [](unsigned char ch) { return ch == '-' || std::isspace(ch); }),
        result.end());
    std::ranges::transform(result, result.begin(),
        [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
    return result;
}

std::wstring normalize(std::wstring_view value) {
    std::wstring result(value);
    const auto embedded_null = result.find(L'\0');
    if (embedded_null != std::wstring::npos) result.resize(embedded_null);
    result.erase(std::remove_if(result.begin(), result.end(),
        [](wchar_t ch) { return ch == L'-' || std::iswspace(ch); }),
        result.end());
    std::ranges::transform(result, result.begin(),
        [](wchar_t ch) { return static_cast<wchar_t>(std::towupper(ch)); });
    return result;
}

std::wstring widen_ascii(std::string_view value) {
    std::wstring result;
    result.reserve(value.size());
    for (const auto ch : value) {
        if (static_cast<unsigned char>(ch) > 0x7f) return {};
        result.push_back(static_cast<wchar_t>(static_cast<unsigned char>(ch)));
    }
    return result;
}

bool exact_interface_path_matches(std::wstring_view symbolic_link,
    std::uint16_t product_id, std::string_view serial) {
    const auto wanted_serial = normalize(widen_ascii(serial));
    if (product_id == 0 || wanted_serial.empty()) return false;

    std::wstring upper_link(symbolic_link);
    std::ranges::transform(upper_link, upper_link.begin(),
        [](wchar_t ch) { return static_cast<wchar_t>(std::towupper(ch)); });
    wchar_t prefix[64]{};
    swprintf_s(prefix, L"\\\\?\\USB#VID_05AC&PID_%04X#", product_id);
    const std::wstring_view expected_prefix(prefix);
    if (!std::wstring_view(upper_link).starts_with(expected_prefix)) return false;
    const auto serial_begin = expected_prefix.size();
    const auto serial_end = upper_link.find(L'#', serial_begin);
    if (serial_end == std::wstring::npos ||
        normalize(std::wstring_view(upper_link).substr(
            serial_begin, serial_end - serial_begin)) != wanted_serial)
        return false;
    constexpr std::wstring_view InterfaceSuffix =
        L"#{F9F3FF14-AE21-48A0-8A25-8011A7A931D9}";
    return std::wstring_view(upper_link).substr(serial_end) == InterfaceSuffix;
}

bool exact_interface_present(std::uint16_t product_id,
    std::string_view serial) noexcept {
    try {
        ULONG characters{};
        auto result = CM_Get_Device_Interface_List_SizeW(&characters,
            const_cast<GUID*>(&LibUsb0DeviceInterfaceGuid), nullptr,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (result != CR_SUCCESS || characters < 2) return false;
        std::wstring paths(characters, L'\0');
        result = CM_Get_Device_Interface_ListW(
            const_cast<GUID*>(&LibUsb0DeviceInterfaceGuid), nullptr,
            paths.data(), characters, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (result != CR_SUCCESS) return false;
        for (const wchar_t* path = paths.c_str(); *path;
             path += std::wcslen(path) + 1) {
            if (exact_interface_path_matches(path, product_id, serial))
                return true;
        }
    } catch (...) {
    }
    return false;
}

class ExactInterfaceTransition final {
public:
    ExactInterfaceTransition(std::uint16_t product_id, std::string serial)
        : product_id_(product_id), serial_(std::move(serial)) {
        CM_NOTIFY_FILTER filter{};
        filter.cbSize = sizeof(filter);
        filter.FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE;
        filter.u.DeviceInterface.ClassGuid = LibUsb0DeviceInterfaceGuid;
        const auto result = CM_Register_Notification(&filter, this,
            &ExactInterfaceTransition::on_notification, &notification_);
        if (result != CR_SUCCESS) throw std::runtime_error(std::format(
            "register exact libusb0 interface notification: configret={}",
            result));
    }

    ~ExactInterfaceTransition() {
        if (notification_) CM_Unregister_Notification(notification_);
    }
    ExactInterfaceTransition(const ExactInterfaceTransition&) = delete;
    ExactInterfaceTransition& operator=(const ExactInterfaceTransition&) = delete;

    bool wait_until_stable(std::chrono::steady_clock::time_point deadline) {
        std::unique_lock lock(mutex_);
        while (std::chrono::steady_clock::now() < deadline) {
            if (!transition_.complete()) {
                condition_.wait_until(lock, deadline);
                continue;
            }

            const auto generation = transition_.generation();
            lock.unlock();
            const bool present = exact_interface_present(product_id_, serial_);
            lock.lock();
            if (!present || !transition_.complete() ||
                transition_.generation() != generation) {
                stable_since_ = {};
            } else {
                const auto now = std::chrono::steady_clock::now();
                if (stable_since_ == std::chrono::steady_clock::time_point{})
                    stable_since_ = now;
                if (now - stable_since_ >= StableInterfaceWindow) return true;
            }
            condition_.wait_until(lock, std::min(deadline,
                std::chrono::steady_clock::now() + PresenceProbeInterval));
        }
        return false;
    }

    bool removed() const noexcept {
        std::scoped_lock lock(mutex_);
        return transition_.removed();
    }

    bool arrived() const noexcept {
        std::scoped_lock lock(mutex_);
        return transition_.arrived();
    }

private:
    static DWORD CALLBACK on_notification(HCMNOTIFICATION, PVOID context,
        CM_NOTIFY_ACTION action, PCM_NOTIFY_EVENT_DATA event_data,
        DWORD) noexcept {
        auto* self = static_cast<ExactInterfaceTransition*>(context);
        if (!self || !event_data || event_data->FilterType !=
                CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE ||
            !exact_interface_path_matches(
                event_data->u.DeviceInterface.SymbolicLink,
                self->product_id_, self->serial_))
            return ERROR_SUCCESS;
        try {
            std::scoped_lock lock(self->mutex_);
            if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) {
                self->transition_.observe(
                    iPhoneMirror::transport::detail::
                        UsbInterfaceTransitionEvent::Removal);
                self->stable_since_ = {};
            } else if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL &&
                self->transition_.removed()) {
                self->transition_.observe(
                    iPhoneMirror::transport::detail::
                        UsbInterfaceTransitionEvent::Arrival);
                self->stable_since_ = {};
            } else {
                return ERROR_SUCCESS;
            }
            self->condition_.notify_all();
        } catch (...) {
        }
        return ERROR_SUCCESS;
    }

    std::uint16_t product_id_{};
    std::string serial_;
    HCMNOTIFICATION notification_{};
    mutable std::mutex mutex_;
    std::condition_variable condition_;
    std::chrono::steady_clock::time_point stable_since_{};
    iPhoneMirror::transport::detail::UsbInterfaceTransitionPolicy transition_;
};

bool quicktime_descriptor_matches(const struct usb_device& device,
    std::uint8_t expected_configuration) noexcept {
    for (int c = 0; c < device.descriptor.bNumConfigurations; ++c) {
        const auto& config = device.config[c];
        if (config.bConfigurationValue != expected_configuration) continue;
        for (int i = 0; i < config.bNumInterfaces; ++i) {
            const auto& group = config.interface[i];
            for (int a = 0; a < group.num_altsetting; ++a) {
                const auto& interface = group.altsetting[a];
                if (interface.bInterfaceClass == 0xff &&
                    interface.bInterfaceSubClass == QuickTimeSubclass)
                    return true;
            }
        }
    }
    return false;
}

std::string topology_for(const struct usb_device& device) {
    if (!device.bus) return {};
    return std::format("{}:{:08x}:{}", device.bus->dirname,
        device.bus->location, device.filename);
}

std::string topology_scope(std::string_view value) {
    const auto separator = value.rfind(':');
    return separator == std::string_view::npos
        ? std::string(value) : std::string(value.substr(0, separator));
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 21;
    const std::wstring_view operation(argv[1]);
    if (operation != L"activate" && operation != L"restore") return 22;
    const auto expected_serial = normalize(narrow_ascii(argv[2]));
    if (expected_serial.empty()) return 23;
    const auto expected_topology = narrow_ascii(argv[4]);
    const auto expected_topology_scope = topology_scope(expected_topology);
    unsigned long parsed_configuration{};
    try {
        std::size_t consumed{};
        parsed_configuration = std::stoul(argv[3], &consumed, 10);
        if (consumed != std::wstring_view(argv[3]).size() ||
            parsed_configuration == 0 || parsed_configuration > 255)
            return 24;
    } catch (...) {
        return 24;
    }
    const auto expected_configuration =
        static_cast<std::uint8_t>(parsed_configuration);

    usb_init();
    if (usb_find_busses() < 0 || usb_find_devices() < 0) return 3;
    bool saw_apple{};
    bool saw_topology{};
    struct usb_device* exact_topology_device{};
    struct usb_device* scoped_device{};
    std::size_t exact_topology_matches{};
    std::size_t scoped_matches{};
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device;
             device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId)
                continue;
            saw_apple = true;
            const auto candidate_topology = topology_for(*device);
            const bool topology_matches = !expected_topology_scope.empty() &&
                topology_scope(candidate_topology) == expected_topology_scope;
            const bool exact_topology_match = !expected_topology.empty() &&
                candidate_topology == expected_topology;
            if (topology_matches) saw_topology = true;
            // Selection is deliberately handle-free. Opening every Apple
            // node to compare serials enters AppleUsbFilter on both phones and
            // can overlap another device's PnP transition. The full libusb0
            // topology contains its synthetic slot and is authoritative while
            // present; a broader bus/location scope is allowed only when it
            // contains exactly one Apple node.
            if (exact_topology_match) {
                ++exact_topology_matches;
                exact_topology_device = device;
            }
            if (topology_matches) {
                ++scoped_matches;
                scoped_device = device;
            }
        }
    }

    if (exact_topology_matches > 1 ||
        (exact_topology_matches == 0 && scoped_matches > 1))
        return 29;
    auto* selected_device = exact_topology_matches == 1
        ? exact_topology_device : scoped_matches == 1 ? scoped_device : nullptr;
    if (!selected_device) {
        if (!saw_apple) return 26;
        if (!expected_topology_scope.empty() && !saw_topology) return 27;
        return 29;
    }
    auto* handle = usb_open(selected_device);
    if (!handle) return 30;

    if (selected_device->descriptor.iSerialNumber != 0) {
        char serial[256]{};
        const int serial_length = usb_get_string_simple(handle,
            selected_device->descriptor.iSerialNumber, serial, sizeof(serial));
        if (serial_length > 0 && normalize(std::string_view(serial,
                static_cast<std::size_t>(serial_length))) != expected_serial) {
            usb_close(handle);
            return 28;
        }
    }

    char active_value{};
    if (usb_control_msg(handle, 0x80, 0x08, 0, 0,
            &active_value, 1, 1000) != 1) {
        usb_close(handle);
        return 4;
    }
    const auto active_configuration =
        static_cast<std::uint8_t>(active_value);
    if (operation == L"activate" &&
        active_configuration == expected_configuration) {
        usb_close(handle);
        return 20;
    }
    if (operation == L"restore" &&
        active_configuration != expected_configuration) {
        usb_close(handle);
        return 20;
    }
    if (operation == L"restore" &&
        !quicktime_descriptor_matches(*selected_device, expected_configuration)) {
        usb_close(handle);
        return 5;
    }

    std::unique_ptr<ExactInterfaceTransition> transition;
    try {
        transition = std::make_unique<ExactInterfaceTransition>(
            selected_device->descriptor.idProduct, expected_serial);
    } catch (const std::exception& error) {
        usb_close(handle);
        std::cerr << "notification_failed error=" << error.what() << '\n';
        return 31;
    }

    const int result = usb_control_msg(handle, 0x40, 0x52, 0,
        operation == L"activate" ? 2 : 0, nullptr, 0, 1000);
    std::cout << "request_sent result=" << result
              << " win32_error=" << GetLastError() << '\n';
    // The vendor request invalidates the PnP node asynchronously. Retain the
    // old process and handle ownership until this exact phone has completed a
    // full removal/arrival and its interface remains present. Exiting as soon
    // as 0x52 returns lets process teardown reclaim the stale handle while
    // AppleUsbFilter is still tearing down its WDF objects.
    const bool stable = transition->wait_until_stable(
        std::chrono::steady_clock::now() + ReenumerationTimeout);
    std::cout << "reenumeration removed=" << transition->removed()
              << " arrived=" << transition->arrived()
              << " stable=" << stable << '\n';
    // Never call usb_close after a successful configuration request: the
    // handle names the invalidated node. Normal process teardown is the only
    // operation performed after the exact PnP transaction is quiescent.
    if (stable) return 0;
    // A failed control transfer can still have been accepted by the device.
    // Once 0x52 was submitted, never issue usb_close against the possibly
    // invalidated handle; distinguish a stalled rearrival from no observed
    // PnP transition for diagnostics.
    return transition->removed() ? 32 : result < 0 ? 10 : 33;
}
