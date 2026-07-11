#include "Media/MediaFoundationDecoder.h"

#include "Media/H264.h"
#include "../Logging.h"

#include <Windows.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <format>
#include <mutex>
#include <stdexcept>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::media {
namespace {

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) throw std::runtime_error(std::format("{} failed: 0x{:08X}", operation, static_cast<unsigned>(result)));
}

void ensure_media_foundation() {
    static std::once_flag flag;
    static HRESULT startup_result = E_FAIL;
    std::call_once(flag, [] { startup_result = MFStartup(MF_VERSION, MFSTARTUP_LITE); });
    check(startup_result, "MFStartup");
}

std::vector<std::uint8_t> parameter_sets_annex_b(const coremedia::FormatDescription& format) {
    std::vector<std::uint8_t> result;
    const auto add = [&result](const std::vector<std::uint8_t>& nalu) {
        result.insert(result.end(), {0, 0, 0, 1});
        result.insert(result.end(), nalu.begin(), nalu.end());
    };
    for (const auto& sps : format.sequence_parameter_sets) add(sps);
    for (const auto& pps : format.picture_parameter_sets) add(pps);
    return result;
}

} // namespace

MediaFoundationH264Decoder::MediaFoundationH264Decoder() {
    ensure_media_foundation();
    const HRESULT com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE) check(com_result, "CoInitializeEx");
    auto decoder_result = CoCreateInstance(CLSID_MSH264DecoderMFT, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&transform_));
    const char* decoder_name = "MSH264DecoderMFT";
    if (FAILED(decoder_result)) {
        decoder_result = CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr,
            CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&transform_));
        decoder_name = "CMSH264DecoderMFT";
    }
    check(decoder_result, "create Microsoft H264 decoder");
    logging::write(std::format("mf_decoder selected={}", decoder_name));
    ComPtr<IMFAttributes> attributes;
    bool d3d11_aware = false;
    HRESULT low_latency_attribute_result = E_NOINTERFACE;
    if (SUCCEEDED(transform_->GetAttributes(&attributes))) {
        low_latency_attribute_result = attributes->SetUINT32(MF_LOW_LATENCY, TRUE);
        UINT32 aware{};
        d3d11_aware = SUCCEEDED(attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware)) && aware != 0;
    }
    ComPtr<ICodecAPI> codec_api;
    if (SUCCEEDED(transform_->QueryInterface(IID_PPV_ARGS(&codec_api)))) {
        VARIANT value;
        VariantInit(&value);
        value.vt = VT_UI4;
        value.ulVal = TRUE;
        const auto codec_low_latency_result = codec_api->SetValue(&CODECAPI_AVLowLatencyMode, &value);
        logging::write(std::format(
            "mf_decoder low_latency_attribute_hr=0x{:08X} codec_hr=0x{:08X}",
            static_cast<unsigned>(low_latency_attribute_result),
            static_cast<unsigned>(codec_low_latency_result)));
        VariantClear(&value);
    } else {
        logging::write(std::format("mf_decoder low_latency_attribute_hr=0x{:08X}",
            static_cast<unsigned>(low_latency_attribute_result)));
    }

    // The WPF preview currently consumes a CPU NV12 frame.  A D3D11 manager
    // makes the MFT return GPU-backed samples on some Windows builds; our
    // compatibility readback path would then synchronously stall on the GPU
    // (and can stop after a few frames). Keep it opt-in until the native
    // texture-to-WPF path is enabled. This also gives us a safe A/B switch for
    // future hardware rendering work.
    const bool request_d3d11 = [] {
        char value[8]{};
        const auto length = GetEnvironmentVariableA("IPHONE_MIRROR_MF_D3D11", value, sizeof(value));
        return length > 0 && (value[0] == '1' || value[0] == 'y' || value[0] == 'Y');
    }();
    if (d3d11_aware && request_d3d11) {
        ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL level{};
        constexpr D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        const auto device_result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, static_cast<UINT>(sizeof(levels) / sizeof(levels[0])), D3D11_SDK_VERSION,
            &d3d_device_, &level, &context);
        if (SUCCEEDED(device_result)) {
            UINT reset_token{};
            const auto manager_result = MFCreateDXGIDeviceManager(&reset_token, &d3d_manager_);
            if (SUCCEEDED(manager_result) && SUCCEEDED(d3d_manager_->ResetDevice(d3d_device_.Get(), reset_token))) {
                const auto set_result = transform_->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                    reinterpret_cast<ULONG_PTR>(d3d_manager_.Get()));
                logging::write(std::format(
                    "mf_decoder d3d11_aware=true set_d3d_manager_hr=0x{:08X} feature_level=0x{:04X}",
                    static_cast<unsigned>(set_result), static_cast<unsigned>(level)));
                if (FAILED(set_result)) {
                    d3d_manager_.Reset();
                    d3d_device_.Reset();
                }
            } else {
                logging::write(std::format(
                    "mf_decoder d3d11_aware=true manager_hr=0x{:08X} device_hr=0x{:08X}",
                    static_cast<unsigned>(manager_result), static_cast<unsigned>(device_result)));
                d3d_manager_.Reset();
                d3d_device_.Reset();
            }
        } else {
            logging::write(std::format(
                "mf_decoder d3d11_aware=true device_hr=0x{:08X}",
                static_cast<unsigned>(device_result)));
        }
    } else {
        logging::write(std::format("mf_decoder d3d11_aware={} d3d11_manager=disabled",
            d3d11_aware ? "true" : "false"));
    }
}

