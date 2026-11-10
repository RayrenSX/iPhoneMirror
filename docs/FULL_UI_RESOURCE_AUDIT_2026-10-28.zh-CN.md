# 全项目界面、资源与交互审查

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；历史测试结论未在本次重新执行。

审查日期：2026-10-28（Asia/Shanghai）。基线：`main` / `43d7a83`。

## 后续缺项补验（2026-10-28）

已补建真实 Core/USB bridge 负载并完成正常构建，补跑主工作区 370 组、首次设置 920 组、
可访问性外壳 20 组、原生外壳 20 组，以及错误、托盘、映射、动画/性能、检查清单和完整五语安装器编译。
Python 499/499、原生 18/18、应用逻辑和驱动管理器测试通过。真实 iPhone 的 USB 投屏及 HID 控制各两轮通过。

无线控制实机尝试因缺少 RemotePairing 记录受阻；AirPlay/蓝牙实际手机流程、系统高对比度、Narrator 和多屏 DPI 仍未完成。
最终证据、追加修复、复跑命令及人工边界见 [UI 审查缺项补验记录](UI_VALIDATION_COMPLETION_2026-10-28.zh-CN.md)。
下方修复复验与原始审查保留为历史记录，其中“缺少负载”“待复跑”等状态应以上述后续记录为准。

## 2026-10-28 修复与复验

以下 8 项已完成代码修复；下方原始审查内容保留为问题与证据基线。
本次修复还处理了真实 200% 字号下发现的按键向导、驱动高级页和故障帮助页裁切。
验证文件位于 `outputs/ui-resource-fixes/`；界面清单和测试入口见
[UI 审计覆盖清单](UI_AUDIT_COVERAGE.md)。

| 问题 | 修复结果 | 复验与边界 |
|---|---|---|
| UI-01 | AirPlay 副标题启用换行，底部操作改为共享 `DialogActions` | 五语言、两主题、100%/200% 文字独立窗口矩阵通过；日语放大截图复核通过 |
| UI-02 | 首次设置正文工厂、标题、动态卡片、结果行、行高，设备说明、驱动页及映射向导文字接入动态资源；纯图标独立定尺寸 | 实际 WPF 动态文字缩放回归通过；驱动矩阵断言标题实际为 22/44 DIP；按键向导五步骤通过。首次设置完整分支仍需 Core 负载复跑 |
| UI-03 | 驱动语言初始化时应用与主程序一致的日语字体回退，默认文字样式动态读取字体；语言字典采用明确程序集 URI | 五语言驱动资源/界面测试通过，矩阵校验日语资源字体为 `Segoe UI, Yu Gothic UI` |
| UI-04 | 两项目 `SatelliteResourceLanguages` 加入 `ja` | 两个 self-contained 输出均包含 12 个日语框架卫星程序集；读取实际发布资源，剪切/复制/粘贴日语菜单文本通过。主程序此处为 UI 专用发布输出，不能作为完整发布包 |
| UI-05 | 设备管理、托盘、录制/媒体输出的未知异常显示五语言摘要和下一步操作；已本地化业务拒绝保留原意，原始异常记录日志；录制保存失败明确提示文件仍可保存 | 五语言异常分类回归通过，包括相同文本但来源未知的异常；没有改写配置加载的原始 InnerException。未破坏真实配置或制造手机/蓝牙故障 |
| UI-06 | 移除首次设置窗口对 `DialogSurfaceBrush` 的局部遮蔽 | 源码作用域复核及正反高对比度资源对的 WPF 切换回归通过；未更改 Windows 高对比度设置，尚未完成该桌面环境中的整窗人工验收 |
| UI-07 | 滚动条行为通过动态 `ClientAreaAnimationKey` 跟随系统偏好；关闭动画立即清除时钟并应用悬停终态，只改变横/竖对应的厚度 | 两方向悬停/退出及动画进行中关闭偏好的回归通过；滑块可拖动长度保持由 Track 计算 |
| UI-08 | 托管矩阵复用 `SupportedUiCultures.All`，静态审计复用五语言列表；主程序、驱动和独立矩阵支持日语定向执行；增加独立窗口/映射五步矩阵及全部 42 个 XAML 入口的专项覆盖映射 | JSON 输出实际语言、主题、字号、窗口组合；明确区分模拟 DPI 工作区和文字放大。修正驱动测试在再次应用主题后意外重置模拟字号的问题，重新执行最终矩阵 |

