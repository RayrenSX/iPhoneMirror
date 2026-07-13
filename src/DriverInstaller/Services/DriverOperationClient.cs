using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed class DriverOperationClient
{
    internal async Task<DriverOperationResult> RunAsync(DriverOperationKind kind,
        AppleDeviceRecord device)
    {
        DriverLogger.Write($"Starting elevated {kind} for {device.InstanceId}.");
        if (!DriverConstants.IsAllowedAppleParent(device.InstanceId) ||
            !string.Equals(DriverConstants.NormalizeSerial(device.Serial), device.Serial,
                StringComparison.OrdinalIgnoreCase))
            return Failure(DriverLocalization.Get("InvalidDeviceTarget"), null);

        var operationId = Guid.NewGuid().ToString("N");
        var paths = DriverConstants.GetOperationPaths(operationId);
        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
            return Failure(DriverLocalization.Get("DriverExecutableMissing"), paths.LogPath);

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
            using var process = Process.Start(start);
            if (process is null)
                return Failure(DriverLocalization.Get("ElevatedProcessStartFailed"), paths.LogPath);
            await process.WaitForExitAsync();

            for (var attempt = 0; attempt < 10 && !File.Exists(paths.ResultPath); attempt++)
                await Task.Delay(100);
            if (!File.Exists(paths.ResultPath))
                return Failure(DriverLocalization.Format("ElevatedProcessNoResult", process.ExitCode),
                    paths.LogPath);

            await using var stream = new FileStream(paths.ResultPath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var result = await JsonSerializer.DeserializeAsync<DriverOperationResult>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var completed = result ?? Failure(DriverLocalization.Get("ElevatedInvalidResult"), paths.LogPath);
            DriverLogger.Write($"Completed elevated {kind}: success={completed.Success}; " +
                $"message={completed.Message}; log={completed.LogPath}");
            return completed;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            DriverLogger.Write($"UAC cancelled for {kind}: {error.Message}");
            return Failure(DriverLocalization.Get("UacCancelled"), paths.LogPath);
        }
        catch (Exception error)
        {
            DriverLogger.Write($"Elevated {kind} failed: {error}");
            return Failure(error.Message, paths.LogPath);
        }
    }

    private static DriverOperationResult Failure(string message, string? logPath) =>
        new(false, false, message, null, null, logPath ?? string.Empty);
}
