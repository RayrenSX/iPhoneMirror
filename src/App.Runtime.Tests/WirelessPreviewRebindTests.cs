using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWirelessPreviewRebindTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        var vm = new MainViewModel();
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var selectedField = typeof(NativeCore).GetField("_selectedPreviewSession", staticMembers)!;
        var windowField = typeof(NativeCore).GetField("_selectedPreviewWindow", staticMembers)!;
        var originalSelection = (long)selectedField.GetValue(null)!;
        var originalWindow = (nint)windowField.GetValue(null)!;
        using var first = new NativeSessionHandle(801, ownsHandle: false);
        using var replacement = new NativeSessionHandle(802, ownsHandle: false);
        using var unrelated = new NativeSessionHandle(803, ownsHandle: false);
        var device = DeviceViewModel.FromNative(new NativeDeviceInfo
        {
            Udid = "airplay://wireless-preview-rebind-fixture", Name = "Preview fixture",
            State = ConnectionState.Ready, ProductType = "iPhone18,3",
        });
        var state = new DeviceCaptureState { Udid = device.Udid };
        var notifications = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentSessionHandle)) notifications++;
        };
        ulong Selection() => unchecked((ulong)(long)selectedField.GetValue(null)!);
        try
        {
            // There is no preview HWND in this fixture. Non-owning identities
            // exercise selection only, never attach a renderer to a fake session.
            selectedField.SetValue(null, 0L);
            windowField.SetValue(null, (nint)0);
            sessions.Set(state);
            vm.Devices.Add(device);
            KeyboardCall(vm, "SetSelectedDevice", device, false);
            var before = notifications;

            sessions.SetHandle(state, first);
            InteractionAssert(ReferenceEquals(vm.SelectedDevice, device) &&
                vm.CurrentSessionHandle == 801 && Selection() == 801 && notifications > before,
                "A newly created session on the already selected wireless card was not selected for the native preview.");

            sessions.SetHandle(state, null);
            InteractionAssert(Selection() == 0 && vm.CurrentSessionHandle == 0,
                "Removing the selected receiver session retained its native preview identity.");
            sessions.Remove(state.Udid);
            var nextState = new DeviceCaptureState { Udid = device.Udid };
            sessions.Set(nextState);
            before = notifications;
            sessions.SetHandle(nextState, replacement);
            // This is the same stable item, as after a receiver-profile restart.
            // SetSelectedDevice intentionally takes its ReferenceEquals early exit.
            KeyboardCall(vm, "SetSelectedDevice", device, false);
            InteractionAssert(ReferenceEquals(vm.SelectedDevice, device) &&
                vm.CurrentSessionHandle == 802 && Selection() == 802 && notifications > before,
                "A replacement session on the same wireless card left native preview routing on the legacy empty source.");
            KeyboardCall(sessions, "PublishHandleChanged", device.Udid, first.RawHandle);
            InteractionAssert(Selection() == 802 && vm.CurrentSessionHandle == 802,
                "A delayed old-handle notification replaced the manager's current preview session.");

            var other = new DeviceCaptureState { Udid = "airplay://unselected-preview-fixture" };
            sessions.Set(other);
            sessions.SetHandle(other, unrelated);
            InteractionAssert(Selection() == 802 && vm.CurrentSessionHandle == 802,
                "An unselected device's new session took over the main preview.");
            sessions.SetHandle(other, null);
            InteractionAssert(Selection() == 802,
                "An unselected device's stop cleared the current preview.");
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                sameDeviceReference = ReferenceEquals(vm.SelectedDevice, device),
                selectedHandle = Selection(), currentSessionHandle = vm.CurrentSessionHandle,
                notifications,
                scope = "Real MainViewModel/DeviceSessionManager handle events with non-owning fixture identities; no native renderer is attached.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS selected wireless session creation/replacement rebinds the native preview and notifies the unchanged card; unrelated devices cannot steal it.");
            return 0;
        }
        finally
        {
            // Never leave fake session identities in shared static preview state.
            selectedField.SetValue(null, originalSelection);
            windowField.SetValue(null, originalWindow);
            sessions.Remove(device.Udid);
            sessions.Remove("airplay://unselected-preview-fixture");
            app.Shutdown();
        }
    }
}
