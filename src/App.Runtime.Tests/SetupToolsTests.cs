using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void ToolsPump(int milliseconds = 30)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Sleep(2);
        } while (timer.ElapsedMilliseconds < milliseconds);
    }
    private static void ToolsComplete(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10)) ToolsPump();
        if (!task.IsCompleted) throw new TimeoutException("Standalone tool task did not settle.");
        task.GetAwaiter().GetResult();
    }
    private static void ToolsCapture(Window window, string path)
    {
        ToolsPump(400); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static int RunSetupToolsTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var vm = new MainViewModel();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var reopenStore = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), "reopen.json"));
        foreach (var disposition in new[] { SetupDisposition.New, SetupDisposition.InProgress, SetupDisposition.Completed })
        {
            var savedStep = disposition == SetupDisposition.InProgress ? SetupStep.Appearance :
                disposition == SetupDisposition.Completed ? SetupStep.Completed : SetupStep.Welcome;
            reopenStore.Save(new() { Disposition = disposition, Step = savedStep });
            var probes = 0;
            var reopened = new FirstRunSetupWindow(vm, reopenStore, rerun: true)
            {
                CheckRuntime = new() { ReadSettingsAsync = _ => { probes++; return Task.FromException<SetupSettingsEvidence>(new Exception("Opening setup must not audit configuration.")); } }
            };
            try
            {
                reopened.Show(); ToolsPump(650);
                var expected = disposition == SetupDisposition.InProgress ? savedStep : SetupStep.Welcome;
                Check(reopened.State.Step == expected && probes == 0 && !reopened.IsAssessing,
                    "Opening setup started an unfinished-settings audit or lost the saved page.");
                Check(!(bool)typeof(FirstRunSetupWindow).GetField("_assessmentVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!,
                    "Opening setup displayed the unfinished-settings summary.");
            }
            finally
            {
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reopened, true);
                reopened.Close();
            }
        }
        // First-run storage failures should show a usable welcome page and a recoverable error.
        reopenStore.Save(new());
        var failedOpen = new FirstRunSetupWindow(vm, reopenStore);
        try
        {
            using (var lockedState = File.Open(reopenStore.PathName, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                failedOpen.Show(); ToolsPump();
                Check(((TextBlock)failedOpen.FindName("Heading")).Text == LocalizationService.Get("SetupTitleWelcome") &&
                    ((TextBlock)failedOpen.FindName("Status")).Text == LocalizationService.Get("SetupSaveFailed"),
                    "First-run checkpoint failure left an empty or misleading setup page.");
            }
        }
        finally
        {
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(failedOpen, true);
            failedOpen.Close();
        }
        Console.WriteLine("PASS first open, resume and completed rerun enter setup directly without configuration audits.");
        var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), "handoff.json"));
        store.Save(new() { Step = SetupStep.Environment, Usage = SetupUsage.WiredOnly, Devices = [new() { Id = "fixture-phone", Name = "iPhone" }] });
        var window = new FirstRunSetupWindow(vm, store, previewOnly: true);
        Task Run() => (Task)typeof(FirstRunSetupWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false])!;
        bool Verified() => (bool)typeof(FirstRunSetupWindow).GetField("_verified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            window.Show();
            var completion = new TaskCompletionSource<DriverManagerLaunchResult>(); var opens = 0; var probes = 0;
            window.OpenDriverToolAsync = token => { opens++; return completion.Task.WaitAsync(token); };
            window.PrepareDevice = (_, install, _, _) => { Check(!install, "Assistant performed an inline installation."); probes++; return Task.FromResult(new SetupDriverProgress("Result", true, null)); };
            var pending = Run(); ToolsPump();
            var setupHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Check(!window.AllowsTransparency &&
                (GetSetupWindowStyle(setupHandle, -20).ToInt64() & 0x00080000) == 0,
                "Setup still uses a layered HWND for its large animated page.");
            var setupSurface = (Border)window.Template.FindName("RoundedWindowSurface", window);
            Check(setupSurface.Effect is null && setupSurface.Background is SolidColorBrush { Color.A: 255 },
                "Setup composites its whole animated content through a shadow or translucent surface.");
            Check(window.WindowState == WindowState.Normal &&
                !(bool)typeof(FirstRunSetupWindow).GetField("_activityAnimating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                "Driver handoff animated or minimized the setup window.");
            ToolsComplete(Run());
            Check(opens == 1 && probes == 0 && !Verified(), "Handoff was duplicated or verified before tool exit.");
            completion.SetResult(new(true, null, "closed")); ToolsComplete(pending);
            Check(Verified() && probes == 1, "Returned driver tool did not verify actual state.");
            Check(window.WindowState == WindowState.Normal, "Driver exit did not restore setup.");
            window.OpenDriverToolAsync = _ => Task.FromResult(new DriverManagerLaunchResult(false, null, "missing executable"));
            ToolsComplete(Run()); Check(!Verified() && probes == 1, "Failed launch incorrectly advanced setup.");
            Check(window.WindowState == WindowState.Normal, "Failed driver launch stranded minimized setup.");
            window.OpenDriverToolAsync = _ => Task.FromResult(new DriverManagerLaunchResult(true, null, "closed"));
            window.PrepareDevice = (_, _, _, _) => Task.FromResult(new SetupDriverProgress("Replug", true, null));
            ToolsComplete(Run()); Check(!Verified(), "A required replug was incorrectly marked ready.");
            completion = new(); window.OpenDriverToolAsync = token => completion.Task.WaitAsync(token);
            pending = Run(); ToolsPump();
            ((Button)window.FindName("CancelOperation")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ToolsComplete(pending); Check(!Verified() && !completion.Task.IsCompleted, "Cancelling the assistant completed the independent tool.");
            Check(window.WindowState == WindowState.Normal, "Cancelling driver handoff did not restore setup.");
            var bindingsPath = Path.Combine(Path.GetFullPath(output), "bindings-" + Guid.NewGuid().ToString("N") + ".json");
            window.CheckRuntime = new() { ReadBindingsAsync = _ => Task.FromResult(new DeviceBindingManager(bindingsPath, readOnly: true)) };
            window.State.Step = SetupStep.Profile;
            window.OpenBindingToolAsync = (_, _) => Task.FromResult<string?>(null);
            ToolsComplete(Run()); Check(!Verified(), "Closing an unconfigured binder marked a profile ready.");
            var bindings = new DeviceBindingManager(bindingsPath);
            window.OpenBindingToolAsync = (identity, _) =>
            {
                Check(identity == "fixture-phone", "Binder lost the target device.");
                Check(bindings.CreateProfileFromIdentity("iPhone", DeviceIdentityType.Wired, identity!, null).Success, "Fixture save failed.");
                return Task.FromResult<string?>(null);
            };
            ToolsComplete(Run()); Check(Verified(), "Persisted binding was not recognized.");
            window.State.Step = SetupStep.Wireless; window.OpenBindingToolAsync = (_, _) => Task.FromResult<string?>(null);
            ToolsComplete(Run()); Check(!Verified(), "A USB profile was treated as an AirPlay binding.");
            var profile = bindings.Profiles.Single();
            bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, "fixture-airplay", "iPhone", null, true);
            ToolsComplete(Run()); Check(Verified(), "An AirPlay binding was not recognized.");
            window.State.Usage = SetupUsage.MirrorAndControl; window.State.Step = SetupStep.Profile;
            ToolsComplete(Run());
            Check(Verified() && window.State.CurrentDevice!.Outcomes[SetupStep.Wireless] == SetupOutcome.Verified &&
                window.State.CurrentDevice.Outcomes[SetupStep.Bluetooth] == SetupOutcome.Skipped,
                "Combined binding did not record saved AirPlay and optional Bluetooth state.");
            window.State.CurrentDevice!.Outcomes[SetupStep.Environment] = SetupOutcome.Verified;
            window.State.CurrentDevice.Outcomes[SetupStep.ReDetection] = SetupOutcome.Verified;
            window.State.Next();
            Check(window.State.Step == SetupStep.DeviceComplete && window.State.DeviceComplete(window.State.CurrentDevice),
                "Combined binder still requires another binding page.");
            window.State.Previous(); Check(window.State.Step == SetupStep.Profile, "Back navigation missed the combined binder.");
            bindings.Bind(profile.Id, DeviceIdentityType.Bluetooth, "fixture-bluetooth", "iPhone", null, true);
            ToolsComplete(Run());
            Check(Verified() && window.State.CurrentDevice.Outcomes[SetupStep.Bluetooth] == SetupOutcome.Verified,
                "Combined binding did not recognize a saved Bluetooth identity.");
            bindings.Unbind(profile.Id, DeviceIdentityType.Bluetooth);
            // Wireless-only setup must use the chosen profile, never import all historical profiles.
            window.State.Usage = SetupUsage.WirelessOnly; window.State.Step = SetupStep.Wireless; window.State.Devices.Clear();
            bindings.CreateProfileFromIdentity("Offline iPhone", DeviceIdentityType.AirPlay, "offline-airplay", null);
            window.OpenBindingToolAsync = (_, _) => Task.FromResult<string?>(null);
            ToolsComplete(Run()); Check(!Verified() && window.State.Devices.Count == 0, "Unselected historical AirPlay profiles were adopted.");
            window.OpenBindingToolAsync = (_, _) => Task.FromResult<string?>("fixture-airplay");
            ToolsComplete(Run());
            Check(Verified() && window.State.Devices.Count == 1 && window.State.Devices[0].Id == "fixture-airplay",
                "Wireless setup ignored the profile selected in the binder.");
            window.State.Devices.Clear();
            using (var checkpointLock = File.Open(store.PathName, FileMode.Open, FileAccess.Read, FileShare.None))
                ToolsComplete(Run());
            Check(!Verified() && window.State.Devices.Count == 0, "A failed checkpoint save retained an adopted device queue.");
            RunBinderWorkflowTests(window, vm, output, Check);
            typeof(FirstRunSetupWindow).GetMethod("ShowAssessmentSummary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            completion = new(); window.OpenDriverToolAsync = token => completion.Task.WaitAsync(token);
            var summaryWait = (Task)typeof(FirstRunSetupWindow).GetMethod("OpenToolFromSummaryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true])!;
            ToolsPump();
            var cancel = (Button)window.FindName("CancelOperation");
            Check(cancel.IsEnabled && cancel.Visibility == Visibility.Visible, "Summary handoff has no usable cancel action.");
            cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ToolsComplete(summaryWait);
            Check(!completion.Task.IsCompleted && ((Button)window.FindName("Next")).IsEnabled, "Cancelling summary handoff left the assistant locked.");
            Console.WriteLine("PASS independent tool handoff, single flight, launch failure, replug, cancellation, target selection and persisted binding verification.");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [culture, false, false]);
                ThemeService.Apply(theme);
                var binder = new DeviceBindingWindow(window, [], vm, previewOnly: true);
                binder.Show(); ToolsCapture(binder, Path.Combine(output, $"binder-empty-{culture}-{theme}.png"));
                binder.Profiles.Add(new ProfileListItem(bindings.FindById(profile.Id)!));
                binder.SelectedProfile = binder.Profiles[0];
                ToolsCapture(binder, Path.Combine(output, $"binder-{culture}-{theme}.png"));
                Check(binder.HasProfile && binder.HasWiredBinding && binder.HasAirPlayBinding && !binder.HasBluetoothBinding,
                    "Binder statuses do not match the selected profile.");
                Check(ComponentVisuals<ScrollViewer>(binder).All(v => v.ScrollableWidth <= 1), "Binder requires horizontal scrolling.");
                binder.Width = 850; binder.Height = 620;
                ToolsCapture(binder, Path.Combine(output, $"binder-compact-{culture}-{theme}.png"));
                Check(ComponentVisuals<ScrollViewer>(binder).All(v => v.ScrollableWidth <= 1), "Compact binder requires horizontal scrolling.");
                binder.Close();
            }
            Console.WriteLine("PASS binder empty/selected/compact rendering in four languages and two themes.");
        }
        finally
        {
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); app.Shutdown();
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetSetupWindowStyle(nint window, int index);

    private static int RenderDriverTools(string assemblyPath, string output)
    {
        Directory.CreateDirectory(output);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var app = (Application)Activator.CreateInstance(assembly.GetType("IPhoneMirror.DriverInstaller.App")!)!;
        app.GetType().GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var localization = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverLocalization")!;
        foreach (var culture in new[] { "zh-CN", "en-US" })
        foreach (var theme in new[] { "Light", "Dark" })
        {
            localization.GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new[] { "--language", culture }]);
            var dictionary = new ResourceDictionary { Source = new Uri($"/iPhoneMirror.Driver;component/Localization/Strings.{culture}.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Add(dictionary);
            var themeService = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeService")!;
            var themeMode = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeMode")!;
            themeService.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Enum.Parse(themeMode, theme), false]);
            var type = assembly.GetType("IPhoneMirror.DriverInstaller.MainWindow")!;
            var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)!;
            window.Show(); ToolsCapture(window, Path.Combine(output, $"driver-empty-{culture}-{theme}.png"));
            var device = CreateParentAuditDevice(assembly);
            ((System.Collections.IList)type.GetProperty("Devices")!.GetValue(window)!).Add(device);
            ((System.Collections.IList)type.GetProperty("ConnectedDevices")!.GetValue(window)!).Add(device);
            type.GetProperty("SelectedDevice")!.SetValue(window, device);
            ToolsCapture(window, Path.Combine(output, $"driver-{culture}-{theme}.png"));
            type.GetProperty("IsAdvancedMode")!.SetValue(window, true);
            ToolsCapture(window, Path.Combine(output, $"driver-advanced-{culture}-{theme}.png"));
            window.Close();
        }
        app.Shutdown();
        Console.WriteLine("PASS independent driver manager light/dark and simple/advanced rendering.");
        return 0;
    }
}
