#include "Capture/WirelessCaptureSession.h"

#include <stdexcept>

namespace iPhoneMirror::capture {

WirelessCaptureSession::WirelessCaptureSession(
    std::shared_ptr<WirelessReceiverHub> receiver, std::wstring device_id,
    CapturePreferences preferences)
    : receiver_(std::move(receiver)), device_id_(std::move(device_id)),
      preferences_(preferences) {}

WirelessCaptureSession::~WirelessCaptureSession() { stop(); }

WirelessCaptureSession::WirelessCaptureSession(std::wstring device_id,
    CapturePreferences preferences)
    : device_id_(std::move(device_id)), preferences_(preferences), external_(true) {}

bool WirelessCaptureSession::publish_external(std::uint32_t kind,
    std::uint32_t width, std::uint32_t height, std::span<const std::uint8_t> payload) {
    if (!external_ || !stream_) return false;
    wireless::MessageHeader header{};
    header.payload_size = static_cast<std::uint32_t>(payload.size());
    if (kind == 1) {
        const auto chroma_size = ((width + 1ULL) / 2) * ((height + 1ULL) / 2);
        if (width < 2 || height < 2 || width > 8192 || height > 8192 ||
            payload.size() != width * static_cast<std::uint64_t>(height) + 2 * chroma_size) return false;
        header.width = width; header.height = height;
        header.stride[0] = width; header.stride[1] = header.stride[2] = (width + 1) / 2;
        header.plane_size[0] = width * height;
        header.plane_size[1] = header.plane_size[2] = static_cast<std::uint32_t>(chroma_size);
        stream_->publish_video(header, payload);
        external_state(true);
    } else if (kind == 2) {
        header.sample_rate = width;
        if (height < 1 || height > 2 || width != 48000 || payload.empty() ||
            payload.size() > 192000 || payload.size() % (height * 2)) return false;
        header.channels = static_cast<std::uint16_t>(height); header.bits_per_sample = 16;
        stream_->publish_audio(header, payload);
    } else return false;
    return true;
}

void WirelessCaptureSession::external_state(bool active) {
    external_active_.store(active);
    if (external_ && stream_) stream_->set_mirror_state(active ?
        wireless::MirrorSenderState::Active : wireless::MirrorSenderState::Reconnecting);
}

void WirelessCaptureSession::start() {
    if (stream_) throw std::runtime_error("wireless capture is already running");
    if (external_) {
        stream_ = std::make_shared<WirelessClientStream>(device_id_, L"CoreDevice");
        stream_->set_identity(L"CoreDevice", true);
        stream_->attach(preferences_);
        return;
    }
    if (!receiver_) throw std::runtime_error("wireless receiver is unavailable");
    stream_ = receiver_->attach(device_id_, preferences_);
}

void WirelessCaptureSession::stop() noexcept {
    if (!stream_) return;
    stream_->detach();
    stream_.reset();
}

Snapshot WirelessCaptureSession::snapshot() const {
    auto result = stream_ ? stream_->snapshot() : Snapshot{.state = State::Stopped,
        .message = L"Wireless mirroring stopped"};
    if (external_) result.message = !external_active_.load() ? L"CoreDevice reconnecting" :
        result.state == State::Streaming ? L"CoreDevice media streaming" : L"CoreDevice: waiting for a complete decoded frame";
    return result;
}

std::int64_t WirelessCaptureSession::latest_frame_timestamp() const {
    return stream_ ? stream_->latest_frame_timestamp() : 0;
}

std::shared_ptr<const media::DecodedFrame> WirelessCaptureSession::latest_frame() const {
    return stream_ ? stream_->latest_frame() : nullptr;
}

std::shared_ptr<const media::DecodedFrame> WirelessCaptureSession::next_render_frame() {
    return stream_ ? stream_->next_render_frame() : nullptr;
}

std::shared_ptr<const AudioPacket> WirelessCaptureSession::next_audio_packet(
    std::uint64_t after_sequence) const {
    return stream_ ? stream_->next_audio_packet(after_sequence) : nullptr;
}

void WirelessCaptureSession::set_audio_enabled(bool enabled) noexcept {
    preferences_.play_audio = enabled;
    if (stream_) stream_->set_audio_enabled(enabled);
}

void WirelessCaptureSession::set_audio_volume(float volume) noexcept {
    preferences_.audio_volume = volume;
    if (stream_) stream_->set_audio_volume(volume);
}

void WirelessCaptureSession::set_target_fps(std::uint32_t target_fps) noexcept {
    preferences_.target_fps = target_fps;
    if (stream_) stream_->set_target_fps(target_fps);
}

std::uint32_t WirelessCaptureSession::target_fps() const noexcept {
    return stream_ ? stream_->target_fps() : preferences_.target_fps;
}

void WirelessCaptureSession::set_decoder_preference(
    media::DecoderPreference preference) noexcept {
    preferences_.decoder_preference = preference;
}

DecoderSwitchStatus WirelessCaptureSession::decoder_switch_status() const noexcept {
    // The wireless host publishes decoded I420/NV12 frames over IPC. Its
    // decoder is outside this process, so this session must not report an
    // indeterminate hardware path forever or claim a software implementation
    // that it cannot inspect.
    return {
        preferences_.decoder_preference,
        preferences_.decoder_preference,
        1,
        1,
        DecoderSwitchPhase::Applied,
        DecoderRuntimeMode::External,
    };
}

void WirelessCaptureSession::request_display_orientation(bool) noexcept {}

} // namespace iPhoneMirror::capture
