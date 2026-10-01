# GitHub 全量 Issues 修复核查（2026-10-01）

> 此文件保留开始修复前的核查结论；后续代码修改、验证及剩余条件见 [修复跟进报告](GITHUB_ISSUE_FIXES_2026-10-01.md)。

**结论：没有全部完成。** 全量取得 24 条 issues，开放 13 条、关闭 11 条。当前仍存在可直接复现的漏修（#31）、未实现的功能（#26）、部分实现的功能（#29），以及尚未完成发布或报告场景验收的修改。不能用关闭数量或通用测试通过率表示修复完成率。

## 核查范围与基线

- 仓库：[RayrenSX/iPhoneMirror](https://github.com/RayrenSX/iPhoneMirror)。通过 GitHub REST API 对 `issues?state=all`、每条 issue 的 timeline、全部 PR、全部 Releases 分页取数；统计排除了 PR。
- 已取得 **24/24 条 timeline**，其中评论数量与每条 issue 的 `comments` 字段一致。核对了正文、评论、关闭理由和关联事件。
- GitHub `main` 与本地 HEAD 均为 [`76e962f`](https://github.com/RayrenSX/iPhoneMirror/commit/76e962f37f55a05e4a7b4c86235338957d879c9f)，提交时间 2026-09-29。
- GitHub 最新发布版仍为 [`v1.8.3`](https://github.com/RayrenSX/iPhoneMirror/releases/tag/v1.8.3)，发布时间 2026-09-06，tag 对应提交 `1270c443a29457751cadd20f6d7bbe1ce213b9db`。`main` 比该提交多 16 个可达提交。
- 仓库里的 `docs/releases/v1.8.4-test4.md` 是版本说明文件；本次 API 返回的 GitHub Releases 中没有对应发布，不能把它作为已发布证据。
- 本地包含大量未提交修改。本报告分别说明“正式版已有”“main 已有”“仅本地追加”，没有把本地候选修复算作 GitHub 已交付。
- 本次核查未修改业务源码或 GitHub issue 状态，新增报告、数据快照和只读最小复现程序。测试执行当前工作区源码；本次没有针对报告人的手机、游戏、远程桌面或热点进行实机重现。

原始 API 数据、完整 timeline、附件、测试日志及结构化核查结果保存在 [取数与验证目录](../outputs-next/github-issue-audit-2026-10-01/)。其中 [audit-data.json](../outputs-next/github-issue-audit-2026-10-01/audit-data.json) 包含完整标题、正文、评论、GitHub 状态及本次核查分类。

## 统计口径

### GitHub 状态

| GitHub 状态 | 数量 | Issue |
|---|---:|---|
| Open | 13 | #10、#19、#24、#26、#29、#30、#31、#32、#34、#35、#37、#38、#39 |
| Closed / completed | 5 | #11、#12、#14、#16、#23 |
| Closed / not_planned | 5 | #15、#17、#21、#25、#27 |
| Closed / duplicate | 1 | #18 |

### 按实际交付证据分类

以下分类互斥，合计 24 条；“有实现”不等于已在原报告环境验收。

| 核查分类 | 数量 | Issue |
|---|---:|---|
| 原始需求已实现，且有发布依据 | 2 | #11、#12 |
| 有对应实现，待发布或报告场景验收 | 7 | #10、#19、#24、#30、#32、#34、#35 |
| 明确未修、未实现或仅部分实现 | 3 | #26、#29、#31 |
| 有历史处理或相关改进，缺少闭环证据 | 10 | #14、#15、#16、#17、#18、#21、#23、#25、#27、#38 |
| 信息/兼容性请求，尚待答复确认 | 2 | #37、#39 |

#12 的“已实现”仅指 USB 反控功能已交付，其断联问题另由 #24 跟踪。五条 `completed` 中，#14、#16、#23 不能仅凭关闭理由认定对应故障已修复。

## 全部 24 条逐项核查

| Issue | GitHub 状态 | 核查结果 | 对应证据与仍需完成的工作 |
|---|---|---|---|
| [#10 DRM 视频黑屏的提示/降级](https://github.com/RayrenSX/iPhoneMirror/issues/10) | Open | **提示功能已实现并发布；报告场景待验收** | `ProtectedVideoDetector` 随 `f9d2866` / v1.7.1 加入，v1.8.3 的 `CaptureSession.cpp` 已调用，核心测试覆盖持续黑帧和恢复。issue 约定发布后用 beIN CONNECT 确认；未见后续确认。可交付范围是疑似受保护内容提示，不代表受保护视频本身可被采集。 |
| [#11 USB-C 耳机转接线误识别](https://github.com/RayrenSX/iPhoneMirror/issues/11) | Closed / completed | **已实现，有发布依据** | `3e32224` / v1.8.1 改为 iPhone/iPad PID 白名单；发布说明明确排除音频适配器。`DeviceCatalog` 调用 `IsAppleMobileCaptureParent`，驱动测试明确拒绝 PID 110A、1200、12A7、200E。此次最小复现也确认非移动设备 PID 被拒绝。 |
| [#12 USB 反控功能](https://github.com/RayrenSX/iPhoneMirror/issues/12) | Closed / completed | **原功能已实现，有发布依据** | v1.8.2 集成 USB/无线反控桥；v1.8.3 修复升级后的 libusb 运行时查找。维护者说明已发布，用户后续也确认可用。但用户同时反馈不稳定，因此不能以本条关闭推导 #24 已解决。 |
| [#14 远程软件控制时鼠标固定右下角](https://github.com/RayrenSX/iPhoneMirror/issues/14) | Closed / completed | **无法确认修复** | issue 由报告人自行关闭，无评论、修复提交或远程控制场景验收。代码中有相对鼠标、捕获与释放逻辑，但未见 RDP/远程软件专项测试。需要指定远程软件与输入模式重现。 |
| [#15 开游戏麦克风后 USB 闪烁、音频中断](https://github.com/RayrenSX/iPhoneMirror/issues/15) | Closed / not_planned | **未证明修复，建议恢复跟踪** | 维护者因当时无法重现而关闭；关闭后另一用户报告 iPad Pro + Call of Duty Mobile 同样出现，关麦即恢复。其“resolved”评论指圆角显示，后半句仍希望声音修复。当前虽有媒体处理改善，没有 BGMI/CODM 开关麦的验收证据。 |
| [#16 BLE 捕获鼠标、无法控制/疑似卡死](https://github.com/RayrenSX/iPhoneMirror/issues/16) | Closed / completed | **解释与替代方案已提供；故障未闭环** | 评论解释隐藏指针设计、F9 退出，并建议使用 USB/无线控制；这没有证明“无法控制/卡死”已修复。后续 main 有 BLE 超时、恢复、释放输入改进，本次运行时测试通过，但未覆盖报告人的 iPhone 11 + AirPlay 实机。 |
| [#17 蓝牙延迟和丢包过高](https://github.com/RayrenSX/iPhoneMirror/issues/17) | Closed / not_planned | **有后续优化，缺量化验收** | `BluetoothMouseReportCoalescer`、通知超时、鼠标通道恢复等实现存在，9 月下旬提交持续改善。缺少报告环境前后 p50/p95 延迟、丢包和卡顿对比；改用有线/无线是建议，不是蓝牙性能问题已修复的证据。 |
| [#18 蓝牙控制延迟、点击失灵/黑屏](https://github.com/RayrenSX/iPhoneMirror/issues/18) | Closed / duplicate | **重复关闭，不能计作独立修复** | 评论建议改用其他控制方式；timeline 未给出明确重复目标或自动关闭提交。可与 #16/#17 合并验收，但目前没有独立验证证据。 |
| [#19 Windows 移动热点 AirPlay 发现](https://github.com/RayrenSX/iPhoneMirror/issues/19) | Open | **main 已修正单网卡选择；未发布、未实机验收** | `76e962f` 引入 `DnsSdAllInterfaces`。调用者未指定接口时不再只选优先网卡，提交给 Windows 的 `InterfaceIndex` 为 0；测试覆盖全接口 sentinel 与本机接口转换。v1.8.3 不含此修改。需要以太网 + Windows 热点 + 手机验证发现、连接、热点开关后的重新发布。 |
| [#21 iOS 26 的 USB driver stack 问题](https://github.com/RayrenSX/iPhoneMirror/issues/21) | Closed / not_planned | **无法确认修复，关闭后仍有失败反馈** | 维护者建议高级模式修复驱动后关闭；报告人随后明确回复无效。后续驱动诊断及本地父驱动管理改进不能替代对该 iPhone 13 / iOS 26 的重测。应和 #15 的 USB/音视频问题分别记录实际错误及日志。 |
| [#23 GitHub 下载失败导致不能反控](https://github.com/RayrenSX/iPhoneMirror/issues/23) | Closed / completed | **网络规避建议已给出；故障未确认解决** | 已核对附件：错误是“开发者镜像下载未通过 GitHub 内容校验或当前 API 被限流”，发生在 DDI 下载，**不是应用更新检查**。评论只建议代理 TUN；无用户成功确认。当前桥有 DDI 来源/校验/错误处理，但仍需在原代理/API 限流环境验证。 |
| [#24 有线反控经常断开，失焦后易失效](https://github.com/RayrenSX/iPhoneMirror/issues/24) | Open | **main 有针对性修复；本地追加恢复改动，仍待发布验收** | PR #28 所述序号乱序问题已在 main 的 `_sendLock` 内分配与序列化路径解决；PR #36 合入相关修复。9 月下旬又有控制生命周期改进。本地另有心跳、事务化恢复、焦点门控等未提交修改；本地恢复报告自己标为部分验证/实机进行中，不能算已发布完成。 |
| [#25 iPad 录屏一帧卡几秒](https://github.com/RayrenSX/iPhoneMirror/issues/25) | Closed / not_planned | **有相关录制优化，未证明报告故障修复** | 关闭后报告人明确区分“iPhone 正常、iPad 预览正常但录制卡”。main 在 PR #36 增加 `FrameReaderAsync`，将 GPU 读回与输出节拍解耦，但没有该 iPad 分辨率、帧率和编码器下的前后对比。应与 #38 一起做录制专项验收。 |
| [#26 直播加入麦克风](https://github.com/RayrenSX/iPhoneMirror/issues/26) | Open | **未实现** | 当前 `MediaOutputRequest`、设置页及音频输入仍围绕投屏音频；未发现电脑麦克风设备选择、采集并与手机音频混合的实现。需要补齐采集源、混音与直播输出。 |
| [#27 iOS 15.4 反控兼容性](https://github.com/RayrenSX/iPhoneMirror/issues/27) | Closed / not_planned | **兼容性答复已给，不能算已支持** | 旧评论称有线/无线需 iOS 18+；当前兼容矩阵将 iOS <17 标为未测试/CoreDevice 可能不可用，并按真实 HID 服务能力启用。RemotePairing/direct HID 回退不证明 iOS 15.4 可反控，不能把较旧版本配对回退写成全版本兼容修复。 |
| [#29 快捷方式一键打开精简独立窗口](https://github.com/RayrenSX/iPhoneMirror/issues/29) | Open | **部分实现** | main 的投屏设置有 `OnApplyLightweightModeClick`，可以切换轻量布局。但未见可从桌面快捷方式直接选择设备、开始会话并仅打开独立窗口的完整启动流程。现有独立窗口和轻量布局不能覆盖“一键直接打开”的完整诉求。 |
| [#30 Wi-Fi 投屏后开启蓝牙控制卡死/崩溃](https://github.com/RayrenSX/iPhoneMirror/issues/30) | Open | **main 有相关修复；未发布、未报告场景验收** | 蓝牙启动以异步方式启动绑定发现，`ControlStatus.RequestPromptAsync` 处理选择；BLE 通知增加超时、代次和故障恢复。PR #36 及后续 `15fb8e4`、`cc0f3ea`、`76e962f` 含相关修改。本次运行时 BLE 生命周期测试通过，但还不能证明 iOS 27 + Wi-Fi 场景完全修复。 |
| [#31 40 位 UDID 被驱动枚举过滤](https://github.com/RayrenSX/iPhoneMirror/issues/31) | Open | **明确未修，已最小复现** | `DriverConstants.cs:44` 的序列号正则仍为一个字符加 `{6,38}`，总长最多 39。直接编译并调用 v1.8.3、main、本地三份真实 `IsAppleMobileCaptureParent`：40 位均拒绝，24/39 位均通过。枚举和提权参数校验均会受影响。 |
| [#32 Windows 文本粘贴到手机](https://github.com/RayrenSX/iPhoneMirror/issues/32) | Open | **main 已实现；未发布、待实际输入验收** | PR #36 加入 `SendUsbPasteTextAsync` → `SendPasteTextAsync` → Python `paste_text` → `PasteboardService.set_text`/粘贴键序列。运行时测试覆盖 Ctrl+V 状态与重复事件抑制。v1.8.3 不含该管线；仍需 WhatsApp、小语种、权限拒绝和断线测试。该实现以 USB/无线桥接为范围，不代表 BLE 任意文本粘贴已支持。 |
| [#34 停止录屏后不保存/丢弃](https://github.com/RayrenSX/iPhoneMirror/issues/34) | Open | **main 已实现；未发布、待文件生命周期验收** | PR #36 加入丢弃按钮和确认，调用 `DiscardPendingRecording()` 删除待保存文件，刷新下一条待保存记录。v1.8.3 不含该方法。仍需验证停止→取消保存→丢弃→再次录制及删除失败提示。 |
| [#35 全屏无法按键退出](https://github.com/RayrenSX/iPhoneMirror/issues/35) | Open | **main 已有按键优先级修复；未发布、待窗口组合验收** | 主窗口及原生预览在反控键盘路由前处理退出全屏；当前逻辑测试检查 Escape/F11 的本地处理优先级及捕获输入路径。需 iPad、主预览/独立预览、启用/未启用反控、失焦恢复组合验证。测试版说明存在不代表 GitHub 已发布。 |
| [#37 请求 PID 12AB / iPad17,4 的现有 Valeria trace](https://github.com/RayrenSX/iPhoneMirror/issues/37) | Open | **资料请求尚未答复** | 无评论。仓库 PID 白名单和核心测试包含 12AB，但未找到同时匹配 iPad17,4 / iPadOS 26.7 的既有脱敏激活前后 trace；通用测试数据不能替代请求的实测资料。本条要求现有资料或确认没有，并非要求新增协议操作。 |
| [#38 录屏声音慢于视频](https://github.com/RayrenSX/iPhoneMirror/issues/38) | Open | **相关改进存在，尚不能认定修复** | main 的录制使用墙钟视频补帧、音频缺失补静音和采样率归一化，部分机制在旧版已存在；本地这份 `MediaOutputService` 差异主要为本地化，不能声称新修复。缺少针对 iPhone14,8 / iOS 16.0.3 的音画偏移与长时漂移测试。 |
| [#39 MacBook 桌面投屏到 Windows](https://github.com/RayrenSX/iPhoneMirror/issues/39) | Open | **兼容性请求尚未答复/验证** | 无评论。项目已有 AirPlay 接收能力，因此不能简单说完全没有基础；但正式文档主要承诺 iPhone/iPad，未找到 macOS 发射端的兼容矩阵或验收。应明确 macOS 版本后测试 Mac→Windows AirPlay；USB iPhone 路径不能作为 Mac 有线镜像支持证据。 |

## 确认的漏修：#31

当前 [DriverConstants.cs 第 44 行](https://github.com/RayrenSX/iPhoneMirror/blob/76e962f37f55a05e4a7b4c86235338957d879c9f/src/DriverInstaller/Services/DriverConstants.cs#L44) 使用：

```csharp
[GeneratedRegex(@"^USB\\VID_05AC&PID_[0-9A-Fa-f]{4}\\[A-Za-z0-9][A-Za-z0-9-]{6,38}$",
    RegexOptions.CultureInvariant)]
```

`IsValidSerial` 接受 40 位，但 `IsAppleMobileCaptureParent` 会先进入 `IsAllowedAppleParent`，在枚举阶段拒绝长度 40 的实例名。`DeviceCatalog.GetAppleDevices()` 因此直接跳过该设备，后面的 C++ 发现逻辑和核心层 40 位序列号测试无法补救。

最小复现使用三份真实源码分别生成 C# 正则并调用真实校验方法，未接触设备、注册表或驱动：

| 源码版本 | 24 位 | 39 位 | 40 位 | `IsValidSerial(40位)` |
|---|---|---|---|---|
| v1.8.3 | 通过 | 通过 | **拒绝** | 通过 |
| main `76e962f` | 通过 | 通过 | **拒绝** | 通过 |
| 当前工作区 | 通过 | 通过 | **拒绝** | 通过 |

程序与结果：[UdidRepro.csproj](../outputs-next/github-issue-audit-2026-10-01/udid-repro/UdidRepro.csproj)、[udid-repro.log](../outputs-next/github-issue-audit-2026-10-01/udid-repro.log)。退出码 1 是三条 40 位预期失败的结果，不是编译错误。下一步应统一父设备实例名与序列号的长度规则，并补驱动枚举/提权校验层的回归测试。

## 修复提交与发布情况

- [PR #28](https://github.com/RayrenSX/iPhoneMirror/pull/28) **关闭但未直接合并**。不能因 PR 关闭认定修复未进入 main，也不能把它计作已合并：已在当前源码确认其核心做法，即在 `_sendLock` 内生成最终帧序号并序列化，相关改动经 [PR #36](https://github.com/RayrenSX/iPhoneMirror/pull/36) 合入。
- [PR #36 / `0e26a3f`](https://github.com/RayrenSX/iPhoneMirror/commit/0e26a3f6b5079001d8920e552c329b2e6c97013e) 已合并，包含控制生命周期、录制、剪贴板、轻量模式等工作，但早于它发布的 v1.8.3 不包含这些新增实现。
- [`76e962f`](https://github.com/RayrenSX/iPhoneMirror/commit/76e962f37f55a05e4a7b4c86235338957d879c9f) 已包含热点多接口策略及后续反控/媒体流程改善。对应 [GitHub Windows CI](https://github.com/RayrenSX/iPhoneMirror/actions/runs/36554871309) 状态为成功。CI 的构建/测试/发布目录生成不等于创建了 GitHub Release。
- #24 还存在本地未提交的恢复及焦点修改。参考 [WIRED_RECOVERY_REAUDIT.md](WIRED_RECOVERY_REAUDIT.md) 和 [KEYBOARD-FOCUS-CONTROL.md](KEYBOARD-FOCUS-CONTROL.md)。这些历史本地记录作为辅助材料，不替代本次验证，也没有升级为 GitHub 已发布结论。
- 24 条 timeline 中没有检出 `referenced`、`cross-referenced` 或 `connected` 事件。上述 issue→实现关系来自正文、PR 内容、Git 历史和代码比对，**不是伪称 GitHub 已建立自动关闭链接**。

## 本次实际执行的验证

| 验证 | 结果 | 证据范围 |
|---|---|---|
| `dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj -c Release --no-restore` | 通过 | 现有驱动测试，包括 PID 白名单；现有套件没有拦住 #31 的漏修。 |
| `dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release --no-restore` | 通过 | 当前工作区逻辑测试；含键盘路由、录制调度/PCM、剪贴板状态等。 |
| `dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-restore` | 通过 | 默认运行时套件；含 BLE 超时/取消/晚到回调/释放、Ctrl+V 状态和状态窗生命周期。未向蓝牙设备发送输入。 |
| `python tests/usb_touch_logic_test.py` | **90 项通过** | 当前工作区 USB 桥逻辑测试。 |
| `dotnet run --project outputs-next/github-issue-audit-2026-10-01/udid-repro/UdidRepro.csproj -c Release` | **3 条预期失败，确认 #31** | v1.8.3、main、工作区的真实父设备校验方法均拒绝 40 位 UDID。 |
| GitHub main Windows CI | 成功 | SHA 与审查基线一致；本次查询结果，非本次重新执行 native 全套测试。 |

日志位于取数与验证目录。没有以历史报告中的 71/120 项数字冒充本次运行结果，也没有把非实机测试推广为全部设备/网络/游戏场景通过。

## 对上次报告的纠正与增量

1. 2026-09-23 报告为 21 条，本次新增 #37、#38、#39，合计 24 条。
2. #31 从“核心测试包含 40 位，待实机”纠正为**驱动层仍有确定性漏修**。
3. #11 从“源码不能证明修复”更新为**PID 白名单、发布说明和驱动测试均提供实现证据**。
4. #19 从“网络环境待验证”更新为**main 已加入专门的多接口发布修复**，仍缺发布及手机端验证。
5. #23 从“更新服务/网络”纠正为**DDI 开发者镜像下载及 GitHub 校验/API 限流**。
6. #32 已确认 Windows→手机的真实文本写入路径，不能只用手机→Windows 轮询作为证据；不额外把用户单向粘贴需求扩大成必须双向同步才算完成。
7. #30 核查的是当前实际异步绑定及 `ControlStatus.RequestPromptAsync` 路径；仅存在一个 `ShowAsync` 方法不足以证明它仍被启动流程调用。

## 建议处理顺序

1. **优先修 #31**：原因明确、影响旧机型驱动安装，并已有不依赖实机的稳定复现。
2. **发布前验收 #24、#30、#35**：反控长时间运行、窗口失焦/恢复、取消/快速启停、全屏退出；将 main 和需要纳入的本地修复整理为明确候选版本。
3. **恢复 #15、#21、#25 的问题跟踪，并与 #38 建立媒体专项验证**：区分驱动状态、游戏开麦、iPad 录制卡顿、音画偏移，保留设备/版本/分辨率/编码器及录制时长。
4. **对已有功能完成发布与验收 #19、#32、#34**；#10 只需按已约定的提示/降级范围完成目标应用确认。
5. **继续实现 #26、补全 #29；答复 #37/#39**。蓝牙性能与远程桌面问题 #14/#16/#17/#18 需要明确环境与量化结果；#23/#27 需要准确的网络及兼容性说明。

本报告建议的是后续维护动作，未代替维护者批量关闭、重开或回复 issues。
