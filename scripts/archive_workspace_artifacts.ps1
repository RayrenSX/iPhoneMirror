#requires -Version 7.0
<#
.SYNOPSIS
Reports or archives idle local build artifacts without changing their original paths.
.EXAMPLE
./scripts/archive_workspace_artifacts.ps1
.EXAMPLE
./scripts/archive_workspace_artifacts.ps1 -Archive -ArchiveRoot F:/iPhoneMirror-archive -MaximumArchiveGB 75
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArchiveRoot = 'F:/iPhoneMirror-archive',
    [ValidateRange(0, 87600)][double]$MinimumAgeHours = 12,
    [ValidateRange(0, 1048576)][double]$MinimumSizeMB = 100,
    [ValidateRange(0, 1048576)][double]$MaximumArchiveGB = 0,
    [switch]$Compressed,
    [ValidateRange(1, 4095)][int]$VolumeMB = 2048,
    [string]$Python = 'python',
    [string[]]$KeepRelativePath = @(),
    [switch]$Archive
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\', '/')
$archiveBase = [IO.Path]::GetFullPath($ArchiveRoot).TrimEnd('\', '/')
$comparison = [StringComparison]::OrdinalIgnoreCase
if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) { throw "Not a Git checkout: $repo" }
if ($archiveBase.Equals($repo, $comparison) -or $archiveBase.StartsWith($repo + '\', $comparison)) {
    throw 'The archive must be outside the working repository.'
}
if ([IO.Path]::GetPathRoot($repo).Equals([IO.Path]::GetPathRoot($archiveBase), $comparison)) {
    throw 'Choose another drive so archiving releases space on the workspace drive.'
}
if (-not ('WorkspaceArtifactStorage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

public sealed class WorkspaceArtifactSnapshot {
    public long Bytes;
    public DateTime NewestUtc;
    public Dictionary<string, (long Size, long Time)> Files = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase);
}
public static class WorkspaceArtifactStorage {
    public static WorkspaceArtifactSnapshot Scan(string root) {
        var result = new WorkspaceArtifactSnapshot { NewestUtc = Directory.GetLastWriteTimeUtc(root) };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0) {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Contains a junction or symbolic link: " + directory.FullName);
            foreach (var entry in directory.EnumerateFileSystemInfos()) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Contains a junction or symbolic link: " + entry.FullName);
                result.NewestUtc = entry.LastWriteTimeUtc > result.NewestUtc ? entry.LastWriteTimeUtc : result.NewestUtc;
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry is DirectoryInfo child) {
                    result.Directories.Add(relative);
                    pending.Push(child);
                } else if (entry is FileInfo file) {
                    result.Bytes += file.Length;
                    result.Files.Add(relative, (file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
        }
        return result;
    }
    static void Compare(WorkspaceArtifactSnapshot expected, WorkspaceArtifactSnapshot actual, long timeTolerance = 0) {
        if (expected.Bytes != actual.Bytes || expected.Files.Count != actual.Files.Count ||
            !expected.Directories.SetEquals(actual.Directories))
            throw new IOException("Directory inventory changed or the copy is incomplete.");
        foreach (var file in expected.Files)
            if (!actual.Files.TryGetValue(file.Key, out var found) || found.Size != file.Value.Size ||
                Math.Abs(found.Time - file.Value.Time) > timeTolerance)
                throw new IOException("File size or timestamp differs: " + file.Key);
    }
    public static long AllocatedBytes(WorkspaceArtifactSnapshot snapshot, long clusterSize) {
        long total = 0;
        foreach (var file in snapshot.Files.Values) total += ((file.Size + clusterSize - 1) / clusterSize) * clusterSize;
        return total;
    }
    public static void AssertUnchanged(string root, WorkspaceArtifactSnapshot expected) {
        Compare(expected, Scan(root));
    }
    public static void Verify(string source, string destination, WorkspaceArtifactSnapshot expected, bool fatTimestamps = false) {
        Compare(expected, Scan(source));
        Compare(expected, Scan(destination), fatTimestamps ? TimeSpan.TicksPerSecond * 2 : 0);
        Parallel.ForEach(expected.Files.Keys, new ParallelOptions { MaxDegreeOfParallelism = 4 }, relative => {
            using var first = File.OpenRead(Path.Combine(source, relative));
            using var second = File.OpenRead(Path.Combine(destination, relative));
            using var sha = SHA256.Create();
            if (!Convert.ToBase64String(sha.ComputeHash(first)).Equals(Convert.ToBase64String(sha.ComputeHash(second))))
                throw new IOException("SHA-256 mismatch: " + relative);
        });
        Compare(expected, Scan(source));
    }
}
'@
}

function Assert-LocalArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $allowed = @('work', 'outputs/iPhoneMirror-main') | ForEach-Object {
        [IO.Path]::GetFullPath((Join-Path $repo $_)).TrimEnd('\') + '\'
    }
    if (-not ($allowed | Where-Object { $full.StartsWith($_, $comparison) })) {
        throw "Outside the permitted artifact directories: $full"
    }
    $current = $full
    while ($true) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Path passes through a junction or symbolic link: $current"
        }
        if ($current.Equals($repo, $comparison)) { break }
        $current = Split-Path -Parent $current
    }
    return $full
}

