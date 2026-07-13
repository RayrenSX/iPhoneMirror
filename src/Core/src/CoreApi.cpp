#include "iPhoneMirror/CoreApi.h"

#include "Device/DeviceManager.h"
#include "Capture/CaptureSession.h"
#include "Capture/WirelessCaptureSession.h"
#include "Capture/WirelessReceiverHub.h"
#include "Logging.h"
#include "Renderer/D3D11PreviewRenderer.h"
#include "Transport/LibUsb0Readiness.h"
#include "Transport/Socket.h"

#include <algorithm>
#include <cmath>
#include <cwchar>
#include <cstring>
#include <exception>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>
#include <memory>
#include <limits>
#include <chrono>
#include <unordered_map>
#include <Windows.h>

namespace {

std::mutex state_mutex;
bool initialized{};
thread_local std::wstring last_error;
iPhoneMirror::device::DeviceManager device_manager;
std::unique_ptr<iPhoneMirror::capture::ICaptureSession> capture_session;
std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> preview_renderer;
std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> wireless_receiver;
iPhoneMirror::capture::CapturePreferences capture_preferences;
float preview_corner_radius{0.1784F};
float preview_corner_exponent{2.36F};

struct MultiSessionContext {
    std::mutex mutex;
    std::unique_ptr<iPhoneMirror::capture::ICaptureSession> capture;
    std::unordered_map<HWND, std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer>> renderers;
    iPhoneMirror::capture::CapturePreferences preferences;
    float corner_radius{0.1784F};
    float corner_exponent{2.36F};
};

std::unordered_map<iPhoneMirror::SessionHandle, std::shared_ptr<MultiSessionContext>> multi_sessions;
iPhoneMirror::SessionHandle next_session_handle{1};

std::shared_ptr<const iPhoneMirror::media::DecodedFrame> latest_preview_frame() {
    std::scoped_lock lock(state_mutex);
    return capture_session ? capture_session->next_render_frame() : nullptr;
}

std::string narrow(const wchar_t* text) {
    if (!text || !*text) return {};
    const int input_length = static_cast<int>(std::wcslen(text));
    const int length = WideCharToMultiByte(CP_UTF8, 0, text, input_length, nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, input_length, result.data(), length, nullptr, nullptr);
    return result;
}

std::wstring product_type_for_udid(const wchar_t* udid) {
    if (!udid || !*udid) return {};
    try {
        for (const auto& device : device_manager.refresh())
            if (device.udid == udid) return device.product_type;
    } catch (...) {}
    return {};
}

std::wstring widen(std::string_view text) {
    if (text.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0) return L"未知错误";
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), length);
    return result;
}

template <std::size_t Size>
void copy_text(wchar_t (&destination)[Size], const std::wstring& source) {
    wcsncpy_s(destination, source.c_str(), _TRUNCATE);
}

std::int32_t fail(iPhoneMirror::Result result, std::wstring message) {
    last_error = std::move(message);
    return static_cast<std::int32_t>(result);
}

void fill_device(iPhoneMirror::DeviceInfo& output, const iPhoneMirror::device::DeviceRecord& input) {
    output = {};
    output.struct_size = sizeof(output);
    output.api_version = iPhoneMirror::ApiVersion;
    output.device_id = input.device_id;
    output.mux_port = input.mux_port;
    output.state = input.state;
    output.usb_connected = input.usb_connected;
    output.pair_record_present = input.pair_record_present;
    output.lockdown_accessible = input.lockdown_accessible;
    copy_text(output.udid, input.udid);
    copy_text(output.name, input.name);
    copy_text(output.product_type, input.product_type);
    copy_text(output.os_version, input.os_version);
    copy_text(output.connection_type, input.connection_type);
    copy_text(output.status, input.status);
}

void fill_wireless_device(iPhoneMirror::DeviceInfo& output,
    const iPhoneMirror::capture::WirelessDeviceSnapshot& input) {
    output = {};
    output.struct_size = sizeof(output);
    output.api_version = iPhoneMirror::ApiVersion;
    output.state = iPhoneMirror::ConnectionState::Ready;
    output.usb_connected = 0;
    output.pair_record_present = 1;
    output.lockdown_accessible = 1;
    copy_text(output.udid, L"airplay://" + input.id);
    copy_text(output.name, input.name.empty() ? L"iPhone" : input.name);
    copy_text(output.product_type, L"AirPlay");
    copy_text(output.connection_type, L"AirPlay");
    copy_text(output.status, L"Connected via AirPlay");
}

std::wstring wireless_device_id(const wchar_t* value) {
    std::wstring result = value ? value : L"";
    constexpr std::wstring_view prefix = L"airplay://";
    if (result.starts_with(prefix)) result.erase(0, prefix.size());
    return result;
}

std::uint8_t clamp_byte(int value) noexcept {
    return static_cast<std::uint8_t>(std::clamp(value, 0, 255));
}

std::size_t nv12_allocated_height(const iPhoneMirror::media::DecodedFrame& frame,
    std::size_t stride) noexcept {
    if (stride == 0) return 0;
    const auto candidate = (frame.nv12.size() * 2U) / (stride * 3U);
    if (candidate >= frame.height) {
        const auto required = stride * candidate + stride * ((candidate + 1U) / 2U);
        if (required <= frame.nv12.size()) return candidate;
    }
    return frame.height;
}

