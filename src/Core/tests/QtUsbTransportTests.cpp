#include "Transport/QtUsbTransport.h"
#include "Protocol/QuickTimePacket.h"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <deque>
#include <stdexcept>

// Supply the entire libusb API used by QtUsbTransport.cpp in this executable.
// It does not link a USB runtime or enumerate/open any real device.
struct libusb_context {};
struct libusb_device {};
struct libusb_device_handle {};

namespace {
libusb_context context;
libusb_device device;
libusb_device_handle handle;
libusb_device* devices[]{&device, nullptr};
struct Transfer { int result; std::vector<std::uint8_t> bytes; };
std::deque<Transfer> transfers;
int bulk_calls{};
int open_handles{};
int claims{};
int unexpected_calls{};

void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

libusb_config_descriptor* descriptor() {
    static std::array<libusb_endpoint_descriptor, 2> endpoints{};
    static libusb_interface_descriptor alternate{};
    static libusb_interface interface_group{};
    static libusb_config_descriptor configuration{};
    endpoints[0].bEndpointAddress = 0x81;
    endpoints[1].bEndpointAddress = 0x02;
    for (auto& endpoint : endpoints) {
        endpoint.bmAttributes = LIBUSB_TRANSFER_TYPE_BULK;
        endpoint.wMaxPacketSize = 512;
    }
    alternate.bInterfaceNumber = 3;
    alternate.bInterfaceClass = 0xff;
    alternate.bInterfaceSubClass = 0x2a;
    alternate.bNumEndpoints = static_cast<std::uint8_t>(endpoints.size());
    alternate.endpoint = endpoints.data();
    interface_group.altsetting = &alternate;
    interface_group.num_altsetting = 1;
    configuration.bConfigurationValue = 5;
    configuration.bNumInterfaces = 1;
    configuration.interface = &interface_group;
    return &configuration;
}
} // namespace

extern "C" {
int LIBUSB_CALL libusb_init(libusb_context** result) { *result = &context; return 0; }
void LIBUSB_CALL libusb_exit(libusb_context*) {}
int LIBUSB_CALLV libusb_set_option(libusb_context*, libusb_option, ...) { return 0; }
ssize_t LIBUSB_CALL libusb_get_device_list(libusb_context*, libusb_device*** result) {
    *result = devices; return 1;
}
void LIBUSB_CALL libusb_free_device_list(libusb_device**, int) {}
libusb_device* LIBUSB_CALL libusb_ref_device(libusb_device* value) { return value; }
void LIBUSB_CALL libusb_unref_device(libusb_device*) {}
std::uint8_t LIBUSB_CALL libusb_get_bus_number(libusb_device*) { return 1; }
std::uint8_t LIBUSB_CALL libusb_get_device_address(libusb_device*) { return 1; }
int LIBUSB_CALL libusb_get_port_numbers(libusb_device*, std::uint8_t* ports, int count) {
    if (count < 1) return LIBUSB_ERROR_OVERFLOW;
    ports[0] = 1; return 1;
}
int LIBUSB_CALL libusb_get_device_descriptor(libusb_device*, libusb_device_descriptor* result) {
    *result = {};
    result->idVendor = 0x05ac;
    result->idProduct = 0x1234;
    result->iSerialNumber = 1;
    result->bNumConfigurations = 1;
    return 0;
}
int LIBUSB_CALL libusb_get_config_descriptor(libusb_device*, std::uint8_t,
    libusb_config_descriptor** result) { *result = descriptor(); return 0; }
void LIBUSB_CALL libusb_free_config_descriptor(libusb_config_descriptor*) {}
int LIBUSB_CALL libusb_open(libusb_device*, libusb_device_handle** result) {
    *result = &handle; ++open_handles; return 0;
}
void LIBUSB_CALL libusb_close(libusb_device_handle*) { --open_handles; }
int LIBUSB_CALL libusb_get_configuration(libusb_device_handle*, int* value) { *value = 5; return 0; }
int LIBUSB_CALL libusb_get_string_descriptor_ascii(libusb_device_handle*, std::uint8_t,
    unsigned char* destination, int size) {
    constexpr char serial[] = "fixture";
    if (size < sizeof(serial)) return LIBUSB_ERROR_OVERFLOW;
    std::memcpy(destination, serial, sizeof(serial));
    return sizeof(serial) - 1;
}
int LIBUSB_CALL libusb_claim_interface(libusb_device_handle*, int number) {
    if (number != 3) return LIBUSB_ERROR_NOT_FOUND;
    ++claims; return 0;
}
int LIBUSB_CALL libusb_release_interface(libusb_device_handle*, int) { --claims; return 0; }
int LIBUSB_CALL libusb_set_configuration(libusb_device_handle*, int) {
    ++unexpected_calls; return LIBUSB_ERROR_NOT_SUPPORTED;
}
int LIBUSB_CALL libusb_set_interface_alt_setting(libusb_device_handle*, int, int) {
    ++unexpected_calls; return LIBUSB_ERROR_NOT_SUPPORTED;
}
int LIBUSB_CALL libusb_clear_halt(libusb_device_handle*, unsigned char) {
    ++unexpected_calls; return LIBUSB_ERROR_NOT_SUPPORTED;
}
int LIBUSB_CALL libusb_control_transfer(libusb_device_handle*, std::uint8_t, std::uint8_t,
    std::uint16_t, std::uint16_t, unsigned char*, std::uint16_t, unsigned int) {
    ++unexpected_calls; return LIBUSB_ERROR_NOT_SUPPORTED;
}
const char* LIBUSB_CALL libusb_error_name(int) { return "test USB result"; }
const libusb_version* LIBUSB_CALL libusb_get_version() { return nullptr; }
int LIBUSB_CALL libusb_bulk_transfer(libusb_device_handle* value, unsigned char endpoint,
    unsigned char* destination, int length, int* transferred, unsigned int timeout) {
    ++bulk_calls;
    if (value != &handle || endpoint != 0x81 || timeout != 25 || transfers.empty()) {
        ++unexpected_calls;
        *transferred = 0;
        return LIBUSB_ERROR_OTHER;
    }
    const auto transfer = std::move(transfers.front());
    transfers.pop_front();
    if (length < 0 || transfer.bytes.size() > static_cast<std::size_t>(length)) {
        ++unexpected_calls;
        *transferred = 0;
        return LIBUSB_ERROR_OVERFLOW;
    }
    std::ranges::copy(transfer.bytes, destination);
    *transferred = static_cast<int>(transfer.bytes.size());
    return transfer.result;
}
} // extern "C"

