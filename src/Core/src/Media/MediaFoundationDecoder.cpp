#include "Media/MediaFoundationDecoder.h"

#include "../Logging.h"

#include <Windows.h>
#include <codecapi.h>
#include <d3d10_1.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <format>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::media {
namespace {

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(std::format("{} failed: 0x{:08X}", operation,
            static_cast<unsigned>(result)));
    }
}

void ensure_media_foundation() {
    static std::once_flag flag;
    static HRESULT startup_result = E_FAIL;
    std::call_once(flag, [] { startup_result = MFStartup(MF_VERSION, MFSTARTUP_LITE); });
    check(startup_result, "MFStartup");
}

bool environment_enabled(const char* name) noexcept {
    char value[8]{};
    const auto length = GetEnvironmentVariableA(name, value, static_cast<DWORD>(std::size(value)));
    return length > 0 && (value[0] == '1' || value[0] == 'y' || value[0] == 'Y');
}

std::string_view acceleration_name(detail::DecoderAcceleration value) noexcept {
    switch (value) {
    case detail::DecoderAcceleration::Software: return "software";
    case detail::DecoderAcceleration::Hardware: return "hardware";
    default: return "unknown";
    }
}

std::string narrow_ascii(std::wstring_view value) {
    std::string result;
    result.reserve(value.size());
    for (const auto ch : value) result.push_back(ch <= 0x7f ? static_cast<char>(ch) : '?');
    return result;
}

std::vector<std::uint8_t> parameter_sets_annex_b(
    const coremedia::FormatDescription& format) {
    std::vector<std::uint8_t> result;
    const auto add = [&result](const std::vector<std::uint8_t>& nalu) {
        result.insert(result.end(), {0, 0, 0, 1});
        result.insert(result.end(), nalu.begin(), nalu.end());
    };
    for (const auto& vps : format.video_parameter_sets) add(vps);
    for (const auto& sps : format.sequence_parameter_sets) add(sps);
    for (const auto& pps : format.picture_parameter_sets) add(pps);
    return result;
}

std::vector<std::uint8_t> length_prefixed_to_annex_b(
    std::span<const std::uint8_t> sample, std::uint8_t length_size) {
    if (length_size < 1 || length_size > 4) {
        throw std::runtime_error("invalid video NAL length size");
    }
    std::vector<std::uint8_t> result;
    result.reserve(sample.size() + 16);
    std::size_t offset{};
    while (offset < sample.size()) {
        if (sample.size() - offset < length_size) {
            throw std::runtime_error("truncated length-prefixed video NAL header");
        }
        std::uint32_t length{};
        for (std::uint8_t index{}; index < length_size; ++index) {
            length = (length << 8U) | sample[offset + index];
        }
        offset += length_size;
        if (length == 0 || length > sample.size() - offset) {
            throw std::runtime_error("invalid length-prefixed video NAL size");
        }
        result.insert(result.end(), {0, 0, 0, 1});
        result.insert(result.end(), sample.begin() + static_cast<std::ptrdiff_t>(offset),
            sample.begin() + static_cast<std::ptrdiff_t>(offset + length));
        offset += length;
    }
    return result;
}

GUID input_subtype(coremedia::VideoCodec codec) {
    if (codec == coremedia::VideoCodec::H264) return MFVideoFormat_H264;
    if (codec == coremedia::VideoCodec::Hevc) return MFVideoFormat_HEVC;
    throw std::invalid_argument("unsupported compressed video codec");
}

coremedia::ColorPrimaries map_primaries(UINT32 value) noexcept {
    switch (value) {
    case MFVideoPrimaries_BT709: return coremedia::ColorPrimaries::Bt709;
    case MFVideoPrimaries_BT2020: return coremedia::ColorPrimaries::Bt2020;
    case MFVideoPrimaries_Display_P3: return coremedia::ColorPrimaries::DisplayP3;
    default: return coremedia::ColorPrimaries::Unspecified;
    }
}

coremedia::TransferFunction map_transfer(UINT32 value) noexcept {
    switch (value) {
    case MFVideoTransFunc_709: return coremedia::TransferFunction::Bt709;
    case MFVideoTransFunc_sRGB: return coremedia::TransferFunction::Srgb;
    case MFVideoTransFunc_2084: return coremedia::TransferFunction::Pq;
    case MFVideoTransFunc_HLG: return coremedia::TransferFunction::Hlg;
    default: return coremedia::TransferFunction::Unspecified;
    }
}

coremedia::MatrixCoefficients map_matrix(UINT32 value) noexcept {
    switch (value) {
    case MFVideoTransferMatrix_BT601: return coremedia::MatrixCoefficients::Bt601;
    case MFVideoTransferMatrix_BT709: return coremedia::MatrixCoefficients::Bt709;
    case MFVideoTransferMatrix_BT2020_10:
    case MFVideoTransferMatrix_BT2020_12:
        return coremedia::MatrixCoefficients::Bt2020;
    default: return coremedia::MatrixCoefficients::Unspecified;
    }
}

coremedia::ColorRange map_range(UINT32 value) noexcept {
    switch (value) {
    case MFNominalRange_0_255: return coremedia::ColorRange::Full;
    case MFNominalRange_16_235: return coremedia::ColorRange::Limited;
    default: return coremedia::ColorRange::Unspecified;
    }
}

coremedia::VideoColorDescription color_description(IMFAttributes* attributes,
    const coremedia::FormatDescription& format,
    const coremedia::VideoColorDescription* base = nullptr) noexcept {
    auto color = base ? *base : format.color;
    UINT32 value{};
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_VIDEO_PRIMARIES, &value))) {
        const auto mapped = map_primaries(value);
        if (mapped != coremedia::ColorPrimaries::Unspecified) color.primaries = mapped;
    }
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_TRANSFER_FUNCTION, &value))) {
        const auto mapped = map_transfer(value);
        if (mapped != coremedia::TransferFunction::Unspecified) color.transfer = mapped;
    }
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_YUV_MATRIX, &value))) {
        const auto mapped = map_matrix(value);
        if (mapped != coremedia::MatrixCoefficients::Unspecified) color.matrix = mapped;
    }
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &value))) {
        const auto mapped = map_range(value);
        if (mapped != coremedia::ColorRange::Unspecified) color.range = mapped;
    }
#if (WINVER >= _WIN32_WINNT_WIN10)
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_MAX_LUMINANCE_LEVEL, &value)))
        color.hdr.max_content_light_level = value;
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_MAX_FRAME_AVERAGE_LUMINANCE_LEVEL, &value)))
        color.hdr.max_frame_average_light_level = value;
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_MAX_MASTERING_LUMINANCE, &value)))
        color.hdr.max_mastering_luminance = value;
    if (attributes && SUCCEEDED(attributes->GetUINT32(MF_MT_MIN_MASTERING_LUMINANCE, &value)))
        color.hdr.min_mastering_luminance = value;
#endif
    if (color.primaries == coremedia::ColorPrimaries::Unspecified)
        color.primaries = coremedia::ColorPrimaries::Bt709;
    if (color.transfer == coremedia::TransferFunction::Unspecified)
        color.transfer = coremedia::TransferFunction::Bt709;
    if (color.matrix == coremedia::MatrixCoefficients::Unspecified) {
        color.matrix = format.height >= 720
            ? coremedia::MatrixCoefficients::Bt709
            : coremedia::MatrixCoefficients::Bt601;
    }
    if (color.range == coremedia::ColorRange::Unspecified)
        color.range = coremedia::ColorRange::Limited;
    return color;
}

struct DecoderCandidate {
    ComPtr<IMFActivate> activation;
    CLSID clsid{};
    bool use_clsid{};
    bool hardware{};
    bool request_dxva{};
    std::string name;
};

