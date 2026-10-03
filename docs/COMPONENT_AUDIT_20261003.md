# 组件与 UxPlay 下载审查（2026-10-03）

## 最终软件验收续记

按用户本轮选择，仅验收软件和安装包，不操作真实 iPhone 或更换系统驱动。
本节是最新结果；下方各轮保留为过程证据，不应把早期失败、旧包大小或已撤回的
精简 FFmpeg 结论当作当前状态。

### 最终源码与发布目录

- 最终构建、回归、安装日志及清单位于 `work/component-review3/delivery`。
  484 项源码/配方/描述文件输入在双模式 publish、测试构建及安装验证期间保持一致。
  `source-before.json` / `source-after.json` 可复核；一次并行焦点守卫修改触发的
  构建拒绝记录保留在 `source-interrupted-before.json` / `source-interrupted-after.json`，
  该中间构建没有推广到发布目录。
- 已协调纳入最新键盘映射的蓝牙/其他设备键盘转发保护、焦点缓存失效和配置恢复修复。
  对方确认源码稳定后，重新核对没有未纳入的源码变化。
- 普通完整构建产生的 native、桥接器、驱动与资源均保留。最终 app-only publish 的文件集
  与发布目录比较，仅差独立构建的驱动 EXE；所有共同文件仅便携版 `iPhoneMirror.exe`
  和安装版 `iPhoneMirror.dll` 改变，已通过测试后推广。
- 最终 `payload-inventory.json`：单文件 bundle 256 项，安装版无缺项；仅 deps 部署元数据不同，
  255 项内容哈希相同。安装版 195 个 native PE、249 个 managed PE，UxPlay 135 个 native PE，
  无未解析的直接导入及意外架构。三个 Wintun 备用架构已明确分类，不伪装成 x64 DLL。
  审计工具现在对缺目录、空目录、缺失导入、意外架构或非预期 bundle 差异返回失败。

### 已生成的验收产物

目录：`work/component-review3/delivery/release`，版本仍为 `1.8.4-test4`。
不是新的线上版本，历史 `outputs/releases` 没有覆盖。Setup 超过 100 MB 是有意接受的结果。

| 产物 | Bytes | SHA-256 |
| --- | ---: | --- |
| `iPhoneMirror-Setup-v1.8.4-test4-x64.exe` | 121,996,115 | `a45a0891ed4aa9127185f55625cfae69792ff74e5ea483f1238f69929d4b69dc` |
| `iPhoneMirror-v1.8.4-test4-win-x64.zip` | 215,730,365 | `d1fa3254f78ace3321f1fb62e905536870732c557503ca4651a5ff6107806c84` |
| `iPhoneMirror-UxPlay-v1.8.4-test4-win-x64.zip` | 70,621,463 | `34e7600555781bbae1f96f3af5b7638011063c7ea7fa3d298c73a4782382a8c3` |

正式压缩打包成功，最终三个文件与 `SHA256SUMS.txt` 全部一致。
直接读取便携 ZIP 中的 **881 个文件** 并逐项与发布目录 SHA-256 比较，全部一致，
见 `archive-verification.log`。打包完成后再次检查 484 项输入，没有后续源码变化。
最终主程序集 SHA-256 为 `56b7f744d1e9cb34c755cf0086cab81d1c8e93470a004dff66c3c6dade85be01`；
便携 EXE 为 `16b5ec79525f08076d6254431f6abd20f5b08b53d337ef2edc8629d17d74f1ab`。

### 最终功能与安装验证

- 最终生产程序集完整重跑：组件运行时、18 格式 HTTP 音频、媒体/HLS、采集、虚拟摄像头、
  更新提权、剪贴板、USB/无线/蓝牙快捷键、多设备输入、交互、键盘映射、下载 UI 和本地化，均通过。
  本地化为 **19,920** 个格式案例、4 次字典切换、12 个控件流程；主逻辑测试也通过。
  快捷键/映射的 USB 和无线数据包捕获在内存中，没有向手机发送操作。
