#include "Capture/CaptureSession.h"

#include "Media/MediaFoundationDecoder.h"
#include "Audio/WasapiRenderer.h"
#include "Logging.h"
#include "Protocol/QuickTimePacket.h"
#include "Transport/LibUsb0Transport.h"
#include "Transport/QtUsbTransport.h"

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <exception>
#include <format>
#include <limits>
#include <memory>
#include <thread>
#include <optional>
#include <utility>
#include <vector>

namespace iPhoneMirror::capture {
namespace {

struct NativeDisplaySize { std::uint32_t width; std::uint32_t height; };

NativeDisplaySize native_display_size(std::wstring_view product_type) noexcept {
    // ProductType-to-panel-pixel mapping. Identifiers sharing a panel are
    // grouped deliberately; HPD1 is sensitive to the exact portrait aspect.
    // Keep unknown/new hardware on the highest empirically safe tier.
    static constexpr std::pair<std::wstring_view, NativeDisplaySize> sizes[] = {
        {L"iPhone13,1", {1080, 2340}}, // iPhone 12 mini
        {L"iPhone14,4", {1080, 2340}}, // iPhone 13 mini
        {L"iPhone18,3", {1206, 2622}}, // iPhone 17 test hardware
        {L"iPad13,16", {1640, 2360}},  // iPad Air (5th generation)
    };
    for (const auto& [identifier, size] : sizes)
        if (identifier == product_type) return size;
    // A phone-shaped fallback makes unknown iPads negotiate an extreme aspect
    // ratio. 1640x2360 is the conservative 4:3-class iPad capability used by
    // current base/Air models; the stream's vdim remains authoritative.
    if (product_type.starts_with(L"iPad")) return {1640, 2360};
    return {1206, 2622};
}

bool same_video_decoder_configuration(const coremedia::FormatDescription& left,
    const coremedia::FormatDescription& right) noexcept {
    const auto& left_color = left.color;
    const auto& right_color = right.color;
    return left.width == right.width && left.height == right.height &&
        left.video_codec() == right.video_codec() &&
        left.nalu_length_size == right.nalu_length_size &&
        left.chroma_format == right.chroma_format &&
        left.bit_depth_luma == right.bit_depth_luma &&
        left.bit_depth_chroma == right.bit_depth_chroma &&
        left.decoder_configuration_record == right.decoder_configuration_record &&
        left.video_parameter_sets == right.video_parameter_sets &&
        left.sequence_parameter_sets == right.sequence_parameter_sets &&
        left.picture_parameter_sets == right.picture_parameter_sets &&
        left_color.primaries == right_color.primaries &&
        left_color.transfer == right_color.transfer &&
        left_color.matrix == right_color.matrix &&
        left_color.range == right_color.range &&
        left_color.hdr.max_content_light_level ==
            right_color.hdr.max_content_light_level &&
        left_color.hdr.max_frame_average_light_level ==
            right_color.hdr.max_frame_average_light_level &&
        left_color.hdr.max_mastering_luminance ==
            right_color.hdr.max_mastering_luminance &&
        left_color.hdr.min_mastering_luminance ==
            right_color.hdr.min_mastering_luminance;
}

DecoderRuntimeMode decoder_runtime_mode(media::DecoderAcceleration acceleration) noexcept {
    switch (acceleration) {
    case media::DecoderAcceleration::Hardware:
        return DecoderRuntimeMode::Hardware;
    case media::DecoderAcceleration::Software:
        return DecoderRuntimeMode::Software;
    default:
        return DecoderRuntimeMode::Unknown;
    }
}

std::wstring widen(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (length <= 0) return L"未知错误";
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), result.data(), length);
    return result;
}

std::optional<transport::AppleUsbDevice> find_device(
    transport::QtUsbContext& context, const transport::AppleUsbIdentity& identity,
    bool require_quicktime = false) {
    auto devices = context.enumerate();
    const auto selection = transport::select_apple_usb_device(devices, identity,
        require_quicktime);
    if (!selection.index) return std::nullopt;
    return std::move(devices[*selection.index]);
}

std::optional<transport::AppleUsbDevice> find_device(
    transport::QtUsbContext& context, const std::string& serial) {
    return find_device(context, transport::AppleUsbIdentity{.serial = serial});
}

class CaptureConnection {
public:
    virtual ~CaptureConnection() = default;
    virtual std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms) = 0;
    virtual void write(std::span<const std::uint8_t> source, unsigned timeout_ms) = 0;
    virtual void clear_halt() = 0;
    virtual void recover_handshake() = 0;
    virtual void disable_quicktime_configuration() = 0;
    virtual void close() noexcept = 0;
};

template <typename Connection>
class CaptureConnectionAdapter final : public CaptureConnection {
public:
    explicit CaptureConnectionAdapter(Connection connection) : connection_(std::move(connection)) {}
    ~CaptureConnectionAdapter() override {
        // The capture worker owns the protocol shutdown sequence.  It sends
        // HPA0/HPD0, drains RELS, and disables configuration exactly once in
        // shutdown_usb().  Repeating the 0x52/0 request from this destructor
        // races device re-enumeration and differs from the working Aisi
        // client, which performs a single disable step.
        connection_.close();
    }
    std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms) override {
        return connection_.read(destination, timeout_ms);
    }
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms) override {
        connection_.write(source, timeout_ms);
    }
    void clear_halt() override { connection_.clear_halt(); }
    void recover_handshake() override { connection_.recover_handshake(); }
    void disable_quicktime_configuration() override { connection_.disable_quicktime_configuration(); }
    void close() noexcept override { connection_.close(); }
private:
    Connection connection_;
};

void restore_libusb0_configuration(
    const transport::AppleUsbIdentity& identity) noexcept {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        try {
            (void)transport::LibUsb0Connection::disable_quicktime_configuration(identity);
            return;
        } catch (...) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    } while (std::chrono::steady_clock::now() < deadline);
}

void restore_qt_configuration(bool use_usbdk,
    const transport::AppleUsbIdentity& identity) noexcept {
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::seconds(5);
    do {
        try {
            transport::QtUsbContext context(use_usbdk);
            (void)transport::QtUsbConnection::disable_quicktime_configuration(
                context, identity);
            return;
        } catch (...) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    } while (std::chrono::steady_clock::now() < deadline);
}

std::uint8_t luma_as_8bit(const media::DecodedFrame& frame,
    const std::uint8_t* row, std::uint32_t x) noexcept {
    if (frame.pixel_format == media::PixelFormat::P010) {
        // P010 stores its 10 significant bits in the high bits of each
        // little-endian 16-bit component. The high byte is the correctly
        // scaled 8-bit value needed by these coarse orientation heuristics.
        return row[static_cast<std::size_t>(x) * 2U + 1U];
    }
    return row[x];
}

std::optional<bool> padded_content_orientation(const media::DecodedFrame& frame) {
    if (frame.width < 64 || frame.height < 64 || frame.nv12.empty()) return std::nullopt;
    const auto stride = static_cast<std::size_t>(std::abs(frame.stride));
    const auto row_bytes = static_cast<std::size_t>(frame.width) *
        (frame.pixel_format == media::PixelFormat::P010 ? 2U : 1U);
    if (stride < row_bytes || frame.nv12.size() < stride * frame.height) return std::nullopt;
    std::uint32_t min_x = frame.width, min_y = frame.height, max_x{}, max_y{};
    std::uint64_t active{};
    constexpr std::uint32_t step = 8;
    for (std::uint32_t y = 0; y < frame.height; y += step) {
        const auto* row = frame.nv12.data() + static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < frame.width; x += step) {
            if (luma_as_8bit(frame, row, x) <= 28) continue;
            min_x = std::min(min_x, x); max_x = std::max(max_x, x);
            min_y = std::min(min_y, y); max_y = std::max(max_y, y);
            ++active;
        }
    }
    if (active < 128 || min_x > max_x || min_y > max_y) return std::nullopt;
    const auto content_width = max_x - min_x + step;
    const auto content_height = max_y - min_y + step;
    const double content_aspect = static_cast<double>(content_width) /
        static_cast<double>(std::max<std::uint32_t>(1, content_height));
    // Letterboxed square/near-square media is not evidence that the physical
    // device rotated. Require a clear landscape/portrait bias. 4:3 remains a
    // valid landscape shape, while 1:1 social video stays in portrait.
    constexpr double OrientationAspectThreshold = 1.20;
    if (frame.height > frame.width &&
        content_width > frame.width * 3U / 4U && content_height < frame.height * 2U / 3U &&
        content_aspect >= OrientationAspectThreshold)
        return true;
    if (frame.width > frame.height &&
        content_height > frame.height * 3U / 4U && content_width < frame.width * 2U / 3U &&
        content_aspect <= 1.0 / OrientationAspectThreshold)
        return false;
    return std::nullopt;
}