std::vector<DecoderCandidate> enumerate_pass(const GUID& subtype, UINT32 flags,
    bool request_dxva = false) {
    MFT_REGISTER_TYPE_INFO input{MFMediaType_Video, subtype};
    IMFActivate** raw{};
    UINT32 count{};
    const auto result = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &input, nullptr, &raw, &count);
    if (FAILED(result)) {
        logging::write(std::format("mf_decoder enumerate flags=0x{:X} hr=0x{:08X}",
            flags, static_cast<unsigned>(result)));
        return {};
    }
    std::vector<DecoderCandidate> candidates;
    candidates.reserve(count);
    for (UINT32 index{}; index < count; ++index) {
        ComPtr<IMFActivate> activation;
        activation.Attach(raw[index]);
        wchar_t* friendly{};
        UINT32 friendly_length{};
        std::string name = "MediaFoundationMFT";
        if (SUCCEEDED(activation->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute,
                &friendly, &friendly_length)) && friendly) {
            name = narrow_ascii(std::wstring_view(friendly, friendly_length));
            CoTaskMemFree(friendly);
        }
        wchar_t* hardware_url{};
        UINT32 hardware_url_length{};
        const bool hardware = SUCCEEDED(activation->GetAllocatedString(
            MFT_ENUM_HARDWARE_URL_Attribute, &hardware_url, &hardware_url_length));
        if (hardware_url) CoTaskMemFree(hardware_url);
        candidates.push_back({
            .activation = std::move(activation),
            .hardware = hardware,
            .request_dxva = request_dxva || hardware,
            .name = std::move(name),
        });
    }
    CoTaskMemFree(raw);
    std::string summary;
    for (const auto& candidate : candidates) {
        if (!summary.empty()) summary += ',';
        summary += candidate.name;
        summary += candidate.hardware ? "[hardware]" :
            candidate.request_dxva ? "[dxva]" : "[software]";
    }
    logging::write(std::format(
        "mf_decoder enumerate flags=0x{:X} count={} candidates={}",
        flags, candidates.size(), summary.empty() ? "none" : summary));
    return candidates;
}

void append_candidates(std::vector<DecoderCandidate>& destination,
    std::vector<DecoderCandidate> source) {
    for (auto& candidate : source) destination.push_back(std::move(candidate));
}

std::vector<DecoderCandidate> software_candidates(const GUID& subtype,
    UINT32 flags, bool request_dxva = false) {
    auto candidates = enumerate_pass(subtype, flags, request_dxva);
    std::erase_if(candidates, [](const DecoderCandidate& candidate) {
        return candidate.hardware;
    });
    return candidates;
}

void append_builtin_candidates(std::vector<DecoderCandidate>& candidates,
    coremedia::VideoCodec codec) {
    const auto add = [&candidates](const CLSID& clsid, std::string name) {
        candidates.push_back({.clsid = clsid, .use_clsid = true,
            .name = std::move(name)});
    };
    if (codec == coremedia::VideoCodec::H264) {
        add(CLSID_MSH264DecoderMFT, "MSH264DecoderMFT");
        add(CLSID_CMSH264DecoderMFT, "CMSH264DecoderMFT");
    } else if (codec == coremedia::VideoCodec::Hevc) {
        add(CLSID_MSH265DecoderMFT, "MSH265DecoderMFT");
    }
}

std::vector<DecoderCandidate> decoder_candidates(coremedia::VideoCodec codec,
    DecoderPreference preference) {
    const auto subtype = input_subtype(codec);
    std::vector<DecoderCandidate> candidates;
    constexpr auto Sorted = static_cast<UINT32>(MFT_ENUM_FLAG_SORTANDFILTER);
    constexpr auto Software = static_cast<UINT32>(MFT_ENUM_FLAG_SYNCMFT) |
        static_cast<UINT32>(MFT_ENUM_FLAG_ASYNCMFT) |
        static_cast<UINT32>(MFT_ENUM_FLAG_LOCALMFT) | Sorted;
    if (preference == DecoderPreference::Auto) {
        append_candidates(candidates, enumerate_pass(subtype,
            static_cast<UINT32>(MFT_ENUM_FLAG_ALL) | Sorted));
    } else if (preference == DecoderPreference::HardwarePreferred) {
        append_candidates(candidates, enumerate_pass(subtype,
            static_cast<UINT32>(MFT_ENUM_FLAG_HARDWARE) | Sorted, true));
        // Microsoft's inbox decoder is registered as a software MFT but can
        // use DXVA after receiving a D3D manager. Try that mode before the
        // identical system MFT without DXVA, which is the reliable fallback.
        append_candidates(candidates, software_candidates(subtype, Software, true));
        append_candidates(candidates, software_candidates(subtype, Software, false));
    } else {
        append_candidates(candidates, software_candidates(subtype, Software));
    }
    append_builtin_candidates(candidates, codec);
    return candidates;
}

} // namespace

std::string_view decoder_preference_name(DecoderPreference value) noexcept {
    switch (value) {
    case DecoderPreference::Auto: return "auto";
    case DecoderPreference::HardwarePreferred: return "hardware_preferred";
    case DecoderPreference::SoftwareCompatible: return "software_compatible";
    }
    return "unknown";
}

std::string_view pixel_format_name(PixelFormat value) noexcept {
    return value == PixelFormat::P010 ? "p010" : "nv12";
}

std::string_view codec_name(coremedia::VideoCodec value) noexcept {
    switch (value) {
    case coremedia::VideoCodec::H264: return "h264";
    case coremedia::VideoCodec::Hevc: return "hevc";
    default: return "unknown";
    }
}

std::string_view color_primaries_name(coremedia::ColorPrimaries value) noexcept {
    switch (value) {
    case coremedia::ColorPrimaries::Bt709: return "bt709";
    case coremedia::ColorPrimaries::Bt2020: return "bt2020";
    case coremedia::ColorPrimaries::DisplayP3: return "display_p3";
    default: return "unspecified";
    }
}

std::string_view transfer_function_name(coremedia::TransferFunction value) noexcept {
    switch (value) {
    case coremedia::TransferFunction::Bt709: return "bt709";
    case coremedia::TransferFunction::Srgb: return "srgb";
    case coremedia::TransferFunction::Pq: return "pq";
    case coremedia::TransferFunction::Hlg: return "hlg";
    default: return "unspecified";
    }
}

std::string_view matrix_coefficients_name(coremedia::MatrixCoefficients value) noexcept {
    switch (value) {
    case coremedia::MatrixCoefficients::Bt601: return "bt601";
    case coremedia::MatrixCoefficients::Bt709: return "bt709";
    case coremedia::MatrixCoefficients::Bt2020: return "bt2020";
    default: return "unspecified";
    }
}

std::string_view color_range_name(coremedia::ColorRange value) noexcept {
    switch (value) {
    case coremedia::ColorRange::Limited: return "limited";
    case coremedia::ColorRange::Full: return "full";
    default: return "unspecified";
    }
}

