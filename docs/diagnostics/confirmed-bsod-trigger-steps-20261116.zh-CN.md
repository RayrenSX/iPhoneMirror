# iPhoneMirror 蓝屏研究：软件内部的危险调用顺序

整理日期：2026-11-16。本文说明历史蓝屏相关路径中，软件实际执行了哪些 USB 操作，以及这些操作为什么会触及驱动对象的生命周期。

**已从旧版源码确认的行为有三项：后台状态检测实际打开、读取和关闭 USB 设备；配置切换请求发出后继续操作旧句柄；恢复失败时再次发送配置切换请求。** 后两项属于同一条停止、恢复链路。历史事件和转储确认了相应蓝屏现象，但具体故障驱动及其内部错误仍需按证据区分。

## 依据与确认范围

本次直接读取本地 Git 标签中的源码核对调用顺序，结合此前已读取的历史聊天及转储分析整理。

| 证据 | 对应版本或记录 | 可以确认的内容 |
| --- | --- | --- |
| 启动与后台检测源码 | v1.5.9，提交 `66f31b8a3d24ae5174d172bd2e54c055b19d2312` | 后台状态检查可以进入实际 USB 枚举、打开、读取 serial 和关闭路径 |
| 停止与恢复源码 | v1.5.10，提交 `63560c4293209dae17117c55724c357867606a6a` | 活动流句柄发送 `0x52` 后仍可能被设置配置、释放接口和关闭；失败恢复存在重复请求路径 |
| 历史启动转储分析 | `080526-21734-01.dmp` | `0x7F / Arg1=8`，I/O 完成路径的 DPC 栈耗尽和双重故障 |
| 历史停止故障记录 | 2026-08-05 11:19:58、11:21:35（UTC+8） | 两次 `WDF_VIOLATION (0x10D)`，参数记录为 `(0x5, 0, 0x1003, …)` |

## 一、后台“检查驱动状态”实际打开了 USB 设备

### 软件实际执行的调用链

v1.5.9 的主窗口每两秒调用 `RefreshAsync()`。刷新设备状态时，在相应条件下执行：

```textj'h'l
UpdateSelectedDriverStatus()
  → VerifySelectedDriverStatusAsync()
  → NativeCore.IsLibUsb0DeviceAvailable(udid)
  → im_is_libusb0_device_available()
  → is_libusb0_device_available(serial)
  → find_libusb0_device(serial)
  → enumerate_libusb0()
      → usb_init()
      → usb_find_busses() / usb_find_devices()
      → 遍历 Apple USB 设备
          → usb_open(device)
          → usb_get_string_simple(... iSerialNumber ...)
          → usb_close(handle)
      → 枚举完成后匹配目标 serial
```

软件为了显示某一台手机的驱动就绪状态，会遍历 Apple USB 设备并尝试打开它们、读取序列号、关闭句柄。目标匹配发生在整次枚举完成后，因此一次单设备状态查询也可能触碰其他 Apple USB 设备。

**频率说明：** 两秒是界面刷新间隔。实际精确探测还受 `Provisional` 驱动状态、10 秒结果缓存、正在执行的探测去重等条件控制；常规刷新入口还检查是否存在活动采集。源码不能支持“每两秒无条件打开一次所有设备”的表述。

此外，旧版环境检查中的 `probe_usb_runtime()` 会创建默认 `QtUsbContext` 并枚举设备；检测到 UsbDk helper 时，还会尝试创建 UsbDk context。这也是已确认存在的自动 USB 后端访问，但没有独立证据将某一次蓝屏唯一归到该分支。

### 与蓝屏的关系

打开、读取和关闭设备会进入实际 USB 驱动路径。当 Apple USB 设备正在离线、重建或重枚举时，这些后台操作会再次访问正在变化的设备对象。历史现场同时存在 `libusb0.sys 1.2.6.0` 与 Apple USB 过滤器。

历史分析确认，蓝屏前有 `AppleUsbFilter.dll → FxObjectHandleGetPtr` 的 WDF 验证器失败及设备接口离线记录；后续转储确认 DPC 栈耗尽和 `0x7F / 8`。**后台探测与设备生命周期变化发生竞态，是当时的高概率解释；I/O 回调究竟在哪个驱动内递归或重入，未被小型转储完整保存。**

