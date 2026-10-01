# iPhoneMirror UI 一致性审计报告（2026-09-30）

> 2026-10-01 统一规范复审及新增窗口验证见 [界面规范复审](UI-STANDARDS-REVIEW.md)。

> 后续文字、控件自适应与裁切修复见 [自适应布局复审](UI-ADAPTIVE-LAYOUT-AUDIT.md)。

后续圆角专项已按“尽可能自绘”更新普通子窗口外壳，见 [弹窗与子窗口圆角核对](UI-ROUNDED-WINDOW-AUDIT.md)。下文关于保留 DWM 子窗口圆角的描述属于上一轮设计，已由该专项取代。

## 修改前审计

审计依据是 App、DriverInstaller、SharedUI 的全部 XAML、资源字典、窗口代码，以及主窗口/独立预览/更新 Markdown 的动态 UI。未使用 Computer Use。基线清单由 `scripts/audit_ui.py` 生成于 `outputs/ui-audit/before.json`，共 29 个带 x:Class 的根节点（含两个 Application）；另有原生预览与代码创建的保护覆盖窗口。

### Critical

未发现可从 UI 源码确认的 Critical 缺陷。

### High

- 反向控制窗口采用多行 Auto 布局，错误详情、设备选择与蓝牙步骤增高时没有整体滚动区域，MaxHeight 下可能隐藏操作按钮。
- 普通按钮的 MinHeight=42 与多处 Height=34/36/40 冲突，固定宽度在英文文本下可能裁剪。
- 更新 ProgressBar 的自定义模板没有不确定进度动画，加载状态不能可靠表达仍在工作。
- 反向控制阶段和提示在 C# 内硬编码画刷，浅色主题对比度不足，切换主题后不会更新。

### Medium

- 主程序和驱动工具各自定义普通/主操作按钮模板，禁用透明度、焦点和圆角不同；主操作模板仅支持文本 Content。
- AppPromptWindow 没有默认/取消键约定；USB 提示和丢弃录制确认仍调用系统 MessageBox。
- 主窗口设备菜单、设备绑定菜单重复定义相似模板，与原生预览菜单圆角不同。
- SettingsSection、ModernDialogSurface、SubWindowTitle 已共享，但字体层级与间距缺少语义资源。
- 高级设置、图像设置、恢复说明等内容区域缺少滚动约束。
- 反向控制窗口存在中文硬编码；动态更新文档仍包含固定颜色和英文空文案。
- 图标按钮和部分列表/输入项缺少明确的键盘焦点或可访问名称。

### Low

- 部分窗口的 18/22/26 内边距、标题 19/20/21、菜单 5/7/8/12 圆角缺少清晰角色划分。
- ThemeService 的淡入未遵循系统减少动画设置；页面动画时长没有读取共享资源。
- 媒体播放叠层使用独立深色对比度体系是合理例外；黑色视频画布不应替换成主题背景。

## 最终实施结果

本次沿用现有 WPF / WPF-UI / Fluent 资源体系，未引入新 UI 框架或第三方依赖。先审查源码，再修改共享样式与具体窗口，最后编译、实际创建窗口、展开控件模板并生成渲染图。全程未使用 Computer Use。

### 1. 实际范围

29 个带 x:Class 的 XAML 根节点包括：主程序 22 个窗口、驱动工具 5 个窗口、2 个 Application。所有资源字典也在扫描范围内，但不计入窗口数。逐窗口结果见后面的检查表。

另外检查了以下动态 UI：

