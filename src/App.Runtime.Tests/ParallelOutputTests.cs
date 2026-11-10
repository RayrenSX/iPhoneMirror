using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestParallelMediaOutputUi(string? output = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext());
        try { TestParallelMediaOutputUiCore(output); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void TestParallelMediaOutputUiCore(string? output)
    {
        using var fixture = new CaptureReviewFixture();
        var vm = (MainViewModel)fixture.Vm;
        fixture.State("parallel-output-test", 811);
        SetKeyboardField(vm, "_selectedDevice", fixture.Device("parallel-output-test"));
        SetKeyboardField(vm, "_pendingRecordingPath", null);
        var original = (MediaOutputCoordinator)KeyboardField(vm, "_mediaOutput");
        var recording = new ParallelOutputSession();
        var streaming = new ParallelOutputSession();
        var outputs = new MediaOutputCoordinator(recording, streaming);
        SetKeyboardField(vm, "_mediaOutput", outputs);
        SetKeyboardField(vm, "_mediaOutputCapabilitiesLoaded", true);
        SetKeyboardField(vm, "_mediaOutputCapabilities", new MediaOutputCapabilities(
            true, true, true, true, true, true, true, "libx264", "unused", ""));
        outputs.StatusChanged += (status, failed) => KeyboardCall(vm, "OnMediaOutputStatusChanged", status, failed);
        var window = new MediaOutputSettingsWindow(vm) { ShowInTaskbar = false, ShowActivated = false };
        if (output is not null) Directory.CreateDirectory(output);
        // Load the real controls, but don't probe external components or
        // enumerate hardware: this fixture supplies output capabilities.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            window.GetType().GetMethod("OnLoaded", KeyboardTestMembers |
                System.Reflection.BindingFlags.DeclaredOnly)!);
        Task<(bool Success, string Message)> Start(MediaOutputKind kind) =>
            (Task<(bool Success, string Message)>)KeyboardCall(vm, "StartMediaOutputAsync",
                new MediaOutputRequest(kind, "unused", 160, 160, 20, 500))!;
        void Capture(string name)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80)); window.UpdateLayout();
            if (output is not null) SaveWindowRender(window, Path.Combine(output, name + ".png"));
        }
        try
        {
            window.Show();
            window.UpdateLayout();
            var tabs = Visuals(window).OfType<TabControl>().First();
            var recordButton = (Button)window.FindName("StartRecordingButton");
            var streamButton = (Button)window.FindName("StartStreamingButton");
            var cameraButton = (Button)window.FindName("StartVirtualCameraButton");
            foreach (var name in new[] { "RecordWidthBox", "RecordHeightBox", "StreamWidthBox", "StreamHeightBox" })
                ((TextBox)window.FindName(name)).Text = "160";
            foreach (var name in new[] { "RecordFpsBox", "StreamFpsBox" }) ((TextBox)window.FindName(name)).Text = "20";
            foreach (var name in new[] { "RecordBitrateBox", "StreamBitrateBox" }) ((TextBox)window.FindName(name)).Text = "500";
            ((TextBox)window.FindName("StreamDestinationBox")).Text = "rtmp://127.0.0.1/live/test";
            KeyboardCall(window, "UpdateStartButtons");
            Capture("record-idle");
            var start = Start(MediaOutputKind.Rtmp);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success && vm.CanStartRecording && !vm.CanStartStreaming,
                "A live stream must leave the recording slot available.");
            KeyboardCall(window, "UpdateStartButtons");
            InteractionAssert(((Button)window.FindName("StartRecordingButton")).IsEnabled,
                "The recording button remained disabled while streaming.");
            InteractionAssert(streamButton.IsEnabled && Equals(streamButton.Content, window.FindResource("StopStreamingOnly")),
                "The live stream action must change to its enabled stop action.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            window.UpdateLayout();
            var recordBounds = recordButton.TransformToAncestor(window).TransformBounds(new Rect(recordButton.RenderSize));
            start = Start(MediaOutputKind.Recording);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success && vm.IsRecordingMediaOutput &&
                vm.CanStopRecording && vm.CanStopStreaming && !vm.CanStartRecording && !vm.CanStartStreaming,
                "The VM did not retain independent live/recording state.");
            var readOnlyWindow = new MediaOutputSettingsWindow(vm, previewOnly: true)
                { ShowInTaskbar = false, ShowActivated = false };
            try
            {
                readOnlyWindow.Show();
                readOnlyWindow.UpdateLayout();
                InteractionAssert(!((Button)readOnlyWindow.FindName("StopAllOutputsButton")).IsEnabled,
                    "Read-only preview exposed a live stop-all command.");
                KeyboardCall(readOnlyWindow, "OnStopAllClick", readOnlyWindow, new RoutedEventArgs());
                InteractionAssert(vm.IsRecordingMediaOutput && vm.IsStreamingMediaOutput,
                    "Read-only preview stopped active outputs.");
            }
            finally { readOnlyWindow.Close(); }
            KeyboardCall(vm, "OnMediaOutputStatusChanged", "HTTP 401 fixture", true);
            InteractionAssert(((TextBlock)window.FindName("FeedbackText")).Text == vm.MediaOutputStatus,
                "Failure feedback retained a stale success message.");
            KeyboardCall(vm, "OnMediaOutputStatusChanged", "Stopped", false);
            InteractionAssert(vm.MediaOutputStatus == Localization.LocalizationService.Get("MediaOutputRecordingAndStreaming"),
                "A stale stopped event hid the active parallel outputs.");
            InteractionAssert(!vm.CanUseVirtualCamera, "Virtual camera must remain exclusive.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            window.UpdateLayout();
            Console.WriteLine($"Recording toggle: enabled={recordButton.IsEnabled}; content={recordButton.Content}; before={recordBounds}; after={recordButton.TransformToAncestor(window).TransformBounds(new Rect(recordButton.RenderSize))}");
            InteractionAssert(recordButton.IsEnabled && Equals(recordButton.Content, window.FindResource("StopRecordingOnly")) &&
                recordBounds == recordButton.TransformToAncestor(window).TransformBounds(new Rect(recordButton.RenderSize)),
                "Recording must switch to stop on the same button without moving it.");
            Capture("record-running");
            // A stop action must bypass any invalid edits to future start settings.
            ((TextBox)window.FindName("RecordWidthBox")).Text = "invalid";
            recordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(vm.IsMediaOutputRunning && !vm.IsRecordingMediaOutput &&
                vm.CanStopStreaming && !vm.CanStopRecording && vm.CanStartRecording,
                "Stopping recording stopped or hid the remaining stream.");
            InteractionAssert(Equals(recordButton.Content, window.FindResource("StartRecording")), "Recording button did not reset after stop.");
            ((TextBox)window.FindName("RecordWidthBox")).Text = "160";
            recordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(vm.IsRecordingMediaOutput, "The same recording action could not start a new run.");
            tabs.SelectedIndex = 1; window.UpdateLayout();
            var streamBounds = streamButton.TransformToAncestor(window).TransformBounds(new Rect(streamButton.RenderSize));
            Capture("stream-running");
            ((TextBox)window.FindName("StreamWidthBox")).Text = "invalid";
            streamButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(!vm.IsStreamingMediaOutput && vm.IsRecordingMediaOutput &&
                Equals(streamButton.Content, window.FindResource("StartStreaming")), "Stopping the stream through its own button affected recording.");
            ((TextBox)window.FindName("StreamWidthBox")).Text = "160";
            streamButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            window.UpdateLayout();
            InteractionAssert(vm.IsStreamingMediaOutput && Equals(streamButton.Content, window.FindResource("StopStreamingOnly")) &&
                streamBounds == streamButton.TransformToAncestor(window).TransformBounds(new Rect(streamButton.RenderSize)),
                "Streaming did not restart with the same in-place action.");
            WaitReviewTask(vm.StopRecordingAsync());
            start = Start(MediaOutputKind.Recording);
            WaitReviewTask(start);
            var stopAll = (Button)window.FindName("StopAllOutputsButton");
            window.UpdateLayout();
            InteractionAssert(stopAll.IsEnabled && vm.IsRecordingMediaOutput && vm.IsStreamingMediaOutput,
                "The stop-all action must be available with both outputs running.");
            stopAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(!vm.IsRecordingMediaOutput && !vm.IsStreamingMediaOutput,
                "The visible stop-all action did not stop both outputs.");
            start = Start(MediaOutputKind.Rtmp);
            WaitReviewTask(start);
            // A canceled auto-save must stay dismissed on repeated status
            // notifications. Explicit Save remains a separate force request.
            var dismissed = Path.Combine(Path.GetTempPath(), "dismissed-recording-fixture.mp4");
            SetKeyboardField(vm, "_pendingRecordingPath", dismissed);
            SetKeyboardField(window, "_lastPromptedRecordingPath", dismissed);
            WaitReviewTask((Task)KeyboardCall(window, "PromptToSaveRecordingAsync", false)!);
            InteractionAssert(!(bool)KeyboardField(window, "_savePromptOpen"),
                "A previously dismissed recording opened another auto-save dialog.");
            SetKeyboardField(vm, "_pendingRecordingPath", null);
            // Remaining checks use the VM. Unsubscribe window save prompts
            // before FindLatest can observe unrelated user pending files.
            window.Close();
            var pending = Path.Combine(Path.GetTempPath(), "parallel-output-pending-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                File.WriteAllText(pending, "completed recording fixture");
                SetKeyboardField(vm, "_pendingRecordingPath", pending);
                InteractionAssert(vm.DiscardPendingRecording() && !File.Exists(pending) && streaming.IsRunning,
                    "A completed recording could not be discarded while streaming continued.");
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            SetKeyboardField(vm, "_pendingRecordingPath", null);
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("different-source"));
            fixture.State("different-source", 812);
            InteractionAssert(!vm.CanStartRecording, "A different selected device must not join an existing stream.");
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("parallel-output-test"));
            start = Start(MediaOutputKind.Recording);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success, "The recording slot did not restart.");
            WaitReviewTask(vm.StopStreamingAsync());
            InteractionAssert(vm.IsRecordingMediaOutput && !vm.CanStopStreaming,
                "Stopping streaming stopped recording.");
            WaitReviewTask(vm.StopMediaOutputAsync());
            InteractionAssert(!vm.IsMediaOutputRunning, "Stop-all left an output running.");
            // Exercise the actual camera pump with inert native callbacks. No
            // registration, camera hardware or real frame source is required.
            var originalCamera = (VirtualCameraService)KeyboardField(vm, "_virtualCamera");
            var camera = new VirtualCameraService((_, _, _) => null, (_, _, _) => 0, _ => 0, () => 0);
            try
            {
                SetKeyboardField(vm, "_virtualCamera", camera);
                SetKeyboardField(vm, "_virtualCameraCapabilities", new VirtualCameraCapabilities(true, true, true, false, false, ""));
                var cameraWindow = new MediaOutputSettingsWindow(vm) { ShowInTaskbar = false, ShowActivated = false };
                cameraWindow.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), cameraWindow,
                    cameraWindow.GetType().GetMethod("OnLoaded", KeyboardTestMembers | System.Reflection.BindingFlags.DeclaredOnly)!);
                try
                {
                    cameraWindow.Show();
                    Visuals(cameraWindow).OfType<TabControl>().First().SelectedIndex = 2;
                    WaitReviewTask(camera.StartAsync(811, 160, 160, 20, CancellationToken.None));
                    KeyboardCall(vm, "NotifyMediaOutputStateChanged");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(80)); cameraWindow.UpdateLayout();
                    var action = (Button)cameraWindow.FindName("StartVirtualCameraButton");
                    InteractionAssert(action.IsVisible && action.IsEnabled && Equals(action.Content, cameraWindow.FindResource("StopVirtualCamera")),
                        "A running virtual camera did not expose its stop action in the camera tab.");
                    if (output is not null) SaveWindowRender(cameraWindow, Path.Combine(output, "camera-running.png"));
                    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                    InteractionAssert(!camera.IsRunning && Equals(action.Content, cameraWindow.FindResource("StartVirtualCamera")),
                        "The virtual camera did not stop from its own action.");
                }
                finally { cameraWindow.Close(); }
            }
            finally
            {
                WaitReviewTask(camera.DisposeAsync().AsTask());
                SetKeyboardField(vm, "_virtualCamera", originalCamera);
            }
            Console.WriteLine("Parallel output VM/UI passed: buttons, independent stops, source identity, exclusive virtual camera and source isolation.");
        }
        finally
        {
            if (window.IsLoaded) window.Close();
            outputs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SetKeyboardField(vm, "_mediaOutput", original);
            SetKeyboardField(vm, "_mediaOutputUdid", null);
        }
    }

    private sealed class ParallelOutputSession : IMediaOutputSession
    {
        public bool IsRunning { get; private set; }
        public ulong SessionHandle { get; private set; }
        public event Action<string, bool>? StatusChanged;
        public Task StartAsync(ulong handle, MediaOutputRequest request, MediaOutputCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            SessionHandle = handle;
            StatusChanged?.Invoke("started", false);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            IsRunning = false;
            SessionHandle = 0;
            StatusChanged?.Invoke("Stopped", false);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
