#include "iPhoneMirror/CoreApi.h"

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace {

using namespace std::chrono_literals;

struct Session {
    std::wstring name;
    std::wstring udid;
    iPhoneMirror::SessionHandle handle{};
    HWND preview_window{};
    std::uint64_t frames{};
    std::int64_t decoded_timestamp{};
};

struct Step {
    std::uint32_t decoder;
    std::uint32_t color;
    const wchar_t* name;
};

struct DecoderObservation {
    std::uint32_t requested{};
    std::uint32_t applied{};
    iPhoneMirror::DecoderSwitchState state{};
    iPhoneMirror::DecoderRuntimeMode runtime{};
    std::uint64_t requested_generation{};
    std::uint64_t applied_generation{};
    std::uint32_t color{};

    bool operator==(const DecoderObservation&) const noexcept = default;
};

std::wstring last_error() {
    const auto* value = im_last_error();
    return value ? value : L"unknown native error";
}

bool read_status(Session& session, iPhoneMirror::CaptureStatus& status) {
    status = {};
    status.struct_size = sizeof(status);
    if (im_session_get_status(session.handle, &status) != 0) {
        std::wcerr << L"status failed for " << session.name << L": " << last_error() << L'\n';
        return false;
    }
    return true;
}

const wchar_t* failure_kind_name(
    iPhoneMirror::CaptureFailureKind kind) noexcept {
    switch (kind) {
    case iPhoneMirror::CaptureFailureKind::None: return L"none";
    case iPhoneMirror::CaptureFailureKind::UsbConnection: return L"usb-connection";
    case iPhoneMirror::CaptureFailureKind::SessionCreation: return L"session-creation";
    case iPhoneMirror::CaptureFailureKind::Driver: return L"driver";
    case iPhoneMirror::CaptureFailureKind::VideoStream: return L"video-stream";
    case iPhoneMirror::CaptureFailureKind::InvalidVideoDimensions: return L"invalid-video-dimensions";
    case iPhoneMirror::CaptureFailureKind::NoVideoFrames: return L"no-video-frames";
    case iPhoneMirror::CaptureFailureKind::SystemClosed: return L"system-closed";
    case iPhoneMirror::CaptureFailureKind::DeviceDisconnected: return L"device-disconnected";
    case iPhoneMirror::CaptureFailureKind::Timeout: return L"timeout";
    case iPhoneMirror::CaptureFailureKind::ExistingSession: return L"existing-session";
    case iPhoneMirror::CaptureFailureKind::ChildProcessExited: return L"child-process-exited";
    case iPhoneMirror::CaptureFailureKind::Unknown: return L"unknown";
    }
    return L"invalid";
}

const wchar_t* failure_stage_name(
    iPhoneMirror::CaptureFailureStage stage) noexcept {
    switch (stage) {
    case iPhoneMirror::CaptureFailureStage::None: return L"none";
    case iPhoneMirror::CaptureFailureStage::UsbPreflight: return L"usb-preflight";
    case iPhoneMirror::CaptureFailureStage::UsbActivation: return L"usb-activation";
    case iPhoneMirror::CaptureFailureStage::DeviceReenumeration: return L"device-reenumeration";
    case iPhoneMirror::CaptureFailureStage::InterfaceOpen: return L"interface-open";
    case iPhoneMirror::CaptureFailureStage::QuickTimeHandshake: return L"quicktime-handshake";
    case iPhoneMirror::CaptureFailureStage::VideoStream: return L"video-stream";
    case iPhoneMirror::CaptureFailureStage::Decoder: return L"decoder";
    case iPhoneMirror::CaptureFailureStage::SessionTeardown: return L"session-teardown";
    case iPhoneMirror::CaptureFailureStage::DeviceDiscovery: return L"device-discovery";
    }
    return L"invalid";
}

void print_capture_failure(const Session& session,
    const iPhoneMirror::CaptureStatus& status) {
    std::wcerr << L"capture_failure device=\"" << session.name
               << L"\" kind=" << failure_kind_name(status.failure_kind)
               << L" stage=" << failure_stage_name(status.failure_stage)
               << L" code=" << status.error_code
               << L" state=" << static_cast<int>(status.state)
               << L" message=\"" << status.message << L"\"\n";
}

const wchar_t* decoder_name(std::uint32_t decoder) noexcept {
    switch (decoder) {
    case 0: return L"auto";
    case 1: return L"hardware-preferred";
    case 2: return L"software-compatible";
    default: return L"invalid";
    }
}

const wchar_t* switch_state_name(iPhoneMirror::DecoderSwitchState state) noexcept {
    switch (state) {
    case iPhoneMirror::DecoderSwitchState::Applied: return L"applied";
    case iPhoneMirror::DecoderSwitchState::Pending: return L"pending";
    case iPhoneMirror::DecoderSwitchState::Failed: return L"failed";
    }
    return L"invalid";
}

const wchar_t* runtime_name(iPhoneMirror::DecoderRuntimeMode runtime) noexcept {
    switch (runtime) {
    case iPhoneMirror::DecoderRuntimeMode::Unknown: return L"unknown";
    case iPhoneMirror::DecoderRuntimeMode::Hardware: return L"hardware";
    case iPhoneMirror::DecoderRuntimeMode::Software: return L"software";
    case iPhoneMirror::DecoderRuntimeMode::External: return L"external";
    }
    return L"invalid";
}

DecoderObservation observe(const iPhoneMirror::VideoOutputStatus& status) noexcept {
    return {
        status.requested_decoder_preference,
        status.applied_decoder_preference,
        status.decoder_switch_state,
        status.decoder_runtime_mode,
        status.requested_decoder_generation,
        status.applied_decoder_generation,
        status.requested_color_output_preference,
    };
}

void pump_window_messages() noexcept {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        if (message.message == WM_QUIT) continue;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

bool attach_probe_preview(Session& session) {
    session.preview_window = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        L"STATIC", session.name.c_str(), WS_POPUP | WS_DISABLED,
        0, 0, 320, 180, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!session.preview_window) {
        std::wcerr << L"preview window creation failed for " << session.name
                   << L" win32_error=" << GetLastError() << L'\n';
        return false;
    }
    if (im_session_attach_preview(session.handle, session.preview_window) != 0) {
        std::wcerr << L"preview attach failed for " << session.name << L": "
                   << last_error() << L'\n';
        DestroyWindow(session.preview_window);
        session.preview_window = nullptr;
        return false;
    }
    return true;
}

bool read_output_status(Session& session, iPhoneMirror::VideoOutputStatus& status) {
    status = {};
    status.struct_size = sizeof(status);
    if (im_session_get_video_output_status(
            session.handle, session.preview_window, &status) != 0) {
        std::wcerr << L"video output status failed for " << session.name << L": "
                   << last_error() << L'\n';
        return false;
    }
    return true;
}

void print_decoder_status(const Session& session, const wchar_t* step,
    const iPhoneMirror::VideoOutputStatus& status) {
    std::wcout << L"decoder_status device=\"" << session.name << L"\" step="
               << step << L" requested="
               << decoder_name(status.requested_decoder_preference)
               << L" applied=" << decoder_name(status.applied_decoder_preference)
               << L" state=" << switch_state_name(status.decoder_switch_state)
               << L" runtime=" << runtime_name(status.decoder_runtime_mode)
               << L" requested_generation=" << status.requested_decoder_generation
               << L" applied_generation=" << status.applied_decoder_generation
               << L" color=" << status.requested_color_output_preference << L'\n';
}

bool wait_for_decoder_commit(Session& session, const Step& step,
    const iPhoneMirror::VideoOutputStatus& before,
    std::chrono::seconds timeout,
    iPhoneMirror::VideoOutputStatus* committed = nullptr) {
    const bool policy_changed =
        before.requested_decoder_preference != step.decoder;
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    std::optional<DecoderObservation> last_observation;
    iPhoneMirror::VideoOutputStatus status{};
    while (std::chrono::steady_clock::now() < deadline) {
        pump_window_messages();
        if (!read_output_status(session, status)) return false;
        const auto observation = observe(status);
        if (!last_observation || observation != *last_observation) {
            print_decoder_status(session, step.name, status);
            last_observation = observation;
        }

        if (status.decoder_switch_state > iPhoneMirror::DecoderSwitchState::Failed ||
            status.decoder_runtime_mode > iPhoneMirror::DecoderRuntimeMode::External) {
            std::wcerr << L"invalid decoder status enum for " << session.name << L'\n';
            return false;
        }
        if (status.requested_decoder_preference != step.decoder ||
            status.requested_color_output_preference != step.color) {
            std::this_thread::sleep_for(100ms);
            continue;
        }

        const bool generation_is_expected = policy_changed
            ? status.requested_decoder_generation > before.requested_decoder_generation
            : status.requested_decoder_generation == before.requested_decoder_generation;
        if (!generation_is_expected) {
            std::this_thread::sleep_for(100ms);
            continue;
        }

        if (status.decoder_switch_state == iPhoneMirror::DecoderSwitchState::Failed) {
            std::wcerr << L"decoder switch rejected for " << session.name
                       << L" requested=" << decoder_name(step.decoder)
                       << L" generation=" << status.requested_decoder_generation
                       << L" retained="
                       << decoder_name(status.applied_decoder_preference)
                       << L" retained_generation="
                       << status.applied_decoder_generation << L'\n';
            return false;
        }
        if (status.decoder_switch_state != iPhoneMirror::DecoderSwitchState::Applied) {
            std::this_thread::sleep_for(100ms);
            continue;
        }
        if (status.applied_decoder_preference != step.decoder ||
            status.applied_decoder_generation != status.requested_decoder_generation) {
            std::wcerr << L"inconsistent applied decoder status for " << session.name
                       << L" requested=" << decoder_name(step.decoder)
                       << L" applied="
                       << decoder_name(status.applied_decoder_preference) << L'\n';
            return false;
        }
        if (step.decoder == 2) {
            if (status.decoder_runtime_mode ==
                iPhoneMirror::DecoderRuntimeMode::Unknown) {
                std::this_thread::sleep_for(100ms);
                continue;
            }
            if (status.decoder_runtime_mode !=
                iPhoneMirror::DecoderRuntimeMode::Software) {
                std::wcerr
                    << L"software-compatible policy used a non-software decoder for "
                    << session.name << L" runtime="
                    << runtime_name(status.decoder_runtime_mode) << L'\n';
                return false;
            }
        }

        std::wcout << L"decoder_verified device=\"" << session.name
                   << L"\" step=" << step.name << L" policy="
                   << decoder_name(step.decoder) << L" runtime="
                   << runtime_name(status.decoder_runtime_mode)
                   << L" generation=" << status.applied_decoder_generation
                   << L" new_generation=" << (policy_changed ? L"true" : L"false")
                   << L'\n';
        if (committed) *committed = status;
        return true;
    }

    std::wcerr << L"decoder commit timeout for " << session.name
               << L" step=" << step.name << L" expected="
               << decoder_name(step.decoder) << L" baseline_generation="
               << before.requested_decoder_generation << L'\n';
    print_decoder_status(session, step.name, status);
    return false;
}

bool wait_for_streaming(Session& session, std::chrono::seconds timeout,
    std::uint64_t minimum_decoded_advances = 1) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    iPhoneMirror::CaptureStatus status{};
    auto latest_timestamp = session.decoded_timestamp;
    std::uint64_t decoded_advances{};
    while (std::chrono::steady_clock::now() < deadline) {
        pump_window_messages();
        if (!read_status(session, status)) return false;
        std::int64_t timestamp{};
        if (im_session_get_latest_video_timestamp(session.handle, &timestamp) != 0) {
            std::wcerr << L"timestamp failed for " << session.name << L": "
                       << last_error() << L'\n';
            return false;
        }
        if (timestamp != 0 && timestamp != latest_timestamp) {
            latest_timestamp = timestamp;
            ++decoded_advances;
        }
        if (status.state == iPhoneMirror::CaptureState::Error ||
            status.state == iPhoneMirror::CaptureState::Stopped) {
            print_capture_failure(session, status);
            return false;
        }
        if (status.state == iPhoneMirror::CaptureState::Streaming &&
            decoded_advances >= minimum_decoded_advances) {
            session.frames = status.video_frames;
            session.decoded_timestamp = latest_timestamp;
            std::wcout << L"streaming " << session.name << L" handle=" << session.handle
                       << L" frames=" << status.video_frames << L" size="
                       << status.width << L'x' << status.height << L" fps="
                       << status.fps << L" decoded_advances=" << decoded_advances
                       << L" timestamp=" << latest_timestamp << L'\n';
            return true;
        }
        std::this_thread::sleep_for(100ms);
    }
    std::wcerr << L"streaming timeout for " << session.name << L" handle="
               << session.handle << L" state=" << static_cast<int>(status.state)
               << L" frames=" << status.video_frames << L" message="
               << status.message << L" decoded_advances=" << decoded_advances
               << L" timestamp=" << latest_timestamp << L'\n';
    print_capture_failure(session, status);
    return false;
}

