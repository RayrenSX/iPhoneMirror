using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private static int RunWiredFinalRetryTests(string output)
    {
        var budget = new UsbControlRecoveryBudget();
        MappingAssert(budget.TryTakeFinalRetry() && !budget.TryTakeFinalRetry(), "Final restart was unbounded.");
        budget.Ready(0);
        budget.Ready(Stopwatch.Frequency * 20);
        budget.Disconnected(Stopwatch.Frequency * 29);
        MappingAssert(!budget.TryTakeFinalRetry(), "Brief readiness rearmed a restart loop.");
        budget.Ready(Stopwatch.Frequency * 40);
        budget.Disconnected(Stopwatch.Frequency * 70);
        MappingAssert(budget.TryTakeFinalRetry(), "Stable readiness did not rearm final recovery.");
        budget.Reset();
        MappingAssert(budget.TryTakeFinalRetry(), "Manual Retry did not reset its budget.");

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), main,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var bindings = new DeviceBindingManager(Path.Combine(Path.GetFullPath(output), $"bindings-{Guid.NewGuid():N}.json"));
        var resolver = new DeviceIdentityResolver(bindings);
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_identityResolver", resolver);
        var controls = (Dictionary<string, DeviceControlSession>)KeyboardField(vm, "_deviceControls");
        var gate = (SemaphoreSlim)KeyboardField(vm, "_lockdownHandshakeGate");
        int Windows() => app.Windows.OfType<ReverseControlStatusWindow>().Count(w => w.IsVisible);
        try
        {
            foreach (var scenario in new[] { "cancel", "cleanup-failure", "rebind", "budget" })
            {
                foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
                controls.Clear();
                vm.Devices.Clear();
                var device = DeviceViewModel.FromNative(new NativeDeviceInfo
                { Udid = "final-retry-" + scenario, Name = "Final retry fixture", State = ConnectionState.Ready });
                var profile = bindings.CreateProfileFromIdentity(device.Name, DeviceIdentityType.Wired, device.Udid, null).Profile!;
                vm.Devices.Add(device);
                vm.SelectedDevice = device;
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                control.Binding = resolver.ResolveControlBinding(device, ReverseControlMode.Usb);
                control.AppleUdid = device.Udid;
                control.WiredEnabled = control.WiredConnected = true;
                control.WiredTarget = device.Udid;
                control.Router.Begin(device.Udid, ReverseControlMode.Usb);
                control.ControlStatus.Begin(ControlStatusMode.Usb, device.Name);
                control.ControlStatus.Ready(ControlStatusMode.Usb, device.Name);
                if (scenario == "budget") MappingAssert(control.WiredRecovery.TryTakeFinalRetry(), "Fixture budget setup failed.");
                var originalResume = control.WiredMuxResume;
                var old = new UsbTouchBridgeHost();
                control.WiredBridge = old;
                SetKeyboardField(old, "_started", 1);
                var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SetKeyboardField(old, "_stopTask", cleanup.Task);
                KeyboardCall(vm, "AttachUsbBridgeEvents", control, old, device, CancellationToken.None);
                gate.Wait();
                try
                {
                    KeyboardCall(old, "OnBridgeEvent", new BridgeEvent("error", "direct_hid_recovery_exhausted", "fixture exhausted"));
                    PumpBinding(TimeSpan.FromMilliseconds(40));
                    var operation = control.WiredOperation.RunAsync(_ => Task.CompletedTask);
                    MappingAssert(control.Starting && control.WiredFinalRetryInProgress && !control.InputEnabled &&
                        control.Router.Mode == ReverseControlMode.None && !operation.IsCompleted && Windows() == 0,
                        "Exhaustion did not silently revoke input and join old cleanup.");
                    KeyboardCall(old, "OnBridgeEvent", new BridgeEvent("error", "direct_hid_recovery_exhausted", "duplicate"));
                    KeyboardCall(old, "OnBridgeEvent", new BridgeEvent("status", "terminated", null));
                    PumpBinding(TimeSpan.FromMilliseconds(40));
                    MappingAssert(ReferenceEquals(control.WiredBridge, old) && Windows() == 0,
                        "Repeated terminal events raced a replacement or displayed failure early.");
                    DeviceControlSession? peer = null;
                    if (scenario == "cancel")
                    {
                        KeyboardCall(vm, "ShowDeviceControlError", control, "Wired", "Specific recovery advice",
                            "ReverseControlRecoveryErrorTitle", "Fixture technical detail", null);
                        PumpBinding(TimeSpan.FromMilliseconds(20));
                        MappingAssert(Windows() == 0 && control.ControlStatus.Current?.Error == "Specific recovery advice",
                            "Deferred startup failure lost its actionable guidance or displayed a window early.");
                        var peerDevice = DeviceViewModel.FromNative(new NativeDeviceInfo
                        { Udid = "final-retry-peer", Name = "Other device", State = ConnectionState.Ready });
                        vm.Devices.Add(peerDevice);
                        peer = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", peerDevice.Udid)!;
                        peer.WiredEnabled = peer.WiredConnected = true;
                        peer.Router.Begin(peerDevice.Udid, ReverseControlMode.Usb);
                        SetKeyboardField(vm, "_selectedDevice", peerDevice);
                        AwaitBinding(vm.StartUsbControlAsync(device.Udid));
                        AwaitBinding(vm.StartWirelessControlAsync(device.Udid));
                        MappingAssert(control.Starting && !control.RequestedWireless &&
                            ReferenceEquals(control.WiredBridge, old), "A competing start changed the final Retry owner.");
                    }
                    if (scenario == "rebind")
                        bindings.Bind(profile.Id, DeviceIdentityType.Wired, "different-phone", "Other phone", null, true);
                    if (scenario == "cleanup-failure") cleanup.TrySetException(new IOException("fixture cleanup failed"));
                    else cleanup.TrySetResult();
                    PumpBinding(TimeSpan.FromMilliseconds(80));
                    if (scenario == "cancel")
                    {
                        MappingAssert(control.WiredBridge is { } replacement && !ReferenceEquals(replacement, old) &&
                            control.Starting && Windows() == 0 && control.Binding?.TargetStableId == device.Udid &&
                            ReferenceEquals(originalResume, control.WiredMuxResume),
                            "Final recovery did not reuse manual startup for the same device and mux lifetime.");
                        AwaitBinding(vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid));
                        AwaitBinding(operation);
                        MappingAssert(!control.Enabled && !control.Starting && !control.WiredFinalRetryInProgress &&
                            control.WiredBridge is null && Windows() == 0,
                            "Cancel left a replacement, popup, or running retry behind.");
                        MappingAssert(peer is { InputEnabled: true } && peer.Router.Mode == ReverseControlMode.Usb,
                            "Recovery or cancellation changed the other selected device's input route.");
                    }
                    else
                    {
                        AwaitBinding(operation);
                        PumpBinding(TimeSpan.FromMilliseconds(40));
                        MappingAssert(control.Failed && !control.Enabled && !control.Starting && Windows() == 1 &&
                            control.ControlStatus.Current?.Stage == ControlStage.Failed,
                            "Final failure did not display exactly one actionable window.");
                        if (scenario == "cleanup-failure")
                        {
                            MappingAssert(ReferenceEquals(control.WiredBridge, old), "Failed cleanup lost bridge ownership.");
                            var retryCalls = 0;
                            KeyboardCall(vm, "ShowDeviceControlError", control, "Wired", "Retry cleanup",
                                "ReverseControlStopErrorTitle", null,
                                new Func<Task>(() => { retryCalls++; return Task.CompletedTask; }));
                            PumpBinding(TimeSpan.FromMilliseconds(40));
                            var failureWindow = app.Windows.OfType<ReverseControlStatusWindow>().Single(w => w.IsVisible);
                            ((ReverseControlStatusViewModel)failureWindow.DataContext).RetryRequested!();
                            MappingAssert(retryCalls == 1,
                                "Queued failure notification replaced the error-specific Retry action.");
                            KeyboardCall(vm, "ShowDeviceControlError", control, "Wired", "Close this failure",
                                "ReverseControlStopErrorTitle", null, null);
                            failureWindow.Close();
                            PumpBinding(TimeSpan.FromMilliseconds(40));
                            MappingAssert(Windows() == 0,
                                "An older failure notification reopened the dismissed error window.");
                        }
                        if (scenario == "rebind")
                            MappingAssert(control.AppleUdid == device.Udid && control.WiredBridge is null &&
                                control.ControlStatus.Current!.Error!.Contains(LocalizationService.Get("ControlBindingRequired")),
                                "Automatic Retry silently adopted a changed binding.");
                        if (scenario == "budget")
                            MappingAssert(control.WiredBridge is null, "Exhausted budget left the failed owner active.");
                    }
                }
                finally
                {
                    cleanup.TrySetResult();
                    gate.Release();
                    SetKeyboardField(old, "_stopTask", Task.CompletedTask);
                    SetKeyboardField(old, "_started", 0);
                    AwaitBinding(vm.CancelReverseControlAsync(ControlStatusMode.Usb, device.Udid));
                }
                Console.WriteLine("PASS final wired Retry: " + scenario);
            }
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            var lastControl = controls.Values.Single();
            lastControl.ControlStatus.Begin(ControlStatusMode.Usb, "Notification fixture");
            Task.Run(() => lastControl.ControlStatus.Failed(ControlStatusMode.Usb,
                "Notification fixture", "Outdated background failure")).GetAwaiter().GetResult();
            lastControl.ControlStatus.Ready(ControlStatusMode.Usb, "Notification fixture");
            PumpBinding(TimeSpan.FromMilliseconds(40));
            MappingAssert(Windows() == 0, "An obsolete background failure opened a window after recovery.");
            Task.Run(() => lastControl.ControlStatus.Failed(ControlStatusMode.Usb,
                "Notification fixture", "Current background failure")).GetAwaiter().GetResult();
            PumpBinding(TimeSpan.FromMilliseconds(40));
            MappingAssert(Windows() == 1, "A current background failure lost its recovery window.");
            Console.WriteLine("PASS wired failure notifications: specific Retry, dismissal, stale/current background failure");
            return 0;
        }
        finally
        {
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            main.Close();
            app.Shutdown();
        }
    }
}
