# iPhoneMirror

iPhoneMirror 是一个面向 Windows 10/11 x64 的 iPhone USB 有线投屏工具。它直接读取 Apple QuickTime Screen Capture 的 H.264 与 PCM 数据，不使用 Wi-Fi 或 AirPlay。

当前版本是可运行的 0.2 开发预览版，已经完成真实 iPhone 的设备发现、信任检查、USB 协议握手、低延迟解码、系统音频播放、原生预览和 OBS 窗口捕获。

## 已实现

- Apple usbmux/Lockdown 设备发现、UDID、设备名、ProductType、iOS 版本和信任状态
- 多设备列表和按 UDID 精确选择
- QuickTime USB configuration 激活、PING/同步时钟、HPD/HPA 会话与完整停止流程
- CoreMedia `CMSampleBuffer`、AVCC H.264、48 kHz 双声道 PCM 解析
- Media Foundation 低延迟 H.264 解码
- D3D11/DirectComposition 原生预览与 WPF 控制界面
- Windows WASAPI 系统音频播放、静音和 0–100% 音量
- 原生、1080p、720p、540p 四档本地渲染上限
- 24/30/60/120 FPS 本地呈现上限
- 全屏、无标题栏独立预览、等比例缩放、自动横竖屏、截图和实时日志
- OBS Window Capture 专用预览窗口
- 每台 iPhone 的 libusb0 过滤驱动检测与一键管理员安装

尚未完成：Media Foundation 虚拟摄像头、正式安装包与商业代码签名。OBS 当前使用窗口捕获。

## 快速使用

1. 安装 Microsoft Store 版 Apple Devices，或包含 Apple Mobile Device Support 的 iTunes 桌面版。
2. 用 USB 数据线连接 iPhone，解锁并在手机上选择“信任此电脑”。
3. 运行 `outputs\iPhoneMirror\iPhoneMirror.exe`。
4. 选择设备并点击“开始投屏”。
5. 如果当前 iPhone 尚未安装采集过滤器，软件会说明变更范围并请求 UAC 管理员授权。
6. 驱动安装后若界面提示重插，只需拔下这台 iPhone，等待两秒再重新连接并解锁。

不要使用 Zadig 把 Apple 父设备替换成 WinUSB/libusb。iPhoneMirror 只在 Apple `usbccgp` 父设备上追加 `libusb0` UpperFilter；它不会替换 Apple 驱动，也不会修改 WPD/usbmux 子接口。

## 每设备驱动安装

Windows 会为不同 iPhone 创建不同的 USB 设备实例，因此过滤器必须按手机实例安装。程序执行以下检查：

- 将 GUI 中选择的 UDID 映射到当前在线的 Apple USB 父实例
- 要求父设备服务仍为 `usbccgp`
- 使用完整设备实例 ID，而不是宽泛的 VID/PID
- 校验内置 libusb-win32 1.2.6 文件哈希与内核驱动签名
- 保存父设备和直属子接口的 UpperFilters 快照
- 只向目标父实例添加 `libusb0`
- 验证 AppleLowerFilter、父驱动、子接口过滤器和 PnP 状态未被破坏
- 用原生核心按目标 UDID 验证 libusb0 确实能够打开这台手机
- 失败时恢复设备过滤器快照

管理员逻辑由 `iPhoneMirror.exe` 自身的编译型 Helper 执行，不会发布或提升 PowerShell 脚本。操作日志和回滚快照位于：

```text
C:\ProgramData\iPhoneMirror\DriverOperations
C:\ProgramData\iPhoneMirror\DriverBackups
```

开发预览版尚未进行商业 Authenticode 代码签名，因此 UAC 仍可能显示“未知发布者”。正式发行前必须签名主程序/Helper，并安装到受保护的 Program Files 目录。

## OBS

1. 在 iPhoneMirror 中打开“OBS 专用窗口”。
2. OBS → 来源 → 窗口捕获。
3. 选择 `iPhoneMirror OBS Preview`。
4. Windows 11 推荐使用 Windows Graphics Capture。

