# 代码逻辑审查 — 2026-10-02

本次审查发现 7 项可修复问题：3 项 P1、4 项 P2。最应优先处理的是媒体投送音频读取任务的闭包竞态，以及 USB 捕获停止／重启时反控桥接清理不完整的问题。

审查基于 `7ef8afa` 及当前工作区已有的未提交修改，不仅针对 Git 差异。报告之后已完成对应修复，修复回归测试和日志保存在 `work/`。P1 表示应优先修复的主要功能故障或设备生命周期风险，P2 表示特定输入／状态下的功能错误或验证流程阻塞。

| 编号 | 优先级 | 问题 | 证据 |
| --- | --- | --- | --- |
| 1 | P1 | 音频任务捕获随后被置空的进程变量 | 实际程序集、受控线程调度复现 |
| 2 | P1 | USB 设置触发重启时跳过反控桥接清理 | 静态调用链确认，硬件后果待实测 |
| 3 | P1 | USB 捕获停止时遗漏 AirPlay 身份持有的有线反控 | 实际程序集、隔离状态复现 |
| 4 | P2 | 未知 AirPlay 型号被误判为型号不兼容 | 实际绑定管理器复现 |
| 5 | P2 | 大段粘贴超过协议帧上限，终止桥接输入读取 | 实际发送器与 Python 接收器复现 |
| 6 | P2 | 开发者窗口目录数量断言过期，默认运行时测试失败 | 默认测试实跑失败 |
| 7 | P2 | 本地化校验遗漏设计资源，阻塞 CI／发布 | 校验脚本实跑失败 |

修复状态：7 项均已处理。音频进程引用、物理设备反控清理、未知型号归一化、粘贴帧限制和本地化资源集合已修改；运行时目录测试改为校验键集合。新增的 `--logic-review` 回归入口覆盖音频生命周期、粘贴帧边界、跨 AirPlay／USB 身份清理及设置重启顺序。

## 1. [P1] 音频读取任务捕获了随后被置空的局部变量

**位置：** `src/App/Services/MediaCastAudioDecoder.cs:74–76`。

`Start` 将 `process` 传入 `Task.Run` 的闭包后，立即执行 `process = null`。闭包捕获的是变量；当后台任务在这次赋值后执行时，`ReadLoopAsync` 收到的进程已经为 `null`，访问 `process.StandardOutput` 触发空引用异常。

这会中断媒体投送源的音频解码，使依赖该 PCM 数据的录制／推流无法获得源音频。它不证明使用 MediaElement 的本地播放一定无声。失败清理中的 `ReferenceEquals(_process, process)` 也无法匹配，因此 `_process` 仍保留旧引用，直到后续显式停止。

隔离程序加载本次构建的真实应用程序集，先占用工作线程，使读取任务在 `Start` 返回后运行，得到：

```text
AUDIO: media_audio_started; media_audio_ffmpeg_diagnostics_active; media_audio_read_failed error=NullReferenceException
AUDIO: stale_process_reference=True
```

**建议：** 在排队前保存不会再被赋值的 `activeProcess`，让读取任务使用该引用；所有权转移后再清空用于异常清理的局部变量。增加受控调度的回归用例，检查读取任务接收正确进程、异常退出能清理进程状态。

## 2. [P1] 设置触发 USB 会话重启时，没有先停止有线反控

**位置：** `src/App/ViewModels/MainViewModel.cs:6861–6862`，`RestartUsbSessionAsync`。

改变 USB 投屏模式、高级 USB 显示参数，或走到视频设置的重启回退路径时，代码直接调用 `StopMediaOutputForSessionAsync` 和 `_sessions.StopAndDestroyAsync`。普通停止路径在销毁捕获前调用的 `DisableWiredControlForCaptureTeardownAsync`，在此处缺失。

有线反控桥接会占用 QuickTime USB 配置下的 usbmux 接口。反控仍活动时销毁捕获，会让原生层在接口仍被占用的状态下恢复 USB 配置，带来重启失败、额外重枚举或再次出现信任提示的风险。项目自身在同文件 `5241–5247` 行记录了这一顺序约束。

已核对设置入口（`947`、`4630`、`6834`、`6843` 行）、正常停止入口（`4699`、`5276` 行）及会话句柄／独立预览关闭回调；没有发现重启链路中其他隐式停止 USB 桥接的步骤。本项确认的是清理顺序缺失，未连接真机验证上述硬件后果。

**建议：** 将“停止相关反控、停止媒体输出、销毁捕获”的顺序收敛到共享的停止流程，使设置重启也遵守该约束。补充调用顺序测试，并在有线反控活动时用真机切换投屏模式验证。

