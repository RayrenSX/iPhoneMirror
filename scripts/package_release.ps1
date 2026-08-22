[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$SkipBuild,
    [switch]$GenerateSbom,
    [switch]$UpdateReleaseManifest,
    [switch]$IncludeMediaOutputRuntime,
    [switch]$OmitMediaOutputRuntime,
    [string]$AppleSupportPackagePath,
    [switch]$ConfirmAppleRedistributionRights
)

$ErrorActionPreference = 'Stop'
if ($IncludeMediaOutputRuntime -and $OmitMediaOutputRuntime) {
    throw '-IncludeMediaOutputRuntime and -OmitMediaOutputRuntime cannot be used together.'
}
$UseMediaOutputRuntime = -not $OmitMediaOutputRuntime
if ($SkipBuild -and -not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
    throw '-AppleSupportPackagePath cannot be used with -SkipBuild.'
}
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Get-ProjectVersion([string]$ProjectPath) {
    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $versions = @($project.Project.PropertyGroup | ForEach-Object {
        if ($null -ne $_.Version -and
            -not [string]::IsNullOrWhiteSpace([string]$_.Version)) {
            ([string]$_.Version).Trim()
        }
    } | Select-Object -Unique)
    if ($versions.Count -ne 1) {
        throw "Project must declare exactly one Version: $ProjectPath"
    }
    return $versions[0]
}

$AppProjectPath = Join-Path $Root 'src\App\iPhoneMirror.App.csproj'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion $AppProjectPath
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid package version in ${AppProjectPath}: $Version"
}
$PublishRoot = Join-Path $Root 'outputs\iPhoneMirror'
$InstallerPublishRoot = Join-Path $Root 'outputs\iPhoneMirror.Installer'
$ReleaseRoot = Join-Path $Root 'outputs\releases'
$ReleaseManifestPath = Join-Path $Root 'updates\releases.json'
$StagingBase = Join-Path $Root 'outputs\release-staging'
$StagingRoot = Join-Path $StagingBase ([Guid]::NewGuid().ToString('N'))
$PackageName = "iPhoneMirror-v$Version-win-x64"
$PackageRoot = Join-Path $StagingRoot $PackageName
$ArchivePath = Join-Path $ReleaseRoot "$PackageName.zip"
$LegacyLatestArchivePath = Join-Path $ReleaseRoot "$PackageName-latest.zip"
$SbomAsset = Join-Path $ReleaseRoot "$PackageName-sbom.spdx.json"
$ChecksumPath = Join-Path $ReleaseRoot 'SHA256SUMS.txt'
$InstallerName = "iPhoneMirror-Setup-v$Version-x64.exe"
$InstallerPath = Join-Path $ReleaseRoot $InstallerName
$StagedArchive = Join-Path $StagingRoot "$PackageName.zip"
$StagedSbomAsset = Join-Path $StagingRoot "$PackageName-sbom.spdx.json"
$StagedChecksum = Join-Path $StagingRoot 'SHA256SUMS.txt'
$StagedInstaller = Join-Path $StagingRoot $InstallerName
$LegacyArchive = Join-Path $Root 'outputs\iPhoneMirror-video-app-discovery-fix.zip'
$PreserveStagingRoot = $false
$MediaOutputManifestPath = Join-Path $Root 'scripts\ffmpeg-runtime-manifest.psd1'
if (-not (Test-Path -LiteralPath $MediaOutputManifestPath -PathType Leaf)) {
    throw "Media-output FFmpeg manifest is missing: $MediaOutputManifestPath"
}
$MediaOutputManifest = Import-PowerShellDataFile -LiteralPath $MediaOutputManifestPath
$MediaOutputRuntimeHashes = [Collections.IDictionary]$MediaOutputManifest.Files

