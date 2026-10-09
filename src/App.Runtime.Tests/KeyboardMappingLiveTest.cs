using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in hardware probe. Uses production enumeration, saved identity
    // bindings, capture and reverse-control startup. No ready state is faked.
    private static int RunKeyboardMappingLiveProbe(string output, bool exercise = false, bool wireless = false, bool interactive = false, bool captureOnly = false, bool ownership = false, bool wiredRestart = false, bool clipboard = false, bool realWindowExit = false, string? selectedUdid = null, bool skipReverseControl = false)
    {
        if (skipReverseControl && (selectedUdid is null || !realWindowExit || wiredRestart))
            throw new ArgumentException("Capture isolation requires selected identity and a real window exit.", nameof(skipReverseControl));
        if (selectedUdid is not null && (!realWindowExit || captureOnly || wireless || exercise || interactive || ownership || clipboard))
            throw new ArgumentException("Selected identity mode only supports wired window-exit verification.", nameof(selectedUdid));
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = captureOnly ? CreateWorkspaceTestWindow(app, includeNativePreview: false) : new MainWindow();
        app.MainWindow = main;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        DispatcherTimer? selectedPoll = null;
        Task? selectedPollTask = null;
        if (selectedUdid is not null)
        {
            // Keep real capture/control/close, but avoid the global inventory
            // when another phone has been withdrawn from hardware testing.
            SetKeyboardField(main, "_startupServicesStarted", true);
            var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            vm.Devices.Add((DeviceViewModel)constructor.Invoke([selectedUdid, "Selected test iPhone",
                "", "", "USB", "", ConnectionState.Ready]));
            MappingAssert(DeviceViewModel.UdidEquals(vm.ResolveAppleUdid(selectedUdid), selectedUdid),
                "Selected phone has no matching saved binding.");
            selectedPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            selectedPoll.Tick += (_, _) =>
            {
                if (selectedPollTask is { IsCompleted: false }) return;
                selectedPollTask?.GetAwaiter().GetResult();
                selectedPollTask = (Task)KeyboardCall(vm, "RefreshActiveSessionStatusAsync")!;
            };
            selectedPoll.Start();
            Console.WriteLine("Selected USB identity from prior verification; inventory disabled, active-session status only.");
        }
        main.Show();
        string? restartAppleUdid = null;
        bool Wait(Func<bool> completed, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!completed() && clock.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return completed();
        }
        try
        {
            if (!captureOnly)
            {
            if (!Wait(() => vm.Devices.Any(d => d.IsWireless == wireless && !d.IsMediaCast), 30))
            {
                Console.WriteLine($"HARDWARE BLOCKED: production discovery returned no {(wireless ? "wireless" : "USB")} iPhone/iPad.");
                return 2;
            }
            var candidates = vm.Devices.Where(d => d.IsWireless == wireless && !d.IsMediaCast &&
                vm.ResolveAppleUdid(d.Udid) is not null).ToArray();
            if (candidates.Length != 1)
            {
                var names = string.Join(", ", candidates.Select(d => $"{d.DisplayName} [{d.Udid}]"));
                Console.WriteLine($"HARDWARE BLOCKED: {candidates.Length} bound {(wireless ? "wireless" : "USB")} targets; requires one unambiguous existing binding. Candidates: {names}");
                return 2;
            }
            vm.SelectedDevice = candidates[0];
            if (realWindowExit && !skipReverseControl) restartAppleUdid = vm.ResolveAppleUdid(vm.SelectedDevice.Udid);
            Console.WriteLine($"HARDWARE selected: {vm.SelectedDevice.DisplayName}");
            if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
            Wait(() => vm.SourceVideoWidth > 0 && vm.SourceVideoHeight > 0, 35);
            if (skipReverseControl)
            {
                MappingAssert(vm.SourceVideoWidth > 0 && vm.SourceVideoHeight > 0,
                    "Capture isolation did not receive a real video frame.");
                vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, "iphone-before.png")));
                Console.WriteLine($"CAPTURE ONLY READY: {vm.SourceVideoWidth}x{vm.SourceVideoHeight}; reverse control never started.");
                AdvanceDispatcher(TimeSpan.FromSeconds(5));
            }
            else
            {
            // The user explicitly confirmed unlock, trust and Developer Mode
            // before running this opt-in probe. This changes no phone setting.
            SetKeyboardField(vm, "_wiredControlPrerequisiteAcknowledged", true);
            SetKeyboardField(vm, "_wirelessControlPrerequisiteAcknowledged", true);
            var status = vm.GetControlStatus(wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb, vm.SelectedDevice.Udid);
            status.StatusChanged += (_, state) => Console.WriteLine($"HARDWARE control: {state.Stage} — {state.Description}");
            var start = wireless ? vm.StartWirelessControlAsync(vm.SelectedDevice.Udid) : vm.StartUsbControlAsync(vm.SelectedDevice.Udid);
            if (!Wait(() => start.IsCompleted, 180))
            { Console.WriteLine("HARDWARE BLOCKED: control startup timeout."); return 2; }
            start.GetAwaiter().GetResult();
            if (vm.GetMappingTargetStatus() != "MappingReady")
            {
                Console.WriteLine($"HARDWARE BLOCKED: {vm.GetMappingTargetStatus()}; {status.Current?.Description}; {status.Current?.Error}");
                return 2;
            }
            var path = Path.GetFullPath(Path.Combine(output, "iphone-before.png"));
            vm.CaptureScreenshot(path);
            Console.WriteLine($"HARDWARE READY: {vm.SourceVideoWidth}x{vm.SourceVideoHeight}, existing {(wireless ? "Wireless" : "USB")} mapping route ready. Frame: {path}");
            }
            }
            if (wiredRestart)
            {
                var target = vm.SelectedDevice!.Udid;
                restartAppleUdid = vm.ResolveAppleUdid(target);
                foreach (var pause in new[] { 0, 1000, 5000, 0, 1000 })
                {
                    var stopControl = vm.CancelReverseControlAsync(ControlStatusMode.Usb, target);
                    MappingAssert(Wait(() => stopControl.IsCompleted, 15), "Wired stop timed out.");
                    stopControl.GetAwaiter().GetResult();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(pause));
                    var timer = Stopwatch.StartNew();
                    var restart = vm.StartUsbControlAsync(target);
                    MappingAssert(Wait(() => restart.IsCompleted, 45), "Wired restart timed out.");
                    restart.GetAwaiter().GetResult();
                    MappingAssert(vm.GetMappingTargetStatus() == "MappingReady", "Wired restart did not reach Ready.");
                    // No synthetic touch/typing: readiness includes the real
                    // HID handshake. Verify capture still provides a frame.
                    vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"restart-{pause}-{timer.ElapsedMilliseconds}.png")));
                    Console.WriteLine($"RESTART PASS: pause_ms={pause}, ready_ms={timer.ElapsedMilliseconds}, capture={vm.SourceVideoWidth}x{vm.SourceVideoHeight}");
                    AdvanceDispatcher(TimeSpan.FromSeconds(2));
                }
            }
            else if (ownership)
            {
                ExerciseLiveKeyboardOwnership(main, vm, output, clipboard);
            }
            else if (interactive || captureOnly)
            {
                // Observe the real production callback only during this explicit
                // hardware test; never synthesize input or log unrelated typing.
                var hookEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var hookField = typeof(MainWindow).GetField("_keyboardHookProc", KeyboardTestMembers)!;
                var originalHook = (Delegate)hookField.GetValue(main)!;
                Func<int, nint, nint, nint> observedHook = (code, message, data) =>
                {
                    var capture = app.Windows.OfType<Windows.KeyboardMappingEditorWindow>()
                        .SingleOrDefault(e => e.State == MappingEditorState.WaitingForKey);
                    var key = code >= 0 ? System.Runtime.InteropServices.Marshal.ReadInt32(data) : 0;
                    var flags = code >= 0 ? System.Runtime.InteropServices.Marshal.ReadInt32(data, 8) : 0;
                    var result = (nint)originalHook.DynamicInvoke(code, message, data)!;
                    if (capture is not null)
                        hookEvents.Enqueue($"PHYSICAL CAPTURE: vk={key}, flags={flags}, message={message}, active={capture.IsActive}, suppressed={result}");
                    return result;
                };
                hookField.SetValue(main, Delegate.CreateDelegate(hookField.FieldType, observedHook.Target, observedHook.Method));
                main.Title = "iPhoneMirror — keyboard mapping hardware verification";
                vm.ManageKeyboardMappingCommand.Execute(null);
                var manager = app.Windows.OfType<Windows.KeyboardMappingWindow>().Single();
                KeyboardCall(manager, "OnAddClick", manager, new RoutedEventArgs());
                var editor = app.Windows.OfType<Windows.KeyboardMappingEditorWindow>().Single();
                if (!editor.Wizard.Capturing) KeyboardCall(editor, "OnCaptureClick", editor, new RoutedEventArgs());
                Console.WriteLine("INTERACTIVE READY: physical key capture is waiting. Configuration is isolated in UI-preview memory; close the test main window to finish.");
                var last = string.Empty;
                Task frameCapture = Task.CompletedTask;
                var clock = Stopwatch.StartNew();
                while (main.IsVisible && clock.Elapsed < TimeSpan.FromMinutes(20) && !File.Exists(Path.Combine(output, "stop")))
                {
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                    while (hookEvents.TryDequeue(out var hookEvent)) Console.WriteLine(hookEvent);
                    var settings = System.Text.Json.JsonSerializer.Serialize(app.UpdateSettings.KeyboardMapping);
                    var state = settings + "|" + vm.KeyboardMappingStatus + "|" +
                        string.Join(";", app.Windows.OfType<Windows.KeyboardMappingEditorWindow>().Select(e =>
                            $"{e.State}:{((System.Windows.Controls.Button)e.FindName("CaptureButton")).Content}"));
                    if (state == last) continue;
                    last = state;
                    Console.WriteLine($"INTERACTIVE STATE: {state}");
                    File.WriteAllText(Path.Combine(output, "interactive-settings.json"), settings);
                    if (!captureOnly && frameCapture.IsCompleted)
                    {
                        // PNG encoding must not block the hook's message-pump
                        // thread. Match the production screenshot command.
                        frameCapture = Task.Run(() =>
                        {
                            var timer = Stopwatch.StartNew();
                            try { vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, "interactive-latest.png"))); }
                            catch (Exception error) { hookEvents.Enqueue($"FRAME FAILED: {error.Message}"); }
                            hookEvents.Enqueue($"FRAME SAVED: elapsed_ms={timer.ElapsedMilliseconds}");
                        });
                    }
                }
                Wait(() => frameCapture.IsCompleted, 10);
                GC.KeepAlive(observedHook);
            }
            else if (exercise)
            {
                // Coordinates chosen for the observed Safari test page: taps
                // in its blank upper-right area; swipes stay away from edges.
                var entries = new[]
                {
                    MappingEntry() with { X = .94, Y = .20 },
                    MappingEntry(MappedTouchAction.LongPress) with { X = .94, Y = .20, DurationMs = 650 },
                    MappingEntry(MappedTouchAction.DoubleTap) with { X = .94, Y = .20, IntervalMs = 100 },
                    MappingEntry(MappedTouchAction.SwipeUp) with { X = .9, Y = .65, Distance = .25, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeDown) with { X = .9, Y = .4, Distance = .25, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeLeft) with { X = .7, Y = .18, Distance = .2, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeRight) with { X = .5, Y = .18, Distance = .2, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.Swipe) with { X = .9, Y = .65, EndX = .9, EndY = .4, DurationMs = 400 },
                };
                using var executor = new KeyboardMappingExecutor();
                foreach (var entry in entries)
                {
                    var state = new KeyboardMappingKeyState();
                    var matched = state.Process(entry.Key!, true, false, true, false, false, [entry]).Mapping!;
                    var width = vm.SourceVideoWidth; var height = vm.SourceVideoHeight;
                    var route = vm.CaptureMappingRoute(() => width == vm.SourceVideoWidth && height == vm.SourceVideoHeight,
                        (x, y) => BluetoothMouseOrientationMapper.MapNormalized(x, y, width, height, 0,
                            vm.AppliedBluetoothPortraitMouseDirection, vm.AppliedBluetoothLandscapeMouseDirection,
                            vm.AppliedBluetoothMouseReverseHorizontal, vm.AppliedBluetoothMouseReverseVertical));
                    MappingAssert(route is not null, "Live route changed before gesture.");
                    AwaitMapping(executor.ExecuteAsync(matched, route!));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
                    vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"iphone-after-{entry.Action}.png")));
                    Console.WriteLine($"HARDWARE SENT: {entry.Action}, target={route!.Target}, backend={(wireless ? "Wireless" : "USB")}");
                }
            }
            else Console.WriteLine("No touch input sent by this probe.");
            return 0;
        }
        finally
        {
            selectedPoll?.Stop();
            if (selectedPollTask is not null)
            {
                MappingAssert(Wait(() => selectedPollTask.IsCompleted, 10), "Selected session status poll did not finish.");
                selectedPollTask.GetAwaiter().GetResult();
            }
            var shutdownSecondsLimit = (int)Math.Ceiling(vm.NormalShutdownTimeout.TotalSeconds);
            if (realWindowExit)
            {
                var actuallyClosed = false;
                var realExitWatch = Stopwatch.StartNew();
                main.Closed += (_, _) => actuallyClosed = true;
                // Exercise the production close path with capture and control
                // still running. A hidden window is not proof of completed exit.
                main.Close();
                MappingAssert(Wait(() => actuallyClosed, shutdownSecondsLimit + 10),
                    "Production window close did not finish.");
                Console.WriteLine($"WINDOW EXIT: elapsed_seconds={realExitWatch.Elapsed.TotalSeconds:F2}");
            }
            var cleanupWatch = Stopwatch.StartNew();
            var stop = vm.CancelReverseControlAsync(wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb);
            var stopped = Wait(() => stop.IsCompleted, 20);
            var stopSeconds = cleanupWatch.Elapsed.TotalSeconds;
            cleanupWatch.Restart();
            var shutdown = vm.ShutdownAsync();
            var shutDown = Wait(() => shutdown.IsCompleted, shutdownSecondsLimit);
            var shutdownSeconds = cleanupWatch.Elapsed.TotalSeconds;
            if (!realWindowExit) CloseWorkspaceTestWindow(main);
            var closed = Wait(() => !main.IsVisible, 15);
            if (!realWindowExit) app.Shutdown();
            // A successful ready/restart loop must not hide timed-out cleanup.
            MappingAssert(stopped, "Hardware control cleanup timed out.");
            stop.GetAwaiter().GetResult();
            MappingAssert(shutDown, "Hardware capture shutdown timed out.");
            shutdown.GetAwaiter().GetResult();
            MappingAssert(closed, "Hardware test window did not close.");
            Console.WriteLine($"CLEANUP PASS: control_stop_seconds={stopSeconds:F2}, capture_shutdown_seconds={shutdownSeconds:F2}");
            if (skipReverseControl || selectedUdid is not null)
            {
                AssertWiredCaptureRestorationEvidence(Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE"));
            }
            if (restartAppleUdid is not null)
            {
                // Shutdown can finish while native capture reports a USB
                // configuration-restore warning. Prove the released phone can
                // still establish control instead of treating task completion
                // as proof that Apple's management connection recovered.
                var bridgePath = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
                Task.Run(() => RunDeviceControlLive(bridgePath, restartAppleUdid, "usb"))
                    .GetAwaiter().GetResult();
                Console.WriteLine("POST-CAPTURE PASS: real USB control, HID acknowledgements and restart.");
            }
        }
    }

    private static void AssertWiredCaptureRestorationEvidence(string? path)
    {
        MappingAssert(path is not null && File.Exists(path), "Capture isolation native evidence missing.");
        var evidence = File.ReadAllText(path!);
        MappingAssert(evidence.Contains("normal_observed=true", StringComparison.Ordinal) &&
            !evidence.Contains("normal_observed=false", StringComparison.Ordinal) &&
            !evidence.Contains("app_shutdown_stop_warning", StringComparison.Ordinal),
            "Capture ended, but Apple USB restoration was not confirmed; cleanup completion is not a pass.");
    }
}
