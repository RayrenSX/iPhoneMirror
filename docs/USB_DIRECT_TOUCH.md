# USB 直连触控

本项目的 `iUsbBridge.exe` 是自研独立实现，通过 USB、CoreDevice 隧道、RSD 和 Universal HID 服务向 iPhone 发送触控报告。

它实现 USB 设备发现、4 字节 little-endian 长度前缀 JSON 输入、五点触控状态机及 58 字节触控报告编码，不需要蓝牙或其他外部控制程序。应用层消息使用本项目定义的 `iphoneMirror.touch.v2` schema。

启动前必须开启开发者模式。当前标准 ZIP 和 Setup 内置 Personalized 与 Cryptex DDI，
构建按 `config/ddi-bundles.json` 固定提交、大小和 SHA-256 校验。应用优先使用随包镜像，
缺失、损坏或不兼容时按相应流程尝试缓存、下载或受限回退；Apple 个性化签名仍可能需要联网。
直接调用桥接器时可用 `--ddi-dir` 指定 Personalized 三文件目录。完整准备顺序和 iOS 27
Cryptex 回退条件见[桥接使用说明](USB_TOUCH_BRIDGE_USAGE.md#usb-模式)。
若媒体流认证返回 `9021`，桥接器会验证 direct
Universal HID；只有设备没有 mainTouchscreen（Service ID `257`）时才停止，不会显示为
“已连接”。已挂载的旧 DDI 缺少该 service 时，桥接器只会自动重挂一次。

触控 surface 使用 Service ID `257`，Report ID `0x09`，坐标为 little-endian UInt16。每份报告包含全部仍活动的触点及本次抬起的触点；`down`/`move` 的状态字节为 `0xC0 | slot`，`up` 为 `slot`。报告布局和设备端行为见[五点触控](FIVE_POINT_TOUCH.md)。

`ready` 仅在 mainTouchscreen（Service ID `257`）通过验证后发出。它的 `authMode` 会说明
是 `mediastream` 还是 `direct`，避免把没有实际触控 surface 的连接显示为成功。
