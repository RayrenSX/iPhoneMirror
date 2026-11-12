using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunInterfaceDescriptionsTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        AboutWindow? about = null;
        ShortcutSettingsWindow? shortcuts = null;
        try
        {
            var vm = (MainViewModel)KeyboardField(owner, "_viewModel");
            var check = (CheckBox)owner.FindName("HideInterfaceDescriptionsCheckBox");
            about = new AboutWindow(app, vm) { Owner = owner };
            shortcuts = new ShortcutSettingsWindow(new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(), _ => null)
                { Owner = owner };
            about.Show(); shortcuts.Show();
            var mainDescriptionKeys = new[] { "AppSubtitleConnectivity",
                "ProjectionSettingsDescription", "UsbProjectionPerDeviceHint", "WirelessResolutionHelp", "WirelessRaopPortHelp", "MediaCastDescription", "RenderLimitHelp",
                "VideoPipelinePreferenceHelp", "MirroringAutoControlDescription", "MappingDescription", "SettingsScopeMouseDirection", "BluetoothMouseDirectionHelp", "KeepUnlocked" };
            IEnumerable<TextBlock> DescriptionTexts(DependencyObject root)
            {
                var seen = new HashSet<DependencyObject>();
                var pending = new Stack<DependencyObject>();
                pending.Push(root);
                while (pending.TryPop(out var node))
                {
                    if (!seen.Add(node)) continue;
                    if (node is TextBlock text) yield return text;
                    foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) pending.Push(child);
                    if (node is System.Windows.Media.Visual)
                        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
                            pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, index));
                }
            }
            void AssertResourceDescriptions(DependencyObject root, IEnumerable<string> keys, bool hidden)
            {
                foreach (var key in keys)
                {
                    var value = (string)app.FindResource(key);
                    var texts = DescriptionTexts(root).Where(text => text.Text == value).ToArray();
                    InteractionAssert(texts.Length > 0, $"Description not exercised: {key}.");
                    foreach (var text in texts)
                        InteractionAssert(text.Visibility == (hidden ? Visibility.Collapsed : Visibility.Visible),
                            $"{key}: description visibility does not follow the preference.");
                }
            }
            var names = new[] { "DeviceUnlockDescription", "ResolutionDescription", "FrameRateDescription", "LatencyDescription", "AudioDescription" };
            void AssertDescriptions(bool hidden)
            {
                DrainDispatcher(); owner.UpdateLayout();
                foreach (var name in names)
                {
                    var text = (TextBlock)owner.FindName(name);
                    InteractionAssert(text.Visibility == (hidden ? Visibility.Collapsed : Visibility.Visible),
                        $"{name}: incorrect visibility for HideInterfaceDescriptions={hidden}.");
                    if (hidden) InteractionAssert(text.DesiredSize.Height == 0, $"{name}: hidden description still reserves height.");
                }
                AssertResourceDescriptions(owner, mainDescriptionKeys, hidden);
                AssertResourceDescriptions(about!, new[] { "AboutSubtitle", "AboutDescription", "UpdateSettingsDescription", "DiagnosticsDescription" }, hidden);
                AssertResourceDescriptions(shortcuts!, new[] { "ShortcutSettingsDescription", "SettingShortcutsDescription", "ShortcutSettingsCaptureHint" }, hidden);
                InteractionAssert(((FrameworkElement)owner.FindName("DetectionStatus")).Visibility == (hidden ? Visibility.Collapsed : Visibility.Visible),
                    "The driver header did not follow the description preference.");
                foreach (var key in new[] { "ApplicationModeLabel", "ThemeTitle", "LanguageToolTip", "MirroringAutoControlTitle" })
                    foreach (var text in Visuals(owner).OfType<TextBlock>().Where(text => text.Text == (string)app.FindResource(key)))
                        InteractionAssert(text.Visibility == Visibility.Visible, $"A required field label was hidden: {key}.");
            }
            InteractionAssert(vm.HideInterfaceDescriptions && JsonSerializer.Deserialize<UpdateSettings>("{}")!.HideInterfaceDescriptions,
                "New/legacy settings must preserve the current compact appearance.");
            KeyboardCall(owner, "SetSettingsPanelVisible", true);
            ((RadioButton)owner.FindName("ApplicationSettingsTab")).IsChecked = true;
            app.IsUiPreviewMode = false;
            owner.MinWidth = 600;
            check.IsChecked = false;
            foreach (var hidden in new[] { true, false })
            {
                check.IsChecked = hidden;
                owner.Width = 900;
                owner.UpdateLayout(); DrainDispatcher();
                InteractionAssert(((FrameworkElement)owner.FindName("WorkspaceSubtitle")).Visibility ==
                    (hidden ? Visibility.Collapsed : Visibility.Visible),
                    "Resizing must preserve the user's subtitle visibility preference.");
                owner.Width = 1724;
                owner.UpdateLayout(); DrainDispatcher();
                AssertDescriptions(hidden);
            }
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                foreach (var hidden in new[] { true, false, true })
                {
                    check.IsChecked = hidden;
                    AssertDescriptions(hidden);
                    InteractionAssert(store.Load().HideInterfaceDescriptions == hidden &&
                        app.UpdateSettings.Clone().HideInterfaceDescriptions == hidden,
                        "Description preference did not survive save/load or clone.");
                    SaveWindowRender(owner, Path.Combine(output, $"{theme}-{(hidden ? "hidden" : "visible")}.png"));
                }
            }
            check.IsChecked = false;
            KeyboardCall(owner, "ToggleFullScreen");
            KeyboardCall(owner, "ToggleFullScreen");
            AssertDescriptions(false);
            var blocked = Path.Combine(Path.GetFullPath(output), "blocked");
            File.WriteAllText(blocked, "not a directory");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocked, "settings.json")));
            check.IsChecked = true;
            DrainDispatcher();
            InteractionAssert(!vm.HideInterfaceDescriptions && check.IsChecked == false && vm.ApplicationSettingsStatus.Length > 0,
                "Save failure did not restore the checkbox and report the error.");
            AssertDescriptions(false);
            SetKeyboardField(app, "_settingsStore", store);
            check.IsChecked = true;
            AssertDescriptions(true);
            InteractionAssert(vm.ApplicationSettingsStatus.Length == 0, "Successful retry did not clear the save error.");
            var values = Visuals(owner.FindName("StatisticsItems") as DependencyObject ?? owner)
                .OfType<TextBlock>().ToDictionary(t => t, System.Windows.Automation.AutomationProperties.GetAutomationId);
            TextBlock Value(string id) => values.Single(pair => pair.Value == id).Key;
            var metrics = new[] { Value("ResolutionValue"), Value("FrameRateValue"), Value("LatencyValue"), Value("AudioValue") };
            InteractionAssert(metrics.All(metric => metric.Text == "—"), "Idle statistics must share the same placeholder.");
            var captureStatus = new NativeCaptureStatus
            {
                State = CaptureState.Streaming, Width = 1082, Height = 2340, Fps = 58.7,
                LatencyMs = 1.8, AudioSampleRate = 48000, AudioChannels = 2, Message = string.Empty,
            };
            KeyboardCall(vm, "ApplyCaptureStatus", captureStatus);
            owner.UpdateLayout();
            InteractionAssert(metrics.Select(metric => metric.Text).SequenceEqual(new[] { "1082×2340", $"{58.7:F1}", $"{1.8:F1}", "48 · 2" }),
                "Active statistics must show values without repeating units.");
            SaveWindowRender(owner, Path.Combine(output, "statistics-active.png"));
            captureStatus.State = CaptureState.Stopped;
            KeyboardCall(vm, "ApplyCaptureStatus", captureStatus);
            owner.UpdateLayout();
            InteractionAssert(metrics.All(metric => metric.Text == "—"), "Stopped capture must not retain old statistics.");
            KeyboardCall(vm, "ResetPreviewState");
            owner.UpdateLayout();
            InteractionAssert(metrics.All(metric => metric.Text == "—"), "Stopping capture must reset all four statistics.");
            Console.WriteLine("PASS: all four statistics share the idle/reset placeholder and show unitless capture values.");
            Console.WriteLine("PASS: main/settings/child-window descriptions and driver header toggle live; labels retained; resize, legacy/clone/persistence, both themes, full screen and save-failure rollback/retry.");
            return 0;
        }
        finally { app.IsUiPreviewMode = true; shortcuts?.Close(); about?.Close(); CloseWorkspaceTestWindow(owner); app.Shutdown(); }
    }
}
