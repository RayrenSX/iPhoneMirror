#pragma once

#include <cstddef>
#include <cstdint>

#ifdef _WIN32
#  ifdef IPHONEMIRROR_CORE_EXPORTS
#    define IM_API extern "C" __declspec(dllexport)
#  else
#    define IM_API extern "C" __declspec(dllimport)
#  endif
#  define IM_CALL __cdecl
#else
#  define IM_API extern "C"
#  define IM_CALL
#endif

namespace iPhoneMirror {

constexpr std::uint32_t ApiVersion = 8;
constexpr std::size_t MaxUdid = 128;
constexpr std::size_t MaxName = 128;
constexpr std::size_t MaxProductType = 64;
constexpr std::size_t MaxOsVersion = 32;
constexpr std::size_t MaxConnectionType = 32;
constexpr std::size_t MaxStatus = 192;
constexpr std::size_t MaxDiagnostic = 512;

enum class Result : std::int32_t {
    Ok = 0,
    InvalidArgument = -1,
    NotInitialized = -2,
    BufferTooSmall = -3,
    TransportUnavailable = -4,
    ProtocolError = -5,
    DeviceNotFound = -6,
    CaptureBackendUnavailable = -7,
    InternalError = -100,
};

enum class ConnectionState : std::int32_t {
    Disconnected = 0,
    UsbPresentNoMux = 1,
    Connected = 2,
    Paired = 3,
    Ready = 4,
    Error = 5,
};

struct DeviceInfo {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t device_id;
    std::uint32_t mux_port;
    ConnectionState state;
    std::int32_t usb_connected;
    std::int32_t pair_record_present;
    std::int32_t lockdown_accessible;
    wchar_t udid[MaxUdid];
    wchar_t name[MaxName];
    wchar_t product_type[MaxProductType];
    wchar_t os_version[MaxOsVersion];
    wchar_t connection_type[MaxConnectionType];
    wchar_t status[MaxStatus];
};

struct EnvironmentInfo {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::int32_t apple_mobile_device_service_installed;
    std::int32_t apple_mobile_device_service_running;
    std::int32_t standard_usbmux_available;
    std::int32_t capture_usbmux_available;
    std::uint32_t physical_apple_usb_devices;
    wchar_t diagnostic[MaxDiagnostic];
    std::int32_t libusb_runtime_available;
    std::int32_t usbdk_backend_available;
    std::uint32_t libusb_apple_devices;
    wchar_t libusb_version[32];
};

enum class CaptureState : std::int32_t {
    Idle = 0,
    ActivatingUsb = 1,
    WaitingForDevice = 2,
    Handshaking = 3,
    Streaming = 4,
    Stopping = 5,
    Stopped = 6,
    Error = 7,
};

struct CaptureStatus {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    CaptureState state;
    std::uint32_t width;
    std::uint32_t height;
    double fps;
    double latency_ms;
    std::uint64_t video_frames;
    std::uint64_t audio_packets;
    std::uint32_t audio_sample_rate;
    std::uint32_t audio_channels;
    wchar_t message[MaxStatus];
};

struct VideoFrameInfo {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t pixel_format; // 1 = BGRA8
    std::int64_t timestamp_100ns;
};

// Versioned capture preferences used by im_start_capture_with_options.
// requested_width/requested_height are retained as ABI field names, but they
// are LOCAL preview-render limits: they never change HPD1 DisplaySize or the
// H.264 stream sent over USB.  The pair (0,0) keeps the decoded/native size;
// target_fps == 0 disables the local presentation cap.
struct CaptureOptions {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t requested_width;
    std::uint32_t requested_height;
    std::uint32_t target_fps;
    std::int32_t play_audio;
    float audio_volume; // Linear gain in the inclusive range [0.0, 1.0].
    std::uint32_t reserved[5];
};

} // namespace iPhoneMirror

IM_API std::int32_t IM_CALL im_initialize();
IM_API void IM_CALL im_shutdown();
IM_API std::uint32_t IM_CALL im_api_version();

// On input, *count is the number of entries available in devices. On output it
// is the number of devices discovered. Passing devices == nullptr is a count query.
IM_API std::int32_t IM_CALL im_refresh_devices(
    iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count);

