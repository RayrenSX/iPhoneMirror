using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Updater;

internal static class UpdateInstallerLauncher
{
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoConsole = 0x00008000;
    private const int SwHide = 0;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileDeleteChild = 0x00000040;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPrivilegeNotHeld = 1314;

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
            // Keep the read handle open through ShellExecuteEx. FileShare.Read
            // prevents a medium-integrity process from replacing or deleting
            // the package between the digest check and the elevated bootstrap
            // opening it for its own verification.
            using var packageLock = LockAndValidatePackage(
                update.Path, update.VerifiedSha256!);
            var processId = StartPowerShell(BuildElevatedPowerShellStartInfo(
                    Path.GetDirectoryName(update.Path) ?? AppContext.BaseDirectory,
                    BuildVerifiedInstallerBootstrap(update.Path,
                        update.VerifiedSha256!, BuildInstallerArguments())));
            DiagnosticLogger.Info("updater", "installer_launched",
                ("format", "exe"), ("pid", processId));
            return;
        }

        if (!update.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded update format is unsupported.");
        // Validate the integrity boundary before materializing a helper. A
        // rejected elevated portable update must not leave temporary files.
        if (IsCurrentProcessElevated())
            throw new InvalidOperationException(
                "Portable updates must be started from a non-administrator " +
                "iPhoneMirror process. Restart the application normally and try again.");
        var requiresElevation = !CanUpdateDirectoryWithoutElevation(
            AppContext.BaseDirectory);
        if (requiresElevation && !CanSafelyElevateDirectoryTree(
                AppContext.BaseDirectory))
            throw new InvalidOperationException(
                "The portable installation contains a writable or reparse-point " +
                "subdirectory and cannot be updated safely with administrator privileges.");
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
        var start = requiresElevation
            ? BuildElevatedPowerShellStartInfo(helperDirectory, encodedBootstrap)
            : BuildUnelevatedPowerShellStartInfo(helperDirectory, encodedBootstrap);
        try
        {
            _ = StartPowerShell(start);
        }
        catch
        {
            TryDeleteHelperDirectory(helperDirectory);
            throw;
        }
        DiagnosticLogger.Info("updater", "installer_launched", ("format", "zip"),
            ("elevated", requiresElevation));
    }

    private static int StartPowerShell(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows update helpers are only supported on Windows.");

        var executeInfo = new ShellExecuteInfo
        {
            Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskNoCloseProcess | SeeMaskNoConsole,
            Verb = string.IsNullOrWhiteSpace(start.Verb) ? null : start.Verb,
            File = start.FileName,
            Parameters = string.Join(" ",
                start.ArgumentList.Select(QuoteCommandLineArgument)),
            Directory = start.WorkingDirectory,
            Show = SwHide,
        };
        if (!ShellExecuteExW(ref executeInfo))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                "Windows could not start the update helper.");
        }

        try
        {
            return executeInfo.Process == 0
                ? 0
                : checked((int)GetProcessId(executeInfo.Process));
        }
        finally
        {
            if (executeInfo.Process != 0)
                _ = CloseHandle(executeInfo.Process);
        }
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.All(character => !char.IsWhiteSpace(character) && character != '\"'))
            return value;
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    internal static ProcessStartInfo BuildElevatedPowerShellStartInfo(
        string workingDirectory, string encodedCommand)
        => BuildPowerShellStartInfo(workingDirectory, encodedCommand, "runas");

    internal static ProcessStartInfo BuildUnelevatedPowerShellStartInfo(
        string workingDirectory, string encodedCommand)
        => BuildPowerShellStartInfo(workingDirectory, encodedCommand, null);

    private static ProcessStartInfo BuildPowerShellStartInfo(
        string workingDirectory, string encodedCommand, string? verb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedCommand);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = verb ?? string.Empty,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                     "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedCommand,
                 })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static bool CanUpdateDirectoryWithoutElevation(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var probe = Path.Combine(root,
            $".iphonemirror-update-write-probe-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            {
                created = true;
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            created = false;
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or
                                      System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(probe); }
                catch (Exception error) when (error is IOException or
                                              UnauthorizedAccessException) { }
            }
        }
    }

    internal static bool CanSafelyElevateDirectoryTree(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        try
        {
            // A protected child is not a safe elevation boundary when its
            // parent can still replace it. Validate every existing component
            // back to the volume root before inspecting the update tree.
            for (var current = root;;)
            {
                if (IsReparseDirectory(current) || HasAnyDirectoryAccess(current,
                        DeleteAccess, WriteDac, WriteOwner))
                    return false;
                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrEmpty(parent)) break;
                if (HasAnyDirectoryAccess(parent, FileDeleteChild,
                        WriteDac, WriteOwner))
                    return false;
                current = parent;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                // Creating either a file or directory is enough to insert a
                // link at a future payload path. DELETE_CHILD and ACL/owner
                // rights can similarly make a checked component replaceable.
                if (IsReparseDirectory(current) || HasAnyDirectoryAccess(current,
                        FileAddFile, FileAddSubdirectory, FileDeleteChild,
                        DeleteAccess, WriteDac, WriteOwner))
                    return false;
                foreach (var child in Directory.EnumerateDirectories(current, "*",
                             SearchOption.TopDirectoryOnly))
                    pending.Push(child);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException or
                                      System.Security.SecurityException or
                                      Win32Exception)
        {
            // An elevated helper may only consume a tree whose complete path
            // topology was inspected at the caller's lower integrity level.
            return false;
        }
    }

    private static bool IsReparseDirectory(string directory) =>
        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;

    private static bool HasAnyDirectoryAccess(string directory,
        params uint[] accessMasks)
    {
        foreach (var accessMask in accessMasks)
        {
            using var handle = CreateFileW(directory, accessMask,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                0, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            if (!handle.IsInvalid) return true;

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorAccessDenied or ErrorPrivilegeNotHeld) continue;
            throw new Win32Exception(error,
                $"Windows could not inspect update-directory access: {directory}");
        }
        return false;
    }

    internal static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public nint Instance;
        public nint IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo executeInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName,
        uint desiredAccess, FileShare shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

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
