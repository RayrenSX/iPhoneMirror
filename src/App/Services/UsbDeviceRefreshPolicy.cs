using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal static class UsbDeviceRefreshPolicy
{
    internal static bool ShouldRefreshMetadata(bool forceDeviceEnumeration,
        bool hasWiredSession) => forceDeviceEnumeration && !hasWiredSession;

    internal static bool IsUsbTransitionState(CaptureState state) => state is
        CaptureState.ActivatingUsb or CaptureState.WaitingForDevice or
        CaptureState.Handshaking or CaptureState.Stopping;

    internal static bool ShouldEnumerateWiredDevices(bool managedTransition,
        IEnumerable<CaptureState> nativeStates) =>
        !managedTransition && !nativeStates.Any(IsUsbTransitionState);
}
