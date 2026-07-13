using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

internal enum IPhoneFilterDriverState
{
    NoDevice,
    Ready,
    Provisional,
    PendingRestart,
    Missing,
    InvalidStack,
    Error,
}

internal sealed record IPhoneFilterDriverStatus(
    IPhoneFilterDriverState State,
    string? InstalledVersion,
    string Diagnostic)
{
    public bool CanStartCapture => State is IPhoneFilterDriverState.Ready or
        IPhoneFilterDriverState.Provisional;
}

/// <summary>
/// Reads the externally installed filter state for one physical iPhone. The
/// WPF process intentionally never installs or mutates drivers.
/// </summary>
internal sealed class IPhoneFilterDriverService
{
    private const string AppleVendorPrefix = "VID_05AC&PID_";
    private const uint CrSuccess = 0x00000000;

    internal IPhoneFilterDriverStatus Inspect(string? udid, bool? exactBackendAvailable = null)
    {
        if (string.IsNullOrWhiteSpace(udid))
            return new(IPhoneFilterDriverState.NoDevice, null, "No selected iPhone.");

        var normalizedSerial = NormalizeSerial(udid);
        try
        {
            var instanceId = FindPhysicalParentInstance(normalizedSerial);
            if (instanceId is null)
                return new(IPhoneFilterDriverState.NoDevice, ReadInstalledVersion(),
                    "The selected iPhone USB parent is not present.");

            using var deviceKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId, writable: false);
            if (deviceKey is null)
                return new(IPhoneFilterDriverState.Error, ReadInstalledVersion(),
                    "The iPhone device registry key cannot be opened.");

            var service = deviceKey.GetValue("Service") as string;
            if (!string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                return new(IPhoneFilterDriverState.InvalidStack, ReadInstalledVersion(),
                    $"Unexpected parent service: {service ?? "(none)"}.");

            var hasFilter = ReadMultiString(deviceKey, "UpperFilters")
                .Any(value => string.Equals(value, "libusb0", StringComparison.OrdinalIgnoreCase));
            var installedVersion = ReadInstalledVersion();
            if (hasFilter)
            {
                var systemDriver = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "drivers", "libusb0.sys");
                if (!File.Exists(systemDriver))
                    return new(IPhoneFilterDriverState.Error, installedVersion,
                        "The device filter is registered, but libusb0.sys is missing.");
                if (!IsServiceRunning("libusb0"))
                    return new(IPhoneFilterDriverState.PendingRestart, installedVersion,
                        "The filter is registered but has not loaded; reconnect this iPhone.");
                if (exactBackendAvailable is true)
                    return new(IPhoneFilterDriverState.Ready, installedVersion,
                        $"libusb0 {installedVersion ?? "unknown"} can open this exact iPhone serial.");
                if (exactBackendAvailable is false)
                    return new(IPhoneFilterDriverState.PendingRestart, installedVersion,
                        "The filter service is running, but this iPhone serial is not visible; reconnect it.");
                // SCM state is global. With multiple phones a running service
                // does not prove that this exact serial is visible through
                // libusb0, so native capture performs the authoritative serial
                // lookup before USB activation.
                return new(IPhoneFilterDriverState.Provisional, installedVersion,
                    $"libusb0 {installedVersion ?? "unknown"} is registered; native capture will verify this serial.");
            }

            return new(IPhoneFilterDriverState.Missing, installedVersion,
                "This iPhone needs an external capture filter driver.");
        }
        catch (Exception error)
        {
            return new(IPhoneFilterDriverState.Error, ReadInstalledVersion(), error.Message);
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
                return IsDevicePresent(candidate);
            });
            if (instanceName is not null)
                return $@"USB\{hardwareKeyName}\{instanceName}";
        }
        return null;
    }

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

    private static bool IsDevicePresent(string instanceId)
    {
        var locate = CM_Locate_DevNodeW(out var node, instanceId, 0);
        return locate == CrSuccess &&
            CM_Get_DevNode_Status(out _, out _, node, 0) == CrSuccess;
    }

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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceNode,
        string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status,
        out uint problemNumber, uint deviceNode, uint flags);

    internal static string NormalizeSerial(string value) => new string(
        value.Where(character => char.IsLetterOrDigit(character)).ToArray()).ToUpperInvariant();
}
