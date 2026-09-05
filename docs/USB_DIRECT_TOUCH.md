# USB 直连触控

本项目的 `UsbTouchBridge.exe` 是自研独立实现，通过 USB、CoreDevice 隧道、RSD 和 Universal HID 服务向 iPhone 发送触控报告。

它实现 USB 设备发现、4 字节 little-endian 长度前缀 JSON 输入、五点触控状态机及 58 字节触控报告编码，不需要蓝牙或其他外部控制程序。应用层消息使用本项目定义的 `iphoneMirror.touch.v2` schema。

启动前必须开启开发者模式。安装包不包含 Apple DDI；设备未挂载时，桥接器会通过
GitHub 官方 API 动态解析 commit 和文件清单，校验 Git blob 身份、文件大小及本地计算的
SHA-256，再通过 Apple 个性化流程挂载；`BuildManifest.plist` 必须匹配当前运行时。首次
使用需要联网，也可显式提供本地官方镜像。若媒体流认证返回 `9021`，桥接器会验证 direct
Universal HID；只有设备没有 mainTouchscreen（Service ID `257`）时才停止，不会显示为
“已连接”。已挂载的旧 DDI 缺少该 service 时，桥接器只会自动重挂一次。

触控 surface 使用 Service ID `257`，Report ID `0x09`，坐标为 little-endian UInt16。`down`/`move` 使用 `0xC2 | slot`，`up` 使用 `0x02 | slot`。

`ready` 仅在 mainTouchscreen（Service ID `257`）通过验证后发出。它的 `authMode` 会说明
是 `mediastream` 还是 `direct`，避免把没有实际触控 surface 的连接显示为成功。
