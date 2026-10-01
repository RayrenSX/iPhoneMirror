# 多语言显示复查（2026-10-01）

本轮以当前工作区为基线，保留已有改动，复查简体中文（zh-CN）、香港繁体中文（zh-HK）和英文（en-US）。历史全量文案审计见 [LOCALIZATION-AUDIT.md](LOCALIZATION-AUDIT.md)。本轮证据位于 `outputs-next/localization-recheck/`。

## 续检收尾（2026-10-01 12:08 构建）

已接续中断点完成复查。接手时清理脚本的规范化 SHA-256 已与宿主一致；随后修订清理文案，再次同步校验值并重新构建。当前主程序每种语言 **1123 项**，驱动管理器 **196 项**，清理脚本 **91 项**，安装器 **296 项**。主程序的字体资源另计。

本次补充修复：

- **清理说明与实际行为一致**：三语入口、标题、说明及启动错误不再声称“强制清除所有驱动”。清理会保留其他设备在用的驱动包；无法验证驱动包使用情况时只移除设备节点。控制台范围提示同步修正，保留可能涉及 Apple 官方驱动及修改前确认的说明。只修改文案和完整性校验值，未改变清理策略。
- **新增香港繁体文案统一**：等待安全停止、部分驱动清单、重连验证失败及刷新失败采用现有的“驅動程式”“過濾器”“記錄”等用词；清理脚本中新增的共享驱动包及 Windows 能力提示也统一为“驅動程式套件”。
- **补足新增提示的实际显示覆盖**：简单和高级模式分别显示等待停止及刷新失败状态，保留原操作结果、原始错误及日志路径；另覆盖重连失败、帮助网站打开失败和父驱动列表部分失败提示。均使用模拟数据，未启动真实驱动操作。

| 本次检查 | 结果 | 证据（相对于 `outputs-next/localization-recheck/`） |
| --- | --- | --- |
| 静态资源、引用、占位符、安装器与清理脚本哈希 | 370 个源码文件，零错误、零警告 | `resumed-resource-check.json`；同时更新 `latest-resource-check.json` |
| PowerShell 资源检查 | 三语键一致，缺失键为零 | `resumed-powershell-verifier.log` |
| 主程序实际编译字典 | 17968 个格式化用例、4 次字典切换、12 个控制流程通过；换行及空格与源码一致 | `resumed-final-app-localization.log` |
| 驱动管理器实际编译字典 | 2352 个格式化用例、3 次切换、45 个操作结果通过；换行及空格与源码一致 | `resumed-final-driver-localization.log` |
| 已打开窗口切换语言 | 30 类界面、90 次切换、2417 项断言；零失败、零绑定诊断 | `resumed-final-live-switch.log`、`resumed-final-live-switch/language-display-results.json` |
| 驱动管理器三语及深浅主题布局 | 96 个场景；零失败、零布局问题、零绑定诊断 | `resumed-final-driver-ui.log`、`resumed-final-driver-ui/driver-results.json` |
| 清理脚本文案 | 1092 个格式化用例通过 | `resumed-cleanup-localization.log` |
| 清理脚本定义测试 | 共享包、使用者变化、蓝牙设备、Windows 能力及进程保留等测试通过；操作系统调用均为测试替身 | `resumed-cleanup-safety.log` |
| 主程序、运行时测试及驱动管理器构建 | 零警告、零错误 | `resumed-final-runner-build.log`、`resumed-final-driver-build.log` |
| 工作区差异检查 | 通过 | `resumed-diff-check.log` |

抽查了最终构建的中英文失败帮助窗口、英文刷新失败状态和香港繁体重连失败提示截图。长说明位于可滚动区域，关闭及重新检测按钮保留在固定操作区。

本次构建使用 `resumed-final-runner/` 和 `resumed-final-driver/`。构建前记录的 383 个源码及相关输入文件见 `resumed-final-source-before-build.json`；构建后及测试期间复核未发生变化。三项二进制 SHA-256 见 `resumed-final-binaries.json`。原中断时未通过的资源检查保存为 `resource-check-before-resume.json`。

