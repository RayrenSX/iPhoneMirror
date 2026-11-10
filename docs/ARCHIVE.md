# 审计与设计记录索引

返回[文档索引](README.md)。以下记录按主题汇总，保留原审查日期、工作区和设备条件。
它们包含历史问题、修复过程、设计决策及当时的验证结果，不是当前版本的统一使用说明。
当前操作先查[使用教程](USER_GUIDE.md)，实现入口查[架构](ARCHITECTURE.md)。

原记录中的未完成项、测试数量、性能数据及本机产物均按原范围解释；本次目录整理未重新执行这些测试。
本地证据路径不随仓库分发，原行号可能因后续修改发生变化。Issue 分项见[问题记录](issues/README.md)，
版本变更见[发布说明](releases/README.md)。

## 文档、代码与 Issue 审查

- [设置与本地主线复审（2026-11-10）](SETTINGS_MAIN_REVIEW_2026-11-10.zh-CN.md)
- [文档统一完善记录（2026-11-05）](DOCUMENTATION_AUDIT_2026-11-05.md)
- [代码逻辑审查 — 2026-10-02](CODE_LOGIC_REVIEW_2026-10-02.md)
- [全仓库代码审查与功能探索（2026-10-11）](CODE_REVIEW_2026-10-11.zh-CN.md)
- [再次审查（2026-10-13）](CODE_REVIEW_2026-10-13.zh-CN.md)
- [全仓库代码审查（2026-10-15）](CODE_REVIEW_2026-10-15.zh-CN.md)
- [项目与文档核对记录（2026-10-01）](DOCUMENTATION_AUDIT_2026-10-01.md)
- [GitHub 用户问题审查报告](GITHUB_ISSUE_AUDIT_2026-09-23.md)
- [GitHub 全量 Issues 修复核查（2026-10-01）](GITHUB_ISSUE_AUDIT_2026-10-01.md)
- [GitHub Issues 修复跟进（2026-10-01）](GITHUB_ISSUE_FIXES_2026-10-01.md)
- [近期 issue 源码修改记录（2026-10-15，范围调整后）](GITHUB_ISSUE_FIXES_2026-10-15.zh-CN.md)
- [近期 GitHub issue 逐项代码审查报告（2026-10-15）](GITHUB_ISSUE_REVIEW_2026-10-15.zh-CN.md)
- [Issue #64：蓝牙输入切换与独立窗口拖动](ISSUE_64_BLUETOOTH_CONTROL_REVIEW_2026-10-23.zh-CN.md)

## 反控、DDI 与连接恢复

- [有线 / 无线反向控制绑定检查](CONTROL_BINDING_VALIDATION.md)
- [有线/无线全链路再审查](CONTROL_CHAIN_REAUDIT.md)
- [无缓存真机、错误回退与复审记录](CONTROL_DEVICE_CLEAN_REVIEW.md)
- [用户环境差异补充审查](CONTROL_ENVIRONMENT_REVIEW.md)
- [有线／无线控制全流程审查（2026-10-17）](CONTROL_FLOW_AUDIT_2026-10-17.zh-CN.md)
- [有线 / 无线控制全链路复审（2026-10-07，第二轮）](CONTROL_FULL_CHAIN_REVIEW.md)
- [有线与无线控制链路审查 — 2026-10-01](CONTROL_LINK_AUDIT_2026-10-01.md)
- [全链路审查：Lockdown 握手与取消边界](CONTROL_LOCKDOWN_REVIEW.md)
- [有线/无线控制全链路审查状态](CONTROL_REVIEW_STATUS.md)
- [有线与无线控制全链路复审（2026-10-07）](CONTROL_TRANSPORT_REVIEW.md)
- [双设备干净环境与故障回退审查](CONTROL_TWO_DEVICE_REVIEW.md)
- [Cryptex 准备修复与复审（2026-10-15）](CRYPTEX_PREPARATION_FIXES_2026-10-15.zh-CN.md)
- [Cryptex 准备全流程审查（2026-10-15）](CRYPTEX_PREPARATION_REVIEW_2026-10-15.zh-CN.md)
- [DDI 回退链补充审查（2026-10-17）](DDI_FALLBACK_AUDIT_2026-10-17.zh-CN.md)
- [DDI 启动卡住修复（2026-10-07）](DDI_STARTUP_REPAIR.md)
- [pre3 反控准备失败排查（2026-10-16）](PRE3_DDI_REGRESSION_2026-10-16.zh-CN.md)
- [有线投屏清理后的自动 USB 重连](USB_AUTO_RECONNECT.md)
- [USB 反控打包与 Apple 签名错误核查](USB_BACKEND_PACKAGING_REPAIR.md)
- [Wired Control success-window countdown investigation](WIRED_CONTROL_COUNTDOWN_FIX.md)
- [Wired reverse-control recovery investigation — 2026-10-01](WIRED_RECOVERY_FIX.md)
- [有线反控重连延迟优化 — 2026-10-01](WIRED_RECOVERY_LATENCY.md)
- [有线反控断联与自动恢复复审 — 2026-10-01](WIRED_RECOVERY_REAUDIT.md)
- [有线投屏画面停顿日志排查（2026-10-18）](WIRED_STUTTER_LOG_REVIEW_2026-10-18.zh-CN.md)

## 输入、键位映射与焦点

- [键盘输入所有权与有线控制修复报告](KEYBOARD_INPUT_OWNERSHIP_REVIEW.md)
- [Keyboard Input Router：审计、实现与验证](KEYBOARD_INPUT_ROUTER.md)
- [键盘映射修复审查（2026-10-03）](KEYBOARD_MAPPING_REPAIR.md)
- [键盘映射向导重构](KEYBOARD_MAPPING_WIZARD.md)
- [搜狗中文模式键盘转发实机验证](SOGOU_IME_VERIFICATION_2026-10-31.zh-CN.md)

## 界面、布局与本地化

- [滚动页对称性、关闭按钮与字号复核](BALANCED_LAYOUT_TYPOGRAPHY_2026-10-28.zh-CN.md)
- [全项目界面、资源与交互审查](FULL_UI_RESOURCE_AUDIT_2026-10-28.zh-CN.md)
- [主界面拖动与蓝牙控制稳定性审查](INTERACTION-STABILITY-AUDIT.md)
- [多语言文字一致性审计表](LOCALIZATION-AUDIT.md)
- [多语言显示复查（2026-10-01）](LOCALIZATION-RECHECK.md)
- [多语言审计验证记录](LOCALIZATION-VALIDATION.md)
- [全项目滚动条位置检查](SCROLLBAR_LAYOUT_AUDIT_2026-10-28.zh-CN.md)
- [文字、控件自适应与裁切复审](UI-ADAPTIVE-LAYOUT-AUDIT.md)
- [iPhoneMirror UI 一致性审计报告（2026-09-30）](UI-CONSISTENCY-AUDIT.md)
- [UI、性能与一致性复审](UI-PERFORMANCE-CONSISTENCY-REVIEW-2026-10-01.md)
- [弹窗与子窗口圆角核对](UI-ROUNDED-WINDOW-AUDIT.md)
- [UI 界面规范复审（2026-10-01）](UI-STANDARDS-REVIEW.md)
- [iPhoneMirror 全量 UI 审查报告](UI_AUDIT_2026-10-02.md)
- [UI 审计覆盖清单](UI_AUDIT_COVERAGE.md)
- [iPhoneMirror UI 修复记录](UI_FIXES_2026-10-02.md)
- [UI 再审查与修复](UI_SECOND_REVIEW_2026-10-28.zh-CN.md)
- [UI 审查缺项补验记录](UI_VALIDATION_COMPLETION_2026-10-28.zh-CN.md)
- [主界面展开/收起动画优化记录](WORKSPACE_REVEAL_ANIMATION.md)

## 首次设置、组件、安装与更新

- [Compact packaging and optional UxPlay](COMPACT_PACKAGING.md)
- [组件与 UxPlay 下载审查（2026-10-03）](COMPONENT_AUDIT_20261003.md)
- [驱动管理器异常路径审查](DRIVER_ERROR_AUDIT.md)
- [设置助手逻辑复查（2026-10-08）](FIRST_RUN_SETUP_REVIEW.md)
- [应用内更新审查与验证（2026-10-23）](IN_APP_UPDATE_REVIEW_2026-10-23.zh-CN.md)
- [完整安装包验证（2026-10-28）](INSTALLER_VALIDATION_2026-10-28.zh-CN.md)
- [设置助手状态检查验收（2026-10-08）](SETUP_ASSESSMENT_REVIEW.md)
- [设置全流程优化与审查（2026-10-13）](SETUP_FLOW_REVIEW_2026-10-13.zh-CN.md)
- [UxPlay 按需下载 404 排查与修复（2026-10-03）](UXPLAY_404_REPAIR_20261003.md)

## 音视频与其他设计

- [AirPlayServer 上游移植评估（2026-10-09）](AIRPLAY_UPSTREAM_ASSESSMENT_20261009.md)
- [iPhoneMirror 同类项目调研与功能建议](COMPETITIVE_RESEARCH_2026-10-23.zh-CN.md)
- [逻辑审查修复记录（2026-10-28）](LOGIC_RETRY_FALLBACK_FIXES_2026-10-28.zh-CN.md)
- [性能审查与优化记录](PERFORMANCE-AUDIT.md)
- [原画虚拟摄像头 60 fps 修复与实机验证](VIRTUAL_CAMERA_60FPS_FIX_2026-10-29.zh-CN.md)

## 早期独立示例

- [USB Touch Demo 交付说明](USB_TOUCH_DEMO_README.txt)：历史 Demo，不是标准构建产物说明。
