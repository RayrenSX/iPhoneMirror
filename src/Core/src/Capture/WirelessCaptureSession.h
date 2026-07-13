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
    ~WirelessCaptureSession() override;
    WirelessCaptureSession(const WirelessCaptureSession&) = delete;
    WirelessCaptureSession& operator=(const WirelessCaptureSession&) = delete;

    void start();
    void stop() noexcept override;
    [[nodiscard]] Snapshot snapshot() const override;
    [[nodiscard]] std::int64_t latest_frame_timestamp() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> latest_frame() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> next_render_frame() override;
    void set_audio_enabled(bool enabled) noexcept override;
    void set_audio_volume(float volume) noexcept override;
    void set_target_fps(std::uint32_t target_fps) noexcept override;
    [[nodiscard]] std::uint32_t target_fps() const noexcept override;
    void request_display_orientation(bool landscape) noexcept override;

private:
    std::shared_ptr<WirelessReceiverHub> receiver_;
    std::shared_ptr<WirelessClientStream> stream_;
    std::wstring device_id_;
    CapturePreferences preferences_;
};

} // namespace iPhoneMirror::capture
