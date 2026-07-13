#include "Audio/WasapiRenderer.h"

#include "Logging.h"

#include <Windows.h>
#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <format>
#include <stdexcept>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::audio {
namespace {

constexpr std::uint32_t LinearPcm = 0x6c70636dU; // 'lpcm'
constexpr std::uint32_t PcmIsFloat = 1U << 0U;
constexpr std::uint32_t PcmIsBigEndian = 1U << 1U;
constexpr std::uint32_t PcmIsSignedInteger = 1U << 2U;
constexpr std::uint32_t PcmIsNonInterleaved = 1U << 5U;
// Three Apple packets provide a stable ~42 ms reserve after priming the 22 ms
// endpoint buffer. One and two packets both produced occasional short reads on
// the real default endpoint; this still stays within Apple's 73 ms buffer-ahead
// interval advertised by the capture protocol.
constexpr std::size_t StartupFrames = 3072;
constexpr std::size_t HighWaterFrames = 4096;

HANDLE as_handle(void* value) noexcept { return static_cast<HANDLE>(value); }

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(std::format("{} failed: 0x{:08X}", operation,
            static_cast<unsigned>(result)));
    }
}

WAVEFORMATEX make_wave_format(const coremedia::AudioStreamBasicDescription& format) {
    WAVEFORMATEX wave{};
    wave.wFormatTag = WAVE_FORMAT_PCM;
    wave.nChannels = static_cast<WORD>(format.channels_per_frame);
    wave.nSamplesPerSec = static_cast<DWORD>(format.sample_rate);
    wave.wBitsPerSample = static_cast<WORD>(format.bits_per_channel);
    wave.nBlockAlign = static_cast<WORD>(format.bytes_per_frame);
    wave.nAvgBytesPerSec = wave.nSamplesPerSec * wave.nBlockAlign;
    wave.cbSize = 0;
    return wave;
}

} // namespace

WasapiRenderer::WasapiRenderer(const coremedia::AudioStreamBasicDescription& format,
    bool playback_enabled, float volume)
    : format_(format), block_align_(format.bytes_per_frame),
      capacity_frames_(std::max<std::size_t>(8192,
          static_cast<std::size_t>(format.sample_rate / 6.0))),
      ring_(capacity_frames_ * block_align_) {
    const bool pcm16_interleaved = format.format_id == LinearPcm &&
        (format.format_flags & PcmIsFloat) == 0 &&
        (format.format_flags & PcmIsBigEndian) == 0 &&
        (format.format_flags & PcmIsSignedInteger) != 0 &&
        (format.format_flags & PcmIsNonInterleaved) == 0 &&
        format.sample_rate >= 8000.0 && format.sample_rate <= 192000.0 &&
        format.channels_per_frame >= 1 && format.channels_per_frame <= 8 &&
        format.bits_per_channel == 16 &&
        format.bytes_per_frame == format.channels_per_frame * 2U;
    if (!pcm16_interleaved) {
        throw std::invalid_argument(std::format(
            "unsupported QuickTime audio format id=0x{:08X} flags=0x{:X} rate={} channels={} bits={} bpf={}",
            format.format_id, format.format_flags, format.sample_rate,
            format.channels_per_frame, format.bits_per_channel, format.bytes_per_frame));
    }

    playback_enabled_.store(playback_enabled, std::memory_order_relaxed);
    const auto initial_volume = std::isfinite(volume) ? std::clamp(volume, 0.0F, 1.0F) : 1.0F;
    const auto initial_units = static_cast<std::uint32_t>(std::lround(initial_volume * 10000.0F));
    volume_units_.store(initial_units, std::memory_order_relaxed);
    current_gain_units_ = playback_enabled ? initial_units : 0U;

    stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    data_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    render_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!stop_event_ || !data_event_ || !render_event_) {
        if (render_event_) CloseHandle(as_handle(render_event_));
        if (data_event_) CloseHandle(as_handle(data_event_));
        if (stop_event_) CloseHandle(as_handle(stop_event_));
        stop_event_ = data_event_ = render_event_ = nullptr;
        throw std::runtime_error("CreateEvent for WASAPI failed");
    }
    logging::write(std::format(
        "wasapi source rate={} channels={} bits={} block_align={} capacity_frames={}",
        format.sample_rate, format.channels_per_frame, format.bits_per_channel,
        block_align_, capacity_frames_));
    worker_ = std::jthread([this](std::stop_token token) { run(token); });
}

