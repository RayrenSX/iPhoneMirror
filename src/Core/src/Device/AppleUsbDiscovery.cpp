#include "Device/AppleUsbDiscovery.h"

#include <Windows.h>
#include <appmodel.h>
#include <SetupAPI.h>

#include <algorithm>
#include <cwctype>
#include <memory>
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
        const std::wstring hardware = property(raw, data, SPDRP_HARDWAREID);
        const std::wstring upper_hardware = uppercase(hardware);
        if (upper_hardware.find(L"USB\\VID_05AC") == std::wstring::npos) continue;

        std::wstring description = property(raw, data, SPDRP_FRIENDLYNAME);
        if (description.empty()) description = property(raw, data, SPDRP_DEVICEDESC);
        results.push_back({std::move(description), hardware});
    }
    return results;
}

} // namespace iPhoneMirror::device
