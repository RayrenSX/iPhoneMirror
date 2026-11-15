using System.Text.Json;
using System.IO;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    internal bool IsDeviceHubReady(DeviceViewModel device) =>
        !device.IsMediaCast && GetReadyUsbControlBridge(device.Udid) is { SupportsDeviceHub: true };

    internal Task StopCoreDeviceCaptureAsync(DeviceViewModel device)
    {
        if (_sessions.Get(device.Udid)?.ExternalCapture is not CoreDeviceCaptureFeed)
            throw new AutomationException("CAPTURE_NOT_COREDEVICE", "This device is not using CoreDevice capture.");
        return StopDeviceSessionAsync(device.Udid);
    }
    internal async Task<JsonElement> DeviceHubUiAsync(DeviceViewModel device, string action,
        object? arguments, CancellationToken cancellation)
    {
        var bridge = GetReadyUsbControlBridge(device.Udid) ??
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "Prepare the device's USB control connection first.", 503);
        if (DeviceHubContract.Require(action).ReadOnly)
            return await bridge.DeviceHubAsync(action, arguments, () => true, cancellation);
        var physical = AutomationPhysicalId(device.Udid);
        var guard = AutomationHumanGuard(device.Udid);
        using var owner = AutomationOwnership.EnterInput(physical);
        return await bridge.DeviceHubAsync(action, arguments, guard, cancellation);
    }

    internal async Task<JsonElement> StartCoreDeviceCaptureAsync(DeviceViewModel device, bool audio,
        bool record, CancellationToken cancellation = default)
    {
        if (device.IsMediaCast)
            throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Select a paired iPhone device for CoreDevice capture.", 422);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "CoreDevice", "iPhoneMirror.MediaDecoder.exe")))
            throw new AutomationException("DECODER_MISSING", "Build and install the dev CoreDevice decoder first.", 503);
        if (_sessions.Get(device.Udid)?.HasSession == true)
            throw new AutomationException("CAPTURE_ALREADY_ACTIVE", "Stop this device's current capture before selecting CoreDevice.");
        if (GetReadyUsbControlBridge(device.Udid) is null)
            await StartAutomationControlAsync(device.Udid, wireless: device.IsWireless, cancellation);
        var bridge = GetReadyUsbControlBridge(device.Udid) ??
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "Finish device trust, binding, Developer Mode and DDI preparation.", 503);
        await _coreGate.WaitAsync(cancellation);
        DeviceCaptureState? state = null;
        try
        {
            if (_disposed || _sessions.Get(device.Udid)?.HasSession == true)
                throw new AutomationException("CAPTURE_ALREADY_ACTIVE", "Capture changed while preparing the device.");
            state = GetOrCreateIndependentDeviceState(device);
            if (state.IsStarting || state.IsStopping) throw new AutomationException("MANAGEMENT_BUSY", "Capture is changing.");
            state.IsStarting = true;
            var settings = CaptureSessionStartSettings(state);
            var created = await _sessions.CreateAsync(() => _core.CreateExternalSession(device.Udid, audio,
                settings.Volume / 100, settings.RenderWidth, settings.RenderHeight,
                (uint)settings.FrameRate, (uint)settings.DecoderPreference), cancellation);
            if (!created.Success || created.Handle is null) throw new InvalidOperationException(created.Message);
            _sessions.SetHandle(state, created.Handle);
            var adjustments = _core.SetDeviceImageAdjustments(created.Handle,
                settings.Brightness, settings.Contrast, settings.Saturation, settings.Gamma);
            if (!adjustments.Success) throw new InvalidOperationException(adjustments.Message);
            var feed = new CoreDeviceCaptureFeed(_core, created.Handle, async () =>
            {
                try { await bridge.DeviceHubAsync("media_stop", null, () => true, CancellationToken.None); }
                catch (Exception error) when (error is AutomationException or OperationCanceledException or TimeoutException or IOException) { }
            });
            state.ExternalCapture = feed;
            var display = AppleDisplayCatalog.Find(device.ProductType);
            var result = await bridge.DeviceHubAsync("media_start", new { pipe = feed.PipeName, audio, record,
                width = display?.Width ?? 0, height = display?.Height ?? 0 }, () => !_disposed, cancellation);
            state.PlayAudio = audio;
            state.MarkVideoSettingsApplied(settings.RenderWidth, settings.RenderHeight, settings.FrameRate,
                settings.DecoderPreference, settings.Brightness, settings.Contrast, settings.Saturation, settings.Gamma);
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            {
                IsCapturing = true; _activeCaptureUdid = device.Udid;
                NotifyCaptureSessionChanged(); NativeCore.SelectPreviewSession(state.Handle);
                OnPropertyChanged(nameof(CurrentSessionHandle));
            }
            return result;
        }
        catch
        {
            if (state is not null) await _sessions.StopAndDestroyAsync(state);
            throw;
        }
        finally { if (state is not null) state.IsStarting = false; _coreGate.Release(); }
    }

    internal async Task<(string Id, ulong Handle)> StartMediaReplayAsync(string path, double speed = 1,
        CancellationToken cancellation = default)
    {
        await _coreGate.WaitAsync(cancellation);
        DeviceCaptureState? state = null;
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MainViewModel));
            var id = "replay-" + Guid.NewGuid().ToString("N");
            var result = await _sessions.CreateAsync(() => _core.CreateExternalSession(id, true, 1), cancellation);
            if (!result.Success || result.Handle is null) throw new InvalidOperationException(result.Message);
            state = new DeviceCaptureState { Udid = id };
            _sessions.Set(state); _sessions.SetHandle(state, result.Handle);
            var feed = new CoreDeviceCaptureFeed(_core, result.Handle, null);
            state.ExternalCapture = feed;
            feed.StartReplay(path, speed);
            return (id, result.Handle.RawHandle);
        }
        catch { if (state is not null) await _sessions.StopAndDestroyAsync(state); throw; }
        finally { _coreGate.Release(); }
    }
}
