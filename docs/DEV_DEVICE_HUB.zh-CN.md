# dev：rPlayHub 特色功能集成

该分支增加 CoreDevice 原生投屏、Python SDK / MCP、编码流重放、媒体恢复，以及完整设备管理入口。
上游功能逐项映射、测试证据和规划项见 [功能对照表](RPLAYHUB_FEATURE_MATRIX.zh-CN.md)。
入口为 **开发者工具 → 设备实验室 · dev**；主窗口右下角版本号连续点击五次打开开发者工具。

## 功能与使用

| 功能 | 已接入的行为 | 使用方式 |
|---|---|---|
| CoreDevice 原生投屏 | DisplayService HEVC 视频和 AAC-ELD 系统音频；复用现有用户态隧道、配对、DDI；I420 / PCM 进入现有原生预览、截图与录制输出 | 选择已配对的 USB 或无线设备，准备连接，停止该设备原有投屏后在媒体页点击「开始原生投屏」 |
| Python SDK | 设备选择、租约、截图、点击/滑动、按键/文字、App 列表/启动、CoreDevice 启停和状态 | `python -m pip install -e ./sdk/python`，详见 [SDK 说明](../sdk/python/README.md) |
| MCP | 官方 Python MCP SDK、stdio 工具调用、截图图片内容及几何信息 | `python -m pip install -e './sdk/python[mcp]'`，运行 `iphonemirror-mcp` |
| 编码流重放 | 记录原始 RTP/RTCP 与时序，离线经过相同组帧、解码、媒体管线，独立预览窗口 | 开始前勾选「录制数据包供重放」；随后「重放录制文件」选择 `.imrtp` |
| 媒体反馈与恢复 | SR/RR+SDES 保活、RCTL、PLI、完整 AU 回执、丢包丢帧、关键帧门控、队列上限、隧道修复后重新绑定 | 「媒体状态」查看完整帧、丢包、队列丢弃和错误；连续 10 秒无完整帧时交给现有桥健康恢复 |
| App 枚举与启动 | 新 DDI 使用 streamapplist；每次使用独立 AppService 通道；启动一次，不强制结束已有 App | 「获取 App 列表」后选择 App，点击「启动所选 App」；API 启动须持有控制租约 |

设备须解锁、信任电脑并启用开发者模式；个别设备需要匹配的 Personalized DDI 和现有控制绑定。
首次准备可能下载并挂载 DDI。原生视频来源不使用 QuickTime 采集过滤驱动；Apple 设备连接支持和配对仍然需要。
实验室与常规设置分别显示，既有「应用、投屏、反控」三类设置保持原样。

录制默认关闭，开启后保存到 `%LOCALAPPDATA%/iPhoneMirror/MediaCaptures`，创建新文件，单文件最多 1 GiB。
文件包含所录屏幕和音频的编码数据，按本机文件处理；不会自动上传。停止捕获时关闭录制。

## 构建与验证

需要 Visual Studio 2026 C++ 工具链、CMake、.NET 10、Python 3.13，以及 MSYS2 **UCRT64** 的
`gcc`、`make`、`patch` 和 `nasm`。MSYS2 的安装和工具包管理由构建者完成。

```powershell
./scripts/build_dev.ps1 -MsysRoot C:/msys64
```

该脚本构建桥、补丁解码器、原生 Core、WPF 应用并运行相关回归；不覆盖正在运行的程序或交付目录。
应用构建输出在 `src/App/bin/Release/net10.0-windows10.0.19041.0/win-x64`。
开发分支的验证构建与主线交付分开；正式交付前先按仓库规则整合到主工作树，再从 `outputs/iPhoneMirror-main` 交付。

解码器单独构建：

```powershell
./scripts/build_coredevice_decoder.ps1 -MsysRoot C:/msys64
```

下载固定版本 FFmpeg 8.1.2 并校验 SHA-256，应用固定 rPlayHub 提交的 RVRA 补丁。
上游补丁末尾的 hunk 行数错误由构建脚本重算；上游原文件保持不变。
附加修改用逐包元数据代替三档固定尺寸，且在访问参考帧前校验尺寸不超过 SPS。
解码采用单线程，进程只接受自定义二进制 stdio，不执行设备操作。
进程随已拥有的桥进程 Job 生命周期结束。

无手机测试：

```powershell
python -m unittest discover -s tests -p coredevice_media_test.py
python -m unittest discover -s tests -p device_hub_test.py
python -m unittest discover -s tests -p python_sdk_test.py
ctest --test-dir build/native -C Release -R '^CoreProtocolTests$' --output-on-failure
# 在完成 build_dev.ps1 后：
python tests/coredevice_media_test.py --write-fixture work/replay-test.imrtp
src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe --automation-api
src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe --coredevice-replay work/replay-test.imrtp
```

