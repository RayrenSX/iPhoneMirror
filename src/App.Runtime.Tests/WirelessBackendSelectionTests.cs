using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWirelessBackendSelectionTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        // Exercise the original-quality option without opening its confirmation dialog.
        vm.SetupActive = true;
        var floating = new ProjectionSettingsWindow(vm,
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.CompletedTask, () => Task.CompletedTask, () => { }, previewOnly: true);
        var language = LocalizationService.SelectedLanguage;
        var theme = ThemeService.Preference;
        try
        {
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            floating.Show();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            ComboBox[] Selectors(string path) => new DependencyObject[] { window, floating }
                .SelectMany(FindVisualDescendants<ComboBox>)
                .Where(combo => ReferenceEquals(combo.DataContext, vm) &&
                    BindingOperations.GetBinding(combo, ComboBox.SelectedItemProperty)?.Path.Path == path)
                .ToArray();
            var backends = Selectors(nameof(vm.SelectedWirelessReceiverBackend));
            var profiles = Selectors(nameof(vm.SelectedWirelessDisplayProfile));
            InteractionAssert(backends.Length >= 2 && profiles.Length >= 2,
                "Both the main and floating settings must participate in the binding test.");
            var changes = 0;
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            foreach (var appearance in new[] { AppTheme.Dark, AppTheme.Light })
            {
                SetAuditCulture(culture);
                ThemeService.Apply(appearance);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(15));
                foreach (var profile in WirelessReceiverConfiguration.DisplayProfiles)
                foreach (var profileSelector in profiles)
                {
                    profileSelector.SelectedItem = profile;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
                    foreach (var backendSelector in backends)
                    foreach (var backend in WirelessReceiverConfiguration.BackendOptions.Concat(
                        WirelessReceiverConfiguration.BackendOptions.Reverse()))
                    {
                        backendSelector.SelectedItem = backend;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
                        InteractionAssert(ReferenceEquals(vm.SelectedWirelessReceiverBackend, backend) &&
                            backends.All(combo => ReferenceEquals(combo.SelectedItem, backend)) &&
                            ReferenceEquals(vm.SelectedWirelessDisplayProfile, profile) &&
                            profiles.All(combo => ReferenceEquals(combo.SelectedItem, profile)),
                            $"{culture}/{appearance}/{profile.Id}: changing the backend changed resolution or desynchronized settings.");
                        changes++;
                    }
                }
            }
            // Apply uses these exact selected objects. Verify their persistence and startup restoration
            // without opening a receiver or modifying the user's preferences.
            app.IsUiPreviewMode = false;
            foreach (var profile in WirelessReceiverConfiguration.DisplayProfiles)
            foreach (var backend in WirelessReceiverConfiguration.BackendOptions)
            {
                vm.SelectedWirelessDisplayProfile = profile;
                vm.SelectedWirelessReceiverBackend = backend;
                InteractionAssert((bool)KeyboardCall(vm, "PersistWirelessReceiverSettings",
                    backend.Backend, profile, "Issue 88 regression")!, "Wireless settings did not save.");
                var saved = store.Load();
                InteractionAssert(saved.WirelessDisplayProfileId == profile.Id &&
                    saved.WirelessReceiverBackend == backend.Backend,
                    "Applying a different backend wrote a different resolution.");
                app.RestoreUpdateSettings(saved);
                var restored = new MainViewModel();
                InteractionAssert(restored.SelectedWirelessReceiverBackend.Backend == backend.Backend &&
                    ReferenceEquals(restored.SelectedWirelessDisplayProfile, profile),
                    "Restarting with the saved backend changed resolution.");
            }
            Console.WriteLine($"PASS wireless backend selection: {changes} real dropdown changes, four resolutions, main/floating settings, five languages, two themes and save/restart roundtrips.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            floating.Close(); window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
    }
}
