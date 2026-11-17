$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identity = 'fallback-test-' + [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $root ('work/ffmpeg-' + $identity)
$cache = Join-Path $root ('work/dependencies/ffmpeg-' + $identity)
$attempts = [Collections.Generic.List[string]]::new()
try {
    $package = Join-Path $fixture 'package'
    New-Item -ItemType Directory -Force -Path (Join-Path $package 'bin') | Out-Null
    [IO.File]::WriteAllText((Join-Path $package 'bin/ffmpeg.exe'), 'executable fixture')
    [IO.File]::WriteAllText((Join-Path $package 'LICENSE'), 'license fixture')
    [IO.File]::WriteAllText((Join-Path $package 'README.txt'), 'readme fixture')
    $archive = Join-Path $fixture 'fixture.zip'
    Compress-Archive -LiteralPath $package -DestinationPath $archive
    $hash = (Get-FileHash $archive).Hash
    $exeHash = (Get-FileHash (Join-Path $package 'bin/ffmpeg.exe')).Hash
    $licenseHash = (Get-FileHash (Join-Path $package 'LICENSE')).Hash
    $readmeHash = (Get-FileHash (Join-Path $package 'README.txt')).Hash
    $manifest = Join-Path $fixture 'manifest.psd1'
    [IO.File]::WriteAllText($manifest, "@{ Version='$identity'; ArchiveName='fixture.zip'; DownloadUrl='https://primary.invalid/fixture.zip'; FallbackDownloadUrls=@('https://fallback.invalid/fixture.zip'); ArchiveSha256='$hash'; Files=@{'ffmpeg.exe'='$exeHash';'LICENSE.txt'='$licenseHash';'README.txt'='$readmeHash'} }")
    function Invoke-WebRequest {
        param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec)
        $attempts.Add([string]$Uri)
        if ($attempts.Count -eq 1) { throw [IO.IOException]::new('Injected primary outage') }
        Copy-Item -LiteralPath $archive -Destination $OutFile
    }
    function Start-Sleep { param($Seconds) }
    $destination = Join-Path $fixture 'result'
    & (Join-Path $PSScriptRoot 'prepare_ffmpeg.ps1') -Destination $destination -RuntimeManifestPath $manifest | Out-Null
    if ($attempts.Count -ne 2 -or $attempts[1] -cne 'https://fallback.invalid/fixture.zip' -or
        (Get-FileHash (Join-Path $destination 'ffmpeg.exe')).Hash -cne $exeHash) { throw 'Fallback download or prepared hash verification failed.' }
    # A corrupt fallback must still fail the pinned archive check.
    Remove-Item -LiteralPath (Join-Path $cache 'fixture.zip')
    [IO.File]::WriteAllText($archive, 'corrupt fallback archive')
    $attempts.Clear()
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'prepare_ffmpeg.ps1') -Destination $destination -RuntimeManifestPath $manifest | Out-Null }
    catch { if ($_.Exception.Message -notlike 'FFmpeg archive hash mismatch:*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Corrupt fallback was accepted.' }
    Write-Output 'PASS FFmpeg primary outage uses fallback; archive and extracted-file hashes remain enforced.'
} finally {
    foreach ($path in @($fixture, $cache)) {
        $full = [IO.Path]::GetFullPath($path)
        if (-not $full.StartsWith((Join-Path $root 'work') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not $full.Contains($identity)) { throw 'Unexpected fixture cleanup path.' }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
}
