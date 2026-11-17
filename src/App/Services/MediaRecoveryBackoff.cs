namespace IPhoneMirror.App.Services;

internal sealed class MediaRecoveryBackoff(
    Func<DateTimeOffset>? clock = null,
    int maximumAttempts = 12,
    TimeSpan? stablePlaybackWindow = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _stablePlaybackWindow =
        stablePlaybackWindow ?? TimeSpan.FromSeconds(10);
    private int _attempts;
    private DateTimeOffset? _openedAt;
    private double _openedPosition;
    private bool _madeStableProgress;

    internal void Reset()
    {
        _attempts = 0;
        _openedAt = null;
        _madeStableProgress = false;
    }

    internal void MarkOpened(double position = 0)
    {
        _openedAt = _clock();
        _openedPosition = position;
        _madeStableProgress = false;
    }

    internal void MarkProgress(double position)
    {
        if (_openedAt is { } openedAt && double.IsFinite(position) &&
            double.IsFinite(_openedPosition) &&
            _clock() - openedAt >= _stablePlaybackWindow &&
            position - _openedPosition >= _stablePlaybackWindow.TotalSeconds)
            _madeStableProgress = true;
    }

    internal bool TryGetNext(out int attempt, out TimeSpan delay)
    {
        if (_madeStableProgress)
            _attempts = 0;
        _openedAt = null;
        _madeStableProgress = false;

        if (_attempts >= maximumAttempts)
        {
            attempt = _attempts;
            delay = TimeSpan.Zero;
            return false;
        }

        attempt = ++_attempts;
        var exponent = Math.Min(attempt - 1, 5);
        delay = TimeSpan.FromMilliseconds(250 * (1 << exponent));
        return true;
    }
}
