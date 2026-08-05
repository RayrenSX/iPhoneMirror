#include "Transport/QtUsbTransport.h"
#include "Transport/AppleUsbIdentityCache.h"

#include <Windows.h>
#include <algorithm>
#include <array>
#include <climits>
#include <format>
#include <memory>
#include <utility>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint16_t AppleVendorId = 0x05ac;
constexpr std::uint8_t VendorInterfaceClass = 0xff;
constexpr std::uint8_t UsbMuxSubclass = 0xfe;
constexpr std::uint8_t QuickTimeSubclass = 0x2a;

bool regular_file_exists(const std::wstring& path) noexcept {
    const auto attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool usbdk_helper_installed() noexcept {
    try {
        wchar_t system_directory[MAX_PATH]{};
        const auto system_length = GetSystemDirectoryW(system_directory, MAX_PATH);
        if (system_length > 0 && system_length < MAX_PATH) {
            std::wstring path(system_directory, system_length);
            path += L"\\UsbDkHelper.dll";
            if (regular_file_exists(path)) return true;
        }

        wchar_t program_files[MAX_PATH]{};
        const auto program_files_length = GetEnvironmentVariableW(
            L"ProgramFiles", program_files, MAX_PATH);
        if (program_files_length == 0 || program_files_length >= MAX_PATH) return false;
        std::wstring path(program_files, program_files_length);
        path += L"\\UsbDk Runtime Library\\UsbDkHelper.dll";
        return regular_file_exists(path);
    } catch (...) {
        return false;
    }
}

struct DeviceListDeleter {
    void operator()(libusb_device** devices) const noexcept { if (devices) libusb_free_device_list(devices, 1); }
};

struct ConfigDeleter {
    void operator()(libusb_config_descriptor* descriptor) const noexcept { if (descriptor) libusb_free_config_descriptor(descriptor); }
};

std::vector<UsbEndpointSet> endpoint_candidates_for(
    libusb_device* device, std::uint8_t wanted_subclass) {
    libusb_device_descriptor device_descriptor{};
    const int descriptor_result = libusb_get_device_descriptor(device, &device_descriptor);
    if (descriptor_result != LIBUSB_SUCCESS) throw UsbError("libusb_get_device_descriptor", descriptor_result);

    std::vector<UsbEndpointSet> candidates;
    for (std::uint8_t config_index = 0;
        config_index < device_descriptor.bNumConfigurations; ++config_index) {
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
                found.alternate_setting = interface.bAlternateSetting;
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
                if (found.bulk_in != 0 && found.bulk_out != 0)
                    candidates.push_back(found);
            }
        }
    }
    return candidates;
}

UsbEndpointSet endpoints_for(libusb_device* device,
    std::uint8_t wanted_subclass) {
    const auto candidates = endpoint_candidates_for(device, wanted_subclass);
    return select_best_quicktime_endpoints(candidates);
}

std::string topology_for(libusb_device* device) {
    std::array<std::uint8_t, 8> ports{};
    const int count = libusb_get_port_numbers(device, ports.data(),
        static_cast<int>(ports.size()));
    if (count <= 0) return {};
    std::string result = std::to_string(libusb_get_bus_number(device));
    result.push_back(':');
    for (int index{}; index < count; ++index) {
        if (index != 0) result.push_back('.');
        result += std::to_string(ports[static_cast<std::size_t>(index)]);
    }
    return result;
}

