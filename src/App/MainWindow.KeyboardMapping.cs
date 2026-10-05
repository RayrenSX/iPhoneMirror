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
    private readonly KeyboardMappingHoldState _mappingHolds = new();
    private readonly KeyboardMappingExecutor _mappingExecutor = new();
    private KeyboardMappingFocusGuard? _mappingFocus;
    private KeyboardMappingWindow? _mappingWindow;
    private DispatcherTimer? _mappingTimer;
    private readonly KeyboardMappingCapture _mappingCapture = new();
    private bool _mappingClosing;
    private readonly Dictionary<Guid, object> _mappingQueued = [];
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
        _mappingTimer.Tick += (_, _) => { RefreshMappingStatus(); RefreshMappingOverlays(); RefreshMappingCursor(); };
        _mappingContinuous.Failed += error => Dispatcher.BeginInvoke(() =>
        {
            if (_mappingClosing) return;
            var status = error.Message.StartsWith("Mapping", StringComparison.Ordinal) ? error.Message : "MappingSendFailed";
            _viewModel.SetKeyboardMappingStatus(status);
            _mappingWindow?.SetRuntimeStatus(status);
            DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "continuous_failed", ("error", error.Message));
        });
        if (_mappingSettings.Enabled && TryEnterKeyboardMappingInputMode()) StartMappingMonitoring();
        RefreshMappingStatus();
    }

    private void StartMappingMonitoring()
    {
        _mappingFocus ??= new KeyboardMappingFocusGuard();
        _mappingTimer?.Start();
        ReconcileKeyboardHook();
        ReconcileMappingMouse();
    }

    private void ShowKeyboardMapping()
    {
        if (_mappingWindow is not null) { _mappingWindow.Activate(); return; }
        CancelMappedGesture();
        var window = new KeyboardMappingWindow(_mappingSettings, ApplyKeyboardMapping,
            key => KeyboardMappingKeys.Conflict(key, GetConfiguredShortcuts().Values),
            BeginMappingKeyCapture, EndMappingKeyCapture, () => MappingStatusKey(),
            () => _viewModel.SelectedDevice?.DisplayName, BeginMappingPositionPick, CancelMappingPositionPick,
            () => MappingSurfaces().LastOrDefault()) { Owner = this };
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
        if (next.Validate() is { } invalidSettings) return LocalizationService.Get(invalidSettings);
        foreach (var entry in next.Profiles.SelectMany(p => p.Mappings))
        {
            if (entry.Validate() is { } invalid) return LocalizationService.Get(invalid);
            if (MappingInputConflict(entry) is { } conflict)
                return LocalizationService.Get(conflict);
        }
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
            _mappingFocus?.Dispose();
            _mappingFocus = null;
            _mappingTimer?.Stop();
        }
        ReconcileKeyboardHook();
        ReconcileMappingMouse();
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
        var needed = !_mappingClosing && (Application.Current is not App { IsUiPreviewMode: true } ||
            _mappingSettings.Enabled || _mappingCapture.Waiting || _mappingCapture.HasHeldKeys);
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

    private void QueueMappedGesture(KeyboardMappingEntry mapping)
    {
        if (mapping.Action == MappedTouchAction.ReleasePointer) { ReleaseMappingPointer(); return; }
        if (_mappingQueued.Count < CoreDeviceTouchProtocol.MaxSlots && !_mappingQueued.ContainsKey(mapping.Id))
        {
            var ticket = new object();
            _mappingQueued.Add(mapping.Id, ticket);
            var release = mapping.Action == MappedTouchAction.HoldUntilRelease ? _mappingHolds.Begin(mapping) : null;
            // Let a quick release/repress enqueue a new lifetime. An older
            // callback must never remove the new press's queue reservation.
            var registration = release?.Token.Register(() =>
            {
                if (_mappingQueued.TryGetValue(mapping.Id, out var queued) && ReferenceEquals(queued, ticket))
                    _mappingQueued.Remove(mapping.Id);
            });
            var generation = _mappingGeneration;
            var foreground = _keyboardForegroundWindow();
            var focusGeneration = _mappingFocus?.Generation;
            Dispatcher.BeginInvoke(async () =>
            {
                if (_mappingQueued.TryGetValue(mapping.Id, out var queued) && ReferenceEquals(queued, ticket))
                    _mappingQueued.Remove(mapping.Id);
                try
                {
                    if (generation == _mappingGeneration && foreground == _keyboardForegroundWindow() &&
                        focusGeneration == _mappingFocus?.Generation)
                        await ExecuteMappedGestureAsync(mapping, release?.Token ?? default);
                }
                finally
                {
                    registration?.Dispose();
                    if (release is not null) _mappingHolds.Complete(mapping.Id, release);
                }
            }, DispatcherPriority.Input);
        }
    }

    private bool MappingFocusAllows()
    {
        if (_bossKeyHidden || _mappingWindow?.IsEditing == true || _mappingCapture.Waiting || _mappingPick is not null) return false;
        var foreground = _keyboardForegroundWindow();
        if (_viewModel.SelectedDevice is { } device && foreground != 0 &&
            foreground == _secondaryMirrors.GetWindowHandle(device.Udid)) return true;
        if (foreground == _windowSource?.Handle)
        {
            // Native HWND focus is authoritative. WPF logical focus can stay
            // on the settings button (or null) after clicking the D3D surface.
            if (_keyboardFocusedWindow() == MainPreviewHost.WindowHandle && MainPreviewHost.WindowHandle != 0) return true;
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

    private async Task ExecuteMappedGestureAsync(KeyboardMappingEntry entry, CancellationToken keyReleased = default)
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
        var foreground = _keyboardForegroundWindow();
        var focusGeneration = _mappingFocus?.Generation;
        var portrait = _viewModel.AppliedBluetoothPortraitMouseDirection;
        var landscape = _viewModel.AppliedBluetoothLandscapeMouseDirection;
        var reverseX = _viewModel.AppliedBluetoothMouseReverseHorizontal;
        var reverseY = _viewModel.AppliedBluetoothMouseReverseVertical;
        var route = _viewModel.CaptureMappingRoute(
            () => generation == _mappingGeneration && foreground == _keyboardForegroundWindow() &&
                focusGeneration == _mappingFocus?.Generation && MappingFocusAllows() && geometry == MappingGeometry(),
            (x, y) => entry.DeviceCoordinates ? (x, y) : BluetoothMouseOrientationMapper.MapNormalized(x, y, geometry.Width, geometry.Height,
                geometry.Rotation, portrait, landscape, reverseX, reverseY));
        if (route is null) return;
        LogMappingLimited("mapping_matched", entry, route.Target);
        try
        {
            var action = entry;
            var cycleIndex = _mappingCycles.GetValueOrDefault(entry.Id);
            if (entry.Action == MappedTouchAction.CycleTargets)
            {
                var points = new[] { new MappingPoint(entry.X, entry.Y) }.Concat(entry.Targets).ToArray();
                var point = points[cycleIndex % points.Length];
                action = entry with { Action = MappedTouchAction.Tap, X = point.X, Y = point.Y };
            }
            if (await _mappingExecutor.ExecuteAsync(action, route, keyReleased, replayCompletedHold: true))
                {
                if (entry.Action == MappedTouchAction.CycleTargets) _mappingCycles[entry.Id] = (cycleIndex + 1) % (entry.Targets.Length + 1);
                LogMappingLimited("action_sent", entry, route.Target);
                if (entry.ReleasePointerAfter) ReleaseMappingPointer();
            }
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
        ObserveKeyboardForeground();
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
        Interlocked.Increment(ref _mappingGeneration);
        _mappingContinuous.Cancel();
        _mappingCycles.Clear();
        ReleaseMappingCursor();
        _mappingHolds.Cancel();
        _mappingExecutor.Cancel();
        _keyboardRouter.ReleaseAllPressedKeys();
    }

    private void OnMappingContextChanged(string? property)
    {
        if (property is nameof(ViewModels.MainViewModel.SelectedDevice) or
            nameof(ViewModels.MainViewModel.CurrentSessionHandle) or
            nameof(ViewModels.MainViewModel.IsCapturing) or
            nameof(ViewModels.MainViewModel.IsVideoProtected) or
            nameof(ViewModels.MainViewModel.IsAudioOnlyAirPlay) or
            nameof(ViewModels.MainViewModel.SourceVideoWidth) or
            nameof(ViewModels.MainViewModel.SourceVideoHeight) or
            nameof(ViewModels.MainViewModel.UsbControlIsInputEnabled) or
            nameof(ViewModels.MainViewModel.BluetoothControlIsInputEnabled))
        {
            CancelMappedGesture();
            RetireInvalidPreviewTouches();
            if (property is nameof(ViewModels.MainViewModel.SourceVideoWidth) or
                nameof(ViewModels.MainViewModel.SourceVideoHeight) && _viewModel.SelectedDevice is { } device)
                _ = ResetPreviewTouchesAsync(device.Udid);
            RefreshMappingStatus();
            _mappingWindow?.SetRuntimeStatus(MappingStatusKey());
            RefreshMappingOverlays();
        }
    }

    private void DisposeKeyboardMapping()
    {
        _mappingClosing = true;
        ReconcileMappingMouse();
        CancelMappingPositionPick();
        RefreshMappingOverlays();
        CancelMappedGesture();
        _mappingFocus?.Dispose();
        _mappingTimer?.Stop();
        _viewModel.KeyboardMappingRequested -= ShowKeyboardMapping;
        ChangeKeyboardOwner(KeyboardInputMode.None);
        ReconcileKeyboardHook();
    }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
