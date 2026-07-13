#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <cstdint>
#include <memory>
#include <string>

namespace iPhoneMirror::capture {

enum class State : std::int32_t {
    Idle = 0,
    ActivatingUsb = 1,
    WaitingForDevice = 2,
    Handshaking = 3,
    Streaming = 4,
    Stopping = 5,
    Stopped = 6,
    Error = 7,
};

enum class UsbProjectionMode : std::uint32_t {
    Demo = 0,
    AirPlay = 1,
    Aisi = 2,
};

struct Snapshot {
    State state{State::Idle};
    std::uint32_t width{};
    std::uint32_t height{};
    double fps{};
    double latency_ms{};
    std::uint64_t video_frames{};
    std::uint64_t audio_packets{};
    std::uint32_t audio_sample_rate{};
    std::uint32_t audio_channels{};
    std::wstring message{L"Idle"};
};

struct CapturePreferences {
    std::uint32_t render_max_width{};
    std::uint32_t render_max_height{};
    std::uint32_t target_fps{60};
    bool play_audio{true};
    float audio_volume{1.0F};
    std::uint32_t usb_requested_width{};
    std::uint32_t usb_requested_height{};
    UsbProjectionMode usb_projection_mode{UsbProjectionMode::Demo};
};

class ICaptureSession {
public:
    virtual ~ICaptureSession() = default;
    virtual void stop() noexcept = 0;
    [[nodiscard]] virtual Snapshot snapshot() const = 0;
    [[nodiscard]] virtual std::int64_t latest_frame_timestamp() const = 0;
    [[nodiscard]] virtual std::shared_ptr<const media::DecodedFrame> latest_frame() const = 0;
    [[nodiscard]] virtual std::shared_ptr<const media::DecodedFrame> next_render_frame() = 0;
    virtual void set_audio_enabled(bool enabled) noexcept = 0;
    virtual void set_audio_volume(float volume) noexcept = 0;
    virtual void set_target_fps(std::uint32_t target_fps) noexcept = 0;
    [[nodiscard]] virtual std::uint32_t target_fps() const noexcept = 0;
    virtual void request_display_orientation(bool landscape) noexcept = 0;
};

} // namespace iPhoneMirror::capture
