[CmdletBinding()]
param(
    [switch]$ListOnly,
    [switch]$PreviewOnly,
    [switch]$NoPause,
    [int]$ExcludeProcessId = 0,
    [int]$ExcludeParentProcessId = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
            [void](Read-Host '按 Enter 键关闭窗口')
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

    Write-Log '扫描当前 PnP 设备...'

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
                "PnP 缓存完成：$($script:DeviceCache.Count) 个设备"
            )

            return
        }
        catch {

            Write-Warn (
                'Get-PnpDevice 扫描失败，切换到 pnputil：' +
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
            "无法枚举 PnP 设备，pnputil ExitCode=$($result.ExitCode)"
        )
    }

    if ([string]::IsNullOrWhiteSpace($result.Text)) {
        throw 'pnputil PnP XML 为空，无法枚举设备。'
    }

    try {
        [xml]$xml = $result.Text
    }
    catch {
        throw (
            'pnputil PnP XML 解析失败：' +
            $_.Exception.Message
        )
    }

    $containerNodes =
        @($xml.SelectNodes('//*[local-name()="Container"]'))

    if ($containerNodes.Count -eq 0) {
        Write-Warn 'pnputil PnP XML 中没有当前连接的设备。'
        Write-OK 'pnputil PnP 缓存完成：0 个设备'
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
        Write-Warn 'pnputil PnP XML 中没有可用设备。'
    }

    Write-OK (
        "pnputil PnP 缓存完成：$($script:DeviceCache.Count) 个设备"
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

function Get-DriverStoreInventory {

    if ($script:DriverInventoryInitialized) {

        return @(
            $script:DriverInventory.Values
        )
    }

    Write-Log '建立 Driver Store 驱动索引...'

    $result =
        Invoke-PnpUtil @(
            '/enum-drivers'
            '/devices'
            '/format'
            'xml'
        )

    if ($result.ExitCode -ne 0) {

        Write-Warn (
            "无法使用 Driver Store XML，ExitCode=$($result.ExitCode)"
        )

        throw (
            "pnputil Driver Store 枚举失败，ExitCode=$($result.ExitCode)；" +
            '已停止清理以避免遗漏驱动包。'
        )
    }

    $xmlText =
        $result.Text

    if ([string]::IsNullOrWhiteSpace($xmlText)) {

        Write-Warn 'Driver Store XML 为空。'

        throw 'Driver Store XML 为空，已停止清理以避免遗漏驱动包。'
    }

    try {

        [xml]$xml =
            $xmlText
    }
    catch {

        Write-Warn (
            'Driver Store XML 解析失败：' +
            $_.Exception.Message
        )

        throw (
            'Driver Store XML 解析失败，已停止清理：' +
            $_.Exception.Message
        )
    }

    # Each Driver node owns its Devices collection. Never scan
    # the complete XML for IDs, or unrelated packages could be
    # associated with the selected Apple device.
    $driverNodes =
        @($xml.SelectNodes('//*[local-name()="Driver"]'))

    if ($driverNodes.Count -eq 0) {
        throw 'Driver Store XML 中没有 Driver 节点，已停止清理以避免遗漏驱动包。'
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
                [string]::IsNullOrWhiteSpace($id) -or
                (Test-IsBthle $id)
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
        "Driver Store 索引完成：$($script:DriverInventory.Count) 个 OEM INF"
    )

    return @(
        $script:DriverInventory.Values
    )
}

# ============================================================
# Find drivers for one physical device
# ============================================================

function Get-DriversForPhysicalDevice {
    param(
        [object]$PhysicalDevice
    )

    $inventory =
        @(Get-DriverStoreInventory)

    if ($inventory.Count -eq 0) {
        return @()
    }

    $targetIds =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )

    foreach ($id in $PhysicalDevice.InstanceIds) {

        if (
            -not (Test-IsBthle $id) -and
            -not [string]::IsNullOrWhiteSpace($id)
        ) {

            [void]$targetIds.Add($id)
        }
    }

    $matched =
        [System.Collections.Generic.List[object]]::new()

    foreach ($driver in $inventory) {

        $isMatch = $false

        foreach ($driverDeviceId in $driver.Devices) {

            if (Test-IsBthle $driverDeviceId) {
                continue
            }

            if ($targetIds.Contains($driverDeviceId)) {

                $isMatch = $true
                break
            }

            # Normalize interface IDs.

            $normalizedDriverId =
                Get-NormalizedUsbIdentity `
                    $driverDeviceId

            if (-not [string]::IsNullOrWhiteSpace($normalizedDriverId)) {

                foreach ($targetId in $targetIds) {

                    $normalizedTargetId =
                        Get-NormalizedUsbIdentity `
                            $targetId

                    if (
                        -not [string]::IsNullOrWhiteSpace($normalizedTargetId) -and
                        $normalizedDriverId -ieq
                        $normalizedTargetId
                    ) {

                        $isMatch = $true
                        break
                    }
                }
            }

            if ($isMatch) {
                break
            }
        }

        if ($isMatch) {

            $matched.Add($driver)
        }
    }

    return @(
        $matched |
            Sort-Object InfName -Unique
    )
}

