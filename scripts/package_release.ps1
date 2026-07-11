[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.3.0-preview.1',
    [switch]$SkipBuild,
    [switch]$GenerateSbom
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$PublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
$ReleaseRoot = Join-Path $Root 'outputs\releases'
$StagingRoot = Join-Path $Root 'outputs\release-staging'
$PackageName = "iPhoneMirror-v$Version-win-x64"
$PackageRoot = Join-Path $StagingRoot $PackageName
$ArchivePath = Join-Path $ReleaseRoot "$PackageName.zip"
$SbomAsset = Join-Path $ReleaseRoot "$PackageName-sbom.spdx.json"
$ChecksumPath = Join-Path $ReleaseRoot 'SHA256SUMS.txt'

Push-Location $Root
try {
    if (-not $SkipBuild) {
        & (Join-Path $Root 'build.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot 'iPhoneMirror.exe'))) {
        throw 'Published application is missing. Run the Release build first.'
    }

    foreach ($path in @($StagingRoot, $ReleaseRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        New-Item -ItemType Directory -Path $path | Out-Null
    }

    Copy-Item -LiteralPath $PublishRoot -Destination $PackageRoot -Recurse

    $forbidden = Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object {
        $_.Extension -in @('.log', '.dmp', '.mdmp', '.pcap', '.pcapng', '.pdb') -or
        $_.Name -eq 'InstallIPhoneFilter.ps1' -or
        $_.Name -eq 'UsbDkHelper.dll'
    }
    if ($forbidden) {
        throw "Forbidden files in release package: $($forbidden.FullName -join ', ')"
    }

    if ($GenerateSbom) {
        $SbomTool = Get-Command sbom-tool,sbom -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $SbomToolPath = $SbomTool.Source
        if (-not $SbomToolPath) {
            $SbomToolPath = Get-ChildItem `
                -LiteralPath "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" `
                -Recurse -File -Filter 'sbom.exe' -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $SbomToolPath) {
            throw 'Microsoft SBOM Tool is not installed or not available on PATH.'
        }

        & $SbomToolPath generate `
            -b $PackageRoot `
            -bc $Root `
            -pn iPhoneMirror `
            -pv $Version `
            -ps RayrenSX `
            -nsb 'https://github.com/RayrenSX/iPhoneMirror'
        if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed: $LASTEXITCODE" }

        $GeneratedSbom = Get-ChildItem -LiteralPath $PackageRoot -Recurse `
            -Filter 'manifest.spdx.json' | Select-Object -First 1
        if (-not $GeneratedSbom) { throw 'SBOM tool did not produce manifest.spdx.json.' }
        Copy-Item -LiteralPath $GeneratedSbom.FullName -Destination $SbomAsset -Force
    }

    Compress-Archive -LiteralPath $PackageRoot -DestinationPath $ArchivePath `
        -CompressionLevel Optimal

    $assets = @($ArchivePath)
    if (Test-Path -LiteralPath $SbomAsset) { $assets += $SbomAsset }
    $checksumLines = foreach ($asset in $assets) {
        $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($asset))"
    }
    [IO.File]::WriteAllText($ChecksumPath,
        ($checksumLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

    Write-Host "Release package: $ArchivePath" -ForegroundColor Green
    Write-Host "Checksums:      $ChecksumPath" -ForegroundColor Green
    if (Test-Path -LiteralPath $SbomAsset) {
        Write-Host "SBOM:           $SbomAsset" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
