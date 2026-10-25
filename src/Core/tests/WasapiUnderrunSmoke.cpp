#include "Audio/WasapiRenderer.h"

#include <chrono>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

// Opt-in integration test: needs a Windows default audio endpoint. Only silent
// PCM is submitted, and no system endpoint settings are changed.
int run_wasapi_underrun_smoke() {
    using namespace std::chrono_literals;
    using iPhoneMirror::audio::WasapiRenderer;
    try {
        const iPhoneMirror::coremedia::AudioStreamBasicDescription format{
            .sample_rate = 48000,
            .format_id = 0x6c70636dU,
            .format_flags = 1U << 2U,
            .bytes_per_packet = 4,
            .frames_per_packet = 1,
            .bytes_per_frame = 4,
            .channels_per_frame = 2,
            .bits_per_channel = 16,
        };
        WasapiRenderer renderer(format);
        const auto wait_for_drain = [&](std::uint64_t expected) {
            const auto deadline = std::chrono::steady_clock::now() + 3s;
            do {
                const auto stats = renderer.stats();
                if (stats.active && stats.rendered_frames == expected &&
                    stats.queued_frames == 0 && stats.underruns != 0 &&
                    stats.dropped_frames == 0) return;
                std::this_thread::sleep_for(10ms);
            } while (std::chrono::steady_clock::now() < deadline);
            const auto stats = renderer.stats();
            std::cerr << "active=" << stats.active
                << " rendered=" << stats.rendered_frames
                << " queued=" << stats.queued_frames
                << " dropped=" << stats.dropped_frames
                << " underruns=" << stats.underruns << '\n';
            throw std::runtime_error("audio tail did not drain after source stopped");
        };

        // Prime startup with ordinary 1024-frame packets. The final one-frame
        // tail cannot fill any normal endpoint period by itself.
        const std::vector<std::uint8_t> packet(1024 * 4);
        for (int i = 0; i < 3; ++i) renderer.enqueue(packet);
        renderer.enqueue(std::vector<std::uint8_t>(4));
        wait_for_drain(3073);

        // Resume with less than either the startup reserve or endpoint period.
        // Playback must remain running and consume it on the next buffer.
        std::this_thread::sleep_for(100ms);
        renderer.enqueue(std::vector<std::uint8_t>(257 * 4));
        wait_for_drain(3330);
        renderer.stop();
        std::cout << "WASAPI partial-buffer drain and resume passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "WASAPI underrun smoke failed: " << error.what() << '\n';
        return 1;
    }
}
