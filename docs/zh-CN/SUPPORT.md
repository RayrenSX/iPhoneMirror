# 问题反馈

[文档索引](../README.md) · [English support](../../SUPPORT.md)

先按实际使用方式查看[常见问题](../USER_GUIDE.md#常见问题)。从「关于」复制完整版本号，并注明使用安装包、便携 ZIP 还是本地构建。若升级到最新公开版后仍复现，请说明。 [公开版本](https://github.com/RayrenSX/iPhoneMirror/releases)

- USB 投屏先核对数据线、手机信任和驱动管理器检查结果。
- AirPlay 请说明采用原始接收端还是 UxPlay。确认同一局域网与防火墙规则。
- USB 或无线触控反控需要 iOS／iPadOS 17.4 及以上、开发者模式、匹配 DDI 和实际 HID 服务。实验性 CoreDevice 视频投屏的 iOS／iPadOS 27 条件单独计算。
- 文件或 IPA 问题请注明是在设备管理、Windows 资源管理器还是签名安装窗口出现，并写清操作和目标目录类型。

## 提交问题

使用 [GitHub Issues](https://github.com/RayrenSX/iPhoneMirror/issues/new/choose) 的缺陷报告或使用咨询表单。提交完整软件版本、Windows 版本和架构、设备型号与系统版本、连接及反控方式、复现步骤、预期和实际结果、发生时间与时区。

通过「关于 → 诊断」查看主程序日志。驱动界面日志位于 `%LOCALAPPDATA%\iPhoneMirror.Driver\Logs\driver-ui.log`。只附相关时间段，并先移除设备标识、用户路径、配对记录、账号、密钥及私人画面。

安全漏洞请使用 GitHub 的私人报告入口。 [私密漏洞报告](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new)
