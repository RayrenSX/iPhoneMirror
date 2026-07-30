using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal static class CaptureErrorGuidance
{
    private const string NoPingMarker = "sent no PING";
    // Keep this match on the stable libusb0 diagnostic prefix. The localized
    // Win32 text after `win error:` can be ANSI/CP936 in the native log and
    // may already be replacement characters by the time it reaches WPF.
    private const string UsbConfigurationMarker =
        "[set_configuration] could not set config";

    internal static bool IsNoPingTimeout(string? diagnostic) =>
        diagnostic?.Contains(NoPingMarker, StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsUsbConfigurationFailure(string? diagnostic) =>
        diagnostic?.Contains(UsbConfigurationMarker,
            StringComparison.OrdinalIgnoreCase) == true;

    internal static bool HasRecoveryGuidance(string? diagnostic) =>
        IsNoPingTimeout(diagnostic) || IsUsbConfigurationFailure(diagnostic);

    internal static string UserMessage(string? diagnostic)
    {
        if (IsNoPingTimeout(diagnostic))
            return LocalizationService.Get("CaptureNoPingRecovery");

        if (IsUsbConfigurationFailure(diagnostic))
            return LocalizationService.Get("CaptureUsbConfigurationRecovery");

        return string.IsNullOrWhiteSpace(diagnostic)
            ? LocalizationService.Get("CaptureError")
            : diagnostic;
    }
}
