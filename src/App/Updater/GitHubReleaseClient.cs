using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.Shared.Networking;

namespace IPhoneMirror.App.Updater;

internal sealed record UpdateDownloadProgress(
    long BytesReceived, long? TotalBytes, double BytesPerSecond)
{
    internal double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : null;
}

internal sealed record DownloadedUpdate(
    ReleaseInfo Release, ReleaseAsset Asset, string Path, bool HashVerified);

internal sealed class GitHubReleaseClient : IDisposable
{
    private const long MaximumUpdateBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumReleaseListCharacters = 4 * 1024 * 1024;
    private const int MaximumChecksumCharacters = 1024 * 1024;
    private static readonly (string Name, Uri Uri)[] ReleaseEndpoints =
    [
        ("github-api", new Uri(
            "https://api.github.com/repos/RayrenSX/iPhoneMirror/releases?per_page=20")),
        ("github-raw", new Uri(
            "https://raw.githubusercontent.com/RayrenSX/iPhoneMirror/main/updates/releases.json")),
    ];
    private static readonly string[] DownloadMirrorPrefixes =
    [
        "https://gh-proxy.org/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ];
    private static readonly HashSet<string> OfficialDownloadHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _downloadRoot;

    internal GitHubReleaseClient(HttpClient? httpClient = null, string? downloadRoot = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("iPhoneMirror-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
        _downloadRoot = downloadRoot ?? Path.Combine(
            UpdateSettingsStore.UserDataDirectory, "Updates");
    }

    internal async Task<ReleaseInfo?> GetLatestAsync(UpdateSettings settings,
        CancellationToken cancellationToken = default)
    {
        DiagnosticLogger.Info("updater", "release_check_begin",
            ("stable", settings.NotifyStableReleases),
            ("prerelease", settings.NotifyPrereleaseReleases),
            ("mirror_fallback", settings.AllowMirrorFallback));
        Exception? lastError = null;
        var endpointCount = settings.AllowMirrorFallback
            ? ReleaseEndpoints.Length
            : 1;
        for (var index = 0; index < endpointCount; ++index)
        {
            var endpoint = ReleaseEndpoints[index];
            try
            {
                var json = await ReadReleaseListAsync(endpoint.Uri, cancellationToken);
                var release = ReleaseParser.ParseLatest(json,
                    settings.NotifyStableReleases,
                    settings.NotifyPrereleaseReleases);
                DiagnosticLogger.Info("updater", "release_check_complete",
                    ("release", release?.TagName ?? "none"),
                    ("endpoint", endpoint.Name));
                return release;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           InvalidDataException or JsonException)
            {
                lastError = error;
                DiagnosticLogger.Exception("updater", "release_endpoint_failed",
                    error, ("endpoint", endpoint.Name));
            }
        }

        throw new HttpRequestException(
            "All configured update endpoints are unavailable.", lastError);
    }

    private async Task<string> ReadReleaseListAsync(Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _httpClient.GetAsync(endpoint,
            HttpCompletionOption.ResponseContentRead, timeout.Token);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update endpoint redirected to an untrusted host.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            throw new InvalidDataException("The update release list is unexpectedly large.");
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        if (json.Length > MaximumReleaseListCharacters)
            throw new InvalidDataException("The update release list is unexpectedly large.");
        return json;
    }

    internal async Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowMirrorFallback = true)
    {
        var asset = release.PreferredAsset ?? throw new InvalidOperationException(
            "This release does not provide a Windows installer or ZIP package.");
        var fileName = Path.GetFileName(asset.Name);
        if (!string.Equals(fileName, asset.Name, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("The update asset has an unsafe file name.");
        if (asset.Size > MaximumUpdateBytes)
            throw new InvalidDataException("The update package is unexpectedly large.");
        var directory = Path.Combine(_downloadRoot,
            SanitizeDirectoryName(release.TagName));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        var partial = destination + ".download";
        TryDelete(partial);
        TryDelete(destination);
        Exception? lastError = null;
        foreach (var downloadUri in BuildDownloadCandidates(asset, allowMirrorFallback))
        {
            TryDelete(partial);
            progress?.Report(new UpdateDownloadProgress(0,
                asset.Size > 0 ? asset.Size : null, 0));
            try
            {
                DiagnosticLogger.Info("updater", "download_begin",
                    ("release", release.TagName), ("asset", asset.Name),
                    ("bytes", asset.Size), ("endpoint", downloadUri.Host));
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                var segmentCount = await DownloadFileAsync(asset, downloadUri, partial, progress,
                    timeout.Token);
                await VerifyAsync(release, asset, partial, allowMirrorFallback,
                    timeout.Token);
                File.Move(partial, destination, overwrite: true);
                DiagnosticLogger.Info("updater", "download_complete",
                    ("release", release.TagName), ("asset", asset.Name),
                    ("sha256_verified", true), ("endpoint", downloadUri.Host),
                    ("segments", segmentCount));
                return new DownloadedUpdate(release, asset, destination,
                    HashVerified: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partial);
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           EndOfStreamException or
                                           InvalidDataException)
            {
                lastError = error;
                DiagnosticLogger.Exception("updater", "download_endpoint_failed",
                    error, ("release", release.TagName), ("asset", asset.Name),
                    ("endpoint", downloadUri.Host));
            }
        }

        TryDelete(partial);
        if (lastError is InvalidDataException invalidData) throw invalidData;
        throw new HttpRequestException(
            "All configured update download endpoints are unavailable.", lastError);
    }

    internal static IReadOnlyList<Uri> BuildDownloadCandidates(
        ReleaseAsset asset, bool allowMirrorFallback)
    {
        if (!ReleaseParser.IsTrustedGitHubAssetUri(asset.DownloadUri))
            throw new InvalidDataException("The update asset URL is not trusted.");
        var candidates = new List<Uri> { asset.DownloadUri };
        if (!allowMirrorFallback || asset.Sha256 is null) return candidates;
        candidates.AddRange(DownloadMirrorPrefixes.Select(prefix =>
            new Uri(prefix + asset.DownloadUri.AbsoluteUri, UriKind.Absolute)));
        return candidates;
    }

    private async Task<int> DownloadFileAsync(ReleaseAsset asset, Uri downloadUri,
        string destination, IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var downloadProgress = progress is null
            ? null
            : new Progress<SegmentedDownloadProgress>(value =>
                progress.Report(new UpdateDownloadProgress(value.BytesReceived,
                    value.TotalBytes, value.BytesPerSecond)));
        var result = await SegmentedHttpDownloader.DownloadAsync(_httpClient,
            downloadUri, destination, new SegmentedDownloadOptions(
                MaximumUpdateBytes,
                asset.Size > 0 ? asset.Size : null),
            finalUri => IsTrustedDownloadFinalUri(downloadUri, finalUri),
            downloadProgress, cancellationToken);
        return result.SegmentCount;
    }

    private async Task VerifyAsync(ReleaseInfo release, ReleaseAsset asset,
        string path, bool allowMirrorFallback, CancellationToken cancellationToken)
    {
        var expected = asset.Sha256;
        if (expected is null)
        {
            var checksumAsset = release.ChecksumAsset ??
                throw new InvalidDataException(
                    "The release does not provide a trusted SHA256 value.");
            var manifest = await ReadChecksumManifestAsync(checksumAsset,
                allowMirrorFallback, cancellationToken);
            expected = ReleaseParser.FindExpectedSha256(manifest, asset.Name) ??
                throw new InvalidDataException(
                    $"SHA256SUMS.txt does not contain {asset.Name}.");
        }
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed SHA256 verification.");
    }

    private async Task<string> ReadChecksumManifestAsync(ReleaseAsset checksumAsset,
        bool allowMirrorFallback, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var uri in BuildDownloadCandidates(checksumAsset,
                     allowMirrorFallback))
        {
            try
            {
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _httpClient.GetAsync(uri,
                    HttpCompletionOption.ResponseContentRead, timeout.Token);
                EnsureTrustedDownloadResponse(uri, response);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumChecksumCharacters)
                    throw new InvalidDataException(
                        "The checksum manifest is unexpectedly large.");
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (bytes.Length > MaximumChecksumCharacters)
                    throw new InvalidDataException(
                        "The checksum manifest is unexpectedly large.");
                if (checksumAsset.Size > 0 && bytes.LongLength != checksumAsset.Size)
                    throw new InvalidDataException(
                        "The checksum manifest size does not match the release metadata.");
                if (checksumAsset.Sha256 is not null)
                {
                    var actual = Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant();
                    if (!actual.Equals(checksumAsset.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "The checksum manifest failed SHA256 verification.");
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           InvalidDataException)
            {
                lastError = error;
            }
        }

        if (lastError is InvalidDataException invalidData) throw invalidData;
        throw new HttpRequestException(
            "All checksum download endpoints are unavailable.", lastError);
    }

    private async Task<HttpResponseMessage> GetResponseHeadersAsync(Uri uri,
        CancellationToken cancellationToken)
    {
        using var headerTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        return await _httpClient.GetAsync(uri,
            HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
    }

    private static void EnsureTrustedDownloadResponse(Uri requestedUri,
        HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri ??
            throw new InvalidDataException("The update response has no final URL.");
        if (!IsTrustedDownloadFinalUri(requestedUri, finalUri))
            throw new InvalidDataException(
                "The update download redirected to an untrusted host.");
    }

    private static bool IsTrustedDownloadFinalUri(Uri requestedUri, Uri finalUri) =>
        finalUri.Scheme == Uri.UriSchemeHttps &&
        (requestedUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? OfficialDownloadHosts.Contains(finalUri.Host)
            : finalUri.Host.Equals(requestedUri.Host,
                StringComparison.OrdinalIgnoreCase));

    private static string SanitizeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "release" : sanitized;
    }

    internal static void CleanupInterruptedDownloads(string? root = null)
    {
        root ??= Path.Combine(UpdateSettingsStore.UserDataDirectory, "Updates");
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.download",
                         SearchOption.AllDirectories))
                TryDelete(file);
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("updater", "interrupted_cleanup_failed", error);
        }
    }

    internal static LogCleanupResult CleanupOldDownloads(string? root = null,
        bool includeCompleted = false)
    {
        root ??= Path.Combine(UpdateSettingsStore.UserDataDirectory, "Updates");
        if (!Directory.Exists(root)) return default;
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!includeCompleted && !file.EndsWith(".download",
                    StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                ++deleted;
                bytes += length;
            }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                ++skipped;
                DiagnosticLogger.Exception("updater", "cached_file_cleanup_failed",
                    error, ("file", Path.GetFileName(file)));
            }
        }
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*",
                         SearchOption.AllDirectories).OrderByDescending(value => value.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            ++skipped;
            DiagnosticLogger.Exception("updater", "cache_directory_cleanup_failed", error);
        }
        return new LogCleanupResult(deleted, bytes, skipped);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("updater", "file_delete_failed", error,
                ("file", Path.GetFileName(path)));
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
