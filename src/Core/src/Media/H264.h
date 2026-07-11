#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace iPhoneMirror::h264 {

// QuickTime CMSampleBuffer block data is AVCC: each NAL unit has a big-endian
// length prefix. Media Foundation/streaming outputs commonly accept Annex-B.
[[nodiscard]] std::vector<std::uint8_t> avcc_to_annex_b(
    std::span<const std::uint8_t> sample,
    std::uint8_t length_field_bytes = 4);

[[nodiscard]] bool is_keyframe_avcc(
    std::span<const std::uint8_t> sample,
    std::uint8_t length_field_bytes = 4);

} // namespace iPhoneMirror::h264

