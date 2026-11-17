using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private static int RunMediaCastSlowStartupTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        ShortMediaPlaybackServer? source = null;
        try
        {
            var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
            InteractionAssert(ffmpeg is not null, "Slow startup requires the trusted bundled FFmpeg.");
            double[] durations = [11.04, 1.0];
            (int Width, int Height)[] sizes = [(160, 90), (160, 90)];
            source = new ShortMediaPlaybackServer(ffmpeg!, output, durations, sizes,
                [TimeSpan.FromSeconds(11), TimeSpan.Zero]);
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
                    KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest((ulong)item + 1,
                        MediaCastCommand.Play, MediaCastFlags.MuteSpecified | MediaCastFlags.Muted,
                        source.Source(item).AbsoluteUri, 0, 0, 0));
                    var watch = Stopwatch.StartNew();
                    double? openedSeconds = null;
                    var maximumPosition = 0d;
                    var frameSaved = false;
                    var tailSaved = false;
                    string? frameHash = null;
                    var frame = Path.Combine(output, $"item-{item + 1}-frame.png");
                    var tail = Path.Combine(output, $"item-{item + 1}-tail.png");
                    var recentSamples = new Queue<object>();
                    var captureTimings = new List<object>();
                    void SaveObservations() => File.WriteAllText(
                        Path.Combine(output, $"item-{item + 1}-observations.json"),
                        JsonSerializer.Serialize(new { maximumPosition, frameSaved, tailSaved, captureTimings,
                            samples = recentSamples.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
                    var initialBridge = KeyboardField(window, "_mediaHlsBridge");
                    InteractionAssert(initialBridge is HlsMediaPlaybackBridge,
                        "The slow-start fixture did not use the real HLS bridge.");
                    while (watch.Elapsed < TimeSpan.FromSeconds(item == 0 ? 55 : 10))
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        var status = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        recentSamples.Enqueue(new { elapsed = watch.Elapsed.TotalSeconds,
                            actual = element.Position.TotalSeconds,
                            productObserved = (double)KeyboardField(window, "_mediaObservedPosition"),
                            opened = (bool)KeyboardField(window, "_mediaOpened"), status = status.ToString() });
                        while (recentSamples.Count > 12) recentSamples.Dequeue();
                        if (status == MediaCastPlaybackStatus.Unknown &&
                            !ReferenceEquals(initialBridge, KeyboardField(window, "_mediaHlsBridge")))
                            SaveObservations();
                        InteractionAssert(status != MediaCastPlaybackStatus.Unknown ||
                            ReferenceEquals(initialBridge, KeyboardField(window, "_mediaHlsBridge")),
                            "A healthy slow item replaced its bridge during opening or playback.");
                        if ((bool)KeyboardField(window, "_mediaOpened"))
                        {
                            openedSeconds ??= watch.Elapsed.TotalSeconds;
                            maximumPosition = Math.Max(maximumPosition, element.Position.TotalSeconds);
                        }
                        if (!frameSaved && maximumPosition >= Math.Min(1.5, durations[item] / 2))
                        {
                            var captured = CaptureSlowStartupFrame(window, frame);
                            captureTimings.Add(new { name = "first", captured.RenderMilliseconds,
                                captured.EncodingMilliseconds, captured.Retries });
                            frameHash = captured.Hash;
                            frameSaved = true;
                        }
                        if (frameSaved && !tailSaved && maximumPosition >= Math.Max(
                                Math.Min(1.5, durations[item] / 2) + 0.15, durations[item] - 0.5))
                        {
                            var captured = CaptureSlowStartupFrame(window, tail);
                            captureTimings.Add(new { name = "tail", captured.RenderMilliseconds,
                                captured.EncodingMilliseconds, captured.Retries });
                            InteractionAssert(frameHash != captured.Hash,
                                "The slow-start item clock advanced without a changing video frame.");
                            tailSaved = true;
                        }
                        InteractionAssert(status != MediaCastPlaybackStatus.Failed,
                            "The slow-start item failed before natural completion.");
                        if (status == MediaCastPlaybackStatus.Completed) break;
                    }
                    SaveObservations();
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed) &&
                        maximumPosition >= durations[item] - 0.5 && frameSaved && tailSaved,
                        $"Slow-start item {item + 1} did not render and complete: actual={maximumPosition:F3}/{durations[item]:F3}.");
                    if (item == 0)
                        InteractionAssert(openedSeconds >= 20 && openedSeconds < 40,
                            $"The fixture must cross the old 15/20s startup deadlines before opening; actual {openedSeconds:F3}s.");
                    InteractionAssert(stopRequests == 0 && viewModel.IsMediaCasting &&
                        !(bool)KeyboardField(window, "_mediaPrematureEndRetried"),
                        "Slow startup completion stopped the session or used a premature-end retry.");
                    results.Add(new { item = item + 1, duration = durations[item], openedSeconds,
                        maximumPosition, elapsedSeconds = watch.Elapsed.TotalSeconds, frame, tail });
                    Console.WriteLine($"PASS slow WPF item {item + 1}: opened={openedSeconds:F3}s, actual={maximumPosition:F3}s, next Play retained.");
                }
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Actual MainWindow/FFmpeg/WPF: each input's first-item playlist response takes 11s, crossing old 15/20s deadlines; the next 1s item must play. This does not emulate a phone queue.",
                    items = results, stopRequests, source.PlaylistRequests, source.SegmentRequests,
                    requestTimings = source.RequestTimings.ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]); }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "slow_start_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            source?.Dispose();
            app.Shutdown();
        }
    }

    private readonly record struct SlowFrameCapture(string Hash, double RenderMilliseconds,
        double EncodingMilliseconds, int Retries);

    private static SlowFrameCapture CaptureSlowStartupFrame(MainWindow window, string path)
    {
        // Take the visual snapshot on its dispatcher, then keep pumping that
        // dispatcher while PNG encoding, disk I/O and pixel checks run off it.
        // Blocking near EOF would itself prevent the product's 250ms position
        // sampler from observing the tail before WMF resets Position.
        for (var retry = 0; retry <= 5; retry++)
        {
            var visual = (FrameworkElement)KeyboardField(window, "MediaCastVideoHost");
            var renderWatch = Stopwatch.StartNew();
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
                (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            renderWatch.Stop();
            var encoding = Task.Run(() =>
            {
                var encodingWatch = Stopwatch.StartNew();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);
                AssertRenderedMediaFrame(path);
                return (Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    Milliseconds: encodingWatch.Elapsed.TotalMilliseconds);
            });
            while (!encoding.IsCompleted) AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            try
            {
                var result = encoding.GetAwaiter().GetResult();
                return new(result.Hash, renderWatch.Elapsed.TotalMilliseconds, result.Milliseconds, retry);
            }
            catch (InvalidOperationException) when (retry < 5)
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            }
        }
        throw new InvalidOperationException("WPF slow-start video stayed black across the bounded capture retries.");
    }
}
