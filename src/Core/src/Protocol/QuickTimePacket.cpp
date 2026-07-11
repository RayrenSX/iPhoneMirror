#include "Protocol/QuickTimePacket.h"

#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace iPhoneMirror::quicktime {
namespace {

std::uint32_t read_u32_le(const std::uint8_t* data) noexcept {
    return static_cast<std::uint32_t>(data[0]) |
        (static_cast<std::uint32_t>(data[1]) << 8U) |
        (static_cast<std::uint32_t>(data[2]) << 16U) |
        (static_cast<std::uint32_t>(data[3]) << 24U);
}

std::uint64_t read_u64_le(const std::uint8_t* data) noexcept {
    return static_cast<std::uint64_t>(read_u32_le(data)) |
        (static_cast<std::uint64_t>(read_u32_le(data + 4)) << 32U);
}

void append_u32_le(std::vector<std::uint8_t>& bytes, std::uint32_t value) {
    for (int shift = 0; shift < 32; shift += 8) bytes.push_back(static_cast<std::uint8_t>(value >> shift));
}

void append_u64_le(std::vector<std::uint8_t>& bytes, std::uint64_t value) {
    append_u32_le(bytes, static_cast<std::uint32_t>(value));
    append_u32_le(bytes, static_cast<std::uint32_t>(value >> 32U));
}

PacketKind classify(std::uint32_t magic) noexcept {
    if (magic == fourcc('p', 'i', 'n', 'g')) return PacketKind::Ping;
    if (magic == fourcc('s', 'y', 'n', 'c')) return PacketKind::Sync;
    if (magic == fourcc('a', 's', 'y', 'n')) return PacketKind::Async;
    if (magic == fourcc('r', 'p', 'l', 'y')) return PacketKind::Reply;
    return PacketKind::Unknown;
}

} // namespace

bool Packet::is_media_sample() const noexcept { return is_video_sample() || is_audio_sample(); }
bool Packet::is_video_sample() const noexcept { return kind == PacketKind::Async && subtype == fourcc('f', 'e', 'e', 'd'); }
bool Packet::is_audio_sample() const noexcept { return kind == PacketKind::Async && subtype == fourcc('e', 'a', 't', '!'); }

Packet parse_payload(std::span<const std::uint8_t> payload) {
    if (payload.size() < 4) throw std::runtime_error("QuickTime packet payload is shorter than its magic");
    Packet packet;
    packet.magic = read_u32_le(payload.data());
    packet.kind = classify(packet.magic);
    packet.payload.assign(payload.begin(), payload.end());

    if ((packet.kind == PacketKind::Sync || packet.kind == PacketKind::Async) && payload.size() >= 16) {
        packet.clock_ref = read_u64_le(payload.data() + 4);
        packet.subtype = read_u32_le(payload.data() + 12);
    } else if (packet.kind == PacketKind::Reply && payload.size() >= 12) {
        packet.clock_ref = read_u64_le(payload.data() + 4); // Correlation id for RPLY.
    }
    return packet;
}

std::vector<Packet> StreamDecoder::push(std::span<const std::uint8_t> bytes) {
    if (!bytes.empty()) buffer_.insert(buffer_.end(), bytes.begin(), bytes.end());
    std::vector<Packet> packets;
    std::size_t consumed{};

    while (buffer_.size() - consumed >= 4) {
        const std::uint32_t packet_length = read_u32_le(buffer_.data() + consumed);
        if (packet_length < 8 || packet_length > MaxPacketBytes) {
            reset();
            throw std::runtime_error("invalid QuickTime USB packet length");
        }
        if (buffer_.size() - consumed < packet_length) break;
        const auto begin = buffer_.data() + consumed + 4;
        packets.push_back(parse_payload(std::span(begin, packet_length - 4)));
        consumed += packet_length;
    }

    if (consumed != 0) buffer_.erase(buffer_.begin(), buffer_.begin() + static_cast<std::ptrdiff_t>(consumed));
    return packets;
}

void StreamDecoder::reset() noexcept { buffer_.clear(); }

std::vector<std::uint8_t> make_ping() {
    std::vector<std::uint8_t> packet;
    packet.reserve(16);
    append_u32_le(packet, 16);
    append_u32_le(packet, fourcc('p', 'i', 'n', 'g'));
    append_u32_le(packet, 0);
    append_u32_le(packet, 1);
    return packet;
}

std::vector<std::uint8_t> make_need(std::uint64_t device_clock_ref) {
    std::vector<std::uint8_t> packet;
    packet.reserve(20);
    append_u32_le(packet, 20);
    append_u32_le(packet, fourcc('a', 's', 'y', 'n'));
    append_u64_le(packet, device_clock_ref);
    append_u32_le(packet, fourcc('n', 'e', 'e', 'd'));
    return packet;
}

std::string fourcc_string(std::uint32_t value) {
    std::string result(4, '\0');
    for (int index = 0; index < 4; ++index) result[index] = static_cast<char>(value >> ((3 - index) * 8));
    return result;
}

} // namespace iPhoneMirror::quicktime
