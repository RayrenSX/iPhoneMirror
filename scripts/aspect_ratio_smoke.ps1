[CmdletBinding()]
param(
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AspectRatioSmokeNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO {
        public uint Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
'@

function Find-ById(
    [System.Windows.Automation.AutomationElement]$RootElement,
    [string]$Id,
    [int]$TimeoutSeconds = 12) {
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

function Wait-PreviewWindow(
    [int]$ProcessId,
    [IntPtr]$MainWindowHandle,
    [int]$TimeoutSeconds = 12) {
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($window in $windows) {
            if ([IntPtr]$window.Current.NativeWindowHandle -ne $MainWindowHandle) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Preview window did not open'
}

$process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable) -PassThru
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

    $main = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $button = Find-ById $main 'PreviewWindowButton'
    try {
        $scroll = $button.GetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern)
        $scroll.ScrollIntoView()
    }
    catch { }
    $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $preview = Wait-PreviewWindow $process.Id $process.MainWindowHandle
    $handle = [IntPtr]$preview.Current.NativeWindowHandle
    Start-Sleep -Seconds 1

    $outer = [AspectRatioSmokeNative+RECT]::new()
    $client = [AspectRatioSmokeNative+RECT]::new()
    [void][AspectRatioSmokeNative]::GetWindowRect($handle, [ref]$outer)
    [void][AspectRatioSmokeNative]::GetClientRect($handle, [ref]$client)
    $clientWidth = $client.Right - $client.Left
    $clientHeight = $client.Bottom - $client.Top
    $extendedStyle = [AspectRatioSmokeNative]::GetWindowLongPtr($handle, -20).ToInt64()
    $noRedirectionBitmap = ($extendedStyle -band 0x00200000) -ne 0

    # Ask the real WndProc how a deliberately square right-edge drag would be
    # constrained. This validates WM_SIZING without relying on mouse speed.
    $proposed = [AspectRatioSmokeNative+RECT]::new()
    $proposed.Left = $outer.Left
    $proposed.Top = $outer.Top
    $proposed.Right = $outer.Left + 700
    $proposed.Bottom = $outer.Top + 700
    $size = [Runtime.InteropServices.Marshal]::SizeOf(
        [type][AspectRatioSmokeNative+RECT])
    $memory = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        [Runtime.InteropServices.Marshal]::StructureToPtr($proposed, $memory, $false)
        [void][AspectRatioSmokeNative]::SendMessage($handle, 0x0214, [IntPtr]2, $memory)
        $locked = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $memory, [type][AspectRatioSmokeNative+RECT])
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($memory)
    }

    $lockedWidth = $locked.Right - $locked.Left
    $lockedHeight = $locked.Bottom - $locked.Top
    $expected = 1206.0 / 2622.0
    $initialAspect = $clientWidth / [double]$clientHeight
    $lockedAspect = $lockedWidth / [double]$lockedHeight
    if ([Math]::Abs($initialAspect - $expected) -gt 0.003) {
        throw "Initial client aspect is not locked: $initialAspect"
    }
    if ([Math]::Abs($lockedAspect - $expected) -gt 0.003) {
        throw "WM_SIZING aspect is not locked: $lockedAspect"
    }

    # A ratio-only implementation can still let a horizontal drag grow until
    # the proportional height is far beyond the screen. Verify that an absurd
    # right-edge proposal is capped by both axes of the monitor work area.
    $oversized = [AspectRatioSmokeNative+RECT]::new()
    $oversized.Left = $outer.Left
    $oversized.Top = $outer.Top
    $oversized.Right = $outer.Left + 50000
    $oversized.Bottom = $outer.Bottom
    $memory = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        [Runtime.InteropServices.Marshal]::StructureToPtr($oversized, $memory, $false)
        [void][AspectRatioSmokeNative]::SendMessage($handle, 0x0214, [IntPtr]2, $memory)
        $limited = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $memory, [type][AspectRatioSmokeNative+RECT])
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($memory)
    }

    $monitor = [AspectRatioSmokeNative]::MonitorFromWindow($handle, 2)
    $monitorInfo = [AspectRatioSmokeNative+MONITORINFO]::new()
    $monitorInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf(
        [type][AspectRatioSmokeNative+MONITORINFO])
    if ($monitor -eq [IntPtr]::Zero -or
        ![AspectRatioSmokeNative]::GetMonitorInfo($monitor, [ref]$monitorInfo)) {
        throw 'Could not read preview monitor work area'
    }
    $frameWidth = ($outer.Right - $outer.Left) - $clientWidth
    $frameHeight = ($outer.Bottom - $outer.Top) - $clientHeight
    $limitedClientWidth = $limited.Right - $limited.Left - $frameWidth
    $limitedClientHeight = $limited.Bottom - $limited.Top - $frameHeight
    $workWidth = $monitorInfo.WorkArea.Right - $monitorInfo.WorkArea.Left
    $workHeight = $monitorInfo.WorkArea.Bottom - $monitorInfo.WorkArea.Top
    $limitedAspect = $limitedClientWidth / [double]$limitedClientHeight
    if ($limitedClientWidth -gt $workWidth -or $limitedClientHeight -gt $workHeight) {
        throw "Horizontal WM_SIZING exceeded work area: ${limitedClientWidth}x${limitedClientHeight}"
    }
    if ([Math]::Abs($limitedAspect - $expected) -gt 0.003) {
        throw "Limited horizontal WM_SIZING lost aspect ratio: $limitedAspect"
    }

    [pscustomobject]@{
        InitialClient = "${clientWidth}x${clientHeight}"
        InitialAspect = [Math]::Round($initialAspect, 6)
        SquareProposal = '700x700'
        LockedRectangle = "${lockedWidth}x${lockedHeight}"
        LockedAspect = [Math]::Round($lockedAspect, 6)
        LimitedHorizontalClient = "${limitedClientWidth}x${limitedClientHeight}"
        LimitedHorizontalAspect = [Math]::Round($limitedAspect, 6)
        NoRedirectionBitmap = $noRedirectionBitmap
    }

    $preview.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern).Close()
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
