# USB 反控组件来源与许可证说明

本页描述桥接器构建来源和主要依赖，不替代各组件完整许可证。iPhoneMirror 自有代码的
GPL-3.0-only 不会自动改变独立桥接器或第三方代码的许可。

## iUsbBridge 与 USBMux 代码来源

- 基于 [iUsbBridge，作者：RayrenSX](https://github.com/RayrenSX/iUsbBridge)。当前构建使用
  仓库内 Python 源码，配方固定提交见 [SOURCE.md](../scripts/usb-bridge-recipe/SOURCE.md)。
- 配方附带 [iUsbBridge 非商业使用许可](../scripts/usb-bridge-recipe/LICENSE)，它不是 OSI
  定义的开源许可证；商业用途需另行取得许可，具体义务以完整文本为准。
- `tools/iostouch/qt/usb.py`、`usbmux_usb.py`、`usbmuxd_server.py` 的另一个来源是
  [iPhoneUsbTouch](https://gitee.com/xiaozai-van-liu/iPhoneUsbTouch)。仓库 README 已记录
  其许可资料不完整的状态；本次文档核对未补足该授权，不能把这些文件标成已取得 MIT/GPL 授权。

下面直接依赖版本与 [requirements.txt](../scripts/usb-bridge-recipe/requirements.txt) 对齐；
未固定的传递依赖以实际构建环境元数据和随包许可证为准。Apple DDI 不包含在标准桥接包中。

## pymobiledevice3 11.3.1
- 许可证: GPL-3.0-or-later
- 用途: USB 设备发现、Lockdown、CoreDeviceProxy 隧道、RSD、RemoteXPC、HID/Display 服务
- 来源: https://github.com/doronz88/pymobiledevice3

## pmd-pytcp 0.3.7 / pmd-net-addr / pmd-net-proto
- 许可证: GPL-3.0-or-later
- 用途: 无需管理员权限的用户态 TCP/IP 隧道
- 来源: pymobiledevice3 的运行时依赖

## pytun-pmd3 3.0.3
- 许可证: MIT
- 用途: pymobiledevice3 的跨平台 TUN 兼容层。桥接器固定启用用户态 PyTCP 路径，不创建 Wintun 适配器。

## qh3（传递依赖）
- 许可证: BSD-3-Clause
- 用途: HTTP/2 + QUIC TLS (RSD 握手)
- 来源: https://github.com/kornia/qh3

## cryptography（传递依赖）
- 许可证: Apache-2.0 OR BSD-3-Clause
- 用途: SRP-6a、X25519/Ed25519、ChaCha20Poly1305
- 来源: https://github.com/pyca/cryptography

## srptools
- 许可证: MIT
- 用途: SRP-6a 协议
- 来源: https://github.com/idomir/srptools

## construct（传递依赖）
- 许可证: MIT
- 用途: 二进制协议解析
- 来源: https://github.com/construct/construct

## frida 17.17.0
- 许可证: Frida 自有许可证 (非分发依赖，仅用于逆向分析)
- 用途: 动态追踪 sidecar 行为（分析阶段使用，不包含在最终交付中）

## pyinstaller 6.21.0
- 许可证: GPL-2.0-or-later (with bootloader exception)
- 用途: 当前默认构建的桥接 onedir 运行时打包
- 来源: https://github.com/pyinstaller/pyinstaller

## 不分发的组件
- DDI/Image.dmg/trustcache: Apple 私有二进制，不包含

## 运行时兼容资源
- Wintun 预编译 DLL: 上游 `pytun-pmd3` 在 Windows 导入时需要加载该兼容资源，
  其许可证文本随 onedir 运行时的 `_internal/pytun_pmd3/wintun/LICENSE.txt` 提供。
  本桥接器强制使用用户态 PyTCP 隧道，不安装、创建或使用 Wintun 网络适配器。
