[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReceiverBinary,
    [Parameter(Mandatory)][string]$FfmpegBuildRecord,
    [string]$DnsSdBinary
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vendored = Join-Path $root 'third_party\airplay-server'
$record = Get-Content -LiteralPath $FfmpegBuildRecord -Raw | ConvertFrom-Json
if ($record.Commit -ne '63b2b0f47df420007c53888ce0e8383d24b8fb06' -or
    $record.SourceSha256 -ine '2d6e9244be21b32756c481f4653d7f17ae08608dd2db1ba04e826cf0052df860' -or
    $record.RecipeSha256 -ine (Get-FileHash (Join-Path $PSScriptRoot 'ffmpeg-airplay-build.sh')).Hash -or
    $record.PatchSha256 -ine (Get-FileHash (Join-Path $PSScriptRoot 'ffmpeg-airplay-mathops.patch')).Hash) {
    throw 'FFmpeg build record does not match the AAC-enabled production recipe.'
}
$names = @('avcodec-58.dll','avutil-56.dll','swresample-3.dll','swscale-5.dll')
if (@($record.Files).Count -ne 4) { throw 'FFmpeg build record must contain four runtime DLLs.' }
$receiver = (Resolve-Path -LiteralPath $ReceiverBinary).Path
$receiverText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($receiver))
foreach ($marker in @('IPHONE_MIRROR_FFMPEG_AAC_ELD','IPHONE_MIRROR_VIDEO_STATE',
        'IPHONE_MIRROR_VIDEO_GEOMETRY','IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION',
        'IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT','IPHONE_MIRROR_MIRROR_CTR_SKIP',
        'IPHONE_MIRROR_MIRROR_SETUP_RESTART')) {
    if (-not $receiverText.Contains($marker)) { throw "Receiver capability is missing: $marker" }
}
$assets = @(@{ Name = 'airplay2dll.dll'; Source = $receiver })
if ($DnsSdBinary) {
    $assets += @{ Name = 'dnssd.dll'; Source = (Resolve-Path -LiteralPath $DnsSdBinary).Path }
}
foreach ($name in $names) {
    $file = @($record.Files | Where-Object { $_.Name -ceq $name })
    if ($file.Count -ne 1) { throw "Missing or duplicate FFmpeg asset: $name" }
    $source = Join-Path $record.RuntimeDirectory $name
    if ((Get-FileHash -LiteralPath $source).Hash -ine $file[0].Sha256) { throw "FFmpeg asset hash mismatch: $name" }
    $assets += @{ Name = $name; Source = $source }
}
$manifestPath = Join-Path $vendored 'SHA256SUMS.txt'
$integrityPath = Join-Path $root 'src\App\Services\RuntimeBinaryIntegrity.cs'
$buildPath = Join-Path $vendored 'BUILD.json'
$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$build.ReceiverPatchSha256 = (Get-FileHash -LiteralPath (
    Join-Path $vendored 'patches\airplay-upstream-125.patch')).Hash.ToLowerInvariant()
$build.Ffmpeg = [ordered]@{
    Commit = $record.Commit
    SourceUrl = $record.SourceUrl
    SourceSha256 = $record.SourceSha256
    RecipeSha256 = $record.RecipeSha256
    PatchSha256 = $record.PatchSha256
    Compiler = $record.Compiler
}
$manifest = [IO.File]::ReadAllText($manifestPath)
$integrity = [IO.File]::ReadAllText($integrityPath)
$operations = @()
foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset.Source).Hash.ToLowerInvariant()
    $escaped = [regex]::Escape($asset.Name)
    $manifestPattern = "(?m)^[0-9a-fA-F]{64}(  bin/x64/$escaped)(\r?)$"
    $integrityPattern = '(\["' + $escaped + '"\]\s*=\s*")[0-9a-fA-F]{64}("[,;])'
    if ([regex]::Matches($manifest, $manifestPattern).Count -ne 1 -or
        [regex]::Matches($integrity, $integrityPattern).Count -ne 1) {
        throw "Runtime integrity entry is missing or ambiguous: $($asset.Name)"
    }
    $manifest = [regex]::Replace($manifest, $manifestPattern, $hash + '${1}${2}')
    $integrity = [regex]::Replace($integrity, $integrityPattern, '${1}' + $hash + '${2}')
    $operations += @{ Target = Join-Path $vendored "bin\x64\$($asset.Name)";
        Source = $asset.Source; ExpectedHash = $hash }
}
$runtimeAssets = $assets
if (-not $DnsSdBinary) {
    $runtimeAssets += @{ Name = 'dnssd.dll'; Source = (Join-Path $vendored 'bin\x64\dnssd.dll') }
}
$build.Runtime = @($runtimeAssets | Sort-Object { $_.Name } | ForEach-Object {
    [ordered]@{ Name = $_.Name; Size = (Get-Item -LiteralPath $_.Source).Length;
        Sha256 = (Get-FileHash -LiteralPath $_.Source).Hash.ToLowerInvariant() }
})
$operations += @{ Target = $manifestPath; Text = $manifest }
$operations += @{ Target = $integrityPath; Text = $integrity }
$operations += @{ Target = Join-Path $vendored 'NOTICE-FFMPEG-BUILD.txt';
    Source = Join-Path $record.RuntimeDirectory 'NOTICE-FFMPEG-BUILD.txt' }
$operations += @{ Target = $buildPath; Text = ($build | ConvertTo-Json -Depth 6) + "`n" }
$transaction = [Guid]::NewGuid().ToString('N')
$completed = $false
$rollbackComplete = $true
try {
    foreach ($operation in $operations) {
        $item = Get-Item -LiteralPath $operation.Target -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Runtime target is a reparse point: $($operation.Target)" }
        $operation.Stage = "$($operation.Target).$transaction.tmp"
        $operation.Backup = "$($operation.Target).$transaction.bak"
        $operation.Replaced = $false
        if ($operation.Source) { Copy-Item -LiteralPath $operation.Source -Destination $operation.Stage }
        else { [IO.File]::WriteAllText($operation.Stage, $operation.Text, [Text.UTF8Encoding]::new($false)) }
        $operation.Hash = (Get-FileHash -LiteralPath $operation.Stage).Hash
        if ($operation.ExpectedHash -and $operation.Hash -ine $operation.ExpectedHash) {
            throw "Runtime source changed while staging: $($operation.Target)"
        }
    }
    foreach ($operation in $operations) {
        [IO.File]::Replace($operation.Stage, $operation.Target, $operation.Backup)
        $operation.Replaced = $true
        if ((Get-FileHash -LiteralPath $operation.Target).Hash -ne $operation.Hash) {
            throw "Installed runtime failed hash verification: $($operation.Target)"
        }
    }
    $completed = $true
}
catch {
    $failure = $_
    foreach ($operation in $operations) {
        if ($operation.Replaced) {
            try { Copy-Item -LiteralPath $operation.Backup -Destination $operation.Target -Force }
            catch { $rollbackComplete = $false; Write-Warning "Rollback failed; retain $($operation.Backup)" }
        }
    }
    throw $failure
}
finally {
    foreach ($operation in $operations) {
        if ($operation.Stage) { Remove-Item -LiteralPath $operation.Stage -Force -ErrorAction SilentlyContinue }
        if (($completed -or $rollbackComplete) -and $operation.Backup) {
            Remove-Item -LiteralPath $operation.Backup -Force -ErrorAction SilentlyContinue
        }
    }
}
Write-Host 'Installed AirPlay runtime, integrity manifests, notices and build record.'
