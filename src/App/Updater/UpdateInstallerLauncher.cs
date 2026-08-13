using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Updater;

internal static class UpdateInstallerLauncher
{
    private const string ZipHelperResourceName =
        "IPhoneMirror.App.Updater.Apply-ZipUpdate.ps1";
    private const string VerifiedScriptBootstrap = """
        $ErrorActionPreference = 'Stop'
        $payloadJson = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$PAYLOAD_BASE64$'))
        $payload = $payloadJson | ConvertFrom-Json
        try {
            $stream = [IO.File]::Open([string]$payload.ScriptPath,
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $algorithm.ComputeHash($stream)).Replace('-', '')
                }
                finally { $algorithm.Dispose() }
                if (-not $actual.Equals([string]$payload.ExpectedSha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The elevated update helper changed after verification.'
                }
                $stream.Position = 0
                $encoding = [Text.UTF8Encoding]::new($false, $true)
                $reader = [IO.StreamReader]::new($stream, $encoding, $true, 4096, $true)
                try { $scriptText = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
                $scriptArguments = @($payload.Arguments)
                if (($scriptArguments.Count % 2) -ne 0) {
                    throw 'The elevated update helper received malformed arguments.'
                }
                $boundArguments = @{}
                for ($index = 0; $index -lt $scriptArguments.Count; $index += 2) {
                    $name = [string]$scriptArguments[$index]
                    if (-not $name.StartsWith('-', [StringComparison]::Ordinal) -or
                            $name.Length -lt 2) {
                        throw 'The elevated update helper received an invalid argument name.'
                    }
                    $boundArguments[$name.Substring(1)] = $scriptArguments[$index + 1]
                }
                & ([ScriptBlock]::Create($scriptText)) @boundArguments
            }
            finally { $stream.Dispose() }
        }
        finally {
            if ($payload.CleanupDirectory) {
                try {
                    [IO.Directory]::Delete(
                        [IO.Path]::GetDirectoryName([string]$payload.ScriptPath), $true)
                }
                catch { }
            }
        }
        """;
    private const string VerifiedInstallerBootstrap = """
        $ErrorActionPreference = 'Stop'
        $payloadJson = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$PAYLOAD_BASE64$'))
        $payload = $payloadJson | ConvertFrom-Json
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isElevated = $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isElevated) { throw 'The update installer bootstrap is not elevated.' }
        $root = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonApplicationData)
        $directory = [IO.Path]::Combine($root,
            'iPhoneMirror-Installer-' + [Guid]::NewGuid().ToString('N'))
        $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $rights = [Security.AccessControl.FileSystemRights]::FullControl
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $administrators, $rights, $inheritance, $propagation, $allow))
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $system, $rights, $inheritance, $propagation, $allow))
        [IO.DirectoryInfo]::new($directory).Create($security)
        try {
            $source = [IO.File]::Open([string]$payload.PackagePath,
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $algorithm.ComputeHash($source)).Replace('-', '')
                }
                finally { $algorithm.Dispose() }
                if (-not $actual.Equals([string]$payload.ExpectedSha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The update installer changed after verification.'
                }
                $source.Position = 0
                $destination = [IO.Path]::Combine($directory,
                    [IO.Path]::GetFileName([string]$payload.PackagePath))
                $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $source.CopyTo($output); $output.Flush() }
                finally { $output.Dispose() }
            }
            finally { $source.Dispose() }
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $destination
            $start.Arguments = [string]$payload.ArgumentLine
            $start.WorkingDirectory = $directory
            $start.UseShellExecute = $false
            $process = [Diagnostics.Process]::Start($start)
            if ($null -eq $process) { throw 'The verified update installer did not start.' }
            try { $process.WaitForExit() }
            finally { $process.Dispose() }
        }
        finally {
            try { [IO.Directory]::Delete($directory, $true) } catch { }
        }
        """;

    private sealed record VerifiedScriptPayload(string ScriptPath,
        string ExpectedSha256, string[] Arguments, bool CleanupDirectory);
    private sealed record VerifiedInstallerPayload(string PackagePath,
        string ExpectedSha256, string ArgumentLine);

