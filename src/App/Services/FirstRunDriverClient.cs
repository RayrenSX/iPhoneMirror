using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed record SetupUsbDevice(string Serial, string Name, bool Healthy = true, bool FilterInstalled = true);
internal sealed record SetupDriverEnvironment(bool AppleReady, bool UsbInstalled, bool UsbFilesMatch,
    bool UsbRunning, string AppleDetail, string UsbDetail);
internal sealed record SetupDriverProgress(string Stage, bool Success, string? Detail, SetupUsbDevice[]? Devices = null,
    SetupDriverEnvironment? Environment = null);
internal static class FirstRunDriverClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static async Task<SetupDriverProgress> RunAsync(string serial, bool install,
        IProgress<SetupDriverProgress> progress, CancellationToken token, bool enumerate = false, bool diagnose = false)
    {
        // A cancelled helper may still be closing its pipe. Queue behind that
        // cleanup instead of turning an ordinary retry into repeated errors.
        await Gate.WaitAsync(token);
        var gateTransferred = false;
        try
        {
            var executable = DriverManagerLauncher.FindExecutable(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER"), Environment.CurrentDirectory)
                ?? throw new FileNotFoundException(LocalizationService.Get("DriverManagerExecutableMissing"));
            var id = Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream("iPhoneMirror.setup." + id, PipeDirection.InOut,
                1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var argument in new[] { "--first-run-pipe", id, diagnose ? "diagnose" : enumerate ? "scan" : install ? "prepare" : "inspect", serial,
                "--language", LocalizationService.EffectiveCulture.Name }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException(LocalizationService.Get("DriverManagerProcessStartFailed"));
            try
            {
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(connectionTimeout.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(install ? TimeSpan.FromMinutes(12) : TimeSpan.FromSeconds(45));
                SetupDriverProgress? result = null;
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (line.Length > 65536) throw new InvalidDataException("Oversized setup response.");
                    result = JsonSerializer.Deserialize<SetupDriverProgress>(line)
                        ?? throw new InvalidDataException("Invalid setup response.");
                    progress.Report(result);
                }
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0 && result?.Success == true)
                    throw new IOException(LocalizationService.Get("SetupDriverFailed"));
                return result ?? throw new IOException(LocalizationService.Get("SetupDriverFailed"));
            }
            catch
            {
                try
                {
                    if (pipe.IsConnected)
                    {
                        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await pipe.WriteAsync("cancel\n"u8.ToArray(), cancelTimeout.Token);
                        await pipe.FlushAsync(cancelTimeout.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                if (!install || enumerate)
                {
                    // These modes only enumerate/inspect; they never start an
                    // installer. Bound their shutdown, including cancellation
                    // before the helper connects to the pipe.
                    pipe.Dispose();
                    await StopReadOnlyHelperAsync(process);
                    throw;
                }
                // Keep the single-flight gate until the original transaction
                // exits, but allow the wizard to close immediately.
                try
                {
                    var pending = Process.GetProcessById(process.Id);
                    gateTransferred = true;
                    _ = ReleaseAfterExitAsync(pending);
                }
                catch (ArgumentException) { /* The helper already exited. */ }
                throw;
            }
        }
        finally { if (!gateTransferred) Gate.Release(); }
    }
    private static async Task StopReadOnlyHelperAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync();
        }
    }
    // Calls are single-flight on the UI thread; gate ownership is transferred
    // to the asynchronous cleanup only after cancellation.
    private static async Task ReleaseAfterExitAsync(Process process)
    {
        try { await process.WaitForExitAsync(); }
        catch (Exception error) { DiagnosticLogger.Exception("setup", "driver_wait_failed", error); }
        finally { process.Dispose(); Gate.Release(); }
    }
}
