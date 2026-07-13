[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.6.0-preview.1',
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

    foreach ($requiredExecutable in @('iPhoneMirror.exe', 'iPhoneMirror.Driver.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $requiredExecutable))) {
            throw "Published executable is missing: $requiredExecutable. Run the Release build first."
        }
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
            -bc (Join-Path $Root 'src') `
            -pn iPhoneMirror `
            -pv $Version `
            -ps RayrenSX `
            -nsb 'https://github.com/RayrenSX/iPhoneMirror'
        if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed: $LASTEXITCODE" }

        $GeneratedSbom = Get-ChildItem -LiteralPath $PackageRoot -Recurse `
            -Filter 'manifest.spdx.json' | Select-Object -First 1
        if (-not $GeneratedSbom) { throw 'SBOM tool did not produce manifest.spdx.json.' }

        # Component Detector understands the .NET graph but cannot infer the
        # licenses of the native binaries deliberately vendored with this
        # Windows package. Add those aggregate components explicitly so the
        # machine-readable SBOM matches THIRD_PARTY_NOTICES.md.
        $Sbom = Get-Content -LiteralPath $GeneratedSbom.FullName -Raw | ConvertFrom-Json
        $RootPackage = $Sbom.packages | Where-Object { $_.SPDXID -eq 'SPDXRef-RootPackage' }
        if (-not $RootPackage) { throw 'Generated SBOM has no root package.' }
        $RootPackage.licenseDeclared =
            'MIT AND GPL-3.0-only AND LGPL-2.1-or-later AND LGPL-3.0-only'
        $RootPackage.licenseConcluded = 'NOASSERTION'
        $RootPackage.copyrightText = 'Copyright (c) 2026 RayrenSX and third-party contributors'

        $NativePackages = @(
            [PSCustomObject][ordered]@{
                name = 'libusb'
                SPDXID = 'SPDXRef-Package-libusb-1.0.29'
                downloadLocation = 'https://github.com/libusb/libusb/releases/tag/v1.0.29'
                filesAnalyzed = $false
                licenseConcluded = 'LGPL-2.1-or-later'
                licenseDeclared = 'LGPL-2.1-or-later'
                copyrightText = 'NOASSERTION'
                versionInfo = '1.0.29'
                supplier = 'Organization: libusb project'
                externalRefs = @([PSCustomObject][ordered]@{
                    referenceCategory = 'PACKAGE-MANAGER'
                    referenceType = 'purl'
                    referenceLocator = 'pkg:github/libusb/libusb@v1.0.29'
                })
            },
            [PSCustomObject][ordered]@{
                name = 'libusb-win32 import library'
                SPDXID = 'SPDXRef-Package-libusb-win32-library-1.2.6.0'
                downloadLocation = 'https://sourceforge.net/projects/libusb-win32/files/libusb-win32-releases/1.2.6.0/'
                filesAnalyzed = $false
                licenseConcluded = 'LGPL-3.0-only'
                licenseDeclared = 'LGPL-3.0-only'
                copyrightText = 'NOASSERTION'
                versionInfo = '1.2.6.0'
                supplier = 'Organization: libusb-win32 project'
            },
            [PSCustomObject][ordered]@{
                name = 'AirPlayServer wireless receiver'
                SPDXID = 'SPDXRef-Package-AirPlayServer-1.1.0'
                downloadLocation = 'https://github.com/xenos1337/AirPlayServer/releases/tag/v1.1.0'
                filesAnalyzed = $false
                licenseConcluded = 'GPL-3.0-only'
                licenseDeclared = 'GPL-3.0-only'
                copyrightText = 'Copyright (c) 2025 xenos1337 and upstream contributors'
                versionInfo = '1.1.0'
                supplier = 'Person: xenos1337'
                externalRefs = @([PSCustomObject][ordered]@{
                    referenceCategory = 'PACKAGE-MANAGER'
                    referenceType = 'purl'
                    referenceLocator = 'pkg:github/xenos1337/AirPlayServer@v1.1.0'
                })
            },
            [PSCustomObject][ordered]@{
                name = 'FFmpeg H.264 runtime'
                SPDXID = 'SPDXRef-Package-FFmpeg-4.4.2'
                downloadLocation = 'https://github.com/FFmpeg/FFmpeg/releases/tag/n4.4.2'
                filesAnalyzed = $false
                licenseConcluded = 'LGPL-2.1-or-later'
                licenseDeclared = 'LGPL-2.1-or-later'
                copyrightText = 'NOASSERTION'
                versionInfo = '4.4.2'
                supplier = 'Organization: FFmpeg project'
            }
        )
        foreach ($NativePackage in $NativePackages) {
            if (-not ($Sbom.packages | Where-Object { $_.SPDXID -eq $NativePackage.SPDXID })) {
                $Sbom.packages += $NativePackage
                $Sbom.relationships += [PSCustomObject][ordered]@{
                    relationshipType = 'DEPENDS_ON'
                    relatedSpdxElement = $NativePackage.SPDXID
                    spdxElementId = 'SPDXRef-RootPackage'
                }
            }
        }

        [IO.File]::WriteAllText($GeneratedSbom.FullName,
            ($Sbom | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
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