bool nv12_to_bgra(const iPhoneMirror::media::DecodedFrame& frame, std::uint8_t* output) {
    if (!output || frame.width == 0 || frame.height == 0) return false;
    const auto source_stride = static_cast<std::size_t>(std::abs(frame.stride));
    if (source_stride < frame.width) return false;
    const auto allocated_height = nv12_allocated_height(frame, source_stride);
    const auto y_bytes = source_stride * allocated_height;
    const auto required_source = y_bytes + source_stride * ((allocated_height + 1U) / 2U);
    if (frame.nv12.size() < required_source) return false;
    const auto* y_plane = frame.nv12.data();
    const auto* uv_plane = y_plane + y_bytes;
    // The MFT reports a 2622-line visible picture in a 2624-line allocation.
    // The extra rows are allocation padding at the end, not a visible prefix.
    // The old black-row heuristic could crop legitimate dark status bars.
    constexpr std::uint32_t leading_padding_rows = 0;
    for (std::uint32_t y = 0; y < frame.height; ++y) {
        auto* destination = output + static_cast<std::size_t>(y) * frame.width * 4U;
        const std::uint32_t source_y = y + leading_padding_rows;
        if (source_y >= frame.height) {
            std::fill(destination, destination + static_cast<std::size_t>(frame.width) * 4U, std::uint8_t{0});
            for (std::uint32_t x = 0; x < frame.width; ++x) destination[x * 4U + 3U] = 255;
            continue;
        }
        const auto* luma = y_plane + static_cast<std::size_t>(source_y) * source_stride;
        const auto* chroma = uv_plane + static_cast<std::size_t>(source_y / 2U) * source_stride;
        for (std::uint32_t x = 0; x < frame.width; ++x) {
            const int c = std::max(0, static_cast<int>(luma[x]) - 16);
            const int d = static_cast<int>(chroma[x & ~1U]) - 128;
            const int e = static_cast<int>(chroma[(x & ~1U) + 1U]) - 128;
            destination[x * 4U + 0U] = clamp_byte((298 * c + 541 * d + 128) >> 8);
            destination[x * 4U + 1U] = clamp_byte((298 * c - 55 * d - 136 * e + 128) >> 8);
            destination[x * 4U + 2U] = clamp_byte((298 * c + 459 * e + 128) >> 8);
            destination[x * 4U + 3U] = 255;
        }
    }

    // Some iOS 26/MFT combinations leave the first few decoded rows with
    // stale chroma. After conversion those rows are an unmistakable solid
    // green strip; remove only rows that are overwhelmingly that pattern.
    std::uint32_t green_padding_rows{};
    const auto row_bytes = static_cast<std::size_t>(frame.width) * 4U;
    for (; green_padding_rows < std::min<std::uint32_t>(16, frame.height); ++green_padding_rows) {
        const auto* row = output + static_cast<std::size_t>(green_padding_rows) * row_bytes;
        std::uint32_t green_pixels{};
        for (std::uint32_t x = 0; x < frame.width; ++x) {
            const auto* pixel = row + static_cast<std::size_t>(x) * 4U;
            if (pixel[0] <= 8 && pixel[2] <= 8 && pixel[1] >= 32) ++green_pixels;
        }
        if (green_pixels * 10U < frame.width * 9U) break;
    }
    if (green_padding_rows != 0 && green_padding_rows < frame.height) {
        const auto kept_rows = static_cast<std::size_t>(frame.height - green_padding_rows);
        std::memmove(output, output + static_cast<std::size_t>(green_padding_rows) * row_bytes,
            kept_rows * row_bytes);
        auto* fill = output + kept_rows * row_bytes;
        std::fill(fill, fill + static_cast<std::size_t>(green_padding_rows) * row_bytes, std::uint8_t{0});
        for (std::uint32_t y = 0; y < green_padding_rows; ++y) {
            auto* row = output + static_cast<std::size_t>(kept_rows + y) * row_bytes;
            for (std::uint32_t x = 0; x < frame.width; ++x) row[x * 4U + 3U] = 255;
        }
    }
    return true;
}

bool nv12_to_bgra_scaled(const iPhoneMirror::media::DecodedFrame& frame,
    std::uint8_t* output, std::uint32_t output_width, std::uint32_t output_height) {
    if (!output || frame.width == 0 || frame.height == 0 || output_width == 0 || output_height == 0) return false;
    const auto source_stride = static_cast<std::size_t>(std::abs(frame.stride));
    if (source_stride < frame.width) return false;
    const auto allocated_height = nv12_allocated_height(frame, source_stride);
    const auto y_bytes = source_stride * allocated_height;
    const auto required_source = y_bytes + source_stride * ((allocated_height + 1U) / 2U);
    if (frame.nv12.size() < required_source) return false;
    const auto* y_plane = frame.nv12.data();
    const auto* uv_plane = y_plane + y_bytes;

    constexpr std::uint32_t leading_padding_rows = 0;

    // Build the nearest-neighbour maps once per output geometry. The old
    // inner loop performed two 64-bit divisions for every output pixel;
    // at 960x2087 this is over four million divisions per frame and can
    // consume more time than the H.264 decode itself.
    static thread_local std::uint32_t cached_source_width{};
    static thread_local std::uint32_t cached_source_height{};
    static thread_local std::uint32_t cached_output_width{};
    static thread_local std::uint32_t cached_output_height{};
    static thread_local std::uint32_t cached_leading_padding_rows{std::numeric_limits<std::uint32_t>::max()};
    static thread_local std::vector<std::uint32_t> x_map;
    static thread_local std::vector<std::uint32_t> y_map;
    if (cached_source_width != frame.width || cached_source_height != frame.height ||
        cached_output_width != output_width || cached_output_height != output_height ||
        cached_leading_padding_rows != leading_padding_rows) {
        x_map.resize(output_width);
        y_map.resize(output_height);
        for (std::uint32_t x = 0; x < output_width; ++x) {
            x_map[x] = std::min<std::uint32_t>(frame.width - 1U,
                static_cast<std::uint32_t>((static_cast<std::uint64_t>(x) * frame.width) / output_width));
        }
        for (std::uint32_t y = 0; y < output_height; ++y) {
            y_map[y] = std::min<std::uint32_t>(frame.height - 1U,
                static_cast<std::uint32_t>((static_cast<std::uint64_t>(y) * frame.height) / output_height) +
                leading_padding_rows);
        }
        cached_source_width = frame.width;
        cached_source_height = frame.height;
        cached_output_width = output_width;
        cached_output_height = output_height;
        cached_leading_padding_rows = leading_padding_rows;
    }

    for (std::uint32_t y = 0; y < output_height; ++y) {
        auto* destination = output + static_cast<std::size_t>(y) * output_width * 4U;
        const auto source_y = y_map[y];
        const auto* luma = y_plane + static_cast<std::size_t>(source_y) * source_stride;
        const auto* chroma = uv_plane + static_cast<std::size_t>(source_y / 2U) * source_stride;
        for (std::uint32_t x = 0; x < output_width; ++x) {
            const auto source_x = x_map[x];
            const auto chroma_x = std::min<std::uint32_t>(
                static_cast<std::uint32_t>(source_stride - 2U), source_x & ~1U);
            const int c = std::max(0, static_cast<int>(luma[source_x]) - 16);
            const int d = static_cast<int>(chroma[chroma_x]) - 128;
            const int e = static_cast<int>(chroma[chroma_x + 1U]) - 128;
            auto* pixel = destination + static_cast<std::size_t>(x) * 4U;
            pixel[0] = clamp_byte((298 * c + 541 * d + 128) >> 8);
            pixel[1] = clamp_byte((298 * c - 55 * d - 136 * e + 128) >> 8);
            pixel[2] = clamp_byte((298 * c + 459 * e + 128) >> 8);
            pixel[3] = 255;
        }
    }

    // Apply the same green-padding guard as the full-size path, in the
    // already-downscaled buffer so the GUI never receives a green strip.
    std::uint32_t green_rows{};
    for (; green_rows < std::min<std::uint32_t>(16, output_height); ++green_rows) {
        const auto* row = output + static_cast<std::size_t>(green_rows) * output_width * 4U;
        std::uint32_t green_pixels{};
        for (std::uint32_t x = 0; x < output_width; ++x) {
            const auto* pixel = row + static_cast<std::size_t>(x) * 4U;
            if (pixel[0] <= 8 && pixel[2] <= 8 && pixel[1] >= 32) ++green_pixels;
        }
        if (green_pixels * 10U < output_width * 9U) break;
    }
    if (green_rows != 0 && green_rows < output_height) {
        const auto row_bytes = static_cast<std::size_t>(output_width) * 4U;
        const auto kept_rows = static_cast<std::size_t>(output_height - green_rows);
        std::memmove(output, output + static_cast<std::size_t>(green_rows) * row_bytes,
            kept_rows * row_bytes);
        auto* fill = output + kept_rows * row_bytes;
        std::fill(fill, fill + static_cast<std::size_t>(green_rows) * row_bytes, std::uint8_t{0});
        for (std::uint32_t y = 0; y < green_rows; ++y) {
            auto* row = output + (kept_rows + y) * row_bytes;
            for (std::uint32_t x = 0; x < output_width; ++x) row[x * 4U + 3U] = 255;
        }
    }
    return true;
}

