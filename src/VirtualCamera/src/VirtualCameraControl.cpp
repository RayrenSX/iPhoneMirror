#include <iPhoneMirror/VirtualCameraApi.h>

#include "FrameExchange.h"
#include "VirtualCameraShared.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <wrl.h>

#include <algorithm>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::virtual_camera {
namespace {

using CreateVirtualCamera = HRESULT (WINAPI*)(
    MFVirtualCameraType, MFVirtualCameraLifetime, MFVirtualCameraAccess,
    LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCamera**);
using IsVirtualCameraTypeSupported = HRESULT (WINAPI*)(MFVirtualCameraType,
                                                       BOOL*);

std::mutex control_mutex;
FramePublisher publisher;
ComPtr<IMFVirtualCamera> virtual_camera;
bool media_foundation_started{};
std::wstring last_message = L"Virtual camera is stopped.";

constexpr wchar_t RegistryClassPath[] =
    L"Software\\Classes\\CLSID\\{4C0D85FD-695A-491D-945B-21DDF7EEC1E2}";
constexpr wchar_t RegistryPath[] =
    L"Software\\Classes\\CLSID\\{4C0D85FD-695A-491D-945B-21DDF7EEC1E2}"
    L"\\InprocServer32";

HRESULT last_win32_error(LSTATUS error) noexcept {
    return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE
                                                     : static_cast<DWORD>(error));
}

HMODULE sensor_group_module() noexcept {
    static HMODULE module = LoadLibraryExW(
        L"mfsensorgroup.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    return module;
}

template <typename Function>
Function sensor_group_function(const char* name) noexcept {
    HMODULE module = sensor_group_module();
    return module == nullptr ? nullptr
                             : reinterpret_cast<Function>(GetProcAddress(module, name));
}

HRESULT query_support(bool& supported) noexcept {
    supported = false;
    const auto function = sensor_group_function<IsVirtualCameraTypeSupported>(
        "MFIsVirtualCameraTypeSupported");
    if (function == nullptr)
        return HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION);
    BOOL value{};
    const HRESULT hr = function(MFVirtualCameraType_SoftwareCameraSource,
                                &value);
    if (SUCCEEDED(hr)) supported = value != FALSE;
    return hr;
}

HRESULT query_registration(bool& registered, std::wstring* path = nullptr) {
    registered = false;
    std::vector<wchar_t> value(32768);
    DWORD bytes = static_cast<DWORD>(value.size() * sizeof(wchar_t));
    const LSTATUS result = RegGetValueW(
        HKEY_LOCAL_MACHINE, RegistryPath, nullptr,
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, value.data(), &bytes);
    if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND)
        return S_OK;
    if (result != ERROR_SUCCESS) return last_win32_error(result);
    if (bytes < sizeof(wchar_t) || bytes % sizeof(wchar_t) != 0 ||
        bytes > value.size() * sizeof(wchar_t))
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    const auto characters = bytes / sizeof(wchar_t);
    const auto length = wcsnlen_s(value.data(), characters);
    if (length == characters) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    registered = length != 0;
    if (path != nullptr) path->assign(value.data(), length);
    return S_OK;
}

void set_message(std::wstring message) { last_message = std::move(message); }

HRESULT stop_locked() noexcept {
    HRESULT first_failure = S_OK;
    if (virtual_camera != nullptr) {
        HRESULT hr = virtual_camera->Stop();
        if (FAILED(hr) && SUCCEEDED(first_failure)) first_failure = hr;
        hr = virtual_camera->Remove();
        if (FAILED(hr) && SUCCEEDED(first_failure)) first_failure = hr;
        hr = virtual_camera->Shutdown();
        if (FAILED(hr) && SUCCEEDED(first_failure)) first_failure = hr;
        virtual_camera.Reset();
    }
    publisher.close();
    if (media_foundation_started) {
        const HRESULT hr = MFShutdown();
        if (FAILED(hr) && SUCCEEDED(first_failure)) first_failure = hr;
        media_foundation_started = false;
    }
    set_message(SUCCEEDED(first_failure) ? L"Virtual camera is stopped."
                                         : L"Virtual camera stop failed.");
    return first_failure;
}

