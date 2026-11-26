using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private static int RunSettingsCategoryTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var original = app.UpdateSettings.Clone();
        app.UpdateSettings.SettingsCategory = "invalid-category";
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        Console.WriteLine("Settings test window ready.");
        try
        {
            T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Select(string category)
            {
                Find<RadioButton>(category + "SettingsTab").IsChecked = true;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                window.UpdateLayout();
                foreach (var name in new[] { "Application", "Projection", "Control" })
                    InteractionAssert(Find<ScrollViewer>(name + "SettingsScrollViewer").Visibility ==
                        (name == category ? Visibility.Visible : Visibility.Collapsed),
                        "Settings selection left multiple categories visible or hid the selected one.");
            }

            InteractionAssert(Find<RadioButton>("ProjectionSettingsTab").IsChecked == true,
                "Unknown saved categories must fall back to projection.");
            KeyboardCall(window, "OnCloseDevicePanelClick", window, new RoutedEventArgs());
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(500));

            // Settings category persistence uses an isolated file, never the user's settings.
            app.IsUiPreviewMode = false;
            try { Select("Control"); }
            finally { app.IsUiPreviewMode = true; }
            var absoluteMouse = FindVisualDescendants<CheckBox>(
                Find<ScrollViewer>("ControlSettingsScrollViewer"))
                .Single(check => System.Windows.Automation.AutomationProperties.GetAutomationId(check) ==
                    "BluetoothWindowMouseCheckBox");
            InteractionAssert(absoluteMouse.IsVisible &&
                absoluteMouse.Template.FindName("Track", absoluteMouse) is Border &&
                System.Windows.Data.BindingOperations.GetBinding(absoluteMouse,
                    System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path ==
                    "BluetoothWindowMouseEnabled",
                "The integrated Bluetooth absolute mouse setting is missing or bound to the wrong preference.");
            InteractionAssert(store.Load().SettingsCategory == "control" &&
                app.UpdateSettings.Clone().SettingsCategory == "control",
                "The selected category did not survive save/load/clone.");
            Console.WriteLine("Category save/load verified.");
            KeyboardCall(window, "OnCloseSettingsPanelClick", window, new RoutedEventArgs());
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            Console.WriteLine("Reopened settings.");
            InteractionAssert(Find<RadioButton>("ControlSettingsTab").IsChecked == true,
                "Reopening settings discarded the selected category.");
            Select("Projection");
            InteractionAssert(Find<RadioButton>("ProjectionSettingsTab").IsChecked == true,
                "The projection settings tab did not select its page.");

            var phoneFrameToggle = Find<CheckBox>("ProjectionPhoneFrameCheckBox");
            InteractionAssert(FindVisualDescendants<CheckBox>(Find<ScrollViewer>("ProjectionSettingsScrollViewer"))
                .All(toggle => toggle != phoneFrameToggle), "Projection must not duplicate the independent frame preference.");
            Select("Application");
            var applicationPage = Find<ScrollViewer>("ApplicationSettingsScrollViewer");
            var mainColorPicker = Find<ComboBox>("ProjectionFrameColorComboBox");
            InteractionAssert(FindVisualDescendants<CheckBox>(applicationPage).Contains(phoneFrameToggle) &&
                FindVisualDescendants<ComboBox>(applicationPage).Contains(mainColorPicker),
                "The independent frame switch and body color must be in the Application category.");
            var previewAppearance = Find<ContentControl>("MainPreviewAppearanceSection");
            var previewSwitches = FindVisualDescendants<CheckBox>(previewAppearance).ToArray();
            InteractionAssert(previewSwitches.Length == 2 && previewSwitches.All(toggle =>
                toggle.Template.FindName("Track", toggle) is Border),
                "The application preview options must show the two switch rows.");
            var mainCorners = previewSwitches[0];
            var mainFrame = previewSwitches[1];
            app.IsUiPreviewMode = false;
            try
            {
                mainFrame.IsChecked = true;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert(store.Load().MainPreviewPhoneFrame && !store.Load().PhoneFrameEnabled &&
                    phoneFrameToggle.IsChecked == false, "The main preview choice must save before connecting a phone and remain independent.");
                mainCorners.IsChecked = false;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert(!mainFrame.IsEnabled && mainFrame.IsChecked == false &&
                    !store.Load().MainPreviewRoundedCorners && !store.Load().MainPreviewPhoneFrame,
                    "Turning off corners must save both changes and disable the frame immediately.");
                ApplyTheme(typeof(App).Assembly, AppTheme.Light);
                mainColorPicker.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                SaveWindowRender(window, Path.Combine(output, "main-preview-corners-off.png"));
                mainCorners.IsChecked = true;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            }
            finally { app.IsUiPreviewMode = true; }
            mainColorPicker.BringIntoView();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            SaveWindowRender(window, Path.Combine(output, "main-application-preview-appearance.png"));
            Select("Projection");

            // Use a synthetic, offline source; no discovery, capture or reverse control starts.
            var vm = (MainViewModel)KeyboardField(window, "_viewModel");
            // Synthetic devices exercise settings, not discovery onboarding.
            vm.Devices.CollectionChanged -= (System.Collections.Specialized.NotifyCollectionChangedEventHandler)
                Delegate.CreateDelegate(typeof(System.Collections.Specialized.NotifyCollectionChangedEventHandler), window,
                    typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
            var device = DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = "settings-layout-fixture", Name = "iPhone · Preview", ProductType = "iPhone13,1", State = ConnectionState.Ready });
            SetKeyboardField(vm, "_selectedDevice", device);
            vm.Devices.Add(device);
            Find<Border>("ControlPanelContent").DataContext = null;
            Find<Border>("ControlPanelContent").DataContext = vm;
            Console.WriteLine("Offline fixture ready.");
            var frameManager = (MultiDevicePreviewManager)KeyboardField(window, "_secondaryMirrors");
            TestIndependentWindowSettings(window, app, vm, device, store);
            Select("Application");
            app.IsUiPreviewMode = false;
            try
            {
                SetKeyboardField(vm, "_selectedDevice", null);
                KeyboardCall(window, "RefreshProjectionPhoneFrame");
                InteractionAssert(frameManager.GetPhoneFrameState().Available && phoneFrameToggle.IsEnabled &&
                    !Find<ComboBox>("ProjectionFrameColorComboBox").IsEnabled,
                    "The global frame switch must remain usable without a device while per-device colors remain unavailable.");
                InteractionAssert(frameManager.SetPhoneFrameAsync(true).GetAwaiter().GetResult(), "Frame preference save failed.");
                InteractionAssert(!frameManager.HasAnyOpen && frameManager.GetPhoneFrameState().Enabled &&
                    store.Load().PhoneFrameEnabled && phoneFrameToggle.IsChecked == true,
                    "Changing the global preference without a device opened a window or failed to persist/synchronize.");
                foreach (var selected in new[]
                {
                    device,
                    DeviceViewModel.FromNative(new NativeDeviceInfo
                    { Udid = "unsupported-frame-fixture", ProductType = "iPhone99,99", State = ConnectionState.Ready }, DeviceAppearance.Unknown),
                    DeviceViewModel.CreateMediaCast(),
                })
                {
                    SetKeyboardField(vm, "_selectedDevice", selected);
                    KeyboardCall(window, "RefreshProjectionPhoneFrame");
                    InteractionAssert(frameManager.GetPhoneFrameState() is { Enabled: true, Available: true } &&
                        phoneFrameToggle.IsChecked == true && phoneFrameToggle.IsEnabled &&
                        frameManager.CanSelectFrameColor == ReferenceEquals(selected, device),
                        "Changing the selected device must preserve the global switch and only gate per-device colors.");
                }
                InteractionAssert(frameManager.SetPhoneFrameAsync(false).GetAwaiter().GetResult() &&
                    !frameManager.HasAnyOpen && !store.Load().PhoneFrameEnabled && phoneFrameToggle.IsChecked == false,
                    "Disabling the global preference from a media source opened a window or failed to persist/synchronize.");
                var blockedFramePreferencePath = Path.Combine(Path.GetFullPath(output), "blocked-global-frame-parent");
                File.WriteAllText(blockedFramePreferencePath, "A file cannot hold settings.");
                SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blockedFramePreferencePath, "settings.json")));
                phoneFrameToggle.IsChecked = true;
                phoneFrameToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                InteractionAssert(!app.UpdateSettings.PhoneFrameEnabled && !frameManager.GetPhoneFrameState().Enabled &&
                    phoneFrameToggle.IsChecked == false && Find<TextBlock>("ProjectionPhoneFrameFeedback").Visibility == Visibility.Visible &&
                    !store.Load().PhoneFrameEnabled,
                    "A failed global preference save must restore the saved state and checkbox with visible feedback.");
            }
            finally
            {
                SetKeyboardField(app, "_settingsStore", store);
                SetKeyboardField(vm, "_selectedDevice", device);
                KeyboardCall(window, "RefreshProjectionPhoneFrame");
                app.IsUiPreviewMode = true;
            }
            Console.WriteLine("PASS global frame preferences persist/synchronize without a device, across sources, and roll back failed saves without starting capture.");

            var frameSettings = new IPhoneMirror.App.Windows.ProjectionSettingsWindow(vm,
                () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, () => Task.CompletedTask, () => { });
            frameManager.ConfigurePhoneFrameSettings(frameSettings);
            frameSettings.Show();
            ((RadioButton)frameSettings.FindName("ApplicationSettingsTab")).IsChecked = true;
            frameSettings.UpdateLayout();
            var colorPicker = (ComboBox)frameSettings.FindName("FrameColorComboBox");
            InteractionAssert(colorPicker.IsVisible && ((CheckBox)frameSettings.FindName("PhoneFrameCheckBox")).IsVisible,
                "The standalone Application page must show the independent frame and color settings.");
            var frameBlockedPath = Path.Combine(Path.GetFullPath(output), "blocked-frame-settings-parent");
            File.WriteAllText(frameBlockedPath, "A file cannot hold settings.");
            app.IsUiPreviewMode = false;
            try
            {
                colorPicker.SelectedValue = "white";
                InteractionAssert(frameManager.SelectedFrameColor == "white" &&
                    store.Load().DeviceFrameColors[device.Udid] == "white" &&
                    (string?)Find<ComboBox>("ProjectionFrameColorComboBox").SelectedValue == "white" && !frameManager.HasAnyOpen,
                    "Floating color selection must persist and synchronize with main settings without opening a preview.");
                colorPicker.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                SaveWindowRender(frameSettings, Path.Combine(output, "floating-frame-color.png"));
                SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(frameBlockedPath, "settings.json")));
                colorPicker.SelectedValue = "blue";
                InteractionAssert(frameManager.SelectedFrameColor == "white" && (string?)colorPicker.SelectedValue == "white" &&
                    ((TextBlock)frameSettings.FindName("FrameColorFeedback")).Visibility == Visibility.Visible,
                    "Failed color save must restore the displayed and active color with visible feedback.");
                SetKeyboardField(app, "_settingsStore", store);
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, ["en-US", false, true]);
                InteractionAssert(((DeviceFrameColorOption)colorPicker.SelectedItem).Label == "White" &&
                    ((TextBlock)frameSettings.FindName("PhoneFrameDescription")).Text.Contains("Manually selected body color"),
                    "Open floating settings must refresh color options and the description after a language change.");
                colorPicker.SelectedValue = "";
                InteractionAssert(frameManager.SelectedFrameColor == "" && !store.Load().DeviceFrameColors.ContainsKey(device.Udid) &&
                    !frameManager.HasAnyOpen && ((TextBlock)frameSettings.FindName("FrameColorFeedback")).Visibility == Visibility.Collapsed,
                    "Automatic color must remove the override and clear previous save feedback without opening a preview.");
            }
            finally
            {
                SetKeyboardField(app, "_settingsStore", store);
                app.IsUiPreviewMode = true;
                frameSettings.Close();
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, ["zh-CN", false, true]);
            }
            Console.WriteLine("PASS shared floating/main color selection, persistence, failure rollback and live language refresh.");


            Select("Control");
            var directionActions = Find<StackPanel>("MouseDirectionSettingsActions");
            var sensitivityActions = Find<StackPanel>("BluetoothSensitivitySettingsActions");
            var applyButton = FindVisualDescendants<Button>(directionActions).Single();
            var sensitivityButton = FindVisualDescendants<Button>(sensitivityActions).Single();
            InteractionAssert(FindVisualDescendants<StackPanel>(
                Find<ScrollViewer>("ControlSettingsScrollViewer")).Contains(directionActions) &&
                FindVisualDescendants<StackPanel>(
                Find<ScrollViewer>("ControlSettingsScrollViewer")).Contains(sensitivityActions),
                "Mouse settings actions must scroll within their individual settings groups.");
            var baselineMouse = vm.BluetoothMouseSensitivity;
            app.UpdateSettings.BluetoothMouseSensitivitySchema = 0;
            app.UpdateSettings.BluetoothMouseSettingsSchema = 0;
            app.UpdateSettings.BluetoothMouseDirectionSchema = 0;
            vm.BluetoothMouseSensitivity = baselineMouse == 123 ? 124 : 123;
            vm.BluetoothMouseReverseHorizontal = !app.UpdateSettings.BluetoothMouseReverseHorizontal;
            var blockedPath = Path.Combine(Path.GetFullPath(output), "blocked-settings-parent");
            File.WriteAllText(blockedPath, "An ordinary file cannot be a settings directory.");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blockedPath, "settings.json")));
            app.IsUiPreviewMode = false;
            try { applyButton.Command.Execute(null); }
            finally { app.IsUiPreviewMode = true; }
            InteractionAssert(vm.HasPendingBluetoothMouseSettings && vm.MouseSettingsStatus.Length > 0 &&
                vm.AppliedBluetoothMouseSensitivity == baselineMouse &&
                app.UpdateSettings.BluetoothMouseSensitivitySchema == 0 &&
                app.UpdateSettings.BluetoothMouseSettingsSchema == 0 &&
                app.UpdateSettings.BluetoothMouseDirectionSchema == 0,
                "Failed mouse save changed live input/schema settings or hid its retry/error state.");
            SetKeyboardField(app, "_settingsStore", store);
            app.IsUiPreviewMode = false;
            try { applyButton.Command.Execute(null); }
            finally { app.IsUiPreviewMode = true; }
            InteractionAssert(!vm.HasPendingMouseDirectionSettings && vm.HasPendingBluetoothSensitivitySettings &&
                vm.MouseDirectionSettingsStatus.Length == 0 && vm.SettingsStatus.Length == 0 &&
                store.Load().DeviceControlPreferences[device.Udid].MouseSensitivity == baselineMouse &&
                vm.AppliedBluetoothMouseSensitivity == baselineMouse &&
                store.Load().DeviceControlPreferences[device.Udid].ReverseHorizontal == vm.AppliedBluetoothMouseReverseHorizontal &&
                !applyButton.Command.CanExecute(null) && sensitivityButton.Command.CanExecute(null),
                "Applying direction settings also applied sensitivity or left the wrong group enabled.");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blockedPath, "settings.json")));
            app.IsUiPreviewMode = false;
            try { sensitivityButton.Command.Execute(null); }
            finally { app.IsUiPreviewMode = true; }
            InteractionAssert(vm.HasPendingBluetoothSensitivitySettings && !vm.HasPendingMouseDirectionSettings &&
                vm.BluetoothSensitivitySettingsStatus.Length > 0 && vm.MouseDirectionSettingsStatus.Length == 0 &&
                vm.AppliedBluetoothMouseSensitivity == baselineMouse &&
                app.UpdateSettings.BluetoothMouseDirectionSchema == 0 &&
                app.UpdateSettings.BluetoothMouseSensitivitySchema == 0,
                "Failed sensitivity save changed direction settings or lost its own retry/error state.");
            SetKeyboardField(app, "_settingsStore", store);
            app.IsUiPreviewMode = false;
            try { sensitivityButton.Command.Execute(null); }
            finally { app.IsUiPreviewMode = true; }
            InteractionAssert(!vm.HasPendingBluetoothMouseSettings && vm.MouseSettingsStatus.Length == 0 &&
                vm.BluetoothSensitivitySettingsStatus.Length == 0 &&
                store.Load().DeviceControlPreferences[device.Udid].MouseSensitivity == vm.AppliedBluetoothMouseSensitivity,
                "Sensitivity retry did not apply its own settings or clear its error.");
            Console.WriteLine("PASS inline direction/sensitivity actions, independent saves and failed-save rollback/retry.");
            TestSettingsPersistence(window, app, vm, device, store, blockedPath);
            Select("Projection");
            InteractionAssert(window.FindName("ApplyVideoSettingsButton") is null &&
                window.FindName("IndependentLayoutSaveButton") is null,
                "Picture and startup layout settings must not require redundant Apply/Save buttons.");
            var pictureViewport = Find<ScrollViewer>("ProjectionSettingsScrollViewer");
            var pictureTitle = FindVisualDescendants<TextBlock>(pictureViewport).Single(text =>
                text.Text == LocalizationService.Get("PictureSettingsTitle"));
            pictureTitle.BringIntoView();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            pictureViewport.ScrollToVerticalOffset(pictureViewport.VerticalOffset +
                pictureTitle.TranslatePoint(new Point(), pictureViewport).Y - 8);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            SaveWindowRender(window, Path.Combine(output, "picture-automatic-settings.png"));

            foreach (var culture in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US", "ja-JP" })
            {
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [culture, false, true]);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    Console.WriteLine($"Checking {culture}/{theme}.");
                    ApplyTheme(typeof(App).Assembly, theme);
                    foreach (var category in new[] { "Application", "Projection", "Control" })
                    {
                        Select(category);
                        var viewport = Find<ScrollViewer>(category + "SettingsScrollViewer");
                        TestSettingsScrollbarAlignment(window, viewport);
                        var actionStyle = (Style)window.FindResource("SettingsActionButton");
                        foreach (var action in FindVisualDescendants<Button>(viewport).Where(button =>
                            ReferenceEquals(button.Style, actionStyle) && button.IsVisible && button.ActualWidth > 0))
                        {
                            var parent = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(action);
                            InteractionAssert(Math.Abs(action.ActualWidth + action.Margin.Left + action.Margin.Right - parent.ActualWidth) <= 1 &&
                                action.ActualHeight >= 40,
                                $"{culture}/{theme}/{category}: settings action button does not fill its row or is too short: {action.Content}.");
                            if (System.Windows.Data.BindingOperations.GetBinding(action, Button.CommandProperty)?.Path.Path == "ApplyWirelessSettingsCommand")
                            {
                                var input = FindVisualDescendants<TextBox>(parent).Single();
                                var inputBounds = input.TransformToAncestor(parent).TransformBounds(new Rect(input.RenderSize));
                                var buttonBounds = action.TransformToAncestor(parent).TransformBounds(new Rect(action.RenderSize));
                                InteractionAssert(Math.Abs(input.ActualWidth - action.ActualWidth) <= 1 &&
                                    Math.Abs(input.ActualHeight - 40) <= 1 &&
                                    buttonBounds.Top >= inputBounds.Bottom + 7,
                                    "The wireless name and Apply button must use equal-width separate rows with an 8px gap.");
                            }
                        }
                        InteractionAssert(viewport.ViewportWidth > 240 && viewport.ScrollableWidth < 1,
                            $"Settings overflow horizontally: {culture}/{theme}/{category}.");
                        var header = Find<RadioButton>(category + "SettingsTab");
                        var texts = FindVisualDescendants<TextBlock>(header).ToArray();
                        InteractionAssert(texts.Length > 0 && texts.All(t => t.ActualHeight >= t.DesiredSize.Height - 1),
                            $"A category label is clipped: {culture}/{theme}/{category}.");
                        if (culture == "zh-CN")
                            SaveWindowRender(window, Path.Combine(output, $"settings-{category.ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}.png"));
                    }
                }
            }

            // Independent scroll viewers preserve position while the header stays outside them.
            Select("Application");
            window.Height = 520;
            window.UpdateLayout();
            var scroll = Find<ScrollViewer>("ApplicationSettingsScrollViewer");
            scroll.ScrollToEnd();
            window.UpdateLayout();
            var offset = scroll.VerticalOffset;
            Select("Control");
            Select("Application");
            InteractionAssert(Math.Abs(scroll.VerticalOffset - offset) < 1,
                "Switching categories lost its scroll position.");
            var headerBefore = Find<RadioButton>("ApplicationSettingsTab").TranslatePoint(new Point(), window);
            scroll.ScrollToHome();
            window.UpdateLayout();
            var headerAfter = Find<RadioButton>("ApplicationSettingsTab").TranslatePoint(new Point(), window);
            InteractionAssert(headerBefore == headerAfter, "The category header scrolled with settings.");

            foreach (var category in new[] { "Application", "Projection", "Control" })
            {
                Select(category);
                TestSettingsScrollbarAlignment(window, Find<ScrollViewer>(category + "SettingsScrollViewer"));
            }

            Console.WriteLine("PASS settings categories: exclusive pages, fallback, save/load/clone, reopen, picture shortcut, stable header, retained scroll and five languages in both themes.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            CloseWorkspaceTestWindow(window);
            app.RestoreUpdateSettings(original);
            app.Shutdown();
        }
    }

    private static void TestIndependentWindowSettings(MainWindow window, App app, MainViewModel vm,
        DeviceViewModel device, UpdateSettingsStore store)
    {
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var priorPreviewMode = app.IsUiPreviewMode;
        app.IsUiPreviewMode = false;
        try
        {
            KeyboardCall(window, "RefreshIndependentWindowLayoutEditor");
            var mode = Find<ComboBox>("IndependentLayoutModeSelector");
            mode.SelectedItem = mode.Items.OfType<ComboBoxItem>()
                .Single(item => Equals(item.Tag, IndependentWindowLayoutMode.Custom));
            var fields = Find<StackPanel>("IndependentLayoutCustomFields");
            InteractionAssert(fields.IsEnabled && fields.Visibility == Visibility.Visible,
                "Selecting a phone must enable its startup layout fields.");
            Find<TextBox>("IndependentLayoutX").Text = "-1200";
            Find<TextBox>("IndependentLayoutY").Text = "180";
            var customY = Find<TextBox>("IndependentLayoutY");
            customY.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, Environment.TickCount, customY, mode)
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            var expected = new IndependentWindowLayout(-1200, 180);
            var saved = store.Load();
            var clone = app.UpdateSettings.Clone();
            InteractionAssert(saved.IndependentWindowStartupMode == IndependentWindowLayoutMode.Custom &&
                clone.IndependentWindowStartupMode == IndependentWindowLayoutMode.Custom &&
                saved.IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(device.Udid.ToUpperInvariant())
                    is { AutoFix: null } history && history.CustomLayout == expected &&
                clone.IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(device.Udid)?.CustomLayout == expected,
                "Layout values or case-insensitive per-device lookups did not survive save/load/clone.");
            mode.BringIntoView();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            var layoutViewport = Find<ScrollViewer>("ProjectionSettingsScrollViewer");
            layoutViewport.ScrollToVerticalOffset(layoutViewport.VerticalOffset +
                mode.TranslatePoint(new Point(), layoutViewport).Y - 70);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            SaveWindowRender(window, Path.Combine(Path.GetDirectoryName(
                Path.GetFullPath((string)KeyboardField(store, "_path")))!, "layout-automatic-save.png"));
            Find<TextBox>("IndependentLayoutWidth").Text = "500";
            var customWidth = Find<TextBox>("IndependentLayoutWidth");
            customWidth.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, Environment.TickCount, customWidth, mode)
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            InteractionAssert(Find<TextBlock>("IndependentLayoutStatus").Text == LocalizationService.Get("IndependentWindowLayoutInvalid") &&
                store.Load().IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(device.Udid)?.CustomLayout == expected,
                "An incomplete width/height pair changed the saved layout.");
            SetKeyboardField(vm, "_selectedDevice", DeviceViewModel.CreateMediaCast());
            KeyboardCall(window, "RefreshIndependentWindowLayoutEditor");
            InteractionAssert(mode.IsEnabled && Equals(mode.SelectedValue, IndependentWindowLayoutMode.Custom) &&
                !Find<StackPanel>("IndependentLayoutCustomFields").IsEnabled &&
                Find<TextBox>("IndependentLayoutX").Text.Length == 0, "The device layout leaked to another source.");
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(window, "RefreshIndependentWindowLayoutEditor");
            InteractionAssert(Find<TextBox>("IndependentLayoutX").Text == "-1200", "Switching back lost the saved layout.");

            InteractionAssert(window.FindName("PreviewHotkeysAllow") is null && window.FindName("PreviewHotkeysBlock") is null,
                "The Windows hotkey lists must only appear in the shortcut editor.");
        }
        finally
        {
            SetKeyboardField(vm, "_selectedDevice", device);
            app.IsUiPreviewMode = priorPreviewMode;
        }
    }
}
