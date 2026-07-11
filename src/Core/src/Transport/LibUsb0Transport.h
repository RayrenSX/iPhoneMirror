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

[[nodiscard]] bool libusb0_available() noexcept;
[[nodiscard]] std::vector<AppleUsbDevice> enumerate_libusb0();
[[nodiscard]] std::optional<AppleUsbDevice> find_libusb0_device(
    std::string_view serial);

class LibUsb0Connection {
public:
    LibUsb0Connection() = default;
    ~LibUsb0Connection();
    LibUsb0Connection(const LibUsb0Connection&) = delete;
    LibUsb0Connection& operator=(const LibUsb0Connection&) = delete;
    LibUsb0Connection(LibUsb0Connection&& other) noexcept;
    LibUsb0Connection& operator=(LibUsb0Connection&& other) noexcept;

    [[nodiscard]] static bool enable_quicktime_configuration(const std::string& serial);
    [[nodiscard]] static bool disable_quicktime_configuration(const std::string& serial);
    [[nodiscard]] static LibUsb0Connection open_quicktime(const std::string& serial);
    [[nodiscard]] std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms);
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms);
    void clear_halt();
    void recover_handshake();
    void disable_quicktime_configuration();
    void close() noexcept;

private:
    usb_dev_handle* handle_{};
    UsbEndpointSet endpoints_{};
    bool claimed_{};
};

} // namespace iPhoneMirror::transport
