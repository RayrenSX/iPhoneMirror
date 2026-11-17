using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private bool _nativeKeyboardChanging;
    private string? _nativeKeyboardStatusKey;
    private RelayCommand? _toggleNativeKeyboardCommand;

    public bool ShowNativeKeyboard => (Application.Current as App)?.UpdateSettings.ShowNativeKeyboard ?? true;
    public string NativeKeyboardButtonText => LocalizationService.Get(
        ShowNativeKeyboard ? "NativeKeyboardShown" : "NativeKeyboardHidden");
    public string NativeKeyboardStatus => _nativeKeyboardStatusKey is null
        ? string.Empty : LocalizationService.Get(_nativeKeyboardStatusKey);
    public bool NativeKeyboardHasError => _nativeKeyboardStatusKey is
        "NativeKeyboardUnavailable" or "NativeKeyboardApplyFailed" or
        "NativeKeyboardSaveFailed" or "NativeKeyboardRollbackFailed";
    public bool CanChangeNativeKeyboard => !_disposed && !_nativeKeyboardChanging &&
        !_deviceControls.Values.Any(control => control.Starting || control.Stopping ||
            control.WiredRecoveryInProgress || control.WiredFinalRetryInProgress ||
            (control.WiredEnabled && !control.WiredConnected) ||
            (control.WirelessEnabled && !control.WirelessConnected));
    public RelayCommand ToggleNativeKeyboardCommand => _toggleNativeKeyboardCommand ??= new(
        () => _ = SetNativeKeyboardVisibleAsync(!ShowNativeKeyboard), () => CanChangeNativeKeyboard);

    private void CheckNativeKeyboardCapability(UsbTouchBridgeHost bridge)
    {
        bool Unavailable(UsbTouchBridgeHost route) => !route.IsReady ||
            !route.SupportsNativeKeyboardVisibility || route.NativeKeyboardVisible != ShowNativeKeyboard;
        // A ready event also belongs to an in-process HID recovery. Aggregate
        // its actual route with the other live peers so the last healthy peer
        // cannot hide a mismatch on another phone or transport.
        var unavailable = Unavailable(bridge) || _deviceControls.Values.Any(control =>
            (control.WiredEnabled && control.WiredConnected && control.WiredBridge is { } wired && Unavailable(wired)) ||
            (control.WirelessEnabled && control.WirelessConnected && control.WirelessBridge is { } wireless && Unavailable(wireless)));
        var next = unavailable ? "NativeKeyboardUnavailable" :
            _nativeKeyboardStatusKey == "NativeKeyboardUnavailable" ? null : _nativeKeyboardStatusKey;
        if (_nativeKeyboardStatusKey == next) return;
        _nativeKeyboardStatusKey = next;
        NotifyNativeKeyboardStateChanged();
    }

    private void NotifyNativeKeyboardStateChanged()
    {
        OnPropertyChanged(nameof(ShowNativeKeyboard));
        OnPropertyChanged(nameof(NativeKeyboardButtonText));
        OnPropertyChanged(nameof(NativeKeyboardStatus));
        OnPropertyChanged(nameof(NativeKeyboardHasError));
        OnPropertyChanged(nameof(CanChangeNativeKeyboard));
        _toggleNativeKeyboardCommand?.NotifyCanExecuteChanged();
    }

    private sealed record NativeKeyboardTarget(DeviceControlSession Control,
        UsbTouchBridgeHost Bridge, bool Wireless);

    private bool OwnsNativeKeyboardTarget(NativeKeyboardTarget target) =>
        !_disposed && !target.Control.Stopping &&
        ReferenceEquals(FindControl(target.Control.DeviceUdid), target.Control) &&
        (target.Wireless
            ? target.Control.WirelessEnabled && ReferenceEquals(target.Control.WirelessBridge, target.Bridge)
            : target.Control.WiredEnabled && ReferenceEquals(target.Control.WiredBridge, target.Bridge));

    private bool IsNativeKeyboardTargetCurrent(NativeKeyboardTarget target) =>
        OwnsNativeKeyboardTarget(target) && !target.Control.Starting &&
        (target.Wireless ? target.Control.WirelessConnected : target.Control.WiredConnected);

    private async Task RetireFailedNativeKeyboardTargetAsync(NativeKeyboardTarget target)
    {
        // A recovering sender can retain the changed keyboard after this
        // transaction fails. Stop that owner before it returns with a mode
        // that no longer matches the saved preference. A replacement host
        // belongs to its own startup and must be left alone.
        if (!OwnsNativeKeyboardTarget(target)) return;
        try
        {
            if (target.Wireless) await DisableWirelessControlAsync(target.Control);
            else await DisableUsbControlAsync(target.Control);
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("native_keyboard_retire_failed",
                ("error", error.GetType().Name)));
        }
    }

    private bool NativeKeyboardAttemptNeedsRetirement(NativeKeyboardTarget target,
        long generation, bool previous) => OwnsNativeKeyboardTarget(target) &&
        (!IsNativeKeyboardTargetCurrent(target) || !target.Bridge.IsReady ||
            !target.Bridge.SupportsNativeKeyboardVisibility ||
            target.Bridge.InputGeneration != generation || target.Bridge.NativeKeyboardVisible != previous);

    internal async Task<bool> SetNativeKeyboardVisibleAsync(bool visible)
    {
        if (!CanChangeNativeKeyboard || Application.Current is not App app) return false;
        var previous = app.UpdateSettings.ShowNativeKeyboard;
        if (visible == previous) return true;
        var targets = _deviceControls.Values.SelectMany(control =>
        {
            var items = new List<NativeKeyboardTarget>();
            if (control.WiredEnabled && control.WiredConnected && control.WiredBridge is { } wired)
                items.Add(new(control, wired, false));
            if (control.WirelessEnabled && control.WirelessConnected && control.WirelessBridge is { } wireless)
                items.Add(new(control, wireless, true));
            return items;
        }).ToArray();
        var applied = new List<NativeKeyboardTarget>();
        NativeKeyboardTarget? attempting = null;
        long attemptingGeneration = 0;
        var uncertainAttempt = false;
        _nativeKeyboardChanging = true;
        _nativeKeyboardStatusKey = null;
        NotifyNativeKeyboardStateChanged();
        try
        {
            foreach (var target in targets)
            {
                if (!target.Bridge.SupportsNativeKeyboardVisibility ||
                    !IsNativeKeyboardTargetCurrent(target))
                {
                    _nativeKeyboardStatusKey = "NativeKeyboardApplyFailed";
                    return false;
                }
                attempting = target;
                attemptingGeneration = target.Bridge.InputGeneration;
                if (!await target.Bridge.SetNativeKeyboardVisibleAsync(visible,
                    () => IsNativeKeyboardTargetCurrent(target), _shutdownCancellation.Token))
                {
                    // Recovery can cancel a reply after the phone mutated.
                    // That target is not in applied yet. Retire its surviving
                    // owner when this attempt's outcome cannot be confirmed;
                    // keep an unchanged, ready peer that explicitly rejected.
                    if (NativeKeyboardAttemptNeedsRetirement(target, attemptingGeneration, previous))
                    {
                        uncertainAttempt = true;
                        await RetireFailedNativeKeyboardTargetAsync(target);
                    }
                    _nativeKeyboardStatusKey = "NativeKeyboardApplyFailed";
                    return false;
                }
                attempting = null;
                applied.Add(target);
            }
            // Earlier peers may recover or be replaced while a later reply
            // is pending. Their old acknowledgements do not prove that every
            // current route still has the requested keyboard mode.
            if (targets.Any(target => !IsNativeKeyboardTargetCurrent(target) ||
                !target.Bridge.IsReady || !target.Bridge.SupportsNativeKeyboardVisibility ||
                target.Bridge.NativeKeyboardVisible != visible))
            {
                _nativeKeyboardStatusKey = "NativeKeyboardApplyFailed";
                return false;
            }
            app.UpdateSettings.ShowNativeKeyboard = visible;
            if (!app.SaveUpdateSettings())
            {
                app.UpdateSettings.ShowNativeKeyboard = previous;
                _nativeKeyboardStatusKey = "NativeKeyboardSaveFailed";
                return false;
            }
            _nativeKeyboardStatusKey = targets.Length == 0
                ? "NativeKeyboardSaved" : "NativeKeyboardApplied";
            return true;
        }
        catch (Exception error)
        {
            if (attempting is { } target &&
                NativeKeyboardAttemptNeedsRetirement(target, attemptingGeneration, previous))
            {
                uncertainAttempt = true;
                await RetireFailedNativeKeyboardTargetAsync(target);
            }
            _nativeKeyboardStatusKey = "NativeKeyboardApplyFailed";
            AddDiagnosticLog(AppLog.Event("native_keyboard_setting_failed",
                ("error", error.GetType().Name)));
            return false;
        }
        finally
        {
            var rollbackSucceeded = !uncertainAttempt;
            if (app.UpdateSettings.ShowNativeKeyboard == previous)
                foreach (var target in applied)
                {
                    if (!IsNativeKeyboardTargetCurrent(target))
                    {
                        if (OwnsNativeKeyboardTarget(target))
                        {
                            rollbackSucceeded = false;
                            await RetireFailedNativeKeyboardTargetAsync(target);
                        }
                        continue;
                    }
                    try
                    {
                        var restored = await target.Bridge.SetNativeKeyboardVisibleAsync(previous,
                            () => IsNativeKeyboardTargetCurrent(target), _shutdownCancellation.Token);
                        rollbackSucceeded &= restored;
                        if (!restored) await RetireFailedNativeKeyboardTargetAsync(target);
                    }
                    catch (Exception error)
                    {
                        rollbackSucceeded = false;
                        AddDiagnosticLog(AppLog.Event("native_keyboard_rollback_failed",
                            ("error", error.GetType().Name)));
                        await RetireFailedNativeKeyboardTargetAsync(target);
                    }
                }
            if (!rollbackSucceeded) _nativeKeyboardStatusKey = "NativeKeyboardRollbackFailed";
            _nativeKeyboardChanging = false;
            NotifyNativeKeyboardStateChanged();
        }
    }
}
