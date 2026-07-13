[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$SourceFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_handlers.h'
if (-not (Test-Path -LiteralPath $SourceFile)) {
    throw "AirPlayServer handshake source is missing: $SourceFile"
}

$Encoding = [Text.Encoding]::Unicode
$Text = [IO.File]::ReadAllText($SourceFile, $Encoding)
$ArrayStart = $Text.IndexOf('char info[] = {', [StringComparison]::Ordinal)
if ($ArrayStart -lt 0) {
    throw 'AirPlayServer display capability array is missing.'
}
$ArrayEnd = $Text.IndexOf("`n`t};", $ArrayStart, [StringComparison]::Ordinal)
if ($ArrayEnd -lt 0) {
    throw 'AirPlayServer display capability array terminator is missing.'
}
$ArrayEnd += 4
$Capability = $Text.Substring($ArrayStart, $ArrayEnd - $ArrayStart)

$SourceValues = [ordered]@{
    '0x11, 0x05, 0xa0' = '0x11, 0x0b, 0x40' # 1440 -> 2880
    '0x10, 0x1e'       = '0x10, 0x3c'       # 30 -> 60 (shared by maxFPS/refreshRate)
    '0x11, 0x0d, 0x70' = '0x11, 0x14, 0x00' # 3440 -> 5120
}

$AlreadyPatched = $true
foreach ($Target in $SourceValues.Values) {
    if ($Capability.IndexOf($Target, [StringComparison]::Ordinal) -lt 0) {
        $AlreadyPatched = $false
        break
    }
}
if ($AlreadyPatched) {
    Write-Host 'AirPlay 5120x2880@60 display capability patch is already applied.'
    return
}

foreach ($Pair in $SourceValues.GetEnumerator()) {
    $Matches = [regex]::Matches($Capability, [regex]::Escape($Pair.Key)).Count
    if ($Matches -ne 1) {
        throw "AirPlayServer display capability no longer matches '$($Pair.Key)' exactly once (found $Matches)."
    }
    $Capability = $Capability.Replace($Pair.Key, $Pair.Value)
}

$Patched = $Text.Substring(0, $ArrayStart) + $Capability + $Text.Substring($ArrayEnd)
$Patched = $Patched.Replace(
    '// Binary plist with display capabilities: 1920x1080 @ 30fps (maxFPS=30, refreshRate=30)',
    '// Binary plist with display capabilities: 5120x2880 @ 60fps (maxFPS=60, refreshRate=60)')
[IO.File]::WriteAllText($SourceFile, $Patched, $Encoding)
Write-Host 'Applied AirPlay 5120x2880@60 display capability patch.'
