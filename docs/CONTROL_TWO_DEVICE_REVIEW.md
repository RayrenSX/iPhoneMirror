# 双设备干净环境与故障回退审查

## 2026-10-09 当前结果：自动重连续测

两台手机均恢复正常 USB 管理通道。新增的单端口自动重连在 iOS 26 完整关窗路径实测通过；iOS 18 关窗正常恢复，双设备隔离中持续 64 次应答、会话未重建。当前验证包为 `artifacts/iPhoneMirror-usb-reconnect-reviewed/`。证据与权限限制见 [USB_AUTO_RECONNECT.md](USB_AUTO_RECONNECT.md)。下方之前的“当前/最新”状态为历史记录，不代替此结果；原完整全链路目标仍未全部完成。

测试日期：2026-10-08 至 2026-10-09。设备为 iPhone18,3 / iOS 26.6.2（23G90）和 iPhone13,1 / iOS 18.7.8（22H352）。不在报告中记录设备序列号。用户于 2026-10-09 续查时重新授权 iOS 26 测试，当前两台均可测试；下方收回设备的描述属于历史阶段。

当前验证包为 `artifacts/iPhoneMirror-usb-inventory-reviewed/`，对应宿主 `work/two-device-review/usb-inventory-runtime-host/`。iOS 18 已信任但 Apple USB 服务仍漏列的现场继续保留；iOS 26 当前实际 USB 配置 3，Apple 服务列出该设备。授权记录 `usb-inventory-device-reauthorization.json`，最新软件修复和包哈希见文末。跨设备对照不能代替 iOS 18 正常状态的同设备对照，也不能证明历史恢复故障根因。

续测当前状态已改变：iOS 26 正向有线/无线及无线恢复通过，但真实关窗和随后只投屏对照均出现信任弹窗及 Apple USB 漏列。详细证据见[当前状态报告](CONTROL_REVIEW_STATUS.md)。只投屏没有启动反向控制，排除了 DDI/HID/共存 mux 为这次故障的必要触发条件。故障仍未修复完成。

**46 项设备故障矩阵通过，但全链路目标尚未完成。** 本轮确认并修复双设备 DDI 拒绝记录覆盖；另观察到 Apple USB 服务暂时漏列其中一台手机，需要手动拔插恢复，根因仍未确认。不能把 DDI 回退测试通过解释为所有用户环境均无错误。

最新 mDNS 软件及指定 iOS 18 实测续查见文末。用户收回 iOS 26 后的新代码尚未在该设备复测；当前指定 iOS 18 的无线正向、主程序恢复及取消通过，但物理网络中断、完整发现及其他未闭环项目不能以这些结果代替。

当前最终验证包为 `artifacts/iPhoneMirror-mdns-resolution-reviewed/`（文末哈希/验证），全量 Python 388 项与指定 iOS 18 的有线/无线启停、主程序无线恢复/取消通过。下文旧包及“最新”描述为对应历史阶段，不代替当前包。

## 清理范围与证据边界

- 将原全局 DDI 缓存移入 `work/two-device-review/quarantine/initial-global-cache`，隔离清单见 `isolation.json`。每项挂载测试前查询并卸载该手机上的 Personalized DDI，保存 `*-unmount.log`；信任、配对、用户文件、驱动和网络配置保留。
- 没有 DDI 环境变量覆盖，使用的应用包不携带默认镜像；当前桥接器只使用显式路径及验证后的缓存。设备可能保留签名票据；`tss-retry` 特别绕过该票据，验证第二次真实 Apple 签名请求。
- 故障仅发生在测试子进程或独立缓存目录。损坏文件实际修改磁盘副本；不可达来源、磁盘满、权限拒绝、锁屏、未信任、开发者模式关闭和阶段超时是受控注入，不能称为物理断网、实际耗尽磁盘或修改手机安全状态。
- 正向通过要求指定设备身份匹配、真实 HID 应答、无终止错误和正常退出。负向通过要求无 ready 且错误码匹配。取消要求确已进入下载、未 ready、正常及时退出。不将桥接进程退出码 0 当作控制成功。
- 19:00 左右的两项测试因会话中断未完成，保留原始日志，不计通过。续测使用 `resumed-` 独立目录；两个未完成下载目录移到 `quarantine/interrupted-staging`。这是测试进程被中断后的残留，区别于正常取消清理。

## 故障矩阵

独立复核脚本 `work/two-device-review/audit_results.py` 检查摘要及原始事件，输出 `matrix-audited.json`：两台各 23 项，共 **46 项**；20 项恢复控制，共 **80 次真实 HID ACK**。以下每行两台均通过。

| 场景 | 结果 |
| --- | --- |
| 有效缓存、全部下载来源不可达 | 使用缓存挂载并控制，无下载 |
| 本地镜像不含该设备 BuildIdentity | 切换已验证镜像，真实签名及挂载后控制 |
| 无缓存、全部下载来源不可达 | `developer_image_download_failed` |
| 缓存父路径被普通文件占用 | `developer_image_cache_failed` |
| Apple 签名第一次 503 | 有限重试，第二次真实请求成功并控制 |
| Apple 签名超时 | `developer_image_tss_timeout` |
| Apple 拒绝签名、无新候选 | `developer_image_tss_rejected`，不循环重试同一镜像 |
| 上传阶段超时 | `developer_image_upload_timeout` |
| 激活挂载阶段超时 | `developer_image_mount_timeout` |
| 锁屏异常 | `apple_device_locked` |
| 未信任异常 | `apple_device_not_trusted` |
| 开发者模式关闭异常 | `developer_mode_required` |
| 下载中取消 | 无 ready、无强制结束，正常清理 |
| GitHub 直连及代理来源不可用 | 从真实镜像站下载、校验、挂载并控制 |
| Image.dmg 内容损坏 | 拒绝缓存，重新下载、校验、挂载并控制 |
| BuildManifest.plist 内容损坏 | 同上 |
| Image.trustcache 内容损坏 | 同上 |
| 缓存校验元数据损坏 | 同上 |
| 缺少 Image.trustcache | 同上 |
| 缓存当前版本指针损坏 | 同上 |
| 写入返回磁盘满 | `developer_image_cache_failed` |
| 写入返回权限拒绝 | `developer_image_cache_failed` |
| RSD 身份不是选定手机 | `device_identity_mismatch`，不发送控制输入 |

实际耗时见 JSON。网络较慢时有恢复测试接近 150 秒总运行时间；该时间包含启动、下载、挂载和 4 秒输入验证，不等同于下载阶段超时。没有将接近期限的情况记作快速恢复。

最初的正式包冷启动：iOS 18 无缓存下载经历 GitHub API 大文件超时后，从镜像站恢复，68.85 秒 ready、8 次 ACK；iOS 26 经手动恢复设备发现后，以重新生成的缓存挂载，8 次 ACK。iOS 26 真正无缓存且仅镜像可用的路径另由 `d1-mirror-only` 覆盖。

## 本轮修复与软件回归

共享缓存原先只有 `.iphoneMirror-ddi-rejected.json`。虽然读取时检查设备/系统范围，但第二台写入会覆盖第一台仍在有效期内的记录，导致第一台再次尝试已被拒绝的内容。

现在每个设备及系统版本范围各自原子写入独立记录，名称使用散列；旧版共享记录仍可按原范围和期限读取，新记录优先。不同设备不互相封锁，也不相互抹掉失败记录。

- `rejection-before.log`：新增双设备回归在修改前失败。
- `python-after-rejection.log`：当前源码全量 **335 项通过**，包括双设备记录保留、旧记录迁移读取、过期和新记录优先。
- 新增 `tests/ddi_http_fallback_test.py` 通过真实本机 HTTP socket 测试登录 HTML、截断响应、限流和正确来源切换；校验下载内容及不发布坏缓存，3 项通过。仅本机测试绕过 HTTPS 来源规则，生产逻辑未放宽。
- 早期 321 项测试中一项在并发负载下超出测试自身 0.3 秒期限；原失败保留在 `python-tests.log`。恢复测试单独 42 项及后续全量测试通过；没有放宽生产超时来掩盖失败。

