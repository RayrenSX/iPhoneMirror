# 圆角窗口与透明浮层复审（2026-11-25）

审查范围是本次圆角外矩形黑底问题及其恢复行为，不代表验证了应用的所有设备、传输和安装功能。

## 发现与补修

`419e30b` 已处理共享 `RoundedWindow`：主窗口的主题后端会向托管子窗口传播 Mica，透明像素下因此仍可能出现矩形背景。

复审逐一枚举应用中的 WPF 窗口类型，确认以下五种透明浮层没有继承该基类，需要同样清除原生背景：

- `KeyboardMappingOverlayWindow`
- `KeyboardMappingOverlayWindow.PickingToolbarWindow`
- `ProtectedContentOverlayWindow`
- `ScreenTextFeedbackWindow`
- `ScreenTextSelectionWindow`

补修复用同一个合成背景恢复方法，在主题刷新、首次挂接、显示、激活和状态变化时恢复浮层透明度。恢复只处理 HWND 的背景效果与合成底色，不修改浮层的内容背景、遮罩或鼠标穿透规则。尚未创建 HWND 和已经销毁的窗口直接返回。

## 验证结果

| 范围 | 结果 |
| --- | --- |
| 主程序全部 38 种 WPF 窗口，含私有键位工具条 | 440 项检查通过 |
| 驱动管理器全部 6 种 WPF 窗口 | 62 项检查通过 |
| 原有深度恢复回归 | 90 项检查通过 |
| OCR 浮层、选择与关闭、文件传输、独立预览菜单 | 通过 |
| 原生投屏 HWND 的手机圆角区域与渲染轮廓对齐 | 8 组尺寸/DPI 检查通过，四角黑带像素均为 0 |
| 独立投屏旋转、手机边框、滚轮命中与圆角刷新 | 通过 |
| 主程序、测试程序、驱动管理器 Release 构建与发布 | 通过 |

目录检查覆盖浅色与深色主题、主题刷新、前后台、隐藏重开、最小化还原，并对可调整大小的透明对话框检查自绘圆角、最大化还原和边缘命中；对原生不透明窗口检查 DWM 圆角偏好。文字选择浮层失去激活后应关闭，按此真实生命周期验证。

检查同时读取 `HwndTarget.BackgroundColor`、`DWMWA_SYSTEMBACKDROP_TYPE` 和 WPF 四角 alpha。仅通过 `RenderTargetBitmap` 截图不足以验证本次问题。

`--ui-regression` 的额外前台焦点断言未通过，未计为成功：测试期间其他桌面窗口取得了前台。关闭日志显示倒计时窗口在关闭前已是 `status_active=False`、`status_foreground=False`，生产逻辑按设计没有抢回前台。当前证据不能用来宣称整套通用 UI 回归通过。

以上运行使用当前 Windows 交互桌面和合成驱动，不包含在 Windows 10/早期 Windows 11 上的实机运行验证。旧版 Windows 的兼容分支已审读，当前设备上无法执行该分支。

## 复现命令与证据

从仓库根目录运行：

```powershell
$runtime = './src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe'
& $runtime --rounded-window-review work/rounded-window-review/app-fixed
& $runtime --rounded-driver-review src/DriverInstaller/bin/Release/net10.0-windows/win-x64/iPhoneMirror.Driver.dll work/rounded-window-review/driver-fixed
& $runtime --rounded-window-recovery work/rounded-window-review/recovery
& $runtime --preview-tools-review work/rounded-window-review/preview-tools
& $runtime --device-corner-clipping work/rounded-window-review/device-corners
& $runtime --independent-corner-profiles work/rounded-window-review/independent-corners
```

目录检查会对程序集中的实际窗口类型进行清点，新增窗口没有测试入口时会报告 `Unreviewed window type`，避免只验证几个代表窗口。JSON 结果与逐窗口 PNG 保存在上述 `work/rounded-window-review` 目录。

更新交付使用当前 `main` 的集成工作区，保留其他未提交修改，仅替换 `outputs/iPhoneMirror-main` 中此次构建的两个界面 EXE。旧 EXE 在 `work/rounded-window-review/backup-*` 下保留。原生投屏与桥接载荷不被此次界面修复替换。
