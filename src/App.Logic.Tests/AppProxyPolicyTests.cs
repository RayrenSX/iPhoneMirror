using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

internal static class AppProxyPolicyTests
{
    internal static readonly string[] EnvironmentNames =
        ["http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "no_proxy", "NO_PROXY"];

    internal static async Task RunAsync()
    {
        var environment = EnvironmentNames.ToDictionary(name => name,
            Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var defaultProxy = HttpClient.DefaultProxy;
        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror-proxy-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestValidation();
            TestPersistence(root);
            using var proxy = new FixtureServer("via-proxy");
            using var origin = new FixtureServer("from-origin");
            var manual = new AppProxySettings { Mode = AppProxyMode.Manual,
                Address = $"http://127.0.0.1:{proxy.Port}" };
            AppProxyPolicy.Apply(manual);
            Assert(AppProxyPolicy.CurrentMode == AppProxyMode.Manual,
                "Manual mode was not applied.");
            Assert(AppProxyPolicy.RequiresExplicitMediaRouting(new Uri("https://media.example.invalid/movie.mp4")) &&
                !AppProxyPolicy.RequiresExplicitMediaRouting(origin.Address) &&
                !AppProxyPolicy.RequiresExplicitMediaRouting(new Uri("file:///C:/movie.mp4")),
                "Explicit media routing must cover remote HTTP(S), retaining local/control sources.");
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                Assert(await client.GetStringAsync("http://media.example.invalid/movie.mp4") == "via-proxy",
                    "A new HttpClient did not use the configured HTTP proxy.");
                Assert(await client.GetStringAsync(origin.Address) == "from-origin",
                    "Manual proxy mode did not bypass a loopback media endpoint.");
            }
            Assert(proxy.Requests.Count == 1 && proxy.Requests.Single().StartsWith(
                "GET http://media.example.invalid/movie.mp4 HTTP/", StringComparison.Ordinal),
                "Manual proxy request did not contain the destination URI.");
            foreach (var address in new[] { "http://localhost:1234/x", "http://127.0.0.2/x", "http://[::1]/x" })
                Assert(HttpClient.DefaultProxy.IsBypassed(new Uri(address)),
                    "A loopback destination was sent to the manual proxy.");
            Assert(!HttpClient.DefaultProxy.IsBypassed(new Uri("http://intranet/x")),
                "The loopback exception must not bypass all single-label remote hosts.");
            await AssertChildEnvironmentAsync(manual.Address, expectDirect: false);

            AppProxyPolicy.Apply(new() { Mode = AppProxyMode.Direct });
            foreach (var variable in EnvironmentNames.Take(6))
                Assert(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)),
                    "Direct mode retained an inherited proxy environment variable.");
            Assert(HttpClient.DefaultProxy.IsBypassed(new Uri("http://media.example.invalid/movie.mp4")),
                "Direct mode did not disable the managed HTTP proxy.");
            DnsEndPoint? connectedTo = null;
            using (var handler = new SocketsHttpHandler
            {
                // Resolve only this fixture hostname inside the test. The
                // actual HTTP stack still decides proxy vs origin first.
                ConnectCallback = async (context, token) =>
                {
                    connectedTo = context.DnsEndPoint;
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, origin.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) })
                Assert(await client.GetStringAsync("http://media.example.invalid/direct") == "from-origin",
                    "Direct mode did not complete an origin request.");
            Assert(connectedTo is { Host: "media.example.invalid", Port: 80 } &&
                origin.Requests.Any(request => request.StartsWith("GET /direct HTTP/", StringComparison.Ordinal)) &&
                proxy.Requests.Count == 1,
                "Direct mode still connected to the proxy rather than the origin.");
            await AssertChildEnvironmentAsync(null, expectDirect: true);

