[CmdletBinding()]
param(
    [string]$Exe,
    [int]$StreamingSeconds = 8
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

function Get-ListItems([System.Windows.Automation.AutomationElement]$List) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($List.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
}

function Select-Item([System.Windows.Automation.AutomationElement]$Item) {
    $pattern = $Item.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Invoke-WhenEnabled(
    [System.Windows.Automation.AutomationElement]$Element,
    [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Element.Current.IsEnabled) {
            $pattern = $Element.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Control did not become enabled: $($Element.Current.AutomationId)"
}

function Wait-ActionState(
    [System.Windows.Automation.AutomationElement]$Action,
    [string]$IdleName,
    [bool]$Capturing,
    [int]$TimeoutSeconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $name = $Action.Current.Name
        # Avoid matching localized source literals: Windows PowerShell 5 can
        # read a UTF-8-without-BOM script through the active ANSI code page.
        $activeName = -not [string]::Equals(
            $name, $IdleName, [StringComparison]::Ordinal)
        if ($activeName -eq $Capturing) { return $name }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Capture action did not enter expected state (capturing=$Capturing); name=$($Action.Current.Name)"
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
    $list = Find-ById $window 'DeviceListBox'
    $action = Find-ById $window 'CaptureActionButton'
    Start-Sleep -Seconds 4
    $items = Get-ListItems $list
    if ($items.Count -lt 2) {
        throw "Capture-switch smoke requires two devices; found $($items.Count)."
    }

    # Start on the most recently appended device, then select the first phone.
    $source = $items[$items.Count - 1]
    $target = $items[0]
    $sourceUdid = $source.Current.AutomationId
    $targetUdid = $target.Current.AutomationId
    $idleActionName = $action.Current.Name
    Select-Item $source
    Start-Sleep -Milliseconds 500
    Invoke-WhenEnabled $action
    $activeLabel = Wait-ActionState $action $idleActionName $true
    Start-Sleep -Seconds $StreamingSeconds

    # This click must synchronously complete native StopCapture/HPA0/HPD0
    # before the target device controls become available.
    Select-Item $target
    $idleLabel = Wait-ActionState $action $idleActionName $false 30
    Start-Sleep -Seconds 2

    $newLog = if (Test-Path -LiteralPath $Log) {
        $stream = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite)
        try {
            [void]$stream.Seek($logOffset, [IO.SeekOrigin]::Begin)
            $reader = [IO.StreamReader]::new($stream)
            try { $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    } else { '' }
    if ($newLog -notmatch 'shutdown_usb handshake_started=.* stop_messages=') {
        throw 'The device switch did not log the mandatory QuickTime shutdown sequence.'
    }

    [pscustomobject]@{
        SourceUdid = $sourceUdid
        TargetUdid = $targetUdid
        ActiveButton = $activeLabel
        IdleButton = $idleLabel
        QuickTimeShutdownLogged = $true
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(20000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
