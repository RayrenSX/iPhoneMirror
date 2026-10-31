# 搜狗中文模式键盘转发实机验证

关联：[Issue #72](https://github.com/RayrenSX/iPhoneMirror/issues/72)。

## 环境

- Windows 11，系统版本 `10.0.26300.0`；当前工作区 `1.8.5-pre4`。
- 从[搜狗官网](https://shurufa.sogou.com/)下载安装 `16.10.0.5858`，安装程序退出码为 `0`。
- 安装包数字签名有效，签名者为 `Beijing Sogou Technology Development Co., Ltd.`。
- 安装包 SHA-256：`59B62D027F1B478854FCEDD6FE5C50710EAC05FBDD00A468A073A1A400D66E9A`。
- iPhone 12 mini，iOS `18.7.8`，真实 USB 投屏及反控，视频 `1082×2340`。
- Windows 已选择“搜狗拼音输入法”，中文模式打开；预览焦点下记录到 `ime_open=True`、`conversion=1`。

## 方法与结果

使用 `SendInput(KEYEVENTF_SCANCODE)` 产生按下/松开事件，经真实 Windows 输入栈和生产 `WH_KEYBOARD_LL` 钩子、路由及 USB HID 发送到手机。测试不直接调用路由回调，也不以 `VK_PACKET`、Unicode 注入或剪贴板粘贴代替实体按键事件。

| 场景 | 观察结果 | 结论 |
|---|---|---|
| 搜狗中文模式，手机 App 资源库搜索框输入 `abc123` | 手机搜索框准确显示 `abc123`；日志记录六组按下/松开，均由 Direct 路由处理 | 通过 |
| 接着退格、左方向键、Shift+A | 手机搜索条件变为 `abc1A2`，修饰键与扩展键均有对应松开事件 | 通过 |
| 清空后输入 `nihao` | 手机搜索框准确显示 `nihao` | 通过 |
| 在电脑独立普通输入框输入同样的 `nihao` 扫描码，再按空格 | 出现真实搜狗候选框，首选“你好”；提交后文本为“你好” | 通过，确认扫描码确实能够经过搜狗中文组合输入 |
| 从电脑输入框切回手机预览，再输入 `abc123` | 手机准确显示 `abc123`，没有残留修饰键或重复字符 | 通过 |

电脑普通输入框中的中文组合没有泄漏到手机。实机测试结束后清空了手机搜索词，并关闭测试窗口和控制会话。

## 证据

本地运行记录保存在 `work/sogou-ime-live-20261031/`（不上传手机画面）：

- `live-session.log`：真实按键虚拟键、扫描码、按下/松开、路由、输入法状态和清理记录。
- `local-control.log`：搜狗实际提交“你好”的对照结果。
- `physical-sogou-chinese-abc123.png`：手机收到字母和数字。
- `physical-sogou-editing.png`：退格、方向键与 Shift 的编辑结果。
- `physical-sogou-chinese-nihao.png`：手机收到 `nihao`。
- `physical-sogou-after-refocus.png`：重新获得焦点后的输入结果。

仓库提供 `App.Runtime.Tests --keyboard-ime-live <绝对输出目录>` 入口观察生产输入链路。需要真实已绑定设备及完整原生运行库；使用测试程序自身的 `.exe` 启动，以便原生核心找到同目录的 `iPhoneMirror.UsbConfigurationSwitch.exe`。实机测试窗口只在前台预览内注入预定义测试扫描码，切换到其他窗口时拒绝注入。

## 范围

本次实机验收覆盖搜狗当前版本、主预览窗口与 USB 反控。按键由自动化扫描码注入产生，并非人工按实体键盘；蓝牙、无线及独立窗口仅有此前的模拟事件回归结果，未将其写成搜狗实机通过。

当前 iPhone 配置英文和简体手写键盘，因此实机画面证明的是电脑中文模式下按键正常转发；电脑普通输入框的对照另行证明搜狗中文组合有效。本次没有验证手机拼音候选选字，也没有验证所有第三方输入法版本。

实际搜狗测试中生产钩子收到普通虚拟键；`VK_PROCESSKEY` 还原分支由定向回归测试覆盖。不能据此声称已在当前搜狗版本上复现旧版故障。
