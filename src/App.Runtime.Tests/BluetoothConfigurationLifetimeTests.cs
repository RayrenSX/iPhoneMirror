using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunBluetoothConfigurationLifetimeTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(owner, "_viewModel");
        var acknowledgement = app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion;
        DeviceBindingManager Manager(string name) => new(Path.Combine(Path.GetFullPath(output), name + ".json"));
        DeviceBindingWindow Binder(DeviceBindingManager manager) => new(owner, [], vm, manager: manager)
        {
            ScanDevicesAsync = _ => Task.CompletedTask,
            StartAirPlayReceiverAsync = _ => Task.FromResult(false),
            ReadBluetoothClientsAsync = _ => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([])
        };
        void Connect(DeviceBindingWindow binder) => typeof(DeviceBindingWindow)
            .GetMethod("ConnectBluetoothClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(binder, [new Button(), new RoutedEventArgs()]);
        try
        {
            vm.SetupActive = true;
            var pending = new TaskCompletionSource<IReadOnlyList<BluetoothClientInfo>>();
            CancellationToken readToken = default;
            var closing = Binder(Manager("discovery-close"));
            closing.ReadBluetoothClientsAsync = token => { readToken = token; return pending.Task; };
            try
            {
                closing.Show(); ToolsPump();
                InteractionAssert(!pending.Task.IsCompleted && readToken.CanBeCanceled, "Discovery did not receive a lifetime token.");
                closing.Close(); ToolsComplete(closing.CleanupTask);
                InteractionAssert(!closing.IsVisible && readToken.IsCancellationRequested && !pending.Task.IsCompleted,
                    "Closing still waits for a native query that ignores cancellation.");
                var lateChanges = 0;
                closing.PropertyChanged += (_, _) => lateChanges++;
                pending.SetException(new InvalidOperationException("late discovery fault")); ToolsPump();
                InteractionAssert(lateChanges == 0, "A late failed discovery updated the closed binder.");
            }
            finally { closing.Close(); }
            Console.WriteLine("PASS discovery close: lifetime cancellation, bounded cleanup and late-fault UI isolation.");

            var slow = new TaskCompletionSource<IReadOnlyList<BluetoothClientInfo>>();
            var calls = 0;
            var timeout = Binder(Manager("discovery-timeout"));
            timeout.BluetoothDiscoveryTimeout = TimeSpan.FromMilliseconds(100);
            timeout.ReadBluetoothClientsAsync = _ => ++calls == 1 ? slow.Task :
                Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([new("fresh", "Fresh client", "", DateTimeOffset.UtcNow)]);
            try
            {
                timeout.Show(); ToolsPump(160);
                InteractionAssert(calls == 1 && !string.IsNullOrWhiteSpace(timeout.ActivityText), "Timed-out discovery left no usable feedback.");
                var secondWait = (Task)KeyboardCall(timeout, "ReadBluetoothClientsForWindowAsync")!;
                try { ToolsComplete(secondWait); throw new InvalidOperationException("Unresponsive discovery did not time out."); }
                catch (TimeoutException) { }
                InteractionAssert(calls == 1, "Timeout started duplicate native queries while the first remained pending.");
                slow.SetResult([new("stale", "Stale client", "", DateTimeOffset.UtcNow)]); ToolsPump();
                var fresh = (Task)KeyboardCall(timeout, "RefreshBluetoothClientsAsync")!; ToolsComplete(fresh);
                InteractionAssert(calls == 2 && timeout.BluetoothClients.Single().Id == "fresh",
                    "Recovery applied an obsolete result or prevented a fresh query.");
            }
            finally { timeout.Close(); ToolsComplete(timeout.CleanupTask); }
            Console.WriteLine("PASS discovery timeout: feedback, single in-flight query, stale-result rejection and fresh retry.");

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var hid = new BluetoothHidMouseService();
            try
            {
                try { ToolsComplete(hid.GetSubscribedClientInfosAsync(cancelled.Token)); throw new InvalidOperationException("Pre-cancelled discovery accessed the adapter."); }
                catch (OperationCanceledException) { }
            }
            finally { ToolsComplete(hid.DisposeAsync().AsTask()); }

            vm.ControlStatus.Begin(ControlStatusMode.Bluetooth, "Retained session");
            vm.ControlStatus.Ready(ControlStatusMode.Bluetooth, "Retained session");
            SetKeyboardField(vm, "_bluetoothControlEnabled", true);
            var busy = Binder(Manager("busy-session"));
            try
            {
                var existingStatus = vm.ControlStatus.Current;
                busy.Show(); ToolsPump();
                var started = vm.StartBluetoothPeripheralForConfigurationAsync("busy-fixture", busy, CancellationToken.None);
                ToolsComplete(started); ToolsComplete(vm.StopBluetoothPeripheralConfigurationAsync());
                InteractionAssert(!started.Result && vm.IsBluetoothReverseControlEnabled &&
                    ReferenceEquals(existingStatus, vm.ControlStatus.Current),
                    "Rejected configuration stopped or replaced an existing control session.");
            }
            finally { SetKeyboardField(vm, "_bluetoothControlEnabled", false); busy.Close(); ToolsComplete(busy.CleanupTask); }
            Console.WriteLine("PASS configuration ownership: rejected startup preserves an existing Bluetooth session and status.");

            const string failure = "OFFLINE_FAILURE_BEFORE_ADAPTER_ACCESS";
            EventHandler<ControlStatusSnapshot> fail = (_, snapshot) =>
            {
                if (snapshot.Stage == ControlStage.CheckingBluetooth) throw new InvalidOperationException(failure);
            };
            vm.ControlStatus.StatusChanged += fail;
            try
            {
                foreach (var firstRun in new[] { true, false })
                foreach (var acknowledged in new[] { true, false })
                {
                    vm.SetupActive = firstRun;
                    app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = acknowledged
                        ? vm.BluetoothWindowMouseEnabled ? BluetoothHidProtocol.AbsoluteMouseReportMapVersion : BluetoothHidProtocol.ReportMapVersion
                        : 0;
                    var manager = Manager($"failure-{firstRun}-{acknowledged}");
                    manager.CreateProfileFromIdentity("Offline iPhone", DeviceIdentityType.Wired, "failure-fixture", null);
                    var binder = Binder(manager);
                    try
                    {
                        binder.Show(); ToolsPump();
                        vm.ControlStatus.Begin(ControlStatusMode.Bluetooth, "Previous attempt");
                        ReverseControlStatusWindow.Show(owner, vm.ControlStatus, retry: () => throw new InvalidOperationException("Stale retry"),
                            countdownElapsed: () => throw new InvalidOperationException("Stale input handoff"));
                        Connect(binder); ToolsPump();
                        if (!acknowledged) vm.ControlStatus.ResolvePrompt(new(ControlPromptAction.Primary));
                        ToolsComplete((Task)KeyboardField(binder, "_bluetoothTask")); ToolsPump();
                        var error = app.Windows.OfType<ReverseControlStatusWindow>().Single(w => w.IsVisible);
                        var errorVm = (ReverseControlStatusViewModel)error.DataContext;
                        InteractionAssert(!binder.IsWorking && vm.ControlStatus.Current?.Stage == ControlStage.Failed &&
                            ReferenceEquals(error.Owner, binder) && errorVm.Diagnostics.Any(text => text.Contains(failure)),
                            "Configuration failure hid its details, kept working, or moved behind its owner.");
                        InteractionAssert(errorVm.RetryRequested is null && KeyboardField(error, "_countdownElapsed") is null,
                            "Reparented failure UI retained a previous route's retry or input callback.");
                        InteractionAssert(errorVm.BluetoothReleaseHintVisibility == Visibility.Collapsed,
                            "Failed binding configuration showed the Bluetooth release shortcut.");
                        if (firstRun && !acknowledged) ToolsCapture(error, Path.Combine(output, "bluetooth-start-failure.png"));
                        // Retrying a failed attempt keeps one owned error surface.
                        Connect(binder); ToolsComplete((Task)KeyboardField(binder, "_bluetoothTask")); ToolsPump();
                        InteractionAssert(app.Windows.OfType<ReverseControlStatusWindow>().Count(w => w.IsVisible) == 1,
                            "Retry duplicated the configuration status window.");
                    }
                    finally { binder.Close(); ToolsComplete(binder.CleanupTask); }
                    InteractionAssert(!app.Windows.OfType<ReverseControlStatusWindow>().Any(w => w.IsVisible),
                        "Closing the binder retained its failure window.");
                    Console.WriteLine($"PASS configuration failure: firstRun={firstRun}, acknowledged={acknowledged}, visible owned details, retry and callback cleanup.");
                }
            }
            finally { vm.ControlStatus.StatusChanged -= fail; }
            return 0;
        }
        finally
        {
            vm.SetupActive = false;
            app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = acknowledgement;
            CloseWorkspaceTestWindow(owner); app.Shutdown();
        }
    }
}
