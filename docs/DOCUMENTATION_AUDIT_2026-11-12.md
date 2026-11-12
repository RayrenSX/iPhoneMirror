# 全库文档复审（2026-11-12）

## 基线与范围

在 `C:/Users/Ray/Documents/iphoneMirror` 的 `main` 上审查，起始 HEAD 为 `1eb833a`。
开始时工作区干净；`git fetch origin` 成功，本地与远端主线一致。主程序和驱动管理器项目版本均为 `1.8.5-pre5`。

全库静态检查覆盖 Git 跟踪及未忽略的 Markdown，包括根目录、技术指南、历史审计、Issue、发布记录、工具和第三方来源说明。
内容核对重点为上次 2026-11-05 审查后变化涉及的三语 README、使用教程、开发指南、架构、路线图、工具栏和 HTTP API。
历史报告按原日期解释，第三方许可证保留原文；本轮不是对全部历史结论重新验收，也未核验外部 URL 或线上 Release 资产。

## 发现与更新

| 问题 | 更新 | 核对依据 |
|---|---|---|
| 当前指南仍使用 pre4 基线 | 六份当前指南统一为 pre5，标明源码提交；保留历史报告中的 pre4 | 主程序及驱动管理器 `.csproj`、Git HEAD |
| 三语首页近期变化落后 | 同步工具栏、设置快捷键、实验性蓝牙绝对鼠标和截图预算，链接 pre5 说明 | `docs/releases/v1.8.5-pre5.md` 与近期提交 |
| 三语 README 把 RAOP 写成固定端口 | 改为默认 5001、可配置，区分 UxPlay 与动态媒体端口 | `WirelessReceiverService.DefaultRaopPort`、`IsValidRaopPort` |
| 使用教程仍描述旧版工具栏浏览按钮和箭头排序 | 改为拖动排序、整行适配、隐藏时滑入、Esc 分级退出与取消恢复 | `PreviewToolbarSettingsWindow.xaml` 及 `.xaml.cs`、`.Drag.cs`、`.Focus.cs` |
| 设置分类说明顺序与当前界面不一致 | 按应用、投屏、反控排列，保留直接显示分组的约定 | 当前设置实现与 `AGENTS.md` |
| 专项验证目录被称为当前交付目录 | 标明历史本机产物；开发指南说明主线交付目录和脚本默认输出的区别 | `AGENTS.md`、`build.ps1` 的 `PublishRoot` 与 `Clear-TestOutputs` |
| UI 蓝牙绝对鼠标容易被误认为 API 绝对触控 | 在架构和 API 说明中区分 UI 输入与 API 能力 | 现有 API 能力契约、蓝牙预览输入路径 |
| 使用教程遗漏截图预算入口 | 补充 1–30 次、新配置默认值、旧值保留及帧率边界 | `AutomationSettings`、`DeveloperToolsWindow.Automation`、`AutomationApiHost` |
| 维护约定遗漏日文同步 | 将日文 README 纳入同步约定，并补齐本次审查索引 | 三语入口与文档索引 |

## 验证与限制

- 修改前：`python scripts/verify_docs.py` 检查 206 份 Markdown、914 个本地链接/图片引用，0 错误。
- 修改后：207 份 Markdown、921 个本地链接/图片引用，0 错误；新增记录可从主索引到达。
- `git diff --check` 通过；最终差异仅涉及文档。
- 本次没有修改应用代码、版本号、发布清单、许可证或二进制，没有执行构建、驱动操作、真机输入或 UI 性能验收。
- 截图继续保留历史图片，并沿用教程中的版本提示；未生成新截图。线上发布状态、外部链接可用性与历史测试结果不在本次验证范围内。
