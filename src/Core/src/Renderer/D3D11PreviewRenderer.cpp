#include "Renderer/D3D11PreviewRenderer.h"
#include "Renderer/OutputModeState.h"

#include "Logging.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <dcomp.h>
#include <dxgi1_3.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstring>
#include <format>
#include <mutex>
#include <stdexcept>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::renderer {
namespace {

std::uint64_t pack_corner_profile(float normalized_radius, float curve_exponent) noexcept {
    return (static_cast<std::uint64_t>(std::bit_cast<std::uint32_t>(normalized_radius)) << 32U) |
        std::bit_cast<std::uint32_t>(curve_exponent);
}

constexpr std::uint32_t DiagnosticMonitorShift = 0U;
constexpr std::uint32_t DiagnosticSourceHdrShift = 2U;
constexpr std::uint32_t DiagnosticHdrSurfaceShift = 4U;
constexpr std::uint32_t DiagnosticPreferenceShift = 5U;
constexpr std::uint32_t DiagnosticMonitorMask = 0x3U << DiagnosticMonitorShift;
constexpr std::uint32_t DiagnosticSourceHdrMask = 0x3U << DiagnosticSourceHdrShift;
constexpr std::uint32_t DiagnosticHdrSurfaceMask = 0x1U << DiagnosticHdrSurfaceShift;
constexpr std::uint32_t DiagnosticPreferenceMask = 0x3U << DiagnosticPreferenceShift;

std::uint32_t publish_diagnostic_field(std::atomic_uint32_t& snapshot,
    std::uint32_t mask, std::uint32_t value) noexcept {
    auto current = snapshot.load(std::memory_order_relaxed);
    for (;;) {
        const auto desired = (current & ~mask) | (value & mask);
        if (snapshot.compare_exchange_weak(current, desired,
            std::memory_order_release, std::memory_order_relaxed)) {
            return current;
        }
    }
}

OutputDiagnostics unpack_output_diagnostics(std::uint32_t packed) noexcept {
    OutputDiagnostics diagnostics;
    diagnostics.monitor_capability = static_cast<MonitorHdrCapability>(
        (packed & DiagnosticMonitorMask) >> DiagnosticMonitorShift);
    const auto source_hdr_state = static_cast<std::uint8_t>(
        (packed & DiagnosticSourceHdrMask) >> DiagnosticSourceHdrShift);
    diagnostics.source_hdr_known = source_hdr_state != 0;
    diagnostics.source_hdr = source_hdr_state == 2;
    diagnostics.actual_hdr_surface = (packed & DiagnosticHdrSurfaceMask) != 0;
    diagnostics.hdr_effective = output::hdr_output_is_effective(
        diagnostics.source_hdr,
        diagnostics.monitor_capability == MonitorHdrCapability::Hdr,
        diagnostics.actual_hdr_surface);
    diagnostics.requested_preference = static_cast<media::ColorOutputPreference>(
        (packed & DiagnosticPreferenceMask) >> DiagnosticPreferenceShift);
    return diagnostics;
}

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(std::format("{} failed: 0x{:08X}", operation,
            static_cast<unsigned>(result)));
    }
}

DXGI_FORMAT dxgi_format(output::SurfaceFormat format) noexcept {
    return format == output::SurfaceFormat::Rgba16Float
        ? DXGI_FORMAT_R16G16B16A16_FLOAT
        : DXGI_FORMAT_B8G8R8A8_UNORM;
}

output::SurfaceFormat output_surface_format(DXGI_FORMAT format) noexcept {
    return format == DXGI_FORMAT_R16G16B16A16_FLOAT
        ? output::SurfaceFormat::Rgba16Float
        : output::SurfaceFormat::Bgra8;
}

output::Failure classify_output_failure(HRESULT result) noexcept {
    if (SUCCEEDED(result)) return output::Failure::None;
    if (result == DXGI_ERROR_UNSUPPORTED) return output::Failure::Unsupported;
    if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET ||
        result == DXGI_ERROR_DEVICE_HUNG ||
        result == DXGI_ERROR_DRIVER_INTERNAL_ERROR) {
        return output::Failure::DeviceLost;
    }
    return output::Failure::Transient;
}

const char* output_failure_name(output::Failure failure) noexcept {
    switch (failure) {
    case output::Failure::None: return "none";
    case output::Failure::Unsupported: return "unsupported";
    case output::Failure::Transient: return "transient";
    case output::Failure::DeviceLost: return "device_lost";
    }
    return "unknown";
}

std::uint64_t steady_milliseconds() noexcept {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<
        std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count());
}

ComPtr<ID3DBlob> compile_shader(const char* source, const char* entry, const char* target) {
    ComPtr<ID3DBlob> shader;
    ComPtr<ID3DBlob> errors;
    const auto result = D3DCompile(source, std::strlen(source), "iPhoneMirrorPreview", nullptr,
        nullptr, entry, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &errors);
    if (FAILED(result)) {
        const auto* message = errors
            ? static_cast<const char*>(errors->GetBufferPointer())
            : "unknown shader error";
        throw std::runtime_error(std::format("D3DCompile {}: {}", entry, message));
    }
    return shader;
}

std::uint32_t leading_padding_rows(const media::DecodedFrame& frame) {
    (void)frame;
    return 0;
}

std::size_t allocated_nv12_height(const media::DecodedFrame& frame, std::size_t stride) {
    const auto candidate = stride == 0 ? 0 : (frame.nv12.size() * 2U) / (stride * 3U);
    if (candidate >= frame.height) {
        const auto required = stride * candidate + stride * ((candidate + 1U) / 2U);
        if (required <= frame.nv12.size()) return candidate;
    }
    return frame.height;
}

const char* monitor_hdr_state_name(MonitorHdrCapability state) noexcept {
    switch (state) {
    case MonitorHdrCapability::Unknown: return "unknown";
    case MonitorHdrCapability::Sdr: return "sdr";
    case MonitorHdrCapability::Hdr: return "hdr";
    }
    return "unknown";
}

MonitorHdrCapability monitor_hdr_state(IDXGIFactory1* factory, HWND window) noexcept {
    if (!factory || !window) return MonitorHdrCapability::Unknown;
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    if (!monitor) return MonitorHdrCapability::Unknown;
    for (UINT adapter_index{};; ++adapter_index) {
        ComPtr<IDXGIAdapter1> adapter;
        const auto adapter_result = factory->EnumAdapters1(adapter_index, &adapter);
        if (adapter_result == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(adapter_result)) return MonitorHdrCapability::Unknown;
        if (!adapter) continue;
        for (UINT output_index{};; ++output_index) {
            ComPtr<IDXGIOutput> output;
            const auto output_result = adapter->EnumOutputs(output_index, &output);
            if (output_result == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(output_result)) return MonitorHdrCapability::Unknown;
            if (!output) continue;
            DXGI_OUTPUT_DESC output_description{};
            if (FAILED(output->GetDesc(&output_description)))
                return MonitorHdrCapability::Unknown;
            if (output_description.Monitor != monitor) continue;
            ComPtr<IDXGIOutput6> output6;
            DXGI_OUTPUT_DESC1 description{};
            const auto output6_result = output.As(&output6);
            if (output6_result == E_NOINTERFACE) return MonitorHdrCapability::Sdr;
            if (FAILED(output6_result) || FAILED(output6->GetDesc1(&description)))
                return MonitorHdrCapability::Unknown;
            const bool advanced_color =
                description.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
                description.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709;
            return advanced_color && description.BitsPerColor >= 10
                ? MonitorHdrCapability::Hdr : MonitorHdrCapability::Sdr;
        }
    }
    return MonitorHdrCapability::Unknown;
}

constexpr const char* VertexShader = R"(
struct VSOutput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VSOutput main(uint vertexId : SV_VertexID) {
    VSOutput output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    output.uv = uv;
    return output;
}
)";

constexpr const char* PreviewPixelShaders = R"(
Texture2D<float> yPlane : register(t0);
Texture2D<float2> uvPlane : register(t1);
Texture2D<float4> image : register(t2);
SamplerState linearSampler : register(s0);

cbuffer ShapeConstants : register(b0) {
    float2 outputSize;
    float cornerRadius;
    float cornerExponent;
    float cornerEnabled;
    float rotationQuarterTurns;
    float2 shapePadding;
    float yOffset;
    float yScale;
    float chromaOffset;
    float chromaScale;
    float redCr;
    float greenCb;
    float greenCr;
    float blueCb;
    float transferFunction;
    float colorPrimaries;
    float hdrSurface;
    float preserveHdr;
    float sourcePeakNits;
    float3 colorPadding;
    float brightness;
    float contrast;
    float saturation;
    float gammaValue;
};

float2 rotateUv(float2 uv) {
    int turns = ((int)round(rotationQuarterTurns) % 4 + 4) % 4;
    if (turns == 1) return float2(uv.y, 1.0 - uv.x);
    if (turns == 2) return float2(1.0 - uv.x, 1.0 - uv.y);
    if (turns == 3) return float2(1.0 - uv.y, uv.x);
    return uv;
}

