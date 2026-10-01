# iPhoneMirror 架构

2026-10-01 对照当前工作区核对，主程序/驱动管理器源码版本为 `1.8.4-test4`。
本页描述实现边界，不表示测试版已公开发布。构建与测试见[开发指南](DEVELOPMENT.md)，
全部文档见[索引](README.md)。

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

上图的 AirPlay/DLNA 部分对应默认的原始接收方案。选择 UxPlay 时，
`iPhoneMirror.UxPlayHost.exe` 管理 UxPlay/GStreamer 子进程，将镜像视频和 PCM 转成
相同无线 IPC，再接入 `WirelessCaptureSession`。UxPlay 不提供 URL/HLS/DLNA 视频应用投放。

### 反向控制与 USBMux 共存

```text
WPF preview input → ReverseControlInputRouter / device binding
  ├─ BluetoothHidMouseService → Windows BLE HID → iOS AssistiveTouch
  └─ UsbTouchBridgeHost → DirectUsbInputBridge → iUsbBridge.exe
       ├─ USB: Apple usbmuxd → Lockdown / CoreDevice
       │    └─ QuickTime 配置已激活时，可接管同一设备的 USBMux 接口
       │       → 进程内本地 usbmuxd 兼容服务（动态回环端口）
       └─ Wireless: usbmux Network / paired RemotePairing discovery
            → userspace tunnel → RSD → Universal HID / Indigo
```

视频与音频仍由核心读取 QuickTime `0x2A` bulk 端点。Python 的
`tools/iostouch/qt/usbmux_usb.py` 和 `usbmuxd_server.py` 提供管理/反控通道，
使反控能与隐藏 USB 配置下的投屏共存；该本地服务不承载视频，也不能等同于固定 `37015`。
停止对应有线投屏前，主程序先释放反控桥占用的 USBMux 接口，再由核心恢复 USB 配置。

USB/无线反控需按设备验证开发者模式、DDI 和 mainTouchscreen `257`；
`ready` 表示服务验证通过，不能代替真实输入效果验收。无线桥不回退到 USB。
蓝牙输入不依赖这套 DDI 路径，受 BLE 外设能力和辅助触控约束。

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

src/UxPlayHost (独立备用宿主)
└─ UxPlay/GStreamer adapter 镜像帧和音频转为相同无线 IPC，不提供 URL/DLNA 投放

src/App (WPF/.NET)
├─ Interop                 C ABI P/Invoke 与原生预览绑定
├─ Models/ViewModels       UI 状态、设备轮询、多设备切换、命令
├─ Services               驱动只读检测/管理器启动、媒体输出、虚拟摄像头、BLE/USB/无线反控、截图
├─ Updater                Release 解析、下载校验、Setup/ZIP 更新交接与用户设置
└─ MainWindow/Windows      主窗口、镜像独立窗口、视频投放界面（独立窗口同时供 OBS 捕获）

src/Shared / src/SharedUI
└─ 共享下载与提权路径校验、主题资源、控件、动画、窗口尺寸和圆角行为

tools/usb_touch_bridge.py + tools/iostouch
└─ CoreDevice/HID、DDI 准备、USBMux 共存、输入状态与恢复；由仓库内配方构建

src/VirtualCamera (Windows Media Foundation component)
├─ MediaSource             current-user frame server media source (RGB32/NV12 metadata)
├─ FrameExchange            bounded per-user shared frame channel
├─ VirtualCameraControl    register, start, stop and unregister operations
└─ VirtualCamera.Admin     elevated one-time registration helper

src/DriverInstaller (独立 WPF/.NET EXE)
├─ DeviceCatalog            Apple Lockdown 元数据、设备选择和父设备状态
├─ AppleSupportInstaller    Apple 官方 USB 支持的离线 MSI/官方安装包流程
├─ ElevatedDriverHost       受 UAC 保护的 libusb0 安装、修复、卸载和回滚
├─ ParentDriver*            按设备确认、候选驱动枚举、重绑定/重建、快照与恢复
├─ DriverLogger             UI 日志、管理员操作日志和 MSI 日志索引
└─ Windows                  一键安装、高级修复、卸载和统一提示窗口

