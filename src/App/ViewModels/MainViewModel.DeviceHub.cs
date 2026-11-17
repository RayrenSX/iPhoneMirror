using System.Text.Json;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private readonly HashSet<string> _deviceHubConnectionStartup = new(StringComparer.OrdinalIgnoreCase);

    internal async Task PrepareDeviceHubConnectionAsync(DeviceViewModel device, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (IsDeviceHubReady(device)) return;
        if (_disposed || device.IsMediaCast || !Devices.Contains(device))
            throw new AutomationException("DEVICE_HUB_CONNECTION_FAILED", "The selected device is unavailable.");
        if (SetupActive)
            throw new AutomationException("MANAGEMENT_BUSY", "Device setup is in progress.");
        if (!AutomationHumanAllowed(device.Udid))
            throw new AutomationException("CONTROL_LOCKED", "The device is owned by an automation session.");
        if (FindControl(device.Udid) is { Starting: true } starting)
        {
            // Join a manual or mirroring startup without taking ownership of
            // its cancellation when the panel closes or selection changes.
            await Task.WhenAll(starting.WiredOperation.RunAsync(_ => Task.CompletedTask),
                starting.WirelessOperation.RunAsync(_ => Task.CompletedTask)).WaitAsync(cancellation);
            return;
        }
        _deviceHubConnectionStartup.Add(device.Udid);
        try { await StartAutomationControlAsync(device.Udid, device.IsWireless, cancellation); }
        finally { _deviceHubConnectionStartup.Remove(device.Udid); }
    }

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
