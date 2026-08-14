using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal static class CaptureErrorGuidance
{
    private const int DeviceSessionClosedErrorCode = -2109;
    private const string NoPingMarker = "sent no PING";
    // Keep this match on the stable libusb0 diagnostic prefix. The localized
    // Win32 text after `win error:` can be ANSI/CP936 in the native log and
    // may already be replacement characters by the time it reaches WPF.
    private const string UsbConfigurationMarker =
        "[set_configuration] could not set config";
    private const string LibUsb0ConfigurationMarker =
        "initialize libusb0 QuickTime configuration";

    internal static bool IsNoPingTimeout(string? diagnostic) =>
        diagnostic?.Contains(NoPingMarker, StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsUsbConfigurationFailure(string? diagnostic) =>
        diagnostic?.Contains(UsbConfigurationMarker,
            StringComparison.OrdinalIgnoreCase) == true ||
        diagnostic?.Contains(LibUsb0ConfigurationMarker,
            StringComparison.OrdinalIgnoreCase) == true;

    internal static bool HasRecoveryGuidance(string? diagnostic) =>
        IsNoPingTimeout(diagnostic) || IsUsbConfigurationFailure(diagnostic);

    internal static string FailureKindText(CaptureFailureKind kind) =>
        LocalizationService.Get(kind switch
        {
            CaptureFailureKind.UsbConnection => "CaptureFailureUsbConnection",
            CaptureFailureKind.SessionCreation => "CaptureFailureSessionCreation",
            CaptureFailureKind.Driver => "CaptureFailureDriver",
            CaptureFailureKind.VideoStream => "CaptureFailureVideoStream",
            CaptureFailureKind.InvalidVideoDimensions => "CaptureFailureVideoDimensions",
            CaptureFailureKind.NoVideoFrames => "CaptureFailureNoFrames",
            CaptureFailureKind.SystemClosed => "CaptureFailureSystemClosed",
            CaptureFailureKind.DeviceDisconnected => "CaptureFailureDeviceDisconnected",
            CaptureFailureKind.Timeout => "CaptureFailureTimeout",
            CaptureFailureKind.ExistingSession => "CaptureFailureExistingSession",
            CaptureFailureKind.ChildProcessExited => "CaptureFailureChildProcess",
            _ => "CaptureFailureUnknown",
        });

    internal static string FailureStageText(CaptureFailureStage stage) =>
        LocalizationService.Get(stage switch
        {
            CaptureFailureStage.UsbPreflight => "CaptureStageUsbPreflight",
            CaptureFailureStage.UsbActivation => "CaptureStageUsbActivation",
            CaptureFailureStage.DeviceReenumeration => "CaptureStageDeviceReenumeration",
            CaptureFailureStage.InterfaceOpen => "CaptureStageInterfaceOpen",
            CaptureFailureStage.QuickTimeHandshake => "CaptureStageQuickTimeHandshake",
            CaptureFailureStage.VideoStream => "CaptureStageVideoStream",
            CaptureFailureStage.Decoder => "CaptureStageDecoder",
            CaptureFailureStage.SessionTeardown => "CaptureStageSessionTeardown",
            CaptureFailureStage.DeviceDiscovery => "CaptureStageDeviceDiscovery",
            _ => "CaptureStageUnknown",
        });

    internal static string UserMessage(NativeCaptureStatus status)
    {
        return UserMessage(status.FailureKind, status.FailureStage,
            status.ErrorCode, status.Message);
    }

    internal static string StatusText(NativeCaptureStatus status) => UserMessage(status);

    internal static bool IsDeviceSessionClosedWarning(NativeCaptureStatus status) =>
        status.FailureKind == CaptureFailureKind.SystemClosed &&
        status.FailureStage == CaptureFailureStage.VideoStream &&
        status.ErrorCode == DeviceSessionClosedErrorCode;

    internal static CaptureFailureKind StartFailureKind(int errorCode) =>
        errorCode switch
        {
            (int)NativeResult.TransportUnavailable => CaptureFailureKind.UsbConnection,
            (int)NativeResult.DeviceNotFound => CaptureFailureKind.UsbConnection,
            (int)NativeResult.SessionAlreadyExists => CaptureFailureKind.ExistingSession,
            (int)NativeResult.UsbConfigurationNotReady => CaptureFailureKind.UsbConnection,
            (int)NativeResult.SessionTeardownFailed => CaptureFailureKind.UsbConnection,
            (int)NativeResult.DriverSafetyBlocked => CaptureFailureKind.Driver,
            _ => CaptureFailureKind.SessionCreation,
        };

    internal static string ErrorCodeText(int errorCode) => errorCode == 0
        ? "0"
        : $"{errorCode} (0x{unchecked((uint)errorCode):X8})";

    internal static string StartFailureMessage(int errorCode, string diagnostic,
        CaptureFailureKind? failureKind = null)
    {
        return UserMessage(failureKind ?? StartFailureKind(errorCode),
            CaptureFailureStage.UsbPreflight, errorCode, diagnostic);
    }

    internal static string UserMessage(CaptureFailureKind kind,
        CaptureFailureStage stage, int errorCode, string? diagnostic)
    {
        // Keep native code, stage and diagnostic text in the structured log.
        // The prompt is deliberately limited to the next useful user action.
        _ = errorCode;
        if (IsNoPingTimeout(diagnostic))
            return LocalizationService.Get("CaptureActionRestartDevice");
        if (IsUsbConfigurationFailure(diagnostic))
            return LocalizationService.Get("CaptureActionReconnectDevice");
        if (kind == CaptureFailureKind.SystemClosed &&
            stage == CaptureFailureStage.VideoStream &&
            errorCode == DeviceSessionClosedErrorCode)
            return LocalizationService.Get("DeviceSessionClosedWarningBody");

        return kind switch
        {
            CaptureFailureKind.UsbConnection => stage == CaptureFailureStage.SessionTeardown
                ? LocalizationService.Get("CaptureActionWaitForCleanup")
                : LocalizationService.Get("CaptureActionReconnectDevice"),
            CaptureFailureKind.Driver => LocalizationService.Get("CaptureActionDriverRecovery"),
            CaptureFailureKind.VideoStream or CaptureFailureKind.InvalidVideoDimensions or
                CaptureFailureKind.NoVideoFrames => LocalizationService.Get("CaptureActionVideoRetry"),
            CaptureFailureKind.DeviceDisconnected =>
                LocalizationService.Get("CaptureActionDeviceDisconnected"),
            CaptureFailureKind.ExistingSession =>
                LocalizationService.Get("CaptureActionWaitForCleanup"),
            CaptureFailureKind.SystemClosed or CaptureFailureKind.ChildProcessExited =>
                LocalizationService.Get("CaptureActionRestartApplication"),
            CaptureFailureKind.Timeout => LocalizationService.Get("CaptureActionReconnectDevice"),
            _ => LocalizationService.Get("CaptureActionRetry"),
        };
    }

    internal static string UserMessage(string? diagnostic)
    {
        if (IsNoPingTimeout(diagnostic))
            return LocalizationService.Get("CaptureActionRestartDevice");

        if (IsUsbConfigurationFailure(diagnostic))
            return LocalizationService.Get("CaptureActionReconnectDevice");

        return LocalizationService.Get("CaptureActionRetry");
    }
}
