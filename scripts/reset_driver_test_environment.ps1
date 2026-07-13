[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$Execute,
    [string]$Confirmation = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RequiredPhrase = 'RESET-IPHONE-MIRROR-DRIVERS'
$ExpectedInstallerHash = 'DF2ABF387893332F28C4DF68B10A6B176DC9706142055DCCCCF447F5A9CEDE2D'
$ExpectedDriverHash = '8058F2AFE6EF96A7D2DED432997FD8655970C9EA75A938EE4557D6A2CB4CC989'
$ExpectedDll64Hash = '4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14'
$ExpectedDll32Hash = '00CACA07869B19D10B370552AC7CC2F6F2EE246FC15DB11650F6CD3F4EF9B666'
$Root = Split-Path -Parent $PSScriptRoot
$Installer = Join-Path $Root 'src\DriverInstaller\Assets\libusb-win32-1.2.6.0\amd64\install-filter.exe'
$DataRoot = Join-Path $env:ProgramData 'iPhoneMirror.Driver\TestResets'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-UpperFilters([Microsoft.Win32.RegistryKey]$Key) {
    $value = $Key.GetValue('UpperFilters', $null,
        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -eq $value) { return @() }
    return @($value)
}

function Get-AppleFilterTargets {
    $usb = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        'SYSTEM\CurrentControlSet\Enum\USB', $false)
    if ($null -eq $usb) { return @() }
    try {
        $targets = @()
        foreach ($hardwareName in $usb.GetSubKeyNames()) {
            if ($hardwareName -notmatch '^VID_05AC&PID_[0-9A-Fa-f]{4}$') { continue }
            $hardware = $usb.OpenSubKey($hardwareName, $false)
            if ($null -eq $hardware) { continue }
            try {
                foreach ($instanceName in $hardware.GetSubKeyNames()) {
                    if ($instanceName -notmatch '^[A-Za-z0-9]+$') { continue }
                    $instance = $hardware.OpenSubKey($instanceName, $false)
                    if ($null -eq $instance) { continue }
                    try {
                        $filters = @(Get-UpperFilters $instance)
                        if ($filters -contains 'libusb0') {
                            $targets += [PSCustomObject]@{
                                InstanceId = "USB\$hardwareName\$instanceName"
                                RegistryPath = "SYSTEM\CurrentControlSet\Enum\USB\$hardwareName\$instanceName"
                                UpperFiltersExisted = $instance.GetValueNames() -contains 'UpperFilters'
                                UpperFilters = $filters
                            }
                        }
                    } finally { $instance.Dispose() }
                }
            } finally { $hardware.Dispose() }
        }
        return @($targets)
    } finally { $usb.Dispose() }
}

function Restore-Snapshots([object[]]$Snapshots) {
    foreach ($snapshot in $Snapshots) {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
            $snapshot.RegistryPath, $true)
        if ($null -eq $key) { throw "Cannot restore $($snapshot.InstanceId)." }
        try {
            if ($snapshot.UpperFiltersExisted) {
                $key.SetValue('UpperFilters', [string[]]$snapshot.UpperFilters,
                    [Microsoft.Win32.RegistryValueKind]::MultiString)
            } else {
                $key.DeleteValue('UpperFilters', $false)
            }
        } finally { $key.Dispose() }
    }
}

function Get-LibUsb0References {
    $references = @()
    foreach ($rootPath in @(
        'SYSTEM\CurrentControlSet\Enum\USB',
        'SYSTEM\CurrentControlSet\Control\Class')) {
        $rootKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($rootPath, $false)
        if ($null -eq $rootKey) { continue }
        try {
            $stack = [Collections.Generic.Stack[object]]::new()
            $stack.Push([PSCustomObject]@{ Key = $rootKey; Path = $rootPath; Owns = $false })
            while ($stack.Count -ne 0) {
                $entry = $stack.Pop()
                try {
                    $filters = @(Get-UpperFilters $entry.Key)
                    if ($filters -contains 'libusb0') { $references += $entry.Path }
                    foreach ($name in $entry.Key.GetSubKeyNames()) {
                        $child = $entry.Key.OpenSubKey($name, $false)
                        if ($null -ne $child) {
                            $stack.Push([PSCustomObject]@{
                                Key = $child
                                Path = "$($entry.Path)\$name"
                                Owns = $true
                            })
                        }
                    }
                } finally {
                    if ($entry.Owns) { $entry.Key.Dispose() }
                }
            }
        } finally { $rootKey.Dispose() }
    }
    return @($references | Sort-Object -Unique)
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) { throw "Hash mismatch: $Path" }
}

