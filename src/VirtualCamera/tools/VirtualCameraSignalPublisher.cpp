#include <iPhoneMirror/VirtualCameraApi.h>

#include <objbase.h>
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwchar>
#include <thread>
#include <vector>

namespace {

void print_hr(const char* operation, std::int32_t result) {
    std::fprintf(stderr, "%s failed: 0x%08X\n", operation,
                 static_cast<unsigned>(result));
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    int duration_seconds = 120;
    std::uint32_t width = 1280;
    std::uint32_t height = 720;
    std::uint32_t frame_rate = 30;
    if (argc > 1)
        duration_seconds =
            static_cast<int>(std::wcstol(argv[1], nullptr, 10));
    if (argc > 2)
        width = static_cast<std::uint32_t>(
            std::wcstoul(argv[2], nullptr, 10));
    if (argc > 3)
        height = static_cast<std::uint32_t>(
            std::wcstoul(argv[3], nullptr, 10));
    if (argc > 4)
        frame_rate = static_cast<std::uint32_t>(
            std::wcstoul(argv[4], nullptr, 10));
    if (duration_seconds < 1 || duration_seconds > 3600 ||
        width < 160 || height < 160 || frame_rate < 10 || frame_rate > 60) {
        std::fprintf(stderr,
                     "Usage: VirtualCameraSignalPublisher [seconds] [width] "
                     "[height] [fps]\n");
        return 2;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        print_hr("CoInitializeEx", hr);
        return 2;
    }

    int exit_code = 2;
    const auto start_result = im_vcam_start_ex(
        L"iPhoneMirror Virtual Camera", width, height, frame_rate);
    if (start_result < 0) {
        print_hr("start virtual camera", start_result);
    } else {
        const std::uint32_t stride = width * 4U;
        std::vector<std::uint8_t> pixels(
            static_cast<std::size_t>(stride) * height);
        for (std::uint32_t y = 0; y < height; ++y) {
            for (std::uint32_t x = 0; x < width; ++x) {
                const auto offset = static_cast<std::size_t>(y) * stride + x * 4U;
                pixels[offset] = static_cast<std::uint8_t>(48U + x * 32U / width);
                pixels[offset + 1U] = static_cast<std::uint8_t>(144U + y * 48U / height);
                pixels[offset + 2U] = 224U;
                pixels[offset + 3U] = 255U;
            }
        }

        const auto started = std::chrono::steady_clock::now();
        const auto deadline = started + std::chrono::seconds(duration_seconds);
        const auto frame_duration =
            std::chrono::duration<double>(1.0 / static_cast<double>(frame_rate));
        std::uint64_t frames{};
        auto next_frame = started;
        while (std::chrono::steady_clock::now() < deadline) {
            const std::uint32_t marker_x = static_cast<std::uint32_t>(
                frames % std::max<std::uint64_t>(1U, width - 8U));
            for (std::uint32_t y = 0; y < height; ++y) {
                for (std::uint32_t marker = 0; marker < 8U &&
                     marker_x + marker < width; ++marker) {
                    const auto offset = static_cast<std::size_t>(y) * stride +
                        (marker_x + marker) * 4U;
                    pixels[offset] = 240U;
                    pixels[offset + 1U] = 48U;
                    pixels[offset + 2U] = 48U;
                }
            }
            const auto elapsed = std::chrono::steady_clock::now() - started;
            const auto timestamp = std::chrono::duration_cast<
                std::chrono::duration<std::int64_t, std::ratio<1, 10'000'000>>>(
                    elapsed).count();
            const auto publish_result = im_vcam_publish_bgra(
                pixels.data(), width, height, stride, timestamp);
            if (publish_result < 0) {
                print_hr("publish virtual camera frame", publish_result);
                break;
            }
            ++frames;
            const std::uint32_t restore_x = marker_x;
            for (std::uint32_t y = 0; y < height; ++y) {
                for (std::uint32_t marker = 0; marker < 8U &&
                     restore_x + marker < width; ++marker) {
                    const std::uint32_t x = restore_x + marker;
                    const auto offset = static_cast<std::size_t>(y) * stride +
                        x * 4U;
                    pixels[offset] = static_cast<std::uint8_t>(
                        48U + x * 32U / width);
                    pixels[offset + 1U] = static_cast<std::uint8_t>(
                        144U + y * 48U / height);
                    pixels[offset + 2U] = 224U;
                }
            }
            next_frame += std::chrono::duration_cast<
                std::chrono::steady_clock::duration>(frame_duration);
            std::this_thread::sleep_until(next_frame);
        }
        std::printf("published=%llu size=%ux%u fps=%u\n",
                    static_cast<unsigned long long>(frames), width, height,
                    frame_rate);
        exit_code = frames == 0 ? 1 : 0;
    }

    const auto stop_result = im_vcam_stop();
    if (stop_result < 0) {
        print_hr("stop virtual camera", stop_result);
        exit_code = 1;
    }
    CoUninitialize();
    return exit_code;
}
