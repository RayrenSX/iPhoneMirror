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

    internal static Size GetAvailableSize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return SystemParameters.WorkArea.Size;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        return new Size((info.WorkArea.Right - info.WorkArea.Left) / scale,
            (info.WorkArea.Bottom - info.WorkArea.Top) / scale);
    }

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
        HwndSource? source = null;
        nint lastMonitor = 0;

        nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
            ref bool handled)
        {
            if (message != 0x0024) return 0; // WM_GETMINMAXINFO
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return 0;
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var width = info.WorkArea.Right - info.WorkArea.Left;
            var height = info.WorkArea.Bottom - info.WorkArea.Top;
            var dpi = GetDpiForWindow(hwnd);
            var scale = (dpi == 0 ? 96d : dpi) / 96d;
            if (double.IsFinite(window.MaxWidth))
                width = Math.Min(width, (int)Math.Ceiling(window.MaxWidth * scale));
            if (double.IsFinite(window.MaxHeight))
                height = Math.Min(height, (int)Math.Ceiling(window.MaxHeight * scale));
            limits.MaxPosition = new NativePoint(info.WorkArea.Left - info.Monitor.Left,
                info.WorkArea.Top - info.Monitor.Top);
            limits.MaxSize = new NativePoint(width, height);
            if (double.IsFinite(window.MaxWidth)) limits.MaxTrackSize.X = width;
            if (double.IsFinite(window.MaxHeight)) limits.MaxTrackSize.Y = height;
            limits.MinTrackSize = new NativePoint(
                Math.Min(width, Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * scale))),
                Math.Min(height, Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * scale))));
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true;
            return 0;
        }

        void InstallHook()
        {
            if (source is not null) return;
            var handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowProcedure);
            lastMonitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        }

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
            if (window.WindowState != WindowState.Normal) return;
            var scale = (dpi == 0 ? 96d : dpi) / 96d;
            window.Width = layout.Width / scale;
            window.Height = layout.Height / scale;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            _ = SetWindowPos(handle, 0, layout.Left, layout.Top,
                layout.Width, layout.Height, SwpNoZOrder | SwpNoActivate);
        }

        window.SourceInitialized += (_, _) =>
        {
            InstallHook();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => FitToCurrentMonitor(center: false)));
        };
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => FitToCurrentMonitor(center: false)));
        window.LocationChanged += (_, _) =>
        {
            var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, MonitorDefaultToNearest);
            if (monitor == lastMonitor) return;
            lastMonitor = monitor;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => FitToCurrentMonitor(center: false)));
        };
        window.Closed += (_, _) => source?.RemoveHook(WindowProcedure);

        if (new WindowInteropHelper(window).Handle != 0)
        {
            InstallHook();
            FitToCurrentMonitor(center: false);
        }
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
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
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
