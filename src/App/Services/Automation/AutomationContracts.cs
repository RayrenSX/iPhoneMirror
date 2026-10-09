using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services.Automation;

internal sealed class AutomationException(string code, string message, int status = 409) : Exception(message)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed record AutomationCapabilities(bool Tap, bool LongPress, bool TouchDown, bool TouchUp,
    bool Swipe, bool TouchPath, bool Keyboard, bool TextInput, bool ClipboardRead, bool ClipboardWrite,
    bool Screenshot);
internal sealed record AutomationGeometry(uint Width, uint Height, string Version,
    string CoordinateSpace = "capturePixels");
internal sealed record AutomationDevice(string Id, string Name, string? Model, string? IosVersion,
    string Connection, bool Online, bool ControlAvailable, Guid? BindingProfileId,
    string? ControlTransport, bool Capturing, AutomationGeometry Geometry, AutomationCapabilities Capabilities);
internal sealed record AutomationTarget(AutomationDevice Device, string SourceId, string PhysicalId,
    Func<bool> IsCurrent, Func<(double X, double Y), (double X, double Y)> Transform,
    Func<Func<bool>, MappedTouchRoute?> TouchRoute, DirectKeyboardRoute? KeyboardRoute,
    Func<string, string?, CancellationToken, Task<string?>>? Clipboard,
    Func<CancellationToken, Task<VideoFrame?>> Capture)
{
    internal Func<bool>? HardwareBusy { get; init; }
    internal Func<bool>? SessionCurrent { get; init; }
}

// Adapts the application's existing managers. It must never discover/connect a
// second device or switch the selected UI device to satisfy an HTTP request.
internal interface IAutomationBackend
{
    Task<IReadOnlyList<AutomationDevice>> GetDevicesAsync(CancellationToken cancellation);
    Task<AutomationTarget> ResolveAsync(string id, CancellationToken cancellation);
    Task<bool> IsHumanInputBusyAsync(string physicalId, CancellationToken cancellation);
    Task ReserveAsync(string physicalId, Action reserve, CancellationToken cancellation);
}

internal sealed record AutomationPoint(double? X, double? Y);
internal sealed record AutomationInput
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? X1 { get; init; }
    public double? Y1 { get; init; }
    public double? X2 { get; init; }
    public double? Y2 { get; init; }
    public double? Duration { get; init; }
    public AutomationPoint[]? Points { get; init; }
    public string? Key { get; init; }
    public string? Text { get; init; }
    public string? TouchId { get; init; }
    public string? GeometryVersion { get; init; }
}

internal static class AutomationDeviceId
{
    internal static string Encode(string source) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
