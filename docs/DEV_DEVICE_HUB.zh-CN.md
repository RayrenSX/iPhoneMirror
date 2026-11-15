# dev：设备管理与脚本接入

入口为主窗口左侧的「设备管理」图标。面板在投屏右侧展开，跟随当前选中的设备；开发者工具中的入口也打开同一面板。旧的重复实验室窗口已移除。

应用、进程、描述文件和文件均可点击行内「…」或右键操作；菜单会选中指向的条目，空白处只显示页面操作。应用和文件支持双击及 Enter；F5 刷新当前页，保留列表选择和滚动位置。安装等操作放在标题栏「更多」菜单，底部只显示数量或简短状态。

搜索支持一键清除、Esc 清空及 Ctrl+F 聚焦，进程也可按名称、路径或 PID 搜索。搜索无结果时可直接重置条件；空描述文件、空目录和未开始的日志均有对应引导。文件页以「加载更多」追加内容，刷新保留已加载页；返回上级会选中刚离开的目录，支持 Backspace / Alt+↑，Ctrl+L 编辑路径、Esc 取消编辑。读取目录失败会保留原地址和内容。日志的开始 / 停止按钮按当前状态切换，停止后仍可筛选和保存。

## 已保留的功能

| 页面或入口 | 功能 |
|---|---|
| 信息 | 设备、系统、电池、存储；只读信息可选中复制 |
| 应用 | 名称 / Bundle ID 搜索；用户、系统、开发、轻 App 筛选；真实图标；打开、安装 IPA、卸载；切换进程列表后可结束进程 |
| 描述文件 | Provisioning / 配置描述文件列表、安装、移除；配置安装仍需手机确认 |
| 文件 | 媒体文件、崩溃报告；目录浏览、分页、分块另存为、报告导出 |
| 日志 | 开始、停止、筛选、跟随和保存实时日志 |
| 控制 | 主屏幕、应用切换器、锁屏、音量；读取和应用手机设置；模拟定位 / 恢复；确认后重启或关机 |
| 更多 → 重放录制文件 | 离线打开 `.imrtp`，经过真实 RTP 组帧、补丁解码器、私有管道和原生预览；压缩包附离线样本 |
| Python SDK / MCP | 鉴权 HTTP API、设备选择、控制租约、截图、输入和设备管理；36 个 MCP 工具 |

不支持的手机设置项在读取后直接移除显示。应用安装需要有效签名及设备许可，不能绕过 iOS 安装限制。定位、安装、删除和电源操作不会自动重试。

## 已删除的功能

2026-11-15 实机检查未能建立可用的 CoreDevice 原生视频流：iPhone 12 mini / iOS 18.7.8 返回不支持；iPhone 17 / iOS 27.2 支持信息存在，但重复启动被系统以 9022 拒绝，即使用户结束通话后复测仍未取得画面。

按「不可用且未能修复就删除」的要求，本版移除了原生投屏启停、播放其音频、原生媒体状态，以及依赖它的 HEVC / RTP 录制。界面、共享桥动作清单、HTTP 路由、OpenAPI、Python SDK 和 MCP 均同步清理，不留下无效入口。常规 USB / AirPlay 投屏、截图及现有录像继续使用原有入口。

离线编码流重放通过真实封装桥与解码链路测试，作为独立工具保留。解码器与必要的 RTP/RTCP 代码也用于离线验证及现有 HID 认证保活，不再宣传为可用的原生媒体功能。

## 连接与恢复

设备需要解锁、信任电脑并准备管理连接；部分控制功能还需要开发者模式、匹配 DDI 和既有控制绑定。

开始或停止 USB 投屏可能触发管理通道重连。面板保留当前列表、筛选和选择，暂停设备操作；通道恢复后自动刷新当前页，无需切换设备。就绪判断来自管理桥，独立于普通 USB 枚举状态。面板显示期间每 500 毫秒仅检查内存连接状态，不额外扫描、重连或执行设备操作。切换设备、切页或隐藏面板会取消旧任务，旧响应和旧菜单不能影响新设备。

刷新、加载、断线状态均使用同一套主题模板，不再使用会变白的系统 ListBox 禁用模板。既有「应用、投屏、反控」三类常规设置保持原有组织。

## 脚本与构建

```powershell
python -m pip install -e './sdk/python[mcp]'
# 使用说明与鉴权配置见 sdk/python/README.md
./scripts/build_dev.ps1 -MsysRoot C:/msys64
```

需要 .NET 10、Visual Studio C++ 工具链、CMake、Python 3.13；重建解码器还需 MSYS2 UCRT64 gcc、make、patch、nasm。脚本构建现有桥、离线解码器、原生 Core 和应用。交付从整合后的 main 构建到 `outputs/iPhoneMirror-main`，不覆盖正在运行的目录。

```powershell
python -m unittest discover -s tests -p device_hub_test.py
python -m unittest discover -s tests -p device_management_test.py
python -m unittest discover -s tests -p python_sdk_test.py
python -m unittest discover -s tests -p coredevice_media_test.py
# 构建 Runtime.Tests 后运行：
IPhoneMirror.App.Runtime.Tests.exe --device-hub-sidebar <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --automation-api
IPhoneMirror.App.Runtime.Tests.exe --coredevice-replay <样本.imrtp>
```

实机结果、保留范围及未执行的破坏性测试见 [验收记录](RPLAYHUB_FEATURE_MATRIX.zh-CN.md)。解码器许可证和来源见 [third_party/rplayhub](../third_party/rplayhub/README.md)。