// Device identity selection is not under test here; restrict the fake backend
// to its one fixture rather than linking the separate libusb0 backend.
namespace iPhoneMirror::transport {
bool apple_usb_serial_equal(std::string_view left, std::string_view right) noexcept {
    return left == right;
}
AppleUsbIdentity make_apple_usb_identity(const AppleUsbDevice& value) noexcept {
    return {.serial = value.serial, .topology_id = value.topology_id};
}
AppleUsbSelection select_apple_usb_device(std::span<const AppleUsbDevice> values,
    const AppleUsbIdentity&, bool) noexcept {
    return values.size() == 1 ? AppleUsbSelection{.index = 0} : AppleUsbSelection{};
}
bool apple_usb_candidate_in_scope(std::string_view topology,
    const AppleUsbIdentity&) noexcept { return topology == "1:1"; }
UsbEndpointSet select_best_quicktime_endpoints(std::span<const UsbEndpointSet> values) noexcept {
    return values.empty() ? UsbEndpointSet{} : values.front();
}
UsbEndpointSet conventional_quicktime_endpoints(const AppleUsbIdentity&) noexcept {
    ++unexpected_calls; return {};
}
} // namespace iPhoneMirror::transport

namespace {
void test_partial_timeout_preserves_packet_framing() {
    using namespace iPhoneMirror;
    transport::QtUsbContext usb(false);
    auto connection = transport::QtUsbConnection::open_quicktime(usb,
        transport::AppleUsbIdentity{.serial = "fixture", .topology_id = "1:1"});
    const auto ping = quicktime::make_ping();
    const auto need = quicktime::make_need(0x12345678);
    std::vector<std::uint8_t> bytes = ping;
    bytes.insert(bytes.end(), need.begin(), need.end());
    transfers.push_back({LIBUSB_ERROR_TIMEOUT, {bytes.begin(), bytes.begin() + 5}});
    transfers.push_back({LIBUSB_ERROR_TIMEOUT, {}});
    transfers.push_back({LIBUSB_SUCCESS, {bytes.begin() + 5, bytes.end()}});
    quicktime::StreamDecoder decoder;
    std::array<std::uint8_t, 64> buffer{};
    const auto first = connection.read(buffer, 25);
    require(first == 5, "return bytes consumed by a timed-out bulk read");
    require(decoder.push(std::span(buffer).first(first)).empty(),
        "buffer the partial first packet");
    require(connection.read(buffer, 25) == 0, "zero-byte timeout remains no data");
    const auto final = connection.read(buffer, 25);
    const auto packets = decoder.push(std::span(buffer).first(final));
    require(packets.size() == 2 && packets[0].kind == quicktime::PacketKind::Ping &&
        packets[1].subtype == quicktime::fourcc('n', 'e', 'e', 'd') &&
        packets[1].clock_ref == 0x12345678 && decoder.buffered_bytes() == 0,
        "preserve QuickTime stream framing across partial timeout and retry");

    // A full packet can also be delivered together with a timeout result.
    transfers.push_back({LIBUSB_ERROR_TIMEOUT, ping});
    require(decoder.push(std::span(buffer).first(connection.read(buffer, 25))).size() == 1,
        "decode a complete packet delivered by a timed-out transfer");
    transfers.push_back({LIBUSB_SUCCESS, {}});
    require(connection.read(buffer, 25) == 0, "empty successful reads remain empty");
    transfers.push_back({LIBUSB_ERROR_NO_DEVICE, {1, 2}});
    bool disconnected{};
    try { (void)connection.read(buffer, 25); }
    catch (const transport::UsbError& error) { disconnected = error.code() == LIBUSB_ERROR_NO_DEVICE; }
    require(disconnected, "non-timeout USB errors still fail capture");
    const auto calls_before_invalid_read = bulk_calls;
    bool invalid{};
    try { (void)connection.read({}, 25); }
    catch (const std::invalid_argument&) { invalid = true; }
    require(invalid && bulk_calls == calls_before_invalid_read,
        "reject an empty destination before calling libusb");
    connection.close();
    require(open_handles == 0 && claims == 0 && transfers.empty() && unexpected_calls == 0,
        "release all fake USB resources without unplanned device operations");
}
} // namespace

int main() {
    try {
        test_partial_timeout_preserves_packet_framing();
        std::puts("QuickTime USB transport tests passed.");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s\n", error.what());
        return 1;
    }
}
