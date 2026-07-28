[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ComposeFile = Join-Path $Root 'docker-compose.yml'
$Docker = Get-Command docker.exe,docker -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $Docker) {
    throw 'Docker Desktop is required to stop the local SRS test environment.'
}

& $Docker.Source compose -f $ComposeFile down
if ($LASTEXITCODE -ne 0) {
    throw "SRS container shutdown failed with exit code $LASTEXITCODE."
}
