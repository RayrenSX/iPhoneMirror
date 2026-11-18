#include "Media/ScreenContentGeometry.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {

using iPhoneMirror::media::DecodedFrame;
using iPhoneMirror::media::PixelFormat;
using iPhoneMirror::media::ScreenContentGeometry;
using iPhoneMirror::media::ScreenContentRect;
using namespace std::chrono_literals;

void require(bool condition, std::string_view message) {
    if (!condition) throw std::runtime_error(std::string(message));
}

struct Picture {
    std::uint32_t x{}, y{}, width{}, height{};
};

constexpr Picture Portrait{108, 0, 184, 400};
constexpr Picture Landscape{0, 108, 400, 184};
constexpr auto Epoch = std::chrono::steady_clock::time_point{10s};

std::uint32_t component_bytes(PixelFormat format) {
    return format == PixelFormat::P010 ? 2U : 1U;
}

void put_component(DecodedFrame& frame, std::size_t offset, std::uint16_t value) {
    frame.nv12.at(offset) = static_cast<std::uint8_t>(value);
    if (frame.pixel_format == PixelFormat::P010)
        frame.nv12.at(offset + 1U) = static_cast<std::uint8_t>(value >> 8U);
}

std::uint16_t component_code(const DecodedFrame& frame, std::uint8_t value) {
    return frame.pixel_format == PixelFormat::P010
        ? static_cast<std::uint16_t>(static_cast<std::uint16_t>(value) << 8U) : value;
}

DecodedFrame make_frame(std::uint32_t width, std::uint32_t height, Picture picture,
    PixelFormat format = PixelFormat::Nv12, std::uint32_t stride = 0,
    std::uint32_t allocation_height = 0) {
    DecodedFrame frame;
    frame.width = width;
    frame.height = height;
    frame.pixel_format = format;
    const auto bytes = component_bytes(format);
    frame.stride = static_cast<std::int32_t>(stride != 0 ? stride : ((width + 1U) & ~1U) * bytes);
    allocation_height = allocation_height != 0 ? allocation_height : height;
    const auto row_pitch = static_cast<std::size_t>(frame.stride);
    const auto uv_offset = row_pitch * allocation_height;
    frame.nv12.assign(uv_offset + row_pitch * ((allocation_height + 1U) / 2U), 0xD3);
    frame.timestamp_100ns = 123'456'789;
    frame.received_at = Epoch;
    frame.color.primaries = iPhoneMirror::coremedia::ColorPrimaries::DisplayP3;
    frame.color.transfer = iPhoneMirror::coremedia::TransferFunction::Srgb;
    frame.color.matrix = iPhoneMirror::coremedia::MatrixCoefficients::Bt709;
    frame.color.range = iPhoneMirror::coremedia::ColorRange::Limited;
    frame.color.hdr = {1234, 456, 1000, 5};
    for (std::uint32_t y = 0; y < allocation_height; ++y)
        for (std::uint32_t x = 0; x < width; ++x) {
            const bool visible = y < height && x >= picture.x && x < picture.x + picture.width &&
                y >= picture.y && y < picture.y + picture.height;
            const auto luma = visible ? static_cast<std::uint8_t>(64U + (x * 3U + y * 7U) % 144U) : 16U;
            put_component(frame, y * row_pitch + x * bytes,
                component_code(frame, static_cast<std::uint8_t>(luma)));
        }
    for (std::uint32_t y = 0; y < (allocation_height + 1U) / 2U; ++y)
        for (std::uint32_t x = 0; x < ((width + 1U) & ~1U); x += 2U) {
            const bool visible = y * 2U < height && x >= picture.x &&
                x < picture.x + picture.width && y * 2U >= picture.y &&
                y * 2U < picture.y + picture.height;
            const auto u = static_cast<std::uint8_t>(visible ? 84U + (x / 2U + y) % 34U : 128U);
            const auto v = static_cast<std::uint8_t>(visible ? 140U + (x / 2U + y * 3U) % 38U : 128U);
            put_component(frame, uv_offset + y * row_pitch + x * bytes, component_code(frame, u));
            put_component(frame, uv_offset + y * row_pitch + (x + 1U) * bytes, component_code(frame, v));
        }
    return frame;
}