void close_sessions(std::vector<Session>& sessions) noexcept {
    for (auto& session : sessions) {
        if (session.preview_window) {
            if (session.handle != 0)
                im_session_detach_preview(session.handle, session.preview_window);
            DestroyWindow(session.preview_window);
            session.preview_window = nullptr;
        }
        if (session.handle == 0) continue;
        (void)im_session_stop(session.handle);
        im_session_destroy(session.handle);
        session.handle = 0;
    }
}

bool stop_and_destroy(Session& session) noexcept {
    if (session.preview_window) {
        if (session.handle != 0)
            im_session_detach_preview(session.handle, session.preview_window);
        DestroyWindow(session.preview_window);
        session.preview_window = nullptr;
    }
    if (session.handle == 0) return true;
    const auto handle = session.handle;
    const int stop_result = im_session_stop(handle);
    if (stop_result != 0) {
        std::wcerr << L"stop failed for " << session.name << L" handle="
                   << handle << L": " << last_error() << L'\n';
    }
    im_session_destroy(handle);
    session.handle = 0;
    return stop_result == 0;
}

bool create_session(Session& session) {
    iPhoneMirror::CaptureOptions options{};
    options.struct_size = sizeof(options);
    options.api_version = iPhoneMirror::ApiVersion;
    options.target_fps = 60;
    options.play_audio = 0;
    options.audio_volume = 0.0F;
    if (im_session_create(session.udid.c_str(), &options, &session.handle) != 0) {
        std::wcerr << L"create failed for " << session.name << L": "
                   << last_error() << L'\n';
        return false;
    }
    if (attach_probe_preview(session)) return true;
    im_session_stop(session.handle);
    im_session_destroy(session.handle);
    session.handle = 0;
    return false;
}

