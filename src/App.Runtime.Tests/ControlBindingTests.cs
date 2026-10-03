using System.Diagnostics;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunControlBindingTests(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        // Device discovery has its own modal onboarding prompt. This fixture
        // exercises control entries after discovery, so detach only that UI hook.
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), main,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        // Use an isolated binding store; never read or modify real device bindings.
        var storePath = Path.Combine(Path.GetFullPath(output), $"bindings-{Guid.NewGuid():N}.json");
        var bindings = new DeviceBindingManager(storePath);
        var resolver = new DeviceIdentityResolver(bindings);
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_identityResolver", resolver);
        // Read the existing XAML buttons from their logical parent; realizing the
        // preview toolbar normally requires an active capture session.
        var buttons = ((StackPanel)main.FindName("PreviewQuickActions")).Children.OfType<Button>().ToArray();
        var wired = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "UsbControlButton");
        var wireless = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "WirelessControlButton");
        var controls = (Dictionary<string, DeviceControlSession>)KeyboardField(vm, "_deviceControls");

        DeviceViewModel Device(string id) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = "Binding test iPhone", State = ConnectionState.Ready });
        void Select(DeviceViewModel? device)
        {
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(vm, "NotifyUsbControlStateChanged");
            PumpBinding(TimeSpan.FromMilliseconds(30));
        }
        Task Start(bool network, string? id = null) => network ? vm.StartWirelessControlAsync(id) : vm.StartUsbControlAsync(id);
        void Reset()
        {
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            controls.Clear();
            vm.Devices.Clear();
            Select(null);
            SetKeyboardField(vm, "_wiredControlPrerequisiteAcknowledged", false);
            SetKeyboardField(vm, "_wirelessControlPrerequisiteAcknowledged", false);
        }
        void NoResources(DeviceControlSession control)
        {
            BindingAssert(control.WiredBridge is null && control.WirelessBridge is null &&
                !control.Enabled && !control.InputEnabled && control.Router.Mode == ReverseControlMode.None,
                "A rejected binding initialized a bridge or input route.");
        }
        void Failed(DeviceControlSession control)
        {
            var status = control.ControlStatus.Current!;
            BindingAssert(status is { Stage: ControlStage.Failed, IsTerminal: true, Prompt: null } &&
                status.Error!.Contains(LocalizationService.Get("ControlBindingRequired")) &&
                status.Error.Contains(LocalizationService.Get("ControlBindingAdvice")),
                "Binding failure must be terminal and expose binding instructions without opening details.");
            NoResources(control);
        }
        try
        {
            // Exercise the actual toolbar bindings and click handlers for both transports
            // and both mirror identity types, including a profile belonging to another phone.
            bindings.CreateProfileFromIdentity("Other phone", DeviceIdentityType.Wired, "other-phone", null);
            foreach (var network in new[] { false, true })
            foreach (var airplay in new[] { false, true })
            {
                Reset();
                var device = Device((airplay ? "airplay://" : "") + $"unbound-{network}-{airplay}");
                var partialProfile = airplay ? bindings.CreateProfileFromIdentity("AirPlay only",
                    DeviceIdentityType.AirPlay, device.Udid, null).Profile : null;
                vm.Devices.Add(device);
                Select(device);
                BindingAssert(wired.IsEnabled && wireless.IsEnabled && vm.CanStartUsbControl && vm.CanStartWirelessControl &&
                    vm.ToggleUsbControlCommand.CanExecute(null), "Binding disabled a control entry.");
                var status = vm.GetControlStatus(network ? ControlStatusMode.Wireless : ControlStatusMode.Usb);
                var stages = new List<ControlStage>();
                var windowPresentAtFailure = false;
                status.StatusChanged += (_, snapshot) =>
                {
                    stages.Add(snapshot.Stage);
                    if (snapshot.Stage == ControlStage.Failed)
                        windowPresentAtFailure = app.Windows.OfType<ReverseControlStatusWindow>().Any(w => w.IsVisible);
                };
                (network ? wireless : wired).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpBinding(TimeSpan.FromMilliseconds(100));
                var control = controls[device.Udid];
                Failed(control);
                BindingAssert(windowPresentAtFailure && stages[0] == ControlStage.CheckingBinding &&
                    stages.All(stage => stage is ControlStage.CheckingBinding or ControlStage.Failed),
                    "Window must open before binding fails, with no later startup stages.");
                var windows = app.Windows.OfType<ReverseControlStatusWindow>().ToArray();
                BindingAssert(windows.Length == 1, "Binding failure opened duplicate windows.");
                BindingAssert(((ReverseControlStatusViewModel)windows[0].DataContext).StageDescription == status.Current!.Error,
                    "Visible failure text omitted the next step.");
                SaveWindowRender(windows[0], Path.Combine(output, $"unbound-{network}-{airplay}.png"));
                AwaitBinding(Start(network));
                BindingAssert(app.Windows.OfType<ReverseControlStatusWindow>().Count() == 1, "Retry duplicated the status window.");
                // Complete the missing identity, then retry the same failed session.
                if (partialProfile is not null)
                    bindings.Bind(partialProfile.Id, DeviceIdentityType.Wired, "usb-" + device.Udid, device.Name, null, true);
                else bindings.CreateProfileFromIdentity(device.Name, DeviceIdentityType.Wired, device.Udid, null);
                var retry = Start(network);
                BindingAssert(control.ControlStatus.Current?.Prompt?.Type == ControlPromptType.Confirmation && !control.Failed,
                    "Retry reused a cached missing binding after the user completed it.");
                control.ControlStatus.ResolvePrompt(new(ControlPromptAction.Cancel));
                AwaitBinding(retry);
            }
            Console.WriteLine("PASS toolbar USB/wireless × USB/AirPlay: enabled when unbound, window first, terminal guidance, no resources, single window on retry.");

            foreach (var network in new[] { false, true })
            foreach (var airplay in new[] { false, true })
            {
                Reset();
                var usbId = $"bound-{network}-{airplay}";
                var device = Device(airplay ? "airplay://" + usbId : usbId);
                vm.Devices.Add(device);
                Select(device);
                var profile = bindings.CreateProfileFromIdentity("Bound phone", DeviceIdentityType.Wired, usbId, null).Profile!;
                if (airplay) bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, device.Udid, device.Name, null, true);
                var task = Start(network);
                var control = controls[device.Udid];
                BindingAssert(!task.IsCompleted && control.ControlStatus.Current?.Prompt?.Type == ControlPromptType.Confirmation &&
                    control.Binding?.ProfileId == profile.Id && control.AppleUdid == usbId,
                    "Correct binding did not reach the existing prerequisite flow with the exact target.");
                var binding = control.Binding;
                AwaitBinding(Start(!network));
                AwaitBinding(Start(network));
                BindingAssert(ReferenceEquals(control.Binding, binding), "Duplicate/mode-switch request replaced a running initialization.");
                // A profile for a different selected device must not inherit this attempt.
                var other = Device("selected-B-" + usbId);
                vm.Devices.Add(other);
                Select(other);
                AwaitBinding(Start(network));
                Failed(controls[other.Udid]);
                BindingAssert(control.AppleUdid == usbId, "Selection changed the target of a pending operation.");
                control.ControlStatus.ResolvePrompt(new(ControlPromptAction.Cancel));
                AwaitBinding(task);
                NoResources(control);
                BindingAssert(control.ControlStatus.Current?.Stage == ControlStage.Cancelled, "Prerequisite cancellation regressed.");
            }
            Console.WriteLine("PASS correctly bound USB/AirPlay in both modes reaches existing confirmation; duplicate/mode switch suppressed; selection B never inherits A.");

            foreach (var network in new[] { false, true })
            foreach (var mutation in new[] { "unbind", "rebind", "remove", "replace" })
            {
                Reset();
                var id = $"pending-{network}-{mutation}";
                var device = Device("airplay://" + id);
                var profile = bindings.CreateProfileFromIdentity("Pending phone", DeviceIdentityType.Wired, id, null).Profile!;
                bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, device.Udid, device.Name, null, true);
                vm.Devices.Add(device);
                Select(device);
                var task = Start(network);
                var control = controls[device.Udid];
                BindingAssert(control.ControlStatus.Current?.Prompt is not null, "Missing prerequisite prompt.");
                if (mutation == "unbind") bindings.Unbind(profile.Id, DeviceIdentityType.Wired);
                if (mutation == "rebind") bindings.Bind(profile.Id, DeviceIdentityType.Wired, id + "-other", "Other", null, true);
                if (mutation is "remove" or "replace") vm.Devices.Remove(device);
                if (mutation == "replace") vm.Devices.Add(Device(device.Udid));
                control.ControlStatus.ResolvePrompt(new(ControlPromptAction.Primary));
                AwaitBinding(task);
                Failed(control);
                BindingAssert(control.ControlStatus.GetProgress(ControlStage.CheckingPermissions) == ControlStageProgress.Pending,
                    "Stale confirmation started permission/resource initialization.");
            }
            Console.WriteLine("PASS unbind, wrong-target rebind, removal and replacement during confirmation stop both modes before resource initialization.");

            foreach (var network in new[] { false, true })
            {
                Reset();
                var id = "gate-" + network;
                var device = Device(id);
                var profile = bindings.CreateProfileFromIdentity("Gate phone", DeviceIdentityType.Wired, id, null).Profile!;
                vm.Devices.Add(device);
                Select(device);
                SetKeyboardField(vm, network ? "_wirelessControlPrerequisiteAcknowledged" : "_wiredControlPrerequisiteAcknowledged", true);
                var gate = (SemaphoreSlim)KeyboardField(vm, "_lockdownHandshakeGate");
                BindingAssert(gate.Wait(0), "Handshake gate was unexpectedly busy.");
                var task = Start(network);
                var control = controls[id];
                var host = network ? control.WirelessBridge : control.WiredBridge;
                BindingAssert(host is { State: ReverseControlState.Idle } && !task.IsCompleted,
                    "Correct binding did not reach the original transport initialization.");
                bindings.Unbind(profile.Id, DeviceIdentityType.Wired);
                gate.Release();
                AwaitBinding(task);
                Failed(control);
                BindingAssert((string?)KeyboardField(host!, "_requestedUdid") is null,
                    "Binding changed while waiting, but bridge StartAsync still ran.");
            }
            Console.WriteLine("PASS both original transport paths reached after valid binding; changing binding during handshake wait prevents bridge StartAsync.");

            foreach (var network in new[] { false, true })
            {
                Reset();
                var id = "recovery-" + network;
                var device = Device("airplay://" + id);
                var profile = bindings.CreateProfileFromIdentity("Recovery phone", DeviceIdentityType.Wired, id, null).Profile!;
                bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, device.Udid, device.Name, null, true);
                vm.Devices.Add(device);
                Select(device);
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                control.Binding = resolver.ResolveControlBinding(device, network ? ReverseControlMode.Wireless : ReverseControlMode.Usb);
                control.AppleUdid = id;
                control.WirelessEnabled = network;
                control.WiredEnabled = !network;
                control.WirelessTarget = network ? device.Udid : null;
                control.WiredTarget = network ? null : device.Udid;
                control.ControlStatus.Begin(network ? ControlStatusMode.Wireless : ControlStatusMode.Usb, device.Name);
                bindings.Bind(profile.Id, DeviceIdentityType.Wired, id + "-other", "Other phone", null, true);
                AwaitBinding((Task)KeyboardCall(vm, network ? "RecoverWirelessControlAsync" : "RecoverUsbControlAsync", control)!);
                Failed(control);
                BindingAssert(control.AppleUdid == id, "Recovery retargeted to a newly bound phone.");
            }
            Console.WriteLine("PASS reconnect refuses a changed binding instead of retargeting an existing control session.");

            Reset();
            var cleanupDevice = Device("recovery-cleanup");
            var cleanupProfile = bindings.CreateProfileFromIdentity("Cleanup phone", DeviceIdentityType.Wired,
                cleanupDevice.Udid, null).Profile!;
            vm.Devices.Add(cleanupDevice);
            Select(cleanupDevice);
            var cleanupControl = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", cleanupDevice.Udid)!;
            cleanupControl.Binding = resolver.ResolveControlBinding(cleanupDevice, ReverseControlMode.Usb);
            cleanupControl.AppleUdid = cleanupDevice.Udid;
            cleanupControl.WiredEnabled = cleanupControl.WiredConnected = true;
            cleanupControl.WiredTarget = cleanupDevice.Udid;
            cleanupControl.ControlStatus.Begin(ControlStatusMode.Usb, cleanupDevice.Name);
            // Hold the real bridge's reader open so DisposeAsync yields without
            // starting a process or requiring an attached phone.
            var cleanupBridge = new UsbTouchBridgeHost();
            var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetKeyboardField(cleanupBridge, "_started", 1);
            SetKeyboardField(KeyboardField(cleanupBridge, "_bridge"), "_readerTask", cleanupRelease.Task);
            cleanupControl.WiredBridge = cleanupBridge;
            bindings.Unbind(cleanupProfile.Id, DeviceIdentityType.Wired);
            var recovery = (Task)KeyboardCall(vm, "RecoverUsbControlAsync", cleanupControl)!;
            ReverseControlStatusViewModel? cleanupStatus = null;
            try
            {
                PumpBinding(TimeSpan.FromMilliseconds(30));
                BindingAssert(!recovery.IsCompleted && cleanupControl.Stopping && !cleanupControl.Starting &&
                    !wired.IsEnabled && !wireless.IsEnabled, "Recovery cleanup must keep both transport entries busy.");
                Failed(cleanupControl);
                cleanupStatus = (ReverseControlStatusViewModel)app.Windows.OfType<ReverseControlStatusWindow>().Single().DataContext;
                BindingAssert(cleanupStatus.RetryVisibility == Visibility.Visible && cleanupStatus.RetryRequested is not null,
                    "Recovery failure did not expose the production Retry action.");
                var failure = cleanupControl.ControlStatus.Current;
                var originalBinding = cleanupControl.Binding;
                // Retry before and after repairing the binding, while disposal is
                // pending. Also try switching modes through the public entry.
                cleanupStatus.RetryRequested!();
                AwaitBinding(Start(true));
                bindings.Bind(cleanupProfile.Id, DeviceIdentityType.Wired, cleanupDevice.Udid, cleanupDevice.Name, null, true);
                cleanupStatus.RetryRequested();
                AwaitBinding(Start(true));
                BindingAssert(!recovery.IsCompleted && cleanupControl.Stopping && !cleanupControl.Starting &&
                    !cleanupControl.RequestedWireless && cleanupControl.AppleUdid == cleanupDevice.Udid &&
                    ReferenceEquals(cleanupControl.Binding, originalBinding) &&
                    ReferenceEquals(cleanupControl.ControlStatus.Current, failure),
                    "Retry or mode switch mutated the session while its old bridge was still being disposed.");
                Failed(cleanupControl);
            }
            finally
            {
                cleanupRelease.TrySetResult();
                AwaitBinding(recovery);
            }
            BindingAssert(!cleanupControl.Stopping && !cleanupControl.Starting && wired.IsEnabled && wireless.IsEnabled &&
                cleanupControl.Status == LocalizationService.Get("ControlBindingRequired"),
                "Completed cleanup left controls busy or overwrote binding guidance.");
            cleanupStatus!.RetryRequested!();
            BindingAssert(cleanupControl.Starting && !cleanupControl.Failed &&
                cleanupControl.ControlStatus.Current?.Prompt?.Type == ControlPromptType.Confirmation &&
                cleanupControl.AppleUdid == cleanupDevice.Udid,
                "Retry after cleanup did not enter the original startup flow with the repaired binding.");
            cleanupControl.ControlStatus.ResolvePrompt(new(ControlPromptAction.Cancel));
            AwaitBinding(cleanupControl.WiredOperation.CancelAsync());
            NoResources(cleanupControl);
            BindingAssert(!cleanupControl.Starting, "Retry remained stuck in Starting after cancellation.");
            Console.WriteLine("PASS delayed USB recovery cleanup: real Retry and wireless switch preserve failure state; entries recover and repaired-binding Retry proceeds.");

            foreach (var airplay in new[] { false, true })
            foreach (var phase in new[] { "binding-recovery", "recovery", "stop" })
            {
                Reset();
                var usbId = $"capture-teardown-{airplay}-{phase}";
                var device = Device(airplay ? "airplay://" + usbId : usbId);
                var profile = bindings.CreateProfileFromIdentity("Teardown phone", DeviceIdentityType.Wired, usbId, null).Profile!;
                if (airplay) bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, device.Udid, device.Name, null, true);
                vm.Devices.Add(device);
                Select(device);
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                control.Binding = resolver.ResolveControlBinding(device, ReverseControlMode.Usb);
                control.AppleUdid = usbId;
                control.WiredEnabled = control.WiredConnected = true;
                control.WiredTarget = device.Udid;
                control.ControlStatus.Begin(ControlStatusMode.Usb, device.Name);
                var bridge = new UsbTouchBridgeHost();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SetKeyboardField(bridge, "_started", 1);
                SetKeyboardField(KeyboardField(bridge, "_bridge"), "_readerTask", release.Task);
                control.WiredBridge = bridge;
                if (phase == "binding-recovery") bindings.Unbind(profile.Id, DeviceIdentityType.Wired);
                var lifetime = phase == "stop"
                    ? vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid)
                    : (Task)KeyboardCall(vm, "RecoverUsbControlAsync", control)!;
                Task teardown = Task.CompletedTask;
                Task duplicateTeardown = Task.CompletedTask;
                Task cancel = Task.CompletedTask;
                try
                {
                    BindingAssert(!lifetime.IsCompleted, "Bridge cleanup did not reach the controlled wait.");
                    // Exercise the exact boundary used before native capture
                    // teardown, including an AirPlay-owned bridge's physical UDID.
                    teardown = (Task)KeyboardCall(vm, "DisableWiredControlForCaptureTeardownAsync", usbId)!;
                    duplicateTeardown = (Task)KeyboardCall(vm, "DisableWiredControlForCaptureTeardownAsync", usbId)!;
                    cancel = vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid);
                    PumpBinding(TimeSpan.FromMilliseconds(30));
                    BindingAssert(!lifetime.IsCompleted && !teardown.IsCompleted && !duplicateTeardown.IsCompleted &&
                        !cancel.IsCompleted && control.Stopping && !control.Starting,
                        $"Capture teardown or duplicate stop returned before {phase} released its USB bridge (AirPlay={airplay}).");
                    AwaitBinding(Start(false));
                    AwaitBinding(Start(true));
                    BindingAssert(control.Stopping && !control.Starting && !teardown.IsCompleted,
                        "Retry or mode switch bypassed capture teardown's cleanup wait.");
                    NoResources(control);
                }
                finally
                {
                    release.TrySetResult();
                    AwaitBinding(Task.WhenAll(lifetime, teardown, duplicateTeardown, cancel));
                }
                BindingAssert(!control.Stopping && !control.Starting && control.WiredTarget is null,
                    "Joined cleanup deadlocked or left stale lifecycle flags/target.");
                NoResources(control);
                if (phase == "binding-recovery") Failed(control);
            }
            Console.WriteLine("PASS USB/AirPlay-owned bridge: capture teardown and duplicate stops wait for binding-failure cleanup, normal recovery and normal stop; retry stays blocked and cleanup completes.");

            foreach (var network in new[] { false, true })
            {
                Reset();
                var device = Device("joined-operation-" + network);
                bindings.CreateProfileFromIdentity("Joined phone", DeviceIdentityType.Wired, device.Udid, null);
                vm.Devices.Add(device);
                Select(device);
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                control.Binding = resolver.ResolveControlBinding(device, network ? ReverseControlMode.Wireless : ReverseControlMode.Usb);
                control.AppleUdid = device.Udid;
                control.Failed = true;
                control.RequestedWireless = !network;
                control.ControlStatus.Begin(network ? ControlStatusMode.Usb : ControlStatusMode.Wireless, device.Name);
                var binding = control.Binding;
                var snapshot = control.ControlStatus.Current;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = network ? control.WirelessOperation : control.WiredOperation;
                // Cover the SingleFlight admission boundary independently of busy
                // flags: a joining caller does not own any startup state.
                var active = operation.RunAsync(_ => release.Task);
                try
                {
                    var joined = Start(network);
                    BindingAssert(ReferenceEquals(joined, active) && !control.Starting && control.Failed &&
                        control.RequestedWireless == !network && control.AppleUdid == device.Udid &&
                        ReferenceEquals(control.Binding, binding) && ReferenceEquals(control.ControlStatus.Current, snapshot),
                        "A request joining an existing operation overwrote startup, target, mode or status state.");
                    NoResources(control);
                }
                finally
                {
                    release.TrySetResult();
                    AwaitBinding(active);
                }
                BindingAssert(!control.Starting, "A joined operation left Starting stuck after completion.");
            }
            Console.WriteLine("PASS USB/wireless requests joining SingleFlight leave startup, target, mode and status state unchanged.");

            Reset();
            var invalid = Device("removed");
            Select(invalid);
            BindingAssert(!wired.IsEnabled && !wireless.IsEnabled, "Removed/stale device enabled toolbar.");
            vm.Devices.Add(invalid);
            invalid.UpdateFrom(DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = invalid.Udid, State = ConnectionState.Disconnected }));
            Select(invalid);
            BindingAssert(!wired.IsEnabled && !wireless.IsEnabled, "Disconnected device enabled toolbar.");
            Select(null);
            BindingAssert(!wired.IsEnabled && !wireless.IsEnabled, "No selection enabled toolbar.");
            var media = DeviceViewModel.CreateMediaCast();
            vm.Devices.Add(media);
            Select(media);
            BindingAssert(!wired.IsEnabled && !wireless.IsEnabled, "Media-only source enabled reverse control.");

            Reset();
            var btDevice = Device("bluetooth-unbound");
            vm.Devices.Add(btDevice);
            Select(btDevice);
            var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
            using var handle = new NativeSessionHandle(123, ownsHandle: false);
            sessions.Set(new DeviceCaptureState { Udid = btDevice.Udid, Handle = handle });
            AwaitBinding(vm.StartBluetoothControlAsync());
            BindingAssert(vm.ControlStatus.Current is { Stage: ControlStage.Failed } &&
                vm.ControlStatus.Current.Error == LocalizationService.Get("ControlBluetoothBindingRequired") && !vm.IsBluetoothControlEnabled,
                "Bluetooth unbound failure behavior regressed.");
            var btProfile = bindings.CreateProfileFromIdentity("BT phone", DeviceIdentityType.Wired, btDevice.Udid, null).Profile!;
            bindings.Bind(btProfile.Id, DeviceIdentityType.Bluetooth, "hid-peer", "Peer", null, true);
            BindingAssert((string?)KeyboardCall(vm, "GetBluetoothControlBinding", btDevice.Udid) == "hid-peer" &&
                resolver.ResolveControlBinding(Device("another-phone"), ReverseControlMode.Bluetooth) is null,
                "Bluetooth target resolution lost the exact profile relationship.");
            sessions.Remove(btDevice.Udid);
            Console.WriteLine("PASS absent/stale/disconnected/media targets remain disabled; Bluetooth missing-binding failure and exact HID target resolution preserved.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(error);
            throw;
        }
        finally
        {
            Reset();
            CloseWorkspaceTestWindow(main);
            SynchronizationContext.SetSynchronizationContext(null);
            app.Shutdown();
        }
    }

    private static void BindingAssert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void PumpBinding(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void AwaitBinding(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
            PumpBinding(TimeSpan.FromMilliseconds(10));
        BindingAssert(task.IsCompleted, "Binding regression timed out.");
        task.GetAwaiter().GetResult();
        PumpBinding(TimeSpan.FromMilliseconds(30));
    }
}
