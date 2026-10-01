using System.Runtime.InteropServices;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;

internal static class ParentDriverTests
{
    private static readonly ParentDriverChoice Composite = new(@"C:\Windows\INF\usb.inf",
        "Composite.Dev", "USB Composite Device", "Microsoft", 0x000A000000010002, 1);
    private static readonly ParentDriverChoice Original = new(@"C:\Windows\INF\oem42.inf",
        "VendorParent.NT", "Vendor parent", "Vendor", 0x0001000000000000, 2);
    private static AppleDeviceRecord Device => new(@"USB\VID_05AC&PID_12A8\0000810100044D600A22001E",
        "0000810100044D600A22001E", "Test phone", "", "iPhone", "", "", 1, "unknown-parent",
        true, false, [], 28, false, "oem42.inf", Original.Section, Original.VersionText);

    internal static void Run()
    {
        // Native layouts must match the Windows x64 SDK, including WCHAR arrays and pointer padding.
        Check(Marshal.SizeOf<ParentDriverNative.DeviceInfo>() == 32);
        Check(Marshal.SizeOf<ParentDriverNative.DriverInfo>() == 1568);
        Check(Marshal.SizeOf<ParentDriverNative.DriverDetail>() == 1584);
        Check(Marshal.SizeOf<ParentDriverNative.InstallParameters>() == 584);

        foreach (var service in new[] { "WinUSB", "libusb0", "libusbK", "usbccgp", "usbaapl64", "AppleUSB", "custom", "" })
        foreach (var problem in new uint[] { 0, 10, 22, 28, 31, 43 })
        {
            var device = Device with { Service = service, ProblemCode = problem };
            Check(device.IsPresent && !device.IsHealthy);
            var consent = ParentDriverConsent.Create(device, ParentDriverAction.Bind, Composite);
            Check(consent.Matches(device));
            Check(ParentDriverConsent.Decode(consent.Encode())?.Matches(device) == true);
        }
        var approval = ParentDriverConsent.Create(Device, ParentDriverAction.Bind, Composite);
        Check(!approval.Matches(Device with { Service = "another-service" }));
        Check(!approval.Matches(Device with { DriverInf = "oem43.inf" }));
        Check(!approval.Matches(Device with { UpperFilters = ["unexpected-filter"] }));
        Check(!approval.Matches(Device with { LowerFilters = ["unexpected-filter"] }));
        Check(!approval.Matches(Device with { IsPresent = false }));
        Check(!approval.Matches(Device with { InstanceId = Device.InstanceId + "1" }));
        Check(ParentDriverConsent.Decode("not-base64") is null);
        Check(ParentDriverConsent.Decode(Convert.ToBase64String("{}"u8.ToArray())) is null);
        Check(ParentDriverConsent.Decode(new string('x', 16385)) is null);
        Check(!(approval with { Action = (ParentDriverAction)99 }).IsValid());
        Check(!(approval with { Action = ParentDriverAction.Reenumerate }).IsValid());
        Check(ParentDriverConsent.Create(Device, ParentDriverAction.Reenumerate, null).IsValid());
        Check(ParentDriverChange.BindingMatches(Bound(Composite) with { DriverSection = "Composite.Dev.NT" }, Composite));
        Check(!ParentDriverChange.BindingMatches(Bound(Composite) with { DriverSection = "Composite.Dev.NT.Other" }, Composite));
        Check(!ParentDriverChange.BindingMatches(Bound(Composite) with { Service = "wrong-service" }, Composite));

        var calls = 0;
        var backupSaved = false;
        var state = Device;
        ParentDriverChangeResult Apply(Func<ParentDriverChoice, bool> install, Action? backup = null) =>
            ParentDriverChange.Apply(approval, Original, () => state, install,
                backup ?? (() => backupSaved = true), _ => { });
        var success = Apply(choice =>
        {
            Check(backupSaved);
            calls++;
            state = Bound(choice);
            return false;
        });
        Check(success.Success && !success.RequiresRestart && calls == 1);

        state = Device with { Service = "changed-after-confirmation" };
        calls = 0;
        Check(Apply(_ => { calls++; return false; }).Message == "ParentChangeRejected" && calls == 0);
        state = Device;
        Check(Apply(_ => { calls++; return false; }, () => throw new IOException("backup failed"))
            .Message == "ParentChangeRejected" && calls == 0);

        // The first native install can change Windows state and then fail.
        state = Device;
        calls = 0;
        var rollback = Apply(choice =>
        {
            calls++;
            if (calls == 1) { state = Bound(choice) with { ProblemCode = 10 }; throw new IOException("partial install"); }
            Check(choice.SameDriver(Original));
            state = Device;
            return false;
        });
        Check(!rollback.Success && rollback.Message == "ParentChangeRolledBack" && calls == 2);

        state = Device;
        calls = 0;
        var unhealthy = Apply(choice =>
        {
            state = ++calls == 1 ? Bound(choice) with { ProblemCode = 31 } : Device;
            return false;
        });
        Check(unhealthy.Message == "ParentChangeRolledBack" && calls == 2);

        state = Device;
        calls = 0;
        var wrongBinding = Apply(_ => { calls++; return false; });
        Check(wrongBinding.Message == "ParentChangeRolledBack" && calls == 2);

        state = Device;
        calls = 0;
        var restart = Apply(_ => { calls++; return true; });
        Check(restart.Success && restart.RequiresRestart && calls == 1);

        state = Device;
        calls = 0;
        var rollbackRestart = Apply(_ => ++calls == 1 ? throw new IOException("install failed") : true);
        Check(!rollbackRestart.Success && rollbackRestart.RequiresRestart && calls == 2);

        state = Device;
        calls = 0;
        var incomplete = Apply(_ => { calls++; throw new IOException("native failure"); });
        Check(incomplete.Message == "ParentChangeRecoveryNeeded" && calls == 2);

        var noOriginal = ParentDriverChange.Apply(approval, null, () => Device,
            _ => throw new IOException("install failed"), () => { }, _ => { });
        Check(noOriginal.Message == "ParentChangeRecoveryNeeded");

        // Both entry points reject missing consent before UAC, files, or driver mutations.
        Check(!new DriverOperationClient().RunAsync(DriverOperationKind.ParentRepair, Device).GetAwaiter().GetResult().Success);
        Check(ElevatedDriverHost.Run([DriverConstants.ElevatedSwitch, "ParentRepair", Device.InstanceId,
            Device.Serial, Guid.NewGuid().ToString("N")]) == 2);
        Check(ElevatedDriverHost.Run([DriverConstants.ElevatedSwitch, "ParentRepair", Device.InstanceId,
            Device.Serial, Guid.NewGuid().ToString("N"), "invalid"]) == 2);

        // Enumerate real Windows candidates read-only. No calls to Install here.
        foreach (var device in new DeviceCatalog().GetAppleDevices(includeMetadata: false).Where(item => item.IsPresent))
        {
            var choices = ParentDriverNative.Enumerate(device.InstanceId);
            Console.WriteLine($"Parent enumeration: service={device.Service}, problem={device.ProblemCode}, choices={choices.Count}");
            foreach (var choice in choices)
                Console.WriteLine($"  {Path.GetFileName(choice.InfPath)} / {choice.Section} / {choice.VersionText}");
            Check(choices.Any(item => item.IsComposite));
            if (!string.IsNullOrEmpty(device.DriverInf))
                Check(choices.Any(item => ParentDriverChange.BindingMatches(device, item)));
        }
    }

    private static AppleDeviceRecord Bound(ParentDriverChoice choice) => Device with
    {
        Service = choice.IsComposite ? "usbccgp" : "unknown-parent", DriverInf = Path.GetFileName(choice.InfPath),
        DriverSection = choice.Section, DriverVersion = choice.VersionText, ProblemCode = 0, IsStarted = true,
    };

    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!condition) throw new InvalidOperationException($"Parent driver regression failed at line {line}.");
    }
}
