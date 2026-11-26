#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <array>
#include <memory>

namespace iPhoneMirror::capture {

// Access under the capture's video mutex. Keep a bounded history shared by
// every preview. Output readers continue to use the newest frame.
class PreviewAudioSync {
public:
    void clear() noexcept { frames_ = {}; next_ = count_ = 0; }

    void publish(const std::shared_ptr<const media::DecodedFrame>& frame, bool enabled) noexcept {
        if (!enabled) return;
        // The hardware decoder owns sixteen shared textures. Leave room for
        // decode and presentation as well as simultaneous output readers.
        if (count_ != 0) {
            const auto& previous = frames_[(next_ + frames_.size() - 1) % frames_.size()];
            if (frame->width != previous->width || frame->height != previous->height) clear();
        }
        frames_[next_] = frame;
        next_ = (next_ + 1) % frames_.size();
        count_ = std::min(count_ + 1, frames_.size());
    }

    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> select(
        const std::shared_ptr<const media::DecodedFrame>& latest, std::uint32_t delay_ms,
        std::chrono::steady_clock::time_point now = std::chrono::steady_clock::now()) const noexcept {
        if (!latest || count_ == 0 || delay_ms == 0) return latest;
        const auto due = now - std::chrono::milliseconds(delay_ms);
        if (latest->received_at <= due) return latest;
        for (std::size_t age = 1; age <= count_; ++age) {
            const auto& frame = frames_[(next_ + frames_.size() - age) % frames_.size()];
            if (frame->received_at <= due || age == count_) return frame;
        }
        return latest;
    }

private:
    std::array<std::shared_ptr<const media::DecodedFrame>, 8> frames_{};
    std::size_t next_{};
    std::size_t count_{};
};

} // namespace iPhoneMirror::capture
