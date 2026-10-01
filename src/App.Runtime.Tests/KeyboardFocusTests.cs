using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private const BindingFlags KeyboardTestMembers =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static object KeyboardField(object owner, string name) =>
        owner.GetType().GetField(name, KeyboardTestMembers) is { } field
            ? field.GetValue(owner)!
            : owner.GetType().GetProperty(name, KeyboardTestMembers)!.GetValue(owner)!;

    private static void SetKeyboardField(object owner, string name, object? value)
    {
        if (owner.GetType().GetField(name, KeyboardTestMembers) is { } field)
        {
            field.SetValue(owner, value);
            return;
        }
        var member = name switch
        {
            "_usbControlEnabled" => "WiredEnabled", "_usbControlConnected" => "WiredConnected",
            "_usbControlDeviceUdid" => "WiredTarget", "_usbTouchBridge" => "WiredBridge",
            "_wirelessControlEnabled" => "WirelessEnabled", "_wirelessControlConnected" => "WirelessConnected",
            "_wirelessControlDeviceUdid" => "WirelessTarget", "_wirelessTouchBridge" => "WirelessBridge",
            _ => throw new MissingFieldException(owner.GetType().Name, name),
        };
        var device = (DeviceViewModel)KeyboardField(owner, "_selectedDevice");
        var control = KeyboardCall(owner, "GetOrCreateControl", device.Udid)!;
        control.GetType().GetField(member, KeyboardTestMembers)!.SetValue(control, value);
    }

    private static object? KeyboardCall(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, KeyboardTestMembers)!.Invoke(owner, args);

    private static int RunKeyboardFocusTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var other = new Window { Width = 260, Height = 160, ShowInTaskbar = false, Owner = window };
        var vm = KeyboardField(window, "_viewModel");
        const string udid = "keyboard-focus-test-iphone";
        var deviceConstructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)deviceConstructor.Invoke([
            udid, "Keyboard test", "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(deviceConstructor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        var devices = (IList)vm.GetType().GetProperty("Devices")!.GetValue(vm)!;
        // The fixture supplies a ready route; suppress device-onboarding UI.
        ((INotifyCollectionChanged)devices).CollectionChanged -=
            (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
                typeof(NotifyCollectionChangedEventHandler), window,
                typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        devices.Add(device);

        var assembly = typeof(App).Assembly;
        var tempBindings = Path.Combine(Path.GetTempPath(), $"keyboard-focus-{Guid.NewGuid():N}.json");
        var bindingsType = assembly.GetType("IPhoneMirror.App.Services.DeviceBindingManager", true)!;
        var bindings = Activator.CreateInstance(bindingsType, KeyboardTestMembers, null, [tempBindings], null)!;
        KeyboardCall(bindings, "CreateProfileFromIdentity", "Keyboard test",
            Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.DeviceIdentityType")!, "Wired"), udid, null);
        var resolverType = assembly.GetType("IPhoneMirror.App.Services.DeviceIdentityResolver", true)!;
        SetKeyboardField(vm, "_identityResolver",
            Activator.CreateInstance(resolverType, KeyboardTestMembers, null, [bindings], null));

        var hostType = assembly.GetType("IPhoneMirror.App.Services.UsbTouchBridgeHost", true)!;
        var host = Activator.CreateInstance(hostType, nonPublic: true)!;
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        SetKeyboardField(bridge, "_stdin", writer);
        typeof(DirectUsbInputBridge).GetProperty("IsReady")!.SetValue(bridge, true);
        hostType.GetProperty("State", KeyboardTestMembers)!.SetValue(host,
            Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ReverseControlState")!, "Ready"));

        try
        {
            foreach (var mode in new[] { "Bluetooth", "Usb", "Wireless" })
            {
                SetKeyboardField(vm, "_bluetoothControlEnabled", mode == "Bluetooth");
                SetKeyboardField(vm, "_bluetoothControlConnected", mode == "Bluetooth");
                SetKeyboardField(vm, "_bluetoothControlInputEnabled", mode == "Bluetooth");
                SetKeyboardField(vm, "_bluetoothControlDeviceUdid", udid);
                SetKeyboardField(vm, "_usbControlEnabled", mode == "Usb");
                SetKeyboardField(vm, "_usbControlConnected", mode == "Usb");
                SetKeyboardField(vm, "_usbControlDeviceUdid", udid);
                SetKeyboardField(vm, "_usbTouchBridge", host);
                SetKeyboardField(vm, "_wirelessControlEnabled", mode == "Wireless");
                SetKeyboardField(vm, "_wirelessControlConnected", mode == "Wireless");
                SetKeyboardField(vm, "_wirelessControlDeviceUdid", udid);
                SetKeyboardField(vm, "_wirelessTouchBridge", host);
                KeyboardCall(KeyboardField(vm, "_reverseInputRouter"), "Begin", udid,
                    Enum.Parse(assembly.GetType("IPhoneMirror.App.Services.ReverseControlMode")!, mode));
                TestKeyboardFocusRoute(window, other, udid, packets, mode != "Bluetooth");
                Console.WriteLine($"{mode}: foreground routing, releases, queued input and independent-window checks passed.");
            }
            TestBluetoothKeyboardTransportGates();
            TestMultipleDeviceControl(window, vm, device, host, packets, bindings);
            Console.WriteLine("Keyboard focus runtime tests passed (USB/Wireless packets captured in memory; no device input sent).");
            return 0;
        }
        finally
        {
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            SetKeyboardField(vm, "_usbControlEnabled", false);
            SetKeyboardField(vm, "_wirelessControlEnabled", false);
            SetKeyboardField(vm, "_usbTouchBridge", null);
            SetKeyboardField(vm, "_wirelessTouchBridge", null);
            other.Close();
            CloseWorkspaceTestWindow(window);
            app.Shutdown();
            File.Delete(tempBindings);
        }
    }

    private static void TestKeyboardFocusRoute(MainWindow window, Window other,
        string udid, MemoryStream packets, bool verifyPackets)
    {
        var assembly = typeof(App).Assembly;
        var kindType = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardKind", true)!;
        var eventType = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardEventArgs", true)!;
        var keys = (HashSet<byte>)KeyboardField(window, "_controlKeyboardUsages");
        var modifiers = (HashSet<int>)KeyboardField(window, "_controlModifierKeys");
        var mainHandle = new WindowInteropHelper(window).Handle;
        var independentHandle = new WindowInteropHelper(other).EnsureHandle();
        nint foreground = 0;
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        void Focus(Window target)
        {
            // Deterministic foreground snapshots: Windows may reject focus
            // stealing from a test process. Raise the real WPF event when the
            // main window loses activation, without moving the user's focus.
            var next = new WindowInteropHelper(target).Handle;
            var previous = foreground;
            foreground = next;
            if (previous == mainHandle && next != mainHandle)
                KeyboardCall(window, "OnDeactivated", EventArgs.Empty);
            if (previous == independentHandle && next != independentHandle)
                KeyboardCall(window, "OnIndependentKeyboardFocusChanged", udid, independentHandle, false);
            if (next == mainHandle)
                KeyboardCall(window, "OnActivated", EventArgs.Empty);
            else if ((nint)KeyboardField(window, "_activeControlWindow") == independentHandle)
                KeyboardCall(window, "OnIndependentKeyboardFocusChanged", udid, independentHandle, true);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        }
        void Key(string kind, int vk = 0, nint? source = null)
        {
            var e = Activator.CreateInstance(eventType, KeyboardTestMembers, null,
                [Enum.Parse(kindType, kind), vk, 0], null)!;
            KeyboardCall(window, "HandleControlKeyboardInput", e, udid, false, source);
        }
        int[][] ReadPackets()
        {
            using var copy = new MemoryStream(packets.ToArray());
            using var reader = new BinaryReader(copy);
            var reports = new List<int[]>();
            while (copy.Position < copy.Length)
            {
                using var document = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
                reports.Add(document.RootElement.GetProperty("usages").EnumerateArray().Select(v => v.GetInt32()).ToArray());
            }
            return reports.ToArray();
        }

        Focus(window);
        packets.SetLength(0);
        Key("Down", 0x11); // Ctrl
        Key("Down", 0x41); // A
        Require(keys.Contains(0x04) && modifiers.Count == 1, "Foreground Ctrl+A was dropped.");
        if (verifyPackets)
        {
            var report = ReadPackets().Last();
            Require(report.Contains(4) && report.Any(usage => usage is >= 0xE0 and <= 0xE7),
                "Foreground key and modifier report was not sent.");
        }

        // Same-process dialogs must be excluded too. Deactivated releases the
        // main route even when the native preview was not the focused child.
        SetKeyboardField(window, "_pasteVPending", true);
        Focus(other);
        Require(keys.Count == 0 && modifiers.Count == 0 &&
            !(bool)KeyboardField(window, "_pasteVPending"), "Focus loss retained held keys or pending paste.");
        if (verifyPackets) Require(ReadPackets().Last().Length == 0, "Focus loss did not send the release report.");
        var length = packets.Length;
        Key("Down", 0x42);
        Key("Down", 0x11);
        Key("Down", 0x56); // Background Ctrl+V must not touch the clipboard.
        Key("Up", 0x42);
        SetKeyboardField(window, "_rawKeyboardInputEnabled", true);
        var raw = Activator.CreateInstance(typeof(MainWindow).GetNestedType("RawKeyboard", BindingFlags.NonPublic)!)!;
        SetKeyboardField(raw, "VirtualKey", (ushort)0x42);
        SetKeyboardField(raw, "Message", (uint)0x0100);
        KeyboardCall(window, "ProcessRawKeyboardInput", raw);
        var wpfHandled = (bool)KeyboardCall(window, "TryRoutePreviewKeyboardEvent",
            System.Windows.Input.Key.B, Enum.Parse(kindType, "Down"))!;
        Require(keys.Count == 0 && modifiers.Count == 0 && packets.Length == length,
            "Background keys reached the route or transport.");
        Require(!wpfHandled, "The WPF fallback swallowed an unfocused key.");

        Focus(window);
        KeyboardCall(window, "ProcessRawKeyboardInput", raw);
        Require(keys.Contains(5), "Focused Raw Input was dropped.");
        Key("Reset");
        var gate = (SemaphoreSlim)KeyboardField(window, "_bluetoothRouteGate");
        gate.Wait();
        try
        {
            Key("Down", 0x43); // Queued while transport/route is busy.
            Focus(other);
            Focus(window); // Foreground again before the queued handler runs.
        }
        finally { gate.Release(); }
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        Require(keys.Count == 0, "A key queued before focus loss was replayed after refocus.");
        if (verifyPackets) Require(!ReadPackets().Any(p => p.Contains(6)), "Stale C report reached the transport.");
        Key("Down", 0x44);
        Require(keys.Contains(7), "Fresh input did not resume after refocus.");
        Key("Reset");

        // Use a real top-level HWND as the independent route. Main-window key
        // callbacks must never borrow that window's focus, nor the reverse.
        SetKeyboardField(window, "_activeControlWindow", independentHandle);
        SetKeyboardField(window, "_activeControlUdid", udid);
        Focus(other);
        Key("Down", 0x45, mainHandle);
        Require(keys.Count == 0, "A stale main-window key borrowed the independent window's focus.");
        Key("Down", 0x45, independentHandle);
        Require(keys.Contains(8), "Focused independent-window input was dropped.");
        Key("Reset", source: independentHandle); // Native WM_KILLFOCUS callback.
        Focus(window);
        Key("Down", 0x46, independentHandle);
        Require(keys.Count == 0, "Background independent-window input was forwarded.");
        SetKeyboardField(window, "_activeControlWindow", (nint)0);
        SetKeyboardField(window, "_activeControlUdid", null);

        if (verifyPackets)
        {
            var vm = KeyboardField(window, "_viewModel");
            var bridge = (DirectUsbInputBridge)KeyboardField(KeyboardField(vm, "_usbTouchBridge"), "_bridge");
            var writerGate = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
            foreach (var refocus in new[] { false, true })
            {
                Focus(window);
                Key("Reset");
                packets.SetLength(0);
                writerGate.Wait();
                try
                {
                    Key("Down", 0x58);
                    Require(packets.Length == 0, "The writer gate did not block the X report.");
                    Focus(other);
                    if (refocus) Focus(window);
                }
                finally { writerGate.Release(); }
                AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                var reports = ReadPackets();
                Require(reports.Length > 0 && reports.All(p => p.Length == 0),
                    "An old press survived the transport wait, or its release was lost.");
                Require(keys.Count == 0, "Stale keyboard state survived the transport wait.");
            }
            Focus(window);
            var guard = (Func<bool>)KeyboardCall(window, "CaptureKeyboardSendGuard", mainHandle)!;
            packets.SetLength(0);
            writerGate.Wait();
            Task paste;
            Task button;
            try
            {
                paste = (Task)KeyboardCall(vm, "SendUsbPasteTextAsync", "Focus test", udid, guard)!;
                button = (Task)KeyboardCall(vm, "SendUsbButtonAsync", (ushort)12, (ushort)0x40, "down", udid, guard)!;
                Focus(other);
            }
            finally { writerGate.Release(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            Require(paste.IsCompletedSuccessfully && button.IsCompletedSuccessfully,
                "Expired paste/button input must complete without a transport error.");
            // Parsing as keyboard reports also rejects any leaked paste or
            // button frame, which has no usages property.
            Require(ReadPackets().All(p => p.Length == 0), "Expired shortcut/paste reached the writer.");
        }
        TestKeyboardHotkeyScope(window, other, udid, Focus);
    }
}
