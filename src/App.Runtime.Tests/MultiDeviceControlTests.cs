using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestMultipleDeviceControl(MainWindow window, object vm,
        DeviceViewModel first, object firstHost, MemoryStream firstPackets, object bindings,
        bool testPointerInput = false)
    {
        var assembly = typeof(App).Assembly;
        var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var second = (DeviceViewModel)constructor.Invoke([
            "multi-device-second", "Second iPhone", "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
        ((IList)vm.GetType().GetProperty("Devices")!.GetValue(vm)!).Add(second);
        KeyboardCall(bindings, "CreateProfileFromIdentity", "Second iPhone",
            Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.DeviceIdentityType")!, "Wired"), second.Udid, null);
        var secondHost = Activator.CreateInstance(firstHost.GetType(), nonPublic: true)!;
        var bridge = (DirectUsbInputBridge)KeyboardField(secondHost, "_bridge");
        using var secondPackets = new MemoryStream();
        using var writer = new StreamWriter(secondPackets, leaveOpen: true);
        SetKeyboardField(bridge, "_stdin", writer);
        typeof(DirectUsbInputBridge).GetProperty("IsReady")!.SetValue(bridge, true);
        secondHost.GetType().GetProperty("State", KeyboardTestMembers)!.SetValue(secondHost,
            Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ReverseControlState")!, "Ready"));
        var nativeA = new Window { Width = 200, Height = 160, ShowInTaskbar = false };
        var nativeB = new Window { Width = 200, Height = 160, ShowInTaskbar = false };
        var hwndA = new WindowInteropHelper(nativeA).EnsureHandle();
        var hwndB = new WindowInteropHelper(nativeB).EnsureHandle();
        var main = new WindowInteropHelper(window).Handle;
        nint foreground = main;
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        var kind = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardKind")!;
        var eventType = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardEventArgs")!;
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        void Key(string udid, int vk, nint hwnd)
        {
            var e = Activator.CreateInstance(eventType, KeyboardTestMembers, null,
                [Enum.Parse(kind, "Down"), vk, 0], null)!;
            KeyboardCall(window, "HandleControlKeyboardInput", e, udid, false, hwnd);
        }
        int[][] Reports(MemoryStream packets)
        {
            using var copy = new MemoryStream(packets.ToArray());
            using var reader = new BinaryReader(copy);
            var values = new List<int[]>();
            while (copy.Position < copy.Length)
            {
                using var doc = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
                if (doc.RootElement.TryGetProperty("usages", out var usages))
                    values.Add(usages.EnumerateArray().Select(v => v.GetInt32()).ToArray());
            }
            return values.ToArray();
        }
        void Focus(DeviceViewModel device, nint hwnd)
        {
            foreground = hwnd == 0 ? main : hwnd;
            if (hwnd == 0) SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(window, "FocusControlDevice", device.Udid, hwnd);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        }
        try
        {
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            SetKeyboardField(vm, "_bluetoothControlInputEnabled", false);
            foreach (var (device, host) in new[] { (first, firstHost), (second, secondHost) })
            {
                SetKeyboardField(vm, "_selectedDevice", device);
                // A ready sender retains the binding snapshot captured at startup.
                // Recovery validates it against the current device profile.
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                var resolver = (DeviceIdentityResolver)KeyboardField(vm, "_identityResolver");
                control.Binding = resolver.ResolveControlBinding(device, ReverseControlMode.Usb)
                    ?? throw new InvalidOperationException($"Missing test binding for {device.Udid}.");
                control.AppleUdid = control.Binding.TargetStableId;
                control.Router.Begin(control.AppleUdid, ReverseControlMode.Usb);
                SetKeyboardField(vm, "_wirelessControlEnabled", false);
                SetKeyboardField(vm, "_wirelessTouchBridge", null);
                SetKeyboardField(vm, "_usbControlEnabled", true);
                SetKeyboardField(vm, "_usbControlConnected", true);
                SetKeyboardField(vm, "_usbControlDeviceUdid", device.Udid);
                SetKeyboardField(vm, "_usbTouchBridge", host);
            }
            ReleaseTestPhysicalKeys(window);
            Focus(first, 0);
            KeyboardCall(vm, "OnPropertyChanged", "SelectedDevice");
            var deviceList = (System.Windows.Controls.ListBox)window.FindName("DeviceListBox");
            window.UpdateLayout();
            Require(deviceList.Items.Count == 2,
                "Multiple connected devices must remain available in the device sidebar.");
            Require(window.FindName("ControlDeviceTabs") is null,
                "The removed device tab strip must not appear above the toolbar.");
            firstPackets.SetLength(0);
            secondPackets.SetLength(0);
            Key(first.Udid, 0x11, main);
            Key(first.Udid, 0x41, main);
            Require(Reports(firstPackets).Last().Contains(4) && secondPackets.Length == 0,
                "Device A keyboard leaked to B.");
            Focus(second, 0);
            Key(second.Udid, 0x42, main);
            Require(Reports(firstPackets).Last().Length == 0, "Device switch did not release A.");
            Require(Reports(secondPackets).Last().SequenceEqual(new[] { 5 }),
                "Device B inherited A's Ctrl/key state.");
            Require((bool)KeyboardCall(vm, "IsUsbControlTarget", first.Udid)!,
                "Selecting B disconnected A.");
            if (testPointerInput) TestBothMainPreviewRoutes("two wired devices");
            TestMultiDeviceInputIsolation(window, vm, first, firstHost, firstPackets,
                second, secondPackets, main, "two wired devices");

            // Block A's transport, switch to B, then release the writer. The
            // queued A press must be dropped, while the A release still passes.
            Focus(first, 0);
            var firstBridge = KeyboardField(firstHost, "_bridge");
            var gate = (SemaphoreSlim)KeyboardField(firstBridge, "_sendLock");
            gate.Wait();
            try { Key(first.Udid, 0x43, main); Focus(second, 0); }
            finally { gate.Release(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            Require(!Reports(firstPackets).Any(r => r.Contains(6)), "Queued A input survived the switch to B.");

            // The same focus routing must work with a wired/wireless pair.
            SetKeyboardField(vm, "_selectedDevice", second);
            SetKeyboardField(vm, "_usbControlEnabled", false);
            SetKeyboardField(vm, "_usbTouchBridge", null);
            SetKeyboardField(vm, "_wirelessControlEnabled", true);
            SetKeyboardField(vm, "_wirelessControlConnected", true);
            SetKeyboardField(vm, "_wirelessTouchBridge", secondHost);
            var secondControl = KeyboardCall(vm, "GetOrCreateControl", second.Udid)!;
            ((DeviceControlSession)secondControl).Router.Begin(second.Udid, ReverseControlMode.Wireless);
            SetKeyboardField(secondControl, "RequestedWireless", true);
            if (testPointerInput) TestBothMainPreviewRoutes("wired/wireless pair");
            TestMultiDeviceInputIsolation(window, vm, first, firstHost, firstPackets,
                second, secondPackets, main, "wired/wireless pair");
            if (testPointerInput)
            {
                SetKeyboardField(vm, "_selectedDevice", first);
                SetKeyboardField(vm, "_usbControlEnabled", false);
                SetKeyboardField(vm, "_usbTouchBridge", null);
                SetKeyboardField(vm, "_wirelessControlEnabled", true);
                SetKeyboardField(vm, "_wirelessControlConnected", true);
                SetKeyboardField(vm, "_wirelessTouchBridge", firstHost);
                ((DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", first.Udid)!).Router.Begin(first.Udid, ReverseControlMode.Wireless);
                TestBothMainPreviewRoutes("two wireless devices");
                TestMultiDeviceInputIsolation(window, vm, first, firstHost, firstPackets,
                    second, secondPackets, main, "two wireless devices");
                SetKeyboardField(vm, "_selectedDevice", first);
                SetKeyboardField(vm, "_wirelessControlEnabled", false);
                SetKeyboardField(vm, "_wirelessTouchBridge", null);
                SetKeyboardField(vm, "_usbControlEnabled", true);
                SetKeyboardField(vm, "_usbTouchBridge", firstHost);
                ((DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", first.Udid)!).Router.Begin(first.Udid, ReverseControlMode.Usb);
                Focus(second, 0);
            }

            // Two actual independent HWNDs with a deterministic foreground
            // snapshot; selection stays on B while both receive fresh input.
            Focus(first, hwndA);
            Key(first.Udid, 0x44, hwndA);
            Focus(second, hwndB);
            Key(second.Udid, 0x45, hwndB);
            var length = firstPackets.Length;
            Key(first.Udid, 0x46, hwndA);
            Require(firstPackets.Length == length, "Background independent A accepted input.");
            Require(Reports(firstPackets).Last().Length == 0 && Reports(secondPackets).Last().Contains(8),
                "Independent window switching failed to release A or route B.");
            Require(ReferenceEquals(KeyboardField(vm, "_selectedDevice"), second),
                "Independent focus changed the main tab.");
            if (testPointerInput)
            {
                Focus(first, hwndA);
                TestFivePointPreview(window, first, firstPackets, hwndA, "independent wired device", independent: true);
                Focus(second, hwndB);
                TestFivePointPreview(window, second, secondPackets, hwndB, "independent wireless device", independent: true);
            }
            TestIndependentBluetoothShortcutTarget(window, vm, first, second, hwndB);

            var mode = Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ControlStatusMode")!, "Usb");
            Require(!ReferenceEquals(KeyboardCall(vm, "GetControlStatus", mode, first.Udid),
                KeyboardCall(vm, "GetControlStatus", mode, second.Udid)), "Devices share progress/cancel state.");
            var firstControl = KeyboardCall(vm, "GetOrCreateControl", first.Udid)!;
            KeyboardCall(vm, "AttachUsbBridgeEvents", firstControl, firstHost, first, CancellationToken.None);
            KeyboardCall(firstHost, "OnBridgeEvent", new BridgeEvent("status", "recovery_triggered", "test recovery"));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            Require(!(bool)KeyboardCall(vm, "IsUsbControlTarget", first.Udid)! &&
                (bool)KeyboardCall(vm, "IsUsbControlTarget", second.Udid)!, "A recovery paused B's route.");
            // Restore the test sender through the real bridge event path.
            typeof(DirectUsbInputBridge).GetProperty("Udid")!.SetValue(firstBridge, first.Udid);
            KeyboardCall(firstHost, "OnBridgeEvent", new BridgeEvent("ready", null, null));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            Require((bool)KeyboardCall(vm, "IsUsbControlTarget", first.Udid)!, "A did not resume after its ready event.");
            var wirelessMode = Enum.Parse(mode.GetType(), "Wireless");
            var pendingCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            KeyboardCall(KeyboardField(secondControl, "WirelessOperation"), "RunAsync",
                (Func<CancellationToken, Task>)(_ => pendingCleanup.Task), CancellationToken.None);
            var stop = (Task)KeyboardCall(vm, "CancelReverseControlAsync", wirelessMode, second.Udid)!;
            try
            {
                var repeatedStop = (Task)KeyboardCall(vm, "CancelReverseControlAsync", wirelessMode, second.Udid)!;
                Require(!stop.IsCompleted && ReferenceEquals(stop, repeatedStop),
                    "Repeated stop did not join the original device cleanup.");
                Require((bool)KeyboardCall(vm, "IsUsbControlTarget", first.Udid)!,
                    "Waiting for B's cleanup interrupted A.");
            }
            finally { pendingCleanup.TrySetResult(); }
            while (!stop.IsCompleted) AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
            stop.GetAwaiter().GetResult();
            Require(!(bool)KeyboardCall(vm, "IsUsbControlTarget", second.Udid)! &&
                (bool)KeyboardCall(vm, "IsUsbControlTarget", first.Udid)!, "Stopping B also stopped A.");
            Focus(first, hwndA);
            Key(first.Udid, 0x47, hwndA);
            Require(Reports(firstPackets).Last().Contains(10), "A stopped receiving input after B closed.");
            Console.WriteLine("Multi-device control passed: tabs, independent HWNDs, held-key releases, queued input, isolated status and stop.");
        }
        finally
        {
            SetKeyboardField(vm, "_selectedDevice", second);
            SetKeyboardField(vm, "_usbControlEnabled", false);
            SetKeyboardField(vm, "_usbTouchBridge", null);
            SetKeyboardField(vm, "_wirelessControlEnabled", false);
            SetKeyboardField(vm, "_wirelessTouchBridge", null);
            SetKeyboardField(vm, "_selectedDevice", first);
            nativeA.Close(); nativeB.Close();
        }

        void TestBothMainPreviewRoutes(string context)
        {
            foreach (var (device, packets, otherPackets) in new[]
            {
                (first, firstPackets, secondPackets),
                (second, secondPackets, firstPackets),
            })
            {
                Focus(device, 0);
                var otherTouches = ReadPreviewTouchPackets(otherPackets).Length;
                TestMainPreviewPointerRoute(window, device, packets, context);
                Require(ReadPreviewTouchPackets(otherPackets).Length == otherTouches,
                    $"{context}: main preview touch leaked into the other device.");
            }
        }
    }
}
