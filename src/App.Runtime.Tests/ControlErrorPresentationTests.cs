using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunControlErrorPresentationTests()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = null;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        try
        {
            var hintStatus = new ControlStatusService();
            var hintView = new ReverseControlStatusViewModel(hintStatus);
            try
            {
                foreach (var mode in Enum.GetValues<ControlStatusMode>())
                {
                    hintStatus.Begin(mode, "Hint test phone"); ToolsPump();
                    MappingAssert(hintView.BluetoothReleaseHintVisibility == Visibility.Collapsed,
                        "The release shortcut appeared before control was ready.");
                    hintStatus.Ready(mode, "Hint test phone"); ToolsPump();
                    MappingAssert(hintView.BluetoothReleaseHintVisibility ==
                        (mode == ControlStatusMode.Bluetooth ? Visibility.Visible : Visibility.Collapsed),
                        "The release hint did not follow the ready transport.");
                    hintStatus.Failed(mode, "Hint test phone", "Offline failure"); ToolsPump();
                    MappingAssert(hintView.BluetoothReleaseHintVisibility == Visibility.Collapsed,
                        "A failed connection retained its release shortcut.");
                }
                hintStatus.Begin(ControlStatusMode.Bluetooth, "Hint test phone");
                hintStatus.Report(ControlStatusMode.Bluetooth, ControlStage.WaitingForPhoneConnection,
                    "Hint test phone", "Waiting"); ToolsPump();
                MappingAssert(hintView.BluetoothReleaseHintVisibility == Visibility.Collapsed,
                    "Waiting for a Bluetooth connection showed a release shortcut.");
                hintStatus.Cancelled(ControlStatusMode.Bluetooth, "Hint test phone", "Cancelled"); ToolsPump();
                MappingAssert(hintView.BluetoothReleaseHintVisibility == Visibility.Collapsed,
                    "Cancelled Bluetooth control showed a release shortcut.");
            }
            finally { hintView.Dispose(); }
            Console.WriteLine("Release shortcut appears only for ready Bluetooth control; hidden while connecting, failed or cancelled.");
            vm.ControlStatus.Begin(ControlStatusMode.Wireless, "Test phone");
            KeyboardCall(vm, "ShowReverseControlError", "wireless", "Original wireless failure",
                "ReverseControlStartErrorTitle", "wireless_remote_pairing_failed: original diagnostic", null, null);
            MappingAssert(vm.ControlStatus.Current is { Stage: ControlStage.Failed, Error: "Original wireless failure" },
                "Missing window lost the original control failure.");
            MappingAssert(vm.ControlStatus.Diagnostics.Any(d => d.TechnicalMessage.Contains("wireless_remote_pairing_failed")),
                "Missing window discarded the diagnostic.");
            MappingAssert((int)KeyboardField(vm, "_reverseControlErrorPromptInFlight") == 0,
                "Missing window left the error prompt guard set.");
            Console.WriteLine("Control failure without a main window retains status/diagnostics and releases the prompt guard.");

            // Reproduce a control start after the configuration workflow was
            // left waiting for the phone. Reject before touching real hardware.
            vm.ControlStatus.Begin(ControlStatusMode.Bluetooth, "Previous phone");
            vm.ControlStatus.Report(ControlStatusMode.Bluetooth, ControlStage.WaitingForPhoneConnection,
                "Previous phone", "Waiting for the previous connection");
            vm.ControlStatus.AddDiagnostic("Previous attempt", "Old connection diagnostic");
            AwaitMapping(vm.EnableBluetoothControlAsync("unbound-bluetooth-test", fromReverseControl: true));
            MappingAssert(vm.ControlStatus.Current is { Mode: ControlStatusMode.Bluetooth,
                Stage: ControlStage.Failed, IsTerminal: true }, "Unbound Bluetooth start did not fail.");
            MappingAssert(vm.ControlStatus.GetProgress(ControlStage.CheckingBinding) == ControlStageProgress.Failed,
                "Binding failure did not mark the current binding check failed.");
            foreach (var stage in ControlStageWorkflow.GetStages(ControlStatusMode.Bluetooth)
                         .Where(stage => stage != ControlStage.CheckingBinding))
                MappingAssert(vm.ControlStatus.GetProgress(stage) == ControlStageProgress.Pending,
                    $"New Bluetooth start retained old progress for {stage}.");
            MappingAssert(!vm.ControlStatus.Diagnostics.Any(d => d.TechnicalMessage == "Old connection diagnostic"),
                "New Bluetooth start retained diagnostics from the configuration attempt.");
            var statusViewModel = new ReverseControlStatusViewModel(vm.ControlStatus);
            try
            {
                MappingAssert(statusViewModel.StageDescription.Contains(LocalizationService.Get("ControlBluetoothBindingRequired")) &&
                    statusViewModel.StageDescription.Contains(LocalizationService.Get("ControlBluetoothBindingAdvice")),
                    "Bluetooth binding instructions are hidden from the error description.");
            }
            finally { statusViewModel.Dispose(); }
            MappingAssert(!vm.IsBluetoothReverseControlEnabled && !vm.IsBluetoothPeripheralAdvertising,
                "Rejected binding unexpectedly enabled Bluetooth control.");
            Console.WriteLine("Bluetooth start clears configuration progress and shows binding instructions in the error description.");
            var waitingControl = new DeviceControlSession("developer-mode-test");
            waitingControl.ControlStatus.Begin(ControlStatusMode.Usb, "Test phone");
            waitingControl.ControlStatus.Report(ControlStatusMode.Usb, ControlStage.CheckingPermissions,
                "Test phone", "Checking");
            MainViewModel.SetDeveloperModeWaiting(waitingControl, "Test phone");
            MappingAssert(!waitingControl.Failed && !waitingControl.WiredEnabled && !waitingControl.WiredConnected,
                "Developer Mode waiting enabled input or marked control failed.");
            MappingAssert(waitingControl.ControlStatus.Current is {
                Stage: ControlStage.WaitingForDeveloperMode, IsTerminal: true, Error: null, Prompt: null },
                "Developer Mode waiting did not end this startup attempt cleanly.");
            MappingAssert(waitingControl.ControlStatus.Diagnostics.All(d => d.Level != "Error"),
                "Developer Mode waiting logged an error.");
            var waitingView = new ReverseControlStatusViewModel(waitingControl.ControlStatus) { RetryRequested = () => { } };
            try
            {
                MappingAssert(waitingView.StageTitle == LocalizationService.Get("ControlStageWaitingForDeveloperMode") &&
                    waitingView.StageDescription == LocalizationService.Get("UsbControlDeveloperModeRevealed") &&
                    waitingView.RetryVisibility == Visibility.Visible && waitingView.CloseVisibility == Visibility.Visible &&
                    !waitingView.CanBeginControlAfterClose && waitingView.CountdownText.Length == 0,
                    "Developer Mode waiting lost its instructions/retry or started the ready countdown.");
                waitingControl.ControlStatus.Report(ControlStatusMode.Usb, ControlStage.PreparingDeviceSupport,
                    "Test phone", "Late bridge event");
                MappingAssert(waitingControl.ControlStatus.Current?.Stage == ControlStage.WaitingForDeveloperMode,
                    "Late bridge event overwrote the waiting state.");
                waitingControl.ControlStatus.Begin(ControlStatusMode.Usb, "Test phone");
                MappingAssert(waitingControl.ControlStatus.Current is {
                    Stage: ControlStage.CheckingBinding, IsTerminal: false, Error: null },
                    "Retry did not start a fresh attempt after waiting.");
            }
            finally { waitingView.Dispose(); }
            Console.WriteLine("Developer Mode waiting retains retry without failure, input or countdown.");
            return 0;
        }
        finally
        {
            AwaitMapping(vm.ShutdownAsync());
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }
}
