using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Microsoft.Win32;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App;

// Build marker: GUI hosts the native D3D11 swapchain; decoded presentation
// frames no longer pass through WPF WriteableBitmap or CompositionTarget.
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private enum LeftWorkspacePanel
    {
        None,
        Mirroring,
        Devices,
    }

    public string VersionText => $"iPhoneMirror {VersionManager.DisplayVersion}";

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _mediaCastTimer;
    private readonly DispatcherTimer _mediaPlaybackTimer;
    private readonly DispatcherTimer _mediaControlsHideTimer;
    private readonly DispatcherTimer _mediaOpeningTimer;
    private readonly MultiDevicePreviewManager _secondaryMirrors;
    private DeveloperToolsWindow? _developerToolsWindow;
    private DeviceBindingWindow? _reverseControlWindow;
    private readonly HashSet<string> _deviceProfileGuidanceShown =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _screenshotGate = new(1, 1);
    private readonly MediaCastEventGate _mediaCastEvents = new();
    private bool _isFullScreen;
    private bool _isWindowMaximized;
    private bool _handlingNativeMaximize;
    private bool _restoreWasWindowMaximized;
    private Rect _windowMaximizeRestoreBounds;
    private WindowStyle _restoreWindowStyle;
    private WindowState _restoreWindowState;
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;
    private Rect _restoreBounds;
    private bool _shutdownStarted;
    private bool _allowClose;
    private int _versionClickCount;
    private DateTime _lastVersionClickUtc;
    private DeviceViewModel? _pressedDevice;
    private Point _devicePressPoint;
    private DateTime _devicePressStartedUtc;
    private bool _deviceDragStarted;
    private int _previewTransitionRevision;
    private bool _usbTouchPressed;
    private (double X, double Y) _lastUsbTouchPosition = (0.5, 0.5);
    private ulong _mediaCommandId;
    private double _mediaStartPosition;
    private bool _mediaPlaying;
    private bool _mediaShouldPlay;
    private double _mediaPlaybackSpeed = 1.0;
    private bool _mediaSpeedFallbackPending;
    private bool _mediaSpeedFallbackPromptShown;
    private DateTime _mediaSpeedChangedUtc;
    private bool _mediaOpened;
    private bool _mediaStopped = true;
    private bool _mediaCastActive;
    private bool _mediaIsLive;
    private bool _mediaUsesHlsBridge;
    // The HLS bridge exposes a fresh local stream after every restart. Keep
    // its programme-time origin separate from MediaElement.Position, which is
    // always relative to that local stream.
    private double _mediaProgramDuration;
    private double _mediaBridgeOffset;
    // The visible/controller timeline is a programme clock, not the current
    // MediaElement timestamp. HLS bridge replacement can reset or jump the
    // latter; a wall-clock anchor keeps progress proportional and monotonic.
    private double _mediaTimelineAnchorPosition;
    private DateTime _mediaTimelineAnchorUtc;
    private bool _mediaTimelineRunning;
    private double _lastRejectedMediaPosition;
    private DateTime _mediaProgressSampleUtc;
    private bool _mediaBuffering;
    private bool _mediaWaitingForFirstFrame;
    private bool _mediaSeekInteraction;
    private bool _mediaSeekCommitPending;
    private bool _mediaSeekTrackInteraction;
    private double _mediaSeekInteractionTarget;
    private bool _mediaSeekLoading;
    private double _lastSeekSliderSyncPosition = double.NaN;
    // Keep the last usable VOD timeline across the short interval where WMF
    // exposes no NaturalDuration while an HLS element is being replaced.
    private double _mediaLastTimelineDuration;
    private double _mediaLastTimelinePosition;
    // A HLS seek replaces the local MediaElement. Keep the programme-time
    // target visible while that replacement is still opening.
    private double? _mediaPendingHlsSeekPosition;
    private DateTime _mediaPendingHlsSeekStartedUtc;
    private double? _mediaPendingSeekPosition;
    private DateTime _mediaPendingSeekStartedUtc;
    private DateTime _mediaPendingSeekLastAttemptUtc;
    private int _mediaPendingSeekAttemptCount;
    private bool _updatingMediaCastControls;
    private bool _mediaControlsVisible = true;
    private double _mediaOpeningPosition;
    private DateTime _mediaOpenedAtUtc;
    private int _mediaRecoveryRevision;
    // HLS VOD manifests can be exposed by WMF one segment at a time. A
    // segment is often shorter than the normal 10-second stability window,
    // so use a shorter window to reset transient-recovery attempts after the
    // stream has made real progress instead of exhausting the budget during
    // an otherwise healthy long programme.
    private readonly MediaRecoveryBackoff _mediaRecoveryBackoff = new(
        stablePlaybackWindow: TimeSpan.FromSeconds(3));
    private CancellationTokenSource _mediaRecoveryCancellation = new();
    private Uri? _mediaSource;
    private Uri? _mediaPlaybackSource;
    private HlsMediaPlaybackBridge? _mediaHlsBridge;
    private readonly MediaCastAudioDecoder _mediaCastAudioDecoder = new();
    private NativePreviewWindow? _mediaCastPreviewWindow;
    private ProjectionSettingsWindow? _projectionSettingsWindow;
    private ShortcutSettingsWindow? _shortcutSettingsWindow;
    private ProtectedContentNoticeWindow? _protectedContentNoticeWindow;
    private string? _protectedContentNoticeUdid;
    private MediaOutputSettingsWindow? _mediaOutputSettingsWindow;
    private string? _projectionSettingsUdid;
    private ulong _projectionSettingsSessionHandle;
    private string? _lastPlaybackReportError;
    private LeftWorkspacePanel _leftWorkspacePanel = LeftWorkspacePanel.Devices;
    private bool _isSettingsPanelVisible;
    private bool _isSynchronizingWorkspacePanelControls;
    private bool _workspaceControlsReady;
    private bool _themeControlReady;
    private int _workspaceTransitionRevision;
    private bool _lightweightModeApplied;
    private bool _lightweightPreviewWidthQueued;
    private bool _lightweightInitialWorkspaceFitQueued;
    private bool _lightweightWidthNeedsFit;
    private bool _lightweightWindowRendering;
    private TimeSpan _lightweightWindowLastRenderTime;
    private TimeSpan _lightweightWindowLastAppliedTime;
    private double _lightweightWindowAnimationProgress;
    private double _lightweightWindowLastAppliedProgress;
    private int _lightweightWindowWidthStartPixels;
    private int _lightweightWindowWidthTargetPixels;
    private int _lightweightWindowLastAppliedWidthPixels;
    private int _lightweightWindowHeightPixels;
    private double _lightweightWindowWidthTargetDips;
    private int _lightweightWindowStartX;
    private int _lightweightWindowTargetX;
    private int _lightweightWindowLastAppliedX;
    private int _lightweightWindowTopPixels;
    private bool _lightweightCenterWidthLocked;
    private bool _lightweightWorkspaceSurfaceAnimationActive;
    private double _lightweightLeftSurfaceStartWidth;
    private double _lightweightLeftSurfaceTargetWidth;
    private double _lightweightRightSurfaceStartWidth;
    private double _lightweightRightSurfaceTargetWidth;
    private double _lightweightLeftGapStartWidth;
    private double _lightweightLeftGapTargetWidth;
    private double _lightweightRightGapStartWidth;
    private double _lightweightRightGapTargetWidth;
    private double _lightweightCenterStartWidth;
    private double _lightweightCenterTargetWidth;
    private double _lightweightTargetMinWidth;
    private double _completeModeWidth;
    private long _mediaCastOutputTimestamp;
    private HwndSource? _windowSource;
    private int _lastControlSourceX;
    private int _lastControlSourceY;
    private uint _lastControlGeometryWidth;
    private uint _lastControlGeometryHeight;
    private int _lastControlGeometryRotation;
    private bool _controlPointerInitialized;
    private readonly Timer _controlPointerTimer;
    private readonly SemaphoreSlim _bluetoothRouteGate = new(1, 1);
    private int _bluetoothRouteChanging;
    private readonly object _controlQueueSync = new();
    private int _pendingControlDx;
    private int _pendingControlDy;
    private int _pendingControlWheel;
    private double _controlWheelRemainder;
    private int _lastWheelResolutionMultiplier = 1;
    private byte _pendingControlButtons;
    private bool _pendingControlStateDirty;
    private long _pendingControlMotionAt;
    private int _controlPointerFlushInFlight;
    private int _controlPointerTimerArmed;
    private byte _controlButtons;
    private double _controlRemainderX;
    private double _controlRemainderY;
    private readonly HashSet<byte> _controlKeyboardUsages = [];
    private readonly HashSet<int> _controlModifierKeys = [];
    private byte _controlKeyboardModifiers;
    private bool _windowsCursorHidden;
    private nint _activeControlWindow;
    private string? _activeControlUdid;
    private bool _rawMouseInputEnabled;
    private bool _rawKeyboardInputEnabled;
    private nint _rawInputBuffer;
    private int _rawInputBufferSize;
    private bool _hotKeyRegistered;
    private readonly Dictionary<BluetoothShortcutAction, KeyboardShortcut> _bluetoothShortcuts = [];
    private readonly HashSet<int> _registeredHotKeyIds = [];
    private bool _bossKeyHidden;
    private int _bossKeyChanging;
    private nint _keyboardHook;
    private readonly LowLevelKeyboardProc _keyboardHookProc;

    private const int WmInput = 0x00FF;
    private const int WmHotKey = 0x0312;
    private const int WmSetCursor = 0x0020;
    private const int WmActivateApp = 0x001C;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmCancelMode = 0x001F;
    private const int WmCaptureChanged = 0x0215;
    private const int BluetoothModeHotKeyId = 0x4991;
    private const int WirelessModeHotKeyId = 0x4992;
    private const int WiredModeHotKeyId = 0x4993;
    private const int BluetoothControlCenterHotKeyId = 0x4982;
    private const int BluetoothNotificationCenterHotKeyId = 0x4983;
    private const int BluetoothAppSwitcherHotKeyId = 0x4984;
    private const int BluetoothHomeHotKeyId = 0x4985;
    private const int BluetoothBossKeyHotKeyId = 0x4987;
    private const int BluetoothDockHotKeyId = 0x4988;
    private const int BluetoothSiriHotKeyId = 0x4990;
    private const int BluetoothVolumeUpHotKeyId = 0x4989;
    private const int BluetoothVolumeDownHotKeyId = 0x498A;
    private const int BluetoothLockScreenHotKeyId = 0x498B;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevNoLegacy = 0x00000030;
    private const uint RidevRemove = 0x00000001;
    private const ushort RawMouseLeftDown = 0x0001;
    private const ushort RawMouseLeftUp = 0x0002;
    private const ushort RawMouseRightDown = 0x0004;
    private const ushort RawMouseRightUp = 0x0008;
    private const ushort RawMouseMiddleDown = 0x0010;
    private const ushort RawMouseMiddleUp = 0x0020;
    private const ushort RawMouseWheel = 0x0400;
    private const uint CursorShowing = 0x00000001;
    private const double LightweightDefaultPreviewAspect = 1206d / 2622d;
    private const double LightweightMinimumPreviewWidth = 320;
    private const double LightweightNormalPortraitPreviewWidth = 340;
    private const double LightweightMinimumWindowWidth = 640;
    private const double LightweightWorkAreaInset = 24;

    private bool IsBluetoothControlActive => IsBluetoothControlActiveFor(
        _activeControlWindow != 0 ? _activeControlUdid : _viewModel.SelectedDevice?.Udid);

    private bool IsBluetoothControlActiveFor(string? udid)
    {
        if (_bossKeyHidden || !_viewModel.BluetoothControlIsInputEnabled ||
            string.IsNullOrWhiteSpace(udid) ||
            !_viewModel.IsBluetoothControlTarget(udid)) return false;
        return _activeControlWindow != 0
            ? DeviceViewModel.UdidEquals(_activeControlUdid, udid)
            : DeviceViewModel.UdidEquals(_viewModel.SelectedDevice?.Udid, udid);
    }

    private bool IsUsbControlActive => _viewModel.UsbControlIsInputEnabled &&
        _activeControlWindow == 0 &&
        _viewModel.IsUsbControlTarget(_viewModel.SelectedDevice?.Udid);

    private static readonly TimeSpan DeviceDragHoldDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AppSwitcherDoublePressInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WorkspaceTransitionDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan LightweightWorkspaceTransitionDuration =
        TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan LightweightWindowFrameInterval =
        TimeSpan.FromMilliseconds(1000d / 60d);
    public MainWindow()
    {
        _keyboardHookProc = KeyboardHookProcedure;
        InitializeComponent();
        // Slider handles direct track clicks at the class-handler level and can
        // mark the mouse event handled before an ordinary XAML handler sees it.
        // Observe handled events so every click/drag has one complete seek
        // transaction and cannot be overwritten by the playback timer.
        MediaCastSeekSlider.AddHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnMediaCastSeekPointerDown),
            handledEventsToo: true);
        MediaCastSeekSlider.AddHandler(Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnMediaCastSeekPointerUp),
            handledEventsToo: true);
        MediaCastSeekSlider.AddHandler(Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnMediaCastSeekPointerMove),
            handledEventsToo: true);
        MediaCastSeekSlider.AddHandler(Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnMediaCastSeekLostCapture),
            handledEventsToo: true);
        MediaCastSeekSlider.AddHandler(Keyboard.KeyUpEvent,
            new KeyEventHandler(OnMediaCastSeekKeyUp),
            handledEventsToo: true);
        if (Application.Current is App app)
        {
            ThemeComboBox.SelectedValue = app.UpdateSettings.Theme.ToString();
            foreach (var action in Enum.GetValues<BluetoothShortcutAction>())
                _bluetoothShortcuts[action] = KeyboardShortcut.FromSettings(
                    app.UpdateSettings, action);
        }
        _themeControlReady = true;
        _workspaceControlsReady = true;
        _viewModel = new MainViewModel();
        MainPreviewHost.PointerInput += OnControlPointerInput;
        MainPreviewHost.KeyboardInput += OnControlKeyboardInput;
        _viewModel.SetMediaCastOutputProviders(
            CaptureMediaCastNv12Frame, CaptureMediaCastVideoFrame,
            afterSequence => _mediaCastAudioDecoder.GetPacket(afterSequence));
        _secondaryMirrors = new MultiDevicePreviewManager(_viewModel,
            () => _hotKeyRegistered,
            (udid, window) => _activeControlWindow == window &&
                (IsBluetoothControlActiveFor(udid) ||
                 (_viewModel.UsbControlIsInputEnabled &&
                  _viewModel.IsUsbControlTarget(udid))));
        _secondaryMirrors.ReverseControlRequested += OnIndependentReverseControlRequested;
        _secondaryMirrors.UsbControlRequested += OnIndependentUsbControlRequested;
        _secondaryMirrors.WirelessControlRequested += OnIndependentWirelessControlRequested;
        _secondaryMirrors.PreviewClosed += OnIndependentPreviewClosed;
        _secondaryMirrors.PointerInput += OnIndependentPointerInput;
        _secondaryMirrors.KeyboardInput += OnIndependentKeyboardInput;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _viewModel.DeviceVideoSizeChanged += OnDeviceVideoSizeChanged;
        _viewModel.DeviceSessionHandleChanged += OnDeviceSessionHandleChanged;
        _viewModel.DeviceProtectionStateChanged += OnDeviceProtectionStateChanged;
        _viewModel.MediaCastCommandReceived += OnMediaCastCommandReceived;
        _viewModel.MediaCastStopRequested += OnMediaCastStopRequested;
        _viewModel.MediaCastAudioSettingsChanged += OnMediaCastAudioSettingsChanged;
        _viewModel.ProjectionSettingsRequested += OnProjectionSettingsRequested;
        _viewModel.MediaOutputSettingsRequested += OnMediaOutputSettingsRequested;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => _ = _viewModel.RefreshAsync();
        _mediaCastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaCastTimer.Tick += (_, _) => _viewModel.RefreshMediaCast();
        _mediaPlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaOpeningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaOpeningTimer.Tick += OnMediaOpeningTimerTick;
        _mediaPlaybackTimer.Tick += OnMediaPlaybackTimerTick;
        _mediaControlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.6),
        };
        _mediaControlsHideTimer.Tick += OnMediaControlsHideTimerTick;
        _controlPointerTimer = new Timer(_ => _ = FlushControlPointerAsync(),
            null, Timeout.Infinite, Timeout.Infinite);
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        Closing += OnClosing;
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_created",
            ("thread", Environment.CurrentManagedThreadId),
            ("dpi", PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 0)));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(WindowMessageHook);
        RegisterBluetoothControlHotkey();
    }

    private void RegisterBluetoothControlHotkey()
    {
        if (_windowSource is null) return;
        if (!TryRegisterShortcutSet(GetConfiguredShortcuts(), out var failedAction))
            _viewModel.AddDiagnosticLog(AppLog.Event("bluetooth_hotkey_register_failed",
                ("shortcut", GetConfiguredShortcuts()[failedAction].DisplayText),
                ("error", Marshal.GetLastWin32Error())));
    }

    private IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut>
        GetConfiguredShortcuts()
    {
        if (_bluetoothShortcuts.Count == 0)
            foreach (var action in Enum.GetValues<BluetoothShortcutAction>())
                _bluetoothShortcuts[action] = KeyboardShortcut.DefaultFor(action);
        return new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(_bluetoothShortcuts);
    }

    private static int HotKeyId(BluetoothShortcutAction action) => action switch
    {
        BluetoothShortcutAction.BluetoothControl => BluetoothModeHotKeyId,
        BluetoothShortcutAction.WirelessControl => WirelessModeHotKeyId,
        BluetoothShortcutAction.WiredControl => WiredModeHotKeyId,
        BluetoothShortcutAction.ControlCenter => BluetoothControlCenterHotKeyId,
        BluetoothShortcutAction.NotificationCenter => BluetoothNotificationCenterHotKeyId,
        BluetoothShortcutAction.AppSwitcher => BluetoothAppSwitcherHotKeyId,
        BluetoothShortcutAction.Home => BluetoothHomeHotKeyId,
        BluetoothShortcutAction.BossKey => BluetoothBossKeyHotKeyId,
        BluetoothShortcutAction.Dock => BluetoothDockHotKeyId,
        BluetoothShortcutAction.Siri => BluetoothSiriHotKeyId,
        BluetoothShortcutAction.VolumeUp => BluetoothVolumeUpHotKeyId,
        BluetoothShortcutAction.VolumeDown => BluetoothVolumeDownHotKeyId,
        BluetoothShortcutAction.LockScreen => BluetoothLockScreenHotKeyId,
        _ => 0,
    };

    private void UnregisterConfiguredHotkeys()
    {
        if (_windowSource is not null)
            foreach (var action in Enum.GetValues<BluetoothShortcutAction>())
                UnregisterHotKey(_windowSource.Handle, HotKeyId(action));
        _registeredHotKeyIds.Clear();
        _hotKeyRegistered = false;
    }

    private bool TryRegisterShortcutSet(
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> shortcuts,
        out BluetoothShortcutAction failedAction)
    {
        failedAction = BluetoothShortcutAction.BluetoothControl;
        if (!KeyboardShortcut.HaveUniqueBoundValues(shortcuts.Values))
            return false;
        UnregisterConfiguredHotkeys();
        if (_windowSource is null) return true;
        foreach (var action in Enum.GetValues<BluetoothShortcutAction>().Where(
            action => action != BluetoothShortcutAction.ReverseControl))
        {
            failedAction = action;
            var shortcut = shortcuts[action];
            if (!shortcut.IsBound || shortcut.VirtualKey is KeyboardShortcut.MouseRight or
                KeyboardShortcut.MouseMiddle) continue;
            if (!RegisterHotKey(_windowSource.Handle, HotKeyId(action),
                    shortcut.RegistrationModifiers, shortcut.VirtualKey))
            {
                UnregisterConfiguredHotkeys();
                return false;
            }
            _registeredHotKeyIds.Add(HotKeyId(action));
        }
        _hotKeyRegistered = _registeredHotKeyIds.Count > 0;
        return true;
    }

    private void ShowShortcutSettings()
    {
        if (_shortcutSettingsWindow is not null)
        {
            _shortcutSettingsWindow.Activate();
            _shortcutSettingsWindow.Focus();
            return;
        }

        var window = new ShortcutSettingsWindow(GetConfiguredShortcuts(),
            ApplyBluetoothShortcuts)
        {
            Owner = this,
        };
        _shortcutSettingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shortcutSettingsWindow, window))
                _shortcutSettingsWindow = null;
        };
        window.Show();
    }

    private string? ApplyBluetoothShortcuts(
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> shortcuts)
    {
        var previous = GetConfiguredShortcuts();
        if (!TryRegisterShortcutSet(shortcuts, out var failedAction))
        {
            _ = TryRegisterShortcutSet(previous, out _);
            return LocalizationService.Format("ShortcutRegistrationFailedFormat",
                shortcuts.TryGetValue(failedAction, out var failed)
                    ? failed.DisplayText : failedAction.ToString());
        }
        if (Application.Current is App app)
        {
            var snapshot = app.UpdateSettings.Clone();
            app.UpdateSettings.BluetoothModeShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.BluetoothControl].VirtualKey;
            app.UpdateSettings.BluetoothModeShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.BluetoothControl].Modifiers;
            app.UpdateSettings.WirelessModeShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.WirelessControl].VirtualKey;
            app.UpdateSettings.WirelessModeShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.WirelessControl].Modifiers;
            app.UpdateSettings.WiredModeShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.WiredControl].VirtualKey;
            app.UpdateSettings.WiredModeShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.WiredControl].Modifiers;
            app.UpdateSettings.BluetoothControlCenterShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.ControlCenter].VirtualKey;
            app.UpdateSettings.BluetoothControlCenterShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.ControlCenter].Modifiers;
            app.UpdateSettings.BluetoothNotificationCenterShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.NotificationCenter].VirtualKey;
            app.UpdateSettings.BluetoothNotificationCenterShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.NotificationCenter].Modifiers;
            app.UpdateSettings.BluetoothAppSwitcherShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.AppSwitcher].VirtualKey;
            app.UpdateSettings.BluetoothAppSwitcherShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.AppSwitcher].Modifiers;
            app.UpdateSettings.BluetoothHomeShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.Home].VirtualKey;
            app.UpdateSettings.BluetoothHomeShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.Home].Modifiers;
            app.UpdateSettings.BluetoothBossShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.BossKey].VirtualKey;
            app.UpdateSettings.BluetoothBossShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.BossKey].Modifiers;
            app.UpdateSettings.BluetoothDockShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.Dock].VirtualKey;
            app.UpdateSettings.BluetoothDockShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.Dock].Modifiers;
            app.UpdateSettings.BluetoothSiriShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.Siri].VirtualKey;
            app.UpdateSettings.BluetoothSiriShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.Siri].Modifiers;
            app.UpdateSettings.BluetoothVolumeUpShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.VolumeUp].VirtualKey;
            app.UpdateSettings.BluetoothVolumeUpShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.VolumeUp].Modifiers;
            app.UpdateSettings.BluetoothVolumeDownShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.VolumeDown].VirtualKey;
            app.UpdateSettings.BluetoothVolumeDownShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.VolumeDown].Modifiers;
            app.UpdateSettings.BluetoothLockScreenShortcutVirtualKey = (int)shortcuts[BluetoothShortcutAction.LockScreen].VirtualKey;
            app.UpdateSettings.BluetoothLockScreenShortcutModifiers = (int)shortcuts[BluetoothShortcutAction.LockScreen].Modifiers;
            app.UpdateSettings.BluetoothShortcutSchema = 5;
            if (!app.SaveUpdateSettings())
            {
                app.UpdateSettings.BluetoothControlShortcutVirtualKey = snapshot.BluetoothControlShortcutVirtualKey;
                app.UpdateSettings.BluetoothControlShortcutModifiers = snapshot.BluetoothControlShortcutModifiers;
                app.UpdateSettings.BluetoothControlShortcutSchema = snapshot.BluetoothControlShortcutSchema;
                app.UpdateSettings.BluetoothControlCenterShortcutVirtualKey = snapshot.BluetoothControlCenterShortcutVirtualKey;
                app.UpdateSettings.BluetoothControlCenterShortcutModifiers = snapshot.BluetoothControlCenterShortcutModifiers;
                app.UpdateSettings.BluetoothNotificationCenterShortcutVirtualKey = snapshot.BluetoothNotificationCenterShortcutVirtualKey;
                app.UpdateSettings.BluetoothNotificationCenterShortcutModifiers = snapshot.BluetoothNotificationCenterShortcutModifiers;
                app.UpdateSettings.BluetoothAppSwitcherShortcutVirtualKey = snapshot.BluetoothAppSwitcherShortcutVirtualKey;
                app.UpdateSettings.BluetoothAppSwitcherShortcutModifiers = snapshot.BluetoothAppSwitcherShortcutModifiers;
                app.UpdateSettings.BluetoothHomeShortcutVirtualKey = snapshot.BluetoothHomeShortcutVirtualKey;
                app.UpdateSettings.BluetoothHomeShortcutModifiers = snapshot.BluetoothHomeShortcutModifiers;
                app.UpdateSettings.BluetoothBossShortcutVirtualKey = snapshot.BluetoothBossShortcutVirtualKey;
                app.UpdateSettings.BluetoothBossShortcutModifiers = snapshot.BluetoothBossShortcutModifiers;
                app.UpdateSettings.BluetoothDockShortcutVirtualKey = snapshot.BluetoothDockShortcutVirtualKey;
                app.UpdateSettings.BluetoothDockShortcutModifiers = snapshot.BluetoothDockShortcutModifiers;
                app.UpdateSettings.BluetoothSiriShortcutVirtualKey = snapshot.BluetoothSiriShortcutVirtualKey;
                app.UpdateSettings.BluetoothSiriShortcutModifiers = snapshot.BluetoothSiriShortcutModifiers;
                app.UpdateSettings.BluetoothShortcutSchema = snapshot.BluetoothShortcutSchema;
                _ = TryRegisterShortcutSet(previous, out _);
                return LocalizationService.Get("ShortcutSettingsSaveFailed");
            }
        }

        _bluetoothShortcuts.Clear();
        foreach (var pair in shortcuts) _bluetoothShortcuts[pair.Key] = pair.Value;
        BluetoothControlNoticeWindow.NotifyShortcutChanged();
        _viewModel.AddDiagnosticLog(AppLog.Event("bluetooth_hotkey_updated"));
        return null;
    }

    private void OnControlPointerInput(object? sender,
        Controls.PreviewPointerEventArgs e)
    {
        if (_activeControlWindow != 0) return;
        if (e.Kind == Controls.PreviewPointerKind.ButtonDown &&
            TryHandleMouseShortcut(e.Button))
        {
            return;
        }
        if (IsUsbControlActive)
        {
            _ = HandleUsbPointerInputAsync(e, _viewModel.SelectedDevice?.Udid);
            return;
        }
        HandleControlPointerInput(e, _viewModel.SelectedDevice?.Udid);
    }

    private bool TryHandleMouseShortcut(byte button)
    {
        var shortcutButton = button switch
        {
            2 => ShortcutMouseButton.Right,
            4 => ShortcutMouseButton.Middle,
            _ => ShortcutMouseButton.None,
        };
        if (shortcutButton == ShortcutMouseButton.None) return false;
        var modifiers = Keyboard.Modifiers;
        var match = _bluetoothShortcuts.FirstOrDefault(pair =>
            pair.Key != BluetoothShortcutAction.ReverseControl &&
            pair.Value.MatchesMouse(shortcutButton, modifiers));
        if (match.Key == default) return false;
        HandleConfiguredShortcut(match.Key);
        return true;
    }

    private async Task HandleUsbPointerInputAsync(Controls.PreviewPointerEventArgs e,
        string? sourceUdid)
    {
        var usbTargetActive = _viewModel.UsbControlIsInputEnabled &&
            _viewModel.IsUsbControlTarget(sourceUdid);
        if ((!IsUsbControlActive && !usbTargetActive) || string.IsNullOrWhiteSpace(sourceUdid)) return;
        var sourceWidth = e.SourceWidth != 0 ? e.SourceWidth : _viewModel.SourceVideoWidth;
        var sourceHeight = e.SourceHeight != 0 ? e.SourceHeight : _viewModel.SourceVideoHeight;
        if (e.Kind == Controls.PreviewPointerKind.Reset)
        {
            if (_usbTouchPressed)
                await _viewModel.SendUsbTouchAsync("up", _lastUsbTouchPosition.X, _lastUsbTouchPosition.Y);
            _usbTouchPressed = false;
            return;
        }
        if (e.Kind is not (Controls.PreviewPointerKind.Move or
            Controls.PreviewPointerKind.ButtonDown or Controls.PreviewPointerKind.ButtonUp)) return;
        var mapped = MapPointerToNormalized(e, sourceWidth, sourceHeight);
        if (mapped is null) return;
        var position = BluetoothMouseOrientationMapper.MapNormalized(
            mapped.Value.X, mapped.Value.Y, sourceWidth, sourceHeight, e.Rotation,
            _viewModel.AppliedBluetoothPortraitMouseDirection,
            _viewModel.AppliedBluetoothLandscapeMouseDirection,
            _viewModel.AppliedBluetoothMouseReverseHorizontal,
            _viewModel.AppliedBluetoothMouseReverseVertical);
        if (e.Kind == Controls.PreviewPointerKind.ButtonDown)
            _usbTouchPressed = true;
        else if (e.Kind == Controls.PreviewPointerKind.Move && !_usbTouchPressed)
            return;
        var action = e.Kind == Controls.PreviewPointerKind.ButtonDown ? "down" :
            e.Kind == Controls.PreviewPointerKind.ButtonUp ? "up" : "move";
        await _viewModel.SendUsbTouchAsync(action, position.X, position.Y);
        _lastUsbTouchPosition = position;
        if (action == "up") _usbTouchPressed = false;
    }

    private static (double X, double Y)? MapPointerToNormalized(
        Controls.PreviewPointerEventArgs e, uint sourceWidth, uint sourceHeight)
    {
        if (e.SurfaceWidth <= 0 || e.SurfaceHeight <= 0 || sourceWidth == 0 || sourceHeight == 0)
            return null;
        var sourceAspect = (double)sourceWidth / sourceHeight;
        var surfaceAspect = (double)e.SurfaceWidth / e.SurfaceHeight;
        double imageX = 0, imageY = 0, imageWidth = e.SurfaceWidth, imageHeight = e.SurfaceHeight;
        if (surfaceAspect > sourceAspect)
        {
            imageWidth = e.SurfaceHeight * sourceAspect;
            imageX = (e.SurfaceWidth - imageWidth) / 2;
        }
        else if (surfaceAspect < sourceAspect)
        {
            imageHeight = e.SurfaceWidth / sourceAspect;
            imageY = (e.SurfaceHeight - imageHeight) / 2;
        }
        if (e.X < imageX || e.X >= imageX + imageWidth ||
            e.Y < imageY || e.Y >= imageY + imageHeight)
            return null;
        return (
            Math.Clamp((e.X - imageX) / imageWidth, 0, 1),
            Math.Clamp((e.Y - imageY) / imageHeight, 0, 1));
    }

    private void HandleControlPointerInput(Controls.PreviewPointerEventArgs e,
        string? sourceUdid = null)
    {
        if (Volatile.Read(ref _bluetoothRouteChanging) != 0) return;
        if (!IsBluetoothControlActiveFor(sourceUdid ??
            _viewModel.SelectedDevice?.Udid)) return;
        if (_rawMouseInputEnabled && e.Kind == Controls.PreviewPointerKind.Move)
            return;
        if (e.Kind == Controls.PreviewPointerKind.Move)
        {
            var sourceWidth = e.SourceWidth != 0 ? e.SourceWidth : _viewModel.SourceVideoWidth;
            var sourceHeight = e.SourceHeight != 0 ? e.SourceHeight : _viewModel.SourceVideoHeight;
            var geometryChanged = sourceWidth != _lastControlGeometryWidth ||
                sourceHeight != _lastControlGeometryHeight ||
                e.Rotation != _lastControlGeometryRotation;
            _lastControlGeometryWidth = sourceWidth;
            _lastControlGeometryHeight = sourceHeight;
            _lastControlGeometryRotation = e.Rotation;
            var mapped = MapPointerToSource(e,
                sourceWidth, sourceHeight);
            if (geometryChanged && _controlPointerInitialized)
            {
                // A rotation or source-size change invalidates the previous
                // absolute coordinate. Re-anchor without emitting a jump.
                _lastControlSourceX = mapped.X;
                _lastControlSourceY = mapped.Y;
                _controlRemainderX = 0;
                _controlRemainderY = 0;
                return;
            }
            if (!_controlPointerInitialized)
            {
                _lastControlSourceX = 0;
                _lastControlSourceY = 0;
                _controlPointerInitialized = true;
            }
            var dx = (double)(mapped.X - _lastControlSourceX);
            var dy = (double)(mapped.Y - _lastControlSourceY);
            _lastControlSourceX = mapped.X;
            _lastControlSourceY = mapped.Y;
            var sensitivity = PointerSensitivity(
                sourceWidth, sourceHeight) *
                (_viewModel.AppliedBluetoothMouseSensitivity / 100.0);
            var oriented = MapMouseDeltaToDeviceOrientation(dx, dy,
                sourceWidth, sourceHeight,
                e.Rotation,
                _viewModel.AppliedBluetoothPortraitMouseDirection,
                _viewModel.AppliedBluetoothLandscapeMouseDirection,
                _viewModel.AppliedBluetoothMouseReverseHorizontal,
                _viewModel.AppliedBluetoothMouseReverseVertical);
            dx = oriented.X;
            dy = oriented.Y;
            var scaledX = dx * sensitivity + _controlRemainderX;
            var scaledY = dy * sensitivity + _controlRemainderY;
            var sendX = (int)Math.Truncate(scaledX);
            var sendY = (int)Math.Truncate(scaledY);
            _controlRemainderX = scaledX - sendX;
            _controlRemainderY = scaledY - sendY;
            if (sendX != 0 || sendY != 0)
            {
                lock (_controlQueueSync)
                {
                    _pendingControlDx = Math.Clamp(_pendingControlDx + sendX,
                        -32767, 32767);
                    _pendingControlDy = Math.Clamp(_pendingControlDy + sendY,
                        -32767, 32767);
                    _pendingControlButtons = _controlButtons;
                    // Keep the timestamp of the first unsent movement. Updating
                    // it for every packet disguises a multi-second route/UI
                    // stall as fresh input and releases the entire old burst.
                    if (_pendingControlMotionAt == 0)
                        _pendingControlMotionAt = Stopwatch.GetTimestamp();
                }
                StartControlPointerTimer();
            }
            return;
        }
        if (e.Kind == Controls.PreviewPointerKind.Reset)
        {
            _controlButtons = 0;
            _controlWheelRemainder = 0;
            lock (_controlQueueSync)
            {
                _pendingControlButtons = 0;
                _pendingControlStateDirty = true;
            }
            StartControlPointerTimer();
            _ = FlushControlPointerAsync(force: true);
            return;
        }
        if (e.Kind == Controls.PreviewPointerKind.Wheel)
        {
            if (e.Wheel != 0)
            {
                var multiplier = Math.Clamp(
                    _viewModel.BluetoothWheelResolutionMultiplier, 1, 10);
                if (multiplier != _lastWheelResolutionMultiplier)
                {
                    _controlWheelRemainder = 0;
                    _lastWheelResolutionMultiplier = multiplier;
                }
                var unitsPerTick = Math.Max(1, 120 / multiplier);
                var wheelTotal = _controlWheelRemainder + e.Wheel *
                    (_viewModel.AppliedBluetoothWheelSensitivity / 100.0);
                var wheelUnits = (int)Math.Truncate(wheelTotal / unitsPerTick);
                _controlWheelRemainder = wheelTotal - wheelUnits * unitsPerTick;
                if (wheelUnits == 0) return;
                lock (_controlQueueSync)
                {
                    _pendingControlWheel = Math.Clamp(_pendingControlWheel - wheelUnits,
                        -127, 127);
                    _pendingControlButtons = _controlButtons;
                    _pendingControlStateDirty = true;
                }
                StartControlPointerTimer();
                _ = FlushControlPointerAsync();
            }
            return;
        }
        if (e.Kind == Controls.PreviewPointerKind.ButtonDown)
            _controlButtons |= e.Button;
        else
            _controlButtons = (byte)(_controlButtons & ~e.Button);
        lock (_controlQueueSync)
        {
            _pendingControlButtons = _controlButtons;
            _pendingControlStateDirty = true;
        }
        StartControlPointerTimer();
        _ = FlushControlPointerAsync(force: true);
    }

    private void StartControlPointerTimer()
    {
        // Do not reset the timer for every raw-input packet. Continuous mouse
        // motion can otherwise create an immediate callback storm and starve
        // both WPF and the BLE notification pump.
        if (Interlocked.Exchange(ref _controlPointerTimerArmed, 1) == 0)
            _controlPointerTimer.Change(1, 4);
    }

    private void StopControlPointerTimer()
    {
        Interlocked.Exchange(ref _controlPointerTimerArmed, 0);
        _controlPointerTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task FlushControlPointerAsync(bool force = false)
    {
        if (Interlocked.Exchange(ref _controlPointerFlushInFlight, 1) != 0)
            return;
        int dx;
        int dy;
        int wheel;
        byte buttons;
        long motionAt;
        try
        {
            var routeUdid = _activeControlWindow != 0
                ? _activeControlUdid
                : _viewModel.SelectedDevice?.Udid;
            if (!IsBluetoothControlActiveFor(routeUdid)) return;
            lock (_controlQueueSync)
            {
                if (!force && _pendingControlDx == 0 && _pendingControlDy == 0 &&
                    _pendingControlWheel == 0 && !_pendingControlStateDirty)
                {
                    StopControlPointerTimer();
                    return;
                }
                dx = _pendingControlDx;
                dy = _pendingControlDy;
                wheel = _pendingControlWheel;
                buttons = _pendingControlButtons;
                motionAt = _pendingControlMotionAt;
                _pendingControlDx = 0;
                _pendingControlDy = 0;
                _pendingControlWheel = 0;
                _pendingControlStateDirty = false;
                _pendingControlMotionAt = 0;
            }
            // BLE notifications can occasionally block behind the Bluetooth
            // stack. Never emit a large, old relative-motion burst after that
            // stall; it is perceived as the iOS pointer flying past the cursor.
            if ((dx != 0 || dy != 0) && motionAt != 0)
            {
                var ageMs = (Stopwatch.GetTimestamp() - motionAt) * 1000.0 /
                    Stopwatch.Frequency;
                if (ageMs > 80) { dx = 0; dy = 0; }
            }
            await _viewModel.SendBluetoothMouseAsync(dx, dy, buttons, wheel);
        }
        finally
        {
            Volatile.Write(ref _controlPointerFlushInFlight, 0);
            lock (_controlQueueSync)
            {
                if (_pendingControlDx == 0 && _pendingControlDy == 0 &&
                    _pendingControlWheel == 0 && !_pendingControlStateDirty)
                    StopControlPointerTimer();
                else
                    StartControlPointerTimer();
            }
        }
    }

    private void OnIndependentPointerInput(string udid,
        Controls.PreviewPointerEventArgs e)
    {
        if (_viewModel.UsbControlIsInputEnabled && _viewModel.IsUsbControlTarget(udid))
        {
            _ = HandleUsbPointerInputAsync(e, udid);
            return;
        }
        if (_activeControlWindow == 0 ||
            !DeviceViewModel.UdidEquals(_activeControlUdid, udid)) return;
        HandleControlPointerInput(e, udid);
    }

    private void OnIndependentKeyboardInput(string udid,
        Controls.PreviewKeyboardEventArgs e)
    {
        _viewModel.AddDiagnosticLog(AppLog.Event("independent_keyboard_window_event",
            ("device", AppLog.Device(udid)), ("kind", e.Kind),
            ("virtual_key", e.VirtualKey), ("scan_code", e.ScanCode),
            ("active_window", _activeControlWindow)));
        // The native preview can deliver a key message while the async route
        // transition is still publishing its active HWND. Route by the event
        // source and current device ownership instead of dropping that first
        // key during the transition.
        if (!_viewModel.IsUsbControlTarget(udid) &&
            !IsBluetoothControlActiveFor(udid)) return;
        HandleControlKeyboardInput(e, udid);
    }

    private async void OnIndependentPreviewClosed(string udid)
    {
        // MultiDevicePreviewManager removes the window before raising this
        // event. Restore the main preview even when reverse control was
        // disabled first and its active-route fields have already been cleared.
        QueueMainPreviewHostSync();
        Interlocked.Exchange(ref _bluetoothRouteChanging, 1);
        await _bluetoothRouteGate.WaitAsync();
        var closingActiveControlWindow = false;
        try
        {
            closingActiveControlWindow = _activeControlWindow != 0 &&
                DeviceViewModel.UdidEquals(_activeControlUdid, udid);
            if (!closingActiveControlWindow)
            {
                // Closing a formerly controlled HWND can be the final native
                // cursor transition. Reassert the inactive state after that
                // HWND has been destroyed, even though Disable already ran.
                if (_activeControlWindow == 0 &&
                    !_viewModel.IsBluetoothControlEnabled)
                    ClearBluetoothControlInputState();
                return;
            }
            _activeControlWindow = 0;
            _activeControlUdid = null;
            ClearBluetoothControlInputState();
            if (_viewModel.IsBluetoothControlEnabled)
                await _viewModel.DisableBluetoothControlAsync();
        }
        catch (Exception error)
        {
            if (closingActiveControlWindow)
            {
                _activeControlWindow = 0;
                _activeControlUdid = null;
                ClearBluetoothControlInputState();
            }
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_reverse_control_close_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
        finally
        {
            Volatile.Write(ref _bluetoothRouteChanging, 0);
            _bluetoothRouteGate.Release();
        }
    }

    private async void OnIndependentReverseControlRequested(string udid, nint window)
    {
        Interlocked.Exchange(ref _bluetoothRouteChanging, 1);
        await _bluetoothRouteGate.WaitAsync();
        try
        {
            ResetControlRouteState();
            if (_viewModel.IsBluetoothControlEnabled && _activeControlWindow == window)
            {
                // Context-menu transport entries are start commands. Keep an
                // already active route instead of treating a second click as
                // an implicit stop; the registered shortcut remains the exit.
            }
            else
            {
                if (_viewModel.IsBluetoothControlEnabled)
                    await _viewModel.DisableBluetoothControlAsync();
                _activeControlWindow = window;
                _activeControlUdid = udid;
                await _viewModel.EnableBluetoothControlAsync(udid);
                if (!_viewModel.IsBluetoothControlEnabled)
                {
                    _activeControlWindow = 0;
                    _activeControlUdid = null;
                }
            }
            if (IsBluetoothControlActiveFor(udid))
            {
                // Independent HWNDs can receive focus before the asynchronous
                // view-model notification reaches the main window. Assert the
                // process-wide cursor state on this route as well.
                SetWindowsCursorHidden(true);
            }
            else
                ClearBluetoothControlInputState();
            _controlPointerInitialized = true;
            _lastControlSourceX = 0;
            _lastControlSourceY = 0;
            _controlRemainderX = 0;
            _controlRemainderY = 0;
            _controlWheelRemainder = 0;
        }
        catch (Exception error)
        {
            _activeControlWindow = 0;
            _activeControlUdid = null;
            ClearBluetoothControlInputState();
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_reverse_control_request_failed",
                ("device", AppLog.Device(udid)),
                ("window", AppLog.Handle((ulong)window.ToInt64())),
                ("error", AppLog.Error(error))));
        }
        finally
        {
            ResetControlRouteState();
            Volatile.Write(ref _bluetoothRouteChanging, 0);
            _bluetoothRouteGate.Release();
        }
    }

    private async void OnIndependentUsbControlRequested(string udid, nint window)
    {
        try
        {
            var device = _viewModel.Devices.FirstOrDefault(candidate =>
                DeviceViewModel.UdidEquals(candidate.Udid, udid));
            if (device is null || device.IsMediaCast) return;
            if (!ReferenceEquals(_viewModel.SelectedDevice, device))
                _viewModel.SelectedDevice = device;
            await _viewModel.StartUsbControlAsync(udid);
            if (_viewModel.IsUsbControlEnabled)
            {
                _activeControlWindow = window;
                _activeControlUdid = udid;
                _secondaryMirrors.PrepareUsbControlWindow(udid);
                ApplyBluetoothControlInputState(activateIndependentWindow: false);
            }
            else
            {
                _activeControlWindow = 0;
                _activeControlUdid = null;
                ClearBluetoothControlInputState();
            }
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog($"USB 控制操作失败：{error.Message}");
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_usb_control_request_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
    }

    private async void OnIndependentWirelessControlRequested(string udid, nint window)
    {
        try
        {
            await _viewModel.StartWirelessControlAsync(udid);
            if (!_viewModel.IsWirelessControlTarget(udid)) return;
            _activeControlWindow = window;
            _activeControlUdid = udid;
            _secondaryMirrors.PrepareUsbControlWindow(udid);
            ApplyBluetoothControlInputState(activateIndependentWindow: false);
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog($"无线反控操作失败：{error.Message}");
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_wireless_control_request_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
    }

    private static (int X, int Y) MapPointerToSource(
        Controls.PreviewPointerEventArgs e, uint sourceWidth, uint sourceHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0 || e.SurfaceWidth <= 0 ||
            e.SurfaceHeight <= 0)
            return (Math.Max(0, e.X), Math.Max(0, e.Y));

        var sourceAspect = (double)sourceWidth / sourceHeight;
        var surfaceAspect = (double)e.SurfaceWidth / e.SurfaceHeight;
        double imageX = 0;
        double imageY = 0;
        double imageWidth = e.SurfaceWidth;
        double imageHeight = e.SurfaceHeight;
        if (surfaceAspect > sourceAspect)
        {
            imageWidth = e.SurfaceHeight * sourceAspect;
            imageX = (e.SurfaceWidth - imageWidth) / 2;
        }
        else if (surfaceAspect < sourceAspect)
        {
            imageHeight = e.SurfaceWidth / sourceAspect;
            imageY = (e.SurfaceHeight - imageHeight) / 2;
        }
        var x = Math.Clamp((e.X - imageX) / imageWidth * sourceWidth,
            0, sourceWidth - 1);
        var y = Math.Clamp((e.Y - imageY) / imageHeight * sourceHeight,
            0, sourceHeight - 1);
        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    private static double PointerSensitivity(uint sourceWidth, uint sourceHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0) return 1.0 / 3.0;
        // iPhone screenshots are normally 3x logical pixels; recent iPads are
        // commonly 2x. HID reports are interpreted in logical pointer units.
        return Math.Min(sourceWidth, sourceHeight) >= 1400 ? 0.5 : 1.0 / 3.0;
    }

    private async void OnControlKeyboardInput(object? sender,
        Controls.PreviewKeyboardEventArgs e)
    {
        _viewModel.AddDiagnosticLog(AppLog.Event("control_keyboard_window_event",
            ("kind", e.Kind), ("virtual_key", e.VirtualKey),
            ("scan_code", e.ScanCode), ("active_window", _activeControlWindow),
            ("selected_device", AppLog.Device(_viewModel.SelectedDevice?.Udid))));
        if (_activeControlWindow != 0) return;
        HandleControlKeyboardInput(e, _viewModel.SelectedDevice?.Udid);
    }

    private async void HandleControlKeyboardInput(
        Controls.PreviewKeyboardEventArgs e, string? sourceUdid = null,
        bool fromRawInput = false)
    {
        await _bluetoothRouteGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _bluetoothRouteChanging) != 0) return;
            var routeUdid = sourceUdid ??
                (_activeControlWindow != 0 ? _activeControlUdid :
                    _viewModel.SelectedDevice?.Udid);
            var usbTargetActive = _viewModel.UsbControlIsInputEnabled &&
                _viewModel.IsUsbControlTarget(routeUdid);
            var bluetoothTargetActive = IsBluetoothControlActiveFor(routeUdid);
            _viewModel.AddDiagnosticLog(AppLog.Event("control_keyboard_route",
                ("kind", e.Kind), ("virtual_key", e.VirtualKey),
                ("scan_code", e.ScanCode), ("source", AppLog.Device(sourceUdid)),
                ("route", AppLog.Device(routeUdid)),
                ("from_raw", fromRawInput), ("usb_target", usbTargetActive),
                ("bluetooth_target", bluetoothTargetActive),
                ("usb_input_enabled", _viewModel.UsbControlIsInputEnabled),
                ("usb_control_enabled", _viewModel.IsUsbControlEnabled)));
            if (!usbTargetActive && !bluetoothTargetActive) return;
            if (e.Kind == Controls.PreviewKeyboardKind.Reset)
            {
                _controlKeyboardUsages.Clear();
                _controlModifierKeys.Clear();
                _controlKeyboardModifiers = 0;
                if (bluetoothTargetActive) await _viewModel.SendBluetoothKeyboardAsync(0, []);
                if (usbTargetActive) await _viewModel.SendUsbKeyboardAsync([]);
                return;
            }
            // Raw Input is preferred on the main preview, but it is not
            // guaranteed to reach this window after focus changes or when an
            // independent preview owns the route.  Keep the normal key-message
            // fallback alive; the pressed-key set makes duplicate down/up
            // notifications idempotent.
            if (!TryMapVirtualKey(e.VirtualKey, out var usage, out var modifier)) return;
            if (e.Kind == Controls.PreviewKeyboardKind.Down)
            {
                if (modifier != 0) _controlModifierKeys.Add(
                    ModifierKeyIdentity(e.VirtualKey, e.ScanCode));
                else if (usage != 0) _controlKeyboardUsages.Add(usage);
            }
            else
            {
                if (modifier != 0) _controlModifierKeys.Remove(
                    ModifierKeyIdentity(e.VirtualKey, e.ScanCode));
                else if (usage != 0) _controlKeyboardUsages.Remove(usage);
            }
            _controlKeyboardModifiers = ModifierMask(_controlModifierKeys);
            var usages = _controlKeyboardUsages.ToArray();
            if (bluetoothTargetActive)
                await _viewModel.SendBluetoothKeyboardAsync(_controlKeyboardModifiers, usages);
            if (usbTargetActive)
            {
                var usbUsages = usages.Concat(ModifierUsages(_controlModifierKeys)).ToArray();
                await _viewModel.SendUsbKeyboardAsync(usbUsages);
            }
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "control_keyboard_input_failed",
                ("device", AppLog.Device(sourceUdid)),
                ("error", AppLog.Error(error))));
        }
        finally
        {
            _bluetoothRouteGate.Release();
        }
    }

    private static IEnumerable<byte> ModifierUsages(IEnumerable<int> modifierKeys) =>
        modifierKeys.Select(key => key switch
        {
            0xA0 or 0xA1 => (byte)(0xE0 + (key - 0xA0)),
            0xA2 or 0xA3 => (byte)(0xE2 + (key - 0xA2)),
            0xA4 or 0xA5 => (byte)(0xE4 + (key - 0xA4)),
            0x5B or 0x5C => (byte)(0xE3 + (key - 0x5B)),
            _ => (byte)0,
        }).Where(usage => usage != 0);

    private static bool TryMapVirtualKey(int virtualKey, out byte usage, out byte modifier)
    {
        usage = 0;
        modifier = 0;
        if (virtualKey is >= 0x41 and <= 0x5A) { usage = (byte)(virtualKey - 0x41 + 4); return true; }
        if (virtualKey is >= 0x31 and <= 0x39) { usage = (byte)(virtualKey - 0x31 + 30); return true; }
        if (virtualKey == 0x30) { usage = 39; return true; }
        usage = virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => 0x02,
            0x11 or 0xA2 or 0xA3 => 0x01,
            0x12 or 0xA4 or 0xA5 => 0x04,
            0x20 => 0x2C, 0x0D => 0x28, 0x08 => 0x2A, 0x09 => 0x2B,
            0x1B => 0x29, 0x14 => 0x39, 0x25 => 0x50, 0x26 => 0x52, 0x27 => 0x4F,
            0x28 => 0x51, 0x2E => 0x4C, 0x2D => 0x49, 0x24 => 0x4A,
            0x23 => 0x4D, 0x21 => 0x4B, 0x22 => 0x4E, 0x2C => 0x46,
            0x90 => 0x53, 0x91 => 0x47, 0x13 => 0x48,
            0xBA => 0x33, 0xBB => 0x2E, 0xBC => 0x36, 0xBD => 0x2D,
            0xBE => 0x37, 0xBF => 0x38, 0xC0 => 0x35, 0xDB => 0x2F,
            0xDC => 0x31, 0xDD => 0x30, 0xDE => 0x34,
            0x60 => 0x62, 0x61 => 0x59, 0x62 => 0x5A, 0x63 => 0x5B,
            0x64 => 0x5C, 0x65 => 0x5D, 0x66 => 0x5E, 0x67 => 0x5F,
            0x68 => 0x60, 0x69 => 0x61, 0x6A => 0x55, 0x6B => 0x57,
            0x6D => 0x56, 0x6E => 0x63, 0x6F => 0x54,
            0x72 => 0x3C, 0x73 => 0x3D, 0x74 => 0x3E, 0x75 => 0x3F,
            0x76 => 0x40, 0x77 => 0x41, 0x78 => 0x42, 0x79 => 0x43,
            0x7A => 0x44, 0x7B => 0x45, _ => (byte)0,
        };
        if (virtualKey is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or
            0x12 or 0xA4 or 0xA5)
        {
            modifier = usage;
            usage = 0;
            return true;
        }
        return usage != 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelLightweightWindowWidthAnimation();
        SetSystemKeySuppression(false);
        ClipCursor(IntPtr.Zero);
        StopControlPointerTimer();
        _controlPointerTimer.Dispose();
        RegisterRawInput(false, false);
        UnregisterConfiguredHotkeys();
        SetWindowsCursorHidden(false);
        if (_rawInputBuffer != 0)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = 0;
            _rawInputBufferSize = 0;
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (IsBluetoothControlActive && _activeControlWindow == 0 &&
            (message == WmKillFocus || message == WmCancelMode ||
             message == WmCaptureChanged ||
             (message == WmActivateApp && wParam == 0)))
        {
            ResetMainControlState();
        }
        if (message == WmInput && _activeControlWindow == 0 &&
            (IsBluetoothControlActive || IsUsbControlActive) &&
            (_rawMouseInputEnabled || _rawKeyboardInputEnabled))
        {
            ProcessRawInput(lParam);
            // WM_INPUT requires DefWindowProc cleanup after GetRawInputData.
            // Skipping it leaves raw packets outstanding in the window
            // manager and eventually replays old mouse movement in bursts.
            _ = DefWindowProcW(hwnd, message, wParam, lParam);
            handled = true;
            return 0;
        }
        if (message == WmHotKey &&
            TryGetShortcutActionByHotKeyId(wParam.ToInt32(), out var hotkeyAction))
        {
            HandleConfiguredShortcut(hotkeyAction);
            handled = true;
            return 0;
        }
        if (message == WmSetCursor && IsBluetoothControlActive)
        {
            SetWindowsCursorHidden(true);
            handled = true;
            return 1;
        }
        if ((message is WmActivateApp or WmSetFocus) &&
            IsBluetoothControlActive)
        {
            SetWindowsCursorHidden(true);
        }
        if (!WindowsAutoPlayGuard.ShouldCancel(message,
                _viewModel.HasAnyCaptureSession))
            return 0;

        handled = true;
        _viewModel.AddDiagnosticLog(AppLog.Event("autoplay_cancelled",
            ("message", "WM_QUERYCANCELAUTOPLAY"), ("capture", true)));
        return 1;
    }

    private void RegisterRawInput(bool mouseEnabled, bool keyboardEnabled)
    {
        var hwnd = _windowSource?.Handle ?? 0;
        if (hwnd == 0) return;
        if (mouseEnabled == _rawMouseInputEnabled &&
            keyboardEnabled == _rawKeyboardInputEnabled) return;
        var deviceSize = (uint)Marshal.SizeOf<RawInputDevice>();
        var devices = new RawInputDevice[2];
        devices[0] = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            // Raw Input owns movement, buttons, and wheel events. Suppress
            // legacy mouse messages only for the relative-mouse Bluetooth
            // route. USB/wireless control uses normal mouse messages so the
            // rest of the WPF window remains clickable.
            Flags = mouseEnabled ? RidevInputSink | RidevNoLegacy : RidevRemove,
            Target = mouseEnabled ? hwnd : 0,
        };
        // The actual preview surface is a native child HWND. Keyboard
        // messages sent to that HWND do not reliably bubble through WPF, so
        // also subscribe to keyboard Raw Input. Keep legacy messages enabled
        // (do not use RIDEV_NOLEGACY) so the WPF path remains a fallback when
        // Raw Input is unavailable during a focus transition.
        devices[1] = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x06,
            Flags = keyboardEnabled ? RidevInputSink : RidevRemove,
            Target = keyboardEnabled ? hwnd : 0,
        };
        var registered = RegisterRawInputDevices(devices, (uint)devices.Length, deviceSize);
        _rawMouseInputEnabled = registered && mouseEnabled;
        _rawKeyboardInputEnabled = registered && keyboardEnabled;
        _viewModel.AddDiagnosticLog(AppLog.Event("raw_input_registration",
            ("mouse_enabled", mouseEnabled), ("keyboard_enabled", keyboardEnabled),
            ("registered", registered),
            ("keyboard", _rawKeyboardInputEnabled),
            ("mouse", _rawMouseInputEnabled),
            ("win32_error", registered ? 0 : Marshal.GetLastWin32Error())));
        MainPreviewHost.SuppressMouseMove = _rawMouseInputEnabled;
        if (mouseEnabled || keyboardEnabled)
        {
            MainPreviewHost.Focus();
        }
        // Reverse control should not trap the Windows pointer inside the
        // preview surface or an independent preview window. Pointer capture
        // still supplies relative mouse reports while the cursor remains free.
        ClipCursor(IntPtr.Zero);
    }

    private void ProcessRawInput(nint rawInput)
    {
        uint size = 0;
        _ = GetRawInputData(rawInput, RidInput, 0, ref size,
            (uint)Marshal.SizeOf<RawInputHeader>());
        if (size == 0) return;
        if (_rawInputBuffer == 0 || _rawInputBufferSize < size)
        {
            if (_rawInputBuffer != 0) Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = Marshal.AllocHGlobal((int)size);
            _rawInputBufferSize = (int)size;
        }
        if (GetRawInputData(rawInput, RidInput, _rawInputBuffer, ref size,
                (uint)Marshal.SizeOf<RawInputHeader>()) == unchecked((uint)-1))
            return;
        var input = Marshal.PtrToStructure<RawInput>(_rawInputBuffer);
        if (input.Header.Type == RimTypeKeyboard)
        {
            ProcessRawKeyboardInput(input.Keyboard);
            return;
        }
        if (input.Header.Type != RimTypeMouse) return;

        var sourceWidth = _viewModel.SourceVideoWidth;
        var sourceHeight = _viewModel.SourceVideoHeight;
        var rotation = 0;
        if (_activeControlWindow != 0 &&
            _secondaryMirrors.TryGetControlGeometry(_activeControlUdid,
                out var windowWidth, out var windowHeight, out var windowRotation))
        {
            sourceWidth = windowWidth;
            sourceHeight = windowHeight;
            rotation = windowRotation;
        }
        if (sourceWidth != _lastControlGeometryWidth ||
            sourceHeight != _lastControlGeometryHeight ||
            rotation != _lastControlGeometryRotation)
        {
            _lastControlGeometryWidth = sourceWidth;
            _lastControlGeometryHeight = sourceHeight;
            _lastControlGeometryRotation = rotation;
            _controlRemainderX = 0;
            _controlRemainderY = 0;
        }
        var sensitivity = PointerSensitivity(sourceWidth, sourceHeight) *
            (_viewModel.AppliedBluetoothMouseSensitivity / 100.0);
        var (deviceDx, deviceDy) = MapMouseDeltaToDeviceOrientation(
            input.Mouse.LastX * sensitivity, input.Mouse.LastY * sensitivity,
            sourceWidth, sourceHeight, rotation,
            _viewModel.AppliedBluetoothPortraitMouseDirection,
            _viewModel.AppliedBluetoothLandscapeMouseDirection,
            _viewModel.AppliedBluetoothMouseReverseHorizontal,
            _viewModel.AppliedBluetoothMouseReverseVertical);
        AddRawControlDelta(deviceDx, deviceDy);

        var flags = input.Mouse.ButtonFlags;
        if ((flags & RawMouseLeftDown) != 0) HandleRawButton(1, true);
        if ((flags & RawMouseLeftUp) != 0) HandleRawButton(1, false);
        if ((flags & RawMouseRightDown) != 0) HandleRawButton(2, true);
        if ((flags & RawMouseRightUp) != 0) HandleRawButton(2, false);
        if ((flags & RawMouseMiddleDown) != 0) HandleRawButton(4, true);
        if ((flags & RawMouseMiddleUp) != 0) HandleRawButton(4, false);
        if ((flags & RawMouseWheel) != 0)
            HandleRawWheel(unchecked((short)input.Mouse.ButtonData));

    }

    private void HandleRawButton(byte button, bool down)
    {
        HandleControlPointerInput(new Controls.PreviewPointerEventArgs(
            down ? Controls.PreviewPointerKind.ButtonDown :
                Controls.PreviewPointerKind.ButtonUp,
            0, 0, button, 0));
    }

    private void HandleRawWheel(short delta)
    {
        if (delta == 0) return;
        HandleControlPointerInput(new Controls.PreviewPointerEventArgs(
            Controls.PreviewPointerKind.Wheel, 0, 0, 0, delta));
    }

    private void ResetMainControlState()
    {
        HandleControlPointerInput(new Controls.PreviewPointerEventArgs(
            Controls.PreviewPointerKind.Reset, 0, 0, 0, 0));
        HandleControlKeyboardInput(new Controls.PreviewKeyboardEventArgs(
            Controls.PreviewKeyboardKind.Reset, 0));
    }

    private void ProcessRawKeyboardInput(RawKeyboard keyboard)
    {
        var isKeyUp = (keyboard.Flags & 0x01) != 0 || keyboard.Message is 0x0101 or 0x0105;
        var virtualKey = keyboard.VirtualKey;
        if (virtualKey is 0x5B or 0x5C or 0x5D or 0x5F)
            return;
        if (TryGetShortcutAction(virtualKey, out var shortcutAction))
        {
            if (!isKeyUp && !_registeredHotKeyIds.Contains(HotKeyId(shortcutAction)))
                HandleConfiguredShortcut(shortcutAction);
            return;
        }
        HandleControlKeyboardInput(new Controls.PreviewKeyboardEventArgs(
                isKeyUp ? Controls.PreviewKeyboardKind.Up :
                    Controls.PreviewKeyboardKind.Down,
                virtualKey, keyboard.MakeCode | ((keyboard.Flags & 0x02) != 0 ? 0x100 : 0)),
            _activeControlWindow != 0 ? _activeControlUdid :
                _viewModel.SelectedDevice?.Udid,
            fromRawInput: true);
    }

    private static int ModifierKeyIdentity(int virtualKey, int scanCode = 0) => virtualKey switch
    {
        0xA0 or 0xA1 => virtualKey,
        0xA2 or 0xA3 => virtualKey,
        0xA4 or 0xA5 => virtualKey,
        0x10 => scanCode == 0x36 ? 0xA1 : 0xA0,
        0x11 => scanCode == 0x11D ? 0xA3 : 0xA2,
        0x12 => scanCode == 0x138 ? 0xA5 : 0xA4,
        _ => virtualKey,
    };

    private static byte ModifierMask(IEnumerable<int> keys)
    {
        byte mask = 0;
        foreach (var key in keys)
        {
            if (key is 0xA0 or 0xA1) mask |= 0x02;
            else if (key is 0xA2 or 0xA3) mask |= 0x01;
            else if (key is 0xA4 or 0xA5) mask |= 0x04;
        }
        return mask;
    }

    private void AddRawControlDelta(double dx, double dy)
    {
        var scaledX = dx + _controlRemainderX;
        var scaledY = dy + _controlRemainderY;
        var sendX = (int)Math.Truncate(scaledX);
        var sendY = (int)Math.Truncate(scaledY);
        _controlRemainderX = scaledX - sendX;
        _controlRemainderY = scaledY - sendY;
        if (sendX == 0 && sendY == 0) return;
        lock (_controlQueueSync)
        {
            _pendingControlDx = Math.Clamp(_pendingControlDx + sendX, -32767, 32767);
            _pendingControlDy = Math.Clamp(_pendingControlDy + sendY, -32767, 32767);
            _pendingControlButtons = _controlButtons;
            if (_pendingControlMotionAt == 0)
                _pendingControlMotionAt = Stopwatch.GetTimestamp();
        }
        StartControlPointerTimer();
    }

    private static (double X, double Y) MapMouseDeltaToDeviceOrientation(
        double dx, double dy, uint sourceWidth, uint sourceHeight, int rotation,
        BluetoothMouseDirection portraitDirection,
        BluetoothMouseDirection landscapeDirection,
        bool reverseHorizontal, bool reverseVertical) =>
        BluetoothMouseOrientationMapper.Map(dx, dy, sourceWidth, sourceHeight,
            rotation, portraitDirection, landscapeDirection,
            reverseHorizontal, reverseVertical);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Let WPF render the window before Apple/usbmux enumeration runs. A
        // stalled service or USB re-enumeration must not make the GUI appear
        // frozen or prevent the user from seeing the current status.
        _refreshTimer.Start();
        _mediaCastTimer.Start();
        ApplyWorkspacePanelState();
        ApplyApplicationDisplayMode();
        QueueInitialLightweightWorkspaceFit();
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_loaded",
            ("width", ActualWidth.ToString("F0")),
            ("height", ActualHeight.ToString("F0"))));
        _ = _viewModel.RefreshAsync();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (!_handlingNativeMaximize && !_isFullScreen &&
            WindowState == WindowState.Maximized)
        {
            _handlingNativeMaximize = true;
            try
            {
                var restoreBounds = _isWindowMaximized
                    ? _windowMaximizeRestoreBounds
                    : RestoreBounds;
                WindowState = WindowState.Normal;
                if (_isWindowMaximized)
                    RestoreWindowFromMaximized();
                else
                    MaximizeWindow(restoreBounds);
            }
            finally
            {
                _handlingNativeMaximize = false;
            }
            return;
        }
        ApplyWindowFramePolicy();
    }

    private void ApplyWindowFramePolicy()
    {
        var flushToDisplayEdge = _isFullScreen || _isWindowMaximized;
        WindowCornerPreference = flushToDisplayEdge
            ? Wpf.Ui.Controls.WindowCornerPreference.DoNotRound
            : Wpf.Ui.Controls.WindowCornerPreference.Round;
        WindowBackdropType = flushToDisplayEdge
            ? Wpf.Ui.Controls.WindowBackdropType.None
            : Wpf.Ui.Controls.WindowBackdropType.Mica;
        ThemeService.SetEdgeToEdge(this, flushToDisplayEdge);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.ShowAboutWindow(this, _viewModel);
    }

    private void OnNavigateMirroringClick(object sender, RoutedEventArgs e)
    {
        if (!_workspaceControlsReady || _isSynchronizingWorkspacePanelControls) return;
        SetLeftWorkspacePanel(_leftWorkspacePanel == LeftWorkspacePanel.Mirroring
            ? LeftWorkspacePanel.None
            : LeftWorkspacePanel.Mirroring);
    }

    private void OnNavigateDevicesClick(object sender, RoutedEventArgs e)
    {
        if (!_workspaceControlsReady || _isSynchronizingWorkspacePanelControls) return;
        SetLeftWorkspacePanel(_leftWorkspacePanel == LeftWorkspacePanel.Devices
            ? LeftWorkspacePanel.None
            : LeftWorkspacePanel.Devices);
    }

    private void OnNavigateOutputClick(object sender, RoutedEventArgs e)
        => OnMediaOutputSettingsRequested();

    private void OnNavigateSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_workspaceControlsReady || _isSynchronizingWorkspacePanelControls) return;
        SetSettingsPanelVisible(!_isSettingsPanelVisible);
    }

    private void OnNavigateReverseControlClick(object sender, RoutedEventArgs e)
    {
        if (_reverseControlWindow is { IsLoaded: true, IsVisible: true })
        {
            _reverseControlWindow.Activate();
            _reverseControlWindow.Focus();
            return;
        }
        _reverseControlWindow = null;
        try
        {
            var window = new DeviceBindingWindow(this, _viewModel.Devices, _viewModel);
            _reverseControlWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_reverseControlWindow, window)) _reverseControlWindow = null;
            };
            window.Show();
            window.Activate();
        }
        catch (Exception error)
        {
            _reverseControlWindow = null;
            DiagnosticLogger.Exception("ui", "device_binding_window_open_failed", error);
            AppPromptWindow.Inform("设备绑定器", $"无法打开设备绑定器：{error.Message}");
        }
    }

    private void OnNavigateDriverClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenDriverManagerCommand.CanExecute(null))
            _viewModel.OpenDriverManagerCommand.Execute(null);
    }

    private void OnNavigateAboutClick(object sender, RoutedEventArgs e) =>
        OnAboutClick(sender, e);

    private void OnCloseDevicePanelClick(object sender, RoutedEventArgs e) =>
        SetLeftWorkspacePanel(LeftWorkspacePanel.None);

    private void OnCloseMirroringPanelClick(object sender, RoutedEventArgs e) =>
        SetLeftWorkspacePanel(LeftWorkspacePanel.None);

    private void OnCloseSettingsPanelClick(object sender, RoutedEventArgs e) =>
        SetSettingsPanelVisible(false);

    private void SetLeftWorkspacePanel(LeftWorkspacePanel panel)
    {
        if (_leftWorkspacePanel == panel) return;
        _leftWorkspacePanel = panel;
        ApplyWorkspacePanelState(animate: IsLoaded && !_isFullScreen);
        _viewModel.AddDiagnosticLog(AppLog.Event("workspace_left_panel_changed",
            ("panel", panel.ToString().ToLowerInvariant())));
    }

    private void SetSettingsPanelVisible(bool visible)
    {
        if (_isSettingsPanelVisible == visible) return;
        _isSettingsPanelVisible = visible;
        ApplyWorkspacePanelState(animate: IsLoaded && !_isFullScreen);
        _viewModel.AddDiagnosticLog(AppLog.Event("workspace_settings_panel_changed",
            ("visible", visible)));
    }

    private void ApplyWorkspacePanelState(bool animate = false)
    {
        animate &= SystemParameters.ClientAreaAnimation;
        var showMirroring = _leftWorkspacePanel == LeftWorkspacePanel.Mirroring;
        var showDevices = _leftWorkspacePanel == LeftWorkspacePanel.Devices;
        var showSettings = _isSettingsPanelVisible;
        var showLeftPanel = showMirroring || showDevices;
        ApplyLightweightPreviewFramePolicy();
        _isSynchronizingWorkspacePanelControls = true;
        try
        {
            MirroringPanelToggle.IsActive = showMirroring;
            DevicePanelToggle.IsActive = showDevices;
            SettingsPanelToggle.IsActive = showSettings;
        }
        finally
        {
            _isSynchronizingWorkspacePanelControls = false;
        }

        if (!animate)
        {
            if (_viewModel.IsLightweightApplicationMode)
                CancelLightweightWindowWidthAnimation();
            CancelWorkspaceGapAnimations();
            DeviceColumn.Width = GridLength.Auto;
            ControlColumn.Width = GridLength.Auto;
            LeftGapColumn.Width = showLeftPanel ? new GridLength(18) : new GridLength(0);
            RightGapColumn.Width = showSettings ? new GridLength(18) : new GridLength(0);
            ++_workspaceTransitionRevision;
            SetWorkspaceSurfaceImmediate(LeftPanelHost, showLeftPanel, 300);
            SetWorkspacePageImmediate(DevicePanel, showDevices);
            SetWorkspacePageImmediate(MirroringPanel, showMirroring);
            SetWorkspaceSurfaceImmediate(ControlPanel, showSettings, 336);
            if (_viewModel.IsLightweightApplicationMode)
                QueueInitialLightweightWorkspaceFit();
            else
                RequestLightweightWindowFit();
            return;
        }

        if (_viewModel.IsLightweightApplicationMode)
        {
            var lightweightRevision = ++_workspaceTransitionRevision;
            AnimateLightweightWindowForWorkspace(showLeftPanel, showSettings);
            AnimateWorkspacePage(DevicePanel, showDevices, fromLeft: true,
                lightweightRevision);
            AnimateWorkspacePage(MirroringPanel, showMirroring, fromLeft: true,
                lightweightRevision);
            AnimateWorkspacePage(ControlPanel, showSettings, fromLeft: false,
                lightweightRevision);
            return;
        }

        DeviceColumn.Width = GridLength.Auto;
        ControlColumn.Width = GridLength.Auto;
        var revision = ++_workspaceTransitionRevision;
        AnimateWorkspaceGap(LeftGapColumn, showLeftPanel ? 18 : 0, revision);
        AnimateWorkspaceGap(RightGapColumn, showSettings ? 18 : 0, revision);
        AnimateWorkspaceSurface(LeftPanelHost, showLeftPanel, 300,
            fromLeft: true, revision);
        AnimateWorkspacePage(DevicePanel, showDevices, fromLeft: true, revision);
        AnimateWorkspacePage(MirroringPanel, showMirroring, fromLeft: true, revision);
        AnimateWorkspaceSurface(ControlPanel, showSettings, 336,
            fromLeft: false, revision);
    }

    private void AnimateLightweightWindowForWorkspace(bool showLeftPanel,
        bool showSettings)
    {
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _isWindowMaximized || CenterColumn.ActualWidth <= 0 ||
            !SystemParameters.ClientAreaAnimation)
            return;

        CancelLightweightWindowWidthAnimation(preserveLayout: true);
        CancelWorkspaceGapAnimations();
        DeviceColumn.Width = GridLength.Auto;
        ControlColumn.Width = GridLength.Auto;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds)) return;
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        var currentWindowWidth = Math.Max(1, bounds.Right - bounds.Left) / scale;
        var currentLeftWidth = GetLightweightElementWidth(LeftPanelHost);
        var currentRightWidth = GetLightweightElementWidth(ControlPanel);
        var currentLeftGap = GetLightweightColumnWidth(LeftGapColumn);
        var currentRightGap = GetLightweightColumnWidth(RightGapColumn);
        var currentCenterWidth = Math.Max(1, CenterColumn.ActualWidth);
        var currentPreviewWidth = PreviewPanel.ActualWidth > 0
            ? PreviewPanel.ActualWidth
            : currentCenterWidth;

        _lightweightLeftSurfaceStartWidth = currentLeftWidth;
        _lightweightLeftSurfaceTargetWidth = showLeftPanel ? 300 : 0;
        _lightweightRightSurfaceStartWidth = currentRightWidth;
        _lightweightRightSurfaceTargetWidth = showSettings ? 336 : 0;
        _lightweightLeftGapStartWidth = currentLeftGap;
        _lightweightLeftGapTargetWidth = showLeftPanel ? 18 : 0;
        _lightweightRightGapStartWidth = currentRightGap;
        _lightweightRightGapTargetWidth = showSettings ? 18 : 0;
        _lightweightCenterStartWidth = currentCenterWidth;
        var baseChromeWidth = GetLightweightFixedChromeWidth(currentWindowWidth);
        var targetSideWidth = _lightweightLeftSurfaceTargetWidth +
            _lightweightLeftGapTargetWidth + _lightweightRightGapTargetWidth +
            _lightweightRightSurfaceTargetWidth;
        var maximumWindowWidth = GetLightweightMaximumWindowWidth();
        var targetWindowLeft = bounds.Left;
        var anchoredMaximumWindowWidth = maximumWindowWidth;
        if (TryGetLightweightWorkArea(out var workArea, out _))
        {
            targetWindowLeft = Math.Clamp(bounds.Left, workArea.Left,
                Math.Max(workArea.Left, workArea.Right - 1));
            anchoredMaximumWindowWidth = Math.Min(maximumWindowWidth,
                Math.Max(1, (workArea.Right - targetWindowLeft) / scale -
                    LightweightWorkAreaInset));
        }
        var hasTargetPanels = showLeftPanel || showSettings;
        var maximumPreviewWidth = Math.Max(1, anchoredMaximumWindowWidth -
            baseChromeWidth - targetSideWidth);
        var hasContentPreviewWidth = TryGetLightweightContentPreviewWidth(
            maximumPreviewWidth, out var contentPreviewWidth);
        var canUseNormalPortraitFrame = !hasContentPreviewWidth && hasTargetPanels &&
            maximumPreviewWidth >= LightweightNormalPortraitPreviewWidth;
        ApplyLightweightPreviewFramePolicy(canUseNormalPortraitFrame,
            hasContentPreviewWidth ? contentPreviewWidth : null);
        var targetPreviewWidth = hasContentPreviewWidth
            ? contentPreviewWidth
            : canUseNormalPortraitFrame
            ? LightweightNormalPortraitPreviewWidth
            : Math.Min(currentPreviewWidth, maximumPreviewWidth);
        var requiredPreviewWidth = hasContentPreviewWidth
            ? contentPreviewWidth
            : canUseNormalPortraitFrame
            ? LightweightNormalPortraitPreviewWidth
            : LightweightMinimumPreviewWidth;
        var minimumWindowWidth = Math.Max(LightweightMinimumWindowWidth,
            baseChromeWidth + targetSideWidth + requiredPreviewWidth);
        _lightweightTargetMinWidth = Math.Min(minimumWindowWidth,
            anchoredMaximumWindowWidth);
        if (MinWidth > _lightweightTargetMinWidth || ActualWidth >= _lightweightTargetMinWidth)
            MinWidth = _lightweightTargetMinWidth;
        _lightweightWidthNeedsFit = false;
        _lightweightCenterTargetWidth = targetPreviewWidth;
        var targetWindowWidth = Math.Min(anchoredMaximumWindowWidth, baseChromeWidth +
            targetSideWidth + _lightweightCenterTargetWidth);
        // Keep the center column star-sized. Its rendered preview width then
        // follows the current HWND width on every frame while the side panels
        // and their gaps advance in lockstep with the outer window.
        _lightweightWorkspaceSurfaceAnimationActive = true;
        ReserveLightweightWorkspaceSurface(LeftPanelHost,
            currentLeftWidth);
        ReserveLightweightWorkspaceSurface(ControlPanel,
            currentRightWidth);
        if (_lightweightLeftSurfaceTargetWidth > 0)
            LeftPanelHost.Visibility = Visibility.Visible;
        if (_lightweightRightSurfaceTargetWidth > 0)
            ControlPanel.Visibility = Visibility.Visible;
        LeftGapColumn.Width = new GridLength(currentLeftGap);
        RightGapColumn.Width = new GridLength(currentRightGap);
        SetLightweightCenterColumnFill();
        var plannedWindowWidth = Math.Clamp(targetWindowWidth,
            _lightweightTargetMinWidth, anchoredMaximumWindowWidth);
        _lightweightWindowStartX = bounds.Left;
        // Keep the left navigation rail fixed while either workspace panel
        // expands or contracts. The available width changes to the right.
        _lightweightWindowTargetX = targetWindowLeft;
        AnimateLightweightWindowWidth(plannedWindowWidth, preserveCenterWidth: true);
    }

    private static double GetLightweightElementWidth(FrameworkElement element) =>
        double.IsFinite(element.Width) ? Math.Max(0, element.Width) :
            Math.Max(0, element.ActualWidth);

    private static double GetLightweightColumnWidth(ColumnDefinition column) =>
        column.Width.IsAbsolute ? Math.Max(0, column.Width.Value) :
            Math.Max(0, column.ActualWidth);

    private static void ReserveLightweightWorkspaceSurface(FrameworkElement element,
        double width)
    {
        element.BeginAnimation(WidthProperty, null);
        if (width > 0) element.Visibility = Visibility.Visible;
        element.Width = width;
    }

    private void RequestLightweightWindowFit()
    {
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _isWindowMaximized || _lightweightWorkspaceSurfaceAnimationActive) return;
        _lightweightWidthNeedsFit = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, QueueLightweightPreviewWidth);
    }

    private void SetLightweightWindowWidthImmediately(double targetWidth)
    {
        if (!double.IsFinite(targetWidth) || targetWidth <= 0) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsLoaded || handle == 0 || !GetWindowRect(handle, out var bounds))
        {
            Width = targetWidth;
            return;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        var targetPixels = Math.Max(1, (int)Math.Round(targetWidth * scale));
        if (Math.Abs((bounds.Right - bounds.Left) - targetPixels) <= 1)
        {
            Width = targetWidth;
            return;
        }

        // A wired QuickTime session can start presenting while WPF processes
        // its first source-size update. Resize the HWND once instead of
        // generating a 280 ms WM_SIZE burst that churns the D3D swap chain.
        _ = SetWindowPos(handle, 0, bounds.Left, bounds.Top, targetPixels,
            Math.Max(1, bounds.Bottom - bounds.Top), SwpNoZOrder | SwpNoActivate);
        Width = targetWidth;
    }

    private void QueueInitialLightweightWorkspaceFit()
    {
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _isWindowMaximized) return;
        if (_lightweightInitialWorkspaceFitQueued) return;
        _lightweightInitialWorkspaceFitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _lightweightInitialWorkspaceFitQueued = false;
            if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
                _isWindowMaximized) return;
            MainContentGrid.UpdateLayout();
            FitLightweightWorkspaceImmediately();
        });
    }

    // The first WPF layout can contain a large star-column remainder from the
    // complete-mode startup width. It is content slack, not window chrome.
    // Commit a final lightweight geometry only after that layout is available.
    private void FitLightweightWorkspaceImmediately()
    {
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _isWindowMaximized) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds)) return;

        CancelLightweightWindowWidthAnimation(preserveLayout: true);
        var showLeftPanel = _leftWorkspacePanel != LeftWorkspacePanel.None;
        var showSettings = _isSettingsPanelVisible;
        var hasPanels = showLeftPanel || showSettings;
        DeviceColumn.Width = GridLength.Auto;
        ControlColumn.Width = GridLength.Auto;
        SetWorkspaceSurfaceImmediate(LeftPanelHost, showLeftPanel, 300);
        SetWorkspaceSurfaceImmediate(ControlPanel, showSettings, 336);
        LeftGapColumn.Width = new GridLength(showLeftPanel ? 18 : 0);
        RightGapColumn.Width = new GridLength(showSettings ? 18 : 0);
        SetLightweightCenterColumnFill();
        MainContentGrid.UpdateLayout();

        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        var currentWindowWidth = Math.Max(1, bounds.Right - bounds.Left) / scale;
        var chromeWidth = GetLightweightFixedChromeWidth(currentWindowWidth);
        var sideWidth = (showLeftPanel ? 318 : 0) +
            (showSettings ? 354 : 0);
        var maximumWindowWidth = GetLightweightMaximumWindowWidth();
        var maximumPreviewWidth = Math.Max(1, maximumWindowWidth - chromeWidth -
            sideWidth);
        var hasContentPreviewWidth = TryGetLightweightContentPreviewWidth(
            maximumPreviewWidth, out var contentPreviewWidth);
        var useNormalPortraitFrame = !hasContentPreviewWidth && hasPanels &&
            maximumPreviewWidth >= LightweightNormalPortraitPreviewWidth;
        var minimumPreviewWidth = hasContentPreviewWidth
            ? contentPreviewWidth
            : hasPanels
            ? LightweightMinimumPreviewWidth
            : Math.Min(LightweightMinimumPreviewWidth, maximumPreviewWidth);
        var minimumWindowWidth = Math.Max(LightweightMinimumWindowWidth,
            chromeWidth + sideWidth + minimumPreviewWidth);
        _lightweightTargetMinWidth = Math.Min(minimumWindowWidth,
            maximumWindowWidth);
        MinWidth = _lightweightTargetMinWidth;

        var targetPreviewWidth = hasContentPreviewWidth
            ? contentPreviewWidth
            : useNormalPortraitFrame
            ? LightweightNormalPortraitPreviewWidth
            : hasPanels
                ? maximumPreviewWidth
                : GetInitialLightweightPreviewWidth(maximumPreviewWidth);
        var targetWindowWidth = Math.Clamp(chromeWidth + sideWidth +
            targetPreviewWidth, _lightweightTargetMinWidth, maximumWindowWidth);
        ApplyLightweightPreviewFramePolicy(useNormalPortraitFrame,
            hasContentPreviewWidth ? targetPreviewWidth : null);
        var windowUsesTargetPreviewWidth = targetWindowWidth <= chromeWidth +
            sideWidth + targetPreviewWidth + 0.5;
        if (useNormalPortraitFrame ||
            (hasContentPreviewWidth && windowUsesTargetPreviewWidth))
            CenterColumn.Width = new GridLength(
                targetPreviewWidth);
        else
            SetLightweightCenterColumnFill();

        var targetPixels = Math.Max(1, (int)Math.Round(targetWindowWidth * scale));
        var targetX = bounds.Left;
        if (TryGetLightweightWorkArea(out var workArea, out _))
            targetX = Math.Clamp(targetX, workArea.Left, Math.Max(workArea.Left,
                workArea.Right - targetPixels));
        _ = SetWindowPos(handle, 0, targetX, bounds.Top, targetPixels,
            Math.Max(1, bounds.Bottom - bounds.Top), SwpNoZOrder | SwpNoActivate);
        Left = targetX / scale;
        Top = bounds.Top / scale;
        Width = targetWindowWidth;
        _lightweightWidthNeedsFit = false;
        MainContentGrid.UpdateLayout();
    }

    private double GetInitialLightweightPreviewWidth(double maximumPreviewWidth)
    {
        var aspect = _viewModel.SourceVideoWidth != 0 &&
            _viewModel.SourceVideoHeight != 0
            ? (double)_viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight
            : LightweightDefaultPreviewAspect;
        var preferredWidth = Math.Max(LightweightMinimumPreviewWidth,
            Math.Round(Math.Max(520, PreviewPanel.ActualHeight) * aspect));
        return Math.Min(preferredWidth, maximumPreviewWidth);
    }

    private static void SetWorkspacePageImmediate(FrameworkElement element, bool visible)
    {
        element.BeginAnimation(OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }
        element.Opacity = visible ? 1 : 0;
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetWorkspaceSurfaceImmediate(FrameworkElement element,
        bool visible, double width)
    {
        element.BeginAnimation(WidthProperty, null);
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
        if (element.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        element.Width = visible ? width : 0;
    }

    private void CancelWorkspaceGapAnimations()
    {
        LeftGapColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        RightGapColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
    }

    private void AnimateWorkspaceGap(ColumnDefinition column, double target,
        int revision)
    {
        var current = Math.Max(0, column.ActualWidth);
        column.BeginAnimation(ColumnDefinition.WidthProperty, null);
        column.Width = new GridLength(current);
        if (Math.Abs(current - target) < 0.5)
        {
            column.Width = new GridLength(target);
            return;
        }

        var animation = new GridLengthAnimation
        {
            From = new GridLength(current),
            To = new GridLength(target),
            Duration = new Duration(WorkspaceTransitionDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        animation.Completed += (_, _) =>
        {
            if (revision != _workspaceTransitionRevision) return;
            column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            column.Width = new GridLength(target);
        };
        column.BeginAnimation(ColumnDefinition.WidthProperty, animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateWorkspaceSurface(FrameworkElement element, bool visible,
        double width, bool fromLeft, int revision)
    {
        if (_lightweightWorkspaceSurfaceAnimationActive) return;
        var wasVisible = element.Visibility == Visibility.Visible;
        var currentWidth = wasVisible
            ? Math.Max(0, element.ActualWidth)
            : 0;
        var targetWidth = visible ? width : 0;
        element.BeginAnimation(WidthProperty, null);
        element.Width = currentWidth;
        if (Math.Abs(currentWidth - targetWidth) < 0.5)
        {
            element.Width = targetWidth;
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!ReferenceEquals(element, LeftPanelHost))
                AnimateWorkspacePage(element, visible, fromLeft, revision,
                    startFromHidden: visible && !wasVisible);
            return;
        }
        if (visible)
        {
            element.Visibility = Visibility.Visible;
            if (ReferenceEquals(element, LeftPanelHost)) element.Opacity = 1;
        }

        var widthAnimation = CreateWorkspaceAnimation(currentWidth, targetWidth);
        widthAnimation.Completed += (_, _) =>
        {
            if (revision != _workspaceTransitionRevision) return;
            element.BeginAnimation(WidthProperty, null);
            element.Width = visible ? width : 0;
            if (!visible) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(WidthProperty, widthAnimation);

        if (!ReferenceEquals(element, LeftPanelHost))
            AnimateWorkspacePage(element, visible, fromLeft, revision,
                startFromHidden: visible && !wasVisible);
    }

    private void AnimateWorkspacePage(FrameworkElement element, bool visible,
        bool fromLeft, int revision, bool startFromHidden = false)
    {
        if (!visible && element.Visibility != Visibility.Visible) return;
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        var wasVisible = !startFromHidden && element.Visibility == Visibility.Visible;
        var direction = fromLeft ? -1d : 1d;
        var currentOpacity = wasVisible ? Math.Clamp(element.Opacity, 0, 1) : 0;
        var currentTranslation = wasVisible ? transform.X : direction * 16;
        element.BeginAnimation(OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        // Preserve the effective animated values before replacing their clocks.
        // Without this, rapid open/close input jumps back to the base values.
        element.Opacity = currentOpacity;
        transform.X = currentTranslation;
        if (visible) element.Visibility = Visibility.Visible;

        var opacity = CreateWorkspaceAnimation(currentOpacity, visible ? 1 : 0);
        var translation = CreateWorkspaceAnimation(currentTranslation,
            visible ? 0 : direction * 12);
        opacity.Completed += (_, _) =>
        {
            if (revision != _workspaceTransitionRevision) return;
            element.BeginAnimation(OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = visible ? 1 : 0;
            transform.X = 0;
            if (!visible) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(OpacityProperty, opacity);
        transform.BeginAnimation(TranslateTransform.XProperty, translation);
    }

    private static DoubleAnimation CreateWorkspaceAnimation(double from, double to)
    {
        var animation = new DoubleAnimation(from, to, WorkspaceTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        // Width animations run a WPF layout pass. A 60 Hz cap remains fluid
        // while avoiding duplicated layout work on high-refresh displays.
        Timeline.SetDesiredFrameRate(animation, 60);
        return animation;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeControlReady || sender is not ComboBox { SelectedValue: string value } ||
            !Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ||
            Application.Current is not App app || app.UpdateSettings.Theme == theme)
            return;
        app.UpdateSettings.Theme = theme;
        ThemeService.Apply(theme);
        app.SaveUpdateSettings();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }

        DragMove();
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_isWindowMaximized)
            RestoreWindowFromMaximized();
        else
            MaximizeWindow();
    }

    private void MaximizeWindow(Rect? restoreBounds = null)
    {
        if (_isFullScreen) return;
        if (_viewModel.IsLightweightApplicationMode)
        {
            CancelLightweightWindowWidthAnimation();
            SizeToContent = SizeToContent.Manual;
            PreviewPanel.ClearValue(WidthProperty);
        }
        if (!_isWindowMaximized)
        {
            _windowMaximizeRestoreBounds = restoreBounds ?? new Rect(
                Left, Top, ActualWidth, ActualHeight);
        }

        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo)) return;

        WindowState = WindowState.Normal;
        _isWindowMaximized = true;
        ApplyWindowFramePolicy();
        _ = SetWindowPos(handle, 0,
            monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
            SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
    }

    private void RestoreWindowFromMaximized()
    {
        if (!_isWindowMaximized) return;
        _isWindowMaximized = false;
        WindowState = WindowState.Normal;
        ApplyWindowFramePolicy();
        Left = _windowMaximizeRestoreBounds.Left;
        Top = _windowMaximizeRestoreBounds.Top;
        Width = _windowMaximizeRestoreBounds.Width;
        Height = _windowMaximizeRestoreBounds.Height;
        ApplyApplicationDisplayMode();
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        var shutdownTimer = Stopwatch.StartNew();
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_closing",
            ("media_cast", _mediaCastActive),
            ("independent_media_window", _mediaCastPreviewWindow is not null),
            ("full_screen", _isFullScreen)));
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_shutdown_begin"));
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _viewModel.DeviceVideoSizeChanged -= OnDeviceVideoSizeChanged;
        _viewModel.DeviceSessionHandleChanged -= OnDeviceSessionHandleChanged;
        _viewModel.DeviceProtectionStateChanged -= OnDeviceProtectionStateChanged;
        _viewModel.MediaCastCommandReceived -= OnMediaCastCommandReceived;
        _viewModel.MediaCastStopRequested -= OnMediaCastStopRequested;
        _viewModel.MediaCastAudioSettingsChanged -= OnMediaCastAudioSettingsChanged;
        _viewModel.ProjectionSettingsRequested -= OnProjectionSettingsRequested;
        _viewModel.MediaOutputSettingsRequested -= OnMediaOutputSettingsRequested;
        _refreshTimer.Stop();
        _mediaCastTimer.Stop();
        var application = Application.Current;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        foreach (Window window in application.Windows.Cast<Window>().ToArray())
        {
            try { window.Hide(); }
            catch (Exception error)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event(
                    "shutdown_window_hide_failed",
                    ("window", window.GetType().Name),
                    ("error", AppLog.Error(error))));
            }
        }
        try { _mediaCastPreviewWindow?.HideForShutdown(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "shutdown_media_preview_hide_failed",
                ("error", AppLog.Error(error))));
        }
        _secondaryMirrors.HideForShutdown();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            try
            {
                StopMediaCastPlayback("window_closing");
            }
            catch (Exception error)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("main_window_media_cleanup_failed",
                    ("error", AppLog.Error(error))));
                Debug.WriteLine($"iPhoneMirror media shutdown failed: {AppLog.Error(error)}");
            }
            try
            {
                _projectionSettingsWindow?.Close();
                _projectionSettingsWindow = null;
                _mediaOutputSettingsWindow?.Close();
                _mediaOutputSettingsWindow = null;
                _secondaryMirrors.Dispose();
            }
            catch (Exception error)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("main_window_preview_cleanup_failed",
                    ("error", AppLog.Error(error))));
                Debug.WriteLine($"iPhoneMirror preview-window shutdown failed: {AppLog.Error(error)}");
            }
            try
            {
                var shutdown = _viewModel.ShutdownAsync();
                var shutdownLimit = (Application.Current as App)?
                    .IsSystemSessionEnding == true
                    ? TimeSpan.FromSeconds(4)
                    : TimeSpan.FromSeconds(15);
                try
                {
                    await shutdown.WaitAsync(shutdownLimit);
                }
                catch (TimeoutException)
                {
                    _viewModel.AddDiagnosticLog(AppLog.Event(
                        "main_window_shutdown_timeout",
                        ("elapsed_ms", shutdownTimer.ElapsedMilliseconds),
                        ("limit_ms", shutdownLimit.TotalMilliseconds),
                        ("system_session_ending", (Application.Current as App)?
                            .IsSystemSessionEnding == true)));
                    // Observe a late completion without keeping the WPF close
                    // path alive. Process termination is the only reliable
                    // escape when a third-party USB kernel call never returns.
                    _ = shutdown.ContinueWith(task =>
                    {
                        if (task.Exception is not null)
                            DiagnosticLogger.Exception("shutdown",
                                "late_shutdown_failed",
                                task.Exception.GetBaseException());
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception error)
            {
                // Window shutdown must complete even if a broken USB stack reports
                // an error after the explicit stop/dispose attempts have run.
                _viewModel.AddDiagnosticLog(AppLog.Event("main_window_core_shutdown_failed",
                    ("elapsed_ms", shutdownTimer.ElapsedMilliseconds),
                    ("error", AppLog.Error(error))));
                Debug.WriteLine($"iPhoneMirror core shutdown failed: {AppLog.Error(error)}");
            }
        }
        finally
        {
            Debug.WriteLine($"iPhoneMirror main window close dispatch completed in " +
                $"{shutdownTimer.ElapsedMilliseconds} ms");
            _allowClose = true;
            application.Shutdown(0);
        }
    }

    private void OnMediaCastCommandReceived(MediaCastRequest request)
    {
        try
        {
            _mediaCommandId = request.CommandId;
            _viewModel.AddDiagnosticLog(AppLog.Event("media_command_received",
                ("id", request.CommandId), ("type", request.Command),
                ("flags", request.Flags),
                ("duration", request.Duration.ToString("F3")),
                ("position", request.StartPosition.ToString("F3")),
                ("volume", request.Volume.ToString("F3")),
                ("active", _mediaCastActive), ("opened", _mediaOpened)));
            switch (request.Command)
            {
            case MediaCastCommand.Stop:
                StopMediaCastPlayback("remote_command");
                _viewModel.AddUiLog(LocalizationService.Get("MediaCastStopped"));
                break;
            case MediaCastCommand.Play:
                PlayMediaCast(request);
                _viewModel.AddUiLog(LocalizationService.Get("MediaCastPlayReceived"));
                break;
            case MediaCastCommand.Pause:
                if (_mediaCastActive)
                {
                    SetMediaCastTimelineRunning(false);
                    _mediaShouldPlay = false;
                    if (_mediaOpened) MediaCastMediaElement.Pause();
                    _mediaCastAudioDecoder.Stop();
                    _mediaPlaying = false;
                    UpdateMediaCastStatistics();
                    UpdateMediaCastControls();
                    if (_mediaOpened) ReportMediaCastPlayback();
                    _viewModel.AddDiagnosticLog(AppLog.Event("media_pause_applied",
                        ("id", request.CommandId), ("position", _mediaStartPosition),
                        ("opened", _mediaOpened)));
                }
                break;
            case MediaCastCommand.Resume:
                if (_mediaCastActive)
                {
                    _mediaShouldPlay = true;
                    if (_mediaOpened) MediaCastMediaElement.Play();
                    RestartMediaCastAudioAtCurrentPosition();
                    _mediaPlaying = _mediaOpened;
                    SynchronizeMediaCastTimelineClock();
                    UpdateMediaCastStatistics();
                    UpdateMediaCastControls();
                    if (_mediaOpened) ReportMediaCastPlayback();
                    _viewModel.AddDiagnosticLog(AppLog.Event("media_resume_applied",
                        ("id", request.CommandId), ("position", _mediaStartPosition),
                        ("opened", _mediaOpened)));
                }
                break;
            case MediaCastCommand.Seek:
                if (_mediaCastActive)
                {
                    var target = ClampMediaPosition(request.StartPosition,
                        clampToDuration: true);
                    // iQIYI sends a small position correction immediately
                    // after MediaOpened (for example target=1 while the local
                    // stream is already around 1-2 seconds). Treat that as a
                    // startup sync acknowledgement; otherwise it needlessly
                    // tears down and restarts the HLS bridge. Larger or later
                    // seeks still take the exact requested programme position.
                    SeekMediaCastToPosition(target,
                        allowCoalesce: IsLikelyMediaCastStartupSeek(target));
                    _viewModel.AddDiagnosticLog(AppLog.Event("media_seek_applied",
                        ("id", request.CommandId), ("target", target),
                        ("opened", _mediaOpened)));
                }
                break;
            case MediaCastCommand.Volume:
                if (_mediaCastActive)
                {
                    var volume = double.IsFinite(request.Volume)
                        ? Math.Clamp(request.Volume, 0, 1) : 1;
                    var muteSpecified = request.Flags.HasFlag(
                        MediaCastFlags.MuteSpecified);
                    var muted = request.Flags.HasFlag(MediaCastFlags.Muted);
                    MediaCastMediaElement.Volume = volume;
                    if (muteSpecified) MediaCastMediaElement.IsMuted = muted;
                    _viewModel.UpdateMediaCastAudioControls(
                        !MediaCastMediaElement.IsMuted, volume);
                    UpdateMediaCastStatistics();
                    UpdateMediaCastControls();
                    if (_mediaOpened) ReportMediaCastPlayback();
                    _viewModel.AddDiagnosticLog(AppLog.Event("media_volume_applied",
                        ("id", request.CommandId), ("volume", volume),
                        ("mute_specified", muteSpecified),
                        ("muted", MediaCastMediaElement.IsMuted),
                        ("opened", _mediaOpened)));
                }
                break;
            }
            _viewModel.AddDiagnosticLog(AppLog.Event("media_command_applied",
                ("id", request.CommandId), ("type", request.Command),
                ("active", _mediaCastActive), ("opened", _mediaOpened),
                ("playing", _mediaPlaying)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_command_failed",
                ("id", request.CommandId), ("type", request.Command),
                ("error", AppLog.Error(error))));
            if (request.Command == MediaCastCommand.Play)
            {
                if (_mediaCastActive) StopMediaCastPlayback("command_failed");
                // A rejected Play still exists in the receiver's command
                // state. Explicitly acknowledge it with the upstream stop
                // protocol even when no local media card was created.
                _viewModel.RequestMediaCastStop(allowInactive: true);
            }
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastPlaybackFailedFormat", AppLog.Error(error.Message)));
        }
    }

    private void OnMediaCastStopRequested()
    {
        try
        {
            StopMediaCastPlayback("native_stop_request");
            _viewModel.AddUiLog(LocalizationService.Get("MediaCastStopped"));
            _viewModel.AddDiagnosticLog(AppLog.Event("media_stop_request_applied",
                ("active", _mediaCastActive)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_stop_request_failed",
                ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media stop event failed: {AppLog.Error(error)}");
        }
    }

    private void OnMediaCastAudioSettingsChanged(bool enabled, double volume)
    {
        try
        {
            MediaCastMediaElement.IsMuted = !enabled;
            MediaCastMediaElement.Volume = Math.Clamp(volume, 0, 1);
            UpdateMediaCastStatistics();
            UpdateMediaCastControls();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_audio_applied",
                ("enabled", enabled), ("volume", volume.ToString("F3")),
                ("opened", _mediaOpened)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(
                AppLog.Event("media_audio_control_failed",
                    ("error", AppLog.Error(SanitizeMediaError(error.Message),
                        error.GetType().Name))));
        }
    }

    private void PlayMediaCast(MediaCastRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(LocalizationService.Get("MediaCastInvalidUrl"));

        _mediaCastAudioDecoder.Stop();
        ResetMediaRecoveryCancellation();
        var generation = _mediaCastEvents.BeginGeneration();
        ++_mediaRecoveryRevision;
        _mediaProgramDuration = MediaSourceClassifier.IsLikelyLive(source) &&
            !MediaCastPlaybackControls.IsReliableDuration(true,
                request.Duration) ? 0 : NormalizeMediaDuration(request.Duration);
        _mediaStartPosition = ClampMediaPosition(request.StartPosition,
            clampToDuration: true, duration: _mediaProgramDuration);
        _mediaLastTimelineDuration = _mediaProgramDuration;
        _mediaLastTimelinePosition = _mediaStartPosition;
        _mediaBridgeOffset = _mediaStartPosition;
        SetMediaCastTimelinePosition(_mediaStartPosition, running: false);
        _lastRejectedMediaPosition = 0;
        _mediaProgressSampleUtc = DateTime.UtcNow;
        ClearMediaCastPendingHlsSeek();
        ClearMediaCastPendingSeek();
        _mediaSeekLoading = false;
        _mediaSeekInteraction = false;
        _mediaSeekCommitPending = false;
        _mediaSeekTrackInteraction = false;
        _mediaSeekInteractionTarget = _mediaStartPosition;
        _mediaPlaying = false;
        _mediaShouldPlay = true;
        _mediaOpened = false;
        _mediaStopped = false;
        _mediaCastActive = true;
        _mediaSource = source;
        _mediaPlaybackSource = source;
        _mediaBuffering = false;
        _mediaWaitingForFirstFrame = true;
        _mediaOpeningPosition = _mediaStartPosition;
        _mediaOpenedAtUtc = DateTime.UtcNow;
        var volume = double.IsFinite(request.Volume)
            ? Math.Clamp(request.Volume, 0, 1) : 1;
        var muteSpecified = request.Flags.HasFlag(MediaCastFlags.MuteSpecified);
        var muted = muteSpecified && request.Flags.HasFlag(MediaCastFlags.Muted);
        var audioEnabled = !muted;
        _mediaUsesHlsBridge = MediaSourceClassifier.IsLikelyLive(source);
        _mediaIsLive = _mediaUsesHlsBridge;
        if (_mediaIsLive)
        {
            _mediaHlsBridge = HlsMediaPlaybackBridge.TryStart(source,
                _mediaStartPosition, message =>
                _viewModel.AddDiagnosticLog(AppLog.Event("hls_bridge",
                    ("message", AppLog.Error(message)))),
                duration => QueueHlsProgramDuration(
                    generation, source, duration));
            if (_mediaHlsBridge is null)
            {
                // Never fall back to WPF's native HLS path. WMF exposes each
                // HLS segment as a short clip and reports MediaEnded at the
                // segment boundary, which makes the sender restart at zero.
                _mediaUsesHlsBridge = false;
                _mediaIsLive = false;
                throw new InvalidOperationException(
                    LocalizationService.Get("MediaCastHlsBackendUnavailable"));
            }
            _mediaPlaybackSource = _mediaHlsBridge.PlaybackUri;
        }
        _mediaRecoveryBackoff.Reset();
        _viewModel.AddDiagnosticLog(AppLog.Event("media_play_begin",
            ("command", request.CommandId),
            ("source", AppLog.MediaSource(source)),
            ("likely_live", _mediaIsLive),
            ("duration", _mediaProgramDuration.ToString("F3")),
            ("generation", generation),
            ("start_position", _mediaStartPosition.ToString("F3")),
            ("volume", volume.ToString("F3")),
            ("mute_specified", muteSpecified), ("muted", muted)));
        _viewModel.BeginMediaCast(volume);
        if (muteSpecified)
            _viewModel.UpdateMediaCastAudioControls(audioEnabled, volume);
        if (!ReplaceMediaCastMediaElement(_mediaPlaybackSource, generation,
                audioEnabled, volume))
        {
            StopMediaCastPlayback("backend_bind_rejected");
            return;
        }
        ShowMediaCastStatus("MediaCastLoadingVideo");
        _mediaOpeningTimer.Start();
        UpdateMediaCastControls();
        SynchronizeMainPreviewHost();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        MediaCastMediaElement.Play();
        _mediaPlaybackTimer.Start();
        _viewModel.AddDiagnosticLog(AppLog.Event("media_play_submitted",
            ("command", request.CommandId), ("generation", generation),
            ("source", AppLog.MediaSource(source))));
    }

    private void StopMediaCastPlayback(string reason = "unspecified")
    {
        var wasActive = _mediaCastActive;
        var command = _mediaCommandId;
        var source = AppLog.MediaSource(_mediaSource);
        var stopTimer = Stopwatch.StartNew();
        _viewModel.AddDiagnosticLog(AppLog.Event("media_stop_begin",
            ("reason", reason), ("active", wasActive),
            ("command", command), ("source", source),
            ("opened", _mediaOpened), ("playing", _mediaPlaying)));
        try
        {
            StopMediaCastPlaybackCore();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_stop_complete",
                ("reason", reason), ("was_active", wasActive),
                ("command", command), ("elapsed_ms", stopTimer.ElapsedMilliseconds),
                ("success", true)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_stop_failed",
                ("reason", reason), ("was_active", wasActive),
                ("command", command), ("elapsed_ms", stopTimer.ElapsedMilliseconds),
                ("error", AppLog.Error(error))));
            ForceMediaCastStopped(error);
        }
    }

    private void StopMediaCastPlaybackCore()
    {
        _mediaOpeningTimer.Stop();
        CancelMediaRecovery();
        _mediaCastEvents.Invalidate();
        ++_mediaRecoveryRevision;
        if (!_mediaStopped)
        {
            _mediaStopped = true;
            _mediaPlaying = false;
            _mediaShouldPlay = false;
            _mediaOpened = false;
            _mediaBuffering = false;
            _mediaWaitingForFirstFrame = false;
            _mediaSeekInteraction = false;
            _mediaSeekCommitPending = false;
            _mediaSeekTrackInteraction = false;
            _mediaSeekInteractionTarget = 0;
            _mediaSeekLoading = false;
            _lastSeekSliderSyncPosition = double.NaN;
            ClearMediaCastPendingHlsSeek();
            ClearMediaCastPendingSeek();
            _mediaPlaybackTimer.Stop();
            ReportMediaCastPlayback();
            try
            {
                MediaCastMediaElement.Stop();
                MediaCastMediaElement.Source = null;
            }
            catch (InvalidOperationException error)
            {
                _viewModel.AddDiagnosticLog(
                    $"media_close_failed error={SanitizeMediaError(error.Message)}");
            }
        }
        _mediaCastActive = false;
        _mediaCastAudioDecoder.Stop();
        _mediaIsLive = false;
        _mediaUsesHlsBridge = false;
        _mediaProgramDuration = 0;
        _mediaBridgeOffset = 0;
        ResetMediaCastTimelineClock();
        _mediaLastTimelineDuration = 0;
        _mediaLastTimelinePosition = 0;
        _mediaSource = null;
        _mediaPlaybackSource = null;
        DisposeHlsMediaBridge();
        _lastRejectedMediaPosition = 0;
        _mediaProgressSampleUtc = default;
        _mediaRecoveryBackoff.Reset();
        _mediaCommandId = 0;
        var previewWindow = _mediaCastPreviewWindow;
        _mediaCastPreviewWindow = null;
        try
        {
            previewWindow?.Dispose();
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(
                $"media_preview_close_failed error={SanitizeMediaError(error.Message)}");
        }
        MediaCastSurface.Visibility = Visibility.Collapsed;
        ResetMediaCastControls();
        _viewModel.EndMediaCast();
        MainPreviewHost.ClearValue(VisibilityProperty);
        SynchronizeMainPreviewHost();
    }

    private void ForceMediaCastStopped(Exception cause)
    {
        Debug.WriteLine($"iPhoneMirror media cleanup failed: {AppLog.Error(cause)}");
        try
        {
            _viewModel.AddDiagnosticLog(
                $"media_cleanup_failed error={SanitizeMediaError(cause.Message)}");
        }
        catch (Exception error)
        {
            Debug.WriteLine($"iPhoneMirror media cleanup logging failed: {AppLog.Error(error)}");
        }

        CancelMediaRecovery();
        _mediaCastEvents.Invalidate();
        ++_mediaRecoveryRevision;
        _mediaStopped = true;
        _mediaPlaying = false;
        _mediaShouldPlay = false;
        _mediaOpened = false;
        _mediaCastActive = false;
        _mediaCastAudioDecoder.Stop();
        _mediaIsLive = false;
        _mediaUsesHlsBridge = false;
        _mediaProgramDuration = 0;
        _mediaBridgeOffset = 0;
        ResetMediaCastTimelineClock();
        _mediaLastTimelineDuration = 0;
        _mediaLastTimelinePosition = 0;
        _mediaBuffering = false;
        _mediaWaitingForFirstFrame = false;
        _mediaSeekInteraction = false;
        _mediaSeekCommitPending = false;
        _mediaSeekTrackInteraction = false;
        _mediaSeekInteractionTarget = 0;
        _mediaSeekLoading = false;
        _lastSeekSliderSyncPosition = double.NaN;
        ClearMediaCastPendingHlsSeek();
        ClearMediaCastPendingSeek();
        _mediaSource = null;
        _mediaPlaybackSource = null;
        DisposeHlsMediaBridge();
        _lastRejectedMediaPosition = 0;
        _mediaProgressSampleUtc = default;
        _mediaRecoveryBackoff.Reset();
        _mediaCommandId = 0;
        try { _mediaPlaybackTimer.Stop(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "timer"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media timer cleanup failed: {AppLog.Error(error)}");
        }
        try
        {
            MediaCastMediaElement.Stop();
            MediaCastMediaElement.Source = null;
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "source"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media source cleanup failed: {AppLog.Error(error)}");
        }
        var previewWindow = _mediaCastPreviewWindow;
        _mediaCastPreviewWindow = null;
        try { previewWindow?.Dispose(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "window"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media window cleanup failed: {AppLog.Error(error)}");
        }
        try { MediaCastSurface.Visibility = Visibility.Collapsed; }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "surface"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media surface cleanup failed: {AppLog.Error(error)}");
        }
        try { ResetMediaCastControls(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "controls"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media controls cleanup failed: {AppLog.Error(error)}");
        }
        try { _viewModel.EndMediaCast(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "state"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror media state cleanup failed: {AppLog.Error(error)}");
        }
        try
        {
            MainPreviewHost.ClearValue(VisibilityProperty);
            SynchronizeMainPreviewHost();
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_force_cleanup_step_failed",
                ("step", "preview_host"), ("error", AppLog.Error(error))));
            Debug.WriteLine($"iPhoneMirror preview host cleanup failed: {AppLog.Error(error)}");
        }
    }

    private bool ReplaceMediaCastMediaElement(Uri source, long generation,
        bool audioEnabled, double volume)
    {
        _viewModel.AddDiagnosticLog(AppLog.Event("media_backend_replace_begin",
            ("generation", generation), ("source", AppLog.MediaSource(source)),
            ("audio", audioEnabled), ("volume", volume.ToString("F3"))));
        var replacement = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            ScrubbingEnabled = true,
            IsMuted = !audioEnabled,
            Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 1,
            // WMF can throw MILAVERR_UNEXPECTEDWMPFAILURE when SpeedRatio is
            // assigned before MediaOpened on an HLS MPEG-TS stream. Apply the
            // selected rate after opening, with a pause/play transaction.
            SpeedRatio = 1.0,
        };
        replacement.MediaOpened += (sender, _) =>
            OnMediaCastMediaOpened(sender, generation);
        replacement.MediaEnded += (sender, _) =>
            OnMediaCastMediaEnded(sender, generation);
        replacement.MediaFailed += (sender, e) =>
            OnMediaCastMediaFailed(sender, e, generation);
        replacement.BufferingStarted += (sender, _) =>
            OnMediaCastBufferingStarted(sender, generation);
        replacement.BufferingEnded += (sender, _) =>
            OnMediaCastBufferingEnded(sender, generation);
        if (!_mediaCastEvents.TryBind(generation, replacement))
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_backend_replace_rejected",
                ("generation", generation), ("reason", "stale_generation")));
            return false;
        }

        var previous = MediaCastMediaElement;
        MediaCastVideoHost.Children.Clear();
        MediaCastVideoHost.Children.Add(replacement);
        MediaCastMediaElement = replacement;
        try
        {
            previous.Stop();
        }
        catch (InvalidOperationException error)
        {
            _viewModel.AddDiagnosticLog(
                $"media_previous_source_stop_failed error={SanitizeMediaError(error.Message)}");
        }
        try
        {
            previous.Source = null;
        }
        catch (InvalidOperationException error)
        {
            _viewModel.AddDiagnosticLog(
                $"media_previous_source_clear_failed error={SanitizeMediaError(error.Message)}");
        }
        replacement.Source = source;
        _viewModel.AddDiagnosticLog(AppLog.Event("media_backend_replace_complete",
            ("generation", generation), ("source", AppLog.MediaSource(source))));
        return true;
    }

    private void DisposeHlsMediaBridge()
    {
        var bridge = _mediaHlsBridge;
        _mediaHlsBridge = null;
        try { bridge?.Dispose(); }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("hls_bridge_dispose_failed",
                ("error", AppLog.Error(error))));
        }
    }

    private bool IsCurrentMediaCastEvent(MediaElement mediaElement, long generation) =>
        _mediaCastActive && _mediaSource is not null &&
        _mediaCastEvents.IsCurrent(generation, mediaElement);

    private void OnMediaCastBufferingStarted(object? sender, long generation)
    {
        if (sender is not MediaElement mediaElement ||
            !IsCurrentMediaCastEvent(mediaElement, generation)) return;
        _mediaBuffering = true;
        SetMediaCastTimelineRunning(false);
        ShowMediaCastStatus("MediaCastLoadingVideo");
        UpdateMediaCastControls(mediaElement);
        _viewModel.AddDiagnosticLog(AppLog.Event("media_buffering_started",
            ("generation", generation),
            ("position", ReadMediaCastPosition(mediaElement).ToString("F3"))));
    }

    private void OnMediaCastBufferingEnded(object? sender, long generation)
    {
        if (sender is not MediaElement mediaElement ||
            !IsCurrentMediaCastEvent(mediaElement, generation)) return;
        _mediaBuffering = false;
        SynchronizeMediaCastTimelineClock();
        if (!_mediaWaitingForFirstFrame)
            MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        UpdateMediaCastControls(mediaElement);
        _viewModel.AddDiagnosticLog(AppLog.Event("media_buffering_ended",
            ("generation", generation),
            ("position", ReadMediaCastPosition(mediaElement).ToString("F3"))));
    }

    private void OnMediaCastMediaOpened(object? sender, long generation)
    {
        if (sender is not MediaElement mediaElement ||
            !IsCurrentMediaCastEvent(mediaElement, generation)) return;
        try
        {
            CompleteMediaCastMediaOpened(mediaElement, generation);
        }
        catch (Exception error)
        {
            RecoverOrStopAfterMediaEventFailure(
                "opened", error, mediaElement, generation);
        }
    }

    private void CompleteMediaCastMediaOpened(
        MediaElement mediaElement, long generation)
    {
        if (!IsCurrentMediaCastEvent(mediaElement, generation)) return;
        ++_mediaRecoveryRevision;
        _mediaOpeningTimer.Stop();
        _mediaOpened = true;
        _mediaRecoveryBackoff.MarkOpened();
        var hasFixedDuration = mediaElement.NaturalDuration.HasTimeSpan &&
            mediaElement.NaturalDuration.TimeSpan > TimeSpan.Zero;
        var naturalDuration = hasFixedDuration
            ? mediaElement.NaturalDuration.TimeSpan.TotalSeconds : 0;
        // WMF reports the current HLS segment as a short fixed-duration clip.
        // Keep segmented sources in the recovery path until a duration large
        // enough to be a real program duration is available.
        var segmentedSource = _mediaSource is not null &&
            MediaSourceClassifier.IsLikelyLive(_mediaSource);
        if (_mediaProgramDuration <= 0 &&
            MediaCastPlaybackControls.IsReliableDuration(segmentedSource,
                naturalDuration))
            _mediaProgramDuration = naturalDuration;
        var hasReliableDuration = _mediaProgramDuration > 0 ||
            MediaCastPlaybackControls.IsReliableDuration(segmentedSource,
                naturalDuration);
        _mediaIsLive = segmentedSource && !hasReliableDuration;
        _mediaStartPosition = ClampMediaPosition(_mediaStartPosition,
            clampToDuration: true);
        if (_mediaStartPosition > 0 && _mediaHlsBridge is null)
        {
            try
            {
                mediaElement.Position = TimeSpan.FromSeconds(_mediaStartPosition);
                BeginMediaCastPendingSeek(_mediaStartPosition);
            }
            catch (InvalidOperationException error)
            {
                _viewModel.AddDiagnosticLog(
                    $"media_initial_seek_ignored position={_mediaStartPosition:F3} " +
                    $"error={SanitizeMediaError(error.Message)}");
                _mediaStartPosition = 0;
            }
        }
        if (_mediaShouldPlay) mediaElement.Play();
        else mediaElement.Pause();
        _mediaPlaying = _mediaShouldPlay;
        // Keep the requested programme position as the loading anchor. WMF's
        // newly-opened HLS element may briefly report zero or a segment-local
        // timestamp before its first frame is actually presented.
        _mediaOpeningPosition = ClampMediaPosition(
            _mediaPendingHlsSeekPosition ?? _mediaStartPosition,
            clampToDuration: true);
        _mediaOpenedAtUtc = DateTime.UtcNow;
        _mediaProgressSampleUtc = _mediaOpenedAtUtc;
        _mediaWaitingForFirstFrame = _mediaShouldPlay;
        if (_mediaPlaybackSpeed != 1.0)
            ApplyMediaCastSpeed(mediaElement, _mediaPlaybackSpeed);
        SynchronizeMediaCastTimelineClock();
        if (_mediaWaitingForFirstFrame || _mediaBuffering)
            ShowMediaCastStatus("MediaCastLoadingVideo");
        else
            MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        if (mediaElement.NaturalVideoWidth > 0 &&
            mediaElement.NaturalVideoHeight > 0)
            _mediaCastPreviewWindow?.SetSourceDimensions(
                (uint)mediaElement.NaturalVideoWidth,
                (uint)mediaElement.NaturalVideoHeight);
        UpdateMediaCastStatistics(mediaElement);
        UpdateMediaCastControls(mediaElement);
        _viewModel.AddDiagnosticLog(AppLog.Event("media_opened",
            ("generation", generation), ("source", AppLog.MediaSource(_mediaSource)),
            ("live", _mediaIsLive),
            ("duration_seconds", _mediaProgramDuration.ToString("F3")),
            ("size", $"{mediaElement.NaturalVideoWidth}x{mediaElement.NaturalVideoHeight}"),
            ("start_position", _mediaStartPosition.ToString("F3")),
            ("should_play", _mediaShouldPlay)));
        ReportMediaCastPlayback();
    }

    private void OnMediaCastMediaEnded(object? sender, long generation)
    {
        if (sender is not MediaElement mediaElement ||
            !IsCurrentMediaCastEvent(mediaElement, generation)) return;
        try
        {
            CompleteMediaCastMediaEnded(mediaElement, generation);
        }
        catch (Exception error)
        {
            RecoverOrStopAfterMediaEventFailure(
                "ended", error, mediaElement, generation);
        }
    }

    private void CompleteMediaCastMediaEnded(
        MediaElement mediaElement, long generation)
    {
        if (!IsCurrentMediaCastEvent(mediaElement, generation)) return;
        AdvanceImplicitMediaProgress(mediaElement);
        var endedPosition = ReadMediaCastPosition(mediaElement);
        // A running FFmpeg bridge is a continuous transport. MediaElement can
        // still emit a spurious EOF while its MPEG-TS input is reconnecting;
        // keep the cast session alive and let the bridge own HLS recovery.
        if (_mediaHlsBridge is { IsRunning: true })
        {
            _mediaOpened = true;
            _mediaPlaying = _mediaShouldPlay;
            _mediaBuffering = false;
            _mediaWaitingForFirstFrame = false;
            SynchronizeMediaCastTimelineClock();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_ended_ignored",
                ("generation", generation), ("position", endedPosition.ToString("F3")),
                ("reason", "hls_bridge_running")));
            try
            {
                if (_mediaShouldPlay) mediaElement.Play();
            }
            catch (InvalidOperationException error)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("hls_bridge_resume_failed",
                    ("error", AppLog.Error(error))));
            }
            UpdateMediaCastStatistics(mediaElement);
            UpdateMediaCastControls(mediaElement);
            ReportMediaCastPlayback();
            return;
        }
        if (_mediaHlsBridge is not null)
        {
            // The bridge has reached the actual end of the HLS output. This
            // is the only EOF that should be allowed to trigger next-episode
            // handling; a MediaElement segment EOF never reaches this branch.
            _mediaIsLive = false;
            _mediaShouldPlay = false;
            _mediaPlaying = false;
            _mediaOpened = false;
            _mediaPlaybackTimer.Stop();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_ended",
                ("generation", generation), ("live", false),
                ("position", endedPosition.ToString("F3")),
                ("source", AppLog.MediaSource(_mediaSource)),
                ("reason", "hls_bridge_eof")));
            QueueMediaCastCompletion();
            return;
        }
        RememberImplicitMediaProgress(endedPosition);
        _mediaOpeningTimer.Stop();
        _mediaOpened = false;
        // Keep a playing heartbeat across a segmented HLS hand-off. Reporting
        // rate=0 during the short reload gap makes some senders interpret a
        // segment boundary as the end of the programme and issue Next.
        _mediaPlaying = _mediaIsLive && _mediaShouldPlay;
        _mediaBuffering = false;
        _mediaWaitingForFirstFrame = false;
        ClearMediaCastPendingSeek();
        _viewModel.AddDiagnosticLog(AppLog.Event("media_ended",
            ("generation", generation), ("live", _mediaIsLive),
            ("position", endedPosition.ToString("F3")),
            ("source", AppLog.MediaSource(_mediaSource))));
        if (_mediaIsLive || _mediaUsesHlsBridge)
        {
            ShowMediaCastStatus("MediaCastLoadingVideo");
            UpdateMediaCastStatistics(mediaElement);
            UpdateMediaCastControls(mediaElement);
            ReportMediaCastPlayback();
            QueueLiveMediaRecovery("stream ended at the current live edge");
            return;
        }
        _mediaShouldPlay = false;
        _mediaPlaybackTimer.Stop();
        MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        _viewModel.AddUiLog(LocalizationService.Get("MediaCastPlaybackEnded"));
        UpdateMediaCastStatistics(mediaElement);
        UpdateMediaCastControls(mediaElement);
        ReportMediaCastPlayback();
        QueueMediaCastCompletion();
    }

    private void OnMediaCastMediaFailed(
        object? sender, ExceptionRoutedEventArgs e, long generation)
    {
        if (sender is not MediaElement mediaElement ||
            !IsCurrentMediaCastEvent(mediaElement, generation)) return;
        try
        {
            CompleteMediaCastMediaFailed(mediaElement, e, generation);
        }
        catch (Exception error)
        {
            RecoverOrStopAfterMediaEventFailure(
                "failed", error, mediaElement, generation);
        }
    }

    private void CompleteMediaCastMediaFailed(MediaElement mediaElement,
        ExceptionRoutedEventArgs e, long generation)
    {
        if (!IsCurrentMediaCastEvent(mediaElement, generation)) return;
        AdvanceImplicitMediaProgress(mediaElement);
        var failedPosition = ReadMediaCastPosition(mediaElement);
        if (_mediaSpeedFallbackPending && _mediaPlaybackSpeed != 1.0)
        {
            // WMF rejects SpeedRatio changes on some HLS MPEG-TS samples with
            // 0x8898050C. Do not leave the recovery loop at the requested rate;
            // rebuild once at the stable native rate instead.
            var requestedSpeed = _mediaPlaybackSpeed;
            _mediaSpeedFallbackPending = false;
            _mediaPlaybackSpeed = 1.0;
            MediaCastSpeedComboBox.SelectedIndex = 2;
            NotifyMediaCastSpeedFallback(requestedSpeed);
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "media_speed_fallback",
                ("position", failedPosition.ToString("F3")),
                ("error", e.ErrorException?.HResult.ToString("X8") ?? "unknown")));
        }
        if (_mediaHlsBridge is { IsRunning: false, ExitedSuccessfully: true })
        {
            _mediaIsLive = false;
            _mediaShouldPlay = false;
            _mediaPlaying = false;
            _mediaOpened = false;
            _mediaPlaybackTimer.Stop();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_failed_as_eof",
                ("generation", generation),
                ("position", failedPosition.ToString("F3")),
                ("reason", "hls_bridge_eof")));
            QueueMediaCastCompletion();
            return;
        }
        RememberImplicitMediaProgress(failedPosition);
        _mediaOpeningTimer.Stop();
        _mediaOpened = false;
        _mediaPlaying = (_mediaIsLive || _mediaUsesHlsBridge) && _mediaShouldPlay;
        _mediaBuffering = false;
        _mediaWaitingForFirstFrame = false;
        ClearMediaCastPendingSeek();
        var message = SanitizeMediaError(
            e.ErrorException?.Message ?? LocalizationService.Get("UnknownError"));
        _viewModel.AddDiagnosticLog(AppLog.Event("media_failed",
            ("generation", generation), ("live", _mediaIsLive),
            ("source", AppLog.MediaSource(_mediaSource)),
            ("error", AppLog.Error(message,
                e.ErrorException?.GetType().Name))));
        if (_mediaIsLive || _mediaUsesHlsBridge)
        {
            ShowMediaCastStatus("MediaCastLoadingVideo");
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastLiveRecoveringFormat", message));
            UpdateMediaCastStatistics(mediaElement);
            UpdateMediaCastControls(mediaElement);
            ReportMediaCastPlayback();
            QueueLiveMediaRecovery(message);
            return;
        }
        _mediaShouldPlay = false;
        _mediaPlaybackTimer.Stop();
        MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        _viewModel.AddUiLog(LocalizationService.Format("MediaCastPlaybackFailedFormat",
            message));
        UpdateMediaCastStatistics(mediaElement);
        UpdateMediaCastControls(mediaElement);
        ReportMediaCastPlayback();
        QueueMediaCastCompletion();
    }

    private void ReportMediaCastPlayback()
    {
        if (!_mediaCastActive) return;
        try
        {
            UpdateMediaCastControls();
            if (_mediaCommandId == 0) return;
            // A HLS bridge starts life in the live/recovery state, but the
            // Play command may already carry the complete programme duration.
            // Preserve that duration while the bridge is opening so the phone
            // keeps a VOD timeline instead of briefly treating it as live.
            var duration = _mediaProgramDuration > 0
                ? _mediaProgramDuration
                : _mediaIsLive ? 0 : ReadMediaCastDuration(
                    MediaCastMediaElement);
            var pendingHlsSeek = ReadMediaCastPendingHlsSeekPosition();
            var position = pendingHlsSeek ??
                (IsMediaCastTimelineLoading()
                    ? ReadMediaCastLoadingPosition()
                    : _mediaOpened
                        ? ReadMediaCastControlPosition(MediaCastMediaElement)
                        : Math.Max(0, _mediaStartPosition));
            // A remote HLS seek is still loading even after MediaOpened has
            // fired. Keep the controller at the requested target until the
            // first stable frame, then resume its normal playing heartbeat.
            // Report the programme clock's actual state. During a bridge
            // replacement the position is intentionally frozen; claiming a
            // non-zero rate here makes the phone add wall-clock time between
            // reports and then snap backwards on the next update.
            var rate = _mediaTimelineRunning ? 1 : 0;
            _viewModel.ReportMediaCastPlayback(_mediaCommandId, duration,
                position,
                rate);
            _lastPlaybackReportError = null;
            UpdateMediaCastStatistics();
        }
        catch (Exception error)
        {
            // WMF can briefly reject Position/NaturalDuration while changing
            // source or recovering a live manifest. IPC can also disappear
            // during receiver shutdown. Neither condition may take down the UI.
            var failure = AppLog.Error(error);
            if (!string.Equals(_lastPlaybackReportError, failure, StringComparison.Ordinal))
            {
                _lastPlaybackReportError = failure;
                _viewModel.AddDiagnosticLog(AppLog.Event("media_playback_state_failed",
                    ("command", _mediaCommandId), ("error", failure)));
                Debug.WriteLine($"iPhoneMirror playback-state report failed: {failure}");
            }
        }
    }

    private void RecoverOrStopAfterMediaEventFailure(string stage, Exception error,
        MediaElement mediaElement, long generation)
    {
        if (!IsCurrentMediaCastEvent(mediaElement, generation)) return;
        AdvanceImplicitMediaProgress(mediaElement);
        var failedPosition = ReadMediaCastPosition(mediaElement);
        RememberImplicitMediaProgress(failedPosition);
        _mediaOpened = false;
        _mediaPlaying = _mediaShouldPlay;
        var message = SanitizeMediaError(error.Message);
        try
        {
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastPlaybackFailedFormat", message));
            _viewModel.AddDiagnosticLog(
                $"media_event_failed stage={stage} error={message}");
        }
        catch (Exception logError)
        {
            Debug.WriteLine($"iPhoneMirror media-event failure logging failed: {AppLog.Error(logError)}");
        }

        if (_mediaCastActive && (_mediaIsLive || _mediaUsesHlsBridge) &&
            _mediaSource is not null)
        {
            try
            {
                ShowMediaCastStatus("MediaCastLoadingVideo");
                QueueLiveMediaRecovery(message);
                return;
            }
            catch (Exception recoveryError)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_schedule_failed",
                    ("stage", stage), ("error", AppLog.Error(recoveryError))));
                Debug.WriteLine($"iPhoneMirror live recovery scheduling failed: {AppLog.Error(recoveryError)}");
            }
        }
        StopMediaCastPlayback("media_event_failed");
    }

    private double ClampMediaPosition(double position, bool clampToDuration = true,
        double duration = 0)
    {
        var knownDuration = NormalizeMediaDuration(duration);
        if (knownDuration <= 0) knownDuration = _mediaProgramDuration;
        if (knownDuration <= 0 && clampToDuration && !_mediaIsLive &&
            MediaCastMediaElement.NaturalDuration.HasTimeSpan)
            knownDuration = MediaCastMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
        return MediaCastPlaybackControls.ClampPosition(position,
            clampToDuration ? knownDuration : 0);
    }

    private static double NormalizeMediaDuration(double duration) =>
        double.IsFinite(duration) && duration > 0 &&
        duration <= TimeSpan.FromDays(7).TotalSeconds ? duration : 0;

    private void QueueHlsProgramDuration(long generation, Uri source,
        double duration)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_mediaCastActive || !_mediaUsesHlsBridge ||
                generation != _mediaCastEvents.CurrentGeneration ||
                !Equals(source, _mediaSource) ||
                !MediaCastPlaybackControls.IsReliableDuration(
                    segmented: true, duration)) return;
            if (Math.Abs(_mediaProgramDuration - duration) < 0.05) return;
            _mediaProgramDuration = duration;
            _mediaIsLive = false;
            _mediaStartPosition = MediaCastPlaybackControls.ClampPosition(
                _mediaStartPosition, duration);
            UpdateMediaCastControls();
            ReportMediaCastPlayback();
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "media_hls_duration_discovered",
                ("generation", generation),
                ("duration", duration.ToString("F3")),
                ("position", _mediaStartPosition.ToString("F3"))));
        });
    }

    private void SeekMediaCastToPosition(double target, bool allowCoalesce = true)
    {
        if (!_mediaCastActive) return;
        target = ClampMediaPosition(target, clampToDuration: true);
        if (_mediaHlsBridge is not null)
        {
            var current = ReadMediaCastTimelinePosition();
            if (allowCoalesce && Math.Abs(target - current) <= 8)
            {
                ClearMediaCastPendingHlsSeek();
                _mediaSeekLoading = false;
                _mediaStartPosition = Math.Max(current, target);
                SetMediaCastTimelinePosition(_mediaStartPosition,
                    running: _mediaTimelineRunning);
                _mediaProgressSampleUtc = DateTime.UtcNow;
                UpdateMediaCastControls();
                ReportMediaCastPlayback();
                _viewModel.AddDiagnosticLog(AppLog.Event(
                    "media_hls_seek_coalesced",
                    ("target", target.ToString("F3")),
                    ("current", current.ToString("F3"))));
                return;
            }
        }
        _mediaStartPosition = target;
        SetMediaCastTimelinePosition(target, running: false);
        _mediaProgressSampleUtc = DateTime.UtcNow;
        ClearMediaCastPendingHlsSeek();

        if (_mediaHlsBridge is null)
        {
            if (!_mediaOpened) return;
            _mediaSeekLoading = true;
            try
            {
                MediaCastMediaElement.Position = TimeSpan.FromSeconds(target);
                RestartMediaCastAudioAtCurrentPosition();
                BeginMediaCastPendingSeek(target);
                UpdateMediaCastControls();
                ReportMediaCastPlayback();
                _viewModel.AddDiagnosticLog(AppLog.Event("media_local_seek",
                    ("target", target.ToString("F3")),
                    ("duration", ReadMediaCastDuration(MediaCastMediaElement)
                        .ToString("F3"))));
            }
            catch (InvalidOperationException error)
            {
                _mediaSeekLoading = false;
                _viewModel.AddDiagnosticLog(AppLog.Event("media_local_seek_failed",
                    ("target", target.ToString("F3")),
                    ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
            }
            return;
        }

        var source = _mediaSource;
        if (source is null) return;
        var generation = _mediaCastEvents.CurrentGeneration;
        var audioEnabled = !MediaCastMediaElement.IsMuted;
        var volume = MediaCastMediaElement.Volume;
        try
        {
            _mediaPendingHlsSeekPosition = target;
            _mediaPendingHlsSeekStartedUtc = DateTime.UtcNow;
            _mediaSeekLoading = true;
            _mediaCastAudioDecoder.Stop();
            DisposeHlsMediaBridge();
            _mediaBridgeOffset = target;
            _mediaPlaybackSource = source;
            _mediaHlsBridge = HlsMediaPlaybackBridge.TryStart(source, target,
                message => _viewModel.AddDiagnosticLog(AppLog.Event("hls_bridge",
                    ("message", AppLog.Error(message)))),
                duration => QueueHlsProgramDuration(
                    generation, source, duration));
            if (_mediaHlsBridge is null)
                throw new InvalidOperationException("HLS bridge restart failed");
            _mediaPlaybackSource = _mediaHlsBridge.PlaybackUri;
            _mediaOpened = false;
            _mediaBuffering = false;
            _mediaWaitingForFirstFrame = _mediaShouldPlay;
            _mediaOpeningPosition = target;
            _mediaOpenedAtUtc = DateTime.UtcNow;
            _mediaProgressSampleUtc = _mediaOpenedAtUtc;
            ClearMediaCastPendingSeek();
            ShowMediaCastStatus("MediaCastLoadingVideo");
            _mediaOpeningTimer.Start();
            if (!ReplaceMediaCastMediaElement(_mediaPlaybackSource, generation,
                    audioEnabled, volume)) return;
            if (_mediaShouldPlay) MediaCastMediaElement.Play();
            else MediaCastMediaElement.Pause();
            _mediaPlaybackTimer.Start();
            UpdateMediaCastControls();
            ReportMediaCastPlayback();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_hls_seek_restart",
                ("target", target.ToString("F3")),
                ("duration", _mediaProgramDuration.ToString("F3"))));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_hls_seek_restart_failed",
                ("target", target.ToString("F3")),
                ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
            QueueLiveMediaRecovery("HLS seek restart failed");
        }
    }

    private void UpdateMediaCastStatistics(MediaElement? mediaElement = null)
    {
        mediaElement ??= MediaCastMediaElement;
        _viewModel.UpdateMediaCastStatistics(
            (uint)Math.Max(0, mediaElement.NaturalVideoWidth),
            (uint)Math.Max(0, mediaElement.NaturalVideoHeight),
            !mediaElement.IsMuted && mediaElement.Volume > 0);
    }

    private void OnMediaPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_mediaSpeedFallbackPending &&
            DateTime.UtcNow - _mediaSpeedChangedUtc > TimeSpan.FromSeconds(5))
            _mediaSpeedFallbackPending = false;
        RetryPendingMediaCastSeek();
        if (_mediaCastActive && _mediaTimelineRunning &&
            !_mediaSeekInteraction && !_mediaSeekLoading)
        {
            // Keep the recovery origin on the same programme clock used by
            // both scrubbers. A later bridge reconnect must resume near the
            // visible position rather than the last explicit seek target.
            _mediaStartPosition = ReadMediaCastTimelinePosition();
        }
        if (_mediaIsLive && _mediaOpened)
        {
            // Keep the last known programme position across HLS segment
            // reloads. WMF may expose a newly-created element at position 0
            // before MediaOpened, so never let that transient value move the
            // sender's progress backwards.
            var current = ReadMediaCastPosition(MediaCastMediaElement);
            if (_mediaPendingSeekPosition is null)
            {
                if (_mediaPlaying && _mediaProgressSampleUtc != default)
                {
                    var elapsed = DateTime.UtcNow - _mediaProgressSampleUtc;
                    if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(15))
                        current = Math.Max(current,
                            _mediaStartPosition + elapsed.TotalSeconds);
                }
                RememberImplicitMediaProgress(current);
                _mediaProgressSampleUtc = DateTime.UtcNow;
            }
        }
        ReportMediaCastPlayback();
    }

    private void OnMediaOpeningTimerTick(object? sender, EventArgs e)
    {
        if (!_mediaCastActive || _mediaOpened || _mediaSource is null) {
            _mediaOpeningTimer.Stop();
            return;
        }
        if (DateTime.UtcNow - _mediaOpenedAtUtc < TimeSpan.FromSeconds(20)) return;
        _mediaOpeningTimer.Stop();
        var generation = _mediaCastEvents.CurrentGeneration;
        _viewModel.AddDiagnosticLog(AppLog.Event("media_open_timeout",
            ("generation", generation),
            ("source", AppLog.MediaSource(_mediaSource))));
        var message = "media source did not open within 20 seconds";
        _viewModel.AddUiLog(LocalizationService.Format(
            "MediaCastPlaybackFailedFormat", message));
        if (_mediaIsLive || _mediaUsesHlsBridge)
        {
            // A live bridge can remain alive while its input playlist/request
            // is stuck (for example when a provider rejects the first HTTP
            // request). Reusing that process only rebinds MediaElement to the
            // same dead listener and leaves the UI in Loading forever.
            DisposeHlsMediaBridge();
            _mediaPlaybackSource = null;
            QueueLiveMediaRecovery(message);
            return;
        }
        StopMediaCastPlayback("media_open_timeout");
    }

    private void ShowMediaCastStatus(string resourceKey)
    {
        MediaCastStatusText.Text = LocalizationService.Get(resourceKey);
        MediaCastStatusPanel.Visibility = Visibility.Visible;
    }

    private void ResetMediaCastTimelineClock()
    {
        _mediaTimelineAnchorPosition = 0;
        _mediaTimelineAnchorUtc = default;
        _mediaTimelineRunning = false;
    }

    private double ReadMediaCastTimelinePosition()
    {
        var position = _mediaTimelineAnchorPosition;
        if (_mediaTimelineRunning && _mediaTimelineAnchorUtc != default)
        {
            var elapsed = DateTime.UtcNow - _mediaTimelineAnchorUtc;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromDays(1))
                position += elapsed.TotalSeconds;
        }
        return ClampMediaPosition(position, clampToDuration: true);
    }

    private void SetMediaCastTimelinePosition(double position, bool running)
    {
        _mediaTimelineAnchorPosition = ClampMediaPosition(position,
            clampToDuration: true);
        _mediaTimelineAnchorUtc = DateTime.UtcNow;
        _mediaTimelineRunning = running;
        _mediaStartPosition = _mediaTimelineAnchorPosition;
        _mediaLastTimelinePosition = _mediaTimelineAnchorPosition;
    }

    private void SetMediaCastTimelineRunning(bool running)
    {
        var position = ReadMediaCastTimelinePosition();
        _mediaTimelineAnchorPosition = position;
        _mediaTimelineAnchorUtc = DateTime.UtcNow;
        _mediaTimelineRunning = running;
        _mediaStartPosition = position;
        _mediaLastTimelinePosition = position;
    }

    private void SynchronizeMediaCastTimelineClock()
    {
        var shouldRun = _mediaCastActive && _mediaShouldPlay && _mediaOpened &&
            !_mediaBuffering && !_mediaWaitingForFirstFrame &&
            !_mediaPendingHlsSeekPosition.HasValue;
        SetMediaCastTimelineRunning(shouldRun);
    }

    private double ReadMediaCastPosition(MediaElement mediaElement)
    {
        try
        {
            var position = mediaElement.Position.TotalSeconds;
            if (!double.IsFinite(position)) return 0;
            position = Math.Max(0, position);
            if (_mediaHlsBridge is not null)
                position += _mediaBridgeOffset;
            // WMF can return the bogus natural-duration endpoint for an HLS
            // element while a playlist is being opened or replaced. Expose
            // the last accepted position instead of advertising programme
            // completion to the sender.
            if (_mediaIsLive && position - _mediaStartPosition > 45)
            {
                if (Math.Abs(position - _lastRejectedMediaPosition) > 0.5)
                {
                    _lastRejectedMediaPosition = position;
                    _viewModel.AddDiagnosticLog(AppLog.Event(
                        "media_position_jump_ignored",
                        ("position", position.ToString("F3")),
                        ("saved_position", _mediaStartPosition.ToString("F3"))));
                }
                return Math.Max(0, _mediaStartPosition);
            }
            return position;
        }
        catch (InvalidOperationException)
        {
            return Math.Max(0, _mediaStartPosition);
        }
    }

    private void AdvanceImplicitMediaProgress(MediaElement mediaElement)
    {
        if (!_mediaIsLive || !_mediaOpened || !_mediaShouldPlay) return;
        var current = ReadMediaCastPosition(mediaElement);
        if (_mediaProgressSampleUtc != default)
        {
            var elapsed = DateTime.UtcNow - _mediaProgressSampleUtc;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(15))
                current = Math.Max(current, _mediaStartPosition + elapsed.TotalSeconds);
        }
        RememberImplicitMediaProgress(current);
        _mediaProgressSampleUtc = DateTime.UtcNow;
    }

    private bool RememberImplicitMediaProgress(double candidate)
    {
        if (!_mediaIsLive || !double.IsFinite(candidate) ||
            candidate <= _mediaStartPosition) return false;

        // A live HLS element normally advances by a few seconds between UI
        // ticks. WMF occasionally returns the playlist's bogus end position
        // (tens of thousands of seconds) while a segment is being reopened;
        // never turn that transient value into the next recovery seek target.
        const double maximumImplicitAdvanceSeconds = 45;
        if (candidate - _mediaStartPosition > maximumImplicitAdvanceSeconds)
        {
            if (Math.Abs(candidate - _lastRejectedMediaPosition) > 0.5)
            {
                _lastRejectedMediaPosition = candidate;
                _viewModel.AddDiagnosticLog(AppLog.Event(
                    "media_position_jump_ignored",
                    ("position", candidate.ToString("F3")),
                    ("saved_position", _mediaStartPosition.ToString("F3"))));
            }
            return false;
        }

        _lastRejectedMediaPosition = 0;
        _mediaStartPosition = candidate;
        return true;
    }

    private void BeginMediaCastPendingSeek(double target)
    {
        var now = DateTime.UtcNow;
        _mediaPendingSeekPosition = target;
        _mediaPendingSeekStartedUtc = now;
        _mediaPendingSeekLastAttemptUtc = now;
        _mediaPendingSeekAttemptCount = 1;
    }

    private void ClearMediaCastPendingHlsSeek()
    {
        _mediaPendingHlsSeekPosition = null;
        _mediaPendingHlsSeekStartedUtc = default;
    }

    private double? ReadMediaCastPendingHlsSeekPosition()
    {
        if (_mediaPendingHlsSeekPosition is not { } target) return null;
        if (!_mediaOpened) return target;

        var elapsed = DateTime.UtcNow - _mediaPendingHlsSeekStartedUtc;
        var actual = ReadMediaCastPosition(MediaCastMediaElement);
        // Do not release the target while the replacement is still buffering
        // or waiting for its first frame. The local element can briefly report
        // the segment origin at this point, which would make both sliders jump
        // back before playback is ready.
        if (Math.Abs(actual - target) <= 2 &&
            !_mediaBuffering && !_mediaWaitingForFirstFrame)
        {
            ClearMediaCastPendingHlsSeek();
            _mediaSeekLoading = false;
            SynchronizeMediaCastTimelineClock();
            return null;
        }
        if (elapsed < TimeSpan.FromSeconds(20)) return target;

        ClearMediaCastPendingHlsSeek();
        _mediaSeekLoading = false;
        SynchronizeMediaCastTimelineClock();
        return null;
    }

    private bool IsMediaCastTimelineLoading(double? pendingHlsSeek = null) =>
        !_mediaOpened || _mediaBuffering || _mediaWaitingForFirstFrame ||
        pendingHlsSeek.HasValue;

    private double ReadMediaCastLoadingPosition()
    {
        return ReadMediaCastTimelinePosition();
    }

    private void ClearMediaCastPendingSeek()
    {
        _mediaPendingSeekPosition = null;
        _mediaPendingSeekStartedUtc = default;
        _mediaPendingSeekLastAttemptUtc = default;
        _mediaPendingSeekAttemptCount = 0;
    }

    private void RetryPendingMediaCastSeek()
    {
        if (!_mediaCastActive || !_mediaOpened || _mediaSeekInteraction ||
            _mediaPendingSeekPosition is not { } target) return;

        var now = DateTime.UtcNow;
        var actual = ReadMediaCastPosition(MediaCastMediaElement);
        if (!MediaCastPlaybackControls.ShouldRetryPendingSeek(
                actual, target, now - _mediaPendingSeekLastAttemptUtc,
                _mediaPendingSeekAttemptCount, _mediaBuffering)) return;

        // Count a rejected attempt as well so an unavailable WMF backend can
        // never cause an unbounded retry loop on the UI thread.
        _mediaPendingSeekLastAttemptUtc = now;
        ++_mediaPendingSeekAttemptCount;
        try
        {
            MediaCastMediaElement.Position = TimeSpan.FromSeconds(target);
            _viewModel.AddDiagnosticLog(AppLog.Event("media_local_seek_retry",
                ("target", target.ToString("F3")),
                ("actual", actual.ToString("F3")),
                ("attempt", _mediaPendingSeekAttemptCount)));
        }
        catch (InvalidOperationException error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_local_seek_retry_failed",
                ("target", target.ToString("F3")),
                ("attempt", _mediaPendingSeekAttemptCount),
                ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
        }
    }

    private double ReadMediaCastControlPosition(MediaElement mediaElement)
    {
        _ = ReadMediaCastPendingHlsSeekPosition();
        if (_mediaPendingSeekPosition is { } pending)
        {
            var actual = ReadMediaCastPosition(mediaElement);
            if (!MediaCastPlaybackControls.ShouldRetainPendingSeek(actual, pending,
                    DateTime.UtcNow - _mediaPendingSeekStartedUtc))
            {
                ClearMediaCastPendingSeek();
                _mediaSeekLoading = false;
                SynchronizeMediaCastTimelineClock();
            }
        }
        return ReadMediaCastTimelinePosition();
    }

    private double ReadMediaCastDuration(MediaElement mediaElement)
    {
        if (_mediaProgramDuration > 0) return _mediaProgramDuration;
        try
        {
            return mediaElement.NaturalDuration.HasTimeSpan
                ? Math.Max(0, mediaElement.NaturalDuration.TimeSpan.TotalSeconds) : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private void UpdateMediaCastControls(MediaElement? mediaElement = null)
    {
        if (_updatingMediaCastControls) return;
        mediaElement ??= MediaCastMediaElement;
        var pendingHlsSeek = ReadMediaCastPendingHlsSeekPosition();
        var timelineLoading = IsMediaCastTimelineLoading(pendingHlsSeek) ||
            _mediaSeekLoading;
        var actualPosition = _mediaOpened
            ? ReadMediaCastPosition(mediaElement) : Math.Max(0, _mediaStartPosition);
        // The slider and controller always use the programme clock. The
        // MediaElement position is intentionally read only for open/seek
        // health checks because HLS replacement timestamps are discontinuous.
        var position = ReadMediaCastTimelinePosition();
        var naturalDuration = _mediaOpened ? ReadMediaCastDuration(mediaElement) :
            _mediaProgramDuration;
        // Keep a known programme duration visible during HLS replacement;
        // otherwise the slider temporarily collapses to Maximum=1 and Value=0
        // until FFmpeg reports the same duration again.
        var duration = _mediaProgramDuration > 0
            ? _mediaProgramDuration
            : _mediaIsLive ? 0 : naturalDuration;
        var canSeek = MediaCastPlaybackControls.CanSeek(
            _mediaOpened, _mediaIsLive, duration);
        // During a HLS replacement the MediaElement is intentionally not
        // seekable yet, but a known programme duration still gives the slider
        // a stable scale and lets it retain the requested target visually.
        var hasTimeline = double.IsFinite(duration) && duration > 0;
        var timelineDuration = hasTimeline ? duration : _mediaLastTimelineDuration;
        // During either kind of seek hand-off, the requested programme
        // position is authoritative. WMF may expose the new segment's local
        // timestamp before its first frame; allowing that value through here
        // is the source of the visible thumb jump.
        var pendingProgrammePosition = pendingHlsSeek ??
            _mediaPendingSeekPosition;
        var timelinePosition = pendingProgrammePosition is { } requested
            ? Math.Clamp(requested, 0, timelineDuration > 0
                ? timelineDuration : duration)
            : hasTimeline
                ? Math.Clamp(position, 0, duration)
                : timelineDuration > 0
                    ? Math.Clamp(_mediaLastTimelinePosition, 0, timelineDuration)
                    : 0;
        if (hasTimeline && !_mediaSeekInteraction && !timelineLoading)
        {
            _mediaLastTimelineDuration = duration;
            _mediaLastTimelinePosition = timelinePosition;
        }

        if (_mediaWaitingForFirstFrame && _mediaOpened &&
            MediaCastPlaybackControls.ShouldRevealVideo(_mediaShouldPlay,
                _mediaBuffering, _mediaOpeningPosition, actualPosition,
                DateTime.UtcNow - _mediaOpenedAtUtc))
        {
            if (pendingProgrammePosition is null &&
                actualPosition >= _mediaOpeningPosition &&
                actualPosition - _mediaOpeningPosition <= 30)
            {
                SetMediaCastTimelinePosition(actualPosition, running: false);
            }
            _mediaWaitingForFirstFrame = false;
            SynchronizeMediaCastTimelineClock();
            if (_mediaShouldPlay)
                StartMediaCastAudioAt(ReadMediaCastTimelinePosition());
            if (!_mediaBuffering)
                MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        }

        _updatingMediaCastControls = true;
        try
        {
            MediaCastControlsPanel.IsEnabled = _mediaCastActive;
            MediaCastPlayPauseButton.IsEnabled = _mediaCastActive;
            MediaCastMuteButton.IsEnabled = _mediaCastActive;
            MediaCastSpeedComboBox.IsEnabled = _mediaCastActive;
            MediaCastVolumeSlider.IsEnabled = _mediaCastActive;
            // Keep the Slider itself enabled while the HLS element is being
            // replaced. Disabling it causes WPF to revoke mouse capture and
            // can turn a normal drag into a second, stale seek transaction.
            // Hit testing is the loading lock; the value/maximum remain stable
            // programme-time coordinates throughout the replacement.
            var sliderMaximum = timelineDuration > 0 ? timelineDuration : 1;
            var sliderCanBeUsed = canSeek && !_mediaSeekLoading &&
                !timelineLoading;
            MediaCastSeekSlider.IsEnabled = _mediaCastActive &&
                timelineDuration > 0;
            // Lock hit testing while loading so Slider's class handler cannot
            // move the Thumb before our guard runs. Keep it hit-testable for
            // an already active drag; MouseUp will finish that transaction
            // and the next sync will apply the loading lock.
            MediaCastSeekSlider.IsHitTestVisible = sliderCanBeUsed ||
                _mediaSeekInteraction;
            MediaCastSeekBackwardButton.IsEnabled = sliderCanBeUsed;
            MediaCastSeekForwardButton.IsEnabled = sliderCanBeUsed;
            if (Math.Abs(MediaCastSeekSlider.Maximum - sliderMaximum) > 0.001)
            {
                MediaCastSeekSlider.Maximum = sliderMaximum;
                _viewModel.AddDiagnosticLog(AppLog.Event("maximum_changed",
                    ("maximum", sliderMaximum.ToString("F3")),
                    ("value", MediaCastSeekSlider.Value.ToString("F3")),
                    ("interaction", _mediaSeekInteraction),
                    ("seek_loading", _mediaSeekLoading),
                    ("opened", _mediaOpened)));
            }
            if (!_mediaSeekInteraction)
            {
                var stableValue = Math.Clamp(timelinePosition, 0, sliderMaximum);
                if (Math.Abs(MediaCastSeekSlider.Value - stableValue) > 0.001)
                {
                    MediaCastSeekSlider.Value = stableValue;
                    // Log only meaningful programmatic moves. Normal 250 ms
                    // clock ticks are intentionally omitted from diagnostics.
                    if (_mediaSeekLoading ||
                        double.IsNaN(_lastSeekSliderSyncPosition) ||
                        Math.Abs(_lastSeekSliderSyncPosition - stableValue) > 2)
                    {
                        _viewModel.AddDiagnosticLog(AppLog.Event("seek_slider_sync",
                            ("value", stableValue.ToString("F3")),
                            ("target", _mediaSeekInteractionTarget.ToString("F3")),
                            ("maximum", sliderMaximum.ToString("F3")),
                            ("interaction", _mediaSeekInteraction),
                            ("seek_loading", _mediaSeekLoading),
                            ("pending_hls", _mediaPendingHlsSeekPosition.HasValue),
                            ("opened", _mediaOpened),
                            ("buffering", _mediaBuffering)));
                    }
                    _lastSeekSliderSyncPosition = stableValue;
                }
            }

            var displayPosition = _mediaSeekInteraction && canSeek
                ? _mediaSeekInteractionTarget
                : timelinePosition;
            MediaCastCurrentTimeText.Text =
                MediaCastPlaybackControls.FormatTime(displayPosition);
            MediaCastDurationText.Text = timelineDuration > 0
                ? MediaCastPlaybackControls.FormatTime(timelineDuration)
                : _mediaOpened && _mediaIsLive
                    ? LocalizationService.Get("MediaCastLive") : "--:--";

            SetAnimatedMediaSymbol(MediaCastPlayPauseIcon,
                _mediaShouldPlay ? SymbolRegular.Pause20 : SymbolRegular.Play20);
            MediaCastPlayPauseButton.ToolTip = LocalizationService.Get(
                _mediaShouldPlay ? "MediaCastPause" : "MediaCastPlay");

            var muted = mediaElement.IsMuted || mediaElement.Volume <= 0;
            SetAnimatedMediaSymbol(MediaCastVolumeIcon,
                muted ? SymbolRegular.SpeakerMute20 : SymbolRegular.Speaker220);
            MediaCastMuteButton.ToolTip = LocalizationService.Get(
                muted ? "MediaCastUnmute" : "MediaCastMute");
            var speedIndex = GetMediaCastSpeedIndex(_mediaPlaybackSpeed);
            if (MediaCastSpeedComboBox.SelectedIndex != speedIndex)
                MediaCastSpeedComboBox.SelectedIndex = speedIndex;
            MediaCastVolumeSlider.Value = Math.Clamp(mediaElement.Volume * 100, 0, 100);
        }
        finally
        {
            _updatingMediaCastControls = false;
        }

        if (!_mediaShouldPlay || _mediaBuffering || _mediaWaitingForFirstFrame)
            RevealMediaCastControls(scheduleAutoHide: false);
        else if (_mediaControlsVisible && !_mediaControlsHideTimer.IsEnabled)
            ScheduleMediaCastControlsAutoHide();
    }

    private static void SetAnimatedMediaSymbol(SymbolIcon icon, SymbolRegular symbol)
    {
        if (icon.Symbol == symbol) return;
        icon.Symbol = symbol;
        if (!SystemParameters.ClientAreaAnimation) return;
        icon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.42, 1,
            TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnMediaControlsHideTimerTick(object? sender, EventArgs e)
    {
        _mediaControlsHideTimer.Stop();
        if (!_mediaCastActive || !_mediaShouldPlay || _mediaBuffering ||
            _mediaWaitingForFirstFrame || _mediaSeekInteraction ||
            MediaCastControlsPanel.IsMouseOver)
        {
            if (_mediaCastActive && _mediaShouldPlay)
                _mediaControlsHideTimer.Start();
            return;
        }
        SetMediaCastControlsVisible(false, animate: true);
    }

    private void RevealMediaCastControls(bool scheduleAutoHide = true)
    {
        SetMediaCastControlsVisible(true, animate: true);
        if (scheduleAutoHide) ScheduleMediaCastControlsAutoHide();
        else _mediaControlsHideTimer.Stop();
    }

    private void ScheduleMediaCastControlsAutoHide()
    {
        _mediaControlsHideTimer.Stop();
        if (!_mediaCastActive || !_mediaShouldPlay || _mediaBuffering ||
            _mediaWaitingForFirstFrame || _mediaSeekInteraction) return;
        _mediaControlsHideTimer.Start();
    }

    private void SetMediaCastControlsVisible(bool visible, bool animate)
    {
        if (_mediaControlsVisible == visible &&
            Math.Abs(MediaCastControlsPanel.Opacity - (visible ? 1 : 0)) < 0.01)
            return;
        _mediaControlsVisible = visible;
        MediaCastControlsPanel.IsHitTestVisible = visible;
        var target = visible ? 1d : 0d;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            MediaCastControlsPanel.BeginAnimation(OpacityProperty, null);
            MediaCastControlsPanel.Opacity = target;
            return;
        }
        MediaCastControlsPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(visible ? 140 : 190))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            }, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnMediaCastPlayerMouseEnter(object sender, MouseEventArgs e) =>
        RevealMediaCastControls();

    private void OnMediaCastPlayerMouseMove(object sender, MouseEventArgs e) =>
        RevealMediaCastControls();

    private void OnMediaCastPlayerMouseLeave(object sender, MouseEventArgs e) =>
        ScheduleMediaCastControlsAutoHide();

    private void OnMediaCastPlayerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var showSkipButtons = width >= 430;
        var showVolumeSlider = width >= 620;
        var showPlaybackSpeed = width >= 560;
        MediaCastSeekBackwardButton.Visibility = showSkipButtons
            ? Visibility.Visible : Visibility.Collapsed;
        MediaCastSeekForwardButton.Visibility = showSkipButtons
            ? Visibility.Visible : Visibility.Collapsed;
        MediaCastVolumeSlider.Visibility = showVolumeSlider
            ? Visibility.Visible : Visibility.Collapsed;
        MediaCastSpeedComboBox.Visibility = showPlaybackSpeed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetMediaCastControls()
    {
        ClearMediaCastPendingSeek();
        _mediaControlsHideTimer.Stop();
        SetMediaCastControlsVisible(true, animate: false);
        _updatingMediaCastControls = true;
        try
        {
            MediaCastStatusPanel.Visibility = Visibility.Collapsed;
            MediaCastControlsPanel.IsEnabled = false;
            MediaCastSeekSlider.IsEnabled = false;
            MediaCastSeekSlider.IsHitTestVisible = false;
            MediaCastSeekSlider.Maximum = 1;
            MediaCastSeekSlider.Value = 0;
            MediaCastSeekBackwardButton.IsEnabled = false;
            MediaCastSeekForwardButton.IsEnabled = false;
            MediaCastPlayPauseButton.IsEnabled = false;
            MediaCastMuteButton.IsEnabled = false;
            MediaCastSpeedComboBox.IsEnabled = false;
            MediaCastSpeedComboBox.SelectedIndex = 2;
            MediaCastVolumeSlider.IsEnabled = false;
            MediaCastCurrentTimeText.Text = "00:00";
            MediaCastDurationText.Text = "--:--";
            MediaCastPlayPauseIcon.Symbol = SymbolRegular.Play20;
            MediaCastVolumeIcon.Symbol = SymbolRegular.Speaker220;
            _mediaPlaybackSpeed = 1.0;
            _mediaSpeedFallbackPending = false;
            _mediaSpeedFallbackPromptShown = false;
            MediaCastVolumeSlider.Value = 100;
        }
        finally
        {
            _updatingMediaCastControls = false;
        }
    }

    private void SetLocalMediaCastPlayback(bool shouldPlay)
    {
        if (!_mediaCastActive) return;
        try
        {
            _mediaShouldPlay = shouldPlay;
            SetMediaCastTimelineRunning(false);
            if (_mediaOpened)
            {
                if (shouldPlay) MediaCastMediaElement.Play();
                else MediaCastMediaElement.Pause();
            }
            if (shouldPlay) RestartMediaCastAudioAtCurrentPosition();
            else _mediaCastAudioDecoder.Stop();
            _mediaPlaying = shouldPlay && _mediaOpened;
            SynchronizeMediaCastTimelineClock();
            UpdateMediaCastControls();
            if (_mediaOpened) ReportMediaCastPlayback();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_local_playback",
                ("playing", shouldPlay),
                ("position", ReadMediaCastPosition(MediaCastMediaElement).ToString("F3"))));
        }
        catch (InvalidOperationException error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_local_playback_failed",
                ("playing", shouldPlay),
                ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
        }
    }

    private void RestartMediaCastAudioAtCurrentPosition()
    {
        if (!_mediaCastActive || !_mediaShouldPlay || _mediaSource is null) return;
        StartMediaCastAudioAt(ReadMediaCastTimelinePosition());
    }

    private void StartMediaCastAudioAt(double position)
    {
        if (!_mediaCastActive || !_mediaShouldPlay || _mediaSource is null) return;
        _mediaCastAudioDecoder.Start(_mediaSource, position, _mediaPlaybackSpeed,
            message => _viewModel.AddDiagnosticLog(AppLog.Event("media_audio",
                ("message", AppLog.Error(message)))));
    }

    private void SeekMediaCastLocally(double requestedPosition)
    {
        if (!_mediaCastActive || !_mediaOpened || _mediaIsLive ||
            _mediaBuffering || _mediaWaitingForFirstFrame ||
            _mediaPendingHlsSeekPosition.HasValue || _mediaSeekLoading)
        {
            LogMediaSeekDiagnostic("seek_commit_ignored", requestedPosition);
            return;
        }
        var duration = ReadMediaCastDuration(MediaCastMediaElement);
        if (!MediaCastPlaybackControls.CanSeek(_mediaOpened, _mediaIsLive, duration))
            return;
        var target = MediaCastPlaybackControls.ClampPosition(
            requestedPosition, duration);
        LogMediaSeekDiagnostic("seek_commit", target);
        SeekMediaCastToPosition(target);
    }

    private bool IsLikelyMediaCastStartupSeek(double target)
    {
        if (!_mediaCastActive || !_mediaUsesHlsBridge ||
            DateTime.UtcNow - _mediaOpenedAtUtc > TimeSpan.FromSeconds(8))
            return false;
        // The sender can issue its initial one-second correction before WPF
        // raises MediaOpened. Compare against the programme clock instead of
        // the not-yet-open local element so that correction is coalesced and
        // cannot restart the HLS bridge from the beginning.
        var current = ReadMediaCastTimelinePosition();
        if (!double.IsFinite(current) || Math.Abs(target - current) > 8)
            return false;
        // iQIYI commonly reports 1-3 seconds after the first frame even when
        // the programme was opened at zero. Keep a small acknowledgement
        // window so a user can still issue a real phone seek immediately
        // after casting without being coalesced into the startup correction.
        return Math.Abs(target - _mediaOpeningPosition) <= 4;
    }

    private void OnMediaCastPlayPauseClick(object sender, RoutedEventArgs e) =>
        SetLocalMediaCastPlayback(!_mediaShouldPlay);

    private void OnMediaCastSeekBackwardClick(object sender, RoutedEventArgs e) =>
        SeekMediaCastLocally(ReadMediaCastControlPosition(MediaCastMediaElement) - 10);

    private void OnMediaCastSeekForwardClick(object sender, RoutedEventArgs e) =>
        SeekMediaCastLocally(ReadMediaCastControlPosition(MediaCastMediaElement) + 10);

    private void OnMediaCastSeekPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pendingHlsSeek = ReadMediaCastPendingHlsSeekPosition();
        var duration = _mediaProgramDuration > 0
            ? _mediaProgramDuration : ReadMediaCastDuration(MediaCastMediaElement);
        var canInteract = _mediaCastActive && _mediaOpened && !_mediaIsLive &&
            !_mediaBuffering && !_mediaWaitingForFirstFrame &&
            !_mediaSeekLoading && !pendingHlsSeek.HasValue &&
            MediaCastPlaybackControls.CanSeek(_mediaOpened, _mediaIsLive, duration);
        if (!MediaCastSeekSlider.IsEnabled || !canInteract)
        {
            LogMediaSeekDiagnostic("seek_pointer_down_ignored");
            e.Handled = true;
            return;
        }
        RevealMediaCastControls(scheduleAutoHide: false);
        _mediaSeekInteraction = true;
        _mediaSeekCommitPending = true;
        _mediaSeekInteractionTarget = MediaCastSeekSlider.Value;
        LogMediaSeekDiagnostic("seek_pointer_down");
        // Own the complete pointer transaction. WPF's Slider/Thumb class
        // handlers can otherwise capture the Thumb and write Value a second
        // time after the track calculation, which is the source of the
        // visible jump while a seek is loading. The media timeline is updated
        // from one proportional target until MouseUp commits it once.
        _mediaSeekTrackInteraction = true;
        e.Handled = true;
        UpdateMediaCastSeekFromPointer(e);
        MediaCastSeekSlider.CaptureMouse();
    }

    private void OnMediaCastSeekPointerMove(object sender, MouseEventArgs e)
    {
        if (!_mediaSeekInteraction || !_mediaSeekTrackInteraction) return;
        UpdateMediaCastSeekFromPointer(e);
        e.Handled = true;
    }

    private void OnMediaCastSeekPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!_mediaSeekInteraction) return;
        var target = _mediaSeekInteractionTarget;
        var trackInteraction = _mediaSeekTrackInteraction;
        _mediaSeekInteraction = false;
        _mediaSeekCommitPending = false;
        _mediaSeekTrackInteraction = false;
        if (trackInteraction && Mouse.Captured == MediaCastSeekSlider)
            Mouse.Capture(null);
        e.Handled = trackInteraction;
        LogMediaSeekDiagnostic("seek_pointer_up", target);
        SeekMediaCastLocally(target);
        ScheduleMediaCastControlsAutoHide();
    }

    private void OnMediaCastSeekLostCapture(object sender, MouseEventArgs e)
    {
        if (!_mediaSeekInteraction || !_mediaSeekCommitPending) return;
        _mediaSeekInteraction = false;
        _mediaSeekCommitPending = false;
        _mediaSeekTrackInteraction = false;
        // Lost capture is cleanup only. WPF raises it when a template is
        // reloaded or focus moves; committing here submits whatever stale
        // value happened to be in the Slider at that instant and causes the
        // visible thumb to jump. A real release is committed by MouseUp.
        LogMediaSeekDiagnostic("seek_lost_capture", _mediaSeekInteractionTarget);
        UpdateMediaCastControls();
        ScheduleMediaCastControlsAutoHide();
    }

    private void UpdateMediaCastSeekFromPointer(MouseEventArgs e)
    {
        // Match the visual rail geometry: WPF positions the Thumb by its
        // centre, so the first/last reachable centres are half a Thumb in from
        // the rail edges. Using the whole Slider width makes edge clicks miss
        // by several seconds on a long programme.
        MediaCastSeekSlider.ApplyTemplate();
        if (MediaCastSeekSlider.Template.FindName("PART_Track",
                MediaCastSeekSlider) is not Track track) return;
        var width = track.ActualWidth;
        var thumbWidth = track.Thumb?.ActualWidth ?? 0;
        if (!double.IsFinite(width) || width <= 0) return;
        if (!double.IsFinite(thumbWidth) || thumbWidth < 0) thumbWidth = 0;
        var railStart = thumbWidth / 2;
        var railEnd = Math.Max(railStart, width - railStart);
        var point = e.GetPosition(track);
        var ratio = railEnd <= railStart
            ? 0
            : Math.Clamp((point.X - railStart) / (railEnd - railStart), 0, 1);
        var target = MediaCastSeekSlider.Minimum +
            (MediaCastSeekSlider.Maximum - MediaCastSeekSlider.Minimum) * ratio;
        if (!double.IsFinite(target)) return;
        var value = MediaCastPlaybackControls.ClampPosition(
            target, MediaCastSeekSlider.Maximum);
        _mediaSeekInteractionTarget = value;
        MediaCastSeekSlider.Value = value;
    }

    private void OnMediaCastSeekValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMediaCastControls || !MediaCastSeekSlider.IsEnabled) return;
        if (_mediaSeekInteraction)
            _mediaSeekInteractionTarget = MediaCastPlaybackControls.ClampPosition(
                e.NewValue, MediaCastSeekSlider.Maximum);
        MediaCastCurrentTimeText.Text = MediaCastPlaybackControls.FormatTime(e.NewValue);
        // ValueChanged is also raised for every programmatic sync while an
        // HLS element is opening. Never treat that notification as a seek;
        // explicit mouse-up or keyboard-up handlers below are the only commit
        // points. This prevents a stale focused slider from restarting HLS
        // during loading and making its thumb jump between positions.
    }

    private void OnMediaCastSeekKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or
            Key.PageUp or Key.PageDown or Key.Home or Key.End)) return;
        if (_mediaSeekInteraction || !MediaCastSeekSlider.IsKeyboardFocusWithin)
            return;
        var target = MediaCastSeekSlider.Value;
        SeekMediaCastLocally(target);
    }

    private void LogMediaSeekDiagnostic(string eventName, double? target = null)
    {
        _viewModel.AddDiagnosticLog(AppLog.Event(eventName,
            ("value", MediaCastSeekSlider.Value.ToString("F3")),
            ("target", (target ?? _mediaSeekInteractionTarget).ToString("F3")),
            ("maximum", MediaCastSeekSlider.Maximum.ToString("F3")),
            ("interaction", _mediaSeekInteraction),
            ("track_interaction", _mediaSeekTrackInteraction),
            ("commit_pending", _mediaSeekCommitPending),
            ("seek_loading", _mediaSeekLoading),
            ("pending_hls", _mediaPendingHlsSeekPosition.HasValue),
            ("opened", _mediaOpened),
            ("buffering", _mediaBuffering)));
    }

    private void OnMediaCastMuteClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaCastActive) return;
        MediaCastMediaElement.IsMuted = !MediaCastMediaElement.IsMuted;
        _viewModel.UpdateMediaCastAudioControls(!MediaCastMediaElement.IsMuted,
            MediaCastMediaElement.Volume);
        UpdateMediaCastStatistics();
        UpdateMediaCastControls();
    }

    private void OnMediaCastVolumeValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMediaCastControls || !_mediaCastActive) return;
        var volume = Math.Clamp(e.NewValue / 100, 0, 1);
        MediaCastMediaElement.Volume = volume;
        _viewModel.UpdateMediaCastAudioControls(
            !MediaCastMediaElement.IsMuted, volume);
        UpdateMediaCastStatistics();
        UpdateMediaCastControls();
    }

    private void OnMediaCastSpeedSelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingMediaCastControls || !_mediaCastActive ||
            MediaCastSpeedComboBox.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var speed) ||
            !double.IsFinite(speed) || speed <= 0)
            return;
        try
        {
            var requested = Math.Clamp(speed, 0.5, 2.0);
            _mediaSpeedFallbackPromptShown = false;
            if (!ApplyMediaCastSpeed(MediaCastMediaElement, requested))
            {
                _mediaPlaybackSpeed = 1.0;
                _mediaSpeedFallbackPending = false;
                MediaCastSpeedComboBox.SelectedIndex = 2;
                NotifyMediaCastSpeedFallback(requested);
                return;
            }
            _mediaPlaybackSpeed = requested;
            _mediaSpeedFallbackPending = requested != 1.0;
            _mediaSpeedChangedUtc = DateTime.UtcNow;
            if (_mediaShouldPlay) RestartMediaCastAudioAtCurrentPosition();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_speed_applied",
                ("speed", _mediaPlaybackSpeed.ToString("F2",
                    CultureInfo.InvariantCulture)),
                ("opened", _mediaOpened), ("playing", _mediaShouldPlay)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_speed_failed",
                ("speed", speed.ToString("F2", CultureInfo.InvariantCulture)),
                ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
            UpdateMediaCastControls();
        }
    }

    private bool ApplyMediaCastSpeed(MediaElement mediaElement, double speed)
    {
        speed = Math.Clamp(speed, 0.5, 2.0);
        var resume = _mediaOpened && _mediaShouldPlay;
        try
        {
            if (resume) mediaElement.Pause();
            mediaElement.SpeedRatio = speed;
            if (resume) mediaElement.Play();
            return true;
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_speed_failed",
                ("speed", speed.ToString("F2", CultureInfo.InvariantCulture)),
                ("error", AppLog.Error(SanitizeMediaError(error.Message)))));
            try { mediaElement.SpeedRatio = 1.0; }
            catch (InvalidOperationException) { }
            return false;
        }
    }

    private void NotifyMediaCastSpeedFallback(double requestedSpeed)
    {
        if (_mediaSpeedFallbackPromptShown) return;
        _mediaSpeedFallbackPromptShown = true;
        _viewModel.AddUiLog(LocalizationService.Format(
            "MediaCastSpeedUnsupportedBody", $"{requestedSpeed:0.##}x"));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_mediaCastActive)
                AppPromptWindow.Inform(
                    LocalizationService.Get("MediaCastSpeedUnsupportedTitle"),
                    LocalizationService.Format(
                        "MediaCastSpeedUnsupportedBody", $"{requestedSpeed:0.##}x"));
        });
    }

    private static int GetMediaCastSpeedIndex(double speed)
    {
        var speeds = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        var index = 0;
        var distance = double.PositiveInfinity;
        for (var i = 0; i < speeds.Length; ++i)
        {
            var candidateDistance = Math.Abs(speed - speeds[i]);
            if (candidateDistance >= distance) continue;
            distance = candidateDistance;
            index = i;
        }
        return index;
    }

    private void QueueMediaCastCompletion()
    {
        var generation = _mediaCastEvents.CurrentGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_mediaCastActive && generation == _mediaCastEvents.CurrentGeneration)
                _viewModel.RequestMediaCastStop();
        });
    }

    private string SanitizeMediaError(string message)
    {
        if (_mediaSource is not null)
            message = message.Replace(_mediaSource.AbsoluteUri, "<media-url>",
                StringComparison.OrdinalIgnoreCase);
        return AppLog.Sanitize(message);
    }

    private void QueueLiveMediaRecovery(string reason)
    {
        if (!_mediaCastActive || (!_mediaIsLive && !_mediaUsesHlsBridge) ||
            _mediaSource is null) return;
        var generation = _mediaCastEvents.CurrentGeneration;
        var revision = ++_mediaRecoveryRevision;
        var source = _mediaSource;
        if (!_mediaRecoveryBackoff.TryGetNext(out var attempt, out var delay))
        {
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastPlaybackFailedFormat", AppLog.Error(reason)));
            _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_exhausted",
                ("generation", generation), ("revision", revision),
                ("attempts", attempt), ("source", AppLog.MediaSource(source)),
                ("reason", AppLog.Error(reason))));
            QueueMediaCastCompletion();
            return;
        }
        _viewModel.AddUiLog(AppLog.Event("live media reconnect",
            ("delay_ms", delay.TotalMilliseconds.ToString("F0")),
            ("attempt", attempt), ("reason", AppLog.Error(reason))));
        _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_queued",
            ("generation", generation), ("revision", revision),
            ("attempt", attempt), ("delay_ms", delay.TotalMilliseconds.ToString("F0")),
            ("source", AppLog.MediaSource(source)), ("reason", AppLog.Error(reason))));
        _ = RecoverLiveMediaAsync(generation, revision, source, delay,
            _mediaRecoveryCancellation.Token);
    }

    private async Task RecoverLiveMediaAsync(
        long generation, int revision, Uri source, TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (_shutdownStarted || !_mediaCastActive ||
            (!_mediaIsLive && !_mediaUsesHlsBridge) ||
            generation != _mediaCastEvents.CurrentGeneration ||
            revision != _mediaRecoveryRevision ||
            !Equals(source, _mediaSource)) return;

        _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_begin",
            ("generation", generation), ("revision", revision),
            ("delay_ms", delay.TotalMilliseconds.ToString("F0")),
            ("source", AppLog.MediaSource(source))));
        try
        {
            // Reloading is reserved for live-stream recovery. User Seek/Resume
            // commands continue to operate directly on the existing MediaElement.
            var audioEnabled = !MediaCastMediaElement.IsMuted;
            var volume = MediaCastMediaElement.Volume;
            if (_mediaHlsBridge is null || !_mediaHlsBridge.IsRunning)
            {
                _mediaCastAudioDecoder.Stop();
                DisposeHlsMediaBridge();
                _mediaBridgeOffset = Math.Max(0, _mediaStartPosition);
                _mediaPlaybackSource = source;
                _mediaHlsBridge = HlsMediaPlaybackBridge.TryStart(source,
                    _mediaBridgeOffset,
                    message => _viewModel.AddDiagnosticLog(AppLog.Event("hls_bridge",
                        ("message", AppLog.Error(message)))),
                    duration => QueueHlsProgramDuration(
                        generation, source, duration));
                if (_mediaHlsBridge is not null)
                    _mediaPlaybackSource = _mediaHlsBridge.PlaybackUri;
            }
            _mediaOpened = false;
            // Keep the sender's transport in PLAYING while the next HLS
            // window is being opened. The local MediaElement is temporarily
            // closed, but this is a segment hand-off rather than a programme
            // completion.
            _mediaPlaying = _mediaShouldPlay;
            _mediaBuffering = false;
            _mediaWaitingForFirstFrame = true;
            _mediaOpeningPosition = ClampMediaPosition(
                _mediaStartPosition, clampToDuration: true);
            _mediaOpenedAtUtc = DateTime.UtcNow;
            _mediaProgressSampleUtc = _mediaOpenedAtUtc;
            ShowMediaCastStatus("MediaCastLoadingVideo");
            _mediaOpeningTimer.Start();
            if (!ReplaceMediaCastMediaElement(
                    _mediaPlaybackSource ?? source, generation,
                    audioEnabled, volume)) return;
            if (_mediaShouldPlay) MediaCastMediaElement.Play();
            else MediaCastMediaElement.Pause();
            _mediaPlaybackTimer.Start();
            UpdateMediaCastControls();
            _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_submitted",
                ("generation", generation), ("revision", revision),
                ("source", AppLog.MediaSource(source))));
        }
        catch (Exception error)
        {
            var message = SanitizeMediaError(error.Message);
            _viewModel.AddDiagnosticLog(AppLog.Event("media_recovery_failed",
                ("generation", generation), ("revision", revision),
                ("source", AppLog.MediaSource(source)),
                ("error", AppLog.Error(message, error.GetType().Name))));
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastLiveRecoveringFormat", message));
            QueueLiveMediaRecovery(message);
        }
    }

    private void ResetMediaRecoveryCancellation()
    {
        var previous = _mediaRecoveryCancellation;
        _mediaRecoveryCancellation = new CancellationTokenSource();
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
    }

    private void CancelMediaRecovery()
    {
        try { _mediaRecoveryCancellation.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A replacement Play can dispose the previous generation while a
            // delayed continuation is unwinding.
        }
    }

    private void OnRefreshPreviewClick(object sender, RoutedEventArgs e) => RefreshPreview();

    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastVersionClickUtc).TotalSeconds > 2) _versionClickCount = 0;
        _lastVersionClickUtc = now;
        if (++_versionClickCount < 5) return;
        _versionClickCount = 0;
        _viewModel.EnableAdvancedMode();
        _viewModel.AddUiLog(LocalizationService.Get("AdvancedModeEnabled"));
        OpenDeveloperTools();
    }

    private void OpenDeveloperTools()
    {
        // Developer tools must remain a regular, non-topmost inspection window.
        Topmost = false;
        if (_developerToolsWindow is not null)
        {
            _developerToolsWindow.Topmost = false;
            _developerToolsWindow.Activate();
            _developerToolsWindow.Focus();
            return;
        }
        try
        {
            // Keep this as an independent window. WPF owned windows are always
            // kept above their owner, which prevents the main window covering
            // the developer tools during layout and z-order inspection.
            var window = new DeveloperToolsWindow(this) { Topmost = false };
            _developerToolsWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_developerToolsWindow, window))
                    _developerToolsWindow = null;
            };
            window.Show();
            window.Activate();
        }
        catch (Exception error)
        {
            _developerToolsWindow = null;
            DiagnosticLogger.Exception("ui", "developer_tools_open_failed", error);
            AppPromptWindow.Inform(
                LocalizationService.Get("DeveloperToolsTitle"), error.Message);
        }
    }

    internal void OpenDeveloperSurface(string key)
    {
        switch (key)
        {
            case "workspace-mirroring":
                OnNavigateMirroringClick(this, new RoutedEventArgs());
                break;
            case "workspace-devices":
                OnNavigateDevicesClick(this, new RoutedEventArgs());
                break;
            case "workspace-settings":
                OnNavigateSettingsClick(this, new RoutedEventArgs());
                break;
            case "workspace-output":
                OnNavigateOutputClick(this, new RoutedEventArgs());
                break;
            case "driver-manager":
                OnNavigateDriverClick(this, new RoutedEventArgs());
                break;
            case "about":
                OnAboutClick(this, new RoutedEventArgs());
                break;
            case "advanced-settings":
                new AdvancedSettingsWindow(1920, 1080, previewOnly: true)
                    { Owner = this }.Show();
                break;
            case "prompt":
                AppPromptWindow.ShowDeveloperPreview(this);
                break;
            case "reverse-control-wired-prerequisite":
                AppPromptWindow.ShowReverseControlPrerequisitePreview(this, wireless: false);
                break;
            case "reverse-control-wireless-prerequisite":
                AppPromptWindow.ShowReverseControlPrerequisitePreview(this, wireless: true);
                break;
            case "reverse-control-error":
                CaptureStatusNoticeWindow.ShowDeveloperReverseControlErrorPreview(this);
                break;
            case "capture-error":
                CaptureStatusNoticeWindow.ShowDeveloperErrorPreview(this);
                break;
            case "session-closed":
                CaptureStatusNoticeWindow.ShowDeveloperStoppedPreview(this);
                break;
            case "usb-config-error":
                CaptureStatusNoticeWindow.ShowDeveloperUsbPreview(this);
                break;
            case "capture-recovery":
                CaptureRecoveryWindow.ShowDeveloperPreview(this);
                break;
            case "image-settings":
                ImageSettingsWindow.ShowDeveloperPreview(this);
                break;
            case "projection-settings":
                ShowDeveloperProjectionSettings();
                break;
            case "media-output":
                ShowDeveloperMediaOutputSettings();
                break;
            case "usb-mode":
                if (_viewModel.UsbProjectionModes.FirstOrDefault() is { } option)
                    new UsbProjectionModeInfoWindow(option) { Owner = this }.Show();
                break;
            case "startup-error":
                new StartupErrorWindow(
                    new InvalidOperationException(LocalizationService.Get("DeveloperStartupErrorBody")),
                    DiagnosticLogger.Path) { Owner = this }.Show();
                break;
            case "update":
                if (Application.Current is App app) app.ShowDeveloperUpdateWindow(this);
                break;
            case "instance-conflict":
                InstanceConflictWindow.ShowDeveloperPreview(this);
                break;
            case "protected-content":
                ProtectedContentNoticeWindow.ShowDeveloperPreview(this);
                break;
            case "native-preview":
                ShowDeveloperNativePreview();
                break;
        }
    }

    private void ShowDeveloperProjectionSettings()
    {
        var window = new ProjectionSettingsWindow(_viewModel,
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => { }) { Owner = this };
        window.Show();
    }

    private void ShowDeveloperMediaOutputSettings()
    {
        var window = new MediaOutputSettingsWindow(_viewModel, previewOnly: true)
        {
            Owner = this,
        };
        window.Show();
    }

    private void ShowDeveloperNativePreview()
    {
        var content = new Border
        {
            Background = (Brush)FindResource("PreviewPanelAltBrush"),
            Padding = new Thickness(32),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = LocalizationService.Get("DeveloperNativePreviewBody"),
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        TextAlignment = TextAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "1920 × 1080",
                        Foreground = (Brush)FindResource("PreviewMutedTextBrush"),
                        FontSize = 13,
                        Margin = new Thickness(0, 10, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        NativePreviewWindow.TryCreateAndShowForContent(content, 1920, 1080,
            LocalizationService.Get("DeveloperNativePreviewTitle"),
            () => true, _ => { }, () => 1, () => { }, () => { },
            out _, message => _viewModel.AddDiagnosticLog(message));
    }

    private void OnUsbProjectionModeInfoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsbProjectionModeOption option }) return;
        e.Handled = true;
        new Windows.UsbProjectionModeInfoWindow(option) { Owner = this }.ShowDialog();
    }

    private async void OnMirrorSimultaneouslyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement { DataContext: Models.DeviceViewModel device } ||
            device.IsMediaCast) return;
        try
        {
            if (_viewModel.IsBluetoothControlEnabled)
                await _viewModel.DisableBluetoothControlAsync();
            var result = await _secondaryMirrors.ShowAsync(device);
            if (result.Success) QueueMainPreviewHostSync();
            _viewModel.AddUiLog(result.Success
                ? LocalizationService.Format("SimultaneousMirrorStartedFormat", device.DisplayName)
                : LocalizationService.Format("SimultaneousMirrorFailedFormat", result.Message));
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog(LocalizationService.Format(
                "SimultaneousMirrorFailedFormat", error.Message));
        }
    }

    private void OnDeviceListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is not ListBoxItem item || item.ContextMenu is null) return;
        if (item.DataContext is DeviceViewModel { IsMediaCast: true })
        {
            e.Handled = true;
            return;
        }

        // WPF selects a ListBoxItem on right-click before opening its menu.
        // That would stop the current phone as a normal device switch. Open
        // the item's menu ourselves and leave the active selection untouched.
        e.Handled = true;
        item.ContextMenu.PlacementTarget = item;
        item.ContextMenu.IsOpen = true;
    }

    private void OnDeviceListLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindListBoxItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not DeviceViewModel device) return;
        _pressedDevice = device;
        _devicePressPoint = e.GetPosition(DeviceListBox);
        _devicePressStartedUtc = DateTime.UtcNow;
        _deviceDragStarted = false;
        DeviceListBox.CaptureMouse();
        e.Handled = true;
    }

    private void OnDeviceListMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedDevice is null || _deviceDragStarted ||
            e.LeftButton != MouseButtonState.Pressed ||
            DateTime.UtcNow - _devicePressStartedUtc < DeviceDragHoldDuration) return;

        var current = e.GetPosition(DeviceListBox);
        if (Math.Abs(current.X - _devicePressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _devicePressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _pressedDevice;
        _deviceDragStarted = true;
        DeviceListBox.ReleaseMouseCapture();
        e.Handled = true;
        try
        {
            DragDrop.DoDragDrop(DeviceListBox, dragged, DragDropEffects.Move);
        }
        finally
        {
            ResetDeviceDragState();
        }
    }

    private void OnDeviceListLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedDevice is null) return;
        var device = _pressedDevice;
        var select = !_deviceDragStarted;
        ResetDeviceDragState();
        if (select) _ = SelectDeviceWithTransitionAsync(device);
        e.Handled = true;
    }

    private async Task SelectDeviceWithTransitionAsync(DeviceViewModel device)
    {
        if (ReferenceEquals(_viewModel.SelectedDevice, device)) return;
        var revision = Interlocked.Increment(ref _previewTransitionRevision);
        var maskTransition = _viewModel.IsCapturing &&
            !_viewModel.HasCaptureSessionFor(device);
        if (!maskTransition)
        {
            PreviewTransitionMask.IsOpen = false;
            DeviceListBox.SelectedItem = device;
            return;
        }

        PreviewTransitionMask.IsOpen = true;
        try
        {
            // A Popup owns a separate HWND and can cover WPF/HwndHost airspace.
            // Give DWM two frames to present it before changing preview owners.
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(34);
            if (revision != _previewTransitionRevision || _shutdownStarted) return;
            DeviceListBox.SelectedItem = device;
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(100);
        }
        catch (Exception error)
        {
            if (!_shutdownStarted)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("device_selection_transition_failed",
                    ("device", AppLog.Device(device.Udid)),
                    ("error", AppLog.Error(error))));
            }
        }
        finally
        {
            if (revision == _previewTransitionRevision)
                PreviewTransitionMask.IsOpen = false;
        }
    }

    private void OnDeviceListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeviceViewModel)) is not DeviceViewModel source)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        var item = FindListBoxItem(e.OriginalSource as DependencyObject);
        var target = item?.DataContext as DeviceViewModel;
        if (target is not null && !ReferenceEquals(source, target))
        {
            var before = CaptureDeviceItemPositions();
            var placeAfter = e.GetPosition(item!).Y >= item!.ActualHeight / 2;
            var oldIndex = _viewModel.Devices.IndexOf(source);
            _viewModel.MoveDevice(source, target, placeAfter);
            if (_viewModel.Devices.IndexOf(source) != oldIndex)
                AnimateDeviceItemsFrom(before);
        }
        e.Handled = true;
    }

    private void OnDeviceListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DeviceViewModel)) is not DeviceViewModel source) return;
        var item = FindListBoxItem(e.OriginalSource as DependencyObject);
        var target = item?.DataContext as DeviceViewModel;
        var placeAfter = item is not null && e.GetPosition(item).Y >= item.ActualHeight / 2;
        _viewModel.MoveDevice(source, target, placeAfter);
        e.Handled = true;
    }

    private void ResetDeviceDragState()
    {
        DeviceListBox.ReleaseMouseCapture();
        _pressedDevice = null;
        _deviceDragStarted = false;
    }

    private Dictionary<DeviceViewModel, double> CaptureDeviceItemPositions()
    {
        var positions = new Dictionary<DeviceViewModel, double>();
        foreach (var device in _viewModel.Devices)
            if (DeviceListBox.ItemContainerGenerator.ContainerFromItem(device) is ListBoxItem item)
                positions[device] = item.TranslatePoint(default, DeviceListBox).Y;
        return positions;
    }

    private void AnimateDeviceItemsFrom(IReadOnlyDictionary<DeviceViewModel, double> before)
    {
        DeviceListBox.UpdateLayout();
        foreach (var device in _viewModel.Devices)
        {
            if (!before.TryGetValue(device, out var oldY) ||
                DeviceListBox.ItemContainerGenerator.ContainerFromItem(device) is not ListBoxItem item)
                continue;
            var delta = oldY - item.TranslatePoint(default, DeviceListBox).Y;
            if (Math.Abs(delta) < 0.5) continue;
            var transform = item.RenderTransform as TranslateTransform ?? new TranslateTransform();
            item.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }

    private void RefreshPreview()
    {
        _viewModel.AddDiagnosticLog(AppLog.Event("preview_refresh_begin",
            ("mode", _mediaCastActive && _viewModel.IsMediaCastSelected
                ? "media_cast" : _viewModel.SelectedDevice?.IsWireless == true
                    ? "wireless" : "wired"),
            ("independent", _mediaCastPreviewWindow is not null ||
                _secondaryMirrors.IsOpen(_viewModel.SelectedDevice))));
        var refreshed = _mediaCastActive && _viewModel.IsMediaCastSelected
            ? RefreshMediaCastPreview()
            :
            (_secondaryMirrors.IsOpen(_viewModel.SelectedDevice)
                ? _secondaryMirrors.Refresh(_viewModel.SelectedDevice)
                : MainPreviewHost.ForceRefresh());
        _viewModel.AddUiLog(LocalizationService.Get(
            refreshed ? "PreviewRefreshed" : "PreviewRefreshFailed"));
        _viewModel.AddDiagnosticLog(AppLog.Event("preview_refresh_complete",
            ("success", refreshed)));
    }

    private bool RefreshMediaCastPreview()
    {
        if (_mediaCastPreviewWindow is not null)
            return _mediaCastPreviewWindow.RefreshPreview();
        MediaCastMediaElement.InvalidateVisual();
        MediaCastSurface.InvalidateVisual();
        return _mediaOpened;
    }

    private async void OnPreviewWindowClick(object sender, RoutedEventArgs e) =>
        await OpenPreviewWindowAsync();

    private async void OnStartBluetoothControlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("bluetooth_control_toolbar_start",
                ("device", AppLog.Device(_viewModel.SelectedDevice?.Udid))));
            await _viewModel.StartBluetoothControlAsync();
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog($"蓝牙反控启动失败：{error.Message}");
            _viewModel.AddDiagnosticLog(AppLog.Event("bluetooth_control_toolbar_failed",
                ("error", AppLog.Error(error))));
        }
    }

    private async void OnStartUsbControlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("usb_control_toolbar_toggle",
                ("device", AppLog.Device(_viewModel.SelectedDevice?.Udid)),
                ("wireless", _viewModel.IsWirelessSelected)));
            await _viewModel.ToggleWiredControlAsync();
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog($"USB 控制操作失败：{error.Message}");
            System.Windows.MessageBox.Show(this, $"USB 控制操作失败：{error.Message}",
                "USB 控制", MessageBoxButton.OK, MessageBoxImage.Warning);
            _viewModel.AddDiagnosticLog(AppLog.Event("usb_control_toolbar_failed",
                ("error", AppLog.Error(error))));
        }
    }

    private async void OnStartWirelessControlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("wireless_control_toolbar_toggle",
                ("device", AppLog.Device(_viewModel.SelectedDevice?.Udid))));
            await _viewModel.ToggleWirelessControlAsync();
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog($"无线反控操作失败：{error.Message}");
            _viewModel.AddDiagnosticLog(AppLog.Event("wireless_control_toolbar_failed",
                ("error", AppLog.Error(error))));
        }
    }

    private async Task OpenPreviewWindowAsync()
    {
        try
        {
            if (_mediaCastActive && _viewModel.IsMediaCastSelected)
            {
                _viewModel.AddDiagnosticLog(AppLog.Event("preview_window_open_begin",
                    ("mode", "media_cast"), ("opened", _mediaCastPreviewWindow is not null)));
                ShowMediaCastPreviewWindow();
                _viewModel.AddUiLog(LocalizationService.Get("PreviewWindowOpened"));
                return;
            }
            var device = _viewModel.SelectedDevice;
            if (device is null) return;
            if (_viewModel.IsBluetoothControlEnabled)
                await _viewModel.DisableBluetoothControlAsync();
            _viewModel.AddDiagnosticLog(AppLog.Event("preview_window_open_begin",
                ("mode", device.IsWireless ? "wireless" : "wired"),
                ("device", AppLog.Device(device.Udid))));
            var result = await _secondaryMirrors.ShowAsync(device);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            QueueMainPreviewHostSync();
            _secondaryMirrors.UpdateDevice(device,
                _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight);
            _viewModel.AddUiLog(LocalizationService.Get("PreviewWindowOpened"));
            _viewModel.AddDiagnosticLog(AppLog.Event("preview_window_open_complete",
                ("device", AppLog.Device(device.Udid)), ("success", true)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("preview_window_open_failed",
                ("error", AppLog.Error(error))));
            _viewModel.AddUiLog(LocalizationService.Format("PreviewWindowOpenFailedFormat", error.Message));
        }
    }

    private void OnProjectionSettingsRequested(string udid)
    {
        var sessionHandle = _viewModel.GetDeviceSessionHandle(udid);
        if (!DeviceViewModel.UdidEquals(_viewModel.SelectedDevice?.Udid, udid)) return;
        if (_projectionSettingsWindow is not null)
        {
            if (DeviceViewModel.UdidEquals(_projectionSettingsUdid, udid) &&
                _projectionSettingsSessionHandle == sessionHandle)
            {
                _projectionSettingsWindow.Activate();
                _projectionSettingsWindow.Focus();
                return;
            }
            _projectionSettingsWindow.Close();
        }
        var window = new ProjectionSettingsWindow(_viewModel,
            () =>
            {
                RefreshPreview();
                return Task.CompletedTask;
            },
            ToggleActiveFullScreenAsync,
            OpenPreviewWindowAsync,
            CaptureScreenshotAsync,
            () => OnMediaOutputSettingsRequested(udid, sessionHandle))
        {
            Owner = this,
        };
        _projectionSettingsWindow = window;
        _projectionSettingsUdid = udid;
        _projectionSettingsSessionHandle = sessionHandle;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_projectionSettingsWindow, window))
            {
                _projectionSettingsWindow = null;
                _projectionSettingsUdid = null;
                _projectionSettingsSessionHandle = 0;
            }
        };
        _viewModel.AddDiagnosticLog(AppLog.Event("projection_settings_window_opened",
            ("device", AppLog.Device(udid)),
            ("handle", AppLog.Handle(sessionHandle))));
        window.Show();
    }

    private void OnMediaOutputSettingsRequested() => OnMediaOutputSettingsRequested(
        _viewModel.SelectedDevice?.Udid, _viewModel.CurrentSessionHandle);

    private void OnMediaOutputSettingsRequested(string? udid, ulong sessionHandle)
    {
        if (_mediaOutputSettingsWindow is not null)
        {
            _mediaOutputSettingsWindow.Activate();
            _mediaOutputSettingsWindow.Focus();
            return;
        }
        var window = new MediaOutputSettingsWindow(_viewModel)
        {
            Owner = this,
        };
        _mediaOutputSettingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_mediaOutputSettingsWindow, window))
            {
                _mediaOutputSettingsWindow = null;
            }
        };
        _viewModel.AddDiagnosticLog(AppLog.Event("media_output_window_opened",
            ("device", AppLog.Device(udid)),
            ("handle", AppLog.Handle(sessionHandle))));
        window.Show();
    }

    private void OnDeviceSessionHandleChanged(string udid, ulong sessionHandle)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
                OnDeviceSessionHandleChanged(udid, sessionHandle));
            return;
        }
        if (_projectionSettingsWindow is not null &&
            DeviceViewModel.UdidEquals(_projectionSettingsUdid, udid) &&
            _projectionSettingsSessionHandle != sessionHandle)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "projection_settings_session_invalidated",
                ("device", AppLog.Device(udid)),
                ("old_handle", AppLog.Handle(_projectionSettingsSessionHandle)),
                ("new_handle", AppLog.Handle(sessionHandle))));
            _projectionSettingsWindow.Close();
        }
    }

    private void OnDeviceProtectionStateChanged(string udid,
        ProtectedContentPresentation presentation)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
                OnDeviceProtectionStateChanged(udid, presentation));
            return;
        }
        if (!presentation.IsProtected)
        {
            if (DeviceViewModel.UdidEquals(_protectedContentNoticeUdid, udid))
            {
                _protectedContentNoticeWindow?.UpdatePresentation(presentation);
            }
            return;
        }
        if (!DeviceViewModel.UdidEquals(_viewModel.SelectedDevice?.Udid, udid))
            return;
        if (_protectedContentNoticeWindow is null)
        {
            _protectedContentNoticeUdid = udid;
            _protectedContentNoticeWindow =
                new ProtectedContentNoticeWindow(udid, presentation, this);
            _protectedContentNoticeWindow.Closed += (_, _) =>
            {
                _protectedContentNoticeWindow = null;
                _protectedContentNoticeUdid = null;
            };
            _protectedContentNoticeWindow.Show();
        }
        else
        {
            _protectedContentNoticeWindow.UpdatePresentation(presentation);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsLightweightApplicationMode) or
            nameof(MainViewModel.IsCapturing) or
            nameof(MainViewModel.IsMediaCasting) or
            nameof(MainViewModel.IsMediaCastSelected) or
            nameof(MainViewModel.SourceVideoWidth) or
            nameof(MainViewModel.SourceVideoHeight))
        {
            if (e.PropertyName != nameof(MainViewModel.IsLightweightApplicationMode) &&
                !HasLightweightWorkspacePanels)
                _lightweightWidthNeedsFit = true;
            ApplyApplicationDisplayMode();
        }
        if (e.PropertyName == nameof(MainViewModel.SelectedDevice) &&
            _activeControlWindow == 0 && _viewModel.IsBluetoothControlEnabled &&
            !_viewModel.IsBluetoothControlTarget(_viewModel.SelectedDevice?.Udid))
        {
            // The HID peripheral remains connected to the device that enabled
            // control. Never keep sending it mouse input from a newly selected
            // main preview; independent-window control owns its own target.
            _ = SwitchBluetoothControlForSelectionChangeAsync();
        }
        if (e.PropertyName == nameof(MainViewModel.SelectedDevice) &&
            _viewModel.IsUsbControlEnabled &&
            !_viewModel.IsUsbControlTarget(_viewModel.SelectedDevice?.Udid))
        {
            _ = _viewModel.DisableUsbControlAsync();
        }
        if (e.PropertyName is nameof(MainViewModel.IsBluetoothControlEnabled) or
            nameof(MainViewModel.BluetoothControlIsConnected) or
            nameof(MainViewModel.BluetoothControlIsInputEnabled) or
            nameof(MainViewModel.BluetoothControlTargetUdid) or
            nameof(MainViewModel.SelectedDevice) or
            nameof(MainViewModel.IsUsbControlEnabled) or
            nameof(MainViewModel.UsbControlIsInputEnabled) or
            nameof(MainViewModel.CanToggleUsbControl))
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedDevice) &&
                _viewModel.IsBluetoothControlEnabled && _activeControlWindow == 0)
            {
                _activeControlUdid = _viewModel.BluetoothControlTargetUdid;
                if (IsLoaded) _refreshTimer.Start();
            }
            else if (!_viewModel.IsBluetoothControlEnabled)
            {
                _activeControlWindow = 0;
                _activeControlUdid = null;
                if (IsLoaded) _refreshTimer.Start();
            }
            ApplyBluetoothControlInputState(
                e.PropertyName != nameof(MainViewModel.SelectedDevice));
        }
        if (e.PropertyName == nameof(MainViewModel.AdvancedSettingsVisibility) &&
            _viewModel.AdvancedSettingsVisibility == Visibility.Visible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                () => AdvancedSettingsCard.BringIntoView());
        }

        if ((e.PropertyName == nameof(MainViewModel.IsCapturing) && !_viewModel.IsCapturing) ||
            (e.PropertyName == nameof(MainViewModel.IsAudioOnlyAirPlay) &&
             _viewModel.IsAudioOnlyAirPlay) ||
            (e.PropertyName == nameof(MainViewModel.IsVideoProtected) &&
             _viewModel.IsVideoProtected))
            MainPreviewHost.SetPresentationVisible(false);

        // Width is raised before height as one atomic status update. Listening
        // to the final height notification avoids resizing twice per frame-
        // format/orientation change.
        if (e.PropertyName is nameof(MainViewModel.SourceVideoHeight) or
            nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.SelectedModel) or
            nameof(MainViewModel.CurrentSessionHandle) or
            nameof(MainViewModel.IsAudioOnlyAirPlay) or
            nameof(MainViewModel.IsVideoProtected))
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedDevice))
            {
                if (_protectedContentNoticeWindow is not null &&
                    !DeviceViewModel.UdidEquals(_protectedContentNoticeUdid,
                        _viewModel.SelectedDevice?.Udid))
                    _protectedContentNoticeWindow.Close();
                if (_projectionSettingsWindow is not null &&
                    !DeviceViewModel.UdidEquals(_projectionSettingsUdid,
                        _viewModel.SelectedDevice?.Udid))
                    _projectionSettingsWindow.Close();
            }
            else if (e.PropertyName == nameof(MainViewModel.CurrentSessionHandle))
            {
                var currentHandle = _viewModel.CurrentSessionHandle;
                if (_projectionSettingsWindow is not null &&
                    _projectionSettingsSessionHandle != currentHandle)
                    _projectionSettingsWindow.Close();
            }
            _secondaryMirrors.UpdateDevice(
                _viewModel.SelectedDevice,
                _viewModel.SourceVideoWidth,
                _viewModel.SourceVideoHeight);
            if (e.PropertyName is nameof(MainViewModel.SelectedDevice) or
                nameof(MainViewModel.CurrentSessionHandle) or
                nameof(MainViewModel.IsAudioOnlyAirPlay) or
                nameof(MainViewModel.IsVideoProtected))
                QueueMainPreviewHostSync();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsCapturing))
            QueueMainPreviewHostSync();
    }

    private void ApplyBluetoothControlInputState(
        bool activateIndependentWindow = true)
    {
        var controlActive = IsBluetoothControlActive;
        var usbControlActive = IsUsbControlActive;
        var usbControlConnected = _viewModel.UsbControlIsInputEnabled;
        MainPreviewHost.CapturePointerInput =
            (controlActive || usbControlActive) && _activeControlWindow == 0;
        if ((controlActive || usbControlActive) && _activeControlWindow == 0)
        {
            // Explicitly focus the native preview whenever any main-window
            // control route becomes active. The preview is a native child HWND,
            // so normal WPF key bubbling is only a fallback path.
            MainPreviewHost.Focus();
        }
        // USB touch control sends touch coordinates and must leave the
        // Windows pointer visible, including while an independent preview
        // owns the active control route.
        SetWindowsCursorHidden(controlActive && !usbControlConnected);
        SetSystemKeySuppression(controlActive);
        RegisterRawInput(controlActive && _activeControlWindow == 0,
            (controlActive || usbControlActive) && _activeControlWindow == 0);
        if (controlActive && _activeControlWindow != 0)
        {
            // Native independent previews only forward input while foreground.
            if (activateIndependentWindow)
                _secondaryMirrors.Activate(_activeControlUdid);
        }
        else if (!controlActive && !usbControlActive)
            ClearBluetoothControlInputState();
    }

    private void ClearBluetoothControlInputState()
    {
        // Hiding a native window or losing its Bluetooth route does not restore
        // process-wide cursor, keyboard, raw-input, or clipping state by itself.
        MainPreviewHost.CapturePointerInput = false;
        SetWindowsCursorHidden(false);
        SetSystemKeySuppression(false);
        RegisterRawInput(false, false);
        ClipCursor(IntPtr.Zero);
        ResetControlRouteState();
        _usbTouchPressed = false;
    }

    private void ApplyApplicationDisplayMode()
    {
        if (_isFullScreen)
        {
            PreviewPanel.ClearValue(WidthProperty);
            return;
        }

        if (!_viewModel.IsLightweightApplicationMode)
        {
            CancelLightweightWindowWidthAnimation();
            EnvironmentPanel.ClearValue(VisibilityProperty);
            StatsPanel.ClearValue(VisibilityProperty);
            StatsGapRow.Height = new GridLength(14);
            PreviewPanel.ClearValue(WidthProperty);
            PreviewPanel.ClearValue(MinWidthProperty);
            PreviewPanel.ClearValue(MaxWidthProperty);
            PreviewPanel.ClearValue(HorizontalAlignmentProperty);
            PreviewPanel.SetResourceReference(Panel.BackgroundProperty,
                "PreviewChromeBrush");
            MinWidth = 1280;
            if (_lightweightModeApplied && !_isWindowMaximized)
            {
                SizeToContent = SizeToContent.Manual;
                if (_completeModeWidth >= MinWidth) Width = _completeModeWidth;
            }
            _lightweightModeApplied = false;
            _lightweightWidthNeedsFit = false;
            return;
        }

        if (!_lightweightModeApplied)
        {
            _completeModeWidth = ActualWidth > 0 ? ActualWidth : Width;
            _lightweightModeApplied = true;
            _lightweightWidthNeedsFit = true;
        }
        EnvironmentPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        StatsGapRow.Height = new GridLength(0);
        PreviewPanel.MinWidth = 0;
        PreviewPanel.ClearValue(WidthProperty);
        if (HasLightweightVideoPresentation)
            PreviewPanel.Background = Brushes.Black;
        else
            PreviewPanel.SetResourceReference(Panel.BackgroundProperty,
                "PreviewChromeBrush");
        ApplyLightweightPreviewFramePolicy();
        if (_isWindowMaximized)
            return;
        SizeToContent = SizeToContent.Manual;
        if (HasLightweightVideoPresentation && !HasLightweightContentDimensions)
        {
            // Keep the stable pre-capture geometry until the decoder has a
            // real source size. The first QuickTime frames are sensitive to
            // repeated host HWND resizes during USB re-enumeration.
            _lightweightWidthNeedsFit = true;
            return;
        }
        if (HasLightweightWorkspacePanels)
        {
            _lightweightWidthNeedsFit = false;
            QueueInitialLightweightWorkspaceFit();
        }
        else
            QueueLightweightPreviewWidth();
    }

    private void QueueLightweightPreviewWidth()
    {
        if (HasLightweightWorkspacePanels)
        {
            _lightweightWidthNeedsFit = false;
            return;
        }
        if (_lightweightWorkspaceSurfaceAnimationActive) return;
        if (_lightweightPreviewWidthQueued) return;
        _lightweightPreviewWidthQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _lightweightPreviewWidthQueued = false;
            if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
                _isWindowMaximized)
            {
                PreviewPanel.ClearValue(WidthProperty);
                return;
            }
            if (CenterColumn.ActualWidth <= 0)
            {
                _lightweightWidthNeedsFit = true;
                return;
            }

            // CenterPanel is the resizable grid column. Subtracting the preview
            // itself would count unused center whitespace as chrome and raise
            // MinWidth to the current window width.
            var currentWindowWidth = GetLightweightCurrentWindowWidth();
            var chromeWidth = Math.Max(0, currentWindowWidth - CenterColumn.ActualWidth);
            var maximumWindowWidth = GetLightweightMaximumWindowWidth();
            var maximumPreviewWidth = Math.Max(1, maximumWindowWidth -
                chromeWidth);
            var hasContentPreviewWidth = TryGetLightweightContentPreviewWidth(
                maximumPreviewWidth, out var contentPreviewWidth);
            var aspect = _viewModel.SourceVideoWidth != 0 &&
                _viewModel.SourceVideoHeight != 0
                ? (double)_viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight
                : LightweightDefaultPreviewAspect;
            var previewHeight = Math.Max(520, PreviewPanel.ActualHeight);
            var preferredPreviewWidth = hasContentPreviewWidth
                ? contentPreviewWidth
                : Math.Max(LightweightMinimumPreviewWidth,
                    Math.Round(previewHeight * aspect));
            var width = Math.Min(preferredPreviewWidth,
                maximumPreviewWidth);
            var minimumWindowWidth = Math.Max(LightweightMinimumWindowWidth,
                chromeWidth + (hasContentPreviewWidth
                    ? contentPreviewWidth
                    : LightweightMinimumPreviewWidth));
            var targetMinimumWindowWidth = Math.Min(minimumWindowWidth,
                maximumWindowWidth);
            _lightweightTargetMinWidth = targetMinimumWindowWidth;
            if (MinWidth > targetMinimumWindowWidth ||
                ActualWidth >= targetMinimumWindowWidth)
                MinWidth = targetMinimumWindowWidth;
            ApplyLightweightPreviewFramePolicy(useNormalPortraitFrame: false,
                hasContentPreviewWidth ? width : null);
            if (_lightweightWidthNeedsFit)
            {
                _lightweightWidthNeedsFit = false;
                var targetWindowWidth = Math.Clamp(chromeWidth + width,
                    targetMinimumWindowWidth, maximumWindowWidth);
                if (hasContentPreviewWidth)
                    SetLightweightWindowWidthImmediately(targetWindowWidth);
                else
                    AnimateLightweightWindowWidth(targetWindowWidth);
            }
        });
    }

    private void ApplyLightweightPreviewFramePolicy(
        bool? useNormalPortraitFrame = null, double? contentPreviewWidth = null)
    {
        if (!_viewModel.IsLightweightApplicationMode) return;
        double requestedContentPreviewWidth;
        if (contentPreviewWidth is { } explicitContentPreviewWidth)
            requestedContentPreviewWidth = explicitContentPreviewWidth;
        else if (!TryGetLightweightContentPreviewWidth(double.PositiveInfinity,
            out requestedContentPreviewWidth))
            requestedContentPreviewWidth = 0;
        if (requestedContentPreviewWidth > 0)
        {
            PreviewPanel.BeginAnimation(WidthProperty, null);
            PreviewPanel.Width = requestedContentPreviewWidth;
            PreviewPanel.MinWidth = requestedContentPreviewWidth;
            PreviewPanel.MaxWidth = requestedContentPreviewWidth;
            PreviewPanel.HorizontalAlignment = HorizontalAlignment.Center;
            return;
        }
        var canUseNormalPortraitFrame = useNormalPortraitFrame ??
            CanUseNormalPortraitPreviewFrame();
        // Preview chrome owns the complete center column unless the current
        // workspace can fit the requested 340 DIP portrait frame.
        if (!canUseNormalPortraitFrame)
        {
            PreviewPanel.BeginAnimation(WidthProperty, null);
            PreviewPanel.Width = double.NaN;
            PreviewPanel.MinWidth = 0;
            PreviewPanel.MaxWidth = double.PositiveInfinity;
            PreviewPanel.ClearValue(WidthProperty);
            PreviewPanel.ClearValue(MinWidthProperty);
            PreviewPanel.ClearValue(MaxWidthProperty);
            PreviewPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        PreviewPanel.Width = LightweightNormalPortraitPreviewWidth;
        PreviewPanel.MinWidth = LightweightNormalPortraitPreviewWidth;
        PreviewPanel.MaxWidth = LightweightNormalPortraitPreviewWidth;
        PreviewPanel.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private bool TryGetLightweightContentPreviewWidth(double maximumWidth,
        out double previewWidth)
    {
        previewWidth = 0;
        if (!HasLightweightVideoPresentation || _viewModel.SourceVideoWidth == 0 ||
            _viewModel.SourceVideoHeight == 0 || maximumWidth <= 0) return false;
        var aspect = (double)_viewModel.SourceVideoWidth /
            _viewModel.SourceVideoHeight;
        var previewHeight = PreviewPanel.ActualHeight > 0
            ? PreviewPanel.ActualHeight
            : Math.Max(520, ActualHeight);
        if (!double.IsFinite(aspect) || aspect <= 0 ||
            !double.IsFinite(previewHeight) || previewHeight <= 0) return false;
        previewWidth = Math.Min(maximumWidth,
            Math.Max(1, Math.Round(previewHeight * aspect)));
        return true;
    }

    private bool CanUseNormalPortraitPreviewFrame()
    {
        var showLeftPanel = _leftWorkspacePanel != LeftWorkspacePanel.None;
        var showSettings = _isSettingsPanelVisible;
        if (!showLeftPanel && !showSettings) return false;
        var targetSideWidth = (showLeftPanel ? 318 : 0) +
            (showSettings ? 354 : 0);
        var chromeWidth = GetLightweightFixedChromeWidth(
            GetLightweightCurrentWindowWidth());
        return GetLightweightMaximumWindowWidth() >= chromeWidth +
            targetSideWidth + LightweightNormalPortraitPreviewWidth;
    }

    private void AnimateLightweightWindowWidth(double targetWidth,
        bool preserveCenterWidth = false)
    {
        if (!double.IsFinite(targetWidth) || targetWidth <= 0) return;
        if (!preserveCenterWidth) ReleaseLightweightCenterWidth();
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsLoaded || handle == 0 || !GetWindowRect(handle, out var bounds))
        {
            Width = targetWidth;
            CompleteLightweightWorkspaceSurfaceAnimation();
            if (preserveCenterWidth) ReleaseLightweightCenterWidth();
            return;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        var targetPixels = Math.Max(1, (int)Math.Round(targetWidth * scale));
        var currentPixels = Math.Max(1, bounds.Right - bounds.Left);
        if (!preserveCenterWidth)
        {
            _lightweightWindowStartX = bounds.Left;
            _lightweightWindowTargetX = bounds.Left;
        }
        _lightweightWindowTopPixels = bounds.Top;
        if (Math.Abs(currentPixels - targetPixels) <= 1 &&
            _lightweightWindowStartX == _lightweightWindowTargetX &&
            !_lightweightWorkspaceSurfaceAnimationActive)
        {
            Width = targetWidth;
            CompleteLightweightWorkspaceSurfaceAnimation();
            if (preserveCenterWidth) ReleaseLightweightCenterWidth();
            return;
        }

        _lightweightWindowWidthStartPixels = currentPixels;
        _lightweightWindowWidthTargetPixels = targetPixels;
        _lightweightWindowHeightPixels = Math.Max(1, bounds.Bottom - bounds.Top);
        _lightweightWindowWidthTargetDips = targetWidth;
        if (preserveCenterWidth) _lightweightCenterWidthLocked = true;
        _lightweightWindowAnimationProgress = 0;
        _lightweightWindowLastAppliedProgress = 0;
        _lightweightWindowLastRenderTime = TimeSpan.Zero;
        _lightweightWindowLastAppliedTime = TimeSpan.Zero;
        _lightweightWindowLastAppliedWidthPixels = currentPixels;
        _lightweightWindowLastAppliedX = bounds.Left;
        if (!_lightweightWindowRendering)
        {
            _lightweightWindowRendering = true;
            CompositionTarget.Rendering += OnLightweightWindowRendering;
        }
    }

    private void OnLightweightWindowRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            StopLightweightWindowRendering();
            return;
        }

        if (_lightweightWindowLastRenderTime == TimeSpan.Zero)
        {
            _lightweightWindowLastRenderTime = rendering.RenderingTime;
            return;
        }
        var elapsed = rendering.RenderingTime - _lightweightWindowLastRenderTime;
        _lightweightWindowLastRenderTime = rendering.RenderingTime;
        var elapsedSeconds = Math.Max(0, elapsed.TotalSeconds);
        _lightweightWindowAnimationProgress = Math.Min(1,
            _lightweightWindowAnimationProgress +
            elapsedSeconds / LightweightWorkspaceTransitionDuration.TotalSeconds);
        var isComplete = _lightweightWindowAnimationProgress >= 1;
        if (isComplete || _lightweightWindowLastAppliedTime == TimeSpan.Zero ||
            rendering.RenderingTime - _lightweightWindowLastAppliedTime >=
            LightweightWindowFrameInterval)
        {
            ApplyLightweightWindowAnimationFrame(handle,
                _lightweightWindowAnimationProgress, force: isComplete);
            _lightweightWindowLastAppliedProgress = _lightweightWindowAnimationProgress;
            _lightweightWindowLastAppliedTime = rendering.RenderingTime;
        }
        if (!isComplete) return;

        StopLightweightWindowRendering();
        SynchronizeLightweightWindowPosition();
        Width = _lightweightWindowWidthTargetDips;
        CompleteLightweightWorkspaceSurfaceAnimation();
        ReleaseLightweightCenterWidth();
    }

    private void CancelLightweightWindowWidthAnimation(bool preserveLayout = false)
    {
        if (_lightweightWorkspaceSurfaceAnimationActive)
            CommitLightweightWorkspaceSurface(_lightweightWindowLastAppliedProgress);
        StopLightweightWindowRendering();
        _lightweightWorkspaceSurfaceAnimationActive = false;
        if (!preserveLayout)
        {
            SetLightweightCenterColumnFill();
            ReleaseLightweightCenterWidth();
        }
    }

    private void StopLightweightWindowRendering()
    {
        if (!_lightweightWindowRendering) return;
        CompositionTarget.Rendering -= OnLightweightWindowRendering;
        _lightweightWindowRendering = false;
        _lightweightWindowLastRenderTime = TimeSpan.Zero;
        _lightweightWindowLastAppliedTime = TimeSpan.Zero;
    }

    private void ApplyLightweightWindowAnimationFrame(nint handle, double progress,
        bool force = false)
    {
        var eased = EaseWorkspaceProgress(progress);
        var width = (int)Math.Round(_lightweightWindowWidthStartPixels +
            (_lightweightWindowWidthTargetPixels - _lightweightWindowWidthStartPixels) * eased);
        var x = (int)Math.Round(_lightweightWindowStartX +
            (_lightweightWindowTargetX - _lightweightWindowStartX) * eased);
        ApplyLightweightWorkspaceSurfaceWidths(eased);
        if (!force && width == _lightweightWindowLastAppliedWidthPixels &&
            x == _lightweightWindowLastAppliedX)
            return;

        if (handle != 0)
            _ = SetWindowPos(handle, 0, x, _lightweightWindowTopPixels, width,
                _lightweightWindowHeightPixels, SwpNoZOrder | SwpNoActivate);
        _lightweightWindowLastAppliedWidthPixels = width;
        _lightweightWindowLastAppliedX = x;
    }

    private void SynchronizeLightweightWindowPosition()
    {
        if (_lightweightWindowStartX == _lightweightWindowTargetX) return;
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = handle == 0 ? 96u : GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        Left = _lightweightWindowTargetX / scale;
        Top = _lightweightWindowTopPixels / scale;
    }

    private void ApplyLightweightWorkspaceSurfaceWidths(double progress)
    {
        if (!_lightweightWorkspaceSurfaceAnimationActive) return;
        LeftPanelHost.Width = Interpolate(_lightweightLeftSurfaceStartWidth,
            _lightweightLeftSurfaceTargetWidth, progress);
        ControlPanel.Width = Interpolate(_lightweightRightSurfaceStartWidth,
            _lightweightRightSurfaceTargetWidth, progress);
        LeftGapColumn.Width = new GridLength(Interpolate(_lightweightLeftGapStartWidth,
            _lightweightLeftGapTargetWidth, progress));
        RightGapColumn.Width = new GridLength(Interpolate(_lightweightRightGapStartWidth,
            _lightweightRightGapTargetWidth, progress));
        // CenterColumn stays star-sized so PreviewPanel.ActualWidth tracks the
        // current window width rather than an animation-time cached width.
        SetLightweightCenterColumnFill();
    }

    private void CompleteLightweightWorkspaceSurfaceAnimation()
    {
        if (!_lightweightWorkspaceSurfaceAnimationActive) return;
        CommitLightweightWorkspaceSurface(1);
        SetLightweightCenterColumnFill();
        if (_lightweightTargetMinWidth > 0)
            MinWidth = _lightweightTargetMinWidth;
        _lightweightWorkspaceSurfaceAnimationActive = false;
        ApplyLightweightPreviewFramePolicy();
        if (_lightweightWidthNeedsFit)
            RequestLightweightWindowFit();
    }

    private void CommitLightweightWorkspaceSurface(double progress)
    {
        var eased = EaseWorkspaceProgress(progress);
        var leftWidth = Interpolate(_lightweightLeftSurfaceStartWidth,
            _lightweightLeftSurfaceTargetWidth, eased);
        var rightWidth = Interpolate(_lightweightRightSurfaceStartWidth,
            _lightweightRightSurfaceTargetWidth, eased);
        LeftPanelHost.Width = leftWidth;
        ControlPanel.Width = rightWidth;
        LeftGapColumn.Width = new GridLength(Interpolate(_lightweightLeftGapStartWidth,
            _lightweightLeftGapTargetWidth, eased));
        RightGapColumn.Width = new GridLength(Interpolate(_lightweightRightGapStartWidth,
            _lightweightRightGapTargetWidth, eased));
        SetLightweightCenterColumnFill();
        if (leftWidth <= 0) LeftPanelHost.Visibility = Visibility.Collapsed;
        if (rightWidth <= 0) ControlPanel.Visibility = Visibility.Collapsed;
    }

    private static double Interpolate(double from, double to, double progress) =>
        from + (to - from) * progress;

    private sealed class GridLengthAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength),
                typeof(GridLengthAnimation), new PropertyMetadata(new GridLength(0)));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength),
                typeof(GridLengthAnimation), new PropertyMetadata(new GridLength(0)));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction),
                typeof(GridLengthAnimation), new PropertyMetadata(null));

        public GridLength From
        {
            get => (GridLength)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public GridLength To
        {
            get => (GridLength)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
            object defaultDestinationValue, AnimationClock animationClock)
        {
            var progress = animationClock.CurrentProgress ?? 1;
            if (EasingFunction is not null)
                progress = EasingFunction.Ease(progress);
            var origin = From.IsAbsolute
                ? From.Value
                : ((GridLength)defaultOriginValue).Value;
            var destination = To.IsAbsolute
                ? To.Value
                : ((GridLength)defaultDestinationValue).Value;
            return new GridLength(origin + (destination - origin) * progress);
        }
    }

    private static double EaseWorkspaceProgress(double progress)
    {
        var remaining = 1 - Math.Clamp(progress, 0, 1);
        return 1 - remaining * remaining * remaining;
    }

    private void LockLightweightCenterWidth()
    {
        if (CenterColumn.ActualWidth <= 0) return;
        CenterColumn.Width = new GridLength(CenterColumn.ActualWidth);
        _lightweightCenterWidthLocked = true;
    }

    private void ReleaseLightweightCenterWidth()
    {
        if (!_lightweightCenterWidthLocked) return;
        SetLightweightCenterColumnFill();
        _lightweightCenterWidthLocked = false;
    }

    private void SetLightweightCenterColumnFill() =>
        CenterColumn.Width = new GridLength(1, GridUnitType.Star);

    private bool HasLightweightVideoPresentation => _viewModel.IsCapturing ||
        (_mediaCastActive && _viewModel.IsMediaCastSelected);

    private bool HasLightweightContentDimensions =>
        _viewModel.SourceVideoWidth != 0 && _viewModel.SourceVideoHeight != 0;

    private bool HasLightweightWorkspacePanels =>
        _leftWorkspacePanel != LeftWorkspacePanel.None || _isSettingsPanelVisible;

    private double GetLightweightFixedChromeWidth(double currentWindowWidth)
    {
        if (MainContentGrid.ActualWidth > 0)
            return Math.Max(0, currentWindowWidth - MainContentGrid.ActualWidth);

        var contentWidth = GetLightweightElementWidth(LeftPanelHost) +
            GetLightweightColumnWidth(LeftGapColumn) +
            Math.Max(0, CenterColumn.ActualWidth) +
            GetLightweightColumnWidth(RightGapColumn) +
            GetLightweightElementWidth(ControlPanel);
        return Math.Max(0, currentWindowWidth - contentWidth);
    }

    private double GetLightweightMaximumWindowWidth()
    {
        if (!TryGetLightweightWorkArea(out var workArea, out var scale))
            return double.PositiveInfinity;
        var workWidth = workArea.Right - workArea.Left;
        return Math.Max(1, workWidth / scale - LightweightWorkAreaInset);
    }

    private bool TryGetLightweightWorkArea(out NativeRect workArea,
        out double scale)
    {
        workArea = default;
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo))
        {
            scale = 1;
            return false;
        }
        var dpi = GetDpiForWindow(handle);
        scale = (dpi == 0 ? 96d : dpi) / 96d;
        workArea = monitorInfo.WorkArea;
        return true;
    }

    private double GetLightweightCurrentWindowWidth()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0 && GetWindowRect(handle, out var bounds))
        {
            var dpi = GetDpiForWindow(handle);
            var scale = (dpi == 0 ? 96d : dpi) / 96d;
            return Math.Max(1, bounds.Right - bounds.Left) / scale;
        }
        return Math.Max(1, ActualWidth);
    }

    private async Task SwitchBluetoothControlForSelectionChangeAsync()
    {
        Interlocked.Exchange(ref _bluetoothRouteChanging, 1);
        await _bluetoothRouteGate.WaitAsync();
        try
        {
            var targetUdid = _viewModel.SelectedDevice?.Udid;
            if (_activeControlWindow == 0 && _viewModel.IsBluetoothControlEnabled &&
                !DeviceViewModel.UdidEquals(_viewModel.BluetoothControlTargetUdid,
                    targetUdid))
                await _viewModel.SwitchBluetoothControlTargetAsync(targetUdid);
        }
        finally
        {
            ResetControlRouteState();
            Volatile.Write(ref _bluetoothRouteChanging, 0);
            _bluetoothRouteGate.Release();
        }
    }

    private void ResetControlRouteState()
    {
        _controlPointerInitialized = false;
        _lastControlSourceX = 0;
        _lastControlSourceY = 0;
        _lastControlGeometryWidth = 0;
        _lastControlGeometryHeight = 0;
        _lastControlGeometryRotation = 0;
        _controlRemainderX = 0;
        _controlRemainderY = 0;
        _controlWheelRemainder = 0;
        _controlButtons = 0;
        lock (_controlQueueSync)
        {
            _pendingControlDx = 0;
            _pendingControlDy = 0;
            _pendingControlWheel = 0;
            _pendingControlButtons = 0;
            _pendingControlStateDirty = false;
            _pendingControlMotionAt = 0;
        }
        _controlKeyboardUsages.Clear();
        _controlModifierKeys.Clear();
        _controlKeyboardModifiers = 0;
        StopControlPointerTimer();
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (var device in e.NewItems.OfType<DeviceViewModel>())
                ShowDeviceProfileGuidance(device);
        }

        // A source panel is useful once there is a choice. Open it exactly
        // when a new source is added; refreshes and removals must not override
        // the user's current panel choice.
        if (e.Action != NotifyCollectionChangedAction.Add ||
            e.NewItems is null || e.NewItems.Count == 0 || _viewModel.Devices.Count <= 1 ||
            _leftWorkspacePanel == LeftWorkspacePanel.Devices)
            return;

        SetLeftWorkspacePanel(LeftWorkspacePanel.Devices);
        _viewModel.AddDiagnosticLog(AppLog.Event("workspace_left_panel_auto_opened",
            ("reason", "device_added"), ("device_count", _viewModel.Devices.Count)));
    }

    private void ShowDeviceProfileGuidance(DeviceViewModel device)
    {
        if (device.IsMediaCast || string.IsNullOrWhiteSpace(device.Udid) ||
            !_deviceProfileGuidanceShown.Add(device.Udid)) return;

        var identityType = device.IsWireless
            ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired;
        if (DeviceBindingManager.Shared.FindByIdentity(identityType, device.Udid) is not null)
            return;

        var titleKey = device.IsWireless
            ? "DeviceProfileGuidanceWirelessTitle"
            : "DeviceProfileGuidanceWiredTitle";
        var bodyKey = device.IsWireless
            ? "DeviceProfileGuidanceWirelessBody"
            : "DeviceProfileGuidanceWiredBody";
        _viewModel.AddDiagnosticLog(AppLog.Event("device_profile_guidance_shown",
            ("device", AppLog.Device(device.Udid)),
            ("transport", device.IsWireless ? "airplay" : "usb")));
        if (!AppPromptWindow.Confirm(LocalizationService.Get(titleKey),
                LocalizationService.Get(bodyKey))) return;

        OnNavigateReverseControlClick(this, new RoutedEventArgs());
    }

    private void QueueMainPreviewHostSync() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Render, SynchronizeMainPreviewHost);

    private void SynchronizeMainPreviewHost()
    {
        var mediaOnMain = _mediaCastActive && _mediaCastPreviewWindow is null &&
            _viewModel.IsMediaCastSelected;
        var independentOnMain = !mediaOnMain && !_viewModel.IsMediaCastSelected &&
            _secondaryMirrors.IsOpen(_viewModel.SelectedDevice);
        MediaCastSurface.Visibility = mediaOnMain
            ? Visibility.Visible : Visibility.Collapsed;
        IndependentPreviewSurface.Visibility = independentOnMain
            ? Visibility.Visible : Visibility.Collapsed;
        var visible = !mediaOnMain && !_viewModel.IsMediaCastSelected &&
            _viewModel.IsCapturing && !_viewModel.IsAudioOnlyAirPlay &&
            !_viewModel.IsVideoProtected &&
            _viewModel.CurrentSessionHandle != 0 && !independentOnMain;
        MainPreviewHost.SetPresentationVisible(visible);
        if (!visible)
        {
            // HwndHost owns native airspace and cannot be covered by the WPF
            // independent-window notice. Collapsing the host removes that
            // child HWND from composition until the main preview is restored.
            MainPreviewHost.Visibility = Visibility.Collapsed;
            MainPreviewHost.Deactivate();
            return;
        }

        MainPreviewHost.ClearValue(VisibilityProperty);
        MainPreviewHost.Activate();
        MainPreviewHost.SetPresentationVisible(true);
    }

    private void OnDeviceVideoSizeChanged(string udid, uint width, uint height) =>
        _secondaryMirrors.UpdateDevice(udid, width, height);

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => _ = ToggleActiveFullScreenAsync();

    private async Task ToggleActiveFullScreenAsync()
    {
        try
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("fullscreen_toggle_begin",
                ("mode", _viewModel.IsMediaCastSelected ? "media_cast" : "device"),
                ("independent", _mediaCastPreviewWindow is not null ||
                    _secondaryMirrors.IsOpen(_viewModel.SelectedDevice))));
            if (_viewModel.IsMediaCastSelected && _mediaCastPreviewWindow is not null)
                _mediaCastPreviewWindow.ToggleFullScreen();
            else if (_secondaryMirrors.IsOpen(_viewModel.SelectedDevice) &&
                _viewModel.SelectedDevice is { } device)
                _ = await _secondaryMirrors.ToggleFullScreenAsync(device);
            else
                ToggleFullScreen();
            UpdateMediaCastFullScreenButton();
            if (_mediaCastActive) RevealMediaCastControls();
            _viewModel.AddDiagnosticLog(AppLog.Event("fullscreen_toggle_complete",
                ("mode", _viewModel.IsMediaCastSelected ? "media_cast" : "device")));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("fullscreen_toggle_failed",
                ("error", AppLog.Error(error))));
            _viewModel.AddUiLog(LocalizationService.Format("FullScreenFailedFormat", error.Message));
        }
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            WindowState = WindowState.Normal;
            WindowStyle = _restoreWindowStyle;
            ResizeMode = _restoreResizeMode;
            Topmost = _restoreTopmost;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            SetNavigationPaneVisible(true);
            RootNavigation.IsPaneOpen = false;
            RootLayout.Margin = new Thickness(12, 18, 18, 18);
            SetFullScreenPreviewBackground(false);
            HeaderGapRow.Height = new GridLength(18);
            StatsGapRow.Height = new GridLength(14);
            PreviewPanel.BorderThickness = new Thickness(1);
            PreviewPanel.CornerRadius = new CornerRadius(16);
            PreviewPanel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            HeaderPanel.Visibility = Visibility.Visible;
            // Entering full screen applies a temporary local Collapsed value.
            // Clear it on exit so the selected session controls toolbar visibility again.
            EnvironmentPanel.ClearValue(UIElement.VisibilityProperty);
            StatsPanel.Visibility = Visibility.Visible;
            FooterPanel.Visibility = Visibility.Visible;
            ApplyWorkspacePanelState();
            _isFullScreen = false;
            WindowState = _restoreWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _restoreWindowState;
            if (_restoreWasWindowMaximized)
                MaximizeWindow(_windowMaximizeRestoreBounds);
            else
                ApplyWindowFramePolicy();
        }
        else
        {
            _restoreWindowStyle = WindowStyle;
            _restoreWindowState = WindowState;
            _restoreResizeMode = ResizeMode;
            _restoreTopmost = Topmost;
            _restoreWasWindowMaximized = _isWindowMaximized;
            _restoreBounds = _isWindowMaximized
                ? _windowMaximizeRestoreBounds
                : WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            };
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo))
                throw new InvalidOperationException("Unable to resolve the current display bounds.");
            RootNavigation.IsPaneOpen = false;
            CancelLightweightWindowWidthAnimation();
            SizeToContent = SizeToContent.Manual;
            PreviewPanel.ClearValue(WidthProperty);
            SetNavigationPaneVisible(false);
            ++_workspaceTransitionRevision;
            SetWorkspaceSurfaceImmediate(LeftPanelHost, visible: false, width: 300);
            SetWorkspacePageImmediate(DevicePanel, visible: false);
            SetWorkspacePageImmediate(MirroringPanel, visible: false);
            SetWorkspaceSurfaceImmediate(ControlPanel, visible: false, width: 336);
            HeaderPanel.Visibility = Visibility.Collapsed;
            EnvironmentPanel.Visibility = Visibility.Collapsed;
            StatsPanel.Visibility = Visibility.Collapsed;
            FooterPanel.Visibility = Visibility.Collapsed;
            DeviceColumn.Width = new GridLength(0);
            LeftGapColumn.Width = new GridLength(0);
            RightGapColumn.Width = new GridLength(0);
            ControlColumn.Width = new GridLength(0);
            RootLayout.Margin = new Thickness(0);
            SetFullScreenPreviewBackground(true);
            HeaderGapRow.Height = new GridLength(0);
            StatsGapRow.Height = new GridLength(0);
            PreviewPanel.BorderThickness = new Thickness(0);
            PreviewPanel.CornerRadius = new CornerRadius(0);
            PreviewPanel.BorderBrush = Brushes.Black;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            _isFullScreen = true;
            ApplyWindowFramePolicy();
            _ = SetWindowPos(handle, HwndTopMost,
                monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
                monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
                monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
                SwpFrameChanged | SwpShowWindow);
        }
        if (!_viewModel.IsMediaCastSelected) MainPreviewHost.Activate();
        MainPreviewHost.IsFullScreenPresentation = _isFullScreen;
        ApplyApplicationDisplayMode();
        UpdateMediaCastFullScreenButton();
        _viewModel.AddDiagnosticLog(AppLog.Event("main_fullscreen_state",
            ("enabled", _isFullScreen)));
    }

    private void SetFullScreenPreviewBackground(bool isFullScreen)
    {
        if (isFullScreen)
        {
            Background = Brushes.Black;
            AppShell.Background = Brushes.Black;
            RootNavigation.Background = Brushes.Black;
            RootLayout.Background = Brushes.Black;
            MainContentGrid.Background = Brushes.Black;
            CenterPanel.Background = Brushes.Black;
            PreviewPanel.Background = Brushes.Black;
            MediaCastSurface.Background = Brushes.Black;
            MediaCastPlayerHost.Background = Brushes.Black;
            MediaCastVideoHost.Background = Brushes.Black;
            return;
        }

        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        AppShell.ClearValue(Panel.BackgroundProperty);
        RootNavigation.Background = Brushes.Transparent;
        RootLayout.ClearValue(Panel.BackgroundProperty);
        MainContentGrid.ClearValue(Panel.BackgroundProperty);
        CenterPanel.ClearValue(Panel.BackgroundProperty);
        PreviewPanel.SetResourceReference(Panel.BackgroundProperty, "PreviewChromeBrush");
        MediaCastSurface.SetResourceReference(Panel.BackgroundProperty, "PreviewChromeBrush");
        MediaCastPlayerHost.SetResourceReference(Panel.BackgroundProperty, "PreviewChromeBrush");
        MediaCastVideoHost.SetResourceReference(Panel.BackgroundProperty, "PreviewChromeBrush");
    }

    private void UpdateMediaCastFullScreenButton(bool? independentState = null)
    {
        var isFullScreen = independentState ??
            (_mediaCastPreviewWindow?.IsFullScreen ?? _isFullScreen);
        SetAnimatedMediaSymbol(MediaCastFullScreenIcon,
            isFullScreen ? SymbolRegular.FullScreenMinimize20 :
                SymbolRegular.FullScreenMaximize20);
        MediaCastFullScreenButton.ToolTip = LocalizationService.Get(
            isFullScreen ? "IndependentWindowExitFullScreen" : "FullScreenPreview");
    }

    private void SetNavigationPaneVisible(bool visible)
    {
        RootNavigation.IsPaneVisible = visible;
        RootNavigation.ApplyTemplate();
        if (RootNavigation.Template.FindName("PaneGrid", RootNavigation) is FrameworkElement pane)
            pane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMediaCastPreviewWindow()
    {
        if (_mediaCastPreviewWindow is not null)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_preview_activate_existing"));
            _mediaCastPreviewWindow.Activate();
            return;
        }

        var width = MediaCastMediaElement.NaturalVideoWidth > 0
            ? (uint)MediaCastMediaElement.NaturalVideoWidth : 16U;
        var height = MediaCastMediaElement.NaturalVideoHeight > 0
            ? (uint)MediaCastMediaElement.NaturalVideoHeight : 9U;
        _viewModel.AddDiagnosticLog(AppLog.Event("media_preview_window_create",
            ("size", $"{width}x{height}"), ("opened", _mediaOpened),
            ("source", AppLog.MediaSource(_mediaSource))));
        MediaCastSurface.Children.Remove(MediaCastPlayerHost);
        if (!NativePreviewWindow.TryCreateAndShowForContent(MediaCastPlayerHost,
                width, height, LocalizationService.Get("MediaCastWindowTitle"),
                () => !MediaCastMediaElement.IsMuted,
                enabled =>
                {
                    MediaCastMediaElement.IsMuted = !enabled;
                    _viewModel.UpdateMediaCastAudioControls(
                        enabled, MediaCastMediaElement.Volume);
                    UpdateMediaCastStatistics();
                },
                () => 1 + _viewModel.ActiveDeviceSessionCount,
                 () =>
                {
                    var result = _viewModel.MuteOtherDeviceSessions(
                        DeviceViewModel.MediaCastUdid);
                    if (!string.IsNullOrWhiteSpace(result.Message))
                         _viewModel.AddUiLog(result.Message);
                 },
                 AttachMediaCastToMainPreview, out var window,
                 _viewModel.AddDiagnosticLog) || window is null)
        {
            AttachMediaCastToMainPreview();
            throw new InvalidOperationException(
                LocalizationService.Get("PreviewRendererAttachFailed"));
        }

        _mediaCastPreviewWindow = window;
        window.FullScreenChanged += enabled => Dispatcher.BeginInvoke(
            DispatcherPriority.Render, () =>
            {
                if (ReferenceEquals(_mediaCastPreviewWindow, window))
                    UpdateMediaCastFullScreenButton(enabled);
            });
        SynchronizeMainPreviewHost();
        window.Closed += (_, _) =>
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_preview_window_closed"));
            if (ReferenceEquals(_mediaCastPreviewWindow, window))
            {
                _mediaCastPreviewWindow = null;
                SynchronizeMainPreviewHost();
                UpdateMediaCastFullScreenButton();
            }
        };
    }

    private void AttachMediaCastToMainPreview()
    {
        if (!MediaCastSurface.Children.Contains(MediaCastPlayerHost))
            MediaCastSurface.Children.Insert(0, MediaCastPlayerHost);
        SynchronizeMainPreviewHost();
    }

    private void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = CaptureScreenshotAsync();

    private void OnMediaOutputToolbarClick(object sender, RoutedEventArgs e) =>
        OnMediaOutputSettingsRequested();

    // MediaElement is a WPF visual rather than a native capture session. The
    // output services request frames on a worker thread, so marshal the
    // render onto the UI dispatcher and return owned pixel buffers.
    private VideoFrame? CaptureMediaCastVideoFrame(uint width, uint height)
    {
        if (!_mediaCastActive || !_viewModel.IsMediaCastSelected)
            return null;
        var pixels = CaptureMediaCastBgra(width, height, out var stride);
        return pixels is null ? null : new VideoFrame(width, height, stride,
            NextMediaCastOutputTimestamp(), pixels);
    }

    private Nv12VideoFrame? CaptureMediaCastNv12Frame(uint width, uint height)
    {
        if (!_mediaCastActive || !_viewModel.IsMediaCastSelected)
            return null;
        var bgra = CaptureMediaCastBgra(width, height, out _);
        if (bgra is null) return null;
        var nv12 = ConvertBgraToNv12(bgra, width, height);
        return new Nv12VideoFrame(width, height, width,
            NextMediaCastOutputTimestamp(), nv12);
    }

    private long NextMediaCastOutputTimestamp()
    {
        var wallClock = DateTime.UtcNow.Ticks;
        while (true)
        {
            var previous = Volatile.Read(ref _mediaCastOutputTimestamp);
            var next = Math.Max(wallClock, previous + 1);
            if (Interlocked.CompareExchange(ref _mediaCastOutputTimestamp,
                    next, previous) == previous)
                return next;
        }
    }

    private byte[]? CaptureMediaCastBgra(uint requestedWidth,
        uint requestedHeight, out uint stride)
    {
        stride = 0;
        if (requestedWidth < 2 || requestedHeight < 2 ||
            requestedWidth > 3840 || requestedHeight > 2160)
            return null;

        if (!Dispatcher.CheckAccess())
        {
            uint capturedStride = 0;
            var renderedPixels = Dispatcher.Invoke(() => CaptureMediaCastBgra(
                requestedWidth, requestedHeight, out capturedStride));
            stride = capturedStride;
            return renderedPixels;
        }

        // Output capture can request frames at 60 fps. Forcing a full layout
        // pass on every request blocks the dispatcher and competes directly
        // with MediaElement composition. SizeChanged/normal WPF layout already
        // keeps ActualWidth/ActualHeight current; only flush layout when a
        // resize is genuinely pending.
        if (!MediaCastVideoHost.IsMeasureValid ||
            !MediaCastVideoHost.IsArrangeValid)
            MediaCastVideoHost.UpdateLayout();
        var sourceWidth = MediaCastVideoHost.ActualWidth;
        var sourceHeight = MediaCastVideoHost.ActualHeight;
        if (sourceWidth < 1 || sourceHeight < 1)
            return null;
        var targetWidth = checked((int)(requestedWidth & ~1U));
        var targetHeight = checked((int)(requestedHeight & ~1U));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(
                targetWidth / sourceWidth, targetHeight / sourceHeight));
            context.DrawRectangle(new VisualBrush(MediaCastVideoHost), null,
                new Rect(0, 0, sourceWidth, sourceHeight));
        }
        var bitmap = new RenderTargetBitmap(targetWidth, targetHeight,
            96, 96, PixelFormats.Bgra32);
        bitmap.Render(drawing);
        stride = checked((uint)(targetWidth * 4));
        var pixels = new byte[checked((int)(stride * (uint)targetHeight))];
        bitmap.CopyPixels(pixels, checked((int)stride), 0);
        return pixels;
    }

    private static byte[] ConvertBgraToNv12(byte[] bgra, uint width, uint height)
    {
        var w = checked((int)width);
        var h = checked((int)height);
        var yPlaneBytes = checked(w * h);
        var output = new byte[checked(yPlaneBytes + yPlaneBytes / 2)];
        static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
        // The capture pipeline carries display RGB as full-range BT.709 NV12.
        // Keep the range explicit so FFmpeg and downstream players do not
        // expand light UI grays a second time as video-range samples.
        static int Y(int r, int g, int b) =>
            (54 * r + 183 * g + 19 * b + 128) >> 8;
        static int U(int r, int g, int b) =>
            ((-29 * r - 99 * g + 128 * b + 128) >> 8) + 128;
        static int V(int r, int g, int b) =>
            ((128 * r - 116 * g - 12 * b + 128) >> 8) + 128;

        for (var y = 0; y < h; ++y)
        for (var x = 0; x < w; ++x)
        {
            var offset = (y * w + x) * 4;
            output[y * w + x] = ClampByte(Y(bgra[offset + 2],
                bgra[offset + 1], bgra[offset]));
        }
        var uvOffset = yPlaneBytes;
        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            var r = 0;
            var g = 0;
            var b = 0;
            var count = 0;
            for (var dy = 0; dy < 2 && y + dy < h; ++dy)
            for (var dx = 0; dx < 2 && x + dx < w; ++dx)
            {
                var offset = ((y + dy) * w + x + dx) * 4;
                b += bgra[offset];
                g += bgra[offset + 1];
                r += bgra[offset + 2];
                ++count;
            }
            r /= count;
            g /= count;
            b /= count;
            var uv = uvOffset + (y / 2) * w + x;
            output[uv] = ClampByte(U(r, g, b));
            output[uv + 1] = ClampByte(V(r, g, b));
        }
        return output;
    }

    private async Task CaptureScreenshotAsync()
    {
        if (!await _screenshotGate.WaitAsync(0))
        {
            _viewModel.AddUiLog(LocalizationService.Get("ScreenshotBusy"));
            return;
        }
        try
        {
            var suggested = ScreenshotService.CreateDefaultPath();
            var dialog = new SaveFileDialog
            {
                Title = LocalizationService.Get("ScreenshotSaveTitle"),
                Filter = LocalizationService.Get("ScreenshotPngFilter"),
                DefaultExt = ".png",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Path.GetDirectoryName(suggested),
                FileName = Path.GetFileName(suggested),
            };
            if (dialog.ShowDialog(this) != true) return;
            var path = dialog.FileName;
            var saved = _mediaCastActive && _viewModel.IsMediaCastSelected
                ? ScreenshotService.CaptureVisualPng(MediaCastVideoHost, path)
                : await Task.Run(() => _viewModel.CaptureScreenshot(path));
            _viewModel.AddUiLog(LocalizationService.Format("ScreenshotSavedFormat", saved));
            _viewModel.AddDiagnosticLog(AppLog.Event("screenshot_complete",
                ("mode", _mediaCastActive && _viewModel.IsMediaCastSelected
                    ? "media_cast" : "device"), ("success", true)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("screenshot_failed",
                ("error", AppLog.Error(error))));
            _viewModel.AddUiLog(LocalizationService.Format("ScreenshotFailedFormat", error.Message));
        }
        finally
        {
            _screenshotGate.Release();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = ResolvePreviewKey(e);
        if (TryGetShortcutAction(KeyInterop.VirtualKeyFromKey(key),
                out var configuredAction))
        {
            if (!_registeredHotKeyIds.Contains(HotKeyId(configuredAction)))
                HandleConfiguredShortcut(configuredAction);
            e.Handled = true;
            return;
        }
        if (TryRoutePreviewKeyboardEvent(key, Controls.PreviewKeyboardKind.Down))
        {
            e.Handled = true;
            return;
        }
        if (IsBluetoothControlActive &&
            Keyboard.Modifiers == ModifierKeys.None &&
            (e.Key is Key.LWin or Key.RWin or Key.Apps or Key.Escape))
        {
            e.Handled = true;
            return;
        }
        if (_mediaCastActive && _viewModel.IsMediaCastSelected &&
            Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is not TextBoxBase &&
            Keyboard.FocusedElement is not Slider &&
            Keyboard.FocusedElement is not ButtonBase)
        {
            if (e.Key is Key.Space or Key.K)
                SetLocalMediaCastPlayback(!_mediaShouldPlay);
            else if (e.Key == Key.Left)
                SeekMediaCastLocally(ReadMediaCastControlPosition(MediaCastMediaElement) - 10);
            else if (e.Key == Key.Right)
                SeekMediaCastLocally(ReadMediaCastControlPosition(MediaCastMediaElement) + 10);
            else if (e.Key == Key.M)
                OnMediaCastMuteClick(this, new RoutedEventArgs());
            else
                goto StandardShortcut;
            e.Handled = true;
            return;
        }

    StandardShortcut:
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.F11) _ = ToggleActiveFullScreenAsync();
        else if (e.Key == Key.Escape && _isFullScreen) ToggleFullScreen();
        else if (e.Key == Key.F5) _ = _viewModel.RefreshAsync(forceDeviceEnumeration: true);
        else if (ctrl && e.Key == Key.R) RefreshPreview();
        else if (ctrl && shift && e.Key == Key.P) OnPreviewWindowClick(this, new RoutedEventArgs());
        else if (ctrl && e.Key == Key.L && Application.Current is App app)
            app.ShowAboutWindow(this, _viewModel, showDiagnostics: true);
        else if (ctrl && e.Key == Key.M) _viewModel.PlayAudio = !_viewModel.PlayAudio;
        else if (ctrl && e.Key == Key.S) _ = CaptureScreenshotAsync();
        else return;
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = ResolvePreviewKey(e);
        if (TryGetShortcutAction(KeyInterop.VirtualKeyFromKey(key), out _))
        {
            e.Handled = true;
            return;
        }
        if (TryRoutePreviewKeyboardEvent(key, Controls.PreviewKeyboardKind.Up))
            e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            MouseButton.Right => ShortcutMouseButton.Right,
            MouseButton.Middle => ShortcutMouseButton.Middle,
            _ => ShortcutMouseButton.None,
        };
        if (button == ShortcutMouseButton.None) return;
        var action = _bluetoothShortcuts.FirstOrDefault(pair =>
            pair.Value.MatchesMouse(button, Keyboard.Modifiers)).Key;
        if (action == default || action == BluetoothShortcutAction.ReverseControl) return;
        HandleConfiguredShortcut(action);
        e.Handled = true;
    }

    private static Key ResolvePreviewKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        // When a non-English Windows IME is active, WPF reports the key-down
        // as VK_PROCESSKEY (Key.ImeProcessed) and only exposes the physical
        // key through ImeProcessedKey. Forwarding 229 would drop the key-down
        // while the matching key-up still contains A/S/D/etc.
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private bool TryRoutePreviewKeyboardEvent(Key key,
        Controls.PreviewKeyboardKind kind)
    {
        if (_activeControlWindow != 0 ||
            (!IsUsbControlActive && !IsBluetoothControlActive)) return false;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;
        _viewModel.AddDiagnosticLog(AppLog.Event("preview_keyboard_fallback",
            ("kind", kind), ("virtual_key", virtualKey),
            ("device", AppLog.Device(_viewModel.SelectedDevice?.Udid)),
            ("usb", IsUsbControlActive), ("bluetooth", IsBluetoothControlActive)));
        HandleControlKeyboardInput(new Controls.PreviewKeyboardEventArgs(
            kind, virtualKey, 0), _viewModel.SelectedDevice?.Udid);
        return true;
    }

    private void OnShortcutSettingsClick(object sender, RoutedEventArgs e) =>
        ShowShortcutSettings();

    private async void OnClearBluetoothBindingsClick(object sender, RoutedEventArgs e)
    {
        if (!AppPromptWindow.Confirm(
                LocalizationService.Get("ClearBluetoothBindingsTitle"),
                LocalizationService.Get("ClearBluetoothBindingsBody")))
            return;
        if (!await _viewModel.ClearBluetoothControlBindingsAsync())
            AppPromptWindow.Inform(
                LocalizationService.Get("ClearBluetoothBindingsTitle"),
                LocalizationService.Get("ClearBluetoothBindingsFailed"));
    }

    private void ToggleBluetoothControlFromHotkey()
    {
        BluetoothControlNoticeWindow.TryCloseActive();
        _ = _viewModel.ToggleBluetoothControlAsync();
    }

    private void HandleConfiguredShortcut(BluetoothShortcutAction action)
    {
        if (action == BluetoothShortcutAction.BossKey)
        {
            _ = ToggleBossKeyWindowsAsync();
            return;
        }
        if (action == BluetoothShortcutAction.BluetoothControl)
            _ = _viewModel.ToggleBluetoothControlAsync();
        else if (action == BluetoothShortcutAction.WirelessControl)
            _ = _viewModel.ToggleWirelessControlAsync();
        else if (action == BluetoothShortcutAction.WiredControl)
            _ = _viewModel.ToggleWiredControlAsync();
        else
            _ = SendConfiguredSystemShortcutAsync(action);
    }

    private async Task ToggleBossKeyWindowsAsync()
    {
        if (Interlocked.Exchange(ref _bossKeyChanging, 1) != 0) return;
        var restoring = _bossKeyHidden;
        var routeHeld = false;
        Interlocked.Exchange(ref _bluetoothRouteChanging, 1);
        try
        {
            if (restoring)
            {
                BossKeyWindowVisibility.RestoreAll();
                NativePreviewWindow.SetAllBossKeyHidden(false);
            }
            else
            {
                _bossKeyHidden = true;
                ApplyBluetoothControlInputState(activateIndependentWindow: false);
                BossKeyWindowVisibility.HideAll();
                NativePreviewWindow.SetAllBossKeyHidden(true);
            }

            await _bluetoothRouteGate.WaitAsync();
            routeHeld = true;
            if (restoring)
            {
                _bossKeyHidden = false;
                ApplyBluetoothControlInputState();
            }
            else if (_viewModel.IsBluetoothControlEnabled)
            {
                try { await _viewModel.ReleaseBluetoothControlInputAsync(); }
                catch (Exception error)
                {
                    _viewModel.AddDiagnosticLog(AppLog.Event(
                        "boss_key_input_release_failed",
                        ("error", AppLog.Error(error))));
                }
            }
            _viewModel.AddDiagnosticLog(AppLog.Event("boss_key_toggled",
                ("hidden", _bossKeyHidden),
                ("wpf_windows", BossKeyWindowVisibility.HiddenWindowCount),
                ("reverse_control", IsBluetoothControlActive)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("boss_key_toggle_failed",
                ("hidden", _bossKeyHidden), ("error", AppLog.Error(error))));
        }
        finally
        {
            if (routeHeld) _bluetoothRouteGate.Release();
            Volatile.Write(ref _bluetoothRouteChanging, 0);
            Volatile.Write(ref _bossKeyChanging, 0);
        }
    }

    private bool TryGetShortcutAction(int virtualKey,
        out BluetoothShortcutAction action)
    {
        var modifiers = Keyboard.Modifiers;
        foreach (var candidate in Enum.GetValues<BluetoothShortcutAction>())
        {
            if (_bluetoothShortcuts.TryGetValue(candidate, out var shortcut) &&
                shortcut.MatchesVirtualKey(virtualKey,
                    modifiers.HasFlag(ModifierKeys.Control),
                    modifiers.HasFlag(ModifierKeys.Alt),
                    modifiers.HasFlag(ModifierKeys.Shift)))
            {
                action = candidate;
                return true;
            }
        }
        action = default;
        return false;
    }

    private static bool TryGetShortcutActionByHotKeyId(int hotKeyId,
        out BluetoothShortcutAction action)
    {
        foreach (var candidate in Enum.GetValues<BluetoothShortcutAction>())
        {
            if (HotKeyId(candidate) == hotKeyId)
            {
                action = candidate;
                return true;
            }
        }
        action = default;
        return false;
    }

    private async Task SendConfiguredSystemShortcutAsync(
        BluetoothShortcutAction action)
    {
        var target = _activeControlWindow != 0 ? _activeControlUdid :
            _viewModel.SelectedDevice?.Udid;
        if (action == BluetoothShortcutAction.ReverseControl) return;
        var indigoButton = GetIndigoButton(action);
        var usage = action switch
        {
            BluetoothShortcutAction.ControlCenter => (byte)0x06, // C
            BluetoothShortcutAction.NotificationCenter => (byte)0x11, // N
            BluetoothShortcutAction.AppSwitcher => (byte)0,
            BluetoothShortcutAction.Home => (byte)0x0B, // H
            BluetoothShortcutAction.Dock => (byte)0x04, // A
            BluetoothShortcutAction.Siri => (byte)0x16, // S
            _ => (byte)0,
        };
        try
        {
            var bluetoothTarget = _viewModel.BluetoothControlIsConnected &&
                _viewModel.IsBluetoothControlTarget(target);
            var usbTarget = _viewModel.UsbControlIsInputEnabled &&
                _viewModel.IsUsbControlTarget(target);
            if (!bluetoothTarget && !usbTarget) return;
            if (action == BluetoothShortcutAction.AppSwitcher && bluetoothTarget)
                await _viewModel.SendBluetoothAppSwitcherAsync();
            else if (usage != 0 && bluetoothTarget)
                await _viewModel.SendBluetoothSystemShortcutAsync(usage);
            if (action == BluetoothShortcutAction.AppSwitcher && usbTarget)
            {
                // iPhone/iPad accepts the hardware Home button twice as the
                // app-switcher gesture. The direct USB/Wireless HID surface
                // does not expose the Bluetooth navigation-menu report, so
                // mirror the already-working Home shortcut instead.
                var home = GetIndigoButton(BluetoothShortcutAction.Home)!.Value;
                await SendUsbButtonPulseAsync(home, target);
                await Task.Delay(AppSwitcherDoublePressInterval);
                await SendUsbButtonPulseAsync(home, target);
            }
            else if (indigoButton is { } button && usbTarget)
            {
                await SendUsbButtonPulseAsync(button, target);
            }
            else if (usage != 0 && usbTarget)
            {
                await _viewModel.SendUsbKeyboardAsync([usage]);
                await Task.Delay(20);
                await _viewModel.SendUsbKeyboardAsync([]);
            }
            _viewModel.AddDiagnosticLog(AppLog.Event("system_shortcut_sent",
                ("action", action.ToString()), ("device", AppLog.Device(target)),
                ("bluetooth", bluetoothTarget), ("usb", usbTarget)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "system_shortcut_failed",
                ("action", action.ToString()), ("device", AppLog.Device(target)),
                ("error", AppLog.Error(error))));
        }
    }

    private async Task SendUsbButtonPulseAsync(
        (ushort Page, ushort Code, int HoldMs) button, string? targetUdid)
    {
        await _viewModel.SendUsbButtonAsync(button.Page, button.Code, "down", targetUdid);
        await Task.Delay(button.HoldMs);
        await _viewModel.SendUsbButtonAsync(button.Page, button.Code, "up", targetUdid);
    }

    private static (ushort Page, ushort Code, int HoldMs)? GetIndigoButton(
        BluetoothShortcutAction action) => action switch
        {
            BluetoothShortcutAction.Home =>
                (CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                 CoreDeviceTouchProtocol.IndigoHome, 50),
            BluetoothShortcutAction.Siri =>
                (CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                 CoreDeviceTouchProtocol.IndigoSiri, 1000),
            BluetoothShortcutAction.VolumeUp =>
                (CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                 CoreDeviceTouchProtocol.IndigoVolumeUp, 50),
            BluetoothShortcutAction.VolumeDown =>
                (CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                 CoreDeviceTouchProtocol.IndigoVolumeDown, 50),
            BluetoothShortcutAction.LockScreen =>
                (CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                 // iOS treats a very short side-button pulse as bounce noise.
                 CoreDeviceTouchProtocol.IndigoLock, 500),
            _ => null,
        };

    private void SetWindowsCursorHidden(bool hidden)
    {
        if (hidden)
        {
            if (IsSystemCursorVisible(out var queryFailed))
                while (ShowCursor(false) >= 0) { }
            else if (queryFailed && !_windowsCursorHidden)
                ShowCursor(false);
            _windowsCursorHidden = true;
        }
        else
        {
            if (!IsSystemCursorVisible(out var queryFailed))
            {
                if (queryFailed && _windowsCursorHidden) ShowCursor(true);
                else while (ShowCursor(true) < 0) { }
            }
            _windowsCursorHidden = false;
        }
    }

    private bool IsSystemCursorVisible(out bool queryFailed)
    {
        var cursor = new CursorInfo
        {
            Size = (uint)Marshal.SizeOf<CursorInfo>(),
        };
        if (!GetCursorInfo(ref cursor))
        {
            queryFailed = true;
            _viewModel.AddDiagnosticLog(AppLog.Event("windows_cursor_query_failed",
                ("win32_error", Marshal.GetLastWin32Error())));
            return false;
        }
        queryFailed = false;
        return (cursor.Flags & CursorShowing) != 0;
    }

    private void SetSystemKeySuppression(bool enabled)
    {
        if (enabled)
        {
            if (_keyboardHook == 0)
                _keyboardHook = SetWindowsHookEx(13, _keyboardHookProc, 0, 0);
        }
        else if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private nint KeyboardHookProcedure(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsBluetoothControlActive)
        {
            var data = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            if (data.VirtualKey is 0x5B or 0x5C or 0x5D or 0x5F)
                return 1;
            var alt = GetAsyncKeyState(0x12) < 0;
            var control = GetAsyncKeyState(0x11) < 0;
            if ((data.VirtualKey == 0x09 && alt) ||
                (data.VirtualKey == 0x1B && (alt || control)) ||
                (data.VirtualKey == 0x73 && alt))
                return 1;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopMost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(0)]
        internal ushort Flags;
        [FieldOffset(2)]
        internal ushort AlignmentPadding;
        [FieldOffset(4)]
        internal ushort ButtonFlags;
        [FieldOffset(6)]
        internal ushort ButtonData;
        [FieldOffset(8)]
        internal uint RawButtons;
        [FieldOffset(12)]
        internal int LastX;
        [FieldOffset(16)]
        internal int LastY;
        [FieldOffset(20)]
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct RawInput
    {
        [FieldOffset(0)]
        internal RawInputHeader Header;
        [FieldOffset(24)]
        internal RawMouse Mouse;
        [FieldOffset(24)]
        internal RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        internal ushort MakeCode;
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort VirtualKey;
        internal uint Message;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Cursor;
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookType,
        LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command,
        nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, int message,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(nint rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
