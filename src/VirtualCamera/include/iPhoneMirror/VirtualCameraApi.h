#pragma once

#include <cstdint>

#ifdef _WIN32
#  ifdef IPHONEMIRROR_VIRTUAL_CAMERA_EXPORTS
#    define IM_VCAM_API extern "C" __declspec(dllexport)
#  else
#    define IM_VCAM_API extern "C" __declspec(dllimport)
#  endif
#  define IM_VCAM_CALL __cdecl
#else
#  define IM_VCAM_API extern "C"
#  define IM_VCAM_CALL
#endif

namespace iPhoneMirror::virtual_camera {

constexpr std::uint32_t ApiVersion = 2;
constexpr std::uint32_t PixelFormatBgra8 = 1;

struct Status {
    std::uint32_t struct_size;
    std::uint32_t api_version;
    std::int32_t supported;
    std::int32_t registered;
    std::int32_t running;
    std::uint32_t published_width;
    std::uint32_t published_height;
    std::uint64_t published_frames;
    wchar_t message[256];
};

} // namespace iPhoneMirror::virtual_camera

// The control calls must run on a COM-initialized thread. WPF's UI thread is
// already initialized as STA. HRESULT values are returned unchanged so the
// managed layer can preserve actionable Windows diagnostics.
IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_get_status(
    iPhoneMirror::virtual_camera::Status* status);

IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_start(
    const wchar_t* friendly_name);

IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_start_ex(
    const wchar_t* friendly_name,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t frame_rate);

IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_publish_bgra(
    const std::uint8_t* pixels,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    std::int64_t timestamp_100ns);

IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_stop();

// Machine registration is required because Windows Camera Frame Server loads
// the media source in a service process. These calls return access denied when
// the caller is not elevated; the release helper invokes them through UAC.
IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_register_media_source(
    const wchar_t* absolute_dll_path);

IM_VCAM_API std::int32_t IM_VCAM_CALL im_vcam_unregister_media_source();
