#include "MediaSource.h"

#include "VirtualCameraShared.h"

#include <ks.h>
#include <ksmedia.h>
#include <mfobjects.h>
#include <propvarutil.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <limits>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::virtual_camera {
namespace {

constexpr DWORD StreamId = 0;
constexpr UINT32 FrameRateDenominator = 1;
constexpr DWORD SamplePoolSize = 10;
constexpr std::size_t MaximumPendingSampleRequests = SamplePoolSize * 2U;
constexpr auto FirstFrameWait = std::chrono::milliseconds(500);
constexpr auto FirstFrameRetryInterval = std::chrono::milliseconds(2);

bool discover_channel_path(std::wstring& path) {
    if (!WaitNamedPipeW(FrameChannelPipeName, 20)) return false;
    HANDLE pipe = CreateFileW(FrameChannelPipeName, GENERIC_READ, 0, nullptr,
        OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    std::vector<wchar_t> buffer(32768);
    DWORD bytes{};
    const BOOL read = ReadFile(pipe, buffer.data(),
        static_cast<DWORD>(buffer.size() * sizeof(wchar_t)), &bytes, nullptr);
    CloseHandle(pipe);
    if (!read || bytes < sizeof(wchar_t) || bytes % sizeof(wchar_t) != 0 ||
        bytes > buffer.size() * sizeof(wchar_t)) return false;
    const auto characters = bytes / sizeof(wchar_t);
    const auto length = wcsnlen_s(buffer.data(), characters);
    if (length == characters) return false;
    path.assign(buffer.data(), length);
    return !path.empty();
}

HRESULT create_media_type(GUID subtype, UINT32 width, UINT32 height,
                          UINT32 frame_rate,
                          IMFMediaType** result) {
    if (result == nullptr) return E_POINTER;
    *result = nullptr;

    ComPtr<IMFMediaType> type;
    HRESULT hr = MFCreateMediaType(&type);
    if (FAILED(hr)) return hr;
    if (FAILED(hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)) ||
        FAILED(hr = type->SetGUID(MF_MT_SUBTYPE, subtype)) ||
        FAILED(hr = type->SetUINT32(MF_MT_INTERLACE_MODE,
                                    MFVideoInterlace_Progressive)) ||
        FAILED(hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE)) ||
        FAILED(hr = type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE)) ||
        FAILED(hr = MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width,
                                       height)) ||
        FAILED(hr = MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE,
                                        frame_rate,
                                        FrameRateDenominator)) ||
        FAILED(hr = MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO,
                                        1, 1)))
        return hr;

    const bool nv12 = subtype == MFVideoFormat_NV12;
    const auto sample_bytes = static_cast<std::uint64_t>(width) * height *
        (nv12 ? 3U : 8U) / 2U;
    if (sample_bytes > std::numeric_limits<UINT32>::max())
        return MF_E_INVALIDMEDIATYPE;
    const auto bitrate_bytes = sample_bytes * 8U * frame_rate;
    const auto sample_size = static_cast<UINT32>(sample_bytes);
    const auto bitrate = static_cast<UINT32>(std::min<std::uint64_t>(
        bitrate_bytes, std::numeric_limits<UINT32>::max()));
    const UINT32 stride = nv12 ? width : width * 4U;
    if (FAILED(hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, sample_size)) ||
        FAILED(hr = type->SetUINT32(MF_MT_AVG_BITRATE, bitrate)) ||
        FAILED(hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, stride)) ||
        FAILED(hr = type->SetUINT32(MF_MT_VIDEO_PRIMARIES,
                                    MFVideoPrimaries_BT709)) ||
        FAILED(hr = type->SetUINT32(MF_MT_TRANSFER_FUNCTION,
                                    MFVideoTransFunc_709)) ||
        FAILED(hr = type->SetUINT32(MF_MT_YUV_MATRIX,
                                    MFVideoTransferMatrix_BT709)) ||
        FAILED(hr = type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,
                                    MFNominalRange_0_255)))
        return hr;
    return type.CopyTo(result);
}

HRESULT copy_event_queue(const std::mutex& mutex,
                         const ComPtr<IMFMediaEventQueue>& queue,
                         ComPtr<IMFMediaEventQueue>& result) {
    auto& mutable_mutex = const_cast<std::mutex&>(mutex);
    std::lock_guard lock(mutable_mutex);
    if (queue == nullptr) return MF_E_SHUTDOWN;
    result = queue;
    return S_OK;
}

template <typename T>
T clamp_byte(T value) noexcept {
    return std::clamp<T>(value, 0, 255);
}

struct OutputRectangle {
    UINT32 x{};
    UINT32 y{};
    UINT32 width{};
    UINT32 height{};
};

