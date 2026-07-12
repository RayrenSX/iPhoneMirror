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
    private readonly Dictionary<string, DeviceViewModel> _devices =
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
        var cornerRadius = profile.IsRounded ? profile.RadiusRatio : 0;
        _ = Interop.NativeCore.SetDeviceCornerProfile(started.Handle,
            cornerRadius, profile.CurveExponent);
        if (!NativePreviewWindow.TryCreateAndShowForSession(started.Handle, 1206, 2622,
                $"iPhoneMirror — {device.DisplayName}", cornerRadius, profile.CurveExponent,
                out var window) || window is null)
        {
            await viewModel.StopDeviceSessionAsync(device.Udid);
            return (false, LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        _windows[device.Udid] = window;
        _devices[device.Udid] = device;
        window.Closed += (_, _) =>
        {
            _windows.Remove(device.Udid);
            _devices.Remove(device.Udid);
        };
        return (true, string.Empty);
    }

    internal void UpdateDevice(string udid, uint width, uint height)
    {
        if (_devices.TryGetValue(udid, out var device)) UpdateDevice(device, width, height);
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
        _devices.Clear();
    }
}
