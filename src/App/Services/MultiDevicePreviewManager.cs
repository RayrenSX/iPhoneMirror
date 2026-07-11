using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Services;

/// <summary>Owns one independent native preview window per device UDID.</summary>
internal sealed class MultiDevicePreviewManager(MainViewModel viewModel) : IDisposable
{
    private readonly Dictionary<string, NativePreviewWindow> _windows =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool IsOpen(DeviceViewModel? device) => device is not null &&
        _windows.ContainsKey(device.Udid);

    internal async Task<(bool Success, string Message)> ShowAsync(DeviceViewModel device)
    {
        if (_windows.TryGetValue(device.Udid, out var existing))
        {
            existing.Activate();
            return (true, string.Empty);
        }
        var started = await viewModel.StartBackgroundSessionAsync(device);
        if (!started.Success) return (false, started.Message);
        var profile = DeviceCornerProfileResolver.Resolve(device.ProductType, 1206, 2622);
        _ = Interop.NativeCore.SetDeviceCornerProfile(started.Handle,
            profile.IsRounded ? profile.RadiusRatio : 0, profile.CurveExponent);
        if (!NativePreviewWindow.TryCreateAndShowForSession(started.Handle, 1206, 2622,
                $"iPhoneMirror — {device.DisplayName}", out var window) || window is null)
        {
            await viewModel.StopDeviceSessionAsync(device.Udid);
            return (false, LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        _windows[device.Udid] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(device.Udid);
            // Closing a view only detaches this HWND. The device session stays
            // alive for instant tab switching and can be stopped explicitly
            // from the selected device's red Stop button.
        };
        return (true, string.Empty);
    }

    internal bool Refresh(DeviceViewModel? device) => device is not null &&
        _windows.TryGetValue(device.Udid, out var window) && window.RefreshPreview();

    internal async Task<bool> ToggleFullScreenAsync(DeviceViewModel device)
    {
        var result = await ShowAsync(device);
        if (!result.Success || !_windows.TryGetValue(device.Udid, out var window)) return false;
        window.ToggleFullScreen();
        return true;
    }

    internal void UpdateDevice(DeviceViewModel? device, uint width, uint height)
    {
        if (device is null) return;
        if (_windows.TryGetValue(device.Udid, out var window) && width != 0 && height != 0)
            window.SetSourceDimensions(width, height);
        var handle = viewModel.GetDeviceSessionHandle(device.Udid);
        if (handle == 0) return;
        var profile = DeviceCornerProfileResolver.Resolve(device.ProductType, width, height);
        _ = Interop.NativeCore.SetDeviceCornerProfile(handle,
            profile.IsRounded ? profile.RadiusRatio : 0, profile.CurveExponent);
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values.ToArray()) window.Dispose();
        _windows.Clear();
    }
}
