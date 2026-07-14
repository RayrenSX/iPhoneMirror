namespace IPhoneMirror.App.Services;

/// <summary>
/// Pure selection/order policy used by the WPF device list. Keeping this free
/// of UI and native dependencies makes the multi-device behavior repeatable in
/// a small automated test.
/// </summary>
internal static class StableDeviceSelection
{
    internal static IReadOnlyList<string> MergeVisibleOrder(
        IEnumerable<string> existingOrder,
        IEnumerable<string> discoveredOrder)
    {
        var discovered = discoveredOrder
            .Where(Valid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visible = new HashSet<string>(discovered, StringComparer.OrdinalIgnoreCase);
        var result = existingOrder
            .Where(udid => Valid(udid) && visible.Contains(udid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var alreadyAdded = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
        foreach (var udid in discovered)
            if (alreadyAdded.Add(udid)) result.Add(udid);
        return result;
    }

    internal static string? ChooseUdid(
        IEnumerable<string> visibleOrder,
        string? previousSelectionUdid,
        string? activeCaptureUdid,
        string? newlyConnectedWirelessUdid = null)
    {
        var visible = visibleOrder.Where(Valid).ToArray();
        return visible.FirstOrDefault(udid => Same(udid, newlyConnectedWirelessUdid))
            ?? visible.FirstOrDefault(udid => Same(udid, previousSelectionUdid))
            ?? visible.FirstOrDefault(udid => Same(udid, activeCaptureUdid))
            ?? visible.FirstOrDefault();
    }

    internal static string? FindNewlyConnected(
        IEnumerable<string> previousWirelessUdids,
        IEnumerable<string> currentWirelessUdids)
    {
        var previous = new HashSet<string>(
            previousWirelessUdids.Where(Valid), StringComparer.OrdinalIgnoreCase);
        return currentWirelessUdids
            .Where(Valid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(previous.Add);
    }

    internal static int CalculateDropIndex(
        int itemCount,
        int sourceIndex,
        int? targetIndex,
        bool placeAfterTarget)
    {
        if (itemCount <= 0 || sourceIndex < 0 || sourceIndex >= itemCount) return sourceIndex;
        var insertionIndex = targetIndex is >= 0 and < int.MaxValue
            ? targetIndex.Value + (placeAfterTarget ? 1 : 0)
            : itemCount;
        insertionIndex = Math.Clamp(insertionIndex, 0, itemCount);
        if (sourceIndex < insertionIndex) --insertionIndex;
        return Math.Clamp(insertionIndex, 0, itemCount - 1);
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