float cornerCoverage(float2 pixel) {
    if (cornerEnabled < 0.5) return 1.0;

    // The GUI resolves a device-family visual fit from ProductType. fwidth
    // creates sub-pixel coverage instead of the binary stair steps produced
    // by an HRGN fallback.
    float radius = max(cornerRadius, 1.0);
    float2 halfSize = outputSize * 0.5;
    float2 corner = max(abs(pixel - halfSize) - (halfSize - radius), 0.0) / radius;
    float powered = pow(corner.x, cornerExponent) + pow(corner.y, cornerExponent);
    float normalizedDistance = pow(max(powered, 1.0e-8), 1.0 / cornerExponent) - 1.0;
    float distancePixels = normalizedDistance * radius;
    float antialiasWidth = max(fwidth(distancePixels), 0.75);
    return 1.0 - smoothstep(-antialiasWidth, antialiasWidth, distancePixels);
}

float4 premultiplyForCorner(float3 rgb, float2 pixel) {
    float alpha = cornerCoverage(pixel);
    return float4(rgb * alpha, alpha);
}

float3 inverseSrgb(float3 value) {
    float3 low = value / 12.92;
    float3 high = pow(max((value + 0.055) / 1.055, 0.0), 2.4);
    return lerp(high, low, step(value, 0.04045));
}

float3 encodeSrgb(float3 value) {
    value = max(value, 0.0);
    float3 low = value * 12.92;
    float3 high = 1.055 * pow(value, 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(value, 0.0031308));
}

float3 pqToNits(float3 value) {
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 32.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 128.0;
    const float c3 = 2392.0 / 128.0;
    float3 p = pow(saturate(value), 1.0 / m2);
    return 10000.0 * pow(max((p - c1) / max(c2 - c3 * p, 1.0e-6), 0.0), 1.0 / m1);
}

float3 hlgToNits(float3 value) {
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;
    float3 low = value * value / 3.0;
    float3 high = (exp((value - c) / a) + b) / 12.0;
    float3 sceneLinear = lerp(high, low, step(value, 0.5));
    return max(sourcePeakNits, 100.0) * pow(max(sceneLinear, 0.0), 1.2);
}

float3 convertPrimariesTo709(float3 linearRgb) {
    if (colorPrimaries > 0.5 && colorPrimaries < 1.5) {
        return float3(
            1.6605 * linearRgb.r - 0.5876 * linearRgb.g - 0.0728 * linearRgb.b,
           -0.1246 * linearRgb.r + 1.1329 * linearRgb.g - 0.0083 * linearRgb.b,
           -0.0182 * linearRgb.r - 0.1006 * linearRgb.g + 1.1187 * linearRgb.b);
    }
    if (colorPrimaries >= 1.5) {
        return float3(
            1.224745 * linearRgb.r - 0.224904 * linearRgb.g,
           -0.042058 * linearRgb.r + 1.042081 * linearRgb.g,
           -0.019642 * linearRgb.r - 0.078655 * linearRgb.g + 1.098537 * linearRgb.b);
    }
    return linearRgb;
}

float3 acesToneMap(float3 linearNits) {
    float3 value = max(linearNits / 203.0, 0.0);
    return saturate((value * (2.51 * value + 0.03)) /
        (value * (2.43 * value + 0.59) + 0.14));
}

float3 applyColorOutput(float3 encodedRgb) {
    encodedRgb = saturate(encodedRgb);
    if (transferFunction > 0.5) {
        float3 nits = transferFunction < 1.5
            ? pqToNits(encodedRgb)
            : hlgToNits(encodedRgb);
        nits = max(convertPrimariesTo709(nits), 0.0);
        if (preserveHdr > 0.5) return nits / 80.0;
        float3 toneMapped = acesToneMap(nits);
        return hdrSurface > 0.5 ? toneMapped : encodeSrgb(toneMapped);
    }
    if (hdrSurface > 0.5 || colorPrimaries > 0.5) {
        float3 linearRgb = max(convertPrimariesTo709(inverseSrgb(encodedRgb)), 0.0);
        return hdrSurface > 0.5 ? linearRgb : encodeSrgb(linearRgb);
    }
    return encodedRgb;
}

float3 applyImageAdjustments(float3 rgb) {
    rgb = (rgb - 0.5) * contrast + 0.5 + brightness;
    float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    rgb = lerp(luminance.xxx, rgb, saturation);
    return pow(saturate(rgb), 1.0 / max(gammaValue, 0.01));
}

float4 nv12Main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    uv = rotateUv(uv);
    float y = max(0.0, yPlane.Sample(linearSampler, uv) - yOffset) * yScale;
    float2 chroma = (uvPlane.Sample(linearSampler, uv) -
        float2(chromaOffset, chromaOffset)) * chromaScale;
    float3 rgb;
    rgb.r = y + redCr * chroma.y;
    rgb.g = y + greenCb * chroma.x + greenCr * chroma.y;
    rgb.b = y + blueCb * chroma.x;
    return premultiplyForCorner(
        applyImageAdjustments(applyColorOutput(rgb)), position.xy);
}

float4 copyMain(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    return premultiplyForCorner(image.Sample(linearSampler, rotateUv(uv)).rgb, position.xy);
}

float4 maskMain(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    return premultiplyForCorner(float3(0.0, 0.0, 0.0), position.xy);
}
)";

} // namespace

struct D3D11PreviewRenderer::Impl {
    struct alignas(16) ShapeConstantData {
        float output_width{};
        float output_height{};
        float corner_radius{};
        float corner_exponent{2.36F};
        float corner_enabled{};
        float rotation_quarter_turns{};
        float padding[2]{};
        float y_offset{};
        float y_scale{1.0F};
        float chroma_offset{0.5F};
        float chroma_scale{1.0F};
        float red_cr{1.5748F};
        float green_cb{-0.1873F};
        float green_cr{-0.4681F};
        float blue_cb{1.8556F};
        float transfer_function{};
        float color_primaries{};
        float hdr_surface{};
        float preserve_hdr{};
        float source_peak_nits{1000.0F};
        float color_padding[3]{};
        float brightness{};
        float contrast{1.0F};
        float saturation{1.0F};
        float gamma{1.0F};
    };

    struct ImageAdjustments {
        float brightness{};
        float contrast{1.0F};
        float saturation{1.0F};
        float gamma{1.0F};
    };

    HWND window{};
    FrameProvider provider;
    std::jthread worker;
    bool composition_mode{};

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIFactory2> factory;
    ComPtr<IDXGISwapChain1> swap_chain;
    ComPtr<IDCompositionDevice> composition_device;
    ComPtr<IDCompositionTarget> composition_target;
    ComPtr<IDCompositionVisual> composition_visual;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11VertexShader> vertex_shader;
    ComPtr<ID3D11PixelShader> pixel_shader;
    ComPtr<ID3D11PixelShader> copy_pixel_shader;
    ComPtr<ID3D11PixelShader> mask_pixel_shader;
    ComPtr<ID3D11Buffer> shape_constants;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Texture2D> y_texture;
    ComPtr<ID3D11Texture2D> uv_texture;
    ComPtr<ID3D11ShaderResourceView> y_view;
    ComPtr<ID3D11ShaderResourceView> uv_view;
    ComPtr<ID3D11Texture2D> shared_gpu_texture;
    ComPtr<ID3D11ShaderResourceView> shared_gpu_y_view;
    ComPtr<ID3D11ShaderResourceView> shared_gpu_uv_view;
    ComPtr<IDXGIKeyedMutex> shared_gpu_mutex;
    std::shared_ptr<const media::DecodedFrame::SharedGpuFrame> shared_gpu_frame;
    bool shared_gpu_acquired{};
    ComPtr<ID3D11Texture2D> render_texture;
    ComPtr<ID3D11RenderTargetView> render_target;
    ComPtr<ID3D11ShaderResourceView> render_view;
    output::State output_state;

    std::uint32_t frame_width{};
    std::uint32_t frame_height{};
    UINT target_width{};
    UINT target_height{};
    UINT render_width{};
    UINT render_height{};
    UINT local_render_width{};
    UINT local_render_height{};
    bool using_limited_pass{};
    std::int64_t last_timestamp{};
    std::shared_ptr<const media::DecodedFrame> last_frame;
    std::uint64_t rendered_frames{};
    std::chrono::steady_clock::time_point first_presented_at{};
    std::uint32_t last_sync_refresh_count{};
    std::chrono::steady_clock::time_point last_sync_sample_at{};
    double display_fps{};
    bool display_statistics_valid{};
    std::atomic_bool refresh_requested{true};
    std::atomic_bool clear_requested{};
    std::atomic_uint32_t max_fps{60};
    // Packed as width in the high dword and height in the low dword so the
    // render thread never observes a mixed pair during a live preset change.
    std::atomic_uint64_t render_size_limit{};
    // Radius/exponent are published as one atomic value so a live device
    // switch cannot render one frame using a mixed profile.
    std::atomic_uint64_t corner_profile{pack_corner_profile(0.1784F, 2.36F)};
    std::mutex image_adjustments_mutex;
    ImageAdjustments image_adjustments;
    std::atomic_int rotation_quarter_turns{};
    std::atomic_uint64_t color_output_policy_generation{};
    // A single packed load prevents torn diagnostics. Renderer-owned fields
    // still advance as their underlying facts change, so a newly detected SDR
    // monitor may legitimately coexist briefly with the previous HDR surface.
    // Source HDR uses 0=unknown, 1=SDR, 2=HDR.
    std::atomic_uint32_t diagnostic_output_snapshot{};
    media::PixelFormat texture_pixel_format{media::PixelFormat::Nv12};
    std::atomic_uint64_t color_signature{};
    HMONITOR configured_monitor{};
    MonitorHdrCapability configured_monitor_hdr{MonitorHdrCapability::Sdr};
    HMONITOR last_probed_monitor{};
    bool factory_refresh_pending{true};
    std::uint64_t monitor_generation{};
    std::chrono::steady_clock::time_point next_color_output_probe{};
    std::chrono::steady_clock::time_point next_resize_allowed{};
    std::uint32_t scheduled_fps{};
    std::chrono::steady_clock::time_point next_present_due{};

