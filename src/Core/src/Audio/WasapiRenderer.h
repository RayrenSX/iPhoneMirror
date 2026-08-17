#pragma once

#include "Media/CoreMedia.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <optional>
#include <span>
#include <thread>
#include <vector>

namespace iPhoneMirror::audio {

namespace detail {

struct WasapiBufferLayout {
    std::uint32_t block_align{};
    std::size_t capacity_frames{};
    std::size_t capacity_bytes{};
};

struct WasapiQueueThresholds {
    std::size_t startup_frames{};
    std::size_t high_water_frames{};
};

struct WasapiEnqueuePlan {
    std::size_t drop_existing_frames{};
    std::size_t final_frames{};
};

[[nodiscard]] std::optional<WasapiBufferLayout> checked_wasapi_buffer_layout(
    const coremedia::AudioStreamBasicDescription& format,
    std::size_t minimum_capacity_frames = 0) noexcept;
[[nodiscard]] WasapiQueueThresholds wasapi_queue_thresholds(
    std::size_t maximum_packet_frames, std::size_t capacity_frames,
    std::size_t endpoint_buffer_frames = 0,
    std::size_t base_startup_frames = 3072,
    std::size_t base_high_water_frames = 4096) noexcept;
[[nodiscard]] WasapiEnqueuePlan plan_wasapi_enqueue(
    std::size_t queued_frames, std::size_t incoming_frames,
    std::size_t capacity_frames, WasapiQueueThresholds thresholds) noexcept;

} // namespace detail

enum class WasapiBufferingMode {
    LowLatency,
    NetworkJitter,
};

struct PlaybackStats {
    bool active{};
    std::uint64_t queued_frames{};
    std::uint64_t rendered_frames{};
    std::uint64_t dropped_frames{};
    std::uint64_t underruns{};
};

// Event-driven shared-mode WASAPI sink for the PCM stream carried by Apple's
// QuickTime screen-capture protocol. All COM and endpoint calls live on the
// dedicated audio thread; the USB producer only performs a bounded ring copy.
class WasapiRenderer {
public:
    explicit WasapiRenderer(const coremedia::AudioStreamBasicDescription& format,
        bool playback_enabled = true, float volume = 1.0F,
        WasapiBufferingMode buffering_mode = WasapiBufferingMode::LowLatency);
    ~WasapiRenderer();
    WasapiRenderer(const WasapiRenderer&) = delete;
    WasapiRenderer& operator=(const WasapiRenderer&) = delete;

    void enqueue(std::span<const std::uint8_t> pcm);
    void set_enabled(bool enabled) noexcept;
    void set_volume(float volume) noexcept;
    void stop() noexcept;
    [[nodiscard]] PlaybackStats stats() const;

private:
    coremedia::AudioStreamBasicDescription format_;
    std::uint32_t block_align_{};
    std::size_t capacity_frames_{};
    std::vector<std::uint8_t> ring_;
    mutable std::mutex queue_mutex_;
    std::size_t read_frame_{};
    std::size_t write_frame_{};
    std::size_t queued_frames_{};
    std::size_t maximum_packet_frames_{};
    std::array<std::size_t, 16> recent_packet_frames_{};
    std::size_t packet_history_index_{};
    std::size_t endpoint_buffer_frames_{};
    std::size_t base_startup_frames_{};
    std::size_t base_high_water_frames_{};
    std::size_t startup_frames_{};
    std::size_t high_water_frames_{};

    void* stop_event_{};
    void* data_event_{};
    void* render_event_{};
    std::jthread worker_;

    std::atomic_bool active_{};
    std::atomic_uint64_t rendered_frames_{};
    std::atomic_uint64_t dropped_frames_{};
    std::atomic_uint64_t underruns_{};
    std::atomic_uint64_t queue_catchups_{};
    std::atomic_bool playback_enabled_{true};
    // Linear gain encoded as 0..10000 to keep the real-time sample loop free
    // from floating point atomics and platform-specific lock fallbacks.
    std::atomic_uint32_t volume_units_{10000};
    std::uint32_t current_gain_units_{10000};

    [[nodiscard]] std::size_t dequeue(std::uint8_t* destination, std::size_t frames);
    [[nodiscard]] std::size_t queued_frames() const;
    void close_events() noexcept;
    void run(std::stop_token token) noexcept;
    void run_endpoint(std::stop_token token);
};

} // namespace iPhoneMirror::audio
