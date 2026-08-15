using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using IPhoneMirror.App.Services;
using IPhoneMirror.Shared.Networking;

namespace IPhoneMirror.App.Updater;

internal enum UpdateDownloadPhase
{
    Download,
    ConnectivityTest,
    ThroughputTest,
}

internal sealed record UpdateDownloadProgress(
    long BytesReceived, long? TotalBytes, double BytesPerSecond,
    UpdateDownloadPhase Phase = UpdateDownloadPhase.Download)
{
    internal double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : null;
}

internal sealed record DownloadedUpdate(
    ReleaseInfo Release, ReleaseAsset Asset, string Path, bool HashVerified,
    string? VerifiedSha256 = null);

internal static class DeploymentLayout
{
    private const string ExecutableName = "iPhoneMirror.exe";
    private const string AppPathsSubKeyPrefix =
        @"Software\Microsoft\Windows\CurrentVersion\App Paths\";

    internal static bool UsesSharedRuntime(string? baseDirectory = null,
        string? registeredExecutablePath = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(root, "iPhoneMirror.dll")) &&
            File.Exists(Path.Combine(root, "iPhoneMirror.deps.json")) &&
            File.Exists(Path.Combine(root, "iPhoneMirror.runtimeconfig.json")))
            return true;

        // Older Setup releases used a single-file executable, so they have no
        // managed runtime markers. Inno Setup registers the installed EXE in
        // App Paths; compare that path with the process directory to preserve
        // Setup updates across the single-file -> shared-runtime migration.
        var executablePath = Path.Combine(root, ExecutableName);
        return IsRegisteredInstall(executablePath, registeredExecutablePath);
    }

    internal static bool IsRegisteredInstall(string executablePath,
        string? registeredExecutablePath = null)
    {
        var expected = NormalizeExecutablePath(executablePath);
        if (expected is null) return false;
        if (registeredExecutablePath is not null)
            return PathsEqual(expected, registeredExecutablePath);
        var executableName = Path.GetFileName(expected);
        if (string.IsNullOrWhiteSpace(executableName)) return false;
        var appPathsSubKey = AppPathsSubKeyPrefix + executableName;

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(appPathsSubKey, writable: false);
                var registered = key?.GetValue(null) as string;
                if (registered is not null && PathsEqual(expected, registered))
                    return true;
            }
            catch (Exception error) when (error is IOException or
                                           UnauthorizedAccessException or
                                           System.Security.SecurityException)
            {
                // Portable copies and restricted environments may not allow
                // registry reads. The runtime-marker check remains sufficient.
                DiagnosticLogger.ExceptionOnce("deployment-registry", "updater",
                    "deployment_registry_probe_failed", error,
                    ("hive", hive), ("view", view));
            }
        }
        return false;
    }

    private static bool PathsEqual(string expected, string actual)
    {
        var normalized = NormalizeExecutablePath(actual);
        return normalized is not null &&
            string.Equals(expected, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = Environment.ExpandEnvironmentVariables(path.Trim());
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            value = endQuote > 1 ? value[1..endQuote] : value[1..];
        }
        try
        {
            return Path.GetFullPath(value.Trim()).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

internal sealed class GitHubReleaseClient : IDisposable
{
    private const long MaximumUpdateBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumReleaseListCharacters = 4 * 1024 * 1024;
    private const int MaximumReleaseNotesCharacters = 1024 * 1024;
    private const int MaximumChecksumCharacters = 1024 * 1024;
    private const int MirrorProbeBytes = 256 * 1024;
    private static readonly TimeSpan CandidatePingWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CandidateThroughputWindow =
        TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(30);
    private static readonly (string Name, Uri Uri)[] ReleaseEndpoints =
    [
        ("github-api", new Uri(
            "https://api.github.com/repos/RayrenSX/iPhoneMirror/releases?per_page=20")),
        ("github-raw", new Uri(
            "https://raw.githubusercontent.com/RayrenSX/iPhoneMirror/main/updates/releases.json")),
    ];
    // Snapshot of all 115 mirrors published by moretools.app/zh-CN/github-proxy
    // on 2026-08-15. The original trusted GitHub URL is appended separately.
    private static readonly string[] DownloadMirrorPrefixes =
    [
        "https://gh-proxy.net/",
        "https://github.cnxiaobai.com/",
        "https://hub.gitmirror.com/",
        "https://www.5555.cab/",
        "https://git.tangbai.cc/",
        "https://gh.ddlc.top/",
        "https://ghproxy.xiaopa.cc/",
        "https://ghproxy.cfd/",
        "https://ghproxy.cc/",
        "https://ghproxy.monkeyray.net/",
        "https://cf.ghproxy.cc/",
        "https://gitproxy.mrhjx.cn/",
        "https://gh.xxooo.cf/",
        "https://github.xxlab.tech/",
        "https://ghproxy.1888866.xyz/",
        "https://github.mlmle.cn/",
        "https://fastgit.cc/",
        "https://gh.1k.ink/",
        "https://ghproxy.net/",
        "https://github.boringhex.top/",
        "https://ghfast.top/",
        "https://y.whereisdoge.work/",
        "https://ghproxy.imciel.com/",
        "https://gh.jdck.fun/",
        "https://xiaomo-station.top/",
        "https://gh.monlor.com/",
        "https://g.blfrp.cn/",
        "https://gh.con.sh/",
        "https://gh.b52m.cn/",
        "https://github.dpik.top/",
        "https://github.geekery.cn/",
        "https://gh.halonice.com/",
        "https://github.limoruirui.com/",
        "https://git.yylx.win/",
        "https://github.tbedu.top/",
        "https://ghproxy.vansour.top/",
        "https://tvv.tw/",
        "https://ghproxy.xzhouqd.com/",
        "https://github-proxy.memory-echoes.cn/",
        "https://gh.catmak.name/",
        "https://hub.ddayh.com/",
        "https://github.ruojian.space/",
        "https://ghproxy.cxkpro.top/",
        "https://ghp.keleyaa.com/",
        "https://ghf.\u65e0\u540d\u6c0f.top/",
        "https://github-proxy.lixxing.top/",
        "https://gh.padao.fun/",
        "https://gp.871201.xyz/",
        "https://gh.wsmdn.dpdns.org/",
        "https://ggg.clwap.dpdns.org/",
        "https://gh-proxy.com/",
        "https://gh.dpik.top/",
        "https://gp.zkitefly.eu.org/",
        "https://gh.bugdey.us.kg/",
        "https://code-hub-hk.freexy.top/",
        "https://github.chenc.dev/",
        "https://ghfile.geekertao.top/",
        "https://kenyu.ggff.net/",
        "https://gh.nxnow.top/",
        "https://github.bullb.net/",
        "https://gitproxy.197545.xyz/",
        "https://gitproxy.127731.xyz/",
        "https://gitproxy1.127731.xyz/",
        "https://jiashu.1win.eu.org/",
        "https://ghproxy.mf-dust.dpdns.org/",
        "https://j.1lin.dpdns.org/",
        "https://gh.jasonzeng.dev/",
        "https://proxy.baguoyuyan.com/",
        "https://github.1ms.xx.kg/",
        "https://gh.198962.xyz/",
        "https://github.880824.xyz/",
        "https://ghps.cc/",
        "https://30006000.xyz/",
        "https://github.tianrld.top/",
        "https://getgit.love8yun.eu.org/",
        "https://github.788787.xyz/",
        "https://ghm.078465.xyz/",
        "https://github-proxy.com/",
        "https://proxy.yaoyaoling.net/",
        "https://ghproxy.sakuramoe.dev/",
        "https://ghproxy.053000.xyz/",
        "https://gh.chjina.com/",
        "https://git.zeas.cc/",
        "https://ghpxy.hwinzniej.top/",
        "https://gh.echofree.xyz/",
        "https://github.zzrbk.xyz/",
        "https://git.669966.xyz/",
        "https://github.ihnic.com/",
        "https://gh.996986.xyz/",
        "https://gh.idayer.com/",
        "https://github.ednovas.xyz/",
        "https://gh.chalin.tk/",
        "https://j.1win.ggff.net/",
        "https://github.lsdfxdk.nyc.mn/",
        "https://gh.aaa.team/",
        "https://github.crdz.eu.org/",
        "https://gh.shiina-rimo.cafe/",
        "https://ghproxy.mirror.skybyte.me/",
        "https://gh.llkk.cc/",
        "https://git.40609891.xyz/",
        "https://github.oterea.top/",
        "https://gh.noki.icu/",
        "https://gh.39.al/",
        "https://ghproxy.cn/",
        "https://down.npee.cn/",
        "https://github.kkproxy.dpdns.org/",
        "https://free.cn.eu.org/",
        "https://git.951959483.xyz/",
        "https://git.820828.xyz/",
        "https://ghproxy.fangkuai.fun/",
        "https://github.cn86.dev/",
        "https://github.zjzzy.cloudns.org/",
        "https://ghb.nilive.top/",
        "https://gitproxy.click/",
        "https://proxy.atoposs.com/",
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

    internal async Task<ReleaseInfo> EnrichReleaseNotesAsync(ReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        var notesUri = new Uri(
            $"https://raw.githubusercontent.com/RayrenSX/iPhoneMirror/{release.TagName}/docs/releases/{release.TagName}.md");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _httpClient.GetAsync(notesUri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                !finalUri.Host.Equals(notesUri.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The release notes endpoint redirected to an untrusted host.");
            response.EnsureSuccessStatusCode();
            var notes = await ReadReleaseNotesAsync(response.Content, timeout.Token);
            if (!string.IsNullOrWhiteSpace(notes))
                return release with { Body = notes.Trim() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           InvalidDataException)
        {
            DiagnosticLogger.Exception("updater", "release_notes_failed", error,
                ("release", release.TagName));
        }
        return release;
    }

    private static async Task<string> ReadReleaseNotesAsync(HttpContent content,
        CancellationToken cancellationToken) =>
        await ReadBoundedTextAsync(content, MaximumReleaseNotesCharacters,
            "The release notes are unexpectedly large.", cancellationToken);

    private async Task<string> ReadReleaseListAsync(Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _httpClient.GetAsync(endpoint,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update endpoint redirected to an untrusted host.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumReleaseListCharacters)
            throw new InvalidDataException("The update release list is unexpectedly large.");
        return await ReadBoundedTextAsync(response.Content,
            MaximumReleaseListCharacters, "The update release list is unexpectedly large.",
            timeout.Token);
    }

    internal async Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowMirrorFallback = true,
        bool? preferInstaller = null)
    {
        var useInstaller = preferInstaller ?? DeploymentLayout.UsesSharedRuntime();
        var asset = release.SelectAsset(useInstaller) ?? throw new InvalidOperationException(
            useInstaller
                ? "This release does not provide a Windows installer package."
                : "This release does not provide a portable ZIP package.");
        DiagnosticLogger.Info("updater", "asset_selected",
            ("deployment", useInstaller ? "installer" : "portable"),
            ("asset", asset.Name));
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
        var downloadCandidates = await RankDownloadCandidatesAsync(asset,
            allowMirrorFallback, progress, cancellationToken);
        foreach (var downloadUri in downloadCandidates)
        {
            TryDelete(partial);
            progress?.Report(new UpdateDownloadProgress(0,
                asset.Size > 0 ? asset.Size : null, 0));
            try
            {
                DiagnosticLogger.Info("updater", "download_begin",
                    ("release", release.TagName), ("asset", asset.Name),
                    ("bytes", asset.Size), ("endpoint", downloadUri.Host));
                using var stallTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stallTimeout.CancelAfter(DownloadStallTimeout);
                var segmentCount = await DownloadFileAsync(asset, downloadUri, partial, progress,
                    stallTimeout, stallTimeout.Token);
                stallTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                var verifiedSha256 = await VerifyAsync(release, asset, partial, allowMirrorFallback,
                    cancellationToken);
                File.Move(partial, destination, overwrite: true);
                DiagnosticLogger.Info("updater", "download_complete",
                    ("release", release.TagName), ("asset", asset.Name),
                    ("sha256_verified", true), ("endpoint", downloadUri.Host),
                    ("segments", segmentCount));
                return new DownloadedUpdate(release, asset, destination,
                    HashVerified: true, VerifiedSha256: verifiedSha256);
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
        if (!allowMirrorFallback || asset.Sha256 is null)
            return [asset.DownloadUri];
        var candidates = DownloadMirrorPrefixes.Select(prefix =>
            new Uri(prefix + asset.DownloadUri.AbsoluteUri, UriKind.Absolute)).ToList();
        candidates.Add(asset.DownloadUri);
        return candidates;
    }

    private async Task<IReadOnlyList<Uri>> RankDownloadCandidatesAsync(
        ReleaseAsset asset, bool allowMirrorFallback,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = BuildDownloadCandidates(asset, allowMirrorFallback);
        if (candidates.Count <= 1) return candidates;

        progress?.Report(new UpdateDownloadProgress(0, null, 0,
            UpdateDownloadPhase.ConnectivityTest));
        using var pingTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pingTimeout.CancelAfter(CandidatePingWindow);
        var pingTasks = candidates.Select(uri =>
            PingDownloadCandidateAsync(uri, pingTimeout.Token)).ToArray();
        var pings = await Task.WhenAll(pingTasks);
        cancellationToken.ThrowIfCancellationRequested();
        var successfulPings = pings
            .Where(result => result is not null)
            .Select(result => result!)
            .ToArray();
        var reachable = successfulPings.Select(result => result.Uri).ToArray();
        var fastestPing = successfulPings
            .OrderBy(result => result.Milliseconds)
            .FirstOrDefault();
        DiagnosticLogger.Info("updater", "download_candidate_ping_complete",
            ("candidates", candidates.Count), ("reachable", reachable.Length),
            ("fastest", fastestPing?.Uri.Host ?? "none"),
            ("fastest_ms", Math.Round(fastestPing?.Milliseconds ?? 0)));
        if (reachable.Length == 0) return [asset.DownloadUri];

        progress?.Report(new UpdateDownloadProgress(0, null, 0,
            UpdateDownloadPhase.ThroughputTest));
        using var throughputTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        throughputTimeout.CancelAfter(CandidateThroughputWindow);
        var probeTasks = reachable.Select(uri =>
            ProbeDownloadCandidateAsync(asset, uri,
                throughputTimeout.Token)).ToArray();
        var probes = await Task.WhenAll(probeTasks);
        cancellationToken.ThrowIfCancellationRequested();

        var available = probes
            .Where(probe => probe is not null)
            .Select(probe => probe!)
            .OrderByDescending(probe => probe.BytesPerSecond)
            .ToArray();
        DiagnosticLogger.Info("updater", "download_throughput_probe_complete",
            ("reachable", reachable.Length), ("measured", available.Length),
            ("selected", available.FirstOrDefault()?.Uri.Host ?? "unmeasured"),
            ("probe_bytes", MirrorProbeBytes));
        return available.Length > 0
            ? available.Select(probe => probe.Uri).ToArray()
            : successfulPings
                .OrderBy(result => result.Milliseconds)
                .Select(result => result.Uri)
                .ToArray();
    }

    private async Task<ReachableDownloadCandidate?> PingDownloadCandidateAsync(
        Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var pingUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/",
                UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Head, pingUri);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                !finalUri.Host.Equals(pingUri.Host,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The candidate ping redirected to an untrusted host.");
            return new ReachableDownloadCandidate(uri,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           IOException or InvalidDataException)
        {
            return null;
        }
    }

    private async Task<DownloadCandidateProbe?> ProbeDownloadCandidateAsync(
        ReleaseAsset asset, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var probeEnd = asset.Size > 0
                ? Math.Min(asset.Size - 1, MirrorProbeBytes - 1)
                : MirrorProbeBytes - 1;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("identity"));
            request.Headers.Range = new RangeHeaderValue(0, probeEnd);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureTrustedDownloadResponse(uri, response);
            response.EnsureSuccessStatusCode();

            var expectedSampleBytes = checked((int)(probeEnd + 1));
            int received;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                if (asset.Size > 0 && response.Content.Headers.ContentLength is > 0 and
                    var contentLength && contentLength != asset.Size)
                    throw new InvalidDataException(
                        "The mirror probe size does not match the release metadata.");
                received = await ReadProbePayloadAsync(response.Content,
                    expectedSampleBytes, requireExactLength: false,
                    cancellationToken);
            }
            else if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != 0 || range.To != probeEnd ||
                    range.Length is not { } totalBytes || totalBytes <= probeEnd ||
                    asset.Size > 0 && totalBytes != asset.Size)
                    throw new InvalidDataException(
                        "The mirror probe returned an invalid content range.");
                received = await ReadProbePayloadAsync(response.Content,
                    expectedSampleBytes, requireExactLength: true,
                    cancellationToken);
            }
            else
            {
                throw new InvalidDataException(
                    "The mirror probe returned an unexpected status.");
            }
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            return new DownloadCandidateProbe(uri, received / seconds);
        }
        catch (Exception error) when (error is HttpRequestException or
                                           OperationCanceledException or
                                           IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<int> ReadProbePayloadAsync(HttpContent content,
        int expectedBytes, bool requireExactLength,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength &&
            (requireExactLength && contentLength != expectedBytes ||
             !requireExactLength && contentLength < expectedBytes))
            throw new InvalidDataException(
                "The mirror probe returned an unexpected content length.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(expectedBytes, 64 * 1024));
        try
        {
            var received = 0;
            while (received < expectedBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, expectedBytes - received)), cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException(
                        "The mirror probe ended before its requested range completed.");
                received = checked(received + count);
            }
            if (requireExactLength &&
                await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new InvalidDataException(
                    "The mirror probe exceeded its requested range.");
            return received;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record ReachableDownloadCandidate(Uri Uri, double Milliseconds);
    private sealed record DownloadCandidateProbe(Uri Uri, double BytesPerSecond);

    private async Task<int> DownloadFileAsync(ReleaseAsset asset, Uri downloadUri,
        string destination, IProgress<UpdateDownloadProgress>? progress,
        CancellationTokenSource stallTimeout,
        CancellationToken cancellationToken)
    {
        var downloadProgress = new InlineProgress<SegmentedDownloadProgress>(value =>
            {
                stallTimeout.CancelAfter(DownloadStallTimeout);
                progress?.Report(new UpdateDownloadProgress(value.BytesReceived,
                    value.TotalBytes, value.BytesPerSecond));
            });
        var result = await SegmentedHttpDownloader.DownloadAsync(_httpClient,
            downloadUri, destination, new SegmentedDownloadOptions(
                MaximumUpdateBytes,
                asset.Size > 0 ? asset.Size : null),
            finalUri => IsTrustedDownloadFinalUri(downloadUri, finalUri),
            downloadProgress, cancellationToken);
        return result.SegmentCount;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private async Task<string> VerifyAsync(ReleaseInfo release, ReleaseAsset asset,
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
        return actual;
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
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                EnsureTrustedDownloadResponse(uri, response);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumChecksumCharacters)
                    throw new InvalidDataException(
                        "The checksum manifest is unexpectedly large.");
                var bytes = await ReadBoundedBytesAsync(response.Content,
                    MaximumChecksumCharacters,
                    "The checksum manifest is unexpectedly large.", timeout.Token);
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

    private static async Task<string> ReadBoundedTextAsync(HttpContent content,
        int maximumCharacters, string errorMessage, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var remaining = maximumCharacters - result.Length;
            var count = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken);
            if (count == 0) return result.ToString();
            if (count > remaining) throw new InvalidDataException(errorMessage);
            result.Append(buffer, 0, count);
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content,
        int maximumBytes, string errorMessage, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - checked((int)output.Length);
            var count = await input.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken);
            if (count == 0) return output.ToArray();
            if (count > remaining) throw new InvalidDataException(errorMessage);
            output.Write(buffer, 0, count);
        }
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
            foreach (var file in EnumerateCacheFiles(root, "*.download"))
                TryDelete(file);
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException or
                                      System.Security.SecurityException)
        {
            DiagnosticLogger.Exception("updater", "interrupted_cleanup_failed", error);
        }
    }

    internal static LogCleanupResult CleanupOldDownloads(string? root = null,
        bool includeCompleted = false)
    {
        root ??= Path.Combine(UpdateSettingsStore.UserDataDirectory, "Updates");
        if (!IsSafeCacheRoot(root)) return default;
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;
        foreach (var file in EnumerateCacheFiles(root, "*"))
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
                         SafeCacheEnumerationOptions).OrderByDescending(value => value.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException or
                                      System.Security.SecurityException)
        {
            ++skipped;
            DiagnosticLogger.Exception("updater", "cache_directory_cleanup_failed", error);
        }
        return new LogCleanupResult(deleted, bytes, skipped);
    }

    private static readonly EnumerationOptions SafeCacheEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static IEnumerable<string> EnumerateCacheFiles(string root, string pattern)
    {
        if (!IsSafeCacheRoot(root)) return [];
        return Directory.EnumerateFiles(root, pattern, SafeCacheEnumerationOptions);
    }

    private static bool IsSafeCacheRoot(string root)
    {
        if (!Directory.Exists(root)) return false;
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return true;
        DiagnosticLogger.Info("updater", "cache_root_reparse_point_rejected");
        return false;
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
