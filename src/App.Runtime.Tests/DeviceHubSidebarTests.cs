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
        TaskCompletionSource<bool>? pauseNext = null;
        string? pauseAction = null;
        string? launchedBundle = null;
        bool ready = true;
        bool emptyProfiles = false, profileReadError = false, pagedFiles = false, failProcessRead = false, failFileRead = false;
        async Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken ct)
        {
            if (action != "get_app_icon") Console.WriteLine("Request: " + device.Udid + " / " + action);
            calls.Add((device.Udid, action));
            if (action == pauseAction && pauseNext is not null)
            {
                var pending = pauseNext; pauseNext = null; await pending.Task;
            }
            if (action == "apps" && delayed is not null)
            {
                // Deliberately ignore cancellation, as a late external response might.
                var pending = delayed; delayed = null; return await pending.Task;
            }
            ct.ThrowIfCancellationRequested();
            var arguments = JsonSerializer.SerializeToElement(args);
            if (action == "list_processes" && failProcessRead)
            { failProcessRead = false; throw new InvalidOperationException("Fixture process read failed."); }
            if (action == "list_dir")
            {
                if (failFileRead) { failFileRead = false; throw new InvalidOperationException("Fixture folder read failed."); }
                var path = arguments.GetProperty("path").GetString();
                if (path == "DCIM") return JsonSerializer.SerializeToElement(new { entries = Array.Empty<object>(), nextOffset = (int?)null, total = 0 });
                if (pagedFiles)
                {
                    var offset = arguments.GetProperty("offset").GetInt32();
                    return JsonSerializer.SerializeToElement(new { entries = (offset == 0 ? new[] { "first.jpg", "second.jpg" } : new[] { "third.jpg" })
                        .Select(name => new { name, path = name, type = "file", size = 120 }), nextOffset = offset == 0 ? (int?)2 : null, total = 3 });
                }
            }
            if (action == "launch") launchedBundle = arguments.GetProperty("bundleId").GetString();
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
                "device_info" => JsonSerializer.SerializeToElement(new
                {
                    device = new { available = true, values = new { DeviceName = device.DisplayName, ProductType = "iPhone17,3", ProductVersion = "18.6", BuildVersion = "22G86", SerialNumber = "TEST-SERIAL", UniqueDeviceID = device.Udid } },
                    battery = new { available = true, values = new { BatteryCurrentCapacity = 86, BatteryIsCharging = true } },
                    disk = new { available = true, values = new { TotalDiskCapacity = 256L * 1024 * 1024 * 1024, TotalDataAvailable = 92L * 1024 * 1024 * 1024 } }
                }),
                "list_profiles" => JsonSerializer.SerializeToElement(new { profiles = emptyProfiles ? Array.Empty<object>() : new object[] { new { id = "fixture-profile", name = "iPhoneMirror Development", kind = "provisioning", organization = "Development Team", expires = "2027-11-15" } },
                    errors = profileReadError ? new Dictionary<string, string> { ["configuration"] = "Fixture unavailable." } : new Dictionary<string, string>() }),
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
        string? copied = null;
        panel.CopyText = value => copied = value;
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
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
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
        ContextMenu Menu(ListBox list) => (ContextMenu)KeyboardCall(panel, "PopulateListMenu", list)!;
        MenuItem MenuAction(ContextMenu menu, string action) => menu.Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == action);
        void ClickMenu(ContextMenu menu, string action)
        {
            MenuAction(menu, action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish();
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
            // Starting QuickTime can temporarily downgrade normal usbmux discovery
            // even while the prepared CoreDevice management bridge remains ready.
            var populated = list.Items.Count;
            first.UpdateFrom(DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = first.Udid, Name = first.Name, ProductType = first.ProductType, OsVersion = first.OsVersion, ConnectionType = "USB", State = ConnectionState.Paired }));
            SetKeyboardField(vm, "_isCapturing", true);
            KeyboardCall(vm, "NotifyCaptureSessionChanged"); Finish();
            Verify(Find<Grid>("Pages").IsVisible && list.Items.Count == populated,
                "Starting projection must not hide a ready management bridge when discovery briefly reports Paired.");
            var readsBeforeRecovery = calls.Count(c => c.Device == first.Udid && c.Action == "apps");
            Find<TextBox>("AppSearch").Text = "WeChat"; list.SelectedIndex = 0;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                ready = false;
                KeyboardCall(vm, "NotifyUsbControlStateChanged"); Finish();
                Verify(Find<Grid>("Pages").IsVisible && !Find<Grid>("Pages").IsEnabled && list.Items.Count == 1,
                    "A capture transport reconnect must keep the selected device's filtered list visible and disable actions.");
                Verify(Find<TextBlock>("RecoveryHint").IsVisible && !Find<StackPanel>("ConnectionPrompt").IsVisible,
                    "A previously connected device must show recovery guidance instead of the first-connection prompt.");
                var requestsWhileOffline = calls.Count;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(550));
                Verify(calls.Count == requestsWhileOffline, "Recovery observation must never start a connection or request on an offline device.");
                ready = true;
                // No view-model notification: the bridge may restore readiness on
                // its worker after the last capture/control property notification.
                AdvanceDispatcher(TimeSpan.FromMilliseconds(600)); Finish();
                Verify(Find<Grid>("Pages").IsVisible && Find<Grid>("Pages").IsEnabled && list.Items.Count == 1,
                    "A recovered bridge must restore the current page without switching devices, including while native discovery remains Paired.");
                Verify(list.SelectedItem is not null, "Recovery refresh must preserve the selected app.");
                Verify(!Find<TextBlock>("RecoveryHint").IsVisible && ReferenceEquals(vm.SelectedDevice, first),
                    "Recovery must clear its hint while retaining the original main selection.");
                Verify(calls.Count(c => c.Device == first.Udid && c.Action == "apps") == readsBeforeRecovery + cycle + 1,
                    "Each recovered connection must perform one current-page refresh, not a retry loop.");
            }
            var blockedRefresh = delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var interruptedRefresh = (Task)KeyboardCall(panel, "RefreshAsync")!;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            ready = false; KeyboardCall(vm, "NotifyUsbControlStateChanged");
            ready = true; KeyboardCall(vm, "NotifyUsbControlStateChanged");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            blockedRefresh.SetResult(HubFixtureApps("pre-recovery-response"));
            WaitReviewTask(interruptedRefresh); Finish();
            Verify(Find<Grid>("Pages").IsEnabled && !((bool)KeyboardField(panel, "_busy")) && list.Items.Count == 1,
                "Recovery during an in-flight refresh must complete the pending refresh and release the busy state.");
            Verify(list.Items.Cast<object>().All(row => ((JsonElement)row.GetType().GetProperty("Data")!.GetValue(row)!).GetProperty("source").GetString() == first.Udid),
                "A response from the pre-recovery management context must not replace the recovered app list.");
            Console.WriteLine("PASS projection startup / discovery downgrade, three reconnects, retained app selection/filter and in-flight refresh recovery.");
            Find<TextBox>("AppSearch").Clear();
            first.UpdateFrom(DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = first.Udid, Name = first.Name, ProductType = first.ProductType, OsVersion = first.OsVersion, ConnectionType = "USB", State = ConnectionState.Ready }));
            SetKeyboardField(vm, "_isCapturing", false);
            KeyboardCall(vm, "NotifyCaptureSessionChanged"); Finish();
            Find<TextBox>("AppSearch").Text = "WeChat"; window.UpdateLayout();
            Verify(list.Items.Count == 1, "App search must match names and bundle IDs.");
            Find<TextBox>("AppSearch").Text = "no-such-app"; Finish();
            Verify(Find<StackPanel>("AppsEmpty").IsVisible && Find<Button>("ResetAppFilters").IsVisible && Find<Button>("AppSearchClear").IsVisible,
                "An empty search must explain the result and offer a direct way to reset it.");
            SaveWindowRender(window, Path.Combine(output, "search-empty.png"));
            Find<Button>("ResetAppFilters").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            Verify(list.Items.Count == populated && Find<TextBox>("AppSearch").Text.Length == 0 && !Find<Button>("AppSearchClear").IsVisible,
                "Resetting filters must restore the complete list without another device read.");
            Find<TextBox>("AppSearch").Text = "WeChat"; Finish();
            list.SelectedIndex = 0;
            var appContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var rowMore = FindVisualDescendants<Button>(appContainer).Single(button => button.Name == "RowMore");
            rowMore.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            Verify(list.ContextMenu!.IsOpen && ReferenceEquals(list.ContextMenu.PlacementTarget, rowMore),
                "A row's visible more button must open the same operations on that exact application.");
            var appMenu = Menu(list);
            Verify(MenuAction(appMenu, "app-launch").IsEnabled && MenuAction(appMenu, "app-remove").IsEnabled,
                "The selected application must expose launch and uninstall through its context menu.");
            ClickMenu(appMenu, "copy");
            Verify(copied == "com.tencent.xin", "Copy Bundle ID must copy the pointed application's identifier.");
            ClickMenu(appMenu, "app-launch");
            Console.WriteLine("App launch passed.");
            Verify(calls.Last(c => c.Action == "launch").Device == first.Udid, "Launch must target the selected main device.");
            Verify(launchedBundle == "com.tencent.xin", "The context menu must launch its captured application.");
            var staleLaunch = MenuAction(Menu(list), "app-launch");
            Find<TextBox>("AppSearch").Clear();
            var pointed = list.Items[1];
            list.ScrollIntoView(pointed); window.UpdateLayout();
            var pointedContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(pointed);
            KeyboardCall(panel, "SelectContextRow", list, pointedContainer);
            Verify(ReferenceEquals(list.SelectedItem, pointed), "Right-clicking another row must select that row immediately.");
            KeyboardCall(panel, "SelectContextRow", list, null);
            var blankMenu = Menu(list);
            Verify(list.SelectedItem is null && !blankMenu.Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-remove") &&
                MenuAction(blankMenu, "app-install").IsEnabled, "Blank-space menus must offer page actions without exposing a previous app's uninstall.");
            Verify(panel.FindName("AppLaunch") is null && panel.FindName("AppRemove") is null,
                "Application actions must not retain the old footer button row.");
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
            var launchCount = calls.Count(c => c.Action == "launch");
            staleLaunch.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            slow.SetResult(HubFixtureApps("stale-device"));
            WaitReviewTask(oldOperation); Finish();
            Console.WriteLine("Stale-response protection passed.");
            Verify(Find<TextBlock>("DeviceNameText").Text == second.DisplayName, "Device name must follow selection.");
            Verify(list.Items.Cast<object>().All(row => ((JsonElement)row.GetType().GetProperty("Data")!.GetValue(row)!).GetProperty("source").GetString() == second.Udid),
                "A stale response must not appear on another device.");
            Verify(calls.Last(c => c.Action == "apps").Device == second.Udid, "The new device must get its own refresh after cancellation.");
            Verify(calls.Count(c => c.Action == "launch") == launchCount, "A menu from a previous device must never execute on the new device.");

            var appFilter = Find<ComboBox>("AppFilter");
            appFilter.SelectedIndex = 5; Finish();
            Verify(Find<TextBox>("AppSearch").IsEnabled, "Process search must remain available.");
            Find<TextBox>("AppSearch").Text = "251"; Finish();
            Verify(list.Items.Count == 1, "Process search must match a PID.");
            Find<TextBox>("AppSearch").Text = "no-process"; Finish();
            Verify(list.Items.Count == 0 && Find<StackPanel>("AppsEmpty").IsVisible, "Process searches must show a useful empty result.");
            Find<Button>("AppSearchClear").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            list.SelectedIndex = 0;
            var processMenu = Menu(list);
            Verify(list.Items.Count == 1 && !processMenu.Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-launch") &&
                MenuAction(processMenu, "app-remove").IsEnabled, "Processes must expose terminate, with no application launch action.");
            ClickMenu(processMenu, "copy"); Verify(copied == "251", "Process menus must copy the PID.");
            appFilter.SelectedIndex = 0; Finish();
            Find<TextBox>("AppSearch").Text = "WeChat";
            failProcessRead = true; appFilter.SelectedIndex = 5; Finish();
            Verify(list.Items.Count == 0 && !Menu(list).Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "app-remove"),
                "A failed process read must never expose cached applications as terminable processes.");
            appFilter.SelectedIndex = 0; Finish();

            window.Height = 680; Finish();
            list.SelectedIndex = 7; list.ScrollIntoView(list.SelectedItem); Finish();
            var selectedId = list.SelectedItem!.GetType().GetProperty("Id")!.GetValue(list.SelectedItem);
            var listScroll = FindVisualDescendants<ScrollViewer>(list).First();
            var listOffset = listScroll.VerticalOffset;
            _ = KeyboardCall(panel, "RefreshAsync"); Finish();
            Verify(Equals(list.SelectedItem!.GetType().GetProperty("Id")!.GetValue(list.SelectedItem), selectedId) &&
                Math.Abs(listScroll.VerticalOffset - listOffset) < 1, "Refreshing must preserve app selection and scroll position.");
            window.Height = 900; Finish();

            Select(2); var profiles = Find<ListBox>("ProfilesList"); profiles.SelectedIndex = 0;
            var profileMenu = Menu(profiles); ClickMenu(profileMenu, "copy");
            Verify(copied == "fixture-profile" && MenuAction(profileMenu, "profile-remove").IsEnabled && MenuAction(profileMenu, "profile-install").IsEnabled,
                "Profile menus must provide copy, remove and install actions.");
            emptyProfiles = true; _ = KeyboardCall(panel, "RefreshAsync"); Finish();
            Verify(Find<StackPanel>("ProfilesEmpty").IsVisible &&
                FindVisualDescendants<Button>(Find<StackPanel>("ProfilesEmpty")).Single().IsEnabled,
                "An empty profile list must explain the state and provide an install entry.");
            SaveWindowRender(window, Path.Combine(output, "profiles-empty.png"));
            profileReadError = true; _ = KeyboardCall(panel, "RefreshAsync"); Finish();
            Verify(Find<TextBlock>("ProfilesEmptyTitle").Text == LocalizationService.Get("DeviceHubPanelProfileReadFailed") && !Find<Button>("ProfileEmptyInstall").IsVisible,
                "An unreadable profile list must be distinguished from a successful, empty list.");
            profileReadError = false;
            emptyProfiles = false; _ = KeyboardCall(panel, "RefreshAsync"); Finish();
            Select(3); var files = Find<ListBox>("FilesList"); files.SelectedIndex = 0;
            var fileMenu = Menu(files); ClickMenu(fileMenu, "copy");
            Verify(copied == "DCIM" && MenuAction(fileMenu, "file-download").IsEnabled, "File menus must copy the selected path and offer open/save.");
            ClickMenu(fileMenu, "file-download");
            Verify(Find<TextBox>("FilePath").Text == "/DCIM", "Opening a folder from the context menu must browse the selected directory.");
            Verify(Find<StackPanel>("FilesEmpty").IsVisible && Find<Button>("FileParent").IsEnabled,
                "An empty subfolder must show guidance and allow returning to its parent.");
            SaveWindowRender(window, Path.Combine(output, "files-empty.png"));
            Find<TextBox>("FilePath").Text = "/unfinished-address"; ClickAction("file-parent");
            Verify(Find<TextBox>("FilePath").Text == "/" && files.SelectedItem?.GetType().GetProperty("Id")!.GetValue(files.SelectedItem)?.ToString() == "DCIM" &&
                !Find<Button>("FileParent").IsEnabled, "Parent navigation must use the open folder and reselect the child, regardless of unfinished address edits.");
            failFileRead = true; Find<TextBox>("FilePath").Text = "/unreadable"; ClickAction("file-browse");
            Verify(Find<TextBox>("FilePath").Text == "/" && files.Items.Count == 3,
                "An unsuccessful folder navigation must preserve the last open address and files.");
            pagedFiles = true; Find<ComboBox>("FileRoot").SelectedIndex = 1; Finish(); files.SelectedIndex = 0;
            Verify(files.Items.Count == 2 && Find<Button>("FileNext").IsVisible, "Paged folders must offer load more.");
            ClickAction("file-next");
            Verify(files.Items.Count == 3 && files.SelectedItem?.GetType().GetProperty("Id")!.GetValue(files.SelectedItem)?.ToString() == "first.jpg" &&
                !Find<Button>("FileNext").IsVisible, "Load more must append files while retaining the first page and selection.");
            files.SelectedIndex = 2; Find<TextBox>("FilePath").Text = "/unfinished-address";
            _ = KeyboardCall(panel, "RefreshAsync"); Finish();
            Verify(files.Items.Count == 3 && Find<TextBox>("FilePath").Text == "/" &&
                files.SelectedItem?.GetType().GetProperty("Id")!.GetValue(files.SelectedItem)?.ToString() == "third.jpg",
                "Refresh must retain loaded pages and the selected file, without submitting an unfinished address.");
            pagedFiles = false; Find<ComboBox>("FileRoot").SelectedIndex = 0; Finish();

            Select(5); ClickAction("settings-read");
            var settings = (System.Collections.IDictionary)KeyboardField(panel, "_settings");
            var borderEditor = settings["showBorders"]!;
            var borderCheck = (CheckBox)borderEditor.GetType().GetProperty("Input")!.GetValue(borderEditor)!;
            Verify(borderCheck.IsChecked == true, "Phone settings must display the actual nested CoreDevice value.");
            var unavailableEditor = settings["liquidGlass"]!;
            Verify(((Grid)unavailableEditor.GetType().GetProperty("Row")!.GetValue(unavailableEditor)!).Visibility == Visibility.Collapsed,
                "Unsupported settings must be removed from the page instead of leaving unusable switches.");
            Verify(!FindVisualDescendants<Button>(panel).Any(button => button.Tag?.ToString() is "media-start" or "media-stop" or "media-status" or "record:h265" or "record:imrtp"),
                "The unusable native-media and encoded-recording buttons must be removed.");

            borderCheck.IsChecked = false; ClickAction("setting:showBorders");
            Verify(settingValues["showBorders"].GetProperty("enabled").GetBoolean() == false && borderCheck.IsChecked == false,
                "A setting change must be applied and re-read on the same target.");
            ((TextBox)KeyboardField(panel, "_latitude")).Text = "91";
            var locationCalls = calls.Count(c => c.Action == "set_location"); ClickAction("location-set");
            Verify(calls.Count(c => c.Action == "set_location") == locationCalls, "Invalid coordinates must not reach the device.");

            Select(4);
            Verify(Find<StackPanel>("LogsEmpty").IsVisible && Find<Button>("LogStart").IsVisible && !Find<Button>("LogStop").IsVisible,
                "An idle console must show a start hint and only the available toggle action.");
            SaveWindowRender(window, Path.Combine(output, "logs-idle.png"));
            Find<Button>("LogStart").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            Verify(!Find<Button>("LogStart").IsVisible && Find<Button>("LogStop").IsVisible,
                "Starting logs must replace start with stop.");
            KeyboardCall(panel, "OnLogTick", null, EventArgs.Empty); Finish();
            Verify(Find<TextBox>("LogText").Text.Contains("SpringBoard"), "Console must render real log messages.");
            Find<TextBox>("LogSearch").Text = "runningboardd";
            Verify(!Find<TextBox>("LogText").Text.Contains("SpringBoard"), "Console filter must filter messages.");
            Find<TextBox>("LogSearch").Text = "no-log-match"; Finish();
            Verify(Find<StackPanel>("LogsEmpty").IsVisible && !Find<Button>("LogSave").IsEnabled,
                "A log filter without results must show a clear-search hint and prevent saving an empty result.");
            Find<Button>("LogSearchClear").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Finish();
            ClickAction("log-stop");
            Verify(Find<TextBox>("LogText").Text.Contains("SpringBoard") && Find<Button>("LogStart").IsVisible && Find<Button>("LogSave").IsEnabled,
                "Stopping the console must retain captured logs for searching and saving.");
            ClickAction("log-start");
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
            Verify(Find<TextBlock>("RecoveryHint").IsVisible && Find<Grid>("Pages").IsVisible && !Find<Grid>("Pages").IsEnabled,
                "An established connection must preserve content while it recovers.");
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
                    if (culture == "zh-CN")
                    {
                        var brightBefore = HubBrightPixelFraction(Find<Grid>("Pages"));
                        var gate = pauseNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        pauseAction = page switch { 0 => "device_info", 1 => "apps", 2 => "list_profiles", 3 => "list_dir", 4 => "syslog_start", _ => "get_settings" };
                        if (page < 4) _ = KeyboardCall(panel, "RefreshAsync");
                        else
                        {
                            var action = page == 4 ? "log-start" : "settings-read";
                            FindVisualDescendants<Button>(panel).Single(button => button.Tag?.ToString() == action)
                                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(50)); window.UpdateLayout();
                        Verify((bool)KeyboardField(panel, "_busy") && !Find<Grid>("Pages").IsEnabled && Find<ProgressBar>("BusyProgress").IsVisible,
                            "Every page must block duplicate device actions and show progress during a pending request.");
                        if (dark) Verify(HubBrightPixelFraction(Find<Grid>("Pages")) <= brightBefore + .03,
                            $"Refreshing page {page} must retain its dark background, without a white disabled-control flash.");
                        SaveWindowRender(window, Path.Combine(output, $"busy-{(dark ? "dark" : "light")}-{page}.png"));
                        gate.SetResult(true); Finish(); pauseAction = null;
                        Verify(Find<Grid>("Pages").IsEnabled && !Find<ProgressBar>("BusyProgress").IsVisible,
                            "A finished request must restore page interaction and remove the progress indicator.");
                        if (page == 1)
                        {
                            list.SelectedIndex = 0;
                            var popup = Menu(list); popup.PlacementTarget = list; popup.IsOpen = true;
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(60)); popup.ApplyTemplate(); popup.UpdateLayout();
                            Verify(popup.IsVisible && MenuAction(popup, "app-launch").IsVisible &&
                                !string.IsNullOrWhiteSpace(MenuAction(popup, "copy").Header?.ToString()),
                                "The first opened app context menu must render localized, actionable items.");
                            if (dark) Verify(HubBrightPixelFraction(popup) < .1, "Dark context menus must use the app theme without a white system background.");
                            _ = KeyboardCall(panel, "RefreshAsync"); Finish();
                            Verify(!popup.IsOpen, "Refreshing must close the previously opened menu.");
                        }
                    }
                    Verify(panel.ActualWidth >= 400, "The inspector must retain a usable width.");
                    var connection = Find<TextBlock>("ConnectionText");
                    var connectionWidth = new FormattedText(connection.Text, System.Globalization.CultureInfo.CurrentUICulture, connection.FlowDirection,
                        new Typeface(connection.FontFamily, connection.FontStyle, connection.FontWeight, connection.FontStretch), connection.FontSize,
                        connection.Foreground, VisualTreeHelper.GetDpi(connection).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    var connectionEnd = connection.TranslatePoint(new Point(connection.ActualWidth, 0), panel).X;
                    var connectionParent = (FrameworkElement)connection.Parent;
                    Verify(connection.ActualWidth + 1 >= connectionWidth && connectionEnd <= panel.ActualWidth - 15,
                        $"The connection status must remain fully readable in {culture}: text={connectionWidth:0.0}, actual={connection.ActualWidth:0.0}, desired={connection.DesiredSize}, offset={VisualTreeHelper.GetOffset(connection)}, end={connectionEnd:0.0}, panel={panel.ActualWidth:0.0}, parent={connectionParent.GetType().Name}/{connectionParent.ActualWidth:0.0}/{connectionParent.DesiredSize}/{connectionParent.TranslatePoint(new Point(), panel)}.");
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
        Console.WriteLine("Integrated device manager passed: row menus, search/reset and process search, retained list position, empty states, folder navigation and appended pagination, log toggle/filter/retention, projection recovery, stale-response protection, six-page refresh backgrounds, five languages, two themes and compact/lightweight layout.");
        return 0;
    }

    private static double HubBrightPixelFraction(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var bright = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 232 && pixels[i + 1] > 232 && pixels[i + 2] > 232 && pixels[i + 3] > 240) bright++;
        return (double)bright / (width * height);
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