bool valid_video_preferences(std::uint32_t width, std::uint32_t height,
    std::uint32_t target_fps) noexcept {
    if ((width == 0) != (height == 0)) return false;
    if (width != 0 && (width < 16 || height < 16 || width > 8192 || height > 8192)) {
        return false;
    }
    return target_fps <= 120;
}

std::int32_t start_capture_locked(const wchar_t* udid,
    const iPhoneMirror::capture::CapturePreferences& preferences) {
    if (!udid || !*udid) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"必须选择 iPhone");
    }
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    try {
        const auto serial = narrow(udid);
        iPhoneMirror::logging::write(std::format(
            "im_start_capture udid={} render_limit={}x{} target_fps={} play_audio={} volume={:.3f}",
            serial, preferences.render_max_width, preferences.render_max_height,
            preferences.target_fps, preferences.play_audio, preferences.audio_volume));
        if (preview_renderer) {
            preview_renderer->set_render_size_limit(
                preferences.render_max_width, preferences.render_max_height);
            preview_renderer->set_max_fps(preferences.target_fps);
        }
        if (capture_session) capture_session->stop();
        auto usb_capture = std::make_unique<iPhoneMirror::capture::CaptureSession>(
            serial, preferences, product_type_for_udid(udid));
        usb_capture->start(false);
        capture_session = std::move(usb_capture);
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        capture_session.reset();
        return fail(iPhoneMirror::Result::TransportUnavailable,
            L"USB 后端无法打开所选 iPhone：" + widen(error.what()));
    }
}

} // namespace

std::int32_t IM_CALL im_initialize() {
    std::scoped_lock lock(state_mutex);
    try {
        iPhoneMirror::logging::initialize();
        iPhoneMirror::logging::write("im_initialize");
        iPhoneMirror::transport::ensure_winsock();
        initialized = true;
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception&) {
        return fail(iPhoneMirror::Result::InternalError, L"初始化 Winsock 失败");
    }
}

void IM_CALL im_shutdown() {
    std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> renderer;
    std::vector<std::shared_ptr<MultiSessionContext>> sessions;
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        renderer = std::move(preview_renderer);
        for (auto& [_, session] : multi_sessions) sessions.push_back(std::move(session));
        multi_sessions.clear();
        receiver = std::move(wireless_receiver);
    }
    // Join the render thread without holding state_mutex: its frame provider
    // briefly takes that mutex to snapshot the active capture session.
    renderer.reset();
    for (auto& context : sessions) {
        std::vector<std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer>> session_renderers;
        {
            std::scoped_lock lock(context->mutex);
            for (auto& [_, session_preview] : context->renderers)
                session_renderers.push_back(std::move(session_preview));
            context->renderers.clear();
        }
        session_renderers.clear();
        if (context->capture) context->capture->stop();
        context->capture.reset();
    }
    if (receiver) receiver->stop();
    {
        std::scoped_lock lock(state_mutex);
        iPhoneMirror::logging::write("im_shutdown");
        if (capture_session) capture_session->stop();
        capture_session.reset();
        initialized = false;
        iPhoneMirror::logging::shutdown();
    }
}

std::uint32_t IM_CALL im_api_version() { return iPhoneMirror::ApiVersion; }

