using System.IO;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string name)
{
    if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"{name}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
}

// usbmux may reverse its enumeration order on every poll. Existing cards must
// never move, while a newly connected phone is appended exactly once.
Sequence(["phone-a", "phone-b"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-b", "phone-a"]),
    "reversed discovery keeps visible order");
Sequence(["phone-a", "phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-c", "phone-b", "phone-a"]),
    "new phone appends without moving selection");
Sequence(["phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b", "phone-c"], ["phone-c", "phone-b"]),
    "disconnected phone is removed without reordering survivors");
Equal("phone-b", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "PHONE-B", "phone-a"),
    "previous selection wins case-insensitively");
Equal("phone-a", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "missing", "PHONE-A"),
    "active capture is fallback selection");
Equal("airplay://phone-b", StableDeviceSelection.ChooseUdid(
        ["phone-a", "airplay://phone-b"], "phone-a", "phone-a", "AIRPLAY://PHONE-B"),
    "new wireless connection is selected once");
Equal("airplay://phone-b", StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["airplay://phone-a", "airplay://phone-b"]),
    "new wireless edge is detected");
Equal<string?>(null, StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["AIRPLAY://PHONE-A"]),
    "known wireless device is not selected repeatedly");
Equal(2, StableDeviceSelection.CalculateDropIndex(3, 0, 2, true),
    "dragging first device after last moves it to the end");
Equal(0, StableDeviceSelection.CalculateDropIndex(3, 2, 0, false),
    "dragging last device before first moves it to the start");

Equal("iPhone 12 mini", AppleProductNames.Resolve("iPhone13,1"),
    "known ProductType uses the real model name");
Equal("iPhone99,9", AppleProductNames.Resolve("iPhone99,9"),
    "unknown ProductType remains visible for diagnostics");

// Ownership remains until an explicit completed stop; changing UI selection
// or observing a capture error must not silently release it.
var session = new CaptureSessionOwnership();
Equal(true, session.SetOwner("phone-a"), "start records owner");
Equal(true, session.RequiresStopBeforeSwitch("phone-b"), "switch requires stop");
Equal(false, session.RequiresStopBeforeSwitch("PHONE-A"), "same device does not stop");
Equal("phone-a", session.OwnerUdid, "status polling does not clear owner");
Equal(true, session.SetOwner(null), "completed stop clears owner");
Equal(false, session.RequiresStopBeforeSwitch("phone-b"), "cleared session needs no second stop");

// Display outlines are selected from ProductType when available. Legacy
// Home-button displays remain rectangular, while metadata-less startup can
// use a conservative decoded-frame aspect fallback.
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve("iPhone18,3", 1206, 2622).Id,
    "iPhone17 family profile");
Equal("iphone-x",
    DeviceCornerProfileResolver.Resolve("iPhone10,3", 1125, 2436).Id,
    "iPhone X profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone10,1", 750, 1334).Id,
    "iPhone 8 remains rectangular");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone14,6", 750, 1334).Id,
    "iPhone SE 3 remains rectangular");
Equal("iphone-notch",
    DeviceCornerProfileResolver.Resolve("iPhone17,5", 1170, 2532).Id,
    "iPhone 16e retains notch profile");
Equal("iphone-12-mini",
    DeviceCornerProfileResolver.Resolve("iPhone13,1", 1080, 2340).Id,
    "iPhone 12 mini has a tighter device-specific curve");
Equal("iphone-13-mini",
    DeviceCornerProfileResolver.Resolve("iPhone14,4", 1080, 2340).Id,
    "iPhone 13 mini has a device-specific curve");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad8,1", 1668, 2388).Id,
    "2018 iPad Pro profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPad12,1", 1620, 2160).Id,
    "Home-button iPad remains rectangular");
Equal("ipad-mini-rounded",
    DeviceCornerProfileResolver.Resolve("iPad14,1", 1488, 2266).Id,
    "iPad mini profile");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve("iPad15,7", 1640, 2360).Id,
    "iPad A16 base profile");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad17,1", 1668, 2420).Id,
    "iPad M5 Pro profile");
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve(null, 1206, 2622).Id,
    "unknown phone geometry fallback");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve(null, 1640, 2360).Id,
    "unknown tablet geometry fallback");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve(null, 1000, 1000).Id,
    "ambiguous geometry does not clip");
Equal(0, DeviceCornerProfile.Rectangular.GetGdiRadius(1206),
    "rectangular fallback radius");

Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("  iPhoneMirror AirPlay  "),
    "wireless receiver name is trimmed");
Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("\r\n[];"),
    "invalid wireless receiver name falls back");
