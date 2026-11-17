using System.Globalization;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal static class MediaCastPlaybackControls
{
    private const int MaximumSeekAttempts = 20;
    // WMF commonly exposes one HLS segment (roughly 4-10 seconds) as the
    // natural duration. Treat short fixed durations as an unreliable window,
    // while retaining normal VOD durations for ordinary seek controls.
    private const double MinimumReliableSegmentedDurationSeconds = 30;
    // WMF can expose a bogus multi-day duration while switching HLS
    // playlists. It is not a useful episode duration for the cast controller.
    private const double MaximumReliableSegmentedDurationSeconds = 12 * 60 * 60;
    private static readonly double MaximumPositionSeconds =
        TimeSpan.FromDays(7).TotalSeconds;

    internal static double ClampPosition(double position, double duration = 0)
    {
        if (!double.IsFinite(position) || position <= 0) return 0;
        var maximum = double.IsFinite(duration) && duration > 0
            ? Math.Min(duration, MaximumPositionSeconds)
            : MaximumPositionSeconds;
        return Math.Min(position, maximum);
    }

    internal static string FormatTime(double seconds)
    {
        var safe = !double.IsFinite(seconds) || seconds <= 0
            ? 0 : Math.Min(seconds, MaximumPositionSeconds);
        var time = TimeSpan.FromSeconds(safe);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture,
                $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{time.Minutes:00}:{time.Seconds:00}");
    }

    internal static bool CanSeek(bool opened, bool isLive, double duration) =>
        opened && !isLive && double.IsFinite(duration) && duration > 0;

    internal static bool UsePlayerTimeline(bool hls, bool opened, bool live,
        bool waitingForFirstFrame, bool seeking) =>
        hls && opened && !live && !waitingForFirstFrame && !seeking;

    internal static bool IsPrematureEnd(double duration, double lastPosition) =>
        double.IsFinite(duration) && duration > 0 &&
        double.IsFinite(lastPosition) && lastPosition >= 0 &&
        // Account for the 250ms progress tick plus a decoded video frame.
        // WMF can reset Position before dispatching its EOF callback.
        duration - lastPosition > Math.Min(2, Math.Max(0.35, duration * 0.02));

    internal static MediaCastPlaybackStatus GetPlaybackStatus(bool active,
        bool stopped, bool shouldPlay, bool loading,
        MediaCastPlaybackStatus terminalStatus = MediaCastPlaybackStatus.Unknown)
    {
        if (!active || stopped) return MediaCastPlaybackStatus.Stopped;
        if (terminalStatus is MediaCastPlaybackStatus.Stopped or MediaCastPlaybackStatus.Failed or
            MediaCastPlaybackStatus.Completed)
            return terminalStatus;
        // User intent remains paused even if WMF is buffering at that moment.
        if (!shouldPlay) return MediaCastPlaybackStatus.Paused;
        return loading ? MediaCastPlaybackStatus.Loading : MediaCastPlaybackStatus.Playing;
    }

    internal static bool IsReliableDuration(bool segmented, double duration) =>
        double.IsFinite(duration) && duration > 0 &&
        (!segmented || (duration >= MinimumReliableSegmentedDurationSeconds &&
            duration <= MaximumReliableSegmentedDurationSeconds));

    internal static double ReportedDuration(bool segmented, double duration) =>
        IsReliableDuration(segmented, duration) ? duration : 0;

    internal static bool ShouldRetainPendingSeek(double actualPosition,
        double targetPosition, TimeSpan elapsed)
    {
        if (!double.IsFinite(actualPosition) ||
            !double.IsFinite(targetPosition)) return false;
        if (elapsed < TimeSpan.FromMilliseconds(500)) return true;
        if (Math.Abs(actualPosition - targetPosition) <= 2) return false;
        return elapsed < TimeSpan.FromSeconds(10);
    }

    internal static bool ShouldRetryPendingSeek(double actualPosition,
        double targetPosition, TimeSpan sinceLastAttempt, int attemptCount,
        bool buffering)
    {
        if (buffering || attemptCount is < 1 or >= MaximumSeekAttempts ||
            !double.IsFinite(actualPosition) ||
            !double.IsFinite(targetPosition)) return false;
        if (Math.Abs(actualPosition - targetPosition) <= 2) return false;
        return sinceLastAttempt >= TimeSpan.FromMilliseconds(400);
    }

    internal static bool ShouldRevealVideo(bool shouldPlay, bool buffering,
        double openingPosition, double currentPosition, TimeSpan openedFor)
    {
        if (buffering) return false;
        if (!shouldPlay) return true;
        if (double.IsFinite(currentPosition) &&
            currentPosition >= openingPosition + 0.02) return true;
        return openedFor >= TimeSpan.FromSeconds(1.5);
    }
}
