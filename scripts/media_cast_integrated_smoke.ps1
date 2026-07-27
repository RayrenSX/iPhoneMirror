[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$SimulateHostFailure,
    [string]$MediaUri = 'https://example.test/integrated-preview.mp4',
    [string]$ExpectedResolution
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class MediaCastSmokeWindows
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(
        IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out RECT bounds);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr window, out RECT bounds);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(
        IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)]
        public string ExecutableName;
    }

    public static int CountVisible(uint wantedProcessId)
    {
        var count = 0;
        EnumWindows((window, parameter) =>
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == wantedProcessId && IsWindowVisible(window)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    public static IntPtr FindOtherVisible(uint wantedProcessId, IntPtr excluded)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (window != excluded && processId == wantedProcessId &&
                IsWindowVisible(window)) found = window;
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    public static uint[] FindChildProcesses(uint parentProcessId, string executableName)
    {
        const uint snapshotProcesses = 0x00000002;
        var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new List<uint>();
            var entry = new PROCESSENTRY32();
            entry.Size = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            while (true)
            {
                if (entry.ParentProcessId == parentProcessId &&
                    string.Equals(entry.ExecutableName, executableName,
                        StringComparison.OrdinalIgnoreCase))
                    result.Add(entry.ProcessId);
                entry.Size = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32NextW(snapshot, ref entry)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error);
                break;
            }
            return result.ToArray();
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
'@

function Send-AvTransport([string]$Action, [string]$Arguments = '') {
    $body = '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">' +
        '<s:Body><u:' + $Action +
        ' xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">' +
        '<InstanceID>0</InstanceID>' + $Arguments + '</u:' + $Action +
        '></s:Body></s:Envelope>'
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
    $header = "POST /dlna/control/avtransport HTTP/1.1`r`n" +
        "Host: 127.0.0.1:8090`r`nContent-Type: text/xml`r`n" +
        "SOAPACTION: `"urn:schemas-upnp-org:service:AVTransport:1#$Action`"`r`n" +
        "Content-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.SendTimeout = 5000
        $client.ReceiveTimeout = 5000
        $client.NoDelay = $true
        $client.Connect('127.0.0.1', 8090)
        $stream = $client.GetStream()
        $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        $stream.Write($bodyBytes, 0, $bodyBytes.Length)
        $stream.Flush()
        $buffer = [byte[]]::new(4096)
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $response = [Text.Encoding]::UTF8.GetString($buffer, 0, $count)
        if ($response -notmatch '^HTTP/1\.1 200 ') {
            throw "$Action failed: $response"
        }
    }
    finally {
        $client.Dispose()
    }
}

function Test-AvTransportStopped {
    $body = '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">' +
        '<s:Body><u:GetTransportInfo ' +
        'xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">' +
        '<InstanceID>0</InstanceID></u:GetTransportInfo></s:Body></s:Envelope>'
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
    $header = "POST /dlna/control/avtransport HTTP/1.1`r`n" +
        "Host: 127.0.0.1:8090`r`nContent-Type: text/xml`r`n" +
        "SOAPACTION: `"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo`"`r`n" +
        "Content-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.SendTimeout = 5000
        $client.ReceiveTimeout = 5000
        $client.NoDelay = $true
        $client.Connect('127.0.0.1', 8090)
        $stream = $client.GetStream()
        $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        $stream.Write($bodyBytes, 0, $bodyBytes.Length)
        $stream.Flush()
        $buffer = [byte[]]::new(8192)
        $response = [Text.StringBuilder]::new()
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            [void]$response.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $count))
        }
        return $response.ToString().Contains(
            '<CurrentTransportState>STOPPED</CurrentTransportState>')
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Find-SelectableById(
    [Windows.Automation.AutomationElement]$RootElement,
    [string]$AutomationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $candidates = $RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($candidate in $candidates) {
        try {
            $pattern = $candidate.GetCurrentPattern(
                [Windows.Automation.SelectionItemPattern]::Pattern)
            return [pscustomobject]@{ Element = $candidate; Pattern = $pattern }
        }
        catch {}
    }
    return $null
}

function Find-ById(
    [Windows.Automation.AutomationElement]$RootElement,
    [string]$AutomationId,
    [int]$TimeoutSeconds = 10) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        $handle = [IntPtr]$RootElement.Current.NativeWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            $RootElement = [Windows.Automation.AutomationElement]::FromHandle($handle)
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Find-AppWindowWithElement(
    [int]$ProcessId,
    [string]$AutomationId,
    [int]$TimeoutSeconds = 15) {
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $elementCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($candidate in $windows) {
            $element = $candidate.FindFirst(
                [Windows.Automation.TreeScope]::Descendants, $elementCondition)
            if ($null -ne $element) {
                return [pscustomobject]@{ Window = $candidate; Element = $element }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq 0 -and -not $process.HasExited -and
        [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Main window did not start.' }

    # Cache the toolbar element while the UI is idle. Traversing the WPF tree
    # through UIA can otherwise time out while the media stack is opening URL.
    $appUi = Find-AppWindowWithElement $process.Id 'PreviewWindowButton'
    if ($null -eq $appUi) { throw 'Main application UI did not become ready.' }
    $window = $appUi.Window
    $previewButton = $appUi.Element
    $mainWindowHandle = [IntPtr]$window.Current.NativeWindowHandle
    $captureButton = Find-ById $window 'CaptureActionButton'
    if ($null -eq $captureButton) { throw 'Capture action button was not found.' }
    $initialCaptureButtonName = $captureButton.Current.Name

    $receiverReady = $false
    $lastReceiverError = 'receiver did not accept a request'
    $escapedMediaUri = [Security.SecurityElement]::Escape($MediaUri)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            Send-AvTransport SetAVTransportURI `
                "<CurrentURI>$escapedMediaUri</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>"
            $receiverReady = $true
        }
        catch {
            $lastReceiverError = $_.Exception.Message
            Start-Sleep -Milliseconds 250
        }
    } while (-not $receiverReady -and [DateTime]::UtcNow -lt $deadline)
    if (-not $receiverReady) {
        throw "DLNA HTTP receiver did not become ready: $lastReceiverError"
    }

    Send-AvTransport Play '<Speed>1</Speed>'

    $surfaceCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'CloseMediaCastButton')
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $surface = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $surfaceCondition)
        if ($null -eq $surface) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $surface -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $surface) {
        throw 'Integrated media surface is not visible.'
    }

    $statusCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'MediaCastStatusPanel')
    $statusPanel = $window.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $statusCondition)
    if ($null -ne $statusPanel) {
        throw 'Media-cast status overlay remained visible over the video surface.'
    }

    $mediaValues = @{}
    foreach ($id in @('MediaCastDeviceCard', 'FrameRateValue',
            'LatencyValue', 'AudioValue')) {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $element = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Media-cast UI value is missing: $id" }
        if ($id -ne 'MediaCastDeviceCard' -and
            [string]::IsNullOrWhiteSpace($element.Current.Name)) {
            throw "Media-cast UI value is blank: $id"
        }
        if ($id -ne 'MediaCastDeviceCard') {
            $mediaValues[$id] = $element.Current.Name
            if ($element.Current.Name -match '^\s*[—-]') {
                throw "Media-cast UI value was not populated: $id"
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedResolution)) {
        $resolutionCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, 'ResolutionValue')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $resolutionValue = $window.FindFirst(
                [Windows.Automation.TreeScope]::Descendants, $resolutionCondition)
            if ($null -eq $resolutionValue -or
                $resolutionValue.Current.Name -ne $ExpectedResolution) {
                Start-Sleep -Milliseconds 150
            }
        } while (($null -eq $resolutionValue -or
            $resolutionValue.Current.Name -ne $ExpectedResolution) -and
            [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $resolutionValue -or
            $resolutionValue.Current.Name -ne $ExpectedResolution) {
            throw "Media resolution was not reported as $ExpectedResolution."
        }
    }

    # A physical device is optional in automated environments. When one is
    # present, verify that a later full Play command cannot steal selection
    # back from it, then return to the media source for the window checks.
    $sourceSwitchVerified = $false
    $physicalSource = Find-SelectableById $window 'DeviceCard'
    $mediaSource = Find-SelectableById $window 'MediaCastDeviceCard'
    if ($null -ne $physicalSource -and $null -ne $mediaSource) {
        $physicalSource.Pattern.Select()
        Start-Sleep -Milliseconds 250
        if (-not $physicalSource.Pattern.Current.IsSelected) {
            throw 'Could not switch from the media source to a physical device.'
        }
        Send-AvTransport SetAVTransportURI `
            "<CurrentURI>$escapedMediaUri</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>"
        Send-AvTransport Play '<Speed>1</Speed>'
        Start-Sleep -Milliseconds 750
        if (-not $physicalSource.Pattern.Current.IsSelected) {
            throw 'A repeated media Play request stole the physical-device selection.'
        }
        $mediaSource.Pattern.Select()
        Start-Sleep -Milliseconds 250
        if (-not $mediaSource.Pattern.Current.IsSelected) {
            throw 'Could not switch back to the media source.'
        }
        $sourceSwitchVerified = $true
    }

    Send-AvTransport Pause
    Send-AvTransport Seek '<Unit>REL_TIME</Unit><Target>00:00:05</Target>'
    Send-AvTransport Play '<Speed>1</Speed>'

    $invoke = $previewButton.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $windowCount = [MediaCastSmokeWindows]::CountVisible($process.Id)
        if ($windowCount -lt 2) { Start-Sleep -Milliseconds 100 }
    } while ($windowCount -lt 2 -and [DateTime]::UtcNow -lt $deadline)
    if ($windowCount -lt 2) { throw 'Media-cast separate window did not open.' }

    $previewHandle = [MediaCastSmokeWindows]::FindOtherVisible(
        $process.Id, $mainWindowHandle)
    if ($previewHandle -eq [IntPtr]::Zero) {
        throw 'Media-cast separate window handle was not found.'
    }
    $style = [MediaCastSmokeWindows]::GetWindowLongPtr($previewHandle, -16).ToInt64()
    if (($style -band 0x00C00000) -eq 0) {
        throw 'Media-cast separate window has no native title bar.'
    }
    $cornerPreference = 0
    $cornerResult = [MediaCastSmokeWindows]::DwmGetWindowAttributeInt(
        $previewHandle, 33, [ref]$cornerPreference, 4)
    if ($cornerResult -eq 0 -and $cornerPreference -eq 1) {
        throw 'Media-cast separate window disables Windows rounded corners.'
    }
    $bounds = [MediaCastSmokeWindows+RECT]::new()
    if (-not [MediaCastSmokeWindows]::GetWindowRect($previewHandle, [ref]$bounds)) {
        throw 'Could not read media-cast separate-window bounds.'
    }
    $x = [Math]::Floor(($bounds.Left + $bounds.Right) / 2)
    $y = [Math]::Floor(($bounds.Top + $bounds.Bottom) / 2)
    $packedValue = [int64]((($y -band 0xffff) -shl 16) -bor ($x -band 0xffff))
    $packedPoint = [IntPtr]::new($packedValue)
    $hit = [MediaCastSmokeWindows]::SendMessage(
        $previewHandle, 0x0084, [IntPtr]::Zero, $packedPoint).ToInt32()
    if ($hit -eq 2) {
        throw 'Media-cast client area still acts as a synthetic drag caption.'
    }
    $clientBounds = [MediaCastSmokeWindows+RECT]::new()
    if (-not [MediaCastSmokeWindows]::GetClientRect($previewHandle, [ref]$clientBounds)) {
        throw 'Could not read media-cast client bounds.'
    }
    $clientX = [Math]::Floor(($clientBounds.Right - $clientBounds.Left) / 2)
    $clientY = [Math]::Floor(($clientBounds.Bottom - $clientBounds.Top) / 2)
    $clientPointValue = [int64]((($clientY -band 0xffff) -shl 16) -bor
        ($clientX -band 0xffff))
    [void][MediaCastSmokeWindows]::SendMessage(
        $previewHandle, 0x0203, [IntPtr]1, [IntPtr]::new($clientPointValue))
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $doubleClickFullScreen = [MediaCastSmokeWindows]::GetProp(
            $previewHandle, 'iPhoneMirrorFullScreen') -ne [IntPtr]::Zero
        if (-not $doubleClickFullScreen) { Start-Sleep -Milliseconds 50 }
    } while (-not $doubleClickFullScreen -and [DateTime]::UtcNow -lt $deadline)
    if (-not $doubleClickFullScreen) {
        throw 'Double-clicking the media client area did not enter full screen.'
    }
    [void][MediaCastSmokeWindows]::SendMessage(
        $previewHandle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)

    $hostFailureRecovered = $false
    $receiverStoppedByUi = $false
    if ($SimulateHostFailure) {
        $expectedHostPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $Exe) `
            'Wireless\iPhoneMirror.WirelessHost.exe'))
        $launchFloor = $process.StartTime.AddSeconds(-2)
        $childIds = [MediaCastSmokeWindows]::FindChildProcesses(
            [uint32]$process.Id, 'iPhoneMirror.WirelessHost.exe')
        $hostCandidates = @($childIds | ForEach-Object {
            try {
                $candidate = Get-Process -Id $_ -ErrorAction Stop
                if ($candidate.StartTime -ge $launchFloor -and
                    [string]::Equals([IO.Path]::GetFullPath($candidate.Path),
                        $expectedHostPath, [StringComparison]::OrdinalIgnoreCase)) {
                    $candidate
                }
            }
            catch { }
        })
        if ($hostCandidates.Count -ne 1) {
            throw "Refusing to terminate an unverified wireless host; expected one owned process, found $($hostCandidates.Count)."
        }
        $hostProcess = $hostCandidates[0]
        $hostProcess.Kill()
        if (-not $hostProcess.WaitForExit(5000)) {
            throw 'Owned wireless host did not exit after forced termination.'
        }
        $closePattern = $surface.GetCurrentPattern(
            [Windows.Automation.InvokePattern]::Pattern)
        $closePattern.Invoke()
        $hostFailureRecovered = $true
    }
    else {
        if (-not $captureButton.Current.IsEnabled) {
            throw 'Capture action button was not enabled during media casting.'
        }
        $stopPattern = $captureButton.GetCurrentPattern(
            [Windows.Automation.InvokePattern]::Pattern)
        $stopButtonName = $captureButton.Current.Name
        if ($stopButtonName -eq $initialCaptureButtonName) {
            throw 'Capture action button still displayed the start action during media casting.'
        }
        $stopPattern.Invoke()
        Start-Sleep -Milliseconds 300
        $surfaceAfterStopClick = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $surfaceCondition)
        if ($null -ne $surfaceAfterStopClick) {
            throw "Capture action button did not execute media stop (name=$stopButtonName)."
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $receiverStoppedByUi = Test-AvTransportStopped
            if (-not $receiverStoppedByUi) { Start-Sleep -Milliseconds 100 }
        } while (-not $receiverStoppedByUi -and [DateTime]::UtcNow -lt $deadline)
        if (-not $receiverStoppedByUi) {
            throw 'The UI stop button did not transition the receiver to STOPPED.'
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $surface = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $surfaceCondition)
        $windowCount = [MediaCastSmokeWindows]::CountVisible($process.Id)
        $hidden = $null -eq $surface
    } while ((!$hidden -or $windowCount -ne 1) -and
        [DateTime]::UtcNow -lt $deadline)
    if (-not $hidden) { throw 'Integrated media surface remained visible after Stop.' }
    if ($windowCount -ne 1) { throw 'Media-cast separate window remained after Stop.' }

    [pscustomobject]@{
        IntegratedSurfaceVisible = $true
        StatusOverlayHidden = $true
        SeparateWindowOpened = $true
        PlaybackControlsAccepted = $true
        MediaDeviceVisible = $true
        MediaStatisticsVisible = $true
        FrameRateDisplay = $mediaValues['FrameRateValue']
        LatencyDisplay = $mediaValues['LatencyValue']
        AudioDisplay = $mediaValues['AudioValue']
        NativeTitleBar = $true
        WindowsCornerPolicy = $cornerPreference
        ClientHitTest = $hit
        DoubleClickFullScreen = $doubleClickFullScreen
        HostFailureRecovered = $hostFailureRecovered
        ReceiverStoppedByUi = $receiverStoppedByUi
        StopButtonName = $stopButtonName
        SourceSwitchVerified = $sourceSwitchVerified
        SurfaceHiddenAfterStop = $hidden
    }
}
finally {
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(12000)) { Stop-Process -Id $process.Id -Force }
    }
}