- 下载 UI 追加真实失败状态下的三语言/双主题/150% 字号检查，验证“取消”和“重试”都可操作，
  不只验证取消按钮。最小窗口的英文大字体需要共享外层滚动区，按钮经滚动可达；
  `component-ui/*large-text-actions*.png` 保存滚到底部的证据，未宣称全部内容始终一屏可见。
- 对最终发布文件执行隔离安装、运行中升级、重装、保留/删除测试用户数据后的卸载。
  **全部 1,152 个安装文件逐项 SHA-256 与发布目录一致**，不只检查启动路径的少量 DLL。
  把四个测试驱动文件临时放入安装目录后，使用安装出来的生产程序集/运行时执行
  组件预检与真实 HLS 三轮、18 格式音频及快速启停测试，全部通过。
  最后移除测试驱动文件并完成卸载；隔离安装目录/测试数据已清理，日志保留。
- 正式 `lzma2/ultra64` 压缩的安装生命周期与功能测试已在上一最终快照完整跑过；
  本次最新 app 文件使用相同安装脚本的无压缩测试变体复核，便于区分压缩验证与最新源码验证。
  测试安装器使用独立 AppId/路径，不替换用户现有安装。
  “旧版本升级”是同一 payload 标注旧版本号，不等于历史旧代码迁移验收。
- Native 14/14、Python 155 项、驱动测试、AirPlay H.264/BGRA/ALAC/PCM 字节对照沿用本轮
  未再改变的 native/bridge 产物验证。UxPlay 完整 GStreamer H.264/AAC 三轮另行重跑通过，
  成功证据为 `work/component-review3/uxplay-media-verified.log`；原 `uxplay-media.log`
  记录的是测试程序编译参数尚未修正时的失败，不应用作成功证据。

### 下载验证与发布前提

最终下载器源码和描述文件未再改变。整合复测日志 `work/component-review3/integrated/network.log`：
完整 70,621,463-byte 组件无应用代理 3 轮（5.50 / 5.29 / 4.66 秒），真实 CONNECT 代理
3 轮（4.51 / 4.26 / 4.05 秒），每轮下载、解压、全树校验；取消/重试、安装取消、损坏、截断、
并发、缓存修复和单流回退通过。404/503 仍分别为 4/8 次有界尝试。
此处是本地 TLS 故障注入，不是公网资产已发布的证明。外网镜像对照见下方第一轮实测。

2026-10-03 13:17（UTC+8）再次通过显式代理请求应用内 UxPlay 发布 URL，仍为 **HTTP 404**，
证据 `work/component-review3/uxplay-public-final-head.log`。必须在发布时上传哈希匹配的 ZIP；
镜像不能补出尚未发布的源资产。本轮没有上传或修改 GitHub Release。

### 可以保留与不能宣称的结论

- 保留：AirPlay 专用 FFmpeg ABI/编解码裁剪、Python 桥接器未用上游功能剔除、驱动未用 WinRT
  投影移除、安装版共享 .NET 布局和固实压缩。这些结论限于应用实际使用的功能，
  不承诺桥接器提供完整 pymobiledevice3 CLI。
- 撤回：媒体源 FFmpeg 白名单裁剪；恢复完整 101,897,728-byte EXE，以兼容性优先。
- UxPlay 分离下载可以保留，但以同时发布匹配 ZIP 为前提；本机完整组件软件功能通过，
  当前公开地址首次下载不可用，不应标为已上线可用。
- 未验收：真实投屏/声音/反控、重连/DDI、系统驱动更换与回滚、其他 Windows/GPU、外部推流端点。
  并行映射聊天的使用文档还记录了关闭 USB 投屏后偶尔需重插数据线的真机观察；
  本聊天未复现或解决这一硬件生命周期问题，不能被软件回归“通过”覆盖。

## 第三轮：精简兼容性与发布目录审查

本轮发现并撤销了确实破坏功能的 FFmpeg 裁剪；100 MB 不作为功能取舍条件。
新构建及测试位于 `work/component-review3`，历史 `outputs/releases` 未被覆盖，也没有上传版本。
以下记录取代早期“精简 FFmpeg 可以保留”的结论。

