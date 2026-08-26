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

constexpr std::uint32_t ApiVersion = 18;
using SessionHandle = std::uint64_t;
constexpr std::size_t MaxUdid = 128;
constexpr std::size_t MaxName = 128;
constexpr std::size_t MaxProductType = 64;
constexpr std::size_t MaxOsVersion = 32;
constexpr std::size_t MaxConnectionType = 32;
constexpr std::size_t MaxStatus = 192;
constexpr std::size_t MaxDiagnostic = 512;
constexpr std::size_t MaxMediaUrl = 16384;

enum class Result : std::int32_t {
    Ok = 0,
    InvalidArgument = -1,
    NotInitialized = -2,
    BufferTooSmall = -3,
    TransportUnavailable = -4,
    ProtocolError = -5,
    DeviceNotFound = -6,
    CaptureBackendUnavailable = -7,
    SessionAlreadyExists = -8,
    DriverSafetyBlocked = -9,
    UsbConfigurationNotReady = -10,
    SessionTeardownFailed = -11,
    UsbConfigurationRestoreWarning = -12,
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
    // Availability/count values are conservative false/zero when the matching
    // known flag is zero. Automatic environment polling deliberately leaves
    // USB kernel backends unprobed.
    std::int32_t usbdk_backend_known;
    std::int32_t libusb_apple_devices_known;
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

enum class CaptureFailureKind : std::int32_t {
    None = 0,
    UsbConnection = 1,
    SessionCreation = 2,
    Driver = 3,
    VideoStream = 4,
    InvalidVideoDimensions = 5,
    NoVideoFrames = 6,
    SystemClosed = 7,
    DeviceDisconnected = 8,
    Timeout = 9,
    ExistingSession = 10,
    ChildProcessExited = 11,
    Unknown = 100,
};

enum class CaptureFailureStage : std::int32_t {
    None = 0,
    UsbPreflight = 1,
    UsbActivation = 2,
    DeviceReenumeration = 3,
    InterfaceOpen = 4,
    QuickTimeHandshake = 5,
    VideoStream = 6,
    Decoder = 7,
    SessionTeardown = 8,
    DeviceDiscovery = 9,
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
    CaptureFailureKind failure_kind;
    CaptureFailureStage failure_stage;
    std::int32_t error_code;
    wchar_t message[MaxStatus];
};

enum class DecoderSwitchState : std::uint32_t {
    Applied = 0,
    Pending = 1,
    Failed = 2,
};

enum class DecoderRuntimeMode : std::uint32_t {
    Unknown = 0,
    Hardware = 1,
    Software = 2,
    External = 3,
};

// Per-preview video diagnostics. Passing a null hwnd returns session-level
// decoder data without renderer diagnostics. With a preview hwnd, renderer
// values describe that preview. Decoder values distinguish a policy request
// from the decoder that has actually committed it on a random-access frame.
struct VideoOutputStatus {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t monitor_hdr_capability; // 0=unknown, 1=SDR, 2=HDR
    std::int32_t source_hdr_known;
    std::int32_t source_hdr;
    std::int32_t actual_hdr_surface;
    // True only for an HDR source on an HDR display with a committed
    // scRGB/FP16 surface; requesting HDR alone is never reported as effective.
    std::int32_t hdr_effective;
    std::uint32_t requested_color_output_preference;
    std::uint32_t requested_decoder_preference;
    std::uint32_t applied_decoder_preference;
    DecoderSwitchState decoder_switch_state;
    DecoderRuntimeMode decoder_runtime_mode;
    std::uint64_t requested_decoder_generation;
    std::uint64_t applied_decoder_generation;
};

struct VideoFrameInfo {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t pixel_format; // 1 = BGRA8, 2 = tightly packed NV12
    std::int64_t timestamp_100ns;
};

struct AudioPacketInfo {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint64_t sequence;
    std::uint32_t sample_rate;
    std::uint16_t channels;
    std::uint16_t bits_per_sample;
};

// Versioned capture preferences used by im_start_capture_with_options.
// requested_width/requested_height are local preview-render limits. The first
// Reserved words 0/1 are the optional advanced USB HPD1 size. Reserved word 2
// selects USB projection mode (0=demo, 1=AirPlay, 2=Aisi-compatible), word 3
// selects decoder policy (0=auto, 1=hardware preferred, 2=software compatible),
// and word 4 selects color output (0=auto, 1=SDR tone-map, 2=prefer HDR).
struct CaptureOptions {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint32_t requested_width;
    std::uint32_t requested_height;
    std::uint32_t target_fps; // Local renderer cap; does not negotiate source FPS.
    std::int32_t play_audio;
    float audio_volume; // Linear gain in the inclusive range [0.0, 1.0].
    std::uint32_t reserved[5];
};

enum class MediaCastCommand : std::uint32_t {
    None = 0,
    Play = 1,
    Stop = 2,
    Pause = 3,
    Resume = 4,
    Seek = 5,
    Volume = 6,
};

enum class MediaCastFlags : std::uint32_t {
    None = 0,
    MuteSpecified = 1,
    Muted = 2,
};

struct MediaCastRequest {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::uint64_t command_id;
    MediaCastCommand command;
    std::uint32_t reserved;
    double duration;
    double start_position;
    double volume;
    wchar_t url[MaxMediaUrl];
};

} // namespace iPhoneMirror

