[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+(;\d+)*$')][string]$WaitPids,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$RestartExecutable,
    [switch]$SkipRestart
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
$isElevated = $currentPrincipal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$privilegedTempRoot = if ($isElevated) {
    [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
}
else { $env:TEMP }
$stagingRoot = Join-Path $privilegedTempRoot (
    'iPhoneMirror-Update-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $privilegedTempRoot (
    'iPhoneMirror-Rollback-' + [Guid]::NewGuid().ToString('N'))
$preserveBackup = $false
$zipLock = $null
$maximumArchiveBytes = 2L * 1024 * 1024 * 1024
$maximumEntryCount = 20000
$maximumEntryBytes = 2L * 1024 * 1024 * 1024
$maximumExpandedBytes = 4L * 1024 * 1024 * 1024
$maximumCompressionRatio = 1000L
$minimumFreeSpaceReserve = 512L * 1024 * 1024

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

function Get-StreamSha256([IO.Stream]$Stream) {
    $originalPosition = $Stream.Position
    try {
        $Stream.Position = 0
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '')
        }
        finally { $algorithm.Dispose() }
    }
    finally { $Stream.Position = $originalPosition }
}

function New-PrivilegedDirectory([string]$Path) {
    if (-not $isElevated) {
        New-Item -ItemType Directory -Path $Path | Out-Null
        return
    }
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $rights = [Security.AccessControl.FileSystemRights]::FullControl
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administrators, $rights, $inheritance, $propagation, $allow))
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $system, $rights, $inheritance, $propagation, $allow))
    [IO.DirectoryInfo]::new($Path).Create($security)
}

function Enable-DirectoryInheritance([string]$Path) {
    if (-not $isElevated) { return }
    $directory = [IO.DirectoryInfo]::new($Path)
    $security = $directory.GetAccessControl(
        [Security.AccessControl.AccessControlSections]::Access)
    $security.SetAccessRuleProtection($false, $false)
    $directory.SetAccessControl($security)
}

function Start-RestartProcess([string]$Path, [string]$WorkingDirectory) {
    if (-not $isElevated) {
        Start-Process -FilePath $Path -WorkingDirectory $WorkingDirectory
        return
    }

    # Delegate process creation to the interactive desktop shell. Shell.Application
    # is hosted by Explorer at the user's normal integrity level, so the updated
    # GUI does not inherit this helper's administrator token.
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $desktopShell = Get-Process -Name explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId } | Select-Object -First 1
    if ($null -eq $desktopShell) {
        throw 'The updated application could not be restarted at normal user privileges.'
    }
    $shell = New-Object -ComObject Shell.Application
    try { $shell.ShellExecute($Path, '', $WorkingDirectory, 'open', 1) }
    finally {
        if ($null -ne $shell) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
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

function Get-SafeZipTarget([string]$EntryName, [string]$Destination) {
    if ([string]::IsNullOrEmpty($EntryName)) {
        throw 'The update ZIP contains an empty entry name.'
    }
    $entryPath = $EntryName.Replace(
        [IO.Path]::AltDirectorySeparatorChar,
        [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($entryPath) -or $entryPath.Contains(':')) {
        throw "The update ZIP contains an unsafe path: $EntryName"
    }
    $destinationRoot = Get-NormalizedPath $Destination
    $destinationPrefix = Get-ChildPathPrefix $destinationRoot
    $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $entryPath))
    if (-not $target.StartsWith($destinationPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The update ZIP entry escapes the staging directory: $EntryName"
    }
    return $target
}

function Assert-SafeZipArchive([IO.Stream]$Stream, [string]$Destination) {
    if ($Stream.Length -gt $maximumArchiveBytes) {
        throw "The update ZIP exceeds the $maximumArchiveBytes-byte download limit."
    }

    $Stream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($Stream,
        [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        if ($archive.Entries.Count -gt $maximumEntryCount) {
            throw "The update ZIP contains more than $maximumEntryCount entries."
        }
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            [void](Get-SafeZipTarget $entry.FullName $Destination)
            if ($entry.Length -gt $maximumEntryBytes) {
                throw "The update ZIP entry '$($entry.FullName)' exceeds the per-file limit."
            }
            $minimumCompressedBytes = [long][Math]::Ceiling(
                $entry.Length / [double]$maximumCompressionRatio)
            if ($entry.Length -gt 0 -and
                    $entry.CompressedLength -lt $minimumCompressedBytes) {
                throw "The update ZIP entry '$($entry.FullName)' exceeds the compression-ratio limit."
            }
            if ($expandedBytes -gt $maximumExpandedBytes - $entry.Length) {
                throw "The update ZIP exceeds the $maximumExpandedBytes-byte expanded-size limit."
            }
            $expandedBytes += $entry.Length
        }

        $destinationRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))
        try {
            $drive = [IO.DriveInfo]::new($destinationRoot)
            if ($drive.IsReady -and $expandedBytes -gt
                    $drive.AvailableFreeSpace - $minimumFreeSpaceReserve) {
                throw 'The update ZIP cannot be extracted without exhausting temporary storage.'
            }
        }
        catch [ArgumentException], [IO.IOException], [UnauthorizedAccessException] {
            # Some network-backed temporary directories do not expose drive capacity.
        }
    }
    finally { $archive.Dispose() }
}

