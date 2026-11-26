function Read-ReleaseNoteSources([string]$Root, [string]$Tag, [switch]$RequireTranslations) {
    if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid release tag: $Tag"
    }

    $notesDirectory = Join-Path $Root 'docs/releases'
    $generatedContent = '(?mi)^##[ \t]+(?:Release assets|发布资产|發行資產)[ \t]*\r?$|<!-- (?:release-(?:assets|notes)|iphonemirror-localized-notes):'
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

function Format-PublicReleaseNotes([hashtable]$Sources, [string]$Repository, [string]$Tag) {
    $chinese = "https://github.com/$Repository/blob/$Tag/docs/releases/$Tag.zh-CN.md"
    # GitHub hides the comment when rendering Markdown. The API returns it
    # with the release metadata, so update windows need no separate requests.
    $json = ConvertTo-Json -InputObject $Sources.Translations -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    if ($encoded.Length -gt 64kb) { throw "Localized notes for $Tag exceed 64 KiB when encoded." }
    return $Sources.English + "`n`n<!-- release-notes:translation-link -->`n[简体中文发布说明]($chinese)" +
        "`n`n<!-- iphonemirror-localized-notes:v1`n$encoded`n-->"
}
