[CmdletBinding()]
param([switch]$RequireExisting)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceHash = '464BEB5E7BF0C311E68B45AE2F04E9CC2AF88851ABB4082231742A74D97B524C'
$cache = Join-Path $root 'work\dependencies\ffmpeg-compact-8.1.2'
$cachedManifest = Join-Path $cache 'runtime-manifest.psd1'
$recipe = @('build_compact_ffmpeg.ps1','ffmpeg-compact-build.sh','ffmpeg-pkg-config-static.sh','test_compact_ffmpeg.ps1') | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash
}
$recipeIdentity = $recipe -join ':'
if (Test-Path -LiteralPath $cachedManifest) {
    $manifest = Import-PowerShellDataFile -LiteralPath $cachedManifest
    if ($manifest.ArchiveSha256 -eq $sourceHash -and $manifest.RecipeIdentity -eq $recipeIdentity) {
        foreach ($entry in $manifest.Files.GetEnumerator()) {
            if ((Get-FileHash -LiteralPath (Join-Path $manifest.PrebuiltDirectory $entry.Key)).Hash -ine $entry.Value) {
                throw "Cached compact FFmpeg is damaged: $($entry.Key)"
            }
        }
        Write-Output $cachedManifest
        return
    }
}
if ($RequireExisting) { throw 'Compact FFmpeg cache is missing or its recipe changed. Run build.ps1 before -SkipBuild packaging.' }
$candidates = @($env:IPHONE_MIRROR_MSYS2_ROOT, 'C:\msys64',
    (Join-Path $root 'work\dependencies\msys2-20260611\root\msys64'))
$bash = Get-Command bash.exe -ErrorAction SilentlyContinue
if ($bash -and $bash.Source -match '\\usr\\bin\\bash\.exe$') {
    $candidates += (Get-Item -LiteralPath $bash.Source).Directory.Parent.Parent.FullName
}
$msysCommand = Get-Command msys2.cmd -ErrorAction SilentlyContinue
if ($msysCommand) {
    $candidates += Join-Path (Split-Path -Parent $msysCommand.Source) 'msys64'
    $candidates += Split-Path -Parent $msysCommand.Source
}
$msys = $candidates | Where-Object {
    $_ -and (Test-Path -LiteralPath (Join-Path $_ 'ucrt64\bin\gcc.exe')) -and
    (Test-Path -LiteralPath (Join-Path $_ 'usr\bin\bash.exe'))
} | Select-Object -First 1
if (-not $msys) { throw 'Set IPHONE_MIRROR_MSYS2_ROOT to an MSYS2 UCRT64 installation with the compact FFmpeg dependencies.' }
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$archive = Join-Path $cache 'ffmpeg-8.1.2.tar.xz'
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ine $sourceHash) {
    $temporary = "$archive.$([Guid]::NewGuid().ToString('N')).download"
    try {
        # Invoke-WebRequest honors the Windows system proxy. The hash is pinned
        # from the official release signature (FCF986EA...D67658D8).
        Invoke-WebRequest 'https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz' -OutFile $temporary -TimeoutSec 300
        if ((Get-FileHash -LiteralPath $temporary).Hash -ine $sourceHash) { throw 'FFmpeg source hash mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $archive -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
$generated = & (Join-Path $PSScriptRoot 'build_compact_ffmpeg.ps1') `
    -SourceArchive $archive -SourceSha256 $sourceHash -MsysRoot $msys | Select-Object -Last 1
if (-not $generated -or -not (Test-Path -LiteralPath $generated)) { throw 'Compact FFmpeg build did not produce a manifest.' }
# The builder includes the exact recipe identity in its manifest. A changed
# recipe never silently reuses an earlier executable on a developer machine.
$built = Import-PowerShellDataFile -LiteralPath $generated
if ($built.RecipeIdentity -ne $recipeIdentity) { throw 'FFmpeg recipe changed during compilation; rebuild before packaging.' }
Copy-Item -LiteralPath $generated -Destination $cachedManifest -Force
Write-Output $cachedManifest