WasapiRenderer::~WasapiRenderer() {
    stop();
    if (render_event_) CloseHandle(as_handle(render_event_));
    if (data_event_) CloseHandle(as_handle(data_event_));
    if (stop_event_) CloseHandle(as_handle(stop_event_));
}

void WasapiRenderer::stop() noexcept {
    if (!worker_.joinable()) return;
    worker_.request_stop();
    if (stop_event_) SetEvent(as_handle(stop_event_));
    worker_.join();
}

void WasapiRenderer::enqueue(std::span<const std::uint8_t> pcm) {
    if (pcm.empty()) return;
    if (pcm.size() % block_align_ != 0) {
        logging::write(std::format("wasapi drop malformed_bytes={} block_align={}",
            pcm.size(), block_align_));
        return;
    }

    auto frames = pcm.size() / block_align_;
    if (frames > capacity_frames_) {
        const auto trimmed = frames - capacity_frames_;
        pcm = pcm.subspan(trimmed * block_align_);
        frames = capacity_frames_;
        dropped_frames_.fetch_add(trimmed, std::memory_order_relaxed);
    }

    std::size_t dropped{};
    std::size_t depth{};
    {
        std::scoped_lock lock(queue_mutex_);
        // Never let endpoint or scheduling stalls turn into audible A/V delay.
        // When more than three Apple packets are pending, retain the newest
        // packet(s) and discard already-stale PCM.
        if (queued_frames_ + frames > HighWaterFrames) {
            const auto desired_total = std::max<std::size_t>(StartupFrames, frames);
            const auto desired_existing = desired_total > frames ? desired_total - frames : 0;
            if (queued_frames_ > desired_existing) {
                dropped = queued_frames_ - desired_existing;
                read_frame_ = (read_frame_ + dropped) % capacity_frames_;
                queued_frames_ -= dropped;
            }
        }
        if (queued_frames_ + frames > capacity_frames_) {
            const auto overflow = queued_frames_ + frames - capacity_frames_;
            read_frame_ = (read_frame_ + overflow) % capacity_frames_;
            queued_frames_ -= overflow;
            dropped += overflow;
        }

        const auto first = std::min(frames, capacity_frames_ - write_frame_);
        std::memcpy(ring_.data() + write_frame_ * block_align_, pcm.data(),
            first * block_align_);
        if (frames > first) {
            std::memcpy(ring_.data(), pcm.data() + first * block_align_,
                (frames - first) * block_align_);
        }
        write_frame_ = (write_frame_ + frames) % capacity_frames_;
        queued_frames_ += frames;
        depth = queued_frames_;
    }
    if (dropped != 0) {
        const auto total = dropped_frames_.fetch_add(dropped, std::memory_order_relaxed) + dropped;
        logging::write(std::format("wasapi queue_catchup dropped={} dropped_total={} depth={}",
            dropped, total, depth));
    }
    SetEvent(as_handle(data_event_));
}

void WasapiRenderer::set_enabled(bool enabled) noexcept {
    playback_enabled_.store(enabled, std::memory_order_relaxed);
}

void WasapiRenderer::set_volume(float volume) noexcept {
    if (!std::isfinite(volume)) return;
    const auto clamped = std::clamp(volume, 0.0F, 1.0F);
    volume_units_.store(static_cast<std::uint32_t>(
        std::lround(clamped * 10000.0F)), std::memory_order_relaxed);
}

