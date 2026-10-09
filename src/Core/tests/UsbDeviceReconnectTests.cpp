// Run the actual reconnect implementation against a fake Windows device tree.
// No device handles or USB requests are sent to Windows by these tests.
#include <Windows.h>
#include <SetupAPI.h>
#include <cfgmgr32.h>
#include <devpkey.h>
#include <winioctl.h>
#include <usbioctl.h>
#include <string>
#include <cstring>
#include <vector>
#include <iostream>

namespace fake {
constexpr auto serial = L"00008150001903580A9B401C";
const std::wstring parent = std::wstring(L"USB\\VID_05AC&PID_12A8\\") + serial;
std::vector<std::wstring> devices;
bool admin, stale, mismatch, descriptor_fail, cycle_fail;
unsigned configuration, port, cycle_count, open_count, descriptor_count;
void reset() {
    devices = {L"USB\\VID_05AC&PID_12A8\\0000810100044D600A22001E", parent};
    admin = true; stale = mismatch = descriptor_fail = cycle_fail = false;
    configuration = 3; port = 4; cycle_count = open_count = descriptor_count = 0;
}
auto get_class = [](const GUID*, PCWSTR, HWND, DWORD) { return reinterpret_cast<HDEVINFO>(1); };
auto destroy = [](HDEVINFO) { return TRUE; };
auto enumerate = [](HDEVINFO, DWORD index, PSP_DEVINFO_DATA data) {
    if (index >= devices.size()) { SetLastError(ERROR_NO_MORE_ITEMS); return FALSE; }
    data->DevInst = index + 1; return TRUE;
};
auto instance = [](HDEVINFO, PSP_DEVINFO_DATA data, PWSTR value, DWORD capacity, PDWORD) {
    wcscpy_s(value, capacity, devices[data->DevInst - 1].c_str()); return TRUE;
};
auto node_id = [](DEVINST node, PWSTR value, ULONG capacity, ULONG) -> CONFIGRET {
    if (node == 99) wcscpy_s(value, capacity, L"USB\\HUB");
    else wcscpy_s(value, capacity, devices[node - 1].c_str());
    return CR_SUCCESS;
};
auto status = [](PULONG value, PULONG problem, DEVINST, ULONG) -> CONFIGRET {
    *value = stale && descriptor_count ? 0 : DN_STARTED; *problem = 0; return CR_SUCCESS;
};
auto property = [](DEVINST, const DEVPROPKEY*, DEVPROPTYPE* type, PBYTE value, PULONG size, ULONG) -> CONFIGRET {
    *type = DEVPROP_TYPE_UINT32; *size = sizeof(ULONG); std::memcpy(value, &port, sizeof(port)); return CR_SUCCESS;
};
auto parent_node = [](PDEVINST value, DEVINST, ULONG) -> CONFIGRET { *value = 99; return CR_SUCCESS; };
auto list_size = [](PULONG size, const GUID*, PCWSTR, ULONG) -> CONFIGRET { *size = 8; return CR_SUCCESS; };
auto list = [](const GUID*, PCWSTR, PWSTR value, ULONG, ULONG) -> CONFIGRET {
    std::memcpy(value, L"hub\0\0", 5 * sizeof(wchar_t)); return CR_SUCCESS;
};
auto create = [](PCWSTR name, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE) {
    ++open_count;
    if (std::wstring_view(name) != L"hub") return INVALID_HANDLE_VALUE;
    return reinterpret_cast<HANDLE>(1);
};
auto close = [](HANDLE) { return TRUE; };
auto allocate = [](PSID_IDENTIFIER_AUTHORITY, BYTE, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, PSID* sid) {
    *sid = reinterpret_cast<PSID>(1); return TRUE;
};
auto member = [](HANDLE, PSID, PBOOL value) { *value = admin; return TRUE; };
auto free_sid = [](PSID) -> PVOID { return nullptr; };
auto tick = []() -> ULONGLONG { return 1000; };
auto ioctl = [](HANDLE, DWORD code, LPVOID input, DWORD, LPVOID output, DWORD, LPDWORD bytes, LPOVERLAPPED) {
    if (code == IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX) {
        auto* value = static_cast<USB_NODE_CONNECTION_INFORMATION_EX*>(output);
        value->ConnectionStatus = DeviceConnected;
        value->DeviceDescriptor.idVendor = 0x05ac; value->DeviceDescriptor.idProduct = 0x12a8;
        value->DeviceDescriptor.iSerialNumber = 3;
        value->CurrentConfigurationValue = static_cast<UCHAR>(configuration);
        *bytes = sizeof(*value); return TRUE;
    }
    if (code == IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION) {
        ++descriptor_count;
        if (descriptor_fail) return FALSE;
        auto* value = static_cast<USB_DESCRIPTOR_REQUEST*>(output);
        const std::wstring text = mismatch ? L"0000810100044D600A22001E" : serial;
        // Real Apple serial descriptors can include a trailing NUL.
        const auto size = static_cast<UCHAR>(2 + (text.size() + 1) * 2);
        value->Data[0] = size; value->Data[1] = USB_STRING_DESCRIPTOR_TYPE;
        std::memcpy(value->Data + 2, text.c_str(), (text.size() + 1) * 2);
        *bytes = static_cast<DWORD>(offsetof(USB_DESCRIPTOR_REQUEST, Data) + size);
        return TRUE;
    }
    if (code == IOCTL_USB_HUB_CYCLE_PORT) {
        const auto* value = static_cast<USB_CYCLE_PORT_PARAMS*>(input);
        if (value->ConnectionIndex != 4) return FALSE;
        ++cycle_count;
        if (cycle_fail) { SetLastError(ERROR_GEN_FAILURE); return FALSE; }
        *bytes = sizeof(*value); return TRUE;
    }
    return FALSE;
};
}