- MainWindow 的完整/轻量工作区、设备菜单、播放控件、预览过渡遮罩、空设备和加载状态。
- NativePreviewWindow 的独立投屏/媒体外壳、全屏、窗口/显示/控制/静音子菜单，以及 ProtectedContentOverlayWindow。
- 反向控制的阶段列表、提示选项、技术详情、蓝牙配对步骤、恢复、完成倒计时与失败操作。
- 更新 Markdown 的动态 FlowDocument、链接、代码、引用和分割线。
- WPF-UI NavigationView、FluentWindow、SymbolIcon 和 WPF 的 ComboBox 下拉、TabControl、ScrollViewer 等模板路径。
- 首次使用对应的信任、驱动、权限和必要操作窗口。未发现独立首次启动向导或通用 Toast 类；审查实际提示条、状态卡与通知窗口，没有另建通知系统。
- 系统文件/目录选择器、蓝牙配对、UAC 等入口保留 Windows 原生交互，不重绘系统界面。

渲染图由真实窗口的 RenderTargetBitmap 生成，不是桌面截图。测试图以主题背景合成 Mica 底色，不能用来判断真实桌面材质或视频帧质量。

### 2. 问题处理状态

| 等级 | 问题 | 最终处理 |
| --- | --- | --- |
| Critical | 未确认此级别 UI 缺陷 | 结论限于本次源码和运行验证 |
| High | 反向状态长内容会挤压操作区域 | 固定标题/底部操作，正文滚动；长提示标题改 Grid 约束宽度，操作组可换行 |
| High | 按钮固定尺寸与最小尺寸冲突、英文裁剪风险 | 普通按钮最小高度统一 40、紧凑按钮 32；文字按钮优先 MinWidth/自适应 |
| High | 更新不确定进度缺少动画 | 移除旧模板，恢复 WPF 原生进度模板 |
| High | 反向控制硬编码颜色和主题不同步 | StatusAppearance + 动态语义画刷；阶段同时用图标、文字和可访问描述 |
| Medium | 两程序重复按钮和输入模板 | 合并到 SharedUI；ContentPresenter 支持图标及复合内容 |
| Medium | 系统 MessageBox 与应用提示混用，默认键不明确 | USB 警告和丢弃录制确认接入 AppPromptWindow；危险确认默认取消 |
| Medium | 弹窗 Esc/关闭/焦点/名称不同 | 通过 SubWindowCloseButton 和 DialogKeyboard 统一；控件先消费 Esc |
| Medium | 设备菜单模板重复 | 主窗口、设备绑定、原生预览复用同一组菜单资源 |
| Medium | 设置、恢复、蓝牙说明长内容缺乏滚动 | 补 ScrollViewer；蓝牙通知按所在显示器工作区限制高度 |
| Medium | 动态 Markdown 与反向提示硬编码文案/画刷 | 接入既有本地化及动态主题资源 |
| Medium | 七个开发者预览目录项没有打开行为 | 已接通设备绑定、AirPlay、蓝牙连接/客户端/通知、快捷键、反向状态 |
| Medium | 部分非模态预览 Show 后设置 DialogResult 会异常 | 为相关 AirPlay/蓝牙预览采用 Close，正式模态结果不变 |
| Medium | 空候选列表缺少下一步说明 | AirPlay、蓝牙客户端增加统一空状态指引 |
| Low | 字号、间距、圆角缺少语义资源 | 新增 DesignTokens 并接入共享样式；保留有明确用途的局部尺寸 |
| Low | 主题/页面动画规则不同 | 使用共享时长，主题切换和页面过渡尊重系统动画偏好 |
| Low | 媒体画布与常规窗口风格不同 | 作为视频对比度、设备形状及原生合成的明确例外保留 |

### 3. 修改的文件、组件和重复资源

