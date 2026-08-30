[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $Root 'config\apple-mobile-capture-pids.txt'
$managedPath = Join-Path $Root 'src\DriverInstaller\Services\DriverConstants.cs'
$nativePath = Join-Path $Root 'src\Core\src\Device\AppleUsbDiscovery.h'
$cleanupPath = Join-Path $Root 'scripts\remove_selected_iphone_drivers.ps1'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Apple mobile capture PID manifest is missing: $manifestPath"
}

$productIds = @(
    Get-Content -LiteralPath $manifestPath | ForEach-Object {
        $value = ($_ -split '#', 2)[0].Trim().ToUpperInvariant()
        if ($value.Length -eq 0) { return }
        if ($value -notmatch '^[0-9A-F]{4}$') {
            throw "Invalid Apple mobile capture PID: $value"
        }
        $value
    }
)
if ($productIds.Count -eq 0 -or
    @($productIds | Select-Object -Unique).Count -ne $productIds.Count) {
    throw 'Apple mobile capture PID manifest must contain unique product IDs.'
}

function Replace-Section([string]$path, [string]$pattern, [string]$replacement,
    [string]$label) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $text = $encoding.GetString([IO.File]::ReadAllBytes($path))
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) {
        throw "Could not locate the $label PID section in $path"
    }
    $expanded = $replacement.Replace('{START}', $match.Groups[1].Value).Replace(
        '{END}', $match.Groups[2].Value)
    $updated = $text.Substring(0, $match.Index) + $expanded +
        $text.Substring($match.Index + $match.Length)
    [IO.File]::WriteAllText($path, $updated, $encoding)
}

$managedValues = ($productIds | ForEach-Object { "0x$_" }) -join ', '
Replace-Section $managedPath `
    '(?s)(private static readonly int\[\] AppleMobileCaptureProductIds =\s*\[).*?(\];)' `
    ('{START}' + "`r`n        " + $managedValues + "`r`n    {END}") `
    'managed'

$nativeValues = ($productIds | ForEach-Object { "0x$_" }) -join ', '
Replace-Section $nativePath `
    '(?s)(constexpr std::array<std::uint32_t, \d+> mobile_capture_product_ids\{).*?(\};)' `
    ('{START}' + "`r`n        " + $nativeValues + "{END}") `
    'native'

$cleanupValues = ($productIds | ForEach-Object { "    '$_'" }) -join "`r`n"
Replace-Section $cleanupPath `
    '(?s)(foreach \(\$productId in @\(\r?\n).*?(\r?\n\)\) \{)' `
    ('{START}' + $cleanupValues + '{END}') `
    'cleanup'
