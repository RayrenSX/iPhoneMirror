# 键盘映射修复审查（2026-10-03）

## 修改前的根因与数据流

审查基线：`fd49d48`。已搜索应用与 Core 的键盘事件、Hook、预览、鼠标触控、坐标、设备身份、窗口/Overlay 实现。

1. `MainWindow.KeyboardMapping.MappingFocusAllows` 以 `_mappingWindow is not null` 拒绝所有输入。管理窗口即使不在前台也会永久暂停。主窗口还用 `_isSettingsPanelVisible`、`Keyboard.FocusedElement is null/ButtonBase` 拒绝输入。原生 `NativePreviewHost` 是 HWND；Win32 焦点进入它后 WPF 逻辑焦点可以仍是旧按钮或为空。因此不能以 WPF 的文本焦点推断原生画面的输入归属。
2. 外部应用焦点由 `KeyboardMappingFocusGuard` 的 UI Automation 工作项检查。UIA 缺失、文本/文档控件、超时快照都会拒绝输入；这是外部应用的保护边界，不应成为本应用原生预览的判断依据。现有状态提示把编辑、真正的文本输入与未知外部焦点混为一谈。
3. 捕获已有共用 `WH_KEYBOARD_LL`，并非 TextBox。`ProcessMappingHook` 在 KeyDown 后清空回调并排队执行，编辑器 `Deactivated` 则结束录入；队列没有事务代次，过期回调仍能修改编辑器。普通键的 down/up 有配对，但修饰键/Win 直接放行可触发菜单/失活。`MappedKey.Validate` 更直接禁止 Win。
4. 编辑器 `ShowDialog` 禁用所有者窗口，没有取点事务或预览取点入口；X/Y 只能手填。要回到当前画面必须使用非模态编辑与显式完成/取消回调，而非增加延时或清空状态。
5. 当前无持续映射 Overlay。`MainPreviewHost` 与 `NativePreviewWindow` 是 D3D 原生 airspace，不能直接用普通 WPF Canvas 覆盖。项目已有 `ProtectedContentOverlayWindow` 使用 owned、透明、无激活窗口；映射显示应复用此方式，普通状态穿透，取点时才接收鼠标。

键盘链路：`KeyboardHookProcedure → ProcessMappingHook → KeyboardMappingKeyState → ExecuteMappedGestureAsync → MainViewModel.CaptureMappingRoute → DeviceControlSession.Router → SendRoutedTouchAsync → UsbTouchBridgeHost/DirectUsbInputBridge → iOS touch`。映射只应由 Hook 执行一次；WPF PreviewKeyDown/Up、原生键盘消息及 RawInput 继续走已有键盘管线，通过 `ShouldSkipMappedDeviceKey` 避免同时发送设备原生键。路由绑定已有 `SelectedDevice/DeviceIdentityResolver/DeviceBindingManager`，禁止另建目标连接。

鼠标链路：`NativePreviewHost.PointerInput / NativePreviewWindow.DispatchPointer → HandleUsbPointerInputAsync → MapPointerToNormalized → BluetoothMouseOrientationMapper.MapNormalized → SendUsbTouchAsync`。原函数按真实 client pixel 尺寸计算居中等比视频区、拒绝黑边，之后应用预览旋转和用户方向/反转设置。D3D 有不足一像素的铺满容差，应在共用内容矩形中同步。取点与 Overlay 必须复用同一个双向变换，不能保存窗口像素。

## 验收状态

此文件记录正在进行的修复，不代表端到端验收已完成。原有文档和历史真机结果不作为本次新交互的完成证据。

## 已实现的修复

