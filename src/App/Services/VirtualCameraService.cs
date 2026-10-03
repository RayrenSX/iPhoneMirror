using IPhoneMirror.App.Localization;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IPhoneMirror.App.Interop;
using IPhoneMirror.Shared.Security;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

internal sealed record VirtualCameraCapabilities(
    bool BackendAvailable,
    bool Supported,
    bool Registered,
    bool UpdateRequired,
    bool Running,
    string Detail);

internal sealed class VirtualCameraService : IAsyncDisposable
{
    private const string Library = "iPhoneMirror.VirtualCamera.dll";
    private const string AdminHelper = "iPhoneMirror.VirtualCamera.Admin.exe";
    private const string MediaSourceResource =
        "IPhoneMirror.App.Payload.iPhoneMirror.VirtualCamera.dll";
    private const string AdminHelperResource =
        "IPhoneMirror.App.Payload.iPhoneMirror.VirtualCamera.Admin.exe";
    private const string VerifiedElevationBootstrap = """
        # Use only .NET APIs and locally defined functions: inherited PSModulePath
        # must never cause user modules to execute in the elevated process.
        $ErrorActionPreference = 'Stop'
        $helperPath = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$HELPER_PATH_BASE64$'))
        $mediaSourcePath = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$MEDIA_PATH_BASE64$'))
        $helperHash = '$HELPER_SHA256$'
        $mediaSourceHash = '$MEDIA_SHA256$'
        $install = $INSTALL$

        function Copy-VerifiedPayload([string]$sourcePath, [string]$expectedHash,
            [string]$destinationPath) {
            $source = [IO.File]::Open($sourcePath, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $algorithm.ComputeHash($source)).Replace('-', '')
                }
                finally { $algorithm.Dispose() }
                if (-not $actual.Equals($expectedHash,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The virtual camera payload changed after verification.'
                }
                $source.Position = 0
                $destination = [IO.File]::Open($destinationPath,
                    [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $source.CopyTo($destination)
                    $destination.Flush()
                }
                finally { $destination.Dispose() }
                $written = [IO.File]::OpenRead($destinationPath)
                try {
                    $algorithm = [Security.Cryptography.SHA256]::Create()
                    try {
                        $writtenHash = [BitConverter]::ToString(
                            $algorithm.ComputeHash($written)).Replace('-', '')
                    }
                    finally { $algorithm.Dispose() }
                }
                finally { $written.Dispose() }
                if (-not $writtenHash.Equals($expectedHash,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The copied virtual camera payload failed verification.'
                }
            }
            finally { $source.Dispose() }
        }

        function New-IsolatedCameraStartInfo([string]$helper, [string]$directory,
            [string]$mediaSource, [bool]$install) {
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $helper
            $start.Arguments = if ($install) { 'install "' + $mediaSource + '"' }
                else { 'uninstall' }
            $start.WorkingDirectory = $directory
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.EnvironmentVariables.Clear()
            $windows = [IO.Directory]::GetParent([Environment]::SystemDirectory).FullName
            $start.EnvironmentVariables['SystemRoot'] = $windows
            $start.EnvironmentVariables['WINDIR'] = $windows
            $start.EnvironmentVariables['PATH'] = [Environment]::SystemDirectory
            $start.EnvironmentVariables['TEMP'] = $directory
            $start.EnvironmentVariables['TMP'] = $directory
            # The native helper uses these variables to choose its install path.
            # Populate them from Windows known folders, never the inherited values.
            $programFiles = [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::ProgramFiles)
            if ([string]::IsNullOrEmpty($programFiles)) {
                throw 'Windows did not provide the Program Files directory.'
            }
            $start.EnvironmentVariables['ProgramFiles'] = $programFiles
            $start.EnvironmentVariables['ProgramW6432'] = $programFiles
            return $start
        }

        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'The virtual camera bootstrap is not elevated.'
        }
        # OS-protected ancestors and an administrator owner prevent a medium
        # integrity process from replacing the directory or changing its ACL.
        $directory = [IO.Path]::Combine([Environment]::SystemDirectory,
            'iPhoneMirror-VirtualCamera-' + [Guid]::NewGuid().ToString('N'))
        $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $security.SetOwner($administrators)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $rights = [Security.AccessControl.FileSystemRights]::FullControl
        foreach ($sid in @($administrators, $system)) {
            $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                $sid, $rights, $inheritance,
                [Security.AccessControl.PropagationFlags]::None, $allow))
        }
        $directoryInfo = [IO.DirectoryInfo]::new($directory)
        if ($directoryInfo.Exists) { throw 'The virtual camera staging directory already exists.' }
        $directoryInfo.Create($security)
        $exitCode = 1
        try {
            $helper = [IO.Path]::Combine($directory, 'iPhoneMirror.VirtualCamera.Admin.exe')
            $mediaSource = [IO.Path]::Combine($directory, 'iPhoneMirror.VirtualCamera.dll')
            Copy-VerifiedPayload $helperPath $helperHash $helper
            Copy-VerifiedPayload $mediaSourcePath $mediaSourceHash $mediaSource
            $start = New-IsolatedCameraStartInfo $helper $directory $mediaSource $install
            $helperProcess = [Diagnostics.Process]::Start($start)
            if ($null -eq $helperProcess) { throw 'The virtual camera helper did not start.' }
            try {
                if (-not $helperProcess.WaitForExit(120000)) {
                    try { $helperProcess.Kill(); $helperProcess.WaitForExit() } catch { }
                    throw 'The virtual camera helper timed out.'
                }
                $exitCode = [int]$helperProcess.ExitCode
            }
            finally { $helperProcess.Dispose() }
        }
        finally {
            try { [IO.Directory]::Delete($directory, $true) } catch { }
        }
        exit $exitCode
        """;
    private const string RegistryClassPath =
        @"Software\Classes\CLSID\{4C0D85FD-695A-491D-945B-21DDF7EEC1E2}\InprocServer32";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<ulong, uint, uint, VideoFrame?> _frameProvider;
    private readonly Func<uint, uint, int, int> _startCamera;
    private readonly Func<VideoFrame, int> _publishFrame;
    private readonly Func<int> _stopCamera;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeStatus
    {
        internal uint StructSize;
        internal uint ApiVersion;
        internal int Supported;
        internal int Registered;
        internal int Running;
        internal uint PublishedWidth;
        internal uint PublishedHeight;
        internal ulong PublishedFrames;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Message;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_vcam_get_status(ref NativeStatus status);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    private static extern int im_vcam_start(
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    private static extern int im_vcam_start_ex(
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        uint width, uint height, uint frameRate);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_vcam_publish_bgra(
        byte[] pixels, uint width, uint height, uint stride, long timestamp100Ns);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_vcam_stop();

    internal event Action<string, bool>? StatusChanged;
    internal bool IsRunning => _runTask is { IsCompleted: false };
    internal ulong SessionHandle { get; private set; }

    internal VirtualCameraService(
        Func<ulong, uint, uint, VideoFrame?> frameProvider)
        : this(frameProvider,
            (width, height, frameRate) => im_vcam_start_ex(
                "iPhoneMirror Virtual Camera", width, height, checked((uint)frameRate)),
            frame => im_vcam_publish_bgra(frame.Pixels,
                frame.Width, frame.Height, frame.Stride, frame.Timestamp100Ns),
            im_vcam_stop)
    {
    }

    internal VirtualCameraService(
        Func<ulong, uint, uint, VideoFrame?> frameProvider,
        Func<uint, uint, int, int> startCamera,
        Func<VideoFrame, int> publishFrame,
        Func<int> stopCamera)
    {
        _frameProvider = frameProvider;
        _startCamera = startCamera;
        _publishFrame = publishFrame;
        _stopCamera = stopCamera;
    }

    internal static VirtualCameraCapabilities Probe()
    {
        try
        {
            var status = new NativeStatus
            {
                StructSize = (uint)Marshal.SizeOf<NativeStatus>(),
                Message = string.Empty,
            };
            var result = im_vcam_get_status(ref status);
            if (result < 0)
                return new(true, false, false, false, false,
                    HResultMessage(result));
            var registered = status.Registered != 0;
            return new(true, status.Supported != 0, status.Registered != 0,
                registered && !InstalledComponentMatchesCurrentBuild(),
                status.Running != 0, status.Message ?? string.Empty);
        }
        catch (Exception error) when (error is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            DiagnosticLogger.ExceptionOnce("virtual-camera-probe", "virtual_camera",
                "probe_failed", error);
            return new(false, false, false, false, false, error.Message);
        }
    }

    private static bool InstalledComponentMatchesCurrentBuild()
    {
        try
        {
            var current = Path.Combine(AppContext.BaseDirectory, Library);
            using var machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var classKey = machine.OpenSubKey(RegistryClassPath);
            var installed = classKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(installed)) return false;
            if (!File.Exists(current) || !File.Exists(installed)) return false;
            using var currentStream = File.OpenRead(current);
            using var installedStream = File.OpenRead(installed);
            return SHA256.HashData(currentStream)
                .SequenceEqual(SHA256.HashData(installedStream));
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or CryptographicException)
        {
            DiagnosticLogger.ExceptionOnce("virtual-camera-version", "virtual_camera",
                "component_comparison_failed", error);
            return false;
        }
    }

    internal static async Task InstallAsync(CancellationToken cancellationToken)
        => await RunAdminAsync(install: true, cancellationToken);

    internal static Task UninstallAsync(CancellationToken cancellationToken) =>
        RunAdminAsync(install: false, cancellationToken);

    private static async Task RunAdminAsync(bool install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateElevationEnvironment(Environment.GetEnvironmentVariables());
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "iPhoneMirror",
            "VirtualCameraAdmin", Guid.NewGuid().ToString("N"));
        var retainStagingDirectory = false;
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var helper = Path.Combine(stagingDirectory, AdminHelper);
            var mediaSource = Path.Combine(stagingDirectory, Library);
            var helperHash = WriteEmbeddedPayload(AdminHelperResource, helper);
            var mediaSourceHash = WriteEmbeddedPayload(MediaSourceResource, mediaSource);
            using var elevationBoundary = ElevationPathLock.Acquire(helper, mediaSource);
            ValidateStagedPayload(helper, helperHash);
            ValidateStagedPayload(mediaSource, mediaSourceHash);

            var start = BuildAdminStartInfo(helper, mediaSource,
                Convert.ToHexString(helperHash), Convert.ToHexString(mediaSourceHash), install);
            try
            {
                using var process = Process.Start(start) ??
                    throw new InvalidOperationException(
                        LocalizationService.Get("VirtualCameraInstallerStartFailed"));
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Keep the verified source handles and directory topology
                    // locked until the elevated bootstrap has stopped reading
                    // them. A medium-integrity caller cannot reliably terminate
                    // a process after UAC elevation.
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(
                            TimeSpan.FromSeconds(15));
                    }
                    catch (TimeoutException)
                    {
                        retainStagingDirectory = true;
                        DiagnosticLogger.ExceptionOnce("virtual-camera-admin-timeout",
                            "virtual-camera", "admin_process_did_not_exit",
                            new TimeoutException(
                                "The elevated virtual camera helper did not exit after cancellation."));
                    }
                    throw;
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        LocalizationService.Format("VirtualCameraInstallerExitedFormat", process.ExitCode));
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException(
                    LocalizationService.Get("VirtualCameraInstallCancelled"), error,
                    cancellationToken);
            }
        }
        finally
        {
            try
            {
                if (!retainStagingDirectory && Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                DiagnosticLogger.Exception("virtual-camera", "admin_staging_cleanup_failed",
                    error);
            }
        }
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
                    $"Virtual camera elevation does not accept runtime overrides: {name}");
        }
    }

    internal static ProcessStartInfo BuildAdminStartInfo(string helperPath,
        string mediaSourcePath, string helperSha256, string mediaSourceSha256, bool install)
    {
        ValidateElevationEnvironment(Environment.GetEnvironmentVariables());
        if (helperSha256.Length != 64 || !helperSha256.All(Uri.IsHexDigit) ||
            mediaSourceSha256.Length != 64 || !mediaSourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA256 digests are required for both camera payloads.");
        var script = VerifiedElevationBootstrap
            .Replace("$HELPER_PATH_BASE64$", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(Path.GetFullPath(helperPath))), StringComparison.Ordinal)
            .Replace("$MEDIA_PATH_BASE64$", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(Path.GetFullPath(mediaSourcePath))), StringComparison.Ordinal)
            .Replace("$HELPER_SHA256$", helperSha256, StringComparison.Ordinal)
            .Replace("$MEDIA_SHA256$", mediaSourceSha256, StringComparison.Ordinal)
            .Replace("$INSTALL$", install ? "$true" : "$false", StringComparison.Ordinal);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                     "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                 })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static byte[] WriteEmbeddedPayload(string resourceName, string destination)
    {
        using var input = typeof(VirtualCameraService).Assembly
            .GetManifestResourceStream(resourceName) ??
            throw new FileNotFoundException(
                LocalizationService.Get("VirtualCameraPayloadMissing"), resourceName);
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        var bytes = memory.ToArray();
        using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        return SHA256.HashData(bytes);
    }

    private static void ValidateStagedPayload(string path, byte[] expectedHash)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (!SHA256.HashData(stream).SequenceEqual(expectedHash))
            throw new InvalidDataException(
                LocalizationService.Format("VirtualCameraPayloadChangedFormat", Path.GetFileName(path)));
    }

    internal async Task StartAsync(ulong sessionHandle, uint width, uint height,
        int frameRate,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sessionHandle);
        await _lifecycleGate.WaitAsync(cancellationToken);
        var startAttempted = false;
        try
        {
            if (IsRunning)
                throw new InvalidOperationException(
                    LocalizationService.Get("VirtualCameraAlreadyRunning"));
            startAttempted = true;
            // MFVirtualCamera::Start can synchronously activate Windows camera
            // infrastructure. Keep that work off WPF's dispatcher thread.
            var result = await Task.Run(
                () => _startCamera(width, height, frameRate),
                cancellationToken);
            if (result < 0) throw new InvalidOperationException(HResultMessage(result));

            var runCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SessionHandle = sessionHandle;
            _runCancellation = runCancellation;
            // PumpAsync performs a full-frame native copy on every tick. Run it
            // on the thread pool so its awaits cannot capture WPF's dispatcher.
            _runTask = Task.Run(
                () => PumpAsync(sessionHandle, width, height,
                    frameRate, runCancellation.Token),
                CancellationToken.None);
            StatusChanged?.Invoke("VirtualCamera", false);
        }
        catch (Exception error) when (startAttempted)
        {
            DiagnosticLogger.Exception("virtual_camera", "start_failed", error);
            try
            {
                // Once the pump owns this run, let its finally block release
                // the native camera. Startup observers can also throw.
                if (_runTask is not null) _runCancellation?.Cancel();
                else await Task.Run(_stopCamera);
            }
            catch (Exception cleanupError)
            {
                DiagnosticLogger.Exception("virtual_camera",
                    "failed_start_cleanup_failed", cleanupError);
            }
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task StopAsync()
    {
        Task? task;
        await _lifecycleGate.WaitAsync();
        try
        {
            _runCancellation?.Cancel();
            task = _runTask;
            if (task is null)
            {
                // Serialize even an idle native stop with StartAsync, otherwise
                // a delayed stop can close a newly started camera.
                try { await Task.Run(_stopCamera); }
                catch (Exception error)
                {
                    DiagnosticLogger.Exception("virtual_camera", "idle_stop_failed", error);
                }
                return;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private async Task PumpAsync(ulong sessionHandle, uint width, uint height,
        int frameRate,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        VideoFrame? lastFrame = null;
        var frameWait = Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / frameRate));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var frame = _frameProvider(sessionHandle, width, height);
                if (frame is not null)
                {
                    // Static AirPlay screens can retain a valid frame indefinitely.
                    // A successful read proves availability even when its source
                    // timestamp repeats or resets; only missing reads time out.
                    lastFrame = frame;
                    frameWait.Restart();
                }
                else if (lastFrame is null)
                {
                    if (frameWait.Elapsed > FrameTimeout)
                        throw new TimeoutException(
                            LocalizationService.Get("MediaOutputFrameTimeout"));
                    continue;
                }
                else if (frameWait.Elapsed > FrameTimeout)
                {
                    throw new TimeoutException(
                        LocalizationService.Get("MediaOutputFrameStalled"));
                }

                var currentFrame = lastFrame;
                var result = _publishFrame(currentFrame);
                if (result < 0)
                    throw new InvalidOperationException(HResultMessage(result));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            try { _stopCamera(); } catch (Exception error) { failure ??= error; }
            await _lifecycleGate.WaitAsync();
            try
            {
                _runTask = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
                SessionHandle = 0;
            }
            finally
            {
                _lifecycleGate.Release();
            }
            if (failure is not null)
                DiagnosticLogger.Exception("virtual_camera", "session_failed", failure,
                    ("handle", AppLog.Handle(sessionHandle)),
                    ("size", $"{width}x{height}"), ("fps", frameRate));
            StatusChanged?.Invoke(failure?.Message ?? "Stopped",
                failure is not null);
        }
    }

    private static string HResultMessage(int result) =>
        Marshal.GetExceptionForHR(result)?.Message ??
        $"Windows error 0x{unchecked((uint)result):X8}.";

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }
}
