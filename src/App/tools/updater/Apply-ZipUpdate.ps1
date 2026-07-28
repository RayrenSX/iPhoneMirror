[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WaitPid,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$RestartExecutable
)

$ErrorActionPreference = 'Stop'
$stagingRoot = Join-Path $env:TEMP ('iPhoneMirror\Update-' + [Guid]::NewGuid().ToString('N'))
try {
    Wait-Process -Id $WaitPid -Timeout 120 -ErrorAction SilentlyContinue
    if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) {
        throw 'iPhoneMirror did not exit before the update timeout.'
    }
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $stagingRoot -Force
    $children = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
    $payloadRoot = if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $children[0].FullName
    }
    else { $stagingRoot }
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot 'iPhoneMirror.exe') -PathType Leaf)) {
        throw 'The update ZIP does not contain iPhoneMirror.exe.'
    }
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    Get-ChildItem -LiteralPath $payloadRoot -Force |
        Copy-Item -Destination $InstallDirectory -Recurse -Force
    Start-Process -FilePath $RestartExecutable -WorkingDirectory $InstallDirectory
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
