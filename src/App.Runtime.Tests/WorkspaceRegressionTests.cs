using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWorkspaceRegressionTests()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, true);
        app.InitializeComponent();
        try
        {
            TestWorkspaceRevealGeometry(app);
            TestWorkspaceAnimations(app);
            TestWorkspaceRevealLifecycle(app);
            Console.WriteLine("Workspace position and animation regressions passed.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestWindowWorkAreaPosition(Application application)
    {
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Style = new Style(typeof(Window)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = area.Left - 80,
            Top = area.Top - 60,
            Width = 320,
            Height = 240,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        application.MainWindow = window;
        window.Show();
        try
        {
            var controller = typeof(App).Assembly.GetType(
                "IPhoneMirror.SharedUI.Services.WindowWorkAreaController")!;
            controller.GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [window]);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            // PointToScreen reads the actual HWND position. The managed Left/Top
            // must use that same origin before subsequent WPF layout or animation.
            var actual = window.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(window);
            if (Math.Abs(window.Left * dpi.DpiScaleX - actual.X) > 1 ||
                Math.Abs(window.Top * dpi.DpiScaleY - actual.Y) > 1 ||
                window.Left < area.Left - 1 || window.Top < area.Top - 1)
                throw new InvalidOperationException(
                    $"Work-area fit left stale WPF coordinates: {window.Left:F1}, {window.Top:F1}; actual client={actual}, dpi={dpi.DpiScaleX}.");
        }
        finally { window.Close(); }
    }

    private static void TestLightweightStartupWhileDispatcherBusy(Application application)
    {
        var busy = true;
        // Model normal queued UI work without blocking rendering or input.
        // Startup sizing must finish before input, even when ContextIdle has
        // not been reached. A background timer alone would leave idle gaps.
        void KeepDispatcherBusy()
        {
            if (busy)
                application.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(KeepDispatcherBusy));
        }
        KeepDispatcherBusy();
        MainWindow? window = null;
        try
        {
            window = CreateWorkspaceTestWindow(application, includeNativePreview: false);
            TestLightweightWorkspaceAnimation(window);
        }
        finally
        {
            busy = false;
            if (window is not null) CloseWorkspaceTestWindow(window);
        }
    }
}
