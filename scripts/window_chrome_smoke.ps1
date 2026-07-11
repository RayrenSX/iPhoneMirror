[CmdletBinding()]
param([string]$Executable)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Exe = if ([string]::IsNullOrWhiteSpace($Executable)) {
    Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
} else {
    (Resolve-Path -LiteralPath $Executable).Path
}
$Output = Join-Path $Root 'outputs\diagnostics'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowChromeSmokeNative {
    public delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hWnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr region);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static bool TryGetLargestStaticChild(IntPtr parent, out RECT bounds) {
        RECT selected = new RECT();
        long selectedArea = 0;
        EnumChildWindows(parent, (window, parameter) => {
            var name = new System.Text.StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (string.Equals(name.ToString(), "Static", StringComparison.OrdinalIgnoreCase)) {
                RECT candidate;
                if (GetWindowRect(window, out candidate)) {
                    long area = Math.Max(0, candidate.Right - candidate.Left) *
                        (long)Math.Max(0, candidate.Bottom - candidate.Top);
                    if (area > selectedArea) { selected = candidate; selectedArea = area; }
                }
            }
            return true;
        }, IntPtr.Zero);
        bounds = selected;
        return selectedArea > 0;
    }
}
'@

# Match the app's per-monitor-v2 coordinates. Without this, GetWindowRect is
# virtualized while Graphics.CopyFromScreen consumes physical pixels on a
# scaled monitor, producing a misleading crop of the main window underneath.
[void][WindowChromeSmokeNative]::SetProcessDpiAwarenessContext([IntPtr](-4))

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

