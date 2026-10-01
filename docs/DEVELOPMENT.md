# 开发、测试与发布

本文于 2026-10-01 对照当前工作区脚本核对，源码版本为 `1.8.4-test4`。
以下命令均从仓库根目录运行。模块关系见[架构说明](ARCHITECTURE.md)，文档入口见[索引](README.md)。

## 环境与依赖

| 工具 | 要求与用途 |
|---|---|
| 系统 | Windows 10/11 x64；WPF 运行时测试需要交互桌面；虚拟摄像头运行需要 Windows 11 |
| Visual Studio C++ Build Tools | MSVC、Windows SDK、CMake；默认 CMake preset 指向 Visual Studio 18 2026，`build.ps1` 会查找已安装的工具链 |
| CMake | 项目最低 3.25；优先使用 Visual Studio 随附版本 |
| .NET | `global.json` 指定 10.0.301，允许 `latestFeature` 向前滚动，不启用预发布 SDK |
| Python | 桥接配方和 CI 使用 Python 3.13 x64，构建时创建隔离虚拟环境 |
| MSYS2 UCRT64 | 默认发布包含 UxPlay；需要 CMake、Ninja、toolchain、pkgconf、GStreamer base/good/bad/libav、libplist、OpenSSL；包名见 Windows workflow |
| 网络 | 首次还原 NuGet/Python 包以及准备 FFmpeg、VC Runtime、UxPlay、Inno Setup 等依赖时需要网络 |

Apple USB 支持和实机信任是运行有线功能的条件，不是离线协议单元测试的条件。
Apple DDI 不随标准安装包分发；反控首次准备 DDI 的网络需求见[桥接使用说明](USB_TOUCH_BRIDGE_USAGE.md)。

## 仓库结构

| 路径 | 职责 |
|---|---|
| `src/App` | WPF 主程序、设备会话、输入路由、媒体输出及更新器 |
| `src/Core` | C++20 C ABI、USB/QuickTime、解码、D3D11、WASAPI 与无线 IPC |
| `src/WirelessHost` / `src/UxPlayHost` | 原始 AirPlay/DLNA 宿主、UxPlay 备用适配进程 |
| `src/VirtualCamera` | Windows 11 Media Foundation 媒体源、帧交换及注册助手 |
| `src/DriverInstaller` | 独立驱动管理器与提权操作入口 |
| `src/Shared` / `src/SharedUI` | 共享下载/提权安全逻辑、主题、控件、窗口行为 |
| `tools/usb_touch_bridge.py` / `tools/iostouch` | 当前 Python 反控桥及 QuickTime 配置下的 USBMux 共存支持 |
| `scripts` / `installer` / `.github/workflows` | 构建依赖、验证、打包、安装器与 CI |
| `src/*.Tests` / `tests` | 托管、Python 及专项恢复测试；原生测试在各模块的 `tests` 下 |
| `docs` / `updates` | 文档与版本说明、应用备用更新清单 |
| `build` / `dist` / `outputs` / `work` | 构建目录、桥接载荷、发布产物和临时依赖；不是唯一源码来源 |

## 常用构建

```powershell
# 默认 Release：构建、测试，并发布自包含应用
./build.ps1 -Configuration Release

# 调试构建和测试，不生成自包含发布目录
./build.ps1 -Configuration Debug -NoPublish
```

`-NoPublish` 仍会构建桥接器并准备原生依赖，不是只编译 C# 的快捷入口。
常规 Release 产物为 `outputs/iPhoneMirror`；安装器载荷为
`outputs/iPhoneMirror.Installer`，主程序与驱动管理器共享外置 .NET 运行时。
二者都包含独立桥接器的完整 onedir 载荷。

| 参数 | 行为与限制 |
|---|---|
| `-SkipTests` | 跳过 `build.ps1` 的测试段；不能据此报告测试通过 |
| `-OmitUxPlayRuntime` | 缺少 MSYS2 时可生成省略 UxPlay 的测试包；原始无线接收端仍保留 |
| `-OmitMediaOutputRuntime` | 省略内置 FFmpeg；应用仍校验候选 FFmpeg 的固定 SHA-256，任意系统 FFmpeg 不能替代 |
| `-Version` | 显式覆盖此次构建的产品版本；常规构建默认取项目版本 |
| `-TestBuild` | 根据源码、版本记录及已有输出递增 `-testN`，重新整理 `outputs` 并将测试版发布到其根目录；使用前保存需要保留的产物；不能与 `-NoPublish` 或 `-Version` 同用 |

### 桥接器构建来源

默认使用仓库内 `scripts/usb-bridge-recipe`，将当前 `tools/usb_touch_bridge.py` 和
`tools/iostouch` 暂存后打包。不会自动克隆上游最新分支。配方来源及固定提交见
[SOURCE.md](../scripts/usb-bridge-recipe/SOURCE.md)，直接依赖版本见
[requirements.txt](../scripts/usb-bridge-recipe/requirements.txt)。

`IPHONE_MIRROR_USB_BRIDGE_ROOT` 覆盖的是构建配方目录；它必须兼容当前 Python 源码。
上游新 Rust 后端采用不同运行时 schema，不能直接替换。应用当前要求运行时清单
schema `1`，而 `ready` 的应用协议号为 `2`，二者含义不同。

