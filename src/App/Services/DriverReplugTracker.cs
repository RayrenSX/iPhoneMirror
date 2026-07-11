namespace IPhoneMirror.App.Services;

/// <summary>Requires a physical absence-then-presence cycle per installed UDID.</summary>
internal sealed class DriverReplugTracker
{
    // false = installation completed but disconnect not observed;
    // true = disconnect observed, waiting for reconnect.
    private readonly Dictionary<string, bool> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool Any => _pending.Count != 0;
    internal bool IsPending(string? udid) =>
        !string.IsNullOrWhiteSpace(udid) && _pending.ContainsKey(udid);

    internal void MarkInstalled(string udid)
    {
        if (string.IsNullOrWhiteSpace(udid))
            throw new ArgumentException("A driver install requires a device UDID.", nameof(udid));
        _pending[udid] = false;
    }

    internal IReadOnlyList<string> ObservePresent(IEnumerable<string> presentUdids)
    {
        var present = presentUdids.Where(udid => !string.IsNullOrWhiteSpace(udid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = new List<string>();
        foreach (var entry in _pending.ToArray())
        {
            if (!present.Contains(entry.Key))
            {
                _pending[entry.Key] = true;
                continue;
            }
            if (!entry.Value) continue;
            _pending.Remove(entry.Key);
            completed.Add(entry.Key);
        }
        return completed;
    }
}
