# QuickTime iOS Screen Capture 协议分析

## 1. 结论摘要

QuickTime 有线投屏不是 AirPlay，也不是一个普通 Lockdown 服务。它由两条不同的 USB
通道组成：

1. **设备管理通道**：USBMux/Lockdown，用于 UDID、配对、设备名、ProductType、iOS 版本；
2. **音视频通道**：iPhone 的隐藏 USB 配置，接口子类 `0x2A`，通过独立 bulk IN/OUT
   端点交换 QuickTime/CoreMedia 私有协议。

Windows 的关键难点不是 H264 解码，而是如何在不破坏 Apple 官方驱动的情况下发送
vendor control request、切换隐藏配置并 claim `0x2A` 接口。

## 2. USB 配置与传输流程

正常连接时，iPhone 暴露 vendor-specific (`bInterfaceClass = 0xFF`) 的 USBMux 接口，
子类是 `0xFE`，包含一对 bulk IN/OUT 端点。

向设备发送以下 vendor control request：

```text
bmRequestType = 0x40  (Host → Device, Vendor, Device)
bRequest      = 0x52
wValue        = 0x0000
wIndex        = 0x0002
payload       = empty
```

设备会重新枚举并增加隐藏配置。新配置中存在子类 `0x2A` 的接口和额外的 bulk IN/OUT
端点，用于音视频协议；原 USBMux 端点仍然存在。关闭时发送相同请求但 `wIndex = 0`，
然后切回 USBMux 配置。

iPhone18,3 / iOS 26.5.2 实测：激活前有 4 个 configuration，其中配置 4 还有一个
子类 `0xFD` 的接口；发送激活请求后增加 configuration 5，真正的 QuickTime 接口为
`class=0xFF, subclass=0x2A, interface=2`，bulk IN=`0x86`、OUT=`0x05`。因此不能把
激活前的 `0xFD` 当作屏幕流接口。

Windows 参考仓库修改了 usbmuxd：发现设备后发送 `0x40/0x52/0/2`，并把它自己的
usbmux TCP 监听端口改为 `37015` 以避开 Apple 官方的 `27015`。**37015 本身不传输
音视频**；C++ 核心仍通过 libusb-win32 直接 claim `0x2A` 端点。

## 3. USBMux 与设备信息

Apple 官方 Windows 服务通常在 `127.0.0.1:27015` 提供 usbmux plist 协议。
每个请求包含 16 字节小端头：

```text
uint32 totalLength
uint32 protocolVersion = 1
uint32 messageType     = 8 (plist)
uint32 tag
UTF-8 XML plist
```

`ListDevices` 返回 DeviceID、SerialNumber/UDID、ConnectionType 等。随后发送 `Connect`，
DeviceID 指定设备，PortNumber 是网络字节序的 iOS 端口。连接 62078 后，socket 变成
Lockdown 隧道；Lockdown plist 使用 4 字节大端长度前缀。

iPhoneMirror 当前使用：

- `ListDevices`：多设备和 UDID；
- `ReadPairRecord`：判断主机是否存在配对记录；
- Lockdown `GetValue`：DeviceName、ProductType、ProductVersion；
- Lockdown 可访问性：辅助判断设备是否已解锁/信任。

配对记录存在不等于当前会话一定被设备接受，因此 UI 分开显示“有配对记录”和
“Lockdown 可访问”。完整信任验证还需要读取 PairRecord、ValidatePair、StartSession
并按响应启用 SSL。

## 4. 音视频会话状态机

claim QuickTime bulk 端点后，主机按如下顺序工作：

1. 设备发送 `PING`，主机回复相同 PING；
2. 设备发送 `SYNC/CWPA`，包含设备音频时钟引用；主机创建本地音频时钟并 `RPLY`；
3. 主机发送 `ASYN/HPD1`，提供显示尺寸和 Valeria/演示模式能力；
4. 主机发送 `ASYN/HPA1`，引用设备音频时钟并请求音频；
5. 设备发送 `SYNC/AFMT`，通常是 48 kHz LPCM ASBD；主机回复 Error=0；
6. 设备发送 `SYNC/CVRP`，含视频时钟引用、队列水位和首个 FormatDescription；
7. 主机回复本地视频时钟，并发送 `ASYN/NEED`；
8. 设备发送 SPRP、CLOK、TIME、TBAS、SRAT 等时钟/属性消息；
9. 设备持续发送 `ASYN/FEED`（视频）和 `ASYN/EAT!`（音频）；
10. 每收到一个 FEED，主机再次发送带设备视频时钟引用的 `NEED`，维持背压。

停止顺序是 HPA0、HPD0、处理 STOP/RPLY 与 RELS，然后释放接口、恢复 USBMux 配置。

## 5. 包格式与字节序

USB 音视频流是长度前缀帧：

```text
uint32_le totalLengthIncludingThisField
uint32_le magic
...
```

Apple FourCC 数值使用显示顺序，例如 `ASYN = 0x6173796e`，但整数以小端写入，
所以线上字节是 `6e 79 73 61`。固定抓包示例：

```text
10 00 00 00 67 6e 69 70 00 00 00 00 01 00 00 00
| length=16 |   PING wire bytes  | zero       | one |
```

主要消息：

- PING：握手；
- SYNC：需要 RPLY，偏移 12 是 CWPA/AFMT/CVRP/CLOK/TIME/SKEW/STOP；
- ASYN：不直接 RPLY，偏移 12 是 HPD1/HPA1/NEED/FEED/EAT!/SPRP 等；
- RPLY：带 64-bit correlation id。

iPhoneMirror 的 `StreamDecoder` 允许任意 USB read 边界：一个 read 可包含半个包、一个包或
多个包。Windows 参考实现只处理多个完整包，并对跨 read 的包标注 TODO，这是高码率下的
稳定性风险。