MediaFoundationH264Decoder::~MediaFoundationH264Decoder() {
    if (output_type_) output_type_->Release();
    if (transform_) transform_->Release();
}

void MediaFoundationH264Decoder::configure(const coremedia::FormatDescription& format,
    std::uint32_t fps_numerator, std::uint32_t fps_denominator) {
    if (!format.is_video() || format.width == 0 || format.height == 0) throw std::invalid_argument("invalid H264 format description");
    format_ = format;
    ComPtr<IMFMediaType> input;
    check(MFCreateMediaType(&input), "MFCreateMediaType input");
    check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "set input major type");
    // The Microsoft decoder requires Annex-B byte streams even though the
    // QuickTime CMSampleBuffer arriving over USB is AVC1/AVCC.
    check(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), "set H264 input subtype");
    check(MFSetAttributeSize(input.Get(), MF_MT_FRAME_SIZE, format.width, format.height), "set input frame size");
    check(MFSetAttributeRatio(input.Get(), MF_MT_FRAME_RATE, fps_numerator, fps_denominator), "set input frame rate");
    check(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "set input interlace mode");
    const bool allow_frame_reordering = [] {
        char value[8]{};
        const auto length = GetEnvironmentVariableA(
            "IPHONE_MIRROR_ALLOW_FRAME_REORDERING", value, sizeof(value));
        return length > 0 && (value[0] == '1' || value[0] == 'y' || value[0] == 'Y');
    }();
    if (!allow_frame_reordering) {
        // The QuickTime screen stream is produced in display order. Without
        // this explicit hint, the inbox H.264 MFT can conservatively retain a
        // full Level 5.1 DPB (14 pictures at 1216x2624) even though the live
        // stream contains no reordered pictures.
        check(input->SetUINT32(MF_MT_VIDEO_NO_FRAME_ORDERING, TRUE),
            "disable H264 frame reordering");
    }
    logging::write(std::format("mf_decoder no_frame_ordering={}",
        allow_frame_reordering ? "false" : "true"));
    const auto sequence_header = parameter_sets_annex_b(format);
    if (!sequence_header.empty()) {
        check(input->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, sequence_header.data(),
            static_cast<UINT32>(sequence_header.size())), "set Annex-B sequence header");
    }
    check(transform_->SetInputType(0, input.Get(), 0), "set decoder input type");
    select_nv12_output();
    check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin decoder streaming");
    check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "start decoder stream");
    configured_ = true;
    sent_parameter_sets_ = false;
}

