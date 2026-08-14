[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Udid,
    [int]$Cycles = 3,
    [int]$StreamingSeconds = 5,
    [ValidateRange(10, 60)]
    [int]$PostExitObservationSeconds = 12
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
$Log = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) `
    'iPhoneMirror\Logs\capture.log'
$logOffset = if (Test-Path -LiteralPath $Log) { (Get-Item -LiteralPath $Log).Length } else { 0 }
$testStart = Get-Date

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

function Normalize-Udid([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value -replace '[-\s]', '').ToUpperInvariant()
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

function Assert-TargetSelected(
    [System.Windows.Automation.AutomationElement]$Window,
    [string]$ExpectedUdid) {
    $selected = Find-ById $Window 'SelectedDeviceUdidText' 2
    $actualUdid = $selected.Current.Name
    if (-not [string]::Equals((Normalize-Udid $actualUdid),
            (Normalize-Udid $ExpectedUdid), [StringComparison]::Ordinal)) {
        throw "Target device is no longer selected or visible: expected $ExpectedUdid, got $actualUdid"
    }
    return $actualUdid
}

function Invoke-TargetAction(
    [System.Windows.Automation.AutomationElement]$Window,
    [string]$ExpectedUdid,
    [int]$TimeoutSeconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        [void](Assert-TargetSelected $Window $ExpectedUdid)
        # The command binding and selected device can both be replaced during
        # refresh. Resolve both immediately before every attempted invocation.
        $element = Find-ById $Window 'CaptureActionButton' 2
        [void](Assert-TargetSelected $Window $ExpectedUdid)
        if ($element.Current.IsEnabled) {
            try {
                $pattern = $element.GetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                return
            } catch [System.Windows.Automation.ElementNotEnabledException] {
                # Device refresh can update command state between the two reads.
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Capture action did not become enabled for target device: $ExpectedUdid"
}

function Assert-NoTargetFailure([string]$LogText, [string]$DeviceToken,
    [string]$Handle = '') {
    $escapedDevice = [regex]::Escape($DeviceToken)
    if ($LogText -match
            "capture_error_release_begin device=$escapedDevice(?:\s|$)" -or
        $LogText -match
            "capture_start_failed device=$escapedDevice(?:\s|$)") {
        throw "The target capture entered error teardown."
    }
    if (-not [string]::IsNullOrWhiteSpace($Handle)) {
        $escapedHandle = [regex]::Escape($Handle)
        if ($LogText -match
                "capture_state device=$escapedDevice handle=$escapedHandle state=Error(?:\s|$)" -or
            $LogText -match
                "capture_stop_failed device=$escapedDevice handle=$escapedHandle(?:\s|$)") {
            throw "The target session $Handle entered an error state."
        }
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

function Get-PnpParentId([string]$InstanceId) {
    $property = Get-PnpDeviceProperty -InstanceId $InstanceId `
        -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue
    if ($null -eq $property) { return '' }
    return [string]$property.Data
}