$RequiredArtifacts = @(
    'iPhoneMirror.exe',
    'iPhoneMirror.Core.dll',
    'iPhoneMirror.UsbConfigurationSwitch.exe',
    'iPhoneMirror.VirtualCamera.dll',
    'iPhoneMirror.VirtualCamera.Admin.exe',
    'iPhoneMirror.Driver.exe',
    'libusb-1.0.dll',
    'libusb0.dll',
    'msvcp140.dll',
    'vcruntime140.dll',
    'vcruntime140_1.dll',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'CHANGELOG.md',
    'DRIVER_DEPENDENCIES.md',
    'licenses\WPF-UI-LICENSE.md',
    'licenses\WPF-UI-ThirdPartyNotices.txt',
    'tools\updater\Apply-ZipUpdate.ps1',
    'licenses\libusb-COPYING.txt',
    'licenses\libusb-win32-COPYING-LGPL.txt',
    'Wireless\iPhoneMirror.WirelessHost.exe',
    'Wireless\msvcp140.dll',
    'Wireless\vcruntime140.dll',
    'Wireless\vcruntime140_1.dll',
    'Wireless\airplay2dll.dll',
    'Wireless\avcodec-58.dll',
    'Wireless\avutil-56.dll',
    'Wireless\dnssd.dll',
    'Wireless\swresample-3.dll',
    'Wireless\swscale-5.dll',
    'Wireless\licenses\LICENSE-FFMPEG-LGPL-2.1.txt',
    'Wireless\licenses\LICENSE-MIT.txt',
    'Wireless\licenses\LICENSE-PLAYFAIR-GPL-3.0.md',
    'Wireless\licenses\NOTICE-FDK-AAC.txt',
    'Wireless\licenses\SOURCE.md',
    'Wireless\licenses\SHA256SUMS.txt'
)

