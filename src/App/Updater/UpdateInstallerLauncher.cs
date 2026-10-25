using IPhoneMirror.App.Localization;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
        # The verified helper uses standard cmdlets. Resolve those only from
        # Windows' protected modules, before any module can be autoloaded.
        $env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory,
            'WindowsPowerShell\v1.0\Modules')
        $scriptPath = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$SCRIPT_PATH_BASE64$'))
        $expectedSha256 = '$EXPECTED_SHA256$'
        $cleanupDirectory = $CLEANUP_DIRECTORY$
        $scriptArguments = @(foreach ($argument in @($ARGUMENTS_BASE64$)) {
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($argument))
        })
        try {
            $stream = [IO.File]::Open($scriptPath,
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $algorithm.ComputeHash($stream)).Replace('-', '')
                }
                finally { $algorithm.Dispose() }
                if (-not $actual.Equals($expectedSha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The elevated update helper changed after verification.'
                }
                $stream.Position = 0
                $encoding = [Text.UTF8Encoding]::new($false, $true)
                $reader = [IO.StreamReader]::new($stream, $encoding, $true, 4096, $true)
                try { $scriptText = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
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
            if ($cleanupDirectory) {
                try {
                    [IO.Directory]::Delete(
                        [IO.Path]::GetDirectoryName($scriptPath), $true)
                }
                catch { }
            }
        }
        """;
    private const string VerifiedInstallerBootstrap = """
        $ErrorActionPreference = 'Stop'
        $packagePath = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$PACKAGE_PATH_BASE64$'))
        $expectedSha256 = '$EXPECTED_SHA256$'
        $argumentLine = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$ARGUMENT_LINE_BASE64$'))
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isElevated = $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        $requiresElevation = $REQUIRES_ELEVATION$
        if ($requiresElevation -and -not $isElevated) {
            throw 'The update installer bootstrap is not elevated.'
        }
        # Inno Setup's loader is x86 even for x64 payloads. A loader placed in
        # native System32 reopens its own path through WOW64's SysWOW64 redirect
        # and fails before it can create an installation log. Program Files is
        # protected and has no System32 filesystem redirection.
        $stagingParent = if ($isElevated) {
            [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        }
            else { [IO.Path]::GetTempPath() }
        $directory = [IO.Path]::Combine($stagingParent,
            'iPhoneMirror-Installer-' + [Guid]::NewGuid().ToString('N'))
        $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $security.SetOwner($administrators)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $rights = [Security.AccessControl.FileSystemRights]::FullControl
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $administrators, $rights, $inheritance, $propagation, $allow))
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $system, $rights, $inheritance, $propagation, $allow))
        $directoryInfo = [IO.DirectoryInfo]::new($directory)
        if ($directoryInfo.Exists) { throw 'The installer staging directory already exists.' }
        if ($isElevated) { $directoryInfo.Create($security) }
        else { $directoryInfo.Create() }
        try {
            $source = [IO.File]::Open($packagePath,
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $algorithm.ComputeHash($source)).Replace('-', '')
                }
                finally { $algorithm.Dispose() }
                if (-not $actual.Equals($expectedSha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The update installer changed after verification.'
                }
                $source.Position = 0
                $destination = [IO.Path]::Combine($directory,
                    [IO.Path]::GetFileName($packagePath))
                $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $source.CopyTo($output); $output.Flush() }
                finally { $output.Dispose() }
            }
            finally { $source.Dispose() }
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $destination
            $start.Arguments = $argumentLine
            $start.WorkingDirectory = $directory
            $start.UseShellExecute = $false
            $start.EnvironmentVariables.Clear()
            $windows = [IO.Directory]::GetParent([Environment]::SystemDirectory).FullName
            $start.EnvironmentVariables['SystemRoot'] = $windows
            $start.EnvironmentVariables['WINDIR'] = $windows
            $start.EnvironmentVariables['PATH'] = [Environment]::SystemDirectory
            $start.EnvironmentVariables['TEMP'] = $directory
            $start.EnvironmentVariables['TMP'] = $directory
            $start.EnvironmentVariables['PSModulePath'] = [IO.Path]::Combine(
                [Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules')
            $process = [Diagnostics.Process]::Start($start)
            if ($null -eq $process) { throw 'The verified update installer did not start.' }
            try {
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) {
                    throw ('The update installer failed with exit code ' + $process.ExitCode)
                }
            }
            finally { $process.Dispose() }
        }
        finally {
            try { [IO.Directory]::Delete($directory, $true) } catch { }
        }
        """;

    internal static async Task LaunchAsync(DownloadedUpdate update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateElevationEnvironment(Environment.GetEnvironmentVariables());
        if (!update.HashVerified)
            throw new InvalidDataException(
                LocalizationService.Get("UpdatePackageNotVerified"));
        if (!IsSha256(update.VerifiedSha256))
            throw new InvalidDataException(
                LocalizationService.Get("UpdateDigestMissing"));
        DiagnosticLogger.Info("updater", "installer_launch_begin",
            ("release", update.Release.TagName), ("asset", update.Asset.Name),
            ("sha256_verified", update.HashVerified));
        var sharedRuntime = DeploymentLayout.UsesSharedRuntime();
        ValidateAssetForDeployment(update.Asset.Name, sharedRuntime);
        var isInstaller = update.Asset.Name.EndsWith(".exe",
            StringComparison.OrdinalIgnoreCase);
        if (isInstaller)
        {
            var currentUser = DeploymentLayout.GetRegisteredInstallHive(
                Path.Combine(AppContext.BaseDirectory, "iPhoneMirror.exe")) ==
                Microsoft.Win32.RegistryHive.CurrentUser;
            // Keep the read handle open through ShellExecuteEx. FileShare.Read
            // prevents a medium-integrity process from replacing or deleting
            // the package between the digest check and the elevated bootstrap
            // opening it for its own verification.
            using var packageLock = await Task.Run(() => LockAndValidatePackage(
                update.Path, update.VerifiedSha256!), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var workingDirectory = Path.GetDirectoryName(update.Path) ?? AppContext.BaseDirectory;
            var bootstrap = BuildInstallerBootstrap(update.Path, update.VerifiedSha256!,
                BuildInstallerArguments(AppContext.BaseDirectory, currentUser), !currentUser);
            using var process = StartPowerShell(currentUser
                ? BuildUnelevatedPowerShellStartInfo(workingDirectory, bootstrap)
                : BuildElevatedPowerShellStartInfo(workingDirectory, bootstrap));
            DiagnosticLogger.Info("updater", "installer_launched",
                ("format", "exe"), ("pid", GetProcessId(process)));
            await WaitForHelperAsync(process);
            DiagnosticLogger.Info("updater", "installer_completed", ("format", "exe"));
            return;
        }

        if (!update.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Get("UpdateFormatUnsupported"));
        // Validate the integrity boundary before materializing a helper. A
        // rejected elevated portable update must not leave temporary files.
        if (IsCurrentProcessElevated())
            throw new InvalidOperationException(
                LocalizationService.Get("UpdatePortableElevationUnsupported"));
        var requiresElevation = !CanUpdateDirectoryWithoutElevation(
            AppContext.BaseDirectory);
        if (requiresElevation && !CanSafelyElevateDirectoryTree(
                AppContext.BaseDirectory))
            throw new InvalidOperationException(
                LocalizationService.Get("UpdatePortableDirectoryUnsafe"));
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
        var readyEventName = @"Local\iPhoneMirror.Update.Ready." + Guid.NewGuid().ToString("N");
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset,
            readyEventName);
        var cancelEventName = @"Local\iPhoneMirror.Update.Cancel." + Guid.NewGuid().ToString("N");
        using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset,
            cancelEventName);
        var scriptArguments = BuildZipArguments(update.Path,
            AppContext.BaseDirectory, executable, update.VerifiedSha256!, waitProcessIds)
            .Concat(["-ReadyEventName", readyEventName, "-CancelEventName", cancelEventName]).ToArray();
        var encodedBootstrap = BuildVerifiedScriptBootstrap(helperScript,
            helperSha256, scriptArguments, cleanupDirectory: true);
        var start = requiresElevation
            ? BuildElevatedPowerShellStartInfo(helperDirectory, encodedBootstrap)
            : BuildUnelevatedPowerShellStartInfo(helperDirectory, encodedBootstrap);
        try
        {
            using var process = StartPowerShell(start);
            DiagnosticLogger.Info("updater", "installer_launched", ("format", "zip"),
                ("elevated", requiresElevation), ("pid", GetProcessId(process)));
            // Only hand off shutdown after the helper verified and extracted the
            // payload. A successfully created PowerShell process alone is not
            // evidence that its script actually started or accepted the update.
            await WaitForHelperAsync(process, readyEvent,
                TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch
        {
            // The helper holds its own event handle. It must observe cancellation
            // before any replacement even if this caller closes its handles now.
            cancelEvent.Set();
            TryDeleteHelperDirectory(helperDirectory);
            throw;
        }
        DiagnosticLogger.Info("updater", "portable_ready", ("format", "zip"),
            ("elevated", requiresElevation));
    }

    internal static SafeProcessHandle StartPowerShell(ProcessStartInfo start)
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
                LocalizationService.Get("UpdateHelperStartFailed"));
        }

        if (executeInfo.Process == 0)
            throw new InvalidOperationException(LocalizationService.Get("UpdateHelperStartFailed"));
        return new SafeProcessHandle(executeInfo.Process, ownsHandle: true);
    }

    internal static async Task WaitForHelperAsync(SafeProcessHandle process,
        EventWaitHandle? readyEvent = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = WaitForSingleObject(process, 0);
            if (wait == 0)
            {
                if (!GetExitCodeProcess(process, out var exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (exitCode != 0 || (readyEvent is not null && !readyEvent.WaitOne(0)))
                    throw new InvalidOperationException(LocalizationService.Format(
                        "UpdateHelperExitedFormat", exitCode));
                return;
            }
            if (wait != 0x00000102) // WAIT_TIMEOUT
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (readyEvent?.WaitOne(0) == true) return;
            if (timeout is { } limit && elapsed.Elapsed >= limit)
                throw new TimeoutException(LocalizationService.Get("UpdateHelperTimedOut"));
            await Task.Delay(100, cancellationToken);
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
        ValidateElevationEnvironment(Environment.GetEnvironmentVariables());
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = verb ?? string.Empty,
            WorkingDirectory = Environment.SystemDirectory,
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

    internal static void ValidateElevationEnvironment(IDictionary environment)
    {
        foreach (DictionaryEntry entry in environment)
        {
            var name = (string)entry.Key;
            if (string.IsNullOrEmpty(entry.Value?.ToString())) continue;
            if (name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DEVPATH", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("APPDOMAIN_MANAGER_ASM", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("APPDOMAIN_MANAGER_TYPE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLRConfigFile", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Update helpers do not accept runtime overrides: {name}");
        }
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
                LocalizationService.Get("UpdateFormatUnsupported"));
        if (sharedRuntime && !isInstaller)
            throw new InvalidOperationException(
                LocalizationService.Get("UpdateSetupRequired"));
        if (!sharedRuntime && !isZip)
            throw new InvalidOperationException(
                LocalizationService.Get("UpdatePortableRequired"));
    }

    internal static string BuildInstallerArguments(string? installDirectory = null,
        bool currentUser = false)
    {
        var logPath = Path.Combine(DiagnosticLogger.DirectoryPath,
            $"installer-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        // Restart Manager must be able to finish shutdown even when an older
        // version turns WM_CLOSE into close-to-tray or cancels an async close.
        var directory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
        return "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS " +
            (currentUser ? "/CURRENTUSER " : "/ALLUSERS ") +
            $"/DIR={QuoteCommandLineArgument(directory)} /STARTAPP=1 /LOG=\"{logPath}\"";
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
        var command = VerifiedScriptBootstrap
            .Replace("$SCRIPT_PATH_BASE64$", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(Path.GetFullPath(scriptPath))), StringComparison.Ordinal)
            .Replace("$EXPECTED_SHA256$", expectedSha256, StringComparison.Ordinal)
            .Replace("$CLEANUP_DIRECTORY$", cleanupDirectory ? "$true" : "$false",
                StringComparison.Ordinal)
            .Replace("$ARGUMENTS_BASE64$", string.Join(", ", arguments.Select(argument =>
                "'" + Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)) + "'")),
                StringComparison.Ordinal);
        return EncodeFailureReportingBootstrap(command);
    }

    internal static string BuildVerifiedInstallerBootstrap(string packagePath,
        string expectedSha256, string argumentLine)
        => BuildInstallerBootstrap(packagePath, expectedSha256, argumentLine, requiresElevation: true);

    internal static string BuildInstallerBootstrap(string packagePath,
        string expectedSha256, string argumentLine, bool requiresElevation)
    {
        if (!IsSha256(expectedSha256))
            throw new ArgumentException("A valid installer SHA256 digest is required.",
                nameof(expectedSha256));
        var command = VerifiedInstallerBootstrap
            .Replace("$REQUIRES_ELEVATION$", requiresElevation ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("$PACKAGE_PATH_BASE64$", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(Path.GetFullPath(packagePath))), StringComparison.Ordinal)
            .Replace("$EXPECTED_SHA256$", expectedSha256, StringComparison.Ordinal)
            .Replace("$ARGUMENT_LINE_BASE64$", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(argumentLine)), StringComparison.Ordinal);
        return EncodeFailureReportingBootstrap(command);
    }

    private static string EncodeFailureReportingBootstrap(string command)
    {
        // The parent can report failures before shutdown. Once it has exited,
        // this helper is the only process that can explain a failed replacement
        // or restart. Use strong-named framework APIs without module discovery.
        var failureMessage = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            LocalizationService.Get("UpdateInstallFailed")));
        var reporter = """
            catch {
                $updateFailure = $_
                $parentRunning = $false
                try {
                    $parentProcess = [Diagnostics.Process]::GetProcessById($PARENT_PID$)
                    try { $parentRunning = -not $parentProcess.HasExited }
                    finally { $parentProcess.Dispose() }
                }
                catch { }
                if (-not $parentRunning) {
                    try {
                        [void][Reflection.Assembly]::Load('System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
                        $failureMessage = [Text.Encoding]::UTF8.GetString(
                            [Convert]::FromBase64String('$FAILURE_MESSAGE_BASE64$'))
                        [void][Windows.Forms.MessageBox]::Show(
                            $failureMessage + "`r`n`r`n" + $updateFailure.Exception.Message,
                            'iPhoneMirror', 0, 16)
                    }
                    catch { }
                }
                throw $updateFailure
            }
            """
            .Replace("$PARENT_PID$", Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$FAILURE_MESSAGE_BASE64$", failureMessage, StringComparison.Ordinal);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "try {\n" + command + "\n}\n" + reporter));
    }

    private static byte[] ReadZipHelperBytes()
    {
        using var input = typeof(UpdateInstallerLauncher).Assembly
            .GetManifestResourceStream(ZipHelperResourceName) ??
            throw new FileNotFoundException(LocalizationService.Get("UpdateHelperMissing"));
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
            throw new InvalidDataException(LocalizationService.Get("UpdateDigestInvalid"));
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    LocalizationService.Get("UpdatePackageChanged"));
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
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

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