struct ScalingMaps {
    UINT32 source_width{};
    UINT32 source_height{};
    UINT32 output_width{};
    UINT32 output_height{};
    std::vector<UINT32> x;
    std::vector<UINT32> y;
};

const ScalingMaps& scaling_maps(UINT32 source_width, UINT32 source_height,
                                UINT32 output_width, UINT32 output_height) {
    static thread_local ScalingMaps maps;
    if (maps.source_width == source_width &&
        maps.source_height == source_height &&
        maps.output_width == output_width &&
        maps.output_height == output_height)
        return maps;

    maps.source_width = source_width;
    maps.source_height = source_height;
    maps.output_width = output_width;
    maps.output_height = output_height;
    maps.x.resize(output_width);
    maps.y.resize(output_height);
    for (UINT32 x = 0; x < output_width; ++x) {
        maps.x[x] = static_cast<UINT32>(
            static_cast<std::uint64_t>(x) * source_width / output_width);
    }
    for (UINT32 y = 0; y < output_height; ++y) {
        maps.y[y] = static_cast<UINT32>(
            static_cast<std::uint64_t>(y) * source_height / output_height);
    }
    return maps;
}

OutputRectangle fitted_rectangle(UINT32 source_width, UINT32 source_height,
                                 UINT32 output_width, UINT32 output_height,
                                 bool even) noexcept {
    if (source_width == 0 || source_height == 0) return {};
    UINT32 width = output_width;
    UINT32 height = static_cast<UINT32>(
        static_cast<std::uint64_t>(source_height) * output_width / source_width);
    if (height > output_height) {
        height = output_height;
        width = static_cast<UINT32>(
            static_cast<std::uint64_t>(source_width) * output_height /
            source_height);
    }
    if (even) {
        width &= ~1U;
        height &= ~1U;
    }
    width = std::max(width, even ? 2U : 1U);
    height = std::max(height, even ? 2U : 1U);
    UINT32 x = (output_width - width) / 2U;
    UINT32 y = (output_height - height) / 2U;
    if (even) {
        x &= ~1U;
        y &= ~1U;
    }
    return {x, y, width, height};
}

void render_bgra(const FrameSnapshot* frame, BYTE* output, LONG pitch,
                 UINT32 width, UINT32 height) {
    for (UINT32 y = 0; y < height; ++y) {
        BYTE* row = output + static_cast<std::ptrdiff_t>(y) * pitch;
        std::memset(row, 0, width * 4U);
        for (UINT32 x = 0; x < width; ++x)
            row[x * 4U + 3U] = 255;
    }
    if (frame == nullptr || frame->pixels.empty()) return;

    const auto rectangle = fitted_rectangle(
        frame->width, frame->height, width, height, false);
    const auto& maps = scaling_maps(frame->width, frame->height,
                                    rectangle.width, rectangle.height);
    for (UINT32 y = 0; y < rectangle.height; ++y) {
        const UINT32 source_y = maps.y[y];
        const BYTE* source_row = frame->pixels.data() +
            static_cast<std::size_t>(source_y) * frame->stride;
        BYTE* output_row = output +
            static_cast<std::ptrdiff_t>(rectangle.y + y) * pitch +
            rectangle.x * 4U;
        for (UINT32 x = 0; x < rectangle.width; ++x) {
            const UINT32 source_x = maps.x[x];
            const BYTE* source = source_row + source_x * 4U;
            BYTE* destination = output_row + x * 4U;
            destination[0] = source[0];
            destination[1] = source[1];
            destination[2] = source[2];
            destination[3] = 255;
        }
    }
}

