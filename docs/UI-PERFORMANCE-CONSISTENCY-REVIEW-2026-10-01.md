# UI、性能与一致性复审

日期：2026-10-01。针对当前工作区源码重新编译、测量与渲染；保留了审查开始前的全部未提交修改。

本轮确认并修复了三类问题。最终主程序 210 组、驱动管理器 96 组、原生/媒体预览外壳 12 组，共 318 组场景通过；在这些场景内没有剩余的窗口加载失败、布局裁切记录或绑定诊断。该结论不覆盖真机长期投屏、不同物理显示器或所有外部故障组合。

## 修复内容

| 问题 | 原有行为与证据 | 本轮修改 |
| --- | --- | --- |
| 关于页持续刷新不可见日志 | 关于/更新设置标签页、隐藏及最小化窗口都维持 500 ms 日志轮询；新增运行时检查在关于页的 650 ms 样本内记录到 1 次轮询 | 仅在可见、未最小化的诊断页启用轮询；进入诊断页立即刷新，恢复显示时继续；关闭后清理事件；首次刷新与定时刷新共用重入保护 |
| 主题切换覆盖透明度 | 主程序窗口设置为 0.63 后切换主题，动画最终将有效透明度保持为 1；驱动管理器使用同样的固定终值实现 | 两个应用都从原有基础透明度计算淡入范围，并使用 `FillBehavior.Stop`，结束后恢复属性原值 |
| 页面动画无法正确停用 | `IsEnabled=false` 不解绑 Loaded；重复开关累积处理器；关闭动画后位移继续运行，且动画处理器将内容透明度改为 1 | 成对管理 Loaded/Unloaded 事件；停用或卸载时停止位移动画；保留原有变换及内容透明度 |

实现位于：

- `src/App/Windows/AboutWindow.xaml`、`AboutWindow.xaml.cs`
- `src/App/Services/ThemeService.cs`
- `src/DriverInstaller/Services/DriverThemeService.cs`
- `src/SharedUI/Animations/PageTransition.cs`

新增 `src/App.Runtime.Tests/UiPerformanceAudit.cs` 和 `--ui-performance <目录>` 入口，覆盖动画开关、变换与透明度保留、日志轮询生命周期和页面操作性能。驱动 UI 矩阵加入透明度回归检查。运行时测试工程补齐应用图标资源，避免测试宿主截图出现空白图标；这是测试宿主资源修正。

## UI 与一致性覆盖

静态清单包含 30 个 XAML 根：28 个窗口和两个 Application。另检查以代码构造的原生预览、媒体外壳及受保护内容覆盖层。

- 主程序：投屏、设备、设置、输出入口、双侧栏、预览工具栏，以及开发工具目录中的全部普通弹窗。包括关于页、更新、启动失败、设备绑定、AirPlay 选择、蓝牙连接、反向控制状态及前置条件、快捷键、恢复/错误提示、图像和投屏/输出设置。
- 驱动管理器：简单/高级模式、忙碌/刷新失败状态、信任确认、操作提示、失败帮助、父级驱动选择和确认、长错误及长正文。
- 组合：简体中文、繁体中文（香港）、英文；Light/Dark；普通及声明最小尺寸；100%、125%、150%、175%、200% 工作区 DIP 预算。DPI 检查是实际 WPF 布局预算测试，不是五台物理显示器验收。
- 交互：选项卡、ComboBox/Tooltip/菜单及子菜单、详情展开、可滚动正文、默认/取消操作、Escape、全屏往返、圆角及缩放边界。
- 共享控件：明暗主题与两种文字大小下的按钮、只读选取、禁用可读性、复选框三态、焦点及换行动作间距。
- 人工图像检查：查看了主程序全部 35 类场景的普通尺寸联系表、英文深色小尺寸联系表、驱动英文深色小尺寸的 16 类场景，以及关于页完整渲染。原生菜单及覆盖层另外保留渲染图。

资源校验结果：三语缺失键 0，明暗主题键差异 0。静态检查选取的正文、状态及主/危险动作配色对比度均大于 4.5:1，最低为浅色成功状态文字约 4.76:1。此结果限于脚本定义的配色对，不代表所有透明叠加、图像内容或系统高对比度模式已经验证。

## 性能测量

### UI

最终测量使用 Release、WPF 默认渲染（报告 tier 2）、预览数据且没有实际媒体流。四类操作各预热一次，再各测六次；操作耗时包含调用、布局及调度处理，不包含随后等待入场动画的固定时间。输出操作会新建输出设置窗口，每轮结束后关闭。

