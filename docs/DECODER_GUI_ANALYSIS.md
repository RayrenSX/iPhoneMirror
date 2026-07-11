# H.264 解码与 GUI 显示对照分析

## 参考项目

### `danielpaulus/quicktime_video_hack`

该项目的核心职责是 USB/QuickTime/CoreMedia 协议，不在协议线程里做 GUI。`FEED` 包解析成
`CMSampleBuffer` 后交给 `CmSampleBufConsumer`，随后立即发送 `ASYN NEED`，使设备继续发送下一帧。
显示由 GStreamer adapter 完成，macOS 示例使用 `vtdec` 硬件解码，Linux 示例使用 `avdec_h264` 软件解码。
因此它的关键设计是：协议接收、消费/解码、渲染由管线和队列解耦，而不是在 USB 读取线程里完成完整的 RGB 转换。

### `chotgpt/quicktime_video_hack_windows`

Windows 参考工程的测试解码器调用 FFmpeg：

1. `avcodec_find_decoder(AV_CODEC_ID_H264)` 创建软件 H.264 decoder；
2. AVCC 长度前缀转换为 Annex-B；
3. `avcodec_send_packet` 后循环 `avcodec_receive_frame`；
4. 输入回调只把最新 `AVFrame` 缓存起来，旧帧被替换；
5. 独立 Qt 线程每 `1000 / 60` ms 取出最新帧，使用 `sws_scale` 转成 RGB，再构造 `QImage` 回调给 Qt widget。

这个项目的低延迟来源是“只保留最新帧”和“显示线程独立”，而不是硬件解码。其 Qt 预览是 `QImage` + 自定义 QWidget 绘制，代码本身没有 D3D11 硬件路径。

## 爱思投屏的本机二进制证据

本机 `C:\Program Files\i4AirPlayer` 中：

- `i4AirPlayer.exe` / `libmediacore_2.dll` 使用 `avcodec-61.dll`、`avutil-59.dll`、`swscale-8.dll`；
- 导入 `avcodec_get_hw_config`、`av_hwdevice_ctx_create`、`av_hwframe_transfer_data`；
- 导入 `d3d11.dll!D3D11CreateDevice`；
- `libmediacore_2.dll` 导出 `StreamCapture_SetD3D11HWDeviceCtxCreate`、`StreamCapture_TryGrabFrame`；
- 二进制字符串包含 `D3D11`、`PS_NV12.hlsl`、`PS_NV12`、`DXVA2`、`gpuSchedule`、`render texture`。

这组符号表明爱思的典型路径是：

```text
H264 -> FFmpeg AVCodec/D3D11VA -> D3D11 NV12 texture
     -> GPU shader NV12/YUV -> RGB -> swap-chain/Qt widget
```

`av_hwframe_transfer_data` 很可能只在录制、截图或虚拟摄像头输出时把 GPU 帧回读到 CPU；预览本身不需要每帧 CPU BGRA 拷贝。`libvirtualcam.dll` 也说明它有独立的虚拟摄像头输出链路。

## 当前项目的确定问题

当前项目的 USB 和协议不是主要瓶颈：真机日志显示 1206×2622、约 58.7 FPS、48 kHz 双声道，`sample_count=1` 且 USB 后端稳定。

当前 Media Foundation 日志显示：

```text
mf_decoder selected=MSH264DecoderMFT
h264_acceleration_hr=0x80070057
```

即当前 MFT 没有接受硬件加速设置，实际走软件解码。大 H.264 样本（约 400–520 KB）会出现 54–63 ms 解码尖峰；同时原生代码还要逐像素执行 NV12→BGRA，再把约 8–12 MB 的图像复制到 WPF `WriteableBitmap`。状态栏的 FPS 是协议收到帧数，`latency_ms` 只是最后一次解码调用，不代表真实呈现延迟。

另外，旧版 Media Foundation 代码在 `ProcessInput` 返回 `MF_E_NOTACCEPTING` 时只取一个输出并直接返回，可能丢掉尚未重新提交的输入样本。该逻辑已经修复为：先排空所有可用输出，再重试同一个输入；每次也会循环取完当前输出队列，只保留最新帧，行为与 Windows 参考项目一致。

## 后续正确路线

1. 保留当前协议和设备后端；
2. 保留“最新帧”有界队列，避免 USB 线程积压；
3. 增加 FFmpeg D3D11VA decoder（动态加载 FFmpeg DLL 或引入可再发布的 FFmpeg 构建）；
4. 用 D3D11 NV12 双平面纹理和 shader 直接渲染，预览不再经过 CPU BGRA；
5. 录制/OBS 虚拟摄像头再按需要执行 GPU→CPU 回读；
6. 单独统计 `capture timestamp → decoded timestamp → submitted timestamp → rendered timestamp`，替换当前“协议 FPS/单次 decode_ms”指标。

## 参考来源

- <https://github.com/danielpaulus/quicktime_video_hack>
- <https://github.com/danielpaulus/quicktime_video_hack/blob/main/doc/technical_documentation.md>
- <https://github.com/chotgpt/quicktime_video_hack_windows>
- <https://github.com/chotgpt/quicktime_video_hack_windows/blob/main/qt_ios_line_cast_screen/src/H264Decoder.cpp>
