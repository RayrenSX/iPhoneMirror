# USB 反控打包与 Apple 签名错误核查

日期：2026-10-08。范围：当前工作区的 Python 桥接器、正式打包流程和错误分类。

## 结论

用户提供的 v1.8.5-pre 修复记录涉及两个独立问题：

1. USB DLL 没有进入冻结桥接器的搜索目录，导致后端不可用。
2. 访问 Apple 签名主机 `gs.apple.com` 的网络路径返回空 HTTP 502，旧版上游解析器又抛出 `IndexError`。

仓库代码支持这一机制解释，但本次没有取得该用户原安装目录或原始日志。
记录中的 836/838 条清单、哈希、代理热加载及真机 ready 属于用户提供的现场证据，
不作为本次重新验证的结果。也不能仅凭通用的“无法反向控制”提示认定所有反馈原因相同。

## 已修复的打包缺口

原 `iUsbBridge.spec` 使用空 `binaries`，依赖 PyInstaller 在构建机器上自动发现 USB 库。
本地旧输出就只包含 `_internal/libusb0.dll`，没有 `_internal/libusb-1.0.dll`。
PyInstaller 的 `pyi_rth_usb.py` 默认在 `sys._MEIPASS` 查找 DLL；本配方中就是 `_internal`。
主程序根目录有 DLL、投屏正常，均不能证明桥接器加载后端成功。

- 源码暂存脚本从仓库既有原版文件读取两个 x64 DLL，先核对固定 SHA-256，再暂存到 `native/`。
- spec 显式将两者打入 `_internal` 根目录，沿用自动生成的完整性清单，不绕过校验。
- 发布清单验证必须包含 `_internal/libusb0.dll` 和 `_internal/libusb-1.0.dll`。
- 冻结 Windows EXE 的 `--check-runtime` 检查 DLL 位置并实际加载两个后端；
  指定后端分别检查，避免 libusb1 回退掩盖 libusb0 缺失。
  该探测不打开设备或发送触控、键盘指令；不代表驱动绑定或真机反控已经通过。

| 文件 | SHA-256 |
|---|---|
| libusb0.dll | `4f18b5d2c28aa66b648c8683c6d09b52b92cbbee85984bbefad5f38a64bc2a14` |
| libusb-1.0.dll | `5072054cb3002ae071f382ad5c2c2b0092d9451c537d0c13444c2b6f968f7251` |

## Apple 签名路径

当前工作区已有 `tools/ddi_support.py`，替代 pymobiledevice3 11.3.1 的同步签名传输：
使用异步 HTTP、总时间限制、有限重试和响应检查。
本次增加空 HTTP 502 的精确回归案例，验证两次尝试后仍返回
`developer_image_tss_unavailable` 并保留 HTTP 状态，不出现 `IndexError`，不把它误判为镜像拒绝。
无效成功响应则归类为 `developer_image_tss_failed`。

本次没有修改任何 Clash 配置。现场针对 `gs.apple.com` 更换路由的办法可以作为排查方向，
但代理组名、订阅规则文件和端点可达性均因用户环境而异；不能把某位用户的配置直接推广。
应让该域名走实际能够访问 Apple 的网络路径；下载 DDI 成功也不表示签名端点可达。

## 验证与交付

验证日志和临时构建源位于 `work/usb-backend-repair/`。
本次实际验证结果：

- 全部 328 项 Python 回归测试通过，包含空 HTTP 502 和新增 USB 自检案例。
- PowerShell 源码暂存、DLL 哈希、清单缺项、篡改拒绝和发布文件列表测试通过。
- 正式 PyInstaller 配方重建成功；实际 EXE 报告 `usb_backend_libusb0`、`usb_backend_libusb1`，退出码 0。
- 在独立产物中分别将两个 DLL 移到 `_internal` 外，以及分别注入无法加载的 DLL，
  四种情况均退出码 1，不报告 ready；随后恢复原文件。