std::int32_t IM_CALL im_refresh_devices(iPhoneMirror::DeviceInfo* devices, std::uint32_t* count) {
    if (!count) return fail(iPhoneMirror::Result::InvalidArgument, L"count 不能为空");
    {
        // DeviceManager is stateless and usbmux discovery does not touch the
        // active capture session. Holding state_mutex across Lockdown network
        // round trips stalls the D3D frame provider and makes a manual
        // multi-device refresh look like a frozen preview.
        std::scoped_lock lock(state_mutex);
        if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    try {
        const auto records = device_manager.refresh();
        const auto required = static_cast<std::uint32_t>(records.size());
        if (!devices) {
            *count = required;
            last_error.clear();
            return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
        }
        const auto capacity = *count;
        *count = required;
        if (capacity < required) return fail(iPhoneMirror::Result::BufferTooSmall, L"设备列表缓冲区不足");
        for (std::size_t index = 0; index < records.size(); ++index) fill_device(devices[index], records[index]);
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (...) {
        return fail(iPhoneMirror::Result::InternalError, L"刷新 Apple 设备时发生异常");
    }
}

std::int32_t IM_CALL im_wireless_receiver_start(const wchar_t* receiver_name,
    const wchar_t* host_path) {
    if (!receiver_name || !*receiver_name || !host_path || !*host_path)
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"Wireless receiver name and host path are required");
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized)
            return fail(iPhoneMirror::Result::NotInitialized, L"Core is not initialized");
        if (!wireless_receiver)
            wireless_receiver = std::make_shared<iPhoneMirror::capture::WirelessReceiverHub>();
        receiver = wireless_receiver;
    }
    try {
        receiver->start(receiver_name, host_path);
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::TransportUnavailable,
            L"Could not start AirPlay receiver: " + widen(error.what()));
    }
}

void IM_CALL im_wireless_receiver_stop() {
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        receiver = wireless_receiver;
    }
    if (receiver) receiver->stop();
}

std::int32_t IM_CALL im_wireless_receiver_get_status(
    std::int32_t* running, std::int32_t* ready) {
    if (!running || !ready)
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"Wireless receiver status outputs are required");
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized)
            return fail(iPhoneMirror::Result::NotInitialized, L"Core is not initialized");
        receiver = wireless_receiver;
    }
    *running = receiver && receiver->running() ? 1 : 0;
    *ready = receiver && receiver->ready() ? 1 : 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_refresh_wireless_devices(
    iPhoneMirror::DeviceInfo* devices, std::uint32_t* count) {
    if (!count)
        return fail(iPhoneMirror::Result::InvalidArgument, L"count cannot be null");
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized)
            return fail(iPhoneMirror::Result::NotInitialized, L"Core is not initialized");
        receiver = wireless_receiver;
    }
    const auto records = receiver ? receiver->devices()
                                  : std::vector<iPhoneMirror::capture::WirelessDeviceSnapshot>{};
    const auto required = static_cast<std::uint32_t>(records.size());
    if (!devices) {
        *count = required;
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    }
    const auto capacity = *count;
    *count = required;
    if (capacity < required)
        return fail(iPhoneMirror::Result::BufferTooSmall,
            L"Wireless device list buffer is too small");
    for (std::size_t index = 0; index < records.size(); ++index)
        fill_wireless_device(devices[index], records[index]);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_environment(iPhoneMirror::EnvironmentInfo* environment) {
    if (!environment || environment->struct_size != sizeof(iPhoneMirror::EnvironmentInfo)) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"EnvironmentInfo 结构版本不匹配");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    try {
        const auto info = device_manager.environment();
        environment->api_version = iPhoneMirror::ApiVersion;
        environment->apple_mobile_device_service_installed = info.service_installed;
        environment->apple_mobile_device_service_running = info.service_running;
        environment->standard_usbmux_available = info.standard_mux;
        environment->capture_usbmux_available = info.capture_mux;
        environment->physical_apple_usb_devices = info.physical_device_count;
        copy_text(environment->diagnostic, info.diagnostic);
        environment->libusb_runtime_available = info.libusb_runtime;
        environment->usbdk_backend_available = info.usbdk_backend;
        environment->libusb_apple_devices = info.libusb_apple_devices;
        copy_text(environment->libusb_version, info.libusb_version);
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (...) {
        return fail(iPhoneMirror::Result::InternalError, L"读取驱动环境时发生异常");
    }
}

std::int32_t IM_CALL im_is_libusb0_device_available(
    const wchar_t* udid, std::int32_t* available) {
    if (!available) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"available cannot be null");
    }
    *available = 0;
    if (!udid || !*udid) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"udid cannot be empty");
    }

    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized,
            L"core is not initialized");
    }
    try {
        const auto serial = narrow(udid);
        if (serial.empty()) {
            return fail(iPhoneMirror::Result::InvalidArgument,
                L"udid could not be converted to UTF-8");
        }
        *available = iPhoneMirror::transport::is_libusb0_device_available(serial) ? 1 : 0;
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::InternalError,
            L"libusb0 device probe failed: " + widen(error.what()));
    } catch (...) {
        return fail(iPhoneMirror::Result::InternalError,
            L"libusb0 device probe failed");
    }
}

std::int32_t IM_CALL im_start_capture_ex(const wchar_t* udid, std::int32_t play_audio) {
    std::scoped_lock lock(state_mutex);
    auto preferences = capture_preferences;
    preferences.play_audio = play_audio != 0;
    return start_capture_locked(udid, preferences);
}

std::int32_t IM_CALL im_start_capture(const wchar_t* udid) {
    return im_start_capture_ex(udid, 1);
}

std::int32_t IM_CALL im_start_capture_with_options(const wchar_t* udid,
    const iPhoneMirror::CaptureOptions* options) {
    if (!options || options->struct_size != sizeof(iPhoneMirror::CaptureOptions) ||
        !valid_video_preferences(options->requested_width, options->requested_height,
            options->target_fps) ||
        !std::isfinite(options->audio_volume) || options->audio_volume < 0.0F ||
        options->audio_volume > 1.0F) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"CaptureOptions 参数无效");
    }
    iPhoneMirror::capture::CapturePreferences preferences{
        .render_max_width = options->requested_width,
        .render_max_height = options->requested_height,
        .target_fps = options->target_fps,
        .play_audio = options->play_audio != 0,
        .audio_volume = options->audio_volume,
        .usb_requested_width = options->reserved[0],
        .usb_requested_height = options->reserved[1],
        .usb_projection_mode = options->reserved[2] <= 2
            ? static_cast<iPhoneMirror::capture::UsbProjectionMode>(options->reserved[2])
            : iPhoneMirror::capture::UsbProjectionMode::Demo,
    };
    std::scoped_lock lock(state_mutex);
    capture_preferences = preferences;
    return start_capture_locked(udid, preferences);
}

