#requires -Version 7.0
[CmdletBinding()]
param([string]$ArchiveRoot = 'F:/iPhoneMirror-archive')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'archive_workspace_artifacts.ps1'
$id = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $repo "work/storage-maintenance/test-$id"
$destination = [IO.Path]::GetFullPath((Join-Path $ArchiveRoot "test-$id"))
$child = $null
function Assert-StorageTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    & git -C $fixture init -b main --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize fixture repository.' }
    "/work/`n/outputs/`n" | Set-Content -LiteralPath (Join-Path $fixture '.gitignore')
    $names = @('work/old-build', 'outputs/iPhoneMirror-main/old-delivery', 'work/in-use',
        'work/recent-file', 'work/tracked', 'work/kept/important', 'work/main-consolidation',
        'work/with-link', 'outputs/iPhoneMirror-main/tools', 'outside')
    foreach ($name in $names) {
        $path = Join-Path $fixture $name
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $path 'data.bin'), [byte[]](0..255) * 16)
    }
    $unicode = Join-Path $fixture 'work/old-build/数据 空格.txt'
    'Archive verification fixture' | Set-Content -LiteralPath $unicode
    (Get-Item -LiteralPath $unicode).IsReadOnly = $true
    New-Item -ItemType Directory -Path (Join-Path $fixture 'work/old-build/empty') | Out-Null
    'Start-Sleep -Seconds 55' | Set-Content -LiteralPath (Join-Path $fixture 'work/in-use/hold.ps1')
    New-Item -ItemType Junction -Path (Join-Path $fixture 'work/with-link/external') -Target (Join-Path $fixture 'outside') | Out-Null
    & git -C $fixture add -f -- work/tracked/data.bin
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare tracked-file fixture.' }
    $old = [DateTime]::UtcNow.AddDays(-10)
    foreach ($parent in @('work', 'outputs')) {
        Get-ChildItem -LiteralPath (Join-Path $fixture $parent) -Recurse -Force |
            Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
            ForEach-Object { $_.LastWriteTimeUtc = $old }
    }
    (Get-Item -LiteralPath (Join-Path $fixture 'work/recent-file/data.bin')).LastWriteTimeUtc = [DateTime]::UtcNow
    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-File', ('"' + (Join-Path $fixture 'work/in-use/hold.ps1') + '"')) -WindowStyle Hidden -PassThru
    $parameters = @{ WorkspaceRoot = $fixture; ArchiveRoot = $destination; MinimumSizeMB = 0;
        MinimumAgeHours = 1; KeepRelativePath = @('work/kept/important') }
    & $script @parameters
    Assert-StorageTest (-not (Test-Path -LiteralPath $destination)) 'Report mode created an archive.'
    Assert-StorageTest (-not ((Get-Item -LiteralPath (Join-Path $fixture 'work/old-build')).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Report mode moved files.'
    $originalHash = (Get-FileHash -LiteralPath $unicode).Hash
    & $script @parameters -Archive -Confirm:$false
    foreach ($relative in @('work/old-build', 'outputs/iPhoneMirror-main/old-delivery')) {
        $path = Join-Path $fixture $relative
        Assert-StorageTest ([bool]((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) "Missing archive junction: $relative"
        Assert-StorageTest ((Get-Item -LiteralPath $path).Target.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase)) 'Junction points outside the test archive.'
    }
    Assert-StorageTest ((Get-FileHash -LiteralPath $unicode).Hash -eq $originalHash) 'Archived contents changed.'
    Assert-StorageTest (Test-Path -LiteralPath (Join-Path $fixture 'work/old-build/empty') -PathType Container) 'Empty directory was lost.'
    foreach ($relative in @('work/in-use', 'work/recent-file', 'work/tracked', 'work/kept', 'work/main-consolidation', 'work/with-link', 'outputs/iPhoneMirror-main/tools')) {
        Assert-StorageTest (-not ((Get-Item -LiteralPath (Join-Path $fixture $relative)).Attributes -band [IO.FileAttributes]::ReparsePoint)) "Protected directory was moved: $relative"
    }
    # A same-size, same-timestamp corruption must fail checksum verification.
    $first = Join-Path $fixture 'outside/first'
    $second = Join-Path $fixture 'outside/second'
    New-Item -ItemType Directory -Path $first, $second | Out-Null
    [IO.File]::WriteAllText((Join-Path $first 'file'), 'good')
    Copy-Item -LiteralPath (Join-Path $first 'file') -Destination (Join-Path $second 'file')
    [IO.File]::WriteAllText((Join-Path $second 'file'), 'evil')
    (Get-Item -LiteralPath (Join-Path $second 'file')).LastWriteTimeUtc = (Get-Item -LiteralPath (Join-Path $first 'file')).LastWriteTimeUtc
    $detected = $false
    try { [WorkspaceArtifactStorage]::Verify($first, $second, [WorkspaceArtifactStorage]::Scan($first)) } catch { $detected = $true }
    Assert-StorageTest $detected 'Same-size file corruption was not detected.'
    $compressedSource = Join-Path $fixture 'work/compressed-build'
    New-Item -ItemType Directory -Path (Join-Path $compressedSource 'empty') -Force | Out-Null
    $compressedData = Join-Path $compressedSource '随机 数据.bin'
    [IO.File]::WriteAllBytes($compressedData, [Security.Cryptography.RandomNumberGenerator]::GetBytes(2500000))
    $compressedHash = (Get-FileHash -LiteralPath $compressedData).Hash
    Get-ChildItem -LiteralPath $compressedSource -Recurse -Force | ForEach-Object { $_.LastWriteTimeUtc = $old }
    (Get-Item -LiteralPath $compressedSource).LastWriteTimeUtc = $old
    & $script @parameters -Archive -Compressed -VolumeMB 1 -Confirm:$false
    $pointerPath = Join-Path $compressedSource 'WORKSPACE-ARCHIVE.json'
    Assert-StorageTest (Test-Path -LiteralPath $pointerPath) 'Compressed archive pointer was not created.'
    Assert-StorageTest (-not (Test-Path -LiteralPath $compressedData)) 'Verified local payload was not released.'
    $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $pointer.Manifest -Raw | ConvertFrom-Json
    Assert-StorageTest ($manifest.parts.Count -ge 3) 'Archive was not split at its volume limit.'
    Assert-StorageTest (@($manifest.parts | Where-Object bytes -gt 1MB).Count -eq 0) 'Archive volume exceeded its size limit.'
    $firstPart = Join-Path (Split-Path -Parent $pointer.Manifest) $manifest.parts[0].name
    $goodBytes = [IO.File]::ReadAllBytes($firstPart)
    $badBytes = $goodBytes.Clone()
    $badBytes[20] = $badBytes[20] -bxor 1
    [IO.File]::WriteAllBytes($firstPart, $badBytes)
    $corruptionOutput = & python (Join-Path $PSScriptRoot 'workspace_archive.py') verify --manifest $pointer.Manifest 2>&1
    Assert-StorageTest ($LASTEXITCODE -ne 0) 'Corrupted archive passed verification.'
    [IO.File]::WriteAllBytes($firstPart, $goodBytes)
    & python (Join-Path $PSScriptRoot 'workspace_archive.py') restore --manifest $pointer.Manifest --destination $compressedSource
    Assert-StorageTest ($LASTEXITCODE -eq 0) 'Split archive could not be restored.'
    Assert-StorageTest ((Get-FileHash -LiteralPath $compressedData).Hash -eq $compressedHash) 'Restored file differs from its original.'
    Assert-StorageTest (Test-Path -LiteralPath (Join-Path $compressedSource 'empty') -PathType Container) 'Compressed archive lost an empty directory.'
    & $script @parameters
    $latest = Get-ChildItem -LiteralPath (Join-Path $fixture 'work/storage-maintenance') -Filter '*.json' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $report = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    Assert-StorageTest ($report.Candidates.Count -eq 0) 'A repeated run tried to archive existing links or protected data.'
    Write-Host 'PASS: report mode, verified relocation, Unicode/read-only/empty files, protected paths, active processes, reparse points, file/volume corruption, split archive restoration, and repeat runs.' -ForegroundColor Green
} finally {
    if ($child -and -not $child.HasExited) { Stop-Process -Id $child.Id -Force }
    if (Test-Path -LiteralPath $fixture) {
        # Remove links themselves before recursively deleting the disposable fixture.
        foreach ($relative in @('work/old-build', 'outputs/iPhoneMirror-main/old-delivery', 'work/with-link/external')) {
            $path = Join-Path $fixture $relative
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                [IO.Directory]::Delete($path)
            }
        }
        $expectedParent = [IO.Path]::GetFullPath((Join-Path $repo 'work/storage-maintenance')).TrimEnd('\') + '\'
        if (-not ([IO.Path]::GetFullPath($fixture).StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase))) { throw 'Invalid fixture cleanup path.' }
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
    if (Test-Path -LiteralPath $destination) {
        $expectedParent = [IO.Path]::GetFullPath($ArchiveRoot).TrimEnd('\') + '\test-'
        if (-not $destination.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test archive cleanup path.' }
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}