bool device_is_discoverable(std::wstring_view udid) {
    std::uint32_t count{};
    if (im_refresh_devices(nullptr, &count) != 0 || count == 0) return false;
    std::vector<iPhoneMirror::DeviceInfo> devices(count);
    auto capacity = count;
    if (im_refresh_devices(devices.data(), &capacity) != 0) return false;
    return std::ranges::any_of(devices, [udid](const auto& device) {
        return device.usb_connected != 0 && device.udid == udid;
    });
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::optional<std::size_t> requested_device;
    bool lifecycle_probe{};
    int argument_index = 1;
    if (argc > 1 && std::wstring_view(argv[1]) == L"--lifecycle") {
        lifecycle_probe = true;
        argument_index = 2;
    }
    if (argument_index < argc && argument_index + 1 == argc) {
        try {
            const auto parsed = std::stoul(argv[argument_index]);
            if (parsed == 0) throw std::invalid_argument("device index is one-based");
            requested_device = parsed;
        } catch (...) {
            std::wcerr << L"usage: iPhoneMirror.PipelineSwitchProbe [--lifecycle] [wired-device-index]\n";
            return 2;
        }
    } else if (argument_index != argc) {
        std::wcerr << L"usage: iPhoneMirror.PipelineSwitchProbe [--lifecycle] [wired-device-index]\n";
        return 2;
    }
    if (lifecycle_probe && !requested_device) requested_device = 1;
    if (im_initialize() != 0) {
        std::wcerr << L"initialize failed: " << last_error() << L'\n';
        return 1;
    }

    std::vector<Session> sessions;
    int exit_code = 1;
    try {
        std::uint32_t count{};
        if (im_refresh_devices(nullptr, &count) != 0 || count == 0)
            throw std::runtime_error("no Apple devices were discovered");
        std::vector<iPhoneMirror::DeviceInfo> devices(count);
        auto capacity = count;
        if (im_refresh_devices(devices.data(), &capacity) != 0)
            throw std::runtime_error("could not read Apple device records");

        std::size_t wired_index{};
        std::unordered_set<std::wstring> seen_udids;
        for (const auto& device : devices) {
            if (device.usb_connected == 0 || device.udid[0] == L'\0') continue;
            // Windows can expose the same physical phone through more than one
            // Apple interface record. Opening each record would make multiple
            // sessions compete for one projection transport and invalidate the
            // switch result, so probe each physical UDID only once.
            if (!seen_udids.emplace(device.udid).second) continue;
            ++wired_index;
            if (requested_device && *requested_device != wired_index) continue;
            Session session;
            session.name = device.name[0] == L'\0'
                ? L"Apple device " + std::to_wstring(sessions.size() + 1)
                : device.name;
            session.udid = device.udid;

            if (!create_session(session)) continue;
            sessions.push_back(std::move(session));
            if (sessions.back().handle == 0)
                throw std::runtime_error("could not attach diagnostic preview");
        }
        if (sessions.empty()) throw std::runtime_error("no wired capture session could be created");

        for (auto& session : sessions) {
            if (!wait_for_streaming(session, 30s, 5))
                throw std::runtime_error("initial stream did not stabilize");
            iPhoneMirror::VideoOutputStatus before{};
            if (!read_output_status(session, before))
                throw std::runtime_error("could not read initial decoder status");
            const Step initial{
                before.requested_decoder_preference,
                before.requested_color_output_preference,
                L"initial",
            };
            if (!wait_for_decoder_commit(session, initial, before, 10s))
                throw std::runtime_error("initial decoder runtime was not confirmed");
        }

        if (lifecycle_probe) {
            if (sessions.size() != 1)
                throw std::runtime_error("lifecycle mode requires exactly one selected device");
            auto& session = sessions.front();
            std::wcout << L"lifecycle first_stream_complete device=\""
                       << session.name << L"\"\n";
            if (!stop_and_destroy(session))
                throw std::runtime_error("first lifecycle stop did not confirm USB teardown");
            if (!device_is_discoverable(session.udid))
                throw std::runtime_error("device was not discoverable after first lifecycle stop");
            std::wcout << L"lifecycle normal_device_rediscovered device=\""
                       << session.name << L"\"\n";
            if (!create_session(session))
                throw std::runtime_error("second lifecycle session could not be created");
            if (!wait_for_streaming(session, 30s, 5))
                throw std::runtime_error("second lifecycle stream did not stabilize");
            std::wcout << L"lifecycle second_stream_complete device=\""
                       << session.name << L"\"\n";
            if (!stop_and_destroy(session))
                throw std::runtime_error("second lifecycle stop did not confirm USB teardown");
            std::wcout << L"lifecycle probe passed device=\""
                       << session.name << L"\"\n";
            exit_code = 0;
            im_shutdown();
            return exit_code;
        }

        constexpr Step steps[] = {
            {0, 2, L"prefer-hdr"},
            {2, 2, L"software-compatible"},
            {1, 2, L"hardware-preferred"},
            {1, 1, L"force-sdr"},
            {0, 0, L"auto"},
        };
        for (const auto& step : steps) {
            std::wcout << L"apply " << step.name << L'\n';
            std::vector<iPhoneMirror::VideoOutputStatus> before;
            before.reserve(sessions.size());
            for (auto& session : sessions) {
                iPhoneMirror::VideoOutputStatus status{};
                if (!read_output_status(session, status))
                    throw std::runtime_error("could not read pre-switch decoder status");
                before.push_back(status);
                const auto original_handle = session.handle;
                if (im_session_set_pipeline_preferences(session.handle,
                        step.decoder, step.color) != 0) {
                    std::wcerr << L"preference update failed for " << session.name
                               << L": " << last_error() << L'\n';
                    throw std::runtime_error("pipeline preference update failed");
                }
                if (session.handle != original_handle)
                    throw std::runtime_error("session handle changed during pipeline update");
            }
            for (std::size_t index{}; index < sessions.size(); ++index) {
                auto& session = sessions[index];
                if (!wait_for_decoder_commit(session, step, before[index], 30s))
                    throw std::runtime_error("decoder policy was not committed");
                if (!wait_for_streaming(session, 15s, 30))
                    throw std::runtime_error("stream stopped after pipeline update");
            }
        }

        std::wcout << L"pipeline switch probe passed sessions=" << sessions.size() << L'\n';
        exit_code = 0;
    } catch (const std::exception& error) {
        std::cerr << "pipeline switch probe failed: " << error.what() << '\n';
    }
    close_sessions(sessions);
    im_shutdown();
    return exit_code;
}
