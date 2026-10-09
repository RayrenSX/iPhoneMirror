using System.IO;
using System.Reflection;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestKeyboardCaptureRecovery(MainWindow main, MemoryStream packets,
        HashSet<int> physicalKeys, Action<int, bool> key, Action finish)
    {
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        var capture = (KeyboardMappingCapture)KeyboardField(main, "_mappingCapture");
        var recorded = new List<MappedKey>();
        void Begin()
        {
            recorded.Clear();
            MappingAssert(KeyboardCall(main, "BeginMappingKeyCapture", (Action<MappedKey>)recorded.Add) is null,
                "Could not start recovery recording fixture.");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            packets.SetLength(0);
        }
        void Recover()
        {
            var hook = (nint)KeyboardField(main, "_keyboardHook");
            MappingAssert(typeof(MainWindow).GetMethod("UnhookWindowsHookEx", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [hook]) is true, "Could not simulate hook loss during recording.");
            KeyboardCall(main, "RefreshKeyboardHook");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        }
        void Leave()
        {
            KeyboardCall(main, "EndMappingKeyCapture");
            KeyboardCall(main, "LeaveKeyboardMappingInputMode");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        }
        try
        {
            foreach (var modifier in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            {
                Begin();
                key(modifier, true); physicalKeys.Add(modifier);
                Recover();
                Leave(); // cancellation must retain the matching up
                key(modifier, true); // repeat must not become a fresh press
                physicalKeys.Clear(); key(modifier, false); finish();
                MappingAssert(recorded.Count == 0 && !capture.HasHeldKeys && !router.HasRetiredKeys &&
                    router.PressedModifiers == 0 && router.PressedKeyCount == 0 && packets.Length == 0,
                    $"Cancelled recording kept modifier {modifier:X} held or dispatched a captured event: recorded={recorded.Count}, captureHeld={capture.HasHeldKeys}, retired={string.Join(',', (HashSet<int>)KeyboardField(router, "_retired"))}, modifiers={router.PressedModifiers}, presses={router.PressedKeyCount}; " + string.Join("; ", MappingFrames(packets)));
                key(0xA2, true); key(0x53, true); key(0x53, false); key(0xA2, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                    $"Cancelled recording blocked or duplicated the shortcut after {modifier:X} release.");
            }

            Begin();
            key(0x41, true); // physical A up is lost while the hook is absent
            Recover();
            MappingAssert(capture.Waiting && !capture.HasHeldKeys && recorded.Count == 0,
                "Lost capture up either stuck the old candidate or synthesized confirmation.");
            key(0x42, true); key(0x42, true); key(0x42, false); finish();
            MappingAssert(recorded.Count == 1 && recorded[0].VirtualKey == 0x42 &&
                !capture.Waiting && !capture.HasHeldKeys && packets.Length == 0,
                "Recording did not accept exactly the fresh B after lost A up.");
            Leave();

            Begin();
            key(0x41, true); physicalKeys.Add(0x41);
            Recover();
            key(0x41, true); // still held across recovery
            MappingAssert(recorded.Count == 0 && capture.Waiting, "Recovery confirmed a still-held key.");
            physicalKeys.Clear(); key(0x41, false); finish();
            MappingAssert(recorded.Count == 1 && recorded[0].VirtualKey == 0x41 &&
                !router.HasRetiredKeys && !capture.HasHeldKeys && packets.Length == 0,
                "Recovered recording lost its real up or left the router quarantined.");
            Leave();

            Begin();
            physicalKeys.Add(0xA0); // press started during outage; no capture down
            Recover();
            KeyboardCall(main, "EndMappingKeyCapture");
            Begin(); // a new recording must not claim the old held key either
            key(0xA0, true);
            physicalKeys.Clear(); key(0xA0, false); finish();
            MappingAssert(recorded.Count == 0 && capture.Waiting && !router.HasRetiredKeys,
                "An unseen outage hold was recorded or its up failed to clear router state.");
            key(0x43, true); key(0x43, false); finish();
            MappingAssert(recorded.Count == 1 && recorded[0].VirtualKey == 0x43 && packets.Length == 0,
                "Fresh recording failed after an unseen outage hold.");
            Leave();
            Console.WriteLine("PASS capture hook recovery: cancelled modifier ups clear both owners; lost up accepts fresh key; held candidate completes once; unseen holds cannot enter a new recording.");
        }
        finally
        {
            physicalKeys.Clear();
            Leave();
            ReleaseTestPhysicalKeys(main);
        }
    }

    private static void TestCaptureRecoveryPhysicalIdentity()
    {
        var capture = new KeyboardMappingCapture();
        var recorded = new List<MappedKey>();
        var key = new MappedKey(0x41, 0x1E, false);
        capture.Begin(recorded.Add);
        capture.Process(key, true, true, action => action());
        capture.ReconcilePhysicalKeys(new HashSet<int> { 0x41 });
        MappingAssert(capture.HasHeldKeys, "Physical sampling confused scan code with virtual key.");
        capture.Process(key, false, true, action => action());
        MappingAssert(recorded.SequenceEqual(new[] { key }) && !capture.HasHeldKeys,
            "Recovery did not preserve the captured scan code through real release.");
        capture.Begin(recorded.Add);
        capture.Process(key, true, true, action => action());
        capture.ReconcilePhysicalKeys(new HashSet<int>());
        MappingAssert(capture.Waiting && !capture.HasHeldKeys && recorded.Count == 1,
            "Released physical capture was not pruned without confirmation.");
        Console.WriteLine("PASS capture physical reconciliation uses virtual state while preserving scan identity.");
    }
}
