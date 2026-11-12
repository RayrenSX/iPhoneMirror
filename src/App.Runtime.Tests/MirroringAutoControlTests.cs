using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        store.Save(app.UpdateSettings);
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
                InteractionAssert(control.ControlStatus.QuietAutomaticStartup,
                    "Automatic USB/wireless startup did not mark its status surface as quiet.");
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
            TestAutoControlLiveSelection(vm, app, state, sessions, controls, streaming, store, window, output);
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

    private static void TestAutoControlLiveSelection(MainViewModel vm, App app, DeviceCaptureState state,
        DeviceSessionManager sessions, Dictionary<string, DeviceControlSession> controls,
        NativeCaptureStatus streaming, UpdateSettingsStore store, ProjectionSettingsWindow window, string output)
    {
        var combo = FindVisualDescendants<ComboBox>(window).Single(box =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "MirroringAutoControlComboBox");
        var option = vm.MirroringAutoControlModes.Single(item => item.Mode == ReverseControlMode.Usb);
        void Observe() => KeyboardCall(vm, "TryStartMirroringControl", state, streaming);
        combo.SelectedItem = vm.MirroringAutoControlModes[0];
        AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        state.ResetRuntimeObservations();
        Observe();
        InteractionAssert(!state.AutoControlAttempted, "Off must not consume the stream's automatic startup attempt.");
        var blocked = Path.Combine(output, "blocked-auto-settings");
        File.WriteAllText(blocked, "blocked");
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(blocked), "settings.json")));
        app.IsUiPreviewMode = false;
        try
        {
            combo.SelectedItem = option;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            InteractionAssert(vm.SelectedMirroringAutoControlMode.Mode == ReverseControlMode.None &&
                ReferenceEquals(combo.SelectedItem, vm.MirroringAutoControlModes[0]) &&
                vm.MirroringAutoControlStatus.Length > 0 && !controls.ContainsKey(state.Udid),
                "A failed dropdown save must restore the visible choice, show its error and avoid starting control.");
            SetKeyboardField(app, "_settingsStore", store);
            combo.SelectedItem = option;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(store.Load().MirroringAutoControlMode == ReverseControlMode.Usb &&
                ReferenceEquals(combo.SelectedItem, option) && vm.MirroringAutoControlStatus.Length == 0 &&
                controls.TryGetValue(state.Udid, out var control) && control.ControlStatus.Current?.Mode == ControlStatusMode.Usb,
                "Choosing USB while mirroring is already streaming did not save and attempt the exact device.");
            var snapshot = controls[state.Udid].ControlStatus.Current;
            Observe();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(ReferenceEquals(snapshot, controls[state.Udid].ControlStatus.Current),
                "A failed automatic attempt restarted without a new preference selection.");
            controls.Clear();
            // Rapid choices must cancel old queued work, even if the final mode matches it again.
            combo.SelectedItem = vm.MirroringAutoControlModes.Single(item => item.Mode == ReverseControlMode.Wireless);
            combo.SelectedItem = option;
            combo.SelectedItem = vm.MirroringAutoControlModes[0];
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            InteractionAssert(controls.Count == 0, "A stale selected mode started after the user turned automatic control off.");

            var pending = (HashSet<string>)KeyboardField(vm, "_mirroringControlStartup");
            pending.Add(state.Udid);
            combo.SelectedItem = option;
            Observe();
            InteractionAssert(!state.AutoControlAttempted, "A temporarily busy device consumed its only automatic attempt.");
            pending.Clear();
            Observe();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            InteractionAssert(controls.ContainsKey(state.Udid), "Automatic startup did not resume after the temporary guard cleared.");
            controls.Clear();
            state.AutoControlAttempted = false;
            var manual = new DeviceControlSession(state.Udid) { WiredEnabled = true };
            var manualStatus = manual.ControlStatus.Current;
            controls[state.Udid] = manual;
            Observe();
            manual.WiredEnabled = false;
            Observe();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(ReferenceEquals(manualStatus, manual.ControlStatus.Current),
                "Manually stopping an existing control route must not automatically restart it.");
            controls.Clear();
        }
        finally { app.IsUiPreviewMode = true; SetKeyboardField(app, "_settingsStore", store); }
        Console.WriteLine("PASS auto-control dropdown: live selection, disk roundtrip, visible save rollback/error, retry, stale choices, busy guard and once-per-choice startup.");
    }
}