本次 96 个驱动界面场景和 90 次语言切换对应上述构建。下文的 210 个主程序布局场景及完整运行时、逻辑测试属于此前构建的历史验证，不计作本次重跑结果。没有连接实体手机执行端到端操作，也没有安装、卸载或清理驱动。

## 前轮修复（历史记录）

- **停止采集的错误回退**：`NativeCore.StopDeviceSession` 原来直接读取带 `{0}` 的模板；改为格式化错误码，避免把占位符显示给用户。
- **蓝牙状态漏译**：蓝牙服务的启动、广播、客户端连接、通知停滞、恢复、断开和 HID 报告名称原来直接输出英文。新增 29 个三语资源，状态及错误通过原有通知路径刷新；Windows 返回的枚举值和原始诊断保留。
- **USB 桥接异常**：输出通道意外关闭的事件原来硬编码中文。新增三语文案，并区分“意外关闭”和原有的“就绪前关闭”，使已连接后断开的描述也准确。
- **组合文案保留旧语言**：无线设置确认、画面设置失败、蓝牙状态与错误、USB 桥接启动异常改用能保留资源来源的组合方法。切换语言后，组成部分同步刷新，设备名、错误码和原始诊断保持原样。
- **USB 模式说明不刷新**：已打开的说明窗口现在同步刷新模式名称、优点、缺点和注意事项。
- **开发者与更新预览**：开发者窗口的状态和诊断立即刷新；更新预览的标题及正文跟随语言切换。只有正文实际变化时才重建文档，普通发布内容不会因为切换语言而被翻译或重置。
- **受保护内容覆盖层**：音频状态文本在切换语言及收到缓存更新时重新本地化。
- **文案空格**：清除 12 项中繁文案中“关闭 有线控制”“影片 投放”等多余空格。
- **编译后空白保留**：在工作区已有的换行保留修复基础上，为 8 条包含有意连续空格或首尾空格的资源补齐 `xml:space="preserve"`。主程序和驱动管理器均已通过源码与实际编译字典逐字比较，包含换行和空格。
- **新增父驱动管理的香港繁体文案**：统一使用“螢幕鏡像”“綁定”“過濾器”“資料”，与现有界面保持一致；覆盖实际管理窗口、长确认弹窗及全部新增操作结果。

主程序每种语言由 1092 增至 **1123 个字符串**（本轮新增 31 项）；字体资源另计。驱动管理器每种语言为 **190 项**，包含其他并行工作新增的 26 项父驱动管理资源；清理脚本为 88 项，安装器有效资源为 296 项。

## 前轮显示与回归验证（历史记录）

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 资源、占位符、文件过滤器、引用、安装器资源和清理脚本哈希 | 零错误、零警告；文件数量见 JSON | `latest-resource-check.json` |
| PowerShell 资源检查器 | 三语键一致，缺失键为零 | `powershell-verifier.log` |
| 主程序实际 WPF 字典 | 源码与编译文本逐字一致；17968 个格式化用例、4 次字典切换、12 个控制流程通过 | `final-app-localization.log` |
| 驱动管理器实际 WPF 字典 | 源码与编译文本逐字一致；2280 个格式化用例、3 次切换、45 个操作结果通过 | `latest-driver-localization.log` |
| 已打开窗口切换语言 | 30 类界面、90 次切换、2417 项断言；零失败、零绑定诊断，包含更新预览正文 | `final-live-switch.log`、`final-live-switch/language-display-results.json` |
| 主程序三语及深浅主题布局 | 210 个界面通过，每种语言 70 个；覆盖多档窗口尺寸、缩放、页签和长文案；零布局问题、零绑定诊断 | `final-ui-cn/`、`final-ui-hk/`、`final-ui-en/` 的 `runtime-results.json` 及同名 `.log` |
| 驱动管理器三语及深浅主题布局 | 60 个界面通过，包含父驱动管理和实际确认文案；零布局问题、零绑定诊断 | `latest-driver-ui.log`、`latest-driver-ui/driver-results.json` |
| 清理脚本文案 | 1056 个格式化用例通过；未执行清理 | `cleanup-tests.log` |
| 静态检查器反例 | 能拒绝未格式化模板及硬编码蓝牙状态；未修改被检查文件 | `scanner-regression.log` |
| 主程序与运行时测试最终构建 | 成功；零警告、零错误 | `driver-final-runner-build.log` |
| 驱动管理器最终构建 | 成功；零警告、零错误 | `driver-final-build.log` |
| 驱动与主程序逻辑测试 | 通过；驱动测试仅枚举现有设备，未执行驱动变更 | `latest-driver-tests.log`、`latest-logic-tests.log` |
| 完整主程序运行时套件 | 最终构建通过，包含实际窗口倒计时、主题、工作区动画、原生预览及蓝牙异常路径 | `final-runtime-tests.log` |
| 工作区差异检查 | 通过；Git 行尾转换提醒不属于差异错误 | `diff-check.log` |