function Test-ArtifactInUse([string]$Path, [object[]]$Processes) {
    $absolute = $Path.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
    $relative = [IO.Path]::GetRelativePath($repo, $Path).Replace('\', '/').ToLowerInvariant()
    foreach ($process in $Processes) {
        $executable = ([string]$process.ExecutablePath).Replace('\', '/').ToLowerInvariant()
        if ($executable.StartsWith($absolute + '/')) { return $true }
        $command = ([string]$process.CommandLine).Replace('\', '/').ToLowerInvariant()
        foreach ($needle in @($absolute, $relative)) {
            if ($command -match ([regex]::Escape($needle) + '(?=/|["''\s]|$)')) { return $true }
        }
    }
    return $false
}

$kept = @('work/main-consolidation', 'work/storage-maintenance', 'work/coredevice-build-tools',
    'work/usb-touch-bridge-python', 'work/dependencies', 'work/usb-bridge-build',
    'outputs/iPhoneMirror-main/Assets', 'outputs/iPhoneMirror-main/licenses',
    'outputs/iPhoneMirror-main/tools', 'outputs/iPhoneMirror-main/Wireless') + $KeepRelativePath
$kept = @($kept | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repo $_)).TrimEnd('\') })
$tracked = @(& git -C $repo ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git index.' }
$processes = @(Get-CimInstance Win32_Process)
$cutoff = [DateTime]::UtcNow.AddHours(-$MinimumAgeHours)
$candidates = [Collections.Generic.List[object]]::new()
$skipped = [Collections.Generic.List[object]]::new()
foreach ($parent in @('work', 'outputs/iPhoneMirror-main')) {
    $parentPath = Join-Path $repo $parent
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) { continue }
    # Never enumerate the contents of an archive link.
    if ((Get-Item -LiteralPath $parentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    foreach ($directory in Get-ChildItem -LiteralPath $parentPath -Directory -Force) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($directory.Name -like '*.archive-pending-*') { continue }
        if ($kept | Where-Object { $directory.FullName.Equals($_, $comparison) -or $_.StartsWith($directory.FullName + '\', $comparison) }) { continue }
        $relative = [IO.Path]::GetRelativePath($repo, $directory.FullName).Replace('\', '/')
        try {
            $source = Assert-LocalArtifactPath $directory.FullName
            if ($tracked | Where-Object { $_.StartsWith($relative + '/', $comparison) }) {
                throw 'Contains tracked files.'
            }
            & git -C $repo check-ignore -q -- $relative
            if ($LASTEXITCODE -ne 0) { throw 'Directory is not ignored by Git.' }
            if (Test-ArtifactInUse $source $processes) { throw 'Referenced by a running process.' }
            $snapshot = [WorkspaceArtifactStorage]::Scan($source)
            if ($snapshot.Bytes -lt $MinimumSizeMB * 1MB -or $snapshot.NewestUtc -gt $cutoff) { continue }
            $candidates.Add([pscustomobject]@{ Source = $source; RelativePath = $relative; Bytes = $snapshot.Bytes;
                Files = $snapshot.Files.Count; Snapshot = $snapshot })
        } catch {
            $skipped.Add([pscustomobject]@{ Path = $relative; Reason = $_.Exception.Message })
        }
    }
}
if ($MaximumArchiveGB -gt 0) {
    $selected = [Collections.Generic.List[object]]::new()
    [long]$selectedBytes = 0
    foreach ($candidate in $candidates | Sort-Object Bytes -Descending) {
        if ($selectedBytes + $candidate.Bytes -le $MaximumArchiveGB * 1GB) {
            $selected.Add($candidate)
            $selectedBytes += $candidate.Bytes
        }
    }
    $candidates = $selected
}
$total = ($candidates | Measure-Object -Property Bytes -Sum).Sum
Write-Host ('Eligible: {0} directories, {1:N2} GiB. Mode: {2}' -f $candidates.Count, ($total / 1GB), $(if ($Archive) { 'archive' } else { 'report only' }))
$candidates | Sort-Object Bytes -Descending | Select-Object -First 10 RelativePath, @{Name='GiB';Expression={[Math]::Round($_.Bytes / 1GB, 3)}} | Format-Table -AutoSize | Out-Host
$skipped | Format-Table -AutoSize | Out-Host

$runName = (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$reportDirectory = Join-Path $repo 'work/storage-maintenance'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$reportPath = Join-Path $reportDirectory ($runName + '.json')
$destinationRoot = Join-Path $archiveBase $runName
$report = [ordered]@{ CreatedAt = [DateTimeOffset]::Now.ToString('o'); Workspace = $repo; ArchiveRoot = $destinationRoot;
    Archive = [bool]$Archive; Compressed = [bool]$Compressed; EligibleBytes = $total; SourceFreeBytesBefore = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($repo)).AvailableFreeSpace;
    Candidates = @($candidates | Select-Object RelativePath, Bytes, Files); Skipped = @($skipped.ToArray()); Results = @() }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
if ($Archive -and $total -gt 0) {
    $archiveDrive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($archiveBase))
    $fatTimestamps = $archiveDrive.DriveFormat -in @('FAT', 'FAT32')
    $archiveVolume = Get-Volume -DriveLetter $archiveDrive.Name.Substring(0, 1)
    $clusterSize = [Math]::Max(4096, $archiveVolume.AllocationUnitSize)
    [long]$estimatedAllocation = 0
    foreach ($candidate in $candidates) {
        if (-not $Compressed -and $fatTimestamps -and @($candidate.Snapshot.Files.Values | Where-Object Item1 -gt ([uint32]::MaxValue)).Count -gt 0) {
            throw "A file exceeds FAT32's file-size limit: $($candidate.RelativePath)"
        }
        $estimatedAllocation += [WorkspaceArtifactStorage]::AllocatedBytes($candidate.Snapshot, $clusterSize)
    }
    $report.EstimatedDestinationAllocationBytes = $estimatedAllocation
    if (-not $archiveDrive.IsReady -or $archiveDrive.AvailableFreeSpace -lt $(if ($Compressed) { 5GB } else { $estimatedAllocation + 4GB })) {
        throw 'The archive drive does not have enough free space.'
    }
    foreach ($candidate in $candidates | Sort-Object Bytes -Descending) {
        $source = $candidate.Source
        $destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $candidate.RelativePath))
        if (-not $destination.StartsWith($destinationRoot.TrimEnd('\') + '\', $comparison)) { throw "Invalid archive path: $destination" }
        if (-not $PSCmdlet.ShouldProcess($source, "Archive to $destination, verify SHA-256, and release the local copy")) { continue }
        $entry = [ordered]@{ RelativePath = $candidate.RelativePath; Destination = $destination; Bytes = $candidate.Bytes; Status = 'Copying' }
        $report.Results += $entry
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
        try {
            $null = Assert-LocalArtifactPath $source
            if (Test-ArtifactInUse $source @(Get-CimInstance Win32_Process)) { throw 'A process started using this directory.' }
            if (Test-Path -LiteralPath $destination) { throw "Destination already exists: $destination" }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            if ($Compressed) {
                [WorkspaceArtifactStorage]::AssertUnchanged($source, $candidate.Snapshot)
                & $Python (Join-Path $PSScriptRoot 'workspace_archive.py') pack --source $source --destination $destination --volume-bytes ($VolumeMB * 1MB)
                if ($LASTEXITCODE -ne 0) { throw 'Compression or archive verification failed; source retained.' }
                $summary = Get-Content -LiteralPath (Join-Path $destination 'summary.json') -Raw | ConvertFrom-Json
                if (-not $summary.verified) { throw 'Archive did not pass verification.' }
                [WorkspaceArtifactStorage]::AssertUnchanged($source, $candidate.Snapshot)
                $entry.CompressedBytes = $summary.compressed_bytes
                $entry.Manifest = Join-Path $destination 'manifest.json'
                $entry.Format = 'split-tar-' + $summary.codec
            } else {
                $copyLog = Join-Path $reportDirectory ($runName + '-' + $report.Results.Count + '.log')
                & robocopy $source $destination /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /MT:8 /NP /NFL /NDL /NJH /NJS "/LOG:$copyLog" | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Copy failed; source retained. See $copyLog" }
                $entry.Status = 'Verifying'
                $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
                [WorkspaceArtifactStorage]::Verify($source, $destination, $candidate.Snapshot, $fatTimestamps)
            }
            $null = Assert-LocalArtifactPath $source
            if (Test-ArtifactInUse $source @(Get-CimInstance Win32_Process)) { throw 'A process started using this directory.' }
            $localRemainder = $source + '.archive-pending-' + [Guid]::NewGuid().ToString('N')
            if (-not $localRemainder.StartsWith($repo + '\', $comparison)) { throw 'Invalid local staging path.' }
            $entry.LocalRemainder = $localRemainder
            $entry.Status = 'ReplacingLocalCopy'
            $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
            Rename-Item -LiteralPath $source -NewName ([IO.Path]::GetFileName($localRemainder))
            try {
                if ($Compressed) {
                    New-Item -ItemType Directory -Path $source | Out-Null
                    [ordered]@{ OriginalPath = $source; Manifest = $entry.Manifest; Bytes = $candidate.Bytes;
                        RestoreCommand = ('python "{0}" restore --manifest "{1}" --destination "{2}"' -f (Join-Path $PSScriptRoot 'workspace_archive.py'), $entry.Manifest, $source)
                    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $source 'WORKSPACE-ARCHIVE.json') -Encoding utf8
                } else {
                    New-Item -ItemType Junction -Path $source -Target $destination | Out-Null
                }
            } catch {
                if (-not (Test-Path -LiteralPath $source)) {
                    Rename-Item -LiteralPath $localRemainder -NewName ([IO.Path]::GetFileName($source))
                }
                throw
            }
            # Delete only the renamed, verified local copy. Never recurse through the junction.
            $null = Assert-LocalArtifactPath $localRemainder
            $null = [WorkspaceArtifactStorage]::Scan($localRemainder)
            Remove-Item -LiteralPath $localRemainder -Recurse -Force
            $entry.Status = 'Archived'
            Write-Host ('Archived {0} ({1:N2} GiB)' -f $candidate.RelativePath, ($candidate.Bytes / 1GB))
        } catch {
            $entry.Status = 'RetainedOrNeedsReview'
            $entry.Error = $_.Exception.Message
            Write-Warning ("Retained for review: {0}: {1}" -f $candidate.RelativePath, $entry.Error)
        }
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    }
}
$report.SourceFreeBytesAfter = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($repo)).AvailableFreeSpace
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host ('Workspace drive free: {0:N2} GiB. Report: {1}' -f ($report.SourceFreeBytesAfter / 1GB), $reportPath)
if (@($report.Results | Where-Object Status -ne 'Archived').Count -gt 0) {
    throw 'Some directories were retained or need review; inspect the report before retrying.'
}