| 文件/区域 | 本次改动 |
| --- | --- |
| `src/SharedUI/Themes/DesignTokens.xaml`（新增） | 字体、字号、行高、间距、圆角；Body/Secondary/Caption/Status 与 KeyboardFocusVisual |
| `src/SharedUI/Controls/Buttons.xaml`（新增） | Secondary、Primary、Danger、Ghost、Compact 与隐式普通按钮，共用一个模板 |
| `src/SharedUI/Controls/Inputs.xaml`（新增） | 从既有 App 资源提取 TextBox、ComboBox、CheckBox、Slider 等控件资源，供两程序共用 |
| `src/SharedUI/Controls/DialogKeyboard.cs`（新增） | Esc 调用可见关闭按钮既有逻辑，保留下拉/快捷键捕获优先权 |
| `src/SharedUI/Controls/StatusAppearance.cs`（新增） | Neutral/Info/Success/Warning/Error/Disabled 动态语义色 |
| `ModernControls.xaml` | 共享文字、详情标签、工具按钮、空状态、焦点、关闭按钮名称与 Tooltip |
| `LightTheme.xaml` / `DarkTheme.xaml` | 成对补充共享状态资源；没有另建平行颜色字典 |
| `ModernAnimations.xaml` / `PageTransition.cs` | 共用动画时长，沿用既有进入动画 |
| `WindowWorkAreaController.cs` | 新增当前显示器可用 DIP 尺寸方法，供蓝牙通知计算上限 |
| 两程序 `App.xaml` / `.csproj` | 引入共享文件；删除重复普通/主按钮和输入模板，移除未使用旧 ComboBox 模板 |
| App `ThemeService.cs` / Driver `DriverThemeService.cs` | 减少动画偏好；驱动同步 WPF-UI 调色板，资源 URI 限定程序集 |
| `MainWindow.xaml/.cs` | 工具按钮、设备菜单、字号/尺寸；接通只读预览目录；USB 警告接入统一提示 |
| `AppPromptWindow.xaml/.cs` | 可滚动正文、语义图标、按钮顺序、普通/危险确认默认键 |
| `ReverseControlStatusWindow.xaml/.cs` | 滚动布局、长标题、阶段 Fluent 图标、动态画刷、本地化、禁用选项、状态可访问描述 |
| `BluetoothControlNoticeWindow.xaml/.cs` | 正文滚动、固定底部操作、当前显示器上限、次要操作在主操作之前 |
| `AirPlayDeviceSelectionWindow` / `BluetoothClientBindingWindow` / `BluetoothConnectionWindow` | 空状态、文字尺寸及预览关闭行为 |
| `AdvancedSettingsWindow` / `ImageSettingsWindow` / `CaptureRecoveryWindow` | 长内容滚动 |
| `UpdateWindow.xaml` / `MarkdownFlowDocumentRenderer.cs` | 原生进度模板，动态文档画刷/字体/空文案；去掉覆盖禁用状态的局部按钮前景 |
| `MediaOutputSettingsWindow.xaml/.cs` | 丢弃录制调用统一危险确认，按钮尺寸一致 |
| About、DeviceBinding、ProjectionSettings、ShortcutSettings、DeveloperTools、CaptureStatusNotice、ProtectedContentNotice、UsbProjectionModeInfo、StartupError、InstanceConflict 的 XAML | 字号、按钮、菜单、边距或关闭样式接入共享资源 |
| Driver MainWindow、PromptWindow、RequiredActionWindow、DeviceTrustWindow | 共享基础控件/字号；“尚未处理”改为次要按钮；Prompt 默认键 |
| App 三语言资源字典 | 本轮阶段、倒计时、配对标题、状态和空文案资源 |

验证工具为新增 `scripts/audit_ui.py`、`UiConsistencyAudit.cs`、`DriverUiConsistencyAudit.cs`、`PreviewShellUiAudit.cs`，并在 Runtime.Tests 的 `Program.cs` 接入命令。驱动 App 的内部预览标记只用于绕过硬件发现/安装启动路径。

现有逻辑/倒计时测试仅调整了必要的来源文件、本地化预期和换行兼容，没有删除时序或操作断言。工作区存在其他任务并发修改 USB、倒计时、媒体、安装/卸载及本地化；没有把整个 Git diff 归属为本轮 UI 工作，也没有回退它们。一次编译整合把本地化函数调用误用的 `const string` 改为 `var`，未扩展业务修改。

### 4. 最终 Design System