## 3. [P1] 清理只按投屏身份查找，遗漏同一物理设备的 AirPlay 反控

**位置：** `src/App/ViewModels/MainViewModel.cs:5248–5251`，`DisableWiredControlForCaptureTeardownAsync`。

该方法仅调用 `FindControl(udid)`。反控字典按投屏身份保存控制会话，AirPlay 的键为 `airplay://…`；对应有线桥接实际连接的物理身份另存于 `DeviceControlSession.AppleUdid`。

因此，同一手机以 AirPlay 身份启用有线反控后，如果停止该手机的 USB 捕获，传入的是物理 USB UDID，查找不能命中 AirPlay 持有的控制会话。方法直接返回，桥接仍可能占用捕获正在释放的接口。即使将第 2 项的清理调用补到重启路径，这个查找遗漏仍然存在。

隔离复现构造一个以 AirPlay 身份为键、`AppleUdid` 指向目标 USB 设备的控制会话，调用真实程序集中的清理方法。返回后：

```text
ALIAS_TEARDOWN: ... wired_enabled_after_stop=True; connected=True
```

这验证了清理遗漏；复现使用合成设备身份和内存状态，没有真实 USB 桥接进程，也没有验证硬件重枚举结果。

**建议：** 按待释放捕获的物理 Apple UDID 找到全部相关有线控制会话，同时覆盖以 AirPlay 身份持有的桥接及正在启动／恢复的桥接，等待它们停止后再销毁捕获。补充“USB 捕获 + AirPlay 身份有线反控”的跨身份回归用例。

## 4. [P2] AirPlay 缺少型号时，绑定流程将占位文本当成真实型号

**位置：** `src/App/Windows/DeviceBindingWindow.xaml.cs:240`，`GetFingerprint`。

原生层在 AirPlay 没有提供型号时，将 `product_type` 设置成显示占位值 `"AirPlay"`（`src/Core/src/CoreApi.cpp:196`）。绑定窗口将这个值直接传给 `DeviceFingerprint.ProductType`。`DeviceBindingManager.ValidateCompatibilityUnsafe`（`183–191` 行）将它与 USB 的实际型号，例如 `iPhone15,2`，比较后返回 `Incompatible`。

这样，同一台手机在型号元数据缺失时无法进入预期的“型号未知、由用户确认”路径。`Bind` 在不兼容分支直接失败，传入 `userConfirmed: true` 也不能继续。

真实绑定管理器复现结果：

```text
Success = False, Compatibility = Incompatible, RequiresConfirmation = False
Error = The detected device model does not match; it cannot be bound.
```

**建议：** 构造指纹时将协议／展示占位值归一化为未知型号，或在数据源处区分显示标签与型号字段。应保留真实型号不一致时的拒绝逻辑。回归用例同时覆盖“缺少型号可确认绑定”和“已知型号不同仍拒绝”。

## 5. [P2] 粘贴发送端没有限制序列化后的帧长度

**位置：** `src/App/Services/DirectUsbInputBridge.cs:546–550`，`SendPasteTextAsync`。

发送器接受任意长度文本，直接写入长度头和 JSON 内容；接收器 `tools/usb_touch_bridge.py:1299–1302` 则要求帧长不超过 `MAX_FRAME_SIZE = 4194304`。默认 JSON 编码会将中文字符转义成六字节的 `\uXXXX`，因此字符数不能用于准确判断协议大小。

使用实际发送器发送 700,000 个“中”字，得到 4,200,087 字节的载荷，已经超过 4 MiB。再将这份帧交给实际 Python 接收器，结果如下：

```text
PASTE: chars=700000; serialized_bytes=4200087; receiver_limit=4194304; oversized=True
RECEIVER: result=None; consumed_bytes=4
{"event":"error","code":"bad_frame","message":"frame length must be between 1 and 4194304"}
```

接收端只读了长度头便返回，`read_messages` 结束，帧体留在流中。因此一次大段粘贴会破坏当前桥接输入流程，并可能使发送端等待管道写入。常规 Ctrl+V 路径会将完整剪贴板文本交给该方法，未见更上层的对应长度保护。

**建议：** 在写入任何头部或内容之前检查序列化后的实际字节数，超限时返回明确错误并保留现有桥接连接。若要支持更长内容，需要设计接收端配套的分片和组装协议；直接把一次粘贴拆成多次粘贴可能改变剪贴板／输入语义。测试应覆盖 ASCII、中文和协议边界。

## 6. [P2] 开发者工具目录新增项目后，默认运行时测试仍断言旧数量

**位置：** `src/App.Runtime.Tests/Program.cs:1005`。