HRESULT normalized_dll_path(const wchar_t* path, std::wstring& normalized) {
    if (path == nullptr || *path == L'\0') return E_INVALIDARG;
    try {
        std::filesystem::path candidate(path);
        if (!candidate.is_absolute()) return E_INVALIDARG;
        candidate = std::filesystem::weakly_canonical(candidate);
        if (!std::filesystem::is_regular_file(candidate))
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        if (_wcsicmp(candidate.extension().c_str(), L".dll") != 0)
            return E_INVALIDARG;
        normalized = candidate.native();
        return S_OK;
    } catch (...) {
        return E_INVALIDARG;
    }
}

struct RegistryKeyCloser {
    void operator()(HKEY key) const noexcept {
        if (key != nullptr) RegCloseKey(key);
    }
};

} // namespace
} // namespace iPhoneMirror::virtual_camera

std::int32_t IM_VCAM_CALL im_vcam_get_status(
    iPhoneMirror::virtual_camera::Status* status) {
    using namespace iPhoneMirror::virtual_camera;
    if (status == nullptr || status->struct_size < sizeof(Status))
        return E_INVALIDARG;
    std::lock_guard lock(control_mutex);
    const std::uint32_t struct_size = status->struct_size;
    *status = {};
    status->struct_size = struct_size;
    status->api_version = ApiVersion;

    bool supported{};
    const HRESULT support_hr = query_support(supported);
    status->supported = supported ? 1 : 0;
    bool registered{};
    const HRESULT registration_hr = query_registration(registered);
    status->registered = registered ? 1 : 0;
    status->running = virtual_camera != nullptr ? 1 : 0;
    status->published_width = publisher.published_width();
    status->published_height = publisher.published_height();
    status->published_frames = publisher.published_frames();
    wcsncpy_s(status->message, last_message.c_str(), _TRUNCATE);
    if (FAILED(support_hr) && support_hr != HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION))
        return support_hr;
    return registration_hr;
}

std::int32_t IM_VCAM_CALL im_vcam_start(const wchar_t* friendly_name) {
    return im_vcam_start_ex(friendly_name, 1280, 720, 30);
}

std::int32_t IM_VCAM_CALL im_vcam_start_ex(
    const wchar_t* friendly_name, std::uint32_t width,
    std::uint32_t height, std::uint32_t frame_rate) {
    using namespace iPhoneMirror::virtual_camera;
    if (width < 160 || width > MaximumFrameWidth ||
        height < 160 || height > MaximumFrameHeight ||
        (width & 1U) != 0 || (height & 1U) != 0 ||
        frame_rate < 10 || frame_rate > 60)
        return E_INVALIDARG;
    std::lock_guard lock(control_mutex);
    if (virtual_camera != nullptr) return S_FALSE;

    bool supported{};
    HRESULT hr = query_support(supported);
    if (FAILED(hr)) {
        set_message(L"Windows virtual camera APIs are unavailable.");
        return hr;
    }
    if (!supported) {
        set_message(L"This Windows installation does not support software virtual cameras.");
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    }
    bool registered{};
    if (FAILED(hr = query_registration(registered))) return hr;
    if (!registered) {
        set_message(L"The virtual camera media source is not installed.");
        return REGDB_E_CLASSNOTREG;
    }
    if (FAILED(hr = publisher.open_for_current_user())) {
        set_message(L"Could not create the cross-process frame buffer.");
        return hr;
    }

    if (FAILED(hr = MFStartup(MF_VERSION, MFSTARTUP_FULL))) {
        publisher.close();
        set_message(L"Media Foundation could not be started.");
        return hr;
    }
    media_foundation_started = true;

    const auto create = sensor_group_function<CreateVirtualCamera>(
        "MFCreateVirtualCamera");
    if (create == nullptr) {
        stop_locked();
        return HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION);
    }
    const wchar_t* name = friendly_name == nullptr || *friendly_name == L'\0'
        ? DefaultFriendlyName : friendly_name;
    ComPtr<IMFVirtualCamera> camera;
    hr = create(MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime_Session,
                MFVirtualCameraAccess_CurrentUser, name,
                MediaSourceClsidString, nullptr, 0, &camera);
    if (SUCCEEDED(hr)) {
        hr = camera->SetString(FrameChannelPathAttribute,
                               publisher.channel_path().c_str());
    }
    if (SUCCEEDED(hr)) hr = camera->SetUINT32(OutputWidthAttribute, width);
    if (SUCCEEDED(hr)) hr = camera->SetUINT32(OutputHeightAttribute, height);
    if (SUCCEEDED(hr))
        hr = camera->SetUINT32(OutputFrameRateAttribute, frame_rate);
    if (SUCCEEDED(hr)) hr = camera->Start(nullptr);
    if (FAILED(hr)) {
        if (camera != nullptr) camera->Shutdown();
        stop_locked();
        set_message(L"Windows could not register the virtual camera session.");
        return hr;
    }
    virtual_camera = std::move(camera);
    set_message(L"Virtual camera is running.");
    return S_OK;
}

