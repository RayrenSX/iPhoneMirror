# Metal 性能 HUD 接入与验证记录

2026-11-18。测试版从本地 `main`（`cd7bdd5`）的整合工作树构建。当前已核对 HUD 的候选为 `outputs/iPhoneMirror-main/rotation-test-20261118-performance/iPhoneMirror.exe`（`1.8.5-pre6.rotation3`）。候选目录的 `BUILD-INFO.json` 记录文件哈希与构建范围，`outputs/iPhoneMirror-main/METAL-HUD-VERIFICATION.json` 记录各版本的 HUD 验证结果。

## 已实现

「设备管理 → 应用」的右键及行内「…」菜单新增 **“以 Metal 性能 HUD 重启”**。确认窗口显示应用名和 Bundle ID，说明关闭应用可能丢失未保存内容，默认取消。进程行和空白菜单不提供该操作。普通“打开”、双击和 Enter 沿用原有启动行为，不结束已有进程。

新动作 `restart_with_metal_hud` 仅接受选定应用的 `bundleId`，复用选定设备的现有 RSD 通道，固定传入 `MTL_HUD_ENABLED=1`、`kill_existing=True`、`start_suspended=False`。失败不自动改走普通启动或重试；成功状态只报告已请求重启，提示在设备或投屏画面确认 HUD。切换设备、切页、隐藏面板、断线及过期响应仍受原有取消上下文约束。

该动作为公开 Hub 写操作，通过既有通用接口调用时需要当前控制租约；动作超时为 35 秒。没有增加通用环境变量注入接口，也没有并入上游独立 GUI。实现入口见 [设备服务](../../tools/device_management.py)、[动作契约](../../tools/device_hub_contract.json) 和 [应用列表操作](../../src/App/Controls/DeviceHubPanel.Management.cs)。

## 自动化与构建验证

| 项目 | 结果 |
|---|---|
| Python Hub／设备服务／运行时检查 | 7／12／8 项通过；覆盖固定启动参数、选定设备、非法参数、普通打开、失败清理及打包契约 |
| WPF `--metal-hud-restart` | 通过；覆盖五语言真实确认窗口、取消、目标绑定、普通打开、重复请求、断线、旧菜单／响应及进程隔离 |
| Release 托管构建 | 通过，0 警告、0 错误 |
| 五语言本地化检查 | 通过 |
| 原生 `CoreProtocolTests` | 通过 |
| 冻结桥接器与交付预检 | 已重新构建；运行时功能检查、载荷清单及完整性检查通过 |
| `rotation1` 集成候选 | 17 项 HUD 源码快照一致。App、Core、桥 EXE 和清单校验通过，冻结桥通过 13 项离线检查。候选成功启动，本地 API 返回 HUD 动作为写操作，超时 35 秒 |
| `rotation3` 集成候选 | 核对了 15 项 HUD 实现及测试源码快照，以及交付版主程序、桥接器、桥运行时清单、内置动作契约的哈希。冻结桥通过 13 项离线检查 |
| 独立 iUsbBridge Python `main` | `d70a6d1` 已同步 HUD 实现、契约与专项测试。12 项顶层运行时模块和 6 项 `iostouch` 模块逐字节一致。27 项专项测试及源码运行时自检通过。独立调用文档见本地提交 `7e777b0` |

证据保存在本机忽略目录 `work/metalhud-20261118`，包括构建日志、`ui-tests.log`、五语言确认窗截图及设备截图；不随 Git 仓库分发。专项 UI 测试源码为 [MetalHudRestartTests.cs](../../src/App.Runtime.Tests/MetalHudRestartTests.cs)。

## 实机结果与边界

设备为 iPhone 17／iOS 27.2；目标应用为 Clash Royale（`com.supercell.scroll`）。本次通过新的 CoreDevice 启动动作取得独立 DVT 截图，HUD 可见，证据为 `work/metalhud-20261118/device-coredevice/device-dvt.png`。

| 路径 | 当前结果 | 证据与限制 |
|---|---|---|
| 独立设备截图 | 已看到 HUD | 上述 DVT 截图确认该设备及应用接受固定环境参数；不能据此保证所有应用支持 |
| USB 投屏 | 已看到 HUD | `work/metalhud-20261118/usb-hud.png` 显示约 59.99 FPS、GPU 5.75 ms、帧间隔 16.67 ms；数值为截图时的读数 |
| AirPlay 投屏 | 待实测 | 首次复查的三个无线地址均返回 `ConnectionTerminatedError`。后续已通过既有 RemotePairing 记录验证目标 iPhone，并取得设备端截图。当前 AirPlay 会话用于横竖屏和延迟测试，尚未在其中启动 HUD 并取帧 |
| 新交付程序菜单的实机点击 | 待验证 | 菜单和请求链已通过隔离 WPF 回归；最新交付版启动及公开接口通过，但没有可用设备；Windows 自动化两次绑定均返回窗口归属错误，停止窗口操作 |

USB 证据采自当时运行的安装实例 `C:/Program Files/iPhoneMirror/iPhoneMirror.exe`，该实例同时用于另一项几何调查。它证明新启动动作产生的 HUD 能进入该 USB 采集路径，不能替代新版交付菜单和整个交付包的实机验收。

`work/metalhud-20261118/resumed` 保存了首次复查的设备列表、无线发现结果和候选接口记录。后续无线成功证据见 `work/metalhud-20261118/standalone-sync/wireless-recheck.log`。临时启用 Automation API 的 `rotation1` 测试实例已正常退出，设置已恢复。两个仓库的 Git fetch 分别遇到连接超时和连接重置，独立桥文档提交保存在本地 `main`。

HUD 中的 FPS、帧间隔与 GPU 时间属于目标 App 的 Metal 渲染指标；iPhoneMirror 状态栏的投屏接收帧率是另一层指标。当前未加入持续数据采集、CSV 或性能报表，也未验证 HUD 在录制、OBS 和虚拟摄像头中的表现。横竖屏聊天生成了匹配候选的 ZIP 并完成其文件校验；本聊天未进行该 ZIP 的手机 HUD 验收，也未生成或验收新的 Setup 安装包。
