using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

internal enum IPhoneFilterDriverState
{
    NoDevice,
    Ready,
    Provisional,
    PendingRestart,
    Missing,
    PackageMissing,
    InvalidStack,
    Error,
}

internal sealed record IPhoneFilterDriverStatus(
    IPhoneFilterDriverState State,
    string Udid,
    string? InstanceId,
    string? InstalledVersion,
    string Diagnostic)
{
    public bool IsReady => State == IPhoneFilterDriverState.Ready;
    public bool CanStartCapture => State is IPhoneFilterDriverState.Ready or
        IPhoneFilterDriverState.Provisional;
    public bool CanInstall => State == IPhoneFilterDriverState.Missing;
}

internal sealed record IPhoneFilterInstallResult(
    bool Success,
    bool RequiresReplug,
    bool Cancelled,
    string Message,
    string? InstanceId,
    string? DriverVersion,
    string? LogPath,
    string? BackupPath);

/// <summary>
/// Reads the filter state for one physical iPhone and launches the small,
/// elevated installer only when that exact device instance needs it.  The WPF
/// process itself intentionally remains unelevated.
/// </summary>
internal sealed class IPhoneFilterDriverService
{
    private const string AppleVendorPrefix = "VID_05AC&PID_";
    private static readonly string PackageRoot = Path.Combine(
        AppContext.BaseDirectory, "Drivers", "libusb-win32-1.2.6.0");

