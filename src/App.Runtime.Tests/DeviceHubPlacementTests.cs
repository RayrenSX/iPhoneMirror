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
        // This comparison requires room for both sidebars at enlarged text
        // sizes. The separate fullscreen matrix exercises the narrow secondary
        // display with its actual DPI instead of inheriting the pointer's screen.
        window.Left = SystemParameters.WorkArea.Left + 40;
        window.Top = SystemParameters.WorkArea.Top + 40;
        DrainDispatcher(); window.UpdateLayout();
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
        var ready = false;
        DeviceHubPanel Panel(bool developer) => new(vm, Request, _ => ready, Path.Combine(output, "icons"),
            (_, _) => Task.CompletedTask, developerMode: developer);
        using var basic = Panel(false);
        var hubHost = (ContentControl)window.FindName("DeviceHubPanelHost");
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
            VerifyHubClosesWithoutShowingSettings();
            SetKeyboardField(window, "_deviceHubPanel", basic);
            hubHost.Content = basic;
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            guidance.Add(first.Udid); guidance.Add(second.Udid); vm.Devices.Add(first); vm.Devices.Add(second); vm.SelectedDevice = first;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            VerifySidebarSwitching();
            if (ReviewCapture)
            {
                LocalizationService.SetLanguage("zh-CN");
                ThemeService.Apply(AppTheme.Dark);
                Finish();
                SaveWindowRender(window, Path.Combine(output, "main-connect-zh-CN-dark.png"));
                var origin = basic.TranslatePoint(new Point(), window);
                File.WriteAllText(Path.Combine(output, "main-connect-bounds.json"), JsonSerializer.Serialize(new
                {
                    origin.X, origin.Y, Width = basic.ActualWidth, Height = basic.ActualHeight
                }));
            }
            var connectButton = (Button)basic.FindName("PrepareButton");
            Verify(connectButton.IsVisible && connectButton.IsEnabled && calls.Count == 0,
                "The pending device panel must expose the connect action before initiating management.");
            var surface = (FrameworkElement)basic.FindName("HubSurface");
            var workspace = (FrameworkElement)window.FindName("CenterPanel");
            Verify(Math.Abs(surface.ActualHeight - workspace.ActualHeight) < 1 && basic.ActualWidth < 400,
                "The device management sidebar must fill the workspace while waiting for a connection.");
            var promptCard = (FrameworkElement)basic.FindName("ConnectionPromptCard");
            Verify(promptCard.ActualHeight < surface.ActualHeight / 2,
                "Connection guidance must size to its content inside the full-height sidebar.");
            var waitingHeight = surface.ActualHeight;
            Verify(((FrameworkElement)basic.FindName("DeviceSummary")).Visibility == Visibility.Collapsed,
                "The main inspector must not reserve a summary row before it has a connection status.");
            ready = true; basic.SetDevice(first, true); Finish();
            Verify(!connectButton.IsVisible && Math.Abs(surface.ActualHeight - waitingHeight) < 1,
                "Connected device lists must use the same full-height sidebar.");
            Verify(Tabs(basic).Where(tab => tab.IsVisible).Select(tab => tab.Tag!.ToString()).SequenceEqual(new[] { "1", "3" }),
                "The main inspector must show only Apps and Files.");
            Verify(((FrameworkElement)basic.FindName("DeviceIdentitySummary")).Visibility == Visibility.Collapsed &&
                basic.FindName("ConnectionText") is TextBlock { IsVisible: true } status &&
                status.Text == LocalizationService.Get("DeviceHubPanelConnected"),
                "The main inspector header must show only the connection status.");
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
            Verify(advanced.FindName("DeviceNameText") is TextBlock { IsVisible: true } deviceName &&
                deviceName.Text == second.Name &&
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

        void VerifyHubClosesWithoutShowingSettings()
        {
            var controlTab = (RadioButton)window.FindName("ControlSettingsTab");
            var settings = (FrameworkElement)window.FindName("ControlPanelContent");
            var sidebar = (FrameworkElement)window.FindName("ControlPanel");
            var nav = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("DeviceHubPanelToggle");
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            controlTab.IsChecked = true;
            FinishWorkspaceReveal(window);

            foreach (var useCloseButton in new[] { true, false })
            {
                nav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                FinishWorkspaceReveal(window);
                var hub = (DeviceHubPanel)hubHost.Content;
                Verify(hub.IsVisible && !settings.IsVisible, "Device management must replace settings on the right.");
                var settingsFlashed = false;
                void TrackSettings(object sender, DependencyPropertyChangedEventArgs args) => settingsFlashed |= settings.IsVisible;
                void VerifyClosingFrame()
                {
                    if (sidebar.IsVisible && sidebar.Width > 1)
                        Verify(hub.IsVisible && !settings.IsVisible && !sidebar.IsHitTestVisible,
                            "The outgoing sidebar must still show device management during the close animation.");
                }
                EventHandler sample = (_, _) => VerifyClosingFrame();
                settings.IsVisibleChanged += TrackSettings;
                try
                {
                    if (useCloseButton) ((Button)hub.FindName("CloseHubButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else nav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    VerifyClosingFrame();
                    Verify(!(bool)KeyboardField(hub, "_active"), "Closing the panel must immediately stop device management background work.");
                    window.LayoutUpdated += sample;
                    FinishWorkspaceReveal(window);
                }
                finally
                {
                    window.LayoutUpdated -= sample;
                    settings.IsVisibleChanged -= TrackSettings;
                }
                Verify(!settingsFlashed && !sidebar.IsVisible && hubHost.Visibility == Visibility.Collapsed,
                    "Closing device management must collapse the sidebar without flashing settings.");
            }

            ((DeviceHubPanel)hubHost.Content).Dispose();
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            FinishWorkspaceReveal(window);
            Verify(settings.IsVisible && controlTab.IsChecked == true, "Opening settings explicitly must restore its previous tab.");
            ((RadioButton)window.FindName("ProjectionSettingsTab")).IsChecked = true;
            ((Button)window.FindName("CloseSettingsPanelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FinishWorkspaceReveal(window);
            Verify(!((FrameworkElement)window.FindName("ControlPanel")).IsVisible, "The settings close button must still collapse the sidebar.");
            Console.WriteLine("PASS device management stays visible during its close animation and settings never flash.");
        }

        void VerifySidebarSwitching()
        {
            var left = (FrameworkElement)window.FindName("DevicePanel");
            var preview = (FrameworkElement)window.FindName("PreviewPanel");
            var settings = (FrameworkElement)window.FindName("ControlPanelContent");
            var settingsTitle = (FrameworkElement)window.FindName("SettingsPanelTitle");
            var settingsClose = (FrameworkElement)window.FindName("CloseSettingsPanelButton");
            var hubSurface = (FrameworkElement)basic.FindName("HubSurface");
            var hubTitle = (FrameworkElement)basic.FindName("HubTitle");
            var hubClose = (FrameworkElement)basic.FindName("CloseHubButton");
            var panels = KeyboardField(window, "_leftWorkspacePanel").GetType();
            void Left(string panel) => KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panels, panel));
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            LocalizationService.SetLanguage("zh-CN");
            ThemeService.Apply(AppTheme.Dark);
            Left("Devices"); Finish();
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, scale, false);
                Finish();
                var widthFactor = 1 + .5 * (scale - 1);
                var leftWidth = WorkspaceRevealPanel.DefaultLeftContentWidth * widthFactor;
                var rightWidth = WorkspaceRevealPanel.DefaultRightContentWidth * widthFactor;
                var hubBounds = Bounds(hubSurface);
                var previewBounds = Bounds(preview);
                var hubTitleOffset = hubTitle.TranslatePoint(default, hubSurface);
                var hubCloseOffset = hubClose.TranslatePoint(default, hubSurface);
                Verify(Math.Abs(left.ActualWidth - leftWidth) < 1 && Math.Abs(hubBounds.Width - rightWidth) < 1,
                    $"Both sidebars must use their expected scaled widths at {scale:P0}.");
                var largestShift = 0d;
                EventHandler capture = (_, _) =>
                {
                    var bounds = Bounds(preview);
                    largestShift = Math.Max(largestShift, Math.Max(Math.Abs(bounds.Left - previewBounds.Left),
                        Math.Abs(bounds.Right - previewBounds.Right)));
                };
                window.LayoutUpdated += capture;
                try
                {
                    KeyboardCall(window, "SetSettingsPanelVisible", true); Finish();
                    Verify(settings.IsVisible && !hubSurface.IsVisible && Bounds(settings) == hubBounds,
                        $"Settings and Device Management must occupy the same sidebar at {scale:P0}.");
                    Verify((settingsTitle.TranslatePoint(default, settings) - hubTitleOffset).Length < 1 &&
                        (settingsClose.TranslatePoint(default, settings) - hubCloseOffset).Length < 1,
                        "Right sidebar titles and close buttons must align.");
                    if (scale == 1 && ReviewCapture)
                        SaveWindowRender(window, Path.Combine(output, "workspace-sources-settings.png"));
                    Left("Mirroring"); Finish();
                    Verify(Math.Abs(((FrameworkElement)window.FindName("MirroringPanel")).ActualWidth - leftWidth) < 1,
                        "Mirroring and sources must occupy the same left sidebar width.");
                    KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
                    if (scale == 1 && ReviewCapture)
                        SaveWindowRender(window, Path.Combine(output, "workspace-mirroring-device-management.png"));
                    Left("Devices"); Finish();
                }
                finally { window.LayoutUpdated -= capture; }
                Verify(largestShift < 1, $"Switching sidebar pages moved the preview by {largestShift:F2} DIP at {scale:P0}.");
            }
            IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1, false);
            Finish();
            Console.WriteLine("PASS sidebar widths, header alignment, and stable preview geometry at 100%, 150%, and 200% text size.");
        }
    }
}
