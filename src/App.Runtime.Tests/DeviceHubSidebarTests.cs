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

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceHubSidebarTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var language = LocalizationService.SelectedLanguage;
        var theme = ThemeService.Preference;
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var calls = new List<(string Device, string Action)>();
        var settingValues = new Dictionary<string, JsonElement>
        {
            ["showBorders"] = JsonSerializer.SerializeToElement(new { showBorders = new { enabled = true } }),
            ["appearance"] = JsonSerializer.SerializeToElement(new { style = "dark" }),
            ["textSize"] = JsonSerializer.SerializeToElement(new { textSize = new { size = new { large = new { } } } })
        };
        TaskCompletionSource<JsonElement>? delayed = null;
        bool ready = true;
        async Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken ct)
        {
            if (action != "get_app_icon") Console.WriteLine("Request: " + device.Udid + " / " + action);
            calls.Add((device.Udid, action));
            if (action == "apps" && delayed is not null)
            {
                // Deliberately ignore cancellation, as a late external response might.
                var pending = delayed; delayed = null; return await pending.Task;
            }
            ct.ThrowIfCancellationRequested();
            var arguments = JsonSerializer.SerializeToElement(args);
            if (action == "set_setting")
            {
                var key = arguments.GetProperty("key").GetString()!;
                settingValues[key] = JsonSerializer.SerializeToElement(new { enabled = arguments.GetProperty("value").GetBoolean() });
            }
            return action switch
            {
                "apps" => HubFixtureApps(device.Udid),
                "get_app_icon" => JsonSerializer.SerializeToElement(new { data = HubFixtureIcon(arguments.GetProperty("bundleId").GetString()!) }),
                "list_processes" => JsonSerializer.SerializeToElement(new { processes = new[] { new { processIdentifier = 251, executableURL = new { relative = "/Applications/WeChat.app/WeChat" } } } }),
                "get_settings" => JsonSerializer.SerializeToElement(new { settings = settingValues.ToDictionary(pair => pair.Key, pair => new { available = true, value = pair.Value }) }),
                "get_setting" => JsonSerializer.SerializeToElement(new { value = settingValues[arguments.GetProperty("key").GetString()!] }),
                "media_status" => JsonSerializer.SerializeToElement(new { running = true, decodedFrames = 1284, audioFrames = 2218, packetGaps = 0, droppedFrames = 0, queueDrops = 0, waitingForKeyframe = false, recording = false }),
                "device_info" => JsonSerializer.SerializeToElement(new
                {
                    device = new { available = true, values = new { DeviceName = device.DisplayName, ProductType = "iPhone17,3", ProductVersion = "18.6", BuildVersion = "22G86", SerialNumber = "TEST-SERIAL", UniqueDeviceID = device.Udid } },
                    battery = new { available = true, values = new { BatteryCurrentCapacity = 86, BatteryIsCharging = true } },
                    disk = new { available = true, values = new { TotalDiskCapacity = 256L * 1024 * 1024 * 1024, TotalDataAvailable = 92L * 1024 * 1024 * 1024 } }
                }),
                "list_profiles" => JsonSerializer.SerializeToElement(new { profiles = new[] { new { id = "fixture-profile", name = "iPhoneMirror Development", kind = "provisioning", organization = "Development Team", expires = "2027-11-15" } }, errors = new { } }),
                "list_dir" => JsonSerializer.SerializeToElement(new { entries = new[] {
                    new { name = "DCIM", path = "DCIM", type = "directory", size = 0 },
                    new { name = "IMG_1024.JPG", path = "IMG_1024.JPG", type = "file", size = 4821320 },
                    new { name = "device-report.ips", path = "device-report.ips", type = "file", size = 172410 }
                }, nextOffset = (int?)null, total = 3 }),
                "syslog_start" => JsonSerializer.SerializeToElement(new { streamId = "fixture-stream" }),
                "syslog_poll" => JsonSerializer.SerializeToElement(new { lines = new[] { new { text = "14:32:18 SpringBoard: Application launch request received" }, new { text = "14:32:18 runningboardd: Process started successfully" } }, cursor = 2L, running = true, dropped = 0 }),
                "launch" => JsonSerializer.SerializeToElement(new { success = true }),
                _ => JsonSerializer.SerializeToElement(new { success = true })
            };
        }
        var panel = new DeviceHubPanel(vm, Request, _ => ready);
        Console.WriteLine("Device manager constructed.");
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id, string name) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = name, ProductType = "iPhone17,3", OsVersion = "18.6", ConnectionType = "USB", State = ConnectionState.Ready });
        var first = Device("hub-ui-first", "Ray 的 iPhone");
        var second = Device("hub-ui-second", "测试 iPhone");
        var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
        guidance.Add(first.Udid); guidance.Add(second.Udid);
        void Finish()
        {
            WaitReviewTask((Task)KeyboardField(panel, "_operation"));
            FinishWorkspaceReveal(window); AdvanceDispatcher(TimeSpan.FromMilliseconds(60)); window.UpdateLayout();
        }
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        void Select(int page)
        {
            ((RadioButton)((System.Windows.Controls.Primitives.UniformGrid)panel.FindName("PageTabs")).Children[page]).IsChecked = true;
            Finish();
        }
        void ClickAction(string action)
        {
            FindVisualDescendants<Button>(panel).Single(button => button.Tag?.ToString() == action)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
        }
        try
        {
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            Console.WriteLine("No-device navigation passed.");
            Verify(Find<StackPanel>("ConnectionPrompt").IsVisible && calls.Count == 0, "Opening without a device must show guidance and must not start control/capture.");
            Console.WriteLine("Adding first device."); vm.Devices.Add(first);
            Console.WriteLine("Adding second device."); vm.Devices.Add(second);
            Console.WriteLine("Selecting first device."); vm.SelectedDevice = first;
            Console.WriteLine("Finishing first refresh: " + ((Task)KeyboardField(panel, "_operation")).Status); Finish();
            Console.WriteLine("First device loaded.");
            Verify(calls.Any(c => c == (first.Udid, "apps")), "Panel must follow the main selection and load apps.");
            Verify(!calls.Any(c => c.Action == "media_start"), "Opening management must not change the capture source.");
            var list = Find<ListBox>("AppsList");
            Find<TextBox>("AppSearch").Text = "WeChat"; window.UpdateLayout();
            Verify(list.Items.Count == 1, "App search must match names and bundle IDs.");
            list.SelectedIndex = 0;
            Find<Button>("AppLaunch").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            Console.WriteLine("App launch passed.");
            Verify(calls.Last(c => c.Action == "launch").Device == first.Udid, "Launch must target the selected main device.");
            Find<TextBox>("AppSearch").Clear();
            KeyboardCall(window, "SetSettingsPanelVisible", true); Finish();
            Verify(!((bool)KeyboardField(window, "_isDeviceHubPanelVisible")) && ((Border)window.FindName("ControlPanelContent")).IsVisible && !panel.IsVisible,
                "Settings and device management must share exactly one right card.");
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            Console.WriteLine("Right card navigation passed.");
            Verify(!((bool)KeyboardField(window, "_isSettingsPanelVisible")) && panel.IsVisible, "Opening management must replace settings in the right card.");

            var slow = delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldOperation = (Task)KeyboardCall(panel, "RefreshAsync")!;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            vm.SelectedDevice = second;
            slow.SetResult(HubFixtureApps("stale-device"));
            WaitReviewTask(oldOperation); Finish();
            Console.WriteLine("Stale-response protection passed.");
            Verify(Find<TextBlock>("DeviceNameText").Text == second.DisplayName, "Device name must follow selection.");
            Verify(list.Items.Cast<object>().All(row => ((JsonElement)row.GetType().GetProperty("Data")!.GetValue(row)!).GetProperty("source").GetString() == second.Udid),
                "A stale response must not appear on another device.");
            Verify(calls.Last(c => c.Action == "apps").Device == second.Udid, "The new device must get its own refresh after cancellation.");

            var appFilter = Find<ComboBox>("AppFilter");
            appFilter.SelectedIndex = 5; Finish();
            Verify(list.Items.Count == 1 && !Find<Button>("AppLaunch").IsEnabled, "Processes must be reachable in Apps with a separate terminate command.");
            appFilter.SelectedIndex = 0; Finish();

            Select(5); ClickAction("settings-read");
            var settings = (System.Collections.IDictionary)KeyboardField(panel, "_settings");
            var borderEditor = settings["showBorders"]!;
            var borderCheck = (CheckBox)borderEditor.GetType().GetProperty("Input")!.GetValue(borderEditor)!;
            Verify(borderCheck.IsChecked == true, "Phone settings must display the actual nested CoreDevice value.");
            borderCheck.IsChecked = false; ClickAction("setting:showBorders");
            Verify(settingValues["showBorders"].GetProperty("enabled").GetBoolean() == false && borderCheck.IsChecked == false,
                "A setting change must be applied and re-read on the same target.");
            ClickAction("media-status");
            Verify(FindVisualDescendants<TextBox>((StackPanel)KeyboardField(panel, "_mediaDetails")).Any(text => text.Text == "1284"),
                "Media status must show counters as readable fields.");
            ((TextBox)KeyboardField(panel, "_latitude")).Text = "91";
            var locationCalls = calls.Count(c => c.Action == "set_location"); ClickAction("location-set");
            Verify(calls.Count(c => c.Action == "set_location") == locationCalls, "Invalid coordinates must not reach the device.");

            Select(4);
            Find<Button>("LogStart").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            KeyboardCall(panel, "OnLogTick", null, EventArgs.Empty); Finish();
            Verify(Find<TextBox>("LogText").Text.Contains("SpringBoard"), "Console must render real log messages.");
            Find<TextBox>("LogSearch").Text = "runningboardd";
            Verify(!Find<TextBox>("LogText").Text.Contains("SpringBoard"), "Console filter must filter messages.");
            Select(1); AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            Console.WriteLine("Console passed.");
            Verify(calls.Any(c => c.Action == "syslog_stop" && c.Device == second.Udid), "Leaving the console must stop the original device's log stream.");

            slow = delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            oldOperation = (Task)KeyboardCall(panel, "RefreshAsync")!;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            KeyboardCall(window, "SetDeviceHubPanelVisible", false);
            slow.SetResult(HubFixtureApps("hidden-stale-device")); WaitReviewTask(oldOperation);
            Verify(!((bool)KeyboardField(panel, "_busy")), "Closing the panel must release the busy state.");
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();

            ready = false; panel.SetDevice(second, true); Finish();
            Verify(Find<Button>("PrepareButton").IsVisible && !Find<Grid>("Pages").IsVisible, "An unprepared connection needs one explicit prepare action.");
            SaveWindowRender(window, Path.Combine(output, "connection-guidance.png"));
            ready = true; panel.SetDevice(second, true); Finish();
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            foreach (var dark in new[] { false, true })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(dark ? AppTheme.Dark : AppTheme.Light);
                Console.WriteLine($"Rendering {culture}, dark={dark}.");
                window.Width = 1440; window.Height = 900; Finish();
                for (var page = 0; page < 6; page++)
                {
                    Select(page);
                    Verify(panel.ActualWidth >= 400, "The inspector must retain a usable width.");
                    var preview = (Border)window.FindName("PreviewPanel");
                    var origin = panel.TranslatePoint(new Point(), window); var end = preview.TranslatePoint(new Point(preview.ActualWidth, 0), window);
                    Verify(origin.X >= end.X - 1, "Inspector must not overlap the projection.");
                    foreach (RadioButton tab in ((System.Windows.Controls.Primitives.UniformGrid)panel.FindName("PageTabs")).Children)
                        Verify(tab.IsVisible && tab.ActualWidth > 40, "All six management tabs must remain visible.");
                    if (culture is "zh-CN" or "en-US" || page == 1)
                        SaveWindowRender(window, Path.Combine(output, $"{culture}-{(dark ? "dark" : "light")}-{page}.png"));
                }
                window.Width = 880; window.Height = 680; Finish(); Select(1);
                SaveWindowRender(window, Path.Combine(output, $"{culture}-{(dark ? "dark" : "light")}-compact.png"));
            }
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Lightweight);
            KeyboardCall(window, "ApplyApplicationDisplayMode"); Finish();
            Verify(panel.IsVisible && panel.ActualWidth > 400, "Management must also work in lightweight mode.");
            vm.Devices.Clear(); vm.SelectedDevice = null; Finish();
            Verify(Find<ListBox>("AppsList").Items.Count == 0, "Deselecting the phone must clear its private data from the inspector.");
        }
        catch (Exception error) { Console.Error.WriteLine("Device manager test failed: " + error); throw; }
        finally
        {
            panel.Dispose(); LocalizationService.SetLanguage(language); ThemeService.Apply(theme);
            CloseWorkspaceTestWindow(window); app.Shutdown();
        }
        Console.WriteLine("Integrated device manager passed: navigation, device targeting, stale-response cancellation, app search/launch, console cleanup, five languages, two themes, six pages and compact/lightweight layout.");
        return 0;
    }

    private static JsonElement HubFixtureApps(string source) => JsonSerializer.SerializeToElement(new
    {
        apps = new[] { ("WeChat", "com.tencent.xin"), ("Safari", "com.apple.mobilesafari"), ("YouTube", "com.google.ios.youtube"), ("App Store", "com.apple.AppStore"),
            ("Music", "com.apple.Music"), ("Booking.com", "com.booking.BookingApp"), ("Skype", "com.skype.skype"), ("Calculator", "com.apple.calculator"),
            ("NewsBreak", "com.particlenews.newsbreak"), ("Files", "com.apple.DocumentsApp"), ("Maps", "com.apple.Maps"), ("Camera", "com.apple.camera") }
            .Select(a => new { bundleId = a.Item2, name = a.Item1, version = "1.0", isFirstParty = a.Item2.StartsWith("com.apple"), isDeveloper = false, isAppClip = false, source })
    });

    private static string HubFixtureIcon(string id)
    {
        var palette = new[] { Color.FromRgb(38, 165, 83), Color.FromRgb(35, 132, 234), Color.FromRgb(235, 69, 69), Color.FromRgb(135, 88, 212) };
        var color = palette[Math.Abs(id.GetHashCode()) % palette.Length];
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 64, 64), 14, 14);
            var label = new FormattedText(id.Split('.').Last()[..1].ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 32, Brushes.White, 1);
            dc.DrawText(label, new Point((64 - label.Width) / 2, (64 - label.Height) / 2));
        }
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return Convert.ToBase64String(stream.ToArray());
    }
}
