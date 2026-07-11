#include "Transport/QtUsbTransport.h"

#include <Windows.h>
#include <algorithm>
#include <climits>
#include <format>
#include <cctype>
#include <memory>
#include <utility>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint16_t AppleVendorId = 0x05ac;
constexpr std::uint8_t VendorInterfaceClass = 0xff;
constexpr std::uint8_t UsbMuxSubclass = 0xfe;
constexpr std::uint8_t QuickTimeSubclass = 0x2a;

bool usbdk_helper_installed() noexcept {
    HMODULE helper = LoadLibraryExW(L"UsbDkHelper.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!helper) {
        wchar_t program_files[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"ProgramFiles", program_files, MAX_PATH);
        if (length > 0 && length < MAX_PATH) {
            std::wstring path(program_files, length);
            path += L"\\UsbDk Runtime Library\\UsbDkHelper.dll";
            helper = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        }
    }
    if (!helper) return false;
    FreeLibrary(helper);
    return true;
}

std::string normalized_serial(std::string value) {
    if (value.size() == 24 && value.find('-') == std::string::npos && value.find('&') == std::string::npos) {
        value.insert(8, "-");
    }
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

struct DeviceListDeleter {
    void operator()(libusb_device** devices) const noexcept { if (devices) libusb_free_device_list(devices, 1); }
};

struct ConfigDeleter {
    void operator()(libusb_config_descriptor* descriptor) const noexcept { if (descriptor) libusb_free_config_descriptor(descriptor); }
};

UsbEndpointSet endpoints_for(libusb_device* device, std::uint8_t wanted_subclass) {
    libusb_device_descriptor device_descriptor{};
    const int descriptor_result = libusb_get_device_descriptor(device, &device_descriptor);
    if (descriptor_result != LIBUSB_SUCCESS) throw UsbError("libusb_get_device_descriptor", descriptor_result);

    for (std::uint8_t config_index = 0; config_index < device_descriptor.bNumConfigurations; ++config_index) {
        libusb_config_descriptor* raw_config{};
        const int result = libusb_get_config_descriptor(device, config_index, &raw_config);
        if (result != LIBUSB_SUCCESS) continue;
        std::unique_ptr<libusb_config_descriptor, ConfigDeleter> config(raw_config);
        for (std::uint8_t interface_index = 0; interface_index < config->bNumInterfaces; ++interface_index) {
            const auto& interface_group = config->interface[interface_index];
            for (int alternate = 0; alternate < interface_group.num_altsetting; ++alternate) {
                const auto& interface = interface_group.altsetting[alternate];
                if (interface.bInterfaceClass != VendorInterfaceClass || interface.bInterfaceSubClass != wanted_subclass) continue;
                UsbEndpointSet found{};
                found.configuration = config->bConfigurationValue;
                found.interface_number = interface.bInterfaceNumber;
                for (std::uint8_t endpoint_index = 0; endpoint_index < interface.bNumEndpoints; ++endpoint_index) {
                    const auto& endpoint = interface.endpoint[endpoint_index];
                    if ((endpoint.bmAttributes & LIBUSB_TRANSFER_TYPE_MASK) != LIBUSB_TRANSFER_TYPE_BULK) continue;
                    if ((endpoint.bEndpointAddress & LIBUSB_ENDPOINT_DIR_MASK) == LIBUSB_ENDPOINT_IN) {
                        if (endpoint.wMaxPacketSize >= found.bulk_in_packet_size) {
                            found.bulk_in = endpoint.bEndpointAddress;
                            found.bulk_in_packet_size = endpoint.wMaxPacketSize;
                        }
                    } else if (endpoint.wMaxPacketSize >= found.bulk_out_packet_size) {
                        found.bulk_out = endpoint.bEndpointAddress;
                        found.bulk_out_packet_size = endpoint.wMaxPacketSize;
                    }
                }
                if (found.bulk_in != 0 && found.bulk_out != 0) return found;
            }
        }
    }
    return {};
}

std::string serial_for(libusb_device* device, const libusb_device_descriptor& descriptor, bool& can_open) {
    if (descriptor.iSerialNumber == 0) return {};
    libusb_device_handle* handle{};
    const int open_result = libusb_open(device, &handle);
    if (open_result != LIBUSB_SUCCESS || !handle) return {};
    can_open = true;
    std::unique_ptr<libusb_device_handle, decltype(&libusb_close)> guard(handle, &libusb_close);
    unsigned char buffer[256]{};
    const int length = libusb_get_string_descriptor_ascii(handle, descriptor.iSerialNumber, buffer, sizeof(buffer));
    return length > 0 ? std::string(reinterpret_cast<char*>(buffer), static_cast<std::size_t>(length)) : std::string{};
}

libusb_device* find_device(QtUsbContext& context, const std::string& serial, AppleUsbDevice& info) {
    libusb_device** raw_devices{};
    const auto count = libusb_get_device_list(context.native(), &raw_devices);
    if (count < 0) throw UsbError("libusb_get_device_list", static_cast<int>(count));
    std::unique_ptr<libusb_device*, DeviceListDeleter> devices(raw_devices);
    for (std::ptrdiff_t index = 0; index < count; ++index) {
        libusb_device_descriptor descriptor{};
        if (libusb_get_device_descriptor(raw_devices[index], &descriptor) != LIBUSB_SUCCESS || descriptor.idVendor != AppleVendorId) continue;
        bool can_open{};
        const auto candidate = serial_for(raw_devices[index], descriptor, can_open);
        if (normalized_serial(candidate) != normalized_serial(serial)) continue;
        info.vendor_id = descriptor.idVendor;
        info.product_id = descriptor.idProduct;
        info.serial = candidate;
        info.can_open = can_open;
        info.quicktime_endpoints = endpoints_for(raw_devices[index], QuickTimeSubclass);
        libusb_ref_device(raw_devices[index]);
        return raw_devices[index];
    }
    throw std::runtime_error("Apple USB device not found by serial");
}

} // namespace

