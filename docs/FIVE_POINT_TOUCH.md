# 五点触控

有线控制和无线控制使用同一套五点触控链路。Windows 触摸屏可在主预览、独立预览和全屏画面操作最多五根手指；不同键位映射可并发执行最多五个手势。同一映射未结束时不重入，按住不连发，长按仍按配置的持续时间执行。

每台设备的鼠标/滚轮、触摸屏和键位映射共享五点额度。第六点不挤掉已有触点，也不会在空位出现后因 move 事件自行变成按下；抬起后重新按下才能占用空位。蓝牙 HID 的相对鼠标路径不提供五点绝对触控。

应用保留每根手指的 Windows 接触 ID、来源窗口和独立的控制会话。触点移动分别合并，抬起和重置始终释放原触点。失焦、窗口关闭、设备切换和画面方向变化清理接触；桥接恢复后，旧会话的输入不能进入新会话。触摸产生的兼容鼠标消息被过滤，避免第一根手指再触发一次鼠标点击。

## HID 报告

旧代码的 `0xC2 | slot` 会使 slot 0/2、1/3 碰撞，而且逐点发送单触点报告会让设备抬起报告中省略的其他手指。现在每个输入批次生成一份完整的 58 字节触控报告，包括保持不动的手指和本批次释放的手指；发送成功后才回收释放的 slot。

| 偏移 | 内容 |
| --- | --- |
| 0 | Report ID `0x09` |
| 1 | 本报告的接触记录数量，1–5（包括本次抬起的记录） |
| 2 | 最大接触数 `5` |
| 3–27 | 最多五条 5 字节记录：状态/slot、X u16 LE、Y u16 LE；未用记录置零 |
| 28–39 | 滚动集合，触控路径置零 |
| 40–44 | 按 slot 索引的接触 identity，活动/抬起记录使用 `slot + 1` |
| 45–50 | 48 位 little-endian 单调时间戳 |
| 51–57 | 手势扩展标记，置零 |

记录首字节低 5 位是 slot，bit 5 是 resting，bit 6 是 touch，bit 7 是 range。按下/移动为 `0xC0 | slot`，抬起为 `slot`，slot 为 0–4。`TOUCHSCREEN_STATE_CONTACT/RELEASE` 仍作为调用方的状态常量，编码器只提取接触语义。

格式核对来源：本机缓存 DDI 的 `dtuhidd`（CoreDevice 642.4，SHA-256 `f0441b3626365706e4ee8f235d932b9b759243fd594bafefaca1b79a320087ae`）。`DigitizerContact.index` getter 使用 `& 0x1f`，`touch/range` getter 读取 bit 6/7；`DigitizerReport.contactIdentityOffset` 为 `320 + index * 8` 位，`remoteTimestamp` 从第 360 位开始。`DigitizerGesture` 对比新报告和上一帧，给省略的活动接触生成释放。仅记录格式结论，不分发 Apple 二进制。

## 验证

- `tests/five_point_touch_test.py`：独立解码真实报告字节，检查五个不同 slot/identity、单指移动保留其他四指、局部释放、slot 复用、超限与恢复。
- `App.Runtime.Tests --keyboard-mapping <输出目录>`：USB/无线映射并发、五点上限、重复映射、取消与鼠标混合。
- `App.Runtime.Tests --preview-pointer`：主预览五指事件、独立设备路由、移出画面抬起、排队取消、单设备及多设备有线/无线组合。

软件测试使用内存传输和合成输入；不能替代 Windows 实体五指触摸屏到 iPhone/iPad 的端到端验收。打包时必须同时使用当前应用与当前 `tools/usb_touch_bridge.py` 生成的桥接器，旧桥接二进制仍含旧报告编码。

### 手机端事件验收工具

`tools/five_point_touch_page.py --bind <电脑局域网地址> --output <记录目录>` 提供 Safari 触点观察页，默认端口为 8765。手机在同一局域网打开页面并保持前台。圆圈仅供显示，不参与命中测试，以免移除圆圈时丢失 TouchEvent 的原始目标。

编译 Runtime.Tests 后执行：

```text
IPhoneMirror.App.Runtime.Tests.exe --five-point-live <记录目录> usb <UDID> <python.exe> <usb_touch_bridge.py> http://<电脑局域网地址>:8765
IPhoneMirror.App.Runtime.Tests.exe --five-point-live <记录目录> wireless <UDID> <python.exe> <usb_touch_bridge.py> http://<电脑局域网地址>:8765
```

只在手机显示观察页时运行，会向设备实际发送触摸。工具使用隔离的设备选择状态，连接真实桥接进程后调用生产触点路由、键位状态机和并发执行器。手机回传的原生 TouchEvent 独立核对五点保持、单点移动、独立抬起、超限拒绝、slot 复用和取消释放。该工具不测试 Windows 实体触摸屏、WM_TOUCH 接收或物理键盘钩子；结果 JSON 明确记录这些边界。输出仅记录测试触点与桥接状态，不记录剪贴板文本。测试完毕需停止观察页服务器。

### 2026-10-05 真机结果

设备为 iPhone 12 mini（iPhone13,1），iOS 18.7.8。使用当前 Release C# 代码与当前 Python 桥接源码；Safari 前台观察页独立回传原生触摸事件。有线在 00:36:09、无线在 00:36:30（UTC+8）完成，均通过以下检查：

| 检查 | USB | 无线 |
| --- | --- | --- |
| 五个不同接触 ID 同时按下并保持 | 通过 | 通过 |
| 只移动第三指，其他四指保持、所有 ID 不变 | 通过 | 通过 |
| 第六点被拒绝，不挤掉原有五点 | 通过 | 通过 |
| 单独抬起第三指，保留四点 | 通过 | 通过 |
| 被拒绝的第六点不能因 move 自动恢复 | 通过 | 通过 |
| 重新 down 可复用释放的空位 | 通过 | 通过 |
| 全部抬起，手机事件计数归零 | 通过 | 通过 |
| 五个不同键位映射并发长按，第六映射和同键重入被拒绝 | 通过 | 通过 |
| 取消映射后全部释放，无残留触点 | 通过 | 通过 |

原始证据保存在本机 `work/five-point-live/phone-events.jsonl`、`usb-result.json`、`wireless-result.json`。首次运行识别到五点，但观察页的可命中圆圈被移除后导致部分事件不能冒泡；修复圆圈 `pointer-events: none` 并刷新页面后，两种传输完整通过。首次失败记录另存为 `usb-observer-v1-result.json`，不计入通过结果。

本次未拔 USB 线；无线轮次显式选择 `wireless`，由桥接器连接 Network 设备。没有使用旧打包 EXE。本机无实体触摸屏，Windows 实体五指输入仍未验收；键位输入由测试程序调用生产键位状态机，因此也不将本轮结果表述为物理按键钩子的真机验收。
