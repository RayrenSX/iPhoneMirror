using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private readonly HashSet<string> _deviceHubConnectionStartup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _wiredMetadataReads = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshWiredDisplayMetadata(DeviceViewModel device)
    {
        if (device.IsWireless || device.IsMediaCast ||
            (device.State != ConnectionState.UsbPresentNoMux && device.HasWiredDisplayMetadata) ||
            _wiredMetadataReads.ContainsKey(device.Udid) ||
            GetReadyDeviceHubBridge(device) is not { } bridge ||
            FindControl(device.Udid)?.AppleUdid is not { Length: > 0 } appleUdid) return;
        _wiredMetadataReads.Add(device.Udid, ReadWiredDisplayMetadataAsync(device, bridge, appleUdid));
    }

    private async Task ReadWiredDisplayMetadataAsync(DeviceViewModel device, UsbTouchBridgeHost bridge, string appleUdid)
    {
        // Let the control startup finish and release its handshake gate first.
        await Task.Yield();
        bool Current() => !_disposed && Devices.Contains(device) &&
            ReferenceEquals(GetReadyDeviceHubBridge(device), bridge);
        try
        {
            if (!Current()) return;
            var result = await bridge.DeviceHubAsync("device_info", null, Current, _shutdownCancellation.Token);
            if (Current()) ApplyWiredDisplayMetadata(device, appleUdid, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("devices", "wired_metadata_read_failed", error,
                ("device", AppLog.Device(device.Udid)));
        }
        finally { _wiredMetadataReads.Remove(device.Udid); }
    }

    private void ApplyWiredDisplayMetadata(DeviceViewModel device, string appleUdid, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("device", out var group) || group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True ||
            !group.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return;
        string Read(string key) => values.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";
        var reportedUdid = Read("UniqueDeviceID");
        if (reportedUdid.Length > 0 && !DeviceViewModel.UdidEquals(
            reportedUdid.Replace("-", ""), appleUdid.Replace("-", ""))) return;
        var metadata = new WiredDeviceMetadata(Read("DeviceName"), Read("ProductType"), Read("ProductVersion"));
        if (Application.Current is not App { IsUiPreviewMode: true })
            DeviceAppearanceCache.Shared.RememberMetadata(device.Udid, metadata);
        if (device.UpdateDisplayMetadata(metadata) && ReferenceEquals(SelectedDevice, device))
            NotifySelectedDeviceProperties();
    }

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
        try { await StartAutomationControlAsync(device.Udid, device.IsWireless || device.IsPairedNetwork, cancellation); }
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
        if (_wiredMetadataReads.TryGetValue(device.Udid, out var metadataRead))
            await metadataRead.WaitAsync(cancellation);
        var bridge = GetReadyDeviceHubBridge(device) ??
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "Prepare the device's USB control connection first.", 503);
        bool Current() => ReferenceEquals(GetReadyDeviceHubBridge(device), bridge);
        if (DeviceHubContract.Require(action).ReadOnly)
        {
            var result = await bridge.DeviceHubAsync(action, arguments, Current, cancellation);
            if (action == "device_info" && !device.IsWireless && Current() &&
                FindControl(device.Udid)?.AppleUdid is { Length: > 0 } appleUdid)
                ApplyWiredDisplayMetadata(device, appleUdid, result);
            return result;
        }
        var physical = AutomationPhysicalId(device.Udid);
        var guard = AutomationHumanGuard(device.Udid);
        using var owner = AutomationOwnership.EnterInput(physical);
        return await bridge.DeviceHubAsync(action, arguments, () => Current() && guard(), cancellation);
    }

    internal string? GetPreviewSharedFilesTarget(DeviceViewModel device) =>
        device.IsWireless ? ResolveAppleUdid(device.Udid) : device.Udid;

    internal async Task<JsonElement> PreviewSharedFilesAsync(DeviceViewModel device, string? udid,
        string? bundleId, string? sourcePath, Func<bool> isCurrent, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!isCurrent() || _disposed || !Devices.Contains(device) || device.IsMediaCast)
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "");
        if (!IsDeviceHubReady(device))
            throw new AutomationException("PREVIEW_FILES_CONTROL_REQUIRED", "", 503);
        if (string.IsNullOrWhiteSpace(udid))
            throw new AutomationException("PREVIEW_FILES_BINDING_REQUIRED", "");
        if (!DeviceViewModel.UdidEquals(GetPreviewSharedFilesTarget(device), udid))
            throw new AutomationException("CONTROL_NOT_AVAILABLE", "");
        var action = sourcePath is null ? "shared_document_apps" : "upload_file";
        object? arguments = sourcePath is null ? null : new { root = "documents", path = "", bundleId, sourcePath };
        return await DeviceHubUiAsync(device, action, arguments, cancellation);
    }

}
