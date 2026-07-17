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

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Send-AvTransport([string]$Action, [string]$Arguments = '') {
    $body = '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">' +
        '<s:Body><u:' + $Action +
        ' xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">' +
        '<InstanceID>0</InstanceID>' + $Arguments + '</u:' + $Action +
        '></s:Body></s:Envelope>'
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
    $header = "POST /dlna/control/avtransport HTTP/1.1`r`n" +
        "Host: 127.0.0.1:8090`r`nContent-Type: text/xml`r`n" +
        "SOAPACTION: `"urn:schemas-upnp-org:service:AVTransport:1#$Action`"`r`n" +
        "Content-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.SendTimeout = 5000
        $client.ReceiveTimeout = 5000
        $client.NoDelay = $true
        $client.Connect('127.0.0.1', 8090)
        $stream = $client.GetStream()
        $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        $stream.Write($bodyBytes, 0, $bodyBytes.Length)
        $stream.Flush()
        $buffer = [byte[]]::new(4096)
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $response = [Text.Encoding]::UTF8.GetString($buffer, 0, $count)
        if ($response -notmatch '^HTTP/1\.1 200 ') {
            throw "$Action failed: $response"
        }
    }
    finally {
        $client.Dispose()
    }
}

$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq 0 -and -not $process.HasExited -and
        [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Main window did not start.' }

    $receiverReady = $false
    $lastReceiverError = 'receiver did not accept a request'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            Send-AvTransport SetAVTransportURI `
                '<CurrentURI>https://example.test/integrated-preview.mp4</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>'
            $receiverReady = $true
        }
        catch {
            $lastReceiverError = $_.Exception.Message
            Start-Sleep -Milliseconds 250
        }
    } while (-not $receiverReady -and [DateTime]::UtcNow -lt $deadline)
    if (-not $receiverReady) {
        throw "DLNA HTTP receiver did not become ready: $lastReceiverError"
    }

    Send-AvTransport Play '<Speed>1</Speed>'

    $surfaceCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'CloseMediaCastButton')
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $window = [Windows.Automation.AutomationElement]::FromHandle(
            $process.MainWindowHandle)
        $surface = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $surfaceCondition)
        if ($null -eq $surface) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $surface -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $surface) {
        throw 'Integrated media surface is not visible.'
    }

    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $processCondition)
    if ($windows.Count -ne 1) {
        throw "Expected one application window, found $($windows.Count)."
    }

    Send-AvTransport Stop
    Start-Sleep -Seconds 1
    $surface = $window.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $surfaceCondition)
    $hidden = $null -eq $surface
    if (-not $hidden) { throw 'Integrated media surface remained visible after Stop.' }

    [pscustomobject]@{
        IntegratedSurfaceVisible = $true
        TopLevelWindowCount = $windows.Count
        SurfaceHiddenAfterStop = $hidden
    }
}
finally {
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(12000)) { Stop-Process -Id $process.Id -Force }
    }
}
