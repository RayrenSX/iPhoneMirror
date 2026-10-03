# 有线 / 无线反向控制绑定检查

## 行为与入口

有线、无线入口不再以“是否存在绑定”决定能否点击。仍保留有效设备、非媒体投送源、设备未移除、
设备未断开、控制未在启停、同一物理设备没有被其他控制会话占用等条件。

原来的限制位于 `MainViewModel.CanEnableUsbControlFor` 的 `GetUsbControlBinding(...) is not null`
和 `CanEnableWirelessControlFor` 的 `Resolve(...).AppleUdid is not null`。主窗口 XAML 的
`CanToggleWiredControl` / `CanToggleWirelessControl` 绑定保持不变；这些属性现在使用不含绑定门槛的入口条件。

工具栏、独立预览菜单、快捷键、旧版 `ToggleUsbControlCommand`、直接启动和重试最终都进入
`StartDeviceControlAsync`。该方法先打开/复用 `ReverseControlStatusWindow`，再执行公共绑定检查；
通过后才调用 `EnableUsbControlCoreAsync` 或 `EnableWirelessControlCoreAsync`。

## 复用与严格校验

复用蓝牙控制原有的 `DeviceIdentityResolver.ResolveProfile` → `DeviceBindingManager.FindByIdentity`
解析链，在解析器中新增 `ResolveControlBinding`。蓝牙的 `GetBluetoothControlBinding` 也使用该方法。

- 当前镜像身份按 Wired 或 AirPlay 类型查找其档案，不按名称猜测，不选取第一个连接的手机。
- USB 与无线触控均使用该档案的 Wired Apple UDID，这是项目原有控制架构；蓝牙使用同档案的 HID 身份。
- 目标身份反查必须属于同一档案；没有档案、缺少对应身份、错误档案都不能通过。
- 每次启动保存档案 ID、来源类型、来源身份、目标身份。后续复查重新读取绑定，并与该快照比较。
- 设备存在性还检查当前设备集合及对象有效性；设备 B 的新请求不会使用设备 A 的状态。

首个状态为 `CheckingBinding`。未绑定时通过原有 `ControlStatusService.Failed` 展示失败标题、
明确的绑定要求、设备绑定器的操作指引，以及原有重试/关闭按钮。失败保持可见，不启动成功倒计时。
提示使用可刷新语言的 `LocalizedText.Join`，覆盖简体中文、香港繁体、台湾繁体和英文资源。

初次校验失败直接返回，不进入前提确认、权限检查、USB/AirPlay 控制桥接、DDI/HID、输入路由初始化。
确认对话框之后、握手锁等待之后、桥接启动返回之后都复查绑定与取消状态。复查异常按原有清理流程释放资源，
并保留绑定指引；不会被转换为通用桥接错误。恢复流程沿用本次会话的绑定快照，不会因改绑而自动控制另一台设备。

## 本次修改文件

| 文件 | 修改内容 |
| --- | --- |
| `src/App/ViewModels/MainViewModel.cs` | 入口条件、蓝牙解析复用、有线/无线初始化与恢复复查、绑定异常提示 |
| `src/App/ViewModels/MainViewModel.MultiControl.cs` | 统一打开窗口、第一阶段校验、失败终止、绑定快照比较 |
| `src/App/Services/DeviceIdentityResolver.cs` | 公共控制目标解析与身份快照 |
| `src/App/Services/DeviceControlSession.cs` | 保存每次控制会话的绑定快照 |
| `src/App/Services/ControlStatusService.cs` | 有线/无线首阶段改为 `CheckingBinding` |
| `src/App/Localization/Strings.zh-CN.xaml` | 绑定要求及操作指引 |
| `src/App/Localization/Strings.zh-HK.xaml` | 绑定要求及操作指引 |
| `src/App/Localization/Strings.zh-TW.xaml` | 为并行新增的台湾繁体资源补充相同提示 |
| `src/App/Localization/Strings.en-US.xaml` | 绑定要求及操作指引 |
| `src/App.Runtime.Tests/ControlBindingTests.cs` | 真实 WPF 窗口、生产入口与状态机回归 |
| `src/App.Runtime.Tests/Program.cs` | 专项测试入口和首阶段断言 |
| `docs/USER_GUIDE.md` | 更新入口与绑定失败操作说明 |
| `docs/CONTROL_BINDING_VALIDATION.md` | 本验证记录 |

共享工作区同时存在其他任务修改；上表只列本次绑定流程修改涉及的文件和内容。

## 验证范围

| 要求场景 | 自动化证据 |
| --- | --- |
| Case 1：有线未绑定 | 生产工具栏按钮启用；点击打开真实状态窗口；仅发生 CheckingBinding/Failed；无桥接或输入资源 |
| Case 2：无线未绑定 | 同上；覆盖 Wired、AirPlay 来源及仅有 AirPlay 身份的未完成档案 |
| Case 3：有线正确绑定 | 精确解析 Apple UDID，进入原有确认和传输初始化；在握手锁处受控停止测试 |
| Case 4：无线正确绑定 | 同上，验证原有无线传输分支；不回退到 USB |
| Case 5：绑定其他设备 | 另一档案不满足当前设备绑定；确认期间改绑也失败；握手等待期间改绑不会调用 Bridge.StartAsync |
| Case 6：设备切换 | A 的进行中请求保留 A；B 单独检查；覆盖设备移除、对象替换、解绑与重连改绑 |
| Case 7：蓝牙 | 原有未绑定失败提示不变；HID 身份仍从精确档案解析；成功/失败/关闭倒计时回归通过 |
| 其他边界 | 重试重新读取新绑定，窗口复用，重复启动和模式切换不覆盖进行中的初始化，无设备/断开/媒体源保持禁用 |

