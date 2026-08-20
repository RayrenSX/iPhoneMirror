using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        $ErrorActionPreference = 'Stop'
        $payloadJson = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('$PAYLOAD_BASE64$'))
        $payload = $payloadJson | ConvertFrom-Json
        $commonData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonApplicationData)
        $directory = [IO.Path]::Combine($commonData,
            'iPhoneMirror-VirtualCamera-' + [Guid]::NewGuid().ToString('N'))
        $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $rights = [Security.AccessControl.FileSystemRights]::FullControl
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $administrators, $rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, $allow))
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $system, $rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, $allow))
        [IO.DirectoryInfo]::new($directory).Create($security)

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

        $exitCode = 1
        try {
            $helper = [IO.Path]::Combine($directory, 'iPhoneMirror.VirtualCamera.Admin.exe')
            $mediaSource = [IO.Path]::Combine($directory, 'iPhoneMirror.VirtualCamera.dll')
            Copy-VerifiedPayload $payload.HelperPath $payload.HelperSha256 $helper
            Copy-VerifiedPayload $payload.MediaSourcePath $payload.MediaSourceSha256 $mediaSource
            Push-Location $directory
            try {
                $arguments = if ([bool]$payload.Install) {
                    @([string]$payload.Command, ('"{0}"' -f $mediaSource))
                } else {
                    @([string]$payload.Command)
                }
                $helperProcess = Start-Process -FilePath $helper -ArgumentList $arguments `
                    -WorkingDirectory $directory -PassThru
                if (-not $helperProcess.WaitForExit(120000)) {
                    try { $helperProcess.Kill() } catch { }
                    throw 'The virtual camera helper timed out.'
                }
                $exitCode = [int]$helperProcess.ExitCode
            }
            finally { Pop-Location }
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
    {
        _frameProvider = frameProvider;
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
        => await RunAdminAsync("install", install: true, cancellationToken);

    internal static Task UninstallAsync(CancellationToken cancellationToken) =>
        RunAdminAsync("uninstall", install: false, cancellationToken);

    private static async Task RunAdminAsync(string command, bool install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0"),
            };
            var payload = new VerifiedElevationPayload(helper, mediaSource,
                Convert.ToHexString(helperHash), Convert.ToHexString(mediaSourceHash),
                command, install);
            var payloadBase64 = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(payload));
            var encodedCommand = VerifiedElevationBootstrap.Replace(
                "$PAYLOAD_BASE64$", payloadBase64, StringComparison.Ordinal);
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                         "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(encodedCommand)),
                     })
                start.ArgumentList.Add(argument);
            try
            {
                using var process = Process.Start(start) ??
                    throw new InvalidOperationException(
                        "The virtual camera installer could not be started.");
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
                        $"The virtual camera installer exited with code {process.ExitCode}.");
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException(
                    "Virtual camera installation was cancelled.", error,
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

    private sealed record VerifiedElevationPayload(string HelperPath,
        string MediaSourcePath, string HelperSha256, string MediaSourceSha256,
        string Command, bool Install);

    private static byte[] WriteEmbeddedPayload(string resourceName, string destination)
    {
        using var input = typeof(VirtualCameraService).Assembly
            .GetManifestResourceStream(resourceName) ??
            throw new FileNotFoundException(
                "The embedded virtual camera installation files are missing.", resourceName);
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
                $"The staged virtual camera payload changed unexpectedly: {Path.GetFileName(path)}");
    }

    internal async Task StartAsync(ulong sessionHandle, uint width, uint height,
        int frameRate,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sessionHandle);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                throw new InvalidOperationException(
                    "The virtual camera is already running.");
            // MFVirtualCamera::Start can synchronously activate Windows camera
            // infrastructure. Keep that work off WPF's dispatcher thread.
            var result = await Task.Run(
                () => im_vcam_start_ex("iPhoneMirror Virtual Camera",
                    width, height, checked((uint)frameRate)),
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
        catch (Exception error)
        {
            DiagnosticLogger.Exception("virtual_camera", "start_failed", error);
            try { await Task.Run(im_vcam_stop); }
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
        }
        finally
        {
            _lifecycleGate.Release();
        }
        if (task is null)
        {
            try { await Task.Run(im_vcam_stop); }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("virtual_camera", "idle_stop_failed", error);
            }
            return;
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
        long lastSourceTimestamp = long.MinValue;
        DateTime lastFrameAdvanceAtUtc = default;
        var firstFrameWait = Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / frameRate));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var frame = _frameProvider(sessionHandle, width, height);
                if (frame is not null &&
                    (lastFrame is null || frame.Timestamp100Ns > lastSourceTimestamp))
                {
                    lastFrame = frame;
                    lastSourceTimestamp = frame.Timestamp100Ns;
                    lastFrameAdvanceAtUtc = DateTime.UtcNow;
                }
                else if (lastFrame is null)
                {
                    if (firstFrameWait.Elapsed > FrameTimeout)
                        throw new TimeoutException(
                            "No projection frame was received for 5 seconds.");
                    continue;
                }
                else if (DateTime.UtcNow - lastFrameAdvanceAtUtc > FrameTimeout)
                {
                    throw new TimeoutException(
                        "The projection frame stopped advancing for 5 seconds.");
                }

                var currentFrame = lastFrame;
                var result = im_vcam_publish_bgra(currentFrame.Pixels,
                    currentFrame.Width, currentFrame.Height, currentFrame.Stride,
                    currentFrame.Timestamp100Ns);
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
            try { im_vcam_stop(); } catch (Exception error) { failure ??= error; }
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
