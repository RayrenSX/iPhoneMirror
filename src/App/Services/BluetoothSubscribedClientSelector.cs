namespace IPhoneMirror.App.Services;

internal static class BluetoothSubscribedClientSelector
{
    internal static string? Select(string? targetDeviceName,
        IReadOnlyCollection<(string Id, string Name)> clients)
    {
        var distinct = clients
            .Where(client => !string.IsNullOrWhiteSpace(client.Id))
            .GroupBy(client => client.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            return distinct.Length == 1 ? distinct[0].Id : null;

        var target = targetDeviceName.Trim();
        var matches = distinct.Where(client => string.Equals(
            client.Name.Trim(), target, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }
}
