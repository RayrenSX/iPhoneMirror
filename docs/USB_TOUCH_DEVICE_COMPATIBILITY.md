# 设备兼容性

本页保留早期设备/服务探测记录，不是按 iOS 大版本提供的兼容承诺。
后续恢复实测见[有线恢复复审](WIRED_RECOVERY_REAUDIT.md)；不同记录的设备、DDI 和桥接版本不能互相替代。

## 当前使用条件

USB／无线触控最低要求 iOS／iPadOS 17.4、开发者模式、匹配 DDI 与实际服务验证；普通 USB/AirPlay 投屏和蓝牙反控分别判断。v1.8.5-pre7 的实验性 CoreDevice 投屏需要 iOS／iPadOS 27+，只提供画面。USB／无线触控仍以设备实际服务验证为准。

下表及“实测设备”保留早期记录；其中“未测试”指该次探测，并非整个项目从未测试该系统。当前设备管理与 CoreDevice 视频边界见[设备管理说明](DEV_DEVICE_HUB.zh-CN.md)。

## iOS 版本兼容性矩阵

| iOS 版本 | 触控 (UniversalHID) | Indigo 按钮 | media stream gate | 说明 |
|----------|---------------------|-------------|-------------------|------|
| iOS < 17.0 | ❌ 未测试 | ❌ 未测试 | ❌ 未测试 | CoreDevice 服务可能不可用 |
| iOS 17.0–17.3.x | USB CoreDeviceProxy 路径不可用 | ⚠️ 未实测 | ⚠️ 未实测 | 保留 USB/AirPlay 投屏与蓝牙反控路径。已信任的 USB 文件共享可走普通 Lockdown |
| iOS 17.4–17.7.x | ⚠️ 需验证 `257` | ⚠️ 未实测 | ⚠️ 未实测 | 可尝试 CoreDeviceProxy，触控以设备实际服务探测为准 |
| iOS 18.x | ⚠️ 需验证 `257` | ⚠️ 未在本桥完整验证 | 可能返回 9021 | 仅在 direct/mediastream 路径验证到 mainTouchscreen 后启用 |
| iOS 26.x | ⚠️ 需验证 `257` | ⚠️ 未测试 | 可能返回 9021 | 不作版本级兼容承诺；DDI 版本和设备实际服务表决定结果 |
| iOS 27.x+ | ⚠️ 未测试 | ⚠️ 未测试 | 未测试 | 仍以实际 service 枚举为准 |

## 实测设备

iOS 17 的 USB 隧道差异见 [pymobiledevice3 的 iOS 17+ 隧道指南](https://doronz88.github.io/pymobiledevice3/master/guides/ios17-tunnels/)。原生 CoreDevice 画面和 USB/AirPlay 画面使用不同的媒体路径。

| 设备 | iOS 版本 | 结果 |
|------|----------|------|
| iPhone 13 mini (iPhone13,1) | 18.7.8 | 历史测试记录显示单指滑动可用；需以当前桥和当前 DDI 复验 |
| iPhone18,1 | 26.6.1 | 早期探测日志仅枚举到 `Services=[]`；该记录不能代表后续 DDI 刷新或当前桥的结果 |

## 9021 错误详情

```
CoreDevice error 9021
Domain: com.apple.dt.CoreDeviceError
NSLocalizedDescription: "Remote control requires iOS 27.0 or later on this device."
```

- 触发条件: `DisplayService.start_video_stream()` (feature `com.apple.coredevice.feature.startmediastream`)
- 影响: `9021` 只反映媒体流认证失败。触控还取决于 Universal HID 是否提供所选显示屏的触摸面。
- 程序行为: 尝试 direct Universal HID，验证触摸面和 HID 通道后发出 `ready`。触摸面不可用时可刷新旧 DDI 并重试。

## 折叠屏反控

认证流与触摸报告使用同一块内置屏。优先读取设备报告的活动屏，旧版 DDI 缺少活动状态时按该设备的投屏宽高比匹配。桥接器用显示屏的 `uniqueId` 匹配 HID 清单里的 `displayUUID`。单屏设备仍可使用 `257`。设备上报活动屏变化时，桥接器释放旧触点并重建反控会话。

用户态隧道直接在 PyTCP 上接收认证流。日志记录接收方式、显示屏编号和 HID 触摸面编号。`input_verified` 表示当前 HID 连接确认了传输，用于判断链路状态。

iPhone Duo 的有线反控会在已有蓝牙绑定且 HID 描述符版本已确认时预热 BLE。订阅就绪后，Home、多任务以及适用的 Globe 组合键可走同一台手机的 BLE。触控、打字、音量和锁屏仍走 USB。Duo 内屏的方向校正使用 SpringBoard 方向，单次手势期间保持坐标变换稳定。

## 早期功能探测记录（iOS 18.x）

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

`9021` 是媒体流认证结果。桥接器在 `9021` 后尝试 direct Universal HID，并在所选显示屏的触摸面通过验证后允许反控。早期 iOS 26.6.1 日志没有该服务。判断实际可用性时需记录设备、DDI、桥接版本及真实输入结果。
