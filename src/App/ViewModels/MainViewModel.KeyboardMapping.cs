using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    // The main window owns capture/hook lifetime; the view model only exposes
    // selected-device status and the existing transport's guarded touch route.
    public event Action? KeyboardMappingRequested;
    public RelayCommand ManageKeyboardMappingCommand => new(() => KeyboardMappingRequested?.Invoke());
    private string _keyboardMappingStatus = "MappingOff";
    public string KeyboardMappingStatus => Localization.LocalizationService.Get(_keyboardMappingStatus);
    internal void SetKeyboardMappingStatus(string resourceKey)
    {
        if (_keyboardMappingStatus == resourceKey) return;
        _keyboardMappingStatus = resourceKey;
        OnPropertyChanged(nameof(KeyboardMappingStatus));
    }

    internal string GetMappingTargetStatus()
    {
        if (_disposed || SelectedDevice is null) return "MappingNoDevice";
        if (SelectedDevice.IsMediaCast && !SelectedDevice.IsWireless) return "MappingUnsupportedDevice";
        if (IsBluetoothControlTarget(SelectedDevice.Udid)) return "MappingBluetoothUnsupported";
        var control = SelectedControl;
        if (control is { Starting: true } or { Stopping: true }) return "MappingConnecting";
        if (GetReadyUsbControlBridge(SelectedDevice.Udid) is null) return "MappingControlNotReady";
        if (SourceVideoWidth == 0 || SourceVideoHeight == 0) return "MappingGeometryUnavailable";
        return "MappingReady";
    }

    internal MappedTouchRoute? CaptureMappingRoute(Func<bool> isCurrent,
        Func<double, double, (double X, double Y)> transform)
    {
        var control = SelectedControl;
        var bridge = GetReadyUsbControlBridge(SelectedDevice?.Udid);
        if (control is null || control.Starting || control.Stopping || bridge is null || control.AppleUdid is null ||
            IsBluetoothControlTarget(SelectedDevice?.Udid)) return null;
        var appleUdid = control.AppleUdid;
        var mode = control.WiredEnabled && ReferenceEquals(control.WiredBridge, bridge)
            ? ReverseControlMode.Usb : ReverseControlMode.Wireless;
        var routerGeneration = control.Router.Generation;
        var bridgeGeneration = bridge.InputGeneration;
        bool SameSession() => bridge.IsReady && bridge.InputGeneration == bridgeGeneration &&
            control.Router.Owns(appleUdid, mode, routerGeneration);
        bool Current() => SameSession() && !_disposed &&
            DeviceViewModel.UdidEquals(SelectedDevice?.Udid, control.DeviceUdid) &&
            ReferenceEquals(GetReadyUsbControlBridge(control.DeviceUdid), bridge) && isCurrent();
        if (!Current()) return null;
        return new(control.DeviceUdid, Current, async (action, x, y, token) =>
        {
            if (!SameSession()) throw new OperationCanceledException();
            if (action != "up" && !Current()) throw new OperationCanceledException();
            // Pointer 2 is reserved for keyboard gestures; pointer 1 belongs to
            // the existing mouse/wheel path. Neither can release the other.
            // Recheck focus, selection and geometry inside the writer lock;
            // they can change while a down/move waits behind another packet.
            // The transport deliberately exempts cleanup releases from this guard.
            await SendRoutedTouchAsync(bridge, action, x, y, 2, token,
                () => Current() && !token.IsCancellationRequested, bridgeGeneration);
            if (action != "up" && !Current()) throw new OperationCanceledException();
        }, transform);
    }
}
