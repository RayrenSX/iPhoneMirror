// Execute the actual helper entry point with device-free USB substitutions.
// No libusb DLL is linked and no phone is opened or configured by this test.
#define wmain usb_switch_entry
#include "../tools/UsbConfigurationSwitch.cpp"
#undef wmain

#include <cstring>

namespace {
usb_bus fake_bus{};
struct usb_device selected{};
struct usb_device other{};
int serial_result{};
std::string serial_value;
unsigned configuration{3};
int opens{}, closes{}, controls{}, vendor_requests{}, other_opens{}, failures{};

void reset() {
    fake_bus = {}; selected = {}; other = {};
    strcpy_s(fake_bus.dirname, "bus-test");
    fake_bus.devices = &selected;
    selected.bus = &fake_bus;
    strcpy_s(selected.filename, "selected");
    selected.descriptor.idVendor = 0x05ac;
    selected.descriptor.idProduct = 0x12a8;
    selected.descriptor.iSerialNumber = 1;
    selected.next = &other;
    other.bus = &fake_bus;
    strcpy_s(other.filename, "other");
    other.descriptor.idVendor = 0x05ac;
    other.descriptor.idProduct = 0x12a8;
    serial_value = "0000810100044D600A22001E";
    serial_result = static_cast<int>(serial_value.size());
    configuration = 3;
    opens = closes = controls = vendor_requests = other_opens = 0;
}

int run(const wchar_t* operation = L"restore") {
    wchar_t name[] = L"helper";
    wchar_t identity[] = L"00008101-00044D600A22001E";
    wchar_t expected[] = L"5";
    wchar_t topology[] = L"bus-test:00000000:selected";
    wchar_t* args[]{name, const_cast<wchar_t*>(operation), identity, expected, topology};
    return usb_switch_entry(5, args);
}

void check(bool success, const char* message) {
    if (success) return;
    ++failures;
    std::cerr << "FAIL: " << message << '\n';
}
} // namespace

extern "C" {
void usb_init() {}
int usb_find_busses() { return 0; }
int usb_find_devices() { return 0; }
usb_bus* usb_get_busses() { return &fake_bus; }
usb_dev_handle* usb_open(struct usb_device* device) {
    ++opens;
    if (device != &selected) ++other_opens;
    return reinterpret_cast<usb_dev_handle*>(device);
}
int usb_close(usb_dev_handle*) { ++closes; return 0; }
int usb_get_string_simple(usb_dev_handle*, int, char* target, size_t capacity) {
    if (serial_result > 0 && static_cast<size_t>(serial_result) <= capacity)
        memcpy(target, serial_value.data(), (std::min)(serial_value.size(), capacity));
    return serial_result;
}
int usb_control_msg(usb_dev_handle*, int type, int request,
    int, int, char* data, int length, int) {
    ++controls;
    if (type == 0x80 && request == 8 && length == 1) {
        *data = static_cast<char>(configuration);
        return 1;
    }
    ++vendor_requests;
    return -1;
}
}

int main() {
    for (const auto unreadable : {-1, 0, 257}) {
        reset(); serial_result = unreadable;
        check(run() == 34 && controls == 0 && vendor_requests == 0 &&
                opens == 1 && closes == 1 && other_opens == 0,
            "an unreadable/out-of-range serial cannot authorize any control transfer");
    }
    reset(); selected.descriptor.iSerialNumber = 0;
    check(run() == 34 && controls == 0 && opens == 1 && closes == 1,
        "a missing serial descriptor rejects topology-only ownership");
    reset(); serial_value = "00008150001903580A9B401C";
    check(run() == 28 && controls == 0 && other_opens == 0,
        "a reused selected slot for another serial is rejected without probing other phones");
    reset();
    check(run() == 20 && controls == 1 && vendor_requests == 0 &&
            opens == 1 && closes == 1 && other_opens == 0,
        "the exact already-normal device needs only GET_CONFIGURATION, with no switch");
    reset(); configuration = 5;
    check(run(L"activate") == 20 && controls == 1 && vendor_requests == 0,
        "an exact already-active QuickTime device is not reconfigured");
    reset();
    check(run(L"inspect") == 0 && controls == 1 && vendor_requests == 0 && closes == 1,
        "inspection reads the exact active configuration without a vendor request");
    if (failures != 0) return 1;
    std::cout << "USB helper identity, unreadable serial, reused slot and no-op checks passed\n";
    return 0;
}
