# 应用内更新审查与验证（2026-10-23）

## 范围与结论

针对“1.8.5-pre 更新到 1.8.5-pre3，下载成功但未完成安装”的反馈，审查了下载校验、部署类型判断、PowerShell 引导、Setup 安装范围、ZIP 替换、退出、重启、取消和错误反馈。

没有收到反馈设备的日志，不能直接认定该用户的机器命中了哪条路径。随后用 GitHub 上未修改的原版发布包完成了实际复现：便携版能成功升级，Setup 版在 UAC 同意后退出码为 1、没有安装日志、旧版仍运行。根因是 64 位 PowerShell 将 32 位 Inno 引导器放在原生 `System32` 目录；引导器重新访问路径时遭遇 WOW64 文件系统重定向。已修复的代码问题有：

1. ZIP 助手只发送窗口关闭消息；主程序启用关闭到托盘时不会退出，助手会等待到 120 秒后才尝试强制退出。
2. 旧流程把 PowerShell 启动成功当作安装交接成功，立即关闭更新窗口；脚本稍后失败时，应用无法显示失败结果。
3. Setup 引导没有检查安装器的退出码；启动失败、取消或安装错误可能被当成成功。
4. Setup 更新总是提权，而且没有固定安装范围和目标目录。原先仅为当前用户安装的应用可能更新到另一处目录。
5. ZIP 准备过程没有交接超时和取消协议。窗口关闭或准备超时后，旧助手仍有机会继续操作。
6. 主程序退出以后，文件复制、回滚或重启失败只会出现在隐藏脚本的错误流中。
7. 更新窗口在 Closing 中释放取消源，无法正确处理被其他处理器取消的关闭；只读预览及关闭后的回调也缺少操作入口保护。
8. 提权 Setup 暂存于 `System32`，导致原版 `pre3` 的 x86 引导器初始化失败，错误为“系统找不到指定的路径”，退出码 1，甚至尚未建立安装日志。
9. 官方备用清单仍只记录到 `pre2`；API 限流时，原版只能发现过时版本。本地清单已按 GitHub 上 `pre3` 的真实发布信息和哈希补齐。

## 修改后的行为

- 下载继续要求有效的 SHA256，保留受信任下载地址和校验文件限制。
- ZIP 先校验、解压并检查必需文件，准备成功后通过命名事件确认；主程序随后调用明确退出入口，绕过关闭到托盘。
- ZIP 准备最多等待两分钟。取消或超时会通知助手，在替换文件前停止；助手在扫描、解压和等待进程期间检查取消事件。
- Setup 保留原安装范围和目录：当前用户安装使用 `/CURRENTUSER`，整机安装使用 `/ALLUSERS`，两者都传递原目录。
- 当前用户更新不主动提权；实际以管理员身份运行的引导仍使用受保护的暂存目录。
- 提权 Setup 改用 `Program Files` 下随机命名、仅管理员和 SYSTEM 可修改的暂存目录，避免 `System32 → SysWOW64` 重定向。没有放宽暂存目录的写权限。
- Setup 使用 `/FORCECLOSEAPPLICATIONS` 完成旧程序的关闭，并观察安装器退出结果。
- 主程序仍运行时，由更新窗口报告失败；主程序已退出时，由助手显示错误详情。
- 取消源在窗口实际 Closed 后才取消和释放。关闭被取消时，下载仍继续；只读和已关闭窗口不启动下载。
- 安装包的二次哈希校验在后台执行，避免阻塞更新窗口的渲染。

## 自动验证

### 逻辑与安全测试

- 完整 App.Logic.Tests：通过，包括下载、镜像切换、校验、ZIP 路径限制、文件替换、失败回滚和部署类型限制。
- `--updater-handoff`：通过，包括失败退出码、缺失交接、等待超时、取消、当前用户安装识别、不完整更新包拒绝，以及主程序退出后的实际错误对话框。
- 默认逻辑测试只运行不依赖交互桌面的交接检查；实际错误对话框检查通过 `--updater-handoff` 显式运行。
- `--updater-elevation-regression`：通过，包括 CLR 环境覆盖拒绝、参数保真、受保护模块路径及助手篡改拒绝。
- `tests/updater_elevation_bootstrap_test.ps1 -DesktopProbe`：通过，包括 Windows PowerShell 5.1 语法、命令解析边界和现有 Explorer 桌面对象查询。

### 实际安装、替换与重启

