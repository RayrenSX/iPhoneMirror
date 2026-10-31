using System.IO;
using System.Windows.Threading;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint RecoveryTestNextHook(nint hook, int code, nint message, nint data);
    private static nint RecoveryTestHook(int code, nint message, nint data) => RecoveryTestNextHook(0, code, message, data);

    private static void TestKeyboardHookRecovery(MainWindow main, MemoryStream packets,
        Action<int, bool> key, Action finish)
    {
        var settings = (KeyboardMappingSettings)KeyboardField(main, "_mappingSettings");
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        var originalKeyState = (Func<int, bool>)KeyboardField(main, "_isPhysicalKeyboardKeyDown");
        var physicalKeys = new HashSet<int>();
        SetKeyboardField(main, "_isPhysicalKeyboardKeyDown", (Func<int, bool>)physicalKeys.Contains);
        // Real desktop keystrokes must not enter this synthetic router fixture.
        // The test still installs/removes native hooks, while key() explicitly
        // invokes the production callback with its controlled input packets.
        var originalHookProc = (Delegate)KeyboardField(main, "_keyboardHookProc");
        SetKeyboardField(main, "_keyboardHookProc", Delegate.CreateDelegate(originalHookProc.GetType(),
            typeof(Program).GetMethod(nameof(RecoveryTestHook), System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)!));
        // Opt this UI-preview fixture into real hook installation without
        // changing its Direct owner or sending any input to the desktop.
        settings.Enabled = true;
        try
        {
            KeyboardCall(main, "ReconcileKeyboardHook");
            var timer = (DispatcherTimer)KeyboardField(main, "_keyboardHookRefreshTimer");
            MappingAssert(timer.IsEnabled, "Keyboard recovery monitoring did not start.");
            packets.SetLength(0);
            key(0x41, true);
            physicalKeys.Add(0x41);
            var before = packets.Length;
            var generation = router.Generation;
            var hook = (nint)KeyboardField(main, "_keyboardHook");
            MappingAssert(hook != 0 && before > 0, "Hook recovery fixture did not hold a direct key.");
            KeyboardCall(main, "RefreshKeyboardHook");
            MappingAssert((nint)KeyboardField(main, "_keyboardHook") != hook &&
                router.Generation == generation && packets.Length == before,
                "Healthy renewal released or duplicated a held key.");
            key(0x41, false); finish();
            physicalKeys.Clear();
            MappingAssert(MappingFrames(packets).Count == 2, "Healthy renewal lost the matching release.");

            // Keep the app's nonzero HHOOK after removing the real Windows
            // subscription: the same observable state as silent hook removal.
            packets.SetLength(0);
            key(0x42, true);
            physicalKeys.Add(0x42);
            hook = (nint)KeyboardField(main, "_keyboardHook");
            MappingAssert(typeof(MainWindow).GetMethod("UnhookWindowsHookEx",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [hook]) is true,
                "Could not remove the test's live Windows hook.");
            // Exercise the actual timer, not just the recovery method.
            AdvanceDispatcher(TimeSpan.FromMilliseconds(2200));
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            MappingAssert(router.Generation > generation && (nint)KeyboardField(main, "_keyboardHook") != 0,
                "A stale nonzero hook handle prevented automatic recovery.");
            var frames = MappingFrames(packets);
            // Handoff may send a per-key release plus its final empty report.
            // It must never repeat the press, and every cleanup report is empty.
            MappingAssert(frames.Count >= 2 && frames[0].GetProperty("usages").GetArrayLength() == 1 &&
                frames.Skip(1).All(f => f.GetProperty("usages").GetArrayLength() == 0),
                "Hook recovery did not clear the device's held key: " + string.Join("; ", frames));
            var recoveredCount = frames.Count;
            key(0x42, true); key(0x42, false); finish();
            physicalKeys.Clear();
            MappingAssert(MappingFrames(packets).Count == recoveredCount, "Recovery re-triggered a retired key hold.");
            packets.SetLength(0);
            key(0x42, true); key(0x42, false); finish();
            MappingAssert(MappingFrames(packets).Count == 2, "Typing did not resume after hook recovery.");
            packets.SetLength(0);
            key(0xA2, true); key(0x53, true);
            physicalKeys.UnionWith([0xA2, 0x53]);
            KeyboardCall(main, "RefreshKeyboardHook");
            key(0x53, false); key(0xA2, false); finish();
            physicalKeys.Clear();
            MappingAssert(MappingFrames(packets).Count == 2 &&
                MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                "Recovered/renewed hook duplicated a shortcut or leaked its modifiers into typing: " + string.Join("; ", MappingFrames(packets)));
            foreach (var modifier in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            {
                key(modifier, true);
                hook = (nint)KeyboardField(main, "_keyboardHook");
                MappingAssert(typeof(MainWindow).GetMethod("UnhookWindowsHookEx",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, [hook]) is true, "Could not simulate lost modifier up.");
                // Physically released, but no hook callback arrived.
                KeyboardCall(main, "RefreshKeyboardHook");
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                MappingAssert(router.PressedModifiers == 0 && !router.HasRetiredKeys,
                    $"Lost modifier {modifier:X} remained logically held after recovery.");
                packets.SetLength(0);
                key(0x41, true); key(0x41, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2, "Lost modifier recovery blocked fresh typing.");
                packets.SetLength(0);
                key(0xA2, true); key(0x53, true); key(0x53, false); key(0xA2, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                    $"Lost modifier {modifier:X} blocked or duplicated the next shortcut.");
            }
            Console.WriteLine("PASS lost modifier ups: all eight left/right modifiers cleared; fresh typing and shortcuts resume without re-pressing the lost key.");
            foreach (var retire in new[] { false, true })
            foreach (var modifier in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            {
                router.RouteEvent(new(modifier, 0, false), true,
                    (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { }, bufferShortcutModifiers: false);
                if (retire) KeyboardCall(main, "ResetKeyboardOwnership");
                finish();
                // The native hook remains healthy; only its key-up was lost.
                KeyboardCall(main, "RefreshKeyboardHook");
                finish();
                MappingAssert(router.PressedModifiers == 0 && !router.HasRetiredKeys,
                    $"Healthy hook retained lost modifier {modifier:X}, retired={retire}.");
                packets.SetLength(0);
                key(0xA2, true); key(0x53, true); key(0x53, false); key(0xA2, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                    $"Shortcut did not recover with a healthy native hook: modifier={modifier:X} retired={retire} mode={router.Mode} frames={string.Join("; ", MappingFrames(packets))}");
            }
            Console.WriteLine("PASS healthy hook lost-ups: all eight modifiers, active and retired; shortcuts resume without restart.");
            TestKeyboardCaptureRecovery(main, packets, physicalKeys, key, finish);
            Console.WriteLine("PASS keyboard hook renewal: healthy held key preserved; stale native handle restored by timer; held state released; typing and shortcut execute once.");
        }
        finally
        {
            SetKeyboardField(main, "_isPhysicalKeyboardKeyDown", originalKeyState);
            settings.Enabled = false;
            KeyboardCall(main, "ReconcileKeyboardHook");
            SetKeyboardField(main, "_keyboardHookProc", originalHookProc);
        }
        MappingAssert((nint)KeyboardField(main, "_keyboardHook") == 0 &&
            !((DispatcherTimer)KeyboardField(main, "_keyboardHookRefreshTimer")).IsEnabled,
            "Disabling hook ownership left the recovery timer or subscription active.");
    }
}
