# 繁體中文（台灣）本地化审查

审查日期：2026-10-03。独立语言为 `zh-TW`，显示名称为「繁體中文（台灣）」；英文界面显示 `Traditional Chinese (Taiwan)`。

## 架构与语言选择

- 主程序使用 WPF `ResourceDictionary`，由 `LocalizationService` 按语言替换合并字典。新增 `TraditionalChineseTaiwan`，加载 `Localization/Strings.zh-TW.xaml`，使用 `CultureInfo.GetCultureInfo("zh-TW")`。
- 主窗口和开发者工具的语言选择器均新增独立台湾选项。沿用 `SelectedLanguage`、`LanguageChanged`、动态资源和 `LocalizedText` 的刷新机制。
- 配置沿用 `%LOCALAPPDATA%/iPhoneMirror/settings.json` 的 `Language` 字段。保存台湾语言不会覆盖接收器名称等其他配置；下次初始化恢复同一语言。
- 系统文化 `zh-TW`、`zh-Hant-TW` 解析到 `zh-TW`。香港、澳门、其他既有繁体标识继续使用原有规则；台湾不会映射为简体或香港字典。
- 驱动管理器使用独立台湾字典，支持已有 `--language` 参数及主程序配置。清理脚本也支持 `-Language zh-TW`、配置语言及系统语言。
- Inno Setup 使用独立 `chinesetaiwan`、Windows 语言 ID `$0404`、codepage 950 和 Microsoft JhengHei UI 字体；原香港语言及 `$0C04` 保留。

## 新增语言资源

| 文件 | 覆盖 |
| --- | --- |
| `src/App/Localization/Strings.zh-TW.xaml` | 1,262 个字符串，另有 1 个字体资源 |
| `src/DriverInstaller/Localization/Strings.zh-TW.xaml` | 196 个字符串 |
| `installer/Languages/ChineseTraditionalTaiwan.isl` | 294 个消息/自定义消息；加上 `.iss` 两个快捷方式共 296 个有效条目 |
| `CHANGELOG.zh-TW.md` | 53 个版本区段、459 条变更说明 |

以下既有文件新增独立台湾文本：驱动清理脚本 91 项；`StartupDiagnostics` 的 2 条故障说明与 5 个备用标签；两个 SRS 测试网页各 82 项。CMD 启动器既有传统中文提示同时适用于香港和台湾，无须重复输出一遍。

主程序涵盖主窗口、工具列、选单、所有设置、设备列表与绑定、各种反向控制、键盘映射、滑鼠与触控、音讯、截图、影片录制、串流、OBS/虚拟摄影机、多设备、独立预览、组件下载、更新器、关于、诊断、对话框及通知。驱动工具包含安装/卸载、权限/信任、父驱动程序和失败恢复提示。

## 用语与内容

逐项审阅台湾译文，统一使用「設定、軟體、螢幕、滑鼠、資料夾、網路、資訊、音訊、使用者、裝置、驅動程式、伺服器、連線、記錄檔、解析度、快速鍵、隱私權、硬體、佇列」。

产品概念统一为「螢幕鏡像」「反向控制」「裝置綁定」「裝置設定檔」。引用 iOS 控制中心实际指令时使用「螢幕鏡像輸出」。录制成品称「影片」，实时通话/输入相关技术语境使用「視訊」。保留 Apple、AirPlay、Bluetooth、USB、Wi-Fi、OBS、FFmpeg、RTMP、SRT、WHIP 和 API/协议名。

关于页面和安装器快捷方式打开台湾更新日志。更新窗口从内嵌台湾日志提取历史版本对应区段，也支持从可信 GitHub 标签读取新版台湾日志。已打开窗口从其他语言切到台湾时，也会按需读取新版台湾说明；重复切换复用读取结果。原始发布内容单独保留，切回其他语言不会丢失内容。

## 完整性

