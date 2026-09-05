# USB Touch Design Notes

本项目的控制层采用独立领域模型：桌面输入先转换为 `TouchPoint`，由 `DirectUsbInputBridge` 组织批次，再交给自研 Python 运行时建立 USB CoreDevice 会话。应用层消息使用 `iphoneMirror.touch.v2` schema 和 `touch_batch`/`points` 字段。

这套应用层协议、状态机、生命周期和错误事件由本项目维护。iPhone 的 Service ID、Report ID、报告长度、坐标编码和状态位属于设备端协议事实，保持这些值是互操作性要求。

设计决策：

- 触点生命周期由逻辑 ID 到五个稳定 slot 的状态机管理。
- USB 会话与鼠标采样解耦，输入通过长度前缀消息传递。
- `gateOpen` 与真实触控送达分开报告，避免把连接成功误报成 UI 成功。
- 所有触点在异常、拔线和退出时执行释放清理。
