using System.IO;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestBluetoothWindowMenus(IPhoneMirror.App.Windows.NativePreviewWindow shell)
    {
        var menu = (System.Windows.Controls.ContextMenu)KeyboardField(shell, "_contextMenu");
        var forwarded = new List<PreviewPointerEventArgs>();
        var pointer = KeyboardField(shell, "_pointerInput");
        var foreground = KeyboardField(shell, "_inputForegroundWindow");
        var wasFixed = (bool)KeyboardField(shell, "_isFixed");
        if (!wasFixed) KeyboardCall(shell, "ToggleFixedWindow");
        SetKeyboardField(shell, "_inputForegroundWindow", (Func<nint>)(() => shell.Handle));
        SetKeyboardField(shell, "_pointerInput", (Action<PreviewPointerEventArgs>)(e => forwarded.Add(e)));
        var position = (nint)((150 << 16) | 75);
        void Message(int message) => KeyboardCall(shell, "WindowProcedure", shell.Handle, message, (nint)0, position, false);
        try
        {
            foreach (var absolute in new[] { false, true, false })
            {
                shell.BluetoothWindowMouseEnabled = absolute;
                forwarded.Clear();
                Message(0x0204); Message(0x0205); Message(0x007B);
                MappingAssert(menu.IsOpen == absolute &&
                    forwarded.Count(e => e.Button == 2) == (absolute ? 0 : 2),
                    "Independent right-click must open the menu only in absolute mode, and otherwise forward the Bluetooth button.");
                menu.IsOpen = false;
                forwarded.Clear();
                Message(0x0206); Message(0x0205); Message(0x007B);
                MappingAssert(menu.IsOpen == absolute && forwarded.Count(e => e.Button == 2) == (absolute ? 0 : 2),
                    "Double right-click bypassed the experimental-mode menu policy.");
                menu.IsOpen = false;
                forwarded.Clear();
                Message(0x0207); Message(0x0208);
                MappingAssert(forwarded.Count(e => e.Button == 4) == 2,
                    "Changing the menu policy lost the middle-button shortcut ingress.");
            }
            shell.BluetoothWindowMouseEnabled = true;
            forwarded.Clear();
            Message(0x0203);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            MappingAssert((bool)KeyboardField(shell, "_isFullScreen") && forwarded.All(e => e.Kind != PreviewPointerKind.ButtonDown),
                "Absolute Bluetooth double-click should enter full screen like wired/wireless, without forwarding a second device press.");
            Message(0x0203);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            MappingAssert(!(bool)KeyboardField(shell, "_isFullScreen"), "Double-click did not leave full screen.");
        }
        finally
        {
            shell.BluetoothWindowMouseEnabled = false;
            if (!wasFixed) KeyboardCall(shell, "ToggleFixedWindow");
            menu.IsOpen = false;
            SetKeyboardField(shell, "_pointerInput", pointer);
            SetKeyboardField(shell, "_inputForegroundWindow", foreground);
        }
        Console.WriteLine("PASS independent Bluetooth menus: absolute right-click menu, relative right-click forwarding, middle-button ingress and live mode changes.");
    }

    private static void TestBluetoothToolbarStop(MainWindow main, MainViewModel vm)
    {
        var button = (System.Windows.Controls.Button)main.FindName("BluetoothControlButton");
        KeyboardCall(vm, "NotifyBluetoothControlStateChanged");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        MappingAssert(button.IsEnabled && vm.BluetoothConnectionActionText == IPhoneMirror.App.Localization.LocalizationService.Get("StopBluetoothControl"),
            "Starting Bluetooth disabled the toolbar button or left a start/pause tooltip.");
        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while ((vm.IsBluetoothControlEnabled || vm.BluetoothControlTargetUdid is not null ||
            (bool)KeyboardField(vm, "_bluetoothControlStopping")) && DateTime.UtcNow < deadline)
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
        MappingAssert(!vm.IsBluetoothControlEnabled && vm.BluetoothControlTargetUdid is null &&
            vm.BluetoothConnectionActionText == IPhoneMirror.App.Localization.LocalizationService.Get("StartBluetoothControl"),
            "The toolbar click paused or retained Bluetooth instead of closing the connection.");
        Console.WriteLine("PASS Bluetooth toolbar: enabled after startup; click stops the connection and restores start tooltip.");
    }

    private static async Task TestAbsoluteDragTrackingAsync()
    {
        var position = (X: 0.0, Y: 0.0);
        byte held = 0;
        var pressedMotion = new List<byte[]>();
        (double X, double Y) releasedAt = default;
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (guard?.Invoke() == false) return Task.FromResult(true);
            if (id == 2)
            {
                MappingAssert(held == 0, "An absolute report interrupted an active device contact.");
                position = ((payload[1] | payload[2] << 8) * 375.0 / 32767,
                    (payload[3] | payload[4] << 8) * 812.0 / 32767);
            }
            if (id == 6)
            {
                if (held != 0 && payload[0] == 0) releasedAt = position;
                held = payload[0];
                if (held != 0 && payload.Skip(1).Any(b => b != 0))
                {
                    pressedMotion.Add(payload.ToArray());
                    var dx = (short)(payload[1] | payload[2] << 8);
                    var dy = (short)(payload[3] | payload[4] << 8);
                    var length = (int)Math.Floor(Math.Sqrt(dx * dx + dy * dy));
                    MappingAssert(dx * dx + dy * dy <= 48 * 48, "An oversized relative report can accelerate into a first-drag jump.");
                    // Independent measured travel, rather than the planner's
                    // prediction function, drives this device-side model.
                    double[] distances = [0, .20, .55, 1.10, 1.63, 2.30, 3.03, 3.80, 4.77,
                        5.8, 6.8, 8.0, 9.3, 10.7, 12.2, 13.7, 15.5];
                    // Quadratic interpolation through the measured 16/32/48
                    // points preserves the nonlinear shape between probes.
                    var measured = length < distances.Length ? distances[length] :
                        15.5 * (length - 32) * (length - 48) / 512.0 -
                        55 * (length - 16) * (length - 48) / 256.0 +
                        120 * (length - 16) * (length - 32) / 512.0;
                    var gain = length == 0 ? 0 : measured / length;
                    position = (position.X + dx * gain, position.Y + dy * gain);
                }
            }
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "absolute-drag-tracking");
        var generation = KeyboardField(service, "_mousePumpGeneration");
        async Task Send(double x, double y, byte buttons)
        {
            var px = (int)Math.Round(x * 32767);
            var py = (int)Math.Round(y * 32767);
            byte[] report = [buttons, (byte)px, (byte)(px >> 8), (byte)py, (byte)(py >> 8), 0];
            MappingAssert(await (Task<bool>)KeyboardCall(service, "SendHybridMouseAsync", report,
                false, generation, (Func<bool>)(() => true))!, "Absolute drag delivery failed.");
            MappingAssert(Math.Abs(position.X - x * 375) <= 3 && Math.Abs(position.Y - y * 812) <= 3 && held == buttons,
                $"A held absolute pointer did not track ({x}, {y}); actual=({position.X}, {position.Y}), buttons={held}.");
        }
        foreach (var buttons in new byte[] { 1, 2, 4, 3 })
        {
            await Send(.2, .3, buttons);
            foreach (var (x, y) in new[] { (.21, .3), (.6, .3), (.6, .7), (.35, .45), (.2, .3) })
                await Send(x, y, buttons);
            await Send(.55, .65, 0); // The coalesced final move precedes up.
        }
        SetKeyboardField(service, "_mousePumpRunning", true);
        await service.SendAbsoluteMouseAsync(.2, .3, 1, 0, "absolute-drag-tracking", () => true);
        for (var i = 0; i < 30; i++)
            await service.SendAbsoluteMouseAsync(.2 + i * .01, .3 + i * .01, 1, 0, "absolute-drag-tracking", () => true);
        await service.SendAbsoluteMouseAsync(.65, .7, 0, 0, "absolute-drag-tracking", () => true);
        await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
        // Independent curve measurements are rounded and differ slightly
        // from the fitted predictor. Allow 1.5% over this full-screen-length
        // path, while still checking the release before the exact re-anchor.
        var longDragTolerance = Math.Sqrt(Math.Pow(.45 * 375, 2) + Math.Pow(.4 * 812, 2)) * .015;
        MappingAssert(held == 0 && Math.Abs(releasedAt.X - .65 * 375) <= longDragTolerance && Math.Abs(releasedAt.Y - .7 * 812) <= longDragTolerance &&
            Math.Abs(position.X - .65 * 375) < .02 && Math.Abs(position.Y - .7 * 812) < .02,
            $"A rapid drag/up burst discarded its final position or released before completing movement: up={releasedAt}, anchor={position}, held={held}.");
        MappingAssert(pressedMotion.Count > 0, "The drag fixture did not exercise held movement.");
        var mini = BluetoothAbsoluteDragMotion.Geometry("iPhone13,1", 1080, 2340, 0);
        var rotated = BluetoothAbsoluteDragMotion.Geometry("iPhone13,1", 2340, 1080, 1);
        var pad = BluetoothAbsoluteDragMotion.Geometry("iPad13,1", 1640, 2360, 0);
        MappingAssert(mini.Width == 375 && Math.Abs(mini.Height - 812.5) < .01 && rotated == mini && pad == (820, 1180),
            "Drag geometry lost device points, source aspect ratio or preview rotation.");
        Console.WriteLine("PASS drag tracking: bounded accelerated travel, residuals, all buttons, final-up position and device geometry.");
    }

    private static async Task TestAbsoluteDragCancellationAsync()
    {
        foreach (var fail in new[] { false, true })
        {
            var allowed = true;
            var motionCount = 0;
            var tail = new List<(byte Id, byte[] Payload)>();
            await using var service = new BluetoothHidMouseService((id, payload, guard) =>
            {
                tail.Add((id, payload.ToArray()));
                if (id == 6 && payload[0] != 0 && payload.Skip(1).Any(b => b != 0))
                {
                    motionCount++;
                    allowed = false;
                    return Task.FromResult(!fail);
                }
                return Task.FromResult(true);
            });
            SetKeyboardField(service, "_useAbsoluteMouse", true);
            SetKeyboardField(service, "_targetDeviceUdid", "drag-cancel");
            var generation = KeyboardField(service, "_mousePumpGeneration");
            Task<bool> Send(byte[] payload, bool reset, Func<bool> guard) =>
                (Task<bool>)KeyboardCall(service, "SendHybridMouseAsync", payload, reset, generation, guard)!;
            await Send([1, 0, 32, 0, 32, 0], false, () => true);
            await Send([1, 0, 96, 0, 96, 0], false, () => allowed);
            MappingAssert(motionCount == 1, "A revoked drag continued emitting movement steps.");
            tail.Clear();
            MappingAssert(await Send([0, 0, 96, 0, 96, 0], true, () => true), "Drag cleanup failed.");
            MappingAssert(tail.First().Id == 6 && tail.First().Payload.All(b => b == 0) &&
                tail.Where(p => p.Id == 6).All(p => p.Payload.All(b => b == 0)),
                "Leaving after a failed/cancelled drag moved before releasing or replayed the uncertain step.");
        }
        Console.WriteLine("PASS bounded drag cancellation: foreground revocation and failed-step cleanup release without remaining motion.");
    }

    private static async Task TestAbsoluteDragKeyboardPriorityAsync()
    {
        var reports = new List<(byte Id, byte[] Payload)>();
        Task? key = null;
        BluetoothHidMouseService? service = null;
        await using var fixture = service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            reports.Add((id, payload.ToArray()));
            if (key is null && id == 6 && payload[0] == 1 && payload[1] != 0)
                key = service!.SendKeyboardAsync(0, [4]);
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "drag-keyboard-priority");
        SetKeyboardField(service, "_mousePumpRunning", true);
        await service.SendAbsoluteMouseAsync(.2, .3, 1, 0, "drag-keyboard-priority", () => true);
        await service.SendAbsoluteMouseAsync(.7, .7, 0, 0, "drag-keyboard-priority", () => true);
        await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
        var firstMove = reports.FindIndex(p => p.Id == 6 && p.Payload[0] == 1 && p.Payload[1] != 0);
        var keyboard = reports.FindIndex(p => p.Id == 1 && p.Payload.Contains((byte)4));
        var up = reports.FindLastIndex(p => p.Id == 6 && p.Payload[0] == 0);
        MappingAssert(key?.IsCompletedSuccessfully == true && keyboard == firstMove + 1 && up > keyboard + 1,
            "A multi-step drag starved a key edge or released before resuming its final movement.");
        var anchor = reports.Last();
        MappingAssert(anchor.Id == 2 && (anchor.Payload[1] | anchor.Payload[2] << 8) == (int)Math.Round(.7 * 32767),
            "Yielding to a keyboard event lost the drag's final absolute position.");
        Console.WriteLine("PASS drag/keyboard scheduling: key delivery interrupts movement and the final drag/up resumes in order.");
    }

    private static async Task TestAbsoluteDragReportBudgetAsync()
    {
        var steps = new List<(short X, short Y)>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (id == 6 && payload[0] == 1 && payload.Skip(1).Any(b => b != 0))
                steps.Add(((short)(payload[1] | payload[2] << 8), (short)(payload[3] | payload[4] << 8)));
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "drag-report-budget");
        var generation = KeyboardField(service, "_mousePumpGeneration");
        Task<bool> Send(byte[] report) => (Task<bool>)KeyboardCall(service, "SendHybridMouseAsync", report,
            false, generation, (Func<bool>)(() => true))!;
        await Send([1, 0, 32, 0, 32, 0]);
        await Send([1, 0, 96, 0, 32, 0]); // 750/4 = 187.5 device points.
        MappingAssert(steps.Count <= 5 && steps.Any(s => Math.Abs(s.X) > 16) && steps.All(s => s.Y == 0),
            "A large drag was replayed as many tiny reports, adding one BLE interval of latency per subdivision.");
        steps.Clear();
        await Send([1, 64, 96, 0, 32, 0]); // Less than one point: keep fine control.
        MappingAssert(steps.Count <= 2 && steps.All(s => Math.Abs(s.X) <= 3 && s.Y == 0),
            "The faster path expanded a tiny movement into a jump.");
        Console.WriteLine("PASS drag responsiveness: large travel uses <=5 reports; fine movement retains sub-point steps.");
    }

    private static void TestAbsoluteWindowGeometry(MainWindow window, DeviceViewModel device)
    {
        void Pointer(PreviewPointerKind kind, int width, int height, uint sourceWidth, uint sourceHeight, int rotation)
        {
            var rect = PreviewCoordinateMapper.ContentRect(width, height, sourceWidth, sourceHeight);
            KeyboardCall(window, "OnIndependentPointerInput", device.Udid,
                new PreviewPointerEventArgs(kind, (int)(rect.Left + rect.Width * .2), (int)(rect.Top + rect.Height * .3),
                    kind is PreviewPointerKind.ButtonDown or PreviewPointerKind.ButtonUp ? (byte)1 : (byte)0,
                    0, width, height, sourceWidth, sourceHeight, rotation, 0, 0) { WindowsButtons = 1 });
        }
        foreach (var change in new[] { "resize", "source", "rotation1", "rotation2", "rotation3", "fullscreen" })
        {
            Pointer(PreviewPointerKind.Reset, 400, 800, 400, 800, 0);
            Pointer(PreviewPointerKind.ButtonDown, 400, 800, 400, 800, 0);
            MappingAssert((byte)KeyboardField(window, "_controlButtons") == 1, "Geometry fixture did not begin a drag.");
            var generation = (long)KeyboardField(window, "_windowBluetoothPointerGeneration");
            var rotation = change.StartsWith("rotation") ? change[^1] - '0' : 0;
            var width = change == "resize" ? 600 : change == "fullscreen" ? 1920 : 400;
            var height = change == "fullscreen" ? 1080 : 800;
            var sourceWidth = change == "source" ? 800u : 400u;
            Pointer(PreviewPointerKind.Move, width, height, sourceWidth, 800, rotation);
            MappingAssert((byte)KeyboardField(window, "_controlButtons") == 0 &&
                (long)KeyboardField(window, "_windowBluetoothPointerGeneration") > generation,
                $"Changing {change} while held continued a drag with incompatible coordinates.");
            var position = ((double X, double Y))KeyboardField(window, "_absoluteWindowMousePosition");
            var expected = rotation switch { 1 => (.7, .2), 2 => (.8, .7), 3 => (.3, .8), _ => (.2, .3) };
            MappingAssert(Math.Abs(position.X - expected.Item1) < .01 && Math.Abs(position.Y - expected.Item2) < .01,
                "Absolute input used the old geometry or incorrect preview rotation.");
            Pointer(PreviewPointerKind.ButtonUp, width, height, sourceWidth, 800, rotation);
            MappingAssert((byte)KeyboardField(window, "_controlButtons") == 0,
                "The old Windows button-up restarted a gesture after geometry changed.");
        }
        Pointer(PreviewPointerKind.Reset, 400, 800, 400, 800, 0);
        Console.WriteLine("PASS absolute window geometry: resize, source changes, all preview rotations and full screen release an old drag.");
    }

    private static async Task TestAbsoluteMouseBacklogAsync()
    {
        foreach (var absolute in new[] { false, true })
        {
            var reports = new List<byte[]>();
            await using var service = new BluetoothHidMouseService((id, payload, guard) =>
            {
                if (id == (absolute ? 6 : 2) && guard?.Invoke() != false) reports.Add(payload.ToArray());
                return Task.FromResult(true);
            });
            SetKeyboardField(service, "_useAbsoluteMouse", absolute);
            SetKeyboardField(service, "_targetDeviceUdid", "backlog");
            Task Point(double x, byte buttons) => absolute
                ? service.SendAbsoluteMouseAsync(x, .5, buttons, 0, "backlog", () => true)
                : service.SendMouseAsync((int)(x * 100), 0, buttons, 0, "backlog", () => true, true);
            Task Pump() => (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
            SetKeyboardField(service, "_mousePumpRunning", true);
            await Point(.2, 1);
            await Pump();
            reports.Clear();
            SetKeyboardField(service, "_mousePumpRunning", true);
            await Point(.2, 0);
            for (var i = 0; i < 32; i++)
            {
                await Point(.8, 1);
                await Point(.8, 0);
            }
            await Point(.9, 1);
            await Point(.9, 0);
            await Pump();
            MappingAssert(reports.Count > 0 && reports[0].All(b => b == 0),
                $"Queue overflow ({absolute}) lost the release of an already held button.");
            MappingAssert(reports.Last()[0] == 0 && (!absolute || reports.Where(p => p[0] != 0).All(p => p.Skip(1).All(b => b == 0))),
                "Queue overflow turned a later independent click into drag movement or left a button held.");
        }
        Console.WriteLine("PASS mouse queue overflow: release precedes new gestures in relative and absolute mode.");
    }

    private static async Task TestAbsoluteMouseEdgeScrollAsync()
    {
        var reports = new List<(byte Id, byte[] Payload)>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (guard?.Invoke() != false) reports.Add((id, payload.ToArray()));
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "edge-scroll");
        foreach (var y in new[] { 0, .01, .05, .95, .99, 1 })
        foreach (var wheel in new[] { -127, -1, 1, 127 })
        {
            reports.Clear();
            SetKeyboardField(service, "_mousePumpRunning", true);
            await service.SendAbsoluteMouseAsync(.3, y, 0, wheel, "edge-scroll", () => true);
            await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
            var press = reports.FindIndex(p => p.Id == 6 && p.Payload[0] == 1);
            var anchor = reports.First(p => p.Id == 2).Payload;
            var requestedY = (int)Math.Round(y * 32767);
            MappingAssert((anchor[3] | anchor[4] << 8) == requestedY,
                "An edge scroll moved its press onto another surface.");
            var motion = reports.Where(p => p.Id == 6 && p.Payload[0] == 1)
                .Sum(p => (short)(p.Payload[3] | p.Payload[4] << 8));
            MappingAssert(Math.Abs(motion) <= 256 && Math.Sign(motion) == (press < 0 ? 0 : -Math.Sign(wheel)) &&
                requestedY / 32767.0 + motion / 1024.0 is >= 0 and <= 1,
                "Edge scrolling reversed direction or exceeded available screen travel.");
            MappingAssert(press < 0 || (Math.Abs(motion) >= 16 && reports.Last(p => p.Id == 6).Payload[0] == 0),
                "A scroll with no available travel generated a tap or retained a button.");
            MappingAssert((reports.Last().Payload[3] | reports.Last().Payload[4] << 8) == requestedY,
                "Edge scrolling failed to restore its original anchor.");
        }
        Console.WriteLine("PASS edge scrolling: both directions, corners and large wheel input preserve the requested surface and release.");
    }

    private static async Task TestAbsoluteScrollFailureMatrixAsync()
    {
        // First collect the successful sequence, then fail every operation in
        // turn, including down, each movement, up and anchor restoration.
        var baselineCount = 0;
        for (var failure = 0; failure <= baselineCount; failure++)
        {
            var attempts = new List<(byte Id, byte[] Payload)>();
            var armed = false;
            await using var service = new BluetoothHidMouseService((id, payload, guard) =>
            {
                if (!armed) return Task.FromResult(true);
                attempts.Add((id, payload.ToArray()));
                return Task.FromResult(attempts.Count != failure);
            });
            SetKeyboardField(service, "_useAbsoluteMouse", true);
            SetKeyboardField(service, "_targetDeviceUdid", "scroll-failure");
            var generation = KeyboardField(service, "_mousePumpGeneration");
            Task<bool> Send(byte[] payload) => (Task<bool>)KeyboardCall(service, "SendHybridMouseAsync",
                payload, false, generation, (Func<bool>)(() => true))!;
            await Send([0, 0, 64, 0, 64, 0]);
            armed = true;
            var succeeded = await Send([0, 0, 64, 0, 64, 3]);
            if (failure == 0)
            {
                MappingAssert(succeeded, "The successful scroll fixture failed.");
                baselineCount = attempts.Count;
                continue;
            }
            MappingAssert(!succeeded && attempts.Count == failure, "A scroll delivery failure was ignored.");
            // Production retains only button state, dropping failed wheel
            // history. A newer position must not drag the synthetic press.
            attempts.Clear();
            armed = false;
            var cleanup = new List<(byte Id, byte[] Payload)>();
            SetKeyboardField(service, "_sendInputReport", (Func<byte, byte[], Func<bool>?, Task<bool>>)((id, payload, guard) =>
            {
                cleanup.Add((id, payload.ToArray()));
                return Task.FromResult(true);
            }));
            MappingAssert(await Send([0, 0, 96, 0, 96, 0]), "A failed scroll could not be cleaned up.");
            MappingAssert(cleanup.Where(p => p.Id == 6).All(p => p.Payload.All(b => b == 0)),
                $"Scroll failure at operation {failure} replayed a press or moved while still held.");
            MappingAssert(await Send([1, 0, 96, 0, 96, 0]) && await Send([0, 0, 96, 0, 96, 0]),
                "A failed scroll prevented the next independent click.");
            MappingAssert(await service.PauseInputAsync(), "A failed scroll made pause cleanup fail.");
        }
        Console.WriteLine($"PASS synthetic scroll failure matrix: all {baselineCount} delivery stages clean up without replay.");
    }

    private static async Task TestAbsoluteScrollThenClickAsync()
    {
        var reports = new List<(byte Id, byte[] Payload)>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            reports.Add((id, payload.ToArray()));
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "scroll-click");
        foreach (var direction in new[] { -1, 1 })
        {
            reports.Clear();
            SetKeyboardField(service, "_mousePumpRunning", true);
            await service.SendAbsoluteMouseAsync(.5, .8, 0, direction, "scroll-click", () => true);
            foreach (var x in new[] { .2, .2, .8 })
            {
                await service.SendAbsoluteMouseAsync(x, .3, 1, 0, "scroll-click", () => true);
                await service.SendAbsoluteMouseAsync(x, .3, 0, 0, "scroll-click", () => true);
            }
            await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
            var presses = reports.Select((p, i) => (p, i)).Where(v => v.p.Id == 6 && v.p.Payload[0] == 1 &&
                v.p.Payload.Skip(1).All(b => b == 0)).ToArray();
            MappingAssert(presses.Length == 4, "Scroll/click fixture lost a press.");
            foreach (var click in presses.Skip(1))
            {
                var beforePress = reports.Take(click.i).ToArray();
                var activation = Array.FindLastIndex(beforePress, p => p.Id == 6 && p.Payload[0] == 0 && p.Payload[1] == 1);
                var anchor = Array.FindLastIndex(beforePress, p => p.Id == 2);
                var previousPress = Array.FindLastIndex(beforePress, p => p.Id == 6 && p.Payload[0] == 1);
                MappingAssert(activation > previousPress && anchor > activation,
                    "A new press after scrolling must select the relative collection before anchoring its new location.");
            }
        }
        Console.WriteLine("PASS scroll-to-click handoff: both wheel directions reactivate and position each independent click.");
    }

    private static void TestAbsoluteMouseSettings(App app, MainViewModel vm)
    {
        var root = Path.Combine(Path.GetTempPath(), "iphoneMirror-absolute-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = KeyboardField(app, "_settingsStore");
        var snapshot = app.UpdateSettings.Clone();
        var preview = app.IsUiPreviewMode;
        try
        {
            var path = Path.Combine(root, "settings.json");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(path));
            app.IsUiPreviewMode = false;
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            SetKeyboardField(vm, "_bluetoothControlStarting", false);
            app.UpdateSettings.BluetoothWindowMouseEnabled = false;
            vm.BluetoothWindowMouseEnabled = true;
            MappingAssert(new UpdateSettingsStore(path).Load().BluetoothWindowMouseEnabled,
                "Absolute positioning opt-in did not survive a disk save and reload.");
            foreach (var state in new[] { "_bluetoothControlEnabled", "_bluetoothControlStarting" })
            {
                SetKeyboardField(vm, state, true);
                vm.BluetoothWindowMouseEnabled = false;
                MappingAssert(!vm.CanConfigureBluetoothWindowMouse && vm.BluetoothWindowMouseEnabled,
                    "An active or starting connection admitted an incompatible report-map change.");
                SetKeyboardField(vm, state, false);
            }
            var blocker = Path.Combine(root, "file-as-directory");
            File.WriteAllText(blocker, "blocked");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocker, "settings.json")));
            vm.BluetoothWindowMouseEnabled = false;
            MappingAssert(vm.BluetoothWindowMouseEnabled, "A failed save did not roll back the experimental opt-in.");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(path));
            vm.BluetoothWindowMouseEnabled = false;
            MappingAssert(!new UpdateSettingsStore(path).Load().BluetoothWindowMouseEnabled,
                "Disabling absolute positioning did not survive restart.");
            File.WriteAllText(path, "{}");
            MappingAssert(!new UpdateSettingsStore(path).Load().BluetoothWindowMouseEnabled,
                "Existing settings without the experimental field enabled it during migration.");

            app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = BluetoothHidProtocol.ReportMapVersion;
            vm.ControlStatus.Begin(ControlStatusMode.Bluetooth, "report-map regression");
            var confirmation = (Task<bool>)KeyboardCall(vm, "AcknowledgeBluetoothHidReportMapChangeAsync",
                BluetoothHidProtocol.AbsoluteMouseReportMapVersion)!;
            MappingAssert(!confirmation.IsCompleted && vm.ControlStatus.Current?.Prompt is not null,
                "First use skipped the cached-pairing repair prompt.");
            vm.ControlStatus.ResolvePrompt(new(ControlPromptAction.Cancel));
            AwaitMapping(confirmation);
            MappingAssert(!confirmation.Result && app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion == BluetoothHidProtocol.ReportMapVersion,
                "Cancelling the descriptor-change prompt accepted an incompatible cached pairing.");
            foreach (var version in new[] { BluetoothHidProtocol.AbsoluteMouseReportMapVersion, BluetoothHidProtocol.ReportMapVersion })
            {
                confirmation = (Task<bool>)KeyboardCall(vm, "AcknowledgeBluetoothHidReportMapChangeAsync", version)!;
                MappingAssert(!confirmation.IsCompleted, "A descriptor change reused the previous mode's acknowledgment.");
                vm.ControlStatus.ResolvePrompt(new(ControlPromptAction.Primary));
                AwaitMapping(confirmation);
                MappingAssert(confirmation.Result && new UpdateSettingsStore(path).Load().BluetoothHidReportMapAcknowledgedVersion == version,
                    "Descriptor-change acknowledgment did not survive a restart.");
                confirmation = (Task<bool>)KeyboardCall(vm, "AcknowledgeBluetoothHidReportMapChangeAsync", version)!;
                MappingAssert(confirmation.IsCompletedSuccessfully && confirmation.Result,
                    "An unchanged acknowledged descriptor prompted again.");
            }
        }
        finally
        {
            app.IsUiPreviewMode = preview;
            app.RestoreUpdateSettings(snapshot);
            SetKeyboardField(app, "_settingsStore", store);
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS experimental setting: disk roundtrip, migration default, active/start guards, failed-save rollback and both report-map prompts.");
    }

    private static async Task TestAbsoluteMouseTimeoutAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        Func<bool>? savedGuard = null;
        byte reportId = 0;
        await using var service = new BluetoothHidMouseService(async (id, _, guard) =>
        {
            reportId = id;
            savedGuard = guard;
            entered.TrySetResult();
            await complete.Task;
            if (guard?.Invoke() != false) Interlocked.Increment(ref admitted);
            return true;
        });
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", "absolute-timeout");
        var pause = service.PauseInputAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        MappingAssert(!await pause.WaitAsync(TimeSpan.FromSeconds(3)) && reportId == 6 && savedGuard?.Invoke() == false,
            "An absolute-mode release timeout succeeded or left late report-6 delivery authorized.");
        complete.SetResult();
        await Task.Delay(30);
        MappingAssert(admitted == 0, "A timed-out report-6 release reached the device after its deadline.");
        Console.WriteLine("PASS absolute release timeout: report 6 is required and late delivery stays revoked.");
    }
}
