using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in only, on the observed App Library search field. This uses real
    // capture, device sessions and HID transports; keyboard callbacks are
    // supplied by the harness rather than claiming physical SendInput capture.
    private static void ExerciseLiveKeyboardOwnership(MainWindow main, MainViewModel vm, string output)
    {
        var target = vm.SelectedDevice!.Udid;
        var hwnd = new WindowInteropHelper(main).Handle;
        var host = (IPhoneMirror.App.Controls.NativePreviewHost)main.FindName("MainPreviewHost");
        var settings = new KeyboardMappingSettings { Enabled = true, SuppressOriginalKey = true,
            // Stay inside the observed search field. Repeated taps may select
            // its text; frame inspection compares receipt, not cursor position.
            Mappings = [MappingEntry() with { X = .5, Y = .095, DeviceCoordinates = true }] };
        var hookData = Activator.CreateInstance(typeof(MainWindow).GetNestedType(
            "LowLevelKeyboardData", System.Reflection.BindingFlags.NonPublic)!)!;
        SetKeyboardField(hookData, "VirtualKey", (uint)MappingTestKey.VirtualKey);
        SetKeyboardField(hookData, "ScanCode", (uint)MappingTestKey.ScanCode);
        void Focus()
        {
            ActivateMappingTestWindow(main);
            KeyboardCall(main, "FocusControlDevice", target, (nint)0);
            host.Focus();
            MappingSetFocus(host.WindowHandle);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
        }
        void Mapping(bool enabled)
        {
            settings.Enabled = enabled;
            var error = KeyboardCall(main, "ApplyKeyboardMapping", settings);
            MappingAssert(error is null, $"Live mapping setting failed: {error}");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        }
        void Raw(int key, bool down)
        {
            var packet = Activator.CreateInstance(typeof(MainWindow).GetNestedType(
                "RawKeyboard", System.Reflection.BindingFlags.NonPublic)!)!;
            SetKeyboardField(packet, "VirtualKey", (ushort)key);
            SetKeyboardField(packet, "Message", down ? 0x100u : 0x101u);
            KeyboardCall(main, "ProcessRawKeyboardInput", packet);
        }
        void Press(int key)
        {
            Raw(key, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(30)); Raw(key, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(90));
        }
        void Hook(bool down) => KeyboardCall(main, "ProcessMappingHook", hookData, down ? (nint)0x100 : (nint)0x101);
        void Frame(string label)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(700));
            vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, label + ".png")));
            Console.WriteLine("LIVE FRAME " + label);
        }
        void ClearSearch()
        {
            var route = vm.CaptureDirectKeyboardRoute(target)!;
            // Explicit, paced releases work with the observed iOS input method;
            // back-to-back Command+A/Backspace did not clear its composition.
            for (var i = 0; i < 16; i++)
            {
                AwaitMapping(route.SendAsync(0, [0x2A], () => true));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                AwaitMapping(route.SendAsync(0, [], () => true));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
        }
        Focus();
        AwaitMapping(vm.SendUsbTouchAsync("down", .5, .095, target));
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        AwaitMapping(vm.SendUsbTouchAsync("up", .5, .095, target));
        Frame("00-search-focused");
        Console.WriteLine("LIVE INSPECTION READY: verify 00-search-focused.png, then create continue in the evidence directory.");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(output, "continue")) && wait.Elapsed < TimeSpan.FromMinutes(5))
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        MappingAssert(File.Exists(Path.Combine(output, "continue")), "Live search inspection was not completed.");
        foreach (var mode in new[] { ControlStatusMode.Usb, ControlStatusMode.Wireless, ControlStatusMode.Usb })
        {
            var label = mode == ControlStatusMode.Wireless ? "wireless" : "usb";
            if (mode != ControlStatusMode.Usb || File.Exists(Path.Combine(output, "usb-direct.png")))
            {
                Mapping(false);
                AwaitLive(vm.CancelReverseControlAsync(mode == ControlStatusMode.Usb ? ControlStatusMode.Wireless : ControlStatusMode.Usb, target));
                AwaitLive(mode == ControlStatusMode.Usb ? vm.StartUsbControlAsync(target) : vm.StartWirelessControlAsync(target));
                MappingAssert(vm.GetMappingTargetStatus() == "MappingReady", $"Live {label} route unavailable: {vm.GetMappingTargetStatus()}");
                if (mode == ControlStatusMode.Usb) label = "usb-restored";
            }
            Focus(); Mapping(false); ClearSearch();
            Press(0x51); Press(0x5A); Press(0x58);
            Frame(label + "-direct"); // qzx
            Mapping(true);
            for (var i = 0; i < 12; i++)
            {
                Hook(true); Hook(true); Raw(0x4A, true);
                Hook(false); Raw(0x4A, false);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(90));
            }
            Frame(label + "-mapping"); // still qzx, no j
            for (var i = 0; i < 20; i++)
            {
                Hook(true); Mapping(false); Raw(0x4A, true);
                Hook(false); Raw(0x4A, false); Mapping(true);
            }
            Frame(label + "-rapid-toggle"); // still qzx
            Mapping(false); Raw(0xA0, true); Mapping(true); Raw(0xA0, false);
            Mapping(false); Raw(0xA0, false); Press(0x42);
            Frame(label + "-resumed"); // qzxb; lowercase proves Shift released
            Console.WriteLine($"LIVE {label}: direct, mapping, rapid toggle and held-Shift scenarios completed; inspect device frames for receipt.");
        }
        ClearSearch();
        Frame("99-cleared");
    }

    private static void AwaitLive(Task task)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromMinutes(3))
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        MappingAssert(task.IsCompleted, "Live transport operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
