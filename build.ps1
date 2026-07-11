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

if (-not (Test-Path $CMake)) {
    $CMake = (Get-Command cmake -ErrorAction Stop).Source
}
if (-not (Test-Path $CTest)) {
    $CTest = (Get-Command ctest -ErrorAction Stop).Source
}

Push-Location $Root
try {
    & $CMake --preset windows-x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed: $LASTEXITCODE" }

    if ($Configuration -eq 'Release') {
        # Some endpoint-security products heuristically block freshly linked,
        # unsigned optimized console test executables. Build only the shipping
        # DLL in Release, then execute the same protocol suite in Debug.
        & $CMake --build --preset windows-x64-release --target iPhoneMirror.Core --parallel
    }
    else {
        & $CMake --build --preset windows-x64-debug --parallel
    }
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }

    if (-not $SkipTests) {
        $TestConfiguration = $Configuration
        if ($Configuration -eq 'Release') {
            & $CMake --build build/native --config Debug --target iPhoneMirror.Core.Tests --parallel
            if ($LASTEXITCODE -ne 0) { throw "Debug protocol test build failed: $LASTEXITCODE" }
            $TestConfiguration = 'Debug'
        }
        & $CTest --test-dir build/native -C $TestConfiguration --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw "Native tests failed: $LASTEXITCODE" }
    }

    if (-not $NoPublish -and (Test-Path 'src/App/iPhoneMirror.App.csproj')) {
        $NativeDll = Join-Path $Root "build/native/src/Core/$Configuration/iPhoneMirror.Core.dll"
        $AppNative = Join-Path $Root 'src/App/native'
        New-Item -ItemType Directory -Force -Path $AppNative | Out-Null
        Copy-Item $NativeDll (Join-Path $AppNative 'iPhoneMirror.Core.dll') -Force
        Copy-Item (Join-Path $Root 'third_party/libusb/bin/x64/libusb-1.0.dll') `
            (Join-Path $AppNative 'libusb-1.0.dll') -Force
        dotnet publish src/App/iPhoneMirror.App.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            --output outputs/iPhoneMirror
        if ($LASTEXITCODE -ne 0) { throw "WPF publish failed: $LASTEXITCODE" }

        $PublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
        foreach ($forbidden in @(
            (Join-Path $PublishRoot 'Drivers\InstallIPhoneFilter.ps1'),
            (Join-Path $PublishRoot 'UsbDkHelper.dll')
        )) {
            if (Test-Path -LiteralPath $forbidden) {
                Remove-Item -LiteralPath $forbidden -Force
            }
        }

        $requiredArtifacts = @(
            'iPhoneMirror.exe',
            'iPhoneMirror.Core.dll',
            'README.md',
            'Drivers\THIRD-PARTY-NOTICES.txt',
            'Drivers\libusb-win32-1.2.6.0\COPYING_GPL.txt',
            'Drivers\libusb-win32-1.2.6.0\COPYING_LGPL.txt',
            'Drivers\libusb-win32-1.2.6.0\libusb-win32-src-1.2.6.0.zip',
            'Drivers\libusb-win32-1.2.6.0\amd64\install-filter.exe',
            'Drivers\libusb-win32-1.2.6.0\amd64\libusb0.sys',
            'Drivers\libusb-win32-1.2.6.0\amd64\libusb0.dll',
            'Drivers\libusb-win32-1.2.6.0\x86\libusb0_x86.dll'
        )
        foreach ($relative in $requiredArtifacts) {
            if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $relative))) {
                throw "Published artifact is missing: $relative"
            }
        }

        $expectedDriverHashes = @{
            'Drivers\libusb-win32-1.2.6.0\amd64\install-filter.exe' =
                'DF2ABF387893332F28C4DF68B10A6B176DC9706142055DCCCCF447F5A9CEDE2D'
            'Drivers\libusb-win32-1.2.6.0\amd64\libusb0.sys' =
                '8058F2AFE6EF96A7D2DED432997FD8655970C9EA75A938EE4557D6A2CB4CC989'
            'Drivers\libusb-win32-1.2.6.0\amd64\libusb0.dll' =
                '4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14'
            'Drivers\libusb-win32-1.2.6.0\x86\libusb0_x86.dll' =
                '00CACA07869B19D10B370552AC7CC2F6F2EE246FC15DB11650F6CD3F4EF9B666'
        }
        foreach ($entry in $expectedDriverHashes.GetEnumerator()) {
            $actual = (Get-FileHash -LiteralPath (Join-Path $PublishRoot $entry.Key) `
                -Algorithm SHA256).Hash
            if ($actual -ne $entry.Value) {
                throw "Published driver hash mismatch: $($entry.Key)"
            }
        }
    }

    Write-Host "Build complete: $Root\outputs\iPhoneMirror" -ForegroundColor Green
}
finally {
    Pop-Location
}
