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
            // Let the borderless top-level native preview own drag/resize hit
            // testing even though this native child covers the whole client.
            handled = true;
            return HtTransparent;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

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