namespace detail {

std::optional<std::uint32_t> checked_video_buffer_size(
    std::uint32_t width, std::uint32_t height, PixelFormat format) noexcept {
    if (width == 0 || height == 0 || width > MaxDecodedVideoDimension ||
        height > MaxDecodedVideoDimension) return std::nullopt;
    const auto component_bytes = format == PixelFormat::P010 ? 2ULL : 1ULL;
    const auto stride = ((static_cast<std::uint64_t>(width) + 1U) & ~1ULL) * component_bytes;
    const auto y_bytes = stride * height;
    const auto uv_bytes = stride * ((static_cast<std::uint64_t>(height) + 1U) / 2U);
    const auto total = y_bytes + uv_bytes;
    if (total > std::numeric_limits<std::uint32_t>::max()) return std::nullopt;
    return static_cast<std::uint32_t>(total);
}

std::optional<std::uint32_t> checked_nv12_buffer_size(
    std::uint32_t width, std::uint32_t height) noexcept {
    return checked_video_buffer_size(width, height, PixelFormat::Nv12);
}

DecoderAcceleration classify_dxva_mode(std::int32_t mode) noexcept {
    switch (mode) {
    case eAVDecVideoDXVAMode_SW:
        return DecoderAcceleration::Software;
    case eAVDecVideoDXVAMode_MC:
    case eAVDecVideoDXVAMode_IDCT:
    case eAVDecVideoDXVAMode_VLD:
        return DecoderAcceleration::Hardware;
    default:
        return DecoderAcceleration::Unknown;
    }
}

std::optional<DxgiReadbackLayout> checked_dxgi_readback_layout(
    std::uint32_t visible_width, std::uint32_t visible_height,
    std::uint32_t allocation_width, std::uint32_t allocation_height,
    std::uint32_t mip_levels, std::uint32_t array_size,
    std::uint32_t source_subresource, std::uint32_t sample_count,
    std::uint32_t row_pitch, PixelFormat format) noexcept {
    if (visible_width == 0 || visible_height == 0 ||
        visible_width > MaxDecodedVideoDimension ||
        visible_height > MaxDecodedVideoDimension ||
        allocation_width < visible_width || allocation_height < visible_height ||
        mip_levels == 0 || array_size == 0 || sample_count != 1 || row_pitch == 0) {
        return std::nullopt;
    }

    const auto maximum_width = static_cast<std::uint64_t>(visible_width) +
        MaxDxgiAllocationPadding;
    const auto maximum_height = static_cast<std::uint64_t>(visible_height) +
        MaxDxgiAllocationPadding;
    if (allocation_width > maximum_width || allocation_height > maximum_height)
        return std::nullopt;

    const auto subresource_count = static_cast<std::uint64_t>(mip_levels) * array_size;
    if (source_subresource >= subresource_count ||
        source_subresource % mip_levels != 0) {
        return std::nullopt;
    }

    const auto component_bytes = format == PixelFormat::P010 ? 2ULL : 1ULL;
    const auto even_allocation_width = static_cast<std::uint64_t>(allocation_width) +
        (allocation_width & 1U);
    const auto minimum_row_pitch = even_allocation_width * component_bytes;
    const auto row_count = static_cast<std::uint64_t>(allocation_height) +
        (static_cast<std::uint64_t>(allocation_height) + 1ULL) / 2ULL;
    if (minimum_row_pitch > std::numeric_limits<std::uint32_t>::max() ||
        row_count > std::numeric_limits<std::uint32_t>::max() ||
        row_pitch < minimum_row_pitch || row_pitch % component_bytes != 0 ||
        row_pitch > static_cast<std::uint32_t>(
            std::numeric_limits<std::int32_t>::max()) ||
        row_count > MaxDxgiReadbackBytes / row_pitch) {
        return std::nullopt;
    }
    const auto total_bytes = row_count * row_pitch;
    if (total_bytes > MaxDxgiReadbackBytes ||
        total_bytes > std::numeric_limits<std::uint32_t>::max()) {
        return std::nullopt;
    }
    return DxgiReadbackLayout{
        .minimum_row_pitch = static_cast<std::uint32_t>(minimum_row_pitch),
        .row_count = static_cast<std::uint32_t>(row_count),
        .total_bytes = static_cast<std::uint32_t>(total_bytes),
    };
}

bool is_random_access_sample(const coremedia::FormatDescription& format,
    std::span<const std::uint8_t> sample) noexcept {
    if (format.nalu_length_size < 1 || format.nalu_length_size > 4) return false;
    std::size_t offset{};
    while (offset < sample.size()) {
        if (sample.size() - offset < format.nalu_length_size) return false;
        std::uint32_t length{};
        for (std::uint8_t index{}; index < format.nalu_length_size; ++index)
            length = (length << 8U) | sample[offset + index];
        offset += format.nalu_length_size;
        if (length == 0 || length > sample.size() - offset) return false;
        if (format.video_codec() == coremedia::VideoCodec::H264) {
            if ((sample[offset] & 0x1fU) == 5U) return true;
        } else if (format.video_codec() == coremedia::VideoCodec::Hevc) {
            const auto type = static_cast<std::uint8_t>((sample[offset] >> 1U) & 0x3fU);
            if (type >= 16U && type <= 21U) return true;
        }
        offset += length;
    }
    return false;
}

YuvConversionParameters yuv_conversion_parameters(PixelFormat format,
    coremedia::ColorRange range, coremedia::MatrixCoefficients matrix) noexcept {
    YuvConversionParameters result;
    const bool full_range = range == coremedia::ColorRange::Full;
    if (format == PixelFormat::P010) {
        constexpr double Denominator = 65535.0;
        result.y_offset = full_range ? 0.0 : (64.0 * 64.0) / Denominator;
        result.y_scale = full_range
            ? Denominator / (1023.0 * 64.0)
            : Denominator / (876.0 * 64.0);
        result.chroma_offset = (512.0 * 64.0) / Denominator;
        result.chroma_scale = full_range
            ? Denominator / (1023.0 * 64.0)
            : Denominator / (896.0 * 64.0);
    } else {
        result.y_offset = full_range ? 0.0 : 16.0 / 255.0;
        result.y_scale = full_range ? 1.0 : 255.0 / 219.0;
        result.chroma_offset = 128.0 / 255.0;
        result.chroma_scale = full_range ? 1.0 : 255.0 / 224.0;
    }
    if (matrix == coremedia::MatrixCoefficients::Bt601) {
        result.red_cr = 1.4020;
        result.green_cb = -0.344136;
        result.green_cr = -0.714136;
        result.blue_cb = 1.7720;
    } else if (matrix == coremedia::MatrixCoefficients::Bt2020) {
        result.red_cr = 1.4746;
        result.green_cb = -0.164553;
        result.green_cr = -0.571353;
        result.blue_cb = 1.8814;
    }
    return result;
}

namespace {

double clamp_unit(double value) noexcept { return std::clamp(value, 0.0, 1.0); }

double inverse_srgb(double value) noexcept {
    value = clamp_unit(value);
    return value <= 0.04045 ? value / 12.92
        : std::pow((value + 0.055) / 1.055, 2.4);
}

double encode_srgb(double value) noexcept {
    value = std::max(0.0, value);
    return value <= 0.0031308 ? value * 12.92
        : 1.055 * std::pow(value, 1.0 / 2.4) - 0.055;
}

double pq_to_nits(double value) noexcept {
    constexpr double m1 = 2610.0 / 16384.0;
    constexpr double m2 = 2523.0 / 32.0;
    constexpr double c1 = 3424.0 / 4096.0;
    constexpr double c2 = 2413.0 / 128.0;
    constexpr double c3 = 2392.0 / 128.0;
    const auto p = std::pow(clamp_unit(value), 1.0 / m2);
    return 10000.0 * std::pow(std::max((p - c1) /
        std::max(c2 - c3 * p, 1.0e-12), 0.0), 1.0 / m1);
}

double hlg_to_nits(double value, double peak_nits) noexcept {
    constexpr double a = 0.17883277;
    constexpr double b = 0.28466892;
    constexpr double c = 0.55991073;
    value = clamp_unit(value);
    const auto scene = value <= 0.5 ? value * value / 3.0
        : (std::exp((value - c) / a) + b) / 12.0;
    return std::max(peak_nits, 100.0) * std::pow(std::max(scene, 0.0), 1.2);
}

SdrRgb convert_primaries_to_709(SdrRgb value,
    coremedia::ColorPrimaries primaries) noexcept {
    if (primaries == coremedia::ColorPrimaries::Bt2020) {
        return {
            1.6605 * value.red - 0.5876 * value.green - 0.0728 * value.blue,
           -0.1246 * value.red + 1.1329 * value.green - 0.0083 * value.blue,
           -0.0182 * value.red - 0.1006 * value.green + 1.1187 * value.blue,
        };
    }
    if (primaries == coremedia::ColorPrimaries::DisplayP3) {
        return {
            1.224745 * value.red - 0.224904 * value.green,
           -0.042058 * value.red + 1.042081 * value.green,
           -0.019642 * value.red - 0.078655 * value.green + 1.098537 * value.blue,
        };
    }
    return value;
}

double aces_tone_map(double nits) noexcept {
    const auto value = std::max(nits / 203.0, 0.0);
    return clamp_unit((value * (2.51 * value + 0.03)) /
        (value * (2.43 * value + 0.59) + 0.14));
}

} // namespace

SdrRgb convert_yuv_to_sdr(double y, double cb, double cr,
    const coremedia::VideoColorDescription& color, PixelFormat format) noexcept {
    return convert_yuv_to_sdr(y, cb, cr, color,
        yuv_conversion_parameters(format, color.range, color.matrix));
}

SdrRgb convert_yuv_to_sdr(double y, double cb, double cr,
    const coremedia::VideoColorDescription& color,
    const YuvConversionParameters& parameters) noexcept {
    y = std::max(0.0, y - parameters.y_offset) * parameters.y_scale;
    cb = (cb - parameters.chroma_offset) * parameters.chroma_scale;
    cr = (cr - parameters.chroma_offset) * parameters.chroma_scale;
    SdrRgb encoded{
        y + parameters.red_cr * cr,
        y + parameters.green_cb * cb + parameters.green_cr * cr,
        y + parameters.blue_cb * cb,
    };
    encoded.red = clamp_unit(encoded.red);
    encoded.green = clamp_unit(encoded.green);
    encoded.blue = clamp_unit(encoded.blue);
    if (color.is_hdr()) {
        auto peak_nits = color.hdr.max_mastering_luminance != 0
            ? static_cast<double>(color.hdr.max_mastering_luminance)
            : color.hdr.max_content_light_level != 0
            ? static_cast<double>(color.hdr.max_content_light_level) : 1000.0;
        SdrRgb nits;
        if (color.transfer == coremedia::TransferFunction::Pq) {
            nits = {pq_to_nits(encoded.red), pq_to_nits(encoded.green),
                pq_to_nits(encoded.blue)};
        } else {
            nits = {hlg_to_nits(encoded.red, peak_nits),
                hlg_to_nits(encoded.green, peak_nits),
                hlg_to_nits(encoded.blue, peak_nits)};
        }
        nits = convert_primaries_to_709(nits, color.primaries);
        return {
            clamp_unit(encode_srgb(aces_tone_map(nits.red))),
            clamp_unit(encode_srgb(aces_tone_map(nits.green))),
            clamp_unit(encode_srgb(aces_tone_map(nits.blue))),
        };
    }
    if (color.primaries != coremedia::ColorPrimaries::Bt709 &&
        color.primaries != coremedia::ColorPrimaries::Unspecified) {
        auto linear = convert_primaries_to_709({inverse_srgb(encoded.red),
            inverse_srgb(encoded.green), inverse_srgb(encoded.blue)}, color.primaries);
        return {clamp_unit(encode_srgb(linear.red)),
            clamp_unit(encode_srgb(linear.green)),
            clamp_unit(encode_srgb(linear.blue))};
    }
    return encoded;
}

} // namespace detail