DecodedFrame square(Picture picture = Landscape, PixelFormat format = PixelFormat::Nv12,
    std::uint32_t stride = 0, std::uint32_t allocation_height = 0) {
    return make_frame(400, 400, picture, format, stride, allocation_height);
}

void require_unchanged(const DecodedFrame& actual, const DecodedFrame& expected,
    std::string_view message) {
    require(actual.width == expected.width && actual.height == expected.height &&
        actual.stride == expected.stride && actual.pixel_format == expected.pixel_format &&
        actual.timestamp_100ns == expected.timestamp_100ns &&
        actual.received_at == expected.received_at && actual.nv12 == expected.nv12 &&
        actual.gpu_frame == expected.gpu_frame, message);
}

void require_exact_crop(const DecodedFrame& actual, const DecodedFrame& source,
    Picture area, std::uint32_t allocation_height = 400) {
    require(actual.width == area.width && actual.height == area.height,
        "normalization publishes the content dimensions without scaling");
    require(actual.timestamp_100ns == source.timestamp_100ns && actual.received_at == source.received_at,
        "normalization preserves PTS and receive time exactly");
    require(actual.pixel_format == source.pixel_format &&
        actual.color.primaries == source.color.primaries && actual.color.transfer == source.color.transfer &&
        actual.color.matrix == source.color.matrix && actual.color.range == source.color.range &&
        actual.color.hdr.max_content_light_level == source.color.hdr.max_content_light_level &&
        actual.color.hdr.max_frame_average_light_level == source.color.hdr.max_frame_average_light_level &&
        actual.color.hdr.max_mastering_luminance == source.color.hdr.max_mastering_luminance &&
        actual.color.hdr.min_mastering_luminance == source.color.hdr.min_mastering_luminance,
        "normalization preserves all color and HDR metadata exactly");
    const auto bytes = component_bytes(source.pixel_format);
    const auto source_stride = static_cast<std::size_t>(source.stride);
    const auto destination_stride = static_cast<std::size_t>(actual.stride);
    require(destination_stride >= area.width * bytes &&
        actual.nv12.size() >= destination_stride * area.height +
            destination_stride * ((area.height + 1U) / 2U), "crop returns complete Y and UV planes");
    for (std::uint32_t y = 0; y < area.height; ++y)
        for (std::uint32_t x = 0; x < area.width * bytes; ++x)
            require(actual.nv12[y * destination_stride + x] ==
                source.nv12[(y + area.y) * source_stride + area.x * bytes + x],
                "each retained luma byte is copied exactly from its original coordinate");
    const auto source_uv = source_stride * allocation_height;
    const auto destination_uv = destination_stride * area.height;
    for (std::uint32_t y = 0; y < area.height / 2U; ++y)
        for (std::uint32_t x = 0; x < area.width * bytes; ++x)
            require(actual.nv12[destination_uv + y * destination_stride + x] ==
                source.nv12[source_uv + (y + area.y / 2U) * source_stride + area.x * bytes + x],
                "each retained chroma byte preserves UV ordering and original coordinates");
}

DecodedFrame at(DecodedFrame frame, std::chrono::milliseconds offset) {
    frame.received_at = Epoch + offset;
    frame.timestamp_100ns += std::chrono::duration_cast<std::chrono::duration<std::int64_t, std::ratio<1, 10'000'000>>>(offset).count();
    return frame;
}

void learn_reference(ScreenContentGeometry& geometry, std::chrono::milliseconds offset = 0ms,
    PixelFormat format = PixelFormat::Nv12) {
    for (const auto step : {0ms, 50ms, 100ms}) {
        auto frame = at(make_frame(184, 400, {0, 0, 184, 400}, format), offset + step);
        const auto original = frame;
        require(!geometry.process(frame), "trusted rectangular reference frames are never cropped");
        require_unchanged(frame, original, "learning a reference leaves rectangular pixels and metadata unchanged");
    }
}

