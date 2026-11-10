using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMirroringAutoControlTests(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var vm = new MainViewModel();
        var sessions = new DeviceSessionManager(_ => { }, _ => { });
        SetKeyboardField(vm, "_sessions", sessions);
        var bindings = new DeviceBindingManager(Path.Combine(Path.GetFullPath(output), "bindings.json"));
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_identityResolver", new DeviceIdentityResolver(bindings));
        using var handle = new NativeSessionHandle(801, ownsHandle: false);
        using var replacement = new NativeSessionHandle(802, ownsHandle: false);
        var state = new DeviceCaptureState { Udid = "auto-control-phone", Handle = handle };
        var streaming = new NativeCaptureStatus { State = CaptureState.Streaming };
        sessions.Set(state);
        vm.Devices.Add(DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = state.Udid, Name = "Auto control phone", State = ConnectionState.Ready }));
        var controls = (Dictionary<string, DeviceControlSession>)KeyboardField(vm, "_deviceControls");
        void Observe(NativeCaptureStatus status) => KeyboardCall(vm, "TryStartMirroringControl", state, status);
        try
        {
            InteractionAssert(new UpdateSettings().MirroringAutoControlMode == ReverseControlMode.None,
                "Existing users must default to manual control.");
            foreach (var option in vm.MirroringAutoControlModes)
            {
                app.IsUiPreviewMode = false;
                try { vm.SelectedMirroringAutoControlMode = option; }
                finally { app.IsUiPreviewMode = true; }
                InteractionAssert(store.Load().MirroringAutoControlMode == option.Mode &&
                    app.UpdateSettings.Clone().MirroringAutoControlMode == option.Mode,
                    "Control mode did not survive save/load/clone.");
            }
            app.UpdateSettings.MirroringAutoControlMode = (ReverseControlMode)999;
            InteractionAssert(vm.SelectedMirroringAutoControlMode.Mode == ReverseControlMode.None,
                "Invalid saved mode did not fall back to manual.");
            app.UpdateSettings.MirroringAutoControlMode = ReverseControlMode.None;
            Observe(streaming);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(controls.Count == 0, "Disabled auto control started a route.");

            foreach (var mode in new[] { ReverseControlMode.Usb, ReverseControlMode.Wireless })
            {
                state.ResetRuntimeObservations();
                app.UpdateSettings.MirroringAutoControlMode = mode;
                Observe(new NativeCaptureStatus { State = CaptureState.Handshaking });
                InteractionAssert(!state.AutoControlAttempted, "Control started before capture connected.");
                Observe(streaming);
                // Selection changes before the deferred start must not redirect it.
                SetKeyboardField(vm, "_selectedDevice", DeviceViewModel.FromNative(new NativeDeviceInfo
                { Udid = "other-selected-phone", Name = "Other", State = ConnectionState.Ready }));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                var control = controls[state.Udid];
                InteractionAssert(control.ControlStatus.Current is { Stage: ControlStage.Failed, Prompt: null } &&
                    control.ControlStatus.Current.Mode == (mode == ReverseControlMode.Usb ? ControlStatusMode.Usb : ControlStatusMode.Wireless) &&
                    control.WiredBridge is null && control.WirelessBridge is null && controls.Count == 1,
                    "Auto start skipped binding validation or targeted the newly selected phone.");
                var snapshot = control.ControlStatus.Current;
                Observe(streaming);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                InteractionAssert(ReferenceEquals(snapshot, control.ControlStatus.Current),
                    "A failed automatic attempt repeated on the next poll.");
                controls.Clear();
            }

            // Stop/restart invalidates queued work and enables one fresh attempt.
            state.ResetRuntimeObservations();
            Observe(streaming);
            sessions.SetHandle(state, replacement);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(controls.Count == 0 && !state.AutoControlAttempted,
                "A stale queued start followed a replacement capture handle.");

            var pending = (HashSet<string>)KeyboardField(vm, "_mirroringControlStartup");
            pending.Add(state.Udid);
            var controlSession = new DeviceControlSession(state.Udid);
            foreach (var wireless in new[] { false, true })
            {
                var prerequisite = (Task<bool>)KeyboardCall(vm, "ConfirmReverseControlPrerequisitesAsync",
                    controlSession, wireless, CancellationToken.None)!;
                InteractionAssert(prerequisite.IsCompletedSuccessfully && prerequisite.Result &&
                    controlSession.ControlStatus.Current?.Prompt is null,
                    "Automatic startup required a second Continue confirmation.");
            }
            pending.Clear();

            var window = new ProjectionSettingsWindow(vm, () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, () => Task.CompletedTask, () => { }, previewOnly: true);
            window.Show();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            SaveWindowRender(window, Path.Combine(output, "mirroring-auto-control.png"));
            window.Close();
            Console.WriteLine("PASS auto control: persisted modes, manual default, invalid mode, streaming gate, exact target, once per session, stale handle and no Continue prompt.");
            return 0;
        }
        finally
        {
            sessions.Remove(state.Udid);
            WaitReviewTask(vm.ShutdownAsync());
            app.Shutdown();
        }
    }
}