### 已复现问题与修复

1. **媒体音频解码回归（由精简引入）。** 用原版 FFmpeg 生成 18 种音频样本，
   经本地 HTTP 和生产 `MediaCastAudioDecoder.BuildArguments` 解码，并与原版 PCM 对比。
   原版全部成功；精简版有 9 项失败：PCM s24le/s32le/u8/f64le、s24be AIFF、
   IMA ADPCM WAV、A-law WAV、WMA、MP2。错误包含 `no decoder found` 和 `Invalid data`。
   原因是解码器/解复用白名单误以为源音频仅限于常见 AAC 等格式。
   默认 build/package 已恢复固定哈希的 essentials 版本；显式自定义 manifest 和
   `-SkipBuild` 也必须经过扩大的能力检查，旧精简版不能仅凭哈希正确进入包。
2. **停止后残留 PCM（独立运行时竞态，不归因于裁剪）。** 恢复版复测发现一次
   `Stopped decoder retained queued audio`：Stop 清队列后，已完成的后台读取重新入队。
   在入队锁内校验取消状态与进程归属后，18 格式 × 3 轮全部通过，附加 75 次快速启停通过。
3. **下载提示误称“更新”。** UxPlay 下载复用了 `Downloading update`。
   三语言共享提示改为中性的“正在下载 / Downloading”，保持更新和组件弹窗都适用。
   此文案调整不改变下载器或安装逻辑。

前两项证据：`audio-before.log`、`audio-restored.log`、`audio-fixed-1.log` 至
`audio-fixed-3.log`、`runtime-fixed.log`。测试失败保留，未以重跑覆盖原始失败证据。

### 组件与体积账本

以下为 bytes；组件未压缩体积不能直接当成 Setup 压缩节省。

| 优化项 | 优化前 | 本轮采用值 | 处理 |
| --- | ---: | ---: | --- |
| 媒体 FFmpeg EXE | 101,897,728 | 101,897,728 | 撤销 15,531,520-byte 精简版；不再宣称此项节省 |
| AirPlay 四个 FFmpeg DLL 合计 | 61,633,536 | 2,179,072 | 保留同源码/ABI 的 H.264、ALAC、缩放、重采样构建 |
| Python 桥接器，EXE + `_internal`，不含清单 | 151,318,212 | 56,654,871 | 保留精简，并增加冻结运行时功能自检 |
| 安装版自包含驱动 EXE，均为内部不压缩 | 165,558,971 | 140,146,667 | 移除未使用 WinRT 投影；自包含、提权及嵌入驱动资源不变 |
| UxPlay 分离组件 | 主包内随附 | 70,621,463 ZIP；179,444,946 解压 | 搬到可选组件，不是删除功能或删减下载后的文件 |
| 主程序单文件与安装版共享运行时 | 单文件内 256 项 | 安装版全部 256 项存在 | 仅改变布局与压缩，不做 managed trimming |

Python 基线来自 `dist/test-build/tools`，当前来自 `dist`，都是现存可核实产物；
基线不是本轮相同源码的受控重建，因此包含后续功能改动的少量字节差异，不能把全部差额
精确归因于裁剪。驱动基线来自 `work/compact-driver-uncompressed`。
每文件/每模块清单和 .NET bundle 明细在 `payload-inventory-final.json`。

### 删除内容、依赖和保留依据

- Python：磁盘项 925 → 835，冻结模块 3,084 → 2,244。移除未调用的上游截图/视频/备份/
  CLI/Web 服务树，以及随其拉入的 PyAV（含 65,593,344-byte `av.libs`）、Pillow、
  Pydantic、SQLite 等。应用桥接 API 不是完整 pymobiledevice3 CLI，不承诺上游所有服务。
  tunnel/HID/display/screen_stream/pasteboard/DDI/TSS/pyimg4、qh3、加密、USB 后端、
  Wintun 和资源文件仍保留。冻结 EXE 实际执行 XPC 往返、五点 HID 报告、QUIC 初始包加密、
  AES-GCM、LZFSE 及 CA 加载，155 项 Python 回归也通过。真机隧道/DDI/恢复仍需硬件验收。
