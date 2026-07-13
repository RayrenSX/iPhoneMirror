<p align="center">
  <img src="src/App/Assets/iPhoneMirror.png" width="112" alt="iPhoneMirror icon">
</p>

<h1 align="center">iPhoneMirror</h1>

<p align="center">
  Windows 上通过 USB 数据线进行低延迟 iPhone 屏幕与系统声音采集。<br>
  Low-latency wired iPhone screen and system-audio capture for Windows.
</p>

<p align="center"><strong>简体中文</strong> · <a href="README.en.md">English</a></p>

<p align="center">
  <a href="https://github.com/RayrenSX/iPhoneMirror/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/RayrenSX/iPhoneMirror?include_prereleases&sort=semver"></a>
  <a href="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml"><img alt="Windows build" src="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-black.svg"></a>
  <img alt="Windows 10 and 11 x64" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
</p>

## 社区交流

欢迎加入 QQ 群，与其他用户交流使用体验、反馈问题、讨论功能建议。

QQ群号：**1050045279**

## 下载

前往 [Releases](https://github.com/RayrenSX/iPhoneMirror/releases) 下载最新的
`iPhoneMirror-*-win-x64.zip`，解压后运行 `iPhoneMirror.exe`。发布包自带 .NET
运行时，无需另外安装 .NET Desktop Runtime。

目标电脑仍需要以下任一 Apple 官方组件：

- Microsoft Store 版 **Apple Devices**；或
- 含 Apple Mobile Device Support 的 iTunes 桌面版。

## 能做什么

| 功能 | 当前实现 |
|---|---|
| 有线投屏 | USB 直连，不依赖 Wi-Fi 或 AirPlay |
| 视频 | CoreMedia/AVCC H.264、Media Foundation 低延迟解码 |
| 渲染 | D3D11/DirectComposition 原生预览，减少 WPF 拷贝与撕裂 |
| 音频 | 48 kHz 双声道 PCM，WASAPI 播放、静音与音量控制 |
| 设备 | iPhone/iPad、UDID、ProductType、系统版本、信任状态、稳定多设备切换 |
| 画面 | 原生/1080p/720p/540p 本地渲染上限，24/30/60/120 FPS 上限 |
| 预览 | 主窗口、无标题独立窗口、全屏、横竖屏、等比例缩放、按型号匹配屏幕圆角 |
| OBS | 独立窗口可直接使用 Window Capture，无重复的专用窗口入口 |
| 工具 | 截图、强制刷新、快捷键、实时日志、中英文界面 |
| 驱动 | 按 iPhone 实例精确安装 libusb0 UpperFilter，带验证与失败回滚 |

分辨率和 FPS 选项只限制本地渲染，不会降低 USB 上传输的原始画面质量。

## 快速开始

1. 安装并启动 Apple Devices 或 Apple Mobile Device Support。
2. 使用数据线连接 iPhone 或 iPad，保持解锁，并在设备上选择“信任此电脑”。
3. 解压 Release 包，运行 `iPhoneMirror.exe`。
4. 在左侧选择设备，点击“开始投屏”。
5. 若该设备首次使用，程序会说明驱动变更范围并请求一次 UAC 授权。
6. 驱动安装完成后，拔下数据线，等左侧设备卡片消失，再重新连接并保持解锁。

切换到另一台设备时，程序会先向上一台设备发送 QuickTime 结束控制并恢复普通
USB 配置；关闭主窗口也会执行同一清理流程。

> [!WARNING]
> 不要使用 Zadig 把 Apple 父设备替换为 WinUSB/libusb。iPhoneMirror 只在目标
> Apple `usbccgp` 设备实例上增加 `libusb0` UpperFilter，不替换 Apple 驱动，
> 也不修改 WPD/usbmux 子接口。

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
outputs/iPhoneMirror/iPhoneMirror.Core.dll
outputs/iPhoneMirror/Drivers/
```

只构建并运行核心测试：

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

## 架构

```text
iPhone
  │ USB / usbmux / Lockdown
  ▼
QuickTime Screen Capture session
  ├─ CoreMedia + AVCC H.264 ─► Media Foundation ─► D3D11 preview
  └─ 48 kHz PCM             ─► WASAPI audio
                                      │
                                      └─► WPF controls / OBS window
```

- [协议说明](docs/PROTOCOL.md)
- [软件架构](docs/ARCHITECTURE.md)
- [D3D11 渲染](docs/D3D11_RENDERING.md)
- [设备圆角配置](docs/DEVICE_CORNER_PROFILES.md)
- [音频输出](docs/WASAPI_AUDIO.md)

## 驱动安全边界

Windows 会为不同 iPhone/iPad 创建不同设备实例，因此过滤驱动需要按设备安装。程序会：

- 将当前选中 UDID 映射到在线 Apple USB 父设备；
- 验证父服务仍为 `usbccgp`；
- 校验内置驱动文件 SHA-256 与上游签名；
- 只修改目标实例的 UpperFilters；
- 验证 AppleLowerFilter、子接口与 PnP 状态；
- 失败时恢复过滤器快照。

操作日志和回滚快照位于：

```text
C:\ProgramData\iPhoneMirror\DriverOperations
C:\ProgramData\iPhoneMirror\DriverBackups
```

## 当前限制

- 还没有 Media Foundation 虚拟摄像头；OBS 当前使用窗口采集。
- 主程序和提权 Helper 尚未商业签名。
- 完全没有 libusb0 的干净 Win10/Win11 环境仍需更广泛验证。
- QuickTime Screen Capture 并非 Apple 公开、稳定的第三方 API。
- 本项目不提供对 iPhone 的触控或远程控制。

## 参与项目

提交问题前请阅读 [支持说明](SUPPORT.md)。开发贡献请阅读
[CONTRIBUTING.md](CONTRIBUTING.md)，安全问题请使用仓库的
[私密漏洞报告](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new)，不要公开粘贴
UDID、配对记录或完整 USB 抓包。

## 许可与致谢

iPhoneMirror 自有代码采用 [MIT License](LICENSE)。随源码和发布包分发的第三方组件
仍受各自许可证约束，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。其中
libusb-win32 内核驱动为 GPLv3，发布包同时提供对应上游源码与完整许可证文本。

协议研究参考：

- [danielpaulus/quicktime_video_hack](https://github.com/danielpaulus/quicktime_video_hack)
- [chotgpt/quicktime_video_hack_windows](https://github.com/chotgpt/quicktime_video_hack_windows)

Apple、iPhone、iOS、QuickTime 是 Apple Inc. 的商标。本项目与 Apple Inc. 无隶属、赞助
或认可关系。
