using System.Diagnostics;
using System.IO;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Updater;

internal static class UpdateInstallerLauncher
{
    internal static void Launch(DownloadedUpdate update)
    {
        if (!update.HashVerified)
            throw new InvalidDataException(
                "The update package was not verified and will not be executed.");
        DiagnosticLogger.Info("updater", "installer_launch_begin",
            ("release", update.Release.TagName), ("asset", update.Asset.Name),
            ("sha256_verified", update.HashVerified));
        var sharedRuntime = DeploymentLayout.UsesSharedRuntime();
        ValidateAssetForDeployment(update.Asset.Name, sharedRuntime);
        var isInstaller = update.Asset.Name.EndsWith(".exe",
            StringComparison.OrdinalIgnoreCase);
        if (isInstaller)
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
        var waitProcessIds = new List<int> { Environment.ProcessId };
        waitProcessIds.AddRange(FindDriverProcessIds(AppContext.BaseDirectory));
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = helperDirectory,
        };
        foreach (var argument in BuildZipArguments(helperScript, update.Path,
                     AppContext.BaseDirectory, executable, waitProcessIds))
            start.ArgumentList.Add(argument);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("The ZIP update helper could not be started.");
        DiagnosticLogger.Info("updater", "installer_launched", ("format", "zip"));
    }

    internal static void ValidateAssetForDeployment(string assetName,
        bool sharedRuntime)
    {
        var isInstaller = assetName.EndsWith(".exe",
            StringComparison.OrdinalIgnoreCase);
        var isZip = assetName.EndsWith(".zip",
            StringComparison.OrdinalIgnoreCase);
        if (!isInstaller && !isZip)
            throw new InvalidOperationException(
                "The downloaded update format is unsupported.");
        if (sharedRuntime && !isInstaller)
            throw new InvalidOperationException(
                "An installed copy must be updated with the Windows Setup package.");
        if (!sharedRuntime && !isZip)
            throw new InvalidOperationException(
                "A portable copy must be updated with the portable ZIP package.");
    }

    internal static string BuildInstallerArguments()
    {
        var logPath = Path.Combine(DiagnosticLogger.DirectoryPath,
            $"installer-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        return "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS " +
            $"/STARTAPP=1 /LOG=\"{logPath}\"";
    }

    internal static IReadOnlyList<string> BuildZipArguments(string script,
        string zipPath, string installDirectory, string restartExecutable,
        int processId) => BuildZipArguments(script, zipPath, installDirectory,
            restartExecutable, [processId]);

    internal static IReadOnlyList<string> BuildZipArguments(string script,
        string zipPath, string installDirectory, string restartExecutable,
        IEnumerable<int> processIds)
    {
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-WaitPids",
        };
        var waitIds = processIds.Where(id => id > 0).Distinct().ToArray();
        if (waitIds.Length == 0)
            throw new ArgumentException("At least one process ID is required.",
                nameof(processIds));
        arguments.Add(string.Join(';', waitIds.Select(id => id.ToString(
            System.Globalization.CultureInfo.InvariantCulture))));
        arguments.AddRange([
            "-ZipPath", zipPath,
            "-InstallDirectory", installDirectory,
            "-RestartExecutable", restartExecutable,
        ]);
        return arguments;
    }

    internal static IReadOnlyList<int> FindDriverProcessIds(string appDirectory)
    {
        var expectedDirectory = Path.GetFullPath(appDirectory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("iPhoneMirror.Driver"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null) continue;
                var fullPath = Path.GetFullPath(path);
                if (string.Equals(Path.GetDirectoryName(fullPath)?.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        expectedDirectory, StringComparison.OrdinalIgnoreCase))
                    result.Add(process.Id);
            }
            catch (Exception error) when (error is InvalidOperationException or
                                          System.ComponentModel.Win32Exception or
                                          NotSupportedException)
            {
                DiagnosticLogger.ExceptionOnce("driver-process-discovery",
                    "updater", "driver_process_probe_failed", error);
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }
}
