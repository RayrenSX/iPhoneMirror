# 已配对设备的无线发现

v1.8.5-pre7 在投屏来源中合并同一设备的已验证连接身份。发现无线在线设备后，可以手动启动实验性 CoreDevice 投屏。操作及使用条件见[使用指南](USER_GUIDE.md#airplay-无线投屏)。

设备列表在后台浏览局域网内的 `_apple-mobdev2._tcp` 和 `_remotepairing._tcp`。
使用本机 Apple Lockdown 配对记录和已有 RemotePairing 记录验证设备，只有验证成功且
UDID 一致的设备才显示为“已配对 · 无线在线”。名称、随机 MAC、TXT identifier 均不用于猜测身份。
无需先开始 AirPlay 投屏，也无需依赖 Apple 服务提供的 Network 列表。

`_apple-mobdev2` 的 SRV 端口只用于广播发现，不直接作为 Lockdown 连接端口；
配对验证使用协议标准端口 62078。实机曾广播 32498，但只在 62078 接受连接。
每条路由轮流验证各配对身份，避免排在前面的失效记录耗尽扫描时间。

手机与电脑需要处于能互相发现的局域网。Lockdown 无线发现要求手机已启用 Wi-Fi 同步；
应用已有的 USB 信任后启用流程仍负责此设置。只有蓝牙绑定或只有 AirPlay 名称、没有
Apple/RemotePairing 配对凭据时，不能通过此功能认定手机在线。

每次发现最多浏览 3 秒、验证 8 秒，全局最多 4 个并发验证。两次扫描开始相隔至少 10 秒，
最近验证成功的记录保留最多 30 秒以容忍丢包；离线设备过期移除。已有 USB 设备和
正在投屏或使用反控的设备从后台验证目标中排除。发现结果不占用原生核心锁，不自动启动
HID、开发者服务、隧道或投屏，不弹出新的配对提示。配对凭据不会写入结果或日志。

USB 或投屏会话占用设备时，只暂停后台无线探测，保留已验证的无线记录。
停止 CoreDevice 投屏或拔掉 USB 后，立即恢复无线扫描，原卡片在最多 30 秒的验证过渡期内
保留，避免先移除再重新出现而丢失选中项。过渡期不会被普通刷新不断延长；设备确实离线且
没有新的验证结果时仍会过期移除。

USB 和 Wi-Fi 以 Apple UDID 合并，插拔后复用原有卡片。无线在线表示发现和配对验证通过，
不表示 USB 采集已就绪或 AirPlay 已连接。选择无线在线设备后，点击“开始投屏”会使用 CoreDevice 投屏；右键设备菜单不再提供单独入口。
CoreDevice 投屏需要 iOS/iPadOS 27 及以上和现有设备绑定、开发者服务条件。连接 USB 后，普通开始按钮继续使用有线采集。设备管理会使用现有无线反控连接流程，仍需既有身份绑定和设备条件。

已在设备绑定器确认属于同一设备的 AirPlay 与 USB/Wi-Fi 身份，在主列表和托盘列表中只显示
一张卡片。优先展示正在投屏的会话；会话结束后回到可用的 USB 或 Wi-Fi 连接，保留设备位置和
选择。同名或同机型不会作为合并依据，未绑定的 AirPlay 发送端继续独立显示。绑定器仍能看到
各连接身份；列表合并不会删除连接、停止会话或丢弃反控通道。

桥接协议新增独立只读命令：

```text
iUsbBridge.exe --discover-paired-devices
stdin:  {"excludeUdids": ["<正在使用的 Apple UDID>"]}
stdout: {"event":"paired_devices","devices":[{"udid":"...","name":"...","productType":"...","osVersion":"..."}]}
```

无设备时 devices 为空。发现服务不可用时返回非零退出码和 `paired_discovery_failed` 事件，
应用保留最近成功记录直到过期。该命令不能与反控或其他操作参数组合。

验证：`python -m unittest discover -s tests -p paired_device_discovery_test.py`；
托管逻辑测试参数 `--paired-device-discovery`；`scripts/test_usb_bridge_build_source.ps1`；
`scripts/verify_localization.ps1`。

协议参考：[pymobiledevice3 的无线身份识别说明](https://github.com/doronz88/pymobiledevice3/blob/master/misc/understanding_idevice_protocol_layers.md#wi-fi-recognizing-a-device-on-the-network)。
