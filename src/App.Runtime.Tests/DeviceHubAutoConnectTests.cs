using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
            Verify(action == "apps" || action == "get_app_icon", "Automatic connection must not start capture, logs, or device mutations.");
            if (action == "get_app_icon") throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Fixture has no icon.");
            reads.Add(device.Udid);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            { apps = new[] { new { name = device.Name, bundleId = "com.fixture.app", type = "User", version = "1.0" } } }));
        }
        using var panel = new DeviceHubPanel(vm, Request, device => ready.Contains(device.Udid), Path.Combine(output, "icons"), Prepare);
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id, ConnectionState state = ConnectionState.Ready) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = id, ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = state });
        var first = Device("自动连接 iPhone A"); var second = Device("自动连接 iPhone B"); var third = Device("自动连接 iPhone C");
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        void Drain()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(45));
            FinishWorkspaceReveal(window); window.UpdateLayout();
        }
        void Finish() { Drain(); WaitReviewTask((Task)KeyboardField(panel, "_operation")); Drain(); }
        void Open(bool visible) { _ = KeyboardCall(window, "SetDeviceHubPanelVisible", visible); Drain(); }
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
            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            behavior = _ => delayed.Task; // Deliberately ignore cancellation to model a late response.
            Open(true);
            Verify(attempts.Count == 1 && attempts[0].Device == first &&
                Find<TextBlock>("ConnectionText").Text == LocalizationService.Get("DeviceHubPanelConnecting") &&
                !FindVisualDescendants<ProgressBar>(panel).Any() && !Find<Button>("PrepareButton").IsVisible,
                "Opening the inspector must connect automatically with a text status and no moving progress bar or prepare button.");
            Tick(); Verify(attempts.Count == 1, "Repeated selection notifications and polling must not duplicate a connection.");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(AppTheme.Dark); Drain();
                Verify(Find<TextBlock>("PromptTitle").Text == LocalizationService.Get("DeviceHubPanelConnectTitle") &&
                    Find<TextBlock>("ConnectionText").Text == LocalizationService.Get("DeviceHubPanelConnecting"), "Connecting guidance must follow the current language.");
                SaveWindowRender(window, Path.Combine(output, "connecting-" + culture + ".png"));
            }
            LocalizationService.SetLanguage("zh-CN");
            delayed.SetResult(); Finish(); delayed = null;
            Verify(Find<Grid>("Pages").IsEnabled && Find<ListBox>("AppsList").Items.Count == 1 && reads.Contains(first.Udid),
                "A successful connection must automatically load the selected page.");
            var count = attempts.Count;
            Open(false); Open(true); Finish();
            Verify(attempts.Count == count, "Reopening a ready device must reuse its existing connection.");

            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously); behavior = _ => delayed.Task;
            vm.SelectedDevice = second; Drain();
            var secondAttempt = attempts[^1];
            Verify(attempts.Count == count + 1 && secondAttempt.Device == second, "Selecting another device must automatically connect that target.");
            vm.SelectedDevice = first; Drain();
            Verify(secondAttempt.Token.IsCancellationRequested, "Changing selection must cancel the previous connection scope.");
            delayed.SetResult(); Finish(); delayed = null;
            Verify(!reads.Contains(second.Udid) && Find<TextBlock>("DeviceNameText").Text == first.Name && Find<Grid>("Pages").IsEnabled,
                "A late connection response must not read or populate another device.");

            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously); behavior = token => delayed.Task.WaitAsync(token);
            vm.SelectedDevice = third; Drain(); var hiddenAttempt = attempts[^1];
            Open(false); Finish();
            Verify(hiddenAttempt.Token.IsCancellationRequested && !ready.Contains(third.Udid), "Closing the panel must cancel its own startup.");
            var hiddenCount = attempts.Count; Drain();
            Verify(attempts.Count == hiddenCount, "A hidden panel must not retry a cancelled connection.");
            delayed.SetResult(); delayed = null;
            behavior = _ => throw new AutomationException("DEVICE_HUB_CONNECTION_FAILED", "Fixture requires device trust.");
            Open(true); Finish();
            Verify(attempts.Count == hiddenCount + 1 && Find<Button>("PrepareButton").IsVisible && Find<Button>("PrepareButton").IsEnabled &&
                !((bool)KeyboardField(panel, "_busy")) && Find<TextBlock>("PromptTitle").Text == LocalizationService.Get("DeviceHubPanelConnectFailed"),
                "Reopening must try once; a failure must offer an explicit retry with useful guidance.");
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
            Verify(attempts.Count == count + 1 && Find<Grid>("Pages").IsEnabled, "A device that becomes available again must reconnect automatically.");
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
            control.Starting = true; control.RequestedWireless = false;
            using var cancellation = new CancellationTokenSource();
            var waiting = vm.PrepareDeviceHubConnectionAsync(first, cancellation.Token); Drain();
            Verify(!waiting.IsCompleted, "The panel must join an already running startup.");
            cancellation.Cancel();
            try { WaitReviewTask(waiting); throw new InvalidOperationException("Panel wait ignored cancellation."); }
            catch (OperationCanceledException) { }
            Verify(!startup.IsCompleted, "Cancelling the panel must not stop an existing manual startup.");
            release.SetResult(); WaitReviewTask(startup); control.Starting = false;
            Console.WriteLine("PASS automatic management connection: selection, visibility, ready reuse, failure/retry, availability, five languages, stale work and shared startup cancellation.");
        }
        finally
        {
            delayed?.TrySetResult(); panel.Dispose(); window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
        return 0;
    }
}
