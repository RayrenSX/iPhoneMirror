#include "Renderer/D3D11PreviewRenderer.h"
#include "iPhoneMirror/CoreApi.h"

#include <Windows.h>
#include <dxgi.h>

#include <atomic>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <format>
#include <functional>
#include <fstream>
#include <filesystem>
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

int sample_owned_pixel(HWND window, double x, double y, std::string& diagnostic) {
    RECT client{};
    require(GetClientRect(window, &client) != FALSE, "Cannot read test client bounds");
    POINT point{static_cast<LONG>((client.right - client.left) * x),
        static_cast<LONG>((client.bottom - client.top) * y)};
    require(ClientToScreen(window, &point) != FALSE, "Cannot locate test sample point");
    // Never sample another application's content if it covers our test window.
    const auto hit = WindowFromPoint(point);
    // WindowFromPoint can skip a STATIC child and return its owning host.
    const auto hit_kind = hit == window ? "test_window"
        : hit == GetParent(window) && hit != nullptr ? "test_parent"
        : hit == nullptr ? "unavailable" : "other_window";
    diagnostic = std::string("WindowFromPoint=") + hit_kind;
    if (!hit || (hit != window && hit != GetParent(window))) {
        diagnostic += " GetPixel=not_attempted (sample point is not confirmed owned)";
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
        actual = sample_owned_pixel(window, 0.5, 0.5, diagnostic);
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

void check_rotated_quadrants(HWND window, const std::string& stage, int turns) {
    // Source quadrants have different gray levels so clockwise/counterclockwise
    // rotation, double rotation and an unrotated letterboxed image all differ.
    constexpr int levels[] = {48, 96, 160, 208};
    constexpr int order[4][4] = {
        {0, 1, 2, 3}, {2, 0, 3, 1}, {3, 2, 1, 0}, {1, 3, 0, 2}};
    const auto end = std::chrono::steady_clock::now() + 3s;
    std::string diagnostic;
    int actual = -1, expected = -1;
    do {
        pump_messages();
        bool matches = true;
        for (int quadrant = 0; quadrant < 4; ++quadrant) {
            expected = levels[order[turns][quadrant]];
            actual = sample_owned_pixel(window,
                (quadrant & 1) ? 0.75 : 0.25,
                (quadrant & 2) ? 0.75 : 0.25, diagnostic);
            if (actual < 0 || std::abs(actual - expected) > 18) {
                matches = false;
                break;
            }
        }
        if (matches) return;
        std::this_thread::sleep_for(10ms);
    } while (std::chrono::steady_clock::now() < end);
    throw std::runtime_error(stage + ": expected quadrant near " +
        std::to_string(expected) + ", got " + std::to_string(actual) + "; " + diagnostic);
}

void test_static_rotation(HWND window, D3D11PreviewRenderer& preview,
    std::atomic<std::shared_ptr<const DecodedFrame>>& provided, const std::string& name) {
    // Remove the clipping region used by the preceding repaint test and leave
    // room in the owned background for both portrait and landscape children.
    require(SetWindowRgn(window, nullptr, TRUE) != 0, name + ": cannot clear test region");
    require(SetWindowPos(GetParent(window), nullptr, 0, 0, 300, 300,
        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE,
        name + ": cannot resize test background");
    auto frame = std::make_shared<DecodedFrame>();
    frame->width = frame->stride = 64;
    frame->height = 128;
    frame->timestamp_100ns = 2;
    frame->color.range = iPhoneMirror::coremedia::ColorRange::Full;
    frame->nv12.resize(64 * 128 * 3 / 2, 128);
    constexpr std::uint8_t levels[] = {48, 96, 160, 208};
    for (unsigned y = 0; y < frame->height; ++y)
        for (unsigned x = 0; x < frame->width; ++x)
            frame->nv12[y * frame->stride + x] = levels[(y >= 64 ? 2 : 0) + (x >= 32 ? 1 : 0)];
    preview.set_image_adjustments(0, 1, 1, 1);
    provided.store(frame);
    for (const bool limited : {false, true}) {
        preview.set_render_size_limit(limited ? 32 : 0, limited ? 64 : 0);
        // Exercise both menu directions, 180 degrees, modulo normalization,
        // and restoring portrait without advancing the source timestamp.
        for (const int requested : {0, 1, 2, 3, 4, -1, 0}) {
            const int turns = ((requested % 4) + 4) % 4;
            require(SetWindowPos(window, nullptr, 0, 0,
                (turns & 1) ? 240 : 120, (turns & 1) ? 120 : 240,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE,
                name + ": rotation resize failed");
            preview.set_rotation(requested);
            check_rotated_quadrants(window, name + (limited ? " limited" : " direct") +
                " rotation=" + std::to_string(requested), turns);
        }
    }
    auto artwork = std::make_shared<iPhoneMirror::renderer::DeviceFrameArtwork>();
    artwork->width = 120; artwork->height = 240;
    artwork->screen_x = artwork->screen_y = 0.03F;
    artwork->screen_width = artwork->screen_height = 0.94F;
    artwork->premultiplied_bgra.resize(120 * 240 * 4);
    artwork->screen_mask.resize(120 * 240);
    for (unsigned y = 0; y < 240; ++y) for (unsigned x = 0; x < 120; ++x) {
        bool screen = x >= 4 && x < 116 && y >= 8 && y < 232;
        artwork->screen_mask[y * 120 + x] = screen ? 255 : 0;
        artwork->premultiplied_bgra[(y * 120 + x) * 4 + 3] = screen ? 0 : 255;
    }
    preview.set_device_frame(artwork);
    // A frame must appear only in the final pass, survive a paused image and
    // rotation, and disappear in fullscreen without changing the source.
    for (const bool limited : {false, true}) {
        preview.set_render_size_limit(limited ? 32 : 0, limited ? 64 : 0);
        for (int turns = 0; turns < 4; ++turns) {
            SetWindowPos(window, nullptr, 0, 0, (turns & 1) ? 240 : 120,
                (turns & 1) ? 120 : 240, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            preview.set_rotation(turns);
            for (int mode = 0; mode < 4; ++mode) {
                // on, fullscreen, restored, off
                if (mode == 1) SetPropW(window, L"iPhoneMirrorFullScreen", reinterpret_cast<HANDLE>(1));
                else RemovePropW(window, L"iPhoneMirrorFullScreen");
                preview.set_phone_frame(mode != 3);
                check_rotated_quadrants(window, name + " phone frame", turns);
                const auto end = std::chrono::steady_clock::now() + 3s;
                bool matched = false;
                std::string diagnostic;
                do {
                    pump_messages();
                    const int edge = sample_owned_pixel(window, 0.015, 0.5, diagnostic);
                    matched = edge >= 0 && ((mode == 0 || mode == 2) ? edge < 35 : edge > 35);
                    if (matched) break;
                    std::this_thread::sleep_for(10ms);
                } while (std::chrono::steady_clock::now() < end);
                require(matched, name + ": phone frame edge mismatch mode=" + std::to_string(mode) + diagnostic);
            }
        }
    }
    // A landscape frame already has its content rotated by the phone. The
    // artwork must use the matching side without rotating the video again.
    auto island_art = std::make_shared<iPhoneMirror::renderer::DeviceFrameArtwork>(*artwork);
    for (int y = 14; y < 24; ++y) for (int x = 40; x < 80; ++x) {
        const auto index = y * 120 + x;
        island_art->screen_mask[index] = 0;
        island_art->premultiplied_bgra[index * 4 + 3] = 255;
    }
    preview.set_device_frame(island_art);
    preview.set_phone_frame(true);
    preview.set_rotation(0);
    SetWindowPos(window, nullptr, 0, 0, 240, 120, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    std::int64_t timestamp = 10;
    std::uint64_t landscape_run = 0;
    for (const bool limited : {false, true}) {
        preview.set_render_size_limit(limited ? 160 : 0, limited ? 80 : 0);
        const auto show_landscape = [&](int source_side, int expected_side, std::uint64_t run) {
            auto landscape = std::make_shared<DecodedFrame>();
            landscape->width = landscape->stride = 224;
            landscape->height = 112;
            landscape->timestamp_100ns = (timestamp += 500'000);
            landscape->wireless_landscape_run = run;
            landscape->color.range = iPhoneMirror::coremedia::ColorRange::Full;
            landscape->nv12.resize(224 * 112 * 3 / 2, 128);
            std::fill_n(landscape->nv12.begin(), 224 * 112, std::uint8_t{200});
            if (source_side) {
                for (int y = 36; y < 76; ++y) for (int x = 6; x < 16; ++x) {
                    const auto column = source_side == 3 ? x : 223 - x;
                    landscape->nv12[y * 224 + column] = 0;
                }
            }
            provided.store(landscape);
            const auto expected = expected_side ? expected_side : 5;
            const auto deadline = std::chrono::steady_clock::now() + 2s;
            do { pump_for(10ms); } while (preview.presented_phone_frame_orientation() != expected &&
                std::chrono::steady_clock::now() < deadline);
            require(preview.presented_phone_frame_orientation() == expected,
                name + ": native landscape artwork did not follow the source sensor");
            pump_for(50ms);
            std::string diagnostic;
            const auto left = sample_owned_pixel(window, 19.0 / 240.0, 0.5, diagnostic);
            const auto right = sample_owned_pixel(window, 1.0 - 19.0 / 240.0, 0.5, diagnostic);
            require(((expected_side == 3 || source_side == 3) ? left >= 0 && left < 35 : left > 140) &&
                    ((expected_side == 1 || source_side == 1) ? right >= 0 && right < 35 : right > 140),
                name + ": extra or missing landscape cutout " + diagnostic);
        };
        for (const int side : {3, 1, 0}) show_landscape(side, side, 0);
        show_landscape(1, 0, ++landscape_run);
        preview.request_refresh();
        pump_for(50ms);
        require(preview.presented_phone_frame_orientation() == 5,
            name + ": a repaint of one wireless frame must not confirm orientation");
        show_landscape(0, 0, landscape_run);
        show_landscape(3, 0, landscape_run);
        show_landscape(3, 3, landscape_run);
        show_landscape(0, 3, landscape_run);
        // Brief contrary content must not move the locked island. Evidence
        // persisting across three frames and 100 ms can flip the same run.
        show_landscape(1, 3, landscape_run);
        show_landscape(1, 3, landscape_run);
        show_landscape(0, 3, landscape_run);
        show_landscape(1, 3, landscape_run);
        show_landscape(1, 3, landscape_run);
        show_landscape(1, 1, landscape_run);
        show_landscape(0, 1, landscape_run);
        // Capture may have observed portrait even if the preview never drew it.
        show_landscape(0, 0, ++landscape_run);
        show_landscape(1, 0, landscape_run);
        show_landscape(1, 1, landscape_run);
        show_landscape(0, 1, landscape_run);
    }
    preview.set_rotation(0);
    preview.set_phone_frame(false);
    const std::weak_ptr<const iPhoneMirror::renderer::DeviceFrameArtwork> empty_preview_artwork = artwork;
    provided.store(nullptr);
    preview.clear();
    pump_for(50ms);
    preview.set_device_frame(nullptr);
    artwork.reset();
    pump_for(150ms);
    require(empty_preview_artwork.expired(), name + ": empty preview retains disabled artwork without a new source frame");
    provided.store(frame);
    require(frame->width == 64 && frame->height == 128 && frame->timestamp_100ns == 2 &&
        frame->nv12.front() == 48 && frame->nv12[64 * 128 - 1] == 208,
        name + ": local rotation changed the source frame");
    std::cout << name << ": static portrait rotation passed in direct and limited passes\n";
}


void test_model_artwork(HWND window, D3D11PreviewRenderer& preview,
    std::atomic<std::shared_ptr<const DecodedFrame>>& provided) {
    wchar_t directory[32768]{};
    if (!GetEnvironmentVariableW(L"IPHONE_MIRROR_FRAME_FIXTURES", directory, 32768)) return;
    const std::filesystem::path root(directory);
    for (const auto* model : {"iphone-12-mini", "iphone-17", "iphone-8", "iphone-14-pro",
        "iphone-16-pro-max", "iphone-18-pro", "iphone-18-pro-max", "ipad-air-4th-generation",
        "ipad-pro-11-inch-m4", "ipad-mini-6th-generation", "ipad-mini-a20-pro",
        "iphone-5", "iphone-6", "iphone-6-plus", "ipad-pro-12-9-inch", "ipad-10th-generation",
        "ipad-pro-11-inch-m5", "iphone-duo", "iphone-duo-inner",
        "ipad", "ipad-2", "ipad-mini", "ipad-3rd-generation", "ipad-4th-generation",
        "ipad-air", "ipad-mini-2", "ipad-mini-3", "ipad-5th-generation",
        "ipad-pro-9-7-inch", "ipad-6th-generation", "iphone", "iphone-3g", "iphone-3gs",
        "iphone-4", "iphone-4-cdma", "iphone-4s", "iphone-se-3rd-generation", "iphone-16e", "iphone-17e"}) {
        auto art = std::make_shared<iPhoneMirror::renderer::DeviceFrameArtwork>();
        std::ifstream input(root / (std::string(model) + ".bin"), std::ios::binary);
        const auto read = [&](void* destination, std::size_t size) {
            input.read(static_cast<char*>(destination), static_cast<std::streamsize>(size));
            require(input.good(), "Incomplete frame fixture");
        };
        read(&art->width, 4); read(&art->height, 4);
        read(&art->screen_x, 4); read(&art->screen_y, 4);
        read(&art->screen_width, 4); read(&art->screen_height, 4);
        require(art->width <= 4096 && art->height <= 4096, "Oversize frame fixture");
        art->premultiplied_bgra.resize(art->width * art->height * 4);
        art->screen_mask.resize(art->width * art->height);
        read(art->premultiplied_bgra.data(), art->premultiplied_bgra.size());
        read(art->screen_mask.data(), art->screen_mask.size());
        preview.set_device_frame(art);
        auto frame = std::make_shared<DecodedFrame>();
        frame->width = frame->stride = static_cast<unsigned>(360.0 * art->screen_width * art->width /
            (art->screen_height * art->height)) & ~1U;
        frame->height = 360;
        frame->timestamp_100ns = 3;
        frame->color.range = iPhoneMirror::coremedia::ColorRange::Full;
        frame->nv12.resize(frame->stride * frame->height * 3 / 2, 128);
        for (unsigned y = 0; y < frame->height; ++y) for (unsigned x = 0; x < frame->width; ++x)
            frame->nv12[y * frame->stride + x] = ((x / 60 + y / 60) % 2) ? 220 : 180;
        provided.store(frame);
        preview.set_phone_frame(true);
        SetWindowPos(GetParent(window), nullptr, 0, 0, 760, 760, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        for (int turns = 0; turns < 4; ++turns) {
            int height = 650, width = static_cast<int>(650.0 * art->width / art->height);
            if (turns & 1) std::swap(width, height);
            SetWindowPos(window, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            preview.set_rotation(turns);
            for (const bool limited : {false, true}) {
                preview.set_render_size_limit(limited ? 180 : 0, limited ? 390 : 0);
                pump_for(250ms);
                // Read the top-center sensor through the same portrait-to-output
                // rotation used by the visible device, not from the source video.
                double x = 0.5, y = art->screen_y + 60.0 / art->height;
                for (int n = 0; n < turns; ++n) { auto old_x = x; x = 1 - y; y = old_x; }
                std::string diagnostic;
                const int sensor = sample_owned_pixel(window, x, y, diagnostic);
                const auto sensor_x = art->width / 2;
                const auto sensor_y = std::min(art->height - 1, static_cast<unsigned>(art->screen_y * art->height + 60));
                const bool is_screen = art->screen_mask[sensor_y * art->width + sensor_x] >= 128;
                require(is_screen ? sensor > 140 : sensor >= 0 && sensor < 40,
                    std::string(model) + ": screen/cutout mismatch " + diagnostic);
                const int screen = sample_owned_pixel(window, 0.5, 0.5, diagnostic);
                require(screen > 140, std::string(model) + ": video is hidden by artwork " + diagnostic);
            }
            if (turns > 1) continue;
            // Screenshot only the owned, visible synthetic test window.
            RECT bounds{}; GetWindowRect(window, &bounds);
            auto screen_dc = GetDC(nullptr); auto memory_dc = CreateCompatibleDC(screen_dc);
            auto bitmap = CreateCompatibleBitmap(screen_dc, width, height);
            auto old_bitmap = SelectObject(memory_dc, bitmap);
            BitBlt(memory_dc, 0, 0, width, height, screen_dc, bounds.left, bounds.top, SRCCOPY);
            SelectObject(memory_dc, old_bitmap);
            BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width; info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
            std::vector<std::uint8_t> pixels(width * height * 4);
            GetDIBits(memory_dc, bitmap, 0, height, pixels.data(), &info, DIB_RGB_COLORS);
            BITMAPFILEHEADER header{}; header.bfType = 0x4d42;
            header.bfOffBits = sizeof(header) + sizeof(BITMAPINFOHEADER);
            header.bfSize = header.bfOffBits + static_cast<DWORD>(pixels.size());
            std::ofstream output(root / (std::string(model) + (turns ? "-landscape.bmp" : "-portrait.bmp")), std::ios::binary);
            output.write(reinterpret_cast<const char*>(&header), sizeof(header));
            output.write(reinterpret_cast<const char*>(&info.bmiHeader), sizeof(BITMAPINFOHEADER));
            output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());
            DeleteObject(bitmap); DeleteDC(memory_dc); ReleaseDC(nullptr, screen_dc);
        }
        // A native landscape source also rotates the physical artwork, even
        // when the user has applied no window rotation.
        auto landscape = std::make_shared<DecodedFrame>(*frame);
        landscape->width = landscape->stride = frame->height;
        landscape->height = frame->width;
        landscape->timestamp_100ns = 4;
        landscape->device_orientation_turns = 1;
        landscape->nv12.assign(landscape->stride * landscape->height * 3 / 2, 128);
        std::fill_n(landscape->nv12.begin(), landscape->stride * landscape->height, std::uint8_t{200});
        provided.store(landscape);
        preview.set_rotation(0);
        const int landscape_height = static_cast<int>(650.0 * art->width / art->height);
        SetWindowPos(window, nullptr, 0, 0, 650, landscape_height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        for (const bool limited : {false, true}) {
            preview.set_render_size_limit(limited ? 180 : 0, limited ? 390 : 0);
            pump_for(250ms);
            std::string diagnostic;
            const auto sensor_y = std::min(art->height - 1, static_cast<unsigned>(art->screen_y * art->height + 60));
            const bool is_screen = art->screen_mask[sensor_y * art->width + art->width / 2] >= 128;
            const int sensor = sample_owned_pixel(window, 1 - art->screen_y - 60.0 / art->height, 0.5, diagnostic);
            require(is_screen ? sensor > 140 : sensor >= 0 && sensor < 40,
                std::string(model) + ": native landscape cutout mismatch " + diagnostic);
            require(sample_owned_pixel(window, 0.5, 0.5, diagnostic) > 140,
                std::string(model) + ": native landscape video is hidden " + diagnostic);
        }
        const std::weak_ptr<const iPhoneMirror::renderer::DeviceFrameArtwork> retained_artwork = art;
        preview.set_phone_frame(false);
        preview.set_device_frame(nullptr);
        art.reset();
        pump_for(250ms);
        require(retained_artwork.expired(), std::string(model) + ": disabled artwork remains retained after paused redraw");
        std::cout << model << ": artwork, cutout, video, rotations and release passed in both GPU paths\n";
    }
}

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

void run_handoff_surface(bool composition, const RECT& work, const wchar_t* window_class) {
    const std::string name = composition ? "DirectComposition" : "child HWND";
    const auto instance = GetModuleHandleW(nullptr);
    TestWindow background{CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        window_class, L"Preview handoff test background", WS_POPUP | WS_VISIBLE,
        work.left + 30, work.top + 30, 200, 200, nullptr, nullptr, instance, nullptr)};
    require(background.handle != nullptr, name + ": cannot create handoff background");
    TestWindow window{CreateWindowExW(composition
        ? WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP : 0,
        window_class, L"Preview handoff test", composition
            ? WS_POPUP | WS_VISIBLE : WS_CHILD | WS_VISIBLE,
        composition ? work.left + 50 : 20, composition ? work.top + 50 : 20,
        128, 128, background.handle, nullptr, instance, nullptr)};
    require(window.handle != nullptr, name + ": cannot create handoff preview");

    auto synthetic = std::make_shared<DecodedFrame>();
    synthetic->width = synthetic->height = synthetic->stride = 64;
    synthetic->timestamp_100ns = 1;
    synthetic->nv12.assign(64 * 64, 100);
    synthetic->nv12.resize(64 * 64 * 3 / 2, 128);
    const auto provider = [synthetic] { return synthetic; };

    // Startup first attaches an empty legacy preview, then replaces it with a
    // device renderer on the same HWND. Do not insert a sleep or DwmFlush
    // between owners: teardown must make the HWND immediately reusable.
    for (unsigned iteration = 0; iteration < 24; ++iteration) {
        if (im_attach_preview_window(window.handle) != 0)
            throw std::runtime_error(name + ": cannot attach empty legacy preview at " +
                std::to_string(iteration));
        require(im_attach_preview_window(window.handle) == 0,
            name + ": reattaching the current owner must reuse it");
        im_detach_preview_window();
        {
            Faults faults;
            faults.next_present_failure.store(S_OK);
            D3D11PreviewRenderer preview(window.handle, provider, faults.hooks());
            wait_for_present(faults, 0, name + " handoff " + std::to_string(iteration));
            require(synthetic->timestamp_100ns == 1,
                name + ": handoff must present an unchanged source frame");
        }
    }
    std::cout << name << ": 24 empty-to-video-to-empty handoffs passed on one HWND\n";
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
    if (!retry_only) test_static_rotation(window.handle, preview, provided, name);
    if (!retry_only && composition) test_model_artwork(window.handle, preview, provided);
    std::cout << name << ": static frame retries, resize, retention, and restore passed; "
        << "occluded_attempts=" << occluded_calls << "; " << faults.diagnostic() << '\n';
}
} // namespace

// Exercises the real swap-chain lifetime and public legacy attach/detach API
// with synthetic frames. No receiver or iOS device is started.
int run_preview_handoff_smoke() {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        WNDCLASSW type{};
        type.lpfnWndProc = DefWindowProcW;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"PreviewHandoffSmoke";
        require(RegisterClassW(&type) != 0, "Cannot register preview handoff test class");
        RECT work{};
        require(SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0) != FALSE,
            "Cannot locate the handoff test work area");
        require(im_initialize() == 0, "Cannot initialize the core for preview handoff");
        for (const bool composition : {false, true})
            run_handoff_surface(composition, work, type.lpszClassName);
        im_shutdown();
        UnregisterClassW(type.lpszClassName, type.hInstance);
        std::cout << "Native preview handoff smoke passed (real S_OK presents).\n";
        return 0;
    } catch (const std::exception& error) {
        im_shutdown();
        std::cerr << error.what() << '\n';
        return 1;
    }
}

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
