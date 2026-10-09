function New-UsbBridgeBuildSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RecipeRoot,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$WorkRoot
    )

    # Reuse the upstream packaging recipe/dependencies without silently using
    # its different source checkout or editing that user's working tree.
    $stage = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ([Guid]::NewGuid().ToString('N'))
    $recipeFiles = @('build.ps1', 'iUsbBridge.spec', 'requirements.txt', 'hooks\hook-usb.py')
    foreach ($name in $recipeFiles) {
        $inputPath = Join-Path $RecipeRoot $name
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
            throw "USB bridge build recipe is missing: $inputPath"
        }
    }
    $bridge = Join-Path $SourceRoot 'usb_touch_bridge.py'
    $runtimeCheck = Join-Path $SourceRoot 'bridge_runtime_check.py'
    $ddiSupport = Join-Path $SourceRoot 'ddi_support.py'
    $mdnsDiscovery = Join-Path $SourceRoot 'mdns_discovery.py'
    $package = Join-Path $SourceRoot 'iostouch'
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf) -or
        -not (Test-Path -LiteralPath $runtimeCheck -PathType Leaf) -or
        -not (Test-Path -LiteralPath $ddiSupport -PathType Leaf) -or
        -not (Test-Path -LiteralPath $mdnsDiscovery -PathType Leaf) -or
        -not (Test-Path -LiteralPath $package -PathType Container)) {
        throw "Audited USB bridge source is incomplete: $SourceRoot"
    }
    # PyInstaller's USB runtime hook searches sys._MEIPASS, not the app root
    # or PATH. Stage the same pinned x64 DLLs used by the native application.
    $repositoryRoot = Split-Path -Parent ([IO.Path]::GetFullPath($SourceRoot).TrimEnd('\'))
    $usbLibraries = @(
        @{
            path = 'src\DriverInstaller\Assets\libusb-win32-1.2.6.0\amd64\libusb0.dll'
            sha256 = '4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14'
        },
        @{
            path = 'third_party\libusb\bin\x64\libusb-1.0.dll'
            sha256 = '5072054CB3002AE071F382AD5C2C2B0092D9451C537D0C13444C2B6F968F7251'
        }
    )
    foreach ($library in $usbLibraries) {
        $inputPath = Join-Path $repositoryRoot $library.path
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
            throw "USB bridge native runtime is missing: $inputPath"
        }
        if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $library.sha256) {
            throw "USB bridge native runtime hash mismatch: $inputPath"
        }
    }
    $stageSource = Join-Path $stage 'src'
    New-Item -ItemType Directory -Path $stageSource -Force | Out-Null
    $stageNative = Join-Path $stage 'native'
    New-Item -ItemType Directory -Path $stageNative -Force | Out-Null
    foreach ($library in $usbLibraries) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $library.path) -Destination $stageNative
    }
    foreach ($name in $recipeFiles) {
        $destination = Join-Path $stage $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $RecipeRoot $name) -Destination $destination
    }
    Copy-Item -LiteralPath $bridge -Destination $stageSource
    Copy-Item -LiteralPath $runtimeCheck -Destination $stageSource
    Copy-Item -LiteralPath $ddiSupport -Destination $stageSource
    Copy-Item -LiteralPath $mdnsDiscovery -Destination $stageSource
    # Copy source only: stale bytecode from either checkout must not ship.
    Get-ChildItem -LiteralPath $package -Recurse -File -Filter '*.py' | ForEach-Object {
        $relative = $_.FullName.Substring([IO.Path]::GetFullPath($SourceRoot).TrimEnd('\').Length + 1)
        $destination = Join-Path $stageSource $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    return $stage
}

function Remove-UsbBridgeBuildSource {
    param([Parameter(Mandatory)][string]$Stage, [Parameter(Mandatory)][string]$WorkRoot)
    $rootPath = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
    $stagePath = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    if ((Split-Path -Parent $stagePath) -ine $rootPath -or
        (Split-Path -Leaf $stagePath) -notmatch '^[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected USB bridge build directory: $stagePath"
    }
    if (-not (Test-Path -LiteralPath $stagePath)) { return }
    # Check the resolved deletion boundary and refuse links anywhere inside it.
    $current = Get-Item -LiteralPath $stagePath -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing USB bridge cleanup through a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }
    $links = @(Get-ChildItem -LiteralPath $stagePath -Recurse -Force | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint
    })
    if ($links.Count -gt 0) { throw "Refusing USB bridge cleanup containing reparse points: $stagePath" }
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