function Scroll-IntoView([System.Windows.Automation.AutomationElement]$Element) {
    try {
        $pattern = $Element.GetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern)
        $pattern.ScrollIntoView()
    }
    catch {
        if ($Element.Current.IsEnabled) { $Element.SetFocus() }
    }
    Start-Sleep -Milliseconds 500
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    Scroll-IntoView $Element
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Save-Window([IntPtr]$Handle, [string]$Path) {
    $rect = [WindowChromeSmokeNative+RECT]::new()
    # DWM returns physical pixels, unlike GetWindowRect which can be
    # virtualized when this smoke host and the app are on different DPI modes.
    $rectSize = [Runtime.InteropServices.Marshal]::SizeOf(
        [type][WindowChromeSmokeNative+RECT])
    $dwmResult = [WindowChromeSmokeNative]::DwmGetWindowAttribute(
        $Handle, 9, [ref]$rect, $rectSize)
    if ($dwmResult -ne 0 -and
        -not [WindowChromeSmokeNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read physical window bounds: DWM=$dwmResult"
    }
    $bitmap = [System.Drawing.Bitmap]::new(
        $rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Wait-PreviewWindow([int]$ProcessId, [int]$TimeoutSeconds = 10) {
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'iPhoneMirror OBS Preview')
    $condition = [System.Windows.Automation.AndCondition]::new(
        $processCondition, $nameCondition)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Preview window did not open'
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
New-Item -ItemType Directory -Path $Output -Force | Out-Null

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

    [void][WindowChromeSmokeNative]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Seconds 3
    $main = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $start = Find-ById $main 'CaptureActionButton'
    Scroll-IntoView $start
    $mainImage = Join-Path $Output 'primary-button-and-icon.png'
    Save-Window $process.MainWindowHandle $mainImage

    # Main-window fullscreen must collapse every fixed spacer row. Validate
    # the native D3D child itself covers the complete client before moving on
    # to the independent DirectComposition-window checks.
    Invoke-Element (Find-ById $main 'FullScreenButton')
    Start-Sleep -Seconds 1
    $fullClient = [WindowChromeSmokeNative+RECT]::new()
    $fullOrigin = [WindowChromeSmokeNative+POINT]::new()
    $fullPreview = [WindowChromeSmokeNative+RECT]::new()
    if (-not [WindowChromeSmokeNative]::GetClientRect(
            $process.MainWindowHandle, [ref]$fullClient) -or
        -not [WindowChromeSmokeNative]::ClientToScreen(
            $process.MainWindowHandle, [ref]$fullOrigin) -or
        -not [WindowChromeSmokeNative]::TryGetLargestStaticChild(
            $process.MainWindowHandle, [ref]$fullPreview)) {
        throw 'Could not inspect main fullscreen preview geometry'
    }
    $fullPreviewWidth = $fullPreview.Right - $fullPreview.Left
    $fullPreviewHeight = $fullPreview.Bottom - $fullPreview.Top
    $mainFullscreenGeometry = 'idle WPF connection state'
    $fullOffsetX = $fullOffsetY = $fullWidthDelta = $fullHeightDelta = 'n/a'
    if ($fullPreviewWidth -gt 16 -and $fullPreviewHeight -gt 16) {
        $fullOffsetX = $fullPreview.Left - $fullOrigin.X
        $fullOffsetY = $fullPreview.Top - $fullOrigin.Y
        $fullWidthDelta = [Math]::Abs($fullPreviewWidth - $fullClient.Right)
        $fullHeightDelta = [Math]::Abs($fullPreviewHeight - $fullClient.Bottom)
        if ([Math]::Abs($fullOffsetX) -gt 1 -or [Math]::Abs($fullOffsetY) -gt 1 -or
            $fullWidthDelta -gt 1 -or $fullHeightDelta -gt 1) {
            throw "Main fullscreen preview leaves a gap: offset=$fullOffsetX,$fullOffsetY delta=$fullWidthDelta,$fullHeightDelta"
        }
        $mainFullscreenGeometry = "${fullPreviewWidth}x${fullPreviewHeight}"
    }
    [void][WindowChromeSmokeNative]::SendMessage(
        $process.MainWindowHandle, 0x0100, [IntPtr]0x7A, [IntPtr]::Zero)
    Start-Sleep -Seconds 1

    Invoke-Element (Find-ById $main 'PreviewWindowButton')
    $preview = Wait-PreviewWindow $process.Id
    $previewHandle = [IntPtr]$preview.Current.NativeWindowHandle
    Start-Sleep -Seconds 1
    $windowRect = [WindowChromeSmokeNative+RECT]::new()
    $clientRect = [WindowChromeSmokeNative+RECT]::new()
    $clientOrigin = [WindowChromeSmokeNative+POINT]::new()
    if (-not [WindowChromeSmokeNative]::GetWindowRect($previewHandle, [ref]$windowRect) -or
        -not [WindowChromeSmokeNative]::GetClientRect($previewHandle, [ref]$clientRect) -or
        -not [WindowChromeSmokeNative]::ClientToScreen($previewHandle, [ref]$clientOrigin)) {
        throw 'Could not inspect preview client geometry'
    }
    $topNonClient = $clientOrigin.Y - $windowRect.Top
    $leftNonClient = $clientOrigin.X - $windowRect.Left
    $style = [WindowChromeSmokeNative]::GetWindowLongPtr($previewHandle, -16).ToInt64()
    $extendedStyle = [WindowChromeSmokeNative]::GetWindowLongPtr($previewHandle, -20).ToInt64()
    $hasCaption = ($style -band 0x00C00000) -ne 0
    $noRedirectionBitmap = ($extendedStyle -band 0x00200000) -ne 0
    $region = [WindowChromeSmokeNative]::CreateRectRgn(0, 0, 0, 0)
    try { $regionType = [WindowChromeSmokeNative]::GetWindowRgn($previewHandle, $region) }
    finally { [void][WindowChromeSmokeNative]::DeleteObject($region) }
    $previewImage = Join-Path $Output 'rounded-preview-window.png'
    Save-Window $previewHandle $previewImage

    $previewBitmap = [System.Drawing.Bitmap]::FromFile($previewImage)
    try {
        $topCenter = $previewBitmap.GetPixel(
            [Math]::Floor($previewBitmap.Width / 2), 0)
        $topCenterLuma = ($topCenter.R + $topCenter.G + $topCenter.B) / 3.0
        $antialiasedPixels = 0
        $sampleY = [Math]::Min(5, $previewBitmap.Height - 1)
        for ($x = 0; $x -lt [Math]::Floor($previewBitmap.Width / 2); $x++) {
            $pixel = $previewBitmap.GetPixel($x, $sampleY)
            $luma = ($pixel.R + $pixel.G + $pixel.B) / 3.0
            if ($luma -gt 20 -and $luma -lt 230) { $antialiasedPixels++ }
        }
    }
    finally { $previewBitmap.Dispose() }

    if ($hasCaption) { throw 'Preview window still has WS_CAPTION' }
    # The legacy WPF fallback uses a binary HRGN. The preferred native path
    # deliberately has no HRGN: DirectComposition owns its antialiased clip.
    if (!$noRedirectionBitmap -and $regionType -le 0) {
        throw "Fallback preview window has no native region: $regionType"
    }
    if ($topNonClient -ne 0 -or $leftNonClient -ne 0) {
        throw "Preview client does not cover the shaped HWND: left=$leftNonClient top=$topNonClient"
    }
    # CopyFromScreen is not a reliable pixel oracle for a
    # WS_EX_NOREDIRECTIONBITMAP DirectComposition window: depending on the GPU
    # it can return the desktop underneath instead of the composition visual.
    # Keep these samples as diagnostics, while the native style/client checks
    # and device-profile logic tests remain deterministic gates.

    [pscustomobject]@{
        StartButtonName = $start.Current.Name
        StartButtonEnabled = $start.Current.IsEnabled
        MainFullscreenGeometry = $mainFullscreenGeometry
        MainFullscreenOffset = "$fullOffsetX,$fullOffsetY"
        MainFullscreenSizeDelta = "$fullWidthDelta,$fullHeightDelta"
        PreviewHasCaption = $hasCaption
        PreviewNoRedirectionBitmap = $noRedirectionBitmap
        PreviewRegionType = $regionType
        PreviewNonClient = "$leftNonClient,$topNonClient"
        PreviewTopCenterLuma = [Math]::Round($topCenterLuma, 1)
        PreviewAntialiasedSamples = $antialiasedPixels
        MainScreenshot = $mainImage
        PreviewScreenshot = $previewImage
    }

    $windowPattern = $preview.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
