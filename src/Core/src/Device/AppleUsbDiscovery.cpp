#include "Device/AppleUsbDiscovery.h"

#include <Windows.h>
#include <appmodel.h>
#include <SetupAPI.h>
#include <cfgmgr32.h>

#include <algorithm>
#include <cctype>
#include <cwctype>
#include <cwchar>
#include <memory>
#include <string_view>
#include <type_traits>
#include <vector>

namespace iPhoneMirror::device {
namespace {

struct DevInfoDeleter {
    void operator()(void* handle) const noexcept {
        if (handle != INVALID_HANDLE_VALUE) SetupDiDestroyDeviceInfoList(handle);
    }
};

std::wstring property(HDEVINFO set, SP_DEVINFO_DATA& data, DWORD property_id) {
    DWORD type{};
    DWORD required{};
    SetupDiGetDeviceRegistryPropertyW(set, &data, property_id, &type, nullptr, 0, &required);
    if (required == 0) return {};
    std::vector<BYTE> buffer(required + sizeof(wchar_t), 0);
    if (!SetupDiGetDeviceRegistryPropertyW(set, &data, property_id, &type, buffer.data(),
            static_cast<DWORD>(buffer.size()), nullptr)) return {};
    return std::wstring(reinterpret_cast<const wchar_t*>(buffer.data()));
}

std::wstring uppercase(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return std::towupper(c); });
    return value;
}

std::wstring instance_id(HDEVINFO set, SP_DEVINFO_DATA& data) {
    std::vector<wchar_t> buffer(MAX_DEVICE_ID_LEN);
    if (!SetupDiGetDeviceInstanceIdW(set, &data, buffer.data(),
            static_cast<DWORD>(buffer.size()), nullptr))
        return {};
    return buffer.data();
}

} // namespace


ServiceState apple_mobile_device_service_state() noexcept {
    ServiceState result;
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!manager) return result;
    SC_HANDLE service = OpenServiceW(manager, L"Apple Mobile Device Service", SERVICE_QUERY_STATUS);
    if (!service) {
        // Some packages expose the internal service name instead of the display name.
        service = OpenServiceW(manager, L"AppleMobileDeviceService", SERVICE_QUERY_STATUS);
    }
    if (service) {
        result.installed = true;
        SERVICE_STATUS_PROCESS status{};
        DWORD bytes{};
        if (QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
                reinterpret_cast<BYTE*>(&status), sizeof(status), &bytes)) {
            result.running = status.dwCurrentState == SERVICE_RUNNING;
        }
        CloseServiceHandle(service);
    }
    CloseServiceHandle(manager);

    if (!result.installed) {
        UINT32 package_count{};
        UINT32 buffer_length{};
        const LONG package_result = GetPackagesByPackageFamily(
            L"AppleInc.AppleDevices_nzyj5cx40ttqa", &package_count, nullptr, &buffer_length, nullptr);
        result.installed = package_count != 0 || package_result == ERROR_INSUFFICIENT_BUFFER;
    }
    return result;
}

std::vector<PhysicalAppleDevice> discover_physical_apple_usb_devices() {
    HDEVINFO raw = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES | DIGCF_PRESENT);
    if (raw == INVALID_HANDLE_VALUE) return {};
    std::unique_ptr<void, DevInfoDeleter> set(raw);

    std::vector<PhysicalAppleDevice> results;
    for (DWORD index = 0;; ++index) {
        SP_DEVINFO_DATA data{};
        data.cbSize = sizeof(data);
        if (!SetupDiEnumDeviceInfo(raw, index, &data)) {
            if (GetLastError() == ERROR_NO_MORE_ITEMS) break;
            continue;
        }
        const auto id = instance_id(raw, data);
        if (!is_apple_mobile_capture_parent_instance_id(id)) continue;
        const std::wstring hardware = property(raw, data, SPDRP_HARDWAREID);
        const std::wstring upper_hardware = uppercase(hardware);
        if (upper_hardware.find(L"USB\\VID_05AC") == std::wstring::npos) continue;

        std::wstring description = property(raw, data, SPDRP_FRIENDLYNAME);
        if (description.empty()) description = property(raw, data, SPDRP_DEVICEDESC);
        results.push_back({std::move(description), hardware});
    }
    return results;
}

