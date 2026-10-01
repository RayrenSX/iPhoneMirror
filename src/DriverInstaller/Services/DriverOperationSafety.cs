using System.Diagnostics;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverOperationSafety
{
    internal const string MutexName = @"Global\iPhoneMirror.Driver.Operation";

    // A timeout requests a safe stop. It must never release the UI or transaction
    // lock while an elevated/native writer is still running.
    internal static async Task WaitForExitAsync(Process process, TimeSpan timeout, Action onTimeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try { onTimeout(); }
            catch (Exception error)
            {
                DriverLogger.WriteException("driver-operation", "timeout_notification_failed", error);
            }
            await process.WaitForExitAsync();
        }
    }

    internal static bool NeedsHealthCheck(DriverOperationKind kind, bool initiallyPresent) =>
        kind != DriverOperationKind.Uninstall || initiallyPresent;

    internal static bool IsReconnectComplete(AppleDeviceRecord? device,
        DriverOperationKind kind, ParentDriverChoice? expectedDriver = null) =>
        device is { IsHealthy: true } && kind switch
        {
            DriverOperationKind.Uninstall => !device.HasLibUsb0Filter,
            DriverOperationKind.ParentRepair => expectedDriver is null ||
                ParentDriverChange.BindingMatches(device, expectedDriver),
            _ => device.IsCaptureParent && device.HasLibUsb0Filter,
        };

    internal static string[] RemoveDeviceArguments(string instanceId, int windowsBuild) =>
        windowsBuild >= 22621 ? ["/remove-device", instanceId, "/force"] : ["/remove-device", instanceId];
}

internal sealed class DriverOperationCancellation : IDisposable
{
    private readonly EventWaitHandle _event;
    private DriverOperationCancellation(EventWaitHandle handle) => _event = handle;
    private static string Name(string operationId)
    {
        if (!DriverConstants.IsValidOperationId(operationId)) throw new ArgumentException("Invalid operation ID.");
        return @"Local\iPhoneMirror.Driver.Cancel." + operationId;
    }
    internal static DriverOperationCancellation Create(string operationId) =>
        new(new EventWaitHandle(false, EventResetMode.ManualReset, Name(operationId)));
    internal static DriverOperationCancellation Open(string operationId) =>
        new(EventWaitHandle.OpenExisting(Name(operationId)));
    internal void Request() => _event.Set();
    internal void Checkpoint()
    {
        if (_event.WaitOne(0)) throw new OperationCanceledException("The driver operation timed out and was cancelled at a safe boundary.");
    }
    public void Dispose() => _event.Dispose();
}
