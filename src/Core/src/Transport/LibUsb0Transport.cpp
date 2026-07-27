#include "Transport/LibUsb0Transport.h"

#include <lusb0_usb.h>

#include <algorithm>
#include <cctype>
#include <climits>
#include <chrono>
#include <format>
#include <mutex>
#include <stdexcept>
#include <thread>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint16_t AppleVendorId = 0x05ac;
constexpr std::uint8_t QuickTimeSubclass = 0x2a;
constexpr std::uint8_t QuickTimePlaceholderSubclass = 0xfd;
std::mutex api_mutex;

std::string normalize(std::string_view source) {
    std::string value(source);
    const auto embedded_null = value.find('\0');
    if (embedded_null != std::string::npos) {
        if (!std::all_of(value.begin() + static_cast<std::ptrdiff_t>(embedded_null),
                value.end(), [](char ch) { return ch == '\0'; })) {
            return {};
        }
        value.resize(embedded_null);
    }
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
        value.pop_back();
    std::size_t leading{};
    while (leading < value.size() &&
        std::isspace(static_cast<unsigned char>(value[leading]))) ++leading;
    if (leading != 0) value.erase(0, leading);
    if (value.size() == 24 && value.find('-') == std::string::npos && value.find('&') == std::string::npos) {
        value.insert(8, "-");
    }
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

std::vector<UsbEndpointSet> endpoint_candidates_for(
    const struct usb_device& device, std::uint8_t subclass) {
    std::vector<UsbEndpointSet> candidates;
    for (int c = 0; c < device.descriptor.bNumConfigurations; ++c) {
        const auto& config = device.config[c];
        for (int i = 0; i < config.bNumInterfaces; ++i) {
            const auto& group = config.interface[i];
            for (int a = 0; a < group.num_altsetting; ++a) {
                const auto& interface_descriptor = group.altsetting[a];
                if (interface_descriptor.bInterfaceClass != 0xff ||
                    interface_descriptor.bInterfaceSubClass != subclass) continue;
                UsbEndpointSet endpoints;
                endpoints.configuration = config.bConfigurationValue;
                endpoints.interface_number = interface_descriptor.bInterfaceNumber;
                endpoints.alternate_setting = interface_descriptor.bAlternateSetting;
                for (int e = 0; e < interface_descriptor.bNumEndpoints; ++e) {
                    const auto& endpoint = interface_descriptor.endpoint[e];
                    if ((endpoint.bmAttributes & 3U) != 2U) continue;
                    if ((endpoint.bEndpointAddress & 0x80U) != 0) {
                        if (endpoint.wMaxPacketSize >= endpoints.bulk_in_packet_size) {
                            endpoints.bulk_in = endpoint.bEndpointAddress;
                            endpoints.bulk_in_packet_size = endpoint.wMaxPacketSize;
                        }
                    } else if (endpoint.wMaxPacketSize >= endpoints.bulk_out_packet_size) {
                        endpoints.bulk_out = endpoint.bEndpointAddress;
                        endpoints.bulk_out_packet_size = endpoint.wMaxPacketSize;
                    }
                }
                if (endpoints.bulk_in && endpoints.bulk_out)
                    candidates.push_back(endpoints);
            }
        }
    }
    return candidates;
}

UsbEndpointSet endpoints_for(const struct usb_device& device, std::uint8_t subclass) {
    const auto candidates = endpoint_candidates_for(device, subclass);
    return select_best_quicktime_endpoints(candidates);
}

std::string topology_for(const struct usb_device& device) {
    if (!device.bus) return {};
    // libusb-win32 does not expose USB port numbers. Its bus location and
    // device path are nevertheless stable across the filter driver's normal
    // refresh on Windows and are only used after an exact serial match fails.
    return std::format("{}:{:08x}:{}", device.bus->dirname,
        device.bus->location, device.filename);
}

void populate_descriptor_summary(const struct usb_device& raw,
    AppleUsbDevice& info) noexcept {
    info.configuration_count = raw.descriptor.bNumConfigurations;
    for (int index = 0; index < raw.descriptor.bNumConfigurations; ++index) {
        info.highest_configuration_value = (std::max)(
            info.highest_configuration_value,
            raw.config[index].bConfigurationValue);
    }
    try { info.topology_id = topology_for(raw); } catch (...) {}
}

int mux_configuration_for(const struct usb_device& device) {
    int selected{};
    for (int c = 0; c < device.descriptor.bNumConfigurations; ++c) {
        const auto& config = device.config[c];
        bool has_mux{};
        bool has_quicktime{};
        for (int i = 0; i < config.bNumInterfaces; ++i) {
            const auto& group = config.interface[i];
            for (int a = 0; a < group.num_altsetting; ++a) {
                const auto subclass = group.altsetting[a].bInterfaceSubClass;
                has_mux = has_mux || subclass == 0xfe;
                // Older iOS descriptors expose the screen-capture interface
                // as 0xFD before activation and change it to 0x2A afterwards.
                // Neither descriptor belongs to the normal USBMux
                // configuration.  Ignoring 0xFD makes iOS 18 restore config
                // 4 instead of config 3, leaving AppleMobileDeviceService
                // unable to rediscover the phone after capture stops.
                has_quicktime = has_quicktime || subclass == QuickTimeSubclass ||
                    subclass == QuickTimePlaceholderSubclass;
            }
        }
        if (has_mux && !has_quicktime) {
            selected = (std::max)(selected, static_cast<int>(config.bConfigurationValue));
        }
    }
    return selected;
}

struct usb_device* find_device(const AppleUsbIdentity& identity,
    usb_dev_handle** opened = nullptr, bool require_quicktime = false) {
    usb_init();
    usb_find_busses();
    usb_find_devices();
    std::vector<AppleUsbDevice> candidates;
    std::vector<struct usb_device*> raw_candidates;
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId) continue;
            AppleUsbDevice candidate;
            candidate.vendor_id = device->descriptor.idVendor;
            candidate.product_id = device->descriptor.idProduct;
            candidate.bus = bus
                ? static_cast<std::uint8_t>(bus->location & 0xffU) : 0;
            candidate.address = device->devnum;
            populate_descriptor_summary(*device, candidate);
            candidate.quicktime_endpoints = endpoints_for(*device,
                QuickTimeSubclass);
            candidate.quicktime_configuration =
                candidate.quicktime_endpoints.configuration != 0;
            usb_dev_handle* handle = usb_open(device);
            if (handle) {
                candidate.can_open = true;
                char value[256]{};
                const int length = usb_get_string_simple(handle,
                    device->descriptor.iSerialNumber, value, sizeof(value));
                if (length > 0) candidate.serial.assign(value,
                    static_cast<std::size_t>(length));
                usb_close(handle);
            }
            candidates.push_back(std::move(candidate));
            raw_candidates.push_back(device);
        }
    }
    const auto selection = select_apple_usb_device(candidates, identity,
        require_quicktime);
    if (!selection.index) return nullptr;
    auto* selected = raw_candidates[*selection.index];
    if (opened) {
        *opened = usb_open(selected);
        if (!*opened) return nullptr;
    }
    return selected;
}

