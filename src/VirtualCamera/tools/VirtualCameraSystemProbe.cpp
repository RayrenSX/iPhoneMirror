#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl.h>
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwchar>
#include <limits>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace {

constexpr wchar_t CameraName[] = L"iPhoneMirror Virtual Camera";

struct FrameMetrics {
    bool analyzed{};
    bool black{};
    bool buffer_2d{};
    LONG pitch{};
    DWORD length{};
    unsigned average{};
    BYTE maximum{};
};

void print_hr(const char* operation, HRESULT result) {
    std::fprintf(stderr, "%s failed: 0x%08X\n", operation,
                 static_cast<unsigned>(result));
}

FrameMetrics analyze_frame(IMFSample* sample, GUID subtype, UINT32 width,
                           UINT32 height) {
    FrameMetrics metrics;
    if (sample == nullptr || width == 0 || height == 0) return metrics;
    ComPtr<IMFMediaBuffer> buffer;
    if (FAILED(sample->GetBufferByIndex(0, &buffer))) return metrics;
    BYTE* pixels{};
    ComPtr<IMF2DBuffer2> buffer_2d;
    BYTE* start{};
    if (SUCCEEDED(buffer.As(&buffer_2d))) {
        if (FAILED(buffer_2d->Lock2DSize(
                MF2DBuffer_LockFlags_Read, &pixels, &metrics.pitch, &start,
                &metrics.length)))
            return metrics;
        metrics.buffer_2d = true;
    } else {
        if (FAILED(buffer->Lock(&pixels, nullptr, &metrics.length)))
            return metrics;
        metrics.pitch = subtype == MFVideoFormat_NV12
            ? static_cast<LONG>(width) : static_cast<LONG>(width * 4U);
    }

    std::uint64_t total{};
    std::uint64_t sampled{};
    if (subtype == MFVideoFormat_NV12 &&
        metrics.pitch >= static_cast<LONG>(width) &&
        metrics.length >= static_cast<std::uint64_t>(metrics.pitch) * height) {
        for (UINT32 y = 0; y < height; y += 8U) {
            const BYTE* row = pixels + static_cast<std::ptrdiff_t>(y) *
                metrics.pitch;
            for (UINT32 x = 0; x < width; x += 8U) {
                const BYTE value = row[x];
                total += value;
                metrics.maximum = std::max(metrics.maximum, value);
                ++sampled;
            }
        }
    } else if (subtype == MFVideoFormat_RGB32 &&
               metrics.pitch >= static_cast<LONG>(width * 4U) &&
               metrics.length >= static_cast<std::uint64_t>(metrics.pitch) *
                   height) {
        for (UINT32 y = 0; y < height; y += 8U) {
            const BYTE* row = pixels + static_cast<std::ptrdiff_t>(y) *
                metrics.pitch;
            for (UINT32 x = 0; x < width; x += 8U) {
                const BYTE* pixel = row + x * 4U;
                const BYTE value = std::max({pixel[0], pixel[1], pixel[2]});
                total += value;
                metrics.maximum = std::max(metrics.maximum, value);
                ++sampled;
            }
        }
    }
    if (metrics.buffer_2d)
        buffer_2d->Unlock2D();
    else
        buffer->Unlock();
    metrics.analyzed = sampled != 0;
    metrics.average = sampled == 0 ? 0U
        : static_cast<unsigned>(total / sampled);
    metrics.black = metrics.analyzed &&
        (subtype == MFVideoFormat_NV12
             ? metrics.average <= 20U && metrics.maximum <= 24U
             : metrics.average < 4U && metrics.maximum < 12U);
    return metrics;
}