if (-not $Execute) {
    $targets = @(Get-AppleFilterTargets)
    Write-Host 'DRY RUN - no system changes were made.'
    Write-Host "Apple parent filters to remove: $($targets.Count)"
    $targets | ForEach-Object { Write-Host "  $($_.InstanceId)" }
    Write-Host "Run with -Execute and enter '$RequiredPhrase' to perform the reset."
    exit 0
}

if (-not (Test-Administrator)) {
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Execute', '-Confirmation', $Confirmation)
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}

if ([string]::IsNullOrWhiteSpace($Confirmation)) {
    $Confirmation = Read-Host "Type $RequiredPhrase to remove all iPhoneMirror capture filters"
}
if ($Confirmation -cne $RequiredPhrase) { throw 'Confirmation phrase did not match.' }

$running = @(Get-Process -Name 'iPhoneMirror','iPhoneMirror.Driver' -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) {
    throw 'Close iPhoneMirror and iPhoneMirror.Driver before resetting the driver environment.'
}

Assert-Hash $Installer $ExpectedInstallerHash
$backup = Join-Path $DataRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$logPath = Join-Path $backup 'reset.log'
Start-Transcript -LiteralPath $logPath -Force | Out-Null
$targets = @(Get-AppleFilterTargets)
$targets | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'filters.json') -Encoding UTF8
$serviceBackup = Join-Path $backup 'libusb0-service.reg'
$globalRemovalStarted = $false

$systemFiles = @(
    [PSCustomObject]@{ Path = "$env:WINDIR\System32\drivers\libusb0.sys"; Hash = $ExpectedDriverHash; BackupName = 'libusb0.sys' },
    [PSCustomObject]@{ Path = "$env:WINDIR\System32\libusb0.dll"; Hash = $ExpectedDll64Hash; BackupName = 'libusb0-system32.dll' },
    [PSCustomObject]@{ Path = "$env:WINDIR\SysWOW64\libusb0.dll"; Hash = $ExpectedDll32Hash; BackupName = 'libusb0-syswow64.dll' }
)
foreach ($file in $systemFiles) {
    if (Test-Path -LiteralPath $file.Path) {
        Assert-Hash $file.Path $file.Hash
        Copy-Item -LiteralPath $file.Path -Destination (Join-Path $backup $file.BackupName) -Force
    }
}

try {
    if ($PSCmdlet.ShouldProcess("$($targets.Count) Apple device(s)", 'Remove exact-device libusb0 filters')) {
        foreach ($target in $targets) {
            $output = & $Installer u "-di=$($target.InstanceId)" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "install-filter uninstall failed for $($target.InstanceId): $output"
            }
        }
    } else {
        Write-Host 'Operation cancelled by ShouldProcess.'
        exit 0
    }

    $remainingApple = @(Get-AppleFilterTargets)
    if ($remainingApple.Count -ne 0) {
        throw "Some Apple libusb0 filters remain: $($remainingApple.InstanceId -join ', ')"
    }
    $references = @(Get-LibUsb0References)
    if ($references.Count -ne 0) {
        Write-Warning 'Global libusb0 service was retained because other references still exist:'
        $references | ForEach-Object { Write-Warning "  $_" }
    } else {
        & "$env:WINDIR\System32\reg.exe" export 'HKLM\SYSTEM\CurrentControlSet\Services\libusb0' $serviceBackup /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to back up the libusb0 service registry key.' }
        $globalRemovalStarted = $true
        foreach ($file in $systemFiles) {
            if (Test-Path -LiteralPath $file.Path) { Assert-Hash $file.Path $file.Hash }
        }
        & "$env:WINDIR\System32\sc.exe" stop libusb0 | Out-Null
        & "$env:WINDIR\System32\sc.exe" delete libusb0 | Out-Null
        Start-Sleep -Milliseconds 500
        foreach ($file in $systemFiles) {
            if (Test-Path -LiteralPath $file.Path) {
                Remove-Item -LiteralPath $file.Path -Force
            }
        }
    }
    Write-Host "Driver reset completed. Backup: $backup"
    Write-Host 'Apple Devices / Apple Mobile Device Support was intentionally preserved.'
    Stop-Transcript | Out-Null
} catch {
    Write-Warning "Reset failed; restoring device filters from $backup"
    if ($globalRemovalStarted) {
        foreach ($file in $systemFiles) {
            $saved = Join-Path $backup $file.BackupName
            if (Test-Path -LiteralPath $saved) {
                Copy-Item -LiteralPath $saved -Destination $file.Path -Force
                Assert-Hash $file.Path $file.Hash
            }
        }
        if (Test-Path -LiteralPath $serviceBackup) {
            & "$env:WINDIR\System32\reg.exe" import $serviceBackup | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning 'Service registry restore failed.' }
        }
    }
    Restore-Snapshots $targets
    Stop-Transcript | Out-Null
    throw
}
