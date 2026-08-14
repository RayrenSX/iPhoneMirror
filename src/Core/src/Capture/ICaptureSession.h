#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

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

enum class FailureKind : std::int32_t {
    None = 0,
    UsbConnection = 1,
    SessionCreation = 2,
    Driver = 3,
    VideoStream = 4,
    InvalidVideoDimensions = 5,
    NoVideoFrames = 6,
    SystemClosed = 7,
    DeviceDisconnected = 8,
    Timeout = 9,
    ExistingSession = 10,
    ChildProcessExited = 11,
    Unknown = 100,
};

enum class FailureStage : std::int32_t {
    None = 0,
    UsbPreflight = 1,
    UsbActivation = 2,
    DeviceReenumeration = 3,
    InterfaceOpen = 4,
    QuickTimeHandshake = 5,
    VideoStream = 6,
    Decoder = 7,
    SessionTeardown = 8,
    DeviceDiscovery = 9,
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
    FailureKind failure_kind{FailureKind::None};
    FailureStage failure_stage{FailureStage::None};
    std::int32_t error_code{};
    std::wstring message{L"Idle"};
};

struct AudioPacket {
    std::uint64_t sequence{};
    std::uint32_t sample_rate{};
    std::uint16_t channels{};
    std::uint16_t bits_per_sample{};
    std::vector<std::uint8_t> pcm;
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
    media::DecoderPreference decoder_preference{media::DecoderPreference::Auto};
    media::ColorOutputPreference color_output_preference{
        media::ColorOutputPreference::ForceSdrToneMap};
    float brightness{};
    float contrast{1.0F};
    float saturation{1.0F};
    float gamma{1.0F};
};

enum class DecoderSwitchPhase : std::uint32_t {
    Applied = 0,
    Pending = 1,
    Failed = 2,
};

enum class DecoderRuntimeMode : std::uint32_t {
    Unknown = 0,
    Hardware = 1,
    Software = 2,
    External = 3,
};

struct DecoderSwitchStatus {
    media::DecoderPreference requested{media::DecoderPreference::Auto};
    media::DecoderPreference applied{media::DecoderPreference::Auto};
    std::uint64_t requested_generation{1};
    std::uint64_t applied_generation{1};
    DecoderSwitchPhase phase{DecoderSwitchPhase::Applied};
    DecoderRuntimeMode runtime_mode{DecoderRuntimeMode::Unknown};
};

class ICaptureSession {
public:
    virtual ~ICaptureSession() = default;
    virtual void stop() noexcept = 0;
    [[nodiscard]] virtual Snapshot snapshot() const = 0;
    [[nodiscard]] virtual std::int64_t latest_frame_timestamp() const = 0;
    [[nodiscard]] virtual std::shared_ptr<const media::DecodedFrame> latest_frame() const = 0;
    [[nodiscard]] virtual std::shared_ptr<const media::DecodedFrame> next_render_frame() = 0;
    [[nodiscard]] virtual std::shared_ptr<const AudioPacket> next_audio_packet(
        std::uint64_t after_sequence) const = 0;
    virtual void set_audio_enabled(bool enabled) noexcept = 0;
    virtual void set_audio_volume(float volume) noexcept = 0;
    virtual void set_target_fps(std::uint32_t target_fps) noexcept = 0;
    [[nodiscard]] virtual std::uint32_t target_fps() const noexcept = 0;
    virtual void set_decoder_preference(media::DecoderPreference preference) noexcept = 0;
    [[nodiscard]] virtual DecoderSwitchStatus decoder_switch_status() const noexcept = 0;
    virtual void request_display_orientation(bool landscape) noexcept = 0;
};

} // namespace iPhoneMirror::capture
