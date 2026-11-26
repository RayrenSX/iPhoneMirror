using System.IO;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Updater;

internal static partial class LocalizedReleaseNotes
{
    private static readonly Lazy<string> TaiwanChangelog = new(() =>
    {
        using var stream = typeof(LocalizedReleaseNotes).Assembly
            .GetManifestResourceStream("Localization.CHANGELOG.zh-TW.md");
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static string ChangelogFileName =>
        LocalizationService.EffectiveCulture.Name == LocalizationService.TraditionalChineseTaiwan
            ? "CHANGELOG.zh-TW.md" : "CHANGELOG.md";

    internal static readonly IReadOnlyList<string> TranslationLanguages =
        ["zh-CN", "zh-HK", "zh-TW", "ja-JP"];

    internal static string Body(ReleaseInfo release) =>
        Body(release, LocalizationService.EffectiveCulture.Name);

    internal static string Body(ReleaseInfo release, string language)
    {
        if (TranslatedBody(release, language) is { } translation) return translation;

        var tagged = LegacyBodies(release.TaggedBody);
        var published = LegacyBodies(release.Body);
        return tagged.English ?? published.English ?? tagged.Chinese ?? published.Chinese ?? string.Empty;
    }

    internal static string? TranslatedBody(ReleaseInfo release, string language)
    {
        if (release.LocalizedNotes.TryGetValue(language, out var localized) &&
            !string.IsNullOrWhiteSpace(localized))
            return localized;

        return language switch
        {
            LocalizationService.TraditionalChineseTaiwan =>
                FindSection(TaiwanChangelog.Value, release.TagName),
            LocalizationService.SimplifiedChinese =>
                LegacyBodies(release.TaggedBody).Chinese ?? LegacyBodies(release.Body).Chinese,
            _ => null,
        };
    }

    private static (string? English, string? Chinese) LegacyBodies(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return (null, null);

        var generated = GeneratedAppendix().Match(source);
        if (generated.Success) source = source[..generated.Index];
        source = source.Trim();
        if (source.Length == 0) return (null, null);

        // For older releases, Chinese appears before a named English section.
        // A short English summary sometimes immediately precedes that heading.
        var heading = LegacyEnglishHeading().Match(source);
        if (heading.Success)
        {
            var prefix = source[..heading.Index].TrimEnd();
            if (ContainsHan(prefix))
            {
                var summary = LegacyEnglishSummary().Match(prefix);
                var chinese = summary.Success ? prefix[..summary.Index].TrimEnd() : prefix;
                if (chinese.EndsWith("\n---", StringComparison.Ordinal)) chinese = chinese[..^4].TrimEnd();
                var english = summary.Success ? source[summary.Index..].Trim() : source[heading.Index..].Trim();
                return (english, chinese);
            }
            return (source, null);
        }

        return ContainsHan(source) ? (null, source) : (source, null);
    }

    private static bool ContainsHan(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?m)^<!-- (?:release-assets:start|release-notes:translation-link) -->")]
    private static partial System.Text.RegularExpressions.Regex GeneratedAppendix();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?m)^(?:## English Summary|### (?:Added|Changed|Fixed|Removed|Security|Notes))[ \t]*\r?$")]
    private static partial System.Text.RegularExpressions.Regex LegacyEnglishHeading();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?m)^v\d+\.\d+\.\d+(?:-[\w.-]+)? (?:is|adds|includes|fixes) [^\r\n\u3400-\u9fff]+\z")]
    private static partial System.Text.RegularExpressions.Regex LegacyEnglishSummary();

    internal static string? FindSection(string changelog, string tagName)
    {
        var version = tagName.StartsWith('v') ? tagName[1..] : tagName;
        var heading = "## [" + version + "]";
        using var reader = new StringReader(changelog);
        var lines = new List<string>();
        var found = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (found) break;
                found = line == heading || line.StartsWith(heading + " - ", StringComparison.Ordinal);
                continue;
            }
            if (found)
            {
                if (line.StartsWith('[') && line.Contains("]: ", StringComparison.Ordinal)) break;
                lines.Add(line);
            }
        }
        var body = string.Join("\n", lines).Trim();
        return found && body.Length > 0 ? body : null;
    }
}
