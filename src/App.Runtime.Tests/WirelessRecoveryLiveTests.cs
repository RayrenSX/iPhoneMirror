using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in: real discovery, saved binding, production recovery, actual HID ACK.
    // No capture configuration change, typing, touch or new binding is performed.
    private static int RunWirelessRecoveryLive(string? selectedUdid = null, string? socketFailure = null)
    {
        if (socketFailure is not null && (selectedUdid is null || socketFailure is not ("hid" or "transport")))
            throw new ArgumentException("A selected device and hid/transport mode are required.", nameof(socketFailure));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = selectedUdid is null ? main : null;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        if (selectedUdid is null) main.Show();
        else
        {
            // The selected phone was independently verified by USB. Avoid the
            // main window's background inventory poll when another phone has
            // been withdrawn from testing. This mode does not test discovery.
            var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            vm.Devices.Add((DeviceViewModel)constructor.Invoke([selectedUdid, "Selected test iPhone",
                "", "", "USB", "", ConnectionState.Ready]));
            MappingAssert(DeviceViewModel.UdidEquals(vm.ResolveAppleUdid(selectedUdid), selectedUdid),
                "Selected phone has no matching saved binding; refusing another target.");
            Console.WriteLine("Selected identity supplied from prior USB verification; background inventory disabled.");
        }
        bool Wait(Func<bool> done, int seconds)
        {
            var timer = Stopwatch.StartNew();
            while (!done() && timer.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return done();
        }
        void Complete(Task task, int seconds)
        {
            MappingAssert(Wait(() => task.IsCompleted, seconds), "Live operation timed out.");
            task.GetAwaiter().GetResult();
        }
        void VerifyInput(UsbTouchBridgeHost host)
        {
            var acknowledgements = 0;
            host.StatusChanged += (_, e) =>
            {
                if (e.Code == "input_verified") Interlocked.Increment(ref acknowledgements);
            };
            for (var count = 1; count <= 3; count++)
            {
                Complete(host.SendKeyboardAsync(Array.Empty<byte>(), CancellationToken.None, releaseAll: true), 10);
                MappingAssert(Wait(() => Volatile.Read(ref acknowledgements) >= count, 10), "Real HID acknowledgement missing.");
            }
        }
        const string delayVariable = "IPHONE_MIRROR_TEST_DISCONNECT_AFTER_SECONDS";
        const string modesVariable = "IPHONE_MIRROR_TEST_DISCONNECT_MODES";
        var originalDelay = Environment.GetEnvironmentVariable(delayVariable);
        var originalModes = Environment.GetEnvironmentVariable(modesVariable);
        try
        {
            MappingAssert(Wait(() => vm.Devices.Any(d => !d.IsWireless && !d.IsMediaCast && vm.ResolveAppleUdid(d.Udid) is not null), 30),
                "No discovered USB identity for wireless control test.");
            var devices = vm.Devices.Where(d => !d.IsWireless && !d.IsMediaCast && vm.ResolveAppleUdid(d.Udid) is not null).ToArray();
            MappingAssert(devices.Length == 1, "Requires one unambiguous bound phone.");
            var device = devices[0];
            vm.SelectedDevice = device;
            SetKeyboardField(vm, "_wirelessControlPrerequisiteAcknowledged", true);
            vm.GetControlStatus(ControlStatusMode.Wireless, device.Udid).StatusChanged += (_, s) =>
                Console.WriteLine($"WIRELESS STATE: {s.Stage}; {s.Description}; error={s.Error}");
            if (socketFailure is not null)
            {
                Environment.SetEnvironmentVariable(delayVariable, "5");
                Environment.SetEnvironmentVariable(modesVariable, socketFailure);
            }
            Complete(vm.StartWirelessControlAsync(device.Udid), 365);
            var control = (DeviceControlSession)KeyboardCall(vm, "FindControl", device.Udid)!;
            var first = control.WirelessBridge;
            MappingAssert(first is { IsReady: true } && control.WirelessConnected,
                $"Wireless startup failed: {control.ControlStatus.Current?.Error}; " +
                string.Join("; ", control.ControlStatus.Diagnostics.Where(d => d.Level == "Error").Select(d => d.TechnicalMessage)));
            var observedSocketFault = 0;
            if (socketFailure is not null)
                first!.StatusChanged += (_, e) =>
                {
                    if (e.Code != $"diagnostic_{socketFailure}_disconnect") return;
                    // The current child owns exactly one real socket close.
                    // A replacement child must inherit ordinary settings.
                    Environment.SetEnvironmentVariable(delayVariable, null);
                    Environment.SetEnvironmentVariable(modesVariable, null);
                    Interlocked.Increment(ref observedSocketFault);
                    Console.WriteLine($"FAULT: current child closing its real {socketFailure} socket.");
                };
            VerifyInput(first!);
            var direct = (DirectUsbInputBridge)KeyboardField(first!, "_bridge");
            var child = (Process)KeyboardField(direct, "_process");
            var timer = Stopwatch.StartNew();
            if (socketFailure is null)
            {
                Console.WriteLine("FAULT: terminating this test's wireless bridge child.");
                child.Kill();
            }
            else
            {
                MappingAssert(Wait(() => Volatile.Read(ref observedSocketFault) == 1, 45),
                    "The packaged child did not close the requested real socket.");
                timer.Restart();
            }
            MappingAssert(Wait(() => control.WirelessConnected && control.WirelessBridge is { IsReady: true } next && !ReferenceEquals(next, first), 180),
                "Production wireless recovery did not become ready.");
            VerifyInput(control.WirelessBridge!);
            Console.WriteLine($"WIRELESS RECOVERY PASS: seconds={timer.Elapsed.TotalSeconds:F2}, real HID ACKs=6.");
            var recovered = control.WirelessBridge!;
            var recoveredDirect = (DirectUsbInputBridge)KeyboardField(recovered, "_bridge");
            var recoveredChild = (Process)KeyboardField(recoveredDirect, "_process");
            recoveredChild.Kill();
            MappingAssert(Wait(() => !control.WirelessConnected, 10), "Recovery did not revoke input readiness.");
            timer.Restart();
            Complete(vm.CancelReverseControlAsync(ControlStatusMode.Wireless, device.Udid), 20);
            MappingAssert(!control.WirelessConnected && !control.WirelessEnabled && control.WirelessBridge is null,
                "Cancel retained wireless readiness or bridge ownership.");
            // Cover every scheduled retry delay (1+2+3 seconds), while pumping
            // the real dispatcher so a late retry cannot hide behind test exit.
            AdvanceDispatcher(TimeSpan.FromSeconds(7));
            MappingAssert(!control.WirelessConnected && !control.WirelessEnabled && control.WirelessBridge is null,
                "Wireless recovery resurrected after user cancellation.");
            Console.WriteLine($"WIRELESS CANCEL PASS: seconds={timer.Elapsed.TotalSeconds - 7:F2}, no delayed reconnection.");
            return 0;
        }
        finally
        {
            try
            {
                Complete(vm.ShutdownAsync(), 45);
                CloseWorkspaceTestWindow(main);
                app.Shutdown();
            }
            finally
            {
                Environment.SetEnvironmentVariable(delayVariable, originalDelay);
                Environment.SetEnvironmentVariable(modesVariable, originalModes);
            }
        }
    }
}
