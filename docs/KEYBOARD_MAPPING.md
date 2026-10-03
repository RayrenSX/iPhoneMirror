# 键盘映射

## 使用

在「投屏设置」的「反向控制设置」下方打开「键盘映射 → 管理键盘映射」。默认关闭，默认没有任何绑定。

1. 选择当前 iPhone/iPad，开启现有 USB 或无线触控反控，等待设备画面与反控就绪。
2. 点击「添加映射 → 录入按键」，按下一个物理键盘按键。
3. 选择点击、长按、双击、自定义滑动或四方向滑动，填写参数并保存。
4. 启用键盘映射，关闭映射和设置窗口。映射作用于当前选中的设备；切换设备无需重建绑定。

列表更改自动保存；编辑窗口需点击「保存」。每条映射可独立开关、编辑、删除。重复按键提供替换、取消、编辑原映射三种选择。保存失败会回滚，不丢失已有设置。

坐标是当前投屏画面的百分比（0–100%），不是 Windows 桌面像素。滑动方向表示手指移动方向；距离是对应轴长度的百分比。起点、终点必须在画面内，不能相同。持续时间为 50–10000 ms，双击间隔为 40–1000 ms。

## 按键与暂停规则

- 按扫描码和扩展标记绑定物理位置；键盘布局切换不会改变绑定位置。数字、小键盘、方向键、导航键、空格、回车、Tab、Esc、功能键等均可录入。
- 普通按键按下一次执行一次，系统自动连发不重复执行。一次只执行一个动作，忙碌期间不积累动作队列。
- Ctrl/Shift/Alt 单独松开时触发；参与组合键则不触发。修饰键不被映射功能拦截。
- Win 键和 Windows 系统组合键不可绑定。F5/F11 保留给现有刷新/全屏，其他现有单键快捷键也检测冲突。Esc 在全屏时保留退出行为。
- 可选「阻止已映射普通按键的原始 Windows 行为」。默认不阻止；没有可用映射目标时也不拦截。
- 编辑映射、操作设置/子窗口、文本输入、隐藏到老板键模式或无法可靠确认焦点时暂停。外部应用的文本/文档控件采取保守暂停策略；不是所有第三方 UI 都能提供可识别的非编辑焦点。
- 设备断开、切换、重新连接、画面尺寸/方向变化或焦点变化会取消在途动作；清理释放只发送到原控制会话，不发送给新设备/新会话。
- 软件注入的键盘输入不触发映射。

## 后端边界

USB 和无线模式复用现有绝对触控通道。现有蓝牙 HID 仅提供相对鼠标，无法可靠定位指定百分比坐标，因此蓝牙模式不执行这些映射，界面会说明并提示切换 USB/无线。原有蓝牙键盘、鼠标和快捷键保持可用；不会创建另一套连接、协议或校准系统。

## 架构

输入路径：现有 `MainWindow` 全局键盘 hook → `KeyboardMappingKeyState` → 有界 `KeyboardMappingExecutor` → 当前 `DeviceControlSession` / `ReverseControlInputRouter` → 现有 `UsbTouchBridgeHost`。

- 共用已有 `WH_KEYBOARD_LL`，映射关闭后不保留映射监听；录入或原有反控仍需要时，由共用生命周期保留 hook。
- hook 内不查询 UI Automation、不做磁盘或设备 I/O。外部焦点由后台单工作项缓存，原生焦点事件使缓存失效；未知/过期结果拒绝执行。
- 目标复用 `SelectedDevice` 与现有身份绑定，不另建设备管理器。发送前和写锁内校验选中设备、路由代次及桥接代次。
- 使用映射专用触点 ID 2；原有鼠标触点 ID 1，避免相互释放。
- 使用现有 `BluetoothMouseOrientationMapper.MapNormalized`、预览尺寸/旋转、横竖屏方向校正及反转设置，不另建坐标转换器。
- 配置位于现有 `UpdateSettings.KeyboardMapping` / `settings.json`，含版本、总开关、原始键行为选项和映射列表。损坏条目被隔离，映射关闭等待用户检查，不重置其他设置。
- 复用 RoundedWindow、Card、Button、AppPrompt、主题和三语言资源。诊断包含配置、冲突、匹配、目标、执行/取消/失败；高频动作日志按类别限速。

## 验证记录（2026-10-03）

| 范围 | 结果及证据边界 |
| --- | --- |
| 映射配置 | 默认空配置、重启持久化、回滚副本、损坏/重复/未知版本、非法参数自动化通过 |
| 键盘 | 85 个按键族案例；物理位置、扩展键、修饰键/组合键、连发、注入输入与快捷键冲突通过 |
| Windows hook | 真实安装/移除共享 hook，调用实际 native callback 验证录入和成对释放；不是人工敲键端到端验收 |
| 焦点 | 真实 WPF/UI Automation 普通控件与文本框切换、失效恢复和未知焦点保护通过 |
| 动作 | 八种动作、时长、间隔、取消/失败释放、忙碌不排队通过 |
| 路由 | USB/无线真实封包写入内存传输；无设备、切换、断连/重连、排队过期代次、蓝牙能力门控通过 |
| 方向/尺寸 | iPhone/iPad 分辨率、竖屏/左横屏/右横屏、尺寸变化使用现有转换逻辑验证通过；不是 iPad 真机验收 |
| UI | 真实窗口打开、添加/编辑/删除、开关、三种冲突选择、三语言 × 深浅主题 × 缩放通过，PNG 渲染检查 |
| iPhone USB 真机 | 现有发现/绑定/控制启动完成，八种动作实际发送成功；画面确认长按菜单和滑动效果，正常退出 |
| iPhone 无线真机 | 现有 Wireless 控制后端就绪，八种动作实际发送成功；画面确认滑动效果，正常退出。画面采集仍使用 USB，不代表 AirPlay 投屏链路验收 |
| 原有功能回归 | 键盘焦点/快捷键、USB/无线/Bluetooth、多设备与过期输入回归；本地化审计通过 |

真机测试使用状态查找、执行器和生产触控路由。未声称完成物理键盘经操作系统到真机的完整人工验收。用户暂无 iPad，因此 iPad 真机、真机横竖屏切换、AirPlay 设备身份直播链路仍需相应设备/场景补验；蓝牙绝对坐标映射明确不支持。

实机过程中发现，关闭既有 USB 投屏后 Apple 服务偶尔只剩 Network 记录，需重插数据线恢复。未更改驱动/服务处理此独立生命周期问题。

### 复现软件回归

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj --no-restore -v:minimal
dotnet run --no-build --no-restore --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -- --keyboard-mapping artifacts/keyboard-mapping
dotnet run --no-build --no-restore --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -- --keyboard-focus
dotnet run --no-build --no-restore --project src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -- --localization-audit
```

UI 和键盘测试应串行运行，避免相互抢焦点。截图和本地验证配置在被 Git 忽略的 `artifacts/keyboard-mapping/` 下。生产设置不会新增默认绑定。

真机动作测试必须人工确认设备已解锁、信任电脑、开启开发者模式，且当前页面可安全点击/滑动。测试坐标针对当次观察页面，不是生产默认值。不要在任意手机页面直接运行 `--keyboard-mapping-live-actions`；先审查 `KeyboardMappingLiveTest.cs` 中的动作坐标。
