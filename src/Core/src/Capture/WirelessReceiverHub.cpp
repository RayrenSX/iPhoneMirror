#include "Capture/WirelessReceiverHub.h"

#include "Audio/WasapiRenderer.h"
#include "IpcProtocol.h"
#include "Logging.h"

#include <Windows.h>
#include <sddl.h>

#include <algorithm>
#include <cmath>
#include <filesystem>
#include <format>
#include <iterator>
#include <stdexcept>

namespace {

HANDLE as_handle(void* value) noexcept { return static_cast<HANDLE>(value); }

class LocalSecurityAttributes final {
public:
    LocalSecurityAttributes() {
        constexpr auto descriptor = L"D:P(A;;GA;;;SY)(A;;GA;;;OW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor,
                SDDL_REVISION_1, &descriptor_, nullptr))
            throw std::runtime_error(std::format(
                "Could not create IPC security descriptor: {}", GetLastError()));
        attributes_ = {
            .nLength = sizeof(attributes_),
            .lpSecurityDescriptor = descriptor_,
            .bInheritHandle = FALSE,
        };
    }

    ~LocalSecurityAttributes() { if (descriptor_) LocalFree(descriptor_); }
    LocalSecurityAttributes(const LocalSecurityAttributes&) = delete;
    LocalSecurityAttributes& operator=(const LocalSecurityAttributes&) = delete;

    SECURITY_ATTRIBUTES* get() noexcept { return &attributes_; }

private:
    PSECURITY_DESCRIPTOR descriptor_{};
    SECURITY_ATTRIBUTES attributes_{};
};

std::wstring unique_ipc_suffix() {
    GUID value{};
    if (FAILED(CoCreateGuid(&value)))
        throw std::runtime_error("Could not generate an AirPlay IPC nonce");
    wchar_t text[40]{};
    if (StringFromGUID2(value, text, static_cast<int>(std::size(text))) == 0)
        throw std::runtime_error("Could not format an AirPlay IPC nonce");
    return std::format(L"{}-{}", GetCurrentProcessId(), text);
}

bool read_all(HANDLE pipe, void* destination, std::size_t size) noexcept {
    auto* bytes = static_cast<std::uint8_t*>(destination);
    while (size != 0) {
        DWORD read{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size, 1024U * 1024U));
        if (!ReadFile(pipe, bytes, request, &read, nullptr) || read == 0) return false;
        bytes += read;
        size -= read;
    }
    return true;
}

bool write_all(HANDLE pipe, const void* source, std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size, 1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr) || written == 0) return false;
        bytes += written;
        size -= written;
    }
    return true;
}

bool connect_expected_client(HANDLE pipe, DWORD expected_process_id,
    std::stop_token stop_token) {
    while (!stop_token.stop_requested()) {
        const auto connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
            GetLastError() == ERROR_PIPE_CONNECTED;
        if (!connected) return false;

        ULONG client_process_id{};
        if (GetNamedPipeClientProcessId(pipe, &client_process_id) &&
            client_process_id == expected_process_id)
            return true;

        iPhoneMirror::logging::write(std::format(
            "wireless_hub rejected pipe client pid={} expected={}",
            client_process_id, expected_process_id));
        DisconnectNamedPipe(pipe);
    }
    return false;
}

std::wstring quote_argument(std::wstring_view value) {
    std::wstring quoted{L"\""};
    std::size_t slashes{};
    for (const auto character : value) {
        if (character == L'\\') {
            ++slashes;
            continue;
        }
        if (character == L'\"') {
            quoted.append(slashes * 2 + 1, L'\\');
            quoted.push_back(L'\"');
            slashes = 0;
            continue;
        }
        quoted.append(slashes, L'\\');
        slashes = 0;
        quoted.push_back(character);
    }
    quoted.append(slashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

std::wstring widen(std::string_view text) {
    if (text.empty()) return {};
    const auto length = MultiByteToWideChar(CP_UTF8, 0, text.data(),
        static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0) return {};
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
        result.data(), length);
    return result;
}

std::string narrow(std::wstring_view text) {
    if (text.empty()) return {};
    const auto length = WideCharToMultiByte(CP_UTF8, 0, text.data(),
        static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
        result.data(), length, nullptr, nullptr);
    return result;
}

template <std::size_t Size>
std::wstring header_text(const char (&value)[Size]) {
    return widen(std::string_view(value, strnlen_s(value, Size)));
}

} // namespace