    Impl(HWND value, FrameProvider frame_provider)
        : window(value), provider(std::move(frame_provider)) {
        initialize();
        worker = std::jthread([this](std::stop_token token) { run(token); });
    }

    ~Impl() {
        if (worker.joinable()) {
            worker.request_stop();
            worker.join();
        }
    }

    void initialize() {
        RECT rect{};
        GetClientRect(window, &rect);
        target_width = std::max<LONG>(1, rect.right - rect.left);
        target_height = std::max<LONG>(1, rect.bottom - rect.top);
        const auto window_style = GetWindowLongPtrW(window, GWL_STYLE);
        const auto extended_style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        composition_mode = (window_style & WS_CHILD) == 0 &&
            (extended_style & WS_EX_NOREDIRECTIONBITMAP) != 0;

        constexpr D3D_FEATURE_LEVEL levels[] = {
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
        };
        D3D_FEATURE_LEVEL selected{};
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
            &device, &selected, &context), "D3D11CreateDevice");

        ComPtr<IDXGIDevice> dxgi_device;
        ComPtr<IDXGIAdapter> adapter;
        check(device.As(&dxgi_device), "query IDXGIDevice");
        ComPtr<IDXGIDevice1> dxgi_device1;
        if (SUCCEEDED(dxgi_device.As(&dxgi_device1))) {
            // Limit queued presents without waiting on the swap-chain latency
            // handle. On a WPF HwndHost child window that handle was observed
            // to signal every ~78 ms instead of each vblank.
            check(dxgi_device1->SetMaximumFrameLatency(1),
                "IDXGIDevice1 SetMaximumFrameLatency");
        }
        check(dxgi_device->GetAdapter(&adapter), "IDXGIDevice GetAdapter");
        check(adapter->GetParent(IID_PPV_ARGS(&factory)), "query IDXGIFactory2");
        factory_refresh_pending = true;
        const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        const auto hdr_monitor = monitor_hdr_state(factory.Get(), window);
        publish_diagnostic_field(diagnostic_output_snapshot,
            DiagnosticMonitorMask,
            static_cast<std::uint32_t>(hdr_monitor) << DiagnosticMonitorShift);
        last_probed_monitor = monitor;
        if (hdr_monitor != MonitorHdrCapability::Unknown) {
            configured_monitor = monitor;
            configured_monitor_hdr = hdr_monitor;
            ++monitor_generation;
            factory_refresh_pending = false;
        }
        const auto now = std::chrono::steady_clock::now();
        next_color_output_probe = now + (hdr_monitor == MonitorHdrCapability::Unknown
            ? std::chrono::milliseconds(250) : std::chrono::seconds(1));
        next_resize_allowed = {};
        DXGI_SWAP_CHAIN_DESC1 swap_description{};
        swap_description.Width = target_width;
        swap_description.Height = target_height;
        // Start in the inexpensive SDR format. The first HDR frame upgrades
        // the swap chain only when the window is actually on an HDR display.
        swap_description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swap_description.SampleDesc.Count = 1;
        swap_description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swap_description.BufferCount = 2;
        swap_description.Scaling = DXGI_SCALING_STRETCH;
        swap_description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swap_description.AlphaMode = composition_mode
            ? DXGI_ALPHA_MODE_PREMULTIPLIED
            : DXGI_ALPHA_MODE_IGNORE;
        const auto create_swap_chain = [&](DXGI_FORMAT format) -> HRESULT {
            swap_description.Format = format;
            ComPtr<IDXGISwapChain1> candidate;
            const auto result = composition_mode
                ? factory->CreateSwapChainForComposition(device.Get(), &swap_description,
                    nullptr, &candidate)
                : factory->CreateSwapChainForHwnd(device.Get(), window, &swap_description,
                    nullptr, nullptr, &candidate);
            if (FAILED(result)) return result;
            if (format == DXGI_FORMAT_R16G16B16A16_FLOAT) {
                ComPtr<IDXGISwapChain3> swap_chain3;
                UINT support{};
                if (FAILED(candidate.As(&swap_chain3)) ||
                    FAILED(swap_chain3->CheckColorSpaceSupport(
                        DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, &support)) ||
                    (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) == 0 ||
                    FAILED(swap_chain3->SetColorSpace1(
                        DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709))) {
                    return DXGI_ERROR_UNSUPPORTED;
                }
            }
            swap_chain = std::move(candidate);
            return S_OK;
        };
        const auto swap_result = create_swap_chain(swap_description.Format);
        check(swap_result, composition_mode
            ? "CreateSwapChainForComposition" : "CreateSwapChainForHwnd");

        if (composition_mode) {
            check(DCompositionCreateDevice(dxgi_device.Get(), IID_PPV_ARGS(&composition_device)),
                "DCompositionCreateDevice");
            check(composition_device->CreateTargetForHwnd(window, TRUE, &composition_target),
                "IDCompositionDevice CreateTargetForHwnd");
            check(composition_device->CreateVisual(&composition_visual),
                "IDCompositionDevice CreateVisual");
            check(composition_visual->SetContent(swap_chain.Get()),
                "IDCompositionVisual SetContent");
            check(composition_target->SetRoot(composition_visual.Get()),
                "IDCompositionTarget SetRoot");
            check(composition_device->Commit(), "IDCompositionDevice Commit");
        } else {
            (void)factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        }

        const auto vertex_blob = compile_shader(VertexShader, "main", "vs_5_0");
        const auto pixel_blob = compile_shader(PreviewPixelShaders, "nv12Main", "ps_5_0");
        const auto copy_pixel_blob = compile_shader(PreviewPixelShaders, "copyMain", "ps_5_0");
        const auto mask_pixel_blob = compile_shader(PreviewPixelShaders, "maskMain", "ps_5_0");
        check(device->CreateVertexShader(vertex_blob->GetBufferPointer(), vertex_blob->GetBufferSize(),
            nullptr, &vertex_shader), "CreateVertexShader");
        check(device->CreatePixelShader(pixel_blob->GetBufferPointer(), pixel_blob->GetBufferSize(),
            nullptr, &pixel_shader), "CreatePixelShader");
        check(device->CreatePixelShader(copy_pixel_blob->GetBufferPointer(),
            copy_pixel_blob->GetBufferSize(), nullptr, &copy_pixel_shader),
            "Create copy PixelShader");
        check(device->CreatePixelShader(mask_pixel_blob->GetBufferPointer(),
            mask_pixel_blob->GetBufferSize(), nullptr, &mask_pixel_shader),
            "Create mask PixelShader");

        D3D11_BUFFER_DESC constant_description{};
        constant_description.ByteWidth = sizeof(ShapeConstantData);
        constant_description.Usage = D3D11_USAGE_DYNAMIC;
        constant_description.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        constant_description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        check(device->CreateBuffer(&constant_description, nullptr, &shape_constants),
            "Create shape constant buffer");

        D3D11_SAMPLER_DESC sampler_description{};
        sampler_description.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sampler_description.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler_description.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler_description.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler_description.MaxLOD = D3D11_FLOAT32_MAX;
        check(device->CreateSamplerState(&sampler_description, &sampler), "CreateSamplerState");
        recreate_target();
        output_state = {};
        output_state.monitor_generation = monitor_generation;
        output_state.policy_generation =
            color_output_policy_generation.load(std::memory_order_acquire);
        output::commit_device_rebuild(output_state,
            output_surface_format(swap_description.Format),
            swap_description.Format == DXGI_FORMAT_R16G16B16A16_FLOAT
                ? output::AppliedColorSpace::Hdr
                : output::AppliedColorSpace::Sdr,
            true);
        publish_diagnostic_field(diagnostic_output_snapshot,
            DiagnosticHdrSurfaceMask,
            static_cast<std::uint32_t>(hdr_surface_active()) << DiagnosticHdrSurfaceShift);
        present_black();
        logging::write(std::format(
            "d3d_preview initialized feature_level=0x{:04X} target={}x{} mode={} output={} hdr_monitor={}",
            static_cast<unsigned>(selected), target_width, target_height,
            composition_mode ? "composition" : "hwnd",
            output_state.applied_color_space == output::AppliedColorSpace::Hdr
                ? "scrgb_fp16" : "sdr_bgra8",
            monitor_hdr_state_name(hdr_monitor)));
    }

    void release_device_resources() noexcept {
        if (context) {
            context->ClearState();
            if (context) context->Flush();
        }
        if (composition_visual) (void)composition_visual->SetContent(nullptr);
        if (composition_device) (void)composition_device->Commit();

        release_shared_gpu_frame();
        target.Reset();
        render_view.Reset();
        render_target.Reset();
        render_texture.Reset();
        y_view.Reset();
        uv_view.Reset();
        y_texture.Reset();
        uv_texture.Reset();
        sampler.Reset();
        shape_constants.Reset();
        mask_pixel_shader.Reset();
        copy_pixel_shader.Reset();
        pixel_shader.Reset();
        vertex_shader.Reset();
        composition_visual.Reset();
        composition_target.Reset();
        composition_device.Reset();
        swap_chain.Reset();
        factory.Reset();
        context.Reset();
        device.Reset();

        frame_width = frame_height = 0;
        render_width = render_height = 0;
        local_render_width = local_render_height = 0;
        using_limited_pass = false;
        scheduled_fps = 0;
        next_present_due = {};
        next_resize_allowed = {};
        output_state.target_valid = false;
        output_state.applied_color_space = output::AppliedColorSpace::Invalid;
        output_state.color_space_monitor_generation = 0;
        output_state.last_failure = output::Failure::DeviceLost;
        publish_diagnostic_field(diagnostic_output_snapshot,
            DiagnosticHdrSurfaceMask, 0);
    }

    void rebuild_device_resources() {
        const auto removed_reason = device ? device->GetDeviceRemovedReason() : E_POINTER;
        logging::write(std::format(
            "d3d_preview device_rebuild_begin removed_hr=0x{:08X}",
            static_cast<unsigned>(removed_reason)));
        release_device_resources();
        try {
            initialize();
            refresh_requested.store(true, std::memory_order_release);
            color_signature.store(0, std::memory_order_relaxed);
            logging::write("d3d_preview device_rebuild_complete");
        } catch (...) {
            release_device_resources();
            logging::write("d3d_preview device_rebuild_failed");
            throw;
        }
    }

    bool hdr_surface_active() const noexcept {
        return output_state.target_valid &&
            output_state.actual_format == output::SurfaceFormat::Rgba16Float &&
            output_state.applied_color_space == output::AppliedColorSpace::Hdr;
    }

    HRESULT try_recreate_target() noexcept {
        ComPtr<ID3D11Texture2D> back_buffer;
        auto result = swap_chain
            ? swap_chain->GetBuffer(0, IID_PPV_ARGS(&back_buffer)) : E_POINTER;
        if (FAILED(result)) return result;
        ComPtr<ID3D11RenderTargetView> replacement;
        result = device->CreateRenderTargetView(
            back_buffer.Get(), nullptr, &replacement);
        if (FAILED(result)) return result;
        target = std::move(replacement);
        return S_OK;
    }

    void recreate_target() {
        target.Reset();
        check(try_recreate_target(), "recreate swap-chain target");
    }

    void release_swap_chain_targets() noexcept {
        ID3D11ShaderResourceView* empty[] = {nullptr, nullptr, nullptr};
        context->PSSetShaderResources(0, 3, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        // Immediate contexts retain binding references after the owning ComPtr
        // is reset. ResizeBuffers requires every back-buffer reference gone.
        context->ClearState();
        context->Flush();
        target.Reset();
        render_view.Reset();
        render_target.Reset();
        render_texture.Reset();
        render_width = render_height = 0;
    }

    HRESULT set_swap_chain_color_space(output::Mode mode) noexcept {
        ComPtr<IDXGISwapChain3> swap_chain3;
        auto result = swap_chain.As(&swap_chain3);
        if (FAILED(result)) return result;
        const auto color_space = mode == output::Mode::Hdr
            ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
            : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        UINT support{};
        result = swap_chain3->CheckColorSpaceSupport(color_space, &support);
        if (FAILED(result)) return result;
        if ((support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) == 0)
            return DXGI_ERROR_UNSUPPORTED;
        return swap_chain3->SetColorSpace1(color_space);
    }

    bool update_output_mode(const media::DecodedFrame& frame) {
        publish_diagnostic_field(diagnostic_output_snapshot,
            DiagnosticSourceHdrMask,
            (frame.color.is_hdr() ? 2U : 1U) << DiagnosticSourceHdrShift);
        const auto now = std::chrono::steady_clock::now();
        const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        if (monitor != last_probed_monitor || now >= next_color_output_probe) {
            bool factory_refresh_failed{};
            if (factory && !factory->IsCurrent()) {
                ComPtr<IDXGIFactory2> replacement;
                const auto result = CreateDXGIFactory1(IID_PPV_ARGS(&replacement));
                if (SUCCEEDED(result)) {
                    factory = std::move(replacement);
                    factory_refresh_pending = true;
                } else {
                    factory_refresh_failed = true;
                }
                logging::write(std::format(
                    "d3d_preview factory_refresh hr=0x{:08X}",
                    static_cast<unsigned>(result)));
            }
            const auto active_hdr = factory_refresh_failed
                ? MonitorHdrCapability::Unknown : monitor_hdr_state(factory.Get(), window);
            publish_diagnostic_field(diagnostic_output_snapshot,
                DiagnosticMonitorMask,
                static_cast<std::uint32_t>(active_hdr) << DiagnosticMonitorShift);
            last_probed_monitor = monitor;
            if (active_hdr == MonitorHdrCapability::Unknown) {
                // DXGI output enumeration can be briefly unavailable while a
                // window crosses monitors. Keep the active format/color space
                // stable and retry without publishing a false SDR transition.
                next_color_output_probe = now + std::chrono::milliseconds(250);
                logging::write(std::format(
                    "d3d_preview monitor_probe monitor={} advanced_color=unknown "
                    "generation={} retry_ms=250",
                    reinterpret_cast<std::uintptr_t>(monitor), monitor_generation));
            } else {
                const bool capability_changed = monitor != configured_monitor ||
                    active_hdr != configured_monitor_hdr || factory_refresh_pending;
                if (capability_changed) ++monitor_generation;
                if (capability_changed) {
                    logging::write(std::format(
                        "d3d_preview monitor_changed monitor={} advanced_color={} generation={}",
                        reinterpret_cast<std::uintptr_t>(monitor),
                        monitor_hdr_state_name(active_hdr), monitor_generation));
                }
                configured_monitor = monitor;
                configured_monitor_hdr = active_hdr;
                factory_refresh_pending = false;
                next_color_output_probe = now + std::chrono::seconds(1);
            }
        }

        // The generation is published after the preference. Acquiring it first
        // guarantees that consuming a new generation also sees its policy.
        const auto policy_generation =
            color_output_policy_generation.load(std::memory_order_acquire);
        const auto preference = unpack_output_diagnostics(
            diagnostic_output_snapshot.load(std::memory_order_acquire))
            .requested_preference;
        const bool want_hdr = frame.color.is_hdr() &&
            configured_monitor_hdr == MonitorHdrCapability::Hdr &&
            preference != media::ColorOutputPreference::ForceSdrToneMap;
        const auto desired_mode = want_hdr ? output::Mode::Hdr : output::Mode::Sdr;
        const auto now_ms = steady_milliseconds();
        const auto plan = output::plan_update(
            output_state, desired_mode, monitor_generation,
            policy_generation, now_ms);
        if (plan.action == output::Action::None) return false;
        if (plan.action == output::Action::RebuildDevice) {
            rebuild_device_resources();
            return true;
        }

        struct Backend {
            Impl& owner;
            HRESULT resize_hr{S_FALSE};
            HRESULT color_hr{S_FALSE};
            HRESULT target_hr{S_FALSE};

            void release_targets() noexcept { owner.release_swap_chain_targets(); }
            output::Failure resize(output::SurfaceFormat format) noexcept {
                resize_hr = owner.swap_chain->ResizeBuffers(0,
                    owner.target_width, owner.target_height, dxgi_format(format), 0);
                return classify_output_failure(resize_hr);
            }
            output::Failure set_color_space(output::Mode mode) noexcept {
                color_hr = owner.set_swap_chain_color_space(mode);
                // Some drivers reject SetColorSpace1 with E_INVALIDARG even
                // after CheckColorSpaceSupport advertises PRESENT support.
                if (color_hr == E_INVALIDARG) return output::Failure::Unsupported;
                return classify_output_failure(color_hr);
            }
            output::Failure create_target() noexcept {
                target_hr = owner.try_recreate_target();
                return classify_output_failure(target_hr);
            }
        } backend{*this};

        const auto result = output::execute_transaction(output_state, backend, now_ms);
        publish_diagnostic_field(diagnostic_output_snapshot,
            DiagnosticHdrSurfaceMask,
            static_cast<std::uint32_t>(hdr_surface_active()) << DiagnosticHdrSurfaceShift);
        if (result.failure != output::Failure::None) {
            logging::write(std::format(
                "d3d_preview output_switch_failed requested={} failure={} "
                "resize_hr=0x{:08X} color_hr=0x{:08X} target_hr=0x{:08X} "
                "actual_format={} color_space={} target_valid={} retry_at_ms={}",
                want_hdr ? "hdr_scrgb" : "sdr_bgra8",
                output_failure_name(result.failure),
                static_cast<unsigned>(backend.resize_hr),
                static_cast<unsigned>(backend.color_hr),
                static_cast<unsigned>(backend.target_hr),
                static_cast<unsigned>(output_state.actual_format),
                static_cast<unsigned>(output_state.applied_color_space),
                output_state.target_valid ? "true" : "false",
                output_state.retry_not_before_ms));
        }
        if (result.rebuild_device) {
            rebuild_device_resources();
            return true;
        }
        if (result.applied) {
            color_signature.store(0, std::memory_order_relaxed);
            logging::write(std::format("d3d_preview output_switched output={}",
                hdr_surface_active() ? "hdr_scrgb" : "sdr_bgra8"));
        } else if (!output_state.target_valid) {
            refresh_requested.store(true, std::memory_order_release);
        }
        if (result.needs_redraw)
            color_signature.store(0, std::memory_order_relaxed);
        return result.applied || result.needs_redraw;
    }

    void update_shape_constants(UINT width, UINT height, bool enabled, int rotation = 0,
        const media::DecodedFrame* frame = nullptr) {
        ShapeConstantData values{};
        values.output_width = static_cast<float>(width);
        values.output_height = static_cast<float>(height);
        const auto packed_profile = corner_profile.load(std::memory_order_relaxed);
        const auto normalized_radius = std::bit_cast<float>(
            static_cast<std::uint32_t>(packed_profile >> 32U));
        const auto curve_exponent = std::bit_cast<float>(
            static_cast<std::uint32_t>(packed_profile & 0xFFFFFFFFU));
        values.corner_radius = static_cast<float>(std::min(width, height)) * normalized_radius;
        values.corner_exponent = curve_exponent;
        values.corner_enabled = enabled && normalized_radius > 0.0F ? 1.0F : 0.0F;
        values.rotation_quarter_turns = static_cast<float>(rotation);
        values.hdr_surface = hdr_surface_active() ? 1.0F : 0.0F;
        {
            std::scoped_lock lock(image_adjustments_mutex);
            values.brightness = image_adjustments.brightness;
            values.contrast = image_adjustments.contrast;
            values.saturation = image_adjustments.saturation;
            values.gamma = image_adjustments.gamma;
        }
        if (frame) {
            const auto conversion = media::detail::yuv_conversion_parameters(
                frame->pixel_format, frame->color.range, frame->color.matrix);
            values.y_offset = static_cast<float>(conversion.y_offset);
            values.y_scale = static_cast<float>(conversion.y_scale);
            values.chroma_offset = static_cast<float>(conversion.chroma_offset);
            values.chroma_scale = static_cast<float>(conversion.chroma_scale);
            values.red_cr = static_cast<float>(conversion.red_cr);
            values.green_cb = static_cast<float>(conversion.green_cb);
            values.green_cr = static_cast<float>(conversion.green_cr);
            values.blue_cb = static_cast<float>(conversion.blue_cb);
            if (frame->color.transfer == coremedia::TransferFunction::Pq)
                values.transfer_function = 1.0F;
            else if (frame->color.transfer == coremedia::TransferFunction::Hlg)
                values.transfer_function = 2.0F;
            if (frame->color.primaries == coremedia::ColorPrimaries::Bt2020)
                values.color_primaries = 1.0F;
            else if (frame->color.primaries == coremedia::ColorPrimaries::DisplayP3)
                values.color_primaries = 2.0F;
            const auto preference = unpack_output_diagnostics(
                diagnostic_output_snapshot.load(std::memory_order_acquire))
                .requested_preference;
            values.preserve_hdr = hdr_surface_active() && frame->color.is_hdr() &&
                preference != media::ColorOutputPreference::ForceSdrToneMap ? 1.0F : 0.0F;
            if (frame->color.hdr.max_mastering_luminance != 0)
                values.source_peak_nits = static_cast<float>(
                    frame->color.hdr.max_mastering_luminance);
            else if (frame->color.hdr.max_content_light_level != 0)
                values.source_peak_nits = static_cast<float>(
                    frame->color.hdr.max_content_light_level);
        }

        D3D11_MAPPED_SUBRESOURCE mapped{};
        check(context->Map(shape_constants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped),
            "Map shape constant buffer");
        std::memcpy(mapped.pData, &values, sizeof(values));
        context->Unmap(shape_constants.Get(), 0);
        context->PSSetConstantBuffers(0, 1, shape_constants.GetAddressOf());
    }

    bool rounded_window_enabled() const noexcept {
        // The native preview enters full screen by removing WS_THICKFRAME
        // instead of maximizing the HWND, so IsZoomed alone is insufficient.
        // Normal composition windows retain the frame solely for hit-tested
        // resizing; full-screen and maximized surfaces must remain rectangular.
        return composition_mode && !IsZoomed(window) &&
            GetPropW(window, L"iPhoneMirrorFullScreen") == nullptr;
    }

    void draw_black_background(bool rounded) {
        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(target_width);
        viewport.Height = static_cast<float>(target_height);
        viewport.MinDepth = 0;
        viewport.MaxDepth = 1;
        context->RSSetViewports(1, &viewport);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex_shader.Get(), nullptr, 0);
        context->PSSetShader(mask_pixel_shader.Get(), nullptr, 0);
        update_shape_constants(target_width, target_height, rounded);
        context->Draw(3, 0);
    }

    void present_black() {
        if (!target || !output_state.target_valid) return;
        constexpr float black[] = {0, 0, 0, 1};
        constexpr float transparent[] = {0, 0, 0, 0};
        context->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
        context->ClearRenderTargetView(target.Get(), composition_mode ? transparent : black);
        if (composition_mode) draw_black_background(rounded_window_enabled());
        check(swap_chain->Present(1, 0), "Present black frame");
    }

    bool resize_if_needed(std::chrono::steady_clock::time_point now) {
        RECT rect{};
        if (!GetClientRect(window, &rect)) return false;
        const auto width = static_cast<UINT>(std::max<LONG>(0, rect.right - rect.left));
        const auto height = static_cast<UINT>(std::max<LONG>(0, rect.bottom - rect.top));
        if (width == 0 || height == 0 || (width == target_width && height == target_height))
            return false;
        // Coalesce WM_SIZE bursts by sampling the newest client size at most
        // once per present interval. The final mismatch remains pending and
        // is applied on the next eligible render-thread iteration.
        if (now < next_resize_allowed) return false;
        next_resize_allowed = now + std::chrono::milliseconds(16);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        target.Reset();
        output_state.target_valid = false;
        const auto resize_result = swap_chain->ResizeBuffers(
            0, width, height, DXGI_FORMAT_UNKNOWN, 0);
        if (FAILED(resize_result)) {
            const auto target_result = try_recreate_target();
            output_state.target_valid = SUCCEEDED(target_result);
            logging::write(std::format(
                "d3d_preview resize_failed size={}x{} hr=0x{:08X} "
                "target_restore_hr=0x{:08X}",
                width, height, static_cast<unsigned>(resize_result),
                static_cast<unsigned>(target_result)));
            if (classify_output_failure(resize_result) == output::Failure::DeviceLost ||
                classify_output_failure(target_result) == output::Failure::DeviceLost) {
                output_state.last_failure = output::Failure::DeviceLost;
                output_state.applied_color_space = output::AppliedColorSpace::Invalid;
                output_state.color_space_monitor_generation = 0;
            }
            check(resize_result, "ResizeBuffers");
        }
        target_width = width;
        target_height = height;
        const auto target_result = try_recreate_target();
        output_state.target_valid = SUCCEEDED(target_result);
        if (classify_output_failure(target_result) == output::Failure::DeviceLost)
            output_state.last_failure = output::Failure::DeviceLost;
        check(target_result, "recreate resized swap-chain target");
        refresh_requested.store(true, std::memory_order_release);
        return true;
    }

    void release_shared_gpu_frame() noexcept {
        if (shared_gpu_acquired && shared_gpu_mutex) {
            if (context) context->Flush();
            // Shared frames can have several read-only consumers. Return the
            // consumer key after this renderer has submitted its sampling work.
            (void)shared_gpu_mutex->ReleaseSync(1);
        }
        shared_gpu_acquired = false;
        shared_gpu_mutex.Reset();
        shared_gpu_y_view.Reset();
        shared_gpu_uv_view.Reset();
        shared_gpu_texture.Reset();
        shared_gpu_frame.reset();
    }

    bool prepare_shared_gpu_frame(const media::DecodedFrame& frame) {
        const auto& gpu_frame = frame.gpu_frame;
        if (!gpu_frame || !gpu_frame->shared_handle || gpu_frame->width == 0 ||
            gpu_frame->height == 0) return false;
        if (shared_gpu_frame != gpu_frame) {
            release_shared_gpu_frame();
            ComPtr<ID3D11Device1> device1;
            check(device.As(&device1), "query D3D11 device 1");
            check(device1->OpenSharedResource1(
                static_cast<HANDLE>(gpu_frame->shared_handle),
                IID_PPV_ARGS(&shared_gpu_texture)), "open shared decoder texture");
            check(shared_gpu_texture.As(&shared_gpu_mutex),
                "query shared decoder keyed mutex");

            D3D11_SHADER_RESOURCE_VIEW_DESC y_description{};
            y_description.Format = gpu_frame->pixel_format == media::PixelFormat::P010
                ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
            y_description.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            y_description.Texture2D.MipLevels = 1;
            check(device->CreateShaderResourceView(shared_gpu_texture.Get(),
                &y_description, &shared_gpu_y_view), "create shared decoder Y view");

            D3D11_SHADER_RESOURCE_VIEW_DESC uv_description{};
            uv_description.Format = gpu_frame->pixel_format == media::PixelFormat::P010
                ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;
            uv_description.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            uv_description.Texture2D.MipLevels = 1;
            check(device->CreateShaderResourceView(shared_gpu_texture.Get(),
                &uv_description, &shared_gpu_uv_view), "create shared decoder UV view");
            shared_gpu_frame = gpu_frame;

        }
        const auto wait_result = shared_gpu_mutex->AcquireSync(1, 1000);
        if (wait_result != WAIT_OBJECT_0)
            throw std::runtime_error(std::format(
                "acquire shared decoder texture failed: 0x{:08X}",
                static_cast<unsigned>(wait_result)));
        shared_gpu_acquired = true;
        return true;
    }

    void recreate_video_textures(const media::DecodedFrame& frame) {
        frame_width = frame.width;
        frame_height = frame.height;
        texture_pixel_format = frame.pixel_format;
        y_view.Reset();
        uv_view.Reset();
        y_texture.Reset();
        uv_texture.Reset();

        D3D11_TEXTURE2D_DESC description{};
        description.Width = frame.width;
        description.Height = frame.height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = frame.pixel_format == media::PixelFormat::P010
            ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DYNAMIC;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        check(device->CreateTexture2D(&description, nullptr, &y_texture), "Create Y texture");
        check(device->CreateShaderResourceView(y_texture.Get(), nullptr, &y_view), "Create Y view");

        description.Width = (frame.width + 1U) / 2U;
        description.Height = (frame.height + 1U) / 2U;
        description.Format = frame.pixel_format == media::PixelFormat::P010
            ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;
        check(device->CreateTexture2D(&description, nullptr, &uv_texture), "Create UV texture");
        check(device->CreateShaderResourceView(uv_texture.Get(), nullptr, &uv_view), "Create UV view");
        logging::write(std::format("d3d_preview textures={}x{} format={}",
            frame.width, frame.height, media::pixel_format_name(frame.pixel_format)));
    }

    std::pair<UINT, UINT> limited_render_size(const media::DecodedFrame& frame) const {
        double scale = 1.0;
        const auto packed = render_size_limit.load(std::memory_order_relaxed);
        const auto limit_width = static_cast<std::uint32_t>(packed >> 32U);
        const auto limit_height = static_cast<std::uint32_t>(packed & 0xFFFFFFFFU);
        if (limit_width != 0 && limit_height != 0) {
            // Presets are expressed in conventional landscape order (for
            // example 1920x1080), while an iPhone frame can rotate at runtime.
            // Compare long/short edges so the same cap follows orientation.
            const auto source_long = std::max(frame.width, frame.height);
            const auto source_short = std::min(frame.width, frame.height);
            const auto limit_long = std::max(limit_width, limit_height);
            const auto limit_short = std::min(limit_width, limit_height);
            scale = std::min(scale, std::min(
                static_cast<double>(limit_long) / source_long,
                static_cast<double>(limit_short) / source_short));
        }

        scale = std::clamp(scale, 1.0 / std::max(frame.width, frame.height), 1.0);
        const auto width = std::max<UINT>(1, static_cast<UINT>(
            std::lround(static_cast<double>(frame.width) * scale)));
        const auto height = std::max<UINT>(1, static_cast<UINT>(
            std::lround(static_cast<double>(frame.height) * scale)));
        return {width, height};
    }

    void ensure_render_texture(UINT width, UINT height) {
        if (render_texture && width == render_width && height == render_height) return;
        ID3D11ShaderResourceView* empty[] = {nullptr, nullptr};
        context->PSSetShaderResources(0, 2, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        render_view.Reset();
        render_target.Reset();
        render_texture.Reset();

        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = dxgi_format(output_state.actual_format);
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        check(device->CreateTexture2D(&description, nullptr, &render_texture),
            "Create limited render texture");
        check(device->CreateRenderTargetView(render_texture.Get(), nullptr, &render_target),
            "Create limited render target");
        check(device->CreateShaderResourceView(render_texture.Get(), nullptr, &render_view),
            "Create limited render view");
        render_width = width;
        render_height = height;
        const auto packed = render_size_limit.load(std::memory_order_relaxed);
        logging::write(std::format(
            "d3d_preview render_texture={}x{} limit={}x{} window={}x{}",
            render_width, render_height, static_cast<std::uint32_t>(packed >> 32U),
            static_cast<std::uint32_t>(packed & 0xFFFFFFFFU), target_width, target_height));
    }

    bool upload_cpu(const media::DecodedFrame& frame) {
        if (!y_texture || frame.width != frame_width || frame.height != frame_height ||
            frame.pixel_format != texture_pixel_format) {
            recreate_video_textures(frame);
        }
        const auto source_stride = static_cast<std::size_t>(std::abs(frame.stride));
        const auto component_bytes = frame.pixel_format == media::PixelFormat::P010 ? 2U : 1U;
        const auto y_row_bytes = static_cast<std::size_t>(frame.width) * component_bytes;
        const auto uv_components = static_cast<std::size_t>((frame.width + 1U) / 2U) * 2U;
        const auto uv_row_bytes = uv_components * component_bytes;
        const auto allocated_height = allocated_nv12_height(frame, source_stride);
        const auto y_bytes = source_stride * allocated_height;
        const auto required = y_bytes + source_stride * ((allocated_height + 1U) / 2U);
        if (source_stride < std::max(y_row_bytes, uv_row_bytes) ||
            frame.nv12.size() < required) {
            throw std::runtime_error("invalid NV12/P010 frame layout for D3D preview");
        }
        const auto padding = leading_padding_rows(frame);
        const auto* source_y_plane = frame.nv12.data();
        const auto* source_uv_plane = source_y_plane + y_bytes;

        D3D11_MAPPED_SUBRESOURCE mapped{};
        check(context->Map(y_texture.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map Y texture");
        for (std::uint32_t row{}; row < frame.height; ++row) {
            auto* destination = static_cast<std::uint8_t*>(mapped.pData) +
                static_cast<std::size_t>(row) * mapped.RowPitch;
            const auto source_row = row + padding;
            if (source_row < frame.height) {
                std::memcpy(destination, source_y_plane +
                    static_cast<std::size_t>(source_row) * source_stride, y_row_bytes);
            } else {
                if (frame.pixel_format == media::PixelFormat::P010) {
                    const auto black = frame.color.range == coremedia::ColorRange::Full
                        ? std::uint16_t{} : static_cast<std::uint16_t>(64U << 6U);
                    std::fill_n(static_cast<std::uint16_t*>(mapped.pData) +
                        static_cast<std::size_t>(row) * (mapped.RowPitch / 2U),
                        frame.width, black);
                } else {
                    std::memset(destination,
                        frame.color.range == coremedia::ColorRange::Full ? 0 : 16,
                        y_row_bytes);
                }
            }
        }
        context->Unmap(y_texture.Get(), 0);

        check(context->Map(uv_texture.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map UV texture");
        const auto uv_height = (frame.height + 1U) / 2U;
        for (std::uint32_t row{}; row < uv_height; ++row) {
            auto* destination = static_cast<std::uint8_t*>(mapped.pData) +
                static_cast<std::size_t>(row) * mapped.RowPitch;
            const auto source_row = row + padding / 2U;
            if (source_row < uv_height) {
                std::memcpy(destination, source_uv_plane +
                    static_cast<std::size_t>(source_row) * source_stride, uv_row_bytes);
            } else {
                if (frame.pixel_format == media::PixelFormat::P010) {
                    std::fill_n(static_cast<std::uint16_t*>(mapped.pData) +
                        static_cast<std::size_t>(row) * (mapped.RowPitch / 2U),
                        uv_components, static_cast<std::uint16_t>(512U << 6U));
                } else {
                    std::memset(destination, 128, uv_row_bytes);
                }
            }
        }
        context->Unmap(uv_texture.Get(), 0);
        return true;
    }

    bool upload(const media::DecodedFrame& frame) {
        if (frame.gpu_frame) {
            try {
                if (prepare_shared_gpu_frame(frame)) {
                    frame_width = frame.width;
                    frame_height = frame.height;
                    texture_pixel_format = frame.pixel_format;
                    return true;
                }
            } catch (const std::exception& error) {
                logging::write(std::format(
                    "d3d_preview shared_frame_import_failed reason={}", error.what()));
            }
            if (frame.nv12.empty() && frame.gpu_frame) {
                auto materialized = frame;
                if (media::detail::materialize_gpu_frame(materialized))
                    return upload_cpu(materialized);
                return false;
            }
        }
        release_shared_gpu_frame();
        return upload_cpu(frame);
    }

    void render(const media::DecodedFrame& frame) {
        if (target_width == 0 || target_height == 0) return;
        if (!upload(frame)) return;

        const auto signature =
            (static_cast<std::uint64_t>(frame.pixel_format) << 32U) |
            (static_cast<std::uint64_t>(frame.color.primaries) << 24U) |
            (static_cast<std::uint64_t>(frame.color.transfer) << 16U) |
            (static_cast<std::uint64_t>(frame.color.matrix) << 8U) |
            static_cast<std::uint64_t>(frame.color.range);
        if (signature != color_signature.load(std::memory_order_relaxed)) {
            color_signature.store(signature, std::memory_order_relaxed);
            const auto preference = unpack_output_diagnostics(
                diagnostic_output_snapshot.load(std::memory_order_acquire))
                .requested_preference;
            logging::write(std::format(
                "d3d_preview color format={} primaries={} transfer={} matrix={} range={} "
                "source_hdr={} output={} policy={}",
                media::pixel_format_name(frame.pixel_format),
                media::color_primaries_name(frame.color.primaries),
                media::transfer_function_name(frame.color.transfer),
                media::matrix_coefficients_name(frame.color.matrix),
                media::color_range_name(frame.color.range),
                frame.color.is_hdr() ? "true" : "false",
                hdr_surface_active() && frame.color.is_hdr() &&
                    preference != media::ColorOutputPreference::ForceSdrToneMap
                    ? "hdr_scrgb" : "sdr_tonemap",
                static_cast<unsigned>(preference)));
        }

        const auto turns = ((rotation_quarter_turns.load(std::memory_order_relaxed) % 4) + 4) % 4;
        const bool swaps_axes = (turns & 1) != 0;
        const float source_aspect = swaps_axes
            ? static_cast<float>(frame.height) / frame.width
            : static_cast<float>(frame.width) / frame.height;
        const float target_aspect = static_cast<float>(target_width) / target_height;
        D3D11_VIEWPORT viewport{};
        if (target_aspect > source_aspect) {
            viewport.Height = static_cast<float>(target_height);
            viewport.Width = viewport.Height * source_aspect;
            viewport.TopLeftX = (static_cast<float>(target_width) - viewport.Width) * 0.5F;
        } else {
            viewport.Width = static_cast<float>(target_width);
            viewport.Height = viewport.Width / source_aspect;
            viewport.TopLeftY = (static_cast<float>(target_height) - viewport.Height) * 0.5F;
        }
        // The native window controller preserves the source aspect in integer
        // client pixels. That final integer rounding can leave the fitted
        // viewport less than one physical pixel short (for example 576x1253
        // for a 1206x2622 source), which appears as a thin black strip at the
        // bottom or side. Fill the complete client only for this sub-pixel
        // mismatch; genuine letterboxing remains unchanged.
        const float horizontal_gap = static_cast<float>(target_width) - viewport.Width;
        const float vertical_gap = static_cast<float>(target_height) - viewport.Height;
        const float aspect_error = std::abs(target_aspect - source_aspect) /
            std::max(source_aspect, 0.000001F);
        const float pixel_error_limit = 1.0F /
            static_cast<float>(std::max(target_width, target_height));
        if (horizontal_gap >= 0.0F && horizontal_gap < 1.0F &&
            vertical_gap >= 0.0F && vertical_gap < 1.0F &&
            aspect_error <= pixel_error_limit) {
            viewport.TopLeftX = 0.0F;
            viewport.TopLeftY = 0.0F;
            viewport.Width = static_cast<float>(target_width);
            viewport.Height = static_cast<float>(target_height);
        }
        viewport.MinDepth = 0;
        viewport.MaxDepth = 1;

        const auto packed_limit = render_size_limit.load(std::memory_order_relaxed);
        auto [limited_width, limited_height] = limited_render_size(frame);
        if (swaps_axes) std::swap(limited_width, limited_height);
        const bool cap_reduces_source = limited_width < frame.width || limited_height < frame.height;
        const bool use_limited_pass = packed_limit != 0 && cap_reduces_source &&
            (viewport.Width > static_cast<float>(limited_width) + 0.5F ||
             viewport.Height > static_cast<float>(limited_height) + 0.5F);
        const auto current_local_width = use_limited_pass
            ? limited_width
            : std::max<UINT>(1, static_cast<UINT>(std::lround(viewport.Width)));
        const auto current_local_height = use_limited_pass
            ? limited_height
            : std::max<UINT>(1, static_cast<UINT>(std::lround(viewport.Height)));
        if (current_local_width != local_render_width ||
            current_local_height != local_render_height ||
            use_limited_pass != using_limited_pass) {
            local_render_width = current_local_width;
            local_render_height = current_local_height;
            using_limited_pass = use_limited_pass;
            logging::write(std::format(
                "d3d_preview local_render={}x{} mode={} source={}x{} window={}x{} limit={}x{}",
                local_render_width, local_render_height,
                using_limited_pass ? "limited" : "direct",
                frame.width, frame.height, target_width, target_height,
                static_cast<std::uint32_t>(packed_limit >> 32U),
                static_cast<std::uint32_t>(packed_limit & 0xFFFFFFFFU)));
        }

        constexpr float black[] = {0, 0, 0, 1};
        constexpr float transparent[] = {0, 0, 0, 0};
        ID3D11ShaderResourceView* empty[] = {nullptr, nullptr, nullptr};
        const auto rounded = rounded_window_enabled();
        const auto draw_nv12 = [&](const D3D11_VIEWPORT& draw_viewport,
                                   UINT output_width, UINT output_height,
                                   bool apply_corner, int rotation) {
            context->RSSetViewports(1, &draw_viewport);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertex_shader.Get(), nullptr, 0);
            context->PSSetShader(pixel_shader.Get(), nullptr, 0);
            update_shape_constants(output_width, output_height, apply_corner, rotation, &frame);
            ID3D11ShaderResourceView* nv12_views[] = {
                shared_gpu_frame ? shared_gpu_y_view.Get() : y_view.Get(),
                shared_gpu_frame ? shared_gpu_uv_view.Get() : uv_view.Get()};
            context->PSSetShaderResources(0, 2, nv12_views);
            context->PSSetSamplers(0, 1, sampler.GetAddressOf());
            context->Draw(3, 0);
            context->PSSetShaderResources(0, 3, empty);
        };

        if (!use_limited_pass) {
            context->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
            context->ClearRenderTargetView(target.Get(), composition_mode ? transparent : black);
            if (composition_mode) draw_black_background(rounded);
            draw_nv12(viewport, target_width, target_height, rounded, turns);
        } else {
            ensure_render_texture(limited_width, limited_height);
            context->OMSetRenderTargets(1, render_target.GetAddressOf(), nullptr);
            context->ClearRenderTargetView(render_target.Get(), black);
            D3D11_VIEWPORT limited_viewport{};
            limited_viewport.Width = static_cast<float>(limited_width);
            limited_viewport.Height = static_cast<float>(limited_height);
            limited_viewport.MinDepth = 0;
            limited_viewport.MaxDepth = 1;
            draw_nv12(limited_viewport, limited_width, limited_height, false, turns);

            // A resource must not remain bound as an RTV when it becomes the
            // SRV for the copy pass; explicit unbinding avoids a driver-side
            // implicit synchronization/ownership transition.
            context->OMSetRenderTargets(0, nullptr, nullptr);
            context->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
            context->ClearRenderTargetView(target.Get(), composition_mode ? transparent : black);
            if (composition_mode) draw_black_background(rounded);
            context->RSSetViewports(1, &viewport);
            context->PSSetShader(copy_pixel_shader.Get(), nullptr, 0);
            update_shape_constants(target_width, target_height, rounded);
            ID3D11ShaderResourceView* copy_view = render_view.Get();
            context->PSSetShaderResources(2, 1, &copy_view);
            context->Draw(3, 0);
            context->PSSetShaderResources(0, 3, empty);
        }
        // The render loop applies the requested frame-rate cap. A nonblocking
        // flip presentation must not hold up the newest landscape frame when
        // DWM is late with a vblank; skip that presentation and keep capture
        // and decode independent of desktop composition.
        const auto present_result = swap_chain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
        if (present_result != DXGI_ERROR_WAS_STILL_DRAWING)
            check(present_result, "Present");
        release_shared_gpu_frame();
        ++rendered_frames;
        if (rendered_frames == 1) first_presented_at = std::chrono::steady_clock::now();
        DXGI_FRAME_STATISTICS frame_statistics{};
        const auto statistics_result = swap_chain->GetFrameStatistics(&frame_statistics);
        if (SUCCEEDED(statistics_result) && frame_statistics.SyncRefreshCount != 0) {
            const auto now = std::chrono::steady_clock::now();
            if (display_statistics_valid &&
                frame_statistics.SyncRefreshCount > last_sync_refresh_count &&
                last_sync_sample_at.time_since_epoch().count() != 0) {
                const auto elapsed = std::chrono::duration<double>(
                    now - last_sync_sample_at).count();
                if (elapsed > 0.05)
                    display_fps = static_cast<double>(
                        frame_statistics.SyncRefreshCount - last_sync_refresh_count) / elapsed;
            }
            last_sync_refresh_count = frame_statistics.SyncRefreshCount;
            last_sync_sample_at = now;
            display_statistics_valid = true;
        } else if (FAILED(statistics_result)) {
            display_statistics_valid = false;
            last_sync_refresh_count = 0;
            last_sync_sample_at = {};
        }
        if (rendered_frames <= 3 || rendered_frames % 300 == 0) {
            const auto elapsed = std::chrono::duration<double>(
                std::chrono::steady_clock::now() - first_presented_at).count();
            const auto present_fps = elapsed > 0.0
                ? static_cast<double>(rendered_frames - 1U) / elapsed
                : 0.0;
            const std::string display_fps_text = display_statistics_valid
                ? std::format("{:.2f}", display_fps) : "unavailable";
            logging::write(std::format(
                "d3d_preview render={} timestamp={} submit_fps={:.2f} display_fps={} "
                "fps_cap={} render={}x{} stats_hr=0x{:08X}",
                rendered_frames, frame.timestamp_100ns, present_fps,
                display_fps_text,
                max_fps.load(std::memory_order_relaxed),
                local_render_width, local_render_height,
                static_cast<unsigned>(statistics_result)));
        }
    }

    void run(std::stop_token token) noexcept {
        while (!token.stop_requested()) {
            try {
                if (!IsWindow(window)) break;
                if (output_state.last_failure == output::Failure::DeviceLost ||
                    !device || !context || !swap_chain) {
                    rebuild_device_resources();
                }
                // Coalesce interactive WM_SIZE bursts independently of media
                // FPS. A resize iteration bypasses the media deadline below
                // and immediately presents the newest retained frame.
                const bool target_resized = resize_if_needed(
                    std::chrono::steady_clock::now());
                if (clear_requested.exchange(false, std::memory_order_acq_rel)) {
                    last_frame.reset();
                    last_timestamp = 0;
                    publish_diagnostic_field(diagnostic_output_snapshot,
                        DiagnosticSourceHdrMask, 0);
                    refresh_requested.store(false, std::memory_order_release);
                    present_black();
                    logging::write("d3d_preview cleared");
                    continue;
                }
                const auto requested_fps = max_fps.load(std::memory_order_relaxed);
                const auto effective_fps = std::clamp<std::uint32_t>(requested_fps, 1, 120);
                if (requested_fps != 0) {
                    const auto now = std::chrono::steady_clock::now();
                    if (scheduled_fps != effective_fps ||
                        next_present_due.time_since_epoch().count() == 0) {
                        scheduled_fps = effective_fps;
                        next_present_due = now;
                    }
                    // Submit shortly before the target deadline so Present(1)
                    // lands on the intended vblank. Deadlines advance from the
                    // ideal cadence (not from the last actual present), which
                    // also gives 24/25 fps a correct 2/3-vblank pattern.
                    if (!target_resized &&
                        now + std::chrono::milliseconds(4) < next_present_due) {
                        std::this_thread::sleep_for(std::chrono::milliseconds(1));
                        continue;
                    }
                } else {
                    scheduled_fps = 0;
                    next_present_due = {};
                }
                const bool force_refresh = refresh_requested.exchange(false,
                    std::memory_order_acq_rel);
                auto frame = provider ? provider() : nullptr;
                if ((!frame || frame->timestamp_100ns == 0) && force_refresh)
                    frame = last_frame;
                if (!frame || frame->timestamp_100ns == 0) {
                    if (target_resized) present_black();
                    if (force_refresh) refresh_requested.store(true, std::memory_order_release);
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    continue;
                }
                const bool output_mode_changed = update_output_mode(*frame);
                if (!output_state.target_valid || !target) {
                    refresh_requested.store(true, std::memory_order_release);
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    continue;
                }
                if (!force_refresh && !target_resized && !output_mode_changed &&
                    frame->timestamp_100ns == last_timestamp) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    continue;
                }
                const auto render_started = std::chrono::steady_clock::now();
                render(*frame);
                const auto render_ms = std::chrono::duration<double, std::milli>(
                    std::chrono::steady_clock::now() - render_started).count();
                last_timestamp = frame->timestamp_100ns;
                last_frame = frame;
                if (requested_fps != 0) {
                    const auto interval = std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                        std::chrono::duration<double>(1.0 / static_cast<double>(effective_fps)));
                    const auto now = std::chrono::steady_clock::now();
                    do { next_present_due += interval; } while (next_present_due <= now);
                }
                if (render_ms >= 20.0 || rendered_frames <= 3 || rendered_frames % 300 == 0) {
                    logging::write(std::format("d3d_preview timing render={} render_ms={:.3f}",
                        rendered_frames, render_ms));
                }
            } catch (const std::exception& error) {
                const auto removed_reason = device
                    ? device->GetDeviceRemovedReason() : E_POINTER;
                if (FAILED(removed_reason)) {
                    output_state.last_failure = output::Failure::DeviceLost;
                    output_state.target_valid = false;
                    output_state.applied_color_space =
                        output::AppliedColorSpace::Invalid;
                    output_state.color_space_monitor_generation = 0;
                    publish_diagnostic_field(diagnostic_output_snapshot,
                        DiagnosticHdrSurfaceMask, 0);
                }
                logging::write(std::format(
                    "d3d_preview error={} removed_hr=0x{:08X}",
                    error.what(), static_cast<unsigned>(removed_reason)));
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
        }
        logging::write(std::format("d3d_preview stopped rendered={}", rendered_frames));
    }
};

D3D11PreviewRenderer::D3D11PreviewRenderer(HWND window, FrameProvider provider)
    : impl_(std::make_unique<Impl>(window, std::move(provider))) {}

D3D11PreviewRenderer::~D3D11PreviewRenderer() = default;

void D3D11PreviewRenderer::request_refresh() noexcept {
    if (impl_) impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::clear() noexcept {
    if (impl_) impl_->clear_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_max_fps(std::uint32_t fps) noexcept {
    if (!impl_) return;
    impl_->max_fps.store(std::min<std::uint32_t>(fps, 120), std::memory_order_relaxed);
    impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_render_size_limit(std::uint32_t width,
    std::uint32_t height) noexcept {
    if (!impl_) return;
    const auto packed = (static_cast<std::uint64_t>(width) << 32U) | height;
    impl_->render_size_limit.store(packed, std::memory_order_relaxed);
    impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_corner_profile(float normalized_radius,
    float curve_exponent) noexcept {
    if (!impl_) return;
    normalized_radius = std::clamp(normalized_radius, 0.0F, 0.5F);
    curve_exponent = std::clamp(curve_exponent, 1.5F, 8.0F);
    impl_->corner_profile.store(pack_corner_profile(normalized_radius, curve_exponent),
        std::memory_order_relaxed);
    impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_rotation(std::int32_t quarter_turns) noexcept {
    if (!impl_) return;
    impl_->rotation_quarter_turns.store(quarter_turns, std::memory_order_relaxed);
    impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_color_output_preference(
    media::ColorOutputPreference preference) noexcept {
    if (!impl_) return;
    const auto previous_snapshot = publish_diagnostic_field(
        impl_->diagnostic_output_snapshot,
        DiagnosticPreferenceMask,
        static_cast<std::uint32_t>(preference) << DiagnosticPreferenceShift);
    const auto previous = unpack_output_diagnostics(previous_snapshot)
        .requested_preference;
    if (previous != preference) {
        impl_->color_output_policy_generation.fetch_add(1, std::memory_order_acq_rel);
    }
    impl_->color_signature.store(0, std::memory_order_relaxed);
    impl_->refresh_requested.store(true, std::memory_order_release);
}

void D3D11PreviewRenderer::set_image_adjustments(float brightness,
    float contrast, float saturation, float gamma) noexcept {
    if (!impl_) return;
    {
        std::scoped_lock lock(impl_->image_adjustments_mutex);
        impl_->image_adjustments = {brightness, contrast, saturation, gamma};
    }
    impl_->refresh_requested.store(true, std::memory_order_release);
}

OutputDiagnostics D3D11PreviewRenderer::output_diagnostics() const noexcept {
    if (!impl_) return {};
    return unpack_output_diagnostics(
        impl_->diagnostic_output_snapshot.load(std::memory_order_acquire));
}

} // namespace iPhoneMirror::renderer
