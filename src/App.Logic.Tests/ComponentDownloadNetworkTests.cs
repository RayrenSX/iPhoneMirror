using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

internal static class ComponentDownloadNetworkTests
{
    private sealed class ProgressSink(Action<UpdateDownloadProgress> action) : IProgress<UpdateDownloadProgress>
    { public void Report(UpdateDownloadProgress value) => action(value); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(string archive, string metadata, string output)
    {
        Directory.CreateDirectory(output);
        var descriptor = JsonSerializer.Deserialize<ComponentDescriptor>(File.ReadAllText(metadata),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        await using var server = new DownloadServer(await File.ReadAllBytesAsync(archive));
        foreach (var proxy in new[] { false, true })
        {
            using var http = server.CreateClient(proxy);
            for (var round = 1; round <= 3; round++)
            {
                var cache = Path.Combine(output, $"{(proxy ? "proxy" : "direct")}-{round}");
                using var client = new GitHubReleaseClient(http, Path.Combine(cache, "Downloads"));
                var phases = new ConcurrentQueue<UpdateDownloadPhase>();
                var proxyConnections = server.ProxyConnections;
                var clock = Stopwatch.StartNew();
                await UxPlayComponent.InstallAsync(descriptor, cache, client,
                    new ProgressSink(p => phases.Enqueue(p.Phase)), () => { }, CancellationToken.None);
                Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is not null, "Installed tree must verify");
                Check(phases.Contains(UpdateDownloadPhase.ConnectivityTest) &&
                    phases.Contains(UpdateDownloadPhase.ThroughputTest) &&
                    phases.Contains(UpdateDownloadPhase.Verification), "Must ping, measure, download and verify");
                var phaseOrder = phases.Distinct().ToArray();
                Check(phaseOrder.SequenceEqual(new[] { UpdateDownloadPhase.ConnectivityTest, UpdateDownloadPhase.ThroughputTest,
                    UpdateDownloadPhase.Download, UpdateDownloadPhase.Verification }), "Probe/download/verification order");
                Check(proxy ? server.ProxyConnections > proxyConnections : server.ProxyConnections == proxyConnections,
                    "Per-client proxy selection must determine actual CONNECT traffic");
                var requests = server.Requests;
                await UxPlayComponent.InstallAsync(descriptor, cache, client,
                    new ProgressSink(_ => { }), () => throw new Exception("Unexpected reinstall"), CancellationToken.None);
                Check(requests == server.Requests, "Verified cache must not download again");
                Check(!Directory.EnumerateFiles(cache, "*.download", SearchOption.AllDirectories).Any() &&
                    !Directory.EnumerateFiles(cache, "*.zip", SearchOption.AllDirectories).Any(), "Completed download cleanup");
                Console.WriteLine($"PASS full component {(proxy ? "CONNECT proxy" : "no proxy")} round {round}: {descriptor.Size} bytes, {clock.Elapsed.TotalSeconds:F2}s");
            }
        }
        Check(server.ProxyConnections > 0 && server.MaximumActiveBodies >= 2 && server.SegmentRequests >= 36,
            "Actual proxy tunnels and six segments per download required");

        using var direct = server.CreateClient(false);
        var recovery = Path.Combine(output, "recovery");
        using var recoveryClient = new GitHubReleaseClient(direct, Path.Combine(recovery, "Downloads"));
        async Task Install(CancellationToken token, Action? installing = null) =>
            await UxPlayComponent.InstallAsync(descriptor, recovery, recoveryClient,
                new ProgressSink(_ => { }), installing ?? (() => { }), token, allowMirrorFallback: false);
        server.Slow = true;
        using (var cancellation = new CancellationTokenSource(150))
        {
            try { await Install(cancellation.Token); throw new Exception("Cancellation did not cancel"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Check(UxPlayComponent.FindInstalledExecutable(descriptor, recovery) is null, "Canceled install must not be available");
        Check(!Directory.EnumerateFiles(recovery, "*.download", SearchOption.AllDirectories).Any(), "Cancel removes partials");
        server.Slow = false;
        server.Corrupt = true;
        try { await Install(CancellationToken.None); throw new Exception("Bad hash accepted"); }
        catch (InvalidDataException) { }
        Check(UxPlayComponent.FindInstalledExecutable(descriptor, recovery) is null, "Bad hash must not install");
        server.Corrupt = false;
        server.Truncate = true;
        try { await Install(CancellationToken.None); throw new Exception("Truncated body accepted"); }
        catch (HttpRequestException) { }
        server.Truncate = false;
        using (var cancellation = new CancellationTokenSource())
        {
            try { await Install(cancellation.Token, cancellation.Cancel); throw new Exception("Install cancellation ignored"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Check(!Directory.EnumerateFiles(recovery, "*.zip", SearchOption.AllDirectories).Any(), "Install cancellation removes archive");
        var before = server.PackageProbes;
        await Task.WhenAll(Install(CancellationToken.None), Install(CancellationToken.None));
        Check(server.PackageProbes == before + 2, "Concurrent installs must share verified cache (one availability check and one download probe)");
        var exe = Path.Combine(recovery, descriptor.Sha256, "uxplay.exe");
        var timestamp = File.GetLastWriteTimeUtc(exe);
        using (var file = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
        { var first = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(first ^ 1)); }
        File.SetLastWriteTimeUtc(exe, timestamp);
        await Install(CancellationToken.None);
        Check(UxPlayComponent.FindInstalledExecutable(descriptor, recovery) is not null, "Same-size timestamp-preserving corruption must be repaired");
        Console.WriteLine("PASS cancel/retry, install-stage cancel, corrupt hash, truncated body, concurrent install and fresh-hash repair");

        foreach (var status in new[] { 404, 503 })
        {
            server.PackageStatus = status;
            var cache = Path.Combine(output, "status-" + status);
            using var client = new GitHubReleaseClient(direct, Path.Combine(cache, "Downloads"));
            before = server.PackageProbes;
            try
            {
                await UxPlayComponent.InstallAsync(descriptor, cache, client,
                    new ProgressSink(_ => { }), () => { }, CancellationToken.None);
                throw new Exception("Failed route accepted");
            }
            catch (HttpRequestException error) { Check((int?)error.StatusCode == status, "Preserve HTTP error status"); }
            var attempts = server.PackageProbes - before;
            Check(status == 404 ? attempts == 1 : attempts is > 0 and <= 9, "Official 404 stops before mirrors; 503 retries stay bounded");
            Console.WriteLine($"PASS HTTP {status}: {attempts} bounded attempts");
        }
        server.PackageStatus = 0;
        server.PackageStatus = 503;
        server.OriginPackageStatus = 404;
        server.FailuresRemaining = 1; // Availability check is inconclusive (503).
        var mixedCache = Path.Combine(output, "origin-404-mirror-503");
        before = server.PackageProbes;
        using (var client = new GitHubReleaseClient(direct, Path.Combine(mixedCache, "Downloads")))
        {
            try
            {
                await UxPlayComponent.InstallAsync(descriptor, mixedCache, client,
                    new ProgressSink(_ => { }), () => { }, CancellationToken.None);
                throw new Exception("Missing official component accepted");
            }
            catch (HttpRequestException error)
            { Check(error.StatusCode == HttpStatusCode.NotFound, "Official 404 must not be replaced by queued mirror retries"); }
        }
        Check(server.PackageProbes - before <= 5, "Official 404 must discard queued transient mirror retries");
        Console.WriteLine("PASS official 404 preserved after inconclusive preflight and mirror 503 failures");
        server.OriginPackageStatus = 0;
        server.PackageStatus = 0;
        server.FailuresRemaining = 1;
        var retryCache = Path.Combine(output, "transient-retry");
        before = server.PackageProbes;
        using (var client = new GitHubReleaseClient(direct, Path.Combine(retryCache, "Downloads")))
            await UxPlayComponent.InstallAsync(descriptor, retryCache, client,
                new ProgressSink(_ => { }), () => { }, CancellationToken.None, false);
        Check(server.PackageProbes == before + 2 && UxPlayComponent.FindInstalledExecutable(descriptor, retryCache) is not null,
            "One transient failure must recover on the single allowed retry");
        Console.WriteLine("PASS transient 503 recovery on retry");
        server.NoRanges = true;
        var singleCache = Path.Combine(output, "no-range");
        using (var client = new GitHubReleaseClient(direct, Path.Combine(singleCache, "Downloads")))
            await UxPlayComponent.InstallAsync(descriptor, singleCache, client,
                new ProgressSink(_ => { }), () => { }, CancellationToken.None, false);
        Check(UxPlayComponent.FindInstalledExecutable(descriptor, singleCache) is not null, "Non-range server fallback");
        Console.WriteLine($"PASS single-stream fallback; proxy tunnels={server.ProxyConnections}; max concurrent bodies={server.MaximumActiveBodies}");
        await UxPlayComponentTests.RunPackageAsync(archive, metadata, Path.Combine(output, "runtime-check"));
    }

    internal static async Task RunPublicAsync(string output, string proxyUrl, bool mirrors = false, bool largeOnly = false)
    {
        Directory.CreateDirectory(output);
        var assets = new[]
        {
            (Version: "1.8.3", Name: "iPhoneMirror-v1.8.3-win-x64-sbom.spdx.json", Size: 861790L,
                Hash: "9a516e94f78444d833ada30a8411fed7e08e2bf7d3c601678276cff3461f8ad9"),
            (Version: "1.8.1", Name: "iPhoneMirror-Setup-v1.8.1-x64.exe", Size: 92115480L,
                Hash: "20b604d8f8df134e0bf89176a5a47ad131a13dd1d813decbd544091ae8bad1a5"),
        };
        var failures = 0;
        foreach (var proxy in largeOnly ? new[] { false } : new[] { false, true })
        foreach (var asset in assets.Where(asset => !largeOnly || asset.Size > 1000000))
        for (var round = 1; round <= (largeOnly ? 2 : asset.Size < 1000000 ? 3 : 1); round++)
        {
            using var handler = new HttpClientHandler { UseProxy = proxy, Proxy = proxy ? new WebProxy(proxyUrl) : null };
            using var http = new HttpClient(handler);
            var directory = Path.Combine(output, $"{(proxy ? "proxy" : "direct")}-{asset.Version}-{round}");
            using var client = new GitHubReleaseClient(http, directory);
            SemanticVersion.TryParse(asset.Version, out var version);
            var releaseAsset = new ReleaseAsset(asset.Name, new Uri($"https://github.com/RayrenSX/iPhoneMirror/releases/download/v{asset.Version}/{asset.Name}"), asset.Size, asset.Hash);
            var release = new ReleaseInfo("v" + asset.Version, "Public transport test", "", DateTimeOffset.MinValue, version, false, null, releaseAsset, null);
            var watch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(largeOnly ? 10 : 4));
            try
            {
                long lastProgress = 0;
                var progress = new ProgressSink(p =>
                {
                    if (!largeOnly || p.Phase != UpdateDownloadPhase.Download) return;
                    var now = watch.ElapsedMilliseconds;
                    var previous = Interlocked.Read(ref lastProgress);
                    if (now - previous > 10000 && Interlocked.CompareExchange(ref lastProgress, now, previous) == previous)
                        Console.WriteLine($"Public large round {round}: {p.BytesReceived / 1000000.0:F1}/{asset.Size / 1000000.0:F1} MB, {p.BytesPerSecond / 1000000.0:F2} MB/s, {watch.Elapsed.TotalSeconds:F0}s");
                });
                var downloaded = await client.DownloadAsync(release, progress, timeout.Token, allowMirrorFallback: mirrors, preferInstaller: false);
                Check(downloaded.VerifiedSha256 == asset.Hash, "Public asset SHA256 mismatch");
                Console.WriteLine($"PASS public {(proxy ? "proxy" : "direct")} {asset.Name} round {round}: {asset.Size} bytes SHA256 verified, {watch.Elapsed.TotalSeconds:F2}s");
                File.Delete(downloaded.Path); // Downloaded test executable is never executed.
            }
            catch (Exception error)
            { failures++; Console.WriteLine($"FAIL public {(proxy ? "proxy" : "direct")} {asset.Name} round {round}: {error.GetType().Name}: {error.GetBaseException().Message}, {watch.Elapsed.TotalSeconds:F2}s"); }
        }
        Check(failures == 0, $"Public network failures: {failures}");
    }

    internal static async Task RunPublicComponentAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var client = new GitHubReleaseClient(downloadRoot: Path.Combine(output, "Downloads"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var descriptor = await UxPlayComponent.InstallLatestAsync(output, client, new ProgressSink(_ => { }),
            () => Console.WriteLine("Download and SHA256 verification completed; extracting component."), timeout.Token);
        Console.WriteLine($"Version: {descriptor.Version}\nPlatform: Windows\nArchitecture: x64\nRelease: {descriptor.ReleaseTag}\nAsset: {descriptor.Name}\nURL: {descriptor.Url}");
        var executable = UxPlayComponent.FindInstalledExecutable(descriptor, output);
        Check(executable is not null, "Public component must install with all file hashes verified");
        Console.WriteLine("PASS public UxPlay download, hash, extraction and runtime preparation: " + executable);
        using var offline = new HttpClient(new OfflineHandler());
        using var offlineClient = new GitHubReleaseClient(offline, Path.Combine(output, "Downloads"));
        await UxPlayComponent.InstallAsync(descriptor, output, offlineClient, new ProgressSink(_ => { }),
            () => throw new Exception("Unexpected reinstall"), timeout.Token);
        Console.WriteLine("PASS verified component cache with network disabled");
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Verified cache must not make HTTP requests: " + request.RequestUri);
    }

    // Real TLS origin + real CONNECT forwarding proxy. Only this test handler
    // reroutes sockets to loopback and pins its ephemeral certificate; production
    // HTTPS/host validation is unchanged and exercised with the original URLs.
    private sealed class DownloadServer : IAsyncDisposable
    {
        private readonly byte[] _data;
        private readonly TcpListener _origin = new(IPAddress.Loopback, 0);
        private readonly TcpListener _proxy = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly Task _origins, _proxies;
        private int _requests, _proxyConnections, _active, _maximumActive, _packageProbes, _segments;
        internal int Requests => _requests;
        internal int ProxyConnections => _proxyConnections;
        internal int PackageProbes => _packageProbes;
        internal int SegmentRequests => _segments;
        internal int MaximumActiveBodies => _maximumActive;
        internal volatile bool Slow, Corrupt, Truncate, NoRanges;
        internal volatile int PackageStatus, OriginPackageStatus;
        internal int FailuresRemaining;

        internal DownloadServer(byte[] data)
        {
            _data = data;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=component-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.Exportable);
            _origin.Start(); _proxy.Start();
            _origins = AcceptAsync(_origin, false); _proxies = AcceptAsync(_proxy, true);
        }
        internal HttpClient CreateClient(bool proxy)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = proxy,
                Proxy = proxy ? new WebProxy($"http://127.0.0.1:{((IPEndPoint)_proxy.LocalEndpoint).Port}") : null,
                SslOptions = new SslClientAuthenticationOptions
                { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == _certificate.GetCertHashString() },
            };
            if (!proxy) handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(_origin.LocalEndpoint, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
            return new HttpClient(handler);
        }
        private async Task AcceptAsync(TcpListener listener, bool proxy)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(client, proxy));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client, bool proxy)
        {
            using (client)
            try
            {
                if (proxy)
                {
                    var stream = client.GetStream();
                    var header = await ReadHeadersAsync(stream, _stop.Token);
                    Check(header.StartsWith("CONNECT "), "Expected proxy CONNECT");
                    Interlocked.Increment(ref _proxyConnections);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync((IPEndPoint)_origin.LocalEndpoint, _stop.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), _stop.Token);
                    using var tunnelStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var a = stream.CopyToAsync(upstream.GetStream(), tunnelStop.Token);
                    var b = upstream.GetStream().CopyToAsync(stream, tunnelStop.Token);
                    await Task.WhenAny(a, b); tunnelStop.Cancel();
                    try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { }
                    return;
                }
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                var headers = await ReadHeadersAsync(tls, _stop.Token);
                Interlocked.Increment(ref _requests);
                if (headers.StartsWith("HEAD "))
                { await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token); return; }
                long start = 0, end = _data.LongLength - 1;
                var rangeLine = headers.Split("\r\n").FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                if (rangeLine is not null)
                { var parts = rangeLine.Split('=')[1].Split('-'); start = long.Parse(parts[0]); end = long.Parse(parts[1]); }
                var packageProbe = rangeLine is not null && end == 0;
                if (rangeLine is not null && end - start > 4 * 1024 * 1024) Interlocked.Increment(ref _segments);
                if (packageProbe) Interlocked.Increment(ref _packageProbes);
                var status = PackageStatus;
                if (OriginPackageStatus != 0 && headers.Split("\r\n").Any(line => line.Equals("Host: github.com", StringComparison.OrdinalIgnoreCase)))
                    status = OriginPackageStatus;
                if (packageProbe && Interlocked.Decrement(ref FailuresRemaining) >= 0) status = 503;
                if (packageProbe && status != 0)
                { await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token); return; }
                if (NoRanges) { start = 0; end = _data.LongLength - 1; }
                var partial = rangeLine is not null && !NoRanges;
                var response = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {end - start + 1}\r\nConnection: close\r\n";
                if (partial) response += $"Content-Range: bytes {start}-{end}/{_data.LongLength}\r\n";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), _stop.Token);
                var active = Interlocked.Increment(ref _active);
                int previous;
                do { previous = _maximumActive; } while (active > previous && Interlocked.CompareExchange(ref _maximumActive, active, previous) != previous);
                try
                {
                    var remaining = end - start + 1;
                    if (Truncate && remaining > 262144) remaining /= 2;
                    var corrupt = Corrupt && start == 0 && remaining > 1;
                    if (corrupt) { await tls.WriteAsync(new byte[] { (byte)(_data[0] ^ 1) }, _stop.Token); start++; remaining--; }
                    while (remaining > 0)
                    {
                        if (Slow) await Task.Delay(20, _stop.Token);
                        var count = (int)Math.Min(65536, remaining);
                        await tls.WriteAsync(_data.AsMemory((int)start, count), _stop.Token);
                        start += count; remaining -= count;
                    }
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            catch (System.Security.Authentication.AuthenticationException error)
            { Console.WriteLine("Test TLS handshake: " + error.Message + " " + error.InnerException?.Message); }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
        private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
        {
            var buffer = new byte[32768]; var count = 0;
            while (count < buffer.Length)
            {
                if (await stream.ReadAsync(buffer.AsMemory(count, 1), token) == 0) throw new EndOfStreamException();
                count++;
                if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10)
                    return Encoding.ASCII.GetString(buffer, 0, count);
            }
            throw new InvalidDataException("Oversized test request");
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _origin.Stop(); _proxy.Stop();
            await Task.WhenAll(_origins, _proxies);
            await Task.WhenAll(_connections);
            _certificate.Dispose(); _stop.Dispose();
        }
    }
}