void require_rejected(ScreenContentGeometry& geometry, const DecodedFrame& picture,
    std::chrono::milliseconds offset, std::string_view message) {
    for (const auto step : {0ms, 50ms, 100ms, 150ms}) {
        auto frame = at(picture, offset + step);
        const auto original = frame;
        require(!geometry.process(frame), message);
        require_unchanged(frame, original, "rejected candidates never alter pixels or geometry");
    }
}

void require_stable_crop(ScreenContentGeometry& geometry, const DecodedFrame& picture,
    Picture area, std::chrono::milliseconds offset, std::uint32_t allocation_height = 400) {
    for (const auto step : {0ms, 50ms}) {
        auto frame = at(picture, offset + step);
        const auto original = frame;
        require(!geometry.process(frame), "a candidate remains uncropped before three frames and 100ms");
        require_unchanged(frame, original, "unstable candidates preserve the full canvas");
    }
    auto frame = at(picture, offset + 100ms);
    const auto original = frame;
    const auto rectangle = geometry.process(frame);
    require(rectangle && *rectangle == ScreenContentRect{area.x, area.y, area.width, area.height},
        "three stable candidates return the measured, chroma-aligned content rectangle");
    require_exact_crop(frame, original, area, allocation_height);
}

void test_reference_and_orientation() {
    ScreenContentGeometry no_panel;
    require_rejected(no_panel, square(), 0ms, "unknown panel and no reference never authorize a crop");

    ScreenContentGeometry no_reference;
    no_reference.configure(1206, 2622);
    require_rejected(no_reference, square(), 0ms,
        "a configured panel without observed rectangular reference never authorizes a crop");

    for (const auto area : {Portrait, Landscape}) {
        ScreenContentGeometry geometry;
        geometry.configure(1206, 2622);
        learn_reference(geometry);
        require_stable_crop(geometry, square(area), area, 150ms);
    }

    ScreenContentGeometry incomplete;
    incomplete.configure(1206, 2622);
    for (const auto step : {0ms, 50ms}) {
        auto frame = at(make_frame(184, 400, {0, 0, 184, 400}), step);
        require(!incomplete.process(frame), "two rectangular frames remain unchanged");
    }
    require_rejected(incomplete, square(), 100ms,
        "two rectangular frames do not establish the three-frame reference");

    ScreenContentGeometry wrong_reference;
    wrong_reference.configure(1206, 2622);
    for (const auto step : {0ms, 50ms, 100ms}) {
        auto frame = at(make_frame(400, 224, {0, 0, 400, 224}), step);
        require(!wrong_reference.process(frame), "foreign rectangular content is left unchanged");
    }
    require_rejected(wrong_reference, square(), 150ms,
        "a movie rectangle whose aspect disagrees with the panel cannot establish a reference");
}

void test_untrusted_content() {
    std::vector<DecodedFrame> rejected;
    rejected.push_back(square({0, 0, 400, 400}));
    rejected.push_back(square({0, 0, 0, 0}));
    rejected.push_back(square({0, 88, 400, 224})); // A legitimate 16:9 movie, not this panel.
    rejected.push_back(square({0, 80, 400, 184})); // Asymmetric composition.
    auto nonblack_border = square();
    std::fill_n(nonblack_border.nv12.begin(), 400, std::uint8_t{48});
    rejected.push_back(std::move(nonblack_border));
    auto one_pixel = square();
    one_pixel.nv12[5 * 400 + 5] = 64;
    rejected.push_back(std::move(one_pixel));
    auto chromatic_border = square();
    chromatic_border.nv12[400 * 400 + 5 * 400 + 4] = 32;
    rejected.push_back(std::move(chromatic_border));
    auto p010_one_pixel = square(Landscape, PixelFormat::P010);
    put_component(p010_one_pixel, 5 * 800 + 5 * 2, component_code(p010_one_pixel, 64));
    rejected.push_back(std::move(p010_one_pixel));
    auto p010_chromatic = square(Landscape, PixelFormat::P010);
    put_component(p010_chromatic, 400 * 800 + 5 * 800 + 4 * 2, component_code(p010_chromatic, 32));
    rejected.push_back(std::move(p010_chromatic));
    auto dark_page = square();
    std::fill(dark_page.nv12.begin(), dark_page.nv12.begin() + 400 * 400, std::uint8_t{16});
    for (std::uint32_t y = 180; y < 220; ++y)
        for (std::uint32_t x = 180; x < 220; ++x)
            dark_page.nv12[y * 400 + x] = 180;
    rejected.push_back(std::move(dark_page));

    for (const auto& frame : rejected) {
        ScreenContentGeometry geometry;
        geometry.configure(1206, 2622);
        learn_reference(geometry);
        require_rejected(geometry, frame, 150ms,
            "full square, black, foreign aspect, asymmetric, active border and dark-page content are rejected");
    }
}

