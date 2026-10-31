using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunAudioOnlyOutputGuardTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            using var fixture = new CaptureReviewFixture();
            var vm = (MainViewModel)fixture.Vm;
            fixture.State("audio-output-offline", 891);
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("audio-output-offline"));
            SetKeyboardField(vm, "_pendingRecordingPath", null);
            SetKeyboardField(vm, "_mediaOutputCapabilitiesLoaded", true);
            SetKeyboardField(vm, "_mediaOutputCapabilities", new MediaOutputCapabilities(
                true, true, true, true, true, true, true, "libx264", "unused", ""));
            SetKeyboardField(vm, "_virtualCameraCapabilities",
                new VirtualCameraCapabilities(true, true, true, false, false, ""));
            var originalOutput = (MediaOutputCoordinator)KeyboardField(vm, "_mediaOutput");
            var originalCamera = (VirtualCameraService)KeyboardField(vm, "_virtualCamera");
            var record = new AudioGuardOutputSession();
            var stream = new AudioGuardOutputSession();
            var output = new MediaOutputCoordinator(record, stream);
            var cameraStarts = 0;
            var camera = new VirtualCameraService((_, _, _) => null,
                (_, _, _) => { cameraStarts++; return 0; }, _ => 0, () => 0);
            SetKeyboardField(vm, "_mediaOutput", output);
            SetKeyboardField(vm, "_virtualCamera", camera);
            var changed = new HashSet<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName is { } name) changed.Add(name); };
            Task<(bool Success, string Message)> Start(MediaOutputKind kind) =>
                (Task<(bool Success, string Message)>)KeyboardCall(vm, "StartMediaOutputAsync",
                    new MediaOutputRequest(kind, "unused", 160, 160, 30, 500))!;
            void CheckNotifications()
            {
                foreach (var name in new[] { nameof(vm.CanStartMediaOutput), nameof(vm.CanStartRecording),
                    nameof(vm.CanStartStreaming), nameof(vm.CanUseVirtualCamera) })
                    InteractionAssert(changed.Contains(name), "Missing source transition notification: " + name);
            }
            try
            {
                InteractionAssert(vm.CanStartRecording && vm.CanStartStreaming && vm.CanUseVirtualCamera,
                    "A video session must retain all output start actions.");
                KeyboardCall(vm, "SetAudioOnlyAirPlay", true);
                CheckNotifications();
                InteractionAssert(!vm.CanStartMediaOutput && !vm.CanStartRecording &&
                    !vm.CanStartStreaming && !vm.CanUseVirtualCamera,
                    "Audio-only AirPlay exposed a video output start action.");
                SetKeyboardField(vm, "_mediaOutputCapabilitiesLoaded", false);
                foreach (var kind in new[] { MediaOutputKind.Recording, MediaOutputKind.Rtmp,
                    MediaOutputKind.Srt, MediaOutputKind.Whip })
                {
                    var task = Start(kind); WaitReviewTask(task);
                    InteractionAssert(!task.Result.Success &&
                        task.Result.Message == LocalizationService.Get("MediaOutputRequiresVideo"),
                        "A direct output start bypassed the audio-only guard: " + kind);
                }
                var cameraTask = vm.StartVirtualCameraAsync(160, 160, 30);
                WaitReviewTask(cameraTask);
                InteractionAssert(!cameraTask.Result.Success && cameraStarts == 0 &&
                    record.Starts == 0 && stream.Starts == 0 && !vm.IsMediaOutputRunning,
                    "A rejected audio-only start reached a video backend.");
                InteractionAssert(!(bool)KeyboardField(vm, "_mediaOutputCapabilitiesLoaded"),
                    "Audio-only rejection must precede external encoder capability probes.");
                SetKeyboardField(vm, "_mediaOutputCapabilitiesLoaded", true);
                // Existing output stop controls must remain available on a source transition.
                record.StartAsync(891, new(MediaOutputKind.Recording, "unused", 160, 160, 30, 500),
                    new(true, true, true, true, true, true, true, "libx264", "unused", "")).GetAwaiter().GetResult();
                InteractionAssert(vm.CanStopRecording && vm.CanStopMediaOutput,
                    "Audio-only state hid the stop actions for an existing output.");
                WaitReviewTask(output.StopAsync());
                changed.Clear();
                KeyboardCall(vm, "SetAudioOnlyAirPlay", false);
                CheckNotifications();
                InteractionAssert(vm.CanStartRecording && vm.CanStartStreaming && vm.CanUseVirtualCamera,
                    "Returning to video did not restore output actions.");
                foreach (var kind in new[] { MediaOutputKind.Recording, MediaOutputKind.Rtmp })
                {
                    var task = Start(kind); WaitReviewTask(task);
                    InteractionAssert(task.Result.Success, "Video output no longer starts: " + kind);
                    WaitReviewTask(output.StopAsync());
                }
                var videoCamera = vm.StartVirtualCameraAsync(160, 160, 30);
                WaitReviewTask(videoCamera);
                InteractionAssert(videoCamera.Result.Success && cameraStarts == 1,
                    "Video camera start was blocked after returning from audio-only mode.");
                Console.WriteLine("PASS audio-only output guards: all four media kinds and camera reject before backend start; source notifications restore starts; existing stops and video output remain available.");
            }
            finally
            {
                WaitReviewTask(output.DisposeAsync().AsTask());
                WaitReviewTask(camera.DisposeAsync().AsTask());
                SetKeyboardField(vm, "_mediaOutput", originalOutput);
                SetKeyboardField(vm, "_virtualCamera", originalCamera);
                SetKeyboardField(vm, "_mediaOutputUdid", null);
            }
            return 0;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); app.Shutdown(); }
    }

    private sealed class AudioGuardOutputSession : IMediaOutputSession
    {
        public int Starts;
        public bool IsRunning { get; private set; }
        public ulong SessionHandle { get; private set; }
        public event Action<string, bool>? StatusChanged;
        public Task StartAsync(ulong handle, MediaOutputRequest request, MediaOutputCapabilities capabilities,
            CancellationToken cancellationToken = default)
        { Starts++; SessionHandle = handle; IsRunning = true; StatusChanged?.Invoke("started", false); return Task.CompletedTask; }
        public Task StopAsync()
        { SessionHandle = 0; IsRunning = false; StatusChanged?.Invoke("Stopped", false); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
