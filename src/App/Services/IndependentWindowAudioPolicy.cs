namespace IPhoneMirror.App.Services;

internal static class IndependentWindowAudioPolicy
{
    internal static bool ShowMuteOthers(int connectedDeviceCount) => connectedDeviceCount > 1;

    internal static string[] GetOtherDeviceIds(string currentUdid, IEnumerable<string> deviceIds) =>
        deviceIds.Where(udid => !string.Equals(udid, currentUdid,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
