# 主界面展开/收起动画优化记录

验证日期：2026-10-03。保留工作区原有的面板尺寸、卡片样式、间距、文字、绑定及 Command；未引入依赖。工作区中同时存在的语言、键盘映射等其他修改未被回退。

## 1. 原因与检查范围

主界面「投屏来源」「投屏」「投屏设置」是横向工作区面板，不是纵向 `Expander`。检查了 `MainWindow.xaml`、主窗口工作区/轻量窗口尺寸代码、自适应布局、共享动画/控件/主题资源、`PageTransition`、`ThemeService`、`LocalizationService` 及现有工作区测试。

- 原先直接动画化承载内容的 Grid/Border 的 Width。设置内的 ScrollViewer、换行文字和控件随每帧宽度变化重新测量、排列；缩到很窄时会发生不自然的换行和内容挤压。实际基线测量确认了重复布局，而非仅从代码推测。
- 面板、列间距、透明度和 TranslateTransform 分别使用动画；完整模式 240 ms，轻量窗口尺寸 260 ms。后者还有独立的 60 Hz 节流，内容与窗口不共用进度。
- 轻量模式在 `CompositionTarget.Rendering` 内设置窗口和面板尺寸。该事件位于布局之后，尺寸变化可能再触发布局；原订阅已有重复订阅保护，未将它误判为确定的内存泄漏。
- 完整模式以 `ActualWidth` 捕获反向动画起点。一次布局前的连续点击可能读到旧的排列结果，而非当前有效宽度。
- 窄窗口堆叠判断依赖动画中的 `ActualWidth > 1`，面板 SizeChanged 又会重新执行工作区自适应逻辑，容易在动画过程中变更布局结构。
- 首次显示原本 Collapsed 的设置卡片，需要创建模板并测量；在这一步之前启动时钟，会让首次布局消耗动画时间。

未发现展开动画每帧主动执行设备枚举、数据刷新或其他业务逻辑的证据。主界面的导航栏 IsPaneOpen、下拉菜单/Popup 使用现有 WPF-UI/控件机制；未把它们改造成工作区面板，也未给条件显示的业务区域添加额外动画。其他窗口的标准错误详情 Expander 不属于此次主界面修改范围。

## 2. 修改文件

| 文件 | 此次改动 |
| --- | --- |
| `src/App/MainWindow.xaml` | 左侧公共容器与设置外层使用裁剪视口；原设置 Border 保留为 ControlPanelContent；增加页面卸载/隐藏处理 |
| `src/App/Controls/WorkspaceRevealPanel.cs` | 固定内容测量/排列宽度，返回视口实际尺寸并裁剪 |
| `src/SharedUI/Animations/RevealTransition.cs` | 可取消、可替换的单动画时钟；延后到首次布局完成后启动；完成时释放时钟与回调 |
| `src/App/MainWindow.WorkspaceTransitions.cs` | 统一面板、间距、页面切换、轻量 HWND 的进度与生命周期 |
| `src/App/MainWindow.xaml.cs` | 接入统一机制，删除旧独立宽度/间距/位移动画和 Rendering 循环；保留轻量窗口几何策略 |
| `src/App/MainWindow.AdaptiveLayout.cs` | 以请求的展开状态决定堆叠，避免依赖中间宽度；仅在标题布局模式变化时更新相关属性 |
| `src/App/iPhoneMirror.App.csproj` | 编译共享 RevealTransition 文件 |
| `src/App.Runtime.Tests/WorkspaceRevealTests.cs` | 实际 WPF 窗口的中间尺寸、状态、反向、生命周期及重复操作测试 |
| `src/App.Runtime.Tests/WorkspacePerformanceAudit.cs` | 可复现的实际内容 Measure/Arrange、布局、分配和 CPU 基线工具 |
| `src/App.Runtime.Tests/WorkspaceRegressionTests.cs`、`Program.cs` | 接入新测试与性能命令 |
| `src/App.Logic.Tests/Program.cs` | 移除写死旧动画类/方法名的断言；几何与生命周期由运行时行为测试覆盖；调整更名后的状态字段检查 |

