[CmdletBinding()]
param(
    [ValidateSet('system', 'zh-CN', 'zh-HK', 'zh-TW', 'en-US')]
    [string]$Language = 'system',
    [switch]$ListOnly,
    [switch]$PreviewOnly,
    [switch]$NoPause,
    [int]$ExcludeProcessId = 0,
    [int]$ExcludeParentProcessId = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# User-visible console messages. Keep protocol fields and confirmation tokens unchanged.
$script:CleanupMessages = @'
{
  "zh-CN": {
    "Pause": "按 Enter 键关闭窗口",
    "ScanPnp": "正在扫描当前 PnP 设备…",
    "PnpCached": "PnP 缓存完成：{0} 个设备",
    "PnpFallback": "Get-PnpDevice 扫描失败，正在改用 pnputil：",
    "PnpEnumerationFailed": "无法枚举 PnP 设备，pnputil 退出代码为 {0}",
    "PnpXmlEmpty": "pnputil PnP XML 为空，无法枚举设备。",
    "PnpXmlInvalid": "pnputil PnP XML 解析失败：",
    "NoConnectedPnp": "pnputil PnP XML 中没有当前连接的设备。",
    "NoCachedPnp": "pnputil PnP 缓存完成：0 个设备",
    "NoAvailablePnp": "pnputil PnP XML 中没有可用设备。",
    "PnpUtilCached": "pnputil PnP 缓存完成：{0} 个设备",
    "IndexDriverStore": "正在建立 Driver Store 驱动索引…",
    "DriverXmlUnavailable": "无法使用 Driver Store XML，退出代码为 {0}",
    "DriverEnumerationFailed": "pnputil Driver Store 枚举失败，退出代码为 {0}；",
    "StopMissingPackages": "已停止清理，以免遗漏驱动包。",
    "DriverXmlEmpty": "Driver Store XML 为空。",
    "DriverXmlEmptyStopped": "Driver Store XML 为空，已停止清理，以免遗漏驱动包。",
    "DriverXmlInvalid": "Driver Store XML 解析失败：",
    "DriverXmlInvalidStopped": "Driver Store XML 解析失败，已停止清理：",
    "DriverNodesMissing": "Driver Store XML 中没有 Driver 节点，已停止清理，以免遗漏驱动包。",
    "DriverIndexReady": "Driver Store 索引完成：{0} 个 OEM INF",
    "ClosingProcesses": "正在关闭 iPhoneMirror 相关进程…",
    "BluetoothExcluded": "已安全排除 BTHLE 设备：{0}",
    "DeviceNode": "设备节点：{0}",
    "DeviceNodeRestart": "设备节点已移除，重启后完成：{0}",
    "DeviceNodeFailedDetail": "设备节点删除失败：{0} ",
    "DeviceNodeFailed": "设备节点删除失败：{0}",
    "DriverPackage": "驱动包：{0}",
    "DriverPackageRestart": "驱动包已删除，重启后完成：{0}",
    "DriverPackageFailedDetail": "驱动包删除失败：{0} ",
    "DriverPackageFailed": "驱动包删除失败：{0}",
    "ManifestWriteFailed": "无法写入清单：",
    "ProtectedLaunchRequired": "请通过 iPhoneMirror.Driver.exe 或发布包中的清理入口运行此工具，以获得受保护的管理员权限。",
    "PnpUtilMissing": "找不到 pnputil.exe：{0}",
    "ScopeWarning": "将清理所选设备的关联设备节点及可安全移除的驱动包，可能包括 Apple 官方驱动；其他设备仍在使用的驱动包会保留。",
    "ReinstallAdvice": "完成后可重新安装 Apple Devices 或 iTunes，以恢复所需驱动。",
    "ScanningDevices": "正在扫描设备…",
    "MappingDevices": "正在建立 Apple 设备关系…",
    "PhysicalDeviceCount": "Apple 物理设备分组完成：{0} 台",
    "NoAppleDevice": "未找到当前连接的 iPhone/iPad。",
    "CheckRequirements": "请确认：",
    "RequirementUsb": "  1. iPhone/iPad 已通过 USB 连接",
    "RequirementUnlocked": "  2. 设备已解锁",
    "RequirementTrusted": "  3. 已在设备上选择“信任此电脑”",
    "RequirementCable": "  4. 数据线支持数据传输",
    "BluetoothNotListed": "BTHLE / Bluetooth LE 设备不会显示。",
    "MappingDrivers": "正在建立驱动关系…",
    "MappingReady": "设备关系建立完成。",
    "DetectedDevices": "检测到以下 Apple 物理设备：",
    "PnpNodeCount": "    PnP 节点：{0}",
    "DriverStoreCount": "    Driver Store 驱动包：{0}",
    "ListOnly": "仅列表模式，未修改系统。",
    "SelectDevice": "请输入设备序号；输入 Q 取消",
    "InvalidDeviceNumber": "设备序号无效。",
    "CleanupPlan": " 清理计划",
    "SelectedDevice": "设备：{0}",
    "OnePhysicalDevice": "物理设备：1 台",
    "AssociatedNodes": "关联 PnP 节点：{0}",
    "AssociatedDrivers": "关联驱动包总数：{0}",
    "PreviewOnly": " 仅预览，未修改系统",
    "NoRemovableNodes": "没有可删除的目标 PnP 节点。",
    "Irreversible": "此操作不可撤销。",
    "Confirm": "请输入 {0} 确认：",
    "ConfirmationMismatch": "确认文字不匹配，未做任何修改。",
    "FinalDeviceCheck": "正在执行删除前的最终设备确认…",
    "SelectedDisconnected": "所选 iPhone 已断开。",
    "RemovingNodes": " 正在卸载设备节点",
    "RemovingPackages": " 正在删除 Driver Store 驱动包",
    "WaitingWindows": "正在等待 Windows 更新设备状态…",
    "FinalVerification": "正在执行最终验证…",
    "CleanupResults": " 清理结果",
    "NodesRemoved": "目标 PnP 节点已清理。",
    "UnresolvedNodes": "仍存在 {0} 个目标 PnP 节点。",
    "RemainingNodes": "仍存在 {0} 个目标节点。",
    "PackagesHandled": "目标 Driver Store 驱动包已处理。",
    "UnresolvedPackages": "仍存在 {0} 个目标 Driver Store 驱动包。",
    "RemainingPackages": "仍存在 {0} 个驱动包。",
    "RestartRequired": "Windows 报告部分操作需要重启。",
    "LogPath": "日志：{0}",
    "Error": "错误：{0}",
    "Location": "位置：",
    "StackTrace": "调用栈：",
    "ErrorLog": "错误日志：{0}",
    "Title": " 清除 iPhone/iPad 关联驱动",
    "Completed": " 清理完成。",
    "CompletedWithErrors": " 清理结束，发生 {0} 个错误。",
    "InternalError": " 清理工具内部错误",
    "FatalError": "iPhoneMirror 驱动清理严重错误",
    "SharedPackagePreserved": "驱动包 {0} 正被其他设备使用，已保留。",
    "PackagesPreservedUnsupported": "此 Windows 版本无法完整列出驱动包使用者；将只清理所选设备节点，保留驱动包。",
    "DriverOperationBusy": "另一个驱动操作或回滚仍在进行，请等待完成后重试清理。"
  },
  "zh-HK": {
    "Pause": "按 Enter 鍵關閉視窗",
    "ScanPnp": "正在掃描目前 PnP 裝置…",
    "PnpCached": "PnP 快取完成：{0} 個裝置",
    "PnpFallback": "Get-PnpDevice 掃描失敗，正在改用 pnputil：",
    "PnpEnumerationFailed": "無法列舉 PnP 裝置，pnputil 結束代碼為 {0}",
    "PnpXmlEmpty": "pnputil PnP XML 為空，無法列舉裝置。",
    "PnpXmlInvalid": "pnputil PnP XML 解析失敗：",
    "NoConnectedPnp": "pnputil PnP XML 中沒有目前連接的裝置。",
    "NoCachedPnp": "pnputil PnP 快取完成：0 個裝置",
    "NoAvailablePnp": "pnputil PnP XML 中沒有可用裝置。",
    "PnpUtilCached": "pnputil PnP 快取完成：{0} 個裝置",
    "IndexDriverStore": "正在建立 Driver Store 驅動程式索引…",
    "DriverXmlUnavailable": "無法使用 Driver Store XML，結束代碼為 {0}",
    "DriverEnumerationFailed": "pnputil Driver Store 列舉失敗，結束代碼為 {0}；",
    "StopMissingPackages": "已停止清理，以免遺漏驅動程式套件。",
    "DriverXmlEmpty": "Driver Store XML 為空。",
    "DriverXmlEmptyStopped": "Driver Store XML 為空，已停止清理，以免遺漏驅動程式套件。",
    "DriverXmlInvalid": "Driver Store XML 解析失敗：",
    "DriverXmlInvalidStopped": "Driver Store XML 解析失敗，已停止清理：",
    "DriverNodesMissing": "Driver Store XML 中沒有 Driver 節點，已停止清理，以免遺漏驅動程式套件。",
    "DriverIndexReady": "Driver Store 索引完成：{0} 個 OEM INF",
    "ClosingProcesses": "正在關閉 iPhoneMirror 相關程序…",
    "BluetoothExcluded": "已安全排除 BTHLE 裝置：{0}",
    "DeviceNode": "裝置節點：{0}",
    "DeviceNodeRestart": "裝置節點已移除，重新啟動後完成：{0}",
    "DeviceNodeFailedDetail": "裝置節點刪除失敗：{0} ",
    "DeviceNodeFailed": "裝置節點刪除失敗：{0}",
    "DriverPackage": "驅動程式套件：{0}",
    "DriverPackageRestart": "驅動程式套件已刪除，重新啟動後完成：{0}",
    "DriverPackageFailedDetail": "驅動程式套件刪除失敗：{0} ",
    "DriverPackageFailed": "驅動程式套件刪除失敗：{0}",
    "ManifestWriteFailed": "無法寫入清單：",
    "ProtectedLaunchRequired": "請透過 iPhoneMirror.Driver.exe 或發佈套件中的清理入口執行此工具，以取得受保護的管理員權限。",
    "PnpUtilMissing": "找不到 pnputil.exe：{0}",
    "ScopeWarning": "將清理所選裝置的關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式；其他裝置仍在使用的驅動程式套件會保留。",
    "ReinstallAdvice": "完成後可重新安裝 Apple Devices 或 iTunes，以還原所需驅動程式。",
    "ScanningDevices": "正在掃描裝置…",
    "MappingDevices": "正在建立 Apple 裝置關係…",
    "PhysicalDeviceCount": "Apple 實體裝置分組完成：{0} 部",
    "NoAppleDevice": "找不到目前連接的 iPhone/iPad。",
    "CheckRequirements": "請確認：",
    "RequirementUsb": "  1. iPhone/iPad 已透過 USB 連接",
    "RequirementUnlocked": "  2. 裝置已解鎖",
    "RequirementTrusted": "  3. 已在裝置上選擇「信任此電腦」",
    "RequirementCable": "  4. 數據線支援資料傳輸",
    "BluetoothNotListed": "BTHLE / Bluetooth LE 裝置不會顯示。",
    "MappingDrivers": "正在建立驅動程式關係…",
    "MappingReady": "裝置關係建立完成。",
    "DetectedDevices": "偵測到以下 Apple 實體裝置：",
    "PnpNodeCount": "    PnP 節點：{0}",
    "DriverStoreCount": "    Driver Store 驅動程式套件：{0}",
    "ListOnly": "僅清單模式，未修改系統。",
    "SelectDevice": "請輸入裝置編號；輸入 Q 取消",
    "InvalidDeviceNumber": "裝置編號無效。",
    "CleanupPlan": " 清理計劃",
    "SelectedDevice": "裝置：{0}",
    "OnePhysicalDevice": "實體裝置：1 部",
    "AssociatedNodes": "關聯 PnP 節點：{0}",
    "AssociatedDrivers": "關聯驅動程式套件總數：{0}",
    "PreviewOnly": " 僅預覽，未修改系統",
    "NoRemovableNodes": "沒有可刪除的目標 PnP 節點。",
    "Irreversible": "此操作無法復原。",
    "Confirm": "請輸入 {0} 確認：",
    "ConfirmationMismatch": "確認文字不符，未作任何修改。",
    "FinalDeviceCheck": "正在執行刪除前的最終裝置確認…",
    "SelectedDisconnected": "所選 iPhone 已中斷連線。",
    "RemovingNodes": " 正在卸載裝置節點",
    "RemovingPackages": " 正在刪除 Driver Store 驅動程式套件",
    "WaitingWindows": "正在等待 Windows 更新裝置狀態…",
    "FinalVerification": "正在執行最終驗證…",
    "CleanupResults": " 清理結果",
    "NodesRemoved": "目標 PnP 節點已清理。",
    "UnresolvedNodes": "仍存在 {0} 個目標 PnP 節點。",
    "RemainingNodes": "仍存在 {0} 個目標節點。",
    "PackagesHandled": "目標 Driver Store 驅動程式套件已處理。",
    "UnresolvedPackages": "仍存在 {0} 個目標 Driver Store 驅動程式套件。",
    "RemainingPackages": "仍存在 {0} 個驅動程式套件。",
    "RestartRequired": "Windows 報告部分操作需要重新啟動。",
    "LogPath": "記錄：{0}",
    "Error": "錯誤：{0}",
    "Location": "位置：",
    "StackTrace": "呼叫堆疊：",
    "ErrorLog": "錯誤記錄：{0}",
    "Title": " 清除 iPhone/iPad 關聯驅動程式",
    "Completed": " 清理完成。",
    "CompletedWithErrors": " 清理結束，發生 {0} 個錯誤。",
    "InternalError": " 清理工具內部錯誤",
    "FatalError": "iPhoneMirror 驅動程式清理嚴重錯誤",
    "SharedPackagePreserved": "驅動程式套件 {0} 正被其他裝置使用，已保留。",
    "PackagesPreservedUnsupported": "此 Windows 版本無法完整列出驅動程式套件使用者；只會清理所選裝置節點，保留驅動程式套件。",
    "DriverOperationBusy": "另一個驅動操作或復原仍在進行，請等待完成後重試清理。"
  },
  "en-US": {
    "Pause": "Press Enter to close this window",
    "ScanPnp": "Scanning current PnP devices…",
    "PnpCached": "PnP cache ready. Devices: {0}",
    "PnpFallback": "Get-PnpDevice scan failed; switching to pnputil: ",
    "PnpEnumerationFailed": "Could not enumerate PnP devices. pnputil exit code: {0}",
    "PnpXmlEmpty": "pnputil PnP XML is empty; devices cannot be enumerated.",
    "PnpXmlInvalid": "Could not parse pnputil PnP XML: ",
    "NoConnectedPnp": "No connected devices were found in pnputil PnP XML.",
    "NoCachedPnp": "pnputil PnP cache ready. Devices: 0",
    "NoAvailablePnp": "No available devices were found in pnputil PnP XML.",
    "PnpUtilCached": "pnputil PnP cache ready. Devices: {0}",
    "IndexDriverStore": "Indexing Driver Store packages…",
    "DriverXmlUnavailable": "Driver Store XML is unavailable. Exit code: {0}",
    "DriverEnumerationFailed": "pnputil Driver Store enumeration failed (exit code {0}). ",
    "StopMissingPackages": "Cleanup stopped to avoid missing driver packages.",
    "DriverXmlEmpty": "Driver Store XML is empty.",
    "DriverXmlEmptyStopped": "Driver Store XML is empty. Cleanup stopped to avoid missing driver packages.",
    "DriverXmlInvalid": "Could not parse Driver Store XML: ",
    "DriverXmlInvalidStopped": "Could not parse Driver Store XML. Cleanup stopped: ",
    "DriverNodesMissing": "Driver Store XML has no Driver nodes. Cleanup stopped to avoid missing driver packages.",
    "DriverIndexReady": "Driver Store index ready. OEM INF files: {0}",
    "ClosingProcesses": "Closing iPhoneMirror processes…",
    "BluetoothExcluded": "BTHLE device safely excluded: {0}",
    "DeviceNode": "Device node: {0}",
    "DeviceNodeRestart": "Device node removed; a restart is required to finish: {0}",
    "DeviceNodeFailedDetail": "Could not remove device node: {0} ",
    "DeviceNodeFailed": "Could not remove device node: {0}",
    "DriverPackage": "Driver package: {0}",
    "DriverPackageRestart": "Driver package removed; a restart is required to finish: {0}",
    "DriverPackageFailedDetail": "Could not remove driver package: {0} ",
    "DriverPackageFailed": "Could not remove driver package: {0}",
    "ManifestWriteFailed": "Could not write the manifest: ",
    "ProtectedLaunchRequired": "Run this tool through iPhoneMirror.Driver.exe or the cleanup entry in the release package to obtain protected administrator privileges.",
    "PnpUtilMissing": "pnputil.exe was not found: {0}",
    "ScopeWarning": "Cleanup will remove device nodes associated with the selected device and driver packages that can be safely removed, which may include official Apple drivers. Packages still used by other devices will be preserved.",
    "ReinstallAdvice": "Afterward, reinstall Apple Devices or iTunes to restore the required drivers.",
    "ScanningDevices": "Scanning devices…",
    "MappingDevices": "Mapping Apple device relationships…",
    "PhysicalDeviceCount": "Apple physical devices grouped: {0}",
    "NoAppleDevice": "No connected iPhone/iPad was found.",
    "CheckRequirements": "Check the following:",
    "RequirementUsb": "  1. The iPhone/iPad is connected by USB",
    "RequirementUnlocked": "  2. The device is unlocked",
    "RequirementTrusted": "  3. You have trusted this computer on the device",
    "RequirementCable": "  4. The cable supports data transfer",
    "BluetoothNotListed": "BTHLE / Bluetooth LE devices are not listed.",
    "MappingDrivers": "Mapping driver relationships…",
    "MappingReady": "Device relationships are ready.",
    "DetectedDevices": "Detected Apple physical devices:",
    "PnpNodeCount": "    PnP nodes: {0}",
    "DriverStoreCount": "    Driver Store packages: {0}",
    "ListOnly": "List-only mode. No system changes were made.",
    "SelectDevice": "Enter a device number, or Q to cancel",
    "InvalidDeviceNumber": "Invalid device number.",
    "CleanupPlan": " Cleanup plan",
    "SelectedDevice": "Device: {0}",
    "OnePhysicalDevice": "Physical devices: 1",
    "AssociatedNodes": "Associated PnP nodes: {0}",
    "AssociatedDrivers": "Total associated driver packages: {0}",
    "PreviewOnly": " Preview only. No system changes were made.",
    "NoRemovableNodes": "There are no target PnP nodes to remove.",
    "Irreversible": "This operation cannot be undone.",
    "Confirm": "Type {0} to confirm:",
    "ConfirmationMismatch": "The confirmation text did not match. No changes were made.",
    "FinalDeviceCheck": "Performing the final device check before removal…",
    "SelectedDisconnected": "The selected iPhone disconnected.",
    "RemovingNodes": " Removing device nodes",
    "RemovingPackages": " Removing Driver Store packages",
    "WaitingWindows": "Waiting for Windows to update device status…",
    "FinalVerification": "Performing final verification…",
    "CleanupResults": " Cleanup results",
    "NodesRemoved": "Target PnP nodes were removed.",
    "UnresolvedNodes": "Target PnP nodes remaining: {0}.",
    "RemainingNodes": "Target nodes remaining: {0}.",
    "PackagesHandled": "Target Driver Store packages were processed.",
    "UnresolvedPackages": "Target Driver Store packages remaining: {0}.",
    "RemainingPackages": "Driver packages remaining: {0}.",
    "RestartRequired": "Windows reported that some operations require a restart.",
    "LogPath": "Log: {0}",
    "Error": "Error: {0}",
    "Location": "Location:",
    "StackTrace": "Stack trace:",
    "ErrorLog": "Error log: {0}",
    "Title": " Remove iPhone/iPad associated drivers",
    "Completed": " Cleanup completed successfully.",
    "CompletedWithErrors": " Cleanup finished. Errors: {0}",
    "InternalError": " Cleanup tool internal error",
    "FatalError": "iPhoneMirror driver cleanup fatal error",
    "SharedPackagePreserved": "Driver package {0} is used by other devices and has been preserved.",
    "PackagesPreservedUnsupported": "This Windows version cannot list all driver package users. Only selected device nodes will be removed; driver packages will be preserved.",
    "DriverOperationBusy": "Another driver operation or rollback is in progress. Wait for it to finish before retrying cleanup."
  },
  "zh-TW": {
    "Pause": "按 Enter 鍵關閉視窗",
    "ScanPnp": "正在掃描目前 PnP 裝置…",
    "PnpCached": "PnP 快取完成：{0} 個裝置",
    "PnpFallback": "Get-PnpDevice 掃描失敗，正在改用 pnputil：",
    "PnpEnumerationFailed": "無法列舉 PnP 裝置，pnputil 結束代碼為 {0}",
    "PnpXmlEmpty": "pnputil PnP XML 為空，無法列舉裝置。",
    "PnpXmlInvalid": "pnputil PnP XML 解析失敗：",
    "NoConnectedPnp": "pnputil PnP XML 中沒有目前連線的裝置。",
    "NoCachedPnp": "pnputil PnP 快取完成：0 個裝置",
    "NoAvailablePnp": "pnputil PnP XML 中沒有可用裝置。",
    "PnpUtilCached": "pnputil PnP 快取完成：{0} 個裝置",
    "IndexDriverStore": "正在建立 Driver Store 驅動程式索引…",
    "DriverXmlUnavailable": "無法使用 Driver Store XML，結束代碼為 {0}",
    "DriverEnumerationFailed": "pnputil Driver Store 列舉失敗，結束代碼為 {0}；",
    "StopMissingPackages": "已停止清理，以免遺漏驅動程式套件。",
    "DriverXmlEmpty": "Driver Store XML 為空。",
    "DriverXmlEmptyStopped": "Driver Store XML 為空，已停止清理，以免遺漏驅動程式套件。",
    "DriverXmlInvalid": "Driver Store XML 解析失敗：",
    "DriverXmlInvalidStopped": "Driver Store XML 解析失敗，已停止清理：",
    "DriverNodesMissing": "Driver Store XML 中沒有 Driver 節點，已停止清理，以免遺漏驅動程式套件。",
    "DriverIndexReady": "Driver Store 索引完成：{0} 個 OEM INF",
    "ClosingProcesses": "正在關閉 iPhoneMirror 相關程序…",
    "BluetoothExcluded": "已安全排除 BTHLE 裝置：{0}",
    "DeviceNode": "裝置節點：{0}",
    "DeviceNodeRestart": "裝置節點已移除，重新啟動後完成：{0}",
    "DeviceNodeFailedDetail": "裝置節點刪除失敗：{0} ",
    "DeviceNodeFailed": "裝置節點刪除失敗：{0}",
    "DriverPackage": "驅動程式套件：{0}",
    "DriverPackageRestart": "驅動程式套件已刪除，重新啟動後完成：{0}",
    "DriverPackageFailedDetail": "驅動程式套件刪除失敗：{0} ",
    "DriverPackageFailed": "驅動程式套件刪除失敗：{0}",
    "ManifestWriteFailed": "無法寫入清單：",
    "ProtectedLaunchRequired": "請透過 iPhoneMirror.Driver.exe 或發布套件中的清理入口執行此工具，以取得受保護的管理員權限。",
    "PnpUtilMissing": "找不到 pnputil.exe：{0}",
    "ScopeWarning": "將清理所選裝置的關聯裝置節點及可安全移除的驅動程式套件，可能包括 Apple 官方驅動程式；其他裝置仍在使用的驅動程式套件會保留。",
    "ReinstallAdvice": "完成後可重新安裝 Apple Devices 或 iTunes，以還原所需驅動程式。",
    "ScanningDevices": "正在掃描裝置…",
    "MappingDevices": "正在建立 Apple 裝置關係…",
    "PhysicalDeviceCount": "Apple 實體裝置分組完成：{0} 部",
    "NoAppleDevice": "找不到目前連線的 iPhone/iPad。",
    "CheckRequirements": "請確認：",
    "RequirementUsb": "  1. iPhone/iPad 已透過 USB 連線",
    "RequirementUnlocked": "  2. 裝置已解鎖",
    "RequirementTrusted": "  3. 已在裝置上選擇「信任此電腦」",
    "RequirementCable": "  4. 傳輸線支援資料傳輸",
    "BluetoothNotListed": "BTHLE / Bluetooth LE 裝置不會顯示。",
    "MappingDrivers": "正在建立驅動程式關係…",
    "MappingReady": "裝置關係建立完成。",
    "DetectedDevices": "偵測到以下 Apple 實體裝置：",
    "PnpNodeCount": "    PnP 節點：{0}",
    "DriverStoreCount": "    Driver Store 驅動程式套件：{0}",
    "ListOnly": "僅清單模式，未修改系統。",
    "SelectDevice": "請輸入裝置編號；輸入 Q 取消",
    "InvalidDeviceNumber": "裝置編號無效。",
    "CleanupPlan": " 清理計劃",
    "SelectedDevice": "裝置：{0}",
    "OnePhysicalDevice": "實體裝置：1 部",
    "AssociatedNodes": "關聯 PnP 節點：{0}",
    "AssociatedDrivers": "關聯驅動程式套件總數：{0}",
    "PreviewOnly": " 僅預覽，未修改系統",
    "NoRemovableNodes": "沒有可刪除的目標 PnP 節點。",
    "Irreversible": "此操作無法復原。",
    "Confirm": "請輸入 {0} 確認：",
    "ConfirmationMismatch": "確認文字不符，未作任何修改。",
    "FinalDeviceCheck": "正在執行刪除前的最終裝置確認…",
    "SelectedDisconnected": "所選 iPhone 已中斷連線。",
    "RemovingNodes": " 正在卸載裝置節點",
    "RemovingPackages": " 正在刪除 Driver Store 驅動程式套件",
    "WaitingWindows": "正在等待 Windows 更新裝置狀態…",
    "FinalVerification": "正在執行最終驗證…",
    "CleanupResults": " 清理結果",
    "NodesRemoved": "目標 PnP 節點已清理。",
    "UnresolvedNodes": "仍存在 {0} 個目標 PnP 節點。",
    "RemainingNodes": "仍存在 {0} 個目標節點。",
    "PackagesHandled": "目標 Driver Store 驅動程式套件已處理。",
    "UnresolvedPackages": "仍存在 {0} 個目標 Driver Store 驅動程式套件。",
    "RemainingPackages": "仍存在 {0} 個驅動程式套件。",
    "RestartRequired": "Windows 報告部分操作需要重新啟動。",
    "LogPath": "記錄檔：{0}",
    "Error": "錯誤：{0}",
    "Location": "位置：",
    "StackTrace": "呼叫堆疊：",
    "ErrorLog": "錯誤記錄檔：{0}",
    "Title": " 清除 iPhone/iPad 關聯驅動程式",
    "Completed": " 清理完成。",
    "CompletedWithErrors": " 清理結束，發生 {0} 個錯誤。",
    "InternalError": " 清理工具內部錯誤",
    "FatalError": "iPhoneMirror 驅動程式清理嚴重錯誤",
    "SharedPackagePreserved": "驅動程式套件 {0} 正被其他裝置使用，已保留。",
    "PackagesPreservedUnsupported": "此 Windows 版本無法完整列出驅動程式套件使用者；只會清理所選裝置節點，保留驅動程式套件。",
    "DriverOperationBusy": "另一個驅動操作或復原仍在進行，請等待完成後重試清理。"
  }
}
'@ | ConvertFrom-Json
if ($Language -eq 'system') {
    try {
        $settingsPath = Join-Path $env:LOCALAPPDATA 'iPhoneMirror\settings.json'
        $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $Language = $settings.Language
    } catch { $Language = 'system' }
}
if ($Language -notin @('zh-CN', 'zh-HK', 'zh-TW', 'en-US')) {
    $cultureName = [Globalization.CultureInfo]::InstalledUICulture.Name
    $Language = if ($cultureName -match '^zh-(TW|Hant-TW)$') { 'zh-TW' }
        elseif ($cultureName -match '^zh-(Hant|HK|MO|CHT)') { 'zh-HK' }
        elseif ($cultureName -match '^zh') { 'zh-CN' } else { 'en-US' }
}
function Get-CleanupText {
    param([string]$Key, [object[]]$Values = @())
    $template = $script:CleanupMessages.$Language.$Key
    if ($Values.Count -eq 0) { return $template }
    return [string]::Format([Globalization.CultureInfo]::GetCultureInfo($Language), $template, $Values)
}


# ============================================================
# Configuration
# ============================================================

$PnpUtil = Join-Path $env:windir 'System32\pnputil.exe'

$LogRoot = Join-Path `
    $env:ProgramData `
    'iPhoneMirror.Driver\DeviceCleanup'

# Apple USB VID
$AppleVid = '05AC'

# This is the same explicit iPhone/iPad capture PID table used by the native
# discovery and driver-manager paths. Keep this list aligned with
# config/apple-mobile-capture-pids.txt. Apple TV (12A7), Watch (12AF), HomePod
# (12B0), and other Apple USB products remain outside this cleanup scope.
$AppleMobileCaptureProductIds =
    [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )

foreach ($productId in @(
    '1290'
    '1291'
    '1292'
    '1293'
    '1294'
    '1297'
    '1299'
    '129A'
    '129C'
    '129D'
    '129E'
    '129F'
    '12A0'
    '12A1'
    '12A2'
    '12A3'
    '12A4'
    '12A5'
    '12A6'
    '12A8'
    '12A9'
    '12AA'
    '12AB'
    '12AC'
)) {
    [void]$AppleMobileCaptureProductIds.Add($productId)
}

# ============================================================
# Runtime state
# ============================================================

$script:DeviceCache = @{}
$script:ContainerCache = @{}
$script:DriverInventory = @{}
$script:DriverInventoryInitialized = $false
$script:DriverInventorySupported = $true
$script:PreservedDrivers = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$cleanupMutex = $null
$cleanupLockTaken = $false
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:RestartRequired = $false
$script:RestartPendingNodes =
    [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
$script:RestartPendingDrivers =
    [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )

$script:CurrentLogPath = ''

# ============================================================
# Console / logging
# ============================================================

function Write-Log {
    param(
        [AllowEmptyString()]
        [string]$Message = ''
    )

    if ($null -eq $Message) {
        $Message = ''
    }

    if ($Message.Length -eq 0) {
        Write-Host ''
        return
    }

    $time = Get-Date -Format 'HH:mm:ss.fff'

    Write-Host "[$time] $Message"
}

function Write-OK {
    param(
        [AllowEmptyString()]
        [string]$Message = ''
    )

    if ([string]::IsNullOrEmpty($Message)) {
        Write-Host ''
        return
    }

    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param(
        [AllowEmptyString()]
        [string]$Message = ''
    )

    if ([string]::IsNullOrEmpty($Message)) {
        Write-Host ''
        return
    }

    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param(
        [AllowEmptyString()]
        [string]$Message = ''
    )

    if ([string]::IsNullOrEmpty($Message)) {
        Write-Host ''
        return
    }

    Write-Host "  [ERROR] $Message" -ForegroundColor Red
}

function Add-Failure {
    param(
        [string]$Message
    )

    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $script:Failures.Add($Message)
    }
}

function Stop-Cleanup {
    param(
        [int]$ExitCode = 0
    )

    if (-not $NoPause) {
        Write-Host ''

        try {
            [void](Read-Host (Get-CleanupText 'Pause'))
        }
        catch {
        }
    }

    exit $ExitCode
}

# ============================================================
# PnPUtil
# ============================================================

function Invoke-PnpUtil {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    try {

        $output = @(
            & $PnpUtil @Arguments 2>&1 |
                ForEach-Object {
                    $_.ToString()
                }
        )

        return [PSCustomObject]@{
            ExitCode = $LASTEXITCODE
            Output   = $output
            Text     = ($output -join [Environment]::NewLine)
        }
    }
    catch {

        return [PSCustomObject]@{
            ExitCode = -1
            Output   = @($_.Exception.Message)
            Text     = $_.Exception.Message
        }
    }
}

# ============================================================
# BTHLE filtering
# ============================================================

function Test-IsBthle {
    param(
        [AllowEmptyString()]
        [string]$InstanceId = '',

        [AllowEmptyString()]
        [string]$Name = '',

        [AllowEmptyString()]
        [string]$Class = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($InstanceId)) {

        if ($InstanceId -match '(?i)^BTHLE\\') {
            return $true
        }

        if ($InstanceId -match '(?i)BTHLEDevice') {
            return $true
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Name)) {

        if ($Name -match '(?i)Bluetooth.*LE') {
            return $true
        }

        if ($Name -match '(?i)Bluetooth Low Energy') {
            return $true
        }
    }

    return $false
}

# ============================================================
# Apple USB detection
# ============================================================

function Test-IsAppleUsb {
    param(
        [AllowEmptyString()]
        [string]$InstanceId = ''
    )

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        return $false
    }

    if ($InstanceId -notmatch
        '(?i)^USB\\VID_05AC&PID_([0-9A-F]{4})' +
        '(?:&(?:MI_[0-9A-F]{2}|REV_[0-9A-F]{4}|RESTORE_MODE))*\\') {
        return $false
    }

    return $AppleMobileCaptureProductIds.Contains($Matches[1])
}

