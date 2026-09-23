[CmdletBinding()]
param(
    [string]$Python = "python",
    [string]$Configuration = "Release",
    [switch]$BridgeOnly,
    [string]$BridgeOutputPath,
    [string]$EnvironmentPath
)

$ErrorActionPreference = "Stop"
$Root = [IO.Path]::GetFullPath($PSScriptRoot)
$WorkspaceRoot = Split-Path -Parent $Root
$Out = Join-Path $Root "dist\iUsbBridge-Demo"
$BridgeDirectory = Join-Path $Root "dist\iUsbBridge"
$Bridge = Join-Path $BridgeDirectory "iUsbBridge.exe"
$Spec = Join-Path $Root "iUsbBridge.spec"
$Source = Join-Path $Root "src\usb_touch_bridge.py"
$Requirements = Join-Path $Root "requirements.txt"
if ([string]::IsNullOrWhiteSpace($EnvironmentPath)) {
    $EnvironmentPath = Join-Path $WorkspaceRoot "work\usb-touch-bridge-python"
}
$EnvironmentPath = [IO.Path]::GetFullPath($EnvironmentPath)
$EnvironmentPython = Join-Path $EnvironmentPath "Scripts\python.exe"

foreach ($required in @($Spec, $Source, $Requirements)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "USB control build input is missing: $required"
    }
}

function Write-BridgeRuntimeManifest([string]$Directory) {
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $manifestPath = Join-Path $fullDirectory 'iUsbBridge.runtime.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
    $files = @(Get-ChildItem -LiteralPath $fullDirectory -Recurse -File |
        Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($fullDirectory.Length + 1) -replace '\\', '/'
            [PSCustomObject][ordered]@{
                path = $relative
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    if ($files.Count -eq 0 -or
        @($files | Where-Object { $_.path -eq 'iUsbBridge.exe' }).Count -ne 1 -or
        @($files | Where-Object { $_.path -like '_internal/*' }).Count -eq 0) {
        throw 'PyInstaller onedir output does not contain the bridge and its runtime directory.'
    }
    $manifest = [PSCustomObject][ordered]@{
        schema = 1
        files = $files
    }
    [IO.File]::WriteAllText($manifestPath,
        ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
}

function Assert-NoReparseChildren([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $directory = Get-Item -LiteralPath $Path -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing recursive mutation through a reparse point: $($directory.FullName)"
    }
    $reparse = @(Get-ChildItem -LiteralPath $Path -Recurse -Force |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
    if ($reparse.Count -ne 0) {
        throw "Refusing recursive mutation through a reparse point: $($reparse[0].FullName)"
    }
}

$bootstrapPython = @(Get-Command $Python -CommandType Application -ErrorAction Stop |
    Select-Object -First 1)[0]
if (-not (Test-Path -LiteralPath $EnvironmentPython -PathType Leaf)) {
    & $bootstrapPython.Source -m venv $EnvironmentPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create USB touch bridge Python environment: $LASTEXITCODE"
    }
}
if (-not (Test-Path -LiteralPath $EnvironmentPython -PathType Leaf)) {
    throw "USB touch bridge Python environment is incomplete: $EnvironmentPython"
}

& $EnvironmentPython -m pip install --disable-pip-version-check --requirement $Requirements
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install USB touch bridge build requirements: $LASTEXITCODE"
}

Push-Location $Root
try {
    & $EnvironmentPython -m PyInstaller --noconfirm --clean $Spec
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed: $LASTEXITCODE" }

    if (-not (Test-Path -LiteralPath $Bridge -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $BridgeDirectory '_internal') -PathType Container)) {
        throw "PyInstaller did not produce the USB touch bridge: $Bridge"
    }
    Write-BridgeRuntimeManifest $BridgeDirectory

    if (-not [string]::IsNullOrWhiteSpace($BridgeOutputPath)) {
        $BridgeOutputPath = [IO.Path]::GetFullPath($BridgeOutputPath)
        $destinationDirectory = Split-Path -Parent $BridgeOutputPath
        $destinationRuntimeDirectory = Join-Path $destinationDirectory '_internal'
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        if (Test-Path -LiteralPath $destinationRuntimeDirectory) {
            Assert-NoReparseChildren $destinationRuntimeDirectory
            Remove-Item -LiteralPath $destinationRuntimeDirectory -Recurse -Force
        }
        Copy-Item -LiteralPath $Bridge -Destination $BridgeOutputPath -Force
        Copy-Item -LiteralPath (Join-Path $BridgeDirectory '_internal') `
            -Destination $destinationRuntimeDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $BridgeDirectory 'iUsbBridge.runtime.json') `
            -Destination (Join-Path $destinationDirectory 'iUsbBridge.runtime.json') -Force
    }

    if ($BridgeOnly) {
        Write-Host "USB touch bridge: $Bridge"
        return
    }

    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    Copy-Item -LiteralPath (Join-Path $BridgeDirectory '*') -Destination $Out -Recurse -Force

    Push-Location (Join-Path $Root "demo")
    try {
        dotnet publish -c $Configuration -r win-x64 --self-contained true -o $Out
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }

    Write-Host "USB control package: $Out"
}
finally { Pop-Location }
