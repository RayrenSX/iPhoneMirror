using System.Windows;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private readonly Dictionary<string, DeviceControlSession> _deviceControls =
        new(StringComparer.OrdinalIgnoreCase);

    private DeviceControlSession? SelectedControl => FindControl(SelectedDevice?.Udid);
    internal void SetControlInputDevice(string? udid)
    {
        if (_disposed) return;
        ClipboardSync.SelectDevice(udid);
        _ = FlushDeviceClipboardAsync();
    }
    internal bool IsDeviceControlEnabled(string? udid) => FindControl(udid)?.Enabled == true;

    private DeviceControlSession? FindControl(string? udid) =>
        udid is not null && _deviceControls.TryGetValue(udid, out var control) ? control : null;

    private DeviceControlSession GetOrCreateControl(string udid)
    {
        if (_deviceControls.TryGetValue(udid, out var existing)) return existing;
        var control = new DeviceControlSession(udid);
        _deviceControls.Add(udid, control);
        control.ControlStatus.StatusChanged += (_, snapshot) =>
        {
            // Automatic recovery must not take focus from another device.
            // Its failure remains visible when this device is selected again.
            if (_disposed || (snapshot.Prompt is null && snapshot.Stage != ControlStage.Failed)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || (snapshot.Prompt is null &&
                    !DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))) return;
                if (Application.Current?.MainWindow is { } owner)
                    ReverseControlStatusWindow.Show(owner, control.ControlStatus,
                        () => _ = CancelReverseControlAsync(snapshot.Mode, udid),
                        () => _ = StartDeviceControlAsync(udid, snapshot.Mode == ControlStatusMode.Wireless));
            }));
        };
        return control;
    }

    internal ControlStatusService GetControlStatus(ControlStatusMode mode, string? udid = null) =>
        mode == ControlStatusMode.Bluetooth || (udid ?? SelectedDevice?.Udid) is not { } target
            ? ControlStatus : GetOrCreateControl(target).ControlStatus;

    private bool IsDeviceControlBusy(string? udid)
    {
        if (FindControl(udid) is { } control && (control.Enabled || control.Starting || control.Stopping))
            return true;
        var appleUdid = udid is null ? null : ResolveAppleUdid(udid);
        return appleUdid is not null && _deviceControls.Values.Any(c =>
            (c.Enabled || c.Starting || c.Stopping) && DeviceViewModel.UdidEquals(c.AppleUdid, appleUdid));
    }

    private Task StartDeviceControlAsync(string? udid, bool wireless)
    {
        var target = udid ?? SelectedDevice?.Udid;
        if (_disposed || target is null) return Task.CompletedTask;
        var device = Devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, target));
        var control = GetOrCreateControl(target);
        var mode = wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb;
        if (control.Enabled || control.Starting || control.Stopping) return Task.CompletedTask;
        if (IsDeviceControlBusy(target))
        {
            control.ControlStatus.Failed(mode, device?.Name ?? "iPhone",
                LocalizationService.Get("ControlDeviceNotReady"),
                LocalizationService.Get("MultiDeviceControlAlreadyActive"));
            return Task.CompletedTask;
        }
        if (wireless ? !CanEnableWirelessControlFor(device) : !CanEnableUsbControlFor(device))
        {
            control.ControlStatus.Failed(mode, device?.Name ?? "iPhone",
                LocalizationService.Get("ControlDeviceNotReady"));
            return Task.CompletedTask;
        }
        var operation = wireless ? control.WirelessOperation : control.WiredOperation;
        return operation.RunAsync(async token =>
        {
            try
            {
                // Only the operation admitted by SingleFlight may change startup
                // state. A request that joins recovery must leave its state intact.
                control.AppleUdid = null;
                control.Binding = null;
                control.Failed = false;
                control.RequestedWireless = wireless;
                control.Starting = true;
                control.ControlStatus.Begin(mode, device!.Name);
                NotifyUsbControlStateChanged();
                // Every entry (toolbar, shortcut, preview menu, command and retry)
                // opens the same status surface before inspecting the binding.
                if (Application.Current?.MainWindow is { } owner)
                    ReverseControlStatusWindow.Show(owner, control.ControlStatus,
                        () => _ = CancelReverseControlAsync(mode, target),
                        () => _ = StartDeviceControlAsync(target, wireless));
                token.ThrowIfCancellationRequested();
                control.Binding = _identityResolver.ResolveControlBinding(device,
                    wireless ? ReverseControlMode.Wireless : ReverseControlMode.Usb);
                if (!ValidateControlBinding(control, device, mode)) return;
                control.AppleUdid = control.Binding!.TargetStableId;
                if (wireless) await EnableWirelessControlCoreAsync(control, token);
                else await EnableUsbControlCoreAsync(control, token);
            }
            finally
            {
                control.Starting = false;
                NotifyUsbControlStateChanged();
            }
        }, _shutdownCancellation.Token);
    }

    private bool IsControlBindingCurrent(DeviceControlSession control, DeviceViewModel? device,
        ControlStatusMode mode) => IsControlDeviceAvailable(device) &&
        DeviceViewModel.UdidEquals(control.DeviceUdid, device!.Udid) &&
        control.Binding?.Matches(_identityResolver.ResolveControlBinding(device,
            mode == ControlStatusMode.Wireless ? ReverseControlMode.Wireless : ReverseControlMode.Usb)) == true;

    private bool ValidateControlBinding(DeviceControlSession control,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeviceViewModel? device,
        ControlStatusMode mode)
    {
        if (IsControlBindingCurrent(control, device, mode)) return true;
        control.Router.Stop();
        control.Failed = true;
        control.Status = LocalizationService.Get("ControlBindingRequired");
        control.ControlStatus.Failed(mode, device?.Name ?? "iPhone",
            ControlBindingFailureMessage(),
            LocalizationService.Get("ControlBindingAdvice"));
        return false;
    }

    private void EnsureControlBindingCurrent(DeviceControlSession control, DeviceViewModel? device,
        ControlStatusMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsControlBindingCurrent(control, device, mode))
            throw new ControlBindingException(ControlBindingFailureMessage());
    }

    private sealed class ControlBindingException(string message) : InvalidOperationException(message);

    private static string ControlBindingFailureMessage() => LocalizedText.Join("\n\n",
        [LocalizationService.Get("ControlBindingRequired"), LocalizationService.Get("ControlBindingAdvice")]);

    private Task DisableDeviceControlAsync(string? udid, bool wireless)
    {
        var control = FindControl(udid ?? SelectedDevice?.Udid);
        return control is null ? Task.CompletedTask : wireless
            ? DisableWirelessControlAsync(control) : DisableUsbControlAsync(control);
    }

    internal Task DisableUsbControlAsync() => DisableDeviceControlAsync(null, false);
    private Task DisableWirelessControlAsync() => DisableDeviceControlAsync(null, true);

    private void ShowDeviceControlError(DeviceControlSession control, string transport,
        string? detail, string titleKey = "ReverseControlStartErrorTitle",
        string? technicalDetails = null, Func<Task>? retryOperation = null) =>
        ShowReverseControlError(transport, detail, titleKey, technicalDetails,
            retryOperation, control);
}
