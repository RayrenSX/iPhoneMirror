using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static JsonElement[] TouchPoints(MemoryStream packets) => MappingFrames(packets)
        .Where(f => f.TryGetProperty("points", out _))
        .SelectMany(f => f.GetProperty("points").EnumerateArray()).ToArray();

    private static async Task TestFivePointMappingAsync(MainViewModel vm, MemoryStream packets, string context)
    {
        packets.SetLength(0);
        using var executor = new KeyboardMappingExecutor();
        var mappings = Enumerable.Range(0, 6).Select(i => MappingEntry(MappedTouchAction.LongPress) with
            { X = .1 + i * .1, DurationMs = 2000 }).ToArray();
        MappedTouchRoute Route() => vm.CaptureMappingRoute(() => true, (x, y) => (x, y))!;
        var running = mappings.Take(5).Select(m => executor.ExecuteAsync(m, Route())).ToArray();
        var downs = TouchPoints(packets);
        MappingAssert(downs.Length == 5 && downs.All(p => p.GetProperty("action").GetString() == "down") &&
            downs.Select(p => p.GetProperty("pointerId").GetInt32()).Distinct().Count() == 5,
            $"{context}: five mappings must press distinct contacts concurrently.");
        MappingAssert(!await executor.ExecuteAsync(mappings[5], Route()), $"{context}: sixth mapping exceeded capacity.");
        MappingAssert(!await executor.ExecuteAsync(mappings[0], Route()), $"{context}: repeated mapping overlapped itself.");
        var completion = executor.Completion;
        MappingAssert(!completion.IsCompleted, "Completion did not include active gestures.");
        executor.Cancel();
        foreach (var task in running)
        {
            try { await task; throw new Exception("Cancelled gesture succeeded."); }
            catch (OperationCanceledException) { }
        }
        await completion;
        MappingAssert(!executor.IsBusy && TouchPoints(packets).GroupBy(p => p.GetProperty("pointerId").GetInt32())
            .All(g => g.Select(p => p.GetProperty("action").GetString()).SequenceEqual(new[] { "down", "up" })),
            $"{context}: cancellation must release every contact independently.");

        // The shared writer enforces five points even across separate input producers.
        packets.SetLength(0);
        var routes = Enumerable.Range(0, 6).Select(_ => Route()).ToArray();
        for (var i = 0; i < 6; ++i) await routes[i].SendAsync("down", .1 * i, .4, default);
        MappingAssert(TouchPoints(packets).Length == 5, $"{context}: mixed producers bypassed the touch budget.");
        await routes[0].SendAsync("up", 0, .4, default);
        var beforeMove = packets.Length;
        await routes[5].SendAsync("move", .7, .8, default);
        MappingAssert(packets.Length == beforeMove, "Rejected sixth finger was resurrected by a move.");
        await routes[5].SendAsync("up", .7, .8, default);
        await routes[5].SendAsync("down", .7, .8, default);
        for (var i = 1; i < 6; ++i) await routes[i].SendAsync("up", .7, .8, default);
        MappingAssert(TouchPoints(packets).Count(p => p.GetProperty("action").GetString() == "down") == 6,
            "Freed slot was not available to a new down.");

        // A mouse release must never lift a concurrent keyboard contact.
        packets.SetLength(0);
        await vm.SendUsbTouchAsync("down", .2, .3, vm.SelectedDevice!.Udid);
        await routes[0].SendAsync("down", .6, .7, default);
        await vm.SendUsbTouchAsync("up", .2, .3, vm.SelectedDevice.Udid);
        await routes[0].SendAsync("move", .7, .7, default);
        await routes[0].SendAsync("up", .7, .7, default);
        MappingAssert(TouchPoints(packets).Select(p => p.GetProperty("action").GetString())
            .SequenceEqual(new[] { "down", "down", "up", "move", "up" }), "Mouse interfered with mapping contact.");
        Console.WriteLine($"PASS {context}: five concurrent mappings, capacity, slot reuse, cancellation and mixed mouse input.");
        await TestFivePointHeldKeysAsync(vm, packets, context);
    }

    private static async Task TestFivePointHeldKeysAsync(MainViewModel vm, MemoryStream packets, string context)
    {
        packets.SetLength(0);
        using var executor = new KeyboardMappingExecutor();
        var keys = new KeyboardMappingKeyState();
        var holds = new KeyboardMappingHoldState();
        var mappings = Enumerable.Range(0, 6).Select(i => MappingEntry(MappedTouchAction.HoldUntilRelease) with
        {
            Key = new MappedKey(0x41 + i, 0x20 + i, false), X = .1 + i * .1, Y = .4,
        }).ToArray();
        var releases = new CancellationTokenSource[6];
        var running = new Task<bool>[6];
        async Task Release(int index)
        {
            var key = mappings[index].Key!;
            holds.Process(key, false);
            var result = keys.Process(key, false, false, true, false, true, mappings);
            MappingAssert(result.Suppress && result.Mapping is null, "Held key up was not paired or retriggered.");
            try { await running[index]; throw new Exception("Released hold completed without cancellation."); }
            catch (OperationCanceledException) { }
            holds.Complete(mappings[index].Id, releases[index]);
        }
        for (var i = 0; i < mappings.Length; ++i)
        {
            var mapping = mappings[i];
            var down = keys.Process(mapping.Key!, true, false, true, false, true, mappings);
            MappingAssert(down.Mapping == mapping, "Another held ordinary key blocked this mapping.");
            releases[i] = holds.Begin(mapping);
            running[i] = executor.ExecuteAsync(down.Mapping!, vm.CaptureMappingRoute(() => true, (x, y) => (x, y))!,
                releases[i].Token);
            MappingAssert(keys.Process(mapping.Key!, true, false, true, false, true, mappings).Mapping is null,
                "Held key autorepeat started another contact.");
        }
        MappingAssert(!await running[5], "Sixth held key displaced an existing contact.");
        holds.Process(mappings[5].Key!, false);
        keys.Process(mappings[5].Key!, false, false, true, false, true, mappings);
        holds.Complete(mappings[5].Id, releases[5]);
        await Task.Delay(100);
        var downs = TouchPoints(packets);
        MappingAssert(downs.Length == 5 && running.Take(5).All(t => !t.IsCompleted),
            "Five physical key lifetimes did not remain active together.");
        var ids = downs.Select(p => p.GetProperty("pointerId").GetInt32()).ToArray();
        MappingAssert(ids.Distinct().Count() == 5, "Held keys shared a contact ID.");
        await Release(2);
        var afterRelease = TouchPoints(packets);
        MappingAssert(afterRelease.Length == 6 && afterRelease[^1].GetProperty("action").GetString() == "up" &&
            afterRelease[^1].GetProperty("pointerId").GetInt32() == ids[2] &&
            new[] { 0, 1, 3, 4 }.All(i => !running[i].IsCompleted),
            "Releasing one key lifted another key's contact.");
        foreach (var index in new[] { 4, 0, 3, 1 }) await Release(index);
        MappingAssert(!executor.IsBusy && TouchPoints(packets).GroupBy(p => p.GetProperty("pointerId").GetInt32())
            .All(g => g.Select(p => p.GetProperty("action").GetString()).SequenceEqual(new[] { "down", "up" })),
            "Multi-key hold left stuck or duplicate contacts.");
        Console.WriteLine($"PASS {context}: five distinct held keys, repeat/capacity rejection and independent key-up packets.");
    }

    private static void TestFivePointPreview(MainWindow window, DeviceViewModel device, MemoryStream packets,
        nint hwnd, string context, bool independent = false)
    {
        if (!independent) MappingAssert(IsTouchWindow(hwnd, out _), "Main preview did not register native touch input.");
        var viewModel = KeyboardField(window, "_viewModel");
        var control = (DeviceControlSession)KeyboardCall(viewModel, "GetOrCreateControl", device.Udid)!;
        var previousAppleUdid = control.AppleUdid;
        control.AppleUdid = device.Udid;
        var preview = window.FindName("MainPreviewHost");
        void Dispatch(PreviewPointerEventArgs input)
        {
            if (independent) KeyboardCall(window, "OnIndependentPointerInput", device.Udid, input);
            else KeyboardCall(window, "OnControlPointerInput", preview, input);
        }
        void Send(PreviewPointerKind kind, uint id, int x = 100, int y = 200) => Dispatch(
            new PreviewPointerEventArgs(kind, x, y, 0, 0, 200, 400, 390, 780, 0, id, hwnd));
        void Reset() => Dispatch(new PreviewPointerEventArgs(PreviewPointerKind.Reset, 0, 0, 0, 0));
        try
        {
            packets.SetLength(0);
            for (uint id = 1; id <= 6; ++id) Send(PreviewPointerKind.TouchDown, id, 20 + (int)id * 20);
            var downs = TouchPoints(packets);
            MappingAssert(downs.Length == 5 && downs.Select(p => p.GetProperty("pointerId").GetInt32()).Distinct().Count() == 5,
                $"{context}: preview did not keep exactly five independent fingers.");
            for (uint id = 1; id <= 5; ++id) Send(PreviewPointerKind.TouchMove, id, 25 + (int)id * 20, 220);
            Send(PreviewPointerKind.TouchUp, 3, -100, -100); // Outside preview still releases.
            Send(PreviewPointerKind.TouchMove, 6);
            Send(PreviewPointerKind.TouchDown, 7);
            Reset();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            var points = TouchPoints(packets);
            MappingAssert(points.Count(p => p.GetProperty("action").GetString() == "down") == 6 &&
                points.Count(p => p.GetProperty("action").GetString() == "move") == 5 &&
                points.Count(p => p.GetProperty("action").GetString() == "up") == 6,
                $"{context}: multi-touch move, excess finger, outside-up or reset is incorrect.");
            MappingAssert(points.GroupBy(p => p.GetProperty("pointerId").GetInt32())
                .All(g => g.First().GetProperty("action").GetString() == "down" &&
                    g.Last().GetProperty("action").GetString() == "up"), "Touch IDs crossed between fingers.");

            // A reset while the pipe is blocked invalidates all queued downs.
            var vm = KeyboardField(window, "_viewModel");
            var host = KeyboardCall(vm, "GetReadyUsbControlBridge", device.Udid)!;
            var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
            var gate = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
            packets.SetLength(0);
            gate.Wait();
            try
            {
                for (uint id = 1; id <= 5; ++id) Send(PreviewPointerKind.TouchDown, id);
                Reset();
            }
            finally { gate.Release(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            MappingAssert(TouchPoints(packets).All(p => p.GetProperty("action").GetString() == "up"),
                $"{context}: queued touch survived reset.");
            Console.WriteLine($"PASS {context}: preview five fingers, movement, outside release, reset and queued cancellation.");
        }
        finally { Reset(); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); control.AppleUdid = previousAppleUdid; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTouchWindow(nint hwnd, out uint flags);
}
