#include "Capture/CaptureSession.h"
#include "Device/AppleUsbDiscovery.h"
#include "Capture/UsbConfigurationRestorePolicy.h"

#include "Transport/UsbMuxClient.h"

#include "Media/MediaFoundationDecoder.h"
#include "Audio/WasapiRenderer.h"
#include "Logging.h"
#include "Protocol/QuickTimePacket.h"
#include "Transport/LibUsb0Transport.h"
#include "Transport/QtUsbTransport.h"

#include <Windows.h>
#include <cfgmgr32.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <exception>
#include <format>
#include <functional>
#include <limits>
#include <memory>
#include <semaphore>
#include <thread>
#include <optional>
#include <utility>
#include <vector>

namespace iPhoneMirror::capture {
namespace {

std::binary_semaphore usb_transition_gate{1};
// The legacy libusb0/AppleUsbFilter stack performs configuration changes as
// asynchronous PnP transactions. A per-session transition gate cannot
// protect the driver when two CaptureSession instances tear down close
// together, so serialize only the restore phase process-wide. Streaming bulk
// transfers remain concurrent; the lease is held from the active-handle 0x52
// request through descriptor observation and any bounded fallback.
std::counting_semaphore<1> libusb0_restore_gate{1};
std::mutex active_usb_backend_mutex;
std::array<std::uint32_t, 3> active_usb_backend_counts{};

std::optional<std::size_t> preferred_active_usb_backend() noexcept {
    try {
        std::scoped_lock lock(active_usb_backend_mutex);
        std::optional<std::size_t> selected;
        for (std::size_t index{}; index < active_usb_backend_counts.size(); ++index) {
            if (active_usb_backend_counts[index] == 0) continue;
            if (!selected || active_usb_backend_counts[index] >
                    active_usb_backend_counts[*selected]) selected = index;
        }
        return selected;
    } catch (...) {
        return std::nullopt;
    }
}

void retain_active_usb_backend(std::size_t backend) noexcept {
    if (backend >= active_usb_backend_counts.size()) return;
    try {
        std::scoped_lock lock(active_usb_backend_mutex);
        ++active_usb_backend_counts[backend];
    } catch (...) {}
}

void release_active_usb_backend(std::size_t backend) noexcept {
    if (backend >= active_usb_backend_counts.size()) return;
    try {
        std::scoped_lock lock(active_usb_backend_mutex);
        if (active_usb_backend_counts[backend] != 0)
            --active_usb_backend_counts[backend];
    } catch (...) {}
}

class DeferredCleanup final {
public:
    DeferredCleanup() = default;
    explicit DeferredCleanup(std::function<void()> cleanup)
        : cleanup_(std::move(cleanup)) {}
    ~DeferredCleanup() { run_now(); }
    DeferredCleanup(const DeferredCleanup&) = delete;
    DeferredCleanup& operator=(const DeferredCleanup&) = delete;

    void arm(std::function<void()> cleanup) {
        cleanup_ = std::move(cleanup);
    }

    void disarm() noexcept { cleanup_ = {}; }

    void run_now() noexcept {
        auto cleanup = std::move(cleanup_);
        cleanup_ = {};
        if (!cleanup) return;
        try { cleanup(); } catch (...) {}
    }

private:
    std::function<void()> cleanup_;
};

class LibUsb0RestoreLease final {
public:
    LibUsb0RestoreLease() = default;
    ~LibUsb0RestoreLease() { release(); }
    LibUsb0RestoreLease(const LibUsb0RestoreLease&) = delete;
    LibUsb0RestoreLease& operator=(const LibUsb0RestoreLease&) = delete;

    [[nodiscard]] bool acquire(std::string_view device_fp) noexcept {
        if (held_) return true;
        // Do not retry a timed-out acquisition from a second cleanup path. A
        // second wait would make application shutdown unbounded and could
        // overlap the previous owner's late PnP transition.
        if (attempted_) return false;
        attempted_ = true;
        device_fp_ = device_fp;
        logging::write(std::format(
            "usb_restore_gate acquire_begin device_fp={} timeout_ms=15000",
            device_fp));
        try {
            held_ = libusb0_restore_gate.try_acquire_for(
                std::chrono::seconds(15));
        } catch (...) {
            held_ = false;
        }
        if (held_) {
            logging::write(std::format(
                "usb_restore_gate acquired=true device_fp={}", device_fp));
        } else {
            logging::write(logging::Level::Warning, "usb_restore_gate",
                std::format(
                    "usb_restore_gate timeout device_fp={} action=skip_restore_controls",
                    device_fp));
        }
        return held_;
    }

    void release() noexcept {
        if (!held_) return;
        held_ = false;
        try { libusb0_restore_gate.release(); } catch (...) {}
        logging::write(std::format(
            "usb_restore_gate released device_fp={}", device_fp_));
    }

    [[nodiscard]] bool held() const noexcept { return held_; }

private:
    bool attempted_{};
    bool held_{};
    std::string_view device_fp_;
};

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
    const transport::QtUsbContext& context,
    const transport::AppleUsbIdentity& identity,
    bool require_quicktime = false) {
    return context.find_apple_device(identity, require_quicktime);
}

std::uint16_t mux_product_id_for(std::string_view serial) noexcept {
    if (serial.empty()) return 0;
    for (const auto port : {std::uint16_t{27015}, std::uint16_t{37015}}) {
        if (!transport::Socket::probe_loopback(port)) continue;
        try {
            transport::UsbMuxClient mux(port);
            for (const auto& device : mux.list_devices()) {
                if (!transport::apple_usb_serial_equal(device.serial, serial) ||
                    device.product_id == 0 || device.product_id > 0xffffU)
                    continue;
                return static_cast<std::uint16_t>(device.product_id);
            }
        } catch (...) {
        }
    }
    return 0;
}

bool usbmux_contains_serial(std::string_view serial) noexcept {
    if (serial.empty()) return false;
    for (const auto port : {std::uint16_t{27015}, std::uint16_t{37015}}) {
        if (!transport::Socket::probe_loopback(port)) continue;
        try {
            transport::UsbMuxClient mux(port);
            for (const auto& device : mux.list_devices()) {
                if (transport::apple_usb_serial_equal(device.serial, serial))
                    return true;
            }
        } catch (...) {
        }
    }
    return false;
}

transport::AppleUsbIdentity requested_usb_identity(std::string_view serial) {
    transport::AppleUsbIdentity identity;
    identity.serial = serial;
    identity.original_product_id = mux_product_id_for(serial);
    return identity;
}

class CaptureConnection {
public:
    virtual ~CaptureConnection() = default;
    virtual std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms) = 0;
    virtual void write(std::span<const std::uint8_t> source, unsigned timeout_ms) = 0;
    virtual void clear_halt() = 0;
    virtual void recover_handshake() = 0;
    [[nodiscard]] virtual bool request_normal_configuration() = 0;
    virtual void cancel_pending_io() noexcept = 0;
    virtual void clear_io_cancellation() noexcept = 0;
    virtual void close() noexcept = 0;
};

template <typename Connection>
class CaptureConnectionAdapter final : public CaptureConnection {
public:
    explicit CaptureConnectionAdapter(Connection connection) : connection_(std::move(connection)) {}
    ~CaptureConnectionAdapter() override { connection_.close(); }
    std::size_t read(std::span<std::uint8_t> destination, unsigned timeout_ms) override {
        return connection_.read(destination, timeout_ms);
    }
    void write(std::span<const std::uint8_t> source, unsigned timeout_ms) override {
        connection_.write(source, timeout_ms);
    }
    void clear_halt() override { connection_.clear_halt(); }
    void recover_handshake() override { connection_.recover_handshake(); }
    bool request_normal_configuration() override {
        return connection_.request_normal_configuration();
    }
    void cancel_pending_io() noexcept override { connection_.cancel_pending_io(); }
    void clear_io_cancellation() noexcept override {
        connection_.clear_io_cancellation();
    }
    void close() noexcept override { connection_.close(); }
private:
    Connection connection_;
};

