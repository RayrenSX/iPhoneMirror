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
