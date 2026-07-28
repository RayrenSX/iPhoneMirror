using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Services;

/// <summary>Owns one independent native preview window per device UDID.</summary>
internal sealed class MultiDevicePreviewManager : IDisposable
{
    private readonly MainViewModel viewModel;
    private readonly Dictionary<string, NativePreviewWindow> _windows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<(bool Success, string Message)>> _opening =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposing;

    internal MultiDevicePreviewManager(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        viewModel.DeviceSessionHandleChanged += OnDeviceSessionHandleChanged;
    }

    internal bool IsOpen(DeviceViewModel? device) => device is not null &&
        _windows.ContainsKey(device.Udid);

    internal Task<(bool Success, string Message)> ShowAsync(DeviceViewModel device)
    {
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_show_requested",
            ("device", AppLog.Device(device.Udid)),
            ("kind", device.IsWireless ? "wireless" : "wired"),
            ("existing", _windows.ContainsKey(device.Udid)),
            ("disposing", _disposing)));
        if (_disposing)
            return Task.FromResult((false, LocalizationService.Get("CaptureStopped")));
        if (_windows.TryGetValue(device.Udid, out var existing))
        {
            var currentHandle = viewModel.GetDeviceSessionHandle(device.Udid);
            if (currentHandle != 0 && existing.SessionHandle == currentHandle)
            {
                existing.Activate();
                viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_reused",
                    ("device", AppLog.Device(device.Udid)),
                    ("handle", AppLog.Handle(currentHandle))));
                return Task.FromResult((true, string.Empty));
            }
            _windows.Remove(device.Udid);
            existing.Dispose();
        }
        if (_opening.TryGetValue(device.Udid, out var pending)) return pending;

        var opening = ShowCoreAsync(device);
        _opening[device.Udid] = opening;
        return CompleteOpeningAsync(device.Udid, opening);
    }

    private async Task<(bool Success, string Message)> CompleteOpeningAsync(string udid,
        Task<(bool Success, string Message)> opening)
    {
        try
        {
            return await opening;
        }
        finally
        {
            if (_opening.TryGetValue(udid, out var current) && ReferenceEquals(current, opening))
                _opening.Remove(udid);
        }
    }

    private async Task<(bool Success, string Message)> ShowCoreAsync(DeviceViewModel device)
    {
        var started = await viewModel.StartBackgroundSessionAsync(device);
        if (!started.Success)
        {
            viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_session_failed",
                ("device", AppLog.Device(device.Udid)),
                ("message", started.Message)));
            return (false, started.Message);
        }
        if (_disposing)
        {
            try
            {
                if (started.Created)
                    await viewModel.StopDeviceSessionAsync(
                        device.Udid, started.Handle, preserveIfSelected: true);
            }
            catch (Exception error)
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
            return (false, LocalizationService.Get("CaptureStopped"));
        }
        var profile = DeviceCornerProfileResolver.Resolve(device.ProductType, 1206, 2622);
        var cornerRadius = profile.IsRounded ? profile.RadiusRatio : 0;
        _ = Interop.NativeCore.SetDeviceCornerProfile(started.Handle,
            cornerRadius, profile.CurveExponent);
        if (!NativePreviewWindow.TryCreateAndShowForSession(started.Handle, 1206, 2622,
                $"iPhoneMirror — {device.DisplayName}", cornerRadius, profile.CurveExponent,
                () => viewModel.IsDeviceAudioEnabled(device.Udid),
                 () => viewModel.ActiveDeviceSessionCount,
                 enabled => LogAudioResult(viewModel.SetDeviceAudioEnabled(device.Udid, enabled)),
                 () => LogAudioResult(viewModel.MuteOtherDeviceSessions(device.Udid)),
                 () => viewModel.ShowImageSettings(device.Udid),
                 out var window, viewModel.AddDiagnosticLog) || window is null)
        {
            if (started.Created)
                await viewModel.StopDeviceSessionAsync(
                    device.Udid, started.Handle, preserveIfSelected: true);
            return (false, LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        _windows[device.Udid] = window;
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_opened",
            ("device", AppLog.Device(device.Udid)),
            ("handle", AppLog.Handle(started.Handle)),
            ("created_session", started.Created)));
        window.Closed += async (_, _) =>
        {
            if (!_windows.TryGetValue(device.Udid, out var tracked) ||
                !ReferenceEquals(tracked, window)) return;
            _windows.Remove(device.Udid);
            viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_closed",
                ("device", AppLog.Device(device.Udid)),
                ("handle", AppLog.Handle(started.Handle)),
                ("created_session", started.Created),
                ("disposing", _disposing)));
            if (_disposing || !started.Created) return;
            try
            {
                // The selected-main check is performed under the same core
                // gate that revokes the handle, closing the selection race.
                await viewModel.StopDeviceSessionAsync(
                    device.Udid, started.Handle, preserveIfSelected: true);
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

    private void OnDeviceSessionHandleChanged(string udid, ulong handle)
    {
        if (!_windows.TryGetValue(udid, out var window) ||
            window.SessionHandle == handle)
            return;
        _windows.Remove(udid);
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_handle_invalidated",
            ("device", AppLog.Device(udid)),
            ("old_handle", AppLog.Handle(window.SessionHandle)),
            ("new_handle", AppLog.Handle(handle))));
        try { window.Dispose(); }
        catch (Exception error)
        {
            try
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
            catch { }
        }
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
        if (_disposing) return;
        _disposing = true;
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_manager_dispose",
            ("count", _windows.Count)));
        viewModel.DeviceSessionHandleChanged -= OnDeviceSessionHandleChanged;
        foreach (var window in _windows.Values.ToArray())
        {
            try { window.Dispose(); }
            catch (Exception error)
            {
                viewModel.AddDiagnosticLog(AppLog.Event(
                    "independent_preview_dispose_failed",
                    ("handle", AppLog.Handle(window.SessionHandle)),
                    ("error", AppLog.Error(error))));
            }
        }
        _windows.Clear();
    }
}
