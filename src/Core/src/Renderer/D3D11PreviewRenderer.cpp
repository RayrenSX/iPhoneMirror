#include "Renderer/D3D11PreviewRenderer.h"

#include "Logging.h"

#include <d3d11.h>
#include <d3dcompiler.h>
#include <dcomp.h>
#include <dxgi1_3.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstring>
#include <format>
#include <stdexcept>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::renderer {
namespace {

std::uint64_t pack_corner_profile(float normalized_radius, float curve_exponent) noexcept {
    return (static_cast<std::uint64_t>(std::bit_cast<std::uint32_t>(normalized_radius)) << 32U) |
        std::bit_cast<std::uint32_t>(curve_exponent);
}

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(std::format("{} failed: 0x{:08X}", operation,
            static_cast<unsigned>(result)));
    }
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

float4 nv12Main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    uv = rotateUv(uv);
    float y = max(0.0, yPlane.Sample(linearSampler, uv) - (16.0 / 255.0)) * 1.16438356;
    float2 chroma = uvPlane.Sample(linearSampler, uv) - float2(0.5, 0.5);
    float3 rgb;
    rgb.r = y + 1.79274107 * chroma.y;
    rgb.g = y - 0.21324861 * chroma.x - 0.53290933 * chroma.y;
    rgb.b = y + 2.11240179 * chroma.x;
    return premultiplyForCorner(saturate(rgb), position.xy);
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
    };

    HWND window{};
    FrameProvider provider;
    std::jthread worker;
    bool composition_mode{};

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
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
    ComPtr<ID3D11Texture2D> render_texture;
    ComPtr<ID3D11RenderTargetView> render_target;
    ComPtr<ID3D11ShaderResourceView> render_view;

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
    std::atomic_bool refresh_requested{true};
    std::atomic_bool clear_requested{};
    std::atomic_uint32_t max_fps{60};
    // Packed as width in the high dword and height in the low dword so the
    // render thread never observes a mixed pair during a live preset change.
    std::atomic_uint64_t render_size_limit{};
    // Radius/exponent are published as one atomic value so a live device
    // switch cannot render one frame using a mixed profile.
    std::atomic_uint64_t corner_profile{pack_corner_profile(0.1784F, 2.36F)};
    std::atomic_int rotation_quarter_turns{};
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
        ComPtr<IDXGIFactory2> factory;
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
        DXGI_SWAP_CHAIN_DESC1 swap_description{};
        swap_description.Width = target_width;
        swap_description.Height = target_height;
        swap_description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swap_description.SampleDesc.Count = 1;
        swap_description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swap_description.BufferCount = 2;
        swap_description.Scaling = DXGI_SCALING_STRETCH;
        swap_description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swap_description.AlphaMode = composition_mode
            ? DXGI_ALPHA_MODE_PREMULTIPLIED
            : DXGI_ALPHA_MODE_IGNORE;
        if (composition_mode) {
            check(factory->CreateSwapChainForComposition(device.Get(), &swap_description,
                nullptr, &swap_chain), "CreateSwapChainForComposition");
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
            check(factory->CreateSwapChainForHwnd(device.Get(), window, &swap_description,
                nullptr, nullptr, &swap_chain), "CreateSwapChainForHwnd");
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
        present_black();
        logging::write(std::format(
            "d3d_preview initialized feature_level=0x{:04X} target={}x{} mode={}",
            static_cast<unsigned>(selected), target_width, target_height,
            composition_mode ? "composition" : "hwnd"));
    }

    void recreate_target() {
        target.Reset();
        ComPtr<ID3D11Texture2D> back_buffer;
        check(swap_chain->GetBuffer(0, IID_PPV_ARGS(&back_buffer)), "swap chain GetBuffer");
        check(device->CreateRenderTargetView(back_buffer.Get(), nullptr, &target),
            "CreateRenderTargetView");
    }

    void update_shape_constants(UINT width, UINT height, bool enabled, int rotation = 0) {
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
        constexpr float black[] = {0, 0, 0, 1};
        constexpr float transparent[] = {0, 0, 0, 0};
        context->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
        context->ClearRenderTargetView(target.Get(), composition_mode ? transparent : black);
        if (composition_mode) draw_black_background(rounded_window_enabled());
        (void)swap_chain->Present(1, 0);
    }

    bool resize_if_needed() {
        RECT rect{};
        if (!GetClientRect(window, &rect)) return false;
        const auto width = static_cast<UINT>(std::max<LONG>(0, rect.right - rect.left));
        const auto height = static_cast<UINT>(std::max<LONG>(0, rect.bottom - rect.top));
        if (width == 0 || height == 0 || (width == target_width && height == target_height))
            return false;
        context->OMSetRenderTargets(0, nullptr, nullptr);
        target.Reset();
        check(swap_chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0),
            "ResizeBuffers");
        target_width = width;
        target_height = height;
        recreate_target();
        return true;
    }

    void recreate_video_textures(const media::DecodedFrame& frame) {
        frame_width = frame.width;
        frame_height = frame.height;
        y_view.Reset();
        uv_view.Reset();
        y_texture.Reset();
        uv_texture.Reset();

        D3D11_TEXTURE2D_DESC description{};
        description.Width = frame.width;
        description.Height = frame.height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = DXGI_FORMAT_R8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DYNAMIC;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        check(device->CreateTexture2D(&description, nullptr, &y_texture), "Create Y texture");
        check(device->CreateShaderResourceView(y_texture.Get(), nullptr, &y_view), "Create Y view");

        description.Width = (frame.width + 1U) / 2U;
        description.Height = (frame.height + 1U) / 2U;
        description.Format = DXGI_FORMAT_R8G8_UNORM;
        check(device->CreateTexture2D(&description, nullptr, &uv_texture), "Create UV texture");
        check(device->CreateShaderResourceView(uv_texture.Get(), nullptr, &uv_view), "Create UV view");
        logging::write(std::format("d3d_preview textures={}x{}", frame.width, frame.height));
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
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
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

    void upload(const media::DecodedFrame& frame) {
        if (!y_texture || frame.width != frame_width || frame.height != frame_height) {
            recreate_video_textures(frame);
        }
        const auto source_stride = static_cast<std::size_t>(std::abs(frame.stride));
        const auto allocated_height = allocated_nv12_height(frame, source_stride);
        const auto y_bytes = source_stride * allocated_height;
        const auto required = y_bytes + source_stride * ((allocated_height + 1U) / 2U);
        if (source_stride < frame.width || frame.nv12.size() < required) {
            throw std::runtime_error("invalid NV12 frame layout for D3D preview");
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
                    static_cast<std::size_t>(source_row) * source_stride, frame.width);
            } else {
                std::memset(destination, 16, frame.width);
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
                    static_cast<std::size_t>(source_row) * source_stride, frame.width);
            } else {
                std::memset(destination, 128, frame.width);
            }
        }
        context->Unmap(uv_texture.Get(), 0);
    }

    void render(const media::DecodedFrame& frame) {
        (void)resize_if_needed();
        if (target_width == 0 || target_height == 0) return;
        upload(frame);

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
            update_shape_constants(output_width, output_height, apply_corner, rotation);
            ID3D11ShaderResourceView* nv12_views[] = {y_view.Get(), uv_view.Get()};
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
        check(swap_chain->Present(1, 0), "Present");
        ++rendered_frames;
        if (rendered_frames == 1) first_presented_at = std::chrono::steady_clock::now();
        if (rendered_frames <= 3 || rendered_frames % 300 == 0) {
            const auto elapsed = std::chrono::duration<double>(
                std::chrono::steady_clock::now() - first_presented_at).count();
            const auto present_fps = elapsed > 0.0
                ? static_cast<double>(rendered_frames - 1U) / elapsed
                : 0.0;
            logging::write(std::format(
                "d3d_preview render={} timestamp={} present_fps={:.2f} fps_cap={} render={}x{}",
                rendered_frames, frame.timestamp_100ns, present_fps,
                max_fps.load(std::memory_order_relaxed),
                local_render_width, local_render_height));
        }
    }

    void run(std::stop_token token) noexcept {
        while (!token.stop_requested()) {
            try {
                if (!IsWindow(window)) break;
                // The aspect controller can resize a newly opened window
                // before capture has produced its first frame.  Keep the
                // composition swap chain matched to the client rectangle even
                // while idle; otherwise the uncovered area is transparent and
                // exposes pieces of the main window underneath.
                const bool target_resized = resize_if_needed();
                if (clear_requested.exchange(false, std::memory_order_acq_rel)) {
                    last_frame.reset();
                    last_timestamp = 0;
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
                    if (now + std::chrono::milliseconds(4) < next_present_due) {
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
                if (!force_refresh && !target_resized &&
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
                logging::write(std::format("d3d_preview error={}", error.what()));
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

} // namespace iPhoneMirror::renderer
