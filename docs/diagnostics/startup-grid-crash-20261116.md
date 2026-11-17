# v1.8.5-pre5 启动布局闪退（2026-11-16）

返回[文档索引](../README.md)。

## 日志结论

用户报告 iPhone 14 Pro / iOS 27.2.1：前一天有线、无线、反控可用，第二天使用约两分钟闪退，
随后每次启动界面无法操作并退出，卸载重装无效。

提供的 `startup.log`、`application (4).log`、`capture (2).log`、`reverse-control (3).log`
记录了北京时间 15:24:09、15:24:30、15:24:55 的三次重启，不包含最初使用两分钟时的故障现场。
三次均运行 `1.8.5-pre5+03771e9`、.NET 10.0.15、Windows build 26200，主窗口为 1365 × 853 DIP。

三次启动都在不足一秒内出现相同的未处理异常：

```text
System.IndexOutOfRangeException
System.Windows.Controls.Grid.SetFinalSizeMaxDiscrepancy
System.Windows.Controls.Grid.ArrangeOverride
```

无线和媒体接收端此前已就绪，必需原生依赖没有缺失；反控始终为 `owner=None`、`device=none`，
没有实际反控输入。日志中的 UxPlay 缺失属于可选组件，当前使用原始接收后端。
这些记录直接定位到 Windows 端 WPF 布局异常，不能据此推断手机系统或反控协议损坏。
原截图未随本次材料提供，无法核对用户看到的具体画面；也未证明本地设置损坏。

## 复现和修复

在 .NET 10.0.9 创建一个星号宽度的 `Grid`，设定有限 `MaxWidth=0.1`、`MinWidth=0.05`，
以 `Math.BitIncrement(0.1)`（`0.10000000000000002`）作为可用宽度执行 `Measure` / `Arrange`，
即可重现相同方法和异常；不需要手机、反控、无线接收端或布局像素舍入。

[WPF Grid 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Grid.cs)
的星号分配算法在移除候选项后可能保留 `-1` 标记，随后在处理近乎相等的剩余空间时再次使用该索引。
这是与日志一致的可复现故障路径；具体触发用户窗口的控件及尺寸仍未从截断堆栈中确定。

主程序和独立驱动管理器现在都在各自的 `App` 静态构造函数中、任何 XAML 加载前，
调用共享的 `WpfLayoutCompatibility.Enable()`，启用 WPF 自带的兼容分配算法：

```text
Switch.System.Windows.Controls.Grid.StarDefinitionsCanExceedAvailableSpace = true
```

开关由 [WPF FrameworkAppContextSwitches](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/FrameworkAppContextSwitches.cs)
提供，首次布局时缓存，因此不能等到发生异常后才设置。每个进程的初始化入口同时保护主窗口、子窗口和第三方模板中的 Grid。
没有通过吞掉数组越界来继续运行，也没有清除用户设置。兼容算法在某些 DPI 下的像素舍入与新算法略有不同，
需结合实际窗口回归确认。

## 验证

- 新增 `App.Runtime.Tests --grid-layout-compatibility`，并接入默认运行时测试入口。
  修复前稳定失败，异常堆栈与用户相同。
- 修复后 2,784 个布局用例通过：2,016 个有限上限边界用例，覆盖两个坐标轴、100%～300% DPI、
  舍入开关及上限两侧的浮点边界，观察到的最大舍入差为 0.667 DIP；另有 768 个混合布局，
  检查 Auto、固定尺寸、带权星号、跨行列、最小／最大约束、折叠恢复和动态尺寸切换的空间分配。
- 第二轮独立进程审查发现，第一轮只修改主程序入口，驱动管理器仍能复现同样越界。
  已把兼容初始化提取为共享实现并接入两个入口；两个入口在任何 XAML 加载前的首个 Grid 探针均通过。
  `build.ps1` 的本地托管测试阶段分别启动两个探针，避免同一进程缓存开关而掩盖遗漏。
- `--complete-window-resize` 通过：侧栏、预览最小宽度、原生窗口缩放、全屏与模式恢复、五种语言和文字缩放。
- `--settings-categories` 通过：三类设置、保存/加载、重开、滚动位置，以及五种语言和明暗主题。
- `--application-mode-visibility` 通过：完整/精简/托盘模式和八组保存的显示选项。
- `--rounded-window-recovery` 通过：84 组窗口恢复样本，0 失败。
- 第二轮新增托盘启动设置、隐藏窗口绑定、资源与滚动条、精简／完整预览尺寸回归，全部通过。
- 驱动管理器界面审查通过：五种语言、明暗主题、1 倍和 2 倍文字，共 320 组渲染，
  0 失败、0 布局问题、0 绑定诊断；驱动安装器完整单元测试通过，未执行实际驱动安装。
- 原生 Core Release 构建通过；应用、运行时测试、驱动管理器及其测试 Release 构建零警告、零错误。
- 第一轮直接加载发布后的单文件 EXE 内程序集，在隔离测试入口执行 2,016 组边界和完整窗口缩放回归，通过；
  这验证了实际发布载荷，但没有执行正常单实例启动及实机连接流程。测试入口没有打包到交付目录。
- 第二轮对最终主程序及驱动 EXE 分别检查包内程序集版本和首个 Grid：均通过；
  最终主程序还通过全部 2,784 组布局与完整窗口缩放回归。直接执行 `build.ps1` 的实际新增检查段，
  两个独立入口及 Automation 生命周期检查均通过。
- 交付目录内的 863 个反控运行时文件哈希、离线功能检查与两类 DDI 清单验证通过。
- 两轮默认完整 `App.Runtime.Tests` 均通过，包含新增边界及混合布局用例、Automation API、
  输入与媒体回归、更新窗口和倒计时等检查。
- 第二轮文档检查：225 个 Markdown、956 个本地链接／图片，0 错误；本次相关源码和新增文件空白检查通过。

默认测试另发现更新窗口测试只提供两行说明，滚动条隐藏时宽度为零，却要求有实际拖动宽度。
已改用足够长的发行说明并明确要求滚动条可见；这是测试场景修正，未修改产品滚动条实现。

原始本地验证日志位于忽略目录 `work/diagnostics/wpf-layout-20261116` 及
`work/diagnostics/wpf-layout-before.log`、`work/diagnostics/wpf-layout-after.log`。
第二轮入口、混合布局、驱动管理器及发布包审查记录位于 `work/diagnostics/wpf-layout-review-20261116`。
这是本地复现与回归结果，尚未在反馈用户的电脑、其 .NET 10.0.15 环境及最初两分钟的投屏场景复测。

## 交付和反馈建议

当前主线集成开发测试构建位于 `outputs/iPhoneMirror-main/startup-gridfix-r2-20261116`，版本为
`1.8.5-startup-gridfix-r2.20261116`，包含两个入口的修复；第一轮目录保留为历史测试产物。
这是当前 `main` 工作树的构建，包含已有未提交的集成修改，
不等同于仅对已发布 pre5 增加补丁，也不是正式 ZIP / Setup 发布验收。
目录中的 `BUILD-INFO.json` 记录组件哈希和验证范围。

可向反馈用户说明：已从日志确认反复启动闪退发生在电脑端界面布局，并复现、修复了对应异常；
请使用完整测试目录复测启动、窗口操作及原来的投屏场景。卸载重装同一版本仍会保留同样的布局算法，
无法消除此代码路径；当前证据不支持要求用户重置手机或清空配对和设置。
