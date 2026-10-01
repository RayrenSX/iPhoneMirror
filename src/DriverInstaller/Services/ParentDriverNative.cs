using System.ComponentModel;
using System.Runtime.InteropServices;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace IPhoneMirror.DriverInstaller.Services;

// SetupAPI lists are tied to one device instance. Never use the hardware-ID-wide
// UpdateDriverForPlugAndPlayDevices API here: it can change other connected phones.
internal sealed class ParentDriverNative : IDisposable
{
    private const uint CompatibleDrivers = 2;
    private readonly nint _set;
    private DeviceInfo _device;
    private nint _listDevice;
    private readonly uint _listType;
    private bool _disposed;
    private readonly List<(ParentDriverChoice Choice, DriverInfo Native)> _drivers = [];

    internal IReadOnlyList<ParentDriverChoice> Choices => _drivers.Select(item => item.Choice).ToArray();

    internal ParentDriverNative(string instanceId, bool composite = false, bool installedOnly = false)
    {
        if (!DriverConstants.IsAppleMobileCaptureParent(instanceId))
            throw new InvalidOperationException("Invalid Apple mobile parent instance.");
        _listType = composite ? 1u : CompatibleDrivers;
        _set = SetupDiCreateDeviceInfoList(0, 0);
        if (_set == new nint(-1)) throw new Win32Exception();
        try
        {
            _device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            Check(SetupDiOpenDeviceInfoW(_set, instanceId, 0, 0, ref _device));
            // A global class list is necessary for usb.inf: a broken parent can
            // belong to USBDevice (or another class), and Apple may omit USB\COMPOSITE.
            // Building this list does not alter the device's class or hardware IDs.
            if (!composite)
            {
                _listDevice = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInfo>());
                Marshal.StructureToPtr(_device, _listDevice, false);
            }
            var parameters = new InstallParameters { Size = Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            Check(SetupDiGetDeviceInstallParamsW(_set, _listDevice, ref parameters));
            parameters.Flags |= 0x00800000; // DI_QUIETINSTALL
            parameters.FlagsEx |= 0x00000800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            parameters.FlagsEx &= ~0x00002000u; // Do not restrict matches to the broken driver's class.
            parameters.FlagsEx |= 0x20000000; // DI_FLAGSEX_RESTART_DEVICE_ONLY
            if (installedOnly) parameters.FlagsEx |= 0x04000000; // DI_FLAGSEX_INSTALLEDDRIVER
            if (composite)
            {
                parameters.Flags |= 0x00010000; // DI_ENUMSINGLEINF
                parameters.DriverPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "usb.inf");
            }
            Check(SetupDiSetDeviceInstallParamsW(_set, _listDevice, ref parameters));
            if (!SetupDiBuildDriverInfoList(_set, _listDevice, _listType))
            {
                var error = Marshal.GetLastWin32Error();
                // A missing original driver must not block binding the system driver.
                if (!composite && error is unchecked((int)0xE0000203) or unchecked((int)0xE0000228)) return;
                throw new Win32Exception(error);
            }
            if (_listDevice != 0) _device = Marshal.PtrToStructure<DeviceInfo>(_listDevice);
            if (composite)
            {
                // DIOD_INHERIT_CLASSDRVS attaches the global usb.inf list to this
                // exact device without changing its registry class or binding.
                Check(SetupDiOpenDeviceInfoW(_set, instanceId, 0, 0x2, ref _device));
                _listDevice = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInfo>());
                Marshal.StructureToPtr(_device, _listDevice, false);
            }
            for (uint index = 0; ; index++)
            {
                var driver = new DriverInfo { Size = Marshal.SizeOf<DriverInfo>() };
                if (!SetupDiEnumDriverInfoW(_set, _listDevice, _listType, index, ref driver))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                var choice = ReadChoice(ref driver, out var hardwareId);
                if (!composite || choice.IsComposite && hardwareId.Equals(@"USB\COMPOSITE", StringComparison.OrdinalIgnoreCase))
                    _drivers.Add((choice, driver));
            }
        }
        catch { Dispose(); throw; }
    }

    internal static IReadOnlyList<ParentDriverChoice> Enumerate(string instanceId) =>
        EnumerateWithDiagnostics(instanceId).Choices;

    internal static ParentDriverEnumeration EnumerateWithDiagnostics(string instanceId) =>
        CollectSources(new (string Name, Func<IReadOnlyList<ParentDriverChoice>> Read)[]
        {
            ("Windows composite", () => ReadSource(instanceId, true, false)),
            ("Compatible", () => ReadSource(instanceId, false, false)),
            ("Installed", () => ReadSource(instanceId, false, true)),
        });

    private static IReadOnlyList<ParentDriverChoice> ReadSource(string instanceId, bool composite, bool installed)
    {
        using var source = new ParentDriverNative(instanceId, composite, installed);
        return source.Choices;
    }

    internal static ParentDriverEnumeration CollectSources(
        IEnumerable<(string Name, Func<IReadOnlyList<ParentDriverChoice>> Read)> sources)
    {
        var choices = new List<ParentDriverChoice>();
        var errors = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                foreach (var choice in source.Read())
                    if (!choices.Any(item => item.SameDriver(choice))) choices.Add(choice);
            }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            {
                errors.Add(source.Name + ": " + error.Message);
                DriverLogger.WriteException("parent-driver", "candidate_source_failed", error,
                    ("source", source.Name));
            }
        }
        return new(choices, errors);
    }

    internal bool Contains(ParentDriverChoice choice) => _drivers.Any(item => item.Choice.SameDriver(choice));

    internal bool Install(ParentDriverChoice choice)
    {
        var entry = _drivers.First(item => item.Choice.SameDriver(choice));
        var driver = entry.Native;
        // Apply flags to the exact opened device, including inherited class lists.
        var devicePointer = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInfo>());
        try
        {
            Marshal.StructureToPtr(_device, devicePointer, false);
            var parameters = new InstallParameters { Size = Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            Check(SetupDiGetDeviceInstallParamsW(_set, devicePointer, ref parameters));
            parameters.Flags |= 0x00800000; // DI_QUIETINSTALL
            parameters.FlagsEx |= 0x20000000; // DI_FLAGSEX_RESTART_DEVICE_ONLY
            Check(SetupDiSetDeviceInstallParamsW(_set, devicePointer, ref parameters));
        }
        finally { Marshal.FreeHGlobal(devicePointer); }
        // An explicit DriverInfo forces this selection for this device, independent of rank.
        Check(DiInstallDevice(0, _set, ref _device, ref driver, 0, out var reboot));
        return reboot; // Windows signature enforcement stays enabled.
    }

    private ParentDriverChoice ReadChoice(ref DriverInfo driver, out string hardwareId)
    {
        _ = SetupDiGetDriverInfoDetailW(_set, _listDevice, ref driver, 0, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || required > 1024 * 1024) throw new Win32Exception(error);
        var size = Math.Max((int)required, Marshal.SizeOf<DriverDetail>());
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, Marshal.SizeOf<DriverDetail>());
            Check(SetupDiGetDriverInfoDetailW(_set, _listDevice, ref driver, buffer, (uint)size, out _));
            var detail = Marshal.PtrToStructure<DriverDetail>(buffer);
            hardwareId = Marshal.PtrToStringUni(buffer + (int)Marshal.OffsetOf<DriverDetail>(nameof(DriverDetail.HardwareId))) ?? "";
            return new ParentDriverChoice(detail.InfFileName, detail.SectionName,
                driver.Description, driver.ProviderName, driver.Version,
                ((long)driver.Date.dwHighDateTime << 32) | (uint)driver.Date.dwLowDateTime);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_set != 0 && _set != new nint(-1)) SetupDiDestroyDeviceInfoList(_set);
        if (_listDevice != 0) { Marshal.FreeHGlobal(_listDevice); _listDevice = 0; }
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(); }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo { internal int Size; internal Guid ClassGuid; internal uint DevInst; internal nuint Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InstallParameters
    {
        internal int Size;
        internal uint Flags, FlagsEx;
        internal nint Parent, Callback, Context, FileQueue;
        internal nuint ClassInstallReserved;
        internal uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverInfo
    {
        internal int Size;
        internal uint DriverType;
        internal nuint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string ProviderName;
        internal FileTime Date;
        internal ulong Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverDetail
    {
        internal int Size;
        internal FileTime InfDate;
        internal uint CompatIdsOffset, CompatIdsLength;
        internal nuint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string SectionName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string InfFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] internal string HardwareId;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId, nint parent, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParamsW(nint set, nint device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceInstallParamsW(nint set, nint device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(nint set, nint device, uint type);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(nint set, nint device, uint type, uint index, ref DriverInfo driver);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDriverInfoDetailW(nint set, nint device, ref DriverInfo driver, nint detail, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiInstallDevice(nint parent, nint set, ref DeviceInfo device, ref DriverInfo driver, uint flags, out bool reboot);
}

internal sealed record ParentDriverEnumeration(IReadOnlyList<ParentDriverChoice> Choices, IReadOnlyList<string> Errors);
