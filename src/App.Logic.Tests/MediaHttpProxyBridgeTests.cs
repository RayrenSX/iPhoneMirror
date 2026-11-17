using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using IPhoneMirror.App.Services;

internal static class MediaHttpProxyBridgeTests
{
    internal static async Task RunAsync()
    {
        var originalProxy = HttpClient.DefaultProxy;
        var diagnostics = new ConcurrentQueue<string>();
        var media = Enumerable.Range(0, 32 * 1024).Select(index => (byte)(index * 17)).ToArray();
        using var proxy = new HttpFixture(media);
        try
        {
            HttpClient.DefaultProxy = new WebProxy(proxy.Address);
            using var local = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(4) };
            var signedSource = new Uri("http://media.example.invalid/movie.mp4?signature=private-test-value");
            using (var bridge = MediaHttpProxyBridge.TryStart(signedSource, diagnostics.Enqueue))
            {
                Require(bridge is not null && bridge.PlaybackUri.IsLoopback &&
                    bridge.PlaybackUri.AbsolutePath.EndsWith(".mp4", StringComparison.Ordinal),
                    "Media relay did not expose a local URI retaining the format.");
                var address = bridge!.PlaybackUri;
                var full = await local.GetByteArrayAsync(address);
                Require(full.SequenceEqual(media) && proxy.Requests.TryPeek(out var first) &&
                    first.Target == signedSource.AbsoluteUri,
                    "Signed external media did not use the configured forward proxy or changed bytes.");

                using var head = await local.SendAsync(new HttpRequestMessage(HttpMethod.Head, address));
                Require(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == media.Length &&
                    (await head.Content.ReadAsByteArrayAsync()).Length == 0,
                    "HEAD did not preserve the original length without sending media bytes.");

                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, address);
                rangeRequest.Headers.Range = new RangeHeaderValue(123, 899);
                rangeRequest.Headers.TryAddWithoutValidation("If-Range", "\"fixture\"");
                using var range = await local.SendAsync(rangeRequest);
                Require(range.StatusCode == HttpStatusCode.PartialContent &&
                    range.Content.Headers.ContentRange?.ToString() == $"bytes 123-899/{media.Length}" &&
                    range.Content.Headers.ContentLength == 777 &&
                    (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(media[123..900]) &&
                    proxy.Requests.Last().Headers.GetValueOrDefault("If-Range") == "\"fixture\"",
                    "Range/If-Range forwarding lost exact partial media bytes or headers.");
                Require(range.Headers.ETag?.Tag == "\"fixture\"" &&
                    range.Headers.AcceptRanges.Contains("bytes") && !range.Headers.Contains("Set-Cookie"),
                    "The relay lost media metadata or forwarded upstream cookies.");

                using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, address);
                invalidRequest.Headers.Range = new RangeHeaderValue(media.Length + 1, null);
                using var invalid = await local.SendAsync(invalidRequest);
                Require(invalid.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                    invalid.Content.Headers.ContentRange?.ToString() == $"bytes */{media.Length}" &&
                    invalid.Content.Headers.ContentLength == 0,
                    "Unsatisfiable source range must remain 416 with its original total length.");

                var requestCount = proxy.Requests.Count;
                using var wrongToken = await local.GetAsync(new Uri(address, "/not-the-playback-token/movie.mp4"));
                using var wrongMethod = await local.PostAsync(address, new ByteArrayContent([]));
                Require(wrongToken.StatusCode == HttpStatusCode.NotFound &&
                    wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed && proxy.Requests.Count == requestCount,
                    "Unauthorized relay paths or write methods reached the upstream source.");
            }

