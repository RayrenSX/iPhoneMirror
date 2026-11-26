using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Updater;
using System.Windows.Threading;

namespace IPhoneMirror.App.Services;

/// <summary>Owns one independent native preview window per device UDID.</summary>
internal sealed class MultiDevicePreviewManager : IDisposable
{
    private readonly MainViewModel viewModel;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<string, nint, bool> _isReverseControlEnabledForWindow;
    private readonly Func<nint, PreviewWindowsHotkeyPolicy?>? _windowsHotkeysFor;
    private readonly Dictionary<string, NativePreviewWindow> _windows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<(bool Success, string Message)>> _opening =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProtectedContentPresentation> _protectionStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sessionRecoveries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposing;
    private bool _disposed;
    private bool _updatingPhoneFrame;
    private int _bindingRefreshQueued;

    internal event Action<string, nint>? ReverseControlRequested;
    internal event Action<string, nint>? UsbControlRequested;
    internal event Action<string, nint>? WirelessControlRequested;
    internal event Action<string>? PreviewClosed;
    internal event Action<NativePreviewWindow>? PreviewCreated;
    internal event Action<string, PreviewPointerEventArgs>? PointerInput;
    internal event Action<string, PreviewKeyboardEventArgs>? KeyboardInput;
    internal event Action<string, NativePreviewWindow>? SelectScreenTextRequested;
    internal event Action<DeviceViewModel, nint, string[]>? FilesDropped;
    internal event Action<string, nint, bool>? KeyboardFocusChanged;

