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
    [[nodiscard]] std::optional<std::int64_t> to_100ns() const noexcept;
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

enum class VideoCodec : std::uint8_t {
    Unknown,
    H264,
    Hevc,
};

enum class ColorPrimaries : std::uint8_t {
    Unspecified,
    Bt709,
    Bt2020,
    DisplayP3,
};

enum class TransferFunction : std::uint8_t {
    Unspecified,
    Bt709,
    Srgb,
    Pq,
    Hlg,
};

enum class MatrixCoefficients : std::uint8_t {
    Unspecified,
    Bt601,
    Bt709,
    Bt2020,
};

enum class ColorRange : std::uint8_t {
    Unspecified,
    Limited,
    Full,
};

struct HdrStaticMetadata {
    std::uint32_t max_content_light_level{};
    std::uint32_t max_frame_average_light_level{};
    std::uint32_t max_mastering_luminance{};
    // 0.0001-nit units, matching CTA-861.3 and DXGI HDR10 metadata.
    std::uint32_t min_mastering_luminance{};
};

struct VideoColorDescription {
    ColorPrimaries primaries{ColorPrimaries::Unspecified};
    TransferFunction transfer{TransferFunction::Unspecified};
    MatrixCoefficients matrix{MatrixCoefficients::Unspecified};
    ColorRange range{ColorRange::Unspecified};
    HdrStaticMetadata hdr;

    [[nodiscard]] bool is_hdr() const noexcept {
        return transfer == TransferFunction::Pq || transfer == TransferFunction::Hlg;
    }
};

struct FormatDescription {
    std::uint32_t media_type{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t codec{};
    std::optional<AudioStreamBasicDescription> audio;
    std::vector<std::uint8_t> extensions;
    std::vector<std::uint8_t> decoder_configuration_record;
    std::vector<std::vector<std::uint8_t>> video_parameter_sets;
    std::vector<std::vector<std::uint8_t>> sequence_parameter_sets;
    std::vector<std::vector<std::uint8_t>> picture_parameter_sets;
    std::uint8_t nalu_length_size{4};
    std::uint8_t chroma_format{1};
    std::uint8_t bit_depth_luma{8};
    std::uint8_t bit_depth_chroma{8};
    VideoColorDescription color;

    [[nodiscard]] bool is_video() const noexcept;
    [[nodiscard]] bool is_audio() const noexcept;
    [[nodiscard]] VideoCodec video_codec() const noexcept;
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
