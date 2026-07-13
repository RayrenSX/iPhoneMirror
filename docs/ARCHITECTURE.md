# iPhoneMirror 架构

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
                                 → WASAPI playback / OBS app-audio capture

iPhone/iPad AirPlay
  └─ Windows DNS-SD discovery + AirPlay protocol (`iPhoneMirror.WirelessHost.exe`)
       ├─ decoded I420 video ─┐
       └─ decoded PCM audio ──┴─ named pipe IPC
                                  └─ WirelessCaptureSession
                                       ├─ I420 → NV12 → shared D3D11 renderer path
                                       └─ PCM → shared WASAPI path
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
│  ├─ H264                 AVCC、SPS/PPS、Annex-B
│  └─ MFDecoder            Annex-B H264 → NV12
├─ Renderer
│  └─ D3D11PreviewRenderer NV12 纹理、shader 色彩转换、DirectComposition
├─ Audio
│  └─ WasapiRenderer       8–192 kHz、1–8 声道 PCM 播放、音量与静音
├─ Capture
│  ├─ CaptureSession       USB QuickTime 会话
│  └─ WirelessCaptureSession  无线宿主 IPC、I420 → NV12、PCM
└─ CoreApi                 稳定 C ABI，供 GUI/OBS 插件调用

src/WirelessHost (独立 GPLv3 进程)
├─ AirPlayServer runtime   AirPlay/FairPlay、H.264/AAC 解码
└─ IpcProtocol             有界命名管道消息，仅传递解码后视频/音频

src/App (WPF/.NET)
├─ Interop                 C ABI P/Invoke 与原生预览绑定
├─ Models/ViewModels       UI 状态、设备轮询、多设备切换、命令
├─ Services               外部驱动只读检测/管理器启动、截图、窗口比例和预览协调
└─ MainWindow/Windows      主窗口、独立窗口、OBS 窗口

src/DriverInstaller (独立 WPF/.NET EXE)
├─ DeviceCatalog            Apple Lockdown 元数据、设备选择和父设备状态
├─ AppleSupportInstaller    Apple 官方 USB 支持的离线 MSI/官方安装包流程
├─ ElevatedDriverHost       受 UAC 保护的 libusb0 安装、修复、卸载和回滚
├─ DriverLogger             UI 日志、管理员操作日志和 MSI 日志索引
└─ Windows                  一键安装、高级修复、卸载和统一提示窗口

发布关系
├─ iPhoneMirror.exe         只读驱动状态并运行 USB/AirPlay 投屏
├─ iPhoneMirror.Driver.exe  独立驱动安装器，主程序按需启动
└─ Wireless/                GPLv3 无线宿主及其隔离的 AirPlay/FFmpeg 运行时
```

## 线程模型

- UI 线程只更新界面；设备刷新在后台执行；
- 每个来源维护独立 SessionHandle；主窗口和多个独立窗口可并行绑定不同来源；
- USB reader 只做 read + packet framing，不做解码；
- 协议线程维护时钟、发送 NEED、分发 FEED/EAT；
- 视频解码队列最多保留 1–2 帧，过载时丢旧的非关键帧；
- 音频使用有界环形缓冲，时钟漂移由 SKEW/PTS 修正；
- 所有停止/拔线路径都可取消并恢复 USB 配置。
- 无线停止事件和父进程句柄共同保证后台宿主不会残留。

## OBS 路线

1. Window Capture：最低风险，预览完成即可用；
2. 共享纹理/Spout 或 OBS source plugin：低拷贝，但需要额外部署；
3. Windows Media Foundation Virtual Camera：用户体验最好，需要注册与签名；
4. UDP/RTMP：作为远程/跨进程输出，不作为本地最低延迟首选。

## 安全与发布

- 不静默替换 Apple 官方 USB 驱动；WinUSB/libusbK 无法切换非首配置，不能误选；
- 采集过滤驱动由独立工具安装、签名、卸载和记录原驱动状态；
- iPhoneMirror 主程序只做只读状态检测，不提权、不写驱动、不修改 UpperFilters；
- 有线开始投屏前才执行当前设备的严格 `libusb0` 序列号检查；无线 AirPlay 不读取驱动状态；
- 崩溃恢复器负责发送 disable request/恢复配置；
- 独立驱动管理器负责 UAC、备份、事务回滚和日志；
- 正式 GitHub Release 包含自包含主程序、独立驱动管理器、SPDX SBOM、SHA-256 清单和第三方许可证；
- 协议输入全部视为不可信，使用长度上限与 checked arithmetic。
