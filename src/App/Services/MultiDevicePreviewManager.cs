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
        var cornerRadius = profile.IsRounded ? profile.RadiusRatio : 0;
        _ = Interop.NativeCore.SetDeviceCornerProfile(started.Handle,
            cornerRadius, profile.CurveExponent);
        if (!NativePreviewWindow.TryCreateAndShowForSession(started.Handle, 1206, 2622,
                $"iPhoneMirror — {device.DisplayName}", cornerRadius, profile.CurveExponent,
                () => viewModel.IsDeviceAudioEnabled(device.Udid),
                () => viewModel.Devices.Count,
                enabled => LogAudioResult(viewModel.SetDeviceAudioEnabled(device.Udid, enabled)),
                () => LogAudioResult(viewModel.MuteOtherDeviceSessions(device.Udid)),
                out var window) || window is null)
        {
            await viewModel.StopDeviceSessionAsync(device.Udid);
            return (false, LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        _windows[device.Udid] = window;
        window.Closed += async (_, _) =>
        {
            _windows.Remove(device.Udid);
            // A secondary window owns the background session it created. Keep
            // the session only when the same device is still driving the main
            // preview; otherwise closing the window must release capture,
            // decoder, audio, and USB resources as well as the HWND.
            if (DeviceViewModel.UdidEquals(viewModel.SelectedDevice?.Udid, device.Udid))
                return;
            try
            {
                await viewModel.StopDeviceSessionAsync(device.Udid);
            }
            catch (Exception error)
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
        };
        return (true, string.Empty);
    }

    private void LogAudioResult((bool Success, string Message) result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message)) viewModel.AddUiLog(result.Message);
    }

    internal void UpdateDevice(string udid, uint width, uint height)
    {
        if (_windows.TryGetValue(udid, out var window) && width != 0 && height != 0)
            window.SetSourceDimensions(width, height);
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
        UpdateDevice(device.Udid, width, height);
        // Do not re-apply the model default here. The detached window owns
        // the user's per-window corner override (including "remove corners");
        // applying the profile on every size/status notification would undo
        // that menu choice as soon as the window is focused or resized.
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values.ToArray()) window.Dispose();
        _windows.Clear();
    }
}