## 双设备共享缓存

`shared-results.json`：两台同时从同一个空缓存启动，禁用直连及代理，真实镜像下载与各自挂载成功，分别 55.12 / 46.01 秒 ready，各 8 次 ACK。校验同一缓存最终完整，测试结束没有暂存目录残留，身份未串用。

新桥接器的双设备无线宿主复测均已在用户收回设备前正常完成：各启动/停止两次，各 6 次真实 ACK。iOS 26 两次 ready 约 22.87 / 22.43 秒；iOS 18 约 23.06 / 22.44 秒。见 `new-host-wireless-ready-d1/d2.log`。此前发现不到服务的失败仍保留，不被后续成功抹去。

用户随后明确收回 iOS 26 设备，后续不得再探测、卸载或控制它。收到指示后检查无存活测试/桥接进程；测试入口增加设备禁用标记。双设备拒绝记录修复的真机专项复测尚未执行，现只有软件回归证据；`rejection_live.py` 不得运行，双设备完整主程序复测保持未完成。后续仅使用仍获授权的 iOS 18 设备。

## 已发现但尚未闭环的设备发现问题

18:49 iOS 26 设备的 PnP 节点正常且底层 USB 可枚举，但 Apple 服务 ListDevices 未列出该设备；另一台仍可正常使用。用户确认没有插拔且已解锁亮屏。程序在 DDI 下载前报告 `apple_device_not_found`，不是 DDI 版本不兼容。

保存现场后尝试重启 Apple 服务被当前权限拒绝，服务未修改。用户手动重新插拔后恢复。证据为 `cold-device1.json`、`cold-device1.log`、`device1-missing-pnp.json`、`restored-device1.json`。未确认根因或自动恢复能力，不自动重启服务以影响另一台正在工作的手机。

## 验证包与剩余工作

最新验证包：`artifacts/iPhoneMirror-wireless-reviewed/`，完整目录一起使用。桥接器 SHA-256：`9FCA570E395823E56676DCFBDF10CADCEFAD153EB34EE72156C157CE20BEC1B9`，包含下述全部桥接器清理及无线修复。主程序现在使用本轮当前源码构建，包含主窗口缺失时保留原始错误的修复；保留验证过的原生核心和 USB 恢复助手。`artifacts/iPhoneMirror-two-device-reviewed/` 的 `9A18...` 是上一阶段包，仍保留，不能代替最终包。

`wireless-build-identity.json` 确认桥接器、DDI 支持、运行时自检源码与隔离构建源码一致；打包自检、836 个文件的清单校验、应用运行时完整性及 72 项错误提示路由通过（`wireless-artifact-integrity.json`、`wireless-ddi-routing.log`）。宿主与新包的应用 DLL、Core、恢复助手和桥接器一致（`wireless-host-identity.json`）。当前应用构建也包含工作区已有其他改动；本报告只证明控制专项验证范围，不能声称所有其他功能改动均验收。较早构建及日志保留为历史证据。

剩余范围仍包括历史 Apple 服务漏列/投屏退出恢复的因果定位、当前双设备主程序全链路、物理 Wi-Fi 中断/漫游、长时间控制及手机 UI 手势验证。HID ACK 只能证明控制传输，不能代替可观察的触点、长按释放等 UI 验证。

## 仅 iOS 18 续查：DDI 服务关闭与无线恢复

发现并修复 DDI 清理的连接句柄泄漏：固定版本 SDK 的 `ServiceConnection.close()` 在 `finally` 清空 writer/socket，即使取消打断了 `wait_closed()`，尚未走到实际 socket.close。原有三秒清理期限能防止挂起，却可能遗留底层连接。`bounded_mounter` 现在保留该 mounter 自己的连接句柄，关闭失败、超时或取消时中止 writer 并关闭 socket；不关闭父 Lockdown 连接，不吞掉取消，不覆盖原始挂载错误。

- `mounter-handles-before.log`：使用真实 SDK `ServiceConnection` 和本机 socket 的两个回归均失败，证实句柄仍打开。修复后同项通过，`python-mounter-final.log` 全量 **337 项通过**。
- `cleanup-d2-mounter-close-timeout.log`：在真实手机服务关闭时注入永不完成的 `wait_closed`，实际触发三次清理超时；仍完成挂载、4 次 HID ACK、正常退出。后续单独查询确认 DDI 挂载可用，未执行拔插、重启服务或驱动。
- `explicit-corrupt-live.log`：另对显式提供的本地 Image.dmg、Image.trustcache 实际写入坏内容，两次均由手机拒绝挂载后切换验证镜像，分别 17.20 / 14.56 秒完成控制，各 4 次 ACK。不能把此项与自动缓存的哈希拒绝混为同一个测试。
- 新增 `--wireless-recovery-selected-live <UDID>` 测试模式，使用先前独立 USB 验证及已有绑定的 iOS 18 身份，关闭主窗口后台设备发现，避免探测已收回的设备。它覆盖生产控制、恢复与取消，**不覆盖生产设备发现**。
- `cleanup-wireless-recovery.log`：新桥接器被测试宿主终止后，当前生产主程序约 24.22 秒重建真实无线控制，前后 6 次 HID ACK；再次中断后取消约 0.07 秒完成，泵送 UI 调度器七秒后没有延迟重连。宿主退出码 0，结束无存活测试或桥接进程。该项是子进程中断，不能称为物理 Wi-Fi 断开。
- `runtime-build-final.log`：当前源码的测试宿主构建 0 警告、0 错误；运行前显式换入验证过的 Core/USB 恢复助手和新桥接器。`cleanup-ddi-routing.log` 的 72 项错误路由/运行时完整性检查通过，`cleanup-host-lifecycle.log` 的启停、取消、旧进程退出隔离通过。
- `cleanup-build-identity.json` 核对桥接器、DDI 支持和运行时检查源码与实际隔离构建一致；打包自检通过。主程序控制专项使用的是当前源码编译宿主，验证包的主程序仍继承此前版本，两者不可混称为同一完整应用版本。

## 仅 iOS 18 续查：服务打开失败与 HID 来源回退

继续检查连接的创建与交接，确认并修复两个遗漏：

1. SDK 的 `start_lockdown_service()` 在打开服务端口后才进行 TLS 握手；握手失败或取消时，尚未返回的连接没有清理所有者。桥接器现在为原有 binary/plist 两种 Lockdown 客户端补上交接前清理，保留端口、BUID、escrow bag 和 TLS 协议；关闭有期限，保存句柄确保 SDK 清空属性后仍能释放 socket，原始错误及取消保持可辨认。
2. HID/显示服务关闭原有期限，但 RemoteXPC 的 `wait_closed()` 超时或取消时没有中止连接。来源回退可在同一隧道上继续尝试，因此不能依赖最后关闭隧道补救。现在仅中止该候选自己拥有的 writer，不关闭父 RSD；媒体认证失败后的 display 清理使用相同边界。

- `service-open-before.log`：真实 SDK 工厂和本机 socket 验证 TLS 失败/取消均遗留未交接连接；修复后 `service-open-final.log` 的 10 项检查通过，包含无 TLS 正常交接、清理超时保持原始失败。
- `remote-cleanup-before.log`：真实 SDK RemoteXPC 配合可控关闭行为与本机 socket，候选关闭超时、取消及媒体认证失败三项均失败；`remote-cleanup-after.log` 同项通过。此项模拟等待对端关闭的 transport，不冒称物理网络故障。
- `python-service-remote-final.log`：当前源码全量 **344 项通过**。
- `service-source-d2-unmount.log` / `service-source-d2.json`：仅卸载指定 iOS 18 设备的 DDI，再由当前源码真实挂载，5.92 秒 ready、4 次真实 ACK、0 终止错误，正常停止。
- `remote-close-d2.log/.json`：真实 HID 服务完成 inventory 后注入第一次候选拒绝和关闭等待超时；日志确认实际 transport abort，后续来源重试成功，5.78 秒 ready、4 次真实 ACK、0 终止错误，停止 0.32 秒。只对测试子进程和缓存副本注入，未改手机信任、网络或安全设置。

