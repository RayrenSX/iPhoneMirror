# 无缓存真机、错误回退与复审记录

日期：2026-10-07。设备：iPhone 12 mini / iPhone13,1，iOS 18.7.8（22H352）。
初始开发者模式开启，Personalized DDI 未挂载，无其他桥接进程占用设备。

## 干净环境的实际范围

批量递归删除 DDI 目录被自动审批以“blocked by policy”拒绝，因此使用可恢复隔离。
用户缓存 `C:/Users/Ray/.pymobiledevice3/Xcode_iOS_DDI_Personalized`、源码随包镜像和
实际测试发布目录的随包镜像，移至 `work/device-clean-review/quarantine/`，原查找路径不存在。
详情见 `isolation.json`。没有删除信任/配对记录，也没有抹除手机数据。

其他历史构建的镜像仍保留，但测试不运行那些构建，不设置 `--ddi-dir` 或镜像环境覆盖，
也不会扫描它们。新发布目录没有随包 DDI。最终打包测试前再次隔离新生成的用户缓存，
并在手机卸载 Personalized DDI，确认 `mounted=false`、用户缓存路径不存在。
测试成功后留下本轮实际下载、完整校验的新缓存，手机保留已成功挂载的 DDI。

电脑无缓存不等于手机无签名。第一次挂载复用了手机保存的 personalization ticket；
另设测试绕过该 ticket，首个 TSS 请求注入 503，第二次真实请求 Apple 成功返回 200，
完成签名、上传、挂载和 HID 应答。没有伪造成功签名或 ready。

## 真机暴露的新问题及修复

第一次无缓存启动实际失败：GitHub Raw 不通，API 成功获取清单，后续来源拖到下载总期限
150 秒，返回 `developer_image_download_timeout`。证据：`cold-usb.log`。

审查发现此前单来源使用整个剩余下载期限，而且 `iter_content(128 KiB)` 可能等待填满
应用层缓冲后才返回；持续慢速传输不会触发 socket 空闲超时，取消/期限检查不能及时运行。

修复如下：

- 每个下载来源设置 45 秒尝试预算，并保留总计 150 秒期限，让后续来源仍有机会。
- 使用 `read1` 处理已到达的部分数据，读取前后检查取消和期限；保持长度、Git blob、SHA-256 校验。
- 中继数据和镜像测速度量使用同一读取方式，探测也检查总期限，避免探测线程被慢速响应长期拖住。
- 记录来源、文件、已收字节数和耗时，便于定位到底卡在哪个来源。

修复后第二次无缓存启动，API 大文件尝试约 45 秒后退出，镜像源约 3.23 秒取得
15,733,248 字节镜像，约 87.17 秒进入 ready，6 次真实输入应答，无错误。
最终打包后的另一轮冷启动更快，约 45.48 秒 ready；耗时受当前网络影响。

## 回退矩阵

“注入”均限定在独立测试进程；其余步骤使用真实手机。注入超时缩短了测试等待期限，
不声称手机或 Apple 服务实际发生这些故障。

| 场景 | 结果与证据 |
|---|---|
| 原实现无缓存下载 | 真实网络 150 秒超时，未 ready；`cold-usb.json` |
| 修复后无缓存下载、上传、挂载 | ready 87.17 秒，6 次 HID 应答；`cold-usb-fixed.json` |
| 新 Apple 签名，第一次 503 注入 | 第二次真实 TSS HTTP 200，挂载后 ready 8.06 秒；`fresh-tss-retry-valid.log` |
| 签名超时注入 | `developer_image_tss_timeout`，无 ready，正常退出 |
| 上传超时注入 | `developer_image_upload_timeout`，无 ready，正常退出 |
| 激活挂载超时注入 | 真实上传完成后 `developer_image_mount_timeout`，无 ready |
| 全下载源失败注入 | `developer_image_download_failed`，无 ready，正常退出 |
| 下载中取消 | 约 0.23 秒停止，无误报 ready 或强制 kill；无已发布缓存 |
| 无效本地镜像 | 跳过损坏包，用本轮已校验缓存挂载；3 次 HID 应答 |
| 无缓存，禁用 direct/proxy，仅镜像 | 三文件真实下载并挂载，ready 31.41 秒，3 次 HID 应答 |
| 媒体认证不可用注入 | direct HID 成功，ready 3.16 秒，3 次应答 |
| Modern HID 不可用注入 | 继续尝试 Legacy；本机 Legacy 返回 RST_STREAM，明确失败，无 ready |
| 所有 HID 不可用注入 | 挂载刷新及受限重试后约 50 秒报 `touch_surface_unavailable`，正常退出 |
| 错误 RSD 身份注入 | `device_identity_mismatch`，未开启输入，无 ready |
| 关闭真实 USB HID socket，再关闭真实 CoreDevice socket | 两次恢复完成，generation 1→2→3，21 次输入应答；`usb-recovery.json` |
| 无线尚未连接适当网络 | `wireless_device_not_discoverable`，没有静默转 USB |
| 用户将无线准备就绪 | Network usbmux 无线真实连接及输入成功 |
| 强制 Network 入口不可用 | 进入 RemotePairing 探索；当前网络无该服务，明确报不可发现，无 ready |
| 关闭真实无线 HID socket | 进入恢复、报告 `apple_connection_lost` 并退出；再次启动无线成功，4 次应答 |