先以 Release 配置编译 `src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj`，并确保 `scripts/prepare_inno_setup.ps1` 已准备好 Inno Setup 编译器，再用 PowerShell 7 运行 `tests/updater_end_to_end_test.ps1`（Debug 构建需加 `-Configuration Debug`）。测试编译真正的 WinForms 可执行程序和生产 Inno Setup 脚本，再经过生产的 `ShellExecuteEx → Windows PowerShell → 安装器/ZIP 助手` 启动链：

- 旧测试程序把关闭请求转成隐藏到托盘，Setup 完成关闭、文件替换及新版进程启动。
- 当前用户安装保持原目录，不请求 UAC；路径包含中文、空格、单引号、美元符号、括号和分号。
- ZIP 准备前取消、准备完成后取消，均不退出旧程序、不替换旧文件。
- ZIP 收到明确退出后才替换可执行文件，并真正启动更新后的可执行程序；验证新进程标记和文件哈希。
- 安装使用唯一的测试 AppId、App Paths 名称和快捷方式名称，结束后卸载测试实例。测试日志保存在 `work/updater-e2e-*`。

最终验证目录：`work/updater-e2e-890bde80a0bf4bb3a3e4ed798ac22974`，包含 `setup.log`、`uninstall.log`、测试进程日志和便携版的新进程标记。

### WPF 界面

- `--updater-window-regression`：通过，包括只读预览、取消 Closing、关闭后网络请求取消和关闭后的重复回调。
- `--ui-regression`：通过，包括更新窗口、主题和相关控件回归。
- `--tray-mode work/updater-review-20261023-tray`：通过，包括关闭到托盘、明确退出、设置往返、启动更新，以及四种语言和两种主题；渲染证据保存在该目录。
- 主程序及 Runtime.Tests 编译通过，无警告、无错误。

## 验证边界与旧版处理

早期安装测试使用唯一身份的当前用户测试实例。后续用户同意 UAC，补充了真实提权验证：`tests/updater_end_to_end_test.ps1 -ElevatedInstallerBootstrap` 通过，证据目录为 `work/updater-e2e-5ee1b881a4d34e949557e4a2f35ffb42`。其安装日志明确记录原始 x86 Setup 从 `C:\Program Files\iPhoneMirror-Installer-...` 启动，并完成安装和新版进程重启。

## 原版发布包实测

测试目录为 `work/release-update-pre-to-pre3`。文件从 GitHub Release 下载，未以本地工作区构建替代原始资产：

| 文件 | SHA256 |
| --- | --- |
| `iPhoneMirror-Setup-v1.8.5-pre-x64.exe` | `ab7de363ba9ebe48fe6706183d717218eefe789e2b64a803e3c6c2644eafc9f5` |
| `iPhoneMirror-v1.8.5-pre-win-x64.zip` | `1a6a90766f92153c78516783129ddbb5d36ba779c13711310f016971f81fee12` |
| `iPhoneMirror-Setup-v1.8.5-pre3-x64.exe` | `1b99b92418794ae33e31c17feca56f06dec71de28536cb9c4bacffbf95fb925b` |
| `iPhoneMirror-v1.8.5-pre3-win-x64.zip` | `bd8642a0d755246e6703c6313525d79b520ea1216b761b537b8574e26cce99dc` |

### 未修改便携版：通过

从原版窗口点击“立即更新”，使用官方网络下载：

- 17:17:36 下载完成、SHA256 通过。
- 17:17:38 ZIP 助手启动。
- 17:17:39 旧主窗口开始退出，17:17:40 正常结束。
- 17:17:58 新版 `1.8.5-pre3+041ef05168c424800ab038cd95b49f70d256f7ae` 进程启动，17:17:59 完成启动。
- 对 ZIP 中 894 个文件逐一计算 SHA256，与实际安装目录全部一致。

证据为 `portable-inapp-repro-logs/application.log`、`portable-inapp-result.txt`、`portable-inapp-file-verification.json` 和捕获到的 `original-helper-process-62688.json`。

另一次并行运行真实安装器时遇到系统提交内存不足和 WPF `OutOfMemoryException`，此轮不计作升级通过或产品根因。之后关闭测试编译服务器、串行重跑，获得上述有效结果。

### 未修改 Setup 版：复现失败

在隔离目录安装原版 `pre`，实际启动并从窗口下载 `pre3`。17:19:16 下载和摘要校验通过，17:19:18 在用户允许 UAC 后启动提权助手，但旧进程继续运行、文件仍是 `pre`，没有 `installer-update` 日志。