void test_layouts_and_preservation() {
    for (const auto format : {PixelFormat::Nv12, PixelFormat::P010})
        for (const auto area : {Portrait, Landscape}) {
            const auto stride = format == PixelFormat::Nv12 ? 401U : 832U;
            const auto allocation = format == PixelFormat::Nv12 ? 400U : 416U;
            ScreenContentGeometry geometry;
            geometry.configure(1206, 2622);
            learn_reference(geometry, 0ms, format);
            require_stable_crop(geometry, square(area, format, stride, allocation), area, 150ms, allocation);
        }

    std::vector<DecodedFrame> malformed;
    auto short_luma = square();
    short_luma.nv12.resize(400 * 399);
    malformed.push_back(std::move(short_luma));
    auto short_chroma = square();
    short_chroma.nv12.resize(short_chroma.nv12.size() - 400);
    malformed.push_back(std::move(short_chroma));
    auto short_stride = square();
    short_stride.stride = 399;
    malformed.push_back(std::move(short_stride));
    auto negative_stride = square();
    negative_stride.stride = -400;
    malformed.push_back(std::move(negative_stride));
    auto odd_p010_stride = square(Landscape, PixelFormat::P010, 801);
    malformed.push_back(std::move(odd_p010_stride));
    auto trailing_byte = square();
    trailing_byte.nv12.push_back(0);
    malformed.push_back(std::move(trailing_byte));
    for (const auto& frame : malformed) {
        ScreenContentGeometry geometry;
        geometry.configure(1206, 2622);
        learn_reference(geometry);
        require_rejected(geometry, frame, 150ms, "malformed plane layouts never authorize a crop or overread");
    }
}

void test_stability_and_reset() {
    ScreenContentGeometry geometry;
    geometry.configure(1206, 2622);
    learn_reference(geometry);
    for (const auto time : {150ms, 160ms, 170ms}) {
        auto frame = at(square(), time);
        require(!geometry.process(frame), "three frames alone cannot bypass the 100ms observation period");
    }
    auto sufficient_time = at(square(), 250ms);
    require(geometry.process(sufficient_time).has_value(), "a stable candidate can mature after the time requirement");

    // Once the actual device returns to a rectangular frame, the next square
    // orientation must accumulate its own stable candidate sequence.
    auto returned = at(make_frame(184, 400, {0, 0, 184, 400}), 300ms);
    require(!geometry.process(returned), "returning to portrait preserves the unpadded frame");
    require_stable_crop(geometry, square(Portrait), Portrait, 350ms);

    geometry.reset();
    require_rejected(geometry, square(), 500ms, "a new session cannot reuse the previous session's reference");
    learn_reference(geometry, 700ms);
    require_stable_crop(geometry, square(), Landscape, 850ms);

    ScreenContentGeometry axis_change;
    axis_change.configure(1206, 2622);
    learn_reference(axis_change);
    for (const auto time : {150ms, 200ms}) {
        auto frame = at(square(Landscape), time);
        require(!axis_change.process(frame), "two old-orientation candidates remain unstable");
    }
    require_stable_crop(axis_change, square(Portrait), Portrait, 250ms);

    ScreenContentGeometry interrupted;
    interrupted.configure(1206, 2622);
    learn_reference(interrupted);
    for (const auto time : {150ms, 200ms}) {
        auto frame = at(square(), time);
        require(!interrupted.process(frame), "candidate waits before an invalid interruption");
    }
    auto invalid = at(square({0, 0, 0, 0}), 250ms);
    require(!interrupted.process(invalid), "all-black interruption is rejected");
    require_stable_crop(interrupted, square(), Landscape, 300ms);

    ScreenContentGeometry reversed_time;
    reversed_time.configure(1206, 2622);
    learn_reference(reversed_time);
    for (const auto time : {150ms, 200ms, 150ms}) {
        auto frame = at(square(), time);
        require(!reversed_time.process(frame), "nonmonotonic receive time cannot mature a candidate");
    }
    require_stable_crop(reversed_time, square(), Landscape, 250ms);

    ScreenContentGeometry size_change;
    size_change.configure(1206, 2622);
    learn_reference(size_change);
    for (const auto time : {150ms, 200ms}) {
        auto frame = at(square(), time);
        require(!size_change.process(frame), "the old square size still waits for stability");
    }
    constexpr Picture larger_landscape{0, 216, 800, 368};
    require_stable_crop(size_change, make_frame(800, 800, larger_landscape), larger_landscape, 250ms, 800);

    ScreenContentGeometry swapped_panel;
    swapped_panel.configure(1206, 2622);
    learn_reference(swapped_panel);
    for (const auto time : {150ms, 200ms}) {
        auto frame = at(square(), time);
        require(!swapped_panel.process(frame), "candidate remains stable across unchanged panel axes");
    }
    swapped_panel.configure(2622, 1206);
    auto third = at(square(), 250ms);
    require(swapped_panel.process(third).has_value(),
        "swapping the same known panel axes does not erase a valid session reference or stable candidate");
    swapped_panel.configure(1640, 2360);
    require_rejected(swapped_panel, square(), 300ms,
        "a genuinely different panel cannot reuse the old panel's reference");
}

