#include "Transport/LibUsb0Transport.h"

#include <lusb0_usb.h>

#include <algorithm>
#include <cctype>
#include <climits>
#include <chrono>
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
    if (value.size() == 24 && value.find('-') == std::string::npos && value.find('&') == std::string::npos) {
        value.insert(8, "-");
    }
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

UsbEndpointSet endpoints_for(const struct usb_device& device, std::uint8_t subclass) {
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
                if (endpoints.bulk_in && endpoints.bulk_out) return endpoints;
            }
        }
    }
    return {};
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

struct usb_device* find_device(const std::string& serial, usb_dev_handle** opened = nullptr) {
    usb_init();
    usb_find_busses();
    usb_find_devices();
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId) continue;
            usb_dev_handle* handle = usb_open(device);
            if (!handle) continue;
            char value[256]{};
            const int length = usb_get_string_simple(handle, device->descriptor.iSerialNumber,
                value, sizeof(value));
            if (length > 0 && apple_usb_serial_equal(value, serial)) {
                if (opened) *opened = handle;
                else usb_close(handle);
                return device;
            }
            usb_close(handle);
        }
    }
    return nullptr;
}

void throw_last_error(const char* operation) {
    throw std::runtime_error(std::string(operation) + ": " + usb_strerror());
}

} // namespace

bool libusb0_available() noexcept {
    HMODULE module = LoadLibraryExW(L"libusb0.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return false;
    FreeLibrary(module);
    return true;
}

bool apple_usb_serial_equal(std::string_view left, std::string_view right) noexcept {
    try {
        return normalize(left) == normalize(right);
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
    std::scoped_lock lock(api_mutex);
    usb_dev_handle* handle{};
    if (!find_device(serial, &handle) || !handle) throw std::runtime_error("libusb0 cannot find selected iPhone");
    const int result = usb_control_msg(handle, 0x40, 0x52, 0, 2, nullptr, 0, 1000);
    usb_close(handle);
    if (result < 0) throw_last_error("enable QuickTime USB configuration");
    return true;
}

bool LibUsb0Connection::disable_quicktime_configuration(const std::string& serial) {
    std::scoped_lock lock(api_mutex);
    usb_dev_handle* handle{};
    if (!find_device(serial, &handle) || !handle) throw std::runtime_error("libusb0 cannot find selected iPhone");
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
    std::scoped_lock lock(api_mutex);
    LibUsb0Connection connection;
    std::string last_error = "unknown libusb0 error";
    // Windows may report BUSY for the first handle immediately after iOS
    // re-enumerates configuration 5. Re-open the device a few times; this is
    // the same settle window used by Apple's capture utility and does not
    // touch the installed filter driver.
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (attempt != 0) std::this_thread::sleep_for(std::chrono::milliseconds(250));
        struct usb_device* device = find_device(serial, &connection.handle_);
        if (!device || !connection.handle_) {
            last_error = "libusb0 cannot find selected iPhone";
            continue;
        }
        connection.endpoints_ = endpoints_for(*device, QuickTimeSubclass);
        if (!connection.endpoints_.configuration) {
            last_error = "iPhone has no QuickTime 0x2A interface";
            connection.close();
            continue;
        }
        if (usb_set_configuration(connection.handle_, connection.endpoints_.configuration) < 0) {
            last_error = usb_strerror();
            // Some libusb0 filter builds report an error when iOS has
            // already made configuration 5 active during re-enumeration.
            // The interface can still be claimed in that case; use this as
            // a narrow fallback before closing and retrying the handle.
            if (usb_claim_interface(connection.handle_, connection.endpoints_.interface_number) >= 0) {
                connection.claimed_ = true;
                return connection;
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
        return connection;
    }
    throw std::runtime_error("usb_set_configuration QuickTime: " + last_error);
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
