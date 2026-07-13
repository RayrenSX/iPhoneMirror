<p align="center">
  <img src="src/App/Assets/iPhoneMirror.png" width="112" alt="iPhoneMirror icon">
</p>

<h1 align="center">iPhoneMirror</h1>

<p align="center">
  Windows 上通过 USB 或 AirPlay 进行低延迟 iPhone 屏幕与系统声音采集。<br>
  Low-latency USB and AirPlay iPhone mirroring for Windows.
</p>

<p align="center"><strong>简体中文</strong> · <a href="README.en.md">English</a></p>

<p align="center">
  <a href="https://github.com/RayrenSX/iPhoneMirror/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/RayrenSX/iPhoneMirror?include_prereleases&sort=semver"></a>
  <a href="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml"><img alt="Windows build" src="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="GPL v3 License" src="https://img.shields.io/badge/license-GPL--3.0--only-3DA639.svg"></a>
  <img alt="Windows 10 and 11 x64" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
</p>

> [!IMPORTANT]
> 当前是公开预览版。程序尚未进行商业 Authenticode 签名，Windows 可能显示
> SmartScreen 或“未知发布者”提示。Apple Screen Capture 是私有协议，未来 iOS
> 更新可能需要同步适配。

## 社区交流

欢迎加入 QQ 群，与其他用户交流使用体验、反馈问题、讨论功能建议。

QQ群号：**1050045279**

## 项目描述

iPhoneMirror 是一个面向 Windows 10/11 x64 的本地 iPhone/iPad 投屏工具，目标是
在不依赖云端中转的情况下，将 USB 有线采集和局域网 AirPlay 接收统一到同一套
预览、音频、截图、独立窗口、OBS 和多设备会话能力中。

项目分为三个清晰边界：C++ 核心负责 Apple 私有 USB 协议、QuickTime/CoreMedia
解析、H.264 解码、D3D11 渲染和 WASAPI 音频；WPF 主程序负责设备列表、会话控制
和用户界面；独立无线宿主负责 AirPlay 协议与解码，并通过有界命名管道传递媒体帧。
驱动安装、修复和卸载由单独的 `iPhoneMirror.Driver.exe` 负责，主程序只读检查
有线设备状态，不在投屏进程中修改系统驱动。

## 下载

