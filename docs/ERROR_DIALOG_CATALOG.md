# 错误弹窗与确认弹窗目录

> 本表按当前代码实际入口整理。控制类错误统一显示在独立的“反向控制状态”窗口中。

## 反向控制错误

| 场景 | 触发条件 | 窗口 | 用户显示内容 | 技术详情 |
|---|---|---|---|---|
| 蓝牙启动失败 | 蓝牙服务返回失败或启动异常 | 反向控制状态窗口 | 蓝牙控制无法连接，请确认蓝牙已开启且设备已配对 | 蓝牙服务错误和原始异常 |
| 有线/USB 启动失败 | USB bridge 启动、设备发现或握手失败 | 反向控制状态窗口 | 按错误码显示信任、锁定、USB、DDI 或触控服务指导 | bridge 错误码和诊断 |
| 无线启动失败 | RemotePairing、隧道或无线 bridge 启动失败 | 反向控制状态窗口 | 显示无线配对、发现、DDI 或网络指导 | bridge 错误码和诊断 |
| 控制通道中断 | bridge 报告 error 或 terminated | 反向控制状态窗口 | 控制通道中断，正在尝试自动重新连接 | 重连次数、错误码 |
| 自动恢复失败 | USB/无线最多重连 3 次仍失败 | 反向控制状态窗口 | 反向控制连接中断，并提示重新连接设备 | 重试次数、异常 |
| 停止/清理失败 | bridge dispose、停止或资源释放异常 | 反向控制状态窗口 | 控制已停止，但设备资源可能尚未完全释放，请重新连接设备 | 清理异常 |
| 设备未信任 | apple_device_not_trusted 或 NotPaired | 反向控制状态窗口 | 请在手机上点按“信任”并重试 | bridge 错误码 |
| 设备锁定 | apple_device_locked 或等待密码 | 反向控制状态窗口 | 请解锁 iPhone 并完成信任/密码提示 | bridge 错误码 |
| USB 配对服务不可用 | apple_usbmux_unavailable | 反向控制状态窗口 | 安装或修复 Apple Devices/iTunes 支持 | bridge 错误码 |
| DDI 未挂载 | developer_image_required | 反向控制状态窗口 | 挂载匹配系统版本的官方 Developer Disk Image | bridge 错误码 |
| DDI 下载失败/超时 | developer_image_download_failed 或 timeout | 反向控制状态窗口 | 检查 GitHub 网络或提供官方本地镜像 | 下载诊断 |
| DDI 完整性失败 | developer_image_download_integrity_failed | 反向控制状态窗口 | 镜像校验失败，下载文件可能不完整或损坏 | 校验结果 |
| DDI 限流 | developer_image_download_rate_limited | 反向控制状态窗口 | GitHub 校验/限流失败，请稍后重试 | HTTP 状态 |
| DDI 版本不兼容 | developer_image_download_incompatible | 反向控制状态窗口 | 更新应用或提供匹配的官方本地镜像 | 系统/镜像版本 |
| DDI 个性化失败 | developer_image_tss_failed | 反向控制状态窗口 | Apple 个性化服务或设备挂载失败 | TSS/挂载诊断 |
| DDI 重挂载失败 | developer_image_remount_failed | 反向控制状态窗口 | 关闭占用设备的工具并重试 | mount/unmount 诊断 |
| 本地 DDI 无效 | manifest 缺失、文件损坏或挂载失败 | 反向控制状态窗口 | 确认来自官方 Xcode 且包含必需文件 | manifest/版本校验 |
| Developer Mode 未开启 | developer_mode_required 或检查失败 | 反向控制状态窗口 | 在设置 > 隐私与安全性 > 开发者模式中开启并重启 | lockdown 查询 |
| 无线配对未完成 | wireless_remote_pairing_required | 反向控制状态窗口 | 先通过 USB 完成一次初始化 | RemotePairing 状态 |
| 无线设备不可发现 | wireless_device_not_discoverable | 反向控制状态窗口 | 保持解锁、同一局域网并允许防火墙发现 | Bonjour/mDNS 诊断 |
| 无线隧道失败 | wireless_remote_pairing_failed | 反向控制状态窗口 | 通过 USB 初始化并确认网络未隔离 | pairing/tunnel 异常 |
| 触控认证门关闭 | remote_control_gate_unavailable/closed | 反向控制状态窗口 | 未确认触控认证，反控未启动 | gate 状态 |
| HID 触控服务缺失 | universal HID service 或 no such service | 反向控制状态窗口 | 保持解锁并信任此电脑后重试 | service inventory |
| 触控面缺失 | touch_surface_unavailable | 反向控制状态窗口 | CoreDevice 未发布 mainTouchscreen | surface/service 列表 |
| iOS 路径不支持 | remote_control_unsupported_ios 或 9021 | 反向控制状态窗口 | 重启 iPhone，仍失败时使用蓝牙控制 | 9021/认证路径 |
| 设备未找到 | apple_device_not_found 或发现失败 | 反向控制状态窗口 | 重新插拔、解锁、信任或先 USB 配对 | UDID/mux/Bonjour |
| USB mux 断开 | socket broken 或 muxexception | 反向控制状态窗口 | 保持解锁并重新插拔数据线 | mux 异常 |
| 未知控制错误 | 未匹配稳定错误码 | 反向控制状态窗口 | 查看诊断日志后重试 | 原始异常和错误码 |

