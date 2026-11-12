using System.ComponentModel;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

// WPF Selector indexes items by hash. A record's generated hash would include
// the mutable PropertyChanged delegate, so binding Label can break selection.
internal sealed class MirroringControlModeOption(ReverseControlMode mode, string resourceKey)
    : INotifyPropertyChanged
{
    public ReverseControlMode Mode { get; } = mode;
    public string Label => LocalizationService.Get(resourceKey);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() =>
        PropertyChanged?.Invoke(this, new(nameof(Label)));
}

internal sealed partial class MainViewModel
{
    private readonly HashSet<string> _mirroringControlStartup = new(StringComparer.OrdinalIgnoreCase);
    private long _mirroringControlPreferenceGeneration;
    private string _mirroringAutoControlStatus = string.Empty;
    public string MirroringAutoControlStatus
    {
        get => LocalizationService.RefreshText(_mirroringAutoControlStatus);
        private set => Set(ref _mirroringAutoControlStatus, value);
    }

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
            if (previous == value.Mode) return;
            app.UpdateSettings.MirroringAutoControlMode = value.Mode;
            if (!app.SaveUpdateSettings())
            {
                app.UpdateSettings.MirroringAutoControlMode = previous;
                MirroringAutoControlStatus = LocalizationService.Get("SetupSaveFailed");
                OnPropertyChanged();
                return;
            }
            MirroringAutoControlStatus = string.Empty;
            ++_mirroringControlPreferenceGeneration;
            OnPropertyChanged();
            // A successful explicit choice also applies to an existing stream.
            // Do not require reconnecting after a prior manual/off observation.
            foreach (var state in _sessions.Values.ToArray())
            {
                state.AutoControlAttempted = false;
                if (state.AutoControlStreaming)
                    TryStartMirroringControl(state, new NativeCaptureStatus { State = CaptureState.Streaming });
            }
        }
    }

    // Both foreground and independent-window sessions pass through these status
    // readers. Attempt once per native session, so a failed or manually stopped
    // control route is never restarted by the next polling tick.
    private void TryStartMirroringControl(DeviceCaptureState state, NativeCaptureStatus status)
    {
        state.AutoControlStreaming = status.State == CaptureState.Streaming;
        if (_disposed || state.AutoControlAttempted || state.IsStopping || !state.HasSession ||
            status.State != CaptureState.Streaming) return;
        var mode = SelectedMirroringAutoControlMode.Mode;
        if (mode == ReverseControlMode.None || SetupActive || !AutomationHumanAllowed(state.Udid)) return;
        if (HasActiveMirroringControl(state.Udid))
        {
            // Keep a manually established route under the user's control.
            // Stopping it later must not cause automatic startup on the next poll.
            state.AutoControlAttempted = true;
            return;
        }
        if (IsDeviceControlBusy(state.Udid) || _bluetoothControlEnabled || _bluetoothControlStarting ||
            _bluetoothControlStopping || _mirroringControlStartup.Contains(state.Udid)) return;
        state.AutoControlAttempted = true;
        var handle = state.Handle!;
        var preferenceGeneration = _mirroringControlPreferenceGeneration;
        // Leave the capture polling/creation stack before starting control.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsCurrentSession(state, handle) || preferenceGeneration != _mirroringControlPreferenceGeneration ||
                SelectedMirroringAutoControlMode.Mode != mode || !state.AutoControlStreaming) return;
            if (SetupActive || !AutomationHumanAllowed(state.Udid) || IsDeviceControlBusy(state.Udid) ||
                _bluetoothControlEnabled || _bluetoothControlStarting || _bluetoothControlStopping ||
                _mirroringControlStartup.Contains(state.Udid))
            {
                state.AutoControlAttempted = false;
                return;
            }
            _ = StartMirroringControlAsync(state.Udid, mode);
        }));
    }

    private bool HasActiveMirroringControl(string udid) => FindControl(udid)?.Enabled == true ||
        (_bluetoothControlEnabled && DeviceViewModel.UdidEquals(_bluetoothControlDeviceUdid, udid));

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
            var statusMode = mode == ReverseControlMode.Bluetooth ? ControlStatusMode.Bluetooth :
                mode == ReverseControlMode.Wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb;
            GetControlStatus(statusMode, udid).Failed(statusMode,
                Devices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, udid))?.Name ?? "iPhone",
                LocalizationService.Get("ControlOperationFailed"), error.Message);
        }
        finally { _mirroringControlStartup.Remove(udid); }
    }
}
