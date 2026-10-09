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
        Where-Object { $_.Name -notlike 'Strings.*.xaml' -and
            $_.FullName -notmatch '[\\/](bin|obj|native)[\\/]' } |
        ForEach-Object {
            $content = Get-Content -Raw -LiteralPath $_.FullName -Encoding utf8
            if ($null -eq $content) { return }
            [regex]::Matches($content, 'DynamicResource\s+([A-Za-z0-9_]+)') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
            [regex]::Matches($content,
                '(?:LocalizationService|DriverLocalization)\.(?:Get|GetOrDefault|Format)\(\s*"([A-Za-z0-9_]+)"(?!\s*\+)') |
                ForEach-Object { [void]$used.Add($_.Groups[1].Value) }
            # Include both resource names selected by a boolean property.
            [regex]::Matches($content,
                '(?:LocalizationService|DriverLocalization)\.(?:Get|GetOrDefault|Format)\(\s*[A-Za-z_][A-Za-z0-9_.]*\s*\?\s*"([A-Za-z0-9_]+)"\s*:\s*"([A-Za-z0-9_]+)"(?=\s*[,\)])') |
                ForEach-Object {
                    [void]$used.Add($_.Groups[1].Value)
                    [void]$used.Add($_.Groups[2].Value)
                }
        }
    return ,$used
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
        if ($values.ContainsKey($key)) {
            throw "Duplicate localization key '$key' in '$Path'."
        }
        if ([string]::IsNullOrWhiteSpace($node.InnerText)) {
            throw "Empty localization value for '$key' in '$Path'."
        }
        if ($node.LocalName -eq 'String') {
            try { [void][System.Text.CompositeFormat]::Parse($node.InnerText) }
            catch { throw "Invalid format string for '$key' in '${Path}': $($_.Exception.Message)" }
        }
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
            ($reference[$key] -replace '\{\{|\}\}', ''), '\{\d+(?:,[^}:]+)?(?::[^}]+)?\}') |
            ForEach-Object Value | Sort-Object)
        $candidateTokens = @([regex]::Matches(
            ($candidate[$key] -replace '\{\{|\}\}', ''), '\{\d+(?:,[^}:]+)?(?::[^}]+)?\}') |
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

function Get-LocalizationDictionaries([string]$ProjectPath, [string]$ServicePath) {
    $dictionaries = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $ProjectPath 'Localization') `
            -Filter 'Strings.*.xaml' -File | Sort-Object Name)) {
        $culture = $file.BaseName.Substring('Strings.'.Length)
        $dictionaries.Add($culture, $file.FullName)
    }
    # Check every dictionary, including new languages. Also catch deletion of a
    # dictionary still declared by the application's language service.
    $service = Get-Content -LiteralPath $ServicePath -Raw -Encoding utf8
    $declared = @([regex]::Matches($service,
        'const\s+string\s+\w+\s*=\s*"([a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($declared.Count -eq 0) {
        throw "No supported languages found in '$ServicePath'."
    }
    foreach ($culture in @('en-US') + $declared) {
        if (-not $dictionaries.Contains($culture)) {
            throw "Missing localization dictionary Strings.$culture.xaml in '$ProjectPath'."
        }
    }
    return $dictionaries
}

function Assert-LocalizationDictionaries(
    [Collections.IDictionary]$Dictionaries,
    [string]$Component) {
    $referencePath = $Dictionaries['en-US']
    $reference = Get-ResourceValues $referencePath
    foreach ($culture in $Dictionaries.Keys) {
        $path = $Dictionaries[$culture]
        $values = Get-ResourceValues $path
        $missing = @($reference.Keys | Where-Object { -not $values.ContainsKey($_) } | Sort-Object)
        $extra = @($values.Keys | Where-Object { -not $reference.ContainsKey($_) } | Sort-Object)
        if ($missing.Count -ne 0 -or $extra.Count -ne 0) {
            throw "$Component localization '$culture' differs from en-US. Missing: $($missing -join ', '); Extra: $($extra -join ', ')."
        }
        Assert-FormatPlaceholders $referencePath $path
        if ($culture -eq 'zh-HK') { Assert-HongKongTerminology $path }
        [pscustomobject]@{
            Component = $Component
            Language = $culture
            Keys = $values.Count
            Status = 'Passed'
        }
    }
}

$LightThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\LightTheme.xaml')
$DarkThemeResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\DarkTheme.xaml')
$DesignResources = Get-ResourceKeys (Join-Path $SharedUI `
    'Themes\DesignTokens.xaml')
