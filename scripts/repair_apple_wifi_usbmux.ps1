#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Set-Service -Name 'Bonjour Service' -StartupType Automatic
if ((Get-Service -Name 'Bonjour Service').Status -ne 'Running') {
    Start-Service -Name 'Bonjour Service'
}

# Rebuild Apple Mobile Device Service's device inventory after Bonjour is live.
Restart-Service -Name 'Apple Mobile Device Service' -Force

Start-Sleep -Seconds 5
Get-Service -Name 'Bonjour Service', 'Apple Mobile Device Service' |
    Select-Object Status, StartType, Name, DisplayName
