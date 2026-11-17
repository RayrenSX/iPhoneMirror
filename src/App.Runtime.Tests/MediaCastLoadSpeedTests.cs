using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
    private static int RunMediaCastLoadSpeedTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        SegmentedPlaybackServer? source = null;
        ShortMediaPlaybackServer? nextSource = null;
        try
        {
            var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
            InteractionAssert(ffmpeg is not null, "Loading speed requires trusted bundled FFmpeg.");
            source = new SegmentedPlaybackServer(ffmpeg!, output, loadSpeed: true);
            Directory.CreateDirectory(Path.Combine(output, "next"));
            nextSource = new ShortMediaPlaybackServer(ffmpeg!, Path.Combine(output, "next"),
                [1d], [(160, 90)], [TimeSpan.Zero]);
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var viewModel = (MainViewModel)KeyboardField(window, "_viewModel");
            var stopRequests = 0;
            Action stopObserved = () => stopRequests++;
            var stopEvent = viewModel.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
            stopEvent.GetAddMethod(true)!.Invoke(viewModel, [stopObserved]);
            try
            {
                var results = new List<object>();
                (string Name, Uri Uri, double Duration)[] items =
                    [("finite-short", source.Source("load-short"), 12),
                     ("streaming", source.Source("load-stream"), 36),
                     ("next-short", nextSource.Source(0), 1)];
                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    var bytesBefore = source.ResponseBytes;
                    var requestsBefore = source.RequestCounts.Values.Sum();
                    KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest((ulong)index + 1,
                        MediaCastCommand.Play, MediaCastFlags.MuteSpecified | MediaCastFlags.Muted,
                        item.Uri.AbsoluteUri, 0, 0, 0));
                    var initialBridge = KeyboardField(window, "_mediaHlsBridge");
                    InteractionAssert(initialBridge is HlsMediaPlaybackBridge, "Loading speed fixture did not use the production bridge.");
                    var watch = Stopwatch.StartNew();
                    double? firstOpenedSeconds = null;
                    long? bytesAtOpen = null;
                    int? requestsAtOpen = null;
                    var maximum = 0d;
                    var positions = new List<double>();
                    string? previousHash = null;
                    var nextCapture = item.Duration == 1 ? 0.25 : 1d;
                    var captureStep = item.Duration == 1 ? 0.4 : 3d;
                    while (watch.Elapsed < TimeSpan.FromSeconds(item.Duration + 45))
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                        var terminal = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        InteractionAssert(terminal != MediaCastPlaybackStatus.Failed, $"Loading speed item {item.Name} failed.");
                        if (terminal == MediaCastPlaybackStatus.Unknown)
                            InteractionAssert(ReferenceEquals(initialBridge, KeyboardField(window, "_mediaHlsBridge")),
                                $"Loading speed item {item.Name} required bridge recovery.");
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        if ((bool)KeyboardField(window, "_mediaOpened"))
                        {
                            if (firstOpenedSeconds is null)
                            {
                                firstOpenedSeconds = watch.Elapsed.TotalSeconds;
                                bytesAtOpen = source.ResponseBytes - bytesBefore;
                                requestsAtOpen = source.RequestCounts.Values.Sum() - requestsBefore;
                            }
                            maximum = Math.Max(maximum, element.Position.TotalSeconds);
                        }
                        if (maximum >= nextCapture && maximum < item.Duration - 0.05)
                        {
                            var frame = Path.Combine(output, $"{item.Name}-{nextCapture:00.00}-frame.png");
                            CaptureVerifiedDiscontinuityFrame(window, frame);
                            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frame)));
                            InteractionAssert(previousHash != hash, $"Loading speed item {item.Name} clock moved with a frozen frame.");
                            previousHash = hash;
                            positions.Add(maximum);
                            nextCapture += captureStep;
                        }
                        if (terminal == MediaCastPlaybackStatus.Completed) break;
                    }
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed) &&
                        maximum > item.Duration - 0.5 && positions.Count >= (item.Duration == 1 ? 2 : (int)(item.Duration / 3)),
                        $"Loading speed item {item.Name} did not render all expected media: {maximum:F3}/{item.Duration:F3}.");
                    InteractionAssert(stopRequests == 0 && viewModel.IsMediaCasting &&
                        !(bool)KeyboardField(window, "_mediaPrematureEndRetried"),
                        $"Loading speed item {item.Name} stopped the sender session or retried premature EOF.");
                    results.Add(new { item.Name, item.Duration, firstOpenedSeconds, maximumActual = maximum,
                        elapsedSeconds = watch.Elapsed.TotalSeconds, bytesAtOpen, requestsAtOpen,
                        responseBytes = source.ResponseBytes - bytesBefore,
                        requestCount = source.RequestCounts.Values.Sum() - requestsBefore, framePositions = positions });
                    Console.WriteLine($"PASS WPF loading {item.Name}: opened={firstOpenedSeconds:F3}s actual={maximum:F3}s frames={positions.Count}.");
                }
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Actual MainWindow/FFmpeg/WPF; identical split fMP4 short/stream sources at 200ms per request and 1MB/s per connection, followed by a new one-second Play. Query-driven known-master fixture matches the broker's single-track projection. Does not emulate phone queue choices.",
                    items = results, stopRequests, requestCounts = source.RequestCounts,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]); }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "load_speed_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            source?.Dispose();
            nextSource?.Dispose();
            app.Shutdown();
        }
    }
}
