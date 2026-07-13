#include "Capture/WirelessCaptureSession.h"

#include <stdexcept>

namespace iPhoneMirror::capture {

WirelessCaptureSession::WirelessCaptureSession(
    std::shared_ptr<WirelessReceiverHub> receiver, std::wstring device_id,
    CapturePreferences preferences)
    : receiver_(std::move(receiver)), device_id_(std::move(device_id)),
      preferences_(preferences) {}

WirelessCaptureSession::~WirelessCaptureSession() { stop(); }

void WirelessCaptureSession::start() {
    if (stream_) throw std::runtime_error("wireless capture is already running");
    if (!receiver_) throw std::runtime_error("wireless receiver is unavailable");
    stream_ = receiver_->attach(device_id_, preferences_);
}

void WirelessCaptureSession::stop() noexcept {
    if (!stream_) return;
    stream_->detach();
    stream_.reset();
}

Snapshot WirelessCaptureSession::snapshot() const {
    return stream_ ? stream_->snapshot() : Snapshot{.state = State::Stopped,
        .message = L"Wireless mirroring stopped"};
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

void WirelessCaptureSession::request_display_orientation(bool) noexcept {}

} // namespace iPhoneMirror::capture
