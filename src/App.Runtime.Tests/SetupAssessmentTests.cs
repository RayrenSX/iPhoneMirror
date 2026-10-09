using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static async Task<int> ProbeSetupRuntimeAsync(string driver)
    {
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER", Path.GetFullPath(driver));
        var runtime = new SetupCheckRuntime();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = cancellation.Token;
        var bridge = await runtime.BridgeAsync(token);
        if (bridge.Item1 != SetupState.Completed) throw new InvalidOperationException("Bridge runtime: " + bridge);
        Console.WriteLine("PASS packaged bridge integrity and real --check-runtime.");
        var receiver = await runtime.ReceiverAsync(WirelessReceiverBackend.Original, token);
        Console.WriteLine("Real original receiver probe: " + receiver.Status);
        var bluetooth = await runtime.BluetoothAsync(token);
        Console.WriteLine("Real Bluetooth environment: " + bluetooth.State + ", paired identities=" + bluetooth.PairedIds?.Length);
        var usb = await runtime.DriverAsync(token);
        if (!usb.Success || usb.Environment is null) throw new InvalidOperationException("Structured driver probe failed.");
        foreach (var device in usb.Devices ?? [])
        {
            var setup = await runtime.ControlDeviceAsync(device.Serial, false, token);
            Console.WriteLine("Real device prerequisite check: " + setup.Item1 + ", code=" + (setup.Item3 ?? "none"));
        }
        return 0;
    }
    private static int RunSetupAssessmentTests(string output, bool preview = false)
    {
        Directory.CreateDirectory(output);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var bindingsPath = Path.Combine(Path.GetFullPath(output), "check-bindings.json");
        var bindings = new DeviceBindingManager(bindingsPath);
        foreach (var old in bindings.Profiles) bindings.DeleteProfile(old.Id);
        var state = new FirstRunSetupState { Disposition = SetupDisposition.Completed, Usage = SetupUsage.WirelessOnly,
            Devices = [new() { Id = "airplay-a", Name = "iPhone A" }] };
        foreach (var step in new[] { SetupStep.Preferences, SetupStep.ApplicationMode, SetupStep.Appearance, SetupStep.Usage,
            SetupStep.MirrorConnection, SetupStep.Wireless, SetupStep.Display }) state.GlobalOutcomes[step] = SetupOutcome.Verified;
        bindings.CreateProfileFromIdentity("iPhone A", DeviceIdentityType.AirPlay, "airplay-a", null);
        var receiverHealthy = true; var driversHealthy = true; var deviceOnline = true; var bluetoothPaired = true;
        TaskCompletionSource<WirelessRuntimeProbeResult>? receiverGate = null;
        var settings = new UpdateSettings();
        var driverCalls = 0; var controlCalls = 0;
        var settingsCalls = 0; var bindingCalls = 0; var receiverCalls = 0; var bluetoothCalls = 0; var bridgeCalls = 0; var identityCalls = 0;
        int[] ProbeCounts() => [settingsCalls, bindingCalls, receiverCalls, driverCalls, identityCalls, bluetoothCalls, bridgeCalls, controlCalls];
        var runtime = new SetupCheckRuntime {
            ReadSettingsAsync = _ => { settingsCalls++; return Task.FromResult(new SetupSettingsEvidence(settings)); },
            ReadBindingsAsync = _ => { bindingCalls++; return Task.FromResult(new DeviceBindingManager(bindingsPath, readOnly: true)); },
            ReceiverAsync = (_, _) => { receiverCalls++; return receiverGate?.Task ?? Task.FromResult(new WirelessRuntimeProbeResult(receiverHealthy ? WirelessRuntimeProbeStatus.Ready : WirelessRuntimeProbeStatus.LoadFailed, 0)); },
            DriverAsync = _ => { driverCalls++; return Task.FromResult(new SetupDriverProgress("Result", true, null,
                deviceOnline ? [new("wired-a", "iPhone A")] : [], new(driversHealthy, true, true, deviceOnline, "apple", "usb"))); },
            WiredIdentitiesAsync = _ => { identityCalls++; return Task.FromResult(deviceOnline ? new[] { "wired-a" } : []); },
            BluetoothAsync = _ => { bluetoothCalls++; return Task.FromResult(new SetupBluetoothEvidence(SetupState.Completed, null, bluetoothPaired ? ["bluetooth-a"] : [])); },
            BridgeAsync = _ => { bridgeCalls++; return Task.FromResult<(SetupState, string?, string?)>((SetupState.Completed, null, null)); },
            ControlDeviceAsync = (_, _, _) => { controlCalls++; return Task.FromResult<(SetupState, string?, string?)>((SetupState.Completed, null, null)); }
        };
        IReadOnlyList<SetupCheckResult> Scan() => new SetupAssessment(runtime).CheckAsync(state, null, default).GetAwaiter().GetResult();
        var results = Scan();
        Check(results.All(r => !r.NeedsAttention), "Fully configured wireless setup was not complete.");
        Check(driverCalls == 0 && controlCalls == 0, "Wireless-only setup probed USB or control hardware.");
        Check(results.Where(r => r.Id is "apple" or "usb" or "bridge" or "bluetooth").All(r => r.State == SetupState.NotRequired), "Unused capabilities were not NotRequired.");
        Check(SetupTaskPlanner.Plan(results).Count == 0, "Completed/NotRequired produced work.");
        receiverHealthy = false; results = Scan();
        Check(results.Single(r => r.Id == "receiver").State == SetupState.Invalid && SetupTaskPlanner.Plan(results)[0].Step == SetupStep.WirelessBackend,
            "Removed receiver was not scheduled for repair.");
        receiverHealthy = true;
        bindings.DeleteProfile(bindings.Profiles[0].Id); results = Scan();
        Check(results.Single(r => r.Id.StartsWith("profile:")).State == SetupState.Invalid && SetupTaskPlanner.Plan(results)[0].Step == SetupStep.Wireless,
            "Deleted binding reused historical Verified state.");
        bindings.CreateProfileFromIdentity("iPhone A", DeviceIdentityType.AirPlay, "airplay-a", null);
        var fullWireless = Scan();
        state.Usage = SetupUsage.MirrorAndControl; state.WirelessEnabled = state.BluetoothEnabled = state.UsbControlEnabled = true;
        state.Devices = [new() { Id = "wired-a", Name = "iPhone A", Outcomes = new() {
            [SetupStep.Environment] = SetupOutcome.Verified, [SetupStep.Profile] = SetupOutcome.Verified,
            [SetupStep.Wireless] = SetupOutcome.Verified, [SetupStep.Bluetooth] = SetupOutcome.Verified } }];
        foreach (var old in bindings.Profiles) bindings.DeleteProfile(old.Id);
        var profile = bindings.CreateProfileFromIdentity("iPhone A", DeviceIdentityType.Wired, "wired-a", null).Profile!;
        bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, "airplay-a", "iPhone A", null, true);
        bindings.Bind(profile.Id, DeviceIdentityType.Bluetooth, "bluetooth-a", "iPhone A", null, true);
        results = Scan(); Check(results.All(r => !r.NeedsAttention), "Configured control setup was not complete.");
        var session = new SetupAssessmentSession(runtime);
        session.CheckAsync(state, null, default).GetAwaiter().GetResult();
        var beforeRefresh = ProbeCounts();
        session.RefreshAsync(state, SetupStep.Appearance, null, default).GetAwaiter().GetResult();
        Check(settingsCalls == beforeRefresh[0] + 1 && ProbeCounts().Skip(1).SequenceEqual(beforeRefresh.Skip(1)), "Changing appearance repeated unrelated hardware checks.");
        beforeRefresh = ProbeCounts(); bluetoothPaired = false;
        var refreshed = session.RefreshAsync(state, SetupStep.Bluetooth, "wired-a", default).GetAwaiter().GetResult();
        Check(refreshed.Single(r => r.Id == "btbinding:wired-a").State == SetupState.Invalid, "Targeted Bluetooth check ignored actual pairing removal.");
        Check(bluetoothCalls == beforeRefresh[5] + 1 && bindingCalls == beforeRefresh[1] + 1 &&
            driverCalls == beforeRefresh[3] && receiverCalls == beforeRefresh[2] && bridgeCalls == beforeRefresh[6] && controlCalls == beforeRefresh[7], "Bluetooth repair repeated unrelated probes.");
        bluetoothPaired = true;
        session.RefreshAsync(state, SetupStep.Bluetooth, "wired-a", default).GetAwaiter().GetResult();
        beforeRefresh = ProbeCounts(); receiverHealthy = false;
        refreshed = session.RefreshAsync(state, SetupStep.WirelessBackend, null, default).GetAwaiter().GetResult();
        Check(refreshed.Single(r => r.Id == "receiver").State == SetupState.Invalid && receiverCalls == beforeRefresh[2] + 1 &&
            driverCalls == beforeRefresh[3] && bluetoothCalls == beforeRefresh[5] && controlCalls == beforeRefresh[7], "Receiver repair did not stay scoped to the receiver.");
        receiverHealthy = true;
        deviceOnline = false; results = Scan();
        Check(results.Single(r => r.Id == "device:wired-a").State == SetupState.Unknown &&
            results.Single(r => r.Id == "profile:wired-a").State == SetupState.Completed, "Offline device invalidated its binding.");
        Check(state.Devices.Count == 1 && bindings.Profiles.Count == 1, "Read-only scan removed offline identity.");
        deviceOnline = true; driversHealthy = false; results = Scan();
        Check(results.Single(r => r.Id == "apple").State == SetupState.Invalid, "Removed Apple support was not invalid.");
        Check(SetupTaskPlanner.Plan(results)[0].Step == SetupStep.Environment, "Driver repair did not precede device tasks.");
        driversHealthy = true; bluetoothPaired = false; results = Scan();
        Check(results.Single(r => r.Id == "btbinding:wired-a").State == SetupState.Invalid, "Removed Windows pairing was not invalid.");
        Check(SetupTaskPlanner.Plan(results).Single().Step == SetupStep.Bluetooth, "Only Bluetooth needed repair, but unrelated settings were queued.");
        var deferred = new HashSet<string> { SetupTaskPlanner.Plan(results).Single().Key };
        Check(SetupTaskPlanner.Plan(results, deferred).Count == 0 && SetupTaskPlanner.Plan(Scan()).Count == 1, "Skip either looped or became permanently completed.");
        state.BluetoothEnabled = false; results = Scan(); Check(results.All(r => !r.NeedsAttention), "Explicitly disabled Bluetooth was still required.");
        settings.DeviceVideoPreferences["wired-a"] = new(99999, 720, 999, (IPhoneMirror.App.Models.DecoderPreference)999);
        Check(Scan().Single(r => r.Id == "display").State == SetupState.Invalid, "Malformed display preferences were accepted.");
        settings.DeviceVideoPreferences.Clear();
        state.BluetoothEnabled = true;
        bindings.Unbind(profile.Id, DeviceIdentityType.AirPlay);
        var missingPair = SetupTaskPlanner.Plan(Scan());
        Check(missingPair.Count == 2 && missingPair[0].Step == SetupStep.Wireless && missingPair[1].Step == SetupStep.Bluetooth,
            "Two missing bindings repeated completed driver or profile steps.");
        bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, "airplay-a", "iPhone A", null, true);
        state.BluetoothEnabled = false;
        var savedBindings = File.ReadAllText(bindingsPath);
        File.WriteAllText(bindingsPath, "{broken");
        Check(Scan().Single(r => r.Id == "profile:wired-a").State == SetupState.Invalid, "Corrupt profiles became completed or empty defaults.");
        File.WriteAllText(bindingsPath, savedBindings);
        var disabled = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl, Step = SetupStep.Profile,
            WirelessEnabled = false, BluetoothEnabled = false, Devices = [new() { Id = "test", Outcomes = new() { [SetupStep.Profile] = SetupOutcome.Verified } }] };
        disabled.Next(); Check(disabled.Step == SetupStep.DeviceComplete, "Full setup entered an explicitly disabled feature.");
        state.PreserveKnownDevices = true; state.ReconcileConnectedDevices([]); Check(state.Devices.Count == 1, "Smart discovery discarded configured devices.");
        var first = new FirstRunSetupState();
        var initial = new SetupAssessment(new SetupCheckRuntime { ReadSettingsAsync = _ => Task.FromResult(new SetupSettingsEvidence(null)) }).CheckAsync(first, null, default).GetAwaiter().GetResult();
        Check(SetupTaskPlanner.Plan(initial)[0].Step == SetupStep.Preferences && initial.All(r => r.Id != "apple"), "First use started hardware checks before usage selection.");
        var corrupted = new SetupCheckRuntime { ReadSettingsAsync = _ => Task.FromResult(new SetupSettingsEvidence(null, new System.Text.Json.JsonException("broken"))) };
        Check(new SetupAssessment(corrupted).CheckAsync(first, null, default).GetAwaiter().GetResult()[0].State == SetupState.Invalid, "Corrupt settings silently became valid defaults.");
        Console.WriteLine("PASS first use, complete/partial setup, removed components, removed pairings, offline identities, dependency repair, NotRequired, session skips and corrupt settings.");

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var vm = new MainViewModel();
        var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), "check-window.json"));
        void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
        void Complete(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(20)) { Pump(); Thread.Sleep(2); } task.GetAwaiter().GetResult(); }
        void Settled(FirstRunSetupWindow window) { var timer = Stopwatch.StartNew(); do { Pump(); Thread.Sleep(2); } while (window.IsAssessing && timer.Elapsed < TimeSpan.FromSeconds(20)); Check(!window.IsAssessing, "UI check did not settle."); window.UpdateLayout(); }
        Task Call(FirstRunSetupWindow window, string method, params object[] args) => (Task)typeof(FirstRunSetupWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
        void Close(FirstRunSetupWindow window) { typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true); window.Close(); }
        void Capture(FirstRunSetupWindow window, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        try
        {
            state.Usage = SetupUsage.WirelessOnly; state.Devices = [new() { Id = "airplay-a", Name = "iPhone A" }];
            if (preview)
            {
                receiverHealthy = false; store.Save(state);
                var window = new FirstRunSetupWindow(vm, store) { CheckRuntime = runtime };
                window.Closed += (_, _) => app.Shutdown();
                app.Run(window);
                return 0;
            }
            foreach (var culture in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [culture, false, false]);
                ThemeService.Apply(theme); store.Save(state);
                receiverGate = new();
                var window = new FirstRunSetupWindow(vm, store, rerun: true) { CheckRuntime = runtime };
                window.Show();
                var rows = (System.Collections.IDictionary)typeof(FirstRunSetupWindow).GetField("_checkRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var progressTimer = Stopwatch.StartNew();
                while ((!rows.Contains("receiver") || progressTimer.ElapsedMilliseconds < 350) && progressTimer.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(2); }
                Check(rows.Contains("receiver") && window.IsAssessing, "Pending real check did not appear in the assessment card.");
                window.UpdateLayout();
                var body = (StackPanel)window.FindName("Body");
                var liveCard = body.Children[0]; var liveReceiver = rows["receiver"];
                var cardPosition = ((FrameworkElement)liveCard).TranslatePoint(new Point(), window);
                Capture(window, $"checking-{culture}-{theme}");
                receiverGate.SetResult(new(WirelessRuntimeProbeStatus.Ready, 0)); receiverGate = null;
                Settled(window);
                Check(ReferenceEquals(liveCard, body.Children[0]) && ReferenceEquals(liveReceiver, rows["receiver"]), "Completion rebuilt the checking card or its rows.");
                var finalPosition = ((FrameworkElement)liveCard).TranslatePoint(new Point(), window);
                Check(Math.Abs(cardPosition.Y - finalPosition.Y) < 1, $"Assessment card moved when checking completed: {cardPosition.Y} -> {finalPosition.Y}.");
                Check(((TextBlock)window.FindName("Heading")).Text == LocalizationService.Get("SetupCheckReady"), "Opening completed setup restarted the wizard.");
                Check(((ScrollViewer)window.FindName("Scroller")).ScrollableHeight <= 1, "Default check summary requires scrolling.");
                Capture(window, $"check-{culture}-{theme}");
                receiverHealthy = false;
                Complete(Call(window, "CheckSettingsAsync"));
                Capture(window, $"attention-{culture}-{theme}");
                receiverHealthy = true;
                Complete(Call(window, "ReconfigureAllAsync"));
                Check(window.State.Step == SetupStep.Welcome && bindings.Profiles.Count == 1, "Explicit reconfiguration failed or deleted profiles.");
                Close(window);
            }
            receiverHealthy = false; store.Save(state);
            bindings.Unbind(profile.Id, DeviceIdentityType.AirPlay);
            var partial = new FirstRunSetupWindow(vm, store) { CheckRuntime = runtime };
            partial.Show(); Settled(partial);
            Complete(Call(partial, "ContinueAssessmentAsync"));
            Check(partial.State.Step == SetupStep.WirelessBackend, "Partial setup did not jump directly to missing receiver.");
            bool SummaryVisible(FirstRunSetupWindow window) => (bool)typeof(FirstRunSetupWindow).GetField("_assessmentVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            void Navigate(FirstRunSetupWindow window, bool backwards = false) => typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [backwards]);
            Complete(Call(partial, "AdvancePendingTasksAsync", false));
            Check(!SummaryVisible(partial) && partial.State.Step == SetupStep.WirelessBackend, "Unsuccessful repair restarted the checking page instead of staying on its step.");
            receiverHealthy = true;
            using (var checkpointLock = File.Open(store.PathName, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Complete(Call(partial, "AdvancePendingTasksAsync", false));
                Check(partial.State.Step == SetupStep.WirelessBackend && !SummaryVisible(partial), "A failed task transition lost the visible step.");
            }
            beforeRefresh = ProbeCounts();
            Navigate(partial); Settled(partial);
            Check(!SummaryVisible(partial) && partial.State.Step == SetupStep.Wireless, "Completing a task did not advance directly to the newly unblocked binding task.");
            Check(receiverCalls == beforeRefresh[2] + 1 && driverCalls == beforeRefresh[3] && bluetoothCalls == beforeRefresh[5] && bridgeCalls == beforeRefresh[6], "Next repeated unrelated checks.");
            beforeRefresh = ProbeCounts(); Navigate(partial, backwards: true);
            Check(SummaryVisible(partial) && ProbeCounts().SequenceEqual(beforeRefresh), "Back started another scan.");
            Complete(Call(partial, "ContinueAssessmentAsync"));
            bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, "airplay-a", "iPhone A", null, true);
            beforeRefresh = ProbeCounts(); Navigate(partial); Settled(partial);
            Check(SummaryVisible(partial) && partial.AssessmentResults.All(r => !r.NeedsAttention), "Task completion did not show the updated final summary.");
            Check(bindingCalls == beforeRefresh[1] + 1 && receiverCalls == beforeRefresh[2] && driverCalls == beforeRefresh[3], "Binding completion repeated component checks.");
            Close(partial);
            receiverHealthy = false;
            var reopened = new FirstRunSetupWindow(vm, store) { CheckRuntime = runtime };
            reopened.Show(); Settled(reopened);
            Check(reopened.AssessmentResults.Single(r => r.Id == "receiver").State == SetupState.Invalid, "Reopening skipped real checks.");
            Complete(Call(reopened, "ContinueAssessmentAsync"));
            beforeRefresh = ProbeCounts();
            typeof(FirstRunSetupWindow).GetMethod("SkipCurrentStep", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, null);
            Settled(reopened);
            Check(ProbeCounts().SequenceEqual(beforeRefresh), "Skipping a task started another scan.");
            Check(reopened.AssessmentResults.Any(r => r.NeedsAttention), "Skipping was incorrectly reported as ready.");
            Close(reopened);
            receiverHealthy = true;
            var repaired = new FirstRunSetupWindow(vm, store) { CheckRuntime = runtime };
            repaired.Show(); Settled(repaired);
            Check(repaired.AssessmentResults.All(r => !r.NeedsAttention), "An externally repaired component was not recognized on reentry.");
            Complete(Call(repaired, "ContinueAssessmentAsync"));
            Check(store.Load().Disposition == SetupDisposition.Completed, "Completion did not persist after verified repair.");
            var waiting = new TaskCompletionSource<SetupSettingsEvidence>();
            store.Save(state);
            var cancelled = new FirstRunSetupWindow(vm, store) { CheckRuntime = new() { ReadSettingsAsync = _ => waiting.Task } };
            cancelled.Show(); Pump();
            Check(cancelled.IsAssessing, "Real asynchronous check did not show checking state.");
            Close(cancelled); Check(vm.SetupActive, "Close released ownership while a check was still running.");
            waiting.SetResult(new(settings)); Settled(cancelled);
            Check(!vm.SetupActive, "Cancelled check retained ownership after cleanup.");
            receiverHealthy = false; store.Save(state);
            var localClose = new FirstRunSetupWindow(vm, store) { CheckRuntime = runtime };
            localClose.Show(); Settled(localClose); Complete(Call(localClose, "ContinueAssessmentAsync"));
            receiverGate = new();
            var refreshPending = Call(localClose, "AdvancePendingTasksAsync", false); Pump();
            Check(localClose.IsAssessing && !SummaryVisible(localClose), "Targeted verification returned to the full scan screen.");
            Close(localClose); Check(vm.SetupActive, "Targeted verification released ownership before cleanup.");
            receiverGate.SetResult(new(WirelessRuntimeProbeStatus.Ready, 0)); receiverGate = null;
            Complete(refreshPending); Check(!vm.SetupActive, "Targeted verification retained ownership after close.");
            Console.WriteLine("PASS scoped probe counts, direct task advancement, no scan on skip/back, failed repair stays inline and targeted-check close ownership.");
            Console.WriteLine("PASS check-on-open, direct repair navigation, explicit full configuration, responsive async close, 4 languages and 2 themes.");
        }
        finally { app.Shutdown(); }
        return 0;
    }
}
