#pragma once

#include "Capture/ICaptureSession.h"
#include "Capture/WirelessReceiverHub.h"

#include <memory>
#include <string>

namespace iPhoneMirror::capture {

class WirelessCaptureSession final : public ICaptureSession {
public:
    WirelessCaptureSession(std::shared_ptr<WirelessReceiverHub> receiver,
        std::wstring device_id, CapturePreferences preferences);
    WirelessCaptureSession(std::wstring device_id, CapturePreferences preferences);
    bool publish_external(std::uint32_t kind, std::uint32_t width,
        std::uint32_t height, std::span<const std::uint8_t> payload);
    void external_state(bool active);
    ~WirelessCaptureSession() override;
    WirelessCaptureSession(const WirelessCaptureSession&) = delete;
    WirelessCaptureSession& operator=(const WirelessCaptureSession&) = delete;

    void start();
    void stop() noexcept override;
    [[nodiscard]] Snapshot snapshot() const override;
    [[nodiscard]] std::int64_t latest_frame_timestamp() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> latest_frame() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> next_render_frame() override;
    [[nodiscard]] std::shared_ptr<const AudioPacket> next_audio_packet(
        std::uint64_t after_sequence) const override;
    void set_audio_enabled(bool enabled) noexcept override;
    void set_audio_volume(float volume) noexcept override;
    void set_target_fps(std::uint32_t target_fps) noexcept override;
    [[nodiscard]] std::uint32_t target_fps() const noexcept override;
    void set_decoder_preference(media::DecoderPreference preference) noexcept override;
    [[nodiscard]] DecoderSwitchStatus decoder_switch_status() const noexcept override;
    void request_display_orientation(bool landscape) noexcept override;

private:
    std::shared_ptr<WirelessReceiverHub> receiver_;
    std::shared_ptr<WirelessClientStream> stream_;
    std::wstring device_id_;
    CapturePreferences preferences_;
    bool external_{};
    std::atomic_bool external_active_{true};
};

} // namespace iPhoneMirror::capture