#define SetupDiGetClassDevsW fake::get_class
#define SetupDiDestroyDeviceInfoList fake::destroy
#define SetupDiEnumDeviceInfo fake::enumerate
#define SetupDiGetDeviceInstanceIdW fake::instance
#define CM_Get_Device_IDW fake::node_id
#define CM_Get_DevNode_Status fake::status
#define CM_Get_DevNode_PropertyW fake::property
#define CM_Get_Parent fake::parent_node
#define CM_Get_Device_Interface_List_SizeW fake::list_size
#define CM_Get_Device_Interface_ListW fake::list
#define CreateFileW fake::create
#define CloseHandle fake::close
#define DeviceIoControl fake::ioctl
#define AllocateAndInitializeSid fake::allocate
#define CheckTokenMembership fake::member
#define FreeSid fake::free_sid
#define GetTickCount64 fake::tick
#include "../tools/UsbDeviceReconnect.cpp"

int main() {
    int failures{};
    const auto check = [&](bool ok, const char* message) {
        if (!ok) { ++failures; std::cerr << "FAIL: " << message << '\n'; }
    };
    const auto run = [] { return reconnect_selected_apple_usb("00008150-001903580A9B401C"); };
    fake::reset();
    check(run() == 0 && fake::cycle_count == 1 && fake::open_count == 1,
        "only the exact selected phone's port cycles once, with NUL-terminated serial");
    fake::reset(); fake::admin = false;
    check(run() == 44 && fake::open_count == 0 && fake::cycle_count == 0,
        "ordinary permissions request elevation before any device handle");
    fake::reset(); fake::mismatch = true;
    check(run() == 42 && fake::cycle_count == 0, "reused port occupied by the other phone is rejected");
    fake::reset(); fake::stale = true;
    check(run() == 42 && fake::cycle_count == 0, "a parent lost during descriptor verification is rejected");
    fake::reset(); fake::descriptor_fail = true;
    check(run() == 42 && fake::cycle_count == 0, "unreadable fresh serial cannot authorize reset");
    fake::reset(); fake::devices.push_back(fake::parent);
    check(run() == 41 && fake::cycle_count == 0, "ambiguous exact parents are rejected");
    fake::reset(); fake::devices.pop_back();
    check(run() == 40 && fake::cycle_count == 0, "the remaining other phone is never selected");
    fake::reset(); fake::port = 0;
    check(run() == 42 && fake::open_count == 0, "zero port cannot become a hub-wide reset");
    for (const auto config : {0U, 1U, 5U}) {
        fake::reset(); fake::configuration = config;
        check(run() == 43 && fake::cycle_count == 0, "unknown or active capture configuration is rejected");
    }
    fake::reset(); fake::cycle_fail = true;
    check(run() == 45 && fake::cycle_count == 1, "a failed cycle remains failed with no repeat");
    fake::reset();
    check(reconnect_selected_apple_usb("00008150-001903580A9B401C", 999) == 46 && fake::open_count == 0,
        "expired consent cannot perform a late cycle");
    fake::reset();
    check(reconnect_selected_apple_usb("*") == 23 && fake::open_count == 0,
        "invalid identity cannot enumerate a reset target");
    return failures == 0 ? 0 : 1;
}
