$script:AppleSupportSignerSubject =
    'CN=Apple Inc., O=Apple Inc., L=Cupertino, S=California, C=US'
$script:AppleSupportPackageName = 'AppleMobileDeviceSupport64.msi'

function Test-AppleSupportSignerSubject {
    param([AllowNull()][string]$Subject)
    return [string]::Equals($Subject, $script:AppleSupportSignerSubject,
        [StringComparison]::Ordinal)
}

function Assert-TrustedAppleSupportPackage {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Apple support package is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Apple support package must not be a reparse point: $Path"
    }
    if ($item.Extension -ne '.msi') {
        throw "Apple support package must be an MSI: $Path"
    }
    if ($item.Length -le 0 -or $item.Length -gt 512L * 1024 * 1024) {
        throw "Apple support package size is invalid: $($item.Length)"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        -not (Test-AppleSupportSignerSubject $signature.SignerCertificate.Subject)) {
        throw "Apple support package signature validation failed: $($signature.Status)"
    }
    return $item
}

function Copy-TrustedAppleSupportPackage {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $sourceItem = Assert-TrustedAppleSupportPackage $Source
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    $destination = Join-Path $DestinationDirectory $script:AppleSupportPackageName
    Copy-Item -LiteralPath $sourceItem.FullName -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $sourceItem.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Apple support package copy verification failed: $destination"
    }
    [void](Assert-TrustedAppleSupportPackage $destination)
    return $destination
}
