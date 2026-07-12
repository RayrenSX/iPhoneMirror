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
    };
    for (const auto& [identifier, size] : sizes)
        if (identifier == product_type) return size;
    return {1206, 2622};
}

std::wstring widen(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (length <= 0) return L"未知错误";
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), result.data(), length);
    return result;
}

std::optional<transport::AppleUsbDevice> find_device(transport::QtUsbContext& context, const std::string& serial) {
    for (auto& device : context.enumerate()) {
        if (transport::apple_usb_serial_equal(device.serial, serial)) return device;
    }
    return std::nullopt;
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

void restore_libusb0_configuration(const std::string& serial) noexcept {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        try {
            (void)transport::LibUsb0Connection::disable_quicktime_configuration(serial);
            return;
        } catch (...) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    } while (std::chrono::steady_clock::now() < deadline);
}

std::optional<bool> padded_content_orientation(const media::DecodedFrame& frame) {
    if (frame.width < 64 || frame.height < 64 || frame.nv12.empty()) return std::nullopt;
    const auto stride = static_cast<std::size_t>(std::abs(frame.stride));
    if (stride < frame.width || frame.nv12.size() < stride * frame.height) return std::nullopt;
    std::uint32_t min_x = frame.width, min_y = frame.height, max_x{}, max_y{};
    std::uint64_t active{};
    constexpr std::uint32_t step = 8;
    for (std::uint32_t y = 0; y < frame.height; y += step) {
        const auto* row = frame.nv12.data() + static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < frame.width; x += step) {
            if (row[x] <= 28) continue;
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
    if (stride < frame.width || frame.nv12.size() < stride * frame.height) return false;
    std::uint64_t samples{}, dark{};
    constexpr std::uint32_t step = 16;
    for (std::uint32_t y = 0; y < frame.height; y += step) {
        const auto* row = frame.nv12.data() + static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < frame.width; x += step) {
            ++samples;
            if (row[x] <= 24) ++dark;
        }
    }
    return samples >= 128 && dark * 100U >= samples * 98U;
}

} // namespace

CaptureSession::CaptureSession(std::string serial, bool play_audio)
    : CaptureSession(std::move(serial), CapturePreferences{.play_audio = play_audio}) {}

CaptureSession::CaptureSession(std::string serial, CapturePreferences preferences,
    std::wstring product_type)
    : serial_(std::move(serial)), preferences_(preferences), product_type_(std::move(product_type)),
      target_fps_(preferences.target_fps),
      play_audio_(preferences.play_audio),
      audio_volume_(std::clamp(preferences.audio_volume, 0.0F, 1.0F)) {}
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

void CaptureSession::request_display_orientation(bool landscape) noexcept {
    requested_display_orientation_.store(landscape ? 2 : 1, std::memory_order_release);
}

void CaptureSession::stop_audio_renderer() noexcept {
    std::unique_ptr<audio::WasapiRenderer> renderer;
    {
        std::scoped_lock lock(audio_mutex_);
        renderer = std::move(audio_renderer_);
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
            stale_render_frames_ += dropped;
        } else {
            frame = std::move(render_queue_.front());
            render_queue_.pop_front();
        }
        selected = ++selected_render_frames_;
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
            selected, depth, dropped, stale_render_frames_, pipeline_ms));
    }
    return frame;
}

void CaptureSession::set_state(State state, std::wstring message) {
    std::scoped_lock lock(mutex_);
    snapshot_.state = state;
    snapshot_.message = std::move(message);
}

