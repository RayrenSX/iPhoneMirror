using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Native top-level DirectComposition preview. WS_EX_NOREDIRECTIONBITMAP keeps
/// DWM from allocating a second WPF redirection surface, allowing the native
/// renderer to attach its visual tree directly to this HWND.
/// </summary>
internal sealed class NativePreviewWindow : IDisposable
{
    internal const string StableTitle = PreviewWindow.StableTitle;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int WmClose = 0x0010;
    private const int WmEraseBackground = 0x0014;
    private const int WmSetIcon = 0x0080;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmDpiChanged = 0x02E0;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkF11 = 0x7A;
    private const int VkMenu = 0x12;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int GwlStyle = -16;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoRedirectionBitmap = 0x00200000;

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;

    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly HwndSource _source;
    private readonly AspectRatioWindowController _aspectController;
    private nint _handle;
    private bool _attached;
    private bool _isFullScreen;
    private bool _disposed;
    private bool _closeQueued;
    private nint _largeIcon;
    private nint _smallIcon;
    private WindowRect _restoreRectangle;
    private nint _restoreStyle;

    private NativePreviewWindow(uint sourceWidth, uint sourceHeight)
    {
        var parameters = new HwndSourceParameters(StableTitle)
        {
            Width = 720,
            Height = 900,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = WsPopup | WsThickFrame | WsSysMenu | WsMinimizeBox |
                WsClipChildren | WsClipSiblings,
            ExtendedWindowStyle = WsExAppWindow | WsExNoRedirectionBitmap,
            TreatAsInputRoot = true,
        };
        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        if (_handle == 0) throw new InvalidOperationException(
            "Could not create the native preview window.");

        _ = SetWindowTextW(_handle, StableTitle);
        ApplyApplicationIcons();
        var cornerPreference = DwmDoNotRound;
        _ = DwmSetWindowAttribute(_handle, DwmWindowCornerPreference,
            ref cornerPreference, sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttributeColor(_handle, DwmBorderColor,
            ref borderColor, sizeof(uint));

        _aspectController = new AspectRatioWindowController(_source,
            sourceWidth, sourceHeight,
            () => !_disposed && !_isFullScreen && _handle != 0 &&
                !IsIconic(_handle) && !IsZoomed(_handle));
        // Install the instance hook only after every callback dependency is
        // initialized; HwndSource construction itself dispatches messages.
        _source.AddHook(WindowProcedure);
        // Recalculate the frame after the hook is installed. WM_NCCALCSIZE
        // then turns the whole top-level HWND into client area while retaining
        // WS_THICKFRAME for native resize and snap behavior.
        _ = SetWindowPos(_handle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    internal event EventHandler? Closed;

    internal nint Handle => _handle;
    internal bool IsFullScreen => _isFullScreen;

    internal static bool TryCreateAndShow(uint sourceWidth, uint sourceHeight,
        out NativePreviewWindow? window)
    {
        window = null;
        NativePreviewWindow? candidate = null;
        try
        {
            candidate = new NativePreviewWindow(sourceWidth, sourceHeight);
            if (!PreviewAttachmentCoordinator.Activate(candidate._handle))
            {
                candidate.Dispose();
                return false;
            }

            candidate._attached = true;
            _ = ShowWindow(candidate._handle, SwShow);
            _ = SetForegroundWindow(candidate._handle);
            window = candidate;
            return true;
        }
        catch
        {
            candidate?.Dispose();
            return false;
        }
    }

    internal void Activate()
    {
        if (_disposed || _handle == 0) return;
        if (IsIconic(_handle)) _ = ShowWindow(_handle, SwRestore);
        else _ = ShowWindow(_handle, SwShow);
        if (!_attached && PreviewAttachmentCoordinator.Activate(_handle))
            _attached = true;
        else if (_attached)
            _ = PreviewAttachmentCoordinator.Activate(_handle);
        _ = SetForegroundWindow(_handle);
        _ = SetFocus(_handle);
    }

    internal bool RefreshPreview() => !_disposed && _handle != 0 &&
        PreviewAttachmentCoordinator.Refresh(_handle);

    internal void SetSourceDimensions(uint width, uint height) =>
        _aspectController.SetSourceDimensions(width, height);

    internal void ToggleFullScreen()
    {
        if (_disposed || _handle == 0) return;
        if (_isFullScreen)
        {
            _isFullScreen = false;
            _ = SetWindowLongPtrW(_handle, GwlStyle, _restoreStyle);
            _ = SetWindowPos(_handle, 0, _restoreRectangle.Left, _restoreRectangle.Top,
                Math.Max(1, _restoreRectangle.Right - _restoreRectangle.Left),
                Math.Max(1, _restoreRectangle.Bottom - _restoreRectangle.Top),
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            _aspectController.Reflow();
            _ = SetForegroundWindow(_handle);
            return;
        }

        if (!GetWindowRect(_handle, out _restoreRectangle)) return;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo)) return;

        _restoreStyle = GetWindowLongPtrW(_handle, GwlStyle);
        _isFullScreen = true;
        var fullScreenStyle = (nint)(_restoreStyle.ToInt64() & ~WsThickFrame);
        _ = SetWindowLongPtrW(_handle, GwlStyle, fullScreenStyle);
        _ = SetWindowPos(_handle, 0, monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
            SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
        _ = SetForegroundWindow(_handle);
    }

    internal void Close() => Dispose();

    private void ApplyApplicationIcons()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            ExtractIconExW(executable, 0, out _largeIcon, out _smallIcon, 1) == 0)
            return;
        if (_largeIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 1, _largeIcon);
        if (_smallIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 0, _smallIcon);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        switch (message)
        {
            case WmNcCalcSize:
                handled = true;
                return 0;
            case WmNcHitTest:
                handled = true;
                return _isFullScreen ? HtClient : HitTestWindow(lParam);
            case WmNcLeftButtonDoubleClick when wParam.ToInt32() == HtCaption:
                handled = true;
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
                return 0;
            case WmEraseBackground:
                // The native DirectComposition visual paints the complete
                // client. Suppressing class-background erase avoids a white
                // flash while its swap chain is being resized/recreated.
                handled = true;
                return 1;
            case WmClose:
                handled = true;
                QueueClose();
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkF11:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkEscape && _isFullScreen:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmSysKeyDown when wParam.ToInt32() == VkReturn && GetKeyState(VkMenu) < 0:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmDpiChanged:
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    _aspectController.Reflow);
                break;
        }
        return 0;
    }

    private nint HitTestWindow(nint packedScreenPoint)
    {
        if (_handle == 0 || !GetWindowRect(_handle, out var bounds)) return HtClient;
        var packed = packedScreenPoint.ToInt64();
        var x = (short)(packed & 0xFFFF);
        var y = (short)((packed >> 16) & 0xFFFF);
        var dpi = GetDpiForWindow(_handle);
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
        return HtCaption;
    }

    private void QueueClose()
    {
        if (_disposed || _closeQueued) return;
        _closeQueued = true;
        _source.Dispatcher.BeginInvoke(DispatcherPriority.Send, Dispose);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = ShowWindow(_handle, SwHide);
        _aspectController.Dispose();
        if (_attached && _handle != 0)
        {
            PreviewAttachmentCoordinator.Unregister(_handle);
            _attached = false;
        }
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
        _handle = 0;
        if (_largeIcon != 0) _ = DestroyIcon(_largeIcon);
        if (_smallIcon != 0 && _smallIcon != _largeIcon) _ = DestroyIcon(_smallIcon);
        _largeIcon = 0;
        _smallIcon = 0;
        Closed?.Invoke(this, EventArgs.Empty);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex,
        out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(nint window, int attribute,
        ref uint value, int valueSize);
}
