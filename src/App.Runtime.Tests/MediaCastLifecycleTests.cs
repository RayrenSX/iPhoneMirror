using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMediaCastLifecycleTests()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            TestMediaCastBufferAndPauseLifecycle();
            TestMediaCastCompletedItemLifecycle();
            TestMediaCastExternalCompletedItemLifecycle();
            TestMediaCastDirectCompletionIgnoresHlsGuard();
            TestMediaCastPrematureEndLifecycle();
            TestMediaCastExternalPrematureEndLifecycle();
            TestMediaCastExhaustedRecoveryLifecycle();
            TestMediaCastFailedItemRetryLifecycle();
            TestMediaCastSeekInvalidatesQueuedRecovery();
            TestMediaCastObservedPositionLifecycle();
            TestMediaCastRunningProducerRecoveryLifecycle();
            TestMediaCastPauseDuringEofGraceLifecycle();
            TestMediaCastQueuedRecoveryRetiresRenderer();
            TestMediaCastOpeningBudgetLifecycle();
            TestMediaCastPreparedSourceLifecycle();
            Console.WriteLine("PASS actual MainWindow media lifecycle: FCUP/external signed HLS/direct MP4 completion retains the sender session; stale callbacks and next Play are isolated; HLS premature EOF and recovery remain bounded.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestMediaCastBufferAndPauseLifecycle()
    {
        using var fixture = new MediaCastLifecycleFixture();
        var window = fixture.Window;
        SetKeyboardField(window, "_mediaOpened", false);
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Opening must be loading.");
        SetKeyboardField(window, "_mediaOpened", true);
        fixture.AssertStatus(MediaCastPlaybackStatus.Playing, "A ready playing item must be playing.");

        KeyboardCall(window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Buffering must not masquerade as a user pause.");
        fixture.Send(MediaCastCommand.Pause, 2);
        fixture.AssertStatus(MediaCastPlaybackStatus.Paused, "A user pause during buffering must stay paused.");
        fixture.Send(MediaCastCommand.Resume, 3);
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Resume must keep loading until buffering ends.");
        KeyboardCall(window, "OnMediaCastBufferingEnded", fixture.Element, fixture.Generation);
        fixture.AssertStatus(MediaCastPlaybackStatus.Playing, "Playback must recover its playing status after buffering.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastCompletedItemLifecycle()
    {
        using var fixture = new MediaCastLifecycleFixture();
        var window = fixture.Window;
        SetKeyboardField(window, "_mediaProgramDuration", 6.6d);
        KeyboardCall(window, "SetMediaCastTimelinePosition", 6.517d, false);
        fixture.StartTimers();
        var cancellation = (CancellationTokenSource)KeyboardField(window, "_mediaRecoveryCancellation");
        KeyboardCall(window, "QueueMediaCastCompletion", true);
        fixture.AssertStatus(MediaCastPlaybackStatus.Completed, "A naturally completed advertisement must retain a distinct completion state.");
        InteractionAssert(Math.Abs((double)KeyboardField(window, "_mediaStartPosition") - 6.6) < 0.001,
            "Completion did not retain the complete programme position.");
        InteractionAssert(cancellation.IsCancellationRequested,
            "Item completion left a delayed recovery able to restart the completed item.");
        fixture.AssertTimersStopped();

        // These are the real callbacks from the same bound MediaElement. A
        // queued callback must not revive an item after its terminal report.
        KeyboardCall(window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
        KeyboardCall(window, "OnMediaCastBufferingEnded", fixture.Element, fixture.Generation);
        KeyboardCall(window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
        KeyboardCall(window, "OnMediaPlaybackTimerTick", null, EventArgs.Empty);
        KeyboardCall(window, "QueueHlsProgramDuration", fixture.Generation,
            (Uri)KeyboardField(window, "_mediaSource"), 120d);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        fixture.AssertStatus(MediaCastPlaybackStatus.Completed, "A late callback resurrected a completed item.");
        InteractionAssert(!(bool)KeyboardField(window, "_mediaShouldPlay") &&
            Math.Abs((double)KeyboardField(window, "_mediaStartPosition") - 6.6) < 0.001,
            "A late callback changed the terminal item position or play intent.");
        InteractionAssert(Math.Abs((double)KeyboardField(window, "_mediaProgramDuration") - 6.6) < 0.001,
            "A delayed FFmpeg duration callback changed an already completed item.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastPrematureEndLifecycle()
    {
        using var fixture = new MediaCastLifecycleFixture();
        var window = fixture.Window;
        SetKeyboardField(window, "_mediaProgramDuration", 855.92d);
        KeyboardCall(window, "SetMediaCastTimelinePosition", 845.675d, false);
        SetKeyboardField(window, "_mediaOpened", false);
        KeyboardCall(window, "QueueMediaCastCompletion", true);
        InteractionAssert((bool)KeyboardField(window, "_mediaPrematureEndRetried"),
            "The first premature EOF did not consume its bounded recovery attempt.");
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Premature EOF recovery must stay loading.");
        var revision = (int)KeyboardField(window, "_mediaRecoveryRevision");

        // Volume/Pause/Resume commands advance the protocol command ID. They
        // must not grant another EOF recovery budget for the same source.
        fixture.Send(MediaCastCommand.Volume, 41);
        KeyboardCall(window, "QueueMediaCastCompletion", true);
        fixture.AssertStatus(MediaCastPlaybackStatus.Failed, "Repeated premature EOF must fail rather than claim completion.");
        InteractionAssert((int)KeyboardField(window, "_mediaRecoveryRevision") > revision,
            "Terminal failure did not invalidate the queued recovery continuation.");
        InteractionAssert(Math.Abs((double)KeyboardField(window, "_mediaStartPosition") - 845.675) < 0.001,
            "Premature EOF failure incorrectly advanced to the programme endpoint.");
        fixture.AssertTimersStopped();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        fixture.AssertStatus(MediaCastPlaybackStatus.Failed, "An obsolete recovery restarted the failed item.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastExternalCompletedItemLifecycle()
    {
        foreach (var hls in new[] { true, false })
        {
            using var fixture = new MediaCastLifecycleFixture();
            var window = fixture.Window;
            var source = new Uri(hls
                ? "http://video.example.test/iqiyi/ad.m3u8?authKey=signed-fixture&expiry=20261117"
                : "http://video.example.test/iqiyi/ad.mp4?authKey=signed-fixture&expiry=20261117");
            SetKeyboardField(window, "_mediaSource", source);
            SetKeyboardField(window, "_mediaUsesHlsBridge", hls);
            SetKeyboardField(window, "_mediaProgramDuration", 6.6d);
            SetKeyboardField(window, "_mediaObservedPosition", 6.517d);
            KeyboardCall(window, "SetMediaCastTimelinePosition", 6.517d, false);
            if (hls) SetKeyboardField(window, "_mediaHlsBridge", CreateCompletedLifecycleBridge());
            fixture.StartTimers();
            var cancellation = (CancellationTokenSource)KeyboardField(window, "_mediaRecoveryCancellation");
            KeyboardCall(window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
            var kind = hls ? "External signed HLS" : "Direct MP4";
            fixture.AssertStatus(MediaCastPlaybackStatus.Completed,
                kind + " natural EOF must complete only the item, preserving the sender's queue.");
            InteractionAssert(cancellation.IsCancellationRequested &&
                Math.Abs((double)KeyboardField(window, "_mediaStartPosition") - 6.6) < 0.001,
                kind + " completion left recovery active or lost its endpoint.");
            fixture.AssertTimersStopped();

            KeyboardCall(window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
            KeyboardCall(window, "OnMediaCastBufferingEnded", fixture.Element, fixture.Generation);
            KeyboardCall(window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
            KeyboardCall(window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
            KeyboardCall(window, "OnMediaPlaybackTimerTick", null, EventArgs.Empty);
            KeyboardCall(window, "QueueHlsProgramDuration", fixture.Generation, source, 120d);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            fixture.AssertStatus(MediaCastPlaybackStatus.Completed, kind + " late callback revived a completed item.");
            InteractionAssert(!(bool)KeyboardField(window, "_mediaShouldPlay") &&
                Math.Abs((double)KeyboardField(window, "_mediaProgramDuration") - 6.6) < 0.001,
                kind + " late callback changed the terminal duration or play intent.");
            fixture.AssertSessionRetained();

            using var nextSource = new TcpListener(IPAddress.Loopback, 0);
            nextSource.Start();
            var next = new Uri($"http://127.0.0.1:{((IPEndPoint)nextSource.LocalEndpoint).Port}/next.mp4");
            KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest(2,
                MediaCastCommand.Play, MediaCastFlags.None, next.AbsoluteUri, 120, 0, 1));
            var nextElement = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
            var nextGeneration = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
            InteractionAssert(nextGeneration > fixture.Generation && !ReferenceEquals(nextElement, fixture.Element) &&
                Equals(KeyboardField(window, "_mediaSource"), next) && Equals(nextElement.Source, next) &&
                Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Unknown) &&
                (double)KeyboardField(window, "_mediaObservedPosition") == 0,
                kind + " completion prevented or contaminated the sender's next Play.");
            KeyboardCall(window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
            KeyboardCall(window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
            KeyboardCall(window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
            KeyboardCall(window, "QueueHlsProgramDuration", fixture.Generation, source, 500d);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(ReferenceEquals(nextElement, KeyboardField(window, "MediaCastMediaElement")) &&
                Equals(KeyboardField(window, "_mediaSource"), next) &&
                Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Unknown) &&
                Math.Abs((double)KeyboardField(window, "_mediaProgramDuration") - 120) < 0.001,
                kind + " retired-item callbacks changed the next item.");
            fixture.AssertSessionRetained();
        }
        Console.WriteLine("PASS external signed HLS and direct MP4 natural completion: sender retained, late callbacks ignored, next Play accepted.");
    }

    private static void TestMediaCastDirectCompletionIgnoresHlsGuard()
    {
        using var fixture = new MediaCastLifecycleFixture();
        SetKeyboardField(fixture.Window, "_mediaSource", new Uri("http://video.example.test/movie.mp4?token=signed-fixture"));
        SetKeyboardField(fixture.Window, "_mediaUsesHlsBridge", false);
        SetKeyboardField(fixture.Window, "_mediaProgramDuration", 120d);
        KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 30d, false);
        KeyboardCall(fixture.Window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
        fixture.AssertStatus(MediaCastPlaybackStatus.Completed,
            "A direct MP4 natural EOF must not be retried using the HLS premature-end heuristic.");
        InteractionAssert(!(bool)KeyboardField(fixture.Window, "_mediaPrematureEndRetried"),
            "Direct MP4 completion consumed an HLS retry budget.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastExternalPrematureEndLifecycle()
    {
        using var fixture = new MediaCastLifecycleFixture();
        SetKeyboardField(fixture.Window, "_mediaSource",
            new Uri("http://video.example.test/iqiyi/programme.m3u8?authKey=signed-fixture&expiry=20261117"));
        SetKeyboardField(fixture.Window, "_mediaProgramDuration", 855.92d);
        KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 845.675d, false);
        SetKeyboardField(fixture.Window, "_mediaOpened", false);
        KeyboardCall(fixture.Window, "QueueMediaCastCompletion", true);
        InteractionAssert((bool)KeyboardField(fixture.Window, "_mediaPrematureEndRetried"),
            "External HLS premature EOF lost its bounded recovery protection.");
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "External HLS premature EOF must recover rather than complete.");
        fixture.Send(MediaCastCommand.Volume, 41);
        KeyboardCall(fixture.Window, "QueueMediaCastCompletion", true);
        fixture.AssertStatus(MediaCastPlaybackStatus.Failed, "Repeated external HLS premature EOF must fail the item.");
        InteractionAssert(Math.Abs((double)KeyboardField(fixture.Window, "_mediaStartPosition") - 845.675) < 0.001,
            "External HLS premature EOF incorrectly advanced to the programme endpoint.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        fixture.AssertStatus(MediaCastPlaybackStatus.Failed, "An obsolete recovery restarted failed external HLS.");
        fixture.AssertTimersStopped();
        fixture.AssertSessionRetained();
        Console.WriteLine("PASS external signed HLS premature EOF: one bounded retry, then failed item with sender session retained.");
    }

    private static HlsMediaPlaybackBridge CreateCompletedLifecycleBridge()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/completed.ts");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--media-bridge-idle");
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        InteractionAssert(process.WaitForExit(5000) && process.ExitCode == 0,
            "Completed HLS producer fixture did not exit successfully.");
        var constructor = typeof(HlsMediaPlaybackBridge).GetConstructors(KeyboardTestMembers).Single();
        return (HlsMediaPlaybackBridge)constructor.Invoke(new object?[] { process, listener, uri, null });
    }

    private static void TestMediaCastExhaustedRecoveryLifecycle()
    {
        foreach (var external in new[] { false, true })
        {
            using var fixture = new MediaCastLifecycleFixture();
            if (external)
                SetKeyboardField(fixture.Window, "_mediaSource",
                    new Uri("http://127.0.0.1:1/signed-media/episode.m3u8?signature=fixture"));
            fixture.StartTimers();
            var cancellation = (CancellationTokenSource)KeyboardField(fixture.Window, "_mediaRecoveryCancellation");
            var backoff = (MediaRecoveryBackoff)KeyboardField(fixture.Window, "_mediaRecoveryBackoff");
            for (var i = 0; i < 12; i++)
                InteractionAssert(backoff.TryGetNext(out _, out _), "Recovery fixture budget ended prematurely.");
            KeyboardCall(fixture.Window, "QueueLiveMediaRecovery", "fixture upstream timeout");
            var kind = external ? "External signed HLS" : "FCUP";
            fixture.AssertStatus(MediaCastPlaybackStatus.Failed, kind + " exhausted recovery must fail only the item.");
            InteractionAssert(cancellation.IsCancellationRequested &&
                Math.Abs((double)KeyboardField(fixture.Window, "_mediaStartPosition") - 30) < 0.001,
                kind + " exhausted recovery changed the last position or left recovery active.");
            fixture.AssertTimersStopped();
            KeyboardCall(fixture.Window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
            KeyboardCall(fixture.Window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
            fixture.Send(MediaCastCommand.Volume, 61);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            fixture.AssertStatus(MediaCastPlaybackStatus.Failed, kind + " late callback revived exhausted recovery.");
            fixture.AssertSessionRetained();
        }
        Console.WriteLine("PASS FCUP and external signed HLS exhausted recovery: failed item, frozen position, canceled retries and retained sender session.");
    }

    private static void TestMediaCastFailedItemRetryLifecycle()
    {
        InteractionAssert(HlsMediaPlaybackBridge.FindBundledFfmpeg() is not null,
            "Failed-item restart regression requires the trusted bundled FFmpeg.");
        foreach (var seek in new[] { true, false })
        {
            using var fixture = new MediaCastLifecycleFixture();
            var window = fixture.Window;
            SetKeyboardField(window, "_mediaProgramDuration", 855.92d);
            KeyboardCall(window, "SetMediaCastTimelinePosition", 845.675d, false);
            KeyboardCall(window, "FinishMediaCastItem", false, "fixture failed item");
            InteractionAssert(KeyboardField(window, "_mediaHlsBridge") is null,
                "Failed item retained its disposed bridge.");
            var cancelled = (CancellationTokenSource)KeyboardField(window, "_mediaRecoveryCancellation");
            if (seek) KeyboardCall(window, "SeekMediaCastToPosition", 840d, false);
            else fixture.Send(MediaCastCommand.Resume, 51);
            var expectedPosition = seek ? 840d : 845.675d;
            InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Unknown) &&
                KeyboardField(window, "_mediaHlsBridge") is not null &&
                Math.Abs((double)KeyboardField(window, "_mediaBridgeOffset") - expectedPosition) < 0.001,
                "Explicit seek/replay did not replace the disposed HLS bridge at the requested programme position.");
            InteractionAssert(!ReferenceEquals(cancelled, KeyboardField(window, "_mediaRecoveryCancellation")) &&
                !((CancellationTokenSource)KeyboardField(window, "_mediaRecoveryCancellation")).IsCancellationRequested,
                "Explicit seek/replay reused the terminal item's cancelled recovery token.");
            if (!seek) fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Explicit replay must load the replacement stream.");
            fixture.AssertSessionRetained();
        }
    }

    private static void TestMediaCastSeekInvalidatesQueuedRecovery()
    {
        using var fixture = new MediaCastLifecycleFixture();
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var port = ((IPEndPoint)upstream.LocalEndpoint).Port;
        SetKeyboardField(fixture.Window, "_mediaSource", new Uri($"http://127.0.0.1:{port}/" +
            new string('a', 48) + "/item/stream.m3u8"));
        KeyboardCall(fixture.Window, "QueueLiveMediaRecovery", "fixture queued reconnect");
        var recoveryRevision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
        KeyboardCall(fixture.Window, "SeekMediaCastToPosition", 40d, false);
        var soughtBridge = KeyboardField(fixture.Window, "_mediaHlsBridge");
        var soughtElement = KeyboardField(fixture.Window, "MediaCastMediaElement");
        InteractionAssert((int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") > recoveryRevision,
            "Explicit seek did not invalidate the earlier scheduled recovery.");
        // The deliberately unanswered upstream leaves the replacement opening
        // long enough for the previous 250ms recovery delay to expire.
        AdvanceDispatcher(TimeSpan.FromMilliseconds(450));
        InteractionAssert(ReferenceEquals(soughtBridge, KeyboardField(fixture.Window, "_mediaHlsBridge")) &&
            ReferenceEquals(soughtElement, KeyboardField(fixture.Window, "MediaCastMediaElement")),
            "A queued recovery rebound the player's new seek source while it was still opening.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastObservedPositionLifecycle()
    {
        using (var fixture = new MediaCastLifecycleFixture())
        {
            SetKeyboardField(fixture.Window, "_mediaProgramDuration", 1d);
            SetKeyboardField(fixture.Window, "_mediaObservedPosition", 0.745d);
            KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 0.745d, true);
            // The real MediaElement has no source and reads zero, matching
            // WMF's reset immediately before its MediaEnded notification.
            KeyboardCall(fixture.Window, "OnMediaPlaybackTimerTick", null, EventArgs.Empty);
            InteractionAssert((double)KeyboardField(fixture.Window, "_mediaStartPosition") >= 0.745,
                "An EOF position reset overwrote the last actual playback observation.");
            KeyboardCall(fixture.Window, "QueueMediaCastCompletion", true);
            fixture.AssertStatus(MediaCastPlaybackStatus.Completed,
                "A rendered one-second item was falsely classified as premature EOF.");
            InteractionAssert(!(bool)KeyboardField(fixture.Window, "_mediaPrematureEndRetried"),
                "A near-end observation triggered an unnecessary short-item recovery.");
        }
        using (var fixture = new MediaCastLifecycleFixture())
        using (var upstream = new TcpListener(IPAddress.Loopback, 0))
        {
            upstream.Start();
            var port = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var source = new Uri($"http://127.0.0.1:{port}/" + new string('a', 48) + "/item/stream.m3u8");
            SetKeyboardField(fixture.Window, "_mediaSource", source);
            SetKeyboardField(fixture.Window, "_mediaObservedPosition", 90d);
            KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 90d, false);
            KeyboardCall(fixture.Window, "SeekMediaCastToPosition", 20d, false);
            InteractionAssert((double)KeyboardField(fixture.Window, "_mediaObservedPosition") == 20 &&
                (double)KeyboardCall(fixture.Window, "ReadMediaCastTimelinePosition")! == 20,
                "A backward seek inherited the previous stream's higher observed position.");
            KeyboardCall(fixture.Window, "OnMediaCastCommandReceived", new MediaCastRequest(2,
                MediaCastCommand.Play, MediaCastFlags.None, source.AbsoluteUri, 120, 0, 0));
            InteractionAssert((double)KeyboardField(fixture.Window, "_mediaObservedPosition") == 0,
                "A new Play inherited the previous item's observed position.");
            fixture.AssertSessionRetained();
        }
    }

    private static void TestMediaCastRunningProducerRecoveryLifecycle()
    {
        foreach (var regression in new[] { false, true })
        {
            using var fixture = new MediaCastLifecycleFixture();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/watchdog.ts");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--media-bridge-idle");
            var process = Process.Start(start)!;
            var constructor = typeof(HlsMediaPlaybackBridge).GetConstructors(KeyboardTestMembers).Single();
            var bridge = (HlsMediaPlaybackBridge)constructor.Invoke(new object?[] { process, listener, uri, null });
            SetKeyboardField(fixture.Window, "_mediaHlsBridge", bridge);
            SetKeyboardField(fixture.Window, "_mediaLastActualPosition", 10d);
            SetKeyboardField(fixture.Window, "_mediaObservedPosition", 10d);
            SetKeyboardField(fixture.Window, "_mediaLastActualAdvanceUtc", regression
                ? DateTime.UtcNow : DateTime.UtcNow - TimeSpan.FromSeconds(16));
            KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 10d, true);
            if (regression)
            {
                KeyboardCall(fixture.Window, "OnMediaPlaybackTimerTick", null, EventArgs.Empty);
                InteractionAssert(bridge.IsRunning, "A transient raw timestamp reset immediately tore down the producer.");
                SetKeyboardField(fixture.Window, "_mediaPositionRegressionSinceUtc", DateTime.UtcNow - TimeSpan.FromSeconds(2));
            }
            InteractionAssert(bridge.IsRunning, "Running-producer watchdog fixture did not retain its producer.");
            KeyboardCall(fixture.Window, "OnMediaPlaybackTimerTick", null, EventArgs.Empty);
            fixture.AssertStatus(MediaCastPlaybackStatus.Loading,
                regression ? "A sustained backward timestamp must recover as loading." : "An alive but stalled producer must recover as loading.");
            InteractionAssert(KeyboardField(fixture.Window, "_mediaHlsBridge") is null && !bridge.IsRunning,
                "Stalled playback retained the old forward-only stream instead of rebuilding it.");
            InteractionAssert(Math.Abs((double)KeyboardField(fixture.Window, "_mediaStartPosition") - 10) < 0.001 &&
                Math.Abs((double)KeyboardCall(fixture.Window, "ReadMediaCastTimelinePosition")! - 10) < 0.001,
                "Stall recovery synthesized time or skipped beyond the last actual observation.");
            var revision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
            KeyboardCall(fixture.Window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
            KeyboardCall(fixture.Window, "OnMediaCastBufferingEnded", fixture.Element, fixture.Generation);
            KeyboardCall(fixture.Window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
            KeyboardCall(fixture.Window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
            InteractionAssert((int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") == revision &&
                !(bool)KeyboardField(fixture.Window, "_mediaOpened") &&
                Math.Abs((double)KeyboardField(fixture.Window, "_mediaStartPosition") - 10) < 0.001,
                "The retired renderer changed the queued recovery before its replacement was bound.");
            fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "A late retired-renderer callback revived playback during recovery.");
            fixture.AssertSessionRetained();
        }
    }

    private static void TestMediaCastPauseDuringEofGraceLifecycle()
    {
        using var fixture = new MediaCastLifecycleFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/paused-eof.ts");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--media-bridge-idle");
        var process = Process.Start(start)!;
        var constructor = typeof(HlsMediaPlaybackBridge).GetConstructors(KeyboardTestMembers).Single();
        var bridge = (HlsMediaPlaybackBridge)constructor.Invoke(new object?[] { process, listener, uri, null });
        SetKeyboardField(fixture.Window, "_mediaHlsBridge", bridge);
        SetKeyboardField(fixture.Window, "_mediaBridgeEndCheckRevision", 17);
        var revision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
        var check = (Task)KeyboardCall(fixture.Window, "CheckMediaCastBridgeEndAsync", fixture.Element,
            fixture.Generation, bridge, 0d, 17, CancellationToken.None)!;
        fixture.Send(MediaCastCommand.Pause, 52);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        InteractionAssert(check.IsCompletedSuccessfully && bridge.IsRunning &&
            ReferenceEquals(bridge, KeyboardField(fixture.Window, "_mediaHlsBridge")) &&
            (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") == revision,
            "A pending EOF grace check restarted a deliberately paused player.");
        fixture.AssertStatus(MediaCastPlaybackStatus.Paused, "An EOF recheck changed explicit pause intent.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastQueuedRecoveryRetiresRenderer()
    {
        using var fixture = new MediaCastLifecycleFixture();
        KeyboardCall(fixture.Window, "SetMediaCastTimelinePosition", 30d, false);
        KeyboardCall(fixture.Window, "QueueLiveMediaRecovery", "fixture failed segment");
        var revision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
        KeyboardCall(fixture.Window, "CompleteMediaCastMediaOpened", fixture.Element, fixture.Generation);
        KeyboardCall(fixture.Window, "CompleteMediaCastMediaEnded", fixture.Element, fixture.Generation);
        KeyboardCall(fixture.Window, "OnMediaCastBufferingStarted", fixture.Element, fixture.Generation);
        KeyboardCall(fixture.Window, "OnMediaCastBufferingEnded", fixture.Element, fixture.Generation);
        InteractionAssert((int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") == revision &&
            !(bool)KeyboardField(fixture.Window, "_mediaOpened"),
            "A failed segment's retired renderer canceled its pending replacement.");
        fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Old failure callbacks revived a recovery that had already been queued.");
        fixture.AssertSessionRetained();
    }

    private static void TestMediaCastOpeningBudgetLifecycle()
    {
        foreach (var scenario in new[] { "probing", "idle", "absolute", "render_grace" })
        {
            using var fixture = new MediaCastLifecycleFixture();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/opening.ts");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--media-bridge-idle");
            var process = Process.Start(start)!;
            var constructor = typeof(HlsMediaPlaybackBridge).GetConstructors(KeyboardTestMembers).Single();
            var bridge = (HlsMediaPlaybackBridge)constructor.Invoke(new object?[] { process, listener, uri, null });
            var elapsed = TimeSpan.Zero;
            var budget = new MediaCastStartupBudget(() => elapsed);
            var budgetField = typeof(HlsMediaPlaybackBridge).GetField("_startupBudget", KeyboardTestMembers) ??
                typeof(HlsMediaPlaybackBridge).GetField("<StartupBudget>k__BackingField", KeyboardTestMembers)!;
            budgetField.SetValue(bridge, budget);
            SetKeyboardField(fixture.Window, "_mediaHlsBridge", bridge);
            SetKeyboardField(fixture.Window, "_mediaOpened", false);
            SetKeyboardField(fixture.Window, "_mediaOpenedAtUtc", DateTime.UtcNow - TimeSpan.FromSeconds(21));
            elapsed = TimeSpan.FromSeconds(11);
            budget.MarkInputReady(0);
            if (scenario is "absolute" or "render_grace")
            {
                elapsed = TimeSpan.FromSeconds(22);
                budget.MarkInputReady(1);
                elapsed = TimeSpan.FromSeconds(33);
                budget.MarkBytesReceived(1);
                elapsed = TimeSpan.FromSeconds(44);
                if (scenario == "render_grace") budget.MarkResponseReady();
                else budget.MarkBytesReceived(2);
            }
            if (scenario == "absolute")
            {
                elapsed = TimeSpan.FromSeconds(55);
                budget.MarkBytesReceived(3);
            }
            elapsed = TimeSpan.FromSeconds(scenario switch
            {
                "probing" => 21,
                "idle" => 31,
                "absolute" => 61,
                _ => 58,
            });
            var revision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
            KeyboardCall(fixture.Window, "OnMediaOpeningTimerTick", null, EventArgs.Empty);
            var shouldRecover = scenario is "idle" or "absolute";
            InteractionAssert(shouldRecover
                ? KeyboardField(fixture.Window, "_mediaHlsBridge") is null && !bridge.IsRunning &&
                    (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") > revision
                : ReferenceEquals(bridge, KeyboardField(fixture.Window, "_mediaHlsBridge")) && bridge.IsRunning &&
                    (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") == revision,
                $"Opening watchdog ignored the shared startup budget: {scenario}.");
            fixture.AssertStatus(MediaCastPlaybackStatus.Loading, "Opening watchdog changed the pending item's loading state.");
            fixture.AssertSessionRetained();
        }
        using (var fixture = new MediaCastLifecycleFixture())
        {
            SetKeyboardField(fixture.Window, "_mediaOpened", false);
            SetKeyboardField(fixture.Window, "_mediaUsesHlsBridge", false);
            SetKeyboardField(fixture.Window, "_mediaOpenedAtUtc", DateTime.UtcNow - TimeSpan.FromSeconds(21));
            SetKeyboardField(fixture.Window, "_mediaSource", new Uri("http://127.0.0.1:1/direct.mp4"));
            KeyboardCall(fixture.Window, "OnMediaOpeningTimerTick", null, EventArgs.Empty);
            InteractionAssert(!(bool)KeyboardField(fixture.Window, "_mediaCastActive"),
                "A direct source lost its existing bounded opening timeout.");
            fixture.AssertTimersStopped();
        }
        Console.WriteLine("PASS actual MainWindow opening watchdog: healthy 21s probe and render grace survive; idle 31s, absolute 61s and direct-source timeout remain bounded.");
    }

    private static void TestMediaCastPreparedSourceLifecycle()
    {
        foreach (var command in new[] { MediaCastCommand.Stop, MediaCastCommand.Play,
                     MediaCastCommand.Seek, MediaCastCommand.Pause })
        {
            using var fixture = new MediaCastLifecycleFixture();
            using var upstream = new TcpListener(IPAddress.Loopback, 0);
            upstream.Start();
            var port = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var bridge = CreateControlledPreparationBridge(out var ready);
            SetKeyboardField(fixture.Window, "_mediaHlsBridge", bridge);
            SetKeyboardField(fixture.Window, "_mediaOpened", false);
            SetKeyboardField(fixture.Window, "_mediaPlaying", false);
            SetKeyboardField(fixture.Window, "_mediaSource", new Uri($"http://127.0.0.1:{port}/" +
                new string('a', 48) + "/item/stream.m3u8"));
            var token = ((CancellationTokenSource)KeyboardField(fixture.Window, "_mediaRecoveryCancellation")).Token;
            var binding = (Task)KeyboardCall(fixture.Window, "BindPreparedMediaCastSourceAsync",
                fixture.Element, bridge.PlaybackUri, fixture.Generation, bridge, token)!;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(!binding.IsCompleted && fixture.Element.Source is null,
                "A preparing bridge assigned its source before readiness.");

            var replacement = new Uri($"http://127.0.0.1:{port}/next.mp4");
            if (command == MediaCastCommand.Play)
                KeyboardCall(fixture.Window, "OnMediaCastCommandReceived", new MediaCastRequest(2,
                    MediaCastCommand.Play, MediaCastFlags.None, replacement.AbsoluteUri, 120, 0, 1));
            else if (command == MediaCastCommand.Seek)
                KeyboardCall(fixture.Window, "SeekMediaCastToPosition", 40d, false);
            else fixture.Send(command, 2);
            var current = (MediaElement)KeyboardField(fixture.Window, "MediaCastMediaElement");
            var revision = (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision");
            ready();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(binding.IsCompletedSuccessfully &&
                (int)KeyboardField(fixture.Window, "_mediaRecoveryRevision") == revision,
                $"A delayed prepared-source continuation changed recovery after {command}.");
            if (command == MediaCastCommand.Pause)
            {
                InteractionAssert(ReferenceEquals(current, fixture.Element) &&
                    Equals(current.Source, bridge.PlaybackUri) &&
                    !(bool)KeyboardField(fixture.Window, "_mediaShouldPlay") &&
                    !(bool)KeyboardField(fixture.Window, "_mediaPlaying"),
                    "Readiness lost a pause requested while the bridge was preparing.");
                fixture.AssertStatus(MediaCastPlaybackStatus.Paused,
                    "The prepared source resumed after a pause during preparation.");
                fixture.AssertSessionRetained();
            }
            else
            {
                InteractionAssert(fixture.Element.Source is null &&
                    (command == MediaCastCommand.Stop || !ReferenceEquals(current, fixture.Element)),
                    $"An obsolete bridge rebound its retired renderer after {command}.");
                if (command == MediaCastCommand.Stop)
                    InteractionAssert(!(bool)KeyboardField(fixture.Window, "_mediaCastActive"),
                        "A delayed readiness result revived a stopped session.");
                else
                {
                    InteractionAssert(command == MediaCastCommand.Play
                        ? Equals(current.Source, replacement)
                        : current.Source is null && Math.Abs((double)KeyboardField(fixture.Window, "_mediaBridgeOffset") - 40) < 0.001,
                        $"An obsolete readiness result changed the replacement chosen by {command}.");
                    fixture.AssertSessionRetained();
                }
            }
        }
        Console.WriteLine("PASS actual MainWindow prepared-source races: Stop, next Play and Seek reject late readiness; Pause survives readiness.");
    }

    private static HlsMediaPlaybackBridge CreateControlledPreparationBridge(out Action ready)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/prepared.ts");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--media-bridge-idle");
        var process = Process.Start(start)!;
        var constructor = typeof(HlsMediaPlaybackBridge).GetConstructors(KeyboardTestMembers).Single();
        var bridge = (HlsMediaPlaybackBridge)constructor.Invoke(new object?[] { process, listener, uri, null });
        var bufferType = typeof(HlsMediaPlaybackBridge).GetNestedType("StartupBuffer", KeyboardTestMembers)!;
        ready = (Action)typeof(Program).GetMethod(nameof(InstallControlledPreparation), KeyboardTestMembers | BindingFlags.Static)!
            .MakeGenericMethod(bufferType).Invoke(null, [bridge])!;
        return bridge;
    }

    private static Action InstallControlledPreparation<T>(HlsMediaPlaybackBridge bridge)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetKeyboardField(bridge, "_startup", new Lazy<Task<T>>(() => completion.Task));
        return () => completion.SetResult((T)Activator.CreateInstance(typeof(T), KeyboardTestMembers,
            null, [ReadOnlyMemory<byte>.Empty, false, null], null)!);
    }

    private sealed class MediaCastLifecycleFixture : IDisposable
    {
        internal readonly MainWindow Window = new() { ShowInTaskbar = false };
        internal readonly MediaElement Element;
        internal readonly long Generation;
        private readonly MainViewModel _viewModel;
        private readonly EventInfo _stopEvent;
        private readonly Action _stopObserved;
        private int _stopRequests;

        internal MediaCastLifecycleFixture()
        {
            _viewModel = (MainViewModel)KeyboardField(Window, "_viewModel");
            _stopObserved = () => _stopRequests++;
            _stopEvent = _viewModel.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
            _stopEvent.GetAddMethod(true)!.Invoke(_viewModel, [_stopObserved]);
            KeyboardCall(_viewModel, "BeginMediaCast", 1d);
            Element = (MediaElement)KeyboardField(Window, "MediaCastMediaElement");
            var gate = (MediaCastEventGate)KeyboardField(Window, "_mediaCastEvents");
            Generation = gate.BeginGeneration();
            InteractionAssert(gate.TryBind(Generation, Element), "Media lifecycle fixture could not bind its player.");
            SetKeyboardField(Window, "_mediaCastActive", true);
            SetKeyboardField(Window, "_mediaStopped", false);
            SetKeyboardField(Window, "_mediaCommandId", 1UL);
            SetKeyboardField(Window, "_mediaOpened", true);
            SetKeyboardField(Window, "_mediaShouldPlay", true);
            SetKeyboardField(Window, "_mediaPlaying", true);
            SetKeyboardField(Window, "_mediaUsesHlsBridge", true);
            SetKeyboardField(Window, "_mediaIsLive", false);
            SetKeyboardField(Window, "_mediaWaitingForFirstFrame", false);
            SetKeyboardField(Window, "_mediaProgramDuration", 120d);
            SetKeyboardField(Window, "_mediaSource", new Uri("http://127.0.0.1:1/" + new string('a', 48) + "/item/stream.m3u8"));
            KeyboardCall(Window, "SetMediaCastTimelinePosition", 30d, false);
        }

        internal void Send(MediaCastCommand command, ulong id) =>
            KeyboardCall(Window, "OnMediaCastCommandReceived",
                new MediaCastRequest(id, command, MediaCastFlags.None, "", 0, 0, 1));

        internal void AssertStatus(MediaCastPlaybackStatus expected, string message) =>
            InteractionAssert(Equals(KeyboardCall(Window, "ReadMediaCastPlaybackStatus"), expected), message);

        internal void StartTimers()
        {
            ((DispatcherTimer)KeyboardField(Window, "_mediaOpeningTimer")).Start();
            ((DispatcherTimer)KeyboardField(Window, "_mediaPlaybackTimer")).Start();
        }

        internal void AssertTimersStopped() =>
            InteractionAssert(!((DispatcherTimer)KeyboardField(Window, "_mediaOpeningTimer")).IsEnabled &&
                !((DispatcherTimer)KeyboardField(Window, "_mediaPlaybackTimer")).IsEnabled,
                "A terminal FCUP item retained an active loading or playback timer.");

        internal void AssertSessionRetained() =>
            InteractionAssert(_stopRequests == 0 && (bool)KeyboardField(Window, "_mediaCastActive") &&
                _viewModel.IsMediaCasting && KeyboardField(Window, "_mediaSource") is not null,
                "An item state change cancelled the sender's casting session.");

        public void Dispose()
        {
            _stopEvent.GetRemoveMethod(true)!.Invoke(_viewModel, [_stopObserved]);
            KeyboardCall(Window, "StopMediaCastPlayback", "media_lifecycle_test_cleanup");
            CloseWorkspaceTestWindow(Window);
        }
    }
}
