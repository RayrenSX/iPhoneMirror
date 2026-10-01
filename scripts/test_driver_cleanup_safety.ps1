$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Join-Path $PSScriptRoot 'remove_selected_iphone_drivers.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
# Evaluate definitions only. All OS discovery, process control and PnP calls below
# are test doubles; the cleanup entry point is never executed.
$functions = $ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
}, $true)
foreach ($name in @('Test-IsBthle', 'Get-NormalizedUsbIdentity', 'Test-DriverExclusiveToDevice',
    'Get-DriversForPhysicalDevice', 'Remove-DriverPackage', 'Remove-DeviceNode',
    'Stop-iPhoneMirrorProcesses', 'Test-PnpDriverInventorySupport')) {
    $definition = $functions | Where-Object Name -EQ $name | Select-Object -First 1
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Write-Log { param($Message) }
function Write-OK { param($Message) }
function Write-Warn { param($Message) }
function Get-CleanupText { param($Key, $Values) return $Key }
function Add-Failure { param($Message) throw $Message }
function Get-DriverStoreInventory { return $script:fakeInventory }
$script:commands = [System.Collections.Generic.List[object]]::new()
$script:help = ''
function Invoke-PnpUtil {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq '/?') { return [pscustomobject]@{ ExitCode = 0; Text = $script:help } }
    $script:commands.Add($Arguments)
    return [pscustomobject]@{ ExitCode = 0; Text = '' }
}
$phoneA = 'USB\VID_05AC&PID_12A8\000081010000000000000001'
$phoneB = 'USB\VID_05AC&PID_12A8\000081010000000000000002'
$selected = [pscustomobject]@{ InstanceIds = @($phoneA) }
$script:fakeInventory = @([pscustomobject]@{ InfName = 'oem42.inf'; Devices = @($phoneA, $phoneB) })
Assert (@(Get-DriversForPhysicalDevice $selected).Count -eq 0) 'Shared package was selected for deletion.'
$script:fakeInventory[0].Devices = @($phoneA)
Assert (@(Get-DriversForPhysicalDevice $selected).Count -eq 1) 'Exclusive package was lost.'
$script:fakeInventory[0].Devices = @($phoneA, 'BTHLE\OTHER')
Assert (@(Get-DriversForPhysicalDevice $selected).Count -eq 0) 'Bluetooth package user was ignored.'

$script:DriverInventory = @{}
$script:DriverInventoryInitialized = $true
$script:DriverInventorySupported = $true
$script:PreservedDrivers = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
# A second phone can start using the package after preview but before deletion.
$script:fakeInventory[0].Devices = @($phoneB)
Assert (Remove-DriverPackage 'oem42.inf' $selected) 'Shared package should be preserved successfully.'
Assert ($script:commands.Count -eq 0) 'Newly shared package reached PnPUtil deletion.'
Assert ($script:PreservedDrivers.Contains('oem42.inf')) 'Preserved package missing from final verification exemptions.'
$script:fakeInventory[0].Devices = @()
Assert (Remove-DriverPackage 'oem42.inf' $selected) 'Unused package cleanup failed.'
Assert ($script:commands.Count -eq 1) 'Unused package was not deleted.'
Assert ($script:commands[0].Count -eq 2 -and $script:commands[0][0] -eq '/delete-driver') 'Deletion must not force-uninstall other devices.'
$script:commands.Clear()
Assert (Remove-DeviceNode $phoneA) 'Device node removal failed.'
Assert ($script:commands[0].Count -eq 2 -and $script:commands[0][0] -eq '/remove-device') 'Node removal must use Windows 10-compatible arguments.'

$script:help = "  /enum-drivers [/class <name>]`n`n  /enum-devices [/devices] [/format <xml>]"
Assert (-not (Test-PnpDriverInventorySupport)) 'Unrelated help flags incorrectly enabled package enumeration.'
$script:help = "  /enum-drivers [/devices]`n                [/format <txt | xml | csv>]`n`n    description"
Assert (Test-PnpDriverInventorySupport) 'Supported package enumeration was not detected.'

$ExcludeProcessId = 0
$ExcludeParentProcessId = 0
$script:stopped = @()
function Get-Process {
    param($Name, $ErrorAction)
    Assert (-not ($Name -contains 'iPhoneMirror.Driver')) 'Cleanup tried to enumerate transaction hosts for termination.'
    return [pscustomobject]@{ Id = 101 }
}
function Stop-Process { param($Id, [switch]$Force, $ErrorAction) $script:stopped += $Id }
Stop-iPhoneMirrorProcesses
Assert ($script:stopped.Count -eq 1) 'Main app process closure was not exercised.'
Write-Output 'Driver cleanup safety passed: shared packages, late sharing, Bluetooth users, safe deletion, Windows capabilities and transaction host preservation. No system changes.'
