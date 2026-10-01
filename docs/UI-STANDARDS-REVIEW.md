# UI 界面规范复审（2026-10-01）

本轮在已有设计系统、圆角专项和自适应布局修复上继续审查，并落实新的共性问题。已完成确认问题的界面修复与专项验证；完整 smoke 尚有一项并行新增的光标断言未通过，详见末尾。依据包括实际 XAML、共享样式、动态窗口代码、真实 WPF 窗口实例和 RenderTargetBitmap 渲染；未使用 Computer Use，未增加框架或第三方依赖。

此前记录：[全局一致性审计](UI-CONSISTENCY-AUDIT.md)、[自绘圆角专项](UI-ROUNDED-WINDOW-AUDIT.md)、[自适应布局专项](UI-ADAPTIVE-LAYOUT-AUDIT.md)。本报告记录本轮增量，不把并行修改的业务代码归入 UI 优化。

## 范围

静态清单覆盖 App、DriverInstaller 和 SharedUI 全部 XAML 与资源字典，共 30 个带 `x:Class` 的根节点：28 个窗口、2 个 Application。另检查独立原生预览、媒体预览、保护覆盖层、菜单/子菜单、ComboBox 和 Tooltip 模板路径。新增的 `ParentDriverWindow` 已纳入，不沿用上一轮的旧窗口数量。

运行场景覆盖主窗口五种工作区、全部普通子窗口、反向控制前置条件/错误/长提示、蓝牙等待/长错误、驱动简单/高级视图、长确认文本、新增父级驱动窗口及其危险确认提示。窗口矩阵加载简体中文、英文、繁体中文和 Light/Dark 两种主题。

## 发现与处理

| 级别 | 问题 | 实施结果 |
| --- | --- | --- |
| Critical | 本轮没有确认此级别 UI 缺陷 | 结论限于实际检查范围 |
| High | Button/CheckBox 的字符串 Content 经 AccessText 渲染时，字号被第三方 AccessText 默认样式和应用 TextBlock 样式覆盖，控件设置大字体也不生效 | 新增共享 `ControlAccessText` / `ControlAccessTextLabel`，字体、字号、字重、字形和前景色随所属控件；实际渲染字号加入回归断言 |
| Medium | 只读输入框整体透明度 0.7，诊断信息和路径对比度被无故降低；禁用输入、复选框也依赖整体透明度 | 只读内容保持完整可读和可选；禁用改用既有语义前景/背景画刷 |
| Medium | 复选框点击高度不足，键盘焦点弱；三态值没有对应的中间状态标记 | 最小高度 32 DIP、透明命中区域、共享焦点样式、按下反馈与中间状态横线 |
| Medium | 多处 WrapPanel 仅有横向按钮间距，长文字导致换行后上下贴紧 | 统一 `DialogActions` / `ControlActions` 与动作边距，行列均保留 8 DIP 间距 |
| Medium | 新父级驱动列表使用系统强调色，选中项可变成大块红色；列表间的选中、焦点、内边距不一致 | `SelectableListItem` 统一父级驱动、驱动设备和 AirPlay 设备列表，使用应用选中色与焦点 |
| Medium | 部分卡片的标题/状态、设备名/连接状态叠在同一 Grid 单元；水平 StackPanel 中的状态文字没有换行宽度 | 标题和状态分栏，长状态改由 DockPanel 分配剩余宽度；省略的驱动设备名称补全文 Tooltip |
| Low | 说明、能力信息、设备标识和状态文字混用 9/10 DIP | 普通 TextBlock 统一到 CaptionFontSize=11；保留有明确用途的密集日志 TextBox 和紧凑底栏按钮 |
| Low | 新父级驱动窗口关闭符号、正文层级、选择列表和入场动画未全部接入共享规范 | 改为 Dismiss20、共享正文/标题样式、可访问列表名称、共享入场动画与动作区 |

## 文件与共享资源

