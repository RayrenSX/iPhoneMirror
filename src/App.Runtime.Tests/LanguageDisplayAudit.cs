using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private const BindingFlags DisplayInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags DisplayStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static Type DisplayType(string name) => typeof(App).Assembly.GetType("IPhoneMirror.App." + name, true)!;
    private static object? DisplayCall(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, DisplayInstance)!.Invoke(target, args);
    private static object? DisplayGet(object target, string name) => target.GetType().GetProperty(name, DisplayInstance)!.GetValue(target);
    private static string DisplayL(string key) => (string)DisplayType("Localization.LocalizationService").GetMethod("Get", DisplayStatic)!.Invoke(null, [key])!;
    private static string DisplayF(string key, params object?[] args) => (string)DisplayType("Localization.LocalizationService").GetMethod("Format", DisplayStatic)!.Invoke(null, [key, args])!;
    private static string DisplayRefresh(string value) => (string)DisplayType("Localization.LocalizationService").GetMethod("RefreshText", DisplayStatic)!.Invoke(null, [value])!;
    private static void DisplayLanguage(string language) => DisplayType("Localization.LocalizationService").GetMethod("ApplyLanguage", DisplayStatic)!.Invoke(null, [language, false, true]);

    private static int RunLanguageDisplayAudit(string output, string? onlySurface = null)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", DisplayInstance)!.SetValue(app, true);
        app.InitializeComponent();
        var failures = new List<string>();
        var results = new List<object>();
        var checks = 0;
        using var trace = new AuditTraceListener();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        void Verify(bool condition, string message)
        {
            checks++;
            if (!condition) failures.Add(message);
        }
        try
        {
            TestDisplayTextOrigins(Verify);
            TestControlDisplaySwitching(Verify);
            TestStartupDisplayFallback(app, Verify);
            TestBridgeOutputDisplay(Verify);
            TestProtectedOverlayDisplay(Verify);
            TestPreviewSettingsIsolation(app, output, Verify);
            Console.WriteLine($"Cached text, control states and startup fallback: {checks} assertions.");
            DisplayLanguage("zh-CN");
            var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            try
            {
                TestMainCachedDisplay(owner.DataContext, Verify);
                TestBluetoothStatusDisplay(owner.DataContext, Verify);
                var open = typeof(MainWindow).GetMethod("OpenDeveloperSurface", DisplayInstance)!;
                foreach (var surface in new[] { "workspace-mirroring", "workspace-devices", "workspace-settings", "workspace-output",
                    "developer-tools", "about", "advanced-settings", "device-binding", "airplay-device-selection",
                    "bluetooth-connection", "bluetooth-client-binding", "bluetooth-control-notice", "shortcut-settings",
                    "reverse-control-status", "prompt", "reverse-control-wired-prerequisite", "reverse-control-wireless-prerequisite",
                    "reverse-control-error", "capture-error", "session-closed", "usb-config-error", "capture-recovery",
                    "image-settings", "projection-settings", "media-output", "usb-mode", "startup-error", "update", "instance-conflict", "protected-content" }.Where(surface => onlySurface is null || surface == onlySurface))
                {
                    Console.WriteLine($"Checking live language switches: {surface}");
                    DisplayLanguage("zh-CN");
                    try
                    {
                        if (surface == "developer-tools")
                        {
                            var developer = (Window)Activator.CreateInstance(DisplayType("Windows.DeveloperToolsWindow"), DisplayInstance, null, [owner], null)!;
                            developer.Owner = owner;
                            developer.Show();
                        }
                        else open.Invoke(owner, [surface]);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        var windows = app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray();
                        if (surface.StartsWith("workspace-")) windows = [owner];
                        Verify(windows.Length > 0, surface + ": no window opened");
                        foreach (var window in windows)
                        {
                            var cachedStatus = new List<(TextBlock Label, string Key)>();
                            foreach (var property in surface switch
                            {
                                "developer-tools" => new[] { "StatusText" },
                                "about" => new[] { "UpdateStatus", "DiagnosticStatus" },
                                "update" => new[] { "StatusText", "UpdateButtonText" },
                                "shortcut-settings" => new[] { "StatusText" },
                                _ => Array.Empty<string>(),
                            })
                            {
                                window.GetType().GetProperty(property, DisplayInstance)!.SetValue(window,
                                    DisplayF("UpdateDownloadFailedFormat", DisplayL("UpdateRequestTimedOut")));
                                cachedStatus.Add((DisplayBinding(window, property), property));
                            }
                            var initial = DisplayDictionary(app);
                            // These are actual rendered WPF labels, not view-model-only values.
                            var labels = FindVisualDescendants<TextBlock>(window).Where(t => t.IsVisible && initial.ContainsKey(t.Text))
                                .Where(t => !IsLanguageSelectionCaption(t))
                                .Select(t => (Label: t, Keys: initial[t.Text])).ToArray();
                            var inputs = FindVisualDescendants<TextBox>(window).Where(t => t.IsVisible && !t.IsReadOnly)
                                .Select(t => (Box: t, Value: t.Text)).ToArray();
                            foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
                            {
                                DisplayLanguage(language);
                                AdvanceDispatcher(TimeSpan.FromMilliseconds(90));
                                foreach (var selector in FindVisualDescendants<ComboBox>(window).Where(c => c.Name == "LanguageComboBox"))
                                    Verify(Equals(selector.SelectedValue, language), $"{surface}/{language}: language selector out of sync");
                                foreach (var (label, property) in cachedStatus)
                                    Verify(label.Text == DisplayF("UpdateDownloadFailedFormat", DisplayL("UpdateRequestTimedOut")),
                                        $"{surface}/{language}: cached error binding stale: {property}");
                                if (surface == "update")
                                {
                                    var viewer = (FlowDocumentScrollViewer)window.FindName("ReleaseNotesViewer");
                                    var text = new System.Windows.Documents.TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text.Trim();
                                    Verify(text == DisplayL("DeveloperPreviewUpdateBody").Trim(), language + ": preview release notes stayed in the previous language");
                                }
                                foreach (var (label, keys) in labels)
                                {
                                    if (!label.IsVisible || PresentationSource.FromVisual(label) is null) continue;
                                    Verify(keys.Any(key => DisplayL(key) == label.Text),
                                        $"{surface}/{language}: stale rendered caption [{string.Join(',', keys)}]: {label.Text}");
                                }
                                foreach (var (box, value) in inputs)
                                    Verify(box.Text == value, $"{surface}/{language}: language change modified an input");
                                AssertVisibleButtonsFit(window);
                                results.Add(new { surface, language, labels = labels.Length, inputs = inputs.Length });
                                if (surface is "about" or "update" or "media-output" or "reverse-control-status" or "startup-error")
                                    SaveWindowRender(window, Path.Combine(output, $"{surface}-{language}.png"));
                            }
                        }
                    }
                    catch (Exception error) { failures.Add(surface + ": " + error.GetBaseException().Message); }
                    finally
                    {
                        foreach (var window in app.Windows.Cast<Window>().Where(w => w != owner).ToArray()) window.Close();
                    }
                }
            }
            finally { CloseWorkspaceTestWindow(owner); }
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            File.WriteAllText(Path.Combine(output, "language-display-results.json"), JsonSerializer.Serialize(
                new { checks, results, failures, bindingErrors = trace.Messages }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Language display audit: {checks} assertions, {results.Count} live window switches, {failures.Count} failures, {trace.Messages.Count} binding diagnostics.");
        return failures.Count == 0 && trace.Messages.Count == 0 ? 0 : 1;
    }

    private static Dictionary<string, string[]> DisplayDictionary(Application app) => app.Resources.MergedDictionaries
        .Where(d => d.Source?.OriginalString.Contains("Localization/Strings.") == true)
        .SelectMany(d => d.Cast<DictionaryEntry>()).Where(e => e.Value is string)
        .GroupBy(e => (string)e.Value!).ToDictionary(g => g.Key, g => g.Select(e => (string)e.Key).ToArray());

    private static bool IsLanguageSelectionCaption(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ComboBox { Name: "LanguageComboBox" }) return true;
        return false;
    }

    private static TextBlock DisplayBinding(object source, string path)
    {
        var label = new TextBlock();
        label.SetBinding(TextBlock.TextProperty, new Binding(path) { Source = source });
        return label;
    }

    private static void TestDisplayTextOrigins(Action<bool, string> verify)
    {
        DisplayLanguage("zh-CN");
        var rawName = new string(DisplayL("Cancel").AsSpan());
        var cached = DisplayF("ReverseControlErrorBodyFormat", DisplayL("ReverseControlTransportWired"), rawName);
        var joined = (string)DisplayType("Localization.LocalizationService").GetMethod("Join", DisplayStatic)!
            .Invoke(null, [Environment.NewLine, new[] { DisplayL("WirelessSettingsReadyImpact"), cached }])!;
        foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
        {
            DisplayLanguage(language);
            verify(DisplayRefresh(rawName) == rawName, "Raw device name matching a resource must remain unchanged");
            verify(DisplayRefresh(cached) == DisplayF("ReverseControlErrorBodyFormat", DisplayL("ReverseControlTransportWired"), rawName),
                language + ": nested resource argument or raw argument changed incorrectly");
            verify(DisplayRefresh(joined) == string.Join(Environment.NewLine, DisplayL("WirelessSettingsReadyImpact"), DisplayRefresh(cached)),
                language + ": joined resource arguments stayed in the previous language");
        }
        var service = DisplayType("Localization.LocalizationService");
        foreach (var (culture, expected) in new[] { ("zh-CN", "zh-CN"), ("zh-SG", "zh-CN"), ("zh-Hans", "zh-CN"),
            ("zh-HK", "zh-HK"), ("zh-TW", "zh-HK"), ("zh-MO", "zh-HK"), ("zh-Hant-TW", "zh-HK"), ("zh-CHT", "zh-HK"),
            ("en-GB", "en-US"), ("ja-JP", "en-US"), ("fr-FR", "en-US") })
            verify((string)service.GetMethod("ResolveCultureName", DisplayStatic)!.Invoke(null, [culture])! == expected,
                culture + ": wrong system-language fallback");
        DisplayLanguage("invalid-language");
        verify((string)service.GetProperty("SelectedLanguage", DisplayStatic)!.GetValue(null)! == "system", "Invalid preference must fall back to system");
    }

    private static void TestBridgeOutputDisplay(Action<bool, string> verify)
    {
        foreach (var language in new[] { "zh-CN", "zh-HK", "en-US" })
        foreach (var wasReady in new[] { false, true })
        {
            DisplayLanguage(language);
            var bridge = new DirectUsbInputBridge();
            BridgeEvent? terminal = null;
            bridge.OnEvent += value => terminal = value;
            using var reader = new StreamReader(new MemoryStream());
            typeof(DirectUsbInputBridge).GetField("_stdout", DisplayInstance)!.SetValue(bridge, reader);
            typeof(DirectUsbInputBridge).GetProperty("IsReady")!.SetValue(bridge, wasReady);
            try
            {
                // Feed EOF directly; no subprocess, driver or device is needed.
                ((Task)DisplayCall(bridge, "ReadLoopAsync", CancellationToken.None)!).GetAwaiter().GetResult();
                verify(terminal?.Code == "bridge_output_closed" && terminal.Message == DisplayL("TouchBridgeOutputDisconnected"),
                    $"{language}/{wasReady}: bridge EOF message is not localized");
                verify(!bridge.IsReady, $"{language}/{wasReady}: EOF left the bridge ready");
            }
            finally { bridge.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        DisplayLanguage("zh-CN");
        var startupBridge = new DirectUsbInputBridge();
        const string rawCode = "bridge_exited";
        const string rawDiagnostic = "USB\\VID_05AC: device unavailable";
        typeof(DirectUsbInputBridge).GetField("_lastErrorCode", DisplayInstance)!.SetValue(startupBridge, rawCode);
        typeof(DirectUsbInputBridge).GetField("_lastStandardError", DisplayInstance)!.SetValue(startupBridge, rawDiagnostic);
        try
        {
            var error = (Exception)DisplayCall(startupBridge, "CreateStartupException", DisplayF("TouchBridgeExitedFormat", 7), null)!;
            foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
            {
                DisplayLanguage(language);
                verify(DisplayRefresh(error.Message) == DisplayF("TouchBridgeExitedFormat", 7) +
                    DisplayF("TouchBridgeErrorCodeFormat", rawCode) + DisplayF("TouchBridgeDiagnosticFormat", rawDiagnostic),
                    language + ": bridge startup exception lost localization or raw diagnostics");
            }
        }
        finally { startupBridge.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void TestProtectedOverlayDisplay(Action<bool, string> verify)
    {
        DisplayLanguage("zh-CN");
        var caption = DisplayF("CaptureVideoProtectedAudioActiveFormat", 48, 2);
        var overlay = (Window)Activator.CreateInstance(DisplayType("Windows.ProtectedContentOverlayWindow"),
            DisplayInstance, null, [(nint)0, caption], null)!;
        try
        {
            var label = (TextBlock)overlay.GetType().GetField("_audioText", DisplayInstance)!.GetValue(overlay)!;
            foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
            {
                DisplayLanguage(language);
                verify(label.Text == DisplayF("CaptureVideoProtectedAudioActiveFormat", 48, 2), language + ": overlay audio caption stale");
                DisplayCall(overlay, "UpdateAudioDisplay", caption);
                verify(label.Text == DisplayF("CaptureVideoProtectedAudioActiveFormat", 48, 2), language + ": cached overlay update restored old language");
            }
        }
        finally { overlay.Close(); }
    }

    private static void TestPreviewSettingsIsolation(App app, string output, Action<bool, string> verify)
    {
        var field = typeof(App).GetField("_settingsStore", DisplayInstance)!;
        var previous = field.GetValue(app);
        var path = Path.GetFullPath(Path.Combine(output, "preview-settings-" + Guid.NewGuid() + ".json"));
        field.SetValue(app, Activator.CreateInstance(DisplayType("Updater.UpdateSettingsStore"), DisplayInstance, null, [path], null));
        try
        {
            verify((bool)DisplayCall(app, "SaveUpdateSettings")! && !File.Exists(path), "UI previews must not persist app preferences");
        }
        finally { field.SetValue(app, previous); }
    }

    private static void TestBluetoothStatusDisplay(object vm, Action<bool, string> verify)
    {
        DisplayLanguage("zh-CN");
        var service = vm.GetType().GetField("_bluetoothControl", DisplayInstance)!.GetValue(vm)!;
        var label = DisplayBinding(vm, "BluetoothControlStatus");
        verify((string)DisplayGet(service, "Status")! == DisplayL("BluetoothControlOff"), "Initial Bluetooth status is hardcoded");
        DisplayCall(service, "SetStatus", DisplayL("BluetoothHidNotificationStalled"), DisplayL("BluetoothHidRouteResetting"));
        foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
        {
            DisplayLanguage(language);
            verify(label.Text == DisplayL("BluetoothHidNotificationStalled") + " " + DisplayL("BluetoothHidRouteResetting"),
                language + ": Bluetooth service status/error event lost localization");
        }
        DisplayCall(service, "SetStatus", DisplayL("BluetoothControlOff"), null);
    }

    private static void TestControlDisplaySwitching(Action<bool, string> verify)
    {
        var serviceType = DisplayType("Services.ControlStatusService");
        var modeType = DisplayType("Services.ControlStatusMode");
        var stageType = DisplayType("Services.ControlStage");
        var vmType = DisplayType("Windows.ReverseControlStatusViewModel");
        foreach (var mode in Enum.GetValues(modeType))
        foreach (var stage in Enum.GetValues(stageType))
        {
            DisplayLanguage("zh-CN");
            var service = Activator.CreateInstance(serviceType, true)!;
            DisplayCall(service, "Report", mode, stage, "Test iPhone", DisplayF("ControlRecoveringFormat", 2, 3), true, "raw USB diagnostic", 2, 3);
            var vm = Activator.CreateInstance(vmType, DisplayInstance, null, [service], null)!;
            try
            {
                var title = DisplayBinding(vm, "StageTitle");
                var body = DisplayBinding(vm, "StageDescription");
                var stages = ((IEnumerable)DisplayGet(vm, "Stages")!).Cast<object>().ToArray();
                var stageLabels = stages.Select(s => DisplayBinding(s, "Title")).ToArray();
                var deadline = vmType.GetField("_autoCloseAtUtc", DisplayInstance)!.GetValue(vm);
                foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
                {
                    DisplayLanguage(language);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(1));
                    verify(title.Text == DisplayL("ControlStage" + stage), $"{mode}/{stage}/{language}: stage binding stale");
                    verify(body.Text == DisplayF("ControlRecoveringFormat", 2, 3), $"{mode}/{stage}/{language}: description binding stale");
                    verify(Equals(deadline, vmType.GetField("_autoCloseAtUtc", DisplayInstance)!.GetValue(vm)), $"{mode}/{stage}: countdown reset");
                    verify(stages.SequenceEqual(((IEnumerable)DisplayGet(vm, "Stages")!).Cast<object>()), $"{mode}/{stage}: stage collection rebuilt");
                    for (var i = 0; i < stages.Length; i++)
                        verify(stageLabels[i].Text == (string)DisplayGet(stages[i], "Title")!, $"{mode}/{stage}/{language}: workflow binding stale");
                }
            }
            finally { DisplayCall(vm, "Dispose"); }
        }
        DisplayLanguage("zh-CN");
        var promptService = Activator.CreateInstance(serviceType, true)!;
        DisplayCall(promptService, "Begin", Enum.Parse(modeType, "Bluetooth"), "Test iPhone");
        var optionType = DisplayType("Services.ControlPromptOption");
        var options = Array.CreateInstance(optionType, 2);
        for (var i = 0; i < 2; i++) options.SetValue(Activator.CreateInstance(optionType,
            ["id" + i, DisplayL("BluetoothClientUnknownName"), DisplayF("BluetoothClientAddressFormat", "RAW:00:" + i), true]), i);
        var prompt = Activator.CreateInstance(DisplayType("Services.ControlPrompt"),
            [Enum.Parse(DisplayType("Services.ControlPromptType"), "Selection"), DisplayL("BluetoothClientBindingTitle"),
                DisplayF("BluetoothClientBindingTargetFormat", "Test iPhone"), DisplayL("Continue"), DisplayL("Cancel"), true, "USB\\VID_05AC / raw error", options])!;
        var task = (Task)DisplayCall(promptService, "RequestPromptAsync", prompt, CancellationToken.None)!;
        var promptVm = Activator.CreateInstance(vmType, DisplayInstance, null, [promptService], null)!;
        try
        {
            vmType.GetProperty("SelectedPromptOption")!.SetValue(promptVm, options.GetValue(1));
            var body = DisplayBinding(promptVm, "PromptMessage");
            var choice = DisplayBinding(options.GetValue(1)!, "DisplayTitle");
            foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
            {
                DisplayLanguage(language);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(1));
                verify(body.Text == DisplayF("BluetoothClientBindingTargetFormat", "Test iPhone"), language + ": prompt message stale");
                verify(choice.Text == DisplayL("BluetoothClientUnknownName"), language + ": prompt option stale");
                verify(ReferenceEquals(DisplayGet(promptVm, "SelectedPromptOption"), options.GetValue(1)), language + ": selection reset");
                verify(!task.IsCompleted, language + ": switching language resolved the pending prompt");
                verify((string)DisplayGet(promptVm, "PromptTechnicalDetails")! == "USB\\VID_05AC / raw error", language + ": raw details altered");
            }
        }
        finally { DisplayCall(promptVm, "Dispose"); }
    }

    private static void TestStartupDisplayFallback(Application app, Action<bool, string> verify)
    {
        foreach (var language in new[] { "zh-CN", "zh-HK", "en-US" })
        {
            DisplayLanguage(language);
            var labels = new[] { ("HeadingText", "StartupErrorHeading"), ("LogLabelText", "StartupErrorLogLabel"),
                ("DetailsExpander", "StartupErrorDetails"), ("OpenLogButton", "StartupErrorOpenLog"), ("CloseButton", "StartupErrorClose") }
                .Select(x => (x.Item1, Text: DisplayL(x.Item2))).ToArray();
            var dictionary = app.Resources.MergedDictionaries.Single(d => d.Source?.OriginalString.Contains("Localization/Strings.") == true);
            var index = app.Resources.MergedDictionaries.IndexOf(dictionary);
            app.Resources.MergedDictionaries.Remove(dictionary);
            Window? window = null;
            try
            {
                window = (Window)Activator.CreateInstance(DisplayType("Windows.StartupErrorWindow"), DisplayInstance, null,
                    [new DllNotFoundException("RAW native failure"), @"C:\Temp\log.txt"], null)!;
                foreach (var (name, expected) in labels)
                {
                    var control = window.FindName(name);
                    var text = control is TextBlock tb ? tb.Text : control is Expander ex ? ex.Header : ((ContentControl)control).Content;
                    verify(Equals(text, expected), language + ": missing startup dictionary leaked a key: " + name);
                }
                verify(((TextBox)window.FindName("DetailsTextBox")).Text.Contains("RAW native failure"), "Startup raw exception lost");
            }
            finally { window?.Close(); app.Resources.MergedDictionaries.Insert(index, dictionary); }
        }
    }

    private static void TestMainCachedDisplay(object vm, Action<bool, string> verify)
    {
        DisplayLanguage("zh-CN");
        var bindings = new[] { ("_bluetoothControlStatus", "BluetoothControlStatus", "BluetoothControlWaiting"),
            ("_mediaOutputStatus", "MediaOutputStatus", "MediaOutputStopping"),
            ("_mediaOutputCapabilitiesText", "MediaOutputCapabilitiesText", "MediaOutputCapabilitiesUnknown"),
            ("_virtualCameraStatusText", "VirtualCameraStatusText", "VirtualCameraInstallRequired") }
            .Select(x => { vm.GetType().GetField(x.Item1, DisplayInstance)!.SetValue(vm, DisplayL(x.Item3)); return (Label: DisplayBinding(vm, x.Item2), Key: x.Item3); }).ToArray();
        foreach (var language in new[] { "zh-HK", "en-US", "zh-CN" })
        {
            DisplayLanguage(language);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            foreach (var (label, key) in bindings) verify(label.Text == DisplayL(key), language + ": main cached state stale: " + key);
        }
    }
}
