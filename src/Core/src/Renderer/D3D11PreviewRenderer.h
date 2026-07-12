#pragma once

#include "Media/MediaFoundationDecoder.h"

#include <Windows.h>

#include <functional>
#include <memory>

namespace iPhoneMirror::renderer {

class D3D11PreviewRenderer {
public:
    using FrameProvider = std::function<std::shared_ptr<const media::DecodedFrame>()>;

    D3D11PreviewRenderer(HWND window, FrameProvider provider);
    ~D3D11PreviewRenderer();
    D3D11PreviewRenderer(const D3D11PreviewRenderer&) = delete;
    D3D11PreviewRenderer& operator=(const D3D11PreviewRenderer&) = delete;

    // Re-present the newest complete frame even when its media timestamp has
    // not changed. Used after a window resize/device restore and by the GUI's
    // explicit refresh action.
    void request_refresh() noexcept;
    // Drops the retained frame and presents black on the render thread. This
    // prevents the previous iPhone's last frame from remaining visible after
    // the user selects another device.
    void clear() noexcept;
    void set_max_fps(std::uint32_t fps) noexcept;
    // Limits the local GPU intermediate render texture while leaving the
    // decoded/USB source untouched. (0,0) uses native source resolution.
    void set_render_size_limit(std::uint32_t width, std::uint32_t height) noexcept;
    // Configures the top-level preview's continuous display outline. Radius is
    // normalized to the short edge; zero disables clipping. Child/main-window
    // previews remain rectangular because their WPF panel owns that shape.
    void set_corner_profile(float normalized_radius, float curve_exponent) noexcept;
    void set_rotation(std::int32_t quarter_turns) noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace iPhoneMirror::renderer