# ============================================================
# Get safe property
# ============================================================

function Get-SafeProperty {
    param(
        [object]$Object,

        [string]$Name,

        [AllowEmptyString()]
        [string]$Default = ''
    )

    if ($null -eq $Object) {
        return $Default
    }

    try {

        $property =
            $Object.PSObject.Properties[$Name]

        if ($null -eq $property) {
            return $Default
        }

        $value =
            $property.Value

        if ($null -eq $value) {
            return $Default
        }

        return [string]$value
    }
    catch {

        return $Default
    }
}

# ============================================================
# Get Container ID
# ============================================================

function Get-DeviceContainerId {
    param(
        [string]$InstanceId
    )

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        return ''
    }

    if ($script:ContainerCache.ContainsKey($InstanceId)) {
        return [string]$script:ContainerCache[$InstanceId]
    }

    $containerId = ''

    try {

        if (
            $null -ne (
                Get-Command `
                    Get-PnpDeviceProperty `
                    -ErrorAction SilentlyContinue
            )
        ) {

            $property =
                Get-PnpDeviceProperty `
                    -InstanceId $InstanceId `
                    -KeyName 'DEVPKEY_Device_ContainerId' `
                    -ErrorAction Stop

            if ($null -ne $property) {

                $value =
                    $property.Data

                if ($null -ne $value) {

                    $containerId =
                        [string]$value
                }
            }
        }
    }
    catch {

        # A single failed property lookup must never
        # abort the complete device scan.
        $containerId = ''
    }

    $script:ContainerCache[$InstanceId] =
        $containerId

    return $containerId
}

# ============================================================
# Normalize USB physical identity
# ============================================================

function Get-NormalizedUsbIdentity {
    param(
        [string]$InstanceId
    )

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        return ''
    }

    $id =
        $InstanceId.ToUpperInvariant()

    # USB interface:
    #
    # USB\VID_05AC&PID_12A8&MI_00\XXXXXXXX
    #
    # USB\VID_05AC&PID_12A8\XXXXXXXX

    $id =
        $id -replace `
            '(?i)&MI_[0-9A-F]{2}(?=\\)', ''

    # Remove known USB interface class fragments.

    $id =
        $id -replace `
            '(?i)&REV_[0-9A-F]{4}', ''

    return $id
}

# Read a value from either an XML attribute or a child element. pnputil has
# used both forms across Windows releases and localized output must not be
# parsed by looking for translated labels.
function Get-XmlValue {
    param(
        [System.Xml.XmlNode]$Node,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Node) {
        return ''
    }

    try {
        if ($null -ne $Node.Attributes) {
            $attribute = $Node.Attributes.GetNamedItem($Name)
            if ($null -ne $attribute -and
                -not [string]::IsNullOrWhiteSpace($attribute.Value)) {
                return [string]$attribute.Value
            }
        }

        $child = $Node.SelectSingleNode(
            "./*[local-name()='$Name']"
        )
        if ($null -ne $child) {
            return [string]$child.InnerText
        }
    }
    catch {
        return ''
    }

    return ''
}

# ============================================================
# Initialize PnP cache
# ============================================================

function Initialize-DeviceCache {

    $script:DeviceCache.Clear()
    $script:ContainerCache.Clear()

    Write-Log (Get-CleanupText 'ScanPnp')

    $getPnpDevice =
        Get-Command `
            Get-PnpDevice `
            -ErrorAction SilentlyContinue

    if ($null -ne $getPnpDevice) {

        try {

            $devices =
                @(
                    Get-PnpDevice `
                        -PresentOnly `
                        -ErrorAction Stop
                )

            foreach ($device in $devices) {

                $instanceId =
                    Get-SafeProperty `
                        $device `
                        'InstanceId'

                if ([string]::IsNullOrWhiteSpace($instanceId)) {
                    continue
                }

                $friendlyName =
                    Get-SafeProperty `
                        $device `
                        'FriendlyName'

                $class =
                    Get-SafeProperty `
                        $device `
                        'Class'

                if (
                    Test-IsBthle `
                        $instanceId `
                        $friendlyName `
                        $class
                ) {
                    continue
                }

                $script:DeviceCache[$instanceId] =
                    $device
            }

            Write-OK (
                (Get-CleanupText 'PnpCached' -Values @($script:DeviceCache.Count))
            )

            return
        }
        catch {

            Write-Warn (
                (Get-CleanupText 'PnpFallback') +
                $_.Exception.Message
            )
        }
    }

    # ========================================================
    # pnputil fallback
    # ========================================================

    $result =
        Invoke-PnpUtil @(
            '/enum-containers'
            '/connected'
            '/devices'
            '/format'
            'xml'
        )

    if ($result.ExitCode -ne 0) {

        throw (
            (Get-CleanupText 'PnpEnumerationFailed' -Values @($result.ExitCode))
        )
    }

    if ([string]::IsNullOrWhiteSpace($result.Text)) {
        throw (Get-CleanupText 'PnpXmlEmpty')
    }

    try {
        [xml]$xml = $result.Text
    }
    catch {
        throw (
            (Get-CleanupText 'PnpXmlInvalid') +
            $_.Exception.Message
        )
    }

    $containerNodes =
        @($xml.SelectNodes('//*[local-name()="Container"]'))

    if ($containerNodes.Count -eq 0) {
        Write-Warn (Get-CleanupText 'NoConnectedPnp')
        Write-OK (Get-CleanupText 'NoCachedPnp')
        return
    }

    foreach ($containerNode in $containerNodes) {
        $containerId = Get-XmlValue $containerNode 'ContainerId'
        foreach ($deviceNode in @(
            $containerNode.SelectNodes(
                './*[local-name()="Devices"]/*[local-name()="Device"]'
            )
        )) {
            $instanceId = Get-XmlValue $deviceNode 'InstanceId'
            if ([string]::IsNullOrWhiteSpace($instanceId)) {
                continue
            }

            $friendlyName = Get-XmlValue $deviceNode 'DeviceDescription'
            if ([string]::IsNullOrWhiteSpace($friendlyName)) {
                $friendlyName = Get-XmlValue $deviceNode 'FriendlyName'
            }

            if (Test-IsBthle $instanceId $friendlyName '') {
                continue
            }

            $script:DeviceCache[$instanceId] =
                [PSCustomObject]@{
                    InstanceId   = $instanceId
                    FriendlyName = $friendlyName
                    Class        = ''
                    ContainerId  = $containerId
                }
        }
    }

    if ($script:DeviceCache.Count -eq 0) {
        Write-Warn (Get-CleanupText 'NoAvailablePnp')
    }

    Write-OK (
        (Get-CleanupText 'PnpUtilCached' -Values @($script:DeviceCache.Count))
    )
}

