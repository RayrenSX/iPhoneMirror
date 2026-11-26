# Bind optional-component metadata to both published app variants. In particular,
# -SkipBuild must not combine a fresh descriptor with stale executables.
function Get-CompactBuildInputs([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg,
    [string]$BuildRoot = (Join-Path $Workspace 'outputs')) {
    $paths = @('outputs\iPhoneMirror\iPhoneMirror.exe',
        'outputs\iPhoneMirror\iPhoneMirror.Driver.exe',
        'outputs\iPhoneMirror.Installer\iPhoneMirror.exe',
        'outputs\iPhoneMirror.Installer\iPhoneMirror.Driver.exe',
        'outputs\iPhoneMirror.Installer\iPhoneMirror.dll')
    $paths += 'config\uxplay-component.json'
    if ($WithFfmpeg) {
        $paths += @('src\App\native\components\ffmpeg.sha256',
            'outputs\iPhoneMirror\tools\ffmpeg\ffmpeg.exe',
            'outputs\iPhoneMirror.Installer\tools\ffmpeg\ffmpeg.exe')
    }
    foreach ($variant in @('iPhoneMirror', 'iPhoneMirror.Installer')) {
        if (Test-Path -LiteralPath (Join-Path $BuildRoot "$variant\tools\CoreDevice")) {
            $paths += @('iPhoneMirror.MediaDecoder.exe', 'LICENSE.FFmpeg', 'LICENSE.rplayhub') |
                ForEach-Object { "outputs\$variant\tools\CoreDevice\$_" }
        }
    }
    $records = [ordered]@{}
    foreach ($path in $paths) {
        $inputFile = if ($path.StartsWith('outputs\')) {
            Join-Path $BuildRoot $path.Substring('outputs\'.Length)
        } else { Join-Path $Workspace $path }
        $records[$path] = (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash
    }
    return $records
}

function Get-CompactBuildRecordPath([string]$Workspace, [string]$BuildRoot) {
    if ($BuildRoot -eq (Join-Path $Workspace 'outputs')) {
        return Join-Path $Workspace 'work\publish\compact-build.json'
    }
    return Join-Path $BuildRoot 'compact-build.json'
}

function Write-CompactBuildRecord([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg,
    [string]$BuildRoot = (Join-Path $Workspace 'outputs')) {
    $records = Get-CompactBuildInputs $Workspace $WithUxPlay $WithFfmpeg $BuildRoot
    $path = Get-CompactBuildRecordPath $Workspace $BuildRoot
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, ($records | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

function Assert-CompactBuildRecord([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg,
    [string]$BuildRoot = (Join-Path $Workspace 'outputs')) {
    $path = Get-CompactBuildRecordPath $Workspace $BuildRoot
    if (-not (Test-Path -LiteralPath $path)) { throw 'Missing compact build record. Rebuild before packaging.' }
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $current = Get-CompactBuildInputs $Workspace $WithUxPlay $WithFfmpeg $BuildRoot
    foreach ($entry in $current.GetEnumerator()) {
        if ($record[$entry.Key] -ine $entry.Value) {
            throw "Published app/runtime metadata changed after building ($($entry.Key)). Rebuild before packaging."
        }
    }
    if ($record.Count -ne $current.Count) {
        throw 'Published app/runtime files were removed after building. Rebuild before packaging.'
    }
}
