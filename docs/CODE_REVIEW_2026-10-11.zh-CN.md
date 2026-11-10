# 全仓库代码审查与功能探索（2026-10-11）

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；源码链接改为仓库相对路径，原记录中的行号仅适用于当时版本；历史测试结论未在本次重新执行。

## 修复与复测记录

用户随后要求修复，现已修复下述 5 个确认问题，保留工作区原有修改：

| 问题 | 实现变更 | 验证 |
| --- | --- | --- |
| 托盘 API 不启动 | 自动化启动移至窗口/托盘共用服务入口，缓存启动任务避免重复启动；后台入口等待启动完成 | 隐藏主窗口保持未 Loaded，API 已运行；重复调用不停止服务 |
| 临时后台 API 换密钥后停止 | 保留并恢复实际运行配置，包括临时端口、权限；原本停止的服务保持停止 | 持久化/临时运行均通过；旧密钥返回 401，新密钥可读取设备；受限权限仍返回 403 |
| 绑定保存失败却成功 | 先提交存储，再发布内存快照；所有修改和批量清除传播失败；界面显示四语言保存失败提示 | 文件替换锁下创建/重命名/删除/绑定/解绑/批量清除均不改变内存和磁盘；解除锁后重试可保存 |
| 蓝牙压力工具编译失败 | 改为引用应用项目并声明 friend assembly，避免生产源码链接依赖漂移 | Release 编译，0 警告、0 错误 |
| SRS 非法 URL 导致退出 | 捕获路径解析异常并返回 400，处理请求 Promise 和文件流异常 | 非法编码、NUL、正常 GET/HEAD、404、405、客户端断开均通过，服务持续运行 |

另修复两个测试夹具：键盘合成测试同时模拟前台窗口和 Win32 焦点，并在原生输入集成测试前恢复真实焦点查询；旧 USB 恢复测试遵循 Host 的启动状态，并提供独立绑定快照和输入所有权注册表。未更改产品焦点保护逻辑。

本次复测通过：App.Logic.Tests 全套、完整 Runtime Tests（退出码 0）、`--automation-lifecycle`、`--preview-pointer`、旧 USB recovery、SRS HTTP 测试、本地化检查及主应用/运行时测试/蓝牙工具构建。PowerShell 与 Node 语法、`git diff --check` 通过。新 API 生命周期测试已接入本机构建测试流程，SRS 测试与蓝牙工具构建已接入 Windows CI；远端 CI 未在此次执行。

回归入口：[AutomationLifecycleTests.cs](../src/App.Runtime.Tests/AutomationLifecycleTests.cs)、[App.Logic.Tests/Program.cs](../src/App.Logic.Tests/Program.cs)、[test_srs_lab_server.mjs](../scripts/test_srs_lab_server.mjs)。证据保存在 `work/fix-*-20261011.log`，其中完整运行时为 fix-runtime-full-20261011.log（本地记录：`work/fix-runtime-full-20261011.log`）。

**此前 AMD 退出崩溃未认定已修复。** 本轮完整套件正常退出；对已有 dump 的检查再次确认故障地址位于 AMD 模块、fast-fail 参数为 7，但原始栈扫描不足以确定根因，也没有据此修改原生图形代码。历史失败及实机验收空白仍保留在下文。此次没有执行驱动变更、升级安装或真机输入。

## 初次审查记录（修复前）

审查对象为当前工作区，分支 `main`、基础提交 `829c591`，包含审查开始前已有的未提交修改和新增文件。初次审查未修改产品源码、未执行驱动安装/卸载、未向手机发送实际控制输入；只新增本报告，并在忽略目录 `work/` 下保存审查脚本、复现程序和日志。以下问题描述与验证结果记录修复前状态。

发现 **3 个已复现的产品问题（P2）和 2 个开发工具问题（P3）**。原生、Python、应用逻辑、驱动逻辑及多数专项运行时检查通过；完整运行时套件存在退出崩溃，不能认定全部验证通过。

## 已确认问题

### 1. [P2] 普通托盘启动不会自动开启已保存的 Automation API