| 文件 | 本轮修改 |
| --- | --- |
| `src/SharedUI/Themes/DesignTokens.xaml` | 输入内边距、动作间距资源；AccessText 及其内部文字的字体/颜色绑定 |
| `src/SharedUI/Controls/Inputs.xaml` | 输入框字号/高度/圆角 token；只读/禁用可读性；复选框命中范围、焦点、三态与字符串标签 |
| `src/SharedUI/Controls/Buttons.xaml` | ContentPresenter 字体传递、共享 AccessText，保留访问键和复合 Content |
| `src/SharedUI/Controls/ModernControls.xaml` | 统一的左右动作组、可选择列表项模板 |
| `src/App/MainWindow.xaml` | Caption 字号、标题/状态分栏、长状态换行、蓝牙设置动作间距 |
| App `AppPromptWindow` / `ReverseControlStatusWindow` | 弹窗/提示动作组间距，技术详情字号 |
| App `DeviceBindingWindow` / `DeveloperToolsWindow` | 绑定/预设动作换行间距；开发工具说明字号 |
| App `AirPlayDeviceSelectionWindow` | 复用共享列表项，名称/型号换行，限制横向滚动 |
| App `AboutWindow` / `BluetoothClientBindingWindow` / `ProjectionSettingsWindow` / `MediaOutputSettingsWindow` | 普通说明、标识、能力信息和状态字号 |
| Driver `MainWindow` | 共享列表项、标题工具间距、设备名与连接状态分栏、全文 Tooltip、Caption 字号 |
| Driver `ParentDriverWindow` | 图标/字体/动画/列表/动作规范，自绘窗口与正文滚动继续保留 |
| Driver `PromptWindow` / `RequiredActionWindow` | 统一弹窗动作间距 |
| `src/App.Runtime.Tests/ControlStateAudit.cs` / `Program.cs` | `--control-states` 验证实际字号、只读选择、焦点、三态、禁用颜色、动作换行距离；`--ui-regression` 可独立运行既有界面回归，不改变完整 smoke 的断言 |
| `src/App.Runtime.Tests/AdaptiveLayoutAudit.cs` | 区分收起 Expander 的有意隐藏与真正裁切；展开内容继续测量并保存渲染图 |
| `src/App.Runtime.Tests/DriverUiConsistencyAudit.cs` | 父级驱动长文本 fixture、应用选中色断言，主窗口填充长设备名 fixture |

合并了驱动设备、AirPlay 与新父级驱动列表的重复/默认项表现。动作组继续保留各页面原本的顺序、事件、Command、默认按钮和取消行为。

## 当前统一规范

| 角色 | 规范 |
| --- | --- |
| Color | Light/Dark 成对主题资源；Text/Muted/Disabled、Accent、Success/Warning/Error/Info；状态继续同时用文字/图标表达 |
| Typography | Page 24、Dialog 20、Section 14、Body 13、Secondary 12、Caption 11、Button 14 DIP；Segoe UI / Microsoft YaHei UI；技术标识使用 CodeFontFamily |
| Spacing | 基于 4/8/12/16/20/24；动作之间横纵均 8；InputPadding 12,8；页面/弹窗和卡片沿用原有 token |
| Radius | 普通子窗口自绘 20，Popup 12，按钮 12，输入/选择列表项 8，卡片 16 DIP；最大化窗口去掉悬浮外框 |
| Buttons / Inputs | 普通控件最小高度 40，紧凑控件/复选框最小高度 32；文字增长时采用自然高度与换行 |
| Icon | Fluent SymbolIcon，关闭统一 Dismiss20；图标尺寸按标题栏、工具栏和状态角色保留 |
| Dialog | 共享 RoundedWindow 外壳/标题/关闭；正文有界滚动；动作可换行；已有默认键和 Esc 行为保留 |
| Status | 沿用 StatusAppearance 和动态语义色，不新增平行状态系统 |
| Animation | 沿用共享短入场/过渡及系统减少动画偏好；父级驱动窗口补入同一规范 |

## 一致性检查表

