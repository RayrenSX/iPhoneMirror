#pragma once

#include "Protocol/QuickTimeSession.h"
#include "Media/MediaFoundationDecoder.h"

#include <cstdint>
#include <atomic>
#include <thread>
#include <mutex>
#include <string>
#include <optional>
#include <memory>
#include <deque>

namespace iPhoneMirror::audio {
class WasapiRenderer;
}

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
    std::wstring message{L"空闲"};
};

struct CapturePreferences {
    // Local D3D presentation limit only. It must never be copied into the
    // QuickTime SessionOptions/HPD1 DisplaySize request.
    std::uint32_t render_max_width{};
    std::uint32_t render_max_height{};
    std::uint32_t target_fps{60};
    bool play_audio{true};
    float audio_volume{1.0F};
    // Advanced-mode USB HPD1 request. (0,0) keeps the normal per-device probe.
    std::uint32_t usb_requested_width{};
    std::uint32_t usb_requested_height{};
};

class CaptureSession {
public:
    explicit CaptureSession(std::string serial, bool play_audio = true);
    CaptureSession(std::string serial, CapturePreferences preferences,
        std::wstring product_type = {});
    ~CaptureSession();
    CaptureSession(const CaptureSession&) = delete;
    CaptureSession& operator=(const CaptureSession&) = delete;

    void start(bool use_usbdk);
    void stop() noexcept;
    [[nodiscard]] Snapshot snapshot() const;
    [[nodiscard]] std::int64_t latest_frame_timestamp() const;
    // Returns an immutable snapshot without copying the multi-megabyte NV12
    // payload. The GUI keeps this shared reference only for the duration of
    // its conversion, while the capture thread can publish the next frame.
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> latest_frame() const;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> next_render_frame();
    void set_audio_enabled(bool enabled) noexcept;
    void set_audio_volume(float volume) noexcept;
    void set_target_fps(std::uint32_t target_fps) noexcept;
    [[nodiscard]] std::uint32_t target_fps() const noexcept;
    void request_display_orientation(bool landscape) noexcept;

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
