using System.IO;
using System.Text.Json;

namespace IPhoneMirror.App.Updater;

internal sealed record ReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256 = null);

internal sealed record ReleaseInfo(
    string TagName,
    string Name,
    string Body,
    DateTimeOffset PublishedAt,
    SemanticVersion Version,
    bool IsPrerelease,
    ReleaseAsset? InstallerAsset,
    ReleaseAsset? ZipAsset,
    ReleaseAsset? ChecksumAsset)
{
    internal ReleaseAsset? PreferredAsset => InstallerAsset ?? ZipAsset;

    internal ReleaseAsset? SelectAsset(bool preferInstaller) => preferInstaller
        ? InstallerAsset
        : ZipAsset;
}

internal static class ReleaseParser
{
    internal static ReleaseInfo? ParseLatest(string json, bool includeStable,
        bool includePrerelease)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned an invalid release list.");
        var releases = new List<ReleaseInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (GetBoolean(element, "draft")) continue;
            var prerelease = GetBoolean(element, "prerelease");
            if ((prerelease && !includePrerelease) || (!prerelease && !includeStable))
                continue;
            var tag = GetRequiredString(element, "tag_name");
            if (!SemanticVersion.TryParse(tag, out var version)) continue;
            var assets = ParseAssets(element);
            var installer = assets
                .Where(asset => IsInstallerExecutable(asset.Name))
                .OrderByDescending(InstallerScore)
                .FirstOrDefault();
            var zip = assets
                .Where(asset => asset.Name.EndsWith(".zip",
                    StringComparison.OrdinalIgnoreCase) &&
                    !asset.Name.Contains("source", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ZipScore)
                .FirstOrDefault();
            var checksum = assets.FirstOrDefault(asset =>
                asset.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                asset.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase));
            var published = DateTimeOffset.TryParse(
                GetOptionalString(element, "published_at"), out var parsedPublished)
                ? parsedPublished : DateTimeOffset.MinValue;
            releases.Add(new ReleaseInfo(tag,
                GetOptionalString(element, "name") ?? tag,
                GetOptionalString(element, "body") ?? string.Empty,
                published, version, prerelease, installer, zip, checksum));
        }
        return releases.OrderByDescending(release => release.Version).FirstOrDefault();
    }

    internal static string? FindExpectedSha256(string manifest, string assetName)
    {
        foreach (var rawLine in manifest.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOfAny([' ', '\t']);
            if (separator != 64) continue;
            var hash = line[..separator];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[separator..].TrimStart().TrimStart('*').Trim();
            if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static IReadOnlyList<ReleaseAsset> ParseAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
            return [];
        var assets = new List<ReleaseAsset>();
        foreach (var element in assetsElement.EnumerateArray())
        {
            var name = GetOptionalString(element, "name");
            var url = GetOptionalString(element, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) ||
                Path.GetFileName(name) != name ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !IsTrustedGitHubAssetUri(uri) ||
                !Path.GetFileName(uri.LocalPath).Equals(name,
                    StringComparison.Ordinal))
                continue;
            var size = element.TryGetProperty("size", out var sizeElement) &&
                sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            var digest = NormalizeSha256(GetOptionalString(element, "digest"));
            assets.Add(new ReleaseAsset(name, uri, Math.Max(0, size), digest));
        }
        return assets;
    }

    internal static bool IsTrustedGitHubAssetUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            "/RayrenSX/iPhoneMirror/releases/download/",
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))
            return null;
        var hash = digest[prefix.Length..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant()
            : null;
    }

    private static int InstallerScore(ReleaseAsset asset)
    {
        var name = asset.Name;
        var score = 100;
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) score += 80;
        if (name.Contains("installer", StringComparison.OrdinalIgnoreCase)) score += 60;
        if (name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("win64", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (name.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("x86", StringComparison.OrdinalIgnoreCase)) score -= 100;
        return score;
    }

    private static bool IsInstallerExecutable(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        (name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("installer", StringComparison.OrdinalIgnoreCase));

    private static int ZipScore(ReleaseAsset asset)
    {
        var score = 0;
        if (asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (asset.Name.Contains("latest", StringComparison.OrdinalIgnoreCase)) score -= 5;
        if (asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase)) score -= 100;
        return score;
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        GetOptionalString(element, name) ??
        throw new InvalidDataException($"GitHub release is missing {name}.");

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.True;
}
