using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void RunFirstRunSetupUsabilityTests(MainViewModel vm, string output,
        Action<int> pump, Action<Task> complete, Action<bool, string> check)
    {
        var store = new FirstRunSetupStore(Path.Combine(output, "usability-checkpoint.json"));
        store.Save(new() { Disposition = SetupDisposition.InProgress, Step = SetupStep.WirelessBackend });
        var window = new FirstRunSetupWindow(vm, store, checkOnOpen: false);
        object? Call(string method, params object[] args) => typeof(FirstRunSetupWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);
        Button Button(string name) => (Button)window.FindName(name);
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Button ActionButton(string key) => ComponentVisuals<Button>(window).Single(b => Equals(b.Content, LocalizationService.Get(key)));
        void Render(SetupStep step) { window.State.Step = step; Call("Render"); window.UpdateLayout(); }
        try
        {
            var installs = 0;
            var installed = false;
            window.ReceiverComponentAvailable = () => installed;
            window.InstallReceiverComponent = (_, _, _) => { installs++; installed = true; return Task.CompletedTask; };
            vm.SelectedWirelessReceiverBackend = vm.WirelessReceiverBackends.First(v => v.Backend == WirelessReceiverBackend.Original);
            window.Show(); window.UpdateLayout();
            ComponentVisuals<RadioButton>(window).Single(c => c.Content is StackPanel p &&
                p.Children.OfType<TextBlock>().Any(t => t.Text == vm.WirelessReceiverBackends.First(v => v.Backend == WirelessReceiverBackend.UxPlay).Label)).IsChecked = true;
            pump(50);
            check(installs == 0 && Button("Next").IsEnabled, "Selecting a backend downloaded components before Next.");
            check(store.Load().WirelessBackendDraft == WirelessReceiverBackend.UxPlay, "Backend choice was not checkpointed.");
            complete((Task)Call("AdvanceAsync")!);
            check(installs == 1 && window.State.Step == SetupStep.Wireless, "Next did not install the selected backend before continuing.");

            var bindingsPath = Path.Combine(output, "usability-bindings-" + Guid.NewGuid().ToString("N") + ".json");
            var bindings = new DeviceBindingManager(bindingsPath);
            var created = bindings.CreateProfileFromIdentity("Saved iPhone", DeviceIdentityType.Wired, "wired-test", null);
            check(created.Success, "Could not create isolated binding fixture.");
            check(bindings.Bind(created.Profile!.Id, DeviceIdentityType.AirPlay, "wireless-test", "Saved iPhone", null, true).Success &&
                bindings.Bind(created.Profile.Id, DeviceIdentityType.Bluetooth, "bluetooth-test", "Saved iPhone", null, true).Success,
                "Could not save isolated wireless/Bluetooth fixtures.");
            window.ReadBindings = () => new DeviceBindingManager(bindingsPath);
            window.State.Usage = SetupUsage.MirrorAndControl;
            window.State.Devices = [new() { Id = "wired-test", Name = "Saved iPhone", Outcomes = new()
            {
                [SetupStep.Environment] = SetupOutcome.Verified, [SetupStep.ReDetection] = SetupOutcome.Verified,
                [SetupStep.Profile] = SetupOutcome.Verified, [SetupStep.Wireless] = SetupOutcome.Verified,
                [SetupStep.Bluetooth] = SetupOutcome.Verified
            } }];
            Render(SetupStep.WirelessBackend);
            check(((TextBlock)window.FindName("DeviceLabel")).Text.Contains("Saved iPhone"), "Backend page lost the current device context.");
            foreach (var step in new[] { SetupStep.Wireless, SetupStep.Bluetooth })
            {
                Render(step);
                check(Button("Next").IsEnabled && ActionButton("SetupConfigureAgain").IsVisible,
                    "Returning to a saved binding forced configuration again: " + step);
            }
            complete((Task)Call("AdvanceAsync")!);
            check(window.State.Step == SetupStep.DeviceComplete, "Saved Bluetooth binding could not continue without pairing again.");
            window.State.Usage = SetupUsage.WirelessOnly;
            window.State.Devices = [new() { Id = "wireless-test", Name = "Saved iPhone" }];
            window.State.GlobalOutcomes[SetupStep.Wireless] = SetupOutcome.Verified;
            Render(SetupStep.Wireless);
            check(Button("Next").IsEnabled, "Wireless-only saved binding could not be reused.");
            bindings.DeleteProfile(created.Profile.Id);
            Render(SetupStep.Wireless);
            check(!Button("Next").IsEnabled, "Checkpoint alone accepted a deleted binding.");
            window.State.Usage = SetupUsage.MirrorAndControl;
            window.State.Devices = [new() { Id = "wired-test", Outcomes = new() { [SetupStep.Bluetooth] = SetupOutcome.Verified } }];
            Render(SetupStep.Bluetooth);
            check(!Button("Next").IsEnabled, "Deleted wired profile still accepted its Bluetooth checkpoint.");

            Render(SetupStep.Environment);
            check(!ActionButton("SetupInstall").IsVisible && ActionButton("SetupRetry").IsVisible, "Environment page offered installation before checking.");
            window.PrepareDevice = (_, _, _, _) => Task.FromResult(new SetupDriverProgress("Result", false, null));
            complete((Task)Call("RunAsync", false)!); window.UpdateLayout();
            check(ActionButton("SetupInstall").IsVisible && !Button("Next").IsEnabled, "Missing environment did not offer installation.");
            var prepares = 0;
            window.PrepareDevice = (_, install, _, _) => { check(!install, "Retry unexpectedly installed drivers."); prepares++; return Task.FromResult(new SetupDriverProgress("Result", true, null)); };
            ActionButton("SetupRetry").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pump(50); window.UpdateLayout();
            check(prepares == 1 && !ActionButton("SetupInstall").IsVisible && Button("Next").IsEnabled, "Healthy environment still offered unnecessary installation.");

            Render(SetupStep.Connection);
            window.DiscoverDevices = _ => Task.FromResult(new SetupDriverProgress("Result", true, null, [new("connected", "iPhone")]));
            Call("StartDiscovery"); pump(50);
            check(Button("Next").IsEnabled, "Fresh connected device did not enable Next.");
            Click("CancelOperation"); pump(50);
            check(!Button("Next").IsEnabled && ActionButton("SetupScanAgain").IsEnabled, "Cancelled scan allowed stale inventory to advance.");

            var pendingScan = new TaskCompletionSource<SetupDriverProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.DiscoverDevices = _ => pendingScan.Task;
            Call("StartDiscovery");
            var advancing = (Task)Call("AdvanceAsync")!;
            Click("CancelOperation");
            pendingScan.SetResult(new("Result", true, null, [new("late", "Late iPhone")]));
            complete(advancing);
            check(window.State.Step == SetupStep.Connection && !Button("Next").IsEnabled && window.State.Devices.All(d => d.Id != "late"),
                "Late successful scan undid the user's cancellation.");

            Render(SetupStep.Welcome);
            var disposition = window.State.Disposition;
            using (File.Open(store.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click("Skip");
                check(window.IsVisible && window.State.Disposition == disposition && ((TextBlock)window.FindName("Status")).Visibility == Visibility.Visible,
                    "Failed defer hid the save error or changed the disposition.");
            }

            Render(SetupStep.Connection);
            var closingScan = new TaskCompletionSource<SetupDriverProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.DiscoverDevices = _ => closingScan.Task;
            Call("StartDiscovery");
            var discovery = (Task)typeof(FirstRunSetupWindow).GetField("_discoveryTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            check(vm.SetupActive, "Closing released setup ownership before device scan cleanup.");
            closingScan.SetResult(new("Result", true, null, [])); complete(discovery);
            check(!vm.SetupActive, "Scan cleanup did not release setup ownership.");
            Console.WriteLine("PASS explicit component download, saved-binding reuse/deletion, device context, driver retry/install gating, stale scan cancellation, defer rollback and scan-close ownership.");
        }
        finally
        {
            typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }
    }
}