- 位置：[MainWindow.Automation.cs:34](../src/App/MainWindow.Automation.cs)、[MainWindow.Tray.cs:19](../src/App/MainWindow.Tray.cs)。
- 触发：保存 `AutomationApi.Enabled=true`，下次以普通托盘模式启动，且不显示主窗口。
- 原因：自动启动 API 仅挂在主窗口 `Loaded` 事件；`StartInTray()` 只创建 HWND、设置托盘和启动设备服务，不加载主窗口。
- 影响：用户已启用 API，但托盘运行时没有监听服务；依赖后台自动化的调用失败。以后加载主窗口才可能触发启动。
- 复现：调用实际主窗口初始化及托盘路径，得到 `savedEnabled=True loaded=False running=False`。
- 建议：将持久化 API 启动放入可见窗口和托盘共用的启动流程，保证只启动一次，并增加“从未显示窗口”的回归测试。

### 2. [P2] 临时后台 API 在重新生成密钥后停止

- 位置：[MainWindow.Automation.cs:108](../src/App/MainWindow.Automation.cs)、[MainWindow.AutomationManagement.cs:10](../src/App/MainWindow.AutomationManagement.cs)。
- 触发：持久化设置中 API 为关闭，通过 `--automation-background` 临时开启，然后重新生成密钥。
- 原因：后台启动使用 `Enabled=true` 的设置副本，并传入 `save:false`；换密钥先停止服务，随后只根据持久化 `Enabled` 决定是否重启。
- 影响：密钥操作成功后原本运行中的后台 API 消失，自动化连接中断。
- 复现：`BACKGROUND_START savedEnabled=False running=True` → `BACKGROUND_ROTATE savedEnabled=False running=False state=AutomationStateStopped`。
- 建议：换密钥前保存服务实际运行状态和生效配置，以其决定重启；覆盖临时后台开启、持久化开启和原本关闭三种状态。

### 3. [P2] 设备绑定落盘失败仍报告成功，重启后丢失修改

- 位置：[DeviceBindingManager.cs:87](../src/App/Services/DeviceBindingManager.cs)、[DeviceBindingManager.cs:257](../src/App/Services/DeviceBindingManager.cs)。
- 触发：绑定文件无法替换，例如被其他进程以不允许删除的方式打开，或目录写入失败。
- 原因：创建档案先修改内存，再调用 `Persist()`；后者吞掉所有异常，只写日志，调用方继续返回成功。其他使用同一持久化方法的修改也存在相同错误传播问题。
- 影响：界面可把未保存的档案/绑定当成已保存；重启后修改丢失，设备关联与用户预期不一致。
- 复现：对独立测试绑定文件 `[]` 持有 `FileShare.Read` 锁，创建档案返回 `success=True`、内存数量为 1，但磁盘仍为 `[]`；重新打开管理器得到 0 个档案。
- 建议：持久化失败应返回错误并撤销内存修改，或先提交存储再更新对外可见状态；界面明确显示保存失败。

### 4. [P3] BluetoothHidStress 工具无法编译

- 位置：[BluetoothHidStress.csproj:11](../tools/BluetoothHidStress/BluetoothHidStress.csproj)。
- 触发：执行 `dotnet build tools/BluetoothHidStress/BluetoothHidStress.csproj -c Release`。
- 原因：项目链接了生产版 `BluetoothHidMouseService.cs`，没有同步其 Localization 和 `DirectKeyboardRoute` 依赖。
- 结果：`CS0234`（Localization）、`CS0246`（DirectKeyboardRoute），共 2 个编译错误。
- 影响：蓝牙压力测试工具不可用；此次未发现它阻塞主程序构建。
- 建议：补齐链接依赖/测试替身，或提取共享组件；把该工具编译加入 CI。

### 5. [P3] SRS 实验面板收到非法 URL 编码会退出

- 位置：[server.mjs:97](../tools/srs-lab/server.mjs)。
- 触发：向实验面板发送 `GET /%ZZ`。
- 原因：异步 HTTP 回调直接调用 `decodeURIComponent`，解码异常未捕获，形成未处理的 Promise rejection。
- 复现：普通请求返回 200；非法请求 `ECONNRESET`；服务进程以代码 1 退出，输出包含 `URIError`。
- 影响：本机实验面板中断。该服务绑定 `127.0.0.1`，此结论不应表述为公开互联网服务漏洞。
- 建议：对 URL 解析和解码错误返回 400；在异步请求入口统一处理异常，并处理文件流错误。

前三项复现日志：audit-probes-20261011.log（本地记录：`work/audit-probes-20261011.log`）。复现程序位于 Program.cs（本地记录：`work/audit-20261011/probes/Program.cs`）；仅使用独立测试状态目录，并抑制设备服务启动。SRS 复现程序为 server-probe.cjs（本地记录：`work/audit-20261011/probes/server-probe.cjs`），使用自建回环子进程并清理；输出见 audit-srs-probe-20261011.log（本地记录：`work/audit-srs-probe-20261011.log`）。

