#include "Renderer/D3D11PreviewRenderer.h"

#include <Windows.h>
#include <dxgi.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <format>
#include <functional>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>

namespace {
using namespace std::chrono_literals;
using iPhoneMirror::media::DecodedFrame;
using iPhoneMirror::renderer::D3D11PreviewRenderer;

void pump_messages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

void pump_for(std::chrono::milliseconds duration) {
    const auto end = std::chrono::steady_clock::now() + duration;
    do {
        pump_messages();
        std::this_thread::sleep_for(5ms);
    } while (std::chrono::steady_clock::now() < end);
}

void require(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

int sample_owned_center(HWND window, std::string& diagnostic) {
    RECT client{};
    require(GetClientRect(window, &client) != FALSE, "Cannot read test client bounds");
    POINT point{(client.right - client.left) / 2, (client.bottom - client.top) / 2};
    require(ClientToScreen(window, &point) != FALSE, "Cannot locate test client center");
    // Never sample another application's content if it covers our test window.
    const auto hit = WindowFromPoint(point);
    // WindowFromPoint can skip a STATIC child and return its owning host.
    const auto hit_kind = hit == window ? "test_window"
        : hit == GetParent(window) && hit != nullptr ? "test_parent"
        : hit == nullptr ? "unavailable" : "other_window";
    diagnostic = std::string("WindowFromPoint=") + hit_kind;
    if (!hit || (hit != window && hit != GetParent(window))) {
        diagnostic += " GetPixel=not_attempted (center is not confirmed owned)";
        return -1;
    }
    const auto dc = GetDC(nullptr);
    if (!dc) {
        diagnostic += " GetDC=unavailable GetPixel=not_attempted";
        return -1;
    }
    const auto pixel = GetPixel(dc, point.x, point.y);
    ReleaseDC(nullptr, dc);
    diagnostic += pixel == CLR_INVALID ? " GetDC=available GetPixel=unavailable"
        : " GetDC=available GetPixel=available";
    return pixel == CLR_INVALID ? -1 : static_cast<int>(GetRValue(pixel));
}

int wait_for_pixel(HWND window, int expected, const std::string& stage) {
    const auto end = std::chrono::steady_clock::now() + 3s;
    int actual = -1;
    std::string diagnostic;
    do {
        pump_messages();
        actual = sample_owned_center(window, diagnostic);
        if (actual >= 0 && std::abs(actual - expected) <= 18) return actual;
        std::this_thread::sleep_for(10ms);
    } while (std::chrono::steady_clock::now() < end);
    throw std::runtime_error(stage + ": expected center near " +
        std::to_string(expected) + ", got " + std::to_string(actual) + "; " + diagnostic);
}

struct TestWindow {
    HWND handle{};
    ~TestWindow() { if (handle) DestroyWindow(handle); }
};

constexpr auto OriginalProcedureProperty = L"PreviewStaticFrameSmoke.OriginalProcedure";

LRESULT CALLBACK child_procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_ERASEBKGND) return 1;
    const auto original = reinterpret_cast<WNDPROC>(GetPropW(window, OriginalProcedureProperty));
    if (message == WM_NCDESTROY) RemovePropW(window, OriginalProcedureProperty);
    return CallWindowProcW(original, window, message, wparam, lparam);
}

struct Faults {
    std::atomic<int> next_upload_failure{}; // 1 = unavailable; 2 = exception.
    std::atomic<HRESULT> next_present_failure{DXGI_ERROR_WAS_STILL_DRAWING};
    std::atomic<bool> hold_occluded{};
    std::atomic<unsigned> upload_failures{};
    std::atomic<unsigned> present_failures{};
    std::atomic<unsigned> present_calls{};
    std::atomic<unsigned> successful_presents{};
    std::atomic<HRESULT> last_present_result{S_FALSE};
    std::function<void()> once_after_failed_present;

    iPhoneMirror::renderer::PreviewTestHooks hooks() {
        return {
            [this] {
                const auto failure = next_upload_failure.exchange(0);
                if (failure == 0) return true;
                ++upload_failures;
                if (failure == 2) throw std::runtime_error("injected transient upload failure");
                return false;
            },
            [this] {
                ++present_calls;
                const auto result = hold_occluded.load()
                    ? DXGI_STATUS_OCCLUDED : next_present_failure.exchange(S_OK);
                if (result != S_OK) {
                    ++present_failures;
                    if (once_after_failed_present) {
                        auto callback = std::exchange(once_after_failed_present, {});
                        callback();
                    }
                }
                return result;
            },
            [this](HRESULT result) {
                last_present_result.store(result);
                if (result == S_OK) ++successful_presents;
            },
        };
    }

    std::string diagnostic() const {
        return std::format("present_attempts={} present_S_OK={} injected_present_failures={} "
            "injected_upload_failures={} last_present_hr=0x{:08X}",
            present_calls.load(), successful_presents.load(), present_failures.load(),
            upload_failures.load(), static_cast<unsigned>(last_present_result.load()));
    }
};

