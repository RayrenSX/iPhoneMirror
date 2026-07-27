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

    internal void Reset()
    {
        _attempts = 0;
        _openedAt = null;
    }

    internal void MarkOpened() => _openedAt = _clock();

    internal bool TryGetNext(out int attempt, out TimeSpan delay)
    {
        if (_openedAt is { } openedAt && _clock() - openedAt >= _stablePlaybackWindow)
            _attempts = 0;
        _openedAt = null;

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
