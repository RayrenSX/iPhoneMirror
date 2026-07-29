using System.Diagnostics;
using System.IO;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Updater;

internal static class UpdateInstallerLauncher
{
    internal static void Launch(DownloadedUpdate update)
    {
        DiagnosticLogger.Info("updater", "installer_launch_begin",
            ("release", update.Release.TagName), ("asset", update.Asset.Name),
            ("sha256_verified", update.HashVerified));
        if (update.Asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = update.Path,
                Arguments = BuildInstallerArguments(),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(update.Path),
            });
            if (process is null)
                throw new InvalidOperationException("The update installer could not be started.");
            DiagnosticLogger.Info("updater", "installer_launched",
                ("format", "exe"), ("pid", process.Id));
            return;
        }

        if (!update.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded update format is unsupported.");
        var sourceScript = Path.Combine(AppContext.BaseDirectory,
            "tools", "updater", "Apply-ZipUpdate.ps1");
        if (!File.Exists(sourceScript))
            throw new FileNotFoundException("The ZIP update helper is missing.", sourceScript);
        var helperDirectory = Path.Combine(Path.GetTempPath(), "iPhoneMirror",
            "Updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var helperScript = Path.Combine(helperDirectory, "Apply-ZipUpdate.ps1");
        File.Copy(sourceScript, helperScript);
        var executable = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "iPhoneMirror.exe");
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = helperDirectory,
        };
        foreach (var argument in BuildZipArguments(helperScript, update.Path,
                     AppContext.BaseDirectory, executable, Environment.ProcessId))
            start.ArgumentList.Add(argument);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("The ZIP update helper could not be started.");
        DiagnosticLogger.Info("updater", "installer_launched", ("format", "zip"));
    }

    internal static string BuildInstallerArguments()
    {
        var logPath = Path.Combine(DiagnosticLogger.DirectoryPath,
            $"installer-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        return "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS " +
            $"/RESTARTAPP=1 /LOG=\"{logPath}\"";
    }

    internal static IReadOnlyList<string> BuildZipArguments(string script,
        string zipPath, string installDirectory, string restartExecutable, int processId) =>
    [
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", script,
        "-WaitPid", processId.ToString(),
        "-ZipPath", zipPath,
        "-InstallDirectory", installDirectory,
        "-RestartExecutable", restartExecutable,
    ];
}
