# OBS 输出与录制路线

## 当前可用：干净预览窗口

应用提供标题固定为 `iPhoneMirror OBS Preview` 的独立预览窗口。该窗口只包含
DirectComposition/D3D11 画面，没有设备列表、按钮或状态叠层，适合作为 OBS 的“窗口捕获”来源。窗口没有系统标题条，尺寸与实际源分辨率锁定，拖动边缘时保持比例。

打开子窗口时，原生渲染器会从主界面切换到子窗口；关闭子窗口后会自动回到主界面。
这是有意的单渲染目标设计，可避免同一帧被上传和呈现两次。

OBS 配置：

1. 来源 → 添加“窗口捕获”；
2. 选择 `[iPhoneMirror.exe]: iPhoneMirror OBS Preview`；
3. 捕获方法优先选择 Windows Graphics Capture（OBS 中通常显示为
   “Windows 10（1903 及以上）”），它能稳定捕获 flip-model
   DirectComposition 顶层窗口及其透明圆角；
4. 在 OBS 预览中对来源执行“变换 → 适配屏幕”；
5. 笔记本有多个 GPU 时，让 OBS 与 iPhoneMirror 使用同一块 GPU。

## 音频进入 OBS

iPhone PCM 由 iPhoneMirror 的 WASAPI 输出播放。OBS 30.1 及以上可直接在“窗口捕获”
属性中启用“捕获音频（Beta）”。也可单独添加“应用程序音频捕获（Beta）”，按
`iPhoneMirror.exe` 匹配进程。

若同时启用了 OBS 的“桌面音频”，不要再重复添加同一输出设备，否则会出现回声或
双重音量。需要完全隔离时，可把应用输出设备路由到虚拟音频线，再在 OBS 中添加该
设备的“音频输入捕获”。

官方参考：

- [OBS Application Audio Capture Guide](https://obsproject.com/kb/application-audio-capture-guide)
- [OBS Sources Guide](https://obsproject.com/kb/sources-guide)
- [OBS GPU Selection Guide](https://obsproject.com/kb/gpu-selection-guide)

## 截图

`ScreenshotService` 直接读取核心最新 BGRA 帧并写 PNG，不截取窗口，因此不会带上
边框或 UI，也不受窗口缩放影响：

```csharp
var path = ScreenshotService.CreateDefaultPath();
ScreenshotService.CapturePng(_core.GetLatestVideoFrame, path);
```

## 录屏阶段

当前立即可用的无损集成方式是由 OBS 对独立预览窗口执行“开始录制”。应用内建录屏
不应再捕获桌面窗口；正确路线是在 C++ 核心中把解码后的 NV12 帧和 48 kHz PCM
同时送入 Media Foundation Sink Writer，使用硬件 H.264/HEVC 编码并生成 MP4。
这样可以保持原始时间戳、避免 GPU→屏幕→捕获的二次拷贝，并能正确进行音画同步。

后续内建录屏需包含：

- 有界视频/音频队列，停止时 drain/finalize；
- NV12 原始时间戳直接写入视频流；
- PCM 写入 AAC 编码流，并以首个共同时间戳归零；
- 编码器过载时丢弃旧视频帧，但不丢音频；
- 临时文件 + 成功 finalize 后原子重命名，避免崩溃留下伪完整 MP4。

虚拟摄像头只解决视频来源，通常不携带系统音频，因此不是本项目录屏/OBS 音频的
第一选择。更低开销的专业 OBS 路线是后续增加 OBS source plugin，共享 D3D11 纹理
并通过 OBS 音频 API 直接提交 PCM。
