using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

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

internal sealed class MainViewModel : INotifyPropertyChanged
{
    // The first InteropBitmap experiment showed row corruption on this WPF
    // build. Keep the implementation available for a corrected memory-section
    // layout, but use the verified WriteableBitmap path in production.
    private static readonly bool EnableInteropPreview = false;
    private readonly NativeCore _core;
    private readonly IPhoneFilterDriverService _filterDriver = new();
    // Serializes every native-core operation that can race USB teardown,
    // device enumeration, restart, or application shutdown.
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    // Device selection can change repeatedly while the native stop operation
    // is still restoring the previous phone's normal USB configuration.
    // Serialize those transitions and let only the newest selection win.
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private readonly CaptureShutdownCoordinator _shutdownCoordinator = new();
    private readonly Dictionary<string, DeviceCaptureState> _deviceSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private DeviceViewModel? _selectedDevice;
    private string _environmentStatus = string.Empty;
    private string _captureStatus = string.Empty;
    private string _driverState = string.Empty;
    private bool _isCapturing;
    private bool _isBusy;
    private string? _activeCaptureUdid;
    // Unlike _activeCaptureUdid, this survives a terminal/error status until
    // StopCapture has actually joined the native session and sent HPA0/HPD0.
    // It prevents a device switch from abandoning a failed/starting session.
    private readonly CaptureSessionOwnership _captureSession = new();
    private int _manualRefreshPending;
    // Installation can restart the device node programmatically, but the
    // libusb0 filter is only considered ready for this GUI after a real
    // unplug/replug cycle has been observed for the exact UDID.
    private readonly DriverReplugTracker _driverReplug = new();
    private ImageSource? _previewSource;
    // WPF's compositor can retain an ImageSource for more than one render
    // pass.  Reusing the immediately previous WriteableBitmap therefore lets
    // WritePixels overwrite a surface while it is still being scanned out,
    // which appears as horizontal/vertical strips from two different frames.
    // Keep a small ring and never write a bitmap again until several other
    // surfaces have been submitted.
    private WriteableBitmap[] _previewBuffers = [];
    private MemoryMappedFile[] _previewMaps = [];
    private MemoryMappedViewAccessor[] _previewViews = [];
    private InteropBitmap[] _interopBuffers = [];
    private bool _useInteropPreview;
    private int _nextPreviewBuffer;
    private long _lastPreviewTimestamp;
    private long _renderedFrames;
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private long _lastFpsUpdateTicks;
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
    private string _settingsStatus = string.Empty;
    private string? _settingsStatusKey = "StatusDefaultSettings";
    private object?[] _settingsStatusArguments = [];
    private string _logText = string.Empty;
    private string _selectedLanguage = LocalizationService.SystemLanguage;
    private NativeEnvironmentInfo? _lastEnvironment;
    private NativeCaptureStatus? _lastCaptureStatus;
    private IPhoneFilterDriverStatus _filterDriverStatus = new(
        IPhoneFilterDriverState.NoDevice, string.Empty, null, null, string.Empty);
    private readonly Queue<string> _visibleLogLines = new();
    private long _logPosition;
    private string _partialLogLine = string.Empty;
    private readonly Decoder _logDecoder = Encoding.UTF8.GetDecoder();
    private readonly string _logPath = Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE")
        ?? Path.Combine(Path.GetTempPath(), "iPhoneMirror-capture.log");

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

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand DriverHelpCommand { get; }
    public RelayCommand InstallDriverCommand { get; }
    public RelayCommand ApplyVideoSettingsCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set => SetSelectedDevice(value, scheduleCaptureSwitch: true);
    }

    public string EnvironmentStatus { get => _environmentStatus; private set => Set(ref _environmentStatus, value); }
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }
    public string DriverState { get => _driverState; private set => Set(ref _driverState, value); }
    private DeviceCaptureState? CurrentDeviceSession => SelectedDevice is null ? null :
        _deviceSessions.GetValueOrDefault(SelectedDevice.Udid);
    public ulong CurrentSessionHandle => CurrentDeviceSession?.Handle ?? 0;
    public bool HasCaptureSession => CurrentDeviceSession?.HasSession == true;
    private bool AnyDeviceSession => _deviceSessions.Values.Any(session => session.HasSession);
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
            InstallDriverCommand.NotifyCanExecuteChanged();
        }
    }
    private bool IsOperationBusy => IsBusy;
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

    // Kept as a compatibility alias for older XAML and start options.
    public bool CaptureAudio { get => PlayAudio; set => PlayAudio = value; }

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
    public string LogPathDisplay => _logPath;
    public Visibility DriverInstallVisibility => !_driverReplug.IsPending(SelectedDevice?.Udid) &&
        _filterDriverStatus.State is IPhoneFilterDriverState.Missing or
            IPhoneFilterDriverState.PackageMissing
            ? Visibility.Visible : Visibility.Collapsed;
    public string DriverInstallButtonText => LocalizationService.Get(
        _filterDriverStatus.State == IPhoneFilterDriverState.PackageMissing
            ? "DriverPackageMissingButton" : "InstallCaptureDriver");

    public ImageSource? PreviewSource { get => _previewSource; private set => Set(ref _previewSource, value); }

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
        _selectedResolutionPreset = ResolutionPresets[0];
        StartCommand = new RelayCommand(() => _ = StartAsync(),
            () => SelectedDevice is not null && !_driverReplug.IsPending(SelectedDevice.Udid) &&
                  !HasCaptureSession && !IsCapturing && !IsOperationBusy);
        StopCommand = new RelayCommand(() => _ = StopAsync(),
            () => HasCaptureSession && !IsOperationBusy);
        // A manual refresh is guaranteed to run after a short in-flight poll;
        // timer refreshes remain best-effort and never build up a queue.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceDeviceEnumeration: true));
        DriverHelpCommand = new RelayCommand(ShowDriverHelp);
        InstallDriverCommand = new RelayCommand(() => _ = InstallSelectedDriverAsync(),
            () => !IsOperationBusy && !IsCapturing &&
                  !_driverReplug.IsPending(SelectedDevice?.Udid) && _filterDriverStatus.CanInstall);
        ApplyVideoSettingsCommand = new RelayCommand(() => _ = ApplyVideoSettingsAsync(),
            () => !IsOperationBusy);
        ClearLogCommand = new RelayCommand(ClearVisibleLog);
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
                if (IsOperationBusy || !await _coreGate.WaitAsync(0)) return;
                gateHeld = true;
            }
            if (_disposed) return;

            // Polling during capture stays status-only. An explicit Refresh,
            // however, performs usbmux/Lockdown discovery so a second iPhone
            // appears without interrupting the active libusb0 capture. It
            // deliberately skips GetEnvironment(), whose global USB probe is
            // unnecessary here and unsafe to run beside a live libusb0 handle.
            if (AnyDeviceSession && !forceDeviceEnumeration)
            {
                var activeCapture = await Task.Run(GetSelectedCaptureStatus);
                ApplyCaptureStatus(activeCapture);
                return;
            }

            NativeEnvironmentInfo? environment = null;
            IReadOnlyList<NativeDeviceInfo> nativeDevices;
            NativeCaptureStatus capture;
            if (AnyDeviceSession)
            {
                (nativeDevices, capture) = await Task.Run(() =>
                    (_core.GetDevices(), GetSelectedCaptureStatus()));
            }
            else
            {
                var result = await Task.Run(() =>
                    (_core.GetEnvironment(), _core.GetDevices(), GetSelectedCaptureStatus()));
                environment = result.Item1;
                nativeDevices = result.Item2;
                capture = result.Item3;
            }

            if (environment is { } currentEnvironment)
            {
                _lastEnvironment = currentEnvironment;
                UpdateEnvironmentStatus(currentEnvironment);
            }

            var devices = nativeDevices
                .Where(device => !string.IsNullOrWhiteSpace(device.Udid))
                .Select(DeviceViewModel.FromNative)
                .GroupBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var captureActive = IsActiveCaptureState(capture.State);
            // Device discovery runs off the UI thread and can overlap a real
            // user click. A selection captured before that await is stale and
            // used to snap the highlight back to the old phone when the poll
            // completes. Read the current UDID only when applying the result.
            var currentSelectionUdid = SelectedDevice?.Udid;
            ReconcileDevices(devices, currentSelectionUdid, captureActive);
            ApplyCaptureStatus(capture);
            await PollBackgroundSessionErrorsAsync();

            if (forceDeviceEnumeration)
                AddUiLog($"device refresh: discovered={devices.Count} visible={Devices.Count} " +
                    $"selected={SelectedDevice?.Udid ?? "-"} active={_activeCaptureUdid ?? "-"}");
        }
        catch (Exception error)
        {
            EnvironmentStatus = LocalizationService.Format("CoreLoadFailedFormat", error.Message);
            DriverState = LocalizationService.Get("Unavailable");
            if (forceDeviceEnumeration) AddUiLog($"device refresh failed: {error.Message}");
        }
        finally
        {
            if (gateHeld) _coreGate.Release();
            if (forceDeviceEnumeration) Interlocked.Exchange(ref _manualRefreshPending, 0);
        }
    }

    private void ReconcileDevices(
        IReadOnlyList<DeviceViewModel> discovered,
        string? previousSelectionUdid,
        bool captureActive)
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
        foreach (var sessionUdid in _deviceSessions.Values
                     .Where(session => session.HasSession).Select(session => session.Udid))
        {
            if (desired.Any(device => DeviceViewModel.UdidEquals(device.Udid, sessionUdid))) continue;
            var retained = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, sessionUdid));
            if (retained is not null) desired.Add(retained);
        }

        TrackDriverReplugCycle(desired);

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
            Devices.Select(device => device.Udid), previousSelectionUdid, _activeCaptureUdid);
        var nextSelection = Devices.FirstOrDefault(device =>
            DeviceViewModel.UdidEquals(device.Udid, nextUdid));
        SetSelectedDevice(nextSelection, scheduleCaptureSwitch: false);
        NotifySelectedDeviceProperties();

        // Never invoke the legacy libusb0 enumeration API while a capture
        // handle is live. The selected device's driver state is refreshed as
        // soon as capture stops or an automatic switch completes.
        if (!captureActive) UpdateSelectedDriverStatus();
    }

    private void SetSelectedDevice(DeviceViewModel? value, bool scheduleCaptureSwitch)
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
        SetCaptureSessionOwner(_activeCaptureUdid);
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

        // A transient null can be published by WPF while a ListBox item is
        // being moved. Let the already queued transition observe the changed
        // selection and release its busy state instead of orphaning it behind
        // a revision that has no corresponding task.
        if (scheduleCaptureSwitch && !HasCaptureSession)
            UpdateSelectedDriverStatus();
    }

    private void NotifySelectedDeviceProperties()
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(SelectedUdid));
        OnPropertyChanged(nameof(SelectedConnection));
    }

    private void SetCaptureSessionOwner(string? udid)
    {
        if (!_captureSession.SetOwner(udid)) return;
        OnPropertyChanged(nameof(HasCaptureSession));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void TrackDriverReplugCycle(IReadOnlyList<DeviceViewModel> discovered)
    {
        if (!_driverReplug.Any) return;
        foreach (var udid in _driverReplug.ObservePresent(discovered.Select(device => device.Udid)))
        {
            AddUiLog(LocalizationService.Format("DriverReplugDetectedFormat", udid));
            NotifyDriverReplugStateChanged();
        }
    }

    private void NotifyDriverReplugStateChanged()
    {
        OnPropertyChanged(nameof(DriverInstallVisibility));
        StartCommand.NotifyCanExecuteChanged();
        InstallDriverCommand.NotifyCanExecuteChanged();
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
        foreach (var state in _deviceSessions.Values.Where(value =>
                     value.Handle != 0 && value.Handle != CurrentSessionHandle).ToArray())
        {
            NativeCaptureStatus status;
            try { status = await Task.Run(() => _core.GetDeviceSessionStatus(state.Handle)); }
            catch { continue; }
            state.LastStatus = new NativeCaptureStatusSnapshot
            {
                Width = status.Width,
                Height = status.Height,
                Fps = status.Fps,
                LatencyMs = status.LatencyMs,
            };
            if (status.State != CaptureState.Error || state.ErrorShown) continue;
            state.ErrorShown = true;
            var name = Devices.FirstOrDefault(device =>
                DeviceViewModel.UdidEquals(device.Udid, state.Udid))?.DisplayName ?? state.Udid;
            MessageBox.Show(status.Message, LocalizationService.Format(
                "DeviceCaptureErrorTitleFormat", name), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetPreviewState()
    {
        DisposePreviewBuffers();
        PreviewSource = null;
        _lastPreviewTimestamp = 0;
        _renderedFrames = 0;
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
        if (_disposed || SelectedDevice is null || HasCaptureSession || IsOperationBusy) return;
        IsBusy = true;
        var gateHeld = false;
        try
        {
            // One-click start includes the per-iPhone filter setup. Windows
            // still displays its normal UAC consent dialog; the main GUI never
            // runs elevated.
            if (!await EnsureSelectedDriverReadyAsync(confirmInstall: true)) return;
            // A user click that lands during the short background poll should
            // run immediately after it, rather than being silently discarded.
            await _coreGate.WaitAsync();
            gateHeld = true;
            if (_disposed) return;
            var device = SelectedDevice;
            if (device is null) return;
            _lastPreviewTimestamp = 0;
            _renderedFrames = 0;
            _renderClock.Restart();
            _lastFpsUpdateTicks = 0;
            var preference = (Success: true, Message: LocalizationService.Get("VideoPreferencesApplied"));
            // Own the session before the native start call can block in USB
            // activation. A device click or window close during that interval
            // must still queue an explicit stop for this exact phone, and the
            // top action changes to its red stop state immediately.
            var state = _deviceSessions.GetValueOrDefault(device.Udid) ?? new DeviceCaptureState
            {
                Udid = device.Udid,
                RenderWidth = SelectedResolutionPreset.Width,
                RenderHeight = SelectedResolutionPreset.Height,
                FrameRate = SelectedFrameRate,
                PlayAudio = PlayAudio,
                Volume = PlaybackVolume,
            };
            _deviceSessions[device.Udid] = state;
            state.IsStarting = true;
            SetCaptureSessionOwner(device.Udid);
            var created = await Task.Run(() => _core.CreateDeviceSession(device.Udid,
                state.RenderWidth, state.RenderHeight, (uint)state.FrameRate,
                state.PlayAudio, state.Volume / 100.0));
            state.IsStarting = false;
            state.Handle = created.Success ? created.Handle : 0;
            var result = (created.Success, created.Message);
            IsCapturing = created.Success;
            _activeCaptureUdid = created.Success ? device.Udid : null;
            SetCaptureSessionOwner(created.Success ? device.Udid : null);
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
            SetCaptureSessionOwner(null);
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
        if (_disposed || IsOperationBusy) return;
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
        if (_disposed || !HasCaptureSession || IsOperationBusy) return;
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
            var handle = state.Handle;
            await Task.Run(() => _core.StopDeviceSession(handle));
            _core.DestroyDeviceSession(handle);
            state.Handle = 0;
            NativeCore.SelectPreviewSession(0);
            OnPropertyChanged(nameof(CurrentSessionHandle));
            SetCaptureSessionOwner(null);
            _activeCaptureUdid = null;
            IsCapturing = false;
            CaptureStatus = LocalizationService.Get("CaptureStopped");
            ResetPreviewState();
            AddUiLog(LocalizationService.Get("StopSessionReleased"));
        }
        catch (Exception error)
        {
            _activeCaptureUdid = null;
            SetCaptureSessionOwner(null);
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
        if (_disposed || !await _logGate.WaitAsync(0)) return;
        try
        {
            if (!File.Exists(_logPath)) return;
            await using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 32 * 1024, useAsync: true);
            if (stream.Length < _logPosition)
            {
                _logPosition = 0;
                _partialLogLine = string.Empty;
                _logDecoder.Reset();
            }
            // On first view show a useful tail instead of replaying an
            // arbitrarily large log accumulated by previous sessions.
            if (_logPosition == 0 && stream.Length > 256 * 1024)
                _logPosition = stream.Length - 256 * 1024;
            if (stream.Length <= _logPosition) return;

            stream.Position = _logPosition;
            var bytesToRead = (int)Math.Min(stream.Length - _logPosition, 256 * 1024);
            var buffer = new byte[bytesToRead];
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) return;
            _logPosition += bytesRead;

            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytesRead)];
            _logDecoder.Convert(buffer, 0, bytesRead, characters, 0, characters.Length,
                flush: false, out _, out var charactersUsed, out _);
            var text = (_partialLogLine + new string(characters, 0, charactersUsed))
                .Replace("\r\n", "\n");
            var lines = text.Split('\n');
            _partialLogLine = lines[^1];
            foreach (var line in lines.Take(lines.Length - 1))
                if (!string.IsNullOrWhiteSpace(line)) AddLogLine(line);
            PublishLogText();
        }
        catch (IOException)
        {
            // The native logger can rotate/flush between length and read.
            // The next timer tick resumes from the last successful offset.
        }
        finally
        {
            _logGate.Release();
        }
    }

    internal void AddUiLog(string message)
    {
        AddLogLine($"{DateTime.Now:HH:mm:ss.fff} [UI] {message}");
        PublishLogText();
    }

    internal ulong GetDeviceSessionHandle(string udid) =>
        _deviceSessions.GetValueOrDefault(udid)?.Handle ?? 0;

    internal async Task<(bool Success, ulong Handle, string Message)> StartBackgroundSessionAsync(
        DeviceViewModel device)
    {
        await _coreGate.WaitAsync();
        try
        {
            if (_deviceSessions.TryGetValue(device.Udid, out var existing) && existing.Handle != 0)
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
            _deviceSessions[device.Udid] = state;
            var result = await Task.Run(() => _core.CreateDeviceSession(device.Udid,
                state.RenderWidth, state.RenderHeight, (uint)state.FrameRate,
                playAudio: false, state.Volume / 100.0));
            state.Handle = result.Success ? result.Handle : 0;
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            {
                IsCapturing = result.Success;
                _activeCaptureUdid = result.Success ? device.Udid : null;
                SetCaptureSessionOwner(_activeCaptureUdid);
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
            if (!_deviceSessions.TryGetValue(udid, out var state) || state.Handle == 0) return;
            var handle = state.Handle;
            await Task.Run(() => _core.StopDeviceSession(handle));
            _core.DestroyDeviceSession(handle);
            state.Handle = 0;
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid))
            {
                NativeCore.SelectPreviewSession(0);
                IsCapturing = false;
                _activeCaptureUdid = null;
                SetCaptureSessionOwner(null);
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
            CaptureStatus = GetCaptureStatusText(status);
        Resolution = status.Width > 0 && status.Height > 0 ? $"{status.Width}×{status.Height}" : "—";
        if (status.Width > 0 && status.Height > 0 &&
            (status.Width != _sourceVideoWidth || status.Height != _sourceVideoHeight))
        {
            _sourceVideoWidth = status.Width;
            _sourceVideoHeight = status.Height;
            OnPropertyChanged(nameof(SourceVideoWidth));
            OnPropertyChanged(nameof(SourceVideoHeight));
        }
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
        var udid = SelectedDevice?.Udid;
        _filterDriverStatus = _filterDriver.Inspect(udid);
        if (_filterDriverStatus.State == IPhoneFilterDriverState.Provisional &&
            !string.IsNullOrWhiteSpace(udid))
        {
            try
            {
                _filterDriverStatus = _filterDriver.Inspect(
                    udid, _core.IsLibUsb0DeviceAvailable(udid));
            }
            catch
            {
                // Keep the conservative Provisional state. Native capture
                // repeats the same exact-serial preflight before activation.
            }
        }
        ApplySelectedDriverState();
        OnPropertyChanged(nameof(DriverInstallVisibility));
        OnPropertyChanged(nameof(DriverInstallButtonText));
        InstallDriverCommand.NotifyCanExecuteChanged();
    }

    private void ApplySelectedDriverState()
    {
        if (SelectedDevice is null) return;
        if (_driverReplug.IsPending(SelectedDevice.Udid))
        {
            DriverState = LocalizationService.Get("DriverReplugRequired");
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
            IPhoneFilterDriverState.PackageMissing => LocalizationService.Get("DriverPackageMissing"),
            IPhoneFilterDriverState.InvalidStack => LocalizationService.Get("DriverInvalidAppleStack"),
            IPhoneFilterDriverState.Error => LocalizationService.Get("DriverFilterStateError"),
            _ => DriverState,
        };
    }

    private async Task InstallSelectedDriverAsync()
    {
        if (_disposed || IsOperationBusy || IsCapturing || SelectedDevice is null) return;
        IsBusy = true;
        try
        {
            await EnsureSelectedDriverReadyAsync(confirmInstall: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureSelectedDriverReadyAsync(bool confirmInstall)
    {
        var device = SelectedDevice;
        if (device is null) return false;
        if (_driverReplug.IsPending(device.Udid))
        {
            CaptureStatus = LocalizationService.Get("DriverReplugRequired");
            AddUiLog(LocalizationService.Get("DriverReplugRequired"));
            return false;
        }
        UpdateSelectedDriverStatus();
        if (_filterDriverStatus.CanStartCapture)
        {
            if (_filterDriverStatus.State == IPhoneFilterDriverState.Provisional)
                AddUiLog("driver preflight is provisional; native libusb0 serial enumeration is authoritative");
            return true;
        }
        if (!_filterDriverStatus.CanInstall)
        {
            CaptureStatus = LocalizationService.Get(_filterDriverStatus.State switch
            {
                IPhoneFilterDriverState.NoDevice => "DriverReconnectPhone",
                IPhoneFilterDriverState.PendingRestart => "DriverReplugRequired",
                IPhoneFilterDriverState.PackageMissing => "DriverPackageMissing",
                IPhoneFilterDriverState.InvalidStack => "DriverInvalidAppleStack",
                _ => "DriverFilterStateError",
            });
            AddUiLog($"driver preflight: {_filterDriverStatus.Diagnostic}");
            return false;
        }

        if (confirmInstall && MessageBox.Show(
                LocalizationService.Format("DriverInstallConfirmFormat", device.DisplayName),
                LocalizationService.Get("DriverInstallTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            AddUiLog(LocalizationService.Get("DriverInstallDeclined"));
            return false;
        }

        CaptureStatus = LocalizationService.Get("DriverInstalling");
        AddUiLog(LocalizationService.Format("DriverInstallStartingFormat", device.Udid));
        var result = await _filterDriver.InstallAsync(device.Udid);
        AddUiLog(LocalizationService.Format("DriverInstallResultFormat", result.Message,
            result.LogPath ?? "-"));
        if (!result.Success)
        {
            CaptureStatus = result.Cancelled
                ? LocalizationService.Get("DriverInstallCancelled")
                : LocalizationService.Format("DriverInstallFailedFormat", result.Message);
            UpdateSelectedDriverStatus();
            return false;
        }

        // Require and observe a physical unplug/replug after every actual
        // installation. A successful SetupAPI restart is not sufficient for
        // every Apple USB stack/phone combination, especially with two phones.
        _driverReplug.MarkInstalled(device.Udid);
        NotifyDriverReplugStateChanged();
        CaptureStatus = LocalizationService.Get("DriverReplugRequired");
        DriverState = LocalizationService.Get("DriverReplugRequired");
        MessageBox.Show(LocalizationService.Get("DriverReplugRequired"),
            LocalizationService.Get("DriverInstallTitle"), MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private static string GetCaptureStatusText(NativeCaptureStatus status) => status.State switch
    {
        CaptureState.Idle => LocalizationService.Get("CaptureIdle"),
        CaptureState.ActivatingUsb => LocalizationService.Get("CaptureActivating"),
        CaptureState.WaitingForDevice => LocalizationService.Get("CaptureWaitingDevice"),
        CaptureState.Handshaking => LocalizationService.Get("CaptureHandshaking"),
        CaptureState.Streaming => LocalizationService.Get("CaptureStreaming"),
        CaptureState.Stopping => LocalizationService.Get("CaptureStopping"),
        CaptureState.Stopped => LocalizationService.Get("CaptureStopped"),
        _ => LocalizationService.Get("CaptureError"),
    };

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

        foreach (var device in Devices) device.NotifyLanguageChanged();

        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedOs));
        OnPropertyChanged(nameof(TargetResolutionDisplay));
        OnPropertyChanged(nameof(TargetFpsDisplay));
        OnPropertyChanged(nameof(DriverInstallButtonText));

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
    }

    private void ShowDriverHelp()
    {
        MessageBox.Show(
            LocalizationService.Get("DriverHelpBody"),
            LocalizationService.Get("DriverHelpTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void RefreshPreview()
    {
        if (!IsCapturing) return;
        var timestamp = _core.GetLatestVideoTimestamp();
        if (timestamp == 0 || timestamp == _lastPreviewTimestamp) return;
        // The preview surface is displayed at roughly 400 px wide for a
        // phone-shaped source. The preview column displays the phone at about
        // 380-400 px wide, so matching that width avoids making WPF upload and
        // composite pixels that are immediately discarded by layout scaling.
        var frame = _core.GetLatestPreviewFrame(400, 2200);
        if (frame is null || frame.Width == 0 || frame.Height == 0 ||
            frame.Timestamp100Ns <= _lastPreviewTimestamp) return;
        _lastPreviewTimestamp = frame.Timestamp100Ns;
        var previewReady = _useInteropPreview
            ? _interopBuffers.Length == 4 && _interopBuffers[0].PixelWidth == (int)frame.Width &&
              _interopBuffers[0].PixelHeight == (int)frame.Height
            : _previewBuffers.Length == 4 && _previewBuffers[0].PixelWidth == (int)frame.Width &&
              _previewBuffers[0].PixelHeight == (int)frame.Height;
        if (!previewReady)
        {
            DisposePreviewBuffers();
            try
            {
                if (!EnableInteropPreview) throw new NotSupportedException();
                var stride = checked((int)frame.Stride);
                var bytes = checked((long)stride * (long)frame.Height);
                _previewMaps = Enumerable.Range(0, 4)
                    .Select(_ => MemoryMappedFile.CreateNew(null, bytes))
                    .ToArray();
                _previewViews = _previewMaps.Select(map => map.CreateViewAccessor(0, bytes,
                    MemoryMappedFileAccess.ReadWrite)).ToArray();
                _interopBuffers = _previewMaps.Select(map =>
                    Imaging.CreateBitmapSourceFromMemorySection(
                        map.SafeMemoryMappedFileHandle.DangerousGetHandle(),
                        (int)frame.Width, (int)frame.Height, PixelFormats.Bgra32,
                        96, 96)).Cast<InteropBitmap>().ToArray();
                _useInteropPreview = true;
            }
            catch
            {
                DisposePreviewBuffers();
                _previewBuffers = Enumerable.Range(0, 4)
                    .Select(_ => new WriteableBitmap((int)frame.Width, (int)frame.Height, 96, 96,
                        PixelFormats.Bgra32, null))
                    .ToArray();
                _useInteropPreview = false;
            }
            _nextPreviewBuffer = 0;
        }

        var nextIndex = _nextPreviewBuffer;
        _nextPreviewBuffer = (_nextPreviewBuffer + 1) % 4;
        ImageSource next;
        if (_useInteropPreview)
        {
            _previewViews[nextIndex].WriteArray(0, frame.Pixels, 0, frame.Pixels.Length);
            _previewViews[nextIndex].Flush();
            _interopBuffers[nextIndex].Invalidate();
            next = _interopBuffers[nextIndex];
        }
        else
        {
            var bitmap = _previewBuffers[nextIndex];
            bitmap.Lock();
            try
            {
                var rowBytes = checked((int)frame.Stride);
                if (bitmap.BackBufferStride == rowBytes)
                {
                    Marshal.Copy(frame.Pixels, 0, bitmap.BackBuffer, frame.Pixels.Length);
                }
                else
                {
                    var sourceOffset = 0;
                    var destination = bitmap.BackBuffer;
                    for (var y = 0U; y < frame.Height; ++y)
                    {
                        Marshal.Copy(frame.Pixels, sourceOffset, destination, rowBytes);
                        sourceOffset += rowBytes;
                        destination += bitmap.BackBufferStride;
                    }
                }
                bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)frame.Width, (int)frame.Height));
            }
            finally
            {
                bitmap.Unlock();
            }
            next = bitmap;
        }
        PreviewSource = next;
        ++_renderedFrames;
        var nowTicks = _renderClock.ElapsedTicks;
        if (_lastFpsUpdateTicks == 0 || nowTicks - _lastFpsUpdateTicks >= Stopwatch.Frequency / 2) {
            var seconds = _renderClock.Elapsed.TotalSeconds;
            if (seconds > 0.0) FpsDisplay = $"{_renderedFrames / seconds:F1} fps";
            _lastFpsUpdateTicks = nowTicks;
        }
    }

    internal async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        DisposePreviewBuffers();
        await _coreGate.WaitAsync();
        await _logGate.WaitAsync();
        try
        {
            await _shutdownCoordinator.StopAndDisposeOnceAsync(
                async () =>
                {
                    try
                    {
                        foreach (var session in _deviceSessions.Values.Where(value => value.Handle != 0).ToArray())
                        {
                            var handle = session.Handle;
                            AddUiLog($"application shutdown stopping device session: udid={session.Udid} handle={handle}");
                            try { await Task.Run(() => _core.StopDeviceSession(handle)); }
                            finally
                            {
                                _core.DestroyDeviceSession(handle);
                                session.Handle = 0;
                            }
                        }
                        // Defensive cleanup for a legacy session created by an
                        // older component in the same process.
                        await Task.Run(_core.StopCapture);
                    }
                    finally
                    {
                        SetCaptureSessionOwner(null);
                        _activeCaptureUdid = null;
                        IsCapturing = false;
                    }
                },
                () => Task.Run(_core.Dispose));
        }
        finally
        {
            _logGate.Release();
            _coreGate.Release();
            _logGate.Dispose();
            _coreGate.Dispose();
        }
    }

    private void DisposePreviewBuffers()
    {
        foreach (var view in _previewViews) view.Dispose();
        foreach (var map in _previewMaps) map.Dispose();
        _previewViews = [];
        _previewMaps = [];
        _interopBuffers = [];
        _previewBuffers = [];
        _useInteropPreview = false;
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
