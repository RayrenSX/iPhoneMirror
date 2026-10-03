[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][string]$MsysRoot,
    [ValidateRange(1,32)][int]$Jobs = 8
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# The receiver was built against this snapshot (avcodec 58.137.100), not 4.4.2.
$commit = '63b2b0f47df420007c53888ce0e8383d24b8fb06'
$sourceHash = '2D6E9244BE21B32756C481F4653D7F17AE08608DD2DB1BA04E826CF0052DF860'
$archive = (Resolve-Path -LiteralPath $SourceArchive).Path
if ((Get-FileHash -LiteralPath $archive).Hash -ine $sourceHash) {
    throw 'AirPlay FFmpeg source archive does not match the pinned upstream snapshot.'
}
$msys = (Resolve-Path -LiteralPath $MsysRoot).Path
$bash = Join-Path $msys 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { throw 'MSYS2 bash is missing.' }
$stage = Join-Path $root ('work\airplay-ffmpeg\' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $stage 'source'
$build = Join-Path $stage 'build'
$runtime = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Force -Path $source,$build,$runtime | Out-Null
$entries = @(& tar.exe -tf $archive)
if ($LASTEXITCODE -ne 0 -or $entries.Count -eq 0 -or @($entries | Where-Object {
    -not $_.StartsWith("FFmpeg-$commit/") -or $_.Contains('\') -or $_.Contains(':') -or
    $_.Split('/') -contains '..'
}).Count) { throw 'Unexpected paths in AirPlay FFmpeg source archive.' }
$details = @(& tar.exe -tvf $archive)
if ($LASTEXITCODE -ne 0 -or @($details | Where-Object { $_ -notmatch '^[d-]' }).Count) {
    throw 'AirPlay FFmpeg source archive contains a link or special file.'
}
& tar.exe -xf $archive -C $source
if ($LASTEXITCODE -ne 0) { throw 'Could not extract AirPlay FFmpeg source archive.' }
$source = Join-Path $source "FFmpeg-$commit"
# Apply only our reviewed two-line compiler fix to the newly extracted source.
# Absolute --directory is intentional; the archive and patch paths are pinned.
& git apply --unsafe-paths --directory=$($source.Replace('\','/')) `
    (Join-Path $PSScriptRoot 'ffmpeg-airplay-mathops.patch')
if ($LASTEXITCODE) { throw 'Could not apply AirPlay FFmpeg assembler compatibility patch.' }
function ConvertTo-MsysPath([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    return '/' + $absolute.Substring(0,1).ToLowerInvariant() + $absolute.Substring(2).Replace('\','/')
}
$previousMsystem = $env:MSYSTEM
try {
    $env:MSYSTEM = 'UCRT64'
    & $bash (ConvertTo-MsysPath (Join-Path $PSScriptRoot 'ffmpeg-airplay-build.sh')) `
        (ConvertTo-MsysPath $source) (ConvertTo-MsysPath $build) $Jobs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'AirPlay FFmpeg compilation failed.' }
}
finally { $env:MSYSTEM = $previousMsystem }
foreach ($library in @('libavcodec/avcodec-58.dll','libavutil/avutil-56.dll',
        'libswresample/swresample-3.dll','libswscale/swscale-5.dll')) {
    Copy-Item -LiteralPath (Join-Path $build $library) -Destination $runtime
}
Copy-Item -LiteralPath (Join-Path $source 'COPYING.LGPLv2.1') -Destination $runtime
$notices = [Text.StringBuilder]::new()
foreach ($dependency in @('libgcc','winpthreads','headers','mingw-w64-libraries')) {
    $licenseRoot = Join-Path $msys "ucrt64\share\licenses\$dependency"
    $texts = @(Get-ChildItem -LiteralPath $licenseRoot -File -Recurse)
    if (-not $texts.Count) { throw "Missing AirPlay FFmpeg build dependency license: $dependency" }
    foreach ($text in $texts) {
        [void]$notices.AppendLine("--- $dependency / $($text.Name) ---")
        [void]$notices.AppendLine([IO.File]::ReadAllText($text.FullName))
    }
}
[IO.File]::WriteAllText((Join-Path $runtime 'NOTICE-FFMPEG-BUILD.txt'),
    $notices.ToString(), [Text.UTF8Encoding]::new($false))
$compilerVersion = & (Join-Path $msys 'ucrt64\bin\gcc.exe') --version | Select-Object -First 1
$record = [ordered]@{
    Commit = $commit
    SourceUrl = "https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/$commit"
    SourceSha256 = $sourceHash
    RecipeSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'ffmpeg-airplay-build.sh')).Hash
    PatchSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'ffmpeg-airplay-mathops.patch')).Hash
    Compiler = $compilerVersion
    SourceDirectory = $source
    BuildDirectory = $build
    RuntimeDirectory = $runtime
    Files = @(Get-ChildItem $runtime -Filter '*.dll' | ForEach-Object {
        [ordered]@{ Name = $_.Name; Size = $_.Length; Sha256 = (Get-FileHash $_.FullName).Hash }
    })
}
[IO.File]::WriteAllText((Join-Path $stage 'build-record.json'),
    ($record | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output (Join-Path $stage 'build-record.json')