function Assert-ProductVersion([string]$Path, [string]$ExpectedVersion) {
    $actual = (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
    $semanticVersion = if ($null -eq $actual) { '' } else {
        ($actual -split '\+', 2)[0].Trim()
    }
    if (-not [string]::Equals($semanticVersion, $ExpectedVersion,
            [StringComparison]::Ordinal)) {
        throw "Embedded product version mismatch: $Path (expected $ExpectedVersion, found $actual)"
    }
}

function Write-Sha256Sidecar([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$Path.sha256"
    [IO.File]::WriteAllText($checksumPath, $hash, [Text.UTF8Encoding]::new($false))

    $writtenHash = [IO.File]::ReadAllText($checksumPath).Trim()
    if (-not [string]::Equals($writtenHash, $hash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Failed to write SHA256 sidecar: $checksumPath"
    }
}

function Format-JsonRecord([string]$Json) {
    $depth = 1
    @($Json -split "`r?`n" | ForEach-Object {
        $line = $_.Trim()
        if ($line.StartsWith('}') -or $line.StartsWith(']')) { --$depth }
        $formatted = ('  ' * $depth) + $line
        if ($line.EndsWith('{') -or $line.EndsWith('[')) { ++$depth }
        $formatted
    }) -join "`n"
}

function Update-ReleaseManifestAssets([string]$Path, [string]$ReleaseVersion,
    [object[]]$Assets) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Fallback release manifest is missing: $Path"
    }
    $json = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $parsed = $json | ConvertFrom-Json
    $releases = if ($parsed -is [array]) {
        @($parsed)
    }
    elseif ($parsed.PSObject.Properties.Name -contains 'value') {
        @($parsed.value)
    }
    else {
        @($parsed)
    }
    $tag = "v$ReleaseVersion"
    $matches = @($releases | Where-Object {
        [string]::Equals([string]$_.tag_name, $tag,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -gt 1) {
        throw "Fallback release manifest contains duplicate $tag entries."
    }
    if ($matches.Count -eq 0) {
        $changelogPath = Join-Path $Root 'CHANGELOG.md'
        $changelog = [IO.File]::ReadAllText($changelogPath, [Text.Encoding]::UTF8)
        $escapedVersion = [regex]::Escape($ReleaseVersion)
        $section = [regex]::Match($changelog,
            "(?ms)^## \[$escapedVersion\] - [^\r\n]+\r?\n(?<body>.*?)(?=^## \[|\z)")
        if (-not $section.Success) {
            throw "CHANGELOG.md has no release section for $ReleaseVersion."
        }
        $release = [PSCustomObject][ordered]@{
            tag_name = $tag
            name = "iPhoneMirror $tag"
            body = $section.Groups['body'].Value.Trim()
            published_at = [DateTimeOffset]::UtcNow.ToString(
                'yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
            draft = $false
            prerelease = $ReleaseVersion.Contains('-')
            assets = @()
        }
        $releases = @($release) + @($releases)
        $matches = @($release)
    }

    $manifestAssets = @($Assets | ForEach-Object {
        $assetPath = [IO.Path]::GetFullPath([string]$_.Final)
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Published release asset is missing: $assetPath"
        }
        $item = Get-Item -LiteralPath $assetPath
        $name = [IO.Path]::GetFileName($assetPath)
        $hash = ((Get-FileHash -LiteralPath $assetPath `
            -Algorithm SHA256).Hash).ToLowerInvariant()
        [PSCustomObject][ordered]@{
            name = $name
            size = $item.Length
            digest = "sha256:$hash"
            browser_download_url =
                "https://github.com/RayrenSX/iPhoneMirror/releases/download/$tag/$name"
        }
    })
    $duplicateNames = @($manifestAssets | Group-Object name |
        Where-Object Count -ne 1)
    if ($duplicateNames.Count -ne 0) {
        throw "Published release asset names are not unique: $($duplicateNames.Name -join ', ')"
    }
    $matches[0].assets = $manifestAssets

    # Windows PowerShell 5.1 can serialize a one-item Object[] as a
    # value/Count wrapper. Serialize each record and assemble the root array
    # explicitly so ReleaseParser always receives a JSON array.
    $serializedEntries = @($releases | ForEach-Object {
        $entry = ConvertTo-Json -InputObject $_ -Depth 100
        Format-JsonRecord $entry
    })
    $serialized = "[`n" + ($serializedEntries -join ",`n") + "`n]"
    if (-not $serialized.TrimStart().StartsWith('[')) {
        throw 'Fallback release manifest root must be a JSON array.'
    }
    $roundTrip = $serialized | ConvertFrom-Json
    $roundTripMatches = @($roundTrip | Where-Object {
        [string]::Equals([string]$_.tag_name, $tag,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($roundTripMatches.Count -ne 1 -or
        @($roundTripMatches[0].assets).Count -ne $manifestAssets.Count) {
        throw 'Fallback release manifest failed round-trip validation.'
    }
    foreach ($expectedAsset in $manifestAssets) {
        $actualAssets = @($roundTripMatches[0].assets | Where-Object {
            [string]::Equals([string]$_.name, [string]$expectedAsset.name,
                [StringComparison]::Ordinal)
        })
        if ($actualAssets.Count -ne 1 -or
            [long]$actualAssets[0].size -ne [long]$expectedAsset.size -or
            -not [string]::Equals([string]$actualAssets[0].digest,
                [string]$expectedAsset.digest,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Fallback release manifest failed validation for $($expectedAsset.name)."
        }
    }
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $serialized + "`n",
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Assert-SafeWorkspaceDirectory([string]$Path) {
    $workspace = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspace + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory is outside the workspace: $fullPath"
    }
    $current = [IO.DirectoryInfo]::new($fullPath)
    while ($null -ne $current -and
        $current.FullName.Length -ge $workspace.Length) {
        if ($current.Exists -and
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a release directory containing a reparse point: $($current.FullName)"
        }
        if ([string]::Equals($current.FullName, $workspace,
                [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $current.Parent
    }
}

function Assert-PublishedOutput {
    Assert-SafeWorkspaceDirectory $PublishRoot
    if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) {
        throw "Published output is missing: $PublishRoot"
    }
    $reparseItems = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -Force |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
    if ($reparseItems.Count -ne 0) {
        throw "Published output contains a reparse point: $($reparseItems[0].FullName)"
    }
    foreach ($relative in $RequiredArtifacts) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $relative) -PathType Leaf)) {
            throw "Published artifact is missing: $relative"
        }
    }

    $fullPublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path.TrimEnd('\')
    $actualArtifacts = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File |
        ForEach-Object { $_.FullName.Substring($fullPublishRoot.Length + 1) })
    $optionalArtifacts = @('AppleMobileDeviceSupport64.msi')
    if ($UseMediaOutputRuntime) {
        $optionalArtifacts += @(
            'tools\ffmpeg\ffmpeg.exe',
            'tools\ffmpeg\LICENSE.txt',
            'tools\ffmpeg\README.txt',
            'tools\ffmpeg\SOURCE.txt'
        )
    }
    $allowedArtifacts = @($RequiredArtifacts) + $optionalArtifacts
    $unexpected = @($actualArtifacts | Where-Object { $_ -notin $allowedArtifacts })
    if ($unexpected.Count -ne 0) {
        throw "Unexpected files in published output: $($unexpected -join ', ')"
    }

    Assert-ProductVersion (Join-Path $PublishRoot 'iPhoneMirror.exe') $Version
    Assert-ProductVersion (Join-Path $PublishRoot 'iPhoneMirror.Driver.exe') $Version
    Assert-ProductVersion (Join-Path $PublishRoot 'iPhoneMirror.Core.dll') $Version

    $appleSupportPackage = Join-Path $PublishRoot 'AppleMobileDeviceSupport64.msi'
    if (Test-Path -LiteralPath $appleSupportPackage -PathType Leaf) {
        if (-not $ConfirmAppleRedistributionRights) {
            throw 'Published output contains Apple software without redistribution confirmation.'
        }
        . (Join-Path $Root 'scripts\AppleSupportPackage.ps1')
        [void](Assert-TrustedAppleSupportPackage $appleSupportPackage)
    }

    $wirelessManifest = Join-Path $PublishRoot 'Wireless\licenses\SHA256SUMS.txt'
    $entries = foreach ($line in Get-Content -LiteralPath $wirelessManifest) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') {
            throw "Invalid published wireless hash entry: $line"
        }
        [PSCustomObject]@{ Hash = $Matches[1]; Name = [IO.Path]::GetFileName($Matches[2]) }
    }
    if (@($entries).Count -eq 0) { throw 'Published wireless hash manifest is empty.' }
    $expectedWirelessBinaries = @(
        'airplay2dll.dll', 'avcodec-58.dll', 'avutil-56.dll',
        'dnssd.dll', 'swresample-3.dll', 'swscale-5.dll'
    )
    $manifestNames = @($entries | ForEach-Object { $_.Name } | Select-Object -Unique)
    $missingHashes = @($expectedWirelessBinaries | Where-Object { $_ -notin $manifestNames })
    $unexpectedHashes = @($manifestNames | Where-Object { $_ -notin $expectedWirelessBinaries })
    if ($missingHashes.Count -ne 0 -or $unexpectedHashes.Count -ne 0 -or
        @($entries).Count -ne $expectedWirelessBinaries.Count) {
        throw 'Published wireless hash manifest does not exactly cover the receiver binaries.'
    }
    foreach ($entry in $entries) {
        $binary = Join-Path (Join-Path $PublishRoot 'Wireless') $entry.Name
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw "Published wireless binary is missing: $($entry.Name)"
        }
        $actual = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
        if ($actual -ne $entry.Hash) {
            throw "Published wireless hash mismatch: $($entry.Name)"
        }
    }

    $sourceLicense = Join-Path $Root 'third_party\libusb\COPYING'
    $publishedLicense = Join-Path $PublishRoot 'licenses\libusb-COPYING.txt'
    if ((Get-FileHash -LiteralPath $sourceLicense -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $publishedLicense -Algorithm SHA256).Hash) {
        throw 'Published libusb license does not match the vendored license.'
    }

    $libUsb0Runtime = Join-Path $PublishRoot 'libusb0.dll'
    $expectedLibUsb0Hash =
        '4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14'
    if ((Get-FileHash -LiteralPath $libUsb0Runtime -Algorithm SHA256).Hash -ne
        $expectedLibUsb0Hash) {
        throw 'Published libusb0 runtime does not match the pinned upstream binary.'
    }
    if ((Get-AuthenticodeSignature -LiteralPath $libUsb0Runtime).Status -ne
        [System.Management.Automation.SignatureStatus]::Valid) {
        throw 'Published libusb0 runtime Authenticode signature is not valid.'
    }

    if ($UseMediaOutputRuntime) {
        foreach ($entry in $MediaOutputRuntimeHashes.GetEnumerator()) {
            $name = [string]$entry.Key
            $published = Join-Path (Join-Path $PublishRoot 'tools\ffmpeg') $name
            if (-not (Test-Path -LiteralPath $published -PathType Leaf)) {
                throw "Published media-output runtime is missing: $name"
            }
            $actual = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash
            if ($actual -ne [string]$entry.Value) {
                throw "Published media-output runtime hash mismatch for ${name}: expected $($entry.Value), got $actual"
            }
        }
        $sourceRecord = Get-Content -LiteralPath `
            (Join-Path $PublishRoot 'tools\ffmpeg\SOURCE.txt') -Raw
        if ($sourceRecord -notmatch [regex]::Escape([string]$MediaOutputManifest.Version) -or
            $sourceRecord -notmatch [regex]::Escape([string]$MediaOutputManifest.ArchiveSha256) -or
            $sourceRecord -notmatch [regex]::Escape([string]$MediaOutputManifest.DownloadUrl)) {
            throw 'Published media-output FFmpeg source record does not match the pinned manifest.'
        }
    }
}

function Publish-StagedAssets([object[]]$Assets, [string[]]$RemovePaths) {
    Assert-SafeWorkspaceDirectory $ReleaseRoot
    New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null
    $backupRoot = Join-Path $StagingRoot '_previous-release'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    # Existing assets remain recoverable until every staged copy verifies.
    $changes = [Collections.Generic.List[object]]::new()
    try {
        $index = 0
        foreach ($asset in $Assets) {
            if (Test-Path -LiteralPath $asset.Final -PathType Container) {
                throw "Release asset path is a directory: $($asset.Final)"
            }
            if (Test-Path -LiteralPath $asset.Final -PathType Leaf) {
                $finalItem = Get-Item -LiteralPath $asset.Final -Force
                if (($finalItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Release asset is a reparse point: $($asset.Final)"
                }
            }
            $backup = Join-Path $backupRoot "$index.bak"
            $hadOriginal = Test-Path -LiteralPath $asset.Final -PathType Leaf
            if ($hadOriginal) {
                Copy-Item -LiteralPath $asset.Final -Destination $backup -Force
            }
            $changes.Add([PSCustomObject]@{
                Final = $asset.Final; Backup = $backup; HadOriginal = $hadOriginal
            })
            Copy-Item -LiteralPath $asset.Staged -Destination $asset.Final -Force
            if ((Get-FileHash -LiteralPath $asset.Staged -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $asset.Final -Algorithm SHA256).Hash) {
                throw "Release asset copy verification failed: $($asset.Final)"
            }
            ++$index
        }
        foreach ($path in $RemovePaths) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Release cleanup path is not a file: $path"
            }
            $removeItem = Get-Item -LiteralPath $path -Force
            if (($removeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release cleanup path is a reparse point: $path"
            }
            $backup = Join-Path $backupRoot "$index.bak"
            Copy-Item -LiteralPath $path -Destination $backup -Force
            $changes.Add([PSCustomObject]@{
                Final = $path; Backup = $backup; HadOriginal = $true
            })
            Remove-Item -LiteralPath $path -Force
            ++$index
        }
    }
    catch {
        $publishError = $_
        $rollbackErrors = @()
        for ($index = $changes.Count - 1; $index -ge 0; --$index) {
            $change = $changes[$index]
            try {
                if ($change.HadOriginal) {
                    if (-not (Test-Path -LiteralPath $change.Backup -PathType Leaf)) {
                        throw "Release backup is missing: $($change.Backup)"
                    }
                    Copy-Item -LiteralPath $change.Backup `
                        -Destination $change.Final -Force
                }
                elseif (Test-Path -LiteralPath $change.Final -PathType Leaf) {
                    Remove-Item -LiteralPath $change.Final -Force
                }
            }
            catch {
                $rollbackErrors += $_.Exception.Message
            }
        }
        if ($rollbackErrors.Count -ne 0) {
            $script:PreserveStagingRoot = $true
            throw "Release publication failed: $($publishError.Exception.Message) " +
                "Rollback was incomplete: $($rollbackErrors -join '; '). " +
                "Recovery files were retained under $backupRoot."
        }
        throw $publishError
    }
}

Push-Location $Root
try {
    foreach ($project in @(
        'src\App\iPhoneMirror.App.csproj',
        'src\DriverInstaller\iPhoneMirror.DriverInstaller.csproj'
    )) {
        $projectVersion = Get-ProjectVersion (Join-Path $Root $project)
        if (-not [string]::Equals($projectVersion, $Version,
                [StringComparison]::Ordinal)) {
            throw "Package version $Version does not match $project version $projectVersion."
        }
    }

    if (-not $SkipBuild) {
        $buildArguments = @{ Configuration = 'Release' }
        if ($OmitMediaOutputRuntime) {
            $buildArguments.OmitMediaOutputRuntime = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($AppleSupportPackagePath)) {
            $buildArguments.AppleSupportPackagePath = $AppleSupportPackagePath
            $buildArguments.ConfirmAppleRedistributionRights =
                $ConfirmAppleRedistributionRights
        }
        & (Join-Path $Root 'build.ps1') @buildArguments
        if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }
    }

    Assert-PublishedOutput
    if (-not (Test-Path -LiteralPath $InstallerPublishRoot -PathType Container)) {
        throw "Shared-runtime installer output is missing: $InstallerPublishRoot"
    }
    $installerRequiredArtifacts = @(
        'iPhoneMirror.exe', 'iPhoneMirror.dll', 'iPhoneMirror.deps.json',
        'iPhoneMirror.runtimeconfig.json', 'iPhoneMirror.Driver.exe',
        'iPhoneMirror.UsbConfigurationSwitch.exe',
        'iPhoneMirror.Driver.dll', 'iPhoneMirror.Driver.deps.json',
        'iPhoneMirror.Driver.runtimeconfig.json', 'hostfxr.dll',
        'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
        'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll'
    )
    if ($UseMediaOutputRuntime) {
        $installerRequiredArtifacts += @(
            'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE.txt',
            'tools\ffmpeg\README.txt', 'tools\ffmpeg\SOURCE.txt'
        )
    }
    foreach ($required in $installerRequiredArtifacts) {
        if (-not (Test-Path -LiteralPath `
                (Join-Path $InstallerPublishRoot $required) -PathType Leaf)) {
            throw "Shared-runtime installer artifact is missing: $required"
        }
    }
    $versionedDac = @(Get-ChildItem -LiteralPath $InstallerPublishRoot `
        -Filter 'mscordaccore_amd64_amd64_*.dll' -File)
    if ($versionedDac.Count -ne 1) {
        throw 'Shared-runtime installer must contain exactly one versioned .NET DAC.'
    }
    if (-not $UseMediaOutputRuntime) {
        $installerFfmpegRoot = Join-Path $InstallerPublishRoot 'tools\ffmpeg'
        $staleInstallerFfmpeg = if (Test-Path -LiteralPath $installerFfmpegRoot `
                -PathType Container) {
            @(Get-ChildItem -LiteralPath $installerFfmpegRoot -Recurse -File)
        }
        else { @() }
        if ($staleInstallerFfmpeg.Count -ne 0) {
            throw 'Shared-runtime installer output still contains FFmpeg files. ' +
                'Rebuild with -OmitMediaOutputRuntime before packaging.'
        }
    }
    Assert-SafeWorkspaceDirectory $StagingRoot
    New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
    & (Join-Path $Root 'scripts\build_installer.ps1') -Version $Version `
        -SkipAppBuild -SourceDirectory $InstallerPublishRoot `
        -OutputDirectory $StagingRoot
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $StagedInstaller -PathType Leaf)) {
        throw "Windows installer build failed: $LASTEXITCODE"
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
        $RootPackage.licenseDeclared = 'GPL-3.0-only'
        $RootPackage.licenseConcluded = 'NOASSERTION'
        $RootPackage.copyrightText = 'Copyright (c) 2026 RayrenSX and third-party contributors'
        $VcRuntimeVersion = (Get-Item -LiteralPath `
            (Join-Path $PublishRoot 'vcruntime140.dll')).VersionInfo.ProductVersion

        $NativePackages = [Collections.Generic.List[object]]@(
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
                SPDXID = 'SPDXRef-Package-AirPlayServer-1.1.2'
                downloadLocation = 'https://github.com/xenos1337/AirPlayServer/releases/tag/v1.1.2'
                filesAnalyzed = $false
                licenseConcluded = 'GPL-3.0-only'
                licenseDeclared = 'GPL-3.0-only'
                copyrightText = 'Copyright (c) 2025 xenos1337 and upstream contributors'
                versionInfo = '1.1.2'
                supplier = 'Person: xenos1337'
                externalRefs = @([PSCustomObject][ordered]@{
                    referenceCategory = 'PACKAGE-MANAGER'
                    referenceType = 'purl'
                    referenceLocator = 'pkg:github/xenos1337/AirPlayServer@v1.1.2'
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
            },
            [PSCustomObject][ordered]@{
                name = 'Microsoft Visual C++ x64 runtime'
                SPDXID = 'SPDXRef-Package-Microsoft-Visual-Cpp-Runtime'
                downloadLocation = 'https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist'
                filesAnalyzed = $false
                licenseConcluded = 'NOASSERTION'
                licenseDeclared = 'NOASSERTION'
                copyrightText = 'Copyright Microsoft Corporation'
                versionInfo = $VcRuntimeVersion
                supplier = 'Organization: Microsoft Corporation'
            }
        )
        if ($UseMediaOutputRuntime) {
            $NativePackages.Add([PSCustomObject][ordered]@{
                name = 'FFmpeg media-output runtime'
                SPDXID = 'SPDXRef-Package-FFmpeg-8.1.2-Gyan'
                downloadLocation = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip'
                filesAnalyzed = $false
                licenseConcluded = 'GPL-3.0-only'
                licenseDeclared = 'GPL-3.0-only'
                copyrightText = 'NOASSERTION'
                versionInfo = '8.1.2'
                supplier = 'Organization: gyan.dev'
            })
        }
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
        Write-Sha256Sidecar $GeneratedSbom.FullName
        Copy-Item -LiteralPath $GeneratedSbom.FullName -Destination $StagedSbomAsset -Force
    }

    Compress-Archive -LiteralPath $PackageRoot -DestinationPath $StagedArchive `
        -CompressionLevel Optimal

    $assets = @(
        [PSCustomObject]@{ Path = $StagedInstaller; Name = $InstallerName },
        [PSCustomObject]@{ Path = $StagedArchive; Name = [IO.Path]::GetFileName($ArchivePath) }
    )
    if ($GenerateSbom) {
        $assets += [PSCustomObject]@{
            Path = $StagedSbomAsset
            Name = [IO.Path]::GetFileName($SbomAsset)
        }
    }
    $checksumLines = foreach ($asset in $assets) {
        $hash = (Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($asset.Name)"
    }
    [IO.File]::WriteAllText($StagedChecksum,
        ($checksumLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

    $publishAssets = @(
        [PSCustomObject]@{ Staged = $StagedInstaller; Final = $InstallerPath },
        [PSCustomObject]@{ Staged = $StagedArchive; Final = $ArchivePath }
    )
    if ($GenerateSbom) {
        $publishAssets += [PSCustomObject]@{ Staged = $StagedSbomAsset; Final = $SbomAsset }
    }
    $publishAssets += [PSCustomObject]@{ Staged = $StagedChecksum; Final = $ChecksumPath }
    $removePaths = @($LegacyLatestArchivePath)
    if (-not $GenerateSbom) { $removePaths += $SbomAsset }
    Publish-StagedAssets $publishAssets $removePaths
    if ($UpdateReleaseManifest) {
        Update-ReleaseManifestAssets $ReleaseManifestPath $Version $publishAssets
    }

    if (Test-Path -LiteralPath $LegacyArchive) {
        Remove-Item -LiteralPath $LegacyArchive -Force
    }

    Write-Host "Release package: $ArchivePath" -ForegroundColor Green
    Write-Host "Windows setup:  $InstallerPath" -ForegroundColor Green
    Write-Host "Checksums:      $ChecksumPath" -ForegroundColor Green
    if ($UpdateReleaseManifest) {
        Write-Host "Release data:  $ReleaseManifestPath" -ForegroundColor Green
    }
    if ($GenerateSbom) {
        Write-Host "SBOM:           $SbomAsset" -ForegroundColor Green
    }
}
finally {
    try {
        if ($PreserveStagingRoot) {
            Write-Warning "Release staging was retained for recovery: $StagingRoot"
        }
        elseif (Test-Path -LiteralPath $StagingRoot) {
            Assert-SafeWorkspaceDirectory $StagingRoot
            Remove-Item -LiteralPath $StagingRoot -Recurse -Force
        }
    }
    finally {
        Pop-Location
    }
}