共享 `ModernAnimations.xaml`、主题资源以及 PageTransition/ThemeService/LocalizationService 已检查；此次没有修改其设计值或内容。

## 3. 动画机制

复用现有 `NormalAnimationDuration`（220 ms）和 `ModernEase`（Cubic EaseOut）。一条 `DoubleAnimation` 提供 0–1 的缓动进度，协调左右裁剪视口宽度、18 DIP 列间距以及轻量窗口 HWND 尺寸；完整模式和轻量模式使用同一时钟机制。没有刷新率上限，也没有延长动画时间。

内容仍按原有的 300/336 DIP 宽度排列，视口揭开/回收内容，不缩放文字、不修改 Height/MaxHeight、不使用 LayoutTransform。外层网格仍执行必要的布局，使预览区正常让位。只有已经展开的左侧面板切换「来源/投屏」时使用轻微交叉淡化；整个卡片开关依靠裁剪即可，不再叠加位移和全卡片透明度动画。

首次显示时先让 WPF 完成模板和布局，再在 Loaded 优先级启动时钟。这个排队操作也可取消。反向操作先停止旧时钟，再以当前有效宽度/透明度为起点，未经过新一轮 Measure 的连续点击也适用。

## 4. 性能结果

基线和最终版本均使用真实 MainWindow、默认 WPF 渲染、150% DPI、Render Tier 2；关闭设备枚举并移除原生视频子窗口，以隔离展开动画。预热一轮后，每个模式执行 20 次设置开关，每次等待 350 ms。探针放在真实设置卡片的内容边界，记录的是实际 MeasureOverride/ArrangeOverride 调用，不是估算。

最终性能命令单独运行；桌面还有其他进程，CPU 数字不属于受控实验结果。

| 指标 | 完整模式：原版 → 最终 | 轻量模式：原版 → 最终 |
| --- | --- | --- |
| 设置内容 Measure | 103 → 0 | 100 → 0 |
| 设置内容 Arrange | 124 → 0 | 107 → 0 |
| UI 线程分配字节 | 151,084,600 → 39,295,000 | 141,364,888 → 10,806,264 |
| 窗口 LayoutUpdated | 144 → 248 | 147 → 148 |
| 进程 CPU 时间 | 10,046.9 → 9,796.9 ms | 10,312.5 → 7,765.6 ms |
| 测试墙钟时间 | 7,238.2 → 7,371.4 ms | 7,256.7 → 7,348.0 ms |

内容布局和分配量显著降低；0 次仅指预热后的固定尺寸内容，不代表整个窗口零布局。窗口外层布局没有归零，完整模式次数反而增加，原因包括移除了帧率限制并增加可取消的布局后启动步骤。不能据这些数据声称所有场景 CPU/GPU 都大幅降低，也不能把 LayoutUpdated 次数当作 FPS。

优化还包括：移除逐帧 Rendering 订阅、复用时钟管理对象、缓存本次动画 HWND、不为不变的宽度/间距/透明度重复写属性、不调用逐帧 UpdateLayout、不在帧回调中创建业务任务或刷新数据。

原始结果：`artifacts/workspace-before/workspace-performance.json`、`artifacts/workspace-final-isolated/workspace-performance.json`。

## 5. 统一范围

「投屏来源」「投屏」共用左侧裁剪容器，「投屏设置」使用同一种容器；完整模式、轻量模式、轻量模式最大化后的面板开关共用时钟、资源时长、缓动与收尾。面板切换不再依赖全局 revision 去忽略旧完成事件，旧时钟会明确取消和解绑。

## 6. 已完成的验证

