using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services;

// WPF MediaElement treats some HLS VOD playlists as a short finite clip and
// raises MediaEnded at the first segment boundary. FFmpeg owns playlist
// refresh, encryption, discontinuities, and segment concatenation here; WPF
// only receives one continuous MPEG-TS HTTP stream.
internal sealed class HlsMediaPlaybackBridge : IDisposable
{
    private const int MaximumStartupBufferBytes = MediaCastStartupBudget.MaximumPrefixBytes;
    private const int MaximumHttpChunkBytes = 32 * 1024;
    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _serveCancellation = new();
    private readonly Task _serveTask;
    private readonly Lazy<Task<StartupBuffer>> _startup;
    private readonly TaskCompletionSource _outputMetadataReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _requestPath;
    private readonly Action<string>? _diagnostic;
    private readonly MediaCastStartupBudget _startupBudget;
    private double _detectedDuration;
    private int _diagnosticOutputReported;
    private int _requestCount;
    private volatile bool _streamFailed;
    private bool _disposed;
    private bool _waitForOutputMetadata;

    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(\d+):([0-5]\d):([0-5]\d(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InputReadyPattern = new(
        @"^Input #([01]),", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private HlsMediaPlaybackBridge(Process process, TcpListener listener,
        Uri playbackUri, Action<string>? diagnostic)
    {
        _process = process;
        _listener = listener;
        PlaybackUri = playbackUri;
        _requestPath = playbackUri.AbsolutePath;
        _diagnostic = diagnostic;
        _startupBudget = new MediaCastStartupBudget(
            process.StartInfo.ArgumentList.Count(argument => argument == "-i"));
        _startup = new Lazy<Task<StartupBuffer>>(StartPreparing,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _serveTask = ServeAsync();
    }

    internal Uri PlaybackUri { get; }

    internal MediaCastStartupBudget StartupBudget => _startupBudget;

    internal bool HasStartupTimedOut => StartupBudget.HasTimedOut;

    internal string StartupStage => StartupBudget.Stage;

    internal TimeSpan StartupRemaining => StartupBudget.Remaining;

    internal async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startup.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal bool IsRunning => !_disposed && !_streamFailed && !_process.HasExited;

    internal bool ExitedSuccessfully
    {
        get
        {
            if (_disposed || !_process.HasExited) return false;
            // HasExited can precede the final ErrorDataReceived callback.
            // Drain redirected diagnostics before accepting exit 0 as EOF.
            _process.WaitForExit();
            return !_streamFailed && _process.ExitCode == 0;
        }
    }

    internal double DetectedDuration => Volatile.Read(ref _detectedDuration);

    internal static HlsMediaPlaybackBridge? TryStart(Uri source,
        Action<string>? diagnostic = null) => TryStart(source, 0, diagnostic);

    internal static HlsMediaPlaybackBridge? TryStart(Uri source,
        double startPosition, Action<string>? diagnostic = null,
        Action<double>? durationAvailable = null, string? ffmpegPath = null)
    {
        var ffmpeg = ffmpegPath is null ? FindBundledFfmpeg() :
            RuntimeBinaryIntegrity.IsTrustedFfmpeg(ffmpegPath) ? ffmpegPath : null;
        if (ffmpeg is null)
        {
            diagnostic?.Invoke("hls_bridge_unavailable reason=ffmpeg_missing");
            return null;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var playbackUri = new Uri($"http://127.0.0.1:{port}/stream-{Guid.NewGuid():N}.ts",
            UriKind.Absolute);
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in BuildArguments(source, playbackUri, startPosition))
            start.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                listener.Stop();
                return null;
            }
            var bridge = new HlsMediaPlaybackBridge(process, listener, playbackUri, diagnostic);
            bridge._waitForOutputMetadata = true;
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                if (args.Data.StartsWith("Output #0,", StringComparison.Ordinal))
                    bridge._outputMetadataReady.TrySetResult();
                var input = InputReadyPattern.Match(args.Data);
                if (input.Success)
                {
                    var previousStage = bridge.StartupStage;
                    bridge.StartupBudget.MarkInputReady(input.Groups[1].Value[0] - '0');
                    if (bridge.StartupStage != previousStage)
                        diagnostic?.Invoke($"hls_bridge_startup_progress stage={bridge.StartupStage}");
                }
                // HLS can skip a permanently failed segment and still exit 0.
                // That is damaged input, not a naturally completed item. A
                // transient "Failed to open segment" alone is recoverable.
                if (IsDamagedInputDiagnostic(args.Data))
                {
                    bridge._streamFailed = true;
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                if (TryParseDuration(args.Data, out var duration) &&
                    Interlocked.CompareExchange(ref bridge._detectedDuration,
                        duration, 0) == 0)
                {
                    diagnostic?.Invoke(
                        $"hls_bridge_duration seconds={duration:F3}");
                    durationAvailable?.Invoke(duration);
                    return;
                }
                var detail = DescribeFfmpegDiagnostic(args.Data);
                if (detail is not null && Interlocked.Increment(
                        ref bridge._diagnosticOutputReported) <= 24)
                    diagnostic?.Invoke("hls_bridge_ffmpeg_detail " + detail);
            };
            process.BeginErrorReadLine();
            _ = bridge._startup.Value;
            var proxyConfigured = start.Environment.TryGetValue("http_proxy", out var proxy) &&
                !string.IsNullOrWhiteSpace(proxy);
            diagnostic?.Invoke($"hls_bridge_started port={port} proxy_configured={proxyConfigured}");
            return bridge;
        }
        catch (Exception error)
        {
            try { process?.Kill(entireProcessTree: true); }
            catch { }
            diagnostic?.Invoke($"hls_bridge_start_failed error={error.GetType().Name}");
            process?.Dispose();
            listener.Stop();
            return null;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(Uri source, Uri output,
        double startPosition = 0)
    {
        // The reconnect flags cover temporary playlist/key/segment failures;
        // do not reconnect at EOF, so EXT-X-ENDLIST remains a genuine VOD EOF.
        var arguments = new List<string>([
            "-hide_banner", "-nostdin", "-loglevel", "info",
            "-reconnect", "1",
            "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
            // Several mobile live-CDN endpoints (including Douyin) reject
            // FFmpeg's default user agent or wait indefinitely on a stalled
            // TLS read. Use a Safari-like identity and bounded I/O waits so
            // the caller can restart the bridge instead of hanging forever.
            "-user_agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
            "-headers", "Accept: */*\r\nAccept-Language: zh-CN,zh;q=0.9,en;q=0.8\r\n",
            "-rw_timeout", "15000000",
            // HTTPS through an explicitly configured HTTP proxy uses FFmpeg's
            // httpproxy transport for CONNECT. Still exclude local-file inputs.
            "-protocol_whitelist", "http,https,tcp,tls,crypto,httpproxy",
            "-i", source.AbsoluteUri,
            "-map", "0:v:0?", "-map", "0:a:0?",
            "-c", "copy", "-avoid_negative_ts", "make_zero",
            // Keep TS packet flushing and the output timestamp delay low;
            // independent demuxer clocks below preserve track interleaving.
            "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0",
            "-f", "mpegts", "pipe:1",
        ]);
        // This is a demuxer-private option. Generic /live/ and /stream/ URLs
        // may serve FLV/MP4/TS and must remain compatible with auto detection.
        if (IsHlsPlaylistSource(source))
        {
            arguments.InsertRange(arguments.IndexOf("-i"), ["-seg_max_retry", "2"]);
        }
        AddFcupInputOptions(arguments, source);
        if (double.IsFinite(startPosition) && startPosition > 0.05)
        {
            // Input seeking lets the local MPEG-TS stream begin at the
            // programme timestamp requested by the controller. The offset is
            // intentionally before -i so FFmpeg seeks the HLS demuxer rather
            // than discarding an already downloaded programme prefix.
            var inputIndex = arguments.IndexOf("-i");
            if (inputIndex >= 0)
            {
                arguments.Insert(inputIndex, startPosition.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                arguments.Insert(inputIndex, "-ss");
            }
            var outputIndex = arguments.IndexOf("pipe:1");
            if (outputIndex >= 0)
            {
                // HLS input seeking preserves the programme PTS. A fresh
                // MediaElement must see the restarted HTTP stream begin at
                // zero, otherwise WMF may wait forever for its nominal start
                // timestamp (especially around the first non-zero keyframe).
                arguments.Insert(outputIndex, (-startPosition).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                arguments.Insert(outputIndex, "-output_ts_offset");
            }
        }
        if (IsHlsPlaylistSource(source))
        {
            // Each HLS track needs its own input clock correction. A shared
            // input can read video across several discontinuities before its
            // audio timestamps catch up, flushing video seconds ahead of audio
            // even though each stream's output DTS is monotonic. Separate
            // demuxers discard the unused track and retain bounded muxing.
            var inputEnd = arguments.IndexOf("-i") + 2;
            var inputOptions = arguments.Take(inputEnd).ToList();
            arguments.InsertRange(inputEnd - 2, ["-an", "-sn", "-dn"]);
            inputEnd += 3;
            inputOptions.RemoveRange(0, inputOptions.IndexOf("-reconnect"));
            // A live playlist can advance between the two opens. Preserve
            // their original clock offset while correcting each input's own
            // discontinuities, rather than shifting both windows to zero.
            inputOptions.InsertRange(inputOptions.IndexOf("-i"), ["-vn", "-sn", "-dn", "-isync", "0"]);
            arguments.InsertRange(inputEnd, inputOptions);
            var audioMap = arguments.FindIndex(value => value == "0:a:0?");
            arguments[audioMap] = "1:a:0?";
            if (IsFcupSource(source))
            {
                // Let the local broker expose one track per independent
                // demuxer. Opening the full master twice probes and downloads
                // the other track before output can start. Unrecognized or
                // muxed playlists remain unchanged by the broker.
                var input = 0;
                for (var index = 0; index < arguments.Count - 1; index++)
                {
                    if (arguments[index] != "-i") continue;
                    var trackSource = new UriBuilder(source)
                    {
                        Query = (source.Query.Length > 1 ? source.Query[1..] + "&" : "") +
                            "iphonemirror_track=" + (input++ == 0 ? "video" : "audio"),
                    };
                    arguments[index + 1] = trackSource.Uri.AbsoluteUri;
                }
            }
        }
        return arguments;
    }

    internal static void AddFcupInputOptions(List<string> arguments, Uri source)
    {
        // The FCUP broker gives opaque phone resources a local playlist-shaped
        // URL, including segments which redirect to TS/fMP4. FFmpeg's suffix
        // cross-check otherwise rejects valid bytes before opening the stream.
        // Scope this to our loopback broker and retain the protocol whitelist.
        if (!IsFcupSource(source)) return;
        var input = arguments.IndexOf("-i");
        // Each phone resource may redirect to a different signed CDN URL.
        // Reusing the previous segment connection repeatedly fails through
        // proxy CONNECT; open each segment independently on this route.
        if (input >= 0) arguments.InsertRange(input,
            ["-extension_picky", "0", "-http_persistent", "0"]);
    }

    internal static bool IsFcupSource(Uri source)
    {
        var parts = source.AbsolutePath.Split('/');
        return source.Scheme == Uri.UriSchemeHttp && source.IsLoopback &&
            parts.Length == 4 && parts[1].Length == 48 &&
            parts[1].All(Uri.IsHexDigit) && parts[3] == "stream.m3u8";
    }

    private static bool IsHlsPlaylistSource(Uri source)
    {
        if (source.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            source.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) return true;
        var query = Uri.UnescapeDataString(source.Query);
        return query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("format=hls", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("format=m3u8", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? DescribeFfmpegDiagnostic(string line)
    {
        // Keep actionable decoder/network messages, not signed CDN addresses,
        // playlist contents or local token URLs. Bound output per process.
        if (!new[] { "error", "failed", "invalid", "mismatch", "not in", "not allowed",
                "timed out", "end of file", "could not", "unsupported", "codec", "stream #", "input #",
                "timestamp discontinuity", "non-monotonic", "corrupt", "skipping" }
            .Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;
        var safe = Regex.Replace(line, @"(?:https?|mlhls|tcp|tls|crypto)://[^\s'""<>]+",
            "<media-url>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        safe = Regex.Replace(safe, "'[^']*'|\"[^\"]*\"", "<value>");
        return safe.Length <= 600 ? safe : safe[..600];
    }

    internal static bool IsDamagedInputDiagnostic(string line)
    {
        // Inspect the diagnostic message, not sender-controlled URL text that
        // may contain the same words in an otherwise harmless opening log.
        var message = Regex.Replace(line, @"^(?:\[[^\]\r\n]*\]\s*)+", "");
        return message.StartsWith("corrupt input packet in stream ",
                   StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(message, @"^Segment \d+ of playlist \d+ failed too many times, skipping\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool TryParseDuration(string line, out double duration)
    {
        duration = 0;
        var match = DurationPattern.Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(match.Groups[3].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds)) return false;
        duration = hours * 3600 + minutes * 60 + seconds;
        return double.IsFinite(duration) && duration > 0;
    }

    internal static string? FindBundledFfmpeg()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        };
        return candidates.FirstOrDefault(path => File.Exists(path) &&
            RuntimeBinaryIntegrity.IsTrustedFfmpeg(path));
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_serveCancellation.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(
                    _serveCancellation.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                var startedStreaming = false;
                var servingFinite = false;
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        _serveCancellation.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    (string Request, string Range) request;
                    try
                    {
                        request = await ReadRequestHeadersAsync(stream,
                            requestTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !_serveCancellation.IsCancellationRequested)
                    {
                        // A local client may connect and never complete a request.
                        // Keep the listener available for the real MediaElement.
                        continue;
                    }
                    var isGet = request.Request.StartsWith($"GET {_requestPath} ", StringComparison.Ordinal);
                    var isHead = request.Request.StartsWith($"HEAD {_requestPath} ", StringComparison.Ordinal);
                    _diagnostic?.Invoke($"hls_bridge_request count={++_requestCount} method={(isGet ? "GET" : isHead ? "HEAD" : "other")} range={request.Range}");
                    if (!isGet && !isHead)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                            _serveCancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    // Short finite TS clips need a known byte length for WMF
                    // to finish opening. RAM holds a bounded prefix, with
                    // bounded disk spooling for larger short clips; long/live
                    // programmes continue directly from the pipe.
                    startedStreaming = !isHead;
                    var startup = isHead ?
                        _startup.IsValueCreated && _startup.Value.IsCompletedSuccessfully ?
                            _startup.Value.Result : new StartupBuffer(ReadOnlyMemory<byte>.Empty, false) :
                        await _startup.Value.WaitAsync(_serveCancellation.Token).ConfigureAwait(false);
                    var prefix = startup.Data;
                    var finite = startup.Complete;
                    servingFinite = finite;
                    var responseStart = 0L;
                    var responseLength = startup.Length;
                    var partial = finite && !isHead && request.Range != "none";
                    if (partial && !TryGetFiniteRange(request.Range, startup.Length,
                        out responseStart, out responseLength))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{startup.Length}\r\n" +
                            "Content-Length: 0\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n"),
                            _serveCancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    var headers = Encoding.ASCII.GetBytes(
                        (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
                        "Content-Type: video/mp2t\r\n" +
                        (finite ? $"Content-Length: {responseLength}\r\nAccept-Ranges: bytes\r\n" : "Transfer-Encoding: chunked\r\n") +
                        (partial ? $"Content-Range: bytes {responseStart}-{responseStart + responseLength - 1}/{startup.Length}\r\n" : "") +
                        "Connection: close\r\nCache-Control: no-cache\r\n\r\n");
                    _diagnostic?.Invoke($"hls_bridge_response finite={finite} startup_bytes={startup.Length}");
                    if (!isHead) StartupBudget.MarkResponseReady();
                    await stream.WriteAsync(headers,
                        _serveCancellation.Token).ConfigureAwait(false);
                    if (isHead) continue;
                    if (finite)
                    {
                        if (startup.File is not null)
                        {
                            startup.File.Position = responseStart;
                            var copyBuffer = new byte[81920];
                            var remaining = responseLength;
                            while (remaining > 0)
                            {
                                var read = await startup.File.ReadAsync(copyBuffer.AsMemory(0,
                                    (int)Math.Min(copyBuffer.Length, remaining)),
                                    _serveCancellation.Token).ConfigureAwait(false);
                                if (read == 0) throw new IOException("HLS finite buffer was truncated");
                                await stream.WriteAsync(copyBuffer.AsMemory(0, read),
                                    _serveCancellation.Token).ConfigureAwait(false);
                                remaining -= read;
                            }
                        }
                        else
                            await stream.WriteAsync(prefix.Slice((int)responseStart, (int)responseLength),
                                _serveCancellation.Token).ConfigureAwait(false);
                    }
                    else
                        await WriteHttpChunksAsync(stream, prefix,
                            _serveCancellation.Token).ConfigureAwait(false);
                    var buffer = new byte[MaximumHttpChunkBytes];
                    long sent = responseLength;
                    while (!finite)
                    {
                        var read = await _process.StandardOutput.BaseStream.ReadAsync(buffer,
                            _serveCancellation.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        await WriteHttpChunksAsync(stream, buffer.AsMemory(0, read),
                            _serveCancellation.Token).ConfigureAwait(false);
                        sent += read;
                    }
                    if (!finite)
                        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(),
                            _serveCancellation.Token).ConfigureAwait(false);
                    _diagnostic?.Invoke($"hls_bridge_stream_complete bytes={sent}");
                    if (!finite) return;
                }
                catch (OperationCanceledException) when (
                    _serveCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (servingFinite && error is IOException { InnerException: SocketException socket } &&
                        !_serveCancellation.IsCancellationRequested)
                    {
                        // WMF can drop a probe GET and open another connection
                        // for the same short item. The completed immutable
                        // buffer is replayable; stdout is never consumed again.
                        _diagnostic?.Invoke($"hls_bridge_client_reconnect socket_error={socket.SocketErrorCode}");
                        continue;
                    }
                    if (startedStreaming)
                    {
                        // Stdout is a forward-only stream. Once a GET consumes
                        // bytes, resuming a new GET from the remaining pipe
                        // silently drops TS headers/frames. Let the caller
                        // recover with a fresh bridge at the observed position.
                        if (!_serveCancellation.IsCancellationRequested)
                        {
                            _streamFailed = true;
                            _diagnostic?.Invoke($"hls_bridge_stream_failed {DescribeIoFailure(error)} stage={StartupStage}");
                            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception) { }
                        }
                        return;
                    }
                    // Header/HEAD probes have not consumed stdout and can be
                    // followed safely by the player's actual GET.
                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (_serveCancellation.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _listener.Stop();
            if (_startup.IsValueCreated)
            {
                try { (await _startup.Value.ConfigureAwait(false)).Dispose(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    ObjectDisposedException or OperationCanceledException) { }
            }
        }
    }

    private static async Task WriteHttpChunksAsync(Stream stream, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        // WMF can receive a full startup prefix at once. Keep its HTTP chunks
        // bounded just like later pipe reads, rather than writing one 512KiB
        // chunk before the programme starts streaming.
        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, MaximumHttpChunkBytes);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("X",
                CultureInfo.InvariantCulture) + "\r\n"), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(data[..count], cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            data = data[count..];
        }
    }

    private Task<StartupBuffer> StartPreparing()
    {
        var task = PrepareStartupAsync();
        // A caller may abandon startup before awaiting it. The bridge still
        // observes preparation errors and retires its producer immediately.
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task<StartupBuffer> PrepareStartupAsync()
    {
        try
        {
            if (_waitForOutputMetadata)
            {
                var exit = _process.WaitForExitAsync(_serveCancellation.Token);
                while (!_outputMetadataReady.Task.IsCompleted)
                {
                    _serveCancellation.Token.ThrowIfCancellationRequested();
                    var remaining = StartupBudget.Remaining;
                    if (remaining <= TimeSpan.Zero) throw StartupTimeout();
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _serveCancellation.Token);
                    var delay = Task.Delay(remaining, delayCancellation.Token);
                    await Task.WhenAny(_outputMetadataReady.Task, exit, delay).ConfigureAwait(false);
                    delayCancellation.Cancel();
                    if (exit.IsCompleted)
                    {
                        await exit.ConfigureAwait(false);
                        _process.WaitForExit();
                        if (!_outputMetadataReady.Task.IsCompleted)
                            throw new IOException("HLS producer failed before output metadata");
                    }
                }
            }
            return await ReadStartupBufferAsync(_process.StandardOutput.BaseStream,
                _serveCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_serveCancellation.IsCancellationRequested)
            {
                _streamFailed = true;
                _diagnostic?.Invoke($"hls_bridge_prepare_failed {DescribeIoFailure(error)} stage={StartupStage}");
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                _serveCancellation.Cancel();
            }
            throw;
        }
    }

    private static string DescribeIoFailure(Exception error) =>
        $"error={error.GetType().Name} hresult={error.HResult:X8} socket_error=" +
        (error.InnerException is SocketException socket ? socket.SocketErrorCode.ToString() : "none");

    private static bool TryGetFiniteRange(string range, long length,
        out long start, out long count)
    {
        start = count = 0;
        var match = Regex.Match(range, @"^bytes=(\d{0,20})-(\d{0,20})$",
            RegexOptions.CultureInvariant);
        if (!match.Success || length <= 0) return false;
        if (match.Groups[1].Length == 0)
        {
            if (match.Groups[2].Length == 0) return false;
            if (!long.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var suffix)) suffix = long.MaxValue;
            if (suffix <= 0) return false;
            count = Math.Min(suffix, length);
            start = length - count;
            return true;
        }
        if (!long.TryParse(match.Groups[1].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
            return false;
        var end = length - 1;
        if (match.Groups[2].Length != 0 && !long.TryParse(match.Groups[2].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out end)) end = long.MaxValue;
        end = Math.Min(end, length - 1);
        if (end < start) return false;
        count = end - start + 1;
        return true;
    }

    private sealed class StartupBuffer(ReadOnlyMemory<byte> data, bool complete,
        FileStream? file = null) : IDisposable
    {
        private int _disposed;
        internal ReadOnlyMemory<byte> Data { get; } = data;
        internal bool Complete { get; } = complete;
        internal FileStream? File { get; } = file;
        internal long Length => File?.Length ?? Data.Length;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) File?.Dispose();
        }
    }

    private async Task<StartupBuffer> ReadStartupBufferAsync(
        Stream source, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumStartupBufferBytes];
        // Wait for FFmpeg's initial network/probe work before deciding whether
        // this is a finite response. Sending unknown-length headers before
        // ANY input arrives recreates the short-clip failure on a slow CDN.
        var filled = await ReadStartupPartAsync(source, bytes, null,
            cancellationToken).ConfigureAwait(false);
        if (filled == 0)
        {
            await ValidateFiniteProducerAsync(cancellationToken).ConfigureAwait(false);
            return new(ReadOnlyMemory<byte>.Empty, true);
        }
        StartupBudget.MarkBytesReceived(filled);

        // From the first bytes, unknown/live media wait at most 500ms for a
        // prefix. Known short items may finish their bounded prefix while
        // making progress, under the same idle/absolute budget as the player.
        var shortItem = DetectedDuration is > 0 and <= 30;
        var prefixWatch = Stopwatch.StartNew();
        while (filled < bytes.Length)
        {
            if (!shortItem && DetectedDuration is > 0 and <= 30)
                shortItem = true;
            var read = await ReadStartupPartAsync(source, bytes.AsMemory(filled),
                shortItem ? null : prefixWatch, cancellationToken).ConfigureAwait(false);
            if (read < 0) return new(bytes.AsMemory(0, filled), false);
            if (read == 0)
            {
                await ValidateFiniteProducerAsync(cancellationToken).ConfigureAwait(false);
                return new(bytes.AsMemory(0, filled), true);
            }
            filled += read;
            StartupBudget.MarkBytesReceived(filled);
        }
        if (!shortItem && DetectedDuration is not (> 0 and <= 30))
            return new(bytes.AsMemory(0, filled), false);

        // WMF can reject a short chunked response which reaches EOF while it
        // is still opening. Keep RAM bounded, but obtain the exact length of
        // finite ads using a private, bounded file which disappears on close.
        var file = new FileStream(Path.Combine(Path.GetTempPath(),
                $"iPhoneMirror-hls-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            await WriteStartupFileAsync(file, bytes.AsMemory(0, filled), cancellationToken)
                .ConfigureAwait(false);
            var total = filled;
            while (true)
            {
                var room = MediaCastStartupBudget.MaximumFiniteBytes - total;
                var read = await ReadStartupPartAsync(source,
                    bytes.AsMemory(0, Math.Min(bytes.Length, Math.Max(1, room))),
                    null, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > room)
                {
                    _diagnostic?.Invoke("hls_bridge_startup_failed reason=finite_size_limit");
                    throw new IOException("HLS finite startup exceeded its disk buffer limit");
                }
                await WriteStartupFileAsync(file, bytes.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                total += read;
                StartupBudget.MarkBytesReceived(total);
            }
            await RunStartupFileOperationAsync(token => file.FlushAsync(token),
                cancellationToken).ConfigureAwait(false);
            await ValidateFiniteProducerAsync(cancellationToken).ConfigureAwait(false);
            file.Position = 0;
            _diagnostic?.Invoke($"hls_bridge_spooled bytes={total}");
            return new(ReadOnlyMemory<byte>.Empty, true, file);
        }
        catch
        {
            try { file.Dispose(); }
            catch (IOException) { }
            throw;
        }
    }

    private async Task WriteStartupFileAsync(FileStream file, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) =>
        await RunStartupFileOperationAsync(token => file.WriteAsync(data, token).AsTask(),
            cancellationToken).ConfigureAwait(false);

    private async Task RunStartupFileOperationAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var write = operation(writeCancellation.Token);
        try
        {
            var remaining = StartupBudget.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            await write.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            writeCancellation.Cancel();
            try { await write.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw StartupTimeout();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writeCancellation.Cancel();
            try { await write.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    private async Task ValidateFiniteProducerAsync(CancellationToken cancellationToken)
    {
        var remaining = StartupBudget.Remaining;
        if (remaining <= TimeSpan.Zero) throw StartupTimeout();
        using var exitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exit = _process.WaitForExitAsync(exitCancellation.Token);
        try
        {
            await exit.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            exitCancellation.Cancel();
            try { await exit.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw StartupTimeout();
        }
        if (!ExitedSuccessfully)
            throw new IOException("HLS producer failed before finite response completion");
        if (StartupBudget.HasTimedOut) throw StartupTimeout();
    }

    private IOException StartupTimeout()
    {
        _diagnostic?.Invoke($"hls_bridge_startup_timeout stage={StartupStage}");
        return new IOException("HLS startup timed out at " + StartupStage);
    }

    private async Task<int> ReadStartupPartAsync(Stream source, Memory<byte> destination,
        Stopwatch? livePrefixWatch, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = source.ReadAsync(destination, readCancellation.Token).AsTask();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                readCancellation.Cancel();
                try { await read.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
            }
            var remaining = StartupBudget.Remaining;
            if (read.IsCompleted && remaining > TimeSpan.Zero)
                return await read.ConfigureAwait(false);
            var liveRemaining = livePrefixWatch is not null && DetectedDuration is not (> 0 and <= 30)
                ? TimeSpan.FromMilliseconds(500) - livePrefixWatch.Elapsed : (TimeSpan?)null;
            var budgetExpired = remaining <= TimeSpan.Zero;
            if (budgetExpired || liveRemaining <= TimeSpan.Zero)
            {
                readCancellation.Cancel();
                // A canceled pipe read must settle before another read takes
                // ownership of stdout. Milestone updates never start a second
                // read; they only extend the wait on this existing operation.
                var completedRead = -1;
                try { completedRead = await read.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                if (!budgetExpired) return completedRead;
                throw StartupTimeout();
            }
            if (liveRemaining.HasValue && liveRemaining.Value < remaining)
                remaining = liveRemaining.Value;
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, waitCancellation.Token);
            await Task.WhenAny(read, delay).ConfigureAwait(false);
            waitCancellation.Cancel();
            // Input #0/#1 may have advanced the shared budget while this read
            // was pending. Recompute the deadline without canceling the read.
        }
    }

    private static async Task<(string Request, string Range)> ReadRequestHeadersAsync(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        string? request = null;
        var range = "none";
        var one = new byte[1];
        // Consume the complete request header before streaming a response.
        // Closing a Windows socket with unread request headers sends a reset,
        // which truncates finite HLS playback even after all TS bytes were sent.
        for (var total = 0; total < 32768; total++)
        {
            var count = await stream.ReadAsync(one, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) return (string.Empty, range);
            if (one[0] == (byte)'\n')
            {
                if (request is null) request = line.ToString();
                else if (line.Length == 0) return (request, range);
                else if (line.ToString().StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.ToString()[6..].Trim();
                    range = Regex.IsMatch(value, @"^bytes=(?:\d{1,20}-\d{0,20}|-\d{1,20})$",
                        RegexOptions.CultureInvariant) ? value : "other";
                }
                line.Clear();
            }
            else if (one[0] != (byte)'\r')
            {
                if (line.Length >= 4096) return (string.Empty, range);
                line.Append((char)one[0]);
            }
        }
        return (string.Empty, range);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serveCancellation.Cancel();
        _listener.Stop();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            _process.Dispose();
            try { _serveTask.Wait(TimeSpan.FromSeconds(2)); }
            catch { }
            if (_startup.IsValueCreated)
            {
                _ = _startup.Value.ContinueWith(completed =>
                {
                    try { completed.Result.Dispose(); }
                    catch (IOException) { }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            _serveCancellation.Dispose();
        }
    }
}
