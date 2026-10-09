[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$manifest = Get-Content (Join-Path $PSScriptRoot 'airplay-upstream-125.json') -Raw | ConvertFrom-Json
$utf8 = [Text.UTF8Encoding]::new($false)
function Get-TextHash([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($text)))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$files = @()
foreach ($entry in $manifest) {
    $path = Join-Path $source $entry.Path
    $bytes = [IO.File]::ReadAllBytes($path)
    $encoding = if ($bytes[0] -eq 255 -and $bytes[1] -eq 254) { [Text.Encoding]::Unicode } else { [Text.UTF8Encoding]::new($true) }
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $hash = Get-TextHash $text
    if ($hash -ne $entry.Base -and $hash -ne $entry.Target) {
        throw "AirPlay v1.2.5 patch source changed: $($entry.Path)"
    }
    $files += @{ Path = $path; Text = $text; Bytes = $bytes; Encoding = $encoding; Hash = $hash; Entry = $entry }
}
if (@($files | Where-Object { $_.Hash -ne $_.Entry.Target }).Count -eq 0) { return }
if (@($files | Where-Object { $_.Hash -ne $_.Entry.Base }).Count -ne 0) { throw 'AirPlay v1.2.5 patch is only partly applied.' }
$complete = $false
try {
    # Upstream mixes UTF-16 and UTF-8 source. Apply the reviewable diff to
    # normalized UTF-8, then restore the compiler's original source encodings.
    foreach ($file in $files) { [IO.File]::WriteAllText($file.Path, $file.Text, $utf8) }
    $patch = Join-Path $PSScriptRoot 'airplay-upstream-125.patch'
    & git apply --check --unsafe-paths --directory=$($source.Replace('\','/')) $patch
    if ($LASTEXITCODE) { throw 'AirPlay v1.2.5 patch check failed.' }
    & git apply --unsafe-paths --directory=$($source.Replace('\','/')) $patch
    if ($LASTEXITCODE) { throw 'AirPlay v1.2.5 patch application failed.' }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.Path).Replace("`r`n", "`n")
        if ((Get-TextHash $text) -ne $file.Entry.Target) { throw "AirPlay patch verification failed: $($file.Entry.Path)" }
        [IO.File]::WriteAllText($file.Path, $text, $file.Encoding)
    }
    $complete = $true
}
finally {
    if (-not $complete) { foreach ($file in $files) { [IO.File]::WriteAllBytes($file.Path, $file.Bytes) } }
}
