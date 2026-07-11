using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
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
    private string _productType = string.Empty;
    private DeviceCornerProfile _cornerProfile =
        DeviceCornerProfileResolver.Resolve(null, 1206, 2622);

    internal bool IsOpen => _nativeWindow is not null || _fallbackWindow is not null;

    internal void Show(Window? owner = null)
    {
        ApplyCornerProfile();
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
        window.SetCornerProfile(_cornerProfile);
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
        UpdateCornerProfile();
        _nativeWindow?.SetSourceDimensions(width, height);
        _aspectController?.SetSourceDimensions(width, height);
    }

    /// <summary>
    /// Updates both capture geometry and Apple's stable hardware identifier.
    /// ProductType produces the most accurate family fit; frame geometry is a
    /// forward-compatible fallback while Lockdown is still loading metadata.
    /// </summary>
    internal void UpdateSourceDevice(string? productType, uint width, uint height)
    {
        _productType = productType?.Trim() ?? string.Empty;
        if (width != 0 && height != 0)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            _nativeWindow?.SetSourceDimensions(width, height);
            _aspectController?.SetSourceDimensions(width, height);
        }
        UpdateCornerProfile();
    }

    private void UpdateCornerProfile()
    {
        var profile = DeviceCornerProfileResolver.Resolve(
            _productType, _sourceWidth, _sourceHeight);
        if (profile == _cornerProfile) return;
        _cornerProfile = profile;
        ApplyCornerProfile();
        _fallbackWindow?.SetCornerProfile(profile);
    }

    private void ApplyCornerProfile() => NativeCore.SetPreviewCornerProfile(
        _cornerProfile.IsRounded ? _cornerProfile.RadiusRatio : 0.0,
        _cornerProfile.CurveExponent);

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
