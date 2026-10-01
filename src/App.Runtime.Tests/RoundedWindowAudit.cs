using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void AssertSelfDrawnWindowCorners(Window window)
    {
        window.ApplyTemplate();
        window.UpdateLayout();
        var surface = window.Template.FindName("RoundedWindowSurface", window) as Border
            ?? throw new InvalidOperationException($"{window.GetType().Name}: shared rounded frame missing.");
        var clip = window.Template.FindName("RoundedWindowContentClip", window) as FrameworkElement
            ?? throw new InvalidOperationException("Rounded content clip missing.");
        if (!window.AllowsTransparency || window.WindowStyle != WindowStyle.None ||
            window.Background is not SolidColorBrush { Color.A: 0 } ||
            surface.CornerRadius != new CornerRadius(20) || surface.BorderThickness != new Thickness(1) ||
            clip.Clip is not RectangleGeometry { RadiusX: 19, RadiusY: 19 })
            throw new InvalidOperationException($"{window.GetType().Name}: rounded alpha frame/clip mismatch.");

        // Verify actual alpha output, not only a Border.CornerRadius declaration.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte Alpha(int x, int y)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel[3];
        }
        if (Alpha(0, 0) != 0 || Alpha(10, 10) > 64 ||
            Alpha(bitmap.PixelWidth / 2, 12) < 240)
            throw new InvalidOperationException($"{window.GetType().Name}: corner alpha is filled or frame is missing.");

        var chromeType = window.GetType().BaseType!;
        var hook = chromeType.GetMethod("WindowProcedure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handle = new WindowInteropHelper(window).Handle;
        var resizable = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        foreach (var (point, expected) in new[] {
            (new Point(11, window.ActualHeight / 2), 10),
            (new Point(window.ActualWidth - 11, window.ActualHeight / 2), 11),
            (new Point(window.ActualWidth / 2, 11), 12),
            (new Point(window.ActualWidth / 2, window.ActualHeight - 11), 15),
            (new Point(window.ActualWidth / 2, window.ActualHeight / 2), 0) })
        {
            var screen = window.PointToScreen(point);
            var packed = (nint)((uint)(ushort)(short)Math.Round(screen.X) |
                ((uint)(ushort)(short)Math.Round(screen.Y) << 16));
            object[] arguments = [handle, 0x0084, (nint)0, packed, false];
            var result = (nint)hook.Invoke(window, arguments)!;
            if (result != (resizable ? expected : 0))
                throw new InvalidOperationException("Custom resize hit-test changed window behavior.");
        }
        if (!resizable) return;
        window.WindowState = WindowState.Maximized;
        window.UpdateLayout();
        if (surface.CornerRadius != new CornerRadius(0) || surface.Margin != new Thickness(0))
            throw new InvalidOperationException("Maximized window retained floating rounded margins.");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        if (surface.CornerRadius != new CornerRadius(20))
            throw new InvalidOperationException("Restored window lost rounded corners.");
    }
}
