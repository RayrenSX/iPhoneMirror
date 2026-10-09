#include "UsbDeviceReconnect.h"
#include "Device/AppleUsbDiscovery.h"

#include <Windows.h>
#include <SetupAPI.h>
#include <cfgmgr32.h>
#include <initguid.h>
#include <devpkey.h>
#include <winioctl.h>
#include <usbioctl.h>

#include <algorithm>
#include <cctype>
#include <cwctype>
#include <cwchar>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

namespace {
std::wstring canonical_serial(std::string_view serial) {
    std::wstring result;
    for (const unsigned char ch : serial) {
        if (ch == '-') continue;
        if (!std::isxdigit(ch)) return {};
        result.push_back(static_cast<wchar_t>(std::toupper(ch)));
    }
    return result.size() >= 16 && result.size() <= 40 ? result : std::wstring{};
}

bool matches_parent(std::wstring_view id, std::wstring_view serial,
    unsigned& product) {
    std::wstring upper(id);
    std::ranges::transform(upper, upper.begin(), std::towupper);
    constexpr std::wstring_view prefix = L"USB\\VID_05AC&PID_";
    if (!upper.starts_with(prefix) || upper.size() != prefix.size() + 5 + serial.size() ||
        upper[prefix.size() + 4] != L'\\' ||
        std::wstring_view(upper).substr(prefix.size() + 5) != serial)
        return false;
    if (swscanf_s(upper.c_str() + prefix.size(), L"%4x", &product) != 1)
        return false;
    return iPhoneMirror::device::is_apple_mobile_capture_product_id(product);
}

struct InfoCloser { void operator()(void* p) const { SetupDiDestroyDeviceInfoList(p); } };
struct HandleCloser { HANDLE value; ~HandleCloser() { CloseHandle(value); } };
}

