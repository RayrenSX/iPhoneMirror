#pragma once

#include "Capture/DecoderSwitchCoordinator.h"
#include "Capture/ICaptureSession.h"
#include "Protocol/QuickTimeSession.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <exception>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>

namespace iPhoneMirror::audio {
class WasapiRenderer;
}

namespace iPhoneMirror::transport {
struct AppleUsbDevice;
}

namespace iPhoneMirror::capture {

// Device discovery enters usbmux/lockdownd and can make Apple's management
// stack reopen interfaces. Keep it outside the wired USB transition window.
[[nodiscard]] bool try_begin_usb_device_discovery() noexcept;
void end_usb_device_discovery() noexcept;

class UsbConfigurationNotReadyError final : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

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

class StreamingSilenceWatchdog final {
public:
    using Clock = std::chrono::steady_clock;
    static constexpr auto SilenceLimit = std::chrono::seconds(10);

    void observe_media(Clock::time_point now) noexcept { last_media_at_ = now; }

    [[nodiscard]] bool expired(Clock::time_point now) const noexcept {
        return last_media_at_ && now >= *last_media_at_ &&
            now - *last_media_at_ >= SilenceLimit;
    }

    [[nodiscard]] std::chrono::milliseconds silence_duration(
        Clock::time_point now) const noexcept {
        if (!last_media_at_ || now < *last_media_at_) return {};
        return std::chrono::duration_cast<std::chrono::milliseconds>(
            now - *last_media_at_);
    }

private:
    std::optional<Clock::time_point> last_media_at_;
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
    mutable std::mutex active_usb_mutex_;
    std::function<void()> active_usb_cancel_;
    UsbBackend usb_backend_{UsbBackend::LibUsb1};
    // The explicit start preflight already opened and identified this device.
    // Reuse that descriptor in the worker so startup does not immediately
    // enumerate and open every Apple device a second time.
    std::unique_ptr<transport::AppleUsbDevice> preflight_device_;
    // A process-wide binary semaphore is acquired by start() and released by
    // the worker after the first media sample or on every failure path. The
    // flag makes the cross-thread release idempotent.
    std::atomic_bool usb_transition_gate_held_{};
    std::atomic_uint32_t target_fps_{60};
    std::atomic_bool play_audio_{true};
    std::atomic<float> audio_volume_{1.0F};
    detail::DecoderSwitchCoordinator decoder_switch_;
    std::atomic_int requested_display_orientation_{};
    std::atomic_uint64_t native_probe_size_{};
    mutable std::mutex audio_mutex_;
    std::unique_ptr<audio::WasapiRenderer> audio_renderer_;
    std::deque<std::shared_ptr<const AudioPacket>> audio_output_queue_;
    std::uint64_t audio_output_sequence_{};

    void run(std::stop_token stop_token) noexcept;
    void acquire_usb_transition_gate() noexcept;
    void release_usb_transition_gate() noexcept;
    void set_state(State state, std::wstring message);
    void set_failure(FailureKind kind, FailureStage stage,
        std::int32_t error_code, std::wstring message);
    void set_stopped_warning(FailureKind kind, FailureStage stage,
        std::int32_t error_code, std::wstring message);
    void stop_audio_renderer() noexcept;
};

} // namespace iPhoneMirror::capture