bool is_apple_usb_parent_instance_id(
    std::wstring_view instance_id) noexcept {
    try {
        const auto upper = uppercase(std::wstring(instance_id));
        constexpr std::wstring_view prefix = L"USB\\VID_05AC&PID_";
        if (!std::wstring_view(upper).starts_with(prefix) ||
            upper.find(L"&MI_") != std::wstring::npos)
            return false;
        const auto separator = upper.find(L'\\', prefix.size());
        if (separator != prefix.size() + 4 || separator + 1 >= upper.size() ||
            upper.find(L'\\', separator + 1) != std::wstring::npos)
            return false;
        return std::all_of(upper.begin() + static_cast<std::ptrdiff_t>(prefix.size()),
            upper.begin() + static_cast<std::ptrdiff_t>(separator),
            [](wchar_t character) { return std::iswxdigit(character) != 0; });
    } catch (...) {
        return false;
    }
}

bool is_apple_mobile_capture_parent_instance_id(
    std::wstring_view instance_id) noexcept {
    try {
        const auto upper = uppercase(std::wstring(instance_id));
        if (!is_apple_usb_parent_instance_id(upper)) return false;
        constexpr std::wstring_view marker = L"&PID_";
        const auto marker_position = upper.find(marker);
        if (marker_position == std::wstring_view::npos ||
            marker_position + marker.size() + 4 > upper.size())
            return false;
        const std::wstring_view product_text(
            upper.data() + marker_position + marker.size(), 4);
        return std::all_of(product_text.begin(), product_text.end(),
                   [](wchar_t character) { return std::iswxdigit(character) != 0; }) &&
            is_apple_mobile_capture_product_id(static_cast<std::uint32_t>(
                std::wcstoul(std::wstring(product_text).c_str(), nullptr, 16)));
    } catch (...) {
        return false;
    }
}

std::string normalized_serial(std::wstring_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto ch : value) {
        if (std::iswalnum(ch))
            result.push_back(static_cast<char>(std::towlower(ch)));
    }
    return result;
}

std::string normalized_serial(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto ch : value) {
        if (std::isalnum(static_cast<unsigned char>(ch)))
            result.push_back(static_cast<char>(std::tolower(
                static_cast<unsigned char>(ch))));
    }
    return result;
}

bool apple_usb_parent_instance_matches_serial(
    std::wstring_view instance_id, std::string_view serial) noexcept {
    try {
        if (!is_apple_usb_parent_instance_id(instance_id))
            return false;
        const auto separator = instance_id.rfind(L'\\');
        if (separator == std::wstring_view::npos ||
            separator + 1 >= instance_id.size())
            return false;
        const auto wanted = normalized_serial(serial);
        return !wanted.empty() && normalized_serial(
            instance_id.substr(separator + 1)) == wanted;
    } catch (...) {
        return false;
    }
}

bool libusb0_apple_interface_path_matches(
    std::wstring_view symbolic_link, std::uint16_t product_id,
    std::string_view serial) noexcept {
    try {
        if (product_id == 0) return false;
        const auto wanted_serial = normalized_serial(serial);
        if (wanted_serial.empty()) return false;

        const auto upper_link = uppercase(std::wstring(symbolic_link));
        wchar_t prefix[64]{};
        swprintf_s(prefix, L"\\\\?\\USB#VID_05AC&PID_%04X#", product_id);
        const std::wstring_view expected_prefix(prefix);
        if (!upper_link.starts_with(expected_prefix)) return false;

        const auto serial_begin = expected_prefix.size();
        const auto serial_end = upper_link.find(L'#', serial_begin);
        if (serial_end == std::wstring::npos ||
            normalized_serial(std::wstring_view(upper_link).substr(
                serial_begin, serial_end - serial_begin)) != wanted_serial)
            return false;

        // This is the libusb-win32 filter interface class installed by the
        // current driver stack. Requiring it prevents an Apple management,
        // imaging or generic USB notification from authorizing a capture open.
        constexpr std::wstring_view LibUsb0InterfaceSuffix =
            L"#{F9F3FF14-AE21-48A0-8A25-8011A7A931D9}";
        return std::wstring_view(upper_link).substr(serial_end) ==
            LibUsb0InterfaceSuffix;
    } catch (...) {
        return false;
    }
}

