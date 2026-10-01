# 有线与无线控制链路审查 — 2026-10-01

审查对象：当前工作区的控制入口、Python 桥接器、C# 状态与输入路由、错误提示及相关测试。按照最后确认的范围，只审查有线和无线两条控制链路。

**结论：确认 9 项问题，其中 P1 两项、P2 七项。优先修复传输方式串线和无线恢复状态。** 当前正常路径已有设备身份校验、触控面验证、发送前就绪检查和有界清理，但这些保护不足以保证异常路径始终符合用户选择的连接方式。

本轮进行源码审查、离线故障注入及实际 C# 状态处理验证；没有进行本轮实机连接和输入验证。历史实机记录只作为背景，不能代表当前工作区或其他设备已验证。本轮未修改产品代码，保留原有未提交改动。

## 1. 控制链路

### 有线

`MainViewModel → UsbTouchBridgeHost → DirectUsbInputBridge (--usb) → Apple USB / QuickTime 共存 USBMux → Lockdown → 开发者模式与 DDI → CoreDevice TCP 隧道 → RSD → Universal HID → 就绪校验 → 输入`

桥内优先修复 HID，再重建隧道，保留可用的共存 USBMux；终止后 C# 也有重启桥接器的恢复流程。停止有线投屏时，当前代码先停止相关控制桥，再恢复采集配置。

### 无线

`MainViewModel → UsbTouchBridgeHost → DirectUsbInputBridge (--wireless) → Network USBMux / RemotePairing mDNS → CoreDevice TCP 隧道 → RSD → Universal HID → 就绪校验 → 输入`

两种无线入口的恢复并不对称：Network USBMux 路径进入共享恢复循环；直接 RemotePairing 路径没有接入相同的隧道读取任务监测。RemotePairing 缺少 DDI 时会要求 USB 准备镜像，这类前置准备与实际输入传输应分别说明。

## 2. 已确认问题

### F1 · P1 · 无线恢复硬编码为 USB，仍上报无线就绪

- 位置：`tools/usb_touch_bridge.py:1520`，调用方 `:2164`。
- 条件：无线通过 Network USBMux 成功启动，随后 HID 或隧道异常，进入 `_connect_with_lockdown()` 的恢复循环。
- 问题：共享的 `_reconnect_lockdown()` 固定调用 `_create_lockdown_with_retry('USB')`。未插线时，桥内恢复会错误消耗 USB 重试预算，再等待外层重启；插线时可能直接用 USB 恢复，但 `_emit_ready()` 仍按 `self.transport_mode` 上报 `wireless`。
- 证据：真实恢复循环的离线注入记录为 `Lockdown=[Network, USB]`，两次 `ready.transport` 均为 `wireless`。
- 建议：按会话传输方式选择恢复入口；无线恢复只能使用 Network 或 RemotePairing，禁止接入 USB 共存恢复。就绪事件应反映实际建立的输入通道。

### F2 · P1 · 有线服务不可用时转入无线发现，仍上报 USB

- 位置：`tools/usb_touch_bridge.py:2206–2212`。
- 条件：选择有线控制，`CoreDeviceTunnelProxy.create()` 返回 `InvalidServiceError`，同一设备已有 RemotePairing 配对且局域网可达。
- 问题：该分支未检查传输模式，直接调用通过 mDNS 发现设备的 `_connect_via_remote_pairing()`。无线连接成功后仍上报 `transport=usb`，C# 只比对这个上报字段，因此无法发现实际通道已改变。
- 证据：对有线 `_run_tunnel_attempt()` 注入服务不存在错误，RemotePairing 被调用一次；模拟其建立成功后，实际 `_emit_ready()` 输出 `usb`。
- 建议：有线模式保留有线边界，报告服务/兼容性错误；若产品需要允许改用无线，应由单独的明确切换流程完成。
- 测试缺口：现有 `test_missing_coredevice_proxy_falls_back_to_remote_pairing` 正在断言这个回退行为，不能将该测试通过作为有线隔离正确的证据。

### F3 · P2 · RemotePairing 无线空闲断线未触发恢复

