using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

// One mirror's transport lifetime. UI selection never changes the owner of
// an in-flight startup, recovery, cancellation, or bridge event callback.
internal sealed class DeviceControlSession(string deviceUdid)
{
    internal string DeviceUdid { get; } = deviceUdid;
    internal string? AppleUdid;
    internal UsbTouchBridgeHost? WiredBridge;
    internal UsbTouchBridgeHost? WirelessBridge;
    internal bool WiredEnabled;
    internal bool WiredConnected;
    internal bool WirelessEnabled;
    internal bool WirelessConnected;
    internal bool WirelessStartupTerminated;
    internal string? WiredTarget;
    internal string? WirelessTarget;
    internal bool Starting;
    internal bool RequestedWireless;
    internal bool Stopping;
    internal bool Failed;
    internal string Status = LocalizationService.Get("UsbControlOff");
    internal readonly SingleFlightOperation WiredOperation = new();
    internal readonly SingleFlightOperation WirelessOperation = new();
    internal readonly SingleFlightOperation StopOperation = new();
    internal readonly ReverseControlInputRouter Router = new();
    internal readonly ControlStatusService ControlStatus = new();
    internal bool Enabled => WiredEnabled || WirelessEnabled;
    internal bool InputEnabled => (WiredEnabled && WiredConnected) ||
        (WirelessEnabled && WirelessConnected);
}