| UI 区域 | 审查内容 | 本轮修复 | 验证 |
| --- | --- | --- | --- |
| MainWindow | 投屏/设备/设置/输出/双侧栏与预览工具栏；空状态、状态条、下拉 | 字号、标题/状态宽度、长状态、按钮换行间距 | App 矩阵 |
| AboutWindow | 关于/更新设置/诊断/日志选项卡 | 标识字号；共享只读样式可读性 | App 矩阵、运行时回归 |
| AdvancedSettingsWindow | 设置项、输入、复选框、滚动 | 应用共享输入规范 | App 矩阵 |
| AirPlayDeviceSelectionWindow | 空列表、选中项模板、名称/型号布局 | 共享列表项、换行与焦点 | App 矩阵及共享列表模板验证 |
| DeviceBindingWindow | 有线/AirPlay/蓝牙绑定卡片 | 动作行列间距 | App 矩阵 |
| BluetoothConnectionWindow | 连接信息、操作、滚动 | 共享控件字体继承 | App 矩阵 |
| BluetoothClientBindingWindow | 客户端信息、状态、标识、操作 | Caption 字号 | App 矩阵 |
| BluetoothControlNoticeWindow | 等待、长失败信息、步骤、动作 | 共享按钮文字 | App 矩阵 |
| ReverseControlStatusWindow | 有线/无线前置条件、运行/失败、长交互提示 | 动作间距、技术说明字号 | App 矩阵、倒计时回归 |
| AppPromptWindow | 确认、长正文、长主操作、关闭 | 动作间距、字体自适应 | App 矩阵、控件状态检查 |
| CaptureRecoveryWindow / CaptureStatusNoticeWindow | 恢复、反向控制错误、捕获错误、断开、USB 配置错误 | 沿用共享控件与自绘外壳 | App 矩阵 |
| ImageSettingsWindow | 图像选项、滚动与动作 | 共享输入规范 | App 矩阵 |
| ProjectionSettingsWindow | 有线/无线/蓝牙设置及状态 | Caption 字号、共享输入 | App 矩阵 |
| MediaOutputSettingsWindow | 录制/流媒体/虚拟摄像头选项卡 | 能力说明字号、共享输入 | App 矩阵 |
| ShortcutSettingsWindow | 捕获输入、状态与动作 | 共享输入规范 | App 矩阵、键盘回归 |
| DeveloperToolsWindow | 目录入口、参数、预设、诊断 | 说明字号、预设动作间距 | App 矩阵 |
| StartupErrorWindow | 摘要、路径、展开详情、关闭 | 共享字体与只读规范 | App 矩阵 |
| UpdateWindow | 发布说明、更新动作、主题切换 | 共享按钮文字 | App 矩阵、主题文字回归 |
| InstanceConflictWindow / UsbProjectionModeInfoWindow | 说明、状态、动作 | 沿用共享规范 | App 矩阵 |
| ProtectedContentNoticeWindow | 保护提示、说明与动作 | 沿用共享规范 | App / 原生预览矩阵 |
| Driver MainWindow | 简单/高级界面、长设备名、工具栏 | 列表项、分栏、字号、间距 | Driver 矩阵 |
| Driver ParentDriverWindow / ParentConfirm | 长设备名/ID、长候选驱动、诊断、滚动、底部动作及危险确认文案 | 新增窗口全套规范接入 | Driver 矩阵与选中色断言 |
| Driver PromptWindow / RequiredActionWindow | 标准提示、长正文/长动作 | 动作间距 | Driver 矩阵 |
| Driver DeviceTrustWindow / FailureHelpWindow | 信任、帮助、错误、滚动和退出 | 共享控件规范 | Driver 矩阵 |
| NativePreviewWindow / ProtectedContentOverlayWindow | 原生投屏/媒体外壳、全屏恢复、覆盖层、菜单/子菜单 | 保留原生渲染边界和既有圆角规范 | 原生预览专项 |
| ComboBox / Tooltip / 菜单 Popup | 模板、主题资源、打开与关闭 | 回归既有共享资源 | App / 原生预览专项 |

## 验证记录

