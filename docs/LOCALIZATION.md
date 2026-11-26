# 本地化维护

当前界面支持简体中文、繁体中文（香港）、繁體中文（台灣）、English 和日本語；在「设置 → 应用」选择语言，也可跟随系统。用户操作见[使用指南](USER_GUIDE.md)。

## 修改入口

| 内容 | 位置 |
|---|---|
| 主程序 | `src/App/Localization/Strings.*.xaml` |
| 驱动管理器 | `src/DriverInstaller/Localization/Strings.*.xaml` |
| 安装器 | `installer/Languages` 及安装脚本的语言消息 |
| 启动失败、清理与测试网页 | 对应脚本和页面的现有语言表 |
| 台湾更新说明 | `CHANGELOG.zh-TW.md` |

新增或删除资源键时同步各语言；保留占位符、快捷键、文件过滤器、链接和换行含义。产品名、协议名、设备自报名称及原始系统错误保持原文。静态扫描未找到引用不代表资源无用，需检查动态拼接和运行时入口。

台灣譯文使用「設定、軟體、螢幕、滑鼠、資料夾、網路、資訊、音訊、裝置、記錄檔、解析度、快速鍵」等當地用語；香港與台灣字典獨立維護，不以簡繁字形轉換代替翻譯。新增頁面沿用現有動態資源與語言切換機制。

## 校验

从仓库根目录运行：

```powershell
./scripts/verify_localization.ps1
python -X utf8 scripts/audit_localization.py --summary
```

需要逐键对照表时再生成，报告位于忽略的 `work/diagnostics/localization/LOCALIZATION-AUDIT.md`，不作为现行说明提交：

```powershell
python -X utf8 scripts/audit_localization.py --write-report --summary
```

静态校验覆盖资源完整性、格式和引用，不能证明翻译自然或页面可读。修改界面文字后，还需按[开发指南](DEVELOPMENT.md#测试入口与覆盖范围)执行相关运行时检查，在五种语言、浅深主题和长文本场景查看截断、重叠与键盘访问。
