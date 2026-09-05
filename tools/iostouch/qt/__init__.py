"""Apple 私有 USB 屏幕采集协议（QuickTime Screen Capture）的 Python 实现。

模块划分：
* coremedia —— CoreMedia 序列化格式：NSNumber、字典、CMTime、CMClock、FormatDescription、CMSampleBuffer
* packets   —— PING / SYNC / ASYN / RPLY 报文的解析与构造
* session   —— 会话状态机（握手、时钟、背压、停止）与跨 USB read 的帧重组
* usb       —— pyusb 设备发现、激活隐藏配置、claim 0x2A 接口、收发
* h264      —— AVCC → Annex-B、SPS/PPS 提取、PyAV 解码

协议蓝本：quicktime_video_hack（MIT）。
"""
