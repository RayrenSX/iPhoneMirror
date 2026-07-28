[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Manifest = Import-PowerShellDataFile -LiteralPath `
    (Join-Path $Root 'scripts\inno-runtime-manifest.psd1')
$ToolRoot = Join-Path $Root 'work\tools\inno-setup'
$Compiler = Join-Path $ToolRoot 'ISCC.exe'
$CacheRoot = Join-Path $Root 'work\cache'
$Installer = Join-Path $CacheRoot "innosetup-$($Manifest.Version).exe"
$LanguageDirectory = Join-Path $ToolRoot 'Languages'
$ChineseSimplified = Join-Path $LanguageDirectory 'ChineseSimplified.isl'
$ChineseSimplifiedCache = Join-Path $CacheRoot `
    "ChineseSimplified-is-$($Manifest.Version).isl"

function Assert-FileHash([string]$Path, [string]$ExpectedHash, [string]$Description) {
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) {
        throw "$Description hash mismatch: expected $ExpectedHash, got $actualHash"
    }
}

if ((Test-Path -LiteralPath $Compiler -PathType Leaf) -and
    (Test-Path -LiteralPath $ChineseSimplified -PathType Leaf) -and
    -not $Force) {
    Assert-FileHash $ChineseSimplified $Manifest.ChineseSimplified.Sha256 `
        'Inno Setup Simplified Chinese translation'
    Write-Output $Compiler
    return
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf) -or $Force) {
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf) -or $Force) {
        Invoke-WebRequest -Uri $Manifest.DownloadUrl -OutFile $Installer -UseBasicParsing
    }
    Assert-FileHash $Installer $Manifest.Sha256 'Inno Setup installer'

    New-Item -ItemType Directory -Force -Path $ToolRoot | Out-Null
    $arguments = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS',
        "/DIR=$ToolRoot"
    )
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru `
        -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup compiler installation failed: $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
        throw "Inno Setup compiler was not installed at the expected path: $Compiler"
    }
}

if (-not (Test-Path -LiteralPath $ChineseSimplifiedCache -PathType Leaf) -or $Force) {
    Invoke-WebRequest -Uri $Manifest.ChineseSimplified.DownloadUrl `
        -OutFile $ChineseSimplifiedCache -UseBasicParsing
}
Assert-FileHash $ChineseSimplifiedCache $Manifest.ChineseSimplified.Sha256 `
    'Inno Setup Simplified Chinese translation'
New-Item -ItemType Directory -Force -Path $LanguageDirectory | Out-Null
Copy-Item -LiteralPath $ChineseSimplifiedCache -Destination $ChineseSimplified -Force
Assert-FileHash $ChineseSimplified $Manifest.ChineseSimplified.Sha256 `
    'Installed Inno Setup Simplified Chinese translation'
Write-Output $Compiler