void render_nv12(const FrameSnapshot* frame, BYTE* output, LONG pitch,
                 UINT32 width, UINT32 height) {
    BYTE* y_plane = output;
    BYTE* uv_plane = output + static_cast<std::ptrdiff_t>(pitch) * height;
    for (UINT32 y = 0; y < height; ++y)
        std::memset(y_plane + static_cast<std::ptrdiff_t>(y) * pitch, 0,
                    width);
    for (UINT32 y = 0; y < height / 2U; ++y)
        std::memset(uv_plane + static_cast<std::ptrdiff_t>(y) * pitch, 128, width);
    if (frame == nullptr || frame->pixels.empty()) return;

    const auto rectangle = fitted_rectangle(
        frame->width, frame->height, width, height, true);
    const auto& maps = scaling_maps(frame->width, frame->height,
                                    rectangle.width, rectangle.height);
    for (UINT32 y = 0; y < rectangle.height; ++y) {
        const UINT32 source_y = maps.y[y];
        const BYTE* source_row = frame->pixels.data() +
            static_cast<std::size_t>(source_y) * frame->stride;
        BYTE* destination = y_plane +
            static_cast<std::ptrdiff_t>(rectangle.y + y) * pitch + rectangle.x;
        for (UINT32 x = 0; x < rectangle.width; ++x) {
            const UINT32 source_x = maps.x[x];
            const BYTE* pixel = source_row + source_x * 4U;
            const int blue = pixel[0];
            const int green = pixel[1];
            const int red = pixel[2];
            destination[x] = static_cast<BYTE>(clamp_byte(
                (54 * red + 183 * green + 19 * blue + 128) >> 8));
        }
    }

    for (UINT32 y = 0; y < rectangle.height; y += 2U) {
        const UINT32 source_y = maps.y[y];
        const BYTE* source_row = frame->pixels.data() +
            static_cast<std::size_t>(source_y) * frame->stride;
        BYTE* destination = uv_plane +
            static_cast<std::ptrdiff_t>((rectangle.y + y) / 2U) * pitch +
            rectangle.x;
        for (UINT32 x = 0; x < rectangle.width; x += 2U) {
            const UINT32 source_x = maps.x[x];
            const BYTE* pixel = source_row + source_x * 4U;
            const int blue = pixel[0];
            const int green = pixel[1];
            const int red = pixel[2];
            destination[x] = static_cast<BYTE>(clamp_byte(
                ((-29 * red - 99 * green + 128 * blue + 128) >> 8) + 128));
            destination[x + 1U] = static_cast<BYTE>(clamp_byte(
                ((128 * red - 116 * green - 12 * blue + 128) >> 8) + 128));
        }
    }
}

HRESULT media_type_layout(IMFMediaType* media_type, GUID& subtype,
                          UINT32& width, UINT32& height, UINT32& sample_size) {
    HRESULT hr = media_type->GetGUID(MF_MT_SUBTYPE, &subtype);
    if (FAILED(hr)) return hr;
    if (subtype != MFVideoFormat_NV12 && subtype != MFVideoFormat_RGB32)
        return MF_E_UNSUPPORTED_FORMAT;
    if (FAILED(hr = MFGetAttributeSize(media_type, MF_MT_FRAME_SIZE,
                                       &width, &height)))
        return hr;
    if (width == 0 || width > MaximumFrameWidth ||
        height == 0 || height > MaximumFrameHeight ||
        (width & 1U) != 0 || (height & 1U) != 0)
        return MF_E_INVALIDMEDIATYPE;
    const auto sample_bytes = static_cast<std::uint64_t>(width) * height *
        (subtype == MFVideoFormat_NV12 ? 3U : 8U) / 2U;
    if (sample_bytes > std::numeric_limits<UINT32>::max())
        return MF_E_INVALIDMEDIATYPE;
    sample_size = static_cast<UINT32>(sample_bytes);
    return S_OK;
}

} // namespace

HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source,
                                            const wchar_t* channel_path,
                                            std::uint32_t output_width,
                                            std::uint32_t output_height,
                                            std::uint32_t frame_rate) {
    if (source == nullptr) return E_INVALIDARG;
    parent_ = source;
    if (channel_path != nullptr) channel_path_.assign(channel_path);
    if (channel_path_.empty()) discover_channel_path(channel_path_);

    HRESULT hr = MFCreateEventQueue(&event_queue_);
    if (FAILED(hr)) return hr;
    if (FAILED(hr = MFCreateAttributes(&attributes_, 8))) return hr;
    if (FAILED(hr = attributes_->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY,
                                         PINNAME_VIDEO_CAPTURE)) ||
        FAILED(hr = attributes_->SetUINT32(MF_DEVICESTREAM_STREAM_ID, StreamId)) ||
        FAILED(hr = attributes_->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED,
                                           TRUE)) ||
        FAILED(hr = attributes_->SetUINT32(
            MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
            MFFrameSourceTypes_Color)))
        return hr;

    frame_rate = std::clamp(frame_rate, 10U, 60U);
    frame_duration_100ns_ = 10'000'000LL / frame_rate;
    std::vector<std::pair<UINT32, UINT32>> sizes;
    if (output_width >= 160 && output_width <= MaximumFrameWidth &&
        output_height >= 160 && output_height <= MaximumFrameHeight &&
        (output_width & 1U) == 0 && (output_height & 1U) == 0) {
        sizes.emplace_back(output_width, output_height);
    } else {
        sizes = {{1280, 720}, {720, 1280}};
    }
    std::vector<ComPtr<IMFMediaType>> media_types;
    media_types.reserve(sizes.size() * 2U);
    for (const auto [width, height] : sizes) {
        // OBS's DirectShow bridge has historically mishandled the padded
        // NV12 surface exposed by Frame Server. Publish RGB32 only so the
        // bridge receives one tightly packed, unambiguous video layout.
        for (const GUID subtype : {MFVideoFormat_RGB32}) {
            ComPtr<IMFMediaType> type;
            if (FAILED(hr = create_media_type(
                    subtype, width, height, frame_rate, &type)))
                return hr;
            media_types.push_back(std::move(type));
        }
    }
    std::vector<IMFMediaType*> raw_types;
    raw_types.reserve(media_types.size());
    for (const auto& type : media_types) raw_types.push_back(type.Get());
    if (FAILED(hr = MFCreateStreamDescriptor(
            StreamId, static_cast<DWORD>(raw_types.size()), raw_types.data(),
            &descriptor_)))
        return hr;
    if (FAILED(hr = attributes_->CopyAllItems(descriptor_.Get()))) return hr;

    ComPtr<IMFMediaTypeHandler> handler;
    if (FAILED(hr = descriptor_->GetMediaTypeHandler(&handler)) ||
        FAILED(hr = handler->SetCurrentMediaType(media_types.front().Get())))
        return hr;

    // Activation must remain successful when the producer is between frames.
    // RequestSample retries opening and emits black until the WPF publisher is
    // available.
    if (!channel_path_.empty()) reader_.open(channel_path_.c_str());
    sample_worker_ = std::jthread(
        [this](std::stop_token token) { process_sample_requests(token); });
    return S_OK;
}

