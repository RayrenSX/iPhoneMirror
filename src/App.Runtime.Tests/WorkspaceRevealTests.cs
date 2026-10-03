using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.UI.Animations;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestWorkspaceRevealLifecycle(App app)
    {
        var originalMode = GetApplicationDisplayMode(app);
        var originalLanguage = LocalizationService.SelectedLanguage;
        var originalTheme = ThemeService.Preference;
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try { TestWorkspaceRevealWindow(window, mode); }
                finally { CloseWorkspaceTestWindow(window); }
            }
        }
        finally
        {
            SetApplicationDisplayMode(app, originalMode);
            AuditFixType("Localization.LocalizationService").GetMethod("ApplyLanguage", InteractionMembers)!
                .Invoke(null, [originalLanguage, false, true]);
            ThemeService.Apply(originalTheme);
        }
    }

    private static void TestWorkspaceRevealWindow(MainWindow window, ApplicationDisplayMode mode)
    {
        var settings = (WorkspaceRevealPanel)window.FindName("ControlPanel");
        var content = (Border)window.FindName("ControlPanelContent");
        var left = (WorkspaceRevealPanel)window.FindName("LeftPanelHost");
        var preview = (FrameworkElement)window.FindName("PreviewPanel");
        var grid = (Grid)window.FindName("MainContentGrid");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        void Settings(bool visible) => AuditFixCall(window, "SetSettingsPanelVisible", visible);
        void Left(string panel) => AuditFixCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, panel));
        bool Running() => ((RevealTransition?)typeof(MainWindow).GetField("_workspaceTransition", InteractionMembers)!
            .GetValue(window))?.IsRunning == true;
        void Check(bool condition, string message) => InteractionAssert(condition, mode + ": " + message);
        void Settle()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (Running() && watch.Elapsed.TotalSeconds < 3)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            window.UpdateLayout();
            Check(!Running(), "Clock did not detach after completion.");
        }
        void Final(bool visible)
        {
            Settle();
            Check(settings.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed), "Wrong final visibility.");
            Check(Math.Abs(settings.Width - (visible ? 336 : 0)) < 0.01, "Wrong final width.");
            Check(settings.Opacity == 1 && !settings.HasAnimatedProperties, "Residual opacity/width clock.");
            Check(((FrameworkElement)window.FindName("DevicePanel")).Opacity == 1 &&
                  ((FrameworkElement)window.FindName("MirroringPanel")).Opacity == 1, "Residual page opacity.");
        }

        // Record SizeChanged during the actual layout, rather than sampling
        // from a Background timer that can run only after busy rendering ends.
        var firstRevealWidths = new List<double>();
        SizeChangedEventHandler firstReveal = (_, e) => firstRevealWidths.Add(e.NewSize.Width);
        settings.SizeChanged += firstReveal;
        try { Settings(true); Final(true); }
        finally { settings.SizeChanged -= firstReveal; }
        Check(firstRevealWidths.Any(width => width > 0 && width < 335),
            "Expansion skipped its intermediate geometry: " + string.Join(",", firstRevealWidths));
        var stableWidth = content.ActualWidth;
        var sizes = new List<double>();
        EventHandler sample = (_, _) => { if (settings.IsVisible) sizes.Add(content.ActualWidth); };
        CompositionTarget.Rendering += sample;
        try { Settings(false); Final(false); Settings(true); Final(true); }
        finally { CompositionTarget.Rendering -= sample; }
        Check(sizes.Count > 1 && sizes.All(w => Math.Abs(w - stableWidth) < 0.01), "Card content squeezed during reveal.");

        // Reversals start at effective values, including several clicks in one
        // dispatcher turn (no Measure/Arrange has run between these clicks).
        for (var i = 0; i < 40; i++)
        {
            var width = settings.Width;
            Settings(i % 2 != 0);
            Check(Math.Abs(settings.Width - width) < 0.01, "Retarget jumped to a stale/base width.");
            if (i % 4 == 0) Left(i % 8 == 0 ? "Mirroring" : "Devices");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(12));
        }
        Settings(true); Left("Devices"); Final(true);
        Check(left.Visibility == Visibility.Visible && Math.Abs(left.Width - 300) < 0.01, "Concurrent left panel lost state.");

        foreach (var language in new[] { "zh-CN", "en-US", "zh-HK", "zh-TW" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            Settings(false); AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            AuditFixType("Localization.LocalizationService").GetMethod("ApplyLanguage", InteractionMembers)!
                .Invoke(null, [language, false, true]);
            ThemeService.Apply(theme);
            Settings(true); Final(true);
            Check(content.ActualWidth == stableWidth, "Theme/language changed reveal content width.");
        }

        Settings(false); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        window.Hide(); Check(!Running(), "Hidden window retains clock.");
        window.Show(); Final(false);
        Settings(true); AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
        window.WindowState = WindowState.Minimized;
        Check(!Running(), "Minimized window retains clock.");
        window.WindowState = WindowState.Normal; Final(true);
        Settings(false); Final(false);
        Settings(true); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        grid.Children.Remove(settings);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
        Check(!Running(), "Unloaded panel retains clock.");
        grid.Children.Add(settings); Final(true);
        Settings(false); AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
        left.Visibility = Visibility.Hidden;
        Check(!Running() && left.Visibility == Visibility.Hidden, "External hide was undone or left a clock.");
        left.Visibility = Visibility.Visible; Final(false);

        foreach (var size in new[] { new Size(768, 416), new Size(960, 600), new Size(1280, 800), new Size(1800, 900) })
        {
            Settings(true); AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
            window.MinWidth = window.MinHeight = 0;
            window.Width = size.Width; window.Height = size.Height;
            Final(true);
            Check(preview.ActualWidth > 100 && preview.ActualHeight > 60, "Resize lost usable preview geometry.");
            Settings(false); Final(false);
            Check(grid.ColumnDefinitions.All(c => c.ActualWidth >= 0), "Negative column width.");
        }

        // Exercise many complete transitions as well as the in-flight reversals
        // above. The same clock owner is reused and no finished clock survives.
        var clockOwner = typeof(MainWindow).GetField("_workspaceTransition", InteractionMembers)!.GetValue(window);
        for (var i = 0; i < 100; i++)
        {
            Settings(i % 2 == 0);
            Settle();
            Check(ReferenceEquals(clockOwner, typeof(MainWindow).GetField("_workspaceTransition", InteractionMembers)!.GetValue(window)),
                "Repeated interaction replaced the clock owner.");
        }
        Settings(true); AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
        AuditFixCall(window, "ApplyWorkspacePanelState", false);
        Check(!Running(), "Immediate state application retained an old animation.");
        Final(true);
        AuditFixCall(window, "OnMaximizeClick", window, new RoutedEventArgs());
        Settings(false); Final(false);
        Settings(true); Final(true);
        AuditFixCall(window, "OnMaximizeClick", window, new RoutedEventArgs());

        // Scale-aware layout arithmetic, separate from a physical monitor/DPI
        // transition (which this deterministic fixture cannot emulate).
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var probe = new WorkspaceRevealPanel { ContentWidth = 336, Width = 140.0 / scale };
            var child = new Border { Child = new TextBlock { Text = "Stable wrapping content", TextWrapping = TextWrapping.Wrap } };
            probe.Children.Add(child);
            probe.Measure(new Size(140.0 / scale, 300.0 / scale));
            probe.Arrange(new Rect(0, 0, 140.0 / scale, 300.0 / scale));
            Check(Math.Abs(child.ActualWidth - 336) < 0.01 && probe.ActualWidth < 336, "Fractional DIP viewport reflowed content.");
        }
        Settings(false); AdvanceDispatcher(TimeSpan.FromMilliseconds(15));
        var finalClock = (RevealTransition?)typeof(MainWindow).GetField("_workspaceTransition", InteractionMembers)!.GetValue(window);
        CloseWorkspaceTestWindow(window);
        Check(finalClock?.IsRunning != true, "Closed window retained a clock.");
        Console.WriteLine($"{mode}: reveal/reversal/40 rapid clicks/100 complete transitions/pages/themes/4 languages/hide/minimize/unload/close/4 sizes/fractional DIP checks passed.");
    }
}
