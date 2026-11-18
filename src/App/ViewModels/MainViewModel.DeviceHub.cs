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
            await Task.WhenAll(starting.WiredOperation.PendingTask,
                starting.WirelessOperation.PendingTask).WaitAsync(cancellation);
            return;
        }
        _deviceHubConnectionStartup.Add(device.Udid);
        try { await StartAutomationControlAsync(device.Udid, device.IsWireless, cancellation); }
        finally { _deviceHubConnectionStartup.Remove(device.Udid); }
    }

    internal bool IsDeviceHubReady(DeviceViewModel device) => GetReadyDeviceHubBridge(device) is not null;

    private UsbTouchBridgeHost? GetReadyDeviceHubBridge(DeviceViewModel device)
    {
        if (FindControl(device.Udid) is not { } control ||
            GetReadyUsbControlBridge(device.Udid) is not { SupportsDeviceHub: true } bridge) return null;
        var mode = ReferenceEquals(bridge, control.WiredBridge) ? ControlStatusMode.Usb : ControlStatusMode.Wireless;
        return IsControlBindingCurrent(control, device, mode) ? bridge : null;
    }

    internal async Task<JsonElement> DeviceHubUiAsync(DeviceViewModel device, string action,
        object? arguments, CancellationToken cancellation)
    {
        var bridge = GetReadyDeviceHubBridge(device) ??
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "Prepare the device's USB control connection first.", 503);
        bool Current() => ReferenceEquals(GetReadyDeviceHubBridge(device), bridge);
        if (DeviceHubContract.Require(action).ReadOnly)
            return await bridge.DeviceHubAsync(action, arguments, Current, cancellation);
        var physical = AutomationPhysicalId(device.Udid);
        var guard = AutomationHumanGuard(device.Udid);
        using var owner = AutomationOwnership.EnterInput(physical);
        return await bridge.DeviceHubAsync(action, arguments, () => Current() && guard(), cancellation);
    }

}
