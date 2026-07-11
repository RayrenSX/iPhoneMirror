[CmdletBinding()]
param(
    [string]$Exe,
    [int]$RefreshCount = 6
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-ListItems([System.Windows.Automation.AutomationElement]$List) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($List.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
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
    $list = Find-ById $window 'DeviceListBox'
    $refresh = Find-ById $window 'RefreshDevicesButton'
    Start-Sleep -Seconds 4

    $items = Get-ListItems $list
    if ($items.Count -lt 2) {
        throw "Multi-device smoke requires at least two visible devices; found $($items.Count)."
    }
    $targetIndex = $items.Count - 1
    $target = $items[$targetIndex]
    $targetName = $target.Current.Name
    $targetUdid = $target.Current.AutomationId
    $selectedUdidText = Find-ById $window 'SelectedDeviceUdidText'
    $selection = $target.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 600

    $initialItems = Get-ListItems $list
    $initialSelected = @()
    for ($index = 0; $index -lt $initialItems.Count; ++$index) {
        $pattern = $initialItems[$index].GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($pattern.Current.IsSelected) { $initialSelected += $index }
    }
    if ($initialSelected.Count -ne 1 -or $initialSelected[0] -ne $targetIndex) {
        throw "Could not select target card $targetIndex; selected indices: $($initialSelected -join ',')."
    }
    if (-not [string]::Equals($selectedUdidText.Current.Name, $targetUdid,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Selection binding did not reach the view model: card=$targetUdid details=$($selectedUdidText.Current.Name)."
    }

    for ($iteration = 1; $iteration -le $RefreshCount; ++$iteration) {
        Invoke-Element $refresh
        Start-Sleep -Milliseconds 900
        $items = Get-ListItems $list
        $selectedIndices = @()
        for ($index = 0; $index -lt $items.Count; ++$index) {
            $pattern = $items[$index].GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($pattern.Current.IsSelected) { $selectedIndices += $index }
        }
        if ($selectedIndices.Count -ne 1) {
            throw "Refresh $iteration selected $($selectedIndices.Count) cards; expected exactly one."
        }
        $selectedIndex = $selectedIndices[0]
        if ($selectedIndex -ne $targetIndex) {
            $currentNames = @($items | ForEach-Object { $_.Current.Name })
            throw "Selection jumped after refresh ${iteration}: expected index $targetIndex ($targetName), " +
                "got $selectedIndex ($($currentNames[$selectedIndex])); details=$($selectedUdidText.Current.Name); " +
                "cards=[$($currentNames -join ' | ')]."
        }
        if (-not [string]::Equals($selectedUdidText.Current.Name, $targetUdid,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "View-model selection changed after refresh ${iteration}: expected $targetUdid, " +
                "got $($selectedUdidText.Current.Name)."
        }
    }

    [pscustomobject]@{
        Devices = $items.Count
        SelectedIndex = $targetIndex
        SelectedName = $targetName
        SelectedUdid = $targetUdid
        Refreshes = $RefreshCount
        Stable = $true
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(15000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
