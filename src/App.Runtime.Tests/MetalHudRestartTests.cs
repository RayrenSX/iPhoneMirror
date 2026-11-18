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
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMetalHudRestartTests(string output)
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
        var calls = new List<(string Device, string Action, string Bundle)>();
        var ready = true;
        var failRestart = false;
        TaskCompletionSource<JsonElement>? pending = null;
        async Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken ct)
        {
            if (action is "launch" or "restart_with_metal_hud")
            {
                var arguments = JsonSerializer.SerializeToElement(args);
                Verify(arguments.EnumerateObject().Count() == 1, "App launch must expose only the selected bundle ID.");
                calls.Add((device.Udid, action, arguments.GetProperty("bundleId").GetString()!));
                if (action == "restart_with_metal_hud")
                {
                    if (failRestart) throw new IOException("Fixture launch failure.");
                    if (pending is not null) return await pending.Task;
                }
            }
            ct.ThrowIfCancellationRequested();
            return action switch
            {
                "apps" => JsonSerializer.SerializeToElement(new { apps = new[] {
                    new { bundleId = "com.fixture.game", name = "Metal Game", version = "1.0", isFirstParty = false, isDeveloper = false, isAppClip = false },
                    new { bundleId = "com.fixture.other", name = "Other App", version = "1.0", isFirstParty = false, isDeveloper = false, isAppClip = false } } }),
                "get_app_icon" => JsonSerializer.SerializeToElement(new { data = HubFixtureIcon("com.fixture.game") }),
                "list_processes" => JsonSerializer.SerializeToElement(new { processes = new[] { new { processIdentifier = 251, name = "Metal Game" } } }),
                _ => JsonSerializer.SerializeToElement(new { status = "launched" })
            };
        }
        using var panel = new DeviceHubPanel(vm, Request, _ => ready, Path.Combine(output, "icons"), (_, _) => Task.CompletedTask);
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = "Metal HUD iPhone", ProductType = "iPhone17,3", OsVersion = "18.6", ConnectionType = "USB", State = ConnectionState.Ready });
        var first = Device("metal-hud-first"); var second = Device("metal-hud-second");
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        var list = Find<ListBox>("AppsList");
        void Finish()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            WaitReviewTask((Task)KeyboardField(panel, "_operation"));
            FinishWorkspaceReveal(window); window.UpdateLayout();
        }
        void Select(string id) => list.SelectedItem = list.Items.Cast<object>().Single(row =>
            row.GetType().GetProperty("Id")!.GetValue(row)?.ToString() == id);
        ContextMenu Menu() => (ContextMenu)KeyboardCall(panel, "PopulateListMenu", list)!;
        MenuItem RestartItem() => Menu().Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "app-metal-hud-restart");
        void Restart() { RestartItem().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish(); }
        try
        {
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            guidance.Add(first.Udid); guidance.Add(second.Udid);
            vm.Devices.Add(first); vm.Devices.Add(second); vm.SelectedDevice = first;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            Select("com.fixture.game");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(AppTheme.Dark); Finish();
                Verify(RestartItem().Header?.ToString() == LocalizationService.Get("DeviceHubPanelRestartMetalHud"),
                    "The Metal HUD restart menu must follow the selected language.");
                if (culture == "zh-CN") Verify(RestartItem().Header?.ToString() == "以 Metal 性能 HUD 重启", "The requested Chinese menu label must match exactly.");
                Exception? promptError = null;
                var shown = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                timer.Tick += (_, _) =>
                {
                    var prompt = app.Windows.OfType<AppPromptWindow>().SingleOrDefault();
                    if (prompt is null) return;
                    timer.Stop(); shown = true;
                    try
                    {
                        Verify(prompt.CancelIsDefault && prompt.PromptBody.Contains("Metal Game") && prompt.PromptBody.Contains("com.fixture.game") &&
                            prompt.PromptBody.Contains(LocalizationService.Get("DeviceHubPanelRestartMetalHudWarning")) &&
                            prompt.ConfirmText == LocalizationService.Get("DeviceHubPanelRestartMetalHud"),
                            "Restart must explain the closed application and HUD support, identify the exact target, and default to Cancel.");
                        SaveWindowRender(prompt, Path.Combine(output, "metal-hud-confirm-" + culture + ".png"));
                    }
                    catch (Exception error) { promptError = error; }
                    finally { KeyboardCall(prompt, "OnCancelClick", prompt, new RoutedEventArgs()); }
                };
                timer.Start(); Restart(); timer.Stop();
                if (promptError is not null) throw promptError;
                Verify(shown && calls.Count == 0, "Cancelling the HUD confirmation must leave the app running.");
            }
            LocalizationService.SetLanguage("zh-CN");
            panel.ConfirmMetalHudRestart = (owner, body) =>
            {
                Verify(owner == window && body.Contains("com.fixture.game"), "Restart must confirm the captured app.");
                Select("com.fixture.other"); return true;
            };
            Restart();
            Verify(calls.SequenceEqual(new[] { (first.Udid, "restart_with_metal_hud", "com.fixture.game") }) &&
                Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelMetalHudRestarted", "Metal Game"),
                "Confirmed restart must preserve the original target and report a request rather than verified HUD visibility.");
            var confirmations = 0;
            panel.ConfirmMetalHudRestart = (_, _) => { confirmations++; return true; };
            var open = Menu().Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "app-launch");
            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish();
            Verify(calls.Last() == (first.Udid, "launch", "com.fixture.other") && confirmations == 0,
                "Ordinary Open must remain independent from Metal HUD restart.");
            Select("com.fixture.game");
            var stale = RestartItem();
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = KeyboardCall(panel, "InvokeActionAsync", "app-metal-hud-restart"); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            var count = calls.Count;
            Verify(!RestartItem().IsEnabled && (bool)KeyboardField(panel, "_busy"), "A pending HUD restart must disable duplicate actions.");
            _ = KeyboardCall(panel, "InvokeActionAsync", "app-metal-hud-restart");
            Verify(calls.Count == count, "A pending HUD restart cannot be dispatched twice.");
            vm.SelectedDevice = second;
            pending.SetResult(JsonSerializer.SerializeToElement(new { status = "launched" })); Finish(); pending = null;
            Verify(Find<TextBlock>("StatusText").Text != LocalizationService.Format("DeviceHubPanelMetalHudRestarted", "Metal Game"),
                "A late launch response must not report success on another device.");
            stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish();
            Verify(calls.Count == count, "An old menu must never restart an app on another device.");
            Select("com.fixture.game"); ready = false; panel.SetDevice(second, true); Finish();
            Verify(!RestartItem().IsEnabled, "A disconnected device must disable HUD restart.");
            ready = true; panel.SetDevice(second, true); Finish(); Select("com.fixture.game");
            panel.ConfirmMetalHudRestart = (_, _) => { vm.SelectedDevice = first; return true; };
            Restart(); Verify(calls.Count == count, "Changing devices during confirmation must cancel the restart.");
            panel.ConfirmMetalHudRestart = (_, _) => true; Select("com.fixture.game");
            failRestart = true; Restart();
            Verify(Find<TextBlock>("StatusText").Text == LocalizationService.Get("DeviceHubPanelOperationFailed"),
                "A rejected restart must show an error instead of claiming HUD success.");
            failRestart = false;
            var filter = Find<ComboBox>("AppFilter");
            filter.SelectedIndex = 5; Finish(); list.SelectedIndex = 0;
            Verify(!Menu().Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-metal-hud-restart"),
                "Process rows cannot offer an application HUD restart.");
            count = calls.Count;
            _ = KeyboardCall(panel, "InvokeActionAsync", "app-metal-hud-restart"); Finish();
            Verify(calls.Count == count, "A direct HUD action cannot treat a PID as a bundle ID.");
            filter.SelectedIndex = 0; Finish(); list.SelectedItem = null;
            Verify(!Menu().Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-metal-hud-restart"),
                "Blank-space menus cannot reuse an earlier HUD target.");
            Console.WriteLine("PASS Metal HUD UI: normal app mode, five languages, real confirmation/cancel, exact target, ordinary Open, duplicate admission, disconnects, stale menus/responses, failures and process isolation.");
        }
        finally
        {
            pending?.TrySetCanceled(); panel.Dispose(); CloseWorkspaceTestWindow(window);
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
        return 0;
    }
}
