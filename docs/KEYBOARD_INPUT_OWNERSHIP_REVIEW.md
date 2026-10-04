# 键盘输入所有权与有线控制修复报告

验证日期：2026-10-04。修改保留在当前工作区，未提交或发布安装包。

## 根因

1. 原有映射钩子、Raw Input、WPF 与原生预览回调没有共同的输入所有权。开关变化不能撤销已排队的直发事件；旧 KeyUp、快捷键或粘贴任务可能跨过切换边界。
2. 原生预览是 STATIC 子窗口，点击不会自动取回键盘焦点。工具栏保留焦点时，鼠标发送又复用了键盘编辑控件检查，导致有线控制虽就绪但点击被拦截。已在真实 HwndHost 回归中复现并修复。
3. 有线→无线→有线时，旧 USB bridge 退出丢失活动投屏配置的一次性 usbmux VERSION 握手状态。下一进程重新握手可能无响应。新增仅限同一投屏生命周期的序号交接；恢复候选仍须完成 Lockdown/HID 验证才能 Ready。

## 所有权模型与生命周期

`KeyboardInputRouter` 在窗口 Dispatcher 上仲裁 `None / Mapping / Direct`。工作线程读取代次许可，不自行选择 owner。切换先进入 None 并撤销旧许可，再取消映射手势、等待发送任务和触摸释放、向原会话发送键盘清空，最后开放新 owner。释放失败时保持关闭并记日志。

映射启用后关闭直接键盘 Raw Input，WPF/原生回调及设备快捷键也必须通过同一路由。DirectKeyboardRoute 固定设备、传输、会话及代次，清理不能重新解析到新设备。有线、无线和 Bluetooth 只选一条直发路径。

路由保存当前 owner 的物理按键集合。切换时仍按住的键进入隔离集合，重复 Down 和尾随 Up 不交给新 owner；松开后的下一次完整按键才恢复。左右修饰键统一为物理身份。bridge 取得写锁后再次验证代次，普通空报告也不能跨 owner。显式 releaseAll 取消 Python 异步粘贴并释放其键盘。

焦点丢失、会话变化撤销发送许可；窗口关闭解除预览及焦点订阅。共享低级钩子按需求安装/卸载。日志记录 owner、代次、设备摘要、传输和清理异常。

鼠标独立校验前台窗口及设备归属，不受本地键盘编辑焦点误拦截；点击预览同时恢复 WPF 和原生焦点。

USB 恢复状态只保存在应用内存，限同一设备的原生投屏生命周期。旧进程正常退出后交接，下一进程一次性消费，并校验 serial、bus/address、product、configuration；不保留 TCP 客户端、认证、按键或 HID 服务。原生会话变化立即替换上下文，迟到的旧进程只能写旧上下文。停止期间立即关闭输入并拒绝迟到 Ready。快照在全部清理结束后生成；恢复后的 Lockdown 失败时丢弃候选，仅尝试一次全新握手。

## 核心文件

| 范围 | 文件 |
| --- | --- |
| 输入路由 | src/App/Services/KeyboardInputRouter.cs；MainWindow.KeyboardInputMode.cs |
| 入口、焦点、快捷键 | src/App/MainWindow.xaml.cs；MainWindow.KeyboardFocus.cs；MainWindow.KeyboardMapping.cs；MainWindow.Shortcuts.cs |
| 预览焦点 | src/App/Controls/NativePreviewHost.cs |
| 设备发送路由 | src/App/ViewModels/MainViewModel.KeyboardInput.cs；MainViewModel.KeyboardMapping.cs；MainViewModel.cs |
| 传输与释放 | DirectUsbInputBridge.cs；UsbTouchBridgeHost.cs；BluetoothHidMouseService.cs；KeyboardMappingExecutor.cs；tools/usb_touch_bridge.py |
| USB 会话交接 | src/App/Services/UsbMuxResumeContext.cs；DeviceControlSession.cs；tools/iostouch/qt/usbmux_usb.py |
| 状态展示 | ProjectionSettingsWindow.xaml 与四种语言 Strings.*.xaml |
| 回归 | KeyboardOwnershipTests.cs；KeyboardOwnershipLiveTests.cs；KeyboardFocusTests.cs；MainPreviewPointerTests.cs；映射、快捷键、多设备测试；tests/keyboard_ownership_test.py；tests/usb_recovery_test.py |