struct MediaFoundationVideoDecoder::Impl {
    DecoderPreference preference{DecoderPreference::Auto};
    ComPtr<IMFTransform> transform;
    ComPtr<IMFMediaType> output_type;
    ComPtr<IMFMediaEventGenerator> event_generator;
    ComPtr<IMFActivate> active_activation;
    ComPtr<IMFDXGIDeviceManager> d3d_manager;
    ComPtr<ID3D11Device> d3d_device;
    ComPtr<ID3D11DeviceContext> d3d_context;
    ComPtr<ID3D11Texture2D> readback_texture;
    DXGI_FORMAT readback_format{DXGI_FORMAT_UNKNOWN};
    UINT readback_width{};
    UINT readback_height{};
    coremedia::FormatDescription format;
    std::vector<DecoderCandidate> candidates;
    std::size_t candidate_index{std::numeric_limits<std::size_t>::max()};
    std::uint32_t fps_numerator{60};
    std::uint32_t fps_denominator{1};
    std::uint32_t minimum_output_bytes{};
    PixelFormat output_format{PixelFormat::Nv12};
    coremedia::VideoColorDescription output_color;
    std::string selected_name;
    detail::DecoderAcceleration actual_acceleration{detail::DecoderAcceleration::Unknown};
    bool selected_hardware{};
    bool selected_candidate_hardware{};
    bool asynchronous{};
    bool dxva_requested{};
    bool acceleration_query_complete{};
    bool acceleration_query_exhausted{};
    bool saw_dxgi_output{};
    std::uint32_t acceleration_query_attempts{};
    std::chrono::steady_clock::time_point next_acceleration_query{};
    std::uint32_t pending_input_requests{};
    bool configured{};
    bool sent_parameter_sets{};
    bool waiting_for_random_access{};

    explicit Impl(DecoderPreference value) : preference(value) {}

    ~Impl() { reset_transform(); }

    void reset_transform() noexcept {
        if (active_activation) (void)active_activation->ShutdownObject();
        output_type.Reset();
        transform.Reset();
        event_generator.Reset();
        active_activation.Reset();
        d3d_manager.Reset();
        d3d_context.Reset();
        d3d_device.Reset();
        readback_texture.Reset();
        readback_format = DXGI_FORMAT_UNKNOWN;
        readback_width = readback_height = 0;
        minimum_output_bytes = 0;
        selected_name.clear();
        actual_acceleration = detail::DecoderAcceleration::Unknown;
        selected_hardware = false;
        selected_candidate_hardware = false;
        asynchronous = false;
        dxva_requested = false;
        acceleration_query_complete = false;
        acceleration_query_exhausted = false;
        saw_dxgi_output = false;
        acceleration_query_attempts = 0;
        next_acceleration_query = {};
        pending_input_requests = 0;
        configured = false;
        sent_parameter_sets = false;
    }

