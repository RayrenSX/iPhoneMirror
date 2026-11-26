using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunUnifiedDeviceCardTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        LocalizationService.SetLanguage("zh-CN");
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), window,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var originalBindings = KeyboardField(vm, "_reverseBindings");
        var originalSessions = KeyboardField(vm, "_sessions");
        var originalReader = KeyboardField(vm, "_captureStatusReader");
        var bindings = new DeviceBindingManager(Path.Combine(output, "bindings-" + Guid.NewGuid().ToString("N") + ".json"));
        var stops = 0;
        var sessions = new DeviceSessionManager(_ => stops++, _ => stops++);
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_sessions", sessions);
        SetKeyboardField(vm, "_captureStatusReader", (Func<NativeSessionHandle, NativeCaptureStatus>)(_ =>
            new NativeCaptureStatus { State = CaptureState.Streaming, Width = 1206, Height = 2622, VideoFrames = 10 }));
        var refreshBindings = (Action)Delegate.CreateDelegate(typeof(Action), vm,
            typeof(MainViewModel).GetMethod("QueueDeviceCardRefresh", KeyboardTestMembers)!);
        bindings.ProfilesChanged += refreshBindings;
        const string apple = "00008150-0000000000000001";
        const string airplayId = "airplay://BA:22:5F:5C:D6:21";
        var profile = bindings.CreateProfileFromIdentity("Ray’s iPhone", DeviceIdentityType.Wired, apple, null).Profile!;
        InteractionAssert(bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, airplayId, "Ray’s iPhone", null, userConfirmed: true).Success,
            "The test identities must belong to one confirmed profile.");
        var wifi = DeviceViewModel.FromPairedNetwork(new(apple, "Ray’s iPhone", "iPhone18,3", "27.2.1"), profile);
        DeviceViewModel Source(string id, string transport) => DeviceViewModel.FromNative(new NativeDeviceInfo {
            Udid = id, Name = "Ray’s iPhone", ProductType = "iPhone18,3", OsVersion = "27.2.1",
            ConnectionType = transport, State = ConnectionState.Ready }, DeviceAppearance.Unknown);
        var airplay = Source(airplayId, "AirPlay");
        var other = Source("00008150-0000000000000002", "USB");
        using var fake = new NativeSessionHandle(70101, ownsHandle: false);
        var state = new DeviceCaptureState { Udid = airplayId, Handle = fake, IsMirroring = true };
        try
        {
            KeyboardCall(vm, "ReconcileDevices", new[] { wifi }, apple, false, null);
            var leftPanelType = KeyboardField(window, "_leftWorkspacePanel").GetType();
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(leftPanelType, "None"));
            vm.Devices.Add(airplay); ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 1 && KeyboardField(window, "_leftWorkspacePanel").ToString() == "None",
                "A second transport for the same physical phone must not auto-open the devices sidebar.");
            KeyboardCall(vm, "ReconcileDevices", new[] { wifi, other }, apple, false, null);
            sessions.Set(state);
            KeyboardCall(vm, "ReconcileDevices", new[] { wifi, other, airplay }, apple, false, null);
            ToolsPump();
            var list = (ListBox)window.FindName("DeviceListBox");
            InteractionAssert(vm.Devices.Count == 3 && vm.DeviceCards.Count == 2 && list.Items.Count == 2,
                "One bound physical phone must have one visible card while all transport routes remain available internally.");
            InteractionAssert(ReferenceEquals(vm.DeviceCards[0], airplay) && ReferenceEquals(vm.SelectedDevice, airplay) &&
                ReferenceEquals(sessions.Get(airplayId)?.Handle, fake) && stops == 0,
                "The active AirPlay session must represent the phone without stopping or replacing its handle.");
            InteractionAssert(vm.DeviceCards.Contains(other), "A separate phone with the same name must remain separate.");
            var selectionNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler selectionObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SelectedDevice)) selectionNotifications++;
            };
            vm.PropertyChanged += selectionObserver;
            for (var refresh = 0; refresh < 10; refresh++) KeyboardCall(vm, "RefreshDeviceCards");
            vm.PropertyChanged -= selectionObserver;
            InteractionAssert(selectionNotifications == 0 && ReferenceEquals(list.SelectedItem, airplay),
                "An unchanged inventory must not publish fake selection changes or disturb the ListBox selection.");
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
            FinishWorkspaceReveal(window);
            ThemeService.Apply(AppTheme.Light); ToolsPump();
            SaveWindowRender(window, Path.Combine(output, "unified-device-airplay-light.png"));
            ThemeService.Apply(AppTheme.Dark); ToolsPump();
            SaveWindowRender(window, Path.Combine(output, "unified-device-airplay-dark.png"));
            var usb = Source(apple, "USB");
            KeyboardCall(vm, "ReconcileDevices", new[] { usb, other, airplay }, airplayId, false, null);
            InteractionAssert(ReferenceEquals(vm.DeviceCards[0], airplay) && stops == 0,
                "Plugging in USB must not displace a live AirPlay session.");
            string? powerTarget = null;
            window.ConfirmDeviceAction = (_, _) => true;
            window.ExecuteDeviceAction = (id, _, _) => { powerTarget = id; return Task.CompletedTask; };
            ToolsComplete(window.RunDeviceActionAsync(airplay, DeviceAction.Restart));
            InteractionAssert(powerTarget == apple && ReferenceEquals(vm.SelectedDevice, airplay) && stops == 0,
                "The merged card must route USB actions to its bound wired identity without changing the active session.");
            vm.Devices.Move(vm.Devices.IndexOf(other), 0);
            sessions.Remove(airplayId);
            KeyboardCall(vm, "ReconcileDevices", new[] { usb, other }, airplayId, false, null);
            InteractionAssert(vm.DeviceCards.Count == 2 && vm.DeviceCards[0].Udid == apple &&
                vm.DeviceCards[0].CardTransport == "USB" && ReferenceEquals(vm.SelectedDevice, vm.DeviceCards[0]),
                "AirPlay disconnect must return to USB in the same position and retain the physical-device selection.");
            KeyboardCall(vm, "ReconcileDevices", new[] { DeviceViewModel.FromPairedNetwork(
                new(apple, "Ray’s iPhone", "iPhone18,3", "27.2.1"), profile), other }, apple, false, null);
            InteractionAssert(vm.DeviceCards[0].IsPairedNetwork && ReferenceEquals(vm.SelectedDevice, vm.DeviceCards[0]),
                "USB unplug must return the grouped phone to its Wi-Fi route.");
            vm.MoveDevice(vm.DeviceCards[0], other, placeAfterTarget: true); ToolsPump();
            KeyboardCall(vm, "RefreshDeviceCards");
            InteractionAssert(ReferenceEquals(vm.DeviceCards[0], other), "Device drag order must survive grouping refresh.");
            var unknown = Source("airplay://unbound-same-name", "AirPlay");
            vm.Devices.Add(unknown); ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 3, "An unbound sender must not merge merely because its name/model matches.");
            vm.Devices.Remove(unknown);
            vm.Devices.Add(airplay); ToolsPump();
            vm.SelectedDevice = other; ToolsPump();
            state.IsMirroring = false;
            sessions.Set(state);
            KeyboardCall(vm, "ObserveMirroringVideo", state, new NativeCaptureStatus {
                State = CaptureState.Streaming, Width = 1206, Height = 2622 });
            ToolsPump();
            InteractionAssert(vm.DeviceCards.Contains(airplay) && ReferenceEquals(vm.SelectedDevice, other) &&
                ReferenceEquals(list.SelectedItem, other),
                "A background stream must update its physical card immediately without stealing selection.");
            sessions.Remove(airplayId);
            typeof(MainViewModel).GetMethod("RefreshSourceCardMirroring", KeyboardTestMembers, null,
                [typeof(string)], null)!.Invoke(vm, [airplayId]); ToolsPump();
            InteractionAssert(!vm.DeviceCards.Contains(airplay) && vm.DeviceCards.Any(card => card.IsPairedNetwork) &&
                ReferenceEquals(vm.SelectedDevice, other),
                "A background session ending must return its card to Wi-Fi without waiting for another inventory poll.");
            InteractionAssert(bindings.Unbind(profile.Id, DeviceIdentityType.AirPlay), "Fixture unbind failed.");
            ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 3 && ReferenceEquals(vm.SelectedDevice, other),
                "Unbinding must split the cards immediately and retain an unrelated selection.");
            vm.SelectedDevice = airplay; ToolsPump();
            InteractionAssert(bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, airplayId,
                "Ray’s iPhone", null, userConfirmed: true).Success, "Fixture rebind failed.");
            ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 2 && vm.SelectedDevice?.IsPairedNetwork == true &&
                ReferenceEquals(list.SelectedItem, vm.SelectedDevice),
                "Rebinding must merge immediately and preserve selection of the physical phone in the real ListBox.");
            var beforeCast = vm.SelectedDevice;
            vm.BeginMediaCast(1); ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 3 && vm.DeviceCards[0].IsMediaCast && vm.SelectedDevice?.IsMediaCast == true,
                "A new virtual media cast must retain its original first-card placement and remain separate from phones.");
            vm.EndMediaCast(); ToolsPump();
            InteractionAssert(vm.DeviceCards.Count == 2 && ReferenceEquals(vm.SelectedDevice, beforeCast) &&
                ReferenceEquals(list.SelectedItem, beforeCast), "Ending a virtual cast must restore the grouped phone selection.");
            using (var tray = new UnifiedCardTrayScope(vm))
            {
                InteractionAssert(tray.List.Items.Count == 2 && ReferenceEquals(tray.List.SelectedItem, vm.SelectedDevice),
                    "The tray picker must share the physical-card inventory and selection.");
                vm.Devices.Clear(); ToolsPump();
                InteractionAssert(list.Items.Count == 0 && tray.List.Items.Count == 0 && vm.SelectedDevice is null,
                    "Removing the last devices must clear both pickers and their selection.");
            }
            var verification = "PASS: confirmed physical identities, live session preservation, USB/Wi-Fi fallback, " +
                "same-name isolation, drag order, unchanged refresh without selection notifications, background stream updates, " +
                "live bind/unbind, physical sidebar auto-reveal, synchronized tray selection and empty inventory.";
            File.WriteAllText(Path.Combine(output, "verification.txt"), verification + Environment.NewLine +
                "Screenshots use fixture devices; no real device command was sent." + Environment.NewLine);
            Console.WriteLine(verification);
            return 0;
        }
        finally
        {
            bindings.ProfilesChanged -= refreshBindings;
            sessions.Remove(airplayId);
            vm.Devices.Clear(); ToolsPump();
            SetKeyboardField(vm, "_reverseBindings", originalBindings);
            SetKeyboardField(vm, "_sessions", originalSessions);
            SetKeyboardField(vm, "_captureStatusReader", originalReader);
            CloseWorkspaceTestWindow(window); app.Shutdown();
        }
    }

    private sealed class UnifiedCardTrayScope : IDisposable
    {
        private readonly TrayPanelWindow _window;
        internal ListBox List => (ListBox)_window.FindName("DeviceList");
        internal UnifiedCardTrayScope(MainViewModel vm)
        {
            _window = new TrayPanelWindow(vm, _ => Task.CompletedTask, () => { }, () => { });
            _window.Show(); ToolsPump();
        }
        public void Dispose() => _window.ClosePermanently();
    }
}
