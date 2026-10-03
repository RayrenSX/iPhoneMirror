# Bind optional-component metadata to both published app variants. In particular,
# -SkipBuild must not combine a fresh descriptor with stale executables.
function Get-CompactBuildInputs([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg) {
    $paths = @('outputs\iPhoneMirror\iPhoneMirror.exe',
        'outputs\iPhoneMirror.Installer\iPhoneMirror.exe',
        'outputs\iPhoneMirror.Installer\iPhoneMirror.dll')
    if ($WithUxPlay) { $paths += 'src\App\native\components\uxplay.json' }
    if ($WithFfmpeg) {
        $paths += @('src\App\native\components\ffmpeg.sha256',
            'outputs\iPhoneMirror\tools\ffmpeg\ffmpeg.exe',
            'outputs\iPhoneMirror.Installer\tools\ffmpeg\ffmpeg.exe')
    }
    $records = [ordered]@{}
    foreach ($path in $paths) {
        $records[$path] = (Get-FileHash -LiteralPath (Join-Path $Workspace $path) -Algorithm SHA256).Hash
    }
    return $records
}

function Write-CompactBuildRecord([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg) {
    $records = Get-CompactBuildInputs $Workspace $WithUxPlay $WithFfmpeg
    $path = Join-Path $Workspace 'work\publish\compact-build.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, ($records | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

function Assert-CompactBuildRecord([string]$Workspace, [bool]$WithUxPlay, [bool]$WithFfmpeg) {
    $path = Join-Path $Workspace 'work\publish\compact-build.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'Missing compact build record. Rebuild before packaging.' }
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $current = Get-CompactBuildInputs $Workspace $WithUxPlay $WithFfmpeg
    foreach ($entry in $current.GetEnumerator()) {
        if ($record[$entry.Key] -ine $entry.Value) {
            throw "Published app/runtime metadata changed after building ($($entry.Key)). Rebuild before packaging."
        }
    }
}