function Test-PnpDescendantOf([string]$InstanceId, [string]$ParentId) {
    $current = $InstanceId
    for ($depth = 0; $depth -lt 16; ++$depth) {
        $current = Get-PnpParentId $current
        if ([string]::IsNullOrWhiteSpace($current)) { return $false }
        if ([string]::Equals($current, $ParentId,
                [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Assert-TargetPnpHealthy([string]$ExpectedUdid) {
    $normalized = Normalize-Udid $ExpectedUdid
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)
    $parents = @($devices | Where-Object {
        $_.InstanceId -match '^USB\\VID_05AC&PID_[^\\]+\\([^\\]+)$' -and
        $_.InstanceId -notmatch '&MI_' -and
        (Normalize-Udid ([regex]::Match(
            $_.InstanceId, '^USB\\[^\\]+\\([^\\]+)$').Groups[1].Value)) -eq
            $normalized
    })
    if ($parents.Count -ne 1) {
        throw "Expected one present Apple parent for $ExpectedUdid; found $($parents.Count)."
    }
    $parent = $parents[0]
    if ($parent.Status -ne 'OK' -or $parent.Problem -ne 'CM_PROB_NONE') {
        throw "The target Apple parent is unhealthy: status=$($parent.Status), problem=$($parent.Problem)."
    }

    foreach ($interface in '00', '01') {
        $children = @($devices | Where-Object {
            $_.InstanceId -match "&MI_$interface\\" -and
            (Test-PnpDescendantOf $_.InstanceId $parent.InstanceId)
        })
        $healthy = @($children | Where-Object {
            $_.Status -eq 'OK' -and $_.Problem -eq 'CM_PROB_NONE'
        })
        if ($healthy.Count -lt 1) {
            $diagnostic = ($children | ForEach-Object {
                "$($_.InstanceId):status=$($_.Status),problem=$($_.Problem)"
            }) -join '; '
            throw "Target Apple MI_$interface did not recover cleanly: $diagnostic"
        }
    }
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
if ($Cycles -lt 1 -or $Cycles -gt 5) { throw 'Cycles must be between 1 and 5.' }

$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$cycleResults = [System.Collections.Generic.List[object]]::new()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
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
    Start-Sleep -Seconds 4
    $itemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = @($list.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $itemCondition))
    if ($items.Count -lt 1) { throw 'Capture restart smoke requires one wired device.' }
    if (-not [string]::IsNullOrWhiteSpace($Udid)) {
        $normalizedWanted = Normalize-Udid $Udid
        $targetFound = $false
        foreach ($candidate in $items) {
            $selection = $candidate.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selection.Select()
            Start-Sleep -Milliseconds 300
            $selectedUdidText = Find-ById $window 'SelectedDeviceUdidText' 2
            $candidateUdid = $selectedUdidText.Current.Name
            if ([string]::Equals((Normalize-Udid $candidateUdid),
                    $normalizedWanted, [StringComparison]::OrdinalIgnoreCase)) {
                $targetFound = $true
                break
            }
        }
        if (-not $targetFound) {
            throw "Requested wired device is not visible: $Udid"
        }
    }
    $selectedUdidText = Find-ById $window 'SelectedDeviceUdidText' 2
    $selectedUdid = $selectedUdidText.Current.Name
    if (-not [string]::IsNullOrWhiteSpace($Udid) -and
        -not [string]::Equals((Normalize-Udid $selectedUdid),
            (Normalize-Udid $Udid),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Requested wired device was not selected: expected $Udid, got $selectedUdid"
    }
    [void](Assert-TargetSelected $window $selectedUdid)
    $targetDeviceToken = Get-DeviceLogToken $selectedUdid
    $escapedTargetDevice = [regex]::Escape($targetDeviceToken)

    for ($cycle = 1; $cycle -le $Cycles; ++$cycle) {
        [void](Assert-TargetSelected $window $selectedUdid)
        $cycleLogStart = if (Test-Path -LiteralPath $Log) {
            (Get-Item -LiteralPath $Log).Length
        } else { 0 }
        $idleAction = Find-ById $window 'CaptureActionButton' 2
        $idleName = $idleAction.Current.Name
        Invoke-TargetAction $window $selectedUdid
        $activeDeadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 150
            [void](Assert-TargetSelected $window $selectedUdid)
            $activeAction = Find-ById $window 'CaptureActionButton' 2
            $activeName = $activeAction.Current.Name
            $cycleLog = Read-LogSuffix $cycleLogStart
            Assert-NoTargetFailure $cycleLog $targetDeviceToken
        } while ([string]::Equals($activeName, $idleName,
            [StringComparison]::Ordinal) -and [DateTime]::UtcNow -lt $activeDeadline)
        if ([string]::Equals($activeName, $idleName,
            [StringComparison]::Ordinal)) {
            throw "Cycle $cycle did not enter the active capture state."
        }

        $sessionHandle = ''
        $nativeDeviceFingerprint = ''
        $streamDeadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 250
            [void](Assert-TargetSelected $window $selectedUdid)
            $cycleLog = Read-LogSuffix $cycleLogStart
            Assert-NoTargetFailure $cycleLog $targetDeviceToken $sessionHandle
            if ([string]::IsNullOrWhiteSpace($sessionHandle)) {
                $startMatch = [regex]::Match($cycleLog,
                    "capture_start_result device=$escapedTargetDevice success=True handle=(h[0-9a-f]+)(?:\s|$)")
                if ($startMatch.Success) {
                    $sessionHandle = $startMatch.Groups[1].Value
                    $handleNumber = [Convert]::ToUInt64($sessionHandle.Substring(1), 16)
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($sessionHandle) -and
                [string]::IsNullOrWhiteSpace($nativeDeviceFingerprint)) {
                $fingerprintMatch = [regex]::Match($cycleLog,
                    "multi_session create handle=$handleNumber udid_fp=([^\s]+)")
                if ($fingerprintMatch.Success) {
                    $nativeDeviceFingerprint = $fingerprintMatch.Groups[1].Value
                }
            }
            $streaming = -not [string]::IsNullOrWhiteSpace($sessionHandle) -and
                $cycleLog -match
                    "capture_state device=$escapedTargetDevice handle=$([regex]::Escape($sessionHandle)) state=Streaming(?:\s|$)"
        } while (-not $streaming -and [DateTime]::UtcNow -lt $streamDeadline)
        if (-not $streaming) { throw "Cycle $cycle did not reach Streaming." }
        if ([string]::IsNullOrWhiteSpace($nativeDeviceFingerprint)) {
            throw "Cycle $cycle could not correlate the target session to its native USB identity."
        }
        $streamUntil = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $StreamingSeconds))
        do {
            Start-Sleep -Milliseconds 250
            [void](Assert-TargetSelected $window $selectedUdid)
            $cycleLog = Read-LogSuffix $cycleLogStart
            Assert-NoTargetFailure $cycleLog $targetDeviceToken $sessionHandle
        } while ([DateTime]::UtcNow -lt $streamUntil)

        Invoke-TargetAction $window $selectedUdid
        $escapedHandle = [regex]::Escape($sessionHandle)
        $escapedFingerprint = [regex]::Escape($nativeDeviceFingerprint)
        $stopDeadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 250
            [void](Assert-TargetSelected $window $selectedUdid)
            $stoppedAction = Find-ById $window 'CaptureActionButton' 2
            $stoppedName = $stoppedAction.Current.Name
            $cycleLog = Read-LogSuffix $cycleLogStart
            Assert-NoTargetFailure $cycleLog $targetDeviceToken $sessionHandle
            $stopComplete = $cycleLog -match
                "capture_stop_complete device=$escapedTargetDevice handle=$escapedHandle .*?success=True(?:\s|$)"
            $restoreConfirmed = $cycleLog -match
                "usb_configuration_restore finalized device_fp=$escapedFingerprint normal_observed=true(?:\s|$)"
            $restoreFailed = $cycleLog -match
                "usb_configuration_restore finalized device_fp=$escapedFingerprint normal_observed=false(?:\s|$)"
            if ($restoreFailed) {
                throw "Cycle $cycle did not restore the target phone to its normal USB configuration."
            }
        } while (
            (-not [string]::Equals($stoppedName, $idleName,
                [StringComparison]::Ordinal) -or
             -not $stopComplete -or -not $restoreConfirmed) -and
            [DateTime]::UtcNow -lt $stopDeadline)
        if (-not [string]::Equals($stoppedName, $idleName,
            [StringComparison]::Ordinal)) {
            throw "Cycle $cycle did not return to the idle capture state."
        }
        if (-not $stopComplete) { throw "Cycle $cycle did not report capture_stop_complete success=True." }
        if (-not $restoreConfirmed) {
            throw "Cycle $cycle did not confirm normal USB configuration restoration."
        }
        $shutdownComplete = $cycleLog -match
            "shutdown_usb device_fp=$escapedFingerprint handshake_started=.* stop_messages="
        if (-not $shutdownComplete) { throw "Cycle $cycle did not log the USB shutdown handshake." }
        $cycleResults.Add([pscustomobject]@{
            Cycle = $cycle
            Udid = $selectedUdid
            Streaming = $true
            ShutdownHandshake = $shutdownComplete
            StopComplete = $stopComplete
            NormalConfigurationRestored = $restoreConfirmed
        })
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(35000)) { Stop-Process -Id $process.Id -Force }
    }
}