UsbError::UsbError(std::string operation, int code)
    : std::runtime_error(std::format("{} failed: {} ({})", operation, libusb_error_name(code), code)), code_(code) {}

QtUsbContext::QtUsbContext(bool use_usbdk) {
    const int result = libusb_init(&context_);
    if (result != LIBUSB_SUCCESS) throw UsbError("libusb_init", result);
    if (use_usbdk) {
        const int option_result = libusb_set_option(context_, LIBUSB_OPTION_USE_USBDK);
        if (option_result != LIBUSB_SUCCESS) {
            libusb_exit(context_);
            context_ = nullptr;
            throw UsbError("libusb UsbDk backend", option_result);
        }
        using_usbdk_ = true;
    }
}

QtUsbContext::~QtUsbContext() { if (context_) libusb_exit(context_); }

std::vector<AppleUsbDevice> QtUsbContext::enumerate() const {
    libusb_device** raw_devices{};
    const auto count = libusb_get_device_list(context_, &raw_devices);
    if (count < 0) throw UsbError("libusb_get_device_list", static_cast<int>(count));
    std::unique_ptr<libusb_device*, DeviceListDeleter> devices(raw_devices);
    std::vector<AppleUsbDevice> result;
    for (std::ptrdiff_t index = 0; index < count; ++index) {
        libusb_device_descriptor descriptor{};
        if (libusb_get_device_descriptor(raw_devices[index], &descriptor) != LIBUSB_SUCCESS || descriptor.idVendor != AppleVendorId) continue;
        AppleUsbDevice device;
        device.vendor_id = descriptor.idVendor;
        device.product_id = descriptor.idProduct;
        device.bus = libusb_get_bus_number(raw_devices[index]);
        device.address = libusb_get_device_address(raw_devices[index]);
        device.serial = serial_for(raw_devices[index], descriptor, device.can_open);
        device.mux_endpoints = endpoints_for(raw_devices[index], UsbMuxSubclass);
        device.quicktime_endpoints = endpoints_for(raw_devices[index], QuickTimeSubclass);
        device.mux_configuration = device.mux_endpoints.configuration != 0;
        device.quicktime_configuration = device.quicktime_endpoints.configuration != 0;
        result.push_back(std::move(device));
    }
    return result;
}

