# 映射盘容量来源与原生投影验证

用户截图中的 300 GB 总量和 16.4 GB 可用与电脑 C 盘一致。现有 WebDAV 服务未返回配额，而 Windows 自带 WebClient 在 `GetDiskFreeSpaceEx` 中报告本机缓存卷容量。

宿主现已通过现有 `device_info` 读取所选设备 `com.apple.disk_usage` 的 `TotalDiskCapacity` 与 `TotalDataAvailable`，保持与设备信息面板相同口径，不使用数据分区容量代替总容量。原始字节计数不舍入；可用为零是有效值，负值或大于总量的值拒绝。读取合并并短暂复用 15 秒，写入后失效。

WebDAV 实现 RFC 4331 的 `quota-used-bytes` 和 `quota-available-bytes`。普通目录 allprop 不额外查询容量，保留文件树缓存的快速显示；显式容量请求和 propname 才读取。真实接口回包与设备原始值一致。当前连接的设备实测总量 128000000000 字节、可用 86295064576 字节；这台测试设备与截图中的 256 GB 设备不同，不能用本次读数替代截图设备的值。

测试明确揭示：即使把配额也加进 allprop，Windows 实际 `GetDiskFreeSpaceEx` 仍回报本机 322144563200 字节总量。已回退实验性的 allprop 改动。因此不能仅凭 HTTP 配额测试声称资源管理器已修好。

为支持资源管理器真实卷容量，在 `work/explorer-storage-20261125/DokanProbe` 准备原生投影原型，复用已有私有 WebDAV UNC 作为内容后端，并通过 Dokan 提供手机的 `GetDiskFreeSpace` 与卷名称。容量和名称回调已通过，尚未安装驱动或把此原型接入生产映射，等待用户选择。

官方 Dokan 2.3.1.1000 x64 MSI 已下载到工作目录。SHA-256 为 `69ff8cb37bfec3a75921c85ffd1c6370b50a9ec4ecef2cf3a009d488dcbf5465`，与 GitHub release API 摘要一致；Authenticode 为 Valid，发布者 LEOSAC。当前机器未安装 Dokan/WinFsp，当前任务进程非管理员，驱动安装需要用户管理员授权。

已验证：托管 Explorer 专项含新增容量测试通过；原生投影回调精确返回 256000000000/175450000000 测试字节，零空间、无效数据、缓存刷新、写入失效、取消和 RFC 配额回包测试通过。尚未执行真实原生卷安装/挂载，尚未更新正在运行的根交付版本。

来源：[RFC 4331](https://www.rfc-editor.org/rfc/rfc4331.html)、[GetDiskFreeSpaceEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)、[Dokan 容量接口](https://dokan-dev.github.io/dokan-dotnet-doc/html/interface_dokan_net_1_1_i_dokan_operations.html)、[Dokan 官方发布](https://github.com/dokan-dev/dokany/releases/tag/v2.3.1.1000)。