## 6. CoreMedia 序列化

FEED/EAT 外层：

```text
ASYN | clockRef | FEED/EAT! | uint32 sampleBufferLength | sbuf | chunks...
```

`sbuf` 内部是连续的 `uint32_le length + uint32_le magic + payload`：

- `opts`：OutputPresentationTimestamp（CMTime）；
- `stia`：CMSampleTimingInfo 数组，每项是 Duration/PTS/DTS 三个 CMTime；
- `sdat`：编码视频或 PCM 音频字节；
- `nsmp`：sample 数量；
- `ssiz`：sample 大小数组，单项可代表统一大小；
- `fdsc`：CMFormatDescription；
- `satt` / `sary`：sample attachments。

CMTime 为 24 字节小端结构：

```text
int64 value
int32 timescale
uint32 flags
int64 epoch
```

视频 fdsc 包含 `mdia=vide`、`vdim`、`codc=avc1`、`extn`。extn 的字典里可找到
AVCDecoderConfigurationRecord，包含 NAL 长度字段宽度、SPS、PPS。

音频 fdsc 包含 `mdia=soun` 和 40 字节 AudioStreamBasicDescription。上游真实夹具是：

```text
sampleRate       48000
format           lpcm
formatFlags      76
bytesPerPacket   4
framesPerPacket  1
bytesPerFrame    4
channels         2
bitsPerChannel   16
```

因此 QuickTime 系统音频通常不是 AAC，而是未压缩的 48 kHz 双声道 16-bit PCM。输出 WAV
只需要写 WAV 头；送 OBS 可通过 WASAPI/共享内存音频源，送 RTMP 时再编码 AAC。

## 7. H264 流

视频 `sdat` 使用 AVCC：每个 NALU 前是大端长度（通常 4 字节），不是 Annex-B start code。
解码流程：

1. 从 fdsc/AVCDecoderConfigurationRecord 提取 SPS/PPS 和 NAL 长度宽度；
2. 将 sample 中的长度前缀逐个验证；
3. Windows H264 MFT 要求 Annex-B；序列头中的 SPS/PPS 和每个 sample 的 NALU 都必须
   改成带 `00 00 00 01` 起始码的形式，不能直接传 AVCDecoderConfigurationRecord；
4. UDP/文件等 Annex-B 消费者使用相同转换；
5. NAL type 5 是 IDR，可用于关键帧/重连检测。

HPD1 `DisplaySize` 是 QuickTime 主机能力/显示参数，不是可靠的 USB 编码分辨率控制。
设备给出的真实 vdim 和 timing 才是最终源流分辨率/帧率；上游抓包示例为
1126×2436、Duration=1/60。本项目 GUI 的 1080p/720p/540p 只限制 Windows 本地
D3D11 渲染，不再改写 HPD1，也不会为切换档位重连 USB。

## 8. Windows 重新设计

不直接移植参考实现，原因：

- libusb-win32 代码和驱动较旧，直接替换 Apple 驱动会破坏正常设备功能；
- 参考读取器无法重组跨 USB read 的大包；
- CoreMedia 解析使用未对齐裸指针，多个长度未先验证；
- SPS/PPS 提取存在小数组下溢和字段命名相反的问题；
- 线程状态与错误传播不足，不适合 GUI/多设备生产环境；
- HPD1 分辨率硬编码为 2160×3840，不代表实际输出能力。

iPhoneMirror 的设计：

- Apple 官方 27015 只负责发现、配对、Lockdown；
- 单独的 QtUsbTransport 负责 control request 和 `0x2A` bulk 端点，具体驱动后端可替换；
- 每设备独立 CaptureSession 和取消令牌；
- 所有长度、乘法、偏移在读取前验证，并设置包上限；
- H264 当前交给 Media Foundation 解码为 NV12，再按需转 BGRA 给 WPF；D3D11 共享纹理是后续优化；
- 音频保留 PCM，输出层按需 WAV/AAC；
- OBS 第一期使用稳定窗口，之后再做 Media Foundation Virtual Camera。

普通 WinUSB/libusbK 不能作为最终答案：libusb 官方 Windows 文档明确列出，WinUSB 和
libusbK 无法把设备切换到非第一个 configuration，而 QuickTime 正依赖动态增加的后续
configuration。已知可行的 libusb0 过滤模式也被官方标为问题较多；UsbDk 同样有稳定性
警告。因此驱动选择必须以真机压力测试、签名和回滚为准，不能仅凭 API 可编译就宣称完成。

参考：https://github.com/libusb/libusb/wiki/Windows

## 9. 兼容性风险

协议是逆向结果，不是 Apple 公共 API。Apple 可以在任意 iOS 版本更改 vendor request、
USB descriptor、消息字段或编码格式。原项目发布说明主要停留在 2020 年，Windows 仓库
虽在 2025 年发布，但没有公开的 iPhone 15/16/17 + iOS 17/18/26 自动化矩阵。

因此每个目标系统必须做真机回归：连接/重枚举、锁屏/解锁、横竖屏、60fps、来电/音频
路由、应用 DRM、长时间运行、拔线重连、多设备与睡眠恢复。

## 10. 一手资料

- 原始实现与测试夹具：https://github.com/danielpaulus/quicktime_video_hack
- 作者技术文档：https://github.com/danielpaulus/quicktime_video_hack/blob/main/doc/technical_documentation.md
- Windows C++ 参考：https://github.com/chotgpt/quicktime_video_hack_windows
- libimobiledevice：https://github.com/libimobiledevice/libimobiledevice
- usbmuxd：https://github.com/libimobiledevice/usbmuxd
