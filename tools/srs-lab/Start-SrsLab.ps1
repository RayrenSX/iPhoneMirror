[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$DashboardPort = 8090,
    [ValidateSet('Auto', 'Srs', 'MediaMtx')]
    [string]$Backend = 'Auto',
    [switch]$DashboardOnly
)

$ErrorActionPreference = 'Stop'
$LabRoot = $PSScriptRoot
$ComposeFile = Join-Path $LabRoot 'docker-compose.yml'
$ServerFile = Join-Path $LabRoot 'server.mjs'
$MediaMtxConfig = Join-Path $LabRoot 'mediamtx.yml'
$MediaMtxVersion = 'v1.19.3'
$MediaMtxArchiveName = "mediamtx_$($MediaMtxVersion)_windows_amd64.zip"
$MediaMtxArchiveHash = '5d82148d1032a6a190d9909a2997d9989457aaadf49af87dd02cd4512d31bebe'
$RuntimeRoot = Join-Path $LabRoot '.runtime'
$MediaMtxRoot = Join-Path $RuntimeRoot "mediamtx-$MediaMtxVersion"
$MediaMtxExecutable = Join-Path $MediaMtxRoot 'mediamtx.exe'

function Test-HttpReady([string]$Uri) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 2
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    } catch { return $false }
}

function Test-DockerReady {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) { return $false }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $docker.Source info 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    } catch { return $false }
    finally { $ErrorActionPreference = $previousPreference }
}

function Install-MediaMtxRuntime {
    if (Test-Path -LiteralPath $MediaMtxExecutable) { return }
    New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $MediaMtxRoot -Force | Out-Null
    $archive = Join-Path $RuntimeRoot $MediaMtxArchiveName
    $download = "https://github.com/bluenviron/mediamtx/releases/download/$MediaMtxVersion/$MediaMtxArchiveName"
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading MediaMTX $MediaMtxVersion Windows runtime..."
        Invoke-WebRequest -Headers @{ 'User-Agent' = 'iPhoneMirror-Lab' } `
            -Uri $download -OutFile $archive
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actualHash -ne $MediaMtxArchiveHash) {
        Remove-Item -LiteralPath $archive -Force
        throw "MediaMTX archive checksum mismatch. Expected $MediaMtxArchiveHash, got $actualHash."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $MediaMtxRoot -Force
    if (-not (Test-Path -LiteralPath $MediaMtxExecutable)) {
        throw 'MediaMTX runtime extraction did not produce mediamtx.exe.'
    }
}

function Start-SrsBackend {
    $docker = (Get-Command docker -ErrorAction Stop).Source
    & $docker compose -f $ComposeFile up -d
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start the SRS container (exit code $LASTEXITCODE)."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-HttpReady 'http://127.0.0.1:1985/api/v1/versions') { return }
        Start-Sleep -Milliseconds 500
    }
    & $docker compose -f $ComposeFile logs --tail 80
    throw 'SRS did not become ready on http://127.0.0.1:1985 within 30 seconds.'
}

function Start-MediaMtxBackend {
    if (Test-HttpReady 'http://127.0.0.1:9997/v3/paths/list') { return }
    Install-MediaMtxRuntime
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $MediaMtxExecutable
    $start.Arguments = '"' + $MediaMtxConfig + '"'
    $start.WorkingDirectory = $MediaMtxRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    [Diagnostics.Process]::Start($start) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-HttpReady 'http://127.0.0.1:9997/v3/paths/list') { return }
        Start-Sleep -Milliseconds 250
    }
    throw 'MediaMTX did not become ready on http://127.0.0.1:9997 within 15 seconds.'
}

$activeBackend = 'None'
if (-not $DashboardOnly) {
    $dockerReady = Test-DockerReady
    if ($Backend -eq 'Srs' -and -not $dockerReady) {
        throw 'The SRS backend requires a running Docker engine. Use -Backend MediaMtx or the default Auto mode on this machine.'
    }
    if ($Backend -eq 'Srs' -or ($Backend -eq 'Auto' -and $dockerReady)) {
        Start-SrsBackend
        $activeBackend = 'SRS'
    } else {
        Start-MediaMtxBackend
        $activeBackend = 'MediaMTX'
    }
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js 18 or later is required for the local Stream Lab dashboard.'
}

if (-not (Test-HttpReady "http://127.0.0.1:$DashboardPort/api/status")) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command node -ErrorAction Stop).Source
    $start.Arguments = '"' + $ServerFile + '"'
    $start.WorkingDirectory = $LabRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['SRS_LAB_PORT'] = $DashboardPort.ToString()
    [Diagnostics.Process]::Start($start) | Out-Null
    Start-Sleep -Milliseconds 350
}

if (-not (Test-HttpReady "http://127.0.0.1:$DashboardPort/api/status")) {
    throw "The Stream Lab dashboard did not start on port $DashboardPort."
}

$status = Invoke-RestMethod -Uri "http://127.0.0.1:$DashboardPort/api/status" -TimeoutSec 3
Write-Host "Stream Lab: http://127.0.0.1:$DashboardPort"
Write-Host "Backend:   $activeBackend"
if ($status.ready) {
    Write-Host "RTMP: $($status.endpoints.rtmp)"
    Write-Host "SRT:  $($status.endpoints.srt)"
    Write-Host "WHIP: $($status.endpoints.whip)"
    Write-Host "WHEP: $($status.endpoints.whep)"
}
