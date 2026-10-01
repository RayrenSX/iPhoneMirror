# 文档索引

本索引按用途组织仓库文档。2026-10-01 核对时，主程序与驱动管理器的源码版本为
`1.8.4-test4`；这不表示该版本已经公开发布。下载版本以
[GitHub Releases](https://github.com/RayrenSX/iPhoneMirror/releases) 为准。
使用旧版安装包时，请同时阅读对应的发布说明；当前工作区的功能可能尚未进入该安装包。

## 从哪里开始

| 任务 | 入口 |
|---|---|
| 下载、了解功能 | [中文 README](../README.md) · [English README](../README.en.md) |
| 安装驱动、投屏、反控、录制 | [完整使用教程](USER_GUIDE.md) |
| 编译、运行测试、打包 | [开发与测试指南](DEVELOPMENT.md) · [贡献规范](../CONTRIBUTING.md) |
| 了解模块和进程边界 | [软件架构](ARCHITECTURE.md) |
| 报告问题或安全漏洞 | [支持说明](../SUPPORT.md) · [安全政策](../SECURITY.md) |
| 查版本变化和待办 | [变更记录](../CHANGELOG.md) · [发布说明目录](releases/) · [路线图](ROADMAP.md) |
| 查许可与来源 | [项目许可证](../LICENSE) · [第三方声明](../THIRD_PARTY_NOTICES.md) · [桥接组件说明](USB_TOUCH_THIRD_PARTY_LICENSES.md) |

## 持续维护的技术说明

| 领域 | 文档及用途 |
|---|---|
| USB 协议 | [PROTOCOL](PROTOCOL.md)：配置切换、QuickTime 包、CoreMedia 与协商 |
| 显示与解码 | [D3D11_RENDERING](D3D11_RENDERING.md)：生产显示路径；[DECODER_GUI_ANALYSIS](DECODER_GUI_ANALYSIS.md)：设计依据与历史测量 |
| 设备外形 | [DEVICE_CORNER_PROFILES](DEVICE_CORNER_PROFILES.md)：ProductType 匹配、圆角与拟合边界 |
| 音频与输出 | [WASAPI_AUDIO](WASAPI_AUDIO.md)：播放与音频输出；[OBS_OUTPUT](OBS_OUTPUT.md)：OBS、录制、推流、虚拟摄像头 |
| 驱动 | [DRIVER_DEPENDENCIES](DRIVER_DEPENDENCIES.md)：依赖及交付；[PARENT_DRIVER_MANAGEMENT](PARENT_DRIVER_MANAGEMENT.md)：父设备绑定、确认与恢复边界 |
| 反控概览 | [USB_DIRECT_TOUCH](USB_DIRECT_TOUCH.md)：前置条件；[USB_TOUCH_ARCHITECTURE](USB_TOUCH_ARCHITECTURE.md)：进程与设备链路；[USB_TOUCH_DESIGN](USB_TOUCH_DESIGN.md)：状态机设计 |
| 反控接口 | [USB_TOUCH_BRIDGE_USAGE](USB_TOUCH_BRIDGE_USAGE.md)：命令行、stdin/stdout、触控与键盘消息 |
| 兼容性 | [USB_TOUCH_DEVICE_COMPATIBILITY](USB_TOUCH_DEVICE_COMPATIBILITY.md)：实测记录及 DDI/HID 验证条件 |
| UI 入口 | [REVERSE_CONTROL_UI_INVENTORY](REVERSE_CONTROL_UI_INVENTORY.md)：反控入口；[ERROR_DIALOG_CATALOG](ERROR_DIALOG_CATALOG.md)：错误和确认窗口 |
| 输入焦点 | [KEYBOARD-FOCUS-CONTROL](KEYBOARD-FOCUS-CONTROL.md)：本地文本输入、快捷键与手机键盘路由 |
| 多设备控屏 | [MULTI_DEVICE_CONTROL](MULTI_DEVICE_CONTROL.md)：连接保留、选项卡与独立窗口焦点、验证范围 |

## 专项审计与验证记录

这些文件保留审查时的发现、修复和验证结果。它们是特定工作区、日期或设备下的记录，
不是所有版本的功能保证，也不表示本次文档整理重新执行了其中的真机或 UI 测试。
发生冲突时，先核对当前源码和目标版本，再更新上面的持续维护文档；不要覆盖历史证据。

| 主题 | 记录 |
|---|---|
| 项目文档核对 | [DOCUMENTATION_AUDIT_2026-10-01](DOCUMENTATION_AUDIT_2026-10-01.md) |
| GitHub 问题 | [2026-09-23 审计](GITHUB_ISSUE_AUDIT_2026-09-23.md) · [2026-10-01 审计](GITHUB_ISSUE_AUDIT_2026-10-01.md) · [2026-10-01 修复记录](GITHUB_ISSUE_FIXES_2026-10-01.md) |
| 驱动错误 | [DRIVER_ERROR_AUDIT](DRIVER_ERROR_AUDIT.md) |
| 有线恢复 | [WIRED_RECOVERY_FIX](WIRED_RECOVERY_FIX.md) · [WIRED_RECOVERY_REAUDIT](WIRED_RECOVERY_REAUDIT.md) · [WIRED_RECOVERY_LATENCY](WIRED_RECOVERY_LATENCY.md) · [WIRED_CONTROL_COUNTDOWN_FIX](WIRED_CONTROL_COUNTDOWN_FIX.md) |
| 交互与性能 | [INTERACTION-STABILITY-AUDIT](INTERACTION-STABILITY-AUDIT.md) · [PERFORMANCE-AUDIT](PERFORMANCE-AUDIT.md) |
| UI 标准与一致性 | [UI-STANDARDS-REVIEW](UI-STANDARDS-REVIEW.md) · [UI-CONSISTENCY-AUDIT](UI-CONSISTENCY-AUDIT.md) |
| 布局与圆角 | [UI-ADAPTIVE-LAYOUT-AUDIT](UI-ADAPTIVE-LAYOUT-AUDIT.md) · [UI-ROUNDED-WINDOW-AUDIT](UI-ROUNDED-WINDOW-AUDIT.md) |
| 本地化 | [LOCALIZATION-AUDIT](LOCALIZATION-AUDIT.md) · [LOCALIZATION-RECHECK](LOCALIZATION-RECHECK.md) · [LOCALIZATION-VALIDATION](LOCALIZATION-VALIDATION.md) · [机器可读改动记录](localization-audit-changes.json) |

[USB_TOUCH_DEMO_README.txt](USB_TOUCH_DEMO_README.txt) 是早期独立 Demo 的交付说明，
不代表 `build.ps1` 会生成该 Demo。当前桥接器的构建配方见
[scripts/usb-bridge-recipe/SOURCE.md](../scripts/usb-bridge-recipe/SOURCE.md)。

## 文档维护约定

- 用户操作变化：同步中英文 README 和使用教程，再补技术说明。
- 协议、驱动、端口、进程或输出行为变化：更新对应专题和架构说明，写明实现入口。
- 构建与测试变化：同步开发指南、贡献规范和实际脚本；示例默认读取项目版本，避免锁死旧版本。
- 发布说明描述该版本的变化；审计记录保留日期、范围、证据和未验证项。
- 截图、日志及示例使用脱敏设备标识；机器绝对路径改为仓库相对路径或明确的占位路径。
- 新增文档时更新本索引；移动文件时同步所有相对链接。
