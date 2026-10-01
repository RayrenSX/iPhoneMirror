using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.UI.Controls;
using Wpf.Ui.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

internal sealed class ReverseControlStatusViewModel : INotifyPropertyChanged
{
    private readonly ControlStatusService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly System.Threading.Timer _deadlineTimer;
    private ControlStatusSnapshot? _snapshot;
    private int _remaining = 5;
    private DateTime _autoCloseAtUtc;
    private int _countdownElapsed;
    private int _countdownArmed;
    private bool _details;
    private bool _disposed;
    internal string WindowId { get; } = Guid.NewGuid().ToString("N");
    public ObservableCollection<ControlStageItem> Stages { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ObservableCollection<ControlPromptOption> PromptOptions { get; } = [];
    private ControlPromptOption? _selectedPromptOption;
    public string DeviceSummary => $"{_snapshot?.DeviceName ?? "iPhone"} · {_snapshot?.Mode switch { ControlStatusMode.Usb => LocalizationService.Get("WiredControlLabel"), ControlStatusMode.Wireless => LocalizationService.Get("WirelessControlLabel"), ControlStatusMode.Bluetooth => LocalizationService.Get("BluetoothControlLabel"), _ => "" }}";
    public string StageTitle => GetStageTitle(_snapshot?.Stage);
    public string StageDescription => LocalizationService.RefreshText(_snapshot?.Error ?? _snapshot?.Description ?? LocalizationService.Get("ControlPreparing"));
    public string CountdownText => _snapshot?.Stage == ControlStage.Ready ? LocalizationService.Format("ControlCloseCountdown", _remaining) : string.Empty;
    public string TotalDurationText => _snapshot?.Duration is { } duration
        ? LocalizationService.Format("ControlTotalDuration", duration.TotalSeconds) : string.Empty;
    public Visibility CancelVisibility => _snapshot is { CanCancel: true, IsTerminal: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseVisibility => _snapshot is { IsTerminal: true } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => _snapshot is { Stage: ControlStage.Failed } && RetryRequested is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PromptVisibility => _snapshot?.Prompt is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BluetoothPairingStepsVisibility =>
        _snapshot is
        {
            Mode: ControlStatusMode.Bluetooth,
            Stage: ControlStage.WaitingForPhoneConnection,
            Prompt: null,
        }
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string BluetoothPairStepOneText => LocalizationService.Get("BluetoothControlPairStepOneFormat");
    public string BluetoothPairStepTwoText => LocalizationService.Format(
        "BluetoothControlPairStepTwo", Environment.MachineName);
    public string BluetoothPairStepThreeText => LocalizationService.Get("BluetoothControlPairStepThree");
    public string BluetoothPairStepFourText => LocalizationService.Get("BluetoothControlPairStepFour");
    public string BluetoothPairStepFiveText => LocalizationService.Get("BluetoothControlPairStepFive");
    public string PromptTitle => LocalizationService.RefreshText(_snapshot?.Prompt?.Title ?? string.Empty);
    public string PromptMessage => LocalizationService.RefreshText(_snapshot?.Prompt?.Message ?? string.Empty);
    public string PromptPrimaryText => LocalizationService.RefreshText(_snapshot?.Prompt?.PrimaryButtonText ?? LocalizationService.Get("Continue"));
    public string PromptSecondaryText => LocalizationService.RefreshText(_snapshot?.Prompt?.SecondaryButtonText ?? LocalizationService.Get("Cancel"));
    public Visibility PromptPrimaryVisibility => _snapshot?.Prompt?.PrimaryButtonText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PromptSecondaryVisibility => _snapshot?.Prompt?.SecondaryButtonText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PromptOptionsVisibility => PromptOptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string PromptTechnicalDetails => LocalizationService.RefreshText(_snapshot?.Prompt?.TechnicalDetails ?? string.Empty);
    public Visibility PromptTechnicalDetailsVisibility => string.IsNullOrWhiteSpace(PromptTechnicalDetails) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsPromptPrimaryEnabled => _snapshot?.Prompt?.Options is null || SelectedPromptOption?.IsEnabled == true;
    public StatusTone PromptTone => _snapshot?.Prompt?.Type switch
    {
        ControlPromptType.Error => StatusTone.Error,
        ControlPromptType.Warning => StatusTone.Warning,
        _ => StatusTone.Info,
    };
    public SymbolRegular PromptSymbol => _snapshot?.Prompt?.Type switch
    {
        ControlPromptType.Error => SymbolRegular.ErrorCircle20,
        ControlPromptType.Warning => SymbolRegular.Warning20,
        _ => SymbolRegular.Info20,
    };
    public ControlPromptOption? SelectedPromptOption
    {
        get => _selectedPromptOption;
        set { if (ReferenceEquals(_selectedPromptOption, value)) return; _selectedPromptOption = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPromptPrimaryEnabled)); }
    }
    public bool IsDetailsExpanded { get => _details; set { _details = value; OnPropertyChanged(); } }
    internal Action? CancelRequested { get; set; }
    internal Action? RetryRequested { get; set; }
    internal event Action? CountdownElapsed;
    internal ReverseControlStatusViewModel(ControlStatusService service)
    {
        // Tick more frequently than the displayed whole seconds. The deadline
        // is absolute, so a delayed UI tick cannot freeze the label at 5 or
        // extend the window lifetime.
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Keep the visible countdown responsive while the preview is receiving
        // a continuous stream of mouse input at the normal dispatcher priority.
        _timer = new DispatcherTimer(DispatcherPriority.Send)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _deadlineTimer = new System.Threading.Timer(_ => OnCountdownDeadline(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        service.StatusChanged += OnStatusChanged; Apply(service.Current);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(new Action(() => OnLanguageChanged(sender, e)));
            return;
        }
        if (_disposed) return;
        // Refresh labels in place: applying the snapshot would reset the
        // selected prompt option and could interfere with an active countdown.
        foreach (var stage in Stages) stage.NotifyLanguageChanged();
        foreach (var option in PromptOptions) option.NotifyLanguageChanged();
        NotifyAll();
        foreach (var property in new[] { nameof(BluetoothPairStepOneText),
            nameof(BluetoothPairStepTwoText), nameof(BluetoothPairStepThreeText),
            nameof(BluetoothPairStepFourText), nameof(BluetoothPairStepFiveText) })
            OnPropertyChanged(property);
    }
    private void OnStatusChanged(object? sender, ControlStatusSnapshot snapshot)
    {
        // Apply owns both timers on the window's dispatcher. A burst of
        // Connecting -> InitializingServices -> Ready notifications can arrive
        // before any queued Apply runs. Arming here would let the older Apply
        // stop that deadline while leaving it marked as already armed.
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() => Apply(snapshot));
    }