- 录入采用 `Idle → WaitingForKey → KeyCaptured → PickingPosition → MappingReady`，在松开物理按键后确认。编辑器失去 WPF 激活不会取消全局录入；只有显式取消、窗口关闭或完成按键才结束事务。取消仍会使排队回调的事务代次失效，并保留已经拦截按下事件所对应的松开事件。
- 原生预览采用 Win32 前台 HWND 和子窗口焦点；后台管理窗口不再让整个映射功能永久暂停。真正的编辑窗口、文本输入和其他设备窗口仍拒绝执行。
- 左/右 Win 分别识别。单独松开执行映射并抑制开始菜单；参与组合键时取消候选，重放暂存的 Win 与后续键的按下事件。
- 编辑器为非模态窗口；取点时暂时隐藏管理界面，直接在当前原生视频上点击/拖动，完成或取消后恢复编辑器。八种动作均有取点入口；滑动记录端点和时长。
- 鼠标与取点共用 `PreviewCoordinateMapper` 的视频内容区和既有 `BluetoothMouseOrientationMapper`。新映射保存设备归一化坐标，标记使用同一转换的逆向投影；旧配置保持原有坐标语义。
- owned 透明 HWND 在主预览及当前设备的独立预览持续显示启用的标记。常态无激活、鼠标穿透；取点临时允许输入。编辑/删除/禁用、尺寸/方向、设备/会话变化与停止投屏驱动更新和清理。

## 本次验证证据（持续更新）

| 项目 | 当前结果与边界 |
| --- | --- |
| 构建 | `work/mapping-verified-build` 构建通过，0 警告、0 错误。 |
| 状态/坐标 | 捕获事务、成对事件、Win 组合键策略、iPhone/iPad 竖横屏尺寸、四种旋转、方向/反转和 100/125/150/200% 坐标运算已通过。DPI 项是坐标测试，不代表所有实际显示器配置。 |
| 真实窗口回归 | `--keyboard-mapping-interaction` 在修正原生鼠标进入/按下/松开事件顺序后完整通过并以 0 退出：主 HwndHost、独立原生 shell、全屏、编辑/开关、设备切换、方向、断连恢复、八种动作取点及渲染像素断言。随后完整 `--keyboard-mapping` 套件也以 0 退出，覆盖配置、85 个按键族案例、八种手势、USB/无线封包、编辑器、方向、设备切换和生命周期。此前退出码 1 的原因分别是外部窗口抢前台、测试注入漏发鼠标进入流程，以及 AMD 驱动在未释放真实渲染资源时崩溃；测试清理已补齐。 |
| 真机准备 | USB：iPhone 12 mini / iOS 18.7.8 曾经经现有发现与绑定启动 USB 触控路由，达到 Ready，视频为 1082×2340；最新 USB 探针仍返回无 USB 设备。无线：同一设备曾通过 `--keyboard-mapping-live-interactive-wireless` 发现并启动无线反控，视频为 1082×2340，控制通道达到 Ready；探针现已按传输类型筛选绑定目标，最近一次运行时无线设备暂未被发现。 |
| 真实 J 录入 | 用户在独立键盘诊断窗口确认显示 J。生产 Hook 日志记录 VK 74，Down flags=0、Up flags=128，两事件均被录入事务成对拦截，编辑器进入 KeyCaptured。证据：`artifacts/mapping-capture-diagnostic.log`。 |
| 首次真机录入异常 | 用户在投屏/控制同时运行的初版验收程序中报告 J 未显示。原因尚未证实；发现该测试程序同步编码 PNG 会阻塞 Hook 所在线程，已改为与正式截图命令一致的后台编码，并加入事件/耗时诊断。尚未将此假设写成已确认根因。 |
| Win 与端到端执行 | 左/右 Win 的状态、组合键重放和系统快捷键不注入策略已通过自动化；独立物理 Win 录入窗口已准备，但当前日志尚未收到新的物理 Win 事件。策略测试与人工 J 录入不等于键盘到 iOS 触控闭环通过。 |
| iPad/多设备硬件 | 使用惰性渲染器与不拥有原生资源的会话夹具覆盖设备生命周期；未声称获得 iPad 或多台真机验收。 |

软件注入事件保持拒绝：诊断中注入 J 的 flags=16/144，未被捕获；真实 J 的 flags=0/128 被捕获。测试没有为了通过验收而放开注入过滤。

无线真机交互探针复现：

```powershell
& 'work/mapping-verified-build/bin/IPhoneMirror.App.Runtime.Tests/debug_win-x64/IPhoneMirror.App.Runtime.Tests.exe' `
  --keyboard-mapping-live-interactive-wireless artifacts/mapping-repair-live-interactive-wireless
```

该探针会复用现有设备发现、绑定和无线反控，配置仅保存在 `IsUiPreviewMode` 内存中；退出前会停止无线控制，不会写入生产映射。
