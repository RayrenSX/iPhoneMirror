# USB 与无线反控架构

当前桥接器以仓库内 Python 源码和固定配方构建，详见[开发指南](DEVELOPMENT.md)。
消息细节以[桥接调用文档](USB_TOUCH_BRIDGE_USAGE.md)为准；投屏总链路见[软件架构](ARCHITECTURE.md)。

## 系统概览

```
┌─────────────────────────────────────────────────────┐
│                 Host (PC)                            │
│                                                      │
│  ┌─────────────┐    stdin/stdout     ┌────────────┐ │
│  │ iPhoneMirror │◄───JSON IPC──────►│  Python     │ │
│  │ (C# WPF)     │   双向格式不同     │ USB bridge  │ │
│  │              │                   │  (自研)      │ │
│  └─────────────┘                   └──────┬─────┘ │
│                                            │        │
│  ┌─────────────────────────────────────────┐       │
│  │ pymobiledevice3 (GPL-3.0-or-later)       │       │
│  │  ├─ Lockdown → usbmuxd                   │       │
│  │  ├─ CoreDeviceTunnelProxy                │       │
│  │  ├─ RemotePairing (SRP-6a + X25519)      │       │
│  │  ├─ Userspace TCP (pmd-pytcp)            │       │
│  │  ├─ RSD (HTTP/2 + RemoteXPC)             │       │
│  │  ├─ UniversalHIDService                  │       │
│  │  ├─ IndigoHIDService                     │       │
│  │  └─ DisplayService (media stream gate)   │       │
│  └─────────────────────────────────────────┘       │
│         │ USB                                        │
└─────────┼────────────────────────────────────────────┘
          ▼
┌──────────────────┐
│   iPhone          │
│  iOS 18.x / 26.x  │  9021: 认证状态按设备实测确认
│  iOS 27.x+        │  未测试，以服务验证为准
└──────────────────┘
```

PC → bridge 的 stdin 为 `4B LE 长度 + UTF-8 JSON`；bridge → PC 的 stdout 为
逐行 JSON 事件；stderr 为诊断日志。输入 JSON 最多 4 MiB，长度不包含前缀。
支持最多五个触点是桥接协议能力，不表示所有 GUI 入口或设备均已完成多指实测。

## 组件说明

### 1. USB 触控运行时 (`iUsbBridge.exe`)
- 发布包中的可执行桥接器由 `tools/usb_touch_bridge.py` 等源码构建，提供稳定的 stdin/stdout IPC 协议
- 诚实报告 9021 gate 状态
- 五点触控状态机、58 字节 HID 报告、异常清理释放触点
- 不依赖外部专有控制程序

### 2. 独立 Demo (`tools/usb_mouse_demo.py`)
- 直接调用 pymobiledevice3 API，不经过 stdin/stdout IPC
- 支持 tap、swipe、Indigo 按钮（Home/音量）
- 交互模式

### 3. C# 集成 (`src/App/Services/`)
- `DirectUsbInputBridge.cs`: 启动/管理 USB 触控桥接器，通过 IPC 通信
- `CoreDeviceTouchProtocol.cs`: 协议常量
- `UsbTouchBridgeHost.cs`: 管理桥接器生命周期、状态事件、输入队列和退出时按键释放

### 4. 启用条件 (iPhoneMirror 集成)
- 有线控制：选择有线来源，设备解锁并信任此电脑，按提示开启开发者模式；桥接器会从 GitHub
  官方 API 获取并校验匹配 DDI，再验证 mainTouchscreen（Service ID 257）。
- 无线控制：选择无线来源，设备已完成配对并在同一局域网可达；桥接器使用 `--wireless`，
  不回退到 USB。
- 首次连接先在设备绑定器建立设备档案，将 USB、AirPlay 和 Bluetooth 身份关联到同一设备。
- 用户主动点击“开启有线控制”或“开启无线控制”；再次点击同一按钮可关闭控制。

### 5. 停止条件
- 无线断开 / USB 拔出 / UDID 不匹配 / 会话异常
- 立即停止并释放所有触点

桥接器会尝试释放触点/按键并在限定时间内清理；设备已断开时无法保证释放报告送达。
有线反控与投屏共存时，停止投屏必须先退出对应反控桥，再由核心恢复 USB 配置。

### 6. QuickTime 配置下的 USBMux

未投屏时优先使用 Apple usbmuxd。设备已处于 QuickTime 隐藏配置时，桥接器可以通过
`tools/iostouch/qt/usbmux_usb.py` 接管同一设备的 USBMux 接口，并由
`usbmuxd_server.py` 在动态回环端口提供兼容服务；`USBMUXD_SOCKET_ADDRESS` 只在桥接
进程内指向该服务。视频/音频仍由 C++ 核心读取 `0x2A`，不进入该本地服务。

## 通信链路

```
Host → USB → usbmuxd → LockdownServiceProvider
  → com.apple.internal.devicecompute.CoreDeviceProxy
  → RemotePairing TCP 隧道 (SRP-6a + X25519/Ed25519 + ChaCha20Poly1305)
  → userspace pytcp 栈 (无需管理员)
  → RSD (HTTP/2 + RemoteXPC 握手)
  → CoreDevice 服务
    ├─ UniversalHIDService (send_report → 58 字节 mainTouchscreen)
    ├─ IndigoHIDService (send_button → Home/音量按钮)
    └─ DisplayService (start_video_stream → 认证状态)
```

## HID 报告格式 (58 字节 mainTouchscreen)

| 偏移 | 长度 | 内容 |
|------|------|------|
| 0 | 1 | Report ID = 0x09 |
| 1 | 1 | 本报告触点记录数（含本次抬起），1–5 |
| 2 | 1 | 0x05 |
| 3-27 | 25 | 五条记录，每条 5 字节：状态/slot、X u16 LE、Y u16 LE；contact=0xC0\|slot，release=slot |
| 28-39 | 12 | 滚动集合，全零 |
| 40-44 | 5 | 按 slot 索引的 identity（slot + 1） |
| 45-50 | 6 | 单调时间戳（48-bit LE） |
| 51-57 | 7 | 扩展手势标记，全零 |

每份报告保留全部仍按下的手指，省略活动手指会触发设备端释放。详见[五点触控](FIVE_POINT_TOUCH.md)。
