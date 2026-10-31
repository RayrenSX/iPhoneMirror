using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in hardware harness: device HID input and production clipboard polling.
    // Commands are supplied locally after inspecting the captured phone frame.
    private static void RunPhoneClipboardScript(MainViewModel vm, string output)
    {
        var target = vm.SelectedDevice!.Udid;
        vm.SetControlInputDevice(target);
        var control = (DeviceControlSession)KeyboardCall(vm, "FindControl", target)!;
        var bridge = control.WiredBridge ?? control.WirelessBridge;
        if (bridge is not null)
        {
            var process = (Process)KeyboardField(KeyboardField(bridge, "_bridge"), "_process");
            var binary = process.MainModule!.FileName;
            using var file = File.OpenRead(binary);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
            var expectedHash = Path.Combine(output, "expected-bridge.sha256");
            if (File.Exists(expectedHash))
                MappingAssert(string.Equals(File.ReadAllText(expectedHash).Trim(), hash, StringComparison.OrdinalIgnoreCase),
                    "The running bridge does not match the intended test artifact.");
            Console.WriteLine($"PHONE BRIDGE path={binary} sha256={hash}");
        }
        uint Sequence() => (uint)typeof(MainViewModel).GetMethod("GetClipboardSequenceNumber",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        if (bridge is not null) bridge.StatusChanged += (_, e) =>
        {
            if (e.EventName == "clipboard_text")
                Console.WriteLine($"PHONE CLIPBOARD EVENT id={e.ClipboardReadId} length={e.Text?.Length} sequence={Sequence()}");
        };
        var deadline = Stopwatch.StartNew();
        var commandPath = Path.Combine(output, "command.json");
        Console.WriteLine("PHONE SCRIPT READY");
        while (deadline.Elapsed < TimeSpan.FromMinutes(15))
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            if (!File.Exists(commandPath)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(commandPath));
            File.Delete(commandPath);
            var command = doc.RootElement;
            var action = command.GetProperty("action").GetString();
            var id = command.GetProperty("id").GetString()!;
            if (action == "stop") break;
            if (action == "tap")
            {
                var x = command.GetProperty("x").GetDouble();
                var y = command.GetProperty("y").GetDouble();
                AwaitMapping(vm.SendUsbTouchAsync("down", x, y, target));
                var holdMs = command.TryGetProperty("holdMs", out var hold)
                    ? Math.Clamp(hold.GetInt32(), 80, 3000) : 80;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(holdMs));
                AwaitMapping(vm.SendUsbTouchAsync("up", x, y, target));
            }
            if (action == "keys")
            {
                var route = vm.CaptureDirectKeyboardRoute(target)!;
                foreach (var report in command.GetProperty("reports").EnumerateArray())
                {
                    var keys = report.EnumerateArray().Select(k => k.GetByte()).ToArray();
                    AwaitMapping(route.SendAsync(0, keys, () => true));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                }
            }
            if (action == "paste_page")
            {
                var route = vm.CaptureDirectKeyboardRoute(target)!;
                foreach (byte[] keys in new byte[][] { [41], [], [227], [227, 15], [227], [] })
                {
                    AwaitMapping(route.SendAsync(0, keys, () => true));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                }
                MappingAssert(route.PasteTextAsync is not null, "Scripted route has no production paste RPC.");
                AwaitMapping(route.PasteTextAsync!(command.GetProperty("text").GetString()!, () => true),
                    TimeSpan.FromSeconds(IPhoneMirror.App.Services.CoreDeviceTouchProtocol.ClipboardRequestTimeoutSeconds +
                        IPhoneMirror.App.Services.CoreDeviceTouchProtocol.ClipboardCleanupTimeoutSeconds + 2));
                // Inspect/handle the phone's native Allow Paste prompt before
                // a separate command navigates. Enter can dismiss that prompt.
            }
            AdvanceDispatcher(TimeSpan.FromSeconds(2));
            var sync = KeyboardField(vm, "_clipboardSyncState");
            var latest = (System.Collections.IDictionary)KeyboardField(sync, "_latest");
            if (latest[target] is { } update)
            {
                var snapshot = (ClipboardSyncState.SequenceSnapshot)KeyboardField(update, "Snapshot");
                Console.WriteLine($"PHONE CLIPBOARD STATE id={id} baseline={snapshot.Sequence} current={Sequence()} applied={KeyboardField(update, "Applied")} attempts={KeyboardField(update, "Attempts")}");
            }
            if (action == "check")
            {
                var expected = command.GetProperty("expected").GetString();
                var timeout = command.TryGetProperty("timeoutSeconds", out var seconds)
                    ? Math.Clamp(seconds.GetInt32(), 1, 360) : 20;
                var clock = Stopwatch.StartNew();
                while (Clipboard.GetText() != expected && clock.Elapsed < TimeSpan.FromSeconds(timeout))
                {
                    if (File.Exists(commandPath))
                    {
                        using var pending = JsonDocument.Parse(File.ReadAllText(commandPath));
                        if (pending.RootElement.GetProperty("action").GetString() == "stop") return;
                    }
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                }
                var actual = Clipboard.GetText();
                var result = new { matched = actual == expected, expected, actual,
                    waitSeconds = clock.Elapsed.TotalSeconds };
                File.WriteAllText(Path.Combine(output, id + ".json"), JsonSerializer.Serialize(result));
                Console.WriteLine($"PHONE CLIPBOARD {id}: matched={actual == expected}");
            }
            vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, id + ".png")));
            Console.WriteLine($"PHONE SCRIPT DONE {id}");
        }
    }
}