## 功能地图与实现边界

| 功能 | 主要实现入口 | 探索结论与使用边界 |
| --- | --- | --- |
| USB 有线镜像 | `src/Core` 的 Device、Transport、Protocol、Media；`src/App/Interop/NativeCore.cs` | QuickTime USB → CoreMedia/H.264 → Media Foundation NV12 → D3D11/DirectComposition；音频走 WASAPI。需 Apple 设备服务、信任配对和相应 USB 驱动环境。 |
| USB 兼容方案 | QuickTime session/配置切换与恢复、App 投屏设置 | A Demo、B AirPlay 实验、C i4Tools 方案；分辨率/FPS 偏好约束本地显示，不应理解为可直接降低 USB 源流量。 |
| 无线屏幕镜像 | `src/WirelessHost`、`src/UxPlayHost`、`WirelessReceiverService.cs` | 原始接收方案与 UxPlay/GStreamer 两条路径，经版本化、有界 IPC 接入共同渲染/音频链。依赖网络发现、防火墙与运行组件。 |
| 视频应用投放 | `src/WirelessHost/DlnaRenderer.cpp`、App 媒体投放链 | 原始方案支持 HTTP(S)/HLS URL 和 DLNA 投放；UxPlay 支持屏幕镜像，不提供这套 URL/HLS/DLNA 投放能力。 |
| 多设备和独立窗口 | `DeviceSessionManager.cs`、`DeviceControlSession.cs`、`NativePreviewWindow.cs` | 按设备维护会话与输入目标；主窗口/独立窗口存在焦点和所有权管理。不能据此推断每台设备都能同时使用独立录制/推流实例。 |
| USB 直接触控 | `UsbTouchBridgeHost.cs`、`DirectUsbInputBridge*`、`tools/usb_touch_bridge.py`、`tools/iostouch` | 配对/CoreDevice/RSD/DDI 服务验证后发送输入，支持最多 5 个触点。需开发者模式等设备条件；桥接 `ready` 只证明服务就绪，不能代替真机触控验收。 |
| 无线直接控制 | 同一桥接链的无线 tunnel/RemotePairing 路径 | 与 AirPlay 视频传输独立；不能把“无线镜像可用”当成“无线反控可用”。无线路径不会自动回退成 USB 控制。 |
| 蓝牙鼠标/键盘 | `BluetoothHidMouseService.cs`、`BluetoothHidProtocol.cs` | Windows BLE HID 外设路径，需适配器外设能力及 iOS 辅助触控。不能提供与 DDI 相同的绝对多点触控或任意 Unicode 剪贴板传输。 |
| 键盘路由与按键映射 | `KeyboardInputRouter*`、`KeyboardMapping*`、`MainWindow.Keyboard*` | 支持点击、长按、双击、滑动及按住到释放；共享输入所有权、窗口焦点、会话代次和释放清理，防止跨设备滞留输入。 |
| 远程文本和剪贴板 | `MainWindow.RemoteTextInput.cs`、`MainWindow.ClipboardShortcuts.cs`、桥接 pasteboard 端点 | 有独立文本与快捷键入口；能力取决于控制通道。专项覆盖 Unicode、emoji 分段、注入键、重放及编辑器焦点保护；仍有一个原生焦点组合测试待调查，见下文。 |
| 设备身份绑定 | `DeviceBindingManager.cs`、设备身份模型/反控路由 | 关联有线、AirPlay 和蓝牙身份，管理显示名称与持久化档案；保存错误传播存在本次发现的问题。 |
| 录制/推流/麦克风 | `MediaOutputService.cs`、`MediaOutputMicrophone.cs` | FFmpeg 输出 MP4、RTMP、SRT、WHIP；视频/PCM 导出及可选麦克风混音；MP4 暂存后完成封装。输出服务为全局路径，不能当成每设备任意并发输出。 |
| 虚拟摄像头 | `VirtualCameraService.cs`、`src/VirtualCamera` | BGRA 帧交换 → Windows 11 Media Foundation 虚拟摄像头；注册通过提权 helper，实际系统注册和第三方采集未执行。 |
| 本地 Automation API | `AutomationApiHost.cs`、`Services/Automation`、`MainWindow.Automation*` | 默认 `127.0.0.1:17890`；密钥、Host/Origin 检查、权限范围、限流、设备租约及输入仲裁。提供设备管理/输入/截图及后台窗口管理能力；启动和换密钥有两项生命周期问题。 |
| 驱动诊断与管理 | `src/DriverInstaller`、App 驱动检测服务 | 主程序检查环境；独立管理器负责提权、包验证、设备策略及安装/清理。逻辑和模拟安全检查通过，未对当前系统执行驱动变更。 |
| 自动更新和安装包 | `src/App/Updater`、`Apply-ZipUpdate.ps1`、构建/安装脚本 | 发布源/镜像、SHA256 校验、ZIP 路径与回滚、提权边界；未执行发布、真实覆盖升级或完整 Setup 安装。 |
| 界面和后台运行 | MainWindow、SharedUI、本地化资源、Tray/CompactLaunch | 工作区、托盘、轻量启动、独立预览、主题和设备圆角；简中/港繁/台繁/英文四套资源。 |

