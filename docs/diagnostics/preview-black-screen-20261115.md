# 开始投屏后黑屏：预览交换链释放（2026-11-15）

## 日志结论

本次提供的 `capture (1).log`、`application (3).log` 和 `reverse-control (2).log` 覆盖了多次运行，包含 1.8.3 和 1.8.5-pre5。与截图中“有分辨率、帧率但无画面”对应的直接故障是 DXGI 预览初始化失败：

```text
Could not attach session preview: CreateSwapChainForHwnd failed: 0x80070005
```

采集日志共出现 78 次上述错误，分布在三个应用会话中。1.8.5-pre5 的对应会话出现 50 次；其他两次发生在 1.8.3，因此不能仅凭本次反馈认定为 pre5 新引入的问题。

关键时间线使用采集日志中的本地时间（UTC+8）：

| 时间 | 观察 | 原始采集日志行号 |
| --- | --- | --- |
| 12:12:01.344 | 启动时创建空预览，目标为 1×1 的子 HWND | 28481 |
| 12:12:02.306 | 空预览的渲染线程退出，`rendered=0` | 28491 |
| 12:12:02.323–02.364 | 同一 HWND 首次接管失败，`CreateSwapChainForHwnd` 返回 `0x80070005` | 28493、28494 |
| 12:12:11.470 | 有线投屏已解码 430 帧，1284×2778，55.12 fps，状态 Streaming | 28604 |
| 12:16:21–12:16:43 | 再次启动有线投屏，视频帧数从 94 增加到 1300，但预览接管继续失败 | 29198、29424 |

因此这段黑屏发生在收到并解码画面之后、窗口显示之前。日志里另有 USB 握手超时、无首帧和反控 DDI 下载证书错误，它们是其他尝试中的故障，不能解释上述持续收帧却无法创建预览的状态。

## 代码修复

`D3D11PreviewRenderer::Impl` 原析构函数只停止并等待渲染线程，随后依赖成员 COM 对象自动释放。D3D11 会延迟销毁资源，仅结束线程或释放成员引用不能保证旧翻转模型交换链已解除对 HWND 的占用。

微软明确要求：替换同一窗口上的翻转模型交换链前，必须释放交换链及其视图、解除流水线绑定，再调用即时上下文的 `Flush` 清理延迟销毁。参见 [ID3D11DeviceContext::Flush](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush) 和 [IDXGIFactory2::CreateSwapChainForHwnd](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd)。

修复包括：

- 渲染线程退出后，显式执行完整的设备资源释放流程。
- 将释放流程中的 `Flush` 移到视图、DirectComposition 引用和交换链引用释放之后。此前它位于这些引用释放之前，只作用于设备重建路径。
- 构造中途失败时也执行同样的清理，避免构造异常后窗口继续被部分初始化的交换链占用。
- 将会话停止、销毁和核心关闭纳入同一把预览所有权锁，直到旧渲染器彻底销毁后才允许其他会话接管 HWND，关闭“从会话表移除但交换链尚未释放”的竞态窗口。
- 接管时不持有会话生命周期锁或渲染器表锁来销毁旧渲染器；新渲染器的表节点先分配，再转移所有权，避免异常展开时等待仍需获取同一把锁的渲染线程。

这修正了与日志模式吻合的生命周期缺陷；未改变采集、解码或 USB/反控协议。

## 验证与边界

- Release 原生核心和测试程序编译成功。
- `ctest --preset windows-x64-release --output-on-failure`：18/18 通过。
- 新增 `iPhoneMirror.Core.Tests.exe --preview-handoff-only`：子 HWND 和 DirectComposition 各完成 24 轮空预览→视频→空预览切换。每轮均使用同一个窗口，不在释放与新建之间插入等待；视频提交实际返回 `S_OK`，源帧时间戳保持不变。
- 新增 `iPhoneMirror.Core.Tests.exe --preview-ownership-only`：两种窗口模式均覆盖公开 API 的跨会话接管、同一会话重复 attach、另一个窗口保持独立渲染、并发 stop/destroy、核心 shutdown/reinitialize，以及交换链已创建后构造异常的回滚。
- `--preview-static-frame-only`：两种窗口的静态画面重绘、缩放、旋转、遮挡、短暂上传失败和恢复测试通过，包括实际画面像素核对。
- 应用、逻辑测试和运行时测试项目 Release 构建均为 0 警告、0 错误；测试构建使用的 Core DLL 与最新原生构建 SHA256 一致。
- `App.Logic.Tests` 和 `App.Runtime.Tests` 默认 smoke 全部通过。审查时补全了逻辑测试缺少的设备设置模型引用，并更新设备独立设置、检查更新按钮绑定和开发工具目录的过期断言。
- 关于窗口的品牌色提取为共享资源后，`--about-usability` 和 `--donation-integrity` 均通过，渲染结果已检查。

本机测试环境为 Windows 11 build 26300；修复前也通过新增的快速切换测试，未复现用户电脑的 `E_ACCESSDENIED`。这些测试证明修复后的资源交接和绘制可用，不能代替受影响 Windows 10 19045 电脑上的现场复测。

修改位于集成的 `main` 工作区；本轮未替换正在运行的 `outputs/iPhoneMirror-main` 程序。获取远端最新状态时 `git fetch origin` 遇到连接重置，已保留所有原有待提交修改。