std::int32_t IM_CALL im_stop_capture() {
    std::scoped_lock lock(state_mutex);
    iPhoneMirror::logging::write("im_stop_capture");
    if (capture_session) capture_session->stop();
    if (preview_renderer) preview_renderer->clear();
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_capture_status(iPhoneMirror::CaptureStatus* status) {
    if (!status || status->struct_size != sizeof(iPhoneMirror::CaptureStatus)) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"CaptureStatus 结构版本不匹配");
    }
    std::scoped_lock lock(state_mutex);
    status->api_version = iPhoneMirror::ApiVersion;
    if (!capture_session) {
        status->state = iPhoneMirror::CaptureState::Idle;
        status->width = status->height = 0;
        status->fps = status->latency_ms = 0;
        status->video_frames = status->audio_packets = 0;
        status->audio_sample_rate = status->audio_channels = 0;
        copy_text(status->message, L"等待设备");
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    }
    const auto snapshot = capture_session->snapshot();
    status->state = static_cast<iPhoneMirror::CaptureState>(snapshot.state);
    status->width = snapshot.width;
    status->height = snapshot.height;
    status->fps = snapshot.fps;
    status->latency_ms = snapshot.latency_ms;
    status->video_frames = snapshot.video_frames;
    status->audio_packets = snapshot.audio_packets;
    status->audio_sample_rate = snapshot.audio_sample_rate;
    status->audio_channels = snapshot.audio_channels;
    copy_text(status->message, snapshot.message);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_latest_video_timestamp(std::int64_t* timestamp_100ns) {
    if (!timestamp_100ns) return fail(iPhoneMirror::Result::InvalidArgument, L"视频时间戳指针无效");
    std::scoped_lock lock(state_mutex);
    if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    if (!capture_session) {
        *timestamp_100ns = 0;
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    }
    *timestamp_100ns = capture_session->latest_frame_timestamp();
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_copy_latest_video_frame(iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::VideoFrameInfo) || !buffer_size) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"VideoFrameInfo 结构版本不匹配");
    }
    std::shared_ptr<const iPhoneMirror::media::DecodedFrame> frame;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
        if (!capture_session) return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"当前没有投屏会话");
        frame = capture_session->latest_frame();
    }
    if (!frame) return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"正在等待首个解码视频帧");
    const auto required_64 = static_cast<std::uint64_t>(frame->width) * frame->height * 4U;
    if (required_64 > std::numeric_limits<std::uint32_t>::max()) {
        return fail(iPhoneMirror::Result::InternalError, L"视频帧尺寸过大");
    }
    const auto required = static_cast<std::uint32_t>(required_64);
    info->api_version = iPhoneMirror::ApiVersion;
    info->width = frame->width;
    info->height = frame->height;
    info->stride = frame->width * 4U;
    info->pixel_format = 1;
    info->timestamp_100ns = frame->timestamp_100ns;
    const auto capacity = *buffer_size;
    *buffer_size = required;
    if (!buffer || capacity < required) return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    if (!nv12_to_bgra(*frame, buffer)) return fail(iPhoneMirror::Result::ProtocolError, L"NV12 视频帧布局无效");
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_copy_latest_video_frame_scaled(iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size,
    std::uint32_t max_width, std::uint32_t max_height) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::VideoFrameInfo) || !buffer_size ||
        max_width == 0 || max_height == 0) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"缩放视频帧参数无效");
    }
    std::shared_ptr<const iPhoneMirror::media::DecodedFrame> frame;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
        if (!capture_session) return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"当前没有投屏会话");
        frame = capture_session->latest_frame();
    }
    if (!frame || frame->width == 0 || frame->height == 0) {
        return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"正在等待首个解码视频帧");
    }
    const auto width_scale = static_cast<double>(max_width) / frame->width;
    const auto height_scale = static_cast<double>(max_height) / frame->height;
    const auto scale = std::min(1.0, std::min(width_scale, height_scale));
    const auto output_width = std::max<std::uint32_t>(1,
        static_cast<std::uint32_t>(std::lround(frame->width * scale)));
    const auto output_height = std::max<std::uint32_t>(1,
        static_cast<std::uint32_t>(std::lround(frame->height * scale)));
    const auto required_64 = static_cast<std::uint64_t>(output_width) * output_height * 4U;
    if (required_64 > std::numeric_limits<std::uint32_t>::max()) {
        return fail(iPhoneMirror::Result::InternalError, L"缩放视频帧尺寸过大");
    }
    const auto required = static_cast<std::uint32_t>(required_64);
    info->api_version = iPhoneMirror::ApiVersion;
    info->width = output_width;
    info->height = output_height;
    info->stride = output_width * 4U;
    info->pixel_format = 1;
    info->timestamp_100ns = frame->timestamp_100ns;
    const auto capacity = *buffer_size;
    *buffer_size = required;
    if (!buffer || capacity < required) return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    const auto conversion_started = std::chrono::steady_clock::now();
    if (!nv12_to_bgra_scaled(*frame, buffer, output_width, output_height)) {
        return fail(iPhoneMirror::Result::ProtocolError, L"NV12 缩放视频帧布局无效");
    }
    static std::uint64_t conversion_count{};
    const auto conversion_number = ++conversion_count;
    if (conversion_number <= 3 || conversion_number % 60 == 0) {
        const auto elapsed = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - conversion_started).count();
        iPhoneMirror::logging::write(std::format(
            "preview_copy n={} source={}x{} output={}x{} conversion_ms={:.3f}",
            conversion_number, frame->width, frame->height, output_width, output_height, elapsed));
    }
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_attach_preview_window(void* hwnd) {
    const auto window = static_cast<HWND>(hwnd);
    if (!window || !IsWindow(window)) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"预览窗口句柄无效");
    }
    std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> previous;
    std::uint32_t target_fps{};
    std::uint32_t render_max_width{};
    std::uint32_t render_max_height{};
    float corner_radius{};
    float corner_exponent{};
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized) {
            return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
        }
        target_fps = capture_session ? capture_session->target_fps() : capture_preferences.target_fps;
        render_max_width = capture_preferences.render_max_width;
        render_max_height = capture_preferences.render_max_height;
        corner_radius = preview_corner_radius;
        corner_exponent = preview_corner_exponent;
        previous = std::move(preview_renderer);
    }
    previous.reset();
    try {
        auto renderer = std::make_unique<iPhoneMirror::renderer::D3D11PreviewRenderer>(
            window, latest_preview_frame);
        renderer->set_render_size_limit(render_max_width, render_max_height);
        renderer->set_max_fps(target_fps);
        renderer->set_corner_profile(corner_radius, corner_exponent);
        std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> displaced;
        bool installed{};
        {
            std::scoped_lock lock(state_mutex);
            // Another attach or shutdown can complete while D3D initializes.
            // Never destroy/join that renderer while holding state_mutex: its
            // frame provider briefly takes the same lock.
            if (initialized) {
                renderer->set_max_fps(capture_session
                    ? capture_session->target_fps()
                    : capture_preferences.target_fps);
                renderer->set_render_size_limit(
                    capture_preferences.render_max_width,
                    capture_preferences.render_max_height);
                renderer->set_corner_profile(preview_corner_radius, preview_corner_exponent);
                displaced = std::move(preview_renderer);
                preview_renderer = std::move(renderer);
                last_error.clear();
                installed = true;
            }
        }
        displaced.reset();
        if (!installed) {
            return fail(iPhoneMirror::Result::NotInitialized, L"核心已在预览初始化期间关闭");
        }
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::InternalError,
            L"D3D11 预览初始化失败：" + widen(error.what()));
    }
}