当前工作区还保留前续聊天的剪贴板修改和测试脚本；上述为本问题的核心文件。

## 自动验证

- .NET Runtime.Tests 及应用编译通过，0 警告、0 错误。
- --keyboard-ownership：1,000 次快速切换、键族、修饰键释放、重复事件、发送锁等待、旧会话、多传输、映射触摸释放均通过；新增 USB checkpoint 单次消费、停止状态与迟到 Ready 测试通过。
- --preview-pointer：修复前点击无 down/up，修复后有线、无线、双有线、双无线及混合设备点击/拖动/取消/焦点/独立窗口隔离通过。
- --shortcuts：Bluetooth、有线、无线、前后台、独立窗口及多设备回归通过。
- --keyboard-mapping：85 个键案例、八种手势、钩子、捕获、配置、编辑器、主题/语言、缩放、独立预览和重连通过。
- Python keyboard_ownership_test.py：2 项通过；clipboard_sync_test.py：27 项通过；usb_recovery_test.py：41 项通过。
- bridge 打包及依赖/XPC/HID/QUIC/加密/压缩自检通过。
- 完整逻辑测试在前续聊天已通过；直接运行 DLL，避免 dotnet run 注入环境变量干扰更新器防护测试。

焦点单测原先同时注入前台快照并接收真实桌面 Activated/Deactivated，异步等待时会互相干扰。现仅在确定性单测中临时解除真实焦点订阅并直接调用生产处理器；原生预览集成测试前恢复真实订阅。通过记录：pointer-isolated、shortcuts-final、ownership-final。

## 真机证据

设备为已有绑定的 iPhone 12 mini / iOS 18.7.8，1082×2340 投屏。在真实预览点击搜索框并通过桌面输入 q，已观察手机收到 q，证明有线输入可操作。

证据位于 work/keyboard-ownership-review：

| 目录 | 结果 |
| --- | --- |
| live-complete | 有线、无线均收到直接字符；映射和 20 次快速开关未混入 j；Shift 切换后收到小写 b。切回有线失败，揭示 VERSION 状态丢失。 |
| mux-fixed-live | 序号交接后成功有线→无线→有线，回到有线收到 qzx，日志明确 resumed without VERSION，脚本退出 0。本轮映射点击搜索框外，不能将其全部画面认定为互斥通过。 |
| accepted-live | 有线、无线均为 qzx，映射/快速开关仍为 qzx，恢复后小写 b。再次切回有线在序号恢复后的 Lockdown 阶段超时，脚本退出 1；据此补充清理末尾快照及一次重新握手。 |
| final-live | 最终构建成功完成有线→无线→有线，脚本退出 0。三段均直接收到 qzx；映射和快速切换保持 qzx，没有 j；恢复后三段均收到小写 b（无线追加为 qzxb，两段有线替换选中文字）。99-cleared.png 确认测试字符清空。 |

最终证据：final-live/keyboard-ownership-evidence.png，展示三段各四个场景。重复映射点击会选中搜索文本，后续 b 替换选中内容，因此以实际画面比较输入，不按 qzxb 固定字符串断言。final-live-log/reverse-control.log 确认切回有线使用相同投屏生命周期的序号恢复；随后 Lockdown/HID 完成才报告 Ready。最终复验修复了此前的重复 VERSION 和本轮遇到的过早快照问题；41 项恢复测试另外覆盖恢复候选失败后的单次重新握手。

## 尚需明确的边界

输入所有权测试中未发现可复现的跨 owner/跨设备泄漏。物理断线后无法保证设备即时收到释放；本轮映射/快速开关真机输入由测试回调提供，真实设备和传输参与，不能替代全部物理键盘钩子端到端验证。

前期退出投屏出现过 warning_code=-12（未确认 USB 恢复普通配置），部分轮次需拔插。最终 final-live 退出未再记录该警告，退出后独立 usbmux 枚举仍显示 USB 设备。由于本机存在 legacy libusb0 与 Apple 过滤驱动叠加，且此次只完成有限轮次，不能声称所有 USB 配置恢复场景已彻底消除。没有修改系统驱动。

最新桥接目标目录为 work/keyboard-mux-final-bridge，构建日志为 work/keyboard-mux-final-bridge-build.log。早期 keyboard-ownership-bridge、keyboard-ownership-final-bridge、keyboard-mux-resume-bridge 均为中间构建，不应用作最终交付。
