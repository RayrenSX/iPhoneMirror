using System.Collections.ObjectModel;
using System.Diagnostics;

namespace IPhoneMirror.App.Services;

internal enum ControlStatusMode { Bluetooth, Wireless, Usb }

internal enum ControlStage
{
    CheckingDevice, CheckingPermissions, PreparingDeviceSupport, Connecting,
    InitializingServices, StartingInputRouter, Ready, Recovering, Stopping, Failed
}

internal sealed record ControlDiagnosticEntry(DateTimeOffset Timestamp, string Message,
    string TechnicalMessage, string Level = "Info");

internal sealed record ControlStatusSnapshot(
    ControlStatusMode Mode, ControlStage Stage, string DeviceName,
    string Description, bool CanCancel, bool IsTerminal, string? Error,
    TimeSpan? Duration, int RetryAttempt = 0, int RetryLimit = 0);

internal sealed class ControlStatusService
{
    private readonly object _gate = new();
    private readonly Dictionary<ControlStage, Stopwatch> _timers = [];
    private readonly Dictionary<ControlStage, TimeSpan> _durations = [];
    private readonly ObservableCollection<ControlDiagnosticEntry> _diagnostics = [];
    private ControlStatusSnapshot? _current;

    internal event EventHandler<ControlStatusSnapshot>? StatusChanged;
    internal IReadOnlyList<ControlDiagnosticEntry> Diagnostics
    {
        get { lock (_gate) return _diagnostics.ToArray(); }
    }
    internal ControlStatusSnapshot? Current { get { lock (_gate) return _current; } }

    internal void Begin(ControlStatusMode mode, string deviceName)
    {
        lock (_gate) { _diagnostics.Clear(); _timers.Clear(); _durations.Clear(); _current = null; }
        Report(mode, ControlStage.CheckingDevice, deviceName, "正在检查设备和绑定状态…", true);
    }

    internal void Report(ControlStatusMode mode, ControlStage stage, string deviceName,
        string description, bool canCancel = true, string? technical = null,
        int retryAttempt = 0, int retryLimit = 0)
    {
        ControlStatusSnapshot snapshot;
        lock (_gate)
        {
            if (_current is not null && _current.Stage != stage &&
                _timers.TryGetValue(_current.Stage, out var previous))
            {
                previous.Stop();
                _durations[_current.Stage] = previous.Elapsed;
            }
            if (!_timers.ContainsKey(stage)) _timers[stage] = Stopwatch.StartNew();
            AddDiagnosticUnsafe(description, technical ?? stage.ToString());
            snapshot = new(mode, stage, deviceName, description, canCancel,
                stage is ControlStage.Ready or ControlStage.Failed, null, null,
                retryAttempt, retryLimit);
            _current = snapshot;
        }
        StatusChanged?.Invoke(this, snapshot);
    }

    internal TimeSpan? GetDuration(ControlStage stage)
    {
        lock (_gate)
        {
            if (_durations.TryGetValue(stage, out var duration)) return duration;
            return _timers.TryGetValue(stage, out var timer) ? timer.Elapsed : null;
        }
    }

    internal void Ready(ControlStatusMode mode, string deviceName, string description = "反向控制已经准备就绪")
    {
        TimeSpan duration;
        lock (_gate)
        {
            foreach (var timer in _timers.Values) timer.Stop();
            duration = _timers.Values.Aggregate(TimeSpan.Zero, (sum, timer) => sum + timer.Elapsed);
        }
        var snapshot = new ControlStatusSnapshot(mode, ControlStage.Ready, deviceName,
            description, false, true, null, duration);
        lock (_gate) { _current = snapshot; AddDiagnosticUnsafe(description, "Ready"); }
        StatusChanged?.Invoke(this, snapshot);
    }

    internal void Failed(ControlStatusMode mode, string deviceName, string error,
        string? technical = null)
    {
        lock (_gate) { AddDiagnosticUnsafe(error, technical ?? error, "Error"); }
        var snapshot = new ControlStatusSnapshot(mode, ControlStage.Failed, deviceName,
            "请检查设备连接状态，然后重试。", false, true, error, null);
        lock (_gate) _current = snapshot;
        StatusChanged?.Invoke(this, snapshot);
    }

    internal void AddDiagnostic(string message, string technical, string level = "Info")
    {
        lock (_gate) AddDiagnosticUnsafe(message, technical, level);
        if (Current is { } current) StatusChanged?.Invoke(this, current);
    }

    private void AddDiagnosticUnsafe(string message, string technical, string level = "Info")
    {
        while (_diagnostics.Count >= 120) _diagnostics.RemoveAt(0);
        _diagnostics.Add(new(DateTimeOffset.Now, message, technical, level));
    }
}
