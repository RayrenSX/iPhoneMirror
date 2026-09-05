# UsbTouchBridge.exe 调用文档

`UsbTouchBridge.exe` 是 iPhoneMirror 的输入桥接进程。它通过 Apple
usbmuxd 建立 CoreDevice 隧道，再向 Universal HID 服务发送触控和键盘报告。
程序本身不提供交互式命令行输入；启动后通过标准输入接收二进制长度前缀的
JSON 消息，通过标准输出发送 JSON Lines 状态事件。

## 1. 基本位置

```text
C:\Users\Ray\Documents\iphoneMirror\dist\UsbTouchBridge.exe
```

查看参数：

```powershell
.\dist\UsbTouchBridge.exe --help
```

## 2. 启动参数

```text
UsbTouchBridge.exe [--usb | --wireless] [--udid UDID] [--rate-hz HZ] [--ddi-dir DIRECTORY]
```

参数说明：

| 参数 | 说明 |
| --- | --- |
| `--usb` | 只选择 usbmuxd 的 `USB` 设备。默认值。 |
| `--wireless` | 优先连接 usbmuxd 的 `Network` 设备；若记录不存在，使用已完成配对的 RemotePairing mDNS 隧道，不会回退到 USB。 |
| `--udid` | 指定目标设备 UDID；不指定时使用该传输类型发现到的第一个设备。 |
| `--rate-hz` | 输入速率提示值，默认 `120`。它不会改变键盘报告格式。 |
| `--ddi-dir` | 可选的本地 Personalized DDI 目录，仅在设备尚未挂载镜像时使用；目录必须包含 `Image.dmg`、`BuildManifest.plist`、`Image.trustcache`。未传入时桥接器直接从 GitHub 官方 API 动态解析 commit 和文件清单，校验 Git blob 身份、文件大小及本地 SHA-256，并要求 manifest build 与当前运行时匹配。 |

`--usb` 与 `--wireless` 互斥，必须最多指定一个。

### USB 模式

```powershell
.\dist\UsbTouchBridge.exe --usb
.\dist\UsbTouchBridge.exe --usb --udid 00008150-001903580A9B401C
```

USB 模式要求 iPhone 通过数据线连接、已解锁并信任此电脑。

### 无线模式

```powershell
.\dist\UsbTouchBridge.exe --wireless
.\dist\UsbTouchBridge.exe --wireless --udid 00008150-001903580A9B401C
```

无线模式使用 Apple 的 usbmux `Network` 设备记录或 CoreDevice 的 RemotePairing
mDNS 隧道，不是 AirPlay，也不是直接连接某个固定 TCP 端口。首次使用通常需要：

1. 先通过 USB 连接并信任 iPhone，并成功启动一次 USB 反控。桥接器会在这个可信 USB 会话中准备 RemotePairing 记录。
2. 在 Apple Devices 或 iTunes 中启用“通过 Wi-Fi 同步此 iPhone”。
3. 让电脑和 iPhone 位于同一局域网，并保持 iPhone 解锁。
4. 保持 Apple Mobile Device Service 和 usbmuxd 正常运行；Windows 防火墙需要允许 Bonjour/mDNS（UDP 5353）。
5. 若设备以 `Network` 类型出现在 usbmuxd 中，桥接器可直接使用；否则会自动尝试已配对的 RemotePairing 服务。

可用 Python 检查设备类型：

```powershell
python -c "import asyncio; from pymobiledevice3.usbmux import list_devices; print(asyncio.run(list_devices()))"
```

如果没有 `Network` 设备，桥接器会尝试 RemotePairing 发现；缺少 RemotePairing
记录时会返回 `wireless_remote_pairing_required`，不会偷偷使用 USB 设备。

## 3. 启动事件

stdout 每行是一个 JSON 对象。常见事件如下：

```json
{"event":"status","code":"connecting_device","message":"正在建立USB设备会话"}
{"event":"status","code":"initializing_touch","message":"正在初始化触控通道"}
{"event":"ready","protocol":2,"capabilities":["iphoneMirror.usb_touch.v2","iphoneMirror.usb_keyboard.v1"],"udid":"...","rateHz":120,"gateOpen":true,"authMode":"direct","transport":"usb"}
```

事件类型：

| `event` | 含义 |
| --- | --- |
| `status` | 连接、初始化或结束状态。 |
| `ready` | HID 会话已建立，可以发送输入帧。 |
| `warning` | 认证 gate、媒体流等非致命问题。 |
| `error` | 消息格式、连接或 HID 发送失败；进程通常随后退出。 |

桥接器只会在 `gateOpen=true` 且已验证 mainTouchscreen（Service ID `257`）时发布
`ready`。`authMode` 为 `mediastream` 或 `direct`；后者表示媒体流认证被拒后，
通过 direct Universal HID 验证的回退路径。已挂载 DDI 缺少该 surface 时，桥接器会
自动重挂一次而非报告一个无法触控的 ready。

## 4. stdin 消息封装

每条消息必须按以下格式写入 stdin：

```text
4 字节 little-endian 无符号长度
长度字节的 UTF-8 JSON
```

长度只表示 JSON 字节数，不包含前面的 4 字节。单帧最大约 64 KiB。

## 5. 触控帧

消息结构：

```json
{
  "schema": "iphoneMirror.touch.v2",
  "kind": "touch_batch",
  "seq": 1,
  "timestampNs": 1234567890,
  "points": [
    {"pointerId": 0, "action": "down", "normalizedX": 0.5, "normalizedY": 0.5}
  ]
}
```

