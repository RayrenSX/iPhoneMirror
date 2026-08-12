[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Manifest = Import-PowerShellDataFile -LiteralPath `
    (Join-Path $Root 'scripts\inno-runtime-manifest.psd1')
$ToolRoot = Join-Path $Root 'work\tools\inno-setup'
$Compiler = Join-Path $ToolRoot 'ISCC.exe'
$CacheRoot = Join-Path $Root 'work\cache'
$Installer = Join-Path $CacheRoot "innosetup-$($Manifest.Version).exe"
$LanguageDirectory = Join-Path $ToolRoot 'Languages'
$ChineseSimplified = Join-Path $LanguageDirectory 'ChineseSimplified.isl'
$ChineseSimplifiedCache = Join-Path $CacheRoot `
    "ChineseSimplified-is-$($Manifest.Version).isl"
$ChineseTraditional = Join-Path $LanguageDirectory 'ChineseTraditional.isl'
$ChineseTraditionalCache = Join-Path $CacheRoot `
    "ChineseTraditional-is-$($Manifest.Version).isl"

function Assert-FileHash([string]$Path, [string]$ExpectedHash, [string]$Description) {
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) {
        throw "$Description hash mismatch: expected $ExpectedHash, got $actualHash"
    }
}

function Test-FileHash([string]$Path, [string]$ExpectedHash) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() `
            -eq $ExpectedHash)
}

function ConvertFrom-HexEscapes([string]$Value) {
    return [regex]::Replace($Value, '<([0-9A-Fa-f]{4})>', {
        param($match)
        [char][Convert]::ToInt32($match.Groups[1].Value, 16)
    })
}

function Assert-HongKongTraditionalChineseTranslation([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw 'The Hong Kong Traditional Chinese translation must be UTF-8 with a BOM.'
    }

    $content = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($true))
    foreach ($required in @(
            'LanguageName=<7e41><9ad4><4e2d><6587>',
            'LanguageID=$0C04',
            'LanguageCodepage=950')) {
        if ($content.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
            throw "Hong Kong Traditional Chinese translation is missing: $required"
        }
    }

    foreach ($legacyTerm in @(
            '<8B8A><91CF>', '<5217><8868>', '<5176><5B83>', '<6C96><7A81>',
            '<6CE8><518A>', '<7A0B><5F0F><96C6><5716><793A>', '<78C1><7247>',
            '<76EE><7684><8CC7><6599><593E>', '<5B89><88DD><578B><614B>',
            '<9644><52A0><7684><5DE5><4F5C>', '<9644><52A0><5DE5><4F5C>',
            '<7A0B><5F0F><7684><5716><793A>', '<9000><51FA><4EE3><78BC>',
            '<985E><578B><5EAB>', '<8EDF><9AD4>', '<8B80><6211><6A94><6848>',
            '<60A8><5FC5><9808><767B><5165><6210><7CFB><7D71><7BA1><7406><54E1>')) {
        $legacyTerm = ConvertFrom-HexEscapes $legacyTerm
        if ($content.IndexOf($legacyTerm, [StringComparison]::Ordinal) -ge 0) {
            throw "Hong Kong Traditional Chinese translation contains legacy terminology: $legacyTerm"
        }
    }
}