HRESULT MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->BeginGetEvent(callback, state);
}

HRESULT MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->EndGetEvent(result, event);
}

HRESULT MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->GetEvent(flags, event);
}

HRESULT MediaStream::QueueEvent(MediaEventType type, REFGUID extended_type,
                                HRESULT status,
                                const PROPVARIANT* event_value) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr
                      : queue->QueueEventParamVar(type, extended_type, status,
                                                 event_value);
}

HRESULT MediaStream::GetMediaSource(IMFMediaSource** source) {
    if (source == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    return parent_.CopyTo(source);
}

HRESULT MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor) {
    if (descriptor == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    return descriptor_.CopyTo(descriptor);
}

HRESULT MediaStream::RequestSample(IUnknown* token) {
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    if (state_ != MF_STREAM_STATE_RUNNING || !selected_)
        return MF_E_INVALIDREQUEST;
    if (sample_requests_.size() >= MaximumPendingSampleRequests)
        return MF_E_NOTACCEPTING;
    try {
        sample_requests_.emplace_back(token);
    } catch (...) {
        return E_OUTOFMEMORY;
    }
    sample_condition_.notify_one();
    return S_OK;
}

void MediaStream::process_sample_requests(
    std::stop_token stop_token) noexcept {
    std::unique_lock lock(mutex_);
    while (!stop_token.stop_requested()) {
        const bool ready = sample_condition_.wait(lock, stop_token, [this] {
            return shutdown_ ||
                (state_ == MF_STREAM_STATE_RUNNING && selected_ &&
                 !sample_requests_.empty());
        });
        if (!ready || shutdown_ || stop_token.stop_requested()) break;

        LONGLONG system_time = MFGetSystemTime();
        if (next_sample_time_ == 0 ||
            system_time > next_sample_time_ + frame_duration_100ns_)
            next_sample_time_ = system_time;
        if (system_time < next_sample_time_) {
            const auto delay = std::chrono::nanoseconds(
                (next_sample_time_ - system_time) * 100);
            const bool interrupted = sample_condition_.wait_for(
                lock, stop_token, delay, [this] {
                    return shutdown_ || state_ != MF_STREAM_STATE_RUNNING ||
                        !selected_;
                });
            if (interrupted || stop_token.stop_requested()) continue;
        }
        if (sample_requests_.empty()) continue;

        ComPtr<IUnknown> token = std::move(sample_requests_.front());
        sample_requests_.pop_front();
        const LONGLONG sample_time = next_sample_time_;
        next_sample_time_ += frame_duration_100ns_;
        const auto generation = stream_generation_;
        ComPtr<IMFVideoSampleAllocator> allocator = allocator_;
        ComPtr<IMFMediaType> media_type = media_type_;

        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buffer;
        lock.unlock();
        HRESULT hr = allocate_sample(allocator.Get(), media_type.Get(),
            &sample, &buffer);
        if (SUCCEEDED(hr))
            hr = render_frame(buffer.Get(), media_type.Get(), stop_token);
        if (SUCCEEDED(hr)) hr = sample->SetSampleTime(sample_time);
        if (SUCCEEDED(hr))
            hr = sample->SetSampleDuration(frame_duration_100ns_);
        if (SUCCEEDED(hr))
            hr = sample->SetUINT64(
                MFSampleExtension_DeviceTimestamp,
                static_cast<UINT64>(sample_time));
        if (SUCCEEDED(hr) && token != nullptr)
            hr = sample->SetUnknown(MFSampleExtension_Token, token.Get());
        lock.lock();
        if (shutdown_ || stop_token.stop_requested() ||
            generation != stream_generation_ ||
            state_ != MF_STREAM_STATE_RUNNING || !selected_)
            continue;
        if (SUCCEEDED(hr)) {
            hr = event_queue_->QueueEventParamUnk(
                MEMediaSample, GUID_NULL, S_OK, sample.Get());
        }
        if (FAILED(hr) && event_queue_ != nullptr)
            event_queue_->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
    }
}

HRESULT MediaStream::SetStreamState(MF_STREAM_STATE state) {
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    if (state_ == state) return S_OK;
    switch (state) {
    case MF_STREAM_STATE_RUNNING:
        return start_locked(nullptr, false);
    case MF_STREAM_STATE_STOPPED:
        return stop_locked(false);
    case MF_STREAM_STATE_PAUSED:
        if (state_ != MF_STREAM_STATE_RUNNING)
            return MF_E_INVALID_STATE_TRANSITION;
        state_ = state;
        return S_OK;
    default:
        return E_INVALIDARG;
    }
}

HRESULT MediaStream::GetStreamState(MF_STREAM_STATE* state) {
    if (state == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    *state = state_;
    return S_OK;
}

HRESULT MediaStream::start(IMFMediaType* media_type, bool send_event) {
    std::lock_guard lock(mutex_);
    return start_locked(media_type, send_event);
}

HRESULT MediaStream::stop(bool send_event) {
    std::lock_guard lock(mutex_);
    return stop_locked(send_event);
}

HRESULT MediaStream::shutdown() {
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) return S_OK;
        shutdown_ = true;
        state_ = MF_STREAM_STATE_STOPPED;
        selected_ = false;
        sample_requests_.clear();
    }
    sample_worker_.request_stop();
    sample_condition_.notify_all();
    if (sample_worker_.joinable()) sample_worker_.join();

    std::lock_guard lock(mutex_);
    reader_.close();
    last_frame_ = {};
    pending_frame_ = {};
    rendered_frame_.clear();
    allocator_.Reset();
    media_type_.Reset();
    descriptor_.Reset();
    attributes_.Reset();
    parent_.Reset();
    if (event_queue_ != nullptr) event_queue_->Shutdown();
    event_queue_.Reset();
    return S_OK;
}

