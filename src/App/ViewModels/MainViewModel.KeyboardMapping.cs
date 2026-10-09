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

    private string _keyboardInputModeStatus = "KeyboardInputModeNone";
    public string KeyboardInputModeStatus => Localization.LocalizationService.Get(_keyboardInputModeStatus);
    internal void SetKeyboardInputModeStatus(string resourceKey)
    {
        if (_keyboardInputModeStatus == resourceKey) return;
        _keyboardInputModeStatus = resourceKey;
        OnPropertyChanged(nameof(KeyboardInputModeStatus));
    }

    internal string GetMappingTargetStatus()
    {
        if (_disposed || SelectedDevice is null) return "MappingNoDevice";
        if (SelectedDevice.IsMediaCast && !SelectedDevice.IsWireless) return "MappingUnsupportedDevice";
        if (IsBluetoothControlTarget(SelectedDevice.Udid)) return "MappingBluetoothUnsupported";
        var control = SelectedControl;
        if (control is { Starting: true } or { Stopping: true }) return "MappingConnecting";
        if (GetReadyUsbControlBridge(SelectedDevice.Udid) is null) return "MappingControlNotReady";
        if (!IsCapturing || CurrentSessionHandle == 0 || SourceVideoWidth == 0 || SourceVideoHeight == 0)
            return "MappingGeometryUnavailable";
        return "MappingReady";
    }

    internal MappedTouchRoute? CaptureMappingRoute(Func<bool> isCurrent,
        Func<double, double, (double X, double Y)> transform)
        => CaptureTouchRoute(SelectedDevice?.Udid, isCurrent, transform, requireSelection: true);

    private int _nextTouchPointerId = 1; // Mouse/wheel retains ID 1.

    internal MappedTouchRoute? CaptureTouchRoute(string? targetUdid, Func<bool> isCurrent,
        Func<double, double, (double X, double Y)> transform, bool requireSelection = false, bool automation = false)
    {
        if (targetUdid is null) return null;
        var humanGuard = automation ? (Func<bool>)(() => true) : AutomationHumanGuard(targetUdid);
        if (!humanGuard()) return null;
        var control = FindControl(targetUdid);
        var bridge = GetReadyUsbControlBridge(targetUdid);
        if (control is null || control.Starting || control.Stopping || bridge is null || control.AppleUdid is null ||
            IsBluetoothControlTarget(targetUdid)) return null;
        var appleUdid = control.AppleUdid;
        var mode = control.WiredEnabled && ReferenceEquals(control.WiredBridge, bridge)
            ? ReverseControlMode.Usb : ReverseControlMode.Wireless;
        var routerGeneration = control.Router.Generation;
        var bridgeGeneration = bridge.InputGeneration;
        bool SameSession() => bridge.IsReady && bridge.InputGeneration == bridgeGeneration &&
            control.Router.Owns(appleUdid, mode, routerGeneration);
        bool Current() => humanGuard() && SameSession() && !_disposed &&
            (!requireSelection || DeviceViewModel.UdidEquals(SelectedDevice?.Udid, control.DeviceUdid)) &&
            (ReferenceEquals(control.WiredBridge, bridge) || ReferenceEquals(control.WirelessBridge, bridge)) && isCurrent();
        if (!Current()) return null;
        var pointerId = Interlocked.Increment(ref _nextTouchPointerId);
        return new(control.DeviceUdid, Current, async (action, x, y, token) =>
        {
            if (!SameSession()) throw new OperationCanceledException();
            if (action != "up" && !Current()) throw new OperationCanceledException();
            // Every gesture keeps its own ID, including cleanup after focus loss.
            // Recheck focus, selection and geometry inside the writer lock;
            // they can change while a down/move waits behind another packet.
            // The transport deliberately exempts cleanup releases from this guard.
            await SendRoutedTouchAsync(bridge, action, x, y, pointerId, token,
                () => Current() && !token.IsCancellationRequested, bridgeGeneration);
            if (action != "up" && !Current()) throw new OperationCanceledException();
        }, transform);
    }
}
