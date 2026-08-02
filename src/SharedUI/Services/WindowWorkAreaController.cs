using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IPhoneMirror.SharedUI.Services;

internal readonly record struct WindowWorkAreaLayout(
    int Left,
    int Top,
    int Width,
    int Height,
    double MinWidth,
    double MinHeight);

internal static class WindowWorkAreaController
{
    private const double HighDpiWorkAreaRatio = 0.80;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();

    internal static void EnableForApplication()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));
    }

    internal static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!AttachedWindows.TryAdd(window, new object())) return;
        var designMinWidth = window.MinWidth;
        var designMinHeight = window.MinHeight;

        void FitToCurrentMonitor(bool center)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0 || !GetWindowRect(handle, out var current)) return;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            };
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo)) return;

            var dpi = GetDpiForWindow(handle);
            var layout = CalculateLayout(
                current.Left, current.Top,
                current.Right - current.Left, current.Bottom - current.Top,
                monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top,
                monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top,
                dpi == 0 ? 96u : dpi, designMinWidth, designMinHeight, center);

            window.MinWidth = layout.MinWidth;
            window.MinHeight = layout.MinHeight;
            var scale = (dpi == 0 ? 96d : dpi) / 96d;
            window.Width = layout.Width / scale;
            window.Height = layout.Height / scale;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            _ = SetWindowPos(handle, 0, layout.Left, layout.Top,
                layout.Width, layout.Height, SwpNoZOrder | SwpNoActivate);
        }

        window.SourceInitialized += (_, _) => window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => FitToCurrentMonitor(center: false)));
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => FitToCurrentMonitor(center: false)));

        if (new WindowInteropHelper(window).Handle != 0)
            FitToCurrentMonitor(center: false);
    }

    internal static WindowWorkAreaLayout CalculateLayout(
        int currentLeft,
        int currentTop,
        int currentWidth,
        int currentHeight,
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        uint dpi,
        double designMinWidth,
        double designMinHeight,
        bool center)
    {
        if (workWidth <= 0) throw new ArgumentOutOfRangeException(nameof(workWidth));
        if (workHeight <= 0) throw new ArgumentOutOfRangeException(nameof(workHeight));
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        var maximumWidth = dpi > 96
            ? Math.Max(1, (int)Math.Round(workWidth * HighDpiWorkAreaRatio))
            : workWidth;
        var maximumHeight = dpi > 96
            ? Math.Max(1, (int)Math.Round(workHeight * HighDpiWorkAreaRatio))
            : workHeight;
        var width = Math.Min(Math.Max(1, currentWidth), maximumWidth);
        var height = Math.Min(Math.Max(1, currentHeight), maximumHeight);
        var maxLeft = workLeft + workWidth - width;
        var maxTop = workTop + workHeight - height;
        var left = center
            ? workLeft + (workWidth - width) / 2
            : Math.Clamp(currentLeft, workLeft, maxLeft);
        var top = center
            ? workTop + (workHeight - height) / 2
            : Math.Clamp(currentTop, workTop, maxTop);
        var scale = dpi / 96d;

        return new WindowWorkAreaLayout(
            left, top, width, height,
            Math.Min(Math.Max(0, designMinWidth), width / scale),
            Math.Min(Math.Max(0, designMinHeight), height / scale));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
