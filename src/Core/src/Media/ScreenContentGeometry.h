#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <chrono>
#include <cstdint>
#include <optional>

namespace iPhoneMirror::media {

struct ScreenContentRect {
    std::uint32_t x{}, y{}, width{}, height{};
    bool operator==(const ScreenContentRect&) const = default;
};

// Conservative, session-local fallback for a previously observed rectangular
// screen becoming a square black canvas. This is pixel evidence, not protocol
// aperture metadata; it must never be used to infer a new device orientation.
class ScreenContentGeometry {
public:
    void configure(std::uint32_t panel_width, std::uint32_t panel_height) noexcept;
    // Retains the configured physical panel, discards all session evidence.
    void reset() noexcept;
    // Call once on each ORIGINAL frame, before publishing it. A returned rect
    // identifies an exact copy in the original canvas; nullopt leaves it whole.
    // GPU frames are never read back. received_at must advance monotonically.
    [[nodiscard]] std::optional<ScreenContentRect> process(DecodedFrame& frame) noexcept;

private:
    void clear_candidate() noexcept;
    std::uint32_t panel_short_{}, panel_long_{};
    std::uint32_t reference_short_{}, reference_long_{}, reference_count_{};
    bool reference_ready_{};
    std::optional<ScreenContentRect> candidate_;
    std::uint32_t candidate_canvas_{}, candidate_count_{};
    bool candidate_confirmed_{};
    std::chrono::steady_clock::time_point candidate_since_{}, last_received_{};
};

} // namespace iPhoneMirror::media