HRESULT MediaStream::set_sample_allocator(IMFVideoSampleAllocator* allocator) {
    if (allocator == nullptr) return E_INVALIDARG;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    if (state_ == MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
    allocator_ = allocator;
    return S_OK;
}

HRESULT MediaStream::copy_attributes(IMFAttributes** attributes) {
    if (attributes == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    return attributes_.CopyTo(attributes);
}

HRESULT MediaStream::check_shutdown_locked() const noexcept {
    return shutdown_ || event_queue_ == nullptr ? MF_E_SHUTDOWN : S_OK;
}

HRESULT MediaStream::start_locked(IMFMediaType* media_type, bool send_event) {
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    HRESULT hr = S_OK;
    if (media_type != nullptr) {
        media_type_ = media_type;
    } else if (media_type_ == nullptr) {
        ComPtr<IMFMediaTypeHandler> handler;
        if (FAILED(hr = descriptor_->GetMediaTypeHandler(&handler)) ||
            FAILED(hr = handler->GetCurrentMediaType(&media_type_)))
            return hr;
    }
    if (allocator_ != nullptr) {
        std::scoped_lock allocator_lock(sample_allocator_mutex_);
        if (FAILED(hr = allocator_->InitializeSampleAllocator(
                SamplePoolSize, media_type_.Get())))
            return hr;
    }
    selected_ = true;
    next_sample_time_ = 0;
    ++stream_generation_;
    state_ = MF_STREAM_STATE_RUNNING;
    sample_condition_.notify_all();
    return send_event
        ? event_queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK,
                                           nullptr)
        : S_OK;
}

HRESULT MediaStream::stop_locked(bool send_event) {
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    state_ = MF_STREAM_STATE_STOPPED;
    selected_ = false;
    sample_requests_.clear();
    next_sample_time_ = 0;
    ++stream_generation_;
    sample_condition_.notify_all();
    return send_event
        ? event_queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK,
                                           nullptr)
        : S_OK;
}

