[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputDirectory,
    [string]$DescriptorPath,
    [switch]$StandaloneRelease
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'outputs\components' }
if (-not $DescriptorPath) { $DescriptorPath = Join-Path $root 'config\uxplay-component.json' }
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\')
$required = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'uxplay-runtime-manifest.psd1')).Files
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $relative) -PathType Leaf)) {
        throw "UxPlay component is incomplete: $relative"
    }
}
if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint
}).Count) { throw 'UxPlay component contains a reparse point.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory,(Split-Path -Parent $DescriptorPath) | Out-Null
$name = "iPhoneMirror-UxPlay-v$Version-win-x64.zip"
$releaseTag = if ($StandaloneRelease) { "uxplay-v$Version" } else { "v$Version" }
$archivePath = Join-Path $OutputDirectory $name
$temporary = "$archivePath.$([Guid]::NewGuid().ToString('N')).tmp"
Add-Type -AssemblyName System.IO.Compression
$files = @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName)
$records = @()
try {
    $stream = [IO.File]::Create($temporary)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($source.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = $file.OpenRead()
            $outputStream = $entry.Open()
            try {
                # Keep one read-only handle open: writers cannot change the file
                # between ZIP creation and hashing the exact same input bytes.
                $length = $inputStream.Length
                $inputStream.CopyTo($outputStream)
                $inputStream.Position = 0
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = [BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose() }
            }
            finally { $inputStream.Dispose(); $outputStream.Dispose() }
            $records += [ordered]@{
                path = $relative
                size = $length
                sha256 = $hash
            }
        }
    }
    finally { $zip.Dispose(); $stream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archivePath -Force
    $descriptor = [ordered]@{
        schema = 1
        version = $Version
        name = $name
        release = $releaseTag
        url = "https://github.com/RayrenSX/iPhoneMirror/releases/download/$releaseTag/$name"
        size = (Get-Item -LiteralPath $archivePath).Length
        sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $records
    }
    [IO.File]::WriteAllText($DescriptorPath, ($descriptor | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host "UxPlay optional component: $archivePath"
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
