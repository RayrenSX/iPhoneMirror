[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$Python = 'python',
    [string]$EnvironmentPath,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $EnvironmentPath) { $EnvironmentPath = Join-Path $repo 'work/usb-touch-bridge-python' }
Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'build_coredevice_decoder.ps1') -MsysRoot $MsysRoot -Python $Python
    . (Join-Path $PSScriptRoot 'UsbBridgeBuildSource.ps1')
    $stageRoot = Join-Path $repo 'work/usb-bridge-build'
    $stage = New-UsbBridgeBuildSource -RecipeRoot (Join-Path $PSScriptRoot 'usb-bridge-recipe') -SourceRoot (Join-Path $repo 'tools') -WorkRoot $stageRoot
    try {
        & (Join-Path $stage 'build.ps1') -BridgeOnly -BridgeOutputPath (Join-Path $repo 'dist/iUsbBridge.exe') -EnvironmentPath $EnvironmentPath -Python $Python
        if ($LASTEXITCODE) { throw 'Device Hub bridge build failed' }
    } finally { Remove-UsbBridgeBuildSource -Stage $stage -WorkRoot $stageRoot }
    cmake --preset windows-x64
    if ($LASTEXITCODE) { throw 'Native configuration failed' }
    cmake --build build/native --config Release --target iPhoneMirror.Core iPhoneMirror.Core.Tests --parallel 6
    if ($LASTEXITCODE) { throw 'Native build failed' }
    $native = Join-Path $repo 'src/App/native'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach ($name in @('iPhoneMirror.Core.dll', 'libusb-1.0.dll')) {
        Copy-Item -LiteralPath (Join-Path $repo "build/native/src/Core/Release/$name") -Destination $native -Force
    }
    Copy-Item -LiteralPath (Join-Path $repo 'src/DriverInstaller/Assets/libusb-win32-1.2.6.0/amd64/libusb0.dll') -Destination $native -Force
    dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release
    if ($LASTEXITCODE) { throw 'Managed build failed' }
    $sdkPython = Join-Path $EnvironmentPath 'Scripts/python.exe'
    & $sdkPython -m pip install -e 'sdk/python[mcp]'
    if ($LASTEXITCODE) { throw 'Python SDK/MCP installation failed' }
    if (-not $SkipTests) {
        ctest --test-dir build/native -C Release -R '^CoreProtocolTests$' --output-on-failure
        if ($LASTEXITCODE) { throw 'Native tests failed' }
        foreach ($pattern in @('coredevice_media_test.py', 'device_hub_test.py', 'device_management_test.py', 'python_sdk_test.py', 'usb*_test.py')) {
            & $sdkPython -m unittest discover -s tests -p $pattern
            if ($LASTEXITCODE) { throw "Python tests failed: $pattern" }
        }
        $runtime = Join-Path $repo 'src/App.Runtime.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/IPhoneMirror.App.Runtime.Tests.exe'
        & $runtime --automation-api
        if ($LASTEXITCODE) { throw 'API integration tests failed' }
        & $sdkPython tests/coredevice_media_test.py --write-fixture work/replay-test.imrtp
        if ($LASTEXITCODE) { throw 'Replay fixture generation failed' }
        & $runtime --coredevice-replay (Join-Path $repo 'work/replay-test.imrtp')
        if ($LASTEXITCODE) { throw 'Packaged replay test failed' }
    }
    Write-Host 'Dev components built and verified. No running program or delivery directory was replaced.'
} finally { Pop-Location }