代码证据：[后台探测及缓存条件](https://github.com/RayrenSX/iPhoneMirror/blob/66f31b8a3d24ae5174d172bd2e54c055b19d2312/src/App/ViewModels/MainViewModel.cs#L2270)、[实际枚举、打开和关闭设备](https://github.com/RayrenSX/iPhoneMirror/blob/66f31b8a3d24ae5174d172bd2e54c055b19d2312/src/Core/src/Transport/LibUsb0Transport.cpp#L350)、[环境检查初始化后端](https://github.com/RayrenSX/iPhoneMirror/blob/66f31b8a3d24ae5174d172bd2e54c055b19d2312/src/Core/src/Transport/QtUsbTransport.cpp#L375)。

故障证据：分析 1.5.9 启动蓝屏原因（本地历史分析）、[归档转储结论](../releases/v1.5.10.md#蓝屏转储结论)。

## 二、发送配置切换请求后，继续设置配置、释放和关闭旧句柄

### 软件实际执行的调用链

v1.5.10 的 `CaptureSession::run()` 中，`shutdown_usb()` 完成协议停止消息和释放确认处理后，执行：

```text
usb->disable_quicktime_configuration()
  → LibUsb0Connection::disable_quicktime_configuration()
      → 在仍占用 QuickTime 接口的活动流句柄上发送：
        usb_control_msg(handle_, 0x40, 0x52, 0, 0, ...)
      → 请求未报错且存在普通配置时：
        usb_set_configuration(handle_, mux_configuration)

usb->close()
  → close_unlocked()
      → 若仍标记为 claimed：usb_release_interface(handle, ...)
      → usb_close(handle)
```

这里的 `0x52` 请求用于关闭 QuickTime 配置、使手机回到普通 USB 配置；它会引起设备断开和重枚举。软件随后却仍使用原句柄设置配置，或者在异常后进入旧句柄的释放、关闭路径。

### 为什么这个顺序危险

流句柄仍关联着已占用的 USB 接口和传输管道。软件先请求拆除该句柄所对应的设备节点，再通过旧句柄继续调用驱动；此时驱动可能已经在清理原来的接口、队列和框架对象。后续设置配置、释放接口和关闭句柄，便可能与这次清理交错。

旧版 `close_unlocked()` 还显式先调用 `usb_release_interface`，再调用 `usb_close`。后续修复删除了前一次显式释放，避免在 PnP 清理时第一次释放失败、关闭操作又触发释放的情况。

**已确认的是上述调用顺序及同一历史停止故障中的 `0x10D`；具体是哪次调用进入了哪个驱动的错误对象处理，现有整理证据没有唯一确定。**

代码证据：[停止时先 disable、再 close](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Capture/CaptureSession.cpp#L837)、[0x52 后设置配置及释放旧句柄](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Transport/LibUsb0Transport.cpp#L698)。

故障证据：修复停止投屏导致的蓝屏（本地历史分析）。

## 三、把会引起重枚举的配置请求当作可重试操作

### 软件实际执行的调用链

在上述旧版停止流程中，如果 `disable_quicktime_configuration()` 抛出异常，`configuration_restored` 保持为 false，备用恢复任务不会被解除。随后执行：

```text
configuration_restore.run_now()
  → restore_libusb0_configuration(identity)
      → 再次查找并打开设备
      → 再次发送 0x52 / disable
      → 请求未报错且存在普通配置时，再调用 usb_set_configuration
      → usb_close
      → 若抛异常，等待 200 ms 后再次尝试
      → 以 5 秒截止时间控制循环
```

### 为什么这个重试条件危险

`0x52` 请求导致的设备断开，本身可能使控制传输返回 I/O 或设备消失错误。此时手机可能已经接受请求并开始重枚举，软件仍将错误理解为恢复失败，再次查找、打开设备并发送配置切换请求。

**已从源码确认存在这条重复请求路径。** 它会在设备生命周期正在变化时追加访问，是同一停止、恢复问题中的风险因素；历史证据没有将它单独隔离为另一个已独立验证的蓝屏根因。

代码证据：[异常后循环重试恢复](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Capture/CaptureSession.cpp#L234)、[每次恢复实际发送请求并操作句柄](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Transport/LibUsb0Transport.cpp#L548)。

## 多设备并发对上述链路的影响

历史停止故障现场记录了两台 iPhone 同时使用 `libusb0`。v1.5.10 源码可确认：

- 启动时获取 USB transition gate，首个音频或视频样本到达后释放；完整停止、恢复阶段没有重新获取该 gate。
- `libusb0` 发现、配置和关闭操作使用独占锁，bulk 读写使用共享锁。旧版已有锁保护，不能表述为所有 API 都无锁并发。
- 进程锁保护的是同步 API 调用，设备节点的异步断开、重枚举和驱动清理可能持续到 API 返回之后。因此不同设备的启动、恢复及后续设备探测仍有交错窗口。

这些事实解释了历史排查为什么收紧整个停止、恢复阶段的协调；双设备是否为蓝屏的必要条件，尚未确定。

代码证据：[首个媒体样本后释放切换 gate](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Capture/CaptureSession.cpp#L1747)、[bulk 读写的共享锁](https://github.com/RayrenSX/iPhoneMirror/blob/63560c4293209dae17117c55724c357867606a6a/src/Core/src/Transport/LibUsb0Transport.cpp#L650)。

## 后续防护针对的软件行为

| 历史行为 | 防护方向及当前实现入口 |
| --- | --- |
| 自动状态显示触发精确设备打开 | 后台状态检查使用设备和驱动信息，精确后端访问留给显式有线会话；见 [MainViewModel](../../src/App/ViewModels/MainViewModel.cs)、[DeviceManager](../../src/Core/src/Device/DeviceManager.cpp) |
| 使用占用接口的流句柄发起设备节点切换 | libusb0 停止路径先关闭流句柄，再交由独立控制流程恢复配置；见 [CaptureSession](../../src/Core/src/Capture/CaptureSession.cpp) |
| I/O 异常后直接重复写配置 | 通过状态机记录请求和目标设备状态，限制重复切换并等待设备恢复；见 [UsbConfigurationRestorePolicy](../../src/Core/src/Capture/UsbConfigurationRestorePolicy.h) |
| 0x52 后继续操作可能失效的控制句柄 | 配置切换 helper 等待目标设备的移除、到达和稳定结果，并在提交请求后避免显式关闭可能失效的句柄；见 [UsbConfigurationSwitch](../../src/Core/tools/UsbConfigurationSwitch.cpp) |

本次整理核对了历史源码及当前防护入口，未执行真实 USB 配置切换。上述旧版行为不能直接当作当前构建仍存在相同缺陷的结论；具体责任驱动也不能仅凭转储中的模块列表确定。#58 的 `0x1E / 0xC0000005` 仍只有报告摘要，具体内部故障链未确认，详见 [历史审查记录](../issues/audit-2026-10-15/58.zh-CN.md)。
