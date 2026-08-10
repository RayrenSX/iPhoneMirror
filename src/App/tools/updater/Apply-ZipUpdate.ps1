[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+(;\d+)*$')][string]$WaitPids,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$RestartExecutable
)

$ErrorActionPreference = 'Stop'
$stagingRoot = Join-Path $env:TEMP ('iPhoneMirror\Update-' + [Guid]::NewGuid().ToString('N'))
try {
    $installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $trackedIds = @($WaitPids -split ';' | ForEach-Object {
        [int]::Parse($_, [Globalization.CultureInfo]::InvariantCulture)
    } | Where-Object { $_ -gt 0 } | Select-Object -Unique)

    $ownedProcessIds = @()
    foreach ($trackedId in $trackedIds) {
        $process = Get-Process -Id $trackedId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try { $processPath = $process.MainModule.FileName } catch { $processPath = $null }
        $owned = $false
        if ($processPath) {
            try {
                $fullProcessPath = [IO.Path]::GetFullPath($processPath)
                $owned = $fullProcessPath.StartsWith($installRoot + '\',
                    [StringComparison]::OrdinalIgnoreCase) -and
                    ([IO.Path]::GetFileName($fullProcessPath) -in @(
                        'iPhoneMirror.exe', 'iPhoneMirror.Driver.exe'))
            }
            catch { $owned = $false }
        }
        if ($owned) {
            $ownedProcessIds += $trackedId
            try { [void]$process.CloseMainWindow() } catch { }
        }
        $process.Dispose()
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ($trackedIds.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        $trackedIds = @($trackedIds | Where-Object {
            Get-Process -Id $_ -ErrorAction SilentlyContinue
        })
        if ($trackedIds.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }

    # A standalone driver manager is not a child of the main app. If it did
    # not honor the close request, terminate it only after validating that its
    # executable is inside the target installation directory.
    if ($trackedIds.Count -ne 0) {
        foreach ($trackedId in $ownedProcessIds) {
            $process = Get-Process -Id $trackedId -ErrorAction SilentlyContinue
            if ($null -eq $process) { continue }
            try { $processPath = $process.MainModule.FileName } catch { $processPath = $null }
            $owned = $false
            if ($processPath) {
                try {
                    $fullProcessPath = [IO.Path]::GetFullPath($processPath)
                    $owned = $fullProcessPath.StartsWith($installRoot + '\',
                        [StringComparison]::OrdinalIgnoreCase) -and
                        ([IO.Path]::GetFileName($fullProcessPath) -in @(
                            'iPhoneMirror.exe', 'iPhoneMirror.Driver.exe'))
                }
                catch { $owned = $false }
            }
            if ($owned) {
                Stop-Process -Id $trackedId -Force -ErrorAction SilentlyContinue
            }
        }
        Start-Sleep -Milliseconds 500
        $remaining = @($trackedIds | Where-Object {
            Get-Process -Id $_ -ErrorAction SilentlyContinue
        })
        if ($remaining.Count -ne 0) {
            throw 'iPhoneMirror or its driver manager did not exit before the update timeout.'
        }
    }
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $stagingRoot -Force
    $children = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
    $payloadRoot = if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $children[0].FullName
    }
    else { $stagingRoot }
    foreach ($required in @('iPhoneMirror.exe', 'iPhoneMirror.Driver.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $required) -PathType Leaf)) {
            throw "The update ZIP does not contain $required."
        }
    }
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    Get-ChildItem -LiteralPath $payloadRoot -Force |
        Copy-Item -Destination $InstallDirectory -Recurse -Force
    # ZIP updates are overlays. Remove files that a newer payload intentionally
    # dropped, including the optional media-output runtime.
    foreach ($relative in @(
            'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE.txt',
            'tools\ffmpeg\README.txt', 'tools\ffmpeg\SOURCE.txt')) {
        $payloadFile = Join-Path $payloadRoot $relative
        $installedFile = Join-Path $InstallDirectory $relative
        if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf) -and
            (Test-Path -LiteralPath $installedFile -PathType Leaf)) {
            Remove-Item -LiteralPath $installedFile -Force
        }
    }
    Start-Process -FilePath $RestartExecutable -WorkingDirectory $InstallDirectory
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