- 位置：`tools/usb_touch_bridge.py:1828–1830`、`:2711–2716`。
- 条件：通过直接 RemotePairing 启动无线控制，随后底层 socket 读取任务结束，用户尚未发送下一次输入。
- 问题：此入口直接等待 `_connect_with_tunnel_result()`，没有使用 `_run_observed_tunnel()`；无线也不启动 HID 健康检查。输入循环继续等待 stdin，剪贴板轮询失败只记调试日志。底层 socket 已结束时，桥仍可维持原来的就绪状态，不能及时启动自动恢复。
- 证据：模拟 socket reader EOF，运行真实 RemotePairing 外层和输入循环，得到 `session_ready=true, recovering=false, monitor_calls=0`。RSD 建立和设备发现使用替身，不涉及真实网络。
- 建议：让两种无线入口都监测实际隧道读取任务；EOF、读取失败和输入失败统一关闭输入闸门，并进入有界无线恢复。

### F4 · P2 · 无线桥已在恢复，界面与路由仍保持 Ready

- 位置：`src/App/ViewModels/MainViewModel.cs:2258–2260`。
- 条件：无线桥发出 `status/recovery_triggered`，之后桥内恢复并再次发出 `ready`。
- 问题：无线事件处理器过滤掉除 `terminated` 之外的状态，也不处理恢复后的 `ready`。底层 `DirectUsbInputBridge` 和 `UsbTouchBridgeHost` 已进入恢复，但 `_wirelessControlConnected`、UI、输入路由未同步暂停；恢复后也不重新初始化路由状态。
- C# 实测：`bridgeReady=false`、`hostState=Recovering`，同时 `viewModelConnected=true`、`inputEnabled=true`、`uiStage=Ready`、`routerMode=Wireless`；再次 ready 后，测试预置的路由按键状态仍有 1 个。
- 影响边界：底层 `IsReady` 检查仍会阻止恢复期间实际发送，因此这里不是“底层仍在发送”；问题是上层错误地继续接收/维护输入状态，并显示已就绪。
- 建议：对应有线事件处理流程，接收恢复事件后清除 connected 和输入状态；新一代 ready 后重新核验身份并恢复路由。

### F5 · P2 · 缺少当前控制协议的旧系统前置检查

- 位置：`tools/usb_touch_bridge.py:1882–1923`。
- 条件：连接 iOS 15 或 iOS 16 设备。
- 问题：预检没有检查已可读取的 `product_version`，直接查询开发者模式并统一使用 `PersonalizedImageMounter`。iOS 15 的开发者模式字段不存在会进入查询失败；iOS 16 即使已开启开发者模式，也会继续尝试 Personalized DDI。当前桥没有旧版 Developer DDI 的控制实现。
- 证据：模拟 iOS 15 的 `MissingValueError` 得到 `developer_mode_check_failed`；模拟 iOS 16.7、开发者模式为 true 时，仍调用 Personalized DDI 挂载。随附 SDK 的 `auto_mount()` 明确将 `<17.0` 分配到旧版 Developer DDI 分支。
- 建议：为当前实现不支持的系统提供明确且本地化的前置错误；对于进入 CoreDevice 范围的版本，再检查实际服务和触控面。通过最低版本检查不等于承诺该大版本全部支持。

### F6 · P2 · 锁屏或连接异常被说成未开启开发者模式

- 位置：`tools/usb_touch_bridge.py:1891–1907`；`src/App/ViewModels/MainViewModel.cs:2674–2676`。
- 条件：开发者模式查询抛出 `PasswordRequiredError`、连接重置或其他查询异常。
- 问题：Python 将这些异常统一转换为 `developer_mode_check_failed`；C# 又将其与确实关闭开发者模式的 `developer_mode_required` 合并。用户看到“此设备未开启开发者模式……开启并重启”，但实际上可能只需解锁或恢复连接。
- 证据：锁屏、连接重置均复现 `developer_mode_check_failed`；实际 C# 提示映射与“开发者模式关闭”完全相同。
- 建议：保留已知锁屏/信任/连接异常分类；对未知查询失败使用“无法确认状态”，只有返回 false 或明确的设备错误才提示开启开发者模式。

### F7 · P2 · GitHub DDI 挂载失败丢失可恢复原因