以上续测未使用 iOS 26。最新桥接器构建、自检和源码哈希比对通过，完整桥接器运行时已换入验证包；原版本保留在 `work/two-device-review/previous-cleanup-runtime`，未覆盖应用的 ffmpeg/updater 组件。当前源码测试宿主构建 0 警告、0 错误（`service-runtime-build.log`），运行前替换验证过的 Core/USB 恢复助手，宿主与验证包的三项二进制哈希一致（`service-host-identity.json`）。

- `service-ddi-routing.log` / `service-host-lifecycle.log`：72 项错误路由、应用运行时完整性、进程启停/取消/旧退出隔离通过。
- `service-packaged-usb.log`：最新发布桥接器实际有线启动/停止两轮通过，5.24 / 4.81 秒 ready、共 6 次真实 ACK，停止 0.35 / 0.33 秒。
- `service-packaged-wireless.log`：同版桥接器实际无线启动/停止两轮通过，22.68 / 23.12 秒 ready、共 6 次真实 ACK，停止 0.39 / 0.33 秒。
- `service-wireless-recovery.log`：当前主程序生产恢复链路在自己的桥接子进程被终止后，24.08 秒重建指定 iOS 18 的无线连接，前后 6 次真实 ACK；再次中断后取消 0.08 秒完成，观察七秒没有延迟重连。该项仍采用指定身份、禁用主窗口后台发现；子进程中断不能代替物理 Wi-Fi 中断测试。

主程序专项使用当前源码编译宿主，验证包主程序仍继承此前版本。新增清理修复在 iOS 26 上尚未复测；不以前期双设备矩阵代替本次新代码的双设备真机验收。

## 仅 iOS 18 续查：退出过程中的取消与锁等待

再次检查正常关闭和多资源收尾，确认并修复三处问题：

1. 先前仅保护未交接的 Lockdown 服务连接，正常接管后的客户端关闭仍直接使用 SDK。现在 USB binary/plist 客户端的正常 `close()` 也保留 writer/socket，限时关闭并确保取消/超时后释放真实句柄。
2. 若取消恰好发生在 HID 清理中，原有 `finally` 中的顺序代码会立即退出，跳过媒体接收器、display、RSD 和 dial plane。新增清理取消范围：当前步骤收到取消后中止，继续其他各自有期限的清理，最后重新抛出取消；不将关闭任务 shield 后遗留在后台，不吞掉用户取消。媒体停止自己的超时仍保留为 TimeoutError。
3. 会话退出原先无限等待剪贴板锁。现在锁等待有期限，取消或超时后仍分离、关闭本会话的剪贴板连接，再清理后续资源。

- `adopted-lockdown-before.log`：真实 SDK 与本机 socket 的正常客户端关闭超时/取消两项回归失败，修复后 `adopted-lockdown-after.log` 的 12 项通过。
- `cleanup-cancel-before.log`：取消发生在 HID 关闭中时，媒体及会话两项清理回归均失败；修复后通过。`cleanup-lock-before.log` 另记录剪贴板锁等待超时和取消的两个失败子场景，`cleanup-lock-after.log` 的 6 项通过。
- `cleanup-cancel-source-retry.log`：来源重试/媒体停止原错误与期限回归 47 项通过；`python-cancellation-final.log`：全量 **349 项通过**。
- `continued-d2-lockdown-close-timeout.json/.log`：iOS 18 正常控制后，真实根 Lockdown 的 `wait_closed` 被注入挂起，日志确认底层 socket 已关闭；4 次实际 ACK、正常退出，停止约 0.45 秒。
- `continued-d2-cleanup-cancel.json/.log`：真实 HID 连接关闭期间注入第二次取消；确认实际 transport abort、RSD writer 释放、dial plane 监听关闭及会话引用清空。控制前 4 次实际 ACK，退出正常且未强杀，停止约 0.40 秒。这是进程内取消注入，不是物理拔线或断 Wi-Fi。

两项真机测试使用独立缓存副本，没有改 iOS 26、信任、驱动、Wi-Fi 或防火墙。

新桥接器隔离构建、自检、三份源码哈希和 836 个文件清单通过；验证包已更新完整运行时，旧版保存到 `previous-service-runtime`。新 C# 宿主构建 0 警告/错误（`cancellation-runtime-build.log`），实际使用的 Core/恢复助手/桥接器与验证包一致（`cancellation-host-identity.json`）；72 项错误路由/完整性及启停/取消/旧退出隔离再次通过。

新增 `--wired-window-exit-selected-live <output> <UDID>`：使用已独立验证的 iOS 18 身份和已有绑定，禁用主窗口全局设备发现，仅轮询所选会话状态；仍执行真实投屏、生产控制、真实窗口关闭及退出后控制。因此不将它描述为设备发现测试。

`cancellation-wired-window.log` / `cancellation-native-window.log`：实际视频 1082×2340、有线控制就绪，主窗口 11.48 秒退出；原生恢复确认 `normal_observed=true`，无恢复/退出超时警告。退出后再启两次同版 USB 控制，5.26 / 4.98 秒 ready、6 次实际 ACK，各 0.37 秒停止，宿主退出码 0。没有拔插、USB reset、重启驱动或 Apple 服务。原生日志仍记录可选 UsbDk 尝试未找到后回退到 libusb0，以及启动期视频/音频队列丢帧；这些记录未删改，不能将本次控制恢复通过描述为日志零警告/零错误或历史间歇故障根因已经确认。

## 无线续查：Windows IPv6 网卡范围、瞬时握手与错误显示

`cancellation-packaged-wireless.log` 的两轮正常无线控制通过（23.13 / 22.80 秒 ready，6 次 ACK）；紧接着 `cancellation-wireless-recovery.log` 在首次启动失败，未进入重建验证。原始诊断保存在 `cancellation-wireless-failure-diagnostics.log`：无线 Network 记录不存在，RemotePairing 广播存在，但路由握手失败包含 `ConnectionTerminatedError` 和 `UnicodeEncodeError`。宿主因无主窗口又抛 `Main window unavailable`，覆盖了调用方看到的原始失败。本轮没有将该失败记作恢复通过。

源码诊断 `continued-d2-route-diagnostics.log` 保留真实栈：网卡范围使用 `Microsoft Wi-Fi Direct Virtual Adapter` 时，Python getaddrinfo 将整个 scoped IPv6 当成域名送入 IDNA，长度超限。另只读接口核对发现，SDK 对通过 IPv4 收到的 AAAA 记录按第一个 fe80::/64 子网选择网卡，可能选中已断开的虚拟适配器。两者均会使本可用的 IPv6 路由失效。

修复：

- 把合法数字范围、OS 内部接口名和唯一 Windows 友好名称解析为接口编号；无唯一映射时保留明确的无线配对失败，继续可用的其他来源，不猜设备身份。
- 友好名称不能证明接收广播的接口。优先使用同一广播服务的 IPv4 子网找到可用接口；仅 IPv6 广播时枚举本机具备链路本地地址的候选范围，再以已有所选设备凭据验证，不接受未经验证的路由。
- 瞬时连接终止/socket 异常允许重新打开一次，两个尝试共用原来的八秒预算；取消、未信任及预算已用尽不增加重试，不新建配对。
- 主程序在错误界面所有者不存在时仍保存失败状态与技术诊断，释放提示互斥标记；不再抛出另一个“主窗口不可用”覆盖原始错误。专项宿主也输出失败详情。

证据：

