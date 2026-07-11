#pragma once

#include "Media/CoreMedia.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <span>
#include <thread>
#include <vector>

namespace iPhoneMirror::audio {

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
        bool playback_enabled = true, float volume = 1.0F);
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

    void* stop_event_{};
    void* data_event_{};
    void* render_event_{};
    std::jthread worker_;

    std::atomic_bool active_{};
    std::atomic_uint64_t rendered_frames_{};
    std::atomic_uint64_t dropped_frames_{};
    std::atomic_uint64_t underruns_{};
    std::atomic_bool playback_enabled_{true};
    // Linear gain encoded as 0..10000 to keep the real-time sample loop free
    // from floating point atomics and platform-specific lock fallbacks.
    std::atomic_uint32_t volume_units_{10000};
    std::uint32_t current_gain_units_{10000};

    [[nodiscard]] std::size_t dequeue(std::uint8_t* destination, std::size_t frames);
    [[nodiscard]] std::size_t queued_frames() const;
    void run(std::stop_token token) noexcept;
    void run_endpoint(std::stop_token token);
};

} // namespace iPhoneMirror::audio
