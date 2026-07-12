[CmdletBinding()]
param(
    [int]$ResolutionIndex = 3,
    [int]$FrameRateIndex = 3,
    [int]$StreamSeconds = 16
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
$Log = Join-Path $env:TEMP 'iPhoneMirror-capture.log'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Find-ById([System.Windows.Automation.AutomationElement]$RootElement, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ById([System.Windows.Automation.AutomationElement]$RootElement, [string]$Id,
    [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-ById $RootElement $Id
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Automation element not found: $Id"
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    $id = $Element.Current.AutomationId
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($Element.Current.IsEnabled) {
            try {
                $pattern = $Element.GetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                return
            }
            catch [System.Windows.Automation.ElementNotEnabledException] {
                # The two-second device refresh can change command state
                # between IsEnabled and Invoke; retry on the next idle slice.
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Automation element stayed disabled: $id"
}

function Select-ComboIndex([System.Windows.Automation.AutomationElement]$Combo, [int]$Index) {
    if ($Index -lt 0) { throw "Combo index cannot be negative: $Index" }
    # WPF hosts popup list items under a separate HWND, so they are not always
    # descendants of the ComboBox automation peer. Keyboard selection is
    # deterministic and exercises the same binding path as a real user.
    $Combo.SetFocus()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    for ($i = 0; $i -lt $Index; ++$i) {
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 200
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Release executable not found: $Exe" }

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
    Start-Sleep -Seconds 5

    $resolution = Wait-ById $window 'ResolutionComboBox'
    $frameRate = Wait-ById $window 'FrameRateComboBox'
    Select-ComboIndex $resolution $ResolutionIndex
    Select-ComboIndex $frameRate $FrameRateIndex
    Invoke-Element (Wait-ById $window 'ApplyVideoSettingsButton')

    $volume = Wait-ById $window 'VolumeSlider'
    $range = $volume.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $range.SetValue(35)

    $captureAction = Wait-ById $window 'CaptureActionButton'
    Invoke-Element $captureAction
    Start-Sleep -Seconds 7

    $audio = Wait-ById $window 'AudioPlaybackCheckBox'
    $toggle = $audio.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    Start-Sleep -Milliseconds 600
    $toggle.Toggle()

    # The detached preview is also the OBS Window Capture surface.
    Invoke-Element (Wait-ById $window 'PreviewWindowButton')
    Start-Sleep -Seconds $StreamSeconds
    Invoke-Element (Wait-ById $window 'RefreshPreviewButton')
    Invoke-Element (Wait-ById $window 'ScreenshotButton')
    Start-Sleep -Seconds 2
    # The same top-right control changes from Start to the red stop state.
    # Resolve it again because WPF can recreate an automation peer when a
    # command/style trigger changes.
    Invoke-Element (Wait-ById $window 'CaptureActionButton')
    Start-Sleep -Seconds 3
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}

if (Test-Path -LiteralPath $Log) {
    Get-Content -LiteralPath $Log -Tail 500 |
        Select-String -Pattern 'video preferences|local_render|render_texture|present_fps|audio playback_enabled|audio volume|preview refresh|capture_run stop|capture_run exception|d3d_preview error'
}
