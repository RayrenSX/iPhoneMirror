[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.5.8',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$PreviousVersion = '1.4.1'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$WorkRoot = Join-Path $Root ('work\installer-test-' + [Guid]::NewGuid().ToString('N'))
$InstallDirectory = Join-Path $WorkRoot 'installed'
$OutputDirectory = Join-Path $WorkRoot 'setups'
$UserDataDirectory = Join-Path $WorkRoot 'user-data'
$SourceDirectory = Join-Path $Root 'outputs\iPhoneMirror'
$Suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$AppName = "iPhoneMirror Installer Test $Suffix"
$AppId = "RayrenSX.iPhoneMirror.InstallerTest.$Suffix"
$AppPathName = "iPhoneMirror.InstallerTest.$Suffix.exe"
$AppUserModelId = "RayrenSX.iPhoneMirror.InstallerTest.$Suffix"
$StartMenuDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) $AppName
$UninstallRegistryPath =
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${AppId}_is1"

function Assert-SafeTestPath([string]$Path) {
    $workspace = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspace + '\work\installer-test-',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer test path is outside the isolated workspace area: $fullPath"
    }
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments,
    [string]$Description) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Build-TestInstaller([string]$BuildVersion) {
    $numericVersion = ($BuildVersion -split '-', 2)[0] + '.0'
    Invoke-Checked $Compiler @(
        "/DMyAppVersion=$BuildVersion",
        "/DMyNumericVersion=$numericVersion",
        "/DMySourceDir=$SourceDirectory",
        "/DMyOutputDir=$OutputDirectory",
        "/DMyAppId=$AppId",
        "/DMyAppName=$AppName",
        "/DMyDefaultDir=$InstallDirectory",
        '/DMyPrivilegesRequired=lowest',
        "/DMyAppUserModelId=$AppUserModelId",
        "/DMyUserDataDir=$UserDataDirectory",
        "/DMyAppPathName=$AppPathName",
        '/DMyCompression=none',
        '/DMySolidCompression=no',
        (Join-Path $Root 'installer\iPhoneMirror.iss')
    ) "Building installer test version $BuildVersion"
    $path = Join-Path $OutputDirectory "iPhoneMirror-Setup-v$BuildVersion-x64.exe"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer test output is missing: $path"
    }
    return $path
}

function Install-TestVersion([string]$SetupPath, [string]$Description) {
    Invoke-Checked $SetupPath @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        "/DIR=$InstallDirectory", "/LOG=$(Join-Path $WorkRoot "$Description.log")"
    ) $Description
}

function Uninstall-TestVersion([string]$UserDataArgument, [string]$Description) {
    $entry = Get-ItemProperty -LiteralPath $UninstallRegistryPath
    $uninstaller = [regex]::Match([string]$entry.UninstallString,
        '^"(?<path>[^"]+.exe)"').Groups['path'].Value
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Test uninstaller is missing: $uninstaller"
    }
    Invoke-Checked $uninstaller @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $UserDataArgument,
        "/LOG=$(Join-Path $WorkRoot "$Description.log")"
    ) $Description
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $UninstallRegistryPath) -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $UninstallRegistryPath) {
        throw "$Description did not complete within 30 seconds."
    }
    Start-Sleep -Milliseconds 500
}

Assert-SafeTestPath $WorkRoot
try {
    $publishedExecutable = Join-Path $SourceDirectory 'iPhoneMirror.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Published application is missing: $SourceDirectory"
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $Compiler = & (Join-Path $Root 'scripts\prepare_inno_setup.ps1') |
        Select-Object -Last 1
    $previousSetup = Build-TestInstaller $PreviousVersion
    $currentSetup = Build-TestInstaller $Version

    Install-TestVersion $previousSetup 'install-previous'
    $installedExecutable = Join-Path $InstallDirectory 'iPhoneMirror.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw 'Previous test version did not install the application.'
    }
    Install-TestVersion $currentSetup 'upgrade-current'

    foreach ($relative in @(
        'libusb0.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
        'Wireless\msvcp140.dll', 'Wireless\vcruntime140.dll',
        'Wireless\vcruntime140_1.dll'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory $relative) `
                -PathType Leaf)) {
            throw "Upgrade did not install required native runtime: $relative"
        }
    }

    $uninstallEntry = Get-ItemProperty -LiteralPath $UninstallRegistryPath
    if ($uninstallEntry.DisplayVersion.Trim() -ne $Version) {
        throw "Upgrade registration version mismatch: $($uninstallEntry.DisplayVersion)"
    }
    $appPathRegistry =
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$AppPathName"
    $appPath = Get-ItemPropertyValue -LiteralPath $appPathRegistry -Name '(default)'
    if (-not [string]::Equals([IO.Path]::GetFullPath($appPath),
            [IO.Path]::GetFullPath((Join-Path $InstallDirectory 'iPhoneMirror.exe')),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "App Paths registration points to an unexpected executable: $appPath"
    }
    $shortcuts = @(Get-ChildItem -LiteralPath $StartMenuDirectory -Filter '*.lnk' -File)
    if ($shortcuts.Count -lt 3) {
        throw "Expected at least three Start menu shortcuts, found $($shortcuts.Count)."
    }

    New-Item -ItemType Directory -Force -Path $UserDataDirectory | Out-Null
    $preserveMarker = Join-Path $UserDataDirectory 'preserve-marker.txt'
    Set-Content -LiteralPath $preserveMarker -Value 'preserve' -Encoding utf8
    Uninstall-TestVersion '/KEEPUSERDATA=1' 'uninstall-preserve'
    if (-not (Test-Path -LiteralPath $preserveMarker -PathType Leaf)) {
        throw 'Uninstall did not preserve user data when requested.'
    }
    if (Test-Path -LiteralPath $UninstallRegistryPath) {
        throw 'Uninstall registration remained after uninstall.'
    }
    if (Test-Path -LiteralPath $StartMenuDirectory) {
        throw 'Start menu shortcuts remained after uninstall.'
    }

    Install-TestVersion $currentSetup 'reinstall-current'
    $deleteMarker = Join-Path $UserDataDirectory 'delete-marker.txt'
    Set-Content -LiteralPath $deleteMarker -Value 'delete' -Encoding utf8
    Uninstall-TestVersion '/DELETEUSERDATA=1' 'uninstall-delete'
    if (Test-Path -LiteralPath $UserDataDirectory) {
        throw 'Uninstall did not delete isolated user data when requested.'
    }
    Write-Host 'Installer upgrade and uninstall tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $StartMenuDirectory) {
        Remove-Item -LiteralPath $StartMenuDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $WorkRoot) {
        Assert-SafeTestPath $WorkRoot
        $cleanupError = $null
        for ($attempt = 1; $attempt -le 10; ++$attempt) {
            try {
                Remove-Item -LiteralPath $WorkRoot -Recurse -Force
                $cleanupError = $null
                break
            }
            catch {
                $cleanupError = $_
                Start-Sleep -Milliseconds 500
            }
        }
        if ($cleanupError) { throw $cleanupError }
    }
}