- `wireless-scope-before.log`：实际 OS getaddrinfo 与来源测试在修复前失败；`wireless-scope-final.log` 的 10 项通过，包含失效网卡提示修正、相同路由去重、取消/未信任不重试，以及重试使用剩余预算。
- `error-window-before.log`：真实 WPF 主程序错误方法复现第二次异常；`error-window-after.log` 保留原始状态/诊断及释放标记的检查通过。
- `continued-d2-ipv6-only.json`：初次仅修正编码后仍选错网卡，0 ACK、无线失败，保留为失败。`continued-d2-ipv6-scope-fixed.json`：修正接口选择后，测试子进程仅保留真实 IPv6 广播路由，iOS 18 24.66 秒 ready、4 次真实 ACK、无终止错误、正常停止；并未物理关闭 IPv4 网络。
- `continued-d2-wireless-handshake-retry.json/.log`：每条候选第一次握手注入连接终止，第二次真实验证、隧道及控制成功，22.68 秒 ready、4 次真实 ACK，停止 0.33 秒。
- `python-route-retry-final.log` 的两个旧测试在增加一次重试后，其只提供两个工厂结果的替身耗尽；已按地址提供各尝试替身并保留原错误/关闭断言。最终 `python-wireless-final.log` 全量 **359 项通过**，不把早期失败抹去。

仅修正 scope 的中间 `scope-bridge` 构建没有换入包，不能用它代替最终源码。最终打包及主程序无线恢复结果继续记录。

最终 `wireless-bridge` 和当前应用宿主构建成功、自检通过，`wireless-runtime-build.log` 为 0 警告/错误。新建上述 `iPhoneMirror-wireless-reviewed` 验证目录，包含当前主程序和完整桥接器运行时，Core/恢复助手继续使用验证过的版本。软件错误路由/运行时完整性、桥接器生命周期和无主窗口错误保留测试均通过（`wireless-ddi-routing.log`、`wireless-host-lifecycle.log`、`wireless-error-window.log`）。

最终包 `wireless-final-usb.log` / `wireless-final-wireless.log` 两轮独立启停均通过，各 6 次实际 ACK：有线 5.58 / 4.95 秒 ready，各 0.36 秒停止；无线 23.25 / 23.15 秒 ready，0.37 / 0.40 秒停止。但 `wireless-final-recovery.log` 仍在主程序首次启动失败，广播的两条路由均返回 `ConnectionTerminatedError`；`wireless-resumed-recovery.log` 随后的复测为广播为空、`wireless_device_not_discoverable`。两个退出码均 1，不能用前期主程序成功替代当前包恢复验收。修正后的错误显示保留了原始错误及技术详情，没有再抛 `Main window unavailable`。

失败后的被动 mDNS 读取又返回两组 IPv4/IPv6 广播，不能据此证明失败期间所选设备广播正常或软件已自动恢复。主程序宿主的 836 个桥接运行时文件与验证包完全一致；只读防火墙检查显示三个配置文件均未启用，不将本机故障归因于未证实的防火墙规则。已请求只确认 iOS 18 的解锁亮屏及无线条件，不要求操作 iOS 26。当前仍继续隔离主程序/独立桥接的区别。

`wireless-host-path-isolation.json/.log`：不用主程序，直接启动主程序宿主目录中的同一桥接器，iOS 18 21.41 秒 ready、4 次实际 ACK、0 终止错误、停止 0.33 秒。这排除了该目录中桥接器内容不完整作为当前失败的解释，但不能独立证明主程序干扰广播或其他因果；间歇发现问题仍需继续定位。设备/网络条件确认仍待用户回复，当前没有活着的测试等待句柄。

## mDNS 软件续查：地址族回退、网卡查询与本地错误提示

固定版本 SDK 的 Bonjour 浏览器存在可独立复现的缺陷：IPv4/IPv6 任一初始化失败都会中断另一个可用协议；原始 socket 在 endpoint 交接失败时没有关闭；第二个协议初始化被取消时没有关闭第一个监听；网卡枚举发生在清理保护之外；坏广播包会中断整个浏览；只发送一次 PTR 请求，首次丢包或分包应答没有补查；IPv4 加入和发送只走默认网卡。

新增受版本控制的 `tools/mdns_discovery.py`，复用 SDK 的 DNS 编码、解析与结果类型，由桥接器持有监听、交接和期限。地址族失败独立处理；所有初始化/取消路径关闭自己的资源；IPv4 查询遍历本地接口，IPv6 接口枚举失败不阻断 IPv4；原有发现期限内重发和补查 SRV/A/AAAA；忽略坏包、去重与处理 goodbye，限制数据队列和缓存大小。DNS 名称仅折叠 ASCII 大小写，保留 Unicode 标签字节。打包暂存脚本显式包含此模块，没有修改安装的 SDK 或机器网络设置。

Windows Proactor 的 UDP transport 会把后续发送排队。连续修改同一 socket 的 `IP_MULTICAST_IF` 后，排队数据可能全部从最后一个接口发送；新增模拟真实队列语义的回归先失败，再将 IPv4 小数据报通过已由 endpoint 持有的非阻塞 socket 立即提交，保证提交时接口选项有效。IPv6 每个数据报的接口在目标 tuple 内，无该选项竞态；EWOULDBLOCK 等单接口发送错误交由下一轮查询重试。

电脑全部本地发现监听均不可用时，现在返回 `wireless_discovery_unavailable`，应用四语提示网卡、本地发现和 UDP 5353 独占条件；不再引导用户按“配对失败”反复连接 USB。没有广播与已发现但握手失败仍使用各自原来的代码，原始 socket 错误链及技术诊断保留。

证据目录仍为 `work/two-device-review/`：

- `mdns-discovery-before.log`：7 项测试在固定 SDK 上出现 5 个失败、3 个错误（含子场景），实际本机 socket 在 endpoint 失败后仍打开。`mdns-discovery-after.log` 的首轮新实现测试还有两个失败：极短的测试预算被未隔离的真实网卡枚举消耗；补上测试接口替身后，行为检查通过，未增大生产期限。
- `mdns-discovery-final-2.log`：15 项通过，含真实本机回环 UDP 丢弃首次请求后的发现和 socket 关闭。该项不接触手机、不等同于物理 Wi-Fi 丢包。
- `mdns-interface-send-before.log`：新增排队发送接口回归失败；`mdns-interface-send-after.log` 同项及完整 16 项发现测试通过。
- `mdns-error-classification-before/after.log`：本地发现不可用原先误分为配对失败，修复后 26 项传输回归通过。`mdns-localization.log` 四语资源/引用检查通过。
- `python-mdns-diagnostics-final.log` 在 .NET/PyInstaller 并行构建期间有 3 个失败、1 个错误，涉及 35–300 毫秒测试预算；日志同时记录超过预算的事件循环延迟。保留该失败；构建结束后未改测试期限/断言，`python-mdns-diagnostics-serial.log` 同源码全量 **375 项通过**。该数字属于接口立即发送修复前的阶段；最终源码回归另行记录。
- `mdns-diagnostics-runtime-build.log`：本轮主程序/宿主构建 0 警告、0 错误。该阶段验证包 `artifacts/iPhoneMirror-mdns-diagnostics-reviewed/`、836 个文件校验、源码/暂存、宿主/包核心文件一致；实际 frozen archive 含 DNS 名称及独立错误修复。`mdns-diagnostics-error-routing.log` **74 项**错误映射/诊断、应用侧 bridge 完整性通过；`mdns-diagnostics-error-window.log` 与 `mdns-diagnostics-host-lifecycle.log` 通过。
- `mdns-diagnostics-d2-state.log`：只查询指定 iOS 18，DDI 挂载正常。`mdns-diagnostics-d2-usb.log`：上述阶段包由当前宿主两轮有线 ready 4.25 / 3.58 秒、6 次实际 HID ACK、停止 0.32 / 0.25 秒。该阶段 bridge 尚不含后来接口立即发送修复，不作为最终 bridge 的实测。
- `mdns-selected-snapshot.log`：只连接指定 iOS 18 验证身份，随后 mDNS 浏览为 **0** 个服务；没有对广播地址发起 TCP 验证。用户此前无线条件确认请求尚待回复，未对收回的 iOS 26 发起新测试。

这些软件缺陷不能独立证明先前主程序无线间歇失败的完整根因。最终包的无线启动/恢复、物理网络中断/漫游、其他设备及历史 Apple 漏列问题仍需真实证据，不宣称全链路已完成。

### 接口发送修复阶段的完整包复验