# ============================================================
# Get Apple candidate nodes
# ============================================================

function Get-AppleCandidateNodes {

    $result =
        [System.Collections.Generic.List[object]]::new()

    foreach ($instanceId in @($script:DeviceCache.Keys)) {

        $device =
            $script:DeviceCache[$instanceId]

        if (
            Test-IsBthle `
                $instanceId `
                (Get-SafeProperty $device 'FriendlyName') `
                (Get-SafeProperty $device 'Class')
        ) {
            continue
        }

        if (-not (Test-IsAppleUsb $instanceId)) {
            continue
        }

        $containerId =
            Get-SafeProperty $device 'ContainerId'

        if ([string]::IsNullOrWhiteSpace($containerId)) {
            $containerId =
                Get-DeviceContainerId $instanceId
        }

        $normalizedUsb =
            Get-NormalizedUsbIdentity $instanceId

        $result.Add(
            [PSCustomObject]@{
                InstanceId   = $instanceId
                Device        = $device
                FriendlyName = Get-SafeProperty `
                    $device `
                    'FriendlyName'
                Class         = Get-SafeProperty `
                    $device `
                    'Class'
                ContainerId   = $containerId
                UsbIdentity   = $normalizedUsb
            }
        )
    }

    return @($result)
}

# ============================================================
# Merge physical devices
# ============================================================

function Get-ApplePhysicalDevices {

    $nodes =
        @(Get-AppleCandidateNodes)

    if ($nodes.Count -eq 0) {
        return @()
    }

    # --------------------------------------------------------
    # Union-Find style grouping.
    #
    # Nodes sharing the same ContainerId OR normalized USB
    # identity are considered the same physical device.
    # --------------------------------------------------------

    $groups = @{}

    foreach ($node in $nodes) {

        $keys =
            [System.Collections.Generic.List[string]]::new()

        if (-not [string]::IsNullOrWhiteSpace($node.ContainerId)) {

            $keys.Add(
                'C:' +
                $node.ContainerId.ToUpperInvariant()
            )
        }

        if (-not [string]::IsNullOrWhiteSpace($node.UsbIdentity)) {

            $keys.Add(
                'U:' +
                $node.UsbIdentity.ToUpperInvariant()
            )
        }

        if ($keys.Count -eq 0) {
            continue
        }

        $existingGroup = $null

        foreach ($key in $keys) {

            if ($groups.ContainsKey($key)) {

                $candidateGroup =
                    $groups[$key]

                if ($null -eq $existingGroup) {

                    $existingGroup =
                        $candidateGroup

                    continue
                }

                if ([object]::ReferenceEquals(
                    $existingGroup,
                    $candidateGroup
                )) {
                    continue
                }

                # A node can connect two existing groups through its
                # container and USB identities. Merge them before adding it.
                foreach ($member in $candidateGroup) {
                    $existingGroup.Add($member)
                }

                foreach ($groupKey in @($groups.Keys)) {
                    if ([object]::ReferenceEquals(
                        $groups[$groupKey],
                        $candidateGroup
                    )) {
                        $groups[$groupKey] = $existingGroup
                    }
                }
            }
        }

        if ($null -eq $existingGroup) {

            $existingGroup =
                [System.Collections.Generic.List[object]]::new()
        }

        $existingGroup.Add($node)

        foreach ($key in $keys) {
            $groups[$key] =
                $existingGroup
        }
    }

    # --------------------------------------------------------
    # Deduplicate group objects.
    # --------------------------------------------------------

    $uniqueGroups =
        [System.Collections.Generic.List[object]]::new()

    $seenGroupObjects =
        [System.Collections.Generic.HashSet[int]]::new()

    foreach ($key in $groups.Keys) {

        $group =
            $groups[$key]

        $hash =
            [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode(
                $group
            )

        if ($seenGroupObjects.Add($hash)) {

            $uniqueGroups.Add($group)
        }
    }

    # --------------------------------------------------------
    # Create final immutable-ish objects.
    #
    # IMPORTANT:
    # Drivers is created HERE.
    # This avoids the previous StrictMode crash.
    # --------------------------------------------------------

    $physicalDevices =
        [System.Collections.Generic.List[object]]::new()

    foreach ($group in $uniqueGroups) {

        $allNodes =
            @(
                $group |
                    Sort-Object InstanceId -Unique
            )

        if ($allNodes.Count -eq 0) {
            continue
        }

        $parent =
            $allNodes |
                Where-Object {
                    $_.InstanceId -notmatch `
                        '(?i)&MI_[0-9A-F]{2}\\'
                } |
                Select-Object -First 1

        if ($null -eq $parent) {
            $parent = $allNodes[0]
        }

        $name =
            [string]$parent.FriendlyName

        if ([string]::IsNullOrWhiteSpace($name)) {

            $name =
                'Apple iPhone/iPad'
        }

        # More useful name normalization.

        if (
            $name -match '(?i)iPhone'
        ) {

            $displayName = 'Apple iPhone'
        }
        elseif (
            $name -match '(?i)iPad'
        ) {

            $displayName = 'Apple iPad'
        }
        else {

            $displayName =
                $name
        }

        $instanceIds =
            @(
                $allNodes |
                    ForEach-Object {
                        [string]$_.InstanceId
                    } |
                    Where-Object {
                        -not (Test-IsBthle $_)
                    } |
                    Sort-Object -Unique
            )

        $containerIds =
            @(
                $allNodes |
                    ForEach-Object {
                        [string]$_.ContainerId
                    } |
                    Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_)
                    } |
                    Sort-Object -Unique
            )

        $usbIdentities =
            @(
                $allNodes |
                    ForEach-Object {
                        [string]$_.UsbIdentity
                    } |
                    Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_)
                    } |
                    Sort-Object -Unique
            )

        $physicalKey = ''

        if ($containerIds.Count -gt 0) {

            $physicalKey =
                'CONTAINER:' +
                $containerIds[0]
        }
        elseif ($usbIdentities.Count -gt 0) {

            $physicalKey =
                'USB:' +
                $usbIdentities[0]
        }
        else {

            $physicalKey =
                'NODE:' +
                $instanceIds[0]
        }

        $physicalDevices.Add(
            [PSCustomObject]@{
                Key           = $physicalKey
                Name          = $displayName
                Parent        = $parent
                Nodes         = @($allNodes)
                InstanceIds   = $instanceIds
                ContainerIds  = $containerIds
                UsbIdentities = $usbIdentities
                Drivers       = @()
            }
        )
    }

    return @(
        $physicalDevices |
            Sort-Object Name, Key
    )
}

