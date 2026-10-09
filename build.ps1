[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoPublish,
    [switch]$TestBuild,
    [switch]$IncludeMediaOutputRuntime,
    [switch]$OmitMediaOutputRuntime,
    [string]$FfmpegRuntimeManifestPath,
    [switch]$IncludeUxPlayRuntime,
    [switch]$OmitUxPlayRuntime,
    [switch]$PrepareUxPlayComponent,
    [string]$AppleSupportPackagePath,
    [switch]$ConfirmAppleRedistributionRights,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
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
if ($IncludeMediaOutputRuntime -and $OmitMediaOutputRuntime) {
    throw '-IncludeMediaOutputRuntime and -OmitMediaOutputRuntime cannot be used together.'
}
$UseMediaOutputRuntime = -not $OmitMediaOutputRuntime
if ($IncludeUxPlayRuntime -and $OmitUxPlayRuntime) {
    throw '-IncludeUxPlayRuntime and -OmitUxPlayRuntime cannot be used together.'
}
# Standard releases publish UxPlay separately. Include is an explicit offline
# bundle; Omit skips building the optional asset on development machines.
$UseUxPlayRuntime = [bool]$IncludeUxPlayRuntime
$BuildUxPlayComponent = -not $OmitUxPlayRuntime

if ($TestBuild -and $NoPublish) {
    throw '-TestBuild requires publishing.'
}
if ($TestBuild -and $PSBoundParameters.ContainsKey('Version')) {
    throw '-TestBuild chooses the next test version automatically; do not pass -Version.'
}
if ($NoPublish -and -not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
    throw '-AppleSupportPackagePath cannot be used with -NoPublish.'
}
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($PrepareUxPlayComponent -and ($TestBuild -or $OmitUxPlayRuntime -or -not $Version)) {
    throw '-PrepareUxPlayComponent requires an explicit release -Version and cannot be used with -TestBuild or -OmitUxPlayRuntime.'
}
# Downloads resolve the latest public Release at runtime. The embedded descriptor
# supplies a minimum component version and trusted legacy-cache hashes; its old
# URL must not gate development/distribution builds.
$OutputsRoot = Join-Path $Root 'outputs'
$TestVersionRecord = Join-Path $Root 'work\test-build-version.txt'
[xml]$appProject = Get-Content -LiteralPath (Join-Path $Root 'src\App\iPhoneMirror.App.csproj') -Raw
$projectVersion = [string]$appProject.Project.PropertyGroup.Version |
    Select-Object -First 1

if ($TestBuild) {
    if ($projectVersion -notmatch '^(?<base>\d+\.\d+\.\d+)-test(?<number>\d+)$') {
        throw "Application project version must be base-testN for test builds: $projectVersion"
    }
    $baseVersion = $Matches['base']
    $currentNumber = [int]$Matches['number']
    $knownVersions = @($projectVersion)
    if (Test-Path -LiteralPath $TestVersionRecord -PathType Leaf) {
        $knownVersions += (Get-Content -LiteralPath $TestVersionRecord -Raw).Trim()
    }
    if (Test-Path -LiteralPath $OutputsRoot -PathType Container) {
        $candidateExecutables = @(Get-ChildItem -LiteralPath $OutputsRoot -Filter 'iPhoneMirror.exe' -File) +
            @(Get-ChildItem -LiteralPath $OutputsRoot -Directory | ForEach-Object {
                Get-ChildItem -LiteralPath $_.FullName -Filter 'iPhoneMirror.exe' -File
            })
        $knownVersions += @($candidateExecutables | ForEach-Object {
            ($_.VersionInfo.ProductVersion -split '\+', 2)[0]
        })
    }
    foreach ($knownVersion in $knownVersions) {
        if ($knownVersion -match ('^' + [regex]::Escape($baseVersion) + '-test(?<number>\d+)$')) {
            $currentNumber = [Math]::Max($currentNumber, [int]$Matches['number'])
        }
    }
    $Version = "$baseVersion-test$($currentNumber + 1)"
    Write-Host "Test build version: $Version"
}
$VersionProperty = if ([string]::IsNullOrWhiteSpace($Version)) {
    $null
} else {
    "-p:Version=$Version"
}

$UsbControlRoot = if ([string]::IsNullOrWhiteSpace($env:IPHONE_MIRROR_USB_BRIDGE_ROOT)) {
    Join-Path $Root 'scripts\usb-bridge-recipe'
} else {
    [IO.Path]::GetFullPath($env:IPHONE_MIRROR_USB_BRIDGE_ROOT)
}
$UsbControlBuild = Join-Path $UsbControlRoot 'build.ps1'
$UsbControlSource = Join-Path $Root 'tools\usb_touch_bridge.py'
$UsbTouchBridgeOutput = Join-Path $Root 'dist\iUsbBridge.exe'
$UsbTouchBridgeRuntimeManifest = Join-Path $Root 'dist\iUsbBridge.runtime.json'
$UsbTouchBridgeRuntimeTools = Join-Path $Root 'scripts\UsbTouchBridgeRuntime.ps1'
$UsbControlEnvironment = Join-Path $Root 'work\usb-touch-bridge-python'
$UsbControlPython = Join-Path $UsbControlEnvironment 'Scripts\python.exe'

if (-not (Test-Path -LiteralPath $UsbTouchBridgeRuntimeTools -PathType Leaf)) {
    throw "USB touch bridge runtime validation script is missing: $UsbTouchBridgeRuntimeTools"
}
. $UsbTouchBridgeRuntimeTools
. (Join-Path $Root 'scripts\UsbBridgeBuildSource.ps1')

function Build-UsbTouchBridge {
    # Use the versioned Python recipe so upstream backend migrations cannot
    # replace the audited source in tools with an incompatible executable.
    foreach ($required in @($UsbControlBuild, $UsbControlSource)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "USB touch bridge build input is missing: $required"
        }
    }

    $stageRoot = Join-Path $Root 'work\usb-bridge-build'
    $stage = New-UsbBridgeBuildSource -RecipeRoot $UsbControlRoot `
        -SourceRoot (Join-Path $Root 'tools') -WorkRoot $stageRoot
    try {
        & (Join-Path $stage 'build.ps1') -BridgeOnly `
            -BridgeOutputPath $UsbTouchBridgeOutput -EnvironmentPath $UsbControlEnvironment
        if ($LASTEXITCODE -ne 0) {
            throw "USB touch bridge build failed: $LASTEXITCODE"
        }
    }
    finally {
        Remove-UsbBridgeBuildSource -Stage $stage -WorkRoot $stageRoot
    }
    if (-not (Test-Path -LiteralPath $UsbTouchBridgeOutput -PathType Leaf) -or
        -not (Test-Path -LiteralPath $UsbTouchBridgeRuntimeManifest -PathType Leaf)) {
        throw 'USB touch bridge output is incomplete.'
    }
    if (-not (Test-Path -LiteralPath $UsbControlPython -PathType Leaf)) {
        throw "USB touch bridge Python environment is missing: $UsbControlPython"
    }
    Assert-UsbTouchBridgeRuntime -Directory (Join-Path $Root 'dist') `
        -Label 'Built USB touch bridge'

    # Exercise the packaged executable before it becomes application content.
    # The bridge help includes localized text. Run it with file-backed UTF-8
    # redirection so runner console encoding cannot affect the smoke test.
    $helpOutput = Join-Path $Root 'work\iUsbBridge-help.txt'
    $helpError = Join-Path $Root 'work\iUsbBridge-help.err.txt'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $helpOutput) | Out-Null
    $previousPythonIoEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        $helpProcess = Start-Process -FilePath $UsbTouchBridgeOutput -ArgumentList '--help' `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput $helpOutput `
            -RedirectStandardError $helpError
        # Some localized bridge builds return 1 after printing help. The
        # manifest/hash validation above is the release gate, so keep this
        # optional probe silent and non-blocking.
    }
    finally {
        $env:PYTHONIOENCODING = $previousPythonIoEncoding
        Remove-Item -LiteralPath $helpOutput, $helpError -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-CMakeTool([string]$Name) {
    $candidateRoots = @(
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'),
        [Environment]::GetEnvironmentVariable('ProgramFiles')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $candidates = @($candidateRoots | ForEach-Object {
        Join-Path $_ ("Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\$Name.exe")
    }) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    if (@($candidates).Count -gt 0) { return @($candidates)[0] }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $vswhere = if ([string]::IsNullOrWhiteSpace($programFilesX86)) { $null } else {
        Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if ($vswhere -and (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        $installations = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        foreach ($installation in @($installations)) {
            $candidate = Join-Path $installation "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\$Name.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    throw "$Name.exe was not found. Install Visual Studio C++/CMake tools or add $Name to PATH."
}

function Invoke-NativeToolWithSanitizedEnvironment(
    [string]$FilePath,
    [string[]]$ArgumentList,
    [switch]$CleanManagedTestEnvironment
) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $Root
    $startInfo.UseShellExecute = $false

    # Some Windows hosts expose both Path and PATH in the inherited block.
    # .NET Framework MSBuild treats those as duplicate Hashtable keys when it
    # launches CL.exe. Rebuild the child environment case-insensitively so the
    # compiler always receives exactly one canonical Path entry.
    $environment = [Environment]::GetEnvironmentVariables()
    $canonicalPath = @($environment.GetEnumerator() |
        Where-Object { $_.Key -ceq 'Path' } |
        Select-Object -First 1).Value
    if ([string]::IsNullOrWhiteSpace($canonicalPath)) {
        $canonicalPath = @($environment.GetEnumerator() |
            Where-Object { $_.Key -ieq 'Path' } |
            Select-Object -First 1).Value
    }
    $startInfo.Environment.Clear()
    $seenEnvironmentNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $environment.GetEnumerator()) {
        $name = [string]$entry.Key
        if ($name -ieq 'Path' -or -not $seenEnvironmentNames.Add($name)) {
            continue
        }
        # SDK/CI runtime overrides are not part of the installed application's
        # environment. Keep them out of test children so elevation tests reach
        # their intended checks; production rejection remains unchanged.
        if ($CleanManagedTestEnvironment -and
            $name -match '^(?i:COMPLUS_|COR_|CORECLR_|DOTNET_|DEVPATH$|APPDOMAIN_MANAGER_ASM$|APPDOMAIN_MANAGER_TYPE$|CLRConfigFile$)') {
            continue
        }
        $startInfo.Environment[$name] = [string]$entry.Value
    }
    $startInfo.Environment['Path'] = $canonicalPath
    # Do not reconnect to long-lived MSBuild worker nodes that may have been
    # created before the environment was sanitized.
    $startInfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    # ArgumentList is unavailable on the .NET Framework ProcessStartInfo used by
    # Windows PowerShell 5.1. Fall back to a Windows-command-line-safe string so
    # release builds work on both PowerShell hosts.
    $quotedArguments = foreach ($argument in $ArgumentList) {
        $value = [string]$argument
        if ([string]::IsNullOrEmpty($value)) {
            '""'
        }
        elseif ($value -notmatch '[\s"]') {
            $value
        }
        else {
            $escapedValue = [regex]::Replace($value, '(\\*)"', '$1$1\\"')
            $escapedValue = [regex]::Replace($escapedValue, '(\\+)$', '$1$1')
            '"' + $escapedValue + '"'
        }
    }
    $startInfo.Arguments = $quotedArguments -join ' '

    # Construct the process explicitly.  On some PowerShell/.NET hosts the
    # static Start overload can return $null even though the child was created,
    # which then turns the following WaitForExit call into a null dereference
    # and aborts the release package.
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start native tool: $FilePath"
    }
    $process.WaitForExit()
    return $process.ExitCode
}
$CMake = Resolve-CMakeTool 'cmake'
$CTest = Resolve-CMakeTool 'ctest'
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
$PrepareUxPlayRuntime = Join-Path $Root 'scripts\prepare_uxplay.ps1'
$UxPlayRuntimeManifestPath = Join-Path $Root 'scripts\uxplay-runtime-manifest.psd1'
$PrepareLibUsb0Runtime = Join-Path $Root 'scripts\prepare_libusb0_runtime.ps1'
$AppleSupportPackageTools = Join-Path $Root 'scripts\AppleSupportPackage.ps1'
$MediaOutputManifestPath = Join-Path $Root 'scripts\ffmpeg-runtime-manifest.psd1'
if ($FfmpegRuntimeManifestPath) { $MediaOutputManifestPath = [IO.Path]::GetFullPath($FfmpegRuntimeManifestPath) }
# Media URLs are not restricted to AAC/H.264. The compact allowlist broke
# previously supported PCM/WMA/AIFF/MP2 inputs; retain the full pinned runtime
# by default until a smaller profile passes the complete compatibility audit.
if (-not (Test-Path -LiteralPath $MediaOutputManifestPath -PathType Leaf)) {
    throw "Media-output FFmpeg manifest is missing: $MediaOutputManifestPath"
}
$MediaOutputManifest = Import-PowerShellDataFile -LiteralPath $MediaOutputManifestPath
$MediaOutputRuntimeHashes = [Collections.IDictionary]$MediaOutputManifest.Files
$MediaOutputRuntimeFiles = @(@($MediaOutputRuntimeHashes.Keys) + @('SOURCE.txt') | Select-Object -Unique)
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

function Clear-TestOutputs {
    Assert-SafeWorkspaceDirectory $OutputsRoot
    if (-not (Test-Path -LiteralPath $OutputsRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $OutputsRoot | Out-Null
        return
    }
    Assert-NoReparseChildren $OutputsRoot
    foreach ($item in @(Get-ChildItem -LiteralPath $OutputsRoot -Force)) {
        if ($item.PSIsContainer) {
            if ($item.Name -in @('releases', 'release-staging')) { continue }
            if ($item.Name -in @('Assets', 'Wireless', 'licenses', 'tools', 'zh-Hans', 'zh-Hant')) {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force
                continue
            }
            # A cancelled test publish can leave its versioned staging
            # directory behind before an executable has been written.
            $children = @(Get-ChildItem -LiteralPath $item.FullName -Force)
            if ($item.Name -match '^iPhoneMirror-test\d+$' -and
                $children.Count -eq 0) {
                Remove-Item -LiteralPath $item.FullName -Force
                continue
            }
            $appExe = Join-Path $item.FullName 'iPhoneMirror.exe'
            $driverExe = Join-Path $item.FullName 'iPhoneMirror.Driver.exe'
            $candidate = if (Test-Path -LiteralPath $appExe -PathType Leaf) {
                $appExe
            } elseif (Test-Path -LiteralPath $driverExe -PathType Leaf) {
                $driverExe
            } else { $null }
            if ($null -ne $candidate -and
                (Get-Item -LiteralPath $candidate).VersionInfo.ProductVersion -match '-test\d+') {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force
                continue
            }
            throw "Unrecognized output directory; refusing to clean it: $($item.FullName)"
        }
        # Versioned final release packages are kept; all other root files are
        # owned by the test publish, including old test archives and PDBs.
        if ($item.Name -match '^iPhoneMirror(?:-Setup)?-v\d+\.\d+\.\d+-(?!test)') {
            continue
        }
        Remove-Item -LiteralPath $item.FullName -Force
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
    if ($TestBuild) { Clear-TestOutputs }
    Build-UsbTouchBridge

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
    $RuntimeIntegritySource = Join-Path $Root `
        'src\App\Services\RuntimeBinaryIntegrity.cs'
    if (Test-Path -LiteralPath $RuntimeIntegritySource -PathType Leaf) {
        $RuntimeIntegrityText = Get-Content -LiteralPath $RuntimeIntegritySource -Raw
        foreach ($entry in $WirelessHashes) {
            $RuntimeName = [IO.Path]::GetFileName($entry.Path)
            $RuntimeHashPattern = '(?ms)\["' + [regex]::Escape($RuntimeName) +
                '"\]\s*=\s*"([0-9a-fA-F]{64})"'
            $RuntimeHashMatches = [regex]::Matches(
                $RuntimeIntegrityText, $RuntimeHashPattern)
            if ($RuntimeHashMatches.Count -ne 1 -or
                $RuntimeHashMatches[0].Groups[1].Value -ne $entry.Hash) {
                throw "Application runtime integrity hash is stale: $($entry.Path)"
            }
        }
    }

    $PidGenerator = Join-Path $Root 'scripts\generate_apple_mobile_capture_pids.ps1'
    if (-not (Test-Path -LiteralPath $PidGenerator -PathType Leaf)) {
        throw "Apple mobile capture PID generator is missing: $PidGenerator"
    }
    & $PidGenerator -Root $Root
    if (-not $?) { throw 'Apple mobile capture PID generation failed.' }

    $nativeVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
        $projectVersion
    } else { $Version }
    $cmakeConfigureArguments = @('--preset', 'windows-x64',
        "-DIPHONEMIRROR_VERSION=$nativeVersion")
    $cmakeConfigureExitCode = Invoke-NativeToolWithSanitizedEnvironment `
        $CMake $cmakeConfigureArguments
    if ($cmakeConfigureExitCode -ne 0) {
        throw "CMake configure failed: $cmakeConfigureExitCode"
    }

    $BuildPreset = "windows-x64-$($Configuration.ToLowerInvariant())"
    $cmakeBuildExitCode = Invoke-NativeToolWithSanitizedEnvironment $CMake `
        @('--build', '--preset', $BuildPreset, '--parallel', '--',
            '/nodeReuse:false')
    if ($cmakeBuildExitCode -ne 0) {
        throw "Native build failed: $cmakeBuildExitCode"
    }

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
        $UxPlayHost = Join-Path $Root `
            "build/native/src/UxPlayHost/$Configuration/iPhoneMirror.UxPlayHost.exe"
        $DnsSdRuntime = Join-Path $WirelessRoot 'bin\x64\dnssd.dll'
        $AppNative = Join-Path $Root 'src/App/native'
        $AppWireless = Join-Path $AppNative 'Wireless'
        $AppUxPlay = Join-Path $AppWireless 'UxPlay'
        if (-not $UseUxPlayRuntime) {
            if (Test-Path -LiteralPath $AppUxPlay) {
                Assert-SafeWorkspaceDirectory $AppUxPlay
                Assert-NoReparseChildren $AppUxPlay
                Remove-Item -LiteralPath $AppUxPlay -Recurse -Force
            }
            $AppUxPlay = Join-Path $Root 'outputs\uxplay-component-runtime'
        }
        $AppFfmpeg = Join-Path $AppNative 'tools\ffmpeg'
        Assert-SafeWorkspaceDirectory $AppNative
        Assert-SafeWorkspaceDirectory $AppWireless
        Assert-SafeWorkspaceDirectory $AppUxPlay
        Assert-SafeWorkspaceDirectory $AppFfmpeg
        New-Item -ItemType Directory -Force -Path $AppNative | Out-Null
        New-Item -ItemType Directory -Force -Path $AppWireless | Out-Null
        if ($BuildUxPlayComponent) {
            New-Item -ItemType Directory -Force -Path $AppUxPlay | Out-Null
        }
        if ($UseMediaOutputRuntime) {
            if (-not (Test-Path -LiteralPath $PrepareMediaOutputRuntime -PathType Leaf)) {
                throw "Media-output FFmpeg preparation script is missing: $PrepareMediaOutputRuntime"
            }
            & $PrepareMediaOutputRuntime -Destination $AppFfmpeg -RuntimeManifestPath $MediaOutputManifestPath
            Assert-ExpectedRuntimeDirectory $AppFfmpeg $MediaOutputRuntimeFiles `
                'Media-output FFmpeg runtime' $MediaOutputRuntimeHashes
            $integrityDirectory = Join-Path $AppNative 'components'
            New-Item -ItemType Directory -Force -Path $integrityDirectory | Out-Null
            [IO.File]::WriteAllText((Join-Path $integrityDirectory 'ffmpeg.sha256'),
                [string]$MediaOutputRuntimeHashes['ffmpeg.exe'], [Text.UTF8Encoding]::new($false))
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
        if ($BuildUxPlayComponent) {
            Copy-Item $UxPlayHost `
                (Join-Path $AppUxPlay 'iPhoneMirror.UxPlayHost.exe') -Force
            Copy-Item (Join-Path $Root 'third_party\uxplay\SOURCE.md') `
                (Join-Path $AppUxPlay 'SOURCE.md') -Force
            if (-not (Test-Path -LiteralPath $PrepareUxPlayRuntime -PathType Leaf)) {
                throw "UxPlay preparation script is missing: $PrepareUxPlayRuntime"
            }
            & $PrepareUxPlayRuntime -Destination $AppUxPlay -DnsSdPath $DnsSdRuntime | Out-Host
            # prepare_uxplay refreshes the optional runtime directory atomically;
            # restore the iPhoneMirror IPC adapter after that refresh.
            Copy-Item $UxPlayHost `
                (Join-Path $AppUxPlay 'iPhoneMirror.UxPlayHost.exe') -Force
            foreach ($relative in $UxPlayRuntimeFiles) {
                if (-not (Test-Path -LiteralPath (Join-Path $AppUxPlay $relative) -PathType Leaf)) {
                    throw "Prepared UxPlay runtime is missing: $relative"
                }
            }
            if ($PrepareUxPlayComponent) {
                & (Join-Path $Root 'scripts\package_uxplay_component.ps1') `
                    -SourceDirectory $AppUxPlay -Version $Version
            }
        }
        # Ship the hash-pinned receiver runtime, not the build-local shim. The
        # latter is compiled for native tests and is not reproducible byte for byte.
        Copy-Item $DnsSdRuntime (Join-Path $AppWireless 'dnssd.dll') -Force
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

        $libUsbDirectory = Join-Path $Root 'third_party\libusb\bin\x64'
        $env:PATH = "$libUsbDirectory$([IO.Path]::PathSeparator)$env:PATH"
        & $UsbControlPython -m unittest discover -s tests -p '*test.py'
        if ($LASTEXITCODE -ne 0) { throw "USB touch bridge tests failed: $LASTEXITCODE" }
        & (Join-Path $Root 'scripts\test_usb_bridge_build_source.ps1') | Out-Host
        & (Join-Path $Root 'tests\driver_elevation_bootstrap_test.ps1') | Out-Host
        & (Join-Path $Root 'tests\virtual_camera_elevation_bootstrap_test.ps1') | Out-Host
        & (Join-Path $Root 'tests\updater_elevation_bootstrap_test.ps1') | Out-Host

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
            dotnet build $Project --no-restore --configuration $Configuration
            if ($LASTEXITCODE -ne 0) { throw "Test build failed: $Project ($LASTEXITCODE)" }
            $testAssembly = dotnet msbuild $Project -nologo `
                "-p:Configuration=$Configuration" -getProperty:TargetPath
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($testAssembly) -or
                -not (Test-Path -LiteralPath $testAssembly.Trim() -PathType Leaf)) {
                throw "Failed to resolve test assembly: $Project"
            }
            # dotnet run injects DOTNET_ROOT_X64 even when its parent is clean.
            # Execute the built assembly directly using the resolved SDK host.
            $testExitCode = Invoke-NativeToolWithSanitizedEnvironment `
                (Get-Command dotnet -ErrorAction Stop).Source @($testAssembly.Trim()) `
                -CleanManagedTestEnvironment
            if ($testExitCode -ne 0) { throw "Tests failed: $Project ($testExitCode)" }
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
            dotnet build $Project --no-restore --configuration $Configuration `
                $VersionProperty
            if ($LASTEXITCODE -ne 0) { throw "Application build failed: $Project ($LASTEXITCODE)" }
        }
    }

    if (-not $NoPublish -and (Test-Path 'src/App/iPhoneMirror.App.csproj')) {
        $PublishRoot = if ($TestBuild) { $OutputsRoot } else {
            Join-Path $Root 'outputs\iPhoneMirror'
        }
        Assert-SafeWorkspaceDirectory $PublishRoot
        if (-not $TestBuild -and (Test-Path -LiteralPath $PublishRoot)) {
            Assert-NoReparseChildren $PublishRoot
            Remove-Item -LiteralPath $PublishRoot -Recurse -Force
        }
        dotnet publish src/App/iPhoneMirror.App.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            -p:IncludeBundledFfmpeg=$($UseMediaOutputRuntime.ToString().ToLowerInvariant()) `
            -p:IncludeBundledUxPlay=$($UseUxPlayRuntime.ToString().ToLowerInvariant()) `
            -p:NuGetAudit=false `
            $VersionProperty `
            --output $PublishRoot
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
            'CHANGELOG.md', 'CHANGELOG.zh-TW.md',
            'DRIVER_DEPENDENCIES.md',
            'tools\iUsbBridge.exe',
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
            'Wireless\licenses\NOTICE-FFMPEG-BUILD.txt',
            'Wireless\licenses\SOURCE.md',
            'Wireless\licenses\BUILD.json',
            'Wireless\licenses\SHA256SUMS.txt'
        )
        if ($UseUxPlayRuntime) {
            $requiredArtifacts += @($UxPlayRuntimeFiles | ForEach-Object {
                Join-Path 'Wireless\UxPlay' $_
            })
        }
        $bridgeToolsRoot = Join-Path $PublishRoot 'tools'
        Assert-UsbTouchBridgeRuntime -Directory $bridgeToolsRoot `
            -Label 'Published USB touch bridge runtime'
        $bridgeRuntimeArtifacts = @(Get-UsbTouchBridgeRuntimePayloadFiles `
            -Directory $bridgeToolsRoot -TargetDirectory 'tools')
        $requiredArtifacts += $bridgeRuntimeArtifacts
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
        $uxplayFiles = @()
        $uxplayRoot = Join-Path $PublishRoot 'Wireless\UxPlay'
        if (Test-Path -LiteralPath $uxplayRoot -PathType Container) {
            $uxplayFiles = @(Get-ChildItem -LiteralPath $uxplayRoot -Recurse -File |
                ForEach-Object { $_.FullName.Substring($PublishRoot.Length + 1) })
        }
        if (-not $UseUxPlayRuntime -and $uxplayFiles.Count -ne 0) {
            throw 'UxPlay runtime was published despite -OmitUxPlayRuntime.'
        }
        $optionalPublishedArtifacts = @(
            'Assets\iPhoneMirror.ico',
            'licenses\WPF-UI-LICENSE.md',
            'licenses\WPF-UI-ThirdPartyNotices.txt',
            'AppleMobileDeviceSupport64.msi'
        )
        if ($UseMediaOutputRuntime) {
            $optionalPublishedArtifacts += @($MediaOutputRuntimeFiles | ForEach-Object {
                Join-Path 'tools\ffmpeg' $_
            })
        }
        # The Personalized DDI bundle shipped beside the bridge (0202f06) is
        # published as loose content; allow every file beneath its directory.
        $ddiRoot = Join-Path $PublishRoot 'tools\ddi'
        if (Test-Path -LiteralPath $ddiRoot -PathType Container) {
            $optionalPublishedArtifacts += @(Get-ChildItem -LiteralPath $ddiRoot -Recurse -File |
                ForEach-Object { $_.FullName.Substring($PublishRoot.Length + 1) })
        }
        $allowedPublishedArtifacts = @($requiredArtifacts) + $optionalPublishedArtifacts +
            $uxplayFiles
        $actualPublishedArtifacts = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File |
            ForEach-Object { $_.FullName.Substring($PublishRoot.Length + 1) } |
            Where-Object {
                -not $TestBuild -or
                ($_ -notmatch '^(?:releases|release-staging)\\' -and
                    $_ -notmatch '^iPhoneMirror(?:-Setup)?-v\d+\.\d+\.\d+-(?!test)')
            })
        $unexpectedPublishedArtifacts = @($actualPublishedArtifacts |
            Where-Object { $_ -notin $allowedPublishedArtifacts })
        if ($unexpectedPublishedArtifacts.Count -ne 0) {
            throw "Published output contains unexpected files: $($unexpectedPublishedArtifacts -join ', ')"
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
            $VersionProperty `
            --output $DriverPublishRoot
        if ($LASTEXITCODE -ne 0) { throw "Driver installer publish failed: $LASTEXITCODE" }

        $DriverPublishedExecutables = @(Get-ChildItem -LiteralPath `
            $DriverPublishRoot -Filter 'iPhoneMirror.Driver.exe' -File)
        if ($DriverPublishedExecutables.Count -ne 1) {
            throw 'Driver installer output must contain exactly one iPhoneMirror.Driver.exe file.'
        }
        $expectedDriverTopLevelFiles = @(
            'iPhoneMirror.Driver.exe',
            'THIRD_PARTY_NOTICES.md'
        )
        $unexpectedDriverFiles = @(Get-ChildItem -LiteralPath `
            $DriverPublishRoot -File | Where-Object {
                $_.Name -notin $expectedDriverTopLevelFiles
            })
        $unexpectedDriverDirectories = @(Get-ChildItem -LiteralPath `
            $DriverPublishRoot -Directory | Where-Object {
                $_.Name -notin @('licenses', 'tools')
            })
        $driverLicenseDirectory = Join-Path $DriverPublishRoot 'licenses'
        $expectedDriverLicenseFiles = @(
            'WPF-UI-LICENSE.md',
            'WPF-UI-ThirdPartyNotices.txt'
        )
        $missingDriverFiles = @($expectedDriverTopLevelFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $DriverPublishRoot $_) -PathType Leaf)
        })
        $missingDriverLicenseFiles = @($expectedDriverLicenseFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $driverLicenseDirectory $_) -PathType Leaf)
        })
        $unexpectedDriverLicenseFiles = if (Test-Path -LiteralPath `
                $driverLicenseDirectory -PathType Container) {
            @(Get-ChildItem -LiteralPath $driverLicenseDirectory -File |
                Where-Object { $_.Name -notin $expectedDriverLicenseFiles })
        }
        else { @() }
        if ($missingDriverFiles.Count -ne 0 -or
            $missingDriverLicenseFiles.Count -ne 0 -or
            $unexpectedDriverFiles.Count -ne 0 -or
            $unexpectedDriverDirectories.Count -ne 0 -or
            $unexpectedDriverLicenseFiles.Count -ne 0) {
            throw 'Driver installer output contains an unexpected licensing payload.'
        }

        $MainPublishRoot = if ($TestBuild) { $OutputsRoot } else {
            Join-Path $Root 'outputs\iPhoneMirror'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $MainPublishRoot 'iPhoneMirror.exe'))) {
            throw 'Main application output is missing before driver-manager integration.'
        }
        Copy-Item -LiteralPath $DriverPublishedExecutables[0].FullName `
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
            'CHANGELOG.md', 'CHANGELOG.zh-TW.md',
            'DRIVER_DEPENDENCIES.md'
        )
        if (Test-Path -LiteralPath (Join-Path $MainPublishRoot `
                'AppleMobileDeviceSupport64.msi') -PathType Leaf) {
            $allowedTopLevelFiles += 'AppleMobileDeviceSupport64.msi'
        }
        $unexpectedFiles = @(Get-ChildItem -LiteralPath $MainPublishRoot -File | Where-Object {
            $_.Name -notin $allowedTopLevelFiles -and
            (-not $TestBuild -or
                $_.Name -notmatch '^iPhoneMirror(?:-Setup)?-v\d+\.\d+\.\d+-(?!test)')
        })
        $unexpectedDirectories = @(Get-ChildItem -LiteralPath $MainPublishRoot -Directory |
            Where-Object { $_.Name -notin @('Assets', 'Wireless', 'licenses', 'tools',
                    'releases', 'release-staging') -or
                (-not $TestBuild -and $_.Name -in @('releases', 'release-staging')) })
        if ($unexpectedFiles.Count -ne 0 -or $unexpectedDirectories.Count -ne 0) {
            $unexpected = @($unexpectedFiles.Name) + @($unexpectedDirectories.Name)
            throw "Unexpected files in compact application output: $($unexpected -join ', ')"
        }

        # Only the main application uses the installer runtime directory. The
        # driver stays self-contained so its complete elevation payload can be
        # verified and staged without loading writable sidecars.
        if (-not $TestBuild) {
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
            -p:IncludeBundledUxPlay=$($UseUxPlayRuntime.ToString().ToLowerInvariant()) `
            -p:NuGetAudit=false `
            $VersionProperty `
            --output $InstallerPublishRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Shared-runtime app publish failed: $LASTEXITCODE"
        }
        # Keep an independently verifiable, self-contained elevation payload.
        # Inner compression prevents Inno's solid stream from deduplicating the
        # shared .NET bytes; disable it only for the installer variant.
        $InstallerDriverRoot = Join-Path $Root 'work\publish\iPhoneMirror.Driver.Installer'
        Assert-SafeWorkspaceDirectory $InstallerDriverRoot
        Assert-NoReparseChildren $InstallerDriverRoot
        dotnet publish src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj `
            --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:EnableCompressionInSingleFile=false -p:NuGetAudit=false `
            $VersionProperty --output $InstallerDriverRoot
        if ($LASTEXITCODE -ne 0) { throw "Installer driver publish failed: $LASTEXITCODE" }
        Copy-Item -LiteralPath (Join-Path $InstallerDriverRoot 'iPhoneMirror.Driver.exe') `
            -Destination (Join-Path $InstallerPublishRoot 'iPhoneMirror.Driver.exe') -Force
        if (-not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
            . $AppleSupportPackageTools
            Copy-TrustedAppleSupportPackage $AppleSupportPackagePath `
                $InstallerPublishRoot | Out-Host
        }
        $installerBridgeToolsRoot = Join-Path $InstallerPublishRoot 'tools'
        Assert-UsbTouchBridgeRuntime -Directory $installerBridgeToolsRoot `
            -Label 'Shared-runtime installer USB touch bridge runtime'
        $installerBridgeRuntimeArtifacts = @(Get-UsbTouchBridgeRuntimePayloadFiles `
            -Directory $installerBridgeToolsRoot -TargetDirectory 'tools')
        $installerRequiredArtifacts = @(
            'iPhoneMirror.exe', 'iPhoneMirror.dll', 'iPhoneMirror.deps.json',
            'iPhoneMirror.UsbConfigurationSwitch.exe',
            'iPhoneMirror.runtimeconfig.json', 'iPhoneMirror.Driver.exe',
            'hostfxr.dll',
            'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
            'Microsoft.AspNetCore.Server.Kestrel.Core.dll', 'Microsoft.AspNetCore.Http.dll',
            'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll'
        )
        $installerRequiredArtifacts += $installerBridgeRuntimeArtifacts
        if ($UseMediaOutputRuntime) {
            $installerRequiredArtifacts += @(
                'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE.txt',
                'tools\ffmpeg\README.txt', 'tools\ffmpeg\SOURCE.txt'
            )
        }
        if ($UseUxPlayRuntime) {
            $installerRequiredArtifacts += @($UxPlayRuntimeFiles | ForEach-Object {
                Join-Path 'Wireless\UxPlay' $_
            })
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
    }

    if ($NoPublish) {
        Write-Host 'Build and tests complete (publishing skipped).' -ForegroundColor Green
    }
    elseif ($TestBuild) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TestVersionRecord) |
            Out-Null
        Set-Content -LiteralPath $TestVersionRecord -Value $Version -NoNewline
        Write-Host "Test build complete: $OutputsRoot\iPhoneMirror.exe ($Version)" `
            -ForegroundColor Green
    }
    else {
        . (Join-Path $Root 'scripts\CompactBuildRecord.ps1')
        Write-CompactBuildRecord $Root $BuildUxPlayComponent $UseMediaOutputRuntime
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
