# 有线/无线全链路再审查

日期：2026-10-08。接续 [用户环境审查](CONTROL_ENVIRONMENT_REVIEW.md) 与 [干净环境真机审查](CONTROL_DEVICE_CLEAN_REVIEW.md)。

## 本轮确认的问题

### HID 启动连接重置未尝试其他服务

Modern HID 打开或查询服务时，SDK 可能抛出 `StreamClosedError`（包括 RST_STREAM），此前不属于候选服务失败类型，会在 Legacy 尝试前退出。清单返回成功后连接被重置的情况也没有在选择候选服务时检测。

现在候选服务的打开、服务清单和同一连接的 PING 应答都完成后才选用。服务连接关闭、重置或超时可以继续尝试另一 HID 服务；失败候选会关闭并恢复键盘 surface 状态。媒体认证路径中的服务连接重置也可进入显式 HID 候选链。底层 CoreDevice/mux 已失效时直接交由传输恢复，不反复打开同一坏隧道内的服务；取消仍直接退出。最终 ready 仍经过发送释放报告与同连接应答校验。

新增用例在修复前实际复现打开、清单、PING 三处失败，见 `work/control-chain-reaudit/before-hid.log`。测试使用可工作的 Legacy 替身验证回退逻辑，不表示本机 iPhone 的 Legacy 服务可用。

### Windows 并发缓存索引发布偶发拒绝访问

本轮第一次全量回归实际遇到：两个会话并发发布完整 DDI 快照时，`os.replace` 替换 `.iphoneMirror-ddi-current.json` 偶发 WinError 5。文件读者/另一个发布者短暂占用时，原代码立即退出并报缓存失败。

现在仅对 Windows 错误 5/32/33 做最多六次尝试，累计退避不超过 0.75 秒；每次尝试和等待前检查取消，下载总期限也覆盖发布。始终使用原子替换，不删除旧索引或已被挂载的快照。持续占用/权限问题仍报告 `developer_image_cache_failed`，复用既有空间、权限和安全软件提示。

覆盖一次占用后成功、等待期间取消保留旧选择、持续拒绝访问有限结束；并行真实文件发布重复 40 轮通过。第一次失败日志后被最终全量日志覆盖，稳定故障注入记录见 `before-cache.log`，重复验证见 `cache-concurrency.log`。该注入日志还有一个已修正的测试参数名错误，不作为产品故障证据。

## 审查范围

| 阶段 | 核对内容 |
| --- | --- |
| 发现与身份 | USB、Network usbmux、RemotePairing 选择；Lockdown/RSD 设备身份核对；无线不自动换成有线 |
| DDI | 开发者模式、挂载查询、本地/缓存/网络来源、校验、签名、上传、挂载、刷新、拒绝记录、取消与总期限 |
| 输入初始化 | 媒体认证、Modern/Legacy HID、触控/键盘 surface、连接应答、ready 门控 |
| 输入与恢复 | 输入代次、过期输入丢弃、HID 健康检查、USB 内部重建、无线主进程重连、剪贴簿任务取消 |
| 进程与退出 | 启动/停止互斥、输入 EOF、旧进程事件隔离、释放按键/触点、关闭部分启动资源 |

本轮没有重置手机信任记录、清除 DDI 缓存或重新执行所有 Apple 签名注入；相关证据保留在上一轮报告。全链路代码审查与已有回归覆盖，不等于每个真实用户环境都被实测。

## 本轮结果与发布文件

证据目录：`work/control-chain-reaudit/`。

- Python 最终全量 **302 项通过**（`python-full.log`）。缓存真实并发发布另做 40 轮，全部通过。
- C# Runtime.Tests 构建 0 警告、0 错误；启动/停止/重启、取消和旧退出事件隔离通过。USB/无线键盘焦点、排队输入、按键释放及双有线/有线无线混合设备隔离测试通过；这些输入路由测试捕获内存数据，不向手机发送按键。
- **66 项**错误映射及诊断保留检查通过；新发布桥接器通过应用端完整性检查和运行时功能自检。
- 源码真机 USB：关闭实际 HID socket 和 CoreDevice socket 后分别恢复，输入代次 1→2→3，25 次实际应答，0 终止错误，无强制退出（`source-usb-recovery.json`）。
- 新发布文件经 C# 主进程真机 USB 两轮启动：3.64 / 3.98 秒；每轮 3 次实际 HID 应答；两次停止均 0.34 秒（`live-usb.log`）。
- 无线真机本轮当前在发现阶段失败：Network usbmux 连接重试后进入 RemotePairing，19.3 秒返回 `wireless_device_not_discoverable`，没有虚报 ready，也没有退到 USB。尚未执行计划的无线 HID 断线注入（`source-wireless-disconnect.json` 中 ready=0）。已请求用户解锁、保持亮屏并确认同网；不能将上一轮无线成功计为本轮验证通过。

独立发布目录：`artifacts/iPhoneMirror-control-reaudited/`，保留完整目录运行 `iPhoneMirror.exe`。
桥接器 SHA-256：`D651D19182A310B80CA7BB6C9F3A300F0A7170CC31C7D4FADD220B9A5854FBEA`。
发布桥接器与构建产物一致，三个桥接源文件与隔离构建源文件哈希一致。

## 尚未完成的真机覆盖

RemotePairing 真机成功路径仍受当前网络发现条件限制；本轮不将 Network usbmux 无线成功当成 RemotePairing 成功。当前 iPhone 的 Legacy HID 上一轮实际返回 RST_STREAM，本轮仅验证遇到此类失败后的控制流与有上限退出。物理拔线、Wi-Fi 漫游、长时间连续触控和其他 iOS/机型没有穷举验证。
