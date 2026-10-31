using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static MainWindow? _shortcutFaultMain;
    private static bool _dropShortcutHookUp;
    private static int _dropShortcutHookDownKey;
    private static nint ShortcutFaultHook(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x104 &&
            _dropShortcutHookDownKey != 0 && Marshal.ReadInt32(data) == _dropShortcutHookDownKey)
        {
            _dropShortcutHookDownKey = 0;
            return RecoveryTestNextHook(0, code, message, data);
        }
        if (code >= 0 && message == 0x101 && Marshal.ReadInt32(data) == 0x70 && _dropShortcutHookUp)
        {
            _dropShortcutHookUp = false;
            return RecoveryTestNextHook(0, code, message, data);
        }
        return (nint)KeyboardCall(_shortcutFaultMain!, "KeyboardHookProcedure", code, message, data)!;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ShortcutLiveInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(12)] internal uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint ShortcutLiveSend(uint count, ShortcutLiveInput[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short ShortcutLiveKeyState(int key);

    // Opt-in real phone test. Faults affect the test process's hook/state only;
    // all keystrokes enter Windows SendInput and the production hook/router.
    private static void ExerciseLiveShortcutRecovery(MainWindow main, MainViewModel vm, string output, bool wireless, bool timing = false, int soakSeconds = 0)
    {
        var app = (App)Application.Current;
        var originalPreviewMode = app.IsUiPreviewMode;
        var originalProc = (Delegate)KeyboardField(main, "_keyboardHookProc");
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var mapping = (KeyboardMappingSettings)KeyboardField(main, "_mappingSettings");
        var originalMappingEnabled = mapping.Enabled;
        var manager = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        var target = vm.SelectedDevice!.Udid;
        var result = new List<string>();
        bool Wait(Func<bool> check, int seconds = 10)
        {
            var watch = Stopwatch.StartNew();
            while (!check() && watch.Elapsed.TotalSeconds < seconds) AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            return check();
        }
        void Complete(Task task, int seconds = 15)
        { MappingAssert(Wait(() => task.IsCompleted, seconds), "Live shortcut operation timed out."); task.GetAwaiter().GetResult(); }
        int Sent() => File.ReadLines(DiagnosticLogger.Path).Count(line => line.Contains("system_shortcut_sent action="));
        int RecoveredPresses() => File.Exists(DiagnosticLogger.ReverseControlLogPath)
            ? File.ReadLines(DiagnosticLogger.ReverseControlLogPath).Count(line =>
                line.Contains("source=window_press ") && line.Contains("decision=shortcut_press_recovered ")) : 0;
        void Input(int vk, bool down)
        {
            MappingAssert(ShortcutLiveSend(1, [new() { Type = 1, Key = (ushort)vk, Flags = down ? 0u : 2u }], 40) == 1,
                $"SendInput failed: {Marshal.GetLastWin32Error()}");
        }
        void Press(int vk, int heldMilliseconds = 70) => Complete(Task.Run(() =>
        { Input(vk, true); try { Thread.Sleep(heldMilliseconds); } finally { Input(vk, false); } }));
        void Pass(string label)
        {
            result.Add(label); Console.WriteLine("LIVE PASS " + label);
            File.WriteAllLines(Path.Combine(output, "shortcut-results.txt"), result);
        }
        try
        {
            Complete(manager.ShowAsync(vm.SelectedDevice));
            manager.PrepareUsbControlWindow(target);
            foreach (var notice in app.Windows.OfType<IPhoneMirror.App.Windows.ReverseControlStatusWindow>().ToArray()) notice.Close();
            manager.Activate(target);
            var window = manager.GetWindowHandle(target);
            var foregroundThread = MappingWindowThread(MappingGetForeground(), out _);
            var currentThread = MappingCurrentThread();
            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                MappingAttachInput(currentThread, foregroundThread, true);
            try { MappingSetForeground(window); }
            finally { if (attached) MappingAttachInput(currentThread, foregroundThread, false); }
            MappingAssert(Wait(() => MappingGetForeground() == window), "Live shortcut preview is not foreground.");
            KeyboardCall(main, "FocusControlDevice", target, window);
            Complete((Task)KeyboardField(main, "_keyboardHandoff"));
            mapping.Enabled = false;
            var configured = bindings.ToDictionary(pair => pair.Key, _ => KeyboardShortcut.Unbound);
            configured[BluetoothShortcutAction.Home] = new(0, 0x70);
            configured[BluetoothShortcutAction.ControlCenter] = new(0, 0x71);
            if (soakSeconds > 0)
            {
                configured[BluetoothShortcutAction.NotificationCenter] = new(0, 0x72);
                configured[BluetoothShortcutAction.AppSwitcher] = new(0, 0x73);
                configured[BluetoothShortcutAction.Dock] = new(0, 0x74);
            }
            KeyboardCall(main, "ConfigureKeyboardShortcuts", configured);
            app.IsUiPreviewMode = false;
            _shortcutFaultMain = main;
            var method = typeof(Program).GetMethod(nameof(ShortcutFaultHook), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            SetKeyboardField(main, "_keyboardHookProc", Delegate.CreateDelegate(originalProc.GetType(), method));
            KeyboardCall(main, "ReconcileKeyboardHook");
            MappingAssert((bool)KeyboardCall(main, "CanForwardControlKeyboard", target, window)!, "Live window input policy refused test.");
            MappingAssert(vm.CaptureDirectKeyboardRoute(target)?.Transport == (wireless ? "WirelessDirect" : "WiredDirect"),
                "Shortcut hardware test did not acquire its requested real transport.");
            if (soakSeconds > 0)
            {
                RunLiveShortcutSoak(main, vm, output, wireless, soakSeconds, window);
                return;
            }

            if (timing)
            {
                if (File.Exists(Path.Combine(output, "button-timing-mode")))
                {
                    RunLiveButtonTiming(main, vm, output, window);
                    return;
                }
                // A/B the former simultaneous chord with the production
                // staggered sequence on the same route/foreground guard.
                foreach (var trial in Enumerable.Range(0, 60))
                {
                    MappingAssert(Wait(() => vm.CaptureDirectKeyboardRoute(target)?.IsCurrent() == true, 45),
                        "Timing route did not recover.");
                    var homeCount = Sent();
                    Press(0x70);
                    MappingAssert(Wait(() => Sent() > homeCount), "Timing Home was not sent.");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(800));
                    var hold = trial % 2 == 0 ? 20 : 100;
                    var stem = Path.Combine(output, $"timing-{trial + 1:D2}-{hold}");
                    vm.CaptureScreenshot(stem + "-before.png");
                    var route = vm.CaptureDirectKeyboardRoute(target);
                    MappingAssert(route is not null && route.IsCurrent(), "Timing test route became unavailable.");
                    var guard = (Func<bool>)KeyboardCall(main, "CapturePointerSendGuard", window)!;
                    async Task Send()
                    {
                        var gate = (SemaphoreSlim)KeyboardCall(main, "KeyboardSessionGate", route!)!;
                        await gate.WaitAsync();
                        try
                        {
                            if (hold == 100)
                                await (Task)KeyboardCall(main, "SendUsbSystemShortcutAsync", (byte)0x11, route!, guard)!;
                            else
                            {
                                try
                                {
                                    await route!.SendButtonAsync!(CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                                        CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage, "down", guard);
                                    await route.SendAsync(0, [0x11], guard);
                                    await Task.Delay(hold);
                                }
                                finally
                                {
                                    var keyUp = route!.SendAsync(0, [], null);
                                    var globeUp = route.SendButtonAsync!(CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                                        CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage, "up", null);
                                    await Task.WhenAll(keyUp, globeUp);
                                }
                            }
                        }
                        finally { gate.Release(); }
                    }
                    Complete(Send());
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(1600));
                    vm.CaptureScreenshot(stem + "-after.png");
                    Console.WriteLine($"TIMING trial={trial + 1} hold_ms={hold} route_current={route!.IsCurrent()} guard={guard()}");
                }
                Press(0x70);
                Pass("60 alternating Notification Center timing trials captured");
                return;
            }

            var before = Sent(); Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 1), "Baseline Home was not sent.");
            vm.CaptureScreenshot(Path.Combine(output, "shortcut-home.png"));
            Pass($"{(wireless ? "Wireless" : "USB")} baseline real SendInput -> hook -> phone command");
            before = Sent();
            SetKeyboardField(main, "_keyboardFocusSuspended", true);
            Press(0x71);
            MappingAssert(Wait(() => Sent() == before + 1), "Stale focus blocked real foreground press.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(700));
            vm.CaptureScreenshot(Path.Combine(output, "shortcut-control-center.png"));
            Pass("stale focus flag: first foreground press sent without restart");
            before = Sent();
            var recoveredBefore = RecoveredPresses();
            _dropShortcutHookDownKey = 0x70;
            Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 1 && RecoveredPresses() == recoveredBefore + 1 &&
                    !router.HasPressedKey(new(0x70, 0, false)) && ShortcutLiveKeyState(0x70) >= 0) && _dropShortcutHookDownKey == 0,
                "Original native window down did not recover the deliberately omitted hook down exactly once.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(700));
            vm.CaptureScreenshot(Path.Combine(output, "shortcut-recovered-down-home.png"));
            Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 2), "Fresh press was swallowed after hook-down loss.");
            Pass("one omitted hook down: original window message recovered one command; next real press sent once");
            before = Sent(); _dropShortcutHookUp = true; Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 1 && !router.HasPressedKey(new(0x70, 0, false))),
                "Native window up did not clear the deliberately omitted hook up.");
            MappingAssert(!_dropShortcutHookUp, "Test fault was not exercised by the real hook.");
            Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 2), "Fresh press was swallowed after hook-up loss.");
            Pass("one omitted hook up: real window release cleared lifetime; next real press sent once");
            before = Sent(); recoveredBefore = RecoveredPresses(); _dropShortcutHookDownKey = 0x71;
            Press(0x71, 2600); AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
            MappingAssert(Sent() == before + 1 && RecoveredPresses() == recoveredBefore + 1 &&
                    _dropShortcutHookDownKey == 0 && !router.HasPressedKey(new(0x71, 0, false)) && ShortcutLiveKeyState(0x71) >= 0,
                "Recovered shortcut down repeated or retained ownership across hook renewal.");
            Pass("omitted hook down with 2.6-second hold: window recovery and hook renewal execute once");
            before = Sent(); Press(0x71, 2600); AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
            MappingAssert(Sent() == before + 1 && !router.HasPressedKey(new(0x71, 0, false)),
                "Long hold across watchdog renewal repeated or retained the shortcut.");
            Pass("2.6-second physical input hold across hook renewal: exactly one command");
            var oldHook = (nint)KeyboardField(main, "_keyboardHook");
            MappingAssert(typeof(MainWindow).GetMethod("UnhookWindowsHookEx", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [oldHook]) is true,
                "Could not remove live test hook.");
            MappingAssert(Wait(() => (nint)KeyboardField(main, "_keyboardHook") != oldHook, 6),
                "Watchdog did not replace the stale nonzero HHOOK.");
            Complete((Task)KeyboardField(main, "_keyboardHandoff"));
            MappingAssert(Wait(() => vm.CaptureDirectKeyboardRoute(target) is { } restored && restored.IsCurrent(), 45),
                "Real control transport did not recover before the post-hook-restoration press.");
            Complete((Task)KeyboardField(main, "_keyboardHandoff"));
            before = Sent(); Press(0x70);
            MappingAssert(Wait(() => Sent() == before + 1), "Real press failed after watchdog recovery.");
            Pass("silently removed hook: timer renewed native subscription; next real press sent once");
        }
        finally
        {
            _dropShortcutHookUp = false;
            _dropShortcutHookDownKey = 0;
            app.IsUiPreviewMode = originalPreviewMode;
            mapping.Enabled = originalMappingEnabled;
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            // A saved configuration can keep mappings enabled in preview
            // mode. Remove the native fault callback before clearing its test
            // target, then let production reconciliation restore the owner.
            var hook = (nint)KeyboardField(main, "_keyboardHook");
            if (hook != 0)
            {
                typeof(MainWindow).GetMethod("UnhookWindowsHookEx", System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [hook]);
                SetKeyboardField(main, "_keyboardHook", (nint)0);
            }
            SetKeyboardField(main, "_keyboardHookProc", originalProc);
            KeyboardCall(main, "ReconcileKeyboardHook");
            _shortcutFaultMain = null;
        }
    }
}
