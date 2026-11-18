#include "Renderer/D3D11PreviewRenderer.h"

#include <Windows.h>
#include <dxgi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <format>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>

namespace {
using namespace std::chrono_literals;
using Clock = std::chrono::steady_clock;
using iPhoneMirror::media::DecodedFrame;
using iPhoneMirror::renderer::D3D11PreviewRenderer;

void require(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

void pump() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

struct TestWindow {
    HWND handle{};
    ~TestWindow() { if (handle) DestroyWindow(handle); }
};

struct TimerResolution {
    using Change = unsigned(WINAPI*)(unsigned);
    HMODULE module{};
    Change end{};
    bool active{};
    explicit TimerResolution(bool requested) {
        if (!requested) return;
        module = LoadLibraryW(L"winmm.dll");
        require(module != nullptr, "Cannot load the synthetic timer-resolution control");
        const auto begin = reinterpret_cast<Change>(GetProcAddress(module, "timeBeginPeriod"));
        end = reinterpret_cast<Change>(GetProcAddress(module, "timeEndPeriod"));
        if (!begin || !end || begin(1) != 0) {
            FreeLibrary(std::exchange(module, nullptr));
            throw std::runtime_error("Cannot request the synthetic 1ms timer resolution");
        }
        active = true;
    }
    ~TimerResolution() {
        if (active) (void)end(1);
        if (module) FreeLibrary(module);
    }
};

struct Counts {
    std::atomic_uint64_t provider{}, upload{}, present{}, successful{}, retry{}, occluded{}, errors{}, produced{}, duplicate{};
};

struct Snapshot {
    std::uint64_t provider{}, upload{}, present{}, successful{}, retry{}, occluded{}, errors{}, produced{}, duplicate{};
};

Snapshot sample(const Counts& counts) {
    return {counts.provider.load(), counts.upload.load(), counts.present.load(),
        counts.successful.load(), counts.retry.load(), counts.occluded.load(), counts.errors.load(),
        counts.produced.load(), counts.duplicate.load()};
}

struct IndependentSource {
    HANDLE timer{};
    std::atomic<std::shared_ptr<const DecodedFrame>> latest;
    std::atomic_bool failed{};
    std::jthread worker;

    IndependentSource(const DecodedFrame& picture, Clock::time_point epoch, Counts& counts) {
        timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_MODIFY_STATE | SYNCHRONIZE);
        require(timer != nullptr, "Cannot create the independent high-resolution source timer");
        try {
            auto initial = std::make_shared<DecodedFrame>(picture);
            initial->timestamp_100ns = 1;
            initial->received_at = epoch;
            latest.store(initial);
            ++counts.produced;
            worker = std::jthread([&, epoch, picture](std::stop_token stop) {
                try {
                    std::uint64_t tick = 1;
                    while (!stop.stop_requested()) {
                        const auto deadline = epoch + std::chrono::nanoseconds(tick * 1'000'000'000ULL / 60ULL);
                        const auto remaining = std::chrono::duration_cast<std::chrono::nanoseconds>(
                            deadline - Clock::now()).count();
                        LARGE_INTEGER due{};
                        due.QuadPart = -std::max<std::int64_t>(1, (remaining + 99) / 100);
                        require(SetWaitableTimerEx(timer, &due, 0, nullptr, nullptr, nullptr, 0) != FALSE,
                            "Cannot arm the independent source timer");
                        require(WaitForSingleObject(timer, 100) == WAIT_OBJECT_0,
                            "The independent source timer did not fire");
                        if (stop.stop_requested()) break;
                        auto frame = std::make_shared<DecodedFrame>(picture);
                        frame->timestamp_100ns = static_cast<std::int64_t>((tick + 1ULL) * 10'000'000ULL / 60ULL);
                        frame->received_at = Clock::now();
                        latest.store(frame);
                        ++counts.produced;
                        ++tick;
                    }
                } catch (...) {
                    failed.store(true);
                }
            });
        } catch (...) {
            CloseHandle(std::exchange(timer, nullptr));
            throw;
        }
    }

    ~IndependentSource() {
        if (worker.joinable()) {
            worker.request_stop();
            worker.join();
        }
        if (timer) { CancelWaitableTimer(timer); CloseHandle(timer); }
    }
};

void report(const std::string& name, unsigned stage, double seconds,
    const Snapshot& current, const Snapshot& previous) {
    std::cout << std::format(
        "preview_cadence surface={} stage={} seconds={:.3f} produced_hz={:.2f} provider_hz={:.2f} duplicate_polls={} upload_hz={:.2f} "
        "present_attempt_hz={:.2f} present_S_OK_fps={:.2f} retries={} occluded={} errors={}\n",
        name, stage, seconds, (current.produced - previous.produced) / seconds,
        (current.provider - previous.provider) / seconds, current.duplicate - previous.duplicate,
        (current.upload - previous.upload) / seconds, (current.present - previous.present) / seconds,
        (current.successful - previous.successful) / seconds, current.retry - previous.retry,
        current.occluded - previous.occluded, current.errors - previous.errors) << std::flush;
}

void run_surface(bool composition, const RECT& work, const wchar_t* class_name, bool independent_source) {
    const std::string name = composition ? "DirectComposition" : "child_HWND";
    const auto instance = GetModuleHandleW(nullptr);
    constexpr int width = 320, height = 696;
    TestWindow parent{CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        class_name, L"Synthetic preview cadence test", WS_POPUP | WS_VISIBLE,
        work.left + 32, work.top + 32, width + 16, height + 16,
        nullptr, nullptr, instance, nullptr)};
    require(parent.handle != nullptr, name + ": cannot create owned background");
    TestWindow window{CreateWindowExW(composition
        ? WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP
        : WS_EX_NOACTIVATE, class_name, L"Synthetic 60 fps preview", composition
            ? WS_POPUP | WS_VISIBLE : WS_CHILD | WS_VISIBLE,
        composition ? work.left + 40 : 8, composition ? work.top + 40 : 8,
        width, height, parent.handle, nullptr, instance, nullptr)};
    require(window.handle != nullptr, name + ": cannot create owned preview");

    // The direct mode quantizes steady time without allocating in provider().
    // The independent mode publishes an immutable copy on its own timer;
    // provider() only loads the latest atomic shared pointer, like capture.
    std::array<std::shared_ptr<DecodedFrame>, 3> frames;
    for (auto& frame : frames) {
        frame = std::make_shared<DecodedFrame>();
        frame->width = frame->stride = 994;
        frame->height = 2160;
        frame->color.range = iPhoneMirror::coremedia::ColorRange::Full;
        frame->color.matrix = iPhoneMirror::coremedia::MatrixCoefficients::Bt709;
        frame->nv12.resize(994 * 2160 * 3 / 2, 128);
        for (unsigned y = 0; y < frame->height; ++y)
            for (unsigned x = 0; x < frame->width; ++x)
                frame->nv12[y * 994 + x] = static_cast<std::uint8_t>(48 + (x / 32 + y / 32) % 160);
    }
    Counts counts;
    const auto source_epoch = Clock::now();
    std::unique_ptr<IndependentSource> producer;
    if (independent_source) producer = std::make_unique<IndependentSource>(*frames[0], source_epoch, counts);
    std::uint64_t supplied_tick = static_cast<std::uint64_t>(-1);
    std::int64_t previous_provider_timestamp{};
    std::shared_ptr<const DecodedFrame> supplied;
    const auto provider = [&]() -> std::shared_ptr<const DecodedFrame> {
        ++counts.provider;
        if (producer) {
            const auto frame = producer->latest.load();
            if (frame && frame->timestamp_100ns == previous_provider_timestamp) ++counts.duplicate;
            if (frame) previous_provider_timestamp = frame->timestamp_100ns;
            return frame;
        }
        const auto now = Clock::now();
        const auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(now - source_epoch).count();
        const auto tick = static_cast<std::uint64_t>(ns) * 60ULL / 1'000'000'000ULL;
        if (!supplied || tick != supplied_tick) {
            auto& frame = frames[tick % frames.size()];
            frame->timestamp_100ns = static_cast<std::int64_t>((tick + 1ULL) * 10'000'000ULL / 60ULL);
            frame->received_at = source_epoch + std::chrono::nanoseconds(tick * 1'000'000'000ULL / 60ULL);
            supplied = frame;
            supplied_tick = tick;
            ++counts.produced;
        } else {
            ++counts.duplicate;
        }
        return supplied;
    };
    iPhoneMirror::renderer::PreviewTestHooks hooks;
    std::atomic_uint64_t timer_waits{};
    if (independent_source) {
        hooks.wait_for_timer = [&](HANDLE timer, DWORD timeout_ms) {
            const auto result = WaitForSingleObject(timer, timeout_ms);
            if (++timer_waits == 500 && result == WAIT_OBJECT_0) return DWORD{WAIT_TIMEOUT};
            return result;
        };
    }
    hooks.before_upload = [&] { ++counts.upload; return true; };
    hooks.after_present = [&](HRESULT result) {
        ++counts.present;
        if (result == S_OK) ++counts.successful;
        else if (result == DXGI_ERROR_WAS_STILL_DRAWING) ++counts.retry;
        else if (result == DXGI_STATUS_OCCLUDED) ++counts.occluded;
        else ++counts.errors;
    };
    {
        D3D11PreviewRenderer preview(window.handle, provider, std::move(hooks));
        preview.set_max_fps(60);
        preview.set_render_size_limit(0, 0);
        preview.set_corner_profile(0, 2.36F);
        preview.set_color_output_preference(iPhoneMirror::media::ColorOutputPreference::ForceSdrToneMap);
        const auto warm_until = Clock::now() + 500ms;
        while (Clock::now() < warm_until) { pump(); std::this_thread::sleep_for(1ms); }
        const auto started = Clock::now();
        auto last_time = started;
        const auto initial = sample(counts);
        auto previous = initial;
        for (unsigned stage = 1; stage <= 4; ++stage) {
            const auto until = started + std::chrono::seconds(stage);
            while (Clock::now() < until) { pump(); std::this_thread::sleep_for(1ms); }
            const auto now = Clock::now();
            const auto current = sample(counts);
            report(name, stage, std::chrono::duration<double>(now - last_time).count(), current, previous);
            last_time = now;
            previous = current;
        }
        report(name + "_total", 4, std::chrono::duration<double>(last_time - started).count(), previous, initial);
        require(previous.successful > initial.successful, name + ": no successful real presentation");
        require(previous.errors == initial.errors, name + ": unexpected presentation failure");
        require(!producer || !producer->failed.load(), name + ": independent source timer failed");
        require(!producer || timer_waits.load() > 500,
            name + ": high-resolution preview waits did not continue after a timeout");
    }
}
} // namespace

int run_preview_cadence_smoke(bool request_timer_resolution, bool independent_source) {
    const auto instance = GetModuleHandleW(nullptr);
    constexpr auto class_name = L"iPhoneMirrorPreviewCadenceSmoke";
    bool registered{};
    try {
        TimerResolution timer_resolution(request_timer_resolution);
        WNDCLASSW window_class{};
        window_class.hInstance = instance;
        window_class.lpfnWndProc = DefWindowProcW;
        window_class.lpszClassName = class_name;
        window_class.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        require(RegisterClassW(&window_class) != 0, "Cannot register cadence preview class");
        registered = true;
        RECT work{};
        require(SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0) != FALSE, "Cannot inspect monitor work area");
        std::cout << "Synthetic preview source=994x2160 NV12 clock=" <<
            (independent_source ? "independent_high_resolution_timer_60Hz" : "provider_steady_60Hz") <<
            " cap=60 render_limit=native timer_resolution=" <<
            (request_timer_resolution ? "requested_1ms" : "unchanged") << ' ' <<
            "duration_per_surface=4s warmup=500ms (presentation submissions, not measured scanout)\n" << std::flush;
        run_surface(false, work, class_name, independent_source);
        run_surface(true, work, class_name, independent_source);
        UnregisterClassW(class_name, instance);
        std::cout << "Preview cadence smoke completed\n";
        return 0;
    } catch (const std::exception& error) {
        if (registered) UnregisterClassW(class_name, instance);
        std::cerr << "Preview cadence smoke failed: " << error.what() << '\n';
        return 1;
    }
}
