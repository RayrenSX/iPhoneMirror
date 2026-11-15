# rPlayHub 功能对照与验收边界

核验日期：2026-11-15。参考 [rPlayHub README](https://github.com/rPlayAI/rPlayHub/blob/fe6a7a9f1cff431acfda142cd3564fc8e0e849a3/README.md)
和 [实际 API 分发代码](https://github.com/rPlayAI/rPlayHub/blob/fe6a7a9f1cff431acfda142cd3564fc8e0e849a3/host-c/api_server.c)，
固定提交 fe6a7a9f1cff431acfda142cd3564fc8e0e849a3；核验时 GitHub main 仍是该提交。
旧协议文档与 README 的部分媒体状态描述落后于源码，本表以实际实现为准。

“已接入”表示桌面、桥和相应 API/SDK 路径存在，并通过列出的离线验证。
**本次没有操作真实 iPhone 验收这些新增能力，不能把“已接入”解释为所有 iOS/DDI/设备均已实测通过。**

| 上游已实现能力 | iPhoneMirror 实现与入口 | 自动验证 | 状态 |
|---|---|---|---|
| CoreDevice HEVC 投屏、AAC-ELD 音频 | 设备实验室 → 媒体；既有配对、DDI、USB/无线桥 → 补丁解码器 → 原生渲染/截图/录像 | 实际解码进程、18 帧旋转重放、原生协议测试 | 已接入，实机待验收 |
| 点击、滑动、主屏幕、App 切换器 | 现有反控/自动化；设备页系统按钮；press_button；沿用 Indigo 按钮与释放保障 | 既有 166 项 USB 回归、按钮取消释放、租约测试 | 已接入，实机效果待验收 |
| 设备选择、信息、电池、存储、隧道 | 设备下拉框、设备页；device_info、tunnel_info、capabilities | 固定设备通道、代次/动作分发与 API 权限检查 | 已接入；各信息域可独立不可用 |
| App 列表、过滤、图标、启动 | 应用页；用户/系统/开发/轻 App 过滤；AppService streaming + InstallationProxy 只读回退；SpringBoard 图标 | 归一化、无隐式结束/重试、真实 PMD 调用与 SDK/MCP | 已接入，实机待验收 |
| IPA 安装、App 卸载 | 应用页选择文件/确认卸载；HTTP 有界上传；AFC 分块暂存 → InstallationProxy | HTTP 上传权限/路径/清理；失败时移除手机暂存文件；不整包读入内存 | 已接入；签名、许可和 iOS 限制仍由手机执行 |
| 进程列表、终止 | 进程页；list_processes、terminate_app（SIGTERM / SIGKILL） | XPC 数字 PID 保留、参数限制、租约与单次分发 | 已接入，实机待验收 |
| Provisioning / 配置描述文件列表、安装、移除 | 描述文件页；Misagent / MCInstall；配置安装保留手机确认 | 缺失配置不假报成功、私有路径不暴露、文件上传与清理 | 已接入；MCInstall 可能被 iOS/设备策略拒绝 |
| Media 文件浏览/读取 | 文件与报告页：media；分页与 192 KiB 分块下载；list_dir、read_file | 路径穿越/符号链接拒绝、变更检测、分块边界、已有文件保护 | 已接入，实机待验收 |
| 崩溃、spin、日志报告浏览/导出 | 文件与报告页：crashes；CrashMover 刷新、CrashCopy AFC、递归导出 | 目录深度/文件/字节上限、Windows 文件名检查；通道清理 | 已接入，真实报告集合待验收 |
| 实时 syslog | 实时日志页、开始/停止/保存；游标轮询；MCP/SDK | 超长行限制、2048 条环形缓存、丢失计数、订阅身份、60 秒闲置释放 | 已接入，实机待验收 |
| 手机外观与辅助功能设置 | 手机设置页；边框、对比度、动态/透明度、VoiceOver、颜色滤镜、Liquid Glass、文字大小、浅/深色、更大字体；另有只读 look-and-feel | 使用真实 PMD CoreDevice 封装核对 actionIdentifier 与输入结构；未知设置拒绝 | 已接入；逐项可用性取决于 iOS/DDI |
| 模拟定位/恢复定位 | 手机设置页经纬度与清除；独立 locationservice | 服务选择、坐标边界、NaN/布尔拒绝、精确 XPC 结构 | 已接入，实机待验收 |
| 重启、关机、休眠 | 设备页确认后调用 Diagnostics；API 需租约 | 动作白名单、读写权限、无自动重试 | 已接入，未对用户手机执行 |
| 截图与 HEVC 录制 | 现有截图/录像；媒体页新增关键帧门控 .h265 及 .imrtp 录制 | 录制从带 VPS/SPS/PPS 的完整 IRAP 起始、磁盘错误隔离、1 GiB 上限 | 已接入；新音频/RVRA 画质实机待验收 |
| 无手机编码流重放 | 媒体页打开 .imrtp；复用实际 RTP/解码/原生画面路径 | 横竖屏、丢包、序号回绕、损坏数据、恢复、逐帧哈希 | 已接入并离线通过 |
| 媒体反馈与恢复 | SR/RR+SDES、RCTL、PLI、完整 AU 回执；丢包等关键帧；解码器重启；隧道修复重绑 | 杀死真实解码器后恢复 9 帧；音频错误不封锁视频；重绑前取消旧任务 | 已接入；长期实机恢复待验收 |
| 脚本/AI 接入 | 复用鉴权 HTTP；Python SDK；41 个 stdio MCP 工具；31 项共享桥动作清单 | 真实 HTTP 服务和真实 MCP stdio 会话；API 权限、独占租约、旧代次、上传清理 | 已接入并离线通过 |

接口沿用本项目的鉴权、设备 ID、控制租约和私有媒体管道；不承诺兼容上游 9876/9877 内部 TCP 帧协议或 Mac 客户端。
现有原生预览、截图、录像和多设备管理仍通过本项目的窗口和会话管理提供。
同一设备的互斥操作遇到忙状态会明确失败，不自动切换手机或重试输入。

## 上游规划项与平台边界

下列项目在参考提交的 README 中明确列为 **Planned**，本次没有实现，也没有把它们计入已完成项：

- 由真实运动传感器驱动的 3D iPhone、折叠视图和传感器感知视角；需要上游设想的可选手机伴侣 App。
- 类 adb 的长期常驻多设备服务与轻量 CLI 架构。
- 跨其他网络的远程 iPhone。

Mac 的 Xcode Simulator 设备源和 macOS/Linux 安装包属于平台特有集成，不在 Windows 可执行文件范围内。
上游 README 声明其协议目标为 iOS 27+；本项目不据此承诺更早 iOS 的新 CoreDevice 功能可用。
常规既有 USB/AirPlay 功能的支持范围不由这张新增功能表扩展。

## 可复核路径

- 桥协议与政策：tools/device_hub_contract.json、tools/device_hub.py、tools/device_management.py。
- 桌面：src/App/Windows/DeviceHubWindow*.cs；三类常规设置的组织保持不变。
- HTTP / 安装：src/App/Automation/AutomationApiHost.DeviceHub.cs、src/App/Services/Automation/AutomationService.DeviceHub.cs。
- SDK / MCP：sdk/python/src/iphonemirror/。
- 验证：tests/device_management_test.py、tests/device_hub_test.py、tests/coredevice_media_test.py、
  tests/python_sdk_test.py、src/App.Runtime.Tests/DeviceHubTests.cs。

构建和调用见 [设备实验室说明](DEV_DEVICE_HUB.zh-CN.md) 与 [Python SDK](../sdk/python/README.md)。