`artifacts/iPhoneMirror-mdns-links-reviewed/` 为该阶段完整包，bridge 为 `A298CD6831DC563665352FD7C61D2980C0F76FA31C9233CCD6A9E182912C88AC`，应用 DLL 为 `BC3D1E4B4BDCABC145BE1346D57C299E10A97DD883EE7755AA096D241D77910D`，Core/USB 恢复助手未变。`mdns-links-build-identity.json` 四份 Python 源码/暂存一致；`mdns-links-artifact-integrity.json` 包和宿主各 836 个清单文件均无失败，包含真实 frozen 发现模块。`python-mdns-links-final.log` 完整 **376 项通过**。最终错误路由、原错误保留和进程生命周期专项均通过。

- `mdns-links-d2-usb.log`：指定 iOS 18 两轮有线就绪 4.09 / 3.52 秒、6 次真实 ACK、停止 0.31 / 0.26 秒。
- `mdns-links-wired-window.log`：主程序真实投屏 1082×2340 与控制共存，关窗 13.74 秒；退出后两次有线就绪 3.51 / 3.71 秒、另 6 次 ACK，各 0.27 秒停止。`mdns-links-native-window.log` 明确 `normal_observed=true`，没有 USB 恢复/主程序退出超时警告；仍保留 UsbDk 未安装后成功 libusb0 回退以及启动期视频/音频丢帧日志，不能声称日志零错误。没有触摸手机 UI、重启服务或驱动。
- `mdns-links-local-network.json`：当前电脑 Wi-Fi 与热点虚拟接口均 Disconnected，此前 `192.168.137.1` 热点地址不存在。没有更改网络设置；此证据解释当前缺少原热点连通条件，不能解释此前网络正常时所有间歇失败。已将具体条件告知用户，请仅恢复 iOS 18 的可达网络。

随后继续审查同名虚拟网卡，发现 `_remote_pairing_route_hosts` 在按服务 IPv4 网段纠正接口之前，先要求描述名唯一，导致丢弃本可解析的 IPv6 地址。已把描述名唯一检查放到网段解析之后；仍保留实际 OS 名称/数字 scope，未能确定接口时拒绝原有歧义，不绕过设备验证。`mdns-duplicate-scope-before.log` 新回归失败，`mdns-duplicate-scope-after.log` 修复后 11 项通过；新增 IPv4 实际路由被阻断的子场景，只靠纠正的 IPv6 回退仍成功（软件替身，不当作真机）。该修复晚于上述包，另行构建最终包，不能混称已在 `mdns-links` 包中。

### 同名网卡修复后的最终包

- 当前完整包：`artifacts/iPhoneMirror-mdns-routes-reviewed/`，bridge SHA-256 `43952F404287EA2977DCF4E104AD8E16974EF26B5719EF32F1840A08B6360324`；应用 DLL `BC3D1E4B4BDCABC145BE1346D57C299E10A97DD883EE7755AA096D241D77910D`，Core/USB 恢复助手仍同前。包含工作区已有其他应用改动，本控制专项不宣称它们全部已验收。
- `mdns-routes-build.log`：构建与 frozen 功能自检通过。`mdns-routes-build-identity.json`：四份源文件/实际暂存一致；另 `audit_mdns_package.py` 直接提取 frozen archive，确认 bridge、DDI 支持、mDNS、运行时检查四个模块 code object 与当前源文件编译结果相等，排除只核对暂存却打包旧代码的问题。
- `mdns-routes-artifact-integrity.json`：包/宿主各 836 个清单文件无失败；`mdns-routes-host-identity.json` 主程序、Core、助手、bridge 一致。`mdns-routes-error-routing.log` 74 项错误路由及应用侧 bridge 校验通过；`mdns-routes-error-window.log` 原始失败保留、`mdns-routes-host-lifecycle.log` 启停/重启/取消/旧退出隔离通过。
- `python-mdns-routes-final.log`：最终源码全量 **377 项通过**，含 IPv4 被阻断而同名网卡的 IPv6 仍可验证的子场景。未用并行构建期间失败的结果冒充通过。
- `mdns-routes-d2-usb.log`：最终包指定 iOS 18 两轮有线就绪 3.93 / 3.53 秒、6 次真实 ACK、停止 0.28 / 0.27 秒，退出码 0。`mdns-routes-final-device-state.log` DDI 挂载正常；`mdns-routes-final-cleanup.json` 没有运行中的测试/bridge/助手。

最新无线启动、主程序恢复和取消尚未在可达网络验收，不将电脑热点断开的环境改写为成功，也不要求重新开放 iOS 26。前一包的真实投屏关窗与退出后控制属于明确记录的阶段；最后新增只涉及无线地址解析，未重新改动主程序或原生模块，但该边界不能代替最终无线实测。

## mDNS 初始化与接口信息缺失的边界续查

继续确认并修复以下遗漏，未修改安装的 SDK、信任、驱动、防火墙或 Wi-Fi 设置：

1. UDP bind 成功不保证能收到组播应答。原实现吞掉每个加入组播接口的异常，即使全部失败仍进入等待。现在该地址族一个接口也未加入时关闭其原始 socket，继续尝试其他地址族；全部失败沿原错误链报告 `wireless_discovery_unavailable`，不进入配对握手。单接口失败不阻断其余接口。
2. SDK DNS parser 接受某些格式异常的标签，随后的补查编码却拒绝它们，造成一个坏 PTR 中断其他有效服务。现在浏览器在接纳数据报前核对相关 DNS 名称的编码和 255 字节长度，跳过坏包；正常服务继续原有期限内的补查。
3. IPv6 数据报自带的接口编号原先仍经过网卡描述映射，描述缺失时会丢掉已知路由。现在 link-local 地址保留接收的数字 scope；网卡描述读取发生 OSError 时保留无需描述的 IPv4/global 地址和数据报 scope。没有接口线索的 IPv6 link-local 地址继续拒绝，不猜测设备身份。

新增证据：

- `mdns-boundaries-before.log`：两种地址族全组播失败回归均失败，实际坏 PTR 引发 `ValueError: label too long`；`mdns-interface-metadata-before.log` 确认接口描述丢失会丢 IPv6 地址、描述枚举 OSError 中断发现。`mdns-boundaries-final-2.log` **21 项**发现测试通过，真实 socket 在 endpoint 失败后的关闭测试增加“确实进入 endpoint”的断言；没有把更早的失败代替它。
- `mdns-boundaries-transport.log`：**27 项**传输回归通过，实际调用 bridge 的 RemotePairing 入口确认全本地协议失败保持原始原因链，且不创建 TCP 配对候选。
- `mdns-boundary-real-sockets.log` 首次测试脚本先因缺少仅用于 DDI 场景的 `TEST_DDI_CACHE` 崩溃，错误事件为空，没有计通过。为这个不需要 DDI 的场景直接运行 bridge main 后，`mdns-boundary-error-final.json/.log` 实际打开的 IPv4/IPv6 UDP socket 均记录已关闭，4.14 秒内收到 `wireless_discovery_unavailable`、0 ready/ACK、正常退出，无强制终止。只对测试进程的组播 setsockopt 与 Network mux 注入，**没有联系任何手机**；这不是 iOS 18 无线实测成功。中文协议数据在原始日志中为 UTF-8，控制台转换显示不能当作 bridge 编码错误。
- `python-mdns-boundary-final.log`：当前源码完整 **383 项通过**。
- 当前完整包：`artifacts/iPhoneMirror-mdns-boundary-reviewed/`，bridge SHA-256 `F5C4F28838D960FC6162FD6A36E726F37A1B1C644D52794DA456C05272F9B199`。应用 DLL/原生 Core/USB 恢复助手与上一阶段相同；本轮只新增发现层修复。`mdns-boundary-build.log` 构建/功能自检、`mdns-boundary-build-identity.json` 四份源文件/暂存、`mdns-boundary-artifact-integrity.json` 四个实际 frozen code object/当前源文件编译结果及包/宿主各 836 文件均通过。
- `mdns-boundary-error-routing.log` 74 项错误映射及应用侧 bridge 完整性、`mdns-boundary-error-window.log` 原始失败保留、`mdns-boundary-host-lifecycle.log` 启停/取消/旧退出隔离通过。
- `mdns-boundary-d2-usb.log`：指定 iOS 18 两轮有线 3.88 / 3.51 秒就绪、6 次实际 HID ACK、0.27 / 0.26 秒停止。`mdns-boundary-final-device-state.log` DDI 挂载正常，`mdns-boundary-final-cleanup.json` 没有存活测试/bridge/助手。
- `mdns-boundary-passive.log`：只连接指定 iOS 18 验证身份，之后浏览 mDNS 服务仍为 0，未对任何广播 TCP 端点发起验证。用户的 iOS 18 可达网络条件问题仍待回答，iOS 26 不参与。

