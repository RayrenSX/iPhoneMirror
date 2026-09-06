# H.264/HEVC 解码与 GUI 显示路径分析

> 本文区分历史测量和当前实现。当前基线为 `v1.8.3` 及其后的工作区代码；旧日志中的
> 单机结果不能直接当作所有 Windows 电脑的运行结论。

## 参考项目

### `danielpaulus/quicktime_video_hack`

该项目的核心职责是 USB/QuickTime/CoreMedia 协议，不在协议线程里做 GUI。`FEED` 包解析成
`CMSampleBuffer` 后交给 `CmSampleBufConsumer`，随后立即发送 `ASYN NEED`，使设备继续发送下一帧。
显示由 GStreamer adapter 完成，macOS 示例使用 `vtdec` 硬件解码，Linux 示例使用 `avdec_h264`
软件解码。因此它的关键设计是：协议接收、消费/解码、渲染由管线和队列解耦，而不是在 USB
读取线程里完成完整的 RGB 转换。

### `chotgpt/quicktime_video_hack_windows`

Windows 参考工程的测试解码器调用 FFmpeg：

1. `avcodec_find_decoder(AV_CODEC_ID_H264)` 创建软件 H.264 decoder；
2. AVCC 长度前缀转换为 Annex-B；
3. `avcodec_send_packet` 后循环 `avcodec_receive_frame`；
4. 输入回调只把最新 `AVFrame` 缓存起来，旧帧被替换；
5. 独立 Qt 线程每 `1000 / 60` ms 取出最新帧，使用 `sws_scale` 转成 RGB，再构造 `QImage` 回调给 Qt widget。

这个项目的低延迟来源是“只保留最新帧”和“显示线程独立”，而不是硬件解码。其 Qt 预览是
`QImage` + 自定义 QWidget 绘制，代码本身没有 D3D11 硬件路径。

## 当前项目的生产路径

USB 视频路径现在是：

```text
QuickTime FEED (AVCC)
  -> Annex-B
  -> 有界压缩 FIFO
  -> MediaFoundationVideoDecoder
       -> Auto/硬件优先：硬件 MFT、DXVA 能力、软件 MFT 依次尝试
       -> 软件兼容：明确关闭 H.264 加速
       -> NV12/P010 + 实际解码器状态
  -> 已解码帧 mailbox（过载时丢弃陈旧完整帧）
  -> D3D11 Y/UV shader
  -> DirectComposition / flip-model 预览
```

`MediaFoundationVideoDecoder` 会读取 MFT 的硬件标记、DXVA 状态和 DXGI 输出，并在状态接口
中区分“请求的策略”和“实际使用的引擎”。硬件 MFT 或 DXVA 输出可能是 `IMFDXGIBuffer`；
为避免跨设备 keyed-mutex 在旋转或窗口切换时阻塞 USB，当前生产交接会把该纹理安全回读为
CPU NV12/P010，再上传到预览设备。软件 MFT 直接产生 CPU 帧。共享纹理池和导入逻辑仍保留，
但尚未作为所有机器的默认交接路径。

WPF 不再逐帧创建或更新 `WriteableBitmap`。截图、录制、推流和虚拟摄像头通过有界的帧导出
接口按需 materialize/readback；编码回压不会反向阻塞 USB 读取和协议回复。

无线屏幕镜像由独立 `iPhoneMirror.WirelessHost.exe` 解码为 I420，经命名管道送入同一套
会话、D3D11 预览、WASAPI 和输出接口。无线视频 App 投屏则把 HTTP(S)/HLS 地址送入独立的
WPF 播放面，并可从媒体源本身解码音频；它不是控制中心的屏幕镜像帧。

## 历史测量与解释

早期单机日志曾显示：

```text
mf_decoder selected=MSH264DecoderMFT
h264_acceleration_hr=0x80070057
```

这只说明当时的 MFT 没有接受那台机器上的硬件设置，并不代表当前代码或所有硬件都只能软件
解码。同期 400–520 KB H.264 样本出现 54–63 ms 解码尖峰，且 GUI 端存在逐像素 NV12→BGRA
和 `WriteableBitmap` 复制；这条旧路径已由原生 D3D11 预览、硬件候选探测和有界 mailbox 替代。

当前仍应把以下量分开观察：

- 协议收到帧数（capture FPS）；
- 解码器提交/输出帧数与实际 `decoder_runtime_mode`；
- 预览 swapchain 的呈现帧数；
- `capture timestamp → decoded timestamp → presented timestamp` 的端到端延迟。

状态栏中的 FPS 或单次 `decode_ms` 不能单独代表最终显示延迟。

## 色彩范围

SDR 屏幕镜像统一按 full-range BT.709 处理：无线 I420 帧在接收时标记为 Full，有线非 HDR
帧在发布前归一化为 Full，HDR 帧保留解码器提供的范围与 PQ/HLG 信息。D3D11 shader、NV12
导出、FFmpeg 输出和 Windows 11 虚拟摄像头使用一致的 primaries/transfer/matrix/range
描述；PNG 截图保存最终 RGB，面向编码和摄像头的 SDR 导出还会统一转换到 full-range BT.709，避免浅色 UI 灰阶在
下游按 video-range 二次扩展后变成纯白。

## 仍待优化的部分

1. 在设备切换、横竖屏和多显示器场景下验证共享 DXGI 纹理的跨设备同步，减少当前受控的
   GPU→CPU 回读；
2. 为每个会话持续记录捕获、解码、提交和呈现时间戳，形成可比较的延迟分布；
3. 扩大硬件 MFT、软件 MFT、HDR、不同 GPU 驱动和 iOS 版本的真机回归矩阵；
4. 继续保持压缩输入 FIFO、有界音视频队列和失败时的可取消清理。

## 爱思投屏的本机二进制证据（可选对照）

维护者在一台安装了爱思投屏的测试机 `C:\Program Files\i4AirPlayer` 中观察到：
以下内容是对照证据，不是 iPhoneMirror 的运行时依赖，也不是用户必须安装的组件。

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

`av_hwframe_transfer_data` 很可能只在录制、截图或虚拟摄像头输出时把 GPU 帧回读到 CPU；
预览本身不需要每帧 CPU BGRA 拷贝。`libvirtualcam.dll` 也说明它有独立的虚拟摄像头输出链路。

## 参考来源

- <https://github.com/danielpaulus/quicktime_video_hack>
- <https://github.com/danielpaulus/quicktime_video_hack/blob/main/doc/technical_documentation.md>
- <https://github.com/chotgpt/quicktime_video_hack_windows>
- <https://github.com/chotgpt/quicktime_video_hack_windows/blob/main/qt_ios_line_cast_screen/src/H264Decoder.cpp>
