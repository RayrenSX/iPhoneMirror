using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestUnifiedKeyboardDispatch()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var hwnd = new WindowInteropHelper(main).Handle;
        nint foreground = hwnd;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => (nint)123));
        var preview = (NativePreviewHost)main.FindName("MainPreviewHost");
        SetKeyboardField(preview, "_window", (nint)123);
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var phone = (DeviceViewModel)ctor.Invoke(["router-phone", "Router fixture", "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", phone);
        SetKeyboardField(vm, "_isCapturing", true);
        SetKeyboardField(vm, "_sourceVideoWidth", 390u); SetKeyboardField(vm, "_sourceVideoHeight", 844u);
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        sessions.Set(new() { Udid = phone.Udid, Handle = new NativeSessionHandle(456, ownsHandle: false) });
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        SetKeyboardField(bridge, "_stdin", writer); SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(host, "<State>k__BackingField", ReverseControlState.Ready);
        var controls = (IDictionary)KeyboardField(vm, "_deviceControls");
        var control = new DeviceControlSession(phone.Udid) { AppleUdid = phone.Udid, WiredBridge = host,
            WirelessBridge = host, WiredConnected = true, WirelessConnected = true };
        controls[phone.Udid] = control;
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        var settings = new KeyboardMappingSettings { Enabled = true, SuppressOriginalKey = true,
            Mappings = new[] { 0x57, 0x41, 0x53, 0x44, 0x43, 0x56 }.Select(vk => new KeyboardMappingEntry
                { Key = new(vk, 0, false), Action = MappedTouchAction.HoldUntilRelease, DeviceCoordinates = true }).ToList() };
        var shortcuts = Enum.GetValues<BluetoothShortcutAction>().ToDictionary(a => a, _ => KeyboardShortcut.Unbound);
        shortcuts[BluetoothShortcutAction.Home] = new(KeyboardShortcut.Control, 0x53);
        var stored = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        foreach (var binding in shortcuts) stored[binding.Key] = binding.Value;
        KeyboardCall(main, "ConfigureKeyboardShortcuts", shortcuts);
        // Actual unmanaged hook callback, with non-injected test packets. No
        // global SendInput and no real phone: only the bridge writer is replaced.
        var data = Marshal.AllocHGlobal(24);
        void Key(int vk, bool down)
        {
            for (var offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(data, offset, 0);
            Marshal.WriteInt32(data, 0, vk);
            KeyboardCall(main, "KeyboardHookProcedure", 0, down ? (nint)0x100 : (nint)0x101, data);
        }
        void SetMapping(bool enabled)
        {
            settings.Enabled = enabled; SetKeyboardField(main, "_mappingSettings", settings);
            KeyboardCall(main, enabled ? "TryEnterKeyboardMappingInputMode" : "LeaveKeyboardMappingInputMode");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff")); ReleaseTestPhysicalKeys(main);
        }
        void Finish() => AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        void FinishClipboard()
        {
            Finish();
            AwaitMapping(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            Finish();
        }
        const string clipboardText = "中文 / café / 😀";
        SetKeyboardField(main, "_readClipboardText", (Func<string>)(() => clipboardText));
        try
        {
            foreach (var mode in new[] { ReverseControlMode.Usb, ReverseControlMode.Wireless })
            {
                control.WiredEnabled = mode == ReverseControlMode.Usb;
                control.WirelessEnabled = mode == ReverseControlMode.Wireless;
                control.Router.Begin(phone.Udid, mode);
                KeyboardCall(main, "FocusControlDevice", phone.Udid, (nint)0);
                SetMapping(true);
                MappingAssert(vm.GetMappingTargetStatus() == "MappingReady", "Fixture mapping route is not ready.");
                packets.SetLength(0);
                foreach (var letter in new[] { 0x43, 0x56 })
                { Key(letter, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); Key(letter, false); Finish(); }
                MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("points", out _)) == 4,
                    $"{mode}: adding clipboard chords broke plain C/V touch mappings.");
                packets.SetLength(0);
                Key(0xA2, true); Key(0x56, true);
                for (var repeat = 0; repeat < 20; repeat++) Key(0x56, true);
                Key(0xA2, false); Key(0x56, false); FinishClipboard();
                var clipboardFrames = MappingFrames(packets);
                MappingAssert(clipboardFrames.Count == 1 &&
                    clipboardFrames[0].GetProperty("kind").GetString() == "paste_text" &&
                    clipboardFrames[0].GetProperty("text").GetString() == clipboardText,
                    $"{mode}: mapping mode Ctrl+V failed to paste once without mapped touches or modifier leakage.");
                packets.SetLength(0);
                Key(0xA3, true); Key(0x43, true);
                for (var repeat = 0; repeat < 20; repeat++) Key(0x43, true);
                Key(0x43, false); Key(0xA3, false); FinishClipboard();
                clipboardFrames = MappingFrames(packets);
                var copyReports = clipboardFrames.Where(f => f.TryGetProperty("usages", out _))
                    .Select(f => string.Join(",", f.GetProperty("usages").EnumerateArray().Select(v => v.GetInt32()).Order())).ToArray();
                MappingAssert(copyReports.SequenceEqual(new[] { "227", "6,227", "227", "" }) &&
                    clipboardFrames.Count == 5 && clipboardFrames[^1].GetProperty("kind").GetString() == "read_clipboard",
                    $"{mode}: Ctrl+C did not send one released Command+C and refresh the phone clipboard. Frames: {string.Join(" | ", clipboardFrames.Select(f => f.ToString()))}");
                SetKeyboardField(main, "_isSettingsPanelVisible", true);
                SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => 0));
                packets.SetLength(0);
                Key(0xA2, true); Key(0x43, true); Key(0x43, false); Key(0x56, true); Key(0x56, false); Key(0xA2, false); FinishClipboard();
                MappingAssert(packets.Length == 0, $"{mode}: a Windows editor's copy/paste reached the phone.");
                SetKeyboardField(main, "_isSettingsPanelVisible", false);
                SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => (nint)123));
                Key(0xA2, true); Key(0x56, true); Key(0x56, false); Key(0xA2, false);
                KeyboardCall(main, "ResetKeyboardOwnership");
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff")); FinishClipboard();
                MappingAssert(MappingFrames(packets).All(f => !f.TryGetProperty("text", out _)),
                    $"{mode}: an obsolete queued paste survived ownership reset.");
                Console.WriteLine($"PASS {mode}: mapping C/V + Ctrl+C/Ctrl+V, Unicode paste, repeat suppression, Windows editor isolation and stale paste cancellation.");
                packets.SetLength(0);
                for (var i = 0; i < 2; i++)
                {
                    Key(0x57, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); Key(0x57, false); Finish();
                    Key(0xA2, true); Key(0x53, true); Key(0x53, true); Key(0xA2, false); Key(0x53, false); Finish();
                }
                var frames = MappingFrames(packets);
                MappingAssert(frames.Count(f => f.TryGetProperty("points", out _)) == 4 &&
                    frames.Count(f => f.TryGetProperty("state", out _)) == 4 &&
                    frames.All(f => !f.TryGetProperty("usages", out _)),
                    $"{mode}: W / Ctrl+S leaked a keyboard report, mapping or repeated shortcut.");
                // Verify the real built-in Screenshot handler is selected
                // while S is mapped. Hold its busy gate to avoid opening a
                // file dialog or pretending this fixture has a native frame.
                shortcuts[BluetoothShortcutAction.Home] = KeyboardShortcut.Unbound;
                stored[BluetoothShortcutAction.Home] = KeyboardShortcut.Unbound;
                KeyboardCall(main, "ConfigureKeyboardShortcuts", shortcuts);
                var screenshotGate = (SemaphoreSlim)KeyboardField(main, "_screenshotGate");
                var logLines = (Queue<string>)KeyboardField(vm, "_visibleLogLines");
                var busyMessage = IPhoneMirror.App.Localization.LocalizationService.Get("ScreenshotBusy");
                var busyBefore = logLines.Count(line => line.Contains(busyMessage, StringComparison.Ordinal));
                packets.SetLength(0); screenshotGate.Wait();
                try
                {
                    Key(0xA2, true); Key(0x53, true); Key(0x53, true); Key(0x53, false); Key(0xA2, false); Finish();
                    MappingAssert(logLines.Count(line => line.Contains(busyMessage, StringComparison.Ordinal)) == busyBefore + 1 && packets.Length == 0,
                        $"{mode}: Ctrl+S did not exclusively invoke the actual screenshot handler once.");
                }
                finally { screenshotGate.Release(); }
                shortcuts[BluetoothShortcutAction.Home] = new(KeyboardShortcut.Control, 0x53);
                stored[BluetoothShortcutAction.Home] = shortcuts[BluetoothShortcutAction.Home];
                KeyboardCall(main, "ConfigureKeyboardShortcuts", shortcuts);
                packets.SetLength(0);
                for (var i = 0; i < 20; i++)
                    foreach (var vk in new[] { 0x57, 0x41, 0x53, 0x44 })
                    { Key(vk, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(2)); Key(vk, false); AdvanceDispatcher(TimeSpan.FromMilliseconds(2)); }
                Finish();
                frames = MappingFrames(packets);
                MappingAssert(frames.Count(f => f.TryGetProperty("points", out _)) == 160, $"{mode}: rapid WASD lost or repeated touch pairs.");
                packets.SetLength(0);
                for (var quick = 0; quick < 8; quick++) { Key(0x57, true); Key(0x57, false); }
                Finish();
                MappingAssert(MappingFrames(packets).Count(f => f.TryGetProperty("points", out _)) == 16,
                    $"{mode}: a complete press/release queued before dispatch was lost.");
                packets.SetLength(0);
                Key(0x57, true); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                for (var i = 0; i < 30; i++) Key(0x57, true);
                foreground = 0;
                KeyboardCall(main, "OnMainKeyboardDeactivated", main, EventArgs.Empty); Finish();
                Key(0x57, false); foreground = hwnd;
                KeyboardCall(main, "OnMainKeyboardActivated", main, EventArgs.Empty); Finish();
                var touches = MappingFrames(packets).Where(f => f.TryGetProperty("points", out _)).ToArray();
                MappingAssert(touches.Length == 2 && touches[1].GetProperty("points")[0].GetProperty("action").GetString() == "up",
                    $"{mode}: loss of focus retained a hold or repeated it.");
                SetMapping(false); packets.SetLength(0);
                Key(0xA2, true); Key(0x41, true); Key(0x41, false); Key(0xA2, false); Finish();
                frames = MappingFrames(packets);
                MappingAssert(frames.Count == 4 && frames[1].GetProperty("usages").EnumerateArray().Any(v => v.GetInt32() == 4) &&
                    frames[^1].GetProperty("usages").GetArrayLength() == 0, $"{mode}: unmatched Ctrl+A failed normal-input fallback.");
                packets.SetLength(0);
                Key(0xA3, true); Key(0x53, true); Key(0x53, false); Key(0xA3, false); Finish();
                MappingAssert(MappingFrames(packets).Count == 2 && MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                    $"{mode}: shortcut modifiers leaked into direct HID.");
                Console.WriteLine($"PASS {mode}: production hook -> router -> configured Home action / mapped hold / direct HID; W+Ctrl+S alternation, 80 WASD pairs, long hold, loss of focus, mapping toggle, Ctrl+A fallback; framed packets inspected.");
            }
            // Reloading bindings invalidates already-recognized actions even
            // if the device, foreground HWND and ownership mode stay the same.
            packets.SetLength(0);
            Key(0xA2, true); Key(0x53, true);
            KeyboardCall(main, "ConfigureKeyboardShortcuts", shortcuts);
            Key(0x53, false); Key(0xA2, false); Finish();
            MappingAssert(packets.Length == 0, "Reloaded shortcuts executed an obsolete queued action.");
            Key(0xA2, true); Key(0x53, true); Key(0x53, false); Key(0xA2, false); Finish();
            MappingAssert(MappingFrames(packets).Count == 2,
                "Reloading shortcuts blocked the next fresh shortcut press.");
            Console.WriteLine("PASS shortcut reload cancels old queued action; fresh press executes once.");
            // A queued action must be revoked even if focus leaves and returns
            // before the dispatcher executes it (the HWND is unchanged again).
            using (var focusGuard = new KeyboardMappingFocusGuard())
            {
                SetKeyboardField(main, "_mappingFocus", focusGuard);
                var invoked = 0;
                KeyboardCall(main, "QueueKeyboardShortcut", (Action)(() => invoked++), (Func<bool>)(() => true));
                SetKeyboardField(focusGuard, "_focusGeneration", focusGuard.Generation + 1);
                Finish();
                MappingAssert(invoked == 0, "Queued shortcut survived a focus-away/back generation change.");
                SetKeyboardField(main, "_mappingFocus", null);
            }
            Console.WriteLine("PASS queued shortcut focus-away/back cancellation.");
            TestBluetoothKeyboardTransportGates();
            packets.SetLength(0);
            Key(0x41, true);
            Key(0xA2, true); Key(0x53, true); // close before the queued action runs
            KeyboardCall(main, "DisposeKeyboardMapping");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff")); Finish();
            var closing = MappingFrames(packets);
            MappingAssert(router.Mode == KeyboardInputMode.None && closing.Count == 2 &&
                closing[0].GetProperty("usages").GetArrayLength() == 1 && closing[1].GetProperty("usages").GetArrayLength() == 0,
                "Closing retained a direct key or executed an obsolete queued shortcut.");
            Key(0x41, false); Key(0x53, false); Key(0xA2, false);
            Console.WriteLine("PASS close during queued shortcut: captured direct session released, retired ups ignored, obsolete action cancelled.");
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            KeyboardCall(main, "ResetKeyboardOwnership"); AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            SetKeyboardField(vm, "_isCapturing", false); sessions.Remove(phone.Udid);
            controls.Clear(); SetKeyboardField(preview, "_window", (nint)0);
            SetKeyboardField(bridge, "_stdin", null); SetKeyboardField(bridge, "<IsReady>k__BackingField", false);
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }
}