    internal static void Launch(DownloadedUpdate update)
    {
        if (!update.HashVerified)
            throw new InvalidDataException(
                "The update package was not verified and will not be executed.");
        if (!IsSha256(update.VerifiedSha256))
            throw new InvalidDataException(
                "The verified update digest is missing or invalid.");
        DiagnosticLogger.Info("updater", "installer_launch_begin",
            ("release", update.Release.TagName), ("asset", update.Asset.Name),
            ("sha256_verified", update.HashVerified));
        var sharedRuntime = DeploymentLayout.UsesSharedRuntime();
        ValidateAssetForDeployment(update.Asset.Name, sharedRuntime);
        var isInstaller = update.Asset.Name.EndsWith(".exe",
            StringComparison.OrdinalIgnoreCase);
        if (isInstaller)
        {
            using (LockAndValidatePackage(update.Path, update.VerifiedSha256!)) { }
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(update.Path),
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-EncodedCommand", BuildVerifiedInstallerBootstrap(update.Path,
                        update.VerifiedSha256!, BuildInstallerArguments()),
                },
            });
            if (process is null)
                throw new InvalidOperationException("The update installer could not be started.");
            DiagnosticLogger.Info("updater", "installer_launched",
                ("format", "exe"), ("pid", process.Id));
            return;
        }

        if (!update.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded update format is unsupported.");
        var helperBytes = ReadZipHelperBytes();
        var helperSha256 = Convert.ToHexString(SHA256.HashData(helperBytes));
        var helperDirectory = Path.Combine(Path.GetTempPath(), "iPhoneMirror",
            "Updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var helperScript = Path.Combine(helperDirectory, "Apply-ZipUpdate.ps1");
        using (var output = new FileStream(helperScript, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            output.Write(helperBytes);
            output.Flush(flushToDisk: true);
        }
        var executable = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "iPhoneMirror.exe");
        var waitProcessIds = new List<int> { Environment.ProcessId };
        waitProcessIds.AddRange(FindDriverProcessIds(AppContext.BaseDirectory));
        var scriptArguments = BuildZipArguments(update.Path,
            AppContext.BaseDirectory, executable, update.VerifiedSha256!, waitProcessIds);
        var encodedBootstrap = BuildVerifiedScriptBootstrap(helperScript,
            helperSha256, scriptArguments, cleanupDirectory: true);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = helperDirectory,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encodedBootstrap);
        try
        {
            using var process = Process.Start(start) ??
                throw new InvalidOperationException(
                    "The ZIP update helper could not be started.");
        }
        catch
        {
            TryDeleteHelperDirectory(helperDirectory);
            throw;
        }
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

    internal static IReadOnlyList<string> BuildZipArguments(string zipPath,
        string installDirectory, string restartExecutable,
        string expectedSha256, int processId) => BuildZipArguments(zipPath,
            installDirectory, restartExecutable, expectedSha256, [processId]);

    internal static IReadOnlyList<string> BuildZipArguments(string zipPath,
        string installDirectory, string restartExecutable,
        string expectedSha256, IEnumerable<int> processIds)
    {
        if (!IsSha256(expectedSha256))
            throw new ArgumentException("A valid SHA256 digest is required.",
                nameof(expectedSha256));
        var arguments = new List<string>
        {
            "-WaitPids",
        };
        var waitIds = processIds.Where(id => id > 0).Distinct().ToArray();
        if (waitIds.Length == 0)
            throw new ArgumentException("At least one process ID is required.",
                nameof(processIds));
        arguments.Add(string.Join(';', waitIds.Select(id => id.ToString(
            System.Globalization.CultureInfo.InvariantCulture))));
        arguments.AddRange([
            "-ZipPath", zipPath,
            "-ExpectedSha256", expectedSha256,
            "-InstallDirectory", installDirectory,
            "-RestartExecutable", restartExecutable,
        ]);
        return arguments;
    }

    internal static string BuildVerifiedScriptBootstrap(string scriptPath,
        string expectedSha256, IReadOnlyList<string> arguments,
        bool cleanupDirectory = false)
    {
        if (!IsSha256(expectedSha256))
            throw new ArgumentException("A valid helper SHA256 digest is required.",
                nameof(expectedSha256));
        var payload = new VerifiedScriptPayload(Path.GetFullPath(scriptPath),
            expectedSha256, arguments.ToArray(), cleanupDirectory);
        var payloadBase64 = Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(payload));
        var command = VerifiedScriptBootstrap.Replace("$PAYLOAD_BASE64$",
            payloadBase64, StringComparison.Ordinal);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    internal static string BuildVerifiedInstallerBootstrap(string packagePath,
        string expectedSha256, string argumentLine)
    {
        if (!IsSha256(expectedSha256))
            throw new ArgumentException("A valid installer SHA256 digest is required.",
                nameof(expectedSha256));
        var payload = new VerifiedInstallerPayload(Path.GetFullPath(packagePath),
            expectedSha256, argumentLine);
        var payloadBase64 = Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(payload));
        var command = VerifiedInstallerBootstrap.Replace("$PAYLOAD_BASE64$",
            payloadBase64, StringComparison.Ordinal);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static byte[] ReadZipHelperBytes()
    {
        using var input = typeof(UpdateInstallerLauncher).Assembly
            .GetManifestResourceStream(ZipHelperResourceName) ??
            throw new FileNotFoundException("The embedded ZIP update helper is missing.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void TryDeleteHelperDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("updater", "helper_cleanup_failed", error);
        }
    }

    internal static FileStream LockAndValidatePackage(string path,
        string expectedSha256)
    {
        if (!IsSha256(expectedSha256))
            throw new InvalidDataException("The verified update digest is invalid.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The update package changed after verification.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
