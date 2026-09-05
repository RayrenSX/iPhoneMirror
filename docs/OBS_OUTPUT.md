# OBS 输出、录制与虚拟摄像头

## 当前可用：干净预览窗口

应用提供无标题栏的独立预览窗口。该窗口只包含 DirectComposition/D3D11 画面，
没有设备列表、按钮或状态叠层，适合作为 OBS 的“窗口捕获”来源。普通设备窗口标题
为 `iPhoneMirror — <设备名称>`；媒体投屏窗口使用包含 `iPhoneMirror` 的本地化标题。
只有调用方未提供设备名称时才会回退到 `iPhoneMirror OBS Preview`，因此不要把固定标题
当作选择条件，应按标题中的 `iPhoneMirror` 和目标设备名称区分来源。窗口尺寸保持源画面
比例，拖动边缘时不会拉伸画面。

每个独立窗口都绑定到对应设备的会话；如果设备尚未有后台会话，打开窗口时会创建一个
独立会话并在关闭时回收。主窗口使用自己的活动渲染目标，多设备独立会话各自维护
renderer，不会把某台设备的帧交给另一台窗口。

OBS 配置：

1. 来源 → 添加“窗口捕获”；
2. 选择标题包含 `iPhoneMirror` 和目标设备名称的窗口；同时打开多个设备时，分别选择
   对应的设备标题；
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

“录制与推流”窗口可把当前会话（包括视频应用投屏）的帧送入随应用发布的 FFmpeg 8.1.2
运行时，输出 MP4、RTMP、SRT 或 WebRTC/WHIP。输出尺寸范围为 160–3840 × 160–2160，
且必须为偶数；帧率支持 10–60，码率支持 500–50000 kbps；横竖屏切换时以黑边保持比例，停止时会等待 FFmpeg 完成
文件或网络输出收尾。投屏源的 PCM 音频和对应编码器可用时会送入输出管线，MP4/RTMP/SRT
编码为 AAC，WHIP 编码为 Opus；没有可用音频或编码器时仍会立即开始纯视频输出。

虚拟摄像头使用 Windows 11 Media Foundation 软件摄像头 API，名称为
`iPhoneMirror Virtual Camera`。首次安装、更新或卸载媒体源需要管理员权限，安装完成
后普通用户即可启动和停止；摄像头只提供视频，声音请在 OBS 中另加“应用程序音频捕获”。

录制、推流和虚拟摄像头的默认尺寸统一取当前预览的实际输出分辨率。输出设置窗口保持
打开时，预览方向或尺寸变化会更新仍处于默认值的控件，但不会覆盖用户手动选择的尺寸。

录制按钮会立即写入应用临时目录；点击“停止输出”并完成 MP4 索引后，应用才弹出保存
位置和文件名。取消保存不会删除录制，下次打开窗口或重启应用后仍可继续保存。独立预览
窗口仍保留为无需安装组件的稳定回退方式。

SDR 输出标记为 full-range BT.709。应用会在 NV12 导出、编码输出和虚拟摄像头媒体类型
中保留这一元数据，避免浅色 UI 灰阶在播放器中被再次按 video-range 扩展而变白；HDR
预览源仍按源的 HDR 元数据处理。

本地协议与设备验证页位于 `tools/srs-lab`。运行
`tools/srs-lab/Start-SrsLab.ps1` 后访问 `http://127.0.0.1:8090`，可检查 RTMP、SRT、
WHIP/WHEP 和 `iPhoneMirror Virtual Camera`。启动脚本优先使用 Docker SRS；Docker 不可用
时会自动下载并校验 MediaMTX Windows 后端。页面会显示当前后端对应的实际推流地址。
