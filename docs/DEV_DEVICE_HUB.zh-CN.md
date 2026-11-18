# dev：设备管理与脚本接入

入口为主窗口左侧的「设备管理」图标。面板在投屏右侧展开，跟随当前选中的设备，只显示「应用」和「文件」。设备信息、连接能力、描述文件、日志、手机控制与 IPA 安装位于「开发者工具 → 设备工具」。两个入口均跟随左侧当前选中的设备并复用已建立的连接。开发者窗口切回常规工具或关闭后会停止该页的后台任务。

应用、进程、描述文件和文件均可点击行内「…」或右键操作；菜单会选中指向的条目，空白处只显示页面操作。应用和文件支持双击及 Enter；F5 刷新当前页，保留列表选择和滚动位置。IPA 和描述文件安装放在开发者设备工具的标题栏「更多」菜单，主窗口应用菜单不再提供 IPA 安装，底部只显示数量或简短状态。

搜索支持一键清除、Esc 清空及 Ctrl+F 聚焦，进程也可按名称、路径或 PID 搜索。搜索无结果时可直接重置条件；空描述文件、空目录和未开始的日志均有对应引导。文件页以「加载更多」追加内容，刷新保留已加载页；返回上级会选中刚离开的目录，支持 Backspace / Alt+↑，Ctrl+L 编辑路径、Esc 取消编辑。读取目录失败会保留原地址和内容。日志的开始 / 停止按钮按当前状态切换，停止后仍可筛选和保存。

应用按 A–Z 首字母分组，中文按拼音首字母归组，数字与其他字符归入「#」。列表保持虚拟化；图标在列表显示后按可见范围加载，缩略图保存在 `%LOCALAPPDATA%/iPhoneMirror/AppIcons`，按设备、Bundle ID 和版本隔离。刷新和重启可复用缓存，版本更新会重新读取；磁盘不可写或缓存损坏时仍可正常显示列表并从手机补取图标。

应用右键和行内「…」菜单提供「以 Metal 性能 HUD 重启」。确认后只关闭并重新启动所选设备上的所选 App，使用 `MTL_HUD_ENABLED=1` 请求 Apple Metal 性能浮层；未保存内容可能丢失。普通「打开」、双击和 Enter 仍只打开应用，不重启旧进程。HUD 通常显示 App 侧 FPS、帧间隔、GPU 时间和内存等，具体取决于系统和应用；成功状态只表示启动请求完成，需要在手机或投屏画面确认浮层。结束目标 App 后从手机图标普通启动可恢复不带 HUD 的进程。

此功能复用设备管理的开发者模式、匹配 DDI 和现有设备隧道，无需在手机安装额外 App。只对支持 Metal HUD 的目标 App 生效，不是系统全局监控；部分应用可能拒绝开发者启动。App 侧 FPS 与底部投屏接收帧率含义不同。基础功能不收集历史性能曲线或导出报表。该动作也可经现有鉴权 Hub 接口以 `restart_with_metal_hud` 调用，仅接受 `bundleId`，需要当前控制租约。

文件页的「导入文件…」可多选电脑上的文件，上传到当前已打开的媒体目录；尚未提交的地址编辑不会改变上传位置。同名文件自动以「文件名 (1).扩展名」等名称保留副本。完成后自动刷新；中途失败会保留已完成的文件并报告数量，未完成的文件会清理。崩溃报告目录只支持读取和导出。导入到媒体目录不会自动添加到 iOS「照片」图库。

普通媒体文件的右键和行内「…」菜单提供「删除文件」，确认框显示名称和完整路径。确认后只删除所选文件并刷新当前目录，不递归删除文件夹；若文件的大小或修改时间已变化，会要求刷新后重新选择。取消确认、切换设备或切换页面后，旧菜单和确认结果不会触发删除。删除为界面私有操作，不通过通用 HTTP Hub API 暴露。

## 已保留的功能

