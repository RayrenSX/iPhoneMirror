# iPhoneMirror 架构（v1.8.3）

## 数据流

```text
iPhone USB
  ├─ USBMux interface 0xFE
  │    └─ Apple Mobile Device Service :27015
  │         ├─ ListDevices / multi-device / UDID
  │         └─ Lockdown :62078 / pairing / device metadata
  │
  └─ QuickTime interface 0x2A
       └─ per-device libusb0 filter + QtUsbTransport/LibUsb0Transport
            └─ Packet framer
                 └─ QuickTime session state machine
                      ├─ FEED → CMSampleBuffer → AVCC H264
                      │          → Media Foundation → NV12
                      │          → D3D11 Y/UV texture + shader conversion
                      │          → DirectComposition preview
                      └─ EAT! → CMSampleBuffer → 48 kHz PCM
                                 ├─ WASAPI playback / OBS app-audio capture
                                 └─ bounded output pipe → FFmpeg AAC/Opus

iPhone/iPad AirPlay/DLNA (one receiver identity, fixed RAOP 5001/AirPlay 7001/
DLNA 8090/SSDP 1900 plus per-session negotiated media ports)
  └─ combined-mode `iPhoneMirror.WirelessHost.exe`
       ├─ screen-mirroring I420/PCM ─ named pipe IPC
       │                            └─ WirelessCaptureSession
       │                                 ├─ I420 → NV12 → shared D3D11 renderer path
       │                                 └─ PCM → shared WASAPI path
       └─ video-app HTTP(S)/HLS URL + playback commands ─ named pipe IPC
                                                          └─ WPF MediaElement playback surface
                                                               ├─ playback state → iPhone/iPad
                                                               ├─ source audio → WASAPI / output pumps
                                                               └─ video frame → recording / streaming / virtual camera

native session / media-cast frame
  ├─ D3D11/DirectComposition main and detached previews
  ├─ lazy CPU NV12 export → FFmpeg 8 MP4 / RTMP / SRT / WHIP
  └─ BGRA frame exchange → Windows 11 Media Foundation virtual camera
```

## 模块边界

```text
src/Core (C++ DLL)
├─ Device
│  ├─ AppleUsbDiscovery     SetupAPI、Apple Devices/AMDS 状态
│  └─ DeviceManager        多设备合并、配对/Lockdown 元数据
├─ Transport
│  ├─ Socket               有超时的 Winsock RAII
│  ├─ UsbMuxClient         27015/37015 plist 协议
│  ├─ QtUsbTransport       vendor request、非首配置切换、bulk I/O
│  └─ LibUsb0Transport     Windows libusb0 filter 后端
├─ Protocol
│  ├─ Plist                有界 XML plist
│  ├─ QuickTimePacket      流重组、FourCC、PING/NEED
│  └─ QuickTimeSession     CWPA/AFMT/CVRP/CLOK/TIME/SKEW 状态机
├─ Media
│  ├─ CoreMedia            CMTime、CMSampleBuffer、fdsc、ASBD
│  ├─ H264/HEVC            AVCC、SPS/PPS、Annex-B
│  └─ MFDecoder            Annex-B H264/HEVC → NV12/P010，硬件/软件策略
├─ Renderer
│  └─ D3D11PreviewRenderer NV12/P010 纹理、BT.709/HDR shader 色彩转换、DirectComposition
├─ Audio
│  └─ WasapiRenderer       8–192 kHz、1–8 声道 PCM 播放、音量与静音
├─ Capture
│  ├─ CaptureSession       USB QuickTime 会话
│  └─ WirelessCaptureSession  无线宿主 IPC、I420 → NV12、PCM
└─ CoreApi                 稳定 C ABI，供 GUI、输出和外部工具调用

src/WirelessHost (独立 GPLv3 进程)
├─ AirPlayServer runtime   AirPlay/FairPlay、H.264/AAC/ALAC 解码、DLNA 控制
└─ IpcProtocol             有界双向命名管道消息，传递镜像帧或视频投放命令/播放状态

src/App (WPF/.NET)
├─ Interop                 C ABI P/Invoke 与原生预览绑定
├─ Models/ViewModels       UI 状态、设备轮询、多设备切换、命令
├─ Services               驱动只读检测/管理器启动、媒体输出、虚拟摄像头、蓝牙 HID、截图和预览协调
└─ MainWindow/Windows      主窗口、镜像独立窗口、视频投放界面、OBS 窗口

src/VirtualCamera (Windows Media Foundation component)
├─ MediaSource             current-user frame server media source (RGB32/NV12 metadata)
├─ FrameExchange            bounded per-user shared frame channel
├─ VirtualCameraControl    register, start, stop and unregister operations
└─ VirtualCamera.Admin     elevated one-time registration helper

src/DriverInstaller (独立 WPF/.NET EXE)
├─ DeviceCatalog            Apple Lockdown 元数据、设备选择和父设备状态
├─ AppleSupportInstaller    Apple 官方 USB 支持的离线 MSI/官方安装包流程
├─ ElevatedDriverHost       受 UAC 保护的 libusb0 安装、修复、卸载和回滚
├─ DriverLogger             UI 日志、管理员操作日志和 MSI 日志索引
└─ Windows                  一键安装、高级修复、卸载和统一提示窗口

发布关系
├─ iPhoneMirror.exe         只读驱动状态并运行 USB/AirPlay 投屏
├─ iPhoneMirror.Driver.exe  独立驱动安装器，主程序按需启动
├─ iPhoneMirror.VirtualCamera.dll / .Admin.exe  Windows 11 虚拟摄像头及一次性注册助手
├─ tools/ffmpeg/             FFmpeg 8 媒体输出/HLS 桥接运行时（可选精简发布）
└─ Wireless/                GPLv3 无线宿主及其隔离的 AirPlay/FFmpeg 4.4.2 运行时
```

