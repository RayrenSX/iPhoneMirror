# 键盘映射向导重构

2026-10-05。目标是降低创建与编辑映射的单页信息量；保留现有映射模型、配置格式和触控执行器。

## 实施前审查

| 问题 | 原有实现 |
| --- | --- |
| 编辑 Window / View | `Windows/KeyboardMappingEditorWindow.xaml`，独立的 RoundedWindow |
| ViewModel | 原来没有编辑器专用 ViewModel，草稿、控件与校验都在 code-behind |
| 新建 | 管理窗口 `Edit(null)`，编辑器维护新条目，保存回调更新列表 |
| 编辑 | 管理窗口 `Edit(entry)`，加载原 Id、启用状态、物理键和参数 |
| 操作类型 | Tap、LongPress、HoldUntilRelease、DoubleTap、Swipe 与四个固定方向；没有文本映射或自由轨迹执行类型 |
| 参数 | 长按时长、双击间隔、滑动方向；滑动时长来自拖动录制 |
| 取点 | 全部现有操作需要位置；滑动还需要终点 |
| 手势 | 滑动用起点、终点、时长表示，保留现有执行语义 |
| Preview | 原生预览 HWND 上的透明 Overlay；MappingPreviewSurface 和 PreviewCoordinateMapper 处理方向、黑边与归一化 |
| Hook | 共享 WH_KEYBOARD_LL → KeyboardMappingCapture，KeyUp 确认，代次取消迟到回调 |
| 输入路由 | KeyboardInputRouter 仲裁 Mapping / Direct，沿用 USB、无线、Bluetooth 能力门控 |
| 保存 | 编辑器回调 → KeyboardMappingWindow.Save → MainWindow.ApplyKeyboardMapping → settings.json |
| 复用样式 | RoundedWindow、CardBorder、PrimaryButton、GhostButton、共享字体/圆角/主题资源、Fluent 图标、FastAnimationDuration |

## 结构与行为

- `Services/KeyboardMappingWizardState.cs` 保存单一草稿、动态步骤、完成状态、当前步骤校验、重复键替换授权和 dirty 状态。
- `Windows/MappingWizard/` 包含按键、操作、参数、取点和确认五种轻量视图，以及短暂的页面过渡。
- 一个编辑 Window、一个固定导航栏。点击/按住为四步；长按/双击/滑动为五步。滑动参数页只有方向，时长在录制时生成。
- 编辑会加载原有数据；未修改直接关闭。明确放弃前草稿不丢失；失败保存不关闭；成功保存显示约 700 ms 后关闭。
- 按键冲突在第一步处理；未完成步骤不能跳过，已完成且前置条件仍有效的步骤可返回。
- 小窗口把完整指示器换为步骤计数和标题，并减少重复标题与间距。正文可以滚动，导航不随正文移动。
- Enter / Esc / Alt+左 / Alt+右用于向导导航。物理按键录入优先拥有 Enter/Esc，支持继续把这些键用作映射；录入结束后恢复导航。编辑已有有效按键时不强制重新录入。

## Preview 与输入边界

按用户要求，坐标和手势继续在原来的投屏预览框内完成。向导没有嵌入预览、不拷贝视频帧、不新建解码器或设备连接。取点步骤提供一个开始/重新取点按钮和完成后的简短摘要，调用原有 `BeginMappingPositionPick`。管理窗口与编辑窗口在取点期间隐藏，Overlay 完成或取消后恢复原步骤。

原 Preview Overlay 使用现有黑边/方向转换及固定方向约束。点击只编辑草稿，不向手机发送触控。拖动显示实时直线路径并记录时长；完整轨迹点不加入原本只执行直线滑动的数据模型。取点前不显示坐标，取点后显示像素摘要；旧映射在缺少设备尺寸时保留原来的百分比表示。

窗口只用一个 500 ms 状态计时器检查现有 Preview 是否仍可用。关闭时停止计时器、取消录入和取点，旧的异步回调不会重新打开窗口或覆盖后续步骤。断线时阻止继续；同设备恢复后保留草稿，设备、尺寸或方向设置变化则要求重新取点。取消重新取点保留先前有效的坐标/手势。

编辑器存活期间，映射执行及设备键盘直发明确受编辑状态门控，已有按键录入仍复用共享 Hook。没有增加独立 Hook，也不改动映射执行器、USB/无线/Bluetooth 发送协议或配置 schema。

## 验证方式

新增 `--keyboard-mapping-wizard <输出目录>`：

