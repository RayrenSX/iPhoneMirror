[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.6.8',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$PreviousVersion = '1.6.7',
    [string]$RuntimeTestDirectory,
    [switch]$UseReleaseCompression
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$UsbTouchBridgeRuntimeScript = Join-Path $Root 'scripts\UsbTouchBridgeRuntime.ps1'
if (-not (Test-Path -LiteralPath $UsbTouchBridgeRuntimeScript -PathType Leaf)) {
    throw "USB touch bridge runtime validator is missing: $UsbTouchBridgeRuntimeScript"
}
. $UsbTouchBridgeRuntimeScript
$UxPlayRuntimeManifestPath = Join-Path $Root 'scripts\uxplay-runtime-manifest.psd1'
if (-not (Test-Path -LiteralPath $UxPlayRuntimeManifestPath -PathType Leaf)) {
    throw "UxPlay runtime manifest is missing: $UxPlayRuntimeManifestPath"
}
$UxPlayRuntimeManifest = Import-PowerShellDataFile -LiteralPath $UxPlayRuntimeManifestPath
$UxPlayRuntimeFiles = @($UxPlayRuntimeManifest.Files)
if ($UxPlayRuntimeFiles.Count -eq 0 -or
    @($UxPlayRuntimeFiles | Select-Object -Unique).Count -ne $UxPlayRuntimeFiles.Count -or
    @($UxPlayRuntimeFiles | Where-Object {
        [string]::IsNullOrWhiteSpace($_) -or [IO.Path]::IsPathRooted($_) -or
        $_.Split([IO.Path]::DirectorySeparatorChar) -contains '..'
    }).Count -ne 0) {
    throw 'UxPlay runtime manifest is invalid.'
}
$WorkRoot = Join-Path $Root ('work\installer-test-' + [Guid]::NewGuid().ToString('N'))
$InstallDirectory = Join-Path $WorkRoot 'installed'
$OutputDirectory = Join-Path $WorkRoot 'setups'
$UserDataDirectory = Join-Path $WorkRoot 'user-data'
$SourceDirectory = Join-Path $Root 'outputs\iPhoneMirror.Installer'
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
    $compressionArguments = if ($UseReleaseCompression) { @() } else {
        @('/DMyCompression=none', '/DMySolidCompression=no')
    }
    $compilerArguments = @(
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
        "/DMyAppPathName=$AppPathName"
    ) + $compressionArguments + @((Join-Path $Root 'installer\iPhoneMirror.iss'))
    Invoke-Checked $Compiler $compilerArguments "Building installer test version $BuildVersion"
    $path = Join-Path $OutputDirectory "iPhoneMirror-Setup-v$BuildVersion-x64.exe"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer test output is missing: $path"
    }
    return $path
}

function Install-TestVersion([string]$SetupPath, [string]$Description,
    [string]$Language = 'english') {
    Invoke-Checked $SetupPath @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        '/CLOSEAPPLICATIONS', "/LANG=$Language",
        "/DIR=$InstallDirectory", "/LOG=$(Join-Path $WorkRoot "$Description.log")"
    ) $Description
}

