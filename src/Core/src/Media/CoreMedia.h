#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

namespace iPhoneMirror::coremedia {

struct CMTime {
    std::int64_t value{};
    std::int32_t timescale{};
    std::uint32_t flags{};
    std::int64_t epoch{};

    [[nodiscard]] double seconds() const noexcept;
    [[nodiscard]] bool valid() const noexcept;
};

struct AudioStreamBasicDescription {
    double sample_rate{};
    std::uint32_t format_id{};
    std::uint32_t format_flags{};
    std::uint32_t bytes_per_packet{};
    std::uint32_t frames_per_packet{};
    std::uint32_t bytes_per_frame{};
    std::uint32_t channels_per_frame{};
    std::uint32_t bits_per_channel{};
    std::uint32_t reserved{};

    [[nodiscard]] std::string format_name() const;
};

[[nodiscard]] CMTime parse_time(std::span<const std::uint8_t> bytes);
[[nodiscard]] AudioStreamBasicDescription parse_audio_format(std::span<const std::uint8_t> bytes);

// FEED/EAT envelopes contain a length-prefixed `sbuf` object after the subtype.
// This view validates that outer envelope without guessing the inner object layout.
struct SampleBufferEnvelope {
    bool video{};
    std::uint64_t clock_ref{};
    std::span<const std::uint8_t> serialized_sample_buffer;
};

[[nodiscard]] SampleBufferEnvelope parse_sample_envelope(std::span<const std::uint8_t> quicktime_payload);

struct SampleTimingInfo {
    CMTime duration;
    CMTime presentation_timestamp;
    CMTime decode_timestamp;
};

struct FormatDescription {
    std::uint32_t media_type{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t codec{};
    std::optional<AudioStreamBasicDescription> audio;
    std::vector<std::uint8_t> extensions;
    std::vector<std::uint8_t> decoder_configuration_record;
    std::vector<std::vector<std::uint8_t>> sequence_parameter_sets;
    std::vector<std::vector<std::uint8_t>> picture_parameter_sets;
    std::uint8_t nalu_length_size{4};

    [[nodiscard]] bool is_video() const noexcept;
    [[nodiscard]] bool is_audio() const noexcept;
};

struct SampleBuffer {
    std::optional<CMTime> output_presentation_timestamp;
    std::optional<FormatDescription> format;
    std::uint32_t sample_count{};
    std::vector<SampleTimingInfo> timing;
    std::vector<std::uint8_t> sample_data;
    std::vector<std::uint32_t> sample_sizes;
};

// Input begins with the `sbuf` magic returned by parse_sample_envelope.
// Every nested length is checked before any field is read.
[[nodiscard]] SampleBuffer parse_sample_buffer(std::span<const std::uint8_t> serialized);

} // namespace iPhoneMirror::coremedia
