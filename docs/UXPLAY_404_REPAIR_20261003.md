# UxPlay 按需下载 404 排查与修复（2026-10-03）

当前状态：本地修复完成，组件已获用户确认并公开发布。修复后已实际完成公网下载、SHA-256 校验、解压、安装和断网缓存复用。真实 iPhone 投屏仍待设备验收。

公开组件：[uxplay-v1.8.4-test43](https://github.com/RayrenSX/iPhoneMirror/releases/tag/uxplay-v1.8.4-test43)。可运行的修复版位于 `work/uxplay-404/app-verified/iPhoneMirror.exe`；主程序标准包仍不捆绑 UxPlay。

## 1. Root Cause

根因是**本地构建生成了一个从未发布的 Release/Asset 地址**。

旧 `build.ps1` 的 `-TestBuild` 自动递增应用版本，并把该版本传入 `package_uxplay_component.ps1`。打包脚本生成本地 ZIP 的同时，把 `src/App/native/components/uxplay.json` 改写为同版本 GitHub Release URL。该文件被 Git 忽略，但随后嵌入应用。因此，本地测试版本 `1.8.4-test43` 在没有任何发布动作的情况下，获得了一个看似可下载的组件地址。

应用既没有获取远程组件 manifest，也没有动态解析 GitHub Release。`UxPlayComponent.ReadDescriptor()` 读取程序集内嵌 JSON，`InstallAsync()` 直接用其中 URL 构建 `ReleaseAsset`。这里不是 CDN 缓存、TLS、Header、架构别名、URL 编码或重定向问题；服务端确实没有该 Release，更没有 ZIP。

## 2. Evidence

修复前的实际安装日志与内嵌清单一致：

```text
Component: UxPlay
Version: 1.8.4-test43
Platform: Windows
Architecture: x64
Repository: RayrenSX/iPhoneMirror
Release: v1.8.4-test43
Asset: iPhoneMirror-UxPlay-v1.8.4-test43-win-x64.zip
URL: https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.8.4-test43/iPhoneMirror-UxPlay-v1.8.4-test43-win-x64.zip
HTTP: 404
Redirects: 0
Final URL: 同请求 URL
Body: Not Found
```

- `gh api repos/RayrenSX/iPhoneMirror/releases/tags/v1.8.4-test43` 返回 404。
- 所有公开 Release 中 UxPlay 独立 asset 数量为 **0**。最新公开 Release 是 `v1.8.3`，仅有 Setup、主程序 ZIP、SBOM、SHA256SUMS 四项。
- 本地 ZIP 确实存在：70,621,465 bytes，SHA-256 `1103adad2c5e57ac9210301d02fbe679d7da5171da639f0bc68bfbde31023cb2`，与旧清单一致，含 137 个文件。
- 旧日志中同一次安装先请求镜像，再请求 `github.com`；官方已返回 404，随后镜像的临时错误重试返回 530，最终错误被覆盖为“所有更新下载线路暂时不可用”。
- 旧算法不是无限循环：最多三个镜像加官方线路，每条临时故障重试一次、间隔 500 ms。另有 5 秒连通性和 12 秒测速阶段。手动点击重试会重新走相同内嵌 URL；从不刷新不存在的远程组件 manifest。

完整链路：`RestartWirelessReceiverAsync` → `ComponentDownloadWindow` → `UxPlayComponent.InstallAsync` → 内嵌 descriptor → `GitHubReleaseClient.DownloadAsync` → 镜像排序 → `SegmentedHttpDownloader` → HTTP 状态 → 下载窗口失败状态。

原始证据位于 `work/uxplay-404/`：`descriptor-before.json`、`http-before.headers`、`http-before.body`、`release-test43.json`、`releases.json`、`original-download-events.log`。

## 3. Fix

### 构建与发布

- 新增受版本控制的 `config/uxplay-component.json`；项目始终嵌入此文件，不再依赖 Git 忽略的构建残留。
- 普通与 `-TestBuild` 构建复用固定的组件版本，应用版本递增不再改写组件 URL。
- 只有显式 `-PrepareUxPlayComponent -Version ...` 才产生待发布的组件清单。该参数拒绝自动测试版号及 `-OmitUxPlayRuntime` 的组合。
- `package_release.ps1` 和 Release workflow 显式进入组件发布构建；workflow 同时明确传递 Release 版本，保证 ZIP、内嵌清单和 Release asset 对齐。
- 分发构建先执行 `verify_uxplay_publication.ps1`：核对公开 Release、精确文件名、URL、size、digest，再匿名验证实际下载入口。API 被限流时改为从公开入口读取完整 ZIP 并验证 SHA-256。待发布的事务构建和显式离线捆绑构建有各自明确路径。
- 发布清单与应用产物的绑定记录改用新 descriptor 路径，`-SkipBuild` 仍拒绝清单与已有程序不一致。

本次已发布组件沿用已验证的原 ZIP 字节，采用独立标签 `uxplay-v1.8.4-test43`。标签不会被应用版本解析器识别为主程序更新，也不匹配应用 Release workflow 的 `v*` 标签触发条件。descriptor 的可选 `release` 字段兼容原来的 `v<version>` 格式；普通应用发布仍可以把对应组件放在该应用 Release。只接受这两种与组件版本一致的标签，不能任意重定向到其他仓库。

```text
Release: uxplay-v1.8.4-test43
Asset: iPhoneMirror-UxPlay-v1.8.4-test43-win-x64.zip
URL: https://github.com/RayrenSX/iPhoneMirror/releases/download/uxplay-v1.8.4-test43/iPhoneMirror-UxPlay-v1.8.4-test43-win-x64.zip
```

该 URL 已于 2026-10-03 19:41 左右（北京时间）公开发布；匿名请求返回 206，并重定向至 `release-assets.githubusercontent.com`。现有旧应用仍嵌入旧 URL，需要使用本次修复构建。

### 下载、日志与 UI

- 验证完整本地缓存后，才用官方 URL 做有界可用性检查；明确的官方 404 立即失败，不启动镜像搜索。
- 官方网络不可达、超时或暂时故障仍允许使用原有镜像下载机制。若实际下载阶段才出现官方 404，也立即取消排队中的镜像重试，保留真实原因。
- 记录组件版本、平台、架构、Release、Asset、真实请求及响应地址、HTTP 状态、尝试次数、重试次数、下载字节、归档校验、解压、安装与就绪事件。公开下载 URL 日志去掉用户凭据、签名查询和 fragment。
- 失败窗口停止进度，显示 HTTP 状态、版本、架构、Release、Asset、URL；保留可操作的取消/重试按钮和语言切换。晚到的进度不能覆盖失败/安装状态。
- ZIP 总哈希、逐文件哈希、解压路径检查、禁止链接、原子安装和按内容哈希存储的缓存继续生效。仅改变发布位置不会让完好缓存重新下载。

## 4. Validation

| 验证项 | 结果 | 证据/范围 |
| --- | --- | --- |
| URL generation | PASS | 旧格式兼容；独立标签验证；`9.8.7-test1` 实际打包正确生成版本、名称、URL 和哈希 |
| Download | PASS（本地 TLS + 公网） | 完整 70.6 MB ZIP；公网经生产镜像选择、6 段下载完成；本地直连及真实 CONNECT 代理各 3 轮 |
| Hash verification | PASS | 总 ZIP 及 137 个文件；损坏、截断被拒绝 |
| Extraction | PASS | 完整目录、拒绝越界/链接、多进程锁、取消及清理 |
| Runtime preparation | PASS | 完整缓存识别及真实 runtime 依赖加载 |
| UxPlay startup | PASS | test44 实际用户缓存中的 UxPlayHost + UxPlay 连续启动两轮，每轮保持就绪 25 秒；运行中与停止后均通过 138 文件完整树/哈希验证；test45 已重新编译 |
| Screen mirroring | 连接与解码 PASS；视觉/听感待确认 | 用户已连接真实 iPhone；收到 500×1080 视频帧并建立 AAC-ELD 音频连接。窗口被同期测试遮挡，未确认画面外观与声音 |
| Existing-cache path | PASS | 再次安装不发 HTTP；发布位置变化仍复用同哈希缓存；同大小且时间戳不变的篡改也被修复 |
| 404 failure handling | PASS | 官方明确 404 一次即停；实际公网待发布 URL 也已复现并记录；UI 退出下载中 |
| 503/transient handling | PASS | 有界重试与恢复；官方预检无结论时仍保留下载线路回退 |
| UI | PASS | 中/繁中/英文、明暗主题、150% 字号、最小窗口、语言切换、取消、重试及关闭竞态 |
| Build | PASS | 独立候选输出目录编译成功，0 warning / 0 error |

验证文件：`build-candidate.log`、`verified-network.log`、`verified-network/logs/application.log`、`final-ui.log`、`final-ui/*.png`、`component-logic.log`、`logic.log`、`packaging-test.log`、`media.log`、`startup.log`、`public-before.log`、`public-before/logs/application.log`。

早期完整逻辑套件通过。最终重跑时，其他功能同期编辑使工作区展开动画断言失败（`Program.cs` 中 “workspace panels animate layout width”）；因此最终完整套件不能标为全绿。本次组件专项、UI、运行时与公网测试全部通过。工作区同期编辑也曾引起编译中间态/输出占用；最终采用 `work/uxplay-404/candidate` 隔离输出并成功构建。

## 5. Regression Check

- 按需下载与安装包大小：仍不把 UxPlay 捆绑到标准主程序；下载前只带 JSON 元数据。
- 缓存与运行时：复用、并发、取消、损坏修复及加载已执行验证。
- 下载传输与更新：已有分段、非 Range 回退、代理与错误恢复测试通过；独立组件标签不会成为主程序更新。
- AirPlay 媒体：真实 GStreamer H.264/AAC 管线三轮验证通过。
- 真实 iPhone AirPlay：已验证发现、连接及解码视频帧；尚未确认视觉/听感。此次未做有线与多设备验收。
- TLS、证书校验、受信任主机限制及组件哈希校验未降低。

## 6. 发布与公网验证

`work/uxplay-404/publication/` 已包含确切的 ZIP、descriptor、SHA256SUMS、发布说明及发布脚本。脚本先建立草稿并验证上传 digest，再公开为组件预发布，禁止覆盖已有资源。

用户已确认“发布组件并继续验证”。三个资源上传后逐项核对 size 与 digest，公开发布成功。GitHub 的 draft 在公开前暂用 `untagged-*` 路径，按 tag 查询会 404；已通过草稿记录核验后正常公开，最终 URL 与 descriptor 完全一致。

匿名 `verify_uxplay_publication.ps1` 通过，HTTP 206；`--component-public-uxplay` 实际公网下载 70,621,465 bytes，六段传输、总哈希及 137 文件校验、安装、禁用网络的缓存复用全部通过。实际选择线路为 `github-proxy.memory-echoes.cn`；官方预检及 GitHub 资源重定向也有单独成功证据。最新稳定版仍为 `v1.8.3`。

test43 发布证据：`public-release.json`、`publication-verification.log`、`public-install.log`、`public-install/logs/application.log`。后续实机检查发现网卡选择问题，已发布 test44；本轮多出口网卡修复发布为 test45，详见第 8 节。

当前修复版程序位于 `work/uxplay-404/app-verified/`，1152 个文件均有 SHA-256 清单。单文件 publish 曾受共享 obj 并发构建影响，内嵌程序集与测试候选不一致，因此未交付该中间产物；当前交付目录直接使用已验证的 `candidate-final` 程序集及对应依赖，补齐 `hostfxr.dll`、`hostpolicy.dll` 和 deps.json 所需的自包含 .NET 原生运行库，并实际启动成功。最终程序集 SHA-256 为 `061a5c47bf970328828323cf8ec33b892888c394aefa7a9f771fbff3c38c8851`，此前固定组件为 test44；最新网卡修复组件为 test45。

## 7. 持续启动发现的缓存失效及修复

首次在实际用户缓存启动接收器后发现：原生 UxPlayHost 把 `GST_REGISTRY` 指向组件根目录的 `gst-registry.bin`。GStreamer 在首次真实启动时写入 427,186 字节的索引，使严格文件树校验发现未列入 manifest 的文件；随后 UI 把组件判为不可用。此前短时原生启动 smoke 在该写入前结束，没有覆盖这个问题。

`GST_REGISTRY_UPDATE=no` 经实测不足以解决首次创建：GStreamer 仅在成功读取已有索引时检查此变量。最终修复位于 `WirelessReceiverService.ProbeRuntime`：UxPlay 通过完整性检查后，为预检和原生接收器继承的进程环境设置优先级更高的 `GST_REGISTRY_1_0`，将索引存入 `%LOCALAPPDATA%/iPhoneMirror/Cache/GStreamer/`，以运行时绝对目录的 SHA-256 区分索引。组件目录仍严格禁止额外文件，不增加白名单，也不关闭任何哈希校验。组件 ZIP 字节未变，无需替换已发布资源。

新增显式集成测试入口 `--component-uxplay-lifecycle <output>`，使用真实已安装组件和生产 `WirelessReceiverController` / `NativeCore`，两轮分别验证：启动成功、持续运行 25 秒、接收器就绪、索引实际写在组件目录外、运行中全部 137 文件哈希通过、停止后完整哈希仍通过。测试日志为 `work/uxplay-404/lifecycle-fixed.log`，原生证据为 `lifecycle-fixed/capture.log`；两轮通过，原生日志无 warning/error。最终构建 `build-fixed.log` 为 0 warning / 0 error。

2026-10-03 20:19（北京时间）已启动最终交付目录，App PID 50512，UxPlayHost PID 44224。应用日志确认 `backend=UxPlay applied_backend=UxPlay available=True running=True ready=True`，实际接收器名称为 `iPhoneMirror-AirPlay-UxPlay-44224`。已请用户在 iPhone/iPad 发起屏幕镜像，实机画面及音频结果待确认。此次启动中的 GitHub 主程序自动更新检查遇到 API 403 限流，未影响已验证组件缓存或 UxPlay 就绪。

## 8. 实机发现失败、网卡修复与 test45

用户反馈在同一路由器下只能看到不带 UxPlay 后缀的 `iPhoneMirror AirPlay`。本机 DNS-SD 查询确认这些记录指向旧接收端的 7001 端口，当前 UxPlay 的名称未出现在 Wi-Fi 发现结果中。

对运行中进程的只读调试确认：UxPlay 内置 mdnsd 已进入事件循环，实际选择的 IPv4 地址属于 Hyper-V Default Switch，手机则连接 WLAN。上游 `mdns_get_default_ipv4()` 用到组播地址的路由选择本地地址，在此机器选中了隔离的虚拟交换机。此前“可能卡住”的初步推测由线程栈排除；问题是广播接口选择，不能仅凭 native Ready 消息把局域网发现标为通过。

新增 `scripts/patches/uxplay-windows-mdns-interface.patch`：Windows 枚举活动的 IPv4 网卡，过滤隧道、虚拟和不支持组播的接口，优先选择物理以太网/Wi-Fi 及有效网关地址；枚举失败时保留原有回退。`prepare_uxplay.ps1` 对固定 upstream commit 幂等应用该补丁，检查失败即停止，组件内附补丁及来源说明。原/新代码在同机独立编译的地址选择探针分别返回虚拟网卡/WLAN 地址；修复进程对从 WLAN 发出的实际 mDNS 请求返回正确的 UxPlay 名称、IP 和 7000 端口。

20:34 左右用户已连接本地诊断候选，日志确认 iPhone13,1 建立 AirPlay 会话，`wireless_video published size=500x1080 stride=500 bytes=810000`，AAC-ELD 44100/2 音频连接已启动。用户表示其他对话的测试窗口遮挡画面，无法确认视觉效果；应用同时停留在首次设备引导弹窗。Windows Computer Use 工具初始化报 `failed to write kernel assets ... os error 3`，因此没有声称截图/外观验收通过。未把音频连接建立等同于实际听感通过。

本地诊断候选曾通过显式 runtime override 定位网卡问题；最终交付已移除该 override，使用公开下载并完整校验后的标准组件缓存。

新的独立组件预发布：<https://github.com/RayrenSX/iPhoneMirror/releases/tag/uxplay-v1.8.4-test45>。

- 文件：`iPhoneMirror-UxPlay-v1.8.4-test45-win-x64.zip`
- 大小：70,624,550 bytes；138 文件（含源码补丁）。
- SHA-256：`2818f767df228c660582c800e2c2ca17178fec36730a309fafd00454a859fa8b`。
- 三个上传资源逐项通过 size/digest 校验后公开；test43 文件未覆盖。
- 匿名发布检查 HTTP 206，实际重定向到 GitHub release assets。
- 生产下载器通过 `github.ednovas.xyz` 六段公网下载；总 ZIP、逐文件校验、解压安装、禁用网络缓存复用通过。
- test45 重新编译的 UxPlay 通过 MSYS2 UCRT64 构建；当前接口组合的选择策略优先 WLAN，并过滤 Hyper-V、VPN、隧道和无组播接口。公开 Release 的匿名 HTTP 206、大小和摘要校验通过。
- 最终 .NET 构建 0 warning / 0 error；运行时回归 8 项通过，包括真实 HLS/音频三次重启、越界/链接/多余文件拒绝、warm preflight 损坏检测。

证据：`publication-fixed/`、`publication-fixed.log`、`publication-final-verification.log`、`public-final-install.log`、`public-final-application.log`、`lifecycle-final.log`、`lifecycle-final/capture.log`、`mdns-public-runtime.log`、`mdns-interface-query.log`、`mdns-fixed-inspect.log`、`device-connection-fixed.log`、`runtime-final.log`、`build-final.log`。

20:46 已打开最终交付程序，App PID 29128，UxPlayHost PID 23204；命令行确认运行时来自 test44 的内容哈希缓存目录，未使用诊断 override。接收器名称 `iPhoneMirror-AirPlay-UxPlay-23204`，应用保持运行供用户后续视觉/听感确认。稳定应用 Release 再次查询仍为 `v1.8.3`。test45 已作为新的独立组件预发布，等待应用重新构建后使用其 descriptor。
