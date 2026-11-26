using System.Diagnostics;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private Task RetryExhaustedUsbControlAsync(DeviceControlSession control,
        UsbTouchBridgeHost bridge, DeviceViewModel device) => control.WiredOperation.RunAsync(async token =>
    {
        if (_disposed || control.Starting || control.Stopping || !control.WiredEnabled ||
            !ReferenceEquals(control.WiredBridge, bridge)) return;
        control.WiredRecoveryInProgress = true;
        try
        {
            await RetryUsbControlAsManualAsync(control, device, token);
        }
        finally { control.WiredRecoveryInProgress = false; }
    }, _shutdownCancellation.Token);

    private async Task RetryUsbControlAsManualAsync(DeviceControlSession control,
        DeviceViewModel device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || control.Stopping || !control.WiredEnabled) return;
        control.WiredRecovery.Disconnected(Stopwatch.GetTimestamp());
        var retry = control.WiredRecovery.TryTakeFinalRetry();
        control.WiredEnabled = control.WiredConnected = false;
        control.Router.Stop();
        control.WiredFinalRetryInProgress = true;
        try
        {
            await StopDuoWiredShortcutsAsync(control);
            control.ControlStatus.Report(ControlStatusMode.Usb, ControlStage.Recovering,
                device.Name, LocalizationService.Get("ControlAutoReconnectingChannel"));
            control.Status = LocalizationService.Get("ReverseControlUsbConnecting");
            DiagnosticLogger.ReverseControl("usb", retry ? "final_retry_begin" : "final_retry_budget_exhausted",
                ("device", AppLog.Device(device.Udid)));
            if (retry)
            {
                // Reuse manual Retry, including the exact binding check and mux
                // checkpoint. Do not stop/recreate capture or reset USB here.
                await StartDeviceControlCoreAsync(control, device, false, token, automaticRecovery: true);
            }
            else
            {
                control.Starting = true;
                NotifyUsbControlStateChanged();
                if (control.WiredBridge is { } oldBridge)
                {
                    await oldBridge.DisposeAsync();
                    if (ReferenceEquals(control.WiredBridge, oldBridge)) control.WiredBridge = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            control.ControlStatus.Failed(ControlStatusMode.Usb, device.Name,
                LocalizationService.Get("ControlRecoveryFailed"), AppLog.Error(error));
            DiagnosticLogger.ReverseControlError("usb", "final_retry_failed",
                ("device", AppLog.Device(device.Udid)), ("error", AppLog.Error(error)));
        }
        finally
        {
            control.WiredFinalRetryInProgress = false;
            control.Starting = false;
            NotifyUsbControlStateChanged();
        }
        if (token.IsCancellationRequested || _disposed || control.Stopping) return;
        if (control.WiredEnabled && control.WiredConnected && control.WiredBridge?.IsReady == true)
        {
            DiagnosticLogger.ReverseControl("usb", "final_retry_complete",
                ("device", AppLog.Device(device.Udid)));
            return;
        }
        // User action remains available after the automatic attempt has ended.
        if (control.ControlStatus.Current?.Stage == ControlStage.WaitingForDeveloperMode)
        {
            control.ControlStatus.WaitForDeveloperMode(device.Name, control.Status);
            if (!SetupActive && !AutomationControlStartup.Contains(device.Udid) &&
                Application.Current?.MainWindow is { } owner)
                ReverseControlStatusWindow.Show(owner, control.ControlStatus,
                    () => _ = CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid),
                    () => _ = StartUsbControlAsync(device.Udid));
            return;
        }
        control.WiredEnabled = control.WiredConnected = false;
        control.WiredTarget = null;
        control.Failed = true;
        control.Router.Stop();
        var message = control.ControlStatus.Current is { Stage: ControlStage.Failed, Error: { } failure }
            ? failure : LocalizationService.Get("UsbControlFailureRecoveryExhausted");
        control.Status = LocalizationService.Format("ReverseControlUsbFailedFormat", message);
        DiagnosticLogger.ReverseControlError("usb", "final_retry_terminal",
            ("device", AppLog.Device(device.Udid)));
        // Emit failure only after the final attempt/cleanup. Intermediate startup
        // failures were kept out of the window's queued status notifications.
        control.ControlStatus.Failed(ControlStatusMode.Usb, device.Name, message);
        ShowDeviceControlError(control, LocalizationService.Get("ReverseControlTransportWired"),
            message, "ReverseControlRecoveryErrorTitle");
        NotifyUsbControlStateChanged();
    }
}