struct usb_device* find_device(const std::string& serial,
    usb_dev_handle** opened = nullptr, bool require_quicktime = false) {
    return find_device(AppleUsbIdentity{.serial = serial}, opened,
        require_quicktime);
}

void throw_last_error(const char* operation) {
    throw std::runtime_error(std::string(operation) + ": " + usb_strerror());
}

} // namespace

AppleUsbIdentity make_apple_usb_identity(const AppleUsbDevice& device) noexcept {
    AppleUsbIdentity identity;
    try {
        identity.serial = device.serial;
        identity.topology_id = device.topology_id;
    } catch (...) {
        return {};
    }
    identity.original_product_id = device.product_id;
    if (device.quicktime_endpoints.configuration != 0) {
        identity.expected_quicktime_configuration =
            device.quicktime_endpoints.configuration;
    } else if (device.highest_configuration_value != 0 &&
        device.highest_configuration_value != 0xff) {
        identity.expected_quicktime_configuration = static_cast<std::uint8_t>(
            device.highest_configuration_value + 1U);
    }
    return identity;
}

AppleUsbSelection select_apple_usb_device(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity,
    bool require_quicktime) noexcept {
    AppleUsbSelection result;
    std::optional<std::size_t> serial_index;
    std::optional<std::size_t> serial_topology_index;
    std::size_t serial_topology_matches{};
    std::optional<std::size_t> topology_index;

    for (std::size_t index{}; index < devices.size(); ++index) {
        const auto& device = devices[index];
        if (require_quicktime && !device.quicktime_configuration) continue;
        const bool serial_match = !identity.serial.empty() &&
            apple_usb_serial_equal(device.serial, identity.serial);
        const bool topology_match = !identity.topology_id.empty() &&
            device.topology_id == identity.topology_id;
        bool candidate_serial_available{};
        try {
            candidate_serial_available = !normalize(device.serial).empty();
        } catch (...) {}
        if (serial_match) {
            ++result.serial_matches;
            serial_index = index;
            if (topology_match) {
                ++serial_topology_matches;
                serial_topology_index = index;
            }
        }
        // A physical-port fallback only bridges a descriptor whose serial is
        // temporarily unreadable. A known, different serial is authoritative
        // and must never be rebound to this capture session.
        if (topology_match && !candidate_serial_available) {
            ++result.topology_matches;
            topology_index = index;
        }
    }

    if (result.serial_matches == 1) {
        result.index = serial_index;
        result.match_kind = AppleUsbMatchKind::Serial;
        return result;
    }
    if (result.serial_matches > 1) {
        if (serial_topology_matches == 1) {
            result.index = serial_topology_index;
            result.match_kind = AppleUsbMatchKind::Serial;
        } else {
            result.ambiguous = true;
        }
        return result;
    }
    if (result.topology_matches == 1) {
        result.index = topology_index;
        result.match_kind = AppleUsbMatchKind::Topology;
        return result;
    }
    result.ambiguous = result.topology_matches > 1;
    return result;
}