# ============================================================
# Driver Store inventory
# ============================================================

function Test-PnpDriverInventorySupport {
    $help = Invoke-PnpUtil @('/?')
    $syntax = [regex]::Match($help.Text, '(?m)^[ \t]+/enum-drivers[^\r\n]*(?:\r?\n[ \t]+\[[^\r\n]*)*').Value
    return $syntax.Contains('/devices') -and $syntax.Contains('/format')
}

function Get-DriverStoreInventory {

    if ($script:DriverInventoryInitialized) {

        return @(
            $script:DriverInventory.Values
        )
    }

    if (-not (Test-PnpDriverInventorySupport)) {
        $script:DriverInventorySupported = $false
        $script:DriverInventoryInitialized = $true
        Write-Warn (Get-CleanupText 'PackagesPreservedUnsupported')
        return @()
    }

    Write-Log (Get-CleanupText 'IndexDriverStore')

    $result =
        Invoke-PnpUtil @(
            '/enum-drivers'
            '/devices'
            '/format'
            'xml'
        )

    if ($result.ExitCode -ne 0) {

        Write-Warn (
            (Get-CleanupText 'DriverXmlUnavailable' -Values @($result.ExitCode))
        )

        throw (
            (Get-CleanupText 'DriverEnumerationFailed' -Values @($result.ExitCode)) +
            (Get-CleanupText 'StopMissingPackages')
        )
    }

    $xmlText =
        $result.Text

    if ([string]::IsNullOrWhiteSpace($xmlText)) {

        Write-Warn (Get-CleanupText 'DriverXmlEmpty')

        throw (Get-CleanupText 'DriverXmlEmptyStopped')
    }

    try {

        [xml]$xml =
            $xmlText
    }
    catch {

        Write-Warn (
            (Get-CleanupText 'DriverXmlInvalid') +
            $_.Exception.Message
        )

        throw (
            (Get-CleanupText 'DriverXmlInvalidStopped') +
            $_.Exception.Message
        )
    }

    # Each Driver node owns its Devices collection. Never scan
    # the complete XML for IDs, or unrelated packages could be
    # associated with the selected Apple device.
    $driverNodes =
        @($xml.SelectNodes('//*[local-name()="Driver"]'))

    if ($driverNodes.Count -eq 0) {
        throw (Get-CleanupText 'DriverNodesMissing')
    }

    foreach ($driverNode in $driverNodes) {

        $infName =
            [string]$driverNode.GetAttribute('DriverName')

        if ($infName -notmatch '(?i)^oem\d+\.inf$') {
            continue
        }

        $deviceIds =
            [System.Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase
            )

        foreach (
            $deviceNode in @(
                $driverNode.SelectNodes(
                    './/*[local-name()="Device"]'
                )
            )
        ) {

            $id =
                [string]$deviceNode.GetAttribute('InstanceId')

            if (
                [string]::IsNullOrWhiteSpace($id)
            ) {
                continue
            }

            [void]$deviceIds.Add($id)
        }

        $originalNameNode =
            $driverNode.SelectSingleNode(
                './*[local-name()="OriginalName"]'
            )

        $providerNode =
            $driverNode.SelectSingleNode(
                './*[local-name()="ProviderName"]'
            )

        $originalName = ''
        $provider = ''

        if ($null -ne $originalNameNode) {
            $originalName = [string]$originalNameNode.InnerText
        }

        if ($null -ne $providerNode) {
            $provider = [string]$providerNode.InnerText
        }

        $record =
            [PSCustomObject]@{
                InfName      = $infName
                OriginalName = $originalName
                Provider     = $provider
                Devices      = @($deviceIds)
            }

        $script:DriverInventory[
            $infName.ToUpperInvariant()
        ] = $record
    }

    $script:DriverInventoryInitialized = $true

    Write-OK (
        (Get-CleanupText 'DriverIndexReady' -Values @($script:DriverInventory.Count))
    )

    return @(
        $script:DriverInventory.Values
    )
}

