using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunProxySettingsTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY", Path.Combine(output, "logs"));
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = new UpdateSettingsStore(Path.Combine(output, "settings.json"));
        // Install the isolated store before WPF can dispatch startup or any save.
        SetKeyboardField(app, "_settingsStore", store);
        app.InitializeComponent();
        var originalLanguage = LocalizationService.SelectedLanguage;
        var runningMode = AppProxyPolicy.CurrentMode;
        MainWindow? window = null;
        try
        {
            app.RestoreUpdateSettings(new UpdateSettings { NetworkProxy = new AppProxySettings() });
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            store.Save(app.UpdateSettings);
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);

            T Find<T>(string name) where T : FrameworkElement =>
                (T)(window.FindName(name) ?? throw new InvalidOperationException("Missing proxy test control: " + name));
            ComboBox Mode() => Find<ComboBox>("NetworkProxyModeComboBox");
            TextBox Address() => Find<TextBox>("NetworkProxyAddressTextBox");
            TextBlock Feedback() => Find<TextBlock>("NetworkProxyFeedback");
            Button SaveButton() => FindVisualDescendants<Button>(Find<Border>("NetworkProxySettingsCard"))
                .Single(button => AutomationProperties.GetAutomationId(button) == "SaveNetworkProxyButton");
            void CheckRunningPolicy() => InteractionAssert(AppProxyPolicy.CurrentMode == runningMode,
                "Saving proxy preferences changed the running network policy before restart.");
            void Save()
            {
                // The only non-preview operation is the synchronous write to our temporary store.
                app.IsUiPreviewMode = false;
                try { SaveButton().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                finally { app.IsUiPreviewMode = true; }
                CheckRunningPolicy();
            }
            void CheckFeedback(string resource) => InteractionAssert(Feedback().Visibility == Visibility.Visible &&
                Feedback().Text == LocalizationService.Get(resource), "Missing proxy feedback: " + resource);
            void Select(string category)
            {
                Find<RadioButton>(category + "SettingsTab").IsChecked = true;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                window.UpdateLayout();
                foreach (var name in new[] { "Application", "Projection", "Control" })
                    InteractionAssert(Find<ScrollViewer>(name + "SettingsScrollViewer").Visibility ==
                        (name == category ? Visibility.Visible : Visibility.Collapsed),
                        "Proxy settings changed the three independent settings categories.");
            }
            void OpenApplication()
            {
                KeyboardCall(window, "OnCloseDevicePanelClick", window, new RoutedEventArgs());
                KeyboardCall(window, "SetSettingsPanelVisible", true);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(450));
                Select("Application");
            }

            OpenApplication();
            var card = Find<Border>("NetworkProxySettingsCard");
            foreach (var category in new[] { "Application", "Projection", "Control" })
            {
                var scroll = Find<ScrollViewer>(category + "SettingsScrollViewer");
                InteractionAssert(FindVisualDescendants<Border>(scroll).Contains(card) == (category == "Application") &&
                    !FindVisualDescendants<Expander>(scroll).Any(),
                    "Proxy settings must be a direct application group without new or collapsed categories.");
                Select(category);
            }
            Select("Application");
            InteractionAssert(Mode().Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag)
                .SequenceEqual(new[] { "Default", "Direct", "Manual" }), "Unexpected proxy mode choices.");
            foreach (var mode in new[] { "Default", "Direct", "Manual" })
            {
                Mode().SelectedValue = mode;
                InteractionAssert(Address().IsEnabled == (mode == "Manual") && Address().Visibility == Visibility.Visible,
                    "Only manual proxy mode should allow editing the visible address.");
            }
            Console.WriteLine("PASS proxy settings remain directly in application settings; all three modes gate address editing.");

            var beforeInvalid = File.ReadAllText(Path.Combine(output, "settings.json"));
            foreach (var invalid in new[] { "", "127.0.0.1:0", "http://user:secret@127.0.0.1:7897", "http://127.0.0.1:7897/path" })
            {
                Address().Text = invalid;
                Save();
                CheckFeedback("NetworkProxyInvalidAddress");
                InteractionAssert(app.UpdateSettings.NetworkProxy.Mode == AppProxyMode.Default &&
                    File.ReadAllText(Path.Combine(output, "settings.json")) == beforeInvalid,
                    "An invalid manual proxy changed saved preferences.");
            }
            Address().Text = " 127.0.0.1:18081 ";
            InteractionAssert(Feedback().Visibility == Visibility.Collapsed, "Editing a proxy draft did not clear the old error.");
            Save();
            CheckFeedback("NetworkProxySaved");
            var saved = store.Load().NetworkProxy;
            InteractionAssert(saved.Mode == AppProxyMode.Manual && saved.Address == "http://127.0.0.1:18081" &&
                Address().Text == saved.Address, "Manual proxy save did not normalize and reload its address.");
            var clone = app.UpdateSettings.Clone();
            InteractionAssert(!ReferenceEquals(clone.NetworkProxy, app.UpdateSettings.NetworkProxy),
                "Cloning application settings aliased the proxy preferences.");
            clone.NetworkProxy.Address = "http://127.0.0.1:18090";
            InteractionAssert(app.UpdateSettings.NetworkProxy.Address == saved.Address &&
                store.Load().NetworkProxy.Address == saved.Address, "Editing a cloned proxy changed the saved settings.");

            foreach (var mode in new[] { AppProxyMode.Direct, AppProxyMode.Default })
            {
                Mode().SelectedValue = mode.ToString();
                Save();
                CheckFeedback("NetworkProxySaved");
                InteractionAssert(app.UpdateSettings.NetworkProxy.Mode == mode && store.Load().NetworkProxy.Mode == mode &&
                    !Address().IsEnabled, "Proxy mode did not survive save/load or left its address editable.");
            }
            Mode().SelectedValue = "Manual";
            Save();
            var committed = app.UpdateSettings.NetworkProxy;
            var blockedParent = Path.Combine(output, "blocked-settings-parent");
            File.WriteAllText(blockedParent, "An ordinary file cannot contain settings.");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blockedParent, "settings.json")));
            Address().Text = "127.0.0.1:18082";
            try
            {
                Save();
                CheckFeedback("NetworkProxySaveFailed");
                InteractionAssert(ReferenceEquals(app.UpdateSettings.NetworkProxy, committed) &&
                    store.Load().NetworkProxy.Address == "http://127.0.0.1:18081" &&
                    Address().Text == "127.0.0.1:18082" && (string)Mode().SelectedValue == "Manual",
                    "A failed proxy save lost its draft, changed committed settings or failed to roll back.");
            }
            finally { SetKeyboardField(app, "_settingsStore", store); }
            Save();
            CheckFeedback("NetworkProxySaved");
            InteractionAssert(store.Load().NetworkProxy.Address == "http://127.0.0.1:18082",
                "Retrying the failed proxy save did not commit the draft.");
            Console.WriteLine("PASS proxy validation, three-mode disk roundtrip, deep clone, failed-save rollback and retry; running policy unchanged.");

            CloseWorkspaceTestWindow(window);
            window = null;
            app.RestoreUpdateSettings(store.Load());
            window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            OpenApplication();
            InteractionAssert((string)Mode().SelectedValue == "Manual" && Address().IsEnabled &&
                Address().Text == "http://127.0.0.1:18082", "Recreating settings did not restore the saved proxy preferences.");
            var renders = new List<object>();
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var width in new[] { 760d, 1180d })
            {
                typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [language, false, true]);
                window.Width = width;
                window.Height = 900;
                Save();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                var scroll = Find<ScrollViewer>("ApplicationSettingsScrollViewer");
                card = Find<Border>("NetworkProxySettingsCard");
                card.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                window.UpdateLayout();
                var bounds = card.TransformToAncestor(scroll).TransformBounds(new Rect(card.RenderSize));
                InteractionAssert(scroll.ScrollableWidth < 1 && bounds.Left >= -1 && bounds.Right <= scroll.ActualWidth + 1 &&
                    bounds.Top >= -1 && bounds.Bottom <= scroll.ActualHeight + 1 &&
                    Mode().ActualWidth > 200 && Address().ActualWidth > 200 && SaveButton().ActualWidth > 200,
                    $"Proxy settings overflowed or did not fit the viewport: {language}/{width}.");
                CheckFeedback("NetworkProxySaved");
                InteractionAssert(FindVisualDescendants<TextBlock>(card).Any(text =>
                    text.Text == LocalizationService.Get("NetworkProxyRestartHelp")), "Proxy restart guidance is missing.");
                var path = Path.Combine(output, $"proxy-settings-{language}-{width:0}.png");
                SaveWindowRender(window, path);
                renders.Add(new { language, requestedWidth = width, actualWidth = window.ActualWidth,
                    viewportWidth = scroll.ViewportWidth, cardWidth = card.ActualWidth, cardHeight = card.ActualHeight, path });
            }
            CheckRunningPolicy();
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                description = "Proxy application-settings controls, isolated persistence, validation, rollback, restart-only policy and rebuilt-window localization/layout.",
                runningMode = runningMode.ToString(), savedMode = store.Load().NetworkProxy.Mode.ToString(),
                settingsPath = Path.Combine(output, "settings.json"), renders,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS proxy settings reload in a new window and render Chinese/English at narrow and wide widths.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            SetKeyboardField(app, "_settingsStore", store);
            if (window is not null) CloseWorkspaceTestWindow(window);
            typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [originalLanguage, false, true]);
            app.Shutdown();
        }
    }
}