UsbEndpointSet select_best_quicktime_endpoints(
    std::span<const UsbEndpointSet> candidates) noexcept {
    UsbEndpointSet selected;
    for (const auto& candidate : candidates) {
        if (candidate.configuration == 0 || candidate.bulk_in == 0 ||
            candidate.bulk_out == 0 || (candidate.bulk_in & 0x80U) == 0 ||
            (candidate.bulk_out & 0x80U) != 0) continue;
        const auto candidate_packet = (std::min)(candidate.bulk_in_packet_size,
            candidate.bulk_out_packet_size);
        const auto selected_packet = (std::min)(selected.bulk_in_packet_size,
            selected.bulk_out_packet_size);
        if (selected.configuration == 0 ||
            candidate.configuration > selected.configuration ||
            (candidate.configuration == selected.configuration &&
                candidate_packet > selected_packet)) {
            selected = candidate;
        }
    }
    return selected;
}

UsbEndpointSet conventional_quicktime_endpoints(
    const AppleUsbIdentity& identity) noexcept {
    if (identity.expected_quicktime_configuration == 0) return {};
    return {
        .configuration = identity.expected_quicktime_configuration,
        .interface_number = 2,
        .alternate_setting = 0,
        .bulk_in = 0x86,
        .bulk_out = 0x05,
        .bulk_in_packet_size = 512,
        .bulk_out_packet_size = 512,
    };
}

std::string describe_apple_usb_candidates(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity) {
    std::string description = std::format("count={}", devices.size());
    for (std::size_t index{}; index < devices.size(); ++index) {
        const auto& device = devices[index];
        description += std::format(
            " [{} vid={:04x} pid={:04x} bus={} addr={} open={} serial_match={} topology_match={} configs={}/{} qt={}:{}:{}:{:02x}:{:02x}]",
            index, device.vendor_id, device.product_id, device.bus, device.address,
            device.can_open,
            !identity.serial.empty() && apple_usb_serial_equal(device.serial, identity.serial),
            !identity.topology_id.empty() && device.topology_id == identity.topology_id,
            device.configuration_count, device.highest_configuration_value,
            device.quicktime_endpoints.configuration,
            device.quicktime_endpoints.interface_number,
            device.quicktime_endpoints.alternate_setting,
            device.quicktime_endpoints.bulk_in,
            device.quicktime_endpoints.bulk_out);
    }
    return description;
}

bool libusb0_available() noexcept {
    HMODULE module = LoadLibraryExW(L"libusb0.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return false;
    FreeLibrary(module);
    return true;
}

bool apple_usb_serial_equal(std::string_view left, std::string_view right) noexcept {
    try {
        const auto normalized_left = normalize(left);
        const auto normalized_right = normalize(right);
        return !normalized_left.empty() && !normalized_right.empty() &&
            normalized_left == normalized_right;
    } catch (...) {
        // This helper is also used by the C ABI readiness probe. Treat an
        // allocation failure as a non-match instead of allowing an exception
        // to cross a noexcept/native boundary.
        return false;
    }
}

std::vector<AppleUsbDevice> enumerate_libusb0() {
    std::scoped_lock lock(api_mutex);
    usb_init();
    usb_find_busses();
    usb_find_devices();
    std::vector<AppleUsbDevice> result;
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId) continue;
            AppleUsbDevice info;
            info.vendor_id = device->descriptor.idVendor;
            info.product_id = device->descriptor.idProduct;
            info.bus = device->bus
                ? static_cast<std::uint8_t>(device->bus->location & 0xffU) : 0;
            info.address = device->devnum;
            populate_descriptor_summary(*device, info);
            if (usb_dev_handle* handle = usb_open(device)) {
                info.can_open = true;
                char serial[256]{};
                if (usb_get_string_simple(handle, device->descriptor.iSerialNumber, serial, sizeof(serial)) > 0) {
                    info.serial = serial;
                }
                usb_close(handle);
            }
            info.quicktime_endpoints = endpoints_for(*device, QuickTimeSubclass);
            info.quicktime_configuration = info.quicktime_endpoints.configuration != 0;
            result.push_back(std::move(info));
        }
    }
    return result;
}

