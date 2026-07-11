using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Clean, stable-title preview surface intended for OBS Window Capture.
/// It deliberately contains no controls or transient overlays.
/// </summary>
public sealed partial class PreviewWindow : Window
{
    internal const string StableTitle = "iPhoneMirror OBS Preview";

    private const int WmNcHitTest = 0x0084;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private ResizeMode _restoreResizeMode;
    private WindowState _restoreState;
    private bool _isFullScreen;
    private bool _shapeUpdatePending;
    private bool _applyingRegion;
    private int _lastRegionWidth = -1;
    private int _lastRegionHeight = -1;
    private int _lastRegionRadius = -1;
    private nint _windowHandle;
    private HwndSource? _windowSource;

    internal PreviewWindow()
    {
        InitializeComponent();
        Title = StableTitle;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => QueueWindowShapeUpdate();
        StateChanged += (_, _) => QueueWindowShapeUpdate();
        DpiChanged += (_, _) => QueueWindowShapeUpdate();
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        // In full screen the whole surface is client area again, so retain a
        // WPF double-click path for returning to the rounded window.
        MouseDoubleClick += (_, _) =>
        {
            if (_isFullScreen) ToggleFullScreen();
        };
    }

    internal bool IsFullScreen => _isFullScreen;

    internal bool RefreshPreview() => PreviewHost.ForceRefresh();

    internal void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            _isFullScreen = false;
            ResizeMode = _restoreResizeMode;
            WindowState = _restoreState == WindowState.Minimized
                ? WindowState.Normal
                : _restoreState;
            QueueWindowShapeUpdate();
            return;
        }

        _restoreResizeMode = ResizeMode;
        _restoreState = WindowState;
        _isFullScreen = true;
        ClearWindowRegion();
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowProcedure);

        // The first non-client calculation happens before SourceInitialized,
        // so the hook above cannot affect the cached client rectangle until a
        // frame recalculation is requested explicitly.  Without this call the
        // retained resize frame leaves a light strip above the D3D child.
        _ = SetWindowPos(_windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        // The first WM_NCCALCSIZE is sent while HwndSource is creating the
        // HWND, before our hook exists. Force one frame recalculation now so
        // WindowProcedure can remove the retained resize non-client band.
        _ = SetWindowPos(_windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        // A custom window region owns the exact radius. Ask DWM not to apply a
        // second Windows 11 corner treatment or a contrasting system border.
        var cornerPreference = DwmDoNotRound;
        _ = DwmSetWindowAttribute(_windowHandle, DwmWindowCornerPreference,
            ref cornerPreference, sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttributeColor(_windowHandle, DwmBorderColor,
            ref borderColor, sizeof(uint));
        QueueWindowShapeUpdate();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
        _windowHandle = 0;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message == WmNcCalcSize)
        {
            // WindowStyle=None still retains the resize frame while
            // ResizeMode=CanResize is active.  Without overriding
            // WM_NCCALCSIZE that non-client frame is painted as a light strip
            // above the HwndHost.  Keep WS_THICKFRAME for native resize/snap,
            // but make the complete shaped HWND client area.
            handled = true;
            return 0;
        }
        if (message == WmNcHitTest && !_isFullScreen && WindowState != WindowState.Maximized)
        {
            var result = HitTestWindow(lParam);
            if (result != 0)
            {
                handled = true;
                return result;
            }
        }
        else if (message == WmNcLeftButtonDoubleClick && wParam.ToInt32() == HtCaption)
        {
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
        }
        return 0;
    }

    private nint HitTestWindow(nint packedScreenPoint)
    {
        if (_windowHandle == 0 || !GetWindowRect(_windowHandle, out var bounds)) return 0;

        var packed = packedScreenPoint.ToInt64();
        var x = (short)(packed & 0xFFFF);
        var y = (short)((packed >> 16) & 0xFFFF);
        var dpi = GetDpiForWindow(_windowHandle);
        var border = Math.Max(6, (int)Math.Round(8.0 * (dpi == 0 ? 1.0 : dpi / 96.0)));

        var left = x < bounds.Left + border;
        var right = x >= bounds.Right - border;
        var top = y < bounds.Top + border;
        var bottom = y >= bounds.Bottom - border;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;

        // The preview has no clickable controls. Treating its client area as a
        // caption gives native drag/snap behavior without a visible title bar.
        return HtCaption;
    }

    private void QueueWindowShapeUpdate()
    {
        if (_windowHandle == 0 || _shapeUpdatePending) return;
        _shapeUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _shapeUpdatePending = false;
            ApplyWindowShape();
        });
    }

    private void ApplyWindowShape()
    {
        if (_windowHandle == 0 || _applyingRegion) return;
        if (_isFullScreen || WindowState == WindowState.Maximized)
        {
            ClearWindowRegion();
            return;
        }
        if (!GetWindowRect(_windowHandle, out var bounds)) return;

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var dpi = GetDpiForWindow(_windowHandle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        // A circular fit of Apple's 1:1 iPhone 17 Pro Product Bezel screen
        // mask measures 15.83% of the short edge.  The preferred composition
        // renderer uses the more accurate continuous curve; this is its GDI
        // fallback for systems where DirectComposition cannot be created.
        var radius = Math.Max((int)Math.Round(Math.Min(width, height) * 0.1583),
            (int)Math.Round(28 * scale));
        if (width == _lastRegionWidth && height == _lastRegionHeight &&
            radius == _lastRegionRadius) return;

        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1,
            radius * 2, radius * 2);
        if (region == 0) return;
        _applyingRegion = true;
        try
        {
            if (SetWindowRgn(_windowHandle, region, true) == 0)
            {
                _ = DeleteObject(region);
                return;
            }
            // SetWindowRgn owns the HRGN after success.
            _lastRegionWidth = width;
            _lastRegionHeight = height;
            _lastRegionRadius = radius;
        }
        finally
        {
            _applyingRegion = false;
        }
    }

    private void ClearWindowRegion()
    {
        if (_windowHandle == 0) return;
        _applyingRegion = true;
        try
        {
            _ = SetWindowRgn(_windowHandle, 0, true);
            _lastRegionWidth = -1;
            _lastRegionHeight = -1;
            _lastRegionRadius = -1;
        }
        finally
        {
            _applyingRegion = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(nint window, int attribute,
        ref uint value, int valueSize);
}
