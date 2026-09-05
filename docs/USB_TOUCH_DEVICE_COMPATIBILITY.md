# 设备兼容性

## iOS 版本兼容性矩阵

| iOS 版本 | 触控 (UniversalHID) | Indigo 按钮 | media stream gate | 说明 |
|----------|---------------------|-------------|-------------------|------|
| iOS < 17.0 | ❌ 未测试 | ❌ 未测试 | ❌ 未测试 | CoreDevice 服务可能不可用 |
| iOS 17.x | ⚠️ 未测试 | ⚠️ 未测试 | ⚠️ 未测试 | CoreDevice 服务可用，gate 行为未知 |
| iOS 18.x | ⚠️ 需验证 `257` | ⚠️ 未在本桥完整验证 | 可能返回 9021 | 仅在 direct/mediastream 路径验证到 mainTouchscreen 后启用 |
| iOS 26.x | ⚠️ 需验证 `257` | ⚠️ 未测试 | 可能返回 9021 | 不作版本级兼容承诺；DDI 版本和设备实际服务表决定结果 |
| iOS 27.x+ | ⚠️ 未测试 | ⚠️ 未测试 | 未测试 | 仍以实际 service 枚举为准 |

## 实测设备

| 设备 | iOS 版本 | 结果 |
|------|----------|------|------|
| iPhone 13 mini (iPhone13,1) | 18.7.8 | 历史测试记录显示单指滑动可用；需以当前桥和当前 DDI 复验 |
| iPhone18,1 | 26.6.1 | 当前日志仅枚举到 `Services=[]`，尚未触达 HID 帧发送；桥会自动刷新一次旧 DDI 后再验证 |

## 9021 错误详情

```
CoreDevice error 9021
Domain: com.apple.dt.CoreDeviceError
NSLocalizedDescription: "Remote control requires iOS 27.0 or later on this device."
```

- 触发条件: `DisplayService.start_video_stream()` (feature `com.apple.coredevice.feature.startmediastream`)
- 影响: 仅凭 9021 不能推断触控一定失败或一定成功；必须检查 Universal HID 是否实际提供 `mainTouchscreen`（Service ID `257`）
- 程序行为: 先尝试 direct Universal HID；只有验证到 `257` 才发 `ready`，否则会刷新一次旧 DDI 后重试

## 可用功能 (iOS 18.x 实测)

| 功能 | 状态 | 路径 |
|------|------|------|
| USB 设备发现 | ✅ | usbmuxd → Lockdown |
| CoreDeviceProxy 隧道 | ✅ | RemotePairing TCP (userspace) |
| RSD 握手 | ✅ | HTTP/2 + RemoteXPC |
| HID surface 枚举 | ✅ | `list_connected_services` 后必须存在 257 |
| 触控 (tap/swipe) | ⚠️ | 仅在 UniversalHIDService 实际发布 257 时启用 |
| Home 按钮 | ✅ | IndigoHIDService.send_button |
| 音量按钮 | ✅ | IndigoHIDService.send_button |
| 截图 | ✅ | ScreenCaptureService |
| Indigo digitizer | ❌ | messageType 未知，设备终止连接 |
| DTX dtservicehub | ❌ | 连接被终止 |

## 结论

`9021` 是媒体流认证结果，不是版本兼容性的充分条件。自研桥在 `9021` 后会尝试
direct Universal HID，但只在 `mainTouchscreen`（257）已枚举时允许反控。当前 iOS 26.6.1
日志没有该服务，因此需要通过自动 DDI 刷新后的真机复验，而不是把“连接成功”误报成触控成功。
