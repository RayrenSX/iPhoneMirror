using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed class DriverOperationClient
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    internal async Task<DriverOperationResult> RunAsync(DriverOperationKind kind,
        AppleDeviceRecord device)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var deviceFingerprint = DriverLogger.DeviceFingerprint(device.Serial);
        if (!DriverConstants.IsAllowedAppleParent(device.InstanceId) ||
            !string.Equals(DriverConstants.NormalizeSerial(device.Serial), device.Serial,
                StringComparison.OrdinalIgnoreCase))
        {
            DriverLogger.WriteWarning("driver-operation", "target_rejected",
                ("operation", operationId), ("kind", kind),
                ("device", deviceFingerprint), ("reason", "target_validation"));
            return Failure(DriverLocalization.Get("InvalidDeviceTarget"), null);
        }

        DriverLogger.WriteEvent("driver-operation", "requested",
            ("operation", operationId), ("kind", kind), ("device", deviceFingerprint),
            ("present", device.IsPresent), ("service", device.Service),
            ("capture_filter", device.HasLibUsb0Filter));
        var paths = DriverConstants.GetOperationPaths(operationId);
        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            DriverLogger.WriteError("driver-operation", "executable_missing",
                ("operation", operationId), ("kind", kind), ("device", deviceFingerprint));
            return Failure(DriverLocalization.Get("DriverExecutableMissing"), paths.LogPath);
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(DriverConstants.ElevatedSwitch);
        start.ArgumentList.Add(kind.ToString());
        start.ArgumentList.Add(device.InstanceId);
        start.ArgumentList.Add(device.Serial);
        start.ArgumentList.Add(operationId);

        try
        {
            DriverLogger.WriteEvent("driver-operation", "elevated_process_start",
                ("operation", operationId), ("kind", kind),
                ("process", Path.GetFileName(executable)),
                ("timeout_ms", OperationTimeout.TotalMilliseconds));
            using var process = Process.Start(start);
            if (process is null)
            {
                DriverLogger.WriteError("driver-operation", "elevated_process_start_failed",
                    ("operation", operationId), ("kind", kind));
                return Failure(DriverLocalization.Get("ElevatedProcessStartFailed"), paths.LogPath);
            }
            using var cancellation = new CancellationTokenSource(OperationTimeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var terminationRequested = false;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        terminationRequested = true;
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception terminationError)
                {
                    DriverLogger.WriteException("driver-operation", "timeout_termination_failed",
                        terminationError, ("operation", operationId), ("kind", kind));
                }
                DriverLogger.WriteError("driver-operation", "elevated_process_timeout",
                    ("operation", operationId), ("kind", kind),
                    ("elapsed_ms", timer.ElapsedMilliseconds),
                    ("termination_requested", terminationRequested),
                    ("terminated", process.HasExited));
                return Failure(DriverLocalization.Get("ElevatedProcessTimeout"), paths.LogPath);
            }

            DriverLogger.WriteEvent("driver-operation", "elevated_process_exit",
                ("operation", operationId), ("kind", kind), ("exit_code", process.ExitCode),
                ("elapsed_ms", timer.ElapsedMilliseconds));

            for (var attempt = 0; attempt < 10 && !File.Exists(paths.ResultPath); attempt++)
                await Task.Delay(100);
            if (!File.Exists(paths.ResultPath))
            {
                DriverLogger.WriteError("driver-operation", "result_missing",
                    ("operation", operationId), ("kind", kind), ("exit_code", process.ExitCode),
                    ("result", DriverLogger.DescribePath(paths.ResultPath)));
                return Failure(DriverLocalization.Format("ElevatedProcessNoResult", process.ExitCode),
                    paths.LogPath);
            }

            await using var stream = new FileStream(paths.ResultPath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var result = await JsonSerializer.DeserializeAsync<DriverOperationResult>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var completed = result ?? Failure(DriverLocalization.Get("ElevatedInvalidResult"), paths.LogPath);
            if (!IsResultConsistentWithExitCode(process.ExitCode, completed))
            {
                DriverLogger.WriteError("driver-operation", "result_exit_code_mismatch",
                    ("operation", operationId), ("kind", kind),
                    ("exit_code", process.ExitCode), ("result_success", completed.Success));
                return Failure(
                    $"The elevated driver operation result did not match process exit code {process.ExitCode}.",
                    paths.LogPath);
            }
            DriverLogger.WriteEvent("driver-operation", "completed",
                ("operation", operationId), ("kind", kind), ("success", completed.Success),
                ("requires_replug", completed.RequiresReplug),
                ("elapsed_ms", timer.ElapsedMilliseconds),
                ("message", completed.Message),
                ("operation_log", DriverLogger.DescribePath(completed.LogPath)));
            return completed;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            DriverLogger.WriteWarning("driver-operation", "uac_cancelled",
                ("operation", operationId), ("kind", kind),
                ("elapsed_ms", timer.ElapsedMilliseconds), ("error", error.Message));
            return Failure(DriverLocalization.Get("UacCancelled"), paths.LogPath);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("driver-operation", "client_failed", error,
                ("operation", operationId), ("kind", kind),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return Failure(error.Message, paths.LogPath);
        }
    }

    private static DriverOperationResult Failure(string message, string? logPath) =>
        new(false, false, message, null, null, logPath ?? string.Empty);

    internal static bool IsResultConsistentWithExitCode(int exitCode,
        DriverOperationResult result) => (exitCode == 0) == result.Success;
}
