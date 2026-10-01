# 有线反控重连延迟优化 — 2026-10-01

状态：**FIX VERIFIED（V9，限本文实测范围）**。普通 HID / CoreDevice 重连已明显加快；底层 mux 重建仍可能约 17 秒，尚不能承诺所有断开都无感。

## 范围与版本

承接上一轮恢复修复，本轮任务是减少断开到恢复可用的时间。原工作区的界面、本地化等并行改动保留。候选 `outputs/recovery-latency-v9` 使用已验证 V5 的应用及原生库，重新打包本轮 Python 桥；不等同于当前工作区全部改动。Program Files 安装未覆盖。

实机沿用 iPhone13,1 / iOS 18.7.8；本轮启动时 Apple Mobile Device Service 运行正常，实际设备经有线 GUI 路径完成 Lockdown/CoreDevice/RSD/HID 握手。没有重装驱动或更换 DDI。

## 延迟证据与修改

最早历史故障仍是 2026-09-29 13:00:18.352 UTC 的 `send_failed / ConnectionResetError`，原恢复路径缺陷见 `WIRED_RECOVERY_FIX.md` 和 `WIRED_RECOVERY_REAUDIT.md`。

V5 基线来自 `work/recovery-device-20261001-120227`：HID 注入断开到 ready 176.1 ms；两次隧道断开 13381.7 / 7684.8 ms；mux 断开 18067.3 ms；一次自然隧道异常 8279.1 ms。主要延迟来自 SDK 读取任务死亡未立即通知恢复、在死隧道上重试 HID、首次固定退避，以及对失效 mux 协议状态的长时间重试。

本轮改动：

1. `tools/usb_touch_bridge.py` 独立观察 CoreDevice 读取任务结束，并检查 mux 读取器死亡；EOF 或正常返回也视为传输终止。观察者不拥有 SDK 任务的取消权，停止应用不触发恢复。
2. 已确认底层传输死亡时直接重建传输，不再等待注定失败的 HID 请求。首次恢复立即执行，后续失败仍采用有界退避。独立监督者、三次重试上限及 generation 输入闸门保留。
3. HID 健康检查间隔从 15 秒最终缩短到 0.5 秒。中间版本的两次空闲 HID 实测中，实际重建仅 128–129 ms，但总间隔 1637–1664 ms，主要在等待 2 秒健康检查。因此最终改为每秒两次小 PING，原有单次应答超时不变。输入发送错误仍可以立即触发恢复；此项不等于承诺所有故障均低于一秒。
4. `tools/iostouch/qt/usbmux_usb.py` 保留 mux 序号和源端口来重开 USB 读取器。仅首个 SYN 探测限制为 0.75 秒，后续重试保留正常预算，避免提前丢弃仍可能有效的状态。
5. V6 于 12:48:09.535（北京时间）关闭 mux，旧状态重开后两次连接失败，第二次仍用了 10 秒，外层到 15 秒才退出，12:48:26.967 恢复。曾尝试 2 秒 resumed 验证上限，但 V8 实机证明不能依赖此优化：19:27:50.301 关闭 mux，19:27:52.514 提前放弃旧状态，此后第二次 VERSION 不回应，19:28:20.416 恢复耗尽。V9 撤回该上限及连续短 SYN，沿用 V6 的保守 mux 预算；保留失败诊断。底层 USB 故障可能仍有较长等待，不能宣传所有断开都亚秒恢复。
6. 新增 SDK EOF、停止取消、死 mux、跳过无效 HID 修复、首个 SYN 失败清理和后续预算、成功 resumed 状态保留等回归。UIA 测试支持分类型故障计划和更密集的观察。计时分析同时保存主机接收时间和桥进程单调时钟 elapsed_ms，避免把缓冲日志的几毫秒间隔误当成实际恢复时间。

根因分类：A（死亡检测延迟）、D（无效等待/重试策略）、G（失效 mux 协议状态）均有代码与日志支持。设备端自然隧道错误及音视频静默的最初诱因尚未确定。

## 已完成和待完成的验证

- Python：136 项通过，`work/recovery-latency-v9-python-final.log`。
- PyInstaller 构建通过，`work/recovery-latency-v9-build.log`；925 个运行时文件按清单核验。
- 应用和原生库直接沿用 V5；本轮没有声称重新运行完整 WPF 测试套件。
- V6 的 15 分钟 UIA 监测完成 8 次恢复、3 次主动刷新，但最终严格验证发现 12:54:14.486 有一次音视频同时静默后的 QuickTime 协议重连和音频计数归零，因此不计为投屏全程无中断通过。该事件晚于最后一次恢复约 29 秒，不能据此直接认定由反控恢复引起。
- V7 中间测试：`work/recovery-device-20261001-191133`，五次注入全部恢复，包含 mux 764 ms；另有自然隧道异常成功恢复。为验证更短的空闲检测间隔，约 12 分钟时主动结束此轮并正常退出；不计为完整 15 分钟认证。
- V8 失败测试：`work/recovery-device-20261001-192507`，HID 284 ms、隧道 626 / 766 ms，但 mux 重建失败，完整测试不通过。
- V9 初次启动失败：`work/recovery-device-20261001-194556`，在首次握手阶段出现空枚举与 Apple mux 连接断开，未进入反控恢复测试。保留失败记录；GUI 测试增加等待实际初始视频帧后再启用有线反控。用户随后说明曾使用手机，但没有足够证据将该启动错误归因于此。
- V9 最终测试：`work/recovery-device-20261001-201213`，`verification.json` 通过，持续 901.754 秒，5 次注入与恢复、3 次主动刷新、9 次 GUI 应答检查、1085 条输入验证记录。
- 全程同一应用 PID 34908、桥 PID 19556、采集会话 `0000885C-00000008A50EF780`。无应用异常、无采集停止、无 QuickTime 快速重连、无音视频计数归零。视频输出计数 1 → 48600，音频计数 1 → 42800；采样 FPS 为 39.0–61.5，不声称恒定 60 fps。