约束：

- `seq` 必须是非负整数，建议每帧递增。
- `action` 为 `down`、`move` 或 `up`。
- `normalizedX`、`normalizedY` 必须在 `0.0` 到 `1.0` 之间。
- 每帧最多 5 个触点，`pointerId` 在同一帧内不能重复。
- 同一个 `pointerId` 的 `down`、`move`、`up` 会被映射到稳定的 0 到 4 号 HID slot。

## 6. 键盘帧

消息结构：

```json
{
  "schema": "iphoneMirror.touch.v2",
  "kind": "keyboard_batch",
  "seq": 2,
  "timestampNs": 1234567891,
  "usages": [4, 224]
}
```

`usages` 是“当前仍按住的全部按键”，不是增量事件：

- 按下 `A`：发送 `[4]`。
- 按住 `A` 再按左 Shift：发送 `[4, 225]`。
- 释放 `A`、仍按左 Shift：发送 `[225]`。
- 释放全部按键：发送 `[]`。

常用 USB HID usage：

| 按键 | usage | 按键 | usage |
| --- | ---: | --- | ---: |
| `A` | `4` | `Z` | `29` |
| `1` | `30` | `0` | `39` |
| `Enter` | `40` | `Esc` | `41` |
| `Backspace` | `42` | `Tab` | `43` |
| `Space` | `44` | `Left Shift` | `225` |
| `Left Ctrl` | `224` | `Left Alt` | `226` |
| `Right Ctrl` | `228` | `Right Alt` | `230` |

键盘帧最多 30 个 usage，每个 usage 必须在 `0` 到 `239` 之间。桥接器会注册
一个虚拟 Universal HID 键盘，并在退出前发送空列表释放所有按键。

## 7. Python 调用示例

以下示例启动 USB 模式，等待 `ready`，发送一次 `A`，再释放键盘并退出。

```python
import json
import struct
import subprocess

exe = r"C:\Users\Ray\Documents\iphoneMirror\dist\UsbTouchBridge.exe"
p = subprocess.Popen(
    [exe, "--usb"],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    text=False,
)

def send(message):
    raw = json.dumps(message, separators=(",", ":")).encode("utf-8")
    p.stdin.write(struct.pack("<I", len(raw)))
    p.stdin.write(raw)
    p.stdin.flush()

while True:
    event = json.loads(p.stdout.readline())
    print(event)
    if event.get("event") == "ready":
        break
    if event.get("event") == "error":
        raise RuntimeError(event)

send({"schema":"iphoneMirror.touch.v2", "kind":"keyboard_batch",
      "seq":1, "timestampNs":0, "usages":[4]})
send({"schema":"iphoneMirror.touch.v2", "kind":"keyboard_batch",
      "seq":2, "timestampNs":0, "usages":[]})

p.stdin.close()
p.wait(timeout=10)
```

将启动参数替换为 `"--wireless"` 即可使用无线模式；也可以同时加入
`"--udid", "设备UDID"`。

## 8. 常见错误

| 错误/现象 | 处理方式 |
| --- | --- |
| `没有 USB 设备` | 检查数据线、信任状态、Apple Mobile Device Service 和 usbmuxd。 |
| `wireless_remote_pairing_required` | 先通过可信 USB 启动一次 USB 反控，以创建 RemotePairing 记录。 |
| `wireless_device_not_discoverable` | 确认同一局域网、iPhone 已解锁，并允许 Windows 防火墙的 Bonjour/mDNS（UDP 5353）。 |
| `wireless_remote_pairing_failed` | RemotePairing 隧道握手失败；重新 USB 初始化后再检查局域网与防火墙。 |
| `developer_mode_required` | 在设备的“设置 > 隐私与安全性 > 开发者模式”中开启开发者模式并重启设备。 |
| `developer_image_download_failed` / `developer_image_download_timeout` | DDI 下载或 GitHub 元数据解析失败；检查 GitHub 网络、限流状态后重试。 |
| `developer_image_download_integrity_failed` / `developer_image_download_rate_limited` | 下载内容未通过完整性校验或线路被限流；稍后重试，或用 `--ddi-dir` 提供官方本地镜像。 |
| `developer_image_tss_failed` | DDI 已下载，但 Apple 个性化服务或设备挂载失败；保持设备解锁并检查 Apple 服务网络。 |
| `touch_surface_unavailable` | 设备 DDI 未提供 mainTouchscreen（257）；桥会自动刷新一次，仍失败时重启设备后重试。 |
| `remote_control_unsupported_ios` 或 `9021` | 媒体流被拒且 direct Universal HID 也不可用；检查 DDI 后重试或改用蓝牙反控。 |
| `unsupported touch message schema` | 检查 `schema`、`kind`、字段名和长度前缀。 |
| 按键卡住 | 发送 `keyboard_batch` 且 `usages: []`；关闭程序时桥接器也会尝试自动释放。 |

## 9. 与 iPhoneMirror 主程序的关系

iPhoneMirror 主程序通过同样的 stdin/stdout 协议启动 bridge。当前 USB 控制
路径传入 `--usb`；无线设备传入 `--wireless`。无线桥接会先检查所选 UDID 的
usbmux `Network` 记录，缺失时再通过相同 UDID 的 RemotePairing 记录发现设备。
