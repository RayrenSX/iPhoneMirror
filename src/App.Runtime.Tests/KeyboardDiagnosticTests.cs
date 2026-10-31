using System.IO;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestFunctionKeyRoutingDiagnostics()
    {
        var router = new KeyboardInputRouter();
        router.CompleteHandoff(router.BeginHandoff(KeyboardInputMode.Direct));
        var available = true;
        var executed = 0;
        var checkedAvailability = 0;
        router.Shortcuts.Configure([new(new(0, 0x70), "Home", () =>
            { checkedAvailability++; return available; }, () => executed++)]);
        KeyboardRouteDecision last = default;
        void Key(bool down) => router.RouteEvent(new(0x70, 0, false), down,
            (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { }, diagnose: d => last = d);
        Key(true);
        MappingAssert(executed == 1 && checkedAvailability == 1 && last.Reason == "shortcut_matched",
            "Diagnostic matching changed binding evaluation or execution.");
        // Deliberately omit up. A second down cannot tell a real hold from a
        // lost release, but must now report why it did not execute again.
        Key(true);
        MappingAssert(executed == 1 && last.Reason == "repeated_down" && last.Owner == KeyboardEventOwner.Shortcut,
            "A retained F-key lifetime was not diagnosed as a repeat.");
        Key(false); Key(true); Key(false);
        MappingAssert(executed == 2 && router.PressedKeyCount == 0,
            "A fresh F-key press failed after the retained lifetime was released.");
        available = false;
        Key(true);
        MappingAssert(last.Reason == "shortcut_unavailable" && last.Owner == KeyboardEventOwner.Windows && executed == 2,
            "Unavailable shortcut diagnostics changed the fallback route.");
        Key(false);
        available = true;
        var generation = router.BeginHandoff(KeyboardInputMode.Direct);
        Key(true);
        MappingAssert(last.Reason == "handoff_pending" && executed == 2, "Pending ownership was not distinguished.");
        router.CompleteHandoff(generation);
        Key(true);
        MappingAssert(last.Reason == "repeated_down" && last.Owner == KeyboardEventOwner.Retired,
            "A handoff-held key was revived before its release.");
        Key(false); Key(true); Key(false);
        MappingAssert(executed == 3, "Fresh key failed after ownership recovery.");
        router.Shortcuts.Match(0x70, KeyboardShortcut.Control, out var mismatch);
        router.Shortcuts.Match(0x71, 0, out var unbound);
        MappingAssert(mismatch == "modifiers_mismatch" && unbound == "unbound_key",
            "Binding mismatch reasons were conflated.");
        Console.WriteLine("PASS F-key diagnostics: missing-up repeat, release recovery, unavailable binding, handoff quarantine, mismatch; no extra execution.");
    }

    private static void TestFunctionKeyHookDiagnostics(MainWindow main, string udid, nint hwnd,
        Action<nint> foreground, Action finish)
    {
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var testBindings = bindings.ToDictionary(pair => pair.Key, _ => KeyboardShortcut.Unbound);
        testBindings[BluetoothShortcutAction.Home] = new(0, 0x70);
        var data = Activator.CreateInstance(typeof(MainWindow).GetNestedType("LowLevelKeyboardData", KeyboardTestMembers)!)!;
        SetKeyboardField(data, "VirtualKey", 0x70u);
        SetKeyboardField(data, "Flags", 0x10u);
        void Key(bool down) => KeyboardCall(main, "ProcessKeyboardHook", data, down ? (nint)0x100 : (nint)0x101);
        string[] Logs() => File.ReadAllLines(DiagnosticLogger.ReverseControlLogPath)
            .Where(line => line.Contains("event=function_key_route ")).ToArray();
        try
        {
            foreground(hwnd);
            KeyboardCall(main, "FocusControlDevice", udid, (nint)0);
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            KeyboardCall(main, "ConfigureKeyboardShortcuts", testBindings);
            finish();
            var before = Logs().Length;
            SetKeyboardField(main, "_keyboardFocusSuspended", true);
            foreground((nint)999);
            Key(true); Key(false);
            finish();
            var blocked = Logs().Skip(before).ToArray();
            MappingAssert(blocked.Length == 2 && blocked.All(line => line.Contains("decision=injected_input_guard") &&
                line.Contains("focus_suspended=True") && line.Contains("source=hook")),
                "Injected F-key down/up disappeared before routing diagnostics.");
            foreground(hwnd);
            KeyboardCall(main, "RecoverKeyboardAvailability");
            finish();
            before = Logs().Length;
            Key(true);
            for (var i = 0; i < 130; i++) Key(true);
            Key(false);
            finish();
            var held = Logs().Skip(before).ToArray();
            MappingAssert(held.Length == 5 && held.Count(line => line.Contains("decision=repeated_down")) == 3 &&
                held.Any(line => line.Contains("decision=shortcut_matched")) &&
                held.Any(line => line.Contains("down=False") && line.Contains("decision=key_released")),
                "F-key hold diagnostics omitted the first repeat/up or logged every repeat.");
            // Hardware input reaches matching even when unavailable. Its
            // result must distinguish matching failure from injected filtering.
            SetKeyboardField(data, "Flags", 0u);
            foreground((nint)999);
            before = Logs().Length;
            Key(true); Key(false); finish();
            MappingAssert(Logs().Skip(before).Any(line => line.Contains("decision=shortcut_unavailable") &&
                line.Contains("foreground=999") && line.Contains("allowed=False")),
                "Background F-key rejection did not retain the actual foreground snapshot.");
            Console.WriteLine("PASS hook diagnostics: injected focus refusal, recovered match, bounded repeat samples, physical background rejection.");
        }
        finally
        {
            foreground(hwnd);
            SetKeyboardField(main, "_keyboardFocusSuspended", false);
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
        }
    }
}
