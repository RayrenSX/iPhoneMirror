# 有线反控断联与自动恢复复审 — 2026-10-01

当前结论：**FIX VERIFIED（限本文实测范围）**。不能保证设备、驱动或 iOS 永远不再断联；本次验证重点是断联后的完整恢复与投屏隔离。

## 实际运行版本与范围

- 原工作区：`C:\Users\Ray\Documents\iphoneMirror`；并行的界面、本地化等修改保留。
- 构建源码：`C:\Users\Ray\.codex\worktrees\wired-recovery\iphoneMirror`。交付二进制对应这个隔离源码，不等同于原工作区的所有并行编辑。
- 候选应用：`outputs/recovery-validation-v5/iPhoneMirror.exe`，桥接器：同目录 `tools/iUsbBridge.exe`。Program Files 安装未覆盖。
- 实机：iPhone13,1，iOS 18.7.8，BuildVersion 22H352；Apple Mobile Device Service 正在运行。
- DDI 沿用已有且一致的镜像组件，未重挂载。设备与 DDI 信息见 `work/recovery-ddi-check.log`、`work/recovery-ddi-trustcache-check.log`；镜像构建号 27A5228h 不等于设备系统构建号，不能据此单独判定不兼容。

## 故障证据与因果边界

用户已澄清：**看到错误之后才插拔设备**。后续插拔不能解释最初故障。

历史最早明确错误：2026-09-29 13:00:18.352 UTC，`send_failed / ConnectionResetError`；检测和 Recovering 转换存在。9 月 30 日的旧实现刷新后清除了已发现的键盘服务 512，随后重复注册失败，又复用了失效 Lockdown/RSD。完整历史证据及调用图见 `docs/WIRED_RECOVERY_FIX.md`，该文的通过结论仅属于第一轮测试。

本轮失败时间（北京时间）：

| 时间 | 真实事件及影响 |
|---|---|
| 11:09:50.372 | V2 视频静默 2565 ms，但音频年龄为 0；快速重连重置 QuickTime 协议，随后 NEED 和视频计数归零。 |
| 11:10:02.520 | 新协议等待视频超时，开始停止和恢复 USB 配置。 |
| 11:10:14.817 | 反控 USB 读取线程永久退出，全部 mux 连接被标记失效。 |
| 11:10:20.014 | 反控检测超时并触发恢复。 |
| 11:10:23–35 | 三次重试仍连接已失效的本地 mux，反复 BadDevError；没有恢复 ready，最终耗尽重试。 |
| 11:10:45–48 | 设备列表消失又出现，符合用户报错后插拔的更正；不据此推断插拔的精确时刻。 |
| 11:45:14–11:46:11 | V4 实机关闭 mux 后，仅重新创建 transport 仍失败：当前投屏配置不回答第二次 VERSION 握手；恢复最终在 11:46:11.950 耗尽。投屏持续约 60 fps。 |
| 12:09:01.219 | V5 出现非注入的 CoreDevice 隧道 SSLError/读取错误；SDK 只记录异常类名，没有具体 SSL 原因。 |
| 12:09:04.868–12:09:09.499 | 超时触发恢复，新 CoreDevice/RSD/HID 及发送器就绪。12:09:21.003 确认 GUI 输入应答，投屏未重启。 |

原始短暂视频静默的设备端原因仍未确定。能确认的是：在音频持续期间重置协议扩大了故障，随后复用死 mux 或丢弃已有 mux 协议状态都无法恢复控制。

失败轮次保留：`work/recovery-device-20261001-110137`（V2）、`work/recovery-device-20261001-114237/evidence-failed-v4.json`（V4）、`work/recovery-device-20261001-115615`（V5 收尾后设备发现失败，尚未开始采集）。均不计为通过。

## 修正后的恢复链路

```text
真实 GUI Wired Control → Lockdown / 开发者服务 → CoreDevice → RSD → HID
  → 清除旧按键/触点 → 同连接 PING ACK → ready → 主机发送器 / UI Connected
发送异常或健康检查失败
  → 单一修复所有者 → 关闭输入闸门 → 优先只重建 HID
  → 失败则关闭旧 CoreDevice/RSD，保留健康 mux
  → mux 读取线程已死：关闭旧监听器和客户端，重开 USB 读取句柄
     同一投屏配置：保留 mux 序号和端口分配，不再次 VERSION
     已回正常配置：撤销旧监听地址，使用 Apple usbmux
  → 验证新的 Lockdown → 检查开发者服务 → 新 RSD/HID
  → 实际中性输入 + PING ACK → 新 generation ready → 发送器替换 → Connected
  → 正常 GUI 输入再次收到应答
连续三次失败 → 明确 Failed，关闭输入，不显示假 Connected
```

有意义的代码变化：

1. `tools/usb_touch_bridge.py`：刷新保留发现的键盘服务；HID 健康检测与实际恢复分开；监督者独立于发送子任务；串行化修复；保留完整 stdin 帧读取；慢写等待同一次请求，不在已取消请求上盲目重发；候选 HID 验证、远端清理和开发者检查有超时。
2. Python/C# 输入附带 generation；旧队列和旧管道中的按下报文不会在恢复后重放。ready 前释放全部触点和键盘，首个恢复后输入由同连接 PING ACK 验证。
3. `tools/iostouch/qt/usbmux_usb.py`：提供读取线程失效状态，重开句柄时保留已协商版本、全局序号和下一个端口；仅旧 TCP 连接失效。恢复不触碰采集接口、不修改 USB 配置。
4. `tools/iostouch/qt/usbmuxd_server.py`：关闭空闲客户端、收拢异步任务后再关闭 IOCP 循环，避免旧服务线程残留。重连的外层三次重试拥有预算，避免每次再嵌套三轮握手。
5. `DirectUsbInputBridge.cs`、`UsbTouchBridgeHost.cs`、`MainViewModel.cs`：关闭和恢复真实输入闸门，校验目标设备/传输类型，处理终止和进程退出，避免自动恢复弹窗抢焦点。
6. `CaptureSession.cpp/.h`：音频仍到达时不因 2.5 秒无视频就重置 QuickTime；完整媒体静默仍有原来的有界处理。停止后保留 USB 恢复超时警告，防止第二次 Stopped 写入把错误清除。
7. GUI AutomationId、正常焦点释放路径及测试脚本提供可重复的实际 GUI 验证。不会重启应用、采集、驱动或 USB 设备栈来完成反控恢复。

