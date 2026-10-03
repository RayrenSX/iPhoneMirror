[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRecord,
    [Parameter(Mandatory)][string]$MsysRoot,
    [Parameter(Mandatory)][string]$Ffmpeg,
    [Parameter(Mandatory)][string]$Ffprobe
)
$ErrorActionPreference = 'Stop'
$record = Get-Content -LiteralPath $BuildRecord -Raw | ConvertFrom-Json
$root = Split-Path -Parent $PSScriptRoot
$test = Join-Path $root ('work\airplay-ffmpeg-smoke\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $test -Force | Out-Null
function Invoke-Media([string[]]$Arguments) {
    & $Ffmpeg -hide_banner -loglevel error -nostdin @Arguments
    if ($LASTEXITCODE) { throw 'FFmpeg fixture generation failed.' }
}
foreach ($entry in @(@('landscape','96x64'),@('portrait','64x96'))) {
    $name = $entry[0]
    Invoke-Media @('-f','lavfi','-i',"testsrc2=size=$($entry[1]):rate=6",'-frames:v','6',
        '-c:v','libx264','-pix_fmt','yuv420p','-f','h264',"$test\$name.h264")
    Invoke-Media @('-i',"$test\$name.h264",'-f','rawvideo',"$test\$name.expected")
}
Invoke-Media @('-f','lavfi','-i','sine=frequency=997:sample_rate=44100','-ac','2',
    '-c:a','alac','-sample_fmt','s16p','-frames:a','1',"$test\audio.m4a")
Invoke-Media @('-i',"$test\audio.m4a",'-f','s16le',"$test\audio.expected")
$json = & $Ffprobe -v error -show_streams -show_packets -show_data -of json "$test\audio.m4a"
if ($LASTEXITCODE) { throw 'Could not inspect ALAC test fixture.' }
$fixture = ($json -join "`n") | ConvertFrom-Json
function Write-HexFixture([string]$Text, [string]$Path) {
    $hex = (($Text -split "`n" | Where-Object { $_ -match '^[0-9a-fA-F]{8}:' } |
        ForEach-Object { ($_ -split ':',2)[1].TrimStart() -split '  ',2 | Select-Object -First 1 }) -join '') -replace '\s',''
    [IO.File]::WriteAllBytes($Path, [Convert]::FromHexString($hex))
}
if (@($fixture.packets).Count -ne 1) { throw 'Expected one ALAC packet.' }
Write-HexFixture $fixture.streams[0].extradata "$test\audio.extra"
Write-HexFixture $fixture.packets[0].data "$test\audio.packet"
$gcc = Join-Path $MsysRoot 'ucrt64\bin\gcc.exe'
$previousPath = $env:PATH
try {
    $env:PATH = (Join-Path ([IO.Path]::GetFullPath($MsysRoot)) 'ucrt64\bin') + ';' + $previousPath
    & $gcc -std=c11 -Wno-deprecated-declarations -static-libgcc `
        "-I$($record.SourceDirectory)" "-I$($record.BuildDirectory)" `
        (Join-Path $PSScriptRoot 'airplay_ffmpeg_smoke.c') `
        "-L$($record.BuildDirectory)/libavcodec" "-L$($record.BuildDirectory)/libavutil" `
        "-L$($record.BuildDirectory)/libswscale" "-L$($record.BuildDirectory)/libswresample" `
        -lavcodec -lavutil -lswscale -lswresample -o "$test\smoke.exe"
    if ($LASTEXITCODE) { throw 'Could not compile AirPlay codec smoke test.' }
    foreach ($file in $record.Files) {
        Copy-Item -LiteralPath (Join-Path $record.RuntimeDirectory $file.Name) -Destination $test
    }
    # Developer-installed DLLs cannot hide a missing runtime dependency.
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    & "$test\smoke.exe" "$test\landscape.h264" "$test\portrait.h264" `
        "$test\audio.extra" "$test\audio.packet" "$test\landscape.actual" `
        "$test\portrait.actual" "$test\audio.actual"
    if ($LASTEXITCODE) { throw 'AirPlay codec smoke failed.' }
}
finally { $env:PATH = $previousPath }
foreach ($name in @('landscape','portrait','audio')) {
    if ((Get-FileHash "$test\$name.expected").Hash -ne (Get-FileHash "$test\$name.actual").Hash) {
        throw "AirPlay $name decoded bytes differ from the reference FFmpeg."
    }
}
Write-Host 'AirPlay decoded video/audio bytes match reference FFmpeg exactly.'
Write-Output $test
