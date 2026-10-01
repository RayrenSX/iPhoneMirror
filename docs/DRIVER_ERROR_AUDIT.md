# 驱动管理器异常路径审查

审查日期：2026-10-01。对象为当前工作区，包括新加入的父驱动管理、原有采集过滤器操作、Apple 支持安装及强制清理脚本。

初次审查发现 **13 处错误处理缺陷：4 项 P1、9 项 P2**。下方 D01–D13 保留了修复前的触发条件及证据。随后已按用户要求完成代码修复；当前状态见下表。审查和修复验证均未执行真实驱动安装、移除或系统注册表修改。

## 修复状态（2026-10-01）

| ID | 已落实的修复 |
|---|---|
| D01 | 只将全部使用者属于所选手机的驱动包纳入计划；保留蓝牙等所有其他使用者作为排除依据。删除前重新枚举使用者，发现共享则保留。包删除不再传 `/uninstall` 或 `/force`，让 Windows 拒绝仍在使用的包，防止检查后的竞争条件。 |
| D02 | 提权主机的互斥锁移到外层 `finally` 释放，覆盖失败回滚及结果写入。 |
| D03 | 客户端超时通过本次操作专属的命名事件请求取消，主机在安全边界检查并恢复。客户端继续等待主机退出并保持忙碌；不会强杀提权主机。主机也会等待子进程停止写入后才回滚。 |
| D04 | 清理在确认后、重新检查设备之前获取同一全局互斥锁；其他操作进行中则拒绝清理。停止进程逻辑不再包含驱动管理器和提权事务主机。 |
| D05 | 历史设备卸载跳过在线健康检查，仍验证目标过滤器已移除及其他过滤器没有被改变。 |
| D06 | 重连后最多等待 20 秒，按操作检查健康状态、过滤器存在/缺失及指定父驱动绑定。父驱动管理会明确提示重连验证失败。 |
| D07 | 收尾刷新单独捕获异常，保留先前操作结果和日志，并清除忙碌状态。 |
| D08 | 父设备移除仅在 Windows 11 22H2 及以后添加 `/force`；清理逐个移除已确认实例，使用兼容参数。按 PnPUtil 帮助中的命令能力判断是否支持完整包使用者枚举；不支持时明确提示只清理节点、保留驱动包。 |
| D09 | Apple 目录下载的完整请求和正文读取受 45 秒时限控制；安装包的单流、分段及回退下载共用 20 分钟总时限和取消令牌。 |
| D10 | 三种候选来源独立捕获错误、合并去重并展示部分失败诊断；提权执行也允许从成功的来源重新验证选择，原驱动不可读取时不再阻止全部选择。 |
| D11 | MSI 返回 3010/1641 时先返回独立 `RequiresRestart`，不再被就绪检查覆盖；当前管理器会记住此状态，阻止依赖它的安装步骤并提示重启。 |
| D12 | 注册表扫描逐设备/硬件节点隔离访问错误，记录诊断；`FindExact` 校验实例和序列号后直接读取目标，目标读取失败仍明确报错。 |
| D13 | 帮助网站启动失败被捕获，保留原帮助页并提供可手动打开的 URL。 |

驱动修改仍须用户对具体设备和动作确认；签名检查、实例匹配、状态快照与回滚保持有效。操作超时意味着请求在安全边界停止，不代表 Windows 内核调用可以被立即中断；若底层调用不返回，管理器保持等待，不能宣称已取消或恢复。

## 修复后的验证

