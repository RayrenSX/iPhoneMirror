using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    internal bool CanUseDeviceClipboard(string? target) => !_disposed &&
        FindControl(target) is { Starting: false, Stopping: false } &&
        GetReadyUsbControlBridge(target) is not null;

    internal DirectKeyboardRoute? CaptureDirectKeyboardRoute(string? target, bool automation = false)
    {
        if (_disposed || target is null) return null;
        var humanGuard = automation ? (Func<bool>)(() => true) : AutomationHumanGuard(target);
        if (!humanGuard()) return null;
        var control = FindControl(target);
        var bridge = GetReadyUsbControlBridge(target);
        if (control is not null && !control.Starting && !control.Stopping && bridge is not null)
        {
            var mode = ReferenceEquals(bridge, control.WiredBridge) && control.WiredEnabled
                ? ReverseControlMode.Usb : ReverseControlMode.Wireless;
            var apple = control.Router.AppleUdid;
            var generation = control.Router.Generation;
            var bridgeGeneration = bridge.InputGeneration;
            bool Current() => humanGuard() && !_disposed && apple is not null && bridge.IsReady &&
                bridge.InputGeneration == bridgeGeneration && control.Router.Owns(apple, mode, generation);
            if (!Current()) return null;
            return new(target, mode == ReverseControlMode.Usb ? "WiredDirect" : "WirelessDirect",
                bridge, generation, Current, (modifiers, usages, canSend) =>
                    bridge.SendKeyboardAsync(usages, canSend: () => Current() && canSend?.Invoke() != false,
                        releaseAll: canSend is null && usages.Count == 0),
                (page, code, state, canSend) => bridge.SendButtonAsync(page, code, state,
                    canSend: () => Current() && canSend?.Invoke() != false, guardRelease: true),
                (text, canSend) => bridge.SendPasteTextAsync(text,
                    canSend: () => Current() && canSend?.Invoke() != false),
                canSend => bridge.SendReadClipboardAsync(
                    canSend: () => Current() && canSend?.Invoke() != false));
        }
        // One route is selected. Bluetooth cannot receive a second copy while
        // a wired/wireless route is ready or still establishing ownership.
        if (control is { Enabled: true } or { Starting: true } or { Stopping: true }) return null;
        if (!BluetoothControlIsConnected || (automation && !_bluetoothControl.IsKeyboardReady) || !IsBluetoothControlTarget(target)) return null;
        var ble = _bluetoothControl.CaptureKeyboardRoute(target);
        return ble with { IsCurrent = () => humanGuard() && ble.IsCurrent() && (!automation || _bluetoothControl.IsKeyboardReady),
            SendAsync = (modifiers, usages, guard) => ble.SendAsync(modifiers, usages,
                () => humanGuard() && guard?.Invoke() != false) };
    }
}
