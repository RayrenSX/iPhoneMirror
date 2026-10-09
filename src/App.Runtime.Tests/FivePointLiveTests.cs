using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in real-device test. Only run with the observer page visible on the phone.
    // Discovery/UI are isolated fixture state; readiness, routes, writer, frozen
    // transport, HID reports and phone-side TouchEvents are real.
    private static int RunFivePointLive(string output, string transport, string udid,
        string python, string script, string observer)
    {
        if (transport is not ("usb" or "wireless")) throw new ArgumentException("Expected usb or wireless");
        if (!string.Equals(Path.GetExtension(script), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Phone event verification requires the complete packaged bridge runtime.", nameof(script));
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        var findings = new List<object>();
        var passed = false;
        void WaitLive(Task task, int seconds = 30)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            MappingAssert(task.IsCompleted, "Hardware operation timed out");
            task.GetAwaiter().GetResult();
        }
        try
        {
            bridge.OnEvent += e =>
            {
                // Never log clipboard content. Readiness comes from the real child.
                if (e.Event is "ready" or "error" || e.Event == "status")
                    Console.WriteLine($"BRIDGE {e.Event}: {e.Code}");
            };
            WaitLive(host.StartAsync(transport == "wireless" ? UsbTouchTransport.Wireless : UsbTouchTransport.Usb,
                udid, Path.GetFullPath(script)), 240);
            MappingAssert(bridge.IsReady && bridge.Udid == udid, "Real bridge not ready");
            var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            var phone = (DeviceViewModel)ctor.Invoke([udid, "Hardware test", "iPhone", "", "USB", "",
                Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
            var control = new DeviceControlSession(udid)
            {
                AppleUdid = udid,
                WiredBridge = transport == "usb" ? host : null,
                WirelessBridge = transport == "wireless" ? host : null,
                WiredEnabled = transport == "usb", WiredConnected = transport == "usb",
                WirelessEnabled = transport == "wireless", WirelessConnected = transport == "wireless"
            };
            control.Router.Begin(udid, transport == "usb" ? ReverseControlMode.Usb : ReverseControlMode.Wireless);
            ((IDictionary)KeyboardField(vm, "_deviceControls"))[udid] = control;
            SetKeyboardField(vm, "_selectedDevice", phone);
            WaitLive(ExerciseFivePointLive(vm, observer, findings), 90);
            passed = true;
            Console.WriteLine($"PASS real phone: {transport} five-point routes and concurrent mappings");
            return 0;
        }
        catch (Exception error)
        {
            findings.Add(new { error = error.ToString() });
            Console.WriteLine($"FAIL hardware: {error.Message}");
            return 1;
        }
        finally
        {
            WaitLive(host.StopAsync(), 30);
            File.WriteAllText(Path.Combine(output, transport + "-result.json"), JsonSerializer.Serialize(new
            {
                transport, passed, at = DateTimeOffset.Now, findings,
                limits = "Isolated view-model route fixture; no physical Windows fingers, WM_TOUCH or keyboard hook input. Phone TouchEvents are independent observations."
            }, new JsonSerializerOptions { WriteIndented = true }));
            ((IDictionary)KeyboardField(vm, "_deviceControls")).Clear();
            main.Close();
            app.Shutdown();
        }
    }

    private static async Task ExerciseFivePointLive(MainViewModel vm, string observer, List<object> findings)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var url = observer.TrimEnd('/') + "/events";
        async Task<JsonElement[]> Read() => JsonSerializer.Deserialize<JsonElement[]>(await http.GetStringAsync(url))!;
        var initial = await Read();
        MappingAssert(initial.Any(e => e.GetProperty("type").GetString() == "ready"), "Open the observer page on the phone first");
        MappingAssert(initial.Last().GetProperty("touches").GetArrayLength() == 0, "Phone already has active touches");
        var cursor = initial.Length;
        async Task<JsonElement> Expect(string stage, int count, string? type = null)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(7))
            {
                var rows = await Read();
                var match = rows.Skip(cursor).FirstOrDefault(e =>
                    e.GetProperty("touches").GetArrayLength() == count &&
                    (type is null || e.GetProperty("type").GetString() == type));
                if (match.ValueKind != JsonValueKind.Undefined)
                {
                    cursor = rows.Length;
                    findings.Add(new { stage, expected = count, observed = match });
                    Console.WriteLine($"PHONE PASS {stage}: {count}");
                    return match;
                }
                await Task.Delay(100);
            }
            throw new InvalidOperationException($"Phone did not report {stage}: expected {count} {type}");
        }
        async Task Stable(string stage, int count)
        {
            await Task.Delay(600);
            var rows = await Read();
            MappingAssert(rows.Last().GetProperty("touches").GetArrayLength() == count &&
                rows.Skip(cursor).All(e => e.GetProperty("touches").GetArrayLength() == count), $"{stage}: contacts changed unexpectedly");
            cursor = rows.Length;
            findings.Add(new { stage, expected = count, observed = rows.Last() });
            Console.WriteLine($"PHONE PASS {stage}: stable {count}");
        }
        MappedTouchRoute Route() => vm.CaptureMappingRoute(() => true, (x, y) => (x, y))
            ?? throw new InvalidOperationException("Production route unavailable");
        var routes = Enumerable.Range(0, 6).Select(_ => Route()).ToArray();
        var positions = Enumerable.Range(0, 6).Select(i => (X: .18 + i * .13, Y: .45)).ToArray();
        Task Send(int index, string action) => routes[index].SendAsync(action, positions[index].X, positions[index].Y, default);
        try
        {
            for (var i = 0; i < 5; i++) await Send(i, "down");
            var five = await Expect("five down", 5, "touchstart");
            await Stable("five held", 5);
            var original = five.GetProperty("touches").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetInt32());
            MappingAssert(original.Count == 5, "Phone reported duplicate contact IDs");
            positions[2] = (positions[2].X, .60);
            await Send(2, "move");
            var moved = await Expect("one moves, four held", 5, "touchmove");
            var movedTouches = moved.GetProperty("touches").EnumerateArray().ToArray();
            MappingAssert(movedTouches.All(t => original.ContainsKey(t.GetProperty("id").GetInt32())), "Moving changed contact IDs");
            MappingAssert(movedTouches.Count(t => Math.Abs(t.GetProperty("y").GetDouble() -
                original[t.GetProperty("id").GetInt32()].GetProperty("y").GetDouble()) > 4) == 1, "Expected exactly one moved finger");
            await Send(5, "down");
            await Stable("sixth rejected", 5);
            await Send(2, "up");
            var four = await Expect("independent up", 4, "touchend");
            MappingAssert(four.GetProperty("touches").EnumerateArray().All(t => original.ContainsKey(t.GetProperty("id").GetInt32())), "Independent up changed remaining IDs");
            await Send(5, "move");
            await Stable("rejected move does not resurrect", 4);
            await Send(5, "up");
            await Send(5, "down");
            await Expect("released slot reused", 5, "touchstart");
        }
        finally
        {
            for (var i = 0; i < 6; i++) await Send(i, "up");
        }
        await Expect("all released", 0, "touchend");
        using var executor = new KeyboardMappingExecutor();
        var mappings = Enumerable.Range(0, 6).Select(i => MappingEntry(MappedTouchAction.LongPress) with
        {
            Key = new MappedKey(0x41 + i, 0x20 + i, false), X = .18 + i * .13, Y = .45, DurationMs = 6000
        }).ToArray();
        var keyState = new KeyboardMappingKeyState();
        var runs = new List<Task<bool>>();
        try
        {
            foreach (var entry in mappings.Take(5))
            {
                var matched = keyState.Process(entry.Key!, true, false, true, false, false, mappings).Mapping;
                MappingAssert(matched is not null, "Key-state mapping failed");
                runs.Add(executor.ExecuteAsync(matched!, Route()));
            }
            await Expect("five concurrent keyboard mappings", 5, "touchstart");
            MappingAssert(!await executor.ExecuteAsync(mappings[5], Route()), "Sixth mapping was accepted");
            MappingAssert(!await executor.ExecuteAsync(mappings[0], Route()), "Same mapping reentered");
            await Stable("mapping cap and same-key suppression", 5);
        }
        finally
        {
            executor.Cancel();
            foreach (var task in runs)
                try { await task; } catch (OperationCanceledException) { }
        }
        await Expect("cancel releases all mappings", 0, "touchend");
        await Stable("no stuck contacts", 0);
    }
}