std::int32_t IM_VCAM_CALL im_vcam_publish_bgra(
    const std::uint8_t* pixels, std::uint32_t width, std::uint32_t height,
    std::uint32_t stride, std::int64_t timestamp_100ns) {
    using namespace iPhoneMirror::virtual_camera;
    std::lock_guard lock(control_mutex);
    if (virtual_camera == nullptr) return CO_E_NOTINITIALIZED;
    return publisher.publish(pixels, width, height, stride, timestamp_100ns);
}

std::int32_t IM_VCAM_CALL im_vcam_stop() {
    using namespace iPhoneMirror::virtual_camera;
    std::lock_guard lock(control_mutex);
    return stop_locked();
}

std::int32_t IM_VCAM_CALL im_vcam_register_media_source(
    const wchar_t* absolute_dll_path) {
    using namespace iPhoneMirror::virtual_camera;
    std::lock_guard lock(control_mutex);
    std::wstring path;
    HRESULT hr = normalized_dll_path(absolute_dll_path, path);
    if (FAILED(hr)) return hr;

    HKEY raw_class_key{};
    LSTATUS created = RegCreateKeyExW(
        HKEY_LOCAL_MACHINE, RegistryClassPath, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_SET_VALUE | KEY_WOW64_64KEY,
        nullptr, &raw_class_key, nullptr);
    if (created != ERROR_SUCCESS) return last_win32_error(created);
    const std::unique_ptr<std::remove_pointer_t<HKEY>, RegistryKeyCloser>
        close_class_key(raw_class_key);

    constexpr wchar_t friendly_name[] = L"iPhoneMirror Virtual Camera Media Source";
    LSTATUS result = RegSetValueExW(
        raw_class_key, nullptr, 0, REG_SZ,
        reinterpret_cast<const BYTE*>(friendly_name), sizeof(friendly_name));
    HKEY raw_server_key{};
    if (result == ERROR_SUCCESS) {
        created = RegCreateKeyExW(
            raw_class_key, L"InprocServer32", 0, nullptr,
            REG_OPTION_NON_VOLATILE, KEY_SET_VALUE | KEY_WOW64_64KEY,
            nullptr, &raw_server_key, nullptr);
        result = created;
    }
    const std::unique_ptr<std::remove_pointer_t<HKEY>, RegistryKeyCloser>
        close_server_key(raw_server_key);
    const DWORD path_bytes = static_cast<DWORD>((path.size() + 1U) * sizeof(wchar_t));
    if (result == ERROR_SUCCESS)
        result = RegSetValueExW(
            raw_server_key, nullptr, 0, REG_SZ,
            reinterpret_cast<const BYTE*>(path.c_str()), path_bytes);
    if (result == ERROR_SUCCESS) {
        constexpr wchar_t threading_model[] = L"Both";
        result = RegSetValueExW(
            raw_server_key, L"ThreadingModel", 0, REG_SZ,
            reinterpret_cast<const BYTE*>(threading_model),
            sizeof(threading_model));
    }
    if (result != ERROR_SUCCESS) {
        RegDeleteTreeW(HKEY_LOCAL_MACHINE,
                       RegistryClassPath);
        return last_win32_error(result);
    }
    set_message(L"Virtual camera media source is installed.");
    return S_OK;
}

std::int32_t IM_VCAM_CALL im_vcam_unregister_media_source() {
    using namespace iPhoneMirror::virtual_camera;
    std::lock_guard lock(control_mutex);
    const HRESULT stop_hr = stop_locked();
    const LSTATUS result = RegDeleteTreeW(
        HKEY_LOCAL_MACHINE, RegistryClassPath);
    if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND &&
        result != ERROR_PATH_NOT_FOUND)
        return last_win32_error(result);
    set_message(L"Virtual camera media source is uninstalled.");
    return stop_hr;
}