void wait_for_present(const Faults& faults, unsigned previous, const std::string& stage) {
    const auto end = std::chrono::steady_clock::now() + 3s;
    do {
        pump_messages();
        if (faults.successful_presents.load() > previous) return;
        std::this_thread::sleep_for(10ms);
    } while (std::chrono::steady_clock::now() < end);
    throw std::runtime_error(stage + ": no actual S_OK presentation; " + faults.diagnostic());
}

void run_surface(bool composition, const RECT& work, const wchar_t* window_class, bool retry_only) {
    const std::string name = composition ? "DirectComposition" : "child HWND";
    const auto instance = GetModuleHandleW(nullptr);
    TestWindow background{CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        window_class, L"Static preview test background", WS_POPUP | WS_VISIBLE,
        work.left + 30, work.top + 30, 260, 230, nullptr, nullptr, instance, nullptr)};
    require(background.handle != nullptr, name + ": cannot create background");
    TestWindow window{composition
        ? CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
            WS_EX_NOREDIRECTIONBITMAP, window_class, L"Static preview test",
            WS_POPUP | WS_VISIBLE, work.left + 50, work.top + 50, 160, 160,
            background.handle, nullptr, instance, nullptr)
        : CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"Static preview test",
            WS_CHILD | WS_VISIBLE | SS_BLACKRECT, 20, 20, 160, 160,
            background.handle, nullptr, instance, nullptr)};
    require(window.handle != nullptr, name + ": cannot create preview");
    if (!composition) {
        // Match NativePreviewHost: suppress WM_ERASEBKGND, but allow the
        // underlying SS_BLACKRECT control to process its normal WM_PAINT.
        const auto original = GetWindowLongPtrW(window.handle, GWLP_WNDPROC);
        require(SetPropW(window.handle, OriginalProcedureProperty,
            reinterpret_cast<HANDLE>(original)) != FALSE, name + ": cannot save window procedure");
        require(SetWindowLongPtrW(window.handle, GWLP_WNDPROC,
            reinterpret_cast<LONG_PTR>(child_procedure)) != 0, name + ": cannot subclass preview");
    }
    pump_for(100ms);

    auto synthetic = std::make_shared<DecodedFrame>();
    synthetic->width = synthetic->height = 64;
    synthetic->stride = 64;
    synthetic->timestamp_100ns = 1;
    synthetic->nv12.assign(64 * 64, 100);
    synthetic->nv12.resize(64 * 64 * 3 / 2, 128);
    std::atomic<std::shared_ptr<const DecodedFrame>> provided{synthetic};
    Faults faults;
    // A rejected first presentation must retain its candidate even if the
    // source stops supplying it immediately afterward.
    faults.once_after_failed_present = [&] { provided.store(nullptr); };
    D3D11PreviewRenderer preview(window.handle, [&] { return provided.load(); }, faults.hooks());
    preview.set_corner_profile(0, 2.36F);
    preview.set_color_output_preference(iPhoneMirror::media::ColorOutputPreference::ForceSdrToneMap);
    const auto check_pixel = [&](int expected, const std::string& stage) {
        try {
            return wait_for_pixel(window.handle, expected, stage);
        } catch (const std::exception& error) {
            throw std::runtime_error(std::string(error.what()) + "; " + faults.diagnostic());
        }
    };
    const auto check_redraw = [&](int expected, const std::string& stage, unsigned previous) {
        wait_for_present(faults, previous, stage);
        return retry_only ? expected : check_pixel(expected, stage);
    };
    const auto baseline = check_redraw(98, name + " rejected initial present", 0);
    require(faults.present_failures.load() == 1,
        name + ": initial presentation rejection was not injected");
    provided.store(synthetic);
    pump_for(100ms);
    if (!retry_only) {
        require(InvalidateRect(window.handle, nullptr, TRUE) != FALSE,
            name + ": cannot invalidate the static preview");
        UpdateWindow(window.handle);
        pump_for(150ms);
        check_pixel(baseline, name + " static frame after WM_PAINT");
        if (!composition) {
            const auto region = CreateRoundRectRgn(0, 0, 160, 160, 20, 20);
            require(region != nullptr, name + ": cannot create test window region");
            if (SetWindowRgn(window.handle, region, TRUE) == 0) {
                DeleteObject(region);
                throw std::runtime_error(name + ": cannot apply test window region");
            }
            pump_for(150ms);
            check_pixel(baseline, name + " same-size window-region repaint");
        }
        ShowWindow(window.handle, SW_HIDE);
        pump_for(80ms);
        ShowWindow(window.handle, SW_SHOWNOACTIVATE);
        pump_for(150ms);
        check_pixel(baseline, name + " static hide/show without a refresh request");
    }

    float brightness{};
    const auto redraw = [&](const std::string& stage) {
        const auto successes_before = faults.successful_presents.load();
        brightness = brightness == 0.0F ? 0.25F : 0.0F;
        preview.set_image_adjustments(brightness, 1, 1, 1);
        check_redraw(baseline + static_cast<int>(brightness * 255),
            name + " " + stage, successes_before);
        pump_for(60ms);
    };
    for (const auto result : {DXGI_ERROR_WAS_STILL_DRAWING, DXGI_STATUS_OCCLUDED}) {
        const auto failures_before = faults.present_failures.load();
        faults.next_present_failure.store(result);
        redraw(result == DXGI_STATUS_OCCLUDED ? "occluded refresh retry" : "busy refresh retry");
        require(faults.present_failures.load() == failures_before + 1,
            name + ": same-timestamp presentation failure was not injected");
    }
    for (const auto failure : {1, 2}) {
        const auto failures_before = faults.upload_failures.load();
        faults.next_upload_failure.store(failure);
        redraw(failure == 1 ? "unavailable upload retry" : "upload exception retry");
        require(faults.upload_failures.load() == failures_before + 1,
            name + ": same-timestamp upload failure was not injected");
    }

    // Hold occlusion long enough to distinguish a bounded retry from a 1 ms
    // full upload/draw loop; release without another refresh or media frame.
    faults.hold_occluded.store(true);
    const auto calls_before = faults.present_calls.load();
    const auto occlusion_successes = faults.successful_presents.load();
    brightness = 0.25F;
    preview.set_image_adjustments(brightness, 1, 1, 1);
    pump_for(350ms);
    const auto occluded_calls = faults.present_calls.load() - calls_before;
    faults.hold_occluded.store(false);
    require(occluded_calls >= 1 && occluded_calls <= 24,
        name + ": excessive/missing occluded retry count " + std::to_string(occluded_calls));
    check_redraw(baseline + 64, name + " recovery after sustained occlusion", occlusion_successes);
    pump_for(60ms);

    // A settings panel changes the main preview's available dimensions while
    // the remote screen may remain completely static.
    const auto resize_calls = faults.present_calls.load();
    const auto resize_successes = faults.successful_presents.load();
    faults.next_present_failure.store(DXGI_ERROR_WAS_STILL_DRAWING);
    require(SetWindowPos(window.handle, nullptr, 0, 0, 190, 140,
        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE, name + ": resize failed");
    pump_for(200ms);
    check_redraw(baseline + 64, name + " resize of static source", resize_successes);
    require(faults.present_calls.load() >= resize_calls + 2,
        name + ": resized frame did not retry its rejected presentation");
    pump_for(60ms);

    provided.store(nullptr);
    faults.next_upload_failure.store(1);
    redraw("retained frame with temporarily absent provider");
    const auto refresh_calls = faults.present_calls.load();
    const auto refresh_successes = faults.successful_presents.load();
    preview.request_refresh();
    pump_for(150ms);
    require(faults.present_calls.load() > refresh_calls,
        name + ": explicit refresh did not present the retained frame");
    check_redraw(baseline, name + " explicit retained-frame refresh", refresh_successes);
    provided.store(synthetic);

    ShowWindow(window.handle, SW_HIDE);
    const auto restore_successes = faults.successful_presents.load();
    faults.next_present_failure.store(DXGI_STATUS_OCCLUDED);
    preview.set_image_adjustments(0.25F, 1, 1, 1);
    pump_for(150ms);
    ShowWindow(window.handle, SW_SHOWNOACTIVATE);
    check_redraw(baseline + 64, name + " hidden static frame restored", restore_successes);
    require(synthetic->timestamp_100ns == 1,
        name + ": smoke test must never advance the source timestamp");
    std::cout << name << ": static frame retries, resize, retention, and restore passed; "
        << "occluded_attempts=" << occluded_calls << "; " << faults.diagnostic() << '\n';
}
} // namespace

// Opt-in GPU smoke test. Its only visible objects and sampled pixels are the
// owned synthetic windows; it never starts a receiver or accesses an iOS device.
int run_preview_static_frame_smoke(bool retry_only) {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        WNDCLASSW type{};
        type.lpfnWndProc = DefWindowProcW;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"PreviewStaticFrameSmoke";
        type.hbrBackground = static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH));
        require(RegisterClassW(&type) != 0, "Cannot register static preview test class");
        RECT work{};
        require(SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0) != FALSE,
            "Cannot locate the test work area");
        for (const bool composition : {false, true})
            run_surface(composition, work, type.lpszClassName, retry_only);
        UnregisterClassW(type.lpszClassName, type.hInstance);
        std::cout << (retry_only
            ? "Native preview static-frame retry smoke passed. Screen pixels and paint visibility were not visually verified.\n"
            : "Native preview static-frame smoke passed.\n");
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
