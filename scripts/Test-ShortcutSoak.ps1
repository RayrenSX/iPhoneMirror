param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$DurationSeconds = 3600,
    [int]$Port = 17890
)
$ErrorActionPreference = 'Stop'
$AppPath = [IO.Path]::GetFullPath($AppPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$settingsPath = Join-Path $env:LOCALAPPDATA 'iPhoneMirror/settings.json'
$backupPath = Join-Path $OutputDirectory 'settings.before.json'
$eventPath = Join-Path $OutputDirectory 'events.jsonl'
$summaryPath = Join-Path $OutputDirectory 'summary.json'
$appProcess = $null
$mainHandle = [IntPtr]::Zero
$settingsChanged = $false
$started = $null
$events = 0
$failures = 0
$focusPauses = 0
$activeSeconds = 0.0
$result = 'preparing'
$reason = $null
$appAssemblyHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $AppPath) 'iPhoneMirror.dll') -Algorithm SHA256).Hash
$bridgeHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $AppPath) 'tools/iUsbBridge.exe') -Algorithm SHA256).Hash
$originalApps = @(Get-Process iPhoneMirror -ErrorAction SilentlyContinue)
if (Test-Path $backupPath) { throw 'Use a fresh output directory; refusing to overwrite settings backup.' }
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ShortcutSoakNative {
 public delegate bool EnumProc(IntPtr w,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc c,IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr w);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int n);
 [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr w);
 [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w,uint m,IntPtr a,IntPtr b);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr w,string name);
 [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a,uint b,bool attach);
 public static void Activate(IntPtr w) {
   ShowWindow(w,9);SetForegroundWindow(w);
   if(GetForegroundWindow()==w)return;
   uint pid;uint fg=GetWindowThreadProcessId(GetForegroundWindow(),out pid),current=GetCurrentThreadId();
   uint target=GetWindowThreadProcessId(w,out pid);
   bool attached=fg!=0&&fg!=current&&AttachThreadInput(current,fg,true);
   bool targetAttached=target!=0&&target!=current&&target!=fg&&AttachThreadInput(current,target,true);
   try{BringWindowToTop(w);SetActiveWindow(w);SetForegroundWindow(w);}
   finally{if(targetAttached)AttachThreadInput(current,target,false);if(attached)AttachThreadInput(current,fg,false);}
 }
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint n,INPUT[] i,int s);
 [StructLayout(LayoutKind.Explicit,Size=40)] struct INPUT {
   [FieldOffset(0)]public uint type;[FieldOffset(8)]public ushort key;[FieldOffset(10)]public ushort scan;
   [FieldOffset(12)]public uint flags;[FieldOffset(16)]public uint time;[FieldOffset(24)]public UIntPtr extra;
 }
 public static void Key(int key,bool down) {
   if(SendInput(1,new[]{new INPUT{type=1,key=(ushort)key,flags=down?0u:2u}},40)!=1)
     throw new InvalidOperationException("SendInput failed: "+Marshal.GetLastWin32Error());
 }
 public static Dictionary<long,string> Windows(int pid) {
   var r=new Dictionary<long,string>(); EnumWindows((w,p)=>{uint id;GetWindowThreadProcessId(w,out id);
     if(id==pid&&IsWindowVisible(w)){var t=new StringBuilder(512);GetWindowText(w,t,512);r[w.ToInt64()]=t.ToString();}return true;},IntPtr.Zero);return r;
 }
 public static double Difference(string a,string b) {
   using(var x=new System.Drawing.Bitmap(a))using(var y=new System.Drawing.Bitmap(b)) {
     if(x.Width!=y.Width||x.Height!=y.Height)return 1;
     int diff=0,total=0;for(int row=1;row<64;row++)for(int col=1;col<32;col++) {
       var p=x.GetPixel(col*x.Width/32,row*x.Height/64);var q=y.GetPixel(col*y.Width/32,row*y.Height/64);
       if(Math.Abs(p.R-q.R)+Math.Abs(p.G-q.G)+Math.Abs(p.B-q.B)>60)diff++;total++;
     }return (double)diff/total;
   }
 }
}
'@ -ReferencedAssemblies @('System.Drawing.Common','System.Drawing.Primitives','System.Private.Windows.GdiPlus','System.Private.Windows.Core','System.Runtime','System.Collections','System.Runtime.InteropServices')
function Event($kind,$data) {
    $row = [ordered]@{ time=[DateTimeOffset]::Now.ToString('o'); kind=$kind; data=$data }
    $row | ConvertTo-Json -Depth 10 -Compress | Add-Content -LiteralPath $eventPath -Encoding utf8
}
function Save-Summary {
    [ordered]@{result=$result;reason=$reason;requestedSeconds=$DurationSeconds;started=$started;
        updated=[DateTimeOffset]::Now.ToString('o');appPid=if($appProcess){$appProcess.Id}else{$null};
        elapsedSeconds=if($started){([DateTimeOffset]::Now-[DateTimeOffset]$started).TotalSeconds}else{0};
        keypresses=$events;anomalies=$failures;focusPauses=$focusPauses;activeSeconds=$activeSeconds;
        input='Windows SendInput -> real iPhoneMirror hook -> real device';
        appPath=$AppPath;appAssemblySha256=$appAssemblyHash;bridgeSha256=$bridgeHash} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}
