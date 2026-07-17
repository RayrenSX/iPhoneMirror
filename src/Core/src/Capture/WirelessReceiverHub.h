#pragma once

#include "Capture/ICaptureSession.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace iPhoneMirror::audio {
class WasapiRenderer;
}

namespace iPhoneMirror::wireless {
struct MessageHeader;
}

namespace iPhoneMirror::capture {

struct WirelessDeviceSnapshot {
    std::wstring id;
    std::wstring name;
    std::wstring product_type;
    std::wstring os_version;
};

enum class MediaCastCommandType : std::uint32_t { None, Play, Stop };

struct MediaCastCommand {
    std::uint64_t id{};
    MediaCastCommandType type{};
    std::wstring url;
    double start_position{};
    double volume{};
};

namespace detail {
[[nodiscard]] bool convert_i420_to_nv12(const wireless::MessageHeader& header,
    std::span<const std::uint8_t> payload, std::vector<std::uint8_t>& destination,
    std::int32_t& destination_stride) noexcept;
}

class WirelessClientStream final {
public:
    WirelessClientStream(std::wstring id, std::wstring name);
    ~WirelessClientStream();

    void set_identity(std::wstring name, bool connected);
    void set_metadata(std::wstring product_type, std::wstring os_version);
    [[nodiscard]] WirelessDeviceSnapshot device() const;
    [[nodiscard]] bool connected() const;
    void attach(CapturePreferences preferences);
    void detach() noexcept;
    [[nodiscard]] Snapshot snapshot() const;
    [[nodiscard]] std::int64_t latest_frame_timestamp() const;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> latest_frame() const;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> next_render_frame();
    void set_audio_enabled(bool enabled) noexcept;
    void set_audio_volume(float volume) noexcept;
    void set_target_fps(std::uint32_t target_fps) noexcept;
    [[nodiscard]] std::uint32_t target_fps() const noexcept;
    void publish_video(const wireless::MessageHeader& header,
        std::span<const std::uint8_t> payload);
    void publish_audio(const wireless::MessageHeader& header,
        std::span<const std::uint8_t> payload);

private:
    std::wstring id_;
    std::wstring name_;
    std::wstring product_type_;
    std::wstring os_version_;
    mutable std::mutex mutex_;
    bool connected_{};
    std::uint32_t attachments_{};
    Snapshot snapshot_;
    std::shared_ptr<const media::DecodedFrame> latest_frame_;
    std::deque<std::shared_ptr<const media::DecodedFrame>> render_queue_;
    std::atomic_uint32_t target_fps_{60};
    std::atomic_bool play_audio_{true};
    std::atomic<float> audio_volume_{1.0F};
    std::mutex audio_mutex_;
    std::unique_ptr<audio::WasapiRenderer> audio_renderer_;
    std::uint32_t audio_sample_rate_{};
    std::uint16_t audio_channels_{};
    std::uint16_t audio_bits_{};
    bool audio_renderer_failed_{};
    std::chrono::steady_clock::time_point started_at_{std::chrono::steady_clock::now()};
    std::chrono::steady_clock::time_point fps_sample_time_{started_at_};
    std::uint64_t fps_sample_frames_{};
    std::uint64_t rejected_video_messages_{};
    std::uint64_t rejected_audio_messages_{};

    void clear_media() noexcept;
    void stop_audio_renderer() noexcept;
};

class WirelessReceiverHub final {
public:
    WirelessReceiverHub() = default;
    ~WirelessReceiverHub();
    WirelessReceiverHub(const WirelessReceiverHub&) = delete;
    WirelessReceiverHub& operator=(const WirelessReceiverHub&) = delete;

    void start(std::wstring receiver_name, std::wstring host_path,
        std::uint32_t width, std::uint32_t height, std::uint32_t frame_rate);
    void stop() noexcept;
    [[nodiscard]] bool running() const noexcept;
    [[nodiscard]] bool ready() const noexcept;
    [[nodiscard]] std::vector<WirelessDeviceSnapshot> devices() const;
    [[nodiscard]] std::shared_ptr<WirelessClientStream> attach(
        std::wstring_view device_id, CapturePreferences preferences);
    [[nodiscard]] MediaCastCommand media_command() const;
    bool update_media_playback(std::uint64_t command_id, double duration,
        double position, double rate) noexcept;

private:
    mutable std::mutex lifecycle_mutex_;
    mutable std::mutex mutex_;
    mutable std::mutex pipe_write_mutex_;
    std::map<std::wstring, std::shared_ptr<WirelessClientStream>, std::less<>> clients_;
    std::wstring receiver_name_;
    std::wstring host_path_;
    std::wstring pipe_name_;
    std::wstring stop_event_name_;
    std::jthread worker_;
    void* pipe_{};
    void* stop_event_{};
    void* process_{};
    std::atomic_bool stopping_{};
    std::atomic_bool ready_{};
    MediaCastCommand media_command_;

    void stop_locked() noexcept;
    void run(std::stop_token stop_token) noexcept;
    void handle_message(const wireless::MessageHeader& header,
        const std::vector<std::uint8_t>& payload);
    [[nodiscard]] std::shared_ptr<WirelessClientStream> get_or_create(
        const wireless::MessageHeader& header, bool mark_connected);
    void mark_all_disconnected() noexcept;
};

} // namespace iPhoneMirror::capture