IM_API std::int32_t IM_CALL im_initialize();
IM_API void IM_CALL im_shutdown();
IM_API std::uint32_t IM_CALL im_api_version();
// Persists a single sanitized application/UI diagnostic in the shared log.
// Messages are limited to 4096 UTF-16 code units and may not be empty.
IM_API std::int32_t IM_CALL im_log_message(const wchar_t* message);

// On input, *count is the number of entries available in devices. On output it
// is the number of devices discovered. Passing devices == nullptr is a count query.
IM_API std::int32_t IM_CALL im_refresh_devices(
    iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count);
// Explicit idle-state refreshes may request fresh Lockdown metadata. Normal
// polling only performs usbmux presence discovery and reuses cached metadata.
IM_API std::int32_t IM_CALL im_refresh_devices_ex(
    iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count,
    std::int32_t refresh_metadata);

// The AirPlay receiver is process-global and remains active independently of
// preview sessions. Connected clients are enumerated as ordinary devices.
IM_API std::int32_t IM_CALL im_wireless_receiver_start(
    const wchar_t* receiver_name, const wchar_t* host_path);
IM_API std::int32_t IM_CALL im_wireless_receiver_start_ex(
    const wchar_t* receiver_name, const wchar_t* host_path,
    std::uint32_t width, std::uint32_t height, std::uint32_t frame_rate);
IM_API void IM_CALL im_wireless_receiver_stop();
IM_API std::int32_t IM_CALL im_wireless_receiver_get_status(
    std::int32_t* running, std::int32_t* ready);
IM_API std::int32_t IM_CALL im_refresh_wireless_devices(
    iPhoneMirror::DeviceInfo* devices, std::uint32_t* count);

// Logical URL-video receiver backed by the combined process-global AirPlay
// host. It shares the advertised receiver identity but never contributes
// devices or frames to the screen-mirroring session pipeline.
IM_API std::int32_t IM_CALL im_media_cast_receiver_start(
    const wchar_t* receiver_name, const wchar_t* host_path);
IM_API void IM_CALL im_media_cast_receiver_stop();
IM_API std::int32_t IM_CALL im_media_cast_receiver_get_status(
    std::int32_t* running, std::int32_t* ready);
IM_API std::int32_t IM_CALL im_media_cast_get_request(
    iPhoneMirror::MediaCastRequest* request);
IM_API std::int32_t IM_CALL im_media_cast_set_playback_state(
    std::uint64_t command_id, double duration, double position, double rate);
// Requests that the active receiver-side URL-video transport transition to
// stopped. The UI should still release its local decoder after this succeeds.
IM_API std::int32_t IM_CALL im_media_cast_request_stop();

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
// Applies local preview-only image controls. brightness is [-1, 1], contrast
// and saturation are [0, 2], and gamma is [0.5, 2]. Exported BGRA frames used
// by recording, streaming, screenshots, and virtual cameras are unchanged.
IM_API std::int32_t IM_CALL im_set_image_adjustments(
    float brightness, float contrast, float saturation, float gamma);
IM_API std::int32_t IM_CALL im_set_audio_enabled(std::int32_t enabled);
IM_API std::int32_t IM_CALL im_set_audio_volume(float volume);

// Multi-device API. Each handle owns one independent USB capture session and
// may be attached/detached from an HWND without stopping its background stream.
IM_API std::int32_t IM_CALL im_session_create(
    const wchar_t* udid, const iPhoneMirror::CaptureOptions* options,
    iPhoneMirror::SessionHandle* handle);
