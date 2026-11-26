# IPA 拖入、签名与安装

在 v1.8.5-pre7 中，将 IPA 拖入主投屏或独立投屏窗口后，可以选择 **传文件** 或 **安装**。其他文件传输方式见[使用指南](USER_GUIDE.md#发送文件与安装-ipa)。
传文件继续使用 App 共享文档；安装只处理这次拖入的 `.ipa`，不会上传其他文件。
两者都需要为目标设备开启有线或无线反控。没有就绪连接时显示开启反控的错误提示。

选择安装后，确认设备、IPA 和签名方式，再按安装。打开窗口或选中方式不会自动安装。

| 方式 | 输入 | 行为 |
|---|---|---|
| 使用现有签名 | 已签名 IPA | 不重签，交给设备检查签名与安装许可 |
| Apple ID | 免费或付费账号、密码、必要的六位验证码 | 连接 Apple，申请或复用开发证书、注册目标设备及 App ID，取得描述文件并本地签名 |
| P12／PFX | PKCS#12 证书、密码、描述文件 | 校验证书和私钥、有效期、设备许可、Bundle ID，再本地签名 |
| 证书＋私钥 | PEM／DER／CER 证书、PEM／DER 私钥、密码、描述文件 | 与 P12 流程相同，支持分开的证书和私钥 |

证书流程支持开发、Ad Hoc、企业描述文件；普通分发证书并不自动带来设备安装许可。
可选择多个描述文件，以覆盖主 App、扩展和嵌套 Watch App；也可明确勾选移除扩展和 Watch App。
Bundle ID 可留空。Apple ID 模式会使用团队后缀生成可注册的标识符。
免费账号的 App 数量、App ID 数量、签名有效期及能力限制由 Apple 决定。
从 App Store 得到的加密 IPA 不能用重签方式解除加密；没有绕过设备的签名检查。

安装按队列处理。取消会停止后续步骤；已经完成安装的 App 保留。设备断开、重新绑定、反控会话
变化或自动化取得设备控制权后，不会把签名结果安装到另一条连接，也不会自动重试安装。
IPA 上限为 512 MiB；归档需包含一个主 App，路径遍历、符号链接、重复路径和异常膨胀会被拒绝。

## 实现与参考

界面位于 `PreviewIpaActionWindow`、`PreviewIpaInstallWindow`。本地签名由
`IpaSigningSession` 通过 JSON stdin 调用 `iUsbBridge --ipa-signing --ipa-workspace <目录>`。
签名工作进程不连接设备；安装复用已就绪反控桥的 `install_app`，通过 AFC 暂存并调用 Apple
InstallationProxy。目标 UDID、连接实例、输入代次和自动化所有权均在提交前检查。

调研了以下原始项目：

- [iPASide](https://github.com/pwnapplehat/iPASide)：选取其 MIT Python 引擎，固定到
  `cd27ac986a2f048a7075f3d15ab8587ae4ac8730`，复用 GSA、开发者服务、配置和 Mach-O/CMS 签名实现。
- [iLoader](https://github.com/nab138/iloader)：参考 Apple ID 侧载流程和设备选择。
- [zsign](https://github.com/zhlynn/zsign)：参考证书和描述文件输入方式；当前集成使用本地 Python
  签名引擎，不要求用户另行下载 zsign。

上游来源、原始文件 SHA-256 和完整 MIT 许可证保存在 `tools/ipaside_engine/UPSTREAM.json`、
`LICENSE`。本地差异见 `README.iphoneMirror.md`。

Apple 登录启用 TLS 证书校验；账号密码、验证码和 P12 密码走进程管道，不放进命令行或日志。
会话和私钥 JSON 缓存使用当前 Windows 用户 DPAPI 加密，位置独立于 iPASide。
签名临时目录由宿主创建，进程树退出后清理。不会为腾出证书名额自动吊销其他签名。

Apple ID 默认使用上游公开的 HTTPS Anisette 服务，可填写自有服务器。
该服务收到随机设备注册身份及连接 IP，不会收到 Apple ID、密码、验证码、会话 token 或配对文件。
账号、证书和设备权限仍由 Apple 决定。

## 验证

```powershell
./work/usb-touch-bridge-python/Scripts/python.exe -m unittest discover -s tests -p ipa_signing_test.py -v
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release
```

运行时测试入口为 `--preview-ipa-install <截图目录>` 和 `--preview-files-control <输出目录>`。
前者覆盖两种拖入动作、四种签名方式、双重认证界面、失败与取消、目标变化及五种语言布局；
Python 测试用合成 IPA 验证真实 RSA/CMS 签名、Mach-O 页哈希、配置检查和 DPAPI。
这些离线测试不代表完成真实 Apple ID 登录、Apple 签发或 iPhone 安装验收。
