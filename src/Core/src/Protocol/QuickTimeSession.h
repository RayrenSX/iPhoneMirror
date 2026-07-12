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
    // Valeria=false makes iOS honor DisplaySize. The protocol selects an
    // encoder tier from the requested long edge; an arbitrary huge square can
    // fall back to 1440p. 1206x2622 is the highest tier verified on both test
    // devices; broader 4K bounds make recent iOS choose a lower encoder tier.
    std::uint32_t requested_width{1206};
    std::uint32_t requested_height{2622};
    bool request_native_display_size{false};
    // The reverse-engineered HPD1 message has no verified frame-rate key.
    // Keep this preference alongside the negotiated dimensions so downstream
    // preview/recording sinks can cap presentation without destabilising the
    // private USB handshake.
    std::uint32_t target_fps{60};
    // Valeria is Apple's showroom/status-bar override (9:41, full battery).
    // QuickTime enables it for demonstrations, but a mirroring application
    // should preserve the phone's real status bar by default.
    bool demo_mode{false};
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
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> begin_display_reconfigure(
        std::uint32_t width, std::uint32_t height);
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> complete_display_reconfigure();
    void set_demo_mode(bool enabled) noexcept { options_.demo_mode = enabled; }
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
