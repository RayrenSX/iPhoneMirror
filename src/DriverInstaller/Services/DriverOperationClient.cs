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
            return Failure("拒绝了无效的 Apple 设备目标。", null);

        var operationId = Guid.NewGuid().ToString("N");
        var paths = DriverConstants.GetOperationPaths(operationId);
        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
            return Failure("无法确定驱动管理器的可执行文件路径。", paths.LogPath);

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
                return Failure("管理员驱动进程未能启动。", paths.LogPath);
            await process.WaitForExitAsync();

            for (var attempt = 0; attempt < 10 && !File.Exists(paths.ResultPath); attempt++)
                await Task.Delay(100);
            if (!File.Exists(paths.ResultPath))
                return Failure($"管理员驱动进程以代码 {process.ExitCode} 退出，但没有返回结果。",
                    paths.LogPath);

            await using var stream = new FileStream(paths.ResultPath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var result = await JsonSerializer.DeserializeAsync<DriverOperationResult>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var completed = result ?? Failure("管理员驱动进程返回了无效结果。", paths.LogPath);
            DriverLogger.Write($"Completed elevated {kind}: success={completed.Success}; " +
                $"message={completed.Message}; log={completed.LogPath}");
            return completed;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            DriverLogger.Write($"UAC cancelled for {kind}: {error.Message}");
            return Failure("用户取消了 Windows 管理员授权。", paths.LogPath);
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