void IM_CALL im_detach_preview_window() {
    std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> renderer;
    {
        std::scoped_lock lock(state_mutex);
        renderer = std::move(preview_renderer);
    }
    renderer.reset();
}

std::int32_t IM_CALL im_force_preview_refresh() {
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    if (!preview_renderer) {
        return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"当前没有已连接的预览窗口");
    }
    preview_renderer->request_refresh();
    iPhoneMirror::logging::write("preview refresh requested");
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_preview_corner_profile(float normalized_radius,
    float curve_exponent) {
    if (!std::isfinite(normalized_radius) || !std::isfinite(curve_exponent) ||
        normalized_radius < 0.0F || normalized_radius > 0.5F ||
        curve_exponent < 1.5F || curve_exponent > 8.0F) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"预览圆角参数无效");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    preview_corner_radius = normalized_radius;
    preview_corner_exponent = curve_exponent;
    if (preview_renderer) {
        preview_renderer->set_corner_profile(normalized_radius, curve_exponent);
    }
    iPhoneMirror::logging::write(std::format(
        "preview corner_profile radius={:.5f} exponent={:.3f} enabled={}",
        normalized_radius, curve_exponent, normalized_radius > 0.0F));
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_video_preferences(std::uint32_t max_width,
    std::uint32_t max_height, std::uint32_t max_fps) {
    if (!valid_video_preferences(max_width, max_height, max_fps)) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"本地渲染尺寸或帧率参数无效");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    capture_preferences.render_max_width = max_width;
    capture_preferences.render_max_height = max_height;
    capture_preferences.target_fps = max_fps;
    if (capture_session) capture_session->set_target_fps(max_fps);
    if (preview_renderer) {
        preview_renderer->set_render_size_limit(max_width, max_height);
        preview_renderer->set_max_fps(max_fps);
    }
    iPhoneMirror::logging::write(std::format(
        "video preferences local_render_limit={}x{} target_fps={} usb_renegotiated=false",
        capture_preferences.render_max_width, capture_preferences.render_max_height,
        max_fps));
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_audio_enabled(std::int32_t enabled) {
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    capture_preferences.play_audio = enabled != 0;
    if (capture_session) capture_session->set_audio_enabled(enabled != 0);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_audio_volume(float volume) {
    if (!std::isfinite(volume) || volume < 0.0F || volume > 1.0F) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"音量必须位于 0.0 到 1.0 之间");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    capture_preferences.audio_volume = volume;
    if (capture_session) capture_session->set_audio_volume(volume);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_create(const wchar_t* udid,
    const iPhoneMirror::CaptureOptions* options, iPhoneMirror::SessionHandle* handle) {
    if (!udid || !*udid || !handle || !options ||
        options->struct_size != sizeof(iPhoneMirror::CaptureOptions) ||
        !valid_video_preferences(options->requested_width, options->requested_height,
            options->target_fps) || !std::isfinite(options->audio_volume) ||
        options->audio_volume < 0.0F || options->audio_volume > 1.0F) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid multi-device session options");
    }
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"Core is not initialized");
    }
    try {
        auto context = std::make_shared<MultiSessionContext>();
        context->preferences = {
            .render_max_width = options->requested_width,
            .render_max_height = options->requested_height,
            .target_fps = options->target_fps,
            .play_audio = options->play_audio != 0,
            .audio_volume = options->audio_volume,
            .usb_requested_width = options->reserved[0],
            .usb_requested_height = options->reserved[1],
            .usb_projection_mode = options->reserved[2] <= 2
                ? static_cast<iPhoneMirror::capture::UsbProjectionMode>(options->reserved[2])
                : iPhoneMirror::capture::UsbProjectionMode::Demo,
        };
        auto usb_capture = std::make_unique<iPhoneMirror::capture::CaptureSession>(
            narrow(udid), context->preferences, product_type_for_udid(udid));
        usb_capture->start(false);
        context->capture = std::move(usb_capture);
        std::scoped_lock lock(state_mutex);
        if (!initialized) {
            context->capture->stop();
            return fail(iPhoneMirror::Result::NotInitialized, L"Core closed while creating session");
        }
        const auto id = next_session_handle++;
        multi_sessions.emplace(id, std::move(context));
        *handle = id;
        iPhoneMirror::logging::write(std::format("multi_session create handle={} udid={}", id, narrow(udid)));
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::TransportUnavailable,
            L"Could not create device session: " + widen(error.what()));
    }
}

