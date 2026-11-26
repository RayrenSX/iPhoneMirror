# 设计与故障研究

返回[文档](README.md)。这里只保留仍能解释实现、复现重要故障或提供独立验证依据的记录。各条记录的日期、设备、版本和结果以原文为准。当前操作见[使用指南](USER_GUIDE.md)，本地 pre7 的范围见[发布说明](releases/v1.8.5-pre7.zh-CN.md)。

## 协议与媒体

- [AirPlay 上游移植依据](AIRPLAY_UPSTREAM_ASSESSMENT_20261009.md)
- [解码与显示设计对照](DECODER_GUI_ANALYSIS.md)
- [AirPlay 视频播放、进度协议与续集交接](diagnostics/airplay-video-playback-20261117.md)
- [原画旋转与方形编码画布调查](diagnostics/original-quality-rotation-geometry-20261118.zh-CN.md)
- [USB 原生画质与横竖屏实测](diagnostics/usb-b-native-quality-20261125.zh-CN.md)
- [有线模式与旧设置迁移验证](diagnostics/usb-mode-ab-migration-20261125.zh-CN.md)
- [虚拟摄像头原画 60 fps 修复与实机边界](VIRTUAL_CAMERA_60FPS_FIX_2026-10-29.zh-CN.md)

## 驱动、连接与稳定性

- [驱动事务、回滚与设备安全审查](DRIVER_ERROR_AUDIT.md)
- [蓝屏触发调用链与证据边界](diagnostics/confirmed-bsod-trigger-steps-20261116.zh-CN.md)
- [有线恢复链路复审与实机结果](WIRED_RECOVERY_REAUDIT.md)
- [API 截图与反控并行稳定性](diagnostics/api-screenshot-stability-20261115.md)
- [空闲鼠标停顿与环境检测锁竞争](diagnostics/idle-pointer-stall-20261125.md)
- [WPF 首次 Grid 布局崩溃](diagnostics/startup-grid-crash-20261116.md)

## 输入、文件与外形

- [键盘路由设计、输入归属与释放](KEYBOARD_INPUT_ROUTER.md)
- [搜狗输入法实机验证范围](SOGOU_IME_VERIFICATION_2026-10-31.zh-CN.md)
- [文件服务域、只读权限及其他项目协议对照](diagnostics/explorer-accessible-20261125.md)
- [映射盘容量与 Windows WebClient 限制](diagnostics/explorer-storage-20261125.md)
- [近期机型圆角与遮罩拟合](diagnostics/recent-iphone-corners-20261117.md)

## 设备兼容性专项

- [Issue #52：Cryptex 与 USB 配置修复验证](issues/52-fix-validation.zh-CN.md)
- [Issue #54：iPad 原生分辨率与协商证据](issues/54-resolution-fix.zh-CN.md)

其他 Issue 直接查 [GitHub Issues](https://github.com/RayrenSX/iPhoneMirror/issues)，版本变化查[发布说明](releases/README.md)。不另存逐条翻译或重复状态表，以免把过期结论误当成现行支持范围。本机日志、交付目录和截图不随仓库分发。