function Api($method,$path,$body=$null,$timeout=25) {
    $args=@{Method=$method;Uri="http://127.0.0.1:$Port/api/v1/$path";Headers=$headers;TimeoutSec=$timeout}
    if($null-ne $body){$args.ContentType='application/json';$args.Body=($body|ConvertTo-Json -Compress)}
    Invoke-RestMethod @args
}
function Capture($path) {
    Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/v1/devices/$deviceId/screenshot" -Headers $headers -OutFile $path -TimeoutSec 15 | Out-Null
}
try {
    Save-Summary
    foreach($old in $originalApps) {
        Event 'close_existing_app' @{pid=$old.Id}
        if(!$old.CloseMainWindow()){throw 'Existing iPhoneMirror did not accept a graceful close.'}
        if(!$old.WaitForExit(60000)){throw 'Existing iPhoneMirror still running; no forced termination performed.'}
    }
    Copy-Item -LiteralPath $settingsPath -Destination $backupPath
    $settings=Get-Content -LiteralPath $settingsPath -Raw|ConvertFrom-Json
    foreach($property in @($settings.PSObject.Properties)) {
        if($property.Name -match 'Shortcut(VirtualKey|Modifiers)$'){$property.Value=0}
    }
    # Five navigation commands; no Siri, lock, volume, text entry or app data edits.
    $bindings=@{BluetoothHomeShortcutVirtualKey=112;BluetoothControlCenterShortcutVirtualKey=113;
        BluetoothNotificationCenterShortcutVirtualKey=114;BluetoothAppSwitcherShortcutVirtualKey=115;BluetoothDockShortcutVirtualKey=116}
    foreach($entry in $bindings.GetEnumerator()){$settings.($entry.Key)=$entry.Value}
    $settings.BluetoothShortcutSchema=6
    $settings.KeyboardMapping.Enabled=$false
    $settings.CheckOnStartup=$false
    $settings.CloseMainWindowToTray=$false
    $settings.AutomationApi.Enabled=$true
    $settings.AutomationApi.Port=$Port
    $settings.AutomationApi.Permissions=@('device.read','device.control','screen.capture')
    $settings|ConvertTo-Json -Depth 50|Set-Content -LiteralPath $settingsPath -Encoding utf8
    $settingsChanged=$true
    $env:IPHONE_MIRROR_APP_LOG_DIRECTORY=Join-Path $OutputDirectory 'logs'
    $env:IPHONE_MIRROR_LOG_FILE=Join-Path $OutputDirectory 'capture.log'
    $appProcess=Start-Process -FilePath $AppPath -WorkingDirectory (Split-Path $AppPath) -WindowStyle Hidden -PassThru
    $apiReady=$false
    for($i=0;$i-lt 60;$i++) {
        Start-Sleep -Seconds 1
        if($appProcess.HasExited){throw 'Test app exited before API startup.'}
        try {
            $keyPath=Join-Path $env:LOCALAPPDATA 'iPhoneMirror/automation-key.bin'
            $apiKey=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
                [IO.File]::ReadAllBytes($keyPath),[Text.Encoding]::UTF8.GetBytes('iPhoneMirror.Automation.v1'),[Security.Cryptography.DataProtectionScope]::CurrentUser))
            $headers=@{'X-API-Key'=$apiKey}
            $null=Api GET 'app/status';$apiReady=$true;break
        } catch {}
    }
    if(!$apiReady){throw 'API startup timeout.'}
    $appProcess.Refresh();$mainHandle=$appProcess.MainWindowHandle
    $phones=@()
    for($i=0;$i-lt 30;$i++) {
        $phones=@((Api GET 'devices').devices|Where-Object {$_.online -and $_.connection -eq 'USB' -and $_.bindingProfileId})
        if($phones.Count){break};Start-Sleep -Seconds 1
    }
    if($phones.Count-ne 1){throw "Expected one connected, bound USB iPhone; found $($phones.Count)."}
    $deviceId=$phones[0].id
    Event 'device' @{name=$phones[0].name;model=$phones[0].model;ios=$phones[0].iosVersion;connection=$phones[0].connection}
    for($attempt=1;$attempt-le 3;$attempt++) {
        $prepared=Api POST "devices/$deviceId/prepare" @{enableControl=$true} 380
        Event 'prepared' @{attempt=$attempt;status=$prepared}
        if($prepared.controlAvailable){break}
        if($attempt-lt 3){Start-Sleep -Seconds 10}
    }
    if(!$prepared.controlAvailable){throw "Real phone control is not ready: $($prepared.actionRequired)"}
    $beforeWindows=[ShortcutSoakNative]::Windows($appProcess.Id)
    $null=Api POST "devices/$deviceId/window/open" @{}
    Start-Sleep -Seconds 2
    $afterWindows=[ShortcutSoakNative]::Windows($appProcess.Id)
    $previewHandles=@($afterWindows.Keys|Where-Object {!$beforeWindows.ContainsKey($_)})
    if($previewHandles.Count-ne 1){throw "Could not uniquely identify the real phone preview window ($($previewHandles.Count) new windows)."}
    $preview=[IntPtr]$previewHandles[0]
    Event 'preview_window' @{handle=$preview.ToInt64();title=$afterWindows[$preview.ToInt64()]}
    for($activation=0;$activation-lt 5;$activation++) {
        [ShortcutSoakNative]::Activate($preview)
        Start-Sleep -Seconds 1
        if([ShortcutSoakNative]::GetForegroundWindow()-eq $preview){break}
        Event 'activation_retry' @{attempt=$activation+1;foreground=[ShortcutSoakNative]::GetForegroundWindow().ToInt64()}
    }
    if([ShortcutSoakNative]::GetForegroundWindow()-ne $preview){throw 'Windows denied preview foreground activation; refusing to send keys elsewhere.'}
    # Current independent-window policy pauses control until fixed or full screen.
    # Use the window's double-click command; injected keyboard navigation is
    # intentionally gated while this not-yet-fixed window has control paused.
    $null=[ShortcutSoakNative]::PostMessage($preview,0xA3,[IntPtr]2,[IntPtr]::Zero)
    Start-Sleep -Seconds 1
    Event 'fullscreen_requested' @{preview=$preview.ToInt64()}
    if([ShortcutSoakNative]::GetProp($preview,'iPhoneMirrorFullScreen')-eq [IntPtr]::Zero){throw 'Preview did not enter full screen; input policy still pauses control.'}
    $beforeImage=Join-Path $OutputDirectory 'before.png'
    $afterImage=Join-Path $OutputDirectory 'after.png'
    Capture $beforeImage
    $started=[DateTimeOffset]::Now.ToString('o');$result='running';Save-Summary
    Event 'soak_started' @{durationSeconds=$DurationSeconds;bindings=$bindings;preview=$preview.ToInt64()}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $sequence=@(@{key=113;action='ControlCenter'},@{key=112;action='Home'},@{key=114;action='NotificationCenter'},
        @{key=112;action='Home'},@{key=115;action='AppSwitcher'},@{key=112;action='Home'},@{key=116;action='Dock'},@{key=112;action='Home'})
    $cycle=0
    $previousAction=$null
    $previousDifference=0.0
    $paused=$false
    $appLog=Join-Path $OutputDirectory 'logs/application.log'
    while($activeSeconds-lt $DurationSeconds) {
        if(Test-Path (Join-Path $OutputDirectory 'stop')){$result='stopped';break}
        if($appProcess.HasExited){throw 'Real app exited during soak.'}
        if([ShortcutSoakNative]::GetProp($preview,'iPhoneMirrorFullScreen')-eq [IntPtr]::Zero) {
            $result='stopped';$reason='Preview left the test full-screen state.'
            Event 'window_state_changed' @{elapsed=$watch.Elapsed.TotalSeconds};break
        }
        if([ShortcutSoakNative]::GetForegroundWindow()-ne $preview) {
            if(!$paused){$focusPauses++;Event 'foreground_lost' @{elapsed=$watch.Elapsed.TotalSeconds;foreground=[ShortcutSoakNative]::GetForegroundWindow().ToInt64()};$paused=$true}
            Save-Summary;Start-Sleep -Seconds 2;continue
        }
        if($paused){Capture $beforeImage;Event 'foreground_resumed' @{elapsed=$watch.Elapsed.TotalSeconds};$paused=$false}
        $iteration=[Diagnostics.Stopwatch]::StartNew()
        $item=$sequence[$cycle % $sequence.Count]
        $logOffset=if(Test-Path $appLog){(Get-Item $appLog).Length}else{0}
        $downAt=[DateTimeOffset]::Now.ToString('o')
        $foregroundBefore=[ShortcutSoakNative]::GetForegroundWindow().ToInt64()
        try {
            [ShortcutSoakNative]::Key($item.key,$true)
            Start-Sleep -Milliseconds 70
        } finally { [ShortcutSoakNative]::Key($item.key,$false) }
        $upAt=[DateTimeOffset]::Now.ToString('o')
        $foregroundAfter=[ShortcutSoakNative]::GetForegroundWindow().ToInt64()
        $events++
        Start-Sleep -Milliseconds 1100
        Capture $afterImage
        $difference=[ShortcutSoakNative]::Difference($beforeImage,$afterImage)
        $initialDifference=$difference
        $lateDifference=$null
        $expectedDismissal=$item.action-eq 'Home' -and $previousAction-in @('ControlCenter','NotificationCenter','AppSwitcher') -and $previousDifference-ge 0.01
        $expectsChange=$item.action-in @('ControlCenter','NotificationCenter','AppSwitcher') -or $expectedDismissal
        if($expectsChange-and $difference-lt 0.01) {
            Copy-Item -LiteralPath $afterImage -Destination (Join-Path $OutputDirectory "frame-$events-early.png")
            Start-Sleep -Milliseconds 700
            Capture $afterImage
            $lateDifference=[ShortcutSoakNative]::Difference($beforeImage,$afterImage)
            $difference=[Math]::Max($difference,$lateDifference)
        }
        $newLog=''
        if(Test-Path $appLog) {
            $stream=[IO.File]::Open($appLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            try {$null=$stream.Seek($logOffset,[IO.SeekOrigin]::Begin);$reader=[IO.StreamReader]::new($stream);$newLog=$reader.ReadToEnd()}finally{$stream.Dispose()}
        }
        $sent=$newLog.Contains("system_shortcut_sent action=$($item.action) ")
        $anomaly=(!$sent)-or ($expectsChange-and $difference-lt 0.01)
        if($anomaly){$failures++}
        Event 'keypress' @{index=$events;key=$item.key;action=$item.action;sent=$sent;imageDifference=$difference;anomaly=$anomaly;elapsed=$watch.Elapsed.TotalSeconds;
            downAt=$downAt;upAt=$upAt;foregroundBefore=$foregroundBefore;foregroundAfter=$foregroundAfter;initialImageDifference=$initialDifference;lateImageDifference=$lateDifference;expectedDismissal=$expectedDismissal}
        if($anomaly -or ($cycle%40-eq 0)) {
            Copy-Item -LiteralPath $beforeImage -Destination (Join-Path $OutputDirectory "frame-$events-before.png")
            Copy-Item -LiteralPath $afterImage -Destination (Join-Path $OutputDirectory "frame-$events-after.png")
            Event 'status' (Api GET "devices/$deviceId/status")
        }
        Copy-Item -LiteralPath $afterImage -Destination $beforeImage -Force
        $previousAction=$item.action
        $previousDifference=$difference
        $cycle++;Save-Summary
        Start-Sleep -Milliseconds 800
        $activeSeconds+=$iteration.Elapsed.TotalSeconds
    }
    if($result-eq 'running'){$result=if($failures){'completed_with_anomalies'}else{'completed'}}
} catch {
    $reason=$_.Exception.Message;$result='failed';Event 'failure' @{message=$reason};Write-Error $reason -ErrorAction Continue
} finally {
    if($appProcess -and !$appProcess.HasExited) {
        if($mainHandle-ne [IntPtr]::Zero){$null=[ShortcutSoakNative]::PostMessage($mainHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}
        else {$null=$appProcess.CloseMainWindow()}
        if(!$appProcess.WaitForExit(60000)){Event 'cleanup_incomplete' @{pid=$appProcess.Id};$reason='App did not exit; backup retained for manual restoration.'}
    }
    if($settingsChanged -and (!$appProcess -or $appProcess.HasExited)) { Copy-Item -LiteralPath $backupPath -Destination $settingsPath -Force }
    Save-Summary
    Event 'finished' @{result=$result;reason=$reason;keypresses=$events;anomalies=$failures}
}
if($result-eq 'failed'){exit 1}
