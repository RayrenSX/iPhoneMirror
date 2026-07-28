[CmdletBinding()]
param(
    [ValidateSet('Rtmp', 'Srt', 'Whip')]
    [string]$Protocol = 'Rtmp',
    [ValidateRange(0, 3600)]
    [int]$DurationSeconds = 0,
    [ValidateRange(1024, 65535)]
    [int]$DashboardPort = 8090
)

$ErrorActionPreference = 'Stop'
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
try {
    $status = Invoke-RestMethod -Uri "http://127.0.0.1:$DashboardPort/api/status" -TimeoutSec 3
} catch {
    throw "The Stream Lab dashboard is unavailable on port $DashboardPort. Start it first."
}
if (-not $status.ready) {
    throw "The Stream Lab media backend is unavailable: $($status.error)"
}
$destination = switch ($Protocol) {
    'Rtmp' { $status.endpoints.rtmp }
    'Srt'  { $status.endpoints.srt }
    'Whip' { $status.endpoints.whip }
}

$arguments = @(
    '-hide_banner', '-loglevel', 'warning', '-nostats', '-re',
    '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30',
    '-f', 'lavfi', '-i', 'sine=frequency=880:sample_rate=48000',
    '-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'zerolatency',
    '-pix_fmt', 'yuv420p', '-g', '60', '-b:v', '2500k'
)
if ($DurationSeconds -gt 0) {
    $arguments += @('-t', $DurationSeconds.ToString())
}

switch ($Protocol) {
    'Rtmp' { $arguments += @('-c:a', 'aac', '-b:a', '128k', '-f', 'flv', $destination) }
    'Srt' { $arguments += @('-c:a', 'aac', '-b:a', '128k', '-f', 'mpegts', $destination) }
    'Whip' {
        $arguments += @('-c:a', 'libopus', '-ac', '2', '-b:a', '96k',
            '-f', 'whip', $destination)
    }
}

Write-Host "Publishing $Protocol test signal to $($status.backend)."
if ($DurationSeconds -eq 0) { Write-Host 'Press Ctrl+C to stop.' }
& $ffmpeg @arguments
if ($LASTEXITCODE -ne 0) {
    throw "$Protocol test signal failed with FFmpeg exit code $LASTEXITCODE."
}