- 主程序构建和运行时测试项目构建：通过，0 警告、0 错误。
- `--workspace-regression`：通过，包含原有位置/轻量启动回归及新增测试。
- 单次展开/收起：记录到中间布局宽度，卡片内部宽度保持稳定，结束时宽度和 Visibility 正确。
- 每种模式 40 次快速反向点击、交错切换左侧页面与设置：通过；反向操作保持当前有效宽度。
- 每种模式 100 次完整开关：通过；复用同一时钟管理对象，结束后无运行时钟。
- Light/Dark 与 zh-CN/en-US/zh-HK/zh-TW 的组合，在动画进行中切换：通过；最终页面和窗口透明度正常。
- 动画中隐藏/显示主窗口、最小化/还原、移除/重新加载设置控件、外部 Hidden、关闭窗口：通过；排队启动和活动时钟均可清理。
- 窗口四档目标尺寸 768×416、960×600、1280×800、1800×900 DIP（实际尺寸受工作区约束）：通过；预览宽高保持正值且满足测试的可用尺寸下限。
- 最大化/还原后的开关：通过，含轻量模式。
- 100/125/150/200% 对应的分数 DIP 几何算术：通过。这不是实际切换系统 DPI。
- 应用逻辑测试：直接执行测试 DLL 通过。`dotnet run` 会注入 DOTNET_ROOT_X64，触发现有更新器环境防护断言，改用直接执行 DLL 后通过，未修改该防护。
- 实际启动优化后的 iPhoneMirror 主程序，操作来源/投屏/设置及连续点击；检查到正确的最终页面、设置可达性和预览区域宽度。后续桌面有另一个测试窗口抢占前台，未据此声称完成所有逐帧视觉项目。
- 修复回归后新增的真实 WPF 几何测试：完整模式 768/900/960 DIP 下收起左右面板，轻量模式隐藏/最小化中断与恢复、重复隐藏、手动缩放保护；全部通过。

运行方式（在项目根目录）：

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj
dotnet src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --workspace-regression
dotnet src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --workspace-performance artifacts/workspace-performance
```

本轮最终构建位于 `artifacts/workspace-final-bin/iPhoneMirror.exe`。

## 7. 剩余限制

后续审查发现并修复了两处边界回归：窄窗口收起面板时暂时保留堆叠拓扑，避免尚未收完的卡片被立即搬到右侧挤窄预览；轻量模式在隐藏/最小化中断动画后，在恢复时重新计算窗口宽度，避免停在动画中间值。新增几何测试覆盖 768/900/960 DIP、反向操作、隐藏/最小化和手动缩放。

未在已完成的状态/几何测试中发现残留时钟、透明度异常、内容横向压缩或预览尺寸塌陷。依旧保留原有 960 DIP 断点的上下堆叠布局转换，以及用户调整窗口尺寸时结束当前动画的策略；这些结构性重排不使用额外的位置动画。跨断点的逐帧视觉连续性仍需要实机观察。

窗口/预览尺寸确实变化时，外层布局及原生预览重定位仍然必要。此次不宣称消除了全部 UI 卡顿、所有机器上的掉帧或原生视频渲染的 GPU 开销。

## 8. NOT TESTED

| 项目 | 原因 |
| --- | --- |
| 实际跨显示器 DPI 切换、真实 100/125/200% DPI | 未更改用户系统显示设置；实际测试 DPI 为 150%，其余仅有分数 DIP 几何测试 |
| 多种真实显示分辨率及多显示器组合 | 未切换显示模式；系统报告主显示模式为 2560×1600、240 Hz，窗口尺寸矩阵不等于显示模式矩阵 |
| GPU 使用率前后对比、呈现帧时延/P95、掉帧率、逐帧闪烁 | 未采集 GPU/Present/ETW 跟踪；静态截图与布局回调不能证明没有掉帧 |
| 最终版本所有场景的连续人工视觉观察，尤其窄窗口跨断点 | 桌面存在并行测试窗口抢占前台；已用确定性 WPF 回归覆盖状态与几何，不能替代连续视觉检查 |
| 长时间真实 USB/AirPlay 视频投屏下的展开收起 | 本轮性能测试隔离了原生视频；没有建立稳定的视频压力测试会话 |
| 修复版本直接产品 EXE 的独立启动 | 桌面已有 iPhoneMirror 单实例，启动修复版本时显示实例冲突；使用修复版本 Runtime 测试宿主的真实 MainWindow 预览完成交互验证 |
| 数小时重复操作、内存泄漏长期趋势 | 已完成每模式 100 次完整切换及 40 次快速反向、关闭清理验证，未进行小时级 soak/堆分析 |
