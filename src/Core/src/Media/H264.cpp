#include "Media/H264.h"

#include <limits>
#include <stdexcept>

namespace iPhoneMirror::h264 {
namespace {

std::uint32_t read_length(std::span<const std::uint8_t> bytes, std::uint8_t width) {
    std::uint32_t result{};
    for (std::uint8_t index = 0; index < width; ++index) result = (result << 8U) | bytes[index];
    return result;
}

template <typename Visitor>
void visit_nalus(std::span<const std::uint8_t> sample, std::uint8_t width, Visitor&& visitor) {
    if (width < 1 || width > 4) throw std::invalid_argument("AVCC length field must be 1..4 bytes");
    std::size_t offset{};
    while (offset < sample.size()) {
        if (sample.size() - offset < width) throw std::runtime_error("truncated AVCC NAL length");
        const auto length = read_length(sample.subspan(offset, width), width);
        offset += width;
        if (length == 0 || length > sample.size() - offset) throw std::runtime_error("invalid AVCC NAL length");
        visitor(sample.subspan(offset, length));
        offset += length;
    }
}

} // namespace


std::vector<std::uint8_t> avcc_to_annex_b(std::span<const std::uint8_t> sample, std::uint8_t length_field_bytes) {
    std::vector<std::uint8_t> output;
    output.reserve(sample.size() + 32);
    visit_nalus(sample, length_field_bytes, [&output](std::span<const std::uint8_t> nalu) {
        output.insert(output.end(), {0, 0, 0, 1});
        output.insert(output.end(), nalu.begin(), nalu.end());
    });
    return output;
}

bool is_keyframe_avcc(std::span<const std::uint8_t> sample, std::uint8_t length_field_bytes) {
    bool keyframe{};
    visit_nalus(sample, length_field_bytes, [&keyframe](std::span<const std::uint8_t> nalu) {
        const auto type = nalu.front() & 0x1fU;
        if (type == 5) keyframe = true;
    });
    return keyframe;
}

} // namespace iPhoneMirror::h264

