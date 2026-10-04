[CmdletBinding()]
param(
    [string]$Exe = '',
    [int]$ControlTimeoutSeconds = 120,
    [ValidateSet('UsbControlButton', 'WirelessControlButton')]
    [string]$ControlAutomationId = 'UsbControlButton',
    [int]$ExistingProcessId = 0,
    [switch]$SkipCaptureStart,
    [string]$EvidenceDirectory = ''
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path (Split-Path -Parent $scriptRoot) 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path (Split-Path -Parent $scriptRoot) 'work\pasteboard-uia'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Executable not found: $Exe" }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$evidence = Join-Path $EvidenceDirectory ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

function Find-Control([int]$ProcessId, [string]$AutomationId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $idCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) {
        $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
        if ($null -ne $found) { return $found }
    }
    return $null
}

function Wait-Control([int]$ProcessId, [string]$AutomationId, [int]$Seconds = $ControlTimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $control = Find-Control $ProcessId $AutomationId
        if ($null -ne $control -and $control.Current.IsEnabled) { return $control }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out locating enabled UIA control: $AutomationId"
}

function Invoke-Control([System.Windows.Automation.AutomationElement]$Control) {
    $Control.SetFocus()
    $Control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Set-WindowsClipboard([string]$Text) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Text))
    $command = "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::SetText([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded')))"
    & powershell.exe -NoProfile -STA -Command $command
    if ($LASTEXITCODE -ne 0) { throw "Failed to set Windows clipboard" }
}

function Get-WindowsClipboard {
    $command = "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::GetText()"
    return (& powershell.exe -NoProfile -STA -Command $command | Out-String).TrimEnd()
}

$ownsProcess = $ExistingProcessId -eq 0
$process = if ($ownsProcess) {
    Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
} else {
    Get-Process -Id $ExistingProcessId -ErrorAction Stop
}
try {
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq 0 -and -not $process.HasExited -and
        [DateTime]::UtcNow -lt $windowDeadline)
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw 'Main application window did not become ready' }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $device = Wait-Control $process.Id 'SelectedDeviceUdidText' 60
    $udid = $device.Current.Name
    if ([string]::IsNullOrWhiteSpace($udid)) { throw 'Selected device UDID is empty' }
    $udid | Set-Content -LiteralPath (Join-Path $evidence 'udid.txt')

    if (-not $SkipCaptureStart) {
        $capture = Wait-Control $process.Id 'CaptureActionButton' 30
        Invoke-Control $capture
        $captureDeadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 500
            $capture = Find-Control $process.Id 'CaptureActionButton'
            if ($null -ne $capture -and $capture.Current.Name -match 'Stop|停止') { break }
        } while ([DateTime]::UtcNow -lt $captureDeadline)
    }

    $control = Wait-Control $process.Id $ControlAutomationId $ControlTimeoutSeconds
    $wirelessAlreadyReady = $ControlAutomationId -eq 'WirelessControlButton' -and
        $control.Current.HelpText -match 'Disable|关闭|關閉'
    if (-not $wirelessAlreadyReady) {
        Invoke-Control $control
    }
    $prompt = Find-Control $process.Id 'ControlPromptPrimaryButton'
    if ($null -ne $prompt -and $prompt.Current.IsEnabled) { Invoke-Control $prompt }
    $statusDeadline = [DateTime]::UtcNow.AddSeconds($ControlTimeoutSeconds)
    $controlReady = $false
    if ($ControlAutomationId -eq 'WirelessControlButton') {
        $statusDeadline = [DateTime]::UtcNow.AddSeconds($ControlTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 500
            $control = Find-Control $process.Id $ControlAutomationId
            $controlReady = $null -ne $control -and $control.Current.IsEnabled -and
                $control.Current.HelpText -match 'Disable|关闭|關閉'
            if ($controlReady) { break }
        } while ([DateTime]::UtcNow -lt $statusDeadline)
    } else {
        do {
            Start-Sleep -Milliseconds 500
            $control = Find-Control $process.Id $ControlAutomationId
            if ($null -ne $control) { $controlReady = ($control.Current.ItemStatus -match 'enabled|Connected') }
            if ($controlReady) { break }
        } while ([DateTime]::UtcNow -lt $statusDeadline)
    }
    if (-not $controlReady) {
        $status = 'missing'
        if ($null -ne $control -and $ControlAutomationId -eq 'WirelessControlButton') { $status = $control.Current.Name }
        elseif ($null -ne $control) { $status = $control.Current.ItemStatus }
        throw "$ControlAutomationId did not become ready: $status"
    }

    $preview = Wait-Control $process.Id 'MainPreviewHost' 30
    $payloads = @('iPhoneMirror-Test-001', 'iPhoneMirror-Test-002', 'iPhoneMirror-Test-003')
    $records = [Collections.Generic.List[object]]::new()
    foreach ($text in $payloads) {
        Set-WindowsClipboard $text
        $preview.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('^v')
        Start-Sleep -Seconds 3
        $records.Add([pscustomobject]@{
            direction = 'windows_to_ios'
            text = $text
            windowsClipboard = Get-WindowsClipboard
            sent = $true
        })
    }
    $records | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'results.json')
    [pscustomobject]@{
        Status = 'PASS_HID_AND_UI_SUBMISSION'
        DeviceUdid = $udid
        PayloadCount = $payloads.Count
        Evidence = $evidence
        Note = 'iOS-side clipboard readback requires a device-side copy/read action; app and bridge logs are preserved for that verification.'
    } | ConvertTo-Json -Depth 4
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'failure.txt')
    [pscustomobject]@{ Status = 'FAIL'; Evidence = $evidence; Error = $_.Exception.Message } | ConvertTo-Json -Depth 4
    throw
}
finally {
    if ($ownsProcess -and $null -ne $process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force }
    }
    $logRoot = Join-Path $env:LOCALAPPDATA 'iPhoneMirror\Logs'
    foreach ($name in @('application.log', 'capture.log', 'reverse-control.log')) {
        $source = Join-Path $logRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $evidence $name) -Force
        }
    }
}
