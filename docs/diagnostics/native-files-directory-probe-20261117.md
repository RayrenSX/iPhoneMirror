# 手机「文件 → 我的 iPhone」真实目录访问核对

## 结论

用户要求 Windows 与手机 Files 的目录、层级和内容一致。App 文档聚合不满足要求，相关草稿已撤回；现有产品源码仍与 app-documents-20261117 交付记录一致。本次没有发布替代界面或新构建。

用户提供的论坛路径指向 `group.com.apple.FileProvider.LocalStorage`，此容器标识已在连接的 iPhone 17 上确认存在。Installation Proxy 对 `com.apple.DocumentsApp` 的只读查询返回了该组容器的实际 UUID 路径。论坛写法中的 UUID 和组标识不是应直接拼接的固定目录名；应以每台设备返回的 GroupContainers 映射为准。

这只确认了容器根路径，不等于已经确认 `File Provider Storage` 子目录内容，也不能证明手机 Files 呈现的全部 13 项来自这个单一物理目录。手机提供的原生视图仍未成功枚举和比较。

## 实机只读结果

2026-11-17，对已配对的 iPhone 12 mini 和 iPhone 17 分别测试，保留现有配对；不建立新配对，不安装软件，不挂载 DDI，不启动 HID 或投屏，不修改手机文件。

- iPhone 17 型号 `iPhone18,3`，现有配对有效。
- Installation Proxy 返回 `group.com.apple.FileProvider.LocalStorage` 及其实际组容器路径。
- CoreDevice 文件控制和数据服务均已公布，但以真实组标识创建 APP_GROUP_DATA_CONTAINER 会话返回 `ContainerLookupErrorDomain`、错误码 7。尚不能仅凭该错误断言具体原因是权限还是服务对系统容器的限制。
- DVT DeviceInfo 在返回的组容器根路径及其 `File Provider Storage` 子路径调用目录查询，都未返回目录列表，报 `DvtDirListError`；没有附加原因。
- 设备公布的相关服务为 House Arrest、AFC、File Relay、CoreDevice 文件控制和数据。未发现额外公布的通用 Files/File Provider 根目录接口。此列表不证明其它方案绝对不存在。
- iPhone 12 mini 的 CoreDevice 系统组容器访问同样返回错误码 7。

这些结果不足以实现或交付完整读写映射。不能把改名后的媒体目录、按 App 拼接的目录或仅可访问部分目录标为原生「我的 iPhone」。

## 复核材料

只读探针：`work/on-my-iphone-20261117/probe-native-files.py`。传入明确的 ProductType，只使用现有配对；创建的服务、隧道及目录查询会话都由异步上下文退出清理。探针只列目录，没有传输用户文件内容。

当前日志：`work/on-my-iphone-20261117/native-probe-iphone17-final.log`。iPhone 12 mini 的预检查记录：`work/on-my-iphone-20261117/native-probe-12mini.log`。日志包含设备返回的本地容器 UUID，应作为本机诊断资料。

参考：

- 用户提供的路径线索：https://iosre.com/t/23863
- Apple 文件访问文档：https://developer.apple.com/documentation/xcode/interacting-with-devices-using-the-command-line ，明确文件操作受设备端域访问权限约束。
- Apple File Provider：https://developer.apple.com/documentation/fileprovider ，说明系统文件视图与应用文档提供机制。

下一步需要找到并验证能够读取原生 Files 视图及其条目的接口或经设备授权的传输通道。在此之前不更改面板为虚假的统一根目录，不声称已完成 Windows 原样映射。

## 继续验证：电脑端直接访问限定

用户进一步明确只接受电脑端直接访问，不接受在手机安装助手。本轮遵守该限定，没有创建或安装手机 App。

新增只读检查和证据：

- AFC 媒体目录查询成功，同一台 iPhone 17 的现有信任和基础文件服务有效。
- LCSign 的 VendDocuments 查询成功，目录共 3 项；仅记录数量，没有读取用户文件内容。该对照证明共享应用目录通道有效，但不等同于系统 Files 访问权限。
- House Arrest 对 `com.apple.DocumentsApp` 的 VendContainer 请求返回 `InstallationLookupFailed`。系统 Files 容器不能通过此应用容器接口取得。
- 查询 `com.apple.afc2` 返回 `InvalidService`，设备未提供该完整文件访问服务。没有安装、开启或模拟该服务。
- CoreDevice 对全部 186 个安装应用返回的能力信息中，只有 5 个容器可访问；`com.apple.DocumentsApp` 明确为 `isContainerAccessible=false`、`isDeveloperApp=false`。LCSign 为可访问的开发者应用。
- 再通过正式 `requireContainerAccess=true` 参数请求一次可访问容器列表，设备仍仅返回 5 个应用，其中不包含 Files。没有获得系统目录访问授权。
- 初次固定 Chrome 的对照不成立，因为这台 iPhone 17 没有返回该 Bundle ID；不能把其错误当作系统目录限制的证据。后续使用实际安装的 LCSign 做了共享文档对照。
- CoreDevice 自动选取可访问开发者应用后的目录查询出现 `IncompleteReadError`，不能声称该客户端的所有目录读取功能已通过。系统 Files 的不可访问能力标志和容器创建错误是独立证据。
- DVT 对媒体目录的对照同样未成功，因此原有 DVT 失败只能算这条客户端接口不可用，不能单独证明系统容器的具体拒绝原因。
- File Relay 的源码接口只接受命名诊断源并返回完整压缩归档，不提供任意目录实时读写。未请求 UserDatabases 等与用户目标无关的数据。
- MobileBackup 类型的快照/恢复不能满足现有目标的实时双向目录映射，未备份或恢复手机。

对应日志：`access-controls.log`、`container-admission.log`、`container-permission.log`，均位于 `work/on-my-iphone-20261117`。探针为同目录下 `probe-access-controls.py`、`probe-container-admission.py`、`probe-container-permission.py`。所有新会话均已退出；失败探针中 SDK 报过未取用 Future 异常，这是诊断连接失败的清理日志，不是新增产品改动。

官方文档仅承诺设备上有权限的域可读写，不承诺任意系统容器可访问。上述实测没有找到满足「只用电脑端、原生完整目录、实时读写」的可用路径。此处结论是当前设备和已测接口不可完成，不能据此宣称所有未来接口绝对不可能，也不能用假目录、备份或 App 聚合替代完成。

目录授权的 iOS 文档已调研，但手机端助手方案已被用户明确排除，不继续开发该方向。现阶段没有可交付的完整映射实现；产品源码、运行版本和用户文件保持原状。本轮 Git fetch 因远端连接重置失败，没有同步或改写已有待提交改动。
