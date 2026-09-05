"""iostouch —— 通过 USB 隧道直接向 iPhone 注入精准触控。

原理：iOS 18 的开发者磁盘映像（DDI）里带有 ``dtuhidd`` 守护进程，它通过 RemoteXPC 暴露
``com.apple.coredevice.hid.universalhidservice``。向其 ``_ServiceID=257`` 的 mainTouchscreen
表面发送 58 字节 HID 报告，即可得到与真实手指完全等价的 ``UIEventTypeTouches``。

前提：设备已信任本机、已开启开发者模式；主机端无需管理员权限（用户态隧道）、无需越狱、
无需在手机上安装任何 App。
"""

__all__ = []
