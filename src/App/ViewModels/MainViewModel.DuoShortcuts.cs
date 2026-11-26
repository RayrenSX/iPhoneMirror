using System.Windows;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _duoShortcutsGate = new(1, 1);
    private DeviceControlSession? _duoShortcutsOwner;

    private bool DuoWiredShortcutRouteIsReady(DeviceControlSession control) =>
        !_disposed && !_bluetoothControlEnabled && !_bluetoothControlStarting && !_bluetoothControlStopping &&
        control is { WiredEnabled: true, WiredConnected: true, Stopping: false, WiredBridge.IsReady: true };

    private async Task PrewarmDuoWiredShortcutsAsync(DeviceViewModel device, DeviceControlSession control)
    {
        if (!IsIPhoneDuo(device) || !DuoWiredShortcutRouteIsReady(control) ||
            Application.Current is not App { IsUiPreviewMode: false } app) return;

        await _duoShortcutsGate.WaitAsync();
        try
        {
            if (!DuoWiredShortcutRouteIsReady(control) || _duoShortcutsOwner is not null ||
                _bluetoothControl.IsAdvertising || GetBluetoothControlBinding(device.Udid) is not { Length: > 0 } client)
                return;
            var absolute = BluetoothWindowMouseEnabled;
            var reportMap = absolute ? BluetoothHidProtocol.AbsoluteMouseReportMapVersion : BluetoothHidProtocol.ReportMapVersion;
            if (app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion != reportMap) return;

            _duoShortcutsOwner = control;
            if (await _bluetoothControl.StartAsync(device.Udid, device.Name, preserveExistingBinding: true,
                    preferredClientId: client, cancellationToken: _shutdownCancellation.Token, useAbsoluteMouse: absolute) &&
                DuoWiredShortcutRouteIsReady(control))
            {
                DiagnosticLogger.ReverseControl("usb", "duo_ble_shortcuts_advertising", ("device", AppLog.Device(device.Udid)));
                return;
            }
            await StopDuoWiredShortcutProviderAsync();
        }
        catch (Exception error)
        {
            DiagnosticLogger.ReverseControlWarning("usb", "duo_ble_shortcuts_start_failed", ("error", AppLog.Error(error)));
            await StopDuoWiredShortcutProviderAsync();
        }
        finally { _duoShortcutsGate.Release(); }
    }

    private async Task StopDuoWiredShortcutsAsync(DeviceControlSession? owner = null)
    {
        if (_duoShortcutsOwner is null || (owner is not null && !ReferenceEquals(_duoShortcutsOwner, owner))) return;
        await _duoShortcutsGate.WaitAsync();
        try
        {
            if (_duoShortcutsOwner is not null && (owner is null || ReferenceEquals(_duoShortcutsOwner, owner)))
                await StopDuoWiredShortcutProviderAsync();
        }
        finally { _duoShortcutsGate.Release(); }
    }

    private async Task StopDuoWiredShortcutProviderAsync()
    {
        try { await _bluetoothControl.StopAsync(); }
        catch (Exception error)
        {
            DiagnosticLogger.ReverseControlWarning("usb", "duo_ble_shortcuts_stop_failed", ("error", AppLog.Error(error)));
        }
        finally { _duoShortcutsOwner = null; }
    }

    internal async Task<bool> TrySendDuoWiredShortcutAsync(string? deviceUdid,
        BluetoothShortcutAction action, byte keyboardUsage, bool usesGlobe, Func<bool> canSend)
    {
        var navigation = action is BluetoothShortcutAction.Home or BluetoothShortcutAction.AppSwitcher;
        if ((!navigation && !usesGlobe) || deviceUdid is null || _duoShortcutsOwner is not { } control ||
            !DeviceViewModel.UdidEquals(deviceUdid, control.DeviceUdid) ||
            GetBluetoothControlBinding(deviceUdid) is not { Length: > 0 } client ||
            !AutomationHumanAllowed(deviceUdid)) return false;

        bool Current() => ReferenceEquals(_duoShortcutsOwner, control) && DuoWiredShortcutRouteIsReady(control) &&
            _bluetoothControl.CanSendSystemShortcutTo(deviceUdid, client, navigation) && canSend();

        if (!Current()) return false;
        if (action == BluetoothShortcutAction.Home)
            await _bluetoothControl.SendIphoneHomeNavigationAsync(Current);
        else if (action == BluetoothShortcutAction.AppSwitcher)
            await _bluetoothControl.SendIphoneAppSwitcherAsync(deviceUdid, Current);
        else
            await _bluetoothControl.SendIphoneSystemShortcutAsync(keyboardUsage, deviceUdid, Current);
        return true;
    }
}
