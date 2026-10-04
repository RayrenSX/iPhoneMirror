using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private KeyboardMappingSettings _mappingSettings = new();
    private readonly KeyboardMappingKeyState _mappingKeys = new();
    private readonly KeyboardMappingExecutor _mappingExecutor = new();
    private KeyboardMappingFocusGuard? _mappingFocus;
    private KeyboardMappingWindow? _mappingWindow;
    private DispatcherTimer? _mappingTimer;
    private readonly KeyboardMappingCapture _mappingCapture = new();
    private readonly KeyboardMappingWindowsKey _mappingWindowsKey = new();
    private bool _systemKeySuppressionRequested;
    private bool _mappingClosing;
    private bool _mappingQueued;
    private long _mappingGeneration;
    private string? _lastMappingStatus;
    private readonly Dictionary<string, long> _mappingLogTimes = [];

    private void InitializeKeyboardMapping()
    {
        if (Application.Current is App app) _mappingSettings = app.UpdateSettings.KeyboardMapping.Clone();
        _viewModel.KeyboardMappingRequested += ShowKeyboardMapping;
        // UI preview must not install a system hook or read another app's focus.
        if (Application.Current is App { IsUiPreviewMode: true }) _mappingSettings.Enabled = false;
        _mappingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _mappingTimer.Tick += (_, _) => { RefreshMappingStatus(); RefreshMappingOverlays(); };
        if (_mappingSettings.Enabled && TryEnterKeyboardMappingInputMode()) StartMappingMonitoring();
        RefreshMappingStatus();
    }

    private void StartMappingMonitoring()
    {
        _mappingFocus ??= new KeyboardMappingFocusGuard();
        _mappingTimer?.Start();
        ReconcileKeyboardHook();
    }

    private void ShowKeyboardMapping()
    {
        if (_mappingWindow is not null) { _mappingWindow.Activate(); return; }
        CancelMappedGesture();
        var window = new KeyboardMappingWindow(_mappingSettings, ApplyKeyboardMapping,
            key => KeyboardMappingKeys.Conflict(key, GetConfiguredShortcuts().Values),
            BeginMappingKeyCapture, EndMappingKeyCapture, () => MappingStatusKey(),
            () => _viewModel.SelectedDevice?.DisplayName, BeginMappingPositionPick, CancelMappingPositionPick) { Owner = this };
        _mappingWindow = window;
        window.Closed += (_, _) =>
        {
            EndMappingKeyCapture();
            _mappingWindow = null;
            RefreshMappingStatus();
        };
        window.Show();
        RefreshMappingStatus();
    }

    private string? ApplyKeyboardMapping(KeyboardMappingSettings next)
    {
        foreach (var entry in next.Mappings)
        {
            if (entry.Validate() is { } invalid) return LocalizationService.Get(invalid);
            if (KeyboardMappingKeys.Conflict(entry.Key!, GetConfiguredShortcuts().Values) is { } conflict)
                return LocalizationService.Get(conflict);
        }
        if (next.Mappings.Count > 256) return LocalizationService.Get("MappingLimit");
        for (var i = 0; i < next.Mappings.Count; ++i)
            for (var j = i + 1; j < next.Mappings.Count; ++j)
                if (next.Mappings[i].Id == next.Mappings[j].Id ||
                    next.Mappings[i].Key!.SamePhysicalKey(next.Mappings[j].Key!))
                    return LocalizationService.Get("MappingDuplicate");
        if (Application.Current is not App app) return LocalizationService.Get("MappingSaveFailed");
        var enteredMappingMode = false;
        if (next.Enabled && !IsKeyboardMappingInputModeActive)
        {
            if (!TryEnterKeyboardMappingInputMode())
                return LocalizationService.Get("MappingDirectKeyboardActive");
            enteredMappingMode = true;
        }
        // Preflight capture before committing settings so a failed hook install
        // leaves both the editor and persisted configuration at the old value.
        if (next.Enabled && _keyboardHook == 0)
        {
            _keyboardHook = InstallKeyboardHook();
            if (_keyboardHook == 0)
            {
                if (enteredMappingMode) LeaveKeyboardMappingInputMode();
                return LocalizationService.Get("MappingHookFailed");
            }
        }
        var previous = app.UpdateSettings.Clone();
        app.UpdateSettings.KeyboardMapping = next.Clone();
        app.UpdateSettings.KeyboardMapping.HadInvalidEntries = false;
        if (!app.SaveUpdateSettings())
        {
            app.RestoreUpdateSettings(previous);
            if (enteredMappingMode) LeaveKeyboardMappingInputMode();
            ReconcileKeyboardHook();
            return LocalizationService.Get("MappingSaveFailed");
        }
        CancelMappedGesture();
        var wasEnabled = _mappingSettings.Enabled;
        _mappingSettings = next.Clone();
        _mappingSettings.HadInvalidEntries = false;
        if (next.Enabled) StartMappingMonitoring();
        else
        {
            LeaveKeyboardMappingInputMode();
            _mappingKeys.Disable();
            _mappingFocus?.Dispose();
            _mappingFocus = null;
            _mappingTimer?.Stop();
        }
        ReconcileKeyboardHook();
        if (wasEnabled != next.Enabled)
            DiagnosticLogger.ReverseControl("keyboard_mapping", next.Enabled ? "enabled" : "disabled");
        RefreshMappingStatus();
        RefreshMappingOverlays();
        return null;
    }

    private string? BeginMappingKeyCapture(Action<MappedKey> captured)
    {
        if (!IsKeyboardMappingInputModeActive && !TryEnterKeyboardMappingInputMode())
            return LocalizationService.Get("MappingDirectKeyboardActive");
        CancelMappedGesture();
        _mappingCapture.Begin(captured);
        ReconcileKeyboardHook();
        if (_keyboardHook != 0) return null;
        _mappingCapture.Cancel();
        if (!_mappingSettings.Enabled) LeaveKeyboardMappingInputMode();
        return LocalizationService.Get("MappingHookFailed");
    }

    private void EndMappingKeyCapture()
    {
        _mappingCapture.Cancel();
        if (!_mappingSettings.Enabled) LeaveKeyboardMappingInputMode();
        ReconcileKeyboardHook();
    }

    private void ReconcileKeyboardHook()
    {
        var needed = !_mappingClosing && (_systemKeySuppressionRequested || IsKeyboardMappingInputModeActive ||
            _keyboardRouter.Mode != _keyboardRouter.RequestedMode ||
            _keyboardRouter.HasRetiredKeys || _mappingCapture.Waiting || _mappingCapture.HasHeldKeys || _mappingWindowsKey.HasHeldKey || _mappingKeys.HasSuppressedKeys);
        if (needed && _keyboardHook == 0)
            _keyboardHook = InstallKeyboardHook();
        else if (!needed && _keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private nint InstallKeyboardHook()
    {
        // A global WH_KEYBOARD_LL hook must identify the module that owns the
        // managed callback when dwThreadId is zero. Passing a null module can
        // return a non-zero handle on some Windows/.NET combinations while
        // silently failing to deliver physical keyboard events.
        var module = GetModuleHandle(null);
        var hook = SetWindowsHookEx(13, _keyboardHookProc, module, 0);
        if (hook == 0)
            DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "hook_install_failed",
                ("error", Marshal.GetLastWin32Error()));
        return hook;
    }

    private bool ProcessMappingHook(LowLevelKeyboardData data, nint message)
    {
        if ((data.Flags & 0x12) != 0) return false; // injected/lower-integrity events
        var down = message is 0x100 or 0x104;
        if (!down && message is not (0x101 or 0x105)) return false;
        var key = new MappedKey((int)data.VirtualKey, (int)data.ScanCode, (data.Flags & 1) != 0);
        var routed = _keyboardRouter.Route(KeyboardInputMode.Mapping,
            ModifierKeyIdentity(key.VirtualKey, key.ScanCode | (key.Extended ? 0x100 : 0)), down);
        // Capture is a global transaction. Once the editor starts waiting, it
        // must keep accepting the physical key even if WPF focus moves to a
        // preview or another window while the user completes the key press.
        if (_mappingCapture.Process(key, down, routed && _mappingCapture.Waiting,
                action => Dispatcher.BeginInvoke(action, DispatcherPriority.Input)))
        {
            Dispatcher.BeginInvoke(ReconcileKeyboardHook);
            return true;
        }
        var modifiers = AnyOtherModifierPressed(key);
        var allowed = routed && IsKeyboardMappingInputModeActive && _mappingSettings.Enabled &&
            MappingFocusAllows() && !_mappingClosing;
        if (key.VirtualKey == 0x1B && MappingIsFullScreen()) allowed = false;
        if (KeyboardMappingKeys.Conflict(key, _bluetoothShortcuts.Values) is not null) allowed = false;
        var canExecute = _viewModel.GetMappingTargetStatus() == "MappingReady";
        var windowsResult = _mappingWindowsKey.Process(key, down, allowed && canExecute, modifiers || _mappingKeys.HasHeldKeys,
            _mappingSettings.Mappings, KeyboardMappingWindowsKey.Replay);
        if (windowsResult.Mapping is { } windowsMapping) QueueMappedGesture(windowsMapping);
        if (!down) Dispatcher.BeginInvoke(ReconcileKeyboardHook);
        if (windowsResult.Suppress) return true;
        var result = _mappingKeys.Process(key, down, false, allowed, modifiers,
            canExecute && _mappingSettings.SuppressOriginalKey, _mappingSettings.Mappings);
        if (!IsKeyboardMappingInputModeActive || !_mappingSettings.Enabled) _mappingKeys.Disable();
        if (result.Mapping is { } mapping) QueueMappedGesture(mapping);
        return result.Suppress;
    }

    private void QueueMappedGesture(KeyboardMappingEntry mapping)
    {
        if (!_mappingQueued && !_mappingExecutor.IsBusy)
        {
            _mappingQueued = true;
            var generation = _mappingGeneration;
            Dispatcher.BeginInvoke(async () =>
            {
                _mappingQueued = false;
                if (generation == _mappingGeneration) await ExecuteMappedGestureAsync(mapping);
            }, DispatcherPriority.Input);
        }
    }

    private bool MappingIsFullScreen() => _isFullScreen ||
        (_viewModel.SelectedDevice is { } device && _secondaryMirrors.IsFullScreen(device.Udid));

    private static bool AnyOtherModifierPressed(MappedKey key)
    {
        foreach (var modifier in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if (modifier != key.VirtualKey && GetAsyncKeyState(modifier) < 0) return true;
        return false;
    }

    private bool MappingFocusAllows()
    {
        if (_bossKeyHidden || _mappingCapture.Waiting || _mappingPick is not null) return false;
        var foreground = GetForegroundWindow();
        if (_viewModel.SelectedDevice is { } device && foreground != 0 &&
            foreground == _secondaryMirrors.GetWindowHandle(device.Udid)) return true;
        if (foreground == _windowSource?.Handle)
        {
            // Native HWND focus is authoritative. WPF logical focus can stay
            // on the settings button (or null) after clicking the D3D surface.
            if (GetFocus() == MainPreviewHost.WindowHandle && MainPreviewHost.WindowHandle != 0) return true;
            if (_isSettingsPanelVisible || Keyboard.FocusedElement is TextBoxBase or PasswordBox or
                    ComboBox or ButtonBase or Slider || Keyboard.FocusedElement is null) return false;
            return true;
        }
        foreach (Window window in Application.Current.Windows)
            if (window != this && foreground == new WindowInteropHelper(window).Handle) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId) return false; // another device's native preview/menu
        return _mappingFocus?.Allows(foreground) == true;
    }

    private (uint Width, uint Height, int Rotation) MappingGeometry()
    {
        return _secondaryMirrors.TryGetControlGeometry(_viewModel.SelectedDevice?.Udid,
            out var width, out var height, out var rotation) ? (width, height, rotation) :
            (_viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight, 0);
    }

    private async Task ExecuteMappedGestureAsync(KeyboardMappingEntry entry)
    {
        if (_keyboardRouter.Mode != KeyboardInputMode.Mapping || !_mappingSettings.Enabled || !MappingFocusAllows()) return;
        LogMappingLimited("key_pressed", entry);
        var status = _viewModel.GetMappingTargetStatus();
        if (status != "MappingReady")
        {
            LogMappingLimited(status == "MappingNoDevice" ? "no_target_device" : "action_unavailable", entry);
            RefreshMappingStatus();
            return;
        }
        var geometry = MappingGeometry();
        var generation = _mappingGeneration;
        var portrait = _viewModel.AppliedBluetoothPortraitMouseDirection;
        var landscape = _viewModel.AppliedBluetoothLandscapeMouseDirection;
        var reverseX = _viewModel.AppliedBluetoothMouseReverseHorizontal;
        var reverseY = _viewModel.AppliedBluetoothMouseReverseVertical;
        var route = _viewModel.CaptureMappingRoute(
            () => generation == _mappingGeneration && MappingFocusAllows() && geometry == MappingGeometry(),
            (x, y) => entry.DeviceCoordinates ? (x, y) : BluetoothMouseOrientationMapper.MapNormalized(x, y, geometry.Width, geometry.Height,
                geometry.Rotation, portrait, landscape, reverseX, reverseY));
        if (route is null) return;
        LogMappingLimited("mapping_matched", entry, route.Target);
        try
        {
            if (await _mappingExecutor.ExecuteAsync(entry, route))
                LogMappingLimited("action_sent", entry, route.Target);
        }
        catch (OperationCanceledException) { LogMappingLimited("action_cancelled_or_disconnected", entry, route.Target); }
        catch (Exception error)
        {
            LogMappingLimited("action_failed", entry, route.Target, error.GetType().Name);
            _viewModel.SetKeyboardMappingStatus("MappingSendFailed");
            _mappingWindow?.SetRuntimeStatus("MappingSendFailed");
        }
    }

    private void LogMappingLimited(string name, KeyboardMappingEntry mapping, string? target = null, string? error = null)
    {
        var now = Stopwatch.GetTimestamp();
        if (_mappingLogTimes.TryGetValue(name, out var last) &&
            Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1)) return;
        _mappingLogTimes[name] = now;
        DiagnosticLogger.ReverseControl("keyboard_mapping", name, ("key", mapping.Key?.VirtualKey),
            ("scan_code", mapping.Key?.ScanCode), ("action", mapping.Action),
            ("target", AppLog.Device(target)), ("error", error));
    }

    private string MappingStatusKey() => _mappingCapture.Waiting ? "MappingWaiting"
        : _mappingPick is not null ? "MappingPicking"
        : !IsKeyboardMappingInputModeActive || !_mappingSettings.Enabled
        ? (_mappingSettings.HadInvalidEntries ? "MappingDamagedConfig" : "MappingOff")
        : _keyboardHook == 0 ? "MappingHookFailed"
        : _viewModel.GetMappingTargetStatus() is { } status && status != "MappingReady" ? status
        : !MappingFocusAllows() ? "MappingPaused" : "MappingReady";

    private void RefreshMappingStatus()
    {
        var key = MappingStatusKey();
        if (key != _lastMappingStatus)
        {
            if (key != "MappingReady") CancelMappedGesture();
            _lastMappingStatus = key;
            _viewModel.SetKeyboardMappingStatus(key);
            _mappingWindow?.SetRuntimeStatus(key);
        }
    }

    private void CancelMappedGesture()
    {
        ++_mappingGeneration;
        _mappingExecutor.Cancel();
        _mappingKeys.CancelCandidates();
        _mappingWindowsKey.Cancel();
    }

    private void OnMappingContextChanged(string? property)
    {
        if (property is nameof(ViewModels.MainViewModel.SelectedDevice) or
            nameof(ViewModels.MainViewModel.CurrentSessionHandle) or
            nameof(ViewModels.MainViewModel.IsCapturing) or
            nameof(ViewModels.MainViewModel.IsVideoProtected) or
            nameof(ViewModels.MainViewModel.IsAudioOnlyAirPlay) or
            nameof(ViewModels.MainViewModel.SourceVideoHeight) or
            nameof(ViewModels.MainViewModel.UsbControlIsInputEnabled) or
            nameof(ViewModels.MainViewModel.BluetoothControlIsInputEnabled))
        {
            CancelMappedGesture();
            RefreshMappingStatus();
            _mappingWindow?.SetRuntimeStatus(MappingStatusKey());
            RefreshMappingOverlays();
        }
    }

    private void DisposeKeyboardMapping()
    {
        _mappingClosing = true;
        CancelMappingPositionPick();
        RefreshMappingOverlays();
        CancelMappedGesture();
        _mappingFocus?.Dispose();
        _mappingTimer?.Stop();
        _viewModel.KeyboardMappingRequested -= ShowKeyboardMapping;
        LeaveKeyboardMappingInputMode();
        ReconcileKeyboardHook();
    }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
