# 原画虚拟摄像头 60 fps 修复与实机验证

> 历史记录：以下发现和验证按文中日期、设备与工作区解释。当前操作见[使用指南](USER_GUIDE.md)，实现与构建见[架构](ARCHITECTURE.md)和[开发指南](DEVELOPMENT.md)。

测试日期：2026-10-28 至 2026-10-29（Asia/Shanghai）。

原画输出的应用端性能瓶颈已修复。真实 USB 手机源为 **1082×2340**，虚拟摄像头保持原画并补齐宽度到 **1088×2340**，协商帧率 60 fps。Windows 摄像头直接读取约 **59.84 个不同画面/秒**；OBS 启用视频采集设备缓冲后，无损录像全段平均 **58.13 次画面更新/秒**，前 5 秒之后平均 **59.16 次/秒**。仍有少量重复帧，不能将结果描述为每秒稳定 60 个不同画面。

## 实现

- `CoreApi.cpp` 使用新的 `SdrBgraConverter.h` 批量转换 SDR NV12/P010 像素；SSE2 属于 Windows x64 的基线指令集。原尺寸输出直接走连续行转换，缩放仍保留既有最近邻采样映射。HDR、Display P3 和 BT.2020 色域沿用既有颜色处理。
- `VirtualCameraService` 在 60 fps 下先检查轻量源时间戳，新源帧到达后读取并发布像素，避免另一个固定 60 Hz 取帧时钟错过有到达波动的帧。每 100 ms 仍执行完整读取，保留静态画面、相同时间戳下的像素更新及源丢失检查。
- 媒体投屏可返回“没有独立源时钟”，此时按选择的帧率读取，避免高频时间戳检查导致不必要的全帧转换；增加了对应回归验证。
- 连续 USB 解码使用双缓冲 GPU 回读，提交本帧拷贝时读取上一帧，避免每帧同步等待 GPU 完成。帧的原始时间戳、颜色和顺序随缓存保留；drain 输出最后一帧，flush 丢弃旧缓存。该选项仅在连续 USB 路径启用，默认的静态/单帧解码保持即时输出。
- Windows 摄像头组件使用独立接收线程和最多 5 帧的有界队列吸收到达波动；启动时预缓冲至 3 帧，静态源有等待上限。消费者慢或暂停时丢弃最旧缓存，防止队列持续增长。队列和双缓冲会增加数帧延迟。
- 摄像头的原画行拷贝使用一次批量写入同时设置不透明 alpha，只清除留白区域，避免在 Frame Server 分配的图像缓冲区上反复逐字节写入。线程启用 1 ms 定时精度，并通过 MMCSS 的 Capture 任务调度，退出时释放设置。

微软对 [MMCSS](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) 和 [Frame Server 样本分配器](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/ne-mfidl-mfsampleallocatorusage) 的说明用于核对系统接口；仍保留系统提供的分配器。

## 实机结果

机器：AMD Ryzen 9 9955HX、AMD Radeon 610M；OBS Studio 33.0.0。使用独立便携 OBS 配置，不依赖应用模拟帧。

| 测量项 | 修复前 | 修复后 |
| --- | --- | --- |
| 手机源尺寸 | 1082×2340 | 1082×2340 |
| 摄像头输出 | 1088×2340，标记 60 fps | 1088×2340，60 fps |
| 应用原画取帧平均耗时 | 56.431 ms | 约 5.9 ms |
| 应用观察到的新源像素/时间戳 | 约 17.35 fps | 中位数 59.955 fps |
| Windows 摄像头直接读取 | 未记录 | 600 帧，60.04 fps；598 次像素变化，59.84 次/秒；黑帧 0 |
| OBS 无损录像画面更新 | 17.11 次/秒 | 58.128 次/秒；前 5 秒后约 59.16 次/秒 |
| 最终 OBS 编码/渲染丢帧 | 0 / 0 | 0 / 0 |

最终 OBS 录像为 `2026-10-29 00-45-46.avi`，UtVideo 无损编码，1088×2340、60 fps，29.917 秒、1795 帧、1739 次中心画面变化。逐像素比较解码后的中心亮度区域，排除手机状态栏和边缘留白；无损编码避免把编码噪声误算成新画面。

OBS 视频采集设备属性中的 **缓冲设为“启用”**。本轮同样代码在禁用缓冲时，OBS 录像平均约 52.21 次画面更新/秒；直接读 Windows 摄像头仍约 59.84 次/秒。缓冲对本机端到端结果有明显影响。独立测试配置已保存为启用，其他 OBS 配置需按此设置。

## 验证与本地产物

- Release 构建成功；托管构建 0 警告、0 错误。
- `CoreProtocolTests`、`MediaSourceActivateTests`、`VirtualCameraComponentTests` 通过。
- 托管虚拟摄像头回归通过：60 fps 取帧、静态画面、时间戳重置、同时间戳像素变化、源丢失、取消、启动/发布失败及停止清理。
- 新增 SDR 测试覆盖 NV12/P010、全/有限范围、BT.601/709/2020 矩阵、缩放、奇数宽度尾部、输出边界、不透明 alpha；与双精度参考转换相差不超过 1 个颜色码值。HDR/广色域不误入快速路径。
- 新增解码测试验证双缓冲 drain 不丢最后一帧，flush 后不返回旧时间戳，输出帧顺序保持正确。

修复后原生组件已安装到本机，构建目录中的应用及测试程序使用相同的原生 DLL。应用程序为 `src/App/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.exe`。

证据位于 `outputs/virtual-camera-live-20261028/`：

- `native-source-pixels/capture-measurements.json`：输入像素变化与时间戳的同时测量。
- `pixel-source-direct-probe.txt`：Windows 摄像头直接读取结果。
- `obs-native-buffered-final-analysis.json`：最终无损录像逐帧分析。
- `buffered-final-obs-recording.json`、`buffered-final-obs.log`、`buffered-final-capture.log`：最终录制统计与日志。
- `2026-10-29 00-45-46.avi`：最终原画无损录像。

测试结束后，测试进程和独立 OBS 已停止，USB 会话已正常释放。录像和截图仅保存在本机。
