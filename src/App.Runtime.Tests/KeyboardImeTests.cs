using System.IO;
using System.Windows;
using IPhoneMirror.App;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestImeKeyboardForwarding(MainWindow window, Window other,
        MemoryStream packets, Action<Window> focus, bool verifyPackets)
    {
        focus(window);
        KeyboardCall(window, "ResetKeyboardOwnership");
        ReleaseTestPhysicalKeys(window);
        KeyboardCall(window, "TryEnterDirectKeyboardInputMode");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        var data = Activator.CreateInstance(typeof(MainWindow).GetNestedType("LowLevelKeyboardData", KeyboardTestMembers)!)!;
        var keys = (HashSet<byte>)KeyboardField(window, "_controlKeyboardUsages");
        var modifiers = (HashSet<int>)KeyboardField(window, "_controlModifierKeys");
        var hook = KeyboardField(window, "_keyboardHook");
        bool Hook(int vk, int scan, bool down, bool extended = false)
        {
            SetKeyboardField(data, "VirtualKey", (uint)vk);
            SetKeyboardField(data, "ScanCode", (uint)scan);
            SetKeyboardField(data, "Flags", extended ? 1u : 0u);
            return (bool)KeyboardCall(window, "ProcessKeyboardHook", data, down ? (nint)0x100 : (nint)0x101)!;
        }
        try
        {
            foreach (var (vk, scan, usage, extended) in new[]
                { (0x41, 0x1E, 0x04, false), (0x4E, 0x31, 0x11, false),
                  (0x31, 0x02, 0x1E, false), (0x20, 0x39, 0x2C, false),
                  (0x25, 0x4B, 0x50, true) })
            {
                packets.SetLength(0);
                InteractionAssert(Hook(0xE5, scan, true, extended), "IME-processed key-down was not owned by the preview.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert(keys.Contains((byte)usage), $"IME-processed physical key did not reach the device keyboard state: vk={vk:X2}, scan={scan:X2}, keys={string.Join(',', keys)}.");
                InteractionAssert(Hook(vk, scan, false, extended), "Physical key-up lost its IME-processed down.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert(keys.Count == 0, "IME-processed key remained held after physical key-up.");
                if (verifyPackets)
                    InteractionAssert(MappingFrames(packets).Any(f => f.TryGetProperty("usages", out var usages) &&
                        usages.EnumerateArray().Any(v => v.GetInt32() == usage)), "IME key never reached the transport.");
                Hook(vk, scan, true, extended);
                Hook(0xE5, scan, false, extended);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                InteractionAssert(keys.Count == 0, "IME-processed key-up did not release a physical key-down.");
            }
            Hook(0xE5, 0x36, true);
            Hook(0xE5, 0x1E, true);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert(modifiers.Contains(0xA1) && keys.Contains(4), "IME Shift+A lost its right-hand modifier identity.");
            Hook(0x41, 0x1E, false);
            Hook(0xA1, 0x36, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert(modifiers.Count == 0 && keys.Count == 0, "IME chord left a modifier held.");
            InteractionAssert(!Hook(0xE5, 0, true) && !Hook(0xE5, 0, false),
                "IME sentinel without a scan code must remain available to Windows.");
            Hook(0x61, 0x4F, true); // NumLock: the same scan code can mean End or numpad 1.
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(keys.Contains(0x59), "Ordinary numpad virtual key was remapped through its scan code.");
            Hook(0x61, 0x4F, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));

            // Native/window and Raw Input fallback share the same restoration.
            SetKeyboardField(window, "_keyboardHook", (nint)0);
            foreach (var kind in new[] { PreviewKeyboardKind.Down, PreviewKeyboardKind.Up })
            {
                KeyboardCall(window, "HandleControlKeyboardInput", new PreviewKeyboardEventArgs(kind, 0xE5, 0x1E),
                    null, false, null);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert(keys.Contains(4) == (kind == PreviewKeyboardKind.Down), "Native IME fallback lost a key transition.");
            }
            var rawEnabled = KeyboardField(window, "_rawKeyboardInputEnabled");
            try
            {
                SetKeyboardField(window, "_rawKeyboardInputEnabled", true);
                var raw = Activator.CreateInstance(typeof(MainWindow).GetNestedType("RawKeyboard", KeyboardTestMembers)!)!;
                SetKeyboardField(raw, "VirtualKey", (ushort)0xE5);
                SetKeyboardField(raw, "MakeCode", (ushort)0x1E);
                foreach (var down in new[] { true, false })
                {
                    SetKeyboardField(raw, "Flags", down ? (ushort)0 : (ushort)1);
                    SetKeyboardField(raw, "Message", down ? 0x100u : 0x101u);
                    KeyboardCall(window, "ProcessRawKeyboardInput", raw);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                    InteractionAssert(keys.Contains(4) == down, "Raw Input IME fallback lost a key transition.");
                }
            }
            finally { SetKeyboardField(window, "_rawKeyboardInputEnabled", rawEnabled); }
            focus(other);
            InteractionAssert(!Hook(0xE5, 0x1E, true), "Background IME key was swallowed.");
            InteractionAssert(keys.Count == 0, "Background IME key reached the phone.");
            Hook(0x41, 0x1E, false);
            focus(window);
            Console.WriteLine("IME physical-key forwarding: processed downs/ups, Shift, extended keys, native fallback and focus guards passed.");
        }
        finally
        {
            SetKeyboardField(window, "_keyboardHook", hook);
            KeyboardCall(window, "ResetKeyboardOwnership");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        }
    }
}
