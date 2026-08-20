[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoPublish,
    [switch]$IncludeMediaOutputRuntime,
    [switch]$OmitMediaOutputRuntime,
    [string]$AppleSupportPackagePath,
    [switch]$ConfirmAppleRedistributionRights
)

$ErrorActionPreference = 'Stop'
if ($IncludeMediaOutputRuntime -and $OmitMediaOutputRuntime) {
    throw '-IncludeMediaOutputRuntime and -OmitMediaOutputRuntime cannot be used together.'
}
$UseMediaOutputRuntime = -not $OmitMediaOutputRuntime
if ($NoPublish -and -not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
    throw '-AppleSupportPackagePath cannot be used with -NoPublish.'
}
# .NET Framework MSBuild rejects environment blocks containing both Path and PATH.
$ProcessEnvironment = [Environment]::GetEnvironmentVariables()
$PathEntries = @($ProcessEnvironment.GetEnumerator() |
    Where-Object { $_.Key -ieq 'Path' })
if ($PathEntries.Count -gt 1) {
    $CanonicalPath = ($PathEntries | Where-Object { $_.Key -ceq 'Path' } |
        Select-Object -First 1).Value
    if ($null -eq $CanonicalPath) { $CanonicalPath = $PathEntries[0].Value }
    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $CanonicalPath, 'Process')
}
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CMake = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$CTest = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe'
$WirelessRoot = Join-Path $Root 'third_party\airplay-server'
$WirelessManifest = Join-Path $WirelessRoot 'SHA256SUMS.txt'
$ExpectedWirelessManifestPaths = @(
    'bin/x64/airplay2dll.dll',
    'bin/x64/avcodec-58.dll',
    'bin/x64/avutil-56.dll',
    'bin/x64/dnssd.dll',
    'bin/x64/swresample-3.dll',
    'bin/x64/swscale-5.dll'
)
$PrepareMediaOutputRuntime = Join-Path $Root 'scripts\prepare_ffmpeg.ps1'
$PrepareVcRuntime = Join-Path $Root 'scripts\prepare_vc_runtime.ps1'
$PrepareLibUsb0Runtime = Join-Path $Root 'scripts\prepare_libusb0_runtime.ps1'
$AppleSupportPackageTools = Join-Path $Root 'scripts\AppleSupportPackage.ps1'
$MediaOutputManifestPath = Join-Path $Root 'scripts\ffmpeg-runtime-manifest.psd1'
if (-not (Test-Path -LiteralPath $MediaOutputManifestPath -PathType Leaf)) {
    throw "Media-output FFmpeg manifest is missing: $MediaOutputManifestPath"
}
$MediaOutputManifest = Import-PowerShellDataFile -LiteralPath $MediaOutputManifestPath
$MediaOutputRuntimeHashes = [Collections.IDictionary]$MediaOutputManifest.Files
$MediaOutputRuntimeFiles = @($MediaOutputRuntimeHashes.Keys) + @('SOURCE.txt')

