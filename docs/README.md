# 文档

[简体中文 README](../README.zh-CN.md) · [English documentation](en/README.md)

现行文档以本地 main 的 v1.8.5-pre7 为基准。这是 1.8.5 正式发布前的最后一个预览版。[pre7 发布说明](zh-CN/RELEASE_NOTES.md)

公开安装包的功能以其各自的发布说明为准。[公开发行版](https://github.com/RayrenSX/iPhoneMirror/releases)

## 使用与支持

| 任务 | 文档 |
|---|---|
| 下载与快速开始 | [English](../README.md) · [简体中文](../README.zh-CN.md) · [日本語](../README.ja.md) |
| 安装、首次设置、投屏、反控与常见问题 | [使用指南](USER_GUIDE.md) · [首次设置](FIRST_RUN_SETUP.md) |
| 配对无线投屏、预览工具与独立窗口 | [有线模式](USER_GUIDE.md#有线模式) · [无线发现](PAIRED_WIRELESS_DISCOVERY.md) · [画面、声音和预览工具](USER_GUIDE.md#画面声音和预览工具) · [独立窗口](USER_GUIDE.md#独立窗口与全屏) |
| 键位映射与多设备 | [键位映射](KEYBOARD_MAPPING.md) · [多设备控屏](MULTI_DEVICE_CONTROL.md) |
| 录制、直播、OBS 与虚拟摄像头 | [媒体输出](OBS_OUTPUT.md) |
| 应用、文件与资源管理器映射 | [设备管理](DEV_DEVICE_HUB.zh-CN.md) |
| IPA 签名与安装 | [IPA 安装](IPA_INSTALL.md) |
| 自动化与 AI 客户端 | [HTTP API](zh-CN/AUTOMATION.md) · [Python SDK / stdio MCP](zh-CN/SDK.md) |
| 问题反馈与安全漏洞 | [问题反馈与私密漏洞报告](zh-CN/SUPPORT.md) |
| 支持作者 | [微信、支付宝与 PayPal 状态](zh-CN/DONATE.md) · [多语言赞赏说明](DONATE.md) |

## 开发与维护

| 领域 | 文档 |
|---|---|
| 构建、测试、打包与贡献 | [开发指南](DEVELOPMENT.md) · [贡献规范](zh-CN/CONTRIBUTING.md) · [工作区空间维护](WORKSPACE_STORAGE.md) |
| 模块与数据流 | [软件架构](ARCHITECTURE.md) |
| USB 音视频 | [协议](PROTOCOL.md) · [D3D11 解码与显示](D3D11_RENDERING.md) · [WASAPI 音频](WASAPI_AUDIO.md) |
| 驱动 | [依赖及交付](DRIVER_DEPENDENCIES.md) · [父驱动管理](PARENT_DRIVER_MANAGEMENT.md) |
| 反控 | [架构](USB_TOUCH_ARCHITECTURE.md) · [桥接接口](USB_TOUCH_BRIDGE_USAGE.md) · [兼容性](USB_TOUCH_DEVICE_COMPATIBILITY.md) · [五点触控](FIVE_POINT_TOUCH.md) · [键盘焦点](KEYBOARD-FOCUS-CONTROL.md) |
| 无线设备发现 | [配对身份与 CoreDevice 条件](PAIRED_WIRELESS_DISCOVERY.md) |
| 设置与界面 | [首次设置](FIRST_RUN_SETUP.md) · [工具栏](TOP_TOOLBAR_CUSTOMIZATION.zh-CN.md) · [错误与确认目录](ERROR_DIALOG_CATALOG.md) · [本地化](LOCALIZATION.md) |
| 机型、圆角与边框 | [面板目录](APPLE_DISPLAY_CATALOG.md) · [圆角拟合](DEVICE_CORNER_PROFILES.md) · [边框来源](device-frames.md) · [覆盖目录](device-frame-coverage.md) |
| 许可与来源 | [项目许可证](../LICENSE) · [第三方声明](zh-CN/THIRD_PARTY_NOTICES.md) · [桥接组件](USB_TOUCH_THIRD_PARTY_LICENSES.md) · [收款码完整性](DONATION_INTEGRITY.zh-CN.md) |

## 版本与研究记录

- [Changelog](../CHANGELOG.md) · [繁體中文變更記錄](../CHANGELOG.zh-TW.md) · [发布说明](releases/README.md) · [后续计划](ROADMAP.md)
- [保留的设计与故障研究](ARCHIVE.md)：协议依据、驱动安全和有独立价值的验证记录。
- [推流验证环境](../tools/srs-lab/README.md) · [旧 SRS 环境](../tools/srs-test/README.md) · [原生协议夹具](../src/Core/tests/fixtures/README.md) · [合成 HEVC 夹具](../tests/fixtures/coredevice/README.md)
- [无线组件来源](../third_party/airplay-server/SOURCE.md) · [UxPlay 来源](../third_party/uxplay/SOURCE.md) · [桥接构建配方](../scripts/usb-bridge-recipe/SOURCE.md)
- [社区行为准则](../CODE_OF_CONDUCT.md) · [PR 模板](../.github/PULL_REQUEST_TEMPLATE.md)

## 文档维护

用户流程变化更新使用指南和相关语言 README；接口、依赖与构建变化更新对应专题。功能实现、源码测试通过和真实设备验收应分别表述，发布说明只描述对应版本。

重复的审查过程、交付日志、逐键生成表和本机截图放在忽略的 `work/` 或 `outputs/diagnostics/`，不另建现行教程。只保留能解释设计、复现重要故障或提供独立证据的研究记录，并写明日期、范围及未验证项。

新增文档从本页或研究／发布索引链接；移动、合并、删除后同步引用。从仓库根目录运行 `python scripts/verify_docs.py` 检查本地链接、图片、锚点、围栏和索引可达性；该检查不验证外部网页或设备行为。第三方许可证和版权文本保留原文。
