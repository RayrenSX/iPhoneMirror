# 多设备控屏

可以同时保持多台 iPhone/iPad 的 USB 或无线反控连接，通过主界面设备列表或独立投屏窗口的焦点选择输入目标。

## 使用

1. 连接设备，分别启动投屏，并按设备完成信任、开发者模式和反控准备。
2. 从设备列表分别选择设备并启用有线或无线反控。已有连接在切换设备时保持。
3. 切换设备后，主界面画面、反控按钮与输入目标跟随所选设备。
4. 也可以为多台设备打开独立投屏窗口，在窗口菜单中分别启用反控，点击要操作的窗口即可切换。独立窗口焦点不修改主界面的设备选择。
5. 有线/无线反控快捷键作用于当前投屏窗口对应的设备。设备操作快捷键仅在投屏窗口位于前台时生效。

输入发送到当前操作的设备，不会广播到全部设备。切换焦点会释放旧设备上的按键、拖动和滚轮手势；切到其他应用后暂停键盘转发。关闭某一设备的反控、断开或恢复该设备，不应中断其他设备的连接。

蓝牙 HID 仍使用一条蓝牙控制连接，可与其他设备的 USB/无线反控连接共存。它不提供多台蓝牙设备同时建立独立 HID 控制连接的能力。同一台 Apple 设备的不同投屏别名不能各自占用一个反控桥。

## 实现与验证

`DeviceControlSession` 按投屏设备 UDID 保存桥接器、启动/停止任务、恢复状态和进度服务；异步操作捕获设备会话，不再依赖之后的主界面选择。Apple UDID 用于防止重复占用同一物理设备。Lockdown 握手保持串行，建立后的连接独立运行。

`MainWindow.KeyboardFocus.cs` 管理焦点路由；输入携带来源设备和 HWND，发送队列中的旧键盘、点击、拖动和滚轮事件在实际写入前通过代次校验失效，释放报告仍可发送。共享路由锁只保护输入状态更新，不等待设备写入；一台设备的发送阻塞不会阻止另一台设备的键盘输入。后台设备迟到的释放事件也不会清除前台设备的排队输入。蓝牙启动快捷键使用当前投屏窗口对应的设备。剪贴板回传跟随最后选中的控制设备，窗口失焦后仍后台同步；其他设备内容按设备缓存，切换时仅补写尚未被新 Windows 复制覆盖的更新。进度窗口按状态服务区分，取消和重试保留原设备。

验证入口：

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-restore
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-build --no-restore -- --keyboard-focus
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-build --no-restore -- --preview-pointer
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-build --no-restore
dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release --no-restore
dotnet run --project tests/UsbRecovery.Tests/UsbRecovery.Tests.csproj -c Release --no-restore
./scripts/verify_localization.ps1
```

多设备测试使用两份独立的内存桥接输出和真实 HWND，覆盖设备列表、键盘目标、修饰键释放、单台设备写入阻塞、失焦及重新聚焦后的旧点击/拖动/滚轮丢弃、后台设备迟到清理、独立窗口蓝牙快捷键目标、恢复隔离、状态服务隔离和停止隔离；不向真实设备发送输入。

双机验收仍需真实 iPhone：分别启用两台设备的反控，在两台文本框之间切换输入、拖动时切换窗口、断开并恢复其中一台、关闭其中一个窗口，再确认另一台画面与输入持续可用。
