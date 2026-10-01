# 文字、控件自适应与裁切复审

> 2026-10-01 统一规范复审及新增窗口验证见 [界面规范复审](UI-STANDARDS-REVIEW.md)。

日期：2026-10-01。接续 [UI 一致性审计](UI-CONSISTENCY-AUDIT.md) 和 [自绘圆角审计](UI-ROUNDED-WINDOW-AUDIT.md)。本轮以实际 WPF 布局测量、渲染、控件状态切换为依据；未使用 Computer Use，未添加依赖。

## 范围与问题

审查两个应用全部 27 个 XAML 窗口（其中 25 个为共享自绘圆角子窗口），以及主窗口中的四个工作区、选项卡、按钮、输入框、复选框、列表、滚动区、展开详情、原生预览菜单与下拉弹层。静态清单另含两个 Application 根，共 29 个 XAML 根。

| 严重度 | 发现 | 修复 |
| --- | --- | --- |
| Critical | 本轮未发现 | — |
| High | 主窗口按真实高 DPI 工作区缩小时，同时展开左右面板会把预览和状态文字挤到近乎零宽；部分侧栏操作在矮窗口下不可见 | 窄窗口下将两个侧栏上下排列，预览仍在独立单元格中；侧栏与空状态说明可滚动；统计信息在两列/四列之间切换，矮窗口改为单行横向滚动以保留预览高度；矮预览区收起装饰图形，优先显示空状态文字 |
| High | 驱动工具简单模式、高级详情操作、失败帮助在小工作区下会裁切；长行动说明没有滚动容器 | 调整标题工具区布局、按比例分配列表与详情宽度，给可变内容添加滚动区；保持操作入口可达 |
| High | 启动错误窗口展开技术详情时，Expander 标题与内容共用不足的高度 | 为整个展开区提供滚动，详情文本框有独立的横向/纵向滚动与有界高度 |
| Medium | 输入框存在 Height=36 与共享 MinHeight=40 冲突；部分双行按钮、信任确认按钮锁死高度 | 用 MinHeight 保留点击区域，让文字决定实际高度 |
| Medium | 默认按钮、复选框的纯文本内容不能换行；设备绑定动作文字在窄列中截断 | 共享模板仅对 string 内容使用可换行的 AccessText，保留快捷键和原有复合内容 |
| Medium | 受保护内容的英文说明截断；通知的长内容缺少工作区高度上限 | 说明换行，通知正文有界滚动，关闭操作保持独立 |
| Medium | 关于页日志禁用横向滚动，长行不可读取；日志标题与按钮共享单元格 | 日志支持横向滚动；路径可换行；标题与动作分列 |
| Medium | 开发工具的预设按钮横向越界、底部文字可能与关闭按钮重叠 | 按钮组换行；底栏分列 |
| Low | 原检查跳过滚动区内控件，只检查 string 按钮；切换选项卡后没有等待实际内容加载 | 增加真实文本测量、祖先裁切、滚动容器检查；实际加载各选项卡、展开详情，并保存普通/小尺寸截图 |

## 设计规则与改动文件

- 普通文本输入和文本按钮使用最小高度，而非固定高度。图标按钮、视频画布和装饰图形保留合理的几何尺寸。
- 文字优先利用可用宽度换行；日志/技术文本保留可以操作的滚动视口。单行选择器仍遵循平台的选择和滚动行为。
- 可变正文占据星号行或有界滚动区，关闭/确认动作保留独立空间。按钮组在需要时换行。
- 自绘窗口外框继续使用 20 DIP 圆角、1 DIP 边框、10 DIP 阴影空间；Popup 保持 12 DIP。正文适配在外框内部完成。
- 颜色、字体层级、主题、状态语义、图标系统和动画沿用现有共享资源。

主要实现：`src/SharedUI/Controls/Buttons.xaml`、`Inputs.xaml`；`src/App/MainWindow.xaml`、新增 `MainWindow.AdaptiveLayout.cs`；App 的 About、AdvancedSettings、DeveloperTools、MediaOutputSettings、ProjectionSettings、ShortcutSettings、CaptureStatusNotice、ProtectedContentNotice、InstanceConflict、StartupError；DriverInstaller 的 MainWindow、DeviceTrust、RequiredAction、Prompt、FailureHelp。