Legacy 虽出现在这台手机的 RSD 服务列表中，但实测不能完成该备用服务连接。
RemotePairing 无线入口实测没有发现服务；本次成功的无线传输是 Network usbmux。
这两项不能记为“备用路径成功”，模拟测试只能证明对应分支选择、清理和重试逻辑。

前两个 `fresh-tss-retry*.json` 失败由测试脚本的属性/异常引用错误引起，已修正；
有效的 TSS 结论仅来自 `fresh-tss-retry-valid`，原始日志保留以便追溯。

## 最终主程序宿主验证与复审

新增显式启用的 `--device-control-live <bridge> <udid> <usb|wireless>` 测试入口，
使用真实 `UsbTouchBridgeHost`、发布后的桥接器和手机，执行两次启动、每次三份空键盘报告、
逐次等待 `input_verified`、停止并再次启动。没有绕过设备身份、传输、gate、会话编号校验。

| 宿主 / 传输 | 首次 ready | 再次 ready | 停止 | 结果 |
|---|---:|---:|---:|---|
| C# 宿主 + 最终打包 USB | 45.48 秒（无缓存、未挂载） | 3.76 秒 | 0.32 / 0.36 秒 | 两轮均三次应答 |
| C# 宿主 + 最终打包无线 | 3.94 秒 | 7.88 秒 | 0.22 / 0.42 秒 | 两轮均三次应答 |

复审回查了下载来源预算、缓存发布与取消、DDI 阶段错误传播、媒体/HID 候选清理、
RSD 身份、主程序 ready 校验、旧会话输入隔离、USB 内部恢复和无线退出后的再次连接。
没有从本轮通过的检查中发现新的待修复问题；这不代表所有网络和设备条件下零错误。

自动验证：Python 全套 286 项通过；新增本地真实 HTTP 慢速响应测试覆盖镜像文件、
中继数据、探测的逐字节传输期限；C# 66 个错误映射案例通过；进程生命周期、多设备键盘焦点、
输入队列隔离和自动化 API 回归通过。故障注入日志中的 timeout、断开和清理 warning 属预期结果。

所有真机输入使用释放报告和同一 HID 连接上的应答验证，没有实际打字、打开应用或更改手机内容。
没有目视核验触点效果，没有物理拔插、Wi-Fi 漫游、真实 Apple 拒签或长时间压力测试。
无线断开后的真实重连在桥接/宿主层验证；主界面自动恢复的状态路由由模拟回归覆盖。

## 交付

完整目录：`artifacts/iPhoneMirror-device-tested/`，启动 `iPhoneMirror.exe`，需要保留整个目录。
该目录不携带 DDI，使用本轮验证的下载/缓存准备流程。

桥接器通过冻结运行时自检、C# 完整性验证、PowerShell 清单逐文件验证；暂存源码与当前桥接源码一致。
构建与发布 SHA-256 一致：`FC3939DD60FD73B9942B56D7811A3E33C96F149DE0662E0A9CE3F09BC0A14C62`。

证据根目录：`work/device-clean-review/`；最终宿主记录为 `host-usb-live.log` 和
`host-wireless-live.log`。旧镜像隔离目录保留，不自动恢复到活动查找路径。
