[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.6.9',
    [switch]$SkipAppBuild,
    [string]$SourceDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$SourceDirectory = if ($SourceDirectory) {
    [IO.Path]::GetFullPath($SourceDirectory)
} else { Join-Path $Root 'outputs\iPhoneMirror.Installer' }
$OutputDirectory = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else { Join-Path $Root 'outputs\releases' }
$numericVersion = ($Version -split '-', 2)[0] + '.0'
$expectedName = "iPhoneMirror-Setup-v$Version-x64.exe"
$expectedPath = Join-Path $OutputDirectory $expectedName

Push-Location $Root
try {
    if (-not $SkipAppBuild) {
        & (Join-Path $Root 'build.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }
    }
    $appExecutable = Join-Path $SourceDirectory 'iPhoneMirror.exe'
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
        throw "Published application is missing: $appExecutable"
    }
    $productVersion = (Get-Item -LiteralPath $appExecutable).VersionInfo.ProductVersion
    $actualVersion = ($productVersion -split '\+', 2)[0].Trim()
    if ($actualVersion -ne $Version) {
        throw "Installer version $Version does not match application version $actualVersion."
    }
    $driverExecutable = Join-Path $SourceDirectory 'iPhoneMirror.Driver.exe'
    if (-not (Test-Path -LiteralPath $driverExecutable -PathType Leaf)) {
        throw "Published driver manager is missing: $driverExecutable"
    }
    $driverProductVersion = (Get-Item -LiteralPath $driverExecutable).VersionInfo.ProductVersion
    $actualDriverVersion = ($driverProductVersion -split '\+', 2)[0].Trim()
    if ($actualDriverVersion -ne $Version) {
        throw "Installer version $Version does not match driver version $actualDriverVersion."
    }
    foreach ($required in @('CHANGELOG.md', 'DRIVER_DEPENDENCIES.md', 'LICENSE',
            'THIRD_PARTY_NOTICES.md',
            'tools\updater\Apply-ZipUpdate.ps1', 'libusb0.dll', 'msvcp140.dll',
            'vcruntime140.dll', 'vcruntime140_1.dll',
            'iPhoneMirror.Core.dll', 'iPhoneMirror.UsbConfigurationSwitch.exe',
            'iPhoneMirror.dll', 'iPhoneMirror.deps.json',
            'iPhoneMirror.runtimeconfig.json', 'iPhoneMirror.Driver.dll',
            'iPhoneMirror.Driver.exe', 'iPhoneMirror.Driver.deps.json',
            'iPhoneMirror.Driver.runtimeconfig.json', 'hostfxr.dll',
            'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
            'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll',
            'Wireless\msvcp140.dll', 'Wireless\vcruntime140.dll',
            'Wireless\vcruntime140_1.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $required) -PathType Leaf)) {
            throw "Installer payload is missing: $required"
        }
    }
    $versionedDac = @(Get-ChildItem -LiteralPath $SourceDirectory `
        -Filter 'mscordaccore_amd64_amd64_*.dll' -File)
    if ($versionedDac.Count -ne 1) {
        throw 'Installer payload must contain exactly one versioned .NET DAC.'
    }
    $appleSupportPackage = Join-Path $SourceDirectory 'AppleMobileDeviceSupport64.msi'
    if (Test-Path -LiteralPath $appleSupportPackage -PathType Leaf) {
        . (Join-Path $Root 'scripts\AppleSupportPackage.ps1')
        [void](Assert-TrustedAppleSupportPackage $appleSupportPackage)
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $compiler = & (Join-Path $Root 'scripts\prepare_inno_setup.ps1') |
        Select-Object -Last 1
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Inno Setup compiler is missing: $compiler"
    }
    & $compiler "/DMyAppVersion=$Version" "/DMyNumericVersion=$numericVersion" `
        "/DMySourceDir=$SourceDirectory" "/DMyOutputDir=$OutputDirectory" `
        (Join-Path $Root 'installer\iPhoneMirror.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        throw "Installer output is missing: $expectedPath"
    }
    $setupVersion = (Get-Item -LiteralPath $expectedPath).VersionInfo.ProductVersion
    $actualSetupVersion = ($setupVersion -split '\+', 2)[0].Trim()
    if ($actualSetupVersion -ne $numericVersion) {
        throw "Installer product version mismatch: expected $numericVersion, got $setupVersion"
    }
    Write-Output $expectedPath
}
finally { Pop-Location }
