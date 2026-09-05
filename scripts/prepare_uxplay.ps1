[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,
    [string]$MsysRoot,
    [string]$SourceRoot,
    [string]$DnsSdPath
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Commit = 'aec205d49302df8d4eb291b9e927ed428b2d0166'
$Repository = 'https://github.com/FDH2/UxPlay.git'
$Version = '1.74'
$FinalDestination = [IO.Path]::GetFullPath($Destination)

function Assert-SafeWorkspaceDirectory([string]$Path) {
    $workspace = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspace + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "UxPlay path is outside the workspace: $fullPath"
    }
    $current = [IO.DirectoryInfo]::new($fullPath)
    while ($null -ne $current -and $current.FullName.Length -ge $workspace.Length) {
        if ($current.Exists -and
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a UxPlay path containing a reparse point: $($current.FullName)"
        }
        if ([string]::Equals($current.FullName, $workspace,
                [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $current.Parent
    }
}

function Resolve-MsysRoot {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($MsysRoot)) { $candidates += $MsysRoot }
    if (-not [string]::IsNullOrWhiteSpace($env:IPHONE_MIRROR_MSYS2_ROOT)) {
        $candidates += $env:IPHONE_MIRROR_MSYS2_ROOT
    }
    # msys2/setup-msys2 exposes its installation through PATH rather than a
    # stable environment variable. Recover the root from the selected bash.
    $bash = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($null -ne $bash -and $bash.Source -match '\\usr\\bin\\bash\.exe$') {
        $candidates += Split-Path (Split-Path $bash.Source -Parent) -Parent
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -and
        (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
        $runnerBash = Get-ChildItem -LiteralPath $env:RUNNER_TEMP -Filter 'bash.exe' `
            -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\usr\\bin\\bash\.exe$' } |
            Select-Object -First 1
        if ($null -ne $runnerBash) {
            $candidates += Split-Path (Split-Path $runnerBash.FullName -Parent) -Parent
        }
    }
    $candidates += @(
        'C:\msys64',
        (Join-Path $Root 'work\dependencies\msys2-20260611\root\msys64'))
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $full = [IO.Path]::GetFullPath($candidate)
        if ((Test-Path -LiteralPath (Join-Path $full 'usr\bin\bash.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $full 'ucrt64\bin\cmake.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $full 'ucrt64\bin\ninja.exe') -PathType Leaf)) {
            return $full
        }
    }
    throw 'MSYS2 UCRT64 is required to build the optional UxPlay fallback. Install MSYS2 or set IPHONE_MIRROR_MSYS2_ROOT.'
}

function Invoke-Msys([string]$Msys, [string]$Command) {
    $oldMsystem = $env:MSYSTEM
    $oldChere = $env:CHERE_INVOKING
    try {
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        & (Join-Path $Msys 'usr\bin\bash.exe') -lc $Command
        if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $Command" }
    }
    finally {
        $env:MSYSTEM = $oldMsystem
        $env:CHERE_INVOKING = $oldChere
    }
}

function Get-MsysOutput([string]$Msys, [string]$Command) {
    $oldMsystem = $env:MSYSTEM
    $oldChere = $env:CHERE_INVOKING
    try {
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        $output = & (Join-Path $Msys 'usr\bin\bash.exe') -lc $Command
        if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $Command" }
        return ($output -join "`n").Trim()
    }
    finally {
        $env:MSYSTEM = $oldMsystem
        $env:CHERE_INVOKING = $oldChere
    }
}

function Get-MsysPath([string]$Msys, [string]$WindowsPath) {
    $oldMsystem = $env:MSYSTEM
    $oldChere = $env:CHERE_INVOKING
    try {
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        return (& (Join-Path $Msys 'usr\bin\bash.exe') -lc "cygpath -u '$WindowsPath'").Trim()
    }
    finally {
        $env:MSYSTEM = $oldMsystem
        $env:CHERE_INVOKING = $oldChere
    }
}

function Get-LddDependencies([string]$Msys, [string]$FilePath) {
    $unixPath = Get-MsysPath $Msys $FilePath
    $oldMsystem = $env:MSYSTEM
    $oldChere = $env:CHERE_INVOKING
    try {
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        $lines = & (Join-Path $Msys 'usr\bin\bash.exe') -lc "ldd '$unixPath'"
    }
    finally {
        $env:MSYSTEM = $oldMsystem
        $env:CHERE_INVOKING = $oldChere
    }
    foreach ($line in $lines) {
        if ($line -match '=>\s+(/ucrt64/(?:bin|lib)/[^\s]+\.dll)\s') {
            $name = [IO.Path]::GetFileName($Matches[1])
            $candidate = Join-Path $Msys ('ucrt64\' + ($Matches[1].Substring(8) -replace '/', '\'))
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                [PSCustomObject]@{ Name = $name; Path = $candidate }
            }
        }
    }
}

function Copy-DependencyClosure([string]$Msys, [string]$Executable,
    [string]$DestinationDirectory, [hashtable]$Seen) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Executable)
    while ($queue.Count -ne 0) {
        $current = $queue.Dequeue()
        foreach ($dependency in @(Get-LddDependencies $Msys $current)) {
            if ($Seen.ContainsKey($dependency.Name)) { continue }
            $Seen[$dependency.Name] = $dependency.Path
            Copy-Item -LiteralPath $dependency.Path -Destination (
                Join-Path $DestinationDirectory $dependency.Name) -Force
            $queue.Enqueue($dependency.Path)
        }
    }
}

Assert-SafeWorkspaceDirectory $Destination
$Msys = Resolve-MsysRoot
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $Root "work\dependencies\uxplay-$Commit"
}
Assert-SafeWorkspaceDirectory $SourceRoot
if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot '.git') -PathType Container)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SourceRoot) | Out-Null
    & git clone --filter=blob:none --no-checkout $Repository $SourceRoot
    if ($LASTEXITCODE -ne 0) { throw "Could not clone UxPlay: $LASTEXITCODE" }
}
$safeSource = (Resolve-Path -LiteralPath $SourceRoot).Path.Replace('\', '/')
$head = (& git -c "safe.directory=$safeSource" -C $SourceRoot rev-parse HEAD).Trim()
if ($head -ne $Commit -or
    -not (Test-Path -LiteralPath (Join-Path $SourceRoot 'CMakeLists.txt') -PathType Leaf)) {
    & git -c "safe.directory=$safeSource" -C $SourceRoot fetch --depth 1 origin $Commit
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch UxPlay commit $Commit" }
    & git -c "safe.directory=$safeSource" -C $SourceRoot checkout --detach $Commit
    if ($LASTEXITCODE -ne 0) { throw "Could not checkout UxPlay commit $Commit" }
}

$buildRoot = Join-Path $Root "work\dependencies\uxplay-build-$Commit"
Assert-SafeWorkspaceDirectory $buildRoot
if (Test-Path -LiteralPath $buildRoot) {
    $item = Get-Item -LiteralPath $buildRoot -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "UxPlay build directory is a reparse point: $buildRoot"
    }
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null

# Use UxPlay's bundled mdnsd implementation on Windows. The external Bonjour
# provider can collide with another AirPlay responder and return -65563 during
# service registration. The bundled implementation keeps registration local
# to this process and needs no system Bonjour SDK.
$sourceUnix = Get-MsysPath $Msys $SourceRoot
$buildUnix = Get-MsysPath $Msys $buildRoot
Invoke-Msys $Msys "cd '$buildUnix' && cmake -G Ninja '$sourceUnix' -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON -DUSE_DNS_SD=OFF && ninja"
$GStreamerVersion = Get-MsysOutput $Msys 'pkg-config --modversion gstreamer-1.0'
if ($GStreamerVersion -notmatch '^\d+(?:\.\d+){1,3}$') {
    throw "Unexpected GStreamer version: $GStreamerVersion"
}
$decoderInspection = Get-MsysOutput $Msys 'gst-inspect-1.0 avdec_h264'
foreach ($property in @('output-corrupt', 'discard-corrupted-frames',
        'automatic-request-sync-points')) {
    if ($decoderInspection.IndexOf($property, [StringComparison]::Ordinal) -lt 0) {
        throw "Bundled avdec_h264 does not support required recovery property: $property"
    }
}
$parserInspection = Get-MsysOutput $Msys 'gst-inspect-1.0 h264parse'
foreach ($property in @('config-interval', 'disable-passthrough')) {
    if ($parserInspection.IndexOf($property, [StringComparison]::Ordinal) -lt 0) {
        throw "Bundled h264parse does not support required recovery property: $property"
    }
}
$builtUxplay = Join-Path $buildRoot 'uxplay.exe'
if (-not (Test-Path -LiteralPath $builtUxplay -PathType Leaf)) {
    throw "UxPlay build did not produce uxplay.exe: $builtUxplay"
}

$destinationParent = Split-Path -Parent $FinalDestination
$destinationLeaf = Split-Path -Leaf $FinalDestination
Assert-SafeWorkspaceDirectory $FinalDestination
New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
if (Test-Path -LiteralPath $FinalDestination) {
    $destinationItem = Get-Item -LiteralPath $FinalDestination -Force
    if (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "UxPlay destination is a reparse point: $FinalDestination"
    }
}
$Destination = Join-Path $destinationParent (
    ".$destinationLeaf.staging-$PID-$([Guid]::NewGuid().ToString('N'))")
Assert-SafeWorkspaceDirectory $Destination
New-Item -ItemType Directory -Path $Destination | Out-Null
try {
$destinationBin = Join-Path $Destination 'bin'
$destinationPlugins = Join-Path $Destination 'lib\gstreamer-1.0'
New-Item -ItemType Directory -Force -Path $destinationBin | Out-Null
New-Item -ItemType Directory -Force -Path $destinationPlugins | Out-Null
Copy-Item -LiteralPath $builtUxplay -Destination (Join-Path $Destination 'uxplay.exe') -Force
$resolvedDnsSdPath = $DnsSdPath
if ([string]::IsNullOrWhiteSpace($resolvedDnsSdPath)) {
    $resolvedDnsSdPath = Join-Path $Root 'src\App\native\Wireless\dnssd.dll'
}
if (-not (Test-Path -LiteralPath $resolvedDnsSdPath -PathType Leaf)) {
    throw "Windows DNS-SD shim is missing: $resolvedDnsSdPath"
}
Copy-Item -LiteralPath $resolvedDnsSdPath -Destination (
    Join-Path $destinationBin 'dnssd.dll') -Force

$pluginNames = @(
    'libgstapp.dll', 'libgstcoreelements.dll',
    'libgstaudioconvert.dll', 'libgstaudioresample.dll',
    'libgstvideoconvertscale.dll', 'libgsty4m.dll', 'libgstvideoparsersbad.dll',
    'libgstlibav.dll')
$pluginRoot = Join-Path $Msys 'ucrt64\lib\gstreamer-1.0'
$seen = @{}
foreach ($pluginName in $pluginNames) {
    $plugin = Join-Path $pluginRoot $pluginName
    if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) {
        throw "Required UxPlay GStreamer plugin is missing: $pluginName"
    }
    Copy-Item -LiteralPath $plugin -Destination (Join-Path $destinationPlugins $pluginName) -Force
    Copy-DependencyClosure $Msys $plugin (Join-Path $Destination 'bin') $seen
}
Copy-DependencyClosure $Msys $builtUxplay (Join-Path $Destination 'bin') $seen

@"
UxPlay $Version Windows runtime

Project: $Repository
Pinned commit: $Commit
Build environment: MSYS2 UCRT64
GStreamer version: $GStreamerVersion
Service discovery: UxPlay bundled mdnsd implementation
GStreamer plugins: app, coreelements, audioconvert, audioresample,
videoconvertscale, y4m, videoparsersbad, libav
Video recovery: h264parse inserts SPS/PPS at every IDR; avdec_h264 discards
corrupted output and waits for the next synchronization point.
"@ | Set-Content -LiteralPath (Join-Path $Destination 'SOURCE.md') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $SourceRoot 'LICENSE') -Destination (
    Join-Path $Destination 'LICENSE') -Force

$runtimeFiles = @(Get-ChildItem -LiteralPath $Destination -Recurse -File)
if ($runtimeFiles.Count -eq 0) { throw 'Prepared UxPlay runtime is empty.' }
$backup = Join-Path $destinationParent (
    ".$destinationLeaf.backup-$PID-$([Guid]::NewGuid().ToString('N'))")
Assert-SafeWorkspaceDirectory $backup
$movedExisting = $false
try {
    if (Test-Path -LiteralPath $FinalDestination) {
        Move-Item -LiteralPath $FinalDestination -Destination $backup
        $movedExisting = $true
    }
    Move-Item -LiteralPath $Destination -Destination $FinalDestination
}
catch {
    if ($movedExisting -and -not (Test-Path -LiteralPath $FinalDestination) -and
        (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $FinalDestination
    }
    throw
}
if ($movedExisting -and (Test-Path -LiteralPath $backup)) {
    Remove-Item -LiteralPath $backup -Recurse -Force
}
Write-Host "Prepared UxPlay $Version runtime in $FinalDestination" -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    throw
}
