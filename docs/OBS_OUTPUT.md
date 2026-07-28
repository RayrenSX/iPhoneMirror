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

## 内建录制、推流与虚拟摄像头

“录制与推流”窗口可把当前会话的投屏帧送入随应用发布的 FFmpeg 8 运行时，输出
MP4、RTMP、SRT 或 WebRTC/WHIP。输出尺寸固定，横竖屏切换时以黑边保持比例，停止时
会等待 FFmpeg 完成文件或网络输出收尾。虚拟摄像头使用 Windows 11 Media Foundation
软件摄像头 API；首次安装媒体源需要管理员权限，之后由普通用户会话启动。

录制、推流和虚拟摄像头的默认尺寸统一取当前预览的实际输出分辨率。输出设置窗口保持
打开时，预览方向或尺寸变化会更新仍处于默认值的控件，但不会覆盖用户手动选择的尺寸。

录制按钮会立即写入应用临时目录；点击“停止输出”并完成 MP4 索引后，应用才弹出保存
位置和文件名。取消保存不会删除录制，下次打开窗口或重启应用后仍可继续保存。投屏 PCM
可用时会编码为 AAC（WHIP 使用立体声 Opus）；音频暂不可用时仍会立即开始纯视频输出，
不会因等待音频而阻止录制。虚拟摄像头只输出视频，OBS 中需要声音时可继续使用“应用程序
音频捕获”选择 `iPhoneMirror.exe`。独立预览窗口仍保留为无需安装组件的稳定回退方式。

本地协议与设备验证页位于 `tools/srs-lab`。运行
`tools/srs-lab/Start-SrsLab.ps1` 后访问 `http://127.0.0.1:8090`，可检查 RTMP、SRT、
WHIP/WHEP 和 `iPhoneMirror Virtual Camera`。启动脚本优先使用 Docker SRS；Docker 不可用
时会自动下载并校验 MediaMTX Windows 后端。页面会显示当前后端对应的实际推流地址。