bool frame_is_nearly_black(const media::DecodedFrame& frame) noexcept {
    if (frame.width < 32 || frame.height < 32 || frame.nv12.empty()) return false;
    const auto stride = static_cast<std::size_t>(std::abs(frame.stride));
    const auto row_bytes = static_cast<std::size_t>(frame.width) *
        (frame.pixel_format == media::PixelFormat::P010 ? 2U : 1U);
    if (stride < row_bytes || frame.nv12.size() < stride * frame.height) return false;
    std::uint64_t samples{}, dark{};
    constexpr std::uint32_t step = 16;
    for (std::uint32_t y = 0; y < frame.height; y += step) {
        const auto* row = frame.nv12.data() + static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < frame.width; x += step) {
            ++samples;
            if (luma_as_8bit(frame, row, x) <= 24) ++dark;
        }
    }
    return samples >= 128 && dark * 100U >= samples * 98U;
}

bool sample_contains_keyframe(const coremedia::SampleBuffer& sample,
    const std::optional<coremedia::FormatDescription>& format) noexcept {
    if (!format || !format->is_video()) return false;
    try {
        const bool has_per_sample_sizes = sample.sample_count > 1 &&
            sample.sample_sizes.size() == sample.sample_count;
        const auto sample_total = has_per_sample_sizes ? sample.sample_count : 1U;
        std::size_t offset{};
        for (std::uint32_t index{}; index < sample_total; ++index) {
            const auto size = has_per_sample_sizes
                ? static_cast<std::size_t>(sample.sample_sizes[index])
                : sample.sample_data.size();
            if (offset > sample.sample_data.size() ||
                size > sample.sample_data.size() - offset) return false;
            if (media::detail::is_random_access_sample(*format,
                    std::span(sample.sample_data).subspan(offset, size))) return true;
            offset += size;
        }
    } catch (...) {}
    return false;
}

} // namespace

namespace detail {

void VideoWorkerFailure::capture_current() noexcept {
    const auto current = std::current_exception();
    try {
        std::scoped_lock lock(mutex_);
        if (!error_) error_ = current;
    } catch (...) {}
    failed_.store(true, std::memory_order_release);
}

bool VideoWorkerFailure::failed() const noexcept {
    return failed_.load(std::memory_order_acquire);
}

void VideoWorkerFailure::rethrow_if_set() const {
    if (!failed()) return;
    std::exception_ptr error;
    {
        std::scoped_lock lock(mutex_);
        error = error_;
    }
    if (error) {
        try {
            std::rethrow_exception(error);
        } catch (const std::exception&) {
            throw;
        } catch (...) {
            throw std::runtime_error(
                "video decoder worker failed with a non-standard exception");
        }
    }
    throw std::runtime_error("video decoder worker failed");
}

bool VideoQueueBudget::has_capacity(std::size_t pending_samples,
    std::size_t pending_bytes, std::size_t incoming_bytes) const noexcept {
    return !awaiting_keyframe_ && pending_samples < MaxPendingSamples &&
        incoming_bytes <= MaxPendingBytes &&
        pending_bytes <= MaxPendingBytes - incoming_bytes;
}

VideoQueueAdmission VideoQueueBudget::admit(std::size_t pending_samples,
    std::size_t pending_bytes, std::size_t incoming_bytes,
    bool keyframe) noexcept {
    if (has_capacity(pending_samples, pending_bytes, incoming_bytes)) {
        return {
            .action = VideoQueueAction::Enqueue,
            .dropped_samples = dropped_samples_,
            .dropped_bytes = dropped_bytes_,
        };
    }

    const auto add_dropped = [this](std::size_t samples, std::size_t bytes) noexcept {
        constexpr auto maximum = std::numeric_limits<std::uint64_t>::max();
        dropped_samples_ = samples > maximum - dropped_samples_
            ? maximum : dropped_samples_ + samples;
        dropped_bytes_ = bytes > maximum - dropped_bytes_
            ? maximum : dropped_bytes_ + bytes;
    };
    const bool recoverable_keyframe = keyframe && incoming_bytes <= MaxPendingBytes;
    if (awaiting_keyframe_) {
        if (recoverable_keyframe) {
            awaiting_keyframe_ = false;
            return {
                .action = VideoQueueAction::ReplaceWithKeyframe,
                .dropped_samples = dropped_samples_,
                .dropped_bytes = dropped_bytes_,
            };
        }
        add_dropped(1, incoming_bytes);
        return {
            .action = VideoQueueAction::DropIncoming,
            .dropped_samples = dropped_samples_,
            .dropped_bytes = dropped_bytes_,
        };
    }

    add_dropped(pending_samples, pending_bytes);
    if (recoverable_keyframe) {
        return {
            .action = VideoQueueAction::ReplaceWithKeyframe,
            .dropped_samples = dropped_samples_,
            .dropped_bytes = dropped_bytes_,
        };
    }

    add_dropped(1, incoming_bytes);
    awaiting_keyframe_ = true;
    return {
        .action = VideoQueueAction::ClearAndDrop,
        .dropped_samples = dropped_samples_,
        .dropped_bytes = dropped_bytes_,
        .entered_recovery = true,
    };
}

bool VideoQueueBudget::awaiting_keyframe() const noexcept {
    return awaiting_keyframe_;
}

} // namespace detail

UsbDisplayConfiguration make_usb_display_configuration(UsbProjectionMode mode,
    std::uint32_t native_width, std::uint32_t native_height,
    std::uint32_t requested_width, std::uint32_t requested_height) noexcept {
    UsbDisplayConfiguration configuration;
    auto& options = configuration.session_options;
    switch (mode) {
    case UsbProjectionMode::Demo:
        options.demo_mode = true;
        options.requested_width = native_width;
        options.requested_height = native_height;
        break;
    case UsbProjectionMode::AirPlay:
        options.demo_mode = false;
        options.requested_width = requested_width != 0 ? requested_width : native_width;
        options.requested_height = requested_height != 0 ? requested_height : native_height;
        configuration.adaptive_reconfiguration = true;
        break;
    case UsbProjectionMode::Aisi:
        options.demo_mode = false;
        options.requested_width = 1565;
        options.requested_height = 1565;
        break;
    }
    return configuration;
}

CaptureSession::CaptureSession(std::string serial, bool play_audio)
    : CaptureSession(std::move(serial), CapturePreferences{.play_audio = play_audio}) {}

CaptureSession::CaptureSession(std::string serial, CapturePreferences preferences,
    std::wstring product_type)
    : serial_(std::move(serial)), preferences_(preferences), product_type_(std::move(product_type)),
      target_fps_(preferences.target_fps),
      play_audio_(preferences.play_audio),
      audio_volume_(std::clamp(preferences.audio_volume, 0.0F, 1.0F)),
      decoder_switch_(preferences.decoder_preference) {}
CaptureSession::~CaptureSession() { stop(); }

void CaptureSession::start(bool use_usbdk) {
    if (worker_.joinable()) throw std::runtime_error("capture session is already running");
    // Synchronous preflight keeps the GUI from reporting a false successful start.
    std::string failure = "libusb cannot see the selected iPhone; USB backend/driver is not ready";
    bool ready{};
    if (transport::libusb0_available()) {
        try {
            const auto device = transport::find_libusb0_device(serial_);
            if (device && device->can_open) {
                usb_backend_ = UsbBackend::LibUsb0;
                ready = true;
            }
        } catch (const std::exception& error) {
            failure = error.what();
        }
    }
    for (const bool candidate : {use_usbdk, !use_usbdk}) {
        if (ready) break;
        try {
            transport::QtUsbContext context(candidate);
            const auto device = find_device(context, serial_);
            if (!device) continue;
            if (!device->can_open) {
                failure = "libusb sees the iPhone but cannot open it; check the USB filter backend";
                continue;
            }
            usb_backend_ = candidate ? UsbBackend::UsbDk : UsbBackend::LibUsb1;
            ready = true;
            break;
        } catch (const std::exception& error) {
            failure = error.what();
        }
    }
    if (!ready) throw std::runtime_error(failure);
    set_state(State::ActivatingUsb, L"正在激活 QuickTime USB 配置");
    worker_ = std::jthread([this](std::stop_token token) { run(token); });
}

void CaptureSession::stop() noexcept {
    if (worker_.joinable()) {
        set_state(State::Stopping, L"正在停止投屏");
        worker_.request_stop();
        worker_.join();
        // A normal stop may race with the bulk read timeout/close path. Keep
        // the terminal state stable for the GUI unless the worker reported a
        // genuine capture error.
        if (snapshot().state != State::Error) set_state(State::Stopped, L"投屏已停止");
    }
    // Decoded frames are immutable but device-specific. Do not let the native
    // preview or screenshot path expose the previous iPhone after a stop and
    // subsequent selection change.
    {
        std::scoped_lock lock(mutex_);
        render_queue_.clear();
        render_queue_bytes_ = 0;
        latest_frame_.reset();
    }
}

void CaptureSession::set_audio_enabled(bool enabled) noexcept {
    play_audio_.store(enabled, std::memory_order_relaxed);
    std::scoped_lock lock(audio_mutex_);
    if (audio_renderer_) audio_renderer_->set_enabled(enabled);
    logging::write(std::format("audio playback_enabled={}", enabled));
}