全链路目标保持未完成。新版无线正向、主程序恢复和取消、物理 Wi-Fi 中断/漫游、历史 Apple USB 漏列因果与手机 UI 手势仍未全部验证；以上软件及有线通过不替代这些验收。

## 分包地址族、局域网缓存和地址解析提示续查

后续新测试始终只使用指定 iOS 18，未测试、连接或卸载已收回的 iOS 26。修复以下四项可独立复现的遗漏：

1. 收到 IPv4 后停止补查 IPv6，反向也一样；首选地址不通时可能丢失另一个可用协议的回退。现在在原发现期限内按缺少的地址族分别补查。
2. 其他局域网设备的 SRV/TXT 填满请求服务的缓存，外部主机地址占满容量后无法加入已发现的手机地址。现在仅接纳请求服务名称范围内的 SRV/TXT，保留分包提前到达的地址；满额时已学习到的服务主机可以替换无关地址主机，仍保留容量上限。
3. goodbye 删除记录后留空桶，继续占用服务/地址缓存容量。现在删除空桶及已撤回的 TXT 项，让随后出现的服务正常使用槽位。
4. 已有 PTR/SRV 服务但没有解析到地址时，原先返回空列表，主程序显示“未发现服务”。现在返回 `wireless_address_resolution_failed`，四语言提示局域网、客户端隔离和 mDNS；不发起 TCP 配对、不把地址问题当作需要刷新 USB 配对。

软件证据仍在 `work/two-device-review/`：

- `mdns-partial-family-before.log` 两个地址族子场景均失败；`mdns-cache-before.log` 无关广播/空桶回归均失败。`mdns-cache-route-after.log` **25 项**发现测试通过，包含真实 DNS 数据报、缺失地址族补查及 IPv4 拒绝后 IPv6 仍验证成功、失败连接关闭的跨模块测试；该连接验证用受控替身，不是真机。
- `mdns-addressless-before.log` 新回归在修复前失败。`python-mdns-resolution-final-2.log` 最终源码全量 **388 项通过**，包含 28 项传输测试；新地址错误不会创建 TCP 候选。`python-mdns-resolution-final.log` 是独立错误码/四语提示完善前的中间通过记录，不代替最终源码验证。
- `mdns-resolution-runtime-build.log` 应用/宿主 0 警告、0 错误；`mdns-resolution-localization.log` 四语资源及引用检查通过。`mdns-resolution-error-routing.log` **76 项**错误映射、原始诊断保留及应用侧 bridge 完整性通过；`mdns-resolution-error-window.log` 和 `mdns-resolution-host-lifecycle.log` 通过。

网络与备用路径证据：

- `mdns-cache-local-adapters.json` 发现热点接口已恢复 Up；`mdns-cache-passive.log` 只连接指定 USB 设备验证身份，然后收到一组广播。`mdns-cache-scope.json` 确认该组 IPv4 邻居网卡地址与 USB 读取的 iOS 18 Wi-Fi 地址相同。没有修改 Wi-Fi/信任/驱动/防火墙。
- `mdns-cache-selected-ipv6-fallback.json/.log` 首次为未发现服务、0 ACK，未计通过。后来只读快照证明同一 host/port/地址组的广播 identifier 已变化；原测试脚本按旧 UUID 过滤，属于测试范围识别的假阴性，生产代码没有该过滤条件。测试改为读取当前邻居表、按已验证的 Wi-Fi 地址关联，仅放行该设备的广播；不通过 TCP 探测其他手机来猜身份。
- 最终源码 `mdns-resolution-selected-ipv6-fallback.json/.log`：测试进程内禁用 Apple Network 路径、两次 IPv4 验证均注入不可用；真实 mDNS 和选定 iOS 18 的真实 IPv6 连接 **21.50 秒 ready、4 次实际 ACK、0.30 秒正常停止**，无终止错误、无强制结束。源码故障注入不能称为发布 exe 自然选择该来源或物理 IPv4 故障。
- `mdns-resolution-addressless.json/.log`：浏览真实广播并确认指定设备后，仅在测试进程丢弃地址应答；**19.47 秒收到独立地址解析错误**、0 ready/ACK、正常退出。TCP 候选入口含禁止调用断言，没有通过伪造 ready/ACK 记为通过。

当前完整验证包为 **`artifacts/iPhoneMirror-mdns-resolution-reviewed/`**：bridge SHA-256 `B0CF26C40A981339EBFFADB3E4CBD1255FA641AE7DC7B5A869FE373EC9DE6F20`，应用 DLL `C10B6A0EDB08C84AAD9A17A66948623EAD40F2A2F870AB28F65017972D80CA1D`。Core `6D75C340071D5EBB74AB0848834345D020AB0B5AD8B1B72980F5980EB65CD71D`、恢复助手 `5F921A5E0D0551C6DD11DBFF1F9DDE69AAA0693F7BB9BA323B5E390402B61F8C` 沿用未改版本。`mdns-resolution-build.log` 构建及 frozen 功能自检通过；`mdns-resolution-build-identity.json` 四份源码/暂存一致；`mdns-resolution-artifact-integrity.json` 四个实际 frozen code object 与当前源文件编译结果相等、包/宿主各 836 个清单文件无失败；`mdns-resolution-host-identity.json` 应用/Core/助手/bridge 匹配。应用仍包含工作区其他功能改动，不借本控制专项验收那些功能。

最终包实机结果：

| 检查 | 结果与原始记录 |
| --- | --- |
| 指定 iOS 18 有线两轮启停 | 3.95 / 3.97 秒 ready，6 次真实 ACK，两次停止均 0.28 秒；`mdns-resolution-d2-usb.log` |
| 指定 iOS 18 无线两轮启停 | 5.05 / 5.00 秒 ready，6 次真实 ACK，停止 0.28 / 0.27 秒；`mdns-resolution-d2-wireless.log` |
| 主程序无线桥接进程中断恢复 | 7.10 秒恢复，前后 6 次真实 ACK；`mdns-resolution-wireless-recovery.log` |
| 恢复中取消 | 0.08 秒完成，继续观察七秒没有延迟重连；同上 |
| 最终状态 | DDI 挂载正常、没有存活测试/bridge/助手；`mdns-resolution-final-device-state.log`、`mdns-resolution-final-cleanup.json` |

恢复检查使用此前独立验证的指定身份，并禁用后台全局设备发现以保护收回的设备；它验证生产恢复与真实 HID 传输，不代表完整设备发现、可见主窗口交互或物理 Wi-Fi 中断。三项最终实机检查退出码均 0（`mdns-resolution-final-live-exits.json`）。

上一阶段 `iPhoneMirror-mdns-cache-reviewed` 的 387 项软件回归、74 项错误路由及其有线/无线/恢复成功日志保留为中间证据，不替代上述最终包含独立地址错误提示的包。该阶段全链路目标仍未完成；后续进展见下节。

## 2026-10-09：直接读取 Safari、持续触点与最终 exe 实测

范围仍为指定 iOS 18；收回的 iOS 26 未连接、控制或测试。用户要求直接读取网页后，通过指定 USB 身份建立 DVT 截图服务，确认 Safari 是测试服务的 403 Forbidden。重新核对这台设备的 Wi-Fi 地址与当前邻居表，确定手机地址由旧的 `.191` 变为 `.11`；只更新观察服务的选定地址限制并定向点击已观察到的 Safari 刷新按钮。收到真实 `ready`、Active 0；未修改信任、配对、系统网络、服务或驱动。原先 ScreenshotService 的 InvalidService 失败日志保留，改用可用的 DVT 截图端点；403 属于测试观察服务，不是生产 DDI/控制故障。