- `dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj --no-restore`：通过，包含新增 `DriverFailureTests`，覆盖故障码 10/28/31/43、卸载与重连判定、旧版 Windows 参数、候选来源部分失败、坏注册表节点隔离、MSI 重启结果、取消前后父绑定恢复、真实普通子进程超时后的等待，以及目录/单流/分段下载正文停滞取消。
- `powershell -NoProfile -File scripts/test_driver_cleanup_safety.ps1`：通过。模拟共享包、确认后出现共享使用者、蓝牙使用者、无强制卸载参数、Windows 命令能力及事务主机排除；没有执行脚本主入口。
- `powershell -NoProfile -File scripts/test_cleanup_localization.ps1`：通过，1092 个格式化用例。
- `powershell -NoProfile -File scripts/verify_localization.ps1`：通过，三种语言各 196 个驱动资源键，缺失引用 0。
- 驱动管理器和测试项目编译通过，无警告和错误。Release 自包含测试版位于 `artifacts/driver-error-fixes/iPhoneMirror.Driver.exe`。
- 驱动界面回归：60 次渲染，0 失败、0 布局问题、0 绑定诊断，覆盖三种语言和明暗主题。报告位于 `artifacts/driver-error-fixes/ui-audit/driver-results.json`。

尚未执行真实提权驱动修改、真实并发安装/清理、Windows 10 真机或重启恢复。相应路径已做控制流复核或模拟验证，仍需硬件回归。旧复现器仅记录审查时状态；正式回归以 `DriverFailureTests` 和 `test_driver_cleanup_safety.ps1` 为准。

## 问题清单

| ID | 优先级 | 问题 | 主要位置 |
|---|---|---|---|
| D01 | P1 | 选一台手机清理，却删除其他手机共用的驱动包 | `scripts/remove_selected_iphone_drivers.ps1:1472,1610` |
| D02 | P1 | 普通过滤器操作在回滚前释放全局互斥锁 | `src/DriverInstaller/Services/ElevatedDriverHost.cs:165,177` |
| D03 | P1 | 客户端超时强杀提权进程，或未确认终止就结束操作 | `src/DriverInstaller/Services/DriverOperationClient.cs:122,137` |
| D04 | P1 | 强制清理直接杀死仍在修改驱动的提权进程 | `scripts/remove_selected_iphone_drivers.ps1:1486,1514` |
| D05 | P2 | 历史设备卸载被“必须在线且健康”的检查回滚 | `src/DriverInstaller/Services/ElevatedDriverHost.cs:143` |
| D06 | P2 | 重连后只检查存在及过滤器，故障设备可能被报为成功 | `src/DriverInstaller/MainWindow.xaml.cs:583` |
| D07 | P2 | 父驱动操作结束时刷新抛异常，可直接退出界面 | `src/DriverInstaller/MainWindow.xaml.cs:447` |
| D08 | P2 | 父设备重建无条件传 `/force`，不兼容支持范围内的 Windows 10 | `src/DriverInstaller/Services/ElevatedDriverHost.cs:737` |
| D09 | P2 | 下载响应体没有有效期限，可能无限等待 | `src/DriverInstaller/Services/AppleSupportInstaller.cs:583,607` |
| D10 | P2 | 一种父驱动列表读取失败，会丢弃其他可用选项 | `src/DriverInstaller/Services/ParentDriverNative.cs:88` |
| D11 | P2 | Apple 安装器明确要求重启时，仍可能返回安装成功 | `src/DriverInstaller/Services/AppleSupportInstaller.cs:278,293` |
| D12 | P2 | 单个异常注册表设备节点会中止所有设备扫描 | `src/DriverInstaller/Services/DeviceCatalog.cs:41,57` |
| D13 | P2 | 错误帮助页打开爱思官网失败时异常未捕获 | `src/DriverInstaller/Windows/FailureHelpWindow.xaml.cs:34` |

## D01：共享驱动包越界删除

`Get-DriversForPhysicalDevice` 只要发现某个驱动包被选中手机使用，就把整个包放入清理列表，没有检查 `driver.Devices` 中是否还有未选择的设备。随后执行 `/delete-driver <inf> /uninstall /force`。该命令会卸载所有使用此包的设备；界面中“只操作一个物理设备”的说明因此不成立。

