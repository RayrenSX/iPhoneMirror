namespace IPhoneMirror.App.Services;

internal static class MediaSourceClassifier
{
    internal static bool IsLikelyLive(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            source.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            return true;

        if (source.Segments.Any(segment => segment.Trim('/').Equals("live",
                StringComparison.OrdinalIgnoreCase) ||
            segment.Trim('/').Equals("hls", StringComparison.OrdinalIgnoreCase) ||
            segment.Trim('/').Equals("stream", StringComparison.OrdinalIgnoreCase) ||
            segment.Trim('/').Equals("playlist", StringComparison.OrdinalIgnoreCase)))
            return true;

        var query = Uri.UnescapeDataString(source.Query);
        return query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("format=hls", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("format=m3u8", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase);
    }
}
