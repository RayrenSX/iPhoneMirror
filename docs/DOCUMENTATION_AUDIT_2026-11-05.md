# 文档统一完善记录（2026-11-05）

## 范围与基线

基于本地工作区和 Git HEAD `eceab25` 核对。主程序及驱动管理器项目版本均为 `1.8.5-pre4`，
工作区含后续未发布修改。整理开始时已有 `BridgeProcessLifecycleTests.cs` 的未提交修改，
本次保留该文件，不改动应用实现、版本号、发布清单或二进制。

全库检查覆盖 Markdown 的编码、围栏、本地链接、图片、标题锚点及 `docs` 索引可达性；
内容核对重点为中英文 README、使用教程、开发指南、架构、反控、输入、依赖及 HTTP API。
同时梳理发布记录、Issue 记录、工具说明和第三方来源文档。供应商许可证与原始历史测试结论保持原文。
这次工作是文档一致性修订，不是重新执行全部功能测试或验证线上发布状态。

## 修订内容与依据

| 问题 | 修订与依据 |
|---|---|
| 入口遗漏大量新增资料 | 补齐[主索引](README.md)、[审计与设计索引](ARCHIVE.md)、[Issue 索引](issues/README.md)和[发布说明索引](releases/README.md)，区分持续维护指南与历史证据 |
| 当前指南仍标为 `1.8.4-test4` | 对照两个应用项目更新工作区基线，明确项目版本、未发布修改和实际 Release 的区别 |
| README 误称切换设备会停止上一台 | 对照 `MainViewModel`、会话管理与托盘逻辑，改为保留其他设备会话，区分停止单台、关闭到托盘与退出应用 |
| 无线档位与 UxPlay 获取方式过时 | 对照 `WirelessReceiverService`、`HostCommon.h` 和组件流程，补充面板长边方形广播能力、实际帧尺寸边界与按需下载 |
| DDI 内置、下载与 Apple USB 支持 MSI 混淆 | 对照 `config/ddi-bundles.json`、`DdiBundle.ps1`、`DirectUsbInputBridge`、`usb_touch_bridge.py` 和 `ddi_support.py`，统一随包镜像、缓存、下载及个性化说明；来源与完整性不写成授权证明 |
| 架构仍列无线 IPC v7、FFmpeg 4.4.2 | 对照 `IpcProtocol.h` 与随库构建记录，改为 IPC v8、FFmpeg `63b2b0f47d`，补充 AVC High 4:4:4 软件解码与实际桥接器 `tools/` 路径 |
| 测试说明与示例冲突 | 对照 `build.ps1` 和测试项目的 TargetFramework/RuntimeIdentifier，补齐默认提权启动及 Automation 生命周期检查，将当前指南的 `dotnet run` 示例改为直接执行程序集 |
| API 文档遗漏管理入口 | 对照 `AutomationApiHost` 与 `MainWindow.AutomationManagement`，补充应用状态、后台准备、打开窗口、权限、租约冲突与错误码 |
| API 示例默认操作第一台设备 | 改为明确选择设备；Python 默认查询或截图，单次点击需显式启用，携带 geometryVersion，并在失败时释放租约；PowerShell/curl 同步选择与释放流程 |
| 历史源码链接依赖个人绝对路径 | 转换为仓库相对链接，历史行号仅保留为当时的定位信息；未入库日志、截图与安装包改为明确的本地记录路径，保留其证据含义 |
| 缺少可重复的文档检查 | 增加 [verify_docs.py](../scripts/verify_docs.py)，按 Git 文件清单校验，避免本机生成目录掩盖新检出仓库中的失效链接 |

## 验证与边界

从仓库根目录执行：

```powershell
python scripts/verify_docs.py
git diff --check
```

另对变更文档中的 PowerShell、Python、JSON/JSON Lines 示例做语法解析；对 Markdown 表格列数
进行扫描。HTTP Python 示例使用替代响应检查设备选择、只读默认流程、显式点击及异常后的租约释放，
不连接手机、不发出真实控制请求。

第一轮结果：198 份 Markdown、843 个本地链接/图片引用检查通过，`docs` 下全部 Markdown
均可从主索引到达；225 张表格列数一致。变更及新增文档中的 35 段 PowerShell、4 段
Python/JSON/JSON Lines 示例语法通过，两个 Python 文件可解析；检查器的围栏、重复标题、
中文锚点与链接提取抽查通过。HTTP Python 示例的 5 个模拟场景通过；`git diff --check` 通过。

没有运行示例中的驱动安装、USB 配置、真实触控、发布和提权命令；没有重新执行 WPF 窗口矩阵、
真机兼容性、安装包或性能验收。外部 URL 未联网复验，旧报告中的本机产物也未重新生成。
文档检查通过只说明上述静态与示例检查结果，不扩大历史记录的验收范围。

## 第二轮：阅读路径与历史边界

- 使用教程增加按任务查找的入口，覆盖 USB、Wi-Fi、反控、录制、日志和 API；支持页增加中英双语反馈模板。
- 英文 README 的 DDI 获取说明与随包载荷对齐，区分 Apple USB 支持安装程序和开发者磁盘镜像。
- 第三方声明移除过时的 `v1.8.3` 当前基线，说明源码清单与具体 Release 载荷的适用范围。
- 精简打包、组件审查和 UxPlay 404 报告增加历史范围说明；竞品调研中的旧文档基线问题补记处理结果。

第二轮结果：198 份 Markdown、869 个本地链接/图片引用检查通过，226 张表格列数一致，
`git diff --check` 通过。本轮仅调整文档文字、导航和反馈模板，沿用第一轮示例验证的适用范围。
