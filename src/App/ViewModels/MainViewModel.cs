using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

internal sealed class DecoderPreferenceOption(
    DecoderPreference preference, string labelResourceKey) : INotifyPropertyChanged
{
    public DecoderPreference Preference { get; } = preference;
    public string Label => LocalizationService.Get(labelResourceKey);
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly record struct SessionStartSettings(
        uint RenderWidth,
        uint RenderHeight,
        int FrameRate,
        bool PlayAudio,
        double Volume,
        uint AdvancedUsbWidth,
        uint AdvancedUsbHeight,
        UsbProjectionMode UsbProjectionMode,
        DecoderPreference DecoderPreference,
        double Brightness,
        double Contrast,
        double Saturation,
        double Gamma);

    internal event Action<string, uint, uint>? DeviceVideoSizeChanged;
    internal event Action<MediaCastRequest>? MediaCastCommandReceived;
    internal event Action? MediaCastStopRequested;
    internal event Action<bool, double>? MediaCastAudioSettingsChanged;
    internal event Action<string, ulong>? DeviceSessionHandleChanged;
    private readonly NativeCore _core;
    private readonly IPhoneFilterDriverService _filterDriver = new();
    private readonly DriverManagerLauncher _driverManager = new();
    private readonly WirelessReceiverController _wireless;
    private readonly MediaCastReceiverController _mediaCast;
    // Serializes every native-core operation that can race USB teardown,
    // device enumeration, restart, or application shutdown.
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly NativeLogTailReader _logReader = new();
    private readonly CaptureShutdownCoordinator _shutdownCoordinator = new();
    private readonly DeviceSessionManager _sessions;
    private readonly Dictionary<string, ImageSettingsWindow> _imageSettingsWindows =
        new(StringComparer.OrdinalIgnoreCase);
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
    private string _decoderStatus = string.Empty;
    private string? _settingsStatusKey = "StatusDefaultSettings";
    private object?[] _settingsStatusArguments = [];
    private string _logText = string.Empty;
    private string _selectedLanguage = LocalizationService.SystemLanguage;
    private NativeEnvironmentInfo? _lastEnvironment;
    private NativeCaptureStatus? _lastCaptureStatus;
    private ulong _lastCaptureStatusHandle;
    private IPhoneFilterDriverStatus _filterDriverStatus = new(
        IPhoneFilterDriverState.NoDevice, null, string.Empty);
    private string _wirelessStatus = string.Empty;
    private string _mediaCastStatus = string.Empty;
    private ulong _lastMediaCastCommandId;
    private bool _isMediaCasting;
    private DeviceViewModel? _mediaCastDevice;
    private string? _selectionBeforeMediaCast;
    private uint _mediaCastWidth;
    private uint _mediaCastHeight;
    private bool _mediaCastAudioEnabled = true;
    private bool _mediaCastPlayAudio = true;
    private double _mediaCastPlaybackVolume = 100;
    private readonly HashSet<string> _knownWirelessDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset CheckedAt, bool Available)>
        _libUsbProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _libUsbProbesInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _visibleLogLines = new();
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private long _refreshSequence;
    private string? _lastInventorySignature;
    private string? _lastRefreshError;
    private string? _lastWirelessStatusSignature;
    private string? _lastMediaCastStatusSignature;
    private string? _lastMediaPollError;
    private string? _lastLogReadError;
    private string? _lastVideoOutputSignature;
    private CaptureState? _lastLoggedCaptureState;
    private ulong _lastLoggedCaptureHandle;
    private long _driverProbeRevision;

    private static readonly TimeSpan LibUsbProbeCacheLifetime = TimeSpan.FromSeconds(10);

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
    public IReadOnlyList<DecoderPreferenceOption> DecoderPreferences { get; } =
    [
        new(DecoderPreference.Auto, "DecoderAuto"),
        new(DecoderPreference.HardwarePreferred, "DecoderHardwarePreferred"),
        new(DecoderPreference.SoftwareCompatible, "DecoderSoftwareCompatible"),
    ];
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand MediaCastStopCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyVideoSettingsCommand { get; }
    public RelayCommand MoreImageSettingsCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand AdvancedSettingsCommand { get; }
    public RelayCommand ApplyWirelessSettingsCommand { get; }
    public RelayCommand OpenDriverManagerCommand { get; }
    public bool IsAdvancedMode { get => _advancedMode; private set { if (Set(ref _advancedMode, value)) OnPropertyChanged(nameof(AdvancedSettingsVisibility)); } }
    public bool IsWirelessSelected => SelectedDevice?.IsWireless == true;
    public bool IsMediaCasting => _isMediaCasting;
    public bool IsMediaCastSelected => SelectedDevice?.IsMediaCast == true;
    public Visibility WiredVideoLimitSettingsVisibility => IsWirelessSelected || IsMediaCastSelected
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WirelessActualVideoSettingsVisibility => IsWirelessSelected && !IsMediaCastSelected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WirelessTopSettingsVisibility => IsWirelessSelected && !IsMediaCastSelected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WirelessBottomSettingsVisibility => IsWirelessSelected || IsMediaCastSelected
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UsbProjectionSettingsVisibility => SelectedDevice is not null &&
        !IsWirelessSelected && !IsMediaCastSelected && !IsBusy && !HasCaptureSession
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdvancedSettingsVisibility => IsAdvancedMode && !IsWirelessSelected &&
        !IsMediaCastSelected &&
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
            MoreImageSettingsCommand.NotifyCanExecuteChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
            MediaCastStopCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
            OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
            OnPropertyChanged(nameof(CanChangeVideoPipeline));
            OnPropertyChanged(nameof(CanOpenImageSettings));
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
    public string AudioDetailDisplay => IsMediaCastSelected
        ? LocalizationService.Get("MediaCastSystemDecoder") : "48 kHz PCM";
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
            if (SelectedDevice is { IsWireless: false, IsMediaCast: false })
                SetPendingVideoSettingsStatus(CurrentDeviceSession);
            else
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
            if (SelectedDevice is { IsWireless: false, IsMediaCast: false })
                SetPendingVideoSettingsStatus(CurrentDeviceSession);
            else
                SetSettingsStatus("PendingSettingsFormat", SelectedResolutionPreset, value);
            OnPropertyChanged(nameof(TargetFpsDisplay));
        }
    }

    public double PlaybackVolume
    {
        get => IsMediaCastSelected ? _mediaCastPlaybackVolume : _playbackVolume;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (IsMediaCastSelected)
            {
                if (Math.Abs(_mediaCastPlaybackVolume - clamped) < 0.001) return;
                _mediaCastPlaybackVolume = clamped;
                OnPropertyChanged();
                _mediaCastAudioEnabled = _mediaCastPlayAudio && clamped > 0;
                AddDiagnosticLog(AppLog.Event("audio_volume_changed",
                    ("source", "media_cast"), ("volume_percent", clamped),
                    ("enabled", _mediaCastAudioEnabled)));
                ApplyMediaCastStatistics();
                MediaCastAudioSettingsChanged?.Invoke(
                    _mediaCastPlayAudio, clamped / 100.0);
                return;
            }
            if (!Set(ref _playbackVolume, clamped)) return;
            if (CurrentDeviceSession is { } session) session.Volume = clamped;
            AddDiagnosticLog(AppLog.Event("audio_volume_changed",
                ("source", AppLog.Device(SelectedDevice?.Udid)),
                ("volume_percent", clamped), ("enabled", _playAudio)));
            var result = CurrentSessionHandle != 0
                ? InvokeDeviceSetting(() => _core.SetDeviceAudioVolume(CurrentSessionHandle, clamped / 100.0))
                : _core.SetAudioVolume(clamped / 100.0);
            if (!result.Success) SetRawSettingsStatus(result.Message);
        }
    }

    public bool PlayAudio
    {
        get => IsMediaCastSelected ? _mediaCastPlayAudio : _playAudio;
        set
        {
            if (IsMediaCastSelected)
            {
                if (_mediaCastPlayAudio == value) return;
                _mediaCastPlayAudio = value;
                OnPropertyChanged();
                _mediaCastAudioEnabled = value && _mediaCastPlaybackVolume > 0;
                AddDiagnosticLog(AppLog.Event("audio_enabled_changed",
                    ("source", "media_cast"), ("enabled", value),
                    ("volume_percent", _mediaCastPlaybackVolume)));
                ApplyMediaCastStatistics();
                MediaCastAudioSettingsChanged?.Invoke(
                    value, _mediaCastPlaybackVolume / 100.0);
                return;
            }
            if (!Set(ref _playAudio, value)) return;
            if (CurrentDeviceSession is { } session) session.PlayAudio = value;
            AddDiagnosticLog(AppLog.Event("audio_enabled_changed",
                ("source", AppLog.Device(SelectedDevice?.Udid)),
                ("enabled", value), ("volume_percent", _playbackVolume)));
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
            AddUiLog(AppLog.Event("usb projection mode selected",
                ("mode", value.Mode), ("device", AppLog.Device(state.Udid))));
            AddDiagnosticLog(AppLog.Event("usb_projection_mode_selected",
                ("mode", value.Mode), ("device", AppLog.Device(state.Udid)),
                ("has_session", state.Handle != 0)));
            if (state.Handle != 0)
            {
                SetSettingsStatus("UsbProjectionModeRestarting");
                _ = RestartUsbSessionAsync(device, state, "usb_projection");
            }
        }
    }

    public bool CanChangeUsbProjectionMode => SelectedDevice is not null &&
        !IsWirelessSelected && !IsMediaCastSelected && !IsBusy;

    private DecoderPreference CurrentDecoderPreference =>
        CurrentDeviceSession?.DecoderPreference ?? DecoderPreference.Auto;

    public DecoderPreferenceOption? SelectedDecoderPreference
    {
        get => DecoderPreferences.FirstOrDefault(option =>
            option.Preference == CurrentDecoderPreference);
        set
        {
            var device = SelectedDevice;
            if (value is null || device is null || device.IsWireless ||
                device.IsMediaCast || IsBusy) return;
            var state = GetOrCreateDeviceState(device);
            if (state.DecoderPreference == value.Preference) return;
            state.DecoderPreference = value.Preference;
            OnPropertyChanged();
            SetPendingVideoSettingsStatus(state);
            AddDiagnosticLog(AppLog.Event("decoder_preference_selected",
                ("preference", value.Preference),
                ("device", AppLog.Device(state.Udid)),
                ("has_session", state.Handle != 0),
                ("pending_apply", true)));
        }
    }

    public bool CanChangeVideoPipeline => SelectedDevice is not null &&
        !IsWirelessSelected && !IsMediaCastSelected && !IsBusy;

    public bool CanOpenImageSettings => SelectedDevice is not null &&
        !IsMediaCastSelected && !IsBusy;

    private static (bool Success, string Message) InvokeDeviceSetting(Action action)
    {
        try { action(); return (true, string.Empty); }
        catch (Exception error) { return (false, error.Message); }
    }
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }
    public string DecoderStatus
    {
        get => _decoderStatus;
        private set => Set(ref _decoderStatus, value);
    }
    public string TargetResolutionDisplay => IsMediaCastSelected
        ? LocalizationService.Get("MediaCastOriginalResolution")
        : LocalizationService.Format("RenderLimitFormat", SelectedResolutionPreset.Label);
    public string TargetFpsDisplay => IsMediaCastSelected
        ? LocalizationService.Format("MediaCastFpsCapabilityFormat",
            _wireless.AppliedProfile.FrameRate)
        : LocalizationService.Format("TargetFpsFormat", SelectedFrameRate);
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
        _sessions.SessionHandleChanged += (udid, handle) =>
            DeviceSessionHandleChanged?.Invoke(udid, handle);
        AddDiagnosticLog(AppLog.Event("app_start",
            ("pid", Environment.ProcessId),
            ("runtime", Environment.Version),
            ("os", Environment.OSVersion.Version),
            ("arch", RuntimeInformation.ProcessArchitecture),
            ("log_override", !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE")))));
        _selectedResolutionPreset = ResolutionPresets[0];
        StartCommand = new RelayCommand(() => _ = StartAsync(),
            () => SelectedDevice is not null && !HasCaptureSession &&
                !IsCapturing && !IsBusy && !IsMediaCasting);
        StopCommand = new RelayCommand(() => _ = StopAsync(),
            () => HasCaptureSession && !IsBusy);
        MediaCastStopCommand = new RelayCommand(() => RequestMediaCastStop(),
            () => IsMediaCasting);
        // A manual refresh is guaranteed to run after a short in-flight poll;
        // timer refreshes remain best-effort and never build up a queue.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceDeviceEnumeration: true));
        ApplyVideoSettingsCommand = new RelayCommand(() => _ = ApplyVideoSettingsAsync(),
            () => !IsBusy);
        MoreImageSettingsCommand = new RelayCommand(ShowImageSettings,
            () => CanOpenImageSettings);
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

        var refreshId = Interlocked.Increment(ref _refreshSequence);
        var refreshElapsed = Stopwatch.StartNew();
        var trigger = forceDeviceEnumeration ? "manual" : "timer";
        if (forceDeviceEnumeration)
            AddDiagnosticLog(AppLog.Event("device_refresh_begin",
                ("id", refreshId), ("trigger", trigger),
                ("sessions", _sessions.Values.Count(state => state.Handle != 0))));
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
            if (_sessions.AnySession)
            {
                // usbmux/Lockdown discovery is independent of the active
                // capture transport. Keep refreshing it so unrelated wired
                // devices can appear and disappear while any session is live.
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
            if (!IsMediaCastSelected)
            {
                var capture = await Task.Run(GetSelectedCaptureStatus);
                if (!IsMediaCastSelected) ApplyCaptureStatus(capture);
            }
            await PollBackgroundSessionErrorsAsync();

            var wiredCount = devices.Count(device => !device.IsWireless);
            var wirelessCount = devices.Count - wiredCount;
            var inventorySignature = string.Join('|',
                devices.OrderBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
                    .Select(device => AppLog.Device(device.Udid)));
            var inventoryChanged = !string.Equals(_lastInventorySignature,
                inventorySignature, StringComparison.Ordinal);
            _lastInventorySignature = inventorySignature;
            _lastRefreshError = null;
            if (forceDeviceEnumeration || inventoryChanged)
                AddDiagnosticLog(AppLog.Event("device_refresh_complete",
                    ("id", refreshId), ("trigger", trigger),
                    ("elapsed_ms", refreshElapsed.ElapsedMilliseconds),
                    ("changed", inventoryChanged), ("discovered", devices.Count),
                    ("wired", wiredCount), ("wireless", wirelessCount),
                    ("visible", Devices.Count),
                    ("selected", AppLog.Device(SelectedDevice?.Udid)),
                    ("active", AppLog.Device(_activeCaptureUdid)),
                    ("new_wireless", AppLog.Device(newlyConnectedWirelessUdid))));
            if (forceDeviceEnumeration)
                AddUiLog(AppLog.Event("device refresh",
                    ("discovered", devices.Count), ("visible", Devices.Count),
                    ("selected", AppLog.Device(SelectedDevice?.Udid)),
                    ("active", AppLog.Device(_activeCaptureUdid))));
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
            var failure = AppLog.Error(error);
            if (forceDeviceEnumeration || !string.Equals(_lastRefreshError,
                    failure, StringComparison.Ordinal))
                AddDiagnosticLog(AppLog.Event("device_refresh_failed",
                    ("id", refreshId), ("trigger", trigger),
                    ("elapsed_ms", refreshElapsed.ElapsedMilliseconds),
                    ("error", failure)));
            _lastRefreshError = failure;
            if (forceDeviceEnumeration)
                AddUiLog($"device refresh failed: {AppLog.Error(error.Message)}");
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
            AddDiagnosticLog(AppLog.Event("wireless_device_removed",
                ("device", AppLog.Device(pair.Key)),
                ("had_session", pair.Value.Handle != 0),
                ("handle", AppLog.Handle(pair.Value.Handle))));
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
            AddDiagnosticLog(AppLog.Event("wireless_session_create_begin",
                ("device", AppLog.Device(device.Udid)),
                ("fps", state.FrameRate), ("audio", state.PlayAudio)));
            var startSettings = CaptureSessionStartSettings(state);
            var result = await Task.Run(() => CreateSession(device, startSettings));
            _sessions.SetHandle(state, result.Success ? result.Handle : 0);
            if (result.Success) state.MarkVideoSettingsApplied(
                startSettings.RenderWidth, startSettings.RenderHeight,
                startSettings.FrameRate, startSettings.DecoderPreference,
                startSettings.Brightness, startSettings.Contrast,
                startSettings.Saturation, startSettings.Gamma);
            AddDiagnosticLog(AppLog.Event("wireless_session_create_end",
                ("device", AppLog.Device(device.Udid)),
                ("success", result.Success),
                ("handle", AppLog.Handle(result.Handle)),
                ("message", result.Message)));
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
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("wireless_settings_begin",
            ("receiver_name_length", sanitized.Length),
            ("profile", profile.Label), ("connected", connectedCount)));
        var changes = new List<string>();
        if (!string.Equals(sanitized, _wireless.AppliedReceiverName, StringComparison.Ordinal))
            changes.Add(LocalizationService.Format("WirelessNameChangeFormat",
                _wireless.AppliedReceiverName, sanitized));
        if (!ReferenceEquals(profile, _wireless.AppliedProfile))
            changes.Add(LocalizationService.Format("WirelessResolutionChangeFormat",
                _wireless.AppliedProfile.Label, profile.Label));
        if (changes.Count == 0)
        {
            AddDiagnosticLog(AppLog.Event("wireless_settings_unchanged"));
            AppPromptWindow.Inform(LocalizationService.Get("WirelessSettingsTitle"),
                LocalizationService.Get("WirelessSettingsUnchanged"));
            return;
        }
        var impact = connectedCount > 0
            ? LocalizationService.Format("WirelessSettingsConnectedImpactFormat", connectedCount)
            : LocalizationService.Get("WirelessSettingsReadyImpact");
        var body = LocalizationService.Format("WirelessSettingsConfirmFormat",
            string.Join(Environment.NewLine, changes), impact, sanitized);
        if (!AppPromptWindow.Confirm(LocalizationService.Get("WirelessSettingsTitle"), body))
        {
            AddDiagnosticLog(AppLog.Event("wireless_settings_cancelled"));
            return;
        }
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
                AddDiagnosticLog(AppLog.Event("wireless_settings_complete",
                    ("success", true), ("profile", profile.Label),
                    ("elapsed_ms", operation.ElapsedMilliseconds)));
            }
            else if (started.IsNewError && started.Error is not null)
            {
                AddDiagnosticLog(AppLog.Event("wireless_settings_failed",
                    ("success", false), ("elapsed_ms", operation.ElapsedMilliseconds),
                    ("error", started.Error)));
                AddUiLog(started.Error);
            }
        }
        catch (Exception error)
        {
            WirelessStatus = LocalizationService.Format("StartFailedFormat", error.Message);
            AddDiagnosticLog(AppLog.Event("wireless_settings_failed",
                ("success", false), ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
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
        if (_isMediaCasting && _mediaCastDevice is not null)
            desired.Insert(0, _mediaCastDevice);

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
            newlyConnectedWirelessUdid, preferNewlyConnectedWireless: !IsMediaCastSelected);
        var nextSelection = Devices.FirstOrDefault(device =>
            DeviceViewModel.UdidEquals(device.Udid, nextUdid));
        SetSelectedDevice(nextSelection, updateDriverStatus: false);
        NotifySelectedDeviceProperties();

        // Never invoke the legacy libusb0 enumeration API while a capture
        // handle is live. The selected device's driver state is refreshed as
        // soon as capture stops or an automatic switch completes.
        if (!captureActive && !IsMediaCastSelected) UpdateSelectedDriverStatus();
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

    internal ulong GetDeviceSessionHandle(string udid) =>
        _sessions.TryGet(udid, out var session) && !session.IsStopping
            ? session.Handle : 0;

    private void SetSelectedDevice(DeviceViewModel? value, bool updateDriverStatus)
    {
        // Collection notifications can cause a two-way ListBox binding to
        // offer null even though the selected stable item is still present.
        // It is not a user selection and must not supersede the real UDID.
        if (value is null && _selectedDevice is not null && Devices.Contains(_selectedDevice)) return;
        if (ReferenceEquals(_selectedDevice, value)) return;
        var previous = _selectedDevice;
        _selectedDevice = value;
        Interlocked.Increment(ref _driverProbeRevision);
        OnPropertyChanged(nameof(SelectedDevice));
        if (value?.IsMediaCast == true)
        {
            ApplyMediaCastStatistics();
            AddDiagnosticLog(AppLog.Event("source_selected",
                ("from", AppLog.Device(previous?.Udid)),
                ("to", AppLog.Device(value.Udid)),
                ("kind", "media_cast"), ("session", AppLog.Handle(0)),
                ("driver_refresh", updateDriverStatus)));
            NotifySelectedDeviceProperties();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
            MoreImageSettingsCommand.NotifyCanExecuteChanged();
            return;
        }
        var session = CurrentDeviceSession;
        _activeCaptureUdid = session?.HasSession == true ? value?.Udid : null;
        IsCapturing = session?.HasSession == true;
        NotifyCaptureSessionChanged();
        NativeCore.SelectPreviewSession(session?.Handle ?? 0);
        OnPropertyChanged(nameof(CurrentSessionHandle));
        RestoreSelectedVideoControls(session);
        // Selection restores controls only. These values already belong to
        // this session; invoking their public setters would resend native
        // audio commands while another core operation may be in progress.
        _playbackVolume = session?.Volume ?? 100;
        _playAudio = session?.PlayAudio ?? true;
        if (value is { IsWireless: false, IsMediaCast: false })
            RestoreSelectedSettingsStatus(session);
        CaptureStatus = session?.HasSession == true
            ? LocalizationService.Get("CaptureStreaming")
            : value?.StatusDisplay ?? LocalizationService.Get("StatusWaitingDevice");
        if (session?.HasSession == true && _lastCaptureStatus is { } cached &&
            _lastCaptureStatusHandle == session.Handle)
            ApplyCaptureStatus(cached);
        else
            ResetPreviewState();
        var sourceKind = value is null ? "none" :
            value.IsWireless ? "wireless" : "wired";
        AddDiagnosticLog(AppLog.Event("source_selected",
            ("from", AppLog.Device(previous?.Udid)),
            ("to", AppLog.Device(value?.Udid)),
            ("kind", sourceKind),
            ("session", AppLog.Handle(session?.Handle ?? 0)),
            ("capturing", IsCapturing), ("driver_refresh", updateDriverStatus)));
        NotifySelectedDeviceProperties();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
        MoreImageSettingsCommand.NotifyCanExecuteChanged();

        if (updateDriverStatus && !_sessions.AnySession)
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
        OnPropertyChanged(nameof(IsMediaCastSelected));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
        OnPropertyChanged(nameof(WiredVideoLimitSettingsVisibility));
        OnPropertyChanged(nameof(WirelessActualVideoSettingsVisibility));
        OnPropertyChanged(nameof(WirelessTopSettingsVisibility));
        OnPropertyChanged(nameof(WirelessBottomSettingsVisibility));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        OnPropertyChanged(nameof(SelectedUsbProjectionMode));
        OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
        OnPropertyChanged(nameof(SelectedDecoderPreference));
        OnPropertyChanged(nameof(CanChangeVideoPipeline));
        OnPropertyChanged(nameof(AdvancedSettingsVisibility));
        OnPropertyChanged(nameof(PlaybackVolume));
        OnPropertyChanged(nameof(PlayAudio));
    }

    private void NotifyCaptureSessionChanged()
    {
        OnPropertyChanged(nameof(HasCaptureSession));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        OnPropertyChanged(nameof(CanChangeVideoPipeline));
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
        DecoderStatus = string.Empty;
        _lastVideoOutputSignature = null;
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
        var requestedDevice = SelectedDevice;
        var requestedState = GetOrCreateDeviceState(requestedDevice);
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("capture_start_begin",
            ("device", AppLog.Device(requestedDevice.Udid)),
            ("kind", requestedDevice.IsWireless ? "wireless" : "wired"),
            ("resolution", $"{SelectedResolutionPreset.Width}x{SelectedResolutionPreset.Height}"),
            ("fps", SelectedFrameRate), ("audio", PlayAudio),
            ("decoder", requestedState.DecoderPreference),
            ("brightness", requestedState.Brightness),
            ("contrast", requestedState.Contrast),
            ("saturation", requestedState.Saturation),
            ("gamma", requestedState.Gamma),
            ("usb_mode", requestedState.UsbProjectionMode)));
        IsBusy = true;
        var gateHeld = false;
        try
        {
            if (requestedDevice is null) return;
            // A user click that lands during the short background poll should
            // run immediately after it, rather than being silently discarded.
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            var device = SelectedDevice;
            if (device is null || !DeviceViewModel.UdidEquals(device.Udid, requestedDevice.Udid)) return;
            // Exact libusb0 verification reaches the native core. Keep it
            // serialized with polling, teardown, and session creation.
            var preflight = await EnsureSourceReadyAsync(device);
            AddDiagnosticLog(AppLog.Event("capture_start_preflight",
                ("device", AppLog.Device(device.Udid)),
                ("success", preflight.Success),
                ("message", preflight.Message)));
            if (!preflight.Success) return;
            var preference = (Success: true, Message: LocalizationService.Get("VideoPreferencesApplied"));
            // Own the session before the native start call can block in USB
            // activation. A device click or window close during that interval
            // must still queue an explicit stop for this exact phone, and the
            // top action changes to its red stop state immediately.
            var state = requestedState;
            if (device.IsWireless) _sessions.SetWirelessPaused(device.Udid, false);
            var startSettings = CaptureSessionStartSettings(state);
            var created = await Task.Run(() => CreateSession(device, startSettings));
            _sessions.SetHandle(state, created.Success ? created.Handle : 0);
            if (created.Success) state.MarkVideoSettingsApplied(
                startSettings.RenderWidth, startSettings.RenderHeight,
                startSettings.FrameRate, startSettings.DecoderPreference,
                startSettings.Brightness, startSettings.Contrast,
                startSettings.Saturation, startSettings.Gamma);
            AddDiagnosticLog(AppLog.Event("capture_start_result",
                ("device", AppLog.Device(device.Udid)),
                ("success", created.Success),
                ("handle", AppLog.Handle(created.Handle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("message", created.Message)));
            // Handle is not observable itself; explicitly refresh the style
            // trigger and command availability as soon as creation finishes.
            OnPropertyChanged(nameof(HasCaptureSession));
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            var result = (created.Success, created.Message);
            NotifyCaptureSessionChanged();
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            {
                IsCapturing = created.Success;
                _activeCaptureUdid = created.Success ? device.Udid : null;
                NativeCore.SelectPreviewSession(state.Handle);
                OnPropertyChanged(nameof(CurrentSessionHandle));
                CaptureStatus = result.Message;
                if (preference.Success)
                    SetSettingsStatus("AppliedRenderFormat", SelectedResolutionPreset, SelectedFrameRate);
                else SetRawSettingsStatus(preference.Message);
            }
            AddUiLog(result.Success
                ? LocalizationService.Get("StartRequested")
                : LocalizationService.Format("StartFailedFormat", result.Message));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("capture_start_failed",
                ("device", AppLog.Device(requestedDevice?.Udid)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            NotifyCaptureSessionChanged();
            var failure = LocalizationService.Format("StartFailedFormat", error.Message);
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedDevice?.Udid))
            {
                _activeCaptureUdid = null;
                IsCapturing = false;
                CaptureStatus = failure;
            }
            AddUiLog(failure);
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
        var requestedDevice = SelectedDevice;
        if (requestedDevice is null || requestedDevice.IsMediaCast) return;
        var requestedUdid = requestedDevice.Udid;
        var requestedState = GetOrCreateDeviceState(requestedDevice);
        var requestedHandle = requestedState.Handle;
        var requestedPreset = SelectedResolutionPreset;
        var requestedFrameRate = SelectedFrameRate;
        var requestedDecoder = requestedState.DecoderPreference;
        var requestedBrightness = requestedState.Brightness;
        var requestedContrast = requestedState.Contrast;
        var requestedSaturation = requestedState.Saturation;
        var requestedGamma = requestedState.Gamma;
        requestedState.RenderWidth = requestedPreset.Width;
        requestedState.RenderHeight = requestedPreset.Height;
        requestedState.FrameRate = requestedFrameRate;
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("video_settings_begin",
            ("device", AppLog.Device(requestedUdid)),
            ("handle", AppLog.Handle(requestedHandle)),
            ("resolution", $"{requestedPreset.Width}x{requestedPreset.Height}"),
            ("fps", requestedFrameRate),
            ("decoder", requestedDecoder),
            ("brightness", requestedBrightness), ("contrast", requestedContrast),
            ("saturation", requestedSaturation), ("gamma", requestedGamma)));

        if (requestedHandle == 0)
        {
            requestedState.MarkVideoSettingsApplied(
                requestedPreset.Width, requestedPreset.Height,
                requestedFrameRate, requestedDecoder, requestedBrightness,
                requestedContrast, requestedSaturation, requestedGamma);
            var savedMessage = LocalizationService.Format("VideoSettingsSavedFormat",
                requestedPreset, requestedFrameRate,
                DecoderPreferenceLabel(requestedDecoder));
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedUdid))
                SetSettingsStatus("VideoSettingsSavedFormat", requestedPreset,
                    requestedFrameRate, DecoderPreferenceLabel(requestedDecoder));
            AddUiLog(savedMessage);
            AddDiagnosticLog(AppLog.Event("video_settings_saved",
                ("device", AppLog.Device(requestedUdid)),
                ("resolution", $"{requestedPreset.Width}x{requestedPreset.Height}"),
                ("fps", requestedFrameRate),
                ("decoder", requestedDecoder),
                ("brightness", requestedBrightness), ("contrast", requestedContrast),
                ("saturation", requestedSaturation), ("gamma", requestedGamma),
                ("elapsed_ms", operation.ElapsedMilliseconds)));
            return;
        }

        IsBusy = true;
        var gateHeld = false;
        var offerReconnect = false;
        var failureMessage = string.Empty;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            if (requestedHandle != 0 &&
                (requestedUdid is null ||
                  !_sessions.TryGet(requestedUdid, out var currentState) ||
                  !ReferenceEquals(currentState, requestedState) ||
                  currentState.Handle != requestedHandle))
                return;

            var pipeline = (Success: true, Message: string.Empty);
            var hasPipelineSettings = !requestedDevice.IsWireless;
            if (hasPipelineSettings)
            {
                pipeline = _core.SetDevicePipelinePreferences(requestedHandle,
                    (uint)requestedDecoder, 1U);
            }
            var adjustments = _core.SetDeviceImageAdjustments(requestedHandle,
                requestedBrightness, requestedContrast, requestedSaturation,
                requestedGamma);
            if (adjustments.Success)
                requestedState.MarkImageAdjustmentsApplied(requestedBrightness,
                    requestedContrast, requestedSaturation, requestedGamma);
            var render = _core.SetDeviceVideoPreferences(requestedHandle,
                requestedPreset.Width, requestedPreset.Height,
                (uint)requestedFrameRate);
            if (render.Success)
            {
                requestedState.MarkRenderSettingsApplied(
                    requestedPreset.Width, requestedPreset.Height,
                    requestedFrameRate);
            }
            var success = pipeline.Success && adjustments.Success && render.Success;
            var targetStillSelected = DeviceViewModel.UdidEquals(
                SelectedDevice?.Udid, requestedUdid);
            failureMessage = string.Join("; ", new[]
            {
                pipeline.Success ? string.Empty : pipeline.Message,
                adjustments.Success ? string.Empty : adjustments.Message,
                render.Success ? string.Empty : render.Message,
            }.Where(message => !string.IsNullOrWhiteSpace(message)));
            AddDiagnosticLog(AppLog.Event("video_settings_result",
                ("device", AppLog.Device(requestedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("success", success),
                ("pipeline_success", pipeline.Success),
                ("adjustments_success", adjustments.Success),
                ("render_success", render.Success),
                ("decoder", requestedDecoder),
                ("brightness", requestedBrightness), ("contrast", requestedContrast),
                ("saturation", requestedSaturation), ("gamma", requestedGamma),
                ("transport_restarted", false),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("message", failureMessage)));
            if (!success)
            {
                if (targetStillSelected)
                    SetSettingsStatus("ApplySettingsFailedFormat", failureMessage);
                offerReconnect = requestedHandle != 0 && requestedDevice is
                    { IsWireless: false, IsMediaCast: false };
            }
            else
            {
                AddUiLog(LocalizationService.Format("AppliedRenderLogFormat",
                    requestedPreset.Label, requestedFrameRate, render.Message));
                if (targetStillSelected)
                {
                    if (requestedDevice is
                        { IsWireless: false, IsMediaCast: false })
                    {
                        SetSettingsStatus("VideoSettingsAppliedFormat", requestedPreset,
                            requestedFrameRate, DecoderPreferenceLabel(requestedDecoder));
                    }
                    else
                    {
                        SetSettingsStatus("AppliedRenderFormat", requestedPreset,
                            requestedFrameRate);
                    }
                }
            }
        }
        catch (Exception error)
        {
            failureMessage = error.Message;
            offerReconnect = requestedHandle != 0 && requestedDevice is
                { IsWireless: false, IsMediaCast: false };
            AddDiagnosticLog(AppLog.Event("video_settings_failed",
                ("device", AppLog.Device(requestedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedUdid))
                SetSettingsStatus("ApplySettingsFailedFormat", error.Message);
        }
        finally
        {
            IsBusy = false;
            if (gateHeld) _coreGate.Release();
        }

        if (!offerReconnect || _disposed || requestedDevice is null ||
            requestedState is null || requestedState.Handle != requestedHandle ||
            !DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedUdid)) return;

        var reconnectBody = LocalizationService.Format("VideoSettingsReconnectBodyFormat",
            failureMessage, DecoderPreferenceLabel(requestedDecoder));
        if (!AppPromptWindow.Confirm(
                LocalizationService.Get("VideoSettingsReconnectTitle"), reconnectBody))
        {
            SetSettingsStatus("VideoSettingsReconnectCancelled");
            AddDiagnosticLog(AppLog.Event("video_settings_reconnect_cancelled",
                ("device", AppLog.Device(requestedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("error", failureMessage)));
            return;
        }

        // ShowDialog pumps dispatcher work. The original session can disappear
        // or be replaced while the confirmation window is open; a stale answer
        // must never restart a newer handle or a device that is no longer selected.
        if (_disposed || IsBusy || requestedUdid is null ||
            !_sessions.TryGet(requestedUdid, out var confirmedState) ||
            !ReferenceEquals(confirmedState, requestedState) ||
            confirmedState.Handle != requestedHandle ||
            confirmedState.RenderWidth != requestedPreset.Width ||
            confirmedState.RenderHeight != requestedPreset.Height ||
            confirmedState.FrameRate != requestedFrameRate ||
            confirmedState.DecoderPreference != requestedDecoder ||
            Math.Abs(confirmedState.Brightness - requestedBrightness) > 0.001 ||
            Math.Abs(confirmedState.Contrast - requestedContrast) > 0.001 ||
            Math.Abs(confirmedState.Saturation - requestedSaturation) > 0.001 ||
            Math.Abs(confirmedState.Gamma - requestedGamma) > 0.001 ||
            !DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedUdid))
        {
            AddDiagnosticLog(AppLog.Event("video_settings_reconnect_stale",
                ("device", AppLog.Device(requestedUdid)),
                ("expected_handle", AppLog.Handle(requestedHandle)),
                ("current_handle", AppLog.Handle(requestedState.Handle)),
                ("selected", AppLog.Device(SelectedDevice?.Udid)),
                ("busy", IsBusy)));
            return;
        }

        AddDiagnosticLog(AppLog.Event("video_settings_reconnect_confirmed",
            ("device", AppLog.Device(requestedUdid)),
            ("handle", AppLog.Handle(requestedHandle)),
            ("decoder", requestedDecoder),
            ("brightness", requestedBrightness), ("contrast", requestedContrast),
            ("saturation", requestedSaturation), ("gamma", requestedGamma)));
        SetSettingsStatus("VideoPipelineRestarting");
        await RestartUsbSessionAsync(requestedDevice, requestedState, "video_settings");
    }

    private async Task StopAsync()
    {
        if (_disposed || !HasCaptureSession || IsBusy) return;
        var requestedState = CurrentDeviceSession;
        var requestedHandle = requestedState?.Handle ?? 0;
        if (requestedState is null || requestedHandle == 0) return;
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("capture_stop_begin",
            ("device", AppLog.Device(requestedState.Udid)),
            ("handle", AppLog.Handle(requestedHandle)),
            ("wireless", DeviceViewModel.IsWirelessUdid(requestedState.Udid))));
        IsBusy = true;
        var gateHeld = false;
        DeviceCaptureState? stoppedState = null;
        CaptureStatus = LocalizationService.Get("CaptureStopping");
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            // Native stop waits for USB release packets and configuration
            // restore. Keep that wait off the WPF UI thread.
            if (!_sessions.TryGet(requestedState.Udid, out var currentState) ||
                !ReferenceEquals(currentState, requestedState) ||
                currentState.Handle != requestedHandle)
            {
                if (DeviceViewModel.UdidEquals(
                    SelectedDevice?.Udid, requestedState.Udid) &&
                    currentState is not { Handle: not 0 })
                {
                    ClearSelectedSessionState(requestedState.Udid);
                    CaptureStatus = LocalizationService.Get("CaptureStopped");
                }
                else if (currentState is { Handle: not 0 } &&
                    DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedState.Udid))
                {
                    _activeCaptureUdid = currentState.Udid;
                    IsCapturing = true;
                    NativeCore.SelectPreviewSession(currentState.Handle);
                    NotifyCaptureSessionChanged();
                    OnPropertyChanged(nameof(CurrentSessionHandle));
                    CaptureStatus = LocalizationService.Get("CaptureStreaming");
                }
                return;
            }
            stoppedState = requestedState;
            var stoppedUdid = stoppedState.Udid;
            await _sessions.StopAndDestroyAsync(stoppedState);
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
            NotifyCaptureSessionChanged();
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, stoppedUdid))
            {
                ClearSelectedSessionState(stoppedUdid);
                CaptureStatus = LocalizationService.Get("CaptureStopped");
            }
            AddUiLog(LocalizationService.Get("StopSessionReleased"));
            AddDiagnosticLog(AppLog.Event("capture_stop_complete",
                ("device", AppLog.Device(stoppedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("success", true)));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("capture_stop_failed",
                ("device", AppLog.Device(requestedState.Udid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            NotifyCaptureSessionChanged();
            var failure = LocalizationService.Format("StopFailedFormat", error.Message);
            if (stoppedState is not null && stoppedState.Handle == 0 &&
                DeviceViewModel.UdidEquals(SelectedDevice?.Udid, stoppedState.Udid))
            {
                ClearSelectedSessionState(stoppedState.Udid);
                CaptureStatus = failure;
            }
            AddUiLog(failure);
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
        IReadOnlyList<string> lines;
        try
        {
            lines = await _logReader.ReadNewLinesAsync();
        }
        catch (Exception error)
        {
            // This method is invoked by a DispatcherTimer without awaiting its
            // task. A transient log-file failure must never surface as an
            // unobserved exception on the UI thread.
            var failure = AppLog.Error(error);
            if (!string.Equals(_lastLogReadError, failure, StringComparison.Ordinal))
                AddDiagnosticLog(AppLog.Event("log_tail_read_failed",
                    ("error", failure)));
            _lastLogReadError = failure;
            return;
        }
        _lastLogReadError = null;
        if (_disposed) return;
        var added = 0;
        foreach (var line in lines)
        {
            // UI events are inserted immediately below and also persisted by
            // the native logger. Suppress their tail copy to avoid duplicates
            // while retaining them in the diagnostic file.
            if (NativeLogTailReader.IsUiEventLine(line)) continue;
            AddLogLine(AppLog.Sanitize(line));
            ++added;
        }
        if (added != 0) PublishLogText();
    }

    public void RefreshMediaCast()
    {
        if (_disposed) return;
        try
        {
            var receiver = _core.GetMediaCastReceiverStatus();
            _lastMediaPollError = null;
            if (!receiver.Running || !receiver.Ready)
            {
                if (_isMediaCasting && !receiver.Running)
                {
                    AddDiagnosticLog(AppLog.Event("media_receiver_lost",
                        ("local_playback_stopping", true)));
                    MediaCastStopRequested?.Invoke();
                }
                else if (!receiver.Running)
                {
                    for (var index = 0; index < 64; index++)
                        if (_core.GetMediaCastRequest() is null) break;
                }
                return;
            }

            // The native side retains a bounded FIFO. Drain it in one dispatcher
            // tick so Play followed immediately by Seek/Pause cannot lose Play or
            // introduce a visible quarter-second delay per control.
            var drained = 0;
            for (var index = 0; index < 64; index++)
            {
                var request = _core.GetMediaCastRequest();
                if (request is null) break;
                if (request.CommandId == _lastMediaCastCommandId) continue;
                _lastMediaCastCommandId = request.CommandId;
                ++drained;
                MediaCastCommandReceived?.Invoke(request);
            }
            if (drained != 0)
                AddDiagnosticLog(AppLog.Event("media_command_queue_drained",
                    ("count", drained), ("last_command", _lastMediaCastCommandId)));
        }
        catch (Exception error)
        {
            var failure = AppLog.Error(error);
            if (!string.Equals(_lastMediaPollError, failure, StringComparison.Ordinal))
            {
                _lastMediaPollError = failure;
                AddDiagnosticLog(AppLog.Event("media_poll_failed", ("error", failure)));
            }
        }
    }

    internal void BeginMediaCast(double volume)
    {
        _mediaCastPlaybackVolume = double.IsFinite(volume)
            ? Math.Clamp(volume * 100.0, 0, 100) : 100;
        _mediaCastPlayAudio = true;
        var isNewSession = !_isMediaCasting;
        AddDiagnosticLog(AppLog.Event("media_cast_begin",
            ("new_session", isNewSession),
            ("volume", _mediaCastPlaybackVolume),
            ("previous_selection", AppLog.Device(SelectedDevice?.Udid))));
        if (isNewSession)
        {
            _selectionBeforeMediaCast = SelectedDevice?.Udid;
            _isMediaCasting = true;
            _mediaCastDevice ??= DeviceViewModel.CreateMediaCast();
            if (!Devices.Contains(_mediaCastDevice)) Devices.Insert(0, _mediaCastDevice);
            OnPropertyChanged(nameof(IsMediaCasting));
            OnPropertyChanged(nameof(DeviceCount));
            OnPropertyChanged(nameof(TargetResolutionDisplay));
            OnPropertyChanged(nameof(TargetFpsDisplay));
            OnPropertyChanged(nameof(AudioDetailDisplay));
            MediaCastStopCommand.NotifyCanExecuteChanged();
        }
        // Select the virtual source when a cast first arrives, but do not take
        // the selection back from the user when the sender later publishes a
        // new Play request (for example after Pause/Seek or changing videos).
        if (isNewSession)
            SetSelectedDevice(_mediaCastDevice, updateDriverStatus: false);
        _mediaCastWidth = _mediaCastHeight = 0;
        _mediaCastAudioEnabled = _mediaCastPlaybackVolume > 0;
        if (IsMediaCastSelected)
        {
            OnPropertyChanged(nameof(PlaybackVolume));
            OnPropertyChanged(nameof(PlayAudio));
        }
        if (IsMediaCastSelected) ApplyMediaCastStatistics();
    }

    internal void UpdateMediaCastStatistics(uint width, uint height, bool audioEnabled)
    {
        if (!_isMediaCasting) return;
        var dimensionsChanged = width > 0 && height > 0 &&
            (width != _mediaCastWidth || height != _mediaCastHeight);
        if (width > 0 && height > 0)
        {
            _mediaCastWidth = width;
            _mediaCastHeight = height;
        }
        _mediaCastAudioEnabled = audioEnabled;
        if (dimensionsChanged)
            AddDiagnosticLog(AppLog.Event("media_cast_dimensions",
                ("size", $"{width}x{height}"), ("audio", audioEnabled)));
        if (IsMediaCastSelected) ApplyMediaCastStatistics();
    }

    internal void UpdateMediaCastAudioControls(bool enabled, double volume)
    {
        _mediaCastPlayAudio = enabled;
        _mediaCastPlaybackVolume = double.IsFinite(volume)
            ? Math.Clamp(volume * 100.0, 0, 100) : _mediaCastPlaybackVolume;
        _mediaCastAudioEnabled = enabled && _mediaCastPlaybackVolume > 0;
        if (!IsMediaCastSelected) return;
        OnPropertyChanged(nameof(PlayAudio));
        OnPropertyChanged(nameof(PlaybackVolume));
        ApplyMediaCastStatistics();
    }

    internal void EndMediaCast()
    {
        if (!_isMediaCasting) return;
        AddDiagnosticLog(AppLog.Event("media_cast_end",
            ("selection", AppLog.Device(SelectedDevice?.Udid)),
            ("size", $"{_mediaCastWidth}x{_mediaCastHeight}"),
            ("audio", _mediaCastAudioEnabled)));
        _isMediaCasting = false;
        OnPropertyChanged(nameof(IsMediaCasting));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
        MediaCastStopCommand.NotifyCanExecuteChanged();

        var restore = SelectedDevice is { IsMediaCast: false } current
            ? current
            : Devices.FirstOrDefault(device => !device.IsMediaCast &&
                DeviceViewModel.UdidEquals(device.Udid, _selectionBeforeMediaCast))
            ?? Devices.FirstOrDefault(device => !device.IsMediaCast);
        SetSelectedDevice(restore, updateDriverStatus: false);
        if (_mediaCastDevice is not null && Devices.Remove(_mediaCastDevice))
            OnPropertyChanged(nameof(DeviceCount));
        _selectionBeforeMediaCast = null;
        _mediaCastWidth = _mediaCastHeight = 0;
        if (restore is null || !HasCaptureSession) ResetPreviewState();
        else if (_lastCaptureStatus is { } status &&
            _lastCaptureStatusHandle == CurrentSessionHandle) ApplyCaptureStatus(status);
    }

    private void ApplyMediaCastStatistics()
    {
        Resolution = _mediaCastWidth > 0 && _mediaCastHeight > 0
            ? $"{_mediaCastWidth}×{_mediaCastHeight}" : "—";
        if (_sourceVideoWidth != _mediaCastWidth || _sourceVideoHeight != _mediaCastHeight)
        {
            _sourceVideoWidth = _mediaCastWidth;
            _sourceVideoHeight = _mediaCastHeight;
            OnPropertyChanged(nameof(SourceVideoWidth));
            OnPropertyChanged(nameof(SourceVideoHeight));
        }
        FpsDisplay = LocalizationService.Format("MediaCastFpsDisplayFormat",
            _wireless.AppliedProfile.FrameRate);
        LatencyDisplay = LocalizationService.Get("MediaCastNetworkStream");
        AudioDisplay = LocalizationService.Get(_mediaCastAudioEnabled
            ? "MediaCastAudioActive" : "MediaCastAudioMuted");
        CaptureStatus = LocalizationService.Get("MediaCastDeviceActive");
    }

    internal void ReportMediaCastPlayback(ulong commandId,
        double duration, double position, double rate)
    {
        var accepted = _core.SetMediaCastPlaybackState(commandId, duration, position, rate);
        if (!accepted)
            AddDiagnosticLog(AppLog.Event("media_playback_state_rejected",
                ("command", commandId), ("duration", duration.ToString("F3")),
                ("position", position.ToString("F3")), ("rate", rate.ToString("F2"))));
    }

    internal void RequestMediaCastStop(bool allowInactive = false)
    {
        if (!IsMediaCasting && !allowInactive) return;
        AddDiagnosticLog(AppLog.Event("media_stop_requested", ("source", "ui")));
        try
        {
            var result = _core.RequestMediaCastStop();
            AddDiagnosticLog(AppLog.Event("media_stop_request_result",
                ("success", result.Success), ("message", result.Message)));
            if (!result.Success)
                AddUiLog(LocalizationService.Format(
                    "MediaCastStopRequestFailedFormat", result.Message));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("media_stop_request_failed",
                ("error", AppLog.Error(error))));
            AddUiLog(LocalizationService.Format(
                "MediaCastStopRequestFailedFormat", AppLog.Error(error.Message)));
        }
        finally
        {
            // Local playback must still stop when the receiver process exits
            // between the click and the IPC write.
            try { MediaCastStopRequested?.Invoke(); }
            catch (Exception error)
            {
                AddDiagnosticLog(AppLog.Event("media_local_stop_failed",
                    ("error", AppLog.Error(error))));
            }
        }
    }

    internal void AddUiLog(string message)
    {
        var safeMessage = AppLog.Message(message);
        if (string.IsNullOrWhiteSpace(safeMessage)) return;
        try { _ = _core.WriteLog($"action {safeMessage}"); }
        catch { /* Logging must not break a user action or shutdown path. */ }
        AddLogLine($"{DateTime.Now:HH:mm:ss.fff} [UI] {safeMessage}");
        PublishLogText();
    }

    internal void AddDiagnosticLog(string message)
    {
        var safeMessage = AppLog.Message(message);
        if (!string.IsNullOrWhiteSpace(safeMessage))
        {
            try { _ = _core.WriteLog($"diagnostic {safeMessage}"); }
            catch { /* Diagnostics are best-effort during native teardown. */ }
        }
    }

    internal bool IsDeviceAudioEnabled(string udid) =>
        _sessions.TryGet(udid, out var state) &&
        state.Handle != 0 && state.PlayAudio;

    internal int ActiveDeviceSessionCount =>
        _sessions.Values.Count(state => state.Handle != 0);

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

    internal async Task<(bool Success, ulong Handle, bool Created, string Message)> StartBackgroundSessionAsync(
        DeviceViewModel device)
    {
        if (_disposed)
            return (false, 0, false, LocalizationService.Get("CaptureStopped"));

        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("independent_session_begin",
            ("device", AppLog.Device(device.Udid)),
            ("kind", device.IsWireless ? "wireless" : "wired")));

        // Reusing an active session must not enumerate USB again. During
        // QuickTime capture the device can temporarily disappear from normal
        // enumeration even though its existing native handle remains valid.
        await _coreGate.WaitAsync();
        try
        {
            if (_disposed)
                return (false, 0, false, LocalizationService.Get("CaptureStopped"));
            if (_sessions.TryGet(device.Udid, out var existing) && existing.Handle != 0)
            {
                AddDiagnosticLog(AppLog.Event("independent_session_reused",
                    ("device", AppLog.Device(device.Udid)),
                    ("handle", AppLog.Handle(existing.Handle)),
                    ("elapsed_ms", operation.ElapsedMilliseconds)));
                return (true, existing.Handle, false, string.Empty);
            }
            var preflight = await EnsureSourceReadyAsync(device);
            if (!preflight.Success)
            {
                AddDiagnosticLog(AppLog.Event("independent_session_preflight_failed",
                    ("device", AppLog.Device(device.Udid)),
                    ("elapsed_ms", operation.ElapsedMilliseconds),
                    ("message", preflight.Message)));
                return (false, 0, false, preflight.Message);
            }
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
            var startSettings = CaptureSessionStartSettings(state);
            var result = await Task.Run(() => CreateSession(device, startSettings));
            _sessions.SetHandle(state, result.Success ? result.Handle : 0);
            if (result.Success) state.MarkVideoSettingsApplied(
                startSettings.RenderWidth, startSettings.RenderHeight,
                startSettings.FrameRate, startSettings.DecoderPreference,
                startSettings.Brightness, startSettings.Contrast,
                startSettings.Saturation, startSettings.Gamma);
            AddDiagnosticLog(AppLog.Event("independent_session_result",
                ("device", AppLog.Device(device.Udid)),
                ("success", result.Success), ("created", result.Success),
                ("handle", AppLog.Handle(result.Handle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("message", result.Message)));
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            {
                IsCapturing = result.Success;
                _activeCaptureUdid = result.Success ? device.Udid : null;
                NotifyCaptureSessionChanged();
                NativeCore.SelectPreviewSession(state.Handle);
                OnPropertyChanged(nameof(CurrentSessionHandle));
            }
            return (result.Success, result.Handle, result.Success, result.Message);
        }
        finally { _coreGate.Release(); }
    }

    internal async Task StopDeviceSessionAsync(string udid, ulong expectedHandle = 0,
        bool preserveIfSelected = false)
    {
        if (_disposed) return;
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("independent_session_stop_begin",
            ("device", AppLog.Device(udid)),
            ("expected", AppLog.Handle(expectedHandle)),
            ("preserve_selected", preserveIfSelected)));
        await _coreGate.WaitAsync();
        DeviceCaptureState? state = null;
        try
        {
            if (_disposed || !_sessions.TryGet(udid, out state) || state.Handle == 0 ||
                expectedHandle != 0 && state.Handle != expectedHandle)
                return;
            if (preserveIfSelected &&
                DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
                return;
            await _sessions.StopAndDestroyAsync(state);
            AddDiagnosticLog(AppLog.Event("independent_session_stop_complete",
                ("device", AppLog.Device(udid)),
                ("elapsed_ms", operation.ElapsedMilliseconds), ("success", true)));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("independent_session_stop_failed",
                ("device", AppLog.Device(udid)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            throw;
        }
        finally
        {
            if (state is not null && state.Handle == 0)
                ClearSelectedSessionState(udid);
            _coreGate.Release();
        }
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
        UpdateVideoOutputStatus();
        _lastCaptureStatus = status;
        _lastCaptureStatusHandle = CurrentSessionHandle;
        var statusChanged = _lastLoggedCaptureHandle != CurrentSessionHandle ||
            _lastLoggedCaptureState != status.State;
        if (statusChanged)
        {
            _lastLoggedCaptureHandle = CurrentSessionHandle;
            _lastLoggedCaptureState = status.State;
            AddDiagnosticLog(AppLog.Event("capture_state",
                ("device", AppLog.Device(SelectedDevice?.Udid)),
                ("handle", AppLog.Handle(CurrentSessionHandle)),
                ("state", status.State),
                ("size", $"{status.Width}x{status.Height}"),
                ("fps", status.Fps.ToString("F2")),
                ("latency_ms", status.LatencyMs.ToString("F1")),
                ("video_frames", status.VideoFrames),
                ("audio_packets", status.AudioPackets),
                ("message", status.Message)));
        }
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

    private void UpdateVideoOutputStatus()
    {
        var handle = CurrentSessionHandle;
        if (handle == 0 || SelectedDevice is not
            { IsWireless: false, IsMediaCast: false })
        {
            DecoderStatus = string.Empty;
            _lastVideoOutputSignature = null;
            return;
        }
        if (!_core.TryGetDeviceVideoOutputStatus(handle, out var status))
        {
            DecoderStatus = LocalizationService.Get("DecoderStatusDetecting");
            return;
        }

        var requestedDecoder = (DecoderPreference)Math.Min(
            status.RequestedDecoderPreference, 2U);
        var appliedDecoder = (DecoderPreference)Math.Min(
            status.AppliedDecoderPreference, 2U);
        var decoderState = status.DecoderSwitchState is
            DecoderSwitchState.Applied or DecoderSwitchState.Pending or
            DecoderSwitchState.Failed
                ? status.DecoderSwitchState
                : DecoderSwitchState.Pending;
        var runtimeMode = status.DecoderRuntimeMode is
            DecoderRuntimeMode.Hardware or DecoderRuntimeMode.Software
                ? status.DecoderRuntimeMode
                : DecoderRuntimeMode.Unknown;
        if (CurrentDeviceSession is { } state && state.Handle == handle)
        {
            var wasPending = state.HasPendingVideoSettings;
            state.SynchronizeAppliedDecoderPreference(appliedDecoder);
            if (wasPending != state.HasPendingVideoSettings)
                RestoreSelectedSettingsStatus(state);
        }

        var signature = $"{handle}:{requestedDecoder}:" +
            $"{appliedDecoder}:{decoderState}:{runtimeMode}:" +
            $"{status.RequestedDecoderGeneration}:" +
            $"{status.AppliedDecoderGeneration}";
        if (!string.Equals(signature, _lastVideoOutputSignature,
                StringComparison.Ordinal))
        {
            _lastVideoOutputSignature = signature;
            AddDiagnosticLog(AppLog.Event("video_output_status",
                ("device", AppLog.Device(SelectedDevice?.Udid)),
                ("handle", AppLog.Handle(handle)),
                ("decoder_requested", requestedDecoder),
                ("decoder_applied", appliedDecoder),
                ("decoder_state", decoderState),
                ("decoder_runtime", runtimeMode),
                ("decoder_requested_generation", status.RequestedDecoderGeneration),
                ("decoder_applied_generation", status.AppliedDecoderGeneration)));
        }

        var decoderStatus = decoderState switch
        {
            DecoderSwitchState.Applied => LocalizationService.Format(
                "DecoderStatusAppliedFormat", DecoderPreferenceLabel(appliedDecoder),
                DecoderRuntimeModeLabel(runtimeMode)),
            DecoderSwitchState.Failed => LocalizationService.Format(
                "DecoderStatusFailedFormat", DecoderPreferenceLabel(appliedDecoder),
                DecoderRuntimeModeLabel(runtimeMode),
                DecoderPreferenceLabel(requestedDecoder)),
            _ => LocalizationService.Format("DecoderStatusPendingFormat",
                DecoderPreferenceLabel(appliedDecoder),
                DecoderPreferenceLabel(requestedDecoder),
                DecoderRuntimeModeLabel(runtimeMode)),
        };
        DecoderStatus = decoderStatus;
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
        var revision = Interlocked.Increment(ref _driverProbeRevision);
        if (IsWirelessSelected)
        {
            RefreshWirelessStatus();
            return;
        }
        if (SelectedDevice is not { IsMediaCast: false } selected) return;

        var status = _filterDriver.Inspect(selected.Udid);
        if (status.State == IPhoneFilterDriverState.Provisional &&
            _libUsbProbeCache.TryGetValue(selected.Udid, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt <= LibUsbProbeCacheLifetime)
            status = _filterDriver.Inspect(selected.Udid, cached.Available);
        _filterDriverStatus = status;
        if (_lastEnvironment is { } environment)
            UpdateEnvironmentStatus(environment);
        else
            ApplySelectedDriverState();

        if (status.State == IPhoneFilterDriverState.Provisional &&
            _libUsbProbesInFlight.Add(selected.Udid))
            _ = VerifySelectedDriverStatusAsync(selected.Udid, revision);
    }

    private async Task VerifySelectedDriverStatusAsync(string udid, long revision)
    {
        var gateHeld = false;
        try
        {
            await _coreGate.WaitAsync(_shutdownCancellation.Token);
            gateHeld = true;
            if (_disposed) return;
            var available = await Task.Run(() => _core.IsLibUsb0DeviceAvailable(udid));
            _libUsbProbeCache[udid] = (DateTimeOffset.UtcNow, available);
            if (_disposed || revision != Interlocked.Read(ref _driverProbeRevision) ||
                !DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid)) return;

            _filterDriverStatus = _filterDriver.Inspect(udid, available);
            if (_lastEnvironment is { } environment)
                UpdateEnvironmentStatus(environment);
            else
                ApplySelectedDriverState();
            AddDiagnosticLog(AppLog.Event("driver_exact_probe_complete",
                ("device", AppLog.Device(udid)), ("available", available)));
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("driver_exact_probe_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
        finally
        {
            if (gateHeld) _coreGate.Release();
            _libUsbProbesInFlight.Remove(udid);
        }
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

    private async Task<(bool Success, string Message)> EnsureSourceReadyAsync(
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
        // libusb0's legacy serial enumeration is process-global. When another
        // phone already has an active capture handle, rely on the per-device
        // registry state here and let native session creation perform its own
        // authoritative open without disturbing the live session.
        var driverStatus = await Task.Run(() => _sessions.AnySession
            ? _filterDriver.Inspect(device.Udid)
            : InspectDriverStatus(device, requireExactBackend: true));
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
        var signature = $"{_wireless.IsAvailable}:{_wireless.Running}:{_wireless.Ready}:" +
            AppLog.Sanitize(_wireless.StartError);
        if (!string.Equals(_lastWirelessStatusSignature, signature,
                StringComparison.Ordinal))
        {
            _lastWirelessStatusSignature = signature;
            AddDiagnosticLog(AppLog.Event("wireless_receiver_state",
                ("available", _wireless.IsAvailable),
                ("running", _wireless.Running),
                ("ready", _wireless.Ready),
                ("profile", _wireless.AppliedProfile.Label),
                ("error", AppLog.Error(_wireless.StartError))));
        }
        if (IsWirelessSelected) ApplySelectedDriverState();
    }

    private void RefreshMediaCastStatus()
    {
        MediaCastStatus = _mediaCast.GetStatusText();
        var signature = $"{_mediaCast.IsAvailable}:{_mediaCast.Running}:{_mediaCast.Ready}:" +
            AppLog.Sanitize(MediaCastStatus);
        if (!string.Equals(_lastMediaCastStatusSignature, signature,
                StringComparison.Ordinal))
        {
            _lastMediaCastStatusSignature = signature;
            AddDiagnosticLog(AppLog.Event("media_receiver_state",
                ("available", _mediaCast.IsAvailable),
                ("running", _mediaCast.Running),
                ("ready", _mediaCast.Ready),
                ("error", AppLog.Error(MediaCastStatus))));
        }
    }

    private SessionStartSettings CaptureSessionStartSettings(
        DeviceCaptureState state) => new(
            state.RenderWidth,
            state.RenderHeight,
            state.FrameRate,
            state.PlayAudio,
            state.Volume,
            IsAdvancedMode ? state.AdvancedUsbWidth : 0,
            IsAdvancedMode ? state.AdvancedUsbHeight : 0,
            state.UsbProjectionMode,
            state.DecoderPreference,
            state.Brightness,
            state.Contrast,
            state.Saturation,
            state.Gamma);

    private (bool Success, ulong Handle, string Message) CreateSession(
        DeviceViewModel device, SessionStartSettings settings)
    {
        if (device.IsWireless)
        {
            return _core.CreateWirelessSession(device.Udid,
                settings.RenderWidth, settings.RenderHeight,
                (uint)settings.FrameRate, settings.PlayAudio,
                settings.Volume / 100.0);
        }
        var created = _core.CreateDeviceSession(device.Udid,
            settings.RenderWidth, settings.RenderHeight,
            (uint)settings.FrameRate, settings.PlayAudio,
            settings.Volume / 100.0,
            settings.AdvancedUsbWidth, settings.AdvancedUsbHeight,
            (uint)settings.UsbProjectionMode,
            (uint)settings.DecoderPreference,
            1U);
        if (!created.Success) return created;
        var adjustments = _core.SetDeviceImageAdjustments(created.Handle,
            settings.Brightness, settings.Contrast, settings.Saturation,
            settings.Gamma);
        if (adjustments.Success) return created;
        try { _core.StopDeviceSession(created.Handle); } catch { }
        _core.DestroyDeviceSession(created.Handle);
        return (false, 0, adjustments.Message);
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

    private void RestoreSelectedVideoControls(DeviceCaptureState? state)
    {
        var frameRate = state?.FrameRate ?? 60;
        if (_selectedFrameRate != frameRate)
        {
            _selectedFrameRate = frameRate;
            OnPropertyChanged(nameof(SelectedFrameRate));
            OnPropertyChanged(nameof(TargetFpsDisplay));
        }

        var preset = state is null ? ResolutionPresets[0] :
            ResolutionPresets.FirstOrDefault(candidate =>
                candidate.Width == state.RenderWidth &&
                candidate.Height == state.RenderHeight) ?? ResolutionPresets[0];
        if (ReferenceEquals(_selectedResolutionPreset, preset)) return;
        _selectedResolutionPreset = preset;
        OnPropertyChanged(nameof(SelectedResolutionPreset));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
    }

    private void RestoreSelectedSettingsStatus(DeviceCaptureState? state)
    {
        if (state is null)
        {
            SetSettingsStatus("StatusDefaultSettings");
            return;
        }
        if (state.HasPendingVideoSettings)
        {
            SetPendingVideoSettingsStatus(state);
            return;
        }

        SetSettingsStatus(state.Handle == 0
                ? "VideoSettingsSavedFormat" : "VideoSettingsAppliedFormat",
            SelectedResolutionPreset, SelectedFrameRate,
            DecoderPreferenceLabel(state.DecoderPreference));
    }

    private string DecoderPreferenceLabel(DecoderPreference preference) =>
        DecoderPreferences.FirstOrDefault(option => option.Preference == preference)?.Label ??
        preference.ToString();

    private static string DecoderRuntimeModeLabel(DecoderRuntimeMode mode) =>
        LocalizationService.Get(mode switch
        {
            DecoderRuntimeMode.Hardware => "DecoderRuntimeHardware",
            DecoderRuntimeMode.Software => "DecoderRuntimeSoftware",
            _ => "DecoderRuntimeUnknown",
        });

    private void SetPendingVideoSettingsStatus(DeviceCaptureState? state)
    {
        var decoder = state?.DecoderPreference ?? DecoderPreference.Auto;
        SetSettingsStatus("PendingVideoSettingsFormat", SelectedResolutionPreset,
            SelectedFrameRate, DecoderPreferenceLabel(decoder));
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
        foreach (var preference in DecoderPreferences) preference.NotifyLanguageChanged();

        foreach (var device in Devices) device.NotifyLanguageChanged();

        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
        OnPropertyChanged(nameof(AppliedWirelessProfileDisplay));
        if (_lastEnvironment is { } environment) UpdateEnvironmentStatus(environment);
        else
        {
            EnvironmentStatus = LocalizationService.Get("StatusCheckingEnvironment");
            DriverState = LocalizationService.Get("StatusDetecting");
        }
        if (IsMediaCastSelected) ApplyMediaCastStatistics();
        else if (_lastCaptureStatus is { } capture &&
            _lastCaptureStatusHandle == CurrentSessionHandle) ApplyCaptureStatus(capture);
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

    private void ShowImageSettings()
    {
        var device = SelectedDevice;
        if (device is null || device.IsMediaCast || IsBusy) return;
        _ = GetOrCreateDeviceState(device);
        ShowImageSettings(device.Udid);
    }

    internal void ShowImageSettings(string udid)
    {
        if (_disposed || IsBusy || string.IsNullOrWhiteSpace(udid) ||
            DeviceViewModel.IsMediaCastUdid(udid)) return;
        if (_imageSettingsWindows.TryGetValue(udid, out var existing))
        {
            existing.Activate();
            existing.Focus();
            return;
        }
        if (!_sessions.TryGet(udid, out var state)) return;
        var original = new ImageAdjustmentValues(state.Brightness, state.Contrast,
            state.Saturation, state.Gamma);
        var window = new ImageSettingsWindow(original,
            values => PreviewImageAdjustments(udid, values),
            values => SaveImageAdjustments(udid, values),
            values => RevertImageAdjustments(udid, values))
        {
            Owner = Application.Current?.MainWindow,
            Topmost = true,
        };
        _imageSettingsWindows[udid] = window;
        window.Closed += (_, _) =>
        {
            if (_imageSettingsWindows.TryGetValue(udid, out var tracked) &&
                ReferenceEquals(tracked, window))
                _imageSettingsWindows.Remove(udid);
        };
        AddDiagnosticLog(AppLog.Event("image_adjustments_window_opened",
            ("device", AppLog.Device(udid)), ("handle", AppLog.Handle(state.Handle))));
        window.Show();
    }

    private (bool Success, string Message) PreviewImageAdjustments(
        string udid, ImageAdjustmentValues values)
    {
        if (_disposed || !_sessions.TryGet(udid, out var state) || state.IsStopping)
            return (false, LocalizationService.Get("ImageAdjustmentsUpdateFailed"));
        if (state.Handle == 0) return (true, string.Empty);
        return _core.SetDeviceImageAdjustments(state.Handle,
            values.Brightness, values.Contrast, values.Saturation, values.Gamma);
    }

    private (bool Success, string Message) SaveImageAdjustments(
        string udid, ImageAdjustmentValues values)
    {
        if (_disposed || !_sessions.TryGet(udid, out var state) || state.IsStopping)
            return (false, LocalizationService.Get("ImageAdjustmentsUpdateFailed"));
        var hadSession = state.Handle != 0;
        var result = PreviewImageAdjustments(udid, values);
        if (!result.Success)
        {
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
                SetSettingsStatus("ApplySettingsFailedFormat", result.Message);
            return result;
        }
        state.Brightness = values.Brightness;
        state.Contrast = values.Contrast;
        state.Saturation = values.Saturation;
        state.Gamma = values.Gamma;
        state.MarkImageAdjustmentsApplied(values.Brightness, values.Contrast,
            values.Saturation, values.Gamma);
        var statusKey = hadSession
            ? "ImageAdjustmentsApplied" : "ImageAdjustmentsSaved";
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
            SetSettingsStatus(statusKey);
        AddUiLog(LocalizationService.Get(statusKey));
        AddDiagnosticLog(AppLog.Event("image_adjustments_saved",
            ("device", AppLog.Device(udid)), ("handle", AppLog.Handle(state.Handle)),
            ("brightness", values.Brightness), ("contrast", values.Contrast),
            ("saturation", values.Saturation), ("gamma", values.Gamma),
            ("applied_live", hadSession)));
        return (true, LocalizationService.Get(statusKey));
    }

    private (bool Success, string Message) RevertImageAdjustments(
        string udid, ImageAdjustmentValues original)
    {
        var result = !_sessions.TryGet(udid, out var state) || state.IsStopping ||
            state.Handle == 0
            ? (Success: true, Message: string.Empty)
            : _core.SetDeviceImageAdjustments(state.Handle,
                original.Brightness, original.Contrast,
                original.Saturation, original.Gamma);
        AddDiagnosticLog(AppLog.Event("image_adjustments_reverted",
            ("device", AppLog.Device(udid)), ("success", result.Success),
            ("brightness", original.Brightness), ("contrast", original.Contrast),
            ("saturation", original.Saturation), ("gamma", original.Gamma),
            ("message", result.Success ? string.Empty : result.Message)));
        if (!result.Success && DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
            SetSettingsStatus("ApplySettingsFailedFormat", result.Message);
        return result;
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
            AddUiLog(AppLog.Event("advanced usb request saved",
                ("size", $"{window.RequestedWidth}x{window.RequestedHeight}"),
                ("device", AppLog.Device(state.Udid))));
            if (state.Handle != 0)
                _ = RestartUsbSessionAsync(device, state, "usb_display");
        }
        if (window.DisableAdvancedModeRequested)
        {
            IsAdvancedMode = false;
            AdvancedSettingsCommand.NotifyCanExecuteChanged();
            state.AdvancedUsbWidth = state.AdvancedUsbHeight = 0;
            SetRawSettingsStatus(LocalizationService.Get("AdvancedModeDisabled"));
            if (state.Handle != 0)
                _ = RestartUsbSessionAsync(device, state, "usb_display");
        }
    }

    private async Task RestartUsbSessionAsync(DeviceViewModel device,
        DeviceCaptureState state, string reason)
    {
        if (_disposed || device.IsWireless || IsBusy || state.Handle == 0) return;
        IsBusy = true;
        var startSettings = CaptureSessionStartSettings(state);
        var gateHeld = false;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            await _sessions.StopAndDestroyAsync(state);
            ClearSelectedSessionState(state.Udid);
            // libusb0 restores the phone's normal configuration during the
            // stop path. Give Windows and the Apple USB stack a complete
            // re-enumeration window before opening QuickTime again.
            Exception? lastFailure = null;
            (bool Success, ulong Handle, string Message) created = (false, 0, "");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(attempt == 1 ? 1500 : 2500,
                    _shutdownCancellation.Token);
                if (_disposed) return;
                created = await Task.Run(() => CreateSession(device, startSettings));
                if (_disposed)
                {
                    if (created.Success)
                    {
                        _sessions.SetHandle(state, created.Handle);
                        await _sessions.StopAndDestroyAsync(state);
                    }
                    return;
                }
                if (!created.Success)
                {
                    lastFailure = new InvalidOperationException(created.Message);
                    continue;
                }

                _sessions.SetHandle(state, created.Handle);
                OnPropertyChanged(nameof(HasCaptureSession));
                var deadline = DateTime.UtcNow.AddSeconds(6);
                var ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, _shutdownCancellation.Token);
                    if (_disposed) return;
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
                    state.MarkVideoSettingsApplied(
                        startSettings.RenderWidth, startSettings.RenderHeight,
                        startSettings.FrameRate, startSettings.DecoderPreference,
                        startSettings.Brightness, startSettings.Contrast,
                        startSettings.Saturation, startSettings.Gamma);
                    var appliedMode = UsbProjectionModes.FirstOrDefault(option =>
                        option.Mode == state.UsbProjectionMode)?.Label ?? state.UsbProjectionMode.ToString();
                    var appliedDecoder = DecoderPreferences.FirstOrDefault(option =>
                        option.Preference == state.DecoderPreference)?.Label ??
                        state.DecoderPreference.ToString();
                    if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, state.Udid))
                    {
                        IsCapturing = true;
                        NativeCore.SelectPreviewSession(state.Handle);
                        OnPropertyChanged(nameof(CurrentSessionHandle));
                        if (reason == "decoder_preference")
                            SetSettingsStatus("DecoderPreferenceAppliedFormat", appliedDecoder);
                        else if (reason == "image_adjustments")
                            SetSettingsStatus("ImageAdjustmentsApplied");
                        else if (reason == "usb_display")
                            SetSettingsStatus("VideoPipelineAppliedFormat",
                                $"USB {state.AdvancedUsbWidth}x{state.AdvancedUsbHeight}");
                        else
                            SetSettingsStatus("UsbProjectionModeAppliedFormat", appliedMode);
                    }
                    AddUiLog(AppLog.Event("video pipeline restarted",
                        ("reason", reason), ("mode", state.UsbProjectionMode),
                        ("decoder", state.DecoderPreference),
                        ("brightness", state.Brightness),
                        ("contrast", state.Contrast),
                        ("saturation", state.Saturation), ("gamma", state.Gamma),
                        ("device", AppLog.Device(state.Udid))));
                    AddDiagnosticLog(AppLog.Event("video_pipeline_restart_complete",
                        ("reason", reason), ("mode", state.UsbProjectionMode),
                        ("decoder", state.DecoderPreference),
                        ("brightness", state.Brightness),
                        ("contrast", state.Contrast),
                        ("saturation", state.Saturation), ("gamma", state.Gamma),
                        ("device", AppLog.Device(state.Udid)),
                        ("handle", AppLog.Handle(state.Handle))));
                    return;
                }
                try { await _sessions.StopAndDestroyAsync(state); } catch { }
                OnPropertyChanged(nameof(HasCaptureSession));
            }
            throw lastFailure ?? new InvalidOperationException(created.Message);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            AddDiagnosticLog(AppLog.Event("video_pipeline_restart_cancelled",
                ("reason", reason),
                ("device", AppLog.Device(state.Udid))));
        }
        catch (Exception error)
        {
            SetRawSettingsStatus(error.Message);
            AddDiagnosticLog(AppLog.Event("video_pipeline_restart_failed",
                ("reason", reason), ("device", AppLog.Device(state.Udid)),
                ("error", AppLog.Error(error))));
            _sessions.SetHandle(state, 0);
            ClearSelectedSessionState(state.Udid);
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
        var shutdownTimer = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("app_shutdown_begin",
            ("sessions", _sessions.Values.Count(state => state.Handle != 0)),
            ("media_cast", _isMediaCasting), ("uptime_ms", _lifetime.ElapsedMilliseconds)));
        foreach (var window in _imageSettingsWindows.Values.ToArray())
            window.CloseForShutdown();
        _imageSettingsWindows.Clear();
        _disposed = true;
        _shutdownCancellation.Cancel();
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        AddDiagnosticLog(AppLog.Event("app_shutdown_wait_core_gate"));
        await _coreGate.WaitAsync();
        AddDiagnosticLog(AppLog.Event("app_shutdown_core_gate_acquired"));
        try
        {
            await _shutdownCoordinator.StopAndDisposeOnceAsync(
                async () =>
                {
                    try
                    {
                        foreach (var session in _sessions.Values.Where(value => value.Handle != 0).ToArray())
                        {
                            AddDiagnosticLog(AppLog.Event("app_shutdown_stop_session",
                                ("device", AppLog.Device(session.Udid)),
                                ("handle", AppLog.Handle(session.Handle))));
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
                async () =>
                {
                    AddDiagnosticLog(AppLog.Event("app_shutdown_core_dispose_begin",
                        ("elapsed_ms", shutdownTimer.ElapsedMilliseconds)));
                    await Task.Run(_core.Dispose);
                });
        }
        catch (Exception error)
        {
            // Keep the exception in the native log before releasing the gate;
            // the window owner can still complete its best-effort close path.
            try
            {
                AddDiagnosticLog(AppLog.Event("app_shutdown_failed",
                    ("elapsed_ms", shutdownTimer.ElapsedMilliseconds),
                    ("error", AppLog.Error(error))));
            }
            catch { }
            throw;
        }
        finally
        {
            _coreGate.Release();
        }
    }

    private void ClearSelectedSessionState(string udid)
    {
        if (!DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid)) return;
        NativeCore.SelectPreviewSession(0);
        _activeCaptureUdid = null;
        IsCapturing = false;
        NotifyCaptureSessionChanged();
        OnPropertyChanged(nameof(CurrentSessionHandle));
        ResetPreviewState();
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