HRESULT find_camera(IMFActivate** result) {
    if (result == nullptr) return E_POINTER;
    *result = nullptr;
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 1);
    if (FAILED(hr)) return hr;
    hr = attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                             MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (FAILED(hr)) return hr;

    IMFActivate** devices{};
    UINT32 count{};
    hr = MFEnumDeviceSources(attributes.Get(), &devices, &count);
    if (FAILED(hr)) return hr;
    ComPtr<IMFActivate> match;
    for (UINT32 index = 0; index < count; ++index) {
        wchar_t* name{};
        UINT32 characters{};
        if (SUCCEEDED(devices[index]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &characters))) {
            if (name != nullptr && std::wstring(name).find(CameraName) !=
                                        std::wstring::npos)
                match = devices[index];
            CoTaskMemFree(name);
        }
        devices[index]->Release();
    }
    CoTaskMemFree(devices);
    if (match == nullptr) return MF_E_NOT_FOUND;
    return match.CopyTo(result);
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    int requested_frames = 600;
    if (argc > 1) {
        const long parsed = std::wcstol(argv[1], nullptr, 10);
        if (parsed >= 30 && parsed <= 36'000)
            requested_frames = static_cast<int>(parsed);
    }
    const bool request_rgb32 = argc > 2 &&
        _wcsicmp(argv[2], L"rgb32") == 0;

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        print_hr("CoInitializeEx", hr);
        return 2;
    }
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        print_hr("MFStartup", hr);
        CoUninitialize();
        return 2;
    }

    int exit_code = 2;
    do {
        ComPtr<IMFActivate> activation;
        if (FAILED(hr = find_camera(&activation))) {
            print_hr("find virtual camera", hr);
            break;
        }
        ComPtr<IMFMediaSource> source;
        if (FAILED(hr = activation->ActivateObject(IID_PPV_ARGS(&source)))) {
            print_hr("activate virtual camera", hr);
            break;
        }
        ComPtr<IMFSourceReader> reader;
        if (FAILED(hr = MFCreateSourceReaderFromMediaSource(source.Get(), nullptr,
                                                             &reader))) {
            print_hr("create source reader", hr);
            source->Shutdown();
            break;
        }

        if (request_rgb32) {
            ComPtr<IMFMediaType> rgb32_type;
            for (DWORD index = 0;; ++index) {
                ComPtr<IMFMediaType> candidate;
                const HRESULT type_hr = reader->GetNativeMediaType(
                    static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                    index, &candidate);
                if (type_hr == MF_E_NO_MORE_TYPES) break;
                if (FAILED(type_hr)) {
                    hr = type_hr;
                    break;
                }
                GUID candidate_subtype{};
                if (SUCCEEDED(candidate->GetGUID(MF_MT_SUBTYPE,
                                                 &candidate_subtype)) &&
                    candidate_subtype == MFVideoFormat_RGB32) {
                    rgb32_type = candidate;
                    break;
                }
            }
            if (FAILED(hr) || rgb32_type == nullptr ||
                FAILED(hr = reader->SetCurrentMediaType(
                    static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                    nullptr, rgb32_type.Get()))) {
                print_hr("select RGB32 media type",
                         FAILED(hr) ? hr : MF_E_INVALIDMEDIATYPE);
                source->Shutdown();
                break;
            }
        }

        ComPtr<IMFMediaType> media_type;
        if (FAILED(hr = reader->GetCurrentMediaType(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                &media_type))) {
            print_hr("read media type", hr);
            source->Shutdown();
            break;
        }
        GUID subtype{};
        UINT32 width{}, height{};
        if (FAILED(hr = media_type->GetGUID(MF_MT_SUBTYPE, &subtype)) ||
            FAILED(hr = MFGetAttributeSize(media_type.Get(), MF_MT_FRAME_SIZE,
                                            &width, &height))) {
            print_hr("read media layout", hr);
            source->Shutdown();
            break;
        }

        int samples{};
        int black_samples{};
        int empty_samples{};
        int current_black_run{};
        int longest_black_run{};
        int logged_black_samples{};
        LONGLONG previous_timestamp = std::numeric_limits<LONGLONG>::min();
        LONGLONG longest_gap{};
        const auto started = std::chrono::steady_clock::now();
        while (samples < requested_frames) {
            DWORD stream_index{}, flags{};
            LONGLONG timestamp{};
            ComPtr<IMFSample> sample;
            hr = reader->ReadSample(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0,
                &stream_index, &flags, &timestamp, &sample);
            if (FAILED(hr)) {
                print_hr("read camera sample", hr);
                break;
            }
            if ((flags & static_cast<DWORD>(MF_SOURCE_READERF_ENDOFSTREAM)) != 0)
                break;
            if (sample == nullptr) {
                ++empty_samples;
                continue;
            }
            if (previous_timestamp != std::numeric_limits<LONGLONG>::min())
                longest_gap = std::max(longest_gap,
                                       timestamp - previous_timestamp);
            previous_timestamp = timestamp;
            const auto metrics = analyze_frame(sample.Get(), subtype, width,
                                               height);
            if (metrics.black) {
                ++black_samples;
                ++current_black_run;
                longest_black_run = std::max(longest_black_run,
                                             current_black_run);
                if (logged_black_samples < 24) {
                    LONGLONG sample_time{}, duration{};
                    UINT64 device_time{};
                    sample->GetSampleTime(&sample_time);
                    sample->GetSampleDuration(&duration);
                    sample->GetUINT64(MFSampleExtension_DeviceTimestamp,
                                      &device_time);
                    std::printf("black index=%d average=%u maximum=%u "
                                "pitch=%ld length=%lu timestamp_ms=%.2f "
                                "sample_time_ms=%.2f duration_ms=%.2f "
                                "device_time_ms=%.2f 2d=%d\n",
                                samples, metrics.average, metrics.maximum,
                                metrics.pitch, metrics.length,
                                timestamp / 10'000.0,
                                sample_time / 10'000.0,
                                duration / 10'000.0,
                                device_time / 10'000.0,
                                metrics.buffer_2d ? 1 : 0);
                    ++logged_black_samples;
                }
            } else {
                current_black_run = 0;
            }
            ++samples;
        }
        const auto elapsed = std::chrono::duration<double>(
            std::chrono::steady_clock::now() - started).count();
        std::printf("format=%s size=%ux%u samples=%d black=%d empty=%d "
                    "fps=%.2f longest_timestamp_gap_ms=%.2f "
                    "longest_black_run=%d\n",
                    subtype == MFVideoFormat_NV12 ? "NV12" :
                    subtype == MFVideoFormat_RGB32 ? "RGB32" : "other",
                    width, height, samples, black_samples, empty_samples,
                    elapsed > 0.0 ? samples / elapsed : 0.0,
                    longest_gap / 10'000.0, longest_black_run);
        source->Shutdown();
        exit_code = FAILED(hr) || samples != requested_frames ||
                    black_samples != 0 ? 1 : 0;
    } while (false);

    MFShutdown();
    CoUninitialize();
    return exit_code;
}
