using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
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

internal sealed class BluetoothMouseDirectionOption(
    BluetoothMouseDirection direction,
    string labelResourceKey) : INotifyPropertyChanged
{
    public BluetoothMouseDirection Direction { get; } = direction;
    public string Label => LocalizationService.Get(labelResourceKey);
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class MainViewModel : INotifyPropertyChanged
{
    // Synthetic handle used by the output services for the WPF media-cast
    // source. It is deliberately outside the native session handle range.
    internal const ulong MediaCastOutputHandle = 0x4D434153544F5554UL;

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
    internal event Action<string, ProtectedContentPresentation>?
        DeviceProtectionStateChanged;
    internal event Action<string>? ProjectionSettingsRequested;
    internal event Action? MediaOutputSettingsRequested;
    private readonly NativeCore _core;
    private readonly IPhoneFilterDriverService _filterDriver = new();
    private readonly DriverManagerLauncher _driverManager = new();
    private readonly WirelessReceiverController _wireless;
    private readonly MediaCastReceiverController _mediaCast;
    // Serializes every native-core operation that can race USB teardown,
    // device enumeration, restart, or application shutdown.
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    // Keeps duplicate commands for one device out of the USB queue while still
    // allowing commands for another selected device to queue behind it.
    private readonly object _sessionLifecycleGate = new();
    private readonly HashSet<string> _sessionLifecycleDevices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly NativeLogTailReader _logReader = new();
    private readonly CaptureShutdownCoordinator _shutdownCoordinator = new();
    private readonly DeviceSessionManager _sessions;
    private readonly MediaOutputService _mediaOutput;
    private readonly VirtualCameraService _virtualCamera;
    private readonly BluetoothHidMouseService _bluetoothControl = new();
    private readonly DeviceBindingManager _reverseBindings = DeviceBindingManager.Shared;
    private readonly ReverseControlInputRouter _reverseInputRouter = new();
    private readonly DeviceIdentityResolver _identityResolver;
    private UsbTouchBridgeHost? _usbTouchBridge;
    private UsbTouchBridgeHost? _wirelessTouchBridge;
    private bool _wirelessControlEnabled;
    private bool _wirelessControlConnected;
    private string? _wirelessControlDeviceUdid;
    private readonly BluetoothControlNoticePolicy _bluetoothNoticePolicy = new();
    private readonly Dictionary<string, ImageSettingsWindow> _imageSettingsWindows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly SemaphoreSlim _mediaOutputGate = new(1, 1);
    // Apple Mobile Device Service can drop one Lockdown socket when two
    // independent clients negotiate concurrently. Serialize only the startup
    // handshakes; the established CoreDevice tunnel then carries control data.
    private readonly SemaphoreSlim _lockdownHandshakeGate = new(1, 1);
    private IReadOnlyList<NativeDeviceInfo> _lastUsbDevices = [];
    private bool _disposed;
    private DeviceViewModel? _selectedDevice;
    private string _environmentStatus = string.Empty;
    private string _captureStatus = string.Empty;
    private string _driverState = string.Empty;
    private bool _isCapturing;
    private bool _isAudioOnlyAirPlay;
    private bool _isBusy;
    private bool _isSettingsDialogOpen;
    private bool _isMediaOutputTransitioning;
    private bool _bluetoothControlStarting;
    private bool _bluetoothControlStopping;
    private bool _usbControlStarting;
    private bool _usbControlStopping;
    private int _activeSessionStatusPolls;
    private string? _activeCaptureUdid;
    private int _manualRefreshPending;
    private string _resolution = "—";
    private uint _sourceVideoWidth;
    private uint _sourceVideoHeight;
    private string _fpsDisplay = "— fps";
    private string _latencyDisplay = "— ms";
    private string _audioDisplay = string.Empty;
    private string _protectedAudioDisplay = string.Empty;
    private ResolutionPreset _selectedResolutionPreset = null!;
    private int _selectedFrameRate = 60;
    private double _playbackVolume = 100;
    private bool _playAudio = true;
    private bool _advancedMode;
    private string _settingsStatus = string.Empty;
    private string _decoderStatus = string.Empty;
    private string _decoderStatusTone = "Hidden";
    private string _mediaOutputStatus = string.Empty;
    private string _mediaOutputTone = "Hidden";
    private string _mediaOutputCapabilitiesText = string.Empty;
    private bool _mediaOutputCapabilitiesLoaded;
    private MediaOutputCapabilities _mediaOutputCapabilities = new(
        false, false, false, false, false, false, false,
        string.Empty, string.Empty, string.Empty);
    private VirtualCameraCapabilities _virtualCameraCapabilities = new(
        false, false, false, false, false, string.Empty);
    private string _virtualCameraStatusText = string.Empty;
    private string? _mediaOutputUdid;
    private string? _pendingRecordingPath;
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
    private WirelessReceiverBackend _selectedWirelessReceiverBackend =
        WirelessReceiverBackend.Original;
    private string _mediaCastStatus = string.Empty;
    private string _bluetoothControlStatus = string.Empty;
    private string _usbControlStatus = string.Empty;
    private bool _bluetoothControlEnabled;
    private bool _reverseControlSetupActive;
    private bool _bluetoothControlConnected;
    private bool _bluetoothControlCalibrated;
    private bool _bluetoothCalibrationInProgress;
    private bool _bluetoothControlInputEnabled;
    private bool _bluetoothControlNoticePending;
    private int _bluetoothBindingPromptInFlight;
    private int _reverseControlErrorPromptInFlight;
    private readonly HashSet<string> _bluetoothBindingPromptedTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _bluetoothControlDeviceUdid;
    private string? _usbControlDeviceUdid;
    private bool _usbControlEnabled;
    private bool _usbControlConnected;
    private bool _wiredControlPrerequisiteAcknowledged;
    private bool _wirelessControlPrerequisiteAcknowledged;
    private long _usbTouchSequence;
    private double _bluetoothMouseSensitivity = 500;
    private double _bluetoothWheelSensitivity = 1000;
    private BluetoothMouseDirection _bluetoothPortraitMouseDirection;
    private BluetoothMouseDirection _bluetoothLandscapeMouseDirection =
        BluetoothMouseDirection.Right;
    private bool _bluetoothMouseReverseHorizontal;
    private bool _bluetoothMouseReverseVertical;
    private double _appliedBluetoothMouseSensitivity = 500;
    private double _appliedBluetoothWheelSensitivity = 1000;
    private BluetoothMouseDirection _appliedBluetoothPortraitMouseDirection;
    private BluetoothMouseDirection _appliedBluetoothLandscapeMouseDirection =
        BluetoothMouseDirection.Right;
    private bool _appliedBluetoothMouseReverseHorizontal;
    private bool _appliedBluetoothMouseReverseVertical;
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
    private readonly WirelessStallRecoveryTracker _wirelessStallRecovery = new();
    private readonly HashSet<ulong> _wirelessRecoveryInFlight = [];
    private readonly WifiSyncInsertionTracker _wifiSyncInsertionTracker = new();
    // AirPlay discovery is heartbeat based. A missed IPC/Bonjour heartbeat
    // must not remove a device (and stop its preview) immediately; retain the
    // last card for a few refreshes while the receiver reconnects.
    private readonly Dictionary<string, int> _wirelessMissingRefreshes =
        new(StringComparer.OrdinalIgnoreCase);
    private const int WirelessDiscoveryGraceRefreshes = 3;
    private readonly Queue<string> _visibleLogLines = new();
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private Func<uint, uint, Nv12VideoFrame?>? _mediaCastNv12FrameProvider;
    private Func<uint, uint, VideoFrame?>? _mediaCastVideoFrameProvider;
    private Func<ulong, AudioPacket?>? _mediaCastAudioPacketProvider;
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
    public IReadOnlyList<WirelessReceiverBackendOption> WirelessReceiverBackends { get; } =
        WirelessReceiverConfiguration.BackendOptions;
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
    public IReadOnlyList<BluetoothMouseDirectionOption> BluetoothMouseDirections { get; } =
    [
        new(BluetoothMouseDirection.Up, "BluetoothMouseDirectionUp"),
        new(BluetoothMouseDirection.Right, "BluetoothMouseDirectionRight"),
        new(BluetoothMouseDirection.Down, "BluetoothMouseDirectionDown"),
        new(BluetoothMouseDirection.Left, "BluetoothMouseDirectionLeft"),
    ];
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand MediaCastStopCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyVideoSettingsCommand { get; }
    public RelayCommand MoreImageSettingsCommand { get; }
    public RelayCommand MediaOutputSettingsCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand AdvancedSettingsCommand { get; }
    public RelayCommand ApplyWirelessSettingsCommand { get; }
    public RelayCommand ApplyBluetoothMouseSettingsCommand { get; }
    public RelayCommand OpenDriverManagerCommand { get; }
    public RelayCommand StartBluetoothControlCommand { get; }
    public RelayCommand StopBluetoothControlCommand { get; }
    public RelayCommand ToggleBluetoothControlCommand { get; }
    public RelayCommand ToggleUsbControlCommand { get; }
    public string BluetoothControlStatus => _bluetoothControlStatus;
    public bool IsBluetoothControlEnabled => _bluetoothControlEnabled;
    internal string? BluetoothControlTargetUdid => _bluetoothControlDeviceUdid;
    public bool BluetoothControlIsInputEnabled => _bluetoothControlEnabled &&
        _bluetoothControlConnected && _bluetoothControlInputEnabled &&
        _bluetoothControl.IsConnected;
    public bool CanStartBluetoothControl => CanEnableBluetoothControlFor(
        SelectedDevice?.Udid);
    public bool CanStopBluetoothControl => _bluetoothControlEnabled &&
        !_bluetoothControlStarting && !_bluetoothControlStopping;
    public bool CanToggleBluetoothControl => !_bluetoothControlStarting &&
        !_bluetoothControlStopping &&
        (_bluetoothControlEnabled || CanEnableBluetoothControlFor(SelectedDevice?.Udid));
    public string BluetoothControlActionText => LocalizationService.Get(
        _bluetoothControlEnabled && _bluetoothControlInputEnabled
            ? "StopBluetoothControl" : "StartBluetoothControl");
    public bool IsUsbControlEnabled => _usbControlEnabled || _wirelessControlEnabled;
    public bool UsbControlIsInputEnabled => (_usbControlEnabled && _usbControlConnected) ||
        (_wirelessControlEnabled && _wirelessControlConnected);
    internal string? UsbControlTargetUdid => _usbControlDeviceUdid ?? _wirelessControlDeviceUdid;
    public string UsbControlStatus => !HasWiredUsbControlDevice && !_usbControlEnabled && !_wirelessControlEnabled &&
        !_usbControlStarting
        ? "请通过 USB 连接并在设备上信任一台 iPhone 或 iPad 后，再启用 USB 控制"
        : _usbControlStatus;
    public bool CanToggleUsbControl => !_usbControlStarting && !_usbControlStopping &&
        (_usbControlEnabled || _wirelessControlEnabled ||
         CanEnableUsbControlFor(SelectedDevice) || CanEnableWirelessControlFor(SelectedDevice));
    public bool CanStartUsbControl => !_usbControlStarting && !_usbControlStopping &&
        !_usbControlEnabled && !_wirelessControlEnabled &&
        CanEnableUsbControlFor(SelectedDevice);
    public bool CanStartWirelessControl => !_usbControlStarting && !_usbControlStopping &&
        !_usbControlEnabled && !_wirelessControlEnabled &&
        CanEnableWirelessControlFor(SelectedDevice);
    public bool CanToggleWiredControl => !_usbControlStarting && !_usbControlStopping &&
        (_usbControlEnabled || (!_wirelessControlEnabled && CanEnableUsbControlFor(SelectedDevice)));
    public bool CanToggleWirelessControl => !_usbControlStarting && !_usbControlStopping &&
        (_wirelessControlEnabled || (!_usbControlEnabled && CanEnableWirelessControlFor(SelectedDevice)));
    public string WiredControlActionText => _usbControlEnabled ? "关闭有线控制" : "开启有线控制";
    public string WirelessControlActionText => _wirelessControlEnabled ? "关闭无线控制" : "开启无线控制";
    public string UsbControlActionText => _usbControlStarting ? "正在连接反向控制" :
        _usbControlStopping ? "正在关闭 USB 控制" :
        !HasWiredUsbControlDevice && !_usbControlEnabled ? "需要 USB 连接" :
        _usbControlStatus.StartsWith("USB 控制连接失败", StringComparison.Ordinal) ? "重试 USB 控制" :
        _wirelessControlEnabled ? "关闭无线反控" : _usbControlEnabled ? "关闭 USB 控制" : "反向控制";

    public ApplicationDisplayMode SelectedApplicationDisplayMode
    {
        get => Application.Current is App app
            ? app.UpdateSettings.ApplicationDisplayMode
            : ApplicationDisplayMode.Complete;
        set
        {
            if (Application.Current is not App app ||
                app.UpdateSettings.ApplicationDisplayMode == value) return;
            var previous = app.UpdateSettings.ApplicationDisplayMode;
            app.UpdateSettings.ApplicationDisplayMode = value;
            if (!app.SaveUpdateSettings())
            {
                app.UpdateSettings.ApplicationDisplayMode = previous;
                OnPropertyChanged(nameof(SelectedApplicationDisplayMode));
                return;
            }
            OnPropertyChanged(nameof(SelectedApplicationDisplayMode));
            OnPropertyChanged(nameof(IsLightweightApplicationMode));
            AddDiagnosticLog(AppLog.Event("application_display_mode_changed",
                ("mode", value.ToString())));
        }
    }

    public bool IsLightweightApplicationMode =>
        SelectedApplicationDisplayMode == ApplicationDisplayMode.Lightweight;

    private bool CanEnableBluetoothControlFor(string? deviceUdid) =>
        !_bluetoothControlEnabled && !_bluetoothControlStarting &&
        !_bluetoothControlStopping && !IsBusy &&
        !_usbControlEnabled && !_usbControlStarting && !_usbControlStopping &&
        !string.IsNullOrWhiteSpace(deviceUdid) &&
        _sessions.TryGet(deviceUdid, out var session) && IsSessionPresentable(session);

    private bool CanEnableUsbControlFor(DeviceViewModel? device) =>
        !_usbControlEnabled && !_usbControlStarting && !_usbControlStopping &&
        device is not null && !device.IsMediaCast &&
        GetUsbControlBinding(device.Udid) is not null;

    private bool CanEnableWirelessControlFor(DeviceViewModel? device) =>
        !_wirelessControlEnabled && !_usbControlEnabled && !_usbControlStarting &&
        !_usbControlStopping && device is not null &&
        _identityResolver.Resolve(device).AppleUdid is not null;

    // AirPlay supplies the picture, but direct touch always goes through a
    // physically connected and trusted Apple USB device.
    private bool HasWiredUsbControlDevice => Devices.Any(device =>
        !device.IsWireless && !device.IsMediaCast);

    private string? GetUsbControlBinding(string mirrorUdid)
    {
        var identity = _identityResolver.Resolve(Devices.FirstOrDefault(d =>
            DeviceViewModel.UdidEquals(d.Udid, mirrorUdid)));
        return identity.AppleUdid;
    }

    private bool HasBluetoothControlTargetSession =>
        !string.IsNullOrWhiteSpace(_bluetoothControlDeviceUdid) &&
        _sessions.TryGet(_bluetoothControlDeviceUdid, out var session) &&
        IsSessionPresentable(session);
    public double BluetoothMouseSensitivity
    {
        get => _bluetoothMouseSensitivity;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 10, 1000);
            if (Set(ref _bluetoothMouseSensitivity, clamped))
            {
                OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
                ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public double BluetoothWheelSensitivity
    {
        get => _bluetoothWheelSensitivity;
        set
        {
            if (!double.IsFinite(value)) return;
            if (Set(ref _bluetoothWheelSensitivity, Math.Clamp(value, 10, 1000)))
            {
                OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
                ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public BluetoothMouseDirectionOption? SelectedBluetoothPortraitMouseDirection
    {
        get => BluetoothMouseDirections.FirstOrDefault(option =>
            option.Direction == _bluetoothPortraitMouseDirection);
        set
        {
            if (value is null ||
                !Set(ref _bluetoothPortraitMouseDirection, value.Direction)) return;
            OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
            ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
        }
    }
    public BluetoothMouseDirectionOption? SelectedBluetoothLandscapeMouseDirection
    {
        get => BluetoothMouseDirections.FirstOrDefault(option =>
            option.Direction == _bluetoothLandscapeMouseDirection);
        set
        {
            if (value is null ||
                !Set(ref _bluetoothLandscapeMouseDirection, value.Direction)) return;
            OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
            ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
        }
    }
    public bool BluetoothMouseReverseHorizontal
    {
        get => _bluetoothMouseReverseHorizontal;
        set
        {
            if (Set(ref _bluetoothMouseReverseHorizontal, value))
            {
                OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
                ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public bool BluetoothMouseReverseVertical
    {
        get => _bluetoothMouseReverseVertical;
        set
        {
            if (Set(ref _bluetoothMouseReverseVertical, value))
            {
                OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
                ApplyBluetoothMouseSettingsCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public string BluetoothDeviceOrientationDisplay =>
        LocalizationService.Format("BluetoothCurrentOrientationFormat",
            LocalizationService.Get(BluetoothMouseOrientationMapper.Detect(
                SourceVideoWidth, SourceVideoHeight) switch
            {
                BluetoothDeviceOrientation.Portrait => "BluetoothDeviceOrientationPortrait",
                BluetoothDeviceOrientation.Landscape => "BluetoothDeviceOrientationLandscape",
                _ => "BluetoothDeviceOrientationUnknown",
            }),
            SourceVideoWidth > 0 && SourceVideoHeight > 0
                ? $"{SourceVideoWidth}×{SourceVideoHeight}" : "—");
    internal double AppliedBluetoothMouseSensitivity => _appliedBluetoothMouseSensitivity;
    internal double AppliedBluetoothWheelSensitivity => _appliedBluetoothWheelSensitivity;
    internal BluetoothMouseDirection AppliedBluetoothPortraitMouseDirection =>
        _appliedBluetoothPortraitMouseDirection;
    internal BluetoothMouseDirection AppliedBluetoothLandscapeMouseDirection =>
        _appliedBluetoothLandscapeMouseDirection;
    internal bool AppliedBluetoothMouseReverseHorizontal =>
        _appliedBluetoothMouseReverseHorizontal;
    internal bool AppliedBluetoothMouseReverseVertical =>
        _appliedBluetoothMouseReverseVertical;
    public bool HasPendingBluetoothMouseSettings => Application.Current is App app &&
        (Math.Abs(_bluetoothMouseSensitivity - app.UpdateSettings.BluetoothMouseSensitivity) > 0.001 ||
         Math.Abs(_bluetoothWheelSensitivity - app.UpdateSettings.BluetoothWheelSensitivity) > 0.001 ||
         (int)_bluetoothPortraitMouseDirection != app.UpdateSettings.BluetoothPortraitMouseDirection ||
         (int)_bluetoothLandscapeMouseDirection != app.UpdateSettings.BluetoothLandscapeMouseDirection ||
         _bluetoothMouseReverseHorizontal != app.UpdateSettings.BluetoothMouseReverseHorizontal ||
         _bluetoothMouseReverseVertical != app.UpdateSettings.BluetoothMouseReverseVertical);

    private void ApplyBluetoothMouseSettings()
    {
        if (Application.Current is not App app) return;
        var previousMouse = app.UpdateSettings.BluetoothMouseSensitivity;
        var previousWheel = app.UpdateSettings.BluetoothWheelSensitivity;
        var previousPortraitDirection = app.UpdateSettings.BluetoothPortraitMouseDirection;
        var previousLandscapeDirection = app.UpdateSettings.BluetoothLandscapeMouseDirection;
        var previousReverseHorizontal = app.UpdateSettings.BluetoothMouseReverseHorizontal;
        var previousReverseVertical = app.UpdateSettings.BluetoothMouseReverseVertical;
        app.UpdateSettings.BluetoothMouseSensitivity = _bluetoothMouseSensitivity;
        app.UpdateSettings.BluetoothMouseSensitivitySchema = 2;
        app.UpdateSettings.BluetoothWheelSensitivity = _bluetoothWheelSensitivity;
        app.UpdateSettings.BluetoothMouseSettingsSchema = 1;
        app.UpdateSettings.BluetoothPortraitMouseDirection =
            (int)_bluetoothPortraitMouseDirection;
        app.UpdateSettings.BluetoothLandscapeMouseDirection =
            (int)_bluetoothLandscapeMouseDirection;
        app.UpdateSettings.BluetoothMouseReverseHorizontal =
            _bluetoothMouseReverseHorizontal;
        app.UpdateSettings.BluetoothMouseReverseVertical =
            _bluetoothMouseReverseVertical;
        app.UpdateSettings.BluetoothMouseDirectionSchema = 1;
        if (!app.SaveUpdateSettings())
        {
            app.UpdateSettings.BluetoothMouseSensitivity = previousMouse;
            app.UpdateSettings.BluetoothWheelSensitivity = previousWheel;
            app.UpdateSettings.BluetoothPortraitMouseDirection = previousPortraitDirection;
            app.UpdateSettings.BluetoothLandscapeMouseDirection = previousLandscapeDirection;
            app.UpdateSettings.BluetoothMouseReverseHorizontal = previousReverseHorizontal;
            app.UpdateSettings.BluetoothMouseReverseVertical = previousReverseVertical;
            SetRawSettingsStatus(LocalizationService.Get("BluetoothMouseSettingsSaveFailed"));
            OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
            ApplyBluetoothMouseSettingsCommand.NotifyCanExecuteChanged();
            return;
        }
        _appliedBluetoothMouseSensitivity = _bluetoothMouseSensitivity;
        _appliedBluetoothWheelSensitivity = _bluetoothWheelSensitivity;
        _appliedBluetoothPortraitMouseDirection = _bluetoothPortraitMouseDirection;
        _appliedBluetoothLandscapeMouseDirection = _bluetoothLandscapeMouseDirection;
        _appliedBluetoothMouseReverseHorizontal = _bluetoothMouseReverseHorizontal;
        _appliedBluetoothMouseReverseVertical = _bluetoothMouseReverseVertical;
        AddDiagnosticLog(AppLog.Event("bluetooth_mouse_settings_applied",
            ("mouse_sensitivity", _bluetoothMouseSensitivity),
            ("wheel_sensitivity", _bluetoothWheelSensitivity),
            ("portrait_direction", (int)_bluetoothPortraitMouseDirection),
            ("landscape_direction", (int)_bluetoothLandscapeMouseDirection),
            ("reverse_horizontal", _bluetoothMouseReverseHorizontal),
            ("reverse_vertical", _bluetoothMouseReverseVertical)));
        OnPropertyChanged(nameof(HasPendingBluetoothMouseSettings));
        ApplyBluetoothMouseSettingsCommand.NotifyCanExecuteChanged();
    }
    public bool IsAdvancedMode { get => _advancedMode; private set { if (Set(ref _advancedMode, value)) OnPropertyChanged(nameof(AdvancedSettingsVisibility)); } }
    public bool IsWirelessSelected => SelectedDevice?.IsWireless == true;
    public bool IsMediaCasting => _isMediaCasting;
    public bool IsMediaCastSelected => SelectedDevice?.IsMediaCast == true;
    public Visibility WiredVideoLimitSettingsVisibility => IsWirelessSelected || IsMediaCastSelected
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VideoSettingsVisibility => SelectedDevice is not null &&
        !IsMediaCastSelected ? Visibility.Visible : Visibility.Collapsed;
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
    public WirelessReceiverBackendOption SelectedWirelessReceiverBackend
    {
        get => WirelessReceiverConfiguration.GetBackendOption(
            _selectedWirelessReceiverBackend);
        set
        {
            if (value is null) return;
            var backend = WirelessReceiverConfiguration.NormalizeBackend(value.Backend);
            if (_selectedWirelessReceiverBackend == backend) return;
            _selectedWirelessReceiverBackend = backend;
            OnPropertyChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
        }
    }
    public string AppliedWirelessBackendDisplay => LocalizationService.Format(
        "WirelessBackendAppliedFormat",
        WirelessReceiverConfiguration.GetBackendOption(_wireless.AppliedBackend).Label);
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
    // A handle remains owned until native teardown completes, but it must never
    // be presented or queried after a stop was requested.
    public ulong CurrentSessionHandle => IsSessionPresentable(CurrentDeviceSession)
        ? CurrentDeviceSession!.Handle
        : 0;
    public bool HasCaptureSession => CurrentDeviceSession?.HasSession == true;
    internal bool HasAnyCaptureSession => _sessions.AnySession;
    // Media casting is a virtual source, so it has no native capture session
    // handle. Keep the preview/output surface visible while that source is
    // active instead of tying the toolbar to USB/AirPlay sessions only.
    public Visibility PreviewAndObsVisibility => CurrentSessionHandle != 0 ||
        IsMediaCasting ? Visibility.Visible : Visibility.Collapsed;
    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) { StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); } } }
    public bool IsAudioOnlyAirPlay => _isAudioOnlyAirPlay;
    public bool IsVideoProtected => CurrentDeviceSession?.VideoProtected == true;
    public bool CanUseVisualPreviewTools =>
        (HasCaptureSession || IsMediaCasting) && !IsAudioOnlyAirPlay &&
        !IsVideoProtected;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            StartBluetoothControlCommand?.NotifyCanExecuteChanged();
            StopBluetoothControlCommand?.NotifyCanExecuteChanged();
            ToggleBluetoothControlCommand?.NotifyCanExecuteChanged();
            ToggleUsbControlCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanStartBluetoothControl));
            OnPropertyChanged(nameof(CanStopBluetoothControl));
            OnPropertyChanged(nameof(CanToggleBluetoothControl));
            OnPropertyChanged(nameof(CanToggleUsbControl));
            ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
            MoreImageSettingsCommand.NotifyCanExecuteChanged();
            ApplyWirelessSettingsCommand.NotifyCanExecuteChanged();
            MediaCastStopCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
            OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
            OnPropertyChanged(nameof(CanChangeVideoPipeline));
            OnPropertyChanged(nameof(CanChangeDecoderPipeline));
            OnPropertyChanged(nameof(CanOpenImageSettings));
            NotifyMediaOutputStateChanged();
            foreach (var window in _imageSettingsWindows.Values.ToArray())
                window.SetEditingEnabled(!value);
        }
    }
    private bool IsSettingsInteractionBlocked => IsBusy || _isSettingsDialogOpen;
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
            if (Application.Current is App app)
                app.UpdateSettings.Language = LocalizationService.SelectedLanguage;
        }
    }
    public string Resolution { get => _resolution; private set => Set(ref _resolution, value); }
    public uint SourceVideoWidth => _sourceVideoWidth;
    public uint SourceVideoHeight => _sourceVideoHeight;
    public string FpsDisplay { get => _fpsDisplay; private set => Set(ref _fpsDisplay, value); }
    public string LatencyDisplay { get => _latencyDisplay; private set => Set(ref _latencyDisplay, value); }
    public string AudioDisplay { get => _audioDisplay; private set => Set(ref _audioDisplay, value); }
    public string ProtectedAudioDisplay
    {
        get => _protectedAudioDisplay;
        private set => Set(ref _protectedAudioDisplay, value);
    }
    public string AudioDetailDisplay => IsMediaCastSelected
        ? LocalizationService.Get("MediaCastSystemDecoder")
        : IsAudioOnlyAirPlay ? LocalizationService.Get("WirelessMusicAudioFormat")
        : "48 kHz PCM";
    public ResolutionPreset SelectedResolutionPreset
    {
        get => _selectedResolutionPreset;
        set
        {
            if (value is null || IsSettingsInteractionBlocked) return;
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
            if (IsSettingsInteractionBlocked) return;
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
            if (Math.Abs(_playbackVolume - clamped) < 0.001) return;
            var session = CurrentDeviceSession;
            (bool Success, string Message) result;
            if (session is { IsStarting: true } or { IsStopping: true })
            {
                result = (false, LocalizationService.Get("StatusWaitingDevice"));
            }
            else if (session is { Handle: not 0 })
            {
                var handle = session.Handle;
                result = InvokeDeviceSetting(() => _core.SetDeviceAudioVolume(handle,
                    clamped / 100.0));
            }
            else if (session is not null)
            {
                // Preserve settings selected before the device session starts.
                result = (true, string.Empty);
            }
            else
            {
                result = _core.SetAudioVolume(clamped / 100.0);
            }
            if (!result.Success)
            {
                SetRawSettingsStatus(result.Message);
                OnPropertyChanged();
                return;
            }
            _playbackVolume = clamped;
            if (session is not null) session.Volume = clamped;
            OnPropertyChanged();
            AddDiagnosticLog(AppLog.Event("audio_volume_changed",
                ("source", AppLog.Device(SelectedDevice?.Udid)),
                ("volume_percent", clamped), ("enabled", _playAudio)));
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
            if (_playAudio == value) return;
            var session = CurrentDeviceSession;
            (bool Success, string Message) result;
            if (session is { IsStarting: true } or { IsStopping: true })
            {
                result = (false, LocalizationService.Get("StatusWaitingDevice"));
            }
            else if (session is { Handle: not 0 })
            {
                var handle = session.Handle;
                result = InvokeDeviceSetting(() => _core.SetDeviceAudioEnabled(handle,
                    value));
            }
            else if (session is not null)
            {
                // Preserve settings selected before the device session starts.
                result = (true, string.Empty);
            }
            else
            {
                result = _core.SetAudioEnabled(value);
            }
            if (!result.Success)
            {
                SetRawSettingsStatus(result.Message);
                OnPropertyChanged();
                return;
            }
            _playAudio = value;
            if (session is not null) session.PlayAudio = value;
            OnPropertyChanged();
            AddDiagnosticLog(AppLog.Event("audio_enabled_changed",
                ("source", AppLog.Device(SelectedDevice?.Udid)),
                ("enabled", value), ("volume_percent", _playbackVolume)));
            SetSettingsStatus(value ? "AudioPlaybackEnabled" : "AudioPlaybackMuted");
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
            if (value is null || device is null || device.IsWireless ||
                IsSettingsInteractionBlocked) return;
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
        !IsWirelessSelected && !IsMediaCastSelected && !IsSettingsInteractionBlocked;

    private DecoderPreference CurrentDecoderPreference =>
        CurrentDeviceSession?.DecoderPreference ?? DecoderPreference.Auto;

    public DecoderPreferenceOption? SelectedDecoderPreference
    {
        get => DecoderPreferences.FirstOrDefault(option =>
            option.Preference == CurrentDecoderPreference);
        set
        {
            var device = SelectedDevice;
            if (value is null || device is null || device.IsMediaCast ||
                IsSettingsInteractionBlocked) return;
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
        !IsWirelessSelected && !IsMediaCastSelected && !IsSettingsInteractionBlocked;

    public bool CanChangeDecoderPipeline => SelectedDevice is not null &&
        !IsMediaCastSelected && !IsSettingsInteractionBlocked;

    public bool CanOpenImageSettings => SelectedDevice is not null &&
        !IsMediaCastSelected && !IsSettingsInteractionBlocked;

    private static (bool Success, string Message) InvokeDeviceSetting(Action action)
    {
        try { action(); return (true, string.Empty); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("settings", "device_setting_failed", error);
            return (false, error.Message);
        }
    }
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }
    public string DecoderStatus
    {
        get => _decoderStatus;
        private set => Set(ref _decoderStatus, value);
    }
    public string DecoderStatusTone
    {
        get => _decoderStatusTone;
        private set
        {
            if (!Set(ref _decoderStatusTone, value)) return;
            OnPropertyChanged(nameof(DecoderStatusVisibility));
        }
    }
    public Visibility DecoderStatusVisibility =>
        DecoderStatusTone == "Hidden" ? Visibility.Collapsed : Visibility.Visible;
    public string MediaOutputStatus
    {
        get => _mediaOutputStatus;
        private set => Set(ref _mediaOutputStatus, value);
    }
    public string MediaOutputTone
    {
        get => _mediaOutputTone;
        private set
        {
            if (!Set(ref _mediaOutputTone, value)) return;
            OnPropertyChanged(nameof(MediaOutputStatusVisibility));
        }
    }
    public Visibility MediaOutputStatusVisibility =>
        MediaOutputTone == "Hidden" ? Visibility.Collapsed : Visibility.Visible;
    public string MediaOutputCapabilitiesText
    {
        get => _mediaOutputCapabilitiesText;
        private set => Set(ref _mediaOutputCapabilitiesText, value);
    }
    public string VirtualCameraStatusText
    {
        get => _virtualCameraStatusText;
        private set => Set(ref _virtualCameraStatusText, value);
    }
    public string VirtualCameraInstallActionText => LocalizationService.Get(
        _virtualCameraCapabilities.UpdateRequired
            ? "UpdateVirtualCamera" : "InstallVirtualCamera");
    public bool IsMediaOutputRunning =>
        _mediaOutput.IsRunning || _virtualCamera.IsRunning;
    public bool IsMediaOutputTransitioning => _isMediaOutputTransitioning;
    public bool CanStopMediaOutput => IsMediaOutputRunning && !IsMediaOutputTransitioning;
    public bool CanStartMediaOutput => (CurrentSessionHandle != 0 ||
        (IsMediaCasting && IsMediaCastSelected &&
         _mediaCastNv12FrameProvider is not null)) &&
        !IsBusy && !IsMediaOutputRunning &&
        !IsMediaOutputTransitioning;
    internal string? PendingRecordingPath =>
        !string.IsNullOrWhiteSpace(_pendingRecordingPath) &&
        File.Exists(_pendingRecordingPath) ? _pendingRecordingPath : null;
    public bool CanRecordMediaOutput =>
        _mediaOutputCapabilities.Supports(MediaOutputKind.Recording);
    public bool CanStreamRtmp =>
        _mediaOutputCapabilities.Supports(MediaOutputKind.Rtmp);
    public bool CanStreamSrt =>
        _mediaOutputCapabilities.Supports(MediaOutputKind.Srt);
    public bool CanStreamWhip =>
        _mediaOutputCapabilities.Supports(MediaOutputKind.Whip);
    public bool CanUseVirtualCamera => CanStartMediaOutput &&
        (!IsMediaCastSelected || _mediaCastVideoFrameProvider is not null) &&
        _virtualCameraCapabilities.BackendAvailable &&
        _virtualCameraCapabilities.Supported &&
        _virtualCameraCapabilities.Registered &&
        !_virtualCameraCapabilities.UpdateRequired;
    public bool CanInstallVirtualCamera => !IsMediaOutputRunning &&
        !IsMediaOutputTransitioning &&
        _virtualCameraCapabilities.BackendAvailable &&
        _virtualCameraCapabilities.Supported &&
        (!_virtualCameraCapabilities.Registered ||
         _virtualCameraCapabilities.UpdateRequired);
    public bool CanUninstallVirtualCamera => !IsMediaOutputRunning &&
        !IsMediaOutputTransitioning &&
        _virtualCameraCapabilities.BackendAvailable &&
        _virtualCameraCapabilities.Registered;
    public Visibility VirtualCameraInstallVisibility =>
        _virtualCameraCapabilities.BackendAvailable &&
        _virtualCameraCapabilities.Supported &&
        (!_virtualCameraCapabilities.Registered ||
         _virtualCameraCapabilities.UpdateRequired)
            ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VirtualCameraStartVisibility =>
        _virtualCameraCapabilities.Registered &&
        !_virtualCameraCapabilities.UpdateRequired
            ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VirtualCameraUninstallVisibility =>
        _virtualCameraCapabilities.Registered
            ? Visibility.Visible : Visibility.Collapsed;
    public string TargetResolutionDisplay => IsAudioOnlyAirPlay
        ? LocalizationService.Get("WirelessMusicNoVideo")
        : IsMediaCastSelected
        ? LocalizationService.Get("MediaCastOriginalResolution")
        : LocalizationService.Format("RenderLimitFormat", SelectedResolutionPreset.Label);
    public string TargetFpsDisplay => IsAudioOnlyAirPlay
        ? LocalizationService.Get("WirelessMusicNoVideo")
        : IsMediaCastSelected
        ? LocalizationService.Format("MediaCastFpsCapabilityFormat",
            _wireless.AppliedProfile.FrameRate)
        : LocalizationService.Format("TargetFpsFormat", SelectedFrameRate);
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string LogPathDisplay => _logReader.Path;

    public MainViewModel()
    {
        _identityResolver = new DeviceIdentityResolver(_reverseBindings);
        _environmentStatus = LocalizationService.Get("StatusCheckingEnvironment");
        _captureStatus = LocalizationService.Get("StatusWaitingDevice");
        _driverState = LocalizationService.Get("StatusDetecting");
        _audioDisplay = LocalizationService.Get("StatusWaiting");
        _protectedAudioDisplay = LocalizationService.Get("StatusWaiting");
        _settingsStatus = LocalizationService.Get("StatusDefaultSettings");
        _mediaOutputStatus = LocalizationService.Get("MediaOutputIdle");
        _mediaOutputCapabilitiesText = LocalizationService.Get("MediaOutputCapabilitiesUnknown");
        _virtualCameraStatusText = LocalizationService.Get("VirtualCameraChecking");
        _bluetoothControlStatus = LocalizationService.Get("BluetoothControlOff");
        _usbControlStatus = "USB 控制未启用";
        _logText = LocalizationService.Get("StatusWaitingLog");
        _selectedLanguage = LocalizationService.SelectedLanguage;
        if (Application.Current is App currentApp)
        {
            _selectedWirelessReceiverBackend =
                WirelessReceiverConfiguration.NormalizeBackend(
                    currentApp.UpdateSettings.WirelessReceiverBackend);
            _bluetoothMouseSensitivity = Math.Clamp(
                currentApp.UpdateSettings.BluetoothMouseSensitivity, 10, 1000);
            _bluetoothWheelSensitivity = Math.Clamp(
                currentApp.UpdateSettings.BluetoothWheelSensitivity, 10, 1000);
            _bluetoothPortraitMouseDirection = (BluetoothMouseDirection)Math.Clamp(
                currentApp.UpdateSettings.BluetoothPortraitMouseDirection, 0, 3);
            _bluetoothLandscapeMouseDirection = (BluetoothMouseDirection)Math.Clamp(
                currentApp.UpdateSettings.BluetoothLandscapeMouseDirection, 0, 3);
            _bluetoothMouseReverseHorizontal = currentApp.UpdateSettings.BluetoothMouseReverseHorizontal;
            _bluetoothMouseReverseVertical = currentApp.UpdateSettings.BluetoothMouseReverseVertical;
            _appliedBluetoothMouseSensitivity = _bluetoothMouseSensitivity;
            _appliedBluetoothWheelSensitivity = _bluetoothWheelSensitivity;
            _appliedBluetoothPortraitMouseDirection = _bluetoothPortraitMouseDirection;
            _appliedBluetoothLandscapeMouseDirection = _bluetoothLandscapeMouseDirection;
            _appliedBluetoothMouseReverseHorizontal = _bluetoothMouseReverseHorizontal;
            _appliedBluetoothMouseReverseVertical = _bluetoothMouseReverseVertical;
        }
        _core = new NativeCore();
        var wirelessReceiver = new WirelessReceiverService();
        _wireless = new WirelessReceiverController(_core, wirelessReceiver);
        _wireless.Backend = _selectedWirelessReceiverBackend;
        if (Application.Current is App restoredApp)
        {
            _wireless.ReceiverName = WirelessReceiverConfiguration.SanitizeReceiverName(
                restoredApp.UpdateSettings.WirelessReceiverName);
            _wireless.SelectedProfile = WirelessReceiverConfiguration.DisplayProfiles
                .FirstOrDefault(profile => string.Equals(profile.Id,
                    restoredApp.UpdateSettings.WirelessDisplayProfileId,
                    StringComparison.OrdinalIgnoreCase))
                ?? WirelessReceiverConfiguration.DefaultDisplayProfile;
        }
        _mediaCast = new MediaCastReceiverController(_core, wirelessReceiver,
            () => _wireless.Backend);
        _sessions = new DeviceSessionManager(_core);
        _mediaOutput = new MediaOutputService(GetOutputNv12Frame,
            GetOutputAudioPacket);
        _mediaOutput.StatusChanged += OnMediaOutputStatusChanged;
        _pendingRecordingPath = PendingRecordingStore.FindLatest();
        _virtualCamera = new VirtualCameraService(GetOutputVideoFrame);
        _virtualCamera.StatusChanged += OnMediaOutputStatusChanged;
        _sessions.SessionHandleChanged += (udid, handle) =>
        {
            // Settings windows are bound to the native session that existed
            // when they opened. Never let one follow a replacement handle.
            InvalidateImageSettingsWindow(udid);
            if (IsMediaOutputRunning && DeviceViewModel.UdidEquals(_mediaOutputUdid, udid))
                _ = StopMediaOutputAsync();
            PublishDeviceProtectionStateChanged(udid, default);
            DeviceSessionHandleChanged?.Invoke(udid, handle);
        };
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
                !IsCapturing && !IsMediaCasting &&
                CanQueueSessionLifecycleOperation(SelectedDevice));
        StopCommand = new RelayCommand(() => _ = StopAsync(),
            CanStopCurrentCapture);
        MediaCastStopCommand = new RelayCommand(() => RequestMediaCastStop(),
            () => IsMediaCasting);
        // A manual refresh is guaranteed to run after a short in-flight poll;
        // timer refreshes remain best-effort and never build up a queue.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceDeviceEnumeration: true));
        ApplyVideoSettingsCommand = new RelayCommand(() => _ = ApplyVideoSettingsAsync(),
            () => !IsSettingsInteractionBlocked);
        MoreImageSettingsCommand = new RelayCommand(ShowImageSettings,
            () => CanOpenImageSettings);
        MediaOutputSettingsCommand = new RelayCommand(
            () => MediaOutputSettingsRequested?.Invoke(), () => SelectedDevice is not null);
        ClearLogCommand = new RelayCommand(ClearVisibleLog);
        AdvancedSettingsCommand = new RelayCommand(ShowAdvancedSettings, () => IsAdvancedMode);
        OpenDriverManagerCommand = new RelayCommand(() => OpenDriverManager());
        StartBluetoothControlCommand = new RelayCommand(() => _ = EnableBluetoothControlAsync(),
            () => CanStartBluetoothControl);
        StopBluetoothControlCommand = new RelayCommand(() => _ = StopBluetoothControlAsync(),
            () => CanStopBluetoothControl);
        ToggleBluetoothControlCommand = new RelayCommand(
            () => _ = ToggleBluetoothControlAsync(), () => CanToggleBluetoothControl);
        ToggleUsbControlCommand = new RelayCommand(
            () => _ = ToggleUsbControlAsync(), () => CanToggleUsbControl);
        BluetoothControlNoticeWindow.ActiveNoticeClosed += OnBluetoothControlNoticeClosed;
        _bluetoothControl.StatusChanged += (_, _) =>
        {
            void Update()
            {
                var connected = _bluetoothControl.IsConnected;
                var connectionChanged = connected != _bluetoothControlConnected;
                _bluetoothControlConnected = connected;
                if (!connected) _bluetoothControlCalibrated = false;
                _bluetoothControlStatus = _bluetoothControl.Error is null
                    ? _bluetoothControl.Status
                    : $"{_bluetoothControl.Status} {_bluetoothControl.Error}";
                AddDiagnosticLog(AppLog.Event("bluetooth_control_state",
                    ("advertising", _bluetoothControl.IsAdvertising),
                    ("connected", connected),
                    ("enabled", _bluetoothControlEnabled),
                    ("status", _bluetoothControl.Status),
                    ("error", _bluetoothControl.Error)));
                OnPropertyChanged(nameof(BluetoothControlStatus));
                if (connectionChanged)
                {
                    OnPropertyChanged(nameof(BluetoothControlIsConnected));
                    OnPropertyChanged(nameof(BluetoothControlIsInputEnabled));
                }
                OnPropertyChanged(nameof(CanStartBluetoothControl));
                OnPropertyChanged(nameof(CanStopBluetoothControl));
                OnPropertyChanged(nameof(CanToggleBluetoothControl));
                StartBluetoothControlCommand.NotifyCanExecuteChanged();
                StopBluetoothControlCommand.NotifyCanExecuteChanged();
                ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
                if (_bluetoothControlEnabled)
                    _ = EnsureBluetoothControlBindingAsync();
            }
            if (Application.Current?.Dispatcher.CheckAccess() == true) Update();
            else Application.Current?.Dispatcher.BeginInvoke(Update);
        };
        ApplyWirelessSettingsCommand = new RelayCommand(() => _ = RestartWirelessReceiverAsync(),
            () => _wireless.IsBackendAvailable(_selectedWirelessReceiverBackend) && !IsBusy);
        ApplyBluetoothMouseSettingsCommand = new RelayCommand(ApplyBluetoothMouseSettings,
            () => HasPendingBluetoothMouseSettings);
        RefreshWirelessStatus();
        RefreshMediaCastStatus();
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    internal bool BluetoothControlIsConnected => _bluetoothControlConnected;
    internal bool IsBluetoothControlTarget(string? udid) =>
        _bluetoothControlEnabled && DeviceViewModel.UdidEquals(
            _bluetoothControlDeviceUdid, udid);
    internal int BluetoothWheelResolutionMultiplier =>
        _bluetoothControl.WheelResolutionMultiplier;

    internal Task SendBluetoothMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0) =>
        _bluetoothControl.SendMouseAsync(dx, dy, buttons, wheel);

    internal Task SendBluetoothKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages) =>
        _bluetoothControl.SendKeyboardAsync(modifiers, usages);

    internal async Task SendUsbKeyboardAsync(IReadOnlyCollection<byte> usages)
    {
        var bridge = _usbTouchBridge is { IsReady: true } ? _usbTouchBridge :
            _wirelessTouchBridge is { IsReady: true } ? _wirelessTouchBridge : null;
        AddDiagnosticLog(AppLog.Event("usb_keyboard_send_begin",
            ("usages", string.Join(',', usages)),
            ("usb_bridge_ready", _usbTouchBridge?.IsReady ?? false),
            ("wireless_bridge_ready", _wirelessTouchBridge?.IsReady ?? false),
            ("usb_target", AppLog.Device(_usbControlDeviceUdid)),
            ("wireless_target", AppLog.Device(_wirelessControlDeviceUdid))));
        if (bridge is null)
        {
            AddDiagnosticLog(AppLog.Event("usb_keyboard_send_skipped",
                ("reason", "no_ready_bridge")));
            return;
        }
        try
        {
            await bridge.SendKeyboardAsync(usages);
            AddDiagnosticLog(AppLog.Event("usb_keyboard_send_complete",
                ("usages", string.Join(',', usages)),
                ("bridge_udid", AppLog.Device(bridge.Udid))));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("usb_keyboard_send_failed",
                ("usages", string.Join(',', usages)),
                ("error", AppLog.Error(error))));
            throw;
        }
    }

    internal async Task SendUsbButtonAsync(ushort usagePage, ushort usageCode,
        string state, string? targetUdid)
    {
        // Both transports can overlap briefly while the user switches modes.
        // Hardware buttons must follow the same target selection as pointer
        // input instead of being delivered to the first bridge that happens
        // to remain ready.
        var bridge = _usbTouchBridge is { IsReady: true } usbBridge &&
                DeviceViewModel.UdidEquals(_usbControlDeviceUdid, targetUdid)
            ? usbBridge
            : _wirelessTouchBridge is { IsReady: true } wirelessBridge &&
                DeviceViewModel.UdidEquals(_wirelessControlDeviceUdid, targetUdid)
                ? wirelessBridge
                : null;
        AddDiagnosticLog(AppLog.Event("usb_button_send_begin",
            ("usage_page", usagePage), ("usage_code", usageCode),
            ("state", state), ("device", AppLog.Device(targetUdid)),
            ("usb_bridge_ready", _usbTouchBridge?.IsReady ?? false),
            ("wireless_bridge_ready", _wirelessTouchBridge?.IsReady ?? false)));
        if (bridge is null)
        {
            AddDiagnosticLog(AppLog.Event("usb_button_send_skipped",
                ("reason", "target_bridge_not_ready"),
                ("device", AppLog.Device(targetUdid))));
            throw new InvalidOperationException("当前设备的反控桥接器尚未就绪。");
        }
        await bridge.SendButtonAsync(usagePage, usageCode, state);
        AddDiagnosticLog(AppLog.Event("usb_button_send_complete",
            ("usage_page", usagePage), ("usage_code", usageCode),
            ("state", state), ("device", AppLog.Device(targetUdid))));
    }

    internal Task SendBluetoothSystemShortcutAsync(byte keyboardUsage) =>
        _bluetoothControl.SendIphoneSystemShortcutAsync(keyboardUsage);

    internal Task SendBluetoothAppSwitcherAsync() =>
        _bluetoothControl.SendIphoneAppSwitcherAsync();

    internal Task ReleaseBluetoothControlInputAsync() =>
        _bluetoothControl.ReleaseAllAsync();

    internal async Task<bool> CalibrateBluetoothControlAsync()
    {
        var target = _bluetoothControlDeviceUdid;
        if (string.IsNullOrWhiteSpace(target)) return false;
        // Relative HID reports have no absolute position. Keep calibration in
        // the HID service so it shares the target route and generation checks.
        return await _bluetoothControl.CalibrateAsync(target,
            _shutdownCancellation.Token);
    }

    internal async Task EnableBluetoothControlAsync(string? targetDeviceUdid = null,
        bool preserveExistingBinding = false, bool fromReverseControl = false,
        bool configurationOnly = false)
    {
        var controlDeviceUdid = targetDeviceUdid ?? SelectedDevice?.Udid;
        if (string.IsNullOrWhiteSpace(controlDeviceUdid) ||
            (!CanEnableBluetoothControlFor(controlDeviceUdid) &&
             !(fromReverseControl && CanStartReverseBluetoothPeripheral))) return;
        if (!AcknowledgeBluetoothHidReportMapChange()) return;
        _bluetoothControlDeviceUdid = controlDeviceUdid;
        var savedBinding = GetBluetoothControlBinding(controlDeviceUdid);
        _reverseControlSetupActive = fromReverseControl && configurationOnly;
        _bluetoothControlNoticePending = !fromReverseControl && _bluetoothNoticePolicy.ShouldShowForDevice(
            controlDeviceUdid);
        _bluetoothControlInputEnabled = !configurationOnly && savedBinding is not null &&
            !_bluetoothControlNoticePending;
        _bluetoothControlStarting = true;
        OnPropertyChanged(nameof(CanStartBluetoothControl));
        OnPropertyChanged(nameof(CanStopBluetoothControl));
        OnPropertyChanged(nameof(CanToggleBluetoothControl));
        OnPropertyChanged(nameof(CanStartUsbControl));
        OnPropertyChanged(nameof(CanStartWirelessControl));
        OnPropertyChanged(nameof(CanToggleWiredControl));
        OnPropertyChanged(nameof(CanToggleWirelessControl));
        StartBluetoothControlCommand.NotifyCanExecuteChanged();
        StopBluetoothControlCommand.NotifyCanExecuteChanged();
        ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleUsbControl));
        ToggleUsbControlCommand?.NotifyCanExecuteChanged();
        NotifyUsbControlStateChanged();

        try
        {
            AddDiagnosticLog(AppLog.Event("bluetooth_control_start_begin",
                ("device", AppLog.Device(controlDeviceUdid)),
                ("show_notice", _bluetoothControlNoticePending)));
            DiagnosticLogger.ReverseControl("bluetooth", "start_begin",
                ("device", AppLog.Device(controlDeviceUdid)),
                ("configuration_only", configurationOnly),
                ("has_binding", savedBinding is not null));
            var targetDeviceName = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, controlDeviceUdid))?.Name;
            var started = await _bluetoothControl.StartAsync(controlDeviceUdid,
                targetDeviceName, preserveExistingBinding, savedBinding,
                _shutdownCancellation.Token);
            if (!started)
            {
                var showFailureNotice = _bluetoothControlNoticePending;
                _bluetoothControlEnabled = false;
                _bluetoothControlConnected = false;
                ResetBluetoothControlInputState();
                NotifyBluetoothControlStateChanged();
                AddDiagnosticLog(AppLog.Event("bluetooth_control_start_complete",
                    ("success", false), ("advertising", false),
                    ("connected", false), ("error", _bluetoothControl.Error)));
                DiagnosticLogger.ReverseControlError("bluetooth", "start_failed",
                    ("error", _bluetoothControl.Error));
                if (showFailureNotice && Application.Current?.MainWindow is { } failedOwner)
                    BluetoothControlNoticeWindow.ShowFailure(failedOwner,
                        _bluetoothControl.Error ?? _bluetoothControl.Status);
                else
                    ShowReverseControlError("蓝牙", _bluetoothControl.Error ?? _bluetoothControl.Status);
                return;
            }

            // Advertising is a valid enabled state. Bluetooth drivers can
            // take much longer than eight seconds before iOS subscribes to
            // the HID reports, so remain available until explicitly stopped.
            _bluetoothControlEnabled = true;
            _bluetoothControlConnected = _bluetoothControl.IsConnected;
            var bluetoothIdentity = _identityResolver.Resolve(Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, controlDeviceUdid)));
            if (!configurationOnly && !string.IsNullOrWhiteSpace(bluetoothIdentity.AppleUdid))
                _reverseInputRouter.Begin(bluetoothIdentity.AppleUdid,
                    ReverseControlMode.Bluetooth);
            NotifyBluetoothControlStateChanged();
            if (!_bluetoothControlConnected && _bluetoothControlNoticePending &&
                Application.Current?.MainWindow is { } owner)
                BluetoothControlNoticeWindow.ShowWaiting(owner,
                    _bluetoothControl.SuggestedDeviceName);
            else if (!_bluetoothControlConnected && _bluetoothControlNoticePending)
                AllowBluetoothControlInput();
            AddUiLog(LocalizationService.Get(_bluetoothControlConnected
                ? "BluetoothControlConnected" : "BluetoothControlWaiting"));
            AddDiagnosticLog(AppLog.Event("bluetooth_control_start_complete",
                ("success", true), ("advertising", _bluetoothControl.IsAdvertising),
                ("connected", _bluetoothControlConnected)));
            DiagnosticLogger.ReverseControl("bluetooth", "start_complete",
                ("advertising", _bluetoothControl.IsAdvertising),
                ("connected", _bluetoothControlConnected));
            await EnsureBluetoothControlBindingAsync();
        }
        catch (OperationCanceledException)
        {
            _bluetoothControlEnabled = false;
            _bluetoothControlConnected = false;
            _reverseInputRouter.Stop();
            ResetBluetoothControlInputState();
            NotifyBluetoothControlStateChanged();
        }
        catch (Exception error)
        {
            var showFailureNotice = _bluetoothControlNoticePending;
            _bluetoothControlEnabled = false;
            _bluetoothControlConnected = false;
            ResetBluetoothControlInputState();
            NotifyBluetoothControlStateChanged();
            AddDiagnosticLog(AppLog.Event("bluetooth_control_start_failed",
                ("error", AppLog.Error(error))));
            DiagnosticLogger.ReverseControlError("bluetooth", "start_failed",
                ("error", AppLog.Error(error)));
            if (showFailureNotice && Application.Current?.MainWindow is { } owner)
                BluetoothControlNoticeWindow.ShowFailure(owner, error.Message);
            else
                ShowReverseControlError("蓝牙", error.Message);
        }
        finally
        {
            _bluetoothControlStarting = false;
            OnPropertyChanged(nameof(CanStartBluetoothControl));
            OnPropertyChanged(nameof(CanStopBluetoothControl));
            OnPropertyChanged(nameof(CanToggleBluetoothControl));
            StartBluetoothControlCommand.NotifyCanExecuteChanged();
            StopBluetoothControlCommand.NotifyCanExecuteChanged();
            ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
            ToggleUsbControlCommand?.NotifyCanExecuteChanged();
        }
    }

    private bool CanStartReverseBluetoothPeripheral =>
        !_bluetoothControlEnabled && !_bluetoothControlStarting &&
        !_bluetoothControlStopping && !IsBusy && !_usbControlEnabled &&
        !_usbControlStarting && !_usbControlStopping;

    internal bool IsBluetoothReverseControlEnabled => _bluetoothControlEnabled;
    internal bool IsBluetoothPeripheralAdvertising => _bluetoothControl.IsAdvertising;
    internal string BluetoothPeripheralStatus => _bluetoothControl.Error ?? _bluetoothControl.Status;
    internal async Task<bool> StartBluetoothPeripheralForConfigurationAsync(string udid)
    {
        await EnableBluetoothControlAsync(udid, fromReverseControl: true,
            configurationOnly: true);
        return _bluetoothControl.IsAdvertising;
    }
    internal Task StopBluetoothPeripheralConfigurationAsync() =>
        DisableBluetoothControlAsync();
    internal async Task<IReadOnlyList<BluetoothClientInfo>> GetReverseBluetoothClientsAsync() =>
        (await _bluetoothControl.GetSubscribedClientInfosAsync()).Select(MarkBluetoothClientBinding).ToArray();
    internal async Task<bool> BindReverseBluetoothClientAsync(string udid, string clientId)
    {
        // The reverse-control window is configuration-only. Persist the
        // stable client ID now; the active HID service binds and enables input
        // later when the user explicitly starts Bluetooth control.
        await Task.CompletedTask;
        return SaveBluetoothControlBinding(udid, clientId);
    }
    internal bool UnbindReverseBluetoothDevice(string udid)
    {
        var profile = _identityResolver.ResolveProfile(Devices.FirstOrDefault(device =>
            DeviceViewModel.UdidEquals(device.Udid, udid))).Profile;
        if (profile is null || !_reverseBindings.Unbind(profile.Id, DeviceIdentityType.Bluetooth))
            return false;
        if (DeviceViewModel.UdidEquals(_bluetoothControlDeviceUdid, udid) &&
            _bluetoothControlEnabled)
            _ = DisableBluetoothControlAsync();
        return true;
    }

    private bool AcknowledgeBluetoothHidReportMapChange()
    {
        if (Application.Current is not App app ||
            app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion >=
                BluetoothHidProtocol.ReportMapVersion)
            return true;
        if (Application.Current.MainWindow is not { } owner) return false;
        BluetoothControlNoticeWindow.ShowReportMapChanged(owner,
            _bluetoothControl.SuggestedDeviceName, () =>
            {
                app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion =
                    BluetoothHidProtocol.ReportMapVersion;
                if (!app.SaveUpdateSettings())
                    AddDiagnosticLog(AppLog.Event("bluetooth_hid_report_map_ack_save_failed"));
            });
        AddDiagnosticLog(AppLog.Event("bluetooth_hid_report_map_repair_required",
            ("version", BluetoothHidProtocol.ReportMapVersion)));
        return false;
    }

    private string? GetBluetoothControlBinding(string udid)
    {
        var device = Devices.FirstOrDefault(candidate => DeviceViewModel.UdidEquals(candidate.Udid, udid));
        return _identityResolver.ResolveProfile(device).Profile?.BluetoothIdentity?.StableId;
    }

    private bool SaveBluetoothControlBinding(string udid, string clientId)
    {
        var device = Devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, udid));
        var profile = _identityResolver.ResolveProfile(device).Profile;
        if (profile is null) return false;
        // Choosing a Bluetooth client is explicit user confirmation. Bluetooth
        // itself has no model fingerprint to compare against the profile.
        return _reverseBindings.Bind(profile.Id, DeviceIdentityType.Bluetooth, clientId,
            clientId, null, userConfirmed: true).Success;
    }

    private async Task EnsureBluetoothControlBindingAsync()
    {
        if (_reverseControlSetupActive) return;
        if (!_bluetoothControlEnabled ||
            Interlocked.Exchange(ref _bluetoothBindingPromptInFlight, 1) != 0)
            return;
        try
        {
            var targetUdid = _bluetoothControlDeviceUdid;
            if (string.IsNullOrWhiteSpace(targetUdid)) return;
            var savedBinding = GetBluetoothControlBinding(targetUdid);
            if (savedBinding is not null && string.Equals(savedBinding,
                    _bluetoothControl.TargetClientId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_bluetoothControl.IsTargetClientConnected)
                    await CompleteBluetoothConnectionAsync();
                return;
            }

            IReadOnlyList<BluetoothClientInfo> clients;
            do
            {
                clients = await _bluetoothControl.GetSubscribedClientInfosAsync();
                if (clients.Count != 0) break;
                await Task.Delay(500);
            }
            while (_bluetoothControlEnabled &&
                DeviceViewModel.UdidEquals(_bluetoothControlDeviceUdid, targetUdid));
            if (clients.Count == 0) return;
            if (savedBinding is not null && clients.Any(client =>
                    string.Equals(client.Id, savedBinding,
                        StringComparison.OrdinalIgnoreCase)))
            {
                // The GATT subscription event can arrive before the route
                // refresh publishes TargetClientId. Restore the persisted
                // client directly instead of asking the user to bind again.
                if (await _bluetoothControl.BindTargetClientAsync(savedBinding))
                {
                    _bluetoothControlConnected =
                        _bluetoothControl.IsTargetClientConnected;
                    _bluetoothControlInputEnabled = !_bluetoothControlNoticePending;
                    NotifyBluetoothControlStateChanged();
                    if (_bluetoothControlConnected)
                        await CompleteBluetoothConnectionAsync();
                }
                return;
            }
            clients = clients.Select(MarkBluetoothClientBinding).ToArray();
            var owner = Application.Current?.MainWindow;
            if (owner is null) return;
            var targetName = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, targetUdid))?.DisplayName ?? targetUdid;
            if (!_bluetoothBindingPromptedTargets.Add(targetUdid)) return;
            BluetoothControlNoticeWindow.TryCloseActive();
            var selected = await owner.Dispatcher.InvokeAsync(() =>
                BluetoothClientBindingWindow.Show(owner, targetName, clients,
                    _bluetoothControl.TargetClientId,
                    RefreshBluetoothBindingClientsAsync,
                    UnbindBluetoothControlBinding));
            if (string.IsNullOrWhiteSpace(selected) ||
                !await _bluetoothControl.BindTargetClientAsync(selected)) return;
            if (!SaveBluetoothControlBinding(targetUdid, selected))
            {
                AddDiagnosticLog(AppLog.Event("bluetooth_control_binding_save_failed",
                    ("device", AppLog.Device(targetUdid))));
                return;
            }
            _bluetoothControlConnected = _bluetoothControl.IsTargetClientConnected;
            _bluetoothControlInputEnabled = !_bluetoothControlNoticePending;
            NotifyBluetoothControlStateChanged();
            if (_bluetoothControlConnected)
                await CompleteBluetoothConnectionAsync();
        }
        finally
        {
            Volatile.Write(ref _bluetoothBindingPromptInFlight, 0);
        }
    }

    private BluetoothClientInfo MarkBluetoothClientBinding(BluetoothClientInfo client)
    {
        var profile = _reverseBindings.FindByIdentity(DeviceIdentityType.Bluetooth, client.Id);
        return profile is null ? client : client with { BoundDeviceName = profile.DisplayName };
    }

    private async Task<IReadOnlyList<BluetoothClientInfo>>
        RefreshBluetoothBindingClientsAsync()
    {
        var clients = await _bluetoothControl.GetSubscribedClientInfosAsync();
        return clients.Select(MarkBluetoothClientBinding).ToArray();
    }

    internal bool UnbindBluetoothControlBinding(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        var profile = _reverseBindings.FindByIdentity(DeviceIdentityType.Bluetooth, clientId);
        if (profile is null || !_reverseBindings.UnbindBluetoothByStableId(clientId)) return false;
        _bluetoothBindingPromptedTargets.Clear();
        var activeProfile = _identityResolver.ResolveProfile(Devices.FirstOrDefault(device =>
            DeviceViewModel.UdidEquals(device.Udid, _bluetoothControlDeviceUdid))).Profile;
        if (_bluetoothControlEnabled && activeProfile?.Id == profile.Id)
            _ = DisableBluetoothControlAsync();
        AddDiagnosticLog(AppLog.Event("bluetooth_control_binding_removed",
            ("profile", profile.Id)));
        return true;
    }

    internal async Task<bool> ClearBluetoothControlBindingsAsync()
    {
        if (_reverseBindings.ClearBluetoothBindings() == 0)
        {
            if (_bluetoothControlEnabled) await DisableBluetoothControlAsync();
            return true;
        }
        _bluetoothBindingPromptedTargets.Clear();
        if (_bluetoothControlEnabled) await DisableBluetoothControlAsync();
        return true;
    }

    internal async Task SwitchBluetoothControlTargetAsync(string? targetDeviceUdid)
    {
        if (!_bluetoothControlEnabled || _bluetoothControlStopping ||
            _bluetoothControlStarting || DeviceViewModel.UdidEquals(
                _bluetoothControlDeviceUdid, targetDeviceUdid))
            return;

        AddDiagnosticLog(AppLog.Event("bluetooth_control_target_switch_begin",
            ("from", AppLog.Device(_bluetoothControlDeviceUdid)),
            ("to", AppLog.Device(targetDeviceUdid))));
        await DisableBluetoothControlAsync();
        if (!string.IsNullOrWhiteSpace(targetDeviceUdid))
            await EnableBluetoothControlAsync(targetDeviceUdid,
                preserveExistingBinding: true);
        AddDiagnosticLog(AppLog.Event("bluetooth_control_target_switch_complete",
            ("target", AppLog.Device(_bluetoothControlDeviceUdid)),
            ("enabled", _bluetoothControlEnabled)));
    }

    internal async Task DisableBluetoothControlAsync()
    {
        if (_bluetoothControlStopping ||
            (!_bluetoothControlEnabled && !_bluetoothControl.IsAdvertising)) return;
        _bluetoothControlStopping = true;
        var controlDeviceUdid = _bluetoothControlDeviceUdid;
        AddDiagnosticLog(AppLog.Event("bluetooth_control_stop_begin",
            ("device", AppLog.Device(controlDeviceUdid)),
            ("connected", _bluetoothControlConnected)));
        DiagnosticLogger.ReverseControl("bluetooth", "stop_begin",
            ("device", AppLog.Device(controlDeviceUdid)),
            ("connected", _bluetoothControlConnected));
        try
        {
            _bluetoothControlEnabled = false;
            _reverseControlSetupActive = false;
            _bluetoothControlConnected = false;
            _bluetoothControlCalibrated = false;
            ResetBluetoothControlInputState();
            NotifyBluetoothControlStateChanged();
            BluetoothControlNoticeWindow.TryCloseActive();
            if (!string.IsNullOrWhiteSpace(controlDeviceUdid))
                _bluetoothBindingPromptedTargets.Remove(controlDeviceUdid);
            await _bluetoothControl.ReleaseAllAsync();
            await _bluetoothControl.StopAsync();
            AddDiagnosticLog(AppLog.Event("bluetooth_control_stop_complete"));
            DiagnosticLogger.ReverseControl("bluetooth", "stop_complete");
        }
        finally
        {
            _bluetoothControlStopping = false;
            OnPropertyChanged(nameof(CanStartBluetoothControl));
            OnPropertyChanged(nameof(CanStopBluetoothControl));
            OnPropertyChanged(nameof(CanToggleBluetoothControl));
            StartBluetoothControlCommand.NotifyCanExecuteChanged();
            StopBluetoothControlCommand.NotifyCanExecuteChanged();
            ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task CompleteBluetoothConnectionAsync()
    {
        if (!_bluetoothControlEnabled || !_bluetoothControlConnected ||
            _bluetoothControlCalibrated || _bluetoothCalibrationInProgress) return;
        _bluetoothCalibrationInProgress = true;
        try
        {
            var calibrated = await CalibrateBluetoothControlAsync();
            if (!_bluetoothControlEnabled || !_bluetoothControl.IsConnected ||
                !calibrated) return;
            _bluetoothControlCalibrated = true;
            // The integrated reverse-control wizard intentionally does not
            // show the legacy waiting notice. Once the selected stable client
            // is bound, allow input directly after calibration.
            if (!_bluetoothControlNoticePending && !_reverseControlSetupActive)
                _bluetoothControlInputEnabled =
                    !string.IsNullOrWhiteSpace(_bluetoothControlDeviceUdid) &&
                    GetBluetoothControlBinding(_bluetoothControlDeviceUdid) is not null;
            NotifyBluetoothControlStateChanged();
            AddUiLog(LocalizationService.Get("BluetoothControlConnected"));
            AddDiagnosticLog(AppLog.Event("bluetooth_control_connected",
                ("device", AppLog.Device(_bluetoothControlDeviceUdid))));
            DiagnosticLogger.ReverseControl("bluetooth", "connected",
                ("device", AppLog.Device(_bluetoothControlDeviceUdid)),
                ("input_enabled", _bluetoothControlInputEnabled));
            if (_bluetoothControlNoticePending && !_bluetoothControlInputEnabled &&
                Application.Current?.MainWindow is { } owner)
                BluetoothControlNoticeWindow.ShowConnected(owner);
            else if (_bluetoothControlNoticePending)
                AllowBluetoothControlInput();
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("bluetooth_control_calibration_failed",
                ("error", AppLog.Error(error))));
        }
        finally
        {
            _bluetoothCalibrationInProgress = false;
        }
    }

    internal bool IsUsbControlTarget(string? udid)
    {
        var device = Devices.FirstOrDefault(candidate => DeviceViewModel.UdidEquals(candidate.Udid, udid));
        var identity = _identityResolver.Resolve(device);
        return (_usbControlEnabled && _reverseInputRouter.Owns(identity.AppleUdid ?? string.Empty, ReverseControlMode.Usb)) ||
            (_wirelessControlEnabled && _reverseInputRouter.Owns(identity.AppleUdid ?? string.Empty, ReverseControlMode.Wireless));
    }

    internal bool IsWirelessControlTarget(string? udid) =>
        _wirelessControlEnabled && DeviceViewModel.UdidEquals(_wirelessControlDeviceUdid, udid);

    internal string? GetAutomaticUsbControlDeviceId() => Devices
        .FirstOrDefault(device => !device.IsWireless && !device.IsMediaCast)?.Udid;

    internal string? ResolveAppleUdid(string mirrorDeviceId) =>
        _identityResolver.Resolve(Devices.FirstOrDefault(d =>
            DeviceViewModel.UdidEquals(d.Udid, mirrorDeviceId))).AppleUdid;

    internal async Task SendUsbTouchAsync(string action, double normalizedX,
        double normalizedY, CancellationToken cancellationToken = default)
    {
        // Select only a ready bridge. During a transport switch the old USB
        // process can still be referenced while the wireless process is
        // already ready; sending to the stale instance drops the gesture.
        var bridge = _usbTouchBridge is { IsReady: true } ? _usbTouchBridge :
            _wirelessTouchBridge is { IsReady: true } ? _wirelessTouchBridge : null;
        if ((!_usbControlEnabled || !_usbControlConnected) &&
            (!_wirelessControlEnabled || !_wirelessControlConnected) || bridge is null ||
            !CoreDeviceTouchProtocol.IsNormalizedCoordinate(normalizedX) ||
            !CoreDeviceTouchProtocol.IsNormalizedCoordinate(normalizedY)) return;
        var point = new TouchPoint(1, action, normalizedX, normalizedY);
        await bridge.SendTouchBatchAsync([point],
            DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(),
            Interlocked.Increment(ref _usbTouchSequence), cancellationToken);
    }

    internal async Task ToggleUsbControlAsync()
    {
        if (_usbControlEnabled) await DisableUsbControlAsync();
        else if (_wirelessControlEnabled) await DisableWirelessControlAsync();
        else if (CanEnableWirelessControlFor(SelectedDevice)) await EnableWirelessControlAsync();
        else await EnableUsbControlAsync();
    }

    // Toolbar and preview-menu commands intentionally start a selected transport
    // only. They must not inherit the legacy toggle's "click again to stop"
    // behavior because each transport now has its own direct entry point.
    internal Task StartBluetoothControlAsync(string? targetDeviceUdid = null) =>
        EnableBluetoothControlAsync(targetDeviceUdid);

    internal async Task ToggleWiredControlAsync()
    {
        if (_usbControlEnabled) await DisableUsbControlAsync();
        else await EnableUsbControlAsync();
    }

    internal async Task ToggleWirelessControlAsync()
    {
        if (_wirelessControlEnabled) await DisableWirelessControlAsync();
        else await EnableWirelessControlAsync();
    }

    internal async Task StartUsbControlAsync(string? targetDeviceUdid = null)
    {
        if (!string.IsNullOrWhiteSpace(targetDeviceUdid))
        {
            var target = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, targetDeviceUdid));
            if (target is null) return;
            SelectedDevice = target;
        }
        if (_usbControlEnabled || _wirelessControlEnabled) return;
        await EnableUsbControlAsync();
    }

    internal async Task StartWirelessControlAsync(string? targetDeviceUdid = null)
    {
        if (!string.IsNullOrWhiteSpace(targetDeviceUdid))
        {
            var target = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, targetDeviceUdid));
            if (target is null) return;
            SelectedDevice = target;
        }
        if (_usbControlEnabled || _wirelessControlEnabled) return;
        await EnableWirelessControlAsync();
    }

    private async Task EnableWirelessControlAsync()
    {
        var device = SelectedDevice;
        var boundUdid = device is null ? null : _identityResolver.Resolve(device).AppleUdid;
        if (device is null || string.IsNullOrWhiteSpace(boundUdid) || !CanEnableWirelessControlFor(device)) return;
        if (!ConfirmReverseControlPrerequisites(wireless: true)) return;
        _usbControlStarting = true;
        _usbControlStatus = "正在连接无线反控桥接器";
        DiagnosticLogger.ReverseControl("wireless", "start_begin",
            ("device", AppLog.Device(device.Udid)), ("apple_device", AppLog.Device(boundUdid)));
        NotifyUsbControlStateChanged();
        var bridge = new UsbTouchBridgeHost();
        bridge.StatusChanged += (_, bridgeEvent) =>
        {
            LogBridgeEvent("wireless", bridgeEvent);
            UpdateReverseControlStartupStatus("无线", bridgeEvent);
            if (bridgeEvent.EventName is not ("error" or "status") ||
                (bridgeEvent.EventName == "status" && bridgeEvent.Code != "terminated")) return;
            _wirelessControlConnected = false;
            _usbControlStatus = "无线反控桥接通道已断开";
            ShowReverseControlError("无线", FormatReverseControlBridgeError(bridgeEvent));
            if (Application.Current?.Dispatcher is { } dispatcher)
                dispatcher.BeginInvoke(async () => await DisableWirelessControlAsync());
        };
        var lockdownGateHeld = false;
        try
        {
            _usbControlStatus = "正在连接无线反控桥接器";
            NotifyUsbControlStateChanged();
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
            // The bridge owns Network usbmux/mDNS discovery. Do not gate this
            // path on the optional system Bonjour service or launch repair UI.
            await _lockdownHandshakeGate.WaitAsync(_shutdownCancellation.Token);
            lockdownGateHeld = true;
            await bridge.StartAsync(UsbTouchTransport.Wireless, boundUdid, bridgePath,
                _shutdownCancellation.Token);
            _wirelessTouchBridge = bridge;
            _wirelessControlEnabled = _wirelessControlConnected = true;
            _wirelessControlDeviceUdid = device.Udid;
            _reverseInputRouter.Begin(boundUdid, ReverseControlMode.Wireless);
            _usbControlStatus = bridge.AuthMode == "direct"
                ? "无线反控已启用（直接 HID）"
                : bridge.GateOpen ? "无线反控已启用" : "无线反控已连接";
            DiagnosticLogger.ReverseControl("wireless", "start_complete",
                ("device", AppLog.Device(device.Udid)), ("gate_open", bridge.GateOpen),
                ("auth_mode", bridge.AuthMode));
        }
        catch (Exception error)
        {
            await bridge.DisposeAsync();
            _usbControlStatus = $"无线反控连接失败：{GetUsbControlFailureMessage(error, bridge)}";
            ShowReverseControlError("无线", GetUsbControlFailureMessage(error, bridge));
            DiagnosticLogger.ReverseControlError("wireless", "start_failed",
                ("device", AppLog.Device(device.Udid)), ("error", AppLog.Error(error)),
                ("bridge_code", bridge.LastErrorCode), ("bridge_diagnostic", bridge.LastDiagnostic));
        }
        finally
        {
            if (lockdownGateHeld) _lockdownHandshakeGate.Release();
            _usbControlStarting = false;
            NotifyUsbControlStateChanged();
        }
    }

    private async Task DisableWirelessControlAsync()
    {
        var bridge = _wirelessTouchBridge;
        _wirelessTouchBridge = null;
        _wirelessControlEnabled = _wirelessControlConnected = false;
        _wirelessControlDeviceUdid = null;
        _reverseInputRouter.Stop();
        try
        {
            if (bridge is not null) await bridge.DisposeAsync();
            _usbControlStatus = "无线反控未启用";
            DiagnosticLogger.ReverseControl("wireless", "stop_complete");
        }
        catch (Exception error)
        {
            _usbControlStatus = $"无线反控关闭失败：{error.Message}";
            AddDiagnosticLog(AppLog.Event("wireless_control_stop_failed",
                ("error", AppLog.Error(error))));
            DiagnosticLogger.ReverseControlError("wireless", "stop_failed",
                ("error", AppLog.Error(error)));
            ShowReverseControlError("无线", $"关闭失败：{error.Message}");
        }
        finally { NotifyUsbControlStateChanged(); }
    }

    private async Task EnableUsbControlAsync()
    {
        var device = SelectedDevice;
        if (!CanEnableUsbControlFor(device)) return;
        if (!ConfirmReverseControlPrerequisites(wireless: false)) return;
        if (_bluetoothControlEnabled) await DisableBluetoothControlAsync();
        if (device is null) return;
        var boundUsbUdid = GetUsbControlBinding(device.Udid);
        if (string.IsNullOrWhiteSpace(boundUsbUdid)) return;
        _usbControlStarting = true;
        _usbControlDeviceUdid = device.Udid;
        _usbControlStatus = "正在连接 USB 控制";
        DiagnosticLogger.ReverseControl("usb", "start_begin",
            ("device", AppLog.Device(device.Udid)), ("apple_device", AppLog.Device(boundUsbUdid)));
        NotifyUsbControlStateChanged();
        var bridge = new UsbTouchBridgeHost();
        bridge.StatusChanged += (_, bridgeEvent) =>
        {
            LogBridgeEvent("usb", bridgeEvent);
            UpdateReverseControlStartupStatus("USB", bridgeEvent);
            if (bridgeEvent.EventName is not ("error" or "status") ||
                (bridgeEvent.EventName == "status" && bridgeEvent.Code != "terminated")) return;
            _usbControlConnected = false;
            _usbControlStatus = "USB 触控通道已断开";
            if (Application.Current?.Dispatcher is { } dispatcher)
                dispatcher.BeginInvoke(async () => await DisableUsbControlAsync());
        };
        var lockdownGateHeld = false;
        try
        {
            var bridgePath = GetUsbDirectControlBridgePath();
            // Bind the AirPlay mirror session to exactly one trusted USB
            // device. Never let the bridge choose the first connected phone.
            await _lockdownHandshakeGate.WaitAsync(_shutdownCancellation.Token);
            lockdownGateHeld = true;
            await bridge.StartAsync(UsbTouchTransport.Usb, boundUsbUdid, bridgePath,
                _shutdownCancellation.Token);
            _usbTouchBridge = bridge;
            _usbControlEnabled = true;
            _usbControlConnected = true;
            _reverseInputRouter.Begin(boundUsbUdid, ReverseControlMode.Usb);
            _usbControlStatus = bridge.AuthMode == "direct"
                ? "USB 控制已启用（直接 HID）"
                : bridge.GateOpen
                ? "USB 控制已启用"
                : "USB 控制已连接（设备认证状态需以实测为准）";
            AddUiLog(_usbControlStatus);
            AddDiagnosticLog(AppLog.Event("usb_control_enabled",
                ("device", AppLog.Device(device.Udid)), ("gate_open", bridge.GateOpen)));
            DiagnosticLogger.ReverseControl("usb", "start_complete",
                ("device", AppLog.Device(device.Udid)), ("gate_open", bridge.GateOpen),
                ("auth_mode", bridge.AuthMode));
        }
        catch (Exception error)
        {
            await bridge.DisposeAsync();
            var message = GetUsbControlFailureMessage(error, bridge);
            _usbControlStatus = $"USB 控制连接失败：{message}";
            ShowReverseControlError("有线", message);
            _usbControlDeviceUdid = null;
            AddDiagnosticLog(AppLog.Event("usb_control_enable_failed",
                ("device", AppLog.Device(device.Udid)), ("error", AppLog.Error(error)),
                ("bridge_code", bridge.LastErrorCode),
                ("bridge_diagnostic", bridge.LastDiagnostic)));
            DiagnosticLogger.ReverseControlError("usb", "start_failed",
                ("device", AppLog.Device(device.Udid)), ("error", AppLog.Error(error)),
                ("bridge_code", bridge.LastErrorCode), ("bridge_diagnostic", bridge.LastDiagnostic));
        }
        finally
        {
            if (lockdownGateHeld) _lockdownHandshakeGate.Release();
            _usbControlStarting = false;
            OnPropertyChanged(nameof(UsbControlActionText));
            NotifyUsbControlStateChanged();
        }
    }

    private bool ConfirmReverseControlPrerequisites(bool wireless)
    {
        var acknowledged = wireless
            ? _wirelessControlPrerequisiteAcknowledged
            : _wiredControlPrerequisiteAcknowledged;
        if (acknowledged) return true;

        if (!AppPromptWindow.ConfirmReverseControlPrerequisite(
                Application.Current.MainWindow, wireless))
        {
            DiagnosticLogger.ReverseControl(wireless ? "wireless" : "usb",
                "prerequisites_cancelled");
            return false;
        }

        if (wireless) _wirelessControlPrerequisiteAcknowledged = true;
        else _wiredControlPrerequisiteAcknowledged = true;
        DiagnosticLogger.ReverseControl(wireless ? "wireless" : "usb",
            "prerequisites_acknowledged");
        return true;
    }

    private static string GetUsbDirectControlBridgePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
    }

    private static string GetUsbControlFailureMessage(Exception error,
        UsbTouchBridgeHost bridge)
    {
        var raw = string.IsNullOrWhiteSpace(error.Message)
            ? string.Empty : AppLog.Sanitize(error.Message);
        if (string.Equals(bridge.LastErrorCode, "apple_usbmux_unavailable",
                StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("ConnectionFailedToUsbmuxd", StringComparison.OrdinalIgnoreCase))
            return "Apple USB 配对服务未就绪。请在驱动管理中安装或修复 Apple Devices/iTunes 支持，连接并解锁 iPhone 后重试。";
        if (string.Equals(bridge.LastErrorCode, "apple_device_not_trusted",
                StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("NotPaired", StringComparison.OrdinalIgnoreCase))
            return "此 iPhone 尚未信任当前 Windows 帐户。请保持手机解锁，重新插拔数据线并在手机上点按“信任”。";
        if (bridge.LastErrorCode is "developer_mode_required" or
            "developer_mode_check_failed")
            return "此设备未开启开发者模式。请在 iPhone/iPad 的“设置 > 隐私与安全性 > 开发者模式”中开启并重启设备后重试。";
        if (string.Equals(bridge.LastErrorCode, "developer_image_required",
                StringComparison.OrdinalIgnoreCase))
            return "设备尚未挂载 Personalized Developer Disk Image。请使用与设备系统匹配的官方 Xcode 开发者镜像完成挂载后重试。";
        if (bridge.LastErrorCode is "developer_image_download_failed" or
            "developer_image_download_timeout")
            return "开发者镜像下载失败或超时。请检查 GitHub 网络连接后重试，也可通过 IPHONE_MIRROR_DDI_DIR 提供官方本地镜像。";
        if (bridge.LastErrorCode is "developer_image_download_rate_limited" or
            "developer_image_download_integrity_failed")
            return "开发者镜像下载未通过 GitHub 内容校验或当前 API 被限流。请稍后重试；桥接器会校验 Git blob 身份、文件大小和本地 SHA-256。";
        if (string.Equals(bridge.LastErrorCode, "developer_image_download_incompatible",
                StringComparison.OrdinalIgnoreCase))
            return "当前桥接器没有与此运行时匹配的开发者镜像下载元数据。请更新 iPhoneMirror，或通过 IPHONE_MIRROR_DDI_DIR 提供官方本地镜像。";
        if (string.Equals(bridge.LastErrorCode, "developer_image_tss_failed",
                StringComparison.OrdinalIgnoreCase))
            return "开发者镜像已下载，但 Apple 个性化服务或设备挂载失败。请检查 Apple 服务网络、保持设备解锁，然后重试。";
        if (string.Equals(bridge.LastErrorCode, "developer_image_remount_failed",
                StringComparison.OrdinalIgnoreCase))
            return "无法刷新旧的 Personalized Developer Disk Image。请关闭可能占用设备的 Xcode/开发工具，重启 iPhone 后再试。";
        if (bridge.LastErrorCode is "developer_image_bundle_invalid" or
            "developer_image_mount_failed" or "developer_image_mount_timeout")
            return "本地开发者镜像无效或无法挂载。请确认镜像来自官方 Xcode、与设备系统兼容，并包含 Image.dmg、BuildManifest.plist 和 Image.trustcache。";
        if (string.Equals(bridge.LastErrorCode, "remote_control_unsupported_ios",
                StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("9021", StringComparison.OrdinalIgnoreCase))
            return "设备拒绝了媒体流认证（9021），且没有可用的直接 Universal HID 触控通道。请重启 iPhone 后重试以清除旧开发者镜像；若仍失败，请使用蓝牙反控。";
        if (string.Equals(bridge.LastErrorCode, "touch_surface_unavailable",
                StringComparison.OrdinalIgnoreCase))
            return "已建立 CoreDevice 会话，但开发者镜像没有发布 mainTouchscreen（257）触控面。请重启 iPhone 后重试，让桥接器自动准备匹配的镜像。";
        if (string.Equals(bridge.LastErrorCode, "wireless_remote_pairing_required",
                StringComparison.OrdinalIgnoreCase))
            return "当前 Windows 帐户尚未完成此设备的无线 CoreDevice 配对。请先通过 USB 连接并解锁 iPhone，完成一次 USB 反控初始化后再试无线反控。";
        if (string.Equals(bridge.LastErrorCode, "wireless_device_not_discoverable",
                StringComparison.OrdinalIgnoreCase))
            return "未发现 iPhone 的无线 CoreDevice 服务。请保持 iPhone 解锁、与电脑处于同一局域网，并允许 Windows 防火墙通过本地网络发现。";
        if (string.Equals(bridge.LastErrorCode, "wireless_remote_pairing_failed",
                StringComparison.OrdinalIgnoreCase))
            return "无线 CoreDevice 配对或隧道建立失败。请先用 USB 连接一次并完成初始化，随后确认 iPhone 已解锁且局域网未隔离。";
        if (bridge.LastErrorCode is "remote_control_gate_unavailable" or
            "remote_control_gate_closed")
            return "设备未确认 CoreDevice 触控认证已开启。为避免输入被系统静默丢弃，反控未启动。";
        if (raw.Contains("com.apple.coredevice.hid.universalhidservice",
                StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("no such service", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("no_such_service", StringComparison.OrdinalIgnoreCase) ||
            (bridge.LastErrorCode?.Contains("nosuchservice",
                StringComparison.OrdinalIgnoreCase) ?? false))
            return "设备暂未提供触控反控服务。请保持设备解锁并信任此电脑后重试；若仍失败，请改用蓝牙反控或无线反控。";
        if (string.Equals(bridge.LastErrorCode, "apple_device_not_found",
                StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Device not found", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("devicenotfound", StringComparison.OrdinalIgnoreCase))
            return "未找到这台设备的 Apple 网络配对会话。请先用数据线连接一次，在 Apple Devices 或 iTunes 中启用“通过 Wi-Fi 与此 iPhone 同步”，保持手机解锁并与电脑连接同一局域网后再试。";
        if (raw.Contains("socket connection broken", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("muxexception", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bridge.LastErrorCode, "muxexception",
                StringComparison.OrdinalIgnoreCase))
            return "Apple USB 服务在设备握手时中断了连接。请保持设备解锁并确认已信任此电脑；若仍失败，请重新插拔数据线后重试。";
        return string.IsNullOrWhiteSpace(raw)
            ? "USB 触控桥接器未提供错误详情。请确认设备已通过 USB 连接并已信任此电脑。"
            : raw;
    }

    private void UpdateReverseControlStartupStatus(string transport,
        BridgeStatusEventArgs bridgeEvent)
    {
        if (!string.Equals(bridgeEvent.EventName, "status",
                StringComparison.OrdinalIgnoreCase)) return;
        var status = bridgeEvent.Code switch
        {
            "checking_developer_environment" => $"正在检查{transport}反控的开发者环境",
            "mounting_developer_image" => "正在自动准备开发者镜像（首次可能需要约 3 分钟）",
            "testing_developer_image_sources" => "正在检查 GitHub 开发者镜像下载",
            "downloading_developer_image" => "正在下载并校验开发者镜像",
            "remounting_developer_image" => "正在刷新旧开发者镜像以启用触控服务",
            "discovering_wireless_device" => "正在发现无线 CoreDevice 设备",
            "waiting_for_hid_service" => "正在等待开发者镜像发布触控服务",
            "initializing_touch" => $"正在初始化{transport}触控通道",
            _ => null,
        };
        if (status is null) return;

        void Apply()
        {
            if (!_usbControlStarting) return;
            _usbControlStatus = status;
            NotifyUsbControlStateChanged();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private static string FormatReverseControlBridgeError(BridgeStatusEventArgs bridgeEvent) =>
        string.IsNullOrWhiteSpace(bridgeEvent.Message)
            ? (string.IsNullOrWhiteSpace(bridgeEvent.Code) ? "反控桥接器报告未知错误。" :
                $"桥接器错误代码：{bridgeEvent.Code}")
            : bridgeEvent.Message!;

    private void ShowReverseControlError(string transport, string? detail)
    {
        if (Interlocked.Exchange(ref _reverseControlErrorPromptInFlight, 1) != 0)
            return;
        var body = string.IsNullOrWhiteSpace(detail)
            ? LocalizationService.Format("ReverseControlErrorBodyFormat", transport,
                "未提供详细信息")
            : LocalizationService.Format("ReverseControlErrorBodyFormat", transport,
                detail.Trim());
        void Show()
        {
            try
            {
                CaptureStatusNoticeWindow.ShowError(
                    LocalizationService.Get("ReverseControlErrorTitle"), body,
                    usbConfiguration: false, reverseControl: true);
            }
            finally
            {
                Volatile.Write(ref _reverseControlErrorPromptInFlight, 0);
            }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { Volatile.Write(ref _reverseControlErrorPromptInFlight, 0); return; }
        if (dispatcher.CheckAccess()) Show();
        else dispatcher.BeginInvoke(Show);
    }

    private static void LogBridgeEvent(string mode, BridgeStatusEventArgs bridgeEvent)
    {
        var isFailure = string.Equals(bridgeEvent.EventName, "error",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bridgeEvent.Code, "terminated", StringComparison.OrdinalIgnoreCase);
        var fields = new (string Key, object? Value)[]
        {
            ("bridge_event", bridgeEvent.EventName),
            ("bridge_code", bridgeEvent.Code),
            ("bridge_message", bridgeEvent.Message),
        };
        if (isFailure)
            DiagnosticLogger.ReverseControlError(mode, "bridge_event", fields);
        else
            DiagnosticLogger.ReverseControl(mode, "bridge_event", fields);
    }

    internal async Task DisableUsbControlAsync()
    {
        if (!_usbControlEnabled && _wirelessControlEnabled)
        {
            await DisableWirelessControlAsync();
            return;
        }
        if (_usbControlStopping || (!_usbControlEnabled && _usbTouchBridge is null)) return;
        _usbControlStopping = true;
        var bridge = _usbTouchBridge;
        _usbTouchBridge = null;
        _usbControlEnabled = false;
        _usbControlConnected = false;
        _reverseInputRouter.Stop();
        _usbControlStatus = "正在关闭 USB 控制";
        OnPropertyChanged(nameof(UsbControlActionText));
        NotifyUsbControlStateChanged();
        try
        {
            if (bridge is not null) await bridge.DisposeAsync();
            _usbControlStatus = "USB 控制未启用";
            AddDiagnosticLog(AppLog.Event("usb_control_disabled"));
            DiagnosticLogger.ReverseControl("usb", "stop_complete");
        }
        catch (Exception error)
        {
            _usbControlStatus = $"USB 控制关闭失败：{error.Message}";
            AddDiagnosticLog(AppLog.Event("usb_control_stop_failed",
                ("error", AppLog.Error(error))));
            DiagnosticLogger.ReverseControlError("usb", "stop_failed",
                ("error", AppLog.Error(error)));
            ShowReverseControlError("有线", $"关闭失败：{error.Message}");
        }
        finally
        {
            _usbControlDeviceUdid = null;
            _usbControlStopping = false;
            OnPropertyChanged(nameof(UsbControlActionText));
            NotifyUsbControlStateChanged();
        }
    }

    private void NotifyUsbControlStateChanged()
    {
        OnPropertyChanged(nameof(IsUsbControlEnabled));
        OnPropertyChanged(nameof(UsbControlIsInputEnabled));
        OnPropertyChanged(nameof(UsbControlStatus));
        OnPropertyChanged(nameof(UsbControlActionText));
        OnPropertyChanged(nameof(CanToggleUsbControl));
        OnPropertyChanged(nameof(CanStartUsbControl));
        OnPropertyChanged(nameof(CanStartWirelessControl));
        OnPropertyChanged(nameof(CanToggleWiredControl));
        OnPropertyChanged(nameof(CanToggleWirelessControl));
        OnPropertyChanged(nameof(WiredControlActionText));
        OnPropertyChanged(nameof(WirelessControlActionText));
        OnPropertyChanged(nameof(UsbControlTargetUdid));
        ToggleUsbControlCommand.NotifyCanExecuteChanged();
    }

    private void NotifyBluetoothControlStateChanged()
    {
        OnPropertyChanged(nameof(IsBluetoothControlEnabled));
        OnPropertyChanged(nameof(BluetoothControlIsConnected));
        OnPropertyChanged(nameof(BluetoothControlIsInputEnabled));
        OnPropertyChanged(nameof(BluetoothControlTargetUdid));
        OnPropertyChanged(nameof(CanStartBluetoothControl));
        OnPropertyChanged(nameof(CanStopBluetoothControl));
        OnPropertyChanged(nameof(CanToggleBluetoothControl));
        OnPropertyChanged(nameof(BluetoothControlActionText));
        StartBluetoothControlCommand.NotifyCanExecuteChanged();
        StopBluetoothControlCommand.NotifyCanExecuteChanged();
        ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
    }

    private void OnBluetoothControlNoticeClosed(object? sender, EventArgs e)
    {
        void Update()
        {
            if (_disposed || !_bluetoothControlEnabled ||
                !_bluetoothControlNoticePending) return;
            if (GetBluetoothControlBinding(_bluetoothControlDeviceUdid ?? string.Empty)
                is not null)
                AllowBluetoothControlInput();
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true) Update();
        else Application.Current?.Dispatcher.BeginInvoke(Update);
    }

    private void AllowBluetoothControlInput()
    {
        if (_bluetoothControlInputEnabled) return;
        _bluetoothControlInputEnabled = true;
        _bluetoothControlNoticePending = false;
        AddDiagnosticLog(AppLog.Event("bluetooth_control_input_enabled",
            ("device", AppLog.Device(_bluetoothControlDeviceUdid)),
            ("connected", _bluetoothControlConnected)));
        OnPropertyChanged(nameof(BluetoothControlIsInputEnabled));
    }

    private void ResetBluetoothControlInputState()
    {
        _bluetoothControlInputEnabled = false;
        _bluetoothControlNoticePending = false;
        _bluetoothControlDeviceUdid = null;
    }

    internal async Task ToggleBluetoothControlAsync()
    {
        if (!IsBluetoothControlEnabled)
        {
            await EnableBluetoothControlAsync();
            return;
        }
        if (!_bluetoothControlInputEnabled)
        {
            _reverseControlSetupActive = false;
            _bluetoothControlInputEnabled = true;
            NotifyBluetoothControlStateChanged();
            if (_bluetoothControlConnected) await CompleteBluetoothConnectionAsync();
            return;
        }
        await DisableBluetoothControlAsync();
    }

    private Task StopBluetoothControlAsync() => DisableBluetoothControlAsync();

    public async Task RefreshAsync(bool forceDeviceEnumeration = false)
    {
        if (_disposed) return;
        if (!_sessions.AnySession)
            Interlocked.Exchange(ref _activeSessionStatusPolls, 0);
        // Device enumeration opens the USB/usbmux stack and takes roughly
        // 250-300ms on a live wired session. Running it on the two-second UI
        // timer produces the periodic preview hitch users perceive while
        // moving the mouse. A live session already owns a stable handle, so
        // poll only its status until the user explicitly refreshes devices.
        if (!forceDeviceEnumeration && _sessions.AnySession)
        {
            await RefreshActiveSessionStatusAsync().ConfigureAwait(true);
            var poll = Interlocked.Increment(ref _activeSessionStatusPolls);
            if (poll % 5 != 0)
            {
                await PollBackgroundSessionErrorsAsync().ConfigureAwait(true);
                return;
            }
            // Every fifth timer tick falls through to the normal inventory
            // path so wireless additions/removals and independent sessions
            // are reconciled without reopening USB on every tick.
        }
        if (forceDeviceEnumeration && Interlocked.Exchange(ref _manualRefreshPending, 1) != 0)
            return;

        var refreshId = Interlocked.Increment(ref _refreshSequence);
        var refreshElapsed = Stopwatch.StartNew();
        var trigger = forceDeviceEnumeration ? "manual" : "timer";
        string[] wifiSyncTargets = [];
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
            var wiredStates = _sessions.Values.Where(state =>
                    !DeviceViewModel.IsWirelessUdid(state.Udid))
                .ToArray();
            var managedUsbTransition = wiredStates.Any(state =>
                state.IsStarting || state.IsStopping);
            CaptureState[] nativeWiredStates = [];
            if (!managedUsbTransition)
            {
                try
                {
                    nativeWiredStates = await Task.Run(() => wiredStates
                        .Where(state => state.Handle != 0)
                        .Select(state => _core.GetDeviceSessionStatus(state.Handle).State)
                        .ToArray());
                }
                catch (Exception error)
                {
                    // A handle can be revoked by an independent preview while
                    // this poll starts. Treat that race as a transition and
                    // retain the last wired inventory for this pass.
                    managedUsbTransition = true;
                    DiagnosticLogger.ExceptionOnce(
                        "device-refresh-transition-status", "devices",
                        "device_refresh_transition_status_failed", error);
                }
            }

            var enumerateWired = UsbDeviceRefreshPolicy.ShouldEnumerateWiredDevices(
                managedUsbTransition, nativeWiredStates);
            // A live wired session already owns a stable native handle. The
            // periodic inventory pass still reconciles wireless devices, but
            // must not reopen usbmux and introduce a visible preview hitch.
            if (!forceDeviceEnumeration && wiredStates.Any(state => state.HasSession))
                enumerateWired = false;
            var refreshWiredMetadata = UsbDeviceRefreshPolicy.ShouldRefreshMetadata(
                forceDeviceEnumeration, wiredStates.Any(state => state.HasSession));
            var wirelessDevices = await Task.Run(_core.GetWirelessDevices);
            if (enumerateWired)
            {
                try
                {
                    if (_sessions.AnySession)
                    {
                        _lastUsbDevices = await Task.Run(() =>
                            _core.GetDevices(refreshWiredMetadata));
                    }
                    else
                    {
                        var result = await Task.Run(() =>
                            (_core.GetEnvironment(),
                                _core.GetDevices(refreshWiredMetadata)));
                        environment = result.Item1;
                        _lastUsbDevices = result.Item2;
                    }
                }
                catch (UsbDeviceRefreshDeferredException error)
                {
                    enumerateWired = false;
                    AddDiagnosticLog(AppLog.Event("device_refresh_deferred",
                        ("id", refreshId), ("trigger", trigger),
                        ("reason", "native_usb_transition"),
                        ("cached_wired", _lastUsbDevices.Count),
                        ("message", error.Message)));
                }
            }
            if (!enumerateWired && forceDeviceEnumeration)
            {
                AddDiagnosticLog(AppLog.Event("device_refresh_deferred",
                    ("id", refreshId), ("trigger", trigger),
                    ("reason", "capture_usb_transition"),
                    ("cached_wired", _lastUsbDevices.Count)));
            }

            if (environment is { } currentEnvironment)
            {
                _lastEnvironment = currentEnvironment;
                UpdateEnvironmentStatus(currentEnvironment);
            }

            if (enumerateWired)
            {
                wifiSyncTargets = _wifiSyncInsertionTracker.Observe(_lastUsbDevices
                    .Where(device => device.UsbConnected != 0)
                    .Select(device => new WiredDeviceTrustState(
                        device.Udid ?? string.Empty,
                        device.DeviceId,
                        device.PairRecordPresent != 0,
                        device.LockdownAccessible != 0)))
                    .ToArray();
            }

            var devices = _lastUsbDevices.Concat(wirelessDevices)
                .Where(device => !string.IsNullOrWhiteSpace(device.Udid))
                .Select(DeviceViewModel.FromNative)
                .GroupBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var known in Devices.Where(device => device.IsWireless).ToArray())
            {
                var present = devices.Any(device => DeviceViewModel.UdidEquals(
                    device.Udid, known.Udid));
                if (present)
                {
                    _wirelessMissingRefreshes.Remove(known.Udid);
                    continue;
                }
                var misses = _wirelessMissingRefreshes.TryGetValue(known.Udid,
                    out var count) ? count + 1 : 1;
                if (misses > WirelessDiscoveryGraceRefreshes)
                {
                    _wirelessMissingRefreshes.Remove(known.Udid);
                    continue;
                }
                _wirelessMissingRefreshes[known.Udid] = misses;
                devices.Add(known);
                AddDiagnosticLog(AppLog.Event("wireless_device_discovery_grace",
                    ("device", AppLog.Device(known.Udid)), ("misses", misses)));
            }
            var wiredDevices = devices.Where(device => !device.IsWireless &&
                !device.IsMediaCast).ToList();
            if (wiredDevices.Count == 0 &&
                _lastEnvironment is { PhysicalAppleUsbDevices: > 0 })
            {
                // QuickTime activation and teardown temporarily remove the
                // device from usbmux. SetupAPI still proves the cable/device is
                // physically present, so keep the known card and expose the
                // degraded Apple-channel state instead of deleting it.
                foreach (var known in Devices.Where(device => !device.IsWireless &&
                             !device.IsMediaCast).ToArray())
                {
                    if (!devices.Any(device => DeviceViewModel.UdidEquals(
                            device.Udid, known.Udid)))
                        devices.Add(known.AsUsbPresentNoMux());
                }
            }
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
        await EnableWifiSyncForDetectedDevicesAsync(wifiSyncTargets);
    }

    private async Task EnableWifiSyncForDetectedDevicesAsync(IEnumerable<string> udids)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        if (!File.Exists(bridgePath)) return;
        foreach (var udid in udids)
        {
            if (_disposed || string.IsNullOrWhiteSpace(udid)) return;
            // Enabling Wi-Fi sync uses the same Lockdown/device plumbing as a
            // wired QuickTime start. Serialize it with capture lifecycle work,
            // and never reconfigure a device that is already starting or live.
            var coreGateHeld = false;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = bridgePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--enable-wifi-sync");
            process.StartInfo.ArgumentList.Add("--udid");
            process.StartInfo.ArgumentList.Add(udid);
            var lockdownGateHeld = false;
            try
            {
                await _coreGate.WaitAsync(_shutdownCancellation.Token);
                coreGateHeld = true;
                if (_disposed || _sessions.Values.Any(state =>
                        string.Equals(state.Udid, udid, StringComparison.OrdinalIgnoreCase) &&
                        (state.IsStarting || state.HasSession)))
                    continue;
                await _lockdownHandshakeGate.WaitAsync(_shutdownCancellation.Token);
                lockdownGateHeld = true;
                if (!process.Start()) continue;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdownCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                var error = await stderr;
                if (process.ExitCode == 0)
                {
                    AddDiagnosticLog(AppLog.Event("wifi_sync_auto_enabled",
                        ("device", AppLog.Device(udid))));
                }
                else
                {
                    AddDiagnosticLog(AppLog.Event("wifi_sync_auto_enable_failed",
                        ("device", AppLog.Device(udid)),
                        ("exit_code", process.ExitCode),
                        ("diagnostic", AppLog.Sanitize(string.IsNullOrWhiteSpace(error)
                            ? output : error))));
                }
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                AddDiagnosticLog(AppLog.Event("wifi_sync_auto_enable_failed",
                    ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                if (lockdownGateHeld) _lockdownHandshakeGate.Release();
                if (coreGateHeld) _coreGate.Release();
            }
        }
    }

    private async Task RefreshActiveSessionStatusAsync()
    {
        if (_disposed || IsMediaCastSelected) return;
        NativeCaptureStatus status;
        try
        {
            status = await Task.Run(GetSelectedCaptureStatus).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("active-session-status-refresh",
                "capture", "active_session_status_refresh_failed", error);
            return;
        }
        ApplyCaptureStatus(status);
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
            InvalidateImageSettingsWindow(pair.Key);
            AddDiagnosticLog(AppLog.Event("wireless_device_removed",
                ("device", AppLog.Device(pair.Key)),
                ("had_session", pair.Value.Handle != 0),
                ("handle", AppLog.Handle(pair.Value.Handle))));
            if (pair.Value.Handle != 0)
            {
                await StopMediaOutputForSessionAsync(pair.Key);
                await _sessions.StopAndDestroyAsync(pair.Value);
            }
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
        var backend = _selectedWirelessReceiverBackend;
        var backendOption = WirelessReceiverConfiguration.GetBackendOption(backend);
        var connectedCount = Devices.Count(device => device.IsWireless);
        var sanitized = WirelessReceiverConfiguration.SanitizeReceiverName(WirelessReceiverName);
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("wireless_settings_begin",
            ("receiver_name_length", sanitized.Length),
            ("backend", backend.ToString()), ("profile", profile.Label),
            ("connected", connectedCount)));
        var changes = new List<string>();
        if (backend != _wireless.Backend)
            changes.Add(LocalizationService.Format("WirelessBackendChangeFormat",
                WirelessReceiverConfiguration.GetBackendOption(_wireless.Backend).Label,
                backendOption.Label));
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
            if (!WirelessReceiverConfiguration.SupportsMediaCast(backend))
                await _mediaCast.StopAsync();
            await _wireless.StopAsync();
            var started = await _wireless.EnsureStartedAsync(sanitized, profile, backend);
            RefreshWirelessStatus();
            if (started.Started)
            {
                PersistWirelessReceiverSettings(backend, profile, sanitized);
                OnPropertyChanged(nameof(WirelessReceiverName));
                OnPropertyChanged(nameof(MediaCastReceiverName));
                OnPropertyChanged(nameof(AppliedWirelessBackendDisplay));
                OnPropertyChanged(nameof(AppliedWirelessProfileDisplay));
                RefreshWirelessStatus();
                RefreshMediaCastStatus();
                AddUiLog(WirelessStatus);
                AddDiagnosticLog(AppLog.Event("wireless_settings_complete",
                    ("success", true), ("backend", backend.ToString()),
                    ("profile", profile.Label),
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

    private void PersistWirelessReceiverSettings(WirelessReceiverBackend backend,
        WirelessDisplayProfile profile, string receiverName)
    {
        if (Application.Current is not App app) return;
        var previousBackend = app.UpdateSettings.WirelessReceiverBackend;
        var previousProfile = app.UpdateSettings.WirelessDisplayProfileId;
        var previousName = app.UpdateSettings.WirelessReceiverName;
        app.UpdateSettings.WirelessReceiverBackend = backend;
        app.UpdateSettings.WirelessDisplayProfileId = profile.Id;
        app.UpdateSettings.WirelessReceiverName = receiverName;
        if (app.SaveUpdateSettings()) return;
        app.UpdateSettings.WirelessReceiverBackend = previousBackend;
        app.UpdateSettings.WirelessDisplayProfileId = previousProfile;
        app.UpdateSettings.WirelessReceiverName = previousName;
        AddDiagnosticLog(AppLog.Event("wireless_backend_save_failed",
            ("backend", backend.ToString()), ("profile", profile.Id)));
        AddUiLog(LocalizationService.Get("WirelessBackendSettingsSaveFailed"));
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
        var hadWiredUsbControlDevice = HasWiredUsbControlDevice;

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
        if (hadWiredUsbControlDevice != HasWiredUsbControlDevice)
        {
            OnPropertyChanged(nameof(UsbControlStatus));
            OnPropertyChanged(nameof(UsbControlActionText));
            OnPropertyChanged(nameof(CanToggleUsbControl));
            OnPropertyChanged(nameof(CanToggleWiredControl));
            OnPropertyChanged(nameof(CanToggleWirelessControl));
            ToggleUsbControlCommand.NotifyCanExecuteChanged();
        }

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

    private static bool IsSessionPresentable(DeviceCaptureState? session) =>
        session is { HasSession: true, IsStopping: false };

    private bool IsSessionLifecycleOperationInProgress(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return false;
        lock (_sessionLifecycleGate) return _sessionLifecycleDevices.Contains(udid);
    }

    private bool HasSessionLifecycleOperationInProgress
    {
        get { lock (_sessionLifecycleGate) return _sessionLifecycleDevices.Count != 0; }
    }

    private bool CanQueueSessionLifecycleOperation(DeviceViewModel? device) =>
        device is not null && !IsSessionLifecycleOperationInProgress(device.Udid) &&
        (!IsBusy || HasSessionLifecycleOperationInProgress);

    // Stopping a live mirror must stay available while unrelated work is
    // refreshing devices or applying settings. Starting remains serialized by
    // CanQueueSessionLifecycleOperation, but a real stop only conflicts with
    // teardown of that same session.
    private bool CanStopCurrentCapture() => CurrentDeviceSession is { HasSession: true,
        IsStopping: false } state && !IsSessionLifecycleOperationInProgress(state.Udid);

    private bool TryBeginSessionLifecycleOperation(string udid)
    {
        bool added;
        lock (_sessionLifecycleGate) added = _sessionLifecycleDevices.Add(udid);
        if (added)
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
        return added;
    }

    private void EndSessionLifecycleOperation(string udid)
    {
        bool removed;
        lock (_sessionLifecycleGate) removed = _sessionLifecycleDevices.Remove(udid);
        if (removed)
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetSelectedDevice(DeviceViewModel? value, bool updateDriverStatus)
    {
        // Collection notifications can cause a two-way ListBox binding to
        // offer null even though the selected stable item is still present.
        // It is not a user selection and must not supersede the real UDID.
        if (value is null && _selectedDevice is not null && Devices.Contains(_selectedDevice)) return;
        if (ReferenceEquals(_selectedDevice, value)) return;
        var previous = _selectedDevice;
        _selectedDevice = value;
        OnPropertyChanged(nameof(SelectedDevice));
        if (value?.IsMediaCast == true)
        {
            OnPropertyChanged(nameof(IsVideoProtected));
            ApplyMediaCastStatistics();
            AddDiagnosticLog(AppLog.Event("source_selected",
                ("from", AppLog.Device(previous?.Udid)),
                ("to", AppLog.Device(value.Udid)),
                ("kind", "media_cast"), ("session", AppLog.Handle(0)),
                ("driver_refresh", updateDriverStatus)));
            NotifySelectedDeviceProperties();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanStartBluetoothControl));
            OnPropertyChanged(nameof(CanStopBluetoothControl));
            OnPropertyChanged(nameof(CanToggleBluetoothControl));
            StartBluetoothControlCommand.NotifyCanExecuteChanged();
            StopBluetoothControlCommand.NotifyCanExecuteChanged();
            ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
            ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
            MoreImageSettingsCommand.NotifyCanExecuteChanged();
            return;
        }
        var session = CurrentDeviceSession;
        var presentableSession = IsSessionPresentable(session);
        _activeCaptureUdid = presentableSession ? value?.Udid : null;
        IsCapturing = presentableSession;
        NotifyCaptureSessionChanged();
        NativeCore.SelectPreviewSession(presentableSession ? session!.Handle : 0);
        RestoreSelectedVideoControls(session);
        // Selection restores controls only. These values already belong to
        // this session; invoking their public setters would resend native
        // audio commands while another core operation may be in progress.
        _playbackVolume = session?.Volume ?? 100;
        _playAudio = session?.PlayAudio ?? true;
        if (value is { IsWireless: false, IsMediaCast: false })
            RestoreSelectedSettingsStatus(session);
        CaptureStatus = presentableSession
            ? LocalizationService.Get("CaptureStreaming")
            : session?.IsStopping == true
                ? LocalizationService.Get("CaptureCleaningDevice")
                : value?.StatusDisplay ?? LocalizationService.Get("StatusWaitingDevice");
        if (presentableSession && session is not null &&
            _lastCaptureStatus is { } cached &&
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
        OnPropertyChanged(nameof(CanStartBluetoothControl));
        OnPropertyChanged(nameof(CanStopBluetoothControl));
        OnPropertyChanged(nameof(CanToggleBluetoothControl));
        OnPropertyChanged(nameof(CanStartUsbControl));
        OnPropertyChanged(nameof(CanStartWirelessControl));
        OnPropertyChanged(nameof(CanToggleWiredControl));
        OnPropertyChanged(nameof(CanToggleWirelessControl));
        StartBluetoothControlCommand.NotifyCanExecuteChanged();
        StopBluetoothControlCommand.NotifyCanExecuteChanged();
        ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(IsVideoProtected));
        OnPropertyChanged(nameof(PreviewAndObsVisibility));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
        OnPropertyChanged(nameof(WiredVideoLimitSettingsVisibility));
        OnPropertyChanged(nameof(VideoSettingsVisibility));
        OnPropertyChanged(nameof(WirelessActualVideoSettingsVisibility));
        OnPropertyChanged(nameof(WirelessTopSettingsVisibility));
        OnPropertyChanged(nameof(WirelessBottomSettingsVisibility));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        OnPropertyChanged(nameof(SelectedUsbProjectionMode));
        OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
        OnPropertyChanged(nameof(SelectedDecoderPreference));
        OnPropertyChanged(nameof(CanChangeVideoPipeline));
        OnPropertyChanged(nameof(CanChangeDecoderPipeline));
        OnPropertyChanged(nameof(AdvancedSettingsVisibility));
        OnPropertyChanged(nameof(PlaybackVolume));
        OnPropertyChanged(nameof(PlayAudio));
        OnPropertyChanged(nameof(CanUseVisualPreviewTools));
        NotifyMediaOutputStateChanged();
        MediaOutputSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleUsbControl));
        ToggleUsbControlCommand?.NotifyCanExecuteChanged();
    }

    private void NotifyCaptureSessionChanged()
    {
        if (!_disposed && _bluetoothControlEnabled && !HasBluetoothControlTargetSession)
            _ = StopBluetoothControlAsync();
        OnPropertyChanged(nameof(CurrentSessionHandle));
        OnPropertyChanged(nameof(HasCaptureSession));
        OnPropertyChanged(nameof(PreviewAndObsVisibility));
        OnPropertyChanged(nameof(CanUseVisualPreviewTools));
        OnPropertyChanged(nameof(UsbProjectionSettingsVisibility));
        OnPropertyChanged(nameof(CanChangeVideoPipeline));
        OnPropertyChanged(nameof(CanChangeDecoderPipeline));
        NotifyMediaOutputStateChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartBluetoothControl));
        OnPropertyChanged(nameof(CanStopBluetoothControl));
        OnPropertyChanged(nameof(CanToggleBluetoothControl));
        StartBluetoothControlCommand.NotifyCanExecuteChanged();
        StopBluetoothControlCommand.NotifyCanExecuteChanged();
        ToggleBluetoothControlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleUsbControl));
        ToggleUsbControlCommand?.NotifyCanExecuteChanged();
        MediaOutputSettingsCommand.NotifyCanExecuteChanged();
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
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce(
                    $"background-session-status-{state.Handle:x}", "capture",
                    "background_session_status_failed", error,
                    ("device", AppLog.Device(state.Udid)),
                    ("handle", AppLog.Handle(state.Handle)));
                continue;
            }
            if (status.Width != 0 && status.Height != 0)
                DeviceVideoSizeChanged?.Invoke(state.Udid, status.Width, status.Height);
            UpdateProtectionState(state, ProtectedContentStatus.Parse(
                status.Message, status.AudioSampleRate, status.AudioChannels));
            if (status.State != CaptureState.Error || state.ErrorShown) continue;
            state.ErrorShown = true;
            var name = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, state.Udid))?.DisplayName ?? state.Udid;
            var sessionClosedWarning =
                CaptureErrorGuidance.IsDeviceSessionClosedWarning(status);
            var errorTitle = LocalizationService.Format(
                sessionClosedWarning
                    ? "DeviceSessionClosedWarningTitleFormat"
                    : "DeviceCaptureErrorTitleFormat",
                name);
            var errorBody = CaptureErrorGuidance.UserMessage(status);
            if (sessionClosedWarning)
            {
                ShowDeviceSessionClosedWarningThenRelease(
                    state, status, errorTitle, errorBody);
            }
            else
            {
                await ReleaseFailedSessionLockedAsync(state, status);
                CaptureStatusNoticeWindow.ShowError(errorTitle, errorBody);
            }
        }
    }

    private async Task ReleaseFailedSessionLockedAsync(DeviceCaptureState state,
        NativeCaptureStatus status)
    {
        var failedHandle = state.Handle;
        if (failedHandle == 0) return;
        AddDiagnosticLog(AppLog.Event("capture_error_release_begin",
            ("device", AppLog.Device(state.Udid)),
            ("handle", AppLog.Handle(failedHandle)),
            ("failure_kind", status.FailureKind),
            ("failure_stage", status.FailureStage),
            ("error_code", status.ErrorCode)));
        try
        {
            // StopAndDestroyAsync revokes the handle synchronously before its
            // first yield. This hides every preview immediately and guarantees
            // native teardown even if recorder or virtual-camera shutdown
            // reports a separate failure.
            var teardown = _sessions.StopAndDestroyAsync(state);
            try { await StopMediaOutputForSessionAsync(state.Udid); }
            finally { await teardown; }
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("capture", "capture_error_release_failed",
                error, ("device", AppLog.Device(state.Udid)),
                ("handle", AppLog.Handle(failedHandle)));
        }
        finally
        {
            NotifyCaptureSessionChanged();
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, state.Udid))
                ClearSelectedSessionState(state.Udid);
        }
    }

    private void ShowDeviceSessionClosedWarningThenRelease(
        DeviceCaptureState state, NativeCaptureStatus status,
        string errorTitle, string errorBody)
    {
        CaptureStatusNoticeWindow.ShowStoppedThen(errorTitle, errorBody,
            () => ReleaseFailedSessionLockedAsync(state, status));
    }

    private void ResetPreviewState()
    {
        SetAudioOnlyAirPlay(false);
        OnPropertyChanged(nameof(IsVideoProtected));
        OnPropertyChanged(nameof(CanUseVisualPreviewTools));
        ProtectedAudioDisplay = CurrentDeviceSession is { VideoProtected: true } state
            ? new ProtectedContentPresentation(true, state.ProtectedAudioActive,
                state.ProtectedAudioSampleRate, state.ProtectedAudioChannels).AudioDisplay
            : LocalizationService.Get("StatusWaiting");
        SetDecoderStatus(string.Empty, "Hidden");
        _lastVideoOutputSignature = null;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        OnPropertyChanged(nameof(SourceVideoWidth));
        OnPropertyChanged(nameof(SourceVideoHeight));
        OnPropertyChanged(nameof(BluetoothDeviceOrientationDisplay));
        Resolution = "—";
        FpsDisplay = "— fps";
        LatencyDisplay = "— ms";
        AudioDisplay = LocalizationService.Get("StatusWaiting");
    }

    private async Task StartAsync()
    {
        if (_disposed || SelectedDevice is null || HasCaptureSession) return;
        var requestedDevice = SelectedDevice;
        var requestedState = GetOrCreateDeviceState(requestedDevice);
        var queuedBehindAnotherOperation = IsBusy || HasSessionLifecycleOperationInProgress;
        if (!CanQueueSessionLifecycleOperation(requestedDevice) ||
            !TryBeginSessionLifecycleOperation(requestedDevice.Udid)) return;
        if (requestedState.IsStarting || requestedState.IsStopping)
        {
            EndSessionLifecycleOperation(requestedDevice.Udid);
            return;
        }

        // A queued request still owns this device's lifecycle. Mark it before
        // waiting for the process-wide USB lock so the request is visible to
        // the user and device refresh never treats the transition as idle.
        requestedState.IsStarting = true;
        var startMarked = true;
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedDevice.Udid))
            CaptureStatus = LocalizationService.Get(queuedBehindAnotherOperation
                ? "CaptureQueued" : "StartRequested");
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("capture_start_begin",
            ("device", AppLog.Device(requestedDevice.Udid)),
            ("kind", requestedDevice.IsWireless ? "wireless" : "wired"),
            ("resolution", $"{SelectedResolutionPreset.Width}x{SelectedResolutionPreset.Height}"),
            ("render_fps_limit", SelectedFrameRate), ("audio", PlayAudio),
            ("decoder", requestedState.DecoderPreference),
            ("brightness", requestedState.Brightness),
            ("contrast", requestedState.Contrast),
            ("saturation", requestedState.Saturation),
            ("gamma", requestedState.Gamma),
            ("usb_mode", requestedState.UsbProjectionMode),
            ("queued", queuedBehindAnotherOperation)));
        var ownsBusyState = !IsBusy;
        if (ownsBusyState) IsBusy = true;
        var gateHeld = false;
        try
        {
            // A user click that lands during the short background poll should
            // run immediately after it, rather than being silently discarded.
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (!ownsBusyState && !IsBusy)
            {
                IsBusy = true;
                ownsBusyState = true;
            }
            if (_disposed) return;
            // This request belongs to the device selected when the button was
            // clicked. Do not silently abandon it if the user changes tabs
            // while it waits behind another device's USB teardown.
            var device = requestedDevice;
            if (requestedState.Handle != 0)
            {
                AddDiagnosticLog(AppLog.Event("capture_start_reused",
                    ("device", AppLog.Device(device.Udid)),
                    ("handle", AppLog.Handle(requestedState.Handle)),
                    ("elapsed_ms", operation.ElapsedMilliseconds)));
                IsCapturing = true;
                _activeCaptureUdid = device.Udid;
                NotifyCaptureSessionChanged();
                NativeCore.SelectPreviewSession(requestedState.Handle);
                OnPropertyChanged(nameof(CurrentSessionHandle));
                CaptureStatus = LocalizationService.Get("StartRequested");
                return;
            }
            // Keep readiness checks, teardown and native session creation on
            // one per-process path so independent windows cannot race the
            // selected device into a duplicate wired start.
            var preflight = await EnsureSourceReadyAsync(device);
            AddDiagnosticLog(AppLog.Event("capture_start_preflight",
                ("device", AppLog.Device(device.Udid)),
                ("success", preflight.Success),
                ("failure_kind", preflight.FailureKind),
                ("failure_stage", CaptureFailureStage.UsbPreflight),
                ("error_code", preflight.ErrorCode),
                ("message", preflight.Message)));
            if (!preflight.Success)
            {
                AddUiLog(LocalizationService.Format(
                    "StartFailedFormat", preflight.Message));
                if (preflight.ErrorCode != 0)
                    CaptureStatusNoticeWindow.ShowError(
                        CaptureErrorGuidance.IsUsbConfigurationFailure(preflight.Message)
                            ? LocalizationService.Get("CaptureUsbConfigurationTitle")
                            : LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                                device.DisplayName),
                        CaptureErrorGuidance.StartFailureMessage(
                            preflight.ErrorCode, preflight.Message,
                            preflight.FailureKind),
                        CaptureErrorGuidance.IsUsbConfigurationFailure(preflight.Message));
                return;
            }
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
                ("error_code", created.ErrorCode),
                ("message", created.Message)));
            // Handle is not observable itself; explicitly refresh the style
            // trigger and command availability as soon as creation finishes.
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
            if (!result.Success)
                CaptureStatusNoticeWindow.ShowError(
                    LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                        device.DisplayName),
                    CaptureErrorGuidance.StartFailureMessage(
                        created.ErrorCode, created.Message));
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
            CaptureStatusNoticeWindow.ShowError(
                LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                    requestedDevice?.DisplayName ??
                    LocalizationService.Get("CaptureError")),
                CaptureErrorGuidance.StartFailureMessage(
                    (int)NativeResult.CaptureBackendUnavailable, error.Message));
        }
        finally
        {
            if (startMarked) requestedState.IsStarting = false;
            if (ownsBusyState) IsBusy = false;
            if (gateHeld) _coreGate.Release();
            EndSessionLifecycleOperation(requestedDevice.Udid);
        }
    }

    private async Task ApplyVideoSettingsAsync()
    {
        if (_disposed) return;
        if (IsSettingsInteractionBlocked || !await _settingsGate.WaitAsync(0))
        {
            SetSettingsStatus("ImageAdjustmentsBusy");
            return;
        }
        try { await ApplyVideoSettingsCoreAsync(); }
        finally { _settingsGate.Release(); }
    }

    private async Task ApplyVideoSettingsCoreAsync()
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

            var pipeline = _core.SetDevicePipelinePreferences(requestedHandle,
                (uint)requestedDecoder, 1U);
            var render = (Success: true, Message: string.Empty);
            if (!requestedDevice.IsWireless)
            {
                render = _core.SetDeviceVideoPreferences(requestedHandle,
                    requestedPreset.Width, requestedPreset.Height,
                    (uint)requestedFrameRate);
                if (render.Success)
                {
                    requestedState.MarkRenderSettingsApplied(
                        requestedPreset.Width, requestedPreset.Height,
                        requestedFrameRate);
                }
            }
            var success = pipeline.Success && render.Success;
            var targetStillSelected = DeviceViewModel.UdidEquals(
                SelectedDevice?.Udid, requestedUdid);
            failureMessage = string.Join("; ", new[]
            {
                pipeline.Success ? string.Empty : pipeline.Message,
                render.Success ? string.Empty : render.Message,
            }.Where(message => !string.IsNullOrWhiteSpace(message)));
            AddDiagnosticLog(AppLog.Event("video_settings_result",
                ("device", AppLog.Device(requestedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("success", success),
                ("pipeline_success", pipeline.Success),
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
                    if (requestedDevice is { IsWireless: false })
                    {
                        SetSettingsStatus("VideoSettingsAppliedFormat", requestedPreset,
                            requestedFrameRate, DecoderPreferenceLabel(requestedDecoder));
                    }
                    else
                    {
                        SetSettingsStatus("DecoderPreferenceSubmittedFormat",
                            DecoderPreferenceLabel(requestedDecoder));
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
        if (_disposed || !HasCaptureSession) return;
        var requestedState = CurrentDeviceSession;
        var requestedHandle = requestedState?.Handle ?? 0;
        if (requestedState is null || requestedHandle == 0) return;
        if (!CanStopCurrentCapture() ||
            !TryBeginSessionLifecycleOperation(requestedState.Udid)) return;
        var operation = Stopwatch.StartNew();
        AddDiagnosticLog(AppLog.Event("capture_stop_begin",
            ("device", AppLog.Device(requestedState.Udid)),
            ("handle", AppLog.Handle(requestedHandle)),
            ("wireless", DeviceViewModel.IsWirelessUdid(requestedState.Udid))));
        var ownsBusyState = !IsBusy;
        if (ownsBusyState) IsBusy = true;
        var gateHeld = false;
        DeviceCaptureState? stoppedState = null;
        // Hide the native HwndHost before USB teardown starts. Native stop can
        // wait on QuickTime and configuration restore; keeping its last frame
        // visible during that interval falsely implies that mirroring is still
        // active and allows a stale preview to be presented after tab changes.
        requestedState.IsStopping = true;
        _activeCaptureUdid = null;
        IsCapturing = false;
        NativeCore.SelectPreviewSession(0);
        NotifyCaptureSessionChanged();
        CaptureStatus = LocalizationService.Get("CaptureCleaningDevice");
        ResetPreviewState();
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (!ownsBusyState && !IsBusy)
            {
                IsBusy = true;
                ownsBusyState = true;
            }
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
            await StopMediaOutputForSessionAsync(stoppedState.Udid);
            UsbConfigurationRestoreWarningException? restoreWarning = null;
            try
            {
                await _sessions.StopAndDestroyAsync(stoppedState);
            }
            catch (UsbConfigurationRestoreWarningException warning)
            {
                restoreWarning = warning;
            }
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
            AddUiLog(restoreWarning is null
                ? LocalizationService.Get("StopSessionReleased")
                : LocalizationService.Format("StopUsbRestoreWarningFormat",
                    AppLog.Message(restoreWarning.Message)));
            AddDiagnosticLog(AppLog.Event("capture_stop_complete",
                ("device", AppLog.Device(stoppedUdid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("success", true),
                ("usb_restore_confirmed", restoreWarning is null),
                ("warning_code", restoreWarning?.ErrorCode ?? 0),
                ("warning", restoreWarning is null
                    ? string.Empty : AppLog.Message(restoreWarning.Message))));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("capture_stop_failed",
                ("device", AppLog.Device(requestedState.Udid)),
                ("handle", AppLog.Handle(requestedHandle)),
                ("elapsed_ms", operation.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            // StopMediaOutput can fail before DeviceSessionManager takes
            // ownership of teardown. In that case the native session is still
            // usable, so restore only its presentation state instead of
            // leaving it permanently marked as "cleaning".
            if (requestedState.Handle == requestedHandle)
            {
                requestedState.IsStopping = false;
                if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, requestedState.Udid))
                {
                    _activeCaptureUdid = requestedState.Udid;
                    IsCapturing = true;
                    NativeCore.SelectPreviewSession(requestedHandle);
                    CaptureStatus = LocalizationService.Get("CaptureStreaming");
                }
            }
            NotifyCaptureSessionChanged();
            var failure = LocalizationService.Format("StopFailedFormat", error.Message);
            if (stoppedState is not null && stoppedState.Handle == 0 &&
                DeviceViewModel.UdidEquals(SelectedDevice?.Udid, stoppedState.Udid))
            {
                ClearSelectedSessionState(stoppedState.Udid);
                CaptureStatus = failure;
            }
            AddUiLog(failure);
            CaptureStatusNoticeWindow.ShowError(
                LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                    SelectedDevice?.DisplayName ??
                    LocalizationService.Get("CaptureError")),
                CaptureErrorGuidance.UserMessage(CaptureFailureKind.UsbConnection,
                    CaptureFailureStage.SessionTeardown,
                    (int)NativeResult.SessionTeardownFailed, error.Message));
        }
        finally
        {
            if (ownsBusyState) IsBusy = false;
            if (gateHeld) _coreGate.Release();
            EndSessionLifecycleOperation(requestedState.Udid);
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
            OnPropertyChanged(nameof(PreviewAndObsVisibility));
            OnPropertyChanged(nameof(CanUseVisualPreviewTools));
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
        NotifyMediaOutputStateChanged();
    }

    internal void SetMediaCastOutputProviders(
        Func<uint, uint, Nv12VideoFrame?>? nv12FrameProvider,
        Func<uint, uint, VideoFrame?>? videoFrameProvider,
        Func<ulong, AudioPacket?>? audioPacketProvider)
    {
        _mediaCastNv12FrameProvider = nv12FrameProvider;
        _mediaCastVideoFrameProvider = videoFrameProvider;
        _mediaCastAudioPacketProvider = audioPacketProvider;
        NotifyMediaOutputStateChanged();
    }

    private Nv12VideoFrame? GetOutputNv12Frame(ulong handle, uint width,
        uint height) => handle == MediaCastOutputHandle
            ? _mediaCastNv12FrameProvider?.Invoke(width, height)
            : _core.GetDeviceOutputNv12Frame(handle, width, height);

    private VideoFrame? GetOutputVideoFrame(ulong handle, uint width,
        uint height) => handle == MediaCastOutputHandle
            ? _mediaCastVideoFrameProvider?.Invoke(width, height)
            : _core.GetDeviceOutputFrame(handle, width, height);

    private AudioPacket? GetOutputAudioPacket(ulong handle, ulong afterSequence) =>
        handle == MediaCastOutputHandle ? _mediaCastAudioPacketProvider?.Invoke(afterSequence) :
            _core.GetDeviceOutputAudioPacket(handle, afterSequence);

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
        if (IsMediaOutputRunning &&
            DeviceViewModel.UdidEquals(_mediaOutputUdid,
                DeviceViewModel.MediaCastUdid))
            _ = StopMediaOutputForSessionAsync(DeviceViewModel.MediaCastUdid);
        AddDiagnosticLog(AppLog.Event("media_cast_end",
            ("selection", AppLog.Device(SelectedDevice?.Udid)),
            ("size", $"{_mediaCastWidth}x{_mediaCastHeight}"),
            ("audio", _mediaCastAudioEnabled)));
        _isMediaCasting = false;
        OnPropertyChanged(nameof(IsMediaCasting));
        OnPropertyChanged(nameof(PreviewAndObsVisibility));
        OnPropertyChanged(nameof(CanUseVisualPreviewTools));
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
        NotifyMediaOutputStateChanged();
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
            OnPropertyChanged(nameof(BluetoothDeviceOrientationDisplay));
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
        DiagnosticLogger.Info("ui", "action", ("message", safeMessage));
        try { _ = _core.WriteLog($"action {safeMessage}"); }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("native-ui-log", "logging",
                "native_ui_write_failed", error);
        }
        AddLogLine($"{DateTime.Now:HH:mm:ss.fff} [UI] {safeMessage}");
        PublishLogText();
    }

    internal void AddDiagnosticLog(string message)
    {
        var safeMessage = AppLog.Message(message);
        if (!string.IsNullOrWhiteSpace(safeMessage))
        {
            DiagnosticLogger.Info("application", "diagnostic",
                ("message", safeMessage));
            try { _ = _core.WriteLog($"diagnostic {safeMessage}"); }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("native-diagnostic-log", "logging",
                    "native_diagnostic_write_failed", error);
            }
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
                    ("failure_kind", preflight.FailureKind),
                    ("failure_stage", CaptureFailureStage.UsbPreflight),
                    ("error_code", preflight.ErrorCode),
                    ("message", preflight.Message)));
                var message = preflight.ErrorCode == 0
                    ? preflight.Message
                    : CaptureErrorGuidance.StartFailureMessage(
                        preflight.ErrorCode, preflight.Message,
                        preflight.FailureKind);
                return (false, 0, false, message);
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
            if (state.IsStarting || state.IsStopping)
                return (false, 0, false, LocalizationService.Get("StatusWaiting"));
            state.IsStarting = true;
            NativeSessionCreateResult result;
            try
            {
                result = await Task.Run(() => CreateSession(device, startSettings));
            }
            finally
            {
                state.IsStarting = false;
            }
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
                ("error_code", result.ErrorCode),
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
            await StopMediaOutputForSessionAsync(state.Udid);
            await _sessions.StopAndDestroyAsync(state);
            AddDiagnosticLog(AppLog.Event("independent_session_stop_complete",
                ("device", AppLog.Device(udid)),
                ("elapsed_ms", operation.ElapsedMilliseconds), ("success", true)));
        }
        catch (UsbConfigurationRestoreWarningException warning)
        {
            AddDiagnosticLog(AppLog.Event("independent_session_stop_complete",
                ("device", AppLog.Device(udid)),
                ("elapsed_ms", operation.ElapsedMilliseconds), ("success", true),
                ("usb_restore_confirmed", false),
                ("warning_code", warning.ErrorCode),
                ("warning", AppLog.Message(warning.Message))));
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
                ("failure_kind", status.FailureKind),
                ("failure_stage", status.FailureStage),
                ("error_code", status.ErrorCode),
                ("message", status.Message)));
        }
        var audioOnlyAirPlay = IsWirelessSelected && status.AudioSampleRate > 0 &&
            status.Width == 0 && status.Height == 0;
        SetAudioOnlyAirPlay(audioOnlyAirPlay);
        var protection = ProtectedContentStatus.Parse(status.Message,
            status.AudioSampleRate, status.AudioChannels);
        var protectedVideo = !audioOnlyAirPlay && protection.IsProtected;
        if (CurrentDeviceSession is { } currentState)
        {
            protection = protection with { IsProtected = protectedVideo };
            UpdateProtectionState(currentState, protection);
        }
        var captureActive = IsActiveCaptureState(status.State);
        IsCapturing = captureActive;
        if (!captureActive && status.State is CaptureState.Idle or CaptureState.Stopped or CaptureState.Error)
            _activeCaptureUdid = null;
        if (status.State is not CaptureState.Idle || SelectedDevice is null)
            CaptureStatus = GetCaptureStatusText(status, IsWirelessSelected);
        if (status.State == CaptureState.Error &&
            CurrentDeviceSession is { ErrorShown: false } failedSession)
        {
            failedSession.ErrorShown = true;
            var sessionClosedWarning =
                CaptureErrorGuidance.IsDeviceSessionClosedWarning(status);
            var errorTitle = LocalizationService.Format(
                sessionClosedWarning
                    ? "DeviceSessionClosedWarningTitleFormat"
                    : "DeviceCaptureErrorTitleFormat",
                SelectedDevice?.DisplayName ?? LocalizationService.Get("CaptureError"));
            var errorBody = CaptureErrorGuidance.UserMessage(status);
            if (sessionClosedWarning)
            {
                ShowDeviceSessionClosedWarningThenRelease(
                    failedSession, status, errorTitle, errorBody);
            }
            else
            {
                _ = ReleaseSelectedFailedSessionAsync(
                    failedSession, status, errorTitle, errorBody);
            }
        }
        Resolution = audioOnlyAirPlay
            ? LocalizationService.Get("WirelessMusicAudioOnly")
            : status.Width > 0 && status.Height > 0 ? $"{status.Width}×{status.Height}" : "—";
        if (status.Width > 0 && status.Height > 0 &&
            (status.Width != _sourceVideoWidth || status.Height != _sourceVideoHeight))
        {
            _sourceVideoWidth = status.Width;
            _sourceVideoHeight = status.Height;
            OnPropertyChanged(nameof(SourceVideoWidth));
            OnPropertyChanged(nameof(SourceVideoHeight));
            OnPropertyChanged(nameof(BluetoothDeviceOrientationDisplay));
        }
        if (status.Width != 0 && status.Height != 0 && SelectedDevice is { } selected)
            DeviceVideoSizeChanged?.Invoke(selected.Udid, status.Width, status.Height);
        FpsDisplay = audioOnlyAirPlay
            ? LocalizationService.Get("WirelessMusicNoVideo")
            : status.Fps > 0 ? $"{status.Fps:F1} fps" : "— fps";
        LatencyDisplay = status.LatencyMs > 0 ? $"{status.LatencyMs:F1} ms" : "— ms";
        AudioDisplay = status.AudioSampleRate > 0
            ? $"{status.AudioSampleRate / 1000.0:F0} kHz · {status.AudioChannels} ch"
            : LocalizationService.Get("StatusWaiting");
        ProtectedAudioDisplay = protection.AudioDisplay;
        EvaluateWirelessStall(status);
    }

    private void EvaluateWirelessStall(NativeCaptureStatus status)
    {
        var state = CurrentDeviceSession;
        var handle = CurrentSessionHandle;
        if (!IsWirelessSelected || state is null || handle == 0 ||
            status.State != CaptureState.Streaming)
        {
            _wirelessStallRecovery.Reset();
            return;
        }

        var timestamp = _core.GetDeviceSessionLatestFrameTimestamp(handle);
        var action = _wirelessStallRecovery.Observe(handle, status, timestamp,
            DateTimeOffset.UtcNow);
        if (action == WirelessStallRecoveryAction.None) return;

        AddDiagnosticLog(AppLog.Event("wireless_stall_detected",
            ("device", AppLog.Device(state.Udid)),
            ("handle", AppLog.Handle(handle)),
            ("action", action),
            ("size", $"{status.Width}x{status.Height}"),
            ("fps", status.Fps.ToString("F2")),
            ("latency_ms", status.LatencyMs.ToString("F1")),
            ("video_frames", status.VideoFrames),
            ("latest_timestamp", timestamp),
            ("attempt", _wirelessStallRecovery.RecoveryAttempts)));

        if (action == WirelessStallRecoveryAction.RefreshPreview)
        {
            var refreshed = NativeCore.ForceDevicePreviewRefresh(handle);
            AddDiagnosticLog(AppLog.Event("wireless_orientation_recovery_attempt",
                ("device", AppLog.Device(state.Udid)),
                ("handle", AppLog.Handle(handle)),
                ("operation", "preview_refresh"), ("success", refreshed)));
            return;
        }

        lock (_wirelessRecoveryInFlight)
        {
            if (!_wirelessRecoveryInFlight.Add(handle)) return;
        }
        _ = RecoverWirelessSessionAsync(state.Udid, handle);
    }

    private async Task RecoverWirelessSessionAsync(string udid, ulong expectedHandle)
    {
        try
        {
            AddDiagnosticLog(AppLog.Event("wireless_orientation_recovery_attempt",
                ("device", AppLog.Device(udid)),
                ("handle", AppLog.Handle(expectedHandle)),
                ("operation", "session_restart")));
            var device = Devices.FirstOrDefault(candidate =>
                DeviceViewModel.UdidEquals(candidate.Udid, udid));
            if (device is null || !_sessions.TryGet(udid, out var state) ||
                state.Handle != expectedHandle || !device.IsWireless)
                return;

            await StopDeviceSessionAsync(udid, expectedHandle);
            if (_disposed) return;
            var result = await StartBackgroundSessionAsync(device);
            AddDiagnosticLog(AppLog.Event("wireless_orientation_recovery_result",
                ("device", AppLog.Device(udid)),
                ("old_handle", AppLog.Handle(expectedHandle)),
                ("new_handle", AppLog.Handle(result.Handle)),
                ("success", result.Success),
                ("message", result.Message)));
            if (!result.Success)
                AddUiLog(AppLog.Event("wireless recovery failed",
                    ("device", AppLog.Device(udid)),
                    ("message", result.Message)));
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("wireless_orientation_recovery_result",
                ("device", AppLog.Device(udid)),
                ("old_handle", AppLog.Handle(expectedHandle)),
                ("success", false), ("error", AppLog.Error(error))));
        }
        finally
        {
            lock (_wirelessRecoveryInFlight) _wirelessRecoveryInFlight.Remove(expectedHandle);
        }
    }

    private async Task ReleaseSelectedFailedSessionAsync(
        DeviceCaptureState state, NativeCaptureStatus status,
        string errorTitle, string errorBody)
    {
        await _coreGate.WaitAsync();
        try
        {
            if (state.Handle != 0)
                await ReleaseFailedSessionLockedAsync(state, status);
        }
        finally { _coreGate.Release(); }

        // A modal prompt must never own the lifetime of a failed USB session.
        // Stop and destroy first so an unattended error dialog cannot retain
        // device handles or delay Windows shutdown.
        CaptureStatusNoticeWindow.ShowError(errorTitle, errorBody);
    }


    private void UpdateVideoOutputStatus()
    {
        var handle = CurrentSessionHandle;
        if (handle == 0 || SelectedDevice is null || SelectedDevice.IsMediaCast)
        {
            SetDecoderStatus(string.Empty, "Hidden");
            _lastVideoOutputSignature = null;
            return;
        }
        if (!_core.TryGetDeviceVideoOutputStatus(handle, out var status))
        {
            SetDecoderStatus(LocalizationService.Get("DecoderStatusDetecting"),
                "Detecting");
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
            DecoderRuntimeMode.Hardware or DecoderRuntimeMode.Software or
            DecoderRuntimeMode.External
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
        var tone = decoderState switch
        {
            DecoderSwitchState.Applied when runtimeMode != DecoderRuntimeMode.Unknown =>
                "Applied",
            DecoderSwitchState.Failed => "Failed",
            _ => "Pending",
        };
        SetDecoderStatus(decoderStatus, tone);
    }

    private void SetDecoderStatus(string text, string tone)
    {
        DecoderStatus = text;
        DecoderStatusTone = tone;
    }

    private void UpdateEnvironmentStatus(NativeEnvironmentInfo environment)
    {
        if (environment.CaptureMuxAvailable != 0)
        {
            EnvironmentStatus = LocalizationService.Get("EnvironmentReadyCapture");
            DriverState = LocalizationService.Get("DriverCaptureReady");
        }
        else if (environment.UsbDkBackendKnown != 0 &&
                 environment.UsbDkBackendAvailable != 0)
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
        if (SelectedDevice is not { IsMediaCast: false } selected) return;

        // Status refreshes run automatically at startup and every two seconds.
        // Keep them strictly in registry/SCM/SetupAPI territory: even a
        // read-only libusb0 enumeration enters the third-party kernel filter
        // and can bugcheck a machine with an incompatible driver stack. The
        // exact serial probe remains part of the explicit Start action below.
        _filterDriverStatus = _filterDriver.Inspect(selected.Udid);
        if (_lastEnvironment is { } environment)
            UpdateEnvironmentStatus(environment);
        else
            ApplySelectedDriverState();
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
            IPhoneFilterDriverState.UnsafeStack => LocalizationService.Get("DriverUnsafeAppleStack"),
            IPhoneFilterDriverState.Error => LocalizationService.Get("DriverFilterStateError"),
            _ => DriverState,
        };
    }

    private async Task<(bool Success, int ErrorCode,
        CaptureFailureKind FailureKind, string Message)> EnsureSourceReadyAsync(
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
                return (false, 0, CaptureFailureKind.Unknown, message);
            }

            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
                CaptureStatus = WirelessStatus;
            ApplySelectedDriverState();
            return (true, 0, CaptureFailureKind.None, WirelessStatus);
        }

        // Wireless devices return above and never enter the USB driver path.
        // Keep the UI preflight in registry/SCM territory. The native capture
        // start performs the one authoritative exact-device open after this
        // check; doing it here as well would enumerate the legacy filter twice
        // and could touch an already-streaming device in a multi-device setup.
        var driverStatus = await Task.Run(() => _filterDriver.Inspect(device.Udid));
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
        {
            _filterDriverStatus = driverStatus;
            ApplySelectedDriverState();
        }
        if (driverStatus.CanStartCapture)
        {
            if (driverStatus.State == IPhoneFilterDriverState.UnsafeStack)
                AddUiLog($"driver safety warning: {driverStatus.Diagnostic}");
            return (true, 0, CaptureFailureKind.None, string.Empty);
        }
        var failure = LocalizationService.Get(driverStatus.State switch
        {
            IPhoneFilterDriverState.NoDevice => "DriverReconnectPhone",
            IPhoneFilterDriverState.PendingRestart => "DriverReplugRequired",
            IPhoneFilterDriverState.Missing => "DriverExternalRequired",
            IPhoneFilterDriverState.InvalidStack => "DriverInvalidAppleStack",
            IPhoneFilterDriverState.UnsafeStack => "DriverUnsafeAppleStack",
            _ => "DriverFilterStateError",
        });
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            CaptureStatus = failure;
        AddUiLog($"driver preflight: {driverStatus.Diagnostic}");
        if (driverStatus.State is IPhoneFilterDriverState.PendingRestart or
            IPhoneFilterDriverState.Missing or IPhoneFilterDriverState.InvalidStack or
            IPhoneFilterDriverState.Error)
            OpenDriverManager(automatic: true);
        var errorCode = (int)NativeResult.CaptureBackendUnavailable;
        var failureKind = driverStatus.State == IPhoneFilterDriverState.NoDevice
            ? CaptureFailureKind.UsbConnection
            : CaptureFailureKind.Driver;
        return (false, errorCode, failureKind, failure);
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

    private static string GetCaptureStatusText(NativeCaptureStatus status, bool wireless)
    {
        if (status.State == CaptureState.Streaming &&
            ProtectedContentStatus.Parse(status.Message, status.AudioSampleRate,
                status.AudioChannels).IsProtected)
            return LocalizationService.Get("CaptureVideoProtected");
        if (wireless && status.AudioSampleRate > 0 &&
            status.Width == 0 && status.Height == 0)
            return LocalizationService.Get("WirelessMusicStreaming");
        if (status.State == CaptureState.Error)
            return CaptureErrorGuidance.StatusText(status);
        return LocalizationService.Get(status.State switch
        {
            CaptureState.Idle => "CaptureIdle",
            CaptureState.ActivatingUsb => wireless ? "WirelessStarting" : "CaptureActivating",
            CaptureState.WaitingForDevice => wireless ? "WirelessWaitingDevice" : "CaptureWaitingDevice",
            CaptureState.Handshaking => wireless ? "WirelessConnecting" : "CaptureHandshaking",
            CaptureState.Streaming => wireless ? "WirelessStreaming" : "CaptureStreaming",
            CaptureState.Stopping => wireless ? "WirelessStopping" : "CaptureStopping",
            CaptureState.Stopped => wireless ? "WirelessStopped" : "CaptureStopped",
            _ => "CaptureError",
        });
    }

    private void SetAudioOnlyAirPlay(bool value)
    {
        if (!Set(ref _isAudioOnlyAirPlay, value, nameof(IsAudioOnlyAirPlay))) return;
        OnPropertyChanged(nameof(CanUseVisualPreviewTools));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
    }

    private void UpdateProtectionState(DeviceCaptureState state,
        ProtectedContentPresentation presentation)
    {
        if (!state.UpdateProtectionState(presentation.IsProtected,
                presentation.AudioActive, presentation.AudioSampleRate,
                presentation.AudioChannels))
            return;
        if (ReferenceEquals(state, CurrentDeviceSession))
        {
            OnPropertyChanged(nameof(IsVideoProtected));
            OnPropertyChanged(nameof(CanUseVisualPreviewTools));
            ProtectedAudioDisplay = presentation.AudioDisplay;
        }
        AddDiagnosticLog(AppLog.Event("protected_content_state",
            ("device", AppLog.Device(state.Udid)),
            ("handle", AppLog.Handle(state.Handle)),
            ("protected", presentation.IsProtected),
            ("audio_active", presentation.AudioActive),
            ("audio_rate", presentation.AudioSampleRate),
            ("audio_channels", presentation.AudioChannels)));
        PublishDeviceProtectionStateChanged(state.Udid, presentation);
    }

    private void PublishDeviceProtectionStateChanged(string udid,
        ProtectedContentPresentation presentation)
    {
        try { DeviceProtectionStateChanged?.Invoke(udid, presentation); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("window", "protected_overlay_update_failed",
                error, ("device", AppLog.Device(udid)),
                ("protected", presentation.IsProtected));
        }
    }

    private void RefreshWirelessStatus()
    {
        WirelessStatus = _wireless.GetStatusText();
        var signature = $"{_wireless.Backend}:{_wireless.AppliedBackend}:" +
            $"{_wireless.IsAvailable}:{_wireless.Running}:{_wireless.Ready}:" +
            AppLog.Sanitize(_wireless.StartError);
        if (!string.Equals(_lastWirelessStatusSignature, signature,
                StringComparison.Ordinal))
        {
            _lastWirelessStatusSignature = signature;
            AddDiagnosticLog(AppLog.Event("wireless_receiver_state",
                ("backend", _wireless.Backend.ToString()),
                ("applied_backend", _wireless.AppliedBackend.ToString()),
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
        var signature = $"{_mediaCast.SupportsCurrentWirelessBackend}:" +
            $"{_mediaCast.IsAvailable}:{_mediaCast.Running}:{_mediaCast.Ready}:" +
            AppLog.Sanitize(MediaCastStatus);
        if (!string.Equals(_lastMediaCastStatusSignature, signature,
                StringComparison.Ordinal))
        {
            _lastMediaCastStatusSignature = signature;
            AddDiagnosticLog(AppLog.Event("media_receiver_state",
                ("supports_wireless_backend", _mediaCast.SupportsCurrentWirelessBackend),
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

    private NativeSessionCreateResult CreateSession(
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
        try { _core.StopDeviceSession(created.Handle); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("capture", "failed_session_rollback_stop",
                error, ("handle", AppLog.Handle(created.Handle)));
        }
        _core.DestroyDeviceSession(created.Handle);
        return new(false, 0, (int)NativeResult.CaptureBackendUnavailable,
            adjustments.Message);
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
            DecoderRuntimeMode.External => "DecoderRuntimeExternal",
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
        foreach (var backend in WirelessReceiverBackends) backend.NotifyLanguageChanged();
        foreach (var mode in UsbProjectionModes) mode.NotifyLanguageChanged();
        foreach (var preference in DecoderPreferences) preference.NotifyLanguageChanged();
        foreach (var direction in BluetoothMouseDirections)
            direction.NotifyLanguageChanged();

        foreach (var device in Devices) device.NotifyLanguageChanged();

        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(AudioDetailDisplay));
        OnPropertyChanged(nameof(VirtualCameraInstallActionText));
        OnPropertyChanged(nameof(SelectedWirelessReceiverBackend));
        OnPropertyChanged(nameof(AppliedWirelessBackendDisplay));
        OnPropertyChanged(nameof(AppliedWirelessProfileDisplay));
        OnPropertyChanged(nameof(BluetoothControlActionText));
        OnPropertyChanged(nameof(BluetoothDeviceOrientationDisplay));
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
        if (device is null || device.IsMediaCast) return;
        _ = GetOrCreateDeviceState(device);
        ShowImageSettings(device.Udid);
    }

    internal void ShowImageSettings(string udid, nint ownerHwnd = 0)
    {
        if (_disposed || string.IsNullOrWhiteSpace(udid) ||
            DeviceViewModel.IsMediaCastUdid(udid)) return;
        if (_imageSettingsWindows.TryGetValue(udid, out var existing))
        {
            existing.Activate();
            existing.Focus();
            return;
        }
        if (IsSettingsInteractionBlocked)
        {
            SetSettingsStatus("ImageAdjustmentsBusy");
            AddUiLog(LocalizationService.Get("ImageAdjustmentsBusy"));
            return;
        }
        if (!_sessions.TryGet(udid, out var state)) return;
        var expectedHandle = state.Handle;
        var original = new ImageAdjustmentValues(state.Brightness, state.Contrast,
            state.Saturation, state.Gamma);
        var window = new ImageSettingsWindow(original,
            values => PreviewImageAdjustments(udid, state, expectedHandle, values),
            values => SaveImageAdjustments(udid, state, expectedHandle, values),
            values => RevertImageAdjustments(udid, state, expectedHandle, values));
        var mainWindow = Application.Current?.MainWindow;
        if (ownerHwnd == 0 && mainWindow is not null)
            window.Owner = mainWindow;
        else if (ownerHwnd != 0)
            new WindowInteropHelper(window).Owner = ownerHwnd;
        _imageSettingsWindows[udid] = window;
        var completed = false;
        void CompleteWindow()
        {
            if (completed) return;
            completed = true;
            if (_imageSettingsWindows.TryGetValue(udid, out var tracked) &&
                ReferenceEquals(tracked, window))
                _imageSettingsWindows.Remove(udid);
            SetSettingsDialogOpen(false);
        }
        window.Closed += (_, _) =>
        {
            CompleteWindow();
        };
        AddDiagnosticLog(AppLog.Event("image_adjustments_window_opened",
            ("device", AppLog.Device(udid)), ("handle", AppLog.Handle(expectedHandle))));
        // Serialize image and video setting submissions without disabling the
        // owner window. Disabling it applies WPF's washed-out overlay to the
        // source list and makes the connected-device state appear unavailable.
        SetSettingsDialogOpen(true);
        try
        {
            window.Show();
            window.Activate();
            window.Focus();
        }
        catch (Exception error)
        {
            CompleteWindow();
            try { window.CloseForShutdown(); }
            catch (Exception closeError)
            {
                DiagnosticLogger.Exception("window", "failed_window_cleanup", closeError);
            }
            SetSettingsStatus("ImageAdjustmentsUpdateFailed");
            AddDiagnosticLog(AppLog.Event("image_adjustments_window_failed",
                ("device", AppLog.Device(udid)),
                ("handle", AppLog.Handle(expectedHandle)),
                ("owner", AppLog.Handle((ulong)ownerHwnd)),
                ("error", AppLog.Error(error))));
        }
    }

    internal void ShowProjectionSettings(string udid)
    {
        if (_disposed || string.IsNullOrWhiteSpace(udid)) return;
        if (IsSettingsInteractionBlocked)
        {
            SetSettingsStatus("ImageAdjustmentsBusy");
            AddUiLog(LocalizationService.Get("ImageAdjustmentsBusy"));
            return;
        }
        var device = Devices.FirstOrDefault(candidate =>
            DeviceViewModel.UdidEquals(candidate.Udid, udid));
        if (device is null || device.IsMediaCast) return;
        SetSelectedDevice(device, updateDriverStatus: true);
        ProjectionSettingsRequested?.Invoke(udid);
    }

    private void SetSettingsDialogOpen(bool value)
    {
        if (_isSettingsDialogOpen == value) return;
        _isSettingsDialogOpen = value;
        ApplyVideoSettingsCommand.NotifyCanExecuteChanged();
        MoreImageSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeUsbProjectionMode));
        OnPropertyChanged(nameof(CanChangeVideoPipeline));
        OnPropertyChanged(nameof(CanChangeDecoderPipeline));
        OnPropertyChanged(nameof(CanOpenImageSettings));
    }

    private (bool Success, string Message) PreviewImageAdjustments(
        string udid, DeviceCaptureState expectedState, ulong expectedHandle,
        ImageAdjustmentValues values)
    {
        return RunImageSettingsOperation(() =>
        {
            if (_disposed || !_sessions.TryGet(udid, out var state) ||
                !ReferenceEquals(state, expectedState) ||
                !state.MatchesSessionHandle(expectedHandle))
                return (false, LocalizationService.Get("ImageAdjustmentsUpdateFailed"));
            if (expectedHandle == 0) return (true, string.Empty);
            return _core.SetDeviceImageAdjustments(state.Handle,
                values.Brightness, values.Contrast, values.Saturation, values.Gamma);
        });
    }

    private (bool Success, string Message) SaveImageAdjustments(
        string udid, DeviceCaptureState expectedState, ulong expectedHandle,
        ImageAdjustmentValues values)
    {
        return RunImageSettingsOperation(() =>
            SaveImageAdjustmentsLocked(udid, expectedState, expectedHandle, values));
    }

    private (bool Success, string Message) SaveImageAdjustmentsLocked(
        string udid, DeviceCaptureState expectedState, ulong expectedHandle,
        ImageAdjustmentValues values)
    {
        if (_disposed || !_sessions.TryGet(udid, out var state) ||
            !ReferenceEquals(state, expectedState) ||
            !state.MatchesSessionHandle(expectedHandle))
            return (false, LocalizationService.Get("ImageAdjustmentsUpdateFailed"));
        var hadSession = expectedHandle != 0;
        var result = !hadSession
            ? (Success: true, Message: string.Empty)
            : _core.SetDeviceImageAdjustments(state.Handle, values.Brightness,
                values.Contrast, values.Saturation, values.Gamma);
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
        string udid, DeviceCaptureState expectedState, ulong expectedHandle,
        ImageAdjustmentValues original)
    {
        return RunImageSettingsOperation(() =>
            RevertImageAdjustmentsLocked(udid, expectedState, expectedHandle, original));
    }

    private (bool Success, string Message) RevertImageAdjustmentsLocked(
        string udid, DeviceCaptureState expectedState, ulong expectedHandle,
        ImageAdjustmentValues original)
    {
        var result = !_sessions.TryGet(udid, out var state) ||
            !ReferenceEquals(state, expectedState) ||
            !state.MatchesSessionHandle(expectedHandle) || expectedHandle == 0
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

    private (bool Success, string Message) RunImageSettingsOperation(
        Func<(bool Success, string Message)> operation)
    {
        if (!_settingsGate.Wait(0))
            return (false, LocalizationService.Get("ImageAdjustmentsBusy"));
        try { return operation(); }
        finally { _settingsGate.Release(); }
    }

    private void InvalidateImageSettingsWindow(string udid)
    {
        if (!_imageSettingsWindows.Remove(udid, out var window)) return;
        window.CloseForSessionInvalidation();
    }

    internal async Task EnsureMediaOutputCapabilitiesAsync(bool force = false)
    {
        if (_disposed) return;
        RefreshVirtualCameraCapabilities();
        if (_mediaOutputCapabilitiesLoaded && !force &&
            _mediaOutputCapabilities.FfmpegAvailable) return;
        MediaOutputCapabilitiesText = LocalizationService.Get("MediaOutputChecking");
        var capabilities = await MediaOutputService.ProbeAsync(_shutdownCancellation.Token);
        _mediaOutputCapabilities = capabilities;
        _mediaOutputCapabilitiesLoaded = true;
        MediaOutputCapabilitiesText = capabilities.FfmpegAvailable
            ? LocalizationService.Format("MediaOutputCapabilitiesFormat",
                capabilities.HasRtmp ? "RTMP" : "—",
                capabilities.HasSrt ? "SRT" : "—",
                capabilities.HasWhip ? "WebRTC/WHIP" : "—",
                string.IsNullOrWhiteSpace(capabilities.PreferredH264Encoder)
                    ? "—" : capabilities.PreferredH264Encoder)
            : LocalizationService.Format("MediaOutputUnavailableFormat", capabilities.Detail);
        OnPropertyChanged(nameof(CanRecordMediaOutput));
        OnPropertyChanged(nameof(CanStreamRtmp));
        OnPropertyChanged(nameof(CanStreamSrt));
        OnPropertyChanged(nameof(CanStreamWhip));
        OnPropertyChanged(nameof(CanStartMediaOutput));
        OnPropertyChanged(nameof(CanUseVirtualCamera));
        OnPropertyChanged(nameof(CanInstallVirtualCamera));
        OnPropertyChanged(nameof(CanUninstallVirtualCamera));
        OnPropertyChanged(nameof(VirtualCameraInstallVisibility));
        OnPropertyChanged(nameof(VirtualCameraStartVisibility));
        OnPropertyChanged(nameof(VirtualCameraUninstallVisibility));
        AddDiagnosticLog(AppLog.Event("media_output_capabilities",
            ("ffmpeg", capabilities.FfmpegAvailable),
            ("path", capabilities.FfmpegPath),
            ("encoder", capabilities.PreferredH264Encoder),
            ("rtmp", capabilities.HasRtmp),
            ("srt", capabilities.HasSrt),
            ("whip", capabilities.HasWhip),
            ("detail", capabilities.Detail)));
    }

    private void RefreshVirtualCameraCapabilities()
    {
        _virtualCameraCapabilities = VirtualCameraService.Probe();
        VirtualCameraStatusText = !_virtualCameraCapabilities.BackendAvailable
            ? LocalizationService.Get("VirtualCameraBackendMissing")
            : !_virtualCameraCapabilities.Supported
                ? LocalizationService.Get("VirtualCameraUnsupported")
                : !_virtualCameraCapabilities.Registered
                    ? LocalizationService.Get("VirtualCameraInstallRequired")
                    : _virtualCameraCapabilities.UpdateRequired
                        ? LocalizationService.Get("VirtualCameraUpdateRequired")
                        : _virtualCamera.IsRunning
                            ? LocalizationService.Get("VirtualCameraRunning")
                            : LocalizationService.Get("VirtualCameraReady");
        OnPropertyChanged(nameof(CanUseVirtualCamera));
        OnPropertyChanged(nameof(CanInstallVirtualCamera));
        OnPropertyChanged(nameof(CanUninstallVirtualCamera));
        OnPropertyChanged(nameof(VirtualCameraInstallVisibility));
        OnPropertyChanged(nameof(VirtualCameraStartVisibility));
        OnPropertyChanged(nameof(VirtualCameraUninstallVisibility));
        OnPropertyChanged(nameof(VirtualCameraInstallActionText));
        AddDiagnosticLog(AppLog.Event("virtual_camera_capabilities",
            ("backend", _virtualCameraCapabilities.BackendAvailable),
            ("supported", _virtualCameraCapabilities.Supported),
            ("registered", _virtualCameraCapabilities.Registered),
            ("updateRequired", _virtualCameraCapabilities.UpdateRequired),
            ("running", _virtualCameraCapabilities.Running),
            ("detail", _virtualCameraCapabilities.Detail)));
    }

    internal async Task<(bool Success, string Message)> StartRecordingAsync(
        uint width, uint height, int frameRate, int bitrateKbps)
    {
        if (PendingRecordingPath is not null)
            return (false, LocalizationService.Get("RecordingPendingSave"));
        var path = PendingRecordingStore.CreatePath();
        var request = new MediaOutputRequest(MediaOutputKind.Recording, path,
            NormalizeOutputWidth(width), NormalizeOutputHeight(height), frameRate, bitrateKbps);
        var result = await StartMediaOutputAsync(request);
        if (result.Success) _pendingRecordingPath = path;
        else
        {
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                DiagnosticLogger.Exception("recording", "failed_output_cleanup",
                    error, ("file", Path.GetFileName(path)));
            }
        }
        return result;
    }

    internal void MarkPendingRecordingSaved(string path)
    {
        if (string.Equals(_pendingRecordingPath, path,
                StringComparison.OrdinalIgnoreCase))
            _pendingRecordingPath = PendingRecordingStore.FindLatest();
    }

    internal async Task<(bool Success, string Message)> StartStreamingAsync(
        MediaOutputKind kind, string destination, string authorization,
        uint width, uint height, int frameRate, int bitrateKbps)
    {
        if (kind is MediaOutputKind.Recording)
            return (false, LocalizationService.Get("MediaOutputInvalidProtocol"));
        var request = new MediaOutputRequest(kind, destination,
            NormalizeOutputWidth(width), NormalizeOutputHeight(height),
            frameRate, bitrateKbps, authorization);
        return await StartMediaOutputAsync(request);
    }

    internal async Task<(bool Success, string Message)> InstallVirtualCameraAsync()
    {
        await _mediaOutputGate.WaitAsync(_shutdownCancellation.Token);
        SetMediaOutputTransitioning(true);
        try
        {
            await EnsureMediaOutputCapabilitiesAsync();
            if (IsMediaOutputRunning)
                return (false, LocalizationService.Get("MediaOutputAlreadyRunning"));
            if (!_virtualCameraCapabilities.BackendAvailable ||
                !_virtualCameraCapabilities.Supported)
                return (false, VirtualCameraStatusText);
            var updating = _virtualCameraCapabilities.UpdateRequired;
            SetMediaOutputStatus(LocalizationService.Get(updating
                    ? "VirtualCameraUpdating" : "VirtualCameraInstalling"),
                "Pending");
            await VirtualCameraService.InstallAsync(_shutdownCancellation.Token);
            RefreshVirtualCameraCapabilities();
            if (!_virtualCameraCapabilities.Registered ||
                _virtualCameraCapabilities.UpdateRequired)
                throw new InvalidOperationException(
                    LocalizationService.Get("VirtualCameraInstallNotDetected"));
            var message = LocalizationService.Get(updating
                ? "VirtualCameraUpdated" : "VirtualCameraInstalled");
            SetMediaOutputStatus(message, "Applied");
            AddDiagnosticLog(AppLog.Event("virtual_camera_installed"));
            return (true, message);
        }
        catch (Exception error)
        {
            RefreshVirtualCameraCapabilities();
            var message = LocalizationService.Format(
                "VirtualCameraInstallFailedFormat", error.Message);
            SetMediaOutputStatus(message, "Failed");
            AddDiagnosticLog(AppLog.Event("virtual_camera_install_failed",
                ("error", AppLog.Error(error))));
            return (false, message);
        }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    internal async Task<(bool Success, string Message)> UninstallVirtualCameraAsync()
    {
        await _mediaOutputGate.WaitAsync(_shutdownCancellation.Token);
        SetMediaOutputTransitioning(true);
        try
        {
            if (IsMediaOutputRunning)
                return (false, LocalizationService.Get("MediaOutputAlreadyRunning"));
            SetMediaOutputStatus(LocalizationService.Get("VirtualCameraUninstalling"),
                "Pending");
            await VirtualCameraService.UninstallAsync(_shutdownCancellation.Token);
            RefreshVirtualCameraCapabilities();
            if (_virtualCameraCapabilities.Registered)
                throw new InvalidOperationException(
                    LocalizationService.Get("VirtualCameraUninstallStillDetected"));
            var message = LocalizationService.Get("VirtualCameraUninstalled");
            SetMediaOutputStatus(message, "Applied");
            AddDiagnosticLog(AppLog.Event("virtual_camera_uninstalled"));
            return (true, message);
        }
        catch (Exception error)
        {
            RefreshVirtualCameraCapabilities();
            var message = LocalizationService.Format(
                "VirtualCameraUninstallFailedFormat", error.Message);
            SetMediaOutputStatus(message, "Failed");
            AddDiagnosticLog(AppLog.Event("virtual_camera_uninstall_failed",
                ("error", AppLog.Error(error))));
            return (false, message);
        }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    internal async Task<(bool Success, string Message)> StartVirtualCameraAsync(
        uint width, uint height, int frameRate)
    {
        if (_disposed) return (false, LocalizationService.Get("CaptureStopped"));
        await _mediaOutputGate.WaitAsync(_shutdownCancellation.Token);
        SetMediaOutputTransitioning(true);
        try
        {
            await EnsureMediaOutputCapabilitiesAsync();
            if (IsMediaOutputRunning)
                return (false, LocalizationService.Get("MediaOutputAlreadyRunning"));
            var handle = CurrentSessionHandle;
            var device = SelectedDevice;
            var mediaCast = IsMediaCasting && IsMediaCastSelected;
            DeviceCaptureState? expectedState = null;
            if (!mediaCast && device is not null)
                _sessions.TryGet(device.Udid, out expectedState);
            if (!_virtualCameraCapabilities.Registered ||
                _virtualCameraCapabilities.UpdateRequired || device is null ||
                (!mediaCast && (handle == 0 || expectedState is null ||
                    !expectedState.MatchesSessionHandle(handle))) ||
                (mediaCast && _mediaCastVideoFrameProvider is null))
                return (false, _virtualCameraCapabilities.Registered &&
                    !_virtualCameraCapabilities.UpdateRequired
                        ? LocalizationService.Get("MediaOutputNoSession")
                        : VirtualCameraStatusText);
            if (mediaCast) handle = MediaCastOutputHandle;
            // RGB32 Frame Server rows are aligned to 64 bytes. Four bytes per
            // pixel therefore requires a width aligned to 16 pixels; choose
            // the nearest aligned width to preserve the source aspect ratio.
            width = NormalizeVirtualCameraWidth(width);
            height = NormalizeOutputHeight(height);
            frameRate = Math.Clamp(frameRate, 10, 60);
            await _virtualCamera.StartAsync(handle, width, height,
                frameRate, _shutdownCancellation.Token);
            if (_disposed || !DeviceViewModel.UdidEquals(SelectedDevice?.Udid,
                device.Udid) || (!mediaCast &&
                (!_sessions.TryGet(device.Udid, out var currentState) ||
                 !ReferenceEquals(expectedState, currentState) ||
                 currentState.Handle != handle)))
            {
                await _virtualCamera.StopAsync();
                var staleMessage = LocalizationService.Get("MediaOutputNoSession");
                SetMediaOutputStatus(staleMessage, "Failed");
                return (false, staleMessage);
            }
            _mediaOutputUdid = device.Udid;
            RefreshVirtualCameraCapabilities();
            var message = LocalizationService.Get("VirtualCameraStarted");
            SetMediaOutputStatus(message, "Applied");
            NotifyMediaOutputStateChanged();
            AddDiagnosticLog(AppLog.Event("virtual_camera_started",
                ("device", AppLog.Device(device.Udid)),
                ("handle", AppLog.Handle(handle)),
                ("size", $"{width}x{height}"), ("fps", frameRate)));
            return (true, message);
        }
        catch (Exception error)
        {
            RefreshVirtualCameraCapabilities();
            var message = LocalizationService.Format(
                "MediaOutputStartFailedFormat", error.Message);
            SetMediaOutputStatus(message, "Failed");
            AddDiagnosticLog(AppLog.Event("virtual_camera_start_failed",
                ("error", AppLog.Error(error))));
            return (false, message);
        }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    internal async Task StopMediaOutputAsync()
    {
        await _mediaOutputGate.WaitAsync();
        SetMediaOutputTransitioning(true);
        try { await StopMediaOutputLockedAsync(); }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    private async Task StopMediaOutputForSessionAsync(string udid)
    {
        await _mediaOutputGate.WaitAsync();
        SetMediaOutputTransitioning(true);
        try
        {
            if (DeviceViewModel.UdidEquals(_mediaOutputUdid, udid))
                await StopMediaOutputLockedAsync();
        }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    private async Task StopMediaOutputLockedAsync()
    {
        if (!IsMediaOutputRunning) return;
        SetMediaOutputStatus(LocalizationService.Get("MediaOutputStopping"), "Pending");
        if (_mediaOutput.IsRunning) await _mediaOutput.StopAsync();
        if (_virtualCamera.IsRunning) await _virtualCamera.StopAsync();
        _mediaOutputUdid = null;
        RefreshVirtualCameraCapabilities();
        NotifyMediaOutputStateChanged();
    }

    internal (uint Width, uint Height) SuggestedMediaOutputSize()
    {
        var state = CurrentDeviceSession;
        var width = SourceVideoWidth;
        var height = SourceVideoHeight;
        if (width == 0 || height == 0)
        {
            width = state?.AppliedRenderWidth > 0
                ? state.AppliedRenderWidth : state?.RenderWidth ?? 0;
            height = state?.AppliedRenderHeight > 0
                ? state.AppliedRenderHeight : state?.RenderHeight ?? 0;
        }
        if (width == 0 || height == 0)
        {
            width = SelectedResolutionPreset.Width;
            height = SelectedResolutionPreset.Height;
        }
        if (width == 0 || height == 0)
        {
            width = 1280;
            height = 720;
        }
        if (width > 3840 || height > 2160)
        {
            var scale = Math.Min(3840.0 / width, 2160.0 / height);
            width = (uint)Math.Max(160, Math.Round(width * scale));
            height = (uint)Math.Max(160, Math.Round(height * scale));
        }
        return (NormalizeOutputWidth(width), NormalizeOutputHeight(height));
    }

    private async Task<(bool Success, string Message)> StartMediaOutputAsync(
        MediaOutputRequest request)
    {
        if (_disposed) return (false, LocalizationService.Get("CaptureStopped"));
        await _mediaOutputGate.WaitAsync(_shutdownCancellation.Token);
        SetMediaOutputTransitioning(true);
        try
        {
            await EnsureMediaOutputCapabilitiesAsync();
            if (IsMediaOutputRunning)
                return (false, LocalizationService.Get("MediaOutputAlreadyRunning"));
            var handle = CurrentSessionHandle;
            var device = SelectedDevice;
            var mediaCast = IsMediaCasting && IsMediaCastSelected;
            DeviceCaptureState? expectedState = null;
            if (!mediaCast && device is not null)
                _sessions.TryGet(device.Udid, out expectedState);
            if (device is null || (!mediaCast &&
                (handle == 0 || expectedState is null ||
                 !expectedState.MatchesSessionHandle(handle))) ||
                (mediaCast && _mediaCastNv12FrameProvider is null))
                return (false, LocalizationService.Get("MediaOutputNoSession"));
            if (mediaCast) handle = MediaCastOutputHandle;
            await _mediaOutput.StartAsync(handle, request, _mediaOutputCapabilities,
                _shutdownCancellation.Token);
            if (_disposed || !DeviceViewModel.UdidEquals(SelectedDevice?.Udid,
                device.Udid) || (!mediaCast &&
                (!_sessions.TryGet(device.Udid, out var currentState) ||
                 !ReferenceEquals(expectedState, currentState) ||
                 currentState.Handle != handle)))
            {
                await _mediaOutput.StopAsync();
                var staleMessage = LocalizationService.Get("MediaOutputNoSession");
                SetMediaOutputStatus(staleMessage, "Failed");
                AddDiagnosticLog(AppLog.Event("media_output_start_invalidated",
                    ("device", AppLog.Device(device.Udid)),
                    ("handle", AppLog.Handle(handle))));
                return (false, staleMessage);
            }
            _mediaOutputUdid = device.Udid;
            SetMediaOutputStatus(LocalizationService.Format(
                request.Kind == MediaOutputKind.Recording
                    ? "MediaOutputRecordingFormat"
                    : "MediaOutputStreamingFormat",
                MediaOutputKindLabel(request.Kind)), "Applied");
            NotifyMediaOutputStateChanged();
            AddUiLog(MediaOutputStatus);
            AddDiagnosticLog(AppLog.Event("media_output_started",
                ("device", AppLog.Device(device.Udid)),
                ("handle", AppLog.Handle(handle)),
                ("kind", request.Kind),
                ("size", $"{request.Width}x{request.Height}"),
                ("fps", request.FrameRate),
                ("bitrate_kbps", request.BitrateKbps)));
            return (true, MediaOutputStatus);
        }
        catch (Exception error)
        {
            var message = LocalizationService.Format(
                "MediaOutputStartFailedFormat", error.Message);
            SetMediaOutputStatus(message, "Failed");
            AddDiagnosticLog(AppLog.Event("media_output_start_failed",
                ("kind", request.Kind), ("error", AppLog.Error(error))));
            return (false, message);
        }
        finally
        {
            SetMediaOutputTransitioning(false);
            _mediaOutputGate.Release();
        }
    }

    private void OnMediaOutputStatusChanged(string message, bool failed)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => OnMediaOutputStatusChanged(message, failed));
            return;
        }
        var localized = message switch
        {
            "Recording" => LocalizationService.Get("MediaOutputRecording"),
            "Live" => LocalizationService.Get("MediaOutputStreaming"),
            "VirtualCamera" => LocalizationService.Get("VirtualCameraRunning"),
            "Stopped" => LocalizationService.Get("MediaOutputStopped"),
            _ => message,
        };
        if (failed) SetMediaOutputStatus(
            LocalizationService.Format("MediaOutputFailedFormat", localized), "Failed");
        else SetMediaOutputStatus(localized, localized == LocalizationService.Get("MediaOutputStopped")
            ? "Hidden" : "Applied");
        if (!IsMediaOutputRunning) _mediaOutputUdid = null;
        if (_virtualCameraCapabilities.BackendAvailable)
            RefreshVirtualCameraCapabilities();
        NotifyMediaOutputStateChanged();
    }

    private void NotifyMediaOutputStateChanged()
    {
        OnPropertyChanged(nameof(IsMediaOutputRunning));
        OnPropertyChanged(nameof(IsMediaOutputTransitioning));
        OnPropertyChanged(nameof(CanStopMediaOutput));
        OnPropertyChanged(nameof(CanStartMediaOutput));
        OnPropertyChanged(nameof(CanUseVirtualCamera));
        OnPropertyChanged(nameof(CanInstallVirtualCamera));
        OnPropertyChanged(nameof(CanUninstallVirtualCamera));
        OnPropertyChanged(nameof(VirtualCameraUninstallVisibility));
    }

    private void SetMediaOutputTransitioning(bool value)
    {
        if (_isMediaOutputTransitioning == value) return;
        _isMediaOutputTransitioning = value;
        NotifyMediaOutputStateChanged();
    }

    private void SetMediaOutputStatus(string text, string tone)
    {
        MediaOutputStatus = text;
        MediaOutputTone = tone;
    }

    private static uint NormalizeOutputWidth(uint value) =>
        Math.Clamp(value == 0 ? 1280U : value & ~1U, 160U, 3840U);

    private static uint NormalizeOutputHeight(uint value) =>
        Math.Clamp(value == 0 ? 720U : value & ~1U, 160U, 2160U);

    private static uint NormalizeVirtualCameraWidth(uint value)
    {
        value = NormalizeOutputWidth(value);
        return Math.Clamp((value + 8U) & ~15U, 160U, 3840U);
    }

    private static string MediaOutputKindLabel(MediaOutputKind kind) => kind switch
    {
        MediaOutputKind.Rtmp => "RTMP",
        MediaOutputKind.Srt => "SRT",
        MediaOutputKind.Whip => "WebRTC/WHIP",
        _ => "MP4",
    };

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
        NativeSessionCreateResult? failedCreate = null;
        NativeCaptureStatus? failedStatus = null;
        try
        {
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            await StopMediaOutputForSessionAsync(state.Udid);
            await _sessions.StopAndDestroyAsync(state);
            ClearSelectedSessionState(state.Udid);
            // Native start waits for the device to expose a stable QuickTime
            // descriptor. Do not add speculative delays or repeat activation;
            // a failed state is surfaced with its native stage and code.
            NativeSessionCreateResult created = new(false, 0, 0, string.Empty);
            {
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
                    failedCreate = created;
                    throw new InvalidOperationException(created.Message);
                }

                _sessions.SetHandle(state, created.Handle);
                NotifyCaptureSessionChanged();
                var deadline = DateTime.UtcNow.AddSeconds(6);
                var ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, _shutdownCancellation.Token);
                    if (_disposed) return;
                    NativeCaptureStatus status;
                    try { status = await Task.Run(() => _core.GetDeviceSessionStatus(created.Handle)); }
                    catch (Exception error)
                    {
                        DiagnosticLogger.Exception("capture", "restart_status_failed",
                            error, ("device", AppLog.Device(state.Udid)),
                            ("handle", AppLog.Handle(created.Handle)));
                        throw;
                    }
                    if (status.State == CaptureState.Streaming) { ready = true; break; }
                    if (status.State == CaptureState.Error || status.State == CaptureState.Stopped)
                    {
                        failedStatus = status;
                        throw new InvalidOperationException(status.Message);
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
                throw new InvalidOperationException(
                    "USB capture session did not reach Streaming before the readiness timeout");
            }
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
            if (state.Handle != 0)
            {
                try { await _sessions.StopAndDestroyAsync(state); }
                catch (Exception cleanupError)
                {
                    DiagnosticLogger.Exception("capture", "restart_cleanup_failed",
                        cleanupError, ("device", AppLog.Device(state.Udid)));
                }
            }
            ClearSelectedSessionState(state.Udid);
            NotifyCaptureSessionChanged();
            // Settings-triggered restarts use the same error contract as an
            // initial start. Do not leave a failed USB/QuickTime transition
            // represented only by the generic status-bar text.
            if (!_disposed)
            {
                var errorBody = failedStatus is { } status
                    ? CaptureErrorGuidance.UserMessage(status)
                    : CaptureErrorGuidance.StartFailureMessage(
                        failedCreate?.ErrorCode ??
                            (int)NativeResult.TransportUnavailable,
                        failedCreate?.Message ?? error.Message);
                CaptureStatusNoticeWindow.ShowError(
                    LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                        device.DisplayName), errorBody);
            }
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
        BluetoothControlNoticeWindow.ActiveNoticeClosed -= OnBluetoothControlNoticeClosed;
        await DisableUsbControlAsync();
        await DisableWirelessControlAsync();
        await _bluetoothControl.DisposeAsync();
        _mediaOutput.StatusChanged -= OnMediaOutputStatusChanged;
        _virtualCamera.StatusChanged -= OnMediaOutputStatusChanged;
        try
        {
            await _mediaOutput.DisposeAsync();
            await _virtualCamera.DisposeAsync();
        }
        catch (Exception error)
        {
            AddDiagnosticLog(AppLog.Event("media_output_shutdown_failed",
                ("error", AppLog.Error(error))));
        }
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
                            try
                            {
                                await _sessions.StopAndDestroyAsync(session);
                            }
                            catch (UsbConfigurationRestoreWarningException warning)
                            {
                                AddDiagnosticLog(AppLog.Event("app_shutdown_stop_warning",
                                    ("device", AppLog.Device(session.Udid)),
                                    ("warning_code", warning.ErrorCode),
                                    ("warning", AppLog.Message(warning.Message))));
                            }
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
            catch (Exception loggingError)
            {
                DiagnosticLogger.Exception("logging", "shutdown_log_failed",
                    loggingError);
            }
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