**复现：** 用脚本原始映射函数，输入一份 `oem42.inf` 同时被手机 A、B 使用的模拟清单，仅选择 A，结果仍将该共享包选入删除列表。未执行删除命令。

**修复建议：** 在删除前重新获取全部使用者；共享包默认保留，仅移除所选设备节点。需要全局删除时必须单独列出所有受影响设备并确认，不能复用单设备确认。

## D02：回滚不在互斥区内

过滤器安装或卸载发生异常时，内层 `finally` 先执行 `ReleaseMutex()`，外层 `catch` 才调用 `RollBack()`。另一个管理器此时可以拿到同名互斥锁并开始安装，而前一个操作仍在恢复过滤器、停止或删除 libusb0 服务、恢复或删除共享系统文件。

**证据：** 静态异常展开顺序；风险集中在普通过滤器事务。父驱动绑定自身的恢复发生在 `RunParentChange` 内，不能据此认为外层普通事务也受保护。

**修复建议：** 让互斥锁覆盖修改、恢复、恢复验证以及结果落盘的整个事务；最后再释放。

## D03：超时不等于操作安全结束

客户端等待五分钟后调用 `process.Kill(entireProcessTree: true)`。如果调用成功，提权主机没有机会运行自身的恢复逻辑；如果普通权限客户端无法终止管理员进程，则只记录异常，然后仍返回普通失败，UI 随后解除忙碌状态。此时系统修改可能还在继续。

**证据：** 静态确认超时分支没有协作取消、终止状态保证或恢复流程。未对真实提权进程做破坏性验证。

**修复建议：** 由提权主机管理阶段超时和恢复，客户端通过 IPC 请求取消。无法确认终止时返回“仍在运行/状态未知”，保留操作关联和忙碌保护，不能把强杀视作回滚。

## D04：清理脚本会打断其他驱动事务

`Stop-iPhoneMirrorProcesses` 按进程名筛选所有 `iPhoneMirror.Driver` 并执行 `Stop-Process -Force`，只排除本次清理主机及指定父进程。它无法区分普通界面与正在进行安装、绑定或回滚的提权主机，也没有获取 `Global\iPhoneMirror.Driver.Operation`。

**触发：** 一个管理器正在执行驱动操作，另一个入口启动强制清理并确认。清理可能在驱动更新中途终止第一个主机。

**修复建议：** 所有驱动修改入口使用同一事务互斥协议。检测到进行中的操作时等待或明确拒绝清理；不要通过进程名强杀管理员事务。

## D05：历史设备无法完成卸载

UI 的 `CanUninstallDriver` 允许未连接但保存了 libusb0 过滤器的设备执行卸载，提权验证也明确允许 `Uninstall` 的目标不在线。但是卸载后仍无条件调用 `WaitForHealthyTarget(..., 20s)`。不存在的 devnode 不可能满足这个条件；即使注册表中的过滤器已移除，也会在超时后被恢复。

**复现：** 用反射调用现有健康检查，对不存在的测试设备得到 `false`；结合卸载分支确认其必然进入失败路径。未调用卸载工具。

**修复建议：** 对原本不在线的目标验证持久化过滤器状态即可；在线目标才进行运行状态检查，并区分“注册表修改完成”和“需重插生效”。

## D06：重连后的健康检查遗漏

设备枚举已把 `IsPresent` 改为“物理存在”，故障代码 10/28/31/43 的设备现在可以返回 `true`。但 `GuideReconnectAsync` 仍沿用旧假设：安装/修复仅检查 `HasLibUsb0Filter`，卸载及父设备重建则直接返回 `true`，不检查 `IsHealthy`。卸载路径还没有验证重连后过滤器确实不存在。

**触发：** 操作时设备正常，重新插入后驱动启动失败，注册表过滤器仍存在。安装路径可以继续显示已安装，并进入信任提示。

