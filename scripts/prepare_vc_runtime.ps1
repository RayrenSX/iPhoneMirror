[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,
    [string[]]$AdditionalDestinationDirectories = @()
)

$ErrorActionPreference = 'Stop'
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ScriptRoot 'VcRuntimeVersion.ps1')
$RequiredRuntimeFiles = @(
    'msvcp140.dll',
    'vcruntime140.dll',
    'vcruntime140_1.dll'
)

function Add-RedistRoot([Collections.Generic.HashSet[string]]$Roots,
    [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $fullPath -PathType Container) -and
        -not $Roots.Contains($fullPath)) {
        [void]$Roots.Add($fullPath)
    }
}

function Find-VcRuntimeDirectory {
    $roots = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    Add-RedistRoot $roots $env:VCToolsRedistDir

    $vswhere = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installations = @(& $vswhere -products * -requires `
            Microsoft.VisualStudio.Component.VC.Redist.14.Latest `
            -property installationPath)
        foreach ($installation in $installations) {
            Add-RedistRoot $roots (Join-Path $installation 'VC\Redist\MSVC')
        }
    }

    foreach ($visualStudioRoot in @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio')
    )) {
        if (-not (Test-Path -LiteralPath $visualStudioRoot -PathType Container)) {
            continue
        }
        foreach ($majorDirectory in Get-ChildItem -LiteralPath $visualStudioRoot `
                -Directory -ErrorAction SilentlyContinue) {
            foreach ($instanceDirectory in Get-ChildItem `
                    -LiteralPath $majorDirectory.FullName -Directory `
                    -ErrorAction SilentlyContinue) {
                Add-RedistRoot $roots (Join-Path $instanceDirectory.FullName `
                    'VC\Redist\MSVC')
            }
        }
    }

    $candidates = foreach ($root in $roots) {
        $versionDirectories = @($root)
        $directX64 = Join-Path $root 'x64'
        if (-not (Test-Path -LiteralPath $directX64 -PathType Container)) {
            $versionDirectories = @(Get-ChildItem -LiteralPath $root -Directory `
                -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        }
        foreach ($versionDirectory in $versionDirectories) {
            $x64 = Join-Path $versionDirectory 'x64'
            if (-not (Test-Path -LiteralPath $x64 -PathType Container)) { continue }
            foreach ($crtDirectory in Get-ChildItem -LiteralPath $x64 -Directory `
                    -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue) {
                $missing = @($RequiredRuntimeFiles | Where-Object {
                    -not (Test-Path -LiteralPath (Join-Path $crtDirectory.FullName $_) `
                        -PathType Leaf)
                })
                if ($missing.Count -ne 0) { continue }
                $runtime = Get-Item -LiteralPath `
                    (Join-Path $crtDirectory.FullName 'vcruntime140.dll')
                [PSCustomObject]@{
                    Directory = $crtDirectory.FullName
                    Version = ConvertTo-VcRuntimeVersion $runtime.VersionInfo.FileVersion
                }
            }
        }
    }

    $selected = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $selected) {
        throw 'The x64 Microsoft Visual C++ runtime was not found in any Visual Studio redist directory.'
    }
    return $selected
}

$runtime = Find-VcRuntimeDirectory
$destinations = @($DestinationDirectory) + $AdditionalDestinationDirectories |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

foreach ($name in $RequiredRuntimeFiles) {
    $source = Join-Path $runtime.Directory $name
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Visual C++ runtime signature validation failed: $source ($($signature.Status))"
    }
    foreach ($destination in $destinations) {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        $target = Join-Path $destination $name
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            throw "Visual C++ runtime copy verification failed: $target"
        }
    }
}

Write-Output "Microsoft Visual C++ runtime $($runtime.Version) from $($runtime.Directory)"
