[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$PlatformToolset,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
# .NET Framework MSBuild rejects environment blocks containing both Path and PATH.
$ProcessEnvironment = [Environment]::GetEnvironmentVariables()
$PathEntries = @($ProcessEnvironment.GetEnumerator() |
    Where-Object { $_.Key -ieq 'Path' })
if ($PathEntries.Count -gt 1) {
    $CanonicalPath = ($PathEntries | Where-Object { $_.Key -ceq 'Path' } |
        Select-Object -First 1).Value
    if ($null -eq $CanonicalPath) { $CanonicalPath = $PathEntries[0].Value }
    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $CanonicalPath, 'Process')
}
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ReceiverRoot = Join-Path $Root 'third_party\airplay-server'
$Commit = '34ba6cfd49b2432cf30e89913d66decb775763e4'
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

$RepositoryRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$SafeRepositoryRoot = $RepositoryRoot.Replace('\', '/')
$Head = (& git -c "safe.directory=$SafeRepositoryRoot" -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $Head -ne $Commit) {
    throw "AirPlayServer source must be at $Commit; found $Head"
}

# Build from a detached clean worktree so local tracked/untracked files can
# never enter the vendored binary, while retaining the caller's source clone.
$BuildSourceRoot = Join-Path $Root ("build\airplay-server-build-" +
    [Guid]::NewGuid().ToString('N'))
& git -c "safe.directory=$SafeRepositoryRoot" -C $RepositoryRoot worktree add --detach `
    $BuildSourceRoot $Commit
if ($LASTEXITCODE -ne 0) {
    throw "Could not create a clean AirPlayServer worktree: $LASTEXITCODE"
}
try {
$SourceRoot = (Resolve-Path -LiteralPath $BuildSourceRoot).Path
& (Join-Path $ReceiverRoot 'patches\Apply-DeviceMetadataPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-DisplayCapabilityPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-ScreenMirroringOnlyPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-NetworkRoutePatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-AirPlayCompatibilityPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-AirPlayMirrorRecoveryPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-AirPlayOrientationAccessUnitPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-AudioCodecPatch.ps1') `
    -SourceRoot $SourceRoot
& (Join-Path $ReceiverRoot 'patches\Apply-AirPlayAudioNegotiationPatch.ps1') `
    -SourceRoot $SourceRoot

$CompatibilityMarkers = @(
    @{ Path = 'AirPlayServerLib\lib\http_parser.c';
       Marker = 'IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY' },
    @{ Path = 'AirPlayServerLib\lib\http_request.c';
       Marker = 'IPHONE_MIRROR_AIRPLAY_HEADER_CASE' },
    @{ Path = 'AirPlayServerLib\lib\pairing.c';
       Marker = 'IPHONE_MIRROR_AIRPLAY_PAIR_VERIFY_TWO_STAGE' },
    @{ Path = 'AirPlayServerLib\lib\raop_rtp_mirror.c';
       Marker = 'IPHONE_MIRROR_AIRPLAY_MIRROR_RECOVERY' },
    @{ Path = 'AirPlayServerLib\lib\raop_rtp.c';
       Marker = 'IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION' },
    @{ Path = 'AirPlayServerLib\lib\raop_rtp_mirror.c';
       Marker = 'IPHONE_MIRROR_MIRROR_NAL_BOUNDS' },
    @{ Path = 'AirPlayServerLib\lib\raop_rtp_mirror.c';
       Marker = 'IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT' },
    @{ Path = 'AirPlayServerLib\lib\raop.c';
       Marker = 'IPHONE_MIRROR_MIRROR_FLAG_OPTIONAL' },
    @{ Path = 'AirPlayServerLib\lib\httpd.c';
       Marker = 'IPHONE_MIRROR_AIRPLAY_RECV_ERROR' },
    @{ Path = 'airplay2dll\FgAirplayChannel.cpp';
       Marker = 'IPHONE_MIRROR_H264_DECODER_RECOVERY' })
foreach ($Entry in $CompatibilityMarkers) {
    $PatchedPath = Join-Path $SourceRoot $Entry.Path
    if (-not ([IO.File]::ReadAllText($PatchedPath).Contains($Entry.Marker))) {
        throw "AirPlay compatibility marker is missing: $($Entry.Marker)"
    }
}

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
$TargetWidth = '6539323511140013000000005A7FFEE610015A41'
$MediaFeaturesLittleEndian = 'F7FE7F5A'
$LegacyHeightAndRate = '6C73100E1105A0101E5F102465306666'
$LegacyWidth = '65393235110D7013000000005A7FFEE610015A41'
if (-not $BinaryHex.Contains($TargetHeightAndRate) -or
    -not $BinaryHex.Contains($TargetWidth) -or
    -not $BinaryHex.Contains($MediaFeaturesLittleEndian) -or
    $BinaryHex.Contains($LegacyHeightAndRate) -or
    $BinaryHex.Contains($LegacyWidth)) {
    throw 'Built AirPlay receiver does not contain the expected display and mode-specific feature capabilities.'
}
$BinaryAscii = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Binary))
foreach ($Marker in @('IPHONE_MIRROR_AIRPLAY_WIDTH', 'IPHONE_MIRROR_AIRPLAY_HEIGHT',
        'IPHONE_MIRROR_AIRPLAY_FPS', 'IPHONE_MIRROR_AIRPLAY_NAME',
        'IPHONE_MIRROR_MEDIA_CAST_BLOCKED',
        'IPHONE_MIRROR_AIRPLAY_MODE',
        'IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY',
        'IPHONE_MIRROR_RAOP_MEDIA_CAST_BLOCKED',
        'IPHONE_MIRROR_AIRPLAY_PAIRING_SEED',
        'IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY',
        'IPHONE_MIRROR_ALAC_AUDIO_DECODE',
        'IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION',
        'IPHONE_MIRROR_H264_DECODER_RECOVERY',
        'IPHONE_MIRROR_H264_ROTATION_RECOVERY',
        'IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT',
        'IPHONE_MIRROR_DLL_ENVIRONMENT_SYNC',
        'IPHONE_MIRROR_RUNTIME_DEVICE_ID',
        'IPHONE_MIRROR_BOUND_MEDIA_SOCKETS',
        'IPHONE_MIRROR_PRESERVE_IPV6_MEDIA',
        'Mirror TCP read timed out after %d ms',
        'iphonemirror://pause',
        'iphonemirror://resume',
        'iphonemirror://seek',
        '2e388006-13ba-4041-9a67-25dd4a43d536',
        'AppleTV3,2', '220.68', 'combined')) {
    if (-not $BinaryAscii.Contains($Marker)) {
        throw "Built AirPlay receiver is missing runtime capability marker: $Marker"
    }
}
if ($BinaryAscii.Contains('0x5A7FFFF7,0x1E') -or
    $BinaryAscii.Contains('0x5A7FFFC0,0x1E') -or
    $BinaryAscii.Contains('0x484051C0,0x0') -or
    $BinaryAscii.Contains('0x1A7FFEC0,0x0') -or
    $BinaryAscii.Contains('0x5A7FFEC0,0x0')) {
    throw 'Built AirPlay receiver contains an inconsistent legacy feature mask.'
}
Write-Host 'Verified combined screen-mirroring and URL-video AirPlay mode.' -ForegroundColor Green
$Hash = (Get-FileHash -LiteralPath $Binary -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Install) {
    $TargetBinary = Join-Path $ReceiverRoot 'bin\x64\airplay2dll.dll'
    $Manifest = Join-Path $ReceiverRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $TargetBinary -PathType Leaf) -or
        -not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        throw 'Vendored AirPlay receiver or SHA256SUMS.txt is missing.'
    }
    foreach ($existing in @($TargetBinary, $Manifest)) {
        $existingItem = Get-Item -LiteralPath $existing -Force
        if (($existingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Vendored AirPlay asset is a reparse point: $existing"
        }
    }

    $ManifestText = [IO.File]::ReadAllText($Manifest)
    $ManifestPattern = '(?m)^[0-9a-fA-F]{64}  bin/x64/airplay2dll\.dll\r?$'
    if ([regex]::Matches($ManifestText, $ManifestPattern).Count -ne 1) {
        throw 'SHA256SUMS.txt must contain exactly one airplay2dll.dll entry.'
    }
    $CarriageReturn = if ($ManifestText.Contains("`r`n")) { "`r" } else { '' }
    $UpdatedManifest = [regex]::Replace($ManifestText, $ManifestPattern,
        "$Hash  bin/x64/airplay2dll.dll$CarriageReturn")

    $TransactionId = [Guid]::NewGuid().ToString('N')
    $StagedBinary = "$TargetBinary.$TransactionId.tmp"
    $StagedManifest = "$Manifest.$TransactionId.tmp"
    $BackupBinary = "$TargetBinary.$TransactionId.bak"
    $BackupManifest = "$Manifest.$TransactionId.bak"
    # File.Replace requires a concrete backup path on the Windows/.NET
    # runtime used by the build environment. Keep separate rollback copies so
    # a failure in the second replacement can still restore both targets.
    $ReplaceBackupBinary = "$TargetBinary.$TransactionId.replace.bak"
    $ReplaceBackupManifest = "$Manifest.$TransactionId.replace.bak"
    $TargetsMayBeModified = $false
    $InstallComplete = $false
    $RollbackComplete = $false
    try {
        Copy-Item -LiteralPath $Binary -Destination $StagedBinary
        [IO.File]::WriteAllText($StagedManifest, $UpdatedManifest,
            [Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath $TargetBinary -Destination $BackupBinary
        Copy-Item -LiteralPath $Manifest -Destination $BackupManifest

        $TargetsMayBeModified = $true
        [IO.File]::Replace($StagedBinary, $TargetBinary, $ReplaceBackupBinary)
        [IO.File]::Replace($StagedManifest, $Manifest, $ReplaceBackupManifest)
        $InstalledHash = (Get-FileHash -LiteralPath $TargetBinary `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($InstalledHash -ne $Hash -or
            -not ([IO.File]::ReadAllText($Manifest).Contains(
                "$Hash  bin/x64/airplay2dll.dll"))) {
            throw 'Installed AirPlay receiver and hash manifest did not verify.'
        }
        $InstallComplete = $true
    }
    catch {
        $installError = $_
        if (-not $TargetsMayBeModified) {
            $RollbackComplete = $true
        }
        else {
            $rollbackErrors = @()
            foreach ($restore in @(
                    [PSCustomObject]@{ Backup = $BackupBinary; Target = $TargetBinary },
                    [PSCustomObject]@{ Backup = $BackupManifest; Target = $Manifest })) {
                try {
                    if (-not (Test-Path -LiteralPath $restore.Backup -PathType Leaf)) {
                        throw "Backup is missing: $($restore.Backup)"
                    }
                    Copy-Item -LiteralPath $restore.Backup `
                        -Destination $restore.Target -Force
                }
                catch {
                    $rollbackErrors += $_.Exception.Message
                }
            }
            $RollbackComplete = $rollbackErrors.Count -eq 0
            if (-not $RollbackComplete) {
                throw "AirPlay receiver install failed: $($installError.Exception.Message) " +
                    "Rollback was incomplete: $($rollbackErrors -join '; '). " +
                    "Recovery backups were retained at $BackupBinary and $BackupManifest."
            }
        }
        throw $installError
    }
    finally {
        foreach ($temporary in @($StagedBinary, $StagedManifest,
                $ReplaceBackupBinary, $ReplaceBackupManifest)) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
        if ($InstallComplete -or $RollbackComplete) {
            foreach ($backup in @($BackupBinary, $BackupManifest)) {
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Host 'Updated vendored receiver and SHA256SUMS.txt.' -ForegroundColor Green
}
Write-Host "AirPlay receiver: $Binary" -ForegroundColor Green
Write-Host "SHA256: $Hash" -ForegroundColor Green
if (-not $Install) {
    Write-Host 'Pass -Install to replace the vendored receiver binary.' -ForegroundColor Yellow
}
}
finally {
    & git -c "safe.directory=$SafeRepositoryRoot" -C $RepositoryRoot worktree remove `
        --force $BuildSourceRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove temporary AirPlayServer worktree: $BuildSourceRoot"
    }
}
