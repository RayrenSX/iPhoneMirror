#include "Media/CoreMedia.h"
#include "Protocol/QuickTimePacket.h"

#include <bit>
#include <cmath>
#include <cstring>
#include <stdexcept>

namespace iPhoneMirror::coremedia {
namespace {

std::uint32_t u32le(const std::uint8_t* p) noexcept {
    return static_cast<std::uint32_t>(p[0]) |
        (static_cast<std::uint32_t>(p[1]) << 8U) |
        (static_cast<std::uint32_t>(p[2]) << 16U) |
        (static_cast<std::uint32_t>(p[3]) << 24U);
}

std::uint64_t u64le(const std::uint8_t* p) noexcept {
    return static_cast<std::uint64_t>(u32le(p)) | (static_cast<std::uint64_t>(u32le(p + 4)) << 32U);
}

std::uint16_t u16be(const std::uint8_t* p) noexcept {
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(p[0]) << 8U) | p[1]);
}

struct Chunk {
    std::uint32_t magic{};
    std::span<const std::uint8_t> payload;
};

Chunk read_chunk(std::span<const std::uint8_t> bytes, std::size_t& offset) {
    if (bytes.size() - offset < 8) throw std::runtime_error("truncated CoreMedia chunk header");
    const auto length = u32le(bytes.data() + offset);
    if (length < 8 || length > bytes.size() - offset) throw std::runtime_error("invalid CoreMedia chunk length");
    Chunk result{
        .magic = u32le(bytes.data() + offset + 4),
        .payload = bytes.subspan(offset + 8, length - 8),
    };
    offset += length;
    return result;
}

void parse_avcc(FormatDescription& format) {
    const auto marker = quicktime::fourcc('d', 'a', 't', 'v');
    for (std::size_t index = 0; index + 4 <= format.extensions.size(); ++index) {
        if (u32le(format.extensions.data() + index) != marker) continue;
        const auto config = std::span(format.extensions).subspan(index + 4);
        if (config.size() < 7 || config[0] != 1) continue;

        std::size_t offset = 4;
        format.nalu_length_size = static_cast<std::uint8_t>((config[offset++] & 0x03U) + 1U);
        const auto sps_count = config[offset++] & 0x1fU;
        std::vector<std::vector<std::uint8_t>> sps;
        bool valid = true;
        for (std::uint8_t item = 0; item < sps_count; ++item) {
            if (config.size() - offset < 2) { valid = false; break; }
            const auto length = u16be(config.data() + offset);
            offset += 2;
            if (length == 0 || length > config.size() - offset) { valid = false; break; }
            sps.emplace_back(config.begin() + static_cast<std::ptrdiff_t>(offset),
                config.begin() + static_cast<std::ptrdiff_t>(offset + length));
            offset += length;
        }
        if (!valid || config.size() - offset < 1) continue;
        const auto pps_count = config[offset++];
        std::vector<std::vector<std::uint8_t>> pps;
        for (std::uint8_t item = 0; item < pps_count; ++item) {
            if (config.size() - offset < 2) { valid = false; break; }
            const auto length = u16be(config.data() + offset);
            offset += 2;
            if (length == 0 || length > config.size() - offset) { valid = false; break; }
            pps.emplace_back(config.begin() + static_cast<std::ptrdiff_t>(offset),
                config.begin() + static_cast<std::ptrdiff_t>(offset + length));
            offset += length;
        }
        if (!valid || sps.empty() || pps.empty()) continue;
        format.sequence_parameter_sets = std::move(sps);
        format.picture_parameter_sets = std::move(pps);
        format.decoder_configuration_record.assign(config.begin(), config.begin() + static_cast<std::ptrdiff_t>(offset));
        return;
    }
}

