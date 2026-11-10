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
            KeyboardCall(window, "OnOpenProjectionSettingsClick", window, new RoutedEventArgs());
            Console.WriteLine("Picture shortcut verified.");
            InteractionAssert(Find<RadioButton>("ProjectionSettingsTab").IsChecked == true,
                "The picture shortcut did not open projection settings.");

            // Use a synthetic, offline source; no discovery, capture or reverse control starts.
            var vm = (MainViewModel)KeyboardField(window, "_viewModel");
            var device = DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = "settings-layout-fixture", Name = "iPhone · Preview", State = ConnectionState.Ready });
            SetKeyboardField(vm, "_selectedDevice", device);
            Find<Border>("ControlPanelContent").DataContext = null;
            Find<Border>("ControlPanelContent").DataContext = vm;
            Console.WriteLine("Offline fixture ready.");

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
}