窗口切换测试还检查了所有控制阶段、嵌套格式化、组合异常、待确认选项、原始设备名称、启动字典缺失回退及音频覆盖层。切换语言不得改变输入内容、选中项、待确认任务或自动关闭时间。

最终构建合计完成 **270 个界面场景**及 **90 次已打开窗口的语言切换**。人工抽查了英文更新预览、香港繁体 USB 模式说明、主按钮、英文父驱动确认弹窗和香港繁体父驱动管理截图；长内容可滚动，操作按钮可见。

此前的 252 个场景、更新窗口专项和 36 次布局补测保留为历史证据，不叠加到最终构建的场景数量。最后一次构建纳入了验证期间新增的公共按钮、输入框、字体样式及蓝牙交互更新。

验证对应 `driver-final-runner/` 中的主程序和测试工具，以及 `driver-final-bin/` 中的驱动管理器，构建完成时间为 **2026-10-01 11:31（北京时间）**。三项二进制 SHA-256 和时间记录于 `latest-binaries.json`。收尾时驱动底层安全逻辑及部分测试文件仍有其他修改，晚于相应构建的文件已单独列出；这些后续修改不属于上述运行时验证结论。`recorded-source-manifest.json` 是收尾时的源码哈希记录，不应当作编译输入快照。

首轮窗口检查中，USB 模式说明和更新预览标题确实存在旧文案；语言下拉框从“简体中文”变成当前选择则是正确行为。检查器现单独断言语言选择值，不再将选择变化误判为旧标签。首轮记录保存在 `live-switch-before-fixes.*`。

## 验证辅助修复

- 为语言显示测试补齐缺失的 `System.IO` 引用，使测试实际可编译、可执行。
- 预览模式保存设置时不写入用户配置，避免窗口预览关闭时保存测试用设置；使用独立目标文件验证无写入。
- 窗口动画回归在并发渲染负载下等待实际终态，保留原有尺寸断言及两秒上限，避免仅依赖固定延时的中间帧误判。
- 增加按窗口名称复测语言显示的入口，便于定位问题而不重复完整矩阵。
- 增加按语言运行完整布局矩阵的入口，并补充父驱动实际确认弹窗和 8 种新操作结果的三语验证。

## 复现命令

```powershell
python -X utf8 scripts/audit_localization.py
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify_localization.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_cleanup_localization.ps1
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release
dotnet build src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj -c Release
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --localization-audit
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --language-display-audit outputs-next/localization-recheck/live-switch
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --language-display-audit outputs-next/localization-recheck/update-final update
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --ui-audit outputs-next/localization-recheck/app-ui
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --ui-audit outputs-next/localization-recheck/app-ui-en --culture en-US
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --driver-localization-audit src/DriverInstaller/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --driver-ui-audit . src/DriverInstaller/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll outputs-next/localization-recheck/driver-ui
```

本轮检查覆盖资源、应用显示逻辑和 WPF 渲染；未连接实体手机执行 USB、蓝牙或 AirPlay 端到端操作，未安装或卸载驱动。外部发布说明、系统及原生组件的原始诊断不属于应用翻译资源。
