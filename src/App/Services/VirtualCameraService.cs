using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using IPhoneMirror.App.Interop;
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
            return false;
        }
    }

    internal static async Task InstallAsync(CancellationToken cancellationToken)
    {
        var mediaSource = Path.Combine(AppContext.BaseDirectory, Library);
        await RunAdminAsync("install", mediaSource, cancellationToken);
    }

    internal static Task UninstallAsync(CancellationToken cancellationToken) =>
        RunAdminAsync("uninstall", null, cancellationToken);

    private static async Task RunAdminAsync(string command, string? mediaSource,
        CancellationToken cancellationToken)
    {
        var helper = Path.Combine(AppContext.BaseDirectory,
            "iPhoneMirror.VirtualCamera.Admin.exe");
        if (!File.Exists(helper) ||
            (mediaSource is not null && !File.Exists(mediaSource)))
            throw new FileNotFoundException(
                "The virtual camera installation files are missing.");

        var start = new ProcessStartInfo
        {
            FileName = helper,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(command);
        if (mediaSource is not null) start.ArgumentList.Add(mediaSource);
        try
        {
            using var process = Process.Start(start) ??
                throw new InvalidOperationException(
                    "The virtual camera installer could not be started.");
            await process.WaitForExitAsync(cancellationToken);
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
        catch
        {
            try { await Task.Run(im_vcam_stop); } catch { }
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
            try { await Task.Run(im_vcam_stop); } catch { }
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
        long lastTimestamp = long.MinValue;
        var staleSince = Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / frameRate));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var frame = _frameProvider(sessionHandle, width, height);
                if (frame is null)
                {
                    if (staleSince.Elapsed > FrameTimeout)
                        throw new TimeoutException(
                            "No projection frame was received for 5 seconds.");
                    continue;
                }
                if (frame.Timestamp100Ns != lastTimestamp)
                {
                    lastTimestamp = frame.Timestamp100Ns;
                    staleSince.Restart();
                }
                else if (staleSince.Elapsed > FrameTimeout)
                {
                    throw new TimeoutException(
                        "The projection session stopped producing frames.");
                }

                var result = im_vcam_publish_bgra(frame.Pixels, frame.Width,
                    frame.Height, frame.Stride, frame.Timestamp100Ns);
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
