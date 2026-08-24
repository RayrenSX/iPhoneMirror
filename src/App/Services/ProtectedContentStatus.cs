using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal readonly record struct ProtectedContentPresentation(
    bool IsProtected,
    bool AudioActive,
    uint AudioSampleRate,
    uint AudioChannels)
{
    internal string AudioDisplay
    {
        get
        {
            if (!AudioActive)
                return LocalizationService.Get("CaptureVideoProtectedAudioUnavailable");
            if (AudioSampleRate == 0 || AudioChannels == 0)
                return LocalizationService.Get("CaptureVideoProtectedAudioActive");
            return LocalizationService.Format(
                "CaptureVideoProtectedAudioActiveFormat",
                AudioSampleRate / 1000.0, AudioChannels);
        }
    }
}

internal static class ProtectedContentStatus
{
    internal const string Marker = "DRM_VIDEO_PROTECTED";
    internal const string AudioActiveMarker =
        "DRM_VIDEO_PROTECTED_AUDIO_ACTIVE";
    internal const string AudioInactiveMarker =
        "DRM_VIDEO_PROTECTED_AUDIO_INACTIVE";

    internal static ProtectedContentPresentation Parse(
        string? message, uint audioSampleRate, uint audioChannels)
    {
        if (string.Equals(message, AudioActiveMarker,
                StringComparison.Ordinal))
            return new(true, true, audioSampleRate, audioChannels);
        if (string.Equals(message, AudioInactiveMarker,
                StringComparison.Ordinal) ||
            string.Equals(message, Marker, StringComparison.Ordinal))
            return new(true, false, audioSampleRate, audioChannels);
        return default;
    }
}
