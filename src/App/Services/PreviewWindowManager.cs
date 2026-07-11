using System.Windows;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Services;

/// <summary>Owns the single clean preview/OBS window.</summary>
internal sealed class PreviewWindowManager : IDisposable
{
    private NativePreviewWindow? _nativeWindow;
    private PreviewWindow? _fallbackWindow;
    private AspectRatioWindowController? _aspectController;
    // iPhone 17 Pro native stream fallback. The real capture dimensions
    // replace this immediately after the first decoded format/status update.
    private uint _sourceWidth = 1206;
    private uint _sourceHeight = 2622;

    internal bool IsOpen => _nativeWindow is not null || _fallbackWindow is not null;

    internal void Show(Window? owner = null)
    {
        if (_nativeWindow is not null)
        {
            _nativeWindow.Activate();
            return;
        }
        if (_fallbackWindow is not null)
        {
            if (_fallbackWindow.WindowState == WindowState.Minimized)
                _fallbackWindow.WindowState = WindowState.Normal;
            _fallbackWindow.Activate();
            return;
        }

        // Prefer a raw no-redirection HWND. The native renderer can bind a
        // DirectComposition visual to it without a WPF/HwndHost intermediate
        // surface. Older cores or unsupported systems fall back transparently.
        if (NativePreviewWindow.TryCreateAndShow(
            _sourceWidth, _sourceHeight, out var nativeWindow) && nativeWindow is not null)
        {
            nativeWindow.Closed += OnNativeWindowClosed;
            _nativeWindow = nativeWindow;
            return;
        }

        var window = new PreviewWindow();
        var aspectController = new AspectRatioWindowController(window, _sourceWidth, _sourceHeight);
        // Intentionally do not set Window.Owner. An owned WPF window is hidden
        // when the main window is minimized, which makes OBS Window Capture
        // freeze or turn black. This manager still closes it with the app.
        _ = owner;
        window.Closed += (_, _) =>
        {
            aspectController.Dispose();
            if (ReferenceEquals(_fallbackWindow, window)) _fallbackWindow = null;
            if (ReferenceEquals(_aspectController, aspectController)) _aspectController = null;
        };
        _fallbackWindow = window;
        _aspectController = aspectController;
        window.Show();
        window.Activate();
    }

    internal void UpdateSourceSize(uint width, uint height)
    {
        if (width == 0 || height == 0) return;
        _sourceWidth = width;
        _sourceHeight = height;
        _nativeWindow?.SetSourceDimensions(width, height);
        _aspectController?.SetSourceDimensions(width, height);
    }

    internal void ToggleFullScreen(Window? owner = null)
    {
        Show(owner);
        if (_nativeWindow is not null) _nativeWindow.ToggleFullScreen();
        else _fallbackWindow?.ToggleFullScreen();
    }

    internal bool Refresh()
    {
        if (_nativeWindow is not null) return _nativeWindow.RefreshPreview();
        return _fallbackWindow?.RefreshPreview() ?? false;
    }

    private void OnNativeWindowClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_nativeWindow, sender)) _nativeWindow = null;
    }

    internal void Close()
    {
        var nativeWindow = _nativeWindow;
        _nativeWindow = null;
        var fallbackWindow = _fallbackWindow;
        _fallbackWindow = null;
        var aspectController = _aspectController;
        _aspectController = null;
        if (nativeWindow is not null)
        {
            nativeWindow.Closed -= OnNativeWindowClosed;
            nativeWindow.Close();
        }
        fallbackWindow?.Close();
        aspectController?.Dispose();
    }

    public void Dispose() => Close();
}
