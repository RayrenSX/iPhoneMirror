[CmdletBinding()]
param(
    [string]$Receiver = (Join-Path $PSScriptRoot 'Wireless/iPhoneMirror.WirelessHost.exe'),
    [ValidateRange(65, 600)][int]$ObserveSeconds = 90,
    [ValidateRange(30, 1200)][int]$ConnectTimeout = 300,
    [string]$LogPath = (Join-Path $PSScriptRoot 'session.log')
)

$ErrorActionPreference = 'Stop'
$Receiver = (Resolve-Path -LiteralPath $Receiver).Path
$LogPath = [IO.Path]::GetFullPath($LogPath)
$suffix = [Guid]::NewGuid().ToString('N')
$pipeName = "iPhoneMirror-NoAudio-$suffix"
$stopName = "Local\iPhoneMirror-NoAudio-Stop-$suffix"
$displayName = 'iPhoneMirror - Phone Audio Test'

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

function Write-Observation([string]$Message) {
    $line = "{0:o} {1}" -f [DateTimeOffset]::Now, $Message
    [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine)
    Write-Host $line
}

function Read-ReceiverBytes([byte[]]$Bytes, [int]$Count, [int]$TimeoutMs, [switch]$AllowTimeout) {
    $wait = [Threading.CancellationTokenSource]::new($TimeoutMs)
    try {
        $offset = 0
        while ($offset -lt $Count) {
            try {
                $read = $pipe.ReadAsync($Bytes, $offset, $Count - $offset, $wait.Token).GetAwaiter().GetResult()
            } catch [OperationCanceledException] {
                if ($AllowTimeout -and $offset -eq 0) { return $false }
                throw
            }
            if ($read -eq 0) { throw 'The AirPlay receiver closed its observation pipe.' }
            $offset += $read
        }
        return $true
    } finally { $wait.Dispose() }
}

$pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut,
    1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 65536, 65536)
$stop = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $stopName)
$process = $null
try {
    [IO.File]::WriteAllText($LogPath, '')
    $raopPort = Get-FreeTcpPort
    do { $airPlayPort = Get-FreeTcpPort } while ($airPlayPort -eq $raopPort)
    $start = [Diagnostics.ProcessStartInfo]::new($Receiver)
    $start.WorkingDirectory = Split-Path -Parent $Receiver
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['IPHONE_MIRROR_DIAGNOSTIC_MIRROR_NO_AUDIO'] = '1'
    [void]$start.Environment.Remove('IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY')
    foreach ($argument in @('--pipe', "\\.\pipe\$pipeName", '--stop-event', $stopName,
            '--name', $displayName, '--parent-pid', "$PID", '--mode', 'mirror',
            '--raop-port', "$raopPort", '--airplay-port', "$airPlayPort",
            '--width', '1280', '--height', '720', '--fps', '30')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $connect = [Threading.CancellationTokenSource]::new(12000)
    try { $pipe.WaitForConnectionAsync($connect.Token).GetAwaiter().GetResult() }
    finally { $connect.Dispose() }
    $header = [byte[]]::new(392)
    $chunk = [byte[]]::new(65536)
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $firstVideo = $null
    $latestVideo = 0.0
    $videoCount = 0
    $audioCount = 0
    $nextReport = 15
    while ($true) {
        if ($null -eq $firstVideo -and $elapsed.Elapsed.TotalSeconds -ge $ConnectTimeout) {
            Write-Observation 'No mirrored video arrived before the connection timeout.'
            break
        }
        if ($null -ne $firstVideo -and $elapsed.Elapsed.TotalSeconds - $firstVideo -ge $ObserveSeconds) {
            Write-Observation ('Observation ended: video frames={0}, audio packets={1}, last frame at {2:N1}s.' -f
                $videoCount, $audioCount, ($latestVideo - $firstVideo))
            break
        }
        if (-not (Read-ReceiverBytes $header $header.Length 500 -AllowTimeout)) { continue }
        $size = [BitConverter]::ToUInt32($header, 8)
        if ([BitConverter]::ToUInt32($header, 0) -ne 0x50414D49 -or
            [BitConverter]::ToUInt16($header, 4) -ne 8 -or $size -gt 64MB) {
            throw 'Unexpected AirPlay IPC header.'
        }
        for ($remaining = $size; $remaining -gt 0; $remaining -= $length) {
            $length = [Math]::Min($chunk.Length, $remaining)
            [void](Read-ReceiverBytes $chunk $length 10000)
        }
        switch ([BitConverter]::ToUInt16($header, 6)) {
            1 { Write-Observation "Ready. Choose '$displayName' in iPhone Screen Mirroring (TCP $raopPort/$airPlayPort)." }
            2 { Write-Observation 'iPhone connected.' }
            3 {
                if ($null -ne $firstVideo) {
                    Write-Observation ('iPhone disconnected at {0:N1}s. Video frames={1}, audio packets={2}.' -f
                        ($elapsed.Elapsed.TotalSeconds - $firstVideo), $videoCount, $audioCount)
                    break
                }
            }
            4 {
                ++$videoCount
                $latestVideo = $elapsed.Elapsed.TotalSeconds
                if ($null -eq $firstVideo) {
                    $firstVideo = $latestVideo
                    Write-Observation 'First mirrored video frame received.'
                }
                if ($latestVideo - $firstVideo -ge $nextReport) {
                    Write-Observation ('Video still arriving at {0:N1}s. Audio packets={1}.' -f
                        ($latestVideo - $firstVideo), $audioCount)
                    $nextReport += 15
                }
            }
            5 {
                ++$audioCount
                if ($audioCount -eq 1) { Write-Observation 'Receiver received an audio packet despite the advertisement.' }
            }
        }
        if ([BitConverter]::ToUInt16($header, 6) -eq 3 -and $null -ne $firstVideo) { break }
    }
} finally {
    [void]$stop.Set()
    $pipe.Dispose()
    if ($null -ne $process) {
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    $stop.Dispose()
}
