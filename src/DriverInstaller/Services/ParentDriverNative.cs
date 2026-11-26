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
    private bool _disposed;
    private readonly List<(ParentDriverChoice Choice, DriverInfo Native)> _drivers = [];

    internal IReadOnlyList<ParentDriverChoice> Choices => _drivers.Select(item => item.Choice).ToArray();

    internal ParentDriverNative(string instanceId, bool installedOnly = false)
    {
        if (!DriverConstants.IsAppleMobileCaptureParent(instanceId))
            throw new InvalidOperationException("Invalid Apple mobile parent instance.");
        _set = SetupDiCreateDeviceInfoList(0, 0);
        if (_set == new nint(-1)) throw new Win32Exception();
        try
        {
            _device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            Check(SetupDiOpenDeviceInfoW(_set, instanceId, 0, 0, ref _device));
            var parameters = new InstallParameters { Size = Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            Check(SetupDiGetDeviceInstallParamsW(_set, ref _device, ref parameters));
            parameters.Flags |= 0x00800000; // DI_QUIETINSTALL
            parameters.FlagsEx |= 0x00000800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            parameters.FlagsEx &= ~0x00002000u; // Do not restrict matches to the broken driver's class.
            parameters.FlagsEx |= 0x20000000; // DI_FLAGSEX_RESTART_DEVICE_ONLY
            if (installedOnly) parameters.FlagsEx |= 0x04000000; // DI_FLAGSEX_INSTALLEDDRIVER
            Check(SetupDiSetDeviceInstallParamsW(_set, ref _device, ref parameters));
            // A device can require Apple's composite INF even though both drivers use usbccgp.
            // Use this device's compatible list so usb.inf requires a matching USB ID.
            if (!SetupDiBuildDriverInfoList(_set, ref _device, CompatibleDrivers))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is unchecked((int)0xE0000203) or unchecked((int)0xE0000228)) return;
                throw new Win32Exception(error);
            }
            for (uint index = 0; ; index++)
            {
                var driver = new DriverInfo { Size = Marshal.SizeOf<DriverInfo>() };
                if (!SetupDiEnumDriverInfoW(_set, ref _device, CompatibleDrivers, index, ref driver))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                _drivers.Add((ReadChoice(ref driver), driver));
            }
        }
        catch { Dispose(); throw; }
    }

    internal static IReadOnlyList<ParentDriverChoice> Enumerate(string instanceId) =>
        EnumerateWithDiagnostics(instanceId).Choices;

    internal static ParentDriverEnumeration EnumerateWithDiagnostics(string instanceId) =>
        CollectSources(new (string Name, Func<IReadOnlyList<ParentDriverChoice>> Read)[]
        {
            ("Compatible", () => ReadSource(instanceId, false)),
            ("Installed", () => ReadSource(instanceId, true)),
        });

    private static IReadOnlyList<ParentDriverChoice> ReadSource(string instanceId, bool installedOnly)
    {
        using var source = new ParentDriverNative(instanceId, installedOnly);
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
        // Use the confirmed driver for this exact instance, independent of rank.
        Check(DiInstallDevice(0, _set, ref _device, ref driver, 0, out var reboot));
        return reboot; // Windows signature enforcement stays enabled.
    }

    private ParentDriverChoice ReadChoice(ref DriverInfo driver)
    {
        _ = SetupDiGetDriverInfoDetailW(_set, ref _device, ref driver, 0, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || required > 1024 * 1024) throw new Win32Exception(error);
        var size = Math.Max((int)required, Marshal.SizeOf<DriverDetail>());
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, Marshal.SizeOf<DriverDetail>());
            Check(SetupDiGetDriverInfoDetailW(_set, ref _device, ref driver, buffer, (uint)size, out _));
            var detail = Marshal.PtrToStructure<DriverDetail>(buffer);
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
    private static extern bool SetupDiGetDeviceInstallParamsW(nint set, ref DeviceInfo device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceInstallParamsW(nint set, ref DeviceInfo device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(nint set, ref DeviceInfo device, uint type);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(nint set, ref DeviceInfo device, uint type, uint index, ref DriverInfo driver);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDriverInfoDetailW(nint set, ref DeviceInfo device, ref DriverInfo driver, nint detail, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiInstallDevice(nint parent, nint set, ref DeviceInfo device, ref DriverInfo driver, uint flags, out bool reboot);
}

internal sealed record ParentDriverEnumeration(IReadOnlyList<ParentDriverChoice> Choices, IReadOnlyList<string> Errors);