# ============================================================
# Stop iPhoneMirror
# ============================================================

function Stop-iPhoneMirrorProcesses {

    $processNames = @(
        'iPhoneMirror'
        'iPhoneMirror.Driver'
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

    Write-Log '关闭 iPhoneMirror 相关进程...'

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
            "安全过滤 BTHLE：$InstanceId"
        )

        return $true
    }

    $result =
        Invoke-PnpUtil @(
            '/remove-device'
            $InstanceId
            '/subtree'
            '/force'
        )

    if ($result.ExitCode -eq 0) {

        Write-OK (
            "设备节点：$InstanceId"
        )

        return $true
    }

    if ($result.ExitCode -eq 3010) {

        $script:RestartRequired = $true
        [void]$script:RestartPendingNodes.Add($InstanceId)

        Write-OK (
            "设备节点已移除，重启后完成：$InstanceId"
        )

        return $true
    }

    Write-Err (
        "设备节点删除失败：$InstanceId " +
        "(ExitCode=$($result.ExitCode))"
    )

    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {

        Write-Host $result.Text `
            -ForegroundColor DarkGray
    }

    Add-Failure (
        "设备节点删除失败：$InstanceId"
    )

    return $false
}

# ============================================================
# Remove driver package
# ============================================================

function Remove-DriverPackage {
    param(
        [string]$InfName
    )

    if (
        [string]::IsNullOrWhiteSpace($InfName) -or
        $InfName -notmatch '(?i)^oem\d+\.inf$'
    ) {

        return $false
    }

    $result =
        Invoke-PnpUtil @(
            '/delete-driver'
            $InfName
            '/uninstall'
            '/force'
        )

    if ($result.ExitCode -eq 0) {

        Write-OK (
            "驱动包：$InfName"
        )

        return $true
    }

    if ($result.ExitCode -eq 3010) {

        $script:RestartRequired = $true
        [void]$script:RestartPendingDrivers.Add($InfName)

        Write-OK (
            "驱动包已删除，重启后完成：$InfName"
        )

        return $true
    }

    Write-Err (
        "驱动包删除失败：$InfName " +
        "(ExitCode=$($result.ExitCode))"
    )

    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {

        Write-Host $result.Text `
            -ForegroundColor DarkGray
    }

    Add-Failure (
        "驱动包删除失败：$InfName"
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
            '无法写入 manifest：' +
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
        Write-Err '请通过 iPhoneMirror.Driver.exe 或发布包中的清理入口运行此工具，以获得受保护的管理员权限。'
        Stop-Cleanup 5
    }
}

