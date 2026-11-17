# Issue #88：归零跳转与 scrub 进度协议修复

本轮基于 `issue88-startup-budget-20261117` 的实机日志继续修复。源代码保留在当前 `main`，当前运行的 PID 31888 不停止、不覆盖。

## 实机证据与边界

日志快照位于 `work/issue88-scrub-fix-20261117/physical-20261117-1152`。44.79 秒内容和随后 14.04 秒内容均完成，分别在北京时间 11:53:32.727、11:54:24.457 收到手机的自然完成 ACK，下一次 Play 也成功打开。但第二段在 11:53:54、11:53:56、11:53:58 三次收到原生 Seek0；最后一次导致从 0 重建 HLS，在约 11 秒等待后重新打开。

Seek0 在原生接收层已存在，并非 C# 日志缺失。三次跳转前 C#/Host 回报的位置分别约为 4.446、6.442、8.646 秒，rate=1，状态 Playing，没有回报位置突然归零的证据。加密请求解密后进入同一套原生路由；旧日志没有请求方法或原参数解析结果，因此不能证明三次原始请求都是合法的 `POST position=0`，也不能证明均为解析错误。

## 已确认的代码缺口

旧 `/scrub` 路由按路径前缀匹配，不区分方法，只查找任意 `position=` 子串并调用 `atof`。空字符串、非法字符串等会被当作 0，GET 带参数也可能产生跳转。另一方面，普通 GET `/scrub` 仅返回空 200，没有当前时长与位置。

进度读取和跳转写入应分开。OpenAirPlay 的[实际协议消息记录](https://openairplay.github.io/airplay-spec/video/http_requests.html)列出 GET 返回 `text/parameters` 的 duration、position，POST 使用以秒计的 position。实际客户端 [python-airplay 的 scrub 实现](https://github.com/cnelson/python-airplay/blob/master/airplay/airplay.py#L346)也会读取 GET 响应，并在写入后读取确认。

本次修复以这些确定缺口为范围：GET 只读取真实进度，POST 严格解析合法目标，保留有效的 Seek0；增加不含 URL、会话或原请求体的数值诊断，便于实机区分合法归零和拒绝的无效请求。没有根据猜测修改 loadedTimeRanges 或丢弃合法的归零操作。

## 验证与交付

旧版真实原生接收器在隔离端口复现了 `GET /scrub?position=bad` 返回 200 并触发 Seek0，证据为 `work/issue88-scrub-fix-20261117/plain-baseline.json`。新版 22 条明文请求矩阵以及真实 SAP/GCM 加密连接的 20 组断言通过；每次动作后读取真实位置，确认只有 0、12.5、2.25 三个合法 POST 触发跳转，GET、非法参数、错误方法和其他会话没有回调。非法请求返回后同一持久加密连接仍能继续读取和执行合法命令，错误响应明确包含零内容长度。

证据位于 `work/issue88-scrub-fix-20261117/plain-final.log`、`plain-final.json`、`encrypted-final.log`、`encrypted-final.json`。46 条纯解析用例、pinned pristine 源码完整补丁、旧补丁升级、strict 版本错误响应升级及重复应用均通过，报告为 `work/issue88-scrub-parser-20261117/report.md`。安装器还验证了失败回滚、运行库与补丁来源哈希同步、错误依赖记录拒绝。

真实原生接收器的 3 次和 123 次 FCUP 资源请求回归通过，均保留三段自然完成队列、关联结束 ACK、HTTP/RTSP/EVENT 混合加密响应与重定向断言；使用隔离端口 17011、17012，未触及运行程序的标准接收端口。完整 Logic 套件通过，Release 编译与发布成功。原生报告为 `work/issue88-scrub-fix-20261117/native-validation-report.md`，长期回归日志为 `transport-123-final.log`。

新诊断 `video_diag event=32` 的六个数值依次为：方法、结果、目标或读回位置的毫秒值、是否执行跳转、是否归属当前会话、入口。方法 1=GET、2=POST、3=其他；结果 1=读取、2=合法跳转、3=缺参数、4=非法值、5=重复参数、6=错误方法、7=其他会话；入口 1=AirPlay、2=RAOP。位置毫秒值仅用于诊断且有上限，实际合法跳转仍使用 double 秒数。

最终构建、协议回归和文件哈希以交付目录 `BUILD-INFO.json` 为准。真实 iPhone 上是否仍发出合法归零请求，需要新版本会话证据；修复代码缺口不等同于已证明旧三次 Seek0 的唯一原因。
