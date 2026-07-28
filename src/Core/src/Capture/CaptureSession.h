#pragma once

#include "Capture/DecoderSwitchCoordinator.h"
#include "Capture/ICaptureSession.h"
#include "Protocol/QuickTimeSession.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <exception>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

namespace iPhoneMirror::audio {
class WasapiRenderer;
}

namespace iPhoneMirror::capture {

namespace detail {

struct VideoDimensions {
    std::uint32_t width{};
    std::uint32_t height{};
};

[[nodiscard]] constexpr std::uint64_t pack_video_dimensions(
    std::uint32_t width, std::uint32_t height) noexcept {
    return (static_cast<std::uint64_t>(width) << 32U) | height;
}

[[nodiscard]] constexpr VideoDimensions unpack_video_dimensions(
    std::uint64_t packed) noexcept {
    return {
        .width = static_cast<std::uint32_t>(packed >> 32U),
        .height = static_cast<std::uint32_t>(packed),
    };
}

class VideoWorkerFailure final {
public:
    void capture_current() noexcept;
    [[nodiscard]] bool failed() const noexcept;
    void rethrow_if_set() const;

private:
    mutable std::mutex mutex_;
    std::exception_ptr error_;
    std::atomic_bool failed_{};
};

enum class VideoQueueAction {
    Enqueue,
    DropIncoming,
    ClearAndDrop,
    ReplaceWithKeyframe,
};

struct VideoQueueAdmission {
    VideoQueueAction action{VideoQueueAction::Enqueue};
    std::uint64_t dropped_samples{};
    std::uint64_t dropped_bytes{};
    bool entered_recovery{};
};

class VideoQueueBudget final {
public:
    static constexpr std::size_t MaxPendingSamples = 12;
    static constexpr std::size_t MaxPendingBytes = 64U * 1024U * 1024U;

    [[nodiscard]] bool has_capacity(std::size_t pending_samples,
        std::size_t pending_bytes, std::size_t incoming_bytes) const noexcept;
    [[nodiscard]] VideoQueueAdmission admit(std::size_t pending_samples,
        std::size_t pending_bytes, std::size_t incoming_bytes,
        bool keyframe) noexcept;
    [[nodiscard]] bool awaiting_keyframe() const noexcept;

private:
    bool awaiting_keyframe_{};
    std::uint64_t dropped_samples_{};
    std::uint64_t dropped_bytes_{};
};

} // namespace detail

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
    void set_decoder_preference(media::DecoderPreference preference) noexcept override;
    [[nodiscard]] DecoderSwitchStatus decoder_switch_status() const noexcept override;
    void request_display_orientation(bool landscape) noexcept override;

private:
    enum class UsbBackend { LibUsb1, UsbDk, LibUsb0 };
    std::string serial_;
    CapturePreferences preferences_;
    std::wstring product_type_;
    std::atomic_uint64_t native_portrait_size_{
        detail::pack_video_dimensions(1206, 2622)};
    mutable std::mutex mutex_;
    Snapshot snapshot_;
    std::shared_ptr<const media::DecodedFrame> latest_frame_;
    std::deque<std::shared_ptr<const media::DecodedFrame>> render_queue_;
    std::size_t render_queue_bytes_{};
    std::uint64_t stale_render_frames_{};
    std::uint64_t selected_render_frames_{};
    std::jthread worker_;
    UsbBackend usb_backend_{UsbBackend::LibUsb1};
    std::atomic_uint32_t target_fps_{60};
    std::atomic_bool play_audio_{true};
    std::atomic<float> audio_volume_{1.0F};
    detail::DecoderSwitchCoordinator decoder_switch_;
    std::atomic_int requested_display_orientation_{};
    std::atomic_uint64_t native_probe_size_{};
    std::mutex audio_mutex_;
    std::unique_ptr<audio::WasapiRenderer> audio_renderer_;

    void run(std::stop_token stop_token) noexcept;
    void set_state(State state, std::wstring message);
    void stop_audio_renderer() noexcept;
};

} // namespace iPhoneMirror::capture