生产桥接器修复两项持续输入问题：

- 180 秒 direct HID 主动刷新只在触点、键盘和按钮全部释放后进行；等待时仍检测旧连接，真实故障照常修复。状态检查和每份 HID 提交使用同一生命周期锁，避免并发按下被刷新抬起。失败释放保留可能按住的状态，实际故障修复仍可替换连接。
- 有线/无线静止触点每秒补发最近成功发送的完整活动状态，保留所有 slot/identity/坐标，生成新单调时间戳。发送新状态前清掉旧保活快照，成功后再采纳；因此释放失败、取消和恢复不会重放旧按下。近期已有移动报告时不补发，不重发键盘按键。

真实失败与对照保留：`held-refresh-natural-held` 和 `held-refresh-static-touch-shift` 在触点与 Shift 静止时分别约 14.3 / 6.7 秒收到手机全部 touchend；无 HID 刷新或连接错误。单独触点静止 25 秒通过（`held-refresh-static-touch-only`），触点与 Shift 每秒补发状态的对照 25 秒通过（`held-refresh-sampled-touch-shift`）。源码保活修复后，测试端不补发 move 的同一组合 25 秒通过（`touch-keepalive-source-static-shift`）。不将这组证据扩大为所有 iOS 版本的内部超时机制已经确定。

首轮五点宿主绕过生产 Host 的启动流程，错误地保留 Idle 状态，导致“桥接器尚未就绪”；`input-recovery-five-point-usb` 失败记录保留。测试现要求完整 exe 运行时，并使用生产 Host 启动/停止，继续通过生产 ViewModel 路由、写入器和真实 HID，由手机 TouchEvents 独立核对。设备发现与窗口选择仍为隔离夹具，不属于物理 Windows 手指、WM_TOUCH 或键盘钩子验收。

当前验证包为 **`artifacts/iPhoneMirror-touch-keepalive-reviewed/`**，bridge SHA-256 `F35C7E6FD75520341CF0D4582B7F728596D45298FECD34954C8226464F65BA72`。应用 DLL `C10B6A0EDB08C84AAD9A17A66948623EAD40F2A2F870AB28F65017972D80CA1D`、Core `6D75C340071D5EBB74AB0848834345D020AB0B5AD8B1B72980F5980EB65CD71D`、恢复助手 `5F921A5E0D0551C6DD11DBFF1F9DDE69AAA0693F7BB9BA323B5E390402B61F8C` 沿用已验证的生产文件。测试程序集虽然从当前工作区构建，但仅覆盖测试文件，未混入同时发生的其他应用功能编译产物。包/宿主身份见 `touch-keepalive-host-identity.json`。

| 最终包检查 | 结果与证据（均位于 work/two-device-review） |
| --- | --- |
| 全量 Python | 396 项通过，41.968 秒；`python-touch-keepalive-final-2.log` |
| 构建及内容 | frozen 功能自检成功；四个实际 code object 与当前源码相等，包/宿主各 836 个清单文件完整；`touch-keepalive-build.log`、`touch-keepalive-artifact-integrity.json` |
| 错误提示与进程 | 76 项错误路由/原始诊断保留、错误窗口、启停/取消/旧进程退出隔离通过；`touch-keepalive-error-routing/error-window/host-lifecycle.log` |
| 有线/无线五点及映射 | 两项退出 0，原触点保持、单指移动、第六点拒绝、独立释放、slot 复用、并发映射及取消后零触点均由手机事件核对；`touch-keepalive-five-point-exits.json` 及各 transport-result.json |
| 有线自然 180 秒刷新节点 | 五点与 Shift 静止 190.49 秒，测试端无 move 补发；原编号不变，部分释放后仍保持其余触点，全释放后才刷新，新触点再次使用正常；一代 ready、最终 Active 0、停止 0.24 秒、正常退出；`touch-keepalive-natural-held.json/.log`、实际截图 `touch-keepalive-five-held.png` |
| 无线静止输入 | 五点与 Shift 静止 25 秒通过，部分/全部释放与新触点正常，停止 0.25 秒、正常退出；`touch-keepalive-wireless-static-shift.json/.log` |
| 发布 exe 自然备用来源 | 上一项未注入发现/路由故障；Apple Network 未列出指定设备，自动进入 RemotePairing，两条 QUIC 握手失败后重开，20.68 秒 ready，真实手机触控通过；同一原始日志 |
| 关闭自己的真实无线 socket | HID / 传输故障分别 7.12 / 6.81 秒生产恢复，每项前后 6 次 ACK；恢复中取消 0.09 / 0.08 秒，各继续观察七秒无旧重试复活；`touch-keepalive-socket-exits.json` 及两份 log |

长按过程中出现一次剪贴板读取短暂失败并自行恢复（`clipboard_poll_recovered`），未中断 HID；来源回退、清理注入等警告照实保留，软件检查通过不等于日志没有警告。故障 socket 是测试进程关闭其自己的真实连接，不能替代物理 Wi-Fi 中断或漫游。

全链路目标保持未完成：历史 Apple USB 漏列的因果定位、物理 Wi-Fi 中断/漫游、更长时间及更多系统交互尚需验证。发布 exe 自然备用来源与当前手机 UI 手势已经补验；本轮最终包有线投屏关窗恢复失败，详见后续现场。前一轮 `input-recovery-wired-window` 中断记录不算通过。

## 关窗、信任弹窗与点击后未恢复的现场

`touch-keepalive-wired-window.log`：1082×2340 真实投屏与控制就绪；真实窗口关闭 21.59 秒，随后有线控制 `apple_device_not_found`，退出码 1。`touch-keepalive-native-window.log` 记录 stop/release 均 2/2、最终 HPD0 成功、先关闭流接口后进行单次恢复请求；助手退出 0，但 Apple USB 行消失、恢复等待超时，`normal_observed=false`，停止警告 -12。

`selected_wireless_screen.py` 使用事先通过指定手机 USB 身份、Wi-Fi MAC 和邻居核对的 `.11` 地址，只连接该端点，既有凭据 `autopair=False`，RSD 再校验 UDID，仅读取截图。`trust-after-cleanup-screen.png` 保存真实信任弹窗。用户明确先未点击，后点信任并输入密码、没有插拔。点击后的 `trust-clicked-without-replug-device.log` 仍 DeviceNotFoundError；原生探针 parent/media/management=1/1/1、27015 的 selected_usb=0，Apple 服务运行中、无投屏/桥接/助手残留。

磁盘 Apple Lockdown 与 RemotePairing 文件未被改写。`trust-after-cleanup-host-identity.json` 只读确认捕获 mux 和 AMDS BUID 相等，AMDS 与本地所选配对记录 HostID、SystemBUID、Host/Root/DeviceCertificate 全相等；没有进行 Pair/Unpair。此证据不证明手机内部信任状态，不能仅凭磁盘记录宣称信任无问题。

新增独立 hub 只读探针 `selected_hub_config.py`：通过所选手机精确 PnP 身份关联父 hub 和端口，仅查询该端口 `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX`；没有手机接口 claim、重置或配置修改。`trust-clicked-without-replug-hub-config.json` 实测普通配置 **3**、正常连接，Apple 服务仍漏列。现阶段故障定位为普通配置切回后管理服务未重新列出，尚未找到充分证据确定根因，不能将助手成功、PnP 正常或点击信任当作全链路恢复成功。现场保留并继续查接口交接和错误提示。

后续 `selected_winusb_probe.py` 重新核对管理子节点的精确父身份，只打开该管理接口而不发送 bulk/control 或 reset，返回 Windows 50（不支持该请求）；`trust-clicked-selected-winusb.json` 保存此结果。使用同一独立地址/既有身份读取所选手机 lockdownd 历史接口，返回零条日志（`trust-clicked-selected-lockdownd.log`），不能用零条输出推断手机没有配对错误。随后助手新增只读 inspect 也确认精确序列号及 active configuration=3；原生 `--usb-restore-state` 已加入 hub 只读查询，得到 parent/media/management=1/1/1、config=3、USBMux 指定 USB=0（`trust-clicked-hub-native-state.log`）。尚未取得重新插拔后正常管理接口对照。