QtUsbConnection::~QtUsbConnection() { close(); }
QtUsbConnection::QtUsbConnection(QtUsbConnection&& other) noexcept
    : handle_(other.handle_), endpoints_(other.endpoints_), claimed_(other.claimed_) {
    other.handle_ = nullptr;
    other.claimed_ = false;
}
QtUsbConnection& QtUsbConnection::operator=(QtUsbConnection&& other) noexcept {
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

bool QtUsbConnection::enable_quicktime_configuration(QtUsbContext& context, const std::string& serial) {
    AppleUsbDevice info;
    libusb_device* device = find_device(context, serial, info);
    std::unique_ptr<libusb_device, decltype(&libusb_unref_device)> device_guard(device, &libusb_unref_device);
    libusb_device_handle* handle{};
    const int open_result = libusb_open(device, &handle);
    if (open_result != LIBUSB_SUCCESS) throw UsbError("libusb_open", open_result);
    std::unique_ptr<libusb_device_handle, decltype(&libusb_close)> handle_guard(handle, &libusb_close);
    const int result = libusb_control_transfer(handle,
        static_cast<std::uint8_t>(LIBUSB_ENDPOINT_OUT) |
            static_cast<std::uint8_t>(LIBUSB_REQUEST_TYPE_VENDOR) |
            static_cast<std::uint8_t>(LIBUSB_RECIPIENT_DEVICE),
        0x52, 0, 2, nullptr, 0, 1000);
    if (result < 0) throw UsbError("enable QuickTime USB configuration", result);
    return true; // Device is expected to disconnect/re-enumerate immediately.
}

QtUsbConnection QtUsbConnection::open_quicktime(QtUsbContext& context, const std::string& serial) {
    AppleUsbDevice info;
    libusb_device* device = find_device(context, serial, info);
    std::unique_ptr<libusb_device, decltype(&libusb_unref_device)> device_guard(device, &libusb_unref_device);
    if (!info.quicktime_configuration) throw std::runtime_error("device has no QuickTime 0x2A USB interface");

    QtUsbConnection result;
    const int open_result = libusb_open(device, &result.handle_);
    if (open_result != LIBUSB_SUCCESS) throw UsbError("libusb_open", open_result);
    result.endpoints_ = info.quicktime_endpoints;
    const int config_result = libusb_set_configuration(result.handle_, result.endpoints_.configuration);
    if (config_result != LIBUSB_SUCCESS && config_result != LIBUSB_ERROR_BUSY) {
        throw UsbError("libusb_set_configuration", config_result);
    }
    const int claim_result = libusb_claim_interface(result.handle_, result.endpoints_.interface_number);
    if (claim_result != LIBUSB_SUCCESS) throw UsbError("libusb_claim_interface", claim_result);
    result.claimed_ = true;
    return result;
}

std::size_t QtUsbConnection::read(std::span<std::uint8_t> destination, unsigned timeout_ms) {
    if (!handle_ || destination.empty()) throw std::invalid_argument("invalid QuickTime USB read");
    int transferred{};
    const int result = libusb_bulk_transfer(handle_, endpoints_.bulk_in, destination.data(),
        static_cast<int>(std::min<std::size_t>(destination.size(), static_cast<std::size_t>(INT_MAX))),
        &transferred, timeout_ms);
    if (result == LIBUSB_ERROR_TIMEOUT) return 0;
    if (result != LIBUSB_SUCCESS) throw UsbError("QuickTime bulk read", result);
    return static_cast<std::size_t>(transferred);
}

void QtUsbConnection::write(std::span<const std::uint8_t> source, unsigned timeout_ms) {
    if (!handle_ || source.empty()) throw std::invalid_argument("invalid QuickTime USB write");
    std::size_t offset{};
    while (offset < source.size()) {
        int transferred{};
        const int amount = static_cast<int>(std::min<std::size_t>(source.size() - offset, static_cast<std::size_t>(INT_MAX)));
        const int result = libusb_bulk_transfer(handle_, endpoints_.bulk_out,
            const_cast<unsigned char*>(source.data() + offset), amount, &transferred, timeout_ms);
        if (result != LIBUSB_SUCCESS) throw UsbError("QuickTime bulk write", result);
        if (transferred <= 0) throw std::runtime_error("QuickTime bulk write made no progress");
        offset += static_cast<std::size_t>(transferred);
    }
}

void QtUsbConnection::clear_halt() {
    if (!handle_) return;
    const int in_result = libusb_clear_halt(handle_, endpoints_.bulk_in);
    if (in_result != LIBUSB_SUCCESS) throw UsbError("clear QuickTime IN halt", in_result);
    const int out_result = libusb_clear_halt(handle_, endpoints_.bulk_out);
    if (out_result != LIBUSB_SUCCESS) throw UsbError("clear QuickTime OUT halt", out_result);
}

void QtUsbConnection::recover_handshake() {
    if (!handle_) return;
    const int result = libusb_control_transfer(handle_, 0x40, 0x40, 0x6400, 0x6400,
        nullptr, 0, 1000);
    if (result < 0) throw UsbError("recover QuickTime handshake", result);
}

void QtUsbConnection::disable_quicktime_configuration() {
    if (!handle_) return;
    const int result = libusb_control_transfer(handle_,
        static_cast<std::uint8_t>(LIBUSB_ENDPOINT_OUT) |
            static_cast<std::uint8_t>(LIBUSB_REQUEST_TYPE_VENDOR) |
            static_cast<std::uint8_t>(LIBUSB_RECIPIENT_DEVICE),
        0x52, 0, 0, nullptr, 0, 1000);
    if (result < 0) throw UsbError("disable QuickTime USB configuration", result);
}

void QtUsbConnection::close() noexcept {
    if (!handle_) return;
    if (claimed_) libusb_release_interface(handle_, endpoints_.interface_number);
    libusb_close(handle_);
    handle_ = nullptr;
    claimed_ = false;
}

UsbRuntimeProbe probe_usb_runtime() noexcept {
    UsbRuntimeProbe probe;
    try {
        const auto* version = libusb_get_version();
        probe.runtime_available = version != nullptr;
        if (version) probe.version = std::format("{}.{}.{}.{}", version->major, version->minor, version->micro, version->nano);
        QtUsbContext default_context(false);
        probe.apple_device_count = static_cast<std::uint32_t>(default_context.enumerate().size());
        if (usbdk_helper_installed()) {
          try {
            QtUsbContext usbdk_context(true);
            probe.usbdk_backend_available = true;
            probe.apple_device_count = std::max(probe.apple_device_count,
                static_cast<std::uint32_t>(usbdk_context.enumerate().size()));
          } catch (...) {
            probe.usbdk_backend_available = false;
          }
        }
    } catch (const std::exception& error) {
        probe.error = error.what();
    }
    return probe;
}

} // namespace iPhoneMirror::transport