    internal MultiDevicePreviewManager(MainViewModel viewModel,
        Func<string, nint, bool>? isReverseControlEnabledForWindow = null,
        Func<nint, PreviewWindowsHotkeyPolicy?>? windowsHotkeysFor = null)
    {
        this.viewModel = viewModel;
        _windowsHotkeysFor = windowsHotkeysFor;
        _isReverseControlEnabledForWindow = isReverseControlEnabledForWindow ??
            ((udid, _) => viewModel.BluetoothControlIsInputEnabled &&
                viewModel.IsBluetoothControlTarget(udid));
        viewModel.DeviceSessionHandleChanged += OnDeviceSessionHandleChanged;
        viewModel.DeviceSessionRecoveryStateChanged += OnDeviceSessionRecoveryStateChanged;
        viewModel.DeviceProtectionStateChanged += OnDeviceProtectionStateChanged;
        viewModel.DeviceInventoryReconciled += RefreshOpenFrameAppearances;
        DeviceBindingManager.Shared.ProfilesChanged += QueueBindingFrameRefresh;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    internal bool IsOpen(DeviceViewModel? device) => device is not null &&
        _windows.ContainsKey(device.Udid);
    internal bool HasAnyOpen => _windows.Count != 0;
    internal event Action? PhoneFrameChanged;

    internal static IndependentWindowLayout? GetSavedLayout(string udid) =>
        (System.Windows.Application.Current as App)?.UpdateSettings.IndependentWindowLayouts
            .GetValueOrDefault(udid) is { IsValid: true, IsEmpty: false } layout ? layout : null;

    internal static IndependentWindowLayoutMode GetLayoutMode() =>
        (System.Windows.Application.Current as App)?.UpdateSettings.IndependentWindowStartupMode
            ?? IndependentWindowLayoutMode.Last;

    internal static IndependentWindowLayout? GetStartupLayout(string udid)
    {
        var layout = GetModeLayout(udid, GetLayoutMode());
        // Locking belongs to the actual window history, not a separate startup
        // override. A later manual unlock must survive closing and reopening.
        var fixedWindow = GetLayoutHistory(udid)?.LastLayout?.Fixed ?? layout?.Fixed ?? false;
        return layout is null ? fixedWindow ? new(Fixed: true) : null : layout with { Fixed = fixedWindow };
    }

    internal static IndependentWindowLayoutHistory? GetLayoutHistory(string udid) =>
        (System.Windows.Application.Current as App)?.UpdateSettings
            .IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(udid);

    internal static IndependentWindowLayout? GetModeLayout(string udid, IndependentWindowLayoutMode mode)
    {
        var history = GetLayoutHistory(udid);
        return mode switch
        {
            IndependentWindowLayoutMode.Last when history?.LastLayout is
                { IsValid: true, HasPosition: true, HasSize: true } last => last,
            IndependentWindowLayoutMode.Preset => GetSavedLayout(udid),
            IndependentWindowLayoutMode.Custom when history?.CustomLayout is
                { IsValid: true, IsEmpty: false } custom => custom,
            _ => null,
        };
    }

    internal IndependentWindowLayout? GetCurrentLayout(string udid) =>
        _windows.GetValueOrDefault(udid)?.GetCurrentLayout();

    internal static void TrackLayoutHistory(string udid, NativePreviewWindow window)
    {
        window.Closed += (_, _) =>
        {
            if (window.ClosedLayout is not { } layout || System.Windows.Application.Current is not App app) return;
            var historyByDevice = app.UpdateSettings.IndependentWindowLayoutHistoryByDevice;
            var history = historyByDevice.GetValueOrDefault(udid) ?? new();
            if (history.LastLayout == layout) return;
            historyByDevice[udid] = history with { LastLayout = layout };
            // Retain the latest snapshot in memory if a write fails. Opening
            // again in this process and subsequent settings saves can use it.
            _ = app.SaveUpdateSettings();
        };
    }

    internal string SelectedFrameColor => viewModel.SelectedDevice is { } device ? FrameAppearance(device).SelectedColorId ?? "" : "";
    internal bool CanSelectFrameColor => viewModel.SelectedDevice is { IsMediaCast: false } device &&
        DeviceFrameCatalog.Find(device.ProductType) is not null;

    internal DeviceAppearance FrameAppearance(DeviceViewModel device)
    {
        var colors = (System.Windows.Application.Current as App)?.UpdateSettings.DeviceFrameColors;
        var selected = colors?.FirstOrDefault(pair => DeviceViewModel.UdidEquals(pair.Key, device.Udid)).Value;
        var appearance = DeviceAppearanceCache.Shared.ForProjection(device, DeviceBindingManager.Shared);
        return appearance with { SelectedColorId = selected };
    }

    internal bool SetSelectedFrameColor(string color)
    {
        if (viewModel.SelectedDevice is not { } device || System.Windows.Application.Current is not App app) return false;
        if (color.Length != 0 && !(DeviceFrameCatalog.Find(device.ProductType)?.Colors ?? []).Any(c => c.Id == color)) return false;
        var previous = app.UpdateSettings.DeviceFrameColors ?? new(StringComparer.OrdinalIgnoreCase);
        var changed = UpdateSettings.CopyDeviceSettings(previous);
        if (color.Length == 0) changed.Remove(device.Udid); else changed[device.Udid] = color;
        var oldAppearance = FrameAppearance(device);
        var appearance = oldAppearance with { SelectedColorId = color };
        _windows.TryGetValue(device.Udid, out var window);
        if (window is not null && !window.UpdateDeviceAppearance(device.ProductType, appearance)) return false;
        app.UpdateSettings.DeviceFrameColors = changed;
        if (!app.SaveUpdateSettings())
        {
            app.UpdateSettings.DeviceFrameColors = previous;
            window?.UpdateDeviceAppearance(device.ProductType, oldAppearance);
            return false;
        }
        PhoneFrameChanged?.Invoke();
        return true;
    }

    internal (bool Enabled, bool Available, string Description) GetPhoneFrameState()
    {
        var device = viewModel.SelectedDevice;
        var description = LocalizationService.Get("ProjectionPhoneFrameHelp");
        if (device is not null)
            description += " " + DeviceFrameCatalog.Describe(DeviceFrameCatalog.Resolve(device.ProductType, FrameAppearance(device)));
        return (SavedPhoneFrameEnabled, true, description);
    }

    private static bool SavedPhoneFrameEnabled =>
        (System.Windows.Application.Current as App)?.UpdateSettings.PhoneFrameEnabled == true;

    private static void ApplySavedPhoneFramePreference(NativePreviewWindow window)
    {
        var enabled = SavedPhoneFrameEnabled;
        if (window.PhoneFramePreferred != enabled && (!enabled || window.CanShowPhoneFrame))
            _ = window.SetPhoneFrame(enabled);
        window.RefreshPhoneFramePreference();
    }

    private void RefreshOpenFrameAppearances()
    {
        foreach (var device in viewModel.Devices)
            if (_windows.TryGetValue(device.Udid, out var window))
            {
                _ = window.UpdateDeviceAppearance(device.ProductType, FrameAppearance(device));
                ApplySavedPhoneFramePreference(window);
            }
    }

    private void QueueBindingFrameRefresh()
    {
        // Binding commits may still hold their lock. Re-evaluate once on the
        // UI dispatcher using the complete, persisted set of profiles.
        if (Interlocked.Exchange(ref _bindingRefreshQueued, 1) != 0) return;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Exchange(ref _bindingRefreshQueued, 0);
            if (_disposed) return;
            RefreshOpenFrameAppearances();
            PhoneFrameChanged?.Invoke();
        });
    }

    internal Task<bool> SetPhoneFrameAsync(bool enabled) => Task.FromResult(SavePhoneFramePreference(enabled));

    private bool SavePhoneFramePreference(bool enabled)
    {
        if (_updatingPhoneFrame || System.Windows.Application.Current is not App app) return false;
        var previous = app.UpdateSettings.PhoneFrameEnabled;
        var changed = new List<(NativePreviewWindow Window, bool Enabled)>();
        var saved = false;
        _updatingPhoneFrame = true;
        try
        {
            foreach (var window in _windows.Values)
            {
                if (window.PhoneFramePreferred == enabled || (enabled && !window.CanShowPhoneFrame)) continue;
                changed.Add((window, window.PhoneFramePreferred));
                if (!window.SetPhoneFrame(enabled)) return false;
            }
            app.UpdateSettings.PhoneFrameEnabled = enabled;
            saved = app.SaveUpdateSettings();
            return saved;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("settings", "global_phone_frame_failed", error);
            return false;
        }
        finally
        {
            if (!saved)
            {
                app.UpdateSettings.PhoneFrameEnabled = previous;
                foreach (var (window, wasEnabled) in changed.AsEnumerable().Reverse())
                {
                    try { _ = window.SetPhoneFrame(wasEnabled); }
                    catch (Exception error) { DiagnosticLogger.Exception("settings", "phone_frame_rollback_failed", error); }
                }
            }
            _updatingPhoneFrame = false;
            foreach (var window in _windows.Values) window.RefreshPhoneFramePreference();
            PhoneFrameChanged?.Invoke();
        }
    }

    internal void ConfigurePhoneFrameSettings(ProjectionSettingsWindow settings)
    {
        settings.ConfigurePhoneFrame(GetPhoneFrameState, SetPhoneFrameAsync);
        settings.ConfigurePhoneFrameColors(() => DeviceFrameCatalog.ColorOptions(viewModel.SelectedDevice?.ProductType),
            () => SelectedFrameColor, SetSelectedFrameColor, () => CanSelectFrameColor);
        void Refresh() => settings.RefreshPhoneFrame();
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
        PhoneFrameChanged += Refresh;
        viewModel.PropertyChanged += OnPropertyChanged;
        settings.Closed += (_, _) =>
        {
            PhoneFrameChanged -= Refresh;
            viewModel.PropertyChanged -= OnPropertyChanged;
        };
    }

    internal void SetBluetoothWindowMouseEnabled(bool enabled)
    {
        foreach (var window in _windows.Values)
            window.BluetoothWindowMouseEnabled = enabled;
    }

    internal nint GetWindowHandle(string? udid) =>
        !string.IsNullOrWhiteSpace(udid) && _windows.TryGetValue(udid, out var window)
            ? window.Handle : 0;

    internal bool ContainsWindow(nint handle) => handle != 0 && _windows.Values.Any(w => w.Handle == handle);
    internal string? DeviceForWindow(nint handle) =>
        _windows.FirstOrDefault(pair => pair.Value.Handle == handle).Key;

    internal Action? BeginTemporarySetting(nint handle, SettingShortcutBinding binding) =>
        _windows.Values.FirstOrDefault(window => window.Handle == handle)?.BeginTemporarySetting(binding);

    internal NativePreviewWindow? FindWindow(nint handle) => handle == 0
        ? null : _windows.Values.FirstOrDefault(window => window.Handle == handle);

    internal bool AllowsControlInput(nint handle) =>
        _windows.Values.All(window => window.Handle != handle || window.AllowsControlInput);

    internal void PrepareBluetoothControlWindow(string? udid)
    {
        if (udid is not null && _windows.TryGetValue(udid, out var window))
            window.PrepareForBluetoothControl();
    }

    internal void ReleasePointerCapture(string? udid)
    {
        if (udid is not null && _windows.TryGetValue(udid, out var window))
            window.ReleasePointerCapture();
    }
    internal bool IsFullScreenWindow(nint handle) => _windows.Values.Any(w => w.Handle == handle && w.IsFullScreen);
    internal bool ToggleFullScreenWindow(nint handle)
    {
        var window = _windows.Values.FirstOrDefault(w => w.Handle == handle);
        if (window is null) return false;
        window.ToggleFullScreen();
        return true;
    }

    internal bool Activate(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid) ||
            !_windows.TryGetValue(udid, out var window)) return false;
        window.Activate();
        return true;
    }

    internal bool PrepareUsbControlWindow(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid) ||
            !_windows.TryGetValue(udid, out var window)) return false;
        window.PrepareForUsbControl();
        return true;
    }

    internal bool TryGetControlGeometry(string? udid, out uint width,
        out uint height, out int rotation)
    {
        width = height = 0;
        rotation = 0;
        if (string.IsNullOrWhiteSpace(udid) ||
            !_windows.TryGetValue(udid, out var window)) return false;
        (width, height, rotation) = window.ControlGeometry;
        return width != 0 && height != 0;
    }

    internal (DeviceFrameProfile? Frame, int Rotation) GetDeviceFrame(string udid) =>
        _windows.TryGetValue(udid, out var window)
            ? (window.ActiveDeviceFrame, window.DeviceFrameRotation) : (null, 0);

    internal bool IsFullScreen(string udid) =>
        _windows.TryGetValue(udid, out var window) && window.IsFullScreen;

    internal Task<(bool Success, string Message)> ShowAsync(DeviceViewModel device,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_show_requested",
            ("device", AppLog.Device(device.Udid)),
            ("kind", device.IsWireless ? "wireless" : "wired"),
            ("existing", _windows.ContainsKey(device.Udid)),
            ("disposing", _disposing)));
        if (_disposing)
            return Task.FromResult((false, LocalizationService.Get("CaptureStopped")));
        if (_windows.TryGetValue(device.Udid, out var existing))
        {
            var currentHandle = viewModel.GetDeviceSessionHandle(device.Udid);
            if (currentHandle != 0 && existing.SessionHandle == currentHandle)
            {
                _ = existing.UpdateDeviceAppearance(device.ProductType, FrameAppearance(device));
                ApplySavedPhoneFramePreference(existing);
                existing.Activate();
                viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_reused",
                    ("device", AppLog.Device(device.Udid)),
                    ("handle", AppLog.Handle(currentHandle))));
                return Task.FromResult((true, string.Empty));
            }
            _windows.Remove(device.Udid);
            PhoneFrameChanged?.Invoke();
            existing.Dispose();
        }
        // A joining request may stop waiting, but does not own another
        // caller's window/session creation and must not cancel it.
        if (_opening.TryGetValue(device.Udid, out var pending)) return pending.WaitAsync(cancellation);

        var opening = ShowCoreAsync(device, cancellation);
        _opening[device.Udid] = opening;
        return CompleteOpeningAsync(device.Udid, opening);
    }

    private async Task<(bool Success, string Message)> CompleteOpeningAsync(string udid,
        Task<(bool Success, string Message)> opening)
    {
        try
        {
            return await opening;
        }
        finally
        {
            if (_opening.TryGetValue(udid, out var current) && ReferenceEquals(current, opening))
                _opening.Remove(udid);
        }
    }

    private async Task<(bool Success, string Message)> ShowCoreAsync(DeviceViewModel device,
        CancellationToken cancellation)
    {
        var startupMonitor = NativePreviewWindow.CaptureStartupMonitor();
        var started = await viewModel.StartBackgroundSessionAsync(device, cancellation);
        if (cancellation.IsCancellationRequested)
        {
            if (started.Created)
                await viewModel.StopDeviceSessionAsync(device.Udid, started.Handle);
            cancellation.ThrowIfCancellationRequested();
        }
        if (!started.Success)
        {
            viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_session_failed",
                ("device", AppLog.Device(device.Udid)),
                ("message", started.Message)));
            return (false, started.Message);
        }
        if (_disposing)
        {
            try
            {
                if (started.Created)
                    await viewModel.StopDeviceSessionAsync(
                        device.Udid, started.Handle, preserveIfSelected: true);
            }
            catch (Exception error)
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
            return (false, LocalizationService.Get("CaptureStopped"));
        }
        var (width, height) = viewModel.GetIndependentSourceGeometry(device.Udid, started.Handle);
        var haveSourceGeometry = width != 0 && height != 0;
        if (!haveSourceGeometry)
        {
            var display = AppleDisplayCatalog.Find(device.ProductType);
            (width, height) = (display?.Width ?? 1206, display?.Height ?? 2622);
        }
        var profile = DeviceCornerProfileResolver.Resolve(device.ProductType, width, height);
        var cornerRadius = profile.IsRounded ? profile.RadiusRatio : 0;
        _ = Interop.NativeCore.SetDeviceCornerProfile(started.Handle,
            cornerRadius, profile.CurveExponent);
        if (!NativePreviewWindow.TryCreateAndShowForSession(started.Handle, width, height,
                $"iPhoneMirror — {device.DisplayName}", cornerRadius, profile.CurveExponent,
                () => viewModel.IsDeviceAudioEnabled(device.Udid),
                 () => viewModel.ActiveDeviceSessionCount,
                 enabled => LogAudioResult(viewModel.SetDeviceAudioEnabled(device.Udid, enabled)),
                 () => LogAudioResult(viewModel.MuteOtherDeviceSessions(device.Udid)),
                 ownerHwnd => viewModel.ShowImageSettings(device.Udid, ownerHwnd),
                 () => viewModel.ShowProjectionSettings(device.Udid),
                 out var window, viewModel.AddDiagnosticLog,
                 // A process can have Bluetooth reverse control enabled for a
                 // different preview. Only the active independent control
                 // window may capture input or hide the system cursor.
                 hwnd => _isReverseControlEnabledForWindow(device.Udid, hwnd),
                 args => PointerInput?.Invoke(device.Udid, args),
                 args => KeyboardInput?.Invoke(device.Udid, args),
                 hwnd => ReverseControlRequested?.Invoke(device.Udid, hwnd),
                 () => viewModel.IsUsbControlTarget(device.Udid),
                 hwnd => UsbControlRequested?.Invoke(device.Udid, hwnd),
                 hwnd => WirelessControlRequested?.Invoke(device.Udid, hwnd),
                 beforeShow: preview =>
                 {
                     preview.SetPreviewTools(() => SelectScreenTextRequested?.Invoke(device.Udid, preview),
                         paths => FilesDropped?.Invoke(device, preview.Handle, paths));
                     preview.WindowsHotkeysFor = _windowsHotkeysFor;
                     _ = preview.UpdateDeviceAppearance(device.ProductType, FrameAppearance(device));
                     ApplySavedPhoneFramePreference(preview);
                 }, layout: GetStartupLayout(device.Udid), startupMonitor: startupMonitor,
                 sourceDimensionsKnown: haveSourceGeometry) || window is null)
        {
            if (started.Created)
                await viewModel.StopDeviceSessionAsync(
                    device.Udid, started.Handle, preserveIfSelected: true);
            return (false, LocalizationService.Get("PreviewRendererAttachFailed"));
        }
        TrackLayoutHistory(device.Udid, window);
        window.PhoneFramePreferenceRequested = SavePhoneFramePreference;
        window.PhoneFramePreference = () => SavedPhoneFrameEnabled;
        window.PhoneFrameChanged += () => { if (!_updatingPhoneFrame) PhoneFrameChanged?.Invoke(); };
        window.BluetoothWindowMouseEnabled = viewModel.BluetoothWindowMouseEnabled;
        _windows[device.Udid] = window;
        PreviewCreated?.Invoke(window);
        window.ActivationChanged += active =>
            KeyboardFocusChanged?.Invoke(device.Udid, window.Handle, active);
        KeyboardFocusChanged?.Invoke(device.Udid, window.Handle, true);
        if (_protectionStates.TryGetValue(device.Udid, out var protection))
            window.SetProtectedContent(protection.IsProtected,
                protection.AudioDisplay);
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_opened",
            ("device", AppLog.Device(device.Udid)),
            ("handle", AppLog.Handle(started.Handle)),
            ("created_session", started.Created)));
        window.Closed += async (_, _) =>
        {
            if (!_windows.TryGetValue(device.Udid, out var tracked) ||
                !ReferenceEquals(tracked, window)) return;
            _windows.Remove(device.Udid);
            PhoneFrameChanged?.Invoke();
            PreviewClosed?.Invoke(device.Udid);
            var closingHandle = window.SessionHandle;
            viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_closed",
                ("device", AppLog.Device(device.Udid)),
                ("handle", AppLog.Handle(closingHandle)),
                ("created_session", started.Created),
                ("disposing", _disposing)));
            if (_disposing || (!started.Created && !viewModel.IsTrayApplicationMode)) return;
            try
            {
                // The selected-main check is performed under the same core
                // gate that revokes the handle, closing the selection race.
                await viewModel.StopDeviceSessionAsync(
                    device.Udid, closingHandle, preserveIfSelected: true,
                    pauseWireless: viewModel.IsTrayApplicationMode);
            }
            catch (Exception error)
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
        };
        return (true, string.Empty);
    }

    private void LogAudioResult((bool Success, string Message) result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message)) viewModel.AddUiLog(result.Message);
    }

    private void OnDeviceSessionHandleChanged(string udid, ulong handle)
    {
        _protectionStates.Remove(udid);
        if (!_windows.TryGetValue(udid, out var window))
            return;

        if (handle == 0 && _sessionRecoveries.Contains(udid))
        {
            viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_preview_recovery_pending",
                ("device", AppLog.Device(udid)),
                ("old_handle", AppLog.Handle(window.SessionHandle))));
            return;
        }

        if (handle != 0 && _sessionRecoveries.Contains(udid))
        {
            if (window.RebindSession(handle))
            {
                ApplySavedPhoneFramePreference(window);
                return;
            }
        }
        else if (window.SessionHandle == handle) return;

        _windows.Remove(udid);
        PhoneFrameChanged?.Invoke();
        PreviewClosed?.Invoke(udid);
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_handle_invalidated",
            ("device", AppLog.Device(udid)),
            ("old_handle", AppLog.Handle(window.SessionHandle)),
            ("new_handle", AppLog.Handle(handle))));
        try { window.Dispose(); }
        catch (Exception error)
        {
            try
            {
                viewModel.AddUiLog(LocalizationService.Format(
                    "StopFailedFormat", error.Message));
            }
            catch (Exception uiError)
            {
                DiagnosticLogger.Exception("window", "preview_dispose_ui_failed",
                    uiError, ("device", AppLog.Device(udid)));
            }
        }
    }

    private void OnDeviceSessionRecoveryStateChanged(string udid, bool recovering)
    {
        if (recovering)
        {
            _sessionRecoveries.Add(udid);
            return;
        }

        _sessionRecoveries.Remove(udid);
        if (viewModel.GetDeviceSessionHandle(udid) != 0 ||
            !_windows.TryGetValue(udid, out var window)) return;

        _windows.Remove(udid);
        PhoneFrameChanged?.Invoke();
        PreviewClosed?.Invoke(udid);
        viewModel.AddDiagnosticLog(AppLog.Event(
            "independent_preview_recovery_failed",
            ("device", AppLog.Device(udid)),
            ("old_handle", AppLog.Handle(window.SessionHandle))));
        try { window.Dispose(); }
        catch (Exception error)
        {
            viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_preview_dispose_failed",
                ("handle", AppLog.Handle(window.SessionHandle)),
                ("error", AppLog.Error(error))));
        }
    }

    private void OnDeviceProtectionStateChanged(string udid,
        ProtectedContentPresentation presentation)
    {
        if (presentation.IsProtected) _protectionStates[udid] = presentation;
        else _protectionStates.Remove(udid);
        try
        {
            if (_windows.TryGetValue(udid, out var window))
                window.SetProtectedContent(presentation.IsProtected,
                    presentation.AudioDisplay);
        }
        catch (Exception error)
        {
            viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_preview_protection_failed",
                ("device", AppLog.Device(udid)),
                ("protected", presentation.IsProtected),
                ("error", AppLog.Error(error))));
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var window in _windows.Values) window.RefreshPhoneFrameDescription();
        PhoneFrameChanged?.Invoke();
        foreach (var (udid, presentation) in _protectionStates.ToArray())
            try
            {
                if (_windows.TryGetValue(udid, out var window))
                    window.SetProtectedContent(true, presentation.AudioDisplay);
            }
            catch (Exception error)
            {
                viewModel.AddDiagnosticLog(AppLog.Event(
                    "independent_preview_protection_language_failed",
                    ("device", AppLog.Device(udid)),
                    ("error", AppLog.Error(error))));
            }
    }

    internal void UpdateDevice(string udid, uint width, uint height)
    {
        if (_windows.TryGetValue(udid, out var window) && width != 0 && height != 0)
            window.SetSourceDimensions(width, height);
    }

    internal bool Refresh(DeviceViewModel? device) => device is not null &&
        _windows.TryGetValue(device.Udid, out var window) && window.RefreshPreview();

    internal async Task<bool> ToggleFullScreenAsync(DeviceViewModel device)
    {
        var result = await ShowAsync(device);
        if (!result.Success || !_windows.TryGetValue(device.Udid, out var window)) return false;
        window.ToggleFullScreen();
        return true;
    }

    internal void UpdateDevice(DeviceViewModel? device, uint width, uint height)
    {
        if (device is null) return;
        if (_windows.TryGetValue(device.Udid, out var appearanceWindow))
        {
            _ = appearanceWindow.UpdateDeviceAppearance(device.ProductType, FrameAppearance(device));
            ApplySavedPhoneFramePreference(appearanceWindow);
        }
        UpdateDevice(device.Udid, width, height);
        // The detached window refreshes its base model/panel profile with
        // these notifications and keeps its per-window "remove corners"
        // choice independent of that profile.
    }

    internal void HideForShutdown()
    {
        if (_disposed) return;
        _disposing = true;
        foreach (var window in _windows.Values.ToArray())
        {
            try { window.HideForShutdown(); }
            catch (Exception error)
            {
                viewModel.AddDiagnosticLog(AppLog.Event(
                    "independent_preview_hide_failed",
                    ("handle", AppLog.Handle(window.SessionHandle)),
                    ("error", AppLog.Error(error))));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposing = true;
        viewModel.AddDiagnosticLog(AppLog.Event("independent_preview_manager_dispose",
            ("count", _windows.Count)));
        viewModel.DeviceSessionHandleChanged -= OnDeviceSessionHandleChanged;
        viewModel.DeviceSessionRecoveryStateChanged -= OnDeviceSessionRecoveryStateChanged;
        viewModel.DeviceProtectionStateChanged -= OnDeviceProtectionStateChanged;
        viewModel.DeviceInventoryReconciled -= RefreshOpenFrameAppearances;
        DeviceBindingManager.Shared.ProfilesChanged -= QueueBindingFrameRefresh;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        foreach (var window in _windows.Values.ToArray())
        {
            try { window.Dispose(); }
            catch (Exception error)
            {
                viewModel.AddDiagnosticLog(AppLog.Event(
                    "independent_preview_dispose_failed",
                    ("handle", AppLog.Handle(window.SessionHandle)),
                    ("error", AppLog.Error(error))));
            }
        }
        _windows.Clear();
        _sessionRecoveries.Clear();
        _protectionStates.Clear();
    }
}