修复中追加的布局处理：映射向导使用最小页高，让放大内容增长并滚动；驱动高级页使用外层页面滚动，标题及状态说明换行；故障帮助页的长标题换行。这些检查不调用真实驱动操作、设备输入或组件下载。

| 最终验证 | 结果 | 证据 |
|---|---|---|
| 主程序及 Runtime.Tests 托管 UI Release 构建 | 0 错误、0 警告；使用原审查的 UI 专用目标跳过缺失载荷 | `build-final.log` |
| 驱动 self-contained 发布 | 正常发布成功 | `publish-driver.log` |
| 独立窗口及映射五步骤 | **360 组，0 失败、0 布局告警、0 绑定诊断** | `standalone-final/standalone-results.json` |
| 驱动五语言、两主题、100%/200% 文字 | **320 组，0 失败、0 布局告警、0 绑定诊断**；实际标题字号断言通过 | `driver-final-results.json`，由 `driver-ja-verified` 及其余四语 `driver-verified-*` 合并 |
| UI 资源专项回归 | 本地化错误、已知业务拒绝、动态首次设置文字、配对高对比度资源、两方向减少动画通过 | `regressions-final.log` |
| 主程序实际资源字典 | 38,880 次格式化、6 次切换、18 个控制流程通过 | `localization-runtime.log` |
| 驱动实际资源字典 | 3,980 次格式化、5 次切换、75 个翻译结果通过 | `driver-localization-runtime.log` |
| 共享控件状态 | 两主题 × 两文字大小通过 | `control-states.log` |
| 日语框架编辑菜单资源 | 两输出均通过 | `framework-ja-app.log`、`framework-ja-driver.log` |
| 静态本地化与审计单元测试 | 0 本地化错误，9 个单元测试通过；Inno 基础语言文件缺失警告仍保留 | `localization.log`、`localization-unit.log` |

最初 `driver-ui/` 中 320 组的模拟字号会在主题切换后被重置，不能用于 200% 通过结论。
后续真实字号复跑发现布局问题并完成修复，以上结论只使用带实际字号断言的最终五语结果。

附加尝试的完整 `App.Logic.Tests` 未全程通过，停在 UxPlay 后台刷新崩溃退避断言，记录于
`logic.log`；不将它计为本次 UI 复验通过项。完整主工作区、首次设置全部分支、原生媒体与
设备错误端到端流程仍因缺少 Core/bridge 负载而待验证；安装器完整五语编译、真实系统
高对比度/多屏/DPI 和屏幕阅读器人工验收也继续保留在下方原始边界中。

## 原始审查记录


本轮为审查，未修改产品代码。检查覆盖主程序、独立驱动管理器、共享 UI、五语言资源、安装器语言声明及清理/辅助工具文案。结果包含 **8 项问题：6 项 P2、2 项 P3**；在已检查范围内未确认 P0/P1。不能据此宣称所有实体设备流程和主工作区运行表现均已通过。

## 结果概览