void CaptureSession::run(std::stop_token stop_token) noexcept {
    const auto native = native_display_size(product_type_);
    native_portrait_width_ = native.width;
    native_portrait_height_ = native.height;
    std::string product_type_ascii;
    product_type_ascii.reserve(product_type_.size());
    for (const auto ch : product_type_)
        product_type_ascii.push_back(ch <= 0x7f ? static_cast<char>(ch) : '?');
    logging::write(std::format(
        "capture_run begin serial={} backend={} product_type={} usb_display_size={}x{} target_fps={} audio={} volume={:.3f}", serial_,
        usb_backend_ == UsbBackend::LibUsb0 ? "libusb0" :
        usb_backend_ == UsbBackend::UsbDk ? "usbdk" : "libusb1",
        product_type_ascii,
        native_portrait_width_, native_portrait_height_,
        target_fps(),
        play_audio_.load(std::memory_order_relaxed),
        audio_volume_.load(std::memory_order_relaxed)));
    std::unique_ptr<CaptureConnection> usb;
    quicktime::StreamDecoder decoder;
    quicktime::SessionOptions session_options;
    // Deliberately keep SessionOptions' protocol defaults. GUI resolution and
    // frame-rate choices are local presentation controls and must not alter
    // HPD1/USB negotiation.
    // Always negotiate the audio stream. The playback toggle is deliberately
    // local so it can be switched on again without restarting USB/QuickTime.
    session_options.request_audio = true;
    session_options.requested_width = native_portrait_width_;
    session_options.requested_height = native_portrait_height_;
    session_options.demo_mode = true;
    if (preferences_.usb_requested_width != 0 && preferences_.usb_requested_height != 0) {
        session_options.requested_width = preferences_.usb_requested_width;
        session_options.requested_height = preferences_.usb_requested_height;
        session_options.demo_mode = false;
        logging::write(std::format("advanced_usb_request={}x{}",
            preferences_.usb_requested_width, preferences_.usb_requested_height));
    }
    // Probe each device instead of relying solely on a model table. Valeria
    // is disabled immediately after the first valid portrait format arrives.
    quicktime::SessionProtocol protocol(session_options);
    bool audio_initialization_disabled{};
    std::vector<std::uint8_t> read_buffer(1024U * 1024U);
    bool shutdown_done{};

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
            if (!device) throw std::runtime_error("iPhone disconnected before capture started");
            if (!device->quicktime_configuration) {
                (void)transport::LibUsb0Connection::enable_quicktime_configuration(serial_);
                set_state(State::WaitingForDevice, L"等待 iPhone 以 QuickTime 配置重新连接");
                const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(12);
                do {
                    if (stop_token.stop_requested()) {
                        restore_libusb0_configuration(serial_);
                        set_state(State::Stopped, L"投屏已取消");
                        return;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    device = transport::find_libusb0_device(serial_);
                    if (device && device->quicktime_configuration) break;
                } while (std::chrono::steady_clock::now() < deadline);
                if (!device || !device->quicktime_configuration) {
                    throw std::runtime_error("iPhone did not re-enumerate with QuickTime interface 0x2A");
                }
                // Aisi waits roughly one second after discovering the new
                // device node before set-configuration/claim. Give Windows
                // and iOS the same settle window after re-enumeration.
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            try {
                usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                    transport::LibUsb0Connection::open_quicktime(serial_));
            } catch (const std::exception& first_error) {
                // Configuration 5 can survive a crashed/aborted owner while
                // its interface claim does not. Recover this device in-place
                // instead of requiring the entire GUI process to restart.
                logging::write(std::format(
                    "quicktime_open recovery begin serial={} first_error={}",
                    serial_, first_error.what()));
                set_state(State::ActivatingUsb, L"姝ｅ湪鎭㈠ iPhone QuickTime USB 閰嶇疆");
                restore_libusb0_configuration(serial_);
                if (stop_token.stop_requested()) {
                    set_state(State::Stopped, L"鎶曞睆宸插彇娑?");
                    return;
                }
                auto normal_device = transport::find_libusb0_device(serial_);
                if (!normal_device)
                    throw std::runtime_error(std::string(first_error.what()) +
                        "; recovery could not find the iPhone after restoring USB configuration");
                (void)transport::LibUsb0Connection::enable_quicktime_configuration(serial_);
                const auto retry_deadline = std::chrono::steady_clock::now() +
                    std::chrono::seconds(12);
                do {
                    if (stop_token.stop_requested()) {
                        restore_libusb0_configuration(serial_);
                        set_state(State::Stopped, L"鎶曞睆宸插彇娑?");
                        return;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    device = transport::find_libusb0_device(serial_);
                    if (device && device->quicktime_configuration) break;
                } while (std::chrono::steady_clock::now() < retry_deadline);
                if (!device || !device->quicktime_configuration)
                    throw std::runtime_error(std::string(first_error.what()) +
                        "; recovery timed out waiting for QuickTime configuration 5");
                std::this_thread::sleep_for(std::chrono::seconds(1));
                usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                    transport::LibUsb0Connection::open_quicktime(serial_));
                quicktime_open_recovered = true;
                logging::write(std::format(
                    "quicktime_open recovery success serial={}", serial_));
            }
        } else {
            qt_context = std::make_unique<transport::QtUsbContext>(usb_backend_ == UsbBackend::UsbDk);
            auto device = find_device(*qt_context, serial_);
            if (!device) throw std::runtime_error("iPhone disconnected before capture started");
            if (!device->quicktime_configuration) {
                (void)transport::QtUsbConnection::enable_quicktime_configuration(*qt_context, serial_);
                set_state(State::WaitingForDevice, L"等待 iPhone 以 QuickTime 配置重新连接");
                const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(12);
                do {
                    if (stop_token.stop_requested()) { set_state(State::Stopped, L"投屏已取消"); return; }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    device = find_device(*qt_context, serial_);
                    if (device && device->quicktime_configuration) break;
                } while (std::chrono::steady_clock::now() < deadline);
                if (!device || !device->quicktime_configuration) {
                    throw std::runtime_error("iPhone did not re-enumerate with QuickTime interface 0x2A");
                }
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            usb = std::make_unique<CaptureConnectionAdapter<transport::QtUsbConnection>>(
                transport::QtUsbConnection::open_quicktime(*qt_context, serial_));
        }
        // libusb1/UsbDk needs an explicit halt clear. The libusb0 filter
        // backend historically succeeded without this extra control transfer
        // and starts its bulk read immediately after claiming interface 2.
        if (usb_backend_ != UsbBackend::LibUsb0) {
            try { usb->clear_halt(); } catch (...) {}
        }
        set_state(State::Handshaking, L"已连接 QuickTime 端点，等待 PING");
        struct PendingVideoSample {
            coremedia::SampleBuffer sample;
            std::optional<coremedia::FormatDescription> format;
            std::chrono::steady_clock::time_point received_at;
        };
        std::mutex video_queue_mutex;
        std::condition_variable video_queue_cv;
        std::deque<PendingVideoSample> video_queue;
        std::atomic<std::int64_t> last_audio_activity_ns{};
        // USB/protocol reception must not wait for a slow H.264 picture. A
        // FIFO queue decouples reception from decode while retaining every
        // reference frame; unlike newest-frame dropping it cannot corrupt an
        // inter-predicted H.264 sequence after a keyframe spike.
        std::jthread video_worker([&](std::stop_token worker_token) {
            std::unique_ptr<media::MediaFoundationH264Decoder> video_decoder;
            std::optional<coremedia::FormatDescription> current_format;
            std::uint32_t decoder_width{};
            std::uint32_t decoder_height{};
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
                }
                if (pending.format) current_format = std::move(pending.format);
                if (!current_format || !current_format->is_video()) continue;
                const auto& format = *current_format;
                if (!video_decoder || decoder_width != format.width || decoder_height != format.height) {
                    video_decoder = std::make_unique<media::MediaFoundationH264Decoder>();
                    video_decoder->configure(format, 60, 1);
                    decoder_width = format.width;
                    decoder_height = format.height;
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
                        if (timing.presentation_timestamp.valid()) {
                            timestamp_100ns = static_cast<std::int64_t>(
                                timing.presentation_timestamp.seconds() * 10'000'000.0);
                        }
                        if (timing.duration.valid()) {
                            duration_100ns = static_cast<std::int64_t>(
                                timing.duration.seconds() * 10'000'000.0);
                        }
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
                    input_times.emplace_back(timestamp_100ns, pending.received_at);
                    // Normal decoder reordering is under a few dozen frames.
                    // Bound diagnostic metadata independently of media data in
                    // case a malformed stream stops returning timestamps.
                    while (input_times.size() > 512) input_times.pop_front();
                    auto decoded_frames = video_decoder->decode(
                        encoded_sample, timestamp_100ns, duration_100ns);
                    const double decode_ms = std::chrono::duration<double, std::milli>(
                        std::chrono::steady_clock::now() - decode_started).count();
                    const bool report_decode = video_decode_count % 120 == 0 ||
                        (decode_ms >= 20.0 && video_decode_count % 30 == 1);
                    if (report_decode) {
                        logging::write(std::format(
                            "video_decode n={} sample_index={} input_bytes={} decode_ms={:.3f} output={} timestamp={}",
                            video_decode_count, sample_index, encoded_sample.size(), decode_ms,
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
                        constexpr std::size_t MaxRenderQueue = 32;
                        if (render_queue_.size() > MaxRenderQueue) render_queue_.pop_front();
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
                        if (!native_probe_published &&
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
                                    native_portrait_height_, native_portrait_width_));
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
                                    native_portrait_width_, native_portrait_height_));
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
            const auto count = usb->read(read_buffer, 250);
            if (count == 0) {
                if (display_reconfigure_pending &&
                    std::chrono::steady_clock::now() >= display_release_deadline) {
                    for (const auto& request : protocol.complete_display_reconfigure())
                        usb->write(request, 1000);
                    logging::write(std::format(
                        "display reconfigure start orientation={} release_seen=false target={}x{}",
                        display_reconfigure_landscape ? "landscape" : "portrait",
                        display_reconfigure_landscape ? native_portrait_height_ : native_portrait_width_,
                        display_reconfigure_landscape ? native_portrait_width_ : native_portrait_height_));
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
                    {
                        std::scoped_lock lock(video_queue_mutex);
                        video_queue.push_back(std::move(pending));
                        if (video_queue.size() > 3 && video_queue.size() % 10 == 0) {
                            logging::write(std::format("video_queue depth={}", video_queue.size()));
                        }
                    }
                    video_queue_cv.notify_one();
                }

                if (event.audio_sample && !audio_initialization_disabled) {
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
                        if (!audio_renderer_) {
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
                        if (audio_renderer_) audio_renderer_->enqueue(sample.sample_data);
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
            if (probed_size != 0 && !display_reconfigure_pending) {
                const auto probed_width = static_cast<std::uint32_t>(probed_size >> 32U);
                const auto probed_height = static_cast<std::uint32_t>(probed_size);
                native_portrait_width_ = probed_width;
                native_portrait_height_ = probed_height;
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
            if (requested_orientation != 0 && !display_reconfigure_pending) {
                const bool landscape = requested_orientation == 2;
                const auto requests = protocol.begin_display_reconfigure(
                    landscape ? native_portrait_height_ : native_portrait_width_,
                    landscape ? native_portrait_width_ : native_portrait_height_);
                for (const auto& request : requests) usb->write(request, 1000);
                display_reconfigure_pending = true;
                display_release_seen = false;
                display_reconfigure_landscape = landscape;
                display_release_deadline = std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(1200);
                logging::write(std::format(
                    "display reconfigure stop orientation={} target={}x{}",
                    landscape ? "landscape" : "portrait",
                    landscape ? native_portrait_height_ : native_portrait_width_,
                    landscape ? native_portrait_width_ : native_portrait_height_));
            }
            if (display_reconfigure_pending && display_release_seen) {
                for (const auto& request : protocol.complete_display_reconfigure())
                    usb->write(request, 1000);
                logging::write(std::format(
                    "display reconfigure start orientation={} release_seen=true target={}x{}",
                    display_reconfigure_landscape ? "landscape" : "portrait",
                    display_reconfigure_landscape ? native_portrait_height_ : native_portrait_width_,
                    display_reconfigure_landscape ? native_portrait_width_ : native_portrait_height_));
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
        if (stop_token.stop_requested()) {
            set_state(State::Stopped, L"投屏已停止");
        } else {
            set_state(State::Error, L"采集失败：" + widen(error.what()));
        }
    }
}

} // namespace iPhoneMirror::capture
