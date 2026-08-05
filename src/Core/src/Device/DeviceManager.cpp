#include "Device/DeviceManager.h"

#include "Device/AppleUsbDiscovery.h"
#include "Protocol/Plist.h"
#include "Transport/Socket.h"
#include "Transport/QtUsbTransport.h"
#include "Transport/LibUsb0Transport.h"
#include "Transport/UsbMuxClient.h"
#include "Logging.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <map>
#include <span>
#include <stdexcept>

namespace iPhoneMirror::device {
namespace {

std::wstring widen(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
        static_cast<int>(utf8.size()), nullptr, 0);
    if (size <= 0) return L"<invalid UTF-8>";
    std::wstring result(static_cast<std::size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
        static_cast<int>(utf8.size()), result.data(), size);
    return result;
}

void append_u32be(std::vector<std::uint8_t>& bytes, std::uint32_t value) {
    bytes.push_back(static_cast<std::uint8_t>(value >> 24U));
    bytes.push_back(static_cast<std::uint8_t>(value >> 16U));
    bytes.push_back(static_cast<std::uint8_t>(value >> 8U));
    bytes.push_back(static_cast<std::uint8_t>(value));
}

std::uint32_t u32be(const std::uint8_t* bytes) noexcept {
    return (static_cast<std::uint32_t>(bytes[0]) << 24U) |
        (static_cast<std::uint32_t>(bytes[1]) << 16U) |
        (static_cast<std::uint32_t>(bytes[2]) << 8U) |
        static_cast<std::uint32_t>(bytes[3]);
}

plist::Value lockdown_exchange(transport::Socket& socket, const plist::Value& request) {
    const std::string xml = plist::to_xml(request);
    std::vector<std::uint8_t> frame;
    frame.reserve(4 + xml.size());
    append_u32be(frame, static_cast<std::uint32_t>(xml.size()));
    frame.insert(frame.end(), xml.begin(), xml.end());
    socket.send_all(frame);
    const auto header = socket.receive_exact(4);
    const auto length = u32be(header.data());
    if (length == 0 || length > 8U * 1024U * 1024U) throw std::runtime_error("invalid lockdownd plist length");
    const auto bytes = socket.receive_exact(length);
    return plist::parse_xml(std::string_view(reinterpret_cast<const char*>(bytes.data()), bytes.size()));
}

void enrich_from_lockdown(transport::Socket& socket, DeviceRecord& record) {
    const auto response = lockdown_exchange(socket, plist::Value::Dict({
        {"Label", plist::Value::String("iPhoneMirror")},
        {"Request", plist::Value::String("GetValue")},
    }));
    if (const auto* error = response.find("Error")) {
        record.status = L"设备已连接，但 Lockdown 拒绝访问：" + widen(error->string_or());
        return;
    }
    const auto* values = response.find("Value");
    if (!values || values->type != plist::Type::Dictionary) return;

    record.lockdown_accessible = true;
    if (const auto* value = values->find("DeviceName")) record.name = widen(value->string_or());
    if (const auto* value = values->find("ProductType")) record.product_type = widen(value->string_or());
    if (const auto* value = values->find("ProductVersion")) record.os_version = widen(value->string_or());
    if (record.name.empty()) record.name = L"iPhone";
}

void add_devices_from_port(std::uint16_t port, std::map<std::string, DeviceRecord, std::less<>>& devices) {
    transport::UsbMuxClient mux(port);
    for (const auto& mux_device : mux.list_devices()) {
        if (!mux_device.connection_type.empty() &&
            _stricmp(mux_device.connection_type.c_str(), "USB") != 0)
            continue;
        // ListDevices can briefly retain a device after unplug/re-enumeration.
        // Require a fresh tunnel before exposing it to the UI; a stale usbmux
        // row cannot complete this Connect request.
        transport::Socket lockdown;
        try {
            lockdown = mux.connect_device(mux_device.device_id, 62078);
        } catch (...) {
            continue;
        }

        DeviceRecord record;
        record.device_id = mux_device.device_id;
        record.mux_port = port;
        record.usb_connected = true;
        record.state = ConnectionState::Connected;
        record.udid = widen(mux_device.serial);
        record.connection_type = widen(mux_device.connection_type.empty() ? "USB" : mux_device.connection_type);
        record.name = L"iPhone";
        record.status = L"USB 已连接";

        try {
            record.pair_record_present = mux.has_pair_record(mux_device.serial);
            if (record.pair_record_present) {
                record.state = ConnectionState::Paired;
                record.status = L"已配对，正在验证设备会话";
            } else {
                record.status = L"等待在 iPhone 上信任此电脑";
            }
        } catch (...) {
            // Device listing is still useful even if this daemon cannot expose pair records.
        }

        try {
            enrich_from_lockdown(lockdown, record);
            if (record.lockdown_accessible) {
                record.state = ConnectionState::Ready;
                record.status = record.pair_record_present ? L"已连接并已配对" : L"已连接；设备信息可读，配对记录未确认";
            }
        } catch (const std::exception&) {
            if (record.pair_record_present) record.status = L"已配对；请解锁 iPhone 后重试";
        }
        devices.insert_or_assign(mux_device.serial, std::move(record));
    }
}

} // namespace