// Subscribes to one client of the process-global AirPlay receiver and routes
// that client's decoded YUV/PCM stream into the normal session pipeline.
IM_API std::int32_t IM_CALL im_wireless_session_create(
    const wchar_t* device_id, const iPhoneMirror::CaptureOptions* options,
    iPhoneMirror::SessionHandle* handle);
IM_API std::int32_t IM_CALL im_session_stop(iPhoneMirror::SessionHandle handle);
IM_API void IM_CALL im_session_destroy(iPhoneMirror::SessionHandle handle);
IM_API std::int32_t IM_CALL im_session_get_status(
    iPhoneMirror::SessionHandle handle, iPhoneMirror::CaptureStatus* status);
// hwnd is optional. When it is null, renderer-specific fields are reported as
// their default/unknown values while decoder state is still returned for the
// active capture session.
IM_API std::int32_t IM_CALL im_session_get_video_output_status(
    iPhoneMirror::SessionHandle handle, void* hwnd,
    iPhoneMirror::VideoOutputStatus* status);
IM_API std::int32_t IM_CALL im_session_attach_preview(
    iPhoneMirror::SessionHandle handle, void* hwnd);
IM_API void IM_CALL im_session_detach_preview(iPhoneMirror::SessionHandle handle, void* hwnd);
IM_API std::int32_t IM_CALL im_session_set_video_preferences(
    iPhoneMirror::SessionHandle handle, std::uint32_t max_width,
    std::uint32_t max_height, std::uint32_t max_fps);
IM_API std::int32_t IM_CALL im_session_set_image_adjustments(
    iPhoneMirror::SessionHandle handle, float brightness, float contrast,
    float saturation, float gamma);
// Updates only the active video pipeline. Decoder changes are committed on the
// next random-access frame; color output changes are applied to every attached
// renderer. The USB/AirPlay transport remains connected.
IM_API std::int32_t IM_CALL im_session_set_pipeline_preferences(
    iPhoneMirror::SessionHandle handle, std::uint32_t decoder_preference,
    std::uint32_t color_output_preference);
IM_API std::int32_t IM_CALL im_session_set_audio_enabled(
    iPhoneMirror::SessionHandle handle, std::int32_t enabled);
IM_API std::int32_t IM_CALL im_session_set_audio_volume(
    iPhoneMirror::SessionHandle handle, float volume);
IM_API std::int32_t IM_CALL im_session_set_corner_profile(
    iPhoneMirror::SessionHandle handle, float normalized_radius,
    float curve_exponent);
IM_API std::int32_t IM_CALL im_session_get_latest_video_timestamp(
    iPhoneMirror::SessionHandle handle, std::int64_t* timestamp_100ns);
IM_API std::int32_t IM_CALL im_session_copy_latest_video_frame(
    iPhoneMirror::SessionHandle handle, iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size,
    std::uint32_t max_width, std::uint32_t max_height);
// Copies the latest frame into an exact-size, tightly packed 8-bit NV12 canvas.
// The source is downscaled if needed, aspect ratio is preserved, and unused
// canvas pixels are filled with YUV black. P010 sources are reduced to 8-bit.
IM_API std::int32_t IM_CALL im_session_copy_latest_video_frame_nv12(
    iPhoneMirror::SessionHandle handle, iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size,
    std::uint32_t output_width, std::uint32_t output_height);
// Copies the oldest buffered PCM packet newer than after_sequence. Audio is
// signed little-endian interleaved PCM. Callers retain sequence and request
// the next packet; a slow caller may skip packets evicted by the bounded queue.
IM_API std::int32_t IM_CALL im_session_copy_next_audio_packet(
    iPhoneMirror::SessionHandle handle, std::uint64_t after_sequence,
    iPhoneMirror::AudioPacketInfo* info, std::uint8_t* buffer,
    std::uint32_t* buffer_size);
IM_API std::int32_t IM_CALL im_session_force_preview_refresh(
    iPhoneMirror::SessionHandle handle);
IM_API std::int32_t IM_CALL im_session_set_window_corner_profile(
    iPhoneMirror::SessionHandle handle, void* hwnd, float normalized_radius,
    float curve_exponent);
IM_API std::int32_t IM_CALL im_session_set_window_rotation(
    iPhoneMirror::SessionHandle handle, void* hwnd, std::int32_t quarter_turns);


IM_API const wchar_t* IM_CALL im_last_error();