验证实现：新增 `src/App.Runtime.Tests/AdaptiveLayoutAudit.cs`，扩展 `UiConsistencyAudit.cs`、`DriverUiConsistencyAudit.cs`。运行时主题文字检查在模板和绑定完成更新后读取实际文字颜色，并兼容 AccessText 的内部文字容器。逻辑测试工程补上当前 LocalizationService 依赖的 LocalizedText 源文件链接。

## 覆盖检查表

| UI 区域 | 检查内容 | 处理/结果 |
| --- | --- | --- |
| MainWindow：投屏、设备、设置、输出 | 最小尺寸、五档工作区、侧栏与空状态、统计数据、显示中的预览工具栏 | 修复窄窗口布局与滚动；工具栏内容超宽时可横向滚动 |
| AboutWindow | 关于、更新设置、诊断三个选项卡；日志/路径 | 修复日志可读性与标题操作布局 |
| AdvancedSettingsWindow | 分辨率输入、验证文字、操作区 | 输入自适应高度 |
| AirPlayDeviceSelectionWindow | 设备列表、空状态、操作区 | 纳入完整矩阵 |
| DeviceBindingWindow | 设备档案、有线/AirPlay/蓝牙绑定卡片与动作 | 共享按钮换行修复窄列裁切 |
| BluetoothClientBindingWindow、BluetoothConnectionWindow | 绑定、等待与取消/继续动作 | 纳入完整矩阵 |
| BluetoothControlNoticeWindow | 等待、失败、超长失败说明 | 正文滚动和底部操作通过矩阵 |
| AppPromptWindow | 普通、前置条件提示、中英混合长正文与长动作文字 | 确认按钮换行；正文可滚动 |
| ReverseControlStatusWindow | 有线/无线前置条件、运行状态、长确认提示、取消/关闭 | 纳入完整矩阵，长提示操作可滚动到达 |
| CaptureRecoveryWindow | 恢复提示与操作 | 纳入完整矩阵 |
| CaptureStatusNoticeWindow | 反向控制错误、捕获错误、会话结束、USB 配置错误 | 正文有界滚动，技术详情展开参与检查 |
| ProtectedContentNoticeWindow | 标题、保护说明、状态徽标 | 修复说明换行和正文滚动 |
| ImageSettingsWindow | 图片选项、操作与滚动 | 纳入完整矩阵 |
| ProjectionSettingsWindow | 投屏/无线/蓝牙控制设置、输入与下拉 | 输入高度统一 |
| MediaOutputSettingsWindow | 录制、流媒体、虚拟摄像头选项卡 | 输入高度统一；逐页实际加载验证 |
| ShortcutSettingsWindow | 快捷键捕获输入、状态、底部动作 | 输入高度自适应 |
| StartupErrorWindow | 错误摘要、日志路径、展开详情、关闭动作 | 修复展开区裁切并专项复测 |
| UpdateWindow | 版本信息、发布说明、下载状态与操作 | 纳入完整矩阵与明暗主题文字回归 |
| InstanceConflictWindow | 冲突说明、状态与两种关闭动作 | 正文有界滚动 |
| UsbProjectionModeInfoWindow | 模式介绍与关闭操作 | 纳入完整矩阵 |
| DeveloperToolsWindow | 全部预览入口、参数、预设、诊断、底栏 | 复合按钮自适应，按钮组换行 |
| Driver MainWindow：简单/高级 | 标题工具区、设备列表、状态、安装/修复/卸载 | 修复小工作区可达性 |
| Driver DeviceTrustWindow | 信任说明、各回答与退出 | 可变内容统一滚动，按钮高度自适应 |
| Driver RequiredActionWindow、PromptWindow | 标准提示与长正文/长动作压力场景 | 正文滚动、动作换行 |
| Driver FailureHelpWindow | 错误详情、帮助方法、重试/关闭 | 错误及帮助内容可滚动 |
| NativePreviewWindow、媒体外壳、保护覆盖层 | 菜单/子菜单、全屏恢复、覆盖层、Popup | 原生外壳专项回归 |
| 普通 ComboBox、Tooltip | 三种语言和明暗主题下展开 | 原有圆角与模板加载回归 |