本轮修复助手在序列号没有读取成功时仍可能操作所选拓扑节点的问题：缺失/失败/超长串读取返回 34，不提交控制请求；序列号不符返回 28。实际助手入口的不连接设备测试覆盖上述场景、其他设备不打开、普通配置恢复无操作、已经 QuickTime 时不重复激活及 inspect 无 vendor request；`trust-helper-identity-tests.log` 通过。原生恢复失败日志新增 hub 配置与 PnP 证据，四语恢复提示同步改为 Apple USB 通道未确认可用，提示信任/密码及仍失败时插拔；原生协议、四语资源、错误窗口检查通过。

独立故障测试复现旧强制停止只杀启动进程后多层 Python 子进程存活。改为 `Kill(entireProcessTree: true)` 后仍复现（`trust-process-tree-before-selected/after/stop-trace/stop-trace-2/stop-observe.log`），不能把该中间方案称为修复。最终新增 `OwnedBridgeProcess`：先 suspended 创建进程，只继承标准管道，再绑定 kill-on-close 作业对象并恢复执行；整个后代树有相同所有者。整个作业保留原 8 秒正常退出预算，退出后的孤儿或崩溃后代在等待 EOF 前收尾；强制结束不再交出 mux 检查点。

`trust-job-lifecycle-final.log` 通过正常启停、重启、取消、旧退出隔离、8 秒真实强制超时、多层后代退出、启动进程崩溃的 readiness 撤销、正常退出后的孤儿、无关进程保留、中文/引号/空参数、环境和 stderr 管道、连续失败启动无持续句柄增长。首次句柄测试因 Win32 错误路径首次使用增加 5 个句柄而失败；跟踪 12 次为一次增加后全程相等，修正比较为首次失败后的稳定计数，原始失败和轨迹完整保留，不称为已经复现生产句柄泄漏。最终应用/宿主构建 0 警告/错误；进程修复与本次真机信任弹窗的因果关系仍未证实。

该阶段 App Logic 在已有蓝牙 raw-input 源码文本断言失败（`trust-diagnostics-app-logic.log`）；误用生命周期参数后触发的一轮广泛 UI 检查遇到剪贴板模拟接收断言失败（`trust-process-tree-before.log`）。未删改这些失败；后续定位及新修复如下。iOS 18 仍需重新插拔对照回复；未测试或连接 iOS 26。全链路目标保持未完成。

## 继续审查：USB 恢复卡片、旧就绪缓存和提示窗口

新的生产缺陷与修复：

- `_usbRestoreRecovery.Observe` 原来读取为显示保留的设备卡片。Apple 服务漏列但 PnP 仍有手机、或另一台手机仍连接时，旧卡片被保留，恢复保护无法记录所选管理通道消失。现在只有本轮实际枚举才能推进恢复，输入为原始 USB 行；延后的枚举不推进状态，重新出现还须本轮 Lockdown 读取成功。保护尚未解除且无有线投屏会话时请求新元数据。此修复允许已观察到缺失的管理通道重新可读后解除保护，不把物理插拔作为唯一来源，也不把卡片变化当作可用证明。
- 原生 `DeviceManager` 新查询失败后用整个旧元数据覆盖，重新写回 Ready/可访问/配对状态。现在失败只保留名称、机型和系统版本，失败状态进入缓存；以后继续尝试，不再继承旧就绪。DeviceID/mux 端口变化也验证新路由，健康且未变化的设备沿用缓存以避免增加正常轮询成本。
- 运行时稳定复现提示窗口把自己设为 Owner：WPF 在没有主窗口时把首个新窗口设为 MainWindow，原对象初始化器随后读到它自己。错误被日志捕获，用户看不到提示。错误与停止提示现在都在构造前保存已有 owner，仅在其可见时指定。`usb-inventory-runtime-logic.log` / `usb-inventory-capture-isolated.log` 保留修复前失败，应用日志同一时刻为 `capture_error_notice_failed`、Owner 不得为自身。

剪贴板失败纠正：测试用的 `UsbTouchBridgeHost` 未进入已启动状态，`OnBridgeEvent` 按设计丢弃全部协议消息，因而旧回复拒绝断言并未实际执行读状态，而新回复断言失败。为设备替身设置启动/停止生命周期后，解析器及有线/无线 VM 接收路径的慢旧回复、新回复、断开/停止后迟到回复均通过。没有修改生产剪贴板逻辑或放宽断言。旧 raw-input 源码文本断言已移除过时的独立窗口禁用要求，相关行为覆盖原已存在；App Logic 后续失败为继承 `DOTNET_ROOT_X64` 被更新器正确拒绝。只在测试子进程清除运行时覆盖变量，未降低更新器安全检查。

验证记录均在 `work/two-device-review/`：

| 检查 | 结果与证据 |
| --- | --- |
| 应用/运行时构建 | 0 警告/错误，`usb-inventory-recovery-runtime-build.log`、`usb-inventory-notice-runtime-build.log` |
| 新原生缓存检查、协议/恢复策略/身份助手 | 3 项通过，`usb-inventory-native-tests.log`；之前全原生 15 项通过保留在 `usb-owner-native-all-tests-complete.log` |
| App Logic | 全量通过，`usb-inventory-app-logic.log`；旧失败完整保留 |
| 应用实际运行时 | `usb-inventory-runtime-logic-final.log` 通过，覆盖双设备替身、保留卡片、延后枚举、旧 Ready、不可读路由、通道返回、错误窗口及释放顺序；没有连接任何手机 |
| 错误路由/诊断与错误显示 | 76 项通过，`usb-inventory-error-routing.log`；无主窗口显示通过 `usb-inventory-error-presentation.log` |
| 包/宿主桥接器 | 各 836 个清单文件完整、4 个实际 frozen 模块与当前源编译相同，`usb-inventory-artifact-audit-final.log` |
| 真实发布 exe 的作业对象路径 | 运行自检、管道、正常退出和空作业通过，`usb-inventory-owned-runtime-check.log`；未连接手机 |

新完整验证包 `artifacts/iPhoneMirror-usb-inventory-reviewed/`，宿主 `work/two-device-review/usb-inventory-runtime-host/`。`usb-inventory-host-identity.json` 确认 App `F80FC3318E7591D0DF7503AE84BD8C66719E29D4916DCD4C9D7A9DFB89674F1D`、Core `133EAF50B9DC3B589FDBA59D27C84CD3BDFA54CE7855B6E21B0189A6B05B4D5B`、助手 `BBCEAE35DD431C716102926294172D625ADE76F8555338BBDAA72EA1F329DD2F`、bridge `F35C7E6FD75520341CF0D4582B7F728596D45298FECD34954C8226464F65BA72` 匹配。第一次暂存假设存在 App PDB 而失败，`usb-inventory-stage.log` 保存原错误；确认该 Release 构建无 PDB 后继续完成同一验证目录，最终记录为 `usb-inventory-stage-final.log`。新包包含工作区其他功能修改，本次专项不宣称全部复审。

最新只读 `usb-owner-trust-accepted-latest-state.log` 仍为 config=3、parent/media/management=1/1/1、27015 的所选 USB=0。故障时段的 PnP 专项只查到两次所选父节点总线移除，没有可用 UMDF Operational 日志；系统/应用没有查到相关 Apple/WUDF 崩溃记录，日志缺失不能排除驱动问题（`usb-owner-selected-pnp-channel-events.json`、`usb-owner-selected-driver-events.json`、`usb-owner-apple-host-fault-events.json`）。仍缺正常插拔后的管理接口对照与新包真实投屏关窗后无需插拔的验证，不能把以上软件通过当作信任弹窗根因已解决。仅 iOS 18 的对照请求仍待回复，iOS 26 不连接/控制/测试；全链路目标未完成。
