param([int]$AppPid=0,[ValidateSet('Inspect','Invoke','Focus','Close')][string]$Action='Inspect',[string]$Id='',[string]$Name='',[string]$Log='C:\Users\Ray\Documents\iphoneMirror\work\recovery-uia.log')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Record([string]$message) { $line=(Get-Date -Format o)+' UI Automation: '+$message; Add-Content -LiteralPath $Log -Value $line; Write-Output $line }
$root=[System.Windows.Automation.AutomationElement]::RootElement
if ($AppPid -eq 0) { $AppPid=(Get-Process iPhoneMirror -ErrorAction Stop | Select-Object -First 1).Id }
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppPid)
$windows=$root.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
if ($windows.Count -eq 0) { throw "No application window for pid=$AppPid" }
$elements=@()
foreach($window in $windows) {
    $elements += $window
    $elements += @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
}
if ($Action -eq 'Inspect') {
    Record "window found pid=$AppPid count=$($windows.Count)"
    foreach($element in $elements) {
        $c=$element.Current
        if($c.AutomationId -or $c.ControlType.ProgrammaticName -eq 'ControlType.Window') {
            [pscustomobject]@{Id=$c.AutomationId;Name=$c.Name;Type=$c.ControlType.ProgrammaticName;Enabled=$c.IsEnabled;Offscreen=$c.IsOffscreen;Status=$c.ItemStatus;Handle=$c.NativeWindowHandle} | ConvertTo-Json -Compress
        }
    }
    exit
}
$target=@($elements | Where-Object { ($Id -and $_.Current.AutomationId -eq $Id) -or ($Name -and $_.Current.Name -eq $Name) }) | Select-Object -First 1
if(!$Id -and !$Name) { $target=$windows[0] }
if(!$target) { throw "Control not found id=$Id name=$Name" }
Record "$Action target id=$($target.Current.AutomationId) name=$($target.Current.Name) pid=$AppPid"
switch($Action) {
 'Invoke' { $pattern=$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $pattern.Invoke() }
 'Focus' { $target.SetFocus() }
 'Close' { $pattern=$target.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $pattern.Close() }
}
Record "$Action submitted"