| 类别 | 规则 |
| --- | --- |
| Color | 继续使用 Background/AppBackground/WindowBackground、Surface/ControlFill、Border、Text/Muted/Disabled、Accent 和 Info/Success/Warning/Error；需随主题变化的值使用 DynamicResource |
| Typography | Segoe UI + Microsoft YaHei UI；代码 Cascadia Mono + Consolas。PageTitle 24、DialogTitle 20、SectionTitle 14、Body 13、Secondary 12、Caption 11、Button 14；正文行高 20、Caption 16；Status 为正文加 SemiBold |
| Spacing | 常用 4/8/12/16/20/24/32；PagePadding 24×20、DialogPadding 24、CardPadding 16、SectionSpacing 16、ControlSpacing 8、按钮 Padding 16×8、紧凑按钮 12×4 |
| Radius | 输入 8、按钮 12、卡片 16、对话框表面 20；系统外壳圆角交给 DWM/WPF-UI；圆形徽章按自身尺寸取半径 |
| Elevation | 复用既有卡片、对话框阴影和系统材质，不逐页新增阴影；Fluent 提示避免重复外壳阴影 |
| Button | 普通最小高度 40、紧凑 32；文字按钮自适应，图标/标题栏保留固定点击区；统一 hover/pressed/disabled/focus 和 ContentPresenter |
| Icon | 使用既有 Fluent SymbolIcon，多数功能图标 16/20；阶段以圆圈/时钟/勾/错误图标表达，不使用 emoji 替代 UI 图标 |
| Inputs | 两程序共用原有输入模板，保持选择和键盘语义，焦点可见、禁用可辨认 |
| Cards | SettingsSection、ModernDialogSurface、CardBorder 为共享基底，窗口保留内容宽度与特定布局 |
| Dialog | 标题/关闭在顶部，长正文可滚动，操作在底部/右侧；取消/关闭先于主操作；普通确认 Enter 为主操作，危险确认为取消；Esc 与现有关闭操作一致 |
| Popup | 菜单/下拉保留失焦关闭；不新增背景点击提交。PreviewTransitionMask 不聚焦、不接收点击，按原生过渡遮罩处理 |
| Status | 同一语义共用动态颜色，同时使用标题、图标或阶段描述；StatusText 提供礼貌播报资源，阶段有可访问描述，不仅靠颜色区分 |
| Loading | 未知进度用活动阶段/不确定进度；下载等有可靠百分比才使用确定进度，不为统一外观伪造进度 |
| Empty State | Secondary/EmptyStateText 说明当前缺少设备/候选项和下一步，相关操作保持禁用 |
| Animation | Fast 160ms、Normal 220ms、EaseOut；保留必要局部滚动/主题淡入时长；主题/PageTransition 尊重系统动画偏好，未添加装饰动画 |

没有强行把所有历史数字换成 token。视频画布、播放叠层、设备轮廓、菜单、数字步骤有独立需求，仍存在 10/14/18 等局部微调；这不是“零局部样式”的实现。

| 状态 | 表达和覆盖 |
| --- | --- |
| Idle / No Device | 中性说明、空状态、禁用操作；工作区/AirPlay/蓝牙客户端预览 |
| Loading / Connecting | 当前动作文字、活动阶段/不确定进度；反向准备、蓝牙等待、更新模板 |
| Connected / Success | 成功图标/文字及原有完成倒计时；状态窗口运行回归 |
| Disconnecting / Disconnected | 进行中的动作或终止原因，按原流程提供关闭/连接；会话关闭预览和阶段逻辑 |
| Recovering / Recovery Failed | 活动阶段/恢复说明，失败保留原因和操作；恢复与倒计时回归 |
| Error / Warning / 权限 / 信任 / 开发者模式 | 图标+描述+详情+下一步；前置条件、错误、信任/必要操作窗口与长反向提示 |
| Disabled / Unsupported / Busy / Mismatch | 禁用选项或明确原因提示；模板、IsEnabled 和源码路径检查，未逐项制造真实设备故障 |

### 5. 全部 UI 区域检查表

“矩阵”指三语言×两主题下真实 WPF 窗口的构造/布局/渲染；“源码”不代表真实硬件操作验证。

