# 项目与文档核对记录（2026-10-01）

## 范围与基线

本次延续中断的“审查完整项目、梳理各个文档、完善文档”任务，按模块检查当前工作区的
应用、核心、无线宿主、UxPlay、驱动管理器、反控桥、共享 UI、媒体输出、更新与构建测试入口，
据此修正文档。主程序与驱动管理器声明 `1.8.4-test4`，原生 API 为 `18`，无线 IPC 为 `7`。
工作区已有未提交的实现和专项审计资料，本次保留这些改动，不将其视为已发布版本。

这是源码与文档的一致性核对，不是逐行安全审计、全量性能评估或真机兼容性认证。
既有审计中的测试结论保持原适用范围；本次没有重新执行其设备、UI 或驱动变更测试。

## 已处理的问题

| 问题 | 实现依据 | 文档处理 |
|---|---|---|
| 无统一入口，持续维护说明与历史记录混在一起 | 根目录、`docs` 与 `docs/releases` 文档清单 | 新增[索引](README.md)，按用户、开发、技术专题、审计和发布记录导航 |
| README 将源码版本与最新发布混用，打包例子固定 1.8.3 | 两个应用 `.csproj`、`package_release.ps1` 的版本读取与一致性检查 | 改为项目版本驱动的打包命令，公开版本指向 Releases |
| 中文 README 仍称构建自动克隆桥接上游 | `build.ps1`、`UsbBridgeBuildSource.ps1`、配方 `SOURCE.md` | 说明仓库内 Python 配方、版本固定、环境变量覆盖和 Rust schema 差异 |
| 测试列表和 CI 跳过条件不完整 | `build.ps1` 测试段、`windows-build.yml`、各测试项目 | 新增[开发与测试指南](DEVELOPMENT.md)，区分默认、专项、交互和实机入口 |
| 架构遗漏 USBMux 共存、UxPlay 和共享模块 | `MainViewModel`、Python capture mux、`UxPlayHost`、`Shared/SharedUI` | 补充反控与视频分路、进程边界、释放顺序和备用方案限制 |
| 把压缩队列等同于显示端最新帧缓存，恢复写成无条件保证 | `CaptureSession.cpp`、停止/恢复代码 | 分开描述压缩 FIFO、解码帧缓存及有界恢复的实际边界 |
| 桥接双向格式写成相同格式，输入上限仍为 64 KiB | `usb_touch_bridge.py` 中 `IpcChannel`、`MAX_FRAME_SIZE` 和协议常量 | 改为 stdin 长度前缀 / stdout JSON Lines、4 MiB；补充按键和剪贴板消息 |
| 把单个桥接 EXE 当成完整部署载荷 | 运行时 schema 1 校验、PyInstaller onedir 配方 | 明确 EXE、`_internal`、清单必须一起部署，Demo 文档标为历史说明 |
| 对未来 iOS 的认证能力推断过度，兼容表混用早期结果 | 服务 `257` 验证、direct fallback、专项恢复记录 | 改为按设备/DDI/桥接版本验证，修复表格列数并保留早期证据 |
| 驱动贡献说明误称驱动逻辑不在本应用包，依赖表误述 WinUSB | `src/DriverInstaller`、父驱动策略与确认入口 | 明确独立 EXE 随包发布；WinUSB 不是正常 QuickTime 采集目标 |
| 渲染文档仍把所有圆角描述为单组 iPhone 参数 | `DeviceCornerProfileResolver`、D3D11 设备外形参数 | 改为按设备族/比例拟合并链接圆角说明 |
| 教程未反映可选麦克风，精简包暗示任意 FFmpeg 可用 | `MediaOutputMicrophone`、`MediaOutputService`、`RuntimeBinaryIntegrity` | 补混音默认关闭及音频前提，明确外部 FFmpeg 仍受固定哈希限制 |
| 反控许可页称全部开源且依赖版本过期 | 配方 LICENSE/requirements、README 已有来源声明 | 区分主项目、桥接许可及 USBMux 来源，修正直接依赖版本与来源链接 |

## 验证方式

- 扫描根目录和 `docs` 下的 Markdown 本地相对链接、图片路径及标题锚点。
- 检查本次变更文档的代码围栏、表格列数，以及 Python、JSON/JSON Lines 示例语法。
- 用 PowerShell 解析器检查变更文档中的 PowerShell 示例；只解析，不执行驱动、设备或发布命令。
- 检查 `docs` 顶层 Markdown 是否全部进入索引，并执行文档范围的 `git diff --check`。

本次结果：82 份 Markdown、196 个本地链接/锚点检查无失败，顶层文档全部已索引；
变更文档的围栏和表格检查通过；4 段 Python/JSON/JSON Lines 示例及 23 段 PowerShell
示例语法检查通过。触控与键盘 JSON 示例分别交给从当前源码提取的纯解码函数验证，
均被接受；4 MiB 输入上限和协议号 `2` 与源码一致。文档范围 `git diff --check` 通过。
检查没有执行示例中的真实设备输入或发布命令。

## 尚需单独处理的事项

- README 已记录三个 USBMux 来源文件缺少完整许可资料；本次仅准确保留该状态，没有取得或
  证明额外授权。文档整理不能替代对这些来源的授权处理。
- 新增恢复/UI/父驱动等实现的完整回归结果，应继续由对应专项记录维护；本次不替其证明通过。
- 旧截图仍可用于理解功能位置，但需在实际发布版本确定后重新核对布局和按钮名称。
- 发布资产、在线版本和更新清单没有上传或同步；源码版本不能当作最新公开版。

后续功能变更请优先同步[文档索引](README.md)所列的持续维护文档，历史报告保留为证据。