void CaptureSession::set_audio_volume(float volume) noexcept {
    if (!std::isfinite(volume)) return;
    const auto clamped = std::clamp(volume, 0.0F, 1.0F);
    audio_volume_.store(clamped, std::memory_order_relaxed);
    std::scoped_lock lock(audio_mutex_);
    if (audio_renderer_) audio_renderer_->set_volume(clamped);
    logging::write(std::format("audio volume={:.3f}", clamped));
}

void CaptureSession::set_target_fps(std::uint32_t target_fps) noexcept {
    target_fps_.store(target_fps, std::memory_order_relaxed);
    logging::write(std::format("video target_fps={}", target_fps));
}

std::uint32_t CaptureSession::target_fps() const noexcept {
    return target_fps_.load(std::memory_order_relaxed);
}

void CaptureSession::set_decoder_preference(media::DecoderPreference preference) noexcept {
    const auto update = decoder_switch_.request(preference);
    if (!update.changed) return;
    logging::write(std::format(
        "video_worker decoder_switch requested from={} to={} generation={}",
        media::decoder_preference_name(update.previous.preference),
        media::decoder_preference_name(update.current.preference),
        update.current.generation));
}

DecoderSwitchStatus CaptureSession::decoder_switch_status() const noexcept {
    return decoder_switch_.status();
}

void CaptureSession::request_display_orientation(bool landscape) noexcept {
    if (preferences_.usb_projection_mode != UsbProjectionMode::AirPlay) return;
    requested_display_orientation_.store(landscape ? 2 : 1, std::memory_order_release);
}

void CaptureSession::stop_audio_renderer() noexcept {
    std::unique_ptr<audio::WasapiRenderer> renderer;
    {
        std::scoped_lock lock(audio_mutex_);
        renderer = std::move(audio_renderer_);
        audio_output_queue_.clear();
    }
    renderer.reset();
}

Snapshot CaptureSession::snapshot() const {
    std::scoped_lock lock(mutex_);
    return snapshot_;
}

std::int64_t CaptureSession::latest_frame_timestamp() const {
    std::scoped_lock lock(mutex_);
    return latest_frame_ ? latest_frame_->timestamp_100ns : 0;
}

std::shared_ptr<const media::DecodedFrame> CaptureSession::latest_frame() const {
    std::scoped_lock lock(mutex_);
    return latest_frame_;
}

std::shared_ptr<const media::DecodedFrame> CaptureSession::next_render_frame() {
    std::size_t dropped{};
    std::size_t depth{};
    std::uint64_t selected{};
    std::uint64_t stale_total{};
    std::shared_ptr<const media::DecodedFrame> frame;
    double pipeline_ms{};
    {
        std::scoped_lock lock(mutex_);
        if (render_queue_.empty()) return nullptr;

        depth = render_queue_.size();
        // Encoded H.264 input must remain FIFO because pictures reference one
        // another. Decoded pictures do not have that restriction. If the MFT
        // releases a burst, or the window stalls briefly, presenting every
        // stale output makes the preview permanently trail the phone. Keep a
        // tiny two-frame jitter allowance, then jump to the newest complete
        // picture (mailbox semantics), exactly where dropping is safe.
        if (depth > 2) {
            dropped = depth - 1;
            frame = std::move(render_queue_.back());
            render_queue_.clear();
            render_queue_bytes_ = 0;
            stale_render_frames_ += dropped;
        } else {
            frame = std::move(render_queue_.front());
            render_queue_.pop_front();
            render_queue_bytes_ -= frame->nv12.size();
        }
        selected = ++selected_render_frames_;
        stale_total = stale_render_frames_;
        if (frame && frame->received_at.time_since_epoch().count() != 0) {
            pipeline_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - frame->received_at).count();
            snapshot_.latency_ms = std::max(0.0, pipeline_ms);
        }
    }
    // A deliberate 24/30 fps presentation cap drops decoded source frames on
    // nearly every selection. Sample that expected mailbox activity instead
    // of turning the real-time log itself into a capture-thread workload.
    if (selected <= 3 || selected % 300 == 0 ||
        (dropped != 0 && selected % 60 == 0)) {
        logging::write(std::format(
            "render_select n={} depth={} dropped={} stale_total={} pipeline_ms={:.3f}",
            selected, depth, dropped, stale_total, pipeline_ms));
    }
    return frame;
}

std::shared_ptr<const AudioPacket> CaptureSession::next_audio_packet(
    std::uint64_t after_sequence) const {
    std::scoped_lock lock(audio_mutex_);
    const auto found = std::find_if(audio_output_queue_.begin(),
        audio_output_queue_.end(), [after_sequence](const auto& packet) {
            return packet && packet->sequence > after_sequence;
        });
    return found == audio_output_queue_.end() ? nullptr : *found;
}

void CaptureSession::set_state(State state, std::wstring message) {
    std::scoped_lock lock(mutex_);
    snapshot_.state = state;
    snapshot_.message = std::move(message);
}