            using (var redirect = MediaHttpProxyBridge.TryStart(new Uri("http://media.example.invalid/redirect.mp4?signature=private-test-value")))
            {
                Require((await local.GetByteArrayAsync(redirect!.PlaybackUri)).SequenceEqual(media) &&
                    proxy.Requests.Last().Target == "http://media.example.invalid/movie.mp4?signature=redirect-private-value",
                    "A relative signed media redirect did not stay on the selected proxy route.");
            }
            using (var chunked = MediaHttpProxyBridge.TryStart(new Uri("http://media.example.invalid/chunked.mp4")))
            using (var response = await local.GetAsync(chunked!.PlaybackUri, HttpCompletionOption.ResponseHeadersRead))
            {
                Require(response.Content.Headers.ContentLength is null &&
                    (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(media),
                    "A source without length must not receive an invented finite length.");
            }
            using (var origin = new HttpFixture(media))
            {
                var before = proxy.Requests.Count;
                using var loopback = MediaHttpProxyBridge.TryStart(new Uri(origin.Address, "movie.mp4"));
                Require((await local.GetByteArrayAsync(loopback!.PlaybackUri)).SequenceEqual(media) &&
                    origin.Requests.Count == 1 && proxy.Requests.Count == before,
                    "Loopback sources must bypass even a configured application proxy.");
            }

            using var waiting = MediaHttpProxyBridge.TryStart(new Uri("http://media.example.invalid/wait.mp4"));
            // Use one raw request here so HttpClient's transparent GET retries
            // cannot turn a correctly closed playback connection into another
            // connection attempt to the retired listener.
            using var waitingClient = new TcpClient();
            await waitingClient.ConnectAsync(IPAddress.Loopback, waiting!.PlaybackUri.Port);
            await waitingClient.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                $"GET {waiting.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"));
            var pending = waitingClient.GetStream().ReadAsync(new byte[1]).AsTask();
            await proxy.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = Stopwatch.StartNew();
            waiting.Dispose();
            try { Require(await pending.WaitAsync(TimeSpan.FromSeconds(2)) == 0, "Disposed relay sent unexpected media."); }
            catch (IOException) { }
            await proxy.WaitCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(disposal.Elapsed < TimeSpan.FromSeconds(2), "Disposing the relay left a source request blocked.");
            Require(!diagnostics.Any(line => line.Contains("private-test-value", StringComparison.Ordinal) ||
                line.Contains("example.invalid", StringComparison.Ordinal)),
                "Relay diagnostics disclosed a signed media source.");
            Console.WriteLine("PASS media HTTP proxy: actual forward proxy, signed redirects, exact bytes, HEAD/206/416, loopback bypass, token/method gating, unknown length and cancellation.");
        }
        finally { HttpClient.DefaultProxy = originalProxy; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers);

    private sealed class HttpFixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly byte[] _media;
        private readonly Task _serving;
        internal readonly ConcurrentQueue<Request> Requests = new();
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WaitCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri Address { get; }

        internal HttpFixture(byte[] media)
        {
            _media = media;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var connection = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = RespondAsync(connection);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task RespondAsync(TcpClient connection)
        {
            using (connection)
            {
                try
                {
                    var stream = connection.GetStream();
                    using var bytes = new MemoryStream();
                    var buffer = new byte[4096];
                    string text;
                    do
                    {
                        var count = await stream.ReadAsync(buffer, _cancellation.Token);
                        if (count == 0) return;
                        bytes.Write(buffer, 0, count);
                        text = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
                    } while (!text.Contains("\r\n\r\n", StringComparison.Ordinal));
                    var lines = text.Split("\r\n");
                    var first = lines[0].Split(' ');
                    var headers = lines.Skip(1).Where(line => line.Contains(':')).ToDictionary(
                        line => line[..line.IndexOf(':')], line => line[(line.IndexOf(':') + 1)..].Trim(),
                        StringComparer.OrdinalIgnoreCase);
                    var request = new Request(first[0], first[1], headers);
                    Requests.Enqueue(request);
                    var path = Uri.TryCreate(request.Target, UriKind.Absolute, out var absolute)
                        ? absolute.AbsolutePath : request.Target.Split('?')[0];
                    if (path == "/wait.mp4")
                    {
                        Waiting.TrySetResult();
                        try
                        {
                            if (await stream.ReadAsync(buffer, _cancellation.Token) == 0) WaitCancelled.TrySetResult();
                        }
                        catch (IOException) { WaitCancelled.TrySetResult(); }
                        return;
                    }
                    if (path == "/redirect.mp4")
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nConnection: close\r\nContent-Length: 0\r\nLocation: /movie.mp4?signature=redirect-private-value\r\n\r\n"), _cancellation.Token);
                        return;
                    }
                    if (path == "/chunked.mp4")
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: video/mp4\r\nTransfer-Encoding: chunked\r\n\r\n"), _cancellation.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(_media.Length.ToString("X") + "\r\n"), _cancellation.Token);
                        await stream.WriteAsync(_media, _cancellation.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"), _cancellation.Token);
                        return;
                    }
                    var status = 200;
                    var begin = 0;
                    var end = _media.Length - 1;
                    var extra = "";
                    if (headers.TryGetValue("Range", out var range))
                    {
                        var parts = range[6..].Split('-');
                        begin = int.Parse(parts[0]);
                        if (parts[1].Length > 0) end = Math.Min(end, int.Parse(parts[1]));
                        status = begin >= _media.Length ? 416 : 206;
                        extra = status == 416 ? $"Content-Range: bytes */{_media.Length}\r\n" :
                            $"Content-Range: bytes {begin}-{end}/{_media.Length}\r\n";
                    }
                    var countBytes = status == 416 ? 0 : end - begin + 1;
                    var response = $"HTTP/1.1 {status} Response\r\nConnection: close\r\nContent-Type: video/mp4\r\n" +
                        $"Content-Length: {countBytes}\r\n{extra}Accept-Ranges: bytes\r\nETag: \"fixture\"\r\nSet-Cookie: upstream-private-cookie=private\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cancellation.Token);
                    if (request.Method != "HEAD" && countBytes > 0)
                        await stream.WriteAsync(_media.AsMemory(begin, countBytes), _cancellation.Token);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            _serving.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }
    }
}
