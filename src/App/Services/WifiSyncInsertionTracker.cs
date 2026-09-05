namespace IPhoneMirror.App.Services;

internal readonly record struct WiredDeviceTrustState(
    string Udid, uint DeviceId, bool PairRecordPresent, bool LockdownAccessible);

/// <summary>
/// Emits one provisioning target per continuous trusted USB insertion.
/// An untrusted device remains eligible until trust becomes available, while
/// disconnecting resets the insertion so the next cable connection is handled.
/// </summary>
internal sealed class WifiSyncInsertionTracker
{
    private readonly Dictionary<string, uint> _attemptedInsertionIds =
        new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> Observe(IEnumerable<WiredDeviceTrustState> devices)
    {
        var current = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Udid))
            .GroupBy(device => device.Udid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var present = new HashSet<string>(
            current.Select(device => device.Udid), StringComparer.OrdinalIgnoreCase);
        foreach (var disconnected in _attemptedInsertionIds.Keys
                     .Where(udid => !present.Contains(udid)).ToArray())
            _attemptedInsertionIds.Remove(disconnected);

        var targets = new List<string>();
        foreach (var device in current)
        {
            if (!device.PairRecordPresent || !device.LockdownAccessible)
                continue;
            if (_attemptedInsertionIds.TryGetValue(device.Udid,
                    out var attemptedDeviceId) && attemptedDeviceId == device.DeviceId)
                continue;
            _attemptedInsertionIds[device.Udid] = device.DeviceId;
            targets.Add(device.Udid);
        }
        return targets;
    }
}