## 验证方法与边界

- 每个语言/主题组合都加载真实窗口及资源，检查普通尺寸、可缩放窗口的声明最小尺寸，以及 100%、125%、150%、175%、200% 的可用 DIP 预算。
- 高 DPI 预算采用实际 WindowWorkAreaController 的 80% 工作区限制（以 1920×1080、40 像素任务栏为基准），比上一轮只按比例缩小的检查更严格。
- 使用同字体、字号、字体粗细、语言、换行和文本格式化规则的 WPF TextBlock 测量正文；检测文字及控件在祖先布局边界处的裁切。滚动区内的元素继续检查；只有可滚动方向允许超出视口。
- 保留正常的省略显示、输入框内部滚动和紧凑导航的图标模式；导航隐藏的文字由 Tooltip/展开导航呈现，避免把有意隐藏的标签当作裁切。
- 对所有普通子窗口继续运行自绘圆角、角部透明度、缩放命中、最大化/还原和 Escape 检查。
- 主 UI 布局矩阵不初始化原生视频设备；原生外壳和运行时 smoke 单独测试，避免显示驱动影响批量布局验证。
- 这是实际 WPF 布局/渲染测试，不等同于在五台物理 DPI 显示器上逐一手动验收。未进行真实设备捕获、驱动安装或远程设备输入。

## 最终验证记录

原始截图、JSON 与日志位于 `outputs/ui-audit/`。

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 第一轮增强检查 | 英文 33 场景发现 274 条尺寸/裁切记录（含同一问题在不同尺寸重复命中及诊断误报），据此修复 | `adaptive-baseline/runtime-results.json` |
| 全语言完整复审 | 204 组合，窗口/资源加载与绑定均无错误；新增展开详情检查发现启动错误详情裁切，随后修复 | `adaptive-full/runtime-results.json` |
| 启动错误专项复测 | 6 组，0 失败、0 布局问题、0 绑定诊断 | `adaptive-startup-recheck/runtime-results.json` |
| 驱动工具最终矩阵 | 48 组，0 失败、0 布局问题、0 绑定诊断 | `adaptive-driver-delivery/driver-results.json` |
| 原生预览、菜单与 Popup | 12 组，0 失败、0 绑定诊断 | `adaptive-preview/preview-shell-results.json` |
| App 运行时回归 | 通过；包含主题文字、倒计时、窗口、工作区和原生预览路径 | `adaptive-smoke-final.log` |
| 工作区位置/动画回归 | 通过 | `adaptive-workspace-regression-final.log` |
| App / Driver 逻辑回归 | 均通过 | `adaptive-logic.log`、`adaptive-driver-tests.log` |
| 最终主程序完整矩阵 | 204 组，0 失败、0 布局问题、0 绑定诊断 | `adaptive-final/runtime-results.json` |
| 最后主窗口视觉调整补测 | 24 组，0 失败、0 布局问题、0 绑定诊断；包括四种主窗口场景、矮窗口、预览工具栏与侧栏关闭/重新打开 | `adaptive-workspace-final/runtime-results.json` |
| App / Driver 最终编译 | 均成功，0 警告、0 错误 | `build-adaptive-delivery.log`、`build-adaptive-driver-delivery.log` |
| 静态清单与格式 | 29 个 XAML 根，明暗主题键一致；变更文件 diff 检查通过 | `adaptive-static.json` |



补充验证：工具栏与两侧面板同时显示的六组组合全部通过（`adaptive-toolbar-final/runtime-results.json`）。最后的主窗口补测覆盖了最终视觉调整，已检测的裁切问题均已修复；物理 DPI 与真实设备测试边界见上文。