| 范围 | 本轮检查与结果 |
|---|---|
| 多语言资源 | 扫描 611 个第一方源码文件；主程序每语言 1,613 条，驱动管理器每语言 199 条；五语言键集合、占位符等静态检查 0 错误 |
| 实际 WPF 字典 | 主程序 38,712 次格式化、6 次字典切换、18 个控制状态文案流程通过；驱动管理器 3,980 次格式化、5 次字典切换、75 个结果翻译通过 |
| 资源与资产 | 59 个 XAML 文件、42 个带类名的界面、2,069 个不同显式资源键、627 个不同文本形式的资源引用；唯一外部样式引用 `DefaultComboBoxStyle` 来自 WPF-UI |
| 图标 | ICO 含 16/20/24/32/40/48/64/96/128/256 共 10 档；应用 PNG 1024×1024、含 alpha；1254×1254 的源图未作为应用资源打包 |
| 深浅主题 | 两套主题键集合一致；审计脚本所检查的 18 组前景/背景对比度均高于 4.5:1；这只是选定语义色对，不能代替所有叠加、透明和视频画面的对比度检查 |
| 共享控件 | 两种主题 × 两档文字大小的只读选择、焦点、三态、禁用可读性和按钮换行间距检查通过 |
| 驱动管理器界面 | 128 组渲染通过；0 构造/行为失败、0 布局告警、0 绑定诊断；此既有矩阵包含四种语言，不含日语 |
| 独立弹窗补充检查 | 13 类窗口 × 五语言 × 深浅主题 × 100%/200% 文字大小，共 260 组；0 构造失败，2 条布局告警，对应同一个日语截断问题 |
| 日语驱动页专项 | 额外读取真实渲染字体与字号，确认日语回退字体和局部固定字号问题 |
| 动画 | 源码检查入场、主题切换、工作区过渡、首次设置、向导和滚动条；发现滚动条遗漏减少动画偏好。主工作区动画性能测试因原生库缺失而受阻 |
| 安装器 | 五语言声明存在；本地缺少 Inno Setup 四个基础语言文件，完整有效消息合成及安装器编译未执行 |
| 主工作区与设备流程 | 静态审查；完整界面矩阵、控制错误呈现专项和动画性能专项因缺少 `iPhoneMirror.Core.dll` 无法继续，不记为产品断言失败，也不记为通过 |

## 确认问题

### UI-01 · P2 · 日语 AirPlay 选择页在 200% 文字大小下截断

