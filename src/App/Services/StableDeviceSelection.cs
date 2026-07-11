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
        string? activeCaptureUdid)
    {
        var visible = visibleOrder.Where(Valid).ToArray();
        return visible.FirstOrDefault(udid => Same(udid, previousSelectionUdid))
            ?? visible.FirstOrDefault(udid => Same(udid, activeCaptureUdid))
            ?? visible.FirstOrDefault();
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