    internal IPhoneFilterDriverStatus Inspect(string? udid, bool? exactBackendAvailable = null)
    {
        if (string.IsNullOrWhiteSpace(udid))
            return new(IPhoneFilterDriverState.NoDevice, string.Empty, null, null,
                "No selected iPhone.");

        var normalizedSerial = NormalizeSerial(udid);
        try
        {
            var instanceId = FindPhysicalParentInstance(normalizedSerial);
            if (instanceId is null)
                return new(IPhoneFilterDriverState.NoDevice, udid, null,
                    ReadInstalledVersion(), "The selected iPhone USB parent is not present.");

            using var deviceKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId, writable: false);
            if (deviceKey is null)
                return new(IPhoneFilterDriverState.Error, udid, instanceId,
                    ReadInstalledVersion(), "The iPhone device registry key cannot be opened.");

            var service = deviceKey.GetValue("Service") as string;
            if (!string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                return new(IPhoneFilterDriverState.InvalidStack, udid, instanceId,
                    ReadInstalledVersion(), $"Unexpected parent service: {service ?? "(none)"}.");

            var hasFilter = ReadMultiString(deviceKey, "UpperFilters")
                .Any(value => string.Equals(value, "libusb0", StringComparison.OrdinalIgnoreCase));
            var installedVersion = ReadInstalledVersion();
            if (hasFilter)
            {
                var systemDriver = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "drivers", "libusb0.sys");
                if (!File.Exists(systemDriver))
                    return new(IPhoneFilterDriverState.Error, udid, instanceId, installedVersion,
                        "The device filter is registered, but libusb0.sys is missing.");
                if (!IsServiceRunning("libusb0"))
                    return new(IPhoneFilterDriverState.PendingRestart, udid, instanceId,
                        installedVersion, "The filter is registered but has not loaded; reconnect this iPhone.");
                if (exactBackendAvailable is true)
                    return new(IPhoneFilterDriverState.Ready, udid, instanceId, installedVersion,
                        $"libusb0 {installedVersion ?? "unknown"} can open this exact iPhone serial.");
                if (exactBackendAvailable is false)
                    return new(IPhoneFilterDriverState.PendingRestart, udid, instanceId,
                        installedVersion,
                        "The filter service is running, but this iPhone serial is not visible; reconnect it.");
                // SCM state is global. With multiple phones a running service
                // does not prove that this exact serial is visible through
                // libusb0, so native capture performs the authoritative serial
                // lookup before USB activation.
                return new(IPhoneFilterDriverState.Provisional, udid, instanceId, installedVersion,
                    $"libusb0 {installedVersion ?? "unknown"} is registered; native capture will verify this serial.");
            }

            if (!PackageAvailable())
                return new(IPhoneFilterDriverState.PackageMissing, udid, instanceId,
                    installedVersion, "The bundled libusb-win32 1.2.6 driver package is incomplete.");

            return new(IPhoneFilterDriverState.Missing, udid, instanceId, installedVersion,
                "This iPhone needs the capture filter.");
        }
        catch (Exception error)
        {
            return new(IPhoneFilterDriverState.Error, udid, null, ReadInstalledVersion(), error.Message);
        }
    }

    internal async Task<IPhoneFilterInstallResult> InstallAsync(
        string udid, CancellationToken cancellationToken = default)
    {
        var status = Inspect(udid);
        if (status.CanStartCapture)
            return new(true, false, false, status.Diagnostic, status.InstanceId,
                status.InstalledVersion, null, null);
        if (!status.CanInstall || status.InstanceId is null)
            return new(false, false, false, status.Diagnostic, status.InstanceId,
                status.InstalledVersion, null, null);

        cancellationToken.ThrowIfCancellationRequested();
        var operationId = Guid.NewGuid().ToString("N");
        var (resultPath, logPath) = DriverHelperMode.GetOperationPaths(operationId);
        var executable = Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
            return new(false, false, false, "The iPhoneMirror executable path is unavailable.",
                status.InstanceId, status.InstalledVersion, logPath, null);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(DriverHelperMode.InstallSwitch);
        start.ArgumentList.Add(status.InstanceId);
        start.ArgumentList.Add(NormalizeSerial(udid));
        start.ArgumentList.Add(operationId);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new(false, false, false, "The elevated driver installer did not start.",
                    status.InstanceId, status.InstalledVersion, logPath, null);
            // Once an administrator-approved mutation starts, cancellation of
            // the GUI wait must not orphan a still-running privileged helper.
            await process.WaitForExitAsync();

            if (!File.Exists(resultPath))
                return new(false, false, false,
                    $"Driver installer exited with code {process.ExitCode} without a result file.",
                    status.InstanceId, status.InstalledVersion, logPath, null);

            await using var stream = File.OpenRead(resultPath);
            var payload = await JsonSerializer.DeserializeAsync<InstallerResultPayload>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
            if (payload is null)
                return new(false, false, false, "Driver installer returned an invalid result.",
                    status.InstanceId, status.InstalledVersion, logPath, null);

            return new(payload.Success, payload.RequiresReplug, false,
                payload.Message ?? (payload.Success ? "Driver installed." : "Driver installation failed."),
                payload.InstanceId ?? status.InstanceId, payload.DriverVersion,
                payload.LogPath ?? logPath, payload.BackupPath);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new(false, false, true, "Windows administrator approval was cancelled.",
                status.InstanceId, status.InstalledVersion, logPath, null);
        }
        catch (Exception error)
        {
            return new(false, false, false, error.Message, status.InstanceId,
                status.InstalledVersion, logPath, null);
        }
    }

    private static string? FindPhysicalParentInstance(string normalizedSerial)
    {
        using var usb = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\USB", writable: false);
        if (usb is null) return null;
        foreach (var hardwareKeyName in usb.GetSubKeyNames()
                     .Where(name => name.StartsWith(AppleVendorPrefix,
                         StringComparison.OrdinalIgnoreCase) &&
                                    !name.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
        {
            using var hardwareKey = usb.OpenSubKey(hardwareKeyName, writable: false);
            if (hardwareKey is null) continue;
            var instanceName = hardwareKey.GetSubKeyNames().FirstOrDefault(name =>
            {
                if (!string.Equals(NormalizeSerial(name), normalizedSerial,
                        StringComparison.OrdinalIgnoreCase)) return false;
                var candidate = $@"USB\{hardwareKeyName}\{name}";
                return DriverHelperMode.IsDevicePresent(candidate);
            });
            if (instanceName is not null)
                return $@"USB\{hardwareKeyName}\{instanceName}";
        }
        return null;
    }

    private static bool PackageAvailable() =>
        File.Exists(Path.Combine(PackageRoot, "amd64", "install-filter.exe")) &&
        File.Exists(Path.Combine(PackageRoot, "amd64", "libusb0.sys")) &&
        File.Exists(Path.Combine(PackageRoot, "amd64", "libusb0.dll")) &&
        File.Exists(Path.Combine(PackageRoot, "x86", "libusb0_x86.dll"));

    private static string? ReadInstalledVersion()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "libusb0.sys");
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadMultiString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string[] values => values,
            string value when !string.IsNullOrWhiteSpace(value) => [value],
            _ => [],
        };

    private static bool IsServiceRunning(string name)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceRunning = 0x00000004;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) return false;
        try
        {
            var service = OpenService(manager, name, ServiceQueryStatus);
            if (service == 0) return false;
            try
            {
                return QueryServiceStatus(service, out var status) &&
                       status.CurrentState == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint serviceManager, string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);

    internal static string NormalizeSerial(string value) => new string(
        value.Where(character => char.IsLetterOrDigit(character)).ToArray()).ToUpperInvariant();

    private sealed class InstallerResultPayload
    {
        public bool Success { get; set; }
        public bool RequiresReplug { get; set; }
        public string? Message { get; set; }
        public string? InstanceId { get; set; }
        public string? DriverVersion { get; set; }
        public string? LogPath { get; set; }
        public string? BackupPath { get; set; }
    }
}
