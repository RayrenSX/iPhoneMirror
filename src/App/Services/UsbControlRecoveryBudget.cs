using System.Diagnostics;

namespace IPhoneMirror.App.Services;

// A new bridge can briefly report ready and then fail again. Do not turn that
// into unlimited complete restarts; rearm only after a stable connection.
internal sealed class UsbControlRecoveryBudget
{
    internal static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);
    private long? _readyAt;
    private bool _used;

    internal void Ready(long now) => _readyAt ??= now;

    internal void Disconnected(long now)
    {
        if (_readyAt is { } ready && Stopwatch.GetElapsedTime(ready, now) >= StableConnection)
            _used = false;
        _readyAt = null;
    }

    internal bool TryTakeFinalRetry()
    {
        if (_used) return false;
        _used = true;
        return true;
    }

    internal void Reset() { _readyAt = null; _used = false; }
}
