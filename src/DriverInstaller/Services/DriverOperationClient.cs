using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.Shared.Security;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed class DriverOperationClient
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);
    private static readonly object ElevationBoundarySync = new();
    private static ElevationPathLock? _processImageLock;
    private static string? _processImageSha256;

    internal static Exception? InitializeElevationBoundary() =>
        EnsureElevationBoundary(out var error) ? null : error;

    internal static bool EnsureElevationBoundary(out Exception? error)
    {
        lock (ElevationBoundarySync)
        {
            if (_processImageLock is not null)
            {
                error = null;
                return true;
            }
            try
            {
                // A framework-dependent apphost does not contain its code or
                // runtime configuration. Never elevate an unbundled build.
#pragma warning disable IL3000 // Empty Location is the single-file deployment check.
                if (!string.IsNullOrEmpty(typeof(DriverOperationClient).Assembly.Location) ||
                    !string.IsNullOrEmpty(typeof(object).Assembly.Location))
#pragma warning restore IL3000
                    throw new InvalidOperationException(
                        "Driver elevation requires the published self-contained single-file build.");
                var executable = Environment.ProcessPath ??
                    Process.GetCurrentProcess().MainModule?.FileName ??
                    throw new FileNotFoundException(
                        "The driver manager executable path is unavailable.");
                var imageLock = ElevationPathLock.Acquire(executable);
                try
                {
                    using var image = File.OpenRead(executable);
                    _processImageSha256 = Convert.ToHexString(SHA256.HashData(image));
                    _processImageLock = imageLock;
                }
                catch { imageLock.Dispose(); throw; }
                error = null;
                return true;
            }
            catch (Exception boundaryError)
            {
                error = boundaryError;
                return false;
            }
        }
    }

    internal event Action<string>? StatusChanged;

    internal static ProcessStartInfo BuildElevatedStartInfo(IReadOnlyList<string> arguments)
    {
        if (!EnsureElevationBoundary(out var error))
            throw new InvalidOperationException("The driver bundle could not be protected.", error);
        return DriverElevationBootstrap.BuildStartInfo(Environment.ProcessPath!,
            _processImageSha256!, arguments);
    }

    internal async Task<DriverOperationResult> RunAsync(DriverOperationKind kind,
        AppleDeviceRecord device, ParentDriverConsent? parentConsent = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var deviceFingerprint = DriverLogger.DeviceFingerprint(device.Serial);
        if (!DriverConstants.IsAppleMobileCaptureParent(device.InstanceId) ||
            (kind == DriverOperationKind.ParentRepair ? parentConsent?.Matches(device) != true : parentConsent is not null) ||
            !string.Equals(DriverConstants.NormalizeSerial(device.Serial), device.Serial,
                StringComparison.OrdinalIgnoreCase))
        {
            DriverLogger.WriteWarning("driver-operation", "target_rejected",
                ("operation", operationId), ("kind", kind),
                ("device", deviceFingerprint), ("reason", "target_validation"));
            return Failure(DriverLocalization.Get("InvalidDeviceTarget"), null);
        }

        DriverLogger.WriteEvent("driver-operation", "requested",
            ("operation", operationId), ("kind", kind), ("device", deviceFingerprint),
            ("present", device.IsPresent), ("service", device.Service),
            ("capture_filter", device.HasLibUsb0Filter));
        var paths = DriverConstants.GetOperationPaths(operationId);
        DriverLogger.WriteEvent("driver-operation", "operation_storage_resolved",
            ("operation", operationId), ("directory", paths.Directory));
        if (!EnsureElevationBoundary(out var boundaryError))
        {
            DriverLogger.WriteException("driver-operation", "elevation_boundary_failed",
                boundaryError!, ("operation", operationId), ("kind", kind),
                ("device", deviceFingerprint));
            return Failure(DriverLocalization.Get("ElevatedProcessStartFailed"), paths.LogPath);
        }
        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            DriverLogger.WriteError("driver-operation", "executable_missing",
                ("operation", operationId), ("kind", kind), ("device", deviceFingerprint));
            return Failure(DriverLocalization.Get("DriverExecutableMissing"), paths.LogPath);
        }

        var arguments = new List<string> { DriverConstants.ElevatedSwitch,
            kind.ToString(), device.InstanceId, device.Serial, operationId };
        if (parentConsent is not null) arguments.Add(parentConsent.Encode());

        try
        {
            var start = BuildElevatedStartInfo(arguments);
            DriverLogger.WriteEvent("driver-operation", "elevated_process_start",
                ("operation", operationId), ("kind", kind),
                ("process", Path.GetFileName(executable)),
                ("timeout_ms", OperationTimeout.TotalMilliseconds));
            using var cancellation = DriverOperationCancellation.Create(operationId);
            cancellationToken.ThrowIfCancellationRequested();
            using var cancelRegistration = cancellationToken.Register(cancellation.Request);
            using var process = Process.Start(start);
            if (process is null)
            {
                DriverLogger.WriteError("driver-operation", "elevated_process_start_failed",
                    ("operation", operationId), ("kind", kind));
                return Failure(DriverLocalization.Get("ElevatedProcessStartFailed"), paths.LogPath);
            }
            await DriverOperationSafety.WaitForExitAsync(process, OperationTimeout, () =>
            {
                cancellation.Request();
                DriverLogger.WriteWarning("driver-operation", "safe_cancellation_requested",
                    ("operation", operationId), ("kind", kind), ("log", paths.LogPath));
                StatusChanged?.Invoke(DriverLocalization.Format("DriverWaitingSafeStop", paths.LogPath));
            });

            DriverLogger.WriteEvent("driver-operation", "elevated_process_exit",
                ("operation", operationId), ("kind", kind), ("exit_code", process.ExitCode),
                ("elapsed_ms", timer.ElapsedMilliseconds));

            for (var attempt = 0; attempt < 10 && !File.Exists(paths.ResultPath); attempt++)
                await Task.Delay(100);
            if (!File.Exists(paths.ResultPath))
            {
                DriverLogger.WriteError("driver-operation", "result_missing",
                    ("operation", operationId), ("kind", kind), ("exit_code", process.ExitCode),
                    ("result", DriverLogger.DescribePath(paths.ResultPath)));
                return Failure(DriverLocalization.Format("ElevatedProcessNoResult", process.ExitCode),
                    paths.LogPath);
            }

            await using var stream = new FileStream(paths.ResultPath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var result = await JsonSerializer.DeserializeAsync<DriverOperationResult>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var completed = result ?? Failure(DriverLocalization.Get("ElevatedInvalidResult"), paths.LogPath);
            // Guard against the elevated host writing a result for a different device.
            if (result is not null && result.InstanceId is not null &&
                !string.Equals(result.InstanceId, device.InstanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                DriverLogger.WriteError("driver-operation", "result_instance_id_mismatch",
                    ("operation", operationId), ("kind", kind),
                    ("expected", deviceFingerprint),
                    ("actual", DriverLogger.DeviceFingerprint(result.InstanceId)));
                return Failure(
                    DriverLocalization.Get("DriverResultTargetMismatch"),
                    paths.LogPath);
            }
            if (!IsResultConsistentWithExitCode(process.ExitCode, completed))
            {
                DriverLogger.WriteError("driver-operation", "result_exit_code_mismatch",
                    ("operation", operationId), ("kind", kind),
                    ("exit_code", process.ExitCode), ("result_success", completed.Success));
                return Failure(
                    DriverLocalization.Format("DriverResultExitCodeMismatchFormat", process.ExitCode),
                    paths.LogPath);
            }
            DriverLogger.WriteEvent("driver-operation", "completed",
                ("operation", operationId), ("kind", kind), ("success", completed.Success),
                ("requires_replug", completed.RequiresReplug),
                ("elapsed_ms", timer.ElapsedMilliseconds),
                ("message", completed.Message),
                ("operation_log", DriverLogger.DescribePath(completed.LogPath)));
            return completed with { Message = DriverLocalization.LocalizeOperationResult(completed.Message) };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            DriverLogger.WriteWarning("driver-operation", "uac_cancelled",
                ("operation", operationId), ("kind", kind),
                ("elapsed_ms", timer.ElapsedMilliseconds), ("error", error.Message));
            return Failure(DriverLocalization.Get("UacCancelled"), paths.LogPath);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("driver-operation", "client_failed", error,
                ("operation", operationId), ("kind", kind),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return Failure(error.Message, paths.LogPath);
        }
    }

    private static DriverOperationResult Failure(string message, string? logPath) =>
        new(false, false, message, null, null, logPath ?? string.Empty);

    internal static bool IsResultConsistentWithExitCode(int exitCode,
        DriverOperationResult result) => (exitCode == 0) == result.Success;
}