namespace iPhoneMirror::capture {

namespace detail {

bool convert_i420_to_nv12(const wireless::MessageHeader& header,
    std::span<const std::uint8_t> payload, std::vector<std::uint8_t>& destination,
    std::int32_t& destination_stride) noexcept {
    if (header.width == 0 || header.height == 0 || header.width > 8192 ||
        header.height > 8192 || header.stride[0] < header.width) return false;
    const auto chroma_width = (header.width + 1U) / 2U;
    const auto chroma_height = (header.height + 1U) / 2U;
    if (header.stride[1] < chroma_width || header.stride[2] < chroma_width ||
        header.plane_size[0] < static_cast<std::uint64_t>(header.stride[0]) * header.height ||
        header.plane_size[1] < static_cast<std::uint64_t>(header.stride[1]) * chroma_height ||
        header.plane_size[2] < static_cast<std::uint64_t>(header.stride[2]) * chroma_height)
        return false;
    const auto total = static_cast<std::uint64_t>(header.plane_size[0]) +
        header.plane_size[1] + header.plane_size[2];
    if (total > payload.size()) return false;

    const auto stride = static_cast<std::size_t>((header.width + 1U) & ~1U);
    const auto y_bytes = stride * header.height;
    try {
        destination.assign(y_bytes + stride * chroma_height, 0);
    } catch (...) {
        return false;
    }
    destination_stride = static_cast<std::int32_t>(stride);

    const auto* y_plane = payload.data();
    const auto* u_plane = y_plane + header.plane_size[0];
    const auto* v_plane = u_plane + header.plane_size[1];
    for (std::uint32_t row = 0; row < header.height; ++row)
        std::copy_n(y_plane + static_cast<std::size_t>(row) * header.stride[0],
            header.width, destination.data() + static_cast<std::size_t>(row) * stride);
    auto* uv = destination.data() + y_bytes;
    for (std::uint32_t row = 0; row < chroma_height; ++row) {
        const auto* source_u = u_plane + static_cast<std::size_t>(row) * header.stride[1];
        const auto* source_v = v_plane + static_cast<std::size_t>(row) * header.stride[2];
        auto* output = uv + static_cast<std::size_t>(row) * stride;
        for (std::uint32_t column = 0; column < chroma_width; ++column) {
            output[column * 2U] = source_u[column];
            output[column * 2U + 1U] = source_v[column];
        }
    }
    return true;
}

} // namespace detail

WirelessClientStream::WirelessClientStream(std::wstring id, std::wstring name)
    : id_(std::move(id)), name_(std::move(name)) {
    snapshot_.state = State::WaitingForDevice;
    snapshot_.message = L"AirPlay client discovered";
}

WirelessClientStream::~WirelessClientStream() { stop_audio_renderer(); }

void WirelessClientStream::set_identity(std::wstring name, bool connected) {
    bool changed_connection{};
    {
        std::scoped_lock lock(mutex_);
        if (!name.empty()) name_ = std::move(name);
        changed_connection = connected_ != connected;
        connected_ = connected;
        if (changed_connection || !connected) {
            snapshot_.state = connected ? State::Handshaking : State::WaitingForDevice;
            snapshot_.message = connected ? L"AirPlay connected: " + name_
                                          : L"AirPlay disconnected";
        }
    }
    if (!connected) clear_media();
}

void WirelessClientStream::set_metadata(
    std::wstring product_type, std::wstring os_version) {
    std::scoped_lock lock(mutex_);
    if (!product_type.empty()) product_type_ = std::move(product_type);
    if (!os_version.empty()) os_version_ = std::move(os_version);
}

WirelessDeviceSnapshot WirelessClientStream::device() const {
    std::scoped_lock lock(mutex_);
    return {
        .id = id_,
        .name = name_,
        .product_type = product_type_,
        .os_version = os_version_,
    };
}

bool WirelessClientStream::connected() const {
    std::scoped_lock lock(mutex_);
    return connected_;
}

void WirelessClientStream::attach(CapturePreferences preferences) {
    {
        std::scoped_lock lock(mutex_);
        ++attachments_;
        snapshot_.state = connected_ ? State::Handshaking : State::WaitingForDevice;
        snapshot_.message = connected_ ? L"AirPlay connected: " + name_
                                       : L"Waiting for AirPlay device";
    }
    set_target_fps(preferences.target_fps);
    set_audio_volume(preferences.audio_volume);
    set_audio_enabled(preferences.play_audio);
}

void WirelessClientStream::detach() noexcept {
    bool stop_audio{};
    {
        std::scoped_lock lock(mutex_);
        if (attachments_ != 0) --attachments_;
        stop_audio = attachments_ == 0;
    }
    if (stop_audio) stop_audio_renderer();
}

Snapshot WirelessClientStream::snapshot() const {
    std::scoped_lock lock(mutex_);
    return snapshot_;
}

std::int64_t WirelessClientStream::latest_frame_timestamp() const {
    std::scoped_lock lock(mutex_);
    return latest_frame_ ? latest_frame_->timestamp_100ns : 0;
}

std::shared_ptr<const media::DecodedFrame> WirelessClientStream::latest_frame() const {
    std::scoped_lock lock(mutex_);
    return latest_frame_;
}

std::shared_ptr<const media::DecodedFrame> WirelessClientStream::next_render_frame() {
    std::scoped_lock lock(mutex_);
    if (render_queue_.empty()) return nullptr;
    auto frame = render_queue_.size() > 2 ? std::move(render_queue_.back())
                                          : std::move(render_queue_.front());
    if (render_queue_.size() > 2) render_queue_.clear();
    else render_queue_.pop_front();
    return frame;
}

void WirelessClientStream::set_audio_enabled(bool enabled) noexcept {
    play_audio_.store(enabled, std::memory_order_relaxed);
    std::scoped_lock lock(audio_mutex_);
    if (audio_renderer_) audio_renderer_->set_enabled(enabled);
}

void WirelessClientStream::set_audio_volume(float volume) noexcept {
    if (!std::isfinite(volume)) return;
    const auto clamped = std::clamp(volume, 0.0F, 1.0F);
    audio_volume_.store(clamped, std::memory_order_relaxed);
    std::scoped_lock lock(audio_mutex_);
    if (audio_renderer_) audio_renderer_->set_volume(clamped);
}

void WirelessClientStream::set_target_fps(std::uint32_t target_fps) noexcept {
    target_fps_.store(target_fps, std::memory_order_relaxed);
}

std::uint32_t WirelessClientStream::target_fps() const noexcept {
    return target_fps_.load(std::memory_order_relaxed);
}

void WirelessClientStream::publish_video(const wireless::MessageHeader& header,
    std::span<const std::uint8_t> payload) {
    const auto received_at = std::chrono::steady_clock::now();
    auto frame = std::make_shared<media::DecodedFrame>();
    if (!detail::convert_i420_to_nv12(header, payload, frame->nv12, frame->stride)) {
        if (++rejected_video_messages_ == 1)
            logging::write(std::format("wireless_video rejected device={} size={}x{}",
                narrow(id_), header.width, header.height));
        return;
    }
    frame->width = header.width;
    frame->height = header.height;
    frame->timestamp_100ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
        received_at - started_at_).count() / 100;
    frame->received_at = received_at;

