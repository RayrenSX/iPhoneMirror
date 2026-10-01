# 弹窗与子窗口圆角核对

> 后续文字、控件自适应与裁切修复见 [自适应布局复审](UI-ADAPTIVE-LAYOUT-AUDIT.md)。

日期：2026-09-30。接续全局 UI 审计，按“尽可能使用自绘圆角”的要求实施。没有使用 Computer Use，没有引入新依赖。

## 发现与修复

1. 主程序多数子窗口依赖 DWM 圆角，内部外框却明确设置 `CornerRadius=0`；与驱动弹窗、蓝牙通知的自绘外框不同。
2. FluentWindow 会在初始化时配置原生 WindowStyle/WindowChrome，单独给它加圆角 Border 无法保证透明角部，也可能形成双重边框。
3. 驱动弹窗已有 10 DIP 外边距，蓝牙通知则没有阴影预留区，阴影与外壳规则不一致。
4. 主菜单、子菜单、播放速度下拉使用不同的外层半径。

已将 **25 个普通 WPF 子窗口**统一接入共享 `RoundedWindow`：透明 HWND、自绘 20 DIP 圆角、1 DIP 主题边框、10 DIP 阴影预留区。正文通过 `RoundedContentClip` 按内边界裁剪，防止子元素和动画把角部重新画成直角。原来内容 Border 只负责间距，不再重复绘制外框。

原来不留阴影的主程序窗口增加了外框预算，尽量保留原来的内容可用尺寸；驱动弹窗复用原有预算。蓝牙通知的动态测量同步扣除新外框空间。

可调整大小的窗口保留 6 DIP 缩放边缘和既有标题拖动/双击最大化行为。最大化在当前显示器工作区内使用直角、无阴影、无浮动外边距；还原后恢复圆角。

## 圆角规则

| UI 类型 | 绘制方式 | 半径/规则 |
| --- | --- | --- |
| 普通弹窗、设置与诊断子窗口 | 共享透明窗口 + WPF 自绘外框 | 20 DIP，内容按 19 DIP 内圆角裁剪 |
| 普通子窗口最大化 | 同一模板的状态切换 | 0 DIP，去边框/阴影/外边距 |
| 主菜单、子菜单、播放速度下拉 | Popup 内自绘 Border | 统一 `PopupCornerRadius=12` |
| 菜单项高亮 | 自绘 Border | 统一使用控件半径 8 DIP |
| 普通 ComboBox | 复用 WPF-UI 的透明 Popup 模板 | 读取同一 `PopupCornerRadius=12`，已实际展开验证 |
| ToolTip | 复用 WPF-UI 的 WPF 模板 | 保留提示气泡的 4 DIP 自绘圆角，无额外 DWM 外框 |
| ModernDialog 内容控件 | 既有自绘 Border | 引用 `DialogCornerRadius=20` |

## 完整窗口清单

下列窗口均改为 `chrome:RoundedWindow`，通过同一个共享模板绘制外框，代码中的数据、动作和窗口所有权流程保持原有实现。

| 项目 | 窗口 | 最终方式 |
| --- | --- | --- |
| App | AboutWindow | 自绘圆角，可缩放 |
| App | AdvancedSettingsWindow | 自绘圆角 |
| App | AirPlayDeviceSelectionWindow | 自绘圆角 |
| App | AppPromptWindow | 自绘圆角 |
| App | BluetoothClientBindingWindow | 自绘圆角 |
| App | BluetoothConnectionWindow | 自绘圆角 |
| App | BluetoothControlNoticeWindow | 自绘圆角，动态高度同步适配 |
| App | CaptureRecoveryWindow | 自绘圆角 |
| App | CaptureStatusNoticeWindow | 自绘圆角 |
| App | DeveloperToolsWindow | 自绘圆角，可缩放 |
| App | DeviceBindingWindow | 自绘圆角，可缩放 |
| App | ImageSettingsWindow | 自绘圆角，可缩放 |
| App | InstanceConflictWindow | 自绘圆角 |
| App | MediaOutputSettingsWindow | 自绘圆角，可缩放 |
| App | ProjectionSettingsWindow | 自绘圆角，可缩放 |
| App | ProtectedContentNoticeWindow | 自绘圆角 |
| App | ReverseControlStatusWindow | 自绘圆角 |
| App | ShortcutSettingsWindow | 自绘圆角 |
| App | StartupErrorWindow | 自绘圆角，可缩放 |
| App | UpdateWindow | 自绘圆角，可缩放 |
| App | UsbProjectionModeInfoWindow | 自绘圆角 |
| DriverInstaller | DeviceTrustWindow | 迁移至同一自绘外框 |
| DriverInstaller | FailureHelpWindow | 迁移至同一自绘外框 |
| DriverInstaller | PromptWindow | 迁移至同一自绘外框 |
| DriverInstaller | RequiredActionWindow | 迁移至同一自绘外框 |