| UI区域 | 审查 | 发现问题 | 是否修复 | 验证结果 |
| --- | --- | --- | --- | --- |
| App MainWindow：投屏/设备/设置/输出/完整/轻量模式 | XAML、代码、动态菜单 | 重复菜单/图标按钮、尺寸 | 是 | 四工作区矩阵；动画/标题栏/主题运行回归 |
| AboutWindow | XAML/代码 | 字号/按钮 | 是 | 矩阵 |
| AdvancedSettingsWindow | XAML/代码 | 滚动约束 | 是 | 矩阵、选项卡 |
| ImageSettingsWindow | XAML/代码 | 滚动/按钮 | 是 | 矩阵 |
| ProjectionSettingsWindow | XAML/代码 | 固定文字按钮 | 是 | 矩阵 |
| MediaOutputSettingsWindow | XAML/代码 | 按钮/录制丢弃提示 | 是 | 矩阵；未处理真实录制文件 |
| ShortcutSettingsWindow | XAML/代码 | 清除按钮重复、焦点 | 是 | 矩阵、窗口运行回归 |
| DeviceBindingWindow | XAML/代码/菜单 | 重复菜单、尺寸 | 是 | 矩阵 |
| AirPlayDeviceSelectionWindow | XAML/代码 | 空指引、非模态预览关闭 | 是 | 矩阵、Esc |
| BluetoothConnectionWindow | XAML/代码 | 滚动、非模态预览关闭 | 是 | 矩阵 |
| BluetoothClientBindingWindow | XAML/代码 | 空指引、按钮 | 是 | 矩阵、绑定选择运行回归 |
| BluetoothControlNoticeWindow | XAML/代码 | 长文越界、按钮顺序 | 是 | 等待/长失败/有线前置/无线前置矩阵 |
| ReverseControlStatusWindow | XAML/代码/状态接口 | 硬编码色/文字、滚动、长标题、禁用选项 | 是 | 矩阵、长提示补测；成功/失败/恢复/人工关闭回归 |
| AppPromptWindow | XAML/代码 | 默认键/危险操作/MessageBox 混用 | 是 | 矩阵、Esc；危险默认策略源码检查 |
| CaptureRecoveryWindow | XAML/代码 | 长说明滚动 | 是 | 矩阵 |
| CaptureStatusNoticeWindow | XAML/代码 | 文字/按钮 | 是 | 捕获错误、关闭会话、USB 配置错误、反向错误矩阵 |
| ProtectedContentNoticeWindow | XAML/代码 | 文字/按钮 | 是 | 矩阵、主题运行回归 |
| UsbProjectionModeInfoWindow | XAML/代码 | 字号/按钮 | 是 | 矩阵 |
| StartupErrorWindow | XAML/代码 | 字号/按钮 | 是 | 矩阵 |
| InstanceConflictWindow | XAML/代码 | 字号/按钮 | 是 | 矩阵；未结束其他实例 |
| UpdateWindow / Markdown | XAML/动态文档 | 进度动画、画刷、禁用文字 | 是 | 矩阵、主题/按钮/滚动条回归 |
| DeveloperToolsWindow / 诊断入口 | XAML/代码 | 七个目录项无行为 | 是 | 开发者窗口回归与预览矩阵 |
| NativePreviewWindow 投屏/媒体外壳 | 原生代码/动态菜单 | 共享资源回归风险，视频有专用样式 | 复用资源 | 外壳补测；真实视频/OBS 未验证 |
| ProtectedContentOverlayWindow | 动态窗口/原生钩子 | 媒体特定设计 | 保留 | 开关/布局/穿透命中回归、原生外壳补测 |
| 设备菜单、ComboBox、速度下拉、子菜单 | 模板/入口 | 资源重复 | 是 | 可见控件展开/关闭、原生子菜单补测；空设备不能覆盖全部实际设备菜单状态 |
| PreviewTransitionMask / 播放控件 | XAML/代码 | 原生 airspace 特定约束 | 保留 | 遮罩源码、全屏背景/媒体按钮回归 |
| Driver MainWindow 简单/高级 | XAML/代码 | 重复按钮/输入、主题 | 是 | 两模式矩阵 |
| Driver PromptWindow | XAML/代码 | 按钮/默认键 | 是 | 模态矩阵、Esc |
| Driver RequiredActionWindow | XAML/代码 | 字体/按钮 | 是 | 模态矩阵、Esc |
| Driver DeviceTrustWindow | XAML/代码 | “尚未处理”危险色 | 是 | 模态矩阵、Esc |
| Driver FailureHelpWindow | XAML/代码 | 共享基础控件回归 | 共享资源统一 | 模态矩阵、Esc |
| 首次使用/权限/信任引导 | 上述实际窗口 | 样式分散 | 是 | 对应预览；未执行安装/授权/配对 |
| Toast/Banner/空/加载状态 | 全局搜索、现有提示实现 | 无独立通用 Toast，空/状态表达分散 | 修复实际界面 | 矩阵与状态回归；没有新增通知框架 |

