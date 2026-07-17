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

$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq 0 -and !$process.HasExited -and
        [DateTime]::UtcNow -lt $deadline)
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) {
        throw 'Main GUI window did not become ready.'
    }

    $hostProcess = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $hostProcess = Get-Process -Name 'iPhoneMirror.WirelessHost' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } while ($null -eq $hostProcess -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $hostProcess) { throw 'Wireless host process did not start.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $ports = @()
    do {
        Start-Sleep -Milliseconds 250
        $ports = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object {
                $_.OwningProcess -eq $hostProcess.Id -and $_.LocalPort -in @(5001, 7001, 8090)
            } | Select-Object -ExpandProperty LocalPort -Unique)
    } while ($ports.Count -lt 3 -and [DateTime]::UtcNow -lt $deadline)
    if (5001 -notin $ports -or 7001 -notin $ports -or 8090 -notin $ports) {
        throw "Wireless host did not listen on AirPlay and DLNA ports: $($ports -join ', ')"
    }

    $udp = [Net.Sockets.UdpClient]::new(
        [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0))
    $udp.Client.ReceiveTimeout = 5000
    try {
        $search = "M-SEARCH * HTTP/1.1`r`nHOST: 239.255.255.250:1900`r`n" +
            "MAN: `"ssdp:discover`"`r`nMX: 1`r`n" +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1`r`n`r`n"
        $searchBytes = [Text.Encoding]::ASCII.GetBytes($search)
        $multicast = [Net.IPEndPoint]::new(
            [Net.IPAddress]::Parse('239.255.255.250'), 1900)
        [void]$udp.Send($searchBytes, $searchBytes.Length, $multicast)
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $response = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$remote))
    }
    finally {
        $udp.Dispose()
    }
    $locationMatch = [regex]::Match($response, '(?im)^LOCATION:\s*(\S+)\s*$')
    $rendererMatch = [regex]::Match($response,
        '(?im)^ST:\s*urn:schemas-upnp-org:device:MediaRenderer:1\s*$')
    if (-not $locationMatch.Success -or -not $rendererMatch.Success) {
        throw "DLNA MediaRenderer discovery response was invalid: $response"
    }
    $location = $locationMatch.Groups[1].Value
    $description = (Invoke-WebRequest -UseBasicParsing -Uri $location -TimeoutSec 5).Content
    if ($description -notmatch '<friendlyName>iPhoneMirror Video</friendlyName>' -or
        $description -notmatch 'urn:schemas-upnp-org:service:AVTransport:1') {
        throw 'DLNA device description did not advertise the expected video renderer.'
    }

    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(12000)) {
        throw 'Main application did not exit cleanly.'
    }
    if (-not $hostProcess.WaitForExit(8000)) {
        throw 'Wireless host did not exit after the main application closed.'
    }

    [pscustomobject]@{
        HostStarted = $true
        Ports = ($ports | Sort-Object) -join ', '
        DlnaLocation = $location
        DlnaDiscovered = $true
        HostExitedAfterStop = $true
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) { Stop-Process -Id $process.Id -Force }
    }
}