Equal(63,
    WirelessReceiverConfiguration.SanitizeReceiverName(new string('a', 80)).Length,
    "wireless receiver name respects the mDNS label limit");
Equal("1080p", WirelessReceiverConfiguration.DefaultDisplayProfile.Id,
    "wireless receiver defaults to the balanced 1080p profile");
Equal(true, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 30),
    "wireless 720p weak-network profile is supported");
Equal(false, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 60),
    "unsupported wireless profile combinations are rejected");
Equal<string?>(null,
    WirelessReceiverConfiguration.FindExecutable(Path.GetTempPath(),
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")),
    "wireless receiver discovery rejects missing executables");

var driverManagerRoot = Path.Combine(Path.GetTempPath(), "iPhoneMirror.App.Tests",
    Guid.NewGuid().ToString("N"));
try
{
    var appDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror");
    var siblingDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror.Driver");
    Directory.CreateDirectory(appDirectory);
    Directory.CreateDirectory(siblingDirectory);
    var siblingExecutable = Path.Combine(siblingDirectory, "iPhoneMirror.Driver.exe");
    File.WriteAllBytes(siblingExecutable, []);
    Equal(Path.GetFullPath(siblingExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, workingDirectory: driverManagerRoot),
        "driver manager discovery finds sibling output");

    var overrideExecutable = Path.Combine(driverManagerRoot, "custom-driver-manager.exe");
    File.WriteAllBytes(overrideExecutable, []);
    Equal(Path.GetFullPath(overrideExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, overrideExecutable,
            driverManagerRoot),
        "driver manager override takes priority");
}
finally
{
    if (Directory.Exists(driverManagerRoot)) Directory.Delete(driverManagerRoot, recursive: true);
}

Equal(false, IndependentWindowAudioPolicy.ShowMuteOthers(1),
    "single device only shows the current-window mute action");
Equal(true, IndependentWindowAudioPolicy.ShowMuteOthers(2),
    "multiple devices show the mute-other-windows action");
Sequence(["phone-b", "phone-c"],
    IndependentWindowAudioPolicy.GetOtherDeviceIds("PHONE-A",
        ["phone-a", "phone-b", "PHONE-B", "phone-c"]),
    "mute-other-windows excludes the current device and duplicate sessions");


// Closing must explicitly stop the QuickTime session before core disposal,
// and repeated close notifications must not send a second shutdown sequence.
var shutdownOrder = new List<string>();
var shutdown = new CaptureShutdownCoordinator();
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("dispose"); return Task.CompletedTask; });
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("duplicate-stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("duplicate-dispose"); return Task.CompletedTask; });
Sequence(["stop", "dispose"], shutdownOrder, "window close cleanup is ordered and idempotent");

var deviceA = new DeviceCaptureState { Udid = "phone-a", Handle = 11, FrameRate = 60, Volume = 80 };
var deviceB = new DeviceCaptureState { Udid = "phone-b", Handle = 22, FrameRate = 30, Volume = 25 };
Equal(UsbProjectionMode.Demo, deviceA.UsbProjectionMode,
    "USB projection defaults to recommended demo mode");
deviceA.UsbProjectionMode = UsbProjectionMode.AirPlay;
deviceB.UsbProjectionMode = UsbProjectionMode.Aisi;
Equal(UsbProjectionMode.AirPlay, deviceA.UsbProjectionMode,
    "device A keeps its independent USB projection mode");
Equal(UsbProjectionMode.Aisi, deviceB.UsbProjectionMode,
    "device B keeps its independent USB projection mode");
deviceB.FrameRate = 24;
Equal((ulong)11, deviceA.Handle, "switching device does not release first session");
Equal(60, deviceA.FrameRate, "device A settings remain independent");
Equal(24, deviceB.FrameRate, "device B settings update independently");

// Even when explicit stop fails, im_shutdown/dispose remains a mandatory
// defensive cleanup path.
var failureOrder = new List<string>();
var failedShutdown = new CaptureShutdownCoordinator();
try
{
    await failedShutdown.StopAndDisposeOnceAsync(
        () => { failureOrder.Add("stop"); throw new InvalidOperationException("stop failed"); },
        () => { failureOrder.Add("dispose"); return Task.CompletedTask; });
    throw new InvalidOperationException("failed shutdown should propagate its stop error");
}
catch (InvalidOperationException error) when (error.Message == "stop failed")
{
}
Sequence(["stop", "dispose"], failureOrder, "core is disposed after stop failure");

Console.WriteLine("App logic tests passed.");