function Expand-SafeZipArchive([IO.Stream]$Stream, [string]$Destination) {
    $Stream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($Stream,
        [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $target = Get-SafeZipTarget $entry.FullName $Destination

            $isDirectory = $entry.FullName.EndsWith('/', [StringComparison]::Ordinal) -or
                $entry.FullName.EndsWith('\', [StringComparison]::Ordinal)
            if ($isDirectory) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                continue
            }
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            $input = $entry.Open()
            $output = [IO.File]::Open($target, [IO.FileMode]::Create,
                [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(1024 * 1024)
                [long]$entryBytes = 0
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($entryBytes -gt $maximumEntryBytes - $read -or
                            $expandedBytes -gt $maximumExpandedBytes - $read) {
                        throw "The update ZIP exceeds its expanded-size limit while extracting '$($entry.FullName)'."
                    }
                    $entryBytes += $read
                    $expandedBytes += $read
                    $minimumCompressedBytes = [long][Math]::Ceiling(
                        $entryBytes / [double]$maximumCompressionRatio)
                    if ($entry.CompressedLength -lt $minimumCompressedBytes) {
                        throw "The update ZIP entry '$($entry.FullName)' exceeds the compression-ratio limit while extracting."
                    }
                    $output.Write($buffer, 0, $read)
                }
                if ($entryBytes -ne $entry.Length) {
                    throw "The update ZIP entry '$($entry.FullName)' has an inconsistent expanded size."
                }
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally { $archive.Dispose() }
}

try {
    $installRoot = Get-NormalizedPath $InstallDirectory
    $installPrefix = Get-ChildPathPrefix $installRoot
    $zipLock = [IO.File]::Open((Get-NormalizedPath $ZipPath),
        [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $actualSha256 = Get-StreamSha256 $zipLock
    if (-not $actualSha256.Equals($ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The update ZIP changed after verification.'
    }
    Assert-SafeZipArchive $zipLock $stagingRoot
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
    New-PrivilegedDirectory $stagingRoot
    Expand-SafeZipArchive $zipLock $stagingRoot
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

    New-PrivilegedDirectory $backupRoot
    $changes = [Collections.Generic.List[object]]::new()
    $createdDirectories = [Collections.Generic.List[string]]::new()
    $fileChangesCommitted = $false
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
                # Elevated updates create new topology with a protected DACL so
                # an inheritable user ACE cannot make the directory swappable
                # before its payload has been installed and verified.
                New-PrivilegedDirectory $destination
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
                New-PrivilegedDirectory $parent
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

        # Restore ordinary inherited read/execute permissions only after every
        # elevated file operation has completed. Explicit Administrators and
        # SYSTEM access remains as a safe fallback.
        $fileChangesCommitted = $true
        # Restore children before their parents. A parent can inherit an ACE
        # that makes its children replaceable by the unelevated caller.
        foreach ($directory in @($createdDirectories | Sort-Object Length -Descending)) {
            Enable-DirectoryInheritance $directory
        }
    }
    catch {
        $updateError = $_
        if ($fileChangesCommitted) {
            # Some directories may already be writable by the unelevated user.
            # Never perform privileged path-based rollback after that boundary.
            $preserveBackup = $true
            throw "Update files were installed, but directory permissions could not be " +
                "fully restored: $($updateError.Exception.Message) Recovery files remain " +
                "in $backupRoot."
        }
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
    if ($SkipRestart) { return }

    $restartPath = Get-NormalizedPath $RestartExecutable
    $restartInsideInstall = $restartPath.StartsWith($installPrefix,
        [StringComparison]::OrdinalIgnoreCase)
    if ($isElevated -and -not $restartInsideInstall) {
        throw 'An elevated portable update cannot restart an executable outside the installation directory.'
    }
    if ($restartInsideInstall) {
        Assert-NoReparsePath $installRoot (Split-Path -Parent $restartPath)
    }
    if (-not (Test-Path -LiteralPath $restartPath -PathType Leaf)) {
        throw 'The updated application executable is missing.'
    }
    $restartItem = Get-Item -LiteralPath $restartPath -Force
    if (($restartItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The updated application executable is a reparse point.'
    }

    # Keep the verified executable immutable until CreateProcess has opened it.
    # Writable portable trees run this helper without elevation; protected
    # portable trees retain UAC but cannot swap this path while it is launched.
    $restartLock = [IO.File]::Open($restartPath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Start-RestartProcess $restartPath $InstallDirectory
    }
    finally { $restartLock.Dispose() }
}
finally {
    if ($null -ne $zipLock) { $zipLock.Dispose() }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $preserveBackup -and (Test-Path -LiteralPath $backupRoot)) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