    private void ArmCountdown()
    {
        if (Interlocked.Exchange(ref _countdownArmed, 1) != 0) return;
        _autoCloseAtUtc = DateTime.UtcNow.AddSeconds(5);
        Interlocked.Exchange(ref _countdownElapsed, 0);
        _deadlineTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        DiagnosticLogger.ReverseControl("status_window", "wired_control_auto_close_countdown_started",
            ("window_id", WindowId), ("control_mode", _snapshot?.Mode));
    }
    private void Apply(ControlStatusSnapshot? snapshot)
    {
        if (_disposed || snapshot is null) return;
        var wasReady = _snapshot?.Stage == ControlStage.Ready;
        var hadPrompt = _snapshot?.Prompt is not null;
        _snapshot = snapshot;
        Stages.Clear();
        foreach (var stage in GetWorkflowStages(snapshot.Mode))
            Stages.Add(new(stage, _service));
        Diagnostics.Clear(); foreach (var item in _service.Diagnostics)
            Diagnostics.Add($"[{item.Timestamp:HH:mm:ss.fff}] [{item.Level}] {item.Message} | {item.TechnicalMessage}");
        PromptOptions.Clear();
        if (snapshot.Prompt?.Options is { } options)
            foreach (var option in options) PromptOptions.Add(option);
        SelectedPromptOption = PromptOptions.FirstOrDefault();
        if (snapshot.Prompt is not null || snapshot.Stage != ControlStage.Ready)
        {
            var wasArmed = Interlocked.Exchange(ref _countdownArmed, 0) != 0;
            _timer.Stop();
            _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (wasArmed)
                DiagnosticLogger.ReverseControl("status_window", "wired_control_auto_close_countdown_cancelled",
                    ("window_id", WindowId), ("stage", snapshot.Stage),
                    ("reason", snapshot.Prompt is not null ? "prompt_pending" : "not_ready"));
        }
        else if (!wasReady || hadPrompt)
        {
            ArmCountdown();
            _remaining = Math.Max(1, (int)Math.Ceiling(
                (_autoCloseAtUtc - DateTime.UtcNow).TotalSeconds));
            _timer.Stop();
            _timer.Start();
        }
        NotifyAll();
    }
    private static IReadOnlyList<ControlStage> GetWorkflowStages(ControlStatusMode mode) =>
        ControlStageWorkflow.GetStages(mode);
    internal static string GetStageTitle(ControlStage? stage) =>
        LocalizationService.Get(stage is null ? "ReverseControlTitle" : "ControlStage" + stage);
    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !CanBeginControlAfterClose)
        {
            _timer.Stop();
            return;
        }

        // DispatcherTimer ticks can be delayed by a busy UI thread. Calculate
        // from an absolute deadline so delayed ticks cannot leave "5 seconds"
        // visible indefinitely or extend the close window.
        var remaining = Math.Max(0, (int)Math.Ceiling((_autoCloseAtUtc - DateTime.UtcNow).TotalSeconds));
        if (_remaining != remaining)
        {
            _remaining = remaining;
            DiagnosticLogger.ReverseControl("status_window", "wired_control_auto_close_countdown_tick",
                ("window_id", WindowId), ("remaining_seconds", _remaining));
            OnPropertyChanged(nameof(CountdownText));
        }
        if (_remaining <= 0)
        {
            _timer.Stop();
            // Also complete here if the one-shot pool timer fired just before
            // its deadline and was ignored. Both paths share the once guard.
            OnCountdownDeadline();
            return;
        }
    }
    private void OnCountdownDeadline()
    {
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(OnCountdownDeadline));
            else
                DiagnosticLogger.ReverseControlWarning("status_window", "wired_control_auto_close_failed",
                    ("window_id", WindowId), ("reason", "dispatcher_shutdown"));
            return;
        }
        // A callback queued from an older countdown must not finish a newer
        // one early. The absolute deadline is the authority.
        if (_disposed || !CanBeginControlAfterClose ||
            _service.Current is not { Stage: ControlStage.Ready, Prompt: null } ||
            Volatile.Read(ref _countdownArmed) == 0 ||
            DateTime.UtcNow < _autoCloseAtUtc ||
            Interlocked.Exchange(ref _countdownElapsed, 1) != 0)
            return;
        _timer.Stop();
        DiagnosticLogger.ReverseControl("status_window", "wired_control_auto_close_countdown_completed",
            ("window_id", WindowId));
        CountdownElapsed?.Invoke();
    }

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Interlocked.Exchange(ref _countdownArmed, 0);
        _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _deadlineTimer.Dispose();
        _service.StatusChanged -= OnStatusChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
    }
    internal bool HasPrompt => _snapshot?.Prompt is not null;
    internal bool CanBeginControlAfterClose =>
        _snapshot?.Stage == ControlStage.Ready && _snapshot.Prompt is null;
    internal void RefreshActions()
    {
        OnPropertyChanged(nameof(CancelVisibility));
        OnPropertyChanged(nameof(CloseVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
    }
    internal void ResolvePrompt(ControlPromptAction action)
    {
        var value = action == ControlPromptAction.Primary ? SelectedPromptOption?.Id : null;
        _service.ResolvePrompt(new(action, value));
    }
    private void NotifyAll()
    {
        foreach (var p in new[] { nameof(DeviceSummary), nameof(StageTitle), nameof(StageDescription), nameof(CountdownText), nameof(TotalDurationText), nameof(CancelVisibility), nameof(CloseVisibility), nameof(RetryVisibility), nameof(PromptVisibility), nameof(BluetoothPairingStepsVisibility), nameof(PromptTitle), nameof(PromptMessage), nameof(PromptPrimaryText), nameof(PromptSecondaryText), nameof(PromptPrimaryVisibility), nameof(PromptSecondaryVisibility), nameof(PromptOptionsVisibility), nameof(PromptTechnicalDetails), nameof(PromptTechnicalDetailsVisibility), nameof(IsPromptPrimaryEnabled), nameof(PromptTone), nameof(PromptSymbol) }) OnPropertyChanged(p);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

internal sealed class ControlStageItem(ControlStage stage, ControlStatusService service) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(null));
    public string Title => ReverseControlStatusViewModel.GetStageTitle(stage);
    public SymbolRegular MarkerSymbol => service.GetProgress(stage) switch
    {
        ControlStageProgress.Completed => SymbolRegular.CheckmarkCircle20,
        ControlStageProgress.Failed => SymbolRegular.ErrorCircle20,
        ControlStageProgress.Active => SymbolRegular.Clock20,
        ControlStageProgress.Skipped => SymbolRegular.Subtract20,
        _ => SymbolRegular.Circle20,
    };
    public StatusTone MarkerTone => service.GetProgress(stage) switch
    {
        ControlStageProgress.Completed => StatusTone.Success,
        ControlStageProgress.Failed => StatusTone.Error,
        ControlStageProgress.Active => StatusTone.Info,
        _ => StatusTone.Disabled,
    };
    public string MarkerDescription => LocalizationService.Get("ControlProgress" + service.GetProgress(stage));
    public string DurationText => service.GetProgress(stage) switch
    {
        ControlStageProgress.Active => "…",
        ControlStageProgress.Completed or ControlStageProgress.Failed
            when service.GetDuration(stage) is { } value => $"{value.TotalSeconds:0.0}s",
        _ => string.Empty,
    };
}

public partial class ReverseControlStatusWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private static ReverseControlStatusWindow? _active;
    private readonly ControlStatusService _service;
    private readonly ReverseControlStatusViewModel _viewModel;
    private Action? _countdownElapsed;
    private nint _nativeHandle;
    private bool _closed;

    private ReverseControlStatusWindow(Window owner, ControlStatusService service, Action? cancel,
        Action? retry, Action? countdownElapsed)
    {
        Owner = owner;
        _service = service;
        _countdownElapsed = countdownElapsed;
        _viewModel = new(service) { CancelRequested = cancel, RetryRequested = retry };
        _viewModel.CountdownElapsed += OnCountdownElapsed;
        DataContext = _viewModel;
        InitializeComponent();
        DiagnosticLogger.ReverseControl("status_window", "wired_control_status_window_created",
            ("window_id", _viewModel.WindowId));
        SourceInitialized += (_, _) =>
            _nativeHandle = new WindowInteropHelper(this).Handle;
        Closed += (_, _) =>
        {
            _closed = true;
            DiagnosticLogger.ReverseControl("status_window", "wired_control_window_closed",
                ("window_id", _viewModel.WindowId), ("handle", _nativeHandle));
            _viewModel.CountdownElapsed -= OnCountdownElapsed;
            _viewModel.Dispose();
            if (ReferenceEquals(_active, this)) _active = null;
        };
    }
    internal static void Show(Window owner, ControlStatusService service, Action? cancel = null,
        Action? retry = null, Action? countdownElapsed = null)
    {
        var active = Application.Current.Windows.OfType<ReverseControlStatusWindow>()
            .FirstOrDefault(window => window.IsVisible && ReferenceEquals(window._service, service));
        if (active is not null)
        {
            if (cancel is not null) active._viewModel.CancelRequested = cancel;
            if (retry is not null) active._viewModel.RetryRequested = retry;
            if (countdownElapsed is not null) active._countdownElapsed = countdownElapsed;
            active._viewModel.RefreshActions();
            active.Activate();
            return;
        }
        _active = new(owner, service, cancel, retry, countdownElapsed);
        _active.Show();
        _active.Activate();
        DiagnosticLogger.ReverseControl("status_window", "wired_control_status_window_shown",
            ("window_id", _active._viewModel.WindowId), ("handle", _active._nativeHandle));
    }
    internal static void ShowDeveloperPreview(Window owner)
    {
        var service = new ControlStatusService();
        service.Report(ControlStatusMode.Usb, ControlStage.Connecting, "iPhone",
            LocalizationService.Get("ControlPreparing"), canCancel: false);
        new ReverseControlStatusWindow(owner, service, null, null, null).Show();
    }
    internal static void CloseActive()
    {
        if (_active is { } window)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                _ = window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send, new Action(() => { if (!window._closed) window.Close(); }));
                return;
            }
            window.Close();
        }
    }
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPrompt) _viewModel.ResolvePrompt(ControlPromptAction.Cancel);
        var beginControl = _viewModel.CanBeginControlAfterClose;
        Close();
        // Bluetooth control is intentionally gated until the status window is
        // gone. A manual close at the ready stage is an explicit equivalent of
        // waiting for the countdown to finish.
        if (beginControl) _countdownElapsed?.Invoke();
    }
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DiagnosticLogger.ReverseControl("status_window", "control_cancel_invoked");
        _viewModel.ResolvePrompt(ControlPromptAction.Cancel);
        _viewModel.CancelRequested?.Invoke();
        Close();
    }
    private void OnPromptPrimaryClick(object sender, RoutedEventArgs e) => _viewModel.ResolvePrompt(ControlPromptAction.Primary);
    private void OnPromptSecondaryClick(object sender, RoutedEventArgs e) => _viewModel.ResolvePrompt(ControlPromptAction.Secondary);
    private void OnRetryClick(object sender, RoutedEventArgs e) => _viewModel.RetryRequested?.Invoke();

    private void OnCountdownElapsed()
    {
        // The view model completes on this window's dispatcher. Close this
        // exact instance; native hiding does not perform WPF Closed cleanup.
        Dispatcher.VerifyAccess();
        if (_closed || !_viewModel.CanBeginControlAfterClose) return;
        DiagnosticLogger.ReverseControl("status_window", "wired_control_auto_close_requested",
            ("window_id", _viewModel.WindowId));
        try
        {
            DiagnosticLogger.ReverseControl("status_window", "wired_control_window_close_called",
                ("window_id", _viewModel.WindowId), ("handle", _nativeHandle), ("visible", IsVisible));
            Close();
            if (_closed) _countdownElapsed?.Invoke();
            else
                DiagnosticLogger.ReverseControlWarning("status_window", "wired_control_auto_close_failed",
                    ("window_id", _viewModel.WindowId), ("reason", "closing_cancelled"));
        }
        catch (Exception error)
        {
            DiagnosticLogger.ReverseControlError("status_window", "wired_control_auto_close_failed",
                ("window_id", _viewModel.WindowId), ("error", AppLog.Error(error)));
        }
    }
}
