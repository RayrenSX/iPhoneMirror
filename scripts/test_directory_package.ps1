[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$DriverDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DirectoryPackage.ps1')
. (Join-Path $PSScriptRoot 'CompactBuildRecord.ps1')
function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    try { & $Action } catch {
        if ($_.Exception.Message -notlike $Expected) { throw }
        return
    }
    throw "Expected rejection: $Expected"
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('iphoneMirror-directory-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    foreach ($name in @('iPhoneMirror.exe', 'iPhoneMirror.dll',
            'iPhoneMirror.VirtualCamera.dll', 'iPhoneMirror.VirtualCamera.Admin.exe')) {
        Copy-Item -LiteralPath (Join-Path $ApplicationDirectory $name) -Destination $fixture
    }
    Assert-Rejected { Assert-DirectoryPackageInputs $fixture $fixture } '*Driver package input is missing*'
    Assert-DirectoryPackageInputs $fixture $DriverDirectory
    $camera = Join-Path $fixture 'iPhoneMirror.VirtualCamera.dll'
    [IO.File]::WriteAllText($camera, 'wrong external payload')
    Assert-Rejected { Assert-DirectoryPackageInputs $fixture $DriverDirectory } '*External/embedded camera payload mismatch*'
    Copy-Item -LiteralPath (Join-Path $ApplicationDirectory 'iPhoneMirror.VirtualCamera.dll') -Destination $camera -Force
    [IO.File]::WriteAllText((Join-Path $fixture 'iPhoneMirror.VirtualCamera.Admin.exe'), 'wrong helper')
    Assert-Rejected { Assert-DirectoryPackageInputs $fixture $DriverDirectory } '*External/embedded camera payload mismatch*'
    # Release -SkipBuild must also reject a substituted or omitted driver,
    # even when the app itself and its version have not changed.
    foreach ($relative in @('outputs\iPhoneMirror\iPhoneMirror.exe',
            'outputs\iPhoneMirror\iPhoneMirror.Driver.exe',
            'outputs\iPhoneMirror.Installer\iPhoneMirror.exe',
            'outputs\iPhoneMirror.Installer\iPhoneMirror.Driver.exe',
            'outputs\iPhoneMirror.Installer\iPhoneMirror.dll', 'config\uxplay-component.json')) {
        $target = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [IO.File]::WriteAllText($target, 'build fixture')
    }
    Write-CompactBuildRecord $fixture $false $false
    Assert-CompactBuildRecord $fixture $false $false
    [IO.File]::WriteAllText((Join-Path $fixture 'outputs\iPhoneMirror\iPhoneMirror.Driver.exe'), 'substituted')
    Assert-Rejected { Assert-CompactBuildRecord $fixture $false $false } '*metadata changed after building*Driver.exe*'
    Write-Output 'PASS missing driver, matching payloads, stale camera/helper, and substituted release driver.'
} finally {
    # Only remove the exact unique fixture created above; no caller path is deleted.
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
