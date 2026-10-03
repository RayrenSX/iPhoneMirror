using System.Runtime.InteropServices;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    // Invalidated on the dispatcher; read by transport workers as well. Reset
    // before waiting for the route lock so queued input cannot survive refocus.
    private long _keyboardInputGeneration;
    private readonly Func<nint> _keyboardForegroundWindow = GetForegroundWindow;
    private string? _keyboardStateUdid;
    private string? ActiveInputDeviceUdid => _activeControlWindow != 0
        ? _activeControlUdid : _viewModel.SelectedDevice?.Udid;

    private void FocusControlDevice(string? udid, nint window)
    {
        var previous = _activeControlUdid ?? _keyboardStateUdid;
        if (_activeControlWindow != window || !DeviceViewModel.UdidEquals(previous, udid))
        {
            if (previous is not null)
            {
                HandleControlKeyboardInput(new PreviewKeyboardEventArgs(PreviewKeyboardKind.Reset, 0), previous);
                _ = HandleUsbPointerInputAsync(new PreviewPointerEventArgs(PreviewPointerKind.Reset, 0, 0, 0, 0), previous);
                if (_viewModel.IsBluetoothControlTarget(previous))
                    _ = _viewModel.SendBluetoothMouseAsync(0, 0, expectedTargetDeviceUdid: previous);
            }
            ResetControlRouteState();
            _keyboardStateUdid = udid;
        }
        _activeControlUdid = udid;
        _activeControlWindow = window;
        _viewModel.SetControlInputDevice(udid);
        ApplyBluetoothControlInputState(activateIndependentWindow: false);
    }

    private Func<bool> CaptureKeyboardSendGuard(nint sourceWindow)
    {
        var generation = Volatile.Read(ref _keyboardInputGeneration);
        var foregroundWindow = _keyboardForegroundWindow;
        // No WPF properties or device dictionaries may be read on the BLE
        // worker. Route/focus resets invalidate this immutable snapshot.
        return () => generation == Volatile.Read(ref _keyboardInputGeneration) &&
            sourceWindow != 0 && sourceWindow == foregroundWindow();
    }

    private nint GetControlKeyboardWindow(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return 0;
        var independentWindow = _secondaryMirrors.GetWindowHandle(udid);
        if (independentWindow != 0) return independentWindow;
        if (_activeControlWindow != 0)
            return DeviceViewModel.UdidEquals(_activeControlUdid, udid)
                ? _activeControlWindow : 0;
        return DeviceViewModel.UdidEquals(_viewModel.SelectedDevice?.Udid, udid)
            ? _windowSource?.Handle ?? 0 : 0;
    }

    private bool CanForwardControlKeyboard(string? udid, nint sourceWindow)
    {
        // Compare the actual top-level HWND, not process ownership or WPF
        // IsActive: native previews have their own HWNDs, and settings dialogs
        // in this same process must not feed the phone's keyboard.
        return !_bossKeyHidden && sourceWindow != 0 &&
            sourceWindow == GetControlKeyboardWindow(udid) &&
            sourceWindow == _keyboardForegroundWindow();
    }

    private bool IsControlKeyboardForeground
    {
        get
        {
            var udid = _activeControlWindow != 0 ? _activeControlUdid :
                _viewModel.SelectedDevice?.Udid;
            return CanForwardControlKeyboard(udid, GetControlKeyboardWindow(udid));
        }
    }

    private void OnMainKeyboardDeactivated(object? sender, EventArgs e)
    {
        _shortcutKeysDown.Clear();
        _ordinaryKeysDown.Clear();
        _mouseShortcutButtons.Clear();
        _viewModel.SetControlInputDevice(null);
        UnregisterDeviceHotkeys();
        _localFullScreenEscapeDown = false;
        _localFullScreenF11Down = false;
        if (_activeControlWindow == 0)
        {
            HandleControlKeyboardInput(new PreviewKeyboardEventArgs(
                PreviewKeyboardKind.Reset, 0), _viewModel.SelectedDevice?.Udid);
            _ = HandleUsbPointerInputAsync(new PreviewPointerEventArgs(
                PreviewPointerKind.Reset, 0, 0, 0, 0), _viewModel.SelectedDevice?.Udid);
        }
    }

    private void OnMainKeyboardActivated(object? sender, EventArgs e) =>
        FocusControlDevice(_viewModel.SelectedDevice?.Udid, 0);

    private void OnIndependentKeyboardFocusChanged(string udid, nint hwnd, bool active)
    {
        if (!active)
        {
            _shortcutKeysDown.Clear();
            _ordinaryKeysDown.Clear();
            _mouseShortcutButtons.Clear();
            _viewModel.SetControlInputDevice(null);
            UnregisterDeviceHotkeys();
            HandleControlKeyboardInput(new PreviewKeyboardEventArgs(
                PreviewKeyboardKind.Reset, 0), udid);
            _ = HandleUsbPointerInputAsync(new PreviewPointerEventArgs(
                PreviewPointerKind.Reset, 0, 0, 0, 0), udid);
        }
        else FocusControlDevice(udid, hwnd);
    }

    private static bool IsGlobalControlShortcut(Services.BluetoothShortcutAction action) =>
        action is Services.BluetoothShortcutAction.BossKey or
            Services.BluetoothShortcutAction.BluetoothControl or
            Services.BluetoothShortcutAction.WirelessControl or
            Services.BluetoothShortcutAction.WiredControl;

    private readonly HashSet<int> _failedDeviceHotKeyIds = [];

    private bool ShouldRegisterDeviceHotkeys => _shortcutSettingsWindow is null && IsControlKeyboardForeground &&
        (IsBluetoothControlActive || _viewModel.IsUsbControlTarget(ActiveInputDeviceUdid));

    private void UnregisterDeviceHotkeys()
    {
        if (_windowSource is null) return;
        foreach (var action in Enum.GetValues<Services.BluetoothShortcutAction>())
        {
            if (IsGlobalControlShortcut(action)) continue;
            var id = HotKeyId(action);
            if (_registeredHotKeyIds.Remove(id))
                UnregisterHotKey(_windowSource.Handle, id);
        }
        _hotKeyRegistered = _registeredHotKeyIds.Count != 0;
    }

    private void RefreshDeviceHotkeys()
    {
        if (_windowSource is null) return;
        if (!ShouldRegisterDeviceHotkeys)
        {
            UnregisterDeviceHotkeys();
            return;
        }
        foreach (var (action, shortcut) in GetConfiguredShortcuts())
        {
            if (IsGlobalControlShortcut(action) || !shortcut.IsBound ||
                shortcut.VirtualKey is Services.KeyboardShortcut.MouseRight or
                    Services.KeyboardShortcut.MouseMiddle) continue;
            var id = HotKeyId(action);
            if (_registeredHotKeyIds.Contains(id)) continue;
            if (RegisterHotKey(_windowSource.Handle, id,
                    shortcut.RegistrationModifiers, shortcut.VirtualKey))
            {
                _registeredHotKeyIds.Add(id);
                _failedDeviceHotKeyIds.Remove(id);
            }
            else if (_failedDeviceHotKeyIds.Add(id))
            {
                _viewModel.AddUiLog(Localization.LocalizationService.Format(
                    "ShortcutRegistrationFailedFormat", shortcut.DisplayText));
            }
        }
        _hotKeyRegistered = _registeredHotKeyIds.Count != 0;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