- AirPlay：接收器构建补丁只选择 H.264 和 ALAC，AAC-ELD 沿用 FDK AAC 路径，未移除 FDK。
  核对实际 PE 导入依赖和符号：接收器对 avcodec/swscale/avutil 的 15 个导入符号均存在；
  库间导入也无缺失，五个 PE 均为 x64。H.264 B 帧/方向切换、BGRA、ALAC→PCM 与参考字节相同。
- 驱动：bundle 唯一移除项是 `Microsoft.Windows.SDK.NET.dll`（24,877,600）与
  `WinRT.Runtime.dll`（528,944）；其他依赖不变。仅主驱动程序集和 deps 元数据因构建改变。
  驱动使用 Win32/WPF，不用这些 WinRT 投影；主程序仍保留它们。驱动资源、脚本哈希、下载故障、
  父设备枚举及提权保护测试通过，没有为了测试实际更换系统驱动。
- 主程序：解析压缩单文件，和安装版逐项比较；256 个 bundle 项没有缺失，只有
  `iPhoneMirror.deps.json` 随部署模式改变，255 项（含程序集/资源/运行时）哈希相同。
  `PublishTrimmed=false`，WPF 反射和 XAML 未裁剪。
- 架构：安装目录 195 个 native PE、249 个 managed PE；没有未解析的直接 DLL 导入。
  三个非 x64 native 文件是 Wintun 原包保留的 arm/arm64/x86 备用资源，运行时按
  `get_python_arch()` 选择 amd64，并非 x64 进程加载了错误架构。
  UxPlay 135 个 PE 均为 x64，直接导入闭包无缺失。
- UxPlay：完整 137 文件组件经过压缩/提取/逐文件哈希检查。使用下载组件自己的
  GStreamer DLL/插件（子进程 PATH 仅 Windows，禁止系统插件目录与外部扫描器）运行
  H.264/AAC 三轮实际解码；视频与参考像素完全一致，AAC 输出有效的 48 kHz 双声道 PCM。
  未把“插件文件存在”当作功能通过。

### 本轮软件回归

- Native Release：14/14 CTest；155 项 Python；C# 主逻辑和驱动测试通过。
- 生产 HLS HTTP→TS→音视频解码三轮，格式/速率/停止清理及请求异常恢复通过。
- 最新 UxPlay ZIP 本地真实 TLS：无应用代理 3 轮，CONNECT 代理 3 轮；每轮重新下载。
  覆盖安装阶段取消、同大小损坏、截断、并发、单流回退；404 为 4 次、503 为 8 次有界尝试。
  `network-final.log` 记录 6 次成功下载与真实代理隧道。外网镜像对照沿用第一轮证据；
  本轮没有把本地 TLS 结果说成公开 GitHub 资产已可用。
- 下载弹窗三语/双主题/最小尺寸/150% 字号/动态语言及取消成功竞态通过，人工复核渲染。
  大字体内容区可滚动、操作按钮保留；不是要求长说明必须一屏完整显示。
- 媒体、采集、虚拟摄像头、更新提权、USB/无线/蓝牙快捷键、键盘映射、剪贴板、交互回归通过。
  本地化 19,904 格式案例、4 字典切换、12 控件流程通过。
  并行聊天修复的剪贴板入队覆盖/超时回传和快捷键失焦/释放问题已纳入本轮统一构建。

### 发布与验收边界

普通 Release 构建、双模式 publish 和隔离目录 package 已完成。完整代码构建期间
460 项源码/配方哈希保持稳定；只有本审查工具自身随后增加了检查。之后下载文案又有
三语言调整，最终打包和安装结果需以下续记为准。

