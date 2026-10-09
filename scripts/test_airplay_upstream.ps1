[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReceiverRoot,
    [Parameter(Mandatory)][string]$FfmpegBuildRecord,
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$Ffmpeg
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$receiver = (Resolve-Path -LiteralPath $ReceiverRoot).Path
$record = Get-Content -LiteralPath $FfmpegBuildRecord -Raw | ConvertFrom-Json
$msys = (Resolve-Path -LiteralPath $MsysRoot).Path
if (-not $Ffmpeg) { $Ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source }
$videoEncoder = (Resolve-Path -LiteralPath $Ffmpeg).Path
$stage = Join-Path $root ('work\airplay-upstream-smoke\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath).Trim()
$cmake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$previousPath = $env:Path
try {
    $env:Path = (Join-Path $msys 'ucrt64\bin') + ';' + $previousPath
    & (Join-Path $msys 'ucrt64\bin\gcc.exe') (Join-Path $PSScriptRoot 'airplay_aac_eld_fixture.c') `
        -static-libgcc -lfdk-aac -lm -o (Join-Path $stage 'fixture.exe')
    if ($LASTEXITCODE) { throw 'Could not compile the test-only AAC-ELD fixture generator.' }
    & (Join-Path $stage 'fixture.exe') (Join-Path $stage 'aac-eld.frames')
    if ($LASTEXITCODE) { throw 'AAC-ELD fixture generation failed.' }
    foreach ($entry in @(@('landscape','96x64'),@('portrait','64x96'))) {
        & $videoEncoder -hide_banner -loglevel error -nostdin -f lavfi `
            -i "testsrc2=size=$($entry[1]):rate=6" -frames:v 1 -c:v libx264 -bf 0 -tune zerolatency `
            -pix_fmt yuv420p -f h264 (Join-Path $stage ($entry[0] + '.h264'))
        if ($LASTEXITCODE) { throw 'H.264 channel fixture generation failed.' }
    }
    & $cmake -S (Join-Path $PSScriptRoot 'airplay-upstream-tests') -B (Join-Path $stage 'build') `
        -A x64 "-DRECEIVER=$($receiver.Replace('\','/'))"
    if ($LASTEXITCODE) { throw 'Could not configure the receiver library tests.' }
    & $cmake --build (Join-Path $stage 'build') --config Release
    if ($LASTEXITCODE) { throw 'Could not build the receiver library tests.' }
    $runtime = Join-Path $stage 'build\Release'
    foreach ($file in $record.Files) {
        Copy-Item -LiteralPath (Join-Path $record.RuntimeDirectory $file.Name) -Destination $runtime
    }
    Copy-Item -LiteralPath (Join-Path $receiver 'external\plist\lib\x64\msys-2.0.dll') -Destination $runtime
    # The decoder probe runs with the staged production FFmpeg DLLs only.
    $env:Path = "$env:SystemRoot\System32;$env:SystemRoot"
    & (Join-Path $runtime 'airplay_upstream_smoke.exe') (Join-Path $stage 'aac-eld.frames')
    if ($LASTEXITCODE) { throw 'Receiver library regression failed.' }
    & (Join-Path $runtime 'airplay_channel_smoke.exe') (Join-Path $stage 'landscape.h264') `
        (Join-Path $stage 'portrait.h264')
    if ($LASTEXITCODE) { throw 'Receiver video-channel regression failed.' }
}
finally { $env:Path = $previousPath }
Write-Output $stage
