#pragma once

#include "Media/CoreMedia.h"
#include "Protocol/QuickTimePacket.h"

#include <chrono>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace iPhoneMirror::quicktime {

enum class SessionState {
    WaitingForPing,
    WaitingForAudioClock,
    Negotiating,
    Streaming,
    Stopping,
    Stopped,
    Error,
};

struct SessionOptions {
    std::uint32_t requested_width{1920};
    std::uint32_t requested_height{1080};
    // The reverse-engineered HPD1 message has no verified frame-rate key.
    // Keep this preference alongside the negotiated dimensions so downstream
    // preview/recording sinks can cap presentation without destabilising the
    // private USB handshake.
    std::uint32_t target_fps{60};
    bool demo_mode{true};
    bool request_audio{true};
    bool advertise_hevc_444{false};
};

struct SessionEvent {
    std::vector<std::vector<std::uint8_t>> outbound;
    std::optional<coremedia::SampleBuffer> video_sample;
    std::optional<coremedia::SampleBuffer> audio_sample;
    std::string warning;
    SessionState state{SessionState::WaitingForPing};
};

class SessionProtocol {
public:
    explicit SessionProtocol(SessionOptions options = {});

    [[nodiscard]] SessionEvent process(const Packet& packet);
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> stop_messages();
    void reset();

    [[nodiscard]] SessionState state() const noexcept { return state_; }
    [[nodiscard]] std::uint64_t video_frames() const noexcept { return video_frames_; }
    [[nodiscard]] std::uint64_t audio_packets() const noexcept { return audio_packets_; }
    [[nodiscard]] const std::optional<coremedia::FormatDescription>& video_format() const noexcept { return video_format_; }
    [[nodiscard]] const std::optional<coremedia::FormatDescription>& audio_format() const noexcept { return audio_format_; }
    [[nodiscard]] const std::optional<coremedia::AudioStreamBasicDescription>& negotiated_audio() const noexcept { return negotiated_audio_; }

private:
    SessionOptions options_;
    SessionState state_{SessionState::WaitingForPing};
    std::chrono::steady_clock::time_point epoch_{std::chrono::steady_clock::now()};
    std::uint64_t device_audio_clock_{};
    std::uint64_t local_audio_clock_{};
    std::uint64_t device_video_clock_{};
    std::uint64_t local_video_clock_{};
    std::uint64_t local_host_clock_{};
    std::uint64_t video_frames_{};
    std::uint64_t audio_packets_{};
    std::optional<coremedia::FormatDescription> video_format_;
    std::optional<coremedia::FormatDescription> audio_format_;
    std::optional<coremedia::AudioStreamBasicDescription> negotiated_audio_;
};

} // namespace iPhoneMirror::quicktime
