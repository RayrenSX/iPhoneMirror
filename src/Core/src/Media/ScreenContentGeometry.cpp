#include "Media/ScreenContentGeometry.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <emmintrin.h>
#include <limits>
#include <vector>

namespace iPhoneMirror::media {
namespace {

struct Layout {
    std::size_t stride{}, allocated_height{}, uv_offset{};
    std::uint32_t component{};
    unsigned black_min{}, black_max{}, neutral_min{}, neutral_max{}, signal_min{};
};

std::optional<Layout> layout(const DecodedFrame& frame) noexcept {
    if (frame.gpu_frame || frame.nv12.empty() || frame.stride <= 0 ||
        frame.width < 64 || frame.height < 64 ||
        frame.width > detail::MaxDecodedVideoDimension || frame.height > detail::MaxDecodedVideoDimension ||
        frame.color.is_hdr() ||
        (frame.pixel_format != PixelFormat::Nv12 && frame.pixel_format != PixelFormat::P010) ||
        (frame.color.range != coremedia::ColorRange::Full &&
         frame.color.range != coremedia::ColorRange::Limited)) return std::nullopt;
    Layout result;
    result.component = frame.pixel_format == PixelFormat::P010 ? 2U : 1U;
    result.stride = static_cast<std::size_t>(frame.stride);
    const auto minimum_stride = static_cast<std::size_t>((frame.width + 1U) & ~1U) * result.component;
    if (result.stride < minimum_stride || result.stride % result.component != 0 ||
        frame.nv12.size() > detail::MaxDxgiReadbackBytes || frame.nv12.size() % result.stride != 0)
        return std::nullopt;
    const auto rows = frame.nv12.size() / result.stride;
    result.allocated_height = rows * 2U / 3U;
    if (result.allocated_height < frame.height ||
        result.allocated_height > static_cast<std::size_t>(frame.height) + detail::MaxDxgiAllocationPadding ||
        result.allocated_height + (result.allocated_height + 1U) / 2U != rows)
        return std::nullopt;
    result.uv_offset = result.stride * result.allocated_height;
    const auto scale = result.component == 2U ? 4U : 1U;
    const bool limited = frame.color.range == coremedia::ColorRange::Limited;
    result.black_min = limited ? 12U * scale : 0U;
    result.black_max = (limited ? 20U : 4U) * scale;
    result.neutral_min = 124U * scale;
    result.neutral_max = 132U * scale;
    result.signal_min = (limited ? 48U : 32U) * scale;
    return result;
}

unsigned sample(const std::uint8_t* row, std::uint32_t x, const Layout& layout) noexcept {
    if (layout.component == 1) return row[x];
    const auto offset = static_cast<std::size_t>(x) * 2U;
    return (static_cast<unsigned>(row[offset]) |
        (static_cast<unsigned>(row[offset + 1U]) << 8U)) >> 6U;
}

bool samples_in_range(const std::uint8_t* row, std::uint32_t begin, std::uint32_t end,
    const Layout& layout, unsigned low, unsigned high) noexcept {
    auto x = begin;
    if (layout.component == 1U) {
        const auto minimum = _mm_set1_epi8(static_cast<char>(low));
        const auto maximum = _mm_set1_epi8(static_cast<char>(high));
        for (; x + 16U <= end; x += 16U) {
            const auto values = _mm_loadu_si128(reinterpret_cast<const __m128i*>(row + x));
            const auto bounded = _mm_min_epu8(_mm_max_epu8(values, minimum), maximum);
            if (_mm_movemask_epi8(_mm_cmpeq_epi8(values, bounded)) != 0xffff) return false;
        }
    } else {
        const auto minimum = _mm_set1_epi16(static_cast<short>(low));
        const auto maximum = _mm_set1_epi16(static_cast<short>(high));
        for (; x + 8U <= end; x += 8U) {
            // P010 uses the upper ten bits. After shifting, all values fit
            // positive int16 lanes, so signed SSE2 comparisons are exact.
            const auto packed = _mm_loadu_si128(reinterpret_cast<const __m128i*>(
                row + static_cast<std::size_t>(x) * 2U));
            const auto values = _mm_srli_epi16(packed, 6);
            const auto outside = _mm_or_si128(_mm_cmpgt_epi16(minimum, values),
                _mm_cmpgt_epi16(values, maximum));
            if (_mm_movemask_epi8(outside) != 0) return false;
        }
    }
    // A vector is loaded only when every component lies inside this exact
    // discarded range. Check the tail individually without crossing a row,
    // entering retained content or reading allocation padding.
    for (; x < end; ++x) {
        const auto value = sample(row, x, layout);
        if (value < low || value > high) return false;
    }
    return true;
}

bool outside_black(const DecodedFrame& frame, const Layout& layout,
    const ScreenContentRect& rect) noexcept {
    // Check EVERY discarded luma pixel and chroma pair. Padding at the end of
    // an allocated texture is excluded; it is not visible screen content.
    for (std::uint32_t y = 0; y < frame.height; ++y) {
        const auto* row = frame.nv12.data() + y * layout.stride;
        const auto black = [&](std::uint32_t begin, std::uint32_t end) {
            return samples_in_range(row, begin, end, layout, layout.black_min, layout.black_max);
        };
        if (y < rect.y || y >= rect.y + rect.height) {
            if (!black(0, frame.width)) return false;
        } else if (!black(0, rect.x) || !black(rect.x + rect.width, frame.width)) return false;
    }
    for (std::uint32_t y = 0; y < frame.height / 2U; ++y) {
        const auto* row = frame.nv12.data() + layout.uv_offset + y * layout.stride;
        const auto neutral = [&](std::uint32_t begin, std::uint32_t end) {
            // U and V use the same neutral range; validate every component in
            // each contiguous run rather than handling pairs separately.
            return samples_in_range(row, begin, end, layout, layout.neutral_min, layout.neutral_max);
        };
        if (y * 2U < rect.y || y * 2U >= rect.y + rect.height) {
            if (!neutral(0, frame.width)) return false;
        } else if (!neutral(0, rect.x) || !neutral(rect.x + rect.width, frame.width)) return false;
    }
    return true;
}

bool supported_inner_edges(const DecodedFrame& frame, const Layout& layout,
    const ScreenContentRect& rect) noexcept {
    // Outward chroma alignment can retain up to three black pixels per side.
    // Look through a six-pixel inner band, then require signal along every
    // edge and spread over it. Sparse text, dark pages and rotating rounded
    // rectangles must not become evidence for a crop.
    constexpr std::uint32_t band = 6;
    for (unsigned edge = 0; edge < 4; ++edge) {
        const bool horizontal = edge < 2;
        const auto length = horizontal ? rect.width : rect.height;
        std::array<std::uint32_t, 4> signals{}, totals{};
        std::uint32_t active{};
        for (std::uint32_t along = 0; along < length; ++along) {
            bool signal{};
            for (std::uint32_t depth = 0; depth < band; ++depth) {
                const auto x = horizontal ? rect.x + along :
                    edge == 2 ? rect.x + depth : rect.x + rect.width - 1U - depth;
                const auto y = !horizontal ? rect.y + along :
                    edge == 0 ? rect.y + depth : rect.y + rect.height - 1U - depth;
                const auto* row = frame.nv12.data() + y * layout.stride;
                if (sample(row, x, layout) >= layout.signal_min) signal = true;
            }
            const auto section = std::min<std::uint32_t>(3, along * 4U / length);
            ++totals[section];
            if (signal) { ++active; ++signals[section]; }
        }
        if (active * 2U < length) return false;
        // Each quarter must have substantial evidence. This also prevents an
        // off-centre or transiently tilted picture from passing at one end.
        for (unsigned section = 0; section < 4; ++section)
            if (signals[section] * 2U < totals[section]) return false;
    }
    return true;
}

std::array<ScreenContentRect, 2> candidates(std::uint32_t canvas,
    std::uint32_t reference_short, std::uint32_t reference_long) noexcept {
    const auto ideal_short = static_cast<double>(canvas) * reference_short / reference_long;
    const auto ideal_start = (canvas - ideal_short) * 0.5;
    const auto start = static_cast<std::uint32_t>(std::floor(ideal_start / 2.0)) * 2U;
    const auto end = std::min(canvas,
        static_cast<std::uint32_t>(std::ceil((canvas - ideal_start) / 2.0)) * 2U);
    return {{{start, 0, end - start, canvas}, {0, start, canvas, end - start}}};
}

bool copy_crop(DecodedFrame& frame, const Layout& layout, const ScreenContentRect& rect) {
    const auto stride = static_cast<std::size_t>(rect.width) * layout.component;
    const auto y_bytes = stride * rect.height;
    std::vector<std::uint8_t> pixels(y_bytes + stride * (rect.height / 2U));
    for (std::uint32_t y = 0; y < rect.height; ++y)
        std::memcpy(pixels.data() + y * stride,
            frame.nv12.data() + (y + rect.y) * layout.stride + rect.x * layout.component, stride);
    for (std::uint32_t y = 0; y < rect.height / 2U; ++y)
        std::memcpy(pixels.data() + y_bytes + y * stride,
            frame.nv12.data() + layout.uv_offset + (y + rect.y / 2U) * layout.stride +
                rect.x * layout.component, stride);
    frame.width = rect.width;
    frame.height = rect.height;
    frame.stride = static_cast<std::int32_t>(stride);
    frame.nv12 = std::move(pixels);
    frame.gpu_frame.reset();
    return true;
}

} // namespace

void ScreenContentGeometry::configure(std::uint32_t width, std::uint32_t height) noexcept {
    const auto short_edge = std::min(width, height), long_edge = std::max(width, height);
    if (short_edge == panel_short_ && long_edge == panel_long_) return;
    panel_short_ = short_edge;
    panel_long_ = long_edge;
    reset();
}

void ScreenContentGeometry::clear_candidate() noexcept {
    candidate_.reset();
    candidate_canvas_ = candidate_count_ = 0;
    candidate_confirmed_ = false;
    candidate_since_ = {};
}

void ScreenContentGeometry::reset() noexcept {
    reference_short_ = reference_long_ = reference_count_ = 0;
    reference_ready_ = false;
    last_received_ = {};
    clear_candidate();
}

std::optional<ScreenContentRect> ScreenContentGeometry::process(DecodedFrame& frame) noexcept {
    try {
        const auto storage = layout(frame);
        if (!storage || panel_short_ < 64 || panel_long_ <= panel_short_ ||
            panel_long_ > detail::MaxDecodedVideoDimension ||
            frame.received_at == std::chrono::steady_clock::time_point{}) {
            clear_candidate();
            reference_count_ = 0;
            return std::nullopt;
        }
        const bool advancing = last_received_ == std::chrono::steady_clock::time_point{} ||
            frame.received_at > last_received_;
        last_received_ = frame.received_at;
        if (!advancing) {
            clear_candidate();
            reference_count_ = 0;
            return std::nullopt;
        }
        if (frame.width != frame.height) {
            clear_candidate();
            const auto short_edge = std::min(frame.width, frame.height);
            const auto long_edge = std::max(frame.width, frame.height);
            const auto ratio_error = std::abs(static_cast<double>(short_edge) * panel_long_ /
                (static_cast<double>(long_edge) * panel_short_) - 1.0);
            if (ratio_error > 0.01) {
                // A different rectangular presentation can be external video
                // rather than this physical screen. Revoke the old reference
                // until three matching original frames establish it again.
                reference_count_ = 0;
                reference_ready_ = false;
                return std::nullopt;
            }
            if (reference_short_ != short_edge || reference_long_ != long_edge) {
                reference_short_ = short_edge;
                reference_long_ = long_edge;
                reference_ready_ = false;
                reference_count_ = 0;
            }
            reference_count_ = std::min(3U, reference_count_ + 1U);
            if (reference_count_ >= 3) reference_ready_ = true;
            return std::nullopt;
        }
        // Squares are never fed back as reference geometry, including squares
        // previously cropped by this helper. The caller supplies originals.
        reference_count_ = 0;
        if (!reference_ready_ || (frame.width & 1U) != 0) {
            clear_candidate();
            return std::nullopt;
        }
        std::optional<ScreenContentRect> accepted;
        for (const auto& rect : candidates(frame.width, reference_short_, reference_long_)) {
            const bool confirmed = candidate_confirmed_ && candidate_ && *candidate_ == rect &&
                candidate_canvas_ == frame.width;
            if (rect.width < 64 || rect.height < 64 ||
                (rect.width == frame.width && rect.height == frame.height) ||
                !outside_black(frame, *storage, rect) ||
                (!confirmed && !supported_inner_edges(frame, *storage, rect))) continue;
            // A verified screen can legitimately become dark. Preserve that
            // exact region while every discarded Y/UV sample is still black;
            // any new signal outside it revokes the decision on this frame.
            if (accepted) {
                clear_candidate();
                return std::nullopt;
            }
            accepted = rect;
        }
        if (!accepted) {
            clear_candidate();
            return std::nullopt;
        }
        if (candidate_ != accepted || candidate_canvas_ != frame.width) {
            clear_candidate();
            candidate_ = accepted;
            candidate_canvas_ = frame.width;
            candidate_since_ = frame.received_at;
        }
        candidate_count_ = std::min(3U, candidate_count_ + 1U);
        if (candidate_count_ < 3 || frame.received_at - candidate_since_ < std::chrono::milliseconds(100))
            return std::nullopt;
        const auto rect = *accepted;
        if (!copy_crop(frame, *storage, rect)) {
            clear_candidate();
            return std::nullopt;
        }
        candidate_confirmed_ = true;
        return rect;
    } catch (...) {
        clear_candidate();
        return std::nullopt;
    }
}

} // namespace iPhoneMirror::media