整体数据流为“采集/无线子进程 → 每设备会话 → 共用渲染与音频 → 预览/输出”，输入则通过“窗口焦点和设备绑定 → 所有权仲裁 → 蓝牙或直接控制桥”独立下发。维护时应特别关注跨进程退出、设备切换、会话代次、取消及释放顺序。

## 审查范围

- 建立源码、配置和构建文件清单：668 个文件、187,994 行；包括测试、脚本、部分第三方头文件与补丁。详情及文件散列见 inventory.json（本地记录：`work/audit-20261011/inventory.json`）。
- 检查项目结构、文档与代码一致性；重点人工跟踪 USB/无线采集、IPC、反控、设备身份、键盘所有权、剪贴板、多设备、媒体输出、虚拟摄像头、API、提权/更新和释放链路。
- 执行构建、语法检查、现有自动测试及针对发现的实际方法复现。
- 这是全仓库清点和核心链路审查，不是对 18.8 万行逐行形式化验证；不包含第三方二进制审计。

## 验证结果

### 已通过

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| C++ 原生 Release 构建 | 成功 | `work/audit-native-build-20261011.log` |
| CTest | 16/16，通过 USB 身份/恢复/传输、协议、无线 IPC、UxPlay 和虚拟摄像头组件检查 | `work/audit-ctest-20261011.log` |
| Python unittest | 400 个测试，OK；使用现有依赖齐全的 venv | `work/audit-python-venv-20261011.log` |
| App.Logic.Tests | 通过 | `work/audit-logic-clean-20261011.log` |
| DriverInstaller.Tests | 通过 | `work/audit-driver-20261011.log` |
| Runtime Tests、Driver UI、UsbTouchDemo 构建 | 0 警告、0 错误 | 对应 `work/audit-*-build-20261011.log` |
| Python AST、XAML/项目 XML | 清单中解析均成功 | `work/audit-20261011/inventory.json` |
| PowerShell 语法 | 76 个 PS1/PSD1 文件，零解析错误 | 只解析，不执行安装/清理动作 |
| Node 语法 | 5 个源文件 `node --check` 通过 | 运行时非法 URL 问题不由语法检查覆盖 |
| 应用/驱动本地化 | App 1569 键×4，驱动 196 键×4；引用与 125 个主题键通过 | `work/audit-localization-20261011.log` |
| Web 本地化 | 6640 个断言通过 | `node scripts/test_web_localization.cjs` |
| 驱动清理安全脚本 | 模拟检查通过 | `scripts/test_driver_cleanup_safety.ps1`，没有系统清理 |
| 清理脚本本地化 | 1456 个用例通过 | `scripts/test_cleanup_localization.ps1`，没有实际清理 |

以下运行时专项均以进程退出码 0 完成：

`--keyboard-focus`、`--keyboard-router`、`--keyboard-ownership`、`--keyboard-mapping-regressions`、`--clipboard-sync`、`--virtual-camera-regression`、`--updater-elevation-regression`、`--localization-audit`、`--media-review`、`--capture-review`、`--logic-review`、`--control-error-presentation`、`--shortcuts`、`--workspace-regression`。

对应日志为 `work/audit-runtime-<专项名称>-20261011.log`。其中本地化运行时覆盖 31,360 个格式组合、5 次切换和 15 条工作流；工作区专项覆盖 100 次转换及中断/模式/主题/语言组合。专项通过证明相应模拟/本机代码路径，不等同于所有手机和驱动环境通过。

### 未通过及待调查项

