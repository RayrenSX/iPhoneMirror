#pragma once

#include <libusb.h>

#include <cstdint>
#include <optional>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace iPhoneMirror::transport {

struct UsbEndpointSet {
    std::uint8_t configuration{};
    std::uint8_t interface_number{};
    std::uint8_t alternate_setting{};
    std::uint8_t bulk_in{};
    std::uint8_t bulk_out{};
    std::uint16_t bulk_in_packet_size{};
    std::uint16_t bulk_out_packet_size{};
};

struct AppleUsbDevice {
    std::uint16_t vendor_id{};
    std::uint16_t product_id{};
    std::uint8_t bus{};
    std::uint8_t address{};
    std::string serial;
    // Stable physical-port identity for the lifetime of a cable connection.
    // USB addresses and PIDs may change when the hidden capture configuration
    // is enabled, so neither is suitable for matching a re-enumerated device.
    std::string topology_id;
    bool can_open{};
    bool mux_configuration{};
    bool quicktime_configuration{};
    // Active bConfigurationValue read from the currently opened device. A
    // QuickTime descriptor can remain cached after the device has returned to
    // its normal configuration, so descriptor presence alone is not state.
    std::uint8_t active_configuration{};
    bool active_configuration_known{};
    std::uint8_t configuration_count{};
    std::uint8_t highest_configuration_value{};
    UsbEndpointSet mux_endpoints;
    UsbEndpointSet quicktime_endpoints;
};

struct AppleUsbIdentity {
    std::string serial;
    std::string topology_id;
    std::uint16_t original_product_id{};
    // The QuickTime configuration is appended to the normal descriptor set.
    // Modern USB-C iPads commonly append configuration 6 rather than 5.
    std::uint8_t expected_quicktime_configuration{};
};

enum class AppleUsbMatchKind {
    None,
    Serial,
    Topology,
};

struct AppleUsbSelection {
    std::optional<std::size_t> index;
    AppleUsbMatchKind match_kind{AppleUsbMatchKind::None};
    bool ambiguous{};
    std::size_t serial_matches{};
    std::size_t topology_matches{};
};

[[nodiscard]] AppleUsbIdentity make_apple_usb_identity(
    const AppleUsbDevice& device) noexcept;
[[nodiscard]] AppleUsbSelection select_apple_usb_device(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity,
    bool require_quicktime) noexcept;
[[nodiscard]] bool apple_usb_candidate_in_scope(
    std::string_view candidate_topology,
    const AppleUsbIdentity& identity) noexcept;
[[nodiscard]] UsbEndpointSet select_best_quicktime_endpoints(
    std::span<const UsbEndpointSet> candidates) noexcept;
[[nodiscard]] UsbEndpointSet conventional_quicktime_endpoints(
    const AppleUsbIdentity& identity) noexcept;
[[nodiscard]] std::string describe_apple_usb_candidates(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity);

// A live capture handle already proves the device identity for its physical
// port. Cache that proof inside each backend's topology namespace. Capture
// startup prefers the backend already used by live sessions and never assumes
// that unrelated backend-specific topology strings are interchangeable.
[[nodiscard]] std::string cached_active_apple_usb_serial(
    std::string_view topology) noexcept;
[[nodiscard]] bool retain_active_apple_usb_identity(
    const AppleUsbIdentity& identity) noexcept;
void release_active_apple_usb_identity(
    std::string_view topology, std::string_view serial) noexcept;

struct UsbRuntimeProbe {
    bool runtime_available{};
    bool usbdk_helper_installed{};
    bool usbdk_backend_probed{};
    bool usbdk_backend_available{};
    std::string version;
    bool apple_device_count_probed{};
    std::uint32_t apple_device_count{};
    std::string error;
};

class UsbRuntimeProbeSource {
public:
    virtual ~UsbRuntimeProbeSource() = default;
    virtual void read_user_mode_metadata(UsbRuntimeProbe& probe) = 0;
    virtual void probe_usb_backends(UsbRuntimeProbe& probe) = 0;
};

class UsbError final : public std::runtime_error {
public:
    UsbError(std::string operation, int code);
    [[nodiscard]] int code() const noexcept { return code_; }
private:
    int code_;
};

class QtUsbContext {
public:
    explicit QtUsbContext(bool use_usbdk);
    ~QtUsbContext();
    QtUsbContext(const QtUsbContext&) = delete;
    QtUsbContext& operator=(const QtUsbContext&) = delete;

    [[nodiscard]] std::vector<AppleUsbDevice> enumerate() const;
    [[nodiscard]] std::optional<AppleUsbDevice> find_apple_device(
        const AppleUsbIdentity& identity,
        bool require_quicktime = false) const;
    [[nodiscard]] libusb_context* native() const noexcept { return context_; }
    [[nodiscard]] bool using_usbdk() const noexcept { return using_usbdk_; }

private:
    libusb_context* context_{};
    bool using_usbdk_{};
};

class QtUsbConnection {
public:
    QtUsbConnection() = default;
    ~QtUsbConnection();
    QtUsbConnection(const QtUsbConnection&) = delete;
    QtUsbConnection& operator=(const QtUsbConnection&) = delete;
    QtUsbConnection(QtUsbConnection&& other) noexcept;
    QtUsbConnection& operator=(QtUsbConnection&& other) noexcept;

    [[nodiscard]] static QtUsbConnection open_quicktime(QtUsbContext& context,
        const AppleUsbIdentity& identity, bool allow_conventional_fallback = false);
    [[nodiscard]] static QtUsbConnection open_quicktime(QtUsbContext& context,
        const std::string& serial);
    [[nodiscard]] static bool enable_quicktime_configuration(QtUsbContext& context,
        const AppleUsbIdentity& identity);
    [[nodiscard]] static bool enable_quicktime_configuration(QtUsbContext& context,
        const std::string& serial);
    [[nodiscard]] static bool disable_quicktime_configuration(QtUsbContext& context,
        const AppleUsbIdentity& identity);

    [[nodiscard]] std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms);
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms);
    void clear_halt();
    void recover_handshake();
    [[nodiscard]] bool request_normal_configuration();
    void cancel_pending_io() noexcept;
    void clear_io_cancellation() noexcept;
    void close() noexcept;
    [[nodiscard]] bool valid() const noexcept { return handle_ != nullptr; }

private:
    void remember_active_identity(const AppleUsbIdentity& identity) noexcept;

    libusb_device_handle* handle_{};
    UsbEndpointSet endpoints_{};
    bool claimed_{};
    bool active_identity_retained_{};
    std::string active_topology_;
    std::string active_serial_;
};

[[nodiscard]] UsbRuntimeProbe probe_usb_runtime() noexcept;
[[nodiscard]] UsbRuntimeProbe probe_usb_runtime(
    UsbRuntimeProbeSource& source, bool probe_backends = false) noexcept;

} // namespace iPhoneMirror::transport