    std::scoped_lock lock(mutex_);
    latest_frame_ = frame;
    render_queue_.push_back(frame);
    const auto queue_limit = attachments_ == 0 ? 1U : 4U;
    while (render_queue_.size() > queue_limit) render_queue_.pop_front();
    snapshot_.state = State::Streaming;
    snapshot_.message = L"Wireless mirroring";
    snapshot_.width = header.width;
    snapshot_.height = header.height;
    ++snapshot_.video_frames;
    const auto elapsed = std::chrono::duration<double>(received_at - fps_sample_time_).count();
    if (elapsed >= 0.5) {
        snapshot_.fps = static_cast<double>(snapshot_.video_frames - fps_sample_frames_) / elapsed;
        fps_sample_frames_ = snapshot_.video_frames;
        fps_sample_time_ = received_at;
    }
    snapshot_.latency_ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - received_at).count();
    if (snapshot_.video_frames == 1)
        logging::write(std::format("wireless_video published device={} size={}x{} stride={} bytes={}",
            narrow(id_), frame->width, frame->height, frame->stride, frame->nv12.size()));
}

void WirelessClientStream::publish_audio(const wireless::MessageHeader& header,
    std::span<const std::uint8_t> payload) {
    if (payload.empty() || header.bits_per_sample != 16 || header.channels == 0 ||
        header.channels > 8 || header.sample_rate < 8000 || header.sample_rate > 192000 ||
        payload.size() % (static_cast<std::size_t>(header.channels) * 2U) != 0) {
        if (++rejected_audio_messages_ == 1)
            logging::write(std::format("wireless_audio rejected device={}", narrow(id_)));
        return;
    }
    bool attached{};
    {
        std::scoped_lock lock(mutex_);
        attached = attachments_ != 0;
    }
    if (attached) {
        std::scoped_lock lock(audio_mutex_);
        const auto format_changed = audio_sample_rate_ != header.sample_rate ||
            audio_channels_ != header.channels || audio_bits_ != header.bits_per_sample;
        if (format_changed || (!audio_renderer_ && !audio_renderer_failed_)) {
            audio_renderer_.reset();
            audio_renderer_failed_ = false;
            audio_sample_rate_ = header.sample_rate;
            audio_channels_ = header.channels;
            audio_bits_ = header.bits_per_sample;
            coremedia::AudioStreamBasicDescription format{
                .sample_rate = static_cast<double>(header.sample_rate),
                .format_id = 0x6c70636dU,
                .format_flags = 1U << 2U,
                .bytes_per_packet = static_cast<std::uint32_t>(header.channels * 2U),
                .frames_per_packet = 1,
                .bytes_per_frame = static_cast<std::uint32_t>(header.channels * 2U),
                .channels_per_frame = header.channels,
                .bits_per_channel = header.bits_per_sample,
            };
            try {
                audio_renderer_ = std::make_unique<audio::WasapiRenderer>(format,
                    play_audio_.load(std::memory_order_relaxed),
                    audio_volume_.load(std::memory_order_relaxed));
            } catch (const std::exception& error) {
                audio_renderer_failed_ = true;
                logging::write(std::format("wireless audio playback disabled for {}: {}",
                    narrow(id_), error.what()));
            }
        }
        if (audio_renderer_) audio_renderer_->enqueue(payload);
    }
    std::scoped_lock lock(mutex_);
    ++snapshot_.audio_packets;
    snapshot_.audio_sample_rate = header.sample_rate;
    snapshot_.audio_channels = header.channels;
}

