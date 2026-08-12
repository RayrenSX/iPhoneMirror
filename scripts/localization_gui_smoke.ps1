[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
$Output = Join-Path $Root 'outputs\diagnostics'
$HongKongResourcePath = Join-Path $Root `
    'src\App\Localization\Strings.zh-HK.xaml'

$hongKongResources = [xml](Get-Content -Raw -LiteralPath `
    $HongKongResourcePath -Encoding utf8)
$xamlNamespaces = [System.Xml.XmlNamespaceManager]::new(
    $hongKongResources.NameTable)
$xamlNamespaces.AddNamespace('x',
    'http://schemas.microsoft.com/winfx/2006/xaml')
$expectedHongKongTitle = $hongKongResources.SelectSingleNode(
    '//*[@x:Key="WindowTitleConnectivity"]', $xamlNamespaces).InnerText
$expectedHongKongStart = $hongKongResources.SelectSingleNode(
    '//*[@x:Key="StartMirroring"]', $xamlNamespaces).InnerText

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LocalizationSmokeNative {
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
    if (-not [LocalizationSmokeNative]::SetCursorPos($x, $y)) {
        throw "SetCursorPos failed for automation element: $($Element.Current.AutomationId)"
    }

    Start-Sleep -Milliseconds 100
    [LocalizationSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [LocalizationSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Select-Index(
    [System.Windows.Automation.AutomationElement]$Combo,
    [int]$Index) {
    $Combo.SetFocus()
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    for ($i = 0; $i -lt $Index; ++$i) {
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 1500
}

function Save-Window([IntPtr]$Handle, [string]$Path) {
    $rect = [LocalizationSmokeNative+RECT]::new()
    if (-not [LocalizationSmokeNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed'
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

    [void][LocalizationSmokeNative]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Seconds 3
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    Click-Element (Find-ById $window 'SettingsPanelToggle')
    Start-Sleep -Milliseconds 500
    $language = Find-ById $window 'LanguageComboBox'

    Select-Index $language 3
    $process.Refresh()
    $englishTitle = $process.MainWindowTitle
    $englishStart = (Find-ById $window 'CaptureActionButton').Current.Name
    $englishImage = Join-Path $Output 'ui-monochrome-en.png'
    Save-Window $process.MainWindowHandle $englishImage

    Select-Index $language 2
    $process.Refresh()
    $hongKongTitle = $process.MainWindowTitle
    $hongKongStart = (Find-ById $window 'CaptureActionButton').Current.Name
    $hongKongImage = Join-Path $Output 'ui-monochrome-zh-HK.png'
    Save-Window $process.MainWindowHandle $hongKongImage

    Select-Index $language 1
    $process.Refresh()
    $chineseTitle = $process.MainWindowTitle
    $chineseStart = (Find-ById $window 'CaptureActionButton').Current.Name
    $chineseImage = Join-Path $Output 'ui-monochrome-zh.png'
    Save-Window $process.MainWindowHandle $chineseImage

    # Leave the persisted preference at System default after the test.
    Select-Index $language 0

    if ($englishTitle -notmatch 'Mirroring' -or $englishStart -ne 'Start mirroring') {
        throw "English switch failed: title='$englishTitle', start='$englishStart'"
    }
    if ($chineseTitle -eq $englishTitle -or $chineseStart -eq $englishStart) {
        throw "Chinese switch failed: title='$chineseTitle', start='$chineseStart'"
    }
    if ($hongKongTitle -ne $expectedHongKongTitle -or
        $hongKongStart -ne $expectedHongKongStart) {
        throw "Hong Kong Chinese switch failed: title='$hongKongTitle', start='$hongKongStart'"
    }

    [pscustomobject]@{
        EnglishTitle = $englishTitle
        EnglishStart = $englishStart
        ChineseTitle = $chineseTitle
        ChineseStart = $chineseStart
        HongKongTitle = $hongKongTitle
        HongKongStart = $hongKongStart
        EnglishScreenshot = $englishImage
        ChineseScreenshot = $chineseImage
        HongKongScreenshot = $hongKongImage
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
