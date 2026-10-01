param([string]$AppExe='C:\Users\Ray\Documents\iphoneMirror\outputs\recovery-validation\iPhoneMirror.exe',[int]$DurationSeconds=660,[int]$ExistingAppPid=0,[switch]$NoFault,[switch]$ContinueConnected,[string]$FaultModes='transport',[ValidateRange(1,3600)][int]$FaultDelaySeconds=30,[ValidateRange(100,5000)][int]$PollMilliseconds=5000)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repo='C:\Users\Ray\Documents\iphoneMirror'
if($ExistingAppPid) {
 $runDir=(Get-Content (Join-Path $repo 'work\recovery-current-run.txt') -Raw).Trim()
 $app=Get-Process -Id $ExistingAppPid -ErrorAction Stop
 if($app.Path -ne $AppExe) {throw 'Existing process does not match the validation executable'}
} else {
 $runDir=Join-Path $repo ('work\recovery-device-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
 New-Item -ItemType Directory $runDir | Out-Null
 Set-Content -LiteralPath (Join-Path $repo 'work\recovery-current-run.txt') -Value $runDir
 $env:IPHONE_MIRROR_APP_LOG_DIRECTORY=$runDir
 $env:IPHONE_MIRROR_TEST_DISCONNECT_AFTER_SECONDS=if($NoFault) {''} else {[string]$FaultDelaySeconds}
 $env:IPHONE_MIRROR_TEST_DISCONNECT_MODES=if($NoFault) {''} else {$FaultModes}
 $app=Start-Process -FilePath $AppExe -WorkingDirectory (Split-Path $AppExe) -PassThru
}
Set-Content -LiteralPath (Join-Path $repo 'work\recovery-app-pid.txt') -Value $app.Id
function Record([string]$message) { $line=(Get-Date -Format o)+' '+$message; Add-Content (Join-Path $runDir 'uia.log') $line; Write-Output $line }
function Find-Control([string]$Id) {
 $root=[System.Windows.Automation.AutomationElement]::RootElement
 $pc=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
 foreach($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children,$pc)) {
  $ic=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
  $e=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$ic)
  if($e) {return $e}
 }
 return $null
}
function Wait-Control([string]$Id) {
 $limit=(Get-Date).AddSeconds(60)
 do { $e=Find-Control $Id; if($e -and $e.Current.IsEnabled) {return $e}; Start-Sleep -Milliseconds 400 } while((Get-Date)-lt $limit)
 throw "Timed out locating $Id"
}
function Invoke-Control([string]$Id) {
 $e=Wait-Control $Id; Record "UI Automation: Invoke $Id name=$($e.Current.Name)"
 $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Send-Release {
 for($attempt=1; $attempt -le 3; $attempt++) {
  try {
   $preview=Wait-Control 'MainPreviewHost'
   $button=Wait-Control 'CaptureActionButton'
   $log=Join-Path $runDir 'reverse-control.log'
   $before=([regex]::Matches((Get-Content $log -Raw),'bridge_code=input_verified [^\r\n]*kind=keyboard_batch')).Count
   Record "UI Automation: focus preview then move focus away; normal preview input-reset path attempt=$attempt"
   $preview.SetFocus()
   Record "UI Automation: preview focus reported=$($preview.Current.HasKeyboardFocus)"
   Start-Sleep -Milliseconds 250
   $button.SetFocus()
   $limit=(Get-Date).AddSeconds(5)
   do {
    Start-Sleep -Milliseconds 200
    $after=([regex]::Matches((Get-Content $log -Raw),'bridge_code=input_verified [^\r\n]*kind=keyboard_batch')).Count
   } while($after -le $before -and (Get-Date) -lt $limit)
   if($after -le $before) {throw 'No acknowledged input after the UI Automation focus-reset operation'}
   Record 'Actual preview keyboard release acknowledged on the HID connection'
   return
  } catch {
   Record "UI Automation: release attempt=$attempt failed: $_"
   if($attempt -eq 3) {throw}
   Start-Sleep -Milliseconds 500
  }
 }
}
function Read-FinalConnectedState {
 $limit=(Get-Date).AddSeconds(10)
 do {
  $wired=Find-Control 'UsbControlButton'
  $capture=Find-Control 'CaptureActionButton'
  if($wired -and $capture) {
   $status=$wired.Current.ItemStatus
   $captureStatus=$capture.Current.Name
   if($status -match 'enabled|\u5df2\u542f\u7528' -and $captureStatus -eq 'Stop mirroring') {
    return @{uiStatus=$status;captureStatus=$captureStatus}
   }
  }
  Record 'UI Automation: final controls not yet observable as Connected with active capture; reacquiring'
  Start-Sleep -Milliseconds 500
 } while((Get-Date) -lt $limit)
 throw 'Final Connected UI and active capture were not observable within 10 seconds'
}
function Get-ProactiveRefreshCount([string]$ReverseLog) {
 $recovering=$false; $count=0
 foreach($line in ($ReverseLog -split "`n")) {
  if($line -match 'bridge_code=recovery_triggered ') {$recovering=$true}
  elseif($line -match 'bridge_code=recovery_completed ') {$recovering=$false}
  elseif(!$recovering -and $line -match 'bridge_code=direct_hid_refreshed ') {$count++}
 }
 return $count
}
try {
 Record "Actual iPhoneMirror.exe PID=$($app.Id) path=$AppExe resumed=$([bool]$ExistingAppPid)"
 if(!$ContinueConnected) {
  Invoke-Control 'CaptureActionButton'
  # Capture changes the USB configuration. Confirm real frames and allow the
  # device enumeration to settle before requesting the control handshake.
  $captureReadyDeadline=(Get-Date).AddSeconds(30)
  do {
   Start-Sleep -Milliseconds 500
   $initialCapture=Get-Content (Join-Path $runDir 'capture.log') -Raw
  } while($initialCapture -notmatch 'video_output n=240 ' -and (Get-Date) -lt $captureReadyDeadline)
  if($initialCapture -notmatch 'video_output n=240 ') {throw 'Capture did not produce the initial 240 frames'}
  Record 'UI Automation: actual capture frames confirmed before wired-control startup'
  Start-Sleep -Seconds 3
  Invoke-Control 'UsbControlButton'
  Invoke-Control 'ControlPromptPrimaryButton'
 }
 $limit=(Get-Date).AddSeconds(80)
 do { $wired=Find-Control 'UsbControlButton'; if($wired -and $wired.Current.ItemStatus -match 'enabled|\u5df2\u542f\u7528') {break}; Start-Sleep -Seconds 1 } while((Get-Date)-lt $limit)
 if(!$wired -or $wired.Current.ItemStatus -notmatch 'enabled|\u5df2\u542f\u7528') {throw 'Wired control did not reach Connected'}
 Record "UI Automation: Connected status=$($wired.Current.ItemStatus)"
 if(!$ContinueConnected) { Invoke-Control 'ControlStatusCloseButton' }
 $started=Get-Date; $lastSample=[datetime]::MinValue; $lastRecoveryCount=0; $lastRefreshCount=0
 if($ContinueConnected) {
  $previous=Get-Content (Join-Path $runDir 'reverse-control.log') -Raw
  $lastRecoveryCount=([regex]::Matches($previous,'bridge_code=recovery_completed ')).Count
  $lastRefreshCount=Get-ProactiveRefreshCount $previous
  Record "Continuing same capture/control session: existing recoveries=$lastRecoveryCount refreshes=$lastRefreshCount"
 }
 $bridgePid=(Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'iUsbBridge.exe' -and $_.ParentProcessId -eq $app.Id} | Select-Object -First 1).ProcessId
 Record "Bridge PID=$bridgePid"
 Send-Release
 while(((Get-Date)-$started).TotalSeconds -lt $DurationSeconds) {
  Start-Sleep -Milliseconds $PollMilliseconds
  $reverse=Get-Content (Join-Path $runDir 'reverse-control.log') -Raw
  $recovered=([regex]::Matches($reverse,'bridge_code=recovery_completed ')).Count
  $refreshed=Get-ProactiveRefreshCount $reverse
  if($recovered -gt $lastRecoveryCount -or $refreshed -gt $lastRefreshCount) {
   Record "Recovery count=$recovered refresh count=$refreshed"
   Send-Release
   $lastRecoveryCount=$recovered; $lastRefreshCount=$refreshed
  }
  if($reverse -match 'bridge_code=direct_hid_recovery_exhausted ') {throw 'Recovery attempts exhausted'}
  $captureLog=Get-Content (Join-Path $runDir 'capture.log') -Raw
  if($captureLog -match 'capture_run exception stop_requested=false|capture_stop_complete') {throw 'Capture failed or stopped during continuous recovery validation'}
  if($reverse -match 'event=control_cancel_invoked ') {throw 'Wired control was explicitly cancelled through its status dialog'}
  if($app.HasExited -or !(Get-Process -Id $bridgePid -ErrorAction SilentlyContinue)) {throw 'Application or bridge exited during the recovery test'}
  if(((Get-Date)-$lastSample).TotalSeconds -ge 30) {
   $wired=Find-Control 'UsbControlButton';$fps=Find-Control 'FrameRateValue';$capture=Find-Control 'CaptureActionButton'
   Record "Sample elapsed_s=$([int]((Get-Date)-$started).TotalSeconds) status=$($wired.Current.ItemStatus) fps=$($fps.Current.Name) capture=$($capture.Current.Name) app_alive=$(!$app.HasExited) bridge_alive=$([bool](Get-Process -Id $bridgePid -ErrorAction SilentlyContinue))"
   $lastSample=Get-Date
  }
 }
 $finalState=Read-FinalConnectedState
 $reverse=Get-Content (Join-Path $runDir 'reverse-control.log') -Raw
 if($app.HasExited -or !(Get-Process -Id $bridgePid -ErrorAction SilentlyContinue)) {throw 'Application or bridge exited before final verification'}
 $summary=[ordered]@{appPid=$app.Id;bridgePid=$bridgePid;durationSeconds=[int]((Get-Date)-$started).TotalSeconds;forcedDisconnects=([regex]::Matches($reverse,'bridge_code=diagnostic_transport_disconnect ')).Count;forcedHidDisconnects=([regex]::Matches($reverse,'bridge_code=diagnostic_hid_disconnect ')).Count;recoveries=([regex]::Matches($reverse,'bridge_code=recovery_completed ')).Count;refreshes=(Get-ProactiveRefreshCount $reverse);verifiedInputs=([regex]::Matches($reverse,'bridge_code=input_verified ')).Count;uiStatus=$finalState.uiStatus;captureStatus=$finalState.captureStatus}
 $summary | ConvertTo-Json | Set-Content (Join-Path $runDir 'summary.json')
 if(!$NoFault) {
  $faultPlan=@($FaultModes.Split(','))
  foreach($mode in @('hid','transport','mux')) {
   $expected=@($faultPlan | Where-Object {$_ -eq $mode}).Count
   $observed=([regex]::Matches($reverse,"bridge_code=diagnostic_${mode}_disconnect ")).Count
   if($observed -ne $expected) {throw "Fault plan mismatch mode=$mode expected=$expected observed=$observed"}
  }
  if($summary.recoveries -lt $faultPlan.Count) {throw 'Not all requested faults recovered'}
 }
 if(!$NoFault -and ($summary.forcedDisconnects -lt 1 -or $summary.recoveries -lt 1 -or $summary.refreshes -lt 3 -or $summary.verifiedInputs -lt 2)) {throw 'Required recovery/refresh/input evidence missing'}
 if($summary.verifiedInputs -lt 1 -or $summary.uiStatus -notmatch 'enabled|\u5df2\u542f\u7528') {throw 'Final input/readiness verification failed'}
 Record "Completed real GUI/device test no_fault=$([bool]$NoFault); application and mirroring remain running"
} catch { Record "TEST FAILED: $_"; throw }
