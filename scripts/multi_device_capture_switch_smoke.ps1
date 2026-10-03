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
$Log = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) `
    'iPhoneMirror\Logs\capture.log'
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

function Wait-ForDeviceCount(
    [System.Windows.Automation.AutomationElement]$List,
    [int]$Minimum = 2,
    [int]$TimeoutSeconds = 30) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $items = @($List.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $condition))
        if ($items.Count -ge $Minimum) { return $items }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return @($List.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition))
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

function Get-DeviceLogToken([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))
        $token = -join ($bytes[0..4] | ForEach-Object { $_.ToString('x2') })
        return "device#$token"
    } finally {
        $sha.Dispose()
    }
}

function Read-LogSuffix([long]$Offset) {
    if (-not (Test-Path -LiteralPath $Log)) { return '' }
    $stream = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Wait-DeviceStreaming([string]$DeviceToken, [long]$Offset,
    [int]$TimeoutSeconds = 60) {
    $escaped = [regex]::Escape($DeviceToken)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $text = Read-LogSuffix $Offset
        if ($text -match "capture_state device=$escaped handle=h[0-9a-f]+ state=Streaming(?:\s|$)") {
            return $text
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Device $DeviceToken did not reach Streaming before the next USB configuration switch."
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
    # Lockdown can expose the two phones on different refreshes after a
    # previous QuickTime configuration transition. Wait for both cards before
    # touching either device.
    $items = Wait-ForDeviceCount $list 2 30
    if ($items.Count -lt 2) {
        throw "Capture-switch smoke requires two devices; found $($items.Count)."
    }

    # Start on the most recently appended device, then select the first phone.
    $source = $items[$items.Count - 1]
    $target = $items[0]
    $selectedUdidText = Find-ById $window 'SelectedDeviceUdidText'
    $idleActionName = $action.Current.Name
    Select-Item $source
    Start-Sleep -Milliseconds 500
    $sourceUdid = $selectedUdidText.Current.Name
    $sourceToken = Get-DeviceLogToken $sourceUdid
    Invoke-WhenEnabled $action
    $activeLabel = Wait-ActionState $action $idleActionName $true
    # Do not start the second phone while the first one is still negotiating
    # its QuickTime endpoint. That concurrent configuration switch can leave
    # one device in WaitingForDevice and makes the result unsafe to interpret.
    $sourceLog = Wait-DeviceStreaming $sourceToken $logOffset 60
    Start-Sleep -Seconds ([Math]::Max(4, [Math]::Floor($StreamingSeconds / 2)))

    # Selection is independent of session ownership: the first stream must
    # remain alive while the second device starts its own native session.
    Select-Item $target
    $targetIdleLabel = Wait-ActionState $action $idleActionName $false 30
    Start-Sleep -Milliseconds 500
    $targetUdid = $selectedUdidText.Current.Name
    $targetToken = Get-DeviceLogToken $targetUdid
    Invoke-WhenEnabled $action
    $targetActiveLabel = Wait-ActionState $action $idleActionName $true
    # Both streams must be producing frames before any stop or selection
    # change is attempted.
    $combinedLog = Wait-DeviceStreaming $targetToken $logOffset 60
    $bothDeadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $combinedLog = Read-LogSuffix $logOffset
        $sourceStreaming = $combinedLog -match
            "capture_state device=$([regex]::Escape($sourceToken)) handle=h[0-9a-f]+ state=Streaming(?:\s|$)"
        $targetStreaming = $combinedLog -match
            "capture_state device=$([regex]::Escape($targetToken)) handle=h[0-9a-f]+ state=Streaming(?:\s|$)"
        if ($sourceStreaming -and $targetStreaming) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $bothDeadline)
    if (-not ($sourceStreaming -and $targetStreaming)) {
        throw "Both devices were not Streaming; stopping or switching was skipped."
    }
    Start-Sleep -Seconds $StreamingSeconds

    # Stop the selected target, then return to the source. Its button must
    # still represent an active session until it is explicitly stopped.
    Invoke-WhenEnabled $action
    $targetStoppedLabel = Wait-ActionState $action $idleActionName $false 45
    Select-Item $source
    $sourceStillActiveLabel = Wait-ActionState $action $idleActionName $true 15
    Invoke-WhenEnabled $action
    $sourceStoppedLabel = Wait-ActionState $action $idleActionName $false 45
    Start-Sleep -Seconds 2

    $newLog = Read-LogSuffix $logOffset
    $createdHandles = @([regex]::Matches($newLog,
        'multi_session create handle=(\d+)') | ForEach-Object {
            $_.Groups[1].Value
        } | Select-Object -Unique)
    if ($createdHandles.Count -lt 2) {
        throw "Concurrent capture did not create two distinct sessions: $($createdHandles -join ',')."
    }
    $streamingStates = [regex]::Matches($newLog,
        'capture_state .*?handle=h\d+ state=Streaming').Count
    if ($streamingStates -lt 2) {
        throw "Both devices did not independently reach Streaming; observed $streamingStates state transitions."
    }
    $shutdowns = [regex]::Matches($newLog,
        'shutdown_usb device_fp=[^ ]+ handshake_started=.*?stop_messages=').Count
    if ($shutdowns -lt 2) {
        throw "Expected two QuickTime shutdown handshakes; observed $shutdowns."
    }

    [pscustomobject]@{
        SourceUdid = $sourceUdid
        TargetUdid = $targetUdid
        SourceActiveButton = $activeLabel
        TargetIdleButton = $targetIdleLabel
        TargetActiveButton = $targetActiveLabel
        TargetStoppedButton = $targetStoppedLabel
        SourceStillActiveButton = $sourceStillActiveLabel
        SourceStoppedButton = $sourceStoppedLabel
        SessionHandles = $createdHandles -join ','
        StreamingTransitions = $streamingStates
        QuickTimeShutdowns = $shutdowns
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
