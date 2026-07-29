using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IPhoneMirror.App.Services;

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
    private static readonly Uri ReleasesUri = new(
        "https://api.github.com/repos/RayrenSX/iPhoneMirror/releases?per_page=20");
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
            ("prerelease", settings.NotifyPrereleaseReleases));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _httpClient.GetAsync(ReleasesUri,
            HttpCompletionOption.ResponseContentRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            throw new InvalidDataException("GitHub returned an unexpectedly large release list.");
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        if (json.Length > MaximumReleaseListCharacters)
            throw new InvalidDataException("GitHub returned an unexpectedly large release list.");
        var release = ReleaseParser.ParseLatest(json, settings.NotifyStableReleases,
            settings.NotifyPrereleaseReleases);
        DiagnosticLogger.Info("updater", "release_check_complete",
            ("release", release?.TagName ?? "none"));
        return release;
    }

    internal async Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
        try
        {
            DiagnosticLogger.Info("updater", "download_begin",
                ("release", release.TagName), ("asset", asset.Name),
                ("bytes", asset.Size));
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            await DownloadFileAsync(asset, partial, progress, timeout.Token);
            var verified = await VerifyIfAvailableAsync(release, asset, partial,
                timeout.Token);
            File.Move(partial, destination, overwrite: true);
            DiagnosticLogger.Info("updater", "download_complete",
                ("release", release.TagName), ("asset", asset.Name),
                ("sha256_verified", verified));
            return new DownloadedUpdate(release, asset, destination, verified);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "download_failed", error,
                ("release", release.TagName), ("asset", asset.Name));
            TryDelete(partial);
            throw;
        }
    }

    private async Task DownloadFileAsync(ReleaseAsset asset, string destination,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(asset.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ??
            (asset.Size > 0 ? asset.Size : null);
        if (total > MaximumUpdateBytes)
            throw new InvalidDataException("The update package is unexpectedly large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received = checked(received + count);
            if (received > MaximumUpdateBytes)
                throw new InvalidDataException("The update package exceeded the size limit.");
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            progress?.Report(new UpdateDownloadProgress(received, total,
                received / seconds));
        }
        await output.FlushAsync(cancellationToken);
        if (total is > 0 && received != total)
            throw new EndOfStreamException(
                $"The update download is incomplete ({received} of {total} bytes).");
    }

    private async Task<bool> VerifyIfAvailableAsync(ReleaseInfo release,
        ReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        if (release.ChecksumAsset is null) return false;
        using var response = await _httpClient.GetAsync(release.ChecksumAsset.DownloadUri,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadAsStringAsync(cancellationToken);
        if (manifest.Length > MaximumChecksumCharacters)
            throw new InvalidDataException("The checksum manifest is unexpectedly large.");
        var expected = ReleaseParser.FindExpectedSha256(manifest, asset.Name) ??
            throw new InvalidDataException(
                $"SHA256SUMS.txt does not contain {asset.Name}.");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed SHA256 verification.");
        return true;
    }

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
