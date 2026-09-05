# 架构说明

## 系统概览

```
┌─────────────────────────────────────────────────────┐
│                 Host (PC)                            │
│                                                      │
│  ┌─────────────┐    stdin/stdout     ┌────────────┐ │
│  │ iPhoneMirror │◄───JSON IPC──────►│  Python     │ │
│  │ (C# WPF)     │   4B LE + JSON    │ USB bridge  │ │
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
│  iOS 27.x+        │  媒体流认证通常可用
└──────────────────┘
```

## 组件说明

### 1. Python USB 触控运行时 (`tools/usb_touch_bridge.py`)
- 提供稳定的 stdin/stdout IPC 协议
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

### 4. 启用条件 (iPhoneMirror 集成)
- 无线 AirPlay 镜像在连
- 同 UDID 的 iPhone 已 USB 连接
- 已配对信任
- RSD 通道验证成功
- 用户主动按"启用 USB 鼠标控制"按钮

### 5. 停止条件
- 无线断开 / USB 拔出 / UDID 不匹配 / 会话异常
- 立即停止并释放所有触点

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
| 1 | 1 | 0x01 |
| 2 | 1 | 0x05 |
| 3 | 1 | 状态字节 (contact=0xC2\|slot, release=0x02\|slot) |
| 4-5 | 2 | X 坐标 (LE u16, 0-65535) |
| 6-7 | 2 | Y 坐标 (LE u16, 0-65535) |
| 8-39 | 32 | 全零 |
| 40-43 | 4 | 0x02 0x00 0x00 0x00 |
| 44-49 | 6 | 时间戳 (48-bit LE, Mach abs time) |
| 50-57 | 8 | 全零 |
