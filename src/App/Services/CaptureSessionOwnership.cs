namespace IPhoneMirror.App.Services;

/// <summary>
/// Tracks ownership until native StopCapture completes. A status error is not
/// a release signal: the QuickTime endpoint can still require HPA0/HPD0.
/// </summary>
internal sealed class CaptureSessionOwnership
{
    internal string? OwnerUdid { get; private set; }
    internal bool HasSession => OwnerUdid is not null;

    internal bool SetOwner(string? udid)
    {
        var normalized = string.IsNullOrWhiteSpace(udid) ? null : udid;
        if (string.Equals(OwnerUdid, normalized, StringComparison.OrdinalIgnoreCase)) return false;
        OwnerUdid = normalized;
        return true;
    }

    internal bool RequiresStopBeforeSwitch(string? targetUdid) =>
        OwnerUdid is not null && !string.IsNullOrWhiteSpace(targetUdid) &&
        !string.Equals(OwnerUdid, targetUdid, StringComparison.OrdinalIgnoreCase);
}