## 最终实测耗时

下表是从实际注入的 socket/读取器关闭日志，到完成中性输入 + 同连接 ACK 验证并恢复发送器的时间，包含检测等待。主机 stdout/stderr 收集会带来少量误差；不同轮次不是严格受控的性能基准。

| 故障 | V5 基线 | V9 本轮 |
|---|---:|---:|
| HID 独立断开 | 0.176 秒（单次） | 0.152 / 0.239 秒 |
| CoreDevice 隧道断开 | 13.382 / 7.685 秒 | 0.666 / 0.581 秒 |
| 底层 mux 读取器断开 | 18.067 秒 | 16.748 秒 |

HID 的主要收益是将空闲探测间隔从 15 秒降为 0.5 秒，单次测量仍取决于故障发生在检查周期中的位置。隧道实测缩短约 91–96%。mux 保留较长预算是实测失败后作出的可靠性取舍，并没有解决该类故障的无感目标。

实际恢复时间线（2026-10-01，北京时间）：

| 时间 | 事件 |
|---|---|
| 20:13:48.994 | 主动关闭真实 CoreDevice socket |
| 20:13:48.999 | `recovery_triggered`，关闭输入闸门 |
| 20:13:49.026 | 开始重建传输 |
| 20:13:49.300 | 开始 RSD 连接 |
| 20:13:49.483 | 开始 HID 初始化 |
| 20:13:49.660 | 已验证 HID、替换发送器并完成恢复 |
| 20:13:50.697 | 正常 GUI 预览焦点释放路径的键盘报告收到 ACK |

三次主动刷新完成于 20:18:58、20:21:58、20:24:59，每次随后均收到 GUI 输入应答。底层 mux 测试第一轮保留协议的尝试在 15 秒预算耗尽后失败，第二轮重新握手成功；投屏会话保持连续。

## 二进制与备份

桥 SHA-256：

- V5：`A31534B70D03B41A245DB103F060325B7E9538939A807F0E4AA5A76BC8362E3F`
- V6：`7F131C43249AF10DD49B3A7AC584306E22BD1DBF4A501A2863941A0E4FB6B6FE`
- V7：`30D9D26F9259E35154973B966905F74522DCA8D55E1FA8CC937A7BCB753CA81B`
- V8：`2E061BEA2167E9BC62AB2BCAEC596723A36FF8108C17230BFAE5552FCEC9AFEC`
- V9：`EED7746D10A83B0FF655B04F94440C4573CFD0BB8809B85751D35E5F98403E8A`

V5 完整运行时备份：`artifacts/backup-before-recovery-latency-20261001-124008`。V6 完整运行时及改动前源码备份：`artifacts/backup-before-recovery-latency-v7-20261001-190637`。V7 完整备份：`artifacts/backup-before-recovery-latency-v8-20261001-191848`。V8 完整备份：`artifacts/backup-before-recovery-latency-v9-20261001-193546`。各目录包含 `_internal`、运行时清单、桥及 SHA-256 文件。本轮候选新建目录，旧版保留。

完整二进制哈希：`work/recovery-latency-v9-binaries-sha256.csv`。打包源码位于 `work/usb-recovery-build/latency-v9/src`，与工作区三个桥文件逐一校验相同。

范围限定的补丁：`work/recovery-latency-only.patch`，相对保存的 V5 源码生成并通过反向可应用检查。源码及补丁哈希：`work/recovery-latency-v9-source-sha256.csv`。启动器 `Launch-Recovery-Build.cmd` 已指向 V9，并清除故障注入环境变量；V5 仍可从 `outputs/recovery-validation-v5/iPhoneMirror.exe` 回退启动。

## 正常运行交付

长测后通过 UIA 正常停止采集，20:35:40 确认 USB 配置恢复，再关闭应用；这属于切换测试配置的收尾，不是恢复机制。随后以 `-NoFault` 启动相同 V9 二进制，20:37:15 Connected，20:37:17 GUI 输入收到 ACK，20:37:46 正常启动检查通过，无注入故障。

正常运行日志：`work/recovery-device-20261001-203649`。交付时应用 PID 27500、桥 PID 49348，投屏与有线反控保持运行。使用工作区根目录的 `Launch-Recovery-Build.cmd` 启动此版本；Program Files 中旧安装不属于此次覆盖范围。

## 使用方式与证明边界

GUI 测试通过 `AutomationId`、InvokePattern 和焦点变化触发真实 Wired Control 路径。恢复后由正常预览焦点释放路径发送中性键盘报告，并确认同一 HID 连接的 PING ACK；没有通过截图检查 iOS 视觉效果。有限时间、单设备的观察不证明永不掉线，强杀桥进程、物理拔线和无线模式不属于本轮实机范围。

```text
Computer Use: NOT USED
Visual desktop agent: NOT USED
Screenshot-based clicking: NOT USED
Coordinate clicking: NOT USED
UI automation: Script-based Windows UI Automation
Real iPhoneMirror GUI: USED
Real device: USED
```
