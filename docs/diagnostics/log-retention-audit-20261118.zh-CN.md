# 日志轮转与清理检查（2026-11-18）

## 结论

“软件持续运行数天后日志达到数 GB”存在明确的代码原因：原生 `capture.log` 只在打开文件时检查 16 MiB 限额，正常持续写入不会再次检查。另一个缺口是 14 天保留和主目录 64 MiB 预算只在启动及手动清理时执行。

检查本机时，主日志目录约 64.01 MiB，`capture.log.1` 约 29.20 MiB，均已超过相应的旧策略声明。本机没有发现数 GB 日志；不能据此确认反馈者所用版本和具体大文件。

## 已修复

- 原生日志每次写入前按实际字节预算检查，持续运行期间也会轮转，保留一个归档；正常新生成的活动文件和归档各不超过 16 MiB。
- 归档被占用或重命名失败时停止追加，每秒重试一次。恢复后记录丢失条数，避免轮转失败导致无限增长或高频文件操作。
- 首次打开旧版遗留的超大活动日志时，只归档最后 16 MiB；文件被占用或处理失败时保持停止追加，等待后续重试。
- 主日志目录每分钟在后台检查过期和总量；`application.log`、`reverse-control.log` 轮转后也立即检查目录预算。
- 自动清理按旧文件优先，保护活动日志，跳过占用或无权限删除的文件并在后续检查重试。间隔内或文件持续被占用时，目录总量仍可能临时超过 64 MiB。
- 手动清理日志及更新缓存改为后台执行，执行时禁止重复点击，并安全处理窗口已关闭的情况。

## 路径和覆盖范围

关于页的路径框是只读显示，当前没有选择目录的入口。已有绝对路径环境变量 `IPHONE_MIRROR_APP_LOG_DIRECTORY` 可以改变托管日志、反控日志和核心日志目录；启动错误日志和驱动日志仍使用默认位置。

驱动管理员操作记录位于 `%PROGRAMDATA%\iPhoneMirror.Driver\Operations\<id>`，当前应用清理不递归处理这些事务目录。其日志每个活动文件及归档分别限 1 MiB；本机发现四份合计约 27 KiB，属于多次驱动操作的历史记录，不能解释仅持续投屏导致的 GB 增长。本次未改变这类事务记录的保留策略。

无线宿主通过 IPC 将日志交给主进程写入，没有独立写入同一个核心日志的文件句柄。USB bridge 输出进入已有轮转的反控日志，检查到的 FFmpeg 诊断有条数和长度限制。

## 验证

- `dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj --configuration Release -- --diagnostic-log-maintenance`：持续运行清理、过期、总量、活动文件保护、锁定跳过与恢复、两类托管日志轮转后的即时清理。
- 全套应用逻辑测试通过。按照 `build.ps1` 的方式直接执行已构建 DLL，避开 `dotnet run` 注入 `DOTNET_ROOT_X64` 对更新提权环境校验用例的干扰；新增日志维护测试已纳入默认测试入口。
- `cmake --build build/native --config Release --target iPhoneMirror.LoggingRotation.Tests iPhoneMirror.Core.Tests`，再运行 `ctest --test-dir build/native -C Release -R '^(LoggingRotationTests|CoreProtocolTests)$' --output-on-failure`：2/2 通过，覆盖连续超过 50 MiB 写入、并发写入、归档锁定恢复、旧版超大文件迁移和退出时大小预算。新增原生测试已注册到默认 CTest。
- 应用 Release 编译通过，零警告、零错误。

修复保存在集成工作目录的 `main` 源码中。检查和测试没有删除真实日志、覆盖交付目录或重启正在运行的程序；运行中的旧版本需在后续更新后才会使用修复。