void populate_descriptor_summary(libusb_device* device,
    const libusb_device_descriptor& descriptor, AppleUsbDevice& info) {
    info.configuration_count = descriptor.bNumConfigurations;
    info.topology_id = topology_for(device);
    for (std::uint8_t index{}; index < descriptor.bNumConfigurations; ++index) {
        libusb_config_descriptor* raw_config{};
        if (libusb_get_config_descriptor(device, index, &raw_config) !=
            LIBUSB_SUCCESS) continue;
        std::unique_ptr<libusb_config_descriptor, ConfigDeleter> config(raw_config);
        info.highest_configuration_value = (std::max)(
            info.highest_configuration_value, config->bConfigurationValue);
    }
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

libusb_device* find_device(const QtUsbContext& context,
    const AppleUsbIdentity& identity, AppleUsbDevice& info,
    bool require_quicktime = false) {
    libusb_device** raw_devices{};
    const auto count = libusb_get_device_list(context.native(), &raw_devices);
    if (count < 0) throw UsbError("libusb_get_device_list", static_cast<int>(count));
    std::unique_ptr<libusb_device*, DeviceListDeleter> devices(raw_devices);
    std::vector<AppleUsbDevice> candidates;
    std::vector<libusb_device*> candidate_devices;
    std::vector<libusb_device_descriptor> candidate_descriptors;
    for (std::ptrdiff_t index = 0; index < count; ++index) {
        libusb_device_descriptor descriptor{};
        if (libusb_get_device_descriptor(raw_devices[index], &descriptor) !=
                LIBUSB_SUCCESS || descriptor.idVendor != AppleVendorId) continue;
        AppleUsbDevice candidate;
        candidate.vendor_id = descriptor.idVendor;
        candidate.product_id = descriptor.idProduct;
        candidate.bus = libusb_get_bus_number(raw_devices[index]);
        candidate.address = libusb_get_device_address(raw_devices[index]);
        candidate.configuration_count = descriptor.bNumConfigurations;
        candidate.topology_id = topology_for(raw_devices[index]);
        if (!identity.topology_id.empty() &&
            candidate.topology_id != identity.topology_id) continue;
        candidate.serial = cached_active_apple_usb_serial(candidate.topology_id);
        candidate.can_open = !candidate.serial.empty();
        candidates.push_back(std::move(candidate));
        candidate_devices.push_back(raw_devices[index]);
        candidate_descriptors.push_back(descriptor);
    }
    // Resolve topology-bound active devices without opening a handle. If the
    // target is not cached, probe one candidate at a time and stop at the
    // first exact serial match. This avoids reopening unrelated phones and
    // preserves a fallback for devices whose PID changed after re-enumeration.
    auto selection = select_apple_usb_device(candidates, identity, false);
    if (!selection.index) {
        std::vector<std::size_t> probe_order(candidates.size());
        for (std::size_t index{}; index < candidates.size(); ++index)
            probe_order[index] = index;
        std::stable_sort(probe_order.begin(), probe_order.end(),
            [&](std::size_t left, std::size_t right) {
                const bool left_pid = identity.original_product_id != 0 &&
                    candidates[left].product_id == identity.original_product_id;
                const bool right_pid = identity.original_product_id != 0 &&
                    candidates[right].product_id == identity.original_product_id;
                return left_pid && !right_pid;
            });
        for (const auto index : probe_order) {
            if (!candidates[index].serial.empty()) continue;
            bool can_open{};
            candidates[index].serial = serial_for(candidate_devices[index],
                candidate_descriptors[index], can_open);
            candidates[index].can_open = can_open;
            if (apple_usb_serial_equal(candidates[index].serial,
                    identity.serial)) {
                selection.index = index;
                selection.match_kind = AppleUsbMatchKind::Serial;
                break;
            }
        }
    }
    if (!selection.index) return nullptr;
    const auto selected_index = *selection.index;
    auto& selected_info = candidates[selected_index];
    populate_descriptor_summary(candidate_devices[selected_index],
        candidate_descriptors[selected_index], selected_info);
    selected_info.mux_endpoints = endpoints_for(candidate_devices[selected_index],
        UsbMuxSubclass);
    selected_info.quicktime_endpoints = endpoints_for(
        candidate_devices[selected_index], QuickTimeSubclass);
    selected_info.mux_configuration =
        selected_info.mux_endpoints.configuration != 0;
    selected_info.quicktime_configuration =
        selected_info.quicktime_endpoints.configuration != 0;
    if (require_quicktime && !selected_info.quicktime_configuration)
        return nullptr;
    info = std::move(selected_info);
    libusb_ref_device(candidate_devices[selected_index]);
    return candidate_devices[selected_index];
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
        populate_descriptor_summary(raw_devices[index], descriptor, device);
        device.serial = cached_active_apple_usb_serial(device.topology_id);
        device.can_open = !device.serial.empty();
        if (device.serial.empty())
            device.serial = serial_for(raw_devices[index], descriptor,
                device.can_open);
        device.mux_endpoints = endpoints_for(raw_devices[index], UsbMuxSubclass);
        device.quicktime_endpoints = endpoints_for(raw_devices[index], QuickTimeSubclass);
        device.mux_configuration = device.mux_endpoints.configuration != 0;
        device.quicktime_configuration = device.quicktime_endpoints.configuration != 0;
        result.push_back(std::move(device));
    }
    return result;
}

