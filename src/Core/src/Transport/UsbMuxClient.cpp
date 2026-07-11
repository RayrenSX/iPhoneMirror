#include "Transport/UsbMuxClient.h"

#include <WinSock2.h>

#include <array>
#include <stdexcept>
#include <utility>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint32_t PlistMessage = 8;
constexpr std::uint32_t ProtocolVersion = 1;
constexpr std::uint32_t MaxMuxPacket = 16U * 1024U * 1024U;

std::uint32_t u32le(const std::uint8_t* bytes) noexcept {
    return static_cast<std::uint32_t>(bytes[0]) |
        (static_cast<std::uint32_t>(bytes[1]) << 8U) |
        (static_cast<std::uint32_t>(bytes[2]) << 16U) |
        (static_cast<std::uint32_t>(bytes[3]) << 24U);
}

void append_u32le(std::vector<std::uint8_t>& bytes, std::uint32_t value) {
    for (int shift = 0; shift < 32; shift += 8) bytes.push_back(static_cast<std::uint8_t>(value >> shift));
}

std::string child_string(const plist::Value& dictionary, std::string_view key) {
    const auto* value = dictionary.find(key);
    return value ? value->string_or() : std::string{};
}

std::uint32_t child_u32(const plist::Value& dictionary, std::string_view key) {
    const auto* value = dictionary.find(key);
    return value ? static_cast<std::uint32_t>(value->integer_or()) : 0;
}

} // namespace

plist::Value UsbMuxClient::base_message(std::string message_type) const {
    return plist::Value::Dict({
        {"BundleID", plist::Value::String("com.openai.iphonemirror")},
        {"ClientVersionString", plist::Value::String("iPhoneMirror 0.3.0")},
        {"MessageType", plist::Value::String(std::move(message_type))},
        {"ProgName", plist::Value::String("iPhoneMirror")},
        {"kLibUSBMuxVersion", plist::Value::Integer(3)},
    });
}

std::pair<Socket, plist::Value> UsbMuxClient::request_with_socket(const plist::Value& body) {
    Socket socket = Socket::connect_loopback(port_);
    const std::string xml = plist::to_xml(body);
    std::vector<std::uint8_t> packet;
    packet.reserve(16 + xml.size());
    append_u32le(packet, static_cast<std::uint32_t>(16 + xml.size()));
    append_u32le(packet, ProtocolVersion);
    append_u32le(packet, PlistMessage);
    append_u32le(packet, next_tag_++);
    packet.insert(packet.end(), xml.begin(), xml.end());
    socket.send_all(packet);

    const auto header = socket.receive_exact(16);
    const auto length = u32le(header.data());
    if (length < 16 || length > MaxMuxPacket) throw std::runtime_error("invalid usbmux response length");
    if (u32le(header.data() + 8) != PlistMessage) throw std::runtime_error("usbmux response is not plist protocol");
    const auto payload = socket.receive_exact(length - 16);
    const std::string xml_response(payload.begin(), payload.end());
    return {std::move(socket), plist::parse_xml(xml_response)};
}

plist::Value UsbMuxClient::request(const plist::Value& body) {
    auto [socket, result] = request_with_socket(body);
    return result;
}

std::vector<MuxDevice> UsbMuxClient::list_devices() {
    const auto response = request(base_message("ListDevices"));
    const auto* list = response.find("DeviceList");
    if (!list || list->type != plist::Type::Array) throw std::runtime_error("usbmux ListDevices response has no DeviceList");

    std::vector<MuxDevice> devices;
    for (const auto& entry : list->array) {
        if (entry.type != plist::Type::Dictionary) continue;
        const auto* properties = entry.find("Properties");
        if (!properties || properties->type != plist::Type::Dictionary) continue;
        MuxDevice device;
        device.device_id = child_u32(entry, "DeviceID");
        device.serial = child_string(*properties, "SerialNumber");
        device.connection_type = child_string(*properties, "ConnectionType");
        device.product_id = child_u32(*properties, "ProductID");
        device.location_id = child_u32(*properties, "LocationID");
        if (!device.serial.empty()) devices.push_back(std::move(device));
    }
    return devices;
}

bool UsbMuxClient::has_pair_record(const std::string& udid) {
    auto message = base_message("ReadPairRecord");
    message.dictionary.emplace("PairRecordID", plist::Value::String(udid));
    const auto response = request(message);
    const auto* data = response.find("PairRecordData");
    return data && data->type == plist::Type::Data && !data->string.empty();
}

Socket UsbMuxClient::connect_device(std::uint32_t device_id, std::uint16_t device_port) {
    auto message = base_message("Connect");
    message.dictionary.emplace("DeviceID", plist::Value::Integer(device_id));
    message.dictionary.emplace("PortNumber", plist::Value::Integer(htons(device_port)));
    auto [socket, response] = request_with_socket(message);
    const auto* number = response.find("Number");
    if (!number || number->integer_or(-1) != 0) throw std::runtime_error("usbmux could not open device tunnel");
    socket.set_timeout(1500);
    return std::move(socket);
}

} // namespace iPhoneMirror::transport
