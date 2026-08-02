using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Microsoft.Win32;

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
    private readonly MultiDevicePreviewManager _secondaryMirrors;
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
    private bool _mediaOpened;
    private bool _mediaStopped = true;
    private bool _mediaCastActive;
    private bool _mediaIsLive;
    private int _mediaRecoveryRevision;
    private readonly MediaRecoveryBackoff _mediaRecoveryBackoff = new();
    private CancellationTokenSource _mediaRecoveryCancellation = new();
    private Uri? _mediaSource;
    private NativePreviewWindow? _mediaCastPreviewWindow;
    private ProjectionSettingsWindow? _projectionSettingsWindow;
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

    private static readonly TimeSpan DeviceDragHoldDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan WorkspaceTransitionDuration = TimeSpan.FromMilliseconds(280);
    public MainWindow()
    {
        InitializeComponent();
        if (Application.Current is App app)
            ThemeComboBox.SelectedValue = app.UpdateSettings.Theme.ToString();
        _themeControlReady = true;
        _workspaceControlsReady = true;
        _viewModel = new MainViewModel();
        _secondaryMirrors = new MultiDevicePreviewManager(_viewModel);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _viewModel.DeviceVideoSizeChanged += OnDeviceVideoSizeChanged;
        _viewModel.DeviceSessionHandleChanged += OnDeviceSessionHandleChanged;
        _viewModel.MediaCastCommandReceived += OnMediaCastCommandReceived;
        _viewModel.MediaCastStopRequested += OnMediaCastStopRequested;
        _viewModel.MediaCastAudioSettingsChanged += OnMediaCastAudioSettingsChanged;
        _viewModel.ProjectionSettingsRequested += OnProjectionSettingsRequested;
        _viewModel.MediaOutputSettingsRequested += OnMediaOutputSettingsRequested;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => _ = _viewModel.RefreshAsync();
        _mediaCastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaCastTimer.Tick += (_, _) => _viewModel.RefreshMediaCast();
        _mediaPlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _mediaPlaybackTimer.Tick += (_, _) => ReportMediaCastPlayback();
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_created",
            ("thread", Environment.CurrentManagedThreadId),
            ("dpi", PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 0)));
    }

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
        var shutdownTimer = Stopwatch.StartNew();
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_closing",
            ("media_cast", _mediaCastActive),
            ("independent_media_window", _mediaCastPreviewWindow is not null),
            ("full_screen", _isFullScreen)));
        _viewModel.AddDiagnosticLog(AppLog.Event("main_window_shutdown_begin"));
        _shutdownStarted = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _viewModel.DeviceVideoSizeChanged -= OnDeviceVideoSizeChanged;
        _viewModel.DeviceSessionHandleChanged -= OnDeviceSessionHandleChanged;
        _viewModel.MediaCastCommandReceived -= OnMediaCastCommandReceived;
        _viewModel.MediaCastStopRequested -= OnMediaCastStopRequested;
        _viewModel.MediaCastAudioSettingsChanged -= OnMediaCastAudioSettingsChanged;
        _viewModel.ProjectionSettingsRequested -= OnProjectionSettingsRequested;
        _viewModel.MediaOutputSettingsRequested -= OnMediaOutputSettingsRequested;
        _refreshTimer.Stop();
        _mediaCastTimer.Stop();
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
                await _viewModel.ShutdownAsync();
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
            Close();
        }
    }

    private void OnMediaCastCommandReceived(MediaCastRequest request)
    {
        try
        {
            _mediaCommandId = request.CommandId;
            _viewModel.AddDiagnosticLog(AppLog.Event("media_command_received",
                ("id", request.CommandId), ("type", request.Command),
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
                    _mediaShouldPlay = false;
                    if (_mediaOpened) MediaCastMediaElement.Pause();
                    _mediaPlaying = false;
                    UpdateMediaCastStatistics();
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
                    _mediaPlaying = _mediaOpened;
                    UpdateMediaCastStatistics();
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
                        clampToDuration: _mediaOpened);
                    _mediaStartPosition = target;
                    if (_mediaOpened)
                    {
                        MediaCastMediaElement.Position = TimeSpan.FromSeconds(target);
                        ReportMediaCastPlayback();
                    }
                    _viewModel.AddDiagnosticLog(AppLog.Event("media_seek_applied",
                        ("id", request.CommandId), ("target", target),
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

        ResetMediaRecoveryCancellation();
        var generation = _mediaCastEvents.BeginGeneration();
        ++_mediaRecoveryRevision;
        _mediaStartPosition = ClampMediaPosition(request.StartPosition,
            clampToDuration: false);
        _mediaPlaying = false;
        _mediaShouldPlay = true;
        _mediaOpened = false;
        _mediaStopped = false;
        _mediaCastActive = true;
        _mediaSource = source;
        _mediaIsLive = MediaSourceClassifier.IsLikelyLive(source);
        _mediaRecoveryBackoff.Reset();
        _viewModel.AddDiagnosticLog(AppLog.Event("media_play_begin",
            ("command", request.CommandId),
            ("source", AppLog.MediaSource(source)),
            ("likely_live", _mediaIsLive),
            ("generation", generation),
            ("start_position", _mediaStartPosition.ToString("F3")),
            ("volume", request.Volume.ToString("F3"))));
        _viewModel.BeginMediaCast(request.Volume);
        if (!ReplaceMediaCastMediaElement(source, generation, audioEnabled: true,
                volume: double.IsFinite(request.Volume)
                    ? Math.Clamp(request.Volume, 0, 1) : 1))
        {
            StopMediaCastPlayback("backend_bind_rejected");
            return;
        }
        MediaCastStatusPanel.Visibility = Visibility.Collapsed;
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
        CancelMediaRecovery();
        _mediaCastEvents.Invalidate();
        ++_mediaRecoveryRevision;
        if (!_mediaStopped)
        {
            _mediaStopped = true;
            _mediaPlaying = false;
            _mediaShouldPlay = false;
            _mediaOpened = false;
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
        _mediaIsLive = false;
        _mediaSource = null;
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
        _mediaIsLive = false;
        _mediaSource = null;
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
        };
        replacement.MediaOpened += (sender, _) =>
            OnMediaCastMediaOpened(sender, generation);
        replacement.MediaEnded += (sender, _) =>
            OnMediaCastMediaEnded(sender, generation);
        replacement.MediaFailed += (sender, e) =>
            OnMediaCastMediaFailed(sender, e, generation);
        if (!_mediaCastEvents.TryBind(generation, replacement))
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_backend_replace_rejected",
                ("generation", generation), ("reason", "stale_generation")));
            return false;
        }

        var previous = MediaCastMediaElement;
        MediaCastPlayerHost.Children.Clear();
        MediaCastPlayerHost.Children.Add(replacement);
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

    private bool IsCurrentMediaCastEvent(MediaElement mediaElement, long generation) =>
        _mediaCastActive && _mediaSource is not null &&
        _mediaCastEvents.IsCurrent(generation, mediaElement);

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
        _mediaOpened = true;
        _mediaRecoveryBackoff.MarkOpened();
        var hasFixedDuration = mediaElement.NaturalDuration.HasTimeSpan &&
            mediaElement.NaturalDuration.TimeSpan > TimeSpan.Zero;
        // An HLS URL is treated as live while opening so transient manifest
        // failures can recover. Once WMF reports a fixed duration, classify it
        // as VOD so an on-demand .m3u8 ends normally instead of looping.
        _mediaIsLive = !hasFixedDuration && _mediaSource is not null;
        _mediaStartPosition = ClampMediaPosition(_mediaStartPosition);
        if (_mediaStartPosition > 0)
        {
            try
            {
                mediaElement.Position = TimeSpan.FromSeconds(_mediaStartPosition);
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
        MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        if (mediaElement.NaturalVideoWidth > 0 &&
            mediaElement.NaturalVideoHeight > 0)
            _mediaCastPreviewWindow?.SetSourceDimensions(
                (uint)mediaElement.NaturalVideoWidth,
                (uint)mediaElement.NaturalVideoHeight);
        UpdateMediaCastStatistics(mediaElement);
        _viewModel.AddDiagnosticLog(AppLog.Event("media_opened",
            ("generation", generation), ("source", AppLog.MediaSource(_mediaSource)),
            ("live", _mediaIsLive),
            ("duration_seconds", (hasFixedDuration
                ? mediaElement.NaturalDuration.TimeSpan.TotalSeconds : 0).ToString("F3")),
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
        _mediaOpened = false;
        _mediaPlaying = false;
        _viewModel.AddDiagnosticLog(AppLog.Event("media_ended",
            ("generation", generation), ("live", _mediaIsLive),
            ("position", mediaElement.Position.TotalSeconds.ToString("F3")),
            ("source", AppLog.MediaSource(_mediaSource))));
        if (_mediaIsLive)
        {
            UpdateMediaCastStatistics(mediaElement);
            ReportMediaCastPlayback();
            QueueLiveMediaRecovery("stream ended at the current live edge");
            return;
        }
        _mediaShouldPlay = false;
        _mediaPlaybackTimer.Stop();
        MediaCastStatusPanel.Visibility = Visibility.Collapsed;
        _viewModel.AddUiLog(LocalizationService.Get("MediaCastPlaybackEnded"));
        UpdateMediaCastStatistics(mediaElement);
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
        _mediaOpened = false;
        _mediaPlaying = false;
        var message = SanitizeMediaError(
            e.ErrorException?.Message ?? LocalizationService.Get("UnknownError"));
        _viewModel.AddDiagnosticLog(AppLog.Event("media_failed",
            ("generation", generation), ("live", _mediaIsLive),
            ("source", AppLog.MediaSource(_mediaSource)),
            ("error", AppLog.Error(message,
                e.ErrorException?.GetType().Name))));
        if (_mediaIsLive)
        {
            _viewModel.AddUiLog(LocalizationService.Format(
                "MediaCastLiveRecoveringFormat", message));
            UpdateMediaCastStatistics(mediaElement);
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
        ReportMediaCastPlayback();
        QueueMediaCastCompletion();
    }

    private void ReportMediaCastPlayback()
    {
        if (_mediaCommandId == 0 || !_mediaCastActive) return;
        try
        {
            var duration = MediaCastMediaElement.NaturalDuration.HasTimeSpan
                ? MediaCastMediaElement.NaturalDuration.TimeSpan.TotalSeconds : 0;
            _viewModel.ReportMediaCastPlayback(_mediaCommandId, duration,
                Math.Max(0, MediaCastMediaElement.Position.TotalSeconds),
                _mediaPlaying ? 1 : 0);
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
        _mediaOpened = false;
        _mediaPlaying = false;
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

        if (_mediaCastActive && _mediaIsLive && _mediaSource is not null)
        {
            try
            {
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

    private double ClampMediaPosition(double position, bool clampToDuration = true)
    {
        if (!double.IsFinite(position) || position <= 0) return 0;
        var maximum = TimeSpan.FromDays(7).TotalSeconds;
        if (clampToDuration && MediaCastMediaElement.NaturalDuration.HasTimeSpan)
            maximum = Math.Min(maximum,
                MediaCastMediaElement.NaturalDuration.TimeSpan.TotalSeconds);
        return Math.Min(position, maximum);
    }

    private void UpdateMediaCastStatistics(MediaElement? mediaElement = null)
    {
        mediaElement ??= MediaCastMediaElement;
        _viewModel.UpdateMediaCastStatistics(
            (uint)Math.Max(0, mediaElement.NaturalVideoWidth),
            (uint)Math.Max(0, mediaElement.NaturalVideoHeight),
            !mediaElement.IsMuted && mediaElement.Volume > 0);
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
        if (!_mediaCastActive || !_mediaIsLive || _mediaSource is null) return;
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
        if (_shutdownStarted || !_mediaCastActive || !_mediaIsLive ||
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
            _mediaOpened = false;
            _mediaPlaying = false;
            if (!ReplaceMediaCastMediaElement(
                    source, generation, audioEnabled, volume)) return;
            if (_mediaShouldPlay) MediaCastMediaElement.Play();
            else MediaCastMediaElement.Pause();
            _mediaPlaybackTimer.Start();
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

    private void OnMediaCastCloseClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RequestMediaCastStop();
    }

    private void OnRefreshPreviewClick(object sender, RoutedEventArgs e) => RefreshPreview();

    private void OnVersionClick(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastVersionClickUtc).TotalSeconds > 2) _versionClickCount = 0;
        _lastVersionClickUtc = now;
        if (++_versionClickCount < 5) return;
        _versionClickCount = 0;
        _viewModel.EnableAdvancedMode();
        _viewModel.AddUiLog(LocalizationService.Get("AdvancedModeEnabled"));
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
            var result = await _secondaryMirrors.ShowAsync(device);
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
            _viewModel.AddDiagnosticLog(AppLog.Event("preview_window_open_begin",
                ("mode", device.IsWireless ? "wireless" : "wired"),
                ("device", AppLog.Device(device.Udid))));
            var result = await _secondaryMirrors.ShowAsync(device);
            if (!result.Success) throw new InvalidOperationException(result.Message);
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AdvancedSettingsVisibility) &&
            _viewModel.AdvancedSettingsVisibility == Visibility.Visible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                () => AdvancedSettingsCard.BringIntoView());
        }

        if (e.PropertyName == nameof(MainViewModel.IsCapturing) && !_viewModel.IsCapturing)
            MainPreviewHost.SetPresentationVisible(false);

        // Width is raised before height as one atomic status update. Listening
        // to the final height notification avoids resizing twice per frame-
        // format/orientation change.
        if (e.PropertyName is nameof(MainViewModel.SourceVideoHeight) or
            nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.SelectedModel) or
            nameof(MainViewModel.CurrentSessionHandle))
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedDevice))
            {
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
                nameof(MainViewModel.CurrentSessionHandle))
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
        MediaCastSurface.Visibility = mediaOnMain
            ? Visibility.Visible : Visibility.Collapsed;
        MainPreviewHost.ClearValue(VisibilityProperty);
        var visible = !mediaOnMain && !_viewModel.IsMediaCastSelected &&
            _viewModel.IsCapturing &&
            _viewModel.CurrentSessionHandle != 0;
        MainPreviewHost.SetPresentationVisible(visible);
        if (visible) MainPreviewHost.Activate();
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
            HeaderGapRow.Height = new GridLength(18);
            EnvironmentGapRow.Height = new GridLength(14);
            StatsGapRow.Height = new GridLength(14);
            PreviewPanel.BorderThickness = new Thickness(1);
            PreviewPanel.CornerRadius = new CornerRadius(16);
            HeaderPanel.Visibility = Visibility.Visible;
            EnvironmentPanel.Visibility = Visibility.Visible;
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
            HeaderGapRow.Height = new GridLength(0);
            EnvironmentGapRow.Height = new GridLength(0);
            StatsGapRow.Height = new GridLength(0);
            PreviewPanel.BorderThickness = new Thickness(0);
            PreviewPanel.CornerRadius = new CornerRadius(0);
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
        _viewModel.AddDiagnosticLog(AppLog.Event("main_fullscreen_state",
            ("enabled", _isFullScreen)));
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
        SynchronizeMainPreviewHost();
        window.Closed += (_, _) =>
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("media_preview_window_closed"));
            if (ReferenceEquals(_mediaCastPreviewWindow, window))
            {
                _mediaCastPreviewWindow = null;
                SynchronizeMainPreviewHost();
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
                ? ScreenshotService.CaptureVisualPng(MediaCastPlayerHost, path)
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
