using System.Diagnostics;

namespace IPhoneMirror.App.Services;

// FFmpeg opens HLS tracks sequentially. Each completed probe and each larger
// startup prefix is real progress, but neither repeated logs nor a byte trickle
// may keep an unopened player alive indefinitely.
internal sealed class MediaCastStartupBudget
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(60);
    internal const int MaximumPrefixBytes = 512 * 1024;
    internal const int MaximumFiniteBytes = 256 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Func<TimeSpan> _elapsed;
    private readonly int _expectedInputs;
    private TimeSpan _lastProgress;
    private int _readyInputs;
    private int _receivedBytes;
    private bool _responseReady;
    private bool _expired;
    private string _stage = "input_probe";

    internal MediaCastStartupBudget(int expectedInputs = 2, Func<TimeSpan>? elapsed = null)
    {
        _expectedInputs = Math.Clamp(expectedInputs, 1, 2);
        var watch = Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => watch.Elapsed);
    }

    internal MediaCastStartupBudget(Func<TimeSpan> elapsed) : this(2, elapsed) { }

    internal TimeSpan Remaining
    {
        get { lock (_gate) return RemainingAt(_elapsed()); }
    }

    internal bool HasTimedOut => Remaining <= TimeSpan.Zero;

    internal string Stage
    {
        get { lock (_gate) return _stage; }
    }

    internal void MarkInputReady(int input)
    {
        lock (_gate)
        {
            var now = _elapsed();
            if (RemainingAt(now) <= TimeSpan.Zero || _responseReady ||
                _receivedBytes > 0 ||
                input < 0 || input >= _expectedInputs ||
                (_readyInputs & (1 << input)) != 0) return;
            _readyInputs |= 1 << input;
            _lastProgress = now;
            _stage = "input_" + input + "_ready";
        }
    }

    internal void MarkBytesReceived(int total)
    {
        lock (_gate)
        {
            var now = _elapsed();
            if (RemainingAt(now) <= TimeSpan.Zero || _responseReady ||
                total <= _receivedBytes || total > MaximumFiniteBytes) return;
            _lastProgress = now;
            _stage = _receivedBytes == 0 ? "first_bytes" : "startup_prefix";
            _receivedBytes = total;
        }
    }

    internal void MarkResponseReady()
    {
        lock (_gate)
        {
            var now = _elapsed();
            if (RemainingAt(now) <= TimeSpan.Zero || _responseReady) return;
            _responseReady = true;
            _lastProgress = now;
            _stage = "player_open";
        }
    }

    // Called only while holding _gate. Once a deadline passes, later progress
    // cannot revive a startup which the reader or the UI has already rejected.
    private TimeSpan RemainingAt(TimeSpan now)
    {
        if (_expired) return TimeSpan.Zero;
        var remaining = TimeSpan.FromTicks(Math.Min(
            (MaximumTimeout - now).Ticks,
            (IdleTimeout - (now - _lastProgress)).Ticks));
        if (remaining > TimeSpan.Zero) return remaining;
        _expired = true;
        return TimeSpan.Zero;
    }
}
