[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
$Output = Join-Path $Root 'outputs\diagnostics'

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
    $language = Find-ById $window 'LanguageComboBox'

    Select-Index $language 2
    $process.Refresh()
    $englishTitle = $process.MainWindowTitle
    $englishStart = (Find-ById $window 'CaptureActionButton').Current.Name
    $englishImage = Join-Path $Output 'ui-monochrome-en.png'
    Save-Window $process.MainWindowHandle $englishImage

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

    [pscustomobject]@{
        EnglishTitle = $englishTitle
        EnglishStart = $englishStart
        ChineseTitle = $chineseTitle
        ChineseStart = $chineseStart
        EnglishScreenshot = $englishImage
        ChineseScreenshot = $chineseImage
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
