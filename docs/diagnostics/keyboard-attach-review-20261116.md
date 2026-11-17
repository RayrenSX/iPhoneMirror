# 连接时外接键盘注册与更新审查（2026-11-16）

## 范围与修复

审查 USB / Wi-Fi 反控的键盘注册、请求编号、按键发送、HID 重建、取消与清理。
修改集成在本机 `main` 工作树；完整交付放在 `outputs/iPhoneMirror-main` 的独立子目录，
保留正在运行的旧版程序。

- 系统 service 512 可接受输入，但已测 DDI 的 usage page/code 为 0/0，不能据此认定外接键盘已连接。
  连接阶段创建 usage page 1/code 6 的标准键盘，并在 `ready` 前发送空报告、验证同一 HID 连接。
- 每个 HID 连接申请独立的键盘 ID。更新时先创建和验证新键盘，再采用新连接并关闭旧连接。
  候选连接失败或取消时保留旧键盘；采用新连接后取消旧连接的清理，不会恢复到已关闭的旧连接。
- 请求使用独立的奇数编号序列，修复固定 SDK 接收响应后覆盖发送计数器的问题。
  发送锁只串行化写入，响应读取仍由原有请求锁拥有；等待健康 PING 不阻塞触点和按键释放。
- 512 回退不再上报外接键盘已就绪；首次真实按键发送失败也不会提前上报成功。
  退出时即使按键释放失败，仍尝试移除本连接创建的键盘。
- 修复三个测试会话缺少 `device_hub`、一个运行时功能检查期望过时的问题，补充固定版本的
  [测试依赖](../../tests/requirements.txt)。构建安装测试依赖，打包明确排除仅供边框测试使用的图像与科学计算模块。
- 同步右侧设备面板加入后过时的焦点和布局断言；修复设备管理列表将 `object` 赋给
  `ItemsSource` 的编译错误，保留分组视图与数组共有的 `IEnumerable` 类型。

键盘描述符及 usage 位图与 [idevice 上游实现](https://github.com/jkcoxson/idevice/blob/master/idevice/src/services/core_device/hid.rs)
对照核对；report ID、位图、常量字节与时间戳共 39 字节。

## 已完成验证

- Python 全量 704 项验证通过；键盘专项同时覆盖 USB / Wi-Fi、direct / mediastream、候选失败、
  并发输入、慢响应、取消和重复关闭。
- EXE 内打包的桥接字节码通过 28 项键盘专项测试。
- 原生 CTest 18 项通过。桥接离线运行时与两类 DDI 清单检查通过。
- 应用逻辑、界面运行时、自动化生命周期和驱动安装器检查通过；VC 运行时版本、
  Apple 支持包及提权引导静态检查通过，三个托管测试项目均为零警告、零编译错误。
- 文档索引补齐后，全部本地链接与可达性检查通过；`git diff --check` 通过。

审查期间，同一工作区的设备面板更新还暴露了刷新跳位问题。面板任务集成修复后，
专项界面验证通过，包括刷新保留选中行、624 个应用的分组虚拟化、五种语言与两种主题。
最终重新发布的集成包为 `1.8.5-keyboard-review-r3.20261116`，位于
`outputs/iPhoneMirror-main/keyboard-review-r3-20261116`。交付目录附带 `BUILD-INFO.json`，
记录程序、桥接程序及桥接源码的 SHA-256，交付包中的桥接代码再次通过 28 项专项测试。

原始日志与验证脚本位于本机忽略目录 `work/keyboard-attach-20261116`：
`review-all-tests-r2.log`、`review-build-r3.log`、`review-frozen-r3.log`、
`review-managed-r6.log`、`review-device-renewal.log`、`review-device-renewal-r2.log`、
`review-device-timer.log`、`review-device-cancel.log`。

Windows 更新助手首次运行曾触发一次 15 秒测试超时。未放宽超时或跳过断言；
单独的完整更新交接回归及后续两轮全量应用逻辑检查均通过，暂未复现。

## 实机证据与边界

本轮设备为 iPhone13,1 / iOS 18.7.8，通过 USB 上的 direct HID 连接验证。
两轮探针每轮执行初次挂载、三次立即更新和最终清理，不发送文字或触控，也不关闭用户正在使用的程序。
日志逐个匹配 `BKKeyboardInfo`：每次都先出现新键盘的 `Usable keyboard attached`，
再出现旧键盘的 `Usable keyboard detached`；四个测试键盘最终全部移除，未出现 dtuhidd assertion。

另一次探针在手机已创建键盘、主机尚未收到注册结果时取消请求。
关闭该 HID 所有者后，iOS 正常移除了此测试键盘，没有遗留已连接状态。

使用生产代码且未缩短 150 秒定时器的空闲连接探针也通过。实测 150.47 秒完成更新，
新键盘先挂载、旧键盘后移除；最终清理了两个测试键盘。验证按本次探针的键盘对象逐个匹配，
排除了正在运行的旧版程序自行更新键盘的日志。

以上不能替代具体 App 的验收。QQ 等 App 是否隐藏或恢复软键盘、中文输入法、实际字符输入和粘贴授权，
仍需在交付包上分别测试；连接外接键盘不等于取得剪贴板读取授权。
本轮未执行真实 Wi-Fi 传输或多小时稳定性测试。
