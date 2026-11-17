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
    private static int RunDeviceHubPlacementTests(string output)
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
        var calls = new List<(string Device, string Action)>();
        Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add((device.Udid, action));
            return Task.FromResult(action switch
            {
                "apps" => HubFixtureApps(device.Udid),
                "get_app_icon" => JsonSerializer.SerializeToElement(new { data = HubFixtureIcon("com.fixture.app") }),
                "device_info" => JsonSerializer.SerializeToElement(new { device = new { available = true, values = new { DeviceName = device.Name, ProductType = "iPhone13,1", ProductVersion = "18.7.8" } } }),
                "list_profiles" => JsonSerializer.SerializeToElement(new { profiles = Array.Empty<object>(), errors = new { } }),
                "list_dir" => JsonSerializer.SerializeToElement(new { entries = Array.Empty<object>(), nextOffset = (int?)null, total = 0 }),
                _ => JsonSerializer.SerializeToElement(new { })
            });
        }
        DeviceHubPanel Panel(bool developer) => new(vm, Request, _ => true, Path.Combine(output, "icons"),
            (_, _) => Task.CompletedTask, developerMode: developer);
        using var basic = Panel(false);
        SetKeyboardField(window, "_deviceHubPanel", basic);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = basic;
        DeviceViewModel Device(string id) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = id, ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = ConnectionState.Ready });
        var first = Device("Ray's iPhone"); var second = Device("Second iPhone");
        DeveloperToolsWindow? developer = null;
        DeviceHubPanel? advanced = null;
        RadioButton[] Tabs(DeviceHubPanel panel) => ((System.Windows.Controls.Primitives.UniformGrid)panel.FindName("PageTabs")).Children.Cast<RadioButton>().ToArray();
        ContextMenu Menu(DeviceHubPanel panel) => (ContextMenu)KeyboardCall(panel, "PopulateListMenu", (ListBox)panel.FindName("AppsList"))!;
        void Finish()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            WaitReviewTask((Task)KeyboardField(basic, "_operation"));
            if (advanced is not null) WaitReviewTask((Task)KeyboardField(advanced, "_operation"));
            FinishWorkspaceReveal(window); window.UpdateLayout(); developer?.UpdateLayout();
        }
        try
        {
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            guidance.Add(first.Udid); guidance.Add(second.Udid); vm.Devices.Add(first); vm.Devices.Add(second); vm.SelectedDevice = first;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            Verify(Tabs(basic).Where(tab => tab.IsVisible).Select(tab => tab.Tag!.ToString()).SequenceEqual(new[] { "1", "3" }),
                "The main inspector must show only Apps and Files.");
            Verify(((System.Windows.Controls.Primitives.UniformGrid)basic.FindName("PageTabs")).Columns == 2 &&
                !((Button)basic.FindName("HeaderMore")).IsVisible &&
                !Menu(basic).Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-install"),
                "The main inspector must not offer IPA installation in either menu.");
            var before = calls.Count(call => call.Action != "get_app_icon");
            _ = KeyboardCall(basic, "InvokeActionAsync", "app-install"); Finish();
            _ = KeyboardCall(basic, "InvokeActionAsync", "log-start"); Finish();
            Tabs(basic)[0].IsChecked = true; Finish();
            Verify(calls.Count(call => call.Action != "get_app_icon") == before && (int)KeyboardField(basic, "_page") == 1 && !((ScrollViewer)basic.FindName("InfoPage")).IsVisible,
                $"Hidden advanced pages and actions must not execute from the main inspector: before={before}, calls={string.Join(',', calls.Select(call => call.Action))}, page={KeyboardField(basic, "_page")}.");

            developer = new DeveloperToolsWindow(window);
            var created = 0;
            developer.CreateDeviceToolsPanel = () => { created++; return advanced = Panel(true); };
            developer.Show(); Finish();
            Verify(created == 0, "General developer tools must not create a device session until its page is opened.");
            KeyboardCall(window, "SetDeviceHubPanelVisible", false); Finish();
            var entry = FindVisualDescendants<Button>(developer).Single(button => button.Tag?.ToString() == "device-hub");
            entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Finish();
            Verify(created == 1 && advanced is { IsVisible: true } && !((bool)KeyboardField(window, "_isDeviceHubPanelVisible")),
                "The developer entry must open tools inside the developer window, without reopening the main inspector.");
            Verify(Tabs(advanced!).All(tab => tab.IsVisible), "All six management pages must remain available in developer tools.");
            Verify(Menu(advanced!).Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-install" && item.IsEnabled),
                "IPA installation must remain available in developer device tools.");
            ((Button)advanced!.FindName("HeaderMore")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Verify(((Button)advanced.FindName("HeaderMore")).ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-install"),
                "Developer tools need the IPA installation header menu as well as the row menu.");
            KeyboardCall(advanced, "CloseMenus");
            Tabs(advanced)[0].IsChecked = true; Finish();
            Verify(calls.Any(call => call.Device == first.Udid && call.Action == "device_info"), "Device information must load inside developer tools.");
            Tabs(advanced)[2].IsChecked = true; Finish();
            Verify(calls.Any(call => call.Device == first.Udid && call.Action == "list_profiles"), "Profiles must load inside developer tools.");
            Tabs(advanced)[4].IsChecked = true; Finish();
            Verify(((Button)advanced.FindName("LogStart")).IsVisible, "The logs page must remain usable inside developer tools.");
            Tabs(advanced)[0].IsChecked = true; vm.SelectedDevice = second; Finish();
            Verify(((TextBlock)advanced.FindName("DeviceNameText")).Text == second.Name &&
                calls.Any(call => call.Device == second.Udid && call.Action == "device_info"),
                "Developer tools must follow the device selected in the main window.");
            ((TabControl)developer.FindName("DeveloperPages")).SelectedIndex = 0; Finish();
            Verify(!((bool)KeyboardField(advanced, "_active")), "Leaving developer device tools must stop their background work.");
            developer.ShowDeviceTools(); Finish(); Verify(created == 1, "Reopening the page must reuse its panel.");

            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Tabs(basic)[1].IsChecked = true; Finish();
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            foreach (var dark in new[] { false, true })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(dark ? AppTheme.Dark : AppTheme.Light); Finish();
                Verify(Tabs(basic).Count(tab => tab.IsVisible) == 2 && Tabs(advanced).All(tab => tab.IsVisible),
                    "The basic and developer page split must survive language and theme changes.");
                Verify(Tabs(advanced).All(tab => tab.Style is not null && tab.Template is not null) &&
                    ((SolidColorBrush)advanced.Foreground).Color == ((SolidColorBrush)advanced.FindResource("TextBrush")).Color,
                    "The embedded device panel must carry its own tabs and theme text colors into the developer window.");
                if (culture is "zh-CN" or "en-US")
                {
                    SaveWindowRender(window, Path.Combine(output, $"main-{culture}-{dark}.png"));
                    SaveWindowRender(developer, Path.Combine(output, $"developer-{culture}-{dark}.png"));
                }
            }
            developer.Close(); Finish();
            Verify(((bool)KeyboardField(advanced, "_disposed")), "Closing developer tools must dispose their device panel.");
            Console.WriteLine("PASS device tool placement: main Apps/Files only; advanced pages and IPA install inside developer tools; selection, lifetime, five languages and two themes.");
        }
        finally
        {
            if (developer?.IsVisible == true) developer.Close();
            advanced?.Dispose(); basic.Dispose(); window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
        return 0;
    }
}
