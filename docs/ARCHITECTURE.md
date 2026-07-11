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
│  └─ WasapiRenderer       48 kHz 双声道播放、音量与静音
└─ CoreApi                 稳定 C ABI，供 GUI/OBS 插件调用

src/App (WPF/.NET)
├─ Interop                 C ABI P/Invoke 与原生预览绑定
├─ Models/ViewModels       UI 状态、设备轮询、多设备切换、命令
├─ Services               驱动 Helper、截图、窗口比例和预览协调
└─ MainWindow/Windows      主窗口、独立窗口、OBS 窗口
```

## 线程模型

- UI 线程只更新界面；设备刷新在后台执行；
- 当前进程同一时间维护一个活动 CaptureSession；切换设备时先完整停止旧会话；
- USB reader 只做 read + packet framing，不做解码；
- 协议线程维护时钟、发送 NEED、分发 FEED/EAT；
- 视频解码队列最多保留 1–2 帧，过载时丢旧的非关键帧；
- 音频使用有界环形缓冲，时钟漂移由 SKEW/PTS 修正；
- 所有停止/拔线路径都可取消并恢复 USB 配置。

## OBS 路线

1. Window Capture：最低风险，预览完成即可用；
2. 共享纹理/Spout 或 OBS source plugin：低拷贝，但需要额外部署；
3. Windows Media Foundation Virtual Camera：用户体验最好，需要注册与签名；
4. UDP/RTMP：作为远程/跨进程输出，不作为本地最低延迟首选。

## 安全与发布

- 不静默替换 Apple 官方 USB 驱动；WinUSB/libusbK 无法切换非首配置，不能误选；
- 过滤驱动必须签名、可卸载，并记录原驱动状态；
- 安装前检查 Secure Boot、Apple Devices、AMDS 与冲突驱动；
- 崩溃恢复器负责发送 disable request/恢复配置；
- 正式 GitHub Release 包含 SPDX SBOM、SHA-256 清单、第三方许可证和对应源码；
- 协议输入全部视为不可信，使用长度上限与 checked arithmetic。