## 保留的原生/特殊窗口

- App 主窗口、驱动管理器主窗口不属于此次普通子窗口迁移，保留既有系统外壳。
- 独立投屏 NativePreviewWindow 使用原生 DirectComposition / HWND；设备轮廓已有原生区域和着色器处理。媒体播放的原生独立窗口保留系统标题栏/圆角，避免把视频承载窗口改成分层透明 WPF 窗口。
- ProtectedContentOverlayWindow 是匹配预览画布的不可交互覆盖层，保留与画布一致的边界，不增加一张悬浮圆角卡片。
- PreviewTransitionMask 是匹配预览表面的过渡遮罩，保留与原生预览边界一致的局部半径。
- Windows 文件选择、系统配对、UAC 等系统 UI 仍由 Windows 绘制。

## 关键实现与验证

- `src/SharedUI/Controls/RoundedWindow.cs`：透明子窗口、原生缩放命中、最大化工作区、内容裁剪。
- `src/SharedUI/Controls/ModernControls.xaml`：唯一自绘外框模板；正文容器 `RoundedWindowContent`；最大化触发器。
- `src/SharedUI/Themes/DesignTokens.xaml`：20 DIP 对话框圆角、12 DIP Popup 圆角、10 DIP 外框阴影预算。
- 两项目 `.csproj` 链接共享类；25 组 XAML/后台基类接入；App 菜单/播放速度 Popup 使用共享圆角资源。
- `src/App.Runtime.Tests/RoundedWindowAudit.cs`：校验透明背景、外框半径、内容 Clip、实际渲染角部 alpha、缩放命中、最大化/还原。
- 全局矩阵对每个普通子窗口执行圆角断言，继续检查文字按钮、长内容、三语言/双主题、五档可用 DIP 预算和 Esc。
- 原有要求 DWM 圆角的测试按新的明确需求改为检查共享自绘外框，保留动作与时序断言。

### 本轮结果

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 静态清单 | 25 个普通子窗口全部为 RoundedWindow，主题键一致 | `rounded-static.json` |
| 主程序完整矩阵 | **192 个场景组合，0 失败、0 布局发现、0 绑定诊断，退出码 0** | `rounded/runtime-results.json`、`rounded-runtime.log` |
| 驱动工具矩阵 | **36 个组合，0 失败、0 布局发现、0 绑定诊断，退出码 0** | `rounded-driver/driver-results.json`、`rounded-driver.log` |
| 原生预览菜单及 Popup 补测 | **12 个外壳组合，0 失败、0 绑定诊断，退出码 0**；六组语言/主题同时检查普通 ComboBox 和 Tooltip | `rounded-preview-shell/preview-shell-results.json`、`rounded-preview-shell.log` |
| App/Driver 编译 | 成功；App 全量编译仍有 5 处既有 nullable 警告（WPF 临时编译可能重复输出），Driver 0 警告/0 错误 | `build-rounded.log`、`build-rounded-driver.log` |
| App 逻辑回归 | 通过，退出码 0 | `rounded-logic.log` |
| App 运行时回归 | 通过，退出码 0；覆盖开发者窗口圆角、工作区、关闭操作、主题、蓝牙通知收缩和反向控制倒计时 | `rounded-smoke.log` |
| Diff 检查 | `git diff --check` 通过 | Git 工作区 |

证据目录为 `outputs/ui-audit/`，本轮文件使用 `rounded` 前缀。PNG 保留实际透明通道，因此可检查角部 alpha，不再给透明窗口先铺矩形主题底色。抽查了确认弹窗、反向状态、关于/诊断窗口和驱动提示等渲染结果。

普通 ComboBox 使用 WPF-UI 的 `Popup` 部件名，并非标准模板常见的 `PART_Popup`；补测适配其真实模板后，确认它已采用透明 Popup 和共享 12 DIP 圆角。没有为此复制第三方控件模板。

### 验证边界

角部 alpha 与窗口布局使用真实 WPF 窗口渲染检查，不依赖桌面截图。五档 DIP 预算不等于在五种物理显示器缩放下完成验收；真实跨屏拖动、系统材质、硬件视频和远程桌面性能仍需要对应环境验证。本轮没有执行真实驱动安装、设备配对或输入控制。
