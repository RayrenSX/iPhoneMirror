function Get-UsbTouchBridgeRuntimeManifestEntries {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Label
    )

    $Directory = [IO.Path]::GetFullPath($Directory).TrimEnd('\\')
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Label directory is missing: $Directory"
    }
    $manifestPath = Join-Path $Directory 'iUsbBridge.runtime.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "$Label manifest is missing: $manifestPath"
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "$Label manifest is invalid: $manifestPath"
    }
    if ($manifest.schema -ne 1 -or $null -eq $manifest.files) {
        throw "$Label manifest has an unsupported schema."
    }
    $entries = @($manifest.files)
    if ($entries.Count -eq 0 -or
        @($entries | Where-Object { $_.path -eq 'iUsbBridge.exe' }).Count -ne 1 -or
        @($entries | Where-Object { $_.path -like '_internal/*' }).Count -eq 0) {
        throw "$Label manifest does not describe a complete onedir bridge."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $relative = [string]$entry.path
        $expectedHash = [string]$entry.sha256
        $segments = @($relative -split '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            $relative.Contains('\') -or
            $relative -match '[\x00-\x1F<>:"|?*]' -or
            @($segments | Where-Object {
                [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..')
            }).Count -ne 0 -or
            ($relative -ne 'iUsbBridge.exe' -and
                -not $relative.StartsWith('_internal/', [StringComparison]::Ordinal)) -or
            $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
            -not $seen.Add($relative)) {
            throw "$Label manifest contains an invalid file entry."
        }
    }
    return $entries
}

function Assert-UsbTouchBridgeRuntime {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Label
    )

    $Directory = [IO.Path]::GetFullPath($Directory).TrimEnd('\\')
    $entries = @(Get-UsbTouchBridgeRuntimeManifestEntries $Directory $Label)
    $runtimeDirectory = Join-Path $Directory '_internal'
    if (-not (Test-Path -LiteralPath $runtimeDirectory -PathType Container)) {
        throw "$Label runtime directory is missing: $runtimeDirectory"
    }
    $reparse = @(Get-ChildItem -LiteralPath $runtimeDirectory -Recurse -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparse.Count -ne 0) {
        throw "$Label runtime directory contains a reparse point: $($reparse[0].FullName)"
    }

    $expected = @($entries | ForEach-Object {
        ([string]$_.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    })
    $actual = @('iUsbBridge.exe') + @(Get-ChildItem -LiteralPath $runtimeDirectory `
        -Recurse -File | ForEach-Object {
            $_.FullName.Substring($Directory.Length + 1)
        })
    $missing = @($expected | Where-Object { $_ -notin $actual })
    $unexpected = @($actual | Where-Object { $_ -notin $expected })
    if ($missing.Count -ne 0 -or $unexpected.Count -ne 0) {
        throw "$Label files do not match its manifest. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
    foreach ($entry in $entries) {
        $relative = [string]$entry.path
        $file = Join-Path $Directory ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "$Label is missing runtime file: $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, [string]$entry.sha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label hash mismatch for $relative"
        }
    }
}

function Get-UsbTouchBridgeRuntimePayloadFiles {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [string]$TargetDirectory = 'tools'
    )

    $entries = @(Get-UsbTouchBridgeRuntimeManifestEntries $Directory 'USB touch bridge runtime')
    return @((Join-Path $TargetDirectory 'iUsbBridge.runtime.json')) + @(
        $entries | ForEach-Object {
            Join-Path $TargetDirectory (([string]$_.path).Replace('/', [IO.Path]::DirectorySeparatorChar))
        })
}
