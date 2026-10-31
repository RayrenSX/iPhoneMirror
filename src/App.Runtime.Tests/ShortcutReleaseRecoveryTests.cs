using System.IO;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestShortcutReleaseRecoveryModel()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        var fired = 0;
        router.Shortcuts.Configure([new(new(0, 0x70), "Home", () => true, () => fired++)]);
        var key = new MappedKey(0x70, 0x3B, false);
        void Down(uint time) => router.RouteEvent(key, true,
            (_, _) => new(KeyboardEventOwner.Direct, true), (_, _) => { }, hookTime: time);
        Down(100);
        // A duplicate/older window up must not unlock a genuine held key.
        MappingAssert(!router.ReleaseShortcutFromWindow(key, 99) && !router.ReleaseShortcutFromWindow(key, 100),
            "Old or same-tick key-up released a newer shortcut.");
        for (uint i = 101; i < 200; i++) Down(i);
        MappingAssert(fired == 1 && !router.ReleaseShortcutFromWindow(key, 150), "Long hold retriggered or stale up ended it.");
        MappingAssert(router.ReleaseShortcutFromWindow(key, 201), "Window up did not recover a missed hook up.");
        MappingAssert(!router.ReleaseShortcutFromWindow(key, 201), "Duplicate up was applied twice.");
        Down(220);
        MappingAssert(fired == 2, "Next fresh shortcut was still swallowed after window up.");
        router.BeginHandoff(KeyboardInputMode.Direct);
        MappingAssert(router.ReleaseShortcutFromWindow(key, 230), "Retired shortcut was not cleared by its real up.");
        router.CompleteHandoff(router.Generation);
        Down(uint.MaxValue - 10);
        MappingAssert(router.ReleaseShortcutFromWindow(key, 5), "Message-time wraparound blocked release recovery.");
        var plain = new MappedKey(0x71, 0x3C, false);
        router.RouteEvent(plain, true, (_, _) => new(KeyboardEventOwner.Direct, true), (_, _) => { }, hookTime: 10);
        MappingAssert(!router.ReleaseShortcutFromWindow(plain, 20) && router.HasPressedKey(plain),
            "Shortcut repair modified a direct-input hold.");
        Console.WriteLine("PASS shortcut release recovery: missed hook up, long holds, stale/duplicate messages, retired keys, clock wrap and direct-input isolation.");
    }

    private static void TestShortcutReleaseRecoveryWindow(MainWindow main, MemoryStream packets,
        string udid, nint hwnd, Action<nint> foreground, Action finish)
    {
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var testBindings = bindings.ToDictionary(p => p.Key, _ => KeyboardShortcut.Unbound);
        testBindings[BluetoothShortcutAction.Home] = new(0, 0x70);
        var previousHook = (nint)KeyboardField(main, "_keyboardHook");
        var previousRawInput = (bool)KeyboardField(main, "_rawKeyboardInputEnabled");
        var packet = Activator.CreateInstance(typeof(MainWindow).GetNestedType("LowLevelKeyboardData", KeyboardTestMembers)!)!;
        SetKeyboardField(packet, "VirtualKey", 0x70u);
        SetKeyboardField(packet, "ScanCode", 0x3Bu);
        SetKeyboardField(packet, "Flags", 0x10u);
        void Down(uint time)
        {
            SetKeyboardField(packet, "Time", time);
            KeyboardCall(main, "ProcessKeyboardHook", packet, (nint)0x100);
        }
        void Up(uint time, string? target = null) => KeyboardCall(main, "HandleControlKeyboardInput",
            new PreviewKeyboardEventArgs(PreviewKeyboardKind.Up, 0x70, 0x3B) { MessageTime = time },
            target ?? udid, false, (nint?)hwnd);
        void HostUp(uint time) => KeyboardCall(main, "OnControlKeyboardInput", null,
            new PreviewKeyboardEventArgs(PreviewKeyboardKind.Up, 0x70, 0x3B) { MessageTime = time });
        try
        {
            foreground(hwnd);
            KeyboardCall(main, "FocusControlDevice", udid, (nint)0);
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            // Settle the preceding test's foreground transition before using
            // a placeholder HHOOK. In preview mode, that transition would
            // otherwise reconcile the placeholder back to zero on first down.
            KeyboardCall(main, "ObserveKeyboardForeground");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            KeyboardCall(main, "ConfigureKeyboardShortcuts", testBindings);
            // This fixture calls hook handlers directly in preview mode,
            // which normally leaves HHOOK zero. Exercise the installed-hook
            // window branch without subscribing to the user's keyboard.
            SetKeyboardField(main, "_keyboardHook", (nint)123);
            packets.SetLength(0);
            SetKeyboardField(main, "_keyboardFocusSuspended", true);
            // No activation notification or watchdog tick before the press.
            Down(100); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 2,
                "A stale focus flag still blocked a foreground shortcut.");
            MappingAssert((nint)KeyboardField(main, "_keyboardHook") != 0,
                "Fixture lost its installed-hook branch before testing window release.");
            Up(110, "wrong-device"); Down(120); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 2,
                "An unrelated window release retriggered the held shortcut.");
            Up(130); Down(140); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 4,
                "Real window fallback failed to clear a shortcut while HHOOK remained installed.");
            SetKeyboardField(main, "_rawKeyboardInputEnabled", true);
            SetKeyboardField(main, "_keyboardHook", (nint)0);
            HostUp(145);
            SetKeyboardField(main, "_keyboardHook", (nint)123);
            Down(146); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 4,
                "Legacy window up stole the release from Raw Input while no hook was installed.");
            HostUp(150); Down(160); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 6,
                "Raw Input registration blocked native host release recovery with a hook installed.");
            Up(170);
            foreground((nint)999);
            Down(180); Up(190); finish();
            MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("state", out _)) == 6,
                "Foreground recovery admitted background input.");
            Console.WriteLine("PASS shortcut window recovery: stale focus admits first foreground press; native up recovers missing hook up; Raw Input ownership preserved; wrong-device/background input blocked.");
        }
        finally
        {
            foreground(hwnd);
            SetKeyboardField(main, "_keyboardFocusSuspended", false);
            SetKeyboardField(main, "_keyboardHook", previousHook);
            SetKeyboardField(main, "_rawKeyboardInputEnabled", previousRawInput);
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            packets.SetLength(0);
        }
    }
}
