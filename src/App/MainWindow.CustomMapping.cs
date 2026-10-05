using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly KeyboardMappingContinuousEngine _mappingContinuous = new();
    private readonly Dictionary<Guid, int> _mappingCycles = [];
    private readonly Dictionary<int, (KeyboardMappingEntry Entry, bool Suppress)> _mappingMousePresses = [];
    private nint _mappingMouseHook;
    private MouseMappingHook? _mappingMouseCallback;
    private bool _mappingCursorClipped;
    private Timer? _mappingCursorWatchdog;
    private bool _mappingEscapeHeld;
    private int _mappingWheelRemainder;

    private string? MappingInputConflict(KeyboardMappingEntry entry)
    {
        if (entry.Modifiers == 0)
            foreach (var key in entry.InputKeys)
                if (KeyboardMappingKeys.Conflict(key, GetConfiguredShortcuts().Values) is { } error) return error;
        uint vk = entry.InputKind == MappingInputKind.MouseButton ? entry.MouseButton switch
        { 2 => KeyboardShortcut.MouseRight, 4 => KeyboardShortcut.MouseMiddle, _ => 0 } : (uint)(entry.Key?.VirtualKey ?? 0);
        if (vk != 0 && GetConfiguredShortcuts().Values.Any(s => s.IsBound && s.VirtualKey == vk && s.Modifiers == entry.Modifiers))
            return "MappingShortcutConflict";
        if (entry.InputKind == MappingInputKind.Keyboard && entry.Modifiers == KeyboardShortcut.Control &&
            vk is 0x43 or 0x56 or 0x52 or 0x4C or 0x4D or 0x53 ||
            entry.Modifiers == (KeyboardShortcut.Control | KeyboardShortcut.Shift) && vk == 0x50)
            return "MappingShortcutConflict";
        return null;
    }

    private uint MappingOwnedModifiers() => _mappingSettings.Mappings.Where(m => m.Enabled && m.Modifiers == 0)
        .SelectMany(m => m.InputKeys).Aggregate(0u, (mask, k) => mask | KeyboardShortcutRecognizer.Modifier(k.VirtualKey));

    private void DispatchMappingInput(KeyboardMappingEntry mapping, MappedKey? key, bool down)
    {
        if (mapping.IsContinuous)
        {
            var keys = mapping.InputKeys.ToArray();
            var direction = mapping.Action == MappedTouchAction.Joystick ? Array.FindIndex(keys, k => k.SamePhysicalKey(key!)) : 0;
            if (direction < 0) return;
            _mappingContinuous.Input(mapping, direction, down, () => CaptureContinuousMappingRoute(mapping),
                i => GetAsyncKeyState(mapping.InputKind == MappingInputKind.MouseButton ? MappingMouseVirtualKey(mapping.MouseButton) :
                    keys[mapping.Action == MappedTouchAction.Joystick ? i : 0].VirtualKey) < 0);
            RefreshMappingCursor();
        }
        else if (down) QueueMappedGesture(mapping);
        else _mappingHolds.Release(mapping.Id);
    }

    private MappedTouchRoute? CaptureContinuousMappingRoute(KeyboardMappingEntry entry)
    {
        if (_keyboardRouter.Mode != KeyboardInputMode.Mapping || !_mappingSettings.Enabled || !MappingFocusAllows() ||
            _viewModel.GetMappingTargetStatus() != "MappingReady") return null;
        var generation = Interlocked.Read(ref _mappingGeneration);
        var foreground = _keyboardForegroundWindow();
        var focusGeneration = _mappingFocus?.Generation;
        var geometry = MappingGeometry();
        var portrait = _viewModel.AppliedBluetoothPortraitMouseDirection;
        var landscape = _viewModel.AppliedBluetoothLandscapeMouseDirection;
        var reverseX = _viewModel.AppliedBluetoothMouseReverseHorizontal;
        var reverseY = _viewModel.AppliedBluetoothMouseReverseVertical;
        (double X, double Y) Transform(double x, double y) => BluetoothMouseOrientationMapper.MapNormalized(x, y,
            geometry.Width, geometry.Height, geometry.Rotation, portrait, landscape, reverseX, reverseY);
        // Context changes increment the generation on the UI thread. The worker
        // reads only captured state/native focus; it never reads WPF controls.
        var route = _viewModel.CaptureMappingRoute(() => generation == Interlocked.Read(ref _mappingGeneration) &&
            foreground == _keyboardForegroundWindow() && focusGeneration == _mappingFocus?.Generation,
            (x, y) => entry.DeviceCoordinates ? (x, y) : Transform(x, y), requireAcknowledgement: true);
        return route is null ? null : route with { TransformOffset = (x, y) =>
            BluetoothMouseOrientationMapper.MapShortSideOffset(x, y, geometry.Width, geometry.Height,
                geometry.Rotation, portrait, landscape, reverseX, reverseY) };
    }

    private void ReconcileMappingMouse()
    {
        var enabled = !_mappingClosing && IsKeyboardMappingInputModeActive && _mappingSettings.Enabled &&
            Application.Current is not App { IsUiPreviewMode: true };
        if (enabled && _mappingMouseHook == 0)
        {
            _mappingMouseCallback ??= MappingMouseHookCallback;
            _mappingMouseHook = InstallMappingMouseHook(14, _mappingMouseCallback, GetModuleHandle(null), 0);
            if (_mappingMouseHook == 0) _mappingWindow?.SetRuntimeStatus("MappingHookFailed");
        }
        else if (!enabled && _mappingMouseHook != 0)
        {
            UnhookWindowsHookEx(_mappingMouseHook);
            _mappingMouseHook = 0;
            _mappingMousePresses.Clear();
        }
        RegisterRawInput(IsBluetoothControlActive && _activeControlWindow == 0, _rawKeyboardInputEnabled);
    }

    private nint MappingMouseHookCallback(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<MappingMouseData>(data);
            if ((input.Flags & 1) == 0 && RouteMappingMouse((int)message, input.MouseData)) return 1;
        }
        return CallNextHookEx(_mappingMouseHook, code, message, data);
    }

    private bool RouteMappingMouse(int message, uint data)
    {
        var button = message switch { 0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 4,
            0x20B or 0x20C => (data >> 16) == 1 ? 8 : 16, _ => 0 };
        var down = message is 0x201 or 0x204 or 0x207 or 0x20B;
        if (button != 0 && _mappingMousePresses.TryGetValue(button, out var held))
        {
            if (!down) { _mappingMousePresses.Remove(button); DispatchMappingInput(held.Entry, null, false); }
            return held.Suppress;
        }
        if ((!down && message != 0x20A) || !_mappingSettings.Enabled || !IsKeyboardMappingInputModeActive || !MappingFocusAllows()) return false;
        var kind = message == 0x20A ? unchecked((short)(data >> 16)) > 0 ? MappingInputKind.WheelUp : MappingInputKind.WheelDown : MappingInputKind.MouseButton;
        var modifiers = _keyboardRouter.PressedModifiers;
        if (_mappingSettings.Selected.ModifiersAsButtons) modifiers &= ~MappingOwnedModifiers();
        var mapping = _mappingSettings.Mappings.FirstOrDefault(m => m.Enabled && m.InputKind == kind && m.Modifiers == modifiers &&
            (kind != MappingInputKind.MouseButton || m.MouseButton == button));
        if (mapping is null || MappingInputConflict(mapping) is not null) return false;
        if (message == 0x20A)
        {
            var delta = unchecked((short)(data >> 16));
            if (Math.Sign(_mappingWheelRemainder) != Math.Sign(delta)) _mappingWheelRemainder = 0;
            _mappingWheelRemainder += delta;
            if (Math.Abs(_mappingWheelRemainder) >= 120)
            { _mappingWheelRemainder %= 120; QueueMappedGesture(mapping); }
        }
        else
        {
            _mappingMousePresses[button] = (mapping, _mappingSettings.SuppressOriginalKey);
            DispatchMappingInput(mapping, null, true);
        }
        return _mappingSettings.SuppressOriginalKey;
    }

    private void ReleaseMappingPointer()
    {
        var cycles = _mappingCycles.ToArray();
        CancelMappedGesture();
        foreach (var (id, index) in cycles) _mappingCycles[id] = index;
        ReleaseMappingCursor();
    }
    private void RefreshMappingCursor()
    {
        if (!_mappingContinuous.HasRelativeDrag || !MappingFocusAllows()) { ReleaseMappingCursor(); return; }
        if (_mappingCursorClipped) return;
        // Capture where activation occurred. Raw movement continues at the
        // desktop edge, and a fixed cursor cannot click outside that location.
        if (!GetMappingCursorPos(out var point)) return;
        var bounds = new NativeRect { Left = point.X, Top = point.Y, Right = point.X + 1, Bottom = point.Y + 1 };
        _mappingCursorClipped = ClipCursor(ref bounds);
        SetWindowsCursorHidden(true);
        var foreground = _keyboardForegroundWindow();
        _mappingCursorWatchdog ??= new Timer(_ =>
        {
            if (_mappingContinuous.HasRelativeDrag && foreground == _keyboardForegroundWindow()) return;
            ClipCursor(0);
            Dispatcher.BeginInvoke(RefreshMappingCursor);
        }, null, 40, 40);
    }
    private void ReleaseMappingCursor()
    {
        if (!_mappingCursorClipped) return;
        _mappingCursorWatchdog?.Dispose(); _mappingCursorWatchdog = null;
        ClipCursor(0);
        _mappingCursorClipped = false;
        SetWindowsCursorHidden(false);
    }
    private static int MappingMouseVirtualKey(int button) => button switch { 1 => 1, 2 => 2, 4 => 4, 8 => 5, 16 => 6, _ => 0 };
    [StructLayout(LayoutKind.Sequential)]
    private struct MappingCursorPoint { internal int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetMappingCursorPos(out MappingCursorPoint point);
    private delegate nint MouseMappingHook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MappingMouseData { internal MappingCursorPoint Point; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint InstallMappingMouseHook(int type, MouseMappingHook callback, nint module, uint thread);
}
