using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceHubAutoConnectTests(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var language = LocalizationService.SelectedLanguage;
        var theme = ThemeService.Preference;
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var ready = new HashSet<string>();
        var attempts = new List<(DeviceViewModel Device, CancellationToken Token)>();
        var reads = new List<string>();
        Func<CancellationToken, Task> behavior = _ => Task.CompletedTask;
        async Task Prepare(DeviceViewModel device, CancellationToken token)
        {
            attempts.Add((device, token));
            await behavior(token);
            ready.Add(device.Udid);
        }
        Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Verify(action is "apps" or "get_app_icon" or "list_dir" or "shared_document_apps",
                "Connecting to management must not start capture, logs, or device mutations.");
            if (action == "get_app_icon") throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Fixture has no icon.");
            reads.Add(device.Udid);
            if (action == "list_dir") return Task.FromResult(JsonSerializer.SerializeToElement(new
                { entries = new[] { new { name = "DCIM", path = "DCIM", type = "directory" } }, nextOffset = (int?)null, total = 1 }));
            if (action == "shared_document_apps") return Task.FromResult(JsonSerializer.SerializeToElement(new { apps = Array.Empty<object>() }));
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            { apps = new[] { new { name = device.Name, bundleId = "com.fixture.app", type = "User", version = "1.0" } } }));
        }
        using var panel = new DeviceHubPanel(vm, Request, device => ready.Contains(device.Udid), Path.Combine(output, "icons"), Prepare);
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id, ConnectionState state = ConnectionState.Ready) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = id, ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = state });
        var first = Device("management-wired-a"); var second = Device("airplay://02:00:00:00:00:02"); var third = Device("management-wired-c");
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        void Drain()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(45));
            FinishWorkspaceReveal(window); window.UpdateLayout();
        }
        void Finish() { Drain(); WaitReviewTask((Task)KeyboardField(panel, "_operation")); Drain(); }
        void Open(bool visible) { _ = KeyboardCall(window, "SetDeviceHubPanelVisible", visible); Drain(); }
        void Connect() { Find<Button>("PrepareButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
        void Tick(int count = 5)
        {
            for (var i = 0; i < count; i++) { panel.SetDevice(vm.SelectedDevice, true); KeyboardCall(panel, "OnConnectionWatchTick", null, EventArgs.Empty); }
            Drain();
        }
        TaskCompletionSource? delayed = null;
        try
        {
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            foreach (var device in new[] { first, second, third }) { guidance.Add(device.Udid); vm.Devices.Add(device); }
            vm.SelectedDevice = first; Drain();
            Verify(attempts.Count == 0, "A hidden panel must not initiate a connection.");
            SetKeyboardField(vm, "_isCapturing", true);
            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            behavior = _ => delayed.Task; // Deliberately ignore cancellation to model a late response.
            Open(true);
            Tick();
            Verify(attempts.Count == 0 && reads.Count == 0 && Find<Button>("PrepareButton").IsVisible &&
                Find<Button>("PrepareButton").Content.ToString() == LocalizationService.Get("DeviceHubPanelConnectControl"),
                "Opening the inspector during wired mirroring must wait for explicit permission to enable control.");
            Connect();
            Verify(attempts.Count == 1 && attempts[0].Device == first &&
                Find<TextBlock>("ConnectionText").Text == LocalizationService.Get("DeviceHubPanelConnecting") &&
                !FindVisualDescendants<ProgressBar>(panel).Any() && !Find<Button>("PrepareButton").IsVisible,
                "An explicit connection must show static connection status and hide the connect button while starting.");
            Tick(); Verify(attempts.Count == 1, "Repeated selection notifications and polling must not duplicate a connection.");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(AppTheme.Dark); Drain();
                Verify(Find<TextBlock>("PromptTitle").Text == LocalizationService.Get("DeviceHubPanelConnectTitle") &&
                    Find<TextBlock>("ConnectionText").Text == LocalizationService.Get("DeviceHubPanelConnecting"), "Connecting guidance must follow the current language.");
                SaveWindowRender(window, Path.Combine(output, "connecting-" + culture + ".png"));
            }
            LocalizationService.SetLanguage("zh-CN");
            var firstAttempt = attempts[^1];
            Find<UniformGrid>("PageTabs").Children.OfType<RadioButton>().Single(tab => Equals(tab.Tag, "3")).IsChecked = true;
            FinishWorkspaceReveal(window);
            Verify(!firstAttempt.Token.IsCancellationRequested && attempts.Count == 1 && !Find<Button>("PrepareButton").IsVisible,
                "Switching pages must keep the explicitly requested connection in progress.");
            delayed.SetResult(); Finish(); delayed = null;
            Verify(Find<Grid>("Pages").IsEnabled && Find<ListBox>("FilesList").Items.Count == 1 && reads.Contains(first.Udid),
                "Finishing preparation must load the page selected during startup.");
            Find<RadioButton>("AppsTab").IsChecked = true; Finish();
            Verify(Find<Grid>("Pages").IsEnabled && Find<ListBox>("AppsList").Items.Count == 1 && reads.Contains(first.Udid),
                "A successful connection must automatically load the selected page.");
            var count = attempts.Count;
            Open(false); Open(true); Finish();
            Verify(attempts.Count == count, "Reopening a ready device must reuse its existing connection.");

            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously); behavior = _ => delayed.Task;
            vm.SelectedDevice = second; Drain();
            Tick();
            Verify(attempts.Count == count && Find<Button>("PrepareButton").IsVisible,
                "Selecting a wireless mirror with the panel open must not start reverse control.");
            Connect();
            var secondAttempt = attempts[^1];
            Verify(attempts.Count == count + 1 && secondAttempt.Device == second, "Explicit connection must target the selected wireless device.");
            vm.SelectedDevice = first; Drain();
            Verify(secondAttempt.Token.IsCancellationRequested, "Changing selection must cancel the previous connection scope.");
            delayed.SetResult(); Finish(); delayed = null;
            Verify(!reads.Contains(second.Udid) && ReferenceEquals(KeyboardField(panel, "_device"), first) && Find<Grid>("Pages").IsEnabled,
                "A late connection response must not read or populate another device.");

            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously); behavior = token => delayed.Task.WaitAsync(token);
            vm.SelectedDevice = third; Drain(); Connect(); var hiddenAttempt = attempts[^1];
            Open(false); Finish();
            Verify(hiddenAttempt.Token.IsCancellationRequested && !ready.Contains(third.Udid), "Closing the panel must cancel its own startup.");
            var hiddenCount = attempts.Count; Drain();
            Verify(attempts.Count == hiddenCount, "A hidden panel must not retry a cancelled connection.");
            delayed.SetResult(); delayed = null;
            behavior = _ => throw new AutomationException("DEVICE_HUB_CONNECTION_FAILED", "Fixture requires device trust.");
            Open(true); Finish();
            Verify(attempts.Count == hiddenCount, "Reopening a disconnected panel must stay idle.");
            Connect(); Finish();
            Verify(attempts.Count == hiddenCount + 1 && Find<Button>("PrepareButton").IsVisible && Find<Button>("PrepareButton").IsEnabled &&
                !((bool)KeyboardField(panel, "_busy")) && Find<TextBlock>("PromptTitle").Text == LocalizationService.Get("DeviceHubPanelConnectFailed"),
                "A failed explicit connection must offer a retry with useful guidance.");
            Tick(); Verify(attempts.Count == hiddenCount + 1, "A failed startup must not repeat on every observer tick.");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture); Drain();
                Verify(Find<Button>("PrepareButton").Content.ToString() == LocalizationService.Get("DeviceHubPanelRetryConnection"), "Retry must be localized.");
                SaveWindowRender(window, Path.Combine(output, "retry-" + culture + ".png"));
            }
            LocalizationService.SetLanguage("zh-CN"); behavior = _ => Task.CompletedTask;
            Find<Button>("PrepareButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Finish();
            Verify(ready.Contains(third.Udid) && Find<Grid>("Pages").IsEnabled && !Find<Button>("PrepareButton").IsVisible,
                "Retry must reconnect and load the page without another manual step.");

            ready.Remove(third.Udid); third.UpdateFrom(Device(third.Udid, ConnectionState.Disconnected)); Drain();
            count = attempts.Count; Tick();
            Verify(attempts.Count == count, "An offline device must not initiate preparation.");
            third.UpdateFrom(Device(third.Udid)); Finish();
            Verify(attempts.Count == count && !Find<Grid>("Pages").IsEnabled,
                "An available device must still wait for a user-initiated connection.");
            var headerMore = Find<Button>("HeaderMore");
            headerMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reconnect = headerMore.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "prepare");
            reconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish();
            Verify(attempts.Count == count + 1 && Find<Grid>("Pages").IsEnabled,
                "The header menu must allow explicitly reconnecting a retained list.");
            ready.Remove(second.Udid);
            count = attempts.Count; vm.SelectedDevice = second; vm.SelectedDevice = first; Finish();
            Verify(attempts.Count == count, "A queued connection must recheck the latest selection before dispatch.");
            var media = DeviceViewModel.CreateMediaCast(); vm.Devices.Add(media); vm.SelectedDevice = media; Finish();
            Verify(attempts.Count == count && !Find<Button>("PrepareButton").IsVisible, "A generic media receiver must not initiate USB preparation.");
            Open(false);

            // A panel joining an existing manual startup only owns its wait.
            var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", first.Udid)!;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startup = control.WiredOperation.RunAsync(_ => release.Task);
            var failedWireless = control.WirelessOperation.RunAsync(_ => Task.FromException(new IOException("Previous startup failed.")));
            Verify(failedWireless.IsFaulted && failedWireless.Exception is not null, "The old transport failure is part of the fixture.");
            control.Starting = true; control.RequestedWireless = false;
            using var cancellation = new CancellationTokenSource();
            var waiting = vm.PrepareDeviceHubConnectionAsync(first, cancellation.Token); Drain();
            Verify(!waiting.IsCompleted, "The panel must join an already running startup.");
            cancellation.Cancel();
            try { WaitReviewTask(waiting); throw new InvalidOperationException("Panel wait ignored cancellation."); }
            catch (OperationCanceledException) { }
            Verify(!startup.IsCompleted, "Cancelling the panel must not stop an existing manual startup.");
            release.SetResult(); WaitReviewTask(startup); control.Starting = false;

            // Finish the transport before the UI continuation observes cancellation.
            var completedBeforeClose = new TaskCompletionSource();
            startup = control.WiredOperation.RunAsync(_ => completedBeforeClose.Task);
            using var closing = new CancellationTokenSource();
            var preparation = vm.StartAutomationControlAsync(first.Udid, false, closing.Token);
            Verify(!preparation.IsCompleted, "The explicit connection must own its startup wait.");
            control.WiredEnabled = control.WiredConnected = true;
            completedBeforeClose.SetResult();
            Verify(startup.IsCompletedSuccessfully, "The transport must finish before the panel closes.");
            closing.Cancel();
            try { WaitReviewTask(preparation); throw new InvalidOperationException("The closed panel continued waiting."); }
            catch (OperationCanceledException) { }
            Verify(control.WiredEnabled && control.WiredConnected, "Closing after startup must preserve the established connection.");
            control.WiredEnabled = control.WiredConnected = false;

            VerifyDeviceHubRoute(vm, first, second, output);
            Console.WriteLine("PASS device management connection: explicit connection and retry, ready reuse, stale work, startup ownership and wired/wireless route revocation.");
        }
        finally
        {
            delayed?.TrySetResult(); panel.Dispose(); window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
        return 0;
    }

    private static void VerifyDeviceHubRoute(MainViewModel vm, DeviceViewModel wired, DeviceViewModel wireless, string output)
    {
        var bindings = new DeviceBindingManager(Path.Combine(output, $"route-test-{Guid.NewGuid():N}.json"));
        var resolver = new DeviceIdentityResolver(bindings);
        var original = KeyboardField(vm, "_identityResolver");
        SetKeyboardField(vm, "_identityResolver", resolver);
        try
        {
            var profile = bindings.CreateProfileFromIdentity(wired.Name, DeviceIdentityType.Wired, wired.Udid, null).Profile!;
            Verify(bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, wireless.Udid, wireless.Name, null, true).Success,
                "The fixture needs a valid wireless binding.");
            foreach (var device in new[] { wired, wireless })
            {
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                using var host = new ExplorerReadyBridge(control);
                if (device.IsWireless)
                {
                    control.WiredBridge = null;
                    control.WiredEnabled = control.WiredConnected = false;
                    control.WirelessBridge = host.Host;
                    control.WirelessEnabled = control.WirelessConnected = true;
                }
                try
                {
                    control.Binding = resolver.ResolveControlBinding(device,
                        device.IsWireless ? ReverseControlMode.Wireless : ReverseControlMode.Usb);
                    using var bytes = new MemoryStream();
                    using var writer = new StreamWriter(bytes, leaveOpen: true);
                    SetKeyboardField(host.Inner, "_stdin", writer);
                    var gate = (SemaphoreSlim)KeyboardField(host.Inner, "_sendLock");
                    Verify(vm.IsDeviceHubReady(device), $"The {device.Udid} route must be ready while its binding matches.");
                    gate.Wait();
                    Task<JsonElement> pending;
                    try
                    {
                        pending = device.IsWireless
                            ? vm.DeviceHubUiAsync(device, "launch", new { bundleId = "com.fixture.app" }, default)
                            : vm.DeviceHubUiAsync(device, "apps", null, default);
                        Verify(!pending.IsCompleted && bytes.Length == 0, "A management request must wait for the bridge writer.");
                        Verify(bindings.Unbind(profile.Id, DeviceIdentityType.Wired), "The fixture must revoke the captured target.");
                        Verify(!vm.IsDeviceHubReady(device), "A stale binding must immediately invalidate management readiness.");
                    }
                    finally { gate.Release(); }
                    try { WaitReviewTask(pending); throw new InvalidOperationException("The stale request reached the bridge."); }
                    catch (OperationCanceledException) { }
                    Verify(bytes.Length == 0 && host.Host.IsReady,
                        "Revoking a queued request must send neither an operation nor a cancellation and leave the bridge open.");
                    Verify(bindings.Bind(profile.Id, DeviceIdentityType.Wired, wired.Udid, wired.Name, null, true).Success,
                        "The fixture must restore the original binding for the next scenario.");
                    SetKeyboardField(host.Inner, "_stdin", null);
                }
                finally
                {
                    control.Binding = null;
                    control.WirelessBridge = null;
                    control.WirelessEnabled = control.WirelessConnected = false;
                }
            }
        }
        finally { SetKeyboardField(vm, "_identityResolver", original); }
    }
}
