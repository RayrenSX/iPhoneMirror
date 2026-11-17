using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IPhoneMirror.App.Services;

// WMF does not expose an application-specific proxy. Keep its original media
// bytes and native seeking, while our HTTP client owns the selected network route.
internal sealed class MediaHttpProxyBridge : IDisposable
{
    private const int MaximumRequestHeaderBytes = 16 * 1024;
    private readonly Uri _source;
    private readonly TcpListener _listener;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly Action<string>? _diagnostic;
    private readonly string _requestPath;
    private readonly Task _serveTask;
    private int _disposed;
    private int _reportedFailures;

    private MediaHttpProxyBridge(Uri source, TcpListener listener,
        Uri playbackUri, Action<string>? diagnostic)
    {
        _source = source;
        _listener = listener;
        PlaybackUri = playbackUri;
        _requestPath = playbackUri.AbsolutePath;
        _diagnostic = diagnostic;
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy),
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _serveTask = ServeAsync(_cancellation.Token);
    }

    internal Uri PlaybackUri { get; }

    internal static MediaHttpProxyBridge? TryStart(Uri source,
        Action<string>? diagnostic = null)
    {
        if (!source.IsAbsoluteUri || !IsHttp(source)) return null;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var extension = Path.GetExtension(source.AbsolutePath);
            if (extension.Length is < 2 or > 10 ||
                extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))
                extension = ".mp4";
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var playback = new Uri($"http://127.0.0.1:{port}/{token}/media{extension}");
            var bridge = new MediaHttpProxyBridge(source, listener, playback, diagnostic);
            diagnostic?.Invoke("media_http_proxy_started");
            return bridge;
        }
        catch (Exception error)
        {
            listener?.Stop();
            diagnostic?.Invoke($"media_http_proxy_start_failed error={error.GetType().Name}");
            return null;
        }
    }

    private async Task ServeAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _connections.TryAdd(connection, 0);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    connection.Dispose();
                    _connections.TryRemove(connection, out _);
                    break;
                }
                _ = ServeRequestAsync(connection, token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Disposing a playback attempt closes both the listener and its clients.
        }
    }

    private async Task ServeRequestAsync(TcpClient connection, CancellationToken token)
    {
        using (connection)
        {
            var responseStarted = false;
            try
            {
                connection.NoDelay = true;
                var output = connection.GetStream();
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ReadRequestAsync(output, requestDeadline.Token).ConfigureAwait(false);
                if (request is null) return;
                if (request.Path != _requestPath)
                {
                    await WriteEmptyResponseAsync(output, 404, token).ConfigureAwait(false);
                    return;
                }
                if (request.Method is not ("GET" or "HEAD"))
                {
                    await WriteEmptyResponseAsync(output, 405, token).ConfigureAwait(false);
                    return;
                }
                using var response = await OpenSourceAsync(request, token).ConfigureAwait(false);
                var headers = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} Response\r\nConnection: close\r\n");
                CopyHeader(response.Content.Headers, "Content-Type", headers);
                CopyHeader(response.Content.Headers, "Content-Length", headers);
                CopyHeader(response.Content.Headers, "Content-Range", headers);
                CopyHeader(response.Content.Headers, "Content-Encoding", headers);
                CopyHeader(response.Content.Headers, "Last-Modified", headers);
                CopyHeader(response.Headers, "Accept-Ranges", headers);
                CopyHeader(response.Headers, "ETag", headers);
                headers.Append("\r\n");
                responseStarted = true;
                await output.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token).ConfigureAwait(false);
                if (request.Method == "HEAD" || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified)
                    return;
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    // Bound a stalled upstream read independently of the total
                    // programme length or a paused local consumer.
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or SocketException or
                OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                if (!token.IsCancellationRequested && Interlocked.Increment(ref _reportedFailures) <= 8)
                    _diagnostic?.Invoke($"media_http_proxy_request_closed error={error.GetType().Name}");
                if (!responseStarted && !token.IsCancellationRequested)
                {
                    try { await WriteEmptyResponseAsync(connection.GetStream(), 502, token).ConfigureAwait(false); }
                    catch (Exception closed) when (closed is IOException or SocketException or
                        ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
                }
            }
            finally { _connections.TryRemove(connection, out _); }
        }
    }

    private async Task<HttpResponseMessage> OpenSourceAsync(Request request, CancellationToken token)
    {
        var source = _source;
        for (var redirect = 0; redirect <= 8; redirect++)
        {
            using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), source);
            upstream.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            foreach (var name in new[] { "Range", "If-Range", "User-Agent", "Accept", "If-None-Match", "If-Modified-Since" })
                if (request.Headers.TryGetValue(name, out var value))
                    upstream.Headers.TryAddWithoutValidation(name, value);
            using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead,
                headerDeadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            if (redirect == 8 || location is null || !Uri.TryCreate(source, location, out var next) || !IsHttp(next))
            {
                response.Dispose();
                throw new HttpRequestException("Invalid media redirect");
            }
            response.Dispose();
            source = next;
        }
        throw new HttpRequestException("Too many media redirects");
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";

    private static void CopyHeader(System.Net.Http.Headers.HttpHeaders source, string name,
        StringBuilder destination)
    {
        if (!source.TryGetValues(name, out var values)) return;
        foreach (var value in values)
            if (!value.Contains('\r') && !value.Contains('\n'))
                destination.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream input, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[2048];
        while (bytes.Length < MaximumRequestHeaderBytes)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return null;
            bytes.Write(buffer, 0, count);
            var text = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            if (end + 4 > MaximumRequestHeaderBytes) return null;
            var lines = text[..end].Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1")) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return null;
                if (!headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) return null;
            }
            return new Request(first[0], first[1], headers);
        }
        return null;
    }

    private static ValueTask WriteEmptyResponseAsync(NetworkStream output, int status,
        CancellationToken token) => output.WriteAsync(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} Response\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        _listener.Stop();
        _client.Dispose();
        foreach (var connection in _connections.Keys) connection.Dispose();
        _ = _serveTask.ContinueWith(_ => _cancellation.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers);

    private sealed class LoopbackBypassProxy(IWebProxy proxy) : IWebProxy
    {
        public ICredentials? Credentials { get => proxy.Credentials; set => proxy.Credentials = value; }
        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? destination : proxy.GetProxy(destination);
        public bool IsBypassed(Uri host) => host.IsLoopback || proxy.IsBypassed(host);
    }
}