std::optional<AppleUsbDevice> find_libusb0_device(std::string_view serial) {
    if (serial.empty() || !libusb0_available()) return std::nullopt;
    for (auto& device : enumerate_libusb0()) {
        if (apple_usb_serial_equal(device.serial, serial)) return device;
    }
    return std::nullopt;
}

std::optional<AppleUsbDevice> find_libusb0_device(
    const AppleUsbIdentity& identity, bool require_quicktime) {
    if ((identity.serial.empty() && identity.topology_id.empty()) ||
        !libusb0_available()) return std::nullopt;
    auto devices = enumerate_libusb0();
    const auto selection = select_apple_usb_device(devices, identity,
        require_quicktime);
    if (!selection.index) return std::nullopt;
    return std::move(devices[*selection.index]);
}

bool is_libusb0_device_available(std::string_view serial) {
    const auto device = find_libusb0_device(serial);
    return device && device->can_open;
}

LibUsb0Connection::~LibUsb0Connection() { close(); }
LibUsb0Connection::LibUsb0Connection(LibUsb0Connection&& other) noexcept
    : handle_(other.handle_), endpoints_(other.endpoints_), claimed_(other.claimed_) {
    other.handle_ = nullptr;
    other.claimed_ = false;
}
LibUsb0Connection& LibUsb0Connection::operator=(LibUsb0Connection&& other) noexcept {
    if (this != &other) {
        close();
        handle_ = other.handle_;
        endpoints_ = other.endpoints_;
        claimed_ = other.claimed_;
        other.handle_ = nullptr;
        other.claimed_ = false;
    }
    return *this;
}

bool LibUsb0Connection::enable_quicktime_configuration(const std::string& serial) {
    return enable_quicktime_configuration(AppleUsbIdentity{.serial = serial});
}

bool LibUsb0Connection::enable_quicktime_configuration(
    const AppleUsbIdentity& identity) {
    std::scoped_lock lock(api_mutex);
    usb_dev_handle* handle{};
    if (!find_device(identity, &handle) || !handle)
        throw std::runtime_error("libusb0 cannot find the selected Apple device");
    const int result = usb_control_msg(handle, 0x40, 0x52, 0, 2, nullptr, 0, 1000);
    usb_close(handle);
    // The request itself disconnects the device. Some libusb0 filter builds
    // report that expected disconnect as an I/O failure even though iOS has
    // accepted the activation. The caller verifies success by reopening the
    // appended configuration, so preserve the uncertain result as false.
    return result >= 0;
}

bool LibUsb0Connection::disable_quicktime_configuration(const std::string& serial) {
    return disable_quicktime_configuration(AppleUsbIdentity{.serial = serial});
}

bool LibUsb0Connection::disable_quicktime_configuration(
    const AppleUsbIdentity& identity) {
    std::scoped_lock lock(api_mutex);
    usb_dev_handle* handle{};
    if (!find_device(identity, &handle) || !handle)
        throw std::runtime_error("libusb0 cannot find the selected Apple device");
    struct usb_device* device = usb_device(handle);
    const int mux_configuration = device ? mux_configuration_for(*device) : 0;
    const int result = usb_control_msg(handle, 0x40, 0x52, 0, 0, nullptr, 0, 1000);
    if (result >= 0 && mux_configuration > 0) {
        (void)usb_set_configuration(handle, mux_configuration);
    }
    usb_close(handle);
    if (result < 0) throw_last_error("disable QuickTime USB configuration");
    return true;
}

LibUsb0Connection LibUsb0Connection::open_quicktime(const std::string& serial) {
    return open_quicktime(AppleUsbIdentity{.serial = serial});
}

