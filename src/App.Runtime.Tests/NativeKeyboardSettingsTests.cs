using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunNativeKeyboardSettingsTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(output, "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var fixtures = new List<NativeKeyboardBridgeFixture>();
        var retiredFixtures = new List<NativeKeyboardBridgeFixture>();
        try
        {
            InteractionAssert(vm.ShowNativeKeyboard &&
                JsonSerializer.Deserialize<UpdateSettings>("{}")!.ShowNativeKeyboard,
                "Native phone keyboard must be shown by default, including older settings.");
            ((RadioButton)window.FindName("ControlSettingsTab")).IsChecked = true;
            var button = (Button)window.FindName("NativeKeyboardVisibilityButton");
            var error = (TextBlock)window.FindName("NativeKeyboardError");
            InteractionAssert(ReferenceEquals(button.Command, vm.ToggleNativeKeyboardCommand) &&
                button.Content?.ToString() == LocalizationService.Get("NativeKeyboardShown") &&
                button.ToolTip?.ToString() == LocalizationService.Get("NativeKeyboardDescription"),
                "The native keyboard button is missing or bound to the wrong command.");
            var keyboardGroup = (StackPanel)button.Parent;
            InteractionAssert(keyboardGroup.Children.IndexOf(error) < keyboardGroup.Children.IndexOf(button) &&
                !keyboardGroup.Children.Cast<UIElement>().Skip(keyboardGroup.Children.IndexOf(button) + 1).OfType<TextBlock>().Any(),
                "Native keyboard feedback must appear above the button, without explanatory or success text below it.");
            AssertKeyboardError(false);
            app.IsUiPreviewMode = false;
            InteractionAssert(Finish(vm.SetNativeKeyboardVisibleAsync(false)) &&
                !vm.ShowNativeKeyboard && !store.Load().ShowNativeKeyboard &&
                !app.UpdateSettings.Clone().ShowNativeKeyboard,
                "A disconnected keyboard preference did not survive save/load/clone.");
            AssertKeyboardError(false);
            InteractionAssert(Finish(vm.SetNativeKeyboardVisibleAsync(true)), "Could not restore shown preference.");
            var unavailable = new UsbTouchBridgeHost { NativeKeyboardVisible = false };
            KeyboardCall(vm, "CheckNativeKeyboardCapability", unavailable);
            InteractionAssert(vm.ShowNativeKeyboard && store.Load().ShowNativeKeyboard &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardUnavailable"),
                "A startup fallback must warn without overwriting the saved keyboard preference.");
            AssertKeyboardError(true);
            unavailable.DisposeAsync().AsTask().GetAwaiter().GetResult();

            foreach (var wireless in new[] { false, true })
            {
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl",
                    wireless ? "keyboard-wireless-fixture" : "keyboard-wired-fixture")!;
                var fixture = new NativeKeyboardBridgeFixture(control, wireless);
                fixtures.Add(fixture);
            }
            // An existing bridge can replace its HID sender and publish ready
            // again without restarting the host. The warning belongs to all
            // current routes, not merely the last one to report ready.
            SetKeyboardField(fixtures[0].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", false);
            fixtures[0].Host.NativeKeyboardVisible = false;
            KeyboardCall(vm, "CheckNativeKeyboardCapability", fixtures[0].Host);
            InteractionAssert(vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardUnavailable"),
                "A recovered sender's unsupported keyboard route was not reported.");
            AssertKeyboardError(true);
            SetKeyboardField(fixtures[0].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", true);
            fixtures[0].Host.NativeKeyboardVisible = true;
            SetKeyboardField(fixtures[1].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", false);
            fixtures[1].Host.NativeKeyboardVisible = false;
            KeyboardCall(vm, "CheckNativeKeyboardCapability", fixtures[0].Host);
            InteractionAssert(vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardUnavailable"),
                "One healthy recovered sender hid another route's keyboard mismatch.");
            SetKeyboardField(fixtures[1].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", true);
            fixtures[1].Host.NativeKeyboardVisible = true;
            KeyboardCall(vm, "CheckNativeKeyboardCapability", fixtures[1].Host);
            InteractionAssert(vm.NativeKeyboardStatus.Length == 0 && vm.ShowNativeKeyboard,
                "A recovered sender kept an obsolete unavailable warning after every live keyboard route was restored.");
            AssertKeyboardError(false);

            // A phone may mutate successfully before a recovery status wins
            // the race with its RPC reply. That attempted target never enters
            // the applied list, but its surviving owner still needs retirement.
            var interruptedChange = vm.SetNativeKeyboardVisibleAsync(false);
            WaitForPendingRequest(fixtures[0]);
            var interruptedFixture = fixtures[0];
            interruptedFixture.MarkRecovering();
            interruptedFixture.Acknowledge();
            InteractionAssert(!Finish(interruptedChange, fixtures) && vm.ShowNativeKeyboard &&
                store.Load().ShowNativeKeyboard && !interruptedFixture.ActualVisible &&
                !interruptedFixture.Control.WiredEnabled && interruptedFixture.Control.WiredBridge is null &&
                (int)KeyboardField(interruptedFixture.Host, "_started") == 0 &&
                KeyboardField(interruptedFixture.Inner, "_stdin") is null &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardRollbackFailed"),
                "A recovery-cancelled keyboard reply left its already changed attempted sender alive.");
            retiredFixtures.Add(interruptedFixture);
            fixtures[0] = new NativeKeyboardBridgeFixture(interruptedFixture.Control, wireless: false);
            foreach (var fixture in fixtures) fixture.Requested.Clear();

            // A recovering sender still owns its changed keyboard route. If
            // another peer rejects the transaction before that sender returns,
            // skip-and-forget rollback would revive it with the wrong mode.
            var recoveringChange = vm.SetNativeKeyboardVisibleAsync(false);
            fixtures[0].Acknowledge();
            WaitForPendingRequest(fixtures[1]);
            var recoveringFixture = fixtures[0];
            recoveringFixture.MarkRecovering();
            fixtures[1].RejectNext = true;
            InteractionAssert(!Finish(recoveringChange, fixtures) && vm.ShowNativeKeyboard &&
                store.Load().ShowNativeKeyboard && !recoveringFixture.Control.WiredEnabled &&
                recoveringFixture.Control.WiredBridge is null &&
                (int)KeyboardField(recoveringFixture.Host, "_started") == 0 &&
                KeyboardField(recoveringFixture.Inner, "_stdin") is null &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardRollbackFailed"),
                "A failed keyboard transaction left its changed recovering sender alive without rollback.");
            retiredFixtures.Add(recoveringFixture);
            fixtures[0] = new NativeKeyboardBridgeFixture(recoveringFixture.Control, wireless: false);
            foreach (var fixture in fixtures) fixture.Requested.Clear();

            // A peer can restart after acknowledging its change while the
            // transaction still waits for another phone. The replacement host
            // starts from the saved preference, so the old acknowledgement
            // cannot authorize persistence for that new route.
            var replacedChange = vm.SetNativeKeyboardVisibleAsync(false);
            fixtures[0].Acknowledge();
            WaitForPendingRequest(fixtures[1]);
            InteractionAssert(!fixtures[0].Host.NativeKeyboardVisible,
                "The first keyboard fixture did not apply its acknowledged change.");
            var retiredFixture = fixtures[0];
            retiredFixtures.Add(retiredFixture);
            fixtures[0] = new NativeKeyboardBridgeFixture(retiredFixture.Control, wireless: false);
            InteractionAssert(!Finish(replacedChange, fixtures) && vm.ShowNativeKeyboard &&
                store.Load().ShowNativeKeyboard && fixtures.All(fixture => fixture.Host.NativeKeyboardVisible) &&
                fixtures[1].Requested.SequenceEqual(new[] { false, true }) && vm.NativeKeyboardHasError,
                "A replaced keyboard route let an obsolete acknowledgement persist a partly applied preference.");
            foreach (var fixture in fixtures) fixture.Requested.Clear();

            // The same host can publish a newer ready generation with a
            // visible-only fallback after its earlier hide acknowledgement.
            // Final validation must inspect that actual state and retire the
            // route if it can no longer restore the old preference by RPC.
            var fallbackChange = vm.SetNativeKeyboardVisibleAsync(false);
            fixtures[0].Acknowledge();
            WaitForPendingRequest(fixtures[1]);
            var fallbackFixture = fixtures[0];
            fallbackFixture.MarkRecovering();
            fallbackFixture.MarkRecovered(visible: true, supported: false);
            KeyboardCall(vm, "CheckNativeKeyboardCapability", fallbackFixture.Host);
            InteractionAssert(!Finish(fallbackChange, fixtures) && vm.ShowNativeKeyboard &&
                store.Load().ShowNativeKeyboard && !fallbackFixture.Control.WiredEnabled &&
                fallbackFixture.Control.WiredBridge is null &&
                KeyboardField(fallbackFixture.Inner, "_stdin") is null &&
                fixtures[1].Host.NativeKeyboardVisible &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardRollbackFailed"),
                "A newer ready fallback let a stale keyboard acknowledgement overwrite the saved mode or survive failed rollback.");
            retiredFixtures.Add(fallbackFixture);
            fixtures[0] = new NativeKeyboardBridgeFixture(fallbackFixture.Control, wireless: false);
            foreach (var fixture in fixtures) fixture.Requested.Clear();

            InteractionAssert(Finish(vm.SetNativeKeyboardVisibleAsync(false), fixtures) &&
                fixtures.All(fixture => fixture.Requested.SequenceEqual(new[] { false })) &&
                !store.Load().ShowNativeKeyboard,
                "A setting change did not reach both live wired and wireless bridges.");
            AssertKeyboardError(false);

            // One rejected remote change must roll back an already updated
            // peer without persisting a partly applied global preference.
            fixtures[1].RejectNext = true;
            InteractionAssert(!Finish(vm.SetNativeKeyboardVisibleAsync(true), fixtures) &&
                !vm.ShowNativeKeyboard && !store.Load().ShowNativeKeyboard &&
                fixtures[0].Requested.TakeLast(2).SequenceEqual(new[] { true, false }) &&
                fixtures.All(fixture => fixture.Host.IsReady && fixture.Control.Enabled) &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardApplyFailed"),
                "A rejected remote change was persisted or left another bridge changed.");
            AssertKeyboardError(true);

            var blocked = Path.Combine(output, "blocked-parent");
            File.WriteAllText(blocked, "not a directory");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocked, "settings.json")));
            InteractionAssert(!Finish(vm.SetNativeKeyboardVisibleAsync(true), fixtures) &&
                !vm.ShowNativeKeyboard && !store.Load().ShowNativeKeyboard &&
                fixtures.All(fixture => fixture.Requested.TakeLast(2).SequenceEqual(new[] { true, false })) &&
                vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardSaveFailed"),
                "A failed save did not restore the live bridges and stored preference.");
            AssertKeyboardError(true);
            KeyboardCall(vm, "CheckNativeKeyboardCapability", fixtures[0].Host);
            InteractionAssert(vm.NativeKeyboardStatus == LocalizationService.Get("NativeKeyboardSaveFailed"),
                "A healthy ready notification hid an unresolved settings-save error.");
            SetKeyboardField(app, "_settingsStore", store);

            SetKeyboardField(fixtures[0].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", false);
            InteractionAssert(!Finish(vm.SetNativeKeyboardVisibleAsync(true), fixtures) &&
                !vm.ShowNativeKeyboard && !store.Load().ShowNativeKeyboard,
                "An older unsupported bridge pretended to apply keyboard visibility.");
            SetKeyboardField(fixtures[0].Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", true);
            fixtures[0].Control.Starting = true;
            InteractionAssert(!vm.CanChangeNativeKeyboard && !Finish(vm.SetNativeKeyboardVisibleAsync(true), fixtures),
                "Keyboard mode changed while a control route was still starting.");
            fixtures[0].Control.Starting = false;
            KeyboardCall(vm, "NotifyUsbControlStateChanged");
            InteractionAssert(Finish(vm.SetNativeKeyboardVisibleAsync(true), fixtures) && store.Load().ShowNativeKeyboard,
                "Retry did not save/apply the native keyboard preference.");
            AssertKeyboardError(false);
            KeyboardCall(window, "SetSettingsPanelVisible", true);
            ((RadioButton)window.FindName("ControlSettingsTab")).IsChecked = true;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(450));
            window.UpdateLayout();
            button.BringIntoView();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            window.UpdateLayout();
            var viewport = (ScrollViewer)window.FindName("ControlSettingsScrollViewer");
            var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
            InteractionAssert(button.IsVisible && bounds.Left >= -1 && bounds.Top >= -1 &&
                bounds.Right <= viewport.ViewportWidth + 1 && bounds.Bottom <= viewport.ViewportHeight + 1,
                "The keyboard setting screenshot did not show the button inside the control settings viewport.");
            SaveWindowRender(window, Path.Combine(output, "native-keyboard-control-settings.png"));
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                defaultShown = true, savedShown = store.Load().ShowNativeKeyboard,
                replacedHostRejected = true, recoveringSenderRetired = true,
                attemptedSenderRetired = true, readyFallbackRejected = true,
                wiredRequests = fixtures[0].Requested, wirelessRequests = fixtures[1].Requested,
                scope = "Real ViewModel and bridge RPC frames with correlated fixture replies; no real device is controlled.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS native keyboard preference: defaults/button/persistence, wired/wireless RPC, ready/host replacement races, attempted/recovering sender retirement and failed-route/save rollback.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            foreach (var fixture in fixtures) fixture.Dispose();
            foreach (var fixture in retiredFixtures) fixture.Dispose();
            CloseWorkspaceTestWindow(window);
            app.Shutdown();
        }

        void AssertKeyboardError(bool expected)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            var status = (TextBlock)window.FindName("NativeKeyboardError");
            InteractionAssert(vm.NativeKeyboardHasError == expected &&
                status.Visibility == (expected ? Visibility.Visible : Visibility.Collapsed),
                expected ? "A keyboard setting error was hidden." : "Normal keyboard state left a success or description row visible.");
        }

        static void WaitForPendingRequest(NativeKeyboardBridgeFixture bridge)
        {
            var watch = Stopwatch.StartNew();
            while (!bridge.HasPendingRequest && watch.Elapsed < TimeSpan.FromSeconds(2))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert(bridge.HasPendingRequest,
                "The keyboard transaction did not advance to the second peer.");
        }

        static bool Finish(Task<bool> task, List<NativeKeyboardBridgeFixture>? bridges = null)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (bridges is not null) foreach (var bridge in bridges) bridge.Acknowledge();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            }
            InteractionAssert(task.IsCompleted, "Keyboard setting fixture did not complete in five seconds.");
            return task.GetAwaiter().GetResult();
        }
    }

    private sealed class NativeKeyboardBridgeFixture : IDisposable
    {
        internal readonly DeviceControlSession Control;
        internal readonly UsbTouchBridgeHost Host = new();
        internal readonly DirectUsbInputBridge Inner;
        internal readonly List<bool> Requested = [];
        internal bool RejectNext;
        internal bool ActualVisible = true;
        internal bool HasPendingRequest => _bytes.Length > _offset;
        private readonly MemoryStream _bytes = new();
        private readonly StreamWriter _writer;
        private int _offset;

        internal NativeKeyboardBridgeFixture(DeviceControlSession control, bool wireless)
        {
            Control = control;
            _writer = new StreamWriter(_bytes, leaveOpen: true);
            Inner = (DirectUsbInputBridge)KeyboardField(Host, "_bridge");
            SetKeyboardField(Inner, "_stdin", _writer);
            SetKeyboardField(Inner, "<IsReady>k__BackingField", true);
            SetKeyboardField(Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", true);
            if (wireless)
            {
                control.WirelessBridge = Host;
                control.WirelessEnabled = control.WirelessConnected = true;
            }
            else
            {
                control.WiredBridge = Host;
                control.WiredEnabled = control.WiredConnected = true;
            }
        }

        internal void Acknowledge()
        {
            var bytes = _bytes.ToArray();
            if (bytes.Length < _offset + 4) return;
            var size = BitConverter.ToInt32(bytes, _offset);
            if (bytes.Length < _offset + 4 + size) return;
            using var document = JsonDocument.Parse(bytes.AsMemory(_offset + 4, size));
            _offset += 4 + size;
            var request = document.RootElement;
            InteractionAssert(request.GetProperty("kind").GetString() == "native_keyboard_visibility",
                "Native keyboard setting used a different input channel.");
            Requested.Add(request.GetProperty("visible").GetBoolean());
            if (!RejectNext) ActualVisible = request.GetProperty("visible").GetBoolean();
            var reply = JsonSerializer.SerializeToElement(new
            {
                requestId = request.GetProperty("requestId").GetString(),
                generation = request.GetProperty("generation").GetInt64(), success = !RejectNext,
                visible = request.GetProperty("visible").GetBoolean(),
            });
            RejectNext = false;
            KeyboardCall(Inner, "CompleteNativeKeyboard", reply);
        }

        internal void MarkRecovering()
        {
            // Model a live process receiving recovery_triggered. A real Stop
            // must close this fixture's stdin and retire its lifecycle owner.
            SetKeyboardField(Host, "_started", 1);
            SetKeyboardField(Host, "_cleanupPending", true);
            SetKeyboardField(Host, "<State>k__BackingField", ReverseControlState.Recovering);
            SetKeyboardField(Inner, "<IsReady>k__BackingField", false);
            SetKeyboardField(Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", false);
            KeyboardCall(Inner, "CancelNativeKeyboard");
            Control.WiredConnected = Control.WirelessConnected = false;
        }

        internal void MarkRecovered(bool visible, bool supported)
        {
            SetKeyboardField(Inner, "_readyGeneration", (long)KeyboardField(Inner, "_readyGeneration") + 1);
            SetKeyboardField(Inner, "_wireGeneration", (long)KeyboardField(Inner, "_wireGeneration") + 1);
            SetKeyboardField(Inner, "<IsReady>k__BackingField", true);
            SetKeyboardField(Inner, "<SupportsNativeKeyboardVisibility>k__BackingField", supported);
            SetKeyboardField(Host, "<State>k__BackingField", ReverseControlState.Ready);
            Host.NativeKeyboardVisible = visible;
            ActualVisible = visible;
            Control.WiredConnected = Control.WiredEnabled;
            Control.WirelessConnected = Control.WirelessEnabled;
        }

        public void Dispose()
        {
            Control.WiredEnabled = Control.WiredConnected = false;
            Control.WirelessEnabled = Control.WirelessConnected = false;
            Control.WiredBridge = Control.WirelessBridge = null;
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _writer.Dispose();
            _bytes.Dispose();
        }
    }
}