1. **完整运行时套件退出崩溃，重复两次。** 两次均输出 `App runtime tests passed.`，随后进程退出码为 `-1073740791`（`0xC0000409`）。Windows Application 1000 记录故障模块 `amdxx64.dll`，版本 `32.0.21032.8002`、偏移 `0xdc4bf5`。这表明异常落在 AMD 图形驱动模块，但尚无原生调用栈可判定驱动本身或应用资源生命周期谁是根因。需要抓取 dump，重点检查预览/渲染退出。日志：首次（本地记录：`work/audit-runtime-20261011.log`）、重复（本地记录：`work/audit-runtime-repeat-20261011.log`）。
2. **`--preview-pointer` 的远程文本焦点断言失败。** 报错为 `Remote text leaked from the settings editor into the device.`，位置 [RemoteTextInputTests.cs:104](../src/App.Runtime.Tests/RemoteTextInputTests.cs)。夹具只设置“设置面板可见”，没有建立真实编辑器焦点；[MainWindow.KeyboardFocus.cs:105](../src/App/MainWindow.KeyboardFocus.cs) 在原生预览持有焦点时明确允许预览输入。夹具替换了前台窗口判断，但原生 `GetFocus` 仍参与结果，普通 `--keyboard-focus` 通过。需先用真实设置编辑器焦点稳定复现，当前不能把这一断言直接认定为实际界面文本泄漏。该进程退出也出现同类 AMD 崩溃。日志：preview-pointer（本地记录：`work/audit-runtime-preview-pointer-20261011.log`）。
3. **旧 `tests/UsbRecovery.Tests` 失败。** 前九项桥接检查通过，随后 `host tracks recovery` 失败。夹具创建未启动的 Host 后直接调用私有事件方法；[UsbTouchBridgeHost.cs:186](../src/App/Services/UsbTouchBridgeHost.cs) 明确丢弃未启动状态的事件。应先更新夹具以遵循启动生命周期；当前不足以证明实际恢复功能退化。日志：usb-recovery（本地记录：`work/audit-usb-recovery-20261011.log`）。
4. **BluetoothHidStress 构建失败**及 **SRS 面板非法请求退出**已列为确认问题，不与以上待调查项混淆。

初次使用系统 Python 时缺少 `httpx`；改用现有 venv 后 400 项通过。逻辑测试需要清除 `DOTNET_ROOT_X64` 并直接运行已构建 DLL，避免 `dotnet run` 注入的环境覆盖触发既有提权防护。这些环境问题未列为产品缺陷。

## 复现入口

以下从仓库根目录执行；测试代码与结果均保留在本机工作区。

```powershell
# 三个产品问题：实际 WPF 方法 + 独立测试状态
dotnet run --project work/audit-20261011/probes/AuditProbes.csproj -c Release

# SRS：自建回环端口，复现后清理子进程
node work/audit-20261011/probes/server-probe.cjs

# 蓝牙工具编译错误
dotnet build tools/BluetoothHidStress/BluetoothHidStress.csproj -c Release

# 现有 Python 测试
$env:PATH = (Join-Path $PWD 'third_party/libusb/bin/x64') + ';' + $env:PATH
& work/issues-51-52/venv/Scripts/python.exe -m unittest discover -s tests -p '*test.py'

# 已构建的逻辑与驱动测试
Remove-Item Env:DOTNET_ROOT_X64 -ErrorAction SilentlyContinue
dotnet src/App.Logic.Tests/bin/Release/net10.0-windows10.0.19041.0/IPhoneMirror.App.Logic.Tests.dll
dotnet src/DriverInstaller.Tests/bin/Release/net10.0-windows10.0.19041.0/iPhoneMirror.DriverInstaller.Tests.dll

# 一个已通过的运行时专项；其他参数见上文
& src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe --keyboard-focus
```

## 实机验收空白及处理建议

本次没有完成实体 iPhone/iPad 的 USB/AirPlay 镜像、USB/无线 DDI 触控、BLE 实际输入、真实录制/网络推流、第三方虚拟摄像头采集、驱动安装/卸载和 ZIP/Setup 完整升级验收。相应能力结论来自实现审查和已有自动测试。

建议先修复三项 P2，并为“无主窗口启动”“临时运行设置”“存储提交失败”补回归；其次修复两个工具问题。发布前调查 AMD 退出崩溃及原生焦点测试，并更新旧恢复夹具；最后按设备/iOS/网络/驱动组合完成实机验收。现有自动检查覆盖较广，但当前结果不足以给出“全功能验证通过”的结论。