HRESULT MediaStream::allocate_sample(IMFVideoSampleAllocator* allocator,
                                     IMFMediaType* media_type,
                                     IMFSample** sample,
                                     IMFMediaBuffer** buffer) {
    if (sample == nullptr || buffer == nullptr) return E_POINTER;
    *sample = nullptr;
    *buffer = nullptr;

    ComPtr<IMFSample> allocated_sample;
    HRESULT hr = S_OK;
    if (allocator != nullptr) {
        std::scoped_lock allocator_lock(sample_allocator_mutex_);
        hr = allocator->AllocateSample(&allocated_sample);
    } else {
        hr = MFCreateSample(&allocated_sample);
        if (SUCCEEDED(hr)) {
            GUID subtype{};
            UINT32 width{}, height{}, bytes{};
            hr = media_type_layout(media_type, subtype, width, height,
                                   bytes);
            ComPtr<IMFMediaBuffer> allocated_buffer;
            if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(bytes, &allocated_buffer);
            if (SUCCEEDED(hr)) hr = allocated_sample->AddBuffer(allocated_buffer.Get());
        }
    }
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaBuffer> allocated_buffer;
    if (FAILED(hr = allocated_sample->GetBufferByIndex(0, &allocated_buffer)))
        return hr;
    if (FAILED(hr = allocated_sample.CopyTo(sample))) return hr;
    return allocated_buffer.CopyTo(buffer);
}

HRESULT MediaStream::render_frame(IMFMediaBuffer* buffer,
                                  IMFMediaType* media_type,
                                  std::stop_token stop_token) {
    if (buffer == nullptr || media_type == nullptr) return E_INVALIDARG;
    GUID subtype{};
    UINT32 width{}, height{}, sample_size{};
    HRESULT hr = media_type_layout(media_type, subtype, width, height,
                                   sample_size);
    if (FAILED(hr)) return hr;

    const auto try_read_frame = [this]() {
        if (!reader_.is_open()) {
            if (channel_path_.empty()) discover_channel_path(channel_path_);
            if (!channel_path_.empty() &&
                FAILED(reader_.open(channel_path_.c_str())))
                channel_path_.clear();
        }
        return reader_.read(pending_frame_);
    };
    bool received_frame = try_read_frame();
    if (!received_frame && last_frame_.pixels.empty()) {
        const auto deadline = std::chrono::steady_clock::now() + FirstFrameWait;
        while (!received_frame && !stop_token.stop_requested() &&
               std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(FirstFrameRetryInterval);
            received_frame = try_read_frame();
        }
    }
    if (stop_token.stop_requested()) return MF_E_SHUTDOWN;
    if (received_frame) std::swap(last_frame_, pending_frame_);
    // Reuse the previous snapshot whenever the publisher is briefly between
    // valid frames instead of exposing transport timing as a black sample.
    const FrameSnapshot* frame = last_frame_.pixels.empty()
        ? nullptr : &last_frame_;

    ComPtr<IMF2DBuffer2> buffer_2d;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&buffer_2d)))) {
        BYTE* scanline{};
        BYTE* buffer_start{};
        LONG pitch{};
        DWORD buffer_length{};
        if (FAILED(hr = buffer_2d->Lock2DSize(
                MF2DBuffer_LockFlags_Write, &scanline, &pitch,
                &buffer_start, &buffer_length)))
            return hr;

        const auto row_bytes = static_cast<std::uint64_t>(width) *
            (subtype == MFVideoFormat_NV12 ? 1U : 4U);
        const auto absolute_pitch = static_cast<std::uint64_t>(
            pitch < 0 ? -static_cast<std::int64_t>(pitch) : pitch);
        const auto required_bytes = absolute_pitch * height;
        if (scanline == nullptr || pitch <= 0 || absolute_pitch < row_bytes ||
            required_bytes > buffer_length) {
            buffer_2d->Unlock2D();
            return MF_E_BUFFERTOOSMALL;
        }

        // Write directly into the allocator's rows. ContiguousCopyFrom is
        // not reliable for the padded RGB32 surfaces used by Frame Server;
        // the returned pitch is the only authoritative row layout.
        if (subtype == MFVideoFormat_NV12)
            render_nv12(frame, scanline, pitch, width, height);
        else
            render_bgra(frame, scanline, pitch, width, height);
        hr = buffer_2d->Unlock2D();
        if (SUCCEEDED(hr)) hr = buffer->SetCurrentLength(sample_size);
        return hr;
    }

    BYTE* data{};
    DWORD maximum{}, current{};
    if (FAILED(hr = buffer->Lock(&data, &maximum, &current))) return hr;
    if (maximum < sample_size) {
        buffer->Unlock();
        return MF_E_BUFFERTOOSMALL;
    }
    const LONG pitch = subtype == MFVideoFormat_NV12
        ? static_cast<LONG>(width) : static_cast<LONG>(width * 4U);
    if (subtype == MFVideoFormat_NV12)
        render_nv12(frame, data, pitch, width, height);
    else
        render_bgra(frame, data, pitch, width, height);
    hr = buffer->Unlock();
    if (SUCCEEDED(hr)) hr = buffer->SetCurrentLength(sample_size);
    return hr;
}

HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activation_attributes) {
    HRESULT hr = MFCreateAttributes(&attributes_, 8);
    if (FAILED(hr)) return hr;
    if (activation_attributes != nullptr &&
        FAILED(hr = activation_attributes->CopyAllItems(attributes_.Get())))
        return hr;
    if (FAILED(hr = MFCreateEventQueue(&event_queue_))) return hr;

    wchar_t channel_path[512]{};
    UINT32 channel_path_length{};
    if (activation_attributes == nullptr ||
        FAILED(activation_attributes->GetString(
            FrameChannelPathAttribute, channel_path,
            static_cast<UINT32>(std::size(channel_path)),
            &channel_path_length)))
        channel_path[0] = L'\0';

    UINT32 output_width{};
    UINT32 output_height{};
    UINT32 frame_rate = 30;
    if (activation_attributes != nullptr) {
        activation_attributes->GetUINT32(OutputWidthAttribute, &output_width);
        activation_attributes->GetUINT32(OutputHeightAttribute, &output_height);
        activation_attributes->GetUINT32(OutputFrameRateAttribute, &frame_rate);
    }

    if (FAILED(hr = Microsoft::WRL::MakeAndInitialize<MediaStream>(
            &stream_, this, channel_path, output_width, output_height,
            frame_rate)))
        return hr;
    ComPtr<IMFStreamDescriptor> stream_descriptor;
    if (FAILED(hr = stream_->GetStreamDescriptor(&stream_descriptor))) return hr;
    IMFStreamDescriptor* descriptors[]{stream_descriptor.Get()};
    if (FAILED(hr = MFCreatePresentationDescriptor(1, descriptors,
                                                    &descriptor_)))
        return hr;
    descriptor_->SelectStream(0);
    state_ = State::stopped;
    return S_OK;
}

HRESULT MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->BeginGetEvent(callback, state);
}

HRESULT MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->EndGetEvent(result, event);
}

HRESULT MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr : queue->GetEvent(flags, event);
}

HRESULT MediaSource::QueueEvent(MediaEventType type, REFGUID extended_type,
                                HRESULT status,
                                const PROPVARIANT* event_value) {
    ComPtr<IMFMediaEventQueue> queue;
    HRESULT hr = copy_event_queue(mutex_, event_queue_, queue);
    return FAILED(hr) ? hr
                      : queue->QueueEventParamVar(type, extended_type, status,
                                                 event_value);
}

HRESULT MediaSource::GetCharacteristics(DWORD* characteristics) {
    if (characteristics == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

HRESULT MediaSource::CreatePresentationDescriptor(
    IMFPresentationDescriptor** descriptor) {
    if (descriptor == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    return descriptor_->Clone(descriptor);
}

HRESULT MediaSource::Start(IMFPresentationDescriptor* presentation_descriptor,
                           const GUID* time_format,
                           const PROPVARIANT* start_position) {
    if (presentation_descriptor == nullptr || start_position == nullptr)
        return E_INVALIDARG;
    if (time_format != nullptr && *time_format != GUID_NULL)
        return MF_E_UNSUPPORTED_TIME_FORMAT;

    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;

    DWORD stream_count{};
    HRESULT hr = presentation_descriptor->GetStreamDescriptorCount(&stream_count);
    if (FAILED(hr)) return hr;
    if (stream_count != 1) return E_INVALIDARG;

    BOOL selected{};
    ComPtr<IMFStreamDescriptor> selected_descriptor;
    if (FAILED(hr = presentation_descriptor->GetStreamDescriptorByIndex(
            0, &selected, &selected_descriptor)))
        return hr;
    if (!selected) return MF_E_INVALIDREQUEST;

    ComPtr<IMFMediaTypeHandler> selected_handler;
    ComPtr<IMFMediaType> selected_type;
    if (FAILED(hr = selected_descriptor->GetMediaTypeHandler(&selected_handler)) ||
        FAILED(hr = selected_handler->GetCurrentMediaType(&selected_type)))
        return hr;

    ComPtr<IMFStreamDescriptor> own_descriptor;
    BOOL own_selected{};
    if (FAILED(hr = descriptor_->GetStreamDescriptorByIndex(
            0, &own_selected, &own_descriptor)))
        return hr;
    ComPtr<IMFMediaTypeHandler> own_handler;
    if (FAILED(hr = own_descriptor->GetMediaTypeHandler(&own_handler)) ||
        FAILED(hr = own_handler->SetCurrentMediaType(selected_type.Get())))
        return hr;
    descriptor_->SelectStream(0);

    ComPtr<IUnknown> stream_unknown;
    if (FAILED(hr = stream_.As(&stream_unknown))) return hr;
    if (FAILED(hr = event_queue_->QueueEventParamUnk(
            announced_stream_ ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
            stream_unknown.Get())))
        return hr;
    announced_stream_ = true;
    if (FAILED(hr = stream_->start(selected_type.Get(), true))) return hr;

    PROPVARIANT start_time{};
    InitPropVariantFromInt64(MFGetSystemTime(), &start_time);
    hr = event_queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK,
                                          &start_time);
    PropVariantClear(&start_time);
    if (FAILED(hr)) return hr;
    state_ = State::started;
    return S_OK;
}

HRESULT MediaSource::Stop() {
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    if (state_ != State::started) return MF_E_INVALID_STATE_TRANSITION;
    HRESULT hr = stream_->stop(true);
    if (FAILED(hr)) return hr;
    descriptor_->DeselectStream(0);
    PROPVARIANT stop_time{};
    InitPropVariantFromInt64(MFGetSystemTime(), &stop_time);
    hr = event_queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK,
                                          &stop_time);
    PropVariantClear(&stop_time);
    if (SUCCEEDED(hr)) state_ = State::stopped;
    return hr;
}

HRESULT MediaSource::Pause() { return MF_E_INVALID_STATE_TRANSITION; }

HRESULT MediaSource::Shutdown() {
    ComPtr<MediaStream> stream;
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (state_ == State::shutdown) return S_OK;
        state_ = State::shutdown;
        stream = stream_;
        queue = event_queue_;
        stream_.Reset();
        descriptor_.Reset();
        attributes_.Reset();
        event_queue_.Reset();
    }
    if (stream != nullptr) stream->shutdown();
    if (queue != nullptr) queue->Shutdown();
    return S_OK;
}

