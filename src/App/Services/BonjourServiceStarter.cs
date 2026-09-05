using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

internal static class BonjourServiceStarter
{
    private static readonly SemaphoreSlim RepairGate = new(1, 1);

    internal static async Task<bool> EnsureRunningAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBonjourRunning()) return true;

        if (!IsServiceInstalled())
            return await RepairMissingServiceAsync(cancellationToken);

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var serviceControl = Path.Combine(systemDirectory, "sc.exe");
        if (!File.Exists(serviceControl)) return false;

        if (await StartAsync(serviceControl, elevated: false, cancellationToken) &&
            await WaitForResponderAsync(cancellationToken)) return true;

        if (!await StartAsync(serviceControl, elevated: true, cancellationToken)) return false;
        return await WaitForResponderAsync(cancellationToken);
    }

    private static bool IsServiceInstalled()
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\Bonjour Service", writable: false);
        return service is not null;
    }

    private static async Task<bool> RepairMissingServiceAsync(
        CancellationToken cancellationToken)
    {
        if (!await RepairGate.WaitAsync(0, cancellationToken))
            return await WaitForResponderAsync(cancellationToken);
        var driverManager = Path.Combine(AppContext.BaseDirectory,
            "iPhoneMirror.Driver.exe");
        try
        {
            if (!File.Exists(driverManager)) return false;
            var start = new ProcessStartInfo
            {
                FileName = driverManager,
                UseShellExecute = true,
                Verb = "runas",
                // Keep the elevated repair UI visible so a UAC prompt or
                // package-install failure cannot look like a hung progress bar.
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--repair-bonjour");
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(20), cancellationToken);
            if (process.ExitCode != 0) return false;
            if (await WaitForResponderAsync(cancellationToken)) return true;

            // The MSI can finish before the service registration becomes
            // visible. Retry the explicit service start once after repair.
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var serviceControl = Path.Combine(systemDirectory, "sc.exe");
            if (!File.Exists(serviceControl)) return false;
            await StartAsync(serviceControl, elevated: true, cancellationToken);
            return await WaitForResponderAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
        finally { RepairGate.Release(); }
    }

    private static async Task<bool> StartAsync(string serviceControl, bool elevated,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = serviceControl,
                UseShellExecute = elevated,
                Verb = elevated ? "runas" : string.Empty,
                CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("start");
            start.ArgumentList.Add("Bonjour Service");
            if (!elevated)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
            }
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode is 0 or 1056;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForResponderAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsBonjourRunning()) return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    private static bool IsBonjourRunning()
    {
        var processes = Process.GetProcessesByName("mDNSResponder");
        try { return processes.Length != 0 || IsServiceReportedRunning(); }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static bool IsServiceReportedRunning()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            start.ArgumentList.Add("query");
            start.ArgumentList.Add("Bonjour Service");
            using var process = Process.Start(start);
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Contains("RUNNING",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
