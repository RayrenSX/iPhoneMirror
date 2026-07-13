using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

internal sealed record DriverManagerLaunchResult(
    bool Success,
    string? ExecutablePath,
    string Message);

internal sealed class DriverManagerLauncher
{
    private const string ExecutableName = "iPhoneMirror.Driver.exe";
    private const int SwRestore = 9;

    internal DriverManagerLaunchResult Launch()
    {
        string? executablePath = null;
        try
        {
            executablePath = FindExecutable(
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER"),
                Environment.CurrentDirectory);
            if (executablePath is null)
                return new(false, null, "The driver manager executable could not be found.");

            if (TryActivateExisting(executablePath))
                return new(true, executablePath, "The running driver manager was activated.");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = true,
            });
            return process is null
                ? new(false, executablePath, "Windows did not start the driver manager process.")
                : new(true, executablePath, "The driver manager was started.");
        }
        catch (Exception error)
        {
            return new(false, executablePath, error.Message);
        }
    }

    internal static string? FindExecutable(string baseDirectory,
        string? overridePath = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var basePath = Path.GetFullPath(baseDirectory);
        var workingPath = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        var parentPath = Directory.GetParent(basePath)?.FullName;
        var candidates = new List<string?>
        {
            ResolveCandidate(overridePath, workingPath),
            Path.Combine(basePath, ExecutableName),
            Path.Combine(basePath, "Tools", ExecutableName),
            parentPath is null ? null : Path.Combine(parentPath, "iPhoneMirror.Driver", ExecutableName),
            Path.Combine(workingPath, "outputs", "iPhoneMirror.Driver", ExecutableName),
            Path.Combine(workingPath, ExecutableName),
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string? ResolveCandidate(string? path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);
    }

    private static bool TryActivateExisting(string executablePath)
    {
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, executablePath,
                            StringComparison.OrdinalIgnoreCase) || process.MainWindowHandle == 0)
                        continue;
                    _ = ShowWindowAsync(process.MainWindowHandle, SwRestore);
                    _ = SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
                catch
                {
                    // A protected or exiting process is not a usable existing window.
                }
            }
        }
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