std::size_t WasapiRenderer::dequeue(std::uint8_t* destination, std::size_t frames) {
    std::scoped_lock lock(queue_mutex_);
    const auto count = std::min(frames, queued_frames_);
    if (count == 0) return 0;
    const auto first = std::min(count, capacity_frames_ - read_frame_);
    std::memcpy(destination, ring_.data() + read_frame_ * block_align_,
        first * block_align_);
    if (count > first) {
        std::memcpy(destination + first * block_align_, ring_.data(),
            (count - first) * block_align_);
    }
    read_frame_ = (read_frame_ + count) % capacity_frames_;
    queued_frames_ -= count;

    // Apply playback enable and a linear gain on the render thread. Ramp over
    // one endpoint buffer so slider changes and mute toggles do not introduce
    // a full-scale discontinuity (audible click). The negotiated source is
    // validated as interleaved signed PCM16 stereo by the constructor.
    const auto target_gain = playback_enabled_.load(std::memory_order_relaxed)
        ? volume_units_.load(std::memory_order_relaxed)
        : 0U;
    const auto start_gain = current_gain_units_;
    auto* samples = reinterpret_cast<std::int16_t*>(destination);
    const auto channels = static_cast<std::size_t>(format_.channels_per_frame);
    const auto delta = static_cast<std::int64_t>(target_gain) - start_gain;
    for (std::size_t frame{}; frame < count; ++frame) {
        const auto gain = static_cast<std::int64_t>(start_gain) +
            delta * static_cast<std::int64_t>(frame + 1U) /
                static_cast<std::int64_t>(count);
        for (std::size_t channel{}; channel < channels; ++channel) {
            const auto index = frame * channels + channel;
            const auto scaled = static_cast<std::int32_t>(samples[index]) * gain / 10000;
            samples[index] = static_cast<std::int16_t>(scaled);
        }
    }
    current_gain_units_ = target_gain;
    return count;
}

std::size_t WasapiRenderer::queued_frames() const {
    std::scoped_lock lock(queue_mutex_);
    return queued_frames_;
}

PlaybackStats WasapiRenderer::stats() const {
    return PlaybackStats{
        .active = active_.load(std::memory_order_relaxed),
        .queued_frames = queued_frames(),
        .rendered_frames = rendered_frames_.load(std::memory_order_relaxed),
        .dropped_frames = dropped_frames_.load(std::memory_order_relaxed),
        .underruns = underruns_.load(std::memory_order_relaxed),
    };
}

void WasapiRenderer::run(std::stop_token token) noexcept {
    const auto com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE) {
        logging::write(std::format("wasapi CoInitializeEx failed=0x{:08X}",
            static_cast<unsigned>(com_result)));
        return;
    }
    DWORD task_index{};
    const auto mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task_index);
    while (!token.stop_requested() &&
           WaitForSingleObject(as_handle(stop_event_), 0) != WAIT_OBJECT_0) {
        try {
            run_endpoint(token);
        } catch (const std::exception& error) {
            active_.store(false, std::memory_order_relaxed);
            logging::write(std::format("wasapi endpoint_error={} retry_ms=500", error.what()));
            if (WaitForSingleObject(as_handle(stop_event_), 500) == WAIT_OBJECT_0) break;
        }
    }
    active_.store(false, std::memory_order_relaxed);
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    if (SUCCEEDED(com_result)) CoUninitialize();
    logging::write(std::format(
        "wasapi stopped rendered_frames={} dropped_frames={} underruns={}",
        rendered_frames_.load(), dropped_frames_.load(), underruns_.load()));
}

