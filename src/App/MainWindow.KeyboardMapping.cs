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
    private Action<MappedKey>? _mappingCapture;
    private readonly HashSet<(uint Scan, bool Extended)> _mappingCaptureKeys = [];
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
        _mappingTimer.Tick += (_, _) => RefreshMappingStatus();
        if (_mappingSettings.Enabled) StartMappingMonitoring();
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
            () => _viewModel.SelectedDevice?.DisplayName) { Owner = this };
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
        // Preflight capture before committing settings so a failed hook install
        // leaves both the editor and persisted configuration at the old value.
        if (next.Enabled && _keyboardHook == 0)
        {
            _keyboardHook = SetWindowsHookEx(13, _keyboardHookProc, 0, 0);
            if (_keyboardHook == 0) return LocalizationService.Get("MappingHookFailed");
        }
        var previous = app.UpdateSettings.Clone();
        app.UpdateSettings.KeyboardMapping = next.Clone();
        app.UpdateSettings.KeyboardMapping.HadInvalidEntries = false;
        if (!app.SaveUpdateSettings())
        {
            app.RestoreUpdateSettings(previous);
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
            _mappingKeys.Reset();
            _mappingFocus?.Dispose();
            _mappingFocus = null;
            _mappingTimer?.Stop();
        }
        ReconcileKeyboardHook();
        if (wasEnabled != next.Enabled)
            DiagnosticLogger.ReverseControl("keyboard_mapping", next.Enabled ? "enabled" : "disabled");
        RefreshMappingStatus();
        return null;
    }

    private string? BeginMappingKeyCapture(Action<MappedKey> captured)
    {
        CancelMappedGesture();
        _mappingCapture = captured;
        ReconcileKeyboardHook();
        if (_keyboardHook != 0) return null;
        _mappingCapture = null;
        return LocalizationService.Get("MappingHookFailed");
    }

    private void EndMappingKeyCapture()
    {
        _mappingCapture = null;
        ReconcileKeyboardHook();
    }

    private void ReconcileKeyboardHook()
    {
        var needed = !_mappingClosing && (_systemKeySuppressionRequested || _mappingSettings.Enabled ||
            _mappingCapture is not null || _mappingCaptureKeys.Count != 0);
        if (needed && _keyboardHook == 0)
            _keyboardHook = SetWindowsHookEx(13, _keyboardHookProc, 0, 0);
        else if (!needed && _keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private bool ProcessMappingHook(LowLevelKeyboardData data, nint message)
    {
        if ((data.Flags & 0x12) != 0) return false; // injected/lower-integrity events
        var down = message is 0x100 or 0x104;
        if (!down && message is not (0x101 or 0x105)) return false;
        var key = new MappedKey((int)data.VirtualKey, (int)data.ScanCode, (data.Flags & 1) != 0);
        var id = (data.ScanCode, key.Extended);
        if (down && _mappingCaptureKeys.Contains(id)) return !key.IsWindows && !key.IsModifier;
        if (!down && _mappingCaptureKeys.Remove(id))
        {
            Dispatcher.BeginInvoke(ReconcileKeyboardHook);
            return !key.IsWindows && !key.IsModifier;
        }
        if (_mappingCapture is { } capture && _mappingWindow?.IsEditorActive == true)
        {
            if (down && _mappingCaptureKeys.Add(id))
            {
                _mappingCapture = null;
                Dispatcher.BeginInvoke(() => capture(key));
            }
            // System modifiers retain their native behavior even during capture.
            return !key.IsWindows && !key.IsModifier;
        }
        if (!_mappingSettings.Enabled) return false;
        var modifiers = AnyOtherModifierPressed(key);
        var allowed = MappingFocusAllows() && !_mappingClosing;
        if (key.VirtualKey == 0x1B && MappingIsFullScreen()) allowed = false;
        if (KeyboardMappingKeys.Conflict(key, _bluetoothShortcuts.Values) is not null) allowed = false;
        var canExecute = _viewModel.GetMappingTargetStatus() == "MappingReady";
        var result = _mappingKeys.Process(key, down, false, allowed, modifiers,
            canExecute && _mappingSettings.SuppressOriginalKey, _mappingSettings.Mappings);
        if (result.Mapping is { } mapping && !_mappingQueued && !_mappingExecutor.IsBusy)
        {
            _mappingQueued = true;
            var generation = _mappingGeneration;
            Dispatcher.BeginInvoke(async () =>
            {
                _mappingQueued = false;
                if (generation == _mappingGeneration) await ExecuteMappedGestureAsync(mapping);
            }, DispatcherPriority.Input);
        }
        return result.Suppress;
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
        if (_bossKeyHidden || _mappingWindow is not null || _mappingCapture is not null) return false;
        var foreground = GetForegroundWindow();
        // Every WPF subwindow (settings, prompts, text input) is a local pause.
        foreach (Window window in Application.Current.Windows)
            if (window != this && window.IsActive) return false;
        if (foreground == _windowSource?.Handle)
        {
            if (_isSettingsPanelVisible || Keyboard.FocusedElement is TextBoxBase or PasswordBox or
                    ComboBox or ButtonBase or Slider || Keyboard.FocusedElement is null) return false;
            return true;
        }
        if (_viewModel.SelectedDevice is { } device && foreground == _secondaryMirrors.GetWindowHandle(device.Udid))
            return true;
        return _mappingFocus?.Allows(foreground) == true;
    }

    private bool ShouldSkipMappedDeviceKey(int virtualKey, string? targetUdid) => _mappingSettings.Enabled &&
        Models.DeviceViewModel.UdidEquals(targetUdid, _viewModel.SelectedDevice?.Udid) &&
        _viewModel.GetMappingTargetStatus() == "MappingReady" && MappingFocusAllows() &&
        !AnyOtherModifierPressed(new(virtualKey, 0, false)) &&
        _mappingSettings.Mappings.Any(m => m.Enabled && !m.Key!.IsModifier &&
            (m.Key.VirtualKey == virtualKey || KeyboardMappingKeys.CurrentVirtualKey(m.Key) == virtualKey));

    private (uint Width, uint Height, int Rotation) MappingGeometry()
    {
        return _secondaryMirrors.TryGetControlGeometry(_viewModel.SelectedDevice?.Udid,
            out var width, out var height, out var rotation) ? (width, height, rotation) :
            (_viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight, 0);
    }

    private async Task ExecuteMappedGestureAsync(KeyboardMappingEntry entry)
    {
        if (!_mappingSettings.Enabled || !MappingFocusAllows()) return;
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
            (x, y) => BluetoothMouseOrientationMapper.MapNormalized(x, y, geometry.Width, geometry.Height,
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

    private string MappingStatusKey() => !_mappingSettings.Enabled
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
    }

    private void OnMappingContextChanged(string? property)
    {
        if (property is nameof(ViewModels.MainViewModel.SelectedDevice) or
            nameof(ViewModels.MainViewModel.CurrentSessionHandle) or
            nameof(ViewModels.MainViewModel.SourceVideoHeight) or
            nameof(ViewModels.MainViewModel.UsbControlIsInputEnabled) or
            nameof(ViewModels.MainViewModel.BluetoothControlIsInputEnabled))
        {
            CancelMappedGesture();
            RefreshMappingStatus();
            _mappingWindow?.SetRuntimeStatus(MappingStatusKey());
        }
    }

    private void DisposeKeyboardMapping()
    {
        _mappingClosing = true;
        CancelMappedGesture();
        _mappingFocus?.Dispose();
        _mappingTimer?.Stop();
        _viewModel.KeyboardMappingRequested -= ShowKeyboardMapping;
        ReconcileKeyboardHook();
    }
}