void test_device_isolation_and_gpu() {
    ScreenContentGeometry first, second;
    first.configure(1206, 2622);
    second.configure(1206, 2622);
    learn_reference(first);
    require_rejected(second, square(), 150ms,
        "another device cannot inherit an established reference from the first device");
    require_stable_crop(first, square(), Landscape, 150ms);

    ScreenContentGeometry gpu;
    gpu.configure(1206, 2622);
    learn_reference(gpu);
    for (const auto time : {150ms, 200ms}) {
        auto frame = at(square(), time);
        require(!gpu.process(frame), "CPU candidates wait before the GPU-only interruption");
    }
    // This alias deliberately has no SharedGpuFrame backing object. A helper
    // that dereferences the opaque handle or invokes readback is incorrect;
    // the no-CPU-pixels branch must reject it without touching the pointer.
    auto marker = std::make_shared<int>(42);
    auto opaque = std::shared_ptr<const DecodedFrame::SharedGpuFrame>(marker,
        reinterpret_cast<const DecodedFrame::SharedGpuFrame*>(marker.get()));
    auto frame = at(square(), 250ms);
    frame.nv12.clear();
    frame.gpu_frame = opaque;
    const auto original = frame;
    require(!gpu.process(frame), "GPU-only frames do not trigger readback or crop");
    require_unchanged(frame, original, "GPU-only frame retains its opaque handle without materialization");
    require_stable_crop(gpu, square(), Landscape, 300ms);
    auto hybrid = at(square(), 450ms);
    hybrid.gpu_frame = opaque;
    const auto hybrid_original = hybrid;
    require(!gpu.process(hybrid), "a frame carrying a GPU handle cannot leave its CPU and GPU geometry inconsistent");
    require_unchanged(hybrid, hybrid_original, "hybrid GPU/CPU frames remain untouched");
}

