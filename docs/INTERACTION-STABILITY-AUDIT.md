# 主界面拖动与蓝牙控制稳定性审查

日期：2026-10-01。范围：主窗口/独立预览的移动、捕获和区域更新，以及蓝牙键鼠发送、超时、恢复、停止与释放。修复已写入工作区，保留了同期其他任务的修改。

## 已确认并修复的问题

| 问题 | 修复及作用 |
| --- | --- |
| 预览位置更新重复创建约 260 个点的圆角区域，并调用 `SetWindowRgn` 触发重绘 | 缓存宽高、半径和曲线形状；位置变化且形状相同时跳过重建。尺寸、DPI 引起的半径变化、全屏和设备轮廓切换仍更新区域；销毁 HWND 时清空缓存。 |
| 预览失去捕获后无条件调用 `ReleaseCapture`，可能取消标题栏刚取得的拖动捕获 | 主预览和独立窗口只释放自己持有的捕获，避免干扰新的捕获所有者。 |
| 重复排队的预览同步和无上限鼠标消息排空增加输入线程负担 | 合并同一轮 Dispatcher 的预览同步，跳过不变的显示状态，每次最多排空 64 条鼠标移动消息。 |
| 已定义 300 毫秒鼠标通知超时，但实际发送传入无限超时 | 启用有限等待，同时为鼠标传输信号量设置超时。取消路由后及时结束托管等待。 |
| 异步发送方法可能在第一次真正挂起前同步执行 WinRT 操作，且调用点持有输入生产锁 | 键鼠报告处理和释放报告均从线程池启动。高频入队检查只读取托管状态。 |
| 旧发送任务、重试或释放任务可能在停止/重新启动后修改新队列状态 | 发送前后及等待后检查代次；在生产锁内切换代次和状态；旧释放完成时不覆盖新状态。 |
| 鼠标发送失败后退出处理循环，可能遗留刚排队的键盘任务 | 鼠标重试耗尽时仍唤醒待处理键盘任务；故障恢复取消所有尚未完成的键盘排队任务。 |
| 恢复清理会重置故障标志，导致上层错过断开通知；状态订阅者异常也可能逃出原生回调 | 清理后重新发布故障状态，逐一隔离状态订阅者异常；恢复任务携带失败连接的代次，忽略已失效的恢复请求。 |

鼠标的原生通知不可靠地支持取消。因此超时只终止托管等待，底层通知仍持有唯一传输槽，直到真正完成后才释放；迟到的异常会被观察。这样不会用新的并行鼠标通知覆盖仍在运行的旧通知。300 毫秒是通知/传输槽等待各自的上限，并非整个断开流程的总耗时承诺。发生通知超时后会进入既有的故障断开处理，用户可重新启用控制。

主要实现文件：

- `src/App/Controls/NativePreviewHost.cs`
- `src/App/Windows/NativePreviewWindow.cs`
- `src/App/MainWindow.xaml.cs`
- `src/App/Services/BluetoothHidMouseService.cs`

## 验证

新增 `src/App.Runtime.Tests/InteractionRegressionTests.cs`，支持 `--interaction-regression` 单独运行，并已接入默认运行时测试入口。

| 检查 | 最终结果 |
| --- | --- |
| Release 应用及运行时测试构建，独立输出目录 | 通过，0 警告、0 错误 |
| 交互回归 | 通过 |
| 默认应用运行时测试，包括本次交互回归 | 通过 |
| `--keyboard-focus`，含蓝牙队列/目标锁、主/独立窗口及 Windows 热键检查 | 通过 |
| 完整应用逻辑测试 | 通过 |
| 修改文件的 `git diff --check` | 通过 |

交互测试使用真实 Windows HWND/GDI，验证圆角区域缓存、调整尺寸、全屏切换及捕获所有权。最终一次测量：预热后 500 次形状不变的区域更新共 40 字节分配、约 0.29 毫秒；40 字节包含测试计时器自身开销。该测量仅针对区域更新路径，不代表整个窗口拖动的帧率。

蓝牙测试使用真实服务队列、信号量及可控 Task，不连接设备。覆盖成功完成、永不完成、取消、原生异常、超时后传输槽持有、释放后的迟到异常、后台启动、旧发送代次、键盘任务收尾、旧连接恢复隔离、故障标志发布，以及交叠释放。全部测试未向真实手机发送输入。逻辑测试中部分既有断言以 `true ||` 绕过检查，因此本轮蓝牙行为结论以新增运行时测试为依据。

复现命令（仓库根目录）：

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --artifacts-path work/interaction-audit/artifacts
& work/interaction-audit/artifacts/bin/IPhoneMirror.App.Runtime.Tests/release_win-x64/IPhoneMirror.App.Runtime.Tests.exe --interaction-regression
& work/interaction-audit/artifacts/bin/IPhoneMirror.App.Runtime.Tests/release_win-x64/IPhoneMirror.App.Runtime.Tests.exe --keyboard-focus
& work/interaction-audit/artifacts/bin/IPhoneMirror.App.Runtime.Tests/release_win-x64/IPhoneMirror.App.Runtime.Tests.exe
dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release
```

构建和测试记录位于 `work/interaction-audit/build.log`、`runtime.log`、`keyboard-focus.log`、`logic.log`；定向交互测试另有 `interaction.log`。

## 实机验证边界

本次未复现真实蓝牙适配器/驱动的 WinRT 卡死，也未测量真实投屏期间跨屏拖动的帧时间。操作系统中的原生调用和客户端元数据查询仍受驱动影响，不能据此承诺整个服务关闭绝不阻塞。

建议实机检查：

1. 播放投屏时持续拖动主窗口，跨不同 DPI 显示器，调整大小并往返全屏，确认圆角、画面及拖动连续性。
2. 蓝牙连续移动、点击、滚轮和输入文字，切换主窗口/独立窗口及其他应用，确认无粘键或鼠标捕获残留。
3. 控制过程中关闭手机蓝牙或离开有效距离，确认本机输入可恢复；重新启用控制后不补发旧移动或按键。
4. 通知拥堵时停止控制、立即重连及退出应用，检查异常日志和迟到回调。