void WirelessClientStream::clear_media() noexcept {
    {
        std::scoped_lock lock(mutex_);
        latest_frame_.reset();
        render_queue_.clear();
        snapshot_.width = snapshot_.height = 0;
        snapshot_.fps = snapshot_.latency_ms = 0;
        snapshot_.audio_sample_rate = snapshot_.audio_channels = 0;
    }
    stop_audio_renderer();
}

void WirelessClientStream::stop_audio_renderer() noexcept {
    std::unique_ptr<audio::WasapiRenderer> renderer;
    {
        std::scoped_lock lock(audio_mutex_);
        renderer = std::move(audio_renderer_);
        audio_sample_rate_ = 0;
        audio_channels_ = audio_bits_ = 0;
        audio_renderer_failed_ = false;
    }
    renderer.reset();
}

WirelessReceiverHub::~WirelessReceiverHub() { stop(); }

void WirelessReceiverHub::start(std::wstring receiver_name, std::wstring host_path,
    std::uint32_t width, std::uint32_t height, std::uint32_t frame_rate) {
    std::scoped_lock lifecycle_lock(lifecycle_mutex_);
    // A crashed host leaves a signaled process handle and a joinable reader
    // thread behind. Reap that stale state before starting its replacement.
    if (process_ && WaitForSingleObject(as_handle(process_), 0) != WAIT_TIMEOUT)
        stop_locked();
    if (worker_.joinable() || process_) return;
    if (!std::filesystem::is_regular_file(host_path))
        throw std::runtime_error("wireless host executable is missing");
    receiver_name_ = std::move(receiver_name);
    host_path_ = std::move(host_path);
    {
        std::scoped_lock lock(mutex_);
        media_command_ = {};
    }
    const auto suffix = unique_ipc_suffix();
    pipe_name_ = L"\\\\.\\pipe\\iPhoneMirror-AirPlay-Hub-" + suffix;
    stop_event_name_ = L"Local\\iPhoneMirror-AirPlay-Hub-Stop-" + suffix;

    LocalSecurityAttributes security;
    const auto pipe = CreateNamedPipeW(pipe_name_.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 64U * 1024U, 8U * 1024U * 1024U,
        0, security.get());
    if (pipe == INVALID_HANDLE_VALUE)
        throw std::runtime_error(std::format("CreateNamedPipe failed: {}", GetLastError()));
    pipe_ = pipe;
    SetLastError(ERROR_SUCCESS);
    const auto stop_event = CreateEventW(security.get(), TRUE, FALSE, stop_event_name_.c_str());
    const auto stop_event_error = GetLastError();
    if (!stop_event) {
        CloseHandle(pipe);
        pipe_ = nullptr;
        throw std::runtime_error(std::format("CreateEvent failed: {}", stop_event_error));
    }
    if (stop_event_error == ERROR_ALREADY_EXISTS) {
        CloseHandle(stop_event);
        CloseHandle(pipe);
        pipe_ = nullptr;
        throw std::runtime_error("AirPlay stop event name was already claimed");
    }
    stop_event_ = stop_event;

    auto command = quote_argument(host_path_) + L" --pipe " + quote_argument(pipe_name_) +
        L" --stop-event " + quote_argument(stop_event_name_) + L" --name " +
        quote_argument(receiver_name_) + L" --parent-pid " + std::to_wstring(GetCurrentProcessId()) +
        L" --width " + std::to_wstring(width) + L" --height " + std::to_wstring(height) +
        L" --fps " + std::to_wstring(frame_rate) +
        L" --mode combined --raop-port 5001 --airplay-port 7001";
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    const auto working_directory = std::filesystem::path(host_path_).parent_path().wstring();
    if (!CreateProcessW(host_path_.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, working_directory.c_str(), &startup, &process)) {
        CloseHandle(stop_event);
        CloseHandle(pipe);
        stop_event_ = pipe_ = nullptr;
        throw std::runtime_error(std::format("CreateProcess for wireless host failed: {}",
            GetLastError()));
    }
    CloseHandle(process.hThread);
    process_ = process.hProcess;
    stopping_.store(false, std::memory_order_release);
    ready_.store(false, std::memory_order_release);
    logging::write(std::format(
        "wireless_hub host_started pid={} receiver={} capability={}x{}@{} mode=combined host={}",
        process.dwProcessId, narrow(receiver_name_), width, height, frame_rate,
        narrow(host_path_)));
    worker_ = std::jthread([this](std::stop_token token) { run(token); });
}