保持原引导逻辑，增加诊断输出后捕获到安装器退出码 1；去掉静默参数后，安装器原生错误窗口为“系统找不到指定的路径”。PE 检查确认发布 Setup 为 `0x014C`（x86），PowerShell 为 `0x8664`（x64）。相同的清理后环境放在普通目录时，真实安装器完成安装并返回 0；将受保护暂存目录改到 `Program Files` 后，也能够创建第二阶段安装进程。

证据为 `setup-original-logs/application.log`、`original-installer-exit-code.txt`、`clean-env-silent.log` 以及生成的诊断引导脚本。诊断回放时曾把助手脚本本身放在被替换目录内，触发自身文件锁；该回放位置不符合正式启动器行为，已排除，不能将该错误归为产品根因。

### Setup 修复验证方法

为验证旧窗口的完整路径，使用原版隔离安装副本，仅修改两处字符串：受保护暂存父目录改为 `Program Files`；安装参数明确 `/CURRENTUSER /DIR=<隔离目录>`，避免碰到本机已有的整机安装。版本仍为 `1.8.5-pre+54c36cad...`，下载、摘要校验、提权及安装器资产均保留原版。测试副本的修改由 `patch-test-copy` 工具执行，原始程序集和前后哈希均保留。

第一次测试副本的参数修饰落在 `/LOG` 引号内，造成无效参数；这是测试工具错误，已修正后重新测试，不计作产品缺陷。修正后的补丁清单保存在 `minimal-patch-corrected-manifest.txt`。

### Setup 修复后的整窗升级：通过

修正测试副本后，从旧版窗口重新检查并点击“立即更新”。一次 GitHub 直连遇到网络超时，界面正确报告下载失败；启用应用自带的下载线路选择后继续测试，没有替换官方安装包或关闭摘要校验。

- 17:48:35 从镜像下载完真实 `pre3` Setup，官方 SHA256 校验通过，提权助手启动。
- 17:48:40 安装日志确认原始安装包位于 `C:\Program Files\iPhoneMirror-Installer-f98d930062f141919c79d8f360e56de1`，并成功创建第二阶段临时安装目录。
- 17:48:41 Restart Manager 请求旧版退出，旧进程正常结束，退出码 0。
- 17:48:52 安装成功，无需重启 Windows。
- 17:48:53 新版进程 PID 50728 自动启动，父进程为 Explorer PID 3756；令牌查询确认 `Elevated=False`，主窗口版本按钮为 `iPhoneMirror v1.8.5-pre3`。
- 主程序、托管程序集及驱动管理器版本均为 `1.8.5-pre3+041ef05168c424800ab038cd95b49f70d256f7ae`。安装覆盖掉了测试程序集修改，最终运行的是未修改的官方 `pre3` 程序。

证据为 `setup-fixed-inapp-retry-logs/installer-update-20261023-094835.log`、`setup-fixed-inapp-result.txt`、`setup-fixed-installed-files.json`、`setup-fixed-new-process-verification.txt` 和 `setup-upgraded-pre3.jpg`。新版从 Explorer 启动后未沿用旧版的日志目录覆盖变量，因此使用进程路径、令牌、文件版本及实际窗口联合确认重启，不以旧日志中是否出现新 PID 为依据。

### 测试清理

隔离的当前用户 Setup 实例已退出并卸载，退出码 0。已逐文件核对并恢复测试前的设置、首次设置状态、绑定资料和 API 密钥。测试创建的 HKCU App Paths 已移除；本机原有 `C:\Program Files\iPhoneMirror` 安装及其 2026-10-16 的卸载记录未修改。诊断窗口及测试进程已结束，文件证据保留在 `work` 目录；详见 `cleanup-result.json`。

本轮验证涵盖真实发布包、真实 UAC 后的提权暂存和当前用户目标目录升级。没有覆盖升级本机原有整机安装，也不代表已经穷尽所有设备、系统和第三方拦截环境。

修复尚未发布，已安装的 1.8.5-pre 不会自动获得新的客户端更新逻辑。受影响用户可以完全退出旧程序（包括托盘）后手动运行 Setup；ZIP 用户退出后解压覆盖。后续现场定位需要 `%LOCALAPPDATA%\iPhoneMirror\Logs\application.log` 以及同目录下的 `installer-update-*.log`。已下载的更新包通常位于 `%LOCALAPPDATA%\iPhoneMirror\Updates\<版本标签>`。
