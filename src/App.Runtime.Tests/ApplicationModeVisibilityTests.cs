using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunApplicationModeVisibilityTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var selector = (ComboBox)window.FindName("ApplicationDisplayModeComboBox");
        var checks = new[] { "HideInterfaceDescriptionsCheckBox", "HidePreviewToolbarCheckBox", "HidePreviewStatusBarCheckBox" }
            .Select(name => (CheckBox)window.FindName(name)).ToArray();
        var hint = (TextBlock)window.FindName("ApplicationModeVisibilityHint");
        void Layout() { DrainDispatcher(); window.UpdateLayout(); }
        void Select(ApplicationDisplayMode mode) { selector.SelectedValue = mode; Layout(); }
        bool[] Saved(UpdateSettings settings) => [settings.HideInterfaceDescriptions, settings.HidePreviewToolbar, settings.HidePreviewStatusBar];
        void Check(bool[] expected, bool editable)
        {
            Layout();
            InteractionAssert(checks.Select(check => check.IsChecked == true).SequenceEqual(expected) &&
                checks.All(check => check.IsEnabled == editable) && vm.CanCustomizeInterfaceVisibility == editable,
                "Checkbox states or availability disagree with the selected mode.");
            InteractionAssert((Visibility)app.FindResource("InterfaceDescriptionsVisibility") ==
                (expected[0] ? Visibility.Collapsed : Visibility.Visible), "Descriptions disagree with their checkbox.");
            foreach (var (name, hidden) in new[] { ("EnvironmentPanel", expected[1]), ("StatsPanel", expected[2]) })
                InteractionAssert(((FrameworkElement)window.FindName(name)).Visibility ==
                    (hidden ? Visibility.Collapsed : Visibility.Visible), $"{name} disagrees with its checkbox.");
            InteractionAssert(hint.Visibility == (editable ? Visibility.Collapsed : Visibility.Visible) &&
                (editable || hint.Text.Length > 0), "The mode-controlled options have no explanation.");
        }
        try
        {
            KeyboardCall(window, "SetSettingsPanelVisible", true); FinishWorkspaceReveal(window);
            ((RadioButton)window.FindName("ApplicationSettingsTab")).IsChecked = true; Layout();
            var theme = (FrameworkElement)window.FindName("ThemeComboBox");
            var themeBottom = theme.TranslatePoint(new Point(0, theme.ActualHeight), window).Y;
            InteractionAssert(selector.TranslatePoint(new Point(), window).Y > themeBottom &&
                checks[0].TranslatePoint(new Point(), window).Y > selector.TranslatePoint(new Point(0, selector.ActualHeight), window).Y,
                "Application mode is not between theme and the visibility options.");
            app.IsUiPreviewMode = false;
            for (var mask = 0; mask < 8; mask++)
            {
                Select(ApplicationDisplayMode.Complete);
                var preferences = Enumerable.Range(0, 3).Select(bit => (mask & (1 << bit)) != 0).ToArray();
                for (var i = 0; i < checks.Length; i++) checks[i].IsChecked = preferences[i];
                Check(preferences, true);
                foreach (var mode in new[] { ApplicationDisplayMode.Lightweight, ApplicationDisplayMode.Tray })
                {
                    Select(mode); Check([true, true, true], false);
                    InteractionAssert(Saved(store.Load()).SequenceEqual(preferences) && Saved(app.UpdateSettings.Clone()).SequenceEqual(preferences),
                        "A mode switch replaced the saved complete-mode preferences.");
                    vm.HideInterfaceDescriptions = false; vm.HidePreviewToolbar = false; vm.HidePreviewStatusBar = false;
                    Check([true, true, true], false);
                    if (mask == 0 && mode == ApplicationDisplayMode.Lightweight)
                        SaveWindowRender(window, Path.Combine(output, "lightweight-controlled.png"));
                    Select(ApplicationDisplayMode.Complete); Check(preferences, true);
                }
                Console.WriteLine($"PASS saved options {mask}: lightweight/tray effective state, lock, persistence and complete-mode restoration.");
            }
            Select(ApplicationDisplayMode.Lightweight);
            var reloaded = store.Load(); app.RestoreUpdateSettings(reloaded);
            var fresh = new MainViewModel();
            try
            {
                InteractionAssert(fresh.HideInterfaceDescriptions && fresh.HidePreviewToolbar && fresh.HidePreviewStatusBar &&
                    !fresh.CanCustomizeInterfaceVisibility, "Reloading lightweight mode did not restore its effective visibility.");
            }
            finally { WaitReviewTask(fresh.ShutdownAsync()); }
            Select(ApplicationDisplayMode.Complete); Check([true, true, true], true);
            checks[0].IsChecked = checks[1].IsChecked = checks[2].IsChecked = false;
            Check([false, false, false], true);
            SaveWindowRender(window, Path.Combine(output, "complete-editable.png"));
            KeyboardCall(vm, "EnterTrayOnMainWindowClose"); Layout(); Check([true, true, true], false);
            InteractionAssert(store.Load().ApplicationDisplayMode == ApplicationDisplayMode.Complete,
                "Temporary close-to-tray changed the startup mode.");
            KeyboardCall(vm, "RestoreMainWindowFromTray"); Layout(); Check([false, false, false], true);
            var blocked = Path.Combine(Path.GetFullPath(output), "blocked"); File.WriteAllText(blocked, "not a directory");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocked, "settings.json")));
            Select(ApplicationDisplayMode.Lightweight); Check([false, false, false], true);
            InteractionAssert(vm.SelectedApplicationDisplayMode == ApplicationDisplayMode.Complete &&
                (ApplicationDisplayMode)selector.SelectedValue == ApplicationDisplayMode.Complete && vm.ApplicationSettingsStatus.Length > 0,
                "Failed mode save left checkbox overrides, selection or lock active.");
            SetKeyboardField(app, "_settingsStore", store);
            Select(ApplicationDisplayMode.Lightweight); Check([true, true, true], false);
            InteractionAssert(vm.ApplicationSettingsStatus.Length == 0, "A mode save retry retained its error.");
            foreach (var language in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US", "ja-JP" })
            {
                LocalizationService.SetLanguage(language); Layout();
                InteractionAssert(hint.Text == (string)app.FindResource("ApplicationModeLightweightVisibilityHint"),
                    "The mode explanation did not follow the selected language.");
            }
            LocalizationService.SetLanguage("zh-CN");
            Console.WriteLine("PASS: theme/mode/options order, reload, temporary tray, failed-save rollback/retry and five-language hints.");
            return 0;
        }
        finally { app.IsUiPreviewMode = true; CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
