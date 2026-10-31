using System.IO;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestKeyboardAvailabilityRecovery(MainWindow main, MemoryStream packets,
        string udid, nint hwnd, Action<nint> foreground, Action<int, bool> key, Action finish)
    {
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var matched = 0;
        router.Shortcuts.Configure(Enumerable.Range(0x70, 10).Select(vk => new RoutedShortcut(
            new(0, (uint)vk), $"F{vk - 0x6F}",
            () => router.Mode == KeyboardInputMode.Direct && KeyboardCall(main, "CanForwardControlKeyboard", udid, hwnd) is true,
            () => KeyboardCall(main, "QueueKeyboardShortcut", (Action)(() => matched++), (Func<bool>)(() => true)))));
        void Drain() => AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        void Probe()
        {
            matched = 0;
            for (var vk = 0x70; vk <= 0x79; vk++) { key(vk, true); key(vk, false); finish(); }
            MappingAssert(matched == 10, $"F1-F10 recovery matched {matched}/10.");
        }
        try
        {
            foreground(hwnd);
            KeyboardCall(main, "FocusControlDevice", udid, (nint)0); Drain();
            Probe();
            SetKeyboardField(main, "_keyboardFocusSuspended", true);
            foreground((nint)999);
            KeyboardCall(main, "RecoverKeyboardAvailability");
            MappingAssert((bool)KeyboardField(main, "_keyboardFocusSuspended"), "Recovery bypassed background focus.");
            foreground(hwnd);
            SetKeyboardField(main, "_isSettingsPanelVisible", true);
            SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => 0));
            KeyboardCall(main, "RecoverKeyboardAvailability");
            MappingAssert((bool)KeyboardField(main, "_keyboardFocusSuspended"), "Recovery bypassed editor focus.");
            SetKeyboardField(main, "_isSettingsPanelVisible", false);
            SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => 123));
            KeyboardCall(main, "RecoverKeyboardAvailability");
            Probe();

            KeyboardCall(main, "LeaveDirectKeyboardInputMode"); Drain();
            MappingAssert(router.Mode == KeyboardInputMode.None, "Fixture did not lose its direct owner.");
            KeyboardCall(main, "RecoverKeyboardAvailability"); Drain(); Probe();

            var attempts = 0;
            var releaseSucceeds = false;
            var failedRoute = new DirectKeyboardRoute(udid, "Test", new object(), 1, () => true,
                (_, _, _) => { attempts++; return releaseSucceeds ? Task.CompletedTask : Task.FromException(new IOException("test release failure")); });
            SetKeyboardField(main, "_directKeyboardRoute", failedRoute);
            KeyboardCall(main, "ResetKeyboardOwnership"); Drain();
            MappingAssert(router.Mode == KeyboardInputMode.None && attempts == 1, "Failed cleanup did not keep input closed.");
            KeyboardCall(main, "RecoverKeyboardAvailability"); Drain();
            MappingAssert(router.Mode == KeyboardInputMode.None && attempts == 2, "Retry bypassed failed device cleanup.");
            releaseSucceeds = true;
            KeyboardCall(main, "RecoverKeyboardAvailability"); Drain();
            MappingAssert(attempts == 3, "Recovery did not retry the captured old route.");
            Probe();

            var gate = (SemaphoreSlim)KeyboardField(main, "_systemShortcutGate");
            gate.Wait();
            try
            {
                KeyboardCall(main, "ResetKeyboardOwnership");
                var generation = router.Generation;
                for (var i = 0; i < 10; i++) KeyboardCall(main, "RecoverKeyboardAvailability");
                MappingAssert(router.Mode == KeyboardInputMode.None && generation == router.Generation,
                    "Recovery bypassed a pending writer or repeatedly queued handoffs.");
            }
            finally { gate.Release(); }
            Drain(); Probe();
            Console.WriteLine("PASS F1-F10 availability: foreground/editor guards, missed activation, missing direct owner, failed release retries, pending writer serialization.");
        }
        finally
        {
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            packets.SetLength(0);
        }
    }
}