void MediaFoundationH264Decoder::select_nv12_output() {
    if (output_type_) { output_type_->Release(); output_type_ = nullptr; }
    for (DWORD index = 0;; ++index) {
        ComPtr<IMFMediaType> candidate;
        const HRESULT result = transform_->GetOutputAvailableType(0, index, &candidate);
        if (result == MF_E_NO_MORE_TYPES) break;
        check(result, "enumerate decoder output type");
        GUID subtype{};
        if (SUCCEEDED(candidate->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFVideoFormat_NV12 &&
            SUCCEEDED(transform_->SetOutputType(0, candidate.Get(), 0))) {
            output_type_ = candidate.Detach();
            return;
        }
    }
    throw std::runtime_error("Microsoft H264 decoder exposes no NV12 output");
}

std::vector<DecodedFrame> MediaFoundationH264Decoder::decode(std::span<const std::uint8_t> avcc_sample,
    std::int64_t timestamp_100ns, std::int64_t duration_100ns) {
    if (!configured_) throw std::logic_error("H264 decoder is not configured");
    auto encoded = h264::avcc_to_annex_b(avcc_sample, format_.nalu_length_size);
    if (!sent_parameter_sets_) {
        auto parameter_sets = parameter_sets_annex_b(format_);
        parameter_sets.insert(parameter_sets.end(), encoded.begin(), encoded.end());
        encoded = std::move(parameter_sets);
        sent_parameter_sets_ = true;
    }

    ComPtr<IMFSample> sample;
    ComPtr<IMFMediaBuffer> buffer;
    check(MFCreateSample(&sample), "create H264 input sample");
    check(MFCreateMemoryBuffer(static_cast<DWORD>(encoded.size()), &buffer), "create H264 input buffer");
    BYTE* destination{};
    DWORD capacity{};
    check(buffer->Lock(&destination, &capacity, nullptr), "lock H264 input buffer");
    std::copy(encoded.begin(), encoded.end(), destination);
    buffer->Unlock();
    check(buffer->SetCurrentLength(static_cast<DWORD>(encoded.size())), "set H264 input length");
    check(sample->AddBuffer(buffer.Get()), "attach H264 input buffer");
    check(sample->SetSampleTime(timestamp_100ns), "set H264 sample time");
    check(sample->SetSampleDuration(duration_100ns), "set H264 sample duration");
    if (h264::is_keyframe_avcc(avcc_sample, format_.nalu_length_size)) {
        check(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE), "mark H264 clean point");
    }

    // A synchronous MFT may temporarily stop accepting input while one or
    // more decoded pictures are waiting in its output queue.  The previous
    // implementation returned the pending picture immediately, which silently
    // discarded the encoded sample that produced MF_E_NOTACCEPTING.  On the
    // iPhone stream this happens periodically around reference-frame changes
    // and causes visible 50-66 ms gaps.  Drain all available output pictures,
    // then retry the same input sample until it is accepted.
    std::vector<DecodedFrame> decoded;
    HRESULT input_result = transform_->ProcessInput(0, sample.Get(), 0);
    while (input_result == MF_E_NOTACCEPTING) {
        auto pending = receive_output();
        if (!pending) {
            check(input_result, "decoder ProcessInput (no output while not accepting)");
        }
        decoded.push_back(std::move(*pending));
        input_result = transform_->ProcessInput(0, sample.Get(), 0);
    }
    check(input_result, "decoder ProcessInput");

    // Consume every frame immediately available. The Microsoft decoder can
    // release a group of reordered pictures at once around an IDR; returning
    // only the newest picture here previously discarded almost half of the
    // valid 60 fps output. The presentation queue decides what to display.
    while (auto output = receive_output()) {
        decoded.push_back(std::move(*output));
    }
    return decoded;
}

std::vector<DecodedFrame> MediaFoundationH264Decoder::drain() {
    if (!configured_) return {};
    check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0), "end decoder stream");
    check(transform_->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0), "drain decoder");
    std::vector<DecodedFrame> decoded;
    while (auto output = receive_output()) {
        decoded.push_back(std::move(*output));
    }
    return decoded;
}

std::optional<DecodedFrame> MediaFoundationH264Decoder::receive_output() {
    MFT_OUTPUT_STREAM_INFO stream_info{};
    check(transform_->GetOutputStreamInfo(0, &stream_info), "get decoder output stream info");
    ComPtr<IMFSample> sample;
    if ((stream_info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0) {
        ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateSample(&sample), "create decoder output sample");
        check(MFCreateMemoryBuffer(std::max<DWORD>(stream_info.cbSize, format_.width * format_.height * 3 / 2),
            &buffer), "create decoder output buffer");
        check(sample->AddBuffer(buffer.Get()), "attach decoder output buffer");
    }

    MFT_OUTPUT_DATA_BUFFER output{};
    output.dwStreamID = 0;
    output.pSample = sample.Get();
    DWORD status{};
    HRESULT result = transform_->ProcessOutput(0, 1, &output, &status);
    if (output.pEvents) output.pEvents->Release();
    if (result == MF_E_TRANSFORM_NEED_MORE_INPUT) return std::nullopt;
    if (result == MF_E_TRANSFORM_STREAM_CHANGE) {
        select_nv12_output();
        return receive_output();
    }
    check(result, "decoder ProcessOutput");
    if (!output.pSample) return std::nullopt;

    ComPtr<IMFMediaBuffer> contiguous;
    check(output.pSample->ConvertToContiguousBuffer(&contiguous), "make NV12 output contiguous");
    BYTE* source{};
    DWORD current{};
    check(contiguous->Lock(&source, nullptr, &current), "lock NV12 output");
    DecodedFrame frame;
    frame.width = format_.width;
    frame.height = format_.height;
    frame.nv12.assign(source, source + current);
    contiguous->Unlock();
    LONGLONG time{};
    if (SUCCEEDED(output.pSample->GetSampleTime(&time))) frame.timestamp_100ns = time;
    UINT32 stride{};
    if (output_type_ && SUCCEEDED(output_type_->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) {
        frame.stride = static_cast<std::int32_t>(stride);
    } else {
        frame.stride = static_cast<std::int32_t>(format_.width);
    }
    return frame;
}

void MediaFoundationH264Decoder::flush() {
    if (!transform_) return;
    transform_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    sent_parameter_sets_ = false;
}

} // namespace iPhoneMirror::media
