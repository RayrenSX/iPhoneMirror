[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$PlatformToolset,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ReceiverRoot = Join-Path $Root 'third_party\airplay-server'
$Commit = 'ff149b2e768bf9ae93199de941ab170571a941a4'
$Repository = 'https://github.com/xenos1337/AirPlayServer.git'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $Root 'build\airplay-server-source'
}

if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot '.git'))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SourceRoot) | Out-Null
    & git clone --filter=blob:none --no-checkout $Repository $SourceRoot
    if ($LASTEXITCODE -ne 0) { throw "Could not clone AirPlayServer: $LASTEXITCODE" }
    & git -C $SourceRoot sparse-checkout init --cone
    & git -C $SourceRoot sparse-checkout set airplay2dll AirPlayServerLib `
        external/ffmpeg external/plist
    & git -C $SourceRoot checkout --detach $Commit
    if ($LASTEXITCODE -ne 0) { throw "Could not check out AirPlayServer $Commit" }
}

$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Head = (& git -C $SourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $Head -ne $Commit) {
    throw "AirPlayServer source must be at $Commit; found $Head"
}
& (Join-Path $ReceiverRoot 'patches\Apply-DeviceMetadataPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-DisplayCapabilityPatch.ps1') `
    -SourceRoot $SourceRoot

$VsWhere = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $VsWhere)) { throw 'vswhere.exe is missing.' }
$Installation = (& $VsWhere -latest -products * -requires Microsoft.Component.MSBuild `
    -property installationPath | Select-Object -Last 1).Trim()
$MsBuild = Join-Path $Installation 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $MsBuild)) { throw 'MSBuild.exe is missing.' }

if ([string]::IsNullOrWhiteSpace($PlatformToolset)) {
    $VcMsBuildRoot = Join-Path $Installation 'MSBuild\Microsoft\VC'
    $PlatformToolset = Get-ChildItem $VcMsBuildRoot -Directory -Filter 'v*' `
        -ErrorAction SilentlyContinue |
        ForEach-Object {
            Get-ChildItem (Join-Path $_.FullName 'Platforms\x64\PlatformToolsets') `
                -Directory -ErrorAction SilentlyContinue
        } |
        Sort-Object Name -Descending -Unique |
        Select-Object -First 1 -ExpandProperty Name
}
if ([string]::IsNullOrWhiteSpace($PlatformToolset)) {
    throw 'No Visual C++ x64 platform toolset is installed.'
}

$OutputDirectory = Join-Path $SourceRoot 'x64\Release'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$Properties = @(
    '/m:1',
    '/nologo',
    '/verbosity:minimal',
    '/t:Rebuild',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    "/p:PlatformToolset=$PlatformToolset",
    "/p:SolutionDir=$SourceRoot\",
    "/p:OutDir=$OutputDirectory\"
)
$PreviousCl = $env:CL
try {
    $env:CL = (($PreviousCl, '/FS') | Where-Object { $_ }) -join ' '
    & $MsBuild (Join-Path $SourceRoot 'AirPlayServerLib\AirPlayLib.vcxproj') `
        @Properties
    if ($LASTEXITCODE -ne 0) { throw "AirPlayLib build failed: $LASTEXITCODE" }
    & $MsBuild (Join-Path $SourceRoot 'airplay2dll\airplay2dll.vcxproj') @Properties
    if ($LASTEXITCODE -ne 0) { throw "airplay2dll build failed: $LASTEXITCODE" }
}
finally {
    $env:CL = $PreviousCl
}

$Binary = Join-Path $SourceRoot 'x64\Release\airplay2dll.dll'
if (-not (Test-Path -LiteralPath $Binary)) {
    throw "Built AirPlay receiver is missing: $Binary"
}
$BinaryHex = [BitConverter]::ToString([IO.File]::ReadAllBytes($Binary)).Replace('-', '')
$TargetHeightAndRate = '6C73100E110B40103C5F102465306666'
$TargetWidth = '65393235111400130000001E5A7FFFF710015A41'
$LegacyHeightAndRate = '6C73100E1105A0101E5F102465306666'
$LegacyWidth = '65393235110D70130000001E5A7FFFF710015A41'
if (-not $BinaryHex.Contains($TargetHeightAndRate) -or
    -not $BinaryHex.Contains($TargetWidth) -or
    $BinaryHex.Contains($LegacyHeightAndRate) -or
    $BinaryHex.Contains($LegacyWidth)) {
    throw 'Built AirPlay receiver does not contain only the expected 5120x2880@60 display capability.'
}
Write-Host 'Verified AirPlay display capability: 5120x2880 @ 60fps.' -ForegroundColor Green
if ($Install) {
    Copy-Item -LiteralPath $Binary `
        -Destination (Join-Path $ReceiverRoot 'bin\x64\airplay2dll.dll') -Force
}
$Hash = (Get-FileHash -LiteralPath $Binary -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "AirPlay receiver: $Binary" -ForegroundColor Green
Write-Host "SHA256: $Hash" -ForegroundColor Green
if (-not $Install) {
    Write-Host 'Pass -Install to replace the vendored receiver binary.' -ForegroundColor Yellow
}
