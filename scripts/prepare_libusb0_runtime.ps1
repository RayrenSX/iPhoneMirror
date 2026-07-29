[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Source = Join-Path $Root `
    'src\DriverInstaller\Assets\libusb-win32-1.2.6.0\amd64\libusb0.dll'
$ExpectedSha256 =
    '4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14'

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "libusb0 runtime is missing: $Source"
}
$actualHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
if ($actualHash -ne $ExpectedSha256) {
    throw "libusb0 runtime hash mismatch: expected $ExpectedSha256, got $actualHash"
}
$signature = Get-AuthenticodeSignature -LiteralPath $Source
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "libusb0 runtime Authenticode signature is not valid: $($signature.Status)"
}

New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
$destination = Join-Path $DestinationDirectory 'libusb0.dll'
Copy-Item -LiteralPath $Source -Destination $destination -Force
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $ExpectedSha256) {
    throw "Copied libusb0 runtime failed verification: $destination"
}
Write-Output "libusb-win32 runtime 1.2.6.0: $destination"
