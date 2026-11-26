using System.Runtime.InteropServices;
using PixelRect = System.Drawing.Rectangle;
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
    private const int WmGetMinMaxInfo = 0x0024;
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
    private double? _interactiveAspectRatio;
    private PixelRect _boundsBeforeInteraction;
    private double? _preferredAutomaticArea;
    private bool _disposed;
    private DispatcherOperation? _queuedResize;
    internal bool AutomaticResizeEnabled { get; set; } = true;
    internal bool KeepPositionOnAutomaticResize { get; set; }
    internal Func<PixelRect, PixelRect, bool, PixelRect>? PlaceAutomaticBounds { get; set; }

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
    }

    /// <summary>
    /// Applies the first work-area fit synchronously while a raw HWND is still
    /// hidden, so its initial visible frame already has the final bounds.
    /// </summary>
    internal bool ApplyInitialBounds() => ResizeWindowToAspect(fitToWorkArea: true);

    internal void SetSourceDimensions(uint width, uint height)
    {
        var newAspect = ValidAspect(width, height);
        if (newAspect is null || Math.Abs(newAspect.Value - _aspectRatio) < 0.00001) return;

        _aspectRatio = newAspect.Value;
        QueueResize(fitToWorkArea: false);
    }

    internal void Reflow() => QueueResize(fitToWorkArea: false);

    internal void BeginInteractiveResize()
    {
        _interactiveAspectRatio ??= _aspectRatio;
        if (GetWindowRect(_handle, out var bounds)) _boundsBeforeInteraction = ToPixelRect(bounds);
    }

    internal void EndInteractiveResize()
    {
        if (_interactiveAspectRatio is not { } previous) return;
        _interactiveAspectRatio = null;
        if (PlaceAutomaticBounds is not null && GetWindowRect(_handle, out var result))
        {
            var bounds = ToPixelRect(result);
            if (bounds.Size != _boundsBeforeInteraction.Size)
            {
                GetFrameSize(out var frameWidth, out var frameHeight);
                _preferredAutomaticArea = ClientArea(bounds, frameWidth, frameHeight, GetDpiScale());
            }
        }
        if (Math.Abs(previous - _aspectRatio) >= 0.00001) Reflow();
    }

    // The native renderer is waiting to present the frame that changed the ratio.
    internal void ApplyPendingResize()
    {
        if (_queuedResize is null) return;
        _queuedResize.Abort();
        _queuedResize = null;
        _ = ResizeWindowToAspect(fitToWorkArea: false);
    }

    private static double? ValidAspect(uint width, uint height)
    {
        if (width == 0 || height == 0) return null;
        var aspect = width / (double)height;
        // Reject transient/corrupt native status while still allowing normal
        // portrait, landscape, tablet, and external-display shapes.
        // URL-video senders may expose ultrawide masters, vertical shorts, or
        // anamorphic/intermediate dimensions. Preserve those legitimate shapes
        // while still rejecting zero and clearly corrupt geometry.
        return aspect is >= 0.05 and <= 20.0 ? aspect : null;
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
        if (_disposed || _handle == 0 || _queuedResize is not null) return;
        _queuedResize = _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _queuedResize = null;
            _ = ResizeWindowToAspect(fitToWorkArea);
        });
    }

    private bool ResizeWindowToAspect(bool fitToWorkArea)
    {
        if (_disposed || _handle == 0 || !AutomaticResizeEnabled || _interactiveAspectRatio is not null || !_canResize() ||
            !GetWindowRect(_handle, out var current) || !TryGetWorkArea(out var workArea)) return false;

        GetFrameSize(out var frameWidth, out var frameHeight);
        var workWidth = Math.Max(1, workArea.Right - workArea.Left - frameWidth);
        var workHeight = Math.Max(1, workArea.Bottom - workArea.Top - frameHeight);
        var dpiScale = GetDpiScale();
        double clientWidth;
        double clientHeight;

        if (fitToWorkArea)
        {
            clientWidth = workWidth * InitialWorkAreaFraction;
            var maxHeight = workHeight * InitialWorkAreaFraction;
            if (PlaceAutomaticBounds is not null)
            {
                clientWidth = Math.Min(workWidth * 0.70, 960 * dpiScale);
                maxHeight = Math.Min(workHeight * 0.78, 840 * dpiScale);
            }
            clientHeight = clientWidth / _aspectRatio;
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
            if (PlaceAutomaticBounds is not null &&
                Math.Abs(currentClientWidth - currentClientHeight * _aspectRatio) <= Math.Max(1, _aspectRatio) &&
                current.Left >= workArea.Left && current.Top >= workArea.Top &&
                current.Right <= workArea.Right && current.Bottom <= workArea.Bottom)
                return true;
            var preferredArea = PlaceAutomaticBounds is not null && _preferredAutomaticArea is { } area
                ? area * dpiScale * dpiScale : (double)currentClientWidth * currentClientHeight;
            clientHeight = Math.Sqrt(preferredArea / _aspectRatio);
            clientWidth = clientHeight * _aspectRatio;

            var scaleDown = Math.Min(1.0, Math.Min(
                workWidth * ChangedWorkAreaFraction / clientWidth,
                workHeight * ChangedWorkAreaFraction / clientHeight));
            clientWidth *= scaleDown;
            clientHeight *= scaleDown;
        }

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
        var keepPosition = KeepPositionOnAutomaticResize && !fitToWorkArea;
        var left = Math.Clamp(keepPosition ? current.Left : centerX - targetWidth / 2,
            workArea.Left, Math.Max(workArea.Left, workArea.Right - targetWidth));
        var top = Math.Clamp(keepPosition ? current.Top : centerY - targetHeight / 2,
            workArea.Top, Math.Max(workArea.Top, workArea.Bottom - targetHeight));

        var target = new PixelRect(left, top, targetWidth, targetHeight);
        if (PlaceAutomaticBounds is { } place)
            target = place(PixelRect.FromLTRB(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom),
                target, fitToWorkArea);
        var applied = target == ToPixelRect(current) ||
            SetWindowPos(_handle, 0, target.Left, target.Top, target.Width, target.Height,
            SwpNoActivate | SwpNoZOrder);
        if (fitToWorkArea && applied && PlaceAutomaticBounds is not null)
            _preferredAutomaticArea = ClientArea(target, frameWidth, frameHeight, dpiScale);
        return applied;
    }

    private double GetDpiScale()
    {
        var dpi = GetDpiForWindow(_handle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private static PixelRect ToPixelRect(WindowRect bounds) =>
        PixelRect.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    private static double ClientArea(PixelRect bounds, int frameWidth, int frameHeight, double dpiScale) =>
        (double)Math.Max(1, bounds.Width - frameWidth) * Math.Max(1, bounds.Height - frameHeight) / (dpiScale * dpiScale);

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message == WmGetMinMaxInfo && lParam != 0 && _window is null)
        {
            // Raw HWNDs otherwise inherit the system caption's minimum tracking
            // width, even when the preview draws no caption at all.
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            limits.MinTrackSize = new NativePoint
            {
                X = Math.Max(1, (int)Math.Ceiling(_minWidthDips * scale)),
                Y = Math.Max(1, (int)Math.Ceiling(_minHeightDips * scale)),
            };
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true;
            return 0;
        }
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
        var aspect = _interactiveAspectRatio ?? _aspectRatio;
        GetFrameSize(out var frameWidth, out var frameHeight);
        var outerWidth = Math.Max(1, rectangle.Right - rectangle.Left);
        var outerHeight = Math.Max(1, rectangle.Bottom - rectangle.Top);
        var clientWidth = Math.Max(1, outerWidth - frameWidth);
        var clientHeight = Math.Max(1, outerHeight - frameHeight);

        var dpi = GetDpiForWindow(_handle);
        var dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;
        var minClientWidth = Math.Max(1.0, _minWidthDips * dpiScale - frameWidth);
        var minClientHeight = Math.Max(1.0, _minHeightDips * dpiScale - frameHeight);

        // WM_SIZING previously enforced the ratio and minimum size only. A
        // horizontal edge could therefore keep increasing the width after the
        // proportional height had already grown beyond the monitor. Derive a
        // ratio-compatible maximum from the current work area so the dragged
        // dimension stops when either screen axis is exhausted.
        var minAspectWidth = Math.Max(minClientWidth, minClientHeight * aspect);
        var minAspectHeight = minAspectWidth / aspect;
        var maxAspectWidth = double.PositiveInfinity;
        if (TryGetWorkArea(out var workArea))
        {
            var maxClientWidth = Math.Max(1.0,
                workArea.Right - workArea.Left - frameWidth);
            var maxClientHeight = Math.Max(1.0,
                workArea.Bottom - workArea.Top - frameHeight);
            maxAspectWidth = Math.Max(minAspectWidth,
                Math.Min(maxClientWidth, maxClientHeight * aspect));
        }
        var maxAspectHeight = maxAspectWidth / aspect;

        var heightFromWidth = clientWidth / aspect;
        var widthFromHeight = clientHeight * aspect;
        var widthDriven = edge is 1 or 2 ||
            (edge is 4 or 5 or 7 or 8 &&
             Math.Abs(heightFromWidth - clientHeight) / clientHeight <=
             Math.Abs(widthFromHeight - clientWidth) / clientWidth);

        double targetClientWidth;
        double targetClientHeight;
        if (widthDriven)
        {
            targetClientWidth = Math.Clamp(clientWidth, minAspectWidth, maxAspectWidth);
            targetClientHeight = targetClientWidth / aspect;
        }
        else
        {
            targetClientHeight = Math.Clamp(clientHeight, minAspectHeight, maxAspectHeight);
            targetClientWidth = targetClientHeight * aspect;
        }

        var targetOuterWidth = Math.Max(1, (int)Math.Round(targetClientWidth) + frameWidth);
        var targetOuterHeight = Math.Max(1, (int)Math.Round(targetClientHeight) + frameHeight);
        var dragLeft = edge is 1 or 4 or 7;
        var dragTop = edge is 3 or 4 or 5;

        if (dragLeft) rectangle.Left = rectangle.Right - targetOuterWidth;
        else rectangle.Right = rectangle.Left + targetOuterWidth;

        if (edge is 1 or 2)
        {
            // A left/right drag has no vertical anchor. Grow around the
            // existing vertical center instead of sending all added height
            // below the window, where it can appear unconstrained.
            var centerY = ((long)rectangle.Top + rectangle.Bottom) / 2;
            rectangle.Top = (int)(centerY - targetOuterHeight / 2L);
            rectangle.Bottom = rectangle.Top + targetOuterHeight;
        }
        else if (dragTop) rectangle.Top = rectangle.Bottom - targetOuterHeight;
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
        _queuedResize?.Abort();
        _queuedResize = null;
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
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
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
