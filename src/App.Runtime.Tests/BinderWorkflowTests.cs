using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void RunBinderWorkflowTests(Window owner, MainViewModel vm, string output, Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetFullPath(output), "binder-workflow-" + Guid.NewGuid().ToString("N") + ".json");
        var manager = new DeviceBindingManager(path);
        DeviceViewModel Device(string id, string name, ConnectionState state = ConnectionState.Ready) =>
            DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = id, Name = name, State = state, ProductType = "iPhone18,1" });
        var first = manager.CreateProfileFromIdentity("A", DeviceIdentityType.Wired, "wired-a", null).Profile!;
        var second = manager.CreateProfileFromIdentity("B", DeviceIdentityType.Wired, "wired-b", null).Profile!;
        ObservableCollection<DeviceViewModel> devices = [Device("wired-a", "A"), Device("wired-b", "B")];
        var receiverReady = new TaskCompletionSource<bool>();
        var receiverStarts = 0;
        var binder = new DeviceBindingWindow(owner, devices, vm, manager: manager)
        {
            ReadBluetoothClientsAsync = _ => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([]),
            ScanDevicesAsync = _ => Task.CompletedTask,
            StartAirPlayReceiverAsync = _ => { receiverStarts++; return receiverReady.Task; }
        };
        object? Call(string name, params object?[] args) => typeof(DeviceBindingWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(binder, args);
        ProfileListItem Item(Guid id) => binder.Profiles.Single(p => p.Id == id);
        void Select(Guid id) => binder.SelectedProfile = Item(id);
        try
        {
            binder.Show(); ToolsPump();
            check(receiverStarts == 1 && binder.CanInteract && !binder.CanRetryAirPlay,
                "Opening the binder did not start AirPlay automatically or blocked device binding.");
            Call("StartAirPlayClick", new Button(), new RoutedEventArgs());
            check(receiverStarts == 1, "AirPlay startup was duplicated while pending.");
            receiverReady.SetResult(true); ToolsPump();
            check(binder.AirPlayReceiverText == IPhoneMirror.App.Localization.LocalizationService.Get("DeviceBindingAirPlayReceiving") && !binder.CanRetryAirPlay,
                "Automatic AirPlay readiness was not displayed.");
            Select(first.Id);
            ToolsPump(450);
            var detailSurface = ComponentVisuals<StackPanel>(binder).Single(p =>
                Equals(IPhoneMirror.UI.Animations.SurfaceMotion.GetRefreshKey(p), first.Id));
            manager.RefreshFromDisk(); Call("SynchronizeProfiles"); ToolsPump(30);
            check(detailSurface.Opacity > .99, "Refreshing an unchanged profile replayed the detail entrance animation.");
            manager.RenameProfile(first.Id, "Updated A"); Call("SynchronizeProfiles"); ToolsPump(30);
            check(detailSurface.Opacity > .99 && binder.ProfileTitle == "Updated A",
                "Updating the selected profile replayed its entrance animation or lost its new name.");
            binder.RequestProfileName = _ => { Select(second.Id); return "Renamed A"; };
            Call("RenameProfileClick", new Button(), new RoutedEventArgs());
            check(manager.FindById(first.Id)?.DisplayName == "Renamed A" && manager.FindById(second.Id)?.DisplayName == "B",
                "Rename targeted the profile selected during its prompt.");
            var wireless = Device("airplay://candidate", "Candidate"); devices.Add(wireless); ToolsPump();
            Select(first.Id); binder.SelectedAirPlayDevice = wireless;
            binder.ConfirmOperation = (_, _) => { Select(second.Id); return true; };
            Call("BindAirPlayClick", new Button(), new RoutedEventArgs());
            check(manager.FindById(first.Id)?.AirPlayIdentity?.StableId == wireless.Udid && manager.FindById(second.Id)?.AirPlayIdentity is null,
                "Binding confirmation changed the target profile.");
            Call("CreateProfile", wireless);
            check(binder.SelectedProfile?.Id == first.Id && manager.Profiles.Count == 2,
                "Adding an existing AirPlay identity created a duplicate or failed to select its profile.");
            Select(second.Id);
            var dropped = Device("airplay://dropped", "Disconnected candidate"); devices.Add(dropped); binder.SelectedAirPlayDevice = dropped;
            binder.ConfirmOperation = (_, _) => { devices.Remove(dropped); return true; };
            Call("BindAirPlayClick", new Button(), new RoutedEventArgs());
            check(manager.FindById(second.Id)?.AirPlayIdentity is null, "A device removed during confirmation was bound.");
            devices.Add(dropped); binder.SelectedAirPlayDevice = dropped;
            binder.ConfirmOperation = (_, _) => { dropped.UpdateFrom(Device(dropped.Udid, dropped.Name, ConnectionState.Disconnected)); return true; };
            Call("BindAirPlayClick", new Button(), new RoutedEventArgs());
            check(manager.FindById(second.Id)?.AirPlayIdentity is null, "An unavailable candidate was accepted after confirmation.");
            // An item may become unavailable without leaving the collection.
            Select(first.Id);
            wireless.UpdateFrom(Device(wireless.Udid, "Candidate", ConnectionState.Disconnected)); ToolsPump();
            check(binder.AirPlayStatus != IPhoneMirror.App.Localization.LocalizationService.Get("DeviceBindingBoundMirroring"),
                "A disconnected AirPlay item still reported active mirroring.");
            var chooser = new AirPlayDeviceSelectionWindow(binder, devices, previewOnly: true);
            chooser.Show();
            try
            {
                check(chooser.Devices.Count == 0, "Chooser contains unavailable or USB devices.");
                var live = Device("airplay://new", "New iPhone"); devices.Add(live); ToolsPump();
                check(chooser.Devices.Contains(live), "An AirPlay connection made after opening the chooser did not appear.");
                chooser.SelectedDevice = live; check(chooser.CanConfirm, "A live AirPlay device cannot be confirmed.");
                live.UpdateFrom(Device(live.Udid, "New iPhone", ConnectionState.Disconnected)); ToolsPump();
                check(!chooser.CanConfirm && chooser.SelectedDevice is null, "Chooser can confirm a disconnected device.");
            }
            finally { chooser.Close(); }
            // Remember an intended device that is not present yet.
            binder.SelectIdentity("wired-late"); devices.Add(Device("wired-late", "Late iPhone")); ToolsPump();
            check(binder.SelectedProfile?.Profile.WiredIdentity?.Udid == "wired-late", "Late target discovery selected another device.");
            binder.SelectIdentity("wired-next"); Select(second.Id);
            devices.Add(Device("wired-next", "Later iPhone")); ToolsPump();
            check(binder.SelectedProfile?.Id == second.Id, "Late discovery overwrote a user's explicit profile selection.");
            var bluetoothOnly = manager.CreateProfileFromIdentity("Bluetooth only", DeviceIdentityType.Bluetooth, "bt-only", null).Profile!;
            Call("SynchronizeProfiles"); Select(bluetoothOnly.Id);
            check(!binder.CanConnectBluetooth, "Bluetooth configuration is enabled without a USB or AirPlay target.");
            // Refresh must leave an unsubmitted selection intact and clear it after disconnect.
            var candidate = Device("airplay://selected", "Selected iPhone"); devices.Add(candidate); ToolsPump();
            Select(second.Id); binder.SelectedAirPlayDevice = candidate;
            Call("SynchronizeProfiles"); ToolsPump();
            check(ReferenceEquals(binder.SelectedAirPlayDevice, candidate), "Refresh lost an unsubmitted AirPlay choice.");
            devices.Remove(candidate); ToolsPump(); check(!binder.CanBindAirPlay, "Removed candidate left its binding action enabled.");
            // Delete the original confirmed target even if selection changes while the prompt is open.
            Select(first.Id); binder.ConfirmOperation = (_, _) => { Select(second.Id); return true; };
            Call("DeleteProfileClick", new Button(), new RoutedEventArgs()); ToolsPump();
            Call("CreateProfilesForConnectedUsbDevices"); Call("SynchronizeProfiles");
            check(manager.FindById(first.Id) is null && manager.FindById(second.Id) is not null && manager.FindByIdentity(DeviceIdentityType.Wired, "wired-a") is null,
                "Delete changed its target or immediately recreated a connected USB profile.");
            // A result that finishes after close must never repopulate the closed window.
            var pending = new TaskCompletionSource<IReadOnlyList<BluetoothClientInfo>>();
            binder.ReadBluetoothClientsAsync = _ => pending.Task;
            var refresh = (Task)Call("RefreshBluetoothClientsAsync")!;
            binder.Close(); pending.SetResult([new("late", "Late client", "", DateTimeOffset.UtcNow)]);
            try { ToolsComplete(refresh); }
            catch (OperationCanceledException) { }
            ToolsComplete(binder.CleanupTask);
            check(binder.BluetoothClients.Count == 0, "A late refresh changed a closed binder.");
            var surface = new System.Windows.Controls.Border { Width = 120, Height = 80, Opacity = .55 };
            var animationWindow = new Window { Width = 180, Height = 140, Content = surface, ShowInTaskbar = false };
            IPhoneMirror.UI.Animations.SurfaceMotion.SetEnabled(surface, true);
            try
            {
                animationWindow.Show(); ToolsPump(60);
                IPhoneMirror.UI.Animations.SurfaceMotion.SetRefreshKey(surface, "A"); ToolsPump(60);
                IPhoneMirror.UI.Animations.SurfaceMotion.SetRefreshKey(surface, "B"); ToolsPump(450);
                check(Math.Abs(surface.Opacity - .55) < .001, "Repeated surface motion overwrote its opacity.");
                var identity = Guid.NewGuid();
                IPhoneMirror.UI.Animations.SurfaceMotion.SetRefreshKey(surface, identity); ToolsPump(450);
                IPhoneMirror.UI.Animations.SurfaceMotion.SetRefreshKey(surface, Guid.Parse(identity.ToString())); ToolsPump(30);
                check(Math.Abs(surface.Opacity - .55) < .001, "An equal reboxed identity restarted the entrance animation.");
                IPhoneMirror.UI.Animations.SurfaceMotion.SetEnabled(surface, false);
                check(!surface.HasAnimatedProperties && Math.Abs(surface.Opacity - .55) < .001, "Disabling motion retained an opacity clock.");
            }
            finally { animationWindow.Close(); }
            Console.WriteLine("PASS repeated transitions preserve opacity and release animation clocks.");
            Console.WriteLine("PASS modal target stability, live AirPlay chooser, late target discovery, user selection, stale-device rejection, delete suppression and close cleanup.");
            var failedReceiver = new DeviceBindingWindow(owner, [], vm, manager: manager)
            {
                ReadBluetoothClientsAsync = _ => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([]),
                ScanDevicesAsync = _ => Task.CompletedTask,
                StartAirPlayReceiverAsync = _ => Task.FromResult(false)
            };
            try
            {
                failedReceiver.Show(); ToolsPump();
                check(failedReceiver.CanRetryAirPlay && failedReceiver.CanInteract, "Automatic AirPlay failure has no retry or blocked USB binding.");
                var lateStart = new TaskCompletionSource<bool>();
                CancellationToken receiverToken = default;
                failedReceiver.StartAirPlayReceiverAsync = token => { receiverToken = token; return lateStart.Task; };
                typeof(DeviceBindingWindow).GetMethod("StartAirPlayClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(failedReceiver, [new Button(), new RoutedEventArgs()]); ToolsPump();
                check(!failedReceiver.CanRetryAirPlay, "AirPlay retry remained enabled during startup.");
                failedReceiver.Close();
                check(receiverToken.IsCancellationRequested && !failedReceiver.CleanupTask.IsCompleted,
                    "Closing the binder did not cancel and await AirPlay startup.");
                var lateUpdates = 0;
                failedReceiver.PropertyChanged += (_, _) => lateUpdates++;
                lateStart.SetResult(true); ToolsComplete(failedReceiver.CleanupTask);
                check(lateUpdates == 0, "A late AirPlay startup updated the closed binder.");
            }
            finally { failedReceiver.Close(); }
            Console.WriteLine("PASS automatic AirPlay startup, responsive binding, single flight, readiness, failure retry and startup close cleanup.");
        }
        finally { binder.Close(); }
    }
}
