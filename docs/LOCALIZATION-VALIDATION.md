# 多语言审计验证记录

日期：2026-09-30。审计结果见 [完整三语对照表](LOCALIZATION-AUDIT.md)，修改前文字和当前调用位置也包含在该表中。机器可读的修改记录为 [localization-audit-changes.json](localization-audit-changes.json)。

## 最终结果

已修复 429 个确认的问题条目，分类可重叠：缺失翻译 242、错误翻译 4、语义不一致 33、占位符问题 2、术语不一致 85、可读性问题 68。主程序新增 132 个实际使用的资源 Key，驱动管理器新增 16 个；原有资源全部保留。

后续按“修复所有”继续处理验证遗留项：已修复工作区动画回归，消除 5 处可空参数声明警告（原 WPF 两次编译共报告 10 次）。完整逻辑测试和默认运行时套件均已重新通过；这些工程修复不计入上述文案条目数。

| 范围 | 每种语言的条目数 | 结果 |
| --- | ---: | --- |
| 主程序 | 1092 | 简体、香港繁体、英文完整对应 |
| 驱动管理器 | 164 | 简体、香港繁体、英文完整对应 |
| 驱动清理 PowerShell | 88 | 三语完整对应 |
| Inno Setup 有效资源 | 296 | 294 条消息及 2 个语言相关快捷方式 |
| XAML 保留的硬编码值 | 42 | 品牌、协议、数字、符号、单位，三语通用 |
| 启动故障独立回退 | 2 | 三语均存在，不依赖语言字典加载 |
| CMD 清理启动器 | 3 | 产品标题及两条三语异常回退 |
| 代码调用对应关系 | 4 | 香港繁体传输匹配、模式名称、取消说明、清理启动错误 |

完整审计表共 1691 行对应项。51 个主程序 Key 和 7 个驱动管理器 Key 静态未发现引用，全部保留并列明；这不等于已经确认废弃。

## 已执行的检查

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Python 全量资源审计 | 350 个第一方文件；0 错误、0 警告 | `resource-check.json` |
| 原有 PowerShell 资源检查器 | 缺失 Key 为 0；主程序 1093 项含 1 个字体资源；驱动管理器 164 项 | `powershell-verifier.log` |
| 主程序与运行时测试 Release 构建 | 成功，0 错误、0 警告 | `app-build.log` |
| 驱动管理器 Release 构建 | 成功，0 错误、0 警告 | `driver-build.log` |
| 主程序实际 WPF 字典与语言切换 | 17472 次格式化、4 次切换、12 个控制就绪/取消流程通过 | `runtime-localization.log` |
| 驱动管理器实际 WPF 字典 | 1968 次格式化、3 次切换、21 个操作结果翻译通过；未知原始诊断保持完整 | `driver-localization.log` |
| App.Logic.Tests 完整逻辑测试 | 最终通过 | `logic-tests.log` |
| DriverInstaller.Tests | 通过，包含清理脚本完整性及驱动操作边界回归 | `driver-tests.log` |
| Windows PowerShell 5.1 清理文本检查 | 1056 次格式化通过，确认令牌保留 | `cleanup-tests.log` |
| CMD 启动器异常回退 | 在无驱动程序的隔离目录运行；三语均正确输出，退出代码 2 | `outputs-next/localization-audit/launcher-test.log` |
| Inno Setup 安装器编译 | 成功，生成验证用安装程序；未执行安装 | `outputs-next/localization-audit/installer-build.log`、`installer-compile/` |
| 主程序三语 × 两种主题 UI 预览 | 最新代码的 192 个界面，0 构造失败、0 布局问题、0 绑定诊断，含长提示与滚动按钮检查 | `ui-audit.log`、`ui/runtime-results.json` |
| 驱动管理器三语 × 两种主题 UI 预览 | 最新代码的 36 个界面，0 构造失败、0 布局问题、0 绑定诊断 | `driver-ui-audit.log`、`driver-ui/driver-results.json` |
| 工作区位置与动画专项回归 | 通过，包含持续后台 UI 队列负载场景 | `workspace-regression.log` |
| 最后一次默认 App.Runtime.Tests | **全部通过**，保留原有动画、控制状态、主题、窗口及原生预览检查 | `runtime-tests.log` |
| `git diff --check` | 通过；仅有 Git 的行尾转换提醒 | `diff-check.log` |

除表内明确注明旧目录的记录外，本轮日志位于 `outputs-next/localization-fixes/`。