LibUsb0Connection LibUsb0Connection::open_quicktime(
    const AppleUsbIdentity& identity, bool allow_conventional_fallback) {
    std::scoped_lock lock(api_mutex);
    LibUsb0Connection connection;
    std::string last_error = "unknown libusb0 error";
    // Windows may report BUSY for the first handle immediately after iOS
    // re-enumerates the appended QuickTime configuration. Re-open the device a
    // few times; this is the same settle window used by Apple's capture
    // utility and does not touch the installed filter driver.
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (attempt != 0) std::this_thread::sleep_for(std::chrono::milliseconds(250));
        struct usb_device* device = find_device(identity, &connection.handle_);
        if (!device || !connection.handle_) {
            last_error = "libusb0 cannot find the selected Apple device";
            continue;
        }
        connection.endpoints_ = endpoints_for(*device, QuickTimeSubclass);
        if (!connection.endpoints_.configuration) {
            if (allow_conventional_fallback) {
                connection.endpoints_ = conventional_quicktime_endpoints(identity);
            }
            if (!connection.endpoints_.configuration) {
                last_error = "Apple device has no QuickTime 0x2A interface";
                connection.close();
                continue;
            }
        }
        if (usb_set_configuration(connection.handle_, connection.endpoints_.configuration) < 0) {
            last_error = usb_strerror();
            // Some libusb0 filter builds report an error when iOS has already
            // made the appended configuration active during re-enumeration.
            // The interface can still be claimed in that case; use this as
            // a narrow fallback before closing and retrying the handle.
            if (usb_claim_interface(connection.handle_, connection.endpoints_.interface_number) >= 0) {
                connection.claimed_ = true;
                if (connection.endpoints_.alternate_setting == 0 ||
                    usb_set_altinterface(connection.handle_,
                        connection.endpoints_.alternate_setting) >= 0) {
                    return connection;
                }
                last_error = usb_strerror();
                connection.close();
                continue;
            }
            connection.close();
            continue;
        }
        if (usb_claim_interface(connection.handle_, connection.endpoints_.interface_number) < 0) {
            last_error = usb_strerror();
            connection.close();
            continue;
        }
        connection.claimed_ = true;
        if (connection.endpoints_.alternate_setting != 0 &&
            usb_set_altinterface(connection.handle_,
                connection.endpoints_.alternate_setting) < 0) {
            last_error = usb_strerror();
            connection.close();
            continue;
        }
        return connection;
    }
    throw std::runtime_error("open QuickTime USB interface: " + last_error);
}

std::size_t LibUsb0Connection::read(std::span<std::uint8_t> destination, unsigned timeout_ms) {
    const int count = usb_bulk_read(handle_, endpoints_.bulk_in,
        reinterpret_cast<char*>(destination.data()),
        static_cast<int>(std::min<std::size_t>(destination.size(), INT_MAX)),
        static_cast<int>(timeout_ms));
    if (count < 0) {
        const std::string error = usb_strerror();
        if (error.find("timeout") != std::string::npos || error.find("Timeout") != std::string::npos) return 0;
        throw_last_error("QuickTime bulk read");
    }
    return static_cast<std::size_t>(count);
}

void LibUsb0Connection::write(std::span<const std::uint8_t> source, unsigned timeout_ms) {
    std::size_t offset{};
    while (offset < source.size()) {
        const int count = usb_bulk_write(handle_, endpoints_.bulk_out,
            reinterpret_cast<char*>(const_cast<std::uint8_t*>(source.data() + offset)),
            static_cast<int>(std::min<std::size_t>(source.size() - offset, INT_MAX)),
            static_cast<int>(timeout_ms));
        if (count <= 0) throw_last_error("QuickTime bulk write");
        offset += static_cast<std::size_t>(count);
    }
}

void LibUsb0Connection::clear_halt() {
    if (usb_clear_halt(handle_, endpoints_.bulk_in) < 0) throw_last_error("clear QuickTime IN halt");
    if (usb_clear_halt(handle_, endpoints_.bulk_out) < 0) throw_last_error("clear QuickTime OUT halt");
}

void LibUsb0Connection::recover_handshake() {
    if (usb_control_msg(handle_, 0x40, 0x40, 0x6400, 0x6400, nullptr, 0, 1000) < 0) {
        throw_last_error("recover QuickTime handshake");
    }
}

void LibUsb0Connection::disable_quicktime_configuration() {
    if (!handle_) return;
    struct usb_device* device = usb_device(handle_);
    const int mux_configuration = device ? mux_configuration_for(*device) : 0;
    if (usb_control_msg(handle_, 0x40, 0x52, 0, 0, nullptr, 0, 1000) < 0) {
        throw_last_error("disable QuickTime USB configuration");
    }
    if (mux_configuration > 0 && usb_set_configuration(handle_, mux_configuration) < 0) {
        throw_last_error("restore USBMux configuration");
    }
}

void LibUsb0Connection::close() noexcept {
    if (!handle_) return;
    if (claimed_) usb_release_interface(handle_, endpoints_.interface_number);
    usb_close(handle_);
    handle_ = nullptr;
    claimed_ = false;
}

} // namespace iPhoneMirror::transport