            AppProxyPolicy.Apply(new());
            Assert(AppProxyPolicy.CurrentMode == AppProxyMode.Default &&
                ReferenceEquals(HttpClient.DefaultProxy, defaultProxy),
                "Default mode did not restore the launch-time managed proxy.");
            foreach (var pair in environment)
                Assert(Environment.GetEnvironmentVariable(pair.Key) == pair.Value,
                    "Default mode did not restore launch-time proxy environment.");
            Assert(!AppProxyPolicy.RequiresExplicitMediaRouting(new Uri("https://media.example.invalid/movie.mp4")),
                "Default mode unexpectedly changed ordinary media routing.");
            Console.WriteLine("PASS application proxy: normalized persistent settings, actual HTTP proxy/direct/loopback routing, child environment and launch-default restoration.");
        }
        finally
        {
            AppProxyPolicy.Apply(new());
            foreach (var pair in environment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            HttpClient.DefaultProxy = defaultProxy;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestValidation()
    {
        foreach (var (input, expected) in new[]
        {
            ("127.0.0.1:7897", "http://127.0.0.1:7897"),
            (" HTTP://LocalHost:7897/ ", "http://localhost:7897"),
            ("http://[::1]:7897", "http://[::1]:7897"),
        })
            Assert(AppProxyPolicy.TryNormalize(new() { Mode = AppProxyMode.Manual, Address = input },
                out var normalized) && normalized.Address == expected,
                "A valid HTTP proxy address was not normalized.");
        foreach (var address in new[] { "", "ftp://host:7897", "https://host:7897", "socks5://host:7897",
            "http://user:secret@host:7897", "http://host:7897/path", "http://host:7897?token=x",
            "http://host:7897#fragment", "http://host:0", "http://host:65536", "http://host\r\nX: value", "not a host" })
            Assert(!AppProxyPolicy.TryNormalize(new() { Mode = AppProxyMode.Manual, Address = address }, out _),
                "A malformed or unsupported manual proxy was accepted.");
        Assert(!AppProxyPolicy.TryNormalize(new() { Mode = (AppProxyMode)999 }, out _) &&
            !AppProxyPolicy.TryNormalize(null, out _) &&
            AppProxyPolicy.TryNormalize(new() { Mode = AppProxyMode.Direct, Address = "invalid value" }, out var direct) &&
            direct.Address == AppProxySettings.DefaultAddress,
            "Unknown modes or inactive draft addresses were handled incorrectly.");
    }

    private static void TestPersistence(string root)
    {
        var store = new UpdateSettingsStore(Path.Combine(root, "settings.json"));
        var settings = new UpdateSettings
        {
            Theme = AppTheme.Dark,
            CheckOnStartup = false,
            NetworkProxy = new() { Mode = AppProxyMode.Manual, Address = "localhost:7897" },
        };
        var clone = settings.Clone();
        clone.NetworkProxy.Mode = AppProxyMode.Direct;
        Assert(settings.NetworkProxy.Mode == AppProxyMode.Manual,
            "Settings clone shares its mutable proxy preferences.");
        store.Save(settings);
        var loaded = store.Load();
        Assert(loaded.NetworkProxy.Mode == AppProxyMode.Manual &&
            loaded.NetworkProxy.Address == "http://localhost:7897" &&
            loaded.Theme == AppTheme.Dark && !loaded.CheckOnStartup,
            "Proxy settings failed to persist without replacing unrelated preferences.");
        Assert(AppProxyPolicy.CurrentMode == AppProxyMode.Default,
            "Saving proxy settings changed active networking before restart.");
        File.WriteAllText(Path.Combine(root, "settings.json"),
            "{\"Theme\":1,\"CheckOnStartup\":false,\"NetworkProxy\":{\"Mode\":999,\"Address\":\"bad\"}}");
        loaded = store.Load();
        Assert(loaded.NetworkProxy.Mode == AppProxyMode.Default && loaded.Theme == AppTheme.Dark && !loaded.CheckOnStartup,
            "An invalid saved proxy mode discarded unrelated settings.");
        File.WriteAllText(Path.Combine(root, "settings.json"),
            "{\"Theme\":1,\"NetworkProxy\":{\"Mode\":2,\"Address\":\"http://user:secret@host\"}}");
        Assert(store.Load().NetworkProxy.Mode == AppProxyMode.Default,
            "Invalid persisted proxy credentials were applied at startup.");
        foreach (var proxyJson in new[] { "[]", "false", "{\"Mode\":\"unknown\"}",
            "{\"Mode\":2,\"Address\":123}" })
        {
            File.WriteAllText(Path.Combine(root, "settings.json"),
                "{\"Theme\":1,\"CheckOnStartup\":false,\"NetworkProxy\":" + proxyJson + "}");
            loaded = store.Load();
            Assert(loaded.NetworkProxy.Mode == AppProxyMode.Default && loaded.Theme == AppTheme.Dark && !loaded.CheckOnStartup,
                "A malformed proxy sub-object discarded unrelated application settings.");
        }
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":1}");
        Assert(store.Load().NetworkProxy.Mode == AppProxyMode.Default,
            "An older settings file did not retain default networking.");
    }

    private static async Task AssertChildEnvironmentAsync(string? address, bool expectDirect)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--proxy-environment-probe");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(process.ExitCode == 0, "Proxy environment probe child failed: " + await errors);
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(await output)!;
        foreach (var variable in EnvironmentNames.Take(6))
            Assert(expectDirect ? string.IsNullOrEmpty(values[variable]) : values[variable] == address,
                "A media/receiver child did not inherit the selected proxy mode.");
        Assert(values["no_proxy"]?.Split(',').Contains("localhost") == true &&
            values["no_proxy"]?.Split(',').Contains("127.0.0.1") == true &&
            values["no_proxy"]?.Split(',').Contains("::1") == true,
            "A child proxy environment lost local receiver bypasses.");
    }

    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serving;
        internal readonly ConcurrentQueue<string> Requests = new();
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal Uri Address => new($"http://127.0.0.1:{Port}/local");

        internal FixtureServer(string body)
        {
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                try
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var first = await reader.ReadLineAsync(_cancellation.Token);
                        if (first is null) continue;
                        for (var line = await reader.ReadLineAsync(_cancellation.Token);
                             !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(_cancellation.Token)) { }
                        Requests.Enqueue(first);
                        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                        await stream.WriteAsync(bytes, _cancellation.Token);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            _serving.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