void WirelessReceiverHub::stop() noexcept {
    std::scoped_lock lifecycle_lock(lifecycle_mutex_);
    stop_locked();
}

void WirelessReceiverHub::stop_locked() noexcept {
    if (!worker_.joinable() && !process_) return;
    stopping_.store(true, std::memory_order_release);
    ready_.store(false, std::memory_order_release);
    if (stop_event_) SetEvent(as_handle(stop_event_));
    if (pipe_) {
        CancelIoEx(as_handle(pipe_), nullptr);
        DisconnectNamedPipe(as_handle(pipe_));
    }
    if (worker_.joinable()) {
        worker_.request_stop();
        worker_.join();
    }
    if (process_) {
        if (WaitForSingleObject(as_handle(process_), 3000) == WAIT_TIMEOUT) {
            TerminateProcess(as_handle(process_), 1);
            WaitForSingleObject(as_handle(process_), 1000);
        }
        CloseHandle(as_handle(process_));
        process_ = nullptr;
    }
    if (stop_event_) {
        CloseHandle(as_handle(stop_event_));
        stop_event_ = nullptr;
    }
    if (pipe_) {
        CloseHandle(as_handle(pipe_));
        pipe_ = nullptr;
    }
    mark_all_disconnected();
    logging::write("wireless_hub stopped");
}