必须一起保留 `iUsbBridge.exe`、`iUsbBridge.runtime.json` 和 `_internal`；只复制 EXE 会
破坏运行时。构建环境位于 `work/usb-touch-bridge-python`，打包产物位于 `dist`。

## 测试入口与覆盖范围

默认构建依次运行 CTest、Python `unittest`、桥接源码暂存测试、三个托管测试程序、
VC Runtime 版本解析和 Apple 支持包验证。`CI=true` 时，脚本明确跳过
`App.Runtime.Tests`；它并非自动检测所有无桌面环境。本地运行 WPF 测试会创建窗口。
本地化校验由 Windows workflow 单独执行，不包含在 `build.ps1` 的测试段中。

若 `ctest` 不在 PATH 中，使用 Visual Studio 随附 `ctest.exe` 的绝对路径运行相同参数；
`build.ps1` 会自动定位它，单独输入 `ctest` 的终端则需要正确的工具路径。

```powershell
./scripts/verify_localization.ps1

# 先完成一次同配置构建，确保原生 DLL 与测试程序匹配
ctest --test-dir build/native -C Release --output-on-failure
dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release
dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj -c Release

# 使用构建创建的 Python 环境，避免缺少桥接依赖
$env:PATH = (Join-Path $PWD 'third_party/libusb/bin/x64') + ';' + $env:PATH
& ./work/usb-touch-bridge-python/Scripts/python.exe -m unittest discover -s tests -p '*test.py'
```

| 测试 | 主要覆盖 |
|---|---|
| CTest | QuickTime/CoreMedia 解析、USB 恢复策略、输出状态、无线/UxPlay IPC 与虚拟摄像头组件 |
| App.Logic.Tests | 设备身份、选择、更新解析、输出参数、媒体及输入路由等逻辑 |
| App.Runtime.Tests | 实际 WPF 加载、窗口交互、本地化、焦点、布局及运行时回归 |
| DriverInstaller.Tests | 驱动识别、错误分类、父驱动确认及恢复策略；模拟测试不等于完成真实驱动变更验证 |
| Python unittest | 触控/键盘消息、桥接安全边界、USBMux 与恢复逻辑 |
| PowerShell 验证 | 暂存源、依赖载荷、签名/哈希和安装器规则等专项检查 |

当前另有未接入默认构建的专项入口，例如：

```powershell
./scripts/test_driver_cleanup_safety.ps1
./scripts/test_cleanup_localization.ps1
dotnet run --project tests/UsbRecovery.Tests/UsbRecovery.Tests.csproj -c Release
```

前两个脚本只载入被测定义并使用模拟调用，不执行实际清理入口。
UI smoke、`Test-WiredRecoveryDevice.ps1`、`Test-WiredControlUia.ps1` 和
`tools/srs-lab/Start-SrsLab.ps1` 属于交互/实机场景，先阅读参数及前置条件，再按改动范围选择。
`reset_driver_test_environment.ps1`、驱动删除脚本和原生 USB 探针可能改变系统或设备状态，
不能当成普通测试批量执行。原生实机探针默认由
`IPHONEMIRROR_BUILD_DANGEROUS_USB_TOOLS=OFF` 排除。

## 打包与版本

```powershell
# 默认从主程序项目读取版本，并要求驱动管理器版本一致
./scripts/package_release.ps1 -GenerateSbom
```

脚本生成 `outputs/releases` 下的 Setup、ZIP、`SHA256SUMS.txt` 和 SPDX SBOM。
不指定旧的 `-Version 1.8.3`，可避免与当前源码版本冲突。`-SkipBuild` 仅适用于已有匹配
载荷；它仍执行版本和完整性检查。构建与打包应使用一致的运行时省略选项。

待上传资产确定后，可加 `-UpdateReleaseManifest` 同步 `updates/releases.json` 中
对应版本的大小与哈希。打包脚本不会代替维护者上传 GitHub Release。
`-AllowVersionOverride` 是显式覆盖，不应拿来掩盖主程序与驱动管理器的版本分歧。

Apple 支持包只在拥有再分发权时显式提供；参数及校验要求见
[驱动依赖说明](DRIVER_DEPENDENCIES.md)。通常公开包不包含 Apple MSI 或 DDI。
发布说明放在 `docs/releases`，根目录变更记录汇总版本；不要把工作区验证结果写成已发布事实。

## 配置、诊断与维护

- 用户设置由 `UpdateSettingsStore` 保存到 `%LOCALAPPDATA%/iPhoneMirror/settings.json`；
  设备绑定位于同目录的 `device-binding-profiles.json`。
- 主程序诊断位于 `%LOCALAPPDATA%/iPhoneMirror/Logs`；驱动 UI 日志位于
  `%LOCALAPPDATA%/iPhoneMirror.Driver/Logs/driver-ui.log`。
- 父驱动快照与提权日志位于 `%ProgramData%/iPhoneMirror.Driver`；快照不等于完整驱动备份。
- 修改 C ABI 或 IPC 时同步两端结构、版本校验和对应测试；原生 API、无线 IPC、桥接运行时
  清单、输入 JSON schema 是不同契约，参见[架构说明](ARCHITECTURE.md)。
- 变更记录注明实际执行的命令、结果及未覆盖的实机环境；脱敏 UDID、配对记录和个人屏幕内容。
