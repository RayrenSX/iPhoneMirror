using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    internal (string Udid, Func<string, CancellationToken, Task> Install) CapturePreviewIpaInstaller(
        DeviceViewModel device, Func<bool> isCurrent)
    {
        var bridge = GetReadyDeviceHubBridge(device) ??
            throw new AutomationException("PREVIEW_FILES_CONTROL_REQUIRED", "", 503);
        var udid = GetPreviewSharedFilesTarget(device);
        if (string.IsNullOrWhiteSpace(udid))
            throw new AutomationException("PREVIEW_FILES_BINDING_REQUIRED", "");
        var guard = AutomationHumanGuard(device.Udid);
        var generation = bridge.InputGeneration;
        bool Current() => !_disposed && isCurrent() && Devices.Contains(device) && guard() &&
            ReferenceEquals(GetReadyDeviceHubBridge(device), bridge) && bridge.InputGeneration == generation &&
            DeviceViewModel.UdidEquals(GetPreviewSharedFilesTarget(device), udid);
        return (udid, async (path, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (_wiredMetadataReads.TryGetValue(device.Udid, out var metadataRead))
                await metadataRead.WaitAsync(cancellation);
            if (!Current()) throw new AutomationException("CONTROL_NOT_AVAILABLE", "", 503);
            using var owner = AutomationOwnership.EnterInput(AutomationPhysicalId(device.Udid));
            await bridge.DeviceHubAsync("install_app", new { path }, Current, cancellation);
        });
    }
}