    void enable_common_attributes(bool request_dxva) {
        ComPtr<IMFAttributes> attributes;
        HRESULT low_latency_attribute_result = E_NOINTERFACE;
        bool d3d11_aware{};
        if (SUCCEEDED(transform->GetAttributes(&attributes))) {
            UINT32 async_attribute{};
            if (SUCCEEDED(attributes->GetUINT32(MF_TRANSFORM_ASYNC, &async_attribute)) &&
                async_attribute != 0) {
                asynchronous = true;
                check(attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE),
                    "unlock asynchronous decoder MFT");
            }
            low_latency_attribute_result = attributes->SetUINT32(MF_LOW_LATENCY, TRUE);
            UINT32 aware{};
            d3d11_aware = SUCCEEDED(attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware)) && aware != 0;
        }
        ComPtr<ICodecAPI> codec_api;
        HRESULT codec_result = E_NOINTERFACE;
        if (SUCCEEDED(transform.As(&codec_api))) {
            VARIANT value;
            VariantInit(&value);
            value.vt = VT_UI4;
            value.ulVal = TRUE;
            codec_result = codec_api->SetValue(&CODECAPI_AVLowLatencyMode, &value);
            VariantClear(&value);
        }
        logging::write(std::format(
            "mf_decoder attributes low_latency_hr=0x{:08X} codec_hr=0x{:08X} d3d11_aware={} async={}",
            static_cast<unsigned>(low_latency_attribute_result),
            static_cast<unsigned>(codec_result), d3d11_aware ? "true" : "false",
            asynchronous ? "true" : "false"));
        if (asynchronous) {
            check(transform.As(&event_generator),
                "query asynchronous decoder event generator");
        }

        if (!d3d11_aware || !request_dxva) {
            logging::write(std::format(
                "mf_decoder d3d11_manager=disabled policy={} d3d11_aware={} requested={}",
                decoder_preference_name(preference), d3d11_aware ? "true" : "false",
                request_dxva ? "true" : "false"));
            return;
        }
        D3D_FEATURE_LEVEL level{};
        constexpr D3D_FEATURE_LEVEL levels[] = {
            D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        const auto device_result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
            &d3d_device, &level, &d3d_context);
        if (FAILED(device_result)) {
            d3d_device.Reset();
            d3d_context.Reset();
            logging::write(std::format(
                "mf_decoder d3d11_manager=unavailable stage=create_device hr=0x{:08X}",
                static_cast<unsigned>(device_result)));
            return;
        }
        ComPtr<ID3D10Multithread> multithread;
        check(d3d_device.As(&multithread),
            "query decoder D3D multithread protection");
        (void)multithread->SetMultithreadProtected(TRUE);
        if (!multithread->GetMultithreadProtected())
            throw std::runtime_error(
                "decoder D3D multithread protection could not be enabled");
        logging::write("mf_decoder d3d11_multithread_protected=true");
        UINT reset_token{};
        auto manager_result = MFCreateDXGIDeviceManager(&reset_token, &d3d_manager);
        if (SUCCEEDED(manager_result))
            manager_result = d3d_manager->ResetDevice(d3d_device.Get(), reset_token);
        if (SUCCEEDED(manager_result)) {
            manager_result = transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                reinterpret_cast<ULONG_PTR>(d3d_manager.Get()));
        }
        if (FAILED(manager_result)) {
            d3d_manager.Reset();
            d3d_context.Reset();
            d3d_device.Reset();
            logging::write(std::format(
                "mf_decoder d3d11_manager=unavailable stage=attach hr=0x{:08X}",
                static_cast<unsigned>(manager_result)));
            return;
        }
        dxva_requested = true;
        logging::write(std::format("mf_decoder d3d11_manager=enabled feature_level=0x{:04X}",
            static_cast<unsigned>(level)));
    }

    void enforce_software_decoding(const DecoderCandidate& candidate) {
        if (preference != DecoderPreference::SoftwareCompatible) return;
        if (candidate.hardware || candidate.request_dxva || dxva_requested) {
            throw std::runtime_error(
                "software decoder policy selected a hardware/DXVA candidate");
        }
        if (format.video_codec() != coremedia::VideoCodec::H264) {
            // The public CodecAPI only exposes a legacy H.264 acceleration
            // switch. HEVC software operation is enforced by never attaching
            // a D3D device manager to the software MFT.
            logging::write(std::format(
                "mf_decoder software_enforcement candidate={} codec={} "
                "method=no_d3d11_manager codecapi=not_available result=software",
                candidate.name, codec_name(format.video_codec())));
            return;
        }

        ComPtr<ICodecAPI> codec_api;
        auto result = transform.As(&codec_api);
        VARIANT value;
        VariantInit(&value);
        value.vt = VT_UI4;
        value.ulVal = FALSE;
        if (SUCCEEDED(result)) {
            result = codec_api->SetValue(
                &CODECAPI_AVDecVideoAcceleration_H264, &value);
        }
        VariantClear(&value);
        logging::write(std::format(
            "mf_decoder software_enforcement candidate={} codec=h264 "
            "method=CODECAPI_AVDecVideoAcceleration_H264 value=false "
            "hr=0x{:08X} result={}",
            candidate.name, static_cast<unsigned>(result),
            SUCCEEDED(result) ? "software" : "rejected"));
        if (FAILED(result)) {
            throw std::runtime_error(std::format(
                "cannot enforce H.264 software decoding: 0x{:08X}",
                static_cast<unsigned>(result)));
        }
    }

    void select_output() {
        output_type.Reset();
        const bool prefer_p010 = format.bit_depth_luma > 8 || format.bit_depth_chroma > 8;
        const std::array<GUID, 2> preferred = prefer_p010
            ? std::array<GUID, 2>{MFVideoFormat_P010, MFVideoFormat_NV12}
            : std::array<GUID, 2>{MFVideoFormat_NV12, MFVideoFormat_P010};
        for (const auto& wanted : preferred) {
            for (DWORD index{};; ++index) {
                ComPtr<IMFMediaType> candidate;
                const auto result = transform->GetOutputAvailableType(0, index, &candidate);
                if (result == MF_E_NO_MORE_TYPES) break;
                check(result, "enumerate decoder output type");
                GUID subtype{};
                if (SUCCEEDED(candidate->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == wanted &&
                    SUCCEEDED(transform->SetOutputType(0, candidate.Get(), 0))) {
                    output_type = std::move(candidate);
                    output_format = wanted == MFVideoFormat_P010 ? PixelFormat::P010 : PixelFormat::Nv12;
                    const auto output_bytes = detail::checked_video_buffer_size(
                        format.width, format.height, output_format);
                    if (!output_bytes) throw std::runtime_error("decoded output buffer size overflow");
                    minimum_output_bytes = *output_bytes;
                    output_color = color_description(output_type.Get(), format);
                    return;
                }
            }
        }
        throw std::runtime_error("decoder exposes neither NV12 nor P010 output");
    }

    void configure_candidate(std::size_t index) {
        reset_transform();
        auto& candidate = candidates.at(index);
        if (candidate.use_clsid) {
            check(CoCreateInstance(candidate.clsid, nullptr, CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&transform)), "create built-in video decoder");
        } else {
            check(candidate.activation->ActivateObject(IID_PPV_ARGS(&transform)),
                "activate enumerated video decoder");
            active_activation = candidate.activation;
        }
        enable_common_attributes(candidate.request_dxva);
        enforce_software_decoding(candidate);

        ComPtr<IMFMediaType> input;
        check(MFCreateMediaType(&input), "create decoder input type");
        check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video),
            "set decoder input major type");
        check(input->SetGUID(MF_MT_SUBTYPE, input_subtype(format.video_codec())),
            "set decoder input subtype");
        check(MFSetAttributeSize(input.Get(), MF_MT_FRAME_SIZE, format.width, format.height),
            "set decoder input frame size");
        check(MFSetAttributeRatio(input.Get(), MF_MT_FRAME_RATE, fps_numerator, fps_denominator),
            "set decoder input frame rate");
        check(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive),
            "set decoder input interlace mode");
        if (!environment_enabled("IPHONE_MIRROR_ALLOW_FRAME_REORDERING")) {
            check(input->SetUINT32(MF_MT_VIDEO_NO_FRAME_ORDERING, TRUE),
                "disable decoder frame reordering");
        }
        if (dxva_requested && format.video_codec() == coremedia::VideoCodec::H264)
            (void)input->SetUINT32(MF_MT_VIDEO_H264_NO_FMOASO, TRUE);
        const auto sequence_header = parameter_sets_annex_b(format);
        if (!sequence_header.empty()) {
            check(input->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, sequence_header.data(),
                static_cast<UINT32>(sequence_header.size())), "set decoder sequence header");
        }
        check(transform->SetInputType(0, input.Get(), 0), "set decoder input type");
        select_output();
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0),
            "begin decoder streaming");
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0),
            "start decoder stream");
        candidate_index = index;
        selected_name = candidate.name;
        selected_candidate_hardware = candidate.hardware;
        actual_acceleration = preference == DecoderPreference::SoftwareCompatible
            ? detail::DecoderAcceleration::Software
            : candidate.hardware ? detail::DecoderAcceleration::Hardware
            : !dxva_requested ? detail::DecoderAcceleration::Software
                              : detail::DecoderAcceleration::Unknown;
        selected_hardware = actual_acceleration == detail::DecoderAcceleration::Hardware;
        configured = true;
        sent_parameter_sets = false;
        logging::write(std::format(
            "mf_decoder selected={} candidate={} dxva_requested={} actual={} "
            "policy={} codec={} output={} size={}x{} "
            "bit_depth={}/{} primaries={} transfer={} matrix={} range={} hdr={}",
            selected_name, candidate.hardware ? "hardware_mft" : "software_mft",
            dxva_requested ? "true" : "false",
            acceleration_name(actual_acceleration),
            decoder_preference_name(preference), codec_name(format.video_codec()),
            pixel_format_name(output_format), format.width, format.height,
            format.bit_depth_luma, format.bit_depth_chroma,
            color_primaries_name(output_color.primaries),
            transfer_function_name(output_color.transfer),
            matrix_coefficients_name(output_color.matrix),
            color_range_name(output_color.range), output_color.is_hdr() ? "true" : "false"));
    }

    void select_first_working_candidate(std::size_t begin = 0) {
        std::string failures;
        for (std::size_t index = begin; index < candidates.size(); ++index) {
            try {
                configure_candidate(index);
                return;
            } catch (const std::exception& error) {
                logging::write(std::format(
                    "mf_decoder candidate_rejected name={} candidate={} "
                    "dxva_intent={} reason={}",
                    candidates[index].name,
                    candidates[index].hardware ? "hardware_mft" : "software_mft",
                    candidates[index].request_dxva ? "true" : "false",
                    error.what()));
                if (!failures.empty()) failures += "; ";
                failures += candidates[index].name + ": " + error.what();
            }
        }
        reset_transform();
        throw std::runtime_error("no compatible Media Foundation decoder: " + failures);
    }

    void configure(const coremedia::FormatDescription& value,
        std::uint32_t numerator, std::uint32_t denominator) {
        if (!value.is_video() || value.video_codec() == coremedia::VideoCodec::Unknown ||
            !detail::checked_video_buffer_size(value.width, value.height, PixelFormat::Nv12) ||
            numerator == 0 || denominator == 0) {
            throw std::invalid_argument("invalid AVC/HEVC format description or dimensions");
        }
        format = value;
        fps_numerator = numerator;
        fps_denominator = denominator;
        candidates = decoder_candidates(format.video_codec(), preference);
        if (candidates.empty()) throw std::runtime_error("no Media Foundation video decoder is installed");
        waiting_for_random_access = false;
        select_first_working_candidate();
    }

    void report_acceleration_mode() {
        constexpr std::uint32_t MaxQueryAttempts = 8;
        constexpr auto QueryInterval = std::chrono::milliseconds(250);
        if (preference == DecoderPreference::SoftwareCompatible ||
            acceleration_query_complete || acceleration_query_exhausted) {
            return;
        }
        const auto now = std::chrono::steady_clock::now();
        if (now < next_acceleration_query) return;
        next_acceleration_query = now + QueryInterval;
        ++acceleration_query_attempts;

        ComPtr<ICodecAPI> codec_api;
        HRESULT result = transform.As(&codec_api);
        VARIANT value;
        VariantInit(&value);
        if (SUCCEEDED(result))
            result = codec_api->GetValue(&CODECAPI_AVDecVideoDXVAMode, &value);
        std::int32_t mode = -1;
        if (SUCCEEDED(result)) {
            if (value.vt == VT_UI4 &&
                value.ulVal <= static_cast<ULONG>(std::numeric_limits<std::int32_t>::max())) {
                mode = static_cast<std::int32_t>(value.ulVal);
            }
            else if (value.vt == VT_I4) mode = value.lVal;
        }
        VariantClear(&value);
        const auto reported = detail::classify_dxva_mode(mode);
        if (reported != detail::DecoderAcceleration::Unknown) {
            actual_acceleration = reported;
            selected_hardware = reported == detail::DecoderAcceleration::Hardware;
            acceleration_query_complete = true;
        } else if (acceleration_query_attempts >= MaxQueryAttempts) {
            acceleration_query_exhausted = true;
            // Some inbox MFTs do not expose CODECAPI_AVDecVideoDXVAMode. A
            // DXGI-backed sample is definitive hardware evidence; after the
            // bounded query window, CPU-backed output from a software MFT is
            // definitive enough to avoid reporting "detecting" forever.
            actual_acceleration = selected_candidate_hardware || saw_dxgi_output
                ? detail::DecoderAcceleration::Hardware
                : detail::DecoderAcceleration::Software;
            selected_hardware =
                actual_acceleration == detail::DecoderAcceleration::Hardware;
            acceleration_query_complete = true;
        }
        logging::write(std::format(
            "mf_decoder acceleration candidate={} dxva_requested={} "
            "query_attempt={}/{} query_hr=0x{:08X} dxva_mode={} actual={} state={}",
            selected_candidate_hardware ? "hardware_mft" : "software_mft",
            dxva_requested ? "true" : "false", acceleration_query_attempts,
            MaxQueryAttempts, static_cast<unsigned>(result), mode,
            acceleration_name(actual_acceleration),
            acceleration_query_complete ? "resolved" :
                acceleration_query_exhausted ? "exhausted" : "retry"));
    }

    bool copy_dxgi_output(IMFSample* sample, DecodedFrame& frame) {
        if (!sample || !d3d_context) return false;
        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(sample->GetBufferByIndex(0, &buffer))) return false;
        ComPtr<IMFDXGIBuffer> dxgi_buffer;
        if (FAILED(buffer.As(&dxgi_buffer))) return false;

        ComPtr<ID3D11Texture2D> source;
        check(dxgi_buffer->GetResource(IID_PPV_ARGS(&source)),
            "get decoder DXGI output texture");
        UINT source_subresource{};
        check(dxgi_buffer->GetSubresourceIndex(&source_subresource),
            "get decoder DXGI output subresource");
        D3D11_TEXTURE2D_DESC description{};
        source->GetDesc(&description);
        const auto expected_format = output_format == PixelFormat::P010
            ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;
        if (description.Format != expected_format ||
            description.Width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
            description.Height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION) {
            throw std::runtime_error(std::format(
                "unexpected decoder DXGI output format={} size={}x{} visible={}x{} "
                "mips={} array={} subresource={} samples={}",
                static_cast<unsigned>(description.Format), description.Width,
                description.Height, format.width, format.height,
                description.MipLevels, description.ArraySize, source_subresource,
                description.SampleDesc.Count));
        }
        const auto component_bytes = output_format == PixelFormat::P010 ? 2ULL : 1ULL;
        const auto minimum_pitch_64 =
            (static_cast<std::uint64_t>(description.Width) +
                (description.Width & 1U)) * component_bytes;
        if (minimum_pitch_64 > std::numeric_limits<std::uint32_t>::max() ||
            !detail::checked_dxgi_readback_layout(
                format.width, format.height, description.Width, description.Height,
                description.MipLevels, description.ArraySize, source_subresource,
                description.SampleDesc.Count,
                static_cast<std::uint32_t>(minimum_pitch_64), output_format)) {
            throw std::runtime_error(std::format(
                "unsafe decoder DXGI source layout size={}x{} visible={}x{} "
                "mips={} array={} subresource={} samples={} max_padding={} max_bytes={}",
                description.Width, description.Height, format.width, format.height,
                description.MipLevels, description.ArraySize, source_subresource,
                description.SampleDesc.Count, detail::MaxDxgiAllocationPadding,
                detail::MaxDxgiReadbackBytes));
        }
        if (!readback_texture || readback_format != description.Format ||
            readback_width != description.Width || readback_height != description.Height) {
            auto readback = description;
            readback.MipLevels = 1;
            readback.ArraySize = 1;
            readback.SampleDesc.Count = 1;
            readback.SampleDesc.Quality = 0;
            readback.Usage = D3D11_USAGE_STAGING;
            readback.BindFlags = 0;
            readback.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            readback.MiscFlags = 0;
            check(d3d_device->CreateTexture2D(&readback, nullptr, &readback_texture),
                "create decoder DXGI readback texture");
            readback_format = description.Format;
            readback_width = description.Width;
            readback_height = description.Height;
        }

        d3d_context->CopySubresourceRegion(readback_texture.Get(), 0, 0, 0, 0,
            source.Get(), source_subresource, nullptr);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check(d3d_context->Map(readback_texture.Get(), 0, D3D11_MAP_READ, 0, &mapped),
            "map decoder DXGI readback texture");
        try {
            const auto source_layout = detail::checked_dxgi_readback_layout(
                format.width, format.height, description.Width, description.Height,
                description.MipLevels, description.ArraySize, source_subresource,
                description.SampleDesc.Count, mapped.RowPitch, output_format);
            const auto visible_layout = detail::checked_dxgi_readback_layout(
                format.width, format.height, format.width, format.height,
                1, 1, 0, 1, mapped.RowPitch, output_format);
            if (!mapped.pData || !source_layout || !visible_layout) {
                throw std::runtime_error(std::format(
                    "invalid decoder DXGI mapped layout row_pitch={} allocation={}x{} "
                    "visible={}x{} max_bytes={}",
                    mapped.RowPitch, description.Width, description.Height,
                    format.width, format.height, detail::MaxDxgiReadbackBytes));
            }
            const auto* source_bytes = static_cast<const std::uint8_t*>(mapped.pData);
            const auto y_bytes = static_cast<std::size_t>(format.height) * mapped.RowPitch;
            const auto chroma_bytes = static_cast<std::size_t>(
                (static_cast<std::uint64_t>(format.height) + 1ULL) / 2ULL) *
                mapped.RowPitch;
            frame.nv12.reserve(visible_layout->total_bytes);
            frame.nv12.insert(frame.nv12.end(), source_bytes, source_bytes + y_bytes);
            const auto* source_chroma = source_bytes +
                static_cast<std::size_t>(description.Height) * mapped.RowPitch;
            frame.nv12.insert(frame.nv12.end(), source_chroma,
                source_chroma + chroma_bytes);
            frame.stride = static_cast<std::int32_t>(mapped.RowPitch);
        } catch (...) {
            d3d_context->Unmap(readback_texture.Get(), 0);
            throw;
        }
        d3d_context->Unmap(readback_texture.Get(), 0);
        saw_dxgi_output = true;
        actual_acceleration = detail::DecoderAcceleration::Hardware;
        selected_hardware = true;
        acceleration_query_complete = true;
        return true;
    }

    std::optional<DecodedFrame> receive_output() {
        for (;;) {
            MFT_OUTPUT_STREAM_INFO stream_info{};
            check(transform->GetOutputStreamInfo(0, &stream_info),
                "get decoder output stream info");
            ComPtr<IMFSample> sample;
            if ((stream_info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0) {
                ComPtr<IMFMediaBuffer> buffer;
                check(MFCreateSample(&sample), "create decoder output sample");
                check(MFCreateMemoryBuffer(std::max<DWORD>(stream_info.cbSize,
                    minimum_output_bytes), &buffer), "create decoder output buffer");
                check(sample->AddBuffer(buffer.Get()), "attach decoder output buffer");
            }
            MFT_OUTPUT_DATA_BUFFER output{};
            output.dwStreamID = 0;
            output.pSample = sample.Get();
            DWORD status{};
            const auto result = transform->ProcessOutput(0, 1, &output, &status);
            ComPtr<IMFCollection> output_events;
            if (output.pEvents) output_events.Attach(output.pEvents);
            // When MFT_OUTPUT_STREAM_PROVIDES_SAMPLES is set, ProcessOutput
            // returns a new sample reference. Adopt it immediately so stream
            // change, failure, and early-return paths release it exactly once.
            ComPtr<IMFSample> transform_sample;
            if (output.pSample && output.pSample != sample.Get())
                transform_sample.Attach(output.pSample);
            IMFSample* decoded_sample = transform_sample
                ? transform_sample.Get() : sample.Get();
            if (result == MF_E_TRANSFORM_NEED_MORE_INPUT) return std::nullopt;
            if (result == MF_E_TRANSFORM_STREAM_CHANGE) {
                select_output();
                continue;
            }
            check(result, "decoder ProcessOutput");
            if (!decoded_sample) return std::nullopt;

            DecodedFrame frame;
            frame.width = format.width;
            frame.height = format.height;
            frame.pixel_format = output_format;
            // Hardware MFTs may attach updated mastering/color information to
            // individual samples. Prefer it over the initial media type, as a
            // stream can switch between SDR and HDR without changing geometry.
            frame.color = color_description(decoded_sample, format, &output_color);
            if (!copy_dxgi_output(decoded_sample, frame)) {
                ComPtr<IMFMediaBuffer> contiguous;
                check(decoded_sample->ConvertToContiguousBuffer(&contiguous),
                    "make decoder output contiguous");
                BYTE* source{};
                DWORD current{};
                check(contiguous->Lock(&source, nullptr, &current), "lock decoder output");
                try {
                    frame.nv12.assign(source, source + current);
                } catch (...) {
                    contiguous->Unlock();
                    throw;
                }
                contiguous->Unlock();
            }
            report_acceleration_mode();
            LONGLONG time{};
            if (SUCCEEDED(decoded_sample->GetSampleTime(&time))) frame.timestamp_100ns = time;
            UINT32 raw_stride{};
            if (frame.stride != 0) {
                // DXGI readback preserves the actual row pitch.
            } else if (output_type && SUCCEEDED(output_type->GetUINT32(
                    MF_MT_DEFAULT_STRIDE, &raw_stride))) {
                frame.stride = static_cast<std::int32_t>(raw_stride);
            } else {
                frame.stride = static_cast<std::int32_t>(format.width *
                    (output_format == PixelFormat::P010 ? 2U : 1U));
            }
            return frame;
        }
    }

    void handle_async_event(IMFMediaEvent* event,
        std::vector<DecodedFrame>& decoded, bool& drain_complete) {
        HRESULT event_status{};
        check(event->GetStatus(&event_status), "read asynchronous decoder event status");
        check(event_status, "asynchronous decoder event");
        MediaEventType type{MEUnknown};
        check(event->GetType(&type), "read asynchronous decoder event type");
        if (type == METransformNeedInput) {
            UINT32 stream_id{};
            if (SUCCEEDED(event->GetUINT32(MF_EVENT_MFT_INPUT_STREAM_ID, &stream_id)) &&
                stream_id != 0) throw std::runtime_error("decoder requested an unknown input stream");
            if (pending_input_requests != std::numeric_limits<std::uint32_t>::max())
                ++pending_input_requests;
        } else if (type == METransformHaveOutput) {
            if (auto output = receive_output()) decoded.push_back(std::move(*output));
        } else if (type == METransformDrainComplete) {
            drain_complete = true;
        }
    }

    bool get_async_event(DWORD flags, std::vector<DecodedFrame>& decoded,
        bool& drain_complete) {
        ComPtr<IMFMediaEvent> event;
        const auto result = event_generator->GetEvent(flags, &event);
        if (result == MF_E_NO_EVENTS_AVAILABLE) return false;
        check(result, "get asynchronous decoder event");
        handle_async_event(event.Get(), decoded, drain_complete);
        return true;
    }

    void pump_available_async_events(std::vector<DecodedFrame>& decoded) {
        bool drain_complete{};
        while (get_async_event(MF_EVENT_FLAG_NO_WAIT, decoded, drain_complete)) {}
    }

    void wait_for_async_input(std::vector<DecodedFrame>& decoded) {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
        bool drain_complete{};
        while (pending_input_requests == 0) {
            if (!get_async_event(MF_EVENT_FLAG_NO_WAIT, decoded, drain_complete)) {
                if (std::chrono::steady_clock::now() >= deadline)
                    throw std::runtime_error("asynchronous decoder input request timed out");
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }
    }

    std::vector<DecodedFrame> decode_once(std::span<const std::uint8_t> source,
        std::int64_t timestamp, std::int64_t duration, bool random_access) {
        auto encoded = length_prefixed_to_annex_b(source, format.nalu_length_size);
        if (!sent_parameter_sets) {
            auto parameter_sets = parameter_sets_annex_b(format);
            parameter_sets.insert(parameter_sets.end(), encoded.begin(), encoded.end());
            encoded = std::move(parameter_sets);
            sent_parameter_sets = true;
        }
        if (encoded.size() > std::numeric_limits<DWORD>::max())
            throw std::runtime_error("compressed video sample is too large");
        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateSample(&sample), "create decoder input sample");
        check(MFCreateMemoryBuffer(static_cast<DWORD>(encoded.size()), &buffer),
            "create decoder input buffer");
        BYTE* destination{};
        DWORD capacity{};
        check(buffer->Lock(&destination, &capacity, nullptr), "lock decoder input buffer");
        std::copy(encoded.begin(), encoded.end(), destination);
        buffer->Unlock();
        check(buffer->SetCurrentLength(static_cast<DWORD>(encoded.size())),
            "set decoder input length");
        check(sample->AddBuffer(buffer.Get()), "attach decoder input buffer");
        check(sample->SetSampleTime(timestamp), "set decoder sample time");
        check(sample->SetSampleDuration(std::max<std::int64_t>(1, duration)),
            "set decoder sample duration");
        if (random_access) check(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE),
            "mark decoder clean point");

        std::vector<DecodedFrame> decoded;
        if (asynchronous) {
            wait_for_async_input(decoded);
            --pending_input_requests;
            check(transform->ProcessInput(0, sample.Get(), 0),
                "asynchronous decoder ProcessInput");
            pump_available_async_events(decoded);
            return decoded;
        }
        auto input_result = transform->ProcessInput(0, sample.Get(), 0);
        while (input_result == MF_E_NOTACCEPTING) {
            auto pending = receive_output();
            if (!pending) check(input_result,
                "decoder ProcessInput (no output while not accepting)");
            decoded.push_back(std::move(*pending));
            input_result = transform->ProcessInput(0, sample.Get(), 0);
        }
        check(input_result, "decoder ProcessInput");
        while (auto output = receive_output()) decoded.push_back(std::move(*output));
        return decoded;
    }

    std::vector<DecodedFrame> drain() {
        if (!configured) return {};
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0),
            "end decoder stream");
        check(transform->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0),
            "drain decoder");
        std::vector<DecodedFrame> decoded;
        if (!asynchronous) {
            while (auto output = receive_output()) decoded.push_back(std::move(*output));
            return decoded;
        }
        bool drain_complete{};
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (!drain_complete) {
            if (!get_async_event(MF_EVENT_FLAG_NO_WAIT, decoded, drain_complete)) {
                if (std::chrono::steady_clock::now() >= deadline)
                    throw std::runtime_error("asynchronous decoder drain timed out");
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }
        return decoded;
    }

    void flush() {
        if (!transform) return;
        (void)transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        pending_input_requests = 0;
        if (asynchronous && event_generator) {
            for (;;) {
                ComPtr<IMFMediaEvent> event;
                if (event_generator->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event) ==
                    MF_E_NO_EVENTS_AVAILABLE) break;
                if (!event) break;
            }
            (void)transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
        sent_parameter_sets = false;
        waiting_for_random_access = true;
    }

    std::vector<DecodedFrame> decode(std::span<const std::uint8_t> sample,
        std::int64_t timestamp, std::int64_t duration) {
        if (!configured) throw std::logic_error("video decoder is not configured");
        const bool random_access = detail::is_random_access_sample(format, sample);
        if (waiting_for_random_access && !random_access) return {};
        if (random_access) waiting_for_random_access = false;
        try {
            return decode_once(sample, timestamp, duration, random_access);
        } catch (const std::exception& error) {
            const auto failed_name = selected_name;
            logging::write(std::format(
                "mf_decoder runtime_failure selected={} random_access={} reason={}",
                failed_name, random_access ? "true" : "false", error.what()));
            const auto next = candidate_index == std::numeric_limits<std::size_t>::max()
                ? candidates.size() : candidate_index + 1U;
            if (next >= candidates.size()) throw;
            select_first_working_candidate(next);
            logging::write(std::format("mf_decoder fallback from={} to={}",
                failed_name, selected_name));
            if (!random_access) {
                waiting_for_random_access = true;
                return {};
            }
            return decode_once(sample, timestamp, duration, true);
        }
    }
};

