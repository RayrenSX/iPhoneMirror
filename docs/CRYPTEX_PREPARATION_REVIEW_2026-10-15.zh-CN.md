# Cryptex 准备全流程审查（2026-10-15）

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；历史测试结论未在本次重新执行。

> 本文件保留修复前的审查发现。后续修复、复审、成品同步及剩余实机验证边界见 [修复与复审记录](CRYPTEX_PREPARATION_FIXES_2026-10-15.zh-CN.md)。

**结论：主协议路径基本一致，但目前还不适合认定为可完整交付。** 发现两项 P2 清理/取消缺陷、一项 P3 超时分流问题；此外，当前应用输出包尚未包含新 Cryptex 实现。现有 14 项专项测试通过，不能覆盖下面已复现的真实 SDK 生命周期缺口。

本轮只做审查、受控复现和报告，没有修改产品源码或依赖库，没有调用 Apple TSS、挂载镜像、卸载现有 DDI、改变 nonce 或连接手机。保留用户要求的 #56/#55/#52 范围。

## 发现

### 1. [P2] 连接握手被取消时，Cryptex 内部连接没有释放

产品接入位置：[ddi_support.py:317](../tools/ddi_support.py#L317)、[ddi_support.py:335](../tools/ddi_support.py#L335)。

`prepare_cryptex_ddi` 直接使用固定版本 SDK 的 `CryptexdService.copy_installed`、`read_personalization_identifiers`、`cryptex_nonce` 和 `install`。SDK 每次创建独立 RemoteXPC 连接，`invoke` 的 `await connection.connect()` 位于 `try/finally` 之前（依赖 `cryptexd.py:241–246`）；安装路径也一样（`:331–339`）。实际 `RemoteXPCConnection.connect()` 在底层 socket 建立后等待握手，仅捕获 `Exception`，不能捕获 `asyncio.CancelledError`。

因此阶段超时或用户取消发生在“socket 已打开、握手未完成”时，Cryptex 局部连接既没有 `close()`，也没有 `abort()`。这些连接没有挂在父 RSD 的 `service` 上，不能把 `rsd.close()` 当作逐条连接已释放的保证。后续 dial-plane/tunnel teardown 可能清理部分传输，但无法替代操作边界的明确所有权。

本次使用**真实 `CryptexdService` 和 `RemoteXPCConnection` 实现**，只替换网络读写：

```text
inventory_handshake_timeout:
  error=developer_image_mount_timeout
  close_calls=0, abort_calls=0, writer_retained=true
real_sdk_install_connect_cancel:
  cancelled=true
  close_calls=0, abort_calls=0, writer_retained=true
```

建议在项目内增加 Cryptex 连接所有权封装，在 `connect()` 之前接管每个子连接；成功、失败、超时和取消都走有界关闭，必要时只 abort 本次子连接。不要去关闭共享父 RSD/Lockdown，也不要单纯把 `service.close()` 加到外层：SDK 的 `CryptexdService.close()` 是空操作，不拥有 `invoke/install` 的局部连接。

验收：真实 SDK 上模拟 connect 握手挂起、握手异常、用户取消和阶段超时；验证对应子连接 close/abort，保留原异常，父 RSD 不被提前释放，下一次操作可重新打开。

### 2. [P2] SDK 无界 close 会阻止阶段超时和总期限返回

位置：[ddi_support.py:316](../tools/ddi_support.py#L316)、[ddi_support.py:334](../tools/ddi_support.py#L334)，以及 [usb_touch_bridge.py:2846](../tools/usb_touch_bridge.py#L2846)。

inventory 的 15 秒和 install 的 90 秒 `asyncio.timeout` 都包在 SDK 调用外；SDK 操作的 `finally` 会 `await connection.close()`。真正的 `RemoteXPCConnection.close()` 继续等待 `writer.wait_closed()`，没有自身期限。如果超时取消打断请求，之后才进入这个 `finally`，刚才那次取消已被递送，关闭等待不会自动再次收到相同超时的取消。操作会继续停在 close，阶段超时无法按期返回。

300 秒 `_preflight_developer_environment` 的总预算也存在相同边界：若它是触发取消的那个期限，请求内部随后开始的关闭等待仍可能卡住。托管层 360 秒的启动超时与进程回收是最后保护，不能算 Python 的阶段/总期限正常生效。

本次缩短期限以验证状态机，不连接网络：

```text
inventory_timeout_blocked_by_sdk_close:
  phase_timeout=0.02s
  observed=0.171s, completed=false, writer_close_calls=1
total_preflight_budget_blocked_by_sdk_close:
  total_budget=0.02s
  observed=0.16s, completed=false, writer_close_calls=1
```

观察后由复现程序第二次取消任务并显式清理，避免残留测试任务。结果证明操作跨过期限仍未完成，不是用真实 300 秒等待推测卡住。

建议给每个子连接的关闭路径设置独立期限，并保存 SDK 清理前的 writer/socket 引用，在失败或取消时 force-abort。继续完成其他拥有资源的清理后，再交还取消或原始错误。不要仅增加外层 timeout，也不要将挂起的关闭任务放到无人管理的后台。

验收：响应卡住后 close 也卡住、关闭阶段收到取消、上传途中断开、总预算先于阶段预算到期；确保在明确的“操作预算＋清理预算”内结束，随后无 pending task/子连接，原错误不被清理错误覆盖。

### 3. [P3] 所有本地阶段超时统一变成 mount_timeout

位置：[ddi_support.py:349](../tools/ddi_support.py#L349)。

同一个 `except TimeoutError` 覆盖 inventory、设备身份/nonce 读取、上传和安装后验证，最终都返回 `developer_image_mount_timeout`。实际在上传阶段等待 90 秒超时，也不会产生已有 UI 专用的 `developer_image_upload_timeout`。UI 根据已收到的桥接错误码选择提示，托管层的“根据最后阶段猜错误码”只在它自己的 360 秒超时发生时执行，不能修正 Python 已经返回的错误码。

受控复现：`last_phase=uploading_developer_image`，返回 `developer_image_mount_timeout`。建议按实际操作分别包装期限和错误码，inventory/身份/nonce 超时指向服务操作，上传超时指向上传，整体预算超时保留总准备超时。测试应从真实执行分支触发超时，再检查应用提示，不能只把错误码直接喂给 UI。

## 产物交付差异

对四份 EXE 执行 `--check-runtime`，并用 PyInstaller `CArchiveReader` 读取归档代码对象和 PYZ 模块表；只解析代码，没有执行归档内代码。

| 产物 | runtime 返回 ready | 桥接含新准备方法 | ddi_support 含 prepare_cryptex_ddi | PYZ 含 cryptexd |
|---|---|---|---|---|
| `dist/iUsbBridge.exe` | 是 | 否 | 否 | 否 |
| `outputs/iPhoneMirror/tools/iUsbBridge.exe` | 是 | 否 | 否 | 否 |
| `outputs/iPhoneMirror.Installer/tools/iUsbBridge.exe` | 是 | 否 | 否 | 否 |
| `work/issue52-fix-bridge/.../dist/iUsbBridge/iUsbBridge.exe` | 是 | 是 | 是 | 是 |

前三份 EXE 完全同哈希 `7567b404ef41639a9133518af961d5ac0cbf567fa9b3cefed2e4047b52f86ee4`；临时构建为 `050b359034b31adb6f53fbad31d9872b5f8d70184072744f995a55ff0d830801`。因此当前 `dist/outputs` 里的应用无法执行新准备流程，不只是“检查输出漏写模块名”。

这属于**现有构建未同步的交付缺口**，不能据此认定新 spec 无法打包：源码 spec 明确列出 Cryptex 模块，临时构建也验证包含。正式构建前应重新从当前源码暂存、构建桥接器并验证新能力，再生成实际应用/ZIP/Setup。旧 runtime manifest 校验只能确认旧文件与旧清单一致，不能证明源码新功能已进入成品。

## 全流程核查

| 阶段 | 已核对的行为 | 判断与边界 |
|---|---|---|
| 设备选择与信任 | 准备前校验 Lockdown UDID，建 RSD 后再次匹配；GetProhibited/NotPaired 分流为信任问题 | 未发现串设备或把信任错误标成开发者模式关闭的直接缺口 |
| 开发者模式 | 先查询；USB 未开启时只请求揭示开关，要求用户在手机开启、重启并确认 | 不自动启用/代替手机确认；无线准备也有前置检查 |
| 路线选择 | **仅 iOS 主版本 27** 走 Cryptex；26/28 等走旧 Personalized 路径 | 这是当前明确实现和测试约束，不能宣传“所有未来 iOS”均使用 Cryptex；本轮不凭推测改为 `>=27` |
| 已安装 DDI | inventory 以 `com.apple.MobileAsset.DDI` 查找，存在则复用，不下载、不 roll nonce、不卸载 | 已安装不等于 HID 就绪；后面仍有真实 HID 检查。没有自动更新旧 Cryptex 的策略，缺 HID 时应明确记录人工诊断需求 |
| 下载来源 | 固定上游提交 `6eae353...`，raw GitHub 失败回退同提交 GitHub API；不跟随重定向 | 下载有 90 秒总预算、每请求时间限制、流量上限和临时文件清理。总预算用尽后不保证还剩时间尝试备用源 |
| 文件校验 | 五个文件分别大小/SHA-256，manifest build=27A5228h，四种 payload 路径和 SHA-384 | 未发现校验放宽或用服务器自报哈希代替固定可信值。损坏缓存不按大小直接信任 |
| 资源解析 | 调用固定 pymobiledevice3 11.3.1 的 `load_cryptex1_assets` | 本轮未完成全 payload 下载，未冒称实际完整加载通过；结构与参数已审查 |
| nonce 与 TSS | 使用 build identity 的 nonce-domain **handle**，调用真实 `add_cryptex1_tags`，异步请求 ticket | handle 4 与 index 4 不是同一含义；此处使用正确 SDK API。不使用传统逐型号 BoardID/ChipID DDI 选择 |
| TSS HTTP | 40 秒总预算，两次有选择的重试，响应大小限制，失败不复制 ticket/nonce/响应正文到错误提示 | 沿用固定 SDK 的 HTTP Apple endpoint；本次没有发请求、不能宣称签名成功。设备验证 Apple ticket 是另外一层检查 |
| 安装参数 | 传 image/trustcache/ticket/info/volumehash 和 SDK 生成的 Cryptex properties | 与上游 auto_install_ddi 参数一致；SDK 原语内部的连接取消/关闭存在上文问题 |
| 安装后确认 | install 返回后再读 inventory，最多三次；无 DDI 条目明确失败 | 没有把 install ACK 单独当成功；也不能把 inventory 当触控可用的充分条件 |
| RSD 重建 | 准备 RSD、dial plane、tunnel 结束后，重新建立控制 RSD | 旧 inventory 是握手快照，重新连接方向正确；关闭子连接缺口见发现 1/2 |
| 无线恢复 | 无线缺 HID 时用匹配 UDID 的可信 USB 准备，再重建无线连接 | 保持 transport=wireless，不把 USB 控制冒充无线成功；没有静默切换输入传输 |
| HID 就绪 | HID 服务、surface、sender 探测之后才发 ready | 缺服务仍会失败，不伪造就绪。未进行真实设备 HID 输入验收 |
| 应用超时与状态 | Python 准备总预算 300 秒；应用首次就绪 360 秒；各阶段有状态码 | SDK finally 可阻塞超时；Cryptex 阶段本地超时错误码过于笼统，见发现 2/3 |
| 打包 | spec 列 Cryptex 模块，requirements 固定版本，源码暂存包含 ddi_support | 新临时成品有能力，常规 dist/outputs 尚是旧版，不能混算交付 |

## 本次验证

- **14/14 Cryptex 专项通过**，本轮重新执行。安装测试将 CryptexdService、asset loader、TSSRequest 整体 mock，说明现有测试为何没有发现真实 SDK 内部连接清理问题。
- 新增 5 个只读受控复现结果，涵盖真实 SDK 的 inventory/安装握手取消、阶段关闭阻塞、总预算关闭阻塞及上传超时错误码。复现程序放在审查输出目录，不改生产测试，也不改变源码。
- 实际下载已取得 `BuildManifest.plist`，804,946 字节及固定 SHA-256 通过，构建 `27A5228h`。用**真实 SDK** 和合成 ChipID/ECID/nonce 完成 TSS request plist 序列化，确认有 Cryptex ticket 请求、16 字节 UDID、nonce handle、无传统 Ap* 字段、不个性化 GenericDmg。没有发送这些测试身份给 Apple。
- 本轮完整文件下载在 `Image.dmg` 传输期间达到 90 秒总预算，正确失败并清理临时 `.part`；未完成本轮完整五文件校验，也未实际调用 Apple TSS。不能沿用 10 月 13 日的下载成功记录当成本次成功。
- 真实 manifest 的 `Cryptex1,Version` 为 `39.999.999.0.0,0`；它与 ProductBuildVersion 或手机 inventory 的版本字符串不是可直接等同比较的字段，未把这个差异直接判成错误。
- 四份可执行文件的运行时检查与归档内容分别核对，结果见产物表；没有运行镜像安装或设备输入。

原始日志、复现程序、JSON 结果、归档检查与源码哈希：cryptex-review-2026-10-15（本地记录：`outputs-next/cryptex-review-2026-10-15`）。

## 建议修复顺序与验收

先实现**拥有并约束每次 Cryptex 子连接的本地包装层**，同时解决 connect 取消泄漏和 close 阻塞；再细分实际阶段错误码，补调用真实 SDK 的故障注入回归。随后重新构建成品，检查能力与源码对应后，再做报告设备首次准备、已有 DDI、断网/代理失败、拔线、取消后重试、USB 准备后的无线恢复及退出验收。

本次没有足够证据证明真实 Apple ticket 和目标手机安装/HID 全链已成功；也没有发现证据要求绕过平台校验、变更 nonce 或卸载用户已有镜像。建议在上述 P2 问题处理和成品同步前，不将新 Cryptex 流程标记为完整交付。
