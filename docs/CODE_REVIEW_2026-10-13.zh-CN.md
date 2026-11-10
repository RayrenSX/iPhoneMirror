# 再次审查（2026-10-13）

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；源码链接改为仓库相对路径，原记录中的行号仅适用于当时版本；历史测试结论未在本次重新执行。

## 修复与复测记录

用户随后要求修复，以下三项已处理；保留下文的修复前审查与复现记录。

- 后台反控准备移除独立的 45 秒超时，沿用桥接器的 DDI/下载/挂载期限，仍响应客户端取消。46 秒受控启动回归通过。
- 准备和打开窗口请求将取消信号传入调度、核心锁等待及预检。取消的锁等待者释放管理入口；原生创建一旦开始，等待结果并停止、销毁本次未发布的句柄后才结束，不丢弃后台创建任务。已有会话不会因请求取消而停止；复用其他调用方的窗口创建时，只取消自己的等待。停止失败仍尝试销毁句柄，USB 恢复警告仍标记需要恢复。
- 六处文档入口改为现有 HTTP API 与 Python/curl 示例，并明确仓库目前没有独立 stdio MCP 服务。

新增回归在 `src/App.Runtime.Tests/AutomationManagementTests.cs`，已接入现有 `--automation-lifecycle` 入口；较慢的 46 秒验证使用 `--automation-lifecycle-long-startup`。

修复后应用/Runtime Tests 构建成功，API 生命周期及新增取消/长启动回归、API 集成和采集回归专项均通过，日志为 `work/fix-review-20261013-{build,automation,api,capture}.log`。构建含已有界面字段 `FirstRunSetupWindow._choice` 的 CS0649 警告（WPF 临时项目和主项目各一次）。未进行真实手机或驱动变更验收。

完整 App.Logic.Tests 构建成功，但执行在 `DeviceBindingWindow.xaml uses the shared child-window title hierarchy` 断言失败；修复期间工作区另有 `DeviceBindingWindow.xaml` 界面重构，本次没有修改该文件或放宽断言。不能将本轮报告为全套测试通过，详见 `work/fix-review-20261013-logic.log`。

## 修复前审查记录

审查对象：基础提交 `829c591` 上的当前未提交修改及新增文件。复查上一轮修复，重点跟踪后台 API 管理、远程文本输入、DDI 提示、USBMux 探测及原生旋转改动。没有修改产品源码。

本轮发现两个 P2 产品问题和一个 P3 文档交付问题。

## 1. [P2] 后台反控准备在正常 DDI 准备完成前被取消

位置：[MainViewModel.AutomationManagement.cs:14](../src/App/ViewModels/MainViewModel.AutomationManagement.cs#L14)。

`StartAutomationControlAsync` 只等待 45 秒，超时即调用 `CancelReverseControlAsync` 终止底层启动，并吞掉超时异常。首次下载、个性化或挂载 DDI 超过 45 秒时，即使底层仍在正常工作，后台准备也会失败。现有桥接器为同一启动流程设置了 360 秒就绪期限，Python DDI 准备期限为 300 秒、挂载期限为 180 秒。API 最后只返回 `controlAvailable=false` 和通用配置建议，无法区分前置条件缺失与被自身提前取消。

证据：直接跟踪代码和超时常量；没有进行真实 DDI 下载或挂载。建议沿用底层启动期限，或提供可查询、可取消的异步准备操作，并返回明确超时状态。

## 2. [P2] 客户端取消准备后仍持有全局管理锁

位置：[MainWindow.AutomationManagement.cs:52](../src/App/MainWindow.AutomationManagement.cs#L52)。

管理入口只在开始前检查取消，随后调用不接收取消参数的 `StartBackgroundSessionAsync`。该方法等待 `_coreGate` 时使用无取消参数的 `WaitAsync()`，之后也不会因请求取消而停止投屏启动。客户端断开或取消后，原请求仍占用 `_automationManagementGate`，其他设备的准备及打开窗口请求也会收到 `MANAGEMENT_BUSY`；锁恢复后，已取消的请求还可能继续启动采集。

已用实际 WPF 方法、假设备和受控 `_coreGate` 复现，无设备输入或采集：

```text
After cancellation: requestCompleted=False, managementGate=0
Next prepare: MANAGEMENT_BUSY
Original request after releasing core gate: AutomationException
```

释放锁之前将视图模型标记为已释放，避免触发真实设备操作。程序：Program.cs（本地记录：`work/review-20261013/Program.cs`）；日志：cancellation.log（本地记录：`work/review-20261013-cancellation.log`）。建议将取消信号传入锁等待和启动流程，在开始硬件操作前再次检查，并明确取消期间新建会话的清理责任；不要仅取消外层等待而让启动任务继续执行。

## 3. [P3] MCP 安装入口指向仓库中不存在的服务

位置：[USER_GUIDE.md:666](USER_GUIDE.md)（原记录行号 L666）。

新增用户指南宣称仓库提供 `tools/iphone-mcp` 服务，六处文档入口都指向该目录的 README，但工作区没有此目录，`git ls-files tools/iphone-mcp` 也没有结果。用户无法按说明安装或配置此服务。应将实现与 README 一并交付，或改为真实的外部项目链接并修正文案。

## 本轮验证

| 检查 | 结果 | 日志 |
| --- | --- | --- |
| App 与 Runtime Tests Release 构建 | 0 警告、0 错误 | `work/review-20261013-build.log` |
| 原生 Release 构建、CTest | 通过，16/16 | `work/review-20261013-native.log` |
| Python unittest | 通过，411 项 | `work/review-20261013-python.log` |
| App.Logic.Tests | 通过 | `work/review-20261013-logic.log` |
| DriverInstaller.Tests 构建与执行 | 通过 | `work/review-20261013-driver.log` |
| API 生命周期专项 | 通过；托盘未加载启动、临时配置换密钥、权限及停止状态保持 | `work/review-20261013-automation.log` |
| API 集成专项 | 通过 | `work/review-20261013-api.log` |
| DDI 启动及反控错误展示专项 | 通过；含 80 个错误路由用例和开发者模式等待状态 | `work/review-20261013-ddi.log` |
| 键盘焦点专项 | 通过；包括远程文字、拆分 emoji、模拟按键及多设备隔离 | `work/review-20261013-keyboard.log` |
| SRS 异常请求测试 | 通过 | 本轮工具输出 |
| `git diff --check` | 通过 | 本轮工具输出 |

本轮没有运行完整 WPF 套件、独立静态画面的像素验证或真实手机/驱动安装验收。上一轮记录的 AMD 退出崩溃没有在本轮重新调查，不应据此认定已修复。逻辑套件退出码为 0，但其输出仍含两次 `dotnet--hide_banner` 子进程诊断；未将整体通过等同于所有子进程场景均正确验证。
