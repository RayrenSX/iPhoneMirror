[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $Root 'outputs\diagnostics'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCaptureNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

function Find-ById(
    [System.Windows.Automation.AutomationElement]$RootElement,
    [string]$Id,
    [int]$TimeoutSeconds = 10) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Automation element not found: $Id"
}

function Click-Element([System.Windows.Automation.AutomationElement]$Element) {
    $bounds = $Element.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "Automation element has no clickable bounds: $($Element.Current.AutomationId)"
    }

    $x = [int][Math]::Round($bounds.Left + ($bounds.Width / 2))
    $y = [int][Math]::Round($bounds.Top + ($bounds.Height / 2))
    if (-not [WindowCaptureNative]::SetCursorPos($x, $y)) {
        throw "SetCursorPos failed for automation element: $($Element.Current.AutomationId)"
    }

    Start-Sleep -Milliseconds 100
    [WindowCaptureNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WindowCaptureNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Save-WindowCapture([IntPtr]$Handle, [string]$Path) {
    $rect = [WindowCaptureNative+RECT]::new()
    if (-not [WindowCaptureNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed'
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

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
    $expectedProcessPath = (Resolve-Path -LiteralPath $Exe).Path
    $actualProcessPath = (Get-Process -Id $process.Id).Path
    if (-not [string]::Equals($expectedProcessPath, $actualProcessPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected GUI process path: $actualProcessPath"
    }

    [void][WindowCaptureNative]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Seconds 4
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)

    $captureAction = Find-ById $window 'CaptureActionButton'
    $deviceToggle = Find-ById $window 'DevicePanelToggle'
    $settingsToggle = Find-ById $window 'SettingsPanelToggle'
    $actionBounds = $captureAction.Current.BoundingRectangle

    $mainPath = Join-Path $OutputDirectory 'ui-polish-main.png'
    Save-WindowCapture $process.MainWindowHandle $mainPath

    Click-Element $settingsToggle
    Start-Sleep -Milliseconds 500
    $language = Find-ById $window 'LanguageComboBox'
    $settingsPath = Join-Path $OutputDirectory 'ui-polish-settings.png'
    Save-WindowCapture $process.MainWindowHandle $settingsPath

    try {
        $combo = Find-ById $window 'ResolutionComboBox' 2
    }
    catch {
        # Source mode intentionally hides the wired render-limit selector.
        # Exercise the always-available wireless resolution selector instead.
        $combo = Find-ById $window 'WirelessResolutionComboBox'
    }
    $expand = $combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 700
    $dropdownPath = Join-Path $OutputDirectory 'ui-polish-dropdown.png'
    Save-WindowCapture $process.MainWindowHandle $dropdownPath
    $expand.Collapse()

    $aboutButton = Find-ById $window 'AboutButton'
    Click-Element $aboutButton
    $aboutWindow = Find-ById `
        ([System.Windows.Automation.AutomationElement]::RootElement) 'AboutWindow'
    if ($aboutWindow.Current.ProcessId -ne $process.Id) {
        throw "About window belongs to an unexpected process: $($aboutWindow.Current.ProcessId)"
    }
    Start-Sleep -Milliseconds 700
    $aboutHandle = [IntPtr]$aboutWindow.Current.NativeWindowHandle
    $aboutPath = Join-Path $OutputDirectory 'ui-polish-about.png'
    Save-WindowCapture $aboutHandle $aboutPath
    $aboutPattern = $aboutWindow.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    $aboutPattern.Close()

    [pscustomobject]@{
        Main = $mainPath
        Settings = $settingsPath
        DropDown = $dropdownPath
        About = $aboutPath
        ProcessPath = $actualProcessPath
        ResolutionName = $combo.Current.Name
        HeaderControlHeight = $actionBounds.Height
        DevicePanelToggle = $deviceToggle.Current.Name
        SettingsPanelToggle = $settingsToggle.Current.Name
        LanguageName = $language.Current.Name
        ProcessAlive = -not $process.HasExited
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
