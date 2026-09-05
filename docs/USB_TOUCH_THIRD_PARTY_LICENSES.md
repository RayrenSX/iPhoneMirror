# 第三方许可证

本项目使用以下开源组件，不包含第三方专有控制代码或 Apple 私有二进制。

## pymobiledevice3 11.3.0
- 许可证: GPL-3.0-or-later
- 用途: USB 设备发现、Lockdown、CoreDeviceProxy 隧道、RSD、RemoteXPC、HID/Display 服务
- 来源: https://github.com/doremonmoon/pymobiledevice3 (公开版)

## pmd-pytcp / pmd-net-addr / pmd-net-proto
- 许可证: GPL-3.0-or-later
- 用途: 无需管理员权限的用户态 TCP/IP 隧道
- 来源: pymobiledevice3 的运行时依赖

## pytun-pmd3 3.0.3
- 许可证: MIT
- 用途: pymobiledevice3 的跨平台 TUN 兼容层。桥接器固定启用用户态 PyTCP 路径，不创建 Wintun 适配器。

## qh3 1.9.4
- 许可证: BSD-3-Clause
- 用途: HTTP/2 + QUIC TLS (RSD 握手)
- 来源: https://github.com/kornia/qh3

## cryptography 50.0.1
- 许可证: Apache-2.0 OR BSD-3-Clause
- 用途: SRP-6a、X25519/Ed25519、ChaCha20Poly1305
- 来源: https://github.com/pyca/cryptography

## srptools
- 许可证: MIT
- 用途: SRP-6a 协议
- 来源: https://github.com/idomir/srptools

## construct 2.10.70
- 许可证: MIT
- 用途: 二进制协议解析
- 来源: https://github.com/construct/construct

## frida 17.17.0
- 许可证: Frida 自有许可证 (非分发依赖，仅用于逆向分析)
- 用途: 动态追踪 sidecar 行为（分析阶段使用，不包含在最终交付中）

## pyinstaller 6.21.0
- 许可证: GPL-2.0-or-later (with bootloader exception)
- 用途: 打包独立 Demo (可选)
- 来源: https://github.com/pyinstaller/pyinstaller

## 不分发的组件
- DDI/Image.dmg/trustcache: Apple 私有二进制，不包含

## 运行时兼容资源
- Wintun 预编译 DLL: 上游 `pytun-pmd3` 在 Windows 导入时需要加载该兼容资源，
  其许可证文本随 onedir 运行时的 `_internal/pytun_pmd3/wintun/LICENSE.txt` 提供。
  本桥接器强制使用用户态 PyTCP 隧道，不安装、创建或使用 Wintun 网络适配器。
