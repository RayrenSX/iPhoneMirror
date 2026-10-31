using System.IO;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestShortcutPressRecoveryModel()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        var fired = 0;
        var direct = 0;
        var available = true;
        router.Shortcuts.Configure([
            new(new(0, 0x70), "Home", () => available, () => fired++),
            new(new(KeyboardShortcut.Control, 0x51), "Custom", () => available, () => fired++),
        ]);
        var home = new MappedKey(0x70, 0x3B, false);
        bool Window(uint time, uint now, bool repeat = false, MappedKey? key = null) =>
            router.RecoverShortcutPressFromWindow(key ?? home, time, now, repeat);
        void Hook(MappedKey key, bool down, uint time) => router.RouteEvent(key, down,
            (_, _) => new(KeyboardEventOwner.Direct, true, _ => direct++), (_, _) => direct++, hookTime: time);
        MappingAssert(Window(100, 101) && fired == 1, "Missing hook down did not match its window message.");
        MappingAssert(!Window(100, 101) && !Window(101, 102), "Window duplicate retriggered a held shortcut.");
        Hook(home, true, 100);
        Hook(home, false, 170);
        MappingAssert(fired == 1 && !Window(100, 180) && !Window(160, 180),
            "Completed hook lifetime was replayed by a delayed window down.");
        router.InvalidateWindowShortcutMessages(190);
        MappingAssert(!Window(200, 201, repeat: true), "A hold from before the focus context manufactured a new shortcut lifetime.");
        MappingAssert(Window(220, 221) && router.ReleaseShortcutFromWindow(home, 230),
            "Fresh window press/release was not recovered after a completed lifetime.");
        Hook(home, true, 300); Hook(home, false, 370);
        MappingAssert(fired == 3 && !Window(300, 380), "Normal hook down/up was duplicated by the window adapter.");
        router.InvalidateWindowShortcutMessages(400);
        MappingAssert(!Window(399, 401) && Window(400, 401), "Focus/binding reset did not reject an older window message.");
        Hook(home, false, 470);
        MappingAssert(!Window(500, 499) && !Window(500, 1501), "Future or stale window message was accepted.");
        available = false;
        MappingAssert(!Window(1600, 1601) && !router.HasPressedKey(home), "Unavailable shortcut acquired a press.");
        available = true;
        MappingAssert(!Window(1700, 1701, key: new(0x41, 0x1E, false)) && direct == 0,
            "Recovery dispatched an ordinary unbound key or replayed input.");
        var control = new MappedKey(0xA2, 0x1D, false);
        var custom = new MappedKey(0x51, 0x10, false);
        Hook(control, true, 1800);
        MappingAssert(Window(1810, 1811, key: custom) && fired == 5,
            "A configured modifier shortcut could not use the existing buffered modifier.");
        MappingAssert(router.ReleaseShortcutFromWindow(custom, 1820), "Recovered custom shortcut retained its release.");
        Hook(control, false, 1830);
        MappingAssert(direct == 0, "Recovery leaked a buffered shortcut modifier into direct input.");
        Hook(home, true, 1900);
        router.BeginHandoff(KeyboardInputMode.Direct);
        MappingAssert(!Window(1910, 1911), "Retired hold entered a new shortcut lifetime.");
        router.CompleteHandoff(router.Generation); Hook(home, false, 1920);
        var oldModifier = new MappedKey(0xA1, 0x36, false);
        Hook(oldModifier, true, 2000);
        router.BeginHandoff(KeyboardInputMode.Direct); router.CompleteHandoff(router.Generation);
        MappingAssert(!Window(2010, 2011), "Window recovery ignored a retired modifier.");
        Hook(oldModifier, false, 2020);
        var priorFired = fired;
        router.RouteEvent(control, true, (_, _) => new(KeyboardEventOwner.Direct, true), (_, _) => { },
            bufferShortcutModifiers: false, hookTime: 2050);
        MappingAssert(!Window(2060, 2061, key: custom) && fired == priorFired,
            "Recovery stole a modifier that had already entered direct input.");
        Hook(control, false, 2070);
        var repeated = new KeyboardInputRouter();
        var repeatFired = 0;
        repeated.Shortcuts.Configure([new(new(0, 0x70), "Home", () => true, () => repeatFired++)]);
        repeated.InvalidateWindowShortcutMessages(90);
        repeated.RouteEvent(home, true, (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { }, hookTime: 100);
        repeated.RouteEvent(home, false, (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { }, hookTime: 170);
        MappingAssert(repeated.RecoverShortcutPressFromWindow(home, 200, 201, true) && repeatFired == 2,
            "Windows' stale previous-key-state flag discarded a fresh press after its witnessed release.");
        MappingAssert(!repeated.RecoverShortcutPressFromWindow(home, 210, 211, true) && repeatFired == 2,
            "A true repeat of the recovered hold executed twice.");
        MappingAssert(!repeated.RouteEvent(home, false, (_, _) => new(KeyboardEventOwner.Windows, false),
                (_, _) => { }, hookTime: 220),
            "Recovered native down suppressed its physical Windows up and left the key logically held.");
        repeated.InvalidateWindowShortcutMessages(230);
        MappingAssert(!repeated.RecoverShortcutPressFromWindow(home, 240, 241, true),
            "A release from the previous focus context admitted an unknown held key.");
        var extended = new KeyboardInputRouter();
        var enterFired = 0;
        extended.Shortcuts.Configure([new(new(0, 0x0D), "Enter", () => true, () => enterFired++)]);
        MappingAssert(extended.RecoverShortcutPressFromWindow(new(0x0D, 0x1C, false), 100, 101, false) &&
            extended.RecoverShortcutPressFromWindow(new(0x0D, 0x1C, true), 100, 101, false) && enterFired == 2,
            "Message deduplication mixed main Enter and keypad Enter physical identities.");
        var wrapped = new KeyboardInputRouter();
        var wrapFired = 0;
        wrapped.Shortcuts.Configure([new(new(0, 0x70), "Home", () => true, () => wrapFired++)]);
        wrapped.InvalidateWindowShortcutMessages(uint.MaxValue - 100);
        MappingAssert(wrapped.RecoverShortcutPressFromWindow(home, uint.MaxValue - 10, 5, false) &&
            wrapped.ReleaseShortcutFromWindow(home, 5) && wrapped.RecoverShortcutPressFromWindow(home, 6, 7, false) &&
            !wrapped.RecoverShortcutPressFromWindow(home, uint.MaxValue - 10, 7, false) && wrapFired == 2,
            "Message clock wrap duplicated or refused fresh shortcut recovery.");
        Console.WriteLine("PASS shortcut press recovery model: missed down, normal-hook dedupe, repeats, stale/future timestamps, focus reset, custom chords, retired holds and clock wrap.");
    }

    private static void TestShortcutPressRecoveryWindow(MainWindow main, MemoryStream packets,
        string udid, nint hwnd, Action<nint> foreground, Action finish)
    {
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var testBindings = bindings.ToDictionary(pair => pair.Key, _ => KeyboardShortcut.Unbound);
        testBindings[BluetoothShortcutAction.Home] = new(0, 0x70);
        var previousHook = (nint)KeyboardField(main, "_keyboardHook");
        var previousRawInput = (bool)KeyboardField(main, "_rawKeyboardInputEnabled");
        var capture = (KeyboardMappingCapture)KeyboardField(main, "_mappingCapture");
        var previousFocusedWindow = (Func<nint>)KeyboardField(main, "_keyboardFocusedWindow");
        var previousSettingsVisible = (bool)KeyboardField(main, "_isSettingsPanelVisible");
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        int Presses() => MappingFrames(packets).Count(frame => frame.TryGetProperty("state", out _));
        uint Now() => unchecked((uint)Environment.TickCount);
        void Window(PreviewKeyboardKind kind, uint? time, string? device = null, nint? source = null,
            bool repeat = false, int key = 0x70, bool raw = false) => KeyboardCall(main, "HandleControlKeyboardInput",
                new PreviewKeyboardEventArgs(kind, key, key == 0x70 ? 0x3B : 0)
                { MessageTime = time, IsRepeat = repeat }, device ?? udid, raw, source ?? hwnd);
        try
        {
            foreground(hwnd);
            KeyboardCall(main, "FocusControlDevice", udid, (nint)0);
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            KeyboardCall(main, "ObserveKeyboardForeground");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            KeyboardCall(main, "ConfigureKeyboardShortcuts", testBindings);
            SetKeyboardField(main, "_keyboardHook", (nint)123);
            SetKeyboardField(main, "_rawKeyboardInputEnabled", false);
            packets.SetLength(0);
            var time = Now();
            // Reproduce the hour-long run: Windows delivers Down/Up to the
            // preview while only Up reaches a still-nonzero native hook.
            Window(PreviewKeyboardKind.Down, time);
            finish();
            MappingAssert(Presses() == 2,
                "A native window shortcut down was lost while the saved HHOOK was nonzero.");
            Window(PreviewKeyboardKind.Down, Now()); finish();
            Window(PreviewKeyboardKind.Down, Now(), repeat: true); finish();
            MappingAssert(Presses() == 2, "Window duplicate repeated a recovered shortcut hold.");
            Window(PreviewKeyboardKind.Up, Now());
            Window(PreviewKeyboardKind.Down, time); finish();
            MappingAssert(Presses() == 2, "A delayed old down replayed a completed shortcut.");
            Window(PreviewKeyboardKind.Down, Now(), repeat: true); finish();
            MappingAssert(Presses() == 4, "A stale Windows previous-state flag blocked a new press after its observed release.");
            Window(PreviewKeyboardKind.Up, Now());
            Window(PreviewKeyboardKind.Down, null);
            Window(PreviewKeyboardKind.Down, unchecked(Now() - 2000));
            Window(PreviewKeyboardKind.Down, Now(), device: "wrong-device");
            Window(PreviewKeyboardKind.Down, Now(), source: (nint)999);
            Window(PreviewKeyboardKind.Down, Now(), raw: true);
            Window(PreviewKeyboardKind.Down, Now(), key: 0x41); finish();
            MappingAssert(Presses() == 4 && !router.HasPressedKey(new(0x41, 0, false)),
                "Invalid/stale/repeated/wrong-target/unbound window input acquired a shortcut or ordinary hold.");
            SetKeyboardField(main, "_rawKeyboardInputEnabled", true);
            Window(PreviewKeyboardKind.Down, Now()); finish();
            SetKeyboardField(main, "_rawKeyboardInputEnabled", false);
            foreground((nint)999); Window(PreviewKeyboardKind.Down, Now()); finish(); foreground(hwnd);
            SetKeyboardField(main, "_mappingClosing", true); Window(PreviewKeyboardKind.Down, Now()); finish();
            SetKeyboardField(main, "_mappingClosing", false);
            capture.Begin(_ => throw new InvalidOperationException("A fallback completed shortcut recording."));
            Window(PreviewKeyboardKind.Down, Now()); finish(); capture.Cancel();
            SetKeyboardField(main, "_isSettingsPanelVisible", true);
            SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => 0));
            Window(PreviewKeyboardKind.Down, Now()); finish();
            SetKeyboardField(main, "_isSettingsPanelVisible", previousSettingsVisible);
            SetKeyboardField(main, "_keyboardFocusedWindow", previousFocusedWindow);
            MappingAssert(Presses() == 4, "Raw Input takeover, background or closing state admitted a window shortcut.");
            Window(PreviewKeyboardKind.Down, Now()); finish();
            MappingAssert(Presses() == 6, "Next fresh press after native recovery was swallowed.");
            Window(PreviewKeyboardKind.Up, Now());
            Console.WriteLine("PASS shortcut press recovery window: missed hook down sent once; repeats, completed lifetimes, stale messages, wrong devices, background, Raw Input, closing and plain keys isolated.");
        }
        finally
        {
            foreground(hwnd);
            SetKeyboardField(main, "_keyboardHook", previousHook);
            SetKeyboardField(main, "_rawKeyboardInputEnabled", previousRawInput);
            SetKeyboardField(main, "_mappingClosing", false);
            SetKeyboardField(main, "_isSettingsPanelVisible", previousSettingsVisible);
            SetKeyboardField(main, "_keyboardFocusedWindow", previousFocusedWindow);
            capture.Cancel();
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            packets.SetLength(0);
        }
    }
}
