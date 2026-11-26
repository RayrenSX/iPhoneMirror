#include "Capture/UsbOrientationMonitor.h"
#include "Logging.h"
#include <Windows.h>
#include <array>
#include <algorithm>
#include <charconv>
#include <filesystem>
#include <format>
#include <string>
#include <thread>
#include <vector>

namespace iPhoneMirror::capture {
void detail::OrientationTimeline::observe(unsigned orientation, Clock::time_point at) {
    const auto turns = springboard_rotation_turns(orientation);
    if (!turns) return;
    std::scoped_lock lock(mutex_);
    if (!samples_.empty() && at < samples_.back().first) return;
    samples_.emplace_back(at, *turns);
    while (samples_.size() > 128) samples_.pop_front();
}
std::optional<unsigned> detail::OrientationTimeline::at(Clock::time_point time) const {
    std::scoped_lock lock(mutex_);
    for (auto sample = samples_.rbegin(); sample != samples_.rend(); ++sample) {
        if (sample->first <= time) {
            if (time - sample->first > std::chrono::seconds(1)) return std::nullopt;
            return sample->second;
        }
    }
    return std::nullopt;
}
void detail::OrientationTimeline::clear() noexcept {
    std::scoped_lock lock(mutex_);
    samples_.clear();
}

namespace {
struct Handle {
    HANDLE value{};
    ~Handle() { reset(); }
    void reset(HANDLE replacement = nullptr) noexcept {
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = replacement;
    }
};
std::optional<unsigned> unsigned_field(std::string_view line, std::string_view prefix) {
    if (!line.starts_with(prefix)) return std::nullopt;
    line.remove_prefix(prefix.size());
    unsigned value{};
    const auto parsed = std::from_chars(line.data(), line.data() + line.size(), value);
    if (parsed.ec != std::errc{} || parsed.ptr != line.data() + line.size()) return std::nullopt;
    return value;
}
}
struct UsbOrientationMonitor::Impl {
    Handle input, output, process, job;
    std::jthread reader;
    detail::OrientationTimeline timeline;
    std::wstring environment_key, published_address, previous_address;
    bool owns_endpoint{};

    void unpublish_endpoint() noexcept {
        if (owns_endpoint && !published_address.empty()) {
            std::array<wchar_t, 128> current{};
            GetEnvironmentVariableW(environment_key.c_str(), current.data(), static_cast<DWORD>(current.size()));
            if (current.data() == published_address)
                SetEnvironmentVariableW(environment_key.c_str(), nullptr);
        }
        published_address.clear();
        owns_endpoint = false;
    }

