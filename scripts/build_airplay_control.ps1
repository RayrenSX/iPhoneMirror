[CmdletBinding()]
param(
    [string]$GoExecutable = 'go',
    [string]$Compiler = 'gcc',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'work/airplay-control' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = Join-Path (Resolve-Path $OutputDirectory).Path 'iPhoneMirror.AirPlayControl.dll'
$source = Join-Path $root 'third_party/airplay-control'
$go = (Get-Command $GoExecutable -ErrorAction Stop).Source
$cc = (Get-Command $Compiler -ErrorAction Stop).Source
$previousPath = $env:PATH; $previousCC = $env:CC; $previousCgo = $env:CGO_ENABLED
try {
    $env:PATH = (Split-Path $cc) + ';' + $env:PATH
    $env:CC = $cc; $env:CGO_ENABLED = '1'
    & $go -C $source test ./...
    if ($LASTEXITCODE) { throw 'AirPlay control tests failed' }
    & $go -C $source build -buildmode=c-shared -trimpath -o $output .
    if ($LASTEXITCODE) { throw 'AirPlay control build failed' }
    $record = [ordered]@{
        UpstreamCommit = 'f47a3ffcfa9458aac40562d73c314de1447df013'
        GoVersion = (& $go version)
        Compiler = (& $cc --version | Select-Object -First 1)
        BinarySha256 = (Get-FileHash $output).Hash.ToLowerInvariant()
        Sources = @(Get-ChildItem $source -File -Recurse | Sort-Object FullName | ForEach-Object {
            @{ Path = [IO.Path]::GetRelativePath($source,$_.FullName).Replace('\','/'); Sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
        })
    }
    $record | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'BUILD.json') -Encoding utf8
} finally { $env:PATH=$previousPath; $env:CC=$previousCC; $env:CGO_ENABLED=$previousCgo }
