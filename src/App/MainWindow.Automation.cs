using System.Windows;
using IPhoneMirror.App.Automation;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private AutomationApiHost? _automationHost;
    private AutomationService? _automationService;
    private readonly AutomationKeyStore _automationKeys = new();
    private readonly SemaphoreSlim _automationSettingsGate = new(1, 1);

    private void InitializeAutomation()
    {
        _viewModel.ConfigureAutomationInputAdmission();
        _viewModel.AutomationHumanInputBusy = physical =>
        {
            bool Matches(string? id) => id is not null && _viewModel.AutomationPhysicalId(id) == physical;
            if (_viewModel.AutomationBluetoothBusy(physical)) return true;
            if (Matches(ActiveInputDeviceUdid) && (_controlButtons != 0 || _controlKeyboardUsages.Count != 0 ||
                _controlModifierKeys.Count != 0 || !_keyboardHandoff.IsCompleted || _keyboardSends.Any(t => !t.IsCompleted) ||
                _systemShortcutGate.CurrentCount == 0)) return true;
            foreach (var device in _viewModel.Devices.Where(d => Matches(d.Udid)))
            {
                if (_mappingExecutor.IsBusyFor(device.Udid)) return true;
                if (_previewTouches.TryGetValue(device.Udid, out var touches) && touches.Count != 0) return true;
                if (_usbTouchStates.TryGetValue(device.Udid, out var pointer) &&
                    (pointer.Pressed || pointer.MoveDraining || pointer.PendingMoveQueued || pointer.WheelDraining || pointer.WheelActive)) return true;
            }
            return false;
        };
        Loaded += OnAutomationLoaded;
    }
    private async void OnAutomationLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnAutomationLoaded;
        if (Application.Current is not App { IsUiPreviewMode: false } app || !app.UpdateSettings.AutomationApi.Enabled) return;
        await ConfigureAutomationAsync(app.UpdateSettings.AutomationApi, save: false);
    }
    private void EnsureAutomationHost()
    {
        if (_automationHost is not null) return;
        _automationService = new(_viewModel, _viewModel.AutomationOwnership, _viewModel.DeviceInput);
        _automationHost = new(_automationService);
        _automationHost.StateChanged += () =>
        {
            if (Dispatcher.CheckAccess()) UpdateAutomationState();
            else Dispatcher.BeginInvoke(UpdateAutomationState);
        };
    }
    internal event Action? AutomationStateChanged;
    internal string AutomationStatusKey { get; private set; } = "AutomationStateStopped";
    internal AutomationSettings AutomationConfiguration => ((Application.Current as App)?.UpdateSettings.AutomationApi ?? new()).Clone();
    internal string AutomationAddress => $"http://127.0.0.1:{(_automationHost?.State == "Running" ? _automationHost.Port : AutomationConfiguration.Port)}";
    internal bool AutomationRunning => _automationHost?.State == "Running";
    private void UpdateAutomationState() => SetAutomationStatus("AutomationState" + (_automationHost?.State ?? "Stopped"));
    private void SetAutomationStatus(string key)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetAutomationStatus(key));
            return;
        }
        AutomationStatusKey = key;
        AutomationStateChanged?.Invoke();
    }
    internal Task<string> LoadAutomationKeyAsync() => Task.Run(_automationKeys.LoadOrCreate);
    internal async Task ConfigureAutomationAsync(AutomationSettings settings, bool save)
    {
        if (_shutdownStarted || !await _automationSettingsGate.WaitAsync(0)) return;
        var saveFailed = false;
        try
        {
            settings = settings.Clone();
            settings.Validate();
            EnsureAutomationHost();
            await _automationHost!.StopAsync();
            if (_shutdownStarted) return;
            if (settings.Enabled)
            {
                var key = await Task.Run(_automationKeys.LoadOrCreate);
                if (_shutdownStarted) return;
                await _automationHost.StartAsync(settings.Clone(), key);
            }
            if (save && Application.Current is App app)
            {
                var previous = app.UpdateSettings.AutomationApi;
                app.UpdateSettings.AutomationApi = settings;
                if (!app.SaveUpdateSettings())
                {
                    app.UpdateSettings.AutomationApi = previous;
                    saveFailed = true;
                    await _automationHost.StopAsync();
                    throw new InvalidOperationException("settings_save_failed");
                }
            }
            UpdateAutomationState();
        }
        catch (Exception error)
        {
            SetAutomationStatus(saveFailed ? "AutomationSaveFailed" : "AutomationStateFailed");
            DiagnosticLogger.Warning("AutomationAPI", "configure_failed", ("type", error.GetType().Name));
        }
        finally { _automationSettingsGate.Release(); }
    }
    internal async Task RegenerateAutomationKeyAsync()
    {
        if (_shutdownStarted || !await _automationSettingsGate.WaitAsync(0)) return;
        try
        {
            if (_automationHost is not null) await _automationHost.StopAsync();
            if (_shutdownStarted) return;
            var key = await Task.Run(_automationKeys.Regenerate);
            if (_shutdownStarted) return;
            if (Application.Current is App app && app.UpdateSettings.AutomationApi.Enabled)
            {
                EnsureAutomationHost();
                await _automationHost!.StartAsync(app.UpdateSettings.AutomationApi.Clone(), key);
            }
            UpdateAutomationState();
        }
        catch (Exception error)
        {
            SetAutomationStatus("AutomationStateFailed");
            DiagnosticLogger.Warning("AutomationAPI", "key_rotation_failed", ("type", error.GetType().Name));
        }
        finally { _automationSettingsGate.Release(); }
    }
    internal async Task ReclaimAutomationAsync()
    {
        try { if (_automationService is not null) await _automationService.RevokeAllAsync(); }
        catch { SetAutomationStatus("AutomationStateFailed"); }
    }
    private async Task ShutdownAutomationAsync()
    {
        try
        {
            if (_automationHost is not null)
            {
                var stop = _automationHost.DisposeAsync().AsTask();
                _ = stop.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                await stop.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception error)
        { DiagnosticLogger.Warning("AutomationAPI", "shutdown_failed", ("type", error.GetType().Name)); }
    }
}
