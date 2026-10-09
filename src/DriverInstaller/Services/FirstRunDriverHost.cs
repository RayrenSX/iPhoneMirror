using System.IO.Pipes;
using System.Text.Json;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

// Presentation-free entry point. All system changes still go through the
// existing signed-package, elevation, snapshot and rollback boundaries.
internal static class FirstRunDriverHost
{
    internal const string Switch = "--first-run-pipe";
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4 || !Guid.TryParseExact(args[1], "N", out _) ||
            args[2] is not ("inspect" or "prepare" or "scan" or "diagnose")) return 2;
        using var pipe = new NamedPipeClientStream(".", "iPhoneMirror.setup." + args[1],
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(15000);
        using var writer = new StreamWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        using var cancel = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await reader.ReadLineAsync(); cancel.Cancel(); }
            catch (IOException) { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        var outputGate = new object();
        void Report(string stage, bool success = false, string? detail = null, object? devices = null, object? environment = null)
        {
            lock (outputGate)
                try { writer.WriteLine(JsonSerializer.Serialize(new { Stage = stage, Success = success, Detail = detail, Devices = devices, Environment = environment })); }
                catch (IOException) { cancel.Cancel(); }
        }
        try
        {
            using var operationGate = new Semaphore(1, 1, @"Local\iPhoneMirror.FirstRunDriver");
            // Read-only discovery must not own the installation semaphore:
            // cancelling its process could otherwise strand the semaphore while
            // another client still has it open. Only mutations need this gate.
            var ownsGate = args[2] == "prepare";
            if (ownsGate && !operationGate.WaitOne(0)) { Report("Busy"); return 1; }
            try
            {
            var catalog = new DeviceCatalog();
            if (args[2] == "diagnose")
            {
                var inventory = await Task.Run(() => catalog.GetAppleDevices(false).Where(d => d.IsPresent)
                    .Select(d => new { d.Serial, Name = string.IsNullOrWhiteSpace(d.DeviceName) ? d.DisplayName : d.DeviceName,
                        Healthy = d.IsHealthy, FilterInstalled = d.HasLibUsb0Filter }).ToArray());
                var apple = await Task.Run(() => catalog.InspectAppleSupport());
                var usb = await Task.Run(catalog.InspectLibUsbStack);
                cancel.Token.ThrowIfCancellationRequested();
                Report("Result", true, devices: inventory, environment: new {
                    AppleReady = apple.Ready, UsbInstalled = usb.ServiceInstalled, UsbFilesMatch = usb.FilesMatch,
                    UsbRunning = usb.ServiceRunning, AppleDetail = apple.Diagnostic, UsbDetail = usb.Diagnostic });
                return 0;
            }
            if (args[2] == "scan")
            {
                var devices = await Task.Run(() => catalog.GetAppleDevices().Where(d => d.IsPresent)
                    .Select(d => new { d.Serial, Name = string.IsNullOrWhiteSpace(d.DeviceName) ? d.DisplayName : d.DeviceName }).ToArray());
                cancel.Token.ThrowIfCancellationRequested();
                Report("Result", true, devices: devices); return 0;
            }
            var serial = DriverConstants.NormalizeSerial(args[3]);
            AppleDeviceRecord? Find() => catalog.GetAppleDevices(false).FirstOrDefault(d =>
                d.IsPresent && string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
            Report("Checking");
            var device = await Task.Run(Find);
            if (device is null) { Report("Disconnected"); return 1; }
            var support = await Task.Run(() => catalog.InspectAppleSupport());
            var stack = await Task.Run(catalog.InspectLibUsbStack);
            cancel.Token.ThrowIfCancellationRequested();
            if (args[2] == "inspect")
            {
                Report("Result", support.Ready && device.IsHealthy && device.HasLibUsb0Filter &&
                    stack.FilesMatch && stack.ServiceRunning);
                return 0;
            }
            if (!support.Ready)
            {
                Report("Apple");
                // Apple MSI cannot safely be killed. Cancellation is observed
                // at its transaction boundary, before starting the USB phase.
                var apple = await new AppleSupportInstaller(catalog).InstallAsync(
                    new InlineProgress(message => Report("Apple", detail: message)));
                if (!apple.Success || apple.RequiresRestart)
                { Report(apple.RequiresRestart ? "Restart" : "AppleFailed", detail: apple.Message); return 1; }
            }
            cancel.Token.ThrowIfCancellationRequested();
            Report("ReDetection");
            device = await Task.Run(Find);
            if (device is null) { Report("Disconnected"); return 1; }
            stack = await Task.Run(catalog.InspectLibUsbStack);
            cancel.Token.ThrowIfCancellationRequested();
            if (!device.HasLibUsb0Filter || !device.IsHealthy || !stack.FilesMatch || !stack.ServiceRunning)
            {
                Report("Usb");
                var operation = new DriverOperationClient();
                operation.StatusChanged += message => Report("Usb", detail: message);
                var result = await operation.RunAsync(device.HasLibUsb0Filter ? DriverOperationKind.Repair :
                    DriverOperationKind.Install, device, cancellationToken: cancel.Token);
                if (!result.Success || result.RequiresRestart)
                { Report(result.RequiresRestart ? "Restart" : "DriverFailed", detail: result.Message); return 1; }
                if (result.RequiresReplug) { Report("Replug", detail: result.Message); return 0; }
            }
            Report("Result", true);
            return 0;
            }
            finally { if (ownsGate) operationGate.Release(); }
        }
        catch (OperationCanceledException) { Report("Cancelled"); return 1; }
        catch (Exception error) { Report("DriverFailed", detail: error.Message); return 1; }
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
