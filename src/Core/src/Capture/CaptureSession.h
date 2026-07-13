#pragma once

#include "Capture/ICaptureSession.h"
#include "Protocol/QuickTimeSession.h"

#include <atomic>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

namespace iPhoneMirror::audio {
class WasapiRenderer;
}

namespace iPhoneMirror::capture {

struct UsbDisplayConfiguration {
    quicktime::SessionOptions session_options;
    bool adaptive_reconfiguration{};
};

[[nodiscard]] UsbDisplayConfiguration make_usb_display_configuration(
    UsbProjectionMode mode, std::uint32_t native_width, std::uint32_t native_height,
    std::uint32_t requested_width = 0, std::uint32_t requested_height = 0) noexcept;

class CaptureSession final : public ICaptureSession {
public:
    explicit CaptureSession(std::string serial, bool play_audio = true);
    CaptureSession(std::string serial, CapturePreferences preferences,
        std::wstring product_type = {});
    ~CaptureSession() override;
    CaptureSession(const CaptureSession&) = delete;
    CaptureSession& operator=(const CaptureSession&) = delete;

    void start(bool use_usbdk);
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
    enum class UsbBackend { LibUsb1, UsbDk, LibUsb0 };
    std::string serial_;
    CapturePreferences preferences_;
    std::wstring product_type_;
    std::uint32_t native_portrait_width_{1206};
    std::uint32_t native_portrait_height_{2622};
    mutable std::mutex mutex_;
    Snapshot snapshot_;
    std::shared_ptr<const media::DecodedFrame> latest_frame_;
    std::deque<std::shared_ptr<const media::DecodedFrame>> render_queue_;
    std::uint64_t stale_render_frames_{};
    std::uint64_t selected_render_frames_{};
    std::jthread worker_;
    UsbBackend usb_backend_{UsbBackend::LibUsb1};
    std::atomic_uint32_t target_fps_{60};
    std::atomic_bool play_audio_{true};
    std::atomic<float> audio_volume_{1.0F};
    std::atomic_int requested_display_orientation_{};
    std::atomic_uint64_t native_probe_size_{};
    std::mutex audio_mutex_;
    std::unique_ptr<audio::WasapiRenderer> audio_renderer_;

    void run(std::stop_token stop_token) noexcept;
    void set_state(State state, std::wstring message);
    void stop_audio_renderer() noexcept;
};

} // namespace iPhoneMirror::capture