void WasapiRenderer::run_endpoint(std::stop_token token) {
    ResetEvent(as_handle(render_event_));
    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDevice> device;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator)), "create MMDeviceEnumerator");
    check(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device),
        "get default render endpoint");

    const auto wave = make_wave_format(format_);
    ComPtr<IAudioClient> client;
    ComPtr<IAudioClient3> client3;
    bool client3_path{};
    UINT32 engine_period{};
    HRESULT direct_result = device->Activate(__uuidof(IAudioClient3), CLSCTX_ALL,
        nullptr, reinterpret_cast<void**>(client3.GetAddressOf()));
    if (SUCCEEDED(direct_result)) {
        WAVEFORMATEX* closest{};
        const auto support = client3->IsFormatSupported(AUDCLNT_SHAREMODE_SHARED,
            &wave, &closest);
        if (closest) CoTaskMemFree(closest);
        if (support == S_OK) {
            UINT32 default_period{}, fundamental_period{}, minimum_period{}, maximum_period{};
            direct_result = client3->GetSharedModeEnginePeriod(&wave, &default_period,
                &fundamental_period, &minimum_period, &maximum_period);
            if (SUCCEEDED(direct_result)) {
                engine_period = minimum_period;
                direct_result = client3->InitializeSharedAudioStream(
                    AUDCLNT_STREAMFLAGS_EVENTCALLBACK, engine_period, &wave, nullptr);
            }
            if (SUCCEEDED(direct_result)) {
                check(client3.As(&client), "query IAudioClient from IAudioClient3");
                client3_path = true;
            }
        } else {
            direct_result = support;
        }
    }

    if (!client3_path) {
        logging::write(std::format(
            "wasapi IAudioClient3 direct_format_unavailable hr=0x{:08X}; using autoconvert",
            static_cast<unsigned>(direct_result)));
        client3.Reset();
        client.Reset();
        check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(client.GetAddressOf())), "activate IAudioClient");
        constexpr DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &wave, nullptr),
            "initialize shared WASAPI autoconvert");
    }

    check(client->SetEventHandle(as_handle(render_event_)), "set WASAPI render event");
    UINT32 buffer_frames{};
    check(client->GetBufferSize(&buffer_frames), "get WASAPI buffer size");
    ComPtr<IAudioRenderClient> render;
    check(client->GetService(IID_PPV_ARGS(&render)), "get IAudioRenderClient");
    logging::write(std::format(
        "wasapi initialized mode={} period_frames={} buffer_frames={}",
        client3_path ? "IAudioClient3" : "autoconvert", engine_period, buffer_frames));

    bool started{};
    const auto stop_client = [&]() noexcept {
        if (started) (void)client->Stop();
        started = false;
        active_.store(false, std::memory_order_relaxed);
    };
    const auto write_available = [&](bool count_underrun) {
        UINT32 padding{};
        check(client->GetCurrentPadding(&padding), "get WASAPI padding");
        if (padding >= buffer_frames) return;
        const UINT32 available = buffer_frames - padding;
        BYTE* destination{};
        check(render->GetBuffer(available, &destination), "get WASAPI render buffer");
        const auto copied = dequeue(destination, available);
        if (copied < available) {
            std::memset(destination + copied * block_align_, 0,
                static_cast<std::size_t>(available - copied) * block_align_);
            if (count_underrun) {
                const auto n = underruns_.fetch_add(1, std::memory_order_relaxed) + 1;
                if (n <= 3 || n % 100 == 0) {
                    logging::write(std::format(
                        "wasapi underrun n={} requested={} copied={} queue={}",
                        n, available, copied, queued_frames()));
                }
            }
        }
        check(render->ReleaseBuffer(available, copied == 0
            ? AUDCLNT_BUFFERFLAGS_SILENT : 0), "release WASAPI render buffer");
        rendered_frames_.fetch_add(copied, std::memory_order_relaxed);
    };

    try {
        const auto prebuffer = std::min<std::size_t>(StartupFrames,
            std::max<UINT32>(1, buffer_frames));
        const HANDLE startup_events[] = {as_handle(stop_event_), as_handle(data_event_)};
        while (!token.stop_requested() && queued_frames() < prebuffer) {
            if (WaitForMultipleObjects(2, startup_events, FALSE, 500) == WAIT_OBJECT_0) {
                stop_client();
                return;
            }
        }
        if (token.stop_requested()) {
            stop_client();
            return;
        }
        std::size_t startup_dropped{};
        std::size_t startup_depth{};
        {
            std::scoped_lock lock(queue_mutex_);
            if (queued_frames_ > StartupFrames) {
                startup_dropped = queued_frames_ - StartupFrames;
                read_frame_ = (read_frame_ + startup_dropped) % capacity_frames_;
                queued_frames_ = StartupFrames;
            }
            startup_depth = queued_frames_;
        }
        if (startup_dropped != 0) {
            const auto total = dropped_frames_.fetch_add(
                startup_dropped, std::memory_order_relaxed) + startup_dropped;
            logging::write(std::format(
                "wasapi startup_catchup dropped={} dropped_total={} depth={}",
                startup_dropped, total, startup_depth));
        }
        write_available(false);
        check(client->Start(), "start WASAPI client");
        started = true;
        active_.store(true, std::memory_order_relaxed);
        logging::write(std::format("wasapi playback_started queue_frames={}", queued_frames()));

        const HANDLE render_events[] = {as_handle(stop_event_), as_handle(render_event_)};
        while (!token.stop_requested()) {
            const auto wait = WaitForMultipleObjects(2, render_events, FALSE, 1000);
            if (wait == WAIT_OBJECT_0) break;
            if (wait == WAIT_OBJECT_0 + 1) {
                write_available(true);
            } else if (wait == WAIT_TIMEOUT) {
                throw std::runtime_error("WASAPI render event timed out");
            } else {
                throw std::runtime_error(std::format("WaitForMultipleObjects failed: {}",
                    GetLastError()));
            }
        }
        stop_client();
    } catch (...) {
        stop_client();
        throw;
    }
}

} // namespace iPhoneMirror::audio
