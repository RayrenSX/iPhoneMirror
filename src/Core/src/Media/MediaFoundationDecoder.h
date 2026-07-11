#pragma once

#include "Media/CoreMedia.h"

#include <cstdint>
#include <chrono>
#include <optional>
#include <span>
#include <vector>
#include <wrl/client.h>

struct IMFTransform;
struct IMFMediaType;
struct IMFDXGIDeviceManager;
struct ID3D11Device;

namespace iPhoneMirror::media {

struct DecodedFrame {
    std::uint32_t width{};
    std::uint32_t height{};
    std::int32_t stride{};
    std::int64_t timestamp_100ns{};
    // Wall-clock moment when the compressed sample carrying this picture was
    // received from USB. This lets the presentation path report the actual
    // receive-to-display delay instead of just the duration of one MFT call.
    std::chrono::steady_clock::time_point received_at{};
    std::vector<std::uint8_t> nv12;
};

class MediaFoundationH264Decoder {
public:
    MediaFoundationH264Decoder();
    ~MediaFoundationH264Decoder();
    MediaFoundationH264Decoder(const MediaFoundationH264Decoder&) = delete;
    MediaFoundationH264Decoder& operator=(const MediaFoundationH264Decoder&) = delete;

    void configure(const coremedia::FormatDescription& format, std::uint32_t fps_numerator = 60,
        std::uint32_t fps_denominator = 1);
    [[nodiscard]] std::vector<DecodedFrame> decode(std::span<const std::uint8_t> avcc_sample,
        std::int64_t timestamp_100ns, std::int64_t duration_100ns);
    [[nodiscard]] std::vector<DecodedFrame> drain();
    void flush();

private:
    IMFTransform* transform_{};
    IMFMediaType* output_type_{};
    Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> d3d_manager_;
    Microsoft::WRL::ComPtr<ID3D11Device> d3d_device_;
    coremedia::FormatDescription format_;
    bool configured_{};
    bool sent_parameter_sets_{};

    void select_nv12_output();
    [[nodiscard]] std::optional<DecodedFrame> receive_output();
};

} // namespace iPhoneMirror::media