HRESULT MediaSource::GetSourceAttributes(IMFAttributes** attributes) {
    if (attributes == nullptr) return E_POINTER;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    return attributes_.CopyTo(attributes);
}

HRESULT MediaSource::GetStreamAttributes(DWORD stream_identifier,
                                         IMFAttributes** attributes) {
    if (stream_identifier != StreamId) return MF_E_NOT_FOUND;
    ComPtr<MediaStream> stream;
    {
        std::lock_guard lock(mutex_);
        if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
        stream = stream_;
    }
    return stream->copy_attributes(attributes);
}

HRESULT MediaSource::SetD3DManager(IUnknown*) { return S_OK; }

HRESULT MediaSource::GetService(REFGUID, REFIID, void** object) {
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

HRESULT MediaSource::KsProperty(PKSPROPERTY, ULONG, void*, ULONG,
                                ULONG* bytes_returned) {
    if (bytes_returned != nullptr) *bytes_returned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

HRESULT MediaSource::KsMethod(PKSMETHOD, ULONG, void*, ULONG,
                              ULONG* bytes_returned) {
    if (bytes_returned != nullptr) *bytes_returned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

HRESULT MediaSource::KsEvent(PKSEVENT, ULONG, void*, ULONG,
                             ULONG* bytes_returned) {
    if (bytes_returned != nullptr) *bytes_returned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

HRESULT MediaSource::SetDefaultAllocator(DWORD output_stream_id,
                                         IUnknown* allocator) {
    if (output_stream_id != StreamId) return MF_E_NOT_FOUND;
    if (allocator == nullptr) return E_INVALIDARG;
    ComPtr<IMFVideoSampleAllocator> sample_allocator;
    HRESULT hr = allocator->QueryInterface(IID_PPV_ARGS(&sample_allocator));
    if (FAILED(hr)) return hr;
    ComPtr<MediaStream> stream;
    {
        std::lock_guard lock(mutex_);
        if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
        stream = stream_;
    }
    return stream->set_sample_allocator(sample_allocator.Get());
}

HRESULT MediaSource::GetAllocatorUsage(DWORD output_stream_id,
                                       DWORD* input_stream_id,
                                       MFSampleAllocatorUsage* usage) {
    if (input_stream_id == nullptr || usage == nullptr) return E_POINTER;
    if (output_stream_id != StreamId) return MF_E_NOT_FOUND;
    std::lock_guard lock(mutex_);
    if (FAILED(check_shutdown_locked())) return MF_E_SHUTDOWN;
    *input_stream_id = StreamId;
    *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
    return S_OK;
}

HRESULT MediaSource::check_shutdown_locked() const noexcept {
    return state_ == State::shutdown || event_queue_ == nullptr
        ? MF_E_SHUTDOWN : S_OK;
}

} // namespace iPhoneMirror::virtual_camera
