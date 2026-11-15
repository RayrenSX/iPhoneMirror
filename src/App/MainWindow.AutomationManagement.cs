using System.Windows;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim _automationManagementGate = new(1, 1);

    internal async Task StartAutomationBackgroundAsync()
    {
        if (_shutdownStarted || Application.Current is not App app) return;
        app.AutomationBackground = true;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Hide();
        StartInTray();
        await StartAutomationOnStartupAsync();
        if (!AutomationRunning)
        {
            var settings = AutomationConfiguration;
            settings.Enabled = true;
            await ConfigureAutomationAsync(settings, save: false);
        }
    }

    private async Task<object> ManageAutomationAsync(string action, string? id, bool enableControl, CancellationToken cancellation)
    {
        var operation = await Dispatcher.InvokeAsync(() => ManageAutomationUiAsync(action, id, enableControl, cancellation),
            System.Windows.Threading.DispatcherPriority.Normal, cancellation);
        return await operation;
    }

    private async Task<object> ManageAutomationUiAsync(string action, string? id, bool enableControl, CancellationToken cancellation)
    {
        if (action == "status") return new { managementVersion = 1, background = (Application.Current as App)?.AutomationBackground == true,
            mainWindowVisible = IsVisible, apiRunning = AutomationRunning, independentWindowOpen = _secondaryMirrors.HasAnyOpen };
        if (!await _automationManagementGate.WaitAsync(0, cancellation))
            throw new AutomationException("MANAGEMENT_BUSY", "Another device preparation is active.");
        try
        {
            var device = _viewModel.Devices.FirstOrDefault(d => AutomationDeviceId.Encode(d.Udid) == id)
                ?? throw new AutomationException("DEVICE_NOT_FOUND", "Select a device returned by list_devices.", 404);
            var physical = _viewModel.AutomationPhysicalId(device.Udid);
            if (_viewModel.AutomationOwnership.Snapshot().Any(l => l.PhysicalId == physical))
                throw new AutomationException("CONTROL_LOCKED", "Release the device control lease before preparing or opening its window.");
            cancellation.ThrowIfCancellationRequested();
            if (action == "window")
            {
                var result = await _secondaryMirrors.ShowAsync(device, cancellation);
                if (!result.Success) throw new AutomationException("DEVICE_NOT_READY", result.Message);
                return new { opened = _secondaryMirrors.IsOpen(device), mainWindowVisible = IsVisible };
            }
            var started = await _viewModel.StartBackgroundSessionAsync(device, cancellation);
            if (!started.Success) throw new AutomationException("DEVICE_NOT_READY", started.Message);
            var backend = (IAutomationBackend)_viewModel;
            var target = await backend.ResolveAsync(id!, cancellation);
            if (enableControl && !target.Device.ControlAvailable)
                await _viewModel.StartAutomationControlAsync(device.Udid, device.IsWireless, cancellation);
            target = await backend.ResolveAsync(id!, cancellation);
            return new { capturing = target.Device.Capturing, controlAvailable = target.Device.ControlAvailable,
                mainWindowVisible = IsVisible,
                actionRequired = enableControl && !target.Device.ControlAvailable
                    ? "Check device binding, unlock/trust, Developer Mode and matching DDI in iPhoneMirror diagnostics." : null };
        }
        finally { _automationManagementGate.Release(); }
    }
}
