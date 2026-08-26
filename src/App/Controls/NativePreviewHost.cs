using System.Runtime.InteropServices;
using System.Windows.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Controls;

internal sealed class NativePreviewHost : HwndHost
{
    private const int WmNcHitTest = 0x0084;
    private const int WmEraseBackground = 0x0014;
    private const int HtTransparent = -1;
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private nint _window;
    private bool _presentationVisible;
    private bool _isFullScreenPresentation;

    internal bool CapturePointerInput { get; set; }
    internal bool SuppressMouseMove { get; set; }
    internal bool IsFullScreenPresentation
    {
        get => _isFullScreenPresentation;
        set
        {
            if (_isFullScreenPresentation == value) return;
            _isFullScreenPresentation = value;
            UpdateWindowRegion();
        }
    }
    internal nint WindowHandle => _window;

    internal event EventHandler<PreviewPointerEventArgs>? PointerInput;
    internal event EventHandler<PreviewKeyboardEventArgs>? KeyboardInput;

    public NativePreviewHost()
    {
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _window = CreateWindowExW(0, "STATIC", string.Empty,
            WsChild | WsClipSiblings | WsClipChildren | SsBlackRect,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException(
            LocalizationService.Get("PreviewChildCreateFailed"));
        if (!Activate())
        {
            DestroyWindow(_window);
            _window = 0;
            throw new InvalidOperationException(LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        if (_presentationVisible) _ = ShowWindow(_window, SwShowNoActivate);
        return new HandleRef(this, _window);
    }

    /// <summary>
    /// Makes this host the single native preview target.  The native renderer
    /// intentionally owns one swap chain, so main/fullscreen/OBS windows hand
    /// ownership to each other instead of rendering the same frame twice.
    /// </summary>
    internal bool Activate()
    {
        if (_window == 0) return false;
        return PreviewAttachmentCoordinator.Activate(_window);
    }

    /// <summary>
    /// Stops and removes the renderer targeting this HWND while retaining the
    /// WPF host so it can be activated again when the main preview returns.
    /// </summary>
    internal void Deactivate()
    {
        if (_window == 0) return;
        PreviewAttachmentCoordinator.Deactivate(_window);
    }

    internal bool ForceRefresh()
    {
        if (_window == 0) return false;
        // Prefer a cheap re-present of the newest decoded frame. Older core
        // builds do not expose that entry point, so retain reattachment as a
        // compatibility fallback.
        return PreviewAttachmentCoordinator.Refresh(_window);
    }

    internal void SetPresentationVisible(bool visible)
    {
        _presentationVisible = visible;
        if (_window != 0) _ = ShowWindow(_window, visible ? SwShowNoActivate : SwHide);
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_window == 0) return;
        var width = Math.Max(1, (int)Math.Round(rcBoundingBox.Width));
        var height = Math.Max(1, (int)Math.Round(rcBoundingBox.Height));
        ApplyWindowRegion(width, height);
    }

    private void UpdateWindowRegion()
    {
        if (_window == 0 || !GetClientRect(_window, out var rect)) return;
        ApplyWindowRegion(Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));
    }

    private void ApplyWindowRegion(int width, int height)
    {
        if (_window == 0) return;
        if (_isFullScreenPresentation)
        {
            // The fullscreen parent is rectangular. Retaining the main
            // preview's rounded child region exposes a light-theme sliver at
            // the edge while the D3D surface itself is correctly black.
            _ = SetWindowRgn(_window, 0, true);
            return;
        }
        var dpi = GetDpiForWindow(_window);
        var radius = Math.Max(2, (int)Math.Round(10.0 * (dpi == 0 ? 1.0 : dpi / 96.0)));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == 0) return;
        // SetWindowRgn owns the region after success.
        if (SetWindowRgn(_window, region, true) == 0) _ = DeleteObject(region);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        PreviewAttachmentCoordinator.Unregister(hwnd.Handle);
        if (hwnd.Handle != 0) DestroyWindow(hwnd.Handle);
        _window = 0;
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            if (CapturePointerInput)
            {
                handled = true;
                return 1; // HTCLIENT
            }
            // Let the borderless top-level native preview own drag/resize hit
            // testing even though this native child covers the whole client.
            handled = true;
            return HtTransparent;
        }
        if (message == 0x0020 && CapturePointerInput) // WM_SETCURSOR
        {
            handled = true;
            return 1;
        }
        if (CapturePointerInput)
        {
            if (message is 0x0008 or 0x001F or 0x0215) // focus/capture lost
            {
                PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                    PreviewPointerKind.Reset, 0, 0, 0, 0));
                KeyboardInput?.Invoke(this, new PreviewKeyboardEventArgs(
                    PreviewKeyboardKind.Reset, 0));
            }
            switch (message)
            {
                case 0x0200: // WM_MOUSEMOVE
                    if (SuppressMouseMove)
                    {
                        handled = true;
                        return 0;
                    }
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.Move, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), 0, 0, GetClientWidth(),
                        GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0204: // WM_RBUTTONDOWN
                case 0x0207: // WM_MBUTTONDOWN
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.ButtonDown, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), MouseButtonFromMessage(message), 0,
                        GetClientWidth(), GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x0202: // WM_LBUTTONUP
                case 0x0205: // WM_RBUTTONUP
                case 0x0208: // WM_MBUTTONUP
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.ButtonUp, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), MouseButtonFromMessage(message), 0,
                        GetClientWidth(), GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x020A: // WM_MOUSEWHEEL
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.Wheel, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), 0, (short)((long)wParam >> 16),
                        GetClientWidth(), GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x0100: // WM_KEYDOWN
                case 0x0104: // WM_SYSKEYDOWN
                    KeyboardInput?.Invoke(this, new PreviewKeyboardEventArgs(
                        PreviewKeyboardKind.Down, (int)wParam,
                        (int)(((long)lParam >> 16) & 0x1FF)));
                    handled = true;
                    return 0;
                case 0x0101: // WM_KEYUP
                case 0x0105: // WM_SYSKEYUP
                    KeyboardInput?.Invoke(this, new PreviewKeyboardEventArgs(
                        PreviewKeyboardKind.Up, (int)wParam,
                        (int)(((long)lParam >> 16) & 0x1FF)));
                    handled = true;
                    return 0;
            }
        }
        if (message == WmEraseBackground)
        {
            // The selected D3D session can be detached one dispatcher frame
            // before WPF shrinks this airspace HWND to its idle 1 px target.
            // Suppress the STATIC control's default white erase during that
            // handoff; SS_BLACKRECT supplies the same black as the preview.
            handled = true;
            return 1;
        }
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    private static short GetSignedLowWord(nint value) => unchecked((short)((long)value & 0xFFFF));
    private static short GetSignedHighWord(nint value) => unchecked((short)(((long)value >> 16) & 0xFFFF));
    private static byte MouseButtonFromMessage(int message) => message switch
    {
        0x0201 or 0x0202 => 1,
        0x0204 or 0x0205 => 2,
        _ => 4,
    };

    private int GetClientWidth()
    {
        if (_window == 0 || !GetClientRect(_window, out var rect)) return 1;
        return Math.Max(1, rect.Right - rect.Left);
    }

    private int GetClientHeight()
    {
        if (_window == 0 || !GetClientRect(_window, out var rect)) return 1;
        return Math.Max(1, rect.Bottom - rect.Top);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}