std::optional<AppleUsbDevice> QtUsbContext::find_apple_device(
    const AppleUsbIdentity& identity, bool require_quicktime) const {
    AppleUsbDevice info;
    libusb_device* device = find_device(*this, identity, info,
        require_quicktime);
    std::unique_ptr<libusb_device, decltype(&libusb_unref_device)> guard(
        device, &libusb_unref_device);
    if (!device)
        return std::nullopt;
    return info;
}

QtUsbConnection::~QtUsbConnection() { close(); }
QtUsbConnection::QtUsbConnection(QtUsbConnection&& other) noexcept
    : handle_(other.handle_), endpoints_(other.endpoints_), claimed_(other.claimed_),
      active_identity_retained_(other.active_identity_retained_),
      active_topology_(std::move(other.active_topology_)),
      active_serial_(std::move(other.active_serial_)) {
    other.handle_ = nullptr;
    other.claimed_ = false;
    other.active_identity_retained_ = false;
    other.active_topology_.clear();
    other.active_serial_.clear();
}
QtUsbConnection& QtUsbConnection::operator=(QtUsbConnection&& other) noexcept {
    if (this != &other) {
        close();
        handle_ = other.handle_;
        endpoints_ = other.endpoints_;
        claimed_ = other.claimed_;
        active_identity_retained_ = other.active_identity_retained_;
        active_topology_ = std::move(other.active_topology_);
        active_serial_ = std::move(other.active_serial_);
        other.handle_ = nullptr;
        other.claimed_ = false;
        other.active_identity_retained_ = false;
        other.active_topology_.clear();
        other.active_serial_.clear();
    }
    return *this;
}

void QtUsbConnection::remember_active_identity(
    const AppleUsbIdentity& identity) noexcept {
    if (identity.topology_id.empty() || identity.serial.empty()) return;
    try {
        active_topology_ = identity.topology_id;
        active_serial_ = identity.serial;
        active_identity_retained_ = retain_active_apple_usb_identity(identity);
    } catch (...) {
        active_identity_retained_ = false;
        active_topology_.clear();
        active_serial_.clear();
    }
}

bool QtUsbConnection::enable_quicktime_configuration(QtUsbContext& context, const std::string& serial) {
    return enable_quicktime_configuration(context,
        AppleUsbIdentity{.serial = serial});
}

bool QtUsbConnection::enable_quicktime_configuration(QtUsbContext& context,
    const AppleUsbIdentity& identity) {
    AppleUsbDevice info;
    libusb_device* device = find_device(context, identity, info);
    if (!device)
        throw std::runtime_error("Apple USB device not found by serial or physical port");
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
    // The successful operation disconnects the device and may surface as
    // NO_DEVICE/IO on some Windows backends. Re-enumeration is the authority.
    return result >= 0;
}

QtUsbConnection QtUsbConnection::open_quicktime(QtUsbContext& context, const std::string& serial) {
    return open_quicktime(context, AppleUsbIdentity{.serial = serial});
}

QtUsbConnection QtUsbConnection::open_quicktime(QtUsbContext& context,
    const AppleUsbIdentity& identity, bool allow_conventional_fallback) {
    AppleUsbDevice info;
    libusb_device* device = find_device(context, identity, info);
    if (!device)
        throw std::runtime_error("Apple USB device not found by serial or physical port");
    std::unique_ptr<libusb_device, decltype(&libusb_unref_device)> device_guard(device, &libusb_unref_device);
    if (!info.quicktime_configuration && allow_conventional_fallback) {
        info.quicktime_endpoints = conventional_quicktime_endpoints(identity);
        info.quicktime_configuration =
            info.quicktime_endpoints.configuration != 0;
    }
    if (!info.quicktime_configuration)
        throw std::runtime_error("Apple device has no QuickTime 0x2A USB interface");

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
    if (result.endpoints_.alternate_setting != 0) {
        const int alternate_result = libusb_set_interface_alt_setting(
            result.handle_, result.endpoints_.interface_number,
            result.endpoints_.alternate_setting);
        if (alternate_result != LIBUSB_SUCCESS)
            throw UsbError("libusb_set_interface_alt_setting", alternate_result);
    }
    auto active_identity = make_apple_usb_identity(info);
    if (active_identity.serial.empty()) active_identity.serial = identity.serial;
    result.remember_active_identity(active_identity);
    return result;
}