专项使用隔离的临时绑定文件和模拟设备，不修改用户绑定。测试在真实 WPF Dispatcher 上运行，
状态窗口截图已检查。正确绑定场景验证到原有传输初始化及桥接启动前的边界；本次没有进行实体 iPhone 的触控联调。

## 可重复执行命令

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -c Release -o artifacts/control-binding-current-bin
& ./artifacts/control-binding-current-bin/IPhoneMirror.App.Runtime.Tests.exe --control-binding artifacts/control-binding-current
& ./artifacts/control-binding-current-bin/IPhoneMirror.App.Runtime.Tests.exe --reverse-control-countdown
./scripts/verify_localization.ps1
```

当前工作区 Release 构建通过，0 警告、0 错误；绑定专项全部通过，窗口倒计时专项通过，本地化缺失键为 0。
日志分别保存在 `artifacts/control-binding-current-build.log`、`artifacts/control-binding-current-tests.log`、
`artifacts/control-binding-countdown.log` 和 `artifacts/control-binding-localization.log`。

共享工作区完整逻辑测试在无关的工作区动画断言
`workspace panels animate layout width so preview resizing stays continuous` 失败；该动画实现正由其他任务修改。
这不属于本次绑定专项的失败，原始输出保存在 `artifacts/control-binding-logic-tests.log`。
另外，在 `fd49d48` 基线加本次绑定代码的独立工作树中，完整 App.Logic.Tests 通过；
输出保存在 `artifacts/control-binding-isolated-logic-tests.log`。

## 审查后修复：桥接清理期间重试卡住

有线自动恢复检测到绑定失效时，会先显示失败窗口，再异步释放旧桥接。此前在清理尚未结束时点击重试，
启动入口会提前设置 `Starting=true`，但 `SingleFlightOperation` 只返回仍在执行的恢复任务，不执行新的启动回调，
导致清理结束后按钮持续禁用。

- `MainViewModel.MultiControl.cs`：启动状态、模式、绑定快照和首阶段状态的修改移入 `RunAsync` 回调，
  仅由实际执行的启动操作修改；加入已有任务的请求保持会话状态。
- `MainViewModel.cs`：绑定失效后的旧 USB 桥接清理期间保持 `Stopping=true`，同时阻止有线重试与无线切换；
  在 `finally` 中恢复入口可用性，并保留绑定失败指引。
- `ControlBindingTests.cs`：用真实 `UsbTouchBridgeHost` 的受控 reader 任务延迟释放，覆盖实际窗口重试、
  清理期间修复绑定与切换无线、清理后再次重试；另覆盖有线/无线请求加入已有 SingleFlight 任务时状态不变。

修复后的当前源码 Release 构建通过，0 警告、0 错误；绑定专项 8 组、窗口倒计时专项全部通过。
原始复现脚本改为引用本次构建后，结果为 `Starting=false`、按钮启用、`Reproduced stuck retry: False`。
测试使用隔离绑定文件与模拟设备，没有启动实体手机控制。

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -c Release -o artifacts/control-binding-fix-bin
& ./artifacts/control-binding-fix-bin/IPhoneMirror.App.Runtime.Tests.exe --control-binding artifacts/control-binding-fix
& ./artifacts/control-binding-fix-bin/IPhoneMirror.App.Runtime.Tests.exe --reverse-control-countdown
dotnet run --project artifacts/binding-review-probe/BindingReviewProbe.csproj --no-restore -c Release
```

构建及绑定回归日志：`artifacts/control-binding-fix-build.log`、`artifacts/control-binding-fix-tests.log`。
窗口倒计时日志：`artifacts/control-binding-fix-countdown.log`。
原始复现与修复后对照：`artifacts/binding-review-results.log`、`artifacts/binding-review-fix-results.log`。

## 审查后修复：停止镜像等待桥接清理

绑定失效后的恢复清理期间，`Stopping=true` 表示旧桥接仍在释放。此前 `DisableUsbControlCoreAsync`
直接返回，使调用它的 `DisableWiredControlForCaptureTeardownAsync` 提前完成，随后原生镜像停止可能与
USB 桥接释放并发。

`MainViewModel.cs` 现在在该分支取消并等待现有 `WiredOperation`，待恢复任务完成清理后才返回。
重复停止请求仍由 `StopOperation` 合并；停止回调不会等待自身所在的 `StopOperation`。
绑定失败状态和指引继续保留，清理期间重试与切换模式保持受阻，清理完成后恢复入口。

`ControlBindingTests.cs` 新增 6 个组合：USB / AirPlay 来源分别覆盖绑定失败清理、正常恢复、正常停止。
测试调用实际的镜像停止前置方法，验证物理 USB 身份能找到 AirPlay 会话持有的桥接；重复停止及取消请求
均在受控 reader 释放前保持未完成，释放后全部结束，无残留桥接、输入路由或启停标志。

当前源码 Release 构建通过，0 警告、0 错误；绑定专项 9 组全部通过。
独立复现探针结果由 `Capture teardown returned before bridge cleanup: True` 变为 `False`，
清理后 `Capture teardown completed after cleanup: True`，原重试卡住问题仍未复现。
测试不需要实体手机，未验证真机 USB 重枚举或信任提示。

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -c Release -o artifacts/control-binding-stop-fix-bin
& ./artifacts/control-binding-stop-fix-bin/IPhoneMirror.App.Runtime.Tests.exe --control-binding artifacts/control-binding-stop-fix
```

日志：`artifacts/control-binding-stop-fix-build.log`、`artifacts/control-binding-stop-fix-tests.log`。
复现前后对照：`artifacts/binding-stop-review-results.log`、`artifacts/binding-stop-fix-results.log`。
