[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$App = Join-Path $Root 'src\App'
$DriverInstaller = Join-Path $Root 'src\DriverInstaller'
$SharedUI = Join-Path $Root 'src\SharedUI'

function Get-ResourceKeys([string]$Path) {
    $xml = [xml](Get-Content -Raw -LiteralPath $Path -Encoding utf8)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    return @($xml.SelectNodes('//*[@x:Key]', $namespaces) | ForEach-Object {
        $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    })
}

function Get-ReferencedResourceKeys([string]$Path) {
    $used = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    Get-ChildItem -LiteralPath $Path -Recurse -File -Include *.xaml,*.cs |
        Where-Object { $_.Name -notlike 'Strings.*.xaml' } |
        ForEach-Object {
            $content = Get-Content -Raw -LiteralPath $_.FullName -Encoding utf8
            if ($null -eq $content) { return }
            [regex]::Matches($content, 'DynamicResource\s+([A-Za-z0-9_]+)') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
            [regex]::Matches($content,
                '(?:LocalizationService|DriverLocalization)\.(?:Get|Format)\(\s*"([A-Za-z0-9_]+)"') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
        }
    return $used
}

function Get-ResourceValues([string]$Path) {
    $xml = [xml](Get-Content -Raw -LiteralPath $Path -Encoding utf8)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    $values = [System.Collections.Generic.Dictionary[string,string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($node in $xml.SelectNodes('//*[@x:Key]', $namespaces)) {
        $key = $node.GetAttribute(
            'Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        $values.Add($key, $node.InnerText)
    }
    return $values
}

function Assert-FormatPlaceholders(
    [string]$ReferencePath,
    [string]$CandidatePath) {
    $reference = Get-ResourceValues $ReferencePath
    $candidate = Get-ResourceValues $CandidatePath
    foreach ($key in $reference.Keys) {
        if (-not $candidate.ContainsKey($key)) { continue }
        $referenceTokens = @([regex]::Matches(
            $reference[$key], '(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}(?!\})') |
            ForEach-Object Value | Sort-Object)
        $candidateTokens = @([regex]::Matches(
            $candidate[$key], '(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}(?!\})') |
            ForEach-Object Value | Sort-Object)
        if (($referenceTokens -join "`n") -ne ($candidateTokens -join "`n")) {
            throw "Format placeholders differ for '$key' between '$ReferencePath' and '$CandidatePath'."
        }
    }
}

function Assert-HongKongTerminology([string]$Path) {
    $values = Get-ResourceValues $Path
    $legacyTerms = @(
        '<5217><8868>', '<97FF><61C9>', '<4F9D><6B21>',
        '<9000><51FA><4EE3><78BC>', '<91CD><7F6E>', '<7FA3>',
        '<8EDF><9AD4>', '<7DB2><8DEF>')
    foreach ($legacyTerm in $legacyTerms) {
        $legacyTerm = [regex]::Replace($legacyTerm, '<([0-9A-Fa-f]{4})>', {
            param($match)
            [char][Convert]::ToInt32($match.Groups[1].Value, 16)
        })
        $matching = @($values.GetEnumerator() | Where-Object {
            $_.Value.IndexOf($legacyTerm, [StringComparison]::Ordinal) -ge 0
        })
        if ($matching.Count -ne 0) {
            throw "Hong Kong localization contains non-localized terminology '$legacyTerm' in '$($matching[0].Key)'."
        }
    }
}

$LightThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\LightTheme.xaml')
$DarkThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\DarkTheme.xaml')
$themeDifference = @(Compare-Object $LightThemeResources $DarkThemeResources)
if ($themeDifference.Count -ne 0) {
    $themeDifference | Format-Table | Out-String | Write-Error
    throw 'Light and dark themes do not contain the same keys.'
}

$ChinesePath = Join-Path $App 'Localization\Strings.zh-CN.xaml'
$HongKongPath = Join-Path $App 'Localization\Strings.zh-HK.xaml'
$EnglishPath = Join-Path $App 'Localization\Strings.en-US.xaml'
$Chinese = Get-ResourceKeys $ChinesePath
$HongKong = Get-ResourceKeys $HongKongPath
$English = Get-ResourceKeys $EnglishPath
$ApplicationResources = @(
    Get-ResourceKeys (Join-Path $App 'App.xaml')
    $LightThemeResources
)
$difference = @(
    Compare-Object $Chinese $English
    Compare-Object $English $HongKong
)
if ($difference.Count -ne 0) {
    $difference | Format-Table | Out-String | Write-Error
    throw 'Localization dictionaries do not contain the same keys.'
}
Assert-FormatPlaceholders $EnglishPath $ChinesePath
Assert-FormatPlaceholders $EnglishPath $HongKongPath
Assert-HongKongTerminology $HongKongPath

$used = Get-ReferencedResourceKeys $App

$missing = @($used | Where-Object {
    $_ -notin $Chinese -and $_ -notin $ApplicationResources
} | Sort-Object)
if ($missing.Count -ne 0) {
    throw "Missing localization keys: $($missing -join ', ')"
}

$DriverChinesePath = Join-Path $DriverInstaller 'Localization\Strings.zh-CN.xaml'
$DriverHongKongPath = Join-Path $DriverInstaller 'Localization\Strings.zh-HK.xaml'
$DriverEnglishPath = Join-Path $DriverInstaller 'Localization\Strings.en-US.xaml'
$DriverChinese = Get-ResourceKeys $DriverChinesePath
$DriverHongKong = Get-ResourceKeys $DriverHongKongPath
$DriverEnglish = Get-ResourceKeys $DriverEnglishPath
$driverDifference = @(
    Compare-Object $DriverChinese $DriverEnglish
    Compare-Object $DriverEnglish $DriverHongKong
)
if ($driverDifference.Count -ne 0) {
    $driverDifference | Format-Table | Out-String | Write-Error
    throw 'Driver localization dictionaries do not contain the same keys.'
}
Assert-FormatPlaceholders $DriverEnglishPath $DriverChinesePath
Assert-FormatPlaceholders $DriverEnglishPath $DriverHongKongPath
Assert-HongKongTerminology $DriverHongKongPath
$DriverApplicationResources = @(
    Get-ResourceKeys (Join-Path $DriverInstaller 'App.xaml')
    $LightThemeResources
)
$driverUsed = Get-ReferencedResourceKeys $DriverInstaller
$driverMissing = @($driverUsed | Where-Object {
    $_ -notin $DriverChinese -and $_ -notin $DriverApplicationResources
} | Sort-Object)
if ($driverMissing.Count -ne 0) {
    throw "Missing driver localization keys: $($driverMissing -join ', ')"
}

[pscustomobject]@{
    ChineseKeys = $Chinese.Count
    HongKongKeys = $HongKong.Count
    EnglishKeys = $English.Count
    ReferencedKeys = $used.Count
    MissingKeys = 0
    DriverChineseKeys = $DriverChinese.Count
    DriverHongKongKeys = $DriverHongKong.Count
    DriverEnglishKeys = $DriverEnglish.Count
    DriverReferencedKeys = $driverUsed.Count
    ThemeKeys = $LightThemeResources.Count
}
