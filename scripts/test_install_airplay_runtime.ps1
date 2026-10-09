[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReceiverBinary,
    [Parameter(Mandatory)][string]$FfmpegBuildRecord,
    [string]$DnsSdBinary
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$receiver = (Resolve-Path -LiteralPath $ReceiverBinary).Path
$recordPath = (Resolve-Path -LiteralPath $FfmpegBuildRecord).Path
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$installArguments = @{ ReceiverBinary = $receiver; FfmpegBuildRecord = $recordPath }
if ($DnsSdBinary) { $installArguments.DnsSdBinary = (Resolve-Path -LiteralPath $DnsSdBinary).Path }
$stage = Join-Path $root ('work\airplay-install-tests\' + [Guid]::NewGuid().ToString('N'))
$vendor = Join-Path $stage 'third_party\airplay-server'
$scriptDirectory = Join-Path $stage 'scripts'
$appDirectory = Join-Path $stage 'src\App\Services'
New-Item -ItemType Directory -Path (Join-Path $vendor 'bin\x64'),
    (Join-Path $vendor 'patches'),$scriptDirectory,$appDirectory -Force | Out-Null
foreach ($name in @('install_airplay_runtime.ps1','ffmpeg-airplay-build.sh','ffmpeg-airplay-mathops.patch')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptDirectory
}
foreach ($name in @('SHA256SUMS.txt','BUILD.json','NOTICE-FFMPEG-BUILD.txt')) {
    Copy-Item -LiteralPath (Join-Path $root "third_party\airplay-server\$name") -Destination $vendor
}
Copy-Item -LiteralPath (Join-Path $root 'third_party\airplay-server\patches\airplay-upstream-125.patch') `
    -Destination (Join-Path $vendor 'patches')
Copy-Item -Path (Join-Path $root 'third_party\airplay-server\bin\x64\*.dll') `
    -Destination (Join-Path $vendor 'bin\x64')
$integrityPath = Join-Path $appDirectory 'RuntimeBinaryIntegrity.cs'
Copy-Item -LiteralPath (Join-Path $root 'src\App\Services\RuntimeBinaryIntegrity.cs') -Destination $integrityPath
$installer = Join-Path $scriptDirectory 'install_airplay_runtime.ps1'
$buildPath = Join-Path $vendor 'BUILD.json'
$targets = @(Get-ChildItem -LiteralPath (Join-Path $vendor 'bin\x64') -File |
    Select-Object -ExpandProperty FullName) + @($integrityPath,$buildPath,
        (Join-Path $vendor 'SHA256SUMS.txt'),(Join-Path $vendor 'NOTICE-FFMPEG-BUILD.txt'))
function Get-Snapshot {
    $snapshot = @{}
    foreach ($path in $targets) { $snapshot[$path] = (Get-FileHash -LiteralPath $path).Hash }
    return $snapshot
}
function Assert-Snapshot($expected) {
    foreach ($path in $expected.Keys) {
        if ((Get-FileHash -LiteralPath $path).Hash -ne $expected[$path]) {
            throw "Rollback or rejection changed $path"
        }
    }
    if (@(Get-ChildItem -LiteralPath $vendor,$appDirectory -Recurse -File |
        Where-Object { $_.Name -match '\.(tmp|bak)$' }).Count) {
        throw 'Transaction temporary files remain after completed rollback.'
    }
}
# Force the final replacement to fail after DLLs, hashes and notices were changed.
# All mutations occur in the disposable fixture, never the production directory.
$before = Get-Snapshot
$buildLock = [IO.File]::Open($buildPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$failed = $false
try {
    try { & $installer @installArguments }
    catch { $failed = $true }
}
finally { $buildLock.Dispose() }
if (-not $failed) { throw 'Expected a locked build record to reject the transaction.' }
Assert-Snapshot $before
& $installer @installArguments
$installed = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
foreach ($asset in $installed.Runtime) {
    $path = Join-Path $vendor "bin\x64\$($asset.Name)"
    if ((Get-FileHash -LiteralPath $path).Hash -ine $asset.Sha256 -or
        (Get-Item -LiteralPath $path).Length -ne $asset.Size) {
        throw "Installed build metadata disagrees with $($asset.Name)"
    }
}
$receiverHash = (Get-FileHash -LiteralPath $receiver).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath (Join-Path $vendor 'bin\x64\airplay2dll.dll')).Hash -ine $receiverHash -or
    -not ([IO.File]::ReadAllText($integrityPath).Contains($receiverHash)) -or
    -not ([IO.File]::ReadAllText((Join-Path $vendor 'SHA256SUMS.txt')).Contains($receiverHash)) -or
    $installed.ReceiverPatchSha256 -ine (Get-FileHash -LiteralPath (
        Join-Path $vendor 'patches\airplay-upstream-125.patch')).Hash -or
    $installed.Ffmpeg.Compiler -ne $record.Compiler) {
    throw 'Installer did not synchronize receiver integrity and build provenance.'
}
if ((Get-FileHash -LiteralPath (Join-Path $vendor 'NOTICE-FFMPEG-BUILD.txt')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $record.RuntimeDirectory 'NOTICE-FFMPEG-BUILD.txt')).Hash) {
    throw 'Build dependency notices were not installed.'
}
if ($DnsSdBinary) {
    $dnsHash = (Get-FileHash -LiteralPath $installArguments.DnsSdBinary).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath (Join-Path $vendor 'bin\x64\dnssd.dll')).Hash -ine $dnsHash -or
        -not ([IO.File]::ReadAllText($integrityPath).Contains($dnsHash)) -or
        -not ([IO.File]::ReadAllText((Join-Path $vendor 'SHA256SUMS.txt')).Contains($dnsHash)) -or
        @($installed.Runtime | Where-Object { $_.Name -eq 'dnssd.dll' }).Count -ne 1) {
        throw 'Installer did not synchronize the rebuilt DNS-SD runtime.'
    }
}
$after = Get-Snapshot
$badRecord = Join-Path $stage 'bad-record.json'
$record.Files[0].Sha256 = '0' * 64
[IO.File]::WriteAllText($badRecord,($record | ConvertTo-Json -Depth 6))
$rejected = $false
try { & $installer -ReceiverBinary $receiver -FfmpegBuildRecord $badRecord }
catch { $rejected = $true }
if (-not $rejected) { throw 'Installer accepted an invalid FFmpeg hash.' }
Assert-Snapshot $after
Write-Host 'Runtime install: rollback, synchronized hashes/build metadata/notices and bad-record rejection passed.'
Write-Output $stage
