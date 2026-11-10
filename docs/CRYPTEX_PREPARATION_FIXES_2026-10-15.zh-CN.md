# Cryptex 准备修复与复审（2026-10-15）

> 文档整理说明（2026-11-05）：文中构建产物、日志和截图的本地记录路径不随仓库分发，保留用于追溯；历史测试结论未在本次重新执行。

依据：[修复前全流程审查](CRYPTEX_PREPARATION_REVIEW_2026-10-15.zh-CN.md)。

**审查中已确认的问题已修复；最终复审在本次源码、自动测试和本地桥接产物范围内未再发现可确认缺陷。** 这不是对所有设备、网络及未来系统版本的无错误保证。真实 Apple 签名、目标手机安装及最终 ZIP/Setup 的实机验收仍未完成。

本轮仅修改 #52 的 Cryptex 准备、清理、运行时验证、构建与对应测试。保留此前 #55 和 #56；没有重新加入已撤回的 #54 D 模式或 #59 媒体 UUID 修改，没有更改设备 nonce、卸载已有 DDI 或操作手机。

## 已修复

| 修复前问题 | 当前实现 | 验证依据 |
|---|---|---|
| 握手取消时 Cryptex 子连接未释放 | 新增本地 `_CryptexConnection` / `_CryptexRsd`，在连接开始前接管每次子连接，失败与取消也进入有界清理 | 使用真实 SDK 的连接/安装握手取消、真实 socket 释放和取消后重试 |
| SDK 无界关闭导致阶段/总期限失效 | 每个子连接关闭有独立期限；超时或取消时 abort 本次 writer；先接管文件读取任务，防止 SDK 吞掉取消 | 请求和关闭同时卡住、总预算先到、文件读取任务清理、关闭期间再取消，无遗留测试任务 |
| 超时统一被报告为 mount_timeout | inventory/身份/nonce 返回 service_timeout，上传返回 upload_timeout，下载总预算返回 download_timeout，整体准备预算保留 prepare_timeout | 从实际 SDK 操作路径触发错误，应用已有 80 项提示/诊断路由验证通过 |
| 准备 RSD 关闭超时仅被忽略 | 准备阶段使用现有远程服务清理入口，保留底层 writer，在异常关闭后 abort | 真实 RSD 对象配合卡住的传输关闭测试 |
| 现有 dist/应用输出仍是旧桥接器 | 从当前源码重新暂存和打包，同步 dist、应用输出及安装器载荷目录 | 归档模块/函数检查、全部 manifest 文件哈希、运行时功能检查与应用侧完整性验证 |
| runtime ready 不能证明新 Cryptex 生命周期能力存在 | 新增 `cryptex_owned_channels` 离线功能探测；构建脚本明确要求该能力及 Cryptex 模块 | 成品运行真实 SDK inventory 解析和子连接清理，不开 socket、不访问设备 |

对应源码：[ddi_support.py](../tools/ddi_support.py)、[usb_touch_bridge.py](../tools/usb_touch_bridge.py)、[bridge_runtime_check.py](../tools/bridge_runtime_check.py)、[桥接构建配方](../scripts/usb-bridge-recipe/build.ps1)。

包装层只改变**本次子连接实例**的生命周期，不修改第三方依赖文件或全局 SDK 类。Apple 请求、nonce-domain handle、TSS tags、五段文件传输与 XPC 类型仍使用固定版本 SDK。父 RSD/Lockdown 由原有上层作用域管理，不被单次 Cryptex 调用意外关闭。

## 复审过程

1. 第一轮修复连接所有权、阶段错误码，补调用真实 SDK 的回归，验证此前两类泄漏/超时路径可结束。
2. 第二轮发现并处理 SDK 在关闭文件读取任务时抑制取消的细节；补实际文件读取任务、关闭期间取消、真实 socket 和重新连接测试。同时将准备 RSD 纳入已有有界清理入口。
3. 对设备身份、开发者模式、iOS 27 路线选择、已安装镜像复用、固定资源校验、TSS 构造、安装 ACK 后 inventory、准备后的 RSD 重建、无线恢复和最终 HID 就绪重新沿调用路径核对。
4. 构建真实桥接器，检查当前源码与构建暂存内容一致；检查成品归档中的新实现与 Cryptex 模块、运行时能力和完整 payload 哈希；更新本地应用输出。
5. 最终重新执行完整 Python 套件、应用侧 DDI 诊断和成品完整性验证、源码暂存/打包检查。当前未留下本轮已确认但未修复的源码发现。