$themeDifference = @(Compare-Object $LightThemeResources $DarkThemeResources)
if ($themeDifference.Count -ne 0) {
    $themeDifference | Format-Table | Out-String | Write-Error
    throw 'Light and dark themes do not contain the same keys.'
}

$AppDictionaries = Get-LocalizationDictionaries $App `
    (Join-Path $App 'Localization\LocalizationService.cs')
$DriverDictionaries = Get-LocalizationDictionaries $DriverInstaller `
    (Join-Path $DriverInstaller 'Services\DriverLocalization.cs')
$languageDifference = @(Compare-Object @($AppDictionaries.Keys) @($DriverDictionaries.Keys))
if ($languageDifference.Count -ne 0) {
    throw "App and driver must provide the same languages. Differences: $($languageDifference.InputObject -join ', ')."
}
$LocalizationResults = @(
    Assert-LocalizationDictionaries $AppDictionaries 'App'
    Assert-LocalizationDictionaries $DriverDictionaries 'Driver'
)
$English = Get-ResourceKeys $AppDictionaries['en-US']
$ApplicationResources = @(
    Get-ResourceKeys (Join-Path $App 'App.xaml')
    $LightThemeResources
    $DesignResources
)
$used = Get-ReferencedResourceKeys $App
# These resource names are constructed from enum values at runtime. A partial
# prefix such as ControlProgress is not itself a resource reference.
$controlSource = Get-Content -Raw -LiteralPath (Join-Path $App 'Services\ControlStatusService.cs')
foreach ($enum in @('ControlStage', 'ControlStageProgress')) {
    $body = [regex]::Match($controlSource, "enum $enum\s*\{([^}]+)\}").Groups[1].Value
    $prefix = if ($enum -eq 'ControlStageProgress') { 'ControlProgress' } else { 'ControlStage' }
    foreach ($name in ($body -split ',')) {
        if ($name.Trim()) { [void]$used.Add($prefix + $name.Trim()) }
    }
}

$missing = @($used | Where-Object {
    $_ -notin $English -and $_ -notin $ApplicationResources
} | Sort-Object)
if ($missing.Count -ne 0) {
    throw "Missing localization keys: $($missing -join ', ')"
}

$DriverEnglish = Get-ResourceKeys $DriverDictionaries['en-US']
$DriverApplicationResources = @(
    Get-ResourceKeys (Join-Path $DriverInstaller 'App.xaml')
    $LightThemeResources
    $DesignResources
)
$driverUsed = Get-ReferencedResourceKeys $DriverInstaller
$driverMissing = @($driverUsed | Where-Object {
    $_ -notin $DriverEnglish -and $_ -notin $DriverApplicationResources
} | Sort-Object)
if ($driverMissing.Count -ne 0) {
    throw "Missing driver localization keys: $($driverMissing -join ', ')"
}

$LocalizationResults | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Referenced keys verified: App=$($used.Count), Driver=$($driverUsed.Count); Theme keys=$($LightThemeResources.Count)."
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @(
        '### Localization verification'
        ''
        '| Component | Language | Keys | Status |'
        '| --- | --- | ---: | --- |'
        $LocalizationResults | ForEach-Object {
            "| $($_.Component) | $($_.Language) | $($_.Keys) | $($_.Status) |"
        }
    )
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8
}
