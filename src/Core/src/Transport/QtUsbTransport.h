#pragma once

#include <libusb.h>

#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace iPhoneMirror::transport {

struct UsbEndpointSet {
    std::uint8_t configuration{};
    std::uint8_t interface_number{};
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
    bool can_open{};
    bool mux_configuration{};
    bool quicktime_configuration{};
    UsbEndpointSet mux_endpoints;
    UsbEndpointSet quicktime_endpoints;
};

struct UsbRuntimeProbe {
    bool runtime_available{};
    bool usbdk_backend_available{};
    std::string version;
    std::uint32_t apple_device_count{};
    std::string error;
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

    [[nodiscard]] static QtUsbConnection open_quicktime(QtUsbContext& context, const std::string& serial);
    [[nodiscard]] static bool enable_quicktime_configuration(QtUsbContext& context, const std::string& serial);

    [[nodiscard]] std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms);
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms);
    void clear_halt();
    void recover_handshake();
    void disable_quicktime_configuration();
    void close() noexcept;
    [[nodiscard]] bool valid() const noexcept { return handle_ != nullptr; }

private:
    libusb_device_handle* handle_{};
    UsbEndpointSet endpoints_{};
    bool claimed_{};
};

[[nodiscard]] UsbRuntimeProbe probe_usb_runtime() noexcept;

} // namespace iPhoneMirror::transport