尚不能用软件回归替代：真实 iPhone 有线/无线投屏和声音、真实反控与重连/DDI、
实际系统驱动安装/回滚、AMD/Intel GPU 编码，以及外部 RTMP/SRT/WHIP 接收服务。
本机 Windows 11 x64 的验证不等于所有 Windows 版本/ARM64 仿真环境都通过。
当前 UxPlay 发布地址的公开资产仍未上传；发布前必须上传与应用描述文件哈希匹配的 ZIP。

## 第二轮续审结果

本节是第一次审查后的新增验证；下文第一轮记录保留为历史证据。
此前并行快捷键变更引起的蓝牙源码断言已恢复通过，新的键盘映射测试也已完成。
本轮没有发布版本或覆盖已有安装包，改动在独立 Release 构建中验证。

新复现并修复了三类运行问题：

1. **HLS 播放连接被重置。** 新增真实 HTTP HLS → MPEG-TS → FFmpeg 解码测试稳定复现
   `Error while copying content to a stream / connection was forcibly closed`。
   原服务只读取请求首行，在仍有未读请求头时关闭 Windows socket，导致响应被视为截断。
   现完整读取请求头，保留 2 秒超时、4 KiB 单行和 32 KiB 总长度限制。
   三轮真实播放、转封装和音频解码通过；同时覆盖错误路径、不完整请求、超长请求头后正常客户端继续访问。
2. **无线预检沿用过期成功结果。** 先预检成功，再损坏解码 DLL，原逻辑仍报告 Ready。
   现在每次启动重新校验并执行预检；测试涵盖 DLL 损坏、恢复以及主机 EXE 损坏。
   正在运行的接收器不会因为轮询而反复预检，只有真正启动时运行。
3. **USB 桥接运行目录校验不完整。** 原检查接受额外残留 DLL、清单范围外路径、未校验的 EXE
   以及 `_internal` 目录链接。现检查实际启动文件和完整运行树，拒绝这些情况，并限制清单大小和目录嵌套深度。
   正常随附桥接器连续 3 轮启动前后全树校验通过，确认未把正常启动产生的数据误判为损坏。

第二轮通过的检查：

- 原生项目 Release 重建；核心 DLL、USB 切换工具、虚拟摄像头 DLL/管理工具、无线主机
  与应用内随附文件的 SHA-256 一致；14/14 CTest 通过。
- 打包组件完整性与原生 DLL 实际加载；桥接器、原始无线主机、FFmpeg 使用仅含 Windows
  的子进程 PATH，各执行 3 轮启动检查。
- HLS 三轮真实传输/解码，含 1.5 倍音频速率、48 kHz 双声道 PCM、停止后清空队列。
- UxPlay 完整 ZIP 重新校验、提取、加载通过；下载弹窗三语言、双主题、150% 字号、动态语言
  与取消/重试竞态回归通过。网络下载矩阵沿用下方第一轮记录，本轮未重复外网大流量测试。
- 最终重跑 150 个 Python 测试、主逻辑、驱动测试、快捷键三种模式、键盘映射、剪贴板、交互、采集清理、
  虚拟摄像头和更新提权运行时回归通过；交互测试没有向蓝牙/iPhone 发送真实按键。
- 本地化：19,888 个格式案例、4 次字典切换、12 个控件工作流通过。
- 精简 FFmpeg 录制/解码/Opus 检查通过；AirPlay H.264/BGRA/ALAC/PCM 与参考输出逐字节一致。

新增测试入口：

```powershell
dotnet build src/App.Runtime.Tests -c Release -o work/component-review2/final
work/component-review2/final/IPhoneMirror.App.Runtime.Tests.exe --component-runtime
```

结果日志：`work/component-review2/final-runtime.log`、`final-logic.log`；
最终弹窗截图：`work/component-review2/final-ui`。
这些检查仍不代表真实 iPhone、特定 GPU、驱动安装或外部推流服务已完成验收。
UxPlay 对应公开 ZIP 未发布的限制仍然存在。

## 结论与发布边界

功能完整性优先，100 MB 仍是体积提示，不作为削减组件功能的理由。
本轮没有上传或发布版本，也没有替换 `outputs/releases` 中已有安装包。
主程序源码及独立测试构建包含本轮修复；打包应在并行的快捷键、剪贴板任务完成后统一进行。

