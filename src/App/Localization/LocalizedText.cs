using System.Runtime.CompilerServices;

namespace IPhoneMirror.App.Localization;

// Existing services exchange strings with UI windows. Keep the resource origin
// attached to those exact string instances so a cached message can be rendered
// again after a language change. Never infer a key from the displayed text: a
// device name can equal a translation, and different keys can share a caption.
// Weak keys allow completed operations and their format arguments to be collected.
internal static class LocalizedText
{
    private sealed record Source(string? Key, object?[] Arguments, string? Separator = null, bool IsFormat = false);
    private static readonly ConditionalWeakTable<string, Source> Sources = new();

    internal static string Resource(string key, string value) =>
        Remember(value, new(key, []));

    internal static string Format(string key, object?[] arguments)
    {
        var source = new Source(key, arguments.ToArray(), IsFormat: true);
        return Remember(Render(source), source);
    }

    internal static string Join(string separator, IEnumerable<string?> values)
    {
        var source = new Source(null, values.Cast<object?>().ToArray(), separator);
        return Remember(Render(source), source);
    }

    internal static string Refresh(string value) => Sources.TryGetValue(value, out var source)
        ? Remember(Render(source), source) : value;

    private static string Render(Source source)
    {
        var arguments = source.Arguments.Select(value => value is string text
            ? Refresh(text) : value).ToArray();
        if (source.Separator is { } separator) return string.Join(separator, arguments);
        var format = LocalizationService.Get(source.Key!);
        return source.IsFormat ? string.Format(LocalizationService.EffectiveCulture, format, arguments)
            : format;
    }

    private static string Remember(string value, Source source)
    {
        if (value.Length == 0) return value;
        // Do not annotate interned strings or resource dictionary values, which
        // may be shared by unrelated keys or by external/user-provided data.
        var tracked = new string(value.AsSpan());
        Sources.Add(tracked, source);
        return tracked;
    }
}
