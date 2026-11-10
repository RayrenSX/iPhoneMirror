# 设置助手与独立工具

设置助手负责偏好设置、分步引导和进度保存。驱动安装和身份绑定分别由正常的驱动管理器、设备绑定器完成。

## 操作流程

- 打开助手直接显示设置页面，不再检查或列出未完成设置。首次进入欢迎页；未完成时恢复上次步骤；已完成后手动重新打开则从欢迎页开始，保留实际偏好和设备绑定。
- 环境步骤打开驱动管理器；设备绑定只显示一页，在同一个设备绑定器中管理 USB、AirPlay 和蓝牙，每台设备只需进入一次。控制方式介绍页只展示说明。
- 驱动管理器使用当前语言和主题启动，已有窗口会被激活。用户在工具中选择设备、安装或修复；关闭工具后助手执行只读检查。需要重新插拔或检查失败时，当前步骤保持未完成。
- 取消等待或退出助手不会结束驱动管理器进程。驱动管理器操作中不能关闭，以免提前结束安装；USB 插拔事件合并后触发重新检测。
- 设备绑定器独立显示档案列表以及 USB、AirPlay、蓝牙连接卡片。助手会传入当前设备，绑定器在助手暂停常规发现时自行刷新设备列表。
- USB 自动建档；打开绑定器即自动启动 AirPlay 接收，启动期间仍可管理设备。手机投屏后实时出现在绑定列表中，已保存的设备直接选中对应档案。侧栏显示接收状态，启动失败时提供重试；缺少组件时打开现有下载窗口。蓝牙配对继续使用现有独立连接窗口。
- 绑定器保留尚未提交的设备选择；本次窗口中解除的 USB 绑定或删除的档案不会被周期刷新立即重新创建。保存失败会显示错误，关闭窗口后助手读取磁盘上的实际结果。
- 蓝牙配置始终在 finally 中停止，关闭请求会等待配置清理。助手在绑定器设备发现任务退出后才恢复检查。
- 无线设置只采用在绑定器中选中的档案；历史设备不会自动加入本次配置。返回已保存的绑定步骤可直接继续；无线和蓝牙可稍后添加。旧版分开的绑定进度恢复到合并后的设备绑定页。
- 绑定、重命名和删除的确认弹窗固定操作目标；设备在确认期间断开时，绑定不会提交。
- 刷新使用稳定的设备集合，保留下拉框选择和键盘焦点，设备断开后清除过期选项。
- 首次进度保存失败仍显示欢迎页和错误提示；操作结果保存失败会回滚当前向导状态。
- 独立工具返回后仅验证当前操作结果，并保留跳过和进度恢复。打开窗口、关闭窗口和跳过都不是配置成功的依据。

## 界面与动画

驱动管理器采用组件状态卡片、设备选择和明确的主操作；设备绑定器使用左侧档案、右侧连接卡片，并提供空状态、操作中和保存反馈。文字与状态支持简体中文、繁体中文（香港）、繁體中文（台灣）、英文和日文，浅色和深色主题共用设计资源。

新窗口及设备切换使用 300 ms 淡入和 10 px 位移，分组间延迟 40–100 ms；工具入口使用 160 ms、2 px 悬停反馈。新过渡不缩放文字、不动画布局尺寸，重复触发替换旧动画，卸载时释放动画，遵循 Windows 关闭动画的设置。

周期刷新和修改当前档案不会重新播放右侧入场动画；动画键按身份值比较，避免相同 GUID 重新装箱后引起闪烁。

## 验证

以下命令从仓库根目录、无 CLR/.NET 注入变量的终端运行；环境说明见[开发指南](DEVELOPMENT.md#测试入口与覆盖范围)。

```powershell
dotnet build src/App.Runtime.Tests --no-restore
dotnet build src/DriverInstaller --no-restore
dotnet build src/DriverInstaller.Tests
dotnet ./src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --setup-tools outputs/setup-tools-qa/tools
dotnet ./src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --setup-assessment outputs/setup-tools-qa/assessment
dotnet ./src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --first-run-setup outputs/setup-tools-qa/first-run
dotnet ./src/App.Runtime.Tests/bin/Debug/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.dll --driver-ui src/DriverInstaller/bin/Debug/net10.0-windows/win-x64/iPhoneMirror.Driver.dll outputs/setup-tools-qa/driver
dotnet ./src/DriverInstaller.Tests/bin/Debug/net10.0-windows10.0.19041.0/iPhoneMirror.DriverInstaller.Tests.dll
```

界面验证使用隔离档案和预览模式，不安装真实驱动、不修改真实配对。真实 UAC 安装、手机信任与蓝牙配对仍需硬件验收。