IM_API std::int32_t IM_CALL im_get_environment(
    iPhoneMirror::EnvironmentInfo* environment);

// Performs a read-only, exact-serial probe through the libusb0 filter backend.
// No USB configuration, interface, endpoint, or driver state is changed.
// `available` receives 1 only when the selected iPhone is enumerated and its
// USB descriptor can be opened; a missing/not-yet-attached filter returns Ok
// with `available == 0`.
IM_API std::int32_t IM_CALL im_is_libusb0_device_available(
    const wchar_t* udid,
    std::int32_t* available);

// Capture is deliberately exposed now so the GUI/API remains stable while the
// USB endpoint backend is completed. It never reports success without a real stream.
IM_API std::int32_t IM_CALL im_start_capture(const wchar_t* udid);
// Extended start entry point. play_audio is a C ABI boolean (0 = disabled,
// nonzero = render the captured system PCM to the default Windows endpoint).
IM_API std::int32_t IM_CALL im_start_capture_ex(const wchar_t* udid, std::int32_t play_audio);
// Preferred extensible start entry point. The options structure must have its
// struct_size set to sizeof(CaptureOptions). Existing start entry points remain
// ABI-compatible and use the preferences last supplied through
// im_set_video_preferences plus their historical audio defaults.
IM_API std::int32_t IM_CALL im_start_capture_with_options(
    const wchar_t* udid,
    const iPhoneMirror::CaptureOptions* options);
IM_API std::int32_t IM_CALL im_stop_capture();
IM_API std::int32_t IM_CALL im_get_capture_status(iPhoneMirror::CaptureStatus* status);
// Returns the timestamp of the newest decoded frame without copying pixels.
// A value of zero means that no decoded frame is available yet.
IM_API std::int32_t IM_CALL im_get_latest_video_timestamp(std::int64_t* timestamp_100ns);
// Copies the latest decoded frame as tightly packed BGRA8. On input,
// *buffer_size is the capacity; on output it is the required byte count.
IM_API std::int32_t IM_CALL im_copy_latest_video_frame(
    iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer,
    std::uint32_t* buffer_size);
// GUI preview variant. The decoded frame is scaled down to fit within the
// requested bounds before BGRA conversion; the aspect ratio is preserved.
IM_API std::int32_t IM_CALL im_copy_latest_video_frame_scaled(
    iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer,
    std::uint32_t* buffer_size,
    std::uint32_t max_width,
    std::uint32_t max_height);

// Attaches the decoded-frame preview to a child HWND. Rendering happens on a
// native D3D11 thread and consumes NV12 directly, avoiding per-frame WPF BGRA
// uploads. Passing an invalid HWND returns InvalidArgument.
IM_API std::int32_t IM_CALL im_attach_preview_window(void* hwnd);
IM_API void IM_CALL im_detach_preview_window();
// Re-presents the newest decoded frame without rebuilding the swap chain.
// This is useful after a display-mode/layout change or an occluded window is
// restored. It returns CaptureBackendUnavailable when no preview is attached.
IM_API std::int32_t IM_CALL im_force_preview_refresh();
// Sets the display-outline fit used by a borderless top-level preview.
// normalized_radius is relative to the short edge (0 disables clipping).
// curve_exponent controls the continuous superellipse and must be [1.5, 8].
// The setting is retained across preview-window reattachment.
IM_API std::int32_t IM_CALL im_set_preview_corner_profile(
    float normalized_radius,
    float curve_exponent);

// Controls may be changed while capture is active. Audio changes take effect
// on the next WASAPI render buffer. max_width/max_height and max_fps are local
// renderer limits and take effect without stopping or renegotiating the USB
// stream. (0,0) preserves the decoded/native resolution and max_fps == 0
// disables the presentation cap. The size limit preserves aspect ratio and is
// interpreted orientation-independently (the larger value caps the long edge).
IM_API std::int32_t IM_CALL im_set_video_preferences(
    std::uint32_t max_width,
    std::uint32_t max_height,
    std::uint32_t max_fps);
IM_API std::int32_t IM_CALL im_set_audio_enabled(std::int32_t enabled);
IM_API std::int32_t IM_CALL im_set_audio_volume(float volume);


IM_API const wchar_t* IM_CALL im_last_error();