## 线程模型

当前 native Core API 版本为 `18`，无线命名管道 IPC 版本为 `7`。修改跨进程结构时必须
同步更新宿主、核心和对应的回归测试；不兼容消息会在握手阶段拒绝。

- UI 线程只更新界面；设备刷新在后台执行；
- 每个来源维护独立 SessionHandle；主窗口和多个独立窗口可并行绑定不同来源；
- USB reader 只做 read + packet framing，不做解码；
- 协议线程维护时钟、发送 NEED、分发 FEED/EAT；
- 视频解码队列最多保留 1–2 帧，过载时丢旧的非关键帧；
- 解码器策略和实际硬件/软件状态分开上报；HDR/SDR 色彩元数据随帧传递，导出端按需规范化；
- 音频使用有界环形缓冲，时钟漂移由 SKEW/PTS 修正；
- 所有停止/拔线路径都可取消并恢复 USB 配置。
- 主窗口的 WPF/native host 与其全屏切换遵循单活动渲染目标；多设备独立会话则各自维护 D3D11 renderer，窗口不会跨会话复用帧。
- 屏幕镜像与视频应用投屏共用一个 combined-mode 宿主，通过有界 IPC 消息类型分流，
  不共享设备会话或播放界面状态；
- 无线停止事件和父进程句柄共同保证后台宿主不会残留。

## OBS 与输出路线

1. Window Capture：当前最简单的本地方案，使用按设备命名的干净独立窗口；
2. FFmpeg 输出：当前支持 MP4、RTMP、SRT 和 WebRTC/WHIP，音频可用时随源音频编码；
3. Windows Media Foundation Virtual Camera：当前已实现，Windows 11 上首次注册需要管理员权限，
   普通用户随后即可启动；摄像头只提供视频；
4. 共享纹理/Spout 或 OBS source plugin：仍是可评估的低拷贝优化，不属于当前公开接口。

## 安全与发布

- 不静默替换 Apple 官方 USB 驱动；WinUSB/libusbK 无法切换非首配置，不能误选；
- 采集过滤驱动由独立工具验证签名、安装、卸载并记录原驱动状态；
- iPhoneMirror 主程序只做只读状态检测，不提权、不写驱动、不修改 UpperFilters；
- 有线开始投屏前才执行当前设备的严格 `libusb0` 序列号检查；无线 AirPlay 不读取驱动状态；
- 崩溃恢复器负责发送 disable request/恢复配置；
- 独立驱动管理器负责 UAC、备份、事务回滚和日志；
- 管理员安装模式下，安装器为无线宿主添加限定到本地子网的 AirPlay/DLNA 防火墙规则，卸载时移除；
- 正式 GitHub Release 包含自包含主程序、独立驱动管理器、SPDX SBOM、SHA-256 清单和第三方许可证；
- 协议输入全部视为不可信，使用长度上限与 checked arithmetic。
