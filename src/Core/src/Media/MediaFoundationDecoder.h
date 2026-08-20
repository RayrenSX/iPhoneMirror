#pragma once

#include "Media/CoreMedia.h"

#include <chrono>
#include <cstdint>
#include <memory>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

namespace iPhoneMirror::media {

enum class DecoderPreference : std::uint8_t {
    Auto = 0,
    HardwarePreferred = 1,
    SoftwareCompatible = 2,
};

enum class ColorOutputPreference : std::uint8_t {
    Auto = 0,
    ForceSdrToneMap = 1,
    PreferHdrWhenSupported = 2,
};

enum class PixelFormat : std::uint8_t {
    Nv12,
    P010,
};

enum class DecoderAcceleration : std::uint8_t {
    Unknown,
    Software,
    Hardware,
};

[[nodiscard]] std::string_view decoder_preference_name(DecoderPreference value) noexcept;
[[nodiscard]] std::string_view pixel_format_name(PixelFormat value) noexcept;
[[nodiscard]] std::string_view codec_name(coremedia::VideoCodec value) noexcept;
[[nodiscard]] std::string_view color_primaries_name(coremedia::ColorPrimaries value) noexcept;
[[nodiscard]] std::string_view transfer_function_name(coremedia::TransferFunction value) noexcept;
[[nodiscard]] std::string_view matrix_coefficients_name(coremedia::MatrixCoefficients value) noexcept;
[[nodiscard]] std::string_view color_range_name(coremedia::ColorRange value) noexcept;

namespace detail {

inline constexpr std::uint32_t MaxDecodedVideoDimension = 8192;
inline constexpr std::uint32_t MaxDxgiAllocationPadding = 512;
inline constexpr std::uint64_t MaxDxgiReadbackBytes = 256ULL * 1024ULL * 1024ULL;

using DecoderAcceleration = ::iPhoneMirror::media::DecoderAcceleration;

struct DxgiReadbackLayout {
    std::uint32_t minimum_row_pitch{};
    std::uint32_t row_count{};
    std::uint32_t total_bytes{};
};

[[nodiscard]] std::optional<std::uint32_t> checked_video_buffer_size(
    std::uint32_t width, std::uint32_t height, PixelFormat format) noexcept;
[[nodiscard]] std::optional<std::uint32_t> checked_nv12_buffer_size(
    std::uint32_t width, std::uint32_t height) noexcept;
[[nodiscard]] DecoderAcceleration classify_dxva_mode(std::int32_t mode) noexcept;
[[nodiscard]] std::optional<DxgiReadbackLayout> checked_dxgi_readback_layout(
    std::uint32_t visible_width, std::uint32_t visible_height,
    std::uint32_t allocation_width, std::uint32_t allocation_height,
    std::uint32_t mip_levels, std::uint32_t array_size,
    std::uint32_t source_subresource, std::uint32_t sample_count,
    std::uint32_t row_pitch, PixelFormat format) noexcept;
[[nodiscard]] bool is_random_access_sample(const coremedia::FormatDescription& format,
    std::span<const std::uint8_t> length_prefixed_sample) noexcept;

struct YuvConversionParameters {
    double y_offset{};
    double y_scale{1.0};
    double chroma_offset{0.5};
    double chroma_scale{1.0};
    double red_cr{1.5748};
    double green_cb{-0.187324};
    double green_cr{-0.468124};
    double blue_cb{1.8556};
};

struct SdrRgb {
    double red{};
    double green{};
    double blue{};
};

[[nodiscard]] YuvConversionParameters yuv_conversion_parameters(PixelFormat format,
    coremedia::ColorRange range, coremedia::MatrixCoefficients matrix) noexcept;
// Converts normalized NV12/P010 component samples to display-ready SDR RGB.
// PQ and HLG inputs are deterministically tone-mapped using the same curve as
// the native D3D preview; SDR inputs retain their encoded transfer function.
[[nodiscard]] SdrRgb convert_yuv_to_sdr(double y, double cb, double cr,
    const coremedia::VideoColorDescription& color, PixelFormat format) noexcept;
[[nodiscard]] SdrRgb convert_yuv_to_sdr(double y, double cb, double cr,
    const coremedia::VideoColorDescription& color,
    const YuvConversionParameters& parameters) noexcept;

} // namespace detail

struct DecodedFrame {
    std::uint32_t width{};
    std::uint32_t height{};
    // Byte stride for both NV12 and P010. P010 therefore normally reports at
    // least width * 2, while NV12 normally reports at least width.
    std::int32_t stride{};
    std::int64_t timestamp_100ns{};
    std::chrono::steady_clock::time_point received_at{};
    PixelFormat pixel_format{PixelFormat::Nv12};
    coremedia::VideoColorDescription color;
    // Contiguous semi-planar Y followed by interleaved UV. The historical
    // member name is retained for ABI-local consumers; pixel_format tells
    // whether each component occupies 8 bits (NV12) or 16 bits (P010).
    std::vector<std::uint8_t> nv12;
};

namespace detail {

// Copies a decoded NV12/P010 frame into a tightly packed 8-bit NV12 canvas.
// Scaling preserves aspect ratio without upscaling, and unused canvas pixels
// are filled with range-correct black and neutral chroma.
[[nodiscard]] bool copy_nv12_frame_letterboxed(const DecodedFrame& frame,
    std::span<std::uint8_t> output, std::uint32_t output_width,
    std::uint32_t output_height) noexcept;

} // namespace detail

class MediaFoundationVideoDecoder {
public:
    explicit MediaFoundationVideoDecoder(
        DecoderPreference preference = DecoderPreference::Auto);
    ~MediaFoundationVideoDecoder();
    MediaFoundationVideoDecoder(const MediaFoundationVideoDecoder&) = delete;
    MediaFoundationVideoDecoder& operator=(const MediaFoundationVideoDecoder&) = delete;

    void configure(const coremedia::FormatDescription& format,
        std::uint32_t fps_numerator = 60, std::uint32_t fps_denominator = 1);
    [[nodiscard]] std::vector<DecodedFrame> decode(
        std::span<const std::uint8_t> length_prefixed_sample,
        std::int64_t timestamp_100ns, std::int64_t duration_100ns);
    [[nodiscard]] std::vector<DecodedFrame> drain();
    void flush();

    [[nodiscard]] DecoderPreference preference() const noexcept;
    [[nodiscard]] std::string_view selected_decoder_name() const noexcept;
    [[nodiscard]] DecoderAcceleration decoder_acceleration() const noexcept;
    [[nodiscard]] bool selected_decoder_is_hardware() const noexcept;
    [[nodiscard]] PixelFormat output_pixel_format() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
    bool com_initialized_{};
};

// Source compatibility for callers compiled against the original H.264-only
// class. configure() now validates and accepts both AVC and HEVC descriptions.
using MediaFoundationH264Decoder = MediaFoundationVideoDecoder;

} // namespace iPhoneMirror::media
