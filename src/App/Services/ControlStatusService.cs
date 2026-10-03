using IPhoneMirror.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace IPhoneMirror.App.Services;

internal enum ControlStatusMode { Bluetooth, Wireless, Usb }

internal enum ControlStage
{
    CheckingDevice,
    CheckingBinding,
    CheckingBluetooth,
    SwitchingBluetoothPeripheral,
    WaitingForPhoneConnection,
    VerifyingTouch,
    CheckingPermissions,
    PreparingDeviceSupport,
    Connecting,
    InitializingServices,
    StartingInputRouter,
    Ready,
    Recovering,
    Stopping,
    Failed,
    Cancelled,
}

internal static class ControlStageWorkflow
{
    private static readonly IReadOnlyList<ControlStage> UsbStages =
    [
        ControlStage.CheckingBinding,
        ControlStage.CheckingPermissions,
        ControlStage.PreparingDeviceSupport,
        ControlStage.Connecting,
        ControlStage.InitializingServices,
        ControlStage.StartingInputRouter,
    ];

    private static readonly IReadOnlyList<ControlStage> WirelessStages =
    [
        ControlStage.CheckingBinding,
        ControlStage.CheckingPermissions,
        ControlStage.Connecting,
        ControlStage.InitializingServices,
        ControlStage.StartingInputRouter,
    ];

    private static readonly IReadOnlyList<ControlStage> BluetoothStages =
    [
        ControlStage.CheckingBinding,
        ControlStage.CheckingBluetooth,
        ControlStage.SwitchingBluetoothPeripheral,
        ControlStage.WaitingForPhoneConnection,
        ControlStage.VerifyingTouch,
    ];

    internal static IReadOnlyList<ControlStage> GetStages(ControlStatusMode mode) =>
        mode switch
        {
            ControlStatusMode.Usb => UsbStages,
            ControlStatusMode.Wireless => WirelessStages,
            ControlStatusMode.Bluetooth => BluetoothStages,
            _ => [],
        };

    internal static bool Contains(ControlStatusMode mode, ControlStage stage) =>
        GetStages(mode).Contains(stage);

    internal static int IndexOf(ControlStatusMode mode, ControlStage stage)
    {
        var stages = GetStages(mode);
        for (var index = 0; index < stages.Count; index++)
            if (stages[index] == stage)
                return index;
        return -1;
    }
}

internal enum ControlStageProgress { Pending, Active, Completed, Failed, Skipped }

internal enum ControlPromptType
{
    Confirmation,
    Warning,
    Error,
    Information,
    UserActionRequired,
    Selection,
}

internal enum ControlPromptAction
{
    Primary,
    Secondary,
    Cancel,
}

internal sealed record ControlPromptOption(
    string Id,
    string Title,
    string? Detail = null,
    bool IsEnabled = true) : System.ComponentModel.INotifyPropertyChanged
{
    public string DisplayTitle => LocalizationService.RefreshText(Title);
    public string DisplayDetail => LocalizationService.RefreshText(Detail ?? string.Empty);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(null));
}

internal sealed record ControlPrompt(
    ControlPromptType Type,
    string Title,
    string Message,
    string? PrimaryButtonText = null,
    string? SecondaryButtonText = null,
    bool IsBlocking = true,
    string? TechnicalDetails = null,
    IReadOnlyList<ControlPromptOption>? Options = null);

internal sealed record ControlPromptResult(
    ControlPromptAction Action,
    string? Value = null);

internal sealed record ControlDiagnosticEntry(DateTimeOffset Timestamp, string Message,
    string TechnicalMessage, string Level = "Info");

internal sealed record ControlStatusSnapshot(
    ControlStatusMode Mode, ControlStage Stage, string DeviceName,
    string Description, bool CanCancel, bool IsTerminal, string? Error,
    TimeSpan? Duration, int RetryAttempt = 0, int RetryLimit = 0,
    ControlPrompt? Prompt = null);