**上线前仍需发布匹配的 UxPlay ZIP。** 当前嵌入的地址：
`https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.8.4-test4/iPhoneMirror-UxPlay-v1.8.4-test4-win-x64.zip`
在本轮直接请求及 `127.0.0.1:7897` 代理请求下均为 HTTP 404。
镜像不能替代尚未发布的源资产；不能据此声称首次在线安装已经可用。

## 修复

- UxPlay 继续复用更新下载器：先对 116 个候选站点进行 HTTPS HEAD 连通性探测，再以 256 KiB Range 数据测速排序，最多 6 路分段下载。不支持 Range 时自动回退单流。
- 下载候选取测速结果前 3 条，并保留官方 GitHub 兜底；每条仅对超时、断流、408/429/5xx 等瞬时故障重试一次。最多 4 条线路、8 次完整下载尝试；404/校验失败不重试同一路线。每次失败重试有 500 ms 退避，连续无数据 30 秒触发换线，取消可中断等待。
- 实测截断响应暴露的 `HttpIOException` 已纳入有限重试，避免错误地跳过换线并作为磁盘错误显示。
- 显式安装重新计算已有缓存的文件哈希，校验工作移出 UI 线程；不再因文件大小和时间戳相同而接受损坏缓存。启动前原有强制校验保留。
- 安装失败或取消时也清理已下载 ZIP。Windows 短暂占用新解压 DLL 导致目录切换失败时，最多进行 3 次额外重试，总退避 1.4 秒。
- 描述文件拒绝空文件项、文件/目录前缀冲突。
- 弹窗使用共享圆角、标题、内容卡片、正文和操作栏样式。重试只在失败后出现；校验、安装、取消状态区分；错误采用本地化说明，保留详细诊断日志；404 单独提示版本资产不可用。
- 调整最小高度，避免英文长标题挤出操作按钮；长内容可滚动。下载完成后旧进度回调不再覆盖安装状态；关闭优先于迟到的成功结果。

## 下载实测

完整本地组件：70,620,171 bytes，SHA-256
`42e5965fb03f644835985f8c984fa37e9207974ea7d82db3b9ecd4fee8ddf8f8`。

测试使用真实本地 TLS 服务、真实 CONNECT 转发代理，以及生产 App 程序集中的下载、校验、解压和缓存逻辑。测试客户端只在自身范围内重定向 socket 并固定临时证书；没有放宽生产 HTTPS/主机验证，也没有改变系统代理。

| 场景 | 结果 |
| --- | --- |
| 完整 UxPlay，不使用代理 | 3 轮通过，每轮重新下载、6 路分段、安装、全树哈希检查 |
| 完整 UxPlay，CONNECT 代理 | 3 轮通过，代理收到真实隧道连接 |
| 已安装后再次安装 | 不发起网络请求 |
| 下载中取消、进入安装时取消 | 不留下可用的半成品；下载临时文件清理 |
| 篡改 ZIP、截断响应 | 拒绝安装，可随后重新成功下载 |
| 并发安装、同大小同时间戳文件损坏 | 安装串行去重；损坏缓存修复 |
| 全部线路 HTTP 404 / HTTP 503 | 分别观察到 4 / 8 次有界尝试 |
| 不支持 Range | 回退单流并通过完整性校验 |
| 实际提取的 UxPlay 主机 | 仅保留 Windows PATH，`--check-runtime` 通过 |

公网测试使用已公开的本仓库资产，不执行下载的旧版安装程序：

| 访问方式 | 861,790-byte SBOM | 92,115,480-byte 安装程序 |
| --- | --- | --- |
| 禁用应用代理、仅官方 GitHub | 3 轮失败：连接重置/连接超时 | 1 轮失败：网络连接 |
| 显式本机代理、仅官方 GitHub | 3 轮通过 | 1 轮通过，153.70 秒 |
| 禁用应用代理、允许测速镜像 | 3 轮通过 | 首轮慢线路达到 240 秒测试上限；复测 2 轮通过，21.81 / 24.59 秒 |
| 显式本机代理、允许测速镜像 | 3 轮通过 | 1 轮通过，39.93 秒 |

