using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "GetKeyboardState")]
    private static extern bool GetShortcutTestKeyboardState(byte[] state);
    [DllImport("user32.dll", EntryPoint = "SetKeyboardState")]
    private static extern bool SetShortcutTestKeyboardState(byte[] state);

    private static void WaitShortcutTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
        InteractionAssert(task.IsCompleted, "Shortcut operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static JsonElement[] ReadShortcutPackets(MemoryStream packets)
    {
        using var copy = new MemoryStream(packets.ToArray());
        using var reader = new BinaryReader(copy);
        var result = new List<JsonElement>();
        while (copy.Position < copy.Length)
        {
            using var doc = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
            result.Add(doc.RootElement.Clone());
        }
        return result.ToArray();
    }

    private static void TestShortcutReview(MainWindow window, Window other,
        string udid, MemoryStream packets, string mode)
    {
        var app = (App)Application.Current;
        var settings = app.UpdateSettings.Clone();
        var vm = KeyboardField(window, "_viewModel");
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(window, "_bluetoothShortcuts");
        var previous = new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(bindings);
        var mainHandle = new WindowInteropHelper(window).Handle;
        var independentHandle = new WindowInteropHelper(other).EnsureHandle();
        nint foreground = mainHandle;
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        KeyboardCall(window, "UnregisterConfiguredHotkeys");
        foreach (var action in Enum.GetValues<BluetoothShortcutAction>())
            bindings[action] = KeyboardShortcut.Unbound;
        try
        {
            if (mode == "Bluetooth")
            {
                TestShortcutEditorAndRegistration(window, other, bindings);
                TestStoredMouseBossKey();
                foreground = mainHandle;
                KeyboardCall(window, "FocusControlDevice", udid, (nint)0);
                AwaitMapping((Task)KeyboardField(window, "_keyboardHandoff"));
                TestBluetoothConsumerShortcuts(window, udid);
            }
            else
            {
                foreach (var (action, usage) in new[]
                {
                    (BluetoothShortcutAction.ControlCenter, 6),
                    (BluetoothShortcutAction.NotificationCenter, 17),
                    (BluetoothShortcutAction.Dock, 4),
                })
                {
                    packets.SetLength(0);
                    WaitShortcutTask((Task)KeyboardCall(window, "SendConfiguredSystemShortcutAsync", action)!);
                    var frames = ReadShortcutPackets(packets);
                    InteractionAssert(frames.Length == 4 &&
                        frames[0].GetProperty("usageCode").GetInt32() == 0x29D &&
                        frames[0].GetProperty("state").GetString() == "down" &&
                        frames[1].GetProperty("usages").EnumerateArray().Single().GetInt32() == usage &&
                        frames[2].GetProperty("usages").GetArrayLength() == 0 &&
                        frames[3].GetProperty("usageCode").GetInt32() == 0x29D &&
                        frames[3].GetProperty("state").GetString() == "up",
                        $"{mode}/{action}: the keyboard key must be enclosed by Globe press/release.");
                }

                bindings[BluetoothShortcutAction.Home] = KeyboardShortcut.HomeDefault;
                bindings[BluetoothShortcutAction.AppSwitcher] = KeyboardShortcut.AppSwitcherDefault;
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
                foreach (var route in new[] { "raw", "main", "independent" })
                {
                    SetKeyboardField(window, "_activeControlWindow", route == "independent" ? independentHandle : (nint)0);
                    SetKeyboardField(window, "_activeControlUdid", route == "independent" ? udid : null);
                    foreground = route == "independent" ? independentHandle : mainHandle;
                    foreach (var button in new byte[] { 2, 4 })
                    {
                        packets.SetLength(0);
                        void Input(bool down)
                        {
                            var e = new PreviewPointerEventArgs(down ? PreviewPointerKind.ButtonDown :
                                PreviewPointerKind.ButtonUp, 20, 20, button, 0, 100, 200);
                            if (route == "raw") KeyboardCall(window, "HandleRawButton", button, down);
                            else if (route == "main") KeyboardCall(window, "OnControlPointerInput", null, e);
                            else KeyboardCall(window, "OnIndependentPointerInput", udid, e);
                        }
                        Input(true);
                        Input(true); // Duplicate notification / held button.
                        Input(false);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                        var frames = ReadShortcutPackets(packets);
                        InteractionAssert(frames.Length == (button == 2 ? 2 : 4) &&
                            frames.All(f => f.GetProperty("kind").GetString() == "button_event" &&
                                f.GetProperty("usageCode").GetInt32() == 0x40),
                            $"{mode}/{route}: mouse binding was skipped, duplicated or leaked a touch.");
                    }
                }

                foreground = mainHandle;
                SetKeyboardField(window, "_activeControlWindow", (nint)0);
                SetKeyboardField(window, "_activeControlUdid", null);
                bindings[BluetoothShortcutAction.Home] = new KeyboardShortcut(0, 0x77); // F8
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
                packets.SetLength(0);
                for (var i = 0; i < 2; i++)
                    KeyboardCall(window, "HandleControlKeyboardInput",
                        new PreviewKeyboardEventArgs(PreviewKeyboardKind.Down, 0x77), udid, false, mainHandle);
                KeyboardCall(window, "HandleControlKeyboardInput",
                    new PreviewKeyboardEventArgs(PreviewKeyboardKind.Up, 0x77), udid, false, mainHandle);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                InteractionAssert(ReadShortcutPackets(packets).Length == 2 &&
                    ReadShortcutPackets(packets).All(f => f.GetProperty("kind").GetString() == "button_event"),
                    "Native fallback repeated a shortcut or leaked F8 into the phone keyboard.");

                TestShortcutModifierTransitions(window, udid, packets, bindings);

                var host = KeyboardField(vm, mode == "Usb" ? "_usbTouchBridge" : "_wirelessTouchBridge");
                var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
                var writerGate = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
                packets.SetLength(0);
                writerGate.Wait();
                var queued = (Task)KeyboardCall(window, "SendConfiguredSystemShortcutAsync",
                    BluetoothShortcutAction.ControlCenter)!;
                foreground = independentHandle;
                writerGate.Release();
                WaitShortcutTask(queued);
                InteractionAssert(ReadShortcutPackets(packets).All(f =>
                    f.GetProperty("kind").GetString() == "keyboard_batch"
                        ? f.GetProperty("usages").GetArrayLength() == 0
                        : f.GetProperty("state").GetString() == "up"),
                    "An expired shortcut sent a press instead of only cleanup releases.");

                foreground = mainHandle;
                using var failedWriterStream = new FailFirstShortcutWriteStream();
                using var failedWriter = new StreamWriter(failedWriterStream, leaveOpen: true);
                var previousWriter = KeyboardField(bridge, "_stdin");
                SetKeyboardField(bridge, "_stdin", failedWriter);
                try
                {
                    WaitShortcutTask((Task)KeyboardCall(window, "SendConfiguredSystemShortcutAsync",
                        BluetoothShortcutAction.ControlCenter)!);
                    var releases = ReadShortcutPackets(failedWriterStream);
                    InteractionAssert(releases.Length == 0 && !bridge.IsReady,
                        "A failed framed write must revoke the bridge rather than append releases to a possibly truncated frame.");
                }
                finally
                {
                    SetKeyboardField(bridge, "_stdin", previousWriter);
                    SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
                }
                var cleanup = new List<string>();
                var cleanupRoute = new DirectKeyboardRoute(udid, "WiredDirect", new object(), 1, () => true,
                    (_, _, _) => { cleanup.Add("keyboard-up"); return Task.CompletedTask; },
                    (_, _, state, _) =>
                    {
                        cleanup.Add(state);
                        return state == "down" ? Task.FromException(new IOException("pre-write failure")) : Task.CompletedTask;
                    });
                try { WaitShortcutTask((Task)KeyboardCall(window, "SendUsbSystemShortcutAsync", (byte)6, cleanupRoute, (Func<bool>)(() => true))!); }
                catch (IOException) { }
                InteractionAssert(cleanup.SequenceEqual(new[] { "down", "keyboard-up", "up" }),
                    "Shortcut cleanup did not attempt both captured-session releases after a failed down.");
            }
            Console.WriteLine($"{mode}: shortcut regression checks passed.");
        }
        finally
        {
            if (KeyboardField(window, "_shortcutSettingsWindow") is Window editor) editor.Close();
            KeyboardCall(window, "UnregisterConfiguredHotkeys");
            bindings.Clear();
            foreach (var pair in previous) bindings[pair.Key] = pair.Value;
            KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
            app.RestoreUpdateSettings(settings);
            SetKeyboardField(window, "_activeControlWindow", (nint)0);
            SetKeyboardField(window, "_activeControlUdid", null);
            foreground = mainHandle;
        }
    }

    private static void TestShortcutModifierTransitions(MainWindow window, string udid,
        MemoryStream packets, Dictionary<BluetoothShortcutAction, KeyboardShortcut> bindings)
    {
        var savedState = new byte[256];
        InteractionAssert(GetShortcutTestKeyboardState(savedState), "Could not snapshot thread keyboard state.");
        var oldHome = bindings[BluetoothShortcutAction.Home];
        var oldRaw = (bool)KeyboardField(window, "_rawKeyboardInputEnabled");
        var hwnd = new WindowInteropHelper(window).Handle;
        var usages = (HashSet<byte>)KeyboardField(window, "_controlKeyboardUsages");
        var inputRouter = (KeyboardInputRouter)KeyboardField(window, "_keyboardRouter");
        void Modifiers(bool control)
        {
            var state = new byte[256];
            if (control) state[0x11] = state[0xA2] = 0x80;
            InteractionAssert(SetShortcutTestKeyboardState(state), "Could not set thread keyboard state.");
        }
        try
        {
            bindings[BluetoothShortcutAction.Home] = new(KeyboardShortcut.Control, 0x43);
            KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
            SetKeyboardField(window, "_rawKeyboardInputEnabled", true);
            foreach (var raw in new[] { false, true })
            foreach (var repeat in new[] { false, true })
            {
                void Key(bool down, int vk)
                {
                    if (raw)
                    {
                        var input = Activator.CreateInstance(typeof(MainWindow).GetNestedType("RawKeyboard", BindingFlags.NonPublic)!)!;
                        SetKeyboardField(input, "VirtualKey", (ushort)vk);
                        SetKeyboardField(input, "Message", down ? 0x100u : 0x101u);
                        KeyboardCall(window, "ProcessRawKeyboardInput", input);
                    }
                    else KeyboardCall(window, "HandleControlKeyboardInput",
                        new PreviewKeyboardEventArgs(down ? PreviewKeyboardKind.Down : PreviewKeyboardKind.Up, vk),
                        udid, false, hwnd);
                }
                inputRouter.ReleaseAllPressedKeys();
                ReleaseTestPhysicalKeys(window);
                packets.SetLength(0);
                Modifiers(false);
                Key(true, 0x43);
                InteractionAssert(usages.Contains(6), "Ordinary C-down was not forwarded.");
                Modifiers(true);
                Key(true, 0x11);
                if (repeat) Key(true, 0x43);
                Key(false, 0x43);
                Modifiers(false);
                Key(false, 0x11);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                var frames = ReadShortcutPackets(packets);
                InteractionAssert(usages.Count == 0 && frames.Length > 0 &&
                    frames.All(f => f.GetProperty("kind").GetString() == "keyboard_batch") &&
                    frames[^1].GetProperty("usages").GetArrayLength() == 0,
                    $"raw={raw}, repeat={repeat}: changing modifiers swallowed release or promoted an ordinary press to a shortcut.");

                // Reverse order: a shortcut must remain consumed after Ctrl
                // is released, including any C auto-repeat before its release.
                Modifiers(true);
                Key(true, 0x11);
                Key(true, 0x43);
                Modifiers(false);
                Key(false, 0x11);
                if (repeat) Key(true, 0x43);
                Key(false, 0x43);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                frames = ReadShortcutPackets(packets);
                InteractionAssert(usages.Count == 0 && inputRouter.PressedKeyCount == 0 &&
                    frames.Count(f => f.GetProperty("kind").GetString() == "button_event") == 2 &&
                    frames.Where(f => f.GetProperty("kind").GetString() == "keyboard_batch")
                        .SkipWhile(f => f.GetProperty("usages").GetArrayLength() != 0)
                        .All(f => !f.GetProperty("usages").EnumerateArray().Any(v => v.GetInt32() == 6)),
                    "Releasing Ctrl before the shortcut key leaked a repeated C or repeated the action.");
            }
        }
        finally
        {
            SetShortcutTestKeyboardState(savedState);
            bindings[BluetoothShortcutAction.Home] = oldHome;
            SetKeyboardField(window, "_rawKeyboardInputEnabled", oldRaw);
            KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
            KeyboardCall(window, "HandleControlKeyboardInput",
                new PreviewKeyboardEventArgs(PreviewKeyboardKind.Reset, 0), udid, false, hwnd);
        }
    }

    private static void TestStoredMouseBossKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shortcut-boss-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var button in new[] { KeyboardShortcut.MouseRight, KeyboardShortcut.MouseMiddle })
            foreach (var modifiers in new[] { 0u, KeyboardShortcut.Control | KeyboardShortcut.Alt })
            {
                var settings = new UpdateSettings
                {
                    BluetoothShortcutSchema = 6,
                    BluetoothModeShortcutSchema = 1,
                    BluetoothBossShortcutVirtualKey = (int)button,
                    BluetoothBossShortcutModifiers = (int)modifiers,
                    // The default boss combination already belongs to another action.
                    BluetoothHomeShortcutVirtualKey = (int)KeyboardShortcut.BossKeyDefault.VirtualKey,
                    BluetoothHomeShortcutModifiers = (int)KeyboardShortcut.BossKeyDefault.Modifiers,
                };
                InteractionAssert(KeyboardShortcut.FromSettings(settings, BluetoothShortcutAction.BossKey) == KeyboardShortcut.Unbound,
                    "In-memory legacy mouse boss binding was retained.");
                var path = Path.Combine(root, "settings.json");
                var store = new UpdateSettingsStore(path);
                store.Save(settings);
                var loaded = store.Load();
                var persisted = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path))!;
                foreach (var normalized in new[] { loaded, persisted })
                    InteractionAssert(normalized.BluetoothBossShortcutVirtualKey == 0 && normalized.BluetoothBossShortcutModifiers == 0 &&
                        normalized.BluetoothHomeShortcutVirtualKey == settings.BluetoothHomeShortcutVirtualKey &&
                        normalized.BluetoothHomeShortcutModifiers == settings.BluetoothHomeShortcutModifiers,
                        "Mouse boss migration was not persisted or overwrote another shortcut.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FailFirstShortcutWriteStream : MemoryStream
    {
        private bool _failed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                return ValueTask.FromException(new IOException("Injected shortcut write failure"));
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static void TestShortcutEditorAndRegistration(MainWindow window, Window other,
        Dictionary<BluetoothShortcutAction, KeyboardShortcut> bindings)
    {
        var register = typeof(MainWindow).GetMethod("RegisterHotKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var unregister = typeof(MainWindow).GetMethod("UnregisterHotKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var otherHandle = new WindowInteropHelper(other).Handle;
        const int probeId = 0x7B02;
        bool Probe(uint key, bool retain = false)
        {
            var ok = (bool)register.Invoke(null, [otherHandle, probeId, 6u, key])!;
            if (ok && !retain) unregister.Invoke(null, [otherHandle, probeId]);
            return ok;
        }
        var keys = Enumerable.Range(0x7C, 12).Select(v => (uint)v).Where(v => Probe(v)).Take(3).ToArray();
        InteractionAssert(keys.Length == 3, "No free combinations for shortcut registration checks.");
        bindings[BluetoothShortcutAction.BluetoothControl] = new(6, keys[0]);
        bindings[BluetoothShortcutAction.Home] = KeyboardShortcut.HomeDefault;
        bindings[BluetoothShortcutAction.AppSwitcher] = KeyboardShortcut.AppSwitcherDefault;
        KeyboardCall(window, "TryRegisterShortcutSet", bindings, BluetoothShortcutAction.BluetoothControl);
        InteractionAssert(Probe(keys[0]) && ((KeyboardInputRouter)KeyboardField(window, "_keyboardRouter"))
            .Shortcuts.Match((int)keys[0], 6) is not null, "Global shortcut is not exclusively owned by the router.");
        KeyboardCall(window, "ShowShortcutSettings");
        var editor = (ShortcutSettingsWindow)KeyboardField(window, "_shortcutSettingsWindow");
        try
        {
            InteractionAssert(Probe(keys[0]), "Opening the editor did not release global hotkeys.");
            KeyboardCall(window, "HandleConfiguredShortcut", BluetoothShortcutAction.BossKey);
            InteractionAssert(!(bool)KeyboardField(window, "_bossKeyHidden"), "A shortcut executed during recording.");
            var home = editor.Rows.Single(r => r.Action == BluetoothShortcutAction.Home);
            var mode = editor.Rows.Single(r => r.Action == BluetoothShortcutAction.BluetoothControl);
            var switcher = editor.Rows.Single(r => r.Action == BluetoothShortcutAction.AppSwitcher);
            var boss = editor.Rows.Single(r => r.Action == BluetoothShortcutAction.BossKey);
            foreach (var button in new[] { KeyboardShortcut.MouseRight, KeyboardShortcut.MouseMiddle })
            foreach (var modifiers in new[] { 0u, KeyboardShortcut.Control | KeyboardShortcut.Alt })
            {
                var mouse = new KeyboardShortcut(modifiers, button);
                InteractionAssert(!editor.TryAssignShortcut(boss, mouse) && !boss.Shortcut.IsBound &&
                    editor.StatusText == IPhoneMirror.App.Localization.LocalizationService.Get("ShortcutSettingsBossKeyKeyboardOnly"),
                    "Editor accepted a mouse boss key or omitted the keyboard-only explanation.");
                var invalid = new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(bindings)
                {
                    [BluetoothShortcutAction.Home] = KeyboardShortcut.Unbound,
                    [BluetoothShortcutAction.AppSwitcher] = KeyboardShortcut.Unbound,
                    [BluetoothShortcutAction.BossKey] = mouse,
                };
                InteractionAssert(KeyboardCall(window, "ApplyBluetoothShortcuts", invalid) is string &&
                    !(bool)KeyboardCall(window, "TryRegisterShortcutSetCore", invalid, BluetoothShortcutAction.BossKey, true)!,
                    "Save or registration accepted a mouse boss key outside the capture UI.");
            }
            InteractionAssert(editor.TryAssignShortcut(boss, KeyboardShortcut.BossKeyDefault) &&
                editor.TryAssignShortcut(boss, KeyboardShortcut.Unbound), "Valid keyboard boss key or unbinding was rejected.");
            InteractionAssert(!editor.TryAssignShortcut(home, mode.Shortcut) &&
                home.Shortcut == KeyboardShortcut.HomeDefault && editor.StatusText.Contains(mode.Label),
                "A duplicate keyboard shortcut replaced the prior binding or omitted its owner.");
            InteractionAssert(!editor.TryAssignShortcut(switcher, home.Shortcut), "Duplicate mouse shortcut accepted.");
            InteractionAssert(editor.TryAssignShortcut(home, KeyboardShortcut.Unbound) &&
                editor.TryAssignShortcut(switcher, KeyboardShortcut.HomeDefault), "Clearing did not free the shortcut.");
            KeyboardCall(editor, "OnResetRowClick", new Button { DataContext = home }, new RoutedEventArgs());
            InteractionAssert(!home.Shortcut.IsBound && !string.IsNullOrEmpty(editor.StatusText),
                "Reset introduced a duplicate mouse shortcut.");
            InteractionAssert(editor.TryAssignShortcut(mode, mode.Shortcut), "Re-entering the same row should succeed.");
            InteractionAssert(!KeyboardShortcut.TryCreate(Key.F12, ModifierKeys.Control, out _) &&
                !KeyboardShortcut.TryCreate(Key.F12, ModifierKeys.Control | ModifierKeys.Alt, out _) &&
                !editor.TryAssignShortcut(home, new KeyboardShortcut(2, 0x7B)), "Modified F12 accepted.");

            var duplicates = new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(bindings)
            {
                [BluetoothShortcutAction.Home] = bindings[BluetoothShortcutAction.BluetoothControl],
            };
            InteractionAssert(KeyboardCall(window, "ApplyBluetoothShortcuts", duplicates) is string,
                "Save accepted duplicate bindings supplied outside the capture UI.");
            home.Shortcut = mode.Shortcut;
            KeyboardCall(editor, "OnSaveClick", null, new RoutedEventArgs());
            InteractionAssert(editor.IsVisible && !string.IsNullOrEmpty(editor.StatusText),
                "The editor closed after trying to save duplicate bindings.");

            var next = new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(bindings)
            {
                [BluetoothShortcutAction.Home] = new(6, keys[1]),
            };
            InteractionAssert(Probe(keys[1], retain: true), "Could not reserve a conflicting external hotkey.");
            try
            {
                InteractionAssert(KeyboardCall(window, "ApplyBluetoothShortcuts", next) is string &&
                    bindings[BluetoothShortcutAction.Home] == KeyboardShortcut.HomeDefault,
                    "Saving from an unfocused preview ignored the external hotkey conflict.");
            }
            finally { unregister.Invoke(null, [otherHandle, probeId]); }
            InteractionAssert(KeyboardCall(window, "ApplyBluetoothShortcuts", next) is null,
                "A valid shortcut set did not save after the external conflict was removed.");
            InteractionAssert(Probe(keys[0]) && Probe(keys[1]), "Save re-enabled hotkeys while recording.");
        }
        finally { editor.Close(); }
        InteractionAssert(Probe(keys[0]) && ((KeyboardInputRouter)KeyboardField(window, "_keyboardRouter"))
            .Shortcuts.Match((int)keys[0], 6) is not null, "Closing the editor failed to restore the routed global shortcut.");
    }

    private static void TestBluetoothConsumerShortcuts(MainWindow window, string udid)
    {
        var vm = KeyboardField(window, "_viewModel");
        var service = KeyboardField(vm, "_bluetoothControl");
        var oldTarget = KeyboardField(service, "_targetDeviceUdid");
        var oldDisposed = KeyboardField(service, "_disposed");
        SetKeyboardField(service, "_disposed", 0);
        SetKeyboardField(service, "_targetDeviceUdid", udid);
        SetKeyboardField(service, "_mousePumpRunning", true);
        var queue = KeyboardField(service, "_keyboardPriorityReports");
        ITuple Dequeue()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((int)KeyboardField(queue, "Count") == 0 && DateTime.UtcNow < deadline)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert((int)KeyboardField(queue, "Count") != 0, "Missing Bluetooth consumer report.");
            return (ITuple)queue.GetType().GetMethod("Dequeue")!.Invoke(queue, null)!;
        }
        try
        {
            foreach (var (action, usage, failPress) in new[]
            {
                (BluetoothShortcutAction.VolumeUp, 0xE9, false),
                (BluetoothShortcutAction.VolumeDown, 0xEA, true),
                (BluetoothShortcutAction.LockScreen, 0x30, false),
            })
            {
                var task = (Task)KeyboardCall(window, "SendConfiguredSystemShortcutAsync", action)!;
                var press = Dequeue();
                var bytes = (byte[])press[1]!;
                InteractionAssert(Convert.ToInt32(press[0]) == 4 && bytes.SequenceEqual(new byte[] { (byte)usage, 0 }),
                    $"{action}: wrong Bluetooth consumer report.");
                var completion = (TaskCompletionSource<bool>)press[2]!;
                if (failPress) completion.SetException(new IOException("Injected notification failure"));
                else completion.SetResult(true);
                var release = Dequeue();
                InteractionAssert(((byte[])release[1]!).All(b => b == 0) && release[3] is Func<bool> session && session(),
                    $"{action}: missing session-bound release after press/failure.");
                ((TaskCompletionSource<bool>)release[2]!).SetResult(true);
                WaitShortcutTask(task);
            }
        }
        finally
        {
            SetKeyboardField(service, "_mousePumpRunning", false);
            SetKeyboardField(service, "_targetDeviceUdid", oldTarget);
            SetKeyboardField(service, "_disposed", oldDisposed);
        }
    }
}
