using System.Reflection;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunBluetoothInputTransportTests()
    {
        Task.Run(TestAbsoluteDragTrackingAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteDragCancellationAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteDragKeyboardPriorityAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteDragReportBudgetAsync).GetAwaiter().GetResult();
        Task.Run(TestBluetoothPauseRouteChangeAsync).GetAwaiter().GetResult();
        Task.Run(TestBluetoothMouseAdmissionAsync).GetAwaiter().GetResult();
        Task.Run(TestWindowBluetoothMouseClickAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteWindowMouseAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteMousePressRetryAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteMouseInputLifecycleAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteMouseBacklogAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteMouseEdgeScrollAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteScrollThenClickAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteScrollFailureMatrixAsync).GetAwaiter().GetResult();
        Task.Run(TestAbsoluteMouseTimeoutAsync).GetAwaiter().GetResult();
        Task.Run(TestBluetoothHidSessionAdmissionAsync).GetAwaiter().GetResult();
        TestBluetoothNotificationResults();
        return 0;
    }

    private static int RunBluetoothInputToggleTests()
    {
        RunBluetoothInputTransportTests();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        // Inputs below are supplied explicitly. Ignore unrelated WM_INPUT
        // packets from the interactive desktop, while retaining registration
        // and the production processor/queue for the supplied test events.
        var source = (HwndSource)KeyboardField(main, "_windowSource");
        var messageHook = (HwndSourceHook)Delegate.CreateDelegate(typeof(HwndSourceHook), main,
            typeof(MainWindow).GetMethod("WindowMessageHook", KeyboardTestMembers)!);
        nint FixtureMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x00FF)
            {
                handled = true;
                return (nint)typeof(MainWindow).GetMethod("DefWindowProcW", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [hwnd, message, wParam, lParam])!;
            }
            return messageHook(hwnd, message, wParam, lParam, ref handled);
        }
        source.RemoveHook(messageHook);
        source.AddHook(FixtureMessageHook);
        // This fixture supplies its own foreground snapshot and physical key
        // events. Unrelated real desktop activation must not invalidate queued
        // shortcuts while the dispatcher pumps transport continuations.
        var activated = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), main,
            typeof(MainWindow).GetMethod("OnMainKeyboardActivated", KeyboardTestMembers)!);
        var deactivated = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), main,
            typeof(MainWindow).GetMethod("OnMainKeyboardDeactivated", KeyboardTestMembers)!);
        var keyboardFocus = (System.Windows.Input.KeyboardFocusChangedEventHandler)Delegate.CreateDelegate(
            typeof(System.Windows.Input.KeyboardFocusChangedEventHandler), main,
            typeof(MainWindow).GetMethod("OnMainKeyboardFocusChanged", KeyboardTestMembers)!);
        main.Activated -= activated;
        main.Deactivated -= deactivated;
        main.PreviewGotKeyboardFocus -= keyboardFocus;
        SetKeyboardField(main, "_isPhysicalKeyboardKeyDown", (Func<int, bool>)(_ => false));
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var service = (BluetoothHidMouseService)KeyboardField(vm, "_bluetoothControl");
        var transportGate = (SemaphoreSlim)KeyboardField(service, "_targetClientGate");
        var reports = (ConcurrentDictionary<byte, byte[]>)KeyboardField(service, "_lastReports");
        var delivered = new ConcurrentQueue<(byte Id, byte[] Payload)>();
        byte? failedReport = null;
        (GattCommunicationStatus Status, ushort Bytes, byte? ProtocolError)? notificationResult = null;
        TaskCompletionSource<bool>? delayedDelivery = null;
        TaskCompletionSource<bool>? deliveryEntered = null;
        Func<bool>? delayedGuard = null;
        var originalSender = KeyboardField(service, "_sendInputReport");
        // Replace only GATT delivery. Queueing, releases, deadlines and model
        // transitions remain the production implementation.
        async Task<bool> Send(byte id, byte[] payload, Func<bool>? guard)
        {
            if (guard?.Invoke() == false) return true;
            if (delayedDelivery is { } delay)
            {
                delayedGuard = guard;
                deliveryEntered?.TrySetResult(true);
                var sent = await delay.Task;
                if (!sent || guard?.Invoke() == false) return false;
            }
            await transportGate.WaitAsync();
            try
            {
                if (guard?.Invoke() == false) return true;
                if (id == failedReport) return false;
                if (notificationResult is { } result && !BluetoothHidMouseService.NotificationSucceeded(
                    result.Status, result.Bytes, payload.Length, result.ProtocolError)) return false;
                delivered.Enqueue((id, payload.ToArray()));
                reports[id] = payload.ToArray();
                return true;
            }
            finally { transportGate.Release(); }
        }
        SetKeyboardField(service, "_sendInputReport", (Func<byte, byte[], Func<bool>?, Task<bool>>)Send);
        const string target = "issue-64-bluetooth";
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)ctor.Invoke([target, "Issue 64", "iPhone19,2", "27.0.1", "USB", "",
            Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        var foreground = new WindowInteropHelper(main).Handle;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => foreground));
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        var windows = (Dictionary<string, NativePreviewWindow>)KeyboardField(
            KeyboardField(main, "_secondaryMirrors"), "_windows");
        NativePreviewWindow? shell = null;
        void Ready()
        {
            SetKeyboardField(vm, "_bluetoothControlEnabled", true);
            SetKeyboardField(vm, "_bluetoothControlConnected", true);
            SetKeyboardField(vm, "_bluetoothControlCalibrated", true);
            SetKeyboardField(vm, "_bluetoothControlDeviceUdid", target);
            SetKeyboardField(vm, "_bluetoothControlInputEnabled", true);
            SetKeyboardField(vm, "_bluetoothControlInputPaused", false);
            SetKeyboardField(service, "_targetDeviceUdid", target);
            SetKeyboardField(service, "_mousePumpStopping", false);
        }
        void Toggle() => AwaitMapping(vm.ToggleBluetoothControlAsync(target));
        void Require(bool condition, string message) => MappingAssert(condition, "Issue 64: " + message);
        try
        {
            sessions.Set(new() { Udid = target, Handle = new Interop.NativeSessionHandle(456, ownsHandle: false) });
            Ready();
            KeyboardCall(main, "FocusControlDevice", target, (nint)0);
            vm.ControlStatus.Ready(ControlStatusMode.Bluetooth, "Issue 64");
            var status = vm.ControlStatus.Current;

            // Block report release, not the UI thread, to exercise a second
            // press arriving before the first press has completed its cleanup.
            transportGate.Wait();
            Task pause;
            Task resume;
            try
            {
                SetKeyboardField(main, "_controlButtons", (byte)1);
                ((HashSet<byte>)KeyboardField(main, "_controlKeyboardUsages")).Add(4);
                SetKeyboardField(service, "_pendingMouseReport", new byte[] { 1, 30, 0, 20, 0, 0 });
                reports[2] = [1, 30, 0, 20, 0, 0];
                reports[1] = [0, 0, 4, 0, 0, 0, 0, 0];
                reports[4] = [0xE9, 0];
                reports[5] = [2, 0];
                pause = vm.ToggleBluetoothControlAsync(target);
                Require(vm.IsBluetoothControlEnabled && vm.BluetoothControlIsInputPaused &&
                    !vm.BluetoothControlIsInputEnabled && !pause.IsCompleted,
                    "pause must release local ownership before the BLE release finishes, retaining the connection.");
                Require(!(bool)KeyboardField(main, "_rawMouseInputEnabled") &&
                    (byte)KeyboardField(main, "_controlButtons") == 0 &&
                    ((HashSet<byte>)KeyboardField(main, "_controlKeyboardUsages")).Count == 0,
                    "pause retained Raw Input or a held local mouse/key.");
                resume = vm.ToggleBluetoothControlAsync(target);
                Require(!resume.IsCompleted && !vm.BluetoothControlIsInputEnabled,
                    "rapid resume overtook the captured session's release.");
            }
            finally { transportGate.Release(); }
            AwaitMapping(Task.WhenAll(pause, resume));
            Require(vm.BluetoothControlIsInputEnabled && !vm.BluetoothControlIsInputPaused &&
                KeyboardField(service, "_pendingMouseReport") is null,
                "second press did not resume cleanly or retained stale pointer motion.");
            Require(new byte[] { 1, 2, 4, 5 }.All(id => delivered.Any(report =>
                report.Id == id && report.Payload.All(value => value == 0))),
                "pause did not deliver releases for every potentially held HID report.");

            for (var i = 0; i < 20; i++)
            {
                Toggle();
                Require(vm.BluetoothControlIsInputPaused && !vm.BluetoothControlIsInputEnabled,
                    "repeated pause did not return control to Windows.");
                KeyboardCall(vm, "BeginBluetoothControlInputAfterStatusCountdown");
                Require(!vm.BluetoothControlIsInputEnabled, "a late countdown undid the user's pause.");
                Toggle();
                Require(vm.BluetoothControlIsInputEnabled && vm.IsBluetoothControlEnabled &&
                    vm.IsBluetoothControlTarget(target), "repeated resume lost the active session or target.");
            }
            Require(ReferenceEquals(status, vm.ControlStatus.Current),
                "pause/resume restarted the connection status workflow.");
            Require(typeof(ReverseControlStatusWindow).GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null) is null, "pause/resume opened another setup dialog.");
            Console.WriteLine("PASS Bluetooth F9: immediate local release, serialized rapid resume, 20 retained-session cycles, no repeated setup dialog or stale motion.");

            Toggle();
            var focusAllowed = true;
            var focusGate = (SemaphoreSlim)KeyboardField(vm, "_bluetoothControlInputGate");
            focusGate.Wait();
            Task deferredFocusResume;
            try
            {
                deferredFocusResume = vm.ToggleBluetoothControlAsync(target, () => focusAllowed);
                focusAllowed = false;
            }
            finally { focusGate.Release(); }
            AwaitMapping(deferredFocusResume);
            Require(vm.BluetoothControlIsInputPaused && !vm.BluetoothControlIsInputEnabled,
                "focus lost while resume was queued still admitted phone input.");
            SetKeyboardField(vm, "_bluetoothControlConnected", false);
            Toggle();
            Require(vm.BluetoothControlIsInputPaused && !vm.BluetoothControlIsInputEnabled,
                "resume accepted a disconnected phone.");
            SetKeyboardField(vm, "_bluetoothControlConnected", true);
            Toggle();
            SetKeyboardField(vm, "_bluetoothControlInputEnabled", false);
            Toggle();
            Require(!vm.BluetoothControlIsInputEnabled && !vm.BluetoothControlIsInputPaused,
                "F9 bypassed the first-connection countdown.");
            Ready();

            // A queued press belonging to the old session must not act on a
            // newly established session, even when its device ID is identical.
            var toggleGate = (SemaphoreSlim)KeyboardField(vm, "_bluetoothControlInputGate");
            toggleGate.Wait();
            Task obsolete;
            try
            {
                obsolete = vm.ToggleBluetoothControlAsync(target);
                KeyboardCall(vm, "ResetBluetoothControlInputState");
                Ready();
            }
            finally { toggleGate.Release(); }
            AwaitMapping(obsolete);
            Require(vm.BluetoothControlIsInputEnabled && !vm.BluetoothControlIsInputPaused,
                "a queued old-session press paused the replacement session.");
            Console.WriteLine("PASS Bluetooth F9 guards: initial countdown, disconnected resume, late countdown and obsolete-session press.");

            var bindings = Enum.GetValues<BluetoothShortcutAction>().ToDictionary(action => action,
                _ => KeyboardShortcut.Unbound);
            bindings[BluetoothShortcutAction.BluetoothControl] = KeyboardShortcut.Default;
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            var dataType = typeof(MainWindow).GetNestedType("LowLevelKeyboardData", BindingFlags.NonPublic)!;
            var keyData = Activator.CreateInstance(dataType)!;
            SetKeyboardField(keyData, "VirtualKey", 0x78u);
            SetKeyboardField(keyData, "ScanCode", 0x43u);
            for (var i = 0; i < 10; i++)
            {
                KeyboardCall(main, "ProcessKeyboardHook", keyData, (nint)0x100);
                // Holding F9 must not toggle repeatedly or leak into the phone.
                KeyboardCall(main, "ProcessKeyboardHook", keyData, (nint)0x100);
                KeyboardCall(main, "ProcessKeyboardHook", keyData, (nint)0x101);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                Require(vm.BluetoothControlIsInputPaused == (i % 2 == 0) &&
                    vm.BluetoothControlIsInputEnabled == (i % 2 != 0),
                    "production F9 hook/router failed to alternate pause/resume exactly once.");
            }
            Console.WriteLine("PASS production F9 hook/router: ten press/release cycles, autorepeat suppressed, connection retained.");

            var captured = vm.CaptureDirectKeyboardRoute(target) ??
                throw new InvalidOperationException("Ready Bluetooth keyboard route missing.");
            Require(captured.IsCurrent(), "ready Bluetooth keyboard route missing.");
            Toggle();
            Require(vm.CaptureDirectKeyboardRoute(target) is null && !captured.IsCurrent(),
                "pause still allowed a new/captured Bluetooth keyboard route.");
            KeyboardCall(main, "TryEnterKeyboardMappingInputMode");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            KeyboardCall(main, "LeaveKeyboardMappingInputMode");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            var letter = Activator.CreateInstance(dataType)!;
            SetKeyboardField(letter, "VirtualKey", 0x41u);
            SetKeyboardField(letter, "ScanCode", 0x1Eu);
            delivered.Clear();
            var suppressed = (bool)KeyboardCall(main, "ProcessKeyboardHook", letter, (nint)0x100)!;
            KeyboardCall(main, "ProcessKeyboardHook", letter, (nint)0x101);
            Require(!suppressed && delivered.IsEmpty,
                "mapping-to-direct transition intercepted or forwarded a paused keyboard press.");
            Toggle();
            Require(!captured.IsCurrent(), "old keyboard route became current again after resume.");
            AwaitMapping(captured.SendAsync(0, [4], () => true));
            Require(delivered.All(report => report.Payload.All(value => value == 0)),
                "old keyboard press survived pause and resume: " + string.Join("; ",
                    delivered.Select(report => $"{report.Id}:{Convert.ToHexString(report.Payload)}")));
            var fresh = vm.CaptureDirectKeyboardRoute(target)!;
            AwaitMapping(fresh.SendAsync(0, [4], () => true));
            Require(delivered.Any(report => report.Id == 1 && report.Payload.Contains((byte)4)),
                "fresh keyboard route failed after resume.");
            AwaitMapping(fresh.SendAsync(0, [], null));
            Console.WriteLine("PASS paused keyboard isolation: mapping-to-direct keeps Windows input; new routes blocked; old routes stay revoked after resume; fresh typing works.");

            // Reproduce the original bug through the production background
            // pointer flush, with admission held across the complete F9 cycle.
            using (var admissionEntered = new ManualResetEventSlim())
            using (var allowAdmission = new ManualResetEventSlim())
            {
                var originalAdmission = service.InputAdmission;
                var admissionCount = 0;
                Task? oldFlush = null;
                service.InputAdmission = () =>
                {
                    if (Interlocked.Increment(ref admissionCount) == 1)
                    {
                        admissionEntered.Set();
                        if (!allowAdmission.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Pointer admission timeout.");
                    }
                    return null!;
                };
                try
                {
                    KeyboardCall(main, "StopControlPointerTimer");
                    SetKeyboardField(main, "_pendingControlDx", 39);
                    SetKeyboardField(main, "_pendingControlDy", 17);
                    SetKeyboardField(main, "_pendingControlButtons", (byte)1);
                    SetKeyboardField(main, "_pendingControlStateDirty", true);
                    oldFlush = Task.Run(() => (Task)KeyboardCall(main, "FlushControlPointerAsync", true)!);
                    Require(admissionEntered.Wait(TimeSpan.FromSeconds(3)), "old pointer flush did not reach admission.");
                    Toggle(); Toggle();
                    delivered.Clear();
                    allowAdmission.Set();
                    AwaitMapping(oldFlush);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                    Require(delivered.All(report => report.Payload.All(value => value == 0)),
                        "pause/resume replayed an old mouse movement or left-button press.");
                    AwaitMapping(vm.SendBluetoothMouseAsync(7, 9, buttons: 1, expectedTargetDeviceUdid: target));
                    AwaitMapping((Task)KeyboardField(service, "_mousePumpTask"));
                    Require(delivered.Any(report => report.Id == 2 && report.Payload[0] == 1 &&
                        report.Payload[1] == 7 && report.Payload[3] == 9),
                        "fresh mouse input failed after rejecting the pre-pause flush.");
                    AwaitMapping(vm.SendBluetoothMouseAsync(0, 0, expectedTargetDeviceUdid: target));
                    AwaitMapping((Task)KeyboardField(service, "_mousePumpTask"));
                }
                finally
                {
                    allowAdmission.Set();
                    if (oldFlush is not null) AwaitMapping(oldFlush);
                    service.InputAdmission = originalAdmission;
                }
            }
            Console.WriteLine("PASS production pointer flush: old left-button/movement held across F9 pause/resume is discarded; fresh mouse input works.");

            var beforePause = vm.CaptureBluetoothInputSendGuard();
            Toggle(); Toggle();
            delivered.Clear();
            AwaitMapping(vm.SendBluetoothMouseAsync(39, 17, buttons: 1, expectedTargetDeviceUdid: target, canSend: beforePause));
            AwaitMapping(vm.SendBluetoothMouseAsync(0, 0, expectedTargetDeviceUdid: target, canSend: beforePause));
            Require(delivered.IsEmpty, "old source snapshot admitted a press/release after pause/resume.");

            // Foreground loss must still clear a phone-side held button. The
            // empty report is session-bound, while old motion/scroll is dropped.
            foreground = (nint)123456;
            delivered.Clear();
            SetKeyboardField(main, "_pendingControlDx", 39);
            SetKeyboardField(main, "_pendingControlDy", 17);
            SetKeyboardField(main, "_pendingControlWheel", 1);
            SetKeyboardField(main, "_controlButtons", (byte)1);
            KeyboardCall(main, "ResetMainControlState");
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            AwaitMapping((Task)KeyboardField(service, "_mousePumpTask"));
            Require(delivered.Any(report => report.Id == 2) &&
                delivered.Where(report => report.Id == 2).All(report => report.Payload.All(value => value == 0)),
                "blur dropped its button release or forwarded old motion/wheel.");
            foreground = new WindowInteropHelper(main).Handle;
            KeyboardCall(main, "FocusControlDevice", target, (nint)0);
            Console.WriteLine("PASS pointer lifecycle: pre-pause source snapshots stay revoked; blur still delivers an empty release without old movement/wheel.");

            // Native hit-testing must follow current input ownership, while
            // leaving a user-selected fixed-window preference untouched.
            var shellCtor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
            var parameters = shellCtor.GetParameters();
            var values = parameters.Select(p => p.DefaultValue).ToArray();
            object[] required = [390u, 844u, "Issue 64 independent window", (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            void Argument(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
            Argument("isReverseControlEnabled", (Func<nint, bool>)(_ => vm.BluetoothControlIsInputEnabled));
            Argument("pointerInput", (Action<PreviewPointerEventArgs>)(_ => { }));
            Argument("requestReverseControl", (Action<nint>)(_ => { }));
            shell = (NativePreviewWindow)shellCtor.Invoke(values);
            KeyboardCall(shell, "ShowInitially");
            KeyboardCall(shell, "EnableReverseControl");
            windows[target] = shell;
            foreground = shell.Handle;
            KeyboardCall(main, "FocusControlDevice", target, shell.Handle);
            var rectArgs = new object?[] { shell.Handle, null };
            typeof(NativePreviewWindow).GetMethod("GetWindowRect", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, rectArgs);
            var rect = rectArgs[1]!;
            var x = ((int)KeyboardField(rect, "Left") + (int)KeyboardField(rect, "Right")) / 2;
            var y = ((int)KeyboardField(rect, "Top") + (int)KeyboardField(rect, "Bottom")) / 2;
            var point = (nint)((y << 16) | (x & 0xFFFF));
            nint Hit() => (nint)KeyboardCall(shell, "WindowProcedure", shell.Handle, 0x84, (nint)0, point, false)!;
            Require(!(bool)KeyboardField(shell, "_isFixed") && Hit() == 1,
                "starting Bluetooth persisted the fixed preference or left a draggable input surface.");
            typeof(NativePreviewWindow).GetMethod("SetCapture", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [shell.Handle]);
            SetKeyboardField(shell, "_capturedMouseButtons", (byte)1);
            Toggle();
            Require(Hit() == 2 && (byte)KeyboardField(shell, "_capturedMouseButtons") == 0 &&
                (nint)typeof(NativePreviewWindow).GetMethod("GetCapture", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null)! != shell.Handle,
                "pausing did not restore caption dragging or release native mouse capture.");
            KeyboardCall(shell, "ToggleFixedWindow");
            Require(Hit() == 1, "explicitly fixed window became draggable.");
            Toggle(); Toggle();
            Require((bool)KeyboardField(shell, "_isFixed") && Hit() == 1,
                "pause/resume changed the user's fixed preference.");
            KeyboardCall(shell, "ToggleFixedWindow");
            Require(Hit() == 2, "unfixing a paused independent window did not restore dragging.");
            Console.WriteLine("PASS independent Bluetooth window: temporary input hit-testing, restored caption dragging/capture, explicit fixed preference retained.");
            Toggle();
            TestBluetoothWindowMenus(shell);
            SetKeyboardField(main, "_controlPointerFlushInFlight", 1);
            try
            {
                TestWindowBluetoothMouseHandoff(main, device, shell.Handle, hwnd => foreground = hwnd);
                TestWindowBluetoothPointer(main, device);
            }
            finally
            {
                SetKeyboardField(main, "_bluetoothWindowMouseEnabled", false);
                KeyboardCall(main, "ResetControlRouteState");
                SetKeyboardField(main, "_controlPointerFlushInFlight", 0);
            }
            Toggle();
            TestBluetoothToolbarStop(main, vm);
            Require(!vm.IsBluetoothControlEnabled && !vm.BluetoothControlIsInputPaused &&
                vm.BluetoothControlTargetUdid is null,
                "explicit stop did not tear down a paused Bluetooth session.");
            Console.WriteLine("PASS explicit Bluetooth stop still clears a paused connection and its target.");

            Ready();
            SetKeyboardField(main, "_activeControlWindow", (nint)0);
            SetKeyboardField(main, "_activeControlUdid", null);
            foreground = new WindowInteropHelper(main).Handle;
            Toggle();
            var second = (DeviceViewModel)ctor.Invoke(["issue-64-phone-B", "Phone B", "iPhone19,2", "27.0.1", "USB", "",
                Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
            SetKeyboardField(vm, "_selectedDevice", second);
            KeyboardCall(main, "FocusControlDevice", second.Udid, (nint)0);
            // B has no prepared capture/binding: the attempted start must fail
            // for B, never resume A or leave its old connection as the target.
            AwaitMapping(vm.ToggleBluetoothControlAsync(second.Udid));
            Require(!vm.IsBluetoothControlEnabled && vm.BluetoothControlTargetUdid is null &&
                vm.ControlStatus.Current?.Stage == ControlStage.Failed,
                "requesting device B resumed device A instead of starting B's workflow.");
            SetKeyboardField(vm, "_selectedDevice", device);
            Console.WriteLine("PASS device switch: F9 on B stops paused A and attempts B without resuming the old target.");

            Ready();
            reports.Clear();
            failedReport = 1;
            Toggle();
            Require(vm.IsBluetoothControlEnabled && vm.BluetoothControlIsInputPaused,
                "an unused optional keyboard report disconnected a mouse-only peer.");
            failedReport = null;
            Toggle();
            Console.WriteLine("PASS optional HID reports: an unused keyboard/consumer/navigation channel does not prevent a retained mouse connection.");

            foreach (var absolute in new[] { false, true })
            foreach (var failedId in new byte[] { absolute ? (byte)6 : (byte)2, 1, 4, 5 })
            {
                Ready();
                SetKeyboardField(service, "_useAbsoluteMouse", absolute);
                reports[1] = [0, 0, 4, 0, 0, 0, 0, 0];
                reports[4] = [0xE9, 0];
                reports[5] = [2, 0];
                failedReport = failedId;
                Toggle();
                Require(!vm.IsBluetoothControlEnabled && !vm.BluetoothControlIsInputEnabled &&
                    vm.BluetoothControlTargetUdid is null,
                    $"failed release for HID report {failedId} retained the connection.");
            }
            failedReport = null;
            SetKeyboardField(service, "_useAbsoluteMouse", false);

            foreach (var result in new (GattCommunicationStatus Status, ushort Bytes, byte? ProtocolError)[]
            {
                (GattCommunicationStatus.Unreachable, 0, null),
                (GattCommunicationStatus.ProtocolError, 0, 0x0E),
                (GattCommunicationStatus.AccessDenied, 0, null),
                (GattCommunicationStatus.Success, 3, null),
                (GattCommunicationStatus.Success, 6, 0x0E),
            })
            {
                Ready();
                reports.Clear();
                notificationResult = result;
                Toggle();
                Require(!vm.IsBluetoothControlEnabled && vm.BluetoothControlTargetUdid is null,
                    $"completed notification {result} was treated as successful pause delivery.");
            }
            notificationResult = null;
            Console.WriteLine("PASS completed notification failure: unreachable/protocol/access-denied/short-write results close the paused connection.");

            Ready();
            reports.Clear();
            reports[1] = new byte[8]; // A previously submitted key-up may have failed.
            SetKeyboardField(service, "_submittedInputReports", 1 << 1);
            failedReport = 1;
            Toggle();
            Require(!vm.IsBluetoothControlEnabled,
                "a cached zero keyboard report hid a failed key-up and bypassed checked release.");
            failedReport = null;

            Ready();
            delayedDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
            deliveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeoutPause = vm.ToggleBluetoothControlAsync(target);
            AwaitMapping(deliveryEntered.Task);
            // F9 arriving during a failed pause must not restart the connection
            // once the failure path has advanced the model's session token.
            var obsoleteResume = vm.ToggleBluetoothControlAsync(target);
            AwaitMapping(Task.WhenAll(timeoutPause, obsoleteResume));
            Require(!vm.IsBluetoothControlEnabled && vm.BluetoothControlTargetUdid is null &&
                delayedGuard?.Invoke() == false,
                "release timeout retained the connection, admitted a late report or replayed queued resume.");
            delayedDelivery.SetResult(true);
            delayedDelivery = null;
            deliveryEntered = null;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            Console.WriteLine("PASS release failure: mouse/keyboard/consumer/navigation failures and timeout close the connection; late delivery and queued resume stay revoked.");

            // Also exercise the real sender's bounded target-lock failure,
            // with no GATT device and without the successful-delivery fixture.
            SetKeyboardField(service, "_sendInputReport", originalSender);
            Ready();
            transportGate.Wait();
            try
            {
                Toggle();
                Require(!vm.IsBluetoothControlEnabled && vm.BluetoothControlTargetUdid is null,
                    "production target-lock timeout retained a paused connection.");
            }
            finally { transportGate.Release(); }
            Console.WriteLine("PASS production release path: target-lock timeout closes the connection instead of reporting a successful pause.");
            TestAbsoluteMouseSettings(app, vm);
            return 0;
        }
        finally
        {
            source.RemoveHook(FixtureMessageHook);
            source.AddHook(messageHook);
            main.Activated += activated;
            main.Deactivated += deactivated;
            main.PreviewGotKeyboardFocus += keyboardFocus;
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            KeyboardCall(main, "ClearBluetoothControlInputState");
            windows.Clear();
            shell?.Dispose();
            SetKeyboardField(main, "_activeControlWindow", (nint)0);
            SetKeyboardField(main, "_activeControlUdid", null);
            sessions.Remove(target);
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static async Task TestBluetoothPauseRouteChangeAsync()
    {
        foreach (var absolute in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = 0;
            await using var service = new BluetoothHidMouseService(async (_, _, guard) =>
            {
                entered.TrySetResult();
                await complete.Task;
                if (guard?.Invoke() != false) Interlocked.Increment(ref admitted);
                return true;
            });
            SetKeyboardField(service, "_targetDeviceUdid", "pause-route-A");
            SetKeyboardField(service, "_useAbsoluteMouse", absolute);
            SetKeyboardField(service, "_targetClientId", "client-A");
            var release = service.PauseInputAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            SetKeyboardField(service, "_routeGeneration", (int)KeyboardField(service, "_routeGeneration") + 1);
            SetKeyboardField(service, "_targetClientId", "client-B");
            complete.SetResult();
            MappingAssert(!await release && admitted == 0,
                "A release belonging to the old BLE route reached a replacement client or reported success.");
            MappingAssert(!service.ResumeInput("pause-route-B"),
                "Resume admitted an unrelated target to the stopped pump.");
        }
        Console.WriteLine("PASS Bluetooth pause route fence: a replaced BLE client rejects delayed releases and wrong-target resume.");
    }

    private static void TestBluetoothNotificationResults()
    {
        MappingAssert(BluetoothHidMouseService.NotificationSucceeded(GattCommunicationStatus.Success, 6, 6),
            "Complete successful notification was rejected.");
        foreach (var status in Enum.GetValues<GattCommunicationStatus>().Where(s => s != GattCommunicationStatus.Success))
            MappingAssert(!BluetoothHidMouseService.NotificationSucceeded(status, 6, 6),
                $"Completed {status} notification was accepted as delivery.");
        MappingAssert(!BluetoothHidMouseService.NotificationSucceeded(GattCommunicationStatus.Success, 5, 6) &&
            !BluetoothHidMouseService.NotificationSucceeded(GattCommunicationStatus.Success, 6, 6, 0x0E),
            "Short notification or protocol error was accepted as delivery.");
        Console.WriteLine("PASS notification result contract: only Success with exact bytes and no protocol error confirms delivery.");
    }

    private static void TestWindowBluetoothMouseHandoff(MainWindow window, DeviceViewModel device,
        nint independent, Action<nint> setForeground)
    {
        SetKeyboardField(window, "_bluetoothWindowMouseEnabled", true);
        var vm = KeyboardField(window, "_viewModel");
        var service = KeyboardField(vm, "_bluetoothControl");
        var main = new WindowInteropHelper(window).Handle;
        var preview = KeyboardField(window, "MainPreviewHost");
        var sends = (HashSet<Task>)KeyboardField(window, "_keyboardSends");
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        try
        {
            SetKeyboardField(window, "_controlButtons", (byte)1);
            KeyboardCall(window, "ApplyBluetoothControlInputState", false);
            InteractionAssert((byte)KeyboardField(window, "_controlButtons") == 0,
                "Enabling absolute input must discard previous drag state.");

            foreach (var owner in new[] { main, independent })
            {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                sends.Add(pending.Task);
                try
                {
                    setForeground(owner);
                    KeyboardCall(window, "FocusControlDevice", device.Udid, owner == main ? (nint)0 : owner);
                    KeyboardCall(window, "ResetKeyboardOwnership");
                    var handoff = (Task)KeyboardField(window, "_keyboardHandoff");
                    InteractionAssert(!handoff.IsCompleted &&
                        !(bool)KeyboardField(window, "_rawMouseInputEnabled"),
                        "Absolute window mode must keep regular mouse input while keyboard input drains.");
                    pending.SetResult();
                    AwaitMapping(handoff);
                    InteractionAssert(!(bool)KeyboardField(window, "_rawMouseInputEnabled") &&
                        !(bool)KeyboardField(preview, "SuppressLegacyMouseButtons"),
                        "A delayed keyboard handoff must not suppress normal mouse clicks in either preview.");
                }
                finally
                {
                    pending.TrySetResult();
                    sends.Remove(pending.Task);
                }
            }

            void Pointer(PreviewPointerKind kind, byte button = 0, nint? keys = null)
            {
                KeyboardCall(window, "OnIndependentPointerInput", device.Udid,
                    new PreviewPointerEventArgs(kind, 150, 150, button, 0, 300, 300, 100, 300)
                    {
                        WindowsButtons = keys is nint state ? NativePreviewMouse.PressedButtons(state) : null,
                    });
            }
            byte Buttons() => (byte)KeyboardField(window, "_controlButtons");
            Pointer(PreviewPointerKind.Move, keys: 0x01);
            InteractionAssert(Buttons() == 0, "Movement must not inherit a Windows button held before activation.");
            Pointer(PreviewPointerKind.ButtonDown, button: 1);
            Pointer(PreviewPointerKind.ButtonDown, button: 2);
            Pointer(PreviewPointerKind.ButtonDown, button: 4);
            Pointer(PreviewPointerKind.Move, keys: 0x12);
            InteractionAssert(Buttons() == 6, "Movement must clear only the released button and retain right/middle.");
            Pointer(PreviewPointerKind.Wheel, keys: unchecked((nint)0xFF880000L));
            InteractionAssert(Buttons() == 0, "A wheel snapshot must clear missed releases even without a scroll tick.");
            TestAbsoluteWindowGeometry(window, device);
        }
        finally
        {
            SetKeyboardField(service, "_useAbsoluteMouse", false);
        }
        Console.WriteLine("PASS absolute mouse: both-window handoff, startup drag isolation and missed-release recovery.");
    }

    private static void TestWindowBluetoothPointer(MainWindow window, DeviceViewModel device)
    {
        KeyboardCall(window, "ResetControlRouteState");
        SetKeyboardField(window, "_bluetoothWindowMouseEnabled", true);
        KeyboardCall(window, "ApplyBluetoothControlInputState", false);
        InteractionAssert(!(bool)KeyboardField(window, "_rawMouseInputEnabled") &&
            !(bool)KeyboardField(window, "_windowsCursorHidden"),
            "Window mouse must leave Windows mouse messages and cursor available.");

        void Send(PreviewPointerKind kind, short x = 150, byte button = 0, int wheel = 0,
            int width = 300) => KeyboardCall(window, "OnIndependentPointerInput", device.Udid,
            new PreviewPointerEventArgs(kind, x, 150, button, wheel, width, 300, 100, 300));
        byte Buttons() => (byte)KeyboardField(window, "_controlButtons");
        int Delta() => (int)KeyboardField(window, "_pendingControlDx");

        Send(PreviewPointerKind.ButtonDown, x: 20, button: 1);
        InteractionAssert(Buttons() == 0, "Clicks on black bars must stay local.");
        Send(PreviewPointerKind.Move, x: 145);
        InteractionAssert(Delta() == 0, "Entering the picture must anchor relative movement.");
        Send(PreviewPointerKind.ButtonDown, button: 1);
        Send(PreviewPointerKind.Move, x: 170);
        InteractionAssert(Buttons() == 1 && Delta() != 0, "An in-picture drag must send motion and a held button.");
        Send(PreviewPointerKind.Move, x: 210);
        InteractionAssert(Buttons() == 0 && Delta() == 0,
            "Crossing into a black bar must release the drag and discard queued movement.");
        Send(PreviewPointerKind.Move, x: 165);
        Send(PreviewPointerKind.ButtonUp, button: 1);
        InteractionAssert(Buttons() == 0 && Delta() == 0,
            "Re-entry must anchor again without restarting the old drag.");
        Send(PreviewPointerKind.Wheel, wheel: 120);
        InteractionAssert((int)KeyboardField(window, "_pendingControlWheel") != 0,
            "The wheel must reach the device inside the picture.");
        Send(PreviewPointerKind.Move, x: 175, width: 320);
        InteractionAssert(Delta() == 0, "Resizing must re-anchor without a pointer jump.");

        Send(PreviewPointerKind.ButtonDown, button: 1);
        Send(PreviewPointerKind.Reset); // WM_MOUSELEAVE or capture/focus loss
        InteractionAssert(Buttons() == 0 && (int)KeyboardField(window, "_pendingControlWheel") == 0,
            "Leaving the HWND must release the drag and clear pending wheel input.");
        KeyboardCall(window, "ResetControlRouteState");
        for (var i = 0; i < 40; i++)
        {
            Send(PreviewPointerKind.ButtonDown, button: 1);
            Send(PreviewPointerKind.ButtonUp, button: 1);
        }
        var queued = (Queue<(int Dx, int Dy, byte Buttons, long At)>)KeyboardField(window, "_windowMouseButtonReports");
        InteractionAssert(queued.Count <= 9 && queued.Peek() is (0, 0, 0, 0) && queued.Last().Buttons == 0,
            "The window-level overflow must begin with an empty release before later independent gestures.");
        Console.WriteLine("Bluetooth window pointer: legacy Windows input, black bars, drag release, re-entry, wheel and resize passed.");
    }

    private static async Task TestAbsoluteWindowMouseAsync()
    {
        var delivered = new ConcurrentQueue<(byte Id, byte[] Payload)>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (id is 2 or 6 && guard?.Invoke() != false) delivered.Enqueue((id, payload.ToArray()));
            return Task.FromResult(true);
        });
        const string target = "absolute-window-mouse";
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", target);
        void HoldPump() => SetKeyboardField(service, "_mousePumpRunning", true);
        Task Pump() => (Task)KeyboardCall(service, "PumpReportsAsync",
            KeyboardField(service, "_mousePumpGeneration"))!;
        Task Point(double x, double y, byte buttons, bool reset = false) =>
            service.SendAbsoluteMouseAsync(x, y, buttons, 0, target, () => true, reset);
        static int X(byte[] report) => report[1] | report[2] << 8;

        HoldPump();
        await Point(0, 1, 1);
        await Point(0, 1, 0);
        await Pump();
        MappingAssert(delivered.Select(report => (report.Id, report.Payload[0], X(report.Payload)))
                .SequenceEqual(new (byte, byte, int)[] { (6, 0, 1), (2, 0, 327), (2, 0, 0), (6, 1, 0), (6, 0, 0), (2, 0, 0) }),
            "A quick click must position first and preserve both relative button edges.");
        delivered.Clear();

        for (var i = 0; i < 2; i++)
        {
            HoldPump();
            await Point(0.5, 0.25, 1);
            await Point(0.75, 0.25, 0);
            await Pump();
            var drag = delivered.ToArray();
            var pressed = Array.FindIndex(drag, r => r.Id == 6 && r.Payload[0] == 1);
            var released = Array.FindLastIndex(drag, r => r.Id == 6 && r.Payload[0] == 0);
            MappingAssert(pressed > 0 && released > pressed &&
                drag.Skip(pressed + 1).Take(released - pressed - 1).All(r => r.Id == 6 && r.Payload[0] == 1 && X(r.Payload) is > 0 and <= 48) &&
                drag.Last().Id == 2 && X(drag.Last().Payload) == 24575,
                "Consecutive drags must preserve the contact with bounded relative steps, release, then restore the anchor.");
            delivered.Clear();
        }

        HoldPump();
        await Point(0.5, 0.25, 1);
        await Point(0.75, 0.25, 1);
        await Pump();
        delivered.Clear();

        HoldPump();
        await Point(0.5, 0.25, 0);
        await Pump();
        var returnDrag = delivered.ToArray();
        MappingAssert(returnDrag[^3].Id == 6 && returnDrag[^3].Payload[0] == 0 &&
                returnDrag[^2].Id == 2 && X(returnDrag[^2].Payload) != 16384 &&
                returnDrag[^1].Id == 2 && X(returnDrag[^1].Payload) == 16384,
            "A drag that returns to its original position needs a changed anchor before restoring that position.");
        delivered.Clear();

        HoldPump();
        await Point(0.5, 0.25, 1);
        await Pump();
        delivered.Clear();

        HoldPump();
        await Point(0.9, 0.9, 1);
        await Point(0.75, 0.25, 0, reset: true);
        await Point(1, 0, 0);
        await Pump();
        MappingAssert(delivered.Select(report => (report.Id, report.Payload[0], X(report.Payload)))
                .SequenceEqual(new (byte, byte, int)[] { (6, 0, 0), (2, 0, 24575), (2, 0, 32767) }),
            "Leaving the surface must release without sending stale drag movement before re-entry.");
        delivered.Clear();

        HoldPump();
        for (var i = 0; i < 10; i++)
        {
            await Point((i + 1) / 11.0, 0.5, 1);
            await Point((i + 1) / 11.0, 0.5, 0);
        }
        await Pump();
        MappingAssert(delivered.Where(report => report.Id == 6 && report.Payload.Skip(1).All(value => value == 0)).Select(report => report.Payload[0])
            .SequenceEqual(Enumerable.Range(0, 20).Select(i => (byte)(i % 2 == 0 ? 1 : 0))),
            "A ten-click burst must retain every down/up, including while the BLE pump is busy.");
        delivered.Clear();

        foreach (var direction in new[] { -1, 1 })
        {
            HoldPump();
            await service.SendAbsoluteMouseAsync(0.5, 0.5, 0, direction, target, () => true);
            await Pump();
            var gesture = delivered.Where(report => report.Id == 6 && report.Payload[1] == 0).Select(report => report.Payload).ToArray();
            MappingAssert(gesture.First()[0] == 1 && gesture.Last()[0] == 0 &&
                gesture.All(report => report[5] == 0) &&
                gesture.Sum(report => (short)(report[3] | report[4] << 8)) == -direction * 24,
                "Wheel input must produce a bounded BLE drag in the requested direction and release it.");
            var anchor = delivered.Last();
            MappingAssert(anchor.Id == 2 && X(anchor.Payload) == 16384 &&
                (anchor.Payload[3] | anchor.Payload[4] << 8) == 16384,
                "Scrolling must restore the absolute cursor to its original position.");
            delivered.Clear();
        }

        MappingAssert(await service.PauseInputAsync(), "Absolute mouse pause could not release the device.");
        MappingAssert(delivered.Select(report => (report.Id, report.Payload[0], X(report.Payload)))
                .SequenceEqual(new (byte, byte, int)[] { (6, 0, 0) }),
            "Pausing must release the relative buttons without repositioning.");
        Console.WriteLine("PASS hybrid window mouse: click, consecutive drags, re-anchor, re-entry and pause.");
    }

    private static async Task TestAbsoluteMousePressRetryAsync()
    {
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentQueue<byte[]>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (id != 6 || payload[0] != 1) return Task.FromResult(true);
            attempts.Enqueue(payload.ToArray());
            if (attempts.Count == 1) return Task.FromResult(false);
            retried.TrySetResult();
            return Task.FromResult(true);
        });
        const string target = "absolute-press-retry";
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", target);
        await service.SendAbsoluteMouseAsync(0.5, 0.5, 1, 0, target, () => true);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(3));
        MappingAssert(attempts.Count == 2 && attempts.All(report => report.Skip(1).All(value => value == 0)),
            "A failed absolute-mode press must retry the held button without replaying motion.");
        MappingAssert(await service.PauseInputAsync(), "A retried press must remain releasable.");

        // Even an unacknowledged press might have reached iOS. Cleanup must
        // release it when input leaves before the next delivery attempt.
        var reports = new ConcurrentQueue<(byte Id, byte[] Payload)>();
        await using var uncertain = new BluetoothHidMouseService((id, payload, guard) =>
        {
            reports.Enqueue((id, payload.ToArray()));
            return Task.FromResult(id != 6 || payload[0] == 0);
        });
        SetKeyboardField(uncertain, "_useAbsoluteMouse", true);
        SetKeyboardField(uncertain, "_targetDeviceUdid", target);
        var generation = KeyboardField(uncertain, "_mousePumpGeneration");
        byte[] press = [1, 0, 64, 0, 64, 0];
        MappingAssert(!await (Task<bool>)KeyboardCall(uncertain, "SendHybridMouseAsync",
            press, false, generation, (Func<bool>)(() => true))!, "The injected press failure was not exercised.");
        byte[] release = [0, 0, 64, 0, 64, 0];
        MappingAssert(await (Task<bool>)KeyboardCall(uncertain, "SendHybridMouseAsync",
            release, true, generation, (Func<bool>)(() => true))!, "Uncertain press cleanup failed.");
        MappingAssert(reports.Where(report => report.Id == 6).Select(report => report.Payload[0])
            .SequenceEqual(new byte[] { 0, 1, 0 }), "The first collection activation must be button-free, and leaving after a failed press must still deliver a release.");

        var settings = new IPhoneMirror.App.Updater.UpdateSettings();
        MappingAssert(!settings.BluetoothWindowMouseEnabled && !settings.Clone().BluetoothWindowMouseEnabled,
            "Experimental absolute positioning must default to off.");
        settings.BluetoothWindowMouseEnabled = true;
        MappingAssert(settings.Clone().BluetoothWindowMouseEnabled,
            "The experimental positioning opt-in must survive settings copies.");
        Console.WriteLine("PASS absolute mouse: failed press retry, uncertain press release and default-off setting.");
    }

    private static async Task TestAbsoluteMouseInputLifecycleAsync()
    {
        var samples = new ConcurrentQueue<(byte Id, byte[] Payload, long At)>();
        var scrollMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowInput = true;
        var cancelScroll = false;
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (guard?.Invoke() == false) return Task.FromResult(true);
            samples.Enqueue((id, payload.ToArray(), System.Diagnostics.Stopwatch.GetTimestamp()));
            if (cancelScroll && id == 6 && payload[0] == 1 && payload[3] != 0)
            {
                allowInput = false;
                scrollMoved.TrySetResult();
            }
            return Task.FromResult(true);
        });
        const string target = "absolute-lifecycle";
        SetKeyboardField(service, "_useAbsoluteMouse", true);
        SetKeyboardField(service, "_targetDeviceUdid", target);
        SetKeyboardField(service, "_mousePumpRunning", true);
        foreach (var x in new[] { 0.25, 0.75 })
        {
            await service.SendAbsoluteMouseAsync(x, 0.5, 1, 0, target, () => true);
            await service.SendAbsoluteMouseAsync(x, 0.5, 0, 0, target, () => true);
        }
        await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
        var reports = samples.ToArray();
        var down = Array.FindIndex(reports, report => report.Id == 6 && report.Payload[0] == 1);
        var up = Array.FindIndex(reports, down + 1, report => report.Id == 6 && report.Payload[0] == 0);
        var anchorAfterUp = Array.FindIndex(reports, up + 1, report => report.Id == 2);
        static double Milliseconds(long elapsed) => elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        MappingAssert(Milliseconds(reports[up].At - reports[down].At) >= 25 &&
            Milliseconds(reports[anchorAfterUp].At - reports[up].At) >= 100,
            "Queued clicks must leave iOS time to observe down/up before repositioning the absolute pointer.");

        samples.Clear();
        cancelScroll = true;
        await service.SendAbsoluteMouseAsync(0.5, 0.5, 0, 3, target, () => allowInput);
        await scrollMoved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.SendAbsoluteMouseAsync(0.5, 0.5, 0, 0, target, () => true, reset: true);
        await ((Task)KeyboardField(service, "_mousePumpTask")).WaitAsync(TimeSpan.FromSeconds(3));
        var gesture = samples.Where(report => report.Id == 6).Select(report => report.Payload).ToArray();
        MappingAssert(gesture.Count(report => report[0] == 1 && report[3] != 0) == 1 &&
            gesture.Last()[0] == 0,
            "Leaving mid-scroll must cancel the remaining movement and release the synthetic button.");
        Console.WriteLine("PASS absolute mouse: click pacing and mid-scroll leave cleanup.");
    }

    private static async Task TestWindowBluetoothMouseClickAsync()
    {
        var delivered = new ConcurrentQueue<byte>();
        await using var service = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (id == 2 && guard?.Invoke() != false) delivered.Enqueue(payload[0]);
            return Task.FromResult(true);
        });
        SetKeyboardField(service, "_targetDeviceUdid", "window-mouse");
        SetKeyboardField(service, "_mousePumpRunning", true);
        Task Send(byte buttons, int wheel = 0) => service.SendMouseAsync(0, 0, buttons, wheel,
            "window-mouse", preserveButtonTransitions: true);
        await Send(1);
        await Send(1, wheel: 1);
        await Send(0);
        await (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
        MappingAssert(delivered.SequenceEqual(new byte[] { 1, 0 }),
            "A fast window click must retain both button transitions while BLE is busy.");
        Console.WriteLine("PASS Bluetooth window mouse: queued click edges survive a wheel tick and a busy BLE pump.");
    }

    private static async Task TestBluetoothMouseAdmissionAsync()
    {
        var delivered = new ConcurrentQueue<byte[]>();
        TaskCompletionSource? sendEntered = null;
        TaskCompletionSource? sendAllowed = null;
        await using var service = new BluetoothHidMouseService(async (id, payload, guard) =>
        {
            if (sendAllowed is { } wait)
            {
                sendEntered?.TrySetResult();
                await wait.Task;
            }
            if (id == 2 && guard?.Invoke() != false) delivered.Enqueue(payload.ToArray());
            return true;
        });
        SetKeyboardField(service, "_targetDeviceUdid", "mouse-admission");
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        service.InputAdmission = () =>
        {
            entered.Set();
            if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return null!;
        };
        var oldSend = Task.Run(() => service.SendMouseAsync(39, 17, buttons: 1, expectedTargetDeviceUdid: "mouse-admission"));
        try
        {
            MappingAssert(entered.Wait(TimeSpan.FromSeconds(3)), "Mouse send never reached admission.");
            MappingAssert(await service.PauseInputAsync(), "Offline release fixture failed.");
            MappingAssert(service.ResumeInput("mouse-admission"), "Mouse fixture did not resume.");
            delivered.Clear();
            proceed.Set();
            await oldSend;
            MappingAssert(delivered.IsEmpty && KeyboardField(service, "_pendingMouseReport") is null &&
                (int)KeyboardField(KeyboardField(service, "_mousePriorityReports"), "Count") == 0,
                "Service admitted a pre-pause mouse producer after resume.");
        }
        finally { proceed.Set(); await oldSend; service.InputAdmission = null; }

        foreach (var kind in new[] { "motion", "button", "wheel" })
        {
            SetKeyboardField(service, "_mousePumpRunning", true);
            var allowed = true;
            await service.SendMouseAsync(7, 9, kind == "button" ? (byte)1 : (byte)0,
                kind == "wheel" ? 1 : 0, "mouse-admission", () => allowed);
            delivered.Clear();
            sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sendAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pump = (Task)KeyboardCall(service, "PumpReportsAsync", KeyboardField(service, "_mousePumpGeneration"))!;
            try
            {
                await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                allowed = false;
            }
            finally { sendAllowed.SetResult(); }
            await pump;
            sendEntered = null;
            sendAllowed = null;
            MappingAssert(delivered.IsEmpty, $"Queued {kind} lost its final foreground guard.");
        }
        Console.WriteLine("PASS mouse admission: service rejects producer crossing pause/resume; queued motion/button/wheel preserve their final guard.");
    }

    private static async Task TestBluetoothHidSessionAdmissionAsync()
    {
        foreach (var reportId in new byte[] { 1, 4, 5 })
        {
            var delivered = new ConcurrentQueue<(byte Id, byte[] Payload)>();
            await using var service = new BluetoothHidMouseService((id, payload, guard) =>
            {
                if (guard?.Invoke() != false) delivered.Enqueue((id, payload.ToArray()));
                return Task.FromResult(true);
            });
            SetKeyboardField(service, "_targetDeviceUdid", "hid-admission");
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            service.InputAdmission = () =>
            {
                entered.Set();
                if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return null!;
            };
            Task Send() => reportId switch
            {
                1 => service.SendKeyboardAsync(0, [4]),
                4 => (Task)KeyboardCall(service, "SendConsumerAsync", (ushort)0xE9, null)!,
                _ => (Task)KeyboardCall(service, "SendNavigationAsync", (ushort)2, null)!,
            };
            var oldSend = Task.Run(Send);
            try
            {
                MappingAssert(entered.Wait(TimeSpan.FromSeconds(3)), $"HID {reportId} missed admission.");
                MappingAssert(await service.PauseInputAsync() && service.ResumeInput("hid-admission"),
                    "HID admission fixture did not pause/resume.");
                delivered.Clear();
                proceed.Set();
                await oldSend;
                MappingAssert(delivered.IsEmpty, $"HID {reportId} producer crossed pause/resume.");
                service.InputAdmission = null;
                await Send();
                MappingAssert(delivered.Any(report => report.Id == reportId && report.Payload.Any(value => value != 0)),
                    $"HID {reportId} fresh input did not resume.");
            }
            finally { proceed.Set(); await oldSend; service.InputAdmission = null; }
        }

        var releases = new ConcurrentQueue<byte[]>();
        await using var shortcutService = new BluetoothHidMouseService((id, payload, guard) =>
        {
            if (id == 4 && guard?.Invoke() != false) releases.Enqueue(payload.ToArray());
            return Task.FromResult(true);
        });
        SetKeyboardField(shortcutService, "_targetDeviceUdid", "shortcut-admission");
        using var releaseEntered = new ManualResetEventSlim();
        using var releaseAllowed = new ManualResetEventSlim();
        var admissions = 0;
        shortcutService.InputAdmission = () =>
        {
            if (Interlocked.Increment(ref admissions) == 2)
            {
                releaseEntered.Set();
                if (!releaseAllowed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return null!;
        };
        var shortcut = Task.Run(() => shortcutService.SendIphoneConsumerShortcutAsync(0xE9, 0, "shortcut-admission"));
        try
        {
            MappingAssert(releaseEntered.Wait(TimeSpan.FromSeconds(3)), "Shortcut release missed admission.");
            MappingAssert(await shortcutService.PauseInputAsync() && shortcutService.ResumeInput("shortcut-admission"),
                "Shortcut fixture did not pause/resume.");
            releases.Clear();
            releaseAllowed.Set();
            await shortcut;
            MappingAssert(releases.IsEmpty, "An old shortcut release reached the resumed input session.");
        }
        finally { releaseAllowed.Set(); await shortcut; shortcutService.InputAdmission = null; }
        Console.WriteLine("PASS HID admission: old keyboard/consumer/navigation producers and shortcut releases stay revoked across pause/resume; fresh input works.");
    }
}
