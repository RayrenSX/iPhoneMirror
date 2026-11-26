# 贡献指南

[文档索引](../README.md) · [English contribution guide](../../CONTRIBUTING.md)

可以从修订文档、翻译、验证设备兼容性或提交代码开始。报告问题前先搜索已有 Issue，并提供软件的完整版本与可复现步骤。 [问题反馈](SUPPORT.md)

开发需要 Windows x64、MSVC、Windows SDK、CMake、.NET 10 和 Python 3.13 x64。可选 UxPlay 组件使用 MSYS2 UCRT64。具体依赖、编译和打包参数见[开发与测试](../DEVELOPMENT.md)。

```powershell
./build.ps1 -Configuration Release
./scripts/verify_localization.ps1
python scripts/verify_docs.py
```

每次提交聚焦一个问题。说明可见行为、协议或驱动改变，以及实际运行的验证。补充涉及行为的文档和本地化。涉及实机时可附型号和系统版本，公开截图或日志前应脱敏。

项目自有代码按 GPL-3.0-only 分发，第三方材料以原许可为准。 [第三方组件说明](THIRD_PARTY_NOTICES.md)
