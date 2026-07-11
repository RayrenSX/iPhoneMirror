# iPhone 系统音频的 Windows 播放链路

QuickTime `EAT!` 包携带 48 kHz、双声道、16-bit signed little-endian、交错 PCM。
每包 1024 个音频帧（4096 字节，约 21.33 ms）。

当前生产路径：

```text
USB EAT! / CMSampleBuffer sdat
  -> 有界 PCM 环形缓冲（USB 线程只做短拷贝）
  -> 独立 MMCSS "Pro Audio" 线程
  -> IAudioClient3 shared/event-driven
  -> 默认 Windows 多媒体播放设备
```

若默认设备不直接接受 PCM16/48 kHz/双声道，代码会重新创建 `IAudioClient`，使用
`AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` 让 Windows 音频引擎完成采样率、声道和
PCM/float 转换。音频失败不会终止 USB 或视频会话。

环形缓冲采用低延迟策略：启动时保留约 3 个 Apple 包，填充设备端点后约有 42 ms
抖动余量；积压超过高水位时丢弃最旧 PCM，避免断点或设备切换后声音延迟无限增加。

GUI 的播放开关只控制本地 WASAPI 增益，不关闭 QuickTime `HPA1/EAT!` 音频协商，
因此静音后可在同一 USB 会话中立即恢复。音量为 0–100% 线性 PCM16 增益；开关或
滑块变化会在一个端点缓冲区内渐变，减少突变爆音。

真机默认端点实测：`IAudioClient3`、480 帧 engine period、1056 帧 endpoint buffer；
98 秒 GUI 会话连续提交 4,704,096 个音频帧，持续播放欠载为 0。

参考：

- [Microsoft low-latency audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)
- [IAudioClient3::InitializeSharedAudioStream](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-initializesharedaudiostream)
- [WASAPI rendering a stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream)
- [Audio client stream flags](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants)