本轮证据保存在 `outputs/ui-audit/standards-review/`。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| App / Runtime.Tests 编译 | 成功，0 警告、0 错误 | `build-app-final.log` |
| DriverInstaller 编译 | 成功，0 警告、0 错误 | `build-driver.log` |
| App 全量矩阵 | 210 组合，0 窗口失败、0 绑定诊断；4 条收起详情的动画裁切记录经下项专项复核排除 | `app/runtime-results.json` |
| 启动错误收起/展开专项 | 三语言 × 两主题，6 组合，0 失败、0 布局问题、0 绑定诊断；新增展开渲染证据 | `startup-recheck/runtime-results.json` |
| Driver 最终矩阵 | 60 组合，0 失败、0 布局问题、0 绑定诊断；含父级驱动和危险确认文案 | `driver-final/driver-results.json` |
| 原生预览 / Popup | 12 组合，0 失败、0 绑定诊断 | `preview/preview-shell-results.json` |
| 控件状态与实际字号 | 两主题 × 13/22 DIP 字号；只读选择、焦点、三态、禁用可读性、换行间距通过 | `control-states-final.log`、`control-states/` |
| 界面运行时回归 | 通过；主题/主操作文字、倒计时、保护窗口、标题栏、开发工具与共享控件 | `ui-regression.log` |
| 工作区位置与动画 | 通过 | `workspace.log` |
| USB / Wireless / Bluetooth 键盘焦点 | 通过，测试数据包仅在内存中捕获 | `keyboard.log` |
| App 逻辑回归 | 通过 | `logic-final.log` |
| Driver 逻辑回归 | 通过 | `driver-tests.log` |
| 静态清单 | 30 个 XAML 根，主题键差异为 0 | `static.json` |
| 格式检查 | `git diff --check` 通过 | `diff-check.log` |

全量检查的 4 条记录都来自已收起 Expander 内部仍在视觉树中的动画内容，没有 `/expanded` 记录。检查现已只跳过收起内容，不跳过其标题或展开内容；专项复核同时保存实际展开图，避免把隐藏动画误判为正文不可达。早期并行键盘/Bluetooth 源码变更曾导致编译和逻辑检查暂时失败，最终编译与逻辑检查已通过，未修改这些业务逻辑来绕过检查。

## 尚未通过的检查

完整默认 Runtime.Tests smoke 停在并行新增的 `CursorRegressionTests.TestReverseControlCursorShapes`：`WndProc must replace cursor 32644 with the system arrow`（见 `smoke.log`）。该测试直接调用未连接可视窗口的预览消息处理函数。本轮没有修改相关指针业务代码或删除断言，不能宣称完整 smoke 通过。已有界面回归另通过 `--ui-regression` 执行；原生预览矩阵和 USB/无线/蓝牙焦点专项仍保留各自结果。

## 验证边界与保留项

- DPI 检查采用真实 WPF Measure/Arrange 和 100%、125%、150%、175%、200% 对应的工作区 DIP 预算，包括高 DPI 下 80% 工作区限制；不等同于五种物理 DPI、多显示器或系统字体缩放的人工验收。
- 控件专项额外使用 13/22 DIP 实际字体，检查模板内文字确实跟随字号变化；正常省略、输入内部滚动、列表/正文滚动不算裁切。
- 普通子窗口继续验证角部透明、自绘圆角、缩放/最大化还原和 Esc；系统文件选择器、UAC、蓝牙配对等由 Windows 管理。
- UI 矩阵以预览和合成 fixture 驱动，不触发真实驱动绑定/重建，不发出设备控制指令。驱动逻辑测试可读取当前设备枚举结果。未以本轮 UI 回归代替真实投屏、硬件输入和驱动操作测试。
- 保留 About 密集日志 TextBox 的 10 DIP 等宽字体及主窗口紧凑底栏按钮的 10 DIP；其视口/交互角色与普通帮助文字不同。
- 运行时渲染图不能证明 Mica 桌面合成材质或真实视频画面质量；原生预览外壳另行验证。
