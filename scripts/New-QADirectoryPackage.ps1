[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$DriverDirectory,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DirectoryPackage.ps1')
. (Join-Path $PSScriptRoot 'UsbTouchBridgeRuntime.ps1')
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$DriverDirectory = (Resolve-Path -LiteralPath $DriverDirectory).Path
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw "Destination already exists: $Destination" }
foreach ($source in @($ApplicationDirectory, $DriverDirectory)) {
    if ($Destination.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Destination must be outside the input directories.'
    }
}
Assert-DirectoryPackageInputs $ApplicationDirectory $DriverDirectory
Assert-UsbTouchBridgeRuntime -Directory (Join-Path $ApplicationDirectory 'tools') -Label 'QA directory bridge'
$package = Join-Path $Destination 'package'
New-Item -ItemType Directory -Path $package | Out-Null
Get-ChildItem -LiteralPath $ApplicationDirectory -Force | Copy-Item -Destination $package -Recurse -Force
# The driver publish is self-contained; its embedded elevation payload must
# stay intact. Shared notices/licenses already in the app must agree.
foreach ($relative in @('THIRD_PARTY_NOTICES.md', 'licenses\WPF-UI-LICENSE.md', 'licenses\WPF-UI-ThirdPartyNotices.txt')) {
    $source = Join-Path $DriverDirectory $relative
    $target = Join-Path $package $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Driver notice is missing: $relative" }
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Driver/app notice mismatch: $relative"
        }
    } else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
}
Copy-Item -LiteralPath (Join-Path $DriverDirectory 'iPhoneMirror.Driver.exe') -Destination $package -Force
Assert-DirectoryPackageInputs $package $package
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($package, $_.FullName); length=$_.Length;
        sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[ordered]@{schema=1; applicationInput=$ApplicationDirectory; driverInput=$DriverDirectory;
    packagePath=$package; driverIntegrated=$true; cameraPayloadsMatch=$true; files=$files} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding utf8
Write-Output $package