- 位置：`tools/usb_touch_bridge.py:2011–2041`；`src/App/ViewModels/MainViewModel.cs:2698–2700`。
- 条件：本地无可用 DDI，GitHub 镜像已取得，但挂载时开发者模式被关闭、操作超时或 Apple TSS 网络失败。
- 问题：GitHub 分支将这些异常统一包装成 `developer_image_mount_failed`；本地分支单独处理的 `DeveloperModeIsNotEnabledError` 在此处也被吞并。C# 最终提示“本地开发者镜像无效……包含三个文件”，偏离实际失败原因。界面虽有 TSS 专用提示，当前桥源码却没有产生 `developer_image_tss_failed` 的路径。
- 证据：上述三类异常全部复现为 `developer_image_mount_failed`；C# 实际映射为本地镜像错误提示。
- 建议：各镜像来源复用相同的异常分类，保留模式关闭、锁屏、TSS、超时及文件/兼容性错误；只对内容确实无效的情况提示检查镜像文件。

### F8 · P2 · 显式 HID 兼容分支中的 9021 仍错误终止

- 位置：`tools/usb_touch_bridge.py:2609–2614`，调用方 `:2538–2539`。
- 条件：初始 `touch_session` 触发服务启动的兼容分支，显式 `_init_touch()` 已成功验证 HID，随后 `_open_gate()` 收到媒体流 9021。
- 问题：此处分支无条件抛出 `remote_control_unsupported_ios`，并声称没有可用的直接触控面；它没有保留已经初始化的 HID。异常发生在上层 catch 分支内部，不会再进入同一个 catch 的 `_enable_direct_hid_fallback()`。
- 证据：运行 `_initialize_touch_with_retry()`，模拟 HID 初始化成功、媒体流返回 9021，得到 `hid_exists=true`、`gate_open=false`，但抛出“不支持”错误。
- 建议：统一两处 9021 的处理，在已有 HID 的情况下继续做发送器验证，验证成功后再以 direct 模式就绪；没有 HID 时维持失败。
- 范围：最初 `touch_session` 直接返回 9021 的常见分支，现有代码已有 direct 回退；本问题限定为后续显式 HID 初始化分支。

### F9 · P2 · Apple USB 服务不可用会阻断独立无线发现

- 位置：`tools/usb_touch_bridge.py:1663–1672`。
- 条件：设备已完成 RemotePairing 配对、网络可达，但本机 Apple USB 服务停止或无法连接。
- 问题：无线启动先访问 Network USBMux，只在 `DeviceNotFoundError` 时尝试 RemotePairing；`ConnectionFailedToUsbmuxdError` 直接终止，提示安装/修复 Apple 支持，未尝试已经可用的无线配对路径。
- 证据：注入该错误得到 `apple_usbmux_unavailable`，RemotePairing 调用次数为 0。
- 建议：无线发现将 USBMux 不可用作为尝试 RemotePairing 的原因之一；如果无线配对/发现也失败，再给出与实际路径匹配的最终错误。

## 3. 异常场景矩阵

“已有处理”表示当前源码有明确处理路径，部分有离线测试；不代表本轮完成对应实机验证。

