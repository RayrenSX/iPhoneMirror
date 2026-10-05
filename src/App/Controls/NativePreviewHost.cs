using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Controls;

internal sealed class NativePreviewHost : HwndHost
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseMove = 0x0200;
    private const uint PmRemove = 0x0001;
    private const int WmEraseBackground = 0x0014;
    private const int HtTransparent = -1;
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    // PreviewPanel has a 16 DIP outer radius and a 1 DIP border. The native
    // child occupies the inner rectangle, so its clipping radius is 15 DIP.
    private const double MainPreviewInnerCornerRadius = 15.0;
    private nint _window;
    private bool _presentationVisible;
    private byte _capturedMouseButtons;
    private bool _isFullScreenPresentation;
    private DeviceCornerProfile _cornerProfile = DeviceCornerProfile.Rectangular;
    private bool _usesDeviceCornerProfile;
    private (int Width, int Height, int Radius, double Curve)? _appliedRegion;

    internal bool CapturePointerInput { get; set; }
    internal bool CaptureTouchInput { get; set; }
    internal bool SuppressMouseMove { get; set; }
    // Raw Input owns button and wheel transitions while Bluetooth control is
    // active. Suppress legacy messages that leak from the child HWND so a
    // duplicate up/down cannot overwrite the HID button state.
    internal bool SuppressLegacyMouseButtons { get; set; }

    internal void ReleasePointerCapture()
    {
        _capturedMouseButtons = 0;
        // WM_CAPTURECHANGED may transfer capture to the title-bar move loop.
        // Releasing the new owner's capture here would interrupt window dragging.
        if (_window != 0 && GetCapture() == _window) _ = ReleaseCapture();
    }
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

    internal void SetDeviceCornerProfile(DeviceCornerProfile profile, bool enabled)
    {
        if (_usesDeviceCornerProfile == enabled && _cornerProfile == profile) return;
        _usesDeviceCornerProfile = enabled;
        _cornerProfile = profile;
        UpdateWindowRegion();
    }

    internal event EventHandler<PreviewPointerEventArgs>? PointerInput;
    internal event EventHandler<PreviewKeyboardEventArgs>? KeyboardInput;

    public NativePreviewHost()
    {
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Keep WPF's HwndHost automation provider, which owns the interop
        // child. Forward logical focus (including UIA SetFocus) to the HWND
        // that actually receives keyboard input and focus-loss releases.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                // Finish WPF/UIA's focus transaction before handing focus to
                // the HWND. Synchronous handoff makes UIA report a failure.
                if (_window != 0 && IsKeyboardFocused) SetFocus(_window);
            }));
    }

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _window = CreateWindowExW(0, "STATIC", string.Empty,
            WsChild | WsClipSiblings | WsClipChildren | SsBlackRect,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException(
            LocalizationService.Get("PreviewChildCreateFailed"));
        NativeTouchInput.RegisterTouchWindow(_window);
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
        if (_presentationVisible == visible) return;
        _presentationVisible = visible;
        if (_window != 0) _ = ShowWindow(_window, visible ? SwShowNoActivate : SwHide);
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        UpdateWindowRegion();
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
        var shortEdge = Math.Min(width, height);
        var dpi = GetDpiForWindow(_window);
        var radius = _isFullScreenPresentation ? 0 : _usesDeviceCornerProfile
            ? _cornerProfile.GetGdiRadius(shortEdge, dpi == 0 ? 1.0 : dpi / 96.0)
            : Math.Max(2, (int)Math.Round(MainPreviewInnerCornerRadius *
                (dpi == 0 ? 1.0 : dpi / 96.0)));
        var shape = (width, height, radius,
            _usesDeviceCornerProfile ? _cornerProfile.CurveExponent : 0.0);
        // Position-only updates do not change the child silhouette. Avoid
        // rebuilding a 260-point HRGN and forcing a repaint on every drag tick.
        if (_appliedRegion == shape) return;
        if (radius == 0)
        {
            if (SetWindowRgn(_window, 0, true) != 0) _appliedRegion = shape;
            return;
        }
        // The D3D shader uses the device profile's superellipse and smooths
        // its edge. Keep the HWND's opaque region on that same curve so the
        // child does not expose a generic circular iPhone corner underneath
        // the shader when compact mode removes the WPF preview frame.
        var region = _usesDeviceCornerProfile
            ? CreateDeviceCornerRegion(width, height, radius,
                _cornerProfile.CurveExponent)
            : CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);
        if (region == 0) return;
        // SetWindowRgn owns the region after success.
        if (SetWindowRgn(_window, region, true) == 0) _ = DeleteObject(region);
        else _appliedRegion = shape;
    }

    private static nint CreateDeviceCornerRegion(int width, int height, int radius,
        double exponent)
    {
        const int segmentsPerCorner = 64;
        if (radius <= 0 || width <= radius * 2 || height <= radius * 2)
            return CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);

        var curve = Math.Clamp(exponent, 1.5, 4.0);
        var points = new NativePoint[segmentsPerCorner * 4 + 4];
        var index = 0;
        AddCorner(width - radius, radius, -Math.PI / 2, 0);
        AddCorner(width - radius, height - radius, 0, Math.PI / 2);
        AddCorner(radius, height - radius, Math.PI / 2, Math.PI);
        AddCorner(radius, radius, Math.PI, Math.PI * 1.5);
        return CreatePolygonRgn(points, index, Windings);

        void AddCorner(double centerX, double centerY, double from, double to)
        {
            for (var segment = 0; segment <= segmentsPerCorner; segment++)
            {
                var angle = from + (to - from) * segment / segmentsPerCorner;
                var x = Math.Pow(Math.Abs(Math.Cos(angle)), 2.0 / curve);
                var y = Math.Pow(Math.Abs(Math.Sin(angle)), 2.0 / curve);
                points[index++] = new NativePoint
                {
                    X = (int)Math.Round(centerX + radius * Math.Sign(Math.Cos(angle)) * x),
                    Y = (int)Math.Round(centerY + radius * Math.Sign(Math.Sin(angle)) * y),
                };
            }
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        PreviewAttachmentCoordinator.Unregister(hwnd.Handle);
        if (hwnd.Handle != 0) DestroyWindow(hwnd.Handle);
        _window = 0;
        _appliedRegion = null;
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message == NativeTouchInput.WmTouch)
        {
            NativeTouchInput.Dispatch(hwnd, wParam, lParam, CapturePointerInput && CaptureTouchInput,
                GetClientWidth(), GetClientHeight(), 0, 0, 0, input =>
                {
                    if (input.Kind == PreviewPointerKind.TouchDown) { Focus(); SetFocus(hwnd); }
                    PointerInput?.Invoke(this, input);
                });
            handled = true;
            return 0;
        }
        if (CapturePointerInput && CaptureTouchInput && NativeTouchInput.IsPromotedMouseMessage(message))
        {
            handled = true;
            return 0;
        }
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
            // Consuming this message also owns the cursor shape. Otherwise a
            // resize/hand cursor from the previous surface remains in use.
            // Setting the shape leaves Bluetooth's visibility state intact.
            NativeCursor.SetArrow();
            handled = true;
            return 1;
        }
        if (CapturePointerInput)
        {
            // Button-up clears our mask before ReleaseCapture synchronously
            // sends WM_CAPTURECHANGED. That is a completed click, not a reset.
            if (message == 0x0215 && _capturedMouseButtons == 0)
            {
                handled = true;
                return 0;
            }
            if (message is 0x0008 or 0x001F or 0x0215) // focus/capture lost
            {
                ReleasePointerCapture();
                PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                    PreviewPointerKind.Reset, 0, 0, 0, 0));
                KeyboardInput?.Invoke(this, new PreviewKeyboardEventArgs(
                    PreviewKeyboardKind.Reset, 0));
            }
            switch (message)
            {
                case WmMouseMove:
                    if (SuppressMouseMove)
                    {
                        handled = true;
                        return 0;
                    }
                    lParam = DrainQueuedMouseMoves(_window, lParam);
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.Move, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), 0, 0, GetClientWidth(),
                        GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0204: // WM_RBUTTONDOWN
                case 0x0207: // WM_MBUTTONDOWN
                    // STATIC children do not take keyboard focus on click.
                    // Restore both WPF and native focus after a toolbar/editor
                    // interaction before dispatching this preview input.
                    // Re-focusing an already focused HWND through WPF briefly
                    // blurs it and retires held shortcut modifiers (e.g. Ctrl).
                    if (GetFocus() != _window)
                    {
                        Focus();
                        SetFocus(_window);
                    }
                    if (SuppressLegacyMouseButtons)
                    {
                        handled = true;
                        return 0;
                    }
                    _capturedMouseButtons |= MouseButtonFromMessage(message);
                    _ = SetCapture(_window);
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.ButtonDown, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), MouseButtonFromMessage(message), 0,
                        GetClientWidth(), GetClientHeight()));
                    handled = true;
                    return 0;
                case 0x0202: // WM_LBUTTONUP
                case 0x0205: // WM_RBUTTONUP
                case 0x0208: // WM_MBUTTONUP
                    if (SuppressLegacyMouseButtons)
                    {
                        handled = true;
                        return 0;
                    }
                    _capturedMouseButtons = (byte)(_capturedMouseButtons & ~MouseButtonFromMessage(message));
                    PointerInput?.Invoke(this, new PreviewPointerEventArgs(
                        PreviewPointerKind.ButtonUp, GetSignedLowWord(lParam),
                        GetSignedHighWord(lParam), MouseButtonFromMessage(message), 0,
                        GetClientWidth(), GetClientHeight()));
                    if (_capturedMouseButtons == 0) _ = ReleaseCapture();
                    handled = true;
                    return 0;
                case 0x020A: // WM_MOUSEWHEEL
                    if (SuppressLegacyMouseButtons)
                    {
                        handled = true;
                        return 0;
                    }
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

    private static nint DrainQueuedMouseMoves(nint hwnd, nint currentLParam)
    {
        var latest = currentLParam;
        var queued = new NativeMessage();
        var drained = 0;
        while (drained++ < 64 && PeekMessageW(ref queued, hwnd, WmMouseMove, WmMouseMove, PmRemove))
            latest = queued.LParam;
        return latest;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Hwnd;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(ref NativeMessage message, nint window,
        uint minMessage, uint maxMessage, uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    private const int Windings = 2;

    [DllImport("gdi32.dll")]
    private static extern nint CreatePolygonRgn([In] NativePoint[] points, int count,
        int mode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}

internal enum PreviewPointerKind { Move, ButtonDown, ButtonUp, Wheel, Reset, TouchDown, TouchMove, TouchUp }
internal sealed class PreviewPointerEventArgs : EventArgs
{
    internal PreviewPointerEventArgs(PreviewPointerKind kind, short x, short y,
        byte button, int wheel, int surfaceWidth = 1, int surfaceHeight = 1,
        uint sourceWidth = 0, uint sourceHeight = 0, int rotation = 0)
        : this(kind, (int)x, (int)y, button, wheel, surfaceWidth, surfaceHeight,
            sourceWidth, sourceHeight, rotation, 0, 0) { }

    internal PreviewPointerEventArgs(PreviewPointerKind kind, int x, int y,
        byte button, int wheel, int surfaceWidth, int surfaceHeight,
        uint sourceWidth, uint sourceHeight, int rotation,
        uint touchId, nint sourceWindow) =>
        (Kind, X, Y, Button, Wheel, SurfaceWidth, SurfaceHeight, SourceWidth,
            SourceHeight, Rotation, TouchId, SourceWindow) = (kind, x, y, button, wheel, surfaceWidth,
            surfaceHeight, sourceWidth, sourceHeight, rotation, touchId, sourceWindow);
    internal uint TouchId { get; }
    internal nint SourceWindow { get; }
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
