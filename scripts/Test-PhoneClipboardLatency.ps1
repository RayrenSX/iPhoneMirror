param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]{1,64}$')][string]$Id,
    [Parameter(Mandatory)][string]$ExpectedFile,
    [string]$KeyReportsFile,
    [ValidateRange(0,1)][double]$CopyX = 0.12,
    [ValidateRange(0,1)][double]$CopyY = 0.115,
    [ValidateRange(1,60)][int]$TimeoutSeconds = 25
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$expected = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ExpectedFile))
$commandPath = Join-Path $OutputDirectory 'command.json'
$pendingPath = Join-Path $OutputDirectory ($Id + '.pending')
$resultPath = Join-Path $OutputDirectory ($Id + '-result.json')
if ((Test-Path -LiteralPath $commandPath) -or (Test-Path -LiteralPath $resultPath)) {
    throw 'A command or result already exists; wait for the command or use a new test id.'
}
$commandJson = if ($KeyReportsFile) {
    # Windows PowerShell 5.1 can serialize nested PS arrays as objects. Keep
    # the validated report JSON intact, including its empty key-up report.
    $reportsJson = [IO.File]::ReadAllText([IO.Path]::GetFullPath($KeyReportsFile))
    $null = $reportsJson | ConvertFrom-Json
    '{"action":"keys","id":"' + $Id + '","reports":' + $reportsJson + '}'
} else {
    @{action='tap';id=$Id;x=$CopyX;y=$CopyY;holdMs=80} | ConvertTo-Json -Depth 8
}
Set-Clipboard -Value ('windows-sentinel-' + $Id)
[IO.File]::WriteAllText($pendingPath,$commandJson,[Text.UTF8Encoding]::new($false))
$started = Get-Date
$clock = [Diagnostics.Stopwatch]::StartNew()
Move-Item -LiteralPath $pendingPath -Destination $commandPath
$matched = $false
$actualLength = 0
do {
    $actual = Get-Clipboard -Raw
    $actualLength = if ($null -eq $actual) { 0 } else { $actual.Length }
    $matched = [string]::Equals($actual,$expected,[StringComparison]::Ordinal)
    if ($matched) { break }
    Start-Sleep -Milliseconds 100
} while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
$clock.Stop()
$result = [ordered]@{
    expected=$expected
    actual=if ($matched) {$expected} else {$null}
    actualLength=$actualLength
    matched=$matched
    seconds=$clock.Elapsed.TotalSeconds
    clock='before dispatch of native Copy action through production HID'
    started=$started.ToString('o')
    ended=(Get-Date).ToString('o')
}
$json = $result | ConvertTo-Json
[IO.File]::WriteAllText($resultPath,$json,[Text.UTF8Encoding]::new($false))
$json
