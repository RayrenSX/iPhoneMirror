using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSourceCardPersistenceStep(string path, string step)
    {
        const SourceCardFields custom = SourceCardFields.Name | SourceCardFields.Model | SourceCardFields.Status;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.GetFullPath(path));
        SetKeyboardField(app, "_settingsStore", store);
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                var primary = step != "dismiss-conflict";
                var conflictingSnapshot = primary ? null : File.ReadAllText(path);
                KeyboardCall(app, "LoadStartupSettings", primary);
                if (step == "unreadable-settings")
                {
                    app.IsUiPreviewMode = false;
                    InteractionAssert(!app.TrySetUpdatePreference(settings => settings.SourceCardFields,
                        (settings, value) => settings.SourceCardFields = value, custom),
                        "An unreadable settings document must block writes based on defaults.");
                }
                else if (!primary)
                {
                    InteractionAssert(File.ReadAllText(path) == conflictingSnapshot,
                        "Reading startup preferences for an instance conflict must not write migrations.");
                    InteractionAssert(app.UpdateSettings.SourceCardFields == SourceCardFields.Default,
                        "The dismissed launcher fixture must begin with default fields.");
                    store.Update(settings => settings.SourceCardFields = custom);
                }
                else
                {
                    var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                    var options = (Button)window.FindName("SourceCardOptionsButton");
                    KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
                    FinishWorkspaceReveal(window);
                    options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    ToolsPump();
                    var menu = options.ContextMenu;
                    var entries = menu.Items.OfType<MenuItem>().ToArray();
                    var expected = step is "reset" or "verify-custom" ? custom : SourceCardFields.Default;
                    InteractionAssert(menu.IsOpen && entries.Length == 7 && entries.Where(item => item.Tag is SourceCardFields)
                        .All(item => item.IsChecked == expected.HasFlag((SourceCardFields)item.Tag)),
                        "A fresh process must open the menu with the saved fields.");
                    app.IsUiPreviewMode = false;
                    var vm = (MainViewModel)window.DataContext;
                    if (step == "custom-and-exit")
                    {
                        InteractionAssert(vm.TrySetSourceCardFields(custom) &&
                            store.Load(persistMigrations: false).SourceCardFields == custom,
                            "The selected fields must be written before shutdown.");
                        Console.WriteLine("PASS source card selection written before abrupt exit.");
                        Environment.Exit(0);
                    }
                    if (step == "reset")
                        InteractionAssert(vm.TrySetSourceCardFields(SourceCardFields.Default) &&
                            store.Load(persistMigrations: false).SourceCardFields == SourceCardFields.Default,
                            "Reset must persist before shutdown.");
                    InteractionAssert(app.UpdateSettings.Language == "ja-JP" && LocalizationService.SelectedLanguage == "ja-JP",
                        "Source preferences must preserve the startup language.");
                    menu.IsOpen = false;
                    CloseWorkspaceTestWindow(window);
                }
                Console.WriteLine("PASS " + step);
                app.IsUiPreviewMode = false;
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.IsUiPreviewMode = true;
                if (app.MainWindow is MainWindow window) CloseWorkspaceTestWindow(window);
                app.Shutdown(1);
            }
        });
        return app.Run();
    }

    private static int RunDeviceContextMenuTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        Console.WriteLine("Device menu: creating isolated workspace.");
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        Console.WriteLine("Device menu: workspace created.");
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        // Device onboarding is a separate modal flow, outside this menu test.
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), window,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var list = (ListBox)window.FindName("DeviceListBox");
        const string firstId = "00008101-0000000000000001";
        const string otherId = "00008101-0000000000000002";
        DeviceViewModel Device(string udid, ConnectionState state = ConnectionState.Ready) =>
            DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = udid, Name = udid == firstId ? "Phone A" : "Phone B",
                ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = state });
        var first = Device(firstId); var other = Device(otherId);
        ContextMenu Menu(DeviceViewModel device)
        {
            window.UpdateLayout(); ToolsPump();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(device);
            InteractionAssert(row is not null, "Device menu target has no row.");
            var menu = row!.ContextMenu; menu.PlacementTarget = row;
            // Anchor review captures to the row rather than the user's desktop cursor.
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            menu.IsOpen = true; ToolsPump();
            return menu;
        }
        try
        {
            vm.Devices.Clear(); vm.Devices.Add(first); vm.Devices.Add(other);
            KeyboardCall(vm, "SetSelectedDevice", first, false);
            Console.WriteLine("Device menu: fixtures loaded.");
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
            FinishWorkspaceReveal(window);
            var panel = (FrameworkElement)window.FindName("DevicePanel");
            var header = (FrameworkElement)window.FindName("DevicePanelHeader");
            var refresh = (Button)window.FindName("RefreshDevicesButton");
            var emptyState = (FrameworkElement)window.FindName("DeviceListEmptyState");
            InteractionAssert(refresh.IsDescendantOf(header) && ReferenceEquals(refresh.Command, vm.RefreshCommand),
                "The source header does not expose the existing refresh action.");
            InteractionAssert(emptyState.Visibility == Visibility.Collapsed, "A populated source list must hide the empty state.");
            VerifyFullHeightSources(panel);
            VerifyDiscoveryRespectsVisiblePanel();
            var sent = new List<(string, DeviceAction)>();
            window.ExecuteDeviceAction = (id, action, token) => { sent.Add((id, action)); return Task.CompletedTask; };
            var confirmations = 0;
            window.ConfirmDeviceAction = (_, _) => { confirmations++; return false; };
            var menu = Menu(other);
            Console.WriteLine("Device menu: context opened.");
            var allItems = menu.Items.OfType<MenuItem>().ToArray();
            InteractionAssert(allItems.Select(item => item.Tag as string).SequenceEqual(
                new[] { "CopyInfo", "CreateShortcut", "Restart", "Shutdown" }),
                "Device context menu lost a supported action or restored the removed sleep action.");
            InteractionAssert(!allItems.Any(item => item.Tag is "CoreDeviceMirror"),
                "CoreDevice mirroring must start through the main Start action, not a separate device menu action.");
            var items = allItems;
            InteractionAssert(items.All(item => item.IsEnabled), "Ready device local and power actions must stay available.");
            InteractionAssert(ReferenceEquals(vm.SelectedDevice, first), "Opening another device's menu switched the current source.");
            string? copied = null; window.CopyDeviceInfoText = text => copied = text;
            KeyboardCall(window, "OnDeviceActionClick", items[0], new RoutedEventArgs());
            InteractionAssert(copied?.Contains(otherId) == true && !copied.Contains(firstId), "Copy used the selected device rather than the clicked one.");
            var shortcuts = new List<DeviceViewModel>();
            window.CreateDeviceShortcut = (target, owner) =>
            {
                InteractionAssert(ReferenceEquals(owner, window), "Shortcut result prompt has the wrong owner.");
                shortcuts.Add(target);
            };
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            InteractionAssert(shortcuts.SequenceEqual(new[] { other }) && ReferenceEquals(vm.SelectedDevice, first),
                "Shortcut creation targeted the selected device or switched the current source.");
            foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US", "ja-JP" })
            {
                DisplayLanguage(language); ToolsPump();
                InteractionAssert(allItems.All(item => item.Header is string text && text.Length > 0 &&
                    !text.StartsWith("DeviceAction") && text is not ("CreateCompactShortcut" or "CoreDeviceMirrorStart")),
                    "A device action label is missing a translation.");
            }
            DisplayLanguage("zh-CN"); ToolsPump();
            SaveMenu(menu, "device-context-menu.png");
            menu.IsOpen = false;
            VerifyCardOptions();
            VerifyStreamingStatus();
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Shutdown));
            InteractionAssert(confirmations == 2 && sent.Count == 0, "Cancel sent a power command.");
            window.ConfirmDeviceAction = (target, _) => { InteractionAssert(ReferenceEquals(target, other), "Confirmation targets the wrong device."); return true; };
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Shutdown));
            InteractionAssert(sent.Count == 2 && sent.All(entry => entry.Item1 == otherId) && ReferenceEquals(vm.SelectedDevice, first),
                "Power routing switched source, targeted another phone, or duplicated a command.");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ExecuteDeviceAction = (_, _, _) => pending.Task;
            var running = window.RunDeviceActionAsync(other, DeviceAction.Restart); ToolsPump();
            menu = Menu(other);
            InteractionAssert(menu.Items.OfType<MenuItem>().Where(item => item.Tag is "CopyInfo" or "CreateShortcut").All(item => item.IsEnabled) &&
                menu.Items.OfType<MenuItem>().Where(item => item.Tag is "Restart" or "Shutdown").All(item => !item.IsEnabled),
                "Busy power command availability is wrong.");
            menu.IsOpen = false;
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            pending.SetResult(); ToolsComplete(running);
            window.ExecuteDeviceAction = (id, action, token) => { sent.Add((id, action)); return Task.CompletedTask; };
            var callsBeforeRemovedAction = sent.Count;
            ToolsComplete(window.RunDeviceActionAsync(other, (DeviceAction)0));
            InteractionAssert(sent.Count == callsBeforeRemovedAction, "The removed sleep action value was still accepted.");
            foreach (var device in new[] { Device("airplay://" + otherId), Device("00008101-0000000000000000", ConnectionState.Connected) })
            {
                vm.Devices.Add(device); menu = Menu(device);
                InteractionAssert(menu.Items.OfType<MenuItem>().Where(item => item.Tag is "CopyInfo" or "CreateShortcut").All(item => item.IsEnabled) &&
                    menu.Items.OfType<MenuItem>().Where(item => item.Tag is "Restart" or "Shutdown").All(item => !item.IsEnabled),
                    "Unavailable power actions were enabled for AirPlay or untrusted devices.");
                menu.IsOpen = false;
            }
            var mediaCast = Device(DeviceViewModel.MediaCastUdid);
            vm.Devices.Add(mediaCast); menu = Menu(mediaCast);
            var mediaShortcut = menu.Items.OfType<MenuItem>().Single(item => item.Tag is "CreateShortcut");
            InteractionAssert(!mediaShortcut.IsEnabled, "Media cast appeared as a launchable device.");
            mediaShortcut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            InteractionAssert(shortcuts.Count == 1, "Media cast unexpectedly created a shortcut.");
            menu.IsOpen = false;
            window.ConfirmDeviceAction = (_, _) => { vm.Devices.Remove(other); return true; };
            var callsBefore = sent.Count;
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            InteractionAssert(sent.Count == callsBefore, "A removed device received an action after confirmation.");
            var start = DeviceActionService.CreateStartInfo(Path.Combine(output, "bridge.exe"), otherId, DeviceAction.Shutdown);
            InteractionAssert(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.SequenceEqual(
                new[] { "--device-action", "shutdown", "--udid", otherId, "--usb" }), "Unsafe or untargeted bridge invocation.");
            try { DeviceActionService.CreateStartInfo("bridge.exe", "airplay://" + otherId, DeviceAction.Restart); throw new InvalidOperationException("Invalid target accepted."); }
            catch (ArgumentException) { }
            try { DeviceActionService.CreateStartInfo("bridge.exe", otherId, (DeviceAction)0); throw new InvalidOperationException("Removed sleep action accepted."); }
            catch (ArgumentException) { }
            for (var index = 0; index < 16; index++) vm.Devices.Add(Device($"00008101-000000000000{index + 100:D4}"));
            window.UpdateLayout(); ToolsPump();
            var scroller = Visuals(list).OfType<ScrollViewer>().First();
            InteractionAssert(scroller.ScrollableHeight > 0, "A long source list must scroll inside its panel.");
            list.ScrollIntoView(vm.Devices[^1]);
            window.UpdateLayout(); ToolsPump();
            InteractionAssert(scroller.VerticalOffset > 0, "The last source cannot be brought into view.");
            vm.Devices.Clear();
            window.UpdateLayout(); ToolsPump();
            InteractionAssert(emptyState.IsVisible && refresh.IsVisible, "The empty source panel must show connection guidance and refresh.");
            Console.WriteLine("PASS device menu: shortcut and device actions, five languages, right target, unchanged selection, cancel/confirm, busy guard, USB/trust availability and disconnect recheck. No real device command sent.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }

        void VerifyDiscoveryRespectsVisiblePanel()
        {
            var panelType = KeyboardField(window, "_leftWorkspacePanel").GetType();
            void Show(string page)
            {
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, page));
                FinishWorkspaceReveal(window);
            }
            // The test uses synthetic devices. Skip onboarding for the added row.
            ((HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown")).Add(other.Udid);
            var added = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, other);
            Show("Mirroring");
            KeyboardCall(window, "OnDeviceCardsCollectionChanged", vm.DeviceCards, added);
            FinishWorkspaceReveal(window);
            InteractionAssert(((FrameworkElement)window.FindName("MirroringPanel")).IsVisible,
                "Discovery must keep the active Mirroring page in view.");

            Show("None");
            KeyboardCall(window, "OnDeviceCardsCollectionChanged", vm.DeviceCards, added);
            FinishWorkspaceReveal(window);
            InteractionAssert(list.IsVisible, "Discovery should reveal the source list when its sidebar is free.");
        }

        void VerifyFullHeightSources(FrameworkElement panel)
        {
            var toggle = (FrameworkElement)window.FindName("DevicePanelToggle");
            var preview = (FrameworkElement)window.FindName("PreviewPanel");
            var host = (FrameworkElement)window.FindName("LeftPanelHost");
            var workspace = (FrameworkElement)window.FindName("CenterPanel");
            VerifyHeight();
            KeyboardCall(window, "OnNavigateDevicesClick", toggle, new RoutedEventArgs());
            FinishWorkspaceReveal(window);
            InteractionAssert(!panel.IsVisible, "The sources navigation entry must still toggle the sidebar.");
            var fullPreviewWidth = preview.ActualWidth;
            vm.Devices.Remove(other);
            KeyboardCall(window, "OnNavigateDevicesClick", toggle, new RoutedEventArgs());
            FinishWorkspaceReveal(window);
            VerifyHeight();
            InteractionAssert(preview.ActualWidth < fullPreviewWidth - 150, "The single-source panel must be docked beside the preview.");
            DisplayLanguage("zh-CN");
            ThemeService.Apply(AppTheme.Dark);
            ToolsPump();
            SaveWindowRender(window, Path.Combine(output, "single-source-sidebar.png"));
            var sourceTitle = (FrameworkElement)window.FindName("DevicePanelTitle");
            var sourceClose = (FrameworkElement)window.FindName("CloseDevicePanelButton");
            var sourceTitlePoint = sourceTitle.TranslatePoint(default, panel);
            var sourceClosePoint = sourceClose.TranslatePoint(default, panel);
            var panels = KeyboardField(window, "_leftWorkspacePanel").GetType();
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panels, "Mirroring"));
            FinishWorkspaceReveal(window);
            var mirroring = (FrameworkElement)window.FindName("MirroringPanel");
            var mirroringTitle = (FrameworkElement)window.FindName("MirroringPanelTitle");
            var mirroringClose = (FrameworkElement)window.FindName("CloseMirroringPanelButton");
            InteractionAssert((sourceTitlePoint - mirroringTitle.TranslatePoint(default, mirroring)).Length < 1 &&
                (sourceClosePoint - mirroringClose.TranslatePoint(default, mirroring)).Length < 1,
                "The source and mirroring sidebar titles and close buttons must share the same coordinates.");
            SaveWindowRender(window, Path.Combine(output, "mirroring-sidebar.png"));
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panels, "Devices"));
            FinishWorkspaceReveal(window);
            ThemeService.Apply(AppTheme.Light);
            ToolsPump();
            VerifyHeight();
            SaveWindowRender(window, Path.Combine(output, "single-source-sidebar-light.png"));
            ThemeService.Apply(AppTheme.Dark);
            vm.Devices.Add(other);
            ToolsPump();
            VerifyHeight();
            Console.WriteLine("PASS source layout: one and multiple devices share the full-height sidebar in both themes.");

            void VerifyHeight() => InteractionAssert(panel.IsVisible && ReferenceEquals(VisualTreeHelper.GetParent(panel), host) &&
                Math.Abs(panel.ActualHeight - workspace.ActualHeight) < 1,
                "The source panel must fill the workspace height while docked.");
        }

        void SaveMenu(ContextMenu menu, string fileName)
        {
            // Popup HWNDs are not part of a Window's WPF render tree.
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var menuOrigin = window.PointFromScreen(menu.PointToScreen(new Point()));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(menu), null,
                    new Rect(menuOrigin, new Size(menu.ActualWidth, menu.ActualHeight)));
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, fileName));
            encoder.Save(stream);
        }

        void VerifyStreamingStatus()
        {
            var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
            var state = new DeviceCaptureState { Udid = other.Udid };
            sessions.Set(state);
            using var handle = new NativeSessionHandle(47, ownsHandle: false);
            try
            {
                sessions.SetHandle(state, handle);
                other.UpdateFrom(Device(otherId, ConnectionState.UsbPresentNoMux));
                InteractionAssert(other.CardStatusDisplay == LocalizationService.Get("DeviceCardStatusWaitingForConnection"),
                    "An idle USB device must show its discovery status.");
                var video = new NativeCaptureStatus { State = CaptureState.Streaming, Width = 1080, Height = 2340 };
                KeyboardCall(vm, "ObserveMirroringVideo", state, video);
                other.UpdateFrom(Device(otherId, ConnectionState.Connected));
                window.UpdateLayout(); ToolsPump();
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(other);
                var text = Visuals(row).OfType<TextBlock>().Single(item => item.Name == "SourceCardStatus");
                InteractionAssert(other.IsMirroring && !first.IsMirroring &&
                    text.Text == LocalizationService.Get("DeviceCardStatusMirroring") && other.CardStatusDetail == text.Text,
                    "Only the streaming device may show mirroring, even when USB discovery reports waiting.");
                SaveWindowRender(window, Path.Combine(output, "source-card-mirroring.png"));

                KeyboardCall(vm, "ObserveMirroringVideo", state, new NativeCaptureStatus { State = CaptureState.Stopped });
                InteractionAssert(!other.IsMirroring, "A stopped video session must clear the card state.");
                KeyboardCall(vm, "ObserveMirroringVideo", state, video);
                sessions.SetHandle(state, null);
                InteractionAssert(!other.IsMirroring && other.CardStatusDisplay == LocalizationService.Get("DeviceCardStatusWaitingForConnection"),
                    "Detaching a handle must clear the mirroring status and restore device readiness.");
            }
            finally
            {
                sessions.SetHandle(state, null);
                sessions.Remove(other.Udid);
                other.UpdateFrom(Device(otherId));
            }
        }

        void VerifyCardOptions()
        {
            var options = (Button)window.FindName("SourceCardOptionsButton");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); ToolsPump();
            var menu = options.ContextMenu;
            var fields = menu.Items.OfType<MenuItem>().Where(item => item.Tag is SourceCardFields)
                .ToDictionary(item => (SourceCardFields)item.Tag);
            InteractionAssert(menu.IsOpen && ReferenceEquals(menu.PlacementTarget, options) && fields.Count == 6 &&
                fields.Values.All(item => item.IsCheckable && item.StaysOpenOnClick), "The source display menu must open next to its gear with six persistent check items.");
            var settingsPath = Path.Combine(Path.GetFullPath(output), "source-card-options.json");
            var store = new UpdateSettingsStore(settingsPath);
            const SourceCardFields expectedDefault = SourceCardFields.Icon | SourceCardFields.Name |
                SourceCardFields.Status | SourceCardFields.Transport;
            File.WriteAllText(settingsPath, "{}");
            InteractionAssert(store.Load().SourceCardFields == expectedDefault &&
                vm.VisibleSourceCardFields == expectedDefault, "A profile without a saved choice must show icon, name, status and connection type only.");
            File.WriteAllText(settingsPath, """{"SourceCardFields":64}""");
            InteractionAssert(store.Load().SourceCardFields == expectedDefault &&
                File.ReadAllText(settingsPath).Contains($"\"SourceCardFields\": {(int)expectedDefault}", StringComparison.Ordinal),
                "A saved mask with no supported fields must be repaired to the compact default.");
            File.WriteAllText(settingsPath, """{"SourceCardFields":72}""");
            InteractionAssert(store.Load().SourceCardFields == SourceCardFields.SystemVersion,
                "Unknown bits must not erase a valid saved field.");
            var originalStore = KeyboardField(app, "_settingsStore");
            var originalSettings = app.UpdateSettings.Clone();
            SetKeyboardField(app, "_settingsStore", store);
            app.IsUiPreviewMode = false;
            try
            {
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
                void VerifyFields(SourceCardFields selected)
                {
                    window.UpdateLayout(); ToolsPump();
                    InteractionAssert(vm.VisibleSourceCardFields == selected && fields.All(pair => pair.Value.IsChecked == selected.HasFlag(pair.Key)),
                        "The device card and menu checks disagree.");
                    foreach (var field in fields.Keys)
                    {
                        var element = Visuals(row).OfType<FrameworkElement>().Single(item => item.Name == "SourceCard" + field);
                        InteractionAssert(element.IsVisible == selected.HasFlag(field), $"The {field} field did not update live.");
                    }
                }

                void Toggle(SourceCardFields field) => KeyboardCall(fields[field], "OnClick");
                VerifyFields(SourceCardFields.Default);
                Toggle(SourceCardFields.Model); Toggle(SourceCardFields.SystemVersion);
                VerifyFields(SourceCardFields.All);
                InteractionAssert(store.Load().SourceCardFields == SourceCardFields.All &&
                    app.UpdateSettings.Clone().SourceCardFields == SourceCardFields.All, "Source display options did not survive save/load and clone.");
                SaveMenu(menu, "source-card-display-options.png");
                foreach (var field in fields.Keys.Where(field => field != SourceCardFields.Name)) Toggle(field);
                VerifyFields(SourceCardFields.Name);
                InteractionAssert(!fields[SourceCardFields.Name].IsEnabled && !vm.TrySetSourceCardFields(0),
                    "A card must retain at least one visible field.");
                var blocked = Path.Combine(output, "blocked-source-card-options");
                File.WriteAllText(blocked, "not a directory");
                SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocked, "settings.json")));
                InteractionAssert(!vm.TrySetSourceCardFields(SourceCardFields.All) && vm.VisibleSourceCardFields == SourceCardFields.Name,
                    "A settings write failure changed the visible fields.");
                SetKeyboardField(app, "_settingsStore", store);
                KeyboardCall(menu.Items.OfType<MenuItem>().Last(), "OnClick");
                VerifyFields(SourceCardFields.Default);
                InteractionAssert(store.Load().SourceCardFields == SourceCardFields.Default && ReferenceEquals(vm.SelectedDevice, first),
                    "Reset failed to save the compact default or changed the selected device.");
                SaveMenu(menu, "source-card-default-options.png");

                Toggle(SourceCardFields.Model);
                Toggle(SourceCardFields.Transport);
                const SourceCardFields custom = SourceCardFields.Icon | SourceCardFields.Name |
                    SourceCardFields.Model | SourceCardFields.Status;
                VerifyFields(custom);
                menu.IsOpen = false;
                var loadedSettings = new UpdateSettingsStore(settingsPath).Load();
                InteractionAssert(loadedSettings.SourceCardFields == custom, "The customized fields must be saved before the window closes.");
                app.RestoreUpdateSettings(loadedSettings);
                app.IsUiPreviewMode = true;
                var reopened = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    var reopenedVm = (MainViewModel)KeyboardField(reopened, "_viewModel");
                    reopenedVm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
                        typeof(NotifyCollectionChangedEventHandler), reopened,
                        typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
                    var reopenedDevice = Device(firstId);
                    reopenedVm.Devices.Add(reopenedDevice);
                    KeyboardCall(reopened, "SetLeftWorkspacePanel", Enum.Parse(KeyboardField(reopened, "_leftWorkspacePanel").GetType(), "Devices"));
                    FinishWorkspaceReveal(reopened);
                    var reopenedList = (ListBox)reopened.FindName("DeviceListBox");
                    var reopenedRow = (ListBoxItem)reopenedList.ItemContainerGenerator.ContainerFromItem(reopenedDevice);
                    var reopenedOptions = (Button)reopened.FindName("SourceCardOptionsButton");
                    reopenedOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    reopened.UpdateLayout(); ToolsPump();
                    foreach (var item in reopenedOptions.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Tag is SourceCardFields))
                    {
                        var field = (SourceCardFields)item.Tag;
                        var element = Visuals(reopenedRow).OfType<FrameworkElement>().Single(item => item.Name == "SourceCard" + field);
                        InteractionAssert(item.IsChecked == custom.HasFlag(field) && element.IsVisible == item.IsChecked,
                            $"The reopened workspace did not restore the {field} preference.");
                    }
                    reopenedOptions.ContextMenu.IsOpen = false;
                }
                finally
                {
                    CloseWorkspaceTestWindow(reopened);
                    app.MainWindow = window;
                }
            }
            finally
            {
                menu.IsOpen = false;
                app.IsUiPreviewMode = true;
                app.RestoreUpdateSettings(originalSettings);
                SetKeyboardField(app, "_settingsStore", originalStore);
            }
            Console.WriteLine("PASS source card display: compact default, settings migration, live visibility, save/load in a fresh workspace, reset and write-failure rollback.");
        }
    }
}
