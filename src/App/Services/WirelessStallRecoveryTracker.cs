using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal enum WirelessStallRecoveryAction
{
    None,
    RefreshPreview,
    RestartSession,
}

/// <summary>
/// Requests one harmless redraw when an AirPlay frame stops advancing.
/// A static phone screen can legitimately stop producing frames indefinitely;
/// neither frame age nor FPS/latency telemetry proves a failed connection.
/// Silence alone must never restart the session and clear its last frame.
/// </summary>
internal sealed class WirelessStallRecoveryTracker
{
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1800);

    private ulong _handle;
    private uint _width;
    private uint _height;
    private long _timestamp;
    private ulong _videoFrames;
    private DateTimeOffset _lastProgressAt;
    private int _recoveryAttempts;
    private bool _initialized;

    internal int RecoveryAttempts => _recoveryAttempts;

    internal WirelessStallRecoveryAction Observe(
        ulong handle, NativeCaptureStatus status, long latestFrameTimestamp,
        DateTimeOffset now)
    {
        if (handle == 0 || status.State != CaptureState.Streaming ||
            status.Width == 0 || status.Height == 0 || status.VideoFrames == 0 ||
            latestFrameTimestamp <= 0)
        {
            Reset();
            return WirelessStallRecoveryAction.None;
        }

        var dimensionsChanged = _initialized &&
            (_width != status.Width || _height != status.Height);
        var handleChanged = !_initialized || _handle != handle;
        if (handleChanged || dimensionsChanged)
        {
            _handle = handle;
            _width = status.Width;
            _height = status.Height;
            _timestamp = latestFrameTimestamp;
            _videoFrames = status.VideoFrames;
            _lastProgressAt = now;
            if (handleChanged || dimensionsChanged) _recoveryAttempts = 0;
            _initialized = true;
            return WirelessStallRecoveryAction.None;
        }

        var advanced = latestFrameTimestamp != _timestamp ||
            status.VideoFrames != _videoFrames;
        if (advanced)
        {
            _timestamp = latestFrameTimestamp;
            _videoFrames = status.VideoFrames;
            _lastProgressAt = now;
            return WirelessStallRecoveryAction.None;
        }

        if (now - _lastProgressAt < StallThreshold || _recoveryAttempts >= 1)
            return WirelessStallRecoveryAction.None;

        _recoveryAttempts++;
        return WirelessStallRecoveryAction.RefreshPreview;
    }

    internal void Reset()
    {
        _handle = 0;
        _width = _height = 0;
        _timestamp = 0;
        _videoFrames = 0;
        _lastProgressAt = default;
        _recoveryAttempts = 0;
        _initialized = false;
    }

}
