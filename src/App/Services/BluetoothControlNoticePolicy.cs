namespace IPhoneMirror.App.Services;

/// <summary>
/// Tracks the Bluetooth-control guidance shown during one application run.
/// The set deliberately lives in memory only, so every application launch
/// guides each device once without repeatedly interrupting later sessions.
/// </summary>
internal sealed class BluetoothControlNoticePolicy
{
    private readonly HashSet<string> _shownDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool ShouldShowForDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        return _shownDeviceIds.Add(deviceId.Trim());
    }
}
