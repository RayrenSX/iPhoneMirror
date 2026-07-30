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

$LightThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\LightTheme.xaml')
$DarkThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\DarkTheme.xaml')
$themeDifference = @(Compare-Object $LightThemeResources $DarkThemeResources)
if ($themeDifference.Count -ne 0) {
    $themeDifference | Format-Table | Out-String | Write-Error
    throw 'Light and dark themes do not contain the same keys.'
}

$Chinese = Get-ResourceKeys (Join-Path $App 'Localization\Strings.zh-CN.xaml')
$English = Get-ResourceKeys (Join-Path $App 'Localization\Strings.en-US.xaml')
$ApplicationResources = @(
    Get-ResourceKeys (Join-Path $App 'App.xaml')
    $LightThemeResources
)
$difference = @(Compare-Object $Chinese $English)
if ($difference.Count -ne 0) {
    $difference | Format-Table | Out-String | Write-Error
    throw 'Localization dictionaries do not contain the same keys.'
}

$used = Get-ReferencedResourceKeys $App

$missing = @($used | Where-Object {
    $_ -notin $Chinese -and $_ -notin $ApplicationResources
} | Sort-Object)
if ($missing.Count -ne 0) {
    throw "Missing localization keys: $($missing -join ', ')"
}

$DriverChinese = Get-ResourceKeys (Join-Path $DriverInstaller `
    'Localization\Strings.zh-CN.xaml')
$DriverEnglish = Get-ResourceKeys (Join-Path $DriverInstaller `
    'Localization\Strings.en-US.xaml')
$driverDifference = @(Compare-Object $DriverChinese $DriverEnglish)
if ($driverDifference.Count -ne 0) {
    $driverDifference | Format-Table | Out-String | Write-Error
    throw 'Driver localization dictionaries do not contain the same keys.'
}
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
    EnglishKeys = $English.Count
    ReferencedKeys = $used.Count
    MissingKeys = 0
    DriverChineseKeys = $DriverChinese.Count
    DriverEnglishKeys = $DriverEnglish.Count
    DriverReferencedKeys = $driverUsed.Count
    ThemeKeys = $LightThemeResources.Count
}