void test_confirmed_dark_content_and_revocation() {
    ScreenContentGeometry geometry;
    geometry.configure(1206, 2622);
    learn_reference(geometry);
    require_stable_crop(geometry, square(), Landscape, 150ms);
    auto dark = at(square({0, 0, 0, 0}), 300ms);
    const auto dark_original = dark;
    const auto dark_rect = geometry.process(dark);
    require(dark_rect && *dark_rect == ScreenContentRect{0, 108, 400, 184},
        "an already confirmed screen retains its exact region when the screen becomes black");
    require_exact_crop(dark, dark_original, Landscape);

    auto outside_signal = at(square(), 350ms);
    outside_signal.nv12[5 * 400 + 5] = 64;
    const auto signal_original = outside_signal;
    require(!geometry.process(outside_signal), "one new signal pixel outside the confirmed region revokes crop immediately");
    require_unchanged(outside_signal, signal_original, "revocation retains the entire signalled canvas");
    require_rejected(geometry, square({0, 0, 0, 0}), 400ms,
        "dark content cannot resurrect a region after its confirmation was revoked");
    require_stable_crop(geometry, square(), Landscape, 600ms);

    ScreenContentGeometry pending;
    pending.configure(1206, 2622);
    learn_reference(pending);
    for (const auto time : {150ms, 151ms, 152ms}) {
        auto frame = at(square(), time);
        require(!pending.process(frame), "three bright frames before 100ms are still unconfirmed");
    }
    require_rejected(pending, square({0, 0, 0, 0}), 250ms,
        "an unconfirmed candidate cannot mature using later black frames");
    require_stable_crop(pending, square(), Landscape, 450ms);

    ScreenContentGeometry wrong_ratio;
    wrong_ratio.configure(1206, 2622);
    learn_reference(wrong_ratio);
    require_stable_crop(wrong_ratio, square(), Landscape, 150ms);
    auto movie = at(make_frame(400, 224, {0, 0, 400, 224}), 300ms);
    const auto movie_original = movie;
    require(!wrong_ratio.process(movie), "a later foreign-aspect rectangle is never cropped");
    require_unchanged(movie, movie_original, "the foreign-aspect rectangle remains exact");
    require_rejected(wrong_ratio, square(), 350ms,
        "a foreign-aspect rectangle revokes the previous screen reference");
    learn_reference(wrong_ratio, 550ms);
    require_stable_crop(wrong_ratio, square(), Landscape, 700ms);
}

void benchmark() {
    constexpr Picture area{0, 582, 2160, 996};
    constexpr unsigned count = 60;
    for (const auto format : {PixelFormat::Nv12, PixelFormat::P010}) {
        ScreenContentGeometry geometry;
        geometry.configure(1206, 2622);
        const auto reference = make_frame(996, 2160, {0, 0, 996, 2160}, format);
        for (const auto time : {0ms, 50ms, 100ms}) {
            auto frame = at(reference, time);
            require(!geometry.process(frame), "benchmark reference is left unchanged");
        }
        const auto canvas = make_frame(2160, 2160, area, format);
        for (const auto time : {150ms, 200ms, 250ms}) {
            auto frame = at(canvas, time);
            const auto result = geometry.process(frame);
            if (time == 250ms) require(result.has_value(), "benchmark enters the confirmed crop state");
        }
        double total_ms{}, maximum_ms{};
        for (unsigned index = 0; index < count; ++index) {
            // Restore the original canvas before starting the timer: only
            // process(), including its actual crop allocation/copy, is timed.
            auto frame = at(canvas, 300ms + std::chrono::milliseconds(17U * index));
            const auto started = std::chrono::steady_clock::now();
            const auto result = geometry.process(frame);
            const auto elapsed_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count();
            require(result && frame.width == area.width && frame.height == area.height,
                "every benchmark frame executes the confirmed crop path");
            total_ms += elapsed_ms;
            maximum_ms = std::max(maximum_ms, elapsed_ms);
        }
        std::cout << "Screen content geometry benchmark format=" <<
            (format == PixelFormat::P010 ? "P010" : "NV12") <<
            " canvas=2160x2160 frames=" << count <<
            " mean_ms=" << total_ms / count << " max_ms=" << maximum_ms << '\n';
    }
}

} // namespace

int main(int argc, char** argv) {
    try {
        test_reference_and_orientation();
        test_untrusted_content();
        test_layouts_and_preservation();
        test_stability_and_reset();
        test_device_isolation_and_gpu();
        test_confirmed_dark_content_and_revocation();
        std::cout << "Screen content geometry tests passed\n";
        if (argc == 2 && std::string_view(argv[1]) == "--benchmark") benchmark();
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