合成 HEVC 样本包括横竖屏 SPS 切换，逐帧和未修改 FFmpeg 的 I420 SHA-256 比对。
测试覆盖丢包后关键帧恢复、RTP 序列号回绕、错误长度、FU/AP、慢消费者、录制损坏、控制租约和过期桥代次。
封装后的 `iUsbBridge.exe → RTP → 补丁解码器 → 私有命名管道 → 原生会话 → 截图输出` 也有真实进程集成测试。

## API

所有路径均在 `/api/v1/devices/{id}` 下，`id` 必须来自设备列表。每个请求须带 `X-API-Key`。

| 方法与路径 | 权限 | 请求 |
|---|---|---|
| `GET /apps` | `device.read` | 已准备好的设备桥 |
| `POST /apps/launch` | `device.control`，`X-Control-Session` | `{"bundleId":"com.apple.Preferences"}` |
| `POST /coredevice/start` | `device.control`、`screen.capture` | `{"audio":true}`；先释放租约并停止该设备原有捕获 |
| `POST /coredevice/stop` | `device.control`、`screen.capture` | 先释放租约；仅停止 CoreDevice 来源 |
| `GET /coredevice/status` | `device.read` | 帧数、丢包、丢帧、错误和关键帧等待状态 |

App 启动与输入共用独占租约、取消和代次检查。请求结果不确定时不自动重试。
录制文件路径和解码器路径不能由公共 HTTP API 指定；录制由实验室或持有租约的 SDK/MCP 调用显式开启。
解码画面只通过当前 Windows 用户可连接的随机命名管道交付。

## 当前验证边界

代码、编译、合成编码样本、真实解码进程、封装桥与离线重放测试已覆盖。
**本次没有使用真实 iPhone 对新链路做验收**，因此保留 dev 实验功能定位。
尚需实机检查不同 iOS/DDI 的媒体协商、AAC-ELD 系统音频、App 启动效果、长时间断线恢复，以及 Apple RVRA 降档画质。
RVRA 仅识别已观察到的 14 字节尾标；未知变体的画质不作保证。上游参考帧缩放核也是近似实现。
原生色彩当前按现有 I420/NV12 SDR 管线处理，不声称 HDR 端到端保真。

参考源码和许可证见 [third_party/rplayhub](../third_party/rplayhub/README.md)。

## 完整设备管理（2026-11-15 补齐）

设备实验室提供应用、进程、描述文件、文件与报告、实时日志、媒体、手机设置、设备八页。
USB 和已配对无线连接都复用当前设备桥；准备连接后在媒体页开始 CoreDevice 投屏。
应用支持安装/卸载、图标、用户/系统/开发/轻 App 过滤；进程支持结束；
描述文件支持 Provisioning 与配置描述文件安装/移除；配置安装保留手机确认。
文件支持 media/crashes 分页浏览、分块下载和崩溃报告导出。
手机设置支持外观/辅助功能和模拟定位/恢复，设备页有系统按钮与电源操作。
媒体页可以独立开始/停止 HEVC 或 RTP 录制；解码器失败会重启并等待新的完整关键帧。

GET /api/v1/hub/actions 返回 27 项公开动作及只读/超时政策。
POST /api/v1/devices/{id}/hub/{action} 接受 JSON 参数：读取需要 device.read，
修改需要 device.control 和当前 X-Control-Session，录制另需 screen.capture。
具体动作与参数见共享清单及 SDK 同名方法。

POST /api/v1/devices/{id}/apps/install 与 /profiles/install 接受 application/octet-stream，
必须指定 Content-Length 和 X-File-Name（只使用扩展名）。IPA 最大 512 MiB，描述文件最大 8 MiB。
上传和安装全程需要有效租约；长操作须续租，SDK 自动续同一个 token。
临时文件在成功、异常和取消后清理；公开 API 不接受服务器本机文件路径。
手机文件路径相对于 media/crashes，禁止目录穿越和符号链接。
所有修改不自动重试；断线重绑前取消旧操作。

新增协议测试：python -m unittest discover -s tests -p device_management_test.py。
实机验收边界仍按上文与功能对照表执行。

中途开始 RTP 录制时，文件先保存一包由已协商 VPS/SPS/PPS 构造的参数引导 AP，随后保存原始数据报和时序；这样手机下一关键帧省略参数时仍可离线解码。磁盘写入或 flush 错误只停止录制，不停止投屏。
