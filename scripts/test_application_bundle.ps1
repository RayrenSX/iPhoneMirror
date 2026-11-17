param(
    [Parameter(Mandatory)][string]$Directory
)

$ErrorActionPreference = 'Stop'
$bundleDirectory = [IO.Path]::GetFullPath($Directory)
if (-not (Test-Path -LiteralPath $bundleDirectory -PathType Container)) {
    throw "Application bundle is missing: $bundleDirectory"
}

# App.csproj and DriverInstaller.csproj are separate publish targets. Check
# their combined delivery, including development builds, before handing it off.
$requiredFiles = @(
    'iPhoneMirror.exe', 'iPhoneMirror.Driver.exe', 'iPhoneMirror.Core.dll',
    'iPhoneMirror.UsbConfigurationSwitch.exe',
    'Wireless/iPhoneMirror.WirelessHost.exe',
    'Wireless/iPhoneMirror.AirPlayControl.dll',
    'Wireless/licenses/BUILD.json',
    'tools/iUsbBridge.exe', 'tools/iUsbBridge.runtime.json',
    'THIRD_PARTY_NOTICES.md',
    'licenses/WPF-UI-LICENSE.md', 'licenses/WPF-UI-ThirdPartyNotices.txt'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundleDirectory $relativePath) -PathType Leaf)) {
        throw "Application bundle is incomplete: $relativePath"
    }
}
. (Join-Path $PSScriptRoot 'ThirdPartyNoticeFiles.ps1')
$noticeWorkspace = Split-Path -Parent $PSScriptRoot
foreach ($notice in @(Get-AdditionalThirdPartyNoticeFiles $noticeWorkspace)) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundleDirectory $notice) -PathType Leaf)) {
        throw "Application bundle is missing a third-party notice/source: $notice"
    }
}

$appPath = Join-Path $bundleDirectory 'iPhoneMirror.exe'
$driverPath = Join-Path $bundleDirectory 'iPhoneMirror.Driver.exe'
$appVersion = (Get-Item -LiteralPath $appPath).VersionInfo.ProductVersion
$driverVersion = (Get-Item -LiteralPath $driverPath).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -cne $driverVersion) {
    throw "Main application and driver manager versions differ: $appVersion / $driverVersion"
}

. (Join-Path $PSScriptRoot 'UsbTouchBridgeRuntime.ps1')
Assert-UsbTouchBridgeRuntime -Directory (Join-Path $bundleDirectory 'tools') -Label 'Application bundle bridge'
$receiverManifest = Get-Content -LiteralPath (Join-Path $bundleDirectory 'Wireless/licenses/BUILD.json') -Raw | ConvertFrom-Json
if (@($receiverManifest.Runtime).Count -eq 0) { throw 'Receiver runtime manifest is empty.' }
foreach ($asset in $receiverManifest.Runtime) {
    $assetName = [string]$asset.Name
    if ([string]::IsNullOrWhiteSpace($assetName) -or
        [IO.Path]::GetFileName($assetName) -cne $assetName -or
        [string]$asset.Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Invalid receiver runtime manifest entry.'
    }
    $assetPath = Join-Path $bundleDirectory ('Wireless/' + $assetName)
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ine $asset.Sha256) {
        throw "Receiver runtime is missing or changed: $assetName"
    }
}

[pscustomobject]@{
    Directory = $bundleDirectory
    ProductVersion = $appVersion
    ApplicationSha256 = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToLowerInvariant()
    DriverManagerSha256 = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Result = 'passed'
}