function Write-CompatibleTraditionalChineseTranslation(
    [string]$Source, [string]$Destination) {
    $content = [IO.File]::ReadAllText($Source, [Text.Encoding]::UTF8)
    foreach ($messageName in @('DownloadingLabel', 'ErrorFileHash1',
            'ErrorFileHash2')) {
        $pattern = '(?m)^' + [regex]::Escape($messageName) +
            '=[^\r\n]*(?:\r?\n|\z)'
        $matches = [regex]::Matches($content, $pattern)
        if ($matches.Count -ne 1) {
            throw "Expected one obsolete $messageName message in the Traditional Chinese translation, found $($matches.Count)."
        }
        $content = [regex]::Replace($content, $pattern, '', 1)
    }

    # The upstream translation targets Taiwan. Keep the vetted upstream text
    # where terminology is shared, and deterministically apply Hong Kong terms.
    $replacements = [ordered]@{
            'LanguageID=$0404' = 'LanguageID=$0C04'
            '<7A0B><5F0F><96C6><5716><793A>' = '<6377><5F91>'
            '<8CC7><6599><593E><5217><8868>' = '<8CC7><6599><593E><6E05><55AE>'
            '<76EE><7684><8CC7><6599><593E>' = '<5B89><88DD><8CC7><6599><593E>'
            '<5B89><88DD><578B><614B>' = '<5B89><88DD><985E><578B>'
            '<9644><52A0><7684><5DE5><4F5C>' = '<5176><4ED6><5DE5><4F5C>'
            '<9644><52A0><5DE5><4F5C>' = '<5176><4ED6><5DE5><4F5C>'
            '<7A0B><5F0F><7684><5716><793A>' = '<61C9><7528><7A0B><5F0F><5716><793A>'
            '<8B8A><91CF>' = '<8B8A><6578>'
            '<5176><5B83>' = '<5176><4ED6>'
            '<6C96><7A81>' = '<885D><7A81>'
            '<6CE8><518A>' = '<8A3B><518A>'
            '<78C1><7247>' = '<78C1><789F>'
            '<8EDF><9AD4>' = '<8EDF><4EF6>'
            '<9000><51FA><4EE3><78BC>' = '<7D50><675F><4EE3><78BC>'
            '<985E><578B><5EAB>' = '<985E><578B><7A0B><5F0F><5EAB>'
            '<8B80><6211><6A94><6848>' = '<8AAA><660E><6A94><6848>'
            '<5B89><88DD><65BC>' = '<5B89><88DD><5230>'
            '<5B89><88DD><5728>' = '<5B89><88DD><5230>'
            '<4E0B><9762>' = '<4E0B><65B9>'
            '<201C>' = '<300C>'
            '<201D>' = '<300D>'
            '<60A8><5FC5><9808><767B><5165><6210><7CFB><7D71><7BA1><7406><54E1>' =
                '<60A8><5FC5><9808><4EE5><7CFB><7D71><7BA1><7406><54E1><8EAB><5206><767B><5165>'
            '<5EFA><7ACB><684C><9762><5716><793A>' = '<5EFA><7ACB><684C><9762><6377><5F91>'
    }
    foreach ($replacement in $replacements.GetEnumerator()) {
        $sourceText = ConvertFrom-HexEscapes ([string]$replacement.Key)
        $destinationText = ConvertFrom-HexEscapes ([string]$replacement.Value)
        $content = $content.Replace(
            $sourceText,
            $destinationText)
    }

    [IO.File]::WriteAllText($Destination, $content,
        [Text.UTF8Encoding]::new($true))
    Assert-HongKongTraditionalChineseTranslation $Destination
}

if ((Test-FileHash $ChineseSimplified $Manifest.ChineseSimplified.Sha256) -and
    (Test-FileHash $ChineseTraditional `
        $Manifest.ChineseTraditional.CompatibleSha256) -and
    (Test-Path -LiteralPath $Compiler -PathType Leaf) -and
    -not $Force) {
    Assert-HongKongTraditionalChineseTranslation $ChineseTraditional
    Write-Output $Compiler
    return
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf) -or $Force) {
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf) -or $Force) {
        Invoke-WebRequest -Uri $Manifest.DownloadUrl -OutFile $Installer -UseBasicParsing
    }
    Assert-FileHash $Installer $Manifest.Sha256 'Inno Setup installer'

    New-Item -ItemType Directory -Force -Path $ToolRoot | Out-Null
    $arguments = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS',
        "/DIR=$ToolRoot"
    )
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru `
        -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup compiler installation failed: $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
        throw "Inno Setup compiler was not installed at the expected path: $Compiler"
    }
}

if (-not (Test-Path -LiteralPath $ChineseSimplifiedCache -PathType Leaf) -or $Force) {
    Invoke-WebRequest -Uri $Manifest.ChineseSimplified.DownloadUrl `
        -OutFile $ChineseSimplifiedCache -UseBasicParsing
}
Assert-FileHash $ChineseSimplifiedCache $Manifest.ChineseSimplified.Sha256 `
    'Inno Setup Simplified Chinese translation'
New-Item -ItemType Directory -Force -Path $LanguageDirectory | Out-Null
Copy-Item -LiteralPath $ChineseSimplifiedCache -Destination $ChineseSimplified -Force
Assert-FileHash $ChineseSimplified $Manifest.ChineseSimplified.Sha256 `
    'Installed Inno Setup Simplified Chinese translation'

if (-not (Test-Path -LiteralPath $ChineseTraditionalCache -PathType Leaf) -or $Force) {
    Invoke-WebRequest -Uri $Manifest.ChineseTraditional.DownloadUrl `
        -OutFile $ChineseTraditionalCache -UseBasicParsing
}
Assert-FileHash $ChineseTraditionalCache $Manifest.ChineseTraditional.Sha256 `
    'Inno Setup Traditional Chinese translation'
Write-CompatibleTraditionalChineseTranslation $ChineseTraditionalCache `
    $ChineseTraditional
Assert-FileHash $ChineseTraditional `
    $Manifest.ChineseTraditional.CompatibleSha256 `
    'Installed Inno Setup Traditional Chinese compatibility translation'
Write-Output $Compiler
