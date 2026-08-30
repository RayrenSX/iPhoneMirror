namespace IPhoneMirror.App.Services;

/// <summary>
/// Keeps the user-selected mirror device associated with one GATT client for
/// the current app run. A new association is only made during explicit setup,
/// never by guessing from a friendly device name.
/// </summary>
internal sealed class BluetoothClientRouteTable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _clientByDeviceUdid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clientsAtTargetStart =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _targetDeviceUdid;

    internal void BeginTarget(string targetDeviceUdid,
        IEnumerable<string> subscribedClientIds, bool clearPreviousBinding = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceUdid);
        lock (_sync)
        {
            if (clearPreviousBinding)
                _clientByDeviceUdid.Remove(targetDeviceUdid);
            var current = subscribedClientIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _clientsAtTargetStart.Clear();
            _clientsAtTargetStart.UnionWith(current);
            _targetDeviceUdid = targetDeviceUdid;
        }
    }

    internal string? Refresh(IEnumerable<string> subscribedClientIds) =>
        Refresh(subscribedClientIds.Select(id => (id, string.Empty)), null);

    internal string? Refresh(IEnumerable<(string Id, string Name)> subscribedClients,
        string? targetDeviceName, string? preferredClientId = null)
    {
        lock (_sync)
        {
            var current = subscribedClients
                .Where(client => !string.IsNullOrWhiteSpace(client.Id))
                .GroupBy(client => client.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var currentIds = current.Select(client => client.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_targetDeviceUdid))
                return null;

            var claimedByOtherDevices = _clientByDeviceUdid
                .Where(pair => !string.Equals(pair.Key, _targetDeviceUdid,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(preferredClientId))
            {
                var preferred = current.FirstOrDefault(client =>
                    string.Equals(client.Id, preferredClientId,
                        StringComparison.OrdinalIgnoreCase));
                if (preferred.Id is not null &&
                    !claimedByOtherDevices.Contains(preferred.Id))
                {
                    _clientByDeviceUdid[_targetDeviceUdid] = preferred.Id;
                    return preferred.Id;
                }
                return null;
            }

            if (_clientByDeviceUdid.TryGetValue(_targetDeviceUdid, out var boundClient) &&
                currentIds.Contains(boundClient))
                return boundClient;

            // A previously bound client disappearing is a disconnect, not an
            // invitation to hand control to another phone. Rebinding requires
            // the original client to return or a fresh explicit setup after
            // the stale association has been cleared.
            if (_clientByDeviceUdid.ContainsKey(_targetDeviceUdid))
                return null;

            var available = current.Where(client =>
                !claimedByOtherDevices.Contains(client.Id)).ToArray();
            var namedMatches = available
                .Where(client => IsMeaningfulName(targetDeviceName) &&
                    IsMeaningfulName(client.Name) && string.Equals(
                        client.Name.Trim(), targetDeviceName!.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (namedMatches.Length == 1)
            {
                _clientByDeviceUdid[_targetDeviceUdid] = namedMatches[0].Id;
                return namedMatches[0].Id;
            }

            var newlySubscribed = available
                .Where(client => !_clientsAtTargetStart.Contains(client.Id))
                .ToArray();

            // A client that was already connected before control was enabled
            // cannot be proven to belong to this mirrored device. Bind only a
            // single fresh subscription that occurred after explicit setup.
            string? selected = newlySubscribed.Length == 1 ? newlySubscribed[0].Id : null;
            if (selected is not null) _clientByDeviceUdid[_targetDeviceUdid] = selected;
            return selected;
        }
    }

    internal bool SetBinding(string targetDeviceUdid, string clientId,
        IEnumerable<string> subscribedClientIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceUdid);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        lock (_sync)
        {
            var current = subscribedClientIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!current.Contains(clientId) || _clientByDeviceUdid.Any(pair =>
                    !string.Equals(pair.Key, targetDeviceUdid,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(pair.Value, clientId,
                        StringComparison.OrdinalIgnoreCase)))
                return false;
            _clientByDeviceUdid[targetDeviceUdid] = clientId;
            return true;
        }
    }

    internal void EndTarget()
    {
        lock (_sync)
        {
            _targetDeviceUdid = null;
            _clientsAtTargetStart.Clear();
        }
    }

    internal bool RemoveBinding(string targetDeviceUdid, string clientId)
    {
        lock (_sync)
        {
            if (!_clientByDeviceUdid.TryGetValue(targetDeviceUdid, out var boundClient) ||
                !string.Equals(boundClient, clientId, StringComparison.OrdinalIgnoreCase))
                return false;
            _clientByDeviceUdid.Remove(targetDeviceUdid);
            return true;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _targetDeviceUdid = null;
            _clientsAtTargetStart.Clear();
            _clientByDeviceUdid.Clear();
        }
    }

    private static bool IsMeaningfulName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Trim() is not "iPhone" and not "iPad";
}