| 页面或入口 | 功能 |
|---|---|
| 开发者工具：信息 | 设备、系统、电池、存储、可用能力和隧道信息；只读信息可选中复制 |
| 应用 | 名称 / Bundle ID 搜索；用户、系统、开发、轻 App 筛选；真实图标；打开、卸载；切换进程列表后可结束进程；安装 IPA 位于开发者设备工具 |
| 开发者工具：描述文件 | Provisioning / 配置描述文件列表、安装、移除；配置安装仍需手机确认 |
| 文件 | 媒体文件、应用共享文档、崩溃报告；目录浏览、分页、文件导入、确认后删除文件、分块另存为、报告导出；媒体和共享文档可映射到资源管理器 |
| 开发者工具：日志 | 开始、停止、筛选、跟随和保存实时日志 |
| 开发者工具：控制 | 主屏幕、应用切换器、锁屏、音量；读取和应用手机设置；模拟定位 / 恢复；确认后重启或关机 |
| Python SDK / MCP | 鉴权 HTTP API、设备选择、控制租约、截图、输入和设备管理；36 个 MCP 工具 |

不支持的手机设置项在读取后直接移除显示。应用安装需要有效签名及设备许可，不能绕过 iOS 安装限制。定位、安装、删除和电源操作不会自动重试。

## 已删除的功能

2026-11-15 实机检查未能建立可用的 CoreDevice 原生视频流：iPhone 12 mini / iOS 18.7.8 返回不支持；iPhone 17 / iOS 27.2 支持信息存在，但重复启动被系统以 9022 拒绝，即使用户结束通话后复测仍未取得画面。

按「不可用且未能修复就删除」的要求，本版移除了原生投屏启停、播放其音频、原生媒体状态，以及依赖它的 HEVC / RTP 录制。界面、共享桥动作清单、HTTP 路由、OpenAPI、Python SDK 和 MCP 均同步清理，不留下无效入口。常规 USB / AirPlay 投屏、截图及现有录像继续使用原有入口。

2026-11-16 按用户要求删除「重放录制文件」及其桌面会话启动路径，交付包不再附重放样本。内部编码流测试仍保留，必要的 RTP/RTCP 代码继续用于现有 HID 认证保活。

## 连接与恢复

文件页的目录下拉框会列出支持文档共享的 App，显示为「App 名称 · 应用文档」。选中后访问该 App 开放的 Documents 目录，可直接导入、另存为和删除文件；开启「映射到资源管理器」后，可在映射盘中复制进出、建立文件夹、重命名和删除。切换目录会先撤销旧映射，需要为新目录重新开启开关。

此入口使用 House Arrest 的 VendDocuments，只开放所选 App 的共享文档；它不是「我的 iPhone」的统一根目录，也不包含未开放共享的 App 私有数据或 iCloud 云盘。映射默认关闭，设备/控制断线或程序退出时清理，Windows WebClient 的传输限制沿用现有系统设置。

打开设备管理、切换设备或重新打开面板时，只会复用当前设备已有的可用通道。需要新建连接时，点击「启用反控并连接」，也可先通过投屏工具栏开启反控。连接列表中断时可从面板右上角的三点菜单重新连接。首次连接仍需要解锁、信任电脑，以及必要的设备绑定、开发者模式和匹配 DDI。连接期间显示静态状态，失败后可点「重试连接」。切换设备或隐藏面板会取消由面板发起的连接；若加入其他入口正在建立的连接，则只取消面板的等待。

开始或停止 USB 投屏可能触发管理通道重连。面板保留当前列表、筛选和选择，暂停设备操作；通道恢复后自动刷新当前页，无需切换设备。就绪判断来自管理桥，独立于普通 USB 枚举状态。面板显示期间每 500 毫秒仅检查内存连接状态；新选中的设备、重新打开的面板或设备恢复可连接状态时才自动尝试连接，不额外扫描。切换设备、切页或隐藏面板会取消旧任务，旧响应和旧菜单不能影响新设备。

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
python -m unittest discover -s tests -p device_file_import_test.py
python -m unittest discover -s tests -p device_file_delete_test.py
python -m unittest discover -s tests -p python_sdk_test.py
python -m unittest discover -s tests -p coredevice_media_test.py
# 构建 Runtime.Tests 后运行：
IPhoneMirror.App.Runtime.Tests.exe --device-hub-sidebar <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --device-hub-file-import <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --device-hub-file-delete <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --device-hub-auto-connect <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --device-hub-placement <截图输出目录>
IPhoneMirror.App.Runtime.Tests.exe --automation-api
IPhoneMirror.App.Runtime.Tests.exe --coredevice-replay <样本.imrtp>
```

实机结果、保留范围及未执行的破坏性测试见 [验收记录](RPLAYHUB_FEATURE_MATRIX.zh-CN.md)。解码器许可证和来源见 [third_party/rplayhub](../third_party/rplayhub/README.md)。