MediaFoundationVideoDecoder::MediaFoundationVideoDecoder(DecoderPreference preference) {
    const auto com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE)
        check(com_result, "CoInitializeEx");
    com_initialized_ = SUCCEEDED(com_result);
    try {
        ensure_media_foundation();
        impl_ = std::make_unique<Impl>(preference);
    } catch (...) {
        impl_.reset();
        if (com_initialized_) {
            CoUninitialize();
            com_initialized_ = false;
        }
        throw;
    }
}

MediaFoundationVideoDecoder::~MediaFoundationVideoDecoder() {
    impl_.reset();
    if (com_initialized_) CoUninitialize();
}

void MediaFoundationVideoDecoder::configure(const coremedia::FormatDescription& format,
    std::uint32_t fps_numerator, std::uint32_t fps_denominator) {
    impl_->configure(format, fps_numerator, fps_denominator);
}

std::vector<DecodedFrame> MediaFoundationVideoDecoder::decode(
    std::span<const std::uint8_t> sample, std::int64_t timestamp_100ns,
    std::int64_t duration_100ns) {
    return impl_->decode(sample, timestamp_100ns, duration_100ns);
}

std::vector<DecodedFrame> MediaFoundationVideoDecoder::drain() {
    return impl_->drain();
}

void MediaFoundationVideoDecoder::flush() {
    impl_->flush();
}

DecoderPreference MediaFoundationVideoDecoder::preference() const noexcept {
    return impl_->preference;
}

std::string_view MediaFoundationVideoDecoder::selected_decoder_name() const noexcept {
    return impl_->selected_name;
}

DecoderAcceleration MediaFoundationVideoDecoder::decoder_acceleration() const noexcept {
    return impl_->actual_acceleration;
}

bool MediaFoundationVideoDecoder::selected_decoder_is_hardware() const noexcept {
    return impl_->selected_hardware;
}

PixelFormat MediaFoundationVideoDecoder::output_pixel_format() const noexcept {
    return impl_->output_format;
}

} // namespace iPhoneMirror::media
