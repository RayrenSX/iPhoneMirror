using System.ComponentModel;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

internal sealed record MirroringControlModeOption(ReverseControlMode Mode, string ResourceKey)
    : INotifyPropertyChanged
{
    public string Label => LocalizationService.Get(ResourceKey);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() =>
        PropertyChanged?.Invoke(this, new(nameof(Label)));
}

internal sealed partial class MainViewModel
{
    private readonly HashSet<string> _mirroringControlStartup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MirroringControlModeOption> MirroringAutoControlModes { get; } =
    [
        new(ReverseControlMode.None, "MirroringAutoControlOff"),
        new(ReverseControlMode.Bluetooth, "BluetoothControlLabel"),
        new(ReverseControlMode.Usb, "WiredControlLabel"),
        new(ReverseControlMode.Wireless, "WirelessControlLabel"),
    ];

    public MirroringControlModeOption SelectedMirroringAutoControlMode
    {
        get => MirroringAutoControlModes.FirstOrDefault(option => option.Mode ==
            (Application.Current as App)?.UpdateSettings.MirroringAutoControlMode)
            ?? MirroringAutoControlModes[0];
        set
        {
            if (value is null || Application.Current is not App app ||
                !MirroringAutoControlModes.Contains(value)) return;
            var previous = app.UpdateSettings.MirroringAutoControlMode;
            app.UpdateSettings.MirroringAutoControlMode = value.Mode;
            if (!app.SaveUpdateSettings()) app.UpdateSettings.MirroringAutoControlMode = previous;
            OnPropertyChanged();
        }
    }

    // Both foreground and independent-window sessions pass through these status
    // readers. Attempt once per native session, so a failed or manually stopped
    // control route is never restarted by the next polling tick.
    private void TryStartMirroringControl(DeviceCaptureState state, NativeCaptureStatus status)
    {
        if (_disposed || state.AutoControlAttempted || state.IsStopping || !state.HasSession ||
            status.State != CaptureState.Streaming) return;
        state.AutoControlAttempted = true;
        var mode = SelectedMirroringAutoControlMode.Mode;
        if (mode == ReverseControlMode.None || SetupActive || !AutomationHumanAllowed(state.Udid)) return;
        var handle = state.Handle!;
        // Leave the capture polling/creation stack before starting control.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsCurrentSession(state, handle) && SelectedMirroringAutoControlMode.Mode == mode)
                _ = StartMirroringControlAsync(state.Udid, mode);
        }));
    }

    private async Task StartMirroringControlAsync(string udid, ReverseControlMode mode)
    {
        if (_disposed || SetupActive || !AutomationHumanAllowed(udid) || IsDeviceControlBusy(udid) ||
            _bluetoothControlEnabled || _bluetoothControlStarting || _bluetoothControlStopping ||
            !_mirroringControlStartup.Add(udid)) return;
        try
        {
            AddDiagnosticLog(AppLog.Event("mirroring_auto_control_start",
                ("device", AppLog.Device(udid)), ("mode", mode)));
            if (mode == ReverseControlMode.Bluetooth)
            {
                if (Application.Current?.MainWindow is { } owner)
                    ReverseControlStatusWindow.Show(owner, ControlStatus,
                        () => _ = CancelReverseControlAsync(ControlStatusMode.Bluetooth, udid),
                        () => _ = StartBluetoothControlAsync(udid));
                await StartBluetoothControlAsync(udid);
            }
            else if (mode == ReverseControlMode.Usb) await StartUsbControlAsync(udid);
            else if (mode == ReverseControlMode.Wireless) await StartWirelessControlAsync(udid);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("control", "mirroring_auto_control_failed", error);
            AddUiLog(LocalizationService.Format("ReverseControlErrorBodyFormat", mode, error.Message));
        }
        finally { _mirroringControlStartup.Remove(udid); }
    }
}
