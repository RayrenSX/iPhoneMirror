# 反向控制键盘焦点限制

键盘只发送到当前获得前台焦点、且拥有该设备控制路由的投屏窗口。适用于蓝牙、USB 和无线反向控制，以及主窗口与独立投屏窗口。

## 行为

- 切到其他程序、设置弹窗，或最小化投屏窗口后，暂停键盘输入及控制设备的系统快捷键。
- 失焦时发送空键盘报告，释放手机上的已按下按键；同时清空本地修饰键和 Ctrl+V 拦截状态。释放报告是失焦后的清理操作，不受焦点限制。
- 回到对应投屏窗口后，新输入立即恢复；失焦前仍在等待路由锁、USB/无线写入锁或蓝牙通知队列的输入不会补发。
- 主窗口和独立窗口的输入不能借用对方的焦点。打开独立投屏窗口后，以对应设备的独立窗口为键盘输入目标。
- 蓝牙的系统按键拦截只在控制窗口位于前台时生效。老板键和控制模式切换仍保留原有全局入口。
- Home、控制中心等设备操作快捷键仅在对应投屏窗口获得焦点且控制已启用时注册。失焦后注销这些 Windows 热键，让其他程序正常使用相同组合键；重新获得焦点后恢复注册。

## 实现

键盘 Raw Input 注册去掉 `RIDEV_INPUTSINK`，保留普通键盘消息作为回退路径。统一输入入口在接收事件和取得异步路由锁后分别核验来源 HWND、设备路由与 `GetForegroundWindow()`。

主窗口通过 WPF `Deactivated` 和原生焦点消息触发释放；独立窗口同时处理 `WM_KILLFOCUS`、`WM_ACTIVATE/WA_INACTIVE` 和应用失活。每次键盘重置或路由重置递增输入代次，使旧的排队输入失效。

每次输入捕获只读取原子代次和前台 HWND 的发送校验函数，传入 USB/无线写入层及蓝牙报告队列。各传输在等待内部锁之后、提交报告之前重新校验；失效输入按正常丢弃处理，不当作连接失败。蓝牙键盘缓存也在最终校验后更新，防止读取缓存时取到已丢弃的按键。空键盘报告、消费者键释放和按钮释放不受失焦限制。Ctrl+V 产生的粘贴及多段系统快捷键同样携带校验。

主窗口的 `Activated/Deactivated` 与独立窗口的 `WM_ACTIVATE` 通知负责同步热键注册；更新设备状态及控制模式时也会重新核对。老板键和三种模式切换保持全局注册。

## 验证

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-restore
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-build --no-restore -- --keyboard-focus
dotnet run --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release --no-build --no-restore
```

焦点测试使用可控的前台 HWND 快照，触发实际 WPF 激活/失活事件，执行真实输入处理与路由逻辑。USB/无线报告写入内存，验证前台发送、后台阻断、空报告释放、写入队列中的旧事件丢弃，以及粘贴/按钮请求的失效。蓝牙测试使用真实报告队列并阻塞目标锁，验证失效报告正常丢弃及释放报告放行，不连接 GATT 设备。热键测试直接调用 Windows 注册接口，验证主窗口和独立窗口失焦后的组合键占用已释放，同时保留全局例外。

实机验收：在 iPhone 文本框中输入，切到记事本继续输入，确认手机不再收到文字；按住 Ctrl/Shift 后切走并松开，再切回确认无粘键；分别检查主窗口、独立窗口、最小化以及设置弹窗。实际设备与 Windows 前台切换仍需人工联调。
