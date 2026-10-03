[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$MsysRoot,
    [Parameter(Mandatory)][string]$Ffmpeg,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$msys = (Resolve-Path -LiteralPath $MsysRoot).Path
$ff = (Resolve-Path -LiteralPath $Ffmpeg).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$probe = Join-Path $output 'uxplay-media-smoke.exe'
$ucrt = Join-Path $msys 'ucrt64'
$compilerPath = $env:PATH
try {
    $env:PATH = "$ucrt/bin;$compilerPath"
    & (Join-Path $ucrt 'bin/gcc.exe') (Join-Path $PSScriptRoot 'uxplay_media_smoke.c') `
        "-I$ucrt/include/gstreamer-1.0" "-I$ucrt/include/glib-2.0" "-I$ucrt/lib/glib-2.0/include" `
        "-L$ucrt/lib" '-lgstapp-1.0' '-lgstreamer-1.0' '-lgobject-2.0' '-lglib-2.0' -static-libgcc -o $probe
    if ($LASTEXITCODE -ne 0) { throw 'GStreamer smoke compilation failed' }
} finally { $env:PATH = $compilerPath }
$h264 = Join-Path $output 'video.h264'
$aac = Join-Path $output 'audio.aac'
$reference = Join-Path $output 'reference.yuv'
& $ff -hide_banner -loglevel error -nostdin -y -f lavfi -i 'testsrc2=size=128x72:rate=10' -t 1 -c:v libx264 -pix_fmt yuv420p -f h264 $h264
if ($LASTEXITCODE -ne 0) { throw 'H264 fixture generation failed' }
& $ff -hide_banner -loglevel error -nostdin -y -i $h264 -pix_fmt yuv420p -f rawvideo $reference
if ($LASTEXITCODE -ne 0) { throw 'H264 reference decode failed' }
& $ff -hide_banner -loglevel error -nostdin -y -f lavfi -i 'sine=frequency=997:sample_rate=44100' -t 1 -ac 2 -c:a aac -f adts $aac
if ($LASTEXITCODE -ne 0) { throw 'AAC fixture generation failed' }
function Invoke-Pipeline([string]$Pipeline, [string]$Name, [string]$InputAudio) {
    $start = [Diagnostics.ProcessStartInfo]::new($probe)
    $start.WorkingDirectory = Join-Path $runtime 'bin'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $start.Environment['GST_PLUGIN_PATH_1_0'] = Join-Path $runtime 'lib/gstreamer-1.0'
    $start.Environment['GST_PLUGIN_SYSTEM_PATH_1_0'] = Join-Path $runtime 'lib/gstreamer-1.0'
    $start.Environment['GST_PLUGIN_PATH'] = Join-Path $runtime 'lib/gstreamer-1.0'
    $start.Environment['GST_PLUGIN_SYSTEM_PATH'] = Join-Path $runtime 'lib/gstreamer-1.0'
    $start.Environment['GST_REGISTRY_FORK'] = 'no'
    $start.Environment['GST_REGISTRY'] = Join-Path $output ("registry-$Name.bin")
    $start.ArgumentList.Add($Pipeline)
    $start.ArgumentList.Add($Name)
    if ($InputAudio) { $start.ArgumentList.Add($InputAudio) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'GStreamer timeout' }
        Write-Host $stdout.GetAwaiter().GetResult()
        $diagnostic = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "GStreamer exited $($process.ExitCode): $diagnostic" }
        if ($diagnostic) { Write-Host $diagnostic }
    } finally { $process.Dispose() }
}
# GStreamer parses forward slash paths on Windows; preserve surrounding quotes.
$h264Path = $h264.Replace('\', '/')
foreach ($round in 1..3) {
    $video = (Join-Path $output "video-$round.yuv").Replace('\', '/')
    $audio = (Join-Path $output "audio-$round.pcm").Replace('\', '/')
    Invoke-Pipeline "filesrc location=`"$h264Path`" ! h264parse config-interval=-1 ! avdec_h264 output-corrupt=false discard-corrupted-frames=true ! video/x-raw,format=I420 ! filesink location=`"$video`"" "h264-$round" ''
    if ((Get-FileHash -LiteralPath $video).Hash -ne (Get-FileHash -LiteralPath $reference).Hash) { throw 'GStreamer H264 decoded pixels differ from reference' }
    Invoke-Pipeline "appsrc name=audio_source format=time caps=`"audio/mpeg,mpegversion=(int)4,channels=(int)2,rate=(int)44100,stream-format=raw,codec_data=(buffer)1210`" ! avdec_aac ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,rate=48000,channels=2,layout=interleaved ! filesink location=`"$audio`"" "aac-$round" $aac
    $pcm = [IO.File]::ReadAllBytes($audio)
    if ($pcm.Length -lt 180000 -or $pcm.Length -gt 210000 -or -not ($pcm.Where({ $_ -ne 0 }, 'First'))) { throw 'GStreamer AAC failed to produce non-silent one-second PCM' }
}
Write-Host 'Packaged UxPlay H264 exact decoded pixels and AAC non-silent resampled PCM passed across three restarts.'
