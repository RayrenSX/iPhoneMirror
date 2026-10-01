using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestBluetoothKeyboardTransportGates()
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Services.BluetoothHidMouseService", true)!;
        var service = Activator.CreateInstance(type, nonPublic: true)!;
        var send = type.GetMethods(KeyboardTestMembers).Single(m =>
            m.Name == "SendKeyboardAsync" && m.GetParameters().Length == 4);
        SetKeyboardField(service, "_targetDeviceUdid", "focus-test");
        var targetGate = (SemaphoreSlim)KeyboardField(service, "_targetClientGate");
        try
        {
            foreach (var waitForTarget in new[] { false, true })
            {
                // Pause the real report pump to model a busy earlier report.
                SetKeyboardField(service, "_mousePumpRunning", true);
                var allowed = true;
                var report = (Task)send.Invoke(service,
                    [(byte)0, new byte[] { 27 }, "focus-test", (Func<bool>)(() => allowed)])!;
                if (report.IsCompleted)
                    throw new InvalidOperationException("Bluetooth fixture failed to queue the press.");
                Task pump;
                if (waitForTarget)
                {
                    targetGate.Wait();
                    try
                    {
                        pump = (Task)KeyboardCall(service, "PumpReportsAsync",
                            KeyboardField(service, "_mousePumpGeneration"))!;
                        if (pump.IsCompleted)
                            throw new InvalidOperationException("Bluetooth fixture did not reach the target gate.");
                        allowed = false;
                    }
                    finally { targetGate.Release(); }
                }
                else
                {
                    allowed = false;
                    pump = (Task)KeyboardCall(service, "PumpReportsAsync",
                        KeyboardField(service, "_mousePumpGeneration"))!;
                }
                if (!Task.WhenAll(pump, report).Wait(TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("Expired Bluetooth input blocked the report pump.");
                if (((IDictionary)KeyboardField(service, "_lastReports")).Count != 0)
                    throw new InvalidOperationException("Expired Bluetooth input was published as a report.");
            }

            // A release must not carry the expired guard into the queue.
            SetKeyboardField(service, "_mousePumpRunning", true);
            var release = (Task)send.Invoke(service,
                [(byte)0, Array.Empty<byte>(), "focus-test", (Func<bool>)(() => false)])!;
            var queue = KeyboardField(service, "_keyboardPriorityReports");
            var queued = (ITuple)queue.GetType().GetMethod("Peek")!.Invoke(queue, null)!;
            if (queued[3] is not null)
                throw new InvalidOperationException("Bluetooth release retained a focus restriction.");
            // No GATT device is connected in this test; finish the inspected
            // release without starting a native notification.
            queue.GetType().GetMethod("Clear")!.Invoke(queue, null);
            ((TaskCompletionSource<bool>)queued[2]!).TrySetResult(true);
            release.GetAwaiter().GetResult();
            Console.WriteLine("Bluetooth transport queue and target-gate focus regressions passed.");
        }
        finally
        {
            SetKeyboardField(service, "_mousePumpRunning", false);
            ((Task)KeyboardCall(service, "ReleaseAllAsync", false)!).GetAwaiter().GetResult();
        }
    }

    private static void TestKeyboardHotkeyScope(MainWindow window, Window other,
        string udid, Action<Window> focus)
    {
        var type = typeof(MainWindow);
        var register = type.GetMethod("RegisterHotKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var unregister = type.GetMethod("UnregisterHotKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var otherHandle = new WindowInteropHelper(other).Handle;
        const int probeId = 0x7A01;
        bool Probe(uint vk)
        {
            var acquired = (bool)register.Invoke(null, [otherHandle, probeId, 6u, vk])!;
            if (acquired) unregister.Invoke(null, [otherHandle, probeId]);
            return acquired;
        }
        var shortcuts = (IDictionary)KeyboardField(window, "_bluetoothShortcuts");
        var previous = shortcuts.Keys.Cast<object>().ToDictionary(k => k, k => shortcuts[k]);
        var assembly = typeof(App).Assembly;
        var actionType = assembly.GetType("IPhoneMirror.App.Services.BluetoothShortcutAction", true)!;
        var shortcutType = assembly.GetType("IPhoneMirror.App.Services.KeyboardShortcut", true)!;
        object Action(string name) => Enum.Parse(actionType, name);
        int Id(object action) => (int)type.GetMethod("HotKeyId", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [action])!;
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        KeyboardCall(window, "UnregisterConfiguredHotkeys");
        // Choose unused combinations without changing the user's settings.
        var keys = Enumerable.Range(0x7C, 12).Select(v => (uint)v).Where(Probe).Take(5).ToArray();
        Require(keys.Length == 5, "Insufficient unused test hotkeys.");
        var actions = new[] { "Home", "BossKey", "BluetoothControl", "WirelessControl", "WiredControl" };
        try
        {
            foreach (var action in previous.Keys) shortcuts[action] = Activator.CreateInstance(shortcutType);
            for (var i = 0; i < actions.Length; i++)
                shortcuts[Action(actions[i])] = Activator.CreateInstance(shortcutType, [6u, keys[i]]);
            focus(window);
            Require((bool)KeyboardCall(window, "TryRegisterShortcutSet", shortcuts,
                Activator.CreateInstance(actionType))!, "Focused hotkey registration failed.");
            Require(!Probe(keys[0]), "The focused device hotkey was not registered with Windows.");
            focus(other);
            Require(Probe(keys[0]), "The background device hotkey is still reserved with Windows.");
            var registered = (HashSet<int>)KeyboardField(window, "_registeredHotKeyIds");
            Require(actions.Skip(1).All(a => registered.Contains(Id(Action(a)))),
                "Focus loss removed a global boss/mode hotkey.");
            Require(keys.Skip(1).All(k => !Probe(k)), "A global hotkey was released with the device hotkeys.");
            focus(window);
            Require(!Probe(keys[0]), "Refocusing did not restore the device hotkey.");

            SetKeyboardField(window, "_activeControlWindow", otherHandle);
            SetKeyboardField(window, "_activeControlUdid", udid);
            focus(other);
            Require(!Probe(keys[0]), "Independent activation did not register the device hotkey.");
            KeyboardCall(window, "OnIndependentKeyboardFocusChanged", udid, otherHandle, false);
            Require(Probe(keys[0]), "Independent deactivation retained the device hotkey.");
            focus(window);
            Require(!Probe(keys[0]), "Returning to the main device did not restore its hotkey.");
            Console.WriteLine("Windows hotkey scope checks passed for main/independent focus and global exceptions.");
        }
        finally
        {
            KeyboardCall(window, "UnregisterConfiguredHotkeys");
            foreach (var (key, value) in previous) shortcuts[key] = value;
            SetKeyboardField(window, "_activeControlWindow", (nint)0);
            SetKeyboardField(window, "_activeControlUdid", null);
        }
    }
}
