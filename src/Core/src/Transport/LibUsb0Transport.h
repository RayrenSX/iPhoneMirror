#pragma once

#include "Transport/LibUsb0Readiness.h"
#include "Transport/QtUsbTransport.h"

#include <optional>
#include <span>
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
[[nodiscard]] std::vector<AppleUsbDevice> enumerate_libusb0();
[[nodiscard]] std::optional<AppleUsbDevice> find_libusb0_device(
    std::string_view serial);
[[nodiscard]] std::optional<AppleUsbDevice> find_libusb0_device(
    const AppleUsbIdentity& identity, bool require_quicktime = false);

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
        const AppleUsbIdentity& identity, bool allow_conventional_fallback = false);
    [[nodiscard]] std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms);
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms);
    void clear_halt();
    void recover_handshake();
    void close() noexcept;

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