FormatDescription parse_format_description(std::span<const std::uint8_t> bytes) {
    FormatDescription format;
    std::size_t offset{};
    const auto media = read_chunk(bytes, offset);
    if (media.magic != quicktime::fourcc('m', 'd', 'i', 'a') || media.payload.size() != 4) {
        throw std::runtime_error("invalid CoreMedia media-type chunk");
    }
    format.media_type = u32le(media.payload.data());

    if (format.is_audio()) {
        const auto asbd = read_chunk(bytes, offset);
        if (asbd.magic != quicktime::fourcc('a', 's', 'b', 'd')) throw std::runtime_error("missing asbd audio format");
        format.audio = parse_audio_format(asbd.payload);
    } else if (format.is_video()) {
        const auto dimensions = read_chunk(bytes, offset);
        if (dimensions.magic != quicktime::fourcc('v', 'd', 'i', 'm') || dimensions.payload.size() < 8) {
            throw std::runtime_error("missing video dimensions");
        }
        format.width = u32le(dimensions.payload.data());
        format.height = u32le(dimensions.payload.data() + 4);

        const auto codec = read_chunk(bytes, offset);
        if (codec.magic != quicktime::fourcc('c', 'o', 'd', 'c') || codec.payload.size() != 4) {
            throw std::runtime_error("missing video codec");
        }
        format.codec = u32le(codec.payload.data());

        const auto extensions = read_chunk(bytes, offset);
        if (extensions.magic != quicktime::fourcc('e', 'x', 't', 'n')) throw std::runtime_error("missing video extensions");
        format.extensions.assign(extensions.payload.begin(), extensions.payload.end());
        parse_avcc(format);
    } else {
        throw std::runtime_error("unsupported CoreMedia media type");
    }
    if (offset != bytes.size()) throw std::runtime_error("trailing bytes in CoreMedia format description");
    return format;
}

} // namespace

double CMTime::seconds() const noexcept {
    return timescale == 0 ? 0.0 : static_cast<double>(value) / static_cast<double>(timescale);
}

bool CMTime::valid() const noexcept { return timescale != 0 && (flags & 1U) != 0; }

CMTime parse_time(std::span<const std::uint8_t> bytes) {
    if (bytes.size() < 24) throw std::runtime_error("CMTime requires 24 bytes");
    CMTime time;
    time.value = static_cast<std::int64_t>(u64le(bytes.data()));
    time.timescale = static_cast<std::int32_t>(u32le(bytes.data() + 8));
    time.flags = u32le(bytes.data() + 12);
    time.epoch = static_cast<std::int64_t>(u64le(bytes.data() + 16));
    return time;
}

AudioStreamBasicDescription parse_audio_format(std::span<const std::uint8_t> bytes) {
    if (bytes.size() < 40) throw std::runtime_error("AudioStreamBasicDescription requires 40 bytes");
    AudioStreamBasicDescription result;
    const auto rate_bits = u64le(bytes.data());
    result.sample_rate = std::bit_cast<double>(rate_bits);
    if (!std::isfinite(result.sample_rate) || result.sample_rate <= 0) {
        throw std::runtime_error("invalid CoreAudio sample rate");
    }
    result.format_id = u32le(bytes.data() + 8);
    result.format_flags = u32le(bytes.data() + 12);
    result.bytes_per_packet = u32le(bytes.data() + 16);
    result.frames_per_packet = u32le(bytes.data() + 20);
    result.bytes_per_frame = u32le(bytes.data() + 24);
    result.channels_per_frame = u32le(bytes.data() + 28);
    result.bits_per_channel = u32le(bytes.data() + 32);
    result.reserved = u32le(bytes.data() + 36);
    return result;
}

std::string AudioStreamBasicDescription::format_name() const {
    // CoreAudio format IDs are conceptually big-endian FourCC values but are
    // serialized as native little-endian integers in this protocol.
    std::string result(4, '\0');
    result[0] = static_cast<char>(format_id >> 24U);
    result[1] = static_cast<char>(format_id >> 16U);
    result[2] = static_cast<char>(format_id >> 8U);
    result[3] = static_cast<char>(format_id);
    return result;
}

SampleBufferEnvelope parse_sample_envelope(std::span<const std::uint8_t> payload) {
    if (payload.size() < 24) throw std::runtime_error("media packet is shorter than FEED/EAT envelope");
    const auto magic = u32le(payload.data());
    const auto subtype = u32le(payload.data() + 12);
    if (magic != quicktime::fourcc('a', 's', 'y', 'n')) throw std::runtime_error("media packet is not ASYN");
    const bool video = subtype == quicktime::fourcc('f', 'e', 'e', 'd');
    const bool audio = subtype == quicktime::fourcc('e', 'a', 't', '!');
    if (!video && !audio) throw std::runtime_error("ASYN packet is not FEED or EAT!");

    const std::uint32_t sample_length = u32le(payload.data() + 16);
    if (sample_length < 8 || 16ULL + sample_length > payload.size()) {
        throw std::runtime_error("invalid serialized CMSampleBuffer length");
    }
    if (u32le(payload.data() + 20) != quicktime::fourcc('s', 'b', 'u', 'f')) {
        throw std::runtime_error("missing sbuf magic in CMSampleBuffer envelope");
    }
    return SampleBufferEnvelope{
        .video = video,
        .clock_ref = u64le(payload.data() + 4),
        .serialized_sample_buffer = payload.subspan(20, sample_length - 4),
    };
}

