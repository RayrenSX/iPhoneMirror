function Read-ReleaseNoteSources([string]$Root, [string]$Tag, [switch]$RequireTranslations) {
    if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid release tag: $Tag"
    }

    $notesDirectory = Join-Path $Root 'docs/releases'
    $generatedContent = '(?mi)^##[ \t]+(?:Release assets|发布资产|發行資產)[ \t]*\r?$|<!-- release-(?:assets|notes):'
    $translations = [ordered]@{}
    foreach ($language in @('zh-CN', 'zh-HK', 'zh-TW', 'ja-JP')) {
        $path = Join-Path $notesDirectory "$Tag.$language.md"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $body = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Trim()
            if ($body -and $body -notmatch $generatedContent) {
                $translations[$language] = $body
            }
        }
        if ($RequireTranslations -and -not $translations.Contains($language)) {
            throw "Release notes are missing or invalid: docs/releases/$Tag.$language.md"
        }
    }
    if ($translations.Count -eq 0) { return }

    $path = Join-Path $notesDirectory "$Tag.md"
    $english = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Trim()
    $englishHeading = '(?m)^### (?:Added|Changed|Fixed|Removed|Security|Notes)[ \t]*\r?$'
    $nonEnglishLine = '(?m)^[ \t]*(?:(?:#{1,6}|[-*]) +|\*\*)?(?:v[0-9.]+(?:-[\w.-]+)? +)?[\u3040-\u30ff\u3400-\u9fff]'
    if ($english -notmatch $englishHeading -or $english -match $generatedContent -or
        $english -match $nonEnglishLine) {
        throw "English release notes are missing or invalid: docs/releases/$Tag.md"
    }

    return @{ English = $english; Translations = $translations }
}

function Format-PublicReleaseNotes([string]$English, [string]$Repository, [string]$Tag) {
    $chinese = "https://github.com/$Repository/blob/$Tag/docs/releases/$Tag.zh-CN.md"
    return $English + "`n`n<!-- release-notes:translation-link -->`n[简体中文发布说明]($chinese)"
}