无代理大文件复测分别选中 `git.yylx.win`（单流回退）与 `git.tangbai.cc`（6 路分段）。
所有成功下载均校验固定 SHA-256；没有仅用 HTTP 200 判断通过。
“无代理”指 `HttpClientHandler.UseProxy=false`，不代表绕过机器可能存在的 VPN/TUN 网络路由。
本轮结果说明镜像可帮助本机官方连接失败的场景，但不能保证所有网络或第三方镜像的可用性。

## 其他组件与 UI 回归

- 14/14 原生 CTest 通过：核心协议、USB 传输/恢复策略、IPC、DNS-SD、无线主机及媒体路由、UxPlay 主机、虚拟摄像头。
- 136 个 Python 测试通过；打包的 `iUsbBridge.exe --check-runtime` 所列依赖全部 ready。
- 驱动安装器测试通过（未额外执行驱动安装/卸载）。
- C# 主逻辑测试通过。
- 随后快捷键聊天继续修改蓝牙代码，最新全量逻辑重跑出现“Bluetooth app switching … Back remains unavailable”源码断言失败，已通知对应聊天处理；因此不能把整个持续变化的工作区标记为最终全绿。组件独立构建及最终组件回归通过。
- 精简 FFmpeg 的 CPU 录制、音视频解码、Opus 编码通过；AirPlay H.264 方向切换、BGRA、ALAC/PCM 输出与参考 FFmpeg 逐字节一致。
- 媒体、采集清理、虚拟摄像头、更新提权的专门运行时回归通过。
- 本地化审查：18,816 个格式案例、4 次字典切换、12 个控件工作流通过。
- 弹窗三语言/双主题/最小尺寸/150%字号渲染、打开时动态语言切换，以及失败重试、阶段顺序、旧回调、重复启动、取消和成功关闭竞态通过。大字号下沿用共享外层滚动区，已检查操作按钮可滚动到达。
- 聚合 UI 回归首次出现一次采集通知时序断言失败；独立采集/逻辑回归及随后连续 2 轮聚合 UI 回归通过。未凭此修改生产采集逻辑，保留其作为时序测试观察项。

本轮不替代真实 iPhone 的有线/无线投屏、音频、反控、断连恢复验收，也不替代不同 GPU/驱动/外部推流端点的现场测试。

## 复现与证据

日志和渲染图位于 `work/component-audit-20261003`，其中网络故障注入日志为
`network-verified.log`（最终生产程序集测试），公网镜像日志为 `public-mirrors.log`、`public-large.log`。
最终截图位于 `ui-verified`。生成的重复组件解压副本仍在上述测试目录；清理命令被环境策略拒绝，原始发布 ZIP 和日志均未改动。

```powershell
dotnet build src/App.Runtime.Tests -c Release -o work/component-audit-20261003/final-bin
$runner = 'work/component-audit-20261003/final-bin/IPhoneMirror.App.Runtime.Tests.exe'
& $runner --component-download-ui work/component-audit-20261003/ui-final
& $runner --component-network outputs/releases/iPhoneMirror-UxPlay-v1.8.4-test4-win-x64.zip src/App/native/components/uxplay.json work/component-audit-20261003/network-new
& $runner --component-public-large work/component-audit-20261003/public-large-new

dotnet build src/App.Logic.Tests -c Release
dotnet src/App.Logic.Tests/bin/Release/net10.0-windows10.0.19041.0/IPhoneMirror.App.Logic.Tests.dll
```

完整网络测试请使用新的输出目录，避免已有缓存让重复运行跳过下载。
`App.Logic.Tests` 另支持 `--component-public-network <output> <proxy-url>` 与
`--component-public-mirrors <output> <proxy-url>`，分别进行官方路径和镜像路径的直连/代理对照。