| 操作 | 样本数 | 中位耗时 | 最大耗时 |
| --- | ---: | ---: | ---: |
| 切换投屏页 | 6 | 17.36 ms | 43.14 ms |
| 切换设备页 | 6 | 15.63 ms | 51.05 ms |
| 切换设置页 | 6 | 4.60 ms | 52.86 ms |
| 打开输出设置 | 6 | 174.06 ms | 289.65 ms |

全部 24 次操作合计中位数 17.36 ms、P95 230.41 ms，UI 线程分配约 10.87 MB。输出设置的创建成本明显高于主页面切换；本轮没有据此缓存窗口或更改输出能力刷新行为。

初次软件渲染采样曾出现约 0.9 个逻辑核的短时 CPU 占用。线程采样定位为 `.NET Tiered Compilation Worker`；当时没有反复布局或大量 UI 调度。等待后台编译预热后，最终约 2 秒闲置样本内没有 LayoutUpdated，进程 CPU 增量低于该次采样可观察粒度。线程统计与进程统计的采集边界不同，原始 JSON 均保留；不将该短样本表述为“应用零 CPU”。主窗口的设备刷新及实际媒体工作没有在预览模式中启动。

### 媒体与原生路径回归

| 项目 | 当前源码实测 |
| --- | --- |
| 120 帧 1920×1080 NV12 交接 | 快照分配 373,250,880 字节；已存在的缓冲复用实现预热后分配 0 字节 |
| 500 次未改变尺寸的预览区域更新 | 40 字节，0.22 ms；保留缩放/全屏区域恢复断言 |
| 原生组件 | 重新编译，CTest 11/11 通过，包含协议、媒体、无线及虚拟摄像头组件 |

帧复用和区域缓存是当前工作区既有修改，本轮进行了验证，没有将它们重复计为新优化。上述微基准不能换算成真实端到端投屏延迟或整体零分配。

静态复核仍可见 D3D 预览的 1 ms 等待轮询，以及托管日志同步追加、原生日志在调用线程触发批量刷盘的路径。本轮没有确认其造成真实负载下的卡顿，也没有改变它们；这些路径需结合实际设备、磁盘与帧时序进一步采样。

## 最终验证记录

本机证据根目录：`work/full-ui-review-20261001/`（Git 忽略目录，保留原始 JSON、日志、PNG 与修复前源码）。

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| 主程序最终 UI 矩阵 | 210 组；失败、布局问题、绑定诊断均为 0；552 张渲染图 | `app-ui-final/runtime-results.json` |
| 驱动管理器最终 UI 矩阵 | 96 组；失败、布局问题、绑定诊断均为 0；192 张渲染图 | `driver-ui/driver-results.json` |
| 原生/媒体预览外壳 | 12 组通过，绑定诊断 0 | `preview-shell/preview-shell-results.json` |
| 控件状态 | 2 主题 × 2 文字大小，通过 | `control-states.log` |
| 主程序完整运行时回归 | 通过，包含交互、光标、主题、窗口和倒计时 | `runtime.log` |
| 工作区与键盘焦点 | 通过；USB/无线包在内存中验证，没有向设备发送输入 | `workspace.log`、`keyboard.log` |
| 多语言运行时 | 18,192 个格式化用例、4 次字典切换、12 个控制流程通过 | `runtime-localization.log` |
| UI 性能及本轮缺陷回归 | 0 失败 | `performance-final/performance-results.json` |
| App / Driver 逻辑测试 | 通过 | `logic.log`、`driver-tests.log` |
| 原生编译及测试 | 编译成功，11/11 通过 | `native-build.log`、`native-tests.log` |
| Python 离线回归 | 136/136 通过 | `python-tests.log` |
| 主程序、运行时测试、驱动管理器构建 | 成功，0 警告、0 错误 | `build-perf-final.log`、`build-driver-final.log` |
| 本轮变更格式及资源检查 | 通过 | `git diff --check`、`static.json`、`localization.log` |

### 复现

先构建测试工程和驱动管理器，然后用测试程序的对应入口运行。以下命令使用标准输出目录；本轮为了保留原有构建并避免占用，实际使用 `--artifacts-path` 分离产物。

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release
dotnet build src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj -c Release
$runner = 'src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe'
& $runner --ui-audit work/ui-review/app
& $runner --driver-ui-audit . src/DriverInstaller/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll work/ui-review/driver
& $runner --preview-shell-ui-audit work/ui-review/preview
& $runner --control-states work/ui-review/controls
& $runner --ui-performance work/ui-review/performance
& $runner --keyboard-focus
& $runner --workspace-regression
& $runner --localization-audit
& $runner
```

本轮没有执行驱动安装/替换、真实设备输入、远端推流、多设备长时间压力、音画同步或物理多屏 DPI 测试。修改已保存在源码和测试中，未打包安装器、发布、提交或推送。