bool FormatDescription::is_video() const noexcept { return media_type == quicktime::fourcc('v', 'i', 'd', 'e'); }
bool FormatDescription::is_audio() const noexcept { return media_type == quicktime::fourcc('s', 'o', 'u', 'n'); }

SampleBuffer parse_sample_buffer(std::span<const std::uint8_t> serialized) {
    if (serialized.size() < 4 || u32le(serialized.data()) != quicktime::fourcc('s', 'b', 'u', 'f')) {
        throw std::runtime_error("serialized CMSampleBuffer does not begin with sbuf");
    }
    SampleBuffer result;
    std::size_t offset = 4;
    while (offset < serialized.size()) {
        const auto chunk = read_chunk(serialized, offset);
        if (chunk.magic == quicktime::fourcc('o', 'p', 't', 's')) {
            result.output_presentation_timestamp = parse_time(chunk.payload);
        } else if (chunk.magic == quicktime::fourcc('s', 't', 'i', 'a')) {
            if (chunk.payload.size() % 72 != 0) throw std::runtime_error("invalid sample timing array length");
            for (std::size_t timing_offset = 0; timing_offset < chunk.payload.size(); timing_offset += 72) {
                result.timing.push_back({
                    .duration = parse_time(chunk.payload.subspan(timing_offset, 24)),
                    .presentation_timestamp = parse_time(chunk.payload.subspan(timing_offset + 24, 24)),
                    .decode_timestamp = parse_time(chunk.payload.subspan(timing_offset + 48, 24)),
                });
            }
        } else if (chunk.magic == quicktime::fourcc('s', 'd', 'a', 't')) {
            result.sample_data.assign(chunk.payload.begin(), chunk.payload.end());
        } else if (chunk.magic == quicktime::fourcc('n', 's', 'm', 'p')) {
            if (chunk.payload.size() != 4) throw std::runtime_error("invalid sample-count chunk");
            result.sample_count = u32le(chunk.payload.data());
        } else if (chunk.magic == quicktime::fourcc('s', 's', 'i', 'z')) {
            if (chunk.payload.size() % 4 != 0) throw std::runtime_error("invalid sample-size array");
            for (std::size_t item = 0; item < chunk.payload.size(); item += 4) {
                result.sample_sizes.push_back(u32le(chunk.payload.data() + item));
            }
        } else if (chunk.magic == quicktime::fourcc('f', 'd', 's', 'c')) {
            result.format = parse_format_description(chunk.payload);
        } else if (chunk.magic == quicktime::fourcc('s', 'a', 't', 't') ||
                   chunk.magic == quicktime::fourcc('s', 'a', 'r', 'y')) {
            // Attachments affect frame metadata but not the encoded payload.
            // They are validated by read_chunk and intentionally skipped here.
        } else {
            throw std::runtime_error("unknown CMSampleBuffer chunk: " + quicktime::fourcc_string(chunk.magic));
        }
    }
    // Apple uses a single entry as a uniform size for every sample (audio),
    // and one entry per sample when sizes vary.
    if (result.sample_count != 0 && !result.sample_sizes.empty() &&
        result.sample_sizes.size() != 1 && result.sample_sizes.size() != result.sample_count) {
        throw std::runtime_error("CMSampleBuffer sample count does not match size array");
    }
    if (result.sample_count > 1 && result.sample_sizes.size() == 1 && !result.sample_data.empty() &&
        static_cast<std::uint64_t>(result.sample_count) * result.sample_sizes.front() != result.sample_data.size()) {
        throw std::runtime_error("CMSampleBuffer uniform sample size does not match payload");
    }
    return result;
}

} // namespace iPhoneMirror::coremedia
