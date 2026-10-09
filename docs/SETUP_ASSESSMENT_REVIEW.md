# 设置助手状态检查验收（2026-10-08）

## 结果页布局调整（2026-10-09）

检查动画进一步与结果页统一：共用卡片和行控件，实际检测时旋转，返回状态后原位显示勾或异常提示。结束时保留行顺序及卡片位置，更新标题并显示操作按钮。异步检测用例验证卡片/行实例保留、结束前后位置一致；四语言双主题的检查中及结果截图位于 `outputs/setup-assessment-animation-20261009`。

结果页改为居中的卡片列表，检查项名称、状态分列对齐，异常就地显示原因。移除四项一页的分页，长列表在卡片内滚动并预留滚动条空间。重新检查与完整重配并排呈现为次要操作；设置助手使用实色背景，避免桌面内容透出干扰阅读。原有检测与任务导航逻辑保持不变。

通过构建及设置状态检查回归，使用 Windows-MCP 检查实际窗口。四语言、双主题的完成及异常结果截图位于 `outputs/setup-assessment-layout-20261009`。

## 完成行为

每次打开设置助手先异步读取当前配置和系统证据，再显示检查结果。已完成和 NotRequired 项不进入任务列表；缺失、失效和无法确认项按前置依赖安排到原有配置页面。任务间仅局部复核受影响的证据，直接进入下一项；跳过与返回不重新扫描，任务结束后展示汇总。重新打开或主动重新检查仍完整读取当前状态。只有用户明确选择「重新配置全部设置」才进入完整流程，实际档案继续保留。

2026-10-09 修正逐步完整重扫：新增 `SetupAssessmentSession`，证据缓存仅存活于本次助手会话。回归统计实际委托调用次数，覆盖外观修改、蓝牙修复、无线组件修复、绑定完成、失败留页、跳过/返回零重扫、依赖解除后直接导航、任务切换保存失败后恢复，以及局部检查关闭时的所有权清理。输出目录为 `outputs/setup-assessment-task-flow-20261009`。原有向导回归的 368 组布局、256 种多设备路径及发现/取消/退出清理通过，输出为 `outputs/setup-task-flow-regression-20261009`。

历史检查点保存用途、功能选择和进度，不能覆盖当前检测结果。离线设备保留身份并显示 Unknown；跳过只影响当前会话，下次仍重新检测。缺失组件、被删除的绑定、取消 Windows 配对和损坏配置均有对应状态检查。检查包含配置与运行组件的前置条件，不等同于完成实际投屏或控制输入。

实现与状态来源见 [FIRST_RUN_SETUP.md](FIRST_RUN_SETUP.md)。主要代码为 `SetupAssessment`、`SetupTaskPlanner`、`SetupCheckRuntime` 和 `FirstRunSetupWindow.Assessment.cs`，复用既有设备枚举、驱动管理器、接收器、档案及桥接程序。

## 构建与自动回归

- App 运行时测试项目、驱动管理器构建成功，0 警告、0 错误。
- `--setup-assessment outputs/setup-assessment-final`：通过首次使用、部分/全部配置、外部修复后重进、缺失组件和配对、离线身份保留、依赖排序、NotRequired、跳过、损坏配置与绑定、实际 WPF 异步入口/关闭、四语言双主题检查。
- `--first-run-setup outputs/setup-assessment-final-regression`：368 组页面/语言/主题/尺寸布局、256 种多设备路径、回退、保存失败回滚、发现与下载的取消、绑定复用/删除、退出清理及动画回归全部通过。
- Python `setup_check_test.py`：4 项通过；`*boundary_test.py`：6 项通过，检查无主动配对、身份核对、前置条件与取消清理。
- `scripts/verify_localization.ps1`：四语言 App 各 1562 个键、Driver 各 196 个键，资源引用和主题键全部通过。
- 本次已跟踪代码的 `git diff --check` 无空白错误；Git 提示现有工作副本采用 CRLF/LF 转换规则。

构建使用独立桥接输出 `work/setup-check-bridge`，没有覆盖现有发布目录。构建命令：

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -p:UsbBridgeRoot=C:\Users\Ray\Documents\iphoneMirror\work\setup-check-bridge -v:q
dotnet build src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj -v:q
```

## Windows-MCP 交互验证

使用 `--setup-assessment-preview outputs/setup-assessment-windows-mcp` 打开的隔离窗口，经 Windows-MCP 实际点击确认：

1. 结果显示一个无线组件异常，基础设置已完成。
2. 「继续设置」直接进入无线接收方式页，展示具体失败原因。
3. 「跳过此项」返回结果页，异常仍保留，主按钮改为「保存并稍后处理」。
4. 点击保存后窗口和预览进程正常退出，检查点保留 InProgress 和跳过记录。

这些操作使用替代检测结果和临时检查点。最终 WPF 截图位于 `outputs/setup-assessment-final`，包括四种语言与两种主题。

## 真实只读运行时验证

`--first-run-driver-probe` 和 `--setup-runtime-probe` 使用本次构建的驱动管理器及冻结桥接程序。实际发现 2 台 Apple 设备；Apple 支持和 USB 文件校验通过，原始无线接收组件 Ready，蓝牙适配器就绪且读取到 2 个配对身份，两台设备的只读控制前置条件均 Completed。桥接完整性和实际 `--check-runtime` 通过。

`--first-run-driver-cancel` 覆盖真实只读助手启动后 0/50/300/900/1400ms 的取消及立即重扫，无残留助手进程。

未执行管理员驱动安装/卸载、真实组件下载、蓝牙重新配对、多设备实际插拔或端到端投屏/控制输入；这些硬件操作仍需对应环境验收。Apple 版本兼容性继续采用已有驱动检测能力。