std::int32_t IM_CALL im_wireless_session_create(const wchar_t* device_id,
    const iPhoneMirror::CaptureOptions* options,
    iPhoneMirror::SessionHandle* handle) {
    if (!device_id || !*device_id || !handle || !options ||
        options->struct_size != sizeof(iPhoneMirror::CaptureOptions) ||
        !valid_video_preferences(options->requested_width, options->requested_height,
            options->target_fps) || !std::isfinite(options->audio_volume) ||
        options->audio_volume < 0.0F || options->audio_volume > 1.0F) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid wireless session options");
    }
    std::shared_ptr<iPhoneMirror::capture::WirelessReceiverHub> receiver;
    {
        std::scoped_lock lock(state_mutex);
        if (!initialized) return fail(iPhoneMirror::Result::NotInitialized, L"Core is not initialized");
        receiver = wireless_receiver;
    }
    if (!receiver || !receiver->running())
        return fail(iPhoneMirror::Result::TransportUnavailable,
            L"AirPlay receiver is not running");
    try {
        auto context = std::make_shared<MultiSessionContext>();
        context->preferences = {
            .render_max_width = options->requested_width,
            .render_max_height = options->requested_height,
            .target_fps = options->target_fps,
            .play_audio = options->play_audio != 0,
            .audio_volume = options->audio_volume,
        };
        auto wireless = std::make_unique<iPhoneMirror::capture::WirelessCaptureSession>(
            receiver, wireless_device_id(device_id), context->preferences);
        wireless->start();
        context->capture = std::move(wireless);
        std::scoped_lock lock(state_mutex);
        if (!initialized) {
            context->capture->stop();
            return fail(iPhoneMirror::Result::NotInitialized,
                L"Core closed while creating wireless session");
        }
        const auto id = next_session_handle++;
        multi_sessions.emplace(id, std::move(context));
        *handle = id;
        iPhoneMirror::logging::write(std::format(
            "wireless_session create handle={} device={}", id, narrow(device_id)));
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::TransportUnavailable,
            L"Could not create wireless session: " + widen(error.what()));
    }
}

static std::shared_ptr<MultiSessionContext> find_multi_session(iPhoneMirror::SessionHandle handle) {
    std::scoped_lock lock(state_mutex);
    const auto found = multi_sessions.find(handle);
    return found == multi_sessions.end() ? nullptr : found->second;
}

