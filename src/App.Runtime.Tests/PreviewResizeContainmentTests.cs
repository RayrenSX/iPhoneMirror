using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewResizeContainmentTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            TestNativeAncestorContainment();
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Lightweight);
            var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            try
            {
                var center = (FrameworkElement)main.FindName("CenterPanel");
                var preview = (FrameworkElement)main.FindName("PreviewPanel");
                foreach (var width in new[] { 600d, 160d, 420d, 80d, 600d })
                {
                    center.Width = width;
                    KeyboardCall(main, "ApplyLightweightPreviewFramePolicy", (bool?)false, (double?)480);
                    main.UpdateLayout(); DrainDispatcher();
                    var bounds = preview.TransformToAncestor(center).TransformBounds(new Rect(preview.RenderSize));
                    InteractionAssert(bounds.Left >= -1 && bounds.Right <= center.ActualWidth + 1 && preview.ActualWidth > 0,
                        $"Preview escapes its resized center column: center={center.ActualWidth}, preview={bounds}.");
                    InteractionAssert(preview.MinWidth == 0 && preview.ActualWidth <= 481,
                        "Source-aspect sizing must be an upper bound, never a minimum that overlaps sidebars.");
                }
                center.ClearValue(FrameworkElement.WidthProperty);
                foreach (var paneOpen in new[] { true, false, true, false })
                {
                    var nav = (Wpf.Ui.Controls.NavigationView)main.FindName("RootNavigation");
                    nav.IsPaneOpen = paneOpen;
                    main.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    var bounds = preview.TransformToAncestor(center).TransformBounds(new Rect(preview.RenderSize));
                    InteractionAssert(bounds.Left >= -1 && bounds.Right <= center.ActualWidth + 1,
                        "Navigation expansion allowed the preview to cover adjacent UI.");
                }
                Console.WriteLine("PASS: source-aspect preview fits shrinking/growing columns and navigation expansion.");
            }
            finally { CloseWorkspaceTestWindow(main); }
            TestPreviewRegionAndCapture();
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestNativeAncestorContainment()
    {
        var host = new NativePreviewHost { Visibility = Visibility.Collapsed };
        // Use a real child HWND below without starting D3D or phone capture.
        // Disposal disables HwndHost's automatic renderer creation; the visual
        // ancestor chain remains available to the production region calculation.
        ((IDisposable)host).Dispose();
        var clipped = new Grid { ClipToBounds = true, Width = 200, Height = 180,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(150, 20, 0, 0) };
        clipped.Children.Add(host);
        var window = new Window { Width = 700, Height = 300, Content = clipped, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        var child = InteractionCreateWindow(0, "STATIC", "", 0x40000000,
            50, 0, 500, 250, new WindowInteropHelper(window).Handle, 0, 0, 0);
        var region = InteractionCreateRectRgn(0, 0, 0, 0);
        try
        {
            InteractionAssert(child != 0, "Could not create native clipping fixture.");
            InteractionSet(host, "_window", child);
            foreach (var left in new[] { 150d, 100d, 220d, 150d })
            {
                clipped.Margin = new Thickness(left, 20, 0, 0);
                window.UpdateLayout(); DrainDispatcher();
                // Exercise both the layout event and the production callback.
                KeyboardCall(host, "UpdateWindowRegion");
                InteractionAssert(InteractionGetWindowRgn(child, region) != 0,
                    "The oversized native child has no clipping region.");
                InteractionGetRgnBox(region, out var actual);
                PreviewStyleGetWindowRect(child, out var native);
                var origin = clipped.PointToScreen(new Point());
                var end = clipped.PointToScreen(new Point(clipped.ActualWidth, clipped.ActualHeight));
                InteractionAssert(native.Left + actual.Left >= origin.X - 1 && native.Top + actual.Top >= origin.Y - 1 &&
                    native.Left + actual.Right <= end.X + 1 && native.Top + actual.Bottom <= end.Y + 1,
                    $"Native region escapes ancestor: region={actual.Left},{actual.Top},{actual.Right},{actual.Bottom}, clip={origin}..{end}.");
            }
            Console.WriteLine("PASS: native HWND region follows ancestor clipping during position-only layout changes.");
        }
        finally
        {
            InteractionSet(host, "_window", (nint)0);
            InteractionDeleteObject(region); InteractionDestroyWindow(child); window.Close();
        }
    }
}
