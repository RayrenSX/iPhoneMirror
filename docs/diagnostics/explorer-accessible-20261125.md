# 可访问目录扩展与其他项目对比

统一映射继续包含媒体、崩溃日志、应用共享文档及获准的完整容器，另增加：

- 「系统目录（可浏览部分）」：Instruments directoryListingForPath 逐项确认能打开的目录，只读。该服务不提供通用文件内容，普通文件不伪装成可读取项。
- 「系统共享库」：remoteFetchSymbols 返回的全部文件，保留原设备目录结构和实际长度，只读按需读取。
- CoreDevice 临时目录、系统崩溃日志、应用数据域和应用组域：使用修正后的原生文件操作协议，只加入实际可访问的项。应用数据根本身可能拒绝访问，但 Documents／Library／tmp 可读，因此在映射中创建只包含可访问子目录的虚拟应用根。保留可写 Documents 入口，同时可追加该应用的只读 Library／tmp 入口。

Windows 不支持的冒号、竖线、控制字符、保留设备名、尾部点／空格、大小写冲突及超长名称显示为可用别名。依据实时目录映射回原始名字，不对输入做任意反转义，也不修改手机上的名称。当前服务根内的可读链接只读展示，禁止循环、断链、越界链接和链接写入。单项权限拒绝或消失不会隐藏可访问同级项。

## 其他项目提供的协议线索

[go-ios fileservice](https://github.com/danielpaulus/go-ios/blob/main/ios/fileservice/fileservice.go) 使用文件域 1–5；[其 XPC 连接](https://github.com/danielpaulus/go-ios/blob/main/ios/xpc/xpc.go) 将会话消息编号保持为 1。真机测试确认每次请求恢复编号 1 后，GetAttributes、目录句柄、ReadFile 和 EndSession 均正常；此前 SDK 默认递增编号会令手机 dtfileserviced 在后续请求触发 XPC reply-channel 断言。生产适配器只在自己的文件服务连接上固定编号，不改变其他设备服务。

目录使用 OpenDirectory／ReadEntryWithAttributes 的 128 项分页与 CloseDirectory，不使用只返回第一条 FileList 的旧枚举包装。无 Entries 的终止响应视为枚举结束，忽略点和父目录。取消后关闭句柄、结束当前文件会话并释放服务。

RootStaging（域 4）实测被当前手机明确拒绝。应用数据仍由 iOS 权限决定，不能将能打开某个域解释为所有 App 均可读取。

| 项目 | 实际接口 | 对当前映射的帮助 |
| --- | --- | --- |
| [iphone-drive-windows](https://github.com/M-Essa11/iphone-drive-windows/blob/main/src/ifuse-win/ifuse-win.cs) | AFC、VendDocuments、Dokan | 挂载实现参考，文件权限范围没有扩大 |
| [AFC Finder](https://github.com/jkcoxson/afc_finder/blob/master/src/main.rs) | AFC jail、VendDocuments、VendContainer、CrashReportCopyMobile | 已覆盖这些范围，Root 模式表示 AFC 根 |
| [AFCExplorer](https://github.com/kawaiizenbo/AFCExplorer/blob/main/AFCExplorer/MainWindow.xaml.cs) | AFC、可选 com.apple.afc2 | AFC2 实测 InvalidService，当前手机不可用 |
| [go-ios](https://github.com/danielpaulus/go-ios/blob/main/ios/fileservice/fileservice.go) | CoreDevice 文件域、分流回复、原生文件操作 | 提供固定消息编号线索，已真机验证并接入 |
| [libimobiledevice](https://github.com/libimobiledevice/libimobiledevice/blob/master/tools/idevicebackup2.c) | AFC、House Arrest、MobileBackup2 | 备份能包含更多数据，但需创建备份快照，属于另一种读取方式 |

标准备份目录未发现本地备份；本轮没有创建备份、开启备份加密、安装越狱服务或改变手机设置。映射使用已经开放并实际能访问的接口。

## 按需读取与验证

系统共享库清单有 88 个文件、共 7,795,937,872 字节。真机验证连续读取 393,216 字节，关闭后从相同偏移重新读取，内容一致。SDK 通用 iter_file_chunks 会启动无界队列，本实现按需拉取 HTTP/2 帧，只保存一帧余量；空闲 30 秒关闭，取消或设备退出清理。没有预下载完整文件。该服务没有远端 seek，顺序读取复用连接，向前 seek 丢弃有界块，向后 seek 重开；Windows WebClient 既有单文件限制仍适用。

真实手机层次审计覆盖 21 个原有入口、291 个目录、2705 个文件／叶子项，发现 4 个原先不能通过 Windows 名称访问的条目：媒体缓存含冒号的空文件、DJI Fly 缓存含竖线的三个空目录，均已验证通过别名打开。实际 Windows WebClient 网络盘还验证读取共享库 65,536 字节、打开冒号文件别名、浏览系统目录及只读属性。

独立回归覆盖名称映射、原始路径写入、首次 stat 后新出现的文件、链接链、分页、固定会话编号、受限应用根、共享库顺序读取／seek／取消／截断／目录变化，以及既有快照和映射生命周期。