- 恢复后的 836 个清单文件全部哈希与覆盖检查通过；两个 DLL 与仓库固定来源完全一致。
  这里的文件数量属于本次源码构建，不要求等于用户旧版包的 838 条。
- 独立 C# 探测程序直接编译当前主程序的 `RuntimeBinaryIntegrity.cs`，验证该组件通过完整性检查。
  探测仅替代错误日志字符串净化依赖，没有替代文件检查逻辑。

重建的桥接器组件位于 `artifacts/iUsbBridge-usb-backend-fixed/`，包含 EXE、`_internal` 和完整性清单。
这是独立验证组件，不是新的完整应用发布包；正式更新应通过仓库打包流程生成完整安装包。
桥接器 EXE SHA-256：`e2ef7bb863d101d2900e36035203877698c9613f2cecdcdcf7a6e3a5cb52b719`。

本次没有在反馈用户的手机上重做签名、挂载或触控测试，也没有发布新版本。

## 复审修复：隔离构建机的 USB DLL

复审发现，spec 显式添加两个 DLL 仍不会阻止 PyInstaller 默认 `hook-usb.py`
搜集构建机 PATH 中的 USB 库。如果额外收集到 `usb-1.0.dll`，运行时 libusb0 的
`usb*` 通配搜索可能先加载它，因 API 不匹配而导致自检失败。
已使用实际构建钩子和运行时钩子复现此问题。

新增配方内的 `hooks/hook-usb.py`，保留运行时钩子需要的 `glob` 隐藏导入，
完全取消构建机 USB 枚举及 PATH 库发现；DLL 仍由 spec 从经过哈希验证的暂存文件显式提供。
源码暂存现在强制携带该钩子，缺失时立即拒绝构建。
仅设置 `PYINSTALLER_USB_HOOK_SKIP_PYUSB_DISCOVERY` 不足以修复，因为上游仍会执行 PATH 搜索。

本轮验证通过：

- 7 项 Python 专项测试，以及 PowerShell 暂存、钩子缺失拒绝、清单和文件哈希测试。
- 将真实 libusb-1.0 DLL 分别命名为 `usb-1.0.dll`、`usb.dll`、`usb-0.1.dll`，
  放到构建进程 PATH 最前面，完成正式配方构建及冻结程序自检。
- 构建日志确认使用暂存的本地 `hook-usb.py`，且保留标准 `pyi_rth_usb.py` 运行时钩子。
- 产物没有包含上述三个别名；固定的两个 DLL 与仓库原文件一致。
  全部 836 个文件通过清单覆盖、逐文件哈希和主程序 C# 完整性校验。
- 最终 EXE 在仅包含 Windows 目录的 PATH，以及上述别名目录优先的 PATH 下，
  均成功加载两个后端，退出码 0。

本轮组件输出：`artifacts/iUsbBridge-usb-hook-fixed/`。
EXE SHA-256：`570898b376045d146d6fb0b9b6cc8af24c3048270a27e743e3a03c29e78205cd`。
日志和验证结果位于 `work/usb-hook-repair/`。该组件尚未作为完整应用版本发布，未增加真机验证。

## 参考

- [PyInstaller USB 启动钩子](https://github.com/pyinstaller/pyinstaller-hooks-contrib/blob/master/_pyinstaller_hooks_contrib/rthooks/pyi_rth_usb.py)
- [pymobiledevice3 11.3.1 TSS 解析器](https://github.com/doronz88/pymobiledevice3/blob/v11.3.1/pymobiledevice3/restore/tss.py)
- [源码暂存](../scripts/UsbBridgeBuildSource.ps1)、[打包配方](../scripts/usb-bridge-recipe/iUsbBridge.spec)、[自检](../tools/bridge_runtime_check.py)
- [运行时完整性校验](../src/App/Services/RuntimeBinaryIntegrity.cs)、[DDI 签名处理](../tools/ddi_support.py)