- 四种语言的主程序、驱动工具、清理脚本、安装器和网页目录均交叉核对；无缺失、多余、重复 Key 或意外空值。
- 安装器 4 个上游可选标签/译者备注按其既有约定留空，属于明确检查例外，不是未完成翻译。
- 检查数字/命名/printf 占位符、安装器参数与快捷键、文件过滤器、换行、转义、标记和链接；台湾与简体及香港文本保持兼容。
- 检查台湾术语和简体专用字。另用 OpenCC 辅助筛查单字，人工排除「台灣、游標、群組」等正确台湾写法，未机械替换。
- 更新日志校验版本顺序、每版本条目与小节数、代码标记、HTML 和链接；网页校验静态标签、可访问性标签和动态翻译调用。
- 44 项保留的 XAML 字面量已复核，均为品牌、协议、数字、符号或单位。完整逐 Key 四语表见 [LOCALIZATION-AUDIT.md](LOCALIZATION-AUDIT.md)。

持续检查入口：`scripts/audit_localization.py`、`scripts/audit_taiwan_content.py`、`scripts/verify_localization.ps1`、`scripts/test_cleanup_localization.ps1`、`scripts/test_web_localization.cjs`；运行时专项入口 `--taiwan-localization`。

## 验证结果

| 验证 | 结果 |
| --- | --- |
| 静态资源审查 | 零错误、零警告 |
| 主程序格式化 | 25,240 个案例、5 次字典切换、15 条控制工作流通过 |
| 驱动本地化 | 3,136 个案例、4 次字典切换、60 种结果通过 |
| 清理脚本 | 1,456 个格式化案例通过，未执行清理操作 |
| 网页 | 6,640 项断言通过，含系统语言和显式语言覆盖 |
| 台湾专项 | 配置保存、重新加载、无关设置保留、文化映射、内嵌更新说明、实时说明切换通过 |
| 线上台湾更新说明 | 正常读取、精确版本匹配、404、大小上限、不可信重定向、取消、窗口语言切换后按需读取及重复切换缓存测试通过 |
| 实时窗口语言切换 | 3,256 项断言、120 次窗口切换，零失败、零绑定诊断 |
| 台湾主程序 UI | 74 个界面渲染，浅/深色与不同尺寸，零布局问题、零绑定诊断 |
| 驱动 UI | 四语言共 128 个渲染，零失败、零布局问题、零绑定诊断 |
| 驱动工具测试 | 通过 |
| 原生模块与 USB 桥接测试 | 14 个原生测试、155 个 Python 测试通过 |

已查看台湾设置/媒体输出/设备绑定和驱动长状态、父驱动确认窗口的截图。长诊断内容保留滚动，未发现台湾文字引入的截断、重叠或溢出，因此未为台湾另设固定尺寸。驱动 UI 检查在断言前等待 WPF 绑定更新，保留原显示断言。

编译：主程序、驱动管理器与运行时测试项目 Release 编译通过，零编译警告/错误。执行项目既有流程：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Configuration Release -NoPublish -OmitUxPlayRuntime -SkipTests
```

该流程完成原生组件、桥接组件和两应用编译。Inno Setup 使用独立验证载荷编译 `.iss` 与四个语言包成功；验证安装包仅用于编译检查，未执行安装、卸载或发布。

**完整回归限制：**也运行了不带 `-SkipTests` 的既有完整流程；在原生和 Python 测试通过后，App.Logic.Tests 被当前工作区并行窗口动画重构的旧源码断言阻断（仍要求 `AnimateWorkspaceSurface` / 旧动画结构）。未弱化或删除这些断言，也未改动动画实现。因此不能声称整个工作区的全量回归全部通过。本地化专项和编译结果不受此断言影响。一次直接运行默认 WPF 套件还遇到测试目录缺少 FFmpeg 的前置条件；该次运行不计为通过。

证据保存在 `work/taiwan-*.log` 及 `outputs-next/taiwan-localization/`（本机验证产物，不纳入发行内容）。

## 人工确认与边界

现有第一方语言资源和版本历史没有待补译条目，也没有翻译占位标记。可在发行前安排台湾母语者做风格审稿，但不依赖该审稿才能使用本语言。

设备名称、用户输入、文件路径、第三方返回的原始技术诊断保留原值。尚未发布的未来版本若未提供台湾更新说明，会保留发布者原文；后续发版应同步维护 `CHANGELOG.zh-TW.md`，持续检查会发现版本或条目遗漏。这不构成语言字典回退到 zh-CN 或 zh-HK。
