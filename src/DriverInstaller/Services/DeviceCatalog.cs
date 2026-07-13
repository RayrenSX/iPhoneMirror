using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed class DeviceCatalog
{
    private const string AppleVendorPrefix = "VID_05AC&PID_";
    private const uint CrSuccess = 0;

    internal IReadOnlyList<AppleDeviceRecord> GetAppleDevices(bool includeMetadata = true)
    {
        var devices = new List<AppleDeviceRecord>();
        var metadata = includeMetadata
            ? AppleDeviceMetadataReader.TryReadAll()
            : new Dictionary<string, AppleDeviceMetadata>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\USB", writable: false);
        if (usb is null) return devices;

        foreach (var hardwareName in usb.GetSubKeyNames()
                     .Where(name => name.StartsWith(AppleVendorPrefix,
                         StringComparison.OrdinalIgnoreCase) &&
                                    !name.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
        {
            using var hardware = usb.OpenSubKey(hardwareName, writable: false);
            if (hardware is null) continue;
            foreach (var instanceName in hardware.GetSubKeyNames())
            {
                var instanceId = $@"USB\{hardwareName}\{instanceName}";
                if (!DriverConstants.IsAllowedAppleParent(instanceId)) continue;
                using var instance = hardware.OpenSubKey(instanceName, writable: false);
                if (instance is null) continue;

                var service = instance.GetValue("Service") as string ?? string.Empty;
                var filters = ReadMultiString(instance, "UpperFilters");
                var serial = DriverConstants.NormalizeSerial(instanceName);
                metadata.TryGetValue(serial, out var deviceMetadata);
                var productType = deviceMetadata?.ProductType ??
                                  ResolveProductType(ReadMultiString(instance, "HardwareID"));
                var modelName = AppleProductNames.Resolve(productType);
                var displayName = ResolveDisplayName(
                    instance.GetValue("FriendlyName") as string ??
                    instance.GetValue("DeviceDesc") as string, instanceName);
                devices.Add(new AppleDeviceRecord(instanceId, serial, displayName,
                    productType, modelName, deviceMetadata?.DeviceName ?? string.Empty,
                    deviceMetadata?.OsVersion ?? string.Empty, 0, service,
                    IsDevicePresent(instanceId),
                    filters.Contains("libusb0", StringComparer.OrdinalIgnoreCase), filters));
            }
        }

        return devices
            .OrderByDescending(device => device.IsPresent)
            .ThenBy(device => device.ModelName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
            .Select((device, index) => device with { DeviceNumber = index + 1 })
            .ToArray();
    }

    internal AppleDeviceRecord? FindExact(string instanceId, string serial) =>
        GetAppleDevices(includeMetadata: false).FirstOrDefault(device =>
            string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(device.Serial, DriverConstants.NormalizeSerial(serial),
                StringComparison.OrdinalIgnoreCase));

    internal AppleSupportStatus InspectAppleSupport()
    {
        foreach (var serviceName in new[]
                 {
                     "Apple Mobile Device Service", "AppleMobileDeviceService",
                 })
        {
            if (!TryQueryService(serviceName, out var running)) continue;
            return new AppleSupportStatus(true, running, serviceName,
                DriverLocalization.Get(running ? "AppleServiceRunning" : "AppleServiceStopped"));
        }
        return new AppleSupportStatus(false, false, null,
            DriverLocalization.Get("AppleSupportMissing"));
    }

    internal LibUsbStackStatus InspectLibUsbStack()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\libusb0", writable: false);
            var installed = service is not null;
            var driverPath = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
            var dll64Path = Path.Combine(Environment.SystemDirectory, "libusb0.dll");
            var dll32Path = Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!,
                "SysWOW64", "libusb0.dll");
            var filesMatch = HashMatches(driverPath, DriverConstants.DriverHash) &&
                             HashMatches(dll64Path, DriverConstants.Dll64Hash) &&
                             HashMatches(dll32Path, DriverConstants.Dll32Hash);
            var version = File.Exists(driverPath)
                ? FileVersionInfo.GetVersionInfo(driverPath).FileVersion
                : null;
            var running = installed && TryQueryService("libusb0", out var serviceRunning) &&
                          serviceRunning;
            var diagnostic = !installed ? DriverLocalization.Get("LibUsbMissing") :
                !filesMatch ? DriverLocalization.Get("LibUsbFilesMismatch") :
                running ? DriverLocalization.Format("LibUsbRunning", version ?? DriverLocalization.Get("UnknownVersion")) :
                DriverLocalization.Get("LibUsbReadyOnConnect");
            return new LibUsbStackStatus(installed, running, filesMatch, version, diagnostic);
        }
        catch (Exception error)
        {
            return new LibUsbStackStatus(false, false, false, null,
                DriverLocalization.Get("LibUsbCheckFailed") + error.Message);
        }
    }

    internal static string[] ReadMultiString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string[] values => values,
            string value when !string.IsNullOrWhiteSpace(value) => [value],
            _ => [],
        };

    private static bool HashMatches(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream)), expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDisplayName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DriverLocalization.Get("AppleMobileDevice");
        var separator = raw.LastIndexOf(';');
        var value = separator >= 0 && separator + 1 < raw.Length
            ? raw[(separator + 1)..]
            : raw;
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string ResolveProductType(IEnumerable<string> hardwareIds)
    {
        foreach (var hardwareId in hardwareIds)
        {
            const string marker = "&REV_";
            var index = hardwareId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || index + marker.Length + 4 > hardwareId.Length) continue;
            var revision = hardwareId.Substring(index + marker.Length, 4);
            if (!revision.All(char.IsAsciiDigit)) continue;
            var major = int.Parse(revision[..2], System.Globalization.CultureInfo.InvariantCulture);
            var minor = int.Parse(revision[2..], System.Globalization.CultureInfo.InvariantCulture);
            if (major is <= 0 or > 99 || minor is < 0 or > 99) continue;
            return $"iPhone{major},{minor}";
        }
        return string.Empty;
    }

    private static bool IsDevicePresent(string instanceId) =>
        CM_Locate_DevNodeW(out var node, instanceId, 0) == CrSuccess &&
        CM_Get_DevNode_Status(out _, out _, node, 0) == CrSuccess;

    private static bool TryQueryService(string name, out bool running)
    {
        const uint scManagerConnect = 0x0001;
        const uint serviceQueryStatus = 0x0004;
        const uint serviceRunning = 0x00000004;
        running = false;
        var manager = OpenSCManager(null, null, scManagerConnect);
        if (manager == 0) return false;
        try
        {
            var service = OpenService(manager, name, serviceQueryStatus);
            if (service == 0) return false;
            try
            {
                if (!QueryServiceStatus(service, out var status)) return true;
                running = status.CurrentState == serviceRunning;
                return true;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceNode,
        string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status,
        out uint problemNumber, uint deviceNode, uint flags);

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
}
