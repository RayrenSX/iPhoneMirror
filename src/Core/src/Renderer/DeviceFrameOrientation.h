#pragma once

#include "Renderer/D3D11PreviewRenderer.h"

#include <algorithm>
#include <optional>

namespace iPhoneMirror::renderer::detail {

// Coordinates in the unrotated artwork texture.
struct SensorBounds {
    float left, top, right, bottom;
    bool operator==(const SensorBounds&) const = default;
};

inline std::optional<SensorBounds> find_center_sensor(const DeviceFrameArtwork& art) noexcept {
    if (art.width == 0 || art.height == 0 ||
        art.screen_mask.size() != static_cast<std::size_t>(art.width) * art.height) return {};
    const auto sx = static_cast<int>(art.screen_x * art.width + 0.5F);
    const auto sy = static_cast<int>(art.screen_y * art.height + 0.5F);
    const auto sw = static_cast<int>(art.screen_width * art.width + 0.5F);
    const auto sh = static_cast<int>(art.screen_height * art.height + 0.5F);
    if (sx < 0 || sy < 0 || sw < 24 || sh < 48 ||
        sx + sw > static_cast<int>(art.width) || sy + sh > static_cast<int>(art.height)) return {};
    const auto opaque = [&](int x, int y) { return art.screen_mask[y * art.width + x] < 240; };
    const auto center = sx + sw / 2;
    const auto margin = sw / 9;
    const auto limit = sy + sh / 8;
    int left = center, right = center, top = limit, bottom = sy;
    for (int y = sy; y < limit; ++y) {
        // Ignore the top edge's antialiasing and any rounded corner pixels.
        if (!opaque(center, y) || opaque(sx + margin, y) || opaque(sx + sw - margin - 1, y)) continue;
        int row_left = center, row_right = center + 1;
        while (row_left > sx + margin && opaque(row_left - 1, y)) --row_left;
        while (row_right < sx + sw - margin && opaque(row_right, y)) ++row_right;
        left = std::min(left, row_left); right = std::max(right, row_right);
        top = std::min(top, y); bottom = y + 1;
    }
    if (bottom - top < std::max(3, sh / 100) || bottom >= limit || right - left < sw / 20) return {};
    return SensorBounds{static_cast<float>(left) / art.width, static_cast<float>(top) / art.height,
        static_cast<float>(right) / art.width, static_cast<float>(bottom) / art.height};
}

// Compare the expected notch/island and its nearby screen pixels at the two
// possible positions. A uniformly dark or symmetric scene is ambiguous.
inline std::optional<int> landscape_sensor_turns(const media::DecodedFrame& frame,
    const DeviceFrameArtwork& art, SensorBounds sensor) noexcept {
    if (frame.width < 64 || frame.height < 32 || frame.stride <= 0 ||
        frame.pixel_format != media::PixelFormat::Nv12 || frame.color.is_hdr()) return {};
    const auto stride = static_cast<std::size_t>(frame.stride);
    if (stride < frame.width || frame.nv12.size() / stride < frame.height) return {};
    const auto left = (sensor.left - art.screen_x) / art.screen_width;
    const auto right = (sensor.right - art.screen_x) / art.screen_width;
    const auto inner = (sensor.bottom - art.screen_y) / art.screen_height;
    const auto depth = ((sensor.top + sensor.bottom) * 0.5F - art.screen_y) / art.screen_height;
    const auto center = (left + right) * 0.5F;
    const auto radius = (right - left) * 0.5F;
    const auto resembles_sensor = [&](bool left_edge) {
        const auto sample = [&](float x, float y) {
            if (!left_edge) { x = 1.0F - x; y = 1.0F - y; }
            const auto row = std::min(frame.height - 1,
                static_cast<unsigned>(std::clamp(y, 0.0F, 1.0F) * frame.height));
            const auto col = std::min(frame.width - 1,
                static_cast<unsigned>(std::clamp(x, 0.0F, 1.0F) * frame.width));
            return frame.nv12[row * stride + col];
        };
        // The inward side and at least one end must contrast with the cutout.
        // iOS can put an activity indicator immediately past the other end.
        for (const float offset : {-0.55F, 0.0F, 0.55F}) {
            if (sample(depth, center + offset * radius) > 24 ||
                sample(inner + 0.015F, center + offset * radius) < 50) return false;
        }
        return sample(depth, center - radius - 0.025F) >= 50 ||
            sample(depth, center + radius + 0.025F) >= 50;
    };
    const bool left_edge = resembles_sensor(true);
    const bool right_edge = resembles_sensor(false);
    if (left_edge == right_edge) return {};
    return left_edge ? 3 : 1;
}

class WirelessLandscapeOrientation {
public:
    std::optional<int> observe(const media::DecodedFrame& frame,
        const DeviceFrameArtwork& art, SensorBounds sensor) noexcept {
        if (run_ != frame.wireless_landscape_run || sensor_ != sensor) {
            *this = {};
            run_ = frame.wireless_landscape_run;
            sensor_ = sensor;
        }
        if (last_timestamp_ == frame.timestamp_100ns) return confirmed_;
        last_timestamp_ = frame.timestamp_100ns;
        const auto side = landscape_sensor_turns(frame, art, sensor);
        if (!side || side == confirmed_) {
            candidate_.reset();
            return confirmed_;
        }
        if (candidate_ != side || frame.timestamp_100ns < candidate_since_) {
            candidate_ = side;
            candidate_since_ = frame.timestamp_100ns;
            candidate_frames_ = 1;
            return confirmed_;
        }
        // New landscape needs two observations. Reversing an already known
        // side needs sustained evidence because screen content can mimic it.
        // A missing, ambiguous, or unchanged cutout never clears the lock.
        constexpr std::int64_t ReverseEvidenceSpan = 1'000'000; // 100 ms in 100-ns ticks
        ++candidate_frames_;
        if (!confirmed_ || (candidate_frames_ >= 3 &&
            frame.timestamp_100ns - candidate_since_ >= ReverseEvidenceSpan)) {
            confirmed_ = side;
            candidate_.reset();
        }
        return confirmed_;
    }

    std::optional<int> confirmed() const noexcept { return confirmed_; }

private:
    std::uint64_t run_{};
    SensorBounds sensor_{};
    std::int64_t last_timestamp_{};
    std::int64_t candidate_since_{};
    unsigned candidate_frames_{};
    std::optional<int> candidate_;
    std::optional<int> confirmed_;
};

} // namespace iPhoneMirror::renderer::detail
