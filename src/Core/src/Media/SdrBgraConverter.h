#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <algorithm>
#include <cstring>
#include <emmintrin.h>
#include <span>

namespace iPhoneMirror::media::detail {

// SSE2 is part of the Windows x64 baseline. Keep SDR conversion in byte
// units and convert four pixels at a time, without per-pixel function calls.
// HDR and wide-gamut sources continue through convert_yuv_to_sdr.
#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable: 4324) // intentional 16-byte SIMD object alignment
#endif
// The SSE vectors require 16-byte alignment. State it on the type itself so
// MSVC does not warn at every stack construction that the object is padded.
class alignas(16) SdrBgraConverter {
public:
    static bool supports(const DecodedFrame& frame) noexcept {
        return !frame.color.is_hdr() &&
            (frame.color.primaries == coremedia::ColorPrimaries::Bt709 ||
             frame.color.primaries == coremedia::ColorPrimaries::Unspecified);
    }

    explicit SdrBgraConverter(const DecodedFrame& frame) noexcept
        : p010_(frame.pixel_format == PixelFormat::P010) {
        const auto p = yuv_conversion_parameters(frame.pixel_format,
            frame.color.range, frame.color.matrix);
        const double denominator = p010_ ? 65535.0 : 255.0;
        y_offset_ = broadcast(p.y_offset * denominator);
        c_offset_ = broadcast(p.chroma_offset * denominator);
        y_scale_ = broadcast(p.y_scale * 255.0 / denominator);
        red_cr_ = broadcast(p.red_cr * p.chroma_scale * 255.0 / denominator);
        green_cb_ = broadcast(p.green_cb * p.chroma_scale * 255.0 / denominator);
        green_cr_ = broadcast(p.green_cr * p.chroma_scale * 255.0 / denominator);
        blue_cb_ = broadcast(p.blue_cb * p.chroma_scale * 255.0 / denominator);
    }

    // Empty x_map means a contiguous row; otherwise it contains one source
    // index per output pixel, preserving the API's nearest-neighbour scaling.
    void row(const std::uint8_t* luma, const std::uint8_t* chroma,
        std::uint8_t* output, std::uint32_t width,
        std::span<const std::uint32_t> x_map = {}) const noexcept {
        std::uint32_t x = 0;
        if (!p010_ && x_map.empty()) {
            const auto zero = _mm_setzero_si128();
            const auto mask = _mm_set1_epi16(255);
            for (; x + 8 <= width; x += 8) {
                const auto y = _mm_unpacklo_epi8(_mm_loadl_epi64(
                    reinterpret_cast<const __m128i*>(luma + x)), zero);
                const auto uv = _mm_loadl_epi64(
                    reinterpret_cast<const __m128i*>(chroma + x));
                const auto u = _mm_and_si128(uv, mask);
                const auto v = _mm_srli_epi16(uv, 8);
                // Each chroma pair belongs to two adjacent luma samples.
                const auto u2 = _mm_unpacklo_epi16(u, u);
                const auto v2 = _mm_unpacklo_epi16(v, v);
                store(output + x * 4U,
                    _mm_cvtepi32_ps(_mm_unpacklo_epi16(y, zero)),
                    _mm_cvtepi32_ps(_mm_unpacklo_epi16(u2, zero)),
                    _mm_cvtepi32_ps(_mm_unpacklo_epi16(v2, zero)));
                store(output + (x + 4U) * 4U,
                    _mm_cvtepi32_ps(_mm_unpackhi_epi16(y, zero)),
                    _mm_cvtepi32_ps(_mm_unpackhi_epi16(u2, zero)),
                    _mm_cvtepi32_ps(_mm_unpackhi_epi16(v2, zero)));
            }
        }
        for (; x < width; x += 4) {
            alignas(16) float ys[4], us[4], vs[4];
            const auto count = std::min<std::uint32_t>(4, width - x);
            for (std::uint32_t lane = 0; lane < 4; ++lane) {
                const auto target = x + std::min(lane, count - 1U);
                const auto source = x_map.empty() ? target : x_map[target];
                const auto uv = source & ~1U;
                ys[lane] = sample(luma, source);
                us[lane] = sample(chroma, uv);
                vs[lane] = sample(chroma, uv + 1U);
            }
            alignas(16) std::uint8_t tail[16];
            auto* destination = count == 4 ? output + x * 4U : tail;
            store(destination, _mm_load_ps(ys), _mm_load_ps(us), _mm_load_ps(vs));
            if (count != 4) std::memcpy(output + x * 4U, tail, count * 4U);
        }
    }

private:
    static __m128 broadcast(double value) noexcept {
        return _mm_set1_ps(static_cast<float>(value));
    }

    float sample(const std::uint8_t* row, std::uint32_t index) const noexcept {
        if (!p010_) return static_cast<float>(row[index]);
        std::uint16_t value;
        std::memcpy(&value, row + static_cast<std::size_t>(index) * 2U, 2);
        return static_cast<float>(value);
    }

    static __m128i bytes(__m128 value) noexcept {
        value = _mm_min_ps(_mm_max_ps(value, _mm_setzero_ps()), broadcast(255));
        return _mm_cvttps_epi32(_mm_add_ps(value, broadcast(0.5)));
    }

    void store(std::uint8_t* output, __m128 y, __m128 cb, __m128 cr) const noexcept {
        y = _mm_mul_ps(_mm_max_ps(_mm_sub_ps(y, y_offset_), _mm_setzero_ps()), y_scale_);
        cb = _mm_sub_ps(cb, c_offset_);
        cr = _mm_sub_ps(cr, c_offset_);
        const auto r = bytes(_mm_add_ps(y, _mm_mul_ps(cr, red_cr_)));
        const auto g = bytes(_mm_add_ps(_mm_add_ps(y, _mm_mul_ps(cb, green_cb_)),
            _mm_mul_ps(cr, green_cr_)));
        const auto b = bytes(_mm_add_ps(y, _mm_mul_ps(cb, blue_cb_)));
        const auto bgra = _mm_or_si128(_mm_or_si128(b, _mm_slli_epi32(g, 8)),
            _mm_or_si128(_mm_slli_epi32(r, 16), _mm_set1_epi32(-16777216)));
        _mm_storeu_si128(reinterpret_cast<__m128i*>(output), bgra);
    }

    bool p010_;
    __m128 y_offset_, c_offset_, y_scale_, red_cr_, green_cb_, green_cr_, blue_cb_;
};
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

} // namespace iPhoneMirror::media::detail