| 场景 | 当前行为与结论 |
|---|---|
| 开发者模式明确关闭 | 已有 `developer_mode_required`，阻止启动并提供开启/重启提示 |
| 开发者模式查询失败 | 存在 F6，不能据查询失败断定模式未开启 |
| iOS 15 / 16 | 存在 F5；需明确当前协议不支持，避免无效开启模式/下载镜像操作 |
| iOS 17 及以上、未来版本 | 以实际 CoreDevice、HID、257 与发送器验证为准；本轮无版本级兼容承诺 |
| 尚未信任 | `NotPairedError` / 开发者查询 `GetProhibitedError` 已映射信任提示；Lockdown 使用 `autopair=False` |
| 锁屏 / 密码待输入 | 顶层 `PasswordRequiredError` 有专用提示，但查询开发者模式阶段存在 F6 |
| 未插线、设备消失 | 有线有 Apple USB 与共存 USBMux 发现/重试；失败可报告未找到控制通道 |
| Apple 驱动/服务不可用 | 有线有专用提示；无线存在 F9 |
| USB 正在投屏、共存通道暂未就绪 | 有界重试与回退，现有测试覆盖 VERSION 失败和不接管其他设备 |
| CoreDeviceProxy 服务不存在 | 有线存在 F2，实际传输可能改变 |
| 无 RemotePairing 记录 | 有专用配对提示，指引先完成 USB 初始化 |
| 不同局域网 / mDNS 被拦截 | RemotePairing 发现有时间限制和同网/防火墙提示；未逐种实测网络拓扑 |
| DDI 尚未挂载 | 有本地候选及 GitHub 获取、挂载和复查流程 |
| GitHub 断网 / 超时 / 限流 / 校验错误 | 下载层有分类及对应提示；本轮不是对外网服务可用性的验证 |
| 已下载但 Apple TSS/挂载异常 | 存在 F7 |
| 旧 DDI / RSD 服务表为空 / 无 257 | 有镜像刷新与服务等待重试；无 257 不允许 ready |
| 媒体流 9021 | 首次认证失败有 direct HID 回退；显式兼容分支存在 F8 |
| 就绪事件目标 UDID 不符 | C# 拒绝 ready；传输字段也比对，但 F1/F2 说明字段可能只是请求方式 |
| 有线 HID 或隧道断开 | 有桥内恢复、发送器验证、generation 隔离及上层恢复事件处理 |
| Network USBMux 无线断开 | 存在 F1、F4 |
| RemotePairing 无线空闲断开 | 存在 F3；新输入可能暴露发送错误，但不能替代主动检测 |
| 桥接器意外退出 / stdout 关闭 | C# 有终止检测和恢复处理；仍受上述传输选择问题影响 |
| 取消、关闭、退出 | 有取消及有界清理；停止桥先关闭 stdin，再等待退出，必要时终止进程 |
| 恢复时旧输入仍在队列中 | 底层有 generation 检查并释放触点；无线 UI/路由状态仍存在 F4 |
| 多设备身份 / 绑定改变 | 有 UDID 比对、恢复前绑定检查；本轮未做多机同时在线的实机交互 |

## 4. 本轮验证

| 验证 | 结果 |
|---|---|
| `python -m unittest discover -s tests -p 'usb_touch_logic_test.py'` | 90 项通过，23.941 秒 |
| `python -m unittest discover -s tests -p 'usb_recovery_test.py'` | 39 项通过，3.129 秒 |
| `dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release` | 通过 |
| 专项 Python 故障探针 | 12 种注入场景，复现上述分支行为 |
| 专项 C# 状态探针 | 编译当前 App，运行实际桥事件处理、Dispatcher 回调与提示映射，复现 F4/F6/F7 |

Python 使用仓库 `work/usb-touch-bridge-python/Scripts/python.exe`，运行前将 `third_party/libusb/bin/x64` 加入进程 PATH。

本地证据保留在被 Git 忽略的 `work/control-link-audit/`：`probe.py`、`python-results.json`、`ControlLinkAudit.csproj`、`Program.cs`、`dotnet-results.json`。探针不连接设备或网络；C# 探针跳过 MainViewModel 的原生初始化，只运行被审查的真实事件处理逻辑。其结果证明相应代码分支与状态问题，不证明任意实机已发生相同事件。

现有测试通过与本次发现并不矛盾：多数测试覆盖单层逻辑，有线到无线回退甚至被既有测试明确允许。应增加“请求传输、实际建立传输、恢复传输、界面状态”贯穿整个会话的断言。

## 5. 建议修复顺序及验收

1. **先修复 F1/F2**：严格保持有线/无线传输边界。分别在插线和不插线时注入无线断线；有线服务不可用时确认没有无线输入连接。
2. **修复 F3/F4**：统一无线断线检测和恢复事件处理。覆盖空闲 EOF、拖拽中断、按住修饰键断开、恢复后首次输入和取消恢复。
3. **修复 F5/F6/F7/F8/F9**：建立稳定的版本/能力及错误分类，验证每个错误码映射到正确操作建议，避免把锁屏、网络和兼容性混为一谈。
4. **补充实机验收**：至少覆盖旧系统、不提供 257 的设备、已验证的 direct HID 设备；检查双线同时存在、移除数据线、Wi-Fi 切换、防火墙阻断和多设备绑定。记录设备系统、DDI 构建及桥接器版本，不能只记录“iOS 大版本”。

完成前两组修复和相应验收前，不建议把两条链路标记为异常恢复审查通过。
