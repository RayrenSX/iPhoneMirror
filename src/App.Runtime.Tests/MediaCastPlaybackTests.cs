using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMediaCastPlaybackTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        try
        {
            var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
            InteractionAssert(ffmpeg is not null, "Real WPF media playback requires the trusted bundled FFmpeg.");
            var durations = new[] { 6.6, 3.2, 4.0, 1.0, 3.2, 3.2, 1.0 };
            (int Width, int Height)[] sizes = [(320, 240), (320, 240), (320, 240), (160, 90),
                (1280, 720), (320, 240), (160, 90)];
            using var server = new ShortMediaPlaybackServer(ffmpeg!, output, durations, sizes,
                mp4Item: 5);
            var sources = durations.Select((_, item) => item is 1 or 4 ? server.ExternalSource(item) :
                item == 5 ? server.Mp4Source(item) : server.Source(item)).ToArray();
            InteractionAssert(!HlsMediaPlaybackBridge.IsFcupSource(sources[1]) &&
                !HlsMediaPlaybackBridge.IsFcupSource(sources[4]) &&
                MediaSourceClassifier.IsLikelyLive(sources[1]) &&
                !MediaSourceClassifier.IsLikelyLive(sources[5]),
                "Cross-application media fixtures must exercise external signed HLS and direct MP4 paths.");
            ProbeShortMediaWithWpf(new Uri(Path.Combine(output, "item-2.ts")), "local TS file");
            ProbeShortMediaWithWpf(server.Segment(1), "HTTP TS with Content-Length");
            ProbeShortMediaWithWpf(server.Segment(3), "one-second HTTP TS with Content-Length", 160, 90);
            using (var bridge = HlsMediaPlaybackBridge.TryStart(server.Source(1), 0))
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
            {
                InteractionAssert(bridge is not null, "Could not start the remuxed-fixture bridge probe.");
                var remuxed = client.GetByteArrayAsync(bridge!.PlaybackUri).GetAwaiter().GetResult();
                var remuxedPath = Path.Combine(output, "item-2-bridged.ts");
                File.WriteAllBytes(remuxedPath, remuxed);
                server.SetBridgedSegment(1, remuxed);
                ProbeShortMediaWithWpf(new Uri(remuxedPath), "exact bridge output as local TS");
                ProbeShortMediaWithWpf(server.BridgedSegment(1), "exact bridge output HTTP with Content-Length");
            }
            InteractionAssert(new FileInfo(Path.Combine(output, "item-5.ts")).Length > 512 * 1024,
                "HD short fixture must exceed 512 KiB so the regression cannot pass by buffering only the small clip.");
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
                for (var item = 0; item < durations.Length; item++)
                {
                    var expectedDuration = durations[item];
                    var requestDuration = item == 1 ? 0 : expectedDuration;
                    var source = sources[item];
                    var sourceKind = item is 1 or 4 ? "signed-external-hls" :
                        item == 5 ? "http-mp4" : "fcup-hls";
                    var request = new MediaCastRequest((ulong)item + 1, MediaCastCommand.Play,
                        MediaCastFlags.MuteSpecified | MediaCastFlags.Muted,
                        source.AbsoluteUri, requestDuration, 0, 0);
                    KeyboardCall(window, "OnMediaCastCommandReceived", request);
                    InteractionAssert(((bool)KeyboardField(window, "_mediaUsesHlsBridge")) == (item != 5),
                        "The direct MP4 fixture did not remain on the native playback path.");
                    var watch = Stopwatch.StartNew();
                    var maximumActualPosition = 0d;
                    var maximumWidth = 0;
                    var maximumHeight = 0;
                    var frameSaved = false;
                    var finalFrameSaved = false;
                    string? firstFrameHash = null;
                    var framePath = Path.Combine(output, $"item-{item + 1}-frame.png");
                    var finalFramePath = Path.Combine(output, $"item-{item + 1}-tail-frame.png");
                    while (watch.Elapsed < TimeSpan.FromSeconds(35))
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        maximumWidth = Math.Max(maximumWidth, element.NaturalVideoWidth);
                        maximumHeight = Math.Max(maximumHeight, element.NaturalVideoHeight);
                        if ((bool)KeyboardField(window, "_mediaOpened"))
                            maximumActualPosition = Math.Max(maximumActualPosition, element.Position.TotalSeconds);
                        if (!frameSaved && maximumActualPosition >= Math.Min(1.5, expectedDuration / 2))
                        {
                            var visual = (FrameworkElement)KeyboardField(window, "MediaCastVideoHost");
                            ScreenshotService.CaptureVisualPng(visual, framePath);
                            AssertRenderedMediaFrame(framePath);
                            firstFrameHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(framePath)));
                            frameSaved = true;
                        }
                        if (frameSaved && !finalFrameSaved && maximumActualPosition >=
                            Math.Max(Math.Min(1.5, expectedDuration / 2) + 0.15, expectedDuration - 0.5))
                        {
                            ScreenshotService.CaptureVisualPng((FrameworkElement)KeyboardField(window, "MediaCastVideoHost"), finalFramePath);
                            AssertRenderedMediaFrame(finalFramePath);
                            InteractionAssert(firstFrameHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(finalFramePath))),
                                "WPF position advanced while the generated video frame remained frozen.");
                            finalFrameSaved = true;
                        }
                        var terminal = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        if (terminal == MediaCastPlaybackStatus.Failed)
                            throw new InvalidOperationException($"Short item {item + 1} failed at actual {maximumActualPosition:F3}/{expectedDuration:F3}; see {Path.Combine(output, "logs")}.");
                        if (terminal == MediaCastPlaybackStatus.Completed) break;
                    }
                    var reportedDuration = (double)KeyboardField(window, "_mediaProgramDuration");
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed),
                        $"Short item {item + 1} did not finish naturally in 35 seconds.");
                    InteractionAssert(maximumActualPosition >= expectedDuration - 0.75 &&
                        maximumWidth == sizes[item].Width && maximumHeight == sizes[item].Height && frameSaved && finalFrameSaved,
                        $"Short item {item + 1} completed without real WPF video progress: actual={maximumActualPosition:F3}, size={maximumWidth}x{maximumHeight}, frame={frameSaved}.");
                    InteractionAssert(stopRequests == 0 && (bool)KeyboardField(window, "_mediaCastActive") && viewModel.IsMediaCasting,
                        "Natural short-item completion stopped the sender's casting session.");
                    InteractionAssert(!(bool)KeyboardField(window, "_mediaPrematureEndRetried"),
                        "A clean short item required an unnecessary premature-EOF recovery.");
                    InteractionAssert(Math.Abs(reportedDuration - expectedDuration) < 0.25,
                        "The short HLS programme duration was replaced by a segment heuristic.");
                    results.Add(new { item = item + 1, sourceKind, expectedDuration, requestDuration, reportedDuration,
                        maximumActualPosition, maximumWidth, maximumHeight,
                        elapsedSeconds = watch.Elapsed.TotalSeconds, framePath, finalFramePath });
                    Console.WriteLine($"PASS WPF short item {item + 1} ({sourceKind}): actual={maximumActualPosition:F3}/{reportedDuration:F3}s, rendered={maximumWidth}x{maximumHeight}, cast retained.");
                }
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Real production MainWindow, FCUP HLS, signed external HLS and native HTTP MP4; fixture publishes each next Play only after terminal completion. This does not emulate an app or iPhone queue decision.",
                    items = results,
                    stopRequests,
                    playlistRequests = server.PlaylistRequests,
                    segmentRequests = server.SegmentRequests,
                    mp4Requests = server.Mp4Requests,
                    mp4Ranges = server.Mp4Ranges.ToArray(),
                    delayedPlaylistItem = 2,
                    playlistDelayMilliseconds = 2000,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]); }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "real_media_playback_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            app.Shutdown();
        }
    }

    private static void ProbeShortMediaWithWpf(Uri source, string label, int width = 320, int height = 240)
    {
        var element = new MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Manual,
            UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
            IsMuted = true,
            Source = source,
        };
        var window = new Window { Width = 400, Height = 320, Content = element, ShowInTaskbar = false };
        string? failure = null;
        element.MediaFailed += (_, e) => failure = e.ErrorException?.HResult.ToString("X8") + ":" + e.ErrorException?.Message;
        try
        {
            window.Show();
            element.Play();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8) && failure is null && element.Position.TotalSeconds < 0.2)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(failure is null && element.NaturalVideoWidth == width && element.NaturalVideoHeight == height && element.Position.TotalSeconds >= 0.2,
                $"The generated fixture was not playable by WPF as {label}: {failure ?? "no progress"}.");
            Console.WriteLine($"PASS WPF fixture validity: {label}, actual={element.Position.TotalSeconds:F3}s, size={element.NaturalVideoWidth}x{element.NaturalVideoHeight}.");
        }
        finally
        {
            element.Stop();
            element.Source = null;
            window.Close();
        }
    }

    private static void AssertRenderedMediaFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        var minimum = 255;
        var maximum = 0;
        var visiblePixels = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var brightest = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            var darkest = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            minimum = Math.Min(minimum, darkest);
            maximum = Math.Max(maximum, brightest);
            if (brightest > 48) visiblePixels++;
        }
        InteractionAssert(maximum - minimum > 48 && visiblePixels > 1000,
            "WPF advanced its position but did not render the generated test pattern.");
    }

    private sealed class ShortMediaPlaybackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serving;
        private readonly byte[][] _segments;
        private readonly ConcurrentDictionary<int, byte[]> _bridgedSegments = new();
        private readonly ConcurrentDictionary<int, byte[]> _mp4Segments = new();
        private readonly double[] _durations;
        private readonly TimeSpan[] _playlistDelays;
        private readonly string _base;
        private readonly string _origin;
        internal readonly ConcurrentDictionary<int, int> PlaylistRequests = new();
        internal readonly ConcurrentDictionary<int, int> SegmentRequests = new();
        internal readonly ConcurrentDictionary<int, int> Mp4Requests = new();
        internal readonly ConcurrentQueue<string> Mp4Ranges = new();
        internal readonly ConcurrentQueue<object> RequestTimings = new();

        internal ShortMediaPlaybackServer(string ffmpeg, string output, double[] durations,
            (int Width, int Height)[] sizes, TimeSpan[]? playlistDelays = null, int? mp4Item = null)
        {
            _durations = durations;
            _playlistDelays = playlistDelays ?? durations.Select((_, index) =>
                index == 1 ? TimeSpan.FromSeconds(2) : TimeSpan.Zero).ToArray();
            InteractionAssert(_playlistDelays.Length == durations.Length,
                "The fixture needs one playlist delay per item.");
            _segments = new byte[durations.Length][];
            for (var item = 0; item < durations.Length; item++)
            {
                var path = Path.Combine(output, $"item-{item + 1}.ts");
                var start = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y",
                    "-f", "lavfi", "-i", $"testsrc2=size={sizes[item].Width}x{sizes[item].Height}:rate=25",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                    "-t", durations[item].ToString(CultureInfo.InvariantCulture),
                    "-c:v", "libx264", "-preset", "ultrafast", "-g", "25", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-muxdelay", "0", "-muxpreload", "0", "-f", "mpegts", path })
                    start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                try
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    InteractionAssert(process.WaitForExit(15000), "Short video fixture generation timed out.");
                    InteractionAssert(process.ExitCode == 0, "Short video fixture generation failed: " + errors.GetAwaiter().GetResult());
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                _segments[item] = File.ReadAllBytes(path);
            }
            if (mp4Item is { } mp4Index)
            {
                var mp4Path = Path.Combine(output, $"item-{mp4Index + 1}.mp4");
                var start = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y",
                    "-i", Path.Combine(output, $"item-{mp4Index + 1}.ts"), "-c", "copy",
                    "-movflags", "+faststart", mp4Path })
                    start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                try
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    InteractionAssert(process.WaitForExit(15000), "Direct MP4 fixture generation timed out.");
                    InteractionAssert(process.ExitCode == 0,
                        "Direct MP4 fixture generation failed: " + errors.GetAwaiter().GetResult());
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                _mp4Segments[mp4Index] = File.ReadAllBytes(mp4Path);
            }
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _origin = $"http://127.0.0.1:{port}";
            _base = _origin + "/" + new string('a', 48);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
            if (durations.Length >= 5)
            using (var blockedReader = new HttpClient(new HttpClientHandler { UseProxy = false })
                   { Timeout = TimeSpan.FromSeconds(5) })
            using (var blockedResponse = blockedReader.GetAsync(Segment(4), HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                // An HTTP client may stop consuming one response while opening
                // another track. Real HTTP servers dispatch those requests in
                // parallel; a sequential fixture deadlocks larger TS bodies.
                var pendingPlaylist = blockedReader.GetStringAsync(Source(0));
                InteractionAssert(pendingPlaylist.Wait(TimeSpan.FromSeconds(3)),
                    "The short-media HTTP fixture serialized a second request behind an unread media body.");
            }
        }

        internal Uri Source(int item) => new($"{_base}/{item}/stream.m3u8");
        internal Uri ExternalSource(int item) => new($"{_origin}/signed-media/{item}/stream.m3u8?signature=fixture%2Bonly");
        internal Uri Mp4Source(int item) => new($"{_origin}/signed-media/{item}/video.mp4?signature=fixture%2Bonly");
        internal Uri Segment(int item) => new($"{_base}/{item}/segment.ts");
        internal Uri BridgedSegment(int item) => new($"{_base}/{item}/bridged.ts");
        internal void SetBridgedSegment(int item, byte[] data) => _bridgedSegments[item] = data;

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token);
                    _ = Task.Run(() => ServeRequestAsync(context));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }

        private async Task ServeRequestAsync(HttpListenerContext context)
        {
                    var requestWatch = Stopwatch.StartNew();
                    var requestItem = -1;
                    var requestKind = "invalid";
                    try
                    {
                        var parts = context.Request.Url!.AbsolutePath.Split('/');
                        if (parts.Length != 4 || !int.TryParse(parts[2], out var item) ||
                            item < 0 || item >= _segments.Length)
                        {
                            context.Response.StatusCode = 404;
                            return;
                        }
                        requestItem = item;
                        requestKind = parts[3];
                        var external = parts[1] == "signed-media";
                        if (external && context.Request.QueryString["signature"] != "fixture+only")
                        {
                            context.Response.StatusCode = 403;
                            return;
                        }
                        if (context.Request.HttpMethod is not ("GET" or "HEAD"))
                        {
                            context.Response.StatusCode = 405;
                            return;
                        }
                        byte[] data;
                        if (parts[3] == "stream.m3u8")
                        {
                            PlaylistRequests.AddOrUpdate(item, 1, (_, count) => count + 1);
                            // The sender supplies no duration for this small
                            // item. A WAN/proxy delay must not force the bridge
                            // into an unknown-length response before FFmpeg can
                            // discover that the complete finite body is small.
                            if (_playlistDelays[item] > TimeSpan.Zero)
                                await Task.Delay(_playlistDelays[item], _cancellation.Token);
                            var segment = external ?
                                $"{_origin}/signed-media/{item}/segment.ts?signature=fixture%2Bonly" :
                                $"{_base}/{item}/segment.ts";
                            data = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:7\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:" +
                                _durations[item].ToString(CultureInfo.InvariantCulture) + ",\n" + segment + "\n#EXT-X-ENDLIST\n");
                            context.Response.ContentType = "application/vnd.apple.mpegurl";
                        }
                        else if (parts[3] == "video.mp4" && _mp4Segments.TryGetValue(item, out var mp4))
                        {
                            Mp4Requests.AddOrUpdate(item, 1, (_, count) => count + 1);
                            data = mp4;
                            context.Response.ContentType = "video/mp4";
                            context.Response.Headers["Accept-Ranges"] = "bytes";
                        }
                        else
                        {
                            SegmentRequests.AddOrUpdate(item, 1, (_, count) => count + 1);
                            data = parts[3] == "bridged.ts" ? _bridgedSegments[item] : _segments[item];
                            context.Response.ContentType = "video/mp2t";
                        }
                        var offset = 0;
                        var length = data.Length;
                        if (parts[3] == "video.mp4" && context.Request.Headers["Range"] is { } range)
                        {
                            Mp4Ranges.Enqueue(range);
                            if (!TryParseShortMediaRange(range, data.Length, out offset, out length))
                            {
                                context.Response.StatusCode = 416;
                                context.Response.Headers["Content-Range"] = $"bytes */{data.Length}";
                                context.Response.ContentLength64 = 0;
                                return;
                            }
                            context.Response.StatusCode = 206;
                            context.Response.Headers["Content-Range"] = $"bytes {offset}-{offset + length - 1}/{data.Length}";
                        }
                        context.Response.ContentLength64 = length;
                        if (context.Request.HttpMethod != "HEAD")
                            await context.Response.OutputStream.WriteAsync(data.AsMemory(offset, length), _cancellation.Token);
                    }
                    catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
                    finally
                    {
                        RequestTimings.Enqueue(new { item = requestItem, kind = requestKind,
                            elapsedMilliseconds = requestWatch.Elapsed.TotalMilliseconds });
                        context.Response.Close();
                    }
        }

        private static bool TryParseShortMediaRange(string value, int size, out int offset, out int length)
        {
            offset = length = 0;
            if (size <= 0 || !value.StartsWith("bytes=", StringComparison.Ordinal)) return false;
            var range = value[6..].Split('-');
            if (range.Length != 2) return false;
            if (range[0].Length == 0)
            {
                if (!long.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                    return false;
                length = (int)Math.Min(size, suffix);
                offset = size - length;
                return true;
            }
            if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= size)
                return false;
            var end = (long)size - 1;
            if (range[1].Length != 0 && !long.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out end))
                return false;
            end = Math.Min(end, size - 1);
            if (end < offset) return false;
            length = (int)(end - offset + 1);
            return true;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            _serving.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }
    }
}
