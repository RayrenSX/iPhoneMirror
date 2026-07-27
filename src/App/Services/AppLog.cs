using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Formatting helpers for application-owned log entries. Native logs may still
/// contain protocol diagnostics, but UI-originated entries must not add a raw
/// device serial, media URL, local path, or multiline exception to that file.
/// </summary>
internal static class AppLog
{
    private const int MaximumEventCharacters = 2048;
    private const int MaximumEventNameCharacters = 64;
    private const int MaximumKeyCharacters = 48;
    private const int MaximumFieldCharacters = 384;
    private static readonly HashSet<string> KnownMediaExtensions = new(
        ["m3u8", "mp4", "m4v", "mov", "ts", "mkv", "webm", "avi",
         "mp3", "m4a", "aac", "wav", "flac", "unknown"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex UrlPattern =
        new(@"\b(?:https?|rtsp|file)://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeaderCredentialPattern =
        new(@"(?<key>\b(?:authorization|proxy-authorization|cookie|set-cookie)\b)\s*:\s*[^\r\n]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SensitiveAssignmentPattern =
        new(@"(?<key>\b(?:authorization|proxy[_-]?authorization|cookie|set[_-]?cookie)\b)\s*=\s*[^\r\n]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BearerCredentialPattern =
        new(@"(?<scheme>\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CredentialKeyValuePattern =
        new(@"(?<key>\b(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|api[_-]?key|secret|client[_-]?secret)\b)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;&]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HostValuePattern =
        new(@"(?<key>\b(?:host|hostname|server|endpoint)\b)\s*[:=]\s*[^\s,;&]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WirelessIdPattern =
        new(@"\bairplay://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppleDevicePattern =
        new(@"(?<![A-Za-z0-9])(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{16,32})(?![A-Za-z0-9])",
            RegexOptions.Compiled);
    private static readonly Regex HexDevicePattern =
        new(@"(?<![A-Za-z0-9])(?<id>[0-9a-fA-F]{20,64})(?![A-Za-z0-9])",
            RegexOptions.Compiled);
    private static readonly Regex MacAddressPattern =
        new(@"(?<![0-9A-Fa-f])(?<id>(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})(?![0-9A-Fa-f])",
            RegexOptions.Compiled);
    private static readonly Regex Ipv4AddressPattern =
        new(@"(?<![0-9.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]{1,5})?(?![0-9.])",
            RegexOptions.Compiled);
    private static readonly Regex WindowsPathPattern =
        new(@"(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\)[^\s]+",
            RegexOptions.Compiled);
    private static readonly Regex UnixUserPathPattern =
        new(@"(?<![A-Za-z0-9])/(?:Users|home|var/folders|tmp)/[^\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string Device(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "none";
        if (string.Equals(identifier, "media-cast://active",
                StringComparison.OrdinalIgnoreCase)) return "media-cast";
        if (identifier.StartsWith("airplay://", StringComparison.OrdinalIgnoreCase))
            return $"wireless#{StableToken(identifier)}";
        return $"device#{StableToken(identifier)}";
    }

    internal static string Handle(ulong handle) => handle == 0
        ? "none" : $"h{handle:x}";

    internal static string MediaSource(Uri? source)
    {
        if (source is null) return "none";
        if (!source.IsAbsoluteUri) return "relative/unknown?query=False";
        try
        {
            var extension = Path.GetExtension(source.AbsolutePath).TrimStart('.');
            if (extension.Length == 0) extension = "unknown";
            if (!KnownMediaExtensions.Contains(extension)) extension = "other";
            return $"{source.Scheme.ToLowerInvariant()}/{extension.ToLowerInvariant()}" +
                $"?query={source.Query.Length != 0}";
        }
        catch (Exception)
        {
            return "invalid/unknown?query=False";
        }
    }

    internal static string Error(Exception? error) => error is null
        ? "none" : Error(error.Message, error.GetType().Name);

    internal static string Error(string? message, string? type = null)
    {
        var sanitized = Sanitize(message);
        if (sanitized.Length > 240) sanitized = sanitized[..240] + "...";
        return string.IsNullOrWhiteSpace(type) ? sanitized : $"{type}:{sanitized}";
    }

    /// <summary>Removes sensitive values from a human-readable UI message.</summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sanitized = HeaderCredentialPattern.Replace(value,
            match => $"{match.Groups["key"].Value}=<redacted>");
        sanitized = SensitiveAssignmentPattern.Replace(sanitized,
            match => $"{match.Groups["key"].Value}=<redacted>");
        sanitized = BearerCredentialPattern.Replace(sanitized,
            match => $"{match.Groups["scheme"].Value}<redacted>");
        sanitized = CredentialKeyValuePattern.Replace(sanitized,
            match => $"{match.Groups["key"].Value}=<redacted>");
        sanitized = UrlPattern.Replace(sanitized,
            match => $"<media-url#{StableToken(match.Value)}>");
        sanitized = WirelessIdPattern.Replace(sanitized,
            match => $"wireless#{StableToken(match.Value)}");
        sanitized = HostValuePattern.Replace(sanitized,
            match => $"{match.Groups["key"].Value}=<host>");
        sanitized = WindowsPathPattern.Replace(sanitized, "<path>");
        sanitized = UnixUserPathPattern.Replace(sanitized, "<path>");
        sanitized = AppleDevicePattern.Replace(sanitized,
            match => $"device#{StableToken(match.Groups["id"].Value)}");
        sanitized = HexDevicePattern.Replace(sanitized,
            match => $"device#{StableToken(match.Groups["id"].Value)}");
        sanitized = MacAddressPattern.Replace(sanitized,
            match => $"host#{StableToken(match.Groups["id"].Value)}");
        sanitized = Ipv4AddressPattern.Replace(sanitized, "<host>");
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return sanitized;
    }

    internal static string Event(string name, params object?[] fields)
    {
        if (fields is null) return Limit(SanitizeKey(name), MaximumEventNameCharacters);
        var builder = new StringBuilder(Limit(SanitizeKey(name),
            MaximumEventNameCharacters));
        foreach (var field in fields)
        {
            if (field is not ITuple tuple || tuple.Length != 2) continue;
            var key = tuple[0]?.ToString();
            var value = tuple[1];
            if (string.IsNullOrWhiteSpace(key)) continue;
            builder.Append(' ').Append(Limit(SanitizeKey(key), MaximumKeyCharacters))
                .Append('=').Append(Limit(
                    SanitizeToken(value?.ToString() ?? "none"),
                    MaximumFieldCharacters));
            if (builder.Length <= MaximumEventCharacters) continue;
            var prefixLength = MaximumEventCharacters - " truncated=true".Length;
            if (prefixLength > 0 && char.IsHighSurrogate(builder[prefixLength - 1]))
                --prefixLength;
            builder.Length = prefixLength;
            builder.Append(" truncated=true");
            break;
        }
        return builder.ToString();
    }

    internal static string Message(string? value) =>
        Limit(Sanitize(value), MaximumEventCharacters);

    private static string SanitizeKey(string value)
    {
        var sanitized = Sanitize(value);
        if (sanitized.Length == 0) return "event";
        var builder = new StringBuilder(sanitized.Length);
        foreach (var character in sanitized)
            builder.Append(char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.' ? character : '_');
        return builder.ToString();
    }

    private static string SanitizeToken(string value)
    {
        var sanitized = Sanitize(value);
        if (sanitized.Length == 0) return "none";
        var builder = new StringBuilder(sanitized.Length);
        foreach (var character in sanitized)
            builder.Append(char.IsWhiteSpace(character) || char.IsControl(character)
                ? '_' : character);
        return builder.ToString();
    }

    private static string Limit(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var prefixLength = maximum - 3;
        if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
            --prefixLength;
        return value[..prefixLength] + "...";
    }

    private static string StableToken(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 5)).ToLowerInvariant();
    }
}