# UMDF and WER publish AppleUsbFilter failures asynchronously. The previous
# smoke test queried the event log immediately after process exit and could
# therefore report success several seconds before the delayed crash appeared.
Start-Sleep -Seconds $PostExitObservationSeconds

$suffix = Read-LogSuffix $logOffset
$shutdowns = @($cycleResults | Where-Object { $_.ShutdownHandshake }).Count
$streams = $cycleResults.Count
$systemDriverEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $testStart } `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -in 10121, 10116, 10120, 41, 1001, 10110, 10111 })
$applicationDriverEvents = @(Get-WinEvent -FilterHashtable `
    @{ LogName = 'Application'; StartTime = $testStart } -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Id -in 1000, 1001, 1026 -and
        $_.Message -match 'AppleUsbFilter|WUDFHost|iPhoneMirror'
    })
$driverEvents = @($systemDriverEvents) + @($applicationDriverEvents)
if ($streams -lt $Cycles) { throw "Expected at least $Cycles Streaming transitions, observed $streams." }
if ($shutdowns -lt $Cycles) { throw "Expected at least $Cycles shutdown handshakes, observed $shutdowns." }
if ($suffix -match "capture_stop_failed device=$escapedTargetDevice(?:\s|$)") {
    throw 'The capture log contains capture_stop_failed.'
}
if ($driverEvents.Count -ne 0) {
    $eventSummary = ($driverEvents | ForEach-Object {
        "$($_.Id):$($_.ProviderName)@$($_.TimeCreated.ToString('HH:mm:ss'))"
    }) -join ', '
    throw "Driver, device-host, or bugcheck events occurred during the smoke test: $eventSummary"
}
Assert-TargetPnpHealthy $selectedUdid

[pscustomobject]@{
    Cycles = $cycleResults
    StreamingTransitions = $streams
    QuickTimeShutdowns = $shutdowns
    PostExitObservationSeconds = $PostExitObservationSeconds
    DriverOrBugcheckEvents = $driverEvents.Count
}
