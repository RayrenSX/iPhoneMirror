using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

internal sealed class ResolutionPreset(string resourceKey, uint width, uint height) : INotifyPropertyChanged
{
    public uint Width { get; } = width;
    public uint Height { get; } = height;
    public string Label => LocalizationService.Get(resourceKey);
    public override string ToString() => Label;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class UsbProjectionModeOption(UsbProjectionMode mode, string labelResourceKey,
    string advantageResourceKey, string disadvantageResourceKey,
    string noticeResourceKey) : INotifyPropertyChanged
{
    public UsbProjectionMode Mode { get; } = mode;
    public string Label => LocalizationService.Get(labelResourceKey);
    public string Advantage => LocalizationService.Get(advantageResourceKey);
    public string Disadvantage => LocalizationService.Get(disadvantageResourceKey);
    public string Notice => LocalizationService.Get(noticeResourceKey);
    internal void NotifyLanguageChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Advantage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Disadvantage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notice)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class MainViewModel : INotifyPropertyChanged
{
    internal event Action<string, uint, uint>? DeviceVideoSizeChanged;
    internal event Action<MediaCastRequest>? MediaCastCommandReceived;
    private readonly NativeCore _core;
    private readonly IPhoneFilterDriverService _filterDriver = new();
    private readonly DriverManagerLauncher _driverManager = new();
    private readonly WirelessReceiverController _wireless;
    private readonly MediaCastReceiverController _mediaCast;
    // Serializes every native-core operation that can race USB teardown,
    // device enumeration, restart, or application shutdown.
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    private readonly NativeLogTailReader _logReader = new();
    private readonly CaptureShutdownCoordinator _shutdownCoordinator = new();
    private readonly DeviceSessionManager _sessions;
    private IReadOnlyList<NativeDeviceInfo> _lastUsbDevices = [];
    private bool _disposed;
    private DeviceViewModel? _selectedDevice;
    private string _environmentStatus = string.Empty;
    private string _captureStatus = string.Empty;
    private string _driverState = string.Empty;
    private bool _isCapturing;
    private bool _isBusy;
    private string? _activeCaptureUdid;
    private int _manualRefreshPending;
    private string _resolution = "—";
    private uint _sourceVideoWidth;
    private uint _sourceVideoHeight;
    private string _fpsDisplay = "— fps";
    private string _latencyDisplay = "— ms";
    private string _audioDisplay = string.Empty;
    private ResolutionPreset _selectedResolutionPreset = null!;
    private int _selectedFrameRate = 60;
    private double _playbackVolume = 100;
    private bool _playAudio = true;
    private bool _advancedMode;
    private string _settingsStatus = string.Empty;
    private string? _settingsStatusKey = "StatusDefaultSettings";
    private object?[] _settingsStatusArguments = [];
    private string _logText = string.Empty;
    private string _selectedLanguage = LocalizationService.SystemLanguage;
    private NativeEnvironmentInfo? _lastEnvironment;
    private NativeCaptureStatus? _lastCaptureStatus;
    private IPhoneFilterDriverStatus _filterDriverStatus = new(
        IPhoneFilterDriverState.NoDevice, null, string.Empty);
    private string _wirelessStatus = string.Empty;
    private string _mediaCastStatus = string.Empty;
    private ulong _lastMediaCastCommandId;
    private readonly HashSet<string> _knownWirelessDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _visibleLogLines = new();

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; } =
    [
        // These values cap only the local D3D preview texture/output. The USB
        // H.264 stream and HPD1 DisplaySize request are deliberately untouched.
        new("ResolutionNative", 0, 0),
        new("Resolution1080p", 1920, 1080),
        new("Resolution720p", 1280, 720),
        new("Resolution540p", 960, 540),
    ];
    public IReadOnlyList<int> FrameRates { get; } = [120, 60, 30, 24];
    public IReadOnlyList<WirelessDisplayProfile> WirelessDisplayProfiles { get; } =
        WirelessReceiverConfiguration.DisplayProfiles;
    public IReadOnlyList<UsbProjectionModeOption> UsbProjectionModes { get; } =
    [
        new(UsbProjectionMode.Demo, "UsbModeDemoLabel", "UsbModeDemoAdvantage",
            "UsbModeDemoDisadvantage", "UsbModeDemoNotice"),
        new(UsbProjectionMode.AirPlay, "UsbModeAirPlayLabel", "UsbModeAirPlayAdvantage",
            "UsbModeAirPlayDisadvantage", "UsbModeAirPlayNotice"),
        new(UsbProjectionMode.Aisi, "UsbModeAisiLabel", "UsbModeAisiAdvantage",
            "UsbModeAisiDisadvantage", "UsbModeAisiNotice"),
    ];

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyVideoSettingsCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand AdvancedSettingsCommand { get; }
    public RelayCommand ApplyWirelessSettingsCommand { get; }
    public RelayCommand OpenDriverManagerCommand { get; }
    public bool IsAdvancedMode { get => _advancedMode; private set { if (Set(ref _advancedMode, value)) OnPropertyChanged(nameof(AdvancedSettingsVisibility)); } }
    public bool IsWirelessSelected => SelectedDevice?.IsWireless == true;
    public Visibility WiredVideoLimitSettingsVisibility => IsWirelessSelected
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WirelessActualVideoSettingsVisibility => IsWirelessSelected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WirelessTopSettingsVisibility => IsWirelessSelected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WirelessBottomSettingsVisibility => IsWirelessSelected
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UsbProjectionSettingsVisibility => SelectedDevice is not null &&
        !IsWirelessSelected && !IsBusy && !HasCaptureSession
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdvancedSettingsVisibility => IsAdvancedMode && !IsWirelessSelected &&
        CurrentUsbProjectionMode == UsbProjectionMode.AirPlay
        ? Visibility.Visible : Visibility.Collapsed;

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set => SetSelectedDevice(value, updateDriverStatus: true);
    }

    public string EnvironmentStatus { get => _environmentStatus; private set => Set(ref _environmentStatus, value); }
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }
    public string DriverState { get => _driverState; private set => Set(ref _driverState, value); }
    public string WirelessReceiverName
    {
        get => _wireless.ReceiverName;
        set
        {
            if (string.Equals(_wireless.ReceiverName, value, StringComparison.Ordinal)) return;
            _wireless.ReceiverName = value;
            OnPropertyChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
        }
    }
    public string WirelessStatus { get => _wirelessStatus; private set => Set(ref _wirelessStatus, value); }
    public string MediaCastReceiverName => _wireless.AppliedReceiverName;
    public string MediaCastStatus { get => _mediaCastStatus; private set => Set(ref _mediaCastStatus, value); }
    public WirelessDisplayProfile SelectedWirelessDisplayProfile
    {
        get => _wireless.SelectedProfile;
        set
        {
            if (value is null || ReferenceEquals(_wireless.SelectedProfile, value)) return;
            _wireless.SelectedProfile = value;
            OnPropertyChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
            if (WirelessReceiverConfiguration.RequiresOriginalQualityWarning(value))
            {
                AppPromptWindow.Inform(
                    LocalizationService.Get("WirelessOriginalQualityWarningTitle"),
                    LocalizationService.Get("WirelessOriginalQualityWarningBody"));
            }
        }
    }
    public string AppliedWirelessProfileDisplay => LocalizationService.Format(
        "WirelessProfileAppliedFormat", _wireless.AppliedProfile.Label);
    private DeviceCaptureState? CurrentDeviceSession => SelectedDevice is null ? null :
        _sessions.Get(SelectedDevice.Udid);
    public ulong CurrentSessionHandle => CurrentDeviceSession?.Handle ?? 0;
    public bool HasCaptureSession => CurrentDeviceSession?.HasSession == true;
    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) { StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); } } }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
            OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
        }
    }
    public string DeviceCount => LocalizationService.Format("DeviceCountFormat", Devices.Count);
    public string SelectedName => SelectedDevice?.DisplayName ?? LocalizationService.Get("NoDeviceSelected");
    public string SelectedModel => SelectedDevice?.ModelDisplay ?? "—";
    public string SelectedOs => SelectedDevice?.OsDisplay ?? "—";
    public string SelectedUdid => SelectedDevice?.Udid ?? "—";
    public string SelectedConnection => SelectedDevice?.ConnectionType ?? "USB";
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !Set(ref _selectedLanguage, value)) return;
            LocalizationService.SetLanguage(value);
        }
    }
    public string Resolution { get => _resolution; private set => Set(ref _resolution, value); }
    public uint SourceVideoWidth => _sourceVideoWidth;
    public uint SourceVideoHeight => _sourceVideoHeight;
    public string FpsDisplay { get => _fpsDisplay; private set => Set(ref _fpsDisplay, value); }
    public string LatencyDisplay { get => _latencyDisplay; private set => Set(ref _latencyDisplay, value); }
    public string AudioDisplay { get => _audioDisplay; private set => Set(ref _audioDisplay, value); }
    public ResolutionPreset SelectedResolutionPreset
    {
        get => _selectedResolutionPreset;
        set
        {
            if (value is null) return;
            if (!Set(ref _selectedResolutionPreset, value)) return;
            if (CurrentDeviceSession is { } session)
            {
                session.RenderWidth = value.Width;
                session.RenderHeight = value.Height;
            }
            SetSettingsStatus("PendingSettingsLocalFormat", value, SelectedFrameRate);
            OnPropertyChanged(nameof(TargetResolutionDisplay));
        }
    }

    public int SelectedFrameRate
    {
        get => _selectedFrameRate;
        set
        {
            if (!Set(ref _selectedFrameRate, value)) return;
            if (CurrentDeviceSession is { } session) session.FrameRate = value;
            SetSettingsStatus("PendingSettingsFormat", SelectedResolutionPreset, value);
            OnPropertyChanged(nameof(TargetFpsDisplay));
        }
    }

    public double PlaybackVolume
    {
        get => _playbackVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!Set(ref _playbackVolume, clamped)) return;
            if (CurrentDeviceSession is { } session) session.Volume = clamped;
            var result = CurrentSessionHandle != 0
                ? InvokeDeviceSetting(() => _core.SetDeviceAudioVolume(CurrentSessionHandle, clamped / 100.0))
                : _core.SetAudioVolume(clamped / 100.0);
            if (!result.Success) SetRawSettingsStatus(result.Message);
        }
    }

    public bool PlayAudio
    {
        get => _playAudio;
        set
        {
            if (!Set(ref _playAudio, value)) return;
            if (CurrentDeviceSession is { } session) session.PlayAudio = value;
            var result = CurrentSessionHandle != 0
                ? InvokeDeviceSetting(() => _core.SetDeviceAudioEnabled(CurrentSessionHandle, value))
                : _core.SetAudioEnabled(value);
            if (result.Success)
                SetSettingsStatus(value ? "AudioPlaybackEnabled" : "AudioPlaybackMuted");
            else SetRawSettingsStatus(result.Message);
        }
    }

    private UsbProjectionMode CurrentUsbProjectionMode =>
        CurrentDeviceSession?.UsbProjectionMode ?? UsbProjectionMode.Demo;

    public UsbProjectionModeOption? SelectedUsbProjectionMode
    {
        get => UsbProjectionModes.FirstOrDefault(option => option.Mode == CurrentUsbProjectionMode);
        set
        {
            var device = SelectedDevice;
            if (value is null || device is null || device.IsWireless || IsBusy) return;
            var state = GetOrCreateDeviceState(device);
            if (state.UsbProjectionMode == value.Mode) return;
            state.UsbProjectionMode = value.Mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdvancedSettingsVisibility));
            SetSettingsStatus("UsbProjectionModeSelectedFormat", value.Label);
            AddUiLog($"USB projection mode {value.Mode} selected for {state.Udid}");
            if (state.Handle != 0)
            {
                SetSettingsStatus("UsbProjectionModeRestarting");
                _ = RestartUsbSessionAsync(device, state);
            }
        }
    }

    public bool CanChangeUsbProjectionMode => SelectedDevice is not null &&
        !IsWirelessSelected && !IsBusy;

    private static (bool Success, string Message) InvokeDeviceSetting(Action action)
    {
        try { action(); return (true, string.Empty); }
        catch (Exception error) { return (false, error.Message); }
    }
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }
    public string TargetResolutionDisplay => LocalizationService.Format(
        "RenderLimitFormat", SelectedResolutionPreset.Label);
    public string TargetFpsDisplay => LocalizationService.Format("TargetFpsFormat", SelectedFrameRate);
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string LogPathDisplay => _logReader.Path;

    public MainViewModel()
    {
        _environmentStatus = LocalizationService.Get("StatusCheckingEnvironment");
        _captureStatus = LocalizationService.Get("StatusWaitingDevice");
        _driverState = LocalizationService.Get("StatusDetecting");
        _audioDisplay = LocalizationService.Get("StatusWaiting");
        _settingsStatus = LocalizationService.Get("StatusDefaultSettings");
        _logText = LocalizationService.Get("StatusWaitingLog");
        _selectedLanguage = LocalizationService.SelectedLanguage;
        _core = new NativeCore();
        _wireless = new WirelessReceiverController(_core);
        _mediaCast = new MediaCastReceiverController(_core);
        _sessions = new DeviceSessionManager(_core);
        _selectedResolutionPreset = ResolutionPresets[0];
        StartCommand = new RelayCommand(() => _ = StartAsync(),
            () => SelectedDevice is not null && !HasCaptureSession &&
                !IsCapturing && !IsBusy);
        StopCommand = new RelayCommand(() => _ = StopAsync(),
            () => HasCaptureSession && !IsBusy);
        // A manual refresh is guaranteed to run after a short in-flight poll;
        // timer refreshes remain best-effort and never build up a queue.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceDeviceEnumeration: true));
        ApplyVideoSettingsCommand = new RelayCommand(() => _ = ApplyVideoSettingsAsync(),
            () => !IsBusy);
        ClearLogCommand = new RelayCommand(ClearVisibleLog);
        AdvancedSettingsCommand = new RelayCommand(ShowAdvancedSettings, () => IsAdvancedMode);
        OpenDriverManagerCommand = new RelayCommand(() => OpenDriverManager());
        ApplyWirelessSettingsCommand = new RelayCommand(() => _ = RestartWirelessReceiverAsync(),
            () => _wireless.IsAvailable && !IsBusy);
        RefreshWirelessStatus();
        RefreshMediaCastStatus();
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public async Task RefreshAsync(bool forceDeviceEnumeration = false)
    {
        if (_disposed) return;
        if (forceDeviceEnumeration && Interlocked.Exchange(ref _manualRefreshPending, 1) != 0)
            return;

        var gateHeld = false;
        try
        {
            if (forceDeviceEnumeration)
            {
                // Do not silently discard a real button click just because the
                // two-second status timer currently owns the gate.
                await _coreGate.WaitAsync();
                gateHeld = true;
            }
            else
            {
                if (IsBusy || !await _coreGate.WaitAsync(0)) return;
                gateHeld = true;
            }
            if (_disposed) return;

            var receiverStart = await _wireless.EnsureStartedAsync();
            if (receiverStart.IsNewError && receiverStart.Error is not null)
                AddUiLog(receiverStart.Error);
            RefreshWirelessStatus();
            await _mediaCast.EnsureStartedAsync();
            RefreshMediaCastStatus();
            NativeEnvironmentInfo? environment = null;
            IReadOnlyList<NativeDeviceInfo> wirelessDevices;
            if (_sessions.AnySession && !forceDeviceEnumeration)
            {
                wirelessDevices = await Task.Run(_core.GetWirelessDevices);
            }
            else
            {
                if (_sessions.AnySession)
                {
                    var result = await Task.Run(() =>
                        (_core.GetDevices(), _core.GetWirelessDevices()));
                    _lastUsbDevices = result.Item1;
                    wirelessDevices = result.Item2;
                }
                else
                {
                    var result = await Task.Run(() =>
                        (_core.GetEnvironment(), _core.GetDevices(), _core.GetWirelessDevices()));
                    environment = result.Item1;
                    _lastUsbDevices = result.Item2;
                    wirelessDevices = result.Item3;
                }
            }

            if (environment is { } currentEnvironment)
            {
                _lastEnvironment = currentEnvironment;
                UpdateEnvironmentStatus(currentEnvironment);
            }

            var devices = _lastUsbDevices.Concat(wirelessDevices)
                .Where(device => !string.IsNullOrWhiteSpace(device.Udid))
                .Select(DeviceViewModel.FromNative)
                .GroupBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var currentWirelessDeviceIds = devices
                .Where(device => device.IsWireless)
                .Select(device => device.Udid)
                .ToArray();
            var newlyConnectedWirelessUdid = StableDeviceSelection.FindNewlyConnected(
                _knownWirelessDeviceIds, currentWirelessDeviceIds);
            RefreshWirelessStatus();
            await SyncWirelessSessionsLockedAsync(devices.Where(device => device.IsWireless));
            _knownWirelessDeviceIds.Clear();
            _knownWirelessDeviceIds.UnionWith(currentWirelessDeviceIds);
            var captureActive = _sessions.AnySession;
            // Device discovery runs off the UI thread and can overlap a real
            // user click. A selection captured before that await is stale and
            // used to snap the highlight back to the old phone when the poll
            // completes. Read the current UDID only when applying the result.
            var currentSelectionUdid = SelectedDevice?.Udid;
            ReconcileDevices(devices, currentSelectionUdid, captureActive,
                newlyConnectedWirelessUdid);
            var capture = await Task.Run(GetSelectedCaptureStatus);
            ApplyCaptureStatus(capture);
            await PollBackgroundSessionErrorsAsync();

            if (forceDeviceEnumeration)
                AddUiLog($"device refresh: discovered={devices.Count} visible={Devices.Count} " +
                    $"selected={SelectedDevice?.Udid ?? "-"} active={_activeCaptureUdid ?? "-"}");
        }
        catch (Exception error)
        {
            // Preserve a previously verified USB environment when a later
            // wireless/session poll fails transiently. Only the initial probe
            // can legitimately classify the whole native core as unavailable.
            if (_lastEnvironment is null)
            {
                EnvironmentStatus = LocalizationService.Format("CoreLoadFailedFormat", error.Message);
                DriverState = LocalizationService.Get("Unavailable");
            }
            if (forceDeviceEnumeration) AddUiLog($"device refresh failed: {error.Message}");
        }
        finally
        {
            if (gateHeld) _coreGate.Release();
            if (forceDeviceEnumeration) Interlocked.Exchange(ref _manualRefreshPending, 0);
        }
    }

    private async Task SyncWirelessSessionsLockedAsync(IEnumerable<DeviceViewModel> connected)
    {
        var wireless = connected.ToList();
        var connectedIds = wireless.Select(device => device.Udid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _sessions.Entries.Where(pair =>
                     DeviceViewModel.IsWirelessUdid(pair.Key) &&
                     !connectedIds.Contains(pair.Key)).ToArray())
        {
            if (pair.Value.Handle != 0)
                await _sessions.StopAndDestroyAsync(pair.Value);
            _sessions.Remove(pair.Key);
            _sessions.SetWirelessPaused(pair.Key, false);
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, pair.Key))
            {
                NativeCore.SelectPreviewSession(0);
                NotifyCaptureSessionChanged();
                _activeCaptureUdid = null;
                IsCapturing = false;
                ResetPreviewState();
            }
        }

        foreach (var device in wireless)
        {
            if (_sessions.IsWirelessPaused(device.Udid)) continue;
            if (_sessions.TryGet(device.Udid, out var existing) &&
                existing.Handle != 0) continue;
            var playAudio = !_sessions.Entries.Any(pair =>
                DeviceViewModel.IsWirelessUdid(pair.Key) && pair.Value.Handle != 0 &&
                pair.Value.PlayAudio);
            var state = existing ?? new DeviceCaptureState
            {
                Udid = device.Udid,
                RenderWidth = 0,
                RenderHeight = 0,
                FrameRate = 60,
                PlayAudio = playAudio,
                Volume = PlaybackVolume,
            };
            _sessions.Set(state);
            var result = await Task.Run(() => CreateSession(device, state));
            state.Handle = result.Success ? result.Handle : 0;
            if (!result.Success) AddUiLog(LocalizationService.Format(
                "StartFailedFormat", result.Message));
        }
    }

    private async Task RestartWirelessReceiverAsync()
    {
        if (_disposed || IsBusy) return;
        var profile = SelectedWirelessDisplayProfile;
        var connectedCount = Devices.Count(device => device.IsWireless);
        var sanitized = WirelessReceiverConfiguration.SanitizeReceiverName(WirelessReceiverName);
        var changes = new List<string>();
        if (!string.Equals(sanitized, _wireless.AppliedReceiverName, StringComparison.Ordinal))
            changes.Add(LocalizationService.Format("WirelessNameChangeFormat",
                _wireless.AppliedReceiverName, sanitized));
        if (!ReferenceEquals(profile, _wireless.AppliedProfile))
            changes.Add(LocalizationService.Format("WirelessResolutionChangeFormat",
                _wireless.AppliedProfile.Label, profile.Label));
        if (changes.Count == 0)
        {
            AppPromptWindow.Inform(LocalizationService.Get("WirelessSettingsTitle"),
                LocalizationService.Get("WirelessSettingsUnchanged"));
            return;
        }
        var impact = connectedCount > 0
            ? LocalizationService.Format("WirelessSettingsConnectedImpactFormat", connectedCount)
            : LocalizationService.Get("WirelessSettingsReadyImpact");
        var body = LocalizationService.Format("WirelessSettingsConfirmFormat",
            string.Join(Environment.NewLine, changes), impact, sanitized);
        if (!AppPromptWindow.Confirm(LocalizationService.Get("WirelessSettingsTitle"), body)) return;
        IsBusy = true;
        var gateHeld = false;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            await SyncWirelessSessionsLockedAsync([]);
            await _wireless.StopAsync();
            var started = await _wireless.EnsureStartedAsync(sanitized, profile);
            RefreshWirelessStatus();
            if (started.Started)
            {
                OnPropertyChanged(nameof(WirelessReceiverName));
                OnPropertyChanged(nameof(MediaCastReceiverName));
                OnPropertyChanged(nameof(AppliedWirelessProfileDisplay));
                RefreshWirelessStatus();
                AddUiLog(LocalizationService.Format("WirelessRunningFormat", sanitized));
            }
            else if (started.IsNewError && started.Error is not null)
                AddUiLog(started.Error);
        }
        catch (Exception error)
        {
            WirelessStatus = LocalizationService.Format("StartFailedFormat", error.Message);
            AddUiLog(WirelessStatus);
        }
        finally
        {
            if (gateHeld) _coreGate.Release();
            IsBusy = false;
        }
        await RefreshAsync(forceDeviceEnumeration: true);
    }

    private void ReconcileDevices(
        IReadOnlyList<DeviceViewModel> discovered,
        string? previousSelectionUdid,
        bool captureActive,
        string? newlyConnectedWirelessUdid)
    {
        var desired = discovered.ToList();

        // The actively mirrored phone temporarily leaves normal usbmux when
        // QuickTime configuration is enabled. Keep its existing card while
        // still merging every other phone returned by usbmux.
        if (captureActive && !string.IsNullOrWhiteSpace(_activeCaptureUdid) &&
            !desired.Any(device => DeviceViewModel.UdidEquals(device.Udid, _activeCaptureUdid)))
        {
            var activeCard = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, _activeCaptureUdid));
            if (activeCard is not null) desired.Add(activeCard);
        }
        foreach (var sessionUdid in _sessions.Values
                     .Where(session => session.HasSession).Select(session => session.Udid))
        {
            if (desired.Any(device => DeviceViewModel.UdidEquals(device.Udid, sessionUdid))) continue;
            var retained = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, sessionUdid));
            if (retained is not null) desired.Add(retained);
        }

        var desiredByUdid = desired
            .GroupBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var previousCount = Devices.Count;

        // Preserve the order and identity of every existing card. usbmux does
        // not guarantee enumeration order; moving items to match each poll
        // makes WPF publish transient selection changes and the highlight
        // appears to jump between phones. New devices are appended once.
        foreach (var existing in Devices.ToArray())
        {
            if (desiredByUdid.TryGetValue(existing.Udid, out var incoming) &&
                !ReferenceEquals(existing, incoming)) existing.UpdateFrom(incoming);
        }
        var stableOrder = StableDeviceSelection.MergeVisibleOrder(
            Devices.Select(device => device.Udid), desired.Select(device => device.Udid));
        foreach (var udid in stableOrder)
            if (!Devices.Any(existing => DeviceViewModel.UdidEquals(existing.Udid, udid)))
                Devices.Add(desiredByUdid[udid]);

        for (var index = Devices.Count - 1; index >= 0; --index)
        {
            if (!desiredByUdid.ContainsKey(Devices[index].Udid)) Devices.RemoveAt(index);
        }
        if (previousCount != Devices.Count) OnPropertyChanged(nameof(DeviceCount));

        var nextUdid = StableDeviceSelection.ChooseUdid(
            Devices.Select(device => device.Udid), previousSelectionUdid, _activeCaptureUdid,
            newlyConnectedWirelessUdid);
        var nextSelection = Devices.FirstOrDefault(device =>
            DeviceViewModel.UdidEquals(device.Udid, nextUdid));
        SetSelectedDevice(nextSelection, updateDriverStatus: false);
        NotifySelectedDeviceProperties();

        // Never invoke the legacy libusb0 enumeration API while a capture
        // handle is live. The selected device's driver state is refreshed as
        // soon as capture stops or an automatic switch completes.
        if (!captureActive) UpdateSelectedDriverStatus();
    }

    internal void MoveDevice(
        DeviceViewModel source,
        DeviceViewModel? target,
        bool placeAfterTarget)
    {
        var sourceIndex = Devices.IndexOf(source);
        if (sourceIndex < 0) return;
        int? targetIndex = target is null ? null : Devices.IndexOf(target);
        var destinationIndex = StableDeviceSelection.CalculateDropIndex(
            Devices.Count, sourceIndex, targetIndex, placeAfterTarget);
        if (destinationIndex == sourceIndex) return;
        Devices.Move(sourceIndex, destinationIndex);
    }

    internal bool HasCaptureSessionFor(DeviceViewModel device) =>
        _sessions.TryGet(device.Udid, out var session) && session.HasSession;

    private void SetSelectedDevice(DeviceViewModel? value, bool updateDriverStatus)
    {
        // Collection notifications can cause a two-way ListBox binding to
        // offer null even though the selected stable item is still present.
        // It is not a user selection and must not supersede the real UDID.
        if (value is null && _selectedDevice is not null && Devices.Contains(_selectedDevice)) return;
        if (ReferenceEquals(_selectedDevice, value)) return;
        _selectedDevice = value;
        OnPropertyChanged(nameof(SelectedDevice));
        var session = CurrentDeviceSession;
        _activeCaptureUdid = session?.HasSession == true ? value?.Udid : null;
        IsCapturing = session?.HasSession == true;
        NotifyCaptureSessionChanged();
        NativeCore.SelectPreviewSession(session?.Handle ?? 0);
        OnPropertyChanged(nameof(CurrentSessionHandle));
        if (session is not null)
        {
            SelectedFrameRate = session.FrameRate;
            PlaybackVolume = session.Volume;
            PlayAudio = session.PlayAudio;
            SelectedResolutionPreset = ResolutionPresets.FirstOrDefault(preset =>
                preset.Width == session.RenderWidth && preset.Height == session.RenderHeight)
                ?? ResolutionPresets[0];
        }
        CaptureStatus = session?.HasSession == true
            ? LocalizationService.Get("CaptureStreaming")
            : value?.StatusDisplay ?? LocalizationService.Get("StatusWaitingDevice");
        NotifySelectedDeviceProperties();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ApplyVideoSettingsCommand.NotifyCanExecuteChanged();

        if (updateDriverStatus && !HasCaptureSession)
            UpdateSelectedDriverStatus();
    }

    private void NotifySelectedDeviceProperties()
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(SelectedUdid));
        OnPropertyChanged(nameof(SelectedConnection));
        OnPropertyChanged(nameof(IsWirelessSelected));
        OnPropertyChanged(nameof(WiredVideoLimitSettingsVisibility));
        OnPropertyChanged(nameof(WirelessActualVideoSettingsVisibility));
        OnPropertyChanged(nameof(WirelessTopSettingsVisibility));
        OnPropertyChanged(nameof(WirelessBottomSettingsVisibility));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        OnPropertyChanged(nameof(SelectedUsbProjectionMode));
        OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
        OnPropertyChanged(nameof(AdvancedSettingsVisibility));
    }

    private void NotifyCaptureSessionChanged()
    {
        OnPropertyChanged(nameof(HasCaptureSession));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static bool IsActiveCaptureState(CaptureState state) => state is
        CaptureState.ActivatingUsb or CaptureState.WaitingForDevice or
        CaptureState.Handshaking or CaptureState.Streaming or CaptureState.Stopping;

    private NativeCaptureStatus GetSelectedCaptureStatus()
    {
        var handle = CurrentSessionHandle;
        return handle != 0 ? _core.GetDeviceSessionStatus(handle) : new NativeCaptureStatus
        {
            StructSize = (uint)Marshal.SizeOf<NativeCaptureStatus>(),
            State = CaptureState.Idle,
            Message = string.Empty,
        };
    }

    private async Task PollBackgroundSessionErrorsAsync()
    {
        foreach (var state in _sessions.Values.Where(value =>
                     value.Handle != 0 && value.Handle != CurrentSessionHandle).ToArray())
        {
            NativeCaptureStatus status;
            try { status = await Task.Run(() => _core.GetDeviceSessionStatus(state.Handle)); }
            catch { continue; }
            if (status.Width != 0 && status.Height != 0)
                DeviceVideoSizeChanged?.Invoke(state.Udid, status.Width, status.Height);
            if (status.State != CaptureState.Error || state.ErrorShown) continue;
            state.ErrorShown = true;
            var name = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, state.Udid))?.DisplayName ?? state.Udid;
            AppPromptWindow.Inform(LocalizationService.Format(
                "DeviceCaptureErrorTitleFormat", name), status.Message);
        }
    }

    private void ResetPreviewState()
    {
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        OnPropertyChanged(nameof(SourceVideoWidth));
        OnPropertyChanged(nameof(SourceVideoHeight));
        Resolution = "—";
        FpsDisplay = "— fps";
        LatencyDisplay = "— ms";
        AudioDisplay = LocalizationService.Get("StatusWaiting");
    }

    private async Task StartAsync()
    {
        if (_disposed || SelectedDevice is null || HasCaptureSession || IsBusy) return;
        IsBusy = true;
        var gateHeld = false;
        try
        {
            var requestedDevice = SelectedDevice;
            if (requestedDevice is null) return;
            var preflight = EnsureSourceReady(requestedDevice);
            if (!preflight.Success) return;
            // A user click that lands during the short background poll should
            // run immediately after it, rather than being silently discarded.
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            var device = SelectedDevice;
            if (device is null || !DeviceViewModel.UdidEquals(device.Udid, requestedDevice.Udid)) return;
            var preference = (Success: true, Message: LocalizationService.Get("VideoPreferencesApplied"));
            // Own the session before the native start call can block in USB
            // activation. A device click or window close during that interval
            // must still queue an explicit stop for this exact phone, and the
            // top action changes to its red stop state immediately.
            var state = GetOrCreateDeviceState(device);
            if (device.IsWireless) _sessions.SetWirelessPaused(device.Udid, false);
            var created = await Task.Run(() => CreateSession(device, state));
            state.Handle = created.Success ? created.Handle : 0;
            // Handle is not observable itself; explicitly refresh the style
            // trigger and command availability as soon as creation finishes.
            OnPropertyChanged(nameof(HasCaptureSession));
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            var result = (created.Success, created.Message);
            IsCapturing = created.Success;
            _activeCaptureUdid = created.Success ? device.Udid : null;
            NotifyCaptureSessionChanged();
            NativeCore.SelectPreviewSession(state.Handle);
            OnPropertyChanged(nameof(CurrentSessionHandle));
            CaptureStatus = result.Message;
            if (preference.Success)
                SetSettingsStatus("AppliedRenderFormat", SelectedResolutionPreset, SelectedFrameRate);
            else SetRawSettingsStatus(preference.Message);
            AddUiLog(result.Success
                ? LocalizationService.Get("StartRequested")
                : LocalizationService.Format("StartFailedFormat", result.Message));
        }
        catch (Exception error)
        {
            _activeCaptureUdid = null;
            NotifyCaptureSessionChanged();
            IsCapturing = false;
            CaptureStatus = LocalizationService.Format("StartFailedFormat", error.Message);
            AddUiLog(CaptureStatus);
        }
        finally
        {
            IsBusy = false;
            if (gateHeld) _coreGate.Release();
        }
    }

    private async Task ApplyVideoSettingsAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        var gateHeld = false;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            var width = SelectedResolutionPreset.Width;
            var height = SelectedResolutionPreset.Height;
            var preference = CurrentSessionHandle != 0
                ? _core.SetDeviceVideoPreferences(CurrentSessionHandle, width, height, (uint)SelectedFrameRate)
                : _core.SetVideoPreferences(width, height, (uint)SelectedFrameRate);
            SetRawSettingsStatus(preference.Message);
            AddUiLog(LocalizationService.Format("AppliedRenderLogFormat",
                SelectedResolutionPreset.Label, SelectedFrameRate, preference.Message));
            if (!preference.Success) return;

            SetSettingsStatus("AppliedRenderFormat", SelectedResolutionPreset, SelectedFrameRate);
        }
        catch (Exception error)
        {
            SetSettingsStatus("ApplySettingsFailedFormat", error.Message);
        }
        finally
        {
            IsBusy = false;
            if (gateHeld) _coreGate.Release();
        }
    }

    private async Task StopAsync()
    {
        if (_disposed || !HasCaptureSession || IsBusy) return;
        IsBusy = true;
        var gateHeld = false;
        CaptureStatus = LocalizationService.Get("CaptureStopping");
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            // Native stop waits for USB release packets and configuration
            // restore. Keep that wait off the WPF UI thread.
            var state = CurrentDeviceSession;
            if (state is null || state.Handle == 0) return;
            var stoppedUdid = state.Udid;
            await _sessions.StopAndDestroyAsync(state);
            if (DeviceViewModel.IsWirelessUdid(stoppedUdid))
            {
                _sessions.SetWirelessPaused(stoppedUdid, true);
                // Destroying the local decoder session only makes the preview
                // black; the iPhone keeps its AirPlay connection alive. Stop
                // the receiver process as well so the sender gets a real
                // transport disconnect and leaves its mirroring state. A short
                // auto-start holdoff prevents an immediate reconnect race.
                await _wireless.StopAsync(TimeSpan.FromSeconds(2));
                RefreshWirelessStatus();
            }
            NativeCore.SelectPreviewSession(0);
            OnPropertyChanged(nameof(CurrentSessionHandle));
            NotifyCaptureSessionChanged();
            _activeCaptureUdid = null;
            IsCapturing = false;
            CaptureStatus = LocalizationService.Get("CaptureStopped");
            ResetPreviewState();
            AddUiLog(LocalizationService.Get("StopSessionReleased"));
        }
        catch (Exception error)
        {
            _activeCaptureUdid = null;
            NotifyCaptureSessionChanged();
            IsCapturing = false;
            ResetPreviewState();
            CaptureStatus = LocalizationService.Format("StopFailedFormat", error.Message);
        }
        finally
        {
            IsBusy = false;
            if (gateHeld) _coreGate.Release();
        }
    }

    public async Task RefreshLogsAsync()
    {
        if (_disposed) return;
        var lines = await _logReader.ReadNewLinesAsync();
        foreach (var line in lines) AddLogLine(line);
        if (lines.Count != 0) PublishLogText();
    }

    public void RefreshMediaCast()
    {
        if (_disposed) return;
        var request = _core.GetMediaCastRequest();
        if (request is null || request.CommandId == _lastMediaCastCommandId) return;
        _lastMediaCastCommandId = request.CommandId;
        MediaCastCommandReceived?.Invoke(request);
    }

    internal void ReportMediaCastPlayback(ulong commandId,
        double duration, double position, double rate) =>
        _core.SetMediaCastPlaybackState(commandId, duration, position, rate);

    internal void AddUiLog(string message)
    {
        AddLogLine($"{DateTime.Now:HH:mm:ss.fff} [UI] {message}");
        PublishLogText();
    }

    internal bool IsDeviceAudioEnabled(string udid) =>
        _sessions.TryGet(udid, out var state) &&
        state.Handle != 0 && state.PlayAudio;

    internal (bool Success, string Message) SetDeviceAudioEnabled(string udid, bool enabled)
    {
        if (!_sessions.TryGet(udid, out var state) || state.Handle == 0)
            return (false, LocalizationService.Get("StatusWaitingDevice"));

        var result = InvokeDeviceSetting(() => _core.SetDeviceAudioEnabled(state.Handle, enabled));
        if (!result.Success) return result;

        state.PlayAudio = enabled;
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
        {
            Set(ref _playAudio, enabled, nameof(PlayAudio));
        }
        SetSettingsStatus(enabled ? "AudioPlaybackEnabled" : "AudioPlaybackMuted");
        return (true, LocalizationService.Get(
            enabled ? "AudioPlaybackEnabled" : "AudioPlaybackMuted"));
    }

    internal (bool Success, string Message) MuteOtherDeviceSessions(string currentUdid)
    {
        var otherIds = IndependentWindowAudioPolicy.GetOtherDeviceIds(currentUdid,
            _sessions.Entries.Where(pair => pair.Value.Handle != 0)
                .Select(pair => pair.Key));
        foreach (var udid in otherIds)
        {
            var result = SetDeviceAudioEnabled(udid, false);
            if (!result.Success) return result;
        }
        return (true, LocalizationService.Get("IndependentWindowOtherWindowsMuted"));
    }

    internal async Task<(bool Success, ulong Handle, string Message)> StartBackgroundSessionAsync(
        DeviceViewModel device)
    {
        var preflight = EnsureSourceReady(device);
        if (!preflight.Success) return (false, 0, preflight.Message);
        await _coreGate.WaitAsync();
        try
        {
            if (_sessions.TryGet(device.Udid, out var existing) && existing.Handle != 0)
                return (true, existing.Handle, string.Empty);
            var state = existing ?? new DeviceCaptureState
            {
                Udid = device.Udid,
                RenderWidth = 0,
                RenderHeight = 0,
                FrameRate = 60,
                PlayAudio = false,
                Volume = 100,
            };
            _sessions.Set(state);
            var result = await Task.Run(() => CreateSession(device, state));
            state.Handle = result.Success ? result.Handle : 0;
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            {
                IsCapturing = result.Success;
                _activeCaptureUdid = result.Success ? device.Udid : null;
                NotifyCaptureSessionChanged();
                NativeCore.SelectPreviewSession(state.Handle);
                OnPropertyChanged(nameof(CurrentSessionHandle));
            }
            return result;
        }
        finally { _coreGate.Release(); }
    }

    internal async Task StopDeviceSessionAsync(string udid)
    {
        await _coreGate.WaitAsync();
        try
        {
            if (!_sessions.TryGet(udid, out var state) || state.Handle == 0) return;
            await _sessions.StopAndDestroyAsync(state);
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
            {
                NativeCore.SelectPreviewSession(0);
                IsCapturing = false;
                _activeCaptureUdid = null;
                NotifyCaptureSessionChanged();
                OnPropertyChanged(nameof(CurrentSessionHandle));
                ResetPreviewState();
            }
        }
        finally { _coreGate.Release(); }
    }

    internal string CaptureScreenshot(string path) =>
        ScreenshotService.CapturePng(_core.GetLatestVideoFrame, path);

    private void AddLogLine(string line)
    {
        _visibleLogLines.Enqueue(line);
        while (_visibleLogLines.Count > 240) _visibleLogLines.Dequeue();
    }

    private void PublishLogText() => LogText = _visibleLogLines.Count == 0
        ? LocalizationService.Get("StatusWaitingLog")
        : string.Join(Environment.NewLine, _visibleLogLines);

    private void ClearVisibleLog()
    {
        _visibleLogLines.Clear();
        LogText = LocalizationService.Get("LogViewCleared");
    }

    private void ApplyCaptureStatus(NativeCaptureStatus status)
    {
        _lastCaptureStatus = status;
        var captureActive = IsActiveCaptureState(status.State);
        IsCapturing = captureActive;
        if (!captureActive && status.State is CaptureState.Idle or CaptureState.Stopped or CaptureState.Error)
            _activeCaptureUdid = null;
        if (status.State is not CaptureState.Idle || SelectedDevice is null)
            CaptureStatus = GetCaptureStatusText(status, IsWirelessSelected);
        Resolution = status.Width > 0 && status.Height > 0 ? $"{status.Width}×{status.Height}" : "—";
        if (status.Width > 0 && status.Height > 0 &&
            (status.Width != _sourceVideoWidth || status.Height != _sourceVideoHeight))
        {
            _sourceVideoWidth = status.Width;
            _sourceVideoHeight = status.Height;
            OnPropertyChanged(nameof(SourceVideoWidth));
            OnPropertyChanged(nameof(SourceVideoHeight));
        }
        if (status.Width != 0 && status.Height != 0 && SelectedDevice is { } selected)
            DeviceVideoSizeChanged?.Invoke(selected.Udid, status.Width, status.Height);
        FpsDisplay = status.Fps > 0 ? $"{status.Fps:F1} fps" : "— fps";
        LatencyDisplay = status.LatencyMs > 0 ? $"{status.LatencyMs:F1} ms" : "— ms";
        AudioDisplay = status.AudioSampleRate > 0
            ? $"{status.AudioSampleRate / 1000.0:F0} kHz · {status.AudioChannels} ch"
            : LocalizationService.Get("StatusWaiting");
    }

    private void UpdateEnvironmentStatus(NativeEnvironmentInfo environment)
    {
        if (environment.CaptureMuxAvailable != 0)
        {
            EnvironmentStatus = LocalizationService.Get("EnvironmentReadyCapture");
            DriverState = LocalizationService.Get("DriverCaptureReady");
        }
        else if (environment.UsbDkBackendAvailable != 0)
        {
            EnvironmentStatus = LocalizationService.Get("EnvironmentReadyUsbDk");
            DriverState = LocalizationService.Format("DriverLibUsbReadyFormat", environment.LibUsbVersion);
        }
        else if (environment.StandardMuxAvailable != 0)
        {
            EnvironmentStatus = LocalizationService.Get("EnvironmentReadyApple");
            DriverState = LocalizationService.Format("DriverAppleReadyFormat", environment.LibUsbVersion);
        }
        else
        {
            EnvironmentStatus = LocalizationService.Get("EnvironmentNeedsApple");
            DriverState = LocalizationService.Get("DriverNeedsApple");
        }
        ApplySelectedDriverState();
    }

    private void UpdateSelectedDriverStatus()
    {
        if (IsWirelessSelected)
        {
            RefreshWirelessStatus();
            return;
        }
        if (SelectedDevice is null) return;
        _filterDriverStatus = InspectDriverStatus(SelectedDevice);
        if (_lastEnvironment is { } environment)
            UpdateEnvironmentStatus(environment);
        else
            ApplySelectedDriverState();
    }

    private IPhoneFilterDriverStatus InspectDriverStatus(DeviceViewModel device,
        bool requireExactBackend = false)
    {
        var status = _filterDriver.Inspect(device.Udid);
        if (status.State == IPhoneFilterDriverState.Provisional)
        {
            try
            {
                status = _filterDriver.Inspect(device.Udid,
                    _core.IsLibUsb0DeviceAvailable(device.Udid));
            }
            catch (Exception error)
            {
                if (requireExactBackend)
                    return new(IPhoneFilterDriverState.Error, status.InstalledVersion,
                        $"Exact libusb0 device verification failed: {error.Message}");
                // Keep the conservative Provisional state. Native capture
                // repeats the same exact-serial preflight before activation.
            }
        }
        return status;
    }

    private void ApplySelectedDriverState()
    {
        if (SelectedDevice is null) return;
        if (IsWirelessSelected)
        {
            DriverState = WirelessStatus;
            EnvironmentStatus = WirelessStatus;
            return;
        }
        DriverState = _filterDriverStatus.State switch
        {
            IPhoneFilterDriverState.Ready => LocalizationService.Format(
                "DriverDeviceFilterReadyFormat", _filterDriverStatus.InstalledVersion ?? "?"),
            IPhoneFilterDriverState.Provisional => LocalizationService.Format(
                "DriverDeviceFilterProvisionalFormat", _filterDriverStatus.InstalledVersion ?? "?"),
            IPhoneFilterDriverState.Missing => LocalizationService.Get("DriverDeviceFilterMissing"),
            IPhoneFilterDriverState.PendingRestart => LocalizationService.Get("DriverReplugRequired"),
            IPhoneFilterDriverState.InvalidStack => LocalizationService.Get("DriverInvalidAppleStack"),
            IPhoneFilterDriverState.Error => LocalizationService.Get("DriverFilterStateError"),
            _ => DriverState,
        };
    }

    private (bool Success, string Message) EnsureSourceReady(
        DeviceViewModel device)
    {
        if (device.IsWireless)
        {
            if (!_wireless.IsAvailable || !_wireless.Running)
            {
                var message = _wireless.GetStatusText();
                if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
                    CaptureStatus = message;
                RefreshWirelessStatus();
                return (false, message);
            }

            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
                CaptureStatus = WirelessStatus;
            ApplySelectedDriverState();
            return (true, WirelessStatus);
        }

        // Wireless devices return above and never enter the USB driver path.
        // A real start click requires an exact serial-level libusb0 result;
        // the provisional background status is not sufficient here.
        var driverStatus = InspectDriverStatus(device, requireExactBackend: true);
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
        {
            _filterDriverStatus = driverStatus;
            ApplySelectedDriverState();
        }
        if (driverStatus.CanStartCapture)
        {
            if (driverStatus.State == IPhoneFilterDriverState.Provisional)
                AddUiLog("driver preflight is provisional; native libusb0 serial enumeration is authoritative");
            return (true, string.Empty);
        }
        var failure = LocalizationService.Get(driverStatus.State switch
        {
            IPhoneFilterDriverState.NoDevice => "DriverReconnectPhone",
            IPhoneFilterDriverState.PendingRestart => "DriverReplugRequired",
            IPhoneFilterDriverState.Missing => "DriverExternalRequired",
            IPhoneFilterDriverState.InvalidStack => "DriverInvalidAppleStack",
            _ => "DriverFilterStateError",
        });
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            CaptureStatus = failure;
        AddUiLog($"driver preflight: {driverStatus.Diagnostic}");
        if (driverStatus.State is IPhoneFilterDriverState.PendingRestart or
            IPhoneFilterDriverState.Missing or IPhoneFilterDriverState.InvalidStack or
            IPhoneFilterDriverState.Error)
            OpenDriverManager(automatic: true);
        return (false, failure);
    }

    private bool OpenDriverManager(bool automatic = false)
    {
        var result = _driverManager.Launch();
        if (result.Success)
        {
            AddUiLog(LocalizationService.Get(automatic
                ? "DriverManagerOpenedAutomatically"
                : "DriverManagerOpened"));
            return true;
        }

        var failure = LocalizationService.Format("DriverManagerLaunchFailedFormat",
            result.Message);
        AddUiLog(failure);
        if (!automatic) CaptureStatus = failure;
        AppPromptWindow.Inform(LocalizationService.Get("DriverManagerTitle"), failure);
        return false;
    }

    private static string GetCaptureStatusText(NativeCaptureStatus status, bool wireless) => status.State switch
    {
        CaptureState.Idle => LocalizationService.Get("CaptureIdle"),
        CaptureState.ActivatingUsb => LocalizationService.Get(wireless ? "WirelessStarting" : "CaptureActivating"),
        CaptureState.WaitingForDevice => LocalizationService.Get(wireless ? "WirelessWaitingDevice" : "CaptureWaitingDevice"),
        CaptureState.Handshaking => LocalizationService.Get(wireless ? "WirelessConnecting" : "CaptureHandshaking"),
        CaptureState.Streaming => LocalizationService.Get(wireless ? "WirelessStreaming" : "CaptureStreaming"),
        CaptureState.Stopping => LocalizationService.Get(wireless ? "WirelessStopping" : "CaptureStopping"),
        CaptureState.Stopped => LocalizationService.Get(wireless ? "WirelessStopped" : "CaptureStopped"),
        _ => LocalizationService.Get("CaptureError"),
    };

    private void RefreshWirelessStatus()
    {
        WirelessStatus = _wireless.GetStatusText();
        if (IsWirelessSelected) ApplySelectedDriverState();
    }

    private void RefreshMediaCastStatus() => MediaCastStatus = _mediaCast.GetStatusText();

    private (bool Success, ulong Handle, string Message) CreateSession(
        DeviceViewModel device, DeviceCaptureState state)
    {
        if (device.IsWireless)
        {
            return _core.CreateWirelessSession(device.Udid,
                state.RenderWidth, state.RenderHeight, (uint)state.FrameRate,
                state.PlayAudio, state.Volume / 100.0);
        }
        return _core.CreateDeviceSession(device.Udid,
            state.RenderWidth, state.RenderHeight, (uint)state.FrameRate,
            state.PlayAudio, state.Volume / 100.0,
            IsAdvancedMode ? state.AdvancedUsbWidth : 0,
            IsAdvancedMode ? state.AdvancedUsbHeight : 0,
            (uint)state.UsbProjectionMode);
    }

    private DeviceCaptureState GetOrCreateDeviceState(DeviceViewModel device)
    {
        if (_sessions.TryGet(device.Udid, out var state)) return state;
        state = new DeviceCaptureState
        {
            Udid = device.Udid,
            RenderWidth = SelectedResolutionPreset.Width,
            RenderHeight = SelectedResolutionPreset.Height,
            FrameRate = SelectedFrameRate,
            PlayAudio = PlayAudio,
            Volume = PlaybackVolume,
        };
        _sessions.Set(state);
        return state;
    }

    private void SetSettingsStatus(string resourceKey, params object?[] arguments)
    {
        _settingsStatusKey = resourceKey;
        _settingsStatusArguments = arguments;
        SettingsStatus = LocalizationService.Format(resourceKey, arguments);
    }

    private void SetRawSettingsStatus(string value)
    {
        _settingsStatusKey = null;
        _settingsStatusArguments = [];
        SettingsStatus = value;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _selectedLanguage = LocalizationService.SelectedLanguage;
        OnPropertyChanged(nameof(SelectedLanguage));
        foreach (var preset in ResolutionPresets) preset.NotifyLanguageChanged();
        foreach (var profile in WirelessDisplayProfiles) profile.NotifyLanguageChanged();
        foreach (var mode in UsbProjectionModes) mode.NotifyLanguageChanged();

        foreach (var device in Devices) device.NotifyLanguageChanged();

        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AppliedWirelessProfileDisplay));
        if (_lastEnvironment is { } environment) UpdateEnvironmentStatus(environment);
        else
        {
            EnvironmentStatus = LocalizationService.Get("StatusCheckingEnvironment");
            DriverState = LocalizationService.Get("StatusDetecting");
        }
        if (_lastCaptureStatus is { } capture) ApplyCaptureStatus(capture);
        else if (SelectedDevice is null) CaptureStatus = LocalizationService.Get("StatusWaitingDevice");
        if (_settingsStatusKey is not null)
            SettingsStatus = LocalizationService.Format(_settingsStatusKey, _settingsStatusArguments);
        if (_visibleLogLines.Count == 0) PublishLogText();
        ApplySelectedDriverState();
        RefreshWirelessStatus();
        RefreshMediaCastStatus();
    }

    internal void EnableAdvancedMode()
    {
        IsAdvancedMode = true;
        AdvancedSettingsCommand.NotifyCanExecuteChanged();
    }

    private void ShowAdvancedSettings()
    {
        if (SelectedDevice is null || SelectedDevice.IsWireless) return;
        var state = GetOrCreateDeviceState(SelectedDevice);
        var device = SelectedDevice;
        var window = new Windows.AdvancedSettingsWindow(state.AdvancedUsbWidth, state.AdvancedUsbHeight)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            state.AdvancedUsbWidth = window.RequestedWidth;
            state.AdvancedUsbHeight = window.RequestedHeight;
            SetRawSettingsStatus($"USB {window.RequestedWidth}×{window.RequestedHeight}");
            AddUiLog($"Advanced USB request {window.RequestedWidth}x{window.RequestedHeight} saved for {state.Udid}");
            if (state.Handle != 0) _ = RestartUsbSessionAsync(device, state);
        }
        if (window.DisableAdvancedModeRequested)
        {
            IsAdvancedMode = false;
            AdvancedSettingsCommand.NotifyCanExecuteChanged();
            state.AdvancedUsbWidth = state.AdvancedUsbHeight = 0;
            SetRawSettingsStatus(LocalizationService.Get("AdvancedModeDisabled"));
            if (state.Handle != 0) _ = RestartUsbSessionAsync(device, state);
        }
    }

    private async Task RestartUsbSessionAsync(DeviceViewModel device, DeviceCaptureState state)
    {
        if (device.IsWireless || IsBusy || state.Handle == 0) return;
        IsBusy = true;
        var gateHeld = false;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            await _sessions.StopAndDestroyAsync(state);
            OnPropertyChanged(nameof(HasCaptureSession));
            // libusb0 restores the phone's normal configuration during the
            // stop path. Give Windows and the Apple USB stack a complete
            // re-enumeration window before opening QuickTime again.
            Exception? lastFailure = null;
            (bool Success, ulong Handle, string Message) created = (false, 0, "");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(attempt == 1 ? 1500 : 2500);
                created = await Task.Run(() => CreateSession(device, state));
                if (!created.Success)
                {
                    lastFailure = new InvalidOperationException(created.Message);
                    continue;
                }

                state.Handle = created.Handle;
                OnPropertyChanged(nameof(HasCaptureSession));
                var deadline = DateTime.UtcNow.AddSeconds(6);
                var ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    NativeCaptureStatus status;
                    try { status = await Task.Run(() => _core.GetDeviceSessionStatus(created.Handle)); }
                    catch (Exception error) { lastFailure = error; break; }
                    if (status.State == CaptureState.Streaming) { ready = true; break; }
                    if (status.State == CaptureState.Error || status.State == CaptureState.Stopped)
                    {
                        lastFailure = new InvalidOperationException(status.Message);
                        break;
                    }
                }
                if (ready)
                {
                    var appliedMode = UsbProjectionModes.FirstOrDefault(option =>
                        option.Mode == state.UsbProjectionMode)?.Label ?? state.UsbProjectionMode.ToString();
                    if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, state.Udid))
                    {
                        IsCapturing = true;
                        NativeCore.SelectPreviewSession(state.Handle);
                        OnPropertyChanged(nameof(CurrentSessionHandle));
                        SetSettingsStatus("UsbProjectionModeAppliedFormat", appliedMode);
                    }
                    AddUiLog($"USB projection mode {state.UsbProjectionMode} applied for {state.Udid}");
                    return;
                }
                try { await _sessions.StopAndDestroyAsync(state); } catch { }
                OnPropertyChanged(nameof(HasCaptureSession));
            }
            throw lastFailure ?? new InvalidOperationException(created.Message);
        }
        catch (Exception error)
        {
            SetRawSettingsStatus(error.Message);
            state.Handle = 0;
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, state.Udid))
                IsCapturing = false;
            OnPropertyChanged(nameof(HasCaptureSession));
        }
        finally
        {
            IsBusy = false;
            if (gateHeld) _coreGate.Release();
        }
    }

    internal async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        await _coreGate.WaitAsync();
        try
        {
            await _shutdownCoordinator.StopAndDisposeOnceAsync(
                async () =>
                {
                    try
                    {
                        foreach (var session in _sessions.Values.Where(value => value.Handle != 0).ToArray())
                        {
                            AddUiLog($"application shutdown stopping device session: " +
                                $"udid={session.Udid} handle={session.Handle}");
                            await _sessions.StopAndDestroyAsync(session);
                        }
                        // Defensive cleanup for a legacy session created by an
                        // older component in the same process.
                        await Task.Run(_core.StopCapture);
                    }
                    finally
                    {
                        NotifyCaptureSessionChanged();
                        _activeCaptureUdid = null;
                        IsCapturing = false;
                    }
                },
                () => Task.Run(_core.Dispose));
        }
        finally
        {
            _coreGate.Release();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