# ============================================================
# Find drivers for one physical device
# ============================================================

function Test-DriverExclusiveToDevice {
    param([object]$Driver, [object]$PhysicalDevice)
    if (@($Driver.Devices).Count -eq 0) { return $false }
    foreach ($id in $Driver.Devices) {
        if (Test-IsBthle $id) { return $false }
        $identity = Get-NormalizedUsbIdentity $id
        $matches = @($PhysicalDevice.InstanceIds | Where-Object {
            $_ -ieq $id -or (-not [string]::IsNullOrWhiteSpace($identity) -and
                (Get-NormalizedUsbIdentity $_) -ieq $identity)
        })
        if ($matches.Count -eq 0) { return $false }
    }
    return $true
}

function Get-DriversForPhysicalDevice {
    param([object]$PhysicalDevice)
    return @(Get-DriverStoreInventory | Where-Object {
        Test-DriverExclusiveToDevice $_ $PhysicalDevice
    } | Sort-Object InfName -Unique)
}

# ============================================================
# Stop iPhoneMirror
# ============================================================

function Stop-iPhoneMirrorProcesses {

    $processNames = @(
        'iPhoneMirror'
        # Driver managers and elevated transaction hosts must never be killed.
    )

    $processes =
        @(
            Get-Process `
                -Name $processNames `
                -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Id -ne $ExcludeProcessId -and
                    $_.Id -ne $ExcludeParentProcessId
                }
        )

    if ($processes.Count -eq 0) {
        return
    }

    Write-Log (Get-CleanupText 'ClosingProcesses')

    foreach ($process in $processes) {

        try {

            Stop-Process `
                -Id $process.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    Start-Sleep -Milliseconds 1000
}

# ============================================================
# Remove device node
# ============================================================

function Remove-DeviceNode {
    param(
        [string]$InstanceId
    )

    if (Test-IsBthle $InstanceId) {

        Write-Warn (
            (Get-CleanupText 'BluetoothExcluded' -Values @($InstanceId))
        )

        return $true
    }

    $result =
        Invoke-PnpUtil @(
            '/remove-device'
            $InstanceId
            # Each confirmed instance is removed separately; avoid unsupported flags.
        )

    if ($result.ExitCode -eq 0) {

        Write-OK (
            (Get-CleanupText 'DeviceNode' -Values @($InstanceId))
        )

        return $true
    }

    if ($result.ExitCode -eq 3010) {

        $script:RestartRequired = $true
        [void]$script:RestartPendingNodes.Add($InstanceId)

        Write-OK (
            (Get-CleanupText 'DeviceNodeRestart' -Values @($InstanceId))
        )

        return $true
    }

    Write-Err (
        (Get-CleanupText 'DeviceNodeFailedDetail' -Values @($InstanceId)) +
        "(ExitCode=$($result.ExitCode))"
    )

    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {

        Write-Host $result.Text `
            -ForegroundColor DarkGray
    }

    Add-Failure (
        (Get-CleanupText 'DeviceNodeFailed' -Values @($InstanceId))
    )

    return $false
}

