#include "Renderer/D3D11PreviewRenderer.h"
#include "iPhoneMirror/CoreApi.h"
#include <Windows.h>
#include <dwmapi.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <thread>

namespace {
void pump() {
    const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(350);
    while (std::chrono::steady_clock::now() < end) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    DwmFlush();
}

int sample(HWND window) {
    RECT bounds{};
    GetWindowRect(window, &bounds);
    const auto dc = GetDC(nullptr);
    const auto pixel = GetPixel(dc, (bounds.left + bounds.right) / 2,
        (bounds.top + bounds.bottom) / 2);
    ReleaseDC(nullptr, dc);
    if (pixel == CLR_INVALID) throw std::runtime_error("Cannot sample the test window");
    return GetRValue(pixel);
}

struct TestWindow {
    HWND handle{};
    ~TestWindow() { if (handle) DestroyWindow(handle); }
};
}

// Opt-in interactive GPU smoke test. All sampled pixels belong to these
// synthetic test windows. No receiver, device, or capture session is started.
int run_preview_opacity_smoke() {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        WNDCLASSW type{};
        type.lpfnWndProc = DefWindowProcW;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"PreviewOpacitySmoke";
        type.hbrBackground = static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH));
        RegisterClassW(&type);
        RECT work{};
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
        TestWindow background{CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            type.lpszClassName, L"Opacity test background", WS_POPUP | WS_VISIBLE,
            work.left + 30, work.top + 30, 380, 190, nullptr, nullptr, type.hInstance, nullptr)};
        TestWindow first{CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW,
            type.lpszClassName, L"Opacity test preview", WS_POPUP | WS_VISIBLE,
            work.left + 50, work.top + 50, 130, 130, background.handle, nullptr, type.hInstance, nullptr)};
        TestWindow second{CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW,
            type.lpszClassName, L"Opacity test companion", WS_POPUP | WS_VISIBLE,
            work.left + 240, work.top + 50, 130, 130, background.handle, nullptr, type.hInstance, nullptr)};
        if (!background.handle || !first.handle || !second.handle)
            throw std::runtime_error("Could not create opacity test windows");
        std::atomic<std::shared_ptr<const iPhoneMirror::media::DecodedFrame>> frame;
        iPhoneMirror::renderer::D3D11PreviewRenderer preview(first.handle, [&] { return frame.load(); });
        iPhoneMirror::renderer::D3D11PreviewRenderer companion(second.handle, [] { return nullptr; });
        preview.set_corner_profile(0, 2.36F);
        companion.set_corner_profile(0, 2.36F);
        for (const bool with_frame : {false, true}) {
            if (with_frame) {
                auto synthetic = std::make_shared<iPhoneMirror::media::DecodedFrame>();
                synthetic->width = synthetic->height = 64;
                synthetic->stride = 64;
                synthetic->timestamp_100ns = 1;
                synthetic->nv12.assign(64 * 64, 16);
                synthetic->nv12.resize(64 * 64 * 3 / 2, 128);
                frame.store(synthetic);
            }
            preview.set_opacity(1.0F); pump();
            const auto opaque = sample(first.handle);
            preview.set_opacity(0.5F); pump();
            const auto half = sample(first.handle);
            preview.set_opacity(0.1F); pump();
            const auto faint = sample(first.handle);
            const auto unchanged = sample(second.handle);
            preview.set_opacity(1.0F); pump();
            const auto restored = sample(first.handle);
            std::cout << (with_frame ? "paused video" : "empty preview")
                << ": opaque=" << opaque << " half=" << half << " faint=" << faint
                << " companion=" << unchanged << " restored=" << restored << '\n';
            if (opaque > 30 || half < opaque + 30 || faint < half + 15 ||
                unchanged > 30 || std::abs(restored - opaque) > 8)
                throw std::runtime_error("Composition opacity did not blend independently over white");
        }
        for (const auto invalid : {0.0F, 1.1F, NAN})
            if (im_session_set_window_opacity(0, first.handle, invalid) == 0)
                throw std::runtime_error("Opacity API accepted invalid input");
        std::cout << "Native preview opacity smoke passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
