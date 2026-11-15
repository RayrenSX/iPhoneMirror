[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsysRoot, [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repo 'work/coredevice-build-tools'
$archive = Join-Path $cache 'ffmpeg.tar.xz'
$source = Join-Path $cache 'ffmpeg-8.1.2'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '464BEB5E7BF0C311E68B45AE2F04E9CC2AF88851ABB4082231742A74D97B524C') {
    throw 'Pinned FFmpeg source hash mismatch'
}
if (-not (Test-Path -LiteralPath $source)) {
    tar -xf $archive -C $cache
    if ($LASTEXITCODE) { throw 'FFmpeg extraction failed' }
}
$env:MSYSTEM = 'UCRT64'
$bash = Join-Path $MsysRoot 'usr/bin/bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { throw 'MSYS2 UCRT64 is required (gcc, make, patch, nasm, python)' }
$unixRepo = '/' + $repo.Replace('\', '/').Replace(':', '').TrimStart('/')
$unixSource = '/' + $source.Replace('\', '/').Replace(':', '').TrimStart('/')
if (-not (Select-String -LiteralPath (Join-Path $source 'libavcodec/hevc/hevcdec.c') -SimpleMatch 'iPhoneMirror: dimensions')) {
    # Reset only these known cache inputs after any interrupted patch attempt.
    tar -xf $archive -C $cache 'ffmpeg-8.1.2/libavcodec/hevc/hevcdec.c' 'ffmpeg-8.1.2/libavcodec/hevc/hevcdec.h'
    if ($LASTEXITCODE) { throw 'FFmpeg patch input extraction failed' }
    # Python from Windows runs the MSYS patch binary using an explicit environment.
    $oldPath = $env:PATH
    try {
        $env:PATH = (Join-Path $MsysRoot 'usr/bin') + ';' + $oldPath
        & $Python (Join-Path $PSScriptRoot 'patch_coredevice_ffmpeg.py') $source
        if ($LASTEXITCODE) { throw 'RVRA patch failed' }
    } finally { $env:PATH = $oldPath }
}
& $bash -lc 'bash "$1" "$2"' -- "$unixRepo/scripts/build_coredevice_decoder.sh" $unixSource
if ($LASTEXITCODE) { throw 'CoreDevice decoder build failed' }
