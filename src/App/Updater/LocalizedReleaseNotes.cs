using System.IO;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Updater;

internal static class LocalizedReleaseNotes
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

    // Publisher-supplied release text is content, not a string to transliterate.
    // Ship reviewed translations for every historical release and prefer the
    // publisher's localized notes for new releases when they are available.
    internal static string Body(ReleaseInfo release) =>
        LocalizationService.EffectiveCulture.Name == LocalizationService.TraditionalChineseTaiwan
            ? TaiwanBody(release) ?? release.Body
            : release.Body;

    internal static string? TaiwanBody(ReleaseInfo release) =>
        release.TaiwanBody ?? FindSection(TaiwanChangelog.Value, release.TagName);

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