internal enum PreviewPointerKind { Move, ButtonDown, ButtonUp, Wheel, Reset }
internal sealed class PreviewPointerEventArgs : EventArgs
{
    internal PreviewPointerEventArgs(PreviewPointerKind kind, short x, short y,
        byte button, int wheel, int surfaceWidth = 1, int surfaceHeight = 1,
        uint sourceWidth = 0, uint sourceHeight = 0, int rotation = 0) =>
        (Kind, X, Y, Button, Wheel, SurfaceWidth, SurfaceHeight, SourceWidth,
            SourceHeight, Rotation) = (kind, x, y, button, wheel, surfaceWidth,
            surfaceHeight, sourceWidth, sourceHeight, rotation);
    internal PreviewPointerKind Kind { get; }
    internal int X { get; }
    internal int Y { get; }
    internal byte Button { get; }
    internal int Wheel { get; }
    internal int SurfaceWidth { get; }
    internal int SurfaceHeight { get; }
    internal uint SourceWidth { get; }
    internal uint SourceHeight { get; }
    internal int Rotation { get; }
}
internal enum PreviewKeyboardKind { Down, Up, Reset }
internal sealed class PreviewKeyboardEventArgs : EventArgs
{
    internal PreviewKeyboardEventArgs(PreviewKeyboardKind kind, int virtualKey,
        int scanCode = 0) => (Kind, VirtualKey, ScanCode) = (kind, virtualKey, scanCode);
    internal PreviewKeyboardKind Kind { get; }
    internal int VirtualKey { get; }
    internal int ScanCode { get; }
}