internal sealed class ControlStatusService
{
    private readonly object _gate = new();
    private readonly Dictionary<ControlStage, Stopwatch> _timers = [];
    private readonly Dictionary<ControlStage, TimeSpan> _durations = [];
    private readonly Dictionary<ControlStage, ControlStageProgress> _progress = [];
    private readonly ObservableCollection<ControlDiagnosticEntry> _diagnostics = [];
    private ControlStatusSnapshot? _current;
    private TaskCompletionSource<ControlPromptResult>? _promptCompletion;

    internal event EventHandler<ControlStatusSnapshot>? StatusChanged;
    internal IReadOnlyList<ControlDiagnosticEntry> Diagnostics
    {
        get { lock (_gate) return _diagnostics.ToArray(); }
    }
    internal ControlStatusSnapshot? Current { get { lock (_gate) return _current; } }

    internal Task<ControlPromptResult> RequestPromptAsync(
        ControlPrompt prompt, CancellationToken cancellationToken = default)
    {
        ControlStatusSnapshot snapshot;
        TaskCompletionSource<ControlPromptResult> completion;
        lock (_gate)
        {
            _promptCompletion?.TrySetResult(new(ControlPromptAction.Cancel));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _promptCompletion = completion;
            var current = _current;
            if (current is null)
            {
                _promptCompletion = null;
                completion.TrySetException(new InvalidOperationException(
                    "A reverse-control status must be started before showing a prompt."));
                return completion.Task;
            }
            snapshot = current with { Prompt = prompt, IsTerminal = false, CanCancel = prompt.IsBlocking };
            _current = snapshot;
            AddDiagnosticUnsafe(prompt.Title, prompt.TechnicalDetails ?? prompt.Message,
                prompt.Type is ControlPromptType.Error ? "Error" : "Info");
        }
        DiagnosticLogger.ReverseControl(snapshot.Mode.ToString().ToLowerInvariant(),
            "prompt_requested", ("type", prompt.Type), ("title", prompt.Title));
        StatusChanged?.Invoke(this, snapshot);
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => ResolvePrompt(new(ControlPromptAction.Cancel)));
        return completion.Task;
    }

    internal bool ResolvePrompt(ControlPromptResult result)
    {
        TaskCompletionSource<ControlPromptResult>? completion;
        ControlStatusSnapshot? snapshot;
        lock (_gate)
        {
            completion = _promptCompletion;
            _promptCompletion = null;
            if (completion is null) return false;
            var current = _current;
            snapshot = current is null
                ? null
                : current with
                {
                    Prompt = null,
                    CanCancel = current.Stage is not (ControlStage.Ready or ControlStage.Failed)
                };
            if (snapshot is not null) _current = snapshot;
        }
        completion.TrySetResult(result);
        if (snapshot is not null) StatusChanged?.Invoke(this, snapshot);
        return true;
    }

    internal void Begin(ControlStatusMode mode, string deviceName)
    {
        lock (_gate)
        {
            _promptCompletion?.TrySetResult(new(ControlPromptAction.Cancel));
            _promptCompletion = null;
            _diagnostics.Clear(); _timers.Clear(); _durations.Clear(); _progress.Clear(); _current = null;
        }
        var initialStage = ControlStage.CheckingBinding;
        var description = mode == ControlStatusMode.Bluetooth
            ? LocalizationService.Get("ControlCheckingBluetoothBinding")
            : LocalizationService.Get("ControlCheckingDeviceBinding");
        Report(mode, initialStage, deviceName, description, true);
    }

    internal void Report(ControlStatusMode mode, ControlStage stage, string deviceName,
        string description, bool canCancel = true, string? technical = null,
        int retryAttempt = 0, int retryLimit = 0)
    {
        ControlStatusSnapshot snapshot;
        ControlStage? completed = null;
        TimeSpan completedDuration = default;
        bool started;
        lock (_gate)
        {
            // Bridge startup events can arrive asynchronously after the
            // control channel is already ready. They describe the old
            // startup phase and must not move the status window backwards.
            // Recovery is intentionally outside the workflow list so a real
            // disconnect can still transition a ready session into recovery.
            if (_current is { IsTerminal: true } terminal &&
                terminal.Mode == mode &&
                ControlStageWorkflow.Contains(mode, stage))
                return;

            var previousStage = _current?.Stage;
            if (previousStage is { } oldStage && oldStage != stage &&
                _timers.TryGetValue(oldStage, out var previous) &&
                _progress.GetValueOrDefault(oldStage) == ControlStageProgress.Active)
            {
                previous.Stop();
                completedDuration = previous.Elapsed;
                _durations[oldStage] = completedDuration;
                _progress[oldStage] = ControlStageProgress.Completed;
                completed = oldStage;
            }
            started = !_timers.ContainsKey(stage);
            if (started) _timers[stage] = Stopwatch.StartNew();
            _progress[stage] = ControlStageProgress.Active;
            var previousIndex = previousStage is { } prior
                ? ControlStageWorkflow.IndexOf(mode, prior)
                : -1;
            var currentIndex = ControlStageWorkflow.IndexOf(mode, stage);
            var workflow = ControlStageWorkflow.GetStages(mode);
            if (previousIndex >= 0 && currentIndex > previousIndex)
                for (var index = previousIndex + 1; index < currentIndex; index++)
                    _progress.TryAdd(workflow[index], ControlStageProgress.Skipped);
            AddDiagnosticUnsafe(description, technical ?? stage.ToString());
            snapshot = new(mode, stage, deviceName, description, canCancel,
                stage is ControlStage.Ready or ControlStage.Failed, null, null,
                retryAttempt, retryLimit);
            _current = snapshot;
        }
        if (completed is { } finished)
            DiagnosticLogger.ReverseControl(mode.ToString().ToLowerInvariant(),
                "stage_complete", ("stage", finished),
                ("duration_ms", (long)completedDuration.TotalMilliseconds));
        DiagnosticLogger.ReverseControl(mode.ToString().ToLowerInvariant(),
            started ? "stage_start" : "stage_detail", ("stage", stage),
            ("description", description), ("technical", technical));
        StatusChanged?.Invoke(this, snapshot);
    }

    internal ControlStageProgress GetProgress(ControlStage stage)
    {
        lock (_gate) return _progress.GetValueOrDefault(stage);
    }

    internal TimeSpan? GetDuration(ControlStage stage)
    {
        lock (_gate)
        {
            if (_durations.TryGetValue(stage, out var duration)) return duration;
            return _timers.TryGetValue(stage, out var timer) ? timer.Elapsed : null;
        }
    }

    internal void Ready(ControlStatusMode mode, string deviceName, string? description = null)
    {
        description ??= LocalizationService.Get("ControlReadyDescription");
        TimeSpan duration;
        ControlStage? completed = null;
        TimeSpan completedDuration = default;
        lock (_gate)
        {
            _promptCompletion?.TrySetResult(new(ControlPromptAction.Cancel));
            _promptCompletion = null;
            if (_current is { } current &&
                ControlStageWorkflow.Contains(current.Mode, current.Stage) &&
                _timers.TryGetValue(current.Stage, out var active))
            {
                active.Stop();
                completed = current.Stage;
                completedDuration = active.Elapsed;
                _durations[current.Stage] = completedDuration;
                _progress[current.Stage] = ControlStageProgress.Completed;
            }
            foreach (var timer in _timers.Values) timer.Stop();
            duration = _timers.Values.Aggregate(TimeSpan.Zero, (sum, timer) => sum + timer.Elapsed);
        }
        var snapshot = new ControlStatusSnapshot(mode, ControlStage.Ready, deviceName,
            description, false, true, null, duration);
        lock (_gate) { _current = snapshot; AddDiagnosticUnsafe(description, "Ready"); }
        if (completed is { } finished)
            DiagnosticLogger.ReverseControl(mode.ToString().ToLowerInvariant(),
                "stage_complete", ("stage", finished),
                ("duration_ms", (long)completedDuration.TotalMilliseconds));
        DiagnosticLogger.ReverseControl(mode.ToString().ToLowerInvariant(),
            "stage_ready", ("duration_ms", (long)duration.TotalMilliseconds),
            ("description", description));
        if (mode == ControlStatusMode.Usb)
            DiagnosticLogger.ReverseControl("usb", "wired_control_connected");
        StatusChanged?.Invoke(this, snapshot);
    }

    internal void Failed(ControlStatusMode mode, string deviceName, string error,
        string? technical = null)
    {
        ControlStage? failedStage;
        TimeSpan failedDuration = default;
        lock (_gate)
        {
            _promptCompletion?.TrySetResult(new(ControlPromptAction.Cancel));
            _promptCompletion = null;
            failedStage = _current?.Stage;
            if (failedStage is { } stage &&
                _timers.TryGetValue(stage, out var timer) && timer.IsRunning)
            {
                timer.Stop();
                failedDuration = timer.Elapsed;
                _durations[stage] = failedDuration;
                _progress[stage] = ControlStageProgress.Failed;
            }
            AddDiagnosticUnsafe(error, technical ?? error, "Error");
        }
        var snapshot = new ControlStatusSnapshot(mode, ControlStage.Failed, deviceName,
            LocalizationService.Get("ControlCheckConnectionAdvice"), false, true, error, null);
        lock (_gate) _current = snapshot;
        DiagnosticLogger.ReverseControlError(mode.ToString().ToLowerInvariant(),
            "stage_failed", ("stage", failedStage),
            ("duration_ms", (long)failedDuration.TotalMilliseconds),
            ("error", error), ("technical", technical));
        StatusChanged?.Invoke(this, snapshot);
    }

    internal void Cancelled(ControlStatusMode mode, string deviceName,
        string? description = null, string? technical = null)
    {
        description ??= LocalizationService.Get("ControlStageCancelled");
        ControlStage? cancelledStage;
        lock (_gate)
        {
            _promptCompletion?.TrySetResult(new(ControlPromptAction.Cancel));
            _promptCompletion = null;
            cancelledStage = _current?.Stage;
            if (cancelledStage is { } stage && _timers.TryGetValue(stage, out var timer))
            {
                timer.Stop();
                _durations[stage] = timer.Elapsed;
                _progress[stage] = ControlStageProgress.Completed;
            }
            AddDiagnosticUnsafe(description, technical ?? description, "Info");
            _current = new(mode, ControlStage.Cancelled, deviceName, description,
                false, true, null, null);
        }
        DiagnosticLogger.ReverseControl(mode.ToString().ToLowerInvariant(),
            "cancelled", ("stage", cancelledStage), ("description", description),
            ("technical", technical));
        StatusChanged?.Invoke(this, Current!);
    }

    internal void FailCurrent(string message, string? technical = null)
    {
        ControlStatusSnapshot? current;
        lock (_gate) current = _current;
        if (current is null) return;
        Failed(current.Mode, current.DeviceName, message, technical);
    }

    internal void AddDiagnostic(string message, string technical, string level = "Info")
    {
        lock (_gate) AddDiagnosticUnsafe(message, technical, level);
        if (Current is { } current)
        {
            DiagnosticLogger.ReverseControl(current.Mode.ToString().ToLowerInvariant(),
                "stage_diagnostic", ("stage", current.Stage),
                ("level", level), ("message", message),
                ("technical", technical));
            StatusChanged?.Invoke(this, current);
        }
    }

    private void AddDiagnosticUnsafe(string message, string technical, string level = "Info")
    {
        while (_diagnostics.Count >= 120) _diagnostics.RemoveAt(0);
        _diagnostics.Add(new(DateTimeOffset.Now, message, technical, level));
    }
}
