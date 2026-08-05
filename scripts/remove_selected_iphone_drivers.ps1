[CmdletBinding()]
param(
    [switch]$ListOnly,
    [switch]$PreviewOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PnpUtil = Join-Path ([Environment]::SystemDirectory) 'pnputil.exe'
$AppleUsbRoot = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB'
$AppleMobileProductPattern = '^VID_05AC&PID_(12A8|12AB)$'
$AppleParentPattern = '^USB\\VID_05AC&PID_[0-9A-Fa-f]{4}\\[A-Za-z0-9]{8,40}$'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-ElevatedCopy {
    $quotedScript = '"' + $PSCommandPath.Replace('"', '\"') + '"'
    $arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File $quotedScript"
    try {
        $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -Verb RunAs -ArgumentList $arguments -Wait -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Host '未获得管理员权限，操作已取消。' -ForegroundColor Red
        exit 5
    }
}

function Resolve-DisplayText([object]$Value, [string]$Fallback) {
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $Fallback }
    $separator = $text.LastIndexOf(';')
    if ($separator -ge 0 -and $separator + 1 -lt $text.Length) {
        $text = $text.Substring($separator + 1)
    }
    if ([string]::IsNullOrWhiteSpace($text)) { return $Fallback }
    return $text.Trim()
}

function Get-ObjectPropertyValue([object]$Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Invoke-PnpUtil([string[]]$Arguments) {
    $output = @(& $PnpUtil @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    return [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
        Text = $output -join [Environment]::NewLine
    }
}

function Convert-PnpCsv([string[]]$Lines) {
    if ($Lines.Count -lt 2) { return @() }
    $headerIndex = -1
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match ',') {
            $headerIndex = $index
            break
        }
    }
    if ($headerIndex -lt 0 -or $headerIndex + 1 -ge $Lines.Count) { return @() }
    $headers = 0..63 | ForEach-Object { "C$_" }
    return @(($Lines | Select-Object -Skip ($headerIndex + 1)) |
        ConvertFrom-Csv -Header $headers)
}

