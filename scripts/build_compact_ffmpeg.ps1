[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$SourceSha256,
    [Parameter(Mandatory)][string]$MsysRoot,
    [ValidateRange(1,32)][int]$Jobs = 8
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = '8.1.2'
$recipeIdentity = (@('build_compact_ffmpeg.ps1','ffmpeg-compact-build.sh','ffmpeg-pkg-config-static.sh','test_compact_ffmpeg.ps1') | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash
}) -join ':'
$archive = (Resolve-Path -LiteralPath $SourceArchive).Path
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $SourceSha256) {
    throw 'FFmpeg source archive SHA-256 does not match the reviewed upstream source hash.'
}
$msys = (Resolve-Path -LiteralPath $MsysRoot).Path
$bash = Join-Path $msys 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { throw 'MSYS2 bash is missing.' }
# Each build gets a new workspace-owned directory; existing outputs are retained.
$stage = Join-Path $root ('work\compact-ffmpeg\' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $stage 'source'
$build = Join-Path $stage 'build'
$runtime = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Force -Path $source,$build,$runtime | Out-Null
$entries = @(& tar.exe -tf $archive)
if ($LASTEXITCODE -ne 0 -or $entries.Count -eq 0 -or @($entries | Where-Object {
    -not $_.StartsWith("ffmpeg-$version/") -or $_.Contains('\') -or $_.Contains(':') -or
    $_.Split('/') -contains '..'
}).Count) { throw 'Unexpected paths in FFmpeg source archive.' }
$details = @(& tar.exe -tvf $archive)
if ($LASTEXITCODE -ne 0 -or @($details | Where-Object { $_ -notmatch '^[d-]' }).Count) {
    throw 'FFmpeg source archive contains a link or special file.'
}
& tar.exe -xf $archive -C $source
if ($LASTEXITCODE -ne 0) { throw 'Could not extract FFmpeg source archive.' }
$source = Join-Path $source "ffmpeg-$version"
function ConvertTo-MsysPath([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    return '/' + $absolute.Substring(0,1).ToLowerInvariant() + $absolute.Substring(2).Replace('\','/')
}
$previousMsystem = $env:MSYSTEM
try {
    $env:MSYSTEM = 'UCRT64'
    & $bash (ConvertTo-MsysPath (Join-Path $PSScriptRoot 'ffmpeg-compact-build.sh')) `
        (ConvertTo-MsysPath $source) (ConvertTo-MsysPath $build) $Jobs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Compact FFmpeg compilation failed.' }
}
finally { $env:MSYSTEM = $previousMsystem }
$binary = Join-Path $build 'ffmpeg.exe'
& (Join-Path $PSScriptRoot 'test_compact_ffmpeg.ps1') -Ffmpeg $binary
Copy-Item -LiteralPath $binary -Destination $runtime
Copy-Item -LiteralPath (Join-Path $source 'COPYING.GPLv3') -Destination (Join-Path $runtime 'LICENSE.txt')
$licenses = [Text.StringBuilder]::new([IO.File]::ReadAllText((Join-Path $runtime 'LICENSE.txt')))
[void]$licenses.AppendLine("`nlibx264 (GPL-2.0-or-later):")
[void]$licenses.AppendLine([IO.File]::ReadAllText((Join-Path $source 'COPYING.GPLv2')))
foreach ($dependency in @('opus','srt','openssl','libvpl','amf-headers','ffnvcodec-headers','libgcc','winpthreads','headers','mingw-w64-libraries')) {
    $licenseRoot = Join-Path $msys "ucrt64\share\licenses\$dependency"
    $texts = @(Get-ChildItem -LiteralPath $licenseRoot -File -Recurse -Force)
    if (-not $texts.Count) { throw "Missing dependency license: $dependency" }
    foreach ($text in $texts) {
        [void]$licenses.AppendLine("`n--- $dependency / $($text.Name) ---")
        [void]$licenses.AppendLine([IO.File]::ReadAllText($text.FullName))
    }
}
[IO.File]::WriteAllText((Join-Path $runtime 'LICENSE.txt'), $licenses.ToString(), [Text.UTF8Encoding]::new($false))
$packages = @('gcc','libx264','opus','srt','openssl','libvpl','amf-headers','ffnvcodec-headers') | ForEach-Object { "mingw-w64-ucrt-x86_64-$_" }
$packageVersions = & (Join-Path $msys 'usr\bin\pacman.exe') -Q @packages
if ($LASTEXITCODE -ne 0) { throw 'Could not record compact FFmpeg build dependency versions.' }
$sourceUrl = "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz"
$record = "FFmpeg $version compact build`nSource: $sourceUrl`nSource SHA-256: $SourceSha256`nRecipe: scripts/ffmpeg-compact-build.sh`nDependency recipes and sources: https://github.com/msys2/MINGW-packages`nDependency versions:`n$($packageVersions -join "`n")`n`nDistributors must provide corresponding sources (including dependency recipes/patches) with their GPL binary distribution.`n"
[IO.File]::WriteAllText((Join-Path $runtime 'SOURCE.txt'), $record, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $runtime 'README.txt'),
    'iPhoneMirror compact FFmpeg: H.264 hardware/software encoding, AAC/Opus, MP4, RTMP/SRT/WHIP, HLS and DirectShow audio.', [Text.UTF8Encoding]::new($false))
$hashes = @('ffmpeg.exe','LICENSE.txt','README.txt','SOURCE.txt') | ForEach-Object {
    "        '$_' = '$((Get-FileHash -LiteralPath (Join-Path $runtime $_) -Algorithm SHA256).Hash)'"
}
$manifest = "@{`n    Version = '$version'`n    Provider = 'iPhoneMirror compact build'`n    RecipeIdentity = '$recipeIdentity'`n    ArchiveName = 'ffmpeg-$version.tar.xz'`n    DownloadUrl = '$sourceUrl'`n    ArchiveSha256 = '$SourceSha256'`n    PrebuiltDirectory = '$($runtime.Replace("'","''"))'`n    Files = @{`n$($hashes -join "`n")`n    }`n}`n"
$manifestPath = Join-Path $stage 'runtime-manifest.psd1'
[IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))
Write-Host "Compact FFmpeg manifest: $manifestPath"
Write-Host "FFmpeg binary size: $((Get-Item -LiteralPath $binary).Length) bytes"
Write-Output $manifestPath