bool is_apple_usb_parent_present(std::string_view serial) noexcept {
    try {
        if (normalized_serial(serial).empty()) return false;
        HDEVINFO raw = SetupDiGetClassDevsW(nullptr, nullptr, nullptr,
            DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (raw == INVALID_HANDLE_VALUE) return false;
        std::unique_ptr<void, DevInfoDeleter> set(raw);

        for (DWORD index{};; ++index) {
            SP_DEVINFO_DATA data{};
            data.cbSize = sizeof(data);
            if (!SetupDiEnumDeviceInfo(raw, index, &data)) {
                if (GetLastError() == ERROR_NO_MORE_ITEMS) break;
                continue;
            }
            wchar_t instance_id[MAX_DEVICE_ID_LEN]{};
            if (!SetupDiGetDeviceInstanceIdW(raw, &data, instance_id,
                    static_cast<DWORD>(std::size(instance_id)), nullptr) ||
                !apple_usb_parent_instance_matches_serial(instance_id, serial))
                continue;

            ULONG status{};
            ULONG problem{};
            if (CM_Get_DevNode_Status(&status, &problem, data.DevInst, 0) !=
                    CR_SUCCESS)
                return false;
            if ((status & DN_STARTED) != 0 && problem == 0)
                return true;
        }
    } catch (...) {
    }
    return false;
}

AppleNormalUsbStackEvidence inspect_apple_normal_usb_stack(
    std::string_view serial) noexcept {
    AppleNormalUsbStackEvidence evidence;
    try {
        if (normalized_serial(serial).empty()) return evidence;
        HDEVINFO raw = SetupDiGetClassDevsW(nullptr, nullptr, nullptr,
            DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (raw == INVALID_HANDLE_VALUE) return evidence;
        std::unique_ptr<void, DevInfoDeleter> set(raw);

        DEVINST parent_node{};
        for (DWORD index{};; ++index) {
            SP_DEVINFO_DATA data{};
            data.cbSize = sizeof(data);
            if (!SetupDiEnumDeviceInfo(raw, index, &data)) {
                if (GetLastError() == ERROR_NO_MORE_ITEMS) break;
                continue;
            }
            wchar_t instance_id[MAX_DEVICE_ID_LEN]{};
            if (!SetupDiGetDeviceInstanceIdW(raw, &data, instance_id,
                    static_cast<DWORD>(std::size(instance_id)), nullptr) ||
                !apple_usb_parent_instance_matches_serial(instance_id, serial))
                continue;
            ULONG status{};
            ULONG problem{};
            if (CM_Get_DevNode_Status(&status, &problem, data.DevInst, 0) !=
                    CR_SUCCESS || (status & DN_STARTED) == 0 || problem != 0)
                return evidence;
            parent_node = data.DevInst;
            evidence.parent_started = true;
            break;
        }
        if (!evidence.parent_started) return evidence;

        // QuickTime mode removes the normal composite management children.
        // Require both normal interfaces belonging to this exact parent:
        // MI_00 owns the WPD/device surface and MI_01 owns Apple Mobile Device
        // / WinUSB management. Seeing only MI_01 is a partial recovery that
        // still makes the phone disappear from user-visible discovery and can
        // leave the next QuickTime activation in a failed-start PnP tree.
        // Some Apple driver versions insert an intermediate USB PDO between
        // the composite parent and MI_ children, so direct-parent comparison
        // is insufficient after the configuration switch.
        for (DWORD index{};; ++index) {
            SP_DEVINFO_DATA data{};
            data.cbSize = sizeof(data);
            if (!SetupDiEnumDeviceInfo(raw, index, &data)) {
                if (GetLastError() == ERROR_NO_MORE_ITEMS) break;
                continue;
            }
            DEVINST candidate = data.DevInst;
            bool belongs_to_parent{};
            for (unsigned depth{}; depth < 16; ++depth) {
                DEVINST candidate_parent{};
                if (CM_Get_Parent(&candidate_parent, candidate, 0) != CR_SUCCESS)
                    break;
                if (candidate_parent == parent_node) {
                    belongs_to_parent = true;
                    break;
                }
                candidate = candidate_parent;
            }
            if (!belongs_to_parent) continue;
            const auto hardware = uppercase(property(raw, data, SPDRP_HARDWAREID));
            if (hardware.find(L"USB\\VID_05AC&PID_") == std::wstring::npos)
                continue;
            const bool media_interface =
                hardware.find(L"&MI_00") != std::wstring::npos;
            const bool management_interface =
                hardware.find(L"&MI_01") != std::wstring::npos;
            if (!media_interface && !management_interface) continue;
            ULONG status{};
            ULONG problem{};
            const bool started =
                CM_Get_DevNode_Status(&status, &problem, data.DevInst, 0) ==
                    CR_SUCCESS && (status & DN_STARTED) != 0 && problem == 0;
            if (media_interface)
                evidence.media_interface_started = started;
            if (management_interface)
                evidence.management_interface_started = started;
        }
    } catch (...) {
    }
    return evidence;
}

bool is_apple_normal_usb_stack_present(std::string_view serial) noexcept {
    return is_complete_apple_normal_usb_stack(
        inspect_apple_normal_usb_stack(serial));
}

bool is_unsafe_apple_usb_filter_combination(
    std::span<const std::wstring> upper_filters,
    std::span<const std::wstring> lower_filters) noexcept {
    const auto contains = [](std::span<const std::wstring> values,
        const wchar_t* wanted_filter) {
        return std::ranges::any_of(values, [&](const auto& value) {
            return _wcsicmp(value.c_str(), wanted_filter) == 0;
        });
    };
    return contains(upper_filters, L"libusb0") &&
        (contains(lower_filters, L"AppleLowerFilter") ||
         contains(lower_filters, L"AppleKmdfFilter"));
}

AppleUsbFilterSafetyResult inspect_apple_usb_filter_stack(
    std::string_view serial) noexcept {
    try {
        std::string wanted;
        wanted.reserve(serial.size());
        for (const auto ch : serial) {
            if (std::isalnum(static_cast<unsigned char>(ch)))
                wanted.push_back(static_cast<char>(std::tolower(
                    static_cast<unsigned char>(ch))));
        }
        if (wanted.empty()) {
            return {AppleUsbFilterSafety::Indeterminate,
                "selected Apple USB serial is empty after normalization"};
        }
        HKEY usb_key{};
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                L"SYSTEM\\CurrentControlSet\\Enum\\USB", 0,
                KEY_READ, &usb_key) != ERROR_SUCCESS)
            return {AppleUsbFilterSafety::Indeterminate,
                "cannot open the Windows USB device registry"};
        auto close_usb = std::unique_ptr<std::remove_pointer_t<HKEY>,
            decltype(&RegCloseKey)>(usb_key, &RegCloseKey);

        bool matching_instance_found{};
        bool started_instance_found{};
        std::string uncertainty;

        for (DWORD hardware_index{};; ++hardware_index) {
            wchar_t hardware_name[256]{};
            DWORD hardware_length = static_cast<DWORD>(std::size(hardware_name));
            const auto hardware_result = RegEnumKeyExW(usb_key, hardware_index,
                hardware_name, &hardware_length, nullptr, nullptr, nullptr, nullptr);
            if (hardware_result == ERROR_NO_MORE_ITEMS) break;
            if (hardware_result != ERROR_SUCCESS) {
                return {AppleUsbFilterSafety::Indeterminate,
                    "cannot enumerate the Windows USB device registry"};
            }
            std::wstring hardware(hardware_name, hardware_length);
            if (uppercase(hardware).find(L"VID_05AC&PID_") != 0 ||
                uppercase(hardware).find(L"&MI_") != std::wstring::npos)
                continue;

            HKEY device_key{};
            if (RegOpenKeyExW(usb_key, hardware_name, 0, KEY_READ,
                    &device_key) != ERROR_SUCCESS) {
                return {AppleUsbFilterSafety::Indeterminate,
                    "cannot open an Apple USB parent registry key"};
            }
            auto close_device = std::unique_ptr<std::remove_pointer_t<HKEY>,
                decltype(&RegCloseKey)>(device_key, &RegCloseKey);
            DWORD device_count{};
            if (RegQueryInfoKeyW(device_key, nullptr, nullptr, nullptr,
                    &device_count, nullptr, nullptr, nullptr, nullptr,
                    nullptr, nullptr, nullptr) != ERROR_SUCCESS) {
                return {AppleUsbFilterSafety::Indeterminate,
                    "cannot enumerate Apple USB parent instances"};
            }
            for (DWORD device_index{}; device_index < device_count; ++device_index) {
                wchar_t instance_name[256]{};
                DWORD instance_length = static_cast<DWORD>(std::size(instance_name));
                if (RegEnumKeyExW(device_key, device_index, instance_name,
                        &instance_length, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) {
                    return {AppleUsbFilterSafety::Indeterminate,
                        "cannot enumerate an Apple USB parent instance"};
                }
                if (normalized_serial(std::wstring_view(instance_name,
                        instance_length)) != wanted)
                    continue;
                matching_instance_found = true;
                HKEY instance_key{};
                if (RegOpenKeyExW(device_key, instance_name, 0, KEY_READ,
                        &instance_key) != ERROR_SUCCESS) {
                    uncertainty = "cannot open the selected Apple USB parent registry key";
                    continue;
                }
                auto close_instance = std::unique_ptr<std::remove_pointer_t<HKEY>,
                    decltype(&RegCloseKey)>(instance_key, &RegCloseKey);
                DEVINST node{};
                const std::wstring instance_id = std::wstring(L"USB\\") +
                    hardware + L"\\" + std::wstring(instance_name, instance_length);
                if (CM_Locate_DevNodeW(&node, const_cast<wchar_t*>(instance_id.c_str()),
                        CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) {
                    uncertainty = "cannot locate the selected Apple USB parent node";
                    continue;
                }
                ULONG node_status{};
                ULONG problem{};
                if (CM_Get_DevNode_Status(&node_status, &problem, node, 0) != CR_SUCCESS) {
                    uncertainty = "cannot read the selected Apple USB parent status";
                    continue;
                }
                if ((node_status & DN_STARTED) == 0) {
                    uncertainty = "the selected Apple USB parent is not started";
                    continue;
                }
                started_instance_found = true;
                auto read_filters = [&](const wchar_t* value_name,
                    std::vector<std::wstring>& result) {
                    DWORD type{};
                    DWORD bytes{};
                    const auto size_result = RegQueryValueExW(instance_key,
                        value_name, nullptr, &type, nullptr, &bytes);
                    if (size_result == ERROR_FILE_NOT_FOUND) return true;
                    if (size_result != ERROR_SUCCESS || type != REG_MULTI_SZ ||
                        bytes < sizeof(wchar_t)) return false;
                    const auto value_count = bytes / sizeof(wchar_t);
                    std::vector<wchar_t> values(value_count + 2, L'\0');
                    if (RegQueryValueExW(instance_key, value_name, nullptr,
                            &type, reinterpret_cast<BYTE*>(values.data()),
                            &bytes) != ERROR_SUCCESS)
                        return false;
                    std::size_t cursor{};
                    while (cursor < value_count && values[cursor] != L'\0') {
                        const auto end = std::find(values.begin() +
                            static_cast<std::ptrdiff_t>(cursor),
                            values.begin() + static_cast<std::ptrdiff_t>(value_count),
                            L'\0');
                        if (end == values.begin() +
                                static_cast<std::ptrdiff_t>(value_count))
                            return false;
                        result.emplace_back(values.begin() +
                            static_cast<std::ptrdiff_t>(cursor), end);
                        cursor = static_cast<std::size_t>(
                            std::distance(values.begin(), end)) + 1;
                    }
                    return true;
                };
                std::vector<std::wstring> upper_filters;
                std::vector<std::wstring> lower_filters;
                if (!read_filters(L"UpperFilters", upper_filters) ||
                    !read_filters(L"LowerFilters", lower_filters)) {
                    uncertainty = "cannot read the selected Apple USB filter chain";
                    continue;
                }
                if (is_unsafe_apple_usb_filter_combination(
                        upper_filters, lower_filters)) {
                    return {AppleUsbFilterSafety::Unsafe,
                        "legacy libusb0 is stacked with AppleLowerFilter/AppleKmdfFilter"};
                }
            }
        }
        if (!matching_instance_found) {
            return {AppleUsbFilterSafety::Indeterminate,
                "the selected Apple USB parent is not present in the registry"};
        }
        if (!started_instance_found || !uncertainty.empty()) {
            return {AppleUsbFilterSafety::Indeterminate,
                uncertainty.empty()
                    ? "the selected Apple USB parent is not started"
                    : std::move(uncertainty)};
        }
        return {AppleUsbFilterSafety::Safe,
            "the selected Apple USB filter chain passed the read-only preflight"};
    } catch (...) {
        return {AppleUsbFilterSafety::Indeterminate,
            "unexpected failure while inspecting the Apple USB filter chain"};
    }
}

} // namespace iPhoneMirror::device