**修复建议：** 重连完成后按操作类型验证健康、目标绑定和过滤器的最终状态。设备重建可以报告“已重新枚举但仍异常”，不能与正常启动混用。

## D07：父驱动窗口收尾刷新异常越过 catch

`OnManageParentClick` 是 `async void`，其外层 `finally` 调用 `RefreshCoreAsync`，只通过内层 `finally` 清除忙碌状态，没有捕获刷新错误。此前 `catch` 捕获不了随后在 `finally` 产生的异常。注册表访问失败等情况会传播到 Dispatcher；应用的全局异常监听仅记录日志，没有将异常标记为已处理。

**修复建议：** 对收尾刷新单独捕获，保留已经得到的驱动操作结果和日志路径，提示状态刷新失败；不要让刷新失败覆盖操作结果或退出界面。

## D08：Windows 10 上的父设备重建参数不受支持

项目声明支持 Windows 10/11 x64，但 `RunPnPRemove` 无条件调用 `/remove-device <instance> /force`。微软将此子命令的 `/force` 标为从 **Windows 11 22H2** 开始支持。Windows 10 及更早 Windows 11 会拒绝不支持的参数，使“重建设备”无法完成；代码还已经把 `removalStarted` 设为 `true`，错误提示可能误导用户认为系统修改已开始。

清理脚本的 `/remove-device ... /force` 同样受影响；其 `/enum-drivers /devices /format xml` 也需单独做版本能力检查，不能假设所有目标 Windows 都有这些扩展参数。