## 后续遗留项修复

1. **窗口动画**：`QueueInitialLightweightWorkspaceFit` 原来使用 `DispatcherPriority.ContextIdle`。持续的背景 UI 更新会让首次尺寸校正迟迟未执行，轻量模式仍保持过大的居中启动窗口；第一次收缩时才把越界的左边缘移动回屏幕。新增忙碌调度队列场景稳定复现了原失败（左边缘从 -46.7 移至 0），随后把首次校正调整为 `DispatcherPriority.Loaded`，确保布局完成后、输入处理前提交窗口位置和尺寸。原有收缩/展开的左边缘不动断言保留。

2. **可空参数警告**：5 个只读原生会话查询已经把空会话处理为“无数据”，但参数声明仍要求非空。将这些参数正确声明为 `NativeSessionHandle?`，与断开期间的查询行为一致；没有使用空值抑制符或关闭警告。新增纯托管回归检查，验证空会话的帧、音频、时间戳和解码器状态都安全返回，不初始化原生运行时。

3. **验证可靠性**：新增 `--workspace-regression` 入口。工作区几何测试移除不参与几何断言的原生视频子窗口，避免为了重复验证尺寸而反复初始化 GPU；默认套件中专门的原生预览检查仍然保留。完整默认套件已以退出代码 0 完成。

最新代码已完成 **228 个界面预览**（主程序 192、驱动管理器 36），覆盖三语、深浅主题、共享圆角窗口以及多档可用窗口尺寸，全部通过。另抽查了香港繁体驱动主页和英文长提示滚动后的实际截图，确认文字清晰、操作按钮可见且可访问。原先 222 个界面的历史快照保留在旧证据目录，本轮结果以 `outputs-next/localization-fixes/` 为准。

## 可复现命令

在仓库根目录执行：

```powershell
python scripts/audit_localization.py --write-report
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify_localization.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_cleanup_localization.ps1

dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-restore
dotnet build src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj -c Release --no-restore
dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release --no-restore
dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj -c Release --no-restore

dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --localization-audit
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --driver-localization-audit src/DriverInstaller/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --workspace-regression
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release

dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --ui-audit outputs-next/localization-fixes/ui
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-build -c Release -- --driver-ui-audit . src/DriverInstaller/bin/Release/net10.0-windows10.0.19041.0/win-x64/iPhoneMirror.Driver.dll outputs-next/localization-fixes/driver-ui
git diff --check
```

安装器编译使用现有验证负载：

```powershell
./work/tools/inno-setup/ISCC.exe /Qp /DMyAppVersion=1.8.4-test4 /DMyNumericVersion=1.8.4.0 /DMySourceDir=C:\Users\Ray\Documents\iphoneMirror\outputs-next\validation-payload /DMyOutputDir=C:\Users\Ray\Documents\iphoneMirror\outputs-next\localization-audit\installer-compile /DMyCompression=none /DMySolidCompression=no ./installer/iPhoneMirror.iss
```

此编译确认 Inno Setup 文案覆盖项与脚本语法可用，并非对本次所有新二进制文件重新打包的正式发布。

## 检查内容与边界

- 核对 XML、重复/空 Key、三语 Key 集合、直接资源调用、L/F 包装调用、XAML 动态资源、枚举拼接状态资源、占位符、文件过滤器、替换字符、安装器快捷键、清理脚本资源引用与哈希。
- 使用实际编译后的 WPF 字典和 .NET 格式化器检查格式，分别覆盖 0、1、2、5；主程序按简体 → 香港繁体 → 英文 → 简体切换，检查字典替换后不会读取旧文本。
- 人工结合调用位置核对状态、技术术语和信息完整性；静态扫描辅助核对，不将正则扫描宣称为能证明所有任意运行时字符串都已被穷尽。
- 安装器使用本地 Inno Setup 语言包叠加项目覆盖项。允许可选备注/标签为空，以及不同语言自然存在的句式和分段差异；保留产品名、系统 UI 引用、协议与键名。
- PowerShell 测试只解析 AST 并提取文本目录及纯格式化函数，未执行清理主体；CMD 测试目录经过检查，不含驱动可执行文件。没有卸载驱动、改变配对、执行安装器或操作真实手机。
- 未进行实体设备上的 USB、AirPlay、蓝牙、录制或恢复端到端测试；原生库、系统及 FFmpeg 返回的原始诊断保留原文。新增文本使用实际发生的状态和错误入口，没有新增语言或功能。
