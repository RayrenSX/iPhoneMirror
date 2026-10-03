[CmdletBinding()]
param([Parameter(Mandatory)][string]$Ffmpeg)
$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $Ffmpeg).Path
$checks = @{
    encoders = @('libx264','h264_nvenc','h264_amf','h264_qsv','h264_mf','aac','libopus')
    # These source formats worked before size optimization. A recording-only
    # smoke cannot certify media-source audio compatibility.
    decoders = @('pcm_s16le','pcm_s24le','pcm_s32le','pcm_u8','pcm_f32le','pcm_f64le',
        'pcm_s24be','adpcm_ima_wav','pcm_alaw','wmav2','flac','alac','aac','mp3','opus','ac3','eac3','mp2')
    muxers = @('mp4','flv','mpegts','whip')
    demuxers = @('rawvideo','s16le','hls','mov','mpegts','dshow','wav','aiff','asf','mp3','ogg')
    protocols = @('file','pipe','http','https','rtmp','rtmps','srt','crypto')
    filters = @('scale','format','aformat','aresample','asetpts','atempo','amix','alimiter','anull')
}
foreach ($entry in $checks.GetEnumerator()) {
    $output = (& $binary -hide_banner "-$($entry.Key)" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect FFmpeg $($entry.Key)." }
    foreach ($feature in $entry.Value) {
        if ($output -notmatch "(?m)(?<![A-Za-z0-9_])$([regex]::Escape($feature))(?![A-Za-z0-9_])") {
            throw "Compact FFmpeg is missing required $($entry.Key): $feature"
        }
    }
}
$root = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $root ('work\compact-ffmpeg-smoke\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$recording = Join-Path $testRoot 'recording.mp4'
& $binary -hide_banner -loglevel error -nostdin -f lavfi -i 'testsrc2=size=128x72:rate=10' `
    -f lavfi -i 'sine=frequency=1000:sample_rate=48000' -t 1 -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart $recording
if ($LASTEXITCODE -ne 0) { throw 'Compact FFmpeg MP4/AAC recording smoke failed.' }
& $binary -hide_banner -loglevel error -nostdin -i $recording -f null -
if ($LASTEXITCODE -ne 0) { throw 'Compact FFmpeg recorded audio/video decode smoke failed.' }
& $binary -hide_banner -loglevel error -nostdin -f lavfi -i 'sine=frequency=1000:sample_rate=48000' `
    -t 1 -c:a libopus -f null -
if ($LASTEXITCODE -ne 0) { throw 'Compact FFmpeg Opus encoding smoke failed.' }
Write-Host 'Compact FFmpeg required capabilities and CPU media smoke passed.'
