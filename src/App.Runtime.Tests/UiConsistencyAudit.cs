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
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
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
                         QuickLayoutAudit ? new[] { "en-US" } : new[] { "zh-CN", "en-US", "zh-HK" }))
            foreach (var theme in (QuickLayoutAudit ? new[] { AppTheme.Light } : new[] { AppTheme.Light, AppTheme.Dark }))
            {
                language.Invoke(null, [culture, false, false]);
                ApplyTheme(assembly, theme);
                var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                owner.ShowInTaskbar = false;
                try
                {
                    foreach (var surface in new[] {
                        "workspace-mirroring", "workspace-devices", "workspace-settings", "workspace-output", "workspace-toolbar",
                        "developer-tools", "about", "advanced-settings", "device-binding", "airplay-device-selection",
                        "bluetooth-connection", "bluetooth-client-binding", "bluetooth-control-notice",
                        "shortcut-settings", "reverse-control-status", "reverse-control-prompt-long", "prompt", "prompt-long", "bluetooth-waiting", "bluetooth-failure-long",
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
                            else if (surface == "reverse-control-prompt-long")
                            {
                                var serviceType = assembly.GetType("IPhoneMirror.App.Services.ControlStatusService")!;
                                var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
                                var mode = Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ControlStatusMode")!, "Usb");
                                serviceType.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [mode, "Test iPhone"]);
                                var promptType = assembly.GetType("IPhoneMirror.App.Services.ControlPrompt")!;
                                var prompt = Activator.CreateInstance(promptType, [
                                    Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ControlPromptType")!, "UserActionRequired"),
                                    "Developer Mode and device trust must be confirmed before starting reverse control",
                                    string.Join(" ", Enumerable.Repeat("Check the device and confirm that this computer is trusted.", 20)),
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
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(240));
                            var windows = app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray();
                            if (!surface.StartsWith("workspace-") && windows.Length == 0)
                                throw new InvalidOperationException("Preview entry did not open a window.");
                            foreach (var window in windows.Length == 0 ? new Window[] { owner } : windows)
                            {
                                window.UpdateLayout();
                                if (window != owner) AssertSelfDrawnWindowCorners(window);
                                // Tab contents and combo popups are separate template/resource paths.
                                ExerciseTabsAndPopups(window);
                                AssertVisibleButtonsFit(window);
                                var name = $"{culture}-{theme}-{surface}";
                                SaveWindowRender(window, Path.Combine(output, name + ".png"));
                                if (surface == "media-output")
                                    AssertMicrophoneReachable(window, Path.Combine(output, name + "-microphone.png"));
                                if (surface == "reverse-control-prompt-long")
                                    AssertPromptActionsReachable(window, Path.Combine(output, name + "-scrolled.png"));
                                ExerciseWorkAreaSizes(window, name, layoutFindings, Path.Combine(output, name));
                                results.Add(new { culture, theme = theme.ToString(), surface,
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
        scroll.ScrollToEnd();
        window.UpdateLayout();
        var bounds = microphone.TransformToAncestor(scroll).TransformBounds(new Rect(microphone.RenderSize));
        if (bounds.Top < -2 || bounds.Bottom > scroll.ActualHeight + 2 || bounds.Right > scroll.ActualWidth + 2)
            throw new InvalidOperationException("Microphone selector cannot be reached by scrolling.");
        SaveWindowRender(window, renderPath);
        scroll.ScrollToHome();
        tabs.SelectedIndex = selected;
        window.UpdateLayout();
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
        DependencyObject? parent = primary;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null && parent is not ScrollViewer) { }
        var scroll = parent as ScrollViewer ?? throw new InvalidOperationException("Prompt scroll area was not found.");
        scroll.ScrollToEnd();
        window.UpdateLayout();
        var bounds = primary.TransformToAncestor(scroll).TransformBounds(new Rect(primary.RenderSize));
        if (bounds.Top < -2 || bounds.Bottom > scroll.ActualHeight + 2 || bounds.Right > scroll.ActualWidth + 2)
            throw new InvalidOperationException("Long prompt primary action cannot be reached by scrolling.");
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

    private static void ExerciseTabsAndPopups(Window window)
    {
        foreach (var tabs in Visuals(window).OfType<TabControl>().ToArray())
        {
            var selected = tabs.SelectedIndex;
            for (var i = 0; i < tabs.Items.Count; ++i)
            {
                tabs.SelectedIndex = i;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                window.UpdateLayout();
                AssertVisibleButtonsFit(window);
            }
            tabs.SelectedIndex = selected;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            window.UpdateLayout();
        }
        foreach (var combo in Visuals(window).OfType<ComboBox>().Where(c => c.IsVisible && c.IsEnabled).ToArray())
        {
            combo.IsDropDownOpen = true;
            window.UpdateLayout();
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
    }

    private sealed class AuditTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
