$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'AppleSupportPackage.ps1')

if (-not (Test-AppleSupportSignerSubject `
        'CN=Apple Inc., O=Apple Inc., L=Cupertino, S=California, C=US')) {
    throw 'The exact Apple signer subject was rejected.'
}
foreach ($invalid in @($null, '', 'CN=Apple Inc.',
        'CN=Microsoft Windows, O=Microsoft Corporation, C=US')) {
    if (Test-AppleSupportSignerSubject $invalid) {
        throw "An invalid Apple signer subject was accepted: $invalid"
    }
}

$invalidPackage = Join-Path ([IO.Path]::GetTempPath()) `
    ("iPhoneMirror-invalid-apple-package-" + [Guid]::NewGuid().ToString('N') + '.msi')
try {
    Set-Content -LiteralPath $invalidPackage -Value 'not an Apple MSI' -Encoding utf8
    try {
        [void](Assert-TrustedAppleSupportPackage $invalidPackage)
        throw 'An unsigned Apple support package was accepted.'
    }
    catch {
        if ($_.Exception.Message -notlike 'Apple support package signature validation failed:*') {
            throw
        }
    }
}
finally {
    Remove-Item -LiteralPath $invalidPackage -Force -ErrorAction SilentlyContinue
}

Write-Output 'Apple support package validation tests passed.'