EnvironmentRecord DeviceManager::environment() const {
    EnvironmentRecord result;
    const auto service = apple_mobile_device_service_state();
    result.service_installed = service.installed;
    result.service_running = service.running;
    result.standard_mux = transport::Socket::probe_loopback(27015);
    result.capture_mux = transport::Socket::probe_loopback(37015);
    result.physical_device_count = static_cast<std::uint32_t>(discover_physical_apple_usb_devices().size());
    const auto usb_runtime = transport::probe_usb_runtime();
    result.libusb_runtime = usb_runtime.runtime_available;
    result.usbdk_backend_known = usb_runtime.usbdk_backend_probed;
    result.usbdk_backend = usb_runtime.usbdk_backend_available;
    result.libusb_apple_devices_known = usb_runtime.apple_device_count_probed;
    result.libusb_apple_devices = usb_runtime.apple_device_count;
    result.libusb_version = widen(usb_runtime.version);
    result.libusb0_available = transport::libusb0_installed();
    // The automatic probe never touches libusb0, so its device count remains
    // deliberately unknown until the explicit capture preflight. Do not copy
    // the libusb-1 count into the legacy backend fields.

    if (result.standard_mux && result.libusb0_apple_devices_known &&
        result.libusb0_apple_devices > 0) {
        result.diagnostic = L"Apple 配对通道与 libusb0 直接采集后端已就绪。";
    } else if (!result.service_installed && !result.capture_mux) {
        result.diagnostic = L"未检测到 Apple Mobile Device Support。请安装 Apple Devices 或 iTunes 驱动；有线采集还需要兼容的 USB 过滤驱动。";
    } else if (!result.service_running && !result.capture_mux) {
        result.diagnostic = L"Apple Devices 已安装，但后台 USB 服务尚未运行。连接并解锁 iPhone 后应自动启动；若未启动请打开 Apple Devices 修复。";
    } else if (result.standard_mux && result.libusb_apple_devices_known &&
        result.libusb_apple_devices > 0) {
        result.diagnostic = L"Apple 配对通道和 libusb 设备枚举可用；开始投屏时将验证隐藏配置访问权限。";
    } else if (result.standard_mux) {
        result.diagnostic = L"Apple 配对通道可用；连接 iPhone 后将检测直接 QuickTime USB 后端。";
    } else if (result.capture_mux) {
        result.diagnostic = L"Windows 采集 usbmuxd 已就绪。";
    } else {
        result.diagnostic = L"Apple USB 服务存在，但 usbmux 端口不可用。";
    }
    if (result.libusb_runtime) {
        result.diagnostic += L" libusb " + result.libusb_version + L" 已加载";
        if (result.usbdk_backend_known) {
            result.diagnostic += result.usbdk_backend
                ? L"，UsbDk 后端可用。"
                : L"，UsbDk 后端不可用。";
        } else {
            result.diagnostic += L"，UsbDk 后端尚未探测（开始投屏时才会访问 USB 后端）。";
        }
    } else {
        result.diagnostic += L" libusb 用户态运行库不可用。";
    }
    if (result.libusb0_available) {
        result.diagnostic += L" libusb0 运行库文件存在；设备枚举将在开始投屏时进行。";
    }
    return result;
}

std::vector<DeviceRecord> DeviceManager::refresh() const {
    std::map<std::string, DeviceRecord, std::less<>> devices;
    if (transport::Socket::probe_loopback(27015)) {
        try { add_devices_from_port(27015, devices); } catch (...) {}
    }
    if (transport::Socket::probe_loopback(37015)) {
        try { add_devices_from_port(37015, devices); } catch (...) {}
    }

    std::vector<DeviceRecord> result;
    result.reserve(devices.size());
    for (auto& [_, device] : devices) result.push_back(std::move(device));
    return result;
}

} // namespace iPhoneMirror::device
