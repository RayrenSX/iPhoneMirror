[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CMake = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$CTest = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe'
$WirelessRoot = Join-Path $Root 'third_party\airplay-server'
$WirelessManifest = Join-Path $WirelessRoot 'SHA256SUMS.txt'

if (-not (Test-Path $CMake)) {
    $CMake = (Get-Command cmake -ErrorAction Stop).Source
}
if (-not (Test-Path $CTest)) {
    $CTest = (Get-Command ctest -ErrorAction Stop).Source
}

Push-Location $Root
try {
    if (-not (Test-Path -LiteralPath $WirelessManifest)) {
        throw 'Wireless receiver hash manifest is missing.'
    }
    $WirelessHashes = foreach ($line in Get-Content -LiteralPath $WirelessManifest) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') {
            throw "Invalid wireless receiver hash entry: $line"
        }
        [PSCustomObject]@{ Hash = $Matches[1].ToLowerInvariant(); Path = $Matches[2] }
    }
    foreach ($entry in $WirelessHashes) {
        $source = Join-Path $WirelessRoot ($entry.Path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Wireless receiver artifact is missing: $($entry.Path)"
        }
        $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Hash) {
            throw "Wireless receiver artifact hash mismatch: $($entry.Path)"
        }
    }

    & $CMake --preset windows-x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed: $LASTEXITCODE" }

    if ($Configuration -eq 'Release') {
        # Some endpoint-security products heuristically block freshly linked,
        # unsigned optimized console test executables. Build only the shipping
        # DLL in Release, then execute the same protocol suite in Debug.
        & $CMake --build --preset windows-x64-release `
            --target iPhoneMirror.Core iPhoneMirror.WirelessHost --parallel
    }
    else {
        & $CMake --build --preset windows-x64-debug --parallel
    }
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }

    if (-not $SkipTests) {
        $TestConfiguration = $Configuration
        if ($Configuration -eq 'Release') {
            & $CMake --build build/native --config Debug `
                --target iPhoneMirror.Core.Tests iPhoneMirror.WirelessHost.Smoke --parallel
            if ($LASTEXITCODE -ne 0) { throw "Debug protocol test build failed: $LASTEXITCODE" }
            $TestConfiguration = 'Debug'
        }
        & $CTest --test-dir build/native -C $TestConfiguration --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw "Native tests failed: $LASTEXITCODE" }

        dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj `
            --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "App logic tests failed: $LASTEXITCODE" }

        dotnet run --project src/DriverInstaller.Tests/iPhoneMirror.DriverInstaller.Tests.csproj `
            --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Driver installer tests failed: $LASTEXITCODE" }
    }

    if (-not $NoPublish -and (Test-Path 'src/App/iPhoneMirror.App.csproj')) {
        $NativeDll = Join-Path $Root "build/native/src/Core/$Configuration/iPhoneMirror.Core.dll"
        $WirelessHost = Join-Path $Root `
            "build/native/src/WirelessHost/$Configuration/iPhoneMirror.WirelessHost.exe"
        $DnsSdShim = Join-Path $Root `
            "build/native/src/WirelessHost/$Configuration/dnssd.dll"
        $AppNative = Join-Path $Root 'src/App/native'
        $AppWireless = Join-Path $AppNative 'Wireless'
        $PublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
        New-Item -ItemType Directory -Force -Path $AppNative | Out-Null
        New-Item -ItemType Directory -Force -Path $AppWireless | Out-Null
        Copy-Item $NativeDll (Join-Path $AppNative 'iPhoneMirror.Core.dll') -Force
        Copy-Item $WirelessHost `
            (Join-Path $AppWireless 'iPhoneMirror.WirelessHost.exe') -Force
        Copy-Item $DnsSdShim (Join-Path $AppWireless 'dnssd.dll') -Force
        Copy-Item (Join-Path $Root 'third_party/libusb/bin/x64/libusb-1.0.dll') `
            (Join-Path $AppNative 'libusb-1.0.dll') -Force
        if (Test-Path -LiteralPath $PublishRoot) {
            Remove-Item -LiteralPath $PublishRoot -Recurse -Force
        }
        dotnet publish src/App/iPhoneMirror.App.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            --output outputs/iPhoneMirror
        if ($LASTEXITCODE -ne 0) { throw "WPF publish failed: $LASTEXITCODE" }

        foreach ($forbidden in @(
            (Join-Path $PublishRoot 'UsbDkHelper.dll'),
            (Join-Path $PublishRoot 'Drivers'),
            (Join-Path $PublishRoot 'install-filter.exe'),
            (Join-Path $PublishRoot 'libusb0.sys')
        )) {
            if (Test-Path -LiteralPath $forbidden) {
                Remove-Item -LiteralPath $forbidden -Force
            }
        }

        $requiredArtifacts = @(
            'iPhoneMirror.exe',
            'iPhoneMirror.Core.dll',
            'README.md',
            'README.en.md',
            'LICENSE',
            'CHANGELOG.md',
            'THIRD_PARTY_NOTICES.md',
            'Wireless\iPhoneMirror.WirelessHost.exe',
            'Wireless\airplay2dll.dll',
            'Wireless\avcodec-58.dll',
            'Wireless\avutil-56.dll',
            'Wireless\dnssd.dll',
            'Wireless\swresample-3.dll',
            'Wireless\swscale-5.dll',
            'Wireless\licenses\SOURCE.md',
            'Wireless\licenses\SHA256SUMS.txt'
        )
        foreach ($relative in $requiredArtifacts) {
            if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $relative))) {
                throw "Published artifact is missing: $relative"
            }
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
        $DriverPublishRoot = Join-Path $Root 'outputs\iPhoneMirror.Driver'
        if (Test-Path -LiteralPath $DriverPublishRoot) {
            Remove-Item -LiteralPath $DriverPublishRoot -Recurse -Force
        }
        dotnet publish src/DriverInstaller/iPhoneMirror.DriverInstaller.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            --output outputs/iPhoneMirror.Driver
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
    }

    Write-Host "Build complete: $Root\outputs\iPhoneMirror" -ForegroundColor Green
    Write-Host "Driver tool: $Root\outputs\iPhoneMirror.Driver\iPhoneMirror.Driver.exe" `
        -ForegroundColor Green
}
finally {
    Pop-Location
}