bool WirelessReceiverHub::running() const noexcept {
    std::scoped_lock lifecycle_lock(lifecycle_mutex_);
    return process_ && WaitForSingleObject(as_handle(process_), 0) == WAIT_TIMEOUT;
}

bool WirelessReceiverHub::ready() const noexcept {
    return ready_.load(std::memory_order_acquire);
}

std::vector<WirelessDeviceSnapshot> WirelessReceiverHub::devices() const {
    std::vector<std::shared_ptr<WirelessClientStream>> streams;
    {
        std::scoped_lock lock(mutex_);
        for (const auto& [_, stream] : clients_) streams.push_back(stream);
    }
    std::vector<WirelessDeviceSnapshot> result;
    for (const auto& stream : streams)
        if (stream->connected()) result.push_back(stream->device());
    std::ranges::sort(result, {}, &WirelessDeviceSnapshot::name);
    return result;
}

std::shared_ptr<WirelessClientStream> WirelessReceiverHub::attach(
    std::wstring_view device_id, CapturePreferences preferences) {
    std::shared_ptr<WirelessClientStream> stream;
    {
        std::scoped_lock lock(mutex_);
        const auto found = clients_.find(device_id);
        if (found == clients_.end() || !found->second->connected())
            throw std::runtime_error("AirPlay device is no longer connected");
        stream = found->second;
    }
    stream->attach(preferences);
    return stream;
}

MediaCastCommand WirelessReceiverHub::media_command() const {
    std::scoped_lock lock(mutex_);
    return media_command_;
}

bool WirelessReceiverHub::update_media_playback(std::uint64_t command_id,
    double duration, double position, double rate) noexcept {
    if (!std::isfinite(duration) || !std::isfinite(position) || !std::isfinite(rate) ||
        duration < 0 || position < 0) return false;
    std::scoped_lock lifecycle_lock(lifecycle_mutex_);
    if (!pipe_ || !process_) return false;
    wireless::MessageHeader header;
    header.type = wireless::MessageType::PlaybackState;
    header.media_command_id = command_id;
    header.media_duration = duration;
    header.media_position = position;
    header.media_rate = rate;
    std::scoped_lock write_lock(pipe_write_mutex_);
    return write_all(as_handle(pipe_), &header, sizeof(header));
}

void WirelessReceiverHub::run(std::stop_token stop_token) noexcept {
    try {
        const auto pipe = as_handle(pipe_);
        const auto expected_process_id = process_ ? GetProcessId(as_handle(process_)) : 0;
        const auto connected = expected_process_id != 0 &&
            connect_expected_client(pipe, expected_process_id, stop_token);
        if (!connected) throw std::runtime_error("wireless host could not connect");
        logging::write("wireless_hub pipe_connected");
        while (!stop_token.stop_requested()) {
            wireless::MessageHeader header;
            if (!read_all(pipe, &header, sizeof(header))) break;
            if (header.magic != wireless::IpcMagic || header.version != wireless::IpcVersion ||
                header.payload_size > wireless::MaxPayloadBytes)
                throw std::runtime_error("invalid wireless IPC message");
            std::vector<std::uint8_t> payload(header.payload_size);
            if (!payload.empty() && !read_all(pipe, payload.data(), payload.size())) break;
            handle_message(header, payload);
        }
    } catch (const std::exception& error) {
        if (!stopping_.load(std::memory_order_acquire))
            logging::write(std::format("wireless_hub error: {}", error.what()));
    }
    ready_.store(false, std::memory_order_release);
    if (!stopping_.load(std::memory_order_acquire)) mark_all_disconnected();
}