- 九种枚举动作、动态步骤、参数边界、重复键/快捷键冲突、替换授权失效、编辑 Id/启用状态保留。
- 真实 WPF 窗口的新建、编辑、返回、取消、失败保存、成功延迟、迟到按键回调与计时器释放。
- 原 Preview 的取点交接、窗口隐藏/恢复、录制时长、取消重录、关闭后迟到回调；确定性会话状态下的断线/恢复，以及编辑期间键盘输入互斥。
- 简中、繁中（台湾/香港）、英文 × 深浅主题 × 正常/最小/最大化，以及 100%/125%/150% 的 WPF 布局缩放检查和 PNG 渲染。

缩放测试不修改用户 Windows 显示设置，不能替代跨显示器的真实 Per-Monitor DPI 验收。设备断线与恢复通过确定性 Preview 状态验证，不宣称等同于手机拔插、有线/无线切换或物理键盘端到端验收。

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -v:minimal -p:OutputPath=C:\Users\Ray\Documents\iphoneMirror\artifacts\wizard-bin\
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-mapping-wizard artifacts/keyboard-mapping-wizard
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-mapping artifacts/wizard-regression
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-mapping-picking artifacts/wizard-original-preview
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-mapping-lifecycle artifacts/wizard-lifecycle
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-router
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --keyboard-ownership
dotnet artifacts/wizard-bin/IPhoneMirror.App.Runtime.Tests.dll --localization-audit
```

桌面焦点测试必须串行执行；若 Windows 拒绝测试窗口获得前台，测试会明确失败，不把未执行部分认定为通过。

## 本轮结果

| 检查 | 结果 |
| --- | --- |
| 应用与 Runtime.Tests 编译 | 通过；NuGet 漏洞信息服务器不可达产生 NU1900，不涉及编译错误 |
| Wizard | 状态、原预览交接、增删改与保存失败、输入互斥、四种语言/两种主题/布局缩放通过，截图位于 `artifacts/keyboard-mapping-wizard` |
| 输入路由 | `--keyboard-router`、`--keyboard-ownership` 通过，包括 USB/无线封包与 Bluetooth 队列/目标门控；使用测试传输，不是真机输入验收 |
| Preview 生命周期 | `--keyboard-mapping-lifecycle` 通过，覆盖真实 HwndHost/独立预览外壳及惯性会话夹具中的缩放、全屏、切换、方向、断线/恢复和关闭清理 |
| 本地化 | `--localization-audit` 通过：26380 格式化案例、5 次字典切换、15 个控件流程 |
| 完整映射套件 | 配置、85 个键案例、捕获事务、坐标、动作、USB/无线封包与实际 Hook 回调通过；随后真实前台焦点切换失败而中断 |
| 原 Overlay 原生鼠标测试 | 坐标/方向/DPI 数学验证通过；原生点击消息未进入 Overlay 鼠标事件，且前台不是测试 Overlay，因此该桌面交互验收未通过，需可交互桌面复验 |

未把物理键盘、真实手机拔插、有线/无线/Bluetooth 真机操作或真实多显示器 DPI 切换标记为已完成。没有更改生产设置、驱动或桥接器。

## 审查修复

- 管理窗口关闭时先请求编辑器关闭，避免 WPF 关闭所属窗口时绕过 `Closing`。有修改时保留两个窗口并显示放弃确认；继续编辑会取消原关闭请求，明确放弃后完成原关闭请求。
- 主窗口退出也先检查映射草稿，在确认前不启动应用清理。取点期间被隐藏的管理窗口和编辑器会在取消取点后恢复，确保确认可见。
- 确认页的位置因设备切换、尺寸或方向变化而失效时，显示“位置已失效。请返回上一步重新取点。”四种语言均已补齐；状态计时器不会清除提示，重新取点后恢复保存。
- `KeyboardMappingWizardRegressionTests.cs` 已加入现有向导测试入口，覆盖新建/编辑的父窗口关闭、隐藏取点窗口恢复、继续编辑/放弃、主窗口退出门控，以及旋转/设备切换/重连后的提示和恢复。原按键、操作及参数在恢复过程中保留。
- 最小窗口的错误提示截图：`artifacts/keyboard-mapping-wizard/wizard-position-invalid.png`。
- 修复验证通过：应用与 Runtime.Tests 构建、完整向导套件（包含新增回归、深浅主题、四种语言及布局缩放）、本地化审计（26400 个格式化案例、5 次字典切换、15 个控件流程）。日志为 `artifacts/wizard-review-tests.log` 和 `artifacts/wizard-review-localization.log`。构建仍有 NuGet 漏洞信息服务器不可达的 NU1900 警告。
