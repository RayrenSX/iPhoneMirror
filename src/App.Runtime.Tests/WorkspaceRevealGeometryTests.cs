using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using IPhoneMirror.UI.Animations;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestWorkspaceRevealGeometry(App app)
    {
        var originalMode = GetApplicationDisplayMode(app);
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    if (mode == ApplicationDisplayMode.Complete) TestStackedWorkspaceClose(window);
                    else TestSuspendedWorkspaceGeometry(window);
                }
                finally { CloseWorkspaceTestWindow(window); }
            }
        }
        finally { SetApplicationDisplayMode(app, originalMode); }
    }

    private static bool WorkspaceRevealRunning(MainWindow window) =>
        ((RevealTransition?)typeof(MainWindow).GetField("_workspaceTransition", InteractionMembers)!
            .GetValue(window))?.IsRunning == true;

    private static void FinishWorkspaceReveal(MainWindow window)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (WorkspaceRevealRunning(window) && watch.Elapsed.TotalSeconds < 3)
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
        window.UpdateLayout();
        InteractionAssert(!WorkspaceRevealRunning(window), "Workspace reveal did not finish.");
    }

    // Dispatch outside Measure/Arrange, at the first real intermediate frame.
    // A fixed timer can miss the animation entirely on a busy dispatcher.
    private static void InterruptWorkspaceReveal(MainWindow window, FrameworkElement surface,
        double fullWidth, Action start, Action interrupt)
    {
        var queued = false;
        var interrupted = false;
        DispatcherOperation? operation = null;
        SizeChangedEventHandler capture = (_, _) =>
        {
            if (queued || surface.Width <= 1 || surface.Width >= fullWidth - 1) return;
            queued = true;
            operation = window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                InteractionAssert(WorkspaceRevealRunning(window), "Interruption missed the active reveal.");
                interrupt();
                interrupted = true;
            });
        };
        surface.SizeChanged += capture;
        try
        {
            start();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!interrupted && watch.Elapsed.TotalSeconds < 3)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(interrupted, "No intermediate frame was available for interruption.");
        }
        finally
        {
            surface.SizeChanged -= capture;
            operation?.Abort();
        }
    }

    private static void TestStackedWorkspaceClose(MainWindow window)
    {
        var settings = (FrameworkElement)window.FindName("ControlPanel");
        var left = (FrameworkElement)window.FindName("LeftPanelHost");
        var preview = (FrameworkElement)window.FindName("PreviewPanel");
        var grid = (Grid)window.FindName("MainContentGrid");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        void Settings(bool visible) => AuditFixCall(window, "SetSettingsPanelVisible", visible);
        void Left(bool visible) => AuditFixCall(window, "SetLeftWorkspacePanel",
            Enum.Parse(panelType, visible ? "Devices" : "None"));
        void OpenBoth()
        {
            Left(true); Settings(true);
            AuditFixCall(window, "ApplyWorkspacePanelState", false);
            window.UpdateLayout();
            InteractionAssert(Grid.GetRow(settings) == 1, "Fixture did not enter stacked layout.");
        }
        window.MinWidth = 0; // Exercise the layout used on small/high-DPI work areas.
        window.Height = 700;
        foreach (var width in new[] { 768d, 900d, 960d })
        foreach (var closeSettings in new[] { true, false })
        {
            window.Width = width;
            OpenBoth();
            var beforePreview = preview.ActualWidth;
            var beforeSettingsHeight = settings.ActualHeight;
            var beforeLeftHeight = left.ActualHeight;
            var samples = new List<(double Width, double Preview, double SettingsHeight, double LeftHeight, int Row)>();
            var outgoing = closeSettings ? settings : left;
            EventHandler capture = (_, _) => samples.Add((outgoing.Width, preview.ActualWidth,
                settings.ActualHeight, left.ActualHeight, Grid.GetRow(settings)));
            window.LayoutUpdated += capture;
            try
            {
                if (closeSettings) Settings(false); else Left(false);
                window.UpdateLayout();
                InteractionAssert(Grid.GetRow(settings) == 1, "Closing card moved before the reveal began.");
                FinishWorkspaceReveal(window);
            }
            finally { window.LayoutUpdated -= capture; }
            var intermediate = samples.Where(s => s.Width > 1 && s.Width < (closeSettings ? 335 : 299)).ToArray();
            InteractionAssert(intermediate.Length > 0, "Stacked close skipped intermediate geometry.");
            InteractionAssert(samples.All(s => s.Preview >= beforePreview - 1),
                $"Closing a stacked card squeezed the preview at {width} DIP: {beforePreview:F1} -> {samples.Min(s => s.Preview):F1}.");
            InteractionAssert(intermediate.All(s => s.Row == 1 &&
                Math.Abs(s.SettingsHeight - beforeSettingsHeight) < 1 && Math.Abs(s.LeftHeight - beforeLeftHeight) < 1),
                "Stacked card changed rows/heights before closing.");
            InteractionAssert(Grid.GetRow(settings) == 0 && grid.RowDefinitions.Count == 0,
                "Stacked rows remained after the close.");
        }
        OpenBoth();
        InterruptWorkspaceReveal(window, settings, 336, () => Settings(false), () => Settings(true));
        FinishWorkspaceReveal(window);
        InteractionAssert(Grid.GetRow(settings) == 1 && settings.Width == 336, "Reversal lost stacked layout.");
        InterruptWorkspaceReveal(window, settings, 336, () => Settings(false),
            () => AuditFixCall(window, "ApplyWorkspacePanelState", false));
        InteractionAssert(Grid.GetRow(settings) == 0 && !WorkspaceRevealRunning(window),
            "Immediate state application retained transitional rows.");
        OpenBoth();
        InterruptWorkspaceReveal(window, settings, 336, () => Settings(false), () => window.Width = 1280);
        FinishWorkspaceReveal(window);
        InteractionAssert(Grid.GetRow(settings) == 0 && grid.RowDefinitions.Count == 0,
            "Resize across the breakpoint retained transitional rows.");
        Console.WriteLine("Complete: 768/900/960 DIP close-left/close-settings intermediate geometry, reversal, immediate apply and breakpoint resize passed.");
    }

    private static void TestSuspendedWorkspaceGeometry(MainWindow window)
    {
        var settings = (FrameworkElement)window.FindName("ControlPanel");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        void Settings(bool visible) => AuditFixCall(window, "SetSettingsPanelVisible", visible);
        AuditFixCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
        Settings(false); FinishWorkspaceReveal(window);
        window.Left = 30;
        Settings(true); FinishWorkspaceReveal(window);
        var openWidth = window.ActualWidth;
        Settings(false); FinishWorkspaceReveal(window);
        var closedWidth = window.ActualWidth;

        foreach (var minimize in new[] { false, true })
        foreach (var opening in new[] { false, true })
        {
            Settings(!opening); FinishWorkspaceReveal(window);
            InterruptWorkspaceReveal(window, settings, 336, () => Settings(opening), () =>
            {
                if (minimize) window.WindowState = WindowState.Minimized;
                else window.Hide();
            });
            InteractionAssert(!WorkspaceRevealRunning(window), "Suspended window retained a reveal clock.");
            if (minimize) window.WindowState = WindowState.Normal;
            else window.Show();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            FinishWorkspaceReveal(window);
            var expected = opening ? openWidth : closedWidth;
            InteractionAssert(Math.Abs(window.ActualWidth - expected) <= 1,
                $"Restore left an intermediate window width (minimize={minimize}, opening={opening}): {window.ActualWidth:F1}, expected {expected:F1}.");
            InteractionAssert(settings.Width == (opening ? 336 : 0), "Restore lost the requested panel state.");
        }

        // Cancel before the Loaded-priority clock starts, then hide again before
        // the queued resume fit runs. The fit must survive that second hide.
        Settings(true); FinishWorkspaceReveal(window);
        Settings(false); window.Hide(); window.Show(); window.Hide();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        window.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        InteractionAssert(Math.Abs(window.ActualWidth - closedWidth) <= 1 && !WorkspaceRevealRunning(window),
            "Repeated suspension lost the pending window fit.");

        Settings(true); FinishWorkspaceReveal(window);
        InterruptWorkspaceReveal(window, settings, 336, () => Settings(false), () => window.Width = 900);
        FinishWorkspaceReveal(window);
        window.Hide(); window.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        InteractionAssert(Math.Abs(window.ActualWidth - 900) <= 1,
            "Resume fit overrode a manual resize that interrupted the animation.");
        Console.WriteLine($"Lightweight: interrupted open/close with hide/show and minimize/restore, repeated suspension, and manual resize passed (open={openWidth:F1}, closed={closedWidth:F1} DIP).");
    }
}
