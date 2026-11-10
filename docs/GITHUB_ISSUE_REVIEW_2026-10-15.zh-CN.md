# 近期 GitHub issue 逐项代码审查报告（2026-10-15）

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；历史测试结论未在本次重新执行。

> 本文件保留修复前的审查基线。后续源码修改、复测及尚待实机验证的条目见 [源码修复记录](GITHUB_ISSUE_FIXES_2026-10-15.zh-CN.md)。

审查结论：**没有足够证据把近期故障直接归为用户误操作。** 确定存在项目侧代码/交付问题，另外有新增功能、支持范围限制和需要设备日志继续确认的故障。GitHub 的“已关闭”不能作为“已修复”统计。

本次完成文档和只读审查，没有修改产品源码、提交补丁、评论或关闭 GitHub issue。保留工作区原有修改。报告将“用户报告”“源码可证”“本次测试”“待实机确认”区分记录，不将候选修复写成已交付。

## 范围与版本

- 仓库：[RayrenSX/iPhoneMirror](https://github.com/RayrenSX/iPhoneMirror)。以客户端日期 2026-10-15、Asia/Shanghai 为审查日期，取数完成时间见原始快照文件时间。
- “近期”定义为 **2026-10-01 00:00（UTC+8）以来新增或更新**：28 条，16 条新建、12 条旧条目更新。全库 40 条实际 issue，7 Open、33 Closed，REST 与 GraphQL 数量一致，排除 PR。近期 6 Open、22 Closed；另一个开放历史条目为 #10。
- 已完整核对近期 28 条正文、全部评论与 timeline，逐条核对评论数量相等。附件可下载的文本日志和 #52 ZIP 均已取得；没有依赖未查看的截图来判断根因。
- 本地 HEAD 与 GitHub main 均为 `829c5919f25718efc91f3fae4ee1e808f193964d`。当前存在大量原有未提交修改，关键源码 SHA-256 已记录。
- GitHub 最新预览是 [v1.8.5-pre2](https://github.com/RayrenSX/iPhoneMirror/releases/tag/v1.8.5-pre2)，2026-10-09 发布，tag 指向 `5a260d7e25e040481959ff86913341e7db24b97a`；上一预览 v1.8.5-pre 为 `54c36cad597e995dbfb3999de42287082d22cce9`；最新非预发布仍是 [v1.8.3](https://github.com/RayrenSX/iPhoneMirror/releases/tag/v1.8.3)。部分报告日志记录 `fb8583b`，这是报告所述构建标识，不能覆盖独立核对的 release tag。
- 多条关闭评论写“1.5.0-pre”，与公开 Releases 不符；报告按实际标签判断，建议更正版本。10 月 1 日审计是历史基线，#26、#29、#31 等不能继续照抄“未实现/未修”。

## 优先处理的结论

1. **#58 USB 投屏蓝屏，P1。** 正常操作触发内核崩溃，优先调查 USB 驱动栈。现有摘要没有故障栈，不能仅凭 dump 包含 libusb0/UsbDk/Apple 驱动就指认模块。保守模式仍可能走 libusb0 回退，不能承诺避免 BSOD。先分析已有 dump 和日志，AirPlay 是当前规避路径。
2. **#59 自动熄屏失效，P1。** 原始日志最后一次复现显示反控先停止，随后 QuickTime 停止消息、两路释放、最终 HPD0 和正常 USB 栈恢复完成。不能再直接推断“没清理 USB”。该会话使用 Demo/Valeria，手机是 iOS beta，需通过 Demo/非 Demo、仅投屏/加反控分组找因；退出成功不证明手机空闲状态恢复。
3. **#55 就绪误判，P1，项目侧缺陷明确。** 商店 Apple Devices 提供可用 USBMux，但传统 AMDS 未 Running，发布版仍判失败。本地已加入真实 ListDevices 探测与端口冲突提示，驱动测试通过；候选代码尚未发布。一次提权 -1 缺结果应单独保留，不伪造原因。
4. **#52 新设备兼容，P1。** USB 描述符可见不等于配置已生效，发布代码对中间态存在处理缺口；iOS 27 DDI 需要独立适配。报告人成功的是累计开发补丁，不能把“多等 5 秒”写成已证实全部根因。当前本地有最小等待修正及 Cryptex 准备，模拟测试通过，最终包与目标手机仍待验收。
5. **#44 失焦泄漏及 #24/#30 稳定性，P1。** 切窗、启用控制属于正常工作流。源码已有焦点门控、发送锁内序号与生命周期改善，仍需原设备的输入隔离、长时断联和 UI 响应验证。
6. **#46 游戏开麦冻结，P1。** 与历史 #15 同族，报告人自行关闭、维护者回复“会检查”不足以证明解决。分别采集手机音频会话变化、USB/无线源、视频样本与 UI 状态。
7. **#54 分辨率与 #56 同录同播，P2。** 都是实际产品能力/策略限制。前者有面板映射缺口且用户填错部分型号规格；后者明确被 UI 与服务端单输出模型限制。不能用修改设置或去掉按钮禁用来宣称支持。

## 分类口径

下列分类互斥，描述的是本次证据能支持的处理方向；“根因待证”包含已有相关代码修正但缺少报告场景闭环的情况，不表示问题不存在。

| 分类 | 数量 | Issue |
|---|---:|---|
| 根因待证 | 11 | #59、#58、#57、#49、#46、#43、#38、#35、#30、#24、#19 |
| 功能或支持范围 | 9 | #56、#54、#51、#47、#39、#34、#32、#29、#26 |
| 代码或交付问题 | 5 | #55、#52、#48、#44、#31 |
| 平台音频路由 | 1 | #53 |
| 前置条件及引导 | 1 | #45 |
| 资料请求 | 1 | #37 |

没有一条能够以现有证据认定为“单纯用户误操作”。#45 涉及真实前置条件与错误引导，仍不能将投屏失败全部归于开发者模式。#53 主要是设备音频路由边界，也不是已修复或用户错误。

## 28 条逐项审查索引

每条文档包含：原反馈与评论/关闭依据、代码逻辑与精确位置、版本归属、操作判断、处置建议、验收条件及本次验证边界。优先级是审查建议，不是对用户影响的完整统计。

| Issue | 当前状态 | 归因 | 优先级 | 审查摘要 |
|---|---|---|---|---|
| [#59 结束 USB 投屏后不能自动熄屏](issues/audit-2026-10-15/59.zh-CN.md) | OPEN | 根因待证 | P1 | 不能归为操作错误，也没有证据确认“反控进程残留”或“USB 未恢复”。最后一次复现的停止链路成功；优先调查 Demo/Valeria 显示会话在 iOS 27.2 beta3 上释放后的状态。Windows Insider 29683.1000 是兼容性变量，不能单凭预览系统归责。 |
| [#58 开始 USB 投屏触发 Windows 蓝屏](issues/audit-2026-10-15/58.zh-CN.md) | OPEN | 根因待证 | P1 | 应按 USB 驱动栈兼容性事故处理。正常点击开始投屏即蓝屏不属于操作错误。进程名 iPhoneMirror.exe 或驱动出现在 dump 模块列表，都不能证明该进程/驱动就是故障根因。 |
| [#57 AirPlay 列表找不到接收器](issues/audit-2026-10-15/57.zh-CN.md) | CLOSED | 根因待证 | P2 | 现有信息不足以区分网络环境、接收器未启动、广播接口、防火墙或旧版缺陷。不能因为使用 v1.8.3 就认定是用户操作错误；仅建议升级后关闭，缺少成功确认。 |
| [#56 同时录制和直播](issues/audit-2026-10-15/56.zh-CN.md) | OPEN | 功能或支持范围 | P2 | 当前产品确实不支持两个内置媒体输出并行，是功能缺口。不是用户没有找到正确按钮。 |
| [#55 Apple Devices 已可通信却被判定支持未就绪](issues/audit-2026-10-15/55.zh-CN.md) | OPEN | 代码或交付问题 | P1 | 主要是准备流程兼容性/诊断缺陷。发布版把传统 Apple 服务 Running 当作必要条件，未认可商店版已可工作的 USBMux 服务；不是用户必须卸载 Apple Devices。另一次提权进程 -1 是独立未定故障。 |
| [#54 iPad 原生分辨率与固定 DisplaySize](issues/audit-2026-10-15/54.zh-CN.md) | OPEN | 功能或支持范围 | P2 | 存在型号映射/协商策略限制，属于产品侧兼容与画质能力缺口。用户填写的“12.9 英寸 2360×1640”规格也有误，应先核对 ProductType；规格写错不否定 1600×1200 的实际反馈。 |
| [#53 投屏时继续在 AirPods 播放手机声音](issues/audit-2026-10-15/53.zh-CN.md) | CLOSED | 平台音频路由 | P2 | 以 iOS 投屏音频路由限制为主要方向，用户切换 AirPods 是正常操作。但“iOS 限制”是维护者结论，本次没有独立设备矩阵证明全部系统均无法实现，不能写成已修复。 |
| [#52 iPhone19,2 的 USB 配置转换与 iOS 27 DDI](issues/audit-2026-10-15/52.zh-CN.md) | CLOSED | 代码或交付问题 | P1 | 是项目侧设备/系统兼容缺口，已有两条独立问题链，不应继续笼统归因驱动没装好。DDI 方向有报告人的实机成功证据；USB 成功来自累计修改，不能认定延长等待就是唯一根因或完整修复。 |
| [#51 直播自定义图层](issues/audit-2026-10-15/51.zh-CN.md) | CLOSED | 功能或支持范围 | P3 | 属于新增功能请求；维护者明确不计划实现，建议 OBS。不属于用户操作问题，也不是已修复的 bug。 |
| [#49 反复弹驱动管理器及 Windows 10 清理失败](issues/audit-2026-10-15/49.zh-CN.md) | CLOSED | 根因待证 | P2 | 原始 USB 投屏失败信息不足；后续 pnputil Driver Store XML 枚举失败是独立的工具兼容性问题，不能把二者混为“用户没装 Apple 驱动”。 |
| [#48 UxPlay 下载指向已删除的 test45 资源](issues/audit-2026-10-15/48.zh-CN.md) | CLOSED | 代码或交付问题 | P2 | 确定是历史测试构建引用不存在组件资产的交付问题；404 不是用户网络配置的充分证据。维护者确认该测试组件已删除。 |
| [#47 提供 macOS 运行版本](issues/audit-2026-10-15/47.zh-CN.md) | OPEN | 功能或支持范围 | P3 | 当前是 Windows 产品，macOS 版本属于移植需求，不能归为用户系统装错或现有 Windows bug。 |
| [#46 游戏开麦后投屏冻结](issues/audit-2026-10-15/46.zh-CN.md) | CLOSED | 根因待证 | P1 | 与旧 #15 的游戏开麦反馈同类，值得作为持续兼容性故障追踪，不能把用户正常开麦视为误操作。报告人关闭不构成修复验证。 |
| [#45 握手超时、开发者模式与 GetProhibited 提示](issues/audit-2026-10-15/45.zh-CN.md) | CLOSED | 前置条件及引导 | P2 | 必须拆开投屏与反控：HID/DDI 的开发者模式/配对是前置条件；普通 USB QuickTime 投屏不应被本条单次观察概括为“iOS 27 一律需开发者模式”。错误引导有产品责任。 |
| [#44 失焦后仍向手机发送键盘输入](issues/audit-2026-10-15/44.zh-CN.md) | CLOSED | 代码或交付问题 | P1 | 报告描述的是输入焦点/所有权缺陷；用户切到别的软件属于正常操作。对应焦点门控已经实现，仍需保留实际场景验收。 |
| [#43 iPhone 13 USB 无法继续投屏，信息不足](issues/audit-2026-10-15/43.zh-CN.md) | CLOSED | 根因待证 | P2 | 无法定责。仅有版本、手机和“投屏未能继续”，缺少实际结果、日志、错误阶段与任何成功确认。 |
| [#39 把 Mac 桌面投到 Windows](issues/audit-2026-10-15/39.zh-CN.md) | CLOSED | 功能或支持范围 | P3 | 这是 macOS 发射端到 Windows 接收端的支持请求，与 #47 的“应用在 Mac 上运行”不同。当前未承诺，不是用户操作问题。 |
| [#38 录屏声音落后视频](issues/audit-2026-10-15/38.zh-CN.md) | CLOSED | 根因待证 | P2 | 应归为录制同步问题待验收，不是用户保存或播放操作错误。已有具体时基修正，仍缺少原报告设备前后样本。 |
| [#37 请求特定 iPad 的现有 Valeria trace](issues/audit-2026-10-15/37.zh-CN.md) | CLOSED | 资料请求 | P3 | 属于证据请求，无代码故障或用户误操作可归责。实现流程图不能替代 iPad17,4 / PID 12AB / iPadOS 26.7 的实测抓取。 |
| [#35 全屏后退出键无反应](issues/audit-2026-10-15/35.zh-CN.md) | CLOSED | 根因待证 | P2 | 是窗口输入路由/全屏退出问题方向；按 Escape/F11 退出全屏属于正常操作。存在针对性实现，但“彻底解决”超出当前证据。 |
| [#34 停止录制后丢弃文件](issues/audit-2026-10-15/34.zh-CN.md) | CLOSED | 功能或支持范围 | P3 | 原始需求是产品缺少丢弃入口，当前已有实现。不是用户不会删除文件。 |
| [#32 Windows 文本粘贴到手机](issues/audit-2026-10-15/32.zh-CN.md) | CLOSED | 功能或支持范围 | P2 | 原始需求已经有 USB/无线文本管线。属于新增能力交付，不应将旧版无此功能认定为用户剪贴板操作错误。 |
| [#31 40 位旧机 UDID 被正则过滤](issues/audit-2026-10-15/31.zh-CN.md) | CLOSED | 代码或交付问题 | P2 | 可直接定位的代码缺陷，已在发布预览代码修正。用户使用旧机或合法 40 位序列号没有操作错误。 |
| [#30 开启蓝牙控制后卡死/崩溃](issues/audit-2026-10-15/30.zh-CN.md) | CLOSED | 根因待证 | P1 | 多位用户复述同类行为，属于产品稳定性/蓝牙兼容问题方向，不能归为点错按钮。源码有针对性生命周期改进，但缺少该场景崩溃栈与复测闭环。 |
| [#29 快捷方式直接打开精简独立预览](issues/audit-2026-10-15/29.zh-CN.md) | CLOSED | 功能或支持范围 | P3 | 原始功能诉求已存在完整启动入口；托盘模式只是相关能力，不能仅以“托盘已做”替代一键启动核查。 |
| [#26 直播添加电脑麦克风](issues/audit-2026-10-15/26.zh-CN.md) | CLOSED | 功能或支持范围 | P2 | 需求已经实现于预览版，应更新旧审计结论；不是用户没找对手机麦克风权限。 |
| [#24 有线反控断开，失焦回来易失效](issues/audit-2026-10-15/24.zh-CN.md) | CLOSED | 根因待证 | P1 | 存在真实控制生命周期问题与序号/焦点处理风险，不能把正常切后台判为错误操作。已有多项代码改善，仍无法据此承诺所有断联彻底解决。 |
| [#19 Windows 移动热点 AirPlay 发现](issues/audit-2026-10-15/19.zh-CN.md) | CLOSED | 根因待证 | P2 | 原发现层单接口策略有明确代码依据，属于软件兼容性问题方向；用户把手机连 Windows 热点是合理网络拓扑。当前已改变策略，但不能把电脑侧注册成功当作手机连接成功。 |

## 关闭状态审查

- #52 曾关闭、重开，最后由**报告人**随累计修改成功反馈关闭。不是 main 合并了最小修复的证据，也不是无线/长时/安装包全部通过的证据。
- #57 只建议升级后由维护者关闭，缺少升级结果；#43 由报告人自行关闭且无评论；#46 由报告人关闭，维护者只说会检查；#49 的清理错误出现于关闭之后，未见原投屏成功确认。这些应按“未闭环”记录。
- #51、#53、#39、#48 的关闭主要属于不计划、范围限制或旧测试版弃用，不能计作 bug 修复。
- #19、#24、#30、#35、#38 有代码改进及维护者关闭说明，但原场景验收不足。#26、#29、#31、#32、#34 的实现与发布预览依据比历史审计更充分；仍分别保留场景/最终包验收边界。
- #37 给出实现图但没有所请求设备的现有 trace；这属于资料答复完整性问题。

## 本次实际验证

完整命令输出位于 validation（本地记录：`outputs-next/issue-audit-2026-10-15/validation`）。下列是本次重新执行的结果，不沿用历史报告的测试次数。

| 验证 | 结果 | 说明 |
|---|---|---|
| Python unittest discover（构建专用 Python） | 425 项通过，27.196 秒 | 含 DDI/Cryptex、HID/USBMux 生命周期、剪贴板与恢复模拟；日志中的故障注入警告不是实机故障 |
| Release 原生构建 + CTest | 17/17 通过，22.16 秒 | 含真实 open_quicktime 的假 libusb API 激活测试、配置恢复策略、DNS-SD 策略、无线/虚拟摄像头 smoke |
| DriverInstaller.Tests 重新构建/执行 | 通过，构建 0 警告/错误 | 包含 AppleMuxSupportTests、UDID/设备验证和模拟恢复；未安装/删除驱动 |
| App.Logic.Tests 重新构建/执行 | 总进程退出 0，输出 App logic tests passed | 输出还包含两次 `dotnet--hide_banner` 子进程诊断，不能称所有子进程验证均干净通过；保留原日志 |
| App.Runtime.Tests 重新构建 | 通过，0 警告/错误 | WPF 专项实际结果见下方 |
| 驱动清理安全测试 | 通过 | 只载入定义与模拟，未执行真实清理 |
| 清理本地化测试 | 1456 例通过 | 未执行清理 |
| #52 附件 MANIFEST 完整性 | 5/5 长度与 SHA-256 一致 | 只检查累计补丁，没有整体应用，也未运行附件里的命令 |
| WPF `--keyboard-focus` | 通过，退出 0 | 日志（本地记录：`outputs-next/issue-audit-2026-10-15/validation/keyboard-focus.log`） |
| WPF `--media-review` | 通过，退出 0 | 日志（本地记录：`outputs-next/issue-audit-2026-10-15/validation/media-review.log`） |
| WPF `--ddi-startup` | 通过，退出 0 | 日志（本地记录：`outputs-next/issue-audit-2026-10-15/validation/ddi-startup.log`） |
| WPF `--capture-review` | 通过，退出 0 | 日志（本地记录：`outputs-next/issue-audit-2026-10-15/validation/capture-review.log`） |

运行时专项首次调用遗漏 `win-x64` 输出目录，四次均未执行到测试代码；已修正路径重新运行，首次错误日志另存 `*-initial-path-error.log`，不计为产品测试失败或通过。专项最终结果以修正后日志为准。

本次未重现 BSOD，未更改手机/服务/驱动配置，未做游戏、AirPods、热点、iPhone19,2、自动熄屏实机测试，也未制作或验收最终 ZIP/Setup。因此不能输出“近期 issues 已全部修好”。

## 建议执行顺序

| 顺序 | 工作 | 完成标准 |
|---|---|---|
| 1 | #58 分析已有内核 dump，#59 四组退出/熄屏对照 | 找到故障模块或明确手机端残留状态，避免无证据重装循环 |
| 2 | 最小审查并发布 #55、#52 候选修复 | 代码评审、目标设备验收、旧设备回归、ZIP/Setup 分别验证 |
| 3 | #44 输入隔离与 #24/#30 控制稳定性、#46 游戏开麦 | 记录复现组合、失败率、恢复时间和线程/协议证据 |
| 4 | #54 画质策略、#56 同录同播产品实现 | 功能边界清楚，实际输出能力与 UI 一致 |
| 5 | 修正文案与关闭依据 | 校正 1.5.0-pre，补“已实现/已发布/已验收”的区别，资料请求明确是否有匹配记录 |

## 其余 12 条历史 issue 索引（本轮不重复深审）

以下没有在选定近期窗口更新，正文已取数，用于避免遗漏全库条目；不冒充本轮逐代码复审。历史分析参考 [2026-10-01 审计](GITHUB_ISSUE_AUDIT_2026-10-01.md)，其结论仅适用于当时版本。

| Issue | 状态 | 最后更新时间（UTC+8） |
|---|---|---|
| [#27 [Feature]: ios兼容性](https://github.com/RayrenSX/iPhoneMirror/issues/27) | CLOSED | 2026-09-16 07:37 |
| [#25 [Feature]: 录屏有问题](https://github.com/RayrenSX/iPhoneMirror/issues/25) | CLOSED | 2026-09-19 15:27 |
| [#23 [Bug]: 手机无法自动拉取下载github的信息](https://github.com/RayrenSX/iPhoneMirror/issues/23) | CLOSED | 2026-09-12 15:05 |
| [#21 [Bug]: ios mirror probelm  via usb ios 26 driver stack problem.](https://github.com/RayrenSX/iPhoneMirror/issues/21) | CLOSED | 2026-09-11 22:34 |
| [#18 [Bug]: 电脑控制手机延迟问题](https://github.com/RayrenSX/iPhoneMirror/issues/18) | CLOSED | 2026-09-10 00:57 |
| [#17 [Feature]: 蓝牙控制延迟过高](https://github.com/RayrenSX/iPhoneMirror/issues/17) | CLOSED | 2026-09-08 17:57 |
| [#16 BLE HID reverse control captures/hides system mouse pointer, unable to control phone, only Win key escapes, possible app freeze](https://github.com/RayrenSX/iPhoneMirror/issues/16) | CLOSED | 2026-09-26 14:46 |
| [#15 [Bug]: iPhoneMirror + AirPlay + BGMI mic: ✅ iPhoneMirror + USB + BGMI mic: ❌ screen flickering + audio breaking](https://github.com/RayrenSX/iPhoneMirror/issues/15) | CLOSED | 2026-09-11 15:14 |
| [#14 [Feature]: 想在家使用远程软件控制办公室的手机](https://github.com/RayrenSX/iPhoneMirror/issues/14) | CLOSED | 2026-09-04 22:31 |
| [#12 [Feature]: usb反控功能](https://github.com/RayrenSX/iPhoneMirror/issues/12) | CLOSED | 2026-09-18 16:43 |
| [#11 [Bug]: 苹果USB-C转3.5mm耳机接口转接线会被识别为iPhone26,50](https://github.com/RayrenSX/iPhoneMirror/issues/11) | CLOSED | 2026-08-31 00:29 |
| [#10 Support / fallback handling for DRM-protected video playback (e.g., beIN CONNECT black screen)](https://github.com/RayrenSX/iPhoneMirror/issues/10) | OPEN | 2026-08-25 13:06 |

## 证据文件

- 完整取数与验证目录（本地记录：`outputs-next/issue-audit-2026-10-15`）：`issues.json`、`issues-rest.json`、`recent-issues.json`、28 条 timeline、Release/tag/main 快照、附件和测试日志。
- 结构化逐项结论（本地记录：`outputs-next/issue-audit-2026-10-15/review-results.json`）：分类、置信度、优先级、代码位置、处置与验收字段，可继续用于排期。
- 关键源码 SHA-256（本地记录：`outputs-next/issue-audit-2026-10-15/source-hashes.json`） 与 `source-snapshots/`：固定本次审查基线，防止之后的工作区修改混淆结论。
- #59 脱敏关键时间线（本地记录：`outputs-next/issue-audit-2026-10-15/issue59-evidence.md`）：包含原始文件行号；下载的 capture.log 是多会话累计文件。

证据等级：源码可证的分支与限制为高；用户实机反馈作为该用户环境的证据，不自动外推所有设备；没有日志、堆栈或变量对照的根因推断保留为待证。纯新增功能不归为 bug；正常支持流程失败不归为误操作。
