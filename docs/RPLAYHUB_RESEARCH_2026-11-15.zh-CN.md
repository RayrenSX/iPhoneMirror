# rPlayHub 调研：iPhoneMirror 可以借鉴什么

> 以下保留实施前的调研记录和当时的验证范围。后续已固定上游提交并在 dev 分支完成五个方向的接入，当前功能、构建与验证范围以 [设备实验室说明](DEV_DEVICE_HUB.zh-CN.md) 为准。

调研日期：2026-11-15。对象：[rPlayAI/rPlayHub](https://github.com/rPlayAI/rPlayHub) 及其独立的 [rplayhub-sdk](https://github.com/rPlayAI/rplayhub-sdk)。

对照本地 `main`（HEAD `c80c0bc`，包括已有未提交改动）。本次只增加研究文档，没有接入上游代码、修改程序或执行真机测试。Git fetch、浏览器和本机直接下载遇到连接重置，外部证据来自网页检索取得的官方仓库文档和源码；未取得完整仓库及可复核的 main 提交 SHA。下文的源码判断应在实施前固定上游提交重新核对。

## 结论与建议顺序

最有价值的技术方向是 **CoreDevice 原生媒体投屏**；最容易近期交付的是 **正式 Python SDK/MCP 适配器**和**可重放的虚拟设备测试后端**。rPlayHub 不是可直接替换 iPhoneMirror 的成熟 Windows 组件。

| 顺序 | 借鉴内容 | 对当前项目的价值 | 建议 |
|---|---|---|---|
| 近期 1 | Python SDK + stdio MCP | 已有 HTTP API 的能力更容易被脚本和 AI 使用 | 从仓库内 Python 示例扩展，适配现有鉴权、设备选择、租约与坐标版本 |
| 近期 2 | 真实编码流重放 + 虚拟控制端 | 无手机也能复现首帧黑屏、横竖屏、裁剪和掉帧 | 复用已有测试基础，补完整会话重放 |
| 专项研究 1 | CoreDevice 视频/音频 | 有机会新增不依赖 QuickTime 隐藏 USB 配置的投屏来源 | 先独立验证支持设备、RVRA 解码、恢复和性能 |
| 专项研究 2 | RTP/RTCP 反馈与恢复 | 降低新媒体链路的黑屏、花屏与长期漂移 | 参考源码中的关键帧请求、完整帧确认与诊断设计 |
| 后续 | 按设备的 App 列表与启动 | 自动化可直接打开目标 App | 先做枚举和启动，再评估文件、日志等设备管理功能 |
| 辅助审查 | 画面几何与点击映射 | 保证外框、黑边、编码填充区不影响点击 | 将其作为现有映射实现的对照案例 |

## 1. 它和我们最重要的区别

iPhoneMirror 当前 USB 视频走 QuickTime `0x2A` 接口，反控另走 CoreDevice；rPlayHub 的特色是经 CoreDevice 隧道承载媒体和 HID。其当前主 README 将实时镜像的要求写为 iOS 27+、开发者模式和 DDI，发布入口以 macOS/Linux 为主，Windows 仍列为 upcoming。README 出现 Windows 界面截图，并不足以证明已有可用 Windows 发行包。[来源：主 README](https://github.com/rPlayAI/rPlayHub)

必须分开看版本范围：独立 SDK 文档声称截图、输入和设备管理可用于 iOS 17+，实时视频镜像则要求 iOS 27+。早期 `host/README.md` 中 iOS 26.5.2 的实验记录不能当作正式实时镜像兼容承诺。[来源：SDK 要求](https://github.com/rPlayAI/rplayhub-sdk)、[Python 实验记录](https://github.com/rPlayAI/rPlayHub/blob/main/host/README.md)

本地依据：[架构](ARCHITECTURE.md)、[反控桥](../tools/usb_touch_bridge.py)。我们已经拥有用户态隧道、DDI 准备、HID、USBMux 共存等基础，因此“增加 CoreDevice”不是从零开始，也不是本次的新建议；真正新增的是把媒体接收结果变成正式画面与音频来源。

## 2. 最值得深入研究：利用现有 CoreDevice 媒体流

本地 `UsbTouchSession._open_gate()` 已通过 `DisplayService.start_video_stream()` 打开媒体流；`_drain_media()` 当前接收后只更新存活时间，不把数据交给视频解码器。`_drain()` 则复用这一路径维持反控授权。**有媒体数据，不代表已有可显示的视频帧。**

可以参考 rPlayHub 的 `host-c/media.c` 和 `core/` 中的媒体协议代码，验证以下路径：

```text
现有 CoreDevice 隧道 / DisplayService
  → 媒体协商与 RTP 接收、重组、反馈
  → HEVC 访问单元 + 活动画面区域 + 时间戳
  → 支持该流的解码器
  → 现有 D3D11 预览 / 截图 / 输出
```

如果这条链路在 Windows 真机上成立，有机会新增一种不需要 QuickTime 采集过滤驱动的投屏来源，并减少两套 USB 接口共存的复杂性。这是架构推断，尚未验证；它仍需配对、Apple USB 通路、开发者模式和 DDI，不能宣传为“完全免驱、免准备”。Linux 的上游实现本身也依赖发行版 `usbmuxd`。[来源：Linux 依赖及用户态隧道](https://github.com/rPlayAI/rPlayHub/blob/main/doc/LINUX.md)

建议先在现有桥之外做实验接收器，或在桥内以有界队列旁路导出；解码变慢不能阻塞授权流接收，否则会连带中断反控。媒体流应按设备和会话统一管理，确认停止时释放手机端流，避免反控和新投屏来源重复申请、互相关闭。

第一阶段验收建议：一台明确支持的手机上连续播放 10 分钟，覆盖快速滚动、旋转、静止画面、锁屏和断线重连；分别记录首帧时间、帧完整性、活动尺寸、CPU/GPU 与恢复时间。通过后再接主程序，扩展机型与系统矩阵。

### 最大障碍：RVRA，而不只是“支持 HEVC”

上游记录了 Apple 动态调整活动分辨率时，普通 FFmpeg 可能无报错却解出花屏的情况。其 `patches/ffmpeg-rvra.patch` 修改 HEVC 参考帧重采样来处理 RVRA。[来源：RVRA 分析](https://github.com/rPlayAI/rPlayHub/blob/main/doc/RVRA-AND-PORTABILITY.md)

阅读补丁发现两项明确限制：

- 需要 `thread_count == 1`，否则关闭 RVRA 重采样；应实测 CPU、延迟与多设备负载。
- 包尾活动尺寸识别使用固定档位 `{1184,2576}`、`{1088,1920}`、`{720,1280}`；其他机型、横屏、iPad 等需要额外验证和泛化。

因此不能因为当前 `MediaFoundationDecoder` 已支持普通 HEVC，就认定能正确解码该流；也不能把这份补丁直接替换到全局解码路径。优先验证专用软件解码，再评估硬件路径。[来源：补丁源码](https://github.com/rPlayAI/rPlayHub/blob/main/patches/ffmpeg-rvra.patch)

## 3. 媒体可靠性：优先看当前 C 源码

`host-c/media.c` 值得参考的机制包括：新观看端加入时请求关键帧；发现访问单元不完整时通知消费者重新等待关键帧；只对完整接收的帧发送长期参考帧确认；对丢失触发的 PLI 请求进行节流。这些机制服务于 CoreDevice RTP 链路，不能直接照搬到 QuickTime USB 协议。[来源：媒体接收与反馈实现](https://github.com/rPlayAI/rPlayHub/blob/main/host-c/media.c)

上游 `app/api/PROTOCOL.md` 的录制说明仍写 PLI 未实现，而上述 C 源码已包含 `media_request_keyframe()`、`rp_rtcp_build_pli()` 等路径。实施时应以固定版本的代码和运行结果为准。[来源：旧 API 说明](https://github.com/rPlayAI/rPlayHub/blob/main/app/api/PROTOCOL.md)

我们的解码链已存在队列约束和关键帧恢复，不需要笼统重建“低延迟架构”。可以补充明确可观测状态：等待参数集、等待关键帧、接收中但未解码、帧不完整、解码成功但未呈现。首帧慢时，用户和开发者才知道卡在哪层。

## 4. 最适合近期做：正式 SDK 与 MCP

独立 `rplayhub-sdk` 提供薄 Python 客户端和 stdio MCP；MCP 将截图直接作为图像返回，点击和滑动提供归一化坐标，适合截图驱动的 AI 操作循环。[来源：客户端](https://github.com/rPlayAI/rplayhub-sdk/blob/main/rplayhub_client.py)、[MCP 实现](https://github.com/rPlayAI/rplayhub-sdk/blob/main/mcp_server.py)

我们已有 [Python 示例](api/python-example.py)、[HTTP API](api/README.md) 和丰富的输入能力，缺口是正式封装、安装入口和 MCP 适配，不是增加一套设备控制协议。

建议最小版本：

1. 设备枚举、状态、截图，以及按能力提供点击、滑动、按键、文本。
2. 显式选择设备；自动取得、续期和释放现有控制租约，保留人工收回控制。
3. 截图附带当前 `geometry`，归一化坐标转换遵循现有 capturePixels 边界及 `GeometryVersion`；旋转或重连后重新取几何。
4. 从环境变量或用户配置读取 API key。继续使用本地 HTTP API 的鉴权、权限和错误码。
5. 对输入超时不自动重试，因为原操作可能已经执行。

不建议照搬上游固定单设备端口、默认第一台设备和每次新建 TCP 的通信结构。我们现有多设备、HTTP 与租约设计更适合继续扩展。上游源码中还有路径处理使用 `/` 拆分等细节，直接移植到 Windows 前需检查。

## 5. 很值得借鉴：无手机的完整会话重放

`scripts/mock_engine.py` 用录制的 HEVC 流模拟引擎，点击和滑动只记日志。它特意让编码尺寸与真实屏幕尺寸不同，并支持从 GOP 中间加入、突发发送，用来复现首帧和几何问题。[来源：重放脚本](https://github.com/rPlayAI/rPlayHub/blob/main/scripts/mock_engine.py)

本项目已有 QuickTime fixtures、合成编码帧、静态预览 smoke test 和大量 runtime tests；值得补的是跨越“数据输入→解码→预览→坐标”的可重复会话，不是再造单元测试框架。

可按同一条录制时间线重放横竖屏、编码尺寸变化、暂停恢复、首帧延迟和断线，同时记录点击最终映射。测试输入只使用合成或确认可公开的画面。验收应检查恢复后的像素与坐标，而不只确认窗口存在。

## 6. UI 和设备管理：有选择地借鉴

**坐标与裁剪**：`core/rp_geometry.c` 区分视频尺寸、设备尺寸、显示区域和 HID 坐标，屏幕区域外点击会拒绝。可作为我们 `PhoneFrameGeometry`、`MappingPreviewSurface` 和方向映射的对照案例。其顶部对齐裁剪假设适用于自己的编码填充，不能据此裁切所有 QuickTime/AirPlay 画面。[来源：几何源码](https://github.com/rPlayAI/rPlayHub/blob/main/core/rp_geometry.c)

**设备管理**：App 枚举、启动/终止、设备日志和文件服务值得按需求补充，尤其“打开指定 App”对自动化有价值。它们需要扩展桥接服务和 API 能力，不是增加几个按钮即可。建议先做只读设备详情、App 列表和显式启动；文件、描述文件安装、模拟位置及系统设置另行评估。[来源：SDK 能力清单](https://github.com/rPlayAI/rplayhub-sdk)

**设备信息与诊断入口**：可学习按当前设备组织信息、故障报告和日志的思路；iPhoneMirror 的应用、投屏、反向控制三类设置继续直接展示，设备管理可独立开窗，避免混入偏好设置。

**暂缓 3D/折叠展示**：主 README 将传感器相关功能列为规划，并明确 CoreDevice 不提供 IMU/铰链遥测，需要可选手机端 companion app。Linux 发布说明已宣传 3D/折叠预览，也不代表真实传感器同步已经完成。对目前稳定投屏和反控的收益低于前述工作。[来源：项目规划](https://github.com/rPlayAI/rPlayHub)、[Linux 发布说明](https://github.com/rPlayAI/rPlayHub/releases/tag/linux-v0.1.3)

## 7. 哪些可以直接使用，哪些需要适配

| 对象 | 可复用程度 | 接入前要做什么 |
|---|---|---|
| `rplayhub-sdk` 的工具划分与 MCP 图像返回方式 | 高，适合参考或按许可改造 | HTTP 后端、多设备、API key、控制租约和 Windows 路径适配 |
| `mock_engine.py` 的重放方法 | 高，适合移植思路 | 改接本项目测试接口，不需要另开生产协议 |
| `rp_geometry.c` | 可作算法和用例参考 | 保留本项目旋转、DPI、外框掩码与源坐标约定 |
| RTP/RTCP、媒体协商模块 | 适合独立验证后选用 | 明确依赖、边界检查、会话生命周期和 Windows 编译支持 |
| `ffmpeg-rvra.patch` | 研究性较强 | 固定 FFmpeg 基线、扩展尺寸识别、性能与多机回归 |
| 整套 `cdhost` | 不适合当即插即用替换件 | 需要 Windows 构建、依赖及设备生命周期审查；没有完整构建验证 |

rPlayHub 主仓库声明 GPL-3.0，独立 SDK 声明 MIT；本项目根许可证也是 GPLv3。实际引入源码时仍需逐文件记录来源、版本、版权和依赖许可证，尤其不能将 SDK 的 MIT 声明套用于主仓库或 FFmpeg 补丁。[来源：主仓库](https://github.com/rPlayAI/rPlayHub)、[SDK](https://github.com/rPlayAI/rplayhub-sdk)

本次最推荐的下一项交付是 **基于现有 HTTP API 的 SDK/MCP**；最值得投入一次专项验证的是 **把反控桥已经接收的 CoreDevice 媒体变成可正确解码的视频来源**。后者先证明画面与恢复正确，再讨论替换驱动路径和性能收益。
