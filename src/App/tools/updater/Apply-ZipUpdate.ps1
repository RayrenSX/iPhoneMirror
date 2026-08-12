[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+(;\d+)*$')][string]$WaitPids,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$RestartExecutable
)

$ErrorActionPreference = 'Stop'
$stagingRoot = Join-Path $env:TEMP ('iPhoneMirror\Update-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $env:TEMP ('iPhoneMirror\Rollback-' + [Guid]::NewGuid().ToString('N'))
$preserveBackup = $false

function Get-NormalizedPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Equals($pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }
    return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Get-ChildPathPrefix([string]$Root) {
    $rootPath = Get-NormalizedPath $Root
    if ($rootPath.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(),
            [StringComparison]::Ordinal)) {
        return $rootPath
    }
    return $rootPath + [IO.Path]::DirectorySeparatorChar
}

function Get-RelativeUpdatePath([string]$Root, [string]$Path) {
    $prefix = Get-ChildPathPrefix $Root
    $fullPath = Get-NormalizedPath $Path
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Update source escapes its payload directory: $fullPath"
    }
    return $fullPath.Substring($prefix.Length)
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '')
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-NoReparsePath([string]$Root, [string]$Path) {
    $rootPath = Get-NormalizedPath $Root
    $currentPath = Get-NormalizedPath $Path
    $rootPrefix = Get-ChildPathPrefix $rootPath
    if ($currentPath -ne $rootPath -and -not $currentPath.StartsWith(
            $rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Update target escapes the installation directory: $currentPath"
    }
    while ($currentPath.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Update target path contains a reparse point: $currentPath"
            }
        }
        if ($currentPath -eq $rootPath) { break }
        $currentPath = Split-Path -Parent $currentPath
    }
}

try {
    $installRoot = Get-NormalizedPath $InstallDirectory
    $installPrefix = Get-ChildPathPrefix $installRoot
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
                $owned = $fullProcessPath.StartsWith($installPrefix,
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
                    $owned = $fullProcessPath.StartsWith($installPrefix,
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

    $payloadItems = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force)
    $reparseItem = $payloadItems | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | Select-Object -First 1
    if ($null -ne $reparseItem) {
        throw "The update ZIP contains a reparse point: $($reparseItem.Name)"
    }

    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    Assert-NoReparsePath $installRoot $installRoot

    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $changes = [Collections.Generic.List[object]]::new()
    $createdDirectories = [Collections.Generic.List[string]]::new()
    try {
        foreach ($directory in @($payloadItems | Where-Object { $_.PSIsContainer } |
                     Sort-Object { $_.FullName.Length })) {
            $relative = Get-RelativeUpdatePath $payloadRoot $directory.FullName
            $destination = Join-Path $installRoot $relative
            Assert-NoReparsePath $installRoot (Split-Path -Parent $destination)
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                throw "An update directory conflicts with an installed file: $relative"
            }
            if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
                New-Item -ItemType Directory -Path $destination | Out-Null
                $createdDirectories.Add($destination)
            }
        }

        $index = 0
        foreach ($source in @($payloadItems | Where-Object { -not $_.PSIsContainer })) {
            $relative = Get-RelativeUpdatePath $payloadRoot $source.FullName
            $destination = Join-Path $installRoot $relative
            $parent = Split-Path -Parent $destination
            Assert-NoReparsePath $installRoot $parent
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                New-Item -ItemType Directory -Path $parent | Out-Null
                $createdDirectories.Add($parent)
            }
            if (Test-Path -LiteralPath $destination -PathType Container) {
                throw "An update file conflicts with an installed directory: $relative"
            }
            $hadOriginal = Test-Path -LiteralPath $destination -PathType Leaf
            $backup = Join-Path $backupRoot "$index.bak"
            if ($hadOriginal) {
                $installedItem = Get-Item -LiteralPath $destination -Force
                if (($installedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "An installed update target is a reparse point: $relative"
                }
                Copy-Item -LiteralPath $destination -Destination $backup
            }
            $changes.Add([PSCustomObject]@{
                Final = $destination; Backup = $backup; HadOriginal = $hadOriginal
            })
            Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
            if ((Get-Sha256 $source.FullName) -ne (Get-Sha256 $destination)) {
                throw "Update copy verification failed: $relative"
            }
            ++$index
        }

        # ZIP updates are overlays. Remove files intentionally dropped by the payload.
        foreach ($relative in @(
                'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE.txt',
                'tools\ffmpeg\README.txt', 'tools\ffmpeg\SOURCE.txt')) {
            $payloadFile = Join-Path $payloadRoot $relative
            $installedFile = Join-Path $installRoot $relative
            Assert-NoReparsePath $installRoot (Split-Path -Parent $installedFile)
            if ((Test-Path -LiteralPath $payloadFile -PathType Leaf) -or
                -not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { continue }
            $installedItem = Get-Item -LiteralPath $installedFile -Force
            if (($installedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "An obsolete update target is a reparse point: $relative"
            }
            $backup = Join-Path $backupRoot "$index.bak"
            Copy-Item -LiteralPath $installedFile -Destination $backup
            $changes.Add([PSCustomObject]@{
                Final = $installedFile; Backup = $backup; HadOriginal = $true
            })
            Remove-Item -LiteralPath $installedFile -Force
            ++$index
        }
    }
    catch {
        $updateError = $_
        $rollbackErrors = @()
        for ($index = $changes.Count - 1; $index -ge 0; --$index) {
            $change = $changes[$index]
            try {
                if ($change.HadOriginal) {
                    if (-not (Test-Path -LiteralPath $change.Backup -PathType Leaf)) {
                        throw "Rollback backup is missing: $($change.Backup)"
                    }
                    Copy-Item -LiteralPath $change.Backup -Destination $change.Final -Force
                }
                elseif (Test-Path -LiteralPath $change.Final -PathType Leaf) {
                    Remove-Item -LiteralPath $change.Final -Force
                }
            }
            catch { $rollbackErrors += $_.Exception.Message }
        }
        foreach ($directory in @($createdDirectories | Sort-Object Length -Descending)) {
            try {
                if ((Test-Path -LiteralPath $directory -PathType Container) -and
                    -not (Get-ChildItem -LiteralPath $directory -Force)) {
                    Remove-Item -LiteralPath $directory -Force
                }
            }
            catch { $rollbackErrors += $_.Exception.Message }
        }
        if ($rollbackErrors.Count -ne 0) {
            $preserveBackup = $true
            throw "Update failed: $($updateError.Exception.Message) Rollback was incomplete: " +
                "$($rollbackErrors -join '; '). Recovery files remain in $backupRoot."
        }
        throw $updateError
    }
    Start-Process -FilePath $RestartExecutable -WorkingDirectory $InstallDirectory
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $preserveBackup -and (Test-Path -LiteralPath $backupRoot)) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
