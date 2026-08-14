#pragma once

#include "Transport/LibUsb0Readiness.h"
#include "Transport/QtUsbTransport.h"

#include <optional>
#include <span>
#include <stop_token>
#include <string>
#include <string_view>
#include <vector>

struct usb_dev_handle;

namespace iPhoneMirror::transport {

// Pure file-system check for automatic environment polling. It must not load
// the legacy user-mode DLL or enter its kernel filter.
[[nodiscard]] bool libusb0_installed() noexcept;
// Loadability check used only after an explicit wired-capture action.
[[nodiscard]] bool libusb0_available() noexcept;
[[nodiscard]] std::vector<AppleUsbDevice> enumerate_libusb0(
    bool probe_serial_descriptors = true,
    bool use_identity_cache = true);
[[nodiscard]] std::optional<AppleUsbDevice> find_libusb0_device(
    std::string_view serial);
[[nodiscard]] std::optional<AppleUsbDevice> find_libusb0_device(
    const AppleUsbIdentity& identity, bool require_quicktime = false);
// A freshly activated legacy filter may need one SET_CONFIGURATION to build
// its pipe table before the appended QuickTime interface can be claimed. It
// must never be repeated for a node that was already in QuickTime mode.
[[nodiscard]] bool can_initialize_libusb0_quicktime_configuration(
    AppleUsbMatchKind match_kind, const AppleUsbDevice& selected,
    const UsbEndpointSet& endpoints) noexcept;
[[nodiscard]] bool is_libusb0_quicktime_configuration_active(
    const AppleUsbDevice& device) noexcept;
[[nodiscard]] bool is_libusb0_invalid_configuration_claim(
    int result, std::string_view detail) noexcept;
// Parse one raw USB configuration descriptor without touching a device. This
// is shared with tests for the single-handle post-activation readiness path.
[[nodiscard]] UsbEndpointSet parse_libusb0_quicktime_configuration(
    std::span<const std::uint8_t> descriptor,
    std::uint8_t expected_configuration) noexcept;

struct LibUsb0QuickTimeOpenOptions {
    bool allow_conventional_fallback{};
    // Authorize one SET_CONFIGURATION only after this session sent the
    // normal-to-QuickTime activation request. Existing QuickTime nodes use a
    // claim-only path so a restart cannot reconfigure a live Apple filter.
    bool allow_configuration_initialization{};
    // After an exact libusb0 device-interface arrival, AppleUsbFilter can make
    // the handle visible before its new descriptors are committed. Keep one
    // verified handle and wait on read-only descriptor evidence; never reopen.
    bool wait_for_activated_descriptor{};
    std::stop_token stop_token{};
};

class LibUsb0Connection {
public:
    LibUsb0Connection() = default;
    ~LibUsb0Connection();
    LibUsb0Connection(const LibUsb0Connection&) = delete;
    LibUsb0Connection& operator=(const LibUsb0Connection&) = delete;
    LibUsb0Connection(LibUsb0Connection&& other) noexcept;
    LibUsb0Connection& operator=(LibUsb0Connection&& other) noexcept;

    [[nodiscard]] static bool enable_quicktime_configuration(const std::string& serial);
    [[nodiscard]] static bool enable_quicktime_configuration(const AppleUsbIdentity& identity);
    [[nodiscard]] static bool disable_quicktime_configuration(const std::string& serial);
    [[nodiscard]] static bool disable_quicktime_configuration(const AppleUsbIdentity& identity);
    [[nodiscard]] static LibUsb0Connection open_quicktime(const std::string& serial);
    [[nodiscard]] static LibUsb0Connection open_quicktime(
        const AppleUsbIdentity& identity,
        LibUsb0QuickTimeOpenOptions options = {});
    [[nodiscard]] std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms);
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms);
    void clear_halt();
    void recover_handshake();
    [[nodiscard]] bool request_normal_configuration();
    void cancel_pending_io() noexcept;
    void clear_io_cancellation() noexcept;
    void close() noexcept;
    [[nodiscard]] const std::string& active_topology_id() const noexcept {
        return active_topology_;
    }

private:
    void remember_active_identity(const AppleUsbIdentity& identity) noexcept;
    void close_unlocked() noexcept;

    usb_dev_handle* handle_{};
    UsbEndpointSet endpoints_{};
    bool claimed_{};
    bool active_identity_retained_{};
    std::string active_topology_;
    std::string active_serial_;
};

} // namespace iPhoneMirror::transport
