# Windows 低延迟解码与显示路径

## 结论

公开实现均将 USB 接收、H.264 解码和显示拆成独立阶段。高性能实现不会把每帧转成
BGRA/RGB 后交给 WPF/QImage，而是让系统解码器和 GPU sink 直接消费视频帧。

本项目当前生产路径：

```text
QuickTime USB FEED
  -> AVCC 转 Annex-B
  -> 有界压缩包 FIFO / 独立 Media Foundation 解码线程
  -> MF_MT_VIDEO_NO_FRAME_ORDERING（实时顺序流）
  -> 已解码帧 mailbox（积压超过 2 帧时跳到最新完整帧）
  -> D3D11 Y/UV 纹理 + pixel shader
  -> 主界面：HwndHost flip-model swapchain
  -> 独立窗口：DirectComposition premultiplied-alpha swapchain
  -> Present(vsync)
```

WPF 不再逐帧调用 `im_copy_latest_video_frame_scaled`，也不再创建或更新
`WriteableBitmap`。

独立窗口使用 `WS_EX_NOREDIRECTIONBITMAP` 与
`CreateSwapChainForComposition`。像素着色器对 Apple 官方 iPhone 17 Pro Product
Bezel 的 1:1 Screen 蒙版进行连续圆角覆盖（`R = 0.1784 × 短边`，
`n = 2.36`），并输出 premultiplied alpha。因此透明边缘由 DWM 合成，
不再经过 1-bit `SetWindowRgn` 裁剪。

## 本地渲染分辨率上限

GUI 的原生/1080p/720p/540p 是 Windows 渲染上限，不改变 QuickTime、USB H.264
传输或 Media Foundation 解码尺寸。长短边会随手机旋转自动对应，例如 1206×2622
竖屏源在 540p 档的上限为约 442×960。

- 当 HWND 实际显示区域小于所选上限时，继续单遍 `NV12 → SwapChain`，避免二次采样；
- 当全屏或 OBS 窗口超过上限时，先渲染到受限 BGRA GPU 纹理，再线性缩放到
  SwapChain；
- 中间纹理只在 D3D worker 线程创建，宽高限制以一个 64-bit atomic 同步；
- 每次 RTV/SRV 角色切换都显式解绑，避免 D3D11 隐式资源冲突。

因此该设置能降低大窗口的像素着色/显存带宽，但不会降低 USB 带宽、H.264 解码负载
或改变截图读取的原始解码帧。

## 真机数据（iPhone18,3 / iOS 26.5.2）

- QuickTime 输入：约 58.5 fps。
- 修复前 MFT 发布：255 / 451，约 33.0 fps。
- 修复后 MFT 发布：452 / 466，约 56.8 fps。
- 独立 D3D11 真机测试：MFT 输出 330 帧，swapchain 呈现 317 帧，流阶段约 57–58 fps。
- 分辨率：1206 x 2622；NV12 stride 1216。
- MFT 分配高度为 2624，可见高度为 2622。UV 平面必须按分配高度定位。
- 禁止无必要的 frame ordering 后，MFT 第 120/240/360/480 帧的输入与输出 PTS
  逐帧相等，不再出现约 14 帧（约 233 ms）的周期性滞后。
- 修复后的 GUI 实测约 59–60 次呈现/秒，USB 接收至选帧通常约 5–19 ms。
- 540p/24fps GUI 回归：主窗口单遍 326×709；OBS 窗口两遍 442×960；第 300 帧
  24.00 fps、第 600 帧 23.85 fps，未出现 D3D11 资源错误。

## 关键修复

1. `MediaFoundationH264Decoder::decode` 返回所有即时可用的 MFT 输出，不能只保留最后一帧。
2. 压缩 H.264 输入严格 FIFO，已解码画面采用低延迟 mailbox；只有已完整解码的陈旧
   画面可被跳过，不能任意丢弃压缩 P 帧。
3. 捕获时 GUI 只读取 capture status，不再枚举 Apple/USB/libusb 设备。
4. 热路径日志采样，后台线程约每 200 ms flush；GUI 折叠日志面板时停止文件轮询。
5. CPU fallback 按 NV12 allocated height 计算 UV offset。
6. Swapchain 通过 `IDXGIDevice1::SetMaximumFrameLatency(1)` 限制排队。曾尝试的
   frame-latency waitable handle 在 WPF `HwndHost` 子窗口上实测约 78 ms 才唤醒，已移除。

## 下一阶段：真正零拷贝硬解

当前 D3D11 renderer 已移除 WPF 瓶颈，但 Media Foundation 仍输出系统内存 NV12。
最终路径应让 decoder、VideoProcessor 和 swapchain 共用一个 D3D11 device：

```text
MFT_MESSAGE_SET_D3D_MANAGER
  -> IMFDXGIBuffer::GetResource / GetSubresourceIndex
  -> ID3D11VideoProcessor::VideoProcessorBlt
  -> flip-model HWND swapchain
```

该路径不得调用 `ConvertToContiguousBuffer`、`Lock`、`nv12.assign` 或 `Marshal.Copy`。

## 参考

- [quicktime_video_hack GStreamer adapter](https://github.com/danielpaulus/quicktime_video_hack/blob/main/screencapture/gstadapter/gst_adapter.go#L222)
- [quicktime_video_hack macOS hardware pipeline](https://github.com/danielpaulus/quicktime_video_hack/blob/main/screencapture/gstadapter/gst_pipeline_builder_mac.go#L15)
- [Microsoft: Supporting Direct3D 11 Video Decoding in Media Foundation](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation)
- [Microsoft: IMFDXGIBuffer::GetResource](https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nf-mfobjects-imfdxgibuffer-getresource)
- [Microsoft: IMFDXGIBuffer::GetSubresourceIndex](https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nf-mfobjects-imfdxgibuffer-getsubresourceindex)
- [Microsoft: DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)
- [Chromium DXVA decoder](https://chromium.googlesource.com/chromium/src/+/ac910b82aa3017c2a5fbf28fca211045c0e72f2a/media/gpu/windows/dxva_video_decode_accelerator_win.cc)
