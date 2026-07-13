[CmdletBinding()]
param(
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $Root 'outputs\iPhoneMirror\iPhoneMirror.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq 0 -and !$process.HasExited -and
        [DateTime]::UtcNow -lt $deadline)
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) {
        throw 'Main GUI window did not become ready.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $wireless = Find-ById $window 'airplay://local-receiver' 15
    $selection = $wireless.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 500
    Invoke-Element (Find-ById $window 'CaptureActionButton')

    $hostProcess = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $hostProcess = Get-Process -Name 'iPhoneMirror.WirelessHost' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } while ($null -eq $hostProcess -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $hostProcess) { throw 'Wireless host process did not start.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $ports = @()
    do {
        Start-Sleep -Milliseconds 250
        $ports = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object {
                $_.OwningProcess -eq $hostProcess.Id -and $_.LocalPort -in @(5001, 7001)
            } | Select-Object -ExpandProperty LocalPort -Unique)
    } while ($ports.Count -lt 2 -and [DateTime]::UtcNow -lt $deadline)
    if (5001 -notin $ports -or 7001 -notin $ports) {
        throw "Wireless host did not listen on both AirPlay ports: $($ports -join ', ')"
    }

    Invoke-Element (Find-ById $window 'CaptureActionButton')
    if (-not $hostProcess.WaitForExit(8000)) {
        throw 'Wireless host did not exit after the unified Stop action.'
    }

    [pscustomobject]@{
        HostStarted = $true
        Ports = ($ports | Sort-Object) -join ', '
        HostExitedAfterStop = $true
    }
}
finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(12000)) { Stop-Process -Id $process.Id -Force }
    }
}