std::int32_t IM_CALL im_session_stop(iPhoneMirror::SessionHandle handle) {
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    std::vector<std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer>> renderers;
    {
        std::scoped_lock lock(context->mutex);
        for (auto& [_, renderer] : context->renderers) renderers.push_back(std::move(renderer));
        context->renderers.clear();
    }
    renderers.clear();
    if (context->capture) context->capture->stop();
    iPhoneMirror::logging::write(std::format("multi_session stop handle={}", handle));
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

void IM_CALL im_session_destroy(iPhoneMirror::SessionHandle handle) {
    std::shared_ptr<MultiSessionContext> context;
    {
        std::scoped_lock lock(state_mutex);
        const auto found = multi_sessions.find(handle);
        if (found == multi_sessions.end()) return;
        context = std::move(found->second);
        multi_sessions.erase(found);
    }
    std::vector<std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer>> renderers;
    {
        std::scoped_lock lock(context->mutex);
        for (auto& [_, renderer] : context->renderers) renderers.push_back(std::move(renderer));
        context->renderers.clear();
    }
    renderers.clear();
    if (context->capture) context->capture->stop();
    context->capture.reset();
    iPhoneMirror::logging::write(std::format("multi_session destroy handle={}", handle));
}

std::int32_t IM_CALL im_session_get_status(iPhoneMirror::SessionHandle handle,
    iPhoneMirror::CaptureStatus* status) {
    if (!status || status->struct_size != sizeof(iPhoneMirror::CaptureStatus))
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid CaptureStatus");
    auto context = find_multi_session(handle);
    if (!context || !context->capture)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    const auto snapshot = context->capture->snapshot();
    status->api_version = iPhoneMirror::ApiVersion;
    status->state = static_cast<iPhoneMirror::CaptureState>(snapshot.state);
    status->width = snapshot.width;
    status->height = snapshot.height;
    status->fps = snapshot.fps;
    status->latency_ms = snapshot.latency_ms;
    status->video_frames = snapshot.video_frames;
    status->audio_packets = snapshot.audio_packets;
    status->audio_sample_rate = snapshot.audio_sample_rate;
    status->audio_channels = snapshot.audio_channels;
    copy_text(status->message, snapshot.message);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_attach_preview(iPhoneMirror::SessionHandle handle, void* hwnd) {
    const auto window = static_cast<HWND>(hwnd);
    if (!window || !IsWindow(window))
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid preview HWND");
    auto context = find_multi_session(handle);
    if (!context || !context->capture)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    try {
        std::weak_ptr<MultiSessionContext> weak = context;
        auto renderer = std::make_unique<iPhoneMirror::renderer::D3D11PreviewRenderer>(window,
            [weak]() -> std::shared_ptr<const iPhoneMirror::media::DecodedFrame> {
                const auto locked = weak.lock();
                return locked && locked->capture ? locked->capture->latest_frame() : nullptr;
            });
        renderer->set_render_size_limit(context->preferences.render_max_width,
            context->preferences.render_max_height);
        renderer->set_max_fps(context->preferences.target_fps);
        renderer->set_corner_profile(context->corner_radius, context->corner_exponent);
        std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> previous;
        {
            std::scoped_lock lock(context->mutex);
            auto& slot = context->renderers[window];
            previous = std::move(slot);
            slot = std::move(renderer);
        }
        previous.reset();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::InternalError, L"Could not attach session preview: " + widen(error.what()));
    }
}

void IM_CALL im_session_detach_preview(iPhoneMirror::SessionHandle handle, void* hwnd) {
    auto context = find_multi_session(handle);
    if (!context) return;
    std::unique_ptr<iPhoneMirror::renderer::D3D11PreviewRenderer> renderer;
    {
        std::scoped_lock lock(context->mutex);
        const auto window = static_cast<HWND>(hwnd);
        const auto found = context->renderers.find(window);
        if (found == context->renderers.end()) return;
        renderer = std::move(found->second);
        context->renderers.erase(found);
    }
    renderer.reset();
}

std::int32_t IM_CALL im_session_set_video_preferences(iPhoneMirror::SessionHandle handle,
    std::uint32_t width, std::uint32_t height, std::uint32_t fps) {
    if (!valid_video_preferences(width, height, fps))
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid video preferences");
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    context->preferences.render_max_width = width;
    context->preferences.render_max_height = height;
    context->preferences.target_fps = fps;
    if (context->capture) context->capture->set_target_fps(fps);
    std::scoped_lock lock(context->mutex);
    for (auto& [_, renderer] : context->renderers) {
        renderer->set_render_size_limit(width, height);
        renderer->set_max_fps(fps);
    }
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_set_audio_enabled(iPhoneMirror::SessionHandle handle, std::int32_t enabled) {
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    context->preferences.play_audio = enabled != 0;
    if (context->capture) context->capture->set_audio_enabled(enabled != 0);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_set_audio_volume(iPhoneMirror::SessionHandle handle, float volume) {
    if (!std::isfinite(volume) || volume < 0.0F || volume > 1.0F)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid audio volume");
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    context->preferences.audio_volume = volume;
    if (context->capture) context->capture->set_audio_volume(volume);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_set_corner_profile(iPhoneMirror::SessionHandle handle,
    float radius, float exponent) {
    if (!std::isfinite(radius) || !std::isfinite(exponent) || radius < 0 || radius > 0.5F ||
        exponent < 1.5F || exponent > 8.0F)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid corner profile");
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    context->corner_radius = radius;
    context->corner_exponent = exponent;
    std::scoped_lock lock(context->mutex);
    for (auto& [_, renderer] : context->renderers)
        renderer->set_corner_profile(radius, exponent);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_get_latest_video_timestamp(iPhoneMirror::SessionHandle handle,
    std::int64_t* timestamp) {
    if (!timestamp) return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid timestamp pointer");
    auto context = find_multi_session(handle);
    if (!context || !context->capture)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    *timestamp = context->capture->latest_frame_timestamp();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_copy_latest_video_frame(iPhoneMirror::SessionHandle handle,
    iPhoneMirror::VideoFrameInfo* info, std::uint8_t* buffer, std::uint32_t* buffer_size,
    std::uint32_t max_width, std::uint32_t max_height) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::VideoFrameInfo) || !buffer_size ||
        ((max_width == 0) != (max_height == 0)))
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid video frame request");
    auto context = find_multi_session(handle);
    if (!context || !context->capture)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    const auto frame = context->capture->latest_frame();
    if (!frame || frame->width == 0 || frame->height == 0)
        return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"Waiting for first decoded frame");
    std::uint32_t width = frame->width;
    std::uint32_t height = frame->height;
    if (max_width != 0) {
        const auto scale = std::min(1.0, std::min(static_cast<double>(max_width) / width,
            static_cast<double>(max_height) / height));
        width = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::lround(width * scale)));
        height = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::lround(height * scale)));
    }
    const auto required64 = static_cast<std::uint64_t>(width) * height * 4U;
    if (required64 > std::numeric_limits<std::uint32_t>::max())
        return fail(iPhoneMirror::Result::InternalError, L"Frame is too large");
    const auto required = static_cast<std::uint32_t>(required64);
    info->api_version = iPhoneMirror::ApiVersion;
    info->width = width;
    info->height = height;
    info->stride = width * 4U;
    info->pixel_format = 1;
    info->timestamp_100ns = frame->timestamp_100ns;
    const auto capacity = *buffer_size;
    *buffer_size = required;
    if (!buffer || capacity < required)
        return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    const auto converted = max_width == 0 ? nv12_to_bgra(*frame, buffer)
        : nv12_to_bgra_scaled(*frame, buffer, width, height);
    return converted ? static_cast<std::int32_t>(iPhoneMirror::Result::Ok)
        : fail(iPhoneMirror::Result::ProtocolError, L"Invalid decoded NV12 frame");
}

std::int32_t IM_CALL im_session_force_preview_refresh(iPhoneMirror::SessionHandle handle) {
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    std::scoped_lock lock(context->mutex);
    if (context->renderers.empty())
        return fail(iPhoneMirror::Result::CaptureBackendUnavailable, L"Session preview is detached");
    for (auto& [_, renderer] : context->renderers) renderer->request_refresh();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_set_window_corner_profile(iPhoneMirror::SessionHandle handle,
    void* hwnd, float radius, float exponent) {
    if (!std::isfinite(radius) || !std::isfinite(exponent) || radius < 0 || radius > 0.5F ||
        exponent < 1.5F || exponent > 8.0F)
        return fail(iPhoneMirror::Result::InvalidArgument, L"Invalid window corner profile");
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    std::scoped_lock lock(context->mutex);
    const auto found = context->renderers.find(static_cast<HWND>(hwnd));
    if (found == context->renderers.end())
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown preview window");
    found->second->set_corner_profile(radius, exponent);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_session_set_window_rotation(iPhoneMirror::SessionHandle handle,
    void* hwnd, std::int32_t quarter_turns) {
    auto context = find_multi_session(handle);
    if (!context) return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown session handle");
    std::scoped_lock lock(context->mutex);
    const auto found = context->renderers.find(static_cast<HWND>(hwnd));
    if (found == context->renderers.end())
        return fail(iPhoneMirror::Result::InvalidArgument, L"Unknown preview window");
    const auto normalized = ((quarter_turns % 4) + 4) % 4;
    if (context->capture && (normalized & 1) != 0)
        context->capture->request_display_orientation(true);
    else if (context->capture && normalized == 0)
        context->capture->request_display_orientation(false);
    found->second->set_rotation(normalized == 2 ? 2 : 0);
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

const wchar_t* IM_CALL im_last_error() { return last_error.c_str(); }