位置：[AirPlayDeviceSelectionWindow.xaml:8](../src/App/Windows/AirPlayDeviceSelectionWindow.xaml#L8)。

副标题 `DeviceBindingAirPlayAddTitle` 使用可放大的字号，却未启用换行。窗口固定宽度 482 DIP，副标题实际需要约 429 DIP，分配宽度约 394 DIP，末尾内容消失。深浅主题均复现，自动检查报告两次同源告警；人工截图检查也确认截断。

复现：日语 → AirPlay 设备选择 → 200% 文字大小。主标题会换行，下一行“AirPlayデバイスのプロファイルを追加”被裁掉末尾。

建议：副标题使用可换行的共享标题样式，同时检查底部横向操作区在长文本和小工作区下是否应使用 `DialogActions`。

证据：日语 200% 截图（本地记录：`outputs/full-ui-audit-2026-10-28/standalone-ui/ja-JP-Light-2-AirPlayDeviceSelectionWindow.png`）、260 组结果（本地记录：`outputs/full-ui-audit-2026-10-28/standalone-ui/probe-results.json`）。

### UI-02 · P2 · 新页面局部文字绕过系统文字放大

位置：[首次设置文本工厂:72](../src/App/Windows/FirstRunSetupWindow.xaml.cs#L72)、[首次设置标题:190](../src/App/Windows/FirstRunSetupWindow.xaml.cs#L190)、[设备管理说明样式:13](../src/App/Windows/DeviceBindingWindow.xaml#L13)、[驱动简洁页:136](../src/DriverInstaller/MainWindow.xaml#L136)。

`AccessibilityAppearance` 通过覆盖 `FontSize` / `LineHeight` 资源响应系统文字大小。上述页面的局部 `FontSize=12/16/22/28`、说明 `LineHeight=19` 及首次设置动态创建的 `FontSize = size` 不读取这些资源。因此同一窗口中部分文字变大、部分关键说明仍保持原大小。

独立日语驱动页在模拟 200% 文字大小后，应用标题为 44 DIP，而“准备投屏环境”标题仍为 28 DIP；局部说明保持 12 DIP。该问题是实际字号未放大，不只是字号 token 命名不统一。源码中还发现按键映射向导的少量固定文字字号，应一起区分文字与纯图标后检查。

建议：正文、说明、标题和行高使用动态语义资源；确有独立字号需求时增加相应 token。图标尺寸保持独立，不要求随文本同比放大。

证据：真实字体字号记录（本地记录：`outputs/full-ui-audit-2026-10-28/font-probe/driver-ja-font.json`）、日语驱动页 200%（本地记录：`outputs/full-ui-audit-2026-10-28/font-probe/driver-ja-200.png`）。

### UI-03 · P2 · 驱动管理器未采用日语字体回退

位置：[Driver App.xaml:20](../src/DriverInstaller/App.xaml#L20)、[DriverLocalization.cs:21](../src/DriverInstaller/Services/DriverLocalization.cs#L21)、[共享默认字体:5](../src/SharedUI/Themes/DesignTokens.xaml#L5)。

主程序切换日语时将字体更新为 `Segoe UI, Yu Gothic UI`；驱动管理器只切换语言字典与 Culture，仍使用 `Segoe UI, Microsoft YaHei UI`。渲染探针确认 `Language=ja-JP` 时，应用字体和真实日文 TextBlock 均保留中文回退。共用汉字会优先采用中文字体中的字形，假名则可能继续回退，主程序与驱动工具的日语排印不一致。

建议：驱动程序初始化语言后、构造窗口前应用相同的语言字体策略；正文样式使用动态字体资源，避免已解析的静态字体阻止更新。

证据同 UI-02 的字体记录。此专项显式限定程序集资源 URI，以避免外部验证宿主的入口程序集影响相对资源解析。

### UI-04 · P2 · 五语言应用的框架资源过滤遗漏日语

位置：[主程序 csproj:41](../src/App/iPhoneMirror.App.csproj#L41)、[驱动管理器 csproj:31](../src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj#L31)。

两者 `SatelliteResourceLanguages` 仅包含 `en;zh-Hans;zh-Hant`，没有 `ja`。日语 XAML 字典本身可以正常工作，但日语框架卫星资源被过滤，本轮构建输出也只有 `zh-Hans`、`zh-Hant` 语言目录。框架提供的默认文本，例如 WPF 默认编辑菜单和框架诊断，无法从应用负载中加载日语卫星资源，会回退到中性语言。

建议：添加 `ja`，分别验证主程序和驱动程序的 self-contained 发布负载及默认编辑菜单。这里确认的是项目过滤条件与构建输出缺失；本轮未将默认菜单逐项展开截图，也未创建正式发布包。

### UI-05 · P2 · 错误入口仍将原始异常直接作为用户说明

位置：[设备管理刷新:351](../src/App/Windows/DeviceBindingWindow.xaml.cs#L351)、[录制恢复:390](../src/App/Windows/MediaOutputSettingsWindow.xaml.cs#L390)、[托盘入口:88](../src/App/Windows/TrayPanelWindow.xaml.cs#L88)。

设备管理、托盘和部分录制流程直接赋值 `error.Message`。存在明确的第一方英文异常来源：[DeviceBindingManager.RefreshFromDisk:50](../src/App/Services/DeviceBindingManager.cs#L50) 在配置不可读时抛出 `Device profiles could not be read.`；设备管理刷新循环直接把它写到 `ActivityText`。因此在中文或日语界面仍会显示英文，且缺少可执行的恢复说明。底层 IO/蓝牙异常还可能随操作系统语言变化。

建议：用户区使用本地化摘要及下一步操作，例如重新读取、重试或查看配置诊断；日志或可展开详情保留原始异常。区分已经本地化的业务拒绝和未知异常，避免把所有 `error.Message` 一律二次包装。配置加载错误应保留原始 InnerException 诊断。

此项由确定的异常来源和 UI 调用链确认；本轮没有破坏用户配置、断开设备或人为制造真实蓝牙故障。

### UI-06 · P2 · 首次设置的局部背景绕过高对比度配色

位置：[FirstRunSetupWindow.xaml:8](../src/App/Windows/FirstRunSetupWindow.xaml#L8)。

首次设置在窗口本地重新定义 `DialogSurfaceBrush`，其颜色取自深浅主题的 `ThemeBackgroundColor`。共享高对比度逻辑在 Application 资源中覆盖同名画刷，但窗口本地资源优先，圆角外壳继续使用普通主题背景；文字却可以读取系统高对比度前景。当前景与保留下来的背景接近时，会破坏可读性。

建议：移除这一局部遮蔽，或让局部背景在高对比度时使用配对的系统窗口颜色。需要同时验证深色主题配浅色高对比度方案、浅色主题配深色高对比度方案。

此项为源码资源作用域确认；本轮未修改用户的系统高对比度设置，未给出实际高对比度截图或声称已测量该场景对比度。

### UI-07 · P3 · 共享滚动条动画遗漏系统减少动画偏好

位置：[ModernControls.xaml:249](../src/SharedUI/Controls/ModernControls.xaml#L249)、[宽度动画:278](../src/SharedUI/Controls/ModernControls.xaml#L278)。

滚动条的鼠标进入/退出会直接启动透明度及宽度/高度 Storyboard，时长 100–200ms，没有 `SystemParameters.ClientAreaAnimation` 条件。页面入场、主题和向导等代码动画已检查该偏好，这组共享模板是遗漏点，影响两程序使用它的滚动区。

建议：系统关闭客户端动画时立即设置最终视觉状态；保留相同的悬停反馈与命中区域。源码中还存在 160/180/300/480ms 等按场景定义的时长，单凭不同数值不判为缺陷。

### UI-08 · P3 · 默认审计矩阵未随日语及新界面扩展

位置：[UiConsistencyAudit.cs:41](../src/App.Runtime.Tests/UiConsistencyAudit.cs#L41)、[DriverUiConsistencyAudit.cs:33](../src/App.Runtime.Tests/DriverUiConsistencyAudit.cs#L33)、[Program.cs:249](../src/App.Runtime.Tests/Program.cs#L249)、[scripts/audit_ui.py:68](../scripts/audit_ui.py#L68)。

两套常规界面矩阵仍枚举四语言；主程序 `--culture` 参数也不接受 `ja-JP`。静态 `audit_ui.py` 的语言集合也只有四语言。较新的 `audit_localization.py` 和运行时字典测试已经支持五语言，所以会出现字典全部通过、日语排版却没有进入常规界面审计的情况，UI-01 是本轮实际发现的例子。

常规主程序矩阵还没有包含首次设置、按键映射、窗口样式、托盘和组件下载等全部界面。项目已有部分独立专项测试，不能据此断言这些页面从未测试，但当前“全量界面矩阵”不足以代表全部产品界面。本轮另建只读探针补查其中可独立构造的窗口。

建议：用统一受支持语言列表生成矩阵并允许日语单独运行；明确维护界面清单与专项覆盖映射，在报告中输出语言/窗口/主题/文字大小的实际组合数。

## 排版、资源与交互的其他检查

- 字号层级已有 Page 24、Dialog 20、Section 14、Body 13、Secondary 12、Caption 11、Button 14 DIP；主要问题集中在新增页面未接入资源，而非需要另建一套设计系统。
- 普通按钮、输入、列表、关闭按钮和圆角窗口已有共享样式；正文滚动与外层 `DialogViewport` 能在大文字下保留内容可达性。200% 截图中部分动作需要滚动才能看到，本身不等于不可访问。
- 图标字形字号与普通文本分开判断。视频预览黑底、设备圆角和代码/诊断等宽字体属于有明确用途的差异，未作为不一致问题。
- 本轮未发现新的中文硬编码、资源缺键或占位符不一致。技术名、品牌、单位、协议和用户输入保留原样；不能把所有英文都判为漏翻译。
- 静态未引用资源不能直接认定废弃，因存在动态键、反射和状态组合调用；本轮未删除资源。
- 错误/危险确认已有共享提示、取消默认键及技术详情路径；共享控件运行时检查通过。原生错误链需要完整 native 负载后再验证。
- PNG/ICO 文件头和资源声明核对通过；这不等于所有 DPI 下的视觉锐度、安装后图标缓存和真实托盘显示均已实测。
- 未引入产品行为或外部网站变更，未发布、安装、卸载驱动、操作真实手机、改变配对或改写用户系统设置。

## 验证方法与边界

所有本轮证据保存在 `outputs/full-ui-audit-2026-10-28/`（Git 忽略目录）。本报告保存在 `docs/`，可单独提交。主要产物：

| 文件 | 内容 |
|---|---|
| `localization.log` | 611 文件静态本地化扫描；0 错误、1 条安装器基础语言文件不可用警告 |
| `ui-inventory.json` | 42 个界面、主题键、选定对比度、硬编码颜色与固定文本按钮清单 |
| `resource-analysis.json` | XAML 资源定义与引用、图标文件头/尺寸清单 |
| `build-runtime.log` | 正常构建因缺少 USB bridge 负载终止的原始记录 |
| `build-ui-only.log` | 仅供 UI 审查的托管构建，0 错误、0 警告 |
| `build-driver.log` | 驱动管理器正常 Release 构建，0 错误、0 警告 |
| `runtime-summary.txt` | 各既有专项的退出状态；缺 native 的三项保留非零退出记录 |
| `localization-runtime.log` / `driver-localization-runtime.log` | 实际 WPF 资源与格式化结果 |
| `control-states.log` | 两主题、两文字大小共享控件检查 |
| `driver-ui/driver-results.json` | 128 组驱动界面结果及 PNG |
| `standalone-ui/probe-results.json` | 260 组独立弹窗实际标签、字号、字体和布局告警 |
| `font-probe/driver-ja-font.json` | 日语驱动页字体及 200% 实际字号 |
| `probe/` | 可重现补充验证的临时 C# 宿主源码；不属于产品代码 |

纯 UI 构建通过临时 `ui-only.targets` 覆盖 USB bridge 负载验证，并移除不存在的 Content 项；没有生成虚假的 bridge、native DLL 或发布包，也未修改原始 csproj。这只能证明当前托管代码与 WPF 资源可以构建，不能替代正式打包验证。

独立探针使用产品实际编译程序集、只读预览构造函数和原有布局检查器；覆盖高级设置、启动错误、独立窗口样式、组件下载、危险确认、采集恢复、蓝牙等待/连接/客户端、AirPlay 选择、文本输入、实例冲突及受保护内容提示。数据为预览 fixture，图像为 WPF 渲染。字体大小模拟通过同一 `AccessibilityAppearance.Apply` 入口完成，没有修改系统注册表。260 组探针未单独采集绑定监听器，不能套用驱动矩阵的“0 绑定诊断”结论。

**尚未完成的验证：**完整主工作区动态布局、首次设置全部分支、按键映射全部步骤、真实 DPI/多屏切换、系统高对比度视觉效果、屏幕阅读器操作、系统关闭动画后的动态行为、原生预览/覆盖层、USB/AirPlay/蓝牙错误端到端流程，以及安装器完整五语编译。这些范围可从源码发现问题，但需要完整运行负载和相应环境才能给出实测结论。

## 建议处理顺序

1. 先处理 UI-01、UI-02、UI-06，恢复大文字和高对比度下的可读性。
2. 再处理 UI-03、UI-04、UI-05，完成日语字体/负载及错误说明的一致性。
3. 同步补齐 UI-08 的语言与界面矩阵，并修复 UI-07 的减少动画支持。
4. 在完整 bridge/native 负载可用后补跑本轮受阻的主工作区、动画及错误链测试，再进行实体设备验收。
