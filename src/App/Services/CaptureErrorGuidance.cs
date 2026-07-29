using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal static class CaptureErrorGuidance
{
    private const string NoPingMarker = "sent no PING";

    internal static bool IsNoPingTimeout(string? diagnostic) =>
        diagnostic?.Contains(NoPingMarker, StringComparison.OrdinalIgnoreCase) == true;

    internal static string UserMessage(string? diagnostic)
    {
        if (IsNoPingTimeout(diagnostic))
            return LocalizationService.Get("CaptureNoPingRecovery");

        return string.IsNullOrWhiteSpace(diagnostic)
            ? LocalizationService.Get("CaptureError")
            : diagnostic;
    }
}
