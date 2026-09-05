#include "FrameExchange.h"
#include "VirtualCameraShared.h"

#include <mfapi.h>
#include <mfidl.h>
#include <wrl.h>
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <span>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace iPhoneMirror::virtual_camera;

namespace {

int failures{};

void check(bool condition, const char* message) {
    if (condition) return;
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
}

void check_hr(HRESULT result, const char* message) {
    if (SUCCEEDED(result)) return;
    std::fprintf(stderr, "FAIL: %s (0x%08X)\n", message,
                 static_cast<unsigned>(result));
    ++failures;
}

std::filesystem::path module_directory() {
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(nullptr, path.data(),
                                            static_cast<DWORD>(path.size()));
    return std::filesystem::path(std::wstring(path.data(), length)).parent_path();
}

void test_control_argument_validation() {
    const auto dll_path = module_directory() / L"iPhoneMirror.VirtualCamera.dll";
    HMODULE module = LoadLibraryW(dll_path.c_str());
    check(module != nullptr, "load virtual camera DLL for API validation");
    if (module == nullptr) return;

    using StartEx = std::int32_t (__cdecl*)(
        const wchar_t*, std::uint32_t, std::uint32_t, std::uint32_t);
    const auto start_ex = reinterpret_cast<StartEx>(
        GetProcAddress(module, "im_vcam_start_ex"));
    check(start_ex != nullptr, "export extended virtual camera start API");
    if (start_ex != nullptr) {
        const auto invalid_argument = static_cast<std::int32_t>(E_INVALIDARG);
        check(start_ex(nullptr, MaximumFrameWidth + 2U, 2160, 30) ==
                  invalid_argument,
              "reject output width above the allocation limit");
        check(start_ex(nullptr, 3840, MaximumFrameHeight + 2U, 30) ==
                  invalid_argument,
              "reject output height above the allocation limit");
        check(start_ex(nullptr, 1279, 720, 30) == invalid_argument,
              "reject odd output dimensions");
        check(start_ex(nullptr, 1280, 720, 61) == invalid_argument,
              "reject frame rates above the supported limit");
    }
    FreeLibrary(module);
}

template <typename Interface>
ComPtr<Interface> event_unknown(IMFMediaEvent* event) {
    PROPVARIANT value{};
    PropVariantInit(&value);
    ComPtr<Interface> result;
    if (SUCCEEDED(event->GetValue(&value)) && value.vt == VT_UNKNOWN &&
        value.punkVal != nullptr)
        value.punkVal->QueryInterface(IID_PPV_ARGS(&result));
    PropVariantClear(&value);
    return result;
}

bool sample_has_nonblack_luma(IMFSample* sample, UINT32 width,
                              UINT32 height) {
    if (sample == nullptr) return false;
    ComPtr<IMFMediaBuffer> buffer;
    if (FAILED(sample->GetBufferByIndex(0, &buffer))) return false;
    ComPtr<IMF2DBuffer2> buffer_2d;
    BYTE* scanline{};
    BYTE* start{};
    LONG pitch{};
    DWORD length{};
    const bool is_2d = SUCCEEDED(buffer.As(&buffer_2d));
    if (is_2d) {
        if (FAILED(buffer_2d->Lock2DSize(MF2DBuffer_LockFlags_Read,
                                         &scanline, &pitch, &start, &length)))
            return false;
    } else {
        DWORD current{};
        if (FAILED(buffer->Lock(&scanline, &length, &current))) return false;
        pitch = static_cast<LONG>(width);
    }
    const bool valid_layout = scanline != nullptr && pitch > 0 &&
        static_cast<std::uint64_t>(pitch) * height <= length;
    bool nonblack{};
    if (valid_layout) {
        const std::array<UINT32, 3> rows{height / 4U, height / 2U,
                                         height * 3U / 4U};
        const std::array<UINT32, 3> columns{width / 4U, width / 2U,
                                            width * 3U / 4U};
        for (const UINT32 y : rows) {
            const BYTE* row = scanline + static_cast<std::ptrdiff_t>(y) * pitch;
            for (const UINT32 x : columns)
                nonblack = nonblack || row[x] != 0;
        }
    }
    if (is_2d) buffer_2d->Unlock2D();
    else buffer->Unlock();
    return nonblack;
}

void test_frame_exchange(FramePublisher& publisher) {
    check_hr(publisher.open_for_current_user(), "open frame publisher");
    if (publisher.channel_path().empty()) return;
    check(publisher.channel_path().find(L"iPhoneMirror\\FrameChannels") !=
              std::wstring::npos,
          "place the frame channel in a service-accessible shared directory");

    FrameReader reader;
    check_hr(reader.open(publisher.channel_path().c_str()), "open frame reader");
    constexpr UINT32 width = 4;
    constexpr UINT32 height = 2;
    constexpr UINT32 stride = width * 4;
    std::array<std::uint8_t, stride * height> pixels{};
    for (std::size_t index = 0; index < pixels.size(); index += 4) {
        pixels[index] = 255;
        pixels[index + 3] = 255;
    }
    check_hr(publisher.publish(pixels.data(), width, height, stride, 123456),
             "publish frame");
    FrameSnapshot snapshot;
    check(reader.read(snapshot), "read stable frame snapshot");
    check(snapshot.width == width && snapshot.height == height,
          "preserve frame dimensions");
    check(snapshot.timestamp_100ns == 123456, "preserve frame timestamp");
    check(snapshot.pixels == std::vector<std::uint8_t>(pixels.begin(), pixels.end()),
          "preserve frame pixels");
}

void test_frame_exchange_contention(FramePublisher& publisher) {
    FrameReader reader;
    check_hr(reader.open(publisher.channel_path().c_str()),
             "open contended frame reader");
    if (!reader.is_open()) return;

    constexpr UINT32 width = 1920;
    constexpr UINT32 height = 1080;
    constexpr UINT32 stride = width * 4;
    constexpr int frame_count = 90;
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(stride) * height);
    std::atomic_bool writer_started{};
    std::atomic_bool writer_finished{};
    std::atomic_bool writer_failed{};
    std::jthread writer([&] {
        for (int frame = 1; frame <= frame_count; ++frame) {
            std::fill(pixels.begin(), pixels.end(),
                      static_cast<std::uint8_t>(frame));
            if (FAILED(publisher.publish(pixels.data(), width, height, stride,
                                         frame * 10'000LL))) {
                writer_failed = true;
                writer_started = true;
                break;
            }
            if (frame == 1) writer_started = true;
            std::this_thread::yield();
        }
        writer_finished = true;
    });

    while (!writer_started) std::this_thread::yield();
    std::size_t snapshots{};
    std::size_t snapshots_during_writes{};
    std::size_t read_failures{};
    bool saw_torn_frame{};
    const auto deadline = std::chrono::steady_clock::now() +
                          std::chrono::seconds(15);
    while ((!writer_finished || snapshots < 8) &&
           std::chrono::steady_clock::now() < deadline) {
        const bool writing = !writer_finished;
        FrameSnapshot snapshot;
        if (!reader.read(snapshot)) {
            ++read_failures;
            continue;
        }
        ++snapshots;
        if (writing) ++snapshots_during_writes;
        if (snapshot.pixels.size() !=
                static_cast<std::size_t>(stride) * height ||
            snapshot.pixels.empty() ||
            !std::all_of(snapshot.pixels.begin(), snapshot.pixels.end(),
                         [&](std::uint8_t value) {
                             return value == snapshot.pixels.front();
                         })) {
            saw_torn_frame = true;
            break;
        }
    }
    writer.join();

    check(!writer_failed, "publish every frame during contention stress");
    check(read_failures == 0,
          "read every snapshot while the publisher is writing");
    check(snapshots_during_writes > 0,
          "read snapshots concurrently with frame publication");
    check(!saw_torn_frame,
          "never expose a partially overwritten frame snapshot");
}

void test_media_source() {
    const auto dll_path = module_directory() / L"iPhoneMirror.VirtualCamera.dll";
    HMODULE module = LoadLibraryW(dll_path.c_str());
    check(module != nullptr, "load virtual camera DLL");
    if (module == nullptr) return;

    using GetClassObject = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    const auto get_class_object = reinterpret_cast<GetClassObject>(
        GetProcAddress(module, "DllGetClassObject"));
    check(get_class_object != nullptr, "export DllGetClassObject");
    if (get_class_object == nullptr) {
        FreeLibrary(module);
        return;
    }

    ComPtr<IClassFactory> factory;
    check_hr(get_class_object(MediaSourceClsid, IID_PPV_ARGS(&factory)),
             "create media source class factory");
    ComPtr<IMFActivate> activate;
    if (factory != nullptr)
        check_hr(factory->CreateInstance(nullptr, IID_PPV_ARGS(&activate)),
                 "create media source activation object");
    ComPtr<IMFMediaSource> source;
    if (activate != nullptr)
        check_hr(activate->ActivateObject(IID_PPV_ARGS(&source)),
                  "activate media source");
    ComPtr<IMFPresentationDescriptor> presentation;
    if (source != nullptr)
        check_hr(source->CreatePresentationDescriptor(&presentation),
                 "create presentation descriptor");
    DWORD stream_count{};
    if (presentation != nullptr)
        check_hr(presentation->GetStreamDescriptorCount(&stream_count),
                 "read stream count");
    check(stream_count == 1, "media source exposes one stream");

    BOOL selected{};
    ComPtr<IMFStreamDescriptor> descriptor;
    if (presentation != nullptr)
        check_hr(presentation->GetStreamDescriptorByIndex(
                     0, &selected, &descriptor),
                 "read stream descriptor");
    ComPtr<IMFMediaTypeHandler> handler;
    DWORD media_type_count{};
    if (descriptor != nullptr &&
        SUCCEEDED(descriptor->GetMediaTypeHandler(&handler)))
        check_hr(handler->GetMediaTypeCount(&media_type_count),
                 "read media type count");
    check(media_type_count == 2, "stream exposes RGB32 landscape/portrait");

    PROPVARIANT start_position{};
    PropVariantInit(&start_position);
    if (source != nullptr && presentation != nullptr)
        check_hr(source->Start(presentation.Get(), nullptr, &start_position),
                  "start media source");

    ComPtr<IMFMediaEvent> source_event;
    ComPtr<IMFMediaStream> stream;
    if (source != nullptr &&
        SUCCEEDED(source->GetEvent(MF_EVENT_FLAG_NO_WAIT, &source_event)))
        stream = event_unknown<IMFMediaStream>(source_event.Get());
    check(stream != nullptr, "source announces media stream");

    ComPtr<IMFMediaEvent> stream_event;
    if (stream != nullptr)
        check_hr(stream->GetEvent(MF_EVENT_FLAG_NO_WAIT, &stream_event),
                 "stream emits start event");
    MediaEventType event_type = MEUnknown;
    if (stream_event != nullptr) stream_event->GetType(&event_type);
    check(event_type == MEStreamStarted, "first stream event is MEStreamStarted");

    if (stream != nullptr)
        check_hr(stream->RequestSample(nullptr), "request media sample");
    stream_event.Reset();
    if (stream != nullptr)
        check_hr(stream->GetEvent(0, &stream_event),
                 "stream emits media sample");
    event_type = MEUnknown;
    if (stream_event != nullptr) stream_event->GetType(&event_type);
    check(event_type == MEMediaSample, "sample event is MEMediaSample");
    auto sample = stream_event == nullptr
        ? ComPtr<IMFSample>{} : event_unknown<IMFSample>(stream_event.Get());
    check(sample != nullptr, "sample event carries IMFSample");
    DWORD buffer_count{};
    if (sample != nullptr) sample->GetBufferCount(&buffer_count);
    check(buffer_count == 1, "sample contains one video buffer");
    ComPtr<IMFMediaBuffer> sample_buffer;
    if (sample != nullptr)
        check_hr(sample->ConvertToContiguousBuffer(&sample_buffer),
                 "read contiguous video sample");
    BYTE* sample_bytes{};
    DWORD maximum_length{}, current_length{};
    if (sample_buffer != nullptr)
        check_hr(sample_buffer->Lock(&sample_bytes, &maximum_length,
                                     &current_length),
                 "lock video sample");
    bool contains_published_pixels{};
    if (sample_bytes != nullptr && current_length >= 1280U * 720U) {
        // The default type is NV12. A black fallback has luma 0 everywhere;
        // the bright-blue test frame must change pixels in the fitted region.
        const auto luma = std::span(sample_bytes, 1280U * 720U);
        contains_published_pixels = std::any_of(
            luma.begin(), luma.end(), [](BYTE value) { return value != 0; });
    }
    check(contains_published_pixels,
          "media source discovers and renders the published frame without activation attributes");
    if (sample_buffer != nullptr && sample_bytes != nullptr) sample_buffer->Unlock();

    if (source != nullptr) {
        check_hr(source->Stop(), "stop media source");
        check_hr(source->Shutdown(), "shutdown media source");
    }
    sample.Reset();
    sample_buffer.Reset();
    stream_event.Reset();
    stream.Reset();
    source_event.Reset();
    handler.Reset();
    descriptor.Reset();
    presentation.Reset();
    source.Reset();
    activate.Reset();
    factory.Reset();
    FreeLibrary(module);
}

void test_configured_media_source(FramePublisher& publisher) {
    constexpr UINT32 published_width = 998;
    constexpr UINT32 published_height = 2160;
    constexpr UINT32 published_stride = published_width * 4U;
    std::vector<std::uint8_t> published_pixels(
        static_cast<std::size_t>(published_stride) * published_height, 90);
    check_hr(publisher.publish(published_pixels.data(), published_width,
                               published_height, published_stride, 654321),
             "publish portrait frame for allocator stress");

    const auto dll_path = module_directory() / L"iPhoneMirror.VirtualCamera.dll";
    HMODULE module = LoadLibraryW(dll_path.c_str());
    check(module != nullptr, "load virtual camera DLL for configured media type");
    if (module == nullptr) return;

    using GetClassObject = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    const auto get_class_object = reinterpret_cast<GetClassObject>(
        GetProcAddress(module, "DllGetClassObject"));
    ComPtr<IClassFactory> factory;
    if (get_class_object != nullptr)
        check_hr(get_class_object(MediaSourceClsid, IID_PPV_ARGS(&factory)),
                 "create configured media source class factory");
    ComPtr<IMFActivate> activate;
    if (factory != nullptr)
        check_hr(factory->CreateInstance(nullptr, IID_PPV_ARGS(&activate)),
                 "create configured activation object");
    if (activate != nullptr) {
        check_hr(activate->SetUINT32(OutputWidthAttribute, 998),
                  "set virtual camera output width");
        check_hr(activate->SetUINT32(OutputHeightAttribute, 2160),
                  "set virtual camera output height");
        check_hr(activate->SetUINT32(OutputFrameRateAttribute, 60),
                 "set virtual camera frame rate");
    }

    ComPtr<IMFMediaSource> source;
    if (activate != nullptr)
        check_hr(activate->ActivateObject(IID_PPV_ARGS(&source)),
                  "activate configured media source");
    ComPtr<IMFSampleAllocatorControl> allocator_control;
    ComPtr<IMFVideoSampleAllocator> sample_allocator;
    if (source != nullptr)
        check_hr(source.As(&allocator_control),
                 "query configured source allocator control");
    if (allocator_control != nullptr) {
        DWORD input_stream{};
        MFSampleAllocatorUsage usage{};
        check_hr(allocator_control->GetAllocatorUsage(0, &input_stream,
                                                       &usage),
                 "read configured allocator usage");
        check(usage == MFSampleAllocatorUsage_UsesProvidedAllocator,
              "source accepts the Frame Server sample allocator");
    }
    check_hr(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&sample_allocator)),
             "create Frame Server style video allocator");
    if (allocator_control != nullptr && sample_allocator != nullptr)
        check_hr(allocator_control->SetDefaultAllocator(0,
                                                        sample_allocator.Get()),
                 "provide Frame Server style video allocator");
    ComPtr<IMFPresentationDescriptor> presentation;
    if (source != nullptr)
        check_hr(source->CreatePresentationDescriptor(&presentation),
                 "create configured presentation descriptor");
    BOOL selected{};
    ComPtr<IMFStreamDescriptor> descriptor;
    if (presentation != nullptr)
        check_hr(presentation->GetStreamDescriptorByIndex(
                     0, &selected, &descriptor),
                 "read configured stream descriptor");
    ComPtr<IMFMediaTypeHandler> handler;
    DWORD media_type_count{};
    if (descriptor != nullptr &&
        SUCCEEDED(descriptor->GetMediaTypeHandler(&handler)))
        check_hr(handler->GetMediaTypeCount(&media_type_count),
                 "read configured media type count");
    check(media_type_count == 1,
          "configured stream exposes only RGB32 at the selected resolution");
    bool saw_nv12{};
    bool saw_rgb32{};
    for (DWORD index = 0; index < media_type_count && handler != nullptr; ++index) {
        ComPtr<IMFMediaType> type;
        check_hr(handler->GetMediaTypeByIndex(index, &type),
                 "read configured media type");
        UINT32 width{}, height{}, numerator{}, denominator{};
        GUID subtype{};
        if (type != nullptr) {
            check_hr(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE,
                                        &width, &height),
                     "read configured frame size");
            check_hr(MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE,
                                         &numerator, &denominator),
                     "read configured frame rate");
            check_hr(type->GetGUID(MF_MT_SUBTYPE, &subtype),
                     "read configured pixel format");
            UINT32 primaries{}, transfer{}, matrix{}, range{};
            check_hr(type->GetUINT32(MF_MT_VIDEO_PRIMARIES, &primaries),
                     "read configured color primaries");
            check_hr(type->GetUINT32(MF_MT_TRANSFER_FUNCTION, &transfer),
                     "read configured transfer function");
            check_hr(type->GetUINT32(MF_MT_YUV_MATRIX, &matrix),
                     "read configured YUV matrix");
            check_hr(type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range),
                     "read configured nominal range");
            check(primaries == MFVideoPrimaries_BT709 &&
                      transfer == MFVideoTransFunc_709 &&
                      matrix == MFVideoTransferMatrix_BT709 &&
                      range == MFNominalRange_0_255,
                  "configured virtual camera type declares full-range BT.709");
        }
        check(width == 998 && height == 2160,
              "configured media type uses the selected resolution");
        check(numerator == 60 && denominator == 1,
              "configured media type advertises 60 fps");
        saw_nv12 = saw_nv12 || subtype == MFVideoFormat_NV12;
        saw_rgb32 = saw_rgb32 || subtype == MFVideoFormat_RGB32;
    }
    check(!saw_nv12 && saw_rgb32,
          "configured stream exposes RGB32 only for OBS compatibility");

    PROPVARIANT start_position{};
    PropVariantInit(&start_position);
    if (source != nullptr && presentation != nullptr)
        check_hr(source->Start(presentation.Get(), nullptr, &start_position),
                 "start configured media source");
    ComPtr<IMFMediaEvent> source_event;
    ComPtr<IMFMediaStream> stream;
    if (source != nullptr &&
        SUCCEEDED(source->GetEvent(MF_EVENT_FLAG_NO_WAIT, &source_event)))
        stream = event_unknown<IMFMediaStream>(source_event.Get());
    ComPtr<IMFMediaEvent> stream_event;
    if (stream != nullptr)
        check_hr(stream->GetEvent(MF_EVENT_FLAG_NO_WAIT, &stream_event),
                 "configured stream emits start event");
    constexpr int stress_frames = 300;
    int nonblack_frames{};
    LONGLONG duration{};
    LONGLONG previous_time{};
    bool timestamps_are_monotonic = true;
    ComPtr<IMFSample> sample;
    std::vector<ComPtr<IMFSample>> held_samples;
    for (int frame = 0; frame < stress_frames && stream != nullptr; ++frame) {
        check_hr(stream->RequestSample(nullptr),
                 "request allocator-backed configured media sample");
        stream_event.Reset();
        check_hr(stream->GetEvent(0, &stream_event),
                 "configured stream emits allocator-backed media sample");
        sample = stream_event == nullptr
            ? ComPtr<IMFSample>{} : event_unknown<IMFSample>(stream_event.Get());
        if (frame == 0 && sample != nullptr) {
            ComPtr<IMFMediaBuffer> sample_buffer;
            DWORD current_length{};
            check_hr(sample->GetBufferByIndex(0, &sample_buffer),
                     "read allocator-backed sample buffer");
            if (sample_buffer != nullptr) {
                check_hr(sample_buffer->GetCurrentLength(&current_length),
                         "read allocator-backed sample length");
                check(current_length == 998U * 2160U * 4U,
                      "allocator-backed sample reports the tight media length");
            }
        }
        if (frame == 0 && sample != nullptr)
            check_hr(sample->GetSampleDuration(&duration),
                     "read configured sample duration");
        LONGLONG sample_time{};
        if (sample != nullptr) {
            check_hr(sample->GetSampleTime(&sample_time),
                     "read configured sample time");
            if (frame != 0)
                timestamps_are_monotonic = timestamps_are_monotonic &&
                    sample_time - previous_time >= duration;
            previous_time = sample_time;
        }
        if (sample_has_nonblack_luma(sample.Get(), 998, 2160))
            ++nonblack_frames;
        held_samples.push_back(sample);
        if (held_samples.size() > 8U)
            held_samples.erase(held_samples.begin());
    }
    check(duration == 10'000'000LL / 60,
          "60 fps samples use the matching Media Foundation duration");
    check(timestamps_are_monotonic,
          "asynchronous samples use a monotonic non-overlapping timeline");
    check(nonblack_frames == stress_frames,
          "allocator-backed stress never emits a black frame");
    check(held_samples.size() == 8U,
          "allocator-backed stress supports eight in-flight samples");

    if (source != nullptr) {
        check_hr(source->Stop(), "stop configured media source");
        check_hr(source->Shutdown(), "shutdown configured media source");
    }
    sample.Reset();
    held_samples.clear();
    stream_event.Reset();
    stream.Reset();
    source_event.Reset();
    sample_allocator.Reset();
    allocator_control.Reset();
    handler.Reset();
    descriptor.Reset();
    presentation.Reset();
    source.Reset();
    activate.Reset();
    factory.Reset();
    FreeLibrary(module);
}

} // namespace

int wmain() {
    check_hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "initialize COM");
    check_hr(MFStartup(MF_VERSION), "initialize Media Foundation");

    test_control_argument_validation();
    FramePublisher publisher;
    test_frame_exchange(publisher);
    if (!publisher.channel_path().empty()) {
        test_frame_exchange_contention(publisher);
        test_media_source();
        test_configured_media_source(publisher);
    }
    publisher.close();

    check_hr(MFShutdown(), "shutdown Media Foundation");
    CoUninitialize();
    if (failures == 0) std::printf("Virtual camera component tests passed.\n");
    return failures == 0 ? 0 : 1;
}