`DeveloperToolsWindow` 已新增 `text-input` 项，`WindowItems` 实际包含 26 项，但测试仍断言数量为 25。默认运行时测试稳定失败：

```text
System.InvalidOperationException: Developer catalog WindowItems expected 25, got 26.
```

异常从默认测试入口传播，导致后续检查不能执行。`build.ps1` 在本地默认测试阶段包含该项目，因此本地完整构建验证也会受阻；脚本明确在 CI 环境跳过这个 WPF 运行时项目，不应将本项误报为同一条 CI 失败原因。

**建议：** 同步预期目录，优先验证预期项目键集合及其可创建性，降低仅维护总数的脆弱性。修复后重新运行默认运行时测试，确认当前失败点之后的检查也能通过。

## 7. [P2] 本地化校验未纳入共享设计资源，误报缺少翻译

**位置：** `scripts/verify_localization.ps1:113–116`；驱动安装器的同类逻辑在 `165–168` 行。

资源扫描器会收集 XAML 中的 `DynamicResource` 引用，但用于排除非本地化资源的集合只包含 `App.xaml` 中直接声明的键以及 LightTheme 资源，没有纳入已合并的 `src/SharedUI/Themes/DesignTokens.xaml`。

当前界面使用动态字体尺寸资源后，校验脚本将这些已定义的数值资源误判为翻译缺失，执行退出码为 1：

```text
Missing localization keys: BodyFontSize, CaptionFontSize, FontSize10, FontSize11,
FontSize12, FontSize13, FontSize15, FontSize16, FontSize18, FontSize19,
FontSize20, FontSize21, FontSize24, SecondaryFontSize, SectionTitleFontSize
```

上述键均定义在共享设计资源中。`.github/workflows/windows-build.yml:65` 和 `.github/workflows/release.yml:124` 都执行该校验，因此当前工作区进入这些流程时会阻塞构建／发布。

**建议：** 将共享资源字典纳入应用和驱动安装器的有效资源集合，或解析合并字典后再识别真正缺少的本地化键。不要将数值设计资源复制到翻译字典。修复后重跑校验，确保真实缺失的翻译仍能被报告。

## 验证结果

| 验证项目 | 结果 | 记录 |
| --- | --- | --- |
| App.Logic.Tests | 通过 | `work/review-logic-tests.log` |
| DriverInstaller.Tests | 通过 | `work/review-driver-tests.log` |
| Python 单元测试 | 136 / 136 通过 | `work/review-python-tests.log` |
| 原生 Release 构建 | 通过 | `work/review-native-build.log` |
| 原生 CTest | 11 / 11 通过 | `work/review-native-tests.log` |
| 默认 App.Runtime.Tests | 通过，包含原先被目录断言阻断的后续检查 | `work/logic-fix-runtime.log` |
| USB 恢复生命周期专项 | 通过 | `work/review-usbrecovery-tests.log` |
| 预览指针／键盘焦点／多设备输入隔离专项 | 通过，输入包在内存中检查 | `work/review-pointer-tests.log` |
| 工作区位置和动画回归 | 通过 | `work/review-workspace-tests.log` |
| 驱动清理安全专项 | 通过，模拟验证，无系统修改 | `work/review-driver-cleanup-tests.log` |
| 本地化校验 | 通过，应用和驱动均无缺失键 | 本次执行输出 |
| 逻辑修复专项 | 通过：音频、粘贴帧、跨身份清理和重启顺序 | `--logic-review` 输出 |
| 独立复现程序 | 修复前构建通过；复现第 1、3、4、5 项 | `work/review-repro-build.log`、`work/review-repro.log` |
| Python 协议接收复现 | 确认超限帧结束输入读取 | `work/review-receiver-repro.log` |

隔离复现源码位于 `work/logic-review-repro/Program.cs` 和 `check_receiver.py`，加载真实应用程序集及 Python 接收实现；没有通过复制待审查逻辑来替代实际实现。音频复现使用本机回环地址，绑定数据使用独立文件和合成身份。

## 审查范围与限制

审查覆盖了仓库主要自有代码的逻辑链路：WPF 应用与设备会话生命周期、USB／无线／蓝牙反控路由、身份绑定、媒体输出与更新流程；C++ 捕获、帧传递、IPC／协议、渲染及虚拟摄像头；Python USB 桥接／usbmux；驱动管理和构建、测试、发布脚本。审查结合静态阅读、现有测试和定向复现，未将第三方依赖及生成产物纳入逐行审查。

这不是每一行、每一种并发时序均已验证的证明。没有执行真机 USB／蓝牙输入、真实驱动安装或卸载，也没有完成录制／推流的完整硬件端到端验证。建议在接入真机后继续验证 USB 重启、跨身份反控和录制／推流音频。