function Assert-SafeWorkspaceDirectory([string]$Path) {
    $workspace = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspace + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory is outside the workspace: $fullPath"
    }
    $current = [IO.DirectoryInfo]::new($fullPath)
    while ($null -ne $current -and
        $current.FullName.Length -ge $workspace.Length) {
        if ($current.Exists -and
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a workspace directory containing a reparse point: $($current.FullName)"
        }
        if ([string]::Equals($current.FullName, $workspace,
                [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $current.Parent
    }
}

function Assert-NoReparseChildren([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $reparse = @(Get-ChildItem -LiteralPath $Path -Recurse -Force |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
    if ($reparse.Count -ne 0) {
        throw "Refusing recursive mutation through a reparse point: $($reparse[0].FullName)"
    }
}

function Assert-ExpectedRuntimeDirectory([string]$Path, [string[]]$ExpectedFiles,
    [string]$Label, [Collections.IDictionary]$ExpectedHashes) {
    Assert-SafeWorkspaceDirectory $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory is missing: $Path"
    }
    $directory = Get-Item -LiteralPath $Path -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label directory is a reparse point: $Path"
    }
    Assert-NoReparseChildren $Path
    $children = @(Get-ChildItem -LiteralPath $Path -Force)
    $directories = @($children | Where-Object { $_.PSIsContainer })
    if ($directories.Count -ne 0) {
        throw "$Label directory contains an unexpected child directory: $($directories[0].Name)"
    }
    $actualFiles = @($children | Where-Object { -not $_.PSIsContainer } |
        ForEach-Object { $_.Name })
    $missing = @($ExpectedFiles | Where-Object { $_ -notin $actualFiles })
    $unexpected = @($actualFiles | Where-Object { $_ -notin $ExpectedFiles })
    if ($missing.Count -ne 0 -or $unexpected.Count -ne 0) {
        throw "$Label directory does not contain the expected files. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
    foreach ($entry in $ExpectedHashes.GetEnumerator()) {
        $file = Join-Path $Path ([string]$entry.Key)
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($actual -ne [string]$entry.Value) {
            throw "$Label hash mismatch for $($entry.Key): expected $($entry.Value), got $actual"
        }
    }
}

if (-not (Test-Path $CMake)) {
    $CMake = (Get-Command cmake -ErrorAction Stop).Source
}
if (-not (Test-Path $CTest)) {
    $CTest = (Get-Command ctest -ErrorAction Stop).Source
}

Push-Location $Root
try {
    Assert-SafeWorkspaceDirectory $WirelessRoot
    if (-not (Test-Path -LiteralPath $WirelessManifest)) {
        throw 'Wireless receiver hash manifest is missing.'
    }
    $manifestItem = Get-Item -LiteralPath $WirelessManifest -Force
    if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Wireless receiver hash manifest must not be a reparse point.'
    }
    $WirelessHashes = foreach ($line in Get-Content -LiteralPath $WirelessManifest) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') {
            throw "Invalid wireless receiver hash entry: $line"
        }
        [PSCustomObject]@{ Hash = $Matches[1].ToLowerInvariant(); Path = $Matches[2] }
    }
    $manifestPaths = @($WirelessHashes | ForEach-Object { $_.Path } |
        Select-Object -Unique)
    $missingManifestPaths = @($ExpectedWirelessManifestPaths |
        Where-Object { $_ -notin $manifestPaths })
    $unexpectedManifestPaths = @($manifestPaths |
        Where-Object { $_ -notin $ExpectedWirelessManifestPaths })
    if ($missingManifestPaths.Count -ne 0 -or
        $unexpectedManifestPaths.Count -ne 0 -or
        @($WirelessHashes).Count -ne $ExpectedWirelessManifestPaths.Count) {
        throw 'Wireless receiver hash manifest does not exactly cover the expected binaries.'
    }
    foreach ($entry in $WirelessHashes) {
        $source = Join-Path $WirelessRoot ($entry.Path -replace '/', '\')
        Assert-SafeWorkspaceDirectory (Split-Path -Parent $source)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Wireless receiver artifact is missing: $($entry.Path)"
        }
        $sourceItem = Get-Item -LiteralPath $source -Force
        if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Wireless receiver artifact is a reparse point: $($entry.Path)"
        }
        $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Hash) {
            throw "Wireless receiver artifact hash mismatch: $($entry.Path)"
        }
    }

    & $CMake --preset windows-x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed: $LASTEXITCODE" }

    $BuildPreset = "windows-x64-$($Configuration.ToLowerInvariant())"
    & $CMake --build --preset $BuildPreset --parallel
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }

    if (Test-Path 'src/App/iPhoneMirror.App.csproj') {
        $NativeDll = Join-Path $Root "build/native/src/Core/$Configuration/iPhoneMirror.Core.dll"
        $UsbConfigurationSwitch = Join-Path $Root `
            "build/native/src/Core/$Configuration/iPhoneMirror.UsbConfigurationSwitch.exe"
        $VirtualCameraDll = Join-Path $Root `
            "build/native/src/VirtualCamera/$Configuration/iPhoneMirror.VirtualCamera.dll"
        $VirtualCameraAdmin = Join-Path $Root `
            "build/native/src/VirtualCamera/$Configuration/iPhoneMirror.VirtualCamera.Admin.exe"
        $WirelessHost = Join-Path $Root `
            "build/native/src/WirelessHost/$Configuration/iPhoneMirror.WirelessHost.exe"
        $DnsSdShim = Join-Path $Root `
            "build/native/src/WirelessHost/$Configuration/dnssd.dll"
        $AppNative = Join-Path $Root 'src/App/native'
        $AppWireless = Join-Path $AppNative 'Wireless'
        $AppFfmpeg = Join-Path $AppNative 'tools\ffmpeg'
        Assert-SafeWorkspaceDirectory $AppNative
        Assert-SafeWorkspaceDirectory $AppWireless
        Assert-SafeWorkspaceDirectory $AppFfmpeg
        New-Item -ItemType Directory -Force -Path $AppNative | Out-Null
        New-Item -ItemType Directory -Force -Path $AppWireless | Out-Null
        if ($UseMediaOutputRuntime) {
            if (-not (Test-Path -LiteralPath $PrepareMediaOutputRuntime -PathType Leaf)) {
                throw "Media-output FFmpeg preparation script is missing: $PrepareMediaOutputRuntime"
            }
            & $PrepareMediaOutputRuntime -Destination $AppFfmpeg
            Assert-ExpectedRuntimeDirectory $AppFfmpeg $MediaOutputRuntimeFiles `
                'Media-output FFmpeg runtime' $MediaOutputRuntimeHashes
        }
        Copy-Item $NativeDll (Join-Path $AppNative 'iPhoneMirror.Core.dll') -Force
        Copy-Item $UsbConfigurationSwitch `
            (Join-Path $AppNative 'iPhoneMirror.UsbConfigurationSwitch.exe') -Force
        Copy-Item $VirtualCameraDll `
            (Join-Path $AppNative 'iPhoneMirror.VirtualCamera.dll') -Force
        Copy-Item $VirtualCameraAdmin `
            (Join-Path $AppNative 'iPhoneMirror.VirtualCamera.Admin.exe') -Force
        Copy-Item $WirelessHost `
            (Join-Path $AppWireless 'iPhoneMirror.WirelessHost.exe') -Force
        Copy-Item $DnsSdShim (Join-Path $AppWireless 'dnssd.dll') -Force
        Copy-Item (Join-Path $Root 'third_party/libusb/bin/x64/libusb-1.0.dll') `
            (Join-Path $AppNative 'libusb-1.0.dll') -Force
        if (-not (Test-Path -LiteralPath $PrepareLibUsb0Runtime -PathType Leaf)) {
            throw "libusb0 runtime preparation script is missing: $PrepareLibUsb0Runtime"
        }
        & $PrepareLibUsb0Runtime -DestinationDirectory $AppNative | Out-Host
        if (-not (Test-Path -LiteralPath $PrepareVcRuntime -PathType Leaf)) {
            throw "Visual C++ runtime preparation script is missing: $PrepareVcRuntime"
        }
        & $PrepareVcRuntime -DestinationDirectory $AppNative `
            -AdditionalDestinationDirectories $AppWireless | Out-Host
    }

    if (-not $SkipTests) {
        & $CTest --test-dir build/native -C $Configuration --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw "Native tests failed: $LASTEXITCODE" }

        $TestProjects = @(
            'src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj',
            'src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj',
            'src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj'
        )
        # The WPF smoke test creates real top-level windows. GitHub-hosted
        # runners do not provide an interactive desktop for reliable teardown;
        # retain it for local Windows validation and run the portable suites in CI.
        if ($env:CI -eq 'true') {
            $TestProjects = $TestProjects | Where-Object {
                $_ -ne 'src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj'
            }
        }
        foreach ($Project in $TestProjects) {
            dotnet restore $Project -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Test restore failed: $Project ($LASTEXITCODE)" }
            dotnet run --no-restore --project $Project --configuration $Configuration
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $Project ($LASTEXITCODE)" }
        }
        & (Join-Path $Root 'scripts\test_vc_runtime_version.ps1') | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "VC runtime version parser tests failed: $LASTEXITCODE"
        }
        & (Join-Path $Root 'scripts\test_apple_support_package.ps1') | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Apple support package validation tests failed: $LASTEXITCODE"
        }
    }

    if ($NoPublish) {
        foreach ($Project in @(
            'src/App/iPhoneMirror.App.csproj',
            'src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj'
        )) {
            dotnet restore $Project -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Application restore failed: $Project ($LASTEXITCODE)" }
            dotnet build $Project --no-restore --configuration $Configuration
            if ($LASTEXITCODE -ne 0) { throw "Application build failed: $Project ($LASTEXITCODE)" }
        }
    }

    if (-not $NoPublish -and (Test-Path 'src/App/iPhoneMirror.App.csproj')) {
        $PublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
        Assert-SafeWorkspaceDirectory $PublishRoot
        if (Test-Path -LiteralPath $PublishRoot) {
            Assert-NoReparseChildren $PublishRoot
            Remove-Item -LiteralPath $PublishRoot -Recurse -Force
        }
        dotnet publish src/App/iPhoneMirror.App.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            -p:IncludeBundledFfmpeg=$($UseMediaOutputRuntime.ToString().ToLowerInvariant()) `
            -p:NuGetAudit=false `
            --output outputs/iPhoneMirror
        if ($LASTEXITCODE -ne 0) { throw "WPF publish failed: $LASTEXITCODE" }

        if (-not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
            if (-not $ConfirmAppleRedistributionRights) {
                throw 'Embedding Apple software requires -ConfirmAppleRedistributionRights.'
            }
            . $AppleSupportPackageTools
            Copy-TrustedAppleSupportPackage $AppleSupportPackagePath $PublishRoot |
                Out-Host
        }

        foreach ($forbidden in @(
            (Join-Path $PublishRoot 'UsbDkHelper.dll'),
            (Join-Path $PublishRoot 'Drivers'),
            (Join-Path $PublishRoot 'install-filter.exe'),
            (Join-Path $PublishRoot 'libusb0.sys')
        )) {
            if (Test-Path -LiteralPath $forbidden) {
                if ((Get-Item -LiteralPath $forbidden -Force).PSIsContainer) {
                    Assert-SafeWorkspaceDirectory $forbidden
                    Assert-NoReparseChildren $forbidden
                    Remove-Item -LiteralPath $forbidden -Recurse -Force
                }
                else {
                    Remove-Item -LiteralPath $forbidden -Force
                }
            }
        }

        $requiredArtifacts = @(
            'iPhoneMirror.exe',
            'iPhoneMirror.Core.dll',
            'iPhoneMirror.UsbConfigurationSwitch.exe',
            'iPhoneMirror.VirtualCamera.dll',
            'iPhoneMirror.VirtualCamera.Admin.exe',
            'libusb-1.0.dll',
            'libusb0.dll',
            'msvcp140.dll',
            'vcruntime140.dll',
            'vcruntime140_1.dll',
            'LICENSE',
            'THIRD_PARTY_NOTICES.md',
            'CHANGELOG.md',
            'DRIVER_DEPENDENCIES.md',
            'tools\updater\Apply-ZipUpdate.ps1',
            'licenses\libusb-COPYING.txt',
            'licenses\libusb-win32-COPYING-LGPL.txt',
            'Wireless\iPhoneMirror.WirelessHost.exe',
            'Wireless\msvcp140.dll',
            'Wireless\vcruntime140.dll',
            'Wireless\vcruntime140_1.dll',
            'Wireless\airplay2dll.dll',
            'Wireless\avcodec-58.dll',
            'Wireless\avutil-56.dll',
            'Wireless\dnssd.dll',
            'Wireless\swresample-3.dll',
            'Wireless\swscale-5.dll',
            'Wireless\licenses\LICENSE-FFMPEG-LGPL-2.1.txt',
            'Wireless\licenses\LICENSE-MIT.txt',
            'Wireless\licenses\LICENSE-PLAYFAIR-GPL-3.0.md',
            'Wireless\licenses\NOTICE-FDK-AAC.txt',
            'Wireless\licenses\SOURCE.md',
            'Wireless\licenses\SHA256SUMS.txt'
        )
        foreach ($relative in $requiredArtifacts) {
            if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $relative))) {
                throw "Published artifact is missing: $relative"
            }
        }
        if ($UseMediaOutputRuntime) {
            Assert-ExpectedRuntimeDirectory (Join-Path $PublishRoot 'tools\ffmpeg') `
                $MediaOutputRuntimeFiles 'Published media-output FFmpeg runtime' `
                $MediaOutputRuntimeHashes
        }
        foreach ($entry in $WirelessHashes) {
            $published = Join-Path (Join-Path $PublishRoot 'Wireless') `
                ([IO.Path]::GetFileName($entry.Path))
            $actual = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $entry.Hash) {
                throw "Published wireless receiver hash mismatch: $([IO.Path]::GetFileName($entry.Path))"
            }
        }
    }

    if (-not $NoPublish -and
        (Test-Path 'src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj')) {
        # The driver manager is shipped beside the main executable, where the
        # application discovers it. Publishing a second identical 69 MB copy
        # under outputs/iPhoneMirror.Driver only wastes disk space.
        $LegacyDriverPublishRoot = Join-Path $Root 'outputs\iPhoneMirror.Driver'
        Assert-SafeWorkspaceDirectory $LegacyDriverPublishRoot
        if (Test-Path -LiteralPath $LegacyDriverPublishRoot) {
            Assert-NoReparseChildren $LegacyDriverPublishRoot
            Remove-Item -LiteralPath $LegacyDriverPublishRoot -Recurse -Force
        }
        $DriverPublishRoot = Join-Path $Root 'work\publish\iPhoneMirror.Driver'
        Assert-SafeWorkspaceDirectory $DriverPublishRoot
        if (Test-Path -LiteralPath $DriverPublishRoot) {
            Assert-NoReparseChildren $DriverPublishRoot
            Remove-Item -LiteralPath $DriverPublishRoot -Recurse -Force
        }
        dotnet publish src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            -p:NuGetAudit=false `
            --output $DriverPublishRoot
        if ($LASTEXITCODE -ne 0) { throw "Driver installer publish failed: $LASTEXITCODE" }

        $DriverPublishedFiles = @(Get-ChildItem -LiteralPath $DriverPublishRoot -File)
        if ($DriverPublishedFiles.Count -ne 1 -or
            $DriverPublishedFiles[0].Name -ne 'iPhoneMirror.Driver.exe') {
            throw 'Driver installer output must contain exactly one iPhoneMirror.Driver.exe file.'
        }

        $MainPublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
        if (-not (Test-Path -LiteralPath (Join-Path $MainPublishRoot 'iPhoneMirror.exe'))) {
            throw 'Main application output is missing before driver-manager integration.'
        }
        Copy-Item -LiteralPath $DriverPublishedFiles[0].FullName `
            -Destination (Join-Path $MainPublishRoot 'iPhoneMirror.Driver.exe') -Force
        if (-not (Test-Path -LiteralPath (Join-Path $MainPublishRoot 'iPhoneMirror.Driver.exe'))) {
            throw 'Driver manager was not copied into the main application output.'
        }
        Assert-SafeWorkspaceDirectory $DriverPublishRoot
        Assert-NoReparseChildren $DriverPublishRoot
        Remove-Item -LiteralPath $DriverPublishRoot -Recurse -Force

        $allowedTopLevelFiles = @(
            'iPhoneMirror.exe',
            'iPhoneMirror.Core.dll',
            'iPhoneMirror.UsbConfigurationSwitch.exe',
            'iPhoneMirror.VirtualCamera.dll',
            'iPhoneMirror.VirtualCamera.Admin.exe',
            'iPhoneMirror.Driver.exe',
            'libusb-1.0.dll',
            'libusb0.dll',
            'msvcp140.dll',
            'vcruntime140.dll',
            'vcruntime140_1.dll',
            'LICENSE',
            'THIRD_PARTY_NOTICES.md',
            'CHANGELOG.md',
            'DRIVER_DEPENDENCIES.md'
        )
        if (Test-Path -LiteralPath (Join-Path $MainPublishRoot `
                'AppleMobileDeviceSupport64.msi') -PathType Leaf) {
            $allowedTopLevelFiles += 'AppleMobileDeviceSupport64.msi'
        }
        $unexpectedFiles = @(Get-ChildItem -LiteralPath $MainPublishRoot -File | Where-Object {
            $_.Name -notin $allowedTopLevelFiles
        })
        $unexpectedDirectories = @(Get-ChildItem -LiteralPath $MainPublishRoot -Directory |
            Where-Object { $_.Name -notin @('Wireless', 'licenses', 'tools') })
        if ($unexpectedFiles.Count -ne 0 -or $unexpectedDirectories.Count -ne 0) {
            $unexpected = @($unexpectedFiles.Name) + @($unexpectedDirectories.Name)
            throw "Unexpected files in compact application output: $($unexpected -join ', ')"
        }

        # The installer uses framework files shared by both WPF entry points.
        # The portable ZIP keeps the two compressed single-file executables.
        $InstallerPublishRoot = Join-Path $Root 'outputs\iPhoneMirror.Installer'
        Assert-SafeWorkspaceDirectory $InstallerPublishRoot
        if (Test-Path -LiteralPath $InstallerPublishRoot) {
            Assert-NoReparseChildren $InstallerPublishRoot
            Remove-Item -LiteralPath $InstallerPublishRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $InstallerPublishRoot | Out-Null
        dotnet publish src/App/iPhoneMirror.App.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:IncludeBundledFfmpeg=$($UseMediaOutputRuntime.ToString().ToLowerInvariant()) `
            -p:NuGetAudit=false `
            --output $InstallerPublishRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Shared-runtime app publish failed: $LASTEXITCODE"
        }
        dotnet publish src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:NuGetAudit=false `
            --output $InstallerPublishRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Shared-runtime driver publish failed: $LASTEXITCODE"
        }
        if (-not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
            . $AppleSupportPackageTools
            Copy-TrustedAppleSupportPackage $AppleSupportPackagePath `
                $InstallerPublishRoot | Out-Host
        }
        $installerRequiredArtifacts = @(
            'iPhoneMirror.exe', 'iPhoneMirror.dll', 'iPhoneMirror.deps.json',
            'iPhoneMirror.UsbConfigurationSwitch.exe',
            'iPhoneMirror.runtimeconfig.json', 'iPhoneMirror.Driver.exe',
            'iPhoneMirror.Driver.dll', 'iPhoneMirror.Driver.deps.json',
            'iPhoneMirror.Driver.runtimeconfig.json', 'hostfxr.dll',
            'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
            'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll'
        )
        if ($UseMediaOutputRuntime) {
            $installerRequiredArtifacts += @(
                'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE.txt',
                'tools\ffmpeg\README.txt', 'tools\ffmpeg\SOURCE.txt'
            )
        }
        foreach ($required in $installerRequiredArtifacts) {
            if (-not (Test-Path -LiteralPath `
                    (Join-Path $InstallerPublishRoot $required) -PathType Leaf)) {
                throw "Shared-runtime installer artifact is missing: $required"
            }
        }
        $versionedDac = @(Get-ChildItem -LiteralPath $InstallerPublishRoot `
            -Filter 'mscordaccore_amd64_amd64_*.dll' -File)
        if ($versionedDac.Count -ne 1) {
            throw 'Shared-runtime installer must contain exactly one versioned .NET DAC.'
        }
    }

    if ($NoPublish) {
        Write-Host 'Build and tests complete (publishing skipped).' -ForegroundColor Green
    }
    else {
        Write-Host "Build complete: $Root\outputs\iPhoneMirror" -ForegroundColor Green
        Write-Host "Installer payload: $Root\outputs\iPhoneMirror.Installer" `
            -ForegroundColor Green
        Write-Host "Driver tool: $Root\outputs\iPhoneMirror\iPhoneMirror.Driver.exe" `
            -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
