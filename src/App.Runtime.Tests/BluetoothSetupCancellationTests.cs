using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunBluetoothSetupCancellationTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(owner, "_viewModel");
        vm.SetupActive = true;
        var acknowledged = app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion;
        try
        {
            // Run the real configuration entry point but cancel its prerequisite;
            // no Bluetooth adapter, phone input or user settings are touched.
            foreach (var close in new[] { "binder", "prompt", "cancel" })
            {
                app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = 0;
                var store = Path.Combine(Path.GetFullPath(output), $"bindings-{close}.json");
                var manager = new DeviceBindingManager(store);
                manager.CreateProfileFromIdentity("Offline iPhone", DeviceIdentityType.Wired, "setup-cancel-fixture", null);
                ObservableCollection<DeviceViewModel> devices =
                [DeviceViewModel.FromNative(new NativeDeviceInfo
                    { Udid = "setup-cancel-fixture", Name = "Offline iPhone", State = ConnectionState.Ready })];
                var binder = new DeviceBindingWindow(owner, devices, vm, manager: manager)
                {
                    ScanDevicesAsync = _ => Task.CompletedTask,
                    StartAirPlayReceiverAsync = _ => Task.FromResult(false),
                    ReadBluetoothClientsAsync = _ => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([])
                };
                try
                {
                    binder.Show(); ToolsPump();
                    if (close == "cancel")
                    {
                        vm.ControlStatus.Begin(ControlStatusMode.Bluetooth, "Earlier attempt");
                        ReverseControlStatusWindow.Show(owner, vm.ControlStatus);
                    }
                    typeof(DeviceBindingWindow).GetMethod("ConnectBluetoothClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(binder, [new Button(), new RoutedEventArgs()]);
                    ToolsPump(150);
                    var prompt = app.Windows.OfType<ReverseControlStatusWindow>().SingleOrDefault(w => w.IsVisible);
                    InteractionAssert(binder.IsWorking && vm.ControlStatus.Current?.Prompt is { IsBlocking: true } &&
                        prompt is not null && ReferenceEquals(prompt.Owner, binder),
                        "First-run Bluetooth confirmation is hidden or owned by the main window.");
                    if (close == "binder") ToolsCapture(prompt!, Path.Combine(output, "bluetooth-confirmation.png"));
                    if (close == "binder") binder.Close();
                    else if (close == "prompt") prompt!.Close();
                    else typeof(ReverseControlStatusWindow).GetMethod("OnCancelClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(prompt, [new Button(), new RoutedEventArgs()]);
                    ToolsComplete((Task)KeyboardField(binder, "_bluetoothTask")); ToolsPump();
                    InteractionAssert(!binder.IsWorking && vm.ControlStatus.Current is { Stage: ControlStage.Cancelled, Prompt: null } &&
                        !app.Windows.OfType<ReverseControlStatusWindow>().Any(w => ReferenceEquals(w.Owner, binder)),
                        $"Closing {close} left Bluetooth setup pending or retained its prompt.");
                    InteractionAssert(close != "binder" || !binder.IsVisible,
                        "The binder still vetoes close after cancellation finishes.");
                    InteractionAssert(app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion == 0,
                        "Cancelling the prerequisite persisted an acknowledgement.");
                    if (close == "prompt")
                    {
                        typeof(DeviceBindingWindow).GetMethod("ConnectBluetoothClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(binder, [new Button(), new RoutedEventArgs()]);
                        ToolsPump();
                        InteractionAssert(binder.IsWorking && app.Windows.OfType<ReverseControlStatusWindow>()
                            .Any(w => w.IsVisible && w.IsEnabled && ReferenceEquals(w.Owner, binder)),
                            "Cancelling a prompt prevents retry in the same binding window.");
                        vm.ControlStatus.ResolvePrompt(new(ControlPromptAction.Secondary));
                        ToolsComplete((Task)KeyboardField(binder, "_bluetoothTask"));
                    }
                }
                finally { binder.Close(); ToolsComplete(binder.CleanupTask); }
                Console.WriteLine($"PASS first-run Bluetooth: visible owned prerequisite, {close} cancellation, cleanup and retry.");
            }

            var service = new ControlStatusService();
            service.Begin(ControlStatusMode.Bluetooth, "prompt cancellation fixture");
            using var oldToken = new CancellationTokenSource();
            using var currentToken = new CancellationTokenSource();
            var first = service.RequestPromptAsync(new(ControlPromptType.UserActionRequired, "First", "First"), oldToken.Token);
            var second = service.RequestPromptAsync(new(ControlPromptType.UserActionRequired, "Second", "Second"), currentToken.Token);
            // Cancel before the first continuation can dispose its registration.
            oldToken.Cancel();
            InteractionAssert(!second.IsCompleted && service.Current?.Prompt?.Title == "Second",
                "An obsolete cancellation registration cancelled a newer prompt.");
            currentToken.Cancel(); ToolsComplete(first); ToolsComplete(second);
            InteractionAssert(second.Result.Action == ControlPromptAction.Cancel && service.Current?.Prompt is null,
                "The active prompt ignored its own cancellation token.");
            var cancelled = service.RequestPromptAsync(new(ControlPromptType.UserActionRequired, "Cancelled", "Cancelled"), currentToken.Token);
            ToolsComplete(cancelled);
            InteractionAssert(service.Current?.Prompt is null, "A pre-cancelled request displayed a prompt.");
            Console.WriteLine("PASS prompt lifetime: replacement isolation, active cancellation and pre-cancelled requests.");

            using var startup = new CancellationTokenSource();
            var hid = new BluetoothHidMouseService();
            var start = hid.StartAsync("offline-cancel", cancellationToken: startup.Token,
                startupProgress: _ => startup.Cancel());
            try { ToolsComplete(start); throw new InvalidOperationException("Bluetooth startup ignored cancellation."); }
            catch (OperationCanceledException) { }
            InteractionAssert(!hid.IsAdvertising && !hid.HasTransportFailure,
                "Cancelled startup retained advertising or reported a transport failure.");
            ToolsComplete(hid.DisposeAsync().AsTask());
            Console.WriteLine("PASS HID startup: cancellation before adapter access releases the service gate without advertising.");
            return 0;
        }
        finally
        {
            vm.SetupActive = false;
            app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = acknowledged;
            CloseWorkspaceTestWindow(owner); app.Shutdown();
        }
    }
}