void WirelessReceiverHub::handle_message(const wireless::MessageHeader& header,
    const std::vector<std::uint8_t>& payload) {
    switch (header.type) {
    case wireless::MessageType::Ready:
        ready_.store(true, std::memory_order_release);
        logging::write("wireless_hub ready");
        break;
    case wireless::MessageType::Connected: {
        const auto stream = get_or_create(header, true);
        if (stream) logging::write(std::format("wireless_hub connected id={} name={}",
            narrow(stream->device().id), narrow(stream->device().name)));
        break;
    }
    case wireless::MessageType::Disconnected: {
        const auto stream = get_or_create(header, false);
        if (stream) {
            auto device = stream->device();
            const auto name = header_text(header.device_name);
            stream->set_identity(name.empty() ? device.name : name, false);
            logging::write(std::format("wireless_hub disconnected id={}", narrow(device.id)));
        }
        break;
    }
    case wireless::MessageType::Video:
        if (const auto stream = get_or_create(header, true)) stream->publish_video(header, payload);
        break;
    case wireless::MessageType::Audio:
        if (const auto stream = get_or_create(header, true)) stream->publish_audio(header, payload);
        break;
    case wireless::MessageType::Log:
        logging::write("airplay_host: " + std::string(
            reinterpret_cast<const char*>(payload.data()), payload.size()));
        break;
    case wireless::MessageType::DeviceInfo: {
        const auto stream = get_or_create(header, false);
        if (stream) {
            stream->set_metadata(header_text(header.product_type),
                header_text(header.os_version));
            const auto device = stream->device();
            logging::write(std::format(
                "wireless_hub metadata id={} model={} os={}", narrow(device.id),
                narrow(device.product_type), narrow(device.os_version)));
        }
        break;
    }
    case wireless::MessageType::MediaPlay: {
        if (header.media_command_id == 0 || payload.empty() || payload.size() > 16U * 1024U)
            throw std::runtime_error("invalid media play command");
        const auto url = widen(std::string_view(
            reinterpret_cast<const char*>(payload.data()), payload.size()));
        if (!url.starts_with(L"http://") && !url.starts_with(L"https://"))
            throw std::runtime_error("media play URL is not HTTP(S)");
        {
            std::scoped_lock lock(mutex_);
            media_command_ = {
                .id = header.media_command_id,
                .type = MediaCastCommandType::Play,
                .url = url,
                .start_position = std::max(0.0, header.media_position),
                .volume = header.media_volume,
            };
        }
        logging::write(std::format("wireless_hub media_play command={} url_bytes={}",
            header.media_command_id, payload.size()));
        break;
    }
    case wireless::MessageType::MediaStop:
        if (header.media_command_id == 0)
            throw std::runtime_error("invalid media stop command");
        {
            std::scoped_lock lock(mutex_);
            media_command_ = {
                .id = header.media_command_id,
                .type = MediaCastCommandType::Stop,
            };
        }
        logging::write(std::format("wireless_hub media_stop command={}",
            header.media_command_id));
        break;
    case wireless::MessageType::PlaybackState:
        throw std::runtime_error("unexpected playback state from wireless host");
    default:
        throw std::runtime_error("unknown wireless IPC message type");
    }
}

std::shared_ptr<WirelessClientStream> WirelessReceiverHub::get_or_create(
    const wireless::MessageHeader& header, bool mark_connected) {
    auto id = header_text(header.device_id);
    if (id.empty()) {
        logging::write("wireless_hub rejected message without device id");
        return nullptr;
    }
    auto name = header_text(header.device_name);
    std::shared_ptr<WirelessClientStream> stream;
    {
        std::scoped_lock lock(mutex_);
        auto [position, inserted] = clients_.try_emplace(id);
        if (inserted) position->second = std::make_shared<WirelessClientStream>(id, name);
        stream = position->second;
    }
    if (mark_connected) stream->set_identity(std::move(name), true);
    return stream;
}

void WirelessReceiverHub::mark_all_disconnected() noexcept {
    std::vector<std::shared_ptr<WirelessClientStream>> streams;
    {
        std::scoped_lock lock(mutex_);
        for (const auto& [_, stream] : clients_) streams.push_back(stream);
    }
    for (const auto& stream : streams) stream->set_identity(stream->device().name, false);
}

} // namespace iPhoneMirror::capture