if (-not (Test-Path -LiteralPath $PnpUtil -PathType Leaf)) {

    Write-Err (
        "找不到 pnputil.exe：$PnpUtil"
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

Write-Host ' iPhone/iPad Remove All Drivers' `
    -ForegroundColor Cyan

Write-Host '========================================' `
    -ForegroundColor Cyan

Write-Host ''
Write-Host '将清除所选设备关联的所有设备节点和驱动包，包括 Apple 官方驱动。' `
    -ForegroundColor Yellow
Write-Host '完成后可重新安装 Apple Devices 或 iTunes 以恢复所需驱动。' `
    -ForegroundColor Yellow
Write-Host ''

try {

    # ========================================================
    # Scan
    # ========================================================

    Write-Log '开始设备扫描...'

    Initialize-DeviceCache

    Write-Log '建立 Apple 设备关系...'

    $physicalDevices =
        @(Get-ApplePhysicalDevices)

    Write-OK (
        "Apple 物理设备分组完成：$($physicalDevices.Count) 台"
    )

    if ($physicalDevices.Count -eq 0) {

        Write-Host ''
        Write-Warn '没有找到当前连接的 iPhone/iPad。'
        Write-Host ''
        Write-Host '请确认：'
        Write-Host '  1. iPhone/iPad 已通过 USB 连接'
        Write-Host '  2. 设备已解锁'
        Write-Host '  3. 已点击“信任此电脑”'
        Write-Host '  4. 数据线支持数据传输'
        Write-Host ''
        Write-Host 'BTHLE / Bluetooth LE 设备不会显示。' `
            -ForegroundColor DarkGray

        Stop-Cleanup 2
    }

    # ========================================================
    # Driver inventory
    # ========================================================

    Write-Log '建立驱动关系...'

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

    Write-OK '设备关系建立完成。'

    # ========================================================
    # Display
    # ========================================================

    Write-Host ''
    Write-Host '检测到以下物理 Apple 设备：' `
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
            '    PnP 节点：{0}' -f
            $device.InstanceIds.Count
        ) -ForegroundColor DarkGray

        Write-Host (
            '    Driver Store 驱动包：{0}' -f
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

        Write-OK '仅列表模式，未修改系统。'

        Stop-Cleanup 0
    }

    # ========================================================
    # Select
    # ========================================================

    $selectedIndex = -1

    while ($selectedIndex -lt 0) {

        $answer =
            Read-Host '请输入设备序号；输入 Q 取消'

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

            Write-Warn '设备序号无效。'
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

    Write-Host ' 清理计划' `
        -ForegroundColor Cyan

    Write-Host '========================================' `
        -ForegroundColor Cyan

    Write-Host ''

    Write-Host (
        '设备：{0}' -f
        $selected.Name
    )

    Write-Host (
        '物理设备：1 台'
    ) -ForegroundColor Green

    Write-Host (
        '关联 PnP 节点：{0}' -f
        $targetNodeArray.Count
    ) -ForegroundColor Green

    Write-Host (
        '关联全部驱动包：{0}' -f
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

        Write-Host ' Preview only - 未修改系统' `
            -ForegroundColor Green

        Write-Host '========================================' `
            -ForegroundColor Green

        Stop-Cleanup 0
    }

    # ========================================================
    # Confirmation
    # ========================================================

    if ($targetNodeArray.Count -eq 0) {

        Write-Err '没有可删除的目标 PnP 节点。'
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
    Write-Host '这是不可撤销的操作。' `
        -ForegroundColor Red

    Write-Host (
        '请输入 {0} 确认：' -f
        $confirmation
    ) -ForegroundColor Yellow

    $typed =
        Read-Host

    if ($typed -cne $confirmation) {

        Write-Warn '确认文字不匹配，未做任何修改。'

        Stop-Cleanup 0
    }

    # ========================================================
    # Re-scan before deletion
    # ========================================================

    Write-Log '执行删除前最终设备确认...'

    Initialize-DeviceCache

    if (-not (Test-PhysicalDeviceConnected $selected)) {

        Write-Warn '所选 iPhone 已断开。'

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

        Write-Host ' 卸载设备节点' `
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

        Write-Host ' 删除 Driver Store 驱动包' `
            -ForegroundColor Cyan

        Write-Host '========================================' `
            -ForegroundColor Cyan

        foreach ($driver in $targetDrivers) {

            Remove-DriverPackage `
                $driver.InfName |
                Out-Null
        }

        # ====================================================
        # Re-enumerate
        # ====================================================

        Write-Host ''

        Write-Log '等待 Windows 更新设备状态...'

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

        Write-Log '执行最终验证...'

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

        Write-Host ' 清理结果' `
            -ForegroundColor Cyan

        Write-Host '========================================' `
            -ForegroundColor Cyan

        Write-Host ''

        if ($remainingNodes.Count -eq 0) {

            Write-OK '目标 PnP 节点已清理。'
        }
        else {
            $unresolvedNodes = @(
                $remainingNodes | Where-Object {
                    -not $script:RestartPendingNodes.Contains($_)
                }
            )
            if ($unresolvedNodes.Count -gt 0) {
                Add-Failure "仍存在 $($unresolvedNodes.Count) 个目标 PnP 节点。"
            }

            Write-Warn (
                "仍存在 $($remainingNodes.Count) 个目标节点。"
            )

            foreach ($id in $remainingNodes) {

                Write-Host (
                    "  $id"
                ) -ForegroundColor Yellow
            }
        }

        if ($remainingDrivers.Count -eq 0) {

            Write-OK '目标 Driver Store 驱动包已处理。'
        }
        else {
            $unresolvedDrivers = @(
                $remainingDrivers | Where-Object {
                    -not $script:RestartPendingDrivers.Contains($_)
                }
            )
            if ($unresolvedDrivers.Count -gt 0) {
                Add-Failure "仍存在 $($unresolvedDrivers.Count) 个目标 Driver Store 驱动包。"
            }

            Write-Warn (
                "仍存在 $($remainingDrivers.Count) 个驱动包。"
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

            Write-Host ' Cleanup completed successfully.' `
                -ForegroundColor Green

            Write-Host '========================================' `
                -ForegroundColor Green

            if ($script:RestartRequired) {

                Write-Host ''
                Write-Warn (
                    'Windows 报告部分操作需要重启。'
                )
            }

            Write-Host ''
            Write-Host (
                "日志：$logPath"
            ) -ForegroundColor DarkGray

            Stop-Cleanup 0
        }

        Write-Host '========================================' `
            -ForegroundColor Red

        Write-Host (
            ' Cleanup finished with errors. Count: {0}' -f
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
            "日志：$logPath"
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

    Write-Host ' CLEANUP INTERNAL ERROR' `
        -ForegroundColor Red

    Write-Host '========================================' `
        -ForegroundColor Red

    Write-Host ''

    Write-Host (
        '错误：{0}' -f
        $_.Exception.Message
    ) -ForegroundColor Red

    Write-Host ''

    if (
        -not [string]::IsNullOrWhiteSpace(
            $_.InvocationInfo.PositionMessage
        )
    ) {

        Write-Host '位置：' `
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

        Write-Host '调用栈：' `
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
            'iPhoneMirror Driver Cleanup Fatal Error'
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
            "错误日志：$errorLog"
        ) -ForegroundColor DarkGray
    }
    catch {
    }

    Stop-Cleanup 1
}
