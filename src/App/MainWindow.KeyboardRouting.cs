using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private nint _lastKeyboardEventForeground;
    private long _keyboardShortcutGeneration;
    private void ObserveKeyboardForeground()
    {
        var foreground = _keyboardForegroundWindow();
        var previous = _lastKeyboardEventForeground;
        _lastKeyboardEventForeground = foreground;
        if (previous != 0 && foreground != previous) ResetKeyboardOwnership();
    }

    private void ConfigureKeyboardShortcuts(IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> shortcuts)
    {
        ++_keyboardShortcutGeneration;
        _keyboardRouter.ReleaseAllPressedKeys();
        var bindings = new List<RoutedShortcut>();
        foreach (var (action, shortcut) in shortcuts)
        {
            if (action == BluetoothShortcutAction.ReverseControl) continue;
            bool Available() => !_mappingClosing && _mappingWindow?.IsEditing != true && _shortcutSettingsWindow is null && !_mappingCapture.Waiting &&
                (IsGlobalControlShortcut(action) || IsControlKeyboardForeground);
            bindings.Add(new(shortcut, action.ToString(), Available,
                () => QueueKeyboardShortcut(() => HandleConfiguredShortcut(action), Available)));
        }
        // User-configured actions keep precedence. Clipboard commands belong
        // to the same recognizer in both Direct and Mapping input modes.
        bindings.Add(new(new(KeyboardShortcut.Control, 0x43), "CopyFromDevice",
            IsDeviceClipboardShortcutAvailable, () => QueueDeviceClipboardShortcut(copy: true)));
        bindings.Add(new(new(KeyboardShortcut.Control, 0x56), "PasteToDevice",
            IsDeviceClipboardShortcutAvailable, () => QueueDeviceClipboardShortcut(copy: false)));
        void Local(int vk, uint modifiers, string name, Action execute, Func<bool>? condition = null, bool anyPreview = false)
        {
            bool Available() => !_mappingClosing && _mappingWindow?.IsEditing != true && !_bossKeyHidden && _shortcutSettingsWindow is null &&
                !_mappingCapture.Waiting && (IsLocalShortcutForeground ||
                    (anyPreview && _secondaryMirrors.ContainsWindow(_keyboardForegroundWindow()))) && condition?.Invoke() != false;
            bindings.Add(new(new(modifiers, (uint)vk), name, Available,
                () => QueueKeyboardShortcut(execute, Available)));
        }
        Local(0x7A, 0, "FullScreen", ToggleKeyboardFullScreen, anyPreview: true);
        Local(0x1B, 0, "ExitFullScreen", ToggleKeyboardFullScreen,
            () => _isFullScreen || _secondaryMirrors.IsFullScreenWindow(_keyboardForegroundWindow()), anyPreview: true);
        Local(0x74, 0, "RefreshDevices", () => _ = _viewModel.RefreshAsync(forceDeviceEnumeration: true));
        Local(0x52, KeyboardShortcut.Control, "RefreshPreview", RefreshPreview);
        Local(0x50, KeyboardShortcut.Control | KeyboardShortcut.Shift, "PreviewWindow",
            () => OnPreviewWindowClick(this, new RoutedEventArgs()));
        Local(0x4C, KeyboardShortcut.Control, "Diagnostics", () =>
        {
            if (Application.Current is App app) app.ShowAboutWindow(this, _viewModel, showDiagnostics: true);
        });
        Local(0x4D, KeyboardShortcut.Control, "Audio", () => _viewModel.PlayAudio = !_viewModel.PlayAudio);
        Local(0x53, KeyboardShortcut.Control, "Screenshot", () => _ = CaptureScreenshotAsync());
        _keyboardRouter.Shortcuts.Configure(bindings);
    }

    private bool IsLocalShortcutForeground =>
        (_keyboardForegroundWindow() == _windowSource?.Handle && !IsMainKeyboardEditorFocused()) ||
        (_secondaryMirrors.GetWindowHandle(_viewModel.SelectedDevice?.Udid) is var preview &&
            preview != 0 && _keyboardForegroundWindow() == preview);

    private void ToggleKeyboardFullScreen()
    {
        if (!_secondaryMirrors.ToggleFullScreenWindow(_keyboardForegroundWindow()))
            _ = ToggleActiveFullScreenAsync();
    }

    private void QueueKeyboardShortcut(Action execute, Func<bool> available)
    {
        var generation = _keyboardRouter.Generation;
        var shortcutGeneration = _keyboardShortcutGeneration;
        var target = ActiveInputDeviceUdid;
        var foreground = _keyboardForegroundWindow();
        var focusGeneration = _mappingFocus?.Generation;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_mappingClosing && generation == _keyboardRouter.Generation &&
                shortcutGeneration == _keyboardShortcutGeneration &&
                Models.DeviceViewModel.UdidEquals(target, ActiveInputDeviceUdid) &&
                foreground == _keyboardForegroundWindow() &&
                focusGeneration == _mappingFocus?.Generation && available()) execute();
        }, DispatcherPriority.Input);
    }

    private bool TryRoutePreviewKeyboardEvent(Key key, PreviewKeyboardKind kind)
    {
        if (!CanForwardControlKeyboard(ActiveInputDeviceUdid, _windowSource?.Handle ?? 0)) return false;
        if (_keyboardHook != 0 || _rawKeyboardInputEnabled) return true;
        HandleControlKeyboardInput(new(kind, KeyInterop.VirtualKeyFromKey(key)), ActiveInputDeviceUdid);
        return true;
    }

    private bool ProcessKeyboardHook(LowLevelKeyboardData data, nint message)
    {
        if ((data.Flags & 0x12) != 0) return false;
        var down = message is 0x100 or 0x104;
        if (!down && message is not (0x101 or 0x105)) return false;
        var scan = (int)data.ScanCode;
        var extended = (data.Flags & 1) != 0;
        var key = new MappedKey(ModifierKeyIdentity((int)data.VirtualKey, scan | (extended ? 0x100 : 0)), scan, extended);
        return RouteKeyboardEvent(key, down, ActiveInputDeviceUdid,
            _activeControlWindow != 0 ? _activeControlWindow : _windowSource?.Handle ?? 0);
    }

    private bool RouteKeyboardEvent(MappedKey key, bool down, string? target, nint window)
    {
        Dispatcher.VerifyAccess();
        ObserveKeyboardForeground();
        // Recording is the router's exclusive editor transaction, including
        // the release of a key held when the transaction was cancelled.
        if (_mappingCapture.Process(key, down, _mappingCapture.Waiting,
            action => Dispatcher.BeginInvoke(action, DispatcherPriority.Input))) return true;
        if (key.VirtualKey == 0x1B && (_mappingEscapeHeld || down && _mappingContinuous.HasRelativeDrag))
        { _mappingEscapeHeld = down; if (down) ReleaseMappingPointer(); return true; }
        var generation = _keyboardRouter.Generation;
        var sourceForeground = _keyboardForegroundWindow();
        var mappingAllowed = IsKeyboardMappingInputModeActive && _mappingSettings.Enabled && MappingFocusAllows();
        KeyboardPressRoute Resolve(MappedKey physical, bool chord)
        {
            if (_mappingClosing || generation != _keyboardRouter.Generation || sourceForeground != _keyboardForegroundWindow())
                return new(KeyboardEventOwner.Retired, true);
            if (mappingAllowed && _viewModel.GetMappingTargetStatus() == "MappingReady")
            {
                var modifiers = _keyboardRouter.PressedModifiers & ~KeyboardShortcutRecognizer.Modifier(physical.VirtualKey);
                if (_mappingSettings.Selected.ModifiersAsButtons) modifiers &= ~MappingOwnedModifiers();
                var mapping = _mappingSettings.Mappings.FirstOrDefault(m => m.Enabled && m.MatchesKey(physical, modifiers) &&
                    (!chord || modifiers != 0 || _mappingSettings.Selected.ModifiersAsButtons));
                if (mapping is not null)
                    return new(KeyboardEventOwner.Mapping, physical.IsWindows || _mappingSettings.SuppressOriginalKey,
                        pressed =>
                        {
                            DispatchMappingInput(mapping, physical, pressed);
                        });
            }
            var session = _mappingWindow?.IsEditing != true && IsDirectKeyboardInputModeActive && CanForwardControlKeyboard(target, window)
                ? _viewModel.CaptureDirectKeyboardRoute(target) : null;
            if (session is not null)
                return new(KeyboardEventOwner.Direct, true, pressed => DispatchControlKeyboardInput(
                    new(pressed ? PreviewKeyboardKind.Down : PreviewKeyboardKind.Up, physical.VirtualKey,
                        physical.ScanCode | (physical.Extended ? 0x100 : 0)), target, false, window, session));
            return new(KeyboardEventOwner.Windows, false);
        }
        var defer = mappingAllowed && !_mappingSettings.Selected.ModifiersAsButtons && (key.IsModifier || key.IsWindows) &&
            _mappingSettings.Mappings.Any(m => m.Enabled && m.InputKeys.Any(k => k.SamePhysicalKey(key)));
        return _keyboardRouter.RouteEvent(key, down, Resolve, ReplayKeyboardEvent, defer,
            bufferShortcutModifiers: !(mappingAllowed && _mappingSettings.Selected.ModifiersAsButtons) &&
                CanForwardControlKeyboard(target, window) && _keyboardRouter.Mode != KeyboardInputMode.None);
    }

    private static void ReplayKeyboardEvent(MappedKey key, bool down)
    {
        var input = new RouterNativeInput
        {
            Type = 1, VirtualKey = (ushort)key.VirtualKey, Scan = (ushort)key.ScanCode,
            Flags = (key.Extended ? 1u : 0) | (down ? 0u : 2u),
        };
        if (RouterSendInput(1, [input], Marshal.SizeOf<RouterNativeInput>()) != 1)
            DiagnosticLogger.ReverseControlWarning("keyboard_input", "replay_failed", ("error", Marshal.GetLastWin32Error()));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct RouterNativeInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort VirtualKey;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint RouterSendInput(uint count, RouterNativeInput[] inputs, int size);
}
