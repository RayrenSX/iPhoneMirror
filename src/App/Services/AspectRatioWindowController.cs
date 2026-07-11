using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Keeps a native preview window's client area at the source-video aspect ratio.
/// All calculations are performed in physical pixels so mixed-DPI resizing does
/// not accumulate WPF DIP rounding errors.
/// </summary>
internal sealed class AspectRatioWindowController : IDisposable
{
    private const int WmSizing = 0x0214;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const double InitialWorkAreaFraction = 0.82;
    private const double ChangedWorkAreaFraction = 0.92;

    private readonly Window? _window;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _canResize;
    private readonly double _minWidthDips;
    private readonly double _minHeightDips;
    private HwndSource? _source;
    private nint _handle;
    private double _aspectRatio;
    private bool _disposed;
    private bool _resizeQueued;

    internal AspectRatioWindowController(Window window, uint sourceWidth, uint sourceHeight)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
        _canResize = () => window.WindowState == WindowState.Normal;
        _minWidthDips = window.MinWidth;
        _minHeightDips = window.MinHeight;
        _aspectRatio = ValidAspect(sourceWidth, sourceHeight) ?? (201.0 / 437.0);
        _window.SourceInitialized += OnSourceInitialized;
        _window.StateChanged += OnStateChanged;
        _window.DpiChanged += OnDpiChanged;
        _window.Closed += OnClosed;
    }

    /// <summary>
    /// Raw-HWND variant used by the no-redirection DirectComposition preview.
    /// The controller observes the supplied HwndSource but never owns it.
    /// </summary>
    internal AspectRatioWindowController(HwndSource source, uint sourceWidth, uint sourceHeight,
        Func<bool> canResize, double minWidthDips = 320, double minHeightDips = 240)
    {
        _dispatcher = source.Dispatcher;
        _canResize = canResize;
        _minWidthDips = minWidthDips;
        _minHeightDips = minHeightDips;
        _aspectRatio = ValidAspect(sourceWidth, sourceHeight) ?? (201.0 / 437.0);
        AttachSource(source);
        QueueResize(fitToWorkArea: true);
    }

    internal void SetSourceDimensions(uint width, uint height)
    {
        var newAspect = ValidAspect(width, height);
        if (newAspect is null || Math.Abs(newAspect.Value - _aspectRatio) < 0.00001) return;

        _aspectRatio = newAspect.Value;
        QueueResize(fitToWorkArea: false);
    }

    internal void Reflow() => QueueResize(fitToWorkArea: false);

    private static double? ValidAspect(uint width, uint height)
    {
        if (width == 0 || height == 0) return null;
        var aspect = width / (double)height;
        // Reject transient/corrupt native status while still allowing normal
        // portrait, landscape, tablet, and external-display shapes.
        return aspect is >= 0.20 and <= 5.0 ? aspect : null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_window is null) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        if (source is null) return;
        AttachSource(source);
        QueueResize(fitToWorkArea: true);
    }

    private void AttachSource(HwndSource source)
    {
        _source = source;
        _handle = source.Handle;
        _source.AddHook(WindowProcedure);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Normal)
            QueueResize(fitToWorkArea: false);
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) =>
        QueueResize(fitToWorkArea: false);

    private void QueueResize(bool fitToWorkArea)
    {
        if (_disposed || _handle == 0 || _resizeQueued) return;
        _resizeQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _resizeQueued = false;
            ResizeWindowToAspect(fitToWorkArea);
        });
    }

    private void ResizeWindowToAspect(bool fitToWorkArea)
    {
        if (_disposed || _handle == 0 || !_canResize() ||
            !GetWindowRect(_handle, out var current) || !TryGetWorkArea(out var workArea)) return;

        GetFrameSize(out var frameWidth, out var frameHeight);
        var workWidth = Math.Max(1, workArea.Right - workArea.Left);
        var workHeight = Math.Max(1, workArea.Bottom - workArea.Top);
        double clientWidth;
        double clientHeight;

        if (fitToWorkArea)
        {
            clientWidth = workWidth * InitialWorkAreaFraction;
            clientHeight = clientWidth / _aspectRatio;
            var maxHeight = workHeight * InitialWorkAreaFraction;
            if (clientHeight > maxHeight)
            {
                clientHeight = maxHeight;
                clientWidth = clientHeight * _aspectRatio;
            }
        }
        else
        {
            // Preserve visual area when the phone rotates. A 90-degree source
            // rotation therefore swaps the window's long/short sides instead
            // of unexpectedly making the preview much larger or smaller.
            var currentClientWidth = Math.Max(1, current.Right - current.Left - frameWidth);
            var currentClientHeight = Math.Max(1, current.Bottom - current.Top - frameHeight);
            var currentArea = (double)currentClientWidth * currentClientHeight;
            clientHeight = Math.Sqrt(currentArea / _aspectRatio);
            clientWidth = clientHeight * _aspectRatio;

            var scaleDown = Math.Min(1.0, Math.Min(
                workWidth * ChangedWorkAreaFraction / clientWidth,
                workHeight * ChangedWorkAreaFraction / clientHeight));
            clientWidth *= scaleDown;
            clientHeight *= scaleDown;
        }

        var dpi = GetDpiForWindow(_handle);
        var dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;
        var minClientWidth = Math.Max(1.0, _minWidthDips * dpiScale - frameWidth);
        var minClientHeight = Math.Max(1.0, _minHeightDips * dpiScale - frameHeight);
        var minimumScale = Math.Max(minClientWidth / clientWidth, minClientHeight / clientHeight);
        var maximumScale = Math.Min(
            workWidth * ChangedWorkAreaFraction / clientWidth,
            workHeight * ChangedWorkAreaFraction / clientHeight);
        var uniformScale = Math.Min(Math.Max(1.0, minimumScale), maximumScale);
        clientWidth *= uniformScale;
        clientHeight *= uniformScale;

        var targetWidth = Math.Max(1, (int)Math.Round(clientWidth) + frameWidth);
        var targetHeight = Math.Max(1, (int)Math.Round(clientHeight) + frameHeight);
        var centerX = fitToWorkArea
            ? (workArea.Left + workArea.Right) / 2
            : (current.Left + current.Right) / 2;
        var centerY = fitToWorkArea
            ? (workArea.Top + workArea.Bottom) / 2
            : (current.Top + current.Bottom) / 2;
        var left = Math.Clamp(centerX - targetWidth / 2,
            workArea.Left, Math.Max(workArea.Left, workArea.Right - targetWidth));
        var top = Math.Clamp(centerY - targetHeight / 2,
            workArea.Top, Math.Max(workArea.Top, workArea.Bottom - targetHeight));

        _ = SetWindowPos(_handle, 0, left, top, targetWidth, targetHeight,
            SwpNoActivate | SwpNoZOrder);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message != WmSizing || lParam == 0 || !_canResize())
            return 0;

        var rectangle = Marshal.PtrToStructure<WindowRect>(lParam);
        ConstrainSizingRectangle(wParam.ToInt32(), ref rectangle);
        Marshal.StructureToPtr(rectangle, lParam, false);
        handled = true;
        return 1;
    }

    private void ConstrainSizingRectangle(int edge, ref WindowRect rectangle)
    {
        GetFrameSize(out var frameWidth, out var frameHeight);
        var outerWidth = Math.Max(1, rectangle.Right - rectangle.Left);
        var outerHeight = Math.Max(1, rectangle.Bottom - rectangle.Top);
        var clientWidth = Math.Max(1, outerWidth - frameWidth);
        var clientHeight = Math.Max(1, outerHeight - frameHeight);

        var dpi = GetDpiForWindow(_handle);
        var dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;
        var minClientWidth = Math.Max(1.0, _minWidthDips * dpiScale - frameWidth);
        var minClientHeight = Math.Max(1.0, _minHeightDips * dpiScale - frameHeight);

        var heightFromWidth = clientWidth / _aspectRatio;
        var widthFromHeight = clientHeight * _aspectRatio;
        var widthDriven = edge is 1 or 2 ||
            (edge is 4 or 5 or 7 or 8 &&
             Math.Abs(heightFromWidth - clientHeight) / clientHeight <=
             Math.Abs(widthFromHeight - clientWidth) / clientWidth);

        double targetClientWidth;
        double targetClientHeight;
        if (widthDriven)
        {
            targetClientWidth = Math.Max(clientWidth,
                Math.Max(minClientWidth, minClientHeight * _aspectRatio));
            targetClientHeight = targetClientWidth / _aspectRatio;
        }
        else
        {
            targetClientHeight = Math.Max(clientHeight,
                Math.Max(minClientHeight, minClientWidth / _aspectRatio));
            targetClientWidth = targetClientHeight * _aspectRatio;
        }

        var targetOuterWidth = Math.Max(1, (int)Math.Round(targetClientWidth) + frameWidth);
        var targetOuterHeight = Math.Max(1, (int)Math.Round(targetClientHeight) + frameHeight);
        var dragLeft = edge is 1 or 4 or 7;
        var dragTop = edge is 3 or 4 or 5;

        if (dragLeft) rectangle.Left = rectangle.Right - targetOuterWidth;
        else rectangle.Right = rectangle.Left + targetOuterWidth;
        if (dragTop) rectangle.Top = rectangle.Bottom - targetOuterHeight;
        else rectangle.Bottom = rectangle.Top + targetOuterHeight;
    }

    private void GetFrameSize(out int frameWidth, out int frameHeight)
    {
        frameWidth = 0;
        frameHeight = 0;
        if (_handle == 0 || !GetWindowRect(_handle, out var windowRectangle) ||
            !GetClientRect(_handle, out var clientRectangle)) return;
        frameWidth = Math.Max(0,
            windowRectangle.Right - windowRectangle.Left - clientRectangle.Right + clientRectangle.Left);
        frameHeight = Math.Max(0,
            windowRectangle.Bottom - windowRectangle.Top - clientRectangle.Bottom + clientRectangle.Top);
    }

    private bool TryGetWorkArea(out WindowRect workArea)
    {
        workArea = default;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        if (monitor == 0) return false;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        workArea = info.WorkArea;
        return true;
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _handle = 0;
        if (_window is not null)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.StateChanged -= OnStateChanged;
            _window.DpiChanged -= OnDpiChanged;
            _window.Closed -= OnClosed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
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
        internal WindowRect Monitor;
        internal WindowRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);
}
