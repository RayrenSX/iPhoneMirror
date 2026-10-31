using System.Diagnostics;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunKeyboardRouterTests()
    {
        TestFunctionKeyRoutingDiagnostics();
        TestShortcutReleaseRecoveryModel();
        TestShortcutPressRecoveryModel();
        TestKeyboardReleaseFailureIsolation();
        TestRetiredKeyReconciliation();
        TestLostKeyUpReconciliation();
        TestMappingCaptureTransactions();
        TestCaptureRecoveryPhysicalIdentity();
        TestUnifiedKeyboardLifetimes();
        TestUnifiedKeyboardDispatch();
        return 0;
    }

    private static void TestLostKeyUpReconciliation()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        var calls = new List<(int Key, bool Down)>();
        var shortcuts = 0;
        var replays = 0;
        router.Shortcuts.Configure([
            new(new(KeyboardShortcut.Control, 0x42), "combo", () => true, () => shortcuts++),
            new(KeyboardShortcut.HomeDefault, "mouse", () => true, () => shortcuts++)]);
        void Key(int vk, bool down) => router.RouteEvent(new(vk, 0, false), down,
            (key, _) => new(KeyboardEventOwner.Direct, true, pressed => calls.Add((key.VirtualKey, pressed))),
            (_, _) => replays++);
        Key(0x41, true); Key(0xA2, true);
        var generation = router.Generation;
        router.ReconcileReleasedKeys(new HashSet<int> { 0x41 });
        MappingAssert(router.Generation == generation && calls.SequenceEqual(new[] { (0x41, true) }) &&
            shortcuts == 0 && replays == 0 && router.PressedModifiers == KeyboardShortcut.Control,
            "Lost candidate recovery replayed input, fired a shortcut, or interrupted a held key.");
        for (var i = 0; i < 100; i++) router.ReconcileReleasedKeys(new HashSet<int>());
        MappingAssert(calls.Count == 1 && router.PressedKeyCount == 2 && replays == 0,
            "Async state reported suppressed holds as up and periodic recovery released them.");
        Key(0xA2, false);
        Key(0x41, false);
        MappingAssert(calls.SequenceEqual(new[] { (0x41, true), (0xA2, true), (0xA2, false), (0x41, false) }),
            "Held key or buffered modifier lost its real release.");
        router.RouteEvent(new(0x5B, 0, false), true, (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { });
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        MappingAssert(!router.RouteShortcutTrigger((int)KeyboardShortcut.MouseRight), "Fixture did not block mouse shortcut.");
        router.ReconcileReleasedKeys(new HashSet<int>());
        MappingAssert(router.RouteShortcutTrigger((int)KeyboardShortcut.MouseRight), "Lost Win up still blocked mouse shortcut.");
        Key(0xA2, true); Key(0x42, true); Key(0x42, false); Key(0xA2, false);
        MappingAssert(shortcuts == 2 && replays == 0, "Keyboard/mouse shortcuts did not resume exactly once.");
        calls.Clear();
        router.RouteEvent(new(0x43, 0, false), true, (_, _) => new(KeyboardEventOwner.Direct, false,
            down => calls.Add((0x43, down))), (_, _) => { });
        router.ReconcileReleasedKeys(new HashSet<int>());
        router.ReconcileReleasedKeys(new HashSet<int>());
        Key(0x43, false);
        MappingAssert(calls.SequenceEqual(new[] { (0x43, true), (0x43, false) }), "Lost direct up was not released exactly once.");
        Console.WriteLine("PASS healthy-hook reconciliation: 100 empty-state polls preserve suppressed holds/candidates; keyboard/mouse shortcuts restored; direct release once.");
    }

    private static void TestRetiredKeyReconciliation()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        var events = new List<(int Key, bool Down)>();
        KeyboardPressRoute Resolve(MappedKey key, bool chord) => new(KeyboardEventOwner.Direct, true,
            down => events.Add((key.VirtualKey, down)));
        void Key(int vk, bool down) => router.RouteEvent(new(vk, 0, false), down, Resolve, (_, _) => { });
        Key(0x41, true); Key(0xA0, true);
        var generation = router.BeginHandoff(KeyboardInputMode.Direct);
        // A remains held; Shift was released unseen; B was first pressed
        // during the outage. Both physical holds must wait for their release.
        router.ReconcileRetiredKeys(new HashSet<int> { 0x41, 0x42 });
        router.CompleteHandoff(generation);
        events.Clear();
        Key(0x41, true); Key(0x42, true);
        MappingAssert(events.Count == 0 && router.PressedModifiers == 0,
            "Recovery replayed an outage hold or retained a released modifier.");
        Key(0x41, false); Key(0x42, false);
        MappingAssert(events.Count == 0 && !router.HasRetiredKeys,
            "Retired outage releases dispatched input or remained stuck.");
        Key(0x41, true); Key(0x41, false); Key(0x42, true); Key(0x42, false);
        MappingAssert(events.SequenceEqual(new[] { (0x41, true), (0x41, false), (0x42, true), (0x42, false) }),
            "Fresh presses failed after outage-held keys were released.");
        Console.WriteLine("PASS retired-key reconciliation: lost modifier up cleared; known and unseen holds quarantined; fresh keys resume once.");
    }

    private static void TestKeyboardReleaseFailureIsolation()
    {
        var router = new KeyboardInputRouter();
        var released = new List<int>();
        KeyboardPressRoute Resolve(MappedKey key, bool chord) => new(KeyboardEventOwner.Direct, true, down =>
        {
            if (down) return;
            released.Add(key.VirtualKey);
            if (key.VirtualKey == 0x41) throw new System.IO.IOException("Failed first backend release");
        });
        foreach (var key in new[] { 0x41, 0x42 })
            router.RouteEvent(new(key, 0, false), true, Resolve, (_, _) => { });
        Exception? failure = null;
        try { router.ReleaseAllPressedKeys(); } catch (Exception error) { failure = error; }
        MappingAssert(failure is null && released.SequenceEqual(new[] { 0x41, 0x42 }),
            "One failing release escaped the router and skipped other pressed keys.");
        router.ReleaseAllPressedKeys();
        foreach (var key in new[] { 0x41, 0x42 })
        {
            router.RouteEvent(new(key, 0, false), true, Resolve, (_, _) => { });
            router.RouteEvent(new(key, 0, false), false, Resolve, (_, _) => { });
        }
        MappingAssert(released.Count == 2 && router.PressedKeyCount == 0, "Failed release lost its retired physical lifetime.");
        Console.WriteLine("PASS independent release failures: all keys attempted once; repeats/ups remain retired.");
    }

    private static void TestUnifiedKeyboardLifetimes()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Mapping));
        var events = new List<string>();
        var replays = new List<string>();
        var target = "USB:A";
        KeyboardPressRoute Resolve(MappedKey key, bool chord)
        {
            var captured = target;
            return new(KeyboardEventOwner.Mapping, true, down => events.Add($"{captured}:{key.VirtualKey:X2}:{down}"));
        }
        void Replay(MappedKey key, bool down) => replays.Add($"{key.VirtualKey:X2}:{down}");
        bool Key(int vk, bool down) => router.RouteEvent(new(vk, 0, false), down, Resolve, Replay);
        void Pair(int vk) { Key(vk, true); Key(vk, false); }
        var shortcuts = new List<string>();
        router.Shortcuts.Configure(new[]
        {
            new RoutedShortcut(new(KeyboardShortcut.Control, 0x53), "Screenshot", () => true, () => shortcuts.Add("S")),
            new RoutedShortcut(new(KeyboardShortcut.Control, 0x70), "A", () => true, () => shortcuts.Add("A")),
            new RoutedShortcut(new(KeyboardShortcut.Control | KeyboardShortcut.Shift, 0x70), "B", () => true, () => shortcuts.Add("B")),
            new RoutedShortcut(new(0, 0x71), "Single", () => true, () => shortcuts.Add("Single")),
        });
        Pair(0x41);
        MappingAssert(events.SequenceEqual(new[] { "USB:A:41:True", "USB:A:41:False" }), "A did not have exactly one pair.");
        events.Clear(); Pair(0x53);
        MappingAssert(events.Count == 2, "Plain S mapping lost.");
        events.Clear();
        for (var i = 0; i < 100; i++)
        {
            Key(0xA2, true); Key(0x53, true); Key(0x53, true);
            Key(0xA2, false); Key(0x53, true); Key(0x53, false);
        }
        MappingAssert(shortcuts.Count == 100 && events.Count == 0 && router.PressedKeyCount == 0,
            "Shortcut repeats or early modifier releases leaked to mapping.");
        Pair(0x70); Key(0xA2, true); Pair(0x70); Key(0xA2, false);
        Key(0xA3, true); Key(0xA1, true); Pair(0x70); Key(0xA3, false); Key(0xA1, false);
        MappingAssert(events.Count == 2 && shortcuts.TakeLast(2).SequenceEqual(new[] { "A", "B" }), "Exact modifier precedence failed.");
        Pair(0x71); MappingAssert(shortcuts.Last() == "Single", "Single-key shortcut lost.");
        events.Clear();
        foreach (var modifier in new[] { 0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C }) Pair(modifier);
        MappingAssert(events.Count == 16 && router.PressedModifiers == 0, "Left/right standalone modifier fallback failed.");
        events.Clear();
        Key(0xA2, true); Pair(0x58); Key(0xA2, false);
        MappingAssert(events.SequenceEqual(new[] { "USB:A:A2:True", "USB:A:58:True", "USB:A:58:False", "USB:A:A2:False" }),
            "Failed candidate lost chronological down/up order.");
        events.Clear();
        Key(0x57, true);
        for (var i = 0; i < 1000; i++) Key(0x57, true);
        Key(0x57, false);
        MappingAssert(events.Count == 2, "Long hold repeated or lost release.");
        events.Clear();
        Key(0x57, true); router.ReleaseAllPressedKeys(); target = "Wireless:B";
        Key(0x57, true); Key(0x57, false); Pair(0x57);
        MappingAssert(events.SequenceEqual(new[] { "USB:A:57:True", "USB:A:57:False", "Wireless:B:57:True", "Wireless:B:57:False" }),
            "Focus/device switch migrated a held key or failed to release.");
        events.Clear();
        foreach (var next in new[] { "Bluetooth:B", "USB:B", "Wireless:A", "USB:A" })
        {
            Key(0x41, true); router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Mapping));
            target = next; Key(0x41, false); Pair(0x41);
        }
        MappingAssert(events.Count == 16, "Mode switches lost or duplicated a pair.");
        events.Clear();
        Key(0xA2, true); router.ReleaseAllPressedKeys(); Key(0xA2, false);
        MappingAssert(events.Count == 0, "Focus loss executed an unresolved candidate.");
        // An ordinary press never becomes a shortcut during a later repeat.
        Key(0x53, true); Key(0xA2, true); Key(0x53, true); Key(0x53, false); Key(0xA2, false);
        MappingAssert(shortcuts.Count == 103 && events.Count == 4, "Modifier change stole an existing mapping lifetime.");
        // Releasing one side must not commit the other side's pending prefix.
        events.Clear();
        Key(0xA2, true); Key(0xA3, true); Key(0xA2, false); Pair(0x53); Key(0xA3, false);
        MappingAssert(events.Count == 2 && shortcuts.Count == 104 && router.PressedModifiers == 0,
            "Releasing left Ctrl stole the still-held right Ctrl's shortcut candidate.");
        var mouse = new KeyboardInputRouter();
        var clicks = 0;
        mouse.Shortcuts.Configure([new(new(KeyboardShortcut.Control, KeyboardShortcut.MouseRight), "Mouse", () => true, () => clicks++)]);
        mouse.RouteEvent(new(0xA2, 0, false), true, Resolve, Replay);
        var beforeMouse = events.Count;
        MappingAssert(mouse.RouteShortcutTrigger((int)KeyboardShortcut.MouseRight), "Buffered Ctrl was invisible to the mouse shortcut.");
        mouse.RouteEvent(new(0xA2, 0, false), false, Resolve, Replay);
        MappingAssert(clicks == 1 && events.Count == beforeMouse, "Mouse shortcut leaked its modifier to mapping.");
        // Windows fallback must replay a suppressed prefix exactly once.
        var windows = new KeyboardInputRouter();
        windows.Shortcuts.Configure([new(new(KeyboardShortcut.Control, 0x53), "S", () => true, () => { })]);
        bool Win(int vk, bool down, bool defer = false) => windows.RouteEvent(new(vk, 0, false), down,
            (_, _) => new(KeyboardEventOwner.Windows, false), Replay, defer);
        MappingAssert(Win(0xA2, true) && Win(0xA2, false) && replays.SequenceEqual(new[] { "A2:True", "A2:False" }), "Standalone Ctrl was swallowed or replayed twice.");
        replays.Clear(); Win(0x5B, true, true); Win(0x44, true); Win(0x44, false); Win(0x5B, false);
        MappingAssert(replays.SequenceEqual(new[] { "5B:True", "44:True" }), "Win chord failed to replay its delayed prefix.");
        var background = new KeyboardInputRouter();
        var globalCount = 0;
        background.Shortcuts.Configure([new(new(KeyboardShortcut.Control, 0x53), "Global", () => true, () => globalCount++)]);
        bool Background(int vk, bool down) => background.RouteEvent(new(vk, 0, false), down,
            (_, _) => new(KeyboardEventOwner.Windows, false), Replay, bufferShortcutModifiers: false);
        MappingAssert(!Background(0xA2, true), "A global shortcut withheld another application's Ctrl+click modifier.");
        MappingAssert(Background(0x53, true) && Background(0x53, false) && !Background(0xA2, false) && globalCount == 1,
            "Global recognition lost a Windows-owned modifier or swallowed its release.");
        // Normal input has no candidate timeout; measure 20k quick WASD pairs.
        events.Clear();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20000; i++) Pair(new[] { 0x57, 0x41, 0x53, 0x44 }[i % 4]);
        clock.Stop();
        MappingAssert(events.Count == 40000 && router.PressedKeyCount == 0, "Rapid WASD lost or duplicated events.");
        Console.WriteLine($"PASS router model: exact chords, 100 Ctrl+S, all eight modifiers, long hold, focus/session retirement, Win replay, 20,000 WASD pairs in {clock.Elapsed.TotalMilliseconds:F1} ms.");
    }
}