未将“镜像已安装”当作 HID 就绪，也未因旧镜像缺服务而自动卸载或滚动 nonce。iOS 主版本 27 以外仍沿用原路线，没有擅自扩展未来系统兼容承诺。

## 最终验证

| 检查 | 结果 |
|---|---|
| 全量 Python unittest | **442 项通过**，25.213 秒 |
| Cryptex 专项 | **31 项通过**：16 项原流程/下载测试和 15 项真实 SDK 生命周期测试 |
| SDK 成功链路 | inventory → 真实身份/nonce 参数 → 真实 TSS request 构造 → 安装 5 段 payload → inventory；替换网络与签名响应，不调用设备 |
| 取消与期限 | 部分 connect、上传、文件读取清理、关闭期间取消、阶段期限、整体期限、取消后重试通过；测试检查未遗留任务 |
| 真实 socket | 部分握手被取消后 socket 关闭，SDK writer 清空 |
| 下载回归 | 固定 revision、坏缓存重取、HTTP 源回退、大小/哈希、manifest digest、取消及总期限后的 partial 清理通过 |
| PyInstaller 桥接构建 | 成功，成品要求 `cryptex_owned_channels` |
| dist / 应用 / 安装器载荷运行时 | 三处均包含新模块与生命周期实现，全部 manifest 文件校验通过，运行时功能检查通过 |
| 应用侧 DDI 专项 | 三处载荷分别通过 80 项错误/提示用例、开发者模式用户操作状态及实际运行时完整性检查 |
| 源码暂存/原生依赖/manifest/打包参数检查 | 通过 |
| 差异与新文件格式检查 | 通过 |

新回归：[cryptex_lifecycle_test.py](../tests/cryptex_lifecycle_test.py)、[cryptex_ddi_test.py](../tests/cryptex_ddi_test.py)、[运行时能力测试](../tests/bridge_runtime_check_test.py)。测试使用假的网络读写承载**真实 CryptexdService 和 RemoteXPCConnection**，没有继续将整个服务 mock 后据此推断底层清理正确。

第一轮全量运行暴露测试辅助函数把协程作为普通 mock 返回值，修正为异步回调后重跑通过，未修改产品行为或放宽断言绕过失败。日志中有意注入的超时和断连 warning 保留在原始记录中，不当成真实设备故障。

## 本地输出与验证边界

已更新并分别校验：

- `dist/iUsbBridge.exe`、对应 `_internal` 和 `iUsbBridge.runtime.json`；
- `outputs/iPhoneMirror/tools/` 中同一套桥接载荷；
- `outputs/iPhoneMirror.Installer/tools/` 中同一套桥接载荷。

三个 EXE 的哈希和具体 manifest 校验结果见 artifact-results.json（本地记录：`outputs-next/cryptex-fixes-2026-10-15/artifact-results.json`）。两份应用输出的旧桥接载荷保存在本轮输出目录 `previous-runtime/`，可核查或恢复。**更新安装器载荷目录不等于生成了新的 Setup 安装 EXE。** 本轮没有发布 GitHub Release，也没有制作/验收最终 ZIP/Setup。

本轮另外尝试对真实固定资源执行 GitHub API 回退下载，当前网络以 `ReadError` 失败；未将这个外部网络失败伪报为下载成功。固定资源完整性的自动回归通过，但本轮没有完成真实五文件下载、Apple ticket 请求或手机安装，仍需在可访问来源和目标设备上验收。

完整日志、构建暂存路径、载荷备份及校验结果：cryptex-fixes-2026-10-15（本地记录：`outputs-next/cryptex-fixes-2026-10-15`）。
