#pragma once

#include <cstddef>
#include <cstdint>

namespace iPhoneMirror::wireless {

inline constexpr std::uint32_t IpcMagic = 0x50414D49U; // IMAP
inline constexpr std::uint16_t IpcVersion = 4;
inline constexpr std::uint32_t MaxPayloadBytes = 64U * 1024U * 1024U;
inline constexpr std::size_t DeviceIdBytes = 64;
inline constexpr std::size_t DeviceNameBytes = 128;
inline constexpr std::size_t ProductTypeBytes = 64;
inline constexpr std::size_t OsVersionBytes = 32;

enum class MessageType : std::uint16_t {
    Ready = 1,
    Connected = 2,
    Disconnected = 3,
    Video = 4,
    Audio = 5,
    Log = 6,
    DeviceInfo = 7,
    MediaPlay = 8,
    MediaStop = 9,
    PlaybackState = 10,
};

#pragma pack(push, 1)
struct MessageHeader {
    std::uint32_t magic{IpcMagic};
    std::uint16_t version{IpcVersion};
    MessageType type{MessageType::Ready};
    std::uint32_t payload_size{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t stride[3]{};
    std::uint32_t plane_size[3]{};
    std::uint32_t sample_rate{};
    std::uint16_t channels{};
    std::uint16_t bits_per_sample{};
    std::uint64_t sequence{};
    std::uint64_t media_command_id{};
    double media_duration{};
    double media_position{};
    double media_rate{};
    double media_volume{};
    std::uint32_t reserved{};
    char device_id[DeviceIdBytes]{};
    char device_name[DeviceNameBytes]{};
    char product_type[ProductTypeBytes]{};
    char os_version[OsVersionBytes]{};
};
#pragma pack(pop)

static_assert(sizeof(MessageHeader) == 392);

} // namespace iPhoneMirror::wireless
