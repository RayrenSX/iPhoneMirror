using System.IO;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestMultiDeviceInputIsolation(MainWindow window, object vm,
        DeviceViewModel first, object firstHost, MemoryStream firstPackets,
        DeviceViewModel second, MemoryStream secondPackets, nint main, string context)
    {
        var previousForeground = KeyboardField(window, "_keyboardForegroundWindow");
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => main));
        var assembly = typeof(App).Assembly;
        var keyboardKind = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardKind")!;
        var keyboardEvent = assembly.GetType("IPhoneMirror.App.Controls.PreviewKeyboardEventArgs")!;
        var pointerKind = assembly.GetType("IPhoneMirror.App.Controls.PreviewPointerKind")!;
        var pointerEvent = assembly.GetType("IPhoneMirror.App.Controls.PreviewPointerEventArgs")!;
        var gate = (SemaphoreSlim)KeyboardField(KeyboardField(firstHost, "_bridge"), "_sendLock");
        void Focus(DeviceViewModel device)
        {
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(window, "FocusControlDevice", device.Udid, (nint)0);
        }
        void Key(DeviceViewModel device, int vk)
        {
            var e = Activator.CreateInstance(keyboardEvent, KeyboardTestMembers, null,
                [Enum.Parse(keyboardKind, "Down"), vk, 0], null)!;
            KeyboardCall(window, "HandleControlKeyboardInput", e, device.Udid, false, main);
        }
        Task Pointer(string kind)
        {
            var e = Activator.CreateInstance(pointerEvent, KeyboardTestMembers, null,
                [Enum.Parse(pointerKind, kind), (short)100, (short)240, (byte)1,
                    kind == "Wheel" ? 120 : 0, 200, 400, 390u, 844u, 0], null)!;
            return (Task)KeyboardCall(window, "HandleUsbPointerInputAsync", e, first.Udid)!;
        }
        bool HasKey(MemoryStream packets, int usage)
        {
            using var copy = new MemoryStream(packets.ToArray());
            using var reader = new BinaryReader(copy);
            while (copy.Position < copy.Length)
            {
                using var doc = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
                if (doc.RootElement.TryGetProperty("usages", out var usages) &&
                    usages.EnumerateArray().Any(v => v.GetInt32() == usage)) return true;
            }
            return false;
        }
        try
        {
            ReleaseTestPhysicalKeys(window);
            Focus(first);
            firstPackets.SetLength(0);
            secondPackets.SetLength(0);
            gate.Wait();
            try
            {
                Key(first, 0x4A);
                Focus(second);
                Key(second, 0x4B);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(HasKey(secondPackets, 14),
                    $"{context}: B's keyboard must send while A's writer remains blocked.");
            }
            finally { gate.Release(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(!HasKey(firstPackets, 13), $"{context}: A's stale J was sent.");

            Focus(second);
            secondPackets.SetLength(0);
            var secondHost = KeyboardCall(vm, "GetReadyUsbControlBridge", second.Udid)!;
            var secondGate = (SemaphoreSlim)KeyboardField(KeyboardField(secondHost, "_bridge"), "_sendLock");
            secondGate.Wait();
            try
            {
                Key(second, 0x4D);
                // An inactive window may finish closing after B already owns
                // the route. Its cleanup must not invalidate B's queued input.
                var reset = Activator.CreateInstance(keyboardEvent, KeyboardTestMembers, null,
                    [Enum.Parse(keyboardKind, "Reset"), 0, 0], null)!;
                KeyboardCall(window, "HandleControlKeyboardInput", reset, first.Udid, false, main);
            }
            finally { secondGate.Release(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(HasKey(secondPackets, 16),
                $"{context}: inactive A's cleanup discarded B's queued keyboard input.");

            foreach (var kind in new[] { "ButtonDown", "ButtonUp", "Move", "Wheel" })
            foreach (var refocus in new[] { false, true })
            {
                Focus(first);
                if (kind is "Move" or "ButtonUp") Pointer("ButtonDown").GetAwaiter().GetResult();
                firstPackets.SetLength(0);
                Task pending;
                gate.Wait();
                try
                {
                    pending = Pointer(kind);
                    Focus(second);
                    if (refocus) Focus(first);
                }
                finally { gate.Release(); }
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var state = KeyboardCall(window, "GetUsbTouchPointerState", first.Udid)!;
                while ((!pending.IsCompleted || (bool)KeyboardField(state, "WheelDraining")) &&
                    DateTime.UtcNow < deadline)
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
                InteractionAssert(pending.IsCompletedSuccessfully &&
                    !(bool)KeyboardField(state, "WheelDraining"),
                    $"{context}: cancelled {kind} did not finish.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                var touches = ReadPreviewTouchPackets(firstPackets);
                InteractionAssert(touches.Length > 0 && touches.All(p => p.Action == "up"),
                    $"{context}: stale {kind} survived focus switch (refocus={refocus}), or release was lost.");
                Focus(first);
                firstPackets.SetLength(0);
                Pointer("ButtonDown").GetAwaiter().GetResult();
                Pointer("ButtonUp").GetAwaiter().GetResult();
                InteractionAssert(ReadPreviewTouchPackets(firstPackets).Select(p => p.Action)
                    .SequenceEqual(new[] { "down", "up" }),
                    $"{context}: fresh touch failed after cancelling {kind}.");
            }
            Console.WriteLine($"{context}: independent keyboard queues, inactive-device cleanup, stale click/drag/wheel cancellation and releases passed.");
        }
        finally
        {
            Focus(second);
            SetKeyboardField(window, "_keyboardForegroundWindow", previousForeground);
        }
    }

    private static void TestIndependentBluetoothShortcutTarget(MainWindow window, object vm,
        DeviceViewModel selected, DeviceViewModel foregroundDevice, nint foregroundHwnd)
    {
        var previousForeground = KeyboardField(window, "_keyboardForegroundWindow");
        var previousSelection = KeyboardField(vm, "_selectedDevice");
        try
        {
            SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foregroundHwnd));
            SetKeyboardField(vm, "_selectedDevice", selected);
            KeyboardCall(window, "FocusControlDevice", foregroundDevice.Udid, foregroundHwnd);
            var action = Enum.Parse(typeof(App).Assembly.GetType(
                "IPhoneMirror.App.Services.BluetoothShortcutAction")!, "BluetoothControl");
            KeyboardCall(window, "HandleConfiguredShortcut", action);
            var snapshot = KeyboardField(KeyboardField(vm, "ControlStatus"), "Current");
            // Both fixtures already own a USB/wireless route. Startup fails
            // before any Bluetooth hardware access but records the real target.
            InteractionAssert((string)KeyboardField(snapshot, "DeviceName") == foregroundDevice.Name,
                "The Bluetooth shortcut must target the foreground independent device.");
            InteractionAssert(ReferenceEquals(KeyboardField(vm, "_selectedDevice"), selected),
                "The Bluetooth shortcut must preserve the main selection.");
        }
        finally
        {
            foreach (var popup in Application.Current.Windows.Cast<Window>()
                .Where(w => w.GetType().Name == "ReverseControlStatusWindow").ToArray()) popup.Close();
            SetKeyboardField(vm, "_selectedDevice", previousSelection);
            SetKeyboardField(window, "_keyboardForegroundWindow", previousForeground);
        }
    }
}
