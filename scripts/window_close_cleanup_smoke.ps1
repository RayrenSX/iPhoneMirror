[CmdletBinding()]
param(
    [string]$Exe,
    [int]$CaptureSeconds = 6
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
$Log = Join-Path $env:TEMP 'iPhoneMirror-capture.log'
$logOffset = if (Test-Path -LiteralPath $Log) { (Get-Item $Log).Length } else { 0 }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ById(
    [System.Windows.Automation.AutomationElement]$RootElement,
    [string]$Id,
    [int]$TimeoutSeconds = 15) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Automation element not found: $Id"
}

function Read-LogSuffix([long]$Offset) {
    if (-not (Test-Path -LiteralPath $Log)) { return '' }
    $stream = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
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
        throw 'Main GUI window did not become ready'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $action = Find-ById $window 'CaptureActionButton'
    Start-Sleep -Seconds 4
    $idleName = $action.Current.Name
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($action.Current.IsEnabled) {
            $invoke = $action.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            break
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Capture action never became enabled.' }

    # Ownership is published before native USB activation, so the single
    # action must enter its red stop state without waiting for PING/video.
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $activeName = $action.Current.Name
        if (-not [string]::Equals($activeName, $idleName,
                [StringComparison]::Ordinal)) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([string]::Equals($activeName, $idleName, [StringComparison]::Ordinal)) {
        throw 'Capture action did not switch to its stop state.'
    }

    Start-Sleep -Seconds $CaptureSeconds
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(35000)) {
        throw 'Window did not complete protocol cleanup and exit within 35 seconds.'
    }

    Start-Sleep -Milliseconds 500
    $suffix = Read-LogSuffix $logOffset
    $starts = ([regex]::Matches($suffix, 'im_start_capture udid=')).Count
    if ($starts -ne 1) { throw "Expected exactly one start request, found $starts." }
    if ($suffix -notmatch 'shutdown_usb handshake_started=.* stop_messages=') {
        throw 'Window close did not send the QuickTime shutdown controls.'
    }
    if ($suffix -notmatch 'im_shutdown') {
        throw 'Window close did not dispose the native core.'
    }

    [pscustomobject]@{
        IdleButton = $idleName
        ActiveButton = $activeName
        StartRequests = $starts
        QuickTimeShutdownLogged = $true
        CoreShutdownLogged = $true
        CleanExit = $true
    }
}
finally {
    if (!$process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
