using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using IPhoneMirror.App;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static bool ReviewCapture => Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_REVIEW_CAPTURE") == "1";
    // Exercises real WPF windows through the app's read-only preview catalog.
    // Does not enumerate devices, send input, install drivers or download updates.
    private static int RunConsistencyAudit(string output, string? onlySurface = null, string? onlyCulture = null)
    {
        // WPF layout is audited separately from the native preview smoke tests.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, true);
        app.InitializeComponent();
        var assembly = typeof(App).Assembly;
        var language = assembly.GetType("IPhoneMirror.App.Localization.LocalizationService")!
            .GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var open = typeof(MainWindow).GetMethod("OpenDeveloperSurface", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var failures = new List<string>();
        var results = new List<object>();
        var layoutFindings = new HashSet<string>();
        using var bindingTrace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingTrace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var culture in (onlyCulture is not null ? new[] { onlyCulture } :
                         QuickLayoutAudit ? new[] { "en-US" } : IPhoneMirror.UI.Services.SupportedUiCultures.All))
            foreach (var theme in (QuickLayoutAudit ? new[] { AppTheme.Light } : new[] { AppTheme.Light, AppTheme.Dark }))
            {
                language.Invoke(null, [culture, false, false]);
                ApplyTheme(assembly, theme);
                if (Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_HIGH_CONTRAST") == "1")
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.HighContrast,
                        Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: false);
                double? auditScale = null;
                if (Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_TEXT_SCALE") is { } scaleText &&
                    double.TryParse(scaleText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var textScale))
                    auditScale = textScale;
                void ApplyAuditAppearance()
                {
                    if (auditScale is null && Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_HIGH_CONTRAST") != "1") return;
                    assembly.GetType("IPhoneMirror.UI.Services.AccessibilityAppearance")!
                        .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [app, auditScale,
                            (bool?)(Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_HIGH_CONTRAST") == "1")]);
                }
                ApplyAuditAppearance();
                var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                ApplyAuditAppearance();
                owner.ShowInTaskbar = false;
                try
                {
                    foreach (var surface in new[] {
                        "workspace-mirroring", "workspace-devices", "workspace-settings", "workspace-output", "workspace-toolbar",
                        "developer-tools", "about", "advanced-settings", "text-input", "device-binding", "airplay-device-selection",
                        "bluetooth-connection", "bluetooth-client-binding", "bluetooth-control-notice",
                        "shortcut-settings", "reverse-control-status", "reverse-control-prompt", "reverse-control-prompt-long", "prompt", "prompt-long", "bluetooth-waiting", "bluetooth-failure-long",
                        "reverse-control-wired-prerequisite", "reverse-control-wireless-prerequisite",
                        "reverse-control-error", "capture-error", "session-closed", "usb-config-error",
                        "capture-recovery", "image-settings", "projection-settings", "media-output",
                        "usb-mode", "startup-error", "update", "instance-conflict", "protected-content" }.Where(s => onlySurface == null || onlySurface.Split(',').Contains(s)))
                    {
                        try
                        {
                            if (surface == "workspace-toolbar")
                            {
                                open.Invoke(owner, ["workspace-settings"]);
                                typeof(MainWindow).GetMethod("SetSettingsPanelVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [true]);
                                ((FrameworkElement)owner.FindName("EnvironmentPanel")).Visibility = Visibility.Visible;
                                ((FrameworkElement)owner.FindName("PreviewQuickActions")).Visibility = Visibility.Visible;
                            }
                            else if (surface == "developer-tools")
                            {
                                var developer = (Window)Activator.CreateInstance(assembly.GetType("IPhoneMirror.App.Windows.DeveloperToolsWindow")!, BindingFlags.Instance | BindingFlags.NonPublic, null, [owner], null)!;
                                developer.Owner = owner;
                                developer.Show();
                            }
                            else if (surface == "prompt-long")
                            {
                                var prompt = (Window)Activator.CreateInstance(assembly.GetType("IPhoneMirror.App.Windows.AppPromptWindow")!, BindingFlags.Instance | BindingFlags.NonPublic, null,
                                    ["Device confirmation / 设备确认 / 裝置確認", string.Join(" ", Enumerable.Repeat("Confirm device trust before continuing. 请确认设备已信任此电脑。", 40)), true, true,
                                     "Continue after confirming device trust and developer mode / 确认后继续", false, IPhoneMirror.UI.Controls.StatusTone.Info], null)!;
                                prompt.Owner = owner;
                                prompt.Show();
                            }
                            else if (surface is "reverse-control-prompt" or "reverse-control-prompt-long")
                            {
                                var serviceType = assembly.GetType("IPhoneMirror.App.Services.ControlStatusService")!;
                                var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
                                var mode = Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ControlStatusMode")!, "Usb");
                                serviceType.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [mode, "Test iPhone"]);
                                var promptType = assembly.GetType("IPhoneMirror.App.Services.ControlPrompt")!;
                                var prompt = Activator.CreateInstance(promptType, [
                                    Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ControlPromptType")!, "UserActionRequired"),
                                    surface == "reverse-control-prompt" ? app.FindResource("ReverseControlPrerequisiteWiredTitle") : "Developer Mode and device trust must be confirmed before starting reverse control",
                                    surface == "reverse-control-prompt" ? app.FindResource("ReverseControlPrerequisiteWiredBody") : string.Join(" ", Enumerable.Repeat("Check the device and confirm that this computer is trusted.", 20)),
                                    app.FindResource("Continue"), app.FindResource("Cancel"), true, null, null]);
                                serviceType.GetMethod("RequestPromptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [prompt, CancellationToken.None]);
                                assembly.GetType("IPhoneMirror.App.Windows.ReverseControlStatusWindow")!
                                    .GetMethod("Show", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [owner, service, null, null, null]);
                            }
                            else if (surface is "bluetooth-waiting" or "bluetooth-failure-long")
                            {
                                var notice = assembly.GetType("IPhoneMirror.App.Windows.BluetoothControlNoticeWindow")!;
                                notice.GetMethod(surface == "bluetooth-waiting" ? "ShowWaiting" : "ShowFailure", BindingFlags.Static | BindingFlags.NonPublic)!
                                    .Invoke(null, [owner, surface == "bluetooth-waiting" ? "Test iPhone" : string.Join(" ", Enumerable.Repeat("Connection failed. Check pairing and try again.", 30))]);
                            }
                            else open.Invoke(owner, [surface]);
                            ApplyAuditAppearance();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(240));
                            var windows = app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray();
                            if (!surface.StartsWith("workspace-") && windows.Length == 0)
                                throw new InvalidOperationException("Preview entry did not open a window.");
                            foreach (var window in windows.Length == 0 ? new Window[] { owner } : windows)
                            {
                                window.UpdateLayout();
                                if (window != owner) AssertSelfDrawnWindowCorners(window);
                                // Tab contents and combo popups are separate template/resource paths.
                                ExerciseTabsAndPopups(window, layoutFindings);
                                AssertVisibleButtonsFit(window);
                                var name = $"{culture}-{theme}-{surface}";
                                SaveWindowRender(window, Path.Combine(output, name + ".png"));
                                if (surface == "media-output")
                                    AssertMicrophoneReachable(window, Path.Combine(output, name + "-microphone.png"));
                                if (ReviewCapture)
                                    VerifyReviewedPage(window, surface, Path.Combine(output, name));
                                if (surface is "reverse-control-prompt" or "reverse-control-prompt-long")
                                    AssertPromptActionsReachable(window, Path.Combine(output, name + "-scrolled.png"));
                                ExerciseWorkAreaSizes(window, name, layoutFindings, Path.Combine(output, name));
                                results.Add(new { culture, theme = theme.ToString(), textScale = auditScale,
                                    actualBodyFontSize = (double)app.FindResource("BodyFontSize"), surface,
                                    window = window.GetType().Name, width = window.ActualWidth, height = window.ActualHeight });
                                if (window != owner) AssertEscapeCloses(window);
                            }
                        }
                        catch (Exception error)
                        {
                            failures.Add($"{culture}/{theme}/{surface}: {error.GetBaseException().Message}");
                        }
                        finally
                        {
                            foreach (var window in app.Windows.Cast<Window>().Where(w => w != owner).ToArray()) window.Close();
                            if (surface == "workspace-toolbar")
                            {
                                ((FrameworkElement)owner.FindName("EnvironmentPanel")).ClearValue(UIElement.VisibilityProperty);
                                ((FrameworkElement)owner.FindName("PreviewQuickActions")).ClearValue(UIElement.VisibilityProperty);
                            }
                        }
                    }
                }
                finally { CloseWorkspaceTestWindow(owner); }
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingTrace);
            File.WriteAllText(Path.Combine(output, "runtime-results.json"), JsonSerializer.Serialize(
                new { results, failures, layoutFindings, bindingErrors = bindingTrace.Messages }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        foreach (var finding in layoutFindings) Console.Error.WriteLine(finding);
        Console.WriteLine($"UI audit: {results.Count} surfaces rendered, {failures.Count} failures, {layoutFindings.Count} layout findings, {bindingTrace.Messages.Count} binding diagnostics.");
        return failures.Count == 0 && bindingTrace.Messages.Count == 0 && layoutFindings.Count == 0 ? 0 : 1;
    }

    private static void VerifyReviewedPage(Window window, string surface, string screenshot)
    {
        switch (surface)
        {
            case "workspace-devices":
                var vm = (IPhoneMirror.App.ViewModels.MainViewModel)window.DataContext;
                if (vm.WirelessStatus == LocalizationService.Get("WirelessNotStarted"))
                {
                    var waitingStatus = (TextBlock)window.FindName("WaitingWirelessStatus");
                    InteractionAssert(string.IsNullOrEmpty(vm.ActiveWirelessReceiverName) && waitingStatus.Visibility == Visibility.Collapsed,
                        "The waiting screen must hide routine status for an inactive AirPlay receiver.");
                    var starting = vm.GetType().GetProperty(nameof(vm.IsWirelessReceiverStarting))!;
                    starting.SetValue(vm, true);
                    window.UpdateLayout();
                    InteractionAssert(waitingStatus.Visibility == Visibility.Visible, "Receiver startup progress must be visible.");
                    starting.SetValue(vm, false);

                    var tone = vm.GetType().GetProperty(nameof(vm.WirelessStatusTone))!;
                    var originalTone = vm.WirelessStatusTone;
                    tone.SetValue(vm, IPhoneMirror.UI.Controls.StatusTone.Error);
                    window.UpdateLayout();
                    InteractionAssert(waitingStatus.Visibility == Visibility.Visible, "Receiver startup errors must be visible.");
                    tone.SetValue(vm, originalTone);

                    var activeName = vm.GetType().GetProperty(nameof(vm.ActiveWirelessReceiverName))!;
                    activeName.SetValue(vm, "iPhoneMirror AirPlay");
                    window.UpdateLayout();
                    var usbCard = (FrameworkElement)window.FindName("WaitingUsbCard");
                    var guide = (FrameworkElement)window.FindName("WaitingAirPlayCard");
                    foreach (var name in new[] { "WaitingUsbInstructions", "WaitingUsbTrust", "WaitingAirPlayInstructions", "WaitingAirPlaySelect" })
                    {
                        var instructions = (TextBlock)window.FindName(name);
                        InteractionAssert(instructions.IsVisible && instructions.TextAlignment == TextAlignment.Center &&
                            !string.IsNullOrWhiteSpace(instructions.Text) && !instructions.Text.Any(c => char.IsPunctuation(c) || c == '→'),
                            "Each connection step must be centered and punctuation-free.");
                    }
                    var waiting = (FrameworkElement)window.FindName("WaitingForDeviceState");
                    InteractionAssert(!FindVisualDescendants<TextBlock>(waiting).Any(text => text.IsVisible && text.Text == vm.ActiveWirelessReceiverName) &&
                        waitingStatus.Visibility == Visibility.Collapsed,
                        "A ready receiver must keep the waiting screen clear of receiver names and status messages.");
                    InteractionAssert(Math.Abs(usbCard.ActualWidth - guide.ActualWidth) <= 1 &&
                        (Grid.GetRow(guide) != 0 || Math.Abs(usbCard.ActualHeight - guide.ActualHeight) <= 1),
                        "Side-by-side connection cards must align at equal widths and heights.");
                    SaveWindowRender(window, screenshot + "-airplay-ready.png");
                    var originalWidth = window.Width;
                    try
                    {
                        window.Width = window.MinWidth;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        window.UpdateLayout();
                        InteractionAssert(Grid.GetRow(guide) == 1 && Math.Abs(usbCard.ActualWidth - guide.ActualWidth) < 1,
                            "A narrow waiting screen must stack both connection options at equal widths.");
                        SaveWindowRender(window, screenshot + "-airplay-ready-narrow.png");
                    }
                    finally { window.Width = originalWidth; }
                    activeName.SetValue(vm, string.Empty);
                }
                break;
            case "projection-settings":
                var initialSize = window.RenderSize;
                foreach (var category in new[] { "Control", "Application", "Projection" })
                {
                    var tab = (RadioButton)window.FindName(category + "SettingsTab");
                    tab.IsChecked = true;
                    window.UpdateLayout();
                    InteractionAssert(window.RenderSize == initialSize &&
                        new[] { "Control", "Application", "Projection" }.All(name =>
                            ((ScrollViewer)window.FindName(name + "SettingsScrollViewer")).IsVisible == (name == category)),
                        $"Floating settings must show only the {category} category and keep its window size.");
                    SaveWindowRender(window, screenshot + "-" + category.ToLowerInvariant() + ".png");
                }
                break;
            case "capture-error":
                var actions = (WrapPanel)window.FindName("NoticeActions");
                InteractionAssert(window.FindName("FailureDetails") is FrameworkElement { IsVisible: true } &&
                    window.FindName("CopyDiagnosticsButton") is Button { IsVisible: true } copy && actions.Children.Contains(copy) &&
                    window.FindName("CloseButton") is Button { IsVisible: true } close && actions.Children.Contains(close) &&
                    window.FindName("RecoveryButton") is Button { IsVisible: true } recovery && actions.Children.Contains(recovery),
                    "Capture error should show technical details and group copy, close and recovery in the fixed footer.");
                break;
            case "media-output":
                var tabs = Visuals(window).OfType<TabControl>().First();
                var originalTab = tabs.SelectedIndex;
                tabs.SelectedIndex = 1;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                window.UpdateLayout();
                var protocol = (ComboBox)window.FindName("StreamProtocolBox");
                var authorization = (FrameworkElement)window.FindName("WhipAuthorizationFields");
                var primary = (Button)window.FindName("StartStreamingButton");
                var address = (TextBlock)window.FindName("StreamAddressLabel");
                var streamingScroll = Ancestors(address).OfType<ScrollViewer>().First();
                protocol.SelectedIndex = 0;
                window.UpdateLayout();
                var streamingHeight = window.ActualHeight;
                InteractionAssert(primary.IsVisible && !authorization.IsVisible &&
                    !streamingScroll.IsAncestorOf(primary),
                    $"Expected a fixed RTMP action with hidden authorization. Action: {primary.IsVisible}, authorization: {authorization.IsVisible}, tab: {tabs.SelectedIndex}.");
                protocol.SelectedIndex = 2;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(authorization.IsVisible &&
                    address.Text == (string)Application.Current.FindResource("StreamWhipAddress"),
                    "Switching to WHIP did not expose its endpoint and token fields.");
                window.UpdateLayout();
                AssertStreamingFieldAlignment(window);
                SaveWindowRender(window, screenshot + "-whip.png");
                protocol.SelectedIndex = 1;
                InteractionAssert(!authorization.IsVisible &&
                    address.Text == (string)Application.Current.FindResource("StreamSrtAddress"),
                    "Switching to SRT did not hide the token and restore the correct address label.");
                protocol.SelectedIndex = 0;
                tabs.SelectedIndex = 0;
                window.UpdateLayout();
                var recordingHeight = window.ActualHeight;
                var outputActions = (Panel)window.FindName("OutputActions");
                InteractionAssert(window.FindName("StartRecordingButton") is Button { IsVisible: true } record &&
                    outputActions.Children.Contains(record) && !primary.IsVisible,
                    "Recording must expose its primary action in the fixed footer, separate from streaming.");
                tabs.SelectedIndex = 2;
                window.UpdateLayout();
                InteractionAssert(Math.Abs(window.ActualHeight - streamingHeight) < 1 &&
                    Math.Abs(recordingHeight - streamingHeight) < 1,
                    "Switching output tabs must keep the window height stable.");
                var outputVm = (IPhoneMirror.App.ViewModels.MainViewModel)window.DataContext;
                var cameraCapabilities = typeof(IPhoneMirror.App.ViewModels.MainViewModel).GetField("_virtualCameraCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var originalCapabilities = cameraCapabilities.GetValue(outputVm);
                var cameraStatus = outputVm.GetType().GetProperty(nameof(outputVm.VirtualCameraStatusText))!;
                var originalStatus = outputVm.VirtualCameraStatusText;
                try
                {
                    cameraCapabilities.SetValue(outputVm, new IPhoneMirror.App.Services.VirtualCameraCapabilities(true, true, false, false, false, ""));
                    cameraStatus.SetValue(outputVm, LocalizationService.Get("VirtualCameraInstallRequired"));
                    KeyboardCall(window, "UpdateStartButtons");
                    window.UpdateLayout();
                    InteractionAssert(window.FindName("InstallVirtualCameraButton") is Button { IsVisible: true } install &&
                        outputActions.Children.Contains(install), "Virtual camera setup must expose install in the fixed footer.");
                    SaveWindowRender(window, screenshot + "-camera-install.png");
                }
                finally
                {
                    cameraCapabilities.SetValue(outputVm, originalCapabilities);
                    cameraStatus.SetValue(outputVm, originalStatus);
                    tabs.SelectedIndex = originalTab;
                }
                break;
            case "shortcut-settings":
                var allShortcuts = (RadioButton)window.FindName("ShortcutFilterAll");
                var boundShortcuts = (RadioButton)window.FindName("ShortcutFilterBound");
                var sections = (ItemsControl)window.FindName("ShortcutSections");
                var filterSurface = (FrameworkElement)window.FindName("ShortcutFilterSurface");
                var save = (Button)window.FindName("SaveButton");
                var cards = Visuals(sections).OfType<Border>().Where(card => card.Name == "ShortcutGroupCard").ToArray();
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                var editors = Visuals(sections).OfType<IPhoneMirror.UI.Controls.ResponsiveFieldRow>()
                    .Select(row => Bounds((FrameworkElement)row.Children[1])).ToArray();
                InteractionAssert(window.FindName("ShortcutJump") is null &&
                    Math.Abs(allShortcuts.ActualWidth - boundShortcuts.ActualWidth) <= 1 &&
                    cards.Length > 0 && cards.All(card => Math.Abs(Bounds(card).Left - Bounds(filterSurface).Left) <= 1 &&
                        Math.Abs(Bounds(card).Right - Bounds(save).Right) <= 1) &&
                    editors.Length > 1 && editors.All(editor => Math.Abs(editor.Left - editors[0].Left) <= 1 &&
                        Math.Abs(editor.Right - editors[0].Right) <= 1),
                    "Shortcut filter, category cards, editor column and footer must share aligned edges.");
                boundShortcuts.IsChecked = true;
                window.UpdateLayout();
                var rows = Visuals(sections).OfType<IPhoneMirror.UI.Controls.ResponsiveFieldRow>()
                    .Where(row => row.IsVisible).Select(row => row.DataContext).ToArray();
                InteractionAssert(rows.Length > 0 && rows.All(row => row is IPhoneMirror.App.Windows.ShortcutBindingRow { IsBound: true }),
                    "Bound-only filtering left unbound shortcuts visible.");
                SaveWindowRender(window, screenshot + "-bound.png");
                allShortcuts.IsChecked = true;
                window.UpdateLayout();
                break;
        }
    }

    private static void ExerciseWorkAreaSizes(Window window, string name, HashSet<string> findings, string? renderPath = null)
    {
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        var minWidth = window.MinWidth;
        var minHeight = window.MinHeight;
        var sizeToContent = window.SizeToContent;
        try
        {
            window.SizeToContent = SizeToContent.Manual;
            window.MinWidth = 0;
            window.MinHeight = 0;
            var budgets = new List<(string Label, double Width, double Height)>
            {
                ("default", width, height),

            };
            if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
                budgets.Add(("minimum", minWidth > 0 ? Math.Min(width, minWidth) : width, minHeight > 0 ? Math.Min(height, minHeight) : height));
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                if (ReviewCapture && scale != 2) continue;
                // Match WindowWorkAreaController's 80% high-DPI work-area cap.
                var ratio = scale > 1 ? 0.8 : 1;
                budgets.Add(($"{scale:P0}", Math.Min(width, 1920 * ratio / scale), Math.Min(height, 1040 * ratio / scale)));
            }
            foreach (var budget in budgets)
            {
                window.Width = budget.Width;
                window.Height = budget.Height;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                window.UpdateLayout();
                AuditAllTabs(window, $"{name} at {budget.Label}", findings, renderPath != null && (budget.Label == "default" || budget.Label == "200%") ? renderPath + "-" + budget.Label.Replace("%", "pct") : null);
                if (renderPath != null && budget.Label == "200%") SaveWindowRender(window, renderPath + "-small.png");
                if (budget.Label == "200%") AssertNarrowWorkspacePanelToggle(window, name, findings);
            }
        }
        finally
        {
            window.MinWidth = minWidth; window.MinHeight = minHeight;
            window.Width = width; window.Height = height; window.SizeToContent = sizeToContent;
            window.UpdateLayout();
        }
    }

    private static void AssertMicrophoneReachable(Window window, string renderPath)
    {
        var tabs = Visuals(window).OfType<TabControl>().First();
        var selected = tabs.SelectedIndex;
        tabs.SelectedIndex = 1;
        window.UpdateLayout();
        var microphone = (ComboBox)window.FindName("MicrophoneBox");
        DependencyObject? parent = microphone;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null && parent is not ScrollViewer) { }
        var scroll = parent as ScrollViewer ?? throw new InvalidOperationException("Microphone scroll area was not found.");
        microphone.BringIntoView();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        window.UpdateLayout();
        var bounds = microphone.TransformToAncestor(scroll).TransformBounds(new Rect(microphone.RenderSize));
        if (bounds.Top < -2 || bounds.Bottom > scroll.ActualHeight + 2 || bounds.Right > scroll.ActualWidth + 2)
            throw new InvalidOperationException("Microphone selector cannot be reached by scrolling.");
        AssertStreamingFieldAlignment(window);
        SaveWindowRender(window, renderPath);
        scroll.ScrollToHome();
        tabs.SelectedIndex = selected;
        window.UpdateLayout();
    }

    private static void AssertStreamingFieldAlignment(Window window)
    {
        Rect Bounds(string name)
        {
            var field = (FrameworkElement)window.FindName(name);
            return field.TransformToAncestor(window).TransformBounds(new Rect(field.RenderSize));
        }

        var protocol = Bounds("StreamProtocolBox");
        var address = Bounds("StreamDestinationBox");
        var microphone = Bounds("MicrophoneBox");
        var width = Bounds("StreamWidthBox");
        var height = Bounds("StreamHeightBox");
        var fps = Bounds("StreamFpsBox");
        var bitrate = Bounds("StreamBitrateBox");
        var refresh = Bounds("RefreshMicrophonesButton");
        var authorization = window.FindName("StreamAuthorizationBox") is FrameworkElement { IsVisible: true }
            ? Bounds("StreamAuthorizationBox") : Rect.Empty;
        const double tolerance = 1;
        bool SameEdges(Rect a, Rect b) => Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Right - b.Right) <= tolerance;
        InteractionAssert(SameEdges(protocol, address) &&
            SameEdges(width, fps) && SameEdges(height, bitrate) &&
            Math.Abs(width.Width - height.Width) <= tolerance &&
            Math.Abs(width.Left - protocol.Left) <= tolerance && Math.Abs(height.Right - protocol.Right) <= tolerance &&
            Math.Abs(refresh.Right - protocol.Right) <= tolerance && refresh.Left >= microphone.Right - tolerance &&
            (authorization.IsEmpty || SameEdges(protocol, authorization)),
            "Streaming fields must share equal columns and keep the microphone refresh action beside its field.");
        var fields = new[] { address, microphone, width, height, fps, bitrate, authorization };
        InteractionAssert(fields.Where(field => !field.IsEmpty)
            .All(field => Math.Abs(field.Height - protocol.Height) <= tolerance),
            $"The output fields must share the protocol picker's height ({protocol.Height:0.##}): " +
            string.Join(", ", fields.Where(field => !field.IsEmpty).Select(field => field.Height.ToString("0.##"))));
    }

    private static void AssertEscapeCloses(Window window)
    {
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
        { RoutedEvent = Keyboard.KeyDownEvent });
        if (window.IsVisible) throw new InvalidOperationException("Escape did not invoke the dialog close action.");
    }

    private static void AssertPromptActionsReachable(Window window, string renderPath)
    {
        var primary = Visuals(window).OfType<Button>().Single(b =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ControlPromptPrimaryButton");
        var scroll = (ScrollViewer)window.FindName("ControlContentScrollViewer");
        foreach (var offset in new[] { 0.0, scroll.ScrollableHeight })
        {
            scroll.ScrollToVerticalOffset(offset);
            window.UpdateLayout();
            var bounds = primary.TransformToAncestor(window).TransformBounds(new Rect(primary.RenderSize));
            var scrollBounds = scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize));
            if (bounds.Top < scrollBounds.Bottom || bounds.Bottom > window.ActualHeight - 10 || bounds.Right > window.ActualWidth - 10)
                throw new InvalidOperationException("Prompt primary action is not fully visible in the fixed footer.");
        }
        SaveWindowRender(window, renderPath);
        scroll.ScrollToHome();
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void ExerciseTabsAndPopups(Window window, HashSet<string>? scrollbarFindings = null)
    {
        foreach (var tabs in Visuals(window).OfType<TabControl>().ToArray())
        {
            var selected = tabs.SelectedIndex;
            var viewportWidth = window.Width;
            var viewportHeight = window.Height;
            for (var i = 0; i < tabs.Items.Count; ++i)
            {
                tabs.SelectedIndex = i;
                window.Width = viewportWidth;
                window.Height = viewportHeight;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                window.UpdateLayout();
                AssertVisibleButtonsFit(window);
            }
            tabs.SelectedIndex = selected;
            window.Width = viewportWidth;
            window.Height = viewportHeight;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            window.UpdateLayout();
        }
        foreach (var combo in Visuals(window).OfType<ComboBox>().Where(c => c.IsVisible && c.IsEnabled).ToArray())
        {
            combo.IsDropDownOpen = true;
            window.UpdateLayout();
            if (scrollbarFindings is not null &&
                (combo.Template.FindName("PART_Popup", combo) ?? combo.Template.FindName("Popup", combo)) is Popup { Child: { } child })
                AuditScrollBarLayout(child, window.GetType().Name + "/combo-popup", scrollbarFindings);
            combo.IsDropDownOpen = false;
        }
        foreach (var element in Visuals(window).OfType<FrameworkElement>().Where(e => e.ContextMenu != null).ToArray())
        {
            var menu = element.ContextMenu!;
            menu.PlacementTarget = element;
            menu.IsOpen = true;
            menu.UpdateLayout();
            menu.IsOpen = false;
        }
    }

    private static void AssertVisibleButtonsFit(Window window)
    {
        foreach (var button in Visuals(window).OfType<Button>().Where(b => b.IsVisible && b.Content is string))
        {
            var content = Visuals(button).OfType<ContentPresenter>().FirstOrDefault();
            if (content == null || button.ActualWidth <= 0) continue;
            if (content.DesiredSize.Width > button.ActualWidth - button.Padding.Left - button.Padding.Right + 2)
                throw new InvalidOperationException($"Button text clipped: {button.Content} ({button.ActualWidth:0} wide).");
        }
    }

    private static void SaveWindowRender(Window window, string path)
    {
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            if (!window.AllowsTransparency)
                drawing.DrawRectangle(window.TryFindResource("WindowBackgroundBrush") as Brush ?? Brushes.White,
                    null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        if (ReviewCapture)
            File.WriteAllText(Path.ChangeExtension(path, ".capture.json"), JsonSerializer.Serialize(new
            {
                window = window.GetType().Name, windowType = window.GetType().FullName,
                assembly = window.GetType().Assembly.GetName().Name,
                title = window.Title, width = window.ActualWidth,
                height = window.ActualHeight, rendering = "Shown WPF window exported with RenderTargetBitmap; not a desktop screen capture.",
                bodyFontSize = window.TryFindResource("BodyFontSize") as double?,
                scrollAreas = Visuals(window).OfType<ScrollViewer>().Where(viewer => viewer.IsVisible).Select(viewer => new
                {
                    name = viewer.Name, type = viewer.GetType().Name, viewer.ViewportWidth, viewer.ViewportHeight,
                    viewer.ExtentWidth, viewer.ExtentHeight, viewer.HorizontalOffset, viewer.VerticalOffset,
                    vertical = viewer.ComputedVerticalScrollBarVisibility.ToString(),
                    horizontal = viewer.ComputedHorizontalScrollBarVisibility.ToString(),
                }).ToArray(),
                fonts = Visuals(window).OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) &&
                    !text.FontFamily.Source.Contains("Fluent", StringComparison.OrdinalIgnoreCase) &&
                    !text.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase))
                    .Select(text => new { text = text.Text, text.FontSize, family = text.FontFamily.Source,
                        lineHeight = double.IsNaN(text.LineHeight) ? (double?)null : text.LineHeight,
                        weight = text.FontWeight.ToString() }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class AuditTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