## 投屏/采集错误

| 场景 | 触发条件 | 窗口 | 用户显示内容 |
|---|---|---|---|
| USB 预检失败 | EnsureSourceReadyAsync 返回失败 | CaptureStatusNoticeWindow | USB 配置、信任、连接或设备指导 |
| 创建会话失败 | native session 创建返回失败 | CaptureStatusNoticeWindow | 稳定的启动失败指导 |
| 启动异常 | 投屏启动流程抛出异常 | CaptureStatusNoticeWindow | 无法启动投屏，请重试或查看日志 |
| 会话意外停止 | 设备关闭连接或会话终止 | 停止/警告窗口 | 投屏已停止及重新连接建议 |
| 停止失败 | teardown 返回失败 | CaptureStatusNoticeWindow | 会话释放和重新连接建议 |
| 采集恢复失败 | 采集线程无响应或恢复失败 | CaptureRecoveryWindow | 恢复、停止投屏或重新连接建议 |
| 受保护内容 | native 层报告 protected content | ProtectedContentNoticeWindow | 当前内容受系统保护，无法捕获 |

## 应用与配置错误

| 场景 | 触发条件 | 窗口 | 用户显示内容 |
|---|---|---|---|
| 关于/开发者/绑定窗口打开失败 | 创建窗口抛出异常 | AppPromptWindow | 操作未完成，请重试；持续失败时查看诊断日志 |
| 外部链接打开失败 | Shell/浏览器启动失败 | AppPromptWindow | 操作未完成，请重试并查看诊断日志 |
| 驱动管理失败 | 驱动操作返回失败 | AppPromptWindow | 驱动失败文案及日志建议 |
| 设备绑定创建失败 | CreateProfile 返回失败 | AppPromptWindow | 无法创建设备档案 |
| 缺少 USB/AirPlay 目标 | 蓝牙绑定没有目标设备 | AppPromptWindow | 请先连接 USB 或 AirPlay 设备 |
| 蓝牙配置启动失败 | 配置外围设备启动失败 | AppPromptWindow | 蓝牙配置启动失败 |
| 蓝牙绑定保存失败 | 保存 Bluetooth profile 失败 | AppPromptWindow | 无法保存 Bluetooth 设备绑定 |
| 绑定兼容性确认 | 兼容性为 Compatible/Unknown | 确认弹窗 | 显示绑定确认说明 |
| 放弃录制 | 关闭设置且有未保存内容 | 确认弹窗 | 确认是否放弃当前录制 |
| 无线设置无变化 | 提交值与当前值相同 | AppPromptWindow | 无线设置未发生变化（信息提示） |

## 统一规则

- 控制错误唯一用户可见窗口为反向控制状态窗口。
- 技术错误码、原始异常和 bridge 诊断放在状态窗口详情中。
- 投屏/采集错误使用 CaptureStatusNoticeWindow，不应与反控错误同时显示。
- 代码入口主要位于 MainViewModel.cs、MainWindow.xaml.cs、CaptureStatusNoticeWindow.xaml.cs、AppPromptWindow.xaml.cs 和 DeviceBindingWindow.xaml.cs。
