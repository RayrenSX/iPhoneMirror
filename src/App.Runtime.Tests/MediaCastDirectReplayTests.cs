using System.Diagnostics;
using System.IO;
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
    private static int RunMediaCastDirectReplayTests(string output, bool failureFirst = false)
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
            InteractionAssert(ffmpeg is not null, "Direct replay requires trusted bundled FFmpeg.");
            const double duration = 3.2;
            using var server = new ShortMediaPlaybackServer(ffmpeg!, output, [duration], [(320, 240)],
                [TimeSpan.Zero], mp4Item: 0);
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var viewModel = (MainViewModel)KeyboardField(window, "_viewModel");
            var stopRequests = 0;
            Action stopObserved = () => ++stopRequests;
            var stopEvent = viewModel.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
            stopEvent.GetAddMethod(true)!.Invoke(viewModel, [stopObserved]);
            var results = new List<object>();
            ulong command = 1;
            var source = server.Mp4Source(0);
            try
            {
                void Send(MediaCastCommand kind, double position = 0) =>
                    KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest(command++, kind,
                        MediaCastFlags.None, "", 0, position, 0));
                void Until(Func<bool> predicate, string failure, double seconds = 8)
                {
                    var watch = Stopwatch.StartNew();
                    while (!predicate() && watch.Elapsed.TotalSeconds < seconds)
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                    InteractionAssert(predicate(), failure);
                }
                void AssertRetained()
                {
                    InteractionAssert(stopRequests == 0 && viewModel.IsMediaCasting &&
                        (bool)KeyboardField(window, "_mediaCastActive"), "Direct replay stopped the sender session.");
                }
                void FailCurrent(string name)
                {
                    var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    var generation = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
                    var failure = (ExceptionRoutedEventArgs)Activator.CreateInstance(typeof(ExceptionRoutedEventArgs),
                        KeyboardTestMembers, null,
                        [MediaElement.MediaFailedEvent, element, new InvalidDataException("fixture native backend failure")], null)!;
                    KeyboardCall(window, "OnMediaCastMediaFailed", element, failure, generation);
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Failed),
                        name + " did not retain the native failure as a failed item.");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    AssertRetained();
                }
                void AssertRestarted(MediaElement retired, long generation, string name)
                {
                    var current = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    InteractionAssert(!ReferenceEquals(current, retired) && Equals(current.Source, source) &&
                        KeyboardField(window, "_mediaHlsBridge") is null &&
                        !(bool)KeyboardField(window, "_mediaUsesHlsBridge") &&
                        Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Unknown) &&
                        ((DispatcherTimer)KeyboardField(window, "_mediaOpeningTimer")).IsEnabled,
                        name + " did not create a fresh native opening attempt after the terminal item.");
                    InteractionAssert(Math.Abs(current.Volume - .33) < .001 && current.IsMuted,
                        name + " lost the current volume or mute setting.");
                    KeyboardCall(window, "CompleteMediaCastMediaEnded", retired, generation);
                    KeyboardCall(window, "CompleteMediaCastMediaOpened", retired, generation);
                    InteractionAssert(ReferenceEquals(current, KeyboardField(window, "MediaCastMediaElement")) &&
                        Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Unknown),
                        name + " accepted an obsolete renderer callback.");
                    AssertRetained();
                }
                void Complete(string name, double start)
                {
                    var maximum = start;
                    string? first = null;
                    var captured = false;
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed.TotalSeconds < 10)
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                        var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                        if ((bool)KeyboardField(window, "_mediaOpened"))
                            maximum = Math.Max(maximum, element.Position.TotalSeconds);
                        if (first is null && maximum > start + .25)
                            first = CaptureSlowStartupFrame(window, Path.Combine(output, name + "-first.png")).Hash;
                        if (!captured && first is not null && maximum >= duration - .4)
                        {
                            var tail = CaptureSlowStartupFrame(window, Path.Combine(output, name + "-tail.png")).Hash;
                            InteractionAssert(tail != first, name + " rendered a frozen video frame.");
                            captured = true;
                        }
                        var terminal = (MediaCastPlaybackStatus)KeyboardField(window, "_mediaTerminalStatus");
                        InteractionAssert(terminal != MediaCastPlaybackStatus.Failed, name + " failed.");
                        if (terminal == MediaCastPlaybackStatus.Completed) break;
                    }
                    InteractionAssert(Equals(KeyboardField(window, "_mediaTerminalStatus"), MediaCastPlaybackStatus.Completed) &&
                        maximum >= duration - .4 && captured &&
                        !(bool)KeyboardField(window, "_mediaPrematureEndRetried"), name + " did not render to natural completion.");
                    AssertRetained();
                    results.Add(new { name, start, maximum, elapsed = watch.Elapsed.TotalSeconds });
                    Console.WriteLine($"PASS native MP4 {name}: start={start:F3}, actual={maximum:F3}, session retained.");
                }
                void SeekPaused(string name, double target, bool locally = false)
                {
                    var retired = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    var generation = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
                    if (locally)
                    {
                        var slider = (Slider)KeyboardField(window, "MediaCastSeekSlider");
                        InteractionAssert(slider.IsEnabled && slider.IsHitTestVisible &&
                            ((Button)KeyboardField(window, "MediaCastSeekBackwardButton")).IsEnabled &&
                            ((Button)KeyboardField(window, "MediaCastSeekForwardButton")).IsEnabled,
                            name + " disabled local seeking for a retained terminal item.");
                        KeyboardCall(window, "SeekMediaCastLocally", target);
                    }
                    else Send(MediaCastCommand.Seek, target);
                    AssertRestarted(retired, generation, name);
                    Until(() => (bool)KeyboardField(window, "_mediaOpened"), name + " never opened.");
                    InteractionAssert(!(bool)KeyboardField(window, "_mediaShouldPlay"), name + " lost pause intent.");
                    var element = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    Until(() => Math.Abs(element.Position.TotalSeconds - target) < .4,
                        name + " did not apply its target on opening.");
                    var before = element.Position.TotalSeconds;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                    InteractionAssert(Math.Abs(element.Position.TotalSeconds - before) < .15,
                        name + " resumed without a play request.");
                    Send(MediaCastCommand.Resume);
                    Complete(name, target);
                }

                KeyboardCall(window, "OnMediaCastCommandReceived", new MediaCastRequest(command++, MediaCastCommand.Play,
                    MediaCastFlags.MuteSpecified | MediaCastFlags.Muted, source.AbsoluteUri, duration, 0, .33));
                if (failureFirst)
                {
                    Until(() => (bool)KeyboardField(window, "_mediaOpened") &&
                        ((MediaElement)KeyboardField(window, "MediaCastMediaElement")).Position.TotalSeconds >= .75,
                        "Native failure fixture did not reach a real frame.");
                    FailCurrent("initial-failure");
                    var failed = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                    var failedGeneration = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
                    Send(MediaCastCommand.Resume);
                    AssertRestarted(failed, failedGeneration, "initial-failure-resume");
                    Complete("initial-failure-resume", .75);
                }
                else
                Complete("initial", 0);
                var retired = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                var generation = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
                Send(MediaCastCommand.Resume);
                AssertRestarted(retired, generation, "completed-resume");
                Complete("completed-resume", 0);
                SeekPaused("completed-local-seek", 1.0, locally: true);

                // A real, opened MP4 retains the normal failed-item state;
                // the next explicit command must reopen its native backend.
                Send(MediaCastCommand.Resume);
                Until(() => (bool)KeyboardField(window, "_mediaOpened") &&
                    ((MediaElement)KeyboardField(window, "MediaCastMediaElement")).Position.TotalSeconds >= .75,
                    "Failed-item fixture did not reach a real native frame.");
                FailCurrent("failed-resume");
                retired = (MediaElement)KeyboardField(window, "MediaCastMediaElement");
                generation = ((MediaCastEventGate)KeyboardField(window, "_mediaCastEvents")).CurrentGeneration;
                Send(MediaCastCommand.Resume);
                AssertRestarted(retired, generation, "failed-resume");
                Complete("failed-resume", .75);
                Send(MediaCastCommand.Resume);
                Until(() => (bool)KeyboardField(window, "_mediaOpened") &&
                    ((MediaElement)KeyboardField(window, "MediaCastMediaElement")).Position.TotalSeconds >= .75,
                    "Second native failure fixture did not reach a real frame.");
                FailCurrent("failed-seek");
                SeekPaused("failed-seek", 1.2);
                Send(MediaCastCommand.Resume);
                Until(() => (bool)KeyboardField(window, "_mediaOpened") &&
                    ((MediaElement)KeyboardField(window, "MediaCastMediaElement")).Position.TotalSeconds >= .75,
                    "Local-seek failure fixture did not reach a real frame.");
                FailCurrent("failed-local-seek");
                SeekPaused("failed-local-seek", 1.0, locally: true);

                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    description = "Actual MainWindow/native HTTP MP4: natural completion replay, completed and failed seek, native failure callback retry, old callback rejection, volume/mute and paused intent. Failure callbacks use ExceptionRoutedEventArgs after actual native playback, not a network failure emulation.",
                    cases = results, stopRequests, server.Mp4Requests,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { stopEvent.GetRemoveMethod(true)!.Invoke(viewModel, [stopObserved]); }
            return 0;
        }
        finally
        {
            if (window is not null)
            {
                KeyboardCall(window, "StopMediaCastPlayback", "direct_replay_test_cleanup");
                CloseWorkspaceTestWindow(window);
            }
            app.Shutdown();
        }
    }
}