function Uninstall-TestVersion([string]$UserDataArgument, [string]$Description,
    [string]$Language = 'english') {
    $entry = Get-ItemProperty -LiteralPath $UninstallRegistryPath
    $uninstaller = [regex]::Match([string]$entry.UninstallString,
        '^"(?<path>[^"]+.exe)"').Groups['path'].Value
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Test uninstaller is missing: $uninstaller"
    }
    # Inno's uninstaller can delegate to a temporary child process. Wait for
    # the entire process tree before inspecting its final cleanup results.
    $process = Start-Process -FilePath $uninstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $UserDataArgument,
        "/LANG=$Language",
        "/LOG=`"$(Join-Path $WorkRoot "$Description.log")`""
    ) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($process.ExitCode)."
    }
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
$DriverProcess = $null
try {
    $publishedExecutable = Join-Path $SourceDirectory 'iPhoneMirror.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Published application is missing: $SourceDirectory"
    }
    $sourceRequiredArtifacts = @(
        'iPhoneMirror.dll', 'iPhoneMirror.Core.dll',
        'iPhoneMirror.UsbConfigurationSwitch.exe', 'iPhoneMirror.Driver.exe',
        'tools\iUsbBridge.exe', 'tools\iUsbBridge.runtime.json',
        'iPhoneMirror.Driver.exe', 'hostfxr.dll',
        'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
        'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll'
    )
    $publishedFfmpeg = 'tools\ffmpeg\ffmpeg.exe'
    if (Test-Path -LiteralPath (Join-Path $SourceDirectory $publishedFfmpeg) `
            -PathType Leaf) {
        $sourceRequiredArtifacts += $publishedFfmpeg
    }
    if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'Wireless\UxPlay\uxplay.exe')) {
      $sourceRequiredArtifacts += @($UxPlayRuntimeFiles | ForEach-Object {
        Join-Path 'Wireless\UxPlay' $_
      })
    }
    foreach ($required in $sourceRequiredArtifacts) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $required) `
                -PathType Leaf)) {
            throw "Shared installer runtime is missing: $required"
        }
    }
    Assert-UsbTouchBridgeRuntime (Join-Path $SourceDirectory 'tools') `
        'Shared installer USB touch bridge runtime'
    $sourceVersionedDac = @(Get-ChildItem -LiteralPath $SourceDirectory `
        -Filter 'mscordaccore_amd64_amd64_*.dll' -File)
    if ($sourceVersionedDac.Count -ne 1) {
        throw 'Shared installer runtime must contain exactly one versioned .NET DAC.'
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $Compiler = & (Join-Path $Root 'scripts\prepare_inno_setup.ps1') |
        Select-Object -Last 1
    $previousSetup = Build-TestInstaller $PreviousVersion
    $currentSetup = Build-TestInstaller $Version

    Install-TestVersion $previousSetup 'install-previous' 'chinesetrad'
    $installedExecutable = Join-Path $InstallDirectory 'iPhoneMirror.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw 'Previous test version did not install the application.'
    }
    $installedDriver = Join-Path $InstallDirectory 'iPhoneMirror.Driver.exe'
    $DriverProcess = Start-Process -FilePath $installedDriver `
        -WorkingDirectory $InstallDirectory -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    if ($DriverProcess.HasExited) {
        throw 'Installed driver manager exited before the upgrade test.'
    }
    Install-TestVersion $currentSetup 'upgrade-current' 'chinesetrad'
    $DriverProcess.Refresh()
    if (-not $DriverProcess.HasExited) {
        throw 'Upgrade did not close the running driver manager.'
    }

    $installedRequiredArtifacts = @(
        'libusb0.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
        'iPhoneMirror.dll', 'iPhoneMirror.Core.dll',
        'iPhoneMirror.UsbConfigurationSwitch.exe', 'iPhoneMirror.Driver.exe',
        'tools\iUsbBridge.exe', 'tools\iUsbBridge.runtime.json',
        'iPhoneMirror.Driver.exe', 'hostfxr.dll',
        'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
        'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll',
        'Wireless\msvcp140.dll', 'Wireless\vcruntime140.dll',
        'Wireless\vcruntime140_1.dll', 'Wireless\avcodec-58.dll',
        'Wireless\avutil-56.dll', 'Wireless\swresample-3.dll', 'Wireless\swscale-5.dll'
    )
    if ($publishedFfmpeg -in $sourceRequiredArtifacts) {
        $installedRequiredArtifacts += $publishedFfmpeg
    }
    if ('Wireless\UxPlay\uxplay.exe' -in $sourceRequiredArtifacts) {
      $installedRequiredArtifacts += @($UxPlayRuntimeFiles | ForEach-Object {
        Join-Path 'Wireless\UxPlay' $_
      })
    }
    foreach ($relative in $installedRequiredArtifacts) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory $relative) `
                -PathType Leaf)) {
            throw "Upgrade did not install required native runtime: $relative"
        }
        if ((Get-FileHash -LiteralPath (Join-Path $InstallDirectory $relative)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $SourceDirectory $relative)).Hash) {
            throw "Installed runtime differs from the published payload: $relative"
        }
    }
    # A launch check covers only dependencies reached on that path. Compare
    # every published file too, including lazy-loaded WPF/native resources.
    $publishedFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse)
    foreach ($file in $publishedFiles) {
        $relative = [IO.Path]::GetRelativePath($SourceDirectory, $file.FullName)
        $installedFile = Join-Path $InstallDirectory $relative
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "Installed payload file is missing or changed: $relative"
        }
    }
    Write-Host "Installed payload: $($publishedFiles.Count) files match published SHA-256 values."
    Assert-UsbTouchBridgeRuntime (Join-Path $InstallDirectory 'tools') `
        'Installed USB touch bridge runtime'
    $installedBridgeLibUsb0 = Join-Path $InstallDirectory `
        'tools\_internal\libusb0.dll'
    if (-not (Test-Path -LiteralPath $installedBridgeLibUsb0 -PathType Leaf) -or
        (Get-FileHash -LiteralPath $installedBridgeLibUsb0 -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $InstallDirectory 'libusb0.dll') `
            -Algorithm SHA256).Hash) {
        throw 'Upgrade did not install the bridge-local libusb0 runtime.'
    }
    $installedVersionedDac = @(Get-ChildItem -LiteralPath $InstallDirectory `
        -Filter 'mscordaccore_amd64_amd64_*.dll' -File)
    if ($installedVersionedDac.Count -ne 1) {
        throw 'Upgrade did not install exactly one versioned .NET DAC.'
    }

    if ($RuntimeTestDirectory) {
        # Only add the harness; all app/runtime files remain the ones installed
        # by Setup. The harness resolves the production assembly and components
        # next to itself, so this tests deployment rather than its build folder.
        foreach ($name in @('IPhoneMirror.App.Runtime.Tests.exe',
            'IPhoneMirror.App.Runtime.Tests.dll', 'IPhoneMirror.App.Runtime.Tests.deps.json',
            'IPhoneMirror.App.Runtime.Tests.runtimeconfig.json')) {
            Copy-Item -LiteralPath (Join-Path $RuntimeTestDirectory $name) -Destination $InstallDirectory
        }
        $runtimeRunner = Join-Path $InstallDirectory 'IPhoneMirror.App.Runtime.Tests.exe'
        Invoke-Checked $runtimeRunner @('--component-runtime') 'Installed component functional regression'
        Invoke-Checked $runtimeRunner @('--component-audio-formats',
            (Join-Path $SourceDirectory 'tools\ffmpeg\ffmpeg.exe')) 'Installed source audio compatibility'
        # Harness files are not installer-owned. Remove only these four copied
        # files before checking that the product uninstalls cleanly.
        foreach ($name in @('IPhoneMirror.App.Runtime.Tests.exe',
            'IPhoneMirror.App.Runtime.Tests.dll', 'IPhoneMirror.App.Runtime.Tests.deps.json',
            'IPhoneMirror.App.Runtime.Tests.runtimeconfig.json')) {
            Remove-Item -LiteralPath (Join-Path $InstallDirectory $name)
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
    $appIdentityRegistry =
        "HKCU:\Software\Classes\AppUserModelId\$AppUserModelId"
    $appIdentity = Get-ItemProperty -LiteralPath $appIdentityRegistry
    if ($appIdentity.DisplayName -ne $AppName -or
        -not [string]::Equals([IO.Path]::GetFullPath($appIdentity.IconUri),
            [IO.Path]::GetFullPath($installedExecutable),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'AppUserModelID registration does not expose the installed icon.'
    }
    $shortcuts = @(Get-ChildItem -LiteralPath $StartMenuDirectory -Filter '*.lnk' -File)
    if ($shortcuts.Count -lt 3) {
        throw "Expected at least three Start menu shortcuts, found $($shortcuts.Count)."
    }

    New-Item -ItemType Directory -Force -Path $UserDataDirectory | Out-Null
    $preserveMarker = Join-Path $UserDataDirectory 'preserve-marker.txt'
    Set-Content -LiteralPath $preserveMarker -Value 'preserve' -Encoding utf8
    Uninstall-TestVersion '/KEEPUSERDATA=1' 'uninstall-preserve' 'chinesetrad'
    if (-not (Test-Path -LiteralPath $preserveMarker -PathType Leaf)) {
        throw 'Uninstall did not preserve user data when requested.'
    }
    if (Test-Path -LiteralPath $UninstallRegistryPath) {
        throw 'Uninstall registration remained after uninstall.'
    }
    if (Test-Path -LiteralPath $appIdentityRegistry) {
        throw 'AppUserModelID registration remained after uninstall.'
    }
    if (Test-Path -LiteralPath $StartMenuDirectory) {
        throw 'Start menu shortcuts remained after uninstall.'
    }

    Install-TestVersion $currentSetup 'reinstall-current' 'chinesetrad'
    $deleteMarker = Join-Path $UserDataDirectory 'delete-marker.txt'
    Set-Content -LiteralPath $deleteMarker -Value 'delete' -Encoding utf8
    Uninstall-TestVersion '/DELETEUSERDATA=1' 'uninstall-delete' 'chinesetrad'
    if (Test-Path -LiteralPath $UserDataDirectory) {
        throw 'Uninstall did not delete isolated user data when requested.'
    }
    Write-Host 'Installer upgrade and uninstall tests passed.' -ForegroundColor Green
}
finally {
    if ($null -ne $DriverProcess -and -not $DriverProcess.HasExited) {
        Stop-Process -Id $DriverProcess.Id -Force -ErrorAction SilentlyContinue
    }
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
