using System.Text.Json;
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

}
