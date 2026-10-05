using System.IO;
using System.Windows.Input;
using System.Windows.Automation;
using System.Diagnostics;
using System.Windows.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in only, on the observed App Library search field. This uses real
    // capture, device sessions and HID transports; keyboard callbacks are
    // supplied by the harness rather than claiming physical SendInput capture.
    private static void ExerciseLiveKeyboardOwnership(MainWindow main, MainViewModel vm, string output, bool clipboard = false)
    {
        var target = vm.SelectedDevice!.Udid;
        var hwnd = new WindowInteropHelper(main).Handle;
        var host = (IPhoneMirror.App.Controls.NativePreviewHost)main.FindName("MainPreviewHost");
        var settings = new KeyboardMappingSettings { Enabled = true, SuppressOriginalKey = true,
            // Stay inside the observed search field. Repeated taps may select
            // its text; frame inspection compares receipt, not cursor position.
            Mappings = new[] { MappingTestKey.VirtualKey, 0x57, 0x41, 0x53, 0x44, 0x43, 0x56 }.Select(vk =>
                MappingEntry(MappedTouchAction.HoldUntilRelease) with
                { Key = new(vk, vk == MappingTestKey.VirtualKey ? MappingTestKey.ScanCode : 0, false),
                  X = .5, Y = .095, DeviceCoordinates = true }).ToList() };
        var hookData = Activator.CreateInstance(typeof(MainWindow).GetNestedType(
            "LowLevelKeyboardData", System.Reflection.BindingFlags.NonPublic)!)!;
        SetKeyboardField(hookData, "VirtualKey", (uint)MappingTestKey.VirtualKey);
        SetKeyboardField(hookData, "ScanCode", (uint)MappingTestKey.ScanCode);
        void Focus()
        {
            // Common file dialogs can return before their activation messages
            // finish. Reacquire only between scenarios; every real input still
            // uses the unchanged production foreground and ownership gates.
            var focusWait = Stopwatch.StartNew();
            while (focusWait.Elapsed < TimeSpan.FromSeconds(8))
            {
                ActivateMappingTestWindow(main);
                KeyboardCall(main, "FocusControlDevice", target, (nint)0);
                host.Focus();
                MappingSetFocus(host.WindowHandle);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                if ((bool)KeyboardCall(main, "CanForwardControlKeyboard", target, hwnd)!) return;
            }
            var actual = MappingGetForeground();
            MappingWindowThread(actual, out var owner);
            throw new InvalidOperationException($"Live preview lost keyboard focus: expected {hwnd}, actual {actual}, process={owner}.");
        }
        void Mapping(bool enabled)
        {
            settings.Enabled = enabled;
            var error = KeyboardCall(main, "ApplyKeyboardMapping", settings);
            MappingAssert(error is null, $"Live mapping setting failed: {error}");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        }
        void Routed(int key, bool down)
        {
            var packet = Activator.CreateInstance(typeof(MainWindow).GetNestedType(
                "LowLevelKeyboardData", System.Reflection.BindingFlags.NonPublic)!)!;
            SetKeyboardField(packet, "VirtualKey", (uint)key);
            SetKeyboardField(packet, "ScanCode", key == MappingTestKey.VirtualKey ? (uint)MappingTestKey.ScanCode : 0u);
            var consumed = KeyboardCall(main, "ProcessKeyboardHook", packet, down ? (nint)0x100 : (nint)0x101);
            if (down && key is 0x51 or 0x5A or 0x58 or 0x42 or 0x57 or 0x41 or 0x53 or 0x44)
            {
                Console.WriteLine($"LIVE ROUTE key={key:X2} consumed={consumed} mode={((KeyboardInputRouter)KeyboardField(main, "_keyboardRouter")).Mode} foreground={KeyboardCall(main, "CanForwardControlKeyboard", target, hwnd)}");
                MappingAssert(consumed is true, "Live key did not enter the expected input route.");
            }
        }
        void Press(int key)
        {
            Routed(key, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(30)); Routed(key, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(90));
        }
        void Hook(bool down) => KeyboardCall(main, "ProcessKeyboardHook", hookData, down ? (nint)0x100 : (nint)0x101);
        long lastFrameTimestamp = -1;
        void Frame(string label)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(700));
            var destination = Path.GetFullPath(Path.Combine(output, label + ".png"));
            var core = (IPhoneMirror.App.Interop.NativeCore)KeyboardField(vm, "_core");
            var timestamp = core.GetLatestVideoFrame()?.Timestamp100Ns ?? -1;
            var python = Environment.GetEnvironmentVariable("IPHONEMIRROR_LIVE_SCREENSHOT_PYTHON");
            if (!string.IsNullOrWhiteSpace(python))
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                var controls = (System.Collections.IDictionary)KeyboardField(vm, "_deviceControls");
                var control = (DeviceControlSession)controls[target]!;
                var bridgeHost = control.WiredEnabled ? control.WiredBridge : control.WirelessBridge;
                var bridge = (DirectUsbInputBridge)KeyboardField(bridgeHost!, "_bridge");
                var bridgeProcess = (Process)KeyboardField(bridge, "_process");
                foreach (var arg in new[] { Path.GetFullPath("tools/keyboard_live_screenshot.py"),
                    "--pid", bridgeProcess.Id.ToString(), "--udid", vm.ResolveAppleUdid(target)!, "--output", destination }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var limit = Stopwatch.StartNew();
                while (!process.HasExited && limit.Elapsed < TimeSpan.FromSeconds(35))
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                if (!process.HasExited) { process.Kill(entireProcessTree: true); throw new TimeoutException("Independent screenshot timed out"); }
                File.WriteAllText(destination + ".log", stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
                MappingAssert(process.ExitCode == 0 && File.Exists(destination), "Independent phone screenshot failed");
                Console.WriteLine($"LIVE FRAME {label}: independent device DVT screenshot; mirror timestamp={timestamp}, changed={timestamp != lastFrameTimestamp}");
            }
            else
            {
                MappingAssert(timestamp >= 0 && timestamp != lastFrameTimestamp,
                    "Mirror frame did not advance; cannot use a cached frame as device receipt evidence. Set IPHONEMIRROR_LIVE_SCREENSHOT_PYTHON for an independent device screenshot.");
                vm.CaptureScreenshot(destination);
                Console.WriteLine($"LIVE FRAME {label}: mirror timestamp={timestamp}");
            }
            lastFrameTimestamp = timestamp;
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
            Focus();
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
            Mapping(false); Focus(); ClearSearch(); Focus();
            if (clipboard)
            {
                foreach (var key in new[] { 0x57, 0x41, 0x53, 0x44, 0x57, 0x41, 0x53, 0x44 }) Press(key);
                Frame(label + "-direct");
                Mapping(true); Focus();
                ExerciseLiveClipboard(main, vm, target, label, Focus, ClearSearch, Frame, Routed);
                continue;
            }
            Press(0x51); Press(0x5A); Press(0x58);
            Frame(label + "-direct"); // qzx
            Focus();
            ClearSearch();
            foreach (var key in new[] { 0x57, 0x41, 0x53, 0x44, 0x57, 0x41, 0x53, 0x44 }) Press(key);
            Frame(label + "-wasd-direct"); // wasdwasd
            Focus();
            ClearSearch(); Press(0x51); Press(0x5A); Press(0x58);
            Mapping(true); Focus();
            Routed(0x57, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(650));
            for (var repeat = 0; repeat < 30; repeat++) Routed(0x57, true);
            Routed(0x57, false); AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            for (var burst = 0; burst < 4; burst++)
                foreach (var key in new[] { 0x57, 0x41, 0x53, 0x44 })
                { Routed(key, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); Routed(key, false); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); }
            for (var i = 0; i < 12; i++)
            {
                Hook(true); Hook(true); Routed(0x4A, true);
                Hook(false); Routed(0x4A, false);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(90));
            }
            Frame(label + "-mapping"); // still qzx, no j
            for (var shot = 0; shot < 2; shot++)
            {
                Focus();
                var screenshot = Path.GetFullPath(Path.Combine(output, $"{label}-shortcut-{shot}.png"));
                var save = Task.Run(() => SaveLiveScreenshotDialog(hwnd, screenshot));
                Routed(0xA2, true); Routed(0x53, true);
                for (var repeat = 0; repeat < 30; repeat++) Routed(0x53, true);
                Routed(0x53, false); Routed(0xA2, false);
                AwaitLive(save);
                var saved = Stopwatch.StartNew();
                while (!File.Exists(screenshot) && saved.Elapsed < TimeSpan.FromSeconds(10))
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                MappingAssert(File.Exists(screenshot), "Real screenshot shortcut did not save a frame.");
                Console.WriteLine($"LIVE SHORTCUT saved {Path.GetFileName(screenshot)} (30 repeat downs, one dialog)");
            }
            Focus();
            for (var i = 0; i < 20; i++)
            {
                Hook(true); Mapping(false); Routed(0x4A, true);
                Hook(false); Routed(0x4A, false); Mapping(true);
            }
            Frame(label + "-rapid-toggle"); // still qzx
            Focus();
            Mapping(false); Routed(0xA0, true); Mapping(true); Routed(0xA0, false);
            Mapping(false); Routed(0xA0, false); Focus(); Press(0x42);
            Frame(label + "-resumed"); // lowercase b proves Shift released; prior taps may select qzx
            Console.WriteLine($"LIVE {label}: direct, mapping, rapid toggle and held-Shift scenarios completed; inspect device frames for receipt.");
        }
        Focus(); ClearSearch();
        Frame("99-cleared");
    }

    private static void SaveLiveScreenshotDialog(nint hwnd, string path)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                AutomationElement? Find(ControlType type, string id) => root.FindFirst(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
                var edit = Find(ControlType.Edit, "1001");
                var save = Find(ControlType.Button, "1");
                if (edit is not null && save is not null)
                {
                    ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
                    ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return;
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Screenshot save dialog did not appear.");
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
