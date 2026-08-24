namespace IPhoneMirror.App.Services;

/// <summary>
/// Keeps Windows AutoPlay from claiming an Apple device while the capture
/// backend is switching the device into its temporary QuickTime interface.
/// </summary>
internal static class WindowsAutoPlayGuard
{
    internal const int QueryCancelAutoPlayMessage = 0x004B;

    internal static bool ShouldCancel(int message, bool captureActive) =>
        captureActive && message == QueryCancelAutoPlayMessage;
}