**验证依据：** [Microsoft PnPUtil 命令说明](https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax#remove-device)。未在 Windows 10 真机执行。

**修复建议：** 优先使用可按实例工作的系统 API，或检测命令能力并构造当前系统支持的参数；失败阶段要区分参数拒绝与修改中途失败。

## D09：网络卡住后 UI 可能一直忙碌

Apple 目录下载使用 `ResponseHeadersRead`，后续 `ReadAsStreamAsync`、`ReadAsync` 没有取消令牌。安装包的分段下载调用也没有传入整项操作的取消令牌。`HttpClient.Timeout` 在这种模式下限制到响应头返回，不会为随后手工读取的整个响应体设定期限。

**复现：** 本地模拟服务器只返回成功响应头并保持正文不发送。把 `HttpClient.Timeout` 设为 1 秒后，经过 2.5 秒正文读取仍未结束；关闭模拟服务器才退出。复现器仅为本地响应模拟 HTTPS 最终 URL，不访问外部服务、不修改生产 URL 校验。

**修复建议：** 为目录及安装包下载设置覆盖全部正文读取的总时限，必要时另设无进展期限，并把取消令牌传到每次请求及流读取。向 UI 提供可取消状态。

## D10：候选驱动枚举没有部分成功结果

`ParentDriverNative.Enumerate` 顺序创建系统复合驱动、兼容驱动和当前驱动的列表。任一构造或枚举发生未被分类的 Windows 错误，整个方法抛异常，调用方丢弃之前成功取得的候选项，只显示空列表。

**触发：** 例如系统 usb.inf 读取失败，但原 Apple 驱动列表仍可用；或当前安装记录损坏而系统复合驱动可以读取。需要修复的用户反而无法选择尚可用的驱动。

**修复建议：** 分开捕获各列表错误，合并已成功读取的候选项，同时返回分项诊断；提权时仍重新验证所选候选项。

## D11：Apple MSI 的重启要求被就绪检查掩盖

MSI 返回 3010/1641 后，代码先检查 `ready.Ready` 并可能直接返回成功，只有未就绪时才处理 `IsRestartRequired`。已有 Apple 服务在运行、INF 已存在时，“服务+文件存在”并不能证明新驱动已加载，因此重启要求会被吞掉，快捷安装还会继续后续操作。

**修复建议：** 在安装结果中保留独立 `RequiresRestart`，优先传递 Windows 明确返回的重启要求；停止依赖新驱动立即生效的后续步骤。

## D12：坏设备记录使好设备也无法查找

设备枚举只处理键不存在返回 `null`，没有对单个设备的 `OpenSubKey`、`GetValue` 和驱动类键读取做隔离。某个历史 Apple 节点权限异常，或重枚举期间键被删除引发 I/O 异常，会使整个列表失败。`FindExact` 也通过完整枚举查找一个设备，因此无关坏节点可阻断选中设备的验证与恢复。

**修复建议：** 单节点记录错误并跳过，保持其他结果可用；按实例查找应直接读取目标并保留明确诊断。安全验证失败应阻止该目标修改，而不是中止所有设备扫描。

## D13：错误帮助页自身存在退出路径

`OnOpenAisiClick` 直接 `Process.Start` 打开 URL，没有 `try/catch`。浏览器关联缺失、启动被策略拒绝或 shell 返回错误时，异常直接到 Dispatcher。旁边的 QQ 按钮已有异常处理，爱思按钮遗漏了同等保护。

**修复建议：** 捕获启动失败并在当前帮助窗口展示 URL 和错误原因，保持原始驱动错误信息可见。

## 异常覆盖范围

| 范围 | 已检查的报错类型 / 分支 | 结论 |
|---|---|---|
| 提权及通信 | UAC 取消、启动失败、参数非法、目标不一致、结果缺失/JSON 错误/退出码不一致、超时 | 常规错误有返回；D02–D04 的事务结束语义需优先修复 |
| 设备识别 | 未连接、未启动、问题代码、Apple 元数据端口不可用、注册表键缺失/无权访问 | 连接与健康已分离；D05、D06、D12 尚未正确适配 |
| 过滤器 | 内置资源缺失、哈希不符、签名验证失败、系统文件占用、服务定义不符、安装工具非零退出、健康检查超时 | 大多能返回错误；需修复事务互斥及历史设备卸载 |
| 父驱动 | 确认缺失/陈旧、候选驱动变化、备份失败、绑定失败、恢复失败、重启待生效、设备移除失败 | 已有核心分支及单元测试；D07、D08、D10 是缺口 |
| Apple 支持安装 | DNS/TLS/HTTP 失败、目录解析错误、下载大小不符、来源/签名拒绝、MSI 非零退出、服务启动失败 | D09、D11 可能导致无限等待或假就绪 |
| 强制清理 | 设备/Driver Store 枚举失败、设备断开、确认不匹配、移除失败、驱动包删除失败、待重启 | D01、D04 涉及超出所选设备或进行中事务的影响 |
| 日志与界面 | 日志写入失败、旋转失败、资源及三语键、外部链接、结束时刷新 | 日志大多不阻断操作；D07、D13 会让错误处理过程本身失败 |

签名拒绝、UAC 被取消、权限不足、设备真正断开及 Windows 要求重启，本身属于需要正确展示的外部结果；本报告没有把这些结果本身当作代码缺陷。也没有建议通过关闭签名检查或放宽目标设备验证来消除报错。

## 初次审查时的验证（修复前）

- `dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj --no-restore`：通过。现有测试主要覆盖纯逻辑、保护规则、正常读取与父绑定模拟流程，不覆盖上述全部失败路径。
- `powershell -NoProfile -File artifacts/driver-error-audit/cleanup-shared-package.ps1`：模拟一台被选中、两台共用驱动，返回 `SharedPackageSelectedForDeletion: true`。
- `dotnet run --project artifacts/driver-error-audit/DriverErrorAudit.csproj`：不存在的目标无法通过健康检查；模拟正文停滞超过 HTTP 超时后仍等待。

复现代码在 `artifacts/driver-error-audit`，不修改真实驱动。并发安装、管理员进程中断、Windows 10 执行结果及真实设备重启恢复仍需专门测试环境验证。D01–D13 的修复及后续回归结果见本文顶部。