void CaptureSession::run(std::stop_token stop_token) noexcept {
    const auto native = native_display_size(product_type_);
    native_portrait_size_.store(
        detail::pack_video_dimensions(native.width, native.height),
        std::memory_order_release);
    std::string product_type_ascii;
    product_type_ascii.reserve(product_type_.size());
    for (const auto ch : product_type_)
        product_type_ascii.push_back(ch <= 0x7f ? static_cast<char>(ch) : '?');
    const auto device_fp = logging::fingerprint(serial_);
    logging::write(std::format(
        "capture_run begin device_fp={} backend={} product_type={} usb_display_size={}x{} target_fps={} audio={} volume={:.3f} decoder_policy={} color_policy={}", device_fp,
        usb_backend_ == UsbBackend::LibUsb0 ? "libusb0" :
        usb_backend_ == UsbBackend::UsbDk ? "usbdk" : "libusb1",
        product_type_ascii,
        native.width, native.height,
        target_fps(),
        play_audio_.load(std::memory_order_relaxed),
        audio_volume_.load(std::memory_order_relaxed),
        media::decoder_preference_name(decoder_switch_.requested().preference),
        static_cast<unsigned>(preferences_.color_output_preference)));
    std::unique_ptr<CaptureConnection> usb;
    quicktime::StreamDecoder decoder;
    const auto display_configuration = make_usb_display_configuration(
        preferences_.usb_projection_mode, native.width, native.height,
        preferences_.usb_requested_width, preferences_.usb_requested_height);
    auto session_options = display_configuration.session_options;
    const bool adaptive_display = display_configuration.adaptive_reconfiguration;
    // Always negotiate the audio stream. The playback toggle is deliberately
    // local so it can be switched on again without restarting USB/QuickTime.
    session_options.request_audio = true;
    if (preferences_.usb_projection_mode == UsbProjectionMode::AirPlay &&
        preferences_.usb_requested_width != 0 && preferences_.usb_requested_height != 0) {
        logging::write(std::format("advanced_usb_request={}x{}",
            preferences_.usb_requested_width, preferences_.usb_requested_height));
    }
    const char* projection_mode = preferences_.usb_projection_mode == UsbProjectionMode::Demo
        ? "demo" : preferences_.usb_projection_mode == UsbProjectionMode::AirPlay
        ? "airplay" : "aisi";
    logging::write(std::format(
        "usb_projection mode={} valeria={} native_size={} display_size={}x{} adaptive={}",
        projection_mode, session_options.demo_mode,
        session_options.request_native_display_size,
        session_options.requested_width, session_options.requested_height,
        adaptive_display));
    quicktime::SessionProtocol protocol(session_options);
    bool audio_initialization_disabled{};
    std::vector<std::uint8_t> read_buffer(1024U * 1024U);
    bool shutdown_done{};
    detail::VideoWorkerFailure video_worker_failure;

    // Once the QuickTime endpoint is open, every exit path must send the same
    // HPA0/HPD0 shutdown controls used by the working macOS/Aisi clients.
    // This also covers a session that never returned its initial PING.
    const auto shutdown_usb = [&]() noexcept {
        if (!usb || shutdown_done) return;
        shutdown_done = true;
        const bool handshake_started = protocol.state() != quicktime::SessionState::WaitingForPing;
        const auto stop_messages = protocol.stop_messages();
        logging::write(std::format("shutdown_usb handshake_started={} stop_messages={}",
            handshake_started, stop_messages.size()));
        try {
            for (const auto& message : stop_messages) {
                try { usb->write(message, 500); } catch (...) {}
            }

            std::size_t release_count{};
            const auto release_deadline = std::chrono::steady_clock::now() +
                (handshake_started ? std::chrono::seconds(6) : std::chrono::seconds(1));
            while (release_count < 2 && std::chrono::steady_clock::now() < release_deadline) {
                try {
                    const auto count = usb->read(read_buffer, 250);
                    if (count == 0) continue;
                    for (const auto& packet : decoder.push(std::span(read_buffer).first(count))) {
                        // Reply to SYNC STOP before accepting RELS. Dropping
                        // this RPLY leaves the device clock session open.
                        try {
                            const auto event = protocol.process(packet);
                            for (const auto& response : event.outbound) {
                                try { usb->write(response, 500); } catch (...) {}
                            }
                        } catch (...) {}
                        if (packet.kind == quicktime::PacketKind::Async &&
                            packet.subtype == quicktime::fourcc('r', 'e', 'l', 's')) {
                            ++release_count;
                        }
                    }
                } catch (...) {
                    break;
                }
            }
        } catch (...) {}
        try { usb->disable_quicktime_configuration(); } catch (...) {}
        usb->close();
    };

    try {
        std::unique_ptr<transport::QtUsbContext> qt_context;
        bool quicktime_open_recovered{};
        if (usb_backend_ == UsbBackend::LibUsb0) {
            auto device = transport::find_libusb0_device(serial_);
            if (!device)
                throw std::runtime_error("Apple device disconnected before capture started");
            auto identity = transport::make_apple_usb_identity(*device);
            logging::write(std::format(
                "usb_identity device_fp={} pid={:04x} configs={}/{} expected_qt_config={} topology={}",
                device_fp, device->product_id, device->configuration_count,
                device->highest_configuration_value,
                identity.expected_quicktime_configuration,
                !identity.topology_id.empty()));
            if (!device->quicktime_configuration) {
                const bool activation_acknowledged =
                    transport::LibUsb0Connection::enable_quicktime_configuration(identity);
                logging::write(std::format(
                    "usb_activation requested device_fp={} acknowledged={} expected_qt_config={}",
                    device_fp, activation_acknowledged,
                    identity.expected_quicktime_configuration));
                set_state(State::WaitingForDevice,
                    L"等待 Apple 设备以 QuickTime 配置重新连接");
                const auto activation_started = std::chrono::steady_clock::now();
                const auto deadline = activation_started + std::chrono::seconds(20);
                std::string last_usb_diagnostic;
                std::string last_usb_open_error;
                do {
                    if (stop_token.stop_requested()) {
                        restore_libusb0_configuration(identity);
                        set_state(State::Stopped, L"投屏已取消");
                        return;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    const auto candidates = transport::enumerate_libusb0();
                    const auto diagnostic = transport::describe_apple_usb_candidates(
                        candidates, identity);
                    if (diagnostic != last_usb_diagnostic) {
                        logging::write(std::format(
                            "usb_reenumeration device_fp={} backend=libusb0 {}",
                            device_fp, diagnostic));
                        last_usb_diagnostic = diagnostic;
                    }
                    device = transport::find_libusb0_device(identity, true);
                    if (device && device->quicktime_configuration) break;
                    if (std::chrono::steady_clock::now() - activation_started >=
                        std::chrono::milliseconds(1500)) {
                        try {
                            usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                                transport::LibUsb0Connection::open_quicktime(identity, true));
                            logging::write(std::format(
                                "usb_reenumeration ready device_fp={} backend=libusb0 fallback=conventional expected_qt_config={}",
                                device_fp, identity.expected_quicktime_configuration));
                            break;
                        } catch (const std::exception& error) {
                            if (last_usb_open_error != error.what()) {
                                last_usb_open_error = error.what();
                                logging::write(std::format(
                                    "usb_reenumeration pending device_fp={} backend=libusb0 error={}",
                                    device_fp, last_usb_open_error));
                            }
                        }
                    }
                } while (std::chrono::steady_clock::now() < deadline);
                if (!usb && (!device || !device->quicktime_configuration)) {
                    logging::write(std::format(
                        "usb_reenumeration descriptor_timeout device_fp={} expected_qt_config={} fallback=conventional",
                        device_fp, identity.expected_quicktime_configuration));
                }
                // Aisi waits roughly one second after discovering the new
                // device node before set-configuration/claim. Give Windows
                // and iOS the same settle window after re-enumeration.
                if (!usb) std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            if (!usb) {
                try {
                    usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                        transport::LibUsb0Connection::open_quicktime(identity,
                            !device || !device->quicktime_configuration));
                } catch (const std::exception& first_error) {
                    // An appended QuickTime configuration can survive a
                    // crashed owner while its interface claim does not.
                    // Recover this device in-place instead of requiring the
                    // entire GUI process to restart.
                    logging::write(std::format(
                        "quicktime_open recovery begin device_fp={} first_error={}",
                        device_fp, first_error.what()));
                    set_state(State::ActivatingUsb,
                        L"正在恢复 Apple 设备的 QuickTime USB 配置");
                    restore_libusb0_configuration(identity);

                    std::optional<transport::AppleUsbDevice> normal_device;
                    const auto restore_deadline = std::chrono::steady_clock::now() +
                        std::chrono::seconds(10);
                    do {
                        if (stop_token.stop_requested()) {
                            set_state(State::Stopped, L"投屏已取消");
                            return;
                        }
                        std::this_thread::sleep_for(std::chrono::milliseconds(250));
                        auto candidate = transport::find_libusb0_device(identity);
                        if (candidate && !candidate->quicktime_configuration) {
                            normal_device = std::move(candidate);
                            break;
                        }
                    } while (std::chrono::steady_clock::now() < restore_deadline);
                    if (!normal_device) {
                        throw std::runtime_error(std::string(first_error.what()) +
                            "; recovery did not observe the selected Apple device's normal USB configuration");
                    }

                    identity = transport::make_apple_usb_identity(*normal_device);
                    (void)transport::LibUsb0Connection::enable_quicktime_configuration(identity);
                    const auto retry_deadline = std::chrono::steady_clock::now() +
                        std::chrono::seconds(20);
                    do {
                        if (stop_token.stop_requested()) {
                            restore_libusb0_configuration(identity);
                            set_state(State::Stopped, L"投屏已取消");
                            return;
                        }
                        std::this_thread::sleep_for(std::chrono::milliseconds(250));
                        device = transport::find_libusb0_device(identity, true);
                        if (device && device->quicktime_configuration) break;
                    } while (std::chrono::steady_clock::now() < retry_deadline);
                    if (!device || !device->quicktime_configuration) {
                        logging::write(std::format(
                            "quicktime_open recovery descriptor_timeout device_fp={} expected_qt_config={} fallback=conventional",
                            device_fp, identity.expected_quicktime_configuration));
                    } else {
                        std::this_thread::sleep_for(std::chrono::seconds(1));
                    }
                    usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                        transport::LibUsb0Connection::open_quicktime(identity,
                            !device || !device->quicktime_configuration));
                    quicktime_open_recovered = true;
                    logging::write(std::format(
                        "quicktime_open recovery success device_fp={}", device_fp));
                }
            }
        } else {
            const bool use_usbdk = usb_backend_ == UsbBackend::UsbDk;
            qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
            auto device = find_device(*qt_context, serial_);
            if (!device)
                throw std::runtime_error("Apple device disconnected before capture started");
            const auto identity = transport::make_apple_usb_identity(*device);
            if (!device->quicktime_configuration) {
                const bool activation_acknowledged =
                    transport::QtUsbConnection::enable_quicktime_configuration(
                        *qt_context, identity);
                logging::write(std::format(
                    "usb_activation requested device_fp={} acknowledged={} expected_qt_config={}",
                    device_fp, activation_acknowledged,
                    identity.expected_quicktime_configuration));
                qt_context.reset();
                set_state(State::WaitingForDevice,
                    L"等待 Apple 设备以 QuickTime 配置重新连接");
                const auto activation_started = std::chrono::steady_clock::now();
                const auto deadline = activation_started + std::chrono::seconds(20);
                std::string last_usb_diagnostic;
                std::string last_usb_open_error;
                do {
                    if (stop_token.stop_requested()) {
                        qt_context.reset();
                        restore_qt_configuration(use_usbdk, identity);
                        set_state(State::Stopped, L"投屏已取消");
                        return;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    try {
                        qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
                        const auto candidates = qt_context->enumerate();
                        const auto diagnostic = transport::describe_apple_usb_candidates(
                            candidates, identity);
                        if (diagnostic != last_usb_diagnostic) {
                            logging::write(std::format(
                                "usb_reenumeration device_fp={} backend={} {}",
                                device_fp, use_usbdk ? "usbdk" : "libusb1",
                                diagnostic));
                            last_usb_diagnostic = diagnostic;
                        }
                        device = find_device(*qt_context, identity, true);
                        if (device && device->quicktime_configuration) break;
                        if (std::chrono::steady_clock::now() - activation_started >=
                            std::chrono::milliseconds(1500)) {
                            auto connection = transport::QtUsbConnection::open_quicktime(
                                *qt_context, identity, true);
                            usb = std::make_unique<CaptureConnectionAdapter<transport::QtUsbConnection>>(
                                std::move(connection));
                            logging::write(std::format(
                                "usb_reenumeration ready device_fp={} backend={} fallback=conventional expected_qt_config={}",
                                device_fp, use_usbdk ? "usbdk" : "libusb1",
                                identity.expected_quicktime_configuration));
                            break;
                        }
                    } catch (const std::exception& error) {
                        qt_context.reset();
                        if (last_usb_open_error != error.what()) {
                            last_usb_open_error = error.what();
                            logging::write(std::format(
                                "usb_reenumeration pending device_fp={} backend={} error={}",
                                device_fp, use_usbdk ? "usbdk" : "libusb1",
                                last_usb_open_error));
                        }
                    }
                } while (std::chrono::steady_clock::now() < deadline);
                if (!usb && (!device || !device->quicktime_configuration)) {
                    logging::write(std::format(
                        "usb_reenumeration descriptor_timeout device_fp={} backend={} expected_qt_config={} fallback=conventional",
                        device_fp, use_usbdk ? "usbdk" : "libusb1",
                        identity.expected_quicktime_configuration));
                }
                if (!usb) std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            if (!usb) {
                if (!qt_context)
                    qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
                usb = std::make_unique<CaptureConnectionAdapter<transport::QtUsbConnection>>(
                    transport::QtUsbConnection::open_quicktime(*qt_context, identity,
                        !device || !device->quicktime_configuration));
            }
        }
        // libusb1/UsbDk needs an explicit halt clear. The libusb0 filter
        // backend historically succeeded without this extra control transfer
        // and starts its bulk read immediately after claiming the discovered
        // QuickTime interface.
        if (usb_backend_ != UsbBackend::LibUsb0) {
            try { usb->clear_halt(); } catch (...) {}
        }
        set_state(State::Handshaking, L"已连接 QuickTime 端点，等待 PING");
        struct PendingVideoSample {
            coremedia::SampleBuffer sample;
            std::optional<coremedia::FormatDescription> format;
            std::chrono::steady_clock::time_point received_at;
            bool reset_decoder{};
        };
        std::mutex video_queue_mutex;
        std::condition_variable video_queue_cv;
        std::deque<PendingVideoSample> video_queue;
        std::size_t video_queue_bytes{};
        detail::VideoQueueBudget video_queue_budget;
        const auto read_native_portrait_size = [this]() noexcept {
            return detail::unpack_video_dimensions(
                native_portrait_size_.load(std::memory_order_acquire));
        };
        std::atomic<std::int64_t> last_audio_activity_ns{};
        // The queue preserves normal H.264 reference order. Sustained decoder
        // overload is handled by the producer as a bounded GOP reset: discard
        // through the next IDR, then rebuild the decoder from that keyframe.
        std::jthread video_worker([&](std::stop_token worker_token) noexcept {
            try {
            std::unique_ptr<media::MediaFoundationVideoDecoder> video_decoder;
            std::optional<coremedia::FormatDescription> current_format;
            std::optional<coremedia::FormatDescription> configured_format;
            const auto initial_decoder_status = decoder_switch_.status();
            auto active_decoder_preference = initial_decoder_status.applied;
            auto active_decoder_runtime_mode = initial_decoder_status.runtime_mode;
            auto applied_decoder_generation =
                initial_decoder_status.applied_generation;
            auto retry_decoder_generation = applied_decoder_generation;
            auto next_decoder_switch_retry = std::chrono::steady_clock::time_point{};
            std::uint64_t preference_switch_wait_samples{};
            std::uint64_t video_decode_count{};
            std::uint64_t video_output_count{};
            int orientation_candidate{};
            int orientation_stability{};
            int last_orientation_request{};
            std::optional<std::chrono::steady_clock::time_point> low_portrait_since;
            auto low_portrait_retry_after = std::chrono::steady_clock::time_point::min();
            std::optional<std::chrono::steady_clock::time_point> black_with_audio_since;
            auto black_landscape_retry_after = std::chrono::steady_clock::time_point::min();
            bool native_probe_published{};
            std::optional<std::chrono::steady_clock::time_point> portrait_after_landscape_since;
            bool saw_landscape_source{};
            bool reordered_timing_reported{};
            std::deque<std::pair<std::int64_t, std::chrono::steady_clock::time_point>> input_times;
            const auto decoder_started = std::chrono::steady_clock::now();
            while (!worker_token.stop_requested()) {
                PendingVideoSample pending;
                {
                    std::unique_lock lock(video_queue_mutex);
                    video_queue_cv.wait_for(lock, std::chrono::milliseconds(10), [&] {
                        return worker_token.stop_requested() || !video_queue.empty();
                    });
                    if (worker_token.stop_requested()) break;
                    if (video_queue.empty()) continue;
                    // Preserve H.264 reference pictures: dropping an arbitrary
                    // inter frame would make the decoder wait for the next
                    // IDR and is perceived as a much worse freeze. The queue
                    // is normally empty (decode is faster than 60 fps); it
                    // only absorbs the occasional large keyframe spike.
                    pending = std::move(video_queue.front());
                    video_queue.pop_front();
                    video_queue_bytes -= pending.sample.sample_data.size();
                }
                video_queue_cv.notify_all();
                if (pending.reset_decoder) {
                    video_decoder.reset();
                    current_format.reset();
                    configured_format.reset();
                    decoder_switch_.set_applied_runtime_mode(
                        {active_decoder_preference, applied_decoder_generation},
                        DecoderRuntimeMode::Unknown);
                    active_decoder_runtime_mode = DecoderRuntimeMode::Unknown;
                    input_times.clear();
                    logging::write("video_worker decoder_reset reason=queue_overflow_keyframe");
                }
                if (pending.format) current_format = std::move(pending.format);
                if (!current_format || !current_format->is_video()) continue;
                const auto& format = *current_format;
                if (!video_decoder || !configured_format ||
                    !same_video_decoder_configuration(*configured_format, format)) {
                    video_decoder = std::make_unique<media::MediaFoundationVideoDecoder>(
                        active_decoder_preference);
                    video_decoder->configure(format, 60, 1);
                    active_decoder_runtime_mode = decoder_runtime_mode(
                        video_decoder->decoder_acceleration());
                    decoder_switch_.set_applied_runtime_mode(
                        {active_decoder_preference, applied_decoder_generation},
                        active_decoder_runtime_mode);
                    configured_format = format;
                }
                auto& sample = pending.sample;
                std::size_t sample_offset{};
                const bool has_per_sample_sizes = sample.sample_count > 1 &&
                    sample.sample_sizes.size() == sample.sample_count;
                const auto sample_total = has_per_sample_sizes ? sample.sample_count : 1U;
                for (std::uint32_t sample_index{}; sample_index < sample_total; ++sample_index) {
                    const auto sample_size = has_per_sample_sizes
                        ? sample.sample_sizes[sample_index]
                        : sample.sample_data.size();
                    if (sample_offset > sample.sample_data.size() ||
                        sample_size > sample.sample_data.size() - sample_offset) {
                        logging::write("video queue sample sizes exceed payload; dropping sample");
                        break;
                    }
                    const auto encoded_sample = std::span<const std::uint8_t>(sample.sample_data)
                        .subspan(sample_offset, sample_size);
                    sample_offset += sample_size;

                    const auto decode_started = std::chrono::steady_clock::now();
                    ++video_decode_count;
                    std::int64_t timestamp_100ns{};
                    std::int64_t duration_100ns{166'667};
                    if (sample_index < sample.timing.size()) {
                        const auto& timing = sample.timing[sample_index];
                        if (const auto timestamp = timing.presentation_timestamp.to_100ns())
                            timestamp_100ns = *timestamp;
                        if (const auto duration = timing.duration.to_100ns();
                            duration && *duration > 0) duration_100ns = *duration;
                        if (!reordered_timing_reported && timing.decode_timestamp.valid() &&
                            timing.presentation_timestamp.valid() &&
                            std::abs(timing.decode_timestamp.seconds() -
                                timing.presentation_timestamp.seconds()) > 0.000001) {
                            reordered_timing_reported = true;
                            logging::write(std::format(
                                "video_timing warning=reordered_pts dts={}/{} pts={}/{}",
                                timing.decode_timestamp.value, timing.decode_timestamp.timescale,
                                timing.presentation_timestamp.value, timing.presentation_timestamp.timescale));
                        }
                    }

                    std::vector<media::DecodedFrame> decoded_frames;
                    bool decoded_by_replacement{};
                    const auto requested = decoder_switch_.requested();
                    const auto requested_generation = requested.generation;
                    if (requested_generation != applied_decoder_generation) {
                        const auto requested_preference = requested.preference;
                        if (requested_generation != retry_decoder_generation) {
                            retry_decoder_generation = requested_generation;
                            next_decoder_switch_retry = {};
                            preference_switch_wait_samples = 0;
                        }
                        if (requested_preference == active_decoder_preference) {
                            const bool coalesced = decoder_switch_.commit_if_current(
                                requested, [&] {
                                    applied_decoder_generation = requested_generation;
                                }, active_decoder_runtime_mode);
                            if (coalesced) {
                                next_decoder_switch_retry = {};
                                preference_switch_wait_samples = 0;
                                logging::write(std::format(
                                    "video_worker decoder_switch coalesced generation={} policy={}",
                                    applied_decoder_generation,
                                    media::decoder_preference_name(active_decoder_preference)));
                            }
                        } else if (!media::detail::is_random_access_sample(
                                format, encoded_sample)) {
                            ++preference_switch_wait_samples;
                            if (preference_switch_wait_samples <= 3 ||
                                preference_switch_wait_samples % 60 == 0) {
                                logging::write(std::format(
                                    "video_worker decoder_switch waiting_for_keyframe "
                                    "generation={} observed_samples={}",
                                    requested_generation,
                                    preference_switch_wait_samples));
                            }
                        } else if (std::chrono::steady_clock::now() >=
                            next_decoder_switch_retry) {
                            try {
                                auto replacement =
                                    std::make_unique<media::MediaFoundationVideoDecoder>(
                                        requested_preference);
                                replacement->configure(format, 60, 1);
                                const bool applied = detail::trial_and_commit_decoder(
                                    decoder_switch_, requested, replacement,
                                    [&](auto& candidate) {
                                        return candidate->decode(encoded_sample,
                                            timestamp_100ns, duration_100ns);
                                    },
                                    [&](std::unique_ptr<media::MediaFoundationVideoDecoder>&&
                                            accepted_decoder,
                                        std::vector<media::DecodedFrame>&& accepted_frames) noexcept {
                                        video_decoder.swap(accepted_decoder);
                                        decoded_frames.swap(accepted_frames);
                                        active_decoder_preference = requested_preference;
                                        active_decoder_runtime_mode =
                                            DecoderRuntimeMode::Unknown;
                                        applied_decoder_generation = requested_generation;
                                    });
                                if (!applied) {
                                    const auto latest_request = decoder_switch_.requested();
                                    retry_decoder_generation = latest_request.generation;
                                    next_decoder_switch_retry = {};
                                    preference_switch_wait_samples = 0;
                                    logging::write(std::format(
                                        "video_worker decoder_switch superseded configured_generation={} "
                                        "latest_generation={} latest_policy={}",
                                        requested_generation, latest_request.generation,
                                        media::decoder_preference_name(
                                            latest_request.preference)));
                                } else {
                                    decoded_by_replacement = true;
                                    retry_decoder_generation = requested_generation;
                                    next_decoder_switch_retry = {};
                                    preference_switch_wait_samples = 0;
                                    input_times.clear();
                                    logging::write(std::format(
                                        "video_worker decoder_switch applied generation={} "
                                        "policy={} selected={} actual={} trial_output_frames={}",
                                        applied_decoder_generation,
                                        media::decoder_preference_name(
                                            active_decoder_preference),
                                        video_decoder->selected_decoder_name(),
                                        active_decoder_runtime_mode ==
                                                DecoderRuntimeMode::Hardware
                                            ? "hardware"
                                            : active_decoder_runtime_mode ==
                                                    DecoderRuntimeMode::Software
                                                ? "software"
                                                : "unknown",
                                        decoded_frames.size()));
                                }
                            } catch (const std::exception& error) {
                                // Retain the known-good decoder and feed it this
                                // IDR. A policy request must never terminate the
                                // transport.
                                const bool failure_recorded =
                                    decoder_switch_.mark_failed_if_current(requested);
                                next_decoder_switch_retry =
                                    std::chrono::steady_clock::now() +
                                    std::chrono::seconds(5);
                                preference_switch_wait_samples = 0;
                                logging::write(std::format(
                                    "video_worker decoder_switch rejected generation={} "
                                    "requested={} retained={} retry_ms=5000 reason={}",
                                    requested_generation,
                                    media::decoder_preference_name(requested_preference),
                                    media::decoder_preference_name(
                                        active_decoder_preference),
                                    error.what()));
                                if (!failure_recorded) {
                                    logging::write(std::format(
                                        "video_worker decoder_switch rejection_superseded "
                                        "generation={}", requested_generation));
                                }
                            }
                        }
                    }

                    if (!decoded_by_replacement) {
                        decoded_frames = video_decoder->decode(
                            encoded_sample, timestamp_100ns, duration_100ns);
                    }
                    const auto observed_runtime_mode = decoder_runtime_mode(
                        video_decoder->decoder_acceleration());
                    if (observed_runtime_mode != active_decoder_runtime_mode) {
                        active_decoder_runtime_mode = observed_runtime_mode;
                        decoder_switch_.set_applied_runtime_mode(
                            {active_decoder_preference, applied_decoder_generation},
                            active_decoder_runtime_mode);
                        logging::write(std::format(
                            "video_worker decoder_runtime generation={} mode={}",
                            applied_decoder_generation,
                            active_decoder_runtime_mode == DecoderRuntimeMode::Hardware
                                ? "hardware"
                                : active_decoder_runtime_mode == DecoderRuntimeMode::Software
                                    ? "software"
                                    : "unknown"));
                    }
                    input_times.emplace_back(timestamp_100ns, pending.received_at);
                    // Normal decoder reordering is under a few dozen frames.
                    // Bound diagnostic metadata independently of media data in
                    // case a malformed stream stops returning timestamps.
                    while (input_times.size() > 512) input_times.pop_front();
                    const double decode_ms = std::chrono::duration<double, std::milli>(
                        std::chrono::steady_clock::now() - decode_started).count();
                    const bool report_decode = video_decode_count % 120 == 0 ||
                        (decode_ms >= 20.0 && video_decode_count % 30 == 1);
                    if (report_decode) {
                        logging::write(std::format(
                            "video_decode n={} sample_index={} codec={} decoder={} input_bytes={} decode_ms={:.3f} output={} timestamp={}",
                            video_decode_count, sample_index, media::codec_name(format.video_codec()),
                            video_decoder->selected_decoder_name(), encoded_sample.size(), decode_ms,
                            decoded_frames.empty() ? "no" : "yes", timestamp_100ns));
                    }
                    std::shared_ptr<const media::DecodedFrame> published;
                    for (auto& decoded_frame : decoded_frames) {
                        const auto received = std::find_if(input_times.begin(), input_times.end(),
                            [&](const auto& entry) { return entry.first == decoded_frame.timestamp_100ns; });
                        if (received != input_times.end()) {
                            decoded_frame.received_at = received->second;
                            input_times.erase(received);
                        } else {
                            decoded_frame.received_at = pending.received_at;
                        }
                        published = std::make_shared<media::DecodedFrame>(std::move(decoded_frame));
                        ++video_output_count;
                        std::scoped_lock lock(mutex_);
                        latest_frame_ = published;
                        render_queue_.push_back(published);
                        render_queue_bytes_ += published->nv12.size();
                        constexpr std::size_t MaxRenderQueue = 32;
                        constexpr std::size_t MaxRenderQueueBytes = 128U * 1024U * 1024U;
                        while (render_queue_.size() > 1 &&
                            (render_queue_.size() > MaxRenderQueue ||
                             render_queue_bytes_ > MaxRenderQueueBytes)) {
                            render_queue_bytes_ -= render_queue_.front()->nv12.size();
                            render_queue_.pop_front();
                            ++stale_render_frames_;
                        }
                    }
                    {
                        std::scoped_lock lock(mutex_);
                        // The renderer replaces this with receive-to-display
                        // latency. Keep decode time only until the first frame
                        // is selected, so headless diagnostics still have a
                        // useful value.
                        if (selected_render_frames_ == 0) snapshot_.latency_ms = decode_ms;
                    }
                    if (published && report_decode) {
                        logging::write(std::format(
                            "video_output n={} width={} height={} stride={} nv12_bytes={} timestamp={}",
                            video_decode_count, published->width, published->height,
                            published->stride, published->nv12.size(), published->timestamp_100ns));
                    }
                    if (published && video_output_count % 15 == 0) {
                        const auto detected = padded_content_orientation(*published);
                        const bool ios_low_portrait_tier =
                            published->width >= 880 && published->width <= 890 &&
                            published->height >= 1918 && published->height <= 1922;
                        const auto orientation_now = std::chrono::steady_clock::now();
                        const auto native_size = read_native_portrait_size();
                        if (adaptive_display && !native_probe_published &&
                            preferences_.usb_requested_width == 0 &&
                            preferences_.usb_requested_height == 0 &&
                            published->height > published->width) {
                            native_probe_published = true;
                            const auto packed = (static_cast<std::uint64_t>(published->width) << 32U) |
                                published->height;
                            native_probe_size_.store(packed, std::memory_order_release);
                            logging::write(std::format(
                                "display valeria_probe source={}x{} captured=true",
                                published->width, published->height));
                        }
                        if (published->width > published->height) {
                            saw_landscape_source = true;
                            portrait_after_landscape_since.reset();
                        } else if (saw_landscape_source && (!detected || !*detected)) {
                            if (!portrait_after_landscape_since)
                                portrait_after_landscape_since = orientation_now;
                            if (orientation_now - *portrait_after_landscape_since >=
                                std::chrono::seconds(2)) {
                                requested_display_orientation_.store(1, std::memory_order_release);
                                saw_landscape_source = false;
                                portrait_after_landscape_since.reset();
                                logging::write(std::format(
                                    "display stable_axis_transition=landscape_to_portrait source={}x{} request=probed_native",
                                    published->width, published->height));
                            }
                        } else if (saw_landscape_source) {
                            portrait_after_landscape_since.reset();
                        }
                        const auto orientation_now_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                            orientation_now.time_since_epoch()).count();
                        const auto audio_age_ns = orientation_now_ns -
                            last_audio_activity_ns.load(std::memory_order_acquire);
                        const bool audio_active = audio_age_ns >= 0 &&
                            audio_age_ns <= std::chrono::duration_cast<std::chrono::nanoseconds>(
                                std::chrono::seconds(1)).count();
                        if (audio_active && frame_is_nearly_black(*published)) {
                            if (!black_with_audio_since) black_with_audio_since = orientation_now;
                            if (orientation_now >= black_landscape_retry_after &&
                                orientation_now - *black_with_audio_since >= std::chrono::seconds(1)) {
                                requested_display_orientation_.store(2, std::memory_order_release);
                                last_orientation_request = 2;
                                orientation_candidate = 0;
                                orientation_stability = 0;
                                black_with_audio_since.reset();
                                black_landscape_retry_after = orientation_now + std::chrono::seconds(15);
                                logging::write(std::format(
                                    "display black_with_audio stable_seconds=1 source={}x{} request=landscape target={}x{}",
                                    published->width, published->height,
                                    native_size.height, native_size.width));
                            }
                        } else {
                            black_with_audio_since.reset();
                        }
                        if (ios_low_portrait_tier) {
                            if (!low_portrait_since) low_portrait_since = orientation_now;
                            if (orientation_now >= low_portrait_retry_after &&
                                orientation_now - *low_portrait_since >= std::chrono::seconds(10)) {
                                requested_display_orientation_.store(1, std::memory_order_release);
                                last_orientation_request = 1;
                                orientation_candidate = 0;
                                orientation_stability = 0;
                                low_portrait_since.reset();
                                low_portrait_retry_after = orientation_now + std::chrono::seconds(30);
                                logging::write(std::format(
                                    "display low_portrait_tier={}x{} stable_seconds=10 request=native_portrait target={}x{}",
                                    published->width, published->height,
                                    native_size.width, native_size.height));
                            }
                        } else {
                            low_portrait_since.reset();
                        }
                        // Keep a confirmed landscape request latched. Recent
                        // iOS versions may briefly alternate 1920x1080 with a
                        // portrait carrier while the phone is still sideways;
                        // clearing here would repeatedly restart the encoder.
                        if (last_orientation_request == 1 && published->height > published->width) {
                            last_orientation_request = 0;
                            orientation_candidate = 0;
                            orientation_stability = 0;
                        }
                        const int candidate = detected ? (*detected ? 2 : 1) : 0;
                        if (candidate != 0 && candidate == orientation_candidate)
                            ++orientation_stability;
                        else {
                            orientation_candidate = candidate;
                            orientation_stability = candidate == 0 ? 0 : 1;
                        }
                        const bool request_pending = last_orientation_request != 0 &&
                            ((last_orientation_request == 2 && published->height > published->width) ||
                             (last_orientation_request == 1 && published->width > published->height));
                        if (!request_pending && orientation_stability >= 3 &&
                            candidate != last_orientation_request) {
                            requested_display_orientation_.store(candidate, std::memory_order_release);
                            last_orientation_request = candidate;
                            logging::write(std::format(
                                "display auto_orientation={} source={}x{}",
                                candidate == 2 ? "landscape" : "portrait",
                                published->width, published->height));
                        }
                    }
                }
            }
            const auto elapsed = std::chrono::duration<double>(
                std::chrono::steady_clock::now() - decoder_started).count();
            logging::write(std::format(
                "video_worker stopped input={} output={} output_fps={:.3f}",
                video_decode_count, video_output_count,
                elapsed > 0 ? static_cast<double>(video_output_count) / elapsed : 0.0));
            } catch (...) {
                video_worker_failure.capture_current();
                video_queue_cv.notify_all();
            }
        });
        const auto started = std::chrono::steady_clock::now();
        auto fps_sample_at = started;
        std::uint64_t fps_sample_frames{};
        bool display_reconfigure_pending{};
        bool display_release_seen{};
        bool display_reconfigure_landscape{};
        auto display_release_deadline = started;
        const auto ping_deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
        const auto ping_recovery_deadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
        bool ping_recovery_attempted{};
        while (!stop_token.stop_requested()) {
            video_worker_failure.rethrow_if_set();
            const auto count = usb->read(read_buffer, 250);
            video_worker_failure.rethrow_if_set();
            if (count == 0) {
                if (display_reconfigure_pending &&
                    std::chrono::steady_clock::now() >= display_release_deadline) {
                    const auto native_size = read_native_portrait_size();
                    for (const auto& request : protocol.complete_display_reconfigure())
                        usb->write(request, 1000);
                    logging::write(std::format(
                        "display reconfigure start orientation={} release_seen=false target={}x{}",
                        display_reconfigure_landscape ? "landscape" : "portrait",
                        display_reconfigure_landscape ? native_size.height : native_size.width,
                        display_reconfigure_landscape ? native_size.width : native_size.height));
                    display_reconfigure_pending = false;
                }
                if (!ping_recovery_attempted &&
                    protocol.state() == quicktime::SessionState::WaitingForPing &&
                    std::chrono::steady_clock::now() >= ping_recovery_deadline) {
                    ping_recovery_attempted = true;
                    try {
                        // Aisi sends a normal PING after its first bulk
                        // timeout. Do not issue the extra 0x40/0x40 control
                        // request here; it can reset an otherwise valid iOS
                        // QuickTime session.
                        usb->write(quicktime::make_ping(), 1000);
                    } catch (...) {
                        // Some libusb0 filter builds report a cancelled OUT
                        // request while the device still processes the kick.
                    }
                }
                if (protocol.state() == quicktime::SessionState::WaitingForPing &&
                    std::chrono::steady_clock::now() >= ping_deadline) {
                    throw std::runtime_error("QuickTime endpoint opened but iPhone sent no PING; keep the device unlocked");
                }
                continue;
            }
            const auto packets = decoder.push(std::span(read_buffer).first(count));
            for (const auto& packet : packets) {
                if (display_reconfigure_pending && packet.kind == quicktime::PacketKind::Async &&
                    packet.subtype == quicktime::fourcc('r', 'e', 'l', 's')) {
                    display_release_seen = true;
                    logging::write("display reconfigure release acknowledged");
                }
                if (packet.kind == quicktime::PacketKind::Async &&
                    packet.subtype == quicktime::fourcc('s', 'p', 'r', 'p')) {
                    std::string preview;
                    const auto bytes = std::min<std::size_t>(packet.payload.size(), 96);
                    preview.reserve(bytes * 3);
                    for (std::size_t index = 0; index < bytes; ++index)
                        preview += std::format("{:02x}", packet.payload[index]);
                    logging::write(std::format("async_sprp bytes={} hex={}",
                        packet.payload.size(), preview));
                }
                auto event = protocol.process(packet);
                if (event.state == quicktime::SessionState::Error) throw std::runtime_error(event.warning);
                for (const auto& response : event.outbound) usb->write(response, 1000);

                if (event.video_sample) {
                    PendingVideoSample pending;
                    pending.received_at = std::chrono::steady_clock::now();
                    pending.sample = std::move(*event.video_sample);
                    if (pending.sample.format) pending.format = std::move(pending.sample.format);
                    else if (protocol.video_format()) pending.format = *protocol.video_format();
                    const auto incoming_bytes = pending.sample.sample_data.size();
                    const auto keyframe = sample_contains_keyframe(pending.sample, pending.format);
                    detail::VideoQueueAdmission admission;
                    std::deque<PendingVideoSample> discarded;
                    std::size_t queue_depth{};
                    std::size_t queue_bytes{};
                    bool enqueued{};
                    bool queue_cancelled{};
                    bool was_recovering{};
                    {
                        std::unique_lock lock(video_queue_mutex);
                        if (!video_queue_budget.awaiting_keyframe() &&
                            !video_queue_budget.has_capacity(video_queue.size(),
                                video_queue_bytes, incoming_bytes)) {
                            video_queue_cv.wait_for(lock, std::chrono::milliseconds(20), [&] {
                                return stop_token.stop_requested() ||
                                    video_worker_failure.failed() ||
                                    video_queue_budget.has_capacity(video_queue.size(),
                                        video_queue_bytes, incoming_bytes);
                            });
                        }
                        queue_cancelled = stop_token.stop_requested() ||
                            video_worker_failure.failed();
                        if (!queue_cancelled) {
                            was_recovering = video_queue_budget.awaiting_keyframe();
                            admission = video_queue_budget.admit(video_queue.size(),
                                video_queue_bytes, incoming_bytes, keyframe);
                            if (admission.action == detail::VideoQueueAction::ClearAndDrop ||
                                admission.action == detail::VideoQueueAction::ReplaceWithKeyframe) {
                                discarded.swap(video_queue);
                                video_queue_bytes = 0;
                            }
                            if (admission.action == detail::VideoQueueAction::Enqueue ||
                                admission.action == detail::VideoQueueAction::ReplaceWithKeyframe) {
                                pending.reset_decoder = admission.action ==
                                    detail::VideoQueueAction::ReplaceWithKeyframe;
                                video_queue_bytes += incoming_bytes;
                                video_queue.push_back(std::move(pending));
                                enqueued = true;
                            }
                            queue_depth = video_queue.size();
                            queue_bytes = video_queue_bytes;
                        }
                    }
                    discarded.clear();
                    video_worker_failure.rethrow_if_set();
                    if (queue_cancelled) break;
                    if (admission.entered_recovery) {
                        logging::write(std::format(
                            "video_queue overflow action=drop_until_keyframe "
                            "incoming_bytes={} dropped_samples={} dropped_bytes={}",
                            incoming_bytes, admission.dropped_samples,
                            admission.dropped_bytes));
                    } else if (admission.action ==
                        detail::VideoQueueAction::ReplaceWithKeyframe) {
                        logging::write(std::format(
                            "video_queue recovery={} action=decoder_reset depth={} bytes={} "
                            "dropped_samples={} dropped_bytes={}",
                            was_recovering ? "keyframe" : "overflow_keyframe",
                            queue_depth, queue_bytes, admission.dropped_samples,
                            admission.dropped_bytes));
                    } else if (admission.action == detail::VideoQueueAction::DropIncoming &&
                        (admission.dropped_samples <= 3 || admission.dropped_samples % 60 == 0)) {
                        logging::write(std::format(
                            "video_queue dropping_until_keyframe dropped_samples={} dropped_bytes={}",
                            admission.dropped_samples, admission.dropped_bytes));
                    }
                    if (enqueued) video_queue_cv.notify_one();
                }

                if (event.audio_sample) {
                    last_audio_activity_ns.store(std::chrono::duration_cast<std::chrono::nanoseconds>(
                        std::chrono::steady_clock::now().time_since_epoch()).count(),
                        std::memory_order_release);
                    const auto& sample = *event.audio_sample;
                    const coremedia::FormatDescription* audio_format{};
                    if (sample.format && sample.format->audio) {
                        audio_format = &*sample.format;
                    } else if (protocol.audio_format() && protocol.audio_format()->audio) {
                        audio_format = &*protocol.audio_format();
                    }
                    if (audio_format && audio_format->audio) {
                        std::scoped_lock lock(audio_mutex_);
                        const auto layout = audio::detail::checked_wasapi_buffer_layout(
                            *audio_format->audio);
                        if (layout && !sample.sample_data.empty()) {
                            auto audio_output = std::make_shared<AudioPacket>();
                            audio_output->sequence = ++audio_output_sequence_;
                            audio_output->sample_rate = static_cast<std::uint32_t>(
                                audio_format->audio->sample_rate);
                            audio_output->channels = static_cast<std::uint16_t>(
                                audio_format->audio->channels_per_frame);
                            audio_output->bits_per_sample = static_cast<std::uint16_t>(
                                audio_format->audio->bits_per_channel);
                            audio_output->pcm.assign(sample.sample_data.begin(),
                                sample.sample_data.end());
                            audio_output_queue_.push_back(std::move(audio_output));
                            while (audio_output_queue_.size() > 256)
                                audio_output_queue_.pop_front();
                        }
                        if (!audio_initialization_disabled && !audio_renderer_) {
                            try {
                                audio_renderer_ = std::make_unique<audio::WasapiRenderer>(
                                    *audio_format->audio,
                                    play_audio_.load(std::memory_order_relaxed),
                                    audio_volume_.load(std::memory_order_relaxed));
                            } catch (const std::exception& error) {
                                logging::write(std::format(
                                    "wasapi initialization_disabled error={}", error.what()));
                                audio_initialization_disabled = true;
                            }
                        }
                        if (!audio_initialization_disabled && audio_renderer_)
                            audio_renderer_->enqueue(sample.sample_data);
                    }
                }

                if (event.video_sample || event.audio_sample) {
                    std::scoped_lock lock(mutex_);
                    snapshot_.state = State::Streaming;
                    snapshot_.message = L"投屏中";
                    snapshot_.video_frames = protocol.video_frames();
                    snapshot_.audio_packets = protocol.audio_packets();
                    const auto now = std::chrono::steady_clock::now();
                    const double fps_seconds = std::chrono::duration<double>(now - fps_sample_at).count();
                    if (fps_seconds >= 0.5) {
                        snapshot_.fps = static_cast<double>(
                            snapshot_.video_frames - fps_sample_frames) / fps_seconds;
                        fps_sample_frames = snapshot_.video_frames;
                        fps_sample_at = now;
                    }
                    if (protocol.video_format()) {
                        snapshot_.width = protocol.video_format()->width;
                        snapshot_.height = protocol.video_format()->height;
                    }
                    if (protocol.audio_format() && protocol.audio_format()->audio) {
                        snapshot_.audio_sample_rate = static_cast<std::uint32_t>(protocol.audio_format()->audio->sample_rate);
                        snapshot_.audio_channels = protocol.audio_format()->audio->channels_per_frame;
                    }
                }
            }
            const auto probed_size = display_reconfigure_pending ? 0 :
                native_probe_size_.exchange(0, std::memory_order_acq_rel);
            if (adaptive_display && probed_size != 0 && !display_reconfigure_pending) {
                const auto probed_width = static_cast<std::uint32_t>(probed_size >> 32U);
                const auto probed_height = static_cast<std::uint32_t>(probed_size);
                native_portrait_size_.store(
                    detail::pack_video_dimensions(probed_width, probed_height),
                    std::memory_order_release);
                const std::uint32_t activation_width = quicktime_open_recovered ? 1080U : probed_width;
                const std::uint32_t activation_height = quicktime_open_recovered ? 1920U : probed_height;
                protocol.set_demo_mode(false);
                for (const auto& request : protocol.begin_display_reconfigure(
                    activation_width, activation_height))
                    usb->write(request, 1000);
                display_reconfigure_pending = true;
                display_release_seen = false;
                display_reconfigure_landscape = false;
                display_release_deadline = std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(1200);
                logging::write(std::format(
                    "display valeria_probe disable target={}x{} probed_native={}x{} recovery_fallback={}",
                    activation_width, activation_height, probed_width, probed_height,
                    quicktime_open_recovered));
            }
            const auto requested_orientation = display_reconfigure_pending ? 0 :
                requested_display_orientation_.exchange(0, std::memory_order_acq_rel);
            if (adaptive_display && requested_orientation != 0 && !display_reconfigure_pending) {
                const bool landscape = requested_orientation == 2;
                const auto native_size = read_native_portrait_size();
                const auto requests = protocol.begin_display_reconfigure(
                    landscape ? native_size.height : native_size.width,
                    landscape ? native_size.width : native_size.height);
                for (const auto& request : requests) usb->write(request, 1000);
                display_reconfigure_pending = true;
                display_release_seen = false;
                display_reconfigure_landscape = landscape;
                display_release_deadline = std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(1200);
                logging::write(std::format(
                    "display reconfigure stop orientation={} target={}x{}",
                    landscape ? "landscape" : "portrait",
                    landscape ? native_size.height : native_size.width,
                    landscape ? native_size.width : native_size.height));
            }
            if (display_reconfigure_pending && display_release_seen) {
                const auto native_size = read_native_portrait_size();
                for (const auto& request : protocol.complete_display_reconfigure())
                    usb->write(request, 1000);
                logging::write(std::format(
                    "display reconfigure start orientation={} release_seen=true target={}x{}",
                    display_reconfigure_landscape ? "landscape" : "portrait",
                    display_reconfigure_landscape ? native_size.height : native_size.width,
                    display_reconfigure_landscape ? native_size.width : native_size.height));
                display_reconfigure_pending = false;
            }
        }

        video_worker.request_stop();
        video_queue_cv.notify_all();
        video_worker.join();
        stop_audio_renderer();
        shutdown_usb();
        logging::write("capture_run stop path");
        set_state(State::Stopped, L"投屏已停止");
    } catch (const std::exception& error) {
        // Stop requests intentionally interrupt USB I/O while iOS restores
        // its normal configuration. This is a normal terminal condition.
        stop_audio_renderer();
        shutdown_usb();
        logging::write(std::format("capture_run exception stop_requested={} error={}",
            stop_token.stop_requested(), error.what()));
        if (stop_token.stop_requested() && !video_worker_failure.failed()) {
            set_state(State::Stopped, L"投屏已停止");
        } else {
            set_state(State::Error, L"采集失败：" + widen(error.what()));
        }
    }
}

} // namespace iPhoneMirror::capture