### 6. 回归结果

证据位于 `outputs/ui-audit/`，基线为 `before.json`，最终静态结果为 `after.json`。

| 检查 | 结果与边界 |
| --- | --- |
| App / Runtime.Tests Build | 成功。完整编译有 5 处既有 nullable 警告，WPF 临时项目重复输出为 10 条；最新增量编译 0 警告/0 错误。未为 UI 审计改写这些媒体会话路径 |
| DriverInstaller Build | 成功，0 警告、0 错误 |
| XAML / ResourceDictionary | 两程序编译成功，实际矩阵窗口和动态模板加载通过 |
| App 全局矩阵 | **186 组合，0 失败、0 布局发现、0 binding diagnostics，退出码 0**；`rendered/runtime-results.json` / `runtime.log` |
| Driver 全局矩阵 | **36 组合，0 失败、0 布局发现、0 binding diagnostics，退出码 0**；`driver/driver-results.json` / `runtime-driver.log` |
| 补测 | 长反向提示与原生外壳结果在下方记录 |
| App 逻辑 | 通过，退出码 0；`logic.log` |
| Driver 逻辑 | 通过，退出码 0；`driver-logic.log` |
| App 运行时 | 通过，退出码 0；`smoke.log`，包含主题、标题栏、工作区、快捷键、蓝牙、保护层、媒体控件和反向状态 |
| 反向控制倒计时 | USB/无线/蓝牙从 5 到 1 后约 5.03–5.07 秒关闭、回调一次；失败/未解决提示保持可见，恢复重启倒计时，人工关闭/替换实例通过 |
| Light / Dark | 主题键差异 0；运行回归包含已打开窗口的主题切换 |
| zh-CN / en-US / zh-HK | 两程序资源键差异 0；矩阵覆盖六种语言/主题组合，不代表逐句人工翻译校对完成 |
| 硬编码颜色 | 非 Themes 目录 XAML 十六进制颜色为 0；不等于所有 C# 颜色常量为 0，媒体/原生画布保留例外 |
| 对比度 | 所测 18 组正文/次要/状态/主按钮/危险/禁用资源色对均 ≥4.5:1，最低浅色 Success/Background **4.76:1**；不代表透明材质或所有像素的无障碍认证 |
| DPI / 工作区 | 检查 100/125/150/175/200% 对应的 DIP 预算，非滚动操作按钮无越界；预算基于 1920×1080 扣除 40px 任务栏 |
| 键盘/无障碍 | 焦点资源、关闭名称/Tooltip、默认键源码和实际 Esc；未进行全部 Tab 顺序及屏幕阅读器人工验收 |
| Diff | `git diff --check` 通过，未新增依赖 |

抽查 PNG 包括英文浅色确认、英文深色反向状态、长蓝牙失败、蓝牙等待、英文浅色驱动高级模式、繁体深色信任窗口。文字层级、状态对比、操作顺序与主题相符；长蓝牙说明在滚动区，关闭操作仍可见。