# ============================================================
# Remove driver package
# ============================================================

function Remove-DriverPackage {
    param(
        [string]$InfName,
        [object]$PhysicalDevice
    )

    if (
        [string]::IsNullOrWhiteSpace($InfName) -or
        $InfName -notmatch '(?i)^oem\d+\.inf$'
    ) {

        return $false
    }

    # Refresh all package users immediately before deletion. Windows also refuses
    # to delete an in-use package because /uninstall and /force are never passed.
    $script:DriverInventory.Clear()
    $script:DriverInventoryInitialized = $false
    $current = @(Get-DriverStoreInventory | Where-Object { $_.InfName -ieq $InfName })
    if (-not $script:DriverInventorySupported -or
        ($current.Count -gt 0 -and @($current[0].Devices).Count -gt 0 -and
         -not (Test-DriverExclusiveToDevice $current[0] $PhysicalDevice))) {
        [void]$script:PreservedDrivers.Add($InfName)
        Write-Warn (Get-CleanupText 'SharedPackagePreserved' -Values @($InfName))
        return $true
    }

    $result =
        Invoke-PnpUtil @(
            '/delete-driver'
            $InfName
        )

    if ($result.ExitCode -eq 0) {

        Write-OK (
            (Get-CleanupText 'DriverPackage' -Values @($InfName))
        )

        return $true
    }

    if ($result.ExitCode -eq 3010) {

        $script:RestartRequired = $true
        [void]$script:RestartPendingDrivers.Add($InfName)

        Write-OK (
            (Get-CleanupText 'DriverPackageRestart' -Values @($InfName))
        )

        return $true
    }

    Write-Err (
        (Get-CleanupText 'DriverPackageFailedDetail' -Values @($InfName)) +
        "(ExitCode=$($result.ExitCode))"
    )

    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {

        Write-Host $result.Text `
            -ForegroundColor DarkGray
    }

    Add-Failure (
        (Get-CleanupText 'DriverPackageFailed' -Values @($InfName))
    )

    return $false
}

# ============================================================
# Verify target device is connected
# ============================================================

function Test-PhysicalDeviceConnected {
    param(
        [object]$PhysicalDevice
    )

    foreach ($id in $PhysicalDevice.InstanceIds) {

        if (Test-IsBthle $id) {
            continue
        }

        if ($script:DeviceCache.ContainsKey($id)) {
            return $true
        }
    }

    return $false
}

# ============================================================
# Create manifest
# ============================================================

function Save-Manifest {
    param(
        [string]$Path,

        [object]$Device,

        [string[]]$DeviceIds,

        [object[]]$Drivers
    )

    try {

        [PSCustomObject]@{
            CreatedAt      = [DateTimeOffset]::Now
            DeviceName     = $Device.Name
            PhysicalKey    = $Device.Key
            ContainerIds   = @($Device.ContainerIds)
            UsbIdentities  = @($Device.UsbIdentities)
            DeviceIds      = @($DeviceIds)
            DriverPackages = @(
                $Drivers |
                    ForEach-Object {
                        $_.InfName
                    }
            )
        } |
            ConvertTo-Json -Depth 12 |
            Set-Content `
                -LiteralPath $Path `
                -Encoding UTF8
    }
    catch {

        Write-Warn (
            (Get-CleanupText 'ManifestWriteFailed') +
            $_.Exception.Message
        )
    }
}

# ============================================================
# MAIN
# ============================================================

if (-not $ListOnly -and -not $PreviewOnly) {

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Err (Get-CleanupText 'ProtectedLaunchRequired')
        Stop-Cleanup 5
    }
}

if (-not (Test-Path -LiteralPath $PnpUtil -PathType Leaf)) {

    Write-Err (
        (Get-CleanupText 'PnpUtilMissing' -Values @($PnpUtil))
    )

    Stop-Cleanup 10
}

try {

    [Console]::OutputEncoding =
        New-Object System.Text.UTF8Encoding($false)

    [Console]::InputEncoding =
        New-Object System.Text.UTF8Encoding($false)
}
catch {
}

Clear-Host

Write-Host '========================================' `
    -ForegroundColor Cyan

Write-Host (Get-CleanupText 'Title') `
    -ForegroundColor Cyan

Write-Host '========================================' `
    -ForegroundColor Cyan

Write-Host ''
Write-Host (Get-CleanupText 'ScopeWarning') `
    -ForegroundColor Yellow
Write-Host (Get-CleanupText 'ReinstallAdvice') `
    -ForegroundColor Yellow
Write-Host ''

try {

    # ========================================================
    # Scan
    # ========================================================

    Write-Log (Get-CleanupText 'ScanningDevices')

    Initialize-DeviceCache

    Write-Log (Get-CleanupText 'MappingDevices')

    $physicalDevices =
        @(Get-ApplePhysicalDevices)

    Write-OK (
        (Get-CleanupText 'PhysicalDeviceCount' -Values @($physicalDevices.Count))
    )

    if ($physicalDevices.Count -eq 0) {

        Write-Host ''
        Write-Warn (Get-CleanupText 'NoAppleDevice')
        Write-Host ''
        Write-Host (Get-CleanupText 'CheckRequirements')
        Write-Host (Get-CleanupText 'RequirementUsb')
        Write-Host (Get-CleanupText 'RequirementUnlocked')
        Write-Host (Get-CleanupText 'RequirementTrusted')
        Write-Host (Get-CleanupText 'RequirementCable')
        Write-Host ''
        Write-Host (Get-CleanupText 'BluetoothNotListed') `
            -ForegroundColor DarkGray

        Stop-Cleanup 2
    }

    # ========================================================
    # Driver inventory
    # ========================================================

    Write-Log (Get-CleanupText 'MappingDrivers')

    $initialDriverInventory =
        @(Get-DriverStoreInventory)

    # ========================================================
    # IMPORTANT:
    # Do NOT dynamically assign a missing property.
    #
    # Build a completely new object instead.
    # ========================================================

    $devicesWithDrivers =
        [System.Collections.Generic.List[object]]::new()

    foreach ($device in $physicalDevices) {

        $drivers =
            @(
                Get-DriversForPhysicalDevice `
                    $device
            )

        $devicesWithDrivers.Add(
            [PSCustomObject]@{
                Key           = $device.Key
                Name          = $device.Name
                Parent        = $device.Parent
                Nodes         = @($device.Nodes)
                InstanceIds   = @($device.InstanceIds)
                ContainerIds  = @($device.ContainerIds)
                UsbIdentities = @($device.UsbIdentities)
                Drivers       = @($drivers)
            }
        )
    }

    $physicalDevices =
        @($devicesWithDrivers)

    Write-OK (Get-CleanupText 'MappingReady')

    # ========================================================
    # Display
    # ========================================================

    Write-Host ''
    Write-Host (Get-CleanupText 'DetectedDevices') `
        -ForegroundColor Cyan

    Write-Host ''

    for ($i = 0; $i -lt $physicalDevices.Count; $i++) {

        $device =
            $physicalDevices[$i]

        Write-Host (
            '[{0}] {1}' -f
            ($i + 1),
            $device.Name
        ) -ForegroundColor White

        Write-Host (
            (Get-CleanupText 'PnpNodeCount') -f
            $device.InstanceIds.Count
        ) -ForegroundColor DarkGray

        Write-Host (
            (Get-CleanupText 'DriverStoreCount') -f
            $device.Drivers.Count
        ) -ForegroundColor DarkGray

        if ($device.ContainerIds.Count -gt 0) {

            Write-Host (
                '    Container：{0}' -f
                $device.ContainerIds[0]
            ) -ForegroundColor DarkGray
        }

        Write-Host ''
    }

    if ($ListOnly) {

        Write-OK (Get-CleanupText 'ListOnly')

        Stop-Cleanup 0
    }

    # ========================================================
    # Select
    # ========================================================

    $selectedIndex = -1

    while ($selectedIndex -lt 0) {

        $answer =
            Read-Host (Get-CleanupText 'SelectDevice')

        if ($answer -match '^(?i)q$') {
            Stop-Cleanup 0
        }

        $number = 0

        if (
            [int]::TryParse(
                $answer,
                [ref]$number
            ) -and
            $number -ge 1 -and
            $number -le $physicalDevices.Count
        ) {

            $selectedIndex =
                $number - 1
        }
        else {

            Write-Warn (Get-CleanupText 'InvalidDeviceNumber')
        }
    }

    $selected =
        $physicalDevices[$selectedIndex]

    # ========================================================
    # Target nodes
    # ========================================================

    $targetNodes =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )

    foreach ($id in $selected.InstanceIds) {

        if (
            -not [string]::IsNullOrWhiteSpace($id) -and
            -not (Test-IsBthle $id)
        ) {

            [void]$targetNodes.Add($id)
        }
    }

    $targetNodeArray =
        @(
            $targetNodes |
                Sort-Object Length -Descending
        )

    $targetUsbIdentities =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )

    foreach ($identity in $selected.UsbIdentities) {
        if (-not [string]::IsNullOrWhiteSpace($identity)) {
            [void]$targetUsbIdentities.Add($identity)
        }
    }

    $targetContainerIds =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )

    foreach ($containerId in $selected.ContainerIds) {
        if (-not [string]::IsNullOrWhiteSpace($containerId)) {
            [void]$targetContainerIds.Add($containerId)
        }
    }

    $targetDrivers =
        @(
            $selected.Drivers |
                Sort-Object InfName -Unique
        )

    # ========================================================
    # Summary
    # ========================================================

    Write-Host ''
    Write-Host '========================================' `
        -ForegroundColor Cyan

    Write-Host (Get-CleanupText 'CleanupPlan') `
        -ForegroundColor Cyan

    Write-Host '========================================' `
        -ForegroundColor Cyan

    Write-Host ''

    Write-Host (
        (Get-CleanupText 'SelectedDevice') -f
        $selected.Name
    )

    Write-Host (
        (Get-CleanupText 'OnePhysicalDevice')
    ) -ForegroundColor Green

    Write-Host (
        (Get-CleanupText 'AssociatedNodes') -f
        $targetNodeArray.Count
    ) -ForegroundColor Green

    Write-Host (
        (Get-CleanupText 'AssociatedDrivers') -f
        $targetDrivers.Count
    ) -ForegroundColor Green

    Write-Host ''

    foreach ($driver in $targetDrivers) {

        $text =
            $driver.InfName

        if (
            -not [string]::IsNullOrWhiteSpace(
                $driver.OriginalName
            )
        ) {

            $text +=
                " / $($driver.OriginalName)"
        }

        if (
            -not [string]::IsNullOrWhiteSpace(
                $driver.Provider
            )
        ) {

            $text +=
                " / $($driver.Provider)"
        }

        Write-Host (
            "  $text"
        )
    }

    if ($PreviewOnly) {

        Write-Host ''
        Write-Host '========================================' `
            -ForegroundColor Green

        Write-Host (Get-CleanupText 'PreviewOnly') `
            -ForegroundColor Green

        Write-Host '========================================' `
            -ForegroundColor Green

        Stop-Cleanup 0
    }

    # ========================================================
    # Confirmation
    # ========================================================

    if ($targetNodeArray.Count -eq 0) {

        Write-Err (Get-CleanupText 'NoRemovableNodes')
        Stop-Cleanup 1
    }

    $baseId =
        $targetNodeArray[0]

    $tailLength =
        [Math]::Min(
            8,
            $baseId.Length
        )

    $confirmation =
        'DELETE-' +
        $baseId.Substring(
            $baseId.Length - $tailLength
        )

    Write-Host ''
    Write-Host (Get-CleanupText 'Irreversible') `
        -ForegroundColor Red

    Write-Host (
        (Get-CleanupText 'Confirm') -f
        $confirmation
    ) -ForegroundColor Yellow

    $typed =
        Read-Host

    if ($typed -cne $confirmation) {

        Write-Warn (Get-CleanupText 'ConfirmationMismatch')

        Stop-Cleanup 0
    }

    # ========================================================
    # Re-scan before deletion
    # ========================================================

    $cleanupMutex = [System.Threading.Mutex]::new($false, 'Global\iPhoneMirror.Driver.Operation')
    try { $cleanupLockTaken = $cleanupMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $cleanupLockTaken = $true }
    if (-not $cleanupLockTaken) { throw (Get-CleanupText 'DriverOperationBusy') }

    Write-Log (Get-CleanupText 'FinalDeviceCheck')

    Initialize-DeviceCache

    if (-not (Test-PhysicalDeviceConnected $selected)) {

        Write-Warn (Get-CleanupText 'SelectedDisconnected')

        Stop-Cleanup 3
    }

    # ========================================================
    # Log
    # ========================================================

    New-Item `
        -ItemType Directory `
        -Path $LogRoot `
        -Force |
        Out-Null

    $operationRoot =
        Join-Path `
            $LogRoot `
            (Get-Date -Format 'yyyyMMdd-HHmmss-fff')

    New-Item `
        -ItemType Directory `
        -Path $operationRoot `
        -Force |
        Out-Null

    $manifestPath =
        Join-Path `
            $operationRoot `
            'manifest.json'

    $logPath =
        Join-Path `
            $operationRoot `
            'cleanup.log'

    $script:CurrentLogPath =
        $logPath

    Save-Manifest `
        -Path $manifestPath `
        -Device $selected `
        -DeviceIds $targetNodeArray `
        -Drivers $targetDrivers

    $transcriptStarted = $false

    try {

        try {

            Start-Transcript `
                -LiteralPath $logPath `
                -Force |
                Out-Null

            $transcriptStarted = $true
        }
        catch {
        }

        Stop-iPhoneMirrorProcesses

        # ====================================================
        # Remove PnP nodes
        # ====================================================

        Write-Host ''
        Write-Host '========================================' `
            -ForegroundColor Cyan

        Write-Host (Get-CleanupText 'RemovingNodes') `
            -ForegroundColor Cyan

        Write-Host '========================================' `
            -ForegroundColor Cyan

        foreach ($id in $targetNodeArray) {

            if (Test-IsBthle $id) {
                continue
            }

            Remove-DeviceNode $id |
                Out-Null
        }

        # ====================================================
        # Remove Driver Store
        # ====================================================

        Write-Host ''
        Write-Host '========================================' `
            -ForegroundColor Cyan

        Write-Host (Get-CleanupText 'RemovingPackages') `
            -ForegroundColor Cyan

        Write-Host '========================================' `
            -ForegroundColor Cyan

        foreach ($driver in $targetDrivers) {

            Remove-DriverPackage `
                -InfName $driver.InfName -PhysicalDevice $selected |
                Out-Null
        }

        # ====================================================
        # Re-enumerate
        # ====================================================

        Write-Host ''

        Write-Log (Get-CleanupText 'WaitingWindows')

        Start-Sleep -Milliseconds 1500

        try {

            Invoke-PnpUtil @(
                '/scan-devices'
            ) | Out-Null
        }
        catch {
        }

        Start-Sleep -Milliseconds 1000

        # A surviving physical device can be re-enumerated with new interface
        # instance IDs. Remove those nodes too before declaring the cleanup done.
        Initialize-DeviceCache
        $reappeared = @(
            Get-ApplePhysicalDevices | Where-Object {
                @($_.ContainerIds | Where-Object {
                    $targetContainerIds.Contains($_)
                }).Count -gt 0 -or
                @($_.UsbIdentities | Where-Object {
                    $targetUsbIdentities.Contains($_)
                }).Count -gt 0
            }
        )
        foreach ($device in $reappeared) {
            foreach ($id in $device.InstanceIds) {
                if ($targetNodes.Add($id)) {
                    Remove-DeviceNode $id | Out-Null
                }
            }
        }
        $targetNodeArray = @($targetNodes | Sort-Object Length -Descending)

        # ====================================================
        # Final verification
        # ====================================================

        Write-Log (Get-CleanupText 'FinalVerification')

        Initialize-DeviceCache

        $remainingNodes =
            [System.Collections.Generic.List[string]]::new()

        foreach ($id in $targetNodeArray) {

            if (Test-IsBthle $id) {
                continue
            }

            if ($script:DeviceCache.ContainsKey($id)) {

                $remainingNodes.Add($id)
            }
        }

        # ----------------------------------------------------
        # Driver verification
        # ----------------------------------------------------

        $remainingDrivers =
            [System.Collections.Generic.List[string]]::new()

        foreach ($driver in $targetDrivers) {

            if ($script:PreservedDrivers.Contains($driver.InfName)) { continue }
            $infPath =
                Join-Path `
                    $env:windir `
                    "INF\$($driver.InfName)"

            if (Test-Path -LiteralPath $infPath) {

                if (-not $script:RestartPendingDrivers.Contains($driver.InfName)) {

                    $remainingDrivers.Add(
                        $driver.InfName
                    )
                }
            }
        }

        # ====================================================
        # Result
        # ====================================================

        Write-Host ''
        Write-Host '========================================' `
            -ForegroundColor Cyan

        Write-Host (Get-CleanupText 'CleanupResults') `
            -ForegroundColor Cyan

        Write-Host '========================================' `
            -ForegroundColor Cyan

        Write-Host ''

        if ($remainingNodes.Count -eq 0) {

            Write-OK (Get-CleanupText 'NodesRemoved')
        }
        else {
            $unresolvedNodes = @(
                $remainingNodes | Where-Object {
                    -not $script:RestartPendingNodes.Contains($_)
                }
            )
            if ($unresolvedNodes.Count -gt 0) {
                Add-Failure (Get-CleanupText 'UnresolvedNodes' -Values @($unresolvedNodes.Count))
            }

            Write-Warn (
                (Get-CleanupText 'RemainingNodes' -Values @($remainingNodes.Count))
            )

            foreach ($id in $remainingNodes) {

                Write-Host (
                    "  $id"
                ) -ForegroundColor Yellow
            }
        }

        if ($remainingDrivers.Count -eq 0) {

            if ($script:DriverInventorySupported) { Write-OK (Get-CleanupText 'PackagesHandled') }
            else { Write-Warn (Get-CleanupText 'PackagesPreservedUnsupported') }
        }
        else {
            $unresolvedDrivers = @(
                $remainingDrivers | Where-Object {
                    -not $script:RestartPendingDrivers.Contains($_)
                }
            )
            if ($unresolvedDrivers.Count -gt 0) {
                Add-Failure (Get-CleanupText 'UnresolvedPackages' -Values @($unresolvedDrivers.Count))
            }

            Write-Warn (
                (Get-CleanupText 'RemainingPackages' -Values @($remainingDrivers.Count))
            )

            foreach ($inf in $remainingDrivers) {

                Write-Host (
                    "  $inf"
                ) -ForegroundColor Yellow
            }
        }

        Write-Host ''

        if ($script:Failures.Count -eq 0) {

            Write-Host '========================================' `
                -ForegroundColor Green

            Write-Host (Get-CleanupText 'Completed') `
                -ForegroundColor Green

            Write-Host '========================================' `
                -ForegroundColor Green

            if ($script:RestartRequired) {

                Write-Host ''
                Write-Warn (
                    (Get-CleanupText 'RestartRequired')
                )
            }

            Write-Host ''
            Write-Host (
                (Get-CleanupText 'LogPath' -Values @($logPath))
            ) -ForegroundColor DarkGray

            Stop-Cleanup 0
        }

        Write-Host '========================================' `
            -ForegroundColor Red

        Write-Host (
            (Get-CleanupText 'CompletedWithErrors') -f
            $script:Failures.Count
        ) -ForegroundColor Red

        Write-Host '========================================' `
            -ForegroundColor Red

        foreach ($failure in $script:Failures) {

            Write-Host (
                "  $failure"
            ) -ForegroundColor Red
        }

        Write-Host ''
        Write-Host (
            (Get-CleanupText 'LogPath' -Values @($logPath))
        ) -ForegroundColor DarkGray

        Stop-Cleanup 1
    }
    finally {

        if ($transcriptStarted) {

            try {

                Stop-Transcript |
                    Out-Null
            }
            catch {
            }
        }
    }
}
catch {

    # ========================================================
    # NEVER hide the actual exception anymore.
    # ========================================================

    Write-Host ''
    Write-Host '========================================' `
        -ForegroundColor Red

    Write-Host (Get-CleanupText 'InternalError') `
        -ForegroundColor Red

    Write-Host '========================================' `
        -ForegroundColor Red

    Write-Host ''

    Write-Host (
        (Get-CleanupText 'Error') -f
        $_.Exception.Message
    ) -ForegroundColor Red

    Write-Host ''

    if (
        -not [string]::IsNullOrWhiteSpace(
            $_.InvocationInfo.PositionMessage
        )
    ) {

        Write-Host (Get-CleanupText 'Location') `
            -ForegroundColor Yellow

        Write-Host (
            $_.InvocationInfo.PositionMessage
        ) -ForegroundColor DarkGray
    }

    Write-Host ''

    if (
        -not [string]::IsNullOrWhiteSpace(
            $_.ScriptStackTrace
        )
    ) {

        Write-Host (Get-CleanupText 'StackTrace') `
            -ForegroundColor Yellow

        Write-Host (
            $_.ScriptStackTrace
        ) -ForegroundColor DarkGray
    }

    Write-Host ''

    # --------------------------------------------------------
    # Emergency log
    # --------------------------------------------------------

    try {

        New-Item `
            -ItemType Directory `
            -Path $LogRoot `
            -Force |
            Out-Null

        $errorLog =
            Join-Path `
                $LogRoot `
                (
                    'fatal-' +
                    (Get-Date -Format 'yyyyMMdd-HHmmss-fff') +
                    '.log'
                )

        @(
            (Get-CleanupText 'FatalError')
            ''
            ('Time: ' + (Get-Date))
            ('Message: ' + $_.Exception.Message)
            ''
            'Position:'
            $_.InvocationInfo.PositionMessage
            ''
            'Stack:'
            $_.ScriptStackTrace
        ) |
            Set-Content `
                -LiteralPath $errorLog `
                -Encoding UTF8

        Write-Host (
            (Get-CleanupText 'ErrorLog' -Values @($errorLog))
        ) -ForegroundColor DarkGray
    }
    catch {
    }

    Stop-Cleanup 1
}

finally {
    if ($cleanupLockTaken) { $cleanupMutex.ReleaseMutex() }
    if ($null -ne $cleanupMutex) { $cleanupMutex.Dispose() }
}
