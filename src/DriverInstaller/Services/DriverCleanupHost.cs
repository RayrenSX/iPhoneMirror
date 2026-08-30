using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverCleanupHost
{
    internal const string Switch = "--run-driver-cleanup";
    private const string ParentProcessIdSwitch = "--cleanup-parent-pid";
    private const string ScriptFileName = "remove_selected_iphone_drivers.ps1";
    private const string ScriptResourceName = "DriverCleanup.Script.ps1";
    private const string ScriptHash =
        "1CC19CCE6F784729BB5AD7D025355C9A4B833C9032D1831D99D91C3B862625E6";

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count >= 1 && string.Equals(arguments[0], Switch,
            StringComparison.Ordinal) &&
        (arguments.Count == 1 || TryGetParentProcessId(arguments, out _));

    internal static void LaunchElevated()
    {
        _ = StartElevatedHost(waitForExit: false);
    }

    private static int StartElevatedHost(bool waitForExit, int? parentProcessId = null)
    {
        if (!DriverOperationClient.EnsureElevationBoundary(out var boundaryError))
            throw new InvalidOperationException(
                "The driver manager executable could not be protected before elevation.",
                boundaryError);

        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName ??
            throw new FileNotFoundException("The driver manager executable is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add(Switch);
        if (parentProcessId is > 0)
        {
            start.ArgumentList.Add(ParentProcessIdSwitch);
            start.ArgumentList.Add(parentProcessId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException(
            "The elevated driver cleanup host did not start.");
        if (!waitForExit) return 0;
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static int Run(IReadOnlyList<string> arguments)
    {
        if (!IsAdministrator())
        {
            try { return StartElevatedHost(waitForExit: true, Environment.ProcessId); }
            catch (Exception error)
            {
                DriverLogger.WriteException("cleanup", "elevation_start_failed", error);
                return 5;
            }
        }
        if (!DriverOperationClient.EnsureElevationBoundary(out var boundaryError))
        {
            DriverLogger.WriteException("cleanup", "elevation_boundary_failed",
                boundaryError ?? new InvalidOperationException(
                    "The driver manager executable could not be protected."));
            return 1;
        }
        try
        {
            var parentProcessId = TryGetParentProcessId(arguments,
                out var requestedParentProcessId) ? requestedParentProcessId : 0;
            var scriptPath = ExtractTrustedScript();
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory,
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-ExcludeProcessId");
            start.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            if (parentProcessId > 0)
            {
                start.ArgumentList.Add("-ExcludeParentProcessId");
                start.ArgumentList.Add(parentProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The driver cleanup script did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("cleanup", "trusted_cleanup_launch_failed", error);
            return 1;
        }
    }

    internal static string ExtractTrustedScript()
    {
        var directory = Path.Combine(DriverConstants.DataRoot, "Cleanup");
        DriverPayload.CreateProtectedSystemDirectory(DriverConstants.DataRoot);
        DriverPayload.CreateProtectedSystemDirectory(directory);
        var scriptPath = Path.Combine(directory, ScriptFileName);
        DriverPayload.EnsureNoReparsePoints(scriptPath);
        if (File.Exists(scriptPath) && IsTrustedScript(scriptPath)) return scriptPath;

        if (File.Exists(scriptPath)) File.Delete(scriptPath);
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            ScriptResourceName) ?? throw new InvalidOperationException(
            "The embedded driver cleanup script is missing.");
        using (var destination = new FileStream(scriptPath, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            source.CopyTo(destination);
        ValidateTrustedScriptHash(scriptPath);
        return scriptPath;
    }

    internal static bool IsTrustedScript(string path)
    {
        try
        {
            DriverPayload.EnsureNoReparsePoints(path);
            ValidateTrustedScriptHash(path);
            return true;
        }
        catch { return false; }
    }

    private static void ValidateTrustedScriptHash(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The embedded driver cleanup script is missing.", path);

        var actual = ComputeCanonicalScriptHash(path);
        if (!string.Equals(actual, ScriptHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The driver cleanup script hash is not trusted; " +
                $"expected={ScriptHash} actual={actual}.");
    }

    internal static string ComputeCanonicalScriptHash(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
        if (text.Length > 0 && text[0] == (char)0xFEFF)
            text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var canonicalBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(canonicalBytes));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static bool TryGetParentProcessId(IReadOnlyList<string> arguments,
        out int processId)
    {
        processId = 0;
        return arguments.Count == 3 &&
            string.Equals(arguments[1], ParentProcessIdSwitch, StringComparison.Ordinal) &&
            int.TryParse(arguments[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out processId) &&
            processId > 0;
    }
}
