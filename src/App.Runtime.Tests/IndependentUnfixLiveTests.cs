using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Explicit hardware test: production discovery, capture, window menu and
    // wireless startup; native messages enter the real HWND and callbacks.
    private static int RunIndependentUnfixLive(string output, bool allowUsbFallback = false)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { passed = false, status = "running", startedUtc = DateTimeOffset.UtcNow }));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = main;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var manager = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        bool Wait(Func<bool> done, int seconds = 30)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return done();
        }
        void Complete(Task task, int seconds = 30)
        {
            MappingAssert(Wait(() => task.IsCompleted, seconds), "Live operation timed out.");
            task.GetAwaiter().GetResult();
        }
        var results = new List<string>();
        void Pass(string text)
        {
            results.Add(text);
            Console.WriteLine("PASS " + text);
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(results,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        main.Show();
        try
        {
            MappingAssert(Wait(() => vm.Devices.Any(d => !d.IsWireless && !d.IsMediaCast)), "No connected USB phone for capture.");
            var candidates = vm.Devices.Where(d => !d.IsWireless && !d.IsMediaCast && vm.ResolveAppleUdid(d.Udid) != null).ToArray();
            MappingAssert(candidates.Length == 1, "Hardware test requires one bound USB phone.");
            var device = candidates.Single();
            vm.SelectedDevice = device;
            var opening = manager.ShowAsync(device);
            Complete(opening, 60);
            MappingAssert(opening.Result.Success, opening.Result.Message);
            var windows = (IDictionary)KeyboardField(manager, "_windows");
            var preview = (NativePreviewWindow)windows[device.Udid]!;
            var hwnd = preview.Handle;
            var pointers = new List<PreviewPointerKind>();
            manager.PointerInput += (_, input) => pointers.Add(input.Kind);
            KeyboardCall(preview, "ToggleFixedWindow");
            MappingAssert((bool)KeyboardField(preview, "_isFixed"), "Initial fix failed.");
            SetKeyboardField(vm, "_wirelessControlPrerequisiteAcknowledged", true);
            SetKeyboardField(vm, "_wiredControlPrerequisiteAcknowledged", true);
            if (allowUsbFallback)
            {
                // Supplemental mode may provision through the supported USB
                // flow. Strict acceptance uses only the wireless menu route.
                Complete(vm.StartUsbControlAsync(device.Udid), 180);
                MappingAssert(vm.IsUsbControlTarget(device.Udid), "USB initialization did not become ready.");
                Complete(vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid), 25);
            }
            var status = vm.GetControlStatus(ControlStatusMode.Wireless, device.Udid);
            status.StatusChanged += (_, s) => Console.WriteLine($"CONTROL {s.Stage}: {s.Description}");
            // Exactly the same handler invoked by the independent-window menu.
            KeyboardCall(main, "OnIndependentWirelessControlRequested", device.Udid, hwnd);
            MappingAssert(Wait(() => vm.IsWirelessControlTarget(device.Udid) || status.Current?.Stage == ControlStage.Failed, 180),
                "Wireless startup timeout.");
            var wireless = vm.IsWirelessControlTarget(device.Udid);
            if (!wireless)
            {
                Console.WriteLine($"WIRELESS BLOCKED: {status.Current?.Description}; {status.Current?.Error}");
                File.WriteAllText(Path.Combine(output, "wireless-blocked.txt"), status.Current?.Error);
                MappingAssert(allowUsbFallback,
                    "Wireless acceptance requires a real wireless connection. Use --independent-unfix-usb-supplement only for the shared-path supplement.");
                Complete(vm.StartUsbControlAsync(device.Udid), 180);
                MappingAssert(vm.IsUsbControlTarget(device.Udid), "Wired fallback did not become ready.");
                manager.PrepareUsbControlWindow(device.Udid);
                Console.WriteLine("Supplemental test uses real USB control on the shared touch/window path.");
            }
            AdvanceDispatcher(TimeSpan.FromSeconds(1));
            foreach (var notice in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) notice.Close();
            UnfixKey(0x12, 0, 0, 0);
            UnfixKey(0x12, 0, 2, 0);
            preview.Activate();
            MappingAssert(Wait(() => UnfixForeground() == hwnd, 5), "Independent HWND not foreground.");
            var session = (DeviceControlSession)KeyboardCall(vm, "FindControl", device.Udid)!;
            var bridge = wireless ? session.WirelessBridge! : session.WiredBridge!;
            var acknowledgements = 0;
            bridge.StatusChanged += (_, e) => { if (e.Code == "input_verified") Interlocked.Increment(ref acknowledgements); };
            Complete(bridge.SendKeyboardAsync([], releaseAll: true));
            MappingAssert(Wait(() => Volatile.Read(ref acknowledgements) > 0, 10), "Phone did not acknowledge real HID input.");
            Pass($"real {(wireless ? "wireless" : "USB")} bridge ready and phone acknowledged HID report");
            MappingAssert(Wait(() => vm.SourceVideoWidth > 0, 20), "Capture has no real frame.");
            vm.CaptureScreenshot(Path.Combine(output, "phone-before.png"));
            Console.WriteLine("READY: awaiting local run.json containing normalized x/y for safe touch probe.");
            var command = Path.Combine(output, "run.json");
            MappingAssert(Wait(() => File.Exists(command), 180), "No safe probe coordinates supplied.");
            using var doc = JsonDocument.Parse(File.ReadAllText(command));
            var nx = doc.RootElement.GetProperty("x").GetDouble();
            var ny = doc.RootElement.GetProperty("y").GetDouble();
            MappingAssert(double.IsFinite(nx) && double.IsFinite(ny) && nx is > .05 and < .95 && ny is > .25 and < .90,
                "Probe coordinates must be finite and inside the inspected phone content.");
            UnfixKey(0x12, 0, 0, 0); UnfixKey(0x12, 0, 2, 0);
            preview.Activate();
            MappingAssert(Wait(() => UnfixForeground() == hwnd, 5), "Probe HWND lost foreground while waiting for coordinates.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            Console.WriteLine($"INPUT admission: fixed={KeyboardField(preview, "_isFixed")} enabled={preview.AllowsControlInput} active={KeyboardCall(preview, "get_IsPointerInputActive")} main={(bool)KeyboardCall(main, "CanForwardControlPointer", device.Udid, hwnd)!}");
            UnfixGetClientRect(hwnd, out var client);
            var x = (int)(client.Right * nx); var y = (int)(client.Bottom * ny);
            nint Point(int px, int py) => (nint)((py << 16) | (px & 0xffff));
            void Input(int msg, int px, int py) => MappingSendMessage(hwnd, msg, msg == 0x201 ? 1 : 0, Point(px, py));
            void Gesture()
            {
                Input(0x201, x, y);
                for (var i = 1; i <= 8; i++)
                { AdvanceDispatcher(TimeSpan.FromMilliseconds(40)); Input(0x200, x, y - i * 8); }
                Input(0x202, x, y - 64);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
            }
            var touchBefore = acknowledgements;
            Gesture();
            // The bridge emits input_verified only for the first input or an
            // empty keyboard report. Use the latter as a FIFO/ping fence after
            // touch rather than incorrectly expecting an ACK for every move.
            Complete(bridge.SendKeyboardAsync([], releaseAll: true));
            MappingAssert(pointers.Contains(PreviewPointerKind.ButtonDown) && Wait(() => acknowledgements > touchBefore, 10),
                $"Fixed window did not deliver a real acknowledged phone gesture: events={string.Join(',', pointers)}, ack={acknowledgements}, previous={touchBefore}.");
            vm.CaptureScreenshot(Path.Combine(output, "phone-fixed-gesture.png"));
            Pass($"fixed window forwards native gesture through production {(wireless ? "wireless" : "USB")} transport");
            // Keep a real press pending and verify unfix releases phone state.
            Input(0x201, x, y);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            var staleGuard = (Func<bool>)KeyboardCall(main, "CapturePointerSendGuard", hwnd)!;
            KeyboardCall(preview, "ToggleFixedWindow");
            Complete((Task)KeyboardField(main, "_keyboardHandoff"));
            MappingAssert(!preview.AllowsControlInput && !manager.AllowsControlInput(hwnd) && !staleGuard(), "Unfix failed to revoke input.");
            MappingAssert((byte)KeyboardField(preview, "_capturedMouseButtons") == 0 && UnfixCapture() != hwnd,
                "Unfix retained native capture.");
            MappingAssert(!(bool)KeyboardCall(main, "CanForwardControlKeyboard", device.Udid, hwnd)! &&
                !(bool)KeyboardCall(main, "CanForwardControlPointer", device.Udid, hwnd)!, "Global hook still admits input after unfix.");
            MappingAssert(!(bool)KeyboardCall(main, "MappingFocusAllows")!, "Key mappings still admitted after unfix.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
            Pass("unfix releases held touch/capture and revokes keyboard, mappings, pointer and queued guards");
            UnfixGetWindowRect(hwnd, out var before);
            var cx = (before.Left + before.Right) / 2; var cy = (before.Top + before.Bottom) / 2;
            MappingAssert(MappingSendMessage(hwnd, 0x84, 0, Point(cx, cy)) == 2, "Unfix did not restore caption hit-test.");
            pointers.Clear();
            Gesture();
            MappingAssert(pointers.Count == 0, "Unfixed HWND still forwarded mouse messages.");
            // Real OS move/resize loop, driven by script mouse events.
            void Drag(int sx, int sy, int dx, int dy, int hit = 0)
            {
                MappingSetCursorPos(sx, sy);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                var drag = Task.Run(() =>
                {
                    if (hit == 0) UnfixMouse(2, 0, 0, 0, 0);
                    else UnfixPostMessage(hwnd, 0xA1, (nint)hit, Point(sx, sy));
                    Thread.Sleep(150);
                    MappingSetCursorPos(sx + dx, sy + dy);
                    Thread.Sleep(200);
                    UnfixMouse(4, 0, 0, 0, 0);
                });
                AdvanceDispatcher(TimeSpan.FromSeconds(1));
                Complete(drag, 5);
            }
            Drag(cx, cy, 45, 30);
            UnfixGetWindowRect(hwnd, out var moved);
            Console.WriteLine($"WINDOW before={before.Left},{before.Top},{before.Right},{before.Bottom} moved={moved.Left},{moved.Top},{moved.Right},{moved.Bottom}");
            MappingAssert(moved.Left != before.Left || moved.Top != before.Top, "Real Windows drag did not move unfixed HWND.");
            Drag(moved.Right - 2, (moved.Top + moved.Bottom) / 2, 35, 0);
            UnfixGetWindowRect(hwnd, out var resized);
            if (resized.Right - resized.Left == moved.Right - moved.Left)
            {
                var edge = MappingSendMessage(hwnd, 0x84, 0, Point(moved.Right - 2, (moved.Top + moved.Bottom) / 2));
                MappingAssert(edge == 11, $"Unfixed right edge did not return HTRIGHT: {edge}");
                // Target the real native nonclient sizing loop explicitly if
                // another foreground window intercepted the injected click.
                Drag(moved.Right - 2, (moved.Top + moved.Bottom) / 2, 35, 0, 11);
                UnfixGetWindowRect(hwnd, out resized);
            }
            Console.WriteLine($"WINDOW resized={resized.Left},{resized.Top},{resized.Right},{resized.Bottom}");
            File.WriteAllText(Path.Combine(output, "window-geometry.json"), JsonSerializer.Serialize(new { before, moved, resized },
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            MappingAssert(resized.Right - resized.Left != moved.Right - moved.Left, "Real Windows resize did not resize unfixed HWND.");
            MappingAssert(pointers.Count == 0 && bridge.IsReady, "Moving/resizing forwarded phone input or disconnected the bridge.");
            Pass("real Windows drag and resize succeed without phone pointer forwarding");
            preview.Activate();
            MappingAssert(Wait(() => UnfixForeground() == hwnd, 5), "Failed to reactivate preview.");
            KeyboardCall(preview, "ToggleFixedWindow");
            MappingAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "Refix did not restore mapping admission.");
            UnfixGetClientRect(hwnd, out client);
            x = (int)(client.Right * nx); y = (int)(client.Bottom * ny);
            var restored = acknowledgements;
            Gesture();
            Complete(bridge.SendKeyboardAsync([], releaseAll: true));
            MappingAssert(Wait(() => acknowledgements > restored, 10), "Refixed touch FIFO fence was not acknowledged.");
            MappingAssert(preview.AllowsControlInput && acknowledgements > restored, "Refix did not restore acknowledged phone input.");
            vm.CaptureScreenshot(Path.Combine(output, "phone-refixed.png"));
            Pass($"refix restores real {(wireless ? "wireless" : "USB")} touch with acknowledged FIFO fence without reconnecting");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, transport = wireless ? "wireless" : "USB", wirelessPassed = wireless, results, before, moved, resized }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failed.txt"), error.ToString());
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { passed = false, status = "failed", error = error.Message, results }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            Complete(vm.CancelReverseControlAsync(ControlStatusMode.Wireless), 25);
            Complete(vm.CancelReverseControlAsync(ControlStatusMode.Usb), 25);
            manager.Dispose();
            Complete(vm.ShutdownAsync(), (int)vm.NormalShutdownTimeout.TotalSeconds + 5);
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
            Console.WriteLine("CLEANUP: control and capture stopped.");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnfixRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool UnfixGetWindowRect(nint hwnd, out UnfixRect rect);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool UnfixGetClientRect(nint hwnd, out UnfixRect rect);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint UnfixForeground();
    [DllImport("user32.dll", EntryPoint = "GetCapture")] private static extern nint UnfixCapture();
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void UnfixMouse(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void UnfixKey(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool UnfixPostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
