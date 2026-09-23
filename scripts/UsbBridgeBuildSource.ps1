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
    foreach ($name in @('build.ps1', 'iUsbBridge.spec', 'requirements.txt')) {
        $inputPath = Join-Path $RecipeRoot $name
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
            throw "USB bridge build recipe is missing: $inputPath"
        }
    }
    $bridge = Join-Path $SourceRoot 'usb_touch_bridge.py'
    $package = Join-Path $SourceRoot 'iostouch'
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf) -or
        -not (Test-Path -LiteralPath $package -PathType Container)) {
        throw "Audited USB bridge source is incomplete: $SourceRoot"
    }
    $stageSource = Join-Path $stage 'src'
    New-Item -ItemType Directory -Path $stageSource -Force | Out-Null
    foreach ($name in @('build.ps1', 'iUsbBridge.spec', 'requirements.txt')) {
        Copy-Item -LiteralPath (Join-Path $RecipeRoot $name) -Destination $stage
    }
    Copy-Item -LiteralPath $bridge -Destination $stageSource
    # Newer bridge releases include a Rust usbmux helper built by the same
    # recipe. Copy the manifest, lockfile, build script, and Rust sources when
    # present so the staged build remains compatible across bridge versions.
    foreach ($name in @('Cargo.toml', 'Cargo.lock', 'build.rs')) {
        $inputPath = Join-Path $RecipeRoot $name
        if (Test-Path -LiteralPath $inputPath -PathType Leaf) {
            Copy-Item -LiteralPath $inputPath -Destination $stage
        }
    }
    $rustSource = Join-Path $RecipeRoot 'src'
    if (Test-Path -LiteralPath $rustSource -PathType Container) {
        Get-ChildItem -LiteralPath $rustSource -Recurse -File -Filter '*.rs' | ForEach-Object {
            $relative = $_.FullName.Substring([IO.Path]::GetFullPath($RecipeRoot).TrimEnd('\\').Length + 1)
            $destination = Join-Path $stage $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
    $patches = Join-Path $RecipeRoot 'patches'
    if (Test-Path -LiteralPath $patches -PathType Container) {
        Get-ChildItem -LiteralPath $patches -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring([IO.Path]::GetFullPath($RecipeRoot).TrimEnd('\\').Length + 1)
            $destination = Join-Path $stage $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
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