struct UsbConfigurationRestoreResult {
    bool normal_observed{};
    bool disable_requested{};
};

template <typename Observe, typename Disable>
UsbConfigurationRestoreResult restore_usb_configuration(
    std::string_view backend, Observe&& observe,
    Disable&& disable, bool primary_request_sent = false) noexcept {
    detail::UsbConfigurationRestorePolicy policy(primary_request_sent);
    // Windows tears down the temporary QuickTime composite node and starts
    // the Apple parent in separate PnP transactions. On the affected
    // libusb0/Apple filter stack this bounded state transition can take several
    // seconds; keep observing the descriptor/PnP evidence without issuing
    // another configuration request.
    constexpr auto RestoreObservationWindow = std::chrono::seconds(12);
    auto deadline = std::chrono::steady_clock::now() + RestoreObservationWindow;
    while (std::chrono::steady_clock::now() < deadline) {
        auto observation = detail::UsbConfigurationObservation::Missing;
        try { observation = observe(); } catch (...) {}

        const auto action = policy.observe(observation);
        if (action == detail::UsbConfigurationRestoreAction::Complete) {
            logging::write(std::format(
                "usb_configuration_restore backend={} result=normal disable_requested={}",
                backend, policy.disable_requested()));
            return {
                .normal_observed = true,
                .disable_requested = policy.disable_requested(),
            };
        }
        if (action == detail::UsbConfigurationRestoreAction::DisableQuickTime) {
            // 0x52 disconnects the device and can surface as I/O/NO_DEVICE even
            // when iOS accepted it. Mark it attempted before entering the
            // transport so no error path can send the request a second time.
            logging::write(std::format(
                "usb_configuration_restore backend={} action=disable_requested", backend));
            deadline = std::chrono::steady_clock::now() + RestoreObservationWindow;
            try { disable(); } catch (...) {}
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    logging::write(std::format(
        "usb_configuration_restore backend={} result=timeout disable_requested={}",
        backend, policy.disable_requested()));
    return {
        .normal_observed = false,
        .disable_requested = policy.disable_requested(),
    };
}

UsbConfigurationRestoreResult restore_libusb0_configuration(
    const transport::AppleUsbIdentity& identity,
    bool primary_request_sent = false) noexcept {
    bool restore_request_sent = primary_request_sent;
    const auto filter_safety =
        device::inspect_apple_usb_filter_stack(identity.serial);
    // The exact-device helper now owns the old handle until removal/arrival is
    // complete, including on the known libusb0 + Apple filter stack. Keep an
    // indeterminate stack observation-only, but allow the guarded one-shot
    // restore for a positively identified safe or known-unsafe stack.
    const bool fallback_allowed =
        filter_safety.safety != device::AppleUsbFilterSafety::Indeterminate;
    if (filter_safety.safety == device::AppleUsbFilterSafety::Unsafe) {
        logging::write(logging::Level::Warning, "usb_safety",
            std::format(
                "usb_configuration_restore backend=libusb0 fallback=guarded_exact_pnp diagnostic={}",
                filter_safety.diagnostic));
    } else if (!fallback_allowed) {
        logging::write(logging::Level::Warning, "usb_safety",
            std::format(
                "usb_configuration_restore backend=libusb0 fallback=disabled diagnostic={}",
                filter_safety.diagnostic));
    }
    auto result = restore_usb_configuration("libusb0",
        [&, previous_state = std::optional<bool>{}, stable_normal = 0U,
            stable_quicktime_residual = 0U,
            automatic_restore_deadline = std::chrono::steady_clock::now() +
                std::chrono::seconds(3)]() mutable {
        // USBMux is the exact, open-free authority for normal configuration.
        // Do not enumerate or reopen libusb0 while AppleUsbFilter is processing
        // the disconnecting control request.
        const bool mux_present = usbmux_contains_serial(identity.serial);
        const auto pnp =
            device::inspect_apple_normal_usb_stack(identity.serial);
        const bool pnp_normal =
            device::is_complete_apple_normal_usb_stack(pnp);
        const bool parent_present = pnp.parent_started;
        const bool normal = mux_present && pnp_normal && parent_present;
        stable_normal = normal ? stable_normal + 1U : 0U;
        // The QuickTime parent is started but has no normal management child
        // or exact USBMux row. A missing parent is only an in-flight PnP
        // transition and must never authorize a control open.
        const bool quicktime_residual = detail::is_libusb0_quicktime_pnp_state(
            mux_present, pnp_normal, parent_present, true);
        stable_quicktime_residual = quicktime_residual
            ? stable_quicktime_residual + 1U : 0U;
        auto observation = detail::stabilize_normal_configuration_observation(
            normal, stable_normal);
        // Closing the claimed stream handle can complete iOS's transition back
        // to the management configuration without another vendor request. Give
        // that observed transition a bounded grace period. Only if it fails to
        // stabilize do we authorize one separate-control fallback request.
        if (fallback_allowed && !restore_request_sent &&
            stable_quicktime_residual >= 2 &&
            std::chrono::steady_clock::now() >= automatic_restore_deadline)
            observation = detail::UsbConfigurationObservation::QuickTime;
        if (!previous_state || *previous_state != normal || stable_normal == 2 ||
            stable_quicktime_residual == 2) {
            logging::write(std::format(
                "usb_configuration_restore backend=libusb0 observation={} usbmux_exact_present={} pnp_normal_stack={} parent_present={} mi00_media_started={} mi01_management_started={} request_sent={} stable_normal={} stable_quicktime_residual={}",
                observation == detail::UsbConfigurationObservation::VerifiedNormal
                    ? "verified_normal" :
                observation == detail::UsbConfigurationObservation::QuickTime
                    ? "fallback_validation_ready" : normal ? "normal_pending" : "missing",
                mux_present, pnp_normal, parent_present,
                pnp.media_interface_started,
                pnp.management_interface_started, restore_request_sent,
                stable_normal, stable_quicktime_residual));
            previous_state = normal;
        }
        return observation;
    }, [&] {
        restore_request_sent = true;
        (void)transport::LibUsb0Connection::disable_quicktime_configuration(identity);
    }, primary_request_sent);
    result.disable_requested = restore_request_sent;
    return result;
}

UsbConfigurationRestoreResult restore_qt_configuration(bool use_usbdk,
    const transport::AppleUsbIdentity& identity,
    bool primary_request_sent = false) noexcept {
    const auto backend = use_usbdk ? std::string_view("usbdk") :
        std::string_view("libusb1");
    return restore_usb_configuration(backend, [&] {
        transport::QtUsbContext context(use_usbdk);
        const auto device = find_device(context, identity);
        if (!device) return detail::UsbConfigurationObservation::Missing;
        return device->quicktime_configuration
            ? detail::UsbConfigurationObservation::QuickTime
            : detail::UsbConfigurationObservation::Normal;
    }, [&] {
        transport::QtUsbContext context(use_usbdk);
        (void)transport::QtUsbConnection::disable_quicktime_configuration(
            context, identity);
    }, primary_request_sent);
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

bool frame_is_protected_black_candidate(
    const media::DecodedFrame& frame) noexcept {
    if (frame.width < 32 || frame.height < 32 || frame.nv12.empty()) return false;
    const auto stride = static_cast<std::size_t>(std::abs(frame.stride));
    const auto component_bytes = frame.pixel_format == media::PixelFormat::P010
        ? 2U : 1U;
    const auto row_bytes = static_cast<std::size_t>(frame.width) * component_bytes;
    const auto chroma_rows = (static_cast<std::size_t>(frame.height) + 1U) / 2U;
    const auto luma_bytes = stride * frame.height;
    if (stride < row_bytes || frame.nv12.size() < luma_bytes + stride * chroma_rows)
        return false;

    std::uint64_t luma_samples{}, dark_luma{}, neutral_chroma{}, chroma_samples{};
    std::uint64_t luma_sum{};
    constexpr std::uint32_t step = 16;
    for (std::uint32_t y = 0; y < frame.height; y += step) {
        const auto* row = frame.nv12.data() + static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < frame.width; x += step) {
            const auto value = luma_as_8bit(frame, row, x);
            ++luma_samples;
            luma_sum += value;
            if (value <= 24) ++dark_luma;
        }
    }
    for (std::uint32_t y = 0; y < frame.height / 2U; y += step) {
        const auto* row = frame.nv12.data() + luma_bytes +
            static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x + 1U < frame.width; x += step) {
            const auto u = luma_as_8bit(frame, row, x);
            const auto v = luma_as_8bit(frame, row, x + 1U);
            ++chroma_samples;
            if (u >= 112 && u <= 144 && v >= 112 && v <= 144)
                ++neutral_chroma;
        }
    }
    return luma_samples >= 128 && chroma_samples >= 64 &&
        dark_luma * 1000U >= luma_samples * 995U &&
        luma_sum <= luma_samples * 20U &&
        neutral_chroma * 100U >= chroma_samples * 98U;
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

bool try_begin_usb_device_discovery() noexcept {
    try {
        return usb_transition_gate.try_acquire();
    } catch (...) {
        return false;
    }
}

void end_usb_device_discovery() noexcept {
    usb_transition_gate.release();
}

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

void VideoQueueBudget::reset() noexcept {
    awaiting_keyframe_ = false;
    dropped_samples_ = 0;
    dropped_bytes_ = 0;
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
    acquire_usb_transition_gate();
    try {
        preflight_device_.reset();
        const auto filter_safety =
            device::inspect_apple_usb_filter_stack(serial_);
        if (filter_safety.safety != device::AppleUsbFilterSafety::Safe) {
            logging::write(logging::Level::Warning, "usb_safety",
                std::format("capture proceeding with conservative USB lifecycle device_fp={} diagnostic={}",
                    logging::fingerprint(serial_), filter_safety.diagnostic));
        }
        // Synchronous preflight keeps the GUI from reporting a false successful start.
        std::string failure =
            "libusb cannot see the selected iPhone; USB backend/driver is not ready";
        std::vector<std::string> backend_diagnostics;
        bool ready{};
        std::unique_ptr<transport::AppleUsbDevice> selected_device;
        const auto requested_identity = requested_usb_identity(serial_);
        const auto backend_name = [](UsbBackend backend) constexpr -> std::string_view {
            return backend == UsbBackend::LibUsb0 ? "libusb0" :
                backend == UsbBackend::UsbDk ? "usbdk" : "libusb1";
        };
        const auto record_backend = [&](UsbBackend backend, std::string detail) {
            backend_diagnostics.push_back(std::format("{}:{}", backend_name(backend), detail));
            logging::write(std::format("usb_backend_attempt backend={} {}",
                backend_name(backend), detail));
        };

        std::vector<UsbBackend> backend_order;
        const auto append_backend = [&](UsbBackend backend) {
            if (std::find(backend_order.begin(), backend_order.end(), backend) ==
                backend_order.end()) backend_order.push_back(backend);
        };
        const auto active_backend = preferred_active_usb_backend();
        const bool unsafe_filter_stack =
            filter_safety.safety != device::AppleUsbFilterSafety::Safe;
        if (unsafe_filter_stack) {
            // AppleUsbFilter has a known WDF handle-lifetime failure when the
            // legacy libusb0 path changes the QuickTime configuration. Prefer
            // the WinUSB/libusb-1 backend (and UsbDk when installed) so this
            // process does not enter that filter unless no safer backend can
            // open the exact device. The libusb0 fallback preserves wired
            // capture on installations that only expose the legacy filter.
            append_backend(use_usbdk ? UsbBackend::UsbDk : UsbBackend::LibUsb1);
            append_backend(use_usbdk ? UsbBackend::LibUsb1 : UsbBackend::UsbDk);
            if (active_backend)
                append_backend(static_cast<UsbBackend>(*active_backend));
            append_backend(UsbBackend::LibUsb0);
            logging::write(std::format(
                "usb_backend_order safety={} preferred=non_libusb0 active_backend={} order=libusb1_or_usbdk_then_fallback",
                filter_safety.safety == device::AppleUsbFilterSafety::Unsafe
                    ? "unsafe" : "indeterminate",
                active_backend ? std::to_string(*active_backend) : "none"));
        } else {
            // Continue on the backend already used by a live session whenever
            // it can see the target. This keeps active-device topology keys in
            // the same namespace and avoids a cross-backend descriptor reopen.
            if (active_backend)
                append_backend(static_cast<UsbBackend>(*active_backend));
            append_backend(UsbBackend::LibUsb0);
            append_backend(use_usbdk ? UsbBackend::UsbDk : UsbBackend::LibUsb1);
            append_backend(use_usbdk ? UsbBackend::LibUsb1 : UsbBackend::UsbDk);
        }

        for (const auto backend : backend_order) {
            record_backend(backend, "begin");
            if (backend == UsbBackend::LibUsb0) {
                if (!transport::libusb0_available()) {
                    record_backend(backend, "unavailable");
                    continue;
                }
                try {
                    const auto device =
                        transport::find_libusb0_device(requested_identity);
                    if (!device) {
                        failure = "libusb0 cannot find the selected iPhone";
                        record_backend(backend, "target_not_found");
                        continue;
                    }
                    if (!device->can_open) {
                        failure = "libusb0 sees the iPhone but cannot open it";
                        record_backend(backend, "target_not_openable");
                        continue;
                    }
                    if (!device->active_configuration_known) {
                        failure = "libusb0 opened the selected iPhone but could not read its active USB configuration; no configuration change was attempted";
                        record_backend(backend, "active_configuration_unreadable");
                        continue;
                    }
                    usb_backend_ = backend;
                    selected_device =
                        std::make_unique<transport::AppleUsbDevice>(*device);
                    ready = true;
                    record_backend(backend, std::format(
                        "selected topology={} active_configuration={} quicktime_descriptor={}",
                        device->topology_id, device->active_configuration,
                        device->quicktime_configuration));
                    break;
                } catch (const std::exception& error) {
                    failure = error.what();
                    record_backend(backend, std::format("exception={}", error.what()));
                } catch (...) {
                    failure = "libusb0 backend raised an unknown exception";
                    record_backend(backend, "exception=unknown");
                }
                continue;
            }

            const bool candidate_uses_usbdk = backend == UsbBackend::UsbDk;
            try {
                transport::QtUsbContext context(candidate_uses_usbdk);
                const auto device = find_device(context, requested_identity);
                if (!device) {
                    failure = std::format("{} cannot find the selected iPhone", backend_name(backend));
                    record_backend(backend, "target_not_found");
                    continue;
                }
                if (!device->can_open) {
                    failure = "libusb sees the iPhone but cannot open it; check the USB filter backend";
                    record_backend(backend, "target_not_openable");
                    continue;
                }
                usb_backend_ = backend;
                selected_device =
                    std::make_unique<transport::AppleUsbDevice>(*device);
                ready = true;
                record_backend(backend, std::format(
                    "selected topology={} quicktime_descriptor={} usbdk={}",
                    device->topology_id, device->quicktime_configuration,
                    candidate_uses_usbdk));
                break;
            } catch (const std::exception& error) {
                failure = error.what();
                record_backend(backend, std::format("exception={}", error.what()));
            } catch (...) {
                failure = std::format("{} backend raised an unknown exception",
                    backend_name(backend));
                record_backend(backend, "exception=unknown");
            }
        }

        if (!ready) {
            std::string attempts;
            for (const auto& diagnostic : backend_diagnostics) {
                if (!attempts.empty()) attempts += "; ";
                attempts += diagnostic;
            }
            throw std::runtime_error(std::format("{}; backend_attempts={}",
                failure, attempts));
        }
        preflight_device_ = std::move(selected_device);
        set_state(State::ActivatingUsb, L"正在激活 QuickTime USB 配置");
        worker_ = std::jthread([this](std::stop_token token) { run(token); });
    } catch (...) {
        release_usb_transition_gate();
        throw;
    }
}

void CaptureSession::acquire_usb_transition_gate() noexcept {
    if (usb_transition_gate_held_.load(std::memory_order_acquire)) return;
    usb_transition_gate.acquire();
    usb_transition_gate_held_.store(true, std::memory_order_release);
}

void CaptureSession::release_usb_transition_gate() noexcept {
    if (usb_transition_gate_held_.exchange(false, std::memory_order_acq_rel))
        usb_transition_gate.release();
}

void CaptureSession::stop() noexcept {
    if (worker_.joinable()) {
        const bool terminal_error_already_published =
            snapshot().state == State::Error;
        if (!terminal_error_already_published)
            set_state(State::Stopping, L"正在停止投屏");
        worker_.request_stop();
        // Cancel pending I/O only on transports that implement a safe cancel.
        // The legacy libusb0 transport deliberately treats this callback as a
        // no-op and lets its bounded synchronous transfers return naturally.
        {
            std::scoped_lock lock(active_usb_mutex_);
            if (active_usb_cancel_) active_usb_cancel_();
        }
        worker_.join();
        // A normal stop may race with the bulk read timeout/close path. Keep
        // the terminal state stable for the GUI unless the worker reported a
        // genuine capture error.
        if (!terminal_error_already_published && snapshot().state != State::Error)
            set_state(State::Stopped, L"投屏已停止");
    }
    // Decoded frames are immutable but device-specific. Do not let the native
    // preview or screenshot path expose the previous iPhone after a stop and
    // subsequent selection change.
    {
        std::scoped_lock lock(mutex_);
        latest_frame_.reset();
    }
    release_usb_transition_gate();
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
    logging::write(std::format("video render_fps_limit={}", target_fps));
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
    // Display orientation is negotiated by iOS as part of the active stream.
    // Restarting QuickTime from a preview rotation can terminate mirroring.
    (void)landscape;
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
    // All preview windows read the same immutable latest-frame mailbox. A
    // destructive FIFO here leaves multi-window renderers with stale GPU
    // references, eventually exhausting the shared-texture pool.
    std::scoped_lock lock(mutex_);
    return latest_frame_;
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
    if (state != State::Streaming)
        protected_video_detected_.store(false, std::memory_order_release);
    snapshot_.state = state;
    if (state != State::Error) {
        snapshot_.failure_kind = FailureKind::None;
        snapshot_.failure_stage = FailureStage::None;
        snapshot_.error_code = 0;
    }
    snapshot_.message = std::move(message);
}

void CaptureSession::set_failure(FailureKind kind, FailureStage stage,
    std::int32_t error_code, std::wstring message) {
    std::scoped_lock lock(mutex_);
    snapshot_.state = State::Error;
    snapshot_.failure_kind = kind;
    snapshot_.failure_stage = stage;
    snapshot_.error_code = error_code;
    snapshot_.message = std::move(message);
}

void CaptureSession::set_stopped_warning(FailureKind kind, FailureStage stage,
    std::int32_t error_code, std::wstring message) {
    std::scoped_lock lock(mutex_);
    snapshot_.state = State::Stopped;
    snapshot_.failure_kind = kind;
    snapshot_.failure_stage = stage;
    snapshot_.error_code = error_code;
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
        "capture_run begin device_fp={} backend={} product_type={} usb_display_size={}x{} render_fps_limit={} audio={} volume={:.3f} decoder_policy={} color_policy={}", device_fp,
        usb_backend_ == UsbBackend::LibUsb0 ? "libusb0" :
        usb_backend_ == UsbBackend::UsbDk ? "usbdk" : "libusb1",
        product_type_ascii,
        native.width, native.height,
        target_fps(),
        play_audio_.load(std::memory_order_relaxed),
        audio_volume_.load(std::memory_order_relaxed),
        media::decoder_preference_name(decoder_switch_.requested().preference),
        static_cast<unsigned>(preferences_.color_output_preference)));
    DeferredCleanup transition_release(
        [this] { release_usb_transition_gate(); });
    UsbConfigurationRestoreResult configuration_restore_result;
    bool configuration_restore_attempted{};
    bool active_normal_request_sent{};
    bool libusb0_restore_authorized{};
    std::optional<transport::AppleUsbIdentity> libusb0_restore_identity;
    LibUsb0RestoreLease libusb0_restore_lease;
    bool libusb0_restore_lease_acquired{};
    DeferredCleanup configuration_restore;
    DeferredCleanup active_backend_release;
    auto preflight_device = std::move(preflight_device_);
    // Keep the transport context alive until after the connection has been
    // closed on every exception path. Destruction is reverse declaration
    // order, so the handle must be declared after its owning context.
    std::unique_ptr<transport::QtUsbContext> qt_context;
    std::unique_ptr<CaptureConnection> usb;
    DeferredCleanup active_connection_release([this] {
        std::scoped_lock lock(active_usb_mutex_);
        active_usb_cancel_ = {};
    });
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
    auto failure_stage = FailureStage::UsbActivation;
    auto failure_kind = FailureKind::UsbConnection;
    std::int32_t failure_code{-2101};
    bool peer_session_ended{};

    // Once the QuickTime endpoint is open, every exit path must send the same
    // HPA0/HPD0 shutdown controls used by the working macOS/Aisi clients.
    // This also covers a session that never returned its initial PING.
    const auto shutdown_usb = [&]() noexcept {
        if (!usb || shutdown_done) return;
        shutdown_done = true;
        const bool handshake_started = protocol.state() != quicktime::SessionState::WaitingForPing;
        const auto stop_messages = protocol.stop_messages();
        logging::write(std::format(
            "shutdown_usb device_fp={} handshake_started={} stop_messages={}",
            device_fp, handshake_started, stop_messages.size()));
        try {
            std::size_t stop_messages_written{};
            for (const auto& message : stop_messages) {
                try {
                    usb->write(message, 500);
                    ++stop_messages_written;
                } catch (const std::exception& error) {
                    logging::write(logging::Level::Warning, "shutdown",
                        std::format(
                            "shutdown_usb device_fp={} stop_write_failed index={} error={}",
                            device_fp, stop_messages_written, error.what()));
                    break;
                } catch (...) {
                    logging::write(logging::Level::Warning, "shutdown",
                        std::format(
                            "shutdown_usb device_fp={} stop_write_failed index={} error=unknown",
                            device_fp, stop_messages_written));
                    break;
                }
            }

            std::size_t release_count{};
            std::size_t sync_stop_count{};
            std::size_t decoded_packet_count{};
            std::string drain_result = "timeout";
            const auto release_deadline = std::chrono::steady_clock::now() +
                (peer_session_ended || !handshake_started
                    ? std::chrono::seconds(1)
                    : std::chrono::seconds(6));
            while (release_count < 2 && std::chrono::steady_clock::now() < release_deadline) {
                try {
                    const auto count = usb->read(read_buffer, 250);
                    if (count == 0) continue;
                    for (const auto& packet : decoder.push(std::span(read_buffer).first(count))) {
                        ++decoded_packet_count;
                        // Reply to SYNC STOP before accepting RELS. Dropping
                        // this RPLY leaves the device clock session open.
                        try {
                            const auto event = protocol.process(packet);
                            if (packet.kind == quicktime::PacketKind::Sync &&
                                packet.subtype == quicktime::fourcc('s', 't', 'o', 'p'))
                                ++sync_stop_count;
                            for (const auto& response : event.outbound) {
                                try { usb->write(response, 500); } catch (...) {}
                            }
                        } catch (...) {}
                        if (packet.kind == quicktime::PacketKind::Async &&
                            packet.subtype == quicktime::fourcc('r', 'e', 'l', 's')) {
                            ++release_count;
                        }
                    }
                } catch (const std::exception& error) {
                    drain_result = std::format("io_error:{}", error.what());
                    break;
                } catch (...) {
                    drain_result = "io_error:unknown";
                    break;
                }
            }
            bool final_hpd0_sent{};
            if (release_count >= 2) {
                drain_result = "complete";
                for (const auto& message : protocol.complete_stop_messages()) {
                    try {
                        usb->write(message, 500);
                        final_hpd0_sent = true;
                    } catch (const std::exception& error) {
                        drain_result = std::format("final_hpd0_error:{}", error.what());
                    } catch (...) {
                        drain_result = "final_hpd0_error:unknown";
                    }
                }
            }
            logging::write(std::format(
                "shutdown_usb device_fp={} drain_result={} stop_written={}/{} sync_stop={} releases={}/2 decoded_packets={} final_hpd0_sent={}",
                device_fp, drain_result, stop_messages_written, stop_messages.size(),
                sync_stop_count, release_count, decoded_packet_count,
                final_hpd0_sent));
        } catch (...) {}
        // AppleUsbFilter is unsafe when the stream handle that owns bulk pipes
        // is also used to disconnect its own PnP node. For libusb0, take the
        // process-wide restore lease and close that claimed stream handle
        // first; the deferred restore then opens one short-lived, unclaimed
        // control handle. libusb-1/UsbDk retain their active-handle path.
        try {
            if (usb_backend_ == UsbBackend::LibUsb0) {
                libusb0_restore_lease_acquired =
                    libusb0_restore_lease.acquire(device_fp);
                logging::write(std::format(
                    "usb_configuration_restore action=close_stream_before_control device_fp={} lease_acquired={}",
                    device_fp, libusb0_restore_lease_acquired));
            } else {
                libusb0_restore_lease_acquired = true;
                const bool acknowledged = usb->request_normal_configuration();
                active_normal_request_sent = true;
                logging::write(std::format(
                    "usb_configuration_restore action=active_handle_requested acknowledged={}",
                    acknowledged));
            }
        } catch (const std::exception& error) {
            // An exception means interface release failed before 0x52, so
            // keep the marker clear and permit one fallback only after close.
            logging::write(logging::Level::Warning, "usb",
                std::format("usb_configuration_restore action=active_handle_not_sent error={}",
                    error.what()));
        } catch (...) {
            logging::write(logging::Level::Warning, "usb",
                "usb_configuration_restore action=active_handle_not_sent error=unknown");
        }
        usb->close();
    };
    const auto prepare_shutdown_usb = [&]() noexcept {
        // Stop and teardown use the same lock so a cancellation cannot be
        // lost between a transfer being submitted and published. Clear the
        // persistent marker only after any concurrent Stop callback returns;
        // a later Stop can still cancel a teardown transfer if the driver
        // stalls while the worker is releasing QuickTime.
        std::scoped_lock lock(active_usb_mutex_);
        if (usb) usb->clear_io_cancellation();
    };
    const auto finalize_configuration_restore = [&](bool publish_failure = true) noexcept {
        if (!configuration_restore_attempted) return true;
        const bool restored = configuration_restore_result.normal_observed;
        logging::write(std::format(
            "usb_configuration_restore finalized device_fp={} normal_observed={} disable_requested={} primary_request_sent={}",
            device_fp, configuration_restore_result.normal_observed,
            configuration_restore_result.disable_requested,
            active_normal_request_sent));
        if (!restored && publish_failure) {
            set_stopped_warning(FailureKind::UsbConnection,
                FailureStage::SessionTeardown,
                -2108,
                L"投屏停止时未确认 Apple USB 设备恢复普通配置；已释放投屏资源，请重新插拔数据线后再试");
        }
        return restored;
    };

    try {
        bool quicktime_open_recovered{};
        bool newly_activated_libusb0{};
        if (usb_backend_ == UsbBackend::LibUsb0) {
            bool quicktime_activation_requested{};
            bool adopted_existing_quicktime{};
            std::optional<transport::AppleUsbDevice> device;
            if (preflight_device) device = std::move(*preflight_device);
            else device = transport::find_libusb0_device(serial_);
            if (!device)
                throw std::runtime_error("Apple device disconnected before capture started");
            auto identity = transport::make_apple_usb_identity(*device);
            logging::write(std::format(
                "usb_identity device_fp={} pid={:04x} configs={}/{} active_config={} active_config_known={} expected_qt_config={} topology={}",
                device_fp, device->product_id, device->configuration_count,
                device->highest_configuration_value,
                device->active_configuration,
                device->active_configuration_known,
                identity.expected_quicktime_configuration,
                identity.topology_id));
            if (transport::is_libusb0_quicktime_configuration_active(*device)) {
                adopted_existing_quicktime = true;
                // Preflight successfully opened and read the exact target in
                // its QuickTime configuration, so a bounded restore is safe if
                // the subsequent claim fails.
                libusb0_restore_authorized = true;
                logging::write(std::format(
                    "usb_preflight device_fp={} configuration=quicktime action=adopt_existing_exact_serial",
                    device_fp));
                libusb0_restore_identity = identity;
                configuration_restore.arm([&] {
                    configuration_restore_attempted = true;
                    if (!libusb0_restore_authorized) {
                        logging::write(logging::Level::Warning, "usb",
                            std::format(
                                "usb_configuration_restore backend=libusb0 action=skip_control reason=quicktime_interface_not_ready device_fp={}",
                                device_fp));
                        return;
                    }
                    if (!libusb0_restore_lease_acquired) {
                        libusb0_restore_lease_acquired =
                            libusb0_restore_lease.acquire(device_fp);
                    }
                    if (!libusb0_restore_lease_acquired) return;
                    configuration_restore_result = restore_libusb0_configuration(
                        *libusb0_restore_identity, active_normal_request_sent);
                });
            }
            if (!transport::is_libusb0_quicktime_configuration_active(*device)) {
                quicktime_activation_requested = true;
                newly_activated_libusb0 = true;
                set_state(State::WaitingForDevice,
                    L"等待 Apple 设备以 QuickTime 配置重新连接");
                failure_stage = FailureStage::DeviceReenumeration;
                failure_code = -2102;
                const bool activation_acknowledged =
                    transport::LibUsb0Connection::enable_quicktime_configuration(identity);
                if (!activation_acknowledged) {
                    throw std::runtime_error(
                        "Apple device rejected the QuickTime configuration request and no USB interface transition occurred");
                }
                libusb0_restore_identity = identity;
                configuration_restore.arm([&] {
                    configuration_restore_attempted = true;
                    if (!libusb0_restore_authorized) {
                        logging::write(logging::Level::Warning, "usb",
                            std::format(
                                "usb_configuration_restore backend=libusb0 action=skip_control reason=quicktime_interface_not_ready device_fp={}",
                                device_fp));
                        return;
                    }
                    if (!libusb0_restore_lease_acquired) {
                        libusb0_restore_lease_acquired =
                            libusb0_restore_lease.acquire(device_fp);
                    }
                    if (!libusb0_restore_lease_acquired) return;
                    configuration_restore_result = restore_libusb0_configuration(
                        *libusb0_restore_identity, active_normal_request_sent);
                });
                logging::write(std::format(
                    "usb_activation requested device_fp={} acknowledged={} expected_qt_config={}",
                    device_fp, activation_acknowledged,
                    identity.expected_quicktime_configuration));
                if (stop_token.stop_requested()) {
                    set_state(State::Stopped, L"投屏已取消");
                    return;
                }
                logging::write(std::format(
                    "usb_reenumeration device_fp={} backend=libusb0 source=configuration_helper stable=true stop_requested=false",
                    device_fp));
            }
            failure_stage = FailureStage::InterfaceOpen;
            failure_code = -2103;
            if (!usb) {
                try {
                    auto connection = transport::LibUsb0Connection::open_quicktime(
                        identity, {
                            .allow_conventional_fallback = false,
                            .allow_configuration_initialization =
                                quicktime_activation_requested ||
                                adopted_existing_quicktime,
                            .wait_for_activated_descriptor =
                                quicktime_activation_requested,
                            .stop_token = stop_token,
                        });
                    if (libusb0_restore_identity &&
                        !connection.active_topology_id().empty()) {
                        const auto previous_topology =
                            libusb0_restore_identity->topology_id;
                        libusb0_restore_identity->topology_id =
                            connection.active_topology_id();
                        logging::write(std::format(
                            "usb_identity device_fp={} action=update_restore_topology previous={} active={}",
                            device_fp, previous_topology,
                            libusb0_restore_identity->topology_id));
                    }
                    usb = std::make_unique<CaptureConnectionAdapter<transport::LibUsb0Connection>>(
                        std::move(connection));
                    libusb0_restore_authorized = true;
                } catch (const std::exception& error) {
                    logging::write(logging::Level::Warning, "usb",
                        std::format("quicktime_open failed device_fp={} recovery=disabled error={}",
                            device_fp, error.what()));
                    throw;
                }
            }
        } else {
            const bool use_usbdk = usb_backend_ == UsbBackend::UsbDk;
            qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
            std::optional<transport::AppleUsbDevice> device;
            if (preflight_device) device = std::move(*preflight_device);
            else device = find_device(*qt_context,
                requested_usb_identity(serial_));
            if (!device)
                throw std::runtime_error("Apple device disconnected before capture started");
            const auto identity = transport::make_apple_usb_identity(*device);
            const auto quicktime_active = [&] {
                return device->quicktime_configuration &&
                    device->active_configuration_known &&
                    device->active_configuration ==
                        device->quicktime_endpoints.configuration;
            };
            if (quicktime_active()) {
                configuration_restore.arm([&, use_usbdk, identity] {
                    configuration_restore_attempted = true;
                    configuration_restore_result = restore_qt_configuration(
                        use_usbdk, identity, active_normal_request_sent);
                });
            }
            if (!quicktime_active()) {
                const bool activation_acknowledged =
                    transport::QtUsbConnection::enable_quicktime_configuration(
                        *qt_context, identity);
                configuration_restore.arm([&, use_usbdk, identity] {
                    configuration_restore_attempted = true;
                    configuration_restore_result = restore_qt_configuration(
                        use_usbdk, identity, active_normal_request_sent);
                });
                logging::write(std::format(
                    "usb_activation requested device_fp={} acknowledged={} expected_qt_config={}",
                    device_fp, activation_acknowledged,
                    identity.expected_quicktime_configuration));
                qt_context.reset();
                set_state(State::WaitingForDevice,
                    L"等待 Apple 设备以 QuickTime 配置重新连接");
                failure_stage = FailureStage::DeviceReenumeration;
                failure_code = -2102;
                const auto deadline = std::chrono::steady_clock::now() +
                    std::chrono::seconds(20);
                std::string last_usb_diagnostic;
                do {
                    if (stop_token.stop_requested()) {
                        qt_context.reset();
                        set_state(State::Stopped, L"投屏已取消");
                        return;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    try {
                        qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
                        device = qt_context->find_apple_device(identity, true);
                        const auto diagnostic = device
                            ? std::format("target_ready=true config={} interface={}",
                                device->quicktime_endpoints.configuration,
                                device->quicktime_endpoints.interface_number)
                            : std::string("target_ready=false");
                        if (diagnostic != last_usb_diagnostic) {
                            logging::write(std::format(
                                "usb_reenumeration device_fp={} backend={} {}",
                                device_fp, use_usbdk ? "usbdk" : "libusb1",
                                diagnostic));
                            last_usb_diagnostic = diagnostic;
                        }
                        if (device && device->quicktime_configuration &&
                            device->active_configuration_known &&
                            device->active_configuration ==
                                device->quicktime_endpoints.configuration) break;
                    } catch (const std::exception& error) {
                        qt_context.reset();
                        logging::write(logging::Level::Warning, "usb",
                            std::format("usb_reenumeration pending device_fp={} backend={} error={}",
                                device_fp, use_usbdk ? "usbdk" : "libusb1", error.what()));
                    }
                } while (std::chrono::steady_clock::now() < deadline);
                if (!device || !device->quicktime_configuration ||
                    !device->active_configuration_known ||
                    device->active_configuration !=
                        device->quicktime_endpoints.configuration) {
                    logging::write(std::format(
                        "usb_reenumeration descriptor_timeout device_fp={} backend={} expected_qt_config={} fallback=disabled",
                        device_fp, use_usbdk ? "usbdk" : "libusb1",
                        identity.expected_quicktime_configuration));
                    throw std::runtime_error(
                        "Apple device did not expose a stable QuickTime USB interface after activation");
                }
                if (!usb) std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            failure_stage = FailureStage::InterfaceOpen;
            failure_code = -2103;
            if (!usb) {
                if (!qt_context)
                    qt_context = std::make_unique<transport::QtUsbContext>(use_usbdk);
                usb = std::make_unique<CaptureConnectionAdapter<transport::QtUsbConnection>>(
                    transport::QtUsbConnection::open_quicktime(*qt_context, identity, false));
            }
        }
        // libusb1/UsbDk needs an explicit halt clear. The libusb0 filter
        // backend historically succeeded without this extra control transfer
        // and starts its bulk read immediately after claiming the discovered
        // QuickTime interface.
        if (usb_backend_ != UsbBackend::LibUsb0) {
            try { usb->clear_halt(); } catch (...) {}
        }
        {
            std::scoped_lock lock(active_usb_mutex_);
            active_usb_cancel_ = [connection = usb.get()] {
                connection->cancel_pending_io();
            };
        }
        if (stop_token.stop_requested()) usb->cancel_pending_io();
        const auto active_backend = static_cast<std::size_t>(usb_backend_);
        retain_active_usb_backend(active_backend);
        active_backend_release.arm([active_backend] {
            release_active_usb_backend(active_backend);
        });
        set_state(State::Handshaking, L"已连接 QuickTime 端点，等待 PING");
        failure_stage = FailureStage::QuickTimeHandshake;
        failure_kind = FailureKind::Timeout;
        failure_code = -2104;
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
        std::atomic_bool fast_stream_reconnect_requested{};
        std::atomic_uint32_t decoder_reconnect_generation{};
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
            detail::ProtectedVideoDetector protected_video_detector;
            bool native_probe_published{};
            bool reordered_timing_reported{};
            std::uint32_t applied_reconnect_generation{};
            std::deque<std::pair<std::int64_t, std::chrono::steady_clock::time_point>> input_times;
            const auto decoder_started = std::chrono::steady_clock::now();
            while (!worker_token.stop_requested()) {
                const auto reconnect_generation = decoder_reconnect_generation.load(
                    std::memory_order_acquire);
                if (reconnect_generation != applied_reconnect_generation) {
                    video_decoder.reset();
                    current_format.reset();
                    configured_format.reset();
                    input_times.clear();
                    applied_reconnect_generation = reconnect_generation;
                    decoder_switch_.set_applied_runtime_mode(
                        {active_decoder_preference, applied_decoder_generation},
                        DecoderRuntimeMode::Unknown);
                    logging::write("video_worker decoder_reset reason=quicktime_fast_reconnect");
                }
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
                    if (configured_format) {
                        std::scoped_lock lock(mutex_);
                        latest_frame_.reset();
                        logging::write(std::format(
                            "video_worker preview_frames_cleared format_change={}x{}_to_{}x{}",
                            configured_format->width, configured_format->height,
                            format.width, format.height));
                    }
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
                    }
                    {
                        std::scoped_lock lock(mutex_);
                        // The renderer replaces this with receive-to-display
                        // latency. Keep decode time for headless diagnostics.
                        snapshot_.latency_ms = decode_ms;
                    }
                    if (published && report_decode) {
                        logging::write(std::format(
                            "video_output n={} width={} height={} stride={} nv12_bytes={} timestamp={}",
                            video_decode_count, published->width, published->height,
                            published->stride, published->nv12.size(), published->timestamp_100ns));
                    }
                    // Keep shared GPU frames on the GPU path. The protected-content
                    // and letterbox heuristics accept unavailable CPU pixels, while
                    // readback during a live format transition can race driver-owned
                    // NV12 surfaces and destabilize the capture process.
                    std::shared_ptr<const media::DecodedFrame> analysis_frame = published;
                    if (published) {
                        if (!analysis_frame->nv12.empty())
                            protected_video_detector.observe(
                                frame_is_protected_black_candidate(*analysis_frame),
                                std::chrono::steady_clock::now());
                        protected_video_detected_.store(
                            protected_video_detector.detected(),
                            std::memory_order_release);
                    }
                    if (published && video_output_count % 15 == 0) {
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
        auto ping_deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
        std::optional<std::chrono::steady_clock::time_point>
            first_video_wait_started;
        auto ping_recovery_deadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
        bool ping_recovery_attempted{};
        detail::FastStreamReconnectGate fast_stream_reconnect_gate;
        detail::StreamingSilenceWatchdog media_silence_watchdog;
        detail::StreamingSilenceWatchdog video_silence_watchdog;
        const auto request_fast_reconnect_for_missing_frame_rate = [&] {
            const auto now = std::chrono::steady_clock::now();
            if (video_silence_watchdog.silence_duration(now) <
                std::chrono::milliseconds(2500)) return;
            if (!fast_stream_reconnect_gate.request()) return;
            fast_stream_reconnect_requested.store(true, std::memory_order_release);
            {
                std::scoped_lock lock(mutex_);
                snapshot_.fps = 0;
                snapshot_.message = L"画面帧率不可用，正在重新连接投屏";
                snapshot_.state = State::Handshaking;
            }
            logging::write(
                std::format("quicktime_fast_reconnect requested=frame_rate_unavailable "
                    "attempt={} video_silence_intervals=10 usb_configuration=retained",
                    fast_stream_reconnect_gate.attempt_count()));
        };
        const auto detect_streaming_media_silence = [&] {
            const auto now = std::chrono::steady_clock::now();
            if (!media_silence_watchdog.expired(now)) return;

            const auto silence = media_silence_watchdog.silence_duration(now);
            const bool usb_parent_present =
                device::is_apple_usb_parent_present(serial_);
            failure_stage = FailureStage::VideoStream;
            failure_kind = usb_parent_present
                ? FailureKind::SystemClosed
                : FailureKind::DeviceDisconnected;
            failure_code = usb_parent_present ? -2109 : -2110;
            peer_session_ended = usb_parent_present;
            const auto diagnostic = usb_parent_present
                ? "The iPhone or iPad ended mirroring while the wired USB device remained connected"
                : "The wired Apple USB device disappeared after the media stream became silent";
            logging::write(logging::Level::Warning, "capture",
                std::format(
                    "streaming_media_silence device_fp={} elapsed_ms={} "
                    "video_frames={} audio_packets={} usb_parent_present={} "
                    "classification={}",
                    device_fp, silence.count(), protocol.video_frames(),
                    protocol.audio_packets(), usb_parent_present,
                    usb_parent_present ? "system_closed" : "device_disconnected"));
            if (peer_session_ended) {
                // Publish the terminal reason before teardown. The managed
                // layer immediately detaches previews, then waits for Stop to
                // finish the bounded native cleanup before showing a prompt.
                set_failure(failure_kind, failure_stage, failure_code,
                    widen(diagnostic));
            }
            throw std::runtime_error(diagnostic);
        };
        while (!stop_token.stop_requested()) {
            video_worker_failure.rethrow_if_set();
            if (fast_stream_reconnect_requested.exchange(false, std::memory_order_acq_rel)) {
                try {
                    {
                        std::scoped_lock lock(video_queue_mutex);
                        video_queue.clear();
                        video_queue_bytes = 0;
                        video_queue_budget.reset();
                    }
                    video_queue_cv.notify_all();
                    decoder_reconnect_generation.fetch_add(1, std::memory_order_acq_rel);
                    stop_audio_renderer();
                    usb->clear_io_cancellation();
                    decoder.reset();
                    protocol.reset();
                    usb->recover_handshake();
                    usb->write(quicktime::make_ping(), 1000);
                    media_silence_watchdog.arm(std::chrono::steady_clock::now());
                    video_silence_watchdog.arm(std::chrono::steady_clock::now());
                    first_video_wait_started.reset();
                    ping_recovery_attempted = true;
                    ping_deadline = std::chrono::steady_clock::now() +
                        std::chrono::seconds(8);
                    ping_recovery_deadline = std::chrono::steady_clock::now() +
                        std::chrono::seconds(1);
                    logging::write(
                        "quicktime_fast_reconnect sent=true usb_configuration=retained");
                } catch (const std::exception& error) {
                    logging::write(logging::Level::Warning, "usb", std::format(
                        "quicktime_fast_reconnect send_failed={} usb_configuration=retained",
                        error.what()));
                }
            }
            const auto count = usb->read(read_buffer, 250);
            video_worker_failure.rethrow_if_set();
            if (protocol.state() != quicktime::SessionState::WaitingForPing &&
                protocol.video_frames() == 0) {
                const auto now = std::chrono::steady_clock::now();
                if (!first_video_wait_started) first_video_wait_started = now;
                if (now - *first_video_wait_started >= std::chrono::seconds(12)) {
                    failure_stage = FailureStage::VideoStream;
                    failure_kind = FailureKind::NoVideoFrames;
                    failure_code = -2105;
                    throw std::runtime_error(
                        "QuickTime session established but no video frame arrived within 12 seconds");
                }
            }
            if (count == 0) {
                request_fast_reconnect_for_missing_frame_rate();
                detect_streaming_media_silence();
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
                        // A freshly activated Apple QuickTime configuration
                        // can expose and claim its bulk pipes before iOS starts
                        // the endpoint. The reference libusb0 path performs
                        // this one control kick after the first read timeout.
                        // Never send it while adopting an existing session.
                        if (newly_activated_libusb0)
                            usb->recover_handshake();
                        usb->write(quicktime::make_ping(), 1000);
                        logging::write(std::format(
                            "quicktime_handshake recovery_sent=true control_kick={}",
                            newly_activated_libusb0));
                    } catch (const std::exception& error) {
                        // Some libusb0 filter builds report a cancelled OUT
                        // request while the device still processes the kick.
                        logging::write(logging::Level::Warning, "usb",
                            std::format(
                                "quicktime_handshake recovery_error={} control_kick={}",
                                error.what(), newly_activated_libusb0));
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
                    failure_stage = FailureStage::Decoder;
                    failure_kind = FailureKind::VideoStream;
                    failure_code = -2107;
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
                        // This is the USB receive thread. Never wait for the
                        // decoder here: high-bitrate landscape frames can
                        // arrive faster than a format-switching decoder
                        // drains them, and even a 20ms wait delays NEED/clock
                        // replies enough for some iOS versions to end the
                        // QuickTime stream. The admission policy below drops
                        // safely through the next IDR and resets the decoder.
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
                    const auto media_received_at = std::chrono::steady_clock::now();
                    media_silence_watchdog.observe_media(media_received_at);
                    if (event.video_sample) {
                        video_silence_watchdog.observe_media(media_received_at);
                        if (fast_stream_reconnect_gate.observe_video_frame())
                            logging::write(std::format(
                                "quicktime_fast_reconnect recovered=true attempt={} "
                                "usb_configuration=retained",
                                fast_stream_reconnect_gate.attempt_count()));
                    }
                    // Keep all wired preflight, activation, re-enumeration,
                    // claim and handshake work serialized. A second wired
                    // session may begin only after this one proves a stable
                    // media stream, or after an error path releases the gate.
                    transition_release.run_now();
                    std::scoped_lock lock(mutex_);
                    snapshot_.state = State::Streaming;
                    const bool video_protected = protected_video_detected_.load(
                        std::memory_order_acquire);
                    const auto media_now_ns =
                        std::chrono::duration_cast<std::chrono::nanoseconds>(
                            media_received_at.time_since_epoch()).count();
                    const auto last_audio_ns = last_audio_activity_ns.load(
                        std::memory_order_acquire);
                    const auto audio_age_ns = media_now_ns - last_audio_ns;
                    const bool audio_active = last_audio_ns > 0 &&
                        audio_age_ns >= 0 &&
                        audio_age_ns <= std::chrono::duration_cast<
                            std::chrono::nanoseconds>(std::chrono::seconds(3)).count();
                    snapshot_.message = !video_protected
                        ? L"投屏中"
                        : audio_active
                            ? L"DRM_VIDEO_PROTECTED_AUDIO_ACTIVE"
                            : L"DRM_VIDEO_PROTECTED_AUDIO_INACTIVE";
                    snapshot_.video_frames = protocol.video_frames();
                    snapshot_.audio_packets = protocol.audio_packets();
                    const auto now = media_received_at;
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
                        if (snapshot_.width == 0 || snapshot_.height == 0) {
                            failure_stage = FailureStage::VideoStream;
                            failure_kind = FailureKind::InvalidVideoDimensions;
                            failure_code = -2106;
                            throw std::runtime_error(
                                "QuickTime video stream reported invalid zero dimensions");
                        }
                    }
                    if (protocol.audio_format() && protocol.audio_format()->audio) {
                        if (!video_protected || audio_active) {
                            snapshot_.audio_sample_rate = static_cast<std::uint32_t>(
                                protocol.audio_format()->audio->sample_rate);
                            snapshot_.audio_channels = protocol.audio_format()->audio->channels_per_frame;
                        } else {
                            snapshot_.audio_sample_rate = 0;
                            snapshot_.audio_channels = 0;
                        }
                    } else {
                        snapshot_.audio_sample_rate = 0;
                        snapshot_.audio_channels = 0;
                    }
                }
            }
            request_fast_reconnect_for_missing_frame_rate();
            detect_streaming_media_silence();
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

        // Serialize the whole release/close/restore transition against other
        // sessions starting or stopping. Streaming bulk calls remain protected
        // by the backend's own API lock.
        acquire_usb_transition_gate();
        video_worker.request_stop();
        video_queue_cv.notify_all();
        video_worker.join();
        stop_audio_renderer();
        prepare_shutdown_usb();
        shutdown_usb();
        configuration_restore.run_now();
        const bool restored = finalize_configuration_restore();
        active_backend_release.run_now();
        libusb0_restore_lease.release();
        transition_release.run_now();
        release_usb_transition_gate();
        logging::write("capture_run stop path");
        if (restored) set_state(State::Stopped, L"投屏已停止");
    } catch (const std::exception& error) {
        // Stop requests intentionally interrupt USB I/O while iOS restores
        // its normal configuration. This is a normal terminal condition.
        acquire_usb_transition_gate();
        stop_audio_renderer();
        prepare_shutdown_usb();
        shutdown_usb();
        configuration_restore.run_now();
        const bool stopped_by_request = stop_token.stop_requested() &&
            !video_worker_failure.failed() && !peer_session_ended;
        const bool restored = finalize_configuration_restore(stopped_by_request);
        active_backend_release.run_now();
        libusb0_restore_lease.release();
        transition_release.run_now();
        release_usb_transition_gate();
        logging::write(std::format("capture_run exception stop_requested={} error={}",
            stop_token.stop_requested(), error.what()));
        if (stopped_by_request) {
            if (restored) set_state(State::Stopped, L"投屏已停止");
        } else {
            std::string diagnostic = error.what();
            if (!restored) {
                diagnostic += "; teardown released application resources but "
                    "could not confirm that the Apple USB device returned to "
                    "its normal configuration";
            }
            if (const auto* usb_error =
                    dynamic_cast<const transport::UsbError*>(&error))
                failure_code = usb_error->code();
            // A bulk transfer can fail after the decoder has already accepted
            // a frame. Do not leave the last sample's Decoder stage attached
            // to a transport failure; that misclassifies the legacy filter
            // error and sends the user down the wrong recovery path.
            const bool bulk_transport_failure =
                diagnostic.find("bulk read") != std::string::npos ||
                diagnostic.find("bulk write") != std::string::npos;
            if (bulk_transport_failure) {
                failure_stage = FailureStage::VideoStream;
                failure_kind = FailureKind::Driver;
            }
            if (diagnostic.find("disconnected") != std::string::npos ||
                diagnostic.find("NO_DEVICE") != std::string::npos ||
                diagnostic.find("no device") != std::string::npos) {
                failure_kind = FailureKind::DeviceDisconnected;
            } else if (diagnostic.find("driver") != std::string::npos ||
                diagnostic.find("libusb0") != std::string::npos) {
                failure_kind = FailureKind::Driver;
            }
            set_failure(failure_kind, failure_stage, failure_code,
                L"采集失败：" + widen(diagnostic));
        }
    }
}

} // namespace iPhoneMirror::capture