int reconnect_selected_apple_usb(std::string_view serial, std::uint64_t deadline) {
    const auto reject = [](const char* stage) {
        std::cerr << "usb_reconnect rejected=true stage=" << stage
                  << " win32_error=" << GetLastError() << '\n';
        return 42;
    };
    const auto wanted = canonical_serial(serial);
    if (wanted.empty()) return 23;
    // Windows can report ERROR_GEN_FAILURE for a non-admin cycle request.
    // Detect the privilege explicitly rather than retrying that ambiguous I/O.
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
    PSID administrators{};
    BOOL elevated{};
    if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID,
            DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &administrators)) return 44;
    const bool checked = CheckTokenMembership(nullptr, administrators, &elevated) != FALSE;
    FreeSid(administrators);
    if (!checked || !elevated) return 44;
    const auto local_deadline = GetTickCount64() + 8000;
    deadline = deadline == 0 ? local_deadline : (std::min)(deadline, local_deadline);
    if (GetTickCount64() >= deadline) return 46;
    const auto raw = SetupDiGetClassDevsW(nullptr, nullptr, nullptr,
        DIGCF_ALLCLASSES | DIGCF_PRESENT);
    if (raw == INVALID_HANDLE_VALUE) return 40;
    std::unique_ptr<void, InfoCloser> info(raw);
    DEVINST selected{};
    unsigned product{};
    std::wstring selected_id;
    for (DWORD index{};; ++index) {
        SP_DEVINFO_DATA data{.cbSize = sizeof(data)};
        if (!SetupDiEnumDeviceInfo(raw, index, &data)) {
            if (GetLastError() == ERROR_NO_MORE_ITEMS) break;
            return 40;
        }
        wchar_t id[MAX_DEVICE_ID_LEN]{};
        if (!SetupDiGetDeviceInstanceIdW(raw, &data, id, MAX_DEVICE_ID_LEN, nullptr))
            return 40;
        unsigned candidate_product{};
        if (!matches_parent(id, wanted, candidate_product)) continue;
        if (selected != 0) return 41;
        selected = data.DevInst;
        selected_id = id;
        product = candidate_product;
    }
    if (!selected) return 40;
    const auto still_selected = [&] {
        wchar_t id[MAX_DEVICE_ID_LEN]{};
        ULONG status{}, problem{};
        return CM_Get_Device_IDW(selected, id, MAX_DEVICE_ID_LEN, 0) == CR_SUCCESS &&
            selected_id == id &&
            CM_Get_DevNode_Status(&status, &problem, selected, 0) == CR_SUCCESS &&
            (status & DN_STARTED) != 0 && problem == 0;
    };
    if (!still_selected()) return reject("parent_changed");
    ULONG port{}, size = sizeof(port);
    DEVPROPTYPE type{};
    if (CM_Get_DevNode_PropertyW(selected, &DEVPKEY_Device_Address, &type,
            reinterpret_cast<PBYTE>(&port), &size, 0) != CR_SUCCESS ||
        type != DEVPROP_TYPE_UINT32 || size != sizeof(port) || port == 0)
        return reject("port_address");
    DEVINST hub_node{};
    wchar_t hub_id[MAX_DEVICE_ID_LEN]{};
    if (CM_Get_Parent(&hub_node, selected, 0) != CR_SUCCESS ||
        CM_Get_Device_IDW(hub_node, hub_id, MAX_DEVICE_ID_LEN, 0) != CR_SUCCESS)
        return reject("parent_hub");
    GUID hub_guid{0xf18a0e88, 0xc30c, 0x11d0,
        {0x88, 0x15, 0x00, 0xa0, 0xc9, 0x06, 0xbe, 0xd8}};
    ULONG characters{};
    if (CM_Get_Device_Interface_List_SizeW(&characters, &hub_guid, hub_id,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS ||
        characters < 2 || characters > 32768) return reject("hub_list_size");
    std::wstring links(characters, L'\0');
    if (CM_Get_Device_Interface_ListW(&hub_guid, hub_id, links.data(), characters,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS) return reject("hub_list");
    const auto end = links.find(L'\0');
    if (end == 0 || end == std::wstring::npos || end + 1 >= links.size() ||
        links[end + 1] != L'\0') return reject("hub_ambiguous");
    HANDLE hub = CreateFileW(links.c_str(), GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (hub == INVALID_HANDLE_VALUE) {
        const auto error = GetLastError();
        std::cerr << "usb_reconnect stage=open_hub win32_error=" << error << '\n';
        return error == ERROR_ACCESS_DENIED ? 44 : 45;
    }
    HandleCloser close{hub};
    std::vector<unsigned char> buffer(sizeof(USB_NODE_CONNECTION_INFORMATION_EX) +
        32 * sizeof(USB_PIPE_INFO));
    auto* connection = reinterpret_cast<USB_NODE_CONNECTION_INFORMATION_EX*>(buffer.data());
    connection->ConnectionIndex = port;
    DWORD bytes{};
    if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
            buffer.data(), static_cast<DWORD>(buffer.size()), buffer.data(),
            static_cast<DWORD>(buffer.size()), &bytes, nullptr) ||
        bytes < sizeof(USB_NODE_CONNECTION_INFORMATION_EX) ||
        connection->ConnectionStatus != DeviceConnected ||
        connection->DeviceDescriptor.idVendor != 0x05ac ||
        connection->DeviceDescriptor.idProduct != product ||
        connection->DeviceIsHub || connection->DeviceDescriptor.iSerialNumber == 0)
        return reject("connection_identity");
    // A restart is permitted only after the capture configuration was restored.
    // Do not interrupt a still-active or newly started QuickTime session.
    if (connection->CurrentConfigurationValue != 3) return 43;
    // Verify the actual occupant's serial through the hub, without opening the
    // phone's legacy filter. A reused port must never reset another phone.
    std::vector<unsigned char> descriptor(offsetof(USB_DESCRIPTOR_REQUEST, Data) + 255);
    auto* request = reinterpret_cast<USB_DESCRIPTOR_REQUEST*>(descriptor.data());
    request->ConnectionIndex = port;
    request->SetupPacket.wValue = static_cast<USHORT>((USB_STRING_DESCRIPTOR_TYPE << 8) |
        connection->DeviceDescriptor.iSerialNumber);
    request->SetupPacket.wIndex = 0x0409;
    request->SetupPacket.wLength = 255;
    if (!DeviceIoControl(hub, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION,
            descriptor.data(), static_cast<DWORD>(descriptor.size()), descriptor.data(),
            static_cast<DWORD>(descriptor.size()), &bytes, nullptr) ||
        bytes < offsetof(USB_DESCRIPTOR_REQUEST, Data) + 2) return reject("serial_descriptor_read");
    const auto* value = request->Data;
    if (value[1] != USB_STRING_DESCRIPTOR_TYPE || value[0] < 2 || value[0] % 2 != 0 ||
        bytes < offsetof(USB_DESCRIPTOR_REQUEST, Data) + value[0]) return reject("serial_descriptor_format");
    std::wstring actual;
    for (unsigned i = 2; i < value[0]; i += 2) {
        const auto ch = static_cast<wchar_t>(value[i] | (value[i + 1] << 8));
        if (ch == L'-') continue;
        actual.push_back(static_cast<wchar_t>(std::towupper(ch)));
    }
    while (!actual.empty() && actual.back() == L'\0') actual.pop_back();
    if (actual != wanted) {
        std::cerr << "usb_reconnect serial_length=" << actual.size()
                  << " expected_length=" << wanted.size() << '\n';
        return reject("serial_mismatch");
    }
    if (!still_selected()) return reject("parent_changed_before_cycle");
    ULONG current_port{}, current_size = sizeof(current_port);
    DEVPROPTYPE current_type{};
    DEVINST current_hub{};
    if (CM_Get_DevNode_PropertyW(selected, &DEVPKEY_Device_Address, &current_type,
            reinterpret_cast<PBYTE>(&current_port), &current_size, 0) != CR_SUCCESS ||
        current_type != DEVPROP_TYPE_UINT32 || current_size != sizeof(current_port) ||
        current_port != port || CM_Get_Parent(&current_hub, selected, 0) != CR_SUCCESS ||
        current_hub != hub_node) return reject("port_or_hub_changed_before_cycle");
    if (GetTickCount64() >= deadline) return 46;
    // One atomic port cycle avoids leaving the phone disabled if the process
    // exits between a separate software-unplug and software-plug operation.
    USB_CYCLE_PORT_PARAMS cycle{.ConnectionIndex = port};
    const bool ok = DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, &cycle, sizeof(cycle),
        &cycle, sizeof(cycle), &bytes, nullptr) != FALSE;
    const auto error = ok ? ERROR_SUCCESS : GetLastError();
    std::cout << "usb_reconnect stage=cycle accepted=" << ok << " win32_error=" << error
              << " usb_status=" << cycle.StatusReturned << '\n';
    if (!ok) return error == ERROR_ACCESS_DENIED ? 44 : 45;
    return cycle.StatusReturned == 0 ? 0 : 45;
}