bool QtUsbConnection::disable_quicktime_configuration(QtUsbContext& context,
    const AppleUsbIdentity& identity) {
    AppleUsbDevice info;
    libusb_device* device = find_device(context, identity, info);
    if (!device)
        throw std::runtime_error("Apple USB device not found by serial or physical port");
    std::unique_ptr<libusb_device, decltype(&libusb_unref_device)> device_guard(
        device, &libusb_unref_device);
    libusb_device_handle* handle{};
    const int open_result = libusb_open(device, &handle);
    if (open_result != LIBUSB_SUCCESS) throw UsbError("libusb_open", open_result);
    std::unique_ptr<libusb_device_handle, decltype(&libusb_close)> handle_guard(
        handle, &libusb_close);
    const int result = libusb_control_transfer(handle,
        static_cast<std::uint8_t>(LIBUSB_ENDPOINT_OUT) |
            static_cast<std::uint8_t>(LIBUSB_REQUEST_TYPE_VENDOR) |
            static_cast<std::uint8_t>(LIBUSB_RECIPIENT_DEVICE),
        0x52, 0, 0, nullptr, 0, 1000);
    if (result < 0) throw UsbError("disable QuickTime USB configuration", result);
    return true;
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
    auto* handle = std::exchange(handle_, nullptr);
    const auto claimed = std::exchange(claimed_, false);
    if (handle) {
        if (claimed) libusb_release_interface(handle, endpoints_.interface_number);
        libusb_close(handle);
    }
    if (active_identity_retained_) {
        release_active_apple_usb_identity(active_topology_, active_serial_);
        active_identity_retained_ = false;
    }
    active_topology_.clear();
    active_serial_.clear();
}

namespace {

class WindowsUsbRuntimeProbeSource final : public UsbRuntimeProbeSource {
public:
    void read_user_mode_metadata(UsbRuntimeProbe& probe) override {
        const auto* version = libusb_get_version();
        probe.runtime_available = version != nullptr;
        if (version) {
            probe.version = std::format("{}.{}.{}.{}", version->major,
                version->minor, version->micro, version->nano);
        }
        probe.usbdk_helper_installed = usbdk_helper_installed();
    }

    void probe_usb_backends(UsbRuntimeProbe& probe) override {
        QtUsbContext default_context(false);
        probe.apple_device_count =
            static_cast<std::uint32_t>(default_context.enumerate().size());
        probe.apple_device_count_probed = true;
        if (!probe.usbdk_helper_installed) return;
        try {
            QtUsbContext usbdk_context(true);
            probe.usbdk_backend_available = true;
            probe.usbdk_backend_probed = true;
            probe.apple_device_count = (std::max)(probe.apple_device_count,
                static_cast<std::uint32_t>(usbdk_context.enumerate().size()));
        } catch (...) {
            probe.usbdk_backend_available = false;
            probe.usbdk_backend_probed = true;
        }
    }
};

} // namespace

UsbRuntimeProbe probe_usb_runtime() noexcept {
    WindowsUsbRuntimeProbeSource source;
    return probe_usb_runtime(source, false);
}

UsbRuntimeProbe probe_usb_runtime(UsbRuntimeProbeSource& source,
    bool probe_backends) noexcept {
    UsbRuntimeProbe probe;
    try {
        source.read_user_mode_metadata(probe);
        // Environment polling runs automatically at startup and every two
        // seconds. It intentionally stops after user-mode metadata. Backend
        // initialization and enumeration are reserved for an explicit capture
        // action because they can enter third-party USB kernel filters.
        if (probe_backends) source.probe_usb_backends(probe);
    } catch (const std::exception& error) {
        probe.error = error.what();
    } catch (...) {
        probe.error = "unknown USB runtime probe failure";
    }
    return probe;
}

} // namespace iPhoneMirror::transport
