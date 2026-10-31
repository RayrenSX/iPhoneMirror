using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace IPhoneMirror.UI.Controls;

/// <summary>Shared, self-drawn chrome for WPF-only secondary windows.</summary>
public class RoundedWindow : Window
{
    private HwndSource? _source;
    public static readonly DependencyProperty IsChromeCloseButtonProperty = DependencyProperty.RegisterAttached(
        "IsChromeCloseButton", typeof(bool), typeof(RoundedWindow), new PropertyMetadata(false, OnChromeCloseButtonChanged));
    public static bool GetIsChromeCloseButton(DependencyObject target) => (bool)target.GetValue(IsChromeCloseButtonProperty);
    public static void SetIsChromeCloseButton(DependencyObject target, bool value) => target.SetValue(IsChromeCloseButtonProperty, value);

    private static void OnChromeCloseButtonChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Button button) return;
        if ((bool)args.NewValue) button.Loaded += PlaceCloseButton;
        else button.Loaded -= PlaceCloseButton;
    }

    private static void PlaceCloseButton(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        if (Window.GetWindow(button) is not RoundedWindow window ||
            window.Template.FindName("PART_WindowActions", window) is not Panel host || button.Parent == host) return;
        // Preserve the original handlers, accessible name and close-confirmation
        // semantics; the window action alone stays outside the scrolling page.
        if (button.Parent is not Panel parent) return;
        parent.Children.Remove(button);
        host.Children.Add(button);
    }

    public RoundedWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SetResourceReference(StyleProperty, typeof(RoundedWindow));
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (AllowsTransparency) return;
        // Large, animated pages use an ordinary HWND. Windows owns the
        // non-client resize frame, shadow and rounded corners in this mode.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (_source is null) return;
        SizeForReadingText(this);
        _source.AddHook(WindowProcedure);
        ApplyNativeCorners();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyNativeCorners();
    }

    private void ApplyNativeCorners()
    {
        if (_source is null) return;
        // Layered windows already draw their own silhouette. Native windows
        // can use DWM corners without a per-pixel alpha surface.
        var corners = !AllowsTransparency && WindowState == WindowState.Normal ? 2 : 1;
        _ = DwmSetWindowAttribute(_source.Handle, 33, ref corners, sizeof(int));
    }

    internal static void SizeForReadingText(Window window)
    {
        var scale = window.TryFindResource("BodyFontSize") is double body ? body / 14 : 1;
        if (scale <= 1 || window.WindowState != WindowState.Normal) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (source is null || !GetMonitorInfoW(MonitorFromWindow(source.Handle, 2), ref monitor)) return;
        var transform = source.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var area = transform.Transform(new Vector(monitor.Work.Right - monitor.Work.Left,
            monitor.Work.Bottom - monitor.Work.Top));
        // Text scaling needs more line length as well as taller controls. Grow
        // the initial dialog only; subsequent resizing remains the user's choice.
        // Leave the full-page scroll fallback available on smaller displays.
        if (!double.IsNaN(window.Width)) window.Width = Math.Max(window.MinWidth, Math.Min(window.Width * (1 + .35 * (scale - 1)), Math.Min(Math.Min(window.MaxWidth, Math.Max(window.Width, 1280)), area.X - 32)));
        if (!double.IsNaN(window.Height)) window.Height = Math.Max(window.MinHeight, Math.Min(window.Height * (1 + .18 * (scale - 1)), Math.Min(Math.Min(window.MaxHeight, Math.Max(window.Height, 860)), area.Y - 32)));
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        base.OnClosed(e);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO: maximize inside this monitor's work area.
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info))
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
                limits.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                var scale = _source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                limits.MinTrackSize = new NativePoint(
                    Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(MinWidth * scale.M11)),
                    Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(MinHeight * scale.M22)));
                if (!double.IsPositiveInfinity(MaxWidth)) limits.MaxTrackSize.X = (int)Math.Ceiling(MaxWidth * scale.M11);
                if (!double.IsPositiveInfinity(MaxHeight)) limits.MaxTrackSize.Y = (int)Math.Ceiling(MaxHeight * scale.M22);
                Marshal.StructureToPtr(limits, lParam, false);
                handled = true;
            }
        }
        if (!AllowsTransparency || message != 0x0084 || WindowState != WindowState.Normal ||
            ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)) return 0;
        // Layered windows have no native resize border. Keep a 6-DIP resize band
        // around the painted surface; signed coordinates support monitors left of primary.
        var point = PointFromScreen(new Point((short)(lParam.ToInt64() & 0xffff),
            (short)((lParam.ToInt64() >> 16) & 0xffff)));
        var inset = TryFindResource("WindowShadowMargin") is Thickness margin ? margin.Left : 10;
        var edge = inset + 6;
        var left = point.X < edge;
        var right = point.X >= ActualWidth - edge;
        var top = point.Y < edge;
        var bottom = point.Y >= ActualHeight - edge;
        var hit = top ? (left ? 13 : right ? 14 : 12)
            : bottom ? (left ? 16 : right ? 17 : 15)
            : left ? 10 : right ? 11 : 0;
        handled = hit != 0;
        return hit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>Clips descendants, including animated content, inside the painted border.</summary>
public sealed class RoundedContentClip : Decorator
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(RoundedContentClip),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsArrange));
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        var radius = Math.Max(0, Radius - 1); // one-DIP frame is outside this content
        Clip = new RectangleGeometry(new Rect(size), radius, radius);
        return size;
    }
}