根因分类：F/G（失效 HID、Lockdown/RSD/mux）、C/E（取消及并发）、D/H（重试和桥生命周期）、J/K（发送器与 UI 状态）均有代码/回归或实机日志支持。新增 G 的一轮实机对比直接显示“全新 VERSION 失败，保留协议状态重开读取器”这一差别。L 为采集重置放大故障；最初视频停顿的设备端原因仍是未决问题。DDI 不被认定为已证实根因。

## 构建、自动测试与实机结果

- Python：129 项通过，`work/recovery-python-v5-final.log`。
- C#：15 项生命周期断言通过，`work/recovery-reaudit-csharp-final.log`。涉及门控、错误 ready、终止、进程失败及跨代队列。
- 原生：11 项测试通过，`work/recovery-native-v5-tests.log`；包含音频存活时不重置协议的回归。
- .NET publish、PyInstaller 和原生构建成功；925 个运行时文件按清单校验。完整 WPF suite 的既有 Application shutdown/theme 失败未宣称通过。
- 本轮日志：`C:\Users\Ray\Documents\iphoneMirror\work\recovery-device-20261001-120227`。
- 已测持续时间：1505.799 秒；恢复 5 次；主动刷新 7 次；GUI 应答检查 13 次。
- 视频输出计数：[1, 86640]；音频计数：[1, 70725]；采样 FPS：[31.2, 63.9]。
- 认证要求包括四次实际故障（HID、CoreDevice 两次、mux）、每次恢复及每次主动刷新后的 GUI 输入应答、同一应用/桥/采集会话、无采集终止、无计数归零、无异常和无监视器失败。结果见本轮 `verification.json`。

| 北京时间 | 恢复/刷新完成 | GUI 输入应答 |
|---|---|---|
| 12:03:11.215 | recovery_completed | 12:03:14.399 |
| 12:03:54.646 | recovery_completed | 12:03:56.197 |
| 12:04:32.325 | recovery_completed | 12:04:39.579 |
| 12:05:20.397 | recovery_completed | 12:05:22.508 |
| 12:08:20.841 | direct_hid_refreshed | 12:08:25.514 |
| 12:09:09.499 | recovery_completed | 12:09:21.003 |
| 12:12:09.910 | direct_hid_refreshed | 12:12:12.774 |
| 12:15:10.485 | direct_hid_refreshed | 12:15:15.970 |
| 12:18:10.908 | direct_hid_refreshed | 12:18:15.720 |
| 12:21:11.346 | direct_hid_refreshed | 12:21:17.071 |
| 12:24:11.811 | direct_hid_refreshed | 12:24:15.816 |
| 12:27:12.253 | direct_hid_refreshed | 12:27:15.132 |

应答验证使用真实预览焦点释放路径发送中性键盘报文并收到同一 HID 连接的 PING ACK，证明发送通道可用；没有用截图观察 iOS 画面效果。无线模式、桥接进程被强杀、无限期运行不在本次实机证明范围内。

## 二进制、备份与回退

- V2 桥 SHA-256：`D9DC790C4F00DC3F049A3E979B61E1F85DBCC2BDE01FEBE410363309BB0A3634`。
- V5 桥 SHA-256：`A31534B70D03B41A245DB103F060325B7E9538939A807F0E4AA5A76BC8362E3F`。
- V5 运行时清单 SHA-256：`C6E6E14CF4FB2EBE6327BD3F456555DC89F7129AF316FE964245405201B793ED`。
- 应用、原生库及清单完整哈希：`work/recovery-v5-binaries-sha256.csv`；桥源码、runtime 清单与已加载模块分别校验。
- 新增完整桥备份：`artifacts/backup-before-recovery-v4-20261001-113801`，含 `_internal`、清单和原生库；原始及第一轮备份均保留。候选使用独立目录，失败版本未覆盖安装目录。
- 源码补丁：`work/recovery-only.patch`，对保存的任务前源码验证可应用；历史补丁和失败日志保留。
- 正常使用启动器：`Launch-Recovery-Build.cmd`，最终验证后指向已验证版本并清除故障注入变量。

## 最终收尾与正常运行状态

长测结束后，12:29:50.415 同时确认正常 USB 配置与采集停止完成，随后通过 UI Automation 关闭应用；这是测试收尾，不是故障恢复机制。12:30:51 以 `-NoFault` 启动相同二进制，12:31:03 Connected，12:31:05 GUI 输入应答，12:31:31 正常启动检查通过。交付时应用 PID 44564、桥 PID 30288，投屏与反控保持运行。

正常运行日志：`work/recovery-device-20261001-123051`。`Launch-Recovery-Build.cmd` 已指向 V5，并清除两项故障注入变量。Program Files 内旧安装不属于本次验证和覆盖范围，请使用该启动器。

## 使用方式声明

```text
Computer Use: NOT USED
Visual desktop agent: NOT USED
Screenshot-based clicking: NOT USED
Coordinate clicking: NOT USED
UI automation: Script-based Windows UI Automation
Real iPhoneMirror GUI: USED
Real device: USED
```