OBS 30.1+ 还可使用“应用程序音频捕获”选择 `iPhoneMirror.exe`。详细说明见 [docs/OBS_OUTPUT.md](docs/OBS_OUTPUT.md)。

## 快捷键

- `F11`：全屏
- `Esc`：退出全屏
- `F5`：刷新设备
- `Ctrl+R`：强制重绘
- `Ctrl+Shift+P`：独立预览
- `Ctrl+L`：实时日志
- `Ctrl+M`：静音/恢复
- `Ctrl+S`：截图

## 构建

开发环境：

- Windows 10/11 x64
- Visual Studio Build Tools（MSVC、Windows SDK、CMake）
- .NET 10 SDK 与 Windows Desktop 工作负载

PowerShell：

```powershell
.\build.ps1 -Configuration Release
```

构建脚本会编译 C++20 核心、执行 CTest，并发布自包含 x64 GUI：

```text
outputs\iPhoneMirror\iPhoneMirror.exe
outputs\iPhoneMirror\iPhoneMirror.Core.dll
outputs\iPhoneMirror\Drivers\
```

GUI 本身不要求目标电脑预装 .NET Desktop Runtime，但 Apple Mobile Device Support 仍是必要的系统依赖。

## 测试

运行原生测试：

```powershell
.\build.ps1 -Configuration Debug -NoPublish
```

命令行真机采集：

```powershell
python .\scripts\diagnose.py --capture <UDID> --seconds 15 `
  --save-frame .\outputs\diagnostics\frame.png
```

GUI 冒烟测试：

```powershell
.\scripts\gui_smoke.ps1 -ResolutionIndex 3 -FrameRateIndex 3 -StreamSeconds 16
```

### 已验证设备

| ProductType / iOS | 原生画面 | 实测结果 |
|---|---:|---|
| `iPhone18,3` / iOS 26.5.2 | 1206×2622 | 约 58.6 FPS，常规解码 3–5 ms，48 kHz 双声道 PCM |
| `iPhone13,1` / iOS 18.7.8 | 1082×2340 | 约 58.9 FPS，常规解码 3–6 ms，48 kHz 双声道 PCM |

`iPhone13,1` 已完成两轮无需重插的连续“启动 → 采集 → 停止 → 再次识别”。停止后 active USB configuration 恢复为 3，usbmux/Lockdown 可自动重新连接。

本轮测试证明的是：在电脑已有全局 libusb0 1.2.6 服务时，为一台全新的 iPhone 设备实例精确挂载过滤器并完成采集。完全没有 libusb0 服务和系统文件的干净 Windows 首次安装路径，仍需在 Win10/Win11 VM（含内存完整性开/关）中单独验证。

## 项目结构

```text
iPhoneMirror/
├─ src/Core/          C++20 USB、协议、CoreMedia、解码、音频与渲染
├─ src/App/           WPF/.NET 10 GUI 与编译型驱动 Helper
├─ docs/              协议、架构和 OBS 文档
├─ scripts/           诊断和真机测试脚本
├─ third_party/       libusb 等依赖与许可证
├─ build.ps1          一键构建、测试和发布
└─ outputs/           发布物与诊断输出
```

协议与架构说明：

- [docs/PROTOCOL.md](docs/PROTOCOL.md)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

参考项目：

- [danielpaulus/quicktime_video_hack](https://github.com/danielpaulus/quicktime_video_hack)
- [chotgpt/quicktime_video_hack_windows](https://github.com/chotgpt/quicktime_video_hack_windows)

libusb-win32 1.2.6 按其许可证随发布物提供 GPL/LGPL 文本、作者信息、第三方声明和完整对应源码包。协议测试夹具的来源及许可见 `src/Core/tests/fixtures/README.md` 与 `THIRD_PARTY_NOTICES.md`。
