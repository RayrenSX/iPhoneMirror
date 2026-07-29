[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ManifestPath = Join-Path $PSScriptRoot 'ffmpeg-runtime-manifest.psd1'
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "FFmpeg runtime manifest is missing: $ManifestPath"
}
$Manifest = Import-PowerShellDataFile -LiteralPath $ManifestPath
$Version = [string]$Manifest.Version
$ArchiveName = [string]$Manifest.ArchiveName
$DownloadUrl = [string]$Manifest.DownloadUrl
$ExpectedSha256 = [string]$Manifest.ArchiveSha256
$ExpectedFiles = [Collections.IDictionary]$Manifest.Files
if ([string]::IsNullOrWhiteSpace($Version) -or
    [string]::IsNullOrWhiteSpace($ArchiveName) -or
    [string]::IsNullOrWhiteSpace($DownloadUrl) -or
    $ExpectedSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
    $ExpectedFiles.Count -eq 0) {
    throw 'FFmpeg runtime manifest is invalid.'
}
$CacheRoot = Join-Path $Root "work\dependencies\ffmpeg-$Version"
$ArchivePath = Join-Path $CacheRoot $ArchiveName
$ExtractRoot = Join-Path $CacheRoot 'extracted'
$FullDestination = [IO.Path]::GetFullPath($Destination)
$FullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')

if (-not $FullDestination.StartsWith($FullRoot + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "FFmpeg destination is outside the workspace: $FullDestination"
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
$ArchiveValid = Test-Path -LiteralPath $ArchivePath -PathType Leaf
if ($ArchiveValid) {
    $ArchiveValid = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -eq
        $ExpectedSha256
}
if (-not $ArchiveValid) {
    $DownloadPath = "$ArchivePath.download"
    $MaximumAttempts = 4
    $RetryDelaysSeconds = @(5, 15, 30)
    for ($Attempt = 1; $Attempt -le $MaximumAttempts; $Attempt++) {
        if (Test-Path -LiteralPath $DownloadPath) {
            Remove-Item -LiteralPath $DownloadPath -Force
        }

        try {
            Write-Host "Downloading FFmpeg runtime (attempt $Attempt of $MaximumAttempts)..."
            Invoke-WebRequest -Uri $DownloadUrl -OutFile $DownloadPath `
                -UseBasicParsing -TimeoutSec 300
            break
        }
        catch {
            if (Test-Path -LiteralPath $DownloadPath) {
                Remove-Item -LiteralPath $DownloadPath -Force
            }

            $StatusCode = $null
            if ($null -ne $_.Exception.Response -and
                $null -ne $_.Exception.Response.StatusCode) {
                $StatusCode = [int]$_.Exception.Response.StatusCode
            }
            $IsTransientFailure = $null -eq $StatusCode -or
                $StatusCode -eq 408 -or
                $StatusCode -eq 429 -or
                $StatusCode -ge 500

            if (-not $IsTransientFailure -or $Attempt -eq $MaximumAttempts) {
                throw
            }

            $DelaySeconds = $RetryDelaysSeconds[$Attempt - 1]
            $FailureDescription = if ($null -eq $StatusCode) {
                $_.Exception.Message
            }
            else {
                "HTTP $StatusCode"
            }
            Write-Warning "FFmpeg download failed with $FailureDescription. Retrying in $DelaySeconds seconds."
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    $ActualHash = (Get-FileHash -LiteralPath $DownloadPath -Algorithm SHA256).Hash
    if ($ActualHash -ne $ExpectedSha256) {
        Remove-Item -LiteralPath $DownloadPath -Force
        throw "FFmpeg archive hash mismatch: expected $ExpectedSha256, got $ActualHash"
    }
    Move-Item -LiteralPath $DownloadPath -Destination $ArchivePath -Force
}

if (Test-Path -LiteralPath $ExtractRoot) {
    Remove-Item -LiteralPath $ExtractRoot -Recurse -Force
}
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractRoot
$PackageRoots = @(Get-ChildItem -LiteralPath $ExtractRoot -Directory)
if ($PackageRoots.Count -ne 1) {
    throw 'The FFmpeg archive must contain exactly one package directory.'
}
$PackageRoot = $PackageRoots[0].FullName
$FfmpegPath = Join-Path $PackageRoot 'bin\ffmpeg.exe'
$LicensePath = Join-Path $PackageRoot 'LICENSE'
$ReadmePath = Join-Path $PackageRoot 'README.txt'
foreach ($RequiredPath in @($FfmpegPath, $LicensePath, $ReadmePath)) {
    if (-not (Test-Path -LiteralPath $RequiredPath -PathType Leaf)) {
        throw "FFmpeg package file is missing: $RequiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $FullDestination | Out-Null
Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $FullDestination 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $LicensePath -Destination (Join-Path $FullDestination 'LICENSE.txt') -Force
Copy-Item -LiteralPath $ReadmePath -Destination (Join-Path $FullDestination 'README.txt') -Force
@"
FFmpeg $Version essentials build for Windows

Binary package: $DownloadUrl
Binary SHA-256: $ExpectedSha256
Build provider: https://www.gyan.dev/ffmpeg/builds/
Upstream source: https://ffmpeg.org/releases/ffmpeg-$Version.tar.xz
"@ | Set-Content -LiteralPath (Join-Path $FullDestination 'SOURCE.txt') -Encoding utf8

foreach ($entry in $ExpectedFiles.GetEnumerator()) {
    $file = Join-Path $FullDestination ([string]$entry.Key)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Prepared FFmpeg runtime file is missing: $($entry.Key)"
    }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($actual -ne [string]$entry.Value) {
        throw "Prepared FFmpeg runtime hash mismatch for $($entry.Key): expected $($entry.Value), got $actual"
    }
}

Write-Output (Join-Path $FullDestination 'ffmpeg.exe')
