# 有线反控自动恢复最后一次完整重试（2026-11-15）

## 行为

失败窗口中的“重试”调用 `StartUsbControlAsync(target)`，与工具栏启动反控共用 `StartDeviceControlCoreAsync`：等待旧反控桥接进程清理，重新核对原设备的绑定，再准备服务、建立反控连接和输入路由。该流程不重启投屏。

现在，当桥接进程报告 `direct_hid_recovery_exhausted`，或宿主的三次常规恢复都失败时，自动执行一次同样的完整启动流程。重试期间停止旧输入路由；成功后不弹窗口，失败且清理结束后才显示失败窗口。开发者模式等需要用户处理的状态仍保留明确提示和手动重试入口。

这次完整重试继续使用原投屏会话的 `WiredMuxResume`，不重置 USB、重新配置设备或重建投屏。旧进程清理失败时保留其所有权，禁止直接创建另一个进程争抢接口。清理期间若设备解绑、改绑或离线，不自动转向另一台手机。

每段连续故障最多一次完整重试。短暂恢复 Ready 不重置额度，防止循环启动；连接稳定 30 秒后才恢复额度，用户手动重试也会重置。重复的 error/terminated 事件共用原有单次执行保护，取消会等待清理并终止后续重试。

## 验证

构建通过，零警告、零错误。`--wired-final-retry` 验证了取消、重复事件、清理失败、改绑、额度耗尽、切换当前选择和其他设备输入隔离；验证失败建议和技术详情在延迟弹窗后仍保留。

原有 `--control-binding` 与 `--silent-auto-control` 回归通过，包括 USB/AirPlay 绑定、USB/无线切换、等待期间改绑、重复启动、延迟清理、捕获会话关闭等待及静默启动窗口行为。

两台真机测试均在反控 IPC 边界注入“恢复耗尽”错误，以验证最后一次完整启动。此项是受控故障注入，不代表已经复现所有真实 USB 掉线原因；没有拔线或重置设备。后续退出旧进程、mux 交接、启动新进程、HID 确认和投屏监测均使用真实设备。

| 真机 | 完整重试及确认耗时 | 投屏监测 | 恢复后输入 |
| --- | --- | --- | --- |
| iPhone13,1 | 5.42 秒 | 77 次采样均 Streaming，句柄不变，帧数 171→429；无弹窗 | 空键盘释放报告收到 HID 确认；取帧并成功编码 PNG |
| iPhone18,3 | 5.12 秒 | 74 次采样均 Streaming，句柄不变，帧数 265→572；无弹窗 | 空键盘释放报告收到 HID 确认；取帧并成功编码 PNG |

两轮均确认旧子进程退出，新桥接进程恢复控制。日志显示 `usbmux reader resumed without VERSION`，沿用已有 mux 序号。原始日志与结果位于 `outputs/diagnostics/wired-final-retry/live-iphone12/` 和 `live-iphone18/`。测试未发送触摸、文字或非空按键，未创建新的设备绑定，测试进程和投屏会话在结束时均正常关闭。

交付从 `main` 整合工作区构建至 `outputs/iPhoneMirror-main`，校验信息位于 `outputs/diagnostics/wired-final-retry/delivery.json`。此版本包含此前的高频截图与 USB 流处理改进，相关验证见 [高频 API 截图排查](api-screenshot-stability-20261115.md)。

首次交付应用 SHA-256 为 `8AA8D675658383983F209C8E1C781E9B8C9F934DFA67C60DAB14D7236E2CDC82`，桥接程序仍为 `AC16E505701CC79528F47841C9298563FDE0301508D1CE05DDC097550C5338E1`，完整桥接运行时清单校验通过。该轮额外的交付版后台启动/API 抽检命令被自动审批策略拦截，未执行；这项不计为通过，也不影响上述已完成的运行时测试和两台真机恢复验证。

## 追加审查与修复

审查发现设备状态的失败通知总是排队显示窗口，晚于错误展示器设置专用“重试”回调。排队的通知会把“重试停止”等专用动作覆盖成通用启动，也可能在关闭窗口后重新打开窗口。新增回归先在修复前稳定失败：`Queued failure notification replaced the error-specific Retry action.`

修复为：UI 线程上的失败或提示通知立即处理，让专用错误展示器最后设置对应动作；后台通知仍调度到 UI，但执行前核对它是否仍是当前状态快照，丢弃已经被恢复或新尝试取代的通知。没有修改投屏和桥接协议。

修复后的 `--wired-final-retry` 全部通过，新增验证专用重试回调、快速关闭失败窗口、过期后台失败不弹窗，以及有效后台失败仍显示窗口。`--control-binding`、`--control-error-presentation`、`--silent-auto-control`、`--reverse-control-countdown` 均通过，构建零警告、零错误。

再次用两台真机在相同 IPC 边界注入恢复耗尽故障：iPhone13,1 完整恢复 5.26 秒，76 次投屏采样均 Streaming，句柄不变，帧数 159→401；iPhone18,3 完整恢复 5.13 秒，74 次采样均 Streaming，句柄不变，帧数 138→390。两台均确认旧控制进程退出、新控制连接收到 HID 确认、PNG 编码成功，自动恢复未弹窗。结果保存在 `outputs/diagnostics/wired-final-retry/review/`，测试结束后设备控制和测试投屏会话均关闭。

审查修复版已从 `main` 整合工作区重新发布到 `outputs/iPhoneMirror-main`，应用 SHA-256 为 `FCB96262C54E9004BC87F80530A55D5833A756D94352BE7B38BD1C3B4BF30566`，桥接程序校验值未变，完整运行时清单复核通过。最新校验记录仍为 `outputs/diagnostics/wired-final-retry/delivery.json`。

本次审查范围内没有发现未解决的问题；上述真机验证仍为受控故障注入，用户的 10–15 台设备环境需要继续以现场日志验证。