function Get-AppleDeviceGroups {
    if (-not (Test-Path -LiteralPath $AppleUsbRoot)) { return @() }
    $parents = @()
    foreach ($hardwareKey in @(Get-ChildItem -LiteralPath $AppleUsbRoot -ErrorAction SilentlyContinue)) {
        $hardwareName = $hardwareKey.PSChildName
        if ($hardwareName -notmatch '^VID_05AC&PID_[0-9A-Fa-f]{4}$') { continue }
        foreach ($instanceKey in @(Get-ChildItem -LiteralPath $hardwareKey.PSPath -ErrorAction SilentlyContinue)) {
            $instanceId = "USB\$hardwareName\$($instanceKey.PSChildName)"
            if ($instanceId -notmatch $AppleParentPattern) { continue }
            $properties = Get-ItemProperty -LiteralPath $instanceKey.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $properties) { continue }
            $fallback = 'Apple 移动设备'
            $name = Resolve-DisplayText (Get-ObjectPropertyValue $properties 'FriendlyName') ''
            if ([string]::IsNullOrWhiteSpace($name)) {
                $name = Resolve-DisplayText `
                    (Get-ObjectPropertyValue $properties 'DeviceDesc') $fallback
            }
            $looksLikeMobileDevice = $hardwareName -match $AppleMobileProductPattern -or
                $name -match '(?i)Apple (Mobile Device|iPhone|iPad)'
            if (-not $looksLikeMobileDevice) { continue }
            $containerId = [string](Get-ObjectPropertyValue $properties 'ContainerID')
            $parents += [PSCustomObject]@{
                Serial = $instanceKey.PSChildName.ToUpperInvariant()
                Name = $name
                InstanceId = $instanceId
                ContainerId = $containerId
                Service = [string](Get-ObjectPropertyValue $properties 'Service')
            }
        }
    }

    $groups = foreach ($group in @($parents | Group-Object Serial)) {
        $roots = @($group.Group)
        [PSCustomObject]@{
            Serial = $group.Name
            Name = $roots[0].Name
            Roots = $roots
            Present = @($roots | Where-Object { Test-DevicePresent $_.InstanceId }).Count -ne 0
        }
    }
    return @($groups | Sort-Object @{ Expression = 'Present'; Descending = $true }, Name, Serial)
}

function Test-DevicePresent([string]$InstanceId) {
    $result = Invoke-PnpUtil @('/enum-devices', '/connected', '/instanceid', $InstanceId,
        '/format', 'csv')
    if ($result.ExitCode -ne 0) { return $false }
    $rows = @(Convert-PnpCsv $result.Output)
    return @($rows | Where-Object { $_.C0 -eq $InstanceId }).Count -ne 0
}

function Get-ContainerDeviceIds([string]$ContainerId) {
    if ([string]::IsNullOrWhiteSpace($ContainerId)) { return @() }
    $result = Invoke-PnpUtil @('/enum-containers', '/containerid', $ContainerId,
        '/devices', '/format', 'csv')
    if ($result.ExitCode -ne 0) { return @() }
    $rows = @(Convert-PnpCsv $result.Output)
    return @($rows | ForEach-Object { [string]$_.C7 } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-MatchingOemInfs([string]$InstanceId) {
    $result = Invoke-PnpUtil @('/enum-devices', '/instanceid', $InstanceId,
        '/drivers', '/format', 'csv')
    if ($result.ExitCode -ne 0) { return @() }
    $matches = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $result.Output) {
        foreach ($match in [regex]::Matches($line, '(?i)\boem\d+\.inf\b')) {
            [void]$matches.Add($match.Value.ToLowerInvariant())
        }
    }
    return @($matches | Sort-Object)
}

function Get-DriverPackageInventory([string[]]$InfNames, [string[]]$TargetDeviceIds) {
    if ($InfNames.Count -eq 0) { return @() }
    $result = Invoke-PnpUtil @('/enum-drivers', '/devices', '/format', 'csv')
    $rows = if ($result.ExitCode -eq 0) { @(Convert-PnpCsv $result.Output) } else { @() }
    $targetSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($deviceId in $TargetDeviceIds) { [void]$targetSet.Add($deviceId) }

    $inventory = foreach ($infName in $InfNames) {
        $packageRows = @($rows | Where-Object { $_.C0 -eq $infName })
        $metadata = $packageRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.C1) } |
            Select-Object -First 1
        $otherDevices = @($packageRows | ForEach-Object { [string]$_.C11 } |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and -not $targetSet.Contains($_)
            } | Sort-Object -Unique)
        [PSCustomObject]@{
            InfName = $infName
            OriginalName = if ($null -ne $metadata) { [string]$metadata.C1 } else { '' }
            Provider = if ($null -ne $metadata) { [string]$metadata.C2 } else { '' }
            OtherDevices = $otherDevices
        }
    }
    return @($inventory)
}

function Get-DeviceIdsForGroup([object]$Device) {
    $deviceIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $Device.Roots) {
        [void]$deviceIds.Add($root.InstanceId)
        foreach ($deviceId in @(Get-ContainerDeviceIds $root.ContainerId)) {
            [void]$deviceIds.Add($deviceId)
        }
    }
    return @($deviceIds | Sort-Object)
}

function Get-ConnectedDeviceBySerial([string]$Serial) {
    return Get-AppleDeviceGroups | Where-Object {
        $_.Present -and $_.Serial -eq $Serial
    } | Select-Object -First 1
}

if (-not $ListOnly -and -not $PreviewOnly -and -not (Test-Administrator)) {
    Start-ElevatedCopy
}
if (-not (Test-Path -LiteralPath $PnpUtil -PathType Leaf)) {
    throw "找不到 Windows 驱动工具：$PnpUtil"
}

try {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    [Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
}
catch { }

Clear-Host
Write-Host 'iPhone/iPad 设备驱动彻底卸载' -ForegroundColor Cyan
Write-Host '--------------------------------'
Write-Host '只有当前通过 USB 连接的 iPhone/iPad 才能执行卸载。' -ForegroundColor Yellow
Write-Host '会删除所选设备的 PnP 节点，以及所有匹配到的第三方 oem*.inf 驱动包。'
Write-Host 'Apple 驱动包通常由多台 Apple 设备共用，删除后其他 iPhone/iPad 也可能需要重装驱动。' `
    -ForegroundColor Yellow
Write-Host 'Windows 自带驱动文件、Apple Devices/iTunes 和 Apple Mobile Device Service 不会被删除。'
Write-Host ''

$devices = @(Get-AppleDeviceGroups | Where-Object { $_.Present })
if ($devices.Count -eq 0) {
    Write-Host '没有找到当前已连接的 iPhone/iPad。请连接、解锁设备后重新运行。' `
        -ForegroundColor Yellow
    exit 2
}

for ($index = 0; $index -lt $devices.Count; $index++) {
    $device = $devices[$index]
    Write-Host ('[{0}] {1}  当前已连接' -f ($index + 1), $device.Name) `
        -ForegroundColor White
    Write-Host ('    序列号：{0}' -f $device.Serial) -ForegroundColor DarkGray
}

if ($ListOnly) { exit 0 }

$selectedIndex = -1
while ($selectedIndex -lt 0) {
    $answer = Read-Host '请输入要清除的设备序号；输入 Q 取消'
    if ($answer -match '^(?i)q$') { exit 0 }
    $number = 0
    if ([int]::TryParse($answer, [ref]$number) -and
        $number -ge 1 -and $number -le $devices.Count) {
        $selectedIndex = $number - 1
    }
    else {
        Write-Host '序号无效，请重新输入。' -ForegroundColor Yellow
    }
}

$selected = $devices[$selectedIndex]
$targetDeviceIds = @(Get-DeviceIdsForGroup $selected)

$infSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($deviceId in $targetDeviceIds) {
    foreach ($infName in @(Get-MatchingOemInfs $deviceId)) { [void]$infSet.Add($infName) }
}
$infNames = @($infSet | Sort-Object)
$packages = @(Get-DriverPackageInventory $infNames $targetDeviceIds)

Write-Host ''
Write-Host ('已选择：{0} / {1}' -f $selected.Name, $selected.Serial) -ForegroundColor Cyan
Write-Host ('将移除设备节点：{0} 个' -f $targetDeviceIds.Count)
foreach ($deviceId in $targetDeviceIds) { Write-Host "  $deviceId" -ForegroundColor DarkGray }
Write-Host ('将删除第三方驱动包：{0} 个' -f $packages.Count)
foreach ($package in $packages) {
    $description = @($package.OriginalName, $package.Provider) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    Write-Host ('  {0}  {1}' -f $package.InfName, ($description -join ' / '))
    if ($package.OtherDevices.Count -ne 0) {
        Write-Warning ("    该 INF 还用于其他 {0} 个设备，删除会同时卸载它们的这个驱动。" -f `
            $package.OtherDevices.Count)
    }
}
if ($packages.Count -eq 0) {
    Write-Host '  未发现可从 Driver Store 删除的第三方 INF；仍会清除设备节点和绑定。' `
        -ForegroundColor DarkGray
}

if ($PreviewOnly) {
    Write-Host ''
    Write-Host '仅预览：未做任何系统更改。' -ForegroundColor Green
    exit 0
}

$tailLength = [Math]::Min(8, $selected.Serial.Length)
$serialTail = $selected.Serial.Substring($selected.Serial.Length - $tailLength)
$confirmation = "DELETE-$serialTail"
Write-Host ''
Write-Host '这是不可撤销操作。请保持手机连接；Windows 可能在卸载后把它显示为未知设备。' `
    -ForegroundColor Red
$typed = Read-Host "请输入 $confirmation 确认"
if ($typed -cne $confirmation) {
    Write-Host '确认文字不匹配，未做任何更改。' -ForegroundColor Yellow
    exit 0
}

$connectedSelection = Get-ConnectedDeviceBySerial $selected.Serial
if ($null -eq $connectedSelection) {
    Write-Host '所选手机已断开。只有手机保持连接时才允许卸载，未做任何更改。' `
        -ForegroundColor Yellow
    exit 3
}
$selected = $connectedSelection
$targetDeviceIds = @(Get-DeviceIdsForGroup $selected)

$running = @(Get-Process -Name 'iPhoneMirror', 'iPhoneMirror.Driver' `
    -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) {
    Write-Host '正在关闭 iPhoneMirror 驱动相关进程...'
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$logRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) `
    'iPhoneMirror.Driver\DeviceCleanup'
$operationRoot = Join-Path $logRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
New-Item -ItemType Directory -Path $operationRoot -Force | Out-Null
$manifestPath = Join-Path $operationRoot 'manifest.json'
$logPath = Join-Path $operationRoot 'cleanup.log'
[PSCustomObject]@{
    CreatedAt = [DateTimeOffset]::Now
    DeviceName = $selected.Name
    Serial = $selected.Serial
    RootDevices = $selected.Roots
    DeviceIds = $targetDeviceIds
    DriverPackages = $packages
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$transcriptStarted = $false
$failures = [Collections.Generic.List[string]]::new()
try {
    Start-Transcript -LiteralPath $logPath -Force | Out-Null
    $transcriptStarted = $true
    Write-Host ''
    Write-Host '开始移除设备节点...'
    foreach ($deviceId in @($targetDeviceIds | Sort-Object Length -Descending)) {
        $result = Invoke-PnpUtil @('/remove-device', $deviceId, '/subtree', '/force')
        if ($result.ExitCode -eq 0) {
            Write-Host "  已移除：$deviceId" -ForegroundColor Green
        }
        else {
            $message = "设备节点移除失败：$deviceId (代码 $($result.ExitCode))"
            $failures.Add($message)
            Write-Warning $message
            if (-not [string]::IsNullOrWhiteSpace($result.Text)) { Write-Host $result.Text }
        }
    }

    Write-Host '开始删除 Driver Store 中的第三方驱动包...'
    foreach ($package in $packages) {
        $result = Invoke-PnpUtil @('/delete-driver', $package.InfName,
            '/uninstall', '/force')
        if ($result.ExitCode -eq 0) {
            Write-Host "  已删除：$($package.InfName)" -ForegroundColor Green
        }
        else {
            $message = "驱动包删除失败：$($package.InfName) (代码 $($result.ExitCode))"
            $failures.Add($message)
            Write-Warning $message
            if (-not [string]::IsNullOrWhiteSpace($result.Text)) { Write-Host $result.Text }
        }
    }

    Start-Sleep -Seconds 2
    $reenumerated = Get-ConnectedDeviceBySerial $selected.Serial
    if ($null -ne $reenumerated) {
        $reenumeratedIds = @(Get-DeviceIdsForGroup $reenumerated)
        if ($reenumeratedIds.Count -ne 0) {
            Write-Host '手机仍连接，正在清理 Windows 重新枚举出的设备节点...'
            foreach ($deviceId in @($reenumeratedIds | Sort-Object Length -Descending)) {
                $result = Invoke-PnpUtil @('/remove-device', $deviceId, '/subtree', '/force')
                if ($result.ExitCode -eq 0) {
                    Write-Host "  已再次移除：$deviceId" -ForegroundColor Green
                }
                else {
                    Write-Warning "重新枚举节点未能移除：$deviceId (代码 $($result.ExitCode))"
                }
            }
        }
    }

    foreach ($package in $packages) {
        if (Test-Path -LiteralPath (Join-Path $env:windir "INF\$($package.InfName)")) {
            $failures.Add("驱动包仍存在：$($package.InfName)")
        }
    }

    Write-Host ''
    if ($failures.Count -eq 0) {
        Write-Host '完成：所选设备使用的第三方驱动包均已清除。' -ForegroundColor Green
        Write-Host '手机保持连接时，Windows 仍可能显示无驱动或未知设备节点，这是正常现象。'
        Write-Host "日志：$logPath" -ForegroundColor DarkGray
        exit 0
    }

    Write-Host ('操作结束，但有 {0} 项未完全清除：' -f $failures.Count) -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  $failure" -ForegroundColor Red }
    Write-Host '请重启 Windows 后再次运行本脚本。'
    Write-Host "日志：$logPath" -ForegroundColor DarkGray
    exit 1
}
finally {
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