前往 [Releases](https://github.com/RayrenSX/iPhoneMirror/releases) 下载最新的
`iPhoneMirror-*-win-x64.zip`，解压后运行 `iPhoneMirror.exe`。发布包自带 .NET
运行时，无需另外安装 .NET Desktop Runtime。压缩包同时包含独立的
`iPhoneMirror.Driver.exe` 驱动管理器，不需要另外下载驱动工具。

目标电脑仍需要以下任一 Apple 官方组件：

- Microsoft Store 版 **Apple Devices**；或
- 含 Apple Mobile Device Support 的 iTunes 桌面版。

无线发现使用 Windows 10/11 自带的 DNS-SD，不安装系统服务、不需要管理员权限，
也不会修改防火墙规则。

## 能做什么

| 功能 | 当前实现 |
|---|---|
| 有线投屏 | USB 直连；按设备选择演示、AirPlay 实验或爱思兼容模式 |
| 无线投屏 | 本地网络 AirPlay，直接接入主预览和全部输出功能 |
| 视频 | CoreMedia/AVCC H.264、Media Foundation 低延迟解码 |
| 渲染 | D3D11/DirectComposition 原生预览，减少 WPF 拷贝与撕裂 |
| 音频 | USB 48 kHz PCM 与 AirPlay PCM，WASAPI 播放、静音与音量控制 |
| 设备 | iPhone/iPad、UDID、ProductType、系统版本、信任状态、稳定多设备切换 |
| 画面 | 原生/1080p/720p/540p 本地渲染上限，24/30/60/120 FPS 上限 |
| 预览 | 主窗口、无标题独立窗口、全屏、横竖屏、等比例缩放、按型号匹配屏幕圆角 |
| OBS | 独立窗口可直接使用 Window Capture，无重复的专用窗口入口 |
| 工具 | 截图、强制刷新、快捷键、实时日志、中英文界面 |
| 驱动 | 有线开始投屏前按当前设备严格检查；异常时打开独立驱动管理器 |

分辨率和 FPS 选项只限制本地渲染，不会降低 USB 上传输的原始画面质量。

有线设备默认使用 **A 演示模式（推荐）**：发送 `Valeria=true` 和原生 `DisplaySize`，完整镜像手机
画面，但状态栏日期、时间和电量会显示 Apple 演示值。**B AirPlay（实验）** 使用
原生尺寸和横竖屏自适应，允许视频 App 切换到外接播放，但可能裁切或显示不全。
**C 爱思模式** 固定为 1565×1565，兼容性更可控，但会限制源画面清晰度。每种模式
右侧的感叹号会显示完整优缺点；选择只作用于当前有线设备。

## 快速开始

1. 解压 Release 包，运行 `iPhoneMirror.exe`。
2. 使用数据线连接 iPhone 或 iPad，保持解锁，并在设备上选择“信任此电脑”。
3. 点击顶部“驱动管理器”，为目标设备执行一键安装；工具会按需补齐 Apple USB
   支持和采集过滤驱动。
4. 在左侧选择设备，点击“开始投屏”。如果当前有线设备的驱动缺失或异常，主程序
   会取消本次启动并自动打开驱动管理器。

切换到另一台设备时，程序会先向上一台设备发送 QuickTime 结束控制并恢复普通
USB 配置；关闭主窗口也会执行同一清理流程。

> [!WARNING]
> 不要使用 Zadig 把 Apple 父设备替换为 WinUSB/libusb。iPhoneMirror 只在目标
> Apple `usbccgp` 设备实例上检测 `libusb0` UpperFilter，不再内置或安装驱动。
> 驱动变更请交给独立驱动工具完成。

## 有线驱动管理

`iPhoneMirror.exe` 只读取驱动状态，所有安装、修复和卸载均由同目录中的独立
`iPhoneMirror.Driver.exe` 完成。主窗口顶部的“驱动管理器”按钮可以随时打开该工具；
已经打开时会激活现有窗口，不会重复启动同一个成品路径。

用户点击有线设备的“开始投屏”时，主程序会针对当前选择的设备检查：

- Apple USB 父设备是否仍为 `usbccgp`；
- 当前设备是否登记 `libusb0` UpperFilter；
- `libusb0.sys` 文件和服务是否正常；
- `libusb0` 是否能够按当前设备序列号完成精确枚举。

任一检查失败都会停止本次有线投屏启动，并自动打开驱动管理器。修复完成并按提示
重新插拔设备后，再回到主程序点击“开始投屏”。驱动管理器界面日志位于
`%LOCALAPPDATA%\iPhoneMirror.Driver\Logs\driver-ui.log`，管理员操作日志位于
`%ProgramData%\iPhoneMirror.Driver`。

> [!NOTE]
> 以上自动检查只针对 USB 有线设备。无线 AirPlay 来源不会读取或要求 `libusb0`，
> 也不会因为驱动状态自动打开驱动管理器。

## 无线 AirPlay

1. 将 Windows 电脑与 iPhone/iPad 连接到同一可信局域网。
2. 在左侧选择“iPhoneMirror AirPlay”来源，可按需修改接收端名称。
3. 点击统一的“开始投屏”按钮。
4. 在 iOS 控制中心打开“屏幕镜像”，选择 `iPhoneMirror AirPlay`。

无线来源使用与 USB 完全相同的会话链路，可直接使用主预览、分辨率/FPS 限制、
音量、截图、独立窗口、全屏、多窗口和 OBS。若 Windows 首次询问网络访问，只允许
可信的专用网络即可。

## 第三方依赖与许可证

| 依赖 | 用途 | 许可证/来源 |
|---|---|---|
| .NET 10、WPF、Windows SDK | 主界面、Windows API 和发布运行时 | Microsoft 官方运行时 |
| libusb 1.0.29 | 可选的 USB 传输兼容层 | LGPL-2.1-or-later，见 `third_party/libusb/` |
| libusb-win32 1.2.6.0 | 独立驱动管理器的 `libusb0` 过滤驱动 | LGPL-3.0 及上游许可证，见 `src/DriverInstaller/Assets/` |
| AirPlayServer 1.1.0 | 无线 AirPlay 接收、FairPlay/视频/音频解码 | GPL-3.0、LGPL-2.1-or-later 及上游许可证，见 `third_party/airplay-server/` |
| FFmpeg 4.4.2 runtime | AirPlay 视频/音频解码依赖 | LGPL-2.1-or-later，随 AirPlayServer 发行物提供 |
| quicktime_video_hack fixtures | QuickTime 协议回归测试向量 | MIT，仅用于 `src/Core/tests/fixtures/` |

Apple Devices、Apple Mobile Device Support、iTunes 和 Windows 系统组件均不是本项目
重新分发的第三方软件。完整版权、来源、版本、哈希和许可证说明见
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 与 AirPlayServer 的 `SOURCE.md`。

## OBS

1. 在 iPhoneMirror 中打开“独立窗口”。
2. OBS → 来源 → 窗口采集。
3. 选择 `iPhoneMirror OBS Preview`。
4. Windows 11 推荐使用 Windows Graphics Capture。

OBS 30.1+ 还可以通过“应用程序音频捕获”选择 `iPhoneMirror.exe`。更多说明见
[OBS 输出文档](docs/OBS_OUTPUT.md)。

## 快捷键

| 快捷键 | 操作 |
|---|---|
| `F11` / `Esc` | 进入/退出全屏 |
| `F5` | 刷新设备 |
| `Ctrl+R` | 强制重绘 |
| `Ctrl+Shift+P` | 打开独立预览 |
| `Ctrl+L` | 显示/隐藏实时日志 |
| `Ctrl+M` | 静音/恢复 |
| `Ctrl+S` | 截图 |

## 已验证设备

| ProductType / iOS | 原生画面 | 真机结果 |
|---|---:|---|
| `iPhone18,3` / iOS 26.5.2 | 1206×2622 | 约 58.6 FPS，常规解码 3–5 ms，48 kHz 双声道 PCM |
| `iPhone13,1` / iOS 18.7.8 | 1082×2340 | 约 58.9 FPS，常规解码 3–6 ms，48 kHz 双声道 PCM |

这些结果仅表示上述实机组合已验证，不构成对所有 iPhone/iOS 版本的兼容性保证。

## 从源码构建

要求：

- Windows 10/11 x64
- Visual Studio 2026 Build Tools：MSVC、Windows SDK、CMake
- .NET 10 SDK 与 Windows Desktop 工作负载

```powershell
git clone https://github.com/RayrenSX/iPhoneMirror.git
cd iPhoneMirror
./build.ps1 -Configuration Release
```

脚本会构建 C++20 核心、运行协议测试并发布自包含 WPF 应用：

```text
outputs/iPhoneMirror/iPhoneMirror.exe
outputs/iPhoneMirror/iPhoneMirror.Driver.exe
outputs/iPhoneMirror/iPhoneMirror.Core.dll
outputs/iPhoneMirror/Wireless/iPhoneMirror.WirelessHost.exe
```

只构建并运行核心测试：

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

## 架构

```text
iPhone/iPad
  ├─ USB / QuickTime ─► H.264 / PCM decode ─┐
  └─ AirPlay ─► WirelessHost ─► I420 / PCM ─┤
                                             └─► native session
                                                  ├─► D3D11 main/detached/fullscreen preview
                                                  ├─► screenshot
                                                  ├─► WASAPI audio
                                                  └─► OBS Window Capture
```

- [协议说明](docs/PROTOCOL.md)
- [软件架构](docs/ARCHITECTURE.md)
- [D3D11 渲染](docs/D3D11_RENDERING.md)
- [设备圆角配置](docs/DEVICE_CORNER_PROFILES.md)
- [音频输出](docs/WASAPI_AUDIO.md)

## 当前限制

- 还没有 Media Foundation 虚拟摄像头；OBS 当前使用窗口采集。
- 主程序尚未商业签名。
- 外部采集驱动的干净 Win10/Win11 安装矩阵仍需更广泛验证。
- QuickTime Screen Capture 并非 Apple 公开、稳定的第三方 API。
- AirPlay 兼容实现并非 Apple 官方接口，未来 iOS 更新可能需要适配。
- 本项目不提供对 iPhone 的触控或远程控制。

## 参与项目

提交问题前请阅读 [支持说明](SUPPORT.md)。开发贡献请阅读
[CONTRIBUTING.md](CONTRIBUTING.md)，安全问题请使用仓库的
[私密漏洞报告](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new)，不要公开粘贴
UDID、配对记录或完整 USB 抓包。

## 许可与致谢

iPhoneMirror 自有代码采用 [GNU General Public License v3.0 only](LICENSE)。分发修改版
或衍生作品时必须遵守 GPLv3 的源码提供、版权声明和同许可证分发要求。随源码和发布包
分发的第三方组件仍受各自许可证约束，详见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

无线接收器作为独立 GPLv3 进程分发，通过命名管道向 GPLv3 主程序传递解码后的媒体帧。
发布包的 `Wireless/licenses` 包含固定版本、源码链接、二进制哈希与完整许可文本。

协议研究参考：

- [danielpaulus/quicktime_video_hack](https://github.com/danielpaulus/quicktime_video_hack)
- [chotgpt/quicktime_video_hack_windows](https://github.com/chotgpt/quicktime_video_hack_windows)

Apple、iPhone、iOS、QuickTime 是 Apple Inc. 的商标。本项目与 Apple Inc. 无隶属、赞助
或认可关系。
