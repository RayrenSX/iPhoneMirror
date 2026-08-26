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
    private long _mediaCastOutputTimestamp;
    private HwndSource? _windowSource;
    private int _lastControlSourceX;
    private int _lastControlSourceY;
    private uint _lastControlGeometryWidth;
    private uint _lastControlGeometryHeight;
    private int _lastControlGeometryRotation;
    private bool _controlPointerInitialized;
    private readonly Timer _controlPointerTimer;
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
    private const int BluetoothControlHotKeyId = 0x4981;
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

    private bool IsBluetoothControlActive =>
        _viewModel.BluetoothControlIsInputEnabled &&
        (_activeControlWindow != 0 ||
         _viewModel.IsBluetoothControlTarget(_viewModel.SelectedDevice?.Udid));

    private static readonly TimeSpan DeviceDragHoldDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan WorkspaceTransitionDuration = TimeSpan.FromMilliseconds(280);
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
            ThemeComboBox.SelectedValue = app.UpdateSettings.Theme.ToString();
        _themeControlReady = true;
        _workspaceControlsReady = true;
        _viewModel = new MainViewModel();
        MainPreviewHost.PointerInput += OnControlPointerInput;
        MainPreviewHost.KeyboardInput += OnControlKeyboardInput;
        _viewModel.SetMediaCastOutputProviders(
            CaptureMediaCastNv12Frame, CaptureMediaCastVideoFrame,
            afterSequence => _mediaCastAudioDecoder.GetPacket(afterSequence));
        _secondaryMirrors = new MultiDevicePreviewManager(_viewModel);
        _secondaryMirrors.ReverseControlRequested += OnIndependentReverseControlRequested;
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
        if (_windowSource is not null)
            _hotKeyRegistered = RegisterHotKey(_windowSource.Handle,
                BluetoothControlHotKeyId, 0, 0x78);
    }

    private void OnControlPointerInput(object? sender,
        Controls.PreviewPointerEventArgs e)
    {
        if (_activeControlWindow != 0) return;
        HandleControlPointerInput(e);
    }

    private void HandleControlPointerInput(Controls.PreviewPointerEventArgs e)
    {
        if (!IsBluetoothControlActive) return;
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
            _controlPointerTimer.Change(1, 8);
    }

    private void StopControlPointerTimer()
    {
        Interlocked.Exchange(ref _controlPointerTimerArmed, 0);
        _controlPointerTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task FlushControlPointerAsync(bool force = false)
    {
        if (!IsBluetoothControlActive ||
            Interlocked.Exchange(ref _controlPointerFlushInFlight, 1) != 0)
            return;
        int dx;
        int dy;
        int wheel;
        byte buttons;
        long motionAt;
        lock (_controlQueueSync)
        {
            if (!force && _pendingControlDx == 0 && _pendingControlDy == 0 &&
                _pendingControlWheel == 0 && !_pendingControlStateDirty)
            {
                StopControlPointerTimer();
                Volatile.Write(ref _controlPointerFlushInFlight, 0);
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
        try
        {
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
        if (_activeControlWindow == 0 ||
            !DeviceViewModel.UdidEquals(_activeControlUdid, udid)) return;
        HandleControlPointerInput(e);
    }

    private void OnIndependentKeyboardInput(string udid,
        Controls.PreviewKeyboardEventArgs e)
    {
        if (_activeControlWindow == 0 ||
            !DeviceViewModel.UdidEquals(_activeControlUdid, udid)) return;
        HandleControlKeyboardInput(e);
    }

    private async void OnIndependentPreviewClosed(string udid)
    {
        try
        {
            QueueMainPreviewHostSync();
            if (!DeviceViewModel.UdidEquals(_activeControlUdid, udid)) return;
            _activeControlWindow = 0;
            _activeControlUdid = null;
            ClipCursor(IntPtr.Zero);
            if (_viewModel.IsBluetoothControlEnabled)
                await _viewModel.DisableBluetoothControlAsync();
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_reverse_control_close_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
    }

    private async void OnIndependentReverseControlRequested(string udid, nint window)
    {
        try
        {
            if (_viewModel.IsBluetoothControlEnabled && _activeControlWindow == window)
            {
                _activeControlWindow = 0;
                _activeControlUdid = null;
                await _viewModel.DisableBluetoothControlAsync();
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
            if (IsBluetoothControlActive)
                ClipCursorToWindow(window);
            else
                ClipCursor(IntPtr.Zero);
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
            ClipCursor(IntPtr.Zero);
            _viewModel.AddDiagnosticLog(AppLog.Event(
                "independent_reverse_control_request_failed",
                ("device", AppLog.Device(udid)),
                ("window", AppLog.Handle((ulong)window.ToInt64())),
                ("error", AppLog.Error(error))));
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
        if (_activeControlWindow != 0) return;
        HandleControlKeyboardInput(e);
    }

    private async void HandleControlKeyboardInput(
        Controls.PreviewKeyboardEventArgs e)
    {
        if (!IsBluetoothControlActive)
            return;
        if (e.Kind == Controls.PreviewKeyboardKind.Reset)
        {
            _controlKeyboardUsages.Clear();
            _controlModifierKeys.Clear();
            _controlKeyboardModifiers = 0;
            await _viewModel.SendBluetoothKeyboardAsync(0, []);
            return;
        }
        if (_rawKeyboardInputEnabled) return;
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
        await _viewModel.SendBluetoothKeyboardAsync(_controlKeyboardModifiers,
            _controlKeyboardUsages.ToArray());
    }

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
        SetSystemKeySuppression(false);
        ClipCursor(IntPtr.Zero);
        StopControlPointerTimer();
        _controlPointerTimer.Dispose();
        RegisterRawMouseInput(false);
        if (_windowSource?.Handle is nint hwnd && hwnd != 0)
        {
            if (_hotKeyRegistered) UnregisterHotKey(hwnd, BluetoothControlHotKeyId);
            _hotKeyRegistered = false;
        }
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
            IsBluetoothControlActive &&
            (_rawMouseInputEnabled || _rawKeyboardInputEnabled))
        {
            ProcessRawMouseInput(lParam);
            handled = true;
            return 0;
        }
        if (message == WmHotKey && wParam.ToInt32() == BluetoothControlHotKeyId)
        {
            BluetoothControlNoticeWindow.TryCloseActive();
            _ = _viewModel.ToggleBluetoothControlAsync();
            handled = true;
            return 0;
        }
        if (message == WmSetCursor && IsBluetoothControlActive)
        {
            SetCursor(0);
            handled = true;
            return 1;
        }
        if ((message is WmActivateApp or WmSetFocus) &&
            IsBluetoothControlActive)
        {
            SetCursor(0);
            if (_activeControlWindow != 0) ClipCursorToWindow(_activeControlWindow);
            else ClipCursorToPreview();
        }
        if (!WindowsAutoPlayGuard.ShouldCancel(message,
                _viewModel.HasAnyCaptureSession))
            return 0;

        handled = true;
        _viewModel.AddDiagnosticLog(AppLog.Event("autoplay_cancelled",
            ("message", "WM_QUERYCANCELAUTOPLAY"), ("capture", true)));
        return 1;
    }

    private void RegisterRawMouseInput(bool enabled)
    {
        var hwnd = _windowSource?.Handle ?? 0;
        if (hwnd == 0) return;
        if (enabled == _rawMouseInputEnabled &&
            enabled == _rawKeyboardInputEnabled) return;
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            // Raw Input owns movement, buttons, and wheel events. Suppress
            // legacy mouse messages to avoid duplicate HID reports.
            Flags = enabled ? RidevInputSink | RidevNoLegacy : RidevRemove,
            Target = enabled ? hwnd : 0,
        };
        // Keyboard input stays on the preview window's existing key-message
        // path. Remove any previous raw keyboard registration explicitly.
        var keyboard = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x06,
            Flags = enabled ? RidevInputSink | RidevNoLegacy : RidevRemove,
            Target = enabled ? hwnd : 0,
        };
        var deviceSize = (uint)Marshal.SizeOf<RawInputDevice>();
        var registered = RegisterRawInputDevices([device], 1, deviceSize);
        var keyboardRegistered = RegisterRawInputDevices([keyboard], 1, deviceSize);
        _rawMouseInputEnabled = registered && enabled;
        _rawKeyboardInputEnabled = keyboardRegistered && enabled;
        MainPreviewHost.SuppressMouseMove = _rawMouseInputEnabled;
        if (enabled)
        {
            MainPreviewHost.Focus();
            ClipCursorToPreview();
        }
        else
        {
            ClipCursor(IntPtr.Zero);
        }
    }

    private void ClipCursorToPreview()
    {
        ClipCursorToWindow(MainPreviewHost.WindowHandle);
    }

    private void ClipCursorToWindow(nint window)
    {
        if (!GetWindowRect(window, out var rect)) return;
        ClipCursor(ref rect);
    }

    private void ProcessRawMouseInput(nint rawInput)
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
        if (virtualKey == 0x78)
        {
            if (!isKeyUp)
            {
                BluetoothControlNoticeWindow.TryCloseActive();
                if (!_hotKeyRegistered)
                    _ = _viewModel.ToggleBluetoothControlAsync();
            }
            return;
        }
        if (!TryMapVirtualKey(virtualKey, out var usage, out var modifier)) return;
        var modifierIdentity = RawModifierKeyIdentity(keyboard, virtualKey);
        if (isKeyUp)
        {
            if (modifier != 0) _controlModifierKeys.Remove(modifierIdentity);
            else if (usage != 0) _controlKeyboardUsages.Remove(usage);
        }
        else
        {
            if (modifier != 0) _controlModifierKeys.Add(modifierIdentity);
            else if (usage != 0) _controlKeyboardUsages.Add(usage);
        }
        _controlKeyboardModifiers = ModifierMask(_controlModifierKeys);
        _ = _viewModel.SendBluetoothKeyboardAsync(_controlKeyboardModifiers,
            _controlKeyboardUsages.ToArray());
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

    private static int RawModifierKeyIdentity(RawKeyboard keyboard, int virtualKey)
    {
        if (virtualKey == 0x10) return keyboard.MakeCode == 0x36 ? 0xA1 : 0xA0;
        if (virtualKey == 0x11) return (keyboard.Flags & 0x02) != 0 ? 0xA3 : 0xA2;
        if (virtualKey == 0x12) return (keyboard.Flags & 0x02) != 0 ? 0xA5 : 0xA4;
        return ModifierKeyIdentity(virtualKey);
    }

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
        ApplyWorkspacePanelState(animate: IsLoaded && !_isFullScreen,
            animateSettings: false);
        _viewModel.AddDiagnosticLog(AppLog.Event("workspace_left_panel_changed",
            ("panel", panel.ToString().ToLowerInvariant())));
    }

    private void SetSettingsPanelVisible(bool visible)
    {
        if (_isSettingsPanelVisible == visible) return;
        _isSettingsPanelVisible = visible;
        ApplyWorkspacePanelState(animate: IsLoaded && !_isFullScreen,
            animateLeft: false);
        _viewModel.AddDiagnosticLog(AppLog.Event("workspace_settings_panel_changed",
            ("visible", visible)));
    }

    private void ApplyWorkspacePanelState(bool animate = false,
        bool animateLeft = true, bool animateSettings = true)
    {
        var showMirroring = _leftWorkspacePanel == LeftWorkspacePanel.Mirroring;
        var showDevices = _leftWorkspacePanel == LeftWorkspacePanel.Devices;
        var showSettings = _isSettingsPanelVisible;
        var showLeftPanel = showMirroring || showDevices;
        DeviceColumn.Width = GridLength.Auto;
        ControlColumn.Width = GridLength.Auto;
        LeftGapColumn.Width = showLeftPanel ? new GridLength(18) : new GridLength(0);
        RightGapColumn.Width = showSettings ? new GridLength(18) : new GridLength(0);
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
            ++_workspaceTransitionRevision;
            SetWorkspaceSurfaceImmediate(LeftPanelHost, showLeftPanel, 300);
            SetWorkspacePageImmediate(DevicePanel, showDevices);
            SetWorkspacePageImmediate(MirroringPanel, showMirroring);
            SetWorkspaceSurfaceImmediate(ControlPanel, showSettings, 336);
            return;
        }

        var revision = ++_workspaceTransitionRevision;
        if (animateLeft)
        {
            AnimateWorkspaceSurface(LeftPanelHost, showLeftPanel, 300,
                fromLeft: true, revision);
            AnimateWorkspacePage(DevicePanel, showDevices, fromLeft: true, revision);
            AnimateWorkspacePage(MirroringPanel, showMirroring, fromLeft: true, revision);
        }
        else
        {
            SetWorkspaceSurfaceImmediate(LeftPanelHost, showLeftPanel, 300);
            SetWorkspacePageImmediate(DevicePanel, showDevices);
            SetWorkspacePageImmediate(MirroringPanel, showMirroring);
        }

        if (animateSettings)
            AnimateWorkspaceSurface(ControlPanel, showSettings, 336,
                fromLeft: false, revision);
        else
            SetWorkspaceSurfaceImmediate(ControlPanel, showSettings, 336);
    }

    private static void SetWorkspacePageImmediate(FrameworkElement element, bool visible)
    {
        element.BeginAnimation(OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.XProperty, null);
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

    private void AnimateWorkspaceSurface(FrameworkElement element, bool visible,
        double width, bool fromLeft, int revision)
    {
        var currentWidth = element.Visibility == Visibility.Visible
            ? Math.Max(0, element.ActualWidth)
            : 0;
        element.BeginAnimation(WidthProperty, null);
        element.Width = currentWidth;
        if (visible)
        {
            element.Visibility = Visibility.Visible;
            if (ReferenceEquals(element, LeftPanelHost)) element.Opacity = 1;
        }

        var widthAnimation = CreateWorkspaceAnimation(currentWidth, visible ? width : 0);
        widthAnimation.Completed += (_, _) =>
        {
            if (revision != _workspaceTransitionRevision) return;
            element.BeginAnimation(WidthProperty, null);
            element.Width = visible ? width : 0;
            if (!visible) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(WidthProperty, widthAnimation);

        if (!ReferenceEquals(element, LeftPanelHost))
            AnimateWorkspacePage(element, visible, fromLeft, revision);
    }

    private void AnimateWorkspacePage(FrameworkElement element, bool visible,
        bool fromLeft, int revision)
    {
        if (!visible && element.Visibility != Visibility.Visible) return;
        element.BeginAnimation(OpacityProperty, null);
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        if (visible) element.Visibility = Visibility.Visible;

        var direction = fromLeft ? -1d : 1d;
        var opacity = CreateWorkspaceAnimation(visible ? 0.35 : element.Opacity,
            visible ? 1 : 0);
        var translation = CreateWorkspaceAnimation(visible ? direction * 18 : 0,
            visible ? 0 : direction * 14);
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

    private static DoubleAnimation CreateWorkspaceAnimation(double from, double to) =>
        new(from, to, WorkspaceTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };

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
        if (e.PropertyName == nameof(MainViewModel.SelectedDevice) &&
            _activeControlWindow == 0 && _viewModel.IsBluetoothControlEnabled &&
            !_viewModel.IsBluetoothControlTarget(_viewModel.SelectedDevice?.Udid))
        {
            // The HID peripheral remains connected to the device that enabled
            // control. Never keep sending it mouse input from a newly selected
            // main preview; independent-window control owns its own target.
            _ = _viewModel.DisableBluetoothControlAsync();
        }
        if (e.PropertyName is nameof(MainViewModel.IsBluetoothControlEnabled) or
            nameof(MainViewModel.BluetoothControlIsConnected) or
            nameof(MainViewModel.BluetoothControlIsInputEnabled) or
            nameof(MainViewModel.SelectedDevice))
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedDevice) &&
                _viewModel.IsBluetoothControlEnabled && _activeControlWindow == 0)
            {
                _activeControlUdid = _viewModel.SelectedDevice?.Udid;
                if (IsLoaded) _refreshTimer.Start();
            }
            else if (!_viewModel.IsBluetoothControlEnabled)
            {
                _activeControlWindow = 0;
                _activeControlUdid = null;
                if (IsLoaded) _refreshTimer.Start();
            }
            var controlActive = IsBluetoothControlActive;
            MainPreviewHost.CapturePointerInput =
                controlActive && _activeControlWindow == 0;
            SetWindowsCursorHidden(controlActive);
            SetSystemKeySuppression(controlActive);
            RegisterRawMouseInput(controlActive &&
                _activeControlWindow == 0);
            if (controlActive && _activeControlWindow != 0)
            {
                // The pairing guidance is owned by the main window. Restore
                // the independent preview to the foreground when it closes,
                // otherwise its foreground-only native input path stays idle.
                if (e.PropertyName != nameof(MainViewModel.SelectedDevice))
                    _secondaryMirrors.Activate(_activeControlUdid);
                ClipCursorToWindow(_activeControlWindow);
            }
            else if (!controlActive)
            {
                // Always release a process-wide cursor clip on disable,
                // disconnect, startup failure, or while waiting for HID
                // subscription. ShowWindow/focus changes do not clear it.
                ClipCursor(IntPtr.Zero);
                _controlButtons = 0;
                lock (_controlQueueSync)
                {
                    _pendingControlButtons = 0;
                    _pendingControlDx = 0;
                    _pendingControlDy = 0;
                    _pendingControlWheel = 0;
                    _pendingControlStateDirty = false;
                }
                _controlRemainderX = 0;
                _controlRemainderY = 0;
                _controlWheelRemainder = 0;
                _controlKeyboardUsages.Clear();
                _controlModifierKeys.Clear();
                _controlKeyboardModifiers = 0;
                StopControlPointerTimer();
                _controlPointerInitialized = false;
            }
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

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
        static int Y(int r, int g, int b) =>
            ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
        static int U(int r, int g, int b) =>
            ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
        static int V(int r, int g, int b) =>
            ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

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
        if (e.Key == Key.F9 && Keyboard.Modifiers == ModifierKeys.None)
        {
            BluetoothControlNoticeWindow.TryCloseActive();
            _ = _viewModel.ToggleBluetoothControlAsync();
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

    private void SetWindowsCursorHidden(bool hidden)
    {
        if (_windowsCursorHidden == hidden) return;
        _windowsCursorHidden = hidden;
        if (hidden)
        {
            while (ShowCursor(false) >= 0) { }
        }
        else
        {
            while (ShowCursor(true) < 0) { }
        }
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
    private const uint SwpNoZOrder = 0x0004;
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

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
