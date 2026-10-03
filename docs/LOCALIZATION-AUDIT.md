# 多语言文字一致性审计表

审计日期：2026-10-03。新增独立台湾繁体中文资源。

## 审查范围

支持语言：简体中文（zh-CN）、香港繁体中文（zh-HK）、台湾繁体中文（zh-TW）、英文（en-US）。

扫描 476 个第一方源码/脚本文件，核对主程序、驱动管理器的八份语言字典、WPF XAML、C# 提示/错误、独立驱动清理脚本，以及安装器有效语言资源。

主程序覆盖主窗口、设备绑定、蓝牙/有线/无线控制、所有设置与状态窗口、采集恢复、截图、录制、推流、虚拟摄像头、更新、关于、诊断和开发者预览。

语言无关内容（产品名、协议、键名、单位、尺寸、路径、设备自报名称）保持原样。原生库、FFmpeg、Windows 返回的原始诊断及结构化日志事件/字段是技术数据，保留原文用于排错。开发/构建脚本、测试断言、第三方代码和许可证不作为应用 UI 字典翻译。台湾完整更新日志另行校验版本、条目、代码标记及链接；两个串流测试网页也纳入资源表。

硬编码表逐项列出保留的 XAML 品牌、协议、数字、符号和单位；node 是 XML 文档中的节点序号。启动故障的两条消息及五个备用标签均有独立四语回退：它们必须在语言字典加载失败时仍然可用。安装器的更新记录、卸载快捷方式也纳入对应表。

## 问题统计

| 分类 | 受影响条目数（可重叠） |
| --- | ---: |
| 缺失翻译 | 242 |
| 错误翻译 | 4 |
| 语义不一致 | 33 |
| 占位符问题 | 2 |
| 术语不一致 | 85 |
| 可读性问题 | 68 |

确认并修复的问题条目：429。问题状态表示**修改前**；表内四语为**修改后**。未发现问题的资源标为“✅ 完全一致”。

缺失翻译按硬编码消息/资源 Key 计数，不按缺少的语言单元格重复计数。静态未发现引用不等于已废弃，全部保留并列出，避免误删外部或动态调用。

## 多语言对照

### App

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| AboutDescription | 低延迟 iPhone 投屏、录制、直播推流与虚拟摄像头。 | 低延遲 iPhone 螢幕鏡像、錄製、直播串流與虛擬攝影機。 | 低延遲 iPhone 螢幕鏡像、錄製、直播串流與虛擬攝影機。 | Low-latency iPhone mirroring, recording, streaming, and virtual camera output. | — | ❌ 术语不一致 → ✅ 已修复 | 关于页沿用投屏、推流正式术语 |
| AboutSubtitle | 应用信息、更新和诊断 | 應用程式資料、更新和診斷 | 應用程式資訊、更新和診斷 | App information, updates, and diagnostics | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| AboutTitle | 关于 | 關於 | 關於 | About | — | ✅ 完全一致 | 保留 |
| AboutToolTip | 关于和更新设置 | 關於和更新設定 | 關於和更新設定 | About and update settings | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ActualFrameRate | 实际帧率 | 實際幀率 | 實際影格率 | Actual frame rate | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ActualResolution | 实际分辨率 | 實際解像度 | 實際解析度 | Actual resolution | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ActualStream | 当前 AirPlay 接收到的实际码流 | 目前 AirPlay 接收到的實際串流 | 目前 AirPlay 接收到的實際串流 | Actual AirPlay stream currently being received | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| AdvancedModeDisabled | 高级模式已关闭，恢复普通预设 | 進階模式已關閉，還原一般預設 | 進階模式已關閉，還原一般預設 | Advanced mode disabled; normal presets restored | — | ✅ 完全一致 | 保留 |
| AdvancedModeEnabled | 高级模式已开启 | 進階模式已開啟 | 進階模式已開啟 | Advanced mode enabled | — | ✅ 完全一致 | 保留 |
| AdvancedResolutionInvalid | 请输入 16–8192 范围内的有效宽度和高度。 | 請輸入 16–8192 範圍內的有效寬度和高度。 | 請輸入 16–8192 範圍內的有效寬度和高度。 | Enter valid width and height values from 16 to 8192. | — | ✅ 完全一致 | 保留 |
| AdvancedSettings | 高级设置 | 進階設定 | 進階設定 | Advanced settings | — | ✅ 完全一致 | 保留 |
| AdvancedSettingsIntro | 此设置仅作用于当前选中的设备，并直接写入 QuickTime USB 分辨率请求。 | 此設定只套用至目前選取的裝置，並直接寫入 QuickTime USB 解像度請求。 | 此設定只套用至目前選取的裝置，並直接寫入 QuickTime USB 解析度請求。 | This setting applies only to the selected device and writes the QuickTime USB resolution request directly. | — | ✅ 完全一致 | 保留 |
| AdvancedSettingsTitle | 高级 USB 设置 | 進階 USB 設定 | 進階 USB 設定 | Advanced USB settings | — | ✅ 完全一致 | 保留 |
| AdvancedUsbResolution | USB 请求分辨率 | USB 請求解像度 | USB 請求解析度 | USB request resolution | — | ✅ 完全一致 | 保留 |
| AdvancedUsbResolutionHelp | 输入例如 1920×1080。应用后将忽略普通画面预设；关闭高级模式后恢复普通预设。 | 輸入例如 1920×1080。套用後將忽略一般畫面預設；關閉進階模式後還原一般預設。 | 輸入例如 1920×1080。套用後將忽略一般畫面預設；關閉進階模式後還原一般預設。 | Enter a value such as 1920×1080. Applying this setting overrides normal preview presets; disabling advanced mode restores them. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| AirPlayEmptyState | 没有可用的 AirPlay 设备。请先在 iPhone 的“屏幕镜像”中连接此电脑。 | 沒有可用的 AirPlay 裝置。請先在 iPhone 的「螢幕鏡像」連接此電腦。 | 沒有可用的 AirPlay 裝置。請先在 iPhone 的「螢幕鏡像輸出」連線此電腦。 | No AirPlay devices available. Connect to this PC from Screen Mirroring on your iPhone. | — | ✅ 完全一致 | 保留 |
| AllowUpdateMirrorFallback | 自动选择最快更新下载线路 | 自動選擇最快更新下載線路 | 自動選擇最快更新下載線路 | Automatically select the fastest update route | — | ✅ 完全一致 | 保留 |
| AllowUpdateMirrorFallbackDescription | 测试 GitHub 和全部镜像，仅对可达线路进行 256 KB 下载测速；下载内容仍必须通过官方发布摘要中的 SHA-256 校验。 | 測試 GitHub 和全部鏡像，只對可達線路進行 256 KB 下載測速；下載內容仍必須通過官方發佈摘要中的 SHA-256 驗證。 | 測試 GitHub 和全部鏡像，只對可達線路進行 256 KB 下載測速；下載內容仍必須通過官方發布摘要中的 SHA-256 驗證。 | Tests GitHub and every mirror, then downloads a 256 KB sample from reachable routes; downloads must still pass SHA-256 verification from trusted release metadata. | — | ✅ 完全一致 | 保留 |
| AlreadyUpToDate | 当前已是所选发布通道的最新版本。 | 目前已是所選發佈通道的最新版本。 | 目前已是所選發布通道的最新版本。 | You have the latest version in the selected release channel. | — | ✅ 完全一致 | 保留 |
| AppPreferencesTitle | 应用设置 | 應用程式設定 | 應用程式設定 | App settings | — | ✅ 完全一致 | 保留 |
| AppSubtitle | Windows 有线 iPhone/iPad 屏幕采集 | Windows 有線 iPhone/iPad 螢幕擷取 | Windows 有線 iPhone/iPad 螢幕擷取 | Wired iPhone/iPad screen capture for Windows | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| AppSubtitleConnectivity | Windows USB 低延迟与 AirPlay 无线投屏 | Windows USB 低延遲與 AirPlay 無線螢幕鏡像 | Windows USB 低延遲與 AirPlay 無線螢幕鏡像 | Low-latency USB and wireless AirPlay mirroring for Windows | — | ✅ 完全一致 | 保留 |
| ApplicationModeComplete | 完整模式 | 完整模式 | 完整模式 | Complete | — | ✅ 完全一致 | 保留 |
| ApplicationModeLabel | 应用模式 | 應用模式 | 應用模式 | Application mode | — | ✅ 完全一致 | 保留 |
| ApplicationModeLightweight | 轻量模式 | 輕量模式 | 輕量模式 | Lightweight | — | ✅ 完全一致 | 保留 |
| ApplicationModeTray | 托盘模式 | 系統匣模式 | 系統匣模式 | System tray | — | ✅ 完全一致 | 保留 |
| AppliedRenderFormat | 已应用本地渲染：{0} · {1} fps | 已套用本機繪製：{0} · {1} fps | 已套用本機繪製：{0} · {1} fps | Local rendering applied: {0} · {1} fps | — | ✅ 完全一致 | 保留 |
| AppliedRenderLogFormat | 本地渲染设置：{0} / {1} fps — {2}；来源码流未重连 | 本機繪製設定：{0} / {1} fps — {2}；來源串流未重新連接 | 本機繪製設定：{0} / {1} fps — {2}；來源串流未重新連線 | Local rendering: {0} / {1} fps — {2}; source stream was not reconnected | — | ✅ 完全一致 | 保留 |
| Apply | 应用 | 套用 | 套用 | Apply | — | ✅ 完全一致 | 保留 |
| ApplyBluetoothMouseSettings | 应用设置 | 套用設定 | 套用設定 | Apply settings | — | ✅ 完全一致 | 保留 |
| ApplyLightweightMode | 一键切换轻量模式 | 一鍵切換輕量模式 | 一鍵切換輕量模式 | Switch to lightweight mode | — | ✅ 完全一致 | 保留 |
| ApplyReceiverName | 应用 | 套用 | 套用 | Apply | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ApplySettingsFailedFormat | 应用画面设置失败：{0} | 套用畫面設定失敗：{0} | 套用畫面設定失敗：{0} | Could not apply video settings: {0} | — | ✅ 完全一致 | 保留 |
| ApplyVideoSettings | 应用画面设置 | 套用畫面設定 | 套用畫面設定 | Apply video settings | — | ✅ 完全一致 | 保留 |
| ApplyWirelessSettings | 应用 | 套用 | 套用 | Apply | — | ✅ 完全一致 | 保留 |
| AudioLabel | 音频 | 音訊 | 音訊 | Audio | — | ✅ 完全一致 | 保留 |
| AudioPlaybackEnabled | Windows 声音播放已开启 | Windows 音訊播放已開啟 | Windows 音訊播放已開啟 | Windows audio playback enabled | — | ✅ 完全一致 | 保留 |
| AudioPlaybackMuted | Windows 声音播放已静音 | Windows 音訊播放已靜音 | Windows 音訊播放已靜音 | Windows audio playback muted | — | ✅ 完全一致 | 保留 |
| AudioStateUpdateFailed | 无法更新声音播放状态 | 無法更新音訊播放狀態 | 無法更新音訊播放狀態 | Could not update audio playback | — | ✅ 完全一致 | 保留 |
| AudioToggleUnsupported | 当前核心版本尚不支持运行时声音开关 | 現有核心版本尚不支援在執行階段切換音訊 | 現有核心版本尚不支援在執行階段切換音訊 | This core version does not support changing audio playback at runtime | — | ✅ 完全一致 | 保留 |
| AudioVolumeFormat | Windows 播放音量 {0:F0}% | Windows 播放音量 {0:F0}% | Windows 播放音量 {0:F0}% | Windows playback volume: {0:F0}% | — | ✅ 完全一致 | 保留 |
| AudioVolumeUnsupported | 当前核心版本尚不支持运行时音量调节 | 現有核心版本尚不支援在執行階段調節音量 | 現有核心版本尚不支援在執行階段調節音量 | This core version does not support runtime volume control | — | ✅ 完全一致 | 保留 |
| AudioVolumeUpdateFailed | 无法更新声音音量 | 無法更新音訊音量 | 無法更新音訊音量 | Could not update playback volume | — | ✅ 完全一致 | 保留 |
| AutoDownloadUpdates | 发现更新后自动开始下载 | 發現更新後自動開始下載 | 發現更新後自動開始下載 | Start downloading automatically when an update is found | — | ✅ 完全一致 | 保留 |
| BackImageSettings | 返回 | 返回 | 返回 | Back | — | ✅ 完全一致 | 保留 |
| BluetoothClientAddressFormat | 设备 ID：{0} | 裝置 ID：{0} | 裝置 ID：{0} | Device ID: {0} | — | ✅ 完全一致 | 保留 |
| BluetoothClientBindingConfirm | 确认绑定 | 確認綁定 | 確認綁定 | Confirm binding | — | ✅ 完全一致 | 保留 |
| BluetoothClientBindingHint | 首次使用此投屏设备时，请确认列表中的蓝牙客户端就是当前设备。之后切换选项卡会自动使用此绑定。 | 首次使用此螢幕鏡像裝置時，請確認清單中的藍牙用戶端就是目前裝置。之後切換分頁會自動使用此綁定。 | 首次使用此螢幕鏡像裝置時，請確認清單中的藍牙用戶端就是目前裝置。之後切換分頁會自動使用此綁定。 | The first time you use this mirrored device, confirm which Bluetooth client belongs to it. Future tab switches will use this binding automatically. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| BluetoothClientBindingRefresh | 刷新蓝牙客户端 | 重新整理藍牙用戶端 | 重新整理藍牙用戶端 | Refresh Bluetooth clients | — | ✅ 完全一致 | 保留 |
| BluetoothClientBindingTargetFormat | 请选择与“{0}”对应的蓝牙手机 | 請選擇與「{0}」對應的藍牙手機 | 請選擇與「{0}」對應的藍牙手機 | Choose the Bluetooth phone for “{0}” | — | ✅ 完全一致 | 保留 |
| BluetoothClientBindingTitle | 绑定蓝牙手机 | 綁定藍牙手機 | 綁定藍牙手機 | Bind Bluetooth phone | — | ✅ 完全一致 | 保留 |
| BluetoothClientBindingUnbind | 解除绑定 | 解除綁定 | 解除綁定 | Unbind | — | ✅ 完全一致 | 保留 |
| BluetoothClientBoundToFormat | 已绑定到：{0} | 已綁定至：{0} | 已綁定至：{0} | Bound to: {0} | — | ✅ 完全一致 | 保留 |
| BluetoothClientConnectionTimeFormat | 本次连接：{0} | 目前連接：{0} | 目前連線：{0} | Connected: {0} | — | ✅ 完全一致 | 保留 |
| BluetoothClientUnknownName | 未命名蓝牙设备 | 未命名藍牙裝置 | 未命名藍牙裝置 | Unnamed Bluetooth device | — | ✅ 完全一致 | 保留 |
| BluetoothControlConnected | iPhone/iPad 已连接蓝牙控制 | iPhone/iPad 已連接藍牙控制 | iPhone/iPad 已連線藍牙控制 | iPhone/iPad connected for Bluetooth control | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| BluetoothControlFailedBody | Windows 未能启动 BLE HID 外设服务。请检查蓝牙适配器和驱动是否支持外设模式。 | Windows 未能啟動 BLE HID 外設服務。請檢查藍牙適配器和驅動是否支援外設模式。 | Windows 無法啟動 BLE HID 周邊服務。請檢查藍牙介面卡和驅動程式是否支援周邊模式。 | Windows could not start the BLE HID peripheral service. Check whether the Bluetooth adapter and driver support peripheral mode. | — | ✅ 完全一致 | 保留 |
| BluetoothControlFailedStatus | 启动失败 | 啟動失敗 | 啟動失敗 | Startup failed | — | ✅ 完全一致 | 保留 |
| BluetoothControlFailedTitle | 蓝牙控制无法启动 | 藍牙控制無法啟動 | 藍牙控制無法啟動 | Bluetooth control could not start | — | ✅ 完全一致 | 保留 |
| BluetoothControlLabel | 蓝牙控制 | 藍牙控制 | 藍牙控制 | Bluetooth control | — | ✅ 完全一致 | 保留 |
| BluetoothControlOff | 蓝牙控制未启用 | 藍牙控制未啟用 | 藍牙控制未啟用 | Bluetooth control is off | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| BluetoothControlPairStepFive | iPhone：若再次出现配对提示窗口，点击“配对”。 | iPhone：若再次出現配對提示視窗，點選「配對」。 | iPhone：若再次出現配對提示視窗，點選「配對」。 | iPhone: If another pairing prompt appears, tap Pair again. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPairStepFour | Windows：在系统蓝牙配对窗口中点击“配对”。 | Windows：在系統藍牙配對視窗中點選「配對」。 | Windows：在系統藍牙配對視窗中點選「配對」。 | Windows: Click Pair in the system Bluetooth pairing dialog. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPairStepOneFormat | iPhone：打开“设置 → 辅助功能 → 触控 → 辅助触控”，开启“辅助触控”。 | iPhone：開啟「設定 → 輔助使用 → 觸控 → 輔助觸控」，開啟「輔助觸控」。 | iPhone：開啟「設定 → 輔助使用 → 觸控 → 輔助觸控」，開啟「輔助觸控」。 | iPhone: Open Settings → Accessibility → Touch → AssistiveTouch, then turn on AssistiveTouch. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPairStepThree | Windows：右下角出现“添加设备 - 点击以设置 iPhone”时，点击该系统通知。 | Windows：右下角出現「新增裝置 - 點選以設定 iPhone」時，點選系統通知。 | Windows：右下角出現「新增裝置 - 點選以設定 iPhone」時，點選系統通知。 | Windows: Click the “Add a device - click to set up iPhone” notification in the bottom-right corner. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPairStepTwo | iPhone：打开“设置 → 蓝牙”，连接“{0}”，然后点击“配对”。 | iPhone：開啟「設定 → 藍牙」，連接「{0}」，然後點選「配對」。 | iPhone：開啟「設定 → 藍牙」，連線「{0}」，然後點選「配對」。 | iPhone: Open Settings → Bluetooth, connect “{0}”, then tap Pair. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPromptAutoCloseFormat | {0} 秒后自动关闭 | {0} 秒後自動關閉 | {0} 秒後自動關閉 | Closes automatically in {0} s | — | ⚠️ 需要优化 → ✅ 已修复 | 秒使用单位 s，适用于倒计时 1 秒 |
| BluetoothControlPromptBodyFormat | Windows 鼠标输入已转发到 iPhone/iPad。按 {0} 释放鼠标并关闭反向控制。 | Windows 滑鼠輸入已轉發到 iPhone/iPad。按 {0} 釋放滑鼠並關閉反向控制。 | Windows 滑鼠輸入已轉發到 iPhone/iPad。按 {0} 釋放滑鼠並關閉反向控制。 | Windows mouse input is now redirected to the iPhone/iPad. Press {0} to release the mouse and stop reverse control. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPromptDetail | 反向控制期间 Windows 光标会隐藏。 | 反向控制期間 Windows 游標會隱藏。 | 反向控制期間 Windows 游標會隱藏。 | The Windows cursor is hidden while reverse control is active. | — | ✅ 完全一致 | 保留 |
| BluetoothControlPromptShortcutFormat | 按 {0} 释放鼠标 | 按 {0} 釋放滑鼠 | 按 {0} 釋放滑鼠 | Press {0} to release the mouse | — | ❌ 含义不一致 → ✅ 已修复 | 补齐释放对象 |
| BluetoothControlPromptTitle | 蓝牙控制已开启 | 藍牙控制已啟用 | 藍牙控制已啟用 | Bluetooth control enabled | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| BluetoothControlReportMapChangedBody | 此更新修改了蓝牙 HID 控制。请在 iPhone/iPad 上忽略现有的 iPhoneMirror 设备，然后重新配对，再启用控制。 | 此更新修改了藍牙 HID 控制。請在 iPhone/iPad 上忽略現有的 iPhoneMirror 裝置，然後重新配對，再啟用控制。 | 此更新修改了藍牙 HID 控制。請在 iPhone/iPad 上忽略現有的 iPhoneMirror 裝置，然後重新配對，再啟用控制。 | This update changes the Bluetooth HID controls. On the iPhone/iPad, forget the existing iPhoneMirror device, then pair it again before enabling control. | — | ✅ 完全一致 | 保留 |
| BluetoothControlReportMapChangedConfirm | 我已重新配对 | 我已重新配對 | 我已重新配對 | I have re-paired | — | ✅ 完全一致 | 保留 |
| BluetoothControlReportMapChangedDetail | 移除旧配对后关闭此提示。完成新配对后再次启用蓝牙控制。 | 移除舊配對後關閉此提示。完成新配對後再次啟用藍牙控制。 | 移除舊配對後關閉此提示。完成新配對後再次啟用藍牙控制。 | Close this message after the old pairing has been removed. Enable Bluetooth control again after completing the new pairing. | — | ✅ 完全一致 | 保留 |
| BluetoothControlReportMapChangedStatus | 需要重新配对 | 需要重新配對 | 需要重新配對 | Pairing required again | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| BluetoothControlReportMapChangedTitle | 蓝牙控制需要重新配对 | 藍牙控制需要重新配對 | 藍牙控制需要重新配對 | Bluetooth control needs to be paired again | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| BluetoothControlStartFailedFormat | 蓝牙控制启动失败：{0} | 藍牙控制啟動失敗：{0} | 藍牙控制啟動失敗：{0} | Bluetooth control startup failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| BluetoothControlWaiting | 蓝牙控制已广播，正在等待 iPhone/iPad 连接 | 藍牙控制已廣播，正在等待 iPhone/iPad 連接 | 藍牙控制已廣播，正在等待 iPhone/iPad 連線 | Bluetooth control is advertising and waiting for an iPhone/iPad | — | ✅ 完全一致 | 保留 |
| BluetoothControlWaitingBody | 请按以下顺序完成 iPhone 与 Windows 的系统配对。若 Windows 已连接过此 HID 设备，请先在蓝牙设置中断开，再重新启用控制并重新连接。 | 請依序完成 iPhone 與 Windows 的系統配對。若 Windows 已連接過此 HID 裝置，請先在藍牙設定中斷開，再重新啟用控制並重新連接。 | 請依序完成 iPhone 與 Windows 的系統配對。若 Windows 已連線過此 HID 裝置，請先在藍牙設定中中斷連線，再重新啟用控制並重新連線。 | Complete the iPhone and Windows system pairing in this order. If Windows is already connected to this HID device, disconnect it in Bluetooth settings before enabling control again and reconnecting. | — | ✅ 完全一致 | 保留 |
| BluetoothControlWaitingDetail | 连接后会弹出蓝牙客户端绑定窗口，请选择对应手机。绑定完成前不会发送输入；等待期间鼠标不会被隐藏或限制。 | 連接後會顯示藍牙用戶端綁定視窗，請選擇對應手機。完成綁定前不會傳送輸入；等待期間滑鼠不會被隱藏或限制。 | 連線後會顯示藍牙用戶端綁定視窗，請選擇對應手機。完成綁定前不會傳送輸入；等待期間滑鼠不會被隱藏或限制。 | After the phone connects, a Bluetooth client binding window will appear. Choose the matching phone; no input is sent before binding is confirmed, and the mouse remains available while waiting. | — | ✅ 完全一致 | 保留 |
| BluetoothControlWaitingStatus | 等待 HID 鼠标连接 | 等待 HID 滑鼠連接 | 等待 HID 滑鼠連線 | Waiting for HID mouse connection | — | ✅ 完全一致 | 保留 |
| BluetoothControlWaitingTargetFormat | 设备列表中通常显示为：{0} | 裝置清單中通常顯示為：{0} | 裝置清單中通常顯示為：{0} | This PC usually appears in the device list as: {0} | — | ✅ 完全一致 | 保留 |
| BluetoothControlWaitingTitle | 正在等待蓝牙控制连接 | 正在等待藍牙控制連接 | 正在等待藍牙控制連線 | Waiting for Bluetooth control | — | ✅ 完全一致 | 保留 |
| BluetoothCurrentOrientationFormat | 当前设备方向：{0}（{1}） | 目前裝置方向：{0}（{1}） | 目前裝置方向：{0}（{1}） | Current device orientation: {0} ({1}) | — | ✅ 完全一致 | 保留 |
| BluetoothDeviceOrientationLandscape | 横屏 | 橫向 | 橫向 | Landscape | — | ✅ 完全一致 | 保留 |
| BluetoothDeviceOrientationPortrait | 竖屏 | 直向 | 直向 | Portrait | — | ✅ 完全一致 | 保留 |
| BluetoothDeviceOrientationUnknown | 未识别 | 未識別 | 未識別 | Unknown | — | ✅ 完全一致 | 保留 |
| BluetoothEmptyState | 未发现已连接的蓝牙设备。请完成配对后刷新。 | 找不到已連接的藍牙裝置。請完成配對後重新整理。 | 找不到已連線的藍牙裝置。請完成配對後重新整理。 | No connected Bluetooth devices found. Complete pairing, then refresh. | — | ✅ 完全一致 | 保留 |
| BluetoothHidAdvertisingFailed | 蓝牙 HID 控制未能开始广播。 | 藍牙 HID 控制未能開始廣播。 | 藍牙 HID 控制未能開始廣播。 | Bluetooth HID control did not start advertising. | — | ✅ 完全一致 | 保留 |
| BluetoothHidAdvertisingFormat | 蓝牙 HID 控制正在广播（{0}）。 | 藍牙 HID 控制正在廣播（{0}）。 | 藍牙 HID 控制正在廣播（{0}）。 | Bluetooth HID control is advertising ({0}). | — | ✅ 完全一致 | 保留 |
| BluetoothHidAdvertisingNotReported | 蓝牙协议栈未报告广播状态。 | 藍牙協定堆疊未回報廣播狀態。 | 藍牙協定堆疊未回報廣播狀態。 | The Bluetooth stack did not report an advertising state. | — | ✅ 完全一致 | 保留 |
| BluetoothHidCharacteristicFailedFormat | 无法创建蓝牙 HID 特征 {0}：{1} | 無法建立藍牙 HID 特徵 {0}：{1} | 無法建立藍牙 HID 特徵 {0}：{1} | Could not create Bluetooth HID characteristic {0}: {1} | — | ❌ 缺失 → ✅ 已修复 | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 |
| BluetoothHidClientIdentificationFailed | 无法识别选定的蓝牙客户端。 | 無法識別所選藍牙用戶端。 | 無法識別所選藍牙用戶端。 | Could not identify the selected Bluetooth client. | — | ✅ 完全一致 | 保留 |
| BluetoothHidClientRefreshFailed | 断开连接后无法刷新蓝牙客户端。 | 中斷連接後無法重新整理藍牙用戶端。 | 中斷連線後無法重新整理藍牙用戶端。 | Could not refresh Bluetooth clients after disconnect. | — | ✅ 完全一致 | 保留 |
| BluetoothHidDisconnected | 蓝牙 HID 控制已断开。 | 藍牙 HID 控制已中斷。 | 藍牙 HID 控制已中斷。 | Bluetooth HID control disconnected. | — | ✅ 完全一致 | 保留 |
| BluetoothHidLowEnergyUnsupported | 蓝牙适配器不支持低功耗蓝牙。 | 藍牙適配器不支援低功耗藍牙。 | 藍牙介面卡不支援低功耗藍牙。 | The Bluetooth adapter does not support Bluetooth Low Energy. | — | ✅ 完全一致 | 保留 |
| BluetoothHidMultipleClientsWaiting | 多个蓝牙客户端已连接，正在等待选定的 iPhone/iPad。 | 多個藍牙用戶端已連接，正在等待所選 iPhone/iPad。 | 多個藍牙用戶端已連線，正在等待所選 iPhone/iPad。 | Multiple Bluetooth clients are connected; waiting for the selected iPhone/iPad. | — | ✅ 完全一致 | 保留 |
| BluetoothHidNotificationStalled | 蓝牙 HID 鼠标通知已停滞。 | 藍牙 HID 滑鼠通知已停滯。 | 藍牙 HID 滑鼠通知已停滯。 | Bluetooth HID mouse notification stalled. | — | ✅ 完全一致 | 保留 |
| BluetoothHidPeripheralSupported | 蓝牙适配器支持 BLE 外设模式。 | 藍牙適配器支援 BLE 外設模式。 | 藍牙介面卡支援 BLE 周邊模式。 | Bluetooth adapter supports BLE peripheral mode. | — | ✅ 完全一致 | 保留 |
| BluetoothHidPeripheralTrying | 尚未确认蓝牙适配器的外设能力，正在尝试 GATT 广播。 | 尚未確認藍牙適配器的外設能力，正在嘗試 GATT 廣播。 | 尚未確認藍牙介面卡的周邊能力，正在嘗試 GATT 廣播。 | Bluetooth adapter peripheral capability is unconfirmed; trying GATT advertising. | — | ✅ 完全一致 | 保留 |
| BluetoothHidPeripheralUnavailable | 此蓝牙适配器无法作为 BLE 外设。 | 此藍牙適配器無法作為 BLE 外設。 | 此藍牙介面卡無法作為 BLE 周邊。 | This Bluetooth adapter cannot act as a BLE peripheral. | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportBootKeyboard | HID 启动协议键盘报告 | HID 啟動協定鍵盤報告 | HID 啟動協定鍵盤報告 | HID boot keyboard report | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportBootMouse | HID 启动协议鼠标报告 | HID 啟動協定滑鼠報告 | HID 啟動協定滑鼠報告 | HID boot mouse report | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportConsumer | HID 媒体控制报告 | HID 媒體控制報告 | HID 媒體控制報告 | HID consumer-control report | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportDescriptorFailedFormat | 无法创建蓝牙 HID 报告描述符：{0} | 無法建立藍牙 HID 報告描述符：{0} | 無法建立藍牙 HID 報告描述元：{0} | Could not create the Bluetooth HID report descriptor: {0} | — | ❌ 缺失 → ✅ 已修复 | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 |
| BluetoothHidReportKeyboard | HID 键盘报告 | HID 鍵盤報告 | HID 鍵盤報告 | HID keyboard report | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportMouse | HID 鼠标报告 | HID 滑鼠報告 | HID 滑鼠報告 | HID mouse report | — | ✅ 完全一致 | 保留 |
| BluetoothHidReportNavigation | HID 导航控制报告 | HID 導航控制報告 | HID 導航控制報告 | HID navigation-control report | — | ✅ 完全一致 | 保留 |
| BluetoothHidRouteResetting | 正在重置蓝牙 HID 通道。 | 正在重設藍牙 HID 通道。 | 正在重設藍牙 HID 通道。 | The Bluetooth HID route is being reset. | — | ✅ 完全一致 | 保留 |
| BluetoothHidSelectedConnected | 选定的蓝牙 HID 客户端已连接。 | 所選藍牙 HID 用戶端已連接。 | 所選藍牙 HID 用戶端已連線。 | A selected Bluetooth HID client is connected. | — | ✅ 完全一致 | 保留 |
| BluetoothHidServiceCreateFailed | 无法创建蓝牙 HID 服务。 | 無法建立藍牙 HID 服務。 | 無法建立藍牙 HID 服務。 | Could not create the Bluetooth HID service. | — | ✅ 完全一致 | 保留 |
| BluetoothHidServiceCreateResultFormat | GattServiceProvider.CreateAsync 返回 {0}。 | GattServiceProvider.CreateAsync 傳回 {0}。 | GattServiceProvider.CreateAsync 傳回 {0}。 | GattServiceProvider.CreateAsync returned {0}. | — | ✅ 完全一致 | 保留 |
| BluetoothHidStartFailed | 无法启动蓝牙 HID 控制。 | 無法啟動藍牙 HID 控制。 | 無法啟動藍牙 HID 控制。 | Bluetooth HID control could not start. | — | ✅ 完全一致 | 保留 |
| BluetoothHidStoppedUnexpectedly | 蓝牙 HID 控制意外停止。 | 藍牙 HID 控制意外停止。 | 藍牙 HID 控制意外停止。 | Bluetooth HID control stopped unexpectedly. | — | ✅ 完全一致 | 保留 |
| BluetoothHidSubscribedFormat | 选定的 iPhone/iPad 已订阅{0}。 | 所選 iPhone/iPad 已訂閱{0}。 | 所選 iPhone/iPad 已訂閱{0}。 | Selected iPhone/iPad subscribed to the {0}. | — | ✅ 完全一致 | 保留 |
| BluetoothHidWaitingClient | 蓝牙 HID 控制正在广播，等待客户端连接。 | 藍牙 HID 控制正在廣播，等待用戶端連接。 | 藍牙 HID 控制正在廣播，等待用戶端連線。 | Bluetooth HID control is advertising; waiting for a client. | — | ✅ 完全一致 | 保留 |
| BluetoothHidWaitingMouseReport | 选定的蓝牙客户端已连接，正在等待其 HID 鼠标报告。 | 所選藍牙用戶端已連接，正在等待其 HID 滑鼠報告。 | 所選藍牙用戶端已連線，正在等待其 HID 滑鼠報告。 | The selected Bluetooth client is connected; waiting for its HID mouse report. | — | ✅ 完全一致 | 保留 |
| BluetoothHidWaitingSelected | 蓝牙客户端已连接，正在等待选定的 iPhone/iPad。 | 藍牙用戶端已連接，正在等待所選 iPhone/iPad。 | 藍牙用戶端已連線，正在等待所選 iPhone/iPad。 | Bluetooth clients are connected; waiting for the selected iPhone/iPad. | — | ✅ 完全一致 | 保留 |
| BluetoothHidWheelDescriptorFailedFormat | 无法创建蓝牙 HID 滚轮功能描述符：{0} | 無法建立藍牙 HID 滾輪功能描述符：{0} | 無法建立藍牙 HID 滾輪功能描述元：{0} | Could not create the Bluetooth HID wheel feature descriptor: {0} | — | ❌ 缺失 → ✅ 已修复 | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 |
| BluetoothHidWheelMultiplierFormat | HID 滚轮分辨率倍数已设为 {0}。 | HID 滾輪解像度倍數已設為 {0}。 | HID 滾輪解析度倍數已設為 {0}。 | HID wheel resolution multiplier set to {0}. | — | ✅ 完全一致 | 保留 |
| BluetoothLandscapeMouseDirection | 横屏鼠标方向 | 橫向滑鼠方向 | 橫向滑鼠方向 | Landscape mouse direction | — | ✅ 完全一致 | 保留 |
| BluetoothLandscapeMouseLeft90 | 向左 90° | 向左 90° | 向左 90° | Left 90° | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性；静态未发现引用，保留待后续调用核对 |
| BluetoothLandscapeMouseLeft90Reverse | 向左 90°（反向） | 向左 90°（反向） | 向左 90°（反向） | Left 90° (reversed) | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性；静态未发现引用，保留待后续调用核对 |
| BluetoothLandscapeMouseRight90 | 向右 90° | 向右 90° | 向右 90° | Right 90° | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性；静态未发现引用，保留待后续调用核对 |
| BluetoothLandscapeMouseRight90Reverse | 向右 90°（反向） | 向右 90°（反向） | 向右 90°（反向） | Right 90° (reversed) | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性；静态未发现引用，保留待后续调用核对 |
| BluetoothMouseDirectionDown | 下 | 下 | 下 | Down | — | ✅ 完全一致 | 保留 |
| BluetoothMouseDirectionHelp | 方向按当前画面宽高自动识别；独立窗口旋转会自动计入。 | 方向會按目前畫面寬高自動識別；獨立視窗旋轉會自動計入。 | 方向會按目前畫面寬高自動識別；獨立視窗旋轉會自動計入。 | Orientation is detected from the current frame size; independent-window rotation is included automatically. | — | ✅ 完全一致 | 保留 |
| BluetoothMouseDirectionLeft | 左 | 左 | 左 | Left | — | ✅ 完全一致 | 保留 |
| BluetoothMouseDirectionRight | 右 | 右 | 右 | Right | — | ✅ 完全一致 | 保留 |
| BluetoothMouseDirectionUp | 上 | 上 | 上 | Up | — | ✅ 完全一致 | 保留 |
| BluetoothMouseSensitivity | 蓝牙鼠标灵敏度 | 藍牙滑鼠靈敏度 | 藍牙滑鼠靈敏度 | Bluetooth mouse sensitivity | — | ✅ 完全一致 | 保留 |
| BluetoothMouseSettingsSaveFailed | 蓝牙反向控制鼠标设置保存失败 | 藍牙反向控制滑鼠設定儲存失敗 | 藍牙反向控制滑鼠設定儲存失敗 | Could not save Bluetooth control mouse settings | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| BluetoothMouseSettingsTitle | 反向控制设置 | 反向控制設定 | 反向控制設定 | Reverse control settings | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| BluetoothPortraitMouseDirection | 竖屏鼠标方向 | 直向滑鼠方向 | 直向滑鼠方向 | Portrait mouse direction | — | ✅ 完全一致 | 保留 |
| BluetoothReverseHorizontal | 左右反转 | 左右反轉 | 左右反轉 | Reverse left/right | — | ✅ 完全一致 | 保留 |
| BluetoothReverseVertical | 上下反转 | 上下反轉 | 上下反轉 | Reverse up/down | — | ✅ 完全一致 | 保留 |
| BluetoothWheelSensitivity | 滚轮灵敏度 | 滾輪靈敏度 | 滾輪靈敏度 | Wheel sensitivity | — | ✅ 完全一致 | 保留 |
| BrightnessLabel | 亮度 | 亮度 | 亮度 | Brightness | — | ✅ 完全一致 | 保留 |
| Cancel | 取消 | 取消 | 取消 | Cancel | — | ✅ 完全一致 | 保留 |
| CannotStartCapture | 无法开始投屏 | 無法開始螢幕鏡像 | 無法開始螢幕鏡像 | Could not start mirroring | — | ✅ 完全一致 | 保留 |
| CaptureActionDeviceDisconnected | 请重新连接并解锁设备后，再次开始投屏。 | 請重新連接並解鎖裝置後，再次開始螢幕鏡像。 | 請重新連線並解鎖裝置後，再次開始螢幕鏡像。 | Reconnect and unlock the device, then start mirroring again. | — | ✅ 完全一致 | 保留 |
| CaptureActionDriverRecovery | 请重新插拔设备后再试；仍无法投屏时重启电脑。 | 請重新插拔裝置後再試；仍無法螢幕鏡像時請重新啟動電腦。 | 請重新插拔裝置後再試；仍無法螢幕鏡像時請重新啟動電腦。 | Reconnect the device, then try again. If it still fails, restart the computer. | — | ✅ 完全一致 | 保留 |
| CaptureActionReconnectDevice | 请保持设备解锁，重新插拔数据线后再试。 | 請保持裝置解鎖，重新插拔數據線後再試。 | 請保持裝置解鎖，重新插拔傳輸線後再試。 | Keep the device unlocked, reconnect the cable, then try again. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| CaptureActionRestartApplication | 请关闭后重新打开 iPhoneMirror，再试一次。 | 請關閉後重新開啟 iPhoneMirror，再試一次。 | 請關閉後重新開啟 iPhoneMirror，再試一次。 | Close and reopen iPhoneMirror, then try again. | — | ✅ 完全一致 | 保留 |
| CaptureActionRestartDevice | 请重启 iPhone 或 iPad 后再试。 | 請重新啟動 iPhone 或 iPad 後再試。 | 請重新啟動 iPhone 或 iPad 後再試。 | Restart the iPhone or iPad, then try again. | — | ✅ 完全一致 | 保留 |
| CaptureActionRetry | 请停止投屏后重新开始；仍失败时重新连接设备。 | 請停止螢幕鏡像後重新開始；仍失敗時重新連接裝置。 | 請停止螢幕鏡像後重新開始；仍失敗時重新連線裝置。 | Stop and start mirroring again. If it still fails, reconnect the device. | — | ✅ 完全一致 | 保留 |
| CaptureActionVideoRetry | 请停止投屏后重新开始；仍无画面时重新连接数据线。 | 請停止螢幕鏡像後重新開始；仍無畫面時重新連接數據線。 | 請停止螢幕鏡像後重新開始；仍無畫面時重新連線傳輸線。 | Stop and start mirroring again. If there is still no picture, reconnect the cable. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| CaptureActionWaitForCleanup | 请等待设备清理完成后，再次开始投屏。 | 請等待裝置清理完成後，再次開始螢幕鏡像。 | 請等待裝置清理完成後，再次開始螢幕鏡像。 | Wait for device cleanup to finish, then start mirroring again. | — | ✅ 完全一致 | 保留 |
| CaptureActivating | 正在启用 USB 投屏接口… | 正在啟用 USB 螢幕鏡像介面… | 正在啟用 USB 螢幕鏡像介面… | Activating the USB mirroring interface… | — | ✅ 完全一致 | 保留 |
| CaptureCleaningDevice | 正在清理设备… | 正在清理裝置… | 正在清理裝置… | Cleaning up device… | — | ✅ 完全一致 | 保留 |
| CaptureDriverSafetyBlocked | 检测到旧版 libusb0 与 Apple USB 过滤驱动叠加。应用不会修改驱动；本次将使用保守的 USB 会话打开和释放顺序。 | 偵測到舊版 libusb0 與 Apple USB 篩選驅動程式疊加。應用程式不會修改驅動程式；本次會使用保守的 USB 工作階段開啟及釋放順序。 | 偵測到舊版 libusb0 與 Apple USB 篩選驅動程式疊加。應用程式不會修改驅動程式；本次會使用保守的 USB 工作階段開啟及釋放順序。 | Legacy libusb0 and Apple USB filters are stacked. The installed drivers are unchanged; this session uses a conservative USB open and teardown order. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| CaptureError | 投屏出现错误 | 螢幕鏡像出現錯誤 | 螢幕鏡像出現錯誤 | Mirroring error | — | ✅ 完全一致 | 保留 |
| CaptureErrorFormat | 投屏错误：{0} | 螢幕鏡像錯誤：{0} | 螢幕鏡像錯誤：{0} | Mirroring error: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| CaptureFailureChildProcess | 子进程异常退出 | 子程序異常結束 | 子程序異常結束 | Child process exited unexpectedly | — | ✅ 完全一致 | 保留 |
| CaptureFailureDetailsFormat | 错误类型：{0}<br>失败阶段：{1}<br>错误码：{2}<br><br>详细信息：{3} | 錯誤類型：{0}<br>失敗階段：{1}<br>錯誤碼：{2}<br><br>詳細資料：{3} | 錯誤類型：{0}<br>失敗階段：{1}<br>錯誤碼：{2}<br><br>詳細資料：{3} | Error type: {0}<br>Failure stage: {1}<br>Error code: {2}<br><br>Details: {3} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| CaptureFailureDeviceDisconnected | 设备已断开 | 裝置已中斷連接 | 裝置已中斷連線 | Device disconnected | — | ✅ 完全一致 | 保留 |
| CaptureFailureDriver | Apple/libusb 驱动异常 | Apple/libusb 驅動程式異常 | Apple/libusb 驅動程式異常 | Apple/libusb driver error | — | ✅ 完全一致 | 保留 |
| CaptureFailureExistingSession | 已存在旧投屏会话 | 仍存在舊的螢幕鏡像工作階段 | 仍存在舊的螢幕鏡像工作階段 | A previous mirroring session still exists | — | ✅ 完全一致 | 保留 |
| CaptureFailureNoFrames | 视频流无帧或黑屏 | 視訊串流沒有畫面或黑畫面 | 視訊串流沒有畫面或黑畫面 | No video frames or black screen | — | ✅ 完全一致 | 保留 |
| CaptureFailureSessionCreation | 投屏会话创建失败 | 螢幕鏡像工作階段建立失敗 | 螢幕鏡像工作階段建立失敗 | Mirroring session creation failed | — | ✅ 完全一致 | 保留 |
| CaptureFailureStatusFormat | 投屏错误：{0}（{1}，错误码 {2}） | 螢幕鏡像錯誤：{0}（{1}，錯誤碼 {2}） | 螢幕鏡像錯誤：{0}（{1}，錯誤碼 {2}） | Mirroring error: {0} ({1}, code {2}) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| CaptureFailureSystemClosed | 会话被系统关闭 | 工作階段已被系統關閉 | 工作階段已被系統關閉 | Session was closed by the system | — | ✅ 完全一致 | 保留 |
| CaptureFailureTimeout | 操作超时 | 操作逾時 | 操作逾時 | Operation timed out | — | ✅ 完全一致 | 保留 |
| CaptureFailureUnknown | 未知异常 | 未知錯誤 | 未知錯誤 | Unknown error | — | ✅ 完全一致 | 保留 |
| CaptureFailureUsbConnection | USB 设备连接失败 | USB 裝置連接失敗 | USB 裝置連線失敗 | USB device connection failed | — | ✅ 完全一致 | 保留 |
| CaptureFailureVideoDimensions | 视频流分辨率异常 | 視訊串流解像度異常 | 視訊串流解析度異常 | Invalid video stream dimensions | — | ✅ 完全一致 | 保留 |
| CaptureFailureVideoStream | 视频流建立或解码失败 | 視訊串流建立或解碼失敗 | 視訊串流建立或解碼失敗 | Video stream or decoding failed | — | ✅ 完全一致 | 保留 |
| CaptureHandshaking | 正在建立投屏会话… | 正在建立螢幕鏡像工作階段… | 正在建立螢幕鏡像工作階段… | Establishing the mirroring session… | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| CaptureIdle | 等待设备 | 等待裝置 | 等待裝置 | Waiting for a device | — | ✅ 完全一致 | 保留 |
| CaptureNoPingCableAction | 更换 Apple 原装或 MFi 认证数据线 | 更換 Apple 原裝或 MFi 認證數據線 | 更換 Apple 原廠或 MFi 認證傳輸線 | Replace the cable with an Apple original or MFi-certified cable | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| CaptureNoPingRecovery | 设备已连接，但长时间没有响应投屏握手。<br><br>请重启 iPhone 或 iPad，然后使用 Apple 原装或 MFi 认证数据线重新连接后重试。<br><br>重试时请保持设备解锁。 | 裝置已連接，但長時間沒有回應螢幕鏡像握手。<br><br>請重新啟動 iPhone 或 iPad，然後使用 Apple 原裝或 MFi 認證數據線重新連接後重試。<br><br>重試時請保持裝置解鎖。 | 裝置已連線，但長時間沒有回應螢幕鏡像握手。<br><br>請重新啟動 iPhone 或 iPad，然後使用 Apple 原廠或 MFi 認證傳輸線重新連線後重試。<br><br>重試時請保持裝置解鎖。 | The device is connected, but it did not respond to the mirroring handshake.<br><br>Restart the iPhone or iPad, then reconnect it with an Apple original or MFi-certified cable and try again.<br><br>Keep the device unlocked while retrying. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语；静态未发现引用，保留待后续调用核对 |
| CaptureNoPingRestartAction | 重启 iPhone 或 iPad | 重新啟動 iPhone 或 iPad | 重新啟動 iPhone 或 iPad | Restart the iPhone or iPad | — | ✅ 完全一致 | 保留 |
| CaptureNoPingSummary | 设备已连接，但长时间没有响应投屏握手。再次尝试前，请优先完成以下两项操作： | 裝置已連接，但長時間沒有回應螢幕鏡像握手。再次嘗試前，請優先完成以下兩項操作： | 裝置已連線，但長時間沒有回應螢幕鏡像握手。再次嘗試前，請優先完成以下兩項操作： | The device is connected, but it did not respond to the mirroring handshake. Complete these two steps before retrying: | — | ✅ 完全一致 | 保留 |
| CaptureNoPingTitle | iPhone/iPad 投屏连接超时 | iPhone/iPad 螢幕鏡像連接逾時 | iPhone/iPad 螢幕鏡像連線逾時 | iPhone/iPad mirroring connection timed out | — | ✅ 完全一致 | 保留 |
| CaptureNoPingUnlockHint | 重新连接并开始投屏时，请保持设备处于解锁状态。 | 重新連接並開始螢幕鏡像時，請保持裝置處於解鎖狀態。 | 重新連線並開始螢幕鏡像時，請保持裝置處於解鎖狀態。 | Keep the device unlocked when reconnecting and starting mirroring again. | — | ✅ 完全一致 | 保留 |
| CaptureNoticeErrorBadge | 投屏未能继续 | 螢幕鏡像未能繼續 | 螢幕鏡像未能繼續 | Mirroring could not continue | — | ✅ 完全一致 | 保留 |
| CaptureNoticeErrorHint | 请检查设备和连接后重试 | 請檢查裝置和連線後重試 | 請檢查裝置和連線後重試 | Check the device and connection, then retry | — | ✅ 完全一致 | 保留 |
| CaptureNoticeStoppedBadge | 投屏已停止 | 螢幕鏡像已停止 | 螢幕鏡像已停止 | Mirroring stopped | — | ✅ 完全一致 | 保留 |
| CaptureNoticeStoppedHint | 重启设备后可再次投屏 | 重新啟動裝置後可再次螢幕鏡像 | 重新啟動裝置後即可再次使用螢幕鏡像 | Restart the device before mirroring again | — | ✅ 完全一致 | 保留 |
| CaptureNoticeUsbBadge | USB 投屏配置未完成 | USB 螢幕鏡像設定未完成 | USB 螢幕鏡像設定未完成 | USB mirroring setup incomplete | — | ✅ 完全一致 | 保留 |
| CaptureNoticeUsbHint | 请重新插拔数据线后重试 | 請重新插拔數據線後重試 | 請重新插拔傳輸線後重試 | Reconnect the cable, then retry | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| CaptureQueued | 等待设备清理… | 等待裝置清理… | 等待裝置清理… | Waiting for device cleanup… | — | ✅ 完全一致 | 保留 |
| CaptureStageDecoder | 视频解码 | 視訊解碼 | 視訊解碼 | Video decoder | — | ✅ 完全一致 | 保留 |
| CaptureStageDeviceDiscovery | 设备发现 | 裝置探索 | 裝置探索 | Device discovery | — | ✅ 完全一致 | 保留 |
| CaptureStageDeviceReenumeration | 等待 USB 设备重新枚举 | 等待 USB 裝置重新枚舉 | 等待 USB 裝置重新列舉 | USB device re-enumeration | — | ✅ 完全一致 | 保留 |
| CaptureStageInterfaceOpen | 打开 QuickTime USB 接口 | 開啟 QuickTime USB 介面 | 開啟 QuickTime USB 介面 | QuickTime USB interface open | — | ✅ 完全一致 | 保留 |
| CaptureStageQuickTimeHandshake | QuickTime 会话握手 | QuickTime 工作階段交握 | QuickTime 工作階段交握 | QuickTime session handshake | — | ✅ 完全一致 | 保留 |
| CaptureStageSessionTeardown | 释放投屏会话 | 釋放螢幕鏡像工作階段 | 釋放螢幕鏡像工作階段 | Session teardown | — | ✅ 完全一致 | 保留 |
| CaptureStageUnknown | 未知阶段 | 未知階段 | 未知階段 | Unknown stage | — | ✅ 完全一致 | 保留 |
| CaptureStageUsbActivation | 激活 QuickTime USB 配置 | 啟用 QuickTime USB 設定 | 啟用 QuickTime USB 設定 | QuickTime USB activation | — | ✅ 完全一致 | 保留 |
| CaptureStageUsbPreflight | USB 设备预检 | USB 裝置預檢 | USB 裝置預檢 | USB device preflight | — | ✅ 完全一致 | 保留 |
| CaptureStageVideoStream | 建立视频流 | 建立視訊串流 | 建立視訊串流 | Video stream setup | — | ✅ 完全一致 | 保留 |
| CaptureStarted | 投屏已启动 | 螢幕鏡像已啟動 | 螢幕鏡像已啟動 | Mirroring started | — | ✅ 完全一致 | 保留 |
| CaptureStopped | 投屏已停止 | 螢幕鏡像已停止 | 螢幕鏡像已停止 | Mirroring stopped | — | ✅ 完全一致 | 保留 |
| CaptureStopping | 正在停止投屏… | 正在停止螢幕鏡像… | 正在停止螢幕鏡像… | Stopping mirroring… | — | ✅ 完全一致 | 保留 |
| CaptureStreaming | 正在投屏 | 正在螢幕鏡像 | 正在進行螢幕鏡像 | Mirroring | — | ✅ 完全一致 | 保留 |
| CaptureUsbConfigurationRecovery | 未能切换 iPhone 或 iPad 的 USB 投屏配置。<br><br>请依次尝试：<br>1. 拔下数据线，等待几秒后重新插入<br>2. 重启 iPhone 或 iPad 后再连接<br>3. 更换 Apple 原装或 MFi 认证数据线<br><br>重新连接并开始投屏时，请保持设备处于解锁状态。 | 未能切換 iPhone 或 iPad 的 USB 螢幕鏡像設定。<br><br>請依序嘗試：<br>1. 拔下數據線，等待幾秒後重新插入<br>2. 重新啟動 iPhone 或 iPad 後再連接<br>3. 更換 Apple 原裝或 MFi 認證數據線<br><br>重新連接並開始螢幕鏡像時，請保持裝置處於解鎖狀態。 | 未能切換 iPhone 或 iPad 的 USB 螢幕鏡像設定。<br><br>請依序嘗試：<br>1. 拔下傳輸線，等待幾秒後重新插入<br>2. 重新啟動 iPhone 或 iPad 後再連線<br>3. 更換 Apple 原廠或 MFi 認證傳輸線<br><br>重新連線並開始螢幕鏡像時，請保持裝置處於解鎖狀態。 | iPhone or iPad USB mirroring configuration could not be activated.<br><br>Try these steps in order:<br>1. Unplug the cable, wait a few seconds, and plug it back in<br>2. Restart the iPhone or iPad, then reconnect it<br>3. Replace the cable with an Apple original or MFi-certified cable<br><br>Keep the device unlocked when reconnecting and starting mirroring again. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语；静态未发现引用，保留待后续调用核对 |
| CaptureUsbConfigurationTitle | USB 投屏配置错误 | USB 螢幕鏡像設定錯誤 | USB 螢幕鏡像設定錯誤 | USB mirroring configuration error | — | ✅ 完全一致 | 保留 |
| CaptureUsbRestoreReplugRequired | 未确认设备已恢复普通 USB 配置，请重新插拔 iPhone 后再开始投屏。 | 未能確認裝置已還原普通 USB 設定，請重新插拔 iPhone 後再開始螢幕鏡像。 | 未能確認裝置已還原一般 USB 設定，請重新插拔 iPhone 後再開始螢幕鏡像。 | The normal USB configuration was not confirmed. Reconnect the iPhone before starting mirroring again. | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtected | 可能是受保护的视频内容 | 可能是受保護的影片內容 | 可能是受保護的影片內容 | Possible protected video content | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedAudioActive | 正在收到音频数据 | 正在收到音訊資料 | 正在收到音訊資料 | Audio samples are arriving | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedAudioActiveFormat | 正在收到音频数据 · {0:F0} kHz · {1} 声道 | 正在收到音訊資料 · {0:F0} kHz · {1} 聲道 | 正在收到音訊資料 · {0:F0} kHz · {1} 聲道 | Audio samples received · {0:F0} kHz · {1} ch | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedAudioBadgeActive | 有音频 | 有音訊 | 有音訊 | Audio on | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedAudioBadgeUnavailable | 无音频 | 無音訊 | 無音訊 | No audio | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedAudioUnavailable | 尚未收到音频数据 | 尚未收到音訊資料 | 尚未收到音訊資料 | No audio samples received | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedNoticeHint | 画面恢复后自动关闭 | 畫面恢復後自動關閉 | 畫面恢復後自動關閉 | Closes when video returns | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedNoticeProtection | 这可能与 DRM / FairPlay 或其他内容保护机制有关 | 這可能與 DRM / FairPlay 或其他內容保護機制有關 | 這可能與 DRM / FairPlay 或其他內容保護機制有關 | This may be related to DRM / FairPlay or another content-protection mechanism | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedNoticeTitle | 请确认当前视频投放是否正常？ | 請確認目前影片投放是否正常？ | 請確認目前影片投放是否正常？ | Is the current video cast working correctly? | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedNoticeVideo | 检测到当前视频画面持续为黑屏 | 偵測到目前影片畫面持續為黑畫面 | 偵測到目前影片畫面持續為黑畫面 | The current video has remained black | — | ✅ 完全一致 | 保留 |
| CaptureVideoProtectedTitle | 检测到持续黑色视频 | 偵測到持續黑色影片 | 偵測到影片持續顯示黑畫面 | Sustained black video detected | — | ✅ 完全一致 | 保留 |
| CaptureWaitingDevice | 等待 Apple 设备投屏接口… | 等待 Apple 裝置螢幕鏡像介面… | 等待 Apple 裝置螢幕鏡像介面… | Waiting for the Apple device mirroring interface… | — | ✅ 完全一致 | 保留 |
| Changelog | 更新日志 | 更新記錄 | 更新記錄 | Changelog | — | ✅ 完全一致 | 保留 |
| CheckForUpdates | 检查更新 | 檢查更新 | 檢查更新 | Check for updates | — | ✅ 完全一致 | 保留 |
| CheckOnStartup | 启动时检查更新 | 啟動時檢查更新 | 啟動時檢查更新 | Check for updates at startup | — | ✅ 完全一致 | 保留 |
| CheckingForUpdates | 正在连接更新服务检查新版本… | 正在連接更新服務檢查新版本… | 正在連線更新服務檢查新版本… | Checking for updates… | — | ⚠️ 需要优化 → ✅ 已修复 | 明确当前为更新检查状态 |
| CleanLogs | 清理日志和更新缓存 | 清理記錄和更新快取 | 清理記錄檔和更新快取 | Clean logs and update cache | — | ✅ 完全一致 | 保留 |
| ClearBluetoothBindings | 清除所有蓝牙绑定数据 | 清除所有藍牙綁定資料 | 清除所有藍牙綁定資料 | Clear all Bluetooth bindings | — | ✅ 完全一致 | 保留 |
| ClearBluetoothBindingsBody | 这会移除所有设备档案中的 Bluetooth 身份，已绑定的 USB 和 AirPlay 身份会保留。 | 這會移除所有裝置檔案中的 Bluetooth 身份，已綁定的 USB 和 AirPlay 身份會保留。 | 這會移除所有裝置設定檔中的 Bluetooth 身分，已綁定的 USB 和 AirPlay 身分會保留。 | This removes the Bluetooth identity from every device profile. Other USB and AirPlay identities remain bound. | — | ✅ 完全一致 | 保留 |
| ClearBluetoothBindingsFailed | 无法更新设备绑定档案。 | 無法更新裝置綁定檔案。 | 無法更新裝置綁定檔案。 | Could not update the device binding profiles. | — | ✅ 完全一致 | 保留 |
| ClearBluetoothBindingsTitle | 清除蓝牙绑定数据 | 清除藍牙綁定資料 | 清除藍牙綁定資料 | Clear Bluetooth bindings | — | ✅ 完全一致 | 保留 |
| ClearView | 清空视图 | 清除檢視 | 清除檢視 | Clear view | — | ✅ 完全一致 | 保留 |
| ClipboardTextTooLarge | 文本过长，无法一次粘贴。请缩短文本后重试。 | 文字過長，無法一次貼上。請縮短文字後重試。 | 文字過長，無法一次貼上。請縮短文字後重試。 | The text is too long to paste at once. Shorten it and try again. | — | ✅ 完全一致 | 保留 |
| Close | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| CloseCurrentInstance | 关闭当前窗口 | 關閉目前視窗 | 關閉目前視窗 | Close this window | — | ✅ 完全一致 | 保留 |
| CloseOtherInstances | 关闭其他窗口 | 關閉其他視窗 | 關閉其他視窗 | Close other windows | — | ⚠️ 需要优化 → ✅ 已修复 | 操作针对其他全部实例 |
| CloseOtherInstancesFailedFormat | 仍有 {0} 个 iPhoneMirror 实例未能关闭。请在任务栏或任务管理器中关闭后重试。 | 仍有 {0} 個 iPhoneMirror 執行個體未能關閉。請在工作列或工作管理員中關閉後重試。 | 仍有 {0} 個 iPhoneMirror 執行個體未能關閉。請在工作列或工作管理員中關閉後重試。 | iPhoneMirror instances still running: {0}. Close them from the taskbar or Task Manager, then try again. | — | ⚠️ 需要优化 → ✅ 已修复 | 数量可能大于 1，避免固定单数 instance/it |
| ClosingOtherInstances | 正在安全关闭其他 iPhoneMirror 窗口… | 正在安全關閉其他 iPhoneMirror 視窗… | 正在安全關閉其他 iPhoneMirror 視窗… | Safely closing other iPhoneMirror windows… | — | ⚠️ 需要优化 → ✅ 已修复 | 操作针对其他全部实例 |
| CompactLaunchDeviceUnavailable | 未找到目标设备，请连接并解锁设备后重试。 | 找不到目標裝置，請連接並解鎖裝置後再試。 | 找不到目標裝置，請連線並解鎖裝置後再試。 | The selected device was not found. Connect and unlock it, then try again. | — | ✅ 完全一致 | 保留 |
| CompactLaunchFailed | 独立窗口启动未完成：{0} | 獨立視窗啟動未完成：{0} | 獨立視窗啟動未完成：{0} | Could not complete independent window launch: {0} | — | ✅ 完全一致 | 保留 |
| CompactLaunchInvalidArguments | 请在 --device 后指定一个设备标识。 | 請在 --device 後指定一個裝置識別碼。 | 請在 --device 後指定一個裝置識別碼。 | Specify one device identifier after --device. | — | ✅ 完全一致 | 保留 |
| CompactLaunchMultipleDevices | 检测到多台设备，请在主窗口选择设备并创建专用快捷方式。 | 偵測到多部裝置，請在主視窗選擇裝置並建立專用捷徑。 | 偵測到多台裝置，請在主視窗選擇裝置並建立專用捷徑。 | Multiple devices are available. Select a device in the main window and create its shortcut. | — | ✅ 完全一致 | 保留 |
| CompactLaunchTitle | 独立窗口启动 | 獨立視窗啟動 | 獨立視窗啟動 | Independent window launch | — | ✅ 完全一致 | 保留 |
| CompactShortcutCreated | 已创建快捷方式：{0} | 已建立捷徑：{0} | 已建立捷徑：{0} | Shortcut created: {0} | — | ✅ 完全一致 | 保留 |
| ConnectionConnected | 已连接 | 已連接 | 已連線 | Connected | — | ✅ 完全一致 | 保留 |
| ConnectionDisconnected | 未连接 | 未連接 | 未連線 | Disconnected | — | ✅ 完全一致 | 保留 |
| ConnectionError | 设备连接异常 | 裝置連接異常 | 裝置連線異常 | Device connection error | — | ✅ 完全一致 | 保留 |
| ConnectionPaired | 已配对，请解锁设备 | 已配對，請解鎖裝置 | 已配對，請解鎖裝置 | Paired; unlock the device | — | ✅ 完全一致 | 保留 |
| ConnectionReady | 已连接并配对 | 已連接並配對 | 已連線並配對 | Connected and paired | — | ✅ 完全一致 | 保留 |
| ConnectionUsbNoMux | USB 已连接，等待 Apple 通道 | USB 已連接，等待 Apple 通道 | USB 已連線，等待 Apple 通道 | USB connected; waiting for Apple channel | — | ✅ 完全一致 | 保留 |
| Continue | 继续 | 繼續 | 繼續 | Continue | — | ✅ 完全一致 | 保留 |
| ContrastLabel | 对比度 | 對比度 | 對比 | Contrast | — | ✅ 完全一致 | 保留 |
| ControlAutoReconnectingChannel | 控制通道暂时中断，正在自动重新连接… | 控制通道暫時中斷，正在自動重新連接… | 控制通道暫時中斷，正在自動重新連線… | The control channel was interrupted. Reconnecting automatically… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBindingAdvice | 请关闭此窗口，打开“设备绑定器”，为当前设备绑定有线 USB 身份；如使用 AirPlay 镜像，请将其绑定到同一台设备的档案，然后重新启动有线或无线控制。 | 請關閉此視窗，開啟「裝置綁定器」，為目前裝置綁定有線 USB 身分；如使用 AirPlay 鏡像，請將其綁定至同一台裝置的檔案，然後重新啟動有線或無線控制。 | 請關閉此視窗，開啟「裝置綁定」，為目前裝置綁定有線 USB 身分；如使用 AirPlay 鏡像，請將其綁定至同一台裝置的設定檔，然後重新啟動有線或無線控制。 | Close this window and open the device binder. Bind this device's wired USB identity; if using AirPlay mirroring, bind it to the same device profile. Then start wired or wireless control again. | — | ✅ 完全一致 | 保留 |
| ControlBindingNotFound | 未找到设备绑定信息。 | 找不到裝置綁定資料。 | 找不到裝置綁定資料。 | Device binding information was not found. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源；静态未发现引用，保留待后续调用核对 |
| ControlBindingRequired | 当前设备尚未完成正确绑定，请先完成设备绑定后再使用反向控制。 | 目前裝置尚未完成正確綁定，請先完成裝置綁定後再使用反向控制。 | 目前裝置尚未完成正確綁定，請先完成裝置綁定後再使用反向控制。 | This device is not correctly bound. Complete device binding before using reverse control. | — | ✅ 完全一致 | 保留 |
| ControlBluetoothBindingAdvice | 请先在设备绑定器中为当前设备完成蓝牙绑定，再启动蓝牙控制。 | 請先在裝置綁定器中為目前裝置完成藍牙綁定，再啟動藍牙控制。 | 請先在裝置綁定中為目前裝置完成藍牙綁定，再啟動藍牙控制。 | Complete the Bluetooth binding for this device in the device binder, then start Bluetooth control. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothBindingRequired | 尚未完成蓝牙设备绑定 | 尚未完成藍牙裝置綁定 | 尚未完成藍牙裝置綁定 | Bluetooth device binding is incomplete | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothCancelled | 已取消蓝牙控制 | 已取消藍牙控制 | 已取消藍牙控制 | Bluetooth control cancelled | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothDisconnected | 设备已断开，请重新连接蓝牙… | 裝置已中斷連線，請重新連接藍牙… | 裝置已中斷連線，請重新連線藍牙… | The device disconnected. Reconnect it over Bluetooth… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothPairingTitle | 请在设备和 Windows 上完成蓝牙配对 | 請在裝置及 Windows 上完成藍牙配對 | 請在裝置及 Windows 上完成藍牙配對 | Complete Bluetooth pairing on your device and Windows | — | ❌ 含义不一致 → ✅ 已修复 | 阶段要求为配对，同时适用于 iPhone/iPad |
| ControlBluetoothReadyToPair | 蓝牙已就绪，请按下方步骤在设备上完成连接… | 藍牙已就緒，請按下方步驟在裝置上完成連線… | 藍牙已就緒，請按下方步驟在裝置上完成連線… | Bluetooth is ready. Follow the steps below to connect your device… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothStartFailed | 无法启用蓝牙控制 | 無法啟用藍牙控制 | 無法啟用藍牙控制 | Could not enable Bluetooth control | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlBluetoothTouchFailed | 蓝牙触控检查未通过 | 藍牙觸控檢查未通過 | 藍牙觸控檢查未通過 | Bluetooth touch check failed | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckConnectionAdvice | 请检查设备连接后重试。 | 請檢查裝置連線後重試。 | 請檢查裝置連線後重試。 | Check the device connection, then try again. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckingBluetoothBinding | 正在检查设备档案中的蓝牙绑定… | 正在檢查裝置檔案中的藍牙綁定… | 正在檢查裝置設定檔中的藍牙綁定… | Checking the Bluetooth binding in the device profile… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckingBluetoothSupport | 正在检查电脑蓝牙是否已开启并支持 BLE… | 正在檢查電腦藍牙是否已開啟並支援 BLE… | 正在檢查電腦藍牙是否已開啟並支援 BLE… | Checking that Bluetooth is on and supports BLE… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckingDeviceBinding | 正在检查设备和绑定状态… | 正在檢查裝置和綁定狀態… | 正在檢查裝置和綁定狀態… | Checking the device and its bindings… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckingDevicePermissions | 正在检查设备权限和连接状态… | 正在檢查裝置權限和連線狀態… | 正在檢查裝置權限和連線狀態… | Checking device permissions and connection status… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCheckingHidChannel | 设备已连接，正在检查 HID 触控通道… | 裝置已連接，正在檢查 HID 觸控通道… | 裝置已連線，正在檢查 HID 觸控通道… | The device is connected. Checking the HID touch channel… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlCloseCountdown | 窗口将在 {0} 秒后关闭 | 視窗將於 {0} 秒後關閉 | 視窗將於 {0} 秒後關閉 | This window closes in {0} s | — | ⚠️ 需要优化 → ✅ 已修复 | 秒使用单位 s，适用于倒计时 1 秒 |
| ControlConnectingChannel | 正在建立设备控制通道… | 正在建立裝置控制通道… | 正在建立裝置控制通道… | Establishing the device control channel… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlDeviceNotReady | 设备未就绪或尚未完成绑定。 | 裝置未就緒或尚未完成綁定。 | 裝置未就緒或尚未完成綁定。 | The device is not ready or has not been bound. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlHidRouteFailed | HID 鼠标通道已连接，但触控路由校验失败。 | HID 滑鼠通道已連接，但觸控路由驗證失敗。 | HID 滑鼠通道已連線，但觸控路由驗證失敗。 | The HID mouse channel is connected, but touch routing validation failed. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlInitializingInput | 正在初始化触控和输入服务… | 正在初始化觸控和輸入服務… | 正在初始化觸控和輸入服務… | Initializing touch and input services… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlOperationFailed | 反向控制失败 | 反向控制失敗 | 反向控制失敗 | Reverse control failed | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlPreparing | 正在准备控制连接… | 正在準備控制連線… | 正在準備控制連線… | Preparing the control connection… | — | ✅ 完全一致 | 保留 |
| ControlPreparingInput | 正在准备鼠标、键盘和系统控制… | 正在準備滑鼠、鍵盤和系統控制… | 正在準備滑鼠、鍵盤和系統控制… | Preparing mouse, keyboard, and system controls… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlPreparingSupportFiles | 正在准备设备所需的支持文件… | 正在準備裝置所需的支援檔案… | 正在準備裝置所需的支援檔案… | Preparing the support files required by the device… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlPrerequisiteCancelled | 已取消启动前的确认。 | 已取消啟動前的確認。 | 已取消啟動前的確認。 | The startup confirmation was cancelled. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlProgressActive | 进行中 | 進行中 | 進行中 | In progress | — | ✅ 完全一致 | 保留 |
| ControlProgressCompleted | 已完成 | 已完成 | 已完成 | Completed | — | ✅ 完全一致 | 保留 |
| ControlProgressFailed | 失败 | 失敗 | 失敗 | Failed | — | ✅ 完全一致 | 保留 |
| ControlProgressPending | 等待中 | 等待中 | 等待中 | Pending | — | ✅ 完全一致 | 保留 |
| ControlProgressSkipped | 已跳过 | 已略過 | 已略過 | Skipped | — | ✅ 完全一致 | 保留 |
| ControlReadyDescription | 反向控制已就绪 | 反向控制已就緒 | 反向控制已就緒 | Reverse control is ready | — | ❌ 缺失 → ✅ 已修复 | 控制就绪默认提示改为运行时查找资源 |
| ControlReconnectingChannel | 控制通道暂时中断，正在尝试重新连接… | 控制通道暫時中斷，正在嘗試重新連接… | 控制通道暫時中斷，正在嘗試重新連線… | The control channel was interrupted. Reconnecting… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlRecoveryFailed | 自动重连失败 | 自動重新連接失敗 | 自動重新連線失敗 | Automatic reconnection failed | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlStageCancelled | 已取消反向控制 | 已取消反向控制 | 已取消反向控制 | Reverse control cancelled | — | ✅ 完全一致 | 保留 |
| ControlStageCheckingBinding | 检查设备绑定 | 檢查裝置綁定 | 檢查裝置綁定 | Checking device binding | — | ✅ 完全一致 | 保留 |
| ControlStageCheckingBluetooth | 检查电脑蓝牙 | 檢查電腦藍牙 | 檢查電腦藍牙 | Checking Bluetooth | — | ✅ 完全一致 | 保留 |
| ControlStageCheckingDevice | 检查设备 | 檢查裝置 | 檢查裝置 | Checking device | — | ✅ 完全一致 | 保留 |
| ControlStageCheckingPermissions | 检查设备权限 | 檢查裝置權限 | 檢查裝置權限 | Checking device permissions | — | ✅ 完全一致 | 保留 |
| ControlStageConnecting | 建立控制连接 | 建立控制連線 | 建立控制連線 | Establishing the control connection | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| ControlStageFailed | 无法启用反向控制 | 無法啟用反向控制 | 無法啟用反向控制 | Could not enable reverse control | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlStageInitializingServices | 初始化控制服务 | 初始化控制服務 | 初始化控制服務 | Initializing control services | — | ✅ 完全一致 | 保留 |
| ControlStagePreparingDeviceSupport | 准备设备支持文件 | 準備裝置支援檔案 | 準備裝置支援檔案 | Preparing device support files | — | ❌ 含义不一致 → ✅ 已修复 | 补齐准备对象为文件 |
| ControlStageReady | 控制已连接 | 控制已連線 | 控制已連線 | Control connected | — | ✅ 完全一致 | 保留 |
| ControlStageRecovering | 正在恢复控制连接 | 正在恢復控制連線 | 正在恢復控制連線 | Recovering control connection | — | ✅ 完全一致 | 保留 |
| ControlStageStartingInputRouter | 启动输入控制 | 啟動輸入控制 | 啟動輸入控制 | Starting input control | — | ✅ 完全一致 | 保留 |
| ControlStageStopping | 正在断开控制连接 | 正在中斷控制連線 | 正在中斷控制連線 | Disconnecting control | — | ✅ 完全一致 | 保留 |
| ControlStageSwitchingBluetoothPeripheral | 切换蓝牙外设模式 | 切換藍牙周邊模式 | 切換藍牙周邊模式 | Switching to Bluetooth peripheral mode | — | ❌ 含义不一致 → ✅ 已修复 | 与实际切换阶段及中文一致 |
| ControlStageVerifyingTouch | 检查触控功能 | 檢查觸控功能 | 檢查觸控功能 | Checking touch input | — | ✅ 完全一致 | 保留 |
| ControlStageWaitingForPhoneConnection | 等待手机连接 | 等待手機連線 | 等待手機連線 | Waiting for phone connection | — | ✅ 完全一致 | 保留 |
| ControlSwitchingBluetoothMode | 正在将电脑蓝牙切换至 HID 外设模式… | 正在將電腦藍牙切換至 HID 周邊模式… | 正在將電腦藍牙切換至 HID 周邊模式… | Switching the computer's Bluetooth to HID peripheral mode… | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlTitle | 投屏设置 | 螢幕鏡像設定 | 螢幕鏡像設定 | Mirroring settings | — | ✅ 完全一致 | 保留 |
| ControlTotalDuration | 本次连接耗时 {0:0.0} 秒 | 本次連線耗時 {0:0.0} 秒 | 本次連線耗時 {0:0.0} 秒 | Connection time: {0:0.0} s | — | ⚠️ 需要优化 → ✅ 已修复 | 与倒计时单位保持一致 |
| ControlWiredCancelled | 已取消有线控制 | 已取消有線控制 | 已取消有線控制 | Wired control cancelled | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlWiredRetryFormat | 正在自动重新连接有线控制（第 {0} 次尝试）… | 正在自動重新連接有線控制（第 {0} 次嘗試）… | 正在自動重新連線有線控制（第 {0} 次嘗試）… | Reconnecting wired control automatically (attempt {0})… | — | ❌ 缺失 → ✅ 已修复 | 自动重连计数提示迁入三语格式资源 |
| ControlWiredStartFailed | 无法启用有线控制 | 無法啟用有線控制 | 無法啟用有線控制 | Could not enable wired control | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源；静态未发现引用，保留待后续调用核对 |
| ControlWirelessCancelled | 已取消无线控制 | 已取消無線控制 | 已取消無線控制 | Wireless control cancelled | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlWirelessRecoveryDisconnected | 无线控制通道在自动重连完成前已断开。 | 無線控制通道在自動重新連接完成前已中斷連線。 | 無線控制通道在自動重新連線完成前已中斷連線。 | The wireless control channel disconnected before automatic reconnection completed. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlWirelessRecoveryFailed | 无线控制自动重连失败 | 無線控制自動重新連接失敗 | 無線控制自動重新連線失敗 | Automatic wireless control reconnection failed | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlWirelessRetryFormat | 正在自动重新连接无线控制（第 {0} 次尝试）… | 正在自動重新連接無線控制（第 {0} 次嘗試）… | 正在自動重新連線無線控制（第 {0} 次嘗試）… | Reconnecting wireless control automatically (attempt {0})… | — | ❌ 缺失 → ✅ 已修复 | 自动重连计数提示迁入三语格式资源 |
| ControlWirelessStartFailed | 无法启用无线控制 | 無法啟用無線控制 | 無法啟用無線控制 | Could not enable wireless control | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源；静态未发现引用，保留待后续调用核对 |
| ControlWirelessStartupDisconnected | 无线控制通道在启动完成前已断开。 | 無線控制通道在啟動完成前已中斷連線。 | 無線控制通道在啟動完成前已中斷連線。 | The wireless control channel disconnected before startup completed. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| ControlWirelessStartupFailed | 无线控制启动失败 | 無線控制啟動失敗 | 無線控制啟動失敗 | Wireless control failed to start | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| CoreLoadFailedFormat | 核心加载失败：{0} | 核心載入失敗：{0} | 核心載入失敗：{0} | Core load failed: {0} | — | ✅ 完全一致 | 保留 |
| CreateCompactShortcut | 创建当前设备的桌面快捷方式 | 建立目前裝置的桌面捷徑 | 建立目前裝置的桌面捷徑 | Create a desktop shortcut for this device | — | ✅ 完全一致 | 保留 |
| CurrentDevice | 当前设备 | 目前裝置 | 目前裝置 | Current device | — | ✅ 完全一致 | 保留 |
| CurrentPreviewResolutionFormat | {0} x {1}（当前预览） | {0} x {1}（目前預覽） | {0} x {1}（目前預覽） | {0} x {1} (current preview) | — | ✅ 完全一致 | 保留 |
| CurrentVersionLabel | 当前版本 | 目前版本 | 目前版本 | Current version | — | ✅ 完全一致 | 保留 |
| DecoderAuto | 自动（推荐） | 自動（推薦） | 自動（推薦） | Auto (recommended) | — | ✅ 完全一致 | 保留 |
| DecoderHardwarePreferred | 硬件优先 | 硬件優先 | 硬體優先 | Hardware preferred | — | ✅ 完全一致 | 保留 |
| DecoderPreferenceAppliedFormat | 已应用解码器：{0} | 已套用解碼器：{0} | 已套用解碼器：{0} | Decoder applied: {0} | — | ✅ 完全一致 | 保留 |
| DecoderPreferenceLabel | 解码器 | 解碼器 | 解碼器 | Decoder | — | ✅ 完全一致 | 保留 |
| DecoderPreferenceQueuedFormat | 已请求切换解码器：{0}（将在关键帧生效） | 已請求切換解碼器：{0}（將在關鍵幀生效） | 已請求切換解碼器：{0}（將在關鍵影格生效） | Decoder switch requested: {0} (applies at a keyframe) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DecoderPreferenceSelectedFormat | 已选择解码器：{0} | 已選擇解碼器：{0} | 已選擇解碼器：{0} | Decoder selected: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DecoderPreferenceSubmittedFormat | 解码器设置已提交：{0}；请在预览下方查看实际状态。 | 解碼器設定已提交：{0}；請在預覽下方查看實際狀態。 | 解碼器設定已提交：{0}；請在預覽下方查看實際狀態。 | Decoder setting submitted: {0}; see the status below the preview for the active mode. | — | ✅ 完全一致 | 保留 |
| DecoderRuntimeExternal | 无线接收器解码 | 無線接收器解碼 | 無線接收器解碼 | wireless receiver decoder | — | ✅ 完全一致 | 保留 |
| DecoderRuntimeHardware | 硬件加速 | 硬件加速 | 硬體加速 | hardware acceleration | — | ✅ 完全一致 | 保留 |
| DecoderRuntimeSoftware | 软件解码 | 軟件解碼 | 軟體解碼 | software decoding | — | ✅ 完全一致 | 保留 |
| DecoderRuntimeUnknown | 检测中 | 偵測中 | 偵測中 | detecting | — | ✅ 完全一致 | 保留 |
| DecoderSoftwareCompatible | 软件兼容 | 軟件相容 | 軟體相容 | Software compatibility | — | ✅ 完全一致 | 保留 |
| DecoderStatusAppliedFormat | 解码器已生效：策略 {0}；实际 {1}。 | 解碼器已生效：策略 {0}；實際 {1}。 | 解碼器已生效：策略 {0}；實際 {1}。 | Decoder active: policy {0}; actual {1}. | — | ✅ 完全一致 | 保留 |
| DecoderStatusDetecting | 解码器状态：正在读取实际解码链路… | 解碼器狀態：正在讀取實際解碼鏈路… | 解碼器狀態：正在讀取實際解碼鏈路… | Decoder status: detecting the active decode path… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| DecoderStatusFailedFormat | 解码器切换失败：已保留 {0}（实际 {1}），将于后续关键帧重试 {2}。 | 解碼器切換失敗：已保留 {0}（實際 {1}），將於後續關鍵幀重試 {2}。 | 解碼器切換失敗：已保留 {0}（實際 {1}），將於後續關鍵影格重試 {2}。 | Decoder switch failed: retained {0} (actual {1}); {2} will retry at a later keyframe. | — | ✅ 完全一致 | 保留 |
| DecoderStatusPendingFormat | 解码器切换中：{0} -&gt; {1}；当前实际 {2}，正在等待关键帧。 | 解碼器切換中：{0} -&gt; {1}；目前實際 {2}，正在等待關鍵幀。 | 解碼器切換中：{0} -&gt; {1}；目前實際 {2}，正在等待關鍵影格。 | Decoder switch pending: {0} -&gt; {1}; active engine {2}, waiting for a keyframe. | — | ✅ 完全一致 | 保留 |
| DeveloperAbout | 关于与更新 | 關於與更新 | 關於與更新 | About and updates | — | ✅ 完全一致 | 保留 |
| DeveloperAboutDescription | 版本、诊断和更新信息 | 版本、診斷和更新資料 | 版本、診斷和更新資料 | Version, diagnostics, and updates | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeveloperActivate | 激活 | 啟用 | 啟用 | Activate | — | ✅ 完全一致 | 保留 |
| DeveloperAdvancedSettings | 高级 USB 设置 | 進階 USB 設定 | 進階 USB 設定 | Advanced USB settings | — | ✅ 完全一致 | 保留 |
| DeveloperAdvancedSettingsDescription | 分辨率输入验证 | 解像度輸入驗證 | 解析度輸入驗證 | Resolution input validation | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeveloperAirPlaySelectionWindow | AirPlay 设备选择 | AirPlay 裝置選擇 | AirPlay 裝置選擇 | AirPlay device selection | — | ✅ 完全一致 | 保留 |
| DeveloperBluetoothClientBindingWindow | 蓝牙客户端绑定 | 藍牙用戶端綁定 | 藍牙用戶端綁定 | Bluetooth client binding | — | ✅ 完全一致 | 保留 |
| DeveloperBluetoothConnectionWindow | 蓝牙连接窗口 | 藍牙連接視窗 | 藍牙連線視窗 | Bluetooth connection window | — | ✅ 完全一致 | 保留 |
| DeveloperBluetoothControlNoticeWindow | 蓝牙控制通知 | 藍牙控制通知 | 藍牙控制通知 | Bluetooth control notice | — | ✅ 完全一致 | 保留 |
| DeveloperCaptureError | 投屏错误弹窗 | 螢幕鏡像錯誤提示 | 螢幕鏡像錯誤提示 | Mirroring error prompt | — | ✅ 完全一致 | 保留 |
| DeveloperCaptureErrorDescription | 视频流失败后的错误提示 | 影片串流失敗後顯示的錯誤提示 | 影片串流失敗後顯示的錯誤提示 | Error shown after a video-stream failure | — | ✅ 完全一致 | 保留 |
| DeveloperCaptureRecovery | 采集恢复提示 | 擷取恢復提示 | 擷取恢復提示 | Capture recovery prompt | — | ✅ 完全一致 | 保留 |
| DeveloperCaptureRecoveryDescription | 无响应时的恢复说明 | 擷取沒有回應時的恢復說明 | 擷取沒有回應時的恢復說明 | Recovery guidance for an unresponsive capture | — | ✅ 完全一致 | 保留 |
| DeveloperClose | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| DeveloperDeviceBindingWindow | 设备绑定窗口 | 裝置綁定視窗 | 裝置綁定視窗 | Device binding window | — | ✅ 完全一致 | 保留 |
| DeveloperDevices | 投屏来源 | 螢幕鏡像來源 | 螢幕鏡像來源 | Mirroring sources | — | ✅ 完全一致 | 保留 |
| DeveloperDevicesDescription | 设备列表和连接面板 | 裝置清單和連線面板 | 裝置清單和連線面板 | Device list and connection panel | — | ✅ 完全一致 | 保留 |
| DeveloperDriver | 驱动管理 | 驅動程式管理 | 驅動程式管理 | Driver manager | — | ✅ 完全一致 | 保留 |
| DeveloperDriverDescription | 打开独立驱动管理器 | 開啟獨立驅動程式管理員 | 開啟獨立驅動程式管理員 | Open the standalone driver manager | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeveloperImageSettings | 图像设置 | 影像設定 | 影像設定 | Image settings | — | ✅ 完全一致 | 保留 |
| DeveloperImageSettingsDescription | 亮度、对比度和伽马滑块 | 亮度、對比度和伽馬滑桿 | 亮度、對比和伽馬滑桿 | Brightness, contrast, and gamma sliders | — | ✅ 完全一致 | 保留 |
| DeveloperInstanceConflict | 实例冲突窗口 | 執行個體衝突視窗 | 執行個體衝突視窗 | Instance conflict window | — | ✅ 完全一致 | 保留 |
| DeveloperInstanceConflictDescription | 只读的多实例提示 | 唯讀多執行個體提示 | 唯讀多執行個體提示 | Read-only multi-instance prompt | — | ✅ 完全一致 | 保留 |
| DeveloperLanguageEnglish | English | English | English | English | — | ✅ 完全一致 | 保留 |
| DeveloperLanguageSimplified | 简体中文 | 簡體中文 | 簡體中文 | Simplified Chinese | — | ✅ 完全一致 | 保留 |
| DeveloperLanguageSystem | 跟随系统 | 跟隨系統 | 跟隨系統 | System | — | ✅ 完全一致 | 保留 |
| DeveloperLanguageTaiwan | 繁體中文（台灣） | 繁體中文（台灣） | 繁體中文（台灣） | Traditional Chinese (Taiwan) | — | ✅ 完全一致 | 保留 |
| DeveloperLanguageTraditional | 繁体中文（香港） | 繁體中文（香港） | 繁體中文（香港） | Traditional Chinese (Hong Kong) | — | ✅ 完全一致 | 保留 |
| DeveloperMediaOutput | 媒体输出窗口 | 媒體輸出視窗 | 媒體輸出視窗 | Media output window | — | ✅ 完全一致 | 保留 |
| DeveloperMediaOutputDescription | 录制、直播和摄像头标签页 | 錄製、直播和攝影機分頁 | 錄製、直播和攝影機分頁 | Recording, live, and camera tabs | — | ✅ 完全一致 | 保留 |
| DeveloperMirroring | 投屏控制 | 螢幕鏡像控制 | 螢幕鏡像控制 | Mirroring controls | — | ✅ 完全一致 | 保留 |
| DeveloperMirroringDescription | 主工作区与预览状态 | 主工作區和預覽狀態 | 主工作區和預覽狀態 | Main workspace and preview state | — | ✅ 完全一致 | 保留 |
| DeveloperNativePreview | 独立预览窗口 | 獨立預覽視窗 | 獨立預覽視窗 | Independent preview window | — | ✅ 完全一致 | 保留 |
| DeveloperNativePreviewBody | 原生预览窗口 | 原生預覽視窗 | 原生預覽視窗 | Native preview window | — | ✅ 完全一致 | 保留 |
| DeveloperNativePreviewDescription | 原生窗口圆角和菜单外壳 | 原生視窗圓角和選單外殼 | 原生視窗圓角和選單外殼 | Native window corner and menu shell | — | ✅ 完全一致 | 保留 |
| DeveloperNativePreviewTitle | iPhoneMirror 独立预览 | iPhoneMirror 獨立預覽 | iPhoneMirror 獨立預覽 | iPhoneMirror independent preview | — | ✅ 完全一致 | 保留 |
| DeveloperOutput | 输出设置 | 輸出設定 | 輸出設定 | Output settings | — | ✅ 完全一致 | 保留 |
| DeveloperOutputDescription | 录制、推流和虚拟摄像头 | 錄製、串流和虛擬攝影機 | 錄製、串流和虛擬攝影機 | Recording, streaming, and virtual camera | — | ❌ 含义不一致 → ✅ 已修复 | 补齐虚拟摄像头含义 |
| DeveloperParamLanguage | 界面语言 | 介面語言 | 介面語言 | Interface language | — | ✅ 完全一致 | 保留 |
| DeveloperParamOpacity | 主窗口透明度 | 主視窗透明度 | 主視窗透明度 | Main window opacity | — | ✅ 完全一致 | 保留 |
| DeveloperParamTheme | 应用主题 | 應用程式主題 | 應用程式主題 | Application theme | — | ✅ 完全一致 | 保留 |
| DeveloperParamTopmost | 主窗口置顶 | 主視窗置頂 | 主視窗最上層顯示 | Keep main window on top | — | ✅ 完全一致 | 保留 |
| DeveloperParamWindowPreset | 窗口预设 | 視窗預設 | 視窗預設 | Window preset | — | ✅ 完全一致 | 保留 |
| DeveloperParametersReset | 参数已恢复默认值 | 參數已還原為預設值 | 參數已還原為預設值 | Parameters restored to defaults | — | ✅ 完全一致 | 保留 |
| DeveloperPresetCompact | 紧凑 1280×700 | 緊湊 1280×700 | 緊湊 1280×700 | Compact 1280×700 | — | ✅ 完全一致 | 保留 |
| DeveloperPresetDefault | 默认 1540×900 | 預設 1540×900 | 預設 1540×900 | Default 1540×900 | — | ✅ 完全一致 | 保留 |
| DeveloperPresetMaximize | 最大化 | 最大化 | 最大化 | Maximize | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewDeviceName | 预览设备 | 預覽裝置 | 預覽裝置 | Preview device | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewPromptBody | 这是标准确认提示框的安全预览。确认和取消按钮不会执行应用操作。 | 這是標準確認提示的安全預覽。按鈕不會執行應用程式操作。 | 這是標準確認提示的安全預覽。按鈕不會執行應用程式操作。 | This is a safe preview of the standard confirmation prompt. The buttons do not perform an app action. | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewPromptTitle | 开发者提示预览 | 開發者提示預覽 | 開發者提示預覽 | Developer prompt preview | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewReverseControlUnavailable | 开发者服务未就绪（开发者工具预览） | 開發者服務未就緒（開發者工具預覽） | 開發者服務未就緒（開發者工具預覽） | The developer service is not ready (developer-tools preview). | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewUpdateBody | 这是发布说明布局的本地预览。下载按钮已禁用，不会访问网络或启动安装程序。 | 這是發佈說明版面的本機預覽。下載按鈕已停用，不會連線或啟動安裝程式。 | 這是發布說明版面的本機預覽。下載按鈕已停用，不會連線或啟動安裝程式。 | This is a local preview of the release-notes layout. Downloading is disabled and no network or installer is started. | — | ✅ 完全一致 | 保留 |
| DeveloperPreviewUpdateTitle | 开发者更新预览 | 開發者更新預覽 | 開發者更新預覽 | Developer update preview | — | ✅ 完全一致 | 保留 |
| DeveloperProjectionSettings | 投屏设置窗口 | 螢幕鏡像設定視窗 | 螢幕鏡像設定視窗 | Mirroring settings window | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| DeveloperProjectionSettingsDescription | 设备状态和快捷操作 | 裝置狀態和快速操作 | 裝置狀態和快速操作 | Device status and quick actions | — | ✅ 完全一致 | 保留 |
| DeveloperPrompt | 确认提示框 | 確認提示 | 確認提示 | Confirmation prompt | — | ✅ 完全一致 | 保留 |
| DeveloperPromptDescription | 标准确认和信息布局 | 標準確認和資料版面 | 標準確認和資料版面 | Standard confirmation and info layout | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeveloperProtectedContent | 受保护视频提示 | 受保護影片提示 | 受保護影片提示 | Protected-video prompt | — | ✅ 完全一致 | 保留 |
| DeveloperProtectedContentDescription | DRM/FairPlay 黑屏检查提示 | DRM/FairPlay 黑畫面檢查提示 | DRM/FairPlay 黑畫面檢查提示 | DRM/FairPlay black-screen check | — | ✅ 完全一致 | 保留 |
| DeveloperReadOnlyPreview | 只读预览：不会执行真实输出操作。 | 唯讀預覽：不會執行真實輸出操作。 | 唯讀預覽：不會執行真實輸出操作。 | Read-only preview: no real output operation will run. | — | ✅ 完全一致 | 保留 |
| DeveloperRefresh | 刷新检查 | 重新整理檢查器 | 重新整理檢查器 | Refresh inspector | — | ✅ 完全一致 | 保留 |
| DeveloperResetParameters | 重置参数 | 重設參數 | 重設參數 | Reset parameters | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlError | 反向控制错误弹窗 | 反向控制錯誤對話方塊 | 反向控制錯誤對話方塊 | Reverse control error prompt | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlErrorDescription | 反向控制桥接失败时的错误信息 | 反向控制橋接失敗時的錯誤訊息 | 反向控制橋接失敗時的錯誤訊息 | Error shown when the reverse-control bridge fails | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlStatusWindow | 反向控制状态 | 反向控制狀態 | 反向控制狀態 | Reverse-control status | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlWiredPrerequisite | 有线控制前置提示 | 有線控制前置提示 | 有線控制前置提示 | Wired control prerequisites | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlWiredPrerequisiteDescription | 开发者模式、USB 信任和 DDI 说明 | 開發者模式、USB 信任和 DDI 說明 | 開發者模式、USB 信任和 DDI 說明 | Developer Mode, USB trust, and DDI guidance | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlWirelessPrerequisite | 无线控制前置提示 | 無線控制前置提示 | 無線控制前置提示 | Wireless control prerequisites | — | ✅ 完全一致 | 保留 |
| DeveloperReverseControlWirelessPrerequisiteDescription | 开发者模式、配对和局域网说明 | 開發者模式、配對和區域網絡說明 | 開發者模式、配對和區域網路說明 | Developer Mode, pairing, and local network guidance | — | ✅ 完全一致 | 保留 |
| DeveloperRuntimeCulture | 区域 | 地區 | 地區 | Culture | — | ✅ 完全一致 | 保留 |
| DeveloperRuntimeDpi | 文字缩放 | 文字縮放 | 文字縮放 | Text scale | — | ✅ 完全一致 | 保留 |
| DeveloperRuntimeTheme | 主题 | 主題 | 主題 | Theme | — | ✅ 完全一致 | 保留 |
| DeveloperRuntimeVersion | 版本 | 版本 | 版本 | Version | — | ✅ 完全一致 | 保留 |
| DeveloperRuntimeWindows | 窗口数 | 視窗數量 | 視窗數量 | Window count | — | ✅ 完全一致 | 保留 |
| DeveloperSessionClosed | 投屏已停止弹窗 | 螢幕鏡像已停止提示 | 螢幕鏡像已停止提示 | Mirroring stopped prompt | — | ✅ 完全一致 | 保留 |
| DeveloperSessionClosedDescription | 从控制中心停止镜像后的提示 | 從控制中心停止鏡像後顯示的提示 | 從控制中心停止鏡像後顯示的提示 | Shown after mirroring is stopped from Control Center | — | ✅ 完全一致 | 保留 |
| DeveloperSettings | 投屏设置 | 螢幕鏡像設定 | 螢幕鏡像設定 | Mirroring settings | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| DeveloperSettingsDescription | AirPlay、语言和主题 | AirPlay、語言和主題 | AirPlay、語言和主題 | AirPlay, language, and theme | — | ✅ 完全一致 | 保留 |
| DeveloperShortcutSettingsWindow | 快捷键设置窗口 | 快捷鍵設定視窗 | 快速鍵設定視窗 | Shortcut settings window | — | ✅ 完全一致 | 保留 |
| DeveloperStartupError | 启动错误窗口 | 啟動錯誤視窗 | 啟動錯誤視窗 | Startup error window | — | ✅ 完全一致 | 保留 |
| DeveloperStartupErrorBody | 这是启动错误窗口的本地预览，用于检查错误摘要、日志路径和展开详情。 | 這是啟動錯誤視窗的本機預覽，用於檢查錯誤摘要、記錄路徑和詳細資料。 | 這是啟動錯誤視窗的本機預覽，用於檢查錯誤摘要、記錄檔路徑和詳細資料。 | This is a local preview of the startup error window for checking the summary, log path, and details expander. | — | ✅ 完全一致 | 保留 |
| DeveloperStartupErrorDescription | 错误摘要和诊断日志 | 錯誤摘要和診斷記錄 | 錯誤摘要和診斷記錄檔 | Error summary and diagnostic log | — | ✅ 完全一致 | 保留 |
| DeveloperSurfaceOpenFailed | 打开预览失败 | 開啟預覽失敗 | 開啟預覽失敗 | Preview failed | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DeveloperSurfaceOpenFailedFormat | 打开预览失败：{0} | 開啟預覽失敗：{0} | 開啟預覽失敗：{0} | Could not open the preview: {0} | — | ✅ 完全一致 | 保留 |
| DeveloperSurfaceOpened | 预览窗口已打开 | 預覽視窗已開啟 | 預覽視窗已開啟 | Preview window opened | — | ✅ 完全一致 | 保留 |
| DeveloperThemeDark | 深色 | 深色 | 深色 | Dark | — | ✅ 完全一致 | 保留 |
| DeveloperThemeLight | 浅色 | 淺色 | 淺色 | Light | — | ✅ 完全一致 | 保留 |
| DeveloperThemeSystem | 跟随系统 | 跟隨系統 | 跟隨系統 | System | — | ✅ 完全一致 | 保留 |
| DeveloperToolsInspectorSection | 窗口检查器 | 視窗檢查器 | 視窗檢查器 | Window inspector | — | ✅ 完全一致 | 保留 |
| DeveloperToolsParametersHint | 参数仅影响当前进程，便于检查主题、缩放和窗口协调 | 只影響目前工作階段，用於檢查主題、縮放和視窗協調 | 只影響目前工作階段，用於檢查主題、縮放和視窗協調 | Session-only controls for theme, scale, and window coordination | — | ✅ 完全一致 | 保留 |
| DeveloperToolsParametersSection | 运行期参数 | 執行階段參數 | 執行階段參數 | Runtime parameters | — | ✅ 完全一致 | 保留 |
| DeveloperToolsPreviewBadge | 只读预览 | 唯讀預覽 | 唯讀預覽 | READ-ONLY PREVIEW | — | ✅ 完全一致 | 保留 |
| DeveloperToolsSubtitle | 统一预览所有页面、弹窗和运行期参数 | 預覽所有頁面、對話方塊和執行階段參數 | 預覽所有頁面、對話方塊和執行階段參數 | Preview every page, dialog, and runtime parameter | — | ✅ 完全一致 | 保留 |
| DeveloperToolsTitle | 开发者工具 | 開發者工具 | 開發者工具 | Developer tools | — | ✅ 完全一致 | 保留 |
| DeveloperToolsWindowsHint | 打开现有窗口的安全预览；网络、设备和破坏性操作已隔离 | 開啟安全預覽；網絡、裝置和破壞性操作已隔離 | 開啟安全預覽；網路、裝置和破壞性操作已隔離 | Open safe previews; network, device, and destructive paths are isolated | — | ✅ 完全一致 | 保留 |
| DeveloperToolsWindowsSection | 子窗口与弹窗 | 子視窗與對話方塊 | 子視窗與對話方塊 | Child windows and dialogs | — | ✅ 完全一致 | 保留 |
| DeveloperToolsWorkspaceHint | 快速切换主窗口中的所有工作区状态 | 快速切換主視窗的所有工作區狀態 | 快速切換主視窗的所有工作區狀態 | Jump through every workspace state in the main window | — | ✅ 完全一致 | 保留 |
| DeveloperToolsWorkspaceSection | 工作区页面 | 工作區頁面 | 工作區頁面 | Workspace pages | — | ✅ 完全一致 | 保留 |
| DeveloperUpdate | 更新窗口 | 更新視窗 | 更新視窗 | Update window | — | ✅ 完全一致 | 保留 |
| DeveloperUpdateDescription | 版本信息和发布说明 | 版本資料和發佈說明 | 版本資料和發布說明 | Version details and release notes | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeveloperUsbConfigError | USB 配置错误弹窗 | USB 設定錯誤提示 | USB 設定錯誤提示 | USB configuration prompt | — | ✅ 完全一致 | 保留 |
| DeveloperUsbConfigErrorDescription | USB 投屏配置失败后的提示 | USB 螢幕鏡像設定失敗後顯示的提示 | USB 螢幕鏡像設定失敗後顯示的提示 | Shown when USB mirroring setup fails | — | ✅ 完全一致 | 保留 |
| DeveloperUsbMode | USB 模式详情 | USB 模式詳情 | USB 模式詳情 | USB mode details | — | ✅ 完全一致 | 保留 |
| DeveloperUsbModeDescription | 模式说明和注意事项 | 模式說明和注意事項 | 模式說明和注意事項 | Mode explanation and notices | — | ✅ 完全一致 | 保留 |
| DeviceAlreadyMirroring | 该设备已在主预览中投屏 | 這部裝置已在主預覽中螢幕鏡像 | 這台裝置已在主預覽中進行螢幕鏡像 | This device is already mirrored in the main preview | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DeviceBindingAddProfileToolTip | 添加 AirPlay 设备档案 | 新增 AirPlay 裝置檔案 | 新增 AirPlay 裝置設定檔 | Add AirPlay device profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingAirPlay | AirPlay | AirPlay | AirPlay | AirPlay | — | ✅ 完全一致 | 保留 |
| DeviceBindingAirPlayAddTitle | 添加 AirPlay 设备档案 | 新增 AirPlay 裝置檔案 | 新增 AirPlay 裝置設定檔 | Add AirPlay device profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingAirPlayDescription | 请让手机与电脑处于同一局域网，在 iPhone/iPad 打开“控制中心”→“屏幕镜像”，选择 iPhoneMirror。镜像建立后会出现在下方列表；选择它并绑定到此档案。 | 請讓手機與電腦處於同一區域網絡，在 iPhone/iPad 開啟「控制中心」→「螢幕鏡像」，選擇 iPhoneMirror。鏡像建立後會出現在下方清單；選擇它並綁定到此檔案。 | 請讓手機與電腦連線至同一區域網路，在 iPhone/iPad 開啟「控制中心」→「螢幕鏡像輸出」，選擇 iPhoneMirror。連線後，裝置會出現在下方清單；請選取並綁定至此設定檔。 | Put the phone and computer on the same local network. On the iPhone/iPad, open Control Center → Screen Mirroring and choose iPhoneMirror. After mirroring starts, select it below and bind it to this profile. | — | ✅ 完全一致 | 保留 |
| DeviceBindingAirPlaySelectDescription | 选择当前正在镜像的 AirPlay 设备以创建档案。 | 選擇目前正在鏡像的 AirPlay 裝置以建立檔案。 | 選擇目前正在鏡像的 AirPlay 裝置以建立設定檔。 | Select the AirPlay device currently being mirrored to create a profile. | — | ✅ 完全一致 | 保留 |
| DeviceBindingAirPlaySelectTitle | 选择 AirPlay 设备 | 選擇 AirPlay 裝置 | 選擇 AirPlay 裝置 | Select AirPlay device | — | ✅ 完全一致 | 保留 |
| DeviceBindingBind | 绑定所选设备 | 綁定所選裝置 | 綁定所選裝置 | Bind selected device | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| DeviceBindingBluetooth | Bluetooth | Bluetooth | Bluetooth | Bluetooth | — | ✅ 完全一致 | 保留 |
| DeviceBindingBluetoothBroadcasting | 电脑正在广播 Bluetooth HID 设备 | 電腦正在廣播 Bluetooth HID 裝置 | 電腦正在廣播 Bluetooth HID 裝置 | The computer is broadcasting a Bluetooth HID device | — | ✅ 完全一致 | 保留 |
| DeviceBindingBluetoothDescription | 点击“连接设备”后，电脑会临时广播 Bluetooth HID 服务。请在 iPhone/iPad 的“设置”→“蓝牙”中选择此电脑完成配对，再点击等待窗口中的“下一步”选择 HID 设备。完成后只保存绑定，不会启动反向控制。 | 按一下「連接裝置」後，電腦會暫時廣播 Bluetooth HID 服務。請在 iPhone/iPad 的「設定」→「藍牙」中選擇此電腦完成配對，再按等待視窗中的「下一步」選擇 HID 裝置。完成後只會儲存綁定，不會啟動反向控制。 | 按一下「連線裝置」後，電腦會暫時廣播 Bluetooth HID 服務。請在 iPhone/iPad 的「設定」→「藍牙」中選擇此電腦完成配對，再按等待視窗中的「下一步」選擇 HID 裝置。完成後只會儲存綁定，不會啟動反向控制。 | Click “Connect device” to temporarily advertise a Bluetooth HID service. On the iPhone/iPad, open Settings → Bluetooth and pair with this computer. Click “Next” in the waiting window and select the HID device. Only the binding is saved; reverse control is not started. | — | ❌ 含义不一致、❌ 术语不一致 → ✅ 已修复 | 补齐英文遗漏的“下一步”操作；统一反向控制正式名称 |
| DeviceBindingBluetoothSaveFailed | 无法保存 Bluetooth 设备绑定。 | 無法儲存 Bluetooth 裝置綁定。 | 無法儲存 Bluetooth 裝置綁定。 | Could not save the Bluetooth device binding. | — | ✅ 完全一致 | 保留 |
| DeviceBindingBluetoothStartFailed | 无法启动 Bluetooth HID 连接。请确认电脑蓝牙已开启。 | 無法啟動 Bluetooth HID 連線。請確認電腦藍牙已開啟。 | 無法啟動 Bluetooth HID 連線。請確認電腦藍牙已開啟。 | Could not start the Bluetooth HID connection. Make sure Bluetooth is enabled on this computer. | — | ✅ 完全一致 | 保留 |
| DeviceBindingBluetoothWaitingDescription | 请在 iPhone/iPad 打开“设置”→“蓝牙”，在设备列表中选择此电脑并完成配对。配对成功后返回这里，点击“下一步”选择刚刚连接的 HID 设备。此流程只保存设备绑定，不会启动蓝牙反向控制。 | 請在 iPhone/iPad 開啟「設定」→「藍牙」，在裝置清單中選擇此電腦並完成配對。配對成功後返回這裡，按一下「下一步」選擇剛剛連接的 HID 裝置。此流程只會儲存裝置綁定，不會啟動藍牙反向控制。 | 請在 iPhone/iPad 開啟「設定」→「藍牙」，在裝置清單中選擇此電腦並完成配對。配對成功後返回這裡，按一下「下一步」選擇剛剛連線的 HID 裝置。此流程只會儲存裝置綁定，不會啟動藍牙反向控制。 | On the iPhone/iPad, open Settings → Bluetooth, select this computer, and complete pairing. Return here and click “Next” to choose the HID device you just connected. This process only saves the device binding; Bluetooth reverse control will not start. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称 |
| DeviceBindingBluetoothWaitingStatus | 等待设备连接 HID 服务 | 等待裝置連接 HID 服務 | 等待裝置連線 HID 服務 | Waiting for the device to connect to the HID service | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| DeviceBindingBluetoothWaitingTitle | 等待 Bluetooth HID 连接 | 等待 Bluetooth HID 連線 | 等待 Bluetooth HID 連線 | Waiting for Bluetooth HID connection | — | ✅ 完全一致 | 保留 |
| DeviceBindingBound | 未绑定 | 未綁定 | 未綁定 | Not bound | — | ❌ 含义不一致 → ✅ 已修复 | 调用条件为身份为空；英文 Bound 与实际未绑定状态相反 |
| DeviceBindingBoundConnected | 已绑定 · 已连接 | 已綁定 · 已連接 | 已綁定 · 已連線 | Bound · Connected | — | ✅ 完全一致 | 保留 |
| DeviceBindingBoundDisconnected | 已绑定 · 已断开 | 已綁定 · 已中斷連線 | 已綁定 · 已中斷連線 | Bound · Disconnected | — | ✅ 完全一致 | 保留 |
| DeviceBindingBoundMirroring | 已绑定 · 当前镜像 | 已綁定 · 目前鏡像 | 已綁定 · 目前鏡像 | Bound · Currently mirrored | — | ✅ 完全一致 | 保留 |
| DeviceBindingBoundUnavailable | 已绑定 · 当前不可用 | 已綁定 · 目前不可用 | 已綁定 · 目前不可用 | Bound · Currently unavailable | — | ✅ 完全一致 | 保留 |
| DeviceBindingCancel | 取消 | 取消 | 取消 | Cancel | — | ✅ 完全一致 | 保留 |
| DeviceBindingCompatibleNeedsConfirmation | 设备型号一致，需要用户确认。 | 裝置型號一致，需要用戶確認。 | 裝置型號一致，需要使用者確認。 | The device model matches, but user confirmation is required. | — | ✅ 完全一致 | 保留 |
| DeviceBindingConfirmBodyFormat | {0}<br><br>确认这是同一台真实设备吗？ | {0}<br><br>確認這是同一台真實裝置嗎？ | {0}<br><br>確認這是同一台真實裝置嗎？ | {0}<br><br>Are you sure this is the same physical device? | — | ✅ 完全一致 | 保留 |
| DeviceBindingConfirmTitle | 确认设备绑定 | 確認裝置綁定 | 確認裝置綁定 | Confirm device binding | — | ✅ 完全一致 | 保留 |
| DeviceBindingConnect | 连接设备 | 連接裝置 | 連線裝置 | Connect device | — | ✅ 完全一致 | 保留 |
| DeviceBindingCreateFailed | 无法创建设备档案。 | 無法建立裝置檔案。 | 無法建立裝置設定檔。 | Could not create the device profile. | — | ✅ 完全一致 | 保留 |
| DeviceBindingCreateProfile | 创建档案 | 建立檔案 | 建立設定檔 | Create profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingDeleteConfirmation | 删除该设备档案将解除所有已绑定的 USB、AirPlay 和 Bluetooth 身份。是否继续？ | 刪除此裝置檔案將解除所有已綁定的 USB、AirPlay 和 Bluetooth 身份。是否繼續？ | 刪除此裝置設定檔將解除所有已綁定的 USB、AirPlay 和 Bluetooth 身分。是否繼續？ | Deleting this device profile will remove all bound USB, AirPlay, and Bluetooth identities. Continue? | — | ✅ 完全一致 | 保留 |
| DeviceBindingDeleteProfile | 删除档案 | 刪除裝置檔案 | 刪除裝置設定檔 | Delete profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingDeleteTitle | 删除设备档案 | 刪除裝置檔案 | 刪除裝置設定檔 | Delete device profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingIdentityAlreadyBound | 该设备身份已经绑定到设备档案。 | 此裝置身份已綁定到裝置檔案。 | 此裝置身分已綁定到裝置設定檔。 | This device identity is already bound to a device profile. | — | ✅ 完全一致 | 保留 |
| DeviceBindingIdentityBoundElsewhere | 该设备身份已绑定到另一台设备档案。 | 此裝置身份已綁定到另一個裝置檔案。 | 此裝置身分已綁定到另一個裝置設定檔。 | This device identity is already bound to another device profile. | — | ✅ 完全一致 | 保留 |
| DeviceBindingIdentityRequired | 设备身份不能为空。 | 裝置身份不可為空。 | 裝置身分不可為空。 | A device identity is required. | — | ✅ 完全一致 | 保留 |
| DeviceBindingIncompatibleModel | 检测到的设备型号不一致，无法绑定。 | 偵測到的裝置型號不一致，無法綁定。 | 偵測到的裝置型號不一致，無法綁定。 | The detected device model does not match; it cannot be bound. | — | ✅ 完全一致 | 保留 |
| DeviceBindingModelUnknown | 暂无型号信息 | 暫無型號資料 | 暫無型號資料 | Model information unavailable | — | ⚠️ 需要优化 → ✅ 已修复 | 未知信息不应暗示正在读取 |
| DeviceBindingNext | 下一步 | 下一步 | 下一步 | Next | — | ✅ 完全一致 | 保留 |
| DeviceBindingOpenFailedFormat | 无法打开设备绑定器：{0} | 無法開啟裝置綁定器：{0} | 無法開啟裝置綁定：{0} | Could not open the device binder: {0} | — | ✅ 完全一致 | 保留 |
| DeviceBindingProfileDescription | 管理此设备档案的 USB、AirPlay 和蓝牙绑定。 | 管理此裝置檔案的 USB、AirPlay 和藍牙綁定。 | 管理此裝置設定檔的 USB、AirPlay 和藍牙綁定。 | Manage the USB, AirPlay, and Bluetooth bindings for this device profile. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| DeviceBindingProfileNotFound | 设备档案不存在。 | 找不到裝置檔案。 | 找不到裝置設定檔。 | The device profile was not found. | — | ✅ 完全一致 | 保留 |
| DeviceBindingProfiles | 我的设备档案 | 我的裝置檔案 | 我的裝置設定檔 | My device profiles | — | ✅ 完全一致 | 保留 |
| DeviceBindingRenameProfile | 重命名档案 | 重新命名裝置檔案 | 重新命名裝置設定檔 | Rename profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingRenamePrompt | 输入设备档案名称 | 輸入裝置檔案名稱 | 輸入裝置設定檔名稱 | Enter a device profile name | — | ✅ 完全一致 | 保留 |
| DeviceBindingRenameTitle | 重命名设备档案 | 重新命名裝置檔案 | 重新命名裝置設定檔 | Rename device profile | — | ❌ 含义不一致 → ✅ 已修复 | 修改的是应用内档案名，不是实体设备名称 |
| DeviceBindingSelectProfile | 选择设备档案 | 選擇裝置檔案 | 選擇裝置設定檔 | Select a device profile | — | ✅ 完全一致 | 保留 |
| DeviceBindingSubtitle | 在同一设备档案中关联 USB、AirPlay 和蓝牙连接。 | 在同一裝置檔案中關聯 USB、AirPlay 和藍牙連線。 | 在同一裝置設定檔中關聯 USB、AirPlay 和藍牙連線。 | Link USB, AirPlay, and Bluetooth connections in one device profile. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| DeviceBindingTitle | 设备绑定器 | 裝置綁定器 | 裝置綁定 | Device binder | — | ✅ 完全一致 | 保留 |
| DeviceBindingUnbind | 解绑 | 解除綁定 | 解除綁定 | Unbind | — | ✅ 完全一致 | 保留 |
| DeviceBindingUnknownNeedsConfirmation | 无法自动验证设备型号，需要用户确认。 | 無法自動驗證裝置型號，需要用戶確認。 | 無法自動驗證裝置型號，需要使用者確認。 | The device model could not be verified automatically; user confirmation is required. | — | ✅ 完全一致 | 保留 |
| DeviceBindingUnnamedDevice | 未命名设备 | 未命名裝置 | 未命名裝置 | Unnamed device | — | ✅ 完全一致 | 保留 |
| DeviceBindingUsbOrAirPlayRequired | 请先为该档案绑定 USB 或 AirPlay 设备。 | 請先為此檔案綁定 USB 或 AirPlay 裝置。 | 請先為此設定檔綁定 USB 或 AirPlay 裝置。 | Bind a USB or AirPlay device to this profile first. | — | ✅ 完全一致 | 保留 |
| DeviceBindingWired | 有线 USB | 有線 USB | 有線 USB | Wired USB | — | ✅ 完全一致 | 保留 |
| DeviceBindingWiredDescription | 使用数据线连接 iPhone/iPad，解锁设备并在提示时选择“信任”。已连接的 USB 设备会自动建立档案；如需为当前档案更换有线身份，可在下方选择后绑定。 | 使用數據線連接 iPhone/iPad，解鎖裝置並在提示時選擇「信任」。已連接的 USB 裝置會自動建立檔案；如需為目前檔案更換有線身份，可在下方選擇後綁定。 | 使用傳輸線連接 iPhone/iPad，解鎖裝置並在提示時選擇「信任」。已連線的 USB 裝置會自動建立設定檔；如需更換目前設定檔綁定的有線裝置，可在下方選取後綁定。 | Connect the iPhone/iPad with a cable, unlock it, and choose “Trust” when prompted. Connected USB devices create profiles automatically; select a device below to bind a different wired identity to this profile. | — | ✅ 完全一致 | 保留 |
| DeviceCaptureErrorTitleFormat | {0} 投屏错误 | {0} 螢幕鏡像錯誤 | {0} 螢幕鏡像錯誤 | Mirroring error — {0} | — | ✅ 完全一致 | 保留 |
| DeviceCountFormat | {0} 个来源 | {0} 個來源裝置 | {0} 個來源裝置 | Sources: {0} | — | ⚠️ 需要优化 → ✅ 已修复 | 使用数量标签，适用于单数与复数 |
| DeviceProfileGuidanceWiredBody | 检测到一台新的有线 iPhone/iPad。为了让有线投屏、无线投屏和反向控制始终对应到同一台真实设备，请现在建立设备档案。<br><br>请保持设备解锁并完成“信任此电脑”。点击“继续”将打开设备绑定器，在其中确认或管理此设备档案；点击“取消”也可以稍后从“反向控制”菜单打开。 | 偵測到新的有線 iPhone/iPad。為了讓有線螢幕鏡像、無線螢幕鏡像和反向控制始終對應同一台真實裝置，請現在建立裝置檔案。<br><br>請保持裝置解鎖並完成「信任此電腦」。點選「繼續」會開啟裝置綁定器，確認或管理此裝置檔案；點選「取消」亦可稍後從「反向控制」選單開啟。 | 偵測到新的有線 iPhone/iPad。為了讓有線螢幕鏡像、無線螢幕鏡像和反向控制始終對應同一台真實裝置，請現在建立裝置設定檔。<br><br>請保持裝置解鎖並完成「信任此電腦」。點選「繼續」會開啟裝置綁定，確認或管理此裝置設定檔；點選「取消」亦可稍後從「反向控制」選單開啟。 | A new wired iPhone/iPad was detected. Create a device profile now so wired mirroring, wireless mirroring, and reverse control always resolve to the same physical device.<br><br>Keep the device unlocked and complete the “Trust This Computer” prompt. Click Continue to open the device binder and confirm or manage this profile; click Cancel to open it later from the Reverse control menu. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeviceProfileGuidanceWiredTitle | 首次连接此有线设备 | 首次連接此有線裝置 | 首次連線此有線裝置 | First connection for this wired device | — | ✅ 完全一致 | 保留 |
| DeviceProfileGuidanceWirelessBody | 检测到一台新的 AirPlay 无线设备。为了让无线投屏能够匹配对应的有线设备并启用反向控制，请现在建立设备档案。<br><br>请确保手机和电脑处于同一局域网，并保持设备名称可识别。点击“继续”将打开设备绑定器，在其中选择并绑定此 AirPlay 设备；点击“取消”也可以稍后从“反向控制”菜单打开。 | 偵測到新的 AirPlay 無線裝置。為了讓無線螢幕鏡像能夠配對對應的有線裝置並啟用反向控制，請現在建立裝置檔案。<br><br>請確保手機和電腦處於同一區域網絡，並保持裝置名稱可識別。點選「繼續」會開啟裝置綁定器，選擇並綁定此 AirPlay 裝置；點選「取消」亦可稍後從「反向控制」選單開啟。 | 偵測到新的 AirPlay 無線裝置。為了讓無線螢幕鏡像能夠配對對應的有線裝置並啟用反向控制，請現在建立裝置設定檔。<br><br>請確保手機和電腦處於同一區域網路，並保持裝置名稱可識別。點選「繼續」會開啟裝置綁定，選擇並綁定此 AirPlay 裝置；點選「取消」亦可稍後從「反向控制」選單開啟。 | A new AirPlay device was detected. Create a device profile now so wireless mirroring can match its wired identity and reverse control can target the correct device.<br><br>Make sure the phone and computer are on the same local network and keep the device name recognizable. Click Continue to open the device binder and bind this AirPlay device; click Cancel to open it later from the Reverse control menu. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DeviceProfileGuidanceWirelessTitle | 首次连接此无线设备 | 首次連接此無線裝置 | 首次連線此無線裝置 | First connection for this wireless device | — | ✅ 完全一致 | 保留 |
| DeviceRefreshFailedFormat | 设备刷新失败：{0} | 裝置重新整理失敗：{0} | 裝置重新整理失敗：{0} | Could not refresh devices: {0} | — | ❌ 缺失 → ✅ 已修复 | 补齐面向用户的日志摘要；原始诊断详情保留 |
| DeviceSessionClosedWarningBody | 检测到您已经在控制中心点击了停止镜像，下一次该设备有线镜像需重启 iPhone/iPad | 偵測到您已在控制中心點擊停止螢幕鏡像，下次使用此裝置進行有線螢幕鏡像前需重新啟動 iPhone/iPad | 偵測到您已在控制中心按一下停止螢幕鏡像，下次使用此裝置進行有線螢幕鏡像前需重新啟動 iPhone/iPad | You stopped mirroring from Control Center. Restart the iPhone or iPad before using wired mirroring with this device again. | — | ✅ 完全一致 | 保留 |
| DeviceSessionClosedWarningTitleFormat | {0} 投屏已停止 | {0} 螢幕鏡像已停止 | {0} 螢幕鏡像已停止 | Mirroring stopped — {0} | — | ✅ 完全一致 | 保留 |
| DevicesTitle | 投屏来源 | 螢幕鏡像來源 | 螢幕鏡像來源 | Sources | — | ✅ 完全一致 | 保留 |
| DiagnosticsCleanedFormat | 清理完成：删除 {0} 个文件，释放 {1}；{2} 个正在使用或无权限的文件已跳过。 | 清理完成：刪除 {0} 個檔案，釋放 {1}；{2} 個正在使用或無權限的檔案已跳過。 | 清理完成：刪除 {0} 個檔案，釋放 {1}；{2} 個正在使用或無權限的檔案已跳過。 | Cleanup complete. Files removed: {0}; space freed: {1}; in-use or inaccessible files skipped: {2}. | — | ⚠️ 需要优化 → ✅ 已修复 | 用数量标签兼容 0、1 和多个文件 |
| DiagnosticsCleanupFailedFormat | 清理失败：{0} | 清理失敗：{0} | 清理失敗：{0} | Cleanup failed: {0} | — | ✅ 完全一致 | 保留 |
| DiagnosticsDescription | 应用错误、投屏核心和更新流程会写入本地日志，便于排查其他电脑上的问题。 | 應用程式錯誤、螢幕鏡像核心和更新流程會寫入本機記錄，方便排解其他電腦上的問題。 | 應用程式錯誤、螢幕鏡像核心和更新流程會寫入本機記錄檔，方便排解其他電腦上的問題。 | Application errors, capture-core events, and update workflows are written to local logs for troubleshooting failures on other PCs. | — | ✅ 完全一致 | 保留 |
| DiagnosticsLocation | 日志位置 | 記錄位置 | 記錄檔位置 | Log location | — | ✅ 完全一致 | 保留 |
| DiagnosticsRetentionSummary | 日志自动限制为单文件 8–16 MB，并清理超过 14 天或总量超过 64 MB 的旧文件。 | 每個記錄檔會自動限制為 8–16 MB，並清理超過 14 天或總大小超過 64 MB 的舊檔案。 | 每個記錄檔會自動限制為 8–16 MB，並清理超過 14 天或總大小超過 64 MB 的舊檔案。 | Logs are capped at 8–16 MB per file; files older than 14 days or beyond a 64 MB total are cleaned automatically. | — | ✅ 完全一致 | 保留 |
| DiagnosticsTitle | 诊断 | 診斷 | 診斷 | Diagnostics | — | ✅ 完全一致 | 保留 |
| DisableAdvancedMode | 关闭高级模式 | 關閉進階模式 | 關閉進階模式 | Disable advanced mode | — | ✅ 完全一致 | 保留 |
| DiscardRecording | 丢弃录制 | 丟棄錄製 | 丟棄錄製 | Discard recording | — | ✅ 完全一致 | 保留 |
| DiscardRecordingConfirmation | 确定要删除这段尚未保存的录制吗？此操作不可撤销。 | 確定要刪除此段尚未儲存的錄製嗎？此操作無法復原。 | 確定要刪除此段尚未儲存的錄製嗎？此操作無法復原。 | Delete this unsaved recording? This cannot be undone. | — | ✅ 完全一致 | 保留 |
| DisplayBoundsUnavailable | 无法获取当前显示器的显示区域。 | 無法取得目前顯示器的顯示範圍。 | 無法取得目前顯示器的顯示範圍。 | Could not determine the current display bounds. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DownloadProgressFormat | 正在下载 {0:F0}% | 正在下載 {0:F0}% | 正在下載 {0:F0}% | Downloading {0:F0}% | — | ✅ 完全一致 | 保留 |
| DownloadingUpdate | 正在下载更新 | 正在下載更新 | 正在下載更新 | Downloading update | — | ✅ 完全一致 | 保留 |
| DriverAppleReadyFormat | Apple 通道就绪 / libusb {0} | Apple 通道就緒 / libusb {0} | Apple 通道就緒 / libusb {0} | Apple channel ready / libusb {0} | — | ✅ 完全一致 | 保留 |
| DriverCaptureReady | USB 采集就绪 | USB 擷取就緒 | USB 擷取就緒 | USB capture ready | — | ✅ 完全一致 | 保留 |
| DriverDeviceFilterMissing | 当前设备需要外部采集驱动 | 目前裝置需要外部擷取驅動程式 | 目前裝置需要外部擷取驅動程式 | This device needs the external capture driver | — | ✅ 完全一致 | 保留 |
| DriverDeviceFilterProvisionalFormat | 过滤驱动已登记 / libusb0 {0}；开始投屏时将核验当前设备 | 過濾驅動程式已註冊 / libusb0 {0}；開始螢幕鏡像時將驗證目前裝置 | 篩選驅動程式已註冊 / libusb0 {0}；開始螢幕鏡像時將驗證目前裝置 | Filter registered / libusb0 {0}; capture will verify this device | — | ✅ 完全一致 | 保留 |
| DriverDeviceFilterReadyFormat | 当前设备已就绪 / libusb0 {0} | 目前裝置已就緒 / libusb0 {0} | 目前裝置已就緒 / libusb0 {0} | This device is ready / libusb0 {0} | — | ✅ 完全一致 | 保留 |
| DriverExternalRequired | 请先安装或修复外部采集驱动，然后重新连接这台 Apple 设备。 | 請先安裝或修復外部擷取驅動程式，然後重新連接這部 Apple 裝置。 | 請先安裝或修復外部擷取驅動程式，然後重新連線這台 Apple 裝置。 | Install or repair the external capture driver, then reconnect this Apple device. | — | ✅ 完全一致 | 保留 |
| DriverFilterStateError | 无法确认 Apple 设备采集驱动状态。 | 無法確認 Apple 裝置擷取驅動程式狀態。 | 無法確認 Apple 裝置擷取驅動程式狀態。 | The Apple device capture-driver state could not be verified. | — | ✅ 完全一致 | 保留 |
| DriverInvalidAppleStack | Apple USB 父设备驱动不兼容。 | Apple USB 父裝置驅動程式不相容。 | Apple USB 父裝置驅動程式不相容。 | The Apple USB parent driver is not compatible. | — | ✅ 完全一致 | 保留 |
| DriverLibUsbReadyFormat | libusb {0} / UsbDk 就绪 | libusb {0} / UsbDk 就緒 | libusb {0} / UsbDk 就緒 | libusb {0} / UsbDk ready | — | ✅ 完全一致 | 保留 |
| DriverManagerActivated | 已切换到正在运行的驱动管理器。 | 已切換至正在執行的驅動程式管理員。 | 已切換至正在執行的驅動程式管理員。 | Switched to the running driver manager. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverManagerButton | 驱动管理器 | 驅動程式管理員 | 驅動程式管理員 | Driver manager | — | ✅ 完全一致 | 保留 |
| DriverManagerExecutableMissing | 找不到驱动管理器。请重新安装完整的 iPhoneMirror 安装包。 | 找不到驅動程式管理員。請重新安裝完整的 iPhoneMirror 安裝套件。 | 找不到驅動程式管理員。請重新安裝完整的 iPhoneMirror 安裝套件。 | The driver manager could not be found. Reinstall the full iPhoneMirror package. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverManagerLaunchFailedFormat | 无法打开驱动管理器：{0} | 無法開啟驅動程式管理員：{0} | 無法開啟驅動程式管理員：{0} | Could not open the driver manager: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DriverManagerOpened | 已打开驱动管理器 | 已開啟驅動程式管理員 | 已開啟驅動程式管理員 | Driver manager opened | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DriverManagerOpenedAutomatically | 检测到有线设备驱动异常，已自动打开驱动管理器 | 偵測到有線裝置驅動程式異常，已自動開啟驅動程式管理員 | 偵測到有線裝置驅動程式異常，已自動開啟驅動程式管理員 | A wired-device driver problem was detected; the driver manager was opened | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| DriverManagerPathInvalid | 驱动管理器路径必须指向可执行文件（.exe）。 | 驅動程式管理員路徑必須指向可執行檔案（.exe）。 | 驅動程式管理員路徑必須指向可執行檔案（.exe）。 | The driver manager path must point to an executable (.exe). | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverManagerProcessStartFailed | Windows 未能启动驱动管理器。 | Windows 未能啟動驅動程式管理員。 | Windows 未能啟動驅動程式管理員。 | Windows could not start the driver manager. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverManagerStarted | 驱动管理器已启动。 | 驅動程式管理員已啟動。 | 驅動程式管理員已啟動。 | The driver manager has started. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverManagerTitle | 驱动管理器 | 驅動程式管理員 | 驅動程式管理員 | Driver manager | — | ✅ 完全一致 | 保留 |
| DriverManagerToolTip | 打开驱动安装、修复与卸载工具 | 開啟驅動程式安裝、修復與解除安裝工具 | 開啟驅動程式安裝、修復與解除安裝工具 | Open the driver installation, repair, and removal tool | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DriverNeedsApple | 需要 Apple USB 通道 | 需要 Apple USB 通道 | 需要 Apple USB 通道 | Apple USB channel required | — | ✅ 完全一致 | 保留 |
| DriverPreflightFormat | 驱动预检结果：{0} | 驅動程式預先檢查結果：{0} | 驅動程式預先檢查結果：{0} | Driver preflight result: {0} | — | ❌ 缺失 → ✅ 已修复 | 补齐面向用户的日志摘要；原始诊断详情保留 |
| DriverReconnectPhone | 请解锁并重新连接这台 Apple 设备，然后刷新设备。 | 請解鎖並重新連接這部 Apple 裝置，然後重新整理裝置。 | 請解鎖並重新連線這台 Apple 裝置，然後重新整理裝置。 | Unlock and reconnect this Apple device, then refresh devices. | — | ✅ 完全一致 | 保留 |
| DriverReplugRequired | 请重新连接这台 Apple 设备，以便外部采集驱动接管。 | 請重新連接這部 Apple 裝置，以便外部擷取驅動程式接管。 | 請重新連線這台 Apple 裝置，以便外部擷取驅動程式接管。 | Reconnect this Apple device so the external capture driver can attach. | — | ✅ 完全一致 | 保留 |
| DriverSafetyWarningFormat | 驱动安全警告：{0} | 驅動程式安全警告：{0} | 驅動程式安全警告：{0} | Driver safety warning: {0} | — | ❌ 缺失 → ✅ 已修复 | 补齐面向用户的日志摘要；原始诊断详情保留 |
| DriverUnsafeAppleStack | 驱动叠加：已启用保守 USB 模式 | 驅動程式疊加：已啟用保守 USB 模式 | 驅動程式疊加：已啟用保守 USB 模式 | Driver stack detected; conservative USB mode enabled | — | ✅ 完全一致 | 保留 |
| EndToEndLabel | 端到端 | 端對端 | 端對端 | End to end | — | ✅ 完全一致 | 保留 |
| EnumerateDevicesFailed | 枚举设备失败 | 列舉裝置失敗 | 列舉裝置失敗 | Could not enumerate devices | — | ✅ 完全一致 | 保留 |
| EnvironmentNeedsApple | 需要启动 Apple 设备服务并连接 iPhone 或 iPad。 | 需要啟動 Apple 裝置服務並連接 iPhone 或 iPad。 | 需要啟動 Apple 裝置服務並連線 iPhone 或 iPad。 | Start Apple device services and connect an iPhone or iPad. | — | ✅ 完全一致 | 保留 |
| EnvironmentReadyApple | Apple 设备通道已就绪，正在检查采集后端。 | Apple 裝置通道已就緒，正在檢查擷取後端。 | Apple 裝置通道已就緒，正在檢查擷取後端。 | The Apple device channel is ready; checking the capture backend. | — | ✅ 完全一致 | 保留 |
| EnvironmentReadyCapture | Apple 配对通道与 USB 采集后端已就绪。 | Apple 配對通道與 USB 擷取後端已就緒。 | Apple 配對通道與 USB 擷取後端已就緒。 | Apple pairing and direct USB capture are ready. | — | ✅ 完全一致 | 保留 |
| EnvironmentReadyUsbDk | Apple 配对通道与 UsbDk 后端已就绪。 | Apple 配對通道與 UsbDk 後端已就緒。 | Apple 配對通道與 UsbDk 後端已就緒。 | Apple pairing and the UsbDk backend are ready. | — | ✅ 完全一致 | 保留 |
| ErrorTechnicalDetails | 技术详情 | 技術詳情 | 技術詳情 | Technical details | — | ✅ 完全一致 | 保留 |
| FfmpegAudioInputTimeout | FFmpeg 未能在 5 秒内连接投屏音频输入。 | FFmpeg 未能在 5 秒內連接螢幕鏡像音訊輸入。 | FFmpeg 未能在 5 秒內連線螢幕鏡像音訊輸入。 | FFmpeg did not connect to the mirroring audio input within 5 seconds. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegAudioStartFailed | 无法启动 FFmpeg 音频解码器。 | 無法啟動 FFmpeg 音訊解碼器。 | 無法啟動 FFmpeg 音訊解碼器。 | The FFmpeg audio decoder could not start. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegEncoderSizeUnsupported | 已安装的 FFmpeg H.264 编码器均无法编码所选尺寸。 | 已安裝的 FFmpeg H.264 編碼器均無法編碼所選尺寸。 | 已安裝的 FFmpeg H.264 編碼器均無法編碼所選尺寸。 | No installed FFmpeg H.264 encoder supports the selected dimensions. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegEncoderUnavailable | 没有可用的兼容 FFmpeg H.264 编码器。 | 沒有可用的相容 FFmpeg H.264 編碼器。 | 沒有可用的相容 FFmpeg H.264 編碼器。 | A compatible FFmpeg H.264 encoder is unavailable. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegExitedFormat | FFmpeg 已退出，代码为 {0}。 | FFmpeg 已結束，代碼為 {0}。 | FFmpeg 已結束，代碼為 {0}。 | FFmpeg exited with code {0}. | — | ❌ 缺失 → ✅ 已修复 | 输出错误详情迁入三语资源 |
| FfmpegNoUsableEncoderFormat | {0} / 没有可用的 H.264 编码器 | {0} / 沒有可用的 H.264 編碼器 | {0} / 沒有可用的 H.264 編碼器 | {0} / no usable H.264 encoder | — | ❌ 缺失 → ✅ 已修复 | 用户可见错误详情补齐三语 |
| FfmpegNotFound | 未找到 FFmpeg。请安装 FFmpeg 8，或将其放入应用目录。 | 找不到 FFmpeg。請安裝 FFmpeg 8，或將其放入應用程式資料夾。 | 找不到 FFmpeg。請安裝 FFmpeg 8，或將其放入應用程式資料夾。 | FFmpeg was not found. Install FFmpeg 8 or place it in the application directory. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegProbeFailed | FFmpeg 输出能力检测失败。 | FFmpeg 輸出能力偵測失敗。 | FFmpeg 輸出能力偵測失敗。 | FFmpeg capability probing failed. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegProtocolUnavailable | FFmpeg 不支持所选输出协议。 | FFmpeg 不支援所選輸出協議。 | FFmpeg 不支援所選輸出協定。 | The selected FFmpeg output protocol is unavailable. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegStartFailed | 无法启动 FFmpeg。 | 無法啟動 FFmpeg。 | 無法啟動 FFmpeg。 | FFmpeg could not be started. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| FfmpegStartupExitedFormat | FFmpeg 在启动时退出，代码为 {0}。 | FFmpeg 在啟動時結束，代碼為 {0}。 | FFmpeg 在啟動時結束，代碼為 {0}。 | FFmpeg exited during startup with code {0}. | — | ❌ 缺失 → ✅ 已修复 | 输出错误详情迁入三语资源 |
| FooterConnectivity | 本机 USB / AirPlay，无云端中转 | 本機 USB / AirPlay，無需雲端中轉 | 本機 USB / AirPlay，無需雲端中轉 | Local USB / AirPlay; no cloud relay | — | ✅ 完全一致 | 保留 |
| FrameRateLabel | 帧率 | 幀率 | 影格率 | Frame rate | — | ✅ 完全一致 | 保留 |
| FullScreenFailedFormat | 切换全屏失败：{0} | 切換全螢幕失敗：{0} | 切換全螢幕失敗：{0} | Could not toggle full screen: {0} | — | ✅ 完全一致 | 保留 |
| FullScreenPreview | 全屏预览 | 全螢幕預覽 | 全螢幕預覽 | Full-screen preview | — | ✅ 完全一致 | 保留 |
| GammaLabel | 伽马 | 伽馬 | 伽馬 | Gamma | — | ✅ 完全一致 | 保留 |
| GeneralOperationFailed | 操作未能完成。请重试；如果问题持续，请查看诊断日志。 | 操作未能完成。請重試；如果問題持續，請查看診斷記錄。 | 操作未能完成。請重試；如果問題持續，請查看診斷記錄檔。 | The operation could not be completed. Retry, and check the diagnostic log if the problem persists. | — | ✅ 完全一致 | 保留 |
| HeightLabel | 高度 | 高度 | 高度 | Height | — | ✅ 完全一致 | 保留 |
| HlsRestartFailed | HLS 播放桥接组件重启失败。 | HLS 播放橋接元件重新啟動失敗。 | HLS 播放橋接元件重新啟動失敗。 | The HLS playback bridge could not restart. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| ImageAdjustmentsApplied | 画面调节已应用。 | 畫面調節已套用。 | 畫面調節已套用。 | Image adjustments applied. | — | ✅ 完全一致 | 保留 |
| ImageAdjustmentsBusy | 另一项投屏设置正在应用，请等待完成后重试。 | 另一項螢幕鏡像設定正在套用，請等待完成後重試。 | 另一項螢幕鏡像設定正在套用，請等待完成後重試。 | Another mirroring setting is being applied. Try again when it finishes. | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| ImageAdjustmentsPreviewOnlyNotice | 仅对本地预览生效，无法应用到推流、虚拟摄像头、录屏或截图画面。 | 僅對本機預覽生效，無法套用到串流、虛擬攝影機、畫面錄製或螢幕擷取畫面。 | 僅對本機預覽生效，無法套用到串流、虛擬攝影機、畫面錄製或螢幕截圖。 | Affects the local preview only. It cannot be applied to streams, the virtual camera, recordings, or screenshots. | — | ✅ 完全一致 | 保留 |
| ImageAdjustmentsSaved | 画面调节已保存，将在下次投屏时应用。 | 畫面調節已儲存，將在下次螢幕鏡像時套用。 | 畫面調節已儲存，將在下次螢幕鏡像時套用。 | Image adjustments saved for the next cast. | — | ✅ 完全一致 | 保留 |
| ImageAdjustmentsUpdateFailed | 无法应用画面调节。 | 無法套用畫面調節。 | 無法套用畫面調節。 | Could not apply image adjustments. | — | ✅ 完全一致 | 保留 |
| ImageAndSound | 画面与声音 | 畫面與音訊 | 畫面與音訊 | Video and audio | — | ✅ 完全一致 | 保留 |
| IndependentPreviewOpened | 已在独立窗口打开 | 已在獨立視窗開啟 | 已在獨立視窗開啟 | Opened in independent window | — | ✅ 完全一致 | 保留 |
| IndependentWindowAudioMenu | 声音 | 音訊 | 音訊 | Audio | — | ✅ 完全一致 | 保留 |
| IndependentWindowBluetoothControl | 蓝牙控制 | 藍牙控制 | 藍牙控制 | Bluetooth control | — | ✅ 完全一致 | 保留 |
| IndependentWindowClose | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| IndependentWindowDisplayMenu | 画面 | 畫面 | 畫面 | Image | — | ✅ 完全一致 | 保留 |
| IndependentWindowEnterFullScreen | 进入全屏 | 進入全螢幕 | 進入全螢幕 | Enter full screen | — | ✅ 完全一致 | 保留 |
| IndependentWindowExitFullScreen | 退出全屏 | 離開全螢幕 | 離開全螢幕 | Exit full screen | — | ✅ 完全一致 | 保留 |
| IndependentWindowFix | 固定窗口 | 固定視窗 | 固定視窗 | Lock window | — | ✅ 完全一致 | 保留 |
| IndependentWindowImageSettings | 调节画面 | 調節畫面 | 調節畫面 | Adjust image | — | ✅ 完全一致 | 保留 |
| IndependentWindowKeepCorners | 保留圆角 | 保留圓角 | 保留圓角 | Keep rounded corners | — | ✅ 完全一致 | 保留 |
| IndependentWindowMute | 静音 | 靜音 | 靜音 | Mute | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| IndependentWindowMuteOthers | 静音其他窗口 | 靜音其他視窗 | 靜音其他視窗 | Mute other windows | — | ✅ 完全一致 | 保留 |
| IndependentWindowMuteThis | 静音该窗口 | 靜音該視窗 | 靜音該視窗 | Mute this window | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpacity | 窗口透明度 | 視窗透明度 | 視窗透明度 | Window opacity | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpacityFailed | 暂时无法调整透明度，请重新打开独立窗口后重试。 | 暫時無法調整透明度，請重新開啟獨立視窗後再試。 | 暫時無法調整透明度，請重新開啟獨立視窗後再試。 | Opacity could not be changed. Reopen the independent window and try again. | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpacityHelp | 数值越低越透明，100% 为完全不透明。 | 數值越低越透明，100% 為完全不透明。 | 數值越低越透明，100% 為完全不透明。 | Lower values are more transparent. 100% is fully opaque. | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpacityReset | 恢复不透明 | 恢復不透明 | 恢復不透明 | Restore opacity | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpaqueOnHover | 鼠标悬停时不透明 | 滑鼠停留時不透明 | 滑鼠停留時不透明 | Opaque on hover | — | ✅ 完全一致 | 保留 |
| IndependentWindowOpaqueOnHoverHelp | 鼠标移入独立窗口时完全不透明，移开后恢复上方设置的透明度。 | 滑鼠移入獨立視窗時完全不透明，移開後恢復上方設定的透明度。 | 滑鼠移入獨立視窗時完全不透明，移開後恢復上方設定的透明度。 | The window becomes fully opaque while the pointer is over it, then returns to the opacity set above. | — | ✅ 完全一致 | 保留 |
| IndependentWindowOtherWindowsMuted | 其他窗口已静音 | 其他視窗已靜音 | 其他視窗已靜音 | Other windows muted | — | ✅ 完全一致 | 保留 |
| IndependentWindowPin | 置顶 | 置頂 | 最上層顯示 | Always on top | — | ✅ 完全一致 | 保留 |
| IndependentWindowProjectionSettings | 投屏设置 | 螢幕鏡像設定 | 螢幕鏡像設定 | Mirroring settings | — | ✅ 完全一致 | 保留 |
| IndependentWindowRemoveCorners | 去除圆角 | 去除圓角 | 去除圓角 | Remove rounded corners | — | ✅ 完全一致 | 保留 |
| IndependentWindowReverseControl | 反向控制 | 反向控制 | 反向控制 | Reverse control | — | ✅ 完全一致 | 保留 |
| IndependentWindowRotateLeft | 向左旋转 | 向左旋轉 | 向左旋轉 | Rotate left | — | ✅ 完全一致 | 保留 |
| IndependentWindowRotateRight | 向右旋转 | 向右旋轉 | 向右旋轉 | Rotate right | — | ✅ 完全一致 | 保留 |
| IndependentWindowStyle | 样式 | 樣式 | 樣式 | Style | — | ✅ 完全一致 | 保留 |
| IndependentWindowStyleSubtitle | 实时调整，仅应用于当前独立窗口。 | 即時調整，只套用至目前獨立視窗。 | 即時調整，只套用至目前獨立視窗。 | Changes apply immediately to this window only. | — | ✅ 完全一致 | 保留 |
| IndependentWindowStyleTitle | 独立窗口样式 | 獨立視窗樣式 | 獨立視窗樣式 | Independent window style | — | ✅ 完全一致 | 保留 |
| IndependentWindowUnfix | 取消固定 | 取消固定 | 取消固定 | Unlock window | — | ✅ 完全一致 | 保留 |
| IndependentWindowUnmuteThis | 取消静音该窗口 | 取消靜音該視窗 | 取消靜音該視窗 | Unmute this window | — | ✅ 完全一致 | 保留 |
| IndependentWindowUnpin | 取消置顶 | 取消置頂 | 取消最上層顯示 | Disable always on top | — | ✅ 完全一致 | 保留 |
| IndependentWindowWindowMenu | 窗口 | 視窗 | 視窗 | Window | — | ✅ 完全一致 | 保留 |
| IndependentWindowWiredProjection | 有线控制 | 有線控制 | 有線控制 | Wired control | — | ❌ 含义不一致 → ✅ 已修复 | 菜单和快捷键实际调用控制功能，不是开始投屏 |
| IndependentWindowWirelessProjection | 无线控制 | 無線控制 | 無線控制 | Wireless control | — | ❌ 含义不一致 → ✅ 已修复 | 菜单和快捷键实际调用控制功能，不是开始投屏 |
| InstallVirtualCamera | 安装虚拟摄像头组件 | 安裝虛擬攝影機元件 | 安裝虛擬攝影機元件 | Install virtual camera component | — | ✅ 完全一致 | 保留 |
| InstanceConflictBody | 检测到另一个 iPhoneMirror 程序正在运行。请选择要保留的窗口。 | 偵測到另一個 iPhoneMirror 程式正在執行。請選擇要保留的視窗。 | 偵測到另一個 iPhoneMirror 程式正在執行。請選擇要保留的視窗。 | Another iPhoneMirror instance is currently running. Choose which window to keep. | — | ✅ 完全一致 | 保留 |
| InstanceConflictHint | 关闭其他窗口后，当前窗口会继续启动；关闭当前窗口不会影响已经运行的投屏。 | 關閉其他視窗後，目前視窗會繼續啟動；關閉目前視窗不會影響進行中的螢幕鏡像。 | 關閉其他視窗後，目前視窗會繼續啟動；關閉目前視窗不會影響進行中的螢幕鏡像。 | Closing the other windows continues this launch. Closing this window leaves active mirroring sessions untouched. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| InstanceConflictTitle | iPhoneMirror 已在运行 | iPhoneMirror 正在執行 | iPhoneMirror 正在執行 | iPhoneMirror is already running | — | ✅ 完全一致 | 保留 |
| KeepUnlocked | 连接后请保持 Apple 设备解锁 | 連接後請保持 Apple 裝置解鎖 | 連線後請保持 Apple 裝置解鎖 | Keep the Apple device unlocked after connecting | — | ✅ 完全一致 | 保留 |
| LanguageChinese | 简体中文 | 簡體中文 | 簡體中文 | 简体中文 | — | ✅ 完全一致 | 保留 |
| LanguageEnglish | English | English | English | English | — | ✅ 完全一致 | 保留 |
| LanguageSystem | 跟随系统 | 跟隨系統 | 跟隨系統 | System default | — | ✅ 完全一致 | 保留 |
| LanguageToolTip | 界面语言 | 介面語言 | 介面語言 | Interface language | — | ✅ 完全一致 | 保留 |
| LanguageTraditionalChineseHongKong | 繁體中文（香港） | 繁體中文（香港） | 繁體中文（香港） | Traditional Chinese (Hong Kong) | — | ✅ 完全一致 | 保留 |
| LanguageTraditionalChineseTaiwan | 繁體中文（台灣） | 繁體中文（台灣） | 繁體中文（台灣） | Traditional Chinese (Taiwan) | — | ✅ 完全一致 | 保留 |
| LatencyLabel | 延迟 | 延遲 | 延遲 | Latency | — | ✅ 完全一致 | 保留 |
| LatestVersionLabel | 最新版本 | 最新版本 | 最新版本 | Latest version | — | ✅ 完全一致 | 保留 |
| LicenseInformation | 许可证信息 | 許可證資料 | 授權資訊 | License information | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| LiveLogHeader | 实时日志  ·  Ctrl+L | 即時記錄  ·  Ctrl+L | 即時記錄檔  ·  Ctrl+L | Live log  ·  Ctrl+L | — | ✅ 完全一致 | 保留 |
| LocalTargetMissing | 找不到要打开的本地文件或文件夹。 | 找不到要開啟的本機檔案或資料夾。 | 找不到要開啟的本機檔案或資料夾。 | The local file or folder could not be found. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| LogViewCleared | 日志视图已清空（不会删除磁盘日志） | 記錄檢視已清除（不會刪除磁碟上的記錄檔） | 記錄檢視已清除（不會刪除磁碟上的記錄檔） | Log view cleared (the log file was not deleted) | — | ✅ 完全一致 | 保留 |
| MappingAction | 触控操作 | 觸控操作 | 觸控操作 | Touch action | — | ✅ 完全一致 | 保留 |
| MappingActionDoubleTap | 双击 | 連按兩下 | 點兩下 | Double tap | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionLongPress | 长按 | 長按 | 長按 | Long press | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionSwipe | 自定义滑动 | 自訂滑動 | 自訂滑動 | Custom swipe | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionSwipeDown | 下滑 | 向下滑動 | 向下滑動 | Swipe down | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionSwipeLeft | 左滑 | 向左滑動 | 向左滑動 | Swipe left | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionSwipeRight | 右滑 | 向右滑動 | 向右滑動 | Swipe right | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionSwipeUp | 上滑 | 向上滑動 | 向上滑動 | Swipe up | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingActionTap | 点击 | 點按 | 點一下 | Tap | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingAdd | 添加映射 | 新增映射 | 新增映射 | Add mapping | — | ✅ 完全一致 | 保留 |
| MappingAutoSave | 列表更改自动保存；编辑中的映射需点击「保存」。 | 清單變更會自動儲存；編輯中的映射需按「儲存」。 | 清單變更會自動儲存；編輯中的映射需按「儲存」。 | List changes save automatically. Use Save to finish editing a mapping. | — | ✅ 完全一致 | 保留 |
| MappingBluetoothUnsupported | 当前蓝牙模式仅支持相对鼠标，无法可靠执行指定坐标。请切换到 USB 或无线触控反控后使用键盘映射。 | 目前藍牙模式僅支援相對滑鼠，無法可靠執行指定座標。請切換至 USB 或無線觸控反控後使用鍵盤映射。 | 目前藍牙模式僅支援相對滑鼠，無法可靠執行指定座標。請切換至 USB 或無線觸控反向控制後使用鍵盤映射。 | The current Bluetooth mode provides only a relative mouse, not reliable coordinate touch. Use USB or wireless touch control for keyboard mappings. | — | ✅ 完全一致 | 保留 |
| MappingCapture | 录入按键 | 錄入按鍵 | 擷取按鍵 | Record key | — | ✅ 完全一致 | 保留 |
| MappingCaptureHelp | 录入一个物理按键并松开。Ctrl / Shift / Alt / Win 单独松开时触发；组合键不触发单键映射。Win 单键映射会阻止开始菜单；系统安全快捷键仍由 Windows 处理。 | 錄入一個實體按鍵並放開。Ctrl / Shift / Alt / Win 單獨放開時觸發；組合鍵不觸發單鍵映射。Win 單鍵映射會阻止開始功能表；系統安全快捷鍵仍由 Windows 處理。 | 擷取一個實體按鍵並放開。Ctrl / Shift / Alt / Win 單獨放開時觸發；組合鍵不觸發單鍵映射。Win 單鍵映射會阻止開始功能表；系統安全快速鍵仍由 Windows 處理。 | Press and release one physical key. Ctrl / Shift / Alt / Win trigger on standalone release; chords do not trigger a single-key mapping. A mapped Win key suppresses Start. Windows retains control of secure system shortcuts. | — | ✅ 完全一致 | 保留 |
| MappingCaptureHint | 请按下要绑定的键盘按键 | 請按下要綁定的鍵盤按鍵 | 請按下要綁定的鍵盤按鍵 | Press the keyboard key to bind | — | ✅ 完全一致 | 保留 |
| MappingConnecting | 等待反向控制会话建立或切换完成… | 等候反向控制工作階段建立或切換完成… | 等候反向控制工作階段建立或切換完成… | Waiting for the control session to connect or finish switching… | — | ✅ 完全一致 | 保留 |
| MappingControlNotReady | 触控反控尚未就绪或已断开。请为当前设备启用 USB 或无线反控。 | 觸控反控尚未就緒或已中斷。請為目前裝置啟用 USB 或無線反控。 | 觸控反向控制尚未就緒或已中斷。請為目前裝置啟用 USB 或無線反向控制。 | Touch control is not ready or has disconnected. Enable USB or wireless control for the selected device. | — | ✅ 完全一致 | 保留 |
| MappingCoordinateHelp | 在当前投屏画面点击或拖动选择位置，无需填写 X/Y。滑动方向表示手指移动方向。 | 在目前投屏畫面點擊或拖曳選擇位置，毋須填寫 X/Y。滑動方向代表手指移動方向。 | 在目前螢幕鏡像畫面點選或拖曳選擇位置，無需填寫 X/Y。滑動方向代表手指移動方向。 | Click or drag directly on the current mirror to choose a position. No X/Y entry is needed. Swipe directions describe finger movement. | — | ✅ 完全一致 | 保留 |
| MappingDamagedConfig | 部分映射配置无效，已关闭键盘映射。请检查保留的映射后重新启用。 | 部分映射設定無效，已關閉鍵盤映射。請檢查保留的映射後重新啟用。 | 部分映射設定無效，已關閉鍵盤映射。請檢查保留的映射後重新啟用。 | Some mappings were invalid. Mapping is off; review the retained mappings before enabling it again. | — | ✅ 完全一致 | 保留 |
| MappingDelete | 删除 | 刪除 | 刪除 | Delete | — | ✅ 完全一致 | 保留 |
| MappingDeleteConfirm | 删除按键「{0}」的映射？ | 刪除按鍵「{0}」的映射？ | 刪除按鍵「{0}」的映射？ | Delete the mapping for {0}? | — | ✅ 完全一致 | 保留 |
| MappingDescription | 将 Windows 键盘按键绑定到 iPhone/iPad 的触控操作。 | 將 Windows 鍵盤按鍵綁定至 iPhone/iPad 的觸控操作。 | 將 Windows 鍵盤按鍵綁定至 iPhone/iPad 的觸控操作。 | Bind Windows keyboard keys to iPhone/iPad touch actions. | — | ✅ 完全一致 | 保留 |
| MappingDistance | 滑动距离（所在轴的 %） | 滑動距離（所在軸的 %） | 滑動距離（所在軸的 %） | Distance (% of the movement axis) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingDoubleSummary | X: {0:0.##}%, Y: {1:0.##}% · 间隔 {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · 間隔 {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · 間隔 {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · {2} ms interval | — | ✅ 完全一致 | 保留 |
| MappingDuplicate | 此按键已经存在映射。请选择替换、取消或编辑原映射。 | 此按鍵已有映射。請選擇取代、取消或編輯原映射。 | 此按鍵已有映射。請選擇取代、取消或編輯原映射。 | This key already has a mapping. Replace it, cancel, or edit the original. | — | ✅ 完全一致 | 保留 |
| MappingDuration | 持续时间（毫秒，50–10000） | 持續時間（毫秒，50–10000） | 持續時間（毫秒，50–10000） | Duration (ms, 50–10000) | — | ✅ 完全一致 | 保留 |
| MappingEdit | 编辑 | 編輯 | 編輯 | Edit | — | ✅ 完全一致 | 保留 |
| MappingEditOriginal | 编辑原映射 | 編輯原映射 | 編輯原映射 | Edit original | — | ✅ 完全一致 | 保留 |
| MappingEditorTitle | 编辑键盘映射 | 編輯鍵盤映射 | 編輯鍵盤映射 | Edit keyboard mapping | — | ✅ 完全一致 | 保留 |
| MappingEmpty | 尚无映射。点击「添加映射」，选择按键和触控操作。 | 尚無映射。按「新增映射」，選擇按鍵及觸控操作。 | 尚無映射。按「新增映射」，選擇按鍵及觸控操作。 | No mappings yet. Add a mapping to choose a key and touch action. | — | ✅ 完全一致 | 保留 |
| MappingEnable | 启用键盘映射 | 啟用鍵盤映射 | 啟用鍵盤映射 | Enable keyboard mapping | — | ✅ 完全一致 | 保留 |
| MappingEnabled | 已启用 | 已啟用 | 已啟用 | Enabled | — | ✅ 完全一致 | 保留 |
| MappingEndX | 终点 X（%） | 終點 X（%） | 終點 X（%） | End X (%) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingEndY | 终点 Y（%） | 終點 Y（%） | 終點 Y（%） | End Y (%) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingGeometryUnavailable | 等待设备画面尺寸与方向。 | 等候裝置畫面尺寸及方向。 | 等候裝置畫面尺寸及方向。 | Waiting for the device's screen size and orientation. | — | ✅ 完全一致 | 保留 |
| MappingGlobalHelp | 启用后可在其他应用前台时生效；在设置、编辑和文字输入时暂停。每次按下执行一次，按住不连发。组合键保留原行为，修饰键不会被拦截。 | 啟用後可在其他應用程式處於前景時生效；在設定、編輯及文字輸入時暫停。每次按下執行一次，按住不連發。組合鍵保留原有行為，修飾鍵不會被攔截。 | 啟用後可在其他應用程式處於前景時生效；在設定、編輯及文字輸入時暫停。每次按下執行一次，按住不連發。組合鍵保留原有行為，修飾鍵不會被攔截。 | Works while other apps are foreground. Pauses in settings, editors and text fields. Holding a key does not repeat actions. Key combinations and modifier keys keep their Windows behavior. | — | ✅ 完全一致 | 保留 |
| MappingHoldSummary | X: {0:0.##}%, Y: {1:0.##}% · {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · {2} 毫秒 | X: {0:0.##}%, Y: {1:0.##}% · {2} ms | — | ✅ 完全一致 | 保留 |
| MappingHookFailed | 无法启动键盘监听。请关闭并重新启用键盘映射。 | 無法啟動鍵盤監聽。請關閉並重新啟用鍵盤映射。 | 無法啟動鍵盤監聽。請關閉並重新啟用鍵盤映射。 | Could not start keyboard capture. Turn mapping off and on again. | — | ✅ 完全一致 | 保留 |
| MappingInterval | 双击间隔（毫秒，40–1000） | 連按間隔（毫秒，40–1000） | 點兩下間隔（毫秒，40–1000） | Double-tap interval (ms, 40–1000) | — | ✅ 完全一致 | 保留 |
| MappingInvalidCoordinates | 坐标必须是 0–100 之间的数值。 | 座標必須是 0–100 之間的數值。 | 座標必須是 0–100 之間的數值。 | Coordinates must be numbers between 0 and 100. | — | ✅ 完全一致 | 保留 |
| MappingInvalidDistance | 滑动距离必须大于 0 且不超过 100%。 | 滑動距離必須大於 0 且不超過 100%。 | 滑動距離必須大於 0 且不超過 100%。 | Swipe distance must be greater than 0 and no more than 100%. | — | ✅ 完全一致 | 保留 |
| MappingInvalidDuration | 持续时间必须是 50–10000 毫秒之间的整数。 | 持續時間必須是 50–10000 毫秒之間的整數。 | 持續時間必須是 50–10000 毫秒之間的整數。 | Duration must be a whole number from 50 to 10000 ms. | — | ✅ 完全一致 | 保留 |
| MappingInvalidInterval | 双击间隔必须是 40–1000 毫秒之间的整数。 | 連按間隔必須是 40–1000 毫秒之間的整數。 | 點兩下間隔必須是 40–1000 毫秒之間的整數。 | Double-tap interval must be a whole number from 40 to 1000 ms. | — | ✅ 完全一致 | 保留 |
| MappingInvalidSwipe | 滑动终点必须位于画面内，且不能与起点相同。请调整起点、终点或距离。 | 滑動終點必須位於畫面內，且不能與起點相同。請調整起點、終點或距離。 | 滑動終點必須位於畫面內，且不能與起點相同。請調整起點、終點或距離。 | The swipe endpoint must be inside the screen and differ from its start. Adjust the points or distance. | — | ✅ 完全一致 | 保留 |
| MappingKey | Windows 按键 | Windows 按鍵 | Windows 按鍵 | Windows key | — | ✅ 完全一致 | 保留 |
| MappingLimit | 最多支持 256 条映射。 | 最多支援 256 項映射。 | 最多支援 256 項映射。 | Up to 256 mappings are supported. | — | ✅ 完全一致 | 保留 |
| MappingManage | 管理键盘映射 | 管理鍵盤映射 | 管理鍵盤映射 | Manage keyboard mappings | — | ✅ 完全一致 | 保留 |
| MappingNoDevice | 等待设备：请选择并连接 iPhone/iPad。 | 等候裝置：請選取並連接 iPhone/iPad。 | 等候裝置：請選取並連線 iPhone/iPad。 | Waiting for a device: select and connect an iPhone/iPad. | — | ✅ 完全一致 | 保留 |
| MappingOff | 键盘映射已关闭 | 鍵盤映射已關閉 | 鍵盤映射已關閉 | Keyboard mapping is off | — | ✅ 完全一致 | 保留 |
| MappingPaused | 键盘映射已暂停。点击当前投屏画面后恢复；编辑或文字输入期间不执行映射。 | 鍵盤映射已暫停。點擊目前投屏畫面後恢復；編輯或文字輸入期間不執行映射。 | 鍵盤映射已暫停。點選目前螢幕鏡像畫面後恢復；編輯或文字輸入期間不執行映射。 | Mapping is paused. Click the current mirror to resume; editing and text input pause execution. | — | ✅ 完全一致 | 保留 |
| MappingPick | 选择位置 | 選擇位置 | 選擇位置 | Choose position | — | ✅ 完全一致 | 保留 |
| MappingPickClick | 点击目标位置。Esc 取消。 | 點擊目標位置。Esc 取消。 | 點選目標位置。Esc 取消。 | Click the target. Esc cancels. | — | ✅ 完全一致 | 保留 |
| MappingPickDrag | 按下并拖动到终点，松开完成。Esc 取消。 | 按下並拖曳到終點，放開完成。Esc 取消。 | 按下並拖曳到終點，放開完成。Esc 取消。 | Press, drag to the endpoint, then release. Esc cancels. | — | ✅ 完全一致 | 保留 |
| MappingPickInside | 将鼠标移入视频内容区域 | 將滑鼠移入影片內容區域 | 將滑鼠移入影片內容區域 | Move the pointer into the video content | — | ✅ 完全一致 | 保留 |
| MappingPickNoPreview | 请先开始当前设备的投屏，再选择位置。 | 請先開始目前裝置的投屏，再選擇位置。 | 請先開始目前裝置的螢幕鏡像，再選擇位置。 | Start mirroring the current device before choosing a position. | — | ✅ 完全一致 | 保留 |
| MappingPickRequired | 请在当前投屏画面选择位置。 | 請在目前投屏畫面選擇位置。 | 請在目前螢幕鏡像畫面選擇位置。 | Choose a position on the current mirror. | — | ✅ 完全一致 | 保留 |
| MappingPicking | 正在投屏画面取点 | 正在投屏畫面取點 | 正在螢幕鏡像畫面取點 | Choosing a position on the mirror | — | ✅ 完全一致 | 保留 |
| MappingPointSummary | X: {0:0.##}%, Y: {1:0.##}% | X: {0:0.##}%, Y: {1:0.##}% | X: {0:0.##}%, Y: {1:0.##}% | X: {0:0.##}%, Y: {1:0.##}% | — | ✅ 完全一致 | 保留 |
| MappingReady | 键盘映射已启用 · 目标为当前选中的设备 | 鍵盤映射已啟用 · 目標為目前選取的裝置 | 鍵盤映射已啟用 · 目標為目前選取的裝置 | Keyboard mapping is ready · targets the selected device | — | ✅ 完全一致 | 保留 |
| MappingRepick | 重新取点 | 重新取點 | 重新取點 | Pick again | — | ✅ 完全一致 | 保留 |
| MappingReplace | 替换 | 取代 | 取代 | Replace | — | ✅ 完全一致 | 保留 |
| MappingSaveFailed | 保存键盘映射失败，更改未应用。请检查配置目录权限后重试。 | 儲存鍵盤映射失敗，變更未套用。請檢查設定目錄權限後重試。 | 儲存鍵盤映射失敗，變更未套用。請檢查設定目錄權限後重試。 | Could not save mappings. Changes were not applied. Check access to the settings folder and retry. | — | ✅ 完全一致 | 保留 |
| MappingSendFailed | 触控发送失败。请检查当前设备的反向控制连接。 | 觸控傳送失敗。請檢查目前裝置的反向控制連線。 | 觸控傳送失敗。請檢查目前裝置的反向控制連線。 | Touch input could not be sent. Check the selected device's control connection. | — | ✅ 完全一致 | 保留 |
| MappingShortcutConflict | 此按键与 iPhoneMirror 快捷键冲突。请更换按键或先调整快捷键设置。F5、F11 保留给刷新和全屏。 | 此按鍵與 iPhoneMirror 快捷鍵衝突。請更換按鍵或先調整快捷鍵設定。F5、F11 保留予重新整理及全螢幕。 | 此按鍵與 iPhoneMirror 快速鍵衝突。請更換按鍵或先調整快速鍵設定。F5、F11 保留供重新整理及全螢幕。 | This key conflicts with an iPhoneMirror shortcut. Choose another key or change the shortcut first. F5 and F11 are reserved for refresh and full screen. | — | ✅ 完全一致 | 保留 |
| MappingStateIdle | 第 1 步：录入 Windows 按键 | 第 1 步：錄入 Windows 按鍵 | 第 1 步：擷取 Windows 按鍵 | Step 1: capture a Windows key | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingStateKeyCaptured | 第 2 步：选择操作，然后选择位置 | 第 2 步：選擇操作，然後選擇位置 | 第 2 步：選擇操作，然後選擇位置 | Step 2: choose an action, then a position | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingStateMappingReady | 按键和位置已确认，可以保存 | 按鍵和位置已確認，可以儲存 | 按鍵和位置已確認，可以儲存 | Key and position confirmed. Ready to save. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingStatePickingPosition | 正在当前投屏画面选择位置 | 正在目前投屏畫面選擇位置 | 正在目前螢幕鏡像畫面選擇位置 | Choosing a position on the current mirror | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingStateWaitingForKey | 请按下一个按键并松开；再次点击可取消录入 | 請按下一個按鍵並放開；再次點擊可取消錄入 | 請按下一個按鍵並放開；再次點選可取消擷取 | Press and release a key; click again to cancel capture | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingSuppress | 阻止已映射普通按键的原始 Windows 行为 | 阻止已映射一般按鍵的原有 Windows 行為 | 阻止已映射一般按鍵的原有 Windows 行為 | Block the original Windows action of mapped ordinary keys | — | ✅ 完全一致 | 保留 |
| MappingSwipeSummary | ({0:0.##}%, {1:0.##}%) → ({2:0.##}%, {3:0.##}%) · {4} 毫秒 | ({0:0.##}%, {1:0.##}%) → ({2:0.##}%, {3:0.##}%) · {4} 毫秒 | ({0:0.##}%, {1:0.##}%) → ({2:0.##}%, {3:0.##}%) · {4} 毫秒 | ({0:0.##}%, {1:0.##}%) → ({2:0.##}%, {3:0.##}%) · {4} ms | — | ✅ 完全一致 | 保留 |
| MappingTargetStatus | 目标：{0} · {1} | 目標：{0} · {1} | 目標：{0} · {1} | Target: {0} · {1} | — | ✅ 完全一致 | 保留 |
| MappingTitle | 键盘映射 | 鍵盤映射 | 鍵盤映射 | Keyboard mapping | — | ✅ 完全一致 | 保留 |
| MappingUnsupportedAction | 不支持此触控操作，请重新选择。 | 不支援此觸控操作，請重新選擇。 | 不支援此觸控操作，請重新選擇。 | Unsupported touch action. Choose another action. | — | ✅ 完全一致 | 保留 |
| MappingUnsupportedDevice | 当前设备不支持触控反控。请选择 iPhone/iPad 投屏设备。 | 目前裝置不支援觸控反控。請選取 iPhone/iPad 投屏裝置。 | 目前裝置不支援觸控反向控制。請選取 iPhone/iPad 螢幕鏡像裝置。 | This device does not support touch control. Select an iPhone/iPad mirror. | — | ✅ 完全一致 | 保留 |
| MappingUnsupportedKey | 不支持此按键。请使用物理键盘录入其他按键。 | 不支援此按鍵。請使用實體鍵盤錄入其他按鍵。 | 不支援此按鍵。請使用實體鍵盤擷取其他按鍵。 | Unsupported key. Record another key using a physical keyboard. | — | ✅ 完全一致 | 保留 |
| MappingWaiting | 等待按键…（再次点击取消） | 等候按鍵…（再次按下以取消） | 等候按鍵…（再次按下以取消） | Waiting for a key… (click again to cancel) | — | ✅ 完全一致 | 保留 |
| MappingWindowsKeyUnsupported | Win 键及 Windows 系统组合键不支持绑定，原始系统行为会保留。 | Win 鍵及 Windows 系統組合鍵不支援綁定，原有系統行為會保留。 | Win 鍵及 Windows 系統組合鍵不支援綁定，原有系統行為會保留。 | Windows keys and Windows system combinations cannot be bound. Their system behavior is preserved. | — | ✅ 完全一致 | 保留 |
| MappingX | X / 起点 X（%） | X / 起點 X（%） | X / 起點 X（%） | X / start X (%) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| MappingY | Y / 起点 Y（%） | Y / 起點 Y（%） | Y / 起點 Y（%） | Y / start Y (%) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| Maximize | 最大化 | 最大化 | 最大化 | Maximize | — | ✅ 完全一致 | 保留 |
| MeasuringUpdateRoutes | 正在测试可达线路的下载速度… | 正在測試可達線路的下載速度… | 正在測試可達線路的下載速度… | Measuring reachable download routes… | — | ✅ 完全一致 | 保留 |
| MediaCastAudioActive | 媒体音频 | 媒體音訊 | 媒體音訊 | media audio | — | ✅ 完全一致 | 保留 |
| MediaCastAudioMuted | 已静音 | 已靜音 | 已靜音 | muted | — | ✅ 完全一致 | 保留 |
| MediaCastDescription | 接收视频 App 内“投屏”按钮发送的影片，不是控制中心的屏幕镜像。 | 接收影片 App 內「投放」按鈕發送的影片，不是控制中心的螢幕鏡像。 | 接收影片 App 內「投放」按鈕傳送的影片，不是控制中心的螢幕鏡像。 | Receives movies sent from a video app's Cast button. This is separate from Control Center Screen Mirroring. | — | ✅ 完全一致 | 保留 |
| MediaCastDeviceActive | 正在投放视频 | 正在投放影片 | 正在投放影片 | Video is being cast | — | ✅ 完全一致 | 保留 |
| MediaCastDeviceConnection | 网络媒体投放 | 網絡媒體投放 | 網路媒體投放 | Network media cast | — | ✅ 完全一致 | 保留 |
| MediaCastDeviceModel | AirPlay / DLNA 视频 | AirPlay / DLNA 影片投放 | AirPlay / DLNA 影片投放 | AirPlay / DLNA video | — | ✅ 完全一致 | 保留 |
| MediaCastDeviceName | 视频应用投屏 | 影片 App 投放 | 影片 App 投放 | Video app casting | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaCastFpsCapabilityFormat | 接收上限 {0} fps | 接收上限 {0} fps | 接收上限 {0} fps | Receiver limit {0} fps | — | ✅ 完全一致 | 保留 |
| MediaCastFpsDisplayFormat | ≤ {0} fps | ≤ {0} fps | ≤ {0} fps | ≤ {0} fps | — | ✅ 完全一致 | 保留 |
| MediaCastHlsBackendUnavailable | HLS 播放组件不可用，请检查安装包是否包含 FFmpeg。 | HLS 播放元件不可用，請檢查安裝包是否包含 FFmpeg。 | HLS 播放元件不可用，請檢查安裝套件是否包含 FFmpeg。 | The HLS playback component is unavailable. Check that FFmpeg is included in the installation. | — | ✅ 完全一致 | 保留 |
| MediaCastInvalidUrl | 收到的视频地址无效或不是 HTTP(S) 地址。 | 收到的影片網址無效或不是 HTTP(S) 網址。 | 收到的影片網址無效或不是 HTTP(S) 網址。 | The received video URL is invalid or is not an HTTP(S) URL. | — | ✅ 完全一致 | 保留 |
| MediaCastLive | 直播 | 直播 | 直播 | Live | — | ✅ 完全一致 | 保留 |
| MediaCastLiveRecoveringFormat | 直播流暂时中断，正在重新连接：{0} | 直播串流暫時中斷，正在重新連接：{0} | 直播串流暫時中斷，正在重新連線：{0} | The live stream was interrupted and is reconnecting: {0} | — | ✅ 完全一致 | 保留 |
| MediaCastLoadingVideo | 正在加载投放的视频… | 正在載入投放的影片… | 正在載入投放的影片… | Loading the cast video… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| MediaCastMute | 静音 | 靜音 | 靜音 | Mute | — | ✅ 完全一致 | 保留 |
| MediaCastNetworkStream | 网络流 | 網絡串流 | 網路串流 | network stream | — | ✅ 完全一致 | 保留 |
| MediaCastOriginalResolution | 媒体原始分辨率 | 媒體原始解像度 | 媒體原始解析度 | Original media resolution | — | ✅ 完全一致 | 保留 |
| MediaCastPause | 暂停 | 暫停 | 暫停 | Pause | — | ✅ 完全一致 | 保留 |
| MediaCastPlay | 播放 | 播放 | 播放 | Play | — | ✅ 完全一致 | 保留 |
| MediaCastPlayReceived | 已收到视频应用投屏请求 | 已收到影片 App 投放請求 | 已收到影片 App 投放請求 | Received a video app casting request | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaCastPlaybackEnded | 视频播放完毕 | 影片播放完畢 | 影片播放完畢 | Video playback ended | — | ✅ 完全一致 | 保留 |
| MediaCastPlaybackFailedFormat | 视频投放播放失败：{0} | 影片投放播放失敗：{0} | 影片投放播放失敗：{0} | Video casting playback failed: {0} | — | ✅ 完全一致 | 保留 |
| MediaCastReady | 视频投放接收端已就绪 | 影片投放接收端已就緒 | 影片投放接收端已就緒 | Video casting receiver ready | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaCastReceiverMissing | 缺少视频投放组件。请重新安装完整的 iPhoneMirror 安装包。 | 缺少影片投放元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | 缺少影片投放元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | The video casting component is missing. Reinstall the full iPhoneMirror package. | — | ⚠️ 需要优化 → ✅ 已修复 | 面向最终用户提供可执行的重新安装建议 |
| MediaCastRequiresOriginalBackend | 视频应用投屏仅支持“原始方案”无线接收端。 | 影片 App 投放僅支援「原始方案」無線接收端。 | 影片 App 投放僅支援「原始方案」無線接收端。 | Video app casting is available only with the Original wireless receiver implementation. | — | ✅ 完全一致 | 保留 |
| MediaCastSeek | 播放进度 | 播放進度 | 播放進度 | Playback position | — | ✅ 完全一致 | 保留 |
| MediaCastSeekBackward | 后退 10 秒 | 後退 10 秒 | 後退 10 秒 | Back 10 seconds | — | ✅ 完全一致 | 保留 |
| MediaCastSeekForward | 前进 10 秒 | 前進 10 秒 | 前進 10 秒 | Forward 10 seconds | — | ✅ 完全一致 | 保留 |
| MediaCastSpeedUnsupportedBody | 当前投放的视频不支持 {0} 播放速度，已恢复为 1x。 | 目前投放的影片不支援 {0} 播放速度，已恢復為 1x。 | 目前投放的影片不支援 {0} 播放速度，已恢復為 1x。 | This cast stream does not support {0} playback. Playback was restored to 1x. | — | ⚠️ 需要优化 → ✅ 已修复 | 参数已包含 x，避免显示 2x 倍速 |
| MediaCastSpeedUnsupportedTitle | 当前流不支持倍速 | 目前串流不支援倍速 | 目前串流不支援倍速 | Playback speed unavailable | — | ✅ 完全一致 | 保留 |
| MediaCastStarting | 正在启动视频投放接收端… | 正在啟動影片投放接收端… | 正在啟動影片投放接收端… | Starting the video casting receiver… | — | ❌ 含义不一致、⚠️ 需要优化 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay；统一进行中状态的省略号 |
| MediaCastStopRequestFailed | 接收端未接受停止投屏请求。 | 接收端未接受停止投放請求。 | 接收端未接受停止投放請求。 | The receiver did not accept the stop request. | — | ✅ 完全一致 | 保留 |
| MediaCastStopRequestFailedFormat | 无法通知投屏发送端停止：{0} | 無法通知影片投放發送端停止：{0} | 無法通知影片投放傳送端停止：{0} | Could not notify the casting sender to stop: {0} | — | ✅ 完全一致 | 保留 |
| MediaCastStopped | 视频应用投屏已停止 | 影片 App 投放已停止 | 影片 App 投放已停止 | Video app casting stopped | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaCastSystemDecoder | 系统媒体解码 | 系統媒體解碼 | 系統媒體解碼 | system media decoder | — | ✅ 完全一致 | 保留 |
| MediaCastTitle | 视频应用投屏 | 影片 App 投放 | 影片 App 投放 | Video app casting | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaCastUnmute | 取消静音 | 取消靜音 | 取消靜音 | Unmute | — | ✅ 完全一致 | 保留 |
| MediaCastWaitingVideo | 等待视频 App 投放内容… | 等待影片 App 投放內容… | 等待影片 App 投放內容… | Waiting for a video app to cast content… | — | ❌ 含义不一致、⚠️ 需要优化 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay；统一进行中状态的省略号 |
| MediaCastWindowTitle | iPhoneMirror 视频应用投屏 | iPhoneMirror 影片 App 投放 | iPhoneMirror 影片 App 投放 | iPhoneMirror Video App Casting | — | ❌ 含义不一致 → ✅ 已修复 | 功能也支持 DLNA，不能仅标为 AirPlay |
| MediaOutputAlreadyRunning | 请先停止当前媒体输出任务。 | 請先停止目前媒體輸出任務。 | 請先停止目前媒體輸出任務。 | Stop the current media output before starting another one. | — | ✅ 完全一致 | 保留 |
| MediaOutputAudioInvalid | 投屏音频数据的 PCM 布局无效。 | 螢幕鏡像音訊資料的 PCM 排列無效。 | 螢幕鏡像音訊資料的 PCM 排列無效。 | The mirroring audio packet has an invalid PCM layout. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputAudioStalled | 投屏音频序列未更新。 | 螢幕鏡像音訊序列未更新。 | 螢幕鏡像音訊序列未更新。 | The mirroring audio sequence did not advance. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputAudioUnsupported | 投屏音频数据使用了不支持的 PCM 格式。 | 螢幕鏡像音訊資料使用了不支援的 PCM 格式。 | 螢幕鏡像音訊資料使用了不支援的 PCM 格式。 | The mirroring audio packet has an unsupported PCM format. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputButton | 录制与推流 | 錄製與串流 | 錄製與串流 | Recording and streaming | — | ✅ 完全一致 | 保留 |
| MediaOutputCapabilitiesFormat | 可用：{0}、{1}、{2}；编码器：{3}。 | 可用：{0}、{1}、{2}；編碼器：{3}。 | 可用：{0}、{1}、{2}；編碼器：{3}。 | Available: {0}, {1}, {2}; encoder: {3}. | — | ✅ 完全一致 | 保留 |
| MediaOutputCapabilitiesUnknown | 尚未检测输出能力。 | 尚未偵測輸出能力。 | 尚未偵測輸出能力。 | Output capabilities have not been checked. | — | ✅ 完全一致 | 保留 |
| MediaOutputChecking | 正在检测 FFmpeg 输出能力… | 正在偵測 FFmpeg 輸出能力… | 正在偵測 FFmpeg 輸出能力… | Checking FFmpeg output capabilities… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| MediaOutputDestinationRequired | 请指定输出位置或服务器地址。 | 請指定輸出位置或伺服器位址。 | 請指定輸出位置或伺服器位址。 | Specify an output location or server address. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputFailedFormat | 媒体输出失败：{0} | 媒體輸出失敗：{0} | 媒體輸出失敗：{0} | Media output failed: {0} | — | ✅ 完全一致 | 保留 |
| MediaOutputFrameInvalid | 原生输出画面布局无效。 | 原生輸出畫面排列無效。 | 原生輸出畫面排列無效。 | The native output frame has an invalid layout. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputFrameStalled | 已连续 5 秒无法获取投屏画面。 | 已連續 5 秒無法取得螢幕鏡像畫面。 | 已連續 5 秒無法取得螢幕鏡像畫面。 | No mirroring frame has been available for 5 seconds. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputFrameTimeout | 已连续 5 秒未收到投屏画面。 | 已連續 5 秒未收到螢幕鏡像畫面。 | 已連續 5 秒未收到螢幕鏡像畫面。 | No mirroring frame was received for 5 seconds. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputIdle | 当前没有媒体输出任务。 | 目前沒有媒體輸出任務。 | 目前沒有媒體輸出任務。 | No media output is active. | — | ✅ 完全一致 | 保留 |
| MediaOutputInvalidDimensions | 输出宽高必须为偶数，宽度为 160–3840，高度为 160–2160。 | 輸出寬高必須為偶數，寬度為 160–3840，高度為 160–2160。 | 輸出寬高必須為偶數，寬度為 160–3840，高度為 160–2160。 | Output dimensions must be even: width 160–3840 and height 160–2160. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputInvalidProtocol | 请选择受支持的推流协议。 | 請選擇受支援的串流協議。 | 請選擇受支援的串流協定。 | Select a supported streaming protocol. | — | ✅ 完全一致 | 保留 |
| MediaOutputInvalidSettings | 请输入偶数输出尺寸、10-60 fps 帧率以及 500-50000 kbps 码率。 | 請輸入偶數輸出尺寸、10-60 fps 幀率以及 500-50000 kbps 位元率。 | 請輸入偶數輸出尺寸、10-60 fps 影格率以及 500-50000 kbps 位元率。 | Enter an even output size, 10-60 fps, and a bitrate from 500 to 50000 kbps. | — | ✅ 完全一致 | 保留 |
| MediaOutputNoSession | 请先开始设备投屏，再启动媒体输出。 | 請先開始裝置螢幕鏡像，再啟動媒體輸出。 | 請先開始裝置螢幕鏡像，再啟動媒體輸出。 | Start mirroring a device before starting media output. | — | ✅ 完全一致 | 保留 |
| MediaOutputRecording | 正在录制 | 正在錄製 | 正在錄製 | Recording | — | ✅ 完全一致 | 保留 |
| MediaOutputRecordingFormat | 已开始录制：{0} | 已開始錄製：{0} | 已開始錄製：{0} | Recording started: {0} | — | ✅ 完全一致 | 保留 |
| MediaOutputRtmpAddressRequired | 请输入 rtmp:// 或 rtmps:// 地址。 | 請輸入 rtmp:// 或 rtmps:// 位址。 | 請輸入 rtmp:// 或 rtmps:// 位址。 | Enter an rtmp:// or rtmps:// address. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputSrtAddressRequired | 请输入 srt:// 地址。 | 請輸入 srt:// 位址。 | 請輸入 srt:// 位址。 | Enter an srt:// address. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MediaOutputStartFailedFormat | 无法启动媒体输出：{0} | 無法啟動媒體輸出：{0} | 無法啟動媒體輸出：{0} | Could not start media output: {0} | — | ✅ 完全一致 | 保留 |
| MediaOutputStopped | 媒体输出已停止。 | 媒體輸出已停止。 | 媒體輸出已停止。 | Media output stopped. | — | ✅ 完全一致 | 保留 |
| MediaOutputStopping | 正在停止输出并完成文件或推流收尾… | 正在停止輸出並完成檔案或串流收尾… | 正在停止輸出並完成檔案或串流收尾… | Stopping output and finalizing the file or stream… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| MediaOutputStreaming | 正在直播推流 | 正在直播串流 | 正在直播串流 | Live streaming | — | ✅ 完全一致 | 保留 |
| MediaOutputStreamingFormat | 已开始直播推流：{0} | 已開始直播串流：{0} | 已開始直播串流：{0} | Live stream started: {0} | — | ✅ 完全一致 | 保留 |
| MediaOutputTitle | 输出设置 | 輸出設定 | 輸出設定 | Output settings | — | ✅ 完全一致 | 保留 |
| MediaOutputUnavailableFormat | 媒体输出不可用：{0} | 媒體輸出不可用：{0} | 媒體輸出不可用：{0} | Media output is unavailable: {0} | — | ✅ 完全一致 | 保留 |
| MediaOutputWhipAddressRequired | 请输入 HTTP(S) WHIP 端点地址。 | 請輸入 HTTP(S) WHIP 端點位址。 | 請輸入 HTTP(S) WHIP 端點位址。 | Enter an HTTP(S) WHIP endpoint. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| MicrophoneAudioRequired | 此输出缺少音频编码器，无法加入麦克风。 | 此輸出缺少音訊編碼器，無法加入麥克風。 | 此輸出缺少音訊編碼器，無法加入麥克風。 | This output needs an audio encoder to include a microphone. | — | ✅ 完全一致 | 保留 |
| MicrophoneEnumerationFailed | 无法读取麦克风列表：{0} | 無法讀取麥克風清單：{0} | 無法讀取麥克風清單：{0} | Could not read microphones: {0} | — | ✅ 完全一致 | 保留 |
| MicrophoneNotFound | 未找到麦克风，请检查 Windows 麦克风访问权限。 | 找不到麥克風，請檢查 Windows 麥克風存取權限。 | 找不到麥克風，請檢查 Windows 麥克風存取權限。 | No microphone found. Check Windows microphone access settings. | — | ✅ 完全一致 | 保留 |
| MicrophoneOff | 不使用麦克风 | 不使用麥克風 | 不使用麥克風 | No microphone | — | ✅ 完全一致 | 保留 |
| Minimize | 最小化 | 最小化 | 最小化 | Minimize | — | ✅ 完全一致 | 保留 |
| MirrorSimultaneously | 同时投屏 | 同時螢幕鏡像 | 同時鏡像多台裝置 | Mirror simultaneously | — | ✅ 完全一致 | 保留 |
| ModelLoading | 型号读取中 | 型號讀取中 | 型號讀取中 | Reading model | — | ✅ 完全一致 | 保留 |
| MoreImageSettingsButton | 更多设置 | 更多設定 | 更多設定 | More settings | — | ✅ 完全一致 | 保留 |
| MoreImageSettingsSubtitle | 为当前设备微调本地预览画面。 | 為現有裝置微調本機預覽畫面。 | 為現有裝置微調本機預覽畫面。 | Fine-tune the local preview for the selected device. | — | ✅ 完全一致 | 保留 |
| MoreImageSettingsTitle | 画面调节 | 畫面調節 | 畫面調節 | Image adjustments | — | ✅ 完全一致 | 保留 |
| MultiDeviceControlAlreadyActive | 此设备已在另一个投屏会话中启用反控，请使用已有窗口。 | 此裝置已在另一個投影工作階段啟用反控，請使用現有視窗。 | 此裝置已在另一個螢幕鏡像工作階段啟用反向控制，請使用現有視窗。 | This device is already controlled through another mirroring session. Use its existing window. | — | ✅ 完全一致 | 保留 |
| MultiDeviceControlHint | 分别为各设备启用反控。切换选项卡或点击独立投屏窗口即可控制对应设备，连接会保持。 | 分別為各裝置啟用反控。切換分頁或點擊獨立投影視窗即可控制對應裝置，連線會保持。 | 分別為各裝置啟用反向控制。切換分頁或按一下獨立螢幕鏡像視窗即可控制對應裝置，連線會保持。 | Enable control for each device, then switch tabs or focus its separate mirror window. Other device connections stay active. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| NativeCoreInitFailed | 原生核心初始化失败 | 原生核心初始化失敗 | 原生核心初始化失敗 | Native core initialization failed | — | ✅ 完全一致 | 保留 |
| NavAbout | 关于 | 關於 | 關於 | About | — | ✅ 完全一致 | 保留 |
| NavAboutDescription | 关于与更新 | 關於與更新 | 關於與更新 | About and updates | — | ✅ 完全一致 | 保留 |
| NavCollapseDescription | 收起导航 | 收起導覽 | 收起導覽 | Collapse navigation | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| NavDeviceBinding | 设备绑定器 | 裝置綁定器 | 裝置綁定 | Device binder | — | ✅ 完全一致 | 保留 |
| NavDeviceBindingDescription | 管理真实设备的 USB、AirPlay 和 Bluetooth 身份绑定 | 管理真實裝置的 USB、AirPlay 和 Bluetooth 身份綁定 | 管理真實裝置的 USB、AirPlay 和 Bluetooth 身分綁定 | Manage USB, AirPlay, and Bluetooth identities for real devices | — | ✅ 完全一致 | 保留 |
| NavDevices | 投屏来源 | 螢幕鏡像來源 | 螢幕鏡像來源 | Sources | — | ✅ 完全一致 | 保留 |
| NavDevicesDescription | 选择投屏来源 | 選擇螢幕鏡像來源 | 選擇螢幕鏡像來源 | Choose a mirroring source | — | ✅ 完全一致 | 保留 |
| NavDriver | 驱动 | 驅動程式 | 驅動程式 | Drivers | — | ✅ 完全一致 | 保留 |
| NavDriverDescription | 驱动管理 | 驅動程式管理 | 驅動程式管理 | Driver manager | — | ✅ 完全一致 | 保留 |
| NavExpandDescription | 展开导航 | 展開導覽 | 展開導覽 | Expand navigation | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| NavExpandToolTip | 展开左侧导航 | 展開左側導覽 | 展開左側導覽 | Expand left navigation | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| NavMirroring | 投屏 | 螢幕鏡像 | 螢幕鏡像 | Mirroring | — | ✅ 完全一致 | 保留 |
| NavMirroringDescription | 投屏控制 | 螢幕鏡像控制 | 螢幕鏡像控制 | Mirroring controls | — | ✅ 完全一致 | 保留 |
| NavOutput | 输出 | 輸出 | 輸出 | Output | — | ✅ 完全一致 | 保留 |
| NavOutputDescription | 输出设置 | 輸出設定 | 輸出設定 | Output settings | — | ✅ 完全一致 | 保留 |
| NavSettings | 设置 | 設定 | 設定 | Settings | — | ✅ 完全一致 | 保留 |
| NavSettingsDescription | 投屏设置 | 螢幕鏡像設定 | 螢幕鏡像設定 | Mirroring settings | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| NoDeviceSelected | 未选择设备 | 未選擇裝置 | 未選擇裝置 | No device selected | — | ✅ 完全一致 | 保留 |
| NotifyPrereleaseReleases | 提醒 Beta 预览版更新 | 提醒 Beta 預覽版更新 | 提醒 Beta 預覽版更新 | Notify me about Beta preview releases | — | ✅ 完全一致 | 保留 |
| NotifyStableReleases | 提醒正式版更新 | 提醒正式版更新 | 提醒正式版更新 | Notify me about stable releases | — | ✅ 完全一致 | 保留 |
| ObsTitleCopyFailedFormat | OBS 专用窗口已打开；复制标题失败：{0} | OBS 專用視窗已開啟；複製標題失敗：{0} | OBS 專用視窗已開啟；複製標題失敗：{0} | OBS preview window opened; could not copy its title: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ObsWindowHelp | 独立窗口可直接用于 OBS“窗口采集”。 | 獨立視窗可直接用於 OBS「視窗擷取」。 | 獨立視窗可直接用於 OBS「視窗擷取」。 | Use the detached window directly with OBS Window Capture. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ObsWindowOpenFailedFormat | 打开 OBS 专用窗口失败：{0} | 開啟 OBS 專用視窗失敗：{0} | 開啟 OBS 專用視窗失敗：{0} | Could not open the OBS preview window: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ObsWindowOpened | OBS 专用窗口已打开；窗口标题已复制 | OBS 專用視窗已開啟；視窗標題已複製 | OBS 專用視窗已開啟；視窗標題已複製 | OBS preview window opened; title copied | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| OpenLinkFailedTitle | 无法打开 | 無法開啟 | 無法開啟 | Could not open | — | ✅ 完全一致 | 保留 |
| OpenLogs | 打开日志目录 | 開啟記錄檔位置 | 開啟記錄檔位置 | Open log folder | — | ✅ 完全一致 | 保留 |
| OpenObsWindow | 打开 OBS 专用窗口 | 開啟 OBS 專用視窗 | 開啟 OBS 專用視窗 | Open OBS preview window | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| OpenReleasePage | 查看完整 Release | 查看完整 Release | 查看完整 Release | View full release | — | ✅ 完全一致 | 保留 |
| OpenShortcutSettings | 设置快捷键 | 設定快捷鍵 | 設定快速鍵 | Configure shortcuts | — | ✅ 完全一致 | 保留 |
| OutputBitrate | 码率（kbps） | 位元率（kbps） | 位元率（kbps） | Bitrate (kbps) | — | ✅ 完全一致 | 保留 |
| OutputFps | 帧率 | 幀率 | 影格率 | Frame rate | — | ✅ 完全一致 | 保留 |
| OutputHeight | 高度 | 高度 | 高度 | Height | — | ✅ 完全一致 | 保留 |
| OutputWidth | 宽度 | 寬度 | 寬度 | Width | — | ✅ 完全一致 | 保留 |
| PendingSettingsFormat | 待应用：{0} · {1} fps | 待套用：{0} · {1} fps | 待套用：{0} · {1} fps | Pending: {0} · {1} fps | — | ✅ 完全一致 | 保留 |
| PendingSettingsLocalFormat | 待应用：{0} · {1} fps（仅本地渲染） | 待套用：{0} · {1} fps（僅本機繪製） | 待套用：{0} · {1} fps（僅本機繪製） | Pending: {0} · {1} fps (local rendering only) | — | ✅ 完全一致 | 保留 |
| PendingVideoSettingsFormat | 待应用：{0} · {1} fps · {2} | 待套用：{0} · {1} fps · {2} | 待套用：{0} · {1} fps · {2} | Pending: {0} · {1} fps · {2} | — | ✅ 完全一致 | 保留 |
| PlayAudioWindows | 在 Windows 播放声音 | 在 Windows 播放音訊 | 在 Windows 播放音訊 | Play audio on Windows | — | ✅ 完全一致 | 保留 |
| PlaybackSpeed | 播放倍速 | 播放倍速 | 播放倍速 | Playback speed | — | ✅ 完全一致 | 保留 |
| PlaybackVolume | 播放音量 | 播放音量 | 播放音量 | Playback volume | — | ✅ 完全一致 | 保留 |
| PreparingDownload | 正在准备安全下载… | 正在準備安全下載… | 正在準備安全下載… | Preparing a secure download… | — | ✅ 完全一致 | 保留 |
| PreviewAndObs | 预览与 OBS | 預覽與 OBS | 預覽與 OBS | Preview and OBS | — | ✅ 完全一致 | 保留 |
| PreviewChildCreateFailed | 无法创建 D3D11 预览子窗口 | 無法建立 D3D11 預覽子視窗 | 無法建立 D3D11 預覽子視窗 | Could not create the D3D11 preview child window | — | ✅ 完全一致 | 保留 |
| PreviewLabel | 预览 | 預覽 | 預覽 | Preview | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| PreviewRefreshFailed | 预览刷新失败：窗口尚未就绪 | 預覽重新整理失敗：視窗尚未就緒 | 預覽重新整理失敗：視窗尚未就緒 | Preview refresh failed: the window is not ready | — | ✅ 完全一致 | 保留 |
| PreviewRefreshed | 预览渲染器已刷新 | 預覽繪製器已重新整理 | 預覽繪製器已重新整理 | Preview renderer refreshed | — | ✅ 完全一致 | 保留 |
| PreviewRendererAttachFailed | 无法连接 D3D11 预览渲染器 | 無法連接 D3D11 預覽渲染器 | 無法連線 D3D11 預覽渲染器 | Could not attach the D3D11 preview renderer | — | ✅ 完全一致 | 保留 |
| PreviewWindowOpenFailedFormat | 打开独立预览失败：{0} | 開啟獨立預覽失敗：{0} | 開啟獨立預覽失敗：{0} | Could not open the preview window: {0} | — | ✅ 完全一致 | 保留 |
| PreviewWindowOpened | 已打开独立预览窗口 | 已開啟獨立預覽視窗 | 已開啟獨立預覽視窗 | Preview window opened | — | ✅ 完全一致 | 保留 |
| ProjectionActions | 投屏操作 | 螢幕鏡像操作 | 螢幕鏡像操作 | Mirroring | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ProjectionSettingsDescription | 按需展开常用投屏操作；开始投屏后可使用画面、窗口和截图功能。 | 按需要展開常用螢幕鏡像操作；開始螢幕鏡像後可使用畫面、視窗和螢幕擷取功能。 | 按需要展開常用螢幕鏡像操作；開始螢幕鏡像後可使用畫面、視窗和螢幕擷取功能。 | Open common mirroring actions when needed. Image, window, and capture tools become available after mirroring starts. | — | ✅ 完全一致 | 保留 |
| ProjectionSettingsTitle | 投屏设置 | 螢幕鏡像設定 | 螢幕鏡像設定 | Mirroring settings | — | ✅ 完全一致 | 保留 |
| PublishedAtLabel | 发布时间 | 發佈時間 | 發布時間 | Published | — | ✅ 完全一致 | 保留 |
| ReadCaptureStatusFailed | 读取采集状态失败 | 讀取擷取狀態失敗 | 讀取擷取狀態失敗 | Could not read capture status | — | ✅ 完全一致 | 保留 |
| ReadDeviceInfoFailed | 读取设备信息失败 | 讀取裝置資料失敗 | 讀取裝置資料失敗 | Could not read device information | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| ReadEnvironmentFailed | 读取环境失败 | 讀取環境失敗 | 讀取環境失敗 | Could not read the environment | — | ✅ 完全一致 | 保留 |
| RecordDescription | 点击后立即开始录制当前投屏画面。结束录制并完成 MP4 封装后，再选择保存位置和文件名。 | 按一下後立即開始錄製目前螢幕鏡像畫面。結束錄製並完成 MP4 封裝後，再選擇儲存位置和檔案名稱。 | 按一下後立即開始錄製目前螢幕鏡像畫面。結束錄製並完成 MP4 封裝後，再選擇儲存位置和檔案名稱。 | Start recording the mirrored video immediately. Choose the save location and file name after MP4 finalization finishes. | — | ✅ 完全一致 | 保留 |
| RecordMp4Filter | MP4 视频 (*.mp4)&#124;*.mp4 | MP4 影片 (*.mp4)&#124;*.mp4 | MP4 影片 (*.mp4)&#124;*.mp4 | MP4 video (*.mp4)&#124;*.mp4 | — | ✅ 完全一致 | 保留 |
| RecordSaveTitle | 保存投屏录制 | 儲存螢幕鏡像錄製 | 儲存錄製影片 | Save mirrored recording | — | ✅ 完全一致 | 保留 |
| RecordTab | 录制 | 錄製 | 錄製 | Record | — | ✅ 完全一致 | 保留 |
| RecordingDiscardFailed | 无法丢弃录制文件。 | 無法丟棄錄製檔案。 | 無法丟棄錄製檔案。 | Could not discard the recording. | — | ✅ 完全一致 | 保留 |
| RecordingDiscarded | 录制已丢弃。 | 錄製已丟棄。 | 錄製已丟棄。 | Recording discarded. | — | ✅ 完全一致 | 保留 |
| RecordingFinalizeTimeout | FFmpeg 未能在 2 分钟内完成录制文件封装。 | FFmpeg 未能在 2 分鐘內完成錄製檔案封裝。 | FFmpeg 未能在 2 分鐘內完成錄製檔案封裝。 | FFmpeg did not finalize the recording within 2 minutes. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| RecordingPendingSave | 上一段录制已完成但尚未保存，请先选择保存位置和文件名。 | 上一段錄製已完成但尚未儲存，請先選擇儲存位置和檔案名稱。 | 上一段錄製已完成但尚未儲存，請先選擇儲存位置和檔案名稱。 | The previous recording is complete but has not been saved. Choose its location and file name first. | — | ✅ 完全一致 | 保留 |
| RecordingSaveFailedFormat | 无法保存录制文件：{0} | 無法儲存錄製檔案：{0} | 無法儲存錄製檔案：{0} | Could not save the recording: {0} | — | ✅ 完全一致 | 保留 |
| RecordingSavedFormat | 录制已保存到：{0} | 錄製已儲存到：{0} | 錄製已儲存到：{0} | Recording saved to: {0} | — | ✅ 完全一致 | 保留 |
| RefreshDevices | 刷新设备 | 重新整理裝置 | 重新整理裝置 | Refresh devices | — | ✅ 完全一致 | 保留 |
| RefreshFrame | 刷新画面 | 重新整理畫面 | 重新整理畫面 | Refresh frame | — | ✅ 完全一致 | 保留 |
| RefreshMicrophones | 刷新麦克风 | 重新整理麥克風 | 重新整理麥克風 | Refresh microphones | — | ✅ 完全一致 | 保留 |
| ReleaseNotesEmpty | 未提供更新说明。 | 未提供更新說明。 | 未提供更新說明。 | No release notes were provided. | — | ✅ 完全一致 | 保留 |
| RemindLater | 稍后提醒 | 稍後提醒 | 稍後提醒 | Remind me later | — | ✅ 完全一致 | 保留 |
| RenderLimitFormat | 渲染上限 {0} | 繪製上限 {0} | 繪製上限 {0} | Render limit: {0} | — | ✅ 完全一致 | 保留 |
| RenderLimitHelp | 只限制 Windows 本地渲染；不会改变来源分辨率，也不会强制 iOS 固定输出帧率 | 只限制 Windows 本機繪製；不會改變來源解像度，也不會強制 iOS 固定輸出幀率 | 只限制 Windows 本機繪製；不會改變來源解析度，也不會強制 iOS 固定輸出影格率 | Limits Windows rendering only; it does not change source resolution or force iOS to output a fixed frame rate | — | ✅ 完全一致 | 保留 |
| RenderResolutionLimit | 渲染分辨率上限 | 繪製解像度上限 | 繪製解析度上限 | Render resolution limit | — | ✅ 完全一致 | 保留 |
| ResetImageSettings | 重置 | 還原 | 還原 | Reset | — | ✅ 完全一致 | 保留 |
| Resolution1080p | 1080p（1920×1080） | 1080p（1920×1080） | 1080p（1920×1080） | 1080p (1920×1080) | — | ✅ 完全一致 | 保留 |
| Resolution540p | 540p（960×540） | 540p（960×540） | 540p（960×540） | 540p (960×540) | — | ✅ 完全一致 | 保留 |
| Resolution720p | 720p（1280×720） | 720p（1280×720） | 720p（1280×720） | 720p (1280×720) | — | ✅ 完全一致 | 保留 |
| ResolutionLabel | 分辨率 | 解像度 | 解析度 | Resolution | — | ✅ 完全一致 | 保留 |
| ResolutionNative | 原生（不限制渲染尺寸） | 原始（不限制繪製尺寸） | 原始（不限制繪製尺寸） | Native (no render limit) | — | ✅ 完全一致 | 保留 |
| RetryUpdate | 重新下载 | 重新下載 | 重新下載 | Download again | — | ✅ 完全一致 | 保留 |
| ReverseControlBluetoothFailureAdvice | 蓝牙控制无法连接。请确认电脑蓝牙已开启、设备已配对，再重新尝试。 | 藍牙控制無法連接。請確認電腦藍牙已開啟、裝置已配對，再重新嘗試。 | 藍牙控制無法連線。請確認電腦藍牙已開啟、裝置已配對，再重新嘗試。 | Bluetooth control could not connect. Check that Bluetooth is on and the device is paired, then retry. | — | ✅ 完全一致 | 保留 |
| ReverseControlBridgeNotReady | 反向控制桥接器尚未就绪。 | 反向控制橋接器尚未就緒。 | 反向控制橋接器尚未就緒。 | The reverse-control bridge is not ready. | — | ❌ 术语不一致、❌ 缺失 → ✅ 已修复 | 统一反向控制正式名称；将代码中的用户可见硬编码文字迁入三语资源 |
| ReverseControlCaptureMuxReady | 正在通过镜像共存的 USB 通道连接设备 | 正在透過與螢幕鏡像共存的 USB 通道連接裝置 | 正在透過與螢幕鏡像共存的 USB 通道連線裝置 | Connecting over the USB channel shared with the mirror | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| ReverseControlCheckingEnvironmentFormat | 正在检查{0}反向控制的开发者环境 | 正在檢查{0}反向控制的開發者環境 | 正在檢查{0}反向控制的開發者環境 | Checking the {0} reverse-control developer environment | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| ReverseControlCheckingImageSources | 正在检查 GitHub 开发者镜像下载 | 正在檢查 GitHub 開發者映像檔下載 | 正在檢查 GitHub 開發者映像檔下載 | Checking GitHub developer-image downloads | — | ✅ 完全一致 | 保留 |
| ReverseControlConnectingWireless | 正在连接无线反向控制桥接器 | 正在連接無線反向控制橋接器 | 正在連線無線反向控制橋接器 | Connecting the wireless reverse-control bridge | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称 |
| ReverseControlDetails | 查看详细信息 | 查看詳細資料 | 查看詳細資料 | View details | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语；静态未发现引用，保留待后续调用核对 |
| ReverseControlDiscoveringWireless | 正在发现无线 CoreDevice 设备 | 正在尋找無線 CoreDevice 裝置 | 正在尋找無線 CoreDevice 裝置 | Discovering wireless CoreDevice devices | — | ✅ 完全一致 | 保留 |
| ReverseControlDownloadingImage | 正在下载并校验开发者镜像 | 正在下載並驗證開發者映像檔 | 正在下載並驗證開發者映像檔 | Downloading and verifying the developer image | — | ✅ 完全一致 | 保留 |
| ReverseControlErrorBodyFormat | {0}反向控制发生错误：{1} | {0}反向控制發生錯誤：{1} | {0}反向控制發生錯誤：{1} | {0} reverse control encountered an error: {1} | — | ✅ 完全一致 | 保留 |
| ReverseControlErrorCodeFormat | 桥接器错误代码：{0} | 橋接器錯誤代碼：{0} | 橋接器錯誤代碼：{0} | Bridge error code: {0} | — | ✅ 完全一致 | 保留 |
| ReverseControlErrorTitle | 反向控制错误 | 反向控制錯誤 | 反向控制錯誤 | Reverse control error | — | ✅ 完全一致 | 保留 |
| ReverseControlInitializingTouchFormat | 正在初始化{0}触控通道 | 正在初始化{0}觸控通道 | 正在初始化{0}觸控通道 | Initializing the {0} touch channel | — | ✅ 完全一致 | 保留 |
| ReverseControlNoDetail | 未提供详细信息 | 未提供詳細資料 | 未提供詳細資料 | No details were provided | — | ✅ 完全一致 | 保留 |
| ReverseControlNoticeErrorBadge | 反向控制未能继续 | 反向控制未能繼續 | 反向控制未能繼續 | Reverse control could not continue | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| ReverseControlPreparingImage | 正在自动准备开发者镜像（首次可能需要约 3 分钟） | 正在自動準備開發者映像檔（首次可能需要約 3 分鐘） | 正在自動準備開發者映像檔（首次可能需要約 3 分鐘） | Preparing the developer image automatically (first use may take about 3 minutes) | — | ✅ 完全一致 | 保留 |
| ReverseControlPrerequisiteStatus | 请确认准备工作后继续 | 請確認準備工作後繼續 | 請確認準備工作後繼續 | Confirm the prerequisites before continuing | — | ✅ 完全一致 | 保留 |
| ReverseControlPrerequisiteWiredBody | 1. 开启“开发者模式”并重启设备。<br>2. USB 连接设备，解锁并点击“信任此电脑”。<br>3. 首次使用会从 GitHub 下载匹配的 DDI。<br>4. 准备完成前不会发送输入。<br><br>完成后点击“继续”，否则点击“取消”。 | 1. 開啟「開發者模式」並重新啟動裝置。<br>2. 使用 USB 連接裝置，解鎖並點選「信任此電腦」。<br>3. 首次使用會從 GitHub 下載相符的 DDI。<br>4. 準備完成前不會傳送輸入。<br><br>完成後點選「繼續」，否則點選「取消」。 | 1. 開啟「開發者模式」並重新啟動裝置。<br>2. 使用 USB 連線裝置，解鎖並點選「信任此電腦」。<br>3. 首次使用會從 GitHub 下載相符的 DDI。<br>4. 準備完成前不會傳送輸入。<br><br>完成後點選「繼續」，否則點選「取消」。 | 1. Turn on Developer Mode and restart the device.<br>2. Connect by USB, unlock it, and tap “Trust This Computer”.<br>3. The matching DDI downloads from GitHub on first use.<br>4. No input is sent until setup is ready.<br><br>Click Continue when ready, or Cancel. | — | ✅ 完全一致 | 保留 |
| ReverseControlPrerequisiteWiredTitle | 启用有线控制前请确认 | 啟用有線控制前請確認 | 啟用有線控制前請確認 | Before enabling wired control | — | ✅ 完全一致 | 保留 |
| ReverseControlPrerequisiteWirelessBody | 1. 开启“开发者模式”并重启设备。<br>2. 先通过 USB 完成信任和无线配对。<br>3. 保持设备解锁，并连接同一局域网。<br>4. 允许 iPhoneMirror 通过 Windows 防火墙通信。<br><br>完成后点击“继续”，否则点击“取消”。 | 1. 開啟「開發者模式」並重新啟動裝置。<br>2. 先透過 USB 完成信任和無線配對。<br>3. 保持裝置解鎖，並連接同一區域網絡。<br>4. 允許 iPhoneMirror 通過 Windows 防火牆通訊。<br><br>完成後點選「繼續」，否則點選「取消」。 | 1. 開啟「開發者模式」並重新啟動裝置。<br>2. 先透過 USB 完成信任和無線配對。<br>3. 保持裝置解鎖，並連線同一區域網路。<br>4. 允許 iPhoneMirror 通過 Windows 防火牆通訊。<br><br>完成後點選「繼續」，否則點選「取消」。 | 1. Turn on Developer Mode and restart the device.<br>2. Complete trust and wireless pairing once over USB.<br>3. Keep the device unlocked on the same local network.<br>4. Allow iPhoneMirror through Windows Firewall.<br><br>Click Continue when ready, or Cancel. | — | ✅ 完全一致 | 保留 |
| ReverseControlPrerequisiteWirelessTitle | 启用无线控制前请确认 | 啟用無線控制前請確認 | 啟用無線控制前請確認 | Before enabling wireless control | — | ✅ 完全一致 | 保留 |
| ReverseControlRecoveryErrorTitle | 反向控制连接中断 | 反向控制連線中斷 | 反向控制連線中斷 | Reverse control disconnected | — | ✅ 完全一致 | 保留 |
| ReverseControlRemountingImage | 正在刷新旧开发者镜像以启用触控服务 | 正在重新整理舊開發者映像檔以啟用觸控服務 | 正在重新整理舊開發者映像檔以啟用觸控服務 | Refreshing the old developer image to enable touch service | — | ✅ 完全一致 | 保留 |
| ReverseControlRetry | 重试 | 重試 | 重試 | Retry | — | ✅ 完全一致 | 保留 |
| ReverseControlStartErrorTitle | 反向控制启动失败 | 反向控制啟動失敗 | 反向控制啟動失敗 | Reverse control could not start | — | ✅ 完全一致 | 保留 |
| ReverseControlStopErrorTitle | 反向控制关闭异常 | 反向控制關閉異常 | 反向控制關閉異常 | Reverse control shutdown issue | — | ✅ 完全一致 | 保留 |
| ReverseControlStopFailureAdvice | 控制已停止，但设备资源可能尚未完全释放。请重新连接设备后再试。 | 控制已停止，但裝置資源可能尚未完全釋放。請重新連接裝置後再試。 | 控制已停止，但裝置資源可能尚未完全釋放。請重新連線裝置後再試。 | Control has stopped, but device resources may not be fully released. Reconnect the device before retrying. | — | ✅ 完全一致 | 保留 |
| ReverseControlStopFailureDetailFormat | 关闭失败：{0} | 關閉失敗：{0} | 關閉失敗：{0} | Shutdown failed: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ReverseControlTitle | 反向控制 | 反向控制 | 反向控制 | Reverse control | — | ✅ 完全一致 | 保留 |
| ReverseControlTransportBluetooth | 蓝牙 | 藍牙 | 藍牙 | Bluetooth | — | ✅ 完全一致 | 保留 |
| ReverseControlTransportWired | 有线 | 有線 | 有線 | wired | — | ✅ 完全一致 | 保留 |
| ReverseControlTransportWireless | 无线 | 無線 | 無線 | wireless | — | ✅ 完全一致 | 保留 |
| ReverseControlUnknownError | 反向控制桥接器报告未知错误。 | 反向控制橋接器報告未知錯誤。 | 反向控制橋接器報告未知錯誤。 | The reverse-control bridge reported an unknown error. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| ReverseControlUsbBindingChanged | 设备 USB 绑定已改变，请重新启用控制。 | 裝置 USB 綁定已變更，請重新啟用控制。 | 裝置 USB 綁定已變更，請重新啟用控制。 | The USB device binding changed. Enable control again. | — | ✅ 完全一致 | 保留 |
| ReverseControlUsbConnected | 有线控制已连接（设备认证状态需以实测为准） | 有線控制已連接（裝置驗證狀態需以實測為準） | 有線控制已連線（裝置驗證狀態需以實測為準） | Wired control connected (device authentication status should be verified in practice) | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbConnecting | 正在连接有线控制 | 正在連接有線控制 | 正在連線有線控制 | Connecting wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbDisconnected | USB 触控通道已断开 | USB 觸控通道已中斷連線 | USB 觸控通道已中斷連線 | The USB touch channel was disconnected | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ReverseControlUsbEnabled | 有线控制已启用 | 有線控制已啟用 | 有線控制已啟用 | Wired control enabled | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbEnabledDirect | 有线控制已启用（直接 HID） | 有線控制已啟用（直接 HID） | 有線控制已啟用（直接 HID） | Wired control enabled (direct HID) | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbFailedFormat | 有线控制连接失败：{0} | 有線控制連線失敗：{0} | 有線控制連線失敗：{0} | Wired control connection failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbOff | 有线控制未启用 | 有線控制未啟用 | 有線控制未啟用 | Wired control is off | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbStopFailedFormat | 有线控制关闭失败：{0} | 有線控制關閉失敗：{0} | 有線控制關閉失敗：{0} | Wired control shutdown failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlUsbStopping | 正在关闭有线控制 | 正在關閉有線控制 | 正在關閉有線控制 | Stopping wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| ReverseControlWaitingTouchService | 正在等待开发者镜像发布触控服务 | 正在等待開發者映像檔發佈觸控服務 | 正在等待開發者映像檔發布觸控服務 | Waiting for the developer image to publish the touch service | — | ✅ 完全一致 | 保留 |
| ReverseControlWirelessConnected | 无线控制已连接 | 無線控制已連接 | 無線控制已連線 | Wireless control connected | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| ReverseControlWirelessDisconnected | 无线反向控制桥接通道已断开 | 無線反向控制通道已中斷連線 | 無線反向控制通道已中斷連線 | The wireless reverse-control channel was disconnected | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称 |
| ReverseControlWirelessEnabled | 无线控制已启用 | 無線控制已啟用 | 無線控制已啟用 | Wireless control enabled | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| ReverseControlWirelessEnabledDirect | 无线控制已启用（直接 HID） | 無線控制已啟用（直接 HID） | 無線控制已啟用（直接 HID） | Wireless control enabled (direct HID) | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| ReverseControlWirelessFailedFormat | 无线控制连接失败：{0} | 無線控制連線失敗：{0} | 無線控制連線失敗：{0} | Wireless control connection failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| ReverseControlWirelessOff | 无线控制未启用 | 無線控制未啟用 | 無線控制未啟用 | Wireless control is off | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| ReverseControlWirelessStopFailedFormat | 无线控制关闭失败：{0} | 無線控制關閉失敗：{0} | 無線控制關閉失敗：{0} | Wireless control shutdown failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| SaturationLabel | 饱和度 | 飽和度 | 飽和度 | Saturation | — | ✅ 完全一致 | 保留 |
| Save | 保存 | 儲存 | 儲存 | Save | — | ✅ 完全一致 | 保留 |
| Screenshot | 截图 | 螢幕擷取畫面 | 螢幕截圖 | Screenshot | — | ✅ 完全一致 | 保留 |
| ScreenshotBusy | 截图正在处理中，请稍候 | 螢幕擷取畫面正在處理中，請稍候 | 螢幕截圖正在處理中，請稍候 | A screenshot is already being processed | — | ✅ 完全一致 | 保留 |
| ScreenshotFailedFormat | 截图失败：{0} | 螢幕擷取畫面失敗：{0} | 螢幕截圖失敗：{0} | Screenshot failed: {0} | — | ✅ 完全一致 | 保留 |
| ScreenshotInvalidFrame | 最新视频帧的 BGRA 布局无效 | 最新影片畫面的 BGRA 排列無效 | 最新影片畫面的 BGRA 排列無效 | The latest BGRA video frame is invalid | — | ✅ 完全一致 | 保留 |
| ScreenshotNoFrame | 尚未收到可截图的视频帧 | 尚未收到可擷取的影片畫面 | 尚未收到可擷取的影片畫面 | No video frame is available for a screenshot | — | ✅ 完全一致 | 保留 |
| ScreenshotPngFilter | PNG 图片 (*.png)&#124;*.png | PNG 圖片 (*.png)&#124;*.png | PNG 圖片 (*.png)&#124;*.png | PNG image (*.png)&#124;*.png | — | ✅ 完全一致 | 保留 |
| ScreenshotSaveTitle | 保存投屏截图 | 儲存螢幕擷取畫面 | 儲存螢幕截圖 | Save mirroring screenshot | — | ✅ 完全一致 | 保留 |
| ScreenshotSavedFormat | 截图已保存：{0} | 螢幕擷取畫面已儲存：{0} | 螢幕截圖已儲存：{0} | Screenshot saved: {0} | — | ✅ 完全一致 | 保留 |
| SeparateWindow | 独立窗口 | 獨立視窗 | 獨立視窗 | Preview window | — | ✅ 完全一致 | 保留 |
| ShortcutKeyNumPadFormat | 数字小键盘 {0} | 數字小鍵盤 {0} | 數字小鍵盤 {0} | Num {0} | — | ✅ 完全一致 | 保留 |
| ShortcutModifierAlt | Alt | Alt | Alt | Alt | — | ✅ 完全一致 | 保留 |
| ShortcutModifierControl | Ctrl | Ctrl | Ctrl | Ctrl | — | ✅ 完全一致 | 保留 |
| ShortcutModifierShift | Shift | Shift | Shift | Shift | — | ✅ 完全一致 | 保留 |
| ShortcutMouseMiddle | 中键 | 中鍵 | 中鍵 | Middle mouse button | — | ✅ 完全一致 | 保留 |
| ShortcutMouseRight | 右键 | 右鍵 | 右鍵 | Right mouse button | — | ✅ 完全一致 | 保留 |
| ShortcutRegistrationFailedFormat | Windows 无法注册 {0}，该快捷键可能已被占用。 | Windows 無法註冊 {0}，該快捷鍵可能已被佔用。 | Windows 無法註冊 {0}，該快速鍵可能已被佔用。 | Windows could not register {0}; it may already be in use. | — | ✅ 完全一致 | 保留 |
| ShortcutSettings | 快捷键设置 | 快捷鍵設定 | 快速鍵設定 | Keyboard shortcuts | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsAllWindows | 显示所有应用窗口 | 顯示所有 App 視窗 | 顯示所有 App 視窗 | Show All App Windows | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsAppSwitcher | 打开多任务 | 開啟多工 | 開啟多工 | Open App Switcher | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsBluetoothControl | 蓝牙控制 | 藍牙控制 | 藍牙控制 | Bluetooth control | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsBossKey | 老板键（隐藏 / 显示窗口） | 老闆鍵（隱藏 / 顯示視窗） | 老闆鍵（隱藏 / 顯示視窗） | Boss key (hide / show windows) | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsBossKeyKeyboardOnly | 老板键只能使用键盘快捷键，以便隐藏窗口后仍可恢复。 | 老闆鍵只能使用鍵盤快捷鍵，以便隱藏視窗後仍可還原。 | 老闆鍵只能使用鍵盤快速鍵，以便隱藏視窗後仍可還原。 | Boss Key requires a keyboard shortcut so hidden windows can still be restored. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsCaptureHint | 聚焦输入框后按下快捷键。Backspace 或 Delete 可解除绑定；F1-F11、右键、中键可单独使用。 | 聚焦輸入框後按下快捷鍵。按 Backspace 或 Delete 可解除繫結；F1-F11、右鍵、中鍵可單獨使用。 | 點選輸入框後按下快速鍵。按 Backspace 或 Delete 可解除綁定；F1-F11、右鍵、中鍵可單獨使用。 | Focus the field, then press a shortcut. Backspace or Delete unbinds it; F1-F11, right mouse, and middle mouse can be used alone. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsCategoryAssistant | 助理 | 助理 | 助理 | Assistant | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsCategoryControl | 控制 | 控制 | 控制 | Control | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsCategoryNavigation | 系统导航 | 系統導覽 | 系統導覽 | System navigation | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsControlCenter | 打开控制中心 | 開啟控制中心 | 開啟控制中心 | Open Control Center | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsDescription | 配置投屏工具的快捷键。 | 設定螢幕鏡像工具的快捷鍵。 | 設定螢幕鏡像工具的快速鍵。 | Configure shortcuts for mirroring tools. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsDock | 显示 Dock | 顯示 Dock | 顯示 Dock | Show Dock | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsDuplicate | 每个动作必须使用不同的快捷键。 | 每個動作必須使用不同的快捷鍵。 | 每個動作必須使用不同的快速鍵。 | Each action must use a different shortcut. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsDuplicateBindingFormat | {0} 已用于“{1}”，请选择其他快捷键或先解除原绑定。 | {0} 已用於「{1}」，請選擇其他快捷鍵或先解除原綁定。 | {0} 已用於「{1}」，請選擇其他快速鍵或先解除原綁定。 | {0} is already assigned to {1}. Choose another shortcut or clear that binding first. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsFullScreen | 切换全屏 | 切換全螢幕 | 切換全螢幕 | Toggle Full Screen | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsHome | 回到桌面 | 返回主畫面 | 返回主畫面 | Go to Home Screen | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsInvalid | 请选择有效快捷键。F12、Windows 键组合及单独修饰键不可用。 | 請選擇有效快捷鍵。F12、Windows 鍵組合及單獨修飾鍵不可用。 | 請選擇有效快速鍵。F12、Windows 鍵組合及單獨修飾鍵不可用。 | Choose a valid shortcut. F12, Windows-key shortcuts, and modifier-only keys are unavailable. | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsKeyboardShortcuts | 显示键盘快捷键 | 顯示鍵盤快速鍵 | 顯示鍵盤快速鍵 | Show Keyboard Shortcuts | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsLockScreen | 锁定屏幕 | 鎖定螢幕 | 鎖定螢幕 | Lock Screen | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsNextApp | 切换到下一个应用 | 切換至下一個 App | 切換至下一個 App | Switch to Next App | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsNotificationCenter | 打开通知栏 | 開啟通知中心 | 開啟通知中心 | Open Notification Center | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsPreviousApp | 切换到上一个应用 | 切換至上一個 App | 切換至上一個 App | Switch to Previous App | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsQuickNote | 新建快捷笔记 | 新增快速備忘錄 | 新增快速備忘錄 | New Quick Note | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsReset | 恢复默认值 | 還原預設值 | 還原預設值 | Restore defaults | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| ShortcutSettingsReverseControl | 反向控制 | 反向控制 | 反向控制 | Reverse control | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsSaveFailed | 无法保存快捷键设置。 | 無法儲存快捷鍵設定。 | 無法儲存快速鍵設定。 | Could not save keyboard shortcut settings. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| ShortcutSettingsSearch | 打开搜索 | 開啟搜尋 | 開啟搜尋 | Open Search | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ShortcutSettingsSiri | 唤醒 Siri | 啟動 Siri | 啟動 Siri | Start Siri | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsTitle | 快捷键设置 | 快捷鍵設定 | 快速鍵設定 | Keyboard shortcuts | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsUnbound | 未绑定 | 未繫結 | 未綁定 | Unbound | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsVolumeDown | 音量- | 音量- | 音量- | Volume down | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsVolumeUp | 音量+ | 音量+ | 音量+ | Volume up | — | ✅ 完全一致 | 保留 |
| ShortcutSettingsWiredControl | 有线控制 | 有線控制 | 有線控制 | Wired control | — | ❌ 含义不一致 → ✅ 已修复 | 菜单和快捷键实际调用控制功能，不是开始投屏 |
| ShortcutSettingsWirelessControl | 无线控制 | 無線控制 | 無線控制 | Wireless control | — | ❌ 含义不一致 → ✅ 已修复 | 菜单和快捷键实际调用控制功能，不是开始投屏 |
| SimultaneousMirrorFailedFormat | 同时投屏启动失败：{0} | 同時螢幕鏡像啟動失敗：{0} | 同時螢幕鏡像啟動失敗：{0} | Could not start simultaneous mirroring: {0} | — | ✅ 完全一致 | 保留 |
| SimultaneousMirrorStartedFormat | 已为 {0} 打开同时投屏窗口 | 已為 {0} 開啟同時螢幕鏡像視窗 | 已為 {0} 開啟同時螢幕鏡像視窗 | Opened a simultaneous mirror window for {0} | — | ✅ 完全一致 | 保留 |
| StartBluetoothControl | 启用蓝牙控制 | 啟用藍牙控制 | 啟用藍牙控制 | Enable Bluetooth control | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| StartFailedFormat | 开始投屏失败：{0} | 開始螢幕鏡像失敗：{0} | 開始螢幕鏡像失敗：{0} | Could not start mirroring: {0} | — | ✅ 完全一致 | 保留 |
| StartMirroring | 开始投屏 | 開始螢幕鏡像 | 開始螢幕鏡像 | Start mirroring | — | ✅ 完全一致 | 保留 |
| StartRecording | 开始录制 | 開始錄製 | 開始錄製 | Start recording | — | ✅ 完全一致 | 保留 |
| StartRequested | 已请求开始投屏 | 已請求開始螢幕鏡像 | 已請求開始螢幕鏡像 | Mirroring start requested | — | ✅ 完全一致 | 保留 |
| StartStreaming | 开始直播推流 | 開始直播串流 | 開始直播串流 | Start live stream | — | ✅ 完全一致 | 保留 |
| StartVirtualCamera | 启动虚拟摄像头 | 啟動虛擬攝影機 | 啟動虛擬攝影機 | Start virtual camera | — | ✅ 完全一致 | 保留 |
| StartingInstaller | 正在启动安装程序，应用即将安全退出… | 正在啟動安裝程式，應用程式即將安全退出… | 正在啟動安裝程式，應用程式即將安全退出… | Starting the installer. The app will close safely… | — | ✅ 完全一致 | 保留 |
| StartupErrorClose | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| StartupErrorDetails | 错误详情 | 錯誤詳細資料 | 錯誤詳細資料 | Error details | — | ✅ 完全一致 | 保留 |
| StartupErrorHeading | iPhoneMirror 无法启动 | iPhoneMirror 無法啟動 | iPhoneMirror 無法啟動 | iPhoneMirror could not start | — | ✅ 完全一致 | 保留 |
| StartupErrorLogLabel | 诊断日志 | 診斷記錄 | 診斷記錄檔 | Diagnostic log | — | ✅ 完全一致 | 保留 |
| StartupErrorOpenLog | 打开日志位置 | 開啟記錄位置 | 開啟記錄檔位置 | Open log location | — | ✅ 完全一致 | 保留 |
| StatusCheckingEnvironment | 正在检查 Apple USB 环境… | 正在檢查 Apple USB 環境… | 正在檢查 Apple USB 環境… | Checking the Apple USB environment… | — | ✅ 完全一致 | 保留 |
| StatusDefaultSettings | 原生 · 60 fps | 原始 · 60 fps | 原始 · 60 fps | Native · 60 fps | — | ❌ 含义不一致 → ✅ 已修复 | Native 表示原始分辨率，不是默认设置 |
| StatusDetecting | 检测中 | 偵測中 | 偵測中 | Detecting | — | ✅ 完全一致 | 保留 |
| StatusWaiting | 等待 | 等待 | 等待 | Waiting | — | ✅ 完全一致 | 保留 |
| StatusWaitingDevice | 等待设备连接 | 等待裝置連接 | 等待裝置連線 | Waiting for a device | — | ✅ 完全一致 | 保留 |
| StatusWaitingLog | 等待核心日志… | 等待核心記錄… | 等待核心記錄檔… | Waiting for core logs… | — | ✅ 完全一致 | 保留 |
| StopBluetoothControl | 关闭蓝牙控制 | 關閉藍牙控制 | 關閉藍牙控制 | Disable Bluetooth control | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为蓝牙控制 |
| StopFailedFormat | 停止投屏失败：{0} | 停止螢幕鏡像失敗：{0} | 停止螢幕鏡像失敗：{0} | Could not stop mirroring: {0} | — | ✅ 完全一致 | 保留 |
| StopMediaOutput | 停止输出 | 停止輸出 | 停止輸出 | Stop output | — | ✅ 完全一致 | 保留 |
| StopMirroring | 停止投屏 | 停止螢幕鏡像 | 停止螢幕鏡像 | Stop mirroring | — | ✅ 完全一致 | 保留 |
| StopSessionReleased | 投屏已停止，会话资源已释放 | 螢幕鏡像已停止，工作階段資源已釋放 | 螢幕鏡像已停止，工作階段資源已釋放 | Mirroring stopped and session resources were released | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| StopUsbRestoreWarningFormat | 投屏已停止，会话资源已释放；但未能确认 iPhone 已恢复普通 USB 配置。诊断：{0} | 螢幕鏡像已停止，工作階段資源已釋放；但未能確認 iPhone 已還原普通 USB 設定。診斷：{0} | 螢幕鏡像已停止，工作階段資源已釋放；但未能確認 iPhone 已還原一般 USB 設定。診斷：{0} | Mirroring stopped and session resources were released, but the iPhone's normal USB configuration was not confirmed. Diagnostic: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| StreamAuthorization | WHIP Bearer 令牌（可选） | WHIP Bearer Token（可選） | WHIP Bearer Token（可選） | WHIP bearer token (optional) | — | ✅ 完全一致 | 保留 |
| StreamDescription | 通过 RTMP、SRT 或 WebRTC WHIP 端点发送当前投屏画面。 | 透過 RTMP、SRT 或 WebRTC WHIP 端點傳送目前的螢幕鏡像畫面。 | 透過 RTMP、SRT 或 WebRTC WHIP 端點傳送目前的螢幕鏡像畫面。 | Send the selected mirror through RTMP, SRT, or a WebRTC WHIP endpoint. | — | ✅ 完全一致 | 保留 |
| StreamDestination | 服务器地址或 WHIP 端点 | 伺服器位址或 WHIP 端點 | 伺服器位址或 WHIP 端點 | Server or WHIP endpoint | — | ✅ 完全一致 | 保留 |
| StreamMicrophone | 直播麦克风 | 直播麥克風 | 直播麥克風 | Stream microphone | — | ✅ 完全一致 | 保留 |
| StreamMicrophoneHint | 与手机声音混合。建议使用耳机避免回声。 | 與手機聲音混合。建議使用耳機避免回音。 | 與手機聲音混合。建議使用耳機避免回音。 | Mix with phone audio. Use headphones to avoid echo. | — | ✅ 完全一致 | 保留 |
| StreamProtocol | 协议 | 協議 | 協定 | Protocol | — | ✅ 完全一致 | 保留 |
| StreamTab | 直播推流 | 直播串流 | 直播串流 | Live stream | — | ✅ 完全一致 | 保留 |
| StreamingStopTimeout | FFmpeg 未能在 15 秒内停止直播推流。 | FFmpeg 未能在 15 秒內停止直播串流。 | FFmpeg 未能在 15 秒內停止直播串流。 | FFmpeg did not stop live streaming within 15 seconds. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| SwitchDeviceFailedFormat | 切换设备失败：{0} | 切換裝置失敗：{0} | 切換裝置失敗：{0} | Could not switch devices: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| SwitchingDeviceFormat | 正在停止当前投屏并切换到 {0}… | 正在停止目前螢幕鏡像並切換到 {0}… | 正在停止目前螢幕鏡像並切換到 {0}… | Stopping the current session before switching to {0}… | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| TargetFpsFormat | 本地上限 {0} fps | 本機上限 {0} fps | 本機上限 {0} fps | Local limit: {0} fps | — | ✅ 完全一致 | 保留 |
| TargetFrameRate | 本地渲染帧率上限 | 本機繪製幀率上限 | 本機繪製影格率上限 | Local rendering frame-rate limit | — | ✅ 完全一致 | 保留 |
| TestingUpdateRoutes | 正在测试 GitHub 和镜像线路… | 正在測試 GitHub 和鏡像線路… | 正在測試 GitHub 和鏡像線路… | Testing GitHub and mirror routes… | — | ✅ 完全一致 | 保留 |
| ThemeDark | 深色 | 深色 | 深色 | Dark | — | ✅ 完全一致 | 保留 |
| ThemeLight | 浅色 | 淺色 | 淺色 | Light | — | ✅ 完全一致 | 保留 |
| ThemeSystem | 跟随系统 | 跟隨系統 | 跟隨系統 | Use system setting | — | ✅ 完全一致 | 保留 |
| ThemeTitle | 应用主题 | 應用程式主題 | 應用程式主題 | App theme | — | ✅ 完全一致 | 保留 |
| TitleBarCloseToolTip | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| TitleBarMaximizeToolTip | 最大化或还原 | 最大化或還原 | 最大化或還原 | Maximize or restore | — | ✅ 完全一致 | 保留 |
| TitleBarMinimizeToolTip | 最小化 | 最小化 | 最小化 | Minimize | — | ✅ 完全一致 | 保留 |
| TouchBridgeAlreadyStarted | USB 触控桥接器已经启动。 | USB 觸控橋接器已啟動。 | USB 觸控橋接器已啟動。 | The USB touch bridge has already started. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeAuthenticationRequired | 设备未确认触控认证已开启，无法启动反向控制。 | 裝置未確認觸控驗證已開啟，無法啟動反向控制。 | 裝置未確認觸控驗證已開啟，無法啟動反向控制。 | The device did not confirm touch authentication, so reverse control could not start. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeDiagnosticFormat |  最近诊断：{0} |  最近診斷：{0} |  最近診斷：{0} |  Latest diagnostic: {0} | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeErrorCodeFormat | （错误代码：{0}） | （錯誤代碼：{0}） | （錯誤代碼：{0}） |  (error code: {0}) | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeExitedFormat | USB 触控桥接器意外退出（代码 {0}）。 | USB 觸控橋接器意外結束（代碼 {0}）。 | USB 觸控橋接器意外結束（代碼 {0}）。 | The USB touch bridge exited unexpectedly (code {0}). | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeInvalidBatch | 触控批次必须包含 1 到 5 个触点。 | 觸控批次必須包含 1 至 5 個觸點。 | 觸控批次必須包含 1 至 5 個觸點。 | A touch batch must contain 1 to 5 points. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeInvalidCoordinates | 触点坐标必须是 0 到 1 之间的有限数值。 | 觸點座標必須是 0 至 1 之間的有限數值。 | 觸點座標必須是 0 至 1 之間的有限數值。 | Touch coordinates must be finite numbers between 0 and 1. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeInvalidOutput | USB 触控桥接器输出格式无效。 | USB 觸控橋接器輸出格式無效。 | USB 觸控橋接器輸出格式無效。 | The USB touch bridge output format is invalid. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeInvalidPointId | 触点编号必须为非负整数。 | 觸點編號必須為非負整數。 | 觸點編號必須為非負整數。 | Touch point IDs must be non-negative integers. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeNotReady | USB 触控桥接器尚未就绪。 | USB 觸控橋接器尚未就緒。 | USB 觸控橋接器尚未就緒。 | The USB touch bridge is not ready. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeNotStarted | USB 触控桥接器未启动。 | USB 觸控橋接器未啟動。 | USB 觸控橋接器未啟動。 | The USB touch bridge has not started. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeOutputClosed | USB 触控桥接器在就绪前关闭了输出通道。 | USB 觸控橋接器在就緒前關閉了輸出通道。 | USB 觸控橋接器在就緒前關閉了輸出通道。 | The USB touch bridge closed its output channel before it was ready. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeOutputDisconnected | USB 触控桥接器意外关闭了输出通道。 | USB 觸控橋接器意外關閉了輸出通道。 | USB 觸控橋接器意外關閉了輸出通道。 | The USB touch bridge unexpectedly closed its output channel. | — | ✅ 完全一致 | 保留 |
| TouchBridgeReportedError | USB 触控桥接器报告错误。 | USB 觸控橋接器報告錯誤。 | USB 觸控橋接器報告錯誤。 | The USB touch bridge reported an error. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeRuntimeIncompleteFormat | USB 触控桥接组件不完整：{0}。请重新安装完整的 iPhoneMirror 安装包。 | USB 觸控橋接元件不完整：{0}。請重新安裝完整的 iPhoneMirror 安裝套件。 | USB 觸控橋接元件不完整：{0}。請重新安裝完整的 iPhoneMirror 安裝套件。 | The USB touch bridge runtime is incomplete: {0}. Reinstall the full iPhoneMirror package. | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeStartFailed | 无法启动 USB 触控桥接器。 | 無法啟動 USB 觸控橋接器。 | 無法啟動 USB 觸控橋接器。 | Could not start the USB touch bridge. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeTargetMismatch | 反向控制桥接目标 UDID 不匹配。 | 反向控制橋接目標 UDID 不符。 | 反向控制橋接目標 UDID 不符。 | The reverse-control bridge target UDID does not match. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| TouchBridgeTargetMismatchFormat | 反向控制目标不匹配：请求 {0}，实际 {1}。 | 反向控制目標不符：要求 {0}，實際為 {1}。 | 反向控制目標不符：要求 {0}，實際為 {1}。 | Reverse-control target mismatch: requested {0}, received {1}. | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeTimeoutFormat | USB 触控桥接器在 {0:0} 秒内未就绪。详细信息：{1} | USB 觸控橋接器在 {0:0} 秒內未就緒。詳細資料：{1} | USB 觸控橋接器在 {0:0} 秒內未就緒。詳細資料：{1} | The USB touch bridge was not ready within {0:0} s. Details: {1} | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TouchBridgeTransportMismatchFormat | 反向控制传输类型不匹配：请求 {0}，实际 {1}。 | 反向控制傳輸類型不符：要求 {0}，實際為 {1}。 | 反向控制傳輸類型不符：要求 {0}，實際為 {1}。 | Reverse-control transport mismatch: requested {0}, received {1}. | — | ❌ 缺失 → ✅ 已修复 | 桥接错误提示迁入三语资源；原始诊断参数保留 |
| TrayExit | 退出应用 | 結束應用程式 | 結束應用程式 | Exit app | — | ✅ 完全一致 | 保留 |
| TrayHidePanel | 收起到托盘 | 收起到系統匣 | 收起到系統匣 | Hide to system tray | — | ✅ 完全一致 | 保留 |
| TrayNoDevices | 连接并解锁 USB 设备，或在 iPhone 的屏幕镜像中选择此电脑。 | 連接並解鎖 USB 裝置，或在 iPhone 的螢幕鏡像中選擇此電腦。 | 連線並解鎖 USB 裝置，或在 iPhone 的螢幕鏡像中選擇此電腦。 | Connect and unlock a USB device, or select this computer in iPhone Screen Mirroring. | — | ✅ 完全一致 | 保留 |
| TrayPanelSubtitle | 选择设备，直接在独立窗口投屏 | 選擇裝置，直接在獨立視窗投影 | 選擇裝置，在獨立視窗中進行螢幕鏡像 | Choose a device to mirror in its own window | — | ✅ 完全一致 | 保留 |
| TrayStartMirroring | 打开独立投屏窗口 | 開啟獨立投影視窗 | 開啟獨立螢幕鏡像視窗 | Open mirroring window | — | ✅ 完全一致 | 保留 |
| TrayStarting | 正在打开投屏窗口… | 正在開啟投影視窗… | 正在開啟螢幕鏡像視窗… | Opening mirroring window… | — | ✅ 完全一致 | 保留 |
| TrayUnavailable | 无法创建托盘图标：{0} | 無法建立系統匣圖示：{0} | 無法建立系統匣圖示：{0} | Could not create the tray icon: {0} | — | ✅ 完全一致 | 保留 |
| UiBluetoothRefreshFailed | 无法刷新蓝牙设备。请检查蓝牙连接后重试；列表可能不是最新状态。 | 無法重新整理藍牙裝置。請檢查藍牙連線後重試；清單可能不是最新狀態。 | 無法重新整理藍牙裝置。請檢查藍牙連線後重試；清單可能不是最新狀態。 | Could not refresh Bluetooth devices. Check Bluetooth and try again; the list may be out of date. | — | ✅ 完全一致 | 保留 |
| UiLogLocationFailed | 无法打开日志位置。你可以复制以下路径并在文件资源管理器中打开：<br>{0} | 無法開啟記錄位置。你可以複製以下路徑並在檔案總管中開啟：<br>{0} | 無法開啟記錄檔位置。您可以複製以下路徑並在檔案總管中開啟：<br>{0} | Could not open the log location. Copy this path and open it in File Explorer:<br>{0} | — | ✅ 完全一致 | 保留 |
| UiProtectedContentShort | 受保护内容 | 受保護內容 | 受保護內容 | Protected content | — | ✅ 完全一致 | 保留 |
| UiRefreshing | 正在刷新… | 正在重新整理… | 正在重新整理… | Refreshing… | — | ✅ 完全一致 | 保留 |
| Unavailable | 不可用 | 不可用 | 不可用 | Unavailable | — | ✅ 完全一致 | 保留 |
| UninstallVirtualCamera | 卸载虚拟摄像头组件 | 解除安裝虛擬攝影機元件 | 解除安裝虛擬攝影機元件 | Uninstall virtual camera component | — | ✅ 完全一致 | 保留 |
| UnknownError | 未知错误 | 未知錯誤 | 未知錯誤 | Unknown error | — | ✅ 完全一致 | 保留 |
| UpdateAvailableFormat | 发现新版本 {0}。 | 發現新版本 {0}。 | 發現新版本 {0}。 | Version {0} is available. | — | ✅ 完全一致 | 保留 |
| UpdateCheckCancelled | 更新检查已取消。 | 更新檢查已取消。 | 更新檢查已取消。 | The update check was cancelled. | — | ✅ 完全一致 | 保留 |
| UpdateCheckFailedFormat | 无法检查更新：{0} | 無法檢查更新：{0} | 無法檢查更新：{0} | Could not check for updates: {0} | — | ✅ 完全一致 | 保留 |
| UpdateChecksumDownloadUnavailable | 所有校验清单下载线路暂时不可用。 | 所有驗證清單下載線路暫時不可用。 | 所有驗證清單下載線路暫時不可用。 | All checksum download routes are temporarily unavailable. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateChecksumEntryMissingFormat | SHA256SUMS.txt 中没有 {0} 的校验值。 | SHA256SUMS.txt 中沒有 {0} 的驗證值。 | SHA256SUMS.txt 中沒有 {0} 的驗證值。 | SHA256SUMS.txt does not contain a checksum for {0}. | — | ❌ 缺失 → ✅ 已修复 | 用户可见错误详情补齐三语 |
| UpdateChecksumFailed | 下载的更新未通过 SHA-256 校验。 | 下載的更新未通過 SHA-256 驗證。 | 下載的更新未通過 SHA-256 驗證。 | The downloaded update failed SHA-256 verification. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateChecksumListInvalid | 校验清单未通过 SHA-256 校验。 | 驗證清單未通過 SHA-256 驗證。 | 驗證清單未通過 SHA-256 驗證。 | The checksum manifest failed SHA-256 verification. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateChecksumListTooLarge | 校验清单大小超出允许范围。 | 驗證清單大小超出允許範圍。 | 驗證清單大小超出允許範圍。 | The checksum manifest exceeds the allowed size. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateChecksumRequired | 此版本未提供可信的 SHA-256 校验值，无法安装。 | 此版本未提供可信的 SHA-256 驗證值，無法安裝。 | 此版本未提供可信的 SHA-256 驗證值，無法安裝。 | This release does not provide a trusted SHA-256 value and cannot be installed. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateChecksumSizeMismatch | 校验清单大小与发布信息不符。 | 驗證清單大小與發佈資料不符。 | 驗證清單大小與發布資料不符。 | The checksum manifest size does not match the release metadata. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateDigestInvalid | 更新校验值无效。 | 更新驗證值無效。 | 更新驗證值無效。 | The update verification digest is invalid. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateDigestMissing | 更新校验值缺失或无效。 | 更新驗證值遺失或無效。 | 更新驗證值遺失或無效。 | The update verification digest is missing or invalid. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateDownloadCancelled | 更新下载已取消，可稍后重新尝试。 | 更新下載已取消，可稍後重新嘗試。 | 更新下載已取消，可稍後重新嘗試。 | The update download was cancelled. You can retry later. | — | ✅ 完全一致 | 保留 |
| UpdateDownloadEndpointsUnavailable | 所有更新下载线路暂时不可用。 | 所有更新下載線路暫時不可用。 | 所有更新下載線路暫時不可用。 | All update download routes are temporarily unavailable. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateDownloadFailedFormat | 更新失败：{0} | 更新失敗：{0} | 更新失敗：{0} | Update failed: {0} | — | ✅ 完全一致 | 保留 |
| UpdateDownloadUntrustedRedirect | 更新下载重定向到了不受信任的网站。 | 更新下載重新導向至不受信任的網站。 | 更新下載重新導向至不受信任的網站。 | The update download redirected to an untrusted host. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateDownloadedNoChecksum | 下载完成；此 Release 未提供 SHA-256 校验值。 | 下載完成；此 Release 未提供 SHA-256 驗證值。 | 下載完成；此 Release 未提供 SHA-256 驗證值。 | Download complete. This release did not provide a SHA-256 value. | — | ❌ 术语不一致 → ✅ 已修复 | 统一校验算法显示名称 |
| UpdateEndpointsUnavailable | 所有更新服务暂时不可用。 | 所有更新服務暫時不可用。 | 所有更新服務暫時不可用。 | All update services are temporarily unavailable. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateFormatUnsupported | 不支持此更新包格式。 | 不支援此更新套件格式。 | 不支援此更新套件格式。 | This update package format is unsupported. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateHelperMissing | 缺少内置 ZIP 更新助手。 | 缺少內置 ZIP 更新助手。 | 缺少內建 ZIP 更新助手。 | The embedded ZIP update helper is missing. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateHelperStartFailed | Windows 无法启动更新助手。 | Windows 無法啟動更新助手。 | Windows 無法啟動更新助手。 | Windows could not start the update helper. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateInstallerPackageMissing | 此版本未提供 Windows 安装包。 | 此版本未提供 Windows 安裝套件。 | 此版本未提供 Windows 安裝套件。 | This release does not provide a Windows installer package. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateNetworkUnavailable | 所有更新线路暂时不可用，请检查网络连接后重试。 | 所有更新連線暫時不可用，請檢查網絡連接後重試。 | 所有更新連線暫時不可用，請檢查網路連線後重試。 | All update routes are temporarily unavailable. Check your connection and try again. | — | ✅ 完全一致 | 保留 |
| UpdateNow | 立即更新 | 立即更新 | 立即更新 | Update now | — | ✅ 完全一致 | 保留 |
| UpdatePackageChanged | 更新包在校验后发生变化，已停止安装。 | 更新套件在驗證後發生變更，已停止安裝。 | 更新套件在驗證後發生變更，已停止安裝。 | The update package changed after verification. Installation stopped. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdatePackageNotVerified | 更新包未经验证，不会运行。 | 更新套件未經驗證，不會執行。 | 更新套件未經驗證，不會執行。 | The update package has not been verified and will not run. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdatePackageTooLarge | 更新包大小超出允许范围。 | 更新套件大小超出允許範圍。 | 更新套件大小超出允許範圍。 | The update package exceeds the allowed size. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdatePortableDirectoryUnsafe | 便携版安装目录包含可写子目录或重解析点，无法安全地使用管理员权限更新。 | 可攜式版本的安裝資料夾包含可寫入的子資料夾或重新分析點，無法安全地使用管理員權限更新。 | 可攜式版本的安裝資料夾包含可寫入的子資料夾或重新分析點，無法安全地使用管理員權限更新。 | The portable installation contains a writable subdirectory or reparse point and cannot be safely updated with administrator privileges. | — | ❌ 缺失 → ✅ 已修复 | 更新启动失败提示补齐三语 |
| UpdatePortableElevationUnsupported | 请以非管理员身份重新启动 iPhoneMirror，再更新便携版。 | 請以非管理員身分重新啟動 iPhoneMirror，再更新可攜式版本。 | 請以非管理員身分重新啟動 iPhoneMirror，再更新可攜式版本。 | Restart iPhoneMirror without administrator privileges, then update the portable edition. | — | ❌ 缺失 → ✅ 已修复 | 更新启动失败提示补齐三语 |
| UpdatePortablePackageMissing | 此版本未提供便携 ZIP 包。 | 此版本未提供可攜式 ZIP 套件。 | 此版本未提供可攜式 ZIP 套件。 | This release does not provide a portable ZIP package. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdatePortableRequired | 便携版必须使用便携 ZIP 包更新。 | 可攜式版本必須使用可攜式 ZIP 套件更新。 | 可攜式版本必須使用可攜式 ZIP 套件更新。 | The portable edition must be updated with the portable ZIP package. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateReleaseFieldMissingFormat | GitHub 版本信息缺少 {0}。 | GitHub 版本資料缺少 {0}。 | GitHub 版本資料缺少 {0}。 | The GitHub release is missing {0}. | — | ❌ 缺失 → ✅ 已修复 | 用户可见错误详情补齐三语 |
| UpdateReleaseListInvalid | GitHub 返回了无效的版本列表。 | GitHub 傳回了無效的版本清單。 | GitHub 傳回了無效的版本清單。 | GitHub returned an invalid release list. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateReleaseListTooLarge | 更新版本列表大小超出允许范围。 | 更新版本清單大小超出允許範圍。 | 更新版本清單大小超出允許範圍。 | The release list exceeds the allowed size. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateRequestTimedOut | 连接更新服务超时，请稍后重试。 | 連接更新服務逾時，請稍後重試。 | 連線更新服務逾時，請稍後重試。 | The update request timed out. Try again later. | — | ✅ 完全一致 | 保留 |
| UpdateResponseUrlMissing | 更新服务未返回最终下载地址。 | 更新服務未傳回最終下載位址。 | 更新服務未傳回最終下載位址。 | The update service did not return a final download URL. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateSettingsDescription | 控制后台检查、自动下载、镜像加速和发布通道。 | 控制背景更新檢查、自動下載、鏡像加速和發佈通道。 | 控制背景更新檢查、自動下載、鏡像加速和發布通道。 | Control background checks, automatic downloads, mirror acceleration, and release channels. | — | ✅ 完全一致 | 保留 |
| UpdateSettingsTitle | 更新设置 | 更新設定 | 更新設定 | Update settings | — | ✅ 完全一致 | 保留 |
| UpdateSetupRequired | 安装版必须使用 Windows 安装包更新。 | 安裝版必須使用 Windows 安裝套件更新。 | 安裝版必須使用 Windows 安裝套件更新。 | The installed edition must be updated with the Windows Setup package. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateStatusReady | 可随时检查 GitHub Release 更新。 | 可隨時檢查 GitHub Release 更新。 | 可隨時檢查 GitHub Release 更新。 | Check GitHub Releases for updates at any time. | — | ✅ 完全一致 | 保留 |
| UpdateStatusTitle | 更新状态 | 更新狀態 | 更新狀態 | Update status | — | ✅ 完全一致 | 保留 |
| UpdateUnsafeFileName | 更新文件名不安全，已停止下载。 | 更新檔案名稱不安全，已停止下載。 | 更新檔案名稱不安全，已停止下載。 | The update file name is unsafe. Download stopped. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateUntrustedAssetUrl | 更新下载地址不受信任。 | 更新下載位址不受信任。 | 更新下載位址不受信任。 | The update download URL is not trusted. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateUntrustedNotesRedirect | 更新说明地址重定向到了不受信任的网站。 | 更新說明位址重新導向至不受信任的網站。 | 更新說明位址重新導向至不受信任的網站。 | The release notes URL redirected to an untrusted host. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateUntrustedRedirect | 更新服务重定向到了不受信任的网站。 | 更新服務重新導向至不受信任的網站。 | 更新服務重新導向至不受信任的網站。 | The update service redirected to an untrusted host. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| UpdateVerified | 下载完成，SHA-256 校验通过。 | 下載完成，SHA-256 驗證通過。 | 下載完成，SHA-256 驗證通過。 | Download complete. SHA-256 verification passed. | — | ❌ 术语不一致 → ✅ 已修复 | 统一校验算法显示名称 |
| UpdateVirtualCamera | 更新虚拟摄像头组件 | 更新虛擬攝影機元件 | 更新虛擬攝影機元件 | Update virtual camera component | — | ✅ 完全一致 | 保留 |
| UpdateWindowSubtitle | 查看更新内容并一键完成升级 | 查看更新內容並一按完成升級 | 查看更新內容並一鍵完成升級 | Review the changes and upgrade in one step | — | ✅ 完全一致 | 保留 |
| UpdateWindowTitle | 发现新版本 | 發現新版本 | 發現新版本 | A new version is available | — | ✅ 完全一致 | 保留 |
| UsbControlDefault | 反向控制 | 反向控制 | 反向控制 | Reverse control | — | ✅ 完全一致 | 保留 |
| UsbControlDisable | 关闭有线控制 | 關閉有線控制 | 關閉有線控制 | Disable wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbControlDisableWireless | 关闭无线控制 | 關閉無線控制 | 關閉無線控制 | Disable wireless control | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| UsbControlFailureAppleUsbmux | Apple USB 配对服务未就绪。请在驱动管理中安装或修复 Apple Devices/iTunes 支持，连接并解锁 iPhone 后重试。 | Apple USB 配對服務未就緒。請在驅動程式管理員中安裝或修復 Apple Devices/iTunes 支援，連接並解鎖 iPhone 後重試。 | Apple USB 配對服務未就緒。請在驅動程式管理員中安裝或修復 Apple Devices/iTunes 支援，連線並解鎖 iPhone 後重試。 | The Apple USB pairing service is not ready. Install or repair Apple Devices/iTunes support in the driver manager, connect and unlock the iPhone, then try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureDeveloperMode | 此设备未开启开发者模式。请在 iPhone/iPad 的“设置 &gt; 隐私与安全性 &gt; 开发者模式”中开启并重启设备后重试。 | 此裝置未開啟開發者模式。請在 iPhone/iPad 的「設定 &gt; 私隱與安全性 &gt; 開發者模式」中開啟並重新啟動裝置後重試。 | 此裝置未開啟開發者模式。請在 iPhone/iPad 的「設定 &gt; 隱私權與安全性 &gt; 開發者模式」中開啟並重新啟動裝置後重試。 | Developer Mode is not enabled on this device. Turn it on under iPhone/iPad Settings → Privacy &amp; Security → Developer Mode, then restart the device and try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureDeviceLocked | iPhone 正处于锁定状态或正在等待输入密码。请解锁手机并保持亮屏，如有“信任此电脑”或密码提示请先在手机上完成，然后重新启动反向控制。 | iPhone 目前處於鎖定狀態或正在等待輸入密碼。請解鎖手機並保持亮屏，如有「信任此電腦」或密碼提示請先在手機上完成，然後重新啟動反向控制。 | iPhone 目前處於鎖定狀態或正在等待輸入密碼。請解鎖手機並保持螢幕開啟，如有「信任此電腦」或密碼提示請先在手機上完成，然後重新啟動反向控制。 | The iPhone is locked or waiting for its passcode. Unlock the phone and keep the screen on, complete any Trust This Computer or passcode prompt on the phone, then start reverse control again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureDeviceNotFound | 未找到这台设备的 Apple 网络配对会话。请先用数据线连接一次，在 Apple Devices 或 iTunes 中启用“通过 Wi-Fi 与此 iPhone 同步”，保持手机解锁并与电脑连接同一局域网后再试。 | 找不到此裝置的 Apple 網絡配對工作階段。請先用數據線連接一次，在 Apple Devices 或 iTunes 中啟用「透過 Wi-Fi 與此 iPhone 同步」，保持手機解鎖並與電腦連接同一區域網絡後再試。 | 找不到此裝置的 Apple 網路配對工作階段。請先用傳輸線連線一次，在 Apple Devices 或 iTunes 中啟用「透過 Wi-Fi 與此 iPhone 同步」，保持手機解鎖並與電腦連線同一區域網路後再試。 | The Apple network-pairing session for this device was not found. Connect by cable once and enable “Sync with this iPhone over Wi-Fi” in Apple Devices or iTunes. Keep the phone unlocked and on the same local network as the computer, then try again. | — | ❌ 含义不一致 → ✅ 已修复 | 补齐英文缺少的保持解锁、与电脑位于同一局域网条件 |
| UsbControlFailureGateUnavailable | 设备未确认 CoreDevice 触控认证已开启。为避免输入被系统静默丢弃，反向控制未启动。 | 裝置未確認 CoreDevice 觸控驗證已開啟。為避免輸入被系統靜默丟棄，反向控制未啟動。 | 裝置未確認 CoreDevice 觸控驗證已開啟。為避免輸入被系統靜默丟棄，反向控制未啟動。 | The device did not confirm that CoreDevice touch authentication is enabled. Reverse control was not started to prevent silent input loss. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| UsbControlFailureImageDownload | 开发者镜像下载失败或超时。请检查 GitHub 网络连接后重试，也可通过 IPHONE_MIRROR_DDI_DIR 提供官方本地镜像。 | 開發者映像檔下載失敗或逾時。請檢查 GitHub 網絡連線後重試，也可透過 IPHONE_MIRROR_DDI_DIR 提供官方本機映像檔。 | 開發者映像檔下載失敗或逾時。請檢查 GitHub 網路連線後重試，也可透過 IPHONE_MIRROR_DDI_DIR 提供官方本機映像檔。 | The developer image download failed or timed out. Check the GitHub network connection, or provide an official local image through IPHONE_MIRROR_DDI_DIR. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageIncompatible | 当前桥接器没有与此运行时匹配的开发者镜像下载元数据。请更新 iPhoneMirror，或通过 IPHONE_MIRROR_DDI_DIR 提供官方本地镜像。 | 目前橋接器沒有與此執行階段匹配的開發者映像檔下載中繼資料。請更新 iPhoneMirror，或透過 IPHONE_MIRROR_DDI_DIR 提供官方本機映像檔。 | 目前橋接器沒有與此執行階段相符的開發者映像檔下載中繼資料。請更新 iPhoneMirror，或透過 IPHONE_MIRROR_DDI_DIR 提供官方本機映像檔。 | No developer-image download metadata matches this runtime. Update iPhoneMirror or provide an official local image through IPHONE_MIRROR_DDI_DIR. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageIntegrity | 开发者镜像校验失败，下载文件可能不完整或已损坏。请检查网络或更换镜像源后重试。 | 開發者映像檔驗證失敗，下載檔案可能不完整或已損壞。請檢查網絡或更換來源後重試。 | 開發者映像檔驗證失敗，下載檔案可能不完整或已損壞。請檢查網路或更換來源後重試。 | Developer image verification failed. The download may be incomplete or damaged. Check the network or use another image source, then retry. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageMount | 本地开发者镜像无效或无法挂载。请确认镜像来自官方 Xcode、与设备系统兼容，并包含 Image.dmg、BuildManifest.plist 和 Image.trustcache。 | 本機開發者映像檔無效或無法掛載。請確認映像檔來自官方 Xcode、與裝置系統相容，並包含 Image.dmg、BuildManifest.plist 和 Image.trustcache。 | 本機開發者映像檔無效或無法掛載。請確認映像檔來自官方 Xcode、與裝置系統相容，並包含 Image.dmg、BuildManifest.plist 和 Image.trustcache。 | The local developer image is invalid or could not be mounted. Confirm it came from official Xcode, matches the device system, and contains Image.dmg, BuildManifest.plist, and Image.trustcache. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageRateLimited | 开发者镜像下载未通过 GitHub 内容校验或当前 API 被限流。请稍后重试。 | 開發者映像檔下載未通過 GitHub 內容驗證或目前 API 被限流。請稍後重試。 | 開發者映像檔下載未通過 GitHub 內容驗證或目前 API 被限流。請稍後重試。 | The developer image download failed GitHub validation or the API is rate-limited. Try again later. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageRemount | 无法刷新旧的 Personalized Developer Disk Image。请关闭可能占用设备的 Xcode/开发工具，重启 iPhone 后再试。 | 無法重新整理舊的 Personalized Developer Disk Image。請關閉可能佔用裝置的 Xcode/開發工具，重新啟動 iPhone 後再試。 | 無法重新整理舊的 Personalized Developer Disk Image。請關閉可能佔用裝置的 Xcode/開發工具，重新啟動 iPhone 後再試。 | The old Personalized Developer Disk Image could not be refreshed. Close Xcode or other tools using the device, restart the iPhone, and try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageRequired | 设备尚未挂载 Personalized Developer Disk Image。请使用与设备系统匹配的官方 Xcode 开发者镜像完成挂载后重试。 | 裝置尚未掛載 Personalized Developer Disk Image。請使用與裝置系統匹配的官方 Xcode 開發者映像檔完成掛載後重試。 | 裝置尚未掛載 Personalized Developer Disk Image。請使用與裝置系統相符的官方 Xcode 開發者映像檔完成掛載後重試。 | The device has not mounted the Personalized Developer Disk Image. Mount the official Xcode image matching the device system, then try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureImageTss | 开发者镜像已下载，但 Apple 个性化服务或设备挂载失败。请检查 Apple 服务网络、保持设备解锁，然后重试。 | 開發者映像檔已下載，但 Apple 個人化服務或裝置掛載失敗。請檢查 Apple 服務網絡、保持裝置解鎖，然後重試。 | 開發者映像檔已下載，但 Apple 個人化服務或裝置掛載失敗。請檢查 Apple 服務網路、保持裝置解鎖，然後重試。 | The developer image was downloaded, but Apple personalization or device mounting failed. Check the Apple-service network, keep the device unlocked, and try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureMux | Apple USB 服务在设备握手时中断了连接。请保持设备解锁并确认已信任此电脑；若仍失败，请重新插拔数据线后重试。 | Apple USB 服務在裝置握手時中斷連線。請保持裝置解鎖並確認已信任此電腦；若仍失敗，請重新插拔數據線後重試。 | Apple USB 服務在裝置握手時中斷連線。請保持裝置解鎖並確認已信任此電腦；若仍失敗，請重新插拔傳輸線後重試。 | The Apple USB service disconnected during the device handshake. Keep the device unlocked and trusted, reconnect the cable, and try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureNoDetails | USB 触控桥接器未提供错误详情。请确认设备已通过 USB 连接并已信任此电脑。 | USB 觸控橋接器未提供錯誤詳細資料。請確認裝置已透過 USB 連接並已信任此電腦。 | USB 觸控橋接器未提供錯誤詳細資料。請確認裝置已透過 USB 連線並已信任此電腦。 | The USB touch bridge provided no error details. Confirm the device is connected by USB and trusted. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureNotTrusted | 此 iPhone 尚未信任当前 Windows 帐户。请保持手机解锁，重新插拔数据线并在手机上点按“信任”。 | 此 iPhone 尚未信任目前 Windows 帳戶。請保持手機解鎖，重新插拔數據線並在手機上點按「信任」。 | 此 iPhone 尚未信任目前 Windows 帳戶。請保持手機解鎖，重新插拔傳輸線並在手機上點一下「信任」。 | This iPhone has not trusted the current Windows account. Keep the phone unlocked, reconnect the cable, and tap “Trust” on the phone. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureTouchService | 设备暂未提供触控反向控制服务。请保持设备解锁并信任此电脑后重试；若仍失败，请改用蓝牙反向控制或无线反向控制。 | 裝置暫未提供觸控反向控制服務。請保持裝置解鎖並信任此電腦後重試；若仍失敗，請改用藍牙反向控制或無線反向控制。 | 裝置暫未提供觸控反向控制服務。請保持裝置解鎖並信任此電腦後重試；若仍失敗，請改用藍牙反向控制或無線反向控制。 | The device has not provided the touch-control service. Keep it unlocked and trusted, then try again; otherwise use Bluetooth or wireless control. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| UsbControlFailureTouchSurface | 已建立 CoreDevice 会话，但开发者镜像没有发布 mainTouchscreen（257）触控面。请重启 iPhone 后重试。 | 已建立 CoreDevice 工作階段，但開發者映像檔沒有發佈 mainTouchscreen（257）觸控面。請重新啟動 iPhone 後重試。 | 已建立 CoreDevice 工作階段，但開發者映像檔沒有發布 mainTouchscreen（257）觸控面。請重新啟動 iPhone 後重試。 | A CoreDevice session was established, but the developer image did not publish the mainTouchscreen (257) touch surface. Restart the iPhone and try again. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureUnknown | USB 反向控制桥接器返回了无法识别的错误。请查看诊断日志后重试。 | USB 反向控制橋接器返回了無法識別的錯誤。請查看診斷記錄後重試。 | USB 反向控制橋接器傳回了無法識別的錯誤。請查看診斷記錄檔後重試。 | The USB reverse-control bridge returned an unrecognized error. Check the diagnostic log and try again. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称 |
| UsbControlFailureUnsupportedIos | 设备拒绝了媒体流认证（9021），且没有可用的直接 Universal HID 触控通道。请重启 iPhone 后重试；若仍失败，请使用蓝牙反向控制。 | 裝置拒絕了媒體串流驗證（9021），且沒有可用的直接 Universal HID 觸控通道。請重新啟動 iPhone 後重試；若仍失敗，請使用藍牙反向控制。 | 裝置拒絕了媒體串流驗證（9021），且沒有可用的直接 Universal HID 觸控通道。請重新啟動 iPhone 後重試；若仍失敗，請使用藍牙反向控制。 | The device rejected media-stream authentication (9021), and no direct Universal HID touch channel is available. Restart the iPhone and try again; if it still fails, use Bluetooth control. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称 |
| UsbControlFailureWiredDeviceNotFound | 未能在 USB 连接中找到这台设备的控制通道。投屏运行时系统可能暂时无法访问设备：请先停止投屏再启动反向控制，或重新插拔数据线、解锁手机并确认已信任此电脑后重试。 | 未能在 USB 連線中找到此裝置的控制通道。螢幕鏡像運行時系統可能暫時無法存取裝置：請先停止螢幕鏡像再啟動反向控制，或重新插拔數據線、解鎖手機並確認已信任此電腦後重試。 | 未能在 USB 連線中找到此裝置的控制通道。螢幕鏡像執行時系統可能暫時無法存取裝置：請先停止螢幕鏡像再啟動反向控制，或重新插拔傳輸線、解鎖手機並確認已信任此電腦後重試。 | The control channel for this device was not found over USB. While mirroring, the system may temporarily lose access to the device: stop the mirror before starting reverse control, or replug the cable, unlock the iPhone, confirm it trusts this PC, and retry. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| UsbControlFailureWirelessNotDiscoverable | 未发现 iPhone 的无线 CoreDevice 服务。请保持 iPhone 解锁、与电脑处于同一局域网，并允许 Windows 防火墙通过本地网络发现。 | 未找到 iPhone 的無線 CoreDevice 服務。請保持 iPhone 解鎖、與電腦處於同一區域網絡，並允許 Windows 防火牆透過本機網絡尋找。 | 未找到 iPhone 的無線 CoreDevice 服務。請保持 iPhone 解鎖、與電腦處於同一區域網路，並允許 Windows 防火牆透過本機網路尋找。 | The iPhone wireless CoreDevice service was not found. Keep the iPhone unlocked on the same local network and allow Windows firewall discovery. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureWirelessPairingFailed | 无线 CoreDevice 配对或隧道建立失败。请先用 USB 连接一次并完成初始化，随后确认 iPhone 已解锁且局域网未隔离。 | 無線 CoreDevice 配對或通道建立失敗。請先用 USB 連接一次並完成初始化，然後確認 iPhone 已解鎖且區域網絡未隔離用戶端。 | 無線 CoreDevice 配對或通道建立失敗。請先用 USB 連線一次並完成初始化，然後確認 iPhone 已解鎖且區域網路未隔離用戶端。 | Wireless CoreDevice pairing or tunnel setup failed. Connect by USB once and complete initialization, then confirm the iPhone is unlocked and the network does not isolate clients. | — | ✅ 完全一致 | 保留 |
| UsbControlFailureWirelessPairingRequired | 当前 Windows 帐户尚未完成此设备的无线 CoreDevice 配对。请先通过 USB 连接并解锁 iPhone，完成一次 USB 反向控制初始化后再试无线反向控制。 | 目前 Windows 帳戶尚未完成此裝置的無線 CoreDevice 配對。請先透過 USB 連接並解鎖 iPhone，完成一次 USB 反向控制初始化後再試無線反向控制。 | 目前 Windows 帳戶尚未完成此裝置的無線 CoreDevice 配對。請先透過 USB 連線並解鎖 iPhone，完成一次 USB 反向控制初始化後再試無線反向控制。 | This Windows account has not completed wireless CoreDevice pairing for this device. Connect by USB and unlock the iPhone, complete USB reverse-control initialization once, then try wireless control again. | — | ❌ 术语不一致 → ✅ 已修复 | 统一反向控制正式名称；统一香港繁体中文既有用语 |
| UsbControlNeedsConnection | 需要 USB 连接 | 需要 USB 連接 | 需要 USB 連線 | USB connection required | — | ✅ 完全一致 | 保留 |
| UsbControlOff | 有线控制未启用 | 有線控制未啟用 | 有線控制未啟用 | Wired control is off | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbControlOperationFailedFormat | 有线控制操作失败：{0} | 有線控制操作失敗：{0} | 有線控制操作失敗：{0} | Wired control operation failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbControlPrerequisite | 请通过 USB 连接 iPhone 或 iPad，并在设备上选择“信任此电脑”，然后启用有线控制。 | 請透過 USB 連接 iPhone 或 iPad，並在裝置上選擇「信任此電腦」，然後啟用有線控制。 | 請透過 USB 連線 iPhone 或 iPad，並在裝置上選擇「信任此電腦」，然後啟用有線控制。 | Connect an iPhone or iPad by USB and trust this computer before enabling wired control. | — | ❌ 含义不一致 → ✅ 已修复 | 信任对象是电脑，不是 iPhone；同时统一有线控制名称 |
| UsbControlRetry | 重试有线控制 | 重試有線控制 | 重試有線控制 | Retry wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbControlStarting | 正在连接反向控制 | 正在連接反向控制 | 正在連線反向控制 | Connecting reverse control | — | ✅ 完全一致 | 保留 |
| UsbControlStopping | 正在关闭有线控制 | 正在關閉有線控制 | 正在關閉有線控制 | Stopping wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbControlTitle | 有线控制 | 有線控制 | 有線控制 | Wired control | — | ❌ 术语不一致 → ✅ 已修复 | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 |
| UsbModeAdvantageTitle | 优点 | 優點 | 優點 | Advantages | — | ✅ 完全一致 | 保留 |
| UsbModeAirPlayAdvantage | 发送 Valeria=false，使用设备原生尺寸，并保留当前的首帧探测、横竖屏识别和自适应重配。视频 App 可以切换到 AirPlay 或外接播放。 | 發送 Valeria=false，使用裝置原始尺寸，並保留現有的首幀偵測、橫直螢幕辨識和自動調整。影片 App 可以切換到 AirPlay 或外接播放。 | 傳送 Valeria=false，使用裝置原始尺寸，並保留現有的首影格偵測、橫直螢幕辨識和自動調整。影片 App 可以切換到 AirPlay 或外接播放。 | Sends Valeria=false, uses the device's native size, and retains first-frame probing, orientation detection, and adaptive reconfiguration. Video apps can switch to AirPlay or external playback. | — | ✅ 完全一致 | 保留 |
| UsbModeAirPlayDisadvantage | 外接内容的分辨率无法保证与手机和窗口完美自适应，可能出现裁切、比例异常或画面显示不全。 | 外接內容的解像度無法保證與手機和視窗完美自適應，可能出現裁切、比例異常或畫面顯示不全。 | 外接內容的解析度無法保證與手機和視窗完美自適應，可能出現裁切、比例異常或畫面顯示不全。 | External-content resolution cannot be guaranteed to adapt perfectly to both the phone and the window. Content may be cropped, use the wrong aspect ratio, or be partly off-screen. | — | ✅ 完全一致 | 保留 |
| UsbModeAirPlayLabel | B AirPlay（实验） | B AirPlay（實驗） | B AirPlay（實驗） | B AirPlay (experimental) | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| UsbModeAirPlayNotice | 需要自定义时，连续点击右下角版本号 5 次开启高级模式。自定义 HPD1 尺寸存在风险，错误参数可能导致黑屏、裁切或 USB 重连失败。 | 需要自訂時，連續按右下角版本號 5 次開啟進階模式。自訂 HPD1 尺寸存在風險，錯誤參數可能導致黑畫面、裁切或 USB 重新連接失敗。 | 需要自訂時，連續按右下角版本號 5 次開啟進階模式。自訂 HPD1 尺寸存在風險，錯誤參數可能導致黑畫面、裁切或 USB 重新連線失敗。 | For custom sizing, click the version in the lower-right corner five times to enable Advanced mode. Custom HPD1 dimensions are risky and can cause a black screen, cropping, or failed USB reconnection. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| UsbModeAisiAdvantage | 采用爱思同类方案，发送 Valeria=false 并固定请求 1565×1565，减少不同设备之间的分辨率协商差异。 | 採用愛思同類方案，發送 Valeria=false 並固定請求 1565×1565，減少不同裝置之間的解像度協商差異。 | 採用愛思同類方案，傳送 Valeria=false 並固定請求 1565×1565，減少不同裝置之間的解析度協商差異。 | Uses an Aisi-style configuration with Valeria=false and a fixed 1565×1565 request, reducing display-negotiation differences between devices. | — | ✅ 完全一致 | 保留 |
| UsbModeAisiDisadvantage | 方形目标会限制源画面的清晰度，不适合画质优先；横屏内容还可能出现缩放、黑边或比例不理想。 | 方形目標會限制原始畫面的畫質，不適合畫質優先；橫向螢幕內容還可能出現縮放、黑邊或比例不理想。 | 方形目標會限制原始畫面的畫質，不適合畫質優先；橫向螢幕內容還可能出現縮放、黑邊或比例不理想。 | The square target limits source clarity and is unsuitable when image quality is the priority. Landscape content may also be scaled, letterboxed, or shown at an imperfect ratio. | — | ✅ 完全一致 | 保留 |
| UsbModeAisiLabel | C 爱思模式 | C 愛思模式 | C 愛思模式 | C Aisi mode | — | ✅ 完全一致 | 保留 |
| UsbModeAisiNotice | 此模式不会使用本项目的原生分辨率探测和横竖屏自适应重配。 | 此模式不會使用本項目的原始解像度偵測和橫直螢幕自動調整。 | 此模式不會使用本專案的原始解析度偵測和橫直向自動調整。 | This mode does not use this application's native-resolution probing or adaptive orientation reconfiguration. | — | ✅ 完全一致 | 保留 |
| UsbModeDemoAdvantage | 发送 Valeria=true 和设备原生 DisplaySize，由 iOS 输出完整的手机镜像，并保持固定演示状态。兼容性和画面完整性最高，推荐日常使用。 | 發送 Valeria=true 和裝置原始 DisplaySize，由 iOS 輸出完整的手機鏡像，並保持固定示範狀態。相容性和畫面完整性最高，推薦日常使用。 | 傳送 Valeria=true 和裝置原始 DisplaySize，由 iOS 輸出完整的手機鏡像，並保持固定示範狀態。相容性和畫面完整性最高，推薦日常使用。 | Sends Valeria=true with the device's native DisplaySize, letting iOS provide a complete phone mirror while remaining in demonstration state. It offers the best compatibility and framing and is recommended for everyday use. | — | ✅ 完全一致 | 保留 |
| UsbModeDemoDisadvantage | iOS 会进入 Apple 演示状态，状态栏日期、时间和电量会锁定为演示值，不能反映手机当前状态。 | iOS 會進入 Apple 示範狀態，狀態列日期、時間和電量會鎖定為示範值，不能反映手機目前狀態。 | iOS 會進入 Apple 示範狀態，狀態列日期、時間和電量會鎖定為示範值，不能反映手機目前狀態。 | iOS enters Apple's demonstration state. The status-bar date, time, and battery are locked to demonstration values instead of the phone's current state. | — | ✅ 完全一致 | 保留 |
| UsbModeDemoLabel | A 演示模式（推荐） | A 示範模式（推薦） | A 示範模式（推薦） | A Demo (recommended) | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| UsbModeDemoNotice | 此模式不会启用视频 App 的 AirPlay 或外接播放画面。 | 此模式不會啟用影片 App 的 AirPlay 或外接播放畫面。 | 此模式不會啟用影片 App 的 AirPlay 或外接播放畫面。 | This mode does not enable AirPlay or external playback from video apps. | — | ✅ 完全一致 | 保留 |
| UsbModeDetailsTitle | 有线投屏模式说明 | 有線螢幕鏡像模式說明 | 有線螢幕鏡像模式說明 | USB mirroring mode details | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| UsbModeDetailsToolTip | 查看该模式的优缺点 | 查看該模式的優缺點 | 查看該模式的優缺點 | View this mode's advantages and disadvantages | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| UsbModeDisadvantageTitle | 缺点 | 缺點 | 缺點 | Disadvantages | — | ✅ 完全一致 | 保留 |
| UsbProjectionModeAppliedFormat | 已应用：{0} | 已套用：{0} | 已套用：{0} | Applied: {0} | — | ✅ 完全一致 | 保留 |
| UsbProjectionModeRestarting | 正在按新模式重新连接当前有线设备… | 正在按新模式重新連接目前有線裝置… | 正在按新模式重新連線目前有線裝置… | Reconnecting the current USB device with the new mode… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| UsbProjectionModeSelectedFormat | 已选择：{0} | 已選擇：{0} | 已選擇：{0} | Selected: {0} | — | ✅ 完全一致 | 保留 |
| UsbProjectionModeTitle | 有线投屏协议 | 有線螢幕鏡像協議 | 有線螢幕鏡像協定 | USB mirroring protocol | — | ❌ 术语不一致 → ✅ 已修复 | 投屏统一为 mirroring，视频应用投放仍使用 casting |
| UsbProjectionPerDeviceHint | 仅应用于当前选中的有线设备。 | 只套用至目前選取的有線裝置。 | 只套用至目前選取的有線裝置。 | Applies only to the currently selected USB device. | — | ✅ 完全一致 | 保留 |
| UxPlayComponentUnavailable | 此构建缺少 UxPlay 下载信息，请使用正式发布的完整应用包。 | 此版本缺少 UxPlay 下載資訊，請使用正式發佈的完整應用程式套件。 | 此版本缺少 UxPlay 下載資訊，請使用正式發布的完整應用程式套件。 | This build has no UxPlay download metadata. Please use a complete release build. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadCancel | 取消 | 取消 | 取消 | Cancel | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadCancelling | 正在取消下载… | 正在取消下載… | 正在取消下載… | Cancelling download… | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadDescription | 首次使用需要下载。自动选择高速镜像，安装后无需重复下载。 | 首次使用需要下載。自動選擇高速鏡像，安裝後無需重複下載。 | 首次使用需要下載。自動選擇高速鏡像，安裝後無需重複下載。 | A download is needed on first use. The fastest mirror is selected automatically and the component is saved for reuse. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadFailed | 组件下载或安装失败，可重试；当前无线方案仍可使用。 | 元件下載或安裝失敗，可重試；目前無線方案仍可使用。 | 元件下載或安裝失敗，可重試；目前無線方案仍可使用。 | Download or installation failed. Retry when ready; your current wireless receiver remains available. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadFindingMirror | 正在连接下载镜像… | 正在連接下載鏡像… | 正在連線下載鏡像… | Connecting to download mirrors… | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadInstalling | 正在校验并安装组件… | 正在驗證並安裝元件… | 正在驗證並安裝元件… | Verifying and installing the component… | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadIntegrityFailed | 组件完整性校验失败，未安装。请重新下载。 | 元件完整性驗證失敗，未安裝。請重新下載。 | 元件完整性驗證失敗，未安裝。請重新下載。 | Component integrity verification failed. Nothing was installed. Please download it again. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadNotFound | 此版本的组件下载地址不可用（404）。请确认对应版本的组件已发布，或稍后重试。 | 此版本的元件下載地址不可用（404）。請確認對應版本的元件已發佈，或稍後重試。 | 此版本的元件下載位址不可用（404）。請確認對應版本的元件已發布，或稍後重試。 | The component for this version is unavailable (404). Check that the matching component has been published, or retry later. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadRetry | 重试 | 重試 | 重試 | Retry | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadStorageFailed | 无法写入组件缓存。请检查剩余磁盘空间和文件访问权限后重试。 | 無法寫入元件快取。請檢查剩餘磁碟空間及檔案存取權限後重試。 | 無法寫入元件快取。請檢查剩餘磁碟空間及檔案存取權限後重試。 | Cannot write to the component cache. Check free disk space and file permissions, then retry. | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadTestingMirror | 正在测速并选择最快的下载线路… | 正在測速並選擇最快的下載線路… | 正在測速並選擇最快的下載線路… | Testing download speeds… | — | ✅ 完全一致 | 保留 |
| UxPlayDownloadTitle | 下载 UxPlay 无线组件 | 下載 UxPlay 無線元件 | 下載 UxPlay 無線元件 | Download UxPlay wireless component | — | ✅ 完全一致 | 保留 |
| VerifyingDownload | 正在校验下载文件… | 正在驗證下載檔案… | 正在驗證下載檔案… | Verifying the download… | — | ✅ 完全一致 | 保留 |
| VideoPipelineAppliedFormat | 已应用视频管线：{0} | 已套用影片管線：{0} | 已套用影片管線：{0} | Video pipeline applied: {0} | — | ✅ 完全一致 | 保留 |
| VideoPipelinePreferenceHelp | 软件兼容会关闭视频解码硬件加速，但 D3D 预览仍会使用 GPU。解码器切换通常在下一关键帧生效。 | 軟件相容會關閉影片解碼硬件加速，但 D3D 預覽仍會使用 GPU。解碼器切換通常在下一關鍵幀生效。 | 軟體相容會關閉影片解碼硬體加速，但 D3D 預覽仍會使用 GPU。解碼器切換通常在下一關鍵影格生效。 | Software compatibility disables hardware video decoding, but the D3D preview still uses the GPU. Decoder changes normally apply at the next keyframe. | — | ✅ 完全一致 | 保留 |
| VideoPipelineRestarting | 正在按新的视频管线设置重新连接当前设备… | 正在按新的影片管線設定重新連接目前裝置… | 正在按新的影片管線設定重新連線目前裝置… | Reconnecting the current device with the new video pipeline… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| VideoPreferencesApplied | 本地渲染参数已应用（来源码流未重连） | 本機繪製參數已套用（來源串流未重新連接） | 本機繪製參數已套用（來源串流未重新連線） | Local rendering settings applied (source stream was not reconnected) | — | ❌ 含义不一致 → ✅ 已修复 | 未重连与码流未改变不是同一含义 |
| VideoPreferencesUnsupported | 当前核心版本尚不支持本地渲染尺寸/帧率调整 | 現有核心版本尚不支援本機繪製尺寸/幀率調整 | 現有核心版本尚不支援本機繪製尺寸/影格率調整 | This core version does not support local render size or frame-rate control | — | ✅ 完全一致 | 保留 |
| VideoPreferencesUpdateFailed | 无法更新本地渲染参数 | 無法更新本機繪製參數 | 無法更新本機繪製參數 | Could not update local rendering settings | — | ✅ 完全一致 | 保留 |
| VideoSettingsAppliedFormat | 设置已提交：{0} · {1} fps · {2} | 設定已提交：{0} · {1} fps · {2} | 設定已提交：{0} · {1} fps · {2} | Settings submitted: {0} · {1} fps · {2} | — | ✅ 完全一致 | 保留 |
| VideoSettingsReconnectBodyFormat | 无法在当前连接中应用画面设置：{0}<br><br>解码器：{1}<br><br>是否停止当前投屏并重新连接该设备？ | 無法在現有連接中套用畫面設定：{0}<br><br>解碼器：{1}<br><br>是否停止現有螢幕鏡像並重新連接這部裝置？ | 無法在現有連線中套用畫面設定：{0}<br><br>解碼器：{1}<br><br>是否停止現有螢幕鏡像並重新連線這台裝置？ | The video settings could not be applied to the current connection: {0}<br><br>Decoder: {1}<br><br>Stop the current cast and reconnect this device? | — | ✅ 完全一致 | 保留 |
| VideoSettingsReconnectCancelled | 已取消重新连接；所选设置尚未全部应用。 | 已取消重新連接；所選設定尚未全部套用。 | 已取消重新連線；所選設定尚未全部套用。 | Reconnect cancelled; the selected settings have not all been applied. | — | ✅ 完全一致 | 保留 |
| VideoSettingsReconnectTitle | 需要重新连接设备 | 需要重新連接裝置 | 需要重新連線裝置 | Reconnect the device? | — | ✅ 完全一致 | 保留 |
| VideoSettingsSavedFormat | 已保存，将在下次开始投屏时应用：{0} · {1} fps · {2} | 已儲存，將在下次開始螢幕鏡像時套用：{0} · {1} fps · {2} | 已儲存，將在下次開始螢幕鏡像時套用：{0} · {1} fps · {2} | Saved for the next cast: {0} · {1} fps · {2} | — | ✅ 完全一致 | 保留 |
| VirtualCameraAlreadyRunning | 虚拟摄像头已在运行。 | 虛擬攝影機已在運作。 | 虛擬攝影機已在運作。 | The virtual camera is already running. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| VirtualCameraBackendMissing | 未安装兼容的 Windows 虚拟摄像头媒体源。按钮将保持禁用，避免误报不存在的摄像头设备。 | 未安裝相容的 Windows 虛擬攝影機媒體源。按鈕將保持停用，避免誤報不存在的攝影機裝置。 | 未安裝相容的 Windows 虛擬攝影機媒體源。按鈕將保持停用，避免誤報不存在的攝影機裝置。 | No compatible Windows virtual-camera media source is installed. This button stays disabled instead of reporting a camera that does not exist. | — | ✅ 完全一致 | 保留 |
| VirtualCameraChecking | 正在检测虚拟摄像头能力… | 正在偵測虛擬攝影機能力… | 正在偵測虛擬攝影機能力… | Checking virtual camera support… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| VirtualCameraDescription | 将当前投屏画面注册为会议和录制软件可选择的摄像头设备。 | 將目前螢幕鏡像畫面註冊為會議和錄製軟件可選擇的攝影機裝置。 | 將目前螢幕鏡像畫面註冊為會議和錄製軟體可選擇的攝影機裝置。 | Expose the selected mirror as a camera device that conferencing and recording applications can select. | — | ✅ 完全一致 | 保留 |
| VirtualCameraInstallCancelled | 已取消安装虚拟摄像头。 | 已取消安裝虛擬攝影機。 | 已取消安裝虛擬攝影機。 | Virtual camera installation was cancelled. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| VirtualCameraInstallFailedFormat | 无法安装虚拟摄像头组件：{0} | 無法安裝虛擬攝影機元件：{0} | 無法安裝虛擬攝影機元件：{0} | Could not install the virtual camera component: {0} | — | ✅ 完全一致 | 保留 |
| VirtualCameraInstallNotDetected | 安装程序已结束，但 Windows 未检测到虚拟摄像头组件。 | 安裝程式已結束，但 Windows 未偵測到虛擬攝影機元件。 | 安裝程式已結束，但 Windows 未偵測到虛擬攝影機元件。 | The installer finished, but Windows did not detect the virtual camera component. | — | ✅ 完全一致 | 保留 |
| VirtualCameraInstallRequired | 需要先安装虚拟摄像头组件。安装过程只会请求一次管理员权限，之后普通用户即可启动。 | 需要先安裝虛擬攝影機元件。安裝過程只會請求一次管理員權限，之後一般使用者即可啟動。 | 需要先安裝虛擬攝影機元件。安裝過程只會請求一次管理員權限，之後一般使用者即可啟動。 | Install the virtual camera component first. Administrator approval is needed once; normal users can start it afterward. | — | ✅ 完全一致 | 保留 |
| VirtualCameraInstalled | 虚拟摄像头组件安装完成。 | 虛擬攝影機元件安裝完成。 | 虛擬攝影機元件安裝完成。 | The virtual camera component was installed. | — | ✅ 完全一致 | 保留 |
| VirtualCameraInstallerExitedFormat | 虚拟摄像头安装程序已退出，代码为 {0}。 | 虛擬攝影機安裝程式已結束，代碼為 {0}。 | 虛擬攝影機安裝程式已結束，代碼為 {0}。 | The virtual camera installer exited with code {0}. | — | ❌ 缺失 → ✅ 已修复 | 输出错误详情迁入三语资源 |
| VirtualCameraInstallerStartFailed | 无法启动虚拟摄像头安装程序。 | 無法啟動虛擬攝影機安裝程式。 | 無法啟動虛擬攝影機安裝程式。 | The virtual camera installer could not be started. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| VirtualCameraInstalling | 正在安装虚拟摄像头组件… | 正在安裝虛擬攝影機元件… | 正在安裝虛擬攝影機元件… | Installing the virtual camera component… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| VirtualCameraPayloadChangedFormat | 虚拟摄像头安装文件意外发生变化：{0} | 虛擬攝影機安裝檔案意外發生變更：{0} | 虛擬攝影機安裝檔案意外發生變更：{0} | The staged virtual camera installation file changed unexpectedly: {0} | — | ❌ 缺失 → ✅ 已修复 | 输出错误详情迁入三语资源 |
| VirtualCameraPayloadMissing | 缺少内置虚拟摄像头安装文件。 | 缺少內置虛擬攝影機安裝檔案。 | 缺少內建虛擬攝影機安裝檔案。 | The embedded virtual camera installation files are missing. | — | ❌ 缺失 → ✅ 已修复 | 将代码中的用户可见硬编码文字迁入三语资源 |
| VirtualCameraReady | 虚拟摄像头组件已就绪，可将当前投屏画面提供给会议或录制软件。 | 虛擬攝影機元件已就緒，可將目前螢幕鏡像畫面提供給會議或錄製軟件。 | 虛擬攝影機元件已就緒，可將目前螢幕鏡像畫面提供給會議或錄製軟體。 | The virtual camera component is ready to expose the current mirror to conferencing or recording apps. | — | ✅ 完全一致 | 保留 |
| VirtualCameraRunning | 虚拟摄像头正在运行 | 虛擬攝影機正在運作 | 虛擬攝影機正在運作 | Virtual camera is running | — | ✅ 完全一致 | 保留 |
| VirtualCameraStarted | 虚拟摄像头已启动。 | 虛擬攝影機已啟動。 | 虛擬攝影機已啟動。 | Virtual camera started. | — | ✅ 完全一致 | 保留 |
| VirtualCameraTab | 虚拟摄像头 | 虛擬攝影機 | 虛擬攝影機 | Virtual camera | — | ✅ 完全一致 | 保留 |
| VirtualCameraUninstallFailedFormat | 无法卸载虚拟摄像头组件：{0} | 無法解除安裝虛擬攝影機元件：{0} | 無法解除安裝虛擬攝影機元件：{0} | Could not uninstall the virtual camera component: {0} | — | ✅ 完全一致 | 保留 |
| VirtualCameraUninstallStillDetected | 卸载程序已结束，但 Windows 仍检测到虚拟摄像头组件。 | 解除安裝程式已結束，但 Windows 仍偵測到虛擬攝影機元件。 | 解除安裝程式已結束，但 Windows 仍偵測到虛擬攝影機元件。 | The uninstaller finished, but Windows still detects the virtual camera component. | — | ✅ 完全一致 | 保留 |
| VirtualCameraUninstalled | 虚拟摄像头组件已卸载。 | 虛擬攝影機元件已解除安裝。 | 虛擬攝影機元件已解除安裝。 | The virtual camera component was uninstalled. | — | ✅ 完全一致 | 保留 |
| VirtualCameraUninstalling | 正在卸载虚拟摄像头组件… | 正在解除安裝虛擬攝影機元件… | 正在解除安裝虛擬攝影機元件… | Uninstalling the virtual camera component… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| VirtualCameraUnsupported | 当前 Windows 版本不支持软件虚拟摄像头。此功能需要 Windows 11。 | 目前 Windows 版本不支援軟件虛擬攝影機。此功能需要 Windows 11。 | 目前 Windows 版本不支援軟體虛擬攝影機。此功能需要 Windows 11。 | This version of Windows does not support software virtual cameras. Windows 11 is required. | — | ✅ 完全一致 | 保留 |
| VirtualCameraUpdateRequired | 检测到旧版虚拟摄像头组件。请先更新组件，再启动虚拟摄像头。 | 偵測到舊版虛擬攝影機元件。請先更新元件，再啟動虛擬攝影機。 | 偵測到舊版虛擬攝影機元件。請先更新元件，再啟動虛擬攝影機。 | An older virtual camera component is installed. Update it before starting the virtual camera. | — | ✅ 完全一致 | 保留 |
| VirtualCameraUpdated | 虚拟摄像头组件更新完成。 | 虛擬攝影機元件更新完成。 | 虛擬攝影機元件更新完成。 | The virtual camera component was updated. | — | ✅ 完全一致 | 保留 |
| VirtualCameraUpdating | 正在更新虚拟摄像头组件… | 正在更新虛擬攝影機元件… | 正在更新虛擬攝影機元件… | Updating the virtual camera component… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WidthLabel | 宽度 | 寬度 | 寬度 | Width | — | ✅ 完全一致 | 保留 |
| WindowTitle | iPhoneMirror — USB iPhone/iPad 投屏 | iPhoneMirror — USB iPhone/iPad 螢幕鏡像 | iPhoneMirror — USB iPhone/iPad 螢幕鏡像 | iPhoneMirror — USB iPhone/iPad Mirroring | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| WindowTitleConnectivity | iPhoneMirror - iPhone/iPad 投屏 | iPhoneMirror - iPhone/iPad 螢幕鏡像 | iPhoneMirror - iPhone/iPad 螢幕鏡像 | iPhoneMirror - iPhone/iPad Mirroring | — | ✅ 完全一致 | 保留 |
| WiredControlDisable | 关闭有线控制 | 關閉有線控制 | 關閉有線控制 | Disable wired control | — | ✅ 完全一致 | 保留 |
| WiredControlEnable | 开启有线控制 | 啟用有線控制 | 啟用有線控制 | Enable wired control | — | ✅ 完全一致 | 保留 |
| WiredControlLabel | 有线控制 | 有線控制 | 有線控制 | Wired control | — | ✅ 完全一致 | 保留 |
| WirelessBackendAppliedFormat | 当前接收方案：{0} | 目前接收方案：{0} | 目前接收方案：{0} | Receiver implementation in use: {0} | — | ✅ 完全一致 | 保留 |
| WirelessBackendChangeFormat | 接收方案：{0} → {1} | 接收方案：{0} → {1} | 接收方案：{0} → {1} | Receiver implementation: {0} → {1} | — | ✅ 完全一致 | 保留 |
| WirelessBackendLabel | 接收方案 | 接收方案 | 接收方案 | Receiver implementation | — | ✅ 完全一致 | 保留 |
| WirelessBackendOriginal | 原始方案 | 原始方案 | 原始方案 | Original | — | ✅ 完全一致 | 保留 |
| WirelessBackendSettingsSaveFailed | 无法保存所选无线接收方案。 | 無法儲存所選無線接收方案。 | 無法儲存所選無線接收方案。 | The selected wireless receiver implementation could not be saved. | — | ✅ 完全一致 | 保留 |
| WirelessBackendUnavailableFormat | 缺少 {0} 无线接收组件。请重新安装完整的 iPhoneMirror 安装包。 | 缺少 {0} 無線接收元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | 缺少 {0} 無線接收元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | The {0} wireless receiver component is missing. Reinstall the full iPhoneMirror package. | — | ⚠️ 需要优化 → ✅ 已修复 | 面向最终用户提供可执行的重新安装建议 |
| WirelessBackendUxPlay | UxPlay（备用） | UxPlay（備用） | UxPlay（備用） | UxPlay (fallback) | — | ✅ 完全一致 | 保留 |
| WirelessConnected | AirPlay 已连接 | AirPlay 已連接 | AirPlay 已連線 | AirPlay connected | — | ✅ 完全一致 | 保留 |
| WirelessConnecting | AirPlay 设备正在连接… | AirPlay 裝置正在連接… | AirPlay 裝置正在連線… | An AirPlay device is connecting… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WirelessControlDisable | 关闭无线控制 | 關閉無線控制 | 關閉無線控制 | Disable wireless control | — | ✅ 完全一致 | 保留 |
| WirelessControlEnable | 开启无线控制 | 啟用無線控制 | 啟用無線控制 | Enable wireless control | — | ✅ 完全一致 | 保留 |
| WirelessControlLabel | 无线控制 | 無線控制 | 無線控制 | Wireless control | — | ✅ 完全一致 | 保留 |
| WirelessControlOperationFailedFormat | 无线控制操作失败：{0} | 無線控制操作失敗：{0} | 無線控制操作失敗：{0} | Wireless control operation failed: {0} | — | ❌ 术语不一致 → ✅ 已修复 | 状态及操作标签统一为无线控制 |
| WirelessLocalNetwork | 本地网络 | 本機網絡 | 本機網路 | Local network | — | ✅ 完全一致 | 保留 |
| WirelessMusicAudioFormat | AirPlay PCM 音频 | AirPlay PCM 音訊 | AirPlay PCM 音訊 | AirPlay PCM audio | — | ✅ 完全一致 | 保留 |
| WirelessMusicAudioOnly | 仅音频 | 只限音訊 | 只限音訊 | Audio only | — | ✅ 完全一致 | 保留 |
| WirelessMusicNoVideo | 无视频流 | 沒有視訊串流 | 沒有視訊串流 | No video stream | — | ✅ 完全一致 | 保留 |
| WirelessMusicStreaming | 正在通过 AirPlay 播放音乐 | 正在透過 AirPlay 播放音樂 | 正在透過 AirPlay 播放音樂 | Playing music through AirPlay | — | ✅ 完全一致 | 保留 |
| WirelessMusicTitle | AirPlay 音乐 | AirPlay 音樂 | AirPlay 音樂 | AirPlay Music | — | ✅ 完全一致 | 保留 |
| WirelessNameChangeFormat | 接收端名称：{0} → {1} | 接收端名稱：{0} → {1} | 接收端名稱：{0} → {1} | Receiver name: {0} → {1} | — | ✅ 完全一致 | 保留 |
| WirelessOriginalQualityWarningBody | 原画模式在部分设备或网络环境下可能出现画面停帧。建议优先使用 1080p；如仍需原画，请确保局域网稳定，并从 iPhone“控制中心” → “屏幕镜像”连接。 | 原畫模式在部分裝置或網絡環境下可能出現畫面停幀。建議優先使用 1080p；如仍需原畫，請確保區域網絡穩定，並從 iPhone「控制中心」 → 「螢幕鏡像」連接。 | 原始畫質模式在部分裝置或網路環境下可能出現畫面停格。建議優先使用 1080p；如仍需原始畫質，請確保區域網路穩定，並從 iPhone「控制中心」 → 「螢幕鏡像輸出」連線。 | Original quality may freeze on some devices or network conditions. 1080p is recommended. If you still need original quality, use a stable local network and connect from iPhone Control Center → Screen Mirroring. | — | ✅ 完全一致 | 保留 |
| WirelessOriginalQualityWarningTitle | 原画模式提示 | 原畫模式提示 | 原始畫質模式提示 | Original quality notice | — | ✅ 完全一致 | 保留 |
| WirelessProfile1080p | 1080p（推荐 · 60 fps） | 1080p（推薦 · 60 fps） | 1080p（推薦 · 60 fps） | 1080p (recommended · 60 fps) | — | ✅ 完全一致 | 保留 |
| WirelessProfile540p | 540p（弱网 · 30 fps） | 540p（弱網 · 30 fps） | 540p（低速網路 · 30 fps） | 540p (weak network · 30 fps) | — | ✅ 完全一致 | 保留 |
| WirelessProfile720p | 720p（流畅 · 30 fps） | 720p（流暢 · 30 fps） | 720p（流暢 · 30 fps） | 720p (smooth · 30 fps) | — | ✅ 完全一致 | 保留 |
| WirelessProfileAppliedFormat | 当前声明：{0} | 現有宣告：{0} | 現有宣告：{0} | Advertised now: {0} | — | ✅ 完全一致 | 保留 |
| WirelessProfileDisconnectConfirmFormat | 当前有 {0} 台无线设备已连接。应用“{1}”会断开全部无线投屏并重启接收端。是否继续？ | 現有 {0} 部無線裝置已連接。套用「{1}」會中斷所有無線螢幕鏡像連接，並重新啟動接收端。是否繼續？ | 現有 {0} 台無線裝置已連線。套用「{1}」會中斷所有無線螢幕鏡像連線，並重新啟動接收端。是否繼續？ | Connected wireless devices: {0}. Applying “{1}” will disconnect all wireless mirroring and restart the receiver. Continue? | — | ⚠️ 需要优化 → ✅ 已修复 | 用数量标签兼容单复数；静态未发现引用，保留待后续调用核对 |
| WirelessProfileMaximum | 原画（最高 5120×2880 · 60 fps） | 原畫（最高 5120×2880 · 60 fps） | 原始畫質（最高 5120×2880 · 60 fps） | Original quality (up to 5120×2880 · 60 fps) | — | ✅ 完全一致 | 保留 |
| WirelessProfileReconnectInstructionsFormat | 已重新声明“{0}”。请在 iPhone 打开“控制中心” → “屏幕镜像”，重新选择“{1}”。如果列表仍保留旧连接，请先点“停止镜像”，再重新选择接收端。 | 已重新宣告「{0}」。請在 iPhone 開啟「控制中心」 → 「螢幕鏡像」，重新選擇「{1}」。如果裝置清單仍保留舊連接，請先按一下「停止鏡像」，再重新選擇接收端。 | 已重新宣告「{0}」。請在 iPhone 開啟「控制中心」 → 「螢幕鏡像輸出」，重新選擇「{1}」。如果裝置清單仍保留舊連線，請先按一下「停止鏡像輸出」，再重新選擇接收端。 | “{0}” is now advertised. On iPhone, open Control Center → Screen Mirroring and select “{1}” again. If the old connection remains listed, tap Stop Mirroring first, then select the receiver again. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| WirelessReady | AirPlay 接收端已就绪 | AirPlay 接收端已就緒 | AirPlay 接收端已就緒 | AirPlay receiver ready | — | ✅ 完全一致 | 保留 |
| WirelessReceiverMissing | 缺少无线接收组件。请重新安装完整的 iPhoneMirror 安装包。 | 缺少無線接收元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | 缺少無線接收元件。請重新安裝完整的 iPhoneMirror 安裝套件。 | The wireless receiver component is missing. Reinstall the full iPhoneMirror package. | — | ⚠️ 需要优化 → ✅ 已修复 | 面向最终用户提供可执行的重新安装建议 |
| WirelessReceiverNameLabel | 接收端名称 | 接收端名稱 | 接收端名稱 | Receiver name | — | ✅ 完全一致 | 保留 |
| WirelessReceiverTitle | 无线 AirPlay | 無線 AirPlay | 無線 AirPlay | Wireless AirPlay | — | ✅ 完全一致 | 保留 |
| WirelessResolutionChangeFormat | 连接分辨率：{0} → {1} | 連接解像度：{0} → {1} | 連線解析度：{0} → {1} | Connection resolution: {0} → {1} | — | ✅ 完全一致 | 保留 |
| WirelessResolutionHelp | 应用后将重新声明 AirPlay 接收能力；弱网请选择 720p 或 540p。 | 套用後將重新宣告 AirPlay 接收能力；弱網請選擇 720p 或 540p。 | 套用後將重新宣告 AirPlay 接收能力；低速網路請選擇 720p 或 540p。 | Applying re-advertises the AirPlay receiver capability. Use 720p or 540p on a weak network. | — | ✅ 完全一致 | 保留 |
| WirelessResolutionLabel | 连接分辨率 | 連接解像度 | 連線解析度 | Connection resolution | — | ✅ 完全一致 | 保留 |
| WirelessResolutionTitle | 无线投屏清晰度 | 無線螢幕鏡像畫質 | 無線螢幕鏡像畫質 | Wireless mirroring quality | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| WirelessRunningFormat | 无线接收端运行中：{0} | 無線接收端正在運作：{0} | 無線接收端正在運作：{0} | Wireless receiver active: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| WirelessRunningWithBackendFormat | {0} 无线接收端运行中：{1} | {0} 無線接收端正在運作：{1} | {0} 無線接收端正在運作：{1} | {0} wireless receiver active: {1} | — | ✅ 完全一致 | 保留 |
| WirelessRuntimeCodeIntegrityBlocked | Windows 安全策略阻止了无线解码组件。请改用 Setup 安装版；若使用 ZIP，请先在 ZIP 的“属性”中勾选“解除锁定”，然后重新解压。 | Windows 安全策略阻止了無線解碼元件。請改用 Setup 安裝版；若使用 ZIP，請先在 ZIP 的「屬性」中選取「解除鎖定」，然後重新解壓縮。 | Windows 安全策略阻止了無線解碼元件。請改用 Setup 安裝版；若使用 ZIP，請先在 ZIP 的「屬性」中選取「解除鎖定」，然後重新解壓縮。 | Windows security policy blocked the wireless decoder. Use the Setup installer, or open the downloaded ZIP's Properties, select Unblock, and extract it again. | — | ✅ 完全一致 | 保留 |
| WirelessRuntimeIncompatible | 无线解码组件版本不兼容，请重新安装最新版本。 | 無線解碼元件版本不相容，請重新安裝最新版本。 | 無線解碼元件版本不相容，請重新安裝最新版本。 | The wireless decoder version is incompatible. Reinstall the latest version. | — | ✅ 完全一致 | 保留 |
| WirelessRuntimeLoadFailedFormat | 无线解码组件无法加载（检查代码 {0}），请重新安装最新版本。 | 無線解碼元件無法載入（檢查代碼 {0}），請重新安裝最新版本。 | 無線解碼元件無法載入（檢查代碼 {0}），請重新安裝最新版本。 | The wireless decoder could not be loaded (check code {0}). Reinstall the latest version. | — | ✅ 完全一致 | 保留 |
| WirelessRuntimeProbeTimedOut | 无线解码组件检查超时，请重新启动应用后重试。 | 無線解碼元件檢查逾時，請重新啟動應用程式後重試。 | 無線解碼元件檢查逾時，請重新啟動應用程式後重試。 | The wireless decoder check timed out. Restart the application and try again. | — | ✅ 完全一致 | 保留 |
| WirelessServiceAlwaysOn | 自动启动 | 自動啟動 | 自動啟動 | Starts automatically | — | ✅ 完全一致 | 保留 |
| WirelessSettingsConfirmFormat | 本次修改：<br>{0}<br><br>{1}<br><br>应用后请在 iPhone 打开“控制中心” → “屏幕镜像”，重新选择“{2}”。 | 本次修改：<br>{0}<br><br>{1}<br><br>套用後請在 iPhone 開啟「控制中心」 → 「螢幕鏡像」，重新選擇「{2}」。 | 本次修改：<br>{0}<br><br>{1}<br><br>套用後請在 iPhone 開啟「控制中心」 → 「螢幕鏡像輸出」，重新選擇「{2}」。 | Changes:<br>{0}<br><br>{1}<br><br>After applying, open Control Center → Screen Mirroring on iPhone and select “{2}” again. | — | ✅ 完全一致 | 保留 |
| WirelessSettingsConnectedImpactFormat | 当前有 {0} 台无线设备连接，应用后会全部断开。 | 現有 {0} 部無線裝置連接，套用後會全部中斷連接。 | 現有 {0} 台無線裝置連線，套用後會全部中斷連線。 | Connected wireless devices: {0}. Applying changes will disconnect all of them. | — | ⚠️ 需要优化 → ✅ 已修复 | 消除 device(s) 表达并保留应用后断开的条件 |
| WirelessSettingsReadyImpact | 接收端将重启并重新发布 AirPlay 能力。 | 接收端將重新啟動並重新發佈 AirPlay 能力。 | 接收端將重新啟動並重新發布 AirPlay 能力。 | The receiver will restart and re-advertise its AirPlay capabilities. | — | ✅ 完全一致 | 保留 |
| WirelessSettingsTitle | 应用无线 AirPlay 设置 | 套用無線 AirPlay 設定 | 套用無線 AirPlay 設定 | Apply wireless AirPlay settings | — | ✅ 完全一致 | 保留 |
| WirelessSettingsUnchanged | 名称和连接分辨率均未改变。 | 名稱和連接解像度均未改變。 | 名稱和連線解析度均未改變。 | The receiver name and connection resolution have not changed. | — | ✅ 完全一致 | 保留 |
| WirelessStarting | 正在启动 AirPlay 接收端… | 正在啟動 AirPlay 接收端… | 正在啟動 AirPlay 接收端… | Starting the AirPlay receiver… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WirelessStopped | 无线投屏已停止 | 無線螢幕鏡像已停止 | 無線螢幕鏡像已停止 | Wireless mirroring stopped | — | ✅ 完全一致 | 保留 |
| WirelessStopping | 正在停止 AirPlay 接收端… | 正在停止 AirPlay 接收端… | 正在停止 AirPlay 接收端… | Stopping the AirPlay receiver… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WirelessStreaming | AirPlay 无线投屏中 | AirPlay 無線螢幕鏡像中 | AirPlay 無線螢幕鏡像中 | Wireless AirPlay mirroring | — | ✅ 完全一致 | 保留 |
| WirelessUxPlayStartupFailed | UxPlay 启动后立即退出。请修复 UxPlay 运行时后重新应用无线设置。 | UxPlay 啟動後立即結束。請修復 UxPlay 執行階段後重新套用無線設定。 | UxPlay 啟動後立即結束。請修復 UxPlay 執行階段後重新套用無線設定。 | UxPlay stopped immediately after startup. Repair the UxPlay runtime, then apply the wireless settings again. | — | ✅ 完全一致 | 保留 |
| WirelessWaitingDevice | AirPlay 接收端已启动，请在 iPhone 控制中心选择屏幕镜像。 | AirPlay 接收端已啟動，請在 iPhone 控制中心選擇螢幕鏡像。 | AirPlay 接收端已啟動，請在 iPhone 控制中心選擇螢幕鏡像。 | The AirPlay receiver is running. Choose Screen Mirroring in iPhone Control Center. | — | ✅ 完全一致 | 保留 |

### DriverInstaller

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Acknowledge | 知道了 | 明白 | 知道了 | Got it | — | ✅ 完全一致 | 保留 |
| AdvancedSettings | 高级设置 | 進階設定 | 進階設定 | Advanced settings | — | ✅ 完全一致 | 保留 |
| AisiBody | 从爱思官网下载并完成一次正常有线投屏，然后返回重新检测。 | 從愛思官網下載並完成一次正常有線螢幕鏡像，然後返回重新偵測。 | 從愛思官網下載並完成一次正常有線螢幕鏡像，然後返回重新偵測。 | Download Aisi Screen Mirroring from its official website, complete one normal wired mirroring session, then return and recheck. | — | ❌ 含义不一致 → ✅ 已修复 | 补齐官方下载来源 |
| AisiTitle | 使用爱思投屏完成驱动准备 | 使用愛思螢幕鏡像完成驅動程式準備 | 使用愛思螢幕鏡像完成驅動程式準備 | Prepare the driver with Aisi Screen Mirroring | — | ✅ 完全一致 | 保留 |
| AppSubtitle | Apple USB 环境与按设备采集过滤驱动 | Apple USB 環境與按裝置擷取過濾驅動程式 | Apple USB 環境與按裝置擷取篩選驅動程式 | Apple USB environment and per-device capture filter | — | ✅ 完全一致 | 保留 |
| AppTitle | iPhoneMirror 驱动管理器 | iPhoneMirror 驅動程式管理員 | iPhoneMirror 驅動程式管理員 | iPhoneMirror Driver Manager | — | ✅ 完全一致 | 保留 |
| Appearance | 外观 | 外觀 | 外觀 | Appearance | — | ✅ 完全一致 | 保留 |
| AppleCompatibilityDownloadUnavailable | 无法下载缺失的 Apple Mobile Device Service 兼容组件。请检查网络后重试，详细原因已写入日志。 | 無法下載遺失的 Apple Mobile Device Service 相容元件。請檢查網絡後重試，詳細原因已寫入記錄。 | 無法下載遺失的 Apple Mobile Device Service 相容元件。請檢查網路後重試，詳細原因已寫入記錄檔。 | The missing Apple Mobile Device Service compatibility component could not be downloaded. Check the network and retry; details were written to the log. | — | ✅ 完全一致 | 保留 |
| AppleDevices | Apple 设备 | Apple 裝置 | Apple 裝置 | Apple devices | — | ✅ 完全一致 | 保留 |
| AppleDownloadUnavailable | 无法自动获得 Apple 官方安装包，已打开 Microsoft Store 中的 Apple Devices 页面。 | 無法自動取得 Apple 官方安裝套件，已開啟 Microsoft Store 中的 Apple Devices 頁面。 | 無法自動取得 Apple 官方安裝套件，已開啟 Microsoft Store 中的 Apple Devices 頁面。 | The official Apple installer could not be obtained automatically. The Apple Devices page was opened in Microsoft Store. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| AppleInstallerFailed | Apple Mobile Device Support 组件安装失败，退出代码 {0}。<br>{1}<br>日志：{2} | Apple Mobile Device Support 元件安裝失敗，結束代碼 {0}。<br>{1}<br>記錄：{2} | Apple Mobile Device Support 元件安裝失敗，結束代碼 {0}。<br>{1}<br>記錄檔：{2} | The Apple Mobile Device Support component failed with exit code {0}.<br>{1}<br>Log: {2} | — | ✅ 完全一致 | 保留 |
| AppleInstallerStartFailed | 无法启动 Apple 安装程序。 | 無法啟動 Apple 安裝程式。 | 無法啟動 Apple 安裝程式。 | The Apple installer could not start. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| AppleMobileDevice | Apple 移动设备 | Apple 流動裝置 | Apple 行動裝置 | Apple mobile device | — | ✅ 完全一致 | 保留 |
| ApplePackageManagerStartFailed | 无法启动软件包管理器。 | 無法啟動套件管理員。 | 無法啟動套件管理員。 | The package manager could not start. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| AppleRestartRequired | Apple Mobile Device Support 已安装，但 Windows 需要重启才能完成驱动和服务注册。请重启电脑后重新检测。<br>日志：{0} | Apple Mobile Device Support 已安裝，但 Windows 需要重新啟動才能完成驅動程式和服務註冊。請重新啟動電腦後重新偵測。<br>記錄：{0} | Apple Mobile Device Support 已安裝，但 Windows 需要重新啟動才能完成驅動程式和服務註冊。請重新啟動電腦後重新偵測。<br>記錄檔：{0} | Apple Mobile Device Support was installed, but Windows must restart to finish registering the driver and service. Restart the PC, then check again.<br>Log: {0} | — | ✅ 完全一致 | 保留 |
| AppleServiceMissing | Apple USB 驱动已安装，但缺少 Apple Mobile Device Service | Apple USB 驅動程式已安裝，但缺少 Apple Mobile Device Service | Apple USB 驅動程式已安裝，但缺少 Apple Mobile Device Service | The Apple USB driver is installed, but Apple Mobile Device Service is missing | — | ✅ 完全一致 | 保留 |
| AppleServiceNotReady | Apple USB 支持不完整：服务或 Apple USB 驱动包尚未就绪。请连接并解锁 iPhone 后重新检测。<br>日志：{0} | Apple USB 支援不完整：服務或 Apple USB 驅動程式包尚未就緒。請連接並解鎖 iPhone 後重新偵測。<br>記錄：{0} | Apple USB 支援不完整：服務或 Apple USB 驅動程式套件尚未就緒。請連線並解鎖 iPhone 後重新偵測。<br>記錄檔：{0} | Apple USB support is incomplete: the service or Apple USB driver package is not ready. Connect and unlock the iPhone, then check again.<br>Log: {0} | — | ✅ 完全一致 | 保留 |
| AppleServiceRunning | Apple USB 服务正在运行 | Apple USB 服務正在執行 | Apple USB 服務正在執行 | Apple USB service is running | — | ✅ 完全一致 | 保留 |
| AppleServiceStopped | Apple USB 服务已安装但未运行 | Apple USB 服務已安裝但未執行 | Apple USB 服務已安裝但未執行 | Apple USB support is installed but the service is stopped | — | ✅ 完全一致 | 保留 |
| AppleSignatureInvalid | Apple 安装包签名无法通过 Windows 验证，已拒绝执行。 | Apple 安裝套件簽名無法通過 Windows 驗證，已拒絕執行。 | Apple 安裝套件簽章無法通過 Windows 驗證，已拒絕執行。 | The Apple installer failed Windows signature validation and was not executed. | — | ✅ 完全一致 | 保留 |
| AppleSupportDownloadProgress | 正在下载 Apple 服务组件：{0}%（{1:F1}/{2:F1} MB） | 正在下載 Apple 服務元件：{0}%（{1:F1}/{2:F1} MB） | 正在下載 Apple 服務元件：{0}%（{1:F1}/{2:F1} MB） | Downloading the Apple service component: {0}% ({1:F1}/{2:F1} MB) | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| AppleSupportDownloadProgressParallel | 正在下载 Apple 服务组件：{0}%（{1:F1}/{2:F1} MB，{3:F1} MB/s，{4} 路） | 正在下載 Apple 服務元件：{0}%（{1:F1}/{2:F1} MB，{3:F1} MB/s，{4} 路） | 正在下載 Apple 服務元件：{0}%（{1:F1}/{2:F1} MB，{3:F1} MB/s，{4} 路） | Downloading the Apple service component: {0}% ({1:F1}/{2:F1} MB, {3:F1} MB/s, {4} connections) | — | ✅ 完全一致 | 保留 |
| AppleSupportDownloadProgressUnknown | 正在下载 Apple 服务组件：已下载 {0:F1} MB | 正在下載 Apple 服務元件：已下載 {0:F1} MB | 正在下載 Apple 服務元件：已下載 {0:F1} MB | Downloading the Apple service component: {0:F1} MB downloaded | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| AppleSupportDownloadProgressUnknownParallel | 正在下载 Apple 服务组件：已下载 {0:F1} MB（{1:F1} MB/s，{2} 路） | 正在下載 Apple 服務元件：已下載 {0:F1} MB（{1:F1} MB/s，{2} 路） | 正在下載 Apple 服務元件：已下載 {0:F1} MB（{1:F1} MB/s，{2} 路） | Downloading the Apple service component: {0:F1} MB ({1:F1} MB/s, {2} connections) | — | ✅ 完全一致 | 保留 |
| AppleSupportDownloadingCompatibility | 正在从 Apple 官方更新服务获取驱动组件… | 正在從 Apple 官方更新服務取得驅動程式元件… | 正在從 Apple 官方更新服務取得驅動程式元件… | Downloading the driver component from Apple Software Update… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleSupportExtractingCompatibility | 正在从 Apple 官方安装包提取服务组件… | 正在從 Apple 官方安裝套件提取服務元件… | 正在從 Apple 官方安裝套件提取服務元件… | Extracting the service component from the official Apple installer… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleSupportInstallTimeout | Apple USB 支持组件安装超时。 | Apple USB 支援元件安裝逾時。 | Apple USB 支援元件安裝逾時。 | Apple USB support installation timed out. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源；静态未发现引用，保留待后续调用核对 |
| AppleSupportInstallingCompatibility | 正在安装 Apple Mobile Device Service 兼容组件… | 正在安裝 Apple Mobile Device Service 相容元件… | 正在安裝 Apple Mobile Device Service 相容元件… | Installing the Apple Mobile Device Service compatibility component… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleSupportInstallingStore | 正在从 Microsoft Store 安装或修复 Apple USB 支持… | 正在從 Microsoft Store 安裝或修復 Apple USB 支援… | 正在從 Microsoft Store 安裝或修復 Apple USB 支援… | Installing or repairing Apple USB support from Microsoft Store… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号；静态未发现引用，保留待后续调用核对 |
| AppleSupportMissing | 未检测到 Apple Devices 或 Apple Mobile Device Support | 未偵測到 Apple Devices 或 Apple Mobile Device Support | 未偵測到 Apple Devices 或 Apple Mobile Device Support | Apple Devices or Apple Mobile Device Support was not found | — | ✅ 完全一致 | 保留 |
| AppleSupportPreparing | 正在准备 Apple USB 支持… | 正在準備 Apple USB 支援… | 正在準備 Apple USB 支援… | Preparing Apple USB support… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleSupportProcessTimeout | Apple 支持组件操作超时。 | Apple 支援元件操作逾時。 | Apple 支援元件操作逾時。 | The Apple support component operation timed out. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| AppleSupportReady | Apple USB 支持已就绪。 | Apple USB 支援已就緒。 | Apple USB 支援已就緒。 | Apple USB support is ready. | — | ✅ 完全一致 | 保留 |
| AppleSupportStartingService | 正在启动 Apple Mobile Device Service… | 正在啟動 Apple Mobile Device Service… | 正在啟動 Apple Mobile Device Service… | Starting Apple Mobile Device Service… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleSupportVerifying | 正在验证 Apple USB 驱动和服务… | 正在驗證 Apple USB 驅動程式和服務… | 正在驗證 Apple USB 驅動程式和服務… | Verifying the Apple USB driver and service… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| AppleUsbDeviceFormat | Apple USB 设备 {0} | Apple USB 裝置 {0} | Apple USB 裝置 {0} | Apple USB device {0} | — | ✅ 完全一致 | 保留 |
| AppleUsbDriverMissing | Apple Mobile Device Service 已安装，但缺少 Apple USB 驱动包 | Apple Mobile Device Service 已安裝，但缺少 Apple USB 驅動程式包 | Apple Mobile Device Service 已安裝，但缺少 Apple USB 驅動程式套件 | Apple Mobile Device Service is installed, but the Apple USB driver package is missing | — | ✅ 完全一致 | 保留 |
| AppleUsbEnvironment | Apple USB 环境 | Apple USB 環境 | Apple USB 環境 | Apple USB environment | — | ✅ 完全一致 | 保留 |
| BackToSimple | 返回简洁界面 | 返回簡易介面 | 返回簡易介面 | Back to simple view | — | ✅ 完全一致 | 保留 |
| Cancel | 取消 | 取消 | 取消 | Cancel | — | ✅ 完全一致 | 保留 |
| CannotOpenLogs | 无法打开日志 | 無法開啟記錄檔 | 無法開啟記錄檔 | Could not open logs | — | ✅ 完全一致 | 保留 |
| CannotOpenQq | 无法打开 QQ | 無法開啟 QQ | 無法開啟 QQ | Could not open QQ | — | ✅ 完全一致 | 保留 |
| CaptureFilterDriver | 采集过滤驱动 | 擷取過濾驅動程式 | 擷取篩選驅動程式 | Capture filter driver | — | ✅ 完全一致 | 保留 |
| CaptureInstalled | 采集驱动已安装 | 擷取驅動程式已安裝 | 擷取驅動程式已安裝 | Capture driver installed | — | ✅ 完全一致 | 保留 |
| CaptureMissing | 未安装采集驱动 | 未安裝擷取驅動程式 | 未安裝擷取驅動程式 | Capture driver not installed | — | ✅ 完全一致 | 保留 |
| CheckPending | 正在检查 | 正在檢查 | 正在檢查 | Checking | — | ✅ 完全一致 | 保留 |
| Close | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 保留 |
| ConfirmOperation | 确认{0} | 確認{0} | 確認{0} | Confirm {0} | — | ✅ 完全一致 | 保留 |
| ConfirmOperationBody | 将对以下设备执行{0}：<br><br>{1}<br>{2}<br><br>操作只修改该设备的 libusb0 过滤器，不替换 Apple 官方驱动。Windows 可能请求管理员授权。 | 將對以下裝置執行{0}：<br><br>{1}<br>{2}<br><br>操作只修改這部裝置的 libusb0 過濾器，不替換 Apple 官方驅動程式。Windows 可能請求管理員授權。 | 將對以下裝置執行{0}：<br><br>{1}<br>{2}<br><br>操作只修改這台裝置的 libusb0 篩選器，不替換 Apple 官方驅動程式。Windows 可能請求管理員授權。 | Perform {0} on the following device:<br><br>{1}<br>{2}<br><br>Only this device's libusb0 filter is changed. Apple's official driver is not replaced. Windows may request administrator approval. | — | ❌ 含义不一致 → ✅ 已修复 | {0} 是操作名，不是设备信息 |
| ConnectIphone | 请先连接 iPhone | 請先連接 iPhone | 請先連線 iPhone | Connect an iPhone first | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| ConnectPhoneBody | Apple USB 支持已准备好。请连接并解锁 iPhone；手机出现提示时点击“信任此电脑”。检测到设备后会自动继续。 | Apple USB 支援已準備就緒。請連接並解鎖 iPhone；手機出現提示時按一下「信任這部電腦」。偵測到裝置後會自動繼續。 | Apple USB 支援已準備就緒。請連線並解鎖 iPhone；手機出現提示時按一下「信任這台電腦」。偵測到裝置後會自動繼續。 | Apple USB support is ready. Connect and unlock the iPhone, then tap Trust if prompted. The tool continues automatically after it detects the device. | — | ✅ 完全一致 | 保留 |
| ConnectPhoneTitle | 请连接 iPhone | 請連接 iPhone | 請連線 iPhone | Connect an iPhone | — | ✅ 完全一致 | 保留 |
| Connected | 已连接 | 已連接 | 已連線 | Connected | — | ✅ 完全一致 | 保留 |
| ConnectionStatus | 连接状态 | 連接狀態 | 連線狀態 | Connection | — | ✅ 完全一致 | 保留 |
| Continue | 继续 | 繼續 | 繼續 | Continue | — | ✅ 完全一致 | 保留 |
| CopyGroup | 复制群号 | 複製群號 | 複製群組編號 | Copy group number | — | ✅ 完全一致 | 保留 |
| CurrentDevice | 当前设备 | 目前裝置 | 目前裝置 | Current device | — | ✅ 完全一致 | 保留 |
| DeviceInstance | 设备实例 | 裝置執行個體 | 裝置執行個體 | Device instance | — | ✅ 完全一致 | 保留 |
| DeviceModelFormat | 设备型号 {0} | 裝置型號 {0} | 裝置型號 {0} | Model {0} | — | ✅ 完全一致 | 保留 |
| DeviceNumberFormat | {0}（设备 {1}） | {0}（裝置 {1}） | {0}（裝置 {1}） | {0} (device {1}) | — | ✅ 完全一致 | 保留 |
| DeviceOsFormat | {0} · iOS {1} | {0} · iOS {1} | {0} · iOS {1} | {0} · iOS {1} | — | ✅ 完全一致 | 保留 |
| DeviceReady | {0} 的投屏驱动已经全部就绪。 | {0} 的螢幕鏡像驅動程式已全部就緒。 | {0} 的螢幕鏡像驅動程式已全部就緒。 | All mirroring drivers for {0} are ready. | — | ❌ 含义不一致 → ✅ 已修复 | 补齐全部驱动就绪含义 |
| DeviceTrustAction | 请解锁 iPhone 或 iPad，并查看设备屏幕 | 請解鎖 iPhone 或 iPad，並查看裝置螢幕 | 請解鎖 iPhone 或 iPad，並查看裝置螢幕 | Unlock the iPhone or iPad and check its screen | — | ✅ 完全一致 | 保留 |
| DeviceTrustConfirmed | 出现了提示，我已点击“信任” | 出現了提示，我已按一下「信任」 | 出現了提示，我已按一下「信任」 | The prompt appeared and I tapped Trust | — | ✅ 完全一致 | 保留 |
| DeviceTrustExplanation | Windows 刚刚变更了驱动，可能会重新识别设备。如果设备出现“要信任此电脑吗？”，请点击“信任”并输入设备密码。 | Windows 剛剛變更了驅動程式，可能會重新辨識裝置。如果裝置出現「要信任這部電腦嗎？」，請按一下「信任」並輸入裝置密碼。 | Windows 剛剛變更了驅動程式，可能會重新辨識裝置。如果裝置出現「要信任這台電腦嗎？」，請按一下「信任」並輸入裝置密碼。 | Windows has changed a driver and may detect the device again. If “Trust This Computer?” appears on the device, tap Trust and enter the device passcode. | — | ✅ 完全一致 | 保留 |
| DeviceTrustNoPrompt | 没有出现提示（此前已信任此电脑） | 沒有出現提示（此前已信任這部電腦） | 沒有出現提示（此前已信任這台電腦） | No prompt appeared (this computer was already trusted) | — | ✅ 完全一致 | 保留 |
| DeviceTrustNotHandled | 尚未处理 | 尚未處理 | 尚未處理 | Not handled yet | — | ✅ 完全一致 | 保留 |
| DeviceTrustPending | 驱动变更已完成。投屏前请解锁设备，并在设备上点击“信任”。 | 驅動程式變更已完成。螢幕鏡像前請解鎖裝置，並在裝置上按一下「信任」。 | 驅動程式變更已完成。螢幕鏡像前請解鎖裝置，並在裝置上按一下「信任」。 | Driver changes are complete. Unlock the device and tap Trust before mirroring. | — | ✅ 完全一致 | 保留 |
| DeviceTrustQuestion | 设备上出现了什么情况？ | 裝置上出現了什麼情況？ | 裝置上出現了什麼情況？ | What happened on the device? | — | ✅ 完全一致 | 保留 |
| DeviceTrustTitle | 确认设备信任状态 | 確認裝置信任狀態 | 確認裝置信任狀態 | Confirm device trust | — | ✅ 完全一致 | 保留 |
| DevicesMissing | 已检测到 {0} 台设备，点击按钮自动安装缺失驱动。 | 已偵測到 {0} 部裝置，按一下按鈕自動安裝遺失驅動程式。 | 已偵測到 {0} 台裝置，按一下按鈕自動安裝遺失驅動程式。 | Devices detected: {0}. Click the button to install missing drivers. | — | ⚠️ 需要优化 → ✅ 已修复 | 用数量标签兼容单复数 |
| DevicesReady | 已检测到 {0} 台设备，投屏驱动均已就绪。 | 已偵測到 {0} 部裝置，螢幕鏡像驅動程式均已就緒。 | 已偵測到 {0} 台裝置，螢幕鏡像驅動程式均已就緒。 | Devices detected: {0}. All mirroring drivers are ready. | — | ⚠️ 需要优化 → ✅ 已修复 | 用数量标签兼容单复数 |
| DriverChangeNotice | 采集操作修改所选设备的过滤器；父驱动管理可在你单独确认后替换该设备的驱动绑定。 | 擷取操作修改所選裝置的過濾器；父驅動程式管理可在你另行確認後取代該裝置的驅動程式綁定。 | 擷取操作修改所選裝置的篩選器；父驅動程式管理可在您另行確認後取代該裝置的驅動程式綁定。 | Capture operations change the selected device's filter. Parent driver management can replace its driver binding after a separate confirmation. | — | ✅ 完全一致 | 保留 |
| DriverCleanupBody | 在独立窗口中选择设备，清理其关联设备节点及可安全移除的驱动包，可能包括 Apple 官方驱动。其他设备仍在使用的驱动包会保留；无法确定使用情况时只清理设备节点。修改系统前需要确认。 | 在獨立視窗中選擇裝置，清理其關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式。其他裝置仍在使用的驅動程式套件會保留；無法確定使用情況時只清理裝置節點。修改系統前需要確認。 | 在獨立視窗中選擇裝置，清理其關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式。其他裝置仍在使用的驅動程式套件會保留；無法確定使用情況時只清理裝置節點。修改系統前需要確認。 | Choose a device in a separate window to remove its associated device nodes and driver packages that can be safely removed, which may include Apple drivers. Packages used by other devices are preserved. If package usage cannot be verified, only device nodes are removed. Confirmation is required before changing the system. | — | ✅ 完全一致 | 保留 |
| DriverCleanupHostStartFailed | 无法启动管理员驱动清理进程。 | 無法啟動管理員驅動程式清理程序。 | 無法啟動管理員驅動程式清理程序。 | The driver cleanup process could not start with administrator access. | — | ❌ 缺失 → ✅ 已修复 | 清理启动失败在已初始化的界面中使用三语提示；启动前的诊断回退保留 |
| DriverCleanupProtectionFailed | 无法验证并保护驱动管理器文件，已停止请求管理员权限。 | 無法驗證及保護驅動程式管理員檔案，已停止要求管理員權限。 | 無法驗證及保護驅動程式管理員檔案，已停止要求管理員權限。 | The driver manager file could not be verified and protected. Administrator access was not requested. | — | ❌ 缺失 → ✅ 已修复 | 清理启动失败在已初始化的界面中使用三语提示；启动前的诊断回退保留 |
| DriverCleanupScriptMissing | 找不到驱动清理脚本：{0} | 找不到驅動程式清理指令碼：{0} | 找不到驅動程式清理指令碼：{0} | The driver cleanup script was not found: {0} | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| DriverCleanupScriptStartFailed | 无法启动驱动清理脚本：{0} | 無法啟動驅動程式清理指令碼：{0} | 無法啟動驅動程式清理指令碼：{0} | Could not start the driver cleanup script: {0} | — | ✅ 完全一致 | 保留 |
| DriverCleanupTitle | 清理设备驱动 | 清理裝置驅動程式 | 清理裝置驅動程式 | Clean up device drivers | — | ✅ 完全一致 | 保留 |
| DriverExecutableMissing | 无法确定驱动管理器的可执行文件路径。 | 無法確定驅動程式管理員的可執行檔路徑。 | 無法確定驅動程式管理員的可執行檔路徑。 | The driver manager executable path could not be determined. | — | ✅ 完全一致 | 保留 |
| DriverFilterInstalledReconnect | 已安装所选设备的采集过滤驱动。请重新连接设备以完成启用。 | 已安裝所選裝置的擷取過濾驅動程式。請重新連接裝置以完成啟用。 | 已安裝所選裝置的擷取篩選驅動程式。請重新連線裝置以完成啟用。 | Selected-device capture filter installed. Reconnect the device to complete activation. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverFilterRemovedReconnect | 已移除所选设备的采集过滤驱动。请重新连接设备以完成卸载。 | 已移除所選裝置的擷取過濾驅動程式。請重新連接裝置以完成卸載。 | 已移除所選裝置的擷取篩選驅動程式。請重新連線裝置以完成卸載。 | Selected-device capture filter removed. Reconnect the device to complete unload. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverInstalled | 当前设备的采集过滤驱动已安装并验证。 | 目前裝置的擷取過濾驅動程式已安裝並驗證。 | 目前裝置的擷取篩選驅動程式已安裝並驗證。 | The capture filter was installed and verified on the current device. | — | ✅ 完全一致 | 保留 |
| DriverOperationDetailsFormat | 详细信息：{0} | 詳細資料：{0} | 詳細資料：{0} | Details: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动操作详情标题 |
| DriverOperationRollbackIncomplete | 驱动操作失败，回滚未完成。请查看操作日志。 | 驅動程式操作失敗，復原未完成。請查看操作記錄。 | 驅動程式操作失敗，復原未完成。請查看操作記錄檔。 | Driver operation failed and rollback was incomplete. Review the operation log. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverOperationRolledBack | 驱动操作失败，已还原所有已备份的状态。 | 驅動程式操作失敗，已還原所有已備份的狀態。 | 驅動程式操作失敗，已還原所有已備份的狀態。 | Driver operation failed and all captured state was restored. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverParentRemoved | 已移除错误的 Apple 父设备。请重新连接 iPhone 以重新绑定 usbccgp。 | 已移除錯誤的 Apple 父裝置。請重新連接 iPhone 以重新綁定 usbccgp。 | 已移除錯誤的 Apple 父裝置。請重新連線 iPhone 以重新綁定 usbccgp。 | The incorrect Apple parent device was removed. Reconnect the iPhone to rebind usbccgp. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverParentRepairRejected | 父驱动修复已被拒绝，尚未修改系统。 | 父驅動程式修復已被拒絕，尚未修改系統。 | 父驅動程式修復已被拒絕，尚未修改系統。 | Parent driver repair was rejected before any system change. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverParentRepairStopped | 移除请求开始后，父驱动修复已停止。请重新连接 iPhone 并查看操作日志。 | 移除要求開始後，父驅動程式修復已停止。請重新連接 iPhone 並查看操作記錄。 | 移除要求開始後，父驅動程式修復已停止。請重新連線 iPhone 並查看操作記錄檔。 | Parent driver repair stopped after the removal request began. Reconnect the iPhone and review the operation log. | — | ❌ 缺失 → ✅ 已修复 | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 |
| DriverRefreshFailed | 状态刷新失败，以上操作结果已保留。原因：{0}。日志：{1} | 狀態重新整理失敗，以上操作結果已保留。原因：{0}。記錄：{1} | 狀態重新整理失敗，以上操作結果已保留。原因：{0}。記錄檔：{1} | Status refresh failed; the operation result above is preserved. Reason: {0}. Log: {1} | — | ✅ 完全一致 | 保留 |
| DriverResultExitCodeMismatchFormat | 管理员驱动操作结果与进程退出代码 {0} 不一致。 | 管理員驅動程式操作結果與程序結束代碼 {0} 不一致。 | 管理員驅動程式操作結果與程序結束代碼 {0} 不一致。 | The elevated driver result did not match process exit code {0}. | — | ❌ 缺失 → ✅ 已修复 | 驱动验证失败提示补齐三语 |
| DriverResultTargetMismatch | 管理员驱动操作返回了其他设备的结果，已拒绝使用。 | 管理員驅動程式操作傳回了其他裝置的結果，已拒絕使用。 | 管理員驅動程式操作傳回了其他裝置的結果，已拒絕使用。 | The elevated driver result did not match the requested device and was rejected. | — | ❌ 缺失 → ✅ 已修复 | 将用户可见硬编码错误或状态提示迁入三语资源 |
| DriverStatus | 驱动状态 | 驅動程式狀態 | 驅動程式狀態 | Driver status | — | ✅ 完全一致 | 保留 |
| DriverUninstalled | 当前设备的采集过滤驱动已卸载。 | 目前裝置的擷取過濾驅動程式已解除安裝。 | 目前裝置的擷取篩選驅動程式已解除安裝。 | The capture filter was removed from the current device. | — | ✅ 完全一致 | 保留 |
| DriverWaitingSafeStop | 操作用时较长，已请求取消，正在等待当前步骤和恢复完成。请保持窗口打开。日志：{0} | 操作需時較長，已請求取消，正在等待目前步驟及還原完成。請保持視窗開啟。記錄：{0} | 操作需時較長，已請求取消，正在等待目前步驟及還原完成。請保持視窗開啟。記錄檔：{0} | The operation is taking longer than expected. Cancellation was requested; waiting for the current step and recovery to finish. Keep this window open. Log: {0} | — | ✅ 完全一致 | 保留 |
| ElevatedInvalidResult | 管理员驱动进程返回了无效结果。 | 以系統管理員身分執行的驅動程式工具傳回了無效結果。 | 以系統管理員身分執行的驅動程式工具傳回了無效結果。 | The elevated driver process returned an invalid result. | — | ✅ 完全一致 | 保留 |
| ElevatedProcessNoResult | 管理员驱动进程以代码 {0} 退出，但没有返回结果。 | 以系統管理員身分執行的驅動程式工具以代碼 {0} 退出，但沒有傳回結果。 | 以系統管理員身分執行的驅動程式工具以代碼 {0} 退出，但沒有傳回結果。 | The elevated driver process exited with code {0} without returning a result. | — | ✅ 完全一致 | 保留 |
| ElevatedProcessStartFailed | 管理员驱动进程未能启动。 | 以系統管理員身分執行的驅動程式操作未能啟動。 | 以系統管理員身分執行的驅動程式操作未能啟動。 | The elevated driver process could not be started. | — | ✅ 完全一致 | 保留 |
| ElevatedProcessTimeout | 管理员驱动操作已超时，请查看操作日志后重试。 | 以系統管理員身分執行的驅動程式操作已逾時，請查看操作記錄後重試。 | 以系統管理員身分執行的驅動程式操作已逾時，請查看操作記錄檔後重試。 | The elevated driver operation timed out. Review the operation log before retrying. | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| FailureHeading | 未能完成驱动操作 | 未能完成驅動程式操作 | 未能完成驅動程式操作 | The driver operation could not be completed | — | ✅ 完全一致 | 保留 |
| FailureMethods | 可以选择以下一种处理方式 | 你可以選擇以下其中一種處理方式 | 您可以選擇以下其中一種處理方式 | Choose one of the following options | — | ✅ 完全一致 | 保留 |
| FailureTitle | 驱动处理失败 | 驅動程式處理失敗 | 驅動程式處理失敗 | Driver operation failed | — | ✅ 完全一致 | 保留 |
| ForceDriverCleanup | 清理设备驱动 | 清理裝置驅動程式 | 清理裝置驅動程式 | Clean up device drivers | — | ✅ 完全一致 | 保留 |
| GroupCopiedBody | QQ 群号 1050045279 已复制。请在 QQ 中搜索并申请加入。 | QQ 群號 1050045279 已複製。請在 QQ 中搜尋並申請加入。 | QQ 群組編號 1050045279 已複製。請在 QQ 中搜尋並申請加入。 | QQ group 1050045279 was copied. Search for it in QQ to apply. | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| GroupCopiedTitle | 群号已复制 | 群號已複製 | 群組編號已複製 | Group number copied | — | ✅ 完全一致 | 保留 |
| HelpBody | 加入 QQ 群 1050045279，并提供操作日志。 | 加入 QQ 群 1050045279，並提供操作記錄。 | 加入 QQ 群組 1050045279，並提供操作記錄檔。 | Join QQ group 1050045279 and provide the operation log. | — | ✅ 完全一致 | 保留 |
| HelpBrowserFailed | 请手动打开：{0}。原因：{1} | 請手動開啟：{0}。原因：{1} | 請手動開啟：{0}。原因：{1} | Open this address manually: {0}. Reason: {1} | — | ✅ 完全一致 | 保留 |
| HelpBrowserFailedTitle | 无法打开帮助网站 | 無法開啟說明網站 | 無法開啟說明網站 | Cannot open the help website | — | ✅ 完全一致 | 保留 |
| HelpTitle | 获取人工帮助 | 取得人工幫助 | 取得人工協助 | Get community help | — | ✅ 完全一致 | 保留 |
| HistoricalDevice | 历史设备 | 過往裝置 | 過往裝置 | Previously connected device | — | ⚠️ 需要优化 → ✅ 已修复 | 优化自然表达和可读性 |
| Install | 安装 | 安裝 | 安裝 | Install | — | ✅ 完全一致 | 保留 |
| InstallAll | 一键安装全部驱动 | 一按安裝全部驅動程式 | 一鍵安裝全部驅動程式 | Install all drivers | — | ✅ 完全一致 | 保留 |
| InstallAppleDevices | 安装 Apple USB 支持 | 安裝 Apple USB 支援 | 安裝 Apple USB 支援 | Install Apple USB support | — | ✅ 完全一致 | 保留 |
| Installed | 已安装 | 已安裝 | 已安裝 | Installed | — | ✅ 完全一致 | 保留 |
| InstallingDriver | 正在为 {0} 安装采集驱动… | 正在為 {0} 安裝擷取驅動程式… | 正在為 {0} 安裝擷取驅動程式… | Installing the capture driver for {0}… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| InvalidDeviceTarget | 拒绝了无效的 Apple 设备目标。 | 拒絕了無效的 Apple 裝置目標。 | 拒絕了無效的 Apple 裝置目標。 | The selected Apple device target is invalid. | — | ✅ 完全一致 | 保留 |
| LibUsbCheckFailed | 无法检查 libusb0： | 無法檢查 libusb0： | 無法檢查 libusb0： | Could not inspect libusb0:  | — | ✅ 完全一致 | 保留 |
| LibUsbFilesMismatch | libusb0 文件缺失或与受信任版本不一致 | libusb0 檔案遺失或與受信任版本不一致 | libusb0 檔案遺失或與受信任版本不一致 | libusb0 files are missing or do not match the trusted version | — | ✅ 完全一致 | 保留 |
| LibUsbMissing | libusb0 共享服务尚未安装 | libusb0 共用服務尚未安裝 | libusb0 共用服務尚未安裝 | The libusb0 shared service is not installed | — | ✅ 完全一致 | 保留 |
| LibUsbReadyOnConnect | libusb0 已安装，连接设备后将加载 | libusb0 已安裝，連接裝置後將載入 | libusb0 已安裝，連線裝置後將載入 | libusb0 is installed and will load when a device connects | — | ✅ 完全一致 | 保留 |
| LibUsbRunning | libusb0 {0} 正在运行 | libusb0 {0} 正在執行 | libusb0 {0} 正在執行 | libusb0 {0} is running | — | ✅ 完全一致 | 保留 |
| LogSuffix | 日志：{0} | 記錄：{0} | 記錄檔：{0} | Log: {0} | — | ✅ 完全一致 | 保留 |
| Maximize | 最大化或还原 | 最大化或還原 | 最大化或還原 | Maximize or restore | — | ✅ 完全一致 | 保留 |
| Minimize | 最小化 | 最小化 | 最小化 | Minimize | — | ✅ 完全一致 | 保留 |
| NamedDeviceFormat | {0}（{1}） | {0}（{1}） | {0}（{1}） | {0} ({1}) | — | ✅ 完全一致 | 保留 |
| NoSelection | 未选择设备 | 未選擇裝置 | 未選擇裝置 | No device selected | — | ✅ 完全一致 | 保留；静态未发现引用，保留待后续调用核对 |
| OfflineMsiFailed | 离线 AppleMobileDeviceSupport MSI 安装失败，退出代码 {0}。<br>{1}<br>日志：{2} | 離線 AppleMobileDeviceSupport MSI 安裝失敗，結束代碼 {0}。<br>{1}<br>記錄：{2} | 離線 AppleMobileDeviceSupport MSI 安裝失敗，結束代碼 {0}。<br>{1}<br>記錄檔：{2} | The offline AppleMobileDeviceSupport MSI failed with exit code {0}.<br>{1}<br>Log: {2} | — | ✅ 完全一致 | 保留 |
| OpenLogs | 打开日志 | 開啟記錄檔 | 開啟記錄檔 | Open logs | — | ✅ 完全一致 | 保留 |
| OpenWebsite | 打开官网 | 開啟官網 | 開啟官網 | Open website | — | ✅ 完全一致 | 保留 |
| Operating | 正在执行{0}：{1}… | 正在執行{0}：{1}… | 正在執行{0}：{1}… | Operation: {0} — {1}… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号；参数为 Install/Repair/Uninstall 基本形式，避免组成 Install device 的假进行时 |
| ParentBackupFormat | 状态快照：{0} | 狀態快照：{0} | 狀態快照：{0} | State snapshot: {0} | — | ✅ 完全一致 | 保留 |
| ParentBind | 绑定所选驱动 | 綁定所選驅動程式 | 綁定所選驅動程式 | Bind selected driver | — | ✅ 完全一致 | 保留 |
| ParentBindingComplete | 父驱动已重新绑定，并已确认设备正常启动。 | 父驅動程式已重新綁定，並已確認裝置正常啟動。 | 父驅動程式已重新綁定，並已確認裝置正常啟動。 | The parent driver was rebound and the device started successfully. | — | ✅ 完全一致 | 保留 |
| ParentBindingRestartRequired | Windows 已接受父驱动绑定，请重启电脑后重新检测；当前尚未验证生效。 | Windows 已接受父驅動程式綁定，請重新啟動電腦後再檢測；目前尚未驗證是否生效。 | Windows 已接受父驅動程式綁定，請重新啟動電腦後再檢測；目前尚未驗證是否生效。 | Windows accepted the parent driver binding. Restart the PC and refresh to verify it; activation has not yet been verified. | — | ✅ 完全一致 | 保留 |
| ParentChangeRecoveryNeeded | 父驱动操作未完成，无法确认已恢复。请重新插拔手机并刷新；必要时在父驱动管理或设备管理器中重新绑定驱动。状态快照和日志保留了操作信息。 | 父驅動程式操作未完成，無法確認已還原。請重新插拔手機並重新整理；必要時在父驅動程式管理或裝置管理員中重新綁定驅動程式。狀態快照及記錄保留了操作資料。 | 父驅動程式操作未完成，無法確認已還原。請重新插拔手機並重新整理；必要時在父驅動程式管理或裝置管理員中重新綁定驅動程式。狀態快照及記錄檔保留了操作資料。 | The parent driver operation did not complete and recovery could not be verified. Reconnect the phone and refresh; use parent driver management or Device Manager to rebind if needed. The state snapshot and log contain operation details. | — | ✅ 完全一致 | 保留 |
| ParentChangeRejected | 修改未执行：设备状态、确认信息或目标驱动已变化，或准备工作失败。请刷新后重新确认，详细原因见日志。 | 未執行變更：裝置狀態、確認資料或目標驅動程式已變更，或準備工作失敗。請重新整理後再次確認，詳細原因請參閱記錄。 | 未執行變更：裝置狀態、確認資料或目標驅動程式已變更，或準備工作失敗。請重新整理後再次確認，詳細原因請參閱記錄檔。 | No change was made: the device state, confirmation, or target driver changed, or preparation failed. Refresh and confirm again; see the log for details. | — | ✅ 完全一致 | 保留 |
| ParentChangeRolledBack | 父驱动绑定失败，已恢复原驱动绑定。原有故障可能仍然存在，请查看日志。 | 父驅動程式綁定失敗，已還原原驅動程式綁定。原有問題可能仍然存在，請查看記錄。 | 父驅動程式綁定失敗，已還原原驅動程式綁定。原有問題可能仍然存在，請查看記錄檔。 | Parent driver binding failed. The previous driver binding was restored; its original problem may remain. See the log for details. | — | ✅ 完全一致 | 保留 |
| ParentCompositeUnavailable | 无法读取 Windows USB 复合驱动，请打开父驱动管理查看其他选项。 | 無法讀取 Windows USB 複合驅動程式，請開啟父驅動程式管理查看其他選項。 | 無法讀取 Windows USB 複合驅動程式，請開啟父驅動程式管理查看其他選項。 | The Windows USB composite driver is unavailable. Open parent driver management for other options. | — | ✅ 完全一致 | 保留 |
| ParentConfirmApply | 确认修改 | 確認修改 | 確認修改 | Confirm change | — | ✅ 完全一致 | 保留 |
| ParentConfirmBody | 设备：{0}<br>{1}<br><br>当前状态：<br>{2}<br><br>执行操作／目标驱动：<br>{3}<br><br>将修改这台手机的父驱动，可能替换 Apple 官方或第三方绑定，并暂时中断该手机的 USB 连接、投屏、控制和 Apple 软件连接。子接口可能重新枚举。Windows 可能要求管理员授权、重新插拔或重启。<br><br>操作前会保存驱动绑定和过滤器状态快照；绑定失败会尝试恢复原绑定，但无法保证恢复成功。是否继续？ | 裝置：{0}<br>{1}<br><br>目前狀態：<br>{2}<br><br>執行操作／目標驅動程式：<br>{3}<br><br>將修改這部手機的父驅動程式，可能取代 Apple 官方或第三方綁定，並暫時中斷此手機的 USB 連線、螢幕鏡像、控制及 Apple 軟件連線。子介面可能重新列舉。Windows 可能要求管理員授權、重新插拔或重新啟動。<br><br>操作前會儲存驅動程式綁定及過濾器狀態快照；綁定失敗會嘗試還原原綁定，但無法保證還原成功。是否繼續？ | 裝置：{0}<br>{1}<br><br>目前狀態：<br>{2}<br><br>執行操作／目標驅動程式：<br>{3}<br><br>將修改這台手機的父驅動程式，可能取代 Apple 官方或第三方綁定，並暫時中斷此手機的 USB 連線、螢幕鏡像、控制及 Apple 軟體連線。子介面可能重新列舉。Windows 可能要求管理員授權、重新插拔或重新啟動。<br><br>操作前會儲存驅動程式綁定及篩選器狀態快照；綁定失敗會嘗試還原原綁定，但無法保證還原成功。是否繼續？ | Device: {0}<br>{1}<br><br>Current state:<br>{2}<br><br>Action / target driver:<br>{3}<br><br>This changes this phone’s parent driver and may replace an Apple or third-party binding. USB connectivity, mirroring, control, and Apple software connections may be interrupted. Child interfaces may be enumerated again. Windows may request administrator approval, reconnection, or a restart.<br><br>Driver binding and filter state will be saved before changes. A failed binding triggers an attempt to restore the previous binding, but recovery is not guaranteed. Continue? | — | ✅ 完全一致 | 保留 |
| ParentConfirmTitle | 确认修改 Apple 父驱动 | 確認修改 Apple 父驅動程式 | 確認修改 Apple 父驅動程式 | Confirm Apple parent driver change | — | ✅ 完全一致 | 保留 |
| ParentDeviceUnavailable | 所选手机已断开，请重新连接后再操作。 | 所選手機已中斷連線，請重新連接後再操作。 | 所選手機已中斷連線，請重新連線後再操作。 | The selected phone is disconnected. Reconnect it before continuing. | — | ✅ 完全一致 | 保留 |
| ParentDriverSelection | 目标驱动（系统复合驱动及 Windows 提供的适用驱动） | 目標驅動程式（系統複合驅動程式及 Windows 提供的適用驅動程式） | 目標驅動程式（系統複合驅動程式及 Windows 提供的適用驅動程式） | Target driver (system composite driver and drivers available for this device) | — | ✅ 完全一致 | 保留 |
| ParentHealthy | 已正常启动 | 已正常啟動 | 已正常啟動 | Started and healthy | — | ✅ 完全一致 | 保留 |
| ParentListPartial | 部分驱动列表读取失败，以下仍列出可用选项。详细信息： | 部分驅動程式清單讀取失敗，以下仍列出可用選項。詳細資料： | 部分驅動程式清單讀取失敗，以下仍列出可用選項。詳細資料： | Some driver lists could not be read. Available choices are still listed. Details: | — | ✅ 完全一致 | 保留 |
| ParentListUnavailable | 无法读取驱动列表。可重建设备后重试，详细原因见日志。 | 無法讀取驅動程式清單。可重建裝置後重試，詳細原因請參閱記錄。 | 無法讀取驅動程式清單。可重建裝置後重試，詳細原因請參閱記錄檔。 | The driver list could not be read. Rebuild the device and retry; see the log for details. | — | ✅ 完全一致 | 保留 |
| ParentManagerHelp | 可修改任意当前父驱动，包括 Apple 官方驱动、第三方驱动或缺失的绑定。有线投屏通常需要 USB Composite Device（usbccgp）。请选择目标驱动，下一步将确认修改。 | 可修改任何目前的父驅動程式，包括 Apple 官方驅動程式、第三方驅動程式或遺失的綁定。有線螢幕鏡像通常需要 USB Composite Device（usbccgp）。請選取目標驅動程式，下一步將確認變更。 | 可修改任何目前的父驅動程式，包括 Apple 官方驅動程式、第三方驅動程式或遺失的綁定。有線螢幕鏡像通常需要 USB Composite Device（usbccgp）。請選取目標驅動程式，下一步將確認變更。 | You can change any current parent binding, including Apple, third-party, or missing drivers. Wired mirroring normally requires USB Composite Device (usbccgp). Select the target driver, then review and confirm the change. | — | ✅ 完全一致 | 保留 |
| ParentManagerTitle | 父驱动管理 | 父驅動程式管理 | 父驅動程式管理 | Manage parent driver | — | ✅ 完全一致 | 保留 |
| ParentNeedsAttention | 未启动或需要检查 | 未啟動或需要檢查 | 未啟動或需要檢查 | Not started or needs attention | — | ✅ 完全一致 | 保留 |
| ParentNoInf | 无驱动包 | 沒有驅動程式套件 | 沒有驅動程式套件 | No driver package | — | ✅ 完全一致 | 保留 |
| ParentNoService | 未绑定服务 | 未綁定服務 | 未綁定服務 | No service bound | — | ✅ 完全一致 | 保留 |
| ParentRepairFailed | 父驱动重新绑定后仍不是 usbccgp，已停止自动安装。日志：{0} | 父驅動程式重新綁定後仍不是 usbccgp，已停止自動安裝。記錄：{0} | 父驅動程式重新綁定後仍不是 usbccgp，已停止自動安裝。記錄檔：{0} | The parent driver is still not usbccgp after repair; automatic installation stopped. Log: {0} | — | ✅ 完全一致 | 保留 |
| ParentReset | 重建设备 | 重建裝置 | 重建裝置 | Rebuild device | — | ✅ 完全一致 | 保留 |
| ParentResetComplete | 已请求移除父设备。请拔下并重新连接手机，让 Windows 重新枚举，再检查父驱动状态。 | 已要求移除父裝置。請拔下並重新連接手機，讓 Windows 重新列舉，再檢查父驅動程式狀態。 | 已要求移除父裝置。請拔下並重新連線手機，讓 Windows 重新列舉，再檢查父驅動程式狀態。 | Parent device removal was requested. Unplug and reconnect the phone so Windows can enumerate it again, then check its parent driver status. | — | ✅ 完全一致 | 保留 |
| ParentResetHelp | “重建设备”会移除所选父设备及其子接口，随后需重新连接手机。原驱动包会保留，Windows 可能再次选择原驱动；更换绑定请选择上方驱动。 | 「重建裝置」會移除選取的父裝置及其子介面，之後需重新連接手機。原驅動程式套件會保留，Windows 可能再次選取原驅動程式；變更綁定請選取上方驅動程式。 | 「重建裝置」會移除選取的父裝置及其子介面，之後需重新連線手機。原驅動程式套件會保留，Windows 可能再次選取原驅動程式；變更綁定請選取上方驅動程式。 | Rebuild device removes the selected parent and its child interfaces. Reconnect the phone afterward. Driver packages remain installed, so Windows may choose the same driver again; select a driver above to change the binding. | — | ✅ 完全一致 | 保留 |
| ParentResetRestartRequired | Windows 要求重启电脑以完成父设备移除。重启后重新连接手机并检测。 | Windows 要求重新啟動電腦以完成父裝置移除。重新啟動後重新連接手機並檢測。 | Windows 要求重新啟動電腦以完成父裝置移除。重新啟動後重新連線手機並檢測。 | Windows requires a PC restart to finish removing the parent device. Reconnect the phone and refresh afterward. | — | ✅ 完全一致 | 保留 |
| ParentRollbackRestartRequired | 父驱动绑定失败；Windows 已接受恢复原绑定，但需要重启电脑。重启后请重新检测。 | 父驅動程式綁定失敗；Windows 已接受還原原綁定，但需要重新啟動電腦。重新啟動後請再次檢測。 | 父驅動程式綁定失敗；Windows 已接受還原原綁定，但需要重新啟動電腦。重新啟動後請再次檢測。 | Parent driver binding failed. Windows accepted restoration of the previous binding, but a PC restart is required. Refresh afterward to verify recovery. | — | ✅ 完全一致 | 保留 |
| ParentStatusFormat | 父驱动：{0} · INF：{1}<br>故障代码：{2} · {3} | 父驅動程式：{0} · INF：{1}<br>問題代碼：{2} · {3} | 父驅動程式：{0} · INF：{1}<br>問題代碼：{2} · {3} | Parent: {0} · INF: {1}<br>Problem code: {2} · {3} | — | ✅ 完全一致 | 保留 |
| PrepareDescription | 自动检测并安装 Apple USB 支持、共享采集驱动，以及当前已连接 iPhone 的采集过滤器。 | 自動偵測並安裝 Apple USB 支援、共用擷取驅動程式，以及目前已連接 iPhone 的擷取過濾器。 | 自動偵測並安裝 Apple USB 支援、共用擷取驅動程式，以及目前已連線 iPhone 的擷取篩選器。 | Automatically detect and install Apple USB support, the shared capture driver, and the capture filter for the connected iPhone. | — | ✅ 完全一致 | 保留 |
| PrepareTitle | 一键准备投屏环境 | 一按準備螢幕鏡像環境 | 一鍵準備螢幕鏡像環境 | Prepare mirroring environment | — | ✅ 完全一致 | 保留 |
| QuickInstallBody | 工具将检查 Apple USB 支持、共享采集驱动和当前选中的 iPhone，并安装缺失项。父驱动异常时，会显示当前绑定和目标驱动，待你单独确认后修复。 | 工具將檢查 Apple USB 支援、共用擷取驅動程式及目前選取的 iPhone，並安裝遺失項目。父驅動程式異常時，會顯示目前綁定及目標驅動程式，待你另行確認後修復。 | 工具將檢查 Apple USB 支援、共用擷取驅動程式及目前選取的 iPhone，並安裝遺失項目。父驅動程式異常時，會顯示目前綁定及目標驅動程式，待您另行確認後修復。 | The tool checks Apple USB support, the shared capture driver, and the selected iPhone, then installs missing components. If the parent driver needs repair, you will see the current binding and target driver and be asked to confirm that change separately. | — | ✅ 完全一致 | 保留 |
| QuickInstallComplete | 一键安装完成，{0} 的投屏驱动已就绪。 | 一按安裝完成，{0} 的螢幕鏡像驅動程式已就緒。 | 一鍵安裝完成，{0} 的螢幕鏡像驅動程式已就緒。 | Installation complete. The mirroring driver for {0} is ready. | — | ✅ 完全一致 | 保留 |
| QuickInstallTitle | 一键安装全部驱动 | 一按安裝全部驅動程式 | 一鍵安裝全部驅動程式 | Install all drivers | — | ✅ 完全一致 | 保留 |
| Recheck | 重新检测 | 重新偵測 | 重新偵測 | Recheck | — | ✅ 完全一致 | 保留 |
| ReconnectBody | 已检测到设备断开。现在请重新连接 iPhone，解锁设备，并在手机出现提示时点击“信任此电脑”。 | 已偵測到裝置中斷連接。現在請重新連接 iPhone，解鎖裝置，並在手機出現提示時按一下「信任這部電腦」。 | 已偵測到裝置中斷連線。現在請重新連線 iPhone，解鎖裝置，並在手機出現提示時按一下「信任這台電腦」。 | The device disconnected. Reconnect and unlock the iPhone, then tap Trust if prompted. | — | ✅ 完全一致 | 保留 |
| ReconnectTimeout | 等待设备重新连接超时。日志：{0} | 等待裝置重新連接逾時。記錄：{0} | 等待裝置重新連線逾時。記錄檔：{0} | Timed out waiting for the device to reconnect. Log: {0} | — | ✅ 完全一致 | 保留 |
| ReconnectTitle | 请重新连接 | 請重新連接 | 請重新連線 | Reconnect the device | — | ✅ 完全一致 | 保留 |
| ReconnectVerificationFailed | 设备重连验证未通过：请检查连接、设备故障代码、父驱动绑定和采集过滤器状态。 | 裝置重新連接驗證未通過：請檢查連接、裝置錯誤代碼、父驅動程式綁定及擷取過濾器狀態。 | 裝置重新連線驗證未通過：請檢查連線、裝置錯誤代碼、父驅動程式綁定及擷取篩選器狀態。 | Reconnect verification failed. Check the connection, device problem code, parent driver binding, and capture filter state. | — | ✅ 完全一致 | 保留 |
| Reconnected | 已经连接 | 已經連接 | 已經連線 | Reconnected | — | ✅ 完全一致 | 保留 |
| Refresh | 刷新 | 重新整理 | 重新整理 | Refresh | — | ✅ 完全一致 | 保留 |
| Repair | 修复 | 修復 | 修復 | Repair | — | ✅ 完全一致 | 保留 |
| RepairingDriver | 正在为 {0} 修复采集驱动… | 正在為 {0} 修復擷取驅動程式… | 正在為 {0} 修復擷取驅動程式… | Repairing the capture driver for {0}… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| RepairingParent | 正在修复 {0} 的父驱动… | 正在修復 {0} 的父驅動程式… | 正在修復 {0} 的父驅動程式… | Repairing the parent driver for {0}… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| ReplugTimeout | 等待设备拔下或重新连接超时。驱动操作本身已经回滚或完成，请查看日志后重试。 | 等待裝置拔下或重新連接逾時。驅動程式操作本身已經復原或完成，請查看記錄後重試。 | 等待裝置拔下或重新連線逾時。驅動程式操作本身已經復原或完成，請查看記錄檔後重試。 | Timed out waiting for the device to disconnect or reconnect. The operation was rolled back or completed; review the log and retry. | — | ✅ 完全一致 | 保留 |
| RequiredActionTitle | 需要你的操作 | 需要你執行操作 | 需要您執行操作 | Action required | — | ✅ 完全一致 | 保留 |
| RequiredAppleInstallBody | 请完成当前这一步后点击“重新检测”。工具不会自动操作商店窗口。 | 請完成目前這一步後按一下「重新偵測」。工具不會自動控制商店視窗。 | 請完成目前這一步後按一下「重新偵測」。工具不會自動控制商店視窗。 | Complete this step, then click Recheck. The tool will not operate the store window for you. | — | ✅ 完全一致 | 保留 |
| RequiredAppleInstallTitle | 需要完成 Apple 官方安装 | 需要完成 Apple 官方安裝 | 需要完成 Apple 官方安裝 | Apple installation required | — | ✅ 完全一致 | 保留 |
| Retry | 重新检测 | 重新偵測 | 重新偵測 | Recheck | — | ❌ 术语不一致 → ✅ 已修复 | 相同重新检测动作与 Recheck 按钮一致 |
| RunDriverCleanup | 清理设备驱动 | 清理裝置驅動程式 | 清理裝置驅動程式 | Clean up device drivers | — | ✅ 完全一致 | 保留 |
| SelectedDeviceDisconnected | 选中的设备已经断开，请重新选择后再试。日志：{0} | 選取的裝置已中斷連接，請重新選擇後再試。記錄：{0} | 選取的裝置已中斷連線，請重新選擇後再試。記錄檔：{0} | The selected device disconnected. Select it again and retry. Log: {0} | — | ✅ 完全一致 | 保留 |
| SimpleNoDevice | 连接 iPhone 后点击按钮，工具会自动完成检测和安装。 | 連接 iPhone 後按一下按鈕，工具會自動完成偵測和安裝。 | 連線 iPhone 後按一下按鈕，工具會自動完成偵測和安裝。 | Connect an iPhone and click the button. The tool will detect and install what is missing. | — | ✅ 完全一致 | 保留 |
| StartDetection | 开始检测 | 開始偵測 | 開始偵測 | Start detection | — | ✅ 完全一致 | 保留 |
| StartInstall | 开始安装 | 開始安裝 | 開始安裝 | Start installation | — | ✅ 完全一致 | 保留 |
| ThemeDark | 深色 | 深色 | 深色 | Dark | — | ✅ 完全一致 | 保留 |
| ThemeLight | 浅色 | 淺色 | 淺色 | Light | — | ✅ 完全一致 | 保留 |
| ThemeSystem | 跟随系统 | 跟隨系統 | 跟隨系統 | Use system setting | — | ✅ 完全一致 | 保留 |
| UacCancelled | 用户取消了 Windows 管理员授权。 | 使用者取消了 Windows 系統管理員授權。 | 使用者取消了 Windows 系統管理員授權。 | Windows administrator approval was cancelled. | — | ✅ 完全一致 | 保留 |
| Uninstall | 卸载 | 解除安裝 | 解除安裝 | Uninstall | — | ✅ 完全一致 | 保留 |
| UnknownVersion | 未知版本 | 未知版本 | 未知版本 | unknown version | — | ✅ 完全一致 | 保留 |
| UnplugBody | 驱动更改已经完成。现在请拔掉这台 iPhone 的数据线，等待设备从列表中消失。 | 驅動程式更改已經完成。現在請拔除這部 iPhone 的數據線，等待裝置從清單中消失。 | 驅動程式更改已經完成。現在請拔除這台 iPhone 的傳輸線，等待裝置從清單中消失。 | The driver change is complete. Unplug this iPhone and wait for it to disappear from the device list. | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| UnplugTitle | 请拔线 | 請拔除數據線 | 請拔除傳輸線 | Unplug the device | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| Unplugged | 已经拔线 | 已拔除數據線 | 已拔除傳輸線 | Unplugged | — | ❌ 术语不一致 → ✅ 已修复 | 统一香港繁体中文既有用语 |
| WaitDeviceTimeout | 等待 Apple 设备连接超时。日志：{0} | 等待 Apple 裝置連接逾時。記錄：{0} | 等待 Apple 裝置連線逾時。記錄檔：{0} | Timed out waiting for an Apple device. Log: {0} | — | ✅ 完全一致 | 保留 |
| WaitingDisconnect | 等待设备断开… | 等待裝置中斷連接… | 等待裝置中斷連線… | Waiting for the device to disconnect… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WaitingForDevice | 等待 Apple 设备连接… | 等待 Apple 裝置連接… | 等待 Apple 裝置連線… | Waiting for an Apple device… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |
| WaitingReconnect | 等待设备重新连接… | 等待裝置重新連接… | 等待裝置重新連線… | Waiting for the device to reconnect… | — | ⚠️ 需要优化 → ✅ 已修复 | 统一进行中状态的省略号 |

### Cleanup

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| AssociatedDrivers | 关联驱动包总数：{0} | 關聯驅動程式套件總數：{0} | 關聯驅動程式套件總數：{0} | Total associated driver packages: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| AssociatedNodes | 关联 PnP 节点：{0} | 關聯 PnP 節點：{0} | 關聯 PnP 節點：{0} | Associated PnP nodes: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| BluetoothExcluded | 已安全排除 BTHLE 设备：{0} | 已安全排除 BTHLE 裝置：{0} | 已安全排除 BTHLE 裝置：{0} | BTHLE device safely excluded: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| BluetoothNotListed | BTHLE / Bluetooth LE 设备不会显示。 | BTHLE / Bluetooth LE 裝置不會顯示。 | BTHLE / Bluetooth LE 裝置不會顯示。 | BTHLE / Bluetooth LE devices are not listed. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| CheckRequirements | 请确认： | 請確認： | 請確認： | Check the following: | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| CleanupPlan |  清理计划 |  清理計劃 |  清理計劃 |  Cleanup plan | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| CleanupResults |  清理结果 |  清理結果 |  清理結果 |  Cleanup results | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ClosingProcesses | 正在关闭 iPhoneMirror 相关进程… | 正在關閉 iPhoneMirror 相關程序… | 正在關閉 iPhoneMirror 相關程序… | Closing iPhoneMirror processes… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Completed |  清理完成。 |  清理完成。 |  清理完成。 |  Cleanup completed successfully. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| CompletedWithErrors |  清理结束，发生 {0} 个错误。 |  清理結束，發生 {0} 個錯誤。 |  清理結束，發生 {0} 個錯誤。 |  Cleanup finished. Errors: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Confirm | 请输入 {0} 确认： | 請輸入 {0} 確認： | 請輸入 {0} 確認： | Type {0} to confirm: | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ConfirmationMismatch | 确认文字不匹配，未做任何修改。 | 確認文字不符，未作任何修改。 | 確認文字不符，未作任何修改。 | The confirmation text did not match. No changes were made. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DetectedDevices | 检测到以下 Apple 物理设备： | 偵測到以下 Apple 實體裝置： | 偵測到以下 Apple 實體裝置： | Detected Apple physical devices: | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DeviceNode | 设备节点：{0} | 裝置節點：{0} | 裝置節點：{0} | Device node: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DeviceNodeFailed | 设备节点删除失败：{0} | 裝置節點刪除失敗：{0} | 裝置節點刪除失敗：{0} | Could not remove device node: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DeviceNodeFailedDetail | 设备节点删除失败：{0}  | 裝置節點刪除失敗：{0}  | 裝置節點刪除失敗：{0}  | Could not remove device node: {0}  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DeviceNodeRestart | 设备节点已移除，重启后完成：{0} | 裝置節點已移除，重新啟動後完成：{0} | 裝置節點已移除，重新啟動後完成：{0} | Device node removed; a restart is required to finish: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverEnumerationFailed | pnputil Driver Store 枚举失败，退出代码为 {0}； | pnputil Driver Store 列舉失敗，結束代碼為 {0}； | pnputil Driver Store 列舉失敗，結束代碼為 {0}； | pnputil Driver Store enumeration failed (exit code {0}).  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverIndexReady | Driver Store 索引完成：{0} 个 OEM INF | Driver Store 索引完成：{0} 個 OEM INF | Driver Store 索引完成：{0} 個 OEM INF | Driver Store index ready. OEM INF files: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverNodesMissing | Driver Store XML 中没有 Driver 节点，已停止清理，以免遗漏驱动包。 | Driver Store XML 中沒有 Driver 節點，已停止清理，以免遺漏驅動程式套件。 | Driver Store XML 中沒有 Driver 節點，已停止清理，以免遺漏驅動程式套件。 | Driver Store XML has no Driver nodes. Cleanup stopped to avoid missing driver packages. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverOperationBusy | 另一个驱动操作或回滚仍在进行，请等待完成后重试清理。 | 另一個驅動操作或復原仍在進行，請等待完成後重試清理。 | 另一個驅動操作或復原仍在進行，請等待完成後重試清理。 | Another driver operation or rollback is in progress. Wait for it to finish before retrying cleanup. | — | ✅ 完全一致 | 保留 |
| DriverPackage | 驱动包：{0} | 驅動程式套件：{0} | 驅動程式套件：{0} | Driver package: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverPackageFailed | 驱动包删除失败：{0} | 驅動程式套件刪除失敗：{0} | 驅動程式套件刪除失敗：{0} | Could not remove driver package: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverPackageFailedDetail | 驱动包删除失败：{0}  | 驅動程式套件刪除失敗：{0}  | 驅動程式套件刪除失敗：{0}  | Could not remove driver package: {0}  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverPackageRestart | 驱动包已删除，重启后完成：{0} | 驅動程式套件已刪除，重新啟動後完成：{0} | 驅動程式套件已刪除，重新啟動後完成：{0} | Driver package removed; a restart is required to finish: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverStoreCount |     Driver Store 驱动包：{0} |     Driver Store 驅動程式套件：{0} |     Driver Store 驅動程式套件：{0} |     Driver Store packages: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverXmlEmpty | Driver Store XML 为空。 | Driver Store XML 為空。 | Driver Store XML 為空。 | Driver Store XML is empty. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverXmlEmptyStopped | Driver Store XML 为空，已停止清理，以免遗漏驱动包。 | Driver Store XML 為空，已停止清理，以免遺漏驅動程式套件。 | Driver Store XML 為空，已停止清理，以免遺漏驅動程式套件。 | Driver Store XML is empty. Cleanup stopped to avoid missing driver packages. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverXmlInvalid | Driver Store XML 解析失败： | Driver Store XML 解析失敗： | Driver Store XML 解析失敗： | Could not parse Driver Store XML:  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverXmlInvalidStopped | Driver Store XML 解析失败，已停止清理： | Driver Store XML 解析失敗，已停止清理： | Driver Store XML 解析失敗，已停止清理： | Could not parse Driver Store XML. Cleanup stopped:  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| DriverXmlUnavailable | 无法使用 Driver Store XML，退出代码为 {0} | 無法使用 Driver Store XML，結束代碼為 {0} | 無法使用 Driver Store XML，結束代碼為 {0} | Driver Store XML is unavailable. Exit code: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Error | 错误：{0} | 錯誤：{0} | 錯誤：{0} | Error: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ErrorLog | 错误日志：{0} | 錯誤記錄：{0} | 錯誤記錄檔：{0} | Error log: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| FatalError | iPhoneMirror 驱动清理严重错误 | iPhoneMirror 驅動程式清理嚴重錯誤 | iPhoneMirror 驅動程式清理嚴重錯誤 | iPhoneMirror driver cleanup fatal error | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| FinalDeviceCheck | 正在执行删除前的最终设备确认… | 正在執行刪除前的最終裝置確認… | 正在執行刪除前的最終裝置確認… | Performing the final device check before removal… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| FinalVerification | 正在执行最终验证… | 正在執行最終驗證… | 正在執行最終驗證… | Performing final verification… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| IndexDriverStore | 正在建立 Driver Store 驱动索引… | 正在建立 Driver Store 驅動程式索引… | 正在建立 Driver Store 驅動程式索引… | Indexing Driver Store packages… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| InternalError |  清理工具内部错误 |  清理工具內部錯誤 |  清理工具內部錯誤 |  Cleanup tool internal error | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| InvalidDeviceNumber | 设备序号无效。 | 裝置編號無效。 | 裝置編號無效。 | Invalid device number. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Irreversible | 此操作不可撤销。 | 此操作無法復原。 | 此操作無法復原。 | This operation cannot be undone. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ListOnly | 仅列表模式，未修改系统。 | 僅清單模式，未修改系統。 | 僅清單模式，未修改系統。 | List-only mode. No system changes were made. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Location | 位置： | 位置： | 位置： | Location: | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| LogPath | 日志：{0} | 記錄：{0} | 記錄檔：{0} | Log: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ManifestWriteFailed | 无法写入清单： | 無法寫入清單： | 無法寫入清單： | Could not write the manifest:  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| MappingDevices | 正在建立 Apple 设备关系… | 正在建立 Apple 裝置關係… | 正在建立 Apple 裝置關係… | Mapping Apple device relationships… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| MappingDrivers | 正在建立驱动关系… | 正在建立驅動程式關係… | 正在建立驅動程式關係… | Mapping driver relationships… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| MappingReady | 设备关系建立完成。 | 裝置關係建立完成。 | 裝置關係建立完成。 | Device relationships are ready. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NoAppleDevice | 未找到当前连接的 iPhone/iPad。 | 找不到目前連接的 iPhone/iPad。 | 找不到目前連線的 iPhone/iPad。 | No connected iPhone/iPad was found. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NoAvailablePnp | pnputil PnP XML 中没有可用设备。 | pnputil PnP XML 中沒有可用裝置。 | pnputil PnP XML 中沒有可用裝置。 | No available devices were found in pnputil PnP XML. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NoCachedPnp | pnputil PnP 缓存完成：0 个设备 | pnputil PnP 快取完成：0 個裝置 | pnputil PnP 快取完成：0 個裝置 | pnputil PnP cache ready. Devices: 0 | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NoConnectedPnp | pnputil PnP XML 中没有当前连接的设备。 | pnputil PnP XML 中沒有目前連接的裝置。 | pnputil PnP XML 中沒有目前連線的裝置。 | No connected devices were found in pnputil PnP XML. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NoRemovableNodes | 没有可删除的目标 PnP 节点。 | 沒有可刪除的目標 PnP 節點。 | 沒有可刪除的目標 PnP 節點。 | There are no target PnP nodes to remove. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| NodesRemoved | 目标 PnP 节点已清理。 | 目標 PnP 節點已清理。 | 目標 PnP 節點已清理。 | Target PnP nodes were removed. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| OnePhysicalDevice | 物理设备：1 台 | 實體裝置：1 部 | 實體裝置：1 部 | Physical devices: 1 | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PackagesHandled | 目标 Driver Store 驱动包已处理。 | 目標 Driver Store 驅動程式套件已處理。 | 目標 Driver Store 驅動程式套件已處理。 | Target Driver Store packages were processed. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PackagesPreservedUnsupported | 此 Windows 版本无法完整列出驱动包使用者；将只清理所选设备节点，保留驱动包。 | 此 Windows 版本無法完整列出驅動程式套件使用者；只會清理所選裝置節點，保留驅動程式套件。 | 此 Windows 版本無法完整列出驅動程式套件使用者；只會清理所選裝置節點，保留驅動程式套件。 | This Windows version cannot list all driver package users. Only selected device nodes will be removed; driver packages will be preserved. | — | ✅ 完全一致 | 保留 |
| Pause | 按 Enter 键关闭窗口 | 按 Enter 鍵關閉視窗 | 按 Enter 鍵關閉視窗 | Press Enter to close this window | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PhysicalDeviceCount | Apple 物理设备分组完成：{0} 台 | Apple 實體裝置分組完成：{0} 部 | Apple 實體裝置分組完成：{0} 部 | Apple physical devices grouped: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpCached | PnP 缓存完成：{0} 个设备 | PnP 快取完成：{0} 個裝置 | PnP 快取完成：{0} 個裝置 | PnP cache ready. Devices: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpEnumerationFailed | 无法枚举 PnP 设备，pnputil 退出代码为 {0} | 無法列舉 PnP 裝置，pnputil 結束代碼為 {0} | 無法列舉 PnP 裝置，pnputil 結束代碼為 {0} | Could not enumerate PnP devices. pnputil exit code: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpFallback | Get-PnpDevice 扫描失败，正在改用 pnputil： | Get-PnpDevice 掃描失敗，正在改用 pnputil： | Get-PnpDevice 掃描失敗，正在改用 pnputil： | Get-PnpDevice scan failed; switching to pnputil:  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpNodeCount |     PnP 节点：{0} |     PnP 節點：{0} |     PnP 節點：{0} |     PnP nodes: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpUtilCached | pnputil PnP 缓存完成：{0} 个设备 | pnputil PnP 快取完成：{0} 個裝置 | pnputil PnP 快取完成：{0} 個裝置 | pnputil PnP cache ready. Devices: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpUtilMissing | 找不到 pnputil.exe：{0} | 找不到 pnputil.exe：{0} | 找不到 pnputil.exe：{0} | pnputil.exe was not found: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpXmlEmpty | pnputil PnP XML 为空，无法枚举设备。 | pnputil PnP XML 為空，無法列舉裝置。 | pnputil PnP XML 為空，無法列舉裝置。 | pnputil PnP XML is empty; devices cannot be enumerated. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PnpXmlInvalid | pnputil PnP XML 解析失败： | pnputil PnP XML 解析失敗： | pnputil PnP XML 解析失敗： | Could not parse pnputil PnP XML:  | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| PreviewOnly |  仅预览，未修改系统 |  僅預覽，未修改系統 |  僅預覽，未修改系統 |  Preview only. No system changes were made. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ProtectedLaunchRequired | 请通过 iPhoneMirror.Driver.exe 或发布包中的清理入口运行此工具，以获得受保护的管理员权限。 | 請透過 iPhoneMirror.Driver.exe 或發佈套件中的清理入口執行此工具，以取得受保護的管理員權限。 | 請透過 iPhoneMirror.Driver.exe 或發布套件中的清理入口執行此工具，以取得受保護的管理員權限。 | Run this tool through iPhoneMirror.Driver.exe or the cleanup entry in the release package to obtain protected administrator privileges. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ReinstallAdvice | 完成后可重新安装 Apple Devices 或 iTunes，以恢复所需驱动。 | 完成後可重新安裝 Apple Devices 或 iTunes，以還原所需驅動程式。 | 完成後可重新安裝 Apple Devices 或 iTunes，以還原所需驅動程式。 | Afterward, reinstall Apple Devices or iTunes to restore the required drivers. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RemainingNodes | 仍存在 {0} 个目标节点。 | 仍存在 {0} 個目標節點。 | 仍存在 {0} 個目標節點。 | Target nodes remaining: {0}. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RemainingPackages | 仍存在 {0} 个驱动包。 | 仍存在 {0} 個驅動程式套件。 | 仍存在 {0} 個驅動程式套件。 | Driver packages remaining: {0}. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RemovingNodes |  正在卸载设备节点 |  正在卸載裝置節點 |  正在卸載裝置節點 |  Removing device nodes | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RemovingPackages |  正在删除 Driver Store 驱动包 |  正在刪除 Driver Store 驅動程式套件 |  正在刪除 Driver Store 驅動程式套件 |  Removing Driver Store packages | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RequirementCable |   4. 数据线支持数据传输 |   4. 數據線支援資料傳輸 |   4. 傳輸線支援資料傳輸 |   4. The cable supports data transfer | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RequirementTrusted |   3. 已在设备上选择“信任此电脑” |   3. 已在裝置上選擇「信任此電腦」 |   3. 已在裝置上選擇「信任此電腦」 |   3. You have trusted this computer on the device | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RequirementUnlocked |   2. 设备已解锁 |   2. 裝置已解鎖 |   2. 裝置已解鎖 |   2. The device is unlocked | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RequirementUsb |   1. iPhone/iPad 已通过 USB 连接 |   1. iPhone/iPad 已透過 USB 連接 |   1. iPhone/iPad 已透過 USB 連線 |   1. The iPhone/iPad is connected by USB | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| RestartRequired | Windows 报告部分操作需要重启。 | Windows 報告部分操作需要重新啟動。 | Windows 報告部分操作需要重新啟動。 | Windows reported that some operations require a restart. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ScanPnp | 正在扫描当前 PnP 设备… | 正在掃描目前 PnP 裝置… | 正在掃描目前 PnP 裝置… | Scanning current PnP devices… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ScanningDevices | 正在扫描设备… | 正在掃描裝置… | 正在掃描裝置… | Scanning devices… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| ScopeWarning | 将清理所选设备的关联设备节点及可安全移除的驱动包，可能包括 Apple 官方驱动；其他设备仍在使用的驱动包会保留。 | 將清理所選裝置的關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式；其他裝置仍在使用的驅動程式套件會保留。 | 將清理所選裝置的關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式；其他裝置仍在使用的驅動程式套件會保留。 | Cleanup will remove device nodes associated with the selected device and driver packages that can be safely removed, which may include official Apple drivers. Packages still used by other devices will be preserved. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| SelectDevice | 请输入设备序号；输入 Q 取消 | 請輸入裝置編號；輸入 Q 取消 | 請輸入裝置編號；輸入 Q 取消 | Enter a device number, or Q to cancel | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| SelectedDevice | 设备：{0} | 裝置：{0} | 裝置：{0} | Device: {0} | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| SelectedDisconnected | 所选 iPhone 已断开。 | 所選 iPhone 已中斷連線。 | 所選 iPhone 已中斷連線。 | The selected iPhone disconnected. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| SharedPackagePreserved | 驱动包 {0} 正被其他设备使用，已保留。 | 驅動程式套件 {0} 正被其他裝置使用，已保留。 | 驅動程式套件 {0} 正被其他裝置使用，已保留。 | Driver package {0} is used by other devices and has been preserved. | — | ✅ 完全一致 | 保留 |
| StackTrace | 调用栈： | 呼叫堆疊： | 呼叫堆疊： | Stack trace: | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| StopMissingPackages | 已停止清理，以免遗漏驱动包。 | 已停止清理，以免遺漏驅動程式套件。 | 已停止清理，以免遺漏驅動程式套件。 | Cleanup stopped to avoid missing driver packages. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| Title |  清除 iPhone/iPad 关联驱动 |  清除 iPhone/iPad 關聯驅動程式 |  清除 iPhone/iPad 關聯驅動程式 |  Remove iPhone/iPad associated drivers | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| UnresolvedNodes | 仍存在 {0} 个目标 PnP 节点。 | 仍存在 {0} 個目標 PnP 節點。 | 仍存在 {0} 個目標 PnP 節點。 | Target PnP nodes remaining: {0}. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| UnresolvedPackages | 仍存在 {0} 个目标 Driver Store 驱动包。 | 仍存在 {0} 個目標 Driver Store 驅動程式套件。 | 仍存在 {0} 個目標 Driver Store 驅動程式套件。 | Target Driver Store packages remaining: {0}. | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |
| WaitingWindows | 正在等待 Windows 更新设备状态… | 正在等待 Windows 更新裝置狀態… | 正在等待 Windows 更新裝置狀態… | Waiting for Windows to update device status… | — | ❌ 缺失 → ✅ 已修复 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 |

### Installer

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CustomMessages/AdditionalIcons | 附加快捷方式： | 附加圖示: | 附加圖示: | Additional shortcuts: | — | ✅ 完全一致 | 保留 |
| CustomMessages/AddonHostProgramNotFound | 您选择的文件夹中无法找到 %1。%n%n您要继续吗？ | %1 無法在您所選的資料夾中找到。%n%n您是否還要繼續？ | %1 無法在您所選的資料夾中找到。%n%n您是否還要繼續？ | %1 could not be located in the folder you selected.%n%nDo you want to continue anyway? | — | ✅ 完全一致 | 保留 |
| CustomMessages/AssocFileExtension | 将 %2 文件扩展名与 %1 建立关联(&amp;A) | 將 %1 與檔案副檔名 %2 產生關聯(&amp;A) | 將 %1 與檔案副檔名 %2 產生關聯(&amp;A) | &amp;Associate %1 with the %2 file extension | — | ✅ 完全一致 | 保留 |
| CustomMessages/AssocingFileExtension | 正在将 %2 文件扩展名与 %1 建立关联... | 正在將 %1 與檔案副檔名 %2 產生關聯... | 正在將 %1 與檔案副檔名 %2 產生關聯... | Associating %1 with the %2 file extension... | — | ✅ 完全一致 | 保留 |
| CustomMessages/AutoStartProgram | 自动启动 %1 | 自動開啟 %1 | 自動開啟 %1 | Automatically start %1 | — | ✅ 完全一致 | 保留 |
| CustomMessages/AutoStartProgramGroupDescription | 启动： | 開啟: | 開啟: | Startup: | — | ✅ 完全一致 | 保留 |
| CustomMessages/CreateDesktopIcon | 创建桌面快捷方式(&amp;D) | 建立桌面捷徑(&amp;D) | 建立桌面捷徑(&amp;D) | Create a &amp;desktop shortcut | — | ✅ 完全一致 | 保留 |
| CustomMessages/CreateQuickLaunchIcon | 创建快速启动栏快捷方式(&amp;Q) | 建立快速啟動圖示(&amp;Q) | 建立快速啟動圖示(&amp;Q) | Create a &amp;Quick Launch shortcut | — | ✅ 完全一致 | 保留 |
| CustomMessages/DeleteUserDataPrompt | 是否同时删除 iPhoneMirror 的用户配置和已下载更新？选择“否”将保留这些数据，以便以后重新安装。 | 是否同時刪除 iPhoneMirror 的使用者設定和已下載的更新？選擇「否」會保留這些資料，以便日後重新安裝。 | 是否同時刪除 iPhoneMirror 的使用者設定和已下載的更新？選擇「否」會保留這些資料，以便日後重新安裝。 | Also delete iPhoneMirror settings and downloaded updates? Choose No to keep this data for a later reinstall. | — | ✅ 完全一致 | 保留 |
| CustomMessages/LaunchProgram | 运行 %1 | 啟動 %1 | 啟動 %1 | Launch %1 | — | ✅ 完全一致 | 保留 |
| CustomMessages/NameAndVersion | %1 版本 %2 | %1 版本 %2 | %1 版本 %2 | %1 version %2 | — | ✅ 完全一致 | 保留 |
| CustomMessages/ProgramOnTheWeb | %1 网站 | %1 的網站 | %1 的網站 | %1 on the Web | — | ✅ 完全一致 | 保留 |
| CustomMessages/UninstallProgram | 卸载 %1 | 解除安裝 %1 | 解除安裝 %1 | Uninstall %1 | — | ✅ 完全一致 | 保留 |
| Icons/Changelog | 更新日志 | 更新記錄 | 更新記錄 | Changelog | — | ✅ 完全一致 | 保留 |
| Icons/Uninstall | 卸载 | 解除安裝 | 解除安裝 | Uninstall | — | ✅ 完全一致 | 保留 |
| Messages/AbortRetryIgnoreCancel | 关闭安装程序 | 取消安裝 | 取消安裝 | Cancel installation | — | ✅ 完全一致 | 保留 |
| Messages/AbortRetryIgnoreIgnore | 忽略错误并继续(&amp;I) | 略過錯誤並繼續 (&amp;I) | 略過錯誤並繼續 (&amp;I) | &amp;Ignore the error and continue | — | ✅ 完全一致 | 保留 |
| Messages/AbortRetryIgnoreRetry | 重试(&amp;T) | 請再試一次 (&amp;T) | 請再試一次 (&amp;T) | &amp;Try again | — | ✅ 完全一致 | 保留 |
| Messages/AbortRetryIgnoreSelectAction | 选择操作 | 選取動作 | 選取動作 | Select action | — | ✅ 完全一致 | 保留 |
| Messages/AboutSetupMenuItem | 关于安装程序(&amp;A)... | 關於安裝程式 (&amp;A)... | 關於安裝程式 (&amp;A)... | &amp;About Setup... | — | ✅ 完全一致 | 保留 |
| Messages/AboutSetupMessage | %1 版本 %2%n%3%n%n%1 主页：%n%4 | %1 版本 %2%n%3%n%n%1 網站：%n%4 | %1 版本 %2%n%3%n%n%1 網站：%n%4 | %1 version %2%n%3%n%n%1 home page:%n%4 | — | ✅ 完全一致 | 保留 |
| Messages/AboutSetupNote |  |  |  |  | — | ✅ 完全一致 | 保留；可选语言包备注/标签，允许为空或按语言不同 |
| Messages/AboutSetupTitle | 关于安装程序 | 關於安裝程式 | 關於安裝程式 | About Setup | — | ✅ 完全一致 | 保留 |
| Messages/AdminPrivilegesRequired | 在安装此程序时您必须以管理员身份登录。 | 您必須以系統管理員身分登入以安裝這個程式。 | 您必須以系統管理員身分登入以安裝這個程式。 | You must be logged in as an administrator when installing this program. | — | ✅ 完全一致 | 保留 |
| Messages/ApplicationsFound | 以下应用程序正在使用将由安装程序更新的文件。建议您允许安装程序自动关闭这些应用程序。 | 下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式。 | 下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式。 | The following applications are using files that need to be updated by Setup. It is recommended that you allow Setup to automatically close these applications. | — | ✅ 完全一致 | 保留 |
| Messages/ApplicationsFound2 | 以下应用程序正在使用将由安装程序更新的文件。建议您允许安装程序自动关闭这些应用程序。安装完成后，安装程序将尝试重新启动这些应用程序。 | 下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式；安裝完成後，安裝程式會嘗試重新開啟它們。 | 下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式；安裝完成後，安裝程式會嘗試重新開啟它們。 | The following applications are using files that need to be updated by Setup. It is recommended that you allow Setup to automatically close these applications. After the installation has completed, Setup will attempt to restart the applications. | — | ✅ 完全一致 | 保留 |
| Messages/ArchiveIncorrectPassword | 压缩文件密码不正确 | 密碼不正確 | 密碼不正確 | The password is incorrect | — | ✅ 完全一致 | 保留 |
| Messages/ArchiveIsCorrupted | 压缩文件已损坏 | 壓縮檔已損毀 | 壓縮檔已損毀 | The archive is corrupted | — | ✅ 完全一致 | 保留 |
| Messages/ArchiveUnsupportedFormat | 不支持的压缩文件格式 | 不支援此壓縮檔格式 | 不支援此壓縮檔格式 | The archive format is unsupported | — | ✅ 完全一致 | 保留 |
| Messages/BadDirName32 | 文件夹名称不能包含下列任何字符：%n%n%1 | 資料夾名稱不得包含以下特殊字元:%n%n%1 | 資料夾名稱不得包含以下特殊字元:%n%n%1 | Folder names cannot include any of the following characters:%n%n%1 | — | ✅ 完全一致 | 保留 |
| Messages/BadGroupName | 文件夹名不能包含下列任何字符：%n%n%1 | 資料夾名稱不得包含下列字元:%n%n%1 | 資料夾名稱不得包含下列字元:%n%n%1 | The folder name cannot include any of the following characters:%n%n%1 | — | ✅ 完全一致 | 保留 |
| Messages/BeveledLabel |  |  |  |  | — | ✅ 完全一致 | 保留；可选语言包备注/标签，允许为空或按语言不同 |
| Messages/BrowseDialogLabel | 在下面的列表中选择一个文件夹，然后点击“确定”。 | 請在下方的資料夾清單中選擇一個資料夾，然後按「確定」。 | 請在下方的資料夾清單中選擇一個資料夾，然後按「確定」。 | Select a folder in the list below, then click OK. | — | ✅ 完全一致 | 保留 |
| Messages/BrowseDialogTitle | 浏览文件夹 | 瀏覽資料夾 | 瀏覽資料夾 | Browse For Folder | — | ✅ 完全一致 | 保留 |
| Messages/ButtonBack | &lt; 上一步(&amp;B) | &lt; 上一步(&amp;B) | &lt; 上一步(&amp;B) | &lt; &amp;Back | — | ✅ 完全一致 | 保留 |
| Messages/ButtonBrowse | 浏览(&amp;B)... | 瀏覽 (&amp;B)... | 瀏覽 (&amp;B)... | &amp;Browse... | — | ✅ 完全一致 | 保留 |
| Messages/ButtonCancel | 取消 | 取消 | 取消 | Cancel | — | ✅ 完全一致 | 保留 |
| Messages/ButtonFinish | 完成(&amp;F) | 完成 (&amp;F) | 完成 (&amp;F) | &amp;Finish | — | ✅ 完全一致 | 保留 |
| Messages/ButtonInstall | 安装(&amp;I) | 安裝(&amp;I) | 安裝(&amp;I) | &amp;Install | — | ✅ 完全一致 | 保留 |
| Messages/ButtonNewFolder | 新建文件夹(&amp;M) | 建立新資料夾 (&amp;M) | 建立新資料夾 (&amp;M) | &amp;Make New Folder | — | ✅ 完全一致 | 保留 |
| Messages/ButtonNext | 下一步(&amp;N) &gt; | 下一步(&amp;N)  &gt; | 下一步(&amp;N)  &gt; | &amp;Next &gt; | — | ✅ 完全一致 | 保留 |
| Messages/ButtonNo | 否(&amp;N) | 否(&amp;N) | 否(&amp;N) | &amp;No | — | ✅ 完全一致 | 保留 |
| Messages/ButtonNoToAll | 全否(&amp;O) | 全部皆否 (&amp;O) | 全部皆否 (&amp;O) | N&amp;o to All | — | ✅ 完全一致 | 保留 |
| Messages/ButtonOK | 确定 | 確定 | 確定 | OK | — | ✅ 完全一致 | 保留 |
| Messages/ButtonStopDownload | 停止下载(&amp;S) | 停止下載 (&amp;S) | 停止下載 (&amp;S) | &amp;Stop download | — | ✅ 完全一致 | 保留 |
| Messages/ButtonStopExtraction | 停止解压(&amp;S) | 停止解壓縮 (&amp;S) | 停止解壓縮 (&amp;S) | &amp;Stop extraction | — | ✅ 完全一致 | 保留 |
| Messages/ButtonWizardBrowse | 浏览(&amp;R)... | 瀏覽 (&amp;R)... | 瀏覽 (&amp;R)... | B&amp;rowse... | — | ✅ 完全一致 | 保留 |
| Messages/ButtonYes | 是(&amp;Y) | 是(&amp;Y) | 是(&amp;Y) | &amp;Yes | — | ✅ 完全一致 | 保留 |
| Messages/ButtonYesToAll | 全是(&amp;A) | 全部皆是 (&amp;A) | 全部皆是 (&amp;A) | Yes to &amp;All | — | ✅ 完全一致 | 保留 |
| Messages/CannotContinue | 安装程序不能继续。请点击“取消”退出。 | 安裝程式無法繼續。請按 「取消」 離開。 | 安裝程式無法繼續。請按 「取消」 離開。 | Setup cannot continue. Please click Cancel to exit. | — | ✅ 完全一致 | 保留 |
| Messages/CannotInstallToNetworkDrive | 安装程序无法安装到一个网络驱动器。 | 安裝程式無法安裝到網絡磁碟機。 | 安裝程式無法安裝到網路磁碟機。 | Setup cannot install to a network drive. | — | ✅ 完全一致 | 保留 |
| Messages/CannotInstallToUNCPath | 安装程序无法安装到一个 UNC 路径。 | 安裝程式無法安裝到 UNC 路徑。 | 安裝程式無法安裝到 UNC 路徑。 | Setup cannot install to a UNC path. | — | ✅ 完全一致 | 保留 |
| Messages/ChangeDiskTitle | 安装程序需要下一张磁盘 | 安裝程式需要下一張磁碟 | 安裝程式需要下一張磁碟 | Setup Needs the Next Disk | — | ✅ 完全一致 | 保留 |
| Messages/ClickFinish | 点击“完成”退出安装程序。 | 按 「完成」 以結束安裝程式。 | 按 「完成」 以結束安裝程式。 | Click Finish to exit Setup. | — | ✅ 完全一致 | 保留 |
| Messages/ClickNext | 点击“下一步”继续，或点击“取消”退出安装程序。 | 按 「下一步」 繼續安裝，或按 「取消」 結束安裝程式。 | 按 「下一步」 繼續安裝，或按 「取消」 結束安裝程式。 | Click Next to continue, or Cancel to exit Setup. | — | ✅ 完全一致 | 保留 |
| Messages/CloseApplications | 自动关闭应用程序(&amp;A) | 自動關閉應用程式 (&amp;A) | 自動關閉應用程式 (&amp;A) | &amp;Automatically close the applications | — | ❌ 含义不一致 → ✅ 已修复 | 补齐自动关闭的行为 |
| Messages/CompactInstallation | 简洁安装 | 最小安裝 | 最小安裝 | Compact installation | — | ✅ 完全一致 | 保留 |
| Messages/ComponentSize1 | %1 KB | %1 KB | %1 KB | %1 KB | — | ✅ 完全一致 | 保留 |
| Messages/ComponentSize2 | %1 MB | %1 MB | %1 MB | %1 MB | — | ✅ 完全一致 | 保留 |
| Messages/ComponentsDiskSpaceGBLabel | 当前选择的组件需要至少 [gb] GB 的磁盘空间。 | 目前的選擇需要至少 [gb] GB 磁碟空間。 | 目前的選擇需要至少 [gb] GB 磁碟空間。 | Current selection requires at least [gb] GB of disk space. | — | ✅ 完全一致 | 保留 |
| Messages/ComponentsDiskSpaceMBLabel | 当前选择的组件需要至少 [mb] MB 的磁盘空间。 | 目前的選擇需要至少 [mb] MB 磁碟空間。 | 目前的選擇需要至少 [mb] MB 磁碟空間。 | Current selection requires at least [mb] MB of disk space. | — | ✅ 完全一致 | 保留 |
| Messages/ConfirmDeleteSharedFile2 | 系统显示以下共享文件已不再被任何程序使用。要删除此共享文件吗？%n%n如果仍有程序使用此文件，删除后可能导致程序无法正常运行。如不确定，请选择“否”。保留此文件不会损害系统。 | 系統顯示以下共用檔案已不再被任何程式使用。要移除此共用檔案嗎？%n%n若仍有程式使用此檔案，移除後可能導致程式無法正常運作。如不確定，請選擇「否」。保留此檔案不會損害系統。 | 系統顯示以下共用檔案已不再被任何程式使用。要移除此共用檔案嗎？%n%n若仍有程式使用此檔案，移除後可能導致程式無法正常運作。如不確定，請選擇「否」。保留此檔案不會損害系統。 | The system indicates that the following shared file is no longer in use by any programs. Would you like for Uninstall to remove this shared file?%n%nIf any programs are still using this file and it is removed, those programs may not function properly. If you are unsure, choose No. Leaving the file on your system will not cause any harm. | — | ❌ 占位符不一致、⚠️ 需要优化 → ✅ 已修复 | 移除新版 Inno Setup 不再提供的 %1 参数；文件名已由独立标签显示；修复“已不有”错字并简化共享文件确认文字 |
| Messages/ConfirmDeleteSharedFileTitle | 删除共享的文件吗？ | 移除共用檔案 | 移除共用檔案 | Remove Shared File? | — | ✅ 完全一致 | 保留 |
| Messages/ConfirmTitle | 确认 | 確認 | 確認 | Confirm | — | ✅ 完全一致 | 保留 |
| Messages/ConfirmUninstall | 您确认要完全移除 %1 及其所有组件吗？ | 確定要完全移除 %1 及其所有元件嗎？ | 確定要完全移除 %1 及其所有元件嗎？ | Are you sure you want to completely remove %1 and all of its components? | — | ❌ 含义不一致 → ✅ 已修复 | 卸载范围为所有组件，不只是相关文件 |
| Messages/CustomInstallation | 自定义安装 | 自訂安裝 | 自訂安裝 | Custom installation | — | ✅ 完全一致 | 保留 |
| Messages/DirDoesntExist | 文件夹：%n%n%1%n%n不存在。您想要创建此文件夹吗？ | 資料夾：%n%n%1%n%n 不存在。要建立該資料夾嗎？ | 資料夾：%n%n%1%n%n 不存在。要建立該資料夾嗎？ | The folder:%n%n%1%n%ndoes not exist. Would you like the folder to be created? | — | ✅ 完全一致 | 保留 |
| Messages/DirDoesntExistTitle | 文件夹不存在 | 資料夾不存在 | 資料夾不存在 | Folder Does Not Exist | — | ✅ 完全一致 | 保留 |
| Messages/DirExists | 文件夹：%n%n%1%n%n已经存在。您一定要安装到这个文件夹中吗？ | 資料夾：%n%n%1%n%n 已經存在。仍要安裝到該資料夾嗎？ | 資料夾：%n%n%1%n%n 已經存在。仍要安裝到該資料夾嗎？ | The folder:%n%n%1%n%nalready exists. Would you like to install to that folder anyway? | — | ✅ 完全一致 | 保留 |
| Messages/DirExistsTitle | 文件夹已存在 | 資料夾已經存在 | 資料夾已經存在 | Folder Exists | — | ✅ 完全一致 | 保留 |
| Messages/DirNameTooLong | 文件夹名称或路径太长。 | 資料夾名稱或路徑太長。 | 資料夾名稱或路徑太長。 | The folder name or path is too long. | — | ✅ 完全一致 | 保留 |
| Messages/DiskSpaceGBLabel | 至少需要有 [gb] GB 的可用磁盘空间。 | 最少需要 [gb] GB 磁碟空間。 | 最少需要 [gb] GB 磁碟空間。 | At least [gb] GB of free disk space is required. | — | ✅ 完全一致 | 保留 |
| Messages/DiskSpaceMBLabel | 至少需要有 [mb] MB 的可用磁盘空间。 | 最少需要 [mb] MB 磁碟空間。 | 最少需要 [mb] MB 磁碟空間。 | At least [mb] MB of free disk space is required. | — | ✅ 完全一致 | 保留 |
| Messages/DiskSpaceWarning | 安装程序至少需要 %1 KB 的可用空间才能安装，但选定驱动器只有 %2 KB 的可用空间。%n%n您一定要继续吗？ | 安裝程式需要至少 %1 KB 的磁碟空間，您所選取的磁碟只有 %2 KB 可用空間。%n%n您要繼續安裝嗎？ | 安裝程式需要至少 %1 KB 的磁碟空間，您所選取的磁碟只有 %2 KB 可用空間。%n%n您要繼續安裝嗎？ | Setup requires at least %1 KB of free space to install, but the selected drive only has %2 KB available.%n%nDo you want to continue anyway? | — | ✅ 完全一致 | 保留 |
| Messages/DiskSpaceWarningTitle | 磁盘空间不足 | 磁碟空間不足 | 磁碟空間不足 | Not Enough Disk Space | — | ✅ 完全一致 | 保留 |
| Messages/DontCloseApplications | 不要关闭应用程序(&amp;D) | 不要關閉應用程式 (&amp;D) | 不要關閉應用程式 (&amp;D) | &amp;Do not close the applications | — | ✅ 完全一致 | 保留 |
| Messages/DownloadingLabel2 | 正在下载文件... | 正在下載檔案... | 正在下載檔案... | Downloading files... | — | ✅ 完全一致 | 保留 |
| Messages/ErrorChangingAttr | 尝试更改下列现有文件的属性时出错： | 變更檔案屬性時發生錯誤： | 變更檔案屬性時發生錯誤： | An error occurred while trying to change the attributes of the existing file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorCloseApplications | 安装程序无法自动关闭所有应用程序。建议您在继续之前，关闭所有在使用需要由安装程序更新的文件的应用程序。 | 安裝程式無法自動關閉所有應用程式。繼續前，請關閉正在使用待更新檔案的應用程式。 | 安裝程式無法自動關閉所有應用程式。繼續前，請關閉正在使用待更新檔案的應用程式。 | Setup was unable to automatically close all applications. It is recommended that you close all applications using files that need to be updated by Setup before continuing. | — | ❌ 含义不一致 → ✅ 已修复 | 应关闭占用文件的应用，而不是关闭应用使用的文件 |
| Messages/ErrorCopying | 尝试复制下列文件时出错： | 複製檔案時發生錯誤： | 複製檔案時發生錯誤： | An error occurred while trying to copy a file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorCreatingDir | 安装程序无法创建目录“%1” | 安裝程式無法建立資料夾「%1」。 | 安裝程式無法建立資料夾「%1」。 | Setup was unable to create the directory "%1" | — | ✅ 完全一致 | 保留 |
| Messages/ErrorCreatingTemp | 尝试在目标目录创建文件时出错： | 在目的資料夾建立檔案時發生錯誤： | 在目的資料夾建立檔案時發生錯誤： | An error occurred while trying to create a file in the destination directory: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorDownloadAborted | 下载已中止 | 已停止下載 | 已停止下載 | Download aborted | — | ✅ 完全一致 | 保留 |
| Messages/ErrorDownloadFailed | 下载失败：%1 %2 | 下載失敗：%1 %2 | 下載失敗：%1 %2 | Download failed: %1 %2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorDownloadSizeFailed | 获取下载大小失败：%1 %2 | 無法取得檔案大小：%1 %2 | 無法取得檔案大小：%1 %2 | Getting size failed: %1 %2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorDownloading | 下载文件时出错： | 下載檔案時發生錯誤： | 下載檔案時發生錯誤： | An error occurred while trying to download a file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorExecutingProgram | 无法执行文件：%n%1 | 無法執行檔案:%n%1 | 無法執行檔案:%n%1 | Unable to execute file:%n%1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorExtracting | 解压压缩文件时出错： | 解壓縮檔案時發生錯誤： | 解壓縮檔案時發生錯誤： | An error occurred while trying to extract an archive: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorExtractionAborted | 解压已中止 | 解壓縮已中止 | 解壓縮已中止 | Extraction aborted | — | ✅ 完全一致 | 保留 |
| Messages/ErrorExtractionFailed | 解压失败：%1 | 解壓縮失敗：%1 | 解壓縮失敗：%1 | Extraction failed: %1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorFileSize | 文件大小错误：预期 %1，实际 %2 | 檔案大小不正確：應為 %1，實際為 %2 | 檔案大小不正確：應為 %1，實際為 %2 | Invalid file size: expected %1, found %2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorFunctionFailed | %1 失败；错误代码 %2 | %1 失敗；代碼 %2 | %1 失敗；代碼 %2 | %1 failed; code %2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorFunctionFailedNoCode | %1 失败 | %1 失敗 | %1 失敗 | %1 failed | — | ✅ 完全一致 | 保留 |
| Messages/ErrorFunctionFailedWithMessage | %1 失败；错误代码 %2.%n%3 | %1 失敗；代碼 %2.%n%3 | %1 失敗；代碼 %2.%n%3 | %1 failed; code %2.%n%3 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorIniEntry | 在文件“%1”中创建 INI 条目时出错。 | 在檔案「%1」中建立 INI 項目時發生錯誤。 | 在檔案「%1」中建立 INI 項目時發生錯誤。 | Error creating INI entry in file "%1". | — | ✅ 完全一致 | 保留 |
| Messages/ErrorInternal2 | 内部错误：%1 | 內部錯誤: %1 | 內部錯誤: %1 | Internal error: %1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorOpeningReadme | 尝试打开自述文件时出错。 | 開啟說明檔案時發生錯誤。 | 開啟說明檔案時發生錯誤。 | An error occurred while trying to open the README file. | — | ✅ 完全一致 | 保留 |
| Messages/ErrorProgress | 无效的进度：%1 / %2 | 進度無效：%1（共 %2） | 進度無效：%1（共 %2） | Invalid progress: %1 of %2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorReadingExistingDest | 尝试读取现有文件时出错： | 讀取現有目的檔案時發生錯誤： | 讀取現有目的檔案時發生錯誤： | An error occurred while trying to read the existing file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorReadingSource | 尝试读取下列源文件时出错： | 讀取來源檔案時發生錯誤： | 讀取來源檔案時發生錯誤： | An error occurred while trying to read the source file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegCreateKey | 创建注册表项时出错：%n%1\%2 | 無法建立登錄機碼：%n%1\%2 | 無法建立登錄機碼：%n%1\%2 | Error creating registry key:%n%1\%2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegOpenKey | 打开注册表项时出错：%n%1\%2 | 無法開啟登錄機碼：%n%1\%2 | 無法開啟登錄機碼：%n%1\%2 | Error opening registry key:%n%1\%2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegSvr32Failed | RegSvr32 失败；退出代码 %1 | RegSvr32 失敗；結束代碼 %1 | RegSvr32 失敗；結束代碼 %1 | RegSvr32 failed with exit code %1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegWriteKey | 写入注册表项时出错：%n%1\%2 | 無法變更登錄機碼：%n%1\%2 | 無法變更登錄機碼：%n%1\%2 | Error writing to registry key:%n%1\%2 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegisterServer | 无法注册 DLL/OCX：%1 | 無法註冊 DLL/OCX 檔案：%1。 | 無法註冊 DLL/OCX 檔案：%1。 | Unable to register the DLL/OCX: %1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRegisterTypeLib | 无法注册类库：%1 | 無法註冊類型程式庫：%1。 | 無法註冊類型程式庫：%1。 | Unable to register the type library: %1 | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRenamingTemp | 尝试重命名下列目标目录中的一个文件时出错： | 在目的資料夾重新命名檔案時發生錯誤： | 在目的資料夾重新命名檔案時發生錯誤： | An error occurred while trying to rename a file in the destination directory: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorReplacingExistingFile | 尝试替换现有文件时出错： | 取代現有檔案時發生錯誤： | 取代現有檔案時發生錯誤： | An error occurred while trying to replace the existing file: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRestartReplace | 重启并替换失败： | 重新啟動電腦後取代檔案失敗： | 重新啟動電腦後取代檔案失敗： | RestartReplace failed: | — | ✅ 完全一致 | 保留 |
| Messages/ErrorRestartingComputer | 安装程序无法重启电脑，请手动重启。 | 安裝程式無法重新啟動電腦，請自行重新啟動。 | 安裝程式無法重新啟動電腦，請自行重新啟動。 | Setup was unable to restart the computer. Please do this manually. | — | ✅ 完全一致 | 保留 |
| Messages/ErrorTitle | 错误 | 錯誤 | 錯誤 | Error | — | ✅ 完全一致 | 保留 |
| Messages/ErrorTooManyFilesInDir | 无法在目录“%1”中创建文件，因为里面包含太多文件 | 無法在資料夾「%1」內建立檔案，因為資料夾內有太多的檔案。 | 無法在資料夾「%1」內建立檔案，因為資料夾內有太多的檔案。 | Unable to create a file in the directory "%1" because it contains too many files | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileNewer2 | 现有的文件比安装程序将要安装的文件还要新。 | 現有檔案比安裝程式嘗試安裝的檔案還新。 | 現有檔案比安裝程式嘗試安裝的檔案還新。 | The existing file is newer than the one Setup is trying to install. | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileNewerKeepExisting | 保留现有的文件(&amp;K) (推荐) | 保留現有檔案 (&amp;K) (建議選項) | 保留現有檔案 (&amp;K) (建議選項) | &amp;Keep the existing file (recommended) | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileNewerOverwriteExisting | 覆盖已存在的文件(&amp;O) | 覆寫現有檔案 (&amp;O) | 覆寫現有檔案 (&amp;O) | &amp;Overwrite the existing file | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileNewerOverwriteOrKeepAll | 为所有冲突文件执行此操作(&amp;D) | 對所有衝突檔案執行相同操作 (&amp;D) | 對所有衝突檔案執行相同操作 (&amp;D) | &amp;Do this for the next conflicts | — | ❌ 含义不一致 → ✅ 已修复 | 作用范围是所有冲突而非下次冲突 |
| Messages/ExistingFileNewerSelectAction | 选择操作 | 選擇操作 | 選擇操作 | Select action | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileReadOnly2 | 无法替换现有文件，它是只读的。 | 無法取代現有檔案，因為檔案已標示為唯讀。 | 無法取代現有檔案，因為檔案已標示為唯讀。 | The existing file could not be replaced because it is marked read-only. | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileReadOnlyKeepExisting | 保留现有文件(&amp;K) | 保留現有檔案 (&amp;K) | 保留現有檔案 (&amp;K) | &amp;Keep the existing file | — | ✅ 完全一致 | 保留 |
| Messages/ExistingFileReadOnlyRetry | 移除只读属性并重试(&amp;R) | 移除唯讀屬性並重試 (&amp;R) | 移除唯讀屬性並重試 (&amp;R) | &amp;Remove the read-only attribute and try again | — | ✅ 完全一致 | 保留 |
| Messages/ExitSetupMessage | 安装程序尚未完成。如果现在退出，将不会安装该程序。%n%n您之后可以再次运行安装程序完成安装。%n%n现在退出安装程序吗？ | 安裝尚未完成。如果您現在結束安裝程式，這個程式將不會被安裝。%n%n您可以稍後再執行安裝程式以完成安裝程序。您現在要結束安裝程式嗎? | 安裝尚未完成。如果您現在結束安裝程式，這個程式將不會被安裝。%n%n您可以稍後再執行安裝程式以完成安裝程序。您現在要結束安裝程式嗎? | Setup is not complete. If you exit now, the program will not be installed.%n%nYou may run Setup again at another time to complete the installation.%n%nExit Setup? | — | ✅ 完全一致 | 保留 |
| Messages/ExitSetupTitle | 退出安装程序 | 結束安裝程式 | 結束安裝程式 | Exit Setup | — | ✅ 完全一致 | 保留 |
| Messages/ExtractingLabel | 正在解压文件... | 正在解壓縮檔案... | 正在解壓縮檔案... | Extracting files... | — | ✅ 完全一致 | 保留 |
| Messages/FileAbortRetryIgnoreIgnoreNotRecommended | 忽略错误并继续(&amp;I) (不推荐) | 略過錯誤並繼續 (不建議) (&amp;I) | 略過錯誤並繼續 (不建議) (&amp;I) | &amp;Ignore the error and continue (not recommended) | — | ✅ 完全一致 | 保留 |
| Messages/FileAbortRetryIgnoreSkipNotRecommended | 跳过此文件(&amp;S) (不推荐) | 略過這個檔案 (不建議) (&amp;S) | 略過這個檔案 (不建議) (&amp;S) | &amp;Skip this file (not recommended) | — | ✅ 完全一致 | 保留 |
| Messages/FileExists2 | 文件已经存在。 | 檔案已存在。 | 檔案已存在。 | The file already exists. | — | ✅ 完全一致 | 保留 |
| Messages/FileExistsKeepExisting | 保留现有的文件(&amp;K) | 保留現有檔案 (&amp;K) | 保留現有檔案 (&amp;K) | &amp;Keep the existing file | — | ❌ 含义不一致 → ✅ 已修复 | 修复保留操作错误使用 Alt+O，应为 Alt+K |
| Messages/FileExistsOverwriteExisting | 覆盖已存在的文件(&amp;O) | 覆寫現有檔案 (&amp;O) | 覆寫現有檔案 (&amp;O) | &amp;Overwrite the existing file | — | ❌ 含义不一致 → ✅ 已修复 | 恢复覆盖操作缺失的 Alt+O 快捷键标记 |
| Messages/FileExistsOverwriteOrKeepAll | 为所有冲突文件执行此操作(&amp;D) | 對所有衝突檔案執行相同操作 (&amp;D) | 對所有衝突檔案執行相同操作 (&amp;D) | &amp;Do this for the next conflicts | — | ❌ 含义不一致 → ✅ 已修复 | 作用范围是所有冲突而非下次冲突 |
| Messages/FileExistsSelectAction | 选择操作 | 選擇操作 | 選擇操作 | Select action | — | ✅ 完全一致 | 保留 |
| Messages/FileNotInDir2 | “%2”中找不到文件“%1”。请插入正确的磁盘或选择其他文件夹。 | 在「%2」找不到檔案「%1」。請插入正確的磁碟或選擇其他資料夾。 | 在「%2」找不到檔案「%1」。請插入正確的磁碟或選擇其他資料夾。 | The file "%1" could not be located in "%2". Please insert the correct disk or select another folder. | — | ✅ 完全一致 | 保留 |
| Messages/FinishedHeadingLabel | [name] 安装完成 | [name] 安裝完成 | [name] 安裝完成 | Completing the [name] Setup Wizard | — | ❌ 占位符不一致 → ✅ 已修复 | 补齐安装完成标题缺少的产品名占位符 |
| Messages/FinishedLabel | 安装程序已在您的电脑中安装了 [name]。您可以通过已安装的快捷方式运行此应用程序。 | 安裝程式已經將 [name] 安裝到您的電腦中，您可以選擇應用程式圖示來執行該應用程式。 | 安裝程式已經將 [name] 安裝到您的電腦中，您可以選擇應用程式圖示來執行該應用程式。 | Setup has finished installing [name] on your computer. The application may be launched by selecting the installed shortcuts. | — | ✅ 完全一致 | 保留 |
| Messages/FinishedLabelNoIcons | 安装程序已在您的电脑中安装了 [name]。 | 安裝程式已經將 [name] 安裝到您的電腦上。 | 安裝程式已經將 [name] 安裝到您的電腦上。 | Setup has finished installing [name] on your computer. | — | ✅ 完全一致 | 保留 |
| Messages/FinishedRestartLabel | 为完成 [name] 的安装，安装程序必须重新启动您的电脑。要立即重启吗？ | 要完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。您想要現在重新啟動電腦嗎？ | 要完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。您想要現在重新啟動電腦嗎？ | To complete the installation of [name], Setup must restart your computer. Would you like to restart now? | — | ✅ 完全一致 | 保留 |
| Messages/FinishedRestartMessage | 为完成 [name] 的安装，安装程序必须重新启动您的电脑。%n%n要立即重启吗？ | 要完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。%n%n您想要現在重新啟動電腦嗎？ | 要完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。%n%n您想要現在重新啟動電腦嗎？ | To complete the installation of [name], Setup must restart your computer.%n%nWould you like to restart now? | — | ✅ 完全一致 | 保留 |
| Messages/FullInstallation | 完全安装 | 完整安裝 | 完整安裝 | Full installation | — | ✅ 完全一致 | 保留 |
| Messages/GroupNameTooLong | 文件夹名或路径太长。 | 資料夾名稱或路徑太長。 | 資料夾名稱或路徑太長。 | The folder name or path is too long. | — | ✅ 完全一致 | 保留 |
| Messages/HelpTextNote |  |  |  |  | — | ✅ 完全一致 | 保留；可选语言包备注/标签，允许为空或按语言不同 |
| Messages/IncorrectPassword | 您输入的密码不正确，请重新输入。 | 您輸入的密碼不正確，請重新輸入。 | 您輸入的密碼不正確，請重新輸入。 | The password you entered is not correct. Please try again. | — | ✅ 完全一致 | 保留 |
| Messages/InfoAfterClickLabel | 准备好继续安装后，点击“下一步”。 | 當您準備好繼續安裝，請按 「下一步」。 | 當您準備好繼續安裝，請按 「下一步」。 | When you are ready to continue with Setup, click Next. | — | ✅ 完全一致 | 保留 |
| Messages/InfoAfterLabel | 请在继续安装前阅读以下重要信息。 | 在繼續安裝之前請閱讀以下重要資訊。 | 在繼續安裝之前請閱讀以下重要資訊。 | Please read the following important information before continuing. | — | ✅ 完全一致 | 保留 |
| Messages/InfoBeforeClickLabel | 准备好继续安装后，点击“下一步”。 | 當您準備好繼續安裝，請按 「下一步」。 | 當您準備好繼續安裝，請按 「下一步」。 | When you are ready to continue with Setup, click Next. | — | ✅ 完全一致 | 保留 |
| Messages/InfoBeforeLabel | 请在继续安装前阅读以下重要信息。 | 在繼續安裝之前請閱讀以下重要資訊。 | 在繼續安裝之前請閱讀以下重要資訊。 | Please read the following important information before continuing. | — | ✅ 完全一致 | 保留 |
| Messages/InformationTitle | 信息 | 訊息 | 訊息 | Information | — | ✅ 完全一致 | 保留 |
| Messages/InstallingLabel | 安装程序正在安装 [name] 到您的电脑，请稍候。 | 請稍候，安裝程式正在將 [name] 安裝到您的電腦上 | 請稍候，安裝程式正在將 [name] 安裝到您的電腦上 | Please wait while Setup installs [name] on your computer. | — | ✅ 完全一致 | 保留 |
| Messages/InvalidDirName | 文件夹名称无效。 | 資料夾名稱不正確。 | 資料夾名稱不正確。 | The folder name is not valid. | — | ✅ 完全一致 | 保留 |
| Messages/InvalidDrive | 您选定的驱动器或 UNC 共享不存在或不能访问。请选择其他位置。 | 您選取的磁碟機或 UNC 名稱不存在或無法存取，請選擇其他的目的地。 | 您選取的磁碟機或 UNC 名稱不存在或無法存取，請選擇其他的目的地。 | The drive or UNC share you selected does not exist or is not accessible. Please select another. | — | ✅ 完全一致 | 保留 |
| Messages/InvalidGroupName | 无效的文件夹名字。 | 資料夾名稱不正確。 | 資料夾名稱不正確。 | The folder name is not valid. | — | ✅ 完全一致 | 保留 |
| Messages/InvalidParameter | 无效的命令行参数：%n%n%1 | 命令列收到無效的參數：%n%n%1 | 命令列收到無效的參數：%n%n%1 | An invalid parameter was passed on the command line:%n%n%1 | — | ✅ 完全一致 | 保留 |
| Messages/InvalidPath | 请输入包含驱动器盘符的完整路径。%n%n例如 C:\App，或 UNC 路径 \\server\share。 | 您必須輸入完整路徑和磁碟機代號。%n%n例如 C:\App 或 UNC 路徑 \\伺服器\共用資料夾。 | 您必須輸入完整路徑和磁碟機代號。%n%n例如 C:\App 或 UNC 路徑 \\伺服器\共用資料夾。 | You must enter a full path with drive letter; for example:%n%nC:\APP%n%nor a UNC path in the form:%n%n\\server\share | — | ❌ 含义不一致 → ✅ 已修复 | 盘符不是卷标 |
| Messages/LastErrorMessage | %1。%n%n错误 %2: %3 | %1%n%n錯誤 %2: %3 | %1%n%n錯誤 %2: %3 | %1.%n%nError %2: %3 | — | ✅ 完全一致 | 保留 |
| Messages/LdrCannotCreateTemp | 无法创建临时文件。安装程序已中止 | 無法建立暫存檔案。安裝程式將會結束。 | 無法建立暫存檔案。安裝程式將會結束。 | Unable to create a temporary file. Setup aborted | — | ✅ 完全一致 | 保留 |
| Messages/LdrCannotExecTemp | 无法执行临时目录中的文件。安装程序已中止 | 無法執行暫存檔案。安裝程式將會結束。 | 無法執行暫存檔案。安裝程式將會結束。 | Unable to execute file in the temporary directory. Setup aborted | — | ✅ 完全一致 | 保留 |
| Messages/LicenseAccepted | 我同意此协议(&amp;A) | 我同意 (&amp;A) | 我同意 (&amp;A) | I &amp;accept the agreement | — | ✅ 完全一致 | 保留 |
| Messages/LicenseLabel | 请在继续安装前阅读以下重要信息。 | 請閱讀以下授權合約。 | 請閱讀以下授權合約。 | Please read the following important information before continuing. | — | ✅ 完全一致 | 保留 |
| Messages/LicenseLabel3 | 请仔细阅读下列许可协议。在继续安装前您必须同意这些协议条款。 | 請閱讀以下授權合約，您必須接受合約的各項條款才能繼續安裝。 | 請閱讀以下授權合約，您必須接受合約的各項條款才能繼續安裝。 | Please read the following License Agreement. You must accept the terms of this agreement before continuing with the installation. | — | ✅ 完全一致 | 保留 |
| Messages/LicenseNotAccepted | 我不同意此协议(&amp;D) | 我不同意 (&amp;D) | 我不同意 (&amp;D) | I &amp;do not accept the agreement | — | ✅ 完全一致 | 保留 |
| Messages/MustEnterGroupName | 您必须输入一个文件夹名。 | 您必須輸入一個資料夾的名稱。 | 您必須輸入一個資料夾的名稱。 | You must enter a folder name. | — | ✅ 完全一致 | 保留 |
| Messages/NewFolderName | 新建文件夹 | 新資料夾 | 新資料夾 | New Folder | — | ✅ 完全一致 | 保留 |
| Messages/NoProgramGroupCheck2 | 不创建开始菜单文件夹(&amp;D) | 不要在「開始」功能表中建立資料夾 (&amp;D) | 不要在「開始」功能表中建立資料夾 (&amp;D) | &amp;Don't create a Start Menu folder | — | ✅ 完全一致 | 保留 |
| Messages/NoRadio | 否，稍后重启电脑(&amp;N) | 否，我稍後重新啟動電腦(&amp;N) | 否，我稍後重新啟動電腦(&amp;N) | &amp;No, I will restart the computer later | — | ✅ 完全一致 | 保留 |
| Messages/NoUninstallWarning | 安装程序检测到下列组件已安装在您的电脑中：%n%n%1%n%n取消选中这些组件不会卸载它们。%n%n确定要继续吗？ | 安裝程式偵測到以下元件已經安裝到您的電腦上:%n%n%1%n%n取消選擇這些元件將不會移除它們。%n%n您仍然要繼續嗎？ | 安裝程式偵測到以下元件已經安裝到您的電腦上:%n%n%1%n%n取消選擇這些元件將不會移除它們。%n%n您仍然要繼續嗎？ | Setup has detected that the following components are already installed on your computer:%n%n%1%n%nDeselecting these components will not uninstall them.%n%nWould you like to continue anyway? | — | ✅ 完全一致 | 保留 |
| Messages/NoUninstallWarningTitle | 组件已存在 | 元件已存在 | 元件已存在 | Components Exist | — | ✅ 完全一致 | 保留 |
| Messages/NotOnThisPlatform | 此程序不能在 %1 上运行。 | 這個程式無法在 %1 執行。 | 這個程式無法在 %1 執行。 | This program will not run on %1. | — | ✅ 完全一致 | 保留 |
| Messages/OnlyAdminCanUninstall | 仅使用管理员权限的用户能完成此卸载。 | 這個程式要具備系統管理員權限的使用者方可解除安裝。 | 這個程式要具備系統管理員權限的使用者方可解除安裝。 | This installation can only be uninstalled by a user with administrative privileges. | — | ✅ 完全一致 | 保留 |
| Messages/OnlyOnTheseArchitectures | 此程序只能安装到为下列处理器架构设计的 Windows 版本中：%n%n%1 | 這個程式只能在專門為以下處理器架構而設計的 Windows 上安裝:%n%n%1 | 這個程式只能在專門為以下處理器架構而設計的 Windows 上安裝:%n%n%1 | This program can only be installed on versions of Windows designed for the following processor architectures:%n%n%1 | — | ✅ 完全一致 | 保留 |
| Messages/OnlyOnThisPlatform | 此程序只能在 %1 上运行。 | 這個程式必須在 %1 執行。 | 這個程式必須在 %1 執行。 | This program must be run on %1. | — | ✅ 完全一致 | 保留 |
| Messages/PasswordEditLabel | 密码(&amp;P)： | 密碼 (&amp;P): | 密碼 (&amp;P): | &amp;Password: | — | ✅ 完全一致 | 保留 |
| Messages/PasswordLabel1 | 这个安装程序有密码保护。 | 這個安裝程式具有密碼保護。 | 這個安裝程式具有密碼保護。 | This installation is password protected. | — | ✅ 完全一致 | 保留 |
| Messages/PasswordLabel3 | 请输入密码，然后点击“下一步”继续。密码区分大小写。 | 請輸入密碼，然後按 「下一步」 繼續。密碼是區分大小寫的。 | 請輸入密碼，然後按 「下一步」 繼續。密碼是區分大小寫的。 | Please provide the password, then click Next to continue. Passwords are case-sensitive. | — | ✅ 完全一致 | 保留 |
| Messages/PathLabel | 路径(&amp;P)： | 路徑(&amp;P): | 路徑(&amp;P): | &amp;Path: | — | ✅ 完全一致 | 保留 |
| Messages/PowerUserPrivilegesRequired | 在安装此程序时您必须以管理员身份或有权限的用户组身份登录。 | 您必須登入成具有系統管理員或 Power User 權限的使用者以安裝這個程式。 | 您必須登入成具有系統管理員或 Power User 權限的使用者以安裝這個程式。 | You must be logged in as an administrator or as a member of the Power Users group when installing this program. | — | ✅ 完全一致 | 保留 |
| Messages/PrepareToInstallNeedsRestart | 安装程序必须重启您的计算机。计算机重启后，请再次运行安装程序以完成 [name] 的安装。%n%n是否立即重新启动？ | 安裝程式必須重新啟動您的電腦。重新啟動後，請再次執行安裝程式以完成 [name] 的安裝。%n%n您想要現在重新啟動電腦嗎？ | 安裝程式必須重新啟動您的電腦。重新啟動後，請再次執行安裝程式以完成 [name] 的安裝。%n%n您想要現在重新啟動電腦嗎？ | Setup must restart your computer. After restarting your computer, run Setup again to complete the installation of [name].%n%nWould you like to restart now? | — | ✅ 完全一致 | 保留 |
| Messages/PreparingDesc | 安装程序正在准备安装 [name] 到您的电脑。 | 安裝程式準備將 [name] 安裝到您的電腦上。 | 安裝程式準備將 [name] 安裝到您的電腦上。 | Setup is preparing to install [name] on your computer. | — | ✅ 完全一致 | 保留 |
| Messages/PreviousInstallNotCompleted | 先前的程序安装或卸载未完成，您需要重启您的电脑以完成。%n%n在重启电脑后，再次运行安装程序以完成 [name] 的安装。 | 先前的安裝或解除安裝尚未完成，必須重新啟動電腦才能完成。%n%n重新啟動後，請再次執行此程式以安裝 [name]。 | 先前的安裝或解除安裝尚未完成，必須重新啟動電腦才能完成。%n%n重新啟動後，請再次執行此程式以安裝 [name]。 | The installation/removal of a previous program was not completed. You will need to restart your computer to complete that installation.%n%nAfter restarting your computer, run Setup again to complete the installation of [name]. | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideAllUsers | 为所有用户安装(&amp;A) | 為所有使用者安裝 (&amp;A) | 為所有使用者安裝 (&amp;A) | Install for &amp;all users | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideAllUsersRecommended | 为所有用户安装(&amp;A) (建议选项) | 為所有使用者安裝 (建議選項) (&amp;A) | 為所有使用者安裝 (建議選項) (&amp;A) | Install for &amp;all users (recommended) | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideCurrentUser | 仅为我安装(&amp;M) | 僅為我安裝 (&amp;M) | 僅為我安裝 (&amp;M) | Install for &amp;me only | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideCurrentUserRecommended | 仅为我安装(&amp;M) (建议选项) | 僅為我安裝 (建議選項) (&amp;M) | 僅為我安裝 (建議選項) (&amp;M) | Install for &amp;me only (recommended) | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideInstruction | 选择安装模式 | 選擇安裝模式 | 選擇安裝模式 | Select install mode | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideText1 | %1 可以为所有用户安装(需要管理员权限)，或仅为您安装。 | 可以為所有使用者安裝 %1 (需要系統管理權限)，或是僅為您安裝。 | 可以為所有使用者安裝 %1 (需要系統管理權限)，或是僅為您安裝。 | %1 can be installed for all users (requires administrative privileges), or for you only. | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideText2 | %1 可以仅为您安装，或为所有用户安装(需要管理员权限)。 | 可以僅為您安裝 %1，或是為所有使用者安裝 (需要系統管理權限)。 | 可以僅為您安裝 %1，或是為所有使用者安裝 (需要系統管理權限)。 | %1 can be installed for you only, or for all users (requires administrative privileges). | — | ✅ 完全一致 | 保留 |
| Messages/PrivilegesRequiredOverrideTitle | 选择安装程序模式 | 選擇安裝程式安裝模式 | 選擇安裝程式安裝模式 | Select Setup Install Mode | — | ✅ 完全一致 | 保留 |
| Messages/ReadyLabel1 | 安装程序准备就绪，现在可以开始安装 [name] 到您的电脑。 | 安裝程式將開始安裝 [name] 到您的電腦中。 | 安裝程式將開始安裝 [name] 到您的電腦中。 | Setup is now ready to begin installing [name] on your computer. | — | ✅ 完全一致 | 保留 |
| Messages/ReadyLabel2a | 点击“安装”继续此安装程序。如果您想重新考虑或修改任何设置，点击“上一步”。 | 按下 「安裝」 繼續安裝，或按 「上一步」 重新檢視或設定各選項的內容。 | 按下 「安裝」 繼續安裝，或按 「上一步」 重新檢視或設定各選項的內容。 | Click Install to continue with the installation, or click Back if you want to review or change any settings. | — | ✅ 完全一致 | 保留 |
| Messages/ReadyLabel2b | 点击“安装”继续此安装程序。 | 按下 「安裝」 繼續安裝。 | 按下 「安裝」 繼續安裝。 | Click Install to continue with the installation. | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoComponents | 已选择组件： | 選擇的元件: | 選擇的元件: | Selected components: | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoDir | 目标位置： | 安裝資料夾: | 安裝資料夾: | Destination location: | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoGroup | 开始菜单文件夹： | 「開始」功能表資料夾: | 「開始」功能表資料夾: | Start Menu folder: | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoTasks | 附加任务： | 其他工作: | 其他工作: | Additional tasks: | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoType | 安装类型： | 安裝類型: | 安裝類型: | Setup type: | — | ✅ 完全一致 | 保留 |
| Messages/ReadyMemoUserInfo | 用户信息： | 使用者資料： | 使用者資料： | User information: | — | ❌ 术语不一致 → ✅ 已修复 | 与项目香港繁体用语及标签标点一致 |
| Messages/RetryCancelCancel | 取消(&amp;C) | 取消 (&amp;C) | 取消 (&amp;C) | Cancel | — | ✅ 完全一致 | 保留 |
| Messages/RetryCancelRetry | 重试(&amp;T) | 重試 (&amp;T) | 重試 (&amp;T) | &amp;Try again | — | ✅ 完全一致 | 保留 |
| Messages/RetryCancelSelectAction | 选择操作 | 選擇操作 | 選擇操作 | Select action | — | ✅ 完全一致 | 保留 |
| Messages/RunEntryExec | 运行 %1 | 執行 %1 | 執行 %1 | Run %1 | — | ✅ 完全一致 | 保留 |
| Messages/RunEntryShellExec | 查阅 %1 | 檢視 %1 | 檢視 %1 | View %1 | — | ✅ 完全一致 | 保留 |
| Messages/SelectComponentsDesc | 您想安装哪些程序组件？ | 選擇將會被安裝的元件。 | 選擇將會被安裝的元件。 | Which components should be installed? | — | ✅ 完全一致 | 保留 |
| Messages/SelectComponentsLabel2 | 选中您想安装的组件；取消您不想安装的组件。然后点击“下一步”继续。 | 選擇您想要安裝的元件；清除您不想安裝的元件。然後按 「下一步」 繼續安裝。 | 選擇您想要安裝的元件；清除您不想安裝的元件。然後按 「下一步」 繼續安裝。 | Select the components you want to install; clear the components you do not want to install. Click Next when you are ready to continue. | — | ✅ 完全一致 | 保留 |
| Messages/SelectDirBrowseLabel | 点击“下一步”继续。如果您想选择其他文件夹，点击“浏览”。 | 按 「下一步」 繼續，如果您想選擇另一個資料夾，請按 「瀏覽」。 | 按 「下一步」 繼續，如果您想選擇另一個資料夾，請按 「瀏覽」。 | To continue, click Next. If you would like to select a different folder, click Browse. | — | ✅ 完全一致 | 保留 |
| Messages/SelectDirDesc | 您想将 [name] 安装在哪里？ | 選擇安裝程式安裝 [name] 的位置。 | 選擇安裝程式安裝 [name] 的位置。 | Where should [name] be installed? | — | ✅ 完全一致 | 保留 |
| Messages/SelectDirLabel3 | 安装程序将安装 [name] 到下面的文件夹中。 | 安裝程式將會把 [name] 安裝到下方的資料夾。 | 安裝程式將會把 [name] 安裝到下方的資料夾。 | Setup will install [name] into the following folder. | — | ✅ 完全一致 | 保留 |
| Messages/SelectDirectoryLabel | 请指定下一张磁盘的位置。 | 請指定下一張磁碟的位置。 | 請指定下一張磁碟的位置。 | Please specify the location of the next disk. | — | ✅ 完全一致 | 保留 |
| Messages/SelectDiskLabel2 | 请插入磁盘 %1 并点击“确定”。%n%n如果这个磁盘中的文件可以在下列文件夹之外的文件夹中找到，请输入正确的路径或点击“浏览”。 | 請插入磁碟 %1，然後按「確定」。%n%n如果檔案不在下方顯示的資料夾中，請輸入正確的資料夾名稱或按「瀏覽」選取。 | 請插入磁碟 %1，然後按「確定」。%n%n如果檔案不在下方顯示的資料夾中，請輸入正確的資料夾名稱或按「瀏覽」選取。 | Please insert Disk %1 and click OK.%n%nIf the files on this disk can be found in a folder other than the one displayed below, enter the correct path or click Browse. | — | ✅ 完全一致 | 保留 |
| Messages/SelectLanguageLabel | 选择安装时使用的语言。 | 選擇在安裝過程中使用的語言: | 選擇在安裝過程中使用的語言: | Select the language to use during the installation. | — | ✅ 完全一致 | 保留 |
| Messages/SelectLanguageTitle | 选择安装语言 | 選擇安裝語言 | 選擇安裝語言 | Select Setup Language | — | ✅ 完全一致 | 保留 |
| Messages/SelectStartMenuFolderBrowseLabel | 点击“下一步”继续。如果您想选择其他文件夹，点击“浏览”。 | 按 「下一步」 繼續，如果您想選擇另一個資料夾，請按 「瀏覽」。 | 按 「下一步」 繼續，如果您想選擇另一個資料夾，請按 「瀏覽」。 | To continue, click Next. If you would like to select a different folder, click Browse. | — | ✅ 完全一致 | 保留 |
| Messages/SelectStartMenuFolderDesc | 安装程序应该在哪里放置程序的快捷方式？ | 選擇安裝程式建立程式的捷徑的位置。 | 選擇安裝程式建立程式的捷徑的位置。 | Where should Setup place the program's shortcuts? | — | ✅ 完全一致 | 保留 |
| Messages/SelectStartMenuFolderLabel3 | 安装程序将在下列“开始”菜单文件夹中创建程序的快捷方式。 | 安裝程式將會把程式的捷徑建立在下方的「開始」功能表資料夾。 | 安裝程式將會把程式的捷徑建立在下方的「開始」功能表資料夾。 | Setup will create the program's shortcuts in the following Start Menu folder. | — | ✅ 完全一致 | 保留 |
| Messages/SelectTasksDesc | 您想要安装程序执行哪些附加任务？ | 選擇要執行的其他工作。 | 選擇要執行的其他工作。 | Which additional tasks should be performed? | — | ✅ 完全一致 | 保留 |
| Messages/SelectTasksLabel2 | 选择您想要安装程序在安装 [name] 时执行的附加任务，然后点击“下一步”。 | 選擇安裝程式在安裝 [name] 時要執行的其他工作，然後按 「下一步」。 | 選擇安裝程式在安裝 [name] 時要執行的其他工作，然後按 「下一步」。 | Select the additional tasks you would like Setup to perform while installing [name], then click Next. | — | ✅ 完全一致 | 保留 |
| Messages/SetupAborted | 安装程序未完成安装。%n%n请修正这个问题并重新运行安装程序。 | 安裝沒有完成。%n%n請更正問題後重新安裝一次。 | 安裝沒有完成。%n%n請更正問題後重新安裝一次。 | Setup was not completed.%n%nPlease correct the problem and run Setup again. | — | ✅ 完全一致 | 保留 |
| Messages/SetupAlreadyRunning | 安装程序正在运行。 | 安裝程式已經在執行。 | 安裝程式已經在執行。 | Setup is already running. | — | ✅ 完全一致 | 保留 |
| Messages/SetupAppRunningError | 安装程序发现 %1 当前正在运行。%n%n请先关闭正在运行的程序，然后点击“确定”继续，或点击“取消”退出。 | 安裝程式偵測到 %1 正在執行。%n%n請關閉該程式後按 「確定」 繼續，或按 「取消」 離開。 | 安裝程式偵測到 %1 正在執行。%n%n請關閉該程式後按 「確定」 繼續，或按 「取消」 離開。 | Setup has detected that %1 is currently running.%n%nPlease close all instances of it now, then click OK to continue, or Cancel to exit. | — | ✅ 完全一致 | 保留 |
| Messages/SetupAppTitle | 安装 | 安裝程式 | 安裝程式 | Setup | — | ✅ 完全一致 | 保留 |
| Messages/SetupFileCorrupt | 安装文件已损坏。请获取程序的新副本。 | 安裝檔案已損毀。請重新下載此軟件。 | 安裝檔案已損毀。請重新下載此軟體。 | The setup files are corrupted. Please obtain a new copy of the program. | — | ✅ 完全一致 | 保留 |
| Messages/SetupFileCorruptOrWrongVer | 安装文件已损坏，或是与这个安装程序的版本不兼容。请修正这个问题或获取新的程序副本。 | 安裝檔案已損毀，或與安裝程式的版本不符。請重新下載此軟件。 | 安裝檔案已損毀，或與安裝程式的版本不符。請重新下載此軟體。 | The setup files are corrupted, or are incompatible with this version of Setup. Please correct the problem or obtain a new copy of the program. | — | ✅ 完全一致 | 保留 |
| Messages/SetupFileMissing | 安装目录中缺少文件 %1。请修正这个问题或者获取程序的新副本。 | 安裝資料夾缺少檔案「%1」。請修正問題或重新下載此軟件。 | 安裝資料夾缺少檔案「%1」。請修正問題或重新下載此軟體。 | The file %1 is missing from the installation directory. Please correct the problem or obtain a new copy of the program. | — | ✅ 完全一致 | 保留 |
| Messages/SetupLdrStartupMessage | 现在将安装 %1。您想要继续吗？ | 這將會安裝 %1。您想要繼續嗎？ | 這將會安裝 %1。您想要繼續嗎？ | This will install %1. Do you wish to continue? | — | ✅ 完全一致 | 保留 |
| Messages/SetupWindowTitle | 安装 - %1 | %1 安裝程式 | %1 安裝程式 | Setup - %1 | — | ✅ 完全一致 | 保留 |
| Messages/SharedFileLocationLabel | 位置： | 位置: | 位置: | Location: | — | ✅ 完全一致 | 保留 |
| Messages/SharedFileNameLabel | 文件名： | 檔案名稱: | 檔案名稱: | File name: | — | ✅ 完全一致 | 保留 |
| Messages/ShowReadmeCheck | 是，我想查阅自述文件 | 是，我要閱讀說明檔案。 | 是，我要閱讀說明檔案。 | Yes, I would like to view the README file | — | ✅ 完全一致 | 保留 |
| Messages/ShutdownBlockReasonInstallingApp | 正在安装 %1。 | 正在安裝 %1。 | 正在安裝 %1。 | Installing %1. | — | ✅ 完全一致 | 保留 |
| Messages/ShutdownBlockReasonUninstallingApp | 正在卸载 %1。 | 正在解除安裝 %1。 | 正在解除安裝 %1。 | Uninstalling %1. | — | ✅ 完全一致 | 保留 |
| Messages/SourceDoesntExist | 源文件“%1”不存在 | 來源檔案「%1」不存在。 | 來源檔案「%1」不存在。 | The source file "%1" does not exist | — | ✅ 完全一致 | 保留 |
| Messages/SourceIsCorrupted | 源文件已损坏 | 來源檔案已經損毀。 | 來源檔案已經損毀。 | The source file is corrupted | — | ✅ 完全一致 | 保留 |
| Messages/SourceVerificationFailed | 源文件验证失败: %1 | 來源檔案驗證失敗：%1 | 來源檔案驗證失敗：%1 | Verification of the source file failed: %1 | — | ✅ 完全一致 | 保留 |
| Messages/StatusClosingApplications | 正在关闭应用程序... | 正在關閉應用程式... | 正在關閉應用程式... | Closing applications... | — | ✅ 完全一致 | 保留 |
| Messages/StatusCreateDirs | 正在创建目录... | 正在建立資料夾... | 正在建立資料夾... | Creating directories... | — | ✅ 完全一致 | 保留 |
| Messages/StatusCreateIcons | 正在创建快捷方式... | 正在建立捷徑... | 正在建立捷徑... | Creating shortcuts... | — | ✅ 完全一致 | 保留 |
| Messages/StatusCreateIniEntries | 正在创建 INI 条目... | 寫入 INI 檔案的項目... | 寫入 INI 檔案的項目... | Creating INI entries... | — | ✅ 完全一致 | 保留 |
| Messages/StatusCreateRegistryEntries | 正在创建注册表条目... | 正在更新登錄... | 正在更新登錄... | Creating registry entries... | — | ✅ 完全一致 | 保留 |
| Messages/StatusDownloadFiles | 正在下载文件... | 正在下載檔案... | 正在下載檔案... | Downloading files... | — | ✅ 完全一致 | 保留 |
| Messages/StatusExtractFiles | 正在提取文件... | 正在解壓縮檔案... | 正在解壓縮檔案... | Extracting files... | — | ✅ 完全一致 | 保留 |
| Messages/StatusRegisterFiles | 正在注册文件... | 正在註冊檔案... | 正在註冊檔案... | Registering files... | — | ✅ 完全一致 | 保留 |
| Messages/StatusRestartingApplications | 正在重启应用程序... | 正在重新開啟應用程式... | 正在重新開啟應用程式... | Restarting applications... | — | ✅ 完全一致 | 保留 |
| Messages/StatusRollback | 正在撤销更改... | 正在復原變更... | 正在復原變更... | Rolling back changes... | — | ✅ 完全一致 | 保留 |
| Messages/StatusRunProgram | 正在完成安装... | 正在完成安裝... | 正在完成安裝... | Finishing installation... | — | ✅ 完全一致 | 保留 |
| Messages/StatusSavingUninstall | 正在保存卸载信息... | 儲存解除安裝資訊... | 儲存解除安裝資訊... | Saving uninstall information... | — | ✅ 完全一致 | 保留 |
| Messages/StatusUninstalling | 正在卸载 %1... | 正在解除安裝 %1... | 正在解除安裝 %1... | Uninstalling %1... | — | ✅ 完全一致 | 保留 |
| Messages/StopDownload | 您确定要停止下载吗？ | 您確定要停止下載嗎？ | 您確定要停止下載嗎？ | Are you sure you want to stop the download? | — | ✅ 完全一致 | 保留 |
| Messages/StopExtraction | 您确定要停止解压吗？ | 確定要停止解壓縮嗎？ | 確定要停止解壓縮嗎？ | Are you sure you want to stop the extraction? | — | ✅ 完全一致 | 保留 |
| Messages/TranslatorNote | 简体中文翻译由Kira(847320916@qq.com)维护。项目地址：https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation |  |  |  | — | ✅ 完全一致 | 保留；可选语言包备注/标签，允许为空或按语言不同 |
| Messages/UninstallAppFullTitle | %1 卸载 | 解除安裝 %1 | 解除安裝 %1 | %1 Uninstall | — | ✅ 完全一致 | 保留 |
| Messages/UninstallAppRunningError | 卸载程序发现 %1 当前正在运行。%n%n请先关闭正在运行的程序，然后点击“确定”继续，或点击“取消”退出。 | 解除安裝程式偵測到 %1 正在執行。%n%n請關閉該程式後按 「確定」 繼續，或按 「取消」 離開。 | 解除安裝程式偵測到 %1 正在執行。%n%n請關閉該程式後按 「確定」 繼續，或按 「取消」 離開。 | Uninstall has detected that %1 is currently running.%n%nPlease close all instances of it now, then click OK to continue, or Cancel to exit. | — | ✅ 完全一致 | 保留 |
| Messages/UninstallAppTitle | 卸载 | 解除安裝 | 解除安裝 | Uninstall | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDataCorrupted | 文件“%1”已损坏。无法卸载 | 檔案「%1」已經損毀，無法解除安裝 | 檔案「%1」已經損毀，無法解除安裝 | "%1" file is corrupted. Cannot uninstall | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMark | %1 (%2) | %1 (%2) | %1 (%2) | %1 (%2) | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMark32Bit | 32 位 | 32 位元 | 32 位元 | 32-bit | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMark64Bit | 64 位 | 64 位元 | 64 位元 | 64-bit | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMarkAllUsers | 所有用户 | 所有使用者 | 所有使用者 | All users | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMarkCurrentUser | 当前用户 | 目前使用者 | 目前使用者 | Current user | — | ✅ 完全一致 | 保留 |
| Messages/UninstallDisplayNameMarks | %1 (%2, %3) | %1 (%2, %3) | %1 (%2, %3) | %1 (%2, %3) | — | ✅ 完全一致 | 保留 |
| Messages/UninstallNotFound | 文件“%1”不存在。无法卸载。 | 檔案「%1」不存在，無法解除安裝。 | 檔案「%1」不存在，無法解除安裝。 | File "%1" does not exist. Cannot uninstall. | — | ✅ 完全一致 | 保留 |
| Messages/UninstallOnlyOnWin64 | 仅允许在 64 位 Windows 中卸载此程序。 | 這個程式只能在 64 位元的 Windows 上解除安裝。 | 這個程式只能在 64 位元的 Windows 上解除安裝。 | This installation can only be uninstalled on 64-bit Windows. | — | ✅ 完全一致 | 保留 |
| Messages/UninstallOpenError | 文件“%1”不能被打开。无法卸载。 | 無法開啟檔案「%1」，無法解除安裝 | 無法開啟檔案「%1」，無法解除安裝 | File "%1" could not be opened. Cannot uninstall | — | ✅ 完全一致 | 保留 |
| Messages/UninstallStatusLabel | 正在从您的电脑中移除 %1，请稍候。 | 正在從您的電腦移除 %1 中，請稍候... | 正在從您的電腦移除 %1 中，請稍候... | Please wait while %1 is removed from your computer. | — | ✅ 完全一致 | 保留 |
| Messages/UninstallUnknownEntry | 卸载日志中遇到一个未知条目 (%1) | 解除安裝記錄檔中發現未知的記錄 (%1)。 | 解除安裝記錄檔中發現未知的記錄 (%1)。 | An unknown entry (%1) was encountered in the uninstall log | — | ✅ 完全一致 | 保留 |
| Messages/UninstallUnsupportedVer | 此版本的卸载程序无法识别卸载日志文件“%1”的格式。无法卸载 | 這個版本的解除安裝程式無法辨識記錄檔 「%1」 之格式，無法解除安裝。 | 這個版本的解除安裝程式無法辨識記錄檔 「%1」 之格式，無法解除安裝。 | The uninstall log file "%1" is in a format not recognized by this version of the uninstaller. Cannot uninstall | — | ✅ 完全一致 | 保留 |
| Messages/UninstalledAll | 已顺利从您的电脑中移除 %1。 | %1 已經成功從您的電腦中移除。 | %1 已經成功從您的電腦中移除。 | %1 was successfully removed from your computer. | — | ✅ 完全一致 | 保留 |
| Messages/UninstalledAndNeedsRestart | 为完成 %1 的卸载，需要重启您的电脑。%n%n立即重启电脑吗？ | 要完成 %1 的解除安裝程序，您必須重新啟動電腦。%n%n您想要現在重新啟動電腦嗎？ | 要完成 %1 的解除安裝程序，您必須重新啟動電腦。%n%n您想要現在重新啟動電腦嗎？ | To complete the uninstallation of %1, your computer must be restarted.%n%nWould you like to restart now? | — | ✅ 完全一致 | 保留 |
| Messages/UninstalledMost | %1 卸载完成。%n%n有部分内容未能被删除，但您可以手动删除它们。 | %1 解除安裝完成。%n%n某些檔案及元件無法移除，您可以自行刪除這些檔案。 | %1 解除安裝完成。%n%n某些檔案及元件無法移除，您可以自行刪除這些檔案。 | %1 uninstall complete.%n%nSome elements could not be removed. These can be removed manually. | — | ✅ 完全一致 | 保留 |
| Messages/UserInfoDesc | 请输入您的信息。 | 請輸入您的資料。 | 請輸入您的資料。 | Please enter your information. | — | ✅ 完全一致 | 保留 |
| Messages/UserInfoName | 用户名(&amp;U)： | 使用者名稱(&amp;U): | 使用者名稱(&amp;U): | &amp;User Name: | — | ✅ 完全一致 | 保留 |
| Messages/UserInfoNameRequired | 您必须输入用户名。 | 您必須輸入您的名稱。 | 您必須輸入您的名稱。 | You must enter a name. | — | ✅ 完全一致 | 保留 |
| Messages/UserInfoOrg | 组织(&amp;O)： | 組織(&amp;O): | 組織(&amp;O): | &amp;Organization: | — | ✅ 完全一致 | 保留 |
| Messages/UserInfoSerial | 序列号(&amp;S)： | 序號(&amp;S): | 序號(&amp;S): | &amp;Serial Number: | — | ✅ 完全一致 | 保留 |
| Messages/VerificationFileHashIncorrect | 文件哈希值不正确 | 檔案雜湊值不正確 | 檔案雜湊值不正確 | The hash of the file is incorrect | — | ✅ 完全一致 | 保留 |
| Messages/VerificationFileNameIncorrect | 文件名不正确 | 檔案名稱不正確 | 檔案名稱不正確 | The name of the file is incorrect | — | ✅ 完全一致 | 保留 |
| Messages/VerificationFileSizeIncorrect | 文件大小不正确 | 檔案大小不正確 | 檔案大小不正確 | The size of the file is incorrect | — | ✅ 完全一致 | 保留 |
| Messages/VerificationFileTagIncorrect | 文件标签不正确 | 檔案標記不正確 | 檔案標記不正確 | The tag of the file is incorrect | — | ✅ 完全一致 | 保留 |
| Messages/VerificationKeyNotFound | 签名文件“%1”使用了未知密钥 | 簽名檔案「%1」使用了未知的金鑰 | 簽章檔案「%1」使用了未知的金鑰 | The signature file "%1" uses an unknown key | — | ✅ 完全一致 | 保留 |
| Messages/VerificationSignatureDoesntExist | 签名文件“%1”不存在 | 簽名檔案「%1」不存在 | 簽章檔案「%1」不存在 | The signature file "%1" does not exist | — | ✅ 完全一致 | 保留 |
| Messages/VerificationSignatureInvalid | 签名文件“%1”无效 | 簽名檔案「%1」無效 | 簽章檔案「%1」無效 | The signature file "%1" is invalid | — | ✅ 完全一致 | 保留 |
| Messages/WelcomeLabel1 | 欢迎使用 [name] 安装向导 | 歡迎使用 [name] 安裝程式 | 歡迎使用 [name] 安裝程式 | Welcome to the [name] Setup Wizard | — | ✅ 完全一致 | 保留 |
| Messages/WelcomeLabel2 | 现在将安装 [name/ver] 到您的电脑中。%n%n建议您在继续安装前关闭所有其他应用程序。 | 安裝程式將會把 [name/ver] 安裝到您的電腦。%n%n強烈建議您在安裝期間關閉其他應用程式，以免發生衝突。 | 安裝程式將會把 [name/ver] 安裝到您的電腦。%n%n強烈建議您在安裝期間關閉其他應用程式，以免發生衝突。 | This will install [name/ver] on your computer.%n%nIt is recommended that you close all other applications before continuing. | — | ✅ 完全一致 | 保留 |
| Messages/WinVersionTooHighError | 此程序不能安装于 %1 版本 %2 或更高。 | 這個程式無法安裝到 %1 版本 %2 或以上的系統。 | 這個程式無法安裝到 %1 版本 %2 或以上的系統。 | This program cannot be installed on %1 version %2 or later. | — | ✅ 完全一致 | 保留 |
| Messages/WinVersionTooLowError | 此程序需要 %1 版本 %2 或更高。 | 這個程式必須在 %1 版本 %2 或以上的系統執行。 | 這個程式必須在 %1 版本 %2 或以上的系統執行。 | This program requires %1 version %2 or later. | — | ✅ 完全一致 | 保留 |
| Messages/WindowsServicePackRequired | 此程序需要 %1 服务包 %2 或更高版本。 | 本安裝程式需要 %1 Service Pack %2 或更新。 | 本安裝程式需要 %1 Service Pack %2 或更新。 | This program requires %1 Service Pack %2 or later. | — | ✅ 完全一致 | 保留 |
| Messages/WindowsVersionNotSupported | 此程序不支持当前计算机运行的 Windows 版本。 | 本安裝程式並不支援目前在電腦所運行的 Windows 版本。 | 本安裝程式並不支援目前在電腦所執行的 Windows 版本。 | This program does not support the version of Windows your computer is running. | — | ✅ 完全一致 | 保留 |
| Messages/WizardInfoAfter | 信息 | 訊息 | 訊息 | Information | — | ✅ 完全一致 | 保留 |
| Messages/WizardInfoBefore | 信息 | 訊息 | 訊息 | Information | — | ✅ 完全一致 | 保留 |
| Messages/WizardInstalling | 正在安装 | 正在安裝 | 正在安裝 | Installing | — | ✅ 完全一致 | 保留 |
| Messages/WizardLicense | 许可协议 | 授權合約 | 授權合約 | License Agreement | — | ✅ 完全一致 | 保留 |
| Messages/WizardPassword | 密码 | 密碼 | 密碼 | Password | — | ✅ 完全一致 | 保留 |
| Messages/WizardPreparing | 正在准备安装 | 準備安裝程式 | 準備安裝程式 | Preparing to Install | — | ✅ 完全一致 | 保留 |
| Messages/WizardReady | 准备安装 | 準備安裝 | 準備安裝 | Ready to Install | — | ✅ 完全一致 | 保留 |
| Messages/WizardSelectComponents | 选择组件 | 選擇元件 | 選擇元件 | Select Components | — | ✅ 完全一致 | 保留 |
| Messages/WizardSelectDir | 选择目标位置 | 選擇安裝資料夾 | 選擇安裝資料夾 | Select Destination Location | — | ✅ 完全一致 | 保留 |
| Messages/WizardSelectProgramGroup | 选择开始菜单文件夹 | 選擇「開始」功能表的資料夾 | 選擇「開始」功能表的資料夾 | Select Start Menu Folder | — | ✅ 完全一致 | 保留 |
| Messages/WizardSelectTasks | 选择附加任务 | 選擇其他工作 | 選擇其他工作 | Select Additional Tasks | — | ✅ 完全一致 | 保留 |
| Messages/WizardUninstalling | 卸载状态 | 解除安裝狀態 | 解除安裝狀態 | Uninstall Status | — | ✅ 完全一致 | 保留 |
| Messages/WizardUserInfo | 用户信息 | 使用者資料 | 使用者資料 | User Information | — | ❌ 术语不一致 → ✅ 已修复 | 与项目香港繁体用语一致 |
| Messages/YesRadio | 是，立即重启电脑(&amp;Y) | 是，立即重新啟動電腦(&amp;Y) | 是，立即重新啟動電腦(&amp;Y) | &amp;Yes, restart the computer now | — | ✅ 完全一致 | 保留 |

### Hardcoded

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| src/App/App.xaml / node 83 / Text | &gt; | &gt; | &gt; | &gt; | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 351 / Text | ! | ! | ! | ! | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 440 / Text | iPhoneMirror | iPhoneMirror | iPhoneMirror | iPhoneMirror | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 715 / Text | 00:00 | 00:00 | 00:00 | 00:00 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 717 / Text | --:-- | --:-- | --:-- | --:-- | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 725 / Text | −10 | −10 | −10 | −10 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 731 / Text | +10 | +10 | +10 | +10 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 734 / Content | 0.5x | 0.5x | 0.5x | 0.5x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 735 / Content | 0.75x | 0.75x | 0.75x | 0.75x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 736 / Content | 1x | 1x | 1x | 1x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 737 / Content | 1.25x | 1.25x | 1.25x | 1.25x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 738 / Content | 1.5x | 1.5x | 1.5x | 1.5x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 739 / Content | 2x | 2x | 2x | 2x | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 838 / Text |   ·   |   ·   |   ·   |   ·   | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/MainWindow.xaml / node 840 / Text | UDID | UDID | UDID | UDID | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/AboutWindow.xaml / node 27 / Text | iPhoneMirror | iPhoneMirror | iPhoneMirror | iPhoneMirror | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/AboutWindow.xaml / node 35 / Content | GitHub | GitHub | GitHub | GitHub | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/AdvancedSettingsWindow.xaml / node 28 / Text | × | × | × | × | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/BluetoothControlNoticeWindow.xaml / node 31 / Text | 1 | 1 | 1 | 1 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/BluetoothControlNoticeWindow.xaml / node 38 / Text | 2 | 2 | 2 | 2 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/BluetoothControlNoticeWindow.xaml / node 45 / Text | 3 | 3 | 3 | 3 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/BluetoothControlNoticeWindow.xaml / node 52 / Text | 4 | 4 | 4 | 4 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/BluetoothControlNoticeWindow.xaml / node 59 / Text | 5 | 5 | 5 | 5 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/CaptureRecoveryWindow.xaml / node 25 / Text | 1 | 1 | 1 | 1 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/CaptureRecoveryWindow.xaml / node 33 / Text | 2 | 2 | 2 | 2 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 112 / Content | 1280 x 720 | 1280 x 720 | 1280 x 720 | 1280 x 720 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 113 / Content | 1920 x 1080 | 1920 x 1080 | 1920 x 1080 | 1920 x 1080 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 114 / Content | 720 x 1280 | 720 x 1280 | 720 x 1280 | 720 x 1280 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 115 / Content | 1080 x 1920 | 1080 x 1920 | 1080 x 1920 | 1080 x 1920 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 119 / Content | 30 fps | 30 fps | 30 fps | 30 fps | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 120 / Content | 60 fps | 60 fps | 60 fps | 60 fps | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 59 / Content | RTMP | RTMP | RTMP | RTMP | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 60 / Content | SRT | SRT | SRT | SRT | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/MediaOutputSettingsWindow.xaml / node 61 / Content | WebRTC / WHIP | WebRTC / WHIP | WebRTC / WHIP | WebRTC / WHIP | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/ReverseControlStatusWindow.xaml / node 44 / Text | 1 | 1 | 1 | 1 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/ReverseControlStatusWindow.xaml / node 50 / Text | 2 | 2 | 2 | 2 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/ReverseControlStatusWindow.xaml / node 56 / Text | 3 | 3 | 3 | 3 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/ReverseControlStatusWindow.xaml / node 62 / Text | 4 | 4 | 4 | 4 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/ReverseControlStatusWindow.xaml / node 68 / Text | 5 | 5 | 5 | 5 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/StartupErrorWindow.xaml / node 1 / Title | iPhoneMirror | iPhoneMirror | iPhoneMirror | iPhoneMirror | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/TrayPanelWindow.xaml / node 10 / Text | iPhoneMirror | iPhoneMirror | iPhoneMirror | iPhoneMirror | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/TrayPanelWindow.xaml / node 32 / Text |  ·  |  ·  |  ·  |  ·  | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/App/Windows/UsbProjectionModeInfoWindow.xaml / node 29 / Text | ! | ! | ! | ! | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |
| src/DriverInstaller/MainWindow.xaml / node 148 / Text | libusb-win32 1.2.6.0 | libusb-win32 1.2.6.0 | libusb-win32 1.2.6.0 | libusb-win32 1.2.6.0 | — | ✅ 完全一致 | 品牌、协议、数字、符号或单位；四语通用，保留 |

### StartupFallback

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| StartupDiagnostics.Label/StartupErrorClose | 关闭 | 關閉 | 關閉 | Close | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.Label/StartupErrorDetails | 错误详情 | 錯誤詳細資料 | 錯誤詳細資料 | Error details | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.Label/StartupErrorHeading | iPhoneMirror 无法启动 | iPhoneMirror 無法啟動 | iPhoneMirror 無法啟動 | iPhoneMirror could not start | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.Label/StartupErrorLogLabel | 诊断日志 | 診斷記錄 | 診斷記錄檔 | Diagnostic log | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.Label/StartupErrorOpenLog | 打开日志位置 | 開啟記錄位置 | 開啟記錄檔位置 | Open log location | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.UserMessage/NativeComponentLoadFailure | 无法加载程序所需的原生组件。请重新安装最新的完整安装包；详细诊断已写入下方日志。 | 無法載入應用程式所需的原生元件。請重新安裝最新的完整安裝程式；詳細診斷資料已寫入下方記錄。 | 無法載入應用程式所需的原生元件。請重新安裝最新的完整安裝套件；詳細診斷資訊已寫入下方記錄檔。 | A required native component could not be loaded. Reinstall the latest full Setup package; detailed diagnostics were written to the log below. | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |
| StartupDiagnostics.UserMessage/StartupFailure | iPhoneMirror 启动时遇到错误。详细诊断已写入下方日志。 | iPhoneMirror 啟動時發生錯誤。詳細診斷資料已寫入下方記錄。 | iPhoneMirror 啟動時發生錯誤。詳細診斷資訊已寫入下方記錄檔。 | iPhoneMirror encountered an error during startup. Detailed diagnostics were written to the log below. | — | ✅ 完全一致 | 语言字典无法加载时的独立四语回退，保留 |

### SrsLab

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 streams | 流数量：0 | 串流數量：0 | 串流數量：0 | 0 streams | — | ✅ 完全一致 | 保留 |
| Active media streams | 活动媒体流 | 使用中的媒體串流 | 使用中的媒體串流 | Active media streams | — | ✅ 完全一致 | 保留 |
| Actual FPS | 实际帧率 | 實際影格率 | 實際影格率 | Actual FPS | — | ✅ 完全一致 | 保留 |
| App | 应用名 | 應用程式名稱 | 應用程式名稱 | App | — | ✅ 完全一致 | 保留 |
| Browser camera is idle. | 浏览器摄像头空闲。 | 瀏覽器攝影機閒置。 | 瀏覽器攝影機閒置。 | Browser camera is idle. | — | ✅ 完全一致 | 保留 |
| Browser device check | 浏览器设备检查 | 瀏覽器裝置檢查 | 瀏覽器裝置檢查 | Browser device check | — | ✅ 完全一致 | 保留 |
| Browser playback | 浏览器播放 | 瀏覽器播放 | 瀏覽器播放 | Browser playback | — | ✅ 完全一致 | 保留 |
| Camera APIs are unavailable in this browser context. | 当前浏览器环境不支持摄像头接口。 | 目前瀏覽器環境不支援攝影機介面。 | 目前瀏覽器環境不支援攝影機介面。 | Camera APIs are unavailable in this browser context. | — | ✅ 完全一致 | 保留 |
| Camera is delivering frames. | 摄像头正在传输画面。 | 攝影機正在傳送畫面。 | 攝影機正在傳送畫面。 | Camera is delivering frames. | — | ✅ 完全一致 | 保留 |
| Camera is not open. | 摄像头未打开。 | 攝影機未開啟。 | 攝影機未開啟。 | Camera is not open. | — | ✅ 完全一致 | 保留 |
| Camera permission request timed out. | 摄像头权限请求超时。 | 攝影機權限請求逾時。 | 攝影機權限要求逾時。 | Camera permission request timed out. | — | ✅ 完全一致 | 保留 |
| Camera permission request timed out. Allow camera access and scan again. | 摄像头权限请求超时。请允许访问后重新扫描。 | 攝影機權限請求逾時。請允許存取後重新掃描。 | 攝影機權限要求逾時。請允許存取後重新掃描。 | Camera permission request timed out. Allow camera access and scan again. | — | ✅ 完全一致 | 保留 |
| Camera {0} | 摄像头 {0} | 攝影機 {0} | 攝影機 {0} | Camera {0} | — | ✅ 完全一致 | 保留 |
| Checking media server | 正在检查媒体服务器 | 正在檢查媒體伺服器 | 正在檢查媒體伺服器 | Checking media server | — | ✅ 完全一致 | 保留 |
| Clients: {0} | 客户端：{0} | 用戶端：{0} | 用戶端：{0} | Clients: {0} | — | ✅ 完全一致 | 保留 |
| Connected. | 已连接。 | 已連線。 | 已連線。 | Connected. | — | ✅ 完全一致 | 保留 |
| Connecting to WHEP... | 正在连接 WHEP… | 正在連線至 WHEP… | 正在連線至 WHEP… | Connecting to WHEP... | — | ✅ 完全一致 | 保留 |
| Dashboard request failed | 状态请求失败 | 狀態請求失敗 | 狀態要求失敗 | Dashboard request failed | — | ✅ 完全一致 | 保留 |
| Device | 设备 | 裝置 | 裝置 | Device | — | ✅ 完全一致 | 保留 |
| Endpoint | 地址 | 位址 | 位址 | Endpoint | — | ✅ 完全一致 | 保留 |
| Endpoints | 连接地址 | 連線位址 | 連線位址 | Endpoints | — | ✅ 完全一致 | 保留 |
| Frame analysis failed: {0} | 帧分析失败：{0} | 影格分析失敗：{0} | 影格分析失敗：{0} | Frame analysis failed: {0} | — | ✅ 完全一致 | 保留 |
| Idle. | 空闲。 | 閒置。 | 閒置。 | Idle. | — | ✅ 完全一致 | 保留 |
| LOCAL TEST BENCH | 本地测试台 | 本機測試台 | 本機測試台 | LOCAL TEST BENCH | — | ✅ 完全一致 | 保留 |
| Longest frame gap | 最长帧间隔 | 最長影格間隔 | 最長影格間隔 | Longest frame gap | — | ✅ 完全一致 | 保留 |
| Media server publish and playback endpoints | 媒体服务器推流与播放地址 | 媒體伺服器串流與播放位址 | 媒體伺服器串流與播放位址 | Media server publish and playback endpoints | — | ✅ 完全一致 | 保留 |
| Media server ready | 媒体服务器已就绪 | 媒體伺服器已就緒 | 媒體伺服器已就緒 | Media server ready | — | ✅ 完全一致 | 保留 |
| Media server unavailable | 媒体服务器不可用 | 媒體伺服器無法使用 | 媒體伺服器無法使用 | Media server unavailable | — | ✅ 完全一致 | 保留 |
| MediaMTX ready | MediaMTX 已就绪 | MediaMTX 已就緒 | MediaMTX 已就緒 | MediaMTX ready | — | ✅ 完全一致 | 保留 |
| Near-black frames | 近黑帧数 | 接近全黑的影格數 | 接近全黑的影格數 | Near-black frames | — | ✅ 完全一致 | 保留 |
| Negotiating WHEP playback | 正在建立 WHEP 播放连接 | 正在建立 WHEP 播放連線 | 正在建立 WHEP 播放連線 | Negotiating WHEP playback | — | ✅ 完全一致 | 保留 |
| No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror. | 没有活动流。请从 iPhoneMirror 启动 RTMP、SRT 或 WHIP 推流。 | 沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。 | 沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。 | No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror. | — | ✅ 完全一致 | 保留 |
| No response from media server | 媒体服务器没有响应 | 媒體伺服器沒有回應 | 媒體伺服器沒有回應 | No response from media server | — | ✅ 完全一致 | 保留 |
| No video input found | 未找到视频输入 | 找不到視訊輸入 | 找不到視訊輸入 | No video input found | — | ✅ 完全一致 | 保留 |
| No video input is available. | 没有可用的视频输入。 | 沒有可用的視訊輸入。 | 沒有可用的視訊輸入。 | No video input is available. | — | ✅ 完全一致 | 保留 |
| No video input was found. | 未找到视频输入。 | 找不到視訊輸入。 | 找不到視訊輸入。 | No video input was found. | — | ✅ 完全一致 | 保留 |
| Not open | 未打开 | 未開啟 | 未開啟 | Not open | — | ✅ 完全一致 | 保留 |
| Open backend player | 打开后端播放器 | 開啟後端播放器 | 開啟後端播放器 | Open backend player | — | ✅ 完全一致 | 保留 |
| Open camera | 打开摄像头 | 開啟攝影機 | 開啟攝影機 | Open camera | — | ✅ 完全一致 | 保留 |
| PUBLISH TARGETS | 推流目标 | 串流目標 | 串流目標 | PUBLISH TARGETS | — | ✅ 完全一致 | 保留 |
| Playback state: {0} | 播放状态：{0} | 播放狀態：{0} | 播放狀態：{0} | Playback state: {0} | — | ✅ 完全一致 | 保留 |
| Protocol | 协议 | 協定 | 協定 | Protocol | — | ✅ 完全一致 | 保留 |
| Publish browser camera through WHIP | 通过 WHIP 推送浏览器摄像头画面 | 透過 WHIP 發佈瀏覽器攝影機畫面 | 透過 WHIP 發布瀏覽器攝影機畫面 | Publish browser camera through WHIP | — | ✅ 完全一致 | 保留 |
| Publish from iPhoneMirror | 从 iPhoneMirror 推流 | 從 iPhoneMirror 發佈串流 | 從 iPhoneMirror 發布串流 | Publish from iPhoneMirror | — | ✅ 完全一致 | 保留 |
| Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP. | 通过 iPhoneMirror 以 RTMP、SRT 或 WHIP 推流，再通过 WHEP 验证同一条流。 | 透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發佈串流，再透過 WHEP 驗證同一串流。 | 透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發布串流，再透過 WHEP 驗證同一串流。 | Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP. | — | ✅ 完全一致 | 保留 |
| Publish state: {0} | 推流状态：{0} | 串流狀態：{0} | 串流狀態：{0} | Publish state: {0} | — | ✅ 完全一致 | 保留 |
| Refresh | 刷新 | 重新整理 | 重新整理 | Refresh | — | ✅ 完全一致 | 保留 |
| Request camera access to list devices | 允许摄像头访问以列出设备 | 允許存取攝影機以列出裝置 | 允許存取攝影機以列出裝置 | Request camera access to list devices | — | ✅ 完全一致 | 保留 |
| Request returned HTTP {0} | 请求返回 HTTP {0} | 請求傳回 HTTP {0} | 要求傳回 HTTP {0} | Request returned HTTP {0} | — | ✅ 完全一致 | 保留 |
| Requesting browser camera... | 正在请求摄像头权限… | 正在請求攝影機權限… | 正在要求攝影機權限… | Requesting browser camera... | — | ✅ 完全一致 | 保留 |
| Resolution | 分辨率 | 解像度 | 解析度 | Resolution | — | ✅ 完全一致 | 保留 |
| Role | 用途 | 用途 | 用途 | Role | — | ✅ 完全一致 | 保留 |
| SERVER INVENTORY | 服务器状态 | 伺服器狀態 | 伺服器狀態 | SERVER INVENTORY | — | ✅ 完全一致 | 保留 |
| SRS ready | SRS 已就绪 | SRS 已就緒 | SRS 已就緒 | SRS ready | — | ✅ 完全一致 | 保留 |
| Scan cameras | 扫描摄像头 | 掃描攝影機 | 掃描攝影機 | Scan cameras | — | ✅ 完全一致 | 保留 |
| Start WHEP playback | 开始 WHEP 播放 | 開始 WHEP 播放 | 開始 WHEP 播放 | Start WHEP playback | — | ✅ 完全一致 | 保留 |
| Start monitor | 开始预览 | 開始預覽 | 開始預覽 | Start monitor | — | ✅ 完全一致 | 保留 |
| Stop | 停止 | 停止 | 停止 | Stop | — | ✅ 完全一致 | 保留 |
| Stop browser publish | 停止浏览器推流 | 停止瀏覽器串流 | 停止瀏覽器串流 | Stop browser publish | — | ✅ 完全一致 | 保留 |
| Stop playback | 停止播放 | 停止播放 | 停止播放 | Stop playback | — | ✅ 完全一致 | 保留 |
| Stream | 流名称 | 串流名稱 | 串流名稱 | Stream | — | ✅ 完全一致 | 保留 |
| Streams: {0} | 流数量：{0} | 串流數量：{0} | 串流數量：{0} | Streams: {0} | — | ✅ 完全一致 | 保留 |
| The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check. | 此页面用于验证视频传输。iPhoneMirror 可在音源可用时包含音频；浏览器摄像头推流是独立的 SRS 测试。 | 此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。 | 此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。 | The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check. | — | ✅ 完全一致 | 保留 |
| Use the same stream name in the app | 使用与应用相同的流名称 | 使用與應用程式相同的串流名稱 | 使用與應用程式相同的串流名稱 | Use the same stream name in the app | — | ✅ 完全一致 | 保留 |
| VIRTUAL CAMERA | 虚拟摄像头 | 虛擬攝影機 | 虛擬攝影機 | VIRTUAL CAMERA | — | ✅ 完全一致 | 保留 |
| Video input | 视频输入 | 視訊輸入 | 視訊輸入 | Video input | — | ✅ 完全一致 | 保留 |
| Video inputs: {0}. Selected: {1}. | 视频输入：{0}。已选择：{1}。 | 視訊輸入：{0}。已選擇：{1}。 | 視訊輸入：{0}。已選擇：{1}。 | Video inputs: {0}. Selected: {1}. | — | ✅ 完全一致 | 保留 |
| WEBRTC PLAYBACK | WebRTC 播放 | WebRTC 播放 | WebRTC 播放 | WEBRTC PLAYBACK | — | ✅ 完全一致 | 保留 |
| WHEP Playback | WHEP 播放 | WHEP 播放 | WHEP 播放 | WHEP Playback | — | ✅ 完全一致 | 保留 |
| WHEP monitor | WHEP 预览 | WHEP 預覽 | WHEP 預覽 | WHEP monitor | — | ✅ 完全一致 | 保留 |
| Waiting for a live stream | 等待实时流 | 等待即時串流 | 等待即時串流 | Waiting for a live stream | — | ✅ 完全一致 | 保留 |
| Waiting for media server. | 等待媒体服务器。 | 等待媒體伺服器。 | 等待媒體伺服器。 | Waiting for media server. | — | ✅ 完全一致 | 保留 |
| Waiting for video frames | 等待视频帧 | 等待視訊影格 | 等待視訊影格 | Waiting for video frames | — | ✅ 完全一致 | 保留 |
| WebRTC connection failed | WebRTC 连接失败 | WebRTC 連線失敗 | WebRTC 連線失敗 | WebRTC connection failed | — | ✅ 完全一致 | 保留 |
| closed | 已关闭 | 已關閉 | 已關閉 | closed | — | ✅ 完全一致 | 保留 |
| disconnected | 已断开 | 已中斷 | 已中斷連線 | disconnected | — | ✅ 完全一致 | 保留 |
| failed | 失败 | 失敗 | 失敗 | failed | — | ✅ 完全一致 | 保留 |
| iPhoneMirror SRS Test | iPhoneMirror SRS 测试 | iPhoneMirror SRS 測試 | iPhoneMirror SRS 測試 | iPhoneMirror SRS Test | — | ✅ 完全一致 | 保留 |
| iPhoneMirror Stream Lab | iPhoneMirror 推流实验室 | iPhoneMirror 串流實驗室 | iPhoneMirror 串流實驗室 | iPhoneMirror Stream Lab | — | ✅ 完全一致 | 保留 |
| unknown | 未知 | 未知 | 未知 | unknown | — | ✅ 完全一致 | 保留 |
| video | 视频 | 視訊 | 視訊 | video | — | ✅ 完全一致 | 保留 |
| {0} at {1}. | {0}（地址：{1}）。 | {0}（位址：{1}）。 | {0}（位址：{1}）。 | {0} at {1}. | — | ✅ 完全一致 | 保留 |

### SrsTest

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 streams | 流数量：0 | 串流數量：0 | 串流數量：0 | 0 streams | — | ✅ 完全一致 | 保留 |
| Active media streams | 活动媒体流 | 使用中的媒體串流 | 使用中的媒體串流 | Active media streams | — | ✅ 完全一致 | 保留 |
| Actual FPS | 实际帧率 | 實際影格率 | 實際影格率 | Actual FPS | — | ✅ 完全一致 | 保留 |
| App | 应用名 | 應用程式名稱 | 應用程式名稱 | App | — | ✅ 完全一致 | 保留 |
| Browser camera is idle. | 浏览器摄像头空闲。 | 瀏覽器攝影機閒置。 | 瀏覽器攝影機閒置。 | Browser camera is idle. | — | ✅ 完全一致 | 保留 |
| Browser device check | 浏览器设备检查 | 瀏覽器裝置檢查 | 瀏覽器裝置檢查 | Browser device check | — | ✅ 完全一致 | 保留 |
| Browser playback | 浏览器播放 | 瀏覽器播放 | 瀏覽器播放 | Browser playback | — | ✅ 完全一致 | 保留 |
| Camera APIs are unavailable in this browser context. | 当前浏览器环境不支持摄像头接口。 | 目前瀏覽器環境不支援攝影機介面。 | 目前瀏覽器環境不支援攝影機介面。 | Camera APIs are unavailable in this browser context. | — | ✅ 完全一致 | 保留 |
| Camera is delivering frames. | 摄像头正在传输画面。 | 攝影機正在傳送畫面。 | 攝影機正在傳送畫面。 | Camera is delivering frames. | — | ✅ 完全一致 | 保留 |
| Camera is not open. | 摄像头未打开。 | 攝影機未開啟。 | 攝影機未開啟。 | Camera is not open. | — | ✅ 完全一致 | 保留 |
| Camera permission request timed out. | 摄像头权限请求超时。 | 攝影機權限請求逾時。 | 攝影機權限要求逾時。 | Camera permission request timed out. | — | ✅ 完全一致 | 保留 |
| Camera permission request timed out. Allow camera access and scan again. | 摄像头权限请求超时。请允许访问后重新扫描。 | 攝影機權限請求逾時。請允許存取後重新掃描。 | 攝影機權限要求逾時。請允許存取後重新掃描。 | Camera permission request timed out. Allow camera access and scan again. | — | ✅ 完全一致 | 保留 |
| Camera {0} | 摄像头 {0} | 攝影機 {0} | 攝影機 {0} | Camera {0} | — | ✅ 完全一致 | 保留 |
| Checking media server | 正在检查媒体服务器 | 正在檢查媒體伺服器 | 正在檢查媒體伺服器 | Checking media server | — | ✅ 完全一致 | 保留 |
| Clients: {0} | 客户端：{0} | 用戶端：{0} | 用戶端：{0} | Clients: {0} | — | ✅ 完全一致 | 保留 |
| Connected. | 已连接。 | 已連線。 | 已連線。 | Connected. | — | ✅ 完全一致 | 保留 |
| Connecting to WHEP... | 正在连接 WHEP… | 正在連線至 WHEP… | 正在連線至 WHEP… | Connecting to WHEP... | — | ✅ 完全一致 | 保留 |
| Dashboard request failed | 状态请求失败 | 狀態請求失敗 | 狀態要求失敗 | Dashboard request failed | — | ✅ 完全一致 | 保留 |
| Device | 设备 | 裝置 | 裝置 | Device | — | ✅ 完全一致 | 保留 |
| Endpoint | 地址 | 位址 | 位址 | Endpoint | — | ✅ 完全一致 | 保留 |
| Endpoints | 连接地址 | 連線位址 | 連線位址 | Endpoints | — | ✅ 完全一致 | 保留 |
| Frame analysis failed: {0} | 帧分析失败：{0} | 影格分析失敗：{0} | 影格分析失敗：{0} | Frame analysis failed: {0} | — | ✅ 完全一致 | 保留 |
| Idle. | 空闲。 | 閒置。 | 閒置。 | Idle. | — | ✅ 完全一致 | 保留 |
| LOCAL TEST BENCH | 本地测试台 | 本機測試台 | 本機測試台 | LOCAL TEST BENCH | — | ✅ 完全一致 | 保留 |
| Longest frame gap | 最长帧间隔 | 最長影格間隔 | 最長影格間隔 | Longest frame gap | — | ✅ 完全一致 | 保留 |
| Media server publish and playback endpoints | 媒体服务器推流与播放地址 | 媒體伺服器串流與播放位址 | 媒體伺服器串流與播放位址 | Media server publish and playback endpoints | — | ✅ 完全一致 | 保留 |
| Media server ready | 媒体服务器已就绪 | 媒體伺服器已就緒 | 媒體伺服器已就緒 | Media server ready | — | ✅ 完全一致 | 保留 |
| Media server unavailable | 媒体服务器不可用 | 媒體伺服器無法使用 | 媒體伺服器無法使用 | Media server unavailable | — | ✅ 完全一致 | 保留 |
| MediaMTX ready | MediaMTX 已就绪 | MediaMTX 已就緒 | MediaMTX 已就緒 | MediaMTX ready | — | ✅ 完全一致 | 保留 |
| Near-black frames | 近黑帧数 | 接近全黑的影格數 | 接近全黑的影格數 | Near-black frames | — | ✅ 完全一致 | 保留 |
| Negotiating WHEP playback | 正在建立 WHEP 播放连接 | 正在建立 WHEP 播放連線 | 正在建立 WHEP 播放連線 | Negotiating WHEP playback | — | ✅ 完全一致 | 保留 |
| No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror. | 没有活动流。请从 iPhoneMirror 启动 RTMP、SRT 或 WHIP 推流。 | 沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。 | 沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。 | No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror. | — | ✅ 完全一致 | 保留 |
| No response from media server | 媒体服务器没有响应 | 媒體伺服器沒有回應 | 媒體伺服器沒有回應 | No response from media server | — | ✅ 完全一致 | 保留 |
| No video input found | 未找到视频输入 | 找不到視訊輸入 | 找不到視訊輸入 | No video input found | — | ✅ 完全一致 | 保留 |
| No video input is available. | 没有可用的视频输入。 | 沒有可用的視訊輸入。 | 沒有可用的視訊輸入。 | No video input is available. | — | ✅ 完全一致 | 保留 |
| No video input was found. | 未找到视频输入。 | 找不到視訊輸入。 | 找不到視訊輸入。 | No video input was found. | — | ✅ 完全一致 | 保留 |
| Not open | 未打开 | 未開啟 | 未開啟 | Not open | — | ✅ 完全一致 | 保留 |
| Open backend player | 打开后端播放器 | 開啟後端播放器 | 開啟後端播放器 | Open backend player | — | ✅ 完全一致 | 保留 |
| Open camera | 打开摄像头 | 開啟攝影機 | 開啟攝影機 | Open camera | — | ✅ 完全一致 | 保留 |
| PUBLISH TARGETS | 推流目标 | 串流目標 | 串流目標 | PUBLISH TARGETS | — | ✅ 完全一致 | 保留 |
| Playback state: {0} | 播放状态：{0} | 播放狀態：{0} | 播放狀態：{0} | Playback state: {0} | — | ✅ 完全一致 | 保留 |
| Protocol | 协议 | 協定 | 協定 | Protocol | — | ✅ 完全一致 | 保留 |
| Publish browser camera through WHIP | 通过 WHIP 推送浏览器摄像头画面 | 透過 WHIP 發佈瀏覽器攝影機畫面 | 透過 WHIP 發布瀏覽器攝影機畫面 | Publish browser camera through WHIP | — | ✅ 完全一致 | 保留 |
| Publish from iPhoneMirror | 从 iPhoneMirror 推流 | 從 iPhoneMirror 發佈串流 | 從 iPhoneMirror 發布串流 | Publish from iPhoneMirror | — | ✅ 完全一致 | 保留 |
| Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP. | 通过 iPhoneMirror 以 RTMP、SRT 或 WHIP 推流，再通过 WHEP 验证同一条流。 | 透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發佈串流，再透過 WHEP 驗證同一串流。 | 透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發布串流，再透過 WHEP 驗證同一串流。 | Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP. | — | ✅ 完全一致 | 保留 |
| Publish state: {0} | 推流状态：{0} | 串流狀態：{0} | 串流狀態：{0} | Publish state: {0} | — | ✅ 完全一致 | 保留 |
| Refresh | 刷新 | 重新整理 | 重新整理 | Refresh | — | ✅ 完全一致 | 保留 |
| Request camera access to list devices | 允许摄像头访问以列出设备 | 允許存取攝影機以列出裝置 | 允許存取攝影機以列出裝置 | Request camera access to list devices | — | ✅ 完全一致 | 保留 |
| Request returned HTTP {0} | 请求返回 HTTP {0} | 請求傳回 HTTP {0} | 要求傳回 HTTP {0} | Request returned HTTP {0} | — | ✅ 完全一致 | 保留 |
| Requesting browser camera... | 正在请求摄像头权限… | 正在請求攝影機權限… | 正在要求攝影機權限… | Requesting browser camera... | — | ✅ 完全一致 | 保留 |
| Resolution | 分辨率 | 解像度 | 解析度 | Resolution | — | ✅ 完全一致 | 保留 |
| Role | 用途 | 用途 | 用途 | Role | — | ✅ 完全一致 | 保留 |
| SERVER INVENTORY | 服务器状态 | 伺服器狀態 | 伺服器狀態 | SERVER INVENTORY | — | ✅ 完全一致 | 保留 |
| SRS ready | SRS 已就绪 | SRS 已就緒 | SRS 已就緒 | SRS ready | — | ✅ 完全一致 | 保留 |
| Scan cameras | 扫描摄像头 | 掃描攝影機 | 掃描攝影機 | Scan cameras | — | ✅ 完全一致 | 保留 |
| Start WHEP playback | 开始 WHEP 播放 | 開始 WHEP 播放 | 開始 WHEP 播放 | Start WHEP playback | — | ✅ 完全一致 | 保留 |
| Start monitor | 开始预览 | 開始預覽 | 開始預覽 | Start monitor | — | ✅ 完全一致 | 保留 |
| Stop | 停止 | 停止 | 停止 | Stop | — | ✅ 完全一致 | 保留 |
| Stop browser publish | 停止浏览器推流 | 停止瀏覽器串流 | 停止瀏覽器串流 | Stop browser publish | — | ✅ 完全一致 | 保留 |
| Stop playback | 停止播放 | 停止播放 | 停止播放 | Stop playback | — | ✅ 完全一致 | 保留 |
| Stream | 流名称 | 串流名稱 | 串流名稱 | Stream | — | ✅ 完全一致 | 保留 |
| Streams: {0} | 流数量：{0} | 串流數量：{0} | 串流數量：{0} | Streams: {0} | — | ✅ 完全一致 | 保留 |
| The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check. | 此页面用于验证视频传输。iPhoneMirror 可在音源可用时包含音频；浏览器摄像头推流是独立的 SRS 测试。 | 此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。 | 此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。 | The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check. | — | ✅ 完全一致 | 保留 |
| Use the same stream name in the app | 使用与应用相同的流名称 | 使用與應用程式相同的串流名稱 | 使用與應用程式相同的串流名稱 | Use the same stream name in the app | — | ✅ 完全一致 | 保留 |
| VIRTUAL CAMERA | 虚拟摄像头 | 虛擬攝影機 | 虛擬攝影機 | VIRTUAL CAMERA | — | ✅ 完全一致 | 保留 |
| Video input | 视频输入 | 視訊輸入 | 視訊輸入 | Video input | — | ✅ 完全一致 | 保留 |
| Video inputs: {0}. Selected: {1}. | 视频输入：{0}。已选择：{1}。 | 視訊輸入：{0}。已選擇：{1}。 | 視訊輸入：{0}。已選擇：{1}。 | Video inputs: {0}. Selected: {1}. | — | ✅ 完全一致 | 保留 |
| WEBRTC PLAYBACK | WebRTC 播放 | WebRTC 播放 | WebRTC 播放 | WEBRTC PLAYBACK | — | ✅ 完全一致 | 保留 |
| WHEP Playback | WHEP 播放 | WHEP 播放 | WHEP 播放 | WHEP Playback | — | ✅ 完全一致 | 保留 |
| WHEP monitor | WHEP 预览 | WHEP 預覽 | WHEP 預覽 | WHEP monitor | — | ✅ 完全一致 | 保留 |
| Waiting for a live stream | 等待实时流 | 等待即時串流 | 等待即時串流 | Waiting for a live stream | — | ✅ 完全一致 | 保留 |
| Waiting for media server. | 等待媒体服务器。 | 等待媒體伺服器。 | 等待媒體伺服器。 | Waiting for media server. | — | ✅ 完全一致 | 保留 |
| Waiting for video frames | 等待视频帧 | 等待視訊影格 | 等待視訊影格 | Waiting for video frames | — | ✅ 完全一致 | 保留 |
| WebRTC connection failed | WebRTC 连接失败 | WebRTC 連線失敗 | WebRTC 連線失敗 | WebRTC connection failed | — | ✅ 完全一致 | 保留 |
| closed | 已关闭 | 已關閉 | 已關閉 | closed | — | ✅ 完全一致 | 保留 |
| disconnected | 已断开 | 已中斷 | 已中斷連線 | disconnected | — | ✅ 完全一致 | 保留 |
| failed | 失败 | 失敗 | 失敗 | failed | — | ✅ 完全一致 | 保留 |
| iPhoneMirror SRS Test | iPhoneMirror SRS 测试 | iPhoneMirror SRS 測試 | iPhoneMirror SRS 測試 | iPhoneMirror SRS Test | — | ✅ 完全一致 | 保留 |
| iPhoneMirror Stream Lab | iPhoneMirror 推流实验室 | iPhoneMirror 串流實驗室 | iPhoneMirror 串流實驗室 | iPhoneMirror Stream Lab | — | ✅ 完全一致 | 保留 |
| unknown | 未知 | 未知 | 未知 | unknown | — | ✅ 完全一致 | 保留 |
| video | 视频 | 視訊 | 視訊 | video | — | ✅ 完全一致 | 保留 |
| {0} at {1}. | {0}（地址：{1}）。 | {0}（位址：{1}）。 | {0}（位址：{1}）。 | {0} at {1}. | — | ✅ 完全一致 | 保留 |

### Launcher

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| ExecutableMissing | 找不到 iPhoneMirror.Driver.exe。请将启动器放在驱动管理器所在文件夹。 | 找不到 iPhoneMirror.Driver.exe。請將啟動器放在驅動程式管理員所在資料夾。 | 找不到 iPhoneMirror.Driver.exe。請將啟動器放在驅動程式管理員所在資料夾。 | iPhoneMirror.Driver.exe was not found. Place this launcher in the driver manager folder. | — | ❌ 缺失 → ✅ 已修复 | 独立 CMD 无法加载语言字典，故障时显示简体、繁体和英文回退；UTF-8/CRLF 防止乱码；独立 CMD 启动器无语言字典，异常时同时显示三语回退 |
| OperationIncomplete | 操作未完成。退出代码：%EXIT_CODE% | 操作未完成。結束代碼：%EXIT_CODE% | 操作未完成。結束代碼：%EXIT_CODE% | The operation did not complete. Exit code: %EXIT_CODE% | — | ❌ 缺失 → ✅ 已修复 | 独立启动器异常结果补齐三语，保留退出代码变量；独立 CMD 启动器无语言字典，异常时同时显示三语回退 |
| Title | iPhoneMirror | iPhoneMirror | iPhoneMirror | iPhoneMirror | — | ❌ 含义不一致 → ✅ 已修复 | 窗口标题保留产品名，避免暗示删除所有设备驱动；原设备范围不变；独立 CMD 启动器无语言字典，异常时同时显示三语回退 |

### 代码调用对应关系

| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Code/MainViewModel.ShowControlOperationError | 蓝牙 / 无线 → 对应控制模式 | 藍牙 / 無線 → 對應控制模式 | 见对应资源 Key | Bluetooth / wireless → corresponding control mode | — | ❌ 含义不一致 → ✅ 已修复 | 补齐香港繁体的蓝牙/无线传输名称匹配，避免错误归类为有线控制 |
| Code/ReverseControlStatusWindow.ModeSummary | 有线控制 | 有線控制 | 见对应资源 Key | Wired control | — | ❌ 术语不一致 → ✅ 已修复 | 有线模式摘要复用 WiredControlLabel，与按钮和状态名称一致 |
| Code/DriverCleanupHost.ExecutableMissing | 无法确定驱动管理器的可执行文件路径。 | 無法確定驅動程式管理員的可執行檔路徑。 | 见对应资源 Key | The driver manager executable path could not be determined. | — | ❌ 缺失 → ✅ 已修复 | 复用 DriverExecutableMissing；界面使用所选语言，字典加载前保留英文诊断回退 |
| Code/ControlStatusService.Cancelled | 已取消反向控制 | 已取消反向控制 | 见对应资源 Key | Reverse control cancelled | — | ❌ 缺失 → ✅ 已修复 | 默认取消说明原为硬编码中文；调用时读取现有 ControlStageCancelled 资源 |

## 代码位置与修改前记录

| Key / 位置 | 修改前文字 | 修改说明 | 当前调用位置 |
| --- | --- | --- | --- |
| App/AboutDescription | zh-CN: 低延迟 iPhone 屏幕投射、录制、直播与虚拟摄像头。；zh-HK: 低延遲 iPhone 螢幕投射、錄製、直播與虛擬攝影機。 | 关于页沿用投屏、推流正式术语 | src/App/Windows/AboutWindow.xaml:69 |
| App/AboutSubtitle | zh-HK: 應用程式資訊、更新和診斷 | 统一香港繁体中文既有用语 | src/App/Windows/AboutWindow.xaml:34 |
| App/AdvancedUsbResolutionHelp | en-US: Enter a value such as 1920×1080. Apply ignores normal preview presets; disabling advanced mode restores them. | 优化自然表达和可读性 | src/App/Windows/AdvancedSettingsWindow.xaml:22 |
| App/BluetoothClientBindingHint | zh-HK: 首次使用此投屏裝置時，請確認清單中的藍牙用戶端就是目前裝置。之後切換分頁會自動使用此綁定。 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:1821；src/App/Windows/BluetoothClientBindingWindow.xaml:34 |
| App/BluetoothControlConnected | zh-CN: iPhone/iPad 已连接蓝牙反向控制；zh-HK: iPhone/iPad 已連接藍牙反向控制 | 状态及操作标签统一为蓝牙控制 | src/App/ViewModels/MainViewModel.cs:1582；src/App/ViewModels/MainViewModel.cs:2016 |
| App/BluetoothControlOff | zh-CN: 蓝牙反向控制未启用；zh-HK: 藍牙反向控制未啟用 | 状态及操作标签统一为蓝牙控制 | src/App/ViewModels/MainViewModel.cs:1132 |
| App/BluetoothControlPromptAutoCloseFormat | en-US: Closes automatically in {0} seconds | 秒使用单位 s，适用于倒计时 1 秒 | src/App/Windows/BluetoothControlNoticeWindow.xaml.cs:85 |
| App/BluetoothControlPromptShortcutFormat | en-US: Press {0} to release | 补齐释放对象 | src/App/Windows/BluetoothControlNoticeWindow.xaml.cs:71 |
| App/BluetoothControlPromptTitle | zh-CN: 蓝牙反向控制已开启；zh-HK: 藍牙反向控制已啟用 | 状态及操作标签统一为蓝牙控制 | src/App/ViewModels/MainViewModel.cs:3126；src/App/Windows/BluetoothControlNoticeWindow.xaml.cs:36 |
| App/BluetoothControlReportMapChangedStatus | en-US: Re-pair required | 优化自然表达和可读性 | src/App/Windows/BluetoothControlNoticeWindow.xaml.cs:83 |
| App/BluetoothControlReportMapChangedTitle | zh-CN: 蓝牙控制需要刷新配对；en-US: Bluetooth control needs pairing refresh | 优化自然表达和可读性 | src/App/ViewModels/MainViewModel.cs:1702；src/App/Windows/BluetoothControlNoticeWindow.xaml.cs:35 |
| App/BluetoothControlStartFailedFormat | zh-CN: 蓝牙反控启动失败：{0}；zh-HK: 藍牙反向控制啟動失敗：{0}；en-US: Bluetooth reverse-control startup failed: {0} | 状态及操作标签统一为蓝牙控制 | src/App/MainWindow.xaml.cs:5474 |
| App/BluetoothHidCharacteristicFailedFormat | en-US: $"Could not create HID characteristic {uuid}: {result.Error}" | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 | src/App/Services/BluetoothHidMouseService.cs:1009 |
| App/BluetoothHidReportDescriptorFailedFormat | en-US: $"Could not create HID report descriptor: {result.Error}" | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 | src/App/Services/BluetoothHidMouseService.cs:1028 |
| App/BluetoothHidWheelDescriptorFailedFormat | en-US: $"Could not create HID wheel feature descriptor: {result.Error}" | 补齐蓝牙启动失败详情中的第一方提示；保留系统错误码 | src/App/Services/BluetoothHidMouseService.cs:1889 |
| App/BluetoothLandscapeMouseLeft90 | zh-CN: 左90度；zh-HK: 左90度；en-US: Left 90 degrees | 优化自然表达和可读性 | 见 Key 所指文件或资源字典 |
| App/BluetoothLandscapeMouseLeft90Reverse | zh-CN: 左90度（反）；zh-HK: 左90度（反）；en-US: Left 90 degrees (reversed) | 优化自然表达和可读性 | 见 Key 所指文件或资源字典 |
| App/BluetoothLandscapeMouseRight90 | zh-CN: 右90度；zh-HK: 右90度；en-US: Right 90 degrees | 优化自然表达和可读性 | 见 Key 所指文件或资源字典 |
| App/BluetoothLandscapeMouseRight90Reverse | zh-CN: 右90度（反）；zh-HK: 右90度（反）；en-US: Right 90 degrees (reversed) | 优化自然表达和可读性 | 见 Key 所指文件或资源字典 |
| App/BluetoothMouseSettingsSaveFailed | zh-CN: 蓝牙反控鼠标设置保存失败；zh-HK: 藍牙反控滑鼠設定儲存失敗 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:584 |
| App/BluetoothMouseSettingsTitle | zh-CN: 蓝牙反控鼠标设置；zh-HK: 藍牙反控滑鼠設定 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/MainWindow.xaml:1841 |
| App/CaptureActionReconnectDevice | zh-HK: 請保持裝置解鎖，重新插拔傳輸線後再試。 | 统一香港繁体中文既有用语 | src/App/Services/CaptureErrorGuidance.cs:107；src/App/Services/CaptureErrorGuidance.cs:117；src/App/Services/CaptureErrorGuidance.cs:127；src/App/Services/CaptureErrorGuidance.cs:138；src/App/Windows/CaptureStatusNoticeWindow.xaml.cs:131 |
| App/CaptureActionVideoRetry | zh-HK: 請停止螢幕鏡像後重新開始；仍無畫面時重新連接傳輸線。 | 统一香港繁体中文既有用语 | src/App/Services/CaptureErrorGuidance.cs:120；src/App/Windows/CaptureStatusNoticeWindow.xaml.cs:112 |
| App/CaptureHandshaking | zh-HK: 正在建立螢幕鏡像會話… | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:5783 |
| App/CaptureNoPingCableAction | zh-HK: 更換 Apple 原裝或 MFi 認證傳輸線 | 统一香港繁体中文既有用语 | src/App/Windows/CaptureRecoveryWindow.xaml:79 |
| App/CaptureNoPingRecovery | zh-HK: 裝置已連接，但長時間沒有回應螢幕鏡像握手。<br><br>請重新啟動 iPhone 或 iPad，然後使用 Apple 原裝或 MFi 認證傳輸線重新連接後重試。<br><br>重試時請保持裝置解鎖。 | 统一香港繁体中文既有用语 | 见 Key 所指文件或资源字典 |
| App/CaptureNoticeUsbHint | zh-HK: 請重新插拔傳輸線後重試 | 统一香港繁体中文既有用语 | src/App/Windows/CaptureStatusNoticeWindow.xaml.cs:40 |
| App/CaptureUsbConfigurationRecovery | zh-HK: 未能切換 iPhone 或 iPad 的 USB 螢幕鏡像設定。<br><br>請依序嘗試：<br>1. 拔下傳輸線，等待幾秒後重新插入<br>2. 重新啟動 iPhone 或 iPad 後再連接<br>3. 更換 Apple 原裝或 MFi 認證傳輸線<br><br>重新連接並開始螢幕鏡像時，請保持裝置處於解鎖狀態。 | 统一香港繁体中文既有用语 | 见 Key 所指文件或资源字典 |
| App/CheckingForUpdates | en-US: Connecting to the update services… | 明确当前为更新检查状态 | src/App/Windows/AboutWindow.xaml.cs:117 |
| App/CloseOtherInstances | en-US: Close other window | 操作针对其他全部实例 | src/App/Windows/InstanceConflictWindow.xaml:94 |
| App/CloseOtherInstancesFailedFormat | en-US: {0} iPhoneMirror instance could not be closed. Close it from the taskbar or Task Manager, then try again. | 数量可能大于 1，避免固定单数 instance/it | src/App/Windows/InstanceConflictWindow.xaml.cs:109 |
| App/ClosingOtherInstances | en-US: Safely closing the other iPhoneMirror window… | 操作针对其他全部实例 | src/App/Windows/InstanceConflictWindow.xaml:81 |
| App/ControlAutoReconnectingChannel | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2572；src/App/ViewModels/MainViewModel.cs:2623 |
| App/ControlBindingNotFound | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2428 |
| App/ControlBluetoothBindingAdvice | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1499 |
| App/ControlBluetoothBindingRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1498 |
| App/ControlBluetoothCancelled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1505 |
| App/ControlBluetoothDisconnected | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2004 |
| App/ControlBluetoothPairingTitle | zh-CN: 请在手机和 Windows 上完成蓝牙连接；zh-HK: 請在手機及 Windows 上完成藍牙連線；en-US: Complete Bluetooth pairing on your phone and Windows | 阶段要求为配对，同时适用于 iPhone/iPad | src/App/Windows/ReverseControlStatusWindow.xaml:59 |
| App/ControlBluetoothReadyToPair | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1574 |
| App/ControlBluetoothStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1491；src/App/ViewModels/MainViewModel.cs:1601 |
| App/ControlBluetoothTouchFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2010 |
| App/ControlCheckConnectionAdvice | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/ControlStatusService.cs:349 |
| App/ControlCheckingBluetoothBinding | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/ControlStatusService.cs:213；src/App/ViewModels/MainViewModel.cs:1485 |
| App/ControlCheckingBluetoothSupport | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1550 |
| App/ControlCheckingDeviceBinding | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/ControlStatusService.cs:214；src/App/ViewModels/MainViewModel.cs:2149；src/App/ViewModels/MainViewModel.cs:2420 |
| App/ControlCheckingDevicePermissions | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2157；src/App/ViewModels/MainViewModel.cs:2430 |
| App/ControlCheckingHidChannel | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1997 |
| App/ControlCloseCountdown | en-US: This window closes in {0} seconds | 秒使用单位 s，适用于倒计时 1 秒 | src/App/Windows/ReverseControlStatusWindow.xaml.cs:35 |
| App/ControlConnectingChannel | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2170；src/App/ViewModels/MainViewModel.cs:2456 |
| App/ControlDeviceNotReady | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1491；src/App/ViewModels/MainViewModel.cs:2152；src/App/ViewModels/MainViewModel.cs:2422 |
| App/ControlHidRouteFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2011 |
| App/ControlInitializingInput | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2185；src/App/ViewModels/MainViewModel.cs:2458 |
| App/ControlOperationFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2808；src/App/ViewModels/MainViewModel.cs:2810 |
| App/ControlPreparingInput | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2186；src/App/ViewModels/MainViewModel.cs:2459 |
| App/ControlPreparingSupportFiles | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2443 |
| App/ControlPrerequisiteCancelled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1505；src/App/ViewModels/MainViewModel.cs:2154；src/App/ViewModels/MainViewModel.cs:2424 |
| App/ControlReadyDescription | 原为代码硬编码；对应源码变更见工作区 diff | 控制就绪默认提示改为运行时查找资源 | src/App/Services/ControlStatusService.cs:292 |
| App/ControlReconnectingChannel | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2255 |
| App/ControlRecoveryFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2606；src/App/ViewModels/MainViewModel.cs:2978；src/App/ViewModels/MainViewModel.cs:3021 |
| App/ControlStageConnecting | en-US: Connecting control | 优化自然表达和可读性 | 见 Key 所指文件或资源字典 |
| App/ControlStageFailed | en-US: Unable to start reverse control | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2483 |
| App/ControlStagePreparingDeviceSupport | en-US: Preparing device support | 补齐准备对象为文件 | 见 Key 所指文件或资源字典 |
| App/ControlStageSwitchingBluetoothPeripheral | en-US: Preparing Bluetooth peripheral mode | 与实际切换阶段及中文一致 | 见 Key 所指文件或资源字典 |
| App/ControlSwitchingBluetoothMode | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:1551 |
| App/ControlTotalDuration | en-US: Connection time: {0:0.0} seconds | 与倒计时单位保持一致 | src/App/Windows/ReverseControlStatusWindow.xaml.cs:37 |
| App/ControlWiredCancelled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2424 |
| App/ControlWiredRetryFormat | 原为代码硬编码；对应源码变更见工作区 diff | 自动重连计数提示迁入三语格式资源 | src/App/ViewModels/MainViewModel.cs:2958 |
| App/ControlWiredStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2422；src/App/ViewModels/MainViewModel.cs:2428 |
| App/ControlWirelessCancelled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2154 |
| App/ControlWirelessRecoveryDisconnected | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2327 |
| App/ControlWirelessRecoveryFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2359 |
| App/ControlWirelessRetryFormat | 原为代码硬编码；对应源码变更见工作区 diff | 自动重连计数提示迁入三语格式资源 | src/App/ViewModels/MainViewModel.cs:2300 |
| App/ControlWirelessStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2152 |
| App/ControlWirelessStartupDisconnected | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2181 |
| App/ControlWirelessStartupFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/ViewModels/MainViewModel.cs:2197 |
| App/DecoderStatusDetecting | zh-CN: 解码器状态：正在读取实际解码链路...；zh-HK: 解碼器狀態：正在讀取實際解碼鏈路...；en-US: Decoder status: detecting the active decode path... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:5540 |
| App/DeveloperAboutDescription | zh-HK: 版本、診斷和更新資訊 | 统一香港繁体中文既有用语 | src/App/Windows/DeveloperToolsWindow.xaml.cs:49；src/App/Windows/DeveloperToolsWindow.xaml.cs:126 |
| App/DeveloperAdvancedSettingsDescription | zh-HK: 解析度輸入驗證 | 统一香港繁体中文既有用语 | src/App/Windows/DeveloperToolsWindow.xaml.cs:53 |
| App/DeveloperDriverDescription | zh-HK: 開啟獨立驅動程式管理器 | 统一香港繁体中文既有用语 | src/App/Windows/DeveloperToolsWindow.xaml.cs:48；src/App/Windows/DeveloperToolsWindow.xaml.cs:125 |
| App/DeveloperOutputDescription | en-US: Recording, streaming, and camera | 补齐虚拟摄像头含义 | src/App/Windows/DeveloperToolsWindow.xaml.cs:47；src/App/Windows/DeveloperToolsWindow.xaml.cs:124 |
| App/DeveloperProjectionSettings | en-US: Projection settings window | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/Windows/DeveloperToolsWindow.xaml.cs:70；src/App/Windows/DeveloperToolsWindow.xaml.cs:150 |
| App/DeveloperPromptDescription | zh-HK: 標準確認和資訊版面 | 统一香港繁体中文既有用语 | src/App/Windows/DeveloperToolsWindow.xaml.cs:61 |
| App/DeveloperSettings | en-US: Projection settings | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/Windows/DeveloperToolsWindow.xaml.cs:46；src/App/Windows/DeveloperToolsWindow.xaml.cs:114 |
| App/DeveloperUpdateDescription | zh-HK: 版本資訊和發佈說明 | 统一香港繁体中文既有用语 | src/App/Windows/DeveloperToolsWindow.xaml.cs:74 |
| App/DeviceBindingBind | zh-CN: 绑定选择设备 | 优化自然表达和可读性 | src/App/Windows/DeviceBindingWindow.xaml:22；src/App/Windows/DeviceBindingWindow.xaml:23 |
| App/DeviceBindingBluetoothDescription | en-US: Click “Connect device” to temporarily advertise a Bluetooth HID service. On the iPhone/iPad, open Settings → Bluetooth and pair with this computer, then choose the HID device in the waiting window. Only the binding is saved; reverse control is not started.；zh-CN: 点击“连接设备”后，电脑会临时广播 Bluetooth HID 服务。请在 iPhone/iPad 的“设置”→“蓝牙”中选择此电脑完成配对，再点击等待窗口中的“下一步”选择 HID 设备。完成后只保存绑定，不会启动反控。 | 补齐英文遗漏的“下一步”操作；统一反向控制正式名称 | src/App/Windows/DeviceBindingWindow.xaml:24 |
| App/DeviceBindingBluetoothWaitingDescription | zh-CN: 请在 iPhone/iPad 打开“设置”→“蓝牙”，在设备列表中选择此电脑并完成配对。配对成功后返回这里，点击“下一步”选择刚刚连接的 HID 设备。此流程只保存设备绑定，不会启动蓝牙反控。 | 统一反向控制正式名称 | src/App/Windows/BluetoothConnectionWindow.xaml:8 |
| App/DeviceBindingBluetoothWaitingStatus | zh-CN: 等待设备订阅 HID 服务；zh-HK: 等待裝置訂閱 HID 服務；en-US: Waiting for device to subscribe to HID service | 优化自然表达和可读性 | src/App/Windows/BluetoothConnectionWindow.xaml:8 |
| App/DeviceBindingBound | en-US: Bound | 调用条件为身份为空；英文 Bound 与实际未绑定状态相反 | src/App/Windows/DeviceBindingWindow.xaml.cs:61；src/App/Windows/DeviceBindingWindow.xaml.cs:63；src/App/Windows/DeviceBindingWindow.xaml.cs:65 |
| App/DeviceBindingModelUnknown | zh-CN: 型号信息待获取；zh-HK: 型號資料待取得 | 未知信息不应暗示正在读取 | src/App/Windows/DeviceBindingWindow.xaml.cs:27 |
| App/DeviceBindingProfileDescription | zh-CN: 为此档案选择并管理三种连接身份。；zh-HK: 為此檔案選擇並管理三種連線身份。；en-US: Choose and manage the three connection identities for this profile. | 优化自然表达和可读性 | src/App/Windows/DeviceBindingWindow.xaml:21 |
| App/DeviceBindingRenameTitle | zh-CN: 重命名设备；zh-HK: 重新命名裝置 | 修改的是应用内档案名，不是实体设备名称 | src/App/Windows/DeviceBindingWindow.xaml.cs:127 |
| App/DeviceBindingSubtitle | zh-CN: 以真实设备档案管理 USB、AirPlay 和 Bluetooth 身份绑定。；zh-HK: 以真實裝置檔案管理 USB、AirPlay 和 Bluetooth 身份綁定。；en-US: Manage USB, AirPlay, and Bluetooth identities for real device profiles. | 优化自然表达和可读性 | src/App/Windows/DeviceBindingWindow.xaml:18 |
| App/DeviceCountFormat | en-US: {0} source(s) | 使用数量标签，适用于单数与复数 | src/App/ViewModels/MainViewModel.cs:738 |
| App/DeviceProfileGuidanceWiredBody | zh-HK: 偵測到新的有線 iPhone/iPad。為了讓有線投屏、無線投屏和反向控制始終對應同一台真實裝置，請現在建立裝置檔案。<br><br>請保持裝置解鎖並完成「信任此電腦」。點選「繼續」會開啟裝置綁定器，確認或管理此裝置檔案；點選「取消」亦可稍後從「反向控制」選單開啟。 | 统一香港繁体中文既有用语 | src/App/MainWindow.xaml.cs:6544 |
| App/DeviceProfileGuidanceWirelessBody | zh-HK: 偵測到新的 AirPlay 無線裝置。為了讓無線投屏能夠配對對應的有線裝置並啟用反向控制，請現在建立裝置檔案。<br><br>請確保手機和電腦處於同一區域網絡，並保持裝置名稱可識別。點選「繼續」會開啟裝置綁定器，選擇並綁定此 AirPlay 裝置；點選「取消」亦可稍後從「反向控制」選單開啟。 | 统一香港繁体中文既有用语 | src/App/MainWindow.xaml.cs:6543 |
| App/DeviceRefreshFailedFormat | en-US: $"device refresh failed: {AppLog.Error(error.Message)}" | 补齐面向用户的日志摘要；原始诊断详情保留 | src/App/ViewModels/MainViewModel.cs:3500 |
| App/DiagnosticsCleanedFormat | en-US: Cleanup complete: removed {0} files and freed {1}; {2} in-use or inaccessible files were skipped. | 用数量标签兼容 0、1 和多个文件 | src/App/Windows/AboutWindow.xaml.cs:214 |
| App/DisplayBoundsUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/MainWindow.xaml.cs:6677 |
| App/DriverManagerActivated | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/DriverManagerLauncher.cs:35 |
| App/DriverManagerExecutableMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/DriverManagerLauncher.cs:28 |
| App/DriverManagerLaunchFailedFormat | zh-HK: 無法開啟驅動程式管理工具：{0} | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:5759 |
| App/DriverManagerOpened | zh-HK: 已開啟驅動程式管理工具 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:5755 |
| App/DriverManagerOpenedAutomatically | zh-HK: 偵測到有線裝置驅動程式異常，已自動開啟驅動程式管理工具 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:5754 |
| App/DriverManagerPathInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/DriverManagerLauncher.cs:32 |
| App/DriverManagerProcessStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/DriverManagerLauncher.cs:56 |
| App/DriverManagerStarted | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/DriverManagerLauncher.cs:57 |
| App/DriverPreflightFormat | en-US: $"driver preflight: {driverStatus.Diagnostic}" | 补齐面向用户的日志摘要；原始诊断详情保留 | src/App/ViewModels/MainViewModel.cs:5736 |
| App/DriverSafetyWarningFormat | en-US: $"driver safety warning: {driverStatus.Diagnostic}" | 补齐面向用户的日志摘要；原始诊断详情保留 | src/App/ViewModels/MainViewModel.cs:5722 |
| App/FfmpegAudioInputTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:263 |
| App/FfmpegAudioStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaCastAudioDecoder.cs:61 |
| App/FfmpegEncoderSizeUnsupported | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:203 |
| App/FfmpegEncoderUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1045 |
| App/FfmpegExitedFormat | 原为代码硬编码；对应源码变更见工作区 diff | 输出错误详情迁入三语资源 | src/App/Services/MediaOutputService.cs:462；src/App/Services/MediaOutputService.cs:576 |
| App/FfmpegNoUsableEncoderFormat | 原为代码硬编码；对应源码变更见工作区 diff | 用户可见错误详情补齐三语 | src/App/Services/MediaOutputService.cs:939 |
| App/FfmpegNotFound | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:140 |
| App/FfmpegProbeFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:134 |
| App/FfmpegProtocolUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1047 |
| App/FfmpegStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:241；src/App/Services/MediaOutputService.cs:1275 |
| App/FfmpegStartupExitedFormat | 原为代码硬编码；对应源码变更见工作区 diff | 输出错误详情迁入三语资源 | src/App/Services/MediaOutputService.cs:1110 |
| App/HlsRestartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/MainWindow.xaml.cs:3880 |
| App/ImageAdjustmentsBusy | en-US: Another projection setting is being applied. Try again when it finishes. | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/ViewModels/MainViewModel.cs:4421；src/App/ViewModels/MainViewModel.cs:6090；src/App/ViewModels/MainViewModel.cs:6091；src/App/ViewModels/MainViewModel.cs:6156；src/App/ViewModels/MainViewModel.cs:6157；src/App/ViewModels/MainViewModel.cs:6274 |
| App/IndependentWindowWiredProjection | zh-CN: 有线投屏；zh-HK: 有線投屏；en-US: Wired projection | 菜单和快捷键实际调用控制功能，不是开始投屏 | src/App/Windows/NativePreviewWindow.cs:943 |
| App/IndependentWindowWirelessProjection | zh-CN: 无线投屏；zh-HK: 無線投屏；en-US: Wireless projection | 菜单和快捷键实际调用控制功能，不是开始投屏 | src/App/Windows/NativePreviewWindow.cs:949 |
| App/InstanceConflictHint | en-US: Closing the other window continues this launch. Closing this window leaves the active mirroring session untouched. | 优化自然表达和可读性 | src/App/Windows/InstanceConflictWindow.xaml:61 |
| App/LicenseInformation | zh-HK: 許可證資訊 | 统一香港繁体中文既有用语 | src/App/Windows/AboutWindow.xaml:92 |
| App/LocalTargetMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Windows/AboutWindow.xaml.cs:179 |
| App/MediaCastDeviceName | zh-HK: 影片 App AirPlay 播放 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/Models/DeviceViewModel.cs:45；src/App/Models/DeviceViewModel.cs:128 |
| App/MediaCastLoadingVideo | zh-CN: 正在加载投放的视频...；zh-HK: 正在載入投放的影片...；en-US: Loading the cast video... | 统一进行中状态的省略号 | src/App/MainWindow.xaml.cs:3068；src/App/MainWindow.xaml.cs:3371；src/App/MainWindow.xaml.cs:3465；src/App/MainWindow.xaml.cs:3567；src/App/MainWindow.xaml.cs:3651；src/App/MainWindow.xaml.cs:3750；src/App/MainWindow.xaml.cs:3889；src/App/MainWindow.xaml.cs:4969 |
| App/MediaCastPlayReceived | zh-HK: 已收到影片 App AirPlay 播放請求 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/MainWindow.xaml.cs:2847 |
| App/MediaCastReady | zh-HK: 影片 AirPlay 接收端已就緒 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/Interop/NativeCore.cs:720；src/App/Services/MediaCastReceiverController.cs:86 |
| App/MediaCastReceiverMissing | zh-CN: 视频投放组件缺失，请重新构建发布包。；zh-HK: 影片投放元件遺失，請重新建構發佈套件。；en-US: The video casting component is missing. Rebuild the release package. | 面向最终用户提供可执行的重新安装建议 | src/App/Interop/NativeCore.cs:721；src/App/Services/MediaCastReceiverController.cs:83 |
| App/MediaCastSpeedUnsupportedBody | zh-CN: 当前投屏视频不支持 {0} 倍速，已恢复到 1x 播放。；zh-HK: 目前投放影片不支援 {0} 倍速，已恢復至 1x 播放。 | 参数已包含 x，避免显示 2x 倍速 | src/App/MainWindow.xaml.cs:4841；src/App/MainWindow.xaml.cs:4848 |
| App/MediaCastStarting | zh-HK: 正在啟動影片 AirPlay 接收端...；zh-CN: 正在启动视频投放接收端...；en-US: Starting the video casting receiver... | 功能也支持 DLNA，不能仅标为 AirPlay；统一进行中状态的省略号 | src/App/Services/MediaCastReceiverController.cs:86 |
| App/MediaCastStopped | zh-HK: 影片 App AirPlay 播放已停止 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/Interop/NativeCore.cs:753；src/App/MainWindow.xaml.cs:2843；src/App/MainWindow.xaml.cs:2950 |
| App/MediaCastTitle | zh-HK: 影片 App AirPlay 播放 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/MainWindow.xaml:1987 |
| App/MediaCastWaitingVideo | zh-HK: 等待影片 App AirPlay 播放內容...；zh-CN: 等待视频 App 投放内容...；en-US: Waiting for a video app to cast content... | 功能也支持 DLNA，不能仅标为 AirPlay；统一进行中状态的省略号 | src/App/MainWindow.xaml:1431 |
| App/MediaCastWindowTitle | zh-HK: iPhoneMirror 影片 App AirPlay 播放 | 功能也支持 DLNA，不能仅标为 AirPlay | src/App/MainWindow.xaml.cs:6790 |
| App/MediaOutputAudioInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:645 |
| App/MediaOutputAudioStalled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:605；src/App/Services/MediaOutputService.cs:756 |
| App/MediaOutputAudioUnsupported | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:641 |
| App/MediaOutputChecking | zh-CN: 正在检测 FFmpeg 输出能力...；zh-HK: 正在偵測 FFmpeg 輸出能力...；en-US: Checking FFmpeg output capabilities... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:6291 |
| App/MediaOutputDestinationRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1054 |
| App/MediaOutputFrameInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:771 |
| App/MediaOutputFrameStalled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/VirtualCameraService.cs:472 |
| App/MediaOutputFrameTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:478；src/App/Services/VirtualCameraService.cs:466 |
| App/MediaOutputInvalidDimensions | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1050 |
| App/MediaOutputRtmpAddressRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1057 |
| App/MediaOutputSrtAddressRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1060 |
| App/MediaOutputStopping | zh-CN: 正在停止输出并完成文件或推流收尾...；zh-HK: 正在停止輸出並完成檔案或串流收尾...；en-US: Stopping output and finalizing the file or stream... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:6602 |
| App/MediaOutputWhipAddressRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/MediaOutputService.cs:1063 |
| App/NavSettingsDescription | en-US: Projection settings | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/MainWindow.xaml:756 |
| App/ReadDeviceInfoFailed | zh-HK: 讀取裝置資訊失敗 | 统一香港繁体中文既有用语 | src/App/Interop/NativeCore.cs:688；src/App/Interop/NativeCore.cs:690 |
| App/RecordingFinalizeTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/MediaOutputService.cs:381 |
| App/ReverseControlBridgeNotReady | zh-CN: 当前设备的反控桥接器尚未就绪。；zh-HK: 目前裝置的反向控制橋接器尚未就緒。；en-US: The reverse-control bridge for the current device is not ready. | 统一反向控制正式名称；将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/UsbTouchBridgeHost.cs:117；src/App/ViewModels/MainViewModel.cs:1359 |
| App/ReverseControlCaptureMuxReady | zh-HK: 正在透過與投屏共存的 USB 通道連接裝置 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2742 |
| App/ReverseControlCheckingEnvironmentFormat | zh-CN: 正在检查{0}反控的开发者环境；zh-HK: 正在檢查{0}反控的開發者環境 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2736 |
| App/ReverseControlConnectingWireless | zh-CN: 正在连接无线反控桥接器 | 统一反向控制正式名称 | src/App/ViewModels/MainViewModel.cs:2158；src/App/ViewModels/MainViewModel.cs:2169 |
| App/ReverseControlDetails | zh-HK: 查看詳細資訊 | 统一香港繁体中文既有用语 | 见 Key 所指文件或资源字典 |
| App/ReverseControlNoticeErrorBadge | zh-CN: 反控未能继续；zh-HK: 反控未能繼續 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/Windows/CaptureStatusNoticeWindow.xaml.cs:35 |
| App/ReverseControlUnknownError | zh-CN: 反控桥接器报告未知错误。；zh-HK: 反控橋接器報告未知錯誤。 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2779 |
| App/ReverseControlUsbConnected | zh-CN: USB 控制已连接（设备认证状态需以实测为准）；zh-HK: USB 控制已連接（裝置驗證狀態需以實測為準）；en-US: USB control connected (device authentication status should be verified in practice) | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2465；src/App/ViewModels/MainViewModel.cs:3004 |
| App/ReverseControlUsbConnecting | zh-CN: 正在连接 USB 控制；zh-HK: 正在連接 USB 控制；en-US: Connecting USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2433；src/App/ViewModels/MainViewModel.cs:2574；src/App/ViewModels/MainViewModel.cs:2624；src/App/ViewModels/MainViewModel.cs:2959 |
| App/ReverseControlUsbEnabled | zh-CN: USB 控制已启用；zh-HK: USB 控制已啟用；en-US: USB control enabled | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2464；src/App/ViewModels/MainViewModel.cs:2591；src/App/ViewModels/MainViewModel.cs:3003 |
| App/ReverseControlUsbEnabledDirect | zh-CN: USB 控制已启用（直接 HID）；zh-HK: USB 控制已啟用（直接 HID）；en-US: USB control enabled (direct HID) | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2462；src/App/ViewModels/MainViewModel.cs:2591；src/App/ViewModels/MainViewModel.cs:3001 |
| App/ReverseControlUsbFailedFormat | zh-CN: USB 控制连接失败：{0}；zh-HK: USB 控制連線失敗：{0}；en-US: USB control connection failed: {0} | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2486；src/App/ViewModels/MainViewModel.cs:2607；src/App/ViewModels/MainViewModel.cs:2976；src/App/ViewModels/MainViewModel.cs:3023 |
| App/ReverseControlUsbOff | zh-CN: USB 控制未启用；zh-HK: USB 控制未啟用；en-US: USB control is off | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2882；src/App/ViewModels/MainViewModel.cs:2938 |
| App/ReverseControlUsbStopFailedFormat | zh-CN: USB 控制关闭失败：{0}；zh-HK: USB 控制關閉失敗：{0}；en-US: USB control shutdown failed: {0} | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2888 |
| App/ReverseControlUsbStopping | zh-CN: 正在关闭 USB 控制；zh-HK: 正在關閉 USB 控制；en-US: Stopping USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:2871 |
| App/ReverseControlWirelessConnected | zh-CN: 无线反控已连接；zh-HK: 無線反向控制已連接；en-US: Wireless reverse control connected | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2190 |
| App/ReverseControlWirelessDisconnected | zh-CN: 无线反控桥接通道已断开 | 统一反向控制正式名称 | src/App/ViewModels/MainViewModel.cs:2256 |
| App/ReverseControlWirelessEnabled | zh-CN: 无线反控已启用；zh-HK: 無線反向控制已啟用；en-US: Wireless reverse control enabled | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2190；src/App/ViewModels/MainViewModel.cs:2332 |
| App/ReverseControlWirelessEnabledDirect | zh-CN: 无线反控已启用（直接 HID）；zh-HK: 無線反向控制已啟用（直接 HID）；en-US: Wireless reverse control enabled (direct HID) | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2189；src/App/ViewModels/MainViewModel.cs:2331 |
| App/ReverseControlWirelessFailedFormat | zh-CN: 无线反控连接失败：{0}；zh-HK: 無線反向控制連線失敗：{0}；en-US: Wireless reverse-control connection failed: {0} | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2206；src/App/ViewModels/MainViewModel.cs:2356 |
| App/ReverseControlWirelessOff | zh-CN: 无线反控未启用；zh-HK: 無線反向控制未啟用；en-US: Wireless reverse control is off | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2289；src/App/ViewModels/MainViewModel.cs:2309；src/App/ViewModels/MainViewModel.cs:2391 |
| App/ReverseControlWirelessStopFailedFormat | zh-CN: 无线反控关闭失败：{0}；zh-HK: 無線反向控制關閉失敗：{0}；en-US: Wireless reverse-control shutdown failed: {0} | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:2396 |
| App/ShortcutSettingsReset | en-US: Restore default | 优化自然表达和可读性 | src/App/Windows/ShortcutSettingsWindow.xaml:32；src/App/Windows/ShortcutSettingsWindow.xaml:33 |
| App/ShortcutSettingsSaveFailed | en-US: Could not save the keyboard shortcut. | 优化自然表达和可读性 | src/App/MainWindow.xaml.cs:578 |
| App/ShortcutSettingsWiredControl | zh-CN: 有线投屏；zh-HK: 有線投屏；en-US: Wired mirroring | 菜单和快捷键实际调用控制功能，不是开始投屏 | src/App/Windows/ShortcutSettingsWindow.xaml.cs:224 |
| App/ShortcutSettingsWirelessControl | zh-CN: 无线投屏；zh-HK: 無線投屏；en-US: Wireless mirroring | 菜单和快捷键实际调用控制功能，不是开始投屏 | src/App/Windows/ShortcutSettingsWindow.xaml.cs:223 |
| App/StartBluetoothControl | zh-CN: 启用蓝牙反向控制；zh-HK: 啟用藍牙反向控制 | 状态及操作标签统一为蓝牙控制 | src/App/ViewModels/MainViewModel.cs:353 |
| App/StatusDefaultSettings | zh-HK: 預設 · 60 fps | Native 表示原始分辨率，不是默认设置 | src/App/ViewModels/MainViewModel.cs:191；src/App/ViewModels/MainViewModel.cs:1128；src/App/ViewModels/MainViewModel.cs:5966 |
| App/StopBluetoothControl | zh-CN: 关闭蓝牙反向控制；zh-HK: 關閉藍牙反向控制 | 状态及操作标签统一为蓝牙控制 | src/App/ViewModels/MainViewModel.cs:353 |
| App/StopSessionReleased | zh-HK: 螢幕鏡像已停止，會話資源已釋放 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:4723 |
| App/StopUsbRestoreWarningFormat | zh-HK: 螢幕鏡像已停止，會話資源已釋放；但未能確認 iPhone 已還原普通 USB 設定。診斷：{0} | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:4724 |
| App/StreamingStopTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Services/MediaOutputService.cs:382 |
| App/TouchBridgeAlreadyStarted | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/UsbTouchBridgeHost.cs:43 |
| App/TouchBridgeAuthenticationRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:371 |
| App/TouchBridgeDiagnosticFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:603 |
| App/TouchBridgeErrorCodeFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:601 |
| App/TouchBridgeExitedFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:582 |
| App/TouchBridgeInvalidBatch | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:189 |
| App/TouchBridgeInvalidCoordinates | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:196 |
| App/TouchBridgeInvalidOutput | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:409 |
| App/TouchBridgeInvalidPointId | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:193 |
| App/TouchBridgeNotReady | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:186；src/App/Services/DirectUsbInputBridge.cs:203；src/App/Services/DirectUsbInputBridge.cs:429；src/App/Services/DirectUsbInputBridge.cs:442；src/App/Services/DirectUsbInputBridge.cs:465；src/App/Services/DirectUsbInputBridge.cs:473；src/App/Services/DirectUsbInputBridge.cs:497；src/App/Services/DirectUsbInputBridge.cs:503；src/App/Services/DirectUsbInputBridge.cs:523；src/App/Services/DirectUsbInputBridge.cs:529 |
| App/TouchBridgeNotStarted | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:415 |
| App/TouchBridgeOutputClosed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:318 |
| App/TouchBridgeReportedError | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:402 |
| App/TouchBridgeRuntimeIncompleteFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:78 |
| App/TouchBridgeStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/DirectUsbInputBridge.cs:136 |
| App/TouchBridgeTargetMismatch | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/UsbTouchBridgeHost.cs:54 |
| App/TouchBridgeTargetMismatchFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:352 |
| App/TouchBridgeTimeoutFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:421 |
| App/TouchBridgeTransportMismatchFormat | 原为代码硬编码；对应源码变更见工作区 diff | 桥接错误提示迁入三语资源；原始诊断参数保留 | src/App/Services/DirectUsbInputBridge.cs:359 |
| App/UpdateChecksumDownloadUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:764 |
| App/UpdateChecksumEntryMissingFormat | 原为代码硬编码；对应源码变更见工作区 diff | 用户可见错误详情补齐三语 | src/App/Updater/GitHubReleaseClient.cs:704 |
| App/UpdateChecksumFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:710 |
| App/UpdateChecksumListInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:746 |
| App/UpdateChecksumListTooLarge | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:732；src/App/Updater/GitHubReleaseClient.cs:735 |
| App/UpdateChecksumRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:699 |
| App/UpdateChecksumSizeMismatch | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:738 |
| App/UpdateDigestInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:544 |
| App/UpdateDigestMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:163 |
| App/UpdateDownloadEndpointsUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:465 |
| App/UpdateDownloadUntrustedRedirect | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:822 |
| App/UpdateDownloadedNoChecksum | zh-CN: 下载完成；此 Release 未提供 SHA256 校验值。；en-US: Download complete. This release did not provide a SHA256 value. | 统一校验算法显示名称 | src/App/Windows/UpdateWindow.xaml.cs:114 |
| App/UpdateEndpointsUnavailable | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:327 |
| App/UpdateFormatUnsupported | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:189；src/App/Updater/UpdateInstallerLauncher.cs:438 |
| App/UpdateHelperMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:521 |
| App/UpdateHelperStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:258 |
| App/UpdateInstallerPackageMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:398 |
| App/UpdatePackageChanged | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:552 |
| App/UpdatePackageNotVerified | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:160 |
| App/UpdatePackageTooLarge | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:408 |
| App/UpdatePortableDirectoryUnsafe | 原为代码硬编码；对应源码变更见工作区 diff | 更新启动失败提示补齐三语 | src/App/Updater/UpdateInstallerLauncher.cs:200 |
| App/UpdatePortableElevationUnsupported | 原为代码硬编码；对应源码变更见工作区 diff | 更新启动失败提示补齐三语 | src/App/Updater/UpdateInstallerLauncher.cs:194 |
| App/UpdatePortablePackageMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:399 |
| App/UpdatePortableRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:444 |
| App/UpdateReleaseFieldMissingFormat | 原为代码硬编码；对应源码变更见工作区 diff | 用户可见错误详情补齐三语 | src/App/Updater/ReleaseParser.cs:181 |
| App/UpdateReleaseListInvalid | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/ReleaseParser.cs:45 |
| App/UpdateReleaseListTooLarge | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:383；src/App/Updater/GitHubReleaseClient.cs:385 |
| App/UpdateResponseUrlMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:819 |
| App/UpdateSetupRequired | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/UpdateInstallerLauncher.cs:441 |
| App/UpdateUnsafeFileName | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:406 |
| App/UpdateUntrustedAssetUrl | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:472 |
| App/UpdateUntrustedNotesRedirect | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:345 |
| App/UpdateUntrustedRedirect | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/App/Updater/GitHubReleaseClient.cs:380 |
| App/UpdateVerified | zh-CN: 下载完成，SHA256 校验通过。；en-US: Download complete. SHA256 verification passed. | 统一校验算法显示名称 | src/App/Windows/UpdateWindow.xaml.cs:113 |
| App/UsbControlDisable | zh-CN: 关闭 USB 控制；zh-HK: 關閉 USB 控制；en-US: Disable USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:384 |
| App/UsbControlDisableWireless | zh-CN: 关闭无线反控；zh-HK: 關閉無線反向控制；en-US: Disable wireless reverse control | 状态及操作标签统一为无线控制 | src/App/ViewModels/MainViewModel.cs:384 |
| App/UsbControlFailureDeviceNotFound | en-US: The Apple network-pairing session for this device was not found. Connect by cable once, enable “Sync with this iPhone over Wi-Fi” in Apple Devices or iTunes, and try again. | 补齐英文缺少的保持解锁、与电脑位于同一局域网条件 | src/App/ViewModels/MainViewModel.cs:2717 |
| App/UsbControlFailureGateUnavailable | zh-CN: 设备未确认 CoreDevice 触控认证已开启。为避免输入被系统静默丢弃，反控未启动。；zh-HK: 裝置未確認 CoreDevice 觸控驗證已開啟。為避免輸入被系統靜默丟棄，反控未啟動。 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2704 |
| App/UsbControlFailureTouchService | zh-CN: 设备暂未提供触控反控服务。请保持设备解锁并信任此电脑后重试；若仍失败，请改用蓝牙反控或无线反控。；zh-HK: 裝置暫未提供觸控反控服務。請保持裝置解鎖並信任此電腦後重試；若仍失敗，請改用藍牙反控或無線反控。 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2711 |
| App/UsbControlFailureUnknown | zh-CN: USB 反控桥接器返回了无法识别的错误。请查看诊断日志后重试。 | 统一反向控制正式名称 | src/App/ViewModels/MainViewModel.cs:2726 |
| App/UsbControlFailureUnsupportedIos | zh-CN: 设备拒绝了媒体流认证（9021），且没有可用的直接 Universal HID 触控通道。请重启 iPhone 后重试；若仍失败，请使用蓝牙反控。 | 统一反向控制正式名称 | src/App/ViewModels/MainViewModel.cs:2689 |
| App/UsbControlFailureWiredDeviceNotFound | zh-HK: 未能在 USB 連線中找到此裝置的控制通道。投屏運行時系統可能暫時無法存取裝置：請先停止投屏再啟動反向控制，或重新插拔數據線、解鎖手機並確認已信任此電腦後重試。 | 统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2718 |
| App/UsbControlFailureWirelessPairingRequired | zh-CN: 当前 Windows 帐户尚未完成此设备的无线 CoreDevice 配对。请先通过 USB 连接并解锁 iPhone，完成一次 USB 反控初始化后再试无线反控。；zh-HK: 目前 Windows 帳戶尚未完成此裝置的無線 CoreDevice 配對。請先透過 USB 連接並解鎖 iPhone，完成一次 USB 反控初始化後再試無線反控。 | 统一反向控制正式名称；统一香港繁体中文既有用语 | src/App/ViewModels/MainViewModel.cs:2695 |
| App/UsbControlOff | zh-CN: USB 控制未启用；zh-HK: USB 控制未啟用；en-US: USB control is off | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:1133 |
| App/UsbControlOperationFailedFormat | zh-CN: USB 控制操作失败：{0}；zh-HK: USB 控制操作失敗：{0}；en-US: USB control operation failed: {0} | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/MainWindow.xaml.cs:1314；src/App/MainWindow.xaml.cs:5492 |
| App/UsbControlPrerequisite | zh-CN: 请通过 USB 连接并在设备上信任一台 iPhone 或 iPad 后，再启用 USB 控制；zh-HK: 請透過 USB 連接並在裝置上信任一部 iPhone 或 iPad 後，再啟用 USB 控制；en-US: Connect an iPhone or iPad by USB and trust this computer before enabling USB control | 信任对象是电脑，不是 iPhone；同时统一有线控制名称 | src/App/ViewModels/MainViewModel.cs:361 |
| App/UsbControlRetry | zh-CN: 重试 USB 控制；zh-HK: 重試 USB 控制；en-US: Retry USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:383 |
| App/UsbControlStopping | zh-CN: 正在关闭 USB 控制；zh-HK: 正在關閉 USB 控制；en-US: Stopping USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/ViewModels/MainViewModel.cs:381 |
| App/UsbControlTitle | zh-CN: USB 控制；zh-HK: USB 控制；en-US: USB control | 相同功能统一为有线控制；USB 作为连接协议的说明予以保留 | src/App/MainWindow.xaml.cs:5494 |
| App/UsbModeAirPlayLabel | zh-CN: B AirPlay(实验) | 优化自然表达和可读性 | src/App/ViewModels/MainViewModel.cs:301 |
| App/UsbModeAirPlayNotice | zh-HK: 需要自訂時，連續按一下右下角版本號 5 次開啟進階模式。自訂 HPD1 尺寸存在風險，錯誤參數可能導致黑畫面、裁切或 USB 重新連接失敗。 | 优化自然表达和可读性 | src/App/ViewModels/MainViewModel.cs:302 |
| App/UsbModeDemoLabel | zh-CN: A 演示模式(推荐) | 优化自然表达和可读性 | src/App/ViewModels/MainViewModel.cs:299 |
| App/UsbModeDetailsTitle | en-US: USB projection mode details | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/Windows/UsbProjectionModeInfoWindow.xaml:6；src/App/Windows/UsbProjectionModeInfoWindow.xaml:25 |
| App/UsbProjectionModeRestarting | zh-CN: 正在按新模式重新连接当前有线设备...；zh-HK: 正在按新模式重新連接目前有線裝置...；en-US: Reconnecting the current USB device with the new mode... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:945 |
| App/UsbProjectionModeTitle | en-US: USB projection protocol | 投屏统一为 mirroring，视频应用投放仍使用 casting | src/App/MainWindow.xaml:1683 |
| App/VideoPipelineRestarting | zh-CN: 正在按新的视频管线设置重新连接当前设备...；zh-HK: 正在按新的影片管線設定重新連接目前裝置...；en-US: Reconnecting the current device with the new video pipeline... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:4625 |
| App/VideoPreferencesApplied | en-US: Local rendering settings applied (source stream unchanged) | 未重连与码流未改变不是同一含义 | src/App/Interop/NativeCore.cs:954；src/App/Interop/NativeCore.cs:967；src/App/Interop/NativeCore.cs:1069；src/App/ViewModels/MainViewModel.cs:4336 |
| App/VirtualCameraAlreadyRunning | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/VirtualCameraService.cs:376 |
| App/VirtualCameraChecking | zh-CN: 正在检测虚拟摄像头能力...；zh-HK: 正在偵測虛擬攝影機能力...；en-US: Checking virtual camera support... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:1131 |
| App/VirtualCameraInstallCancelled | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/VirtualCameraService.cs:317 |
| App/VirtualCameraInstallerExitedFormat | 原为代码硬编码；对应源码变更见工作区 diff | 输出错误详情迁入三语资源 | src/App/Services/VirtualCameraService.cs:312 |
| App/VirtualCameraInstallerStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/VirtualCameraService.cs:284 |
| App/VirtualCameraInstalling | zh-CN: 正在安装虚拟摄像头组件...；zh-HK: 正在安裝虛擬攝影機元件...；en-US: Installing the virtual camera component... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:6429 |
| App/VirtualCameraPayloadChangedFormat | 原为代码硬编码；对应源码变更见工作区 diff | 输出错误详情迁入三语资源 | src/App/Services/VirtualCameraService.cs:363 |
| App/VirtualCameraPayloadMissing | 原为代码硬编码；对应源码变更见工作区 diff | 将代码中的用户可见硬编码文字迁入三语资源 | src/App/Services/VirtualCameraService.cs:346 |
| App/VirtualCameraUninstalling | zh-CN: 正在卸载虚拟摄像头组件...；zh-HK: 正在解除安裝虛擬攝影機元件...；en-US: Uninstalling the virtual camera component... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:6468 |
| App/VirtualCameraUpdating | zh-CN: 正在更新虚拟摄像头组件...；zh-HK: 正在更新虛擬攝影機元件...；en-US: Updating the virtual camera component... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:6429 |
| App/WirelessBackendUnavailableFormat | zh-CN: {0} 无线接收组件缺失，请重新构建发布包。；zh-HK: {0} 無線接收元件遺失，請重新建構發佈套件。；en-US: The {0} wireless receiver component is missing. Rebuild the release package. | 面向最终用户提供可执行的重新安装建议 | src/App/Services/WirelessReceiverController.cs:162；src/App/ViewModels/MainViewModel.cs:3690 |
| App/WirelessConnecting | zh-CN: AirPlay 设备正在连接...；zh-HK: AirPlay 裝置正在連接...；en-US: An AirPlay device is connecting... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:5783 |
| App/WirelessControlOperationFailedFormat | zh-CN: 无线反控操作失败：{0}；zh-HK: 無線反向控制操作失敗：{0}；en-US: Wireless reverse-control operation failed: {0} | 状态及操作标签统一为无线控制 | src/App/MainWindow.xaml.cs:1335；src/App/MainWindow.xaml.cs:5511 |
| App/WirelessProfileDisconnectConfirmFormat | en-US: {0} wireless device(s) are connected. Applying “{1}” will disconnect all wireless mirroring and restart the receiver. Continue? | 用数量标签兼容单复数 | 见 Key 所指文件或资源字典 |
| App/WirelessReceiverMissing | zh-CN: 无线接收组件缺失，请重新构建发布包。；zh-HK: 無線接收元件遺失，請重新建構發佈套件。；en-US: The wireless receiver component is missing. Rebuild the release package. | 面向最终用户提供可执行的重新安装建议 | src/App/Interop/NativeCore.cs:704 |
| App/WirelessSettingsConnectedImpactFormat | en-US: {0} wireless device(s) are connected and will all be disconnected. | 消除 device(s) 表达并保留应用后断开的条件 | src/App/ViewModels/MainViewModel.cs:3722 |
| App/WirelessStarting | zh-CN: 正在启动 AirPlay 接收端...；en-US: Starting the AirPlay receiver... | 统一进行中状态的省略号 | src/App/Services/WirelessReceiverController.cs:166；src/App/ViewModels/MainViewModel.cs:5781 |
| App/WirelessStopping | zh-CN: 正在停止 AirPlay 接收端...；zh-HK: 正在停止 AirPlay 接收端...；en-US: Stopping the AirPlay receiver... | 统一进行中状态的省略号 | src/App/ViewModels/MainViewModel.cs:5785 |
| Cleanup/AssociatedDrivers | 原始文字: 关联全部驱动包：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:77；scripts/remove_selected_iphone_drivers.ps1:167；scripts/remove_selected_iphone_drivers.ps1:257；scripts/remove_selected_iphone_drivers.ps1:2029 |
| Cleanup/AssociatedNodes | 原始文字: 关联 PnP 节点：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:76；scripts/remove_selected_iphone_drivers.ps1:166；scripts/remove_selected_iphone_drivers.ps1:256；scripts/remove_selected_iphone_drivers.ps1:2024 |
| Cleanup/BluetoothExcluded | 原始文字: 安全过滤 BTHLE：$InstanceId | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:41；scripts/remove_selected_iphone_drivers.ps1:131；scripts/remove_selected_iphone_drivers.ps1:221；scripts/remove_selected_iphone_drivers.ps1:1538 |
| Cleanup/BluetoothNotListed | 原始文字: BTHLE / Bluetooth LE 设备不会显示。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:64；scripts/remove_selected_iphone_drivers.ps1:154；scripts/remove_selected_iphone_drivers.ps1:244；scripts/remove_selected_iphone_drivers.ps1:1805 |
| Cleanup/CheckRequirements | 原始文字: 请确认： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:59；scripts/remove_selected_iphone_drivers.ps1:149；scripts/remove_selected_iphone_drivers.ps1:239；scripts/remove_selected_iphone_drivers.ps1:1799 |
| Cleanup/CleanupPlan | 原始文字:  清理计划 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:73；scripts/remove_selected_iphone_drivers.ps1:163；scripts/remove_selected_iphone_drivers.ps1:253；scripts/remove_selected_iphone_drivers.ps1:2006 |
| Cleanup/CleanupResults | 原始文字:  清理结果 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:89；scripts/remove_selected_iphone_drivers.ps1:179；scripts/remove_selected_iphone_drivers.ps1:269；scripts/remove_selected_iphone_drivers.ps1:2341 |
| Cleanup/ClosingProcesses | 原始文字: 关闭 iPhoneMirror 相关进程... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:40；scripts/remove_selected_iphone_drivers.ps1:130；scripts/remove_selected_iphone_drivers.ps1:220；scripts/remove_selected_iphone_drivers.ps1:1508 |
| Cleanup/Completed | 原始文字:  Cleanup completed successfully. | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:103；scripts/remove_selected_iphone_drivers.ps1:193；scripts/remove_selected_iphone_drivers.ps1:283；scripts/remove_selected_iphone_drivers.ps1:2408 |
| Cleanup/CompletedWithErrors | 原始文字:  Cleanup finished with errors. Count: {0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:104；scripts/remove_selected_iphone_drivers.ps1:194；scripts/remove_selected_iphone_drivers.ps1:284；scripts/remove_selected_iphone_drivers.ps1:2434 |
| Cleanup/Confirm | 原始文字: 请输入 {0} 确认： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:81；scripts/remove_selected_iphone_drivers.ps1:171；scripts/remove_selected_iphone_drivers.ps1:261；scripts/remove_selected_iphone_drivers.ps1:2110 |
| Cleanup/ConfirmationMismatch | 原始文字: 确认文字不匹配，未做任何修改。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:82；scripts/remove_selected_iphone_drivers.ps1:172；scripts/remove_selected_iphone_drivers.ps1:262；scripts/remove_selected_iphone_drivers.ps1:2119 |
| Cleanup/DetectedDevices | 原始文字: 检测到以下物理 Apple 设备： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:67；scripts/remove_selected_iphone_drivers.ps1:157；scripts/remove_selected_iphone_drivers.ps1:247；scripts/remove_selected_iphone_drivers.ps1:1862 |
| Cleanup/DeviceNode | 原始文字: 设备节点：$InstanceId | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:42；scripts/remove_selected_iphone_drivers.ps1:132；scripts/remove_selected_iphone_drivers.ps1:222；scripts/remove_selected_iphone_drivers.ps1:1555 |
| Cleanup/DeviceNodeFailed | 原始文字: 设备节点删除失败：$InstanceId | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:45；scripts/remove_selected_iphone_drivers.ps1:135；scripts/remove_selected_iphone_drivers.ps1:225；scripts/remove_selected_iphone_drivers.ps1:1585 |
| Cleanup/DeviceNodeFailedDetail | 原始文字: 设备节点删除失败：$InstanceId  | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:44；scripts/remove_selected_iphone_drivers.ps1:134；scripts/remove_selected_iphone_drivers.ps1:224；scripts/remove_selected_iphone_drivers.ps1:1574 |
| Cleanup/DeviceNodeRestart | 原始文字: 设备节点已移除，重启后完成：$InstanceId | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:43；scripts/remove_selected_iphone_drivers.ps1:133；scripts/remove_selected_iphone_drivers.ps1:223；scripts/remove_selected_iphone_drivers.ps1:1567 |
| Cleanup/DriverEnumerationFailed | 原始文字: pnputil Driver Store 枚举失败，ExitCode=$($result.ExitCode)； | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:32；scripts/remove_selected_iphone_drivers.ps1:122；scripts/remove_selected_iphone_drivers.ps1:212；scripts/remove_selected_iphone_drivers.ps1:1265 |
| Cleanup/DriverIndexReady | 原始文字: Driver Store 索引完成：$($script:DriverInventory.Count) 个 OEM INF | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:39；scripts/remove_selected_iphone_drivers.ps1:129；scripts/remove_selected_iphone_drivers.ps1:219；scripts/remove_selected_iphone_drivers.ps1:1380 |
| Cleanup/DriverNodesMissing | 原始文字: Driver Store XML 中没有 Driver 节点，已停止清理以避免遗漏驱动包。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:38；scripts/remove_selected_iphone_drivers.ps1:128；scripts/remove_selected_iphone_drivers.ps1:218；scripts/remove_selected_iphone_drivers.ps1:1305 |
| Cleanup/DriverPackage | 原始文字: 驱动包：$InfName | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:46；scripts/remove_selected_iphone_drivers.ps1:136；scripts/remove_selected_iphone_drivers.ps1:226；scripts/remove_selected_iphone_drivers.ps1:1619 |
| Cleanup/DriverPackageFailed | 原始文字: 驱动包删除失败：$InfName | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:49；scripts/remove_selected_iphone_drivers.ps1:139；scripts/remove_selected_iphone_drivers.ps1:229；scripts/remove_selected_iphone_drivers.ps1:1649 |
| Cleanup/DriverPackageFailedDetail | 原始文字: 驱动包删除失败：$InfName  | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:48；scripts/remove_selected_iphone_drivers.ps1:138；scripts/remove_selected_iphone_drivers.ps1:228；scripts/remove_selected_iphone_drivers.ps1:1638 |
| Cleanup/DriverPackageRestart | 原始文字: 驱动包已删除，重启后完成：$InfName | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:47；scripts/remove_selected_iphone_drivers.ps1:137；scripts/remove_selected_iphone_drivers.ps1:227；scripts/remove_selected_iphone_drivers.ps1:1631 |
| Cleanup/DriverStoreCount | 原始文字:     Driver Store 驱动包：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:69；scripts/remove_selected_iphone_drivers.ps1:159；scripts/remove_selected_iphone_drivers.ps1:249；scripts/remove_selected_iphone_drivers.ps1:1884 |
| Cleanup/DriverXmlEmpty | 原始文字: Driver Store XML 为空。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:34；scripts/remove_selected_iphone_drivers.ps1:124；scripts/remove_selected_iphone_drivers.ps1:214；scripts/remove_selected_iphone_drivers.ps1:1275 |
| Cleanup/DriverXmlEmptyStopped | 原始文字: Driver Store XML 为空，已停止清理以避免遗漏驱动包。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:35；scripts/remove_selected_iphone_drivers.ps1:125；scripts/remove_selected_iphone_drivers.ps1:215；scripts/remove_selected_iphone_drivers.ps1:1277 |
| Cleanup/DriverXmlInvalid | 原始文字: Driver Store XML 解析失败： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:36；scripts/remove_selected_iphone_drivers.ps1:126；scripts/remove_selected_iphone_drivers.ps1:216；scripts/remove_selected_iphone_drivers.ps1:1288 |
| Cleanup/DriverXmlInvalidStopped | 原始文字: Driver Store XML 解析失败，已停止清理： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:37；scripts/remove_selected_iphone_drivers.ps1:127；scripts/remove_selected_iphone_drivers.ps1:217；scripts/remove_selected_iphone_drivers.ps1:1293 |
| Cleanup/DriverXmlUnavailable | 原始文字: 无法使用 Driver Store XML，ExitCode=$($result.ExitCode) | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:31；scripts/remove_selected_iphone_drivers.ps1:121；scripts/remove_selected_iphone_drivers.ps1:211；scripts/remove_selected_iphone_drivers.ps1:1261 |
| Cleanup/Error | 原始文字: 错误：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:98；scripts/remove_selected_iphone_drivers.ps1:188；scripts/remove_selected_iphone_drivers.ps1:278；scripts/remove_selected_iphone_drivers.ps1:2488 |
| Cleanup/ErrorLog | 原始文字: 错误日志：$errorLog | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:101；scripts/remove_selected_iphone_drivers.ps1:191；scripts/remove_selected_iphone_drivers.ps1:281；scripts/remove_selected_iphone_drivers.ps1:2564 |
| Cleanup/FatalError | 原始文字: iPhoneMirror Driver Cleanup Fatal Error | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:106；scripts/remove_selected_iphone_drivers.ps1:196；scripts/remove_selected_iphone_drivers.ps1:286；scripts/remove_selected_iphone_drivers.ps1:2548 |
| Cleanup/FinalDeviceCheck | 原始文字: 执行删除前最终设备确认... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:83；scripts/remove_selected_iphone_drivers.ps1:173；scripts/remove_selected_iphone_drivers.ps1:263；scripts/remove_selected_iphone_drivers.ps1:2128 |
| Cleanup/FinalVerification | 原始文字: 执行最终验证... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:88；scripts/remove_selected_iphone_drivers.ps1:178；scripts/remove_selected_iphone_drivers.ps1:268；scripts/remove_selected_iphone_drivers.ps1:2289 |
| Cleanup/IndexDriverStore | 原始文字: 建立 Driver Store 驱动索引... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:30；scripts/remove_selected_iphone_drivers.ps1:120；scripts/remove_selected_iphone_drivers.ps1:210；scripts/remove_selected_iphone_drivers.ps1:1248 |
| Cleanup/InternalError | 原始文字:  CLEANUP INTERNAL ERROR | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:105；scripts/remove_selected_iphone_drivers.ps1:195；scripts/remove_selected_iphone_drivers.ps1:285；scripts/remove_selected_iphone_drivers.ps1:2479 |
| Cleanup/InvalidDeviceNumber | 原始文字: 设备序号无效。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:72；scripts/remove_selected_iphone_drivers.ps1:162；scripts/remove_selected_iphone_drivers.ps1:252；scripts/remove_selected_iphone_drivers.ps1:1937 |
| Cleanup/Irreversible | 原始文字: 这是不可撤销的操作。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:80；scripts/remove_selected_iphone_drivers.ps1:170；scripts/remove_selected_iphone_drivers.ps1:260；scripts/remove_selected_iphone_drivers.ps1:2106 |
| Cleanup/ListOnly | 原始文字: 仅列表模式，未修改系统。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:70；scripts/remove_selected_iphone_drivers.ps1:160；scripts/remove_selected_iphone_drivers.ps1:250；scripts/remove_selected_iphone_drivers.ps1:1901 |
| Cleanup/Location | 原始文字: 位置： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:99；scripts/remove_selected_iphone_drivers.ps1:189；scripts/remove_selected_iphone_drivers.ps1:279；scripts/remove_selected_iphone_drivers.ps1:2500 |
| Cleanup/LogPath | 原始文字: 日志：$logPath | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:97；scripts/remove_selected_iphone_drivers.ps1:187；scripts/remove_selected_iphone_drivers.ps1:277；scripts/remove_selected_iphone_drivers.ps1:2424；scripts/remove_selected_iphone_drivers.ps1:2450 |
| Cleanup/ManifestWriteFailed | 原始文字: 无法写入 manifest： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:50；scripts/remove_selected_iphone_drivers.ps1:140；scripts/remove_selected_iphone_drivers.ps1:230；scripts/remove_selected_iphone_drivers.ps1:1717 |
| Cleanup/MappingDevices | 原始文字: 建立 Apple 设备关系... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:56；scripts/remove_selected_iphone_drivers.ps1:146；scripts/remove_selected_iphone_drivers.ps1:236；scripts/remove_selected_iphone_drivers.ps1:1785 |
| Cleanup/MappingDrivers | 原始文字: 建立驱动关系... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:65；scripts/remove_selected_iphone_drivers.ps1:155；scripts/remove_selected_iphone_drivers.ps1:245；scripts/remove_selected_iphone_drivers.ps1:1815 |
| Cleanup/MappingReady | 原始文字: 设备关系建立完成。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:66；scripts/remove_selected_iphone_drivers.ps1:156；scripts/remove_selected_iphone_drivers.ps1:246；scripts/remove_selected_iphone_drivers.ps1:1855 |
| Cleanup/NoAppleDevice | 原始文字: 没有找到当前连接的 iPhone/iPad。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:58；scripts/remove_selected_iphone_drivers.ps1:148；scripts/remove_selected_iphone_drivers.ps1:238；scripts/remove_selected_iphone_drivers.ps1:1797 |
| Cleanup/NoAvailablePnp | 原始文字: pnputil PnP XML 中没有可用设备。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:28；scripts/remove_selected_iphone_drivers.ps1:118；scripts/remove_selected_iphone_drivers.ps1:208；scripts/remove_selected_iphone_drivers.ps1:901 |
| Cleanup/NoCachedPnp | 原始文字: pnputil PnP 缓存完成：0 个设备 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:27；scripts/remove_selected_iphone_drivers.ps1:117；scripts/remove_selected_iphone_drivers.ps1:207；scripts/remove_selected_iphone_drivers.ps1:865 |
| Cleanup/NoConnectedPnp | 原始文字: pnputil PnP XML 中没有当前连接的设备。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:26；scripts/remove_selected_iphone_drivers.ps1:116；scripts/remove_selected_iphone_drivers.ps1:206；scripts/remove_selected_iphone_drivers.ps1:864 |
| Cleanup/NoRemovableNodes | 原始文字: 没有可删除的目标 PnP 节点。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:79；scripts/remove_selected_iphone_drivers.ps1:169；scripts/remove_selected_iphone_drivers.ps1:259；scripts/remove_selected_iphone_drivers.ps1:2086 |
| Cleanup/NodesRemoved | 原始文字: 目标 PnP 节点已清理。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:90；scripts/remove_selected_iphone_drivers.ps1:180；scripts/remove_selected_iphone_drivers.ps1:270；scripts/remove_selected_iphone_drivers.ps1:2351 |
| Cleanup/OnePhysicalDevice | 原始文字: 物理设备：1 台 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:75；scripts/remove_selected_iphone_drivers.ps1:165；scripts/remove_selected_iphone_drivers.ps1:255；scripts/remove_selected_iphone_drivers.ps1:2020 |
| Cleanup/PackagesHandled | 原始文字: 目标 Driver Store 驱动包已处理。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:93；scripts/remove_selected_iphone_drivers.ps1:183；scripts/remove_selected_iphone_drivers.ps1:273；scripts/remove_selected_iphone_drivers.ps1:2377 |
| Cleanup/Pause | 原始文字: 按 Enter 键关闭窗口 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:19；scripts/remove_selected_iphone_drivers.ps1:109；scripts/remove_selected_iphone_drivers.ps1:199；scripts/remove_selected_iphone_drivers.ps1:467 |
| Cleanup/PhysicalDeviceCount | 原始文字: Apple 物理设备分组完成：$($physicalDevices.Count) 台 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:57；scripts/remove_selected_iphone_drivers.ps1:147；scripts/remove_selected_iphone_drivers.ps1:237；scripts/remove_selected_iphone_drivers.ps1:1791 |
| Cleanup/PnpCached | 原始文字: PnP 缓存完成：$($script:DeviceCache.Count) 个设备 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:21；scripts/remove_selected_iphone_drivers.ps1:111；scripts/remove_selected_iphone_drivers.ps1:201；scripts/remove_selected_iphone_drivers.ps1:812 |
| Cleanup/PnpEnumerationFailed | 原始文字: 无法枚举 PnP 设备，pnputil ExitCode=$($result.ExitCode) | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:23；scripts/remove_selected_iphone_drivers.ps1:113；scripts/remove_selected_iphone_drivers.ps1:203；scripts/remove_selected_iphone_drivers.ps1:842 |
| Cleanup/PnpFallback | 原始文字: Get-PnpDevice 扫描失败，切换到 pnputil： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:22；scripts/remove_selected_iphone_drivers.ps1:112；scripts/remove_selected_iphone_drivers.ps1:202；scripts/remove_selected_iphone_drivers.ps1:820 |
| Cleanup/PnpNodeCount | 原始文字:     PnP 节点：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:68；scripts/remove_selected_iphone_drivers.ps1:158；scripts/remove_selected_iphone_drivers.ps1:248；scripts/remove_selected_iphone_drivers.ps1:1879 |
| Cleanup/PnpUtilCached | 原始文字: pnputil PnP 缓存完成：$($script:DeviceCache.Count) 个设备 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:29；scripts/remove_selected_iphone_drivers.ps1:119；scripts/remove_selected_iphone_drivers.ps1:209；scripts/remove_selected_iphone_drivers.ps1:905 |
| Cleanup/PnpUtilMissing | 原始文字: 找不到 pnputil.exe：$PnpUtil | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:52；scripts/remove_selected_iphone_drivers.ps1:142；scripts/remove_selected_iphone_drivers.ps1:232；scripts/remove_selected_iphone_drivers.ps1:1740 |
| Cleanup/PnpXmlEmpty | 原始文字: pnputil PnP XML 为空，无法枚举设备。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:24；scripts/remove_selected_iphone_drivers.ps1:114；scripts/remove_selected_iphone_drivers.ps1:204；scripts/remove_selected_iphone_drivers.ps1:847 |
| Cleanup/PnpXmlInvalid | 原始文字: pnputil PnP XML 解析失败： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:25；scripts/remove_selected_iphone_drivers.ps1:115；scripts/remove_selected_iphone_drivers.ps1:205；scripts/remove_selected_iphone_drivers.ps1:855 |
| Cleanup/PreviewOnly | 原始文字:  Preview only - 未修改系统 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:78；scripts/remove_selected_iphone_drivers.ps1:168；scripts/remove_selected_iphone_drivers.ps1:258；scripts/remove_selected_iphone_drivers.ps1:2071 |
| Cleanup/ProtectedLaunchRequired | 原始文字: 请通过 iPhoneMirror.Driver.exe 或发布包中的清理入口运行此工具，以获得受保护的管理员权限。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:51；scripts/remove_selected_iphone_drivers.ps1:141；scripts/remove_selected_iphone_drivers.ps1:231；scripts/remove_selected_iphone_drivers.ps1:1732 |
| Cleanup/ReinstallAdvice | 原始文字: 完成后可重新安装 Apple Devices 或 iTunes 以恢复所需驱动。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:54；scripts/remove_selected_iphone_drivers.ps1:144；scripts/remove_selected_iphone_drivers.ps1:234；scripts/remove_selected_iphone_drivers.ps1:1771 |
| Cleanup/RemainingNodes | 原始文字: 仍存在 $($remainingNodes.Count) 个目标节点。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:92；scripts/remove_selected_iphone_drivers.ps1:182；scripts/remove_selected_iphone_drivers.ps1:272；scripts/remove_selected_iphone_drivers.ps1:2364 |
| Cleanup/RemainingPackages | 原始文字: 仍存在 $($remainingDrivers.Count) 个驱动包。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:95；scripts/remove_selected_iphone_drivers.ps1:185；scripts/remove_selected_iphone_drivers.ps1:275；scripts/remove_selected_iphone_drivers.ps1:2390 |
| Cleanup/RemovingNodes | 原始文字:  卸载设备节点 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:85；scripts/remove_selected_iphone_drivers.ps1:175；scripts/remove_selected_iphone_drivers.ps1:265；scripts/remove_selected_iphone_drivers.ps1:2205 |
| Cleanup/RemovingPackages | 原始文字:  删除 Driver Store 驱动包 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:86；scripts/remove_selected_iphone_drivers.ps1:176；scripts/remove_selected_iphone_drivers.ps1:266；scripts/remove_selected_iphone_drivers.ps1:2229 |
| Cleanup/RequirementCable | 原始文字:   4. 数据线支持数据传输 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:63；scripts/remove_selected_iphone_drivers.ps1:153；scripts/remove_selected_iphone_drivers.ps1:243；scripts/remove_selected_iphone_drivers.ps1:1803 |
| Cleanup/RequirementTrusted | 原始文字:   3. 已点击“信任此电脑” | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:62；scripts/remove_selected_iphone_drivers.ps1:152；scripts/remove_selected_iphone_drivers.ps1:242；scripts/remove_selected_iphone_drivers.ps1:1802 |
| Cleanup/RequirementUnlocked | 原始文字:   2. 设备已解锁 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:61；scripts/remove_selected_iphone_drivers.ps1:151；scripts/remove_selected_iphone_drivers.ps1:241；scripts/remove_selected_iphone_drivers.ps1:1801 |
| Cleanup/RequirementUsb | 原始文字:   1. iPhone/iPad 已通过 USB 连接 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:60；scripts/remove_selected_iphone_drivers.ps1:150；scripts/remove_selected_iphone_drivers.ps1:240；scripts/remove_selected_iphone_drivers.ps1:1800 |
| Cleanup/RestartRequired | 原始文字: Windows 报告部分操作需要重启。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:96；scripts/remove_selected_iphone_drivers.ps1:186；scripts/remove_selected_iphone_drivers.ps1:276；scripts/remove_selected_iphone_drivers.ps1:2418 |
| Cleanup/ScanPnp | 原始文字: 扫描当前 PnP 设备... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:20；scripts/remove_selected_iphone_drivers.ps1:110；scripts/remove_selected_iphone_drivers.ps1:200；scripts/remove_selected_iphone_drivers.ps1:759 |
| Cleanup/ScanningDevices | 原始文字: 开始设备扫描... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:55；scripts/remove_selected_iphone_drivers.ps1:145；scripts/remove_selected_iphone_drivers.ps1:235；scripts/remove_selected_iphone_drivers.ps1:1781 |
| Cleanup/ScopeWarning | 原始文字: 将清除所选设备关联的所有设备节点和驱动包，包括 Apple 官方驱动。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:53；scripts/remove_selected_iphone_drivers.ps1:143；scripts/remove_selected_iphone_drivers.ps1:233；scripts/remove_selected_iphone_drivers.ps1:1769 |
| Cleanup/SelectDevice | 原始文字: 请输入设备序号；输入 Q 取消 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:71；scripts/remove_selected_iphone_drivers.ps1:161；scripts/remove_selected_iphone_drivers.ps1:251；scripts/remove_selected_iphone_drivers.ps1:1915 |
| Cleanup/SelectedDevice | 原始文字: 设备：{0} | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:74；scripts/remove_selected_iphone_drivers.ps1:164；scripts/remove_selected_iphone_drivers.ps1:254；scripts/remove_selected_iphone_drivers.ps1:2015 |
| Cleanup/SelectedDisconnected | 原始文字: 所选 iPhone 已断开。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:84；scripts/remove_selected_iphone_drivers.ps1:174；scripts/remove_selected_iphone_drivers.ps1:264；scripts/remove_selected_iphone_drivers.ps1:2134 |
| Cleanup/StackTrace | 原始文字: 调用栈： | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:100；scripts/remove_selected_iphone_drivers.ps1:190；scripts/remove_selected_iphone_drivers.ps1:280；scripts/remove_selected_iphone_drivers.ps1:2516 |
| Cleanup/StopMissingPackages | 原始文字: 已停止清理以避免遗漏驱动包。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:33；scripts/remove_selected_iphone_drivers.ps1:123；scripts/remove_selected_iphone_drivers.ps1:213；scripts/remove_selected_iphone_drivers.ps1:1266 |
| Cleanup/Title | 原始文字:  iPhone/iPad Remove All Drivers | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:102；scripts/remove_selected_iphone_drivers.ps1:192；scripts/remove_selected_iphone_drivers.ps1:282；scripts/remove_selected_iphone_drivers.ps1:1762 |
| Cleanup/UnresolvedNodes | 原始文字: 仍存在 $($unresolvedNodes.Count) 个目标 PnP 节点。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:91；scripts/remove_selected_iphone_drivers.ps1:181；scripts/remove_selected_iphone_drivers.ps1:271；scripts/remove_selected_iphone_drivers.ps1:2360 |
| Cleanup/UnresolvedPackages | 原始文字: 仍存在 $($unresolvedDrivers.Count) 个目标 Driver Store 驱动包。 | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:94；scripts/remove_selected_iphone_drivers.ps1:184；scripts/remove_selected_iphone_drivers.ps1:274；scripts/remove_selected_iphone_drivers.ps1:2386 |
| Cleanup/WaitingWindows | 原始文字: 等待 Windows 更新设备状态... | 驱动清理控制台硬编码提示补齐三语；操作和确认令牌不变 | scripts/remove_selected_iphone_drivers.ps1:87；scripts/remove_selected_iphone_drivers.ps1:177；scripts/remove_selected_iphone_drivers.ps1:267；scripts/remove_selected_iphone_drivers.ps1:2248 |
| Code/ControlStatusService.Cancelled | zh-CN: 已取消反向控制 | 默认取消说明原为硬编码中文；调用时读取现有 ControlStageCancelled 资源 | 见 Key 所指文件或资源字典 |
| Code/DriverCleanupHost.ExecutableMissing | en-US: The driver manager executable is missing. | 复用 DriverExecutableMissing；界面使用所选语言，字典加载前保留英文诊断回退 | 见 Key 所指文件或资源字典 |
| Code/MainViewModel.ShowControlOperationError | zh-HK: 藍牙／無線名称无法匹配，回落至 USB 状态 | 补齐香港繁体的蓝牙/无线传输名称匹配，避免错误归类为有线控制 | 见 Key 所指文件或资源字典 |
| Code/ReverseControlStatusWindow.ModeSummary | 所有语言: USB | 有线模式摘要复用 WiredControlLabel，与按钮和状态名称一致 | 见 Key 所指文件或资源字典 |
| DriverInstaller/AisiBody | en-US: Download Aisi Screen Mirroring, complete one normal wired mirroring session, then return and check again. | 补齐官方下载来源 | src/DriverInstaller/Windows/FailureHelpWindow.xaml:63 |
| DriverInstaller/AppleInstallerStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/DriverInstaller/Services/AppleSupportInstaller.cs:885 |
| DriverInstaller/ApplePackageManagerStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/DriverInstaller/Services/AppleSupportInstaller.cs:462 |
| DriverInstaller/AppleSupportDownloadingCompatibility | en-US: Downloading the driver component from Apple Software Update... | 统一进行中状态的省略号 | src/DriverInstaller/Services/AppleSupportInstaller.cs:172；src/DriverInstaller/Services/AppleSupportInstaller.cs:220 |
| DriverInstaller/AppleSupportExtractingCompatibility | en-US: Extracting the service component from the official Apple installer... | 统一进行中状态的省略号 | src/DriverInstaller/Services/AppleSupportInstaller.cs:246 |
| DriverInstaller/AppleSupportInstallTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/DriverInstaller/Services/AppleSupportInstaller.cs:914 |
| DriverInstaller/AppleSupportInstallingCompatibility | en-US: Installing the Apple Mobile Device Service compatibility component... | 统一进行中状态的省略号 | src/DriverInstaller/Services/AppleSupportInstaller.cs:187；src/DriverInstaller/Services/AppleSupportInstaller.cs:221；src/DriverInstaller/Services/AppleSupportInstaller.cs:766 |
| DriverInstaller/AppleSupportInstallingStore | en-US: Installing or repairing Apple USB support from Microsoft Store... | 统一进行中状态的省略号 | 见 Key 所指文件或资源字典 |
| DriverInstaller/AppleSupportPreparing | en-US: Preparing Apple USB support... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:210 |
| DriverInstaller/AppleSupportProcessTimeout | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/DriverInstaller/Services/AppleSupportInstaller.cs:477 |
| DriverInstaller/AppleSupportStartingService | en-US: Starting Apple Mobile Device Service... | 统一进行中状态的省略号 | src/DriverInstaller/Services/AppleSupportInstaller.cs:145 |
| DriverInstaller/AppleSupportVerifying | en-US: Verifying the Apple USB driver and service... | 统一进行中状态的省略号 | src/DriverInstaller/Services/AppleSupportInstaller.cs:271 |
| DriverInstaller/ConfirmOperationBody | en-US: The following device will be changed: {0}<br><br>{1}<br>{2}<br><br>Only this device's libusb0 filter is changed. Apple's official driver is not replaced. Windows may request administrator approval. | {0} 是操作名，不是设备信息 | src/DriverInstaller/MainWindow.xaml.cs:417 |
| DriverInstaller/DeviceReady | en-US: The mirroring driver for {0} is ready. | 补齐全部驱动就绪含义 | src/DriverInstaller/MainWindow.xaml.cs:338 |
| DriverInstaller/DevicesMissing | en-US: Detected {0} device(s); click the button to install missing drivers. | 用数量标签兼容单复数 | src/DriverInstaller/MainWindow.xaml.cs:598 |
| DriverInstaller/DevicesReady | en-US: Detected {0} device(s); all mirroring drivers are ready. | 用数量标签兼容单复数 | src/DriverInstaller/MainWindow.xaml.cs:597 |
| DriverInstaller/DriverCleanupHostStartFailed | 原为代码硬编码；对应源码变更见工作区 diff | 清理启动失败在已初始化的界面中使用三语提示；启动前的诊断回退保留 | src/DriverInstaller/Services/DriverCleanupHost.cs:54 |
| DriverInstaller/DriverCleanupProtectionFailed | 原为代码硬编码；对应源码变更见工作区 diff | 清理启动失败在已初始化的界面中使用三语提示；启动前的诊断回退保留 | src/DriverInstaller/Services/DriverCleanupHost.cs:32 |
| DriverInstaller/DriverFilterInstalledReconnect | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:53 |
| DriverInstaller/DriverFilterRemovedReconnect | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:52 |
| DriverInstaller/DriverOperationDetailsFormat | 原为代码硬编码；对应源码变更见工作区 diff | 驱动操作详情标题 | src/DriverInstaller/Services/DriverLocalization.cs:66 |
| DriverInstaller/DriverOperationRollbackIncomplete | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:57 |
| DriverInstaller/DriverOperationRolledBack | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:56 |
| DriverInstaller/DriverParentRemoved | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:51 |
| DriverInstaller/DriverParentRepairRejected | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:55 |
| DriverInstaller/DriverParentRepairStopped | 原为代码硬编码；对应源码变更见工作区 diff | 管理员进程返回的操作结果在界面层本地化，保留原始技术详情 | src/DriverInstaller/Services/DriverLocalization.cs:54 |
| DriverInstaller/DriverResultExitCodeMismatchFormat | 原为代码硬编码；对应源码变更见工作区 diff | 驱动验证失败提示补齐三语 | src/DriverInstaller/Services/DriverOperationClient.cs:177 |
| DriverInstaller/DriverResultTargetMismatch | 原为代码硬编码；对应源码变更见工作区 diff | 将用户可见硬编码错误或状态提示迁入三语资源 | src/DriverInstaller/Services/DriverOperationClient.cs:168 |
| DriverInstaller/GroupCopiedBody | zh-CN: QQ群号 1050045279 已复制。请在 QQ 中搜索并申请加入。 | 优化自然表达和可读性 | src/DriverInstaller/Windows/FailureHelpWindow.xaml.cs:26 |
| DriverInstaller/HistoricalDevice | en-US: Historical device | 优化自然表达和可读性 | src/DriverInstaller/Models/DriverModels.cs:27 |
| DriverInstaller/InstallingDriver | en-US: Installing the capture driver for {0}... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:345 |
| DriverInstaller/Operating | en-US: {0} {1}...；zh-CN: 正在{0} {1}…；zh-HK: 正在{0} {1}… | 统一进行中状态的省略号；参数为 Install/Repair/Uninstall 基本形式，避免组成 Install device 的假进行时 | src/DriverInstaller/MainWindow.xaml.cs:430 |
| DriverInstaller/RepairingDriver | en-US: Repairing the capture driver for {0}... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:345 |
| DriverInstaller/RepairingParent | en-US: Repairing the parent driver for {0}... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:315 |
| DriverInstaller/Retry | en-US: Check again | 相同重新检测动作与 Recheck 按钮一致 | src/DriverInstaller/Windows/FailureHelpWindow.xaml:90 |
| DriverInstaller/UnplugBody | zh-HK: 驅動程式更改已經完成。現在請拔除這部 iPhone 的傳輸線，等待裝置從清單中消失。 | 统一香港繁体中文既有用语 | src/DriverInstaller/MainWindow.xaml.cs:510 |
| DriverInstaller/UnplugTitle | zh-HK: 請拔除傳輸線 | 统一香港繁体中文既有用语 | src/DriverInstaller/MainWindow.xaml.cs:510 |
| DriverInstaller/Unplugged | zh-HK: 已拔除傳輸線 | 统一香港繁体中文既有用语 | src/DriverInstaller/MainWindow.xaml.cs:510 |
| DriverInstaller/WaitingDisconnect | en-US: Waiting for the device to disconnect... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:540 |
| DriverInstaller/WaitingForDevice | en-US: Waiting for an Apple device... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:385 |
| DriverInstaller/WaitingReconnect | en-US: Waiting for the device to reconnect... | 统一进行中状态的省略号 | src/DriverInstaller/MainWindow.xaml.cs:540 |
| Installer/Messages/CloseApplications | zh-HK: 關閉應用程式 (&amp;A) | 补齐自动关闭的行为 | 见 Key 所指文件或资源字典 |
| Installer/Messages/ConfirmDeleteSharedFile2 | zh-HK: 系統顯示下列共用檔案已不再被任何程式所使用，您要移除這些檔案嗎?%n%n%1%n%n倘若您移除了以上檔案但仍有程式需要使用它們，將造成這些程式無法正常執行，因此您若無法確定請選擇 [否]。保留這些檔案在您的系統中不會造成任何損害。；zh-CN: 系统表示下列共享的文件已不有其他程序使用。您希望卸载程序删除这些共享的文件吗？%n%n如果删除这些文件，但仍有程序在使用这些文件，则这些程序可能出现异常。如果您不能确定，请选择“否”，在系统中保留这些文件以免引发问题。 | 移除新版 Inno Setup 不再提供的 %1 参数；文件名已由独立标签显示；修复“已不有”错字并简化共享文件确认文字 | 见 Key 所指文件或资源字典 |
| Installer/Messages/ConfirmUninstall | zh-HK: 您確定要完全移除 %1 及其相關的檔案嗎？ | 卸载范围为所有组件，不只是相关文件 | 见 Key 所指文件或资源字典 |
| Installer/Messages/ErrorCloseApplications | zh-HK: 安裝程式無法自動關閉所有應用程式。建議您在繼續前先關閉所有應用程式使用的檔案。 | 应关闭占用文件的应用，而不是关闭应用使用的文件 | 见 Key 所指文件或资源字典 |
| Installer/Messages/ExistingFileNewerOverwriteOrKeepAll | zh-HK: 對下次衝突執行相同操作 (&amp;D) | 作用范围是所有冲突而非下次冲突 | 见 Key 所指文件或资源字典 |
| Installer/Messages/FileExistsKeepExisting | zh-HK: 保留現有檔案 (&amp;O) | 修复保留操作错误使用 Alt+O，应为 Alt+K | 见 Key 所指文件或资源字典 |
| Installer/Messages/FileExistsOverwriteExisting | zh-HK: 覆寫現有檔案 | 恢复覆盖操作缺失的 Alt+O 快捷键标记 | 见 Key 所指文件或资源字典 |
| Installer/Messages/FileExistsOverwriteOrKeepAll | zh-HK: 對下次衝突執行相同操作 (&amp;D) | 作用范围是所有冲突而非下次冲突 | 见 Key 所指文件或资源字典 |
| Installer/Messages/FinishedHeadingLabel | zh-HK: 安裝完成 | 补齐安装完成标题缺少的产品名占位符 | 见 Key 所指文件或资源字典 |
| Installer/Messages/InvalidPath | zh-CN: 您必须输入一个带驱动器卷标的完整路径，例如：%n%nC:\APP%n%n或UNC路径：%n%n\\server\share | 盘符不是卷标 | 见 Key 所指文件或资源字典 |
| Installer/Messages/ReadyMemoUserInfo | zh-HK: 使用者資訊 | 与项目香港繁体用语及标签标点一致 | 见 Key 所指文件或资源字典 |
| Installer/Messages/WizardUserInfo | zh-HK: 使用者資訊 | 与项目香港繁体用语一致 | 见 Key 所指文件或资源字典 |
| Launcher/ExecutableMissing | en-US: iPhoneMirror.Driver.exe was not found next to this launcher. | 独立 CMD 无法加载语言字典，故障时显示简体、繁体和英文回退；UTF-8/CRLF 防止乱码 | 见 Key 所指文件或资源字典 |
| Launcher/OperationIncomplete | en-US: Operation did not complete. Exit code: %EXIT_CODE% | 独立启动器异常结果补齐三语，保留退出代码变量 | 见 Key 所指文件或资源字典 |
| Launcher/Title | en-US: iPhone/iPad Remove All Drivers | 窗口标题保留产品名，避免暗示删除所有设备驱动；原设备范围不变 | 见 Key 所指文件或资源字典 |

## 静态未发现引用的资源

以下是保守的字面引用扫描结果，可能包含动态拼接、外部窗口或预留入口；没有据此删除资源。

- App：74 项。`AboutToolTip`, `ActualFrameRate`, `ActualResolution`, `ActualStream`, `AppSubtitle`, `ApplyReceiverName`, `BluetoothLandscapeMouseLeft90`, `BluetoothLandscapeMouseLeft90Reverse`, `BluetoothLandscapeMouseRight90`, `BluetoothLandscapeMouseRight90Reverse`, `CaptureDriverSafetyBlocked`, `CaptureErrorFormat`, `CaptureFailureDetailsFormat`, `CaptureFailureStatusFormat`, `CaptureNoPingRecovery`, `CaptureUsbConfigurationRecovery`, `ControlBindingNotFound`, `ControlWiredStartFailed`, `ControlWirelessStartFailed`, `DecoderPreferenceQueuedFormat`, `DecoderPreferenceSelectedFormat`, `DeveloperSurfaceOpenFailed`, `DeviceAlreadyMirroring`, `DriverManagerToolTip`, `IndependentWindowMute`, `MappingActionDoubleTap`, `MappingActionLongPress`, `MappingActionSwipe`, `MappingActionSwipeDown`, `MappingActionSwipeLeft`, `MappingActionSwipeRight`, `MappingActionSwipeUp`, `MappingActionTap`, `MappingDistance`, `MappingEndX`, `MappingEndY`, `MappingStateIdle`, `MappingStateKeyCaptured`, `MappingStateMappingReady`, `MappingStatePickingPosition`, `MappingStateWaitingForKey`, `MappingX`, `MappingY`, `MultiDeviceControlHint`, `NavCollapseDescription`, `NavExpandDescription`, `NavExpandToolTip`, `ObsTitleCopyFailedFormat`, `ObsWindowHelp`, `ObsWindowOpenFailedFormat`, `ObsWindowOpened`, `OpenObsWindow`, `PreviewLabel`, `ProjectionActions`, `ReverseControlDetails`, `ReverseControlStopFailureDetailFormat`, `ReverseControlUsbDisconnected`, `ShortcutSettings`, `ShortcutSettingsAllWindows`, `ShortcutSettingsFullScreen`, `ShortcutSettingsKeyboardShortcuts`, `ShortcutSettingsNextApp`, `ShortcutSettingsPreviousApp`, `ShortcutSettingsQuickNote`, `ShortcutSettingsReverseControl`, `ShortcutSettingsSearch`, `SwitchDeviceFailedFormat`, `SwitchingDeviceFormat`, `UsbModeDetailsToolTip`, `WindowTitle`, `WirelessProfileDisconnectConfirmFormat`, `WirelessProfileReconnectInstructionsFormat`, `WirelessResolutionTitle`, `WirelessRunningFormat`
- DriverInstaller：9 项。`AppleDownloadUnavailable`, `AppleSupportDownloadProgress`, `AppleSupportDownloadProgressUnknown`, `AppleSupportInstallTimeout`, `AppleSupportInstallingStore`, `ConnectIphone`, `DriverCleanupScriptMissing`, `ElevatedProcessTimeout`, `NoSelection`

## 完整性检查

- XML、Key、空值、占位符、文件过滤器、动态状态资源及脚本哈希：0 项错误。
- 安装器使用本地固定版本 Inno Setup 语言包叠加项目覆盖项核对；未修改上游语言包。
- 换行保留语义分段，不要求不同语言标点和句法逐字相同。
- 同时核对位置/命名占位符、printf 占位符以及安装器参数。

台湾新增语言的构建、运行时格式化、语言切换及 UI 布局验证见 [LOCALIZATION-TAIWAN.md](LOCALIZATION-TAIWAN.md)；早期三语审查见 [LOCALIZATION-VALIDATION.md](LOCALIZATION-VALIDATION.md)。

## 扫描文件清单

- `src/App/Animations/WindowDragBehavior.cs`
- `src/App/App.xaml`
- `src/App/App.xaml.cs`
- `src/App/Controls/AdaptiveToolbar.cs`
- `src/App/Controls/NativePreviewHost.cs`
- `src/App/Controls/PreviewAttachmentCoordinator.cs`
- `src/App/Controls/ThemedSymbolIcon.cs`
- `src/App/Controls/WorkspaceRevealPanel.cs`
- `src/App/Interop/NativeCore.cs`
- `src/App/Interop/NativeCursor.cs`
- `src/App/Localization/LocalizationService.cs`
- `src/App/Localization/LocalizedText.cs`
- `src/App/Localization/Strings.en-US.xaml`
- `src/App/Localization/Strings.zh-CN.xaml`
- `src/App/Localization/Strings.zh-HK.xaml`
- `src/App/Localization/Strings.zh-TW.xaml`
- `src/App/MainWindow.AdaptiveLayout.cs`
- `src/App/MainWindow.CompactLaunch.cs`
- `src/App/MainWindow.KeyboardFocus.cs`
- `src/App/MainWindow.KeyboardMapping.cs`
- `src/App/MainWindow.MappingOverlay.cs`
- `src/App/MainWindow.Shortcuts.cs`
- `src/App/MainWindow.Tray.cs`
- `src/App/MainWindow.WorkspaceTransitions.cs`
- `src/App/MainWindow.xaml`
- `src/App/MainWindow.xaml.cs`
- `src/App/Models/DeviceCaptureState.cs`
- `src/App/Models/DeviceCornerProfile.cs`
- `src/App/Models/DeviceViewModel.cs`
- `src/App/Services/AppIdentity.cs`
- `src/App/Services/AppleProductNames.cs`
- `src/App/Services/AppLog.cs`
- `src/App/Services/AspectRatioWindowController.cs`
- `src/App/Services/BluetoothClientInfo.cs`
- `src/App/Services/BluetoothClientRouteTable.cs`
- `src/App/Services/BluetoothControlNoticePolicy.cs`
- `src/App/Services/BluetoothHidMouseService.cs`
- `src/App/Services/BluetoothHidProtocol.cs`
- `src/App/Services/BluetoothMouseOrientationMapper.cs`
- `src/App/Services/BluetoothMouseReportCoalescer.cs`
- `src/App/Services/BonjourServiceStarter.cs`
- `src/App/Services/BossKeyWindowVisibility.cs`
- `src/App/Services/CaptureErrorGuidance.cs`
- `src/App/Services/CaptureShutdownCoordinator.cs`
- `src/App/Services/ClipboardSyncState.cs`
- `src/App/Services/CompactLaunchChannel.cs`
- `src/App/Services/CompactLaunchOptions.cs`
- `src/App/Services/ControlStatusService.cs`
- `src/App/Services/CoreDeviceTouchProtocol.cs`
- `src/App/Services/DeviceBindingManager.cs`
- `src/App/Services/DeviceControlSession.cs`
- `src/App/Services/DeviceCornerProfileResolver.cs`
- `src/App/Services/DeviceIdentityResolver.cs`
- `src/App/Services/DeviceSessionManager.cs`
- `src/App/Services/DiagnosticLogger.cs`
- `src/App/Services/DirectUsbInputBridge.cs`
- `src/App/Services/DriverManagerLauncher.cs`
- `src/App/Services/HlsMediaPlaybackBridge.cs`
- `src/App/Services/IndependentWindowAudioPolicy.cs`
- `src/App/Services/IPhoneFilterDriverService.cs`
- `src/App/Services/KeyboardMapping.cs`
- `src/App/Services/KeyboardMappingCapture.cs`
- `src/App/Services/KeyboardMappingExecutor.cs`
- `src/App/Services/KeyboardMappingFocusGuard.cs`
- `src/App/Services/KeyboardMappingKeys.cs`
- `src/App/Services/KeyboardMappingKeyState.cs`
- `src/App/Services/KeyboardMappingWindowsKey.cs`
- `src/App/Services/KeyboardShortcut.cs`
- `src/App/Services/MappingPreviewSurface.cs`
- `src/App/Services/MediaCastAudioDecoder.cs`
- `src/App/Services/MediaCastEventGate.cs`
- `src/App/Services/MediaCastPlaybackControls.cs`
- `src/App/Services/MediaCastReceiverController.cs`
- `src/App/Services/MediaOutputMicrophone.cs`
- `src/App/Services/MediaOutputService.cs`
- `src/App/Services/MediaRecoveryBackoff.cs`
- `src/App/Services/MediaSourceClassifier.cs`
- `src/App/Services/MultiDevicePreviewManager.cs`
- `src/App/Services/NativeLogTailReader.cs`
- `src/App/Services/PendingRecordingStore.cs`
- `src/App/Services/PreviewCoordinateMapper.cs`
- `src/App/Services/ProtectedContentStatus.cs`
- `src/App/Services/RawMouseDeltaTracker.cs`
- `src/App/Services/ReverseControlInputRouter.cs`
- `src/App/Services/RuntimeBinaryIntegrity.cs`
- `src/App/Services/ScreenshotService.cs`
- `src/App/Services/SingleFlightOperation.cs`
- `src/App/Services/SingleInstanceCoordinator.cs`
- `src/App/Services/StableDeviceSelection.cs`
- `src/App/Services/StartupDiagnostics.cs`
- `src/App/Services/ThemeService.cs`
- `src/App/Services/TrayIconService.cs`
- `src/App/Services/UsbDeviceRefreshPolicy.cs`
- `src/App/Services/UsbRestoreRecoveryTracker.cs`
- `src/App/Services/UsbTouchBridgeHost.cs`
- `src/App/Services/UxPlayComponent.cs`
- `src/App/Services/VirtualCameraService.cs`
- `src/App/Services/WifiSyncInsertionTracker.cs`
- `src/App/Services/WindowsAutoPlayGuard.cs`
- `src/App/Services/WirelessReceiverController.cs`
- `src/App/Services/WirelessReceiverService.cs`
- `src/App/Services/WirelessStallRecoveryTracker.cs`
- `src/App/tools/updater/Apply-ZipUpdate.ps1`
- `src/App/Updater/GitHubReleaseClient.cs`
- `src/App/Updater/LocalizedReleaseNotes.cs`
- `src/App/Updater/MarkdownFlowDocumentRenderer.cs`
- `src/App/Updater/ReleaseParser.cs`
- `src/App/Updater/SemanticVersion.cs`
- `src/App/Updater/UpdateInstallerLauncher.cs`
- `src/App/Updater/UpdateSettingsStore.cs`
- `src/App/Updater/VersionManager.cs`
- `src/App/ViewModels/MainViewModel.cs`
- `src/App/ViewModels/MainViewModel.KeyboardMapping.cs`
- `src/App/ViewModels/MainViewModel.MultiControl.cs`
- `src/App/ViewModels/RelayCommand.cs`
- `src/App/Windows/AboutWindow.xaml`
- `src/App/Windows/AboutWindow.xaml.cs`
- `src/App/Windows/AdvancedSettingsWindow.xaml`
- `src/App/Windows/AdvancedSettingsWindow.xaml.cs`
- `src/App/Windows/AirPlayDeviceSelectionWindow.xaml`
- `src/App/Windows/AirPlayDeviceSelectionWindow.xaml.cs`
- `src/App/Windows/AppPromptWindow.xaml`
- `src/App/Windows/AppPromptWindow.xaml.cs`
- `src/App/Windows/BluetoothClientBindingWindow.xaml`
- `src/App/Windows/BluetoothClientBindingWindow.xaml.cs`
- `src/App/Windows/BluetoothConnectionWindow.xaml`
- `src/App/Windows/BluetoothConnectionWindow.xaml.cs`
- `src/App/Windows/BluetoothControlNoticeWindow.xaml`
- `src/App/Windows/BluetoothControlNoticeWindow.xaml.cs`
- `src/App/Windows/CaptureRecoveryWindow.xaml`
- `src/App/Windows/CaptureRecoveryWindow.xaml.cs`
- `src/App/Windows/CaptureStatusNoticeWindow.xaml`
- `src/App/Windows/CaptureStatusNoticeWindow.xaml.cs`
- `src/App/Windows/ComponentDownloadWindow.xaml`
- `src/App/Windows/ComponentDownloadWindow.xaml.cs`
- `src/App/Windows/DeveloperToolsWindow.xaml`
- `src/App/Windows/DeveloperToolsWindow.xaml.cs`
- `src/App/Windows/DeviceBindingWindow.xaml`
- `src/App/Windows/DeviceBindingWindow.xaml.cs`
- `src/App/Windows/ImageSettingsWindow.xaml`
- `src/App/Windows/ImageSettingsWindow.xaml.cs`
- `src/App/Windows/InstanceConflictWindow.xaml`
- `src/App/Windows/InstanceConflictWindow.xaml.cs`
- `src/App/Windows/KeyboardMappingEditorWindow.xaml`
- `src/App/Windows/KeyboardMappingEditorWindow.xaml.cs`
- `src/App/Windows/KeyboardMappingOverlayWindow.cs`
- `src/App/Windows/KeyboardMappingWindow.xaml`
- `src/App/Windows/KeyboardMappingWindow.xaml.cs`
- `src/App/Windows/MediaOutputSettingsWindow.xaml`
- `src/App/Windows/MediaOutputSettingsWindow.xaml.cs`
- `src/App/Windows/NativePreviewWindow.cs`
- `src/App/Windows/ProjectionSettingsWindow.xaml`
- `src/App/Windows/ProjectionSettingsWindow.xaml.cs`
- `src/App/Windows/ProtectedContentNoticeWindow.xaml`
- `src/App/Windows/ProtectedContentNoticeWindow.xaml.cs`
- `src/App/Windows/ProtectedContentOverlayWindow.cs`
- `src/App/Windows/ReverseControlStatusWindow.xaml`
- `src/App/Windows/ReverseControlStatusWindow.xaml.cs`
- `src/App/Windows/ShortcutSettingsWindow.xaml`
- `src/App/Windows/ShortcutSettingsWindow.xaml.cs`
- `src/App/Windows/StartupErrorWindow.xaml`
- `src/App/Windows/StartupErrorWindow.xaml.cs`
- `src/App/Windows/TextInputWindow.xaml`
- `src/App/Windows/TextInputWindow.xaml.cs`
- `src/App/Windows/TrayPanelWindow.xaml`
- `src/App/Windows/TrayPanelWindow.xaml.cs`
- `src/App/Windows/UpdateWindow.xaml`
- `src/App/Windows/UpdateWindow.xaml.cs`
- `src/App/Windows/UsbProjectionModeInfoWindow.xaml`
- `src/App/Windows/UsbProjectionModeInfoWindow.xaml.cs`
- `src/App/Windows/WindowStyleSettingsWindow.xaml`
- `src/App/Windows/WindowStyleSettingsWindow.xaml.cs`
- `src/App.Logic.Tests/ClipboardSyncTests.cs`
- `src/App.Logic.Tests/ComponentDownloadNetworkTests.cs`
- `src/App.Logic.Tests/DotnetTestSmoke.cs`
- `src/App.Logic.Tests/IssueFixMediaTests.cs`
- `src/App.Logic.Tests/MediaOutputTestInterop.cs`
- `src/App.Logic.Tests/Program.cs`
- `src/App.Logic.Tests/UxPlayComponentTests.cs`
- `src/App.Runtime.Tests/AdaptiveLayoutAudit.cs`
- `src/App.Runtime.Tests/AdaptiveToolbarTests.cs`
- `src/App.Runtime.Tests/CaptureReviewRegressionTests.cs`
- `src/App.Runtime.Tests/ClipboardSyncRegressionTests.cs`
- `src/App.Runtime.Tests/ComponentDownloadTests.cs`
- `src/App.Runtime.Tests/ComponentRuntimeTests.cs`
- `src/App.Runtime.Tests/ControlBindingTests.cs`
- `src/App.Runtime.Tests/ControlStateAudit.cs`
- `src/App.Runtime.Tests/CursorRegressionTests.cs`
- `src/App.Runtime.Tests/DotnetTestSmoke.cs`
- `src/App.Runtime.Tests/DriverComboBoxTests.cs`
- `src/App.Runtime.Tests/DriverUiConsistencyAudit.cs`
- `src/App.Runtime.Tests/InteractionRegressionTests.cs`
- `src/App.Runtime.Tests/KeyboardFocusTests.cs`
- `src/App.Runtime.Tests/KeyboardMappingInteractionTests.cs`
- `src/App.Runtime.Tests/KeyboardMappingLiveTest.cs`
- `src/App.Runtime.Tests/KeyboardMappingTests.cs`
- `src/App.Runtime.Tests/KeyboardTransportTests.cs`
- `src/App.Runtime.Tests/LanguageDisplayAudit.cs`
- `src/App.Runtime.Tests/LocalizationAuditTests.cs`
- `src/App.Runtime.Tests/LogicReviewRegressionTests.cs`
- `src/App.Runtime.Tests/MainPreviewPointerTests.cs`
- `src/App.Runtime.Tests/MediaReviewRegressionTests.cs`
- `src/App.Runtime.Tests/MultiDeviceControlTests.cs`
- `src/App.Runtime.Tests/MultiDeviceInputIsolationTests.cs`
- `src/App.Runtime.Tests/PreviewContextMenuTests.cs`
- `src/App.Runtime.Tests/PreviewShellUiAudit.cs`
- `src/App.Runtime.Tests/PreviewStyleTests.cs`
- `src/App.Runtime.Tests/Program.cs`
- `src/App.Runtime.Tests/ReverseControlCountdownTests.cs`
- `src/App.Runtime.Tests/RoundedWindowAudit.cs`
- `src/App.Runtime.Tests/ShortcutRegressionTests.cs`
- `src/App.Runtime.Tests/TaiwanLocalizationTests.cs`
- `src/App.Runtime.Tests/ThemeContentAudit.cs`
- `src/App.Runtime.Tests/TrayModeTests.cs`
- `src/App.Runtime.Tests/TrayRegressionTests.cs`
- `src/App.Runtime.Tests/TrayStartupThemeTests.cs`
- `src/App.Runtime.Tests/UiAuditFixTests.cs`
- `src/App.Runtime.Tests/UiConsistencyAudit.cs`
- `src/App.Runtime.Tests/UiPerformanceAudit.cs`
- `src/App.Runtime.Tests/UpdaterElevationRegressionTests.cs`
- `src/App.Runtime.Tests/VirtualCameraRegressionTests.cs`
- `src/App.Runtime.Tests/WiredControlLiveCountdownTest.cs`
- `src/App.Runtime.Tests/WorkspacePerformanceAudit.cs`
- `src/App.Runtime.Tests/WorkspaceRegressionTests.cs`
- `src/App.Runtime.Tests/WorkspaceRevealTests.cs`
- `src/Core/include/iPhoneMirror/CoreApi.h`
- `src/Core/resources/iPhoneMirror.Core.rc`
- `src/Core/src/Audio/WasapiRenderer.cpp`
- `src/Core/src/Audio/WasapiRenderer.h`
- `src/Core/src/Capture/CaptureSession.cpp`
- `src/Core/src/Capture/CaptureSession.h`
- `src/Core/src/Capture/DecoderSwitchCoordinator.h`
- `src/Core/src/Capture/ICaptureSession.h`
- `src/Core/src/Capture/UsbConfigurationRestorePolicy.h`
- `src/Core/src/Capture/WirelessCaptureSession.cpp`
- `src/Core/src/Capture/WirelessCaptureSession.h`
- `src/Core/src/Capture/WirelessReceiverHub.cpp`
- `src/Core/src/Capture/WirelessReceiverHub.h`
- `src/Core/src/CoreApi.cpp`
- `src/Core/src/Device/AppleUsbDiscovery.cpp`
- `src/Core/src/Device/AppleUsbDiscovery.h`
- `src/Core/src/Device/DeviceManager.cpp`
- `src/Core/src/Device/DeviceManager.h`
- `src/Core/src/Logging.cpp`
- `src/Core/src/Logging.h`
- `src/Core/src/Media/CoreMedia.cpp`
- `src/Core/src/Media/CoreMedia.h`
- `src/Core/src/Media/H264.cpp`
- `src/Core/src/Media/H264.h`
- `src/Core/src/Media/MediaFoundationDecoder.cpp`
- `src/Core/src/Media/MediaFoundationDecoder.h`
- `src/Core/src/Protocol/Plist.cpp`
- `src/Core/src/Protocol/Plist.h`
- `src/Core/src/Protocol/QuickTimePacket.cpp`
- `src/Core/src/Protocol/QuickTimePacket.h`
- `src/Core/src/Protocol/QuickTimeSession.cpp`
- `src/Core/src/Protocol/QuickTimeSession.h`
- `src/Core/src/Renderer/D3D11PreviewRenderer.cpp`
- `src/Core/src/Renderer/D3D11PreviewRenderer.h`
- `src/Core/src/Renderer/OutputModeState.h`
- `src/Core/src/Transport/AppleUsbIdentityCache.h`
- `src/Core/src/Transport/LibUsb0Readiness.h`
- `src/Core/src/Transport/LibUsb0Transport.cpp`
- `src/Core/src/Transport/LibUsb0Transport.h`
- `src/Core/src/Transport/QtUsbTransport.cpp`
- `src/Core/src/Transport/QtUsbTransport.h`
- `src/Core/src/Transport/Socket.cpp`
- `src/Core/src/Transport/Socket.h`
- `src/Core/src/Transport/UsbInterfaceTransitionPolicy.h`
- `src/Core/src/Transport/UsbMuxClient.cpp`
- `src/Core/src/Transport/UsbMuxClient.h`
- `src/Core/tests/CoreTests.cpp`
- `src/Core/tests/OutputModeStateTests.cpp`
- `src/Core/tests/PreviewOpacitySmoke.cpp`
- `src/Core/tests/PreviewStaticFrameSmoke.cpp`
- `src/Core/tests/QtUsbTransportTests.cpp`
- `src/Core/tests/UsbConfigurationRestorePolicyTests.cpp`
- `src/Core/tools/LibUsb0Probe.cpp`
- `src/Core/tools/PipelineSwitchProbe.cpp`
- `src/Core/tools/UsbConfigurationSwitch.cpp`
- `src/DriverInstaller/App.xaml`
- `src/DriverInstaller/App.xaml.cs`
- `src/DriverInstaller/GlobalUsings.cs`
- `src/DriverInstaller/Localization/Strings.en-US.xaml`
- `src/DriverInstaller/Localization/Strings.zh-CN.xaml`
- `src/DriverInstaller/Localization/Strings.zh-HK.xaml`
- `src/DriverInstaller/Localization/Strings.zh-TW.xaml`
- `src/DriverInstaller/MainWindow.xaml`
- `src/DriverInstaller/MainWindow.xaml.cs`
- `src/DriverInstaller/Models/DriverModels.cs`
- `src/DriverInstaller/Properties/AssemblyInfo.cs`
- `src/DriverInstaller/Services/AppleDeviceMetadataReader.cs`
- `src/DriverInstaller/Services/AppleProductNames.cs`
- `src/DriverInstaller/Services/AppleSoftwareUpdateCatalog.cs`
- `src/DriverInstaller/Services/AppleSupportInstaller.cs`
- `src/DriverInstaller/Services/DeviceCatalog.cs`
- `src/DriverInstaller/Services/DriverCleanupHost.cs`
- `src/DriverInstaller/Services/DriverConstants.cs`
- `src/DriverInstaller/Services/DriverElevation.Bootstrap.ps1`
- `src/DriverInstaller/Services/DriverElevationBootstrap.cs`
- `src/DriverInstaller/Services/DriverLocalization.cs`
- `src/DriverInstaller/Services/DriverLogger.cs`
- `src/DriverInstaller/Services/DriverOperationClient.cs`
- `src/DriverInstaller/Services/DriverOperationSafety.cs`
- `src/DriverInstaller/Services/DriverPayload.cs`
- `src/DriverInstaller/Services/DriverThemeService.cs`
- `src/DriverInstaller/Services/ElevatedDriverHost.cs`
- `src/DriverInstaller/Services/ParentDriverChange.cs`
- `src/DriverInstaller/Services/ParentDriverNative.cs`
- `src/DriverInstaller/Services/ParentDriverPolicy.cs`
- `src/DriverInstaller/Windows/DeviceTrustWindow.xaml`
- `src/DriverInstaller/Windows/DeviceTrustWindow.xaml.cs`
- `src/DriverInstaller/Windows/FailureHelpWindow.xaml`
- `src/DriverInstaller/Windows/FailureHelpWindow.xaml.cs`
- `src/DriverInstaller/Windows/ParentDriverWindow.xaml`
- `src/DriverInstaller/Windows/ParentDriverWindow.xaml.cs`
- `src/DriverInstaller/Windows/PromptWindow.xaml`
- `src/DriverInstaller/Windows/PromptWindow.xaml.cs`
- `src/DriverInstaller/Windows/RequiredActionWindow.xaml`
- `src/DriverInstaller/Windows/RequiredActionWindow.xaml.cs`
- `src/DriverInstaller.Tests/DriverFailureTests.cs`
- `src/DriverInstaller.Tests/ParentDriverTests.cs`
- `src/DriverInstaller.Tests/Program.cs`
- `src/Shared/Networking/SegmentedHttpDownloader.cs`
- `src/Shared/Security/ElevationPathLock.cs`
- `src/SharedUI/Animations/ModernAnimations.xaml`
- `src/SharedUI/Animations/PageTransition.cs`
- `src/SharedUI/Animations/RevealTransition.cs`
- `src/SharedUI/Controls/Buttons.xaml`
- `src/SharedUI/Controls/ComboBoxTextLayout.cs`
- `src/SharedUI/Controls/DialogKeyboard.cs`
- `src/SharedUI/Controls/DialogViewport.cs`
- `src/SharedUI/Controls/Inputs.xaml`
- `src/SharedUI/Controls/ModernButton.cs`
- `src/SharedUI/Controls/ModernCard.cs`
- `src/SharedUI/Controls/ModernControls.xaml`
- `src/SharedUI/Controls/ModernDialog.cs`
- `src/SharedUI/Controls/RoundedWindow.cs`
- `src/SharedUI/Controls/StatusAppearance.cs`
- `src/SharedUI/Services/AccessibilityAppearance.cs`
- `src/SharedUI/Services/WindowWorkAreaController.cs`
- `src/SharedUI/Themes/DarkTheme.xaml`
- `src/SharedUI/Themes/DesignTokens.xaml`
- `src/SharedUI/Themes/LightTheme.xaml`
- `src/UxPlayHost/tests/UxPlayHostFixture.cpp`
- `src/UxPlayHost/tests/UxPlayHostSmoke.cpp`
- `src/UxPlayHost/UxPlayHost.cpp`
- `src/VirtualCamera/include/iPhoneMirror/VirtualCameraApi.h`
- `src/VirtualCamera/src/ComServer.cpp`
- `src/VirtualCamera/src/FrameExchange.cpp`
- `src/VirtualCamera/src/FrameExchange.h`
- `src/VirtualCamera/src/MediaSource.cpp`
- `src/VirtualCamera/src/MediaSource.h`
- `src/VirtualCamera/src/MediaSourceActivate.cpp`
- `src/VirtualCamera/src/MediaSourceActivate.h`
- `src/VirtualCamera/src/ModuleState.h`
- `src/VirtualCamera/src/VirtualCameraControl.cpp`
- `src/VirtualCamera/src/VirtualCameraShared.h`
- `src/VirtualCamera/tests/MediaSourceActivateTests.cpp`
- `src/VirtualCamera/tests/VirtualCameraComponentTests.cpp`
- `src/VirtualCamera/tools/VirtualCameraAdmin.cpp`
- `src/VirtualCamera/tools/VirtualCameraSignalPublisher.cpp`
- `src/VirtualCamera/tools/VirtualCameraSystemProbe.cpp`
- `src/WirelessHost/DlnaRenderer.cpp`
- `src/WirelessHost/DlnaRenderer.h`
- `src/WirelessHost/DnsSdAdvertisementPolicy.h`
- `src/WirelessHost/DnsSdRegistrationPolicy.h`
- `src/WirelessHost/DnsSdShim.cpp`
- `src/WirelessHost/HostCommon.h`
- `src/WirelessHost/HttpUrl.h`
- `src/WirelessHost/IpcProtocol.h`
- `src/WirelessHost/tests/DnsSdRegistrationPolicyTests.cpp`
- `src/WirelessHost/tests/IpcWriterTests.cpp`
- `src/WirelessHost/tests/WirelessHostPreflightSmoke.cpp`
- `src/WirelessHost/tests/WirelessHostSmoke.cpp`
- `src/WirelessHost/tests/WirelessHostStub.cpp`
- `src/WirelessHost/tests/WirelessReceiverProtocolSmoke.cpp`
- `src/WirelessHost/WirelessHost.cpp`
- `scripts/airplay_media_control_smoke.ps1`
- `scripts/AppleSupportPackage.ps1`
- `scripts/aspect_ratio_smoke.ps1`
- `scripts/audit_component_payloads.py`
- `scripts/audit_localization.py`
- `scripts/audit_taiwan_content.py`
- `scripts/audit_ui.py`
- `scripts/build_airplay_ffmpeg.ps1`
- `scripts/build_airplay_receiver.ps1`
- `scripts/build_compact_ffmpeg.ps1`
- `scripts/build_icon.py`
- `scripts/build_installer.ps1`
- `scripts/CompactBuildRecord.ps1`
- `scripts/diagnose.py`
- `scripts/generate_apple_mobile_capture_pids.ps1`
- `scripts/gui_smoke.ps1`
- `scripts/hls_recovery_server.py`
- `scripts/localization_gui_smoke.ps1`
- `scripts/media_cast_integrated_smoke.ps1`
- `scripts/multi_device_capture_switch_smoke.ps1`
- `scripts/multi_device_selection_smoke.ps1`
- `scripts/package_release.ps1`
- `scripts/package_uxplay_component.ps1`
- `scripts/prepare_compact_ffmpeg.ps1`
- `scripts/prepare_ffmpeg.ps1`
- `scripts/prepare_inno_setup.ps1`
- `scripts/prepare_libusb0_runtime.ps1`
- `scripts/prepare_uxplay.ps1`
- `scripts/prepare_vc_runtime.ps1`
- `scripts/remove_selected_iphone_drivers.ps1`
- `scripts/repair_apple_wifi_usbmux.ps1`
- `scripts/reset_driver_test_environment.ps1`
- `scripts/single_device_capture_restart_smoke.ps1`
- `scripts/Test-WiredControlUia.ps1`
- `scripts/Test-WiredRecoveryDevice.ps1`
- `scripts/test_airplay_ffmpeg.ps1`
- `scripts/test_apple_support_package.ps1`
- `scripts/test_cleanup_localization.ps1`
- `scripts/test_compact_ffmpeg.ps1`
- `scripts/test_driver_cleanup_safety.ps1`
- `scripts/test_installer.ps1`
- `scripts/test_usb_bridge_build_source.ps1`
- `scripts/test_uxplay_media.ps1`
- `scripts/test_vc_runtime_version.ps1`
- `scripts/ui_visual_smoke.ps1`
- `scripts/usb-bridge-recipe/build.ps1`
- `scripts/UsbBridgeBuildSource.ps1`
- `scripts/UsbTouchBridgeRuntime.ps1`
- `scripts/VcRuntimeVersion.ps1`
- `scripts/verify_localization.ps1`
- `scripts/verify_uxplay_publication.ps1`
- `scripts/window_chrome_smoke.ps1`
- `scripts/window_close_cleanup_smoke.ps1`
- `scripts/wireless_receiver_smoke.ps1`
- `tools/BluetoothHidStress/BluetoothClientInfoStub.cs`
- `tools/BluetoothHidStress/Program.cs`
- `tools/bridge_runtime_check.py`
- `tools/capture_ipc_apis.py`
- `tools/capture_original_frame_rpc.py`
- `tools/capture_process_writes.py`
- `tools/capture_socket_io.py`
- `tools/capture_winsock_frames.py`
- `tools/coredevice_touch_bridge.py`
- `tools/dtx_devicehub_probe.py`
- `tools/enable_wifi_sync.py`
- `tools/inspect_hid_session.py`
- `tools/iostouch/__init__.py`
- `tools/iostouch/qt/__init__.py`
- `tools/iostouch/qt/muxbridge.py`
- `tools/iostouch/qt/usb.py`
- `tools/iostouch/qt/usbmux_usb.py`
- `tools/iostouch/qt/usbmuxd_server.py`
- `tools/probe_dtservicehub.py`
- `tools/probe_dtservicehub_rsd.py`
- `tools/probe_dtx_channel.py`
- `tools/probe_dtx_screenshot.py`
- `tools/probe_rsd_services.py`
- `tools/probe_tcp_coredevice.py`
- `tools/probe_universalhid.py`
- `tools/probe_universalhid_send.py`
- `tools/srs-lab/app.js`
- `tools/srs-lab/Enable-DockerPrerequisites.ps1`
- `tools/srs-lab/index.html`
- `tools/srs-lab/localize.js`
- `tools/srs-lab/Publish-TestSignal.ps1`
- `tools/srs-lab/Start-SrsLab.ps1`
- `tools/srs-test/Start-SrsTest.ps1`
- `tools/srs-test/Stop-SrsTest.ps1`
- `tools/srs-test/web/index.html`
- `tools/srs-test/web/localize.js`
- `tools/usb_mouse_demo.py`
- `tools/usb_touch_bridge.py`
- `tools/usb_touch_console.py`
- `tools/UsbTouchDemo/Program.cs`
- `installer/DesktopShell.iss`
- `installer/iPhoneMirror.iss`
- `Launch-Recovery-Build.cmd`
- `Remove-Selected-iPhone-Drivers.cmd`
