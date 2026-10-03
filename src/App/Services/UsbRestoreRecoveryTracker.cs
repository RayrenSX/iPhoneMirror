namespace IPhoneMirror.App.Services;

/// <summary>
/// Keeps a device out of capture startup after native teardown could not
/// confirm that iOS returned to its normal USB configuration. The block is
/// released only after the device disappears and is later observed again.
/// </summary>
internal sealed class UsbRestoreRecoveryTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _blocked =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnected =
        new(StringComparer.OrdinalIgnoreCase);

    internal void MarkRecoveryRequired(string udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return;
        lock (_gate)
        {
            _blocked.Add(udid);
            _disconnected.Remove(udid);
        }
    }

    internal bool IsBlocked(string udid)
    {
        lock (_gate) return _blocked.Contains(udid);
    }

    /// <summary>
    /// A missing observation is not enough by itself to clear the block. The
    /// next present observation must follow it, proving a real re-enumeration.
    /// </summary>
    internal IReadOnlyList<string> Observe(IEnumerable<string> presentUdids)
    {
        var present = new HashSet<string>(presentUdids.Where(
            value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        var cleared = new List<string>();
        lock (_gate)
        {
            foreach (var udid in _blocked.ToArray())
            {
                if (!present.Contains(udid))
                {
                    _disconnected.Add(udid);
                    continue;
                }
                if (!_disconnected.Remove(udid)) continue;
                _blocked.Remove(udid);
                cleared.Add(udid);
            }
        }
        return cleared;
    }
}
