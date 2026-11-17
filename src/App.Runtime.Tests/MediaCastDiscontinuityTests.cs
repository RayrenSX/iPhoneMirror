using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMediaCastDiscontinuityTests(string output, string? selectedScenario = null)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        SegmentedPlaybackServer? source = null;
        try
        {
            var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
            InteractionAssert(ffmpeg is not null, "Discontinuous WPF playback requires the trusted bundled FFmpeg.");
            source = new SegmentedPlaybackServer(ffmpeg!, output);
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var viewModel = (MainViewModel)KeyboardField(window, "_viewModel");
            var stopRequests = 0;
            Action stopObserved = () => stopRequests++;
            var stopEvent = viewModel.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
            stopEvent.GetAddMethod(true)!.Invoke(viewModel, [stopObserved]);
            var results = new List<object>();
            try
            {
                (string Name, double Start, double Duration)[] scenarios =
                    [("continuous", 0, 24), ("continuous", 0.064, 24), ("reset", 0.064, 24),
                        ("reset-small", 0.064, 24), ("paced", 0.064, 36), ("reset-long", 0.064, 60)];
                if (selectedScenario is not null)
                    scenarios = scenarios.Where(scenario => scenario.Name == selectedScenario).ToArray();
                else scenarios = scenarios.Where(scenario => scenario.Name != "reset-long").ToArray();
                InteractionAssert(scenarios.Length > 0, "Unknown segmented playback scenario.");
                for (var index = 0; index < scenarios.Length; index++)
                {
                    var scenario = scenarios[index];
                    var uri = source.Source(scenario.Name);
                    KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest((ulong)index + 1,
                        MediaCastCommand.Play, MediaCastFlags.MuteSpecified | MediaCastFlags.Muted,
                        uri.AbsoluteUri, 0, scenario.Start, 0));
                    var watch = Stopwatch.StartNew();
                    var samples = new List<object>();
                    string? previousFrameHash = null;
                    var framePositions = new List<double>();
                    var previous = scenario.Start;
                    var maximum = scenario.Start;
                    double? firstOpenedSeconds = null;
                    var sawRunningProducerAfterMiddle = false;
                    var blackCaptureRetries = 0;
                    var nextCapture = 2d;
                    var lastCaptureUtc = DateTime.UtcNow;
                    while (watch.Elapsed < TimeSpan.FromSeconds(scenario.Duration + 35))
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        var actual = element.Position.TotalSeconds;
                        var bridgeOffset = (double)KeyboardField(window, "_mediaBridgeOffset");
                        actual += bridgeOffset;
                        var opened = (bool)KeyboardField(window, "_mediaOpened");
                        if (opened) firstOpenedSeconds ??= watch.Elapsed.TotalSeconds;
                        var terminal = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        if (opened && maximum < scenario.Duration - 1)
                        {
                            InteractionAssert(previous - actual < 0.75,
                                $"{scenario.Name} @ {scenario.Start}: actual WPF position rolled back {previous:F3} -> {actual:F3} while opening had completed.");
                            previous = actual;
                        }
                        if (opened) maximum = Math.Max(maximum, actual);
                        if (opened && actual > scenario.Duration / 2 && KeyboardField(window, "_mediaHlsBridge") is HlsMediaPlaybackBridge { IsRunning: true })
                            sawRunningProducerAfterMiddle = true;
                        if (opened && actual >= nextCapture && actual < scenario.Duration - 0.1)
                        {
                            var framePath = Path.Combine(output, $"scenario-{index + 1}-{nextCapture:00}-frame.png");
                            blackCaptureRetries += CaptureVerifiedDiscontinuityFrame(window, framePath);
                            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(framePath)));
                            InteractionAssert(previousFrameHash != hash,
                                $"{scenario.Name} @ {scenario.Start}: visible video remained frozen at {actual:F3} seconds.");
                            previousFrameHash = hash;
                            framePositions.Add(actual);
                            nextCapture += 3;
                            lastCaptureUtc = DateTime.UtcNow;
                        }
                        if (opened && watch.Elapsed > TimeSpan.FromSeconds(10))
                        {
                            if (DateTime.UtcNow - lastCaptureUtc >= TimeSpan.FromSeconds(10))
                            {
                                File.WriteAllText(Path.Combine(output, $"scenario-{index + 1}-failed-samples.json"),
                                    JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                                throw new InvalidOperationException($"{scenario.Name} @ {scenario.Start}: actual rendered progress stalled near {maximum:F3} while the session stayed open.");
                            }
                        }
                        samples.Add(new { elapsed = watch.Elapsed.TotalSeconds, actual, opened, terminal = terminal.ToString() });
                        if (terminal == MediaCastPlaybackStatus.Failed)
                            throw new InvalidOperationException($"{scenario.Name} @ {scenario.Start} failed at {maximum:F3}; see {output}.");
                        if (terminal == MediaCastPlaybackStatus.Completed) break;
                    }
                    File.WriteAllText(Path.Combine(output, $"scenario-{index + 1}-samples.json"),
                        JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed) && maximum > scenario.Duration - 1,
                        $"{scenario.Name} @ {scenario.Start} did not complete all {scenario.Duration} seconds of real playback; actual={maximum:F3}.");
                    InteractionAssert(framePositions.Count >= (int)((scenario.Duration - 2) / 3) &&
                        framePositions.Any(p => p > scenario.Duration / 2 && p < scenario.Duration / 2 + 3),
                        "The discontinuity fixture did not observe changing frames across its middle boundary.");
                    InteractionAssert(!(bool)KeyboardField(window, "_mediaPrematureEndRetried"),
                        "A valid discontinuous HLS source required an unnecessary premature EOF recovery.");
                    if (scenario.Name == "paced")
                        InteractionAssert(sawRunningProducerAfterMiddle && firstOpenedSeconds <= 15,
                            "The paced fixture did not open promptly with its producer still running after the middle of actual playback.");
                    InteractionAssert(stopRequests == 0 && viewModel.IsMediaCasting,
                        "A valid source boundary stopped the sender's casting session.");
                    results.Add(new { scenario.Name, scenario.Start, maximumActual = maximum,
                        scenario.Duration, elapsed = watch.Elapsed.TotalSeconds, firstOpenedSeconds, framePositions,
                        sawRunningProducerAfterMiddle, blackCaptureRetries });
                    Console.WriteLine($"PASS WPF segmented {scenario.Name} @ {scenario.Start:F3}: actual={maximum:F3}, changing frames={framePositions.Count}.");
                }
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Real MainWindow/FFmpeg/WPF separate fMP4 audio/video HLS, continuous and timestamp-reset periods; does not model phone queue decisions.",
                    scenarios = results,
                    stopRequests,
                    requestCounts = source.RequestCounts,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]); }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "segmented_playback_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            source?.Dispose();
            app.Shutdown();
        }
    }

    private static int CaptureVerifiedDiscontinuityFrame(MainWindow window, string path)
    {
        // WPF's asynchronous video surface can be unavailable to a single
        // RenderTargetBitmap call even while Position and the next frame move.
        // Bound any retry to 200ms and still reject persistent black output.
        for (var retry = 0; retry <= 5; retry++)
        {
            ScreenshotService.CaptureVisualPng((FrameworkElement)KeyboardField(window, "MediaCastVideoHost"), path);
            try { AssertRenderedMediaFrame(path); return retry; }
            catch (InvalidOperationException) when (retry < 5)
            { AdvanceDispatcher(TimeSpan.FromMilliseconds(40)); }
        }
        throw new InvalidOperationException("WPF video stayed black across the bounded 200ms capture window.");
    }

    private sealed class SegmentedPlaybackServer : IDisposable
    {
        private readonly string _assets;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serving;
        private readonly string _base;
        private readonly bool _loadSpeed;
        private long _responseBytes;
        internal readonly ConcurrentDictionary<string, int> RequestCounts = new();
        internal long ResponseBytes => Interlocked.Read(ref _responseBytes);

        internal SegmentedPlaybackServer(string ffmpeg, string output, bool loadSpeed = false)
        {
            _loadSpeed = loadSpeed;
            _assets = Path.Combine(output, "assets");
            Directory.CreateDirectory(_assets);
            foreach (var period in new[] { ("continuous", 24), ("paced", 36), ("p0", 12), ("p1", 12), ("small0", 6), ("small1", 6), ("small2", 6), ("small3", 6) })
            foreach (var track in new[] { "video", "audio" })
            {
                var directory = Path.Combine(_assets, period.Item1, track);
                Directory.CreateDirectory(directory);
                var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
                if (track == "video")
                {
                    arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=360x640:rate=60", "-t", period.Item2.ToString(CultureInfo.InvariantCulture),
                        "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "120", "-keyint_min", "120", "-sc_threshold", "0"]);
                    if (period.Item1 == "p1" || period.Item1 == "small1" || period.Item1 == "small3")
                        arguments.AddRange(["-vf", "hue=h=90"]);
                }
                else
                    arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", period.Item2.ToString(CultureInfo.InvariantCulture),
                        "-vn", "-c:a", "aac", "-ac", "2", "-ar", "44100"]);
                arguments.AddRange(["-f", "hls", "-hls_time", "2", "-hls_list_size", "0", "-hls_segment_type", "fmp4",
                    "-hls_fmp4_init_filename", Path.Combine(directory, "init.mp4"), "-hls_segment_filename", Path.Combine(directory, "part-%03d.m4s"), Path.Combine(directory, "stream.m3u8")]);
                var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEndAsync();
                try
                {
                    InteractionAssert(process.WaitForExit(45000), "fMP4 fixture encoding timed out.");
                    InteractionAssert(process.ExitCode == 0, "fMP4 fixture encoding failed: " + error.GetAwaiter().GetResult());
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            foreach (var mode in new[] { "continuous", "reset", "reset-small", "paced", "reset-long", "load-short", "load-stream" })
            {
                var presentation = Path.Combine(_assets, mode + "-presentation");
                Directory.CreateDirectory(presentation);
                File.WriteAllText(Path.Combine(presentation, "stream.m3u8"),
                    "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"English\",DEFAULT=YES,AUTOSELECT=YES,URI=\"audio.m3u8\"\n" +
                    "#EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS=\"avc1.42c01e,mp4a.40.2\",AUDIO=\"audio\"\nvideo.m3u8\n");
                foreach (var track in new[] { "video", "audio" })
                {
                    var periods = mode is "paced" or "load-stream" ? new[] { "paced" } :
                        mode == "load-short" ? new[] { "p0" } :
                        mode == "continuous" ? new[] { "continuous" } :
                        mode == "reset-long" ? Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? "small0" : "small1").ToArray() :
                        mode == "reset-small" ? new[] { "small0", "small1", "small2", "small3" } : new[] { "p0", "p1" };
                    var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
                    for (var periodIndex = 0; periodIndex < periods.Length; periodIndex++)
                    {
                        var period = periods[periodIndex];
                        if (periodIndex > 0) playlist.AppendLine("#EXT-X-DISCONTINUITY");
                        var prefix = mode == "paced" ? "../paced-assets" : "..";
                        foreach (var line in File.ReadAllLines(Path.Combine(_assets, period, track, "stream.m3u8")))
                        {
                            if (line.StartsWith("#EXT-X-MAP:"))
                                playlist.AppendLine($"#EXT-X-MAP:URI=\"{prefix}/{period}/{track}/init.mp4\"");
                            else if (line.StartsWith("#EXTINF:")) playlist.AppendLine(line);
                            else if (line.Length > 0 && !line.StartsWith('#')) playlist.AppendLine($"{prefix}/{period}/{track}/{line}");
                        }
                    }
                    playlist.AppendLine("#EXT-X-ENDLIST");
                    File.WriteAllText(Path.Combine(presentation, track + ".m3u8"), playlist.ToString());
                }
            }
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _base = $"http://127.0.0.1:{port}/" + new string('b', 48);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        internal Uri Source(string mode) => new($"{_base}/{mode}-presentation/stream.m3u8");

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var parts = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            var paced = parts.Length > 1 && parts[1] == "paced-assets";
                            var relative = string.Join(Path.DirectorySeparatorChar, parts.Skip(paced ? 2 : 1));
                            var track = context.Request.QueryString["iphonemirror_track"];
                            var requestLabel = relative.Replace(Path.DirectorySeparatorChar, '/') +
                                (track is "video" or "audio" ? "?track=" + track : "");
                            RequestCounts.AddOrUpdate(requestLabel, 1, (_, count) => count + 1);
                            var path = Path.GetFullPath(Path.Combine(_assets, relative));
                            if (!path.StartsWith(_assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                            { context.Response.StatusCode = 404; return; }
                            context.Response.ContentType = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                                ? "application/vnd.apple.mpegurl" : path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/iso.segment";
                            var data = await File.ReadAllBytesAsync(path, _cancellation.Token);
                            if (Path.GetFileName(path) == "stream.m3u8" &&
                                track is "video" or "audio")
                            {
                                // Equivalent to the broker's explicitly selected AVC/AAC
                                // master projection for this known fixture. An absent
                                // query retains the ordinary full master response.
                                var codec = track == "video" ? "avc1.42c01e" : "mp4a.40.2";
                                data = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:7\n" +
                                    $"#EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS=\"{codec}\"\n{track}.m3u8\n");
                            }
                            context.Response.ContentLength64 = data.Length;
                            if (context.Request.HttpMethod != "HEAD")
                            {
                                if (_loadSpeed)
                                {
                                    await Task.Delay(200, _cancellation.Token);
                                    for (var offset = 0; offset < data.Length; offset += 16384)
                                    {
                                        var count = Math.Min(16384, data.Length - offset);
                                        await context.Response.OutputStream.WriteAsync(data.AsMemory(offset, count), _cancellation.Token);
                                        await context.Response.OutputStream.FlushAsync(_cancellation.Token);
                                        Interlocked.Add(ref _responseBytes, count);
                                        await Task.Delay(TimeSpan.FromSeconds(count / 1_000_000d), _cancellation.Token);
                                    }
                                    return;
                                }
                                var isLaterFragment = paced && Path.GetFileName(path).StartsWith("part-") &&
                                    int.TryParse(Path.GetFileNameWithoutExtension(path)[5..], out var fragment) && fragment >= 3;
                                if (isLaterFragment)
                                {
                                    // Keep the actual producer alive while WPF
                                    // crosses several audio/video segment
                                    // boundaries, as on a real paced CDN.
                                    for (var offset = 0; offset < data.Length; offset += 16384)
                                    {
                                        var count = Math.Min(16384, data.Length - offset);
                                        await context.Response.OutputStream.WriteAsync(data.AsMemory(offset, count), _cancellation.Token);
                                        await context.Response.OutputStream.FlushAsync(_cancellation.Token);
                                        await Task.Delay(TimeSpan.FromMilliseconds(1500d * count / data.Length), _cancellation.Token);
                                    }
                                }
                                else await context.Response.OutputStream.WriteAsync(data, _cancellation.Token);
                            }
                        }
                        catch (Exception error) when (error is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
                        finally { context.Response.Close(); }
                    });
                }
            }
            catch (Exception error) when (error is HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            _serving.GetAwaiter().GetResult();
            _listener.Close();
            _cancellation.Dispose();
        }
    }
}
