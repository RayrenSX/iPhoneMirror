using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunKeyboardOwnershipTests()
    {
        TestKeyboardOwnerStateMachine();
        TestMuxCheckpointOwnership();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var hwnd = new WindowInteropHelper(main).Handle;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => hwnd));
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(host, "<State>k__BackingField", ReverseControlState.Ready);
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)ctor.Invoke(["ownership-phone", "Ownership test", "iPhone15,2", "18.0",
            "USB", "", Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        var control = new DeviceControlSession(device.Udid) { AppleUdid = device.Udid,
            WiredBridge = host, WirelessBridge = host, WiredConnected = true, WirelessConnected = true };
        var controls = (IDictionary)KeyboardField(vm, "_deviceControls");
        controls[device.Udid] = control;
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        void Key(int vk, bool down) => KeyboardCall(main, "HandleControlKeyboardInput",
            new PreviewKeyboardEventArgs(down ? PreviewKeyboardKind.Down : PreviewKeyboardKind.Up, vk),
            device.Udid, false, (nint?)hwnd);
        void Drain() => AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
        int[][] Reports() => MappingFrames(packets).Where(f => f.TryGetProperty("usages", out _))
            .Select(f => f.GetProperty("usages").EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
        void Mapping(bool enabled)
        {
            KeyboardCall(main, enabled ? "TryEnterKeyboardMappingInputMode" : "LeaveKeyboardMappingInputMode");
        }
        try
        {
            foreach (var mode in new[] { ReverseControlMode.Usb, ReverseControlMode.Wireless })
            {
                control.WiredEnabled = mode == ReverseControlMode.Usb;
                control.WirelessEnabled = mode == ReverseControlMode.Wireless;
                control.Router.Begin(device.Udid, mode);
                ReleaseTestPhysicalKeys(main);
                KeyboardCall(main, "FocusControlDevice", device.Udid, (nint)0);
                Drain();
                packets.SetLength(0);
                Key(0x41, true); Key(0x41, true); Key(0x41, false); Key(0x41, false);
                MappingAssert(Reports().Length == 2 && Reports()[0].SequenceEqual(new[] { 4 }) && Reports()[1].Length == 0,
                    $"{mode}: direct input must produce one down/up pair, including duplicate ingress.");

                foreach (var (vk, expected) in new[] { (0xA0, 0xE1), (0xA1, 0xE5), (0xA2, 0xE0),
                    (0xA3, 0xE4), (0xA4, 0xE2), (0xA5, 0xE6) })
                {
                    packets.SetLength(0);
                    Key(vk, true); Key(vk, false);
                    MappingAssert(Reports()[^2].SequenceEqual(new[] { expected }) && Reports()[^1].Length == 0, $"{mode}: wrong modifier usage for {vk:X}.");
                    Key(vk, true); // Candidate or direct modifier must be retired by takeover.
                    Mapping(true); Drain();
                    MappingAssert(Reports().Last().Length == 0, $"{mode}: takeover did not release the held modifier.");
                    var count = packets.Length;
                    Key(vk, false); Key(0x42, true); Key(0x42, false);
                    MappingAssert(packets.Length == count, $"{mode}: direct events leaked during mapping.");
                    Mapping(false); Drain();
                    Key(vk, true); Key(vk, false); // prior physical up retired the old lifetime in the shared router
                    MappingAssert(Reports()[^2].SequenceEqual(new[] { expected }) && Reports()[^1].Length == 0,
                        $"{mode}: a fresh modifier did not resume after the old physical release.");
                    Key(vk, true); Key(vk, false);
                    MappingAssert(Reports().Length >= 4 && Reports().Last().Length == 0, $"{mode}: fresh modifier did not resume.");
                }

                // Lock the production framed writer. Old press must be revoked,
                // cleanup must drain, and rapid toggles cannot open over it.
                packets.SetLength(0);
                var sendLock = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
                sendLock.Wait();
                try
                {
                    Key(0x58, true);
                    Mapping(true); Mapping(false); Mapping(true);
                    Key(0x59, true);
                    MappingAssert(router.Mode == KeyboardInputMode.None && packets.Length == 0,
                        $"{mode}: a new owner opened while the old writer was blocked.");
                }
                finally { sendLock.Release(); }
                Drain();
                MappingAssert(router.Mode == KeyboardInputMode.Mapping && Reports().All(r => r.Length == 0),
                    $"{mode}: queued direct press survived mapping takeover.");
                ReleaseTestPhysicalKeys(main);

                // Drive the real mapping executor and the real transport. The
                // direct owner cannot resume before its cancelled touch is up.
                packets.SetLength(0);
                var executor = (KeyboardMappingExecutor)KeyboardField(main, "_mappingExecutor");
                var gesture = Task.WhenAll(Enumerable.Range(0, 5).Select(_ => executor.ExecuteAsync(
                    MappingEntry(MappedTouchAction.LongPress) with { DurationMs = 1000 },
                    vm.CaptureMappingRoute(() => router.Mode == KeyboardInputMode.Mapping, (x, y) => (x, y))!)));
                MappingAssert(!gesture.IsCompleted, "Long-press fixture did not hold a touch.");
                Mapping(false);
                MappingAssert(router.Mode == KeyboardInputMode.None, "Direct owner opened before mapping cleanup.");
                Drain();
                MappingAssert(gesture.IsCompleted && router.Mode == KeyboardInputMode.Direct,
                    "Mapping cleanup did not finish before restoring direct input.");
                var actions = MappingFrames(packets).Where(f => f.TryGetProperty("points", out _))
                    .Select(f => f.GetProperty("points")[0].GetProperty("action").GetString()).ToArray();
                MappingAssert(actions.SequenceEqual(Enumerable.Repeat("down", 5).Concat(Enumerable.Repeat("up", 5))) && Reports().Length == 0,
                    "Mapping cancellation leaked a keyboard packet or retained a touch.");

                // Capture a session, then reconnect while its release waits.
                var old = vm.CaptureDirectKeyboardRoute(device.Udid)!;
                packets.SetLength(0);
                sendLock.Wait();
                Task stale;
                try
                {
                    stale = old.SendAsync(0, [], null);
                    control.Router.Stop();
                    control.Router.Begin(device.Udid, mode);
                }
                finally { sendLock.Release(); }
                AwaitMapping(stale);
                MappingAssert(packets.Length == 0 && !old.IsCurrent(), "Old session cleanup reached a replacement session.");

                // Even if both backends are temporarily ready, only the router's
                // established transport may be captured.
                control.WiredEnabled = true; control.WirelessEnabled = true;
                var overlap = vm.CaptureDirectKeyboardRoute(device.Udid);
                MappingAssert(mode == ReverseControlMode.Usb ? overlap?.Transport == "WiredDirect" : overlap is null,
                    "Overlapping backend readiness created a second keyboard owner.");
                Console.WriteLine($"PASS {mode}: direct/mapping exclusion, modifier release, repeats, writer drain, rapid toggles, touch cleanup, stale session and transport overlap.");
            }
            return 0;
        }
        finally
        {
            controls.Clear();
            SetKeyboardField(bridge, "_stdin", null);
            SetKeyboardField(bridge, "<IsReady>k__BackingField", false);
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static void TestMuxCheckpointOwnership()
    {
        var context = new UsbMuxResumeContext();
        var bridge = new DirectUsbInputBridge { MuxResumeContext = context };
        const string checkpoint = "{\"event\":\"capture_mux_checkpoint\",\"state\":{\"schema\":1}}";
        KeyboardCall(bridge, "HandleLine", checkpoint);
        MappingAssert(KeyboardField(bridge, "_muxCheckpoint") is null,
            "A running bridge must not publish a restart checkpoint.");
        SetKeyboardField(bridge, "_stopping", 1);
        SetKeyboardField(bridge, "_requestedWireless", true);
        KeyboardCall(bridge, "HandleLine", checkpoint);
        MappingAssert(KeyboardField(bridge, "_muxCheckpoint") is null,
            "A wireless bridge must not publish USB protocol state.");
        SetKeyboardField(bridge, "_requestedWireless", false);
        KeyboardCall(bridge, "HandleLine", checkpoint);
        MappingAssert((string)KeyboardField(bridge, "_muxCheckpoint") == "{\"schema\":1}",
            "The stopping wired bridge lost its checkpoint.");
        KeyboardCall(bridge, "HandleLine", "{\"event\":\"ready\"}");
        MappingAssert(!bridge.IsReady, "A late Ready reopened a stopping bridge.");
        context.Save("one-capture");
        MappingAssert(context.Take() == "one-capture" && context.Take() is null,
            "A protocol checkpoint must be consumed once.");
        var replacement = new UsbMuxResumeContext();
        context.Save("late-old-exit");
        MappingAssert(replacement.Take() is null,
            "An old process checkpoint reached a replacement capture lifetime.");
        Console.WriteLine("PASS capture mux checkpoint: stopping USB only, late Ready blocked, one-shot capture ownership.");
    }

    private static void TestKeyboardOwnerStateMachine()
    {
        var keys = Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10))
            .Concat(new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x0D, 0x20, 0x08, 0x09, 0x1B });
        foreach (var first in new[] { KeyboardInputMode.Direct, KeyboardInputMode.Mapping })
        foreach (var key in keys)
        {
            var router = new KeyboardInputRouter();
            router.CompleteHandoff(router.BeginHandoff(first));
            var lease = router.Generation;
            MappingAssert(router.Route(first, key, true), "First press was not routed.");
            var next = first == KeyboardInputMode.Mapping ? KeyboardInputMode.Direct : KeyboardInputMode.Mapping;
            var transition = router.BeginHandoff(next);
            MappingAssert(!router.Owns(lease, first), "Worker retained an old ownership lease.");
            MappingAssert(!router.Route(next, key, true), "Held repeat crossed a pending handoff.");
            router.CompleteHandoff(transition);
            MappingAssert(!router.Route(next, key, false), "Old up was delivered to new owner.");
            MappingAssert(router.Route(next, key, true) && router.Route(next, key, false), "Fresh pair did not resume.");
        }
        var rapid = new KeyboardInputRouter();
        for (var i = 0; i < 1000; i++)
        {
            var old = rapid.BeginHandoff(KeyboardInputMode.Mapping);
            var current = rapid.BeginHandoff(KeyboardInputMode.Direct);
            MappingAssert(!rapid.CompleteHandoff(old) && rapid.CompleteHandoff(current), "Stale transition reopened an owner.");
            MappingAssert(!rapid.Route(KeyboardInputMode.Mapping, 65, true), "Non-owner consumed a key.");
            MappingAssert(rapid.Route(KeyboardInputMode.Direct, 65, true) &&
                !rapid.Route(KeyboardInputMode.Direct, 65, true) && rapid.Route(KeyboardInputMode.Direct, 65, false),
                "Rapid press duplicated or lost its release.");
        }
        Console.WriteLine("PASS keyboard owner model: all requested key families, both handoff directions, stale leases and 1,000 rapid toggles.");
    }
}
