[CmdletBinding()]
param(
    [string]$Candidate = '127.0.0.1',
    [switch]$ExposeLan,
    [switch]$OpenBrowser
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ComposeFile = Join-Path $Root 'docker-compose.yml'

if (-not (Test-Path -LiteralPath $ComposeFile -PathType Leaf)) {
    throw "SRS compose file is missing: $ComposeFile"
}
if ([string]::IsNullOrWhiteSpace($Candidate) -or $Candidate -match '[\s"'']') {
    throw 'Candidate must be a single IPv4/IPv6 address or hostname without whitespace or quotes.'
}

$Docker = Get-Command docker.exe,docker -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $Docker) {
    throw 'Docker Desktop is required for the local SRS test environment. Install and start it, then rerun this script.'
}

& $Docker.Source version --format '{{.Client.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker is installed but its daemon is not available.'
}

$env:SRS_CANDIDATE = $Candidate
$env:SRS_BIND_ADDRESS = if ($ExposeLan) { '0.0.0.0' } else { '127.0.0.1' }
& $Docker.Source compose -f $ComposeFile up --detach
if ($LASTEXITCODE -ne 0) {
    throw "SRS container startup failed with exit code $LASTEXITCODE."
}

$testPage = 'http://127.0.0.1:8080/iphoneMirror-test/'
Write-Host 'SRS local test environment is running.' -ForegroundColor Green
Write-Host "Test page: $testPage"
Write-Host 'RTMP: rtmp://127.0.0.1/live/livestream'
Write-Host 'SRT:  srt://127.0.0.1:10080?streamid=#!::r=live/livestream,m=publish'
Write-Host 'WHIP: http://127.0.0.1:1985/rtc/v1/whip/?app=live&stream=livestream'
if ($OpenBrowser) { Start-Process $testPage }
