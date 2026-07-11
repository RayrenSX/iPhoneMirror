[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$App = Join-Path $Root 'src\App'

function Get-ResourceKeys([string]$Path) {
    $xml = [xml](Get-Content -Raw -LiteralPath $Path -Encoding utf8)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    return @($xml.SelectNodes('//*[@x:Key]', $namespaces) | ForEach-Object {
        $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    })
}

$Chinese = Get-ResourceKeys (Join-Path $App 'Localization\Strings.zh-CN.xaml')
$English = Get-ResourceKeys (Join-Path $App 'Localization\Strings.en-US.xaml')
$difference = @(Compare-Object $Chinese $English)
if ($difference.Count -ne 0) {
    $difference | Format-Table | Out-String | Write-Error
    throw 'Localization dictionaries do not contain the same keys.'
}

$used = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
Get-ChildItem -LiteralPath $App -Recurse -File -Include *.xaml,*.cs |
    Where-Object { $_.Name -notlike 'Strings.*.xaml' } |
    ForEach-Object {
        $content = Get-Content -Raw -LiteralPath $_.FullName -Encoding utf8
        if ($null -ne $content) {
            [regex]::Matches($content, 'DynamicResource\s+([A-Za-z0-9_]+)') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
            [regex]::Matches($content,
                'LocalizationService\.(?:Get|Format)\(\s*"([A-Za-z0-9_]+)"') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
        }
    }

$missing = @($used | Where-Object { $_ -notin $Chinese } | Sort-Object)
if ($missing.Count -ne 0) {
    throw "Missing localization keys: $($missing -join ', ')"
}

[pscustomobject]@{
    ChineseKeys = $Chinese.Count
    EnglishKeys = $English.Count
    ReferencedKeys = $used.Count
    MissingKeys = 0
}
