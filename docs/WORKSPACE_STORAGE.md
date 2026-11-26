# 本地工作区空间维护

开发继续使用 C 盘的 `main` 工作区。v1.8.5-pre7 的本地构建位于 `outputs/iPhoneMirror-main/pre7/iPhoneMirror`。文件内的版本信息可用来辨认并列保存的构建。历史产物按各自目录识别。
多次使用不同的 `--artifacts-path` 或发布目录会反复复制 .NET、桥接器及媒体依赖；这些临时目录应及时归档。

从仓库根目录运行：

```powershell
# 默认只查看：12 小时内没有更新、至少 100 MiB 的闲置目录
./scripts/archive_workspace_artifacts.ps1

# 推荐：压缩到外接硬盘，逐文件和逐卷 SHA-256 校验后释放 C 盘副本
./scripts/archive_workspace_artifacts.ps1 -Archive -Compressed -ArchiveRoot F:/iPhoneMirror-archive

# 需要保留旧路径直接运行时，可逐文件转存并建立目录联接
./scripts/archive_workspace_artifacts.ps1 -Archive -ArchiveRoot F:/iPhoneMirror-archive -MaximumArchiveGB 75
```

脚本只处理 `work` 和 `outputs/iPhoneMirror-main` 的直接子目录。当前交付根目录的文件、
`Assets`、`licenses`、`tools`、`Wireless`，以及构建工具、桥接环境、恢复备份和运行进程引用的目录均保留。
包含 Git 跟踪文件或目录联接的目录也会跳过。已有的归档联接不会重复遍历或归档。

可用 `-KeepRelativePath work/my-investigation` 额外保留目录，用 `-MinimumAgeHours` 和
`-MinimumSizeMB` 调整筛选范围；`-MaximumArchiveGB` 限制单次转存的数据量（优先大目录）。
正在使用但未出现在进程命令行中的调查目录，应显式加入保留列表。
压缩模式默认每卷不超过 2 GiB，兼容 FAT32；`-VolumeMB` 可以调整每卷上限。
Python 使用 gzip；若安装可选的 `zstandard==0.25.0`，会自动采用更快的 Zstandard。
可将此包隔离安装到 `work/storage-maintenance/python-deps`，不改变应用或桥接器环境：

```powershell
python -m pip install --target work/storage-maintenance/python-deps --no-deps --only-binary=:all: zstandard==0.25.0
```

读取 Zstandard 归档也需要该包；gzip 归档只依赖 Python 标准库。
每个文件和分卷均通过 SHA-256 校验，原目录留下 `WORKSPACE-ARCHIVE.json`，包含归档位置和恢复命令。
恢复到空目录，或仅含该指针的原目录：

```powershell
python scripts/workspace_archive.py restore --manifest "F:/iPhoneMirror-archive/本次目录/work/历史构建/manifest.json" --destination "work/历史构建"
```

归档时额外预留 4 GiB；压缩模式接近预留空间时中止当前目录并保留原件。
逐文件转存按簇大小估算占用，FAT32 时间戳允许两秒精度差异，存在超过 4 GiB 文件的目录应改用压缩模式。
报告及复制日志位于 `work/storage-maintenance`，记录每个原路径与归档路径。
复制或校验失败会保留 C 盘原目录；替换联接后如果本地副本未删除，报告会记录其 `.archive-pending-*` 路径，供核对后处理。

压缩模式减少重复构建副本的存储占用，历史内容使用前需恢复；目录联接模式需要归档盘保持在线才能通过原路径访问。
后续开发尽量复用 `build/native`、项目默认 `bin/obj` 和一个固定验证目录，避免每次检查都创建新的完整构建副本。

归档脚本的独立验证（使用一次性文件及实际目录联接，不操作设备）：

```powershell
./scripts/test_archive_workspace_artifacts.ps1 -ArchiveRoot F:/iPhoneMirror-archive
```