    void stop() noexcept {
        // Stop clients before releasing the mux owner. In normal application
        // teardown, reverse-control is already stopped before capture ends.
        input.reset();
        if (process.value && WaitForSingleObject(process.value, 5000) == WAIT_TIMEOUT) {
            TerminateJobObject(job.value, 1);
            WaitForSingleObject(process.value, 1000);
        }
        reader.request_stop();
        if (reader.joinable()) reader.join();
        timeline.clear();
        process.reset(); output.reset(); job.reset();
    }
    ~Impl() { stop(); }
    void read(std::stop_token token, bool required) noexcept {
        bool reported_orientation = false;
        try {
            std::string pending;
            std::array<char, 2048> bytes{};
            while (!token.stop_requested()) {
                DWORD available{};
                if (!PeekNamedPipe(output.value, nullptr, 0, nullptr, &available, nullptr)) break;
                if (!available) {
                    if (WaitForSingleObject(process.value, 0) == WAIT_OBJECT_0) break;
                    std::this_thread::sleep_for(std::chrono::milliseconds(10));
                    continue;
                }
                DWORD count{};
                if (!ReadFile(output.value, bytes.data(),
                        static_cast<DWORD>(std::min<std::size_t>(bytes.size(), available)), &count, nullptr)) break;
                pending.append(bytes.data(), count);
                for (auto end = pending.find('\n'); end != std::string::npos; end = pending.find('\n')) {
                    auto line = pending.substr(0, end);
                    pending.erase(0, end + 1);
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    if (const auto orientation = unsigned_field(line, "IM_ORIENTATION ")) {
                        timeline.observe(*orientation, detail::OrientationTimeline::Clock::now());
                        if (!required && !reported_orientation && detail::springboard_rotation_turns(*orientation)) {
                            reported_orientation = true;
                            logging::write("usb_projection artwork_orientation=springboard mode=demo");
                        }
                    } else if (line == "IM_ORIENTATION_UNAVAILABLE") {
                        timeline.clear();
                        if (!required) logging::write("usb_orientation unavailable mode=demo fallback=video");
                    } else if (const auto port = unsigned_field(line, "IM_MUX "); port && *port && *port <= 65535) {
                        published_address = std::format(L"127.0.0.1:{}", *port);
                        owns_endpoint = published_address != previous_address;
                        if (owns_endpoint)
                            SetEnvironmentVariableW(environment_key.c_str(), published_address.c_str());
                        logging::write(std::format("usb_orientation broker_port={} scope=loopback", *port));
                    }
                }
                if (pending.size() > 16384) pending.clear();
            }
        } catch (...) {}
        timeline.clear();
        unpublish_endpoint();
    }
};
UsbOrientationMonitor::UsbOrientationMonitor() : impl_(std::make_unique<Impl>()) {}
UsbOrientationMonitor::~UsbOrientationMonitor() = default;
void UsbOrientationMonitor::stop() noexcept { impl_->stop(); }
std::optional<unsigned> UsbOrientationMonitor::at(detail::OrientationTimeline::Clock::time_point time) const {
    return impl_->timeline.at(time);
}
bool UsbOrientationMonitor::start(std::string_view serial, std::stop_token stop_token, bool required) {
    if (impl_->process.value || stop_token.stop_requested()) return false;
    try {
        std::wstring identity;
        for (const auto character : serial) {
            if (character == '-') continue;
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            identity.push_back(static_cast<wchar_t>(character >= 'a' && character <= 'f'
                ? character - 'a' + 'A' : character));
        }
        if (identity.empty()) return false;
        impl_->environment_key = L"IPHONE_MIRROR_CAPTURE_MUX_" + identity;
        std::array<wchar_t, 128> previous{};
        GetEnvironmentVariableW(impl_->environment_key.c_str(), previous.data(), static_cast<DWORD>(previous.size()));
        impl_->previous_address = previous.data();
        std::array<wchar_t, 32768> executable{};
        const auto length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
        if (!length || length >= executable.size()) return false;
        const auto helper = std::filesystem::path(executable.data()).parent_path() / L"tools" / L"iUsbBridge.exe";
        if (!std::filesystem::is_regular_file(helper)) return false;
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE input_read{}, input_write{}, output_read{}, output_write{};
        if (!CreatePipe(&input_read, &input_write, &security, 0)) return false;
        Handle child_input; child_input.value = input_read;
        impl_->input.value = input_write;
        if (!CreatePipe(&output_read, &output_write, &security, 0)) { impl_->stop(); return false; }
        Handle child_output; child_output.value = output_write;
        impl_->output.value = output_read;
        SetHandleInformation(input_write, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(output_read, HANDLE_FLAG_INHERIT, 0);
        SIZE_T attribute_bytes{};
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attribute_bytes);
        std::vector<std::byte> attributes(attribute_bytes);
        auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        if (!InitializeProcThreadAttributeList(list, 1, 0, &attribute_bytes)) { impl_->stop(); return false; }
        const std::array<HANDLE, 2> handles{input_read, output_write};
        const bool updated = UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            const_cast<HANDLE*>(handles.data()), sizeof(handles), nullptr, nullptr) != FALSE;
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = input_read;
        startup.StartupInfo.hStdOutput = startup.StartupInfo.hStdError = output_write;
        startup.lpAttributeList = list;
        std::wstring command = L"\"" + helper.wstring() + L"\" --observe-orientation --usb --udid " +
            std::wstring(serial.begin(), serial.end());
        PROCESS_INFORMATION process{};
        const bool created = updated && CreateProcessW(helper.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT,
            nullptr, helper.parent_path().c_str(), &startup.StartupInfo, &process);
        DeleteProcThreadAttributeList(list);
        if (!created) { impl_->stop(); return false; }
        impl_->process.value = process.hProcess;
        Handle thread; thread.value = process.hThread;
        impl_->job.value = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!impl_->job.value || !SetInformationJobObject(impl_->job.value, JobObjectExtendedLimitInformation,
                &limits, sizeof(limits)) || !AssignProcessToJobObject(impl_->job.value, process.hProcess)) {
            TerminateProcess(process.hProcess, 1); impl_->stop(); return false;
        }
        ResumeThread(process.hThread);
        child_input.reset(); child_output.reset();
        impl_->reader = std::jthread([this, required](std::stop_token token) { impl_->read(token, required); });
        // A reverse-control bridge can borrow the mux endpoint even when
        // orientation is unavailable. Keep its owner until capture teardown.
        if (!required) return true;
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
        while (!stop_token.stop_requested() && std::chrono::steady_clock::now() < deadline) {
            if (at(std::chrono::steady_clock::now())) {
                logging::write("usb_orientation ready source=springboard capture_mux=shared");
                return true;
            }
            if (WaitForSingleObject(process.hProcess, 0) == WAIT_OBJECT_0) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
        impl_->stop();
    } catch (...) { impl_->stop(); }
    if (required && !stop_token.stop_requested())
        logging::write("usb_orientation unavailable native_airplay=required");
    return false;
}
}
