using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static async Task<int> TestFirstRunDriverCancellationAsync(string executable)
    {
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER", Path.GetFullPath(executable));
        var diagnosis = FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), CancellationToken.None, diagnose: true).GetAwaiter().GetResult();
        if (!diagnosis.Success || diagnosis.Environment is null || diagnosis.Devices is null)
            throw new InvalidOperationException("Structured setup diagnosis failed.");
        Console.WriteLine($"PASS real structured diagnosis: Apple={diagnosis.Environment.AppleReady}, USB files={diagnosis.Environment.UsbFilesMatch}, devices={diagnosis.Devices.Length}.");
        var gate = (SemaphoreSlim)typeof(FirstRunDriverClient).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        await gate.WaitAsync();
        try
        {
            using var waiting = new CancellationTokenSource();
            var queued = FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), waiting.Token, enumerate: true);
            await Task.Delay(100);
            if (queued.IsCompleted) throw new InvalidOperationException("Busy helper rejected retry instead of waiting for cleanup.");
            waiting.Cancel();
            try { await queued; throw new InvalidOperationException("Queued scan ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { gate.Release(); }
        int[] HelperProcesses() => System.Diagnostics.Process.GetProcessesByName("iPhoneMirror.Driver").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
        }).Select(p => { using (p) return p.Id; }).ToArray();
        var knownProcesses = HelperProcesses().ToHashSet();
        foreach (var delay in new[] { 0, 50, 300, 900, 1400 })
        {
            using var cancellation = new CancellationTokenSource();
            var scan = FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), cancellation.Token, enumerate: true);
            await Task.Delay(delay);
            var timer = System.Diagnostics.Stopwatch.StartNew(); cancellation.Cancel();
            // Retry immediately, before cancelled scan cleanup has finished.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var retry = FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), deadline.Token, enumerate: true);
            try { await scan; } catch (OperationCanceledException) { }
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Read-only cancellation did not finish promptly.");
            var result = await retry;
            if (!result.Success || result.Devices is null) throw new InvalidOperationException("Retry failed after cancellation: " + result.Stage + ": " + result.Detail);
            Console.WriteLine($"PASS real helper cancel at {delay} ms and immediate rescan, devices={result.Devices.Length}");
        }
        var leaked = HelperProcesses().Where(id => !knownProcesses.Contains(id)).ToArray();
        if (leaked.Length != 0) throw new InvalidOperationException("Cancelled read-only helper leaked: " + string.Join(",", leaked));
        Console.WriteLine("PASS cancellable gate wait and no remaining scan helper processes; no driver installation performed.");
        return 0;
    }
    private static int ProbeFirstRunDriver(string executable)
    {
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER", Path.GetFullPath(executable));
        var scan = FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), CancellationToken.None, enumerate: true).GetAwaiter().GetResult();
        if (!scan.Success || scan.Devices is null) throw new InvalidOperationException("Headless PnP scan failed.");
        Console.WriteLine($"PASS real headless PnP scan: {scan.Devices.Length} connected Apple USB devices.");
        foreach (var device in scan.Devices)
        {
            var status = FirstRunDriverClient.RunAsync(device.Serial, false, new Progress<SetupDriverProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            if (status.Stage != "Result") throw new InvalidOperationException("Unexpected inspection result: " + status.Stage);
            Console.WriteLine("PASS real read-only environment inspection, ready=" + status.Success);
        }
        return 0;
    }
    private static int RunFirstRunSetupTests(string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        foreach (var usage in Enum.GetValues<SetupUsage>())
        {
            var state = new FirstRunSetupState { Usage = usage, Devices = [new() { Id = "a" }, new() { Id = "b" }] };
            var visited = new List<(SetupStep Step, int Device)>();
            for (var count = 0; state.Step != SetupStep.Completed && count < 60; count++)
            {
                visited.Add((state.Step, state.DeviceIndex));
                state.Record(state.Optional ? SetupOutcome.Skipped : SetupOutcome.Verified);
                state.Next();
            }
            Check(state.Step == SetupStep.Completed, "Flow did not terminate: " + usage);
            Check(visited.Any(v => v.Step == SetupStep.MirrorConnection) == (usage != SetupUsage.MirrorAndControl), "Mirror-only transport question was not isolated.");
            Check(!visited.Any(v => v.Step is SetupStep.WiredControl or SetupStep.WirelessControl or SetupStep.WirelessIntroduction or SetupStep.BluetoothIntroduction), "Setup still runs functional control tests or split introduction pages.");
            if (usage == SetupUsage.WirelessOnly)
            {
                Check(!visited.Any(v => v.Step is SetupStep.Environment or SetupStep.ReDetection or SetupStep.Profile or SetupStep.Bluetooth), "Wireless-only touched USB or Bluetooth.");
                Check(state.GlobalOutcomes[SetupStep.Wireless] == SetupOutcome.Verified, "Wireless outcome was attached to an unrelated device.");
            }
            else
            {
                Check(visited.Count(v => v.Step == SetupStep.Environment) == 2, "Not all devices configured.");
                Check(visited.FindIndex(v => v.Step == SetupStep.DeviceComplete && v.Device == 0) <
                    visited.FindIndex(v => v.Step == SetupStep.Environment && v.Device == 1), "Device setup interleaved.");
                Check(state.Devices.All(state.DeviceComplete), "Complete device rejected.");
                state.Devices[0].Outcomes.Remove(SetupStep.ReDetection);
                Check(!state.DeviceComplete(state.Devices[0]), "Missing post-install validation accepted.");
                if (usage == SetupUsage.WiredOnly)
                    Check(!visited.Any(v => v.Step is SetupStep.Wireless or SetupStep.Bluetooth or SetupStep.WiredControl), "Wired-only entered optional control flow.");
                else
                    Check(visited.Count(v => v.Step == SetupStep.Bluetooth) == 2 && visited.Any(v => v.Step == SetupStep.ControlIntroduction), "Control introduction or per-device Bluetooth missing.");
                state.Previous(); state.Previous(); state.Previous();
                Check(state.Step == SetupStep.DeviceComplete && state.DeviceIndex == 1, "Back did not return to last device.");
                state.SelectUsage(SetupUsage.WirelessOnly);
                Check(state.Devices.Count == 0 && state.GlobalOutcomes.Keys.All(s => s is SetupStep.Welcome or SetupStep.Preferences or SetupStep.ApplicationMode or SetupStep.Appearance), "Changing branch reused incompatible checkpoints.");
            }
        }
        foreach (var usage in Enum.GetValues<SetupUsage>())
        {
            var state = new FirstRunSetupState { Usage = usage, Step = SetupStep.Preferences };
            for (var count = 0; state.Step != SetupStep.Completed && count < 50; count++) state.Skip();
            Check(state.Step == SetupStep.Completed && !state.HasSavedDevices && state.HasSkippedSteps,
                "Skipping all steps did not finish without hardware: " + usage);
            foreach (var step in new[] { SetupStep.Environment, SetupStep.ReDetection, SetupStep.Profile, SetupStep.WirelessBackend, SetupStep.Wireless, SetupStep.Bluetooth })
            {
                if (usage == SetupUsage.WirelessOnly && step is not (SetupStep.WirelessBackend or SetupStep.Wireless)) continue;
                if (usage == SetupUsage.WiredOnly && step is SetupStep.WirelessBackend or SetupStep.Wireless or SetupStep.Bluetooth) continue;
                var skippable = new FirstRunSetupState { Usage = usage, Step = step,
                    Devices = [new() { Id = "a", Outcomes = new() { [SetupStep.Environment] = SetupOutcome.Verified } }] };
                skippable.Skip();
                Check(skippable.Step != step, "Hardware step could not be skipped: " + step);
                if (step is SetupStep.ReDetection or SetupStep.Profile)
                    Check(skippable.Step == SetupStep.DeviceComplete && !skippable.HasSavedDevices,
                        "Skipped identity still entered a dependent binding page.");
            }
        }
        foreach (var usage in Enum.GetValues<SetupUsage>())
        {
            var display = new FirstRunSetupState { Usage = usage, CustomizeDisplay = true, Step = SetupStep.Display,
                GlobalOutcomes = new() { [SetupStep.Wireless] = SetupOutcome.Verified },
                Devices = [new() { Id = "a", Outcomes = new() { [SetupStep.Profile] = SetupOutcome.Verified, [SetupStep.Wireless] = SetupOutcome.Verified } },
                    new() { Id = "b", Outcomes = new() { [SetupStep.Profile] = SetupOutcome.Verified } }] };
            var route = new List<(SetupStep Step, int Device)>();
            while (display.Step != SetupStep.Validation && route.Count < 20)
            {
                route.Add((display.Step, display.DisplayDeviceIndex)); display.Next();
            }
            Check(display.Step == SetupStep.Validation, "Custom display flow did not terminate.");
            Check(route.Any(x => x.Step == SetupStep.WirelessDisplay) == (usage != SetupUsage.WiredOnly), "Display branch mixed wired and wireless options.");
            Check(route.Count(x => x.Step == SetupStep.WiredDecoder) == (usage == SetupUsage.WirelessOnly ? 0 : 2), "Custom settings did not visit each wired device.");
            foreach (var expected in route.AsEnumerable().Reverse())
            {
                display.Previous();
                Check(display.Step == expected.Step && (display.Step == SetupStep.Display || display.DisplayDeviceIndex == expected.Device), "Custom display backtracking lost a step or device.");
            }
        }
        var store = new FirstRunSetupStore(Path.Combine(output, "checkpoint-test.json"));
        foreach (var skippedIdentity in new[] { SetupStep.ReDetection, SetupStep.Profile })
        {
            var state = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl, Step = skippedIdentity,
                Devices = [new() { Id = "a" }] };
            state.Skip(); state.Previous();
            Check(state.Step == skippedIdentity, "Back after skipping identity entered a dependent binding page.");
        }
        var noWireless = new FirstRunSetupState { Step = SetupStep.WirelessBackend, CustomizeDisplay = true };
        noWireless.Skip(); noWireless.Next();
        Check(noWireless.Step == SetupStep.Validation && !noWireless.UsesWirelessMirroring,
            "Skipped wireless setup still customized or applied receiver settings.");
        noWireless.Previous(); noWireless.Previous();
        Check(noWireless.Step == SetupStep.WirelessBackend, "Back after skipping wireless backend entered AirPlay binding.");
        var changingUsage = new FirstRunSetupState { GlobalOutcomes = new() { [SetupStep.Preferences] = SetupOutcome.Skipped, [SetupStep.Wireless] = SetupOutcome.Verified } };
        changingUsage.SelectUsage(SetupUsage.WiredOnly);
        Check(changingUsage.GlobalOutcomes.GetValueOrDefault(SetupStep.Preferences) == SetupOutcome.Skipped && !changingUsage.GlobalOutcomes.ContainsKey(SetupStep.Wireless),
            "Changing usage erased unrelated preferences or retained old device outcomes.");
        var missingIdentity = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl, Step = SetupStep.Bluetooth, Devices = [new() { Id = "a" }] };
        store.Save(missingIdentity);
        Check(store.Load().Step == SetupStep.ReDetection, "Incomplete checkpoint entered Bluetooth without a wired identity.");
        for (var mask = 0; mask < 256; mask++)
        {
            var mixed = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl, Step = SetupStep.Environment,
                CustomizeDisplay = true, Devices = [new() { Id = "a" }, new() { Id = "b" }, new() { Id = "c" }] };
            for (var count = 0; mixed.Step != SetupStep.Completed && count < 60; count++)
            {
                if (mixed.Step is SetupStep.WirelessBackend or SetupStep.Wireless or SetupStep.Bluetooth)
                    Check(mixed.CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.Profile) == SetupOutcome.Verified,
                        "Mixed skip path entered binding without a profile.");
                if ((mask & (1 << (count % 8))) != 0) mixed.Skip();
                else { mixed.Record(SetupOutcome.Verified); mixed.Next(); }
            }
            Check(mixed.Step == SetupStep.Completed, "Mixed multi-device skipping failed to terminate.");
        }
        var inventory = new FirstRunSetupState();
        inventory.ReconcileConnectedDevices([new("a", "iPhone A")]);
        inventory.ReconcileConnectedDevices([new("c", "iPhone C"), new("a", "iPhone A"), new("b", "iPhone B")]);
        Check(string.Join(",", inventory.Devices.Select(d => d.Id)) == "a,c,b", "New devices did not preserve first-observed order.");
        inventory.ReconcileConnectedDevices([new("b", "iPhone B"), new("c", "iPhone C")]);
        Check(string.Join(",", inventory.Devices.Select(d => d.Id)) == "c,b", "Unplugged device stayed in discovery queue.");
        inventory.ReconcileConnectedDevices([]);
        Check(inventory.Devices.Count == 0, "Empty scan retained disconnected devices.");
        var optionalWireless = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl,
            Devices = [new() { Id = "wired", Outcomes = new() { [SetupStep.Wireless] = SetupOutcome.Skipped } }] };
        Check(!optionalWireless.UsesWirelessMirroring, "Skipping wireless still requires a receiver on the display page.");
        optionalWireless.Devices.Add(new() { Id = "wireless", Outcomes = new() { [SetupStep.Wireless] = SetupOutcome.Verified } });
        Check(optionalWireless.UsesWirelessMirroring, "A configured wireless device lost its receiver settings.");
        optionalWireless.Usage = SetupUsage.WiredOnly;
        Check(!optionalWireless.UsesWirelessMirroring, "Wired-only setup started a wireless receiver.");
        optionalWireless.Usage = SetupUsage.WirelessOnly;
        Check(!optionalWireless.UsesWirelessMirroring, "Unconfigured wireless-only setup still applies receiver settings.");
        optionalWireless.GlobalOutcomes[SetupStep.Wireless] = SetupOutcome.Verified;
        Check(optionalWireless.UsesWirelessMirroring, "Wireless-only setup did not require its receiver.");
        var draft = new FirstRunSetupState { Disposition = SetupDisposition.InProgress, Usage = SetupUsage.MirrorAndControl,
            Step = SetupStep.Bluetooth, DeviceIndex = 1, PreferencesApplied = true,
            Devices = [new() { Id = "a" }, new() { Id = "b", Outcomes = new() { [SetupStep.Profile] = SetupOutcome.Verified, [SetupStep.Wireless] = SetupOutcome.Skipped } }] };
        store.Save(draft); var restored = store.Load();
        Check(!restored.ShouldOpen && restored.DeviceIndex == 1 && restored.Step == SetupStep.Bluetooth && restored.CurrentDevice!.Outcomes[SetupStep.Wireless] == SetupOutcome.Skipped,
            "Resume lost device, step or skipped outcome.");
        foreach (var disposition in new[] { SetupDisposition.Deferred, SetupDisposition.Completed })
        { draft.Disposition = disposition; store.Save(draft); Check(!store.Load().ShouldOpen, "Deferred/completed setup reopened automatically."); }
        draft.Disposition = SetupDisposition.InProgress; draft.Step = SetupStep.Completed; store.Save(draft);
        Check(store.Load().Step == SetupStep.Validation, "Resumed success bypassed verification.");
        draft.GlobalOutcomes[SetupStep.Validation] = SetupOutcome.Skipped; store.Save(draft);
        Check(store.Load().Step == SetupStep.Completed && store.Load().GlobalOutcomes[SetupStep.Validation] == SetupOutcome.Skipped,
            "Resuming partial completion forced an explicitly skipped final check.");
        File.WriteAllText(store.PathName, "{broken"); Check(store.Load().Disposition == SetupDisposition.New, "Corrupt checkpoint crashed startup.");
        File.WriteAllText(store.PathName, "{\"Version\":99}");
        Check(store.Load().Disposition == SetupDisposition.New, "Unsupported checkpoint version crashed startup.");
        File.WriteAllText(store.PathName, "{\"Step\":999}");
        Check(store.Load().Step == SetupStep.Welcome, "Invalid checkpoint step crashed startup.");
        var capture = new DeviceCaptureState { Udid = "test" };
        var preferences = new DeviceVideoPreferences(1280, 720, 30, DecoderPreference.SoftwareCompatible);
        preferences.Apply(capture);
        Check(capture.RenderWidth == 1280 && capture.FrameRate == 30 && capture.DecoderPreference == DecoderPreference.SoftwareCompatible,
            "Persisted video preferences were not restored.");
        new DeviceVideoPreferences(99999, 720, 999, (DecoderPreference)999).Apply(capture);
        Check(capture.RenderWidth == 1280 && capture.FrameRate == 30, "Malformed video preferences were applied.");
        Console.WriteLine("PASS branch isolation, ordered two-device setup, all-step skipping without hardware, dependent binding skips, backtracking, resume and corrupt checkpoint.");

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var language = typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var render = typeof(FirstRunSetupWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var vm = new MainViewModel();
        try
        {
            var startupStore = new FirstRunSetupStore(Path.Combine(output, "startup-once.json"));
            if (File.Exists(startupStore.PathName)) File.Delete(startupStore.PathName);
            Check(startupStore.Load().ShouldOpen, "First startup did not request setup.");
            var firstDisplay = new FirstRunSetupWindow(vm, startupStore, checkOnOpen: false);
            firstDisplay.Show(); firstDisplay.UpdateLayout();
            Check(!startupStore.Load().ShouldOpen, "First display did not persist the automatic-open guard.");
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(firstDisplay, true);
            firstDisplay.Close();
            Check(!startupStore.Load().ShouldOpen, "Unfinished setup reopened on the next startup.");
            var manualDisplay = new FirstRunSetupWindow(vm, startupStore, rerun: true, checkOnOpen: false);
            manualDisplay.Show(); manualDisplay.UpdateLayout();
            Check(manualDisplay.IsVisible && !startupStore.Load().ShouldOpen, "Manual setup entry was blocked or rearmed automatic startup.");
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manualDisplay, true);
            manualDisplay.Close();
            Console.WriteLine("PASS setup opens automatically once and remains available manually with unfinished progress.");
            var count = 0;
            foreach (var culture in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var width in new[] { 600, 880 })
            {
                language.Invoke(null, [culture, false, false]); ThemeService.Apply(theme);
                foreach (var step in Enum.GetValues<SetupStep>().Where(s => s is not (SetupStep.Devices or SetupStep.WiredControl or SetupStep.WirelessControl or SetupStep.WirelessIntroduction or SetupStep.BluetoothIntroduction)))
                {
                    var previewStore = new FirstRunSetupStore(Path.Combine(output, "preview.json"));
                    var state = new FirstRunSetupState { Usage = SetupUsage.MirrorAndControl, Step = step, PreferencesApplied = true,
                        Devices = [new() { Id = "preview-only-device", Name = "iPhone · UI preview" }] };
                    foreach (var outcome in new[] { SetupStep.Profile, SetupStep.WiredControl, SetupStep.Wireless, SetupStep.WirelessControl, SetupStep.Bluetooth })
                        state.Devices[0].Outcomes[outcome] = outcome == SetupStep.Bluetooth ? SetupOutcome.Skipped : SetupOutcome.Verified;
                    if (step is SetupStep.Connection or SetupStep.Completed)
                        for (var index = 1; index < 9; index++) state.Devices.Add(new() { Id = "preview-" + index, Name = "iPad · " + index,
                            Outcomes = new(state.Devices[0].Outcomes) });
                    previewStore.Save(state);
                    var window = new FirstRunSetupWindow(vm, previewStore, previewOnly: true) { Width = width, Height = width == 600 ? 540 : 760 };
                    IPhoneMirror.UI.Animations.PageTransition.SetIsEnabled((DependencyObject)window.Content, false);
                    window.State.Step = step;
                    try
                    {
                        window.Show(); render.Invoke(window, null); window.UpdateLayout();
                        Check(window.OwnedWindows.Count == 0, "Setup spawned another window.");
                        if (width == 880)
                        {
                            var scroll = (ScrollViewer)window.FindName("Scroller");
                            Check(scroll.ScrollableHeight <= 1, $"Default-size page requires scrolling: {culture}/{theme}/{step}: {scroll.ScrollableHeight}");
                            foreach (var card in ComponentVisuals<RadioButton>(window))
                            foreach (var text in ComponentVisuals<TextBlock>(card))
                            {
                                var bounds = text.TransformToAncestor(card).TransformBounds(new Rect(text.RenderSize));
                                Check(bounds.Bottom <= card.ActualHeight + 1 && bounds.Right <= card.ActualWidth + 1, $"Clipped card text: {culture}/{step}/{text.Text}");
                            }
                        }
                        var indicator = (Grid)window.FindName("StepIndicator");
                        Check(indicator.Children.Count == 5, "Setup must show three categories joined by two lines.");
                        foreach (var name in new[] { "Previous", "Next", "Skip", "CancelOperation" })
                        {
                            var button = (Button)window.FindName(name);
                            if (!button.IsVisible) continue;
                            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                            Check(bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
                                $"Clipped navigation: {culture}/{theme}/{width}/{step}/{name}: {bounds}");
                        }
                        if (step is SetupStep.Environment or SetupStep.ReDetection or SetupStep.Profile or SetupStep.WiredControl or SetupStep.Validation)
                            Check(!((Button)window.FindName("Next")).IsEnabled, "Unverified hardware advanced: " + step);
                        foreach (var text in ComponentVisuals<TextBlock>(window))
                            Check(!text.Text.StartsWith("Setup", StringComparison.Ordinal), "Missing localization: " + text.Text);
                        if (width == 880 && culture == "zh-CN" || width == 600 && culture == "en-US" && step is SetupStep.Preferences or SetupStep.ControlIntroduction or SetupStep.Usage)
                        {
                            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(output, $"{culture}-{theme}-{width}-{step}.png")); png.Save(file);
                        }
                        if (width == 880 && culture == "zh-CN" && step == SetupStep.Usage)
                        {
                            foreach (var dpi in new[] { 144, 192 })
                            {
                                var scaled = new RenderTargetBitmap((int)(window.ActualWidth * dpi / 96), (int)(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                                scaled.Render(window);
                                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(scaled));
                                using var file = File.Create(Path.Combine(output, $"{culture}-{theme}-{dpi}dpi.png")); png.Save(file);
                            }
                        }
                        if (width == 880)
                        {
                            var nextPage = ComponentVisuals<Button>(window).FirstOrDefault(b => b.Content is string text && text == "›");
                            var previousPage = ComponentVisuals<Button>(window).FirstOrDefault(b => b.Content is string text && text == "‹");
                            while (nextPage?.IsEnabled == true)
                            {
                                nextPage.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); window.UpdateLayout();
                                Check(((ScrollViewer)window.FindName("Scroller")).ScrollableHeight <= 1, "A later option/device page required scrolling.");
                            }
                            while (previousPage?.IsEnabled == true)
                            {
                                previousPage.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); window.UpdateLayout();
                            }
                        }
                        if (step == SetupStep.Environment)
                        {
                            using (var lockedCheckpoint = File.Open(previewStore.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                            {
                                typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
                                Check(window.State.Step == SetupStep.Environment && window.State.DeviceIndex == 0,
                                    "Failed checkpoint save moved state away from the visible page.");
                            }
                            var run = typeof(FirstRunSetupWindow).GetMethod("RunOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                            var failed = (Task)run.Invoke(window, new object[] { new Func<CancellationToken, Task>(_ => Task.FromException(new IOException("Test-only installation failure"))) })!;
                            Check(failed.IsCompleted && !((Button)window.FindName("Next")).IsEnabled && ((Expander)window.FindName("Help")).Visibility == Visibility.Visible,
                                "Installation failure did not retain the hardware gate and recovery guidance.");
                            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            var running = (Task)run.Invoke(window, new object[] { new Func<CancellationToken, Task>(_ => pending.Task) })!;
                            Check(!((Button)window.FindName("Previous")).IsEnabled && !((Button)window.FindName("Next")).IsEnabled &&
                                ((StackPanel)window.FindName("BusyBar")).Visibility == Visibility.Visible, "Busy state allowed navigation or hid feedback.");
                            Check(((StackPanel)window.FindName("Body")).Visibility == Visibility.Collapsed, "Busy step did not reserve room for its inline prompt.");
                            Check(((Button)window.FindName("Skip")).IsEnabled && ((Button)window.FindName("CancelOperation")).IsEnabled,
                                "Busy step does not expose cancellation and skipping.");
                            window.UpdateLayout();
                            var busyButtons = new[] { "Previous", "CancelOperation", "Skip", "Next" }.Select(n => (Button)window.FindName(n)).ToArray();
                            for (var index = 1; index < busyButtons.Length; index++)
                                Check(busyButtons[index - 1].TranslatePoint(new Point(busyButtons[index - 1].ActualWidth, 0), window).X <=
                                    busyButtons[index].TranslatePoint(new Point(), window).X + 1,
                                    $"Busy footer controls overlap: {culture}/{theme}/{width}");
                            var cancellation = (CancellationTokenSource)typeof(FirstRunSetupWindow).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                            cancellation.Cancel(); pending.TrySetCanceled();
                            var frame = new DispatcherFrame();
                            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                            var attempts = 0;
                            timer.Tick += (_, _) => { if (running.IsCompleted || ++attempts > 100) frame.Continue = false; };
                            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                            Check(running.IsCompleted && ((Button)window.FindName("Previous")).IsEnabled, "Cancellation left navigation blocked.");
                        }
                        window.Close(); Check(window.IsVisible, "Close discarded progress without confirmation.");
                        ((System.Windows.Controls.Border)window.FindName("ExitPanel")).Visibility = Visibility.Collapsed;
                        typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    }
                    finally { window.Close(); }
                    Check(!vm.SetupActive, "Setup lifecycle flag leaked."); count++;
                }
                Console.WriteLine($"PASS layouts {culture}/{theme}/{width}");
            }
            Console.WriteLine($"PASS {count} page/theme/language/size layouts, navigation bounds, hardware gates, close confirmation and lifecycle cleanup.");
            // Exercise the production animation path on choice-only pages: no hardware is started.
            var animationStore = new FirstRunSetupStore(Path.Combine(output, "animation-preview.json"));
            animationStore.Save(new() { Step = SetupStep.Usage, Usage = SetupUsage.MirrorAndControl });
            var animatedWindow = new FirstRunSetupWindow(vm, animationStore, checkOnOpen: false);
            void Pump(int milliseconds)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            }
            try
            {
                animatedWindow.Show(); animatedWindow.UpdateLayout(); Pump(400);
                var card = ComponentVisuals<RadioButton>(animatedWindow).First();
                card.IsChecked = true;
                Check(animatedWindow.State.Usage == SetupUsage.WirelessOnly, "Selecting an animated card did not update the branch.");
                Check(card.Tag is Geometry, "Card icon depends on an emoji font.");
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                Pump(240);
                if (SystemParameters.ClientAreaAnimation)
                    Check(((TransformGroup)card.RenderTransform).Children.OfType<TranslateTransform>().Single().Y < -2.5, "Hover animation did not lift the card.");
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(animatedWindow, [true]);
                Check(animatedWindow.FindName("OutgoingPage") is null, "Page transition still stretches an old screenshot.");
                Check(((TextBlock)animatedWindow.FindName("Heading")).RenderTransform is not ScaleTransform, "Page heading was scaled during navigation.");
                Pump(500);
                Check(animatedWindow.State.Step == SetupStep.Appearance, "Animated back navigation selected the wrong page.");
                typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(animatedWindow, [true]);
                typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(animatedWindow, [true]);
                Pump(600); animatedWindow.UpdateLayout();
                var languageCards = ComponentVisuals<RadioButton>(animatedWindow).ToArray();
                var positions = languageCards.Select(c => c.TranslatePoint(new Point(), animatedWindow)).ToArray();
                language.Invoke(null, ["zh-CN", false, false]); animatedWindow.UpdateLayout(); Pump(400);
                var localizedCards = ComponentVisuals<RadioButton>(animatedWindow).ToArray();
                Check(languageCards.SequenceEqual(localizedCards), "Changing language rebuilt the cards.");
                var localizedPositions = localizedCards.Select(c => c.TranslatePoint(new Point(), animatedWindow)).ToArray();
                Check(localizedPositions.Zip(positions).All(p => (p.First - p.Second).Length < .5),
                    "Changing language moved the cards: " + string.Join("; ", positions.Zip(localizedPositions).Select(p => $"{p.First} -> {p.Second}")));
                Check(localizedCards.All(c => !c.HasAnimatedProperties), "Changing language replayed card entry animation.");
                animatedWindow.State.Step = SetupStep.Welcome;
                typeof(FirstRunSetupWindow).GetMethod("Enter", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int)])!.Invoke(animatedWindow, [1]);
                Pump(160);
                var welcomeHeading = (TextBlock)animatedWindow.FindName("Heading");
                if (SystemParameters.ClientAreaAnimation) Check(welcomeHeading.Opacity < 1, "Welcome text did not fade in.");
                Check(welcomeHeading.TextAlignment == TextAlignment.Center && welcomeHeading.RenderTransform is not ScaleTransform, "Welcome heading is not centered or is scaled.");
                Pump(750);
                Console.WriteLine("PASS live-text transitions without scaling, card feedback and stable language switching.");
            }
            finally
            {
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(animatedWindow, true);
                animatedWindow.Close();
            }
            var flowStore = new FirstRunSetupStore(Path.Combine(output, "flow-preview.json"));
            flowStore.Save(new() { Step = SetupStep.Connection, Usage = SetupUsage.MirrorAndControl });
            var flow = new FirstRunSetupWindow(vm, flowStore, previewOnly: true);
            void Complete(Task task)
            {
                for (var attempt = 0; !task.IsCompleted && attempt < 100; attempt++) Pump(25);
                Check(task.IsCompleted, "Setup operation did not complete."); task.GetAwaiter().GetResult();
            }
            try
            {
                flow.Show();
                var scans = 0;
                flow.DiscoverDevices = _ => Task.FromResult(new SetupDriverProgress("Result", true, null,
                    ++scans == 1 ? [new("one", "iPhone One")] : [new("one", "iPhone One"), new("two", "iPhone Two")]));
                typeof(FirstRunSetupWindow).GetMethod("StartDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                Pump(1750);
                Check(scans >= 2 && flow.State.Devices.Count == 2, "Discovery stopped after finding the first device.");
                Check(((TextBlock)flow.FindName("DeviceLabel")).Text.Contains('2'), "Live count was not updated.");
                typeof(FirstRunSetupWindow).GetMethod("StopDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                var stoppedCount = scans; Pump(1700);
                Check(scans == stoppedCount, "Discovery kept scanning after leaving the page.");

                var pendingScan = new TaskCompletionSource<SetupDriverProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                var freshScans = 0;
                flow.DiscoverDevices = _ => ++freshScans == 1 ? pendingScan.Task :
                    Task.FromResult(new SetupDriverProgress("Result", true, null, []));
                typeof(FirstRunSetupWindow).GetMethod("StartDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                Check(!((Button)flow.FindName("Next")).IsEnabled, "Restored inventory advanced before a fresh scan.");
                var advance = (Task)typeof(FirstRunSetupWindow).GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null)!;
                pendingScan.SetResult(new("Result", true, null, [])); Complete(advance);
                Check(flow.State.Step == SetupStep.Connection && flow.State.Devices.Count == 0,
                    "Next discarded the final scan and advanced with an unplugged device.");
                typeof(FirstRunSetupWindow).GetMethod("StopDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                Pump(50);
                flow.DiscoverDevices = _ => Task.FromCanceled<SetupDriverProgress>(new CancellationToken(true));
                typeof(FirstRunSetupWindow).GetMethod("StartDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                Pump(50);
                Check(!((Button)flow.FindName("Next")).IsEnabled && ((Expander)flow.FindName("Help")).Visibility == Visibility.Visible,
                    "A scanner timeout silently retained stale device readiness.");
                typeof(FirstRunSetupWindow).GetMethod("StopDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                Pump(50);

                flow.DiscoverDevices = async token =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new("Result", true, null, []);
                };
                typeof(FirstRunSetupWindow).GetMethod("StartDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                var cancelAdvance = (Task)typeof(FirstRunSetupWindow).GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null)!;
                Check(((Button)flow.FindName("CancelOperation")).IsEnabled && ((Button)flow.FindName("Skip")).IsEnabled,
                    "Waiting for the final scan disabled both cancellation and skipping.");
                ((Button)flow.FindName("CancelOperation")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Complete(cancelAdvance);
                Check(flow.State.Step == SetupStep.Connection && typeof(FirstRunSetupWindow).GetField("_discovery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flow) is null,
                    "Cancelling final discovery advanced or restarted scanning.");
                typeof(FirstRunSetupWindow).GetMethod("StartDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
                var skipAdvance = (Task)typeof(FirstRunSetupWindow).GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null)!;
                ((Button)flow.FindName("Skip")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Complete(skipAdvance);
                Check(flow.State.Step == SetupStep.Display && flow.State.Devices.Count == 0,
                    "Skipping the final scan did not proceed without a device.");

                flow.State.Step = SetupStep.WirelessBackend;
                vm.SelectedWirelessReceiverBackend = vm.WirelessReceiverBackends.First(v => v.Backend == WirelessReceiverBackend.UxPlay);
                render.Invoke(flow, null);
                var installed = false; var installs = 0;
                flow.ReceiverComponentAvailable = () => installed;
                flow.InstallReceiverComponent = async (progress, installing, token) =>
                {
                    installs++; progress.Report(new UpdateDownloadProgress(40, 100, 10));
                    await Task.Delay(60, token);
                    Check(((ProgressBar)flow.FindName("DownloadProgress")).Value == 40, "Component progress did not display actual percentage.");
                    installing(); installed = true;
                };
                var run = typeof(FirstRunSetupWindow).GetMethod("RunOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var ensure = typeof(FirstRunSetupWindow).GetMethod("EnsureReceiverComponentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Func<CancellationToken, Task> install = token => (Task)ensure.Invoke(flow, [token])!;
                Complete((Task)run.Invoke(flow, [install])!);
                Complete((Task)run.Invoke(flow, [install])!);
                Check(installs == 1, "Available UxPlay was downloaded again.");
                installed = false;
                flow.InstallReceiverComponent = (_, _, _) => Task.FromException(new IOException("Test download failure"));
                Complete((Task)run.Invoke(flow, [install])!);
                Check(!(bool)typeof(FirstRunSetupWindow).GetField("_verified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flow)!, "Failed download was accepted.");
                flow.InstallReceiverComponent = (_, _, token) => Task.Delay(Timeout.Infinite, token);
                var cancelling = (Task)run.Invoke(flow, [install])!; Pump(50);
                ((CancellationTokenSource)typeof(FirstRunSetupWindow).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flow)!).Cancel();
                Complete(cancelling);
                Check(((Button)flow.FindName("Previous")).IsEnabled, "Cancelling component download left navigation blocked.");
                Console.WriteLine("PASS continuous discovery, live count, scan cancellation, inline component download, existing-component reuse, failure and cancellation.");

                flow.State.Usage = SetupUsage.MirrorAndControl; flow.State.Step = SetupStep.Environment;
                flow.State.Devices = [new() { Id = "skip-device", Name = "Skip test" }]; flow.State.DeviceIndex = 0;
                render.Invoke(flow, null);
                var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pendingOperation = (Task)run.Invoke(flow, [new Func<CancellationToken, Task>(_ => cleanup.Task)])!;
                ((Button)flow.FindName("CancelOperation")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(50);
                Check(!pendingOperation.IsCompleted && !((Button)flow.FindName("Previous")).IsEnabled,
                    "Cancellation released navigation before operation cleanup ended.");
                Check(((Button)flow.FindName("Skip")).IsEnabled, "Running operation cannot be skipped.");
                flow.UpdateLayout();
                var footerButtons = new[] { "Previous", "CancelOperation", "Skip", "Next" }.Select(n => (Button)flow.FindName(n)).ToArray();
                for (var index = 1; index < footerButtons.Length; index++)
                    Check(footerButtons[index - 1].TranslatePoint(new Point(footerButtons[index - 1].ActualWidth, 0), flow).X <=
                        footerButtons[index].TranslatePoint(new Point(), flow).X + 1, "Cancel and skip controls overlap.");
                ((Button)flow.FindName("Skip")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(flow.State.Step == SetupStep.Environment, "Skip advanced before safe cancellation completed.");
                cleanup.SetResult(); Complete(pendingOperation);
                Check(flow.State.Step == SetupStep.ReDetection && flow.State.Devices[0].Outcomes[SetupStep.Environment] == SetupOutcome.Skipped,
                    "Queued skip did not continue after cancellation.");
                ((Button)flow.FindName("Skip")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(flow.State.Step == SetupStep.DeviceComplete && !flow.State.HasSavedDevices,
                    "Skipping identity still required dependent binding.");

                flow.State.Step = SetupStep.WirelessBackend; flow.State.DeviceIndex = 0;
                flow.State.CurrentDevice!.Outcomes[SetupStep.Profile] = SetupOutcome.Verified;
                flow.State.CurrentDevice.Outcomes[SetupStep.WirelessBackend] = SetupOutcome.Skipped;
                flow.State.CurrentDevice.Outcomes.Remove(SetupStep.Wireless);
                using (var lockedState = File.Open(flowStore.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    typeof(FirstRunSetupWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, [false]);
                    Check(flow.State.Step == SetupStep.WirelessBackend && !flow.State.CurrentDevice!.Outcomes.ContainsKey(SetupStep.Wireless),
                        "Failed navigation save retained mutations from the destination step.");
                }

                flow.State.Step = SetupStep.ReDetection; render.Invoke(flow, null);
                var delayedProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var poll = typeof(FirstRunSetupWindow).GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Func<CancellationToken, Task> polling = token => (Task)poll.Invoke(flow, [new Func<Task<bool>>(() => delayedProbe.Task), token])!;
                var pollingOperation = (Task)run.Invoke(flow, [polling])!;
                ((Button)flow.FindName("CancelOperation")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(50);
                Check(!pollingOperation.IsCompleted, "Cancelling abandoned an in-flight native probe.");
                delayedProbe.SetResult(true); Complete(pollingOperation);
                Check(!((Button)flow.FindName("Next")).IsEnabled, "Cancelled probe was marked verified after its late result.");

                flow.State.Usage = SetupUsage.WirelessOnly; flow.State.Step = SetupStep.Validation;
                flow.State.Devices.Clear(); flow.State.GlobalOutcomes.Clear(); render.Invoke(flow, null);
                flow.State.GlobalOutcomes[SetupStep.Wireless] = SetupOutcome.Verified;
                flow.ApplyWirelessPreferences = _ => Task.FromException<bool>(new IOException("Test preferences save failure"));
                Complete((Task)typeof(FirstRunSetupWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, [false])!);
                Check(!((Button)flow.FindName("Next")).IsEnabled && flow.State.GlobalOutcomes.GetValueOrDefault(SetupStep.Validation) != SetupOutcome.Verified,
                    "Wireless save failure was marked complete.");
                ((Button)flow.FindName("Skip")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(flow.State.Step == SetupStep.Completed && flow.State.GlobalOutcomes[SetupStep.Validation] == SetupOutcome.Skipped,
                    "Saving preferences could not be explicitly skipped.");
                Check(((Button)flow.FindName("Next")).IsEnabled && ((TextBlock)flow.FindName("Heading")).Text == LocalizationService.Get("SetupTitlePartial"),
                    "Partial setup was blocked or presented as fully configured.");
                flow.State.Disposition = SetupDisposition.InProgress;
                using (var lockedState = File.Open(flowStore.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Complete((Task)typeof(FirstRunSetupWindow).GetMethod("FinishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, [false])!);
                    Check(flow.State.Disposition == SetupDisposition.InProgress && flow.IsVisible,
                        "Finish save failure left setup marked complete.");
                }
                flow.State.GlobalOutcomes[SetupStep.Wireless] = SetupOutcome.Skipped;
                flow.State.Step = SetupStep.Validation;
                var receiverApplications = 0;
                flow.ApplyWirelessPreferences = _ => { receiverApplications++; return Task.FromResult(false); };
                Complete((Task)typeof(FirstRunSetupWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, [false])!);
                Check(receiverApplications == 0, "Finalizing skipped wireless setup applied receiver settings.");

                var settingsField = typeof(App).GetField("_settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var previousStore = settingsField.GetValue(app);
                var settingsSnapshot = app.UpdateSettings.Clone();
                var blocker = Path.Combine(output, "settings-parent-file"); File.WriteAllText(blocker, "not a directory");
                try
                {
                    settingsField.SetValue(app, new UpdateSettingsStore(Path.Combine(Path.GetFullPath(blocker), "settings.json")));
                    app.IsUiPreviewMode = false;
                    Check(!vm.SetupSaveWirelessPreferences(), "Wireless persistence swallowed a write failure.");
                    Check(app.UpdateSettings.WirelessReceiverBackend == settingsSnapshot.WirelessReceiverBackend &&
                        app.UpdateSettings.WirelessDisplayProfileId == settingsSnapshot.WirelessDisplayProfileId,
                        "Failed wireless save did not restore the previous preferences.");
                }
                finally { app.IsUiPreviewMode = true; settingsField.SetValue(app, previousStore); app.RestoreUpdateSettings(settingsSnapshot); }

                flow.State.Step = SetupStep.Preferences; flow.State.Usage = SetupUsage.WiredOnly;
                flow.State.GlobalOutcomes.Clear(); render.Invoke(flow, null);
                for (var attempt = 0; flow.State.Step != SetupStep.Completed && attempt < 40; attempt++)
                    ((Button)flow.FindName("Skip")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(flow.State.Step == SetupStep.Completed && !flow.State.HasSavedDevices, "UI could not skip every step without a device.");
                ((Button)flow.FindName("Next")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!flow.IsVisible && !flow.StartRequested && flowStore.Load().Disposition == SetupDisposition.Completed,
                    "Finishing skipped setup forced device validation or mirroring.");
                var closing = new FirstRunSetupWindow(vm, flowStore, previewOnly: true);
                closing.State.Step = SetupStep.Environment; closing.Show();
                var closeCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closeTask = (Task)run.Invoke(closing, [new Func<CancellationToken, Task>(_ => closeCleanup.Task)])!;
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(closing, true);
                closing.Close();
                Check(vm.SetupActive && !closeTask.IsCompleted, "Closing released setup ownership before cleanup.");
                closeCleanup.SetResult(); Complete(closeTask);
                Check(!vm.SetupActive, "Closed setup never released ownership after cleanup.");
                var completedDraft = new FirstRunSetupState { Disposition = SetupDisposition.Completed, Step = SetupStep.Completed,
                    WirelessRestarted = true, CustomizeDisplay = true, DisplayDeviceIndex = 8,
                    WirelessBackendDraft = WirelessReceiverBackend.UxPlay };
                flowStore.Save(completedDraft);
                var rerun = new FirstRunSetupWindow(vm, flowStore, rerun: true, previewOnly: true);
                Check(rerun.State.Disposition == SetupDisposition.Completed && rerun.State.CustomizeDisplay,
                    "Opening the assistant erased completed setup history before checking it.");
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rerun, true); rerun.Close();
                flowStore.Save(new() { Disposition = SetupDisposition.InProgress, Step = SetupStep.WirelessBackend,
                    WirelessBackendDraft = WirelessReceiverBackend.Original, WirelessProfileDraft = vm.WirelessDisplayProfiles.Last().Id });
                var resumed = new FirstRunSetupWindow(vm, flowStore, previewOnly: true, checkOnOpen: false);
                Check(vm.SelectedWirelessReceiverBackend.Backend == WirelessReceiverBackend.Original &&
                    vm.SelectedWirelessDisplayProfile.Id == vm.WirelessDisplayProfiles.Last().Id, "Resume lost wireless preference draft.");
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resumed, true); resumed.Close();
                RunFirstRunSetupUsabilityTests(vm, output, Pump, Complete, Check);
                Console.WriteLine("PASS 256 mixed multi-device routes, identity backtracking, checkpoint rollback, native probe ownership, wireless skip isolation, clean rerun and wireless draft restore.");
                Console.WriteLine("PASS fresh discovery gate, final unplug scan, cancel ownership, running skip, identity dependencies, persistence failure and all-skipped UI completion.");
            }
            finally
            {
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(flow, true);
                flow.Close();
            }
        }
        finally { app.Shutdown(); }
        return 0;
    }
}
