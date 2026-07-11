#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace iPhoneMirror::quicktime {

constexpr std::uint32_t fourcc(char a, char b, char c, char d) noexcept {
    // Apple defines FourCC constants in display order (e.g. 'asyn' is
    // 0x6173796e) and this USB protocol serializes that integer little-endian.
    // Consequently the bytes on the wire are reversed: 6e 79 73 61.
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(a)) << 24U) |
        (static_cast<std::uint32_t>(static_cast<unsigned char>(b)) << 16U) |
        (static_cast<std::uint32_t>(static_cast<unsigned char>(c)) << 8U) |
        static_cast<std::uint32_t>(static_cast<unsigned char>(d));
}

enum class PacketKind { Ping, Sync, Async, Reply, Unknown };

struct Packet {
    PacketKind kind{PacketKind::Unknown};
    std::uint32_t magic{};
    std::uint32_t subtype{};
    std::uint64_t clock_ref{};
    std::vector<std::uint8_t> payload; // Length prefix is excluded.

    [[nodiscard]] bool is_media_sample() const noexcept;
    [[nodiscard]] bool is_video_sample() const noexcept;
    [[nodiscard]] bool is_audio_sample() const noexcept;
};

class StreamDecoder {
public:
    static constexpr std::uint32_t MaxPacketBytes = 64U * 1024U * 1024U;

    [[nodiscard]] std::vector<Packet> push(std::span<const std::uint8_t> bytes);
    void reset() noexcept;
    [[nodiscard]] std::size_t buffered_bytes() const noexcept { return buffer_.size(); }

private:
    std::vector<std::uint8_t> buffer_;
};

[[nodiscard]] Packet parse_payload(std::span<const std::uint8_t> payload);
[[nodiscard]] std::vector<std::uint8_t> make_ping();
[[nodiscard]] std::vector<std::uint8_t> make_need(std::uint64_t device_clock_ref);
[[nodiscard]] std::string fourcc_string(std::uint32_t value);

} // namespace iPhoneMirror::quicktime