发布关系
├─ iPhoneMirror.exe         只读驱动状态并运行 USB/AirPlay 投屏
├─ iPhoneMirror.Driver.exe  独立驱动安装器，主程序按需启动
├─ iUsbBridge.exe + _internal + iUsbBridge.runtime.json  独立反控运行时
├─ iPhoneMirror.VirtualCamera.dll / .Admin.exe  Windows 11 虚拟摄像头及一次性注册助手
├─ tools/ffmpeg/             FFmpeg 8 媒体输出/HLS 桥接运行时（可选精简发布）
└─ Wireless/                原始宿主和隔离的 AirPlay/FFmpeg 4.4.2；UxPlay/ 下为备用运行时
```

## 接口契约

| 契约 | 当前值 | 定义与边界 |
|---|---|---|
| Core C ABI | `18` | `src/Core/include/iPhoneMirror/CoreApi.h` 与 `src/App/Interop/NativeCore.cs` |
| 无线 IPC | `7` | `src/WirelessHost/IpcProtocol.h`；392 字节消息头，载荷最多 64 MiB |
| 桥接 stdin | `iphoneMirror.touch.v2` | 4 字节 LE 长度（不含前缀）+ UTF-8 JSON；当前 Python 上限 4 MiB |
| 桥接 stdout | JSON Lines，`ready.protocol=2` | 生命周期、能力、设备身份与恢复事件；不是二进制长度前缀 |
| 桥接运行时清单 | schema `1` | `iUsbBridge.runtime.json`，用于文件布局/完整性校验，与输入协议版本不同 |

修改契约时同步发送端、接收端、版本检查和回归测试，详见[桥接协议](USB_TOUCH_BRIDGE_USAGE.md)。

## 线程与生命周期

- UI 线程只更新界面；设备刷新在后台执行；
- 每个来源维护独立 SessionHandle；主窗口和多个独立窗口可并行绑定不同来源；
- USB 会话工作线程执行读取、分帧、协议状态机和 FEED/EAT 分发；视频解码由独立线程执行；
- 压缩视频使用按样本数、字节数约束的 FIFO；溢出时等待关键帧并重置解码状态，不能随意丢弃参考帧；
- 解码后帧缓存与压缩包队列分开管理；显示端过载时取最新完整帧，不能把其策略解释成压缩队列只存 1–2 帧；
- 解码器策略和实际硬件/软件状态分开上报；HDR/SDR 色彩元数据随帧传递，导出端按需规范化；
- 音频由有界队列交给独立 WASAPI 线程；协议时钟交换与本地缓冲策略是不同层次；
- 停止/拔线路径有取消、清理和有界恢复逻辑；物理拔线或驱动故障时可能仍需重新插拔，不能保证恢复成功。
- 主窗口的 WPF/native host 与其全屏切换遵循单活动渲染目标；多设备独立会话则各自维护 D3D11 renderer，窗口不会跨会话复用帧。
- 屏幕镜像与视频应用投屏共用一个 combined-mode 宿主，通过有界 IPC 消息类型分流，
  不共享设备会话或播放界面状态；
- 无线停止事件和父进程句柄用于在退出时回收后台宿主。

## OBS 与输出路线

1. Window Capture：当前最简单的本地方案，使用按设备命名的干净独立窗口；
2. FFmpeg 输出：当前支持 MP4、RTMP、SRT 和 WebRTC/WHIP，音频可用时随源音频编码；
3. Windows Media Foundation Virtual Camera：当前已实现，Windows 11 上首次注册需要管理员权限，
   普通用户随后即可启动；摄像头只提供视频；
4. 共享纹理/Spout 或 OBS source plugin：仍是可评估的低拷贝优化，不属于当前公开接口。

当前工作区允许在录制/推流时显式选择麦克风，与来源 PCM 混音；默认关闭。
缺少可用音频管线时不能启用麦克风混音，虚拟摄像头仍只发布视频。

## 安全与发布

- 不静默替换 Apple 官方 USB 驱动；WinUSB/libusbK 无法切换非首配置，不能误选；
- 采集过滤驱动由独立工具验证签名、安装、卸载并记录原驱动状态；
- iPhoneMirror 主程序只做只读状态检测，不提权、不写驱动、不修改 UpperFilters；
- 有线开始投屏前才执行当前设备的严格 `libusb0` 序列号检查；无线 AirPlay 不读取驱动状态；
- USB 会话退出时尝试停止协议并恢复配置；进程被强制终止不保证清理完成；
- 独立驱动管理器负责 UAC、备份、事务回滚和日志；
- 管理员安装模式下，安装器为无线宿主添加限定到本地子网的 AirPlay/DLNA 防火墙规则，卸载时移除；
- 正式 GitHub Release 包含自包含主程序、独立驱动管理器、SPDX SBOM、SHA-256 清单和第三方许可证；
- 协议输入全部视为不可信，使用长度上限与 checked arithmetic。

父驱动管理可在用户确认后重新绑定选中设备，或重建设备节点。
具体快照、回滚及重启后复核边界见[父驱动管理](PARENT_DRIVER_MANAGEMENT.md)。