#### 补测记录

- 长反向控制提示：**6 个语言/主题组合，0 失败、0 布局发现、0 binding diagnostics，退出码 0**。标题正常换行，长正文滚动，底部窗口取消操作可见；另实际滚动到底并断言提示的主操作完整进入可视区域，保留 `-scrolled.png`。见 `prompt-long/runtime-results.json`、`prompt-long.log` 和对应 PNG。
- 独立预览：**12 个原生外壳组合，0 失败、0 binding diagnostics，退出码 0**。覆盖三语言×两主题×投屏/媒体两种外壳，实际打开动态菜单与子菜单、进入/退出全屏，投屏外壳打开/关闭保护覆盖层。见 `preview-shell/preview-shell-results.json`、`preview-shell.log`。
- 原生补测的构造函数渲染回调为空实现；它证明真实 HWND/菜单/全屏/保护层可运行，不证明设备视频链路正常。

### 7. 已知问题、未覆盖项和保留例外

1. **AMD 原生渲染退出异常**：早期 App 批量矩阵完成界面断言后，进程退出触发 `0xC0000409`；Windows Application 日志指向 `amdxx64.dll`，版本 `32.0.21032.8002`。仅 WPF SoftwareOnly 仍会复现；纯布局矩阵在 Show 前移除原生预览子 HWND 后，186 组合正常退出。这是测试隔离，**没有声称修复生产 GPU 生命周期或驱动崩溃**。默认运行时回归仍保留原生控件并成功退出。
2. **真实 DPI/多显示器**：五档 DIP 预算不是五种物理缩放下的栅格化测试；跨屏拖动、Popup 屏幕边界、DWM 材质/阴影和更小分辨率仍需相应环境实测。已检查工作区与 DpiChanged 源码路径。
3. **真实硬件操作**：未执行 USB/无线/BLE 输入、驱动安装卸载、信任授权、开发者模式变更或真实断线恢复。错误/前置条件的预览与状态服务回归不能替代设备全链路验收。
4. **独立预览**：补测使用真实 HWND 和菜单/全屏/保护层，但渲染回调为空；真实视频、解码、OBS 捕获和 GPU 生命周期不属于外壳补测结论。
5. **设计例外**：黑色视频画布、深色媒体叠层、设备圆角/纵横比、紧凑按钮、局部微调保留；系统对话框由 Windows 绘制。减少动画改动针对主题/页面过渡，没有声称重写所有既有控件动画。
6. **可访问性**：已统一并检查焦点/名称/对比度/按键约定，仍未完成屏幕阅读器端到端人工验收；未强制所有历史 TextBlock 都换成同一个 Style。
7. **并发工作**：日志反映执行时工作区状态；其他任务后续改变服务或语言资源时，需重跑相关检查。本报告不归属其他任务的业务变更。

### 8. 重跑方式

在仓库根目录使用 PowerShell；已有 Runtime.Tests 进程结束后再构建，避免 Windows 文件锁。

```powershell
python scripts/audit_ui.py --output outputs/ui-audit/after.json
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -v quiet -p:IncludeBundledFfmpeg=false
dotnet build src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj --no-restore -v quiet
$uiRuntime = 'src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe'
& $uiRuntime --ui-audit outputs/ui-audit/rendered
& $uiRuntime --ui-audit outputs/ui-audit/prompt-long reverse-control-prompt-long
& $uiRuntime --driver-ui-audit $PWD.Path 'src/DriverInstaller/bin/Debug/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll' outputs/ui-audit/driver
& $uiRuntime --preview-shell-ui-audit outputs/ui-audit/preview-shell
& $uiRuntime
git diff --check
```

新增长提示后，后续完整 App 矩阵为 32 场景×3 语言×2 主题，即 192 组合。本轮 186 完整组合与最后新增长提示分别保留日志，不混报为同一次运行。
