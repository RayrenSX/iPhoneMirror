[CmdletBinding()]
param(
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }

function Send-AirPlayRequest([string]$Target, [int]$CSeq) {
    $request = "POST $Target HTTP/1.1`r`n" +
        "Host: 127.0.0.1:7001`r`nCSeq: $CSeq`r`n" +
        "User-Agent: iPhoneMirror-Smoke/1.0`r`n" +
        "Content-Length: 0`r`nConnection: close`r`n`r`n"
    $bytes = [Text.Encoding]::ASCII.GetBytes($request)
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 5000
        $client.SendTimeout = 5000
        $client.Connect('127.0.0.1', 7001)
        $stream = $client.GetStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
        $buffer = [byte[]]::new(4096)
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $response = [Text.Encoding]::ASCII.GetString($buffer, 0, $count)
        if ($response -notmatch '^HTTP/1\.1 200 ') {
            throw "Request $Target failed: $response"
        }
    }
    finally {
        $client.Dispose()
    }
}

$log = Join-Path $Root 'work\airplay-media-control-smoke.log'
Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
$previousLog = $env:IPHONE_MIRROR_LOG_FILE
$env:IPHONE_MIRROR_LOG_FILE = $log
$process = $null
try {
    $process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $pauseSent = $false
    do {
        try {
            Send-AirPlayRequest '/rate?value=0.000000' 1
            $pauseSent = $true
        }
        catch { Start-Sleep -Milliseconds 250 }
    } while (-not $pauseSent -and [DateTime]::UtcNow -lt $deadline)
    if (-not $pauseSent) { throw 'AirPlay port 7001 did not accept media control.' }

    Send-AirPlayRequest '/scrub?position=12.500000' 2
    Send-AirPlayRequest '/rate?value=1.000000' 3

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $logText = if (Test-Path -LiteralPath $log) {
            Get-Content -LiteralPath $log -Raw
        } else { '' }
        $pauseSeen = $logText.Contains('media_control type=3')
        # Host log chunks can split a long native line, so permit whitespace
        # inside the formatted position while still checking the exact seek.
        $seekSeen = $logText -match
            '(?s)media_control type=5.*?position=12\.\s*500'
        $resumeSeen = $logText.Contains('media_control type=4')
    } while ((-not $pauseSeen -or -not $seekSeen -or -not $resumeSeen) -and
        [DateTime]::UtcNow -lt $deadline)
    if (-not $pauseSeen -or -not $seekSeen -or -not $resumeSeen) {
        throw "AirPlay media-control IPC was incomplete:`n$logText"
    }

    [pscustomobject]@{
        PauseIpcReceived = $pauseSeen
        SeekIpcReceived = $seekSeen
        ResumeIpcReceived = $resumeSeen
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(12000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
    $env:IPHONE_MIRROR_LOG_FILE = $previousLog
}
