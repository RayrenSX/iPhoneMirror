[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$features = @(
    'Microsoft-Windows-Subsystem-Linux',
    'VirtualMachinePlatform',
    'Containers'
)

foreach ($feature in $features) {
    Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart |
        Out-Null
}

Write-Host 'Docker prerequisites enabled. Restart Windows before starting Docker Desktop.'
