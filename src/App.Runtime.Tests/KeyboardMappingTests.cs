using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static readonly MappedKey MappingTestKey = new(0x4A, 0x24, false);
    private static KeyboardMappingEntry MappingEntry(MappedTouchAction action = MappedTouchAction.Tap) =>
        new() { Key = MappingTestKey, Action = action, DurationMs = 64, IntervalMs = 40 };
    private static void MappingAssert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static int RunKeyboardMappingTests(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            TestMappingConfiguration(output);
            TestMappingKeys();
            TestMappingCaptureTransactions();
            TestMappingWindowsKeyChords();
            TestMappingVisualCoordinates();
            AwaitMapping(TestMappingGesturesAsync());
            AwaitMapping(TestMappingHoldUntilReleaseAsync());
            AwaitMapping(TestMappingFrameCancellationAsync());
            TestMappingRoutes(app);
            TestMappingHookAndEntry(app);
            TestMappingNativePreviewFocus(app);
            TestMappingFocusGuard();
            TestMappingWindows(app, output);
            TestMappingOverlayInteraction(app, output);
            TestMappingPickPreservesDuration();
            TestMappingSurfaceLifecycle(app);
            Console.WriteLine("PASS keyboard mapping: configuration, key families/chords/repeat, all gestures, cancellation, USB/wireless framed transport, device switching/reconnect, orientation, editor CRUD/conflicts, three languages, both themes, resized windows.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void AwaitMapping(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(15))
            AdvanceDispatcher(TimeSpan.FromMilliseconds(5));
        MappingAssert(task.IsCompleted, "Mapping test timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void TestMappingConfiguration(string output)
    {
        var path = Path.Combine(output, "settings-roundtrip.json");
        var store = new UpdateSettingsStore(path);
        var initial = new UpdateSettingsStore(Path.Combine(output, $"missing-{Guid.NewGuid():N}.json")).Load();
        MappingAssert(!initial.KeyboardMapping.Enabled && initial.KeyboardMapping.Mappings.Count == 0,
            "Fresh installs must have no default bindings and no capture.");
        initial.Language = "zh-HK";
        initial.KeyboardMapping = new() { Enabled = true, SuppressOriginalKey = true,
            Mappings = Enum.GetValues<MappedTouchAction>().Select((a, i) => MappingEntry(a) with
                { Key = new MappedKey(0x41 + i, 0x10 + i, false) }).ToList() };
        store.Save(initial);
        var loaded = store.Load();
        MappingAssert(loaded.KeyboardMapping.Enabled && loaded.KeyboardMapping.SuppressOriginalKey &&
            loaded.KeyboardMapping.Mappings.SequenceEqual(initial.KeyboardMapping.Mappings), "Mappings did not survive restart.");
        var clone = loaded.Clone();
        clone.KeyboardMapping.Mappings.Clear();
        MappingAssert(loaded.KeyboardMapping.Mappings.Count == Enum.GetValues<MappedTouchAction>().Length, "Settings rollback snapshot aliases the mapping list.");
        var valid = JsonSerializer.Serialize(MappingEntry());
        var corruptCases = new[]
        {
            "null", "42", "{\"Mappings\":null}", "{\"SchemaVersion\":9,\"Enabled\":true}",
            "{\"Enabled\":true,\"Mappings\":[" + valid + "," + valid + ",null,{}]}",
            "{\"Mappings\":[{\"Key\":{\"VirtualKey\":65,\"ScanCode\":30},\"Action\":999}]}"
        };
        foreach (var json in corruptCases)
        {
            var result = JsonSerializer.Deserialize<UpdateSettings>("{\"Language\":\"zh-HK\",\"KeyboardMapping\":" + json + "}")!;
            MappingAssert(result.Language == "zh-HK" && !result.KeyboardMapping.Enabled &&
                result.KeyboardMapping.HadInvalidEntries, "Corrupt mapping reset unrelated settings or stayed enabled.");
            MappingAssert(result.KeyboardMapping.SchemaVersion == 1, "Recovered mappings retained an unsupported schema version.");
        }
        MappingAssert((MappingEntry() with { X = double.NaN }).Validate() is not null, "NaN was accepted.");
        MappingAssert((MappingEntry(MappedTouchAction.SwipeUp) with { Y = .1, Distance = .2 }).Validate() is not null,
            "An out-of-bounds swipe was silently clipped.");
        MappingAssert((MappingEntry(MappedTouchAction.Swipe) with { EndX = .5, EndY = .5 }).Validate() is not null,
            "Zero-length swipe was accepted.");
        Console.WriteLine("PASS mapping settings roundtrip, clone isolation, corrupt/duplicate/unknown-schema data and validation.");
    }

    private static void TestMappingKeys()
    {
        var families = Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10))
            .Concat(Enumerable.Range(0x70, 12)).Concat(Enumerable.Range(0x60, 10))
            .Concat(new[] { 0x20, 0x0D, 0x09, 0x1B, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,
                0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F, 0xBA, 0x13 }).ToArray();
        foreach (var vk in families)
        {
            var key = new MappedKey(vk, vk, false);
            MappingAssert(key.Validate() is null, $"Key family rejected: {vk:X}");
            var state = new KeyboardMappingKeyState();
            var mapping = MappingEntry() with { Key = key };
            var down = state.Process(key, true, false, true, false, true, [mapping]);
            var repeat = state.Process(key, true, false, true, false, true, [mapping]);
            var up = state.Process(key, false, false, true, false, true, [mapping]);
            MappingAssert((key.IsModifier ? up.Mapping : down.Mapping) == mapping && repeat.Mapping is null,
                $"Press/release/repeat wrong for {vk:X}");
            MappingAssert(key.IsModifier ? !down.Suppress && !up.Suppress : down.Suppress && up.Suppress,
                "Original Windows transitions were not paired.");
        }
        var ctrl = new MappedKey(0xA2, 0x1D, false);
        var state2 = new KeyboardMappingKeyState();
        var controlMapping = MappingEntry() with { Key = ctrl };
        state2.Process(ctrl, true, false, true, false, false, [controlMapping]);
        state2.Process(MappingTestKey, true, false, true, true, false, [controlMapping]);
        state2.Process(MappingTestKey, false, false, true, true, false, [controlMapping]);
        MappingAssert(state2.Process(ctrl, false, false, true, false, false, [controlMapping]).Mapping is null,
            "Ctrl+key fired a standalone Ctrl mapping.");
        var entry = MappingEntry();
        foreach (var flags in new[] { (Allowed: false, Modifiers: false, Injected: false),
            (Allowed: true, Modifiers: true, Injected: false), (Allowed: true, Modifiers: false, Injected: true) })
        {
            var result = new KeyboardMappingKeyState().Process(MappingTestKey, true, flags.Injected,
                flags.Allowed, flags.Modifiers, true, [entry]);
            MappingAssert(result.Mapping is null && !result.Suppress, "Paused, chord or injected input was mapped.");
        }
        MappingAssert(MappingTestKey.SamePhysicalKey(MappingTestKey with { VirtualKey = 0x5A }), "Layout changed physical binding.");
        MappingAssert(!new MappedKey(0x0D, 0x1C, false).SamePhysicalKey(new(0x0D, 0x1C, true)), "Numpad Enter collided.");
        MappingAssert(new MappedKey(0x5B, 0x5B, true).Validate() is null &&
            new MappedKey(0x5C, 0x5C, true).Validate() is null, "Left/right Win capture was rejected.");
        MappingAssert(KeyboardMappingKeys.Conflict(new(0x78, 0x43, false), [KeyboardShortcut.Default]) is not null,
            "F9 shortcut collision was missed.");
        MappingAssert(KeyboardMappingKeys.Conflict(new(0x7B, 0x58, false), []) is null, "F12 cannot be mapped.");
        MappingAssert(KeyboardMappingKeys.Conflict(new(0x7A, 0x57, false), []) is not null, "Fullscreen collision missed.");
        Console.WriteLine($"PASS {families.Length} key cases, physical layout identity, extended keys, modifiers, system keys, conflict and repeat policy.");
    }

    private static async Task TestMappingGesturesAsync()
    {
        using var executor = new KeyboardMappingExecutor();
        foreach (var action in Enum.GetValues<MappedTouchAction>().Where(a => a != MappedTouchAction.HoldUntilRelease))
        {
            var samples = new List<(string Action, double X, double Y, long Ms)>();
            var clock = Stopwatch.StartNew();
            var route = new MappedTouchRoute("test", () => true, (kind, x, y, _) =>
            { samples.Add((kind, x, y, clock.ElapsedMilliseconds)); return Task.CompletedTask; }, (x, y) => (x, y));
            var entry = MappingEntry(action);
            MappingAssert(await executor.ExecuteAsync(entry, route), "Gesture was not executed.");
            MappingAssert(samples.First().Action == "down" && samples.Last().Action == "up", "Unbalanced contact.");
            MappingAssert(samples.Count(p => p.Action == "down") == (action == MappedTouchAction.DoubleTap ? 2 : 1),
                "Wrong tap count.");
            if (entry.IsSwipe)
                MappingAssert(samples.Count(p => p.Action == "move") >= 2 &&
                    Math.Abs(samples.Last().X - entry.EndPoint.X) < 1e-6 &&
                    Math.Abs(samples.Last().Y - entry.EndPoint.Y) < 1e-6, "Swipe endpoint/direction wrong.");
            if (entry.IsSwipe || action == MappedTouchAction.LongPress)
                MappingAssert(samples.Last().Ms >= entry.DurationMs - 8, "Gesture duration was ignored.");
            if (action == MappedTouchAction.DoubleTap)
                MappingAssert(samples[2].Ms - samples[1].Ms >= entry.IntervalMs - 8, "Double-tap interval ignored.");
        }
        foreach (var cancel in new[] { true, false })
        {
            var events = new List<string>();
            var current = true;
            var route = new MappedTouchRoute("old", () => current, (action, _, _, _) =>
            { events.Add(action); return Task.CompletedTask; }, (x, y) => (x, y));
            var heldMapping = MappingEntry(MappedTouchAction.LongPress) with { DurationMs = 1000 };
            var first = executor.ExecuteAsync(heldMapping, route);
            MappingAssert(!await executor.ExecuteAsync(heldMapping, route), "The same mapping overlapped itself.");
            if (cancel) executor.Cancel(); else current = false;
            try { await first; throw new Exception("Cancelled gesture completed."); }
            catch (OperationCanceledException) { }
            MappingAssert(events.SequenceEqual(new[] { "down", "up" }), "Cancellation failed to release old contact.");
        }
        var fails = new List<string>();
        try
        {
            await executor.ExecuteAsync(MappingEntry(), new("failure", () => true, (action, _, _, _) =>
            { fails.Add(action); return action == "down" ? Task.FromException(new IOException()) : Task.CompletedTask; }, (x, y) => (x, y)));
            throw new Exception("Send failure was hidden.");
        }
        catch (IOException) { }
        MappingAssert(fails.SequenceEqual(new[] { "down", "up" }) && !executor.IsBusy, "Failed down left contact or executor stuck.");
        Console.WriteLine("PASS eight gesture actions, timed motion, long press, double tap, bounded input, cancellation and failed-send cleanup.");
    }

    private static void TestMappingRoutes(App app)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(host, "<State>k__BackingField", ReverseControlState.Ready);
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        DeviceViewModel Device(string id, string model) => (DeviceViewModel)ctor.Invoke([
            id, id, model, "18.0", "USB", "", Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        var phone = Device("mapping-iphone", "iPhone15,2");
        var pad = Device("mapping-ipad", "iPad14,3");
        var controls = (IDictionary)KeyboardField(vm, "_deviceControls");
        var control = new DeviceControlSession(phone.Udid) { AppleUdid = phone.Udid, WiredBridge = host,
            WiredConnected = true, WiredEnabled = true };
        controls[phone.Udid] = control;
        SetKeyboardField(vm, "_selectedDevice", phone);
        try
        {
            foreach (var mode in new[] { ReverseControlMode.Usb, ReverseControlMode.Wireless })
            {
                control.WiredEnabled = mode == ReverseControlMode.Usb;
                control.WirelessEnabled = mode == ReverseControlMode.Wireless;
                control.WirelessConnected = control.WirelessEnabled;
                control.WirelessBridge = control.WirelessEnabled ? host : null;
                control.Router.Begin(phone.Udid, mode);
                AwaitMapping(TestFivePointMappingAsync(vm, packets, mode.ToString()));
                foreach (var dimensions in new[] { (1170u, 2532u), (1668u, 2388u), (2532u, 1170u), (2388u, 1668u) })
                foreach (var turn in new[] { 0, 1, 3 })
                {
                    packets.SetLength(0);
                    var route = vm.CaptureMappingRoute(() => true, (x, y) =>
                        BluetoothMouseOrientationMapper.MapNormalized(x, y, dimensions.Item1, dimensions.Item2,
                            turn, BluetoothMouseDirection.Up, BluetoothMouseDirection.Right, false, false))!;
                    using var executor = new KeyboardMappingExecutor();
                    AwaitMapping(executor.ExecuteAsync(MappingEntry() with { X = .2, Y = .7 }, route));
                    var frames = MappingFrames(packets);
                    MappingAssert(frames.Count == 2 && frames.All(f => f.GetProperty("points")[0].GetProperty("pointerId").GetInt32() > 1) &&
                        frames.Select(f => f.GetProperty("points")[0].GetProperty("pointerId").GetInt32()).Distinct().Count() == 1,
                        "Mapping bypassed touch framing or collided with mouse pointer ID.");
                    // Independent expected positions for the existing preview
                    // rotation followed by per-orientation direction mapping.
                    (double X, double Y) expected = (dimensions.Item1 < dimensions.Item2, turn) switch
                    {
                        (true, 0) => (.2, .7), (true, 1) => (.8, .3), (true, 3) => (.2, .7),
                        (false, 0) => (.3, .2), (false, 1) => (.3, .2), _ => (.7, .8),
                    };
                    var point = frames[0].GetProperty("points")[0];
                    MappingAssert(Math.Abs(point.GetProperty("normalizedX").GetDouble() - expected.X) < 1e-6 &&
                        Math.Abs(point.GetProperty("normalizedY").GetDouble() - expected.Y) < 1e-6, "Existing coordinate converter not applied.");
                }
                var captured = vm.CaptureMappingRoute(() => true, (x, y) => (x, y))!;
                SetKeyboardField(vm, "_selectedDevice", pad);
                MappingAssert(!captured.IsCurrent(), "Old target survived device switch.");
                var before = packets.Length;
                try { AwaitMapping(captured.SendAsync("down", .5, .5, default)); throw new Exception("Old target accepted down."); }
                catch (OperationCanceledException) { }
                MappingAssert(packets.Length == before, "Switch sent a touch to the old device.");
                // Cleanup after selection changes must release only the captured session.
                AwaitMapping(captured.SendAsync("up", .5, .5, default));
                SetKeyboardField(vm, "_selectedDevice", phone);
                control.Router.Stop();
                control.Router.Begin(phone.Udid, mode);
                before = packets.Length;
                try { AwaitMapping(captured.SendAsync("up", .5, .5, default)); throw new Exception("Reconnect accepted old release."); }
                catch (OperationCanceledException) { }
                MappingAssert(packets.Length == before, "Old release affected reconnected device.");
                var restored = vm.CaptureMappingRoute(() => true, (x, y) => (x, y));
                MappingAssert(restored?.IsCurrent() == true, "Reconnect did not restore fresh route.");
                // Exercise the real bridge writer lock with a release queued
                // before recovery; generation is rechecked inside that lock.
                var gate = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
                gate.Wait();
                var stale = restored!.SendAsync("up", .5, .5, default);
                SetKeyboardField(bridge, "_readyGeneration", bridge.InputGeneration + 1);
                gate.Release();
                try { AwaitMapping(stale); throw new Exception("Writer accepted stale generation."); }
                catch (OperationCanceledException) { }
                MappingAssert(packets.Length == before, "Queued stale release escaped the writer lock.");

                foreach (var action in new[] { "down", "move" })
                {
                    var allowed = true;
                    var queuedRoute = vm.CaptureMappingRoute(() => allowed, (x, y) => (x, y))!;
                    before = packets.Length;
                    gate.Wait();
                    var queuedInput = queuedRoute.SendAsync(action, .5, .5, default);
                    allowed = false; // Focus/geometry becomes invalid while waiting for the writer.
                    gate.Release();
                    try { AwaitMapping(queuedInput); throw new Exception("Expired mapping did not cancel."); }
                    catch (OperationCanceledException) { }
                    MappingAssert(packets.Length == before, $"Queued {action} escaped after focus/geometry invalidation.");
                    AwaitMapping(queuedRoute.SendAsync("up", .5, .5, default));
                    MappingAssert(MappingFrames(packets).Last().GetProperty("points")[0].GetProperty("action").GetString() == "up",
                        "Focus loss prevented the captured session's cleanup release.");
                }
            }
            SetKeyboardField(vm, "_selectedDevice", null);
            MappingAssert(vm.GetMappingTargetStatus() == "MappingNoDevice" && vm.CaptureMappingRoute(() => true, (x, y) => (x, y)) is null,
                "No-device state can send input.");
            SetKeyboardField(vm, "_selectedDevice", phone);
            SetKeyboardField(vm, "_bluetoothControlEnabled", true);
            SetKeyboardField(vm, "_bluetoothControlDeviceUdid", phone.Udid);
            MappingAssert(vm.GetMappingTargetStatus() == "MappingBluetoothUnsupported", "Relative Bluetooth advertised absolute touch.");
            SetKeyboardField(main, "_mappingSettings", new KeyboardMappingSettings { Enabled = true, Mappings = [MappingEntry()] });
            KeyboardCall(main, "TryEnterKeyboardMappingInputMode");
            MappingAssert(!(bool)KeyboardCall(main, "TryEnterDirectKeyboardInputMode")!,
                "Unsupported mapping must still own the keyboard; it cannot leak to direct Bluetooth.");
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            SetKeyboardField(vm, "_sourceVideoWidth", 1170u);
            SetKeyboardField(vm, "_sourceVideoHeight", 2532u);
            MappingAssert(vm.GetMappingTargetStatus() == "MappingGeometryUnavailable",
                "A stopped preview with stale dimensions advertised executable mapping input.");
            MappingAssert(!(bool)KeyboardCall(main, "TryEnterDirectKeyboardInputMode")!,
                "Mapping ownership must exclude every direct preview route.");
            Console.WriteLine("PASS USB/wireless framed packets, iPhone/iPad dimensions, portrait/left/right rotation, selection changes, reconnect generations and Bluetooth capability gate.");
        }
        finally
        {
            SetKeyboardField(vm, "_bluetoothControlEnabled", false);
            controls.Clear();
            SetKeyboardField(bridge, "_stdin", null);
            SetKeyboardField(bridge, "<IsReady>k__BackingField", false);
            CloseWorkspaceTestWindow(main);
        }
    }

    private static List<JsonElement> MappingFrames(MemoryStream stream)
    {
        var result = new List<JsonElement>();
        using var copy = new MemoryStream(stream.ToArray());
        using var reader = new BinaryReader(copy);
        while (copy.Position < copy.Length)
        {
            using var json = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
            result.Add(json.RootElement.Clone());
        }
        return result;
    }

    private static void TestMappingWindows(App app, string output)
    {
        var languageMethod = typeof(LocalizationService).GetMethod("ApplyLanguage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            languageMethod.Invoke(null, [language, false, false]);
            ThemeService.Apply(theme);
            var settings = new KeyboardMappingSettings { Mappings = [MappingEntry(), MappingEntry(MappedTouchAction.SwipeUp) with
                { Key = new MappedKey(0x57, 0x11, false) }] };
            var manager = new KeyboardMappingWindow(settings, next => { settings = next.Clone(); return null; },
                _ => null, _ => null, () => { }, () => "MappingNoDevice") { ShowInTaskbar = false };
            app.MainWindow = manager;
            manager.Show();
            try
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(260));
                SaveWindowRender(manager, Path.Combine(output, $"mapping-{language}-{theme}.png"));
                manager.Width = 560; manager.Height = 500;
                manager.UpdateLayout();
                CheckMappingButtons(manager);
                SaveWindowRender(manager, Path.Combine(output, $"mapping-small-{language}-{theme}.png"));
                var toggle = (CheckBox)manager.FindName("EnabledBox");
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MappingAssert(settings.Enabled, "Manager enable did not save.");
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MappingAssert(!settings.Enabled, "Manager disable did not save.");
                Action<MappedKey>? capture = null;
                KeyboardMappingEntry? saved = null;
                Guid? replaced = null;
                var editor = new KeyboardMappingEditorWindow(null, settings.Mappings, _ => null,
                    callback => { capture = callback; return null; }, () => { },
                    (entry, replace) => { saved = entry; replaced = replace; return null; },
                    (entry, completed) => { completed(entry with { X = .2, Y = .7, EndX = .8, EndY = .3,
                        DeviceCoordinates = true }); return null; })
                    { Owner = manager, ShowInTaskbar = false };
                editor.Show();
                try
                {
                    MappingAssert(capture is not null && !((Button)editor.FindName("SaveButton")).IsEnabled,
                        "Key capture did not start automatically.");
                    capture!(MappingTestKey);
                    MappingAssert(((WrapPanel)editor.FindName("ConflictPanel")).Visibility == Visibility.Visible &&
                        !editor.Wizard.CanNext && saved is null, "Duplicate mapping did not block the key step.");
                    SaveWindowRender(editor, Path.Combine(output, $"mapping-conflict-{language}-{theme}.png"));
                    KeyboardCall(editor, "OnReplaceClick", editor, new RoutedEventArgs());
                    WizardNext(editor);
                    var actionBox = (ListBox)editor.FindName("ActionBox");
                    actionBox.SelectedValue = MappedTouchAction.Swipe;
                    MappingAssert(actionBox.Items.Count == 5, "Existing action types were not retained.");
                    WizardNext(editor);
                    MappingAssert(((ComboBox)editor.FindName("DirectionBox")).Items.Count == 5,
                        "Swipe directions were not grouped under one action.");
                    WizardNext(editor);
                    MappingAssert(!((Button)editor.FindName("SaveButton")).IsEnabled,
                        "Capture bypassed required visual picking.");
                    ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    MappingAssert(editor.State == MappingEditorState.MappingReady, "Pick did not finish the editing transaction.");
                    WizardNext(editor);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(260));
                    SaveWindowRender(editor, Path.Combine(output, $"mapping-editor-{language}-{theme}.png"));
                    editor.Width = 480; editor.Height = 560; editor.UpdateLayout(); CheckMappingButtons(editor);
                    WizardNext(editor);
                    MappingAssert(saved is { X: .2, Y: .7, EndX: .8, EndY: .3, Action: MappedTouchAction.Swipe } &&
                        replaced == settings.Mappings[0].Id, "Replace did not preserve the edited mapping and conflict ID.");
                }
                finally { DiscardWizard(editor); }
                var directionalEditor = new KeyboardMappingEditorWindow(settings.Mappings[1], settings.Mappings,
                    _ => null, _ => null, () => { }, (entry, _) => { saved = entry; return null; },
                    (entry, completed) => { completed(entry with { X = .5, Y = .3, EndX = .5, EndY = .7,
                        DeviceCoordinates = true }); return null; }) { Owner = manager, ShowInTaskbar = false };
                directionalEditor.Show();
                try
                {
                    MappingAssert(((ListBox)directionalEditor.FindName("ActionBox")).SelectedValue is MappedTouchAction.Swipe &&
                        ((ComboBox)directionalEditor.FindName("DirectionBox")).SelectedValue is MappedTouchAction.SwipeUp,
                        "Existing directional swipe did not restore its direction.");
                    WizardTo(directionalEditor, MappingWizardStep.Parameters);
                    ((ComboBox)directionalEditor.FindName("DirectionBox")).SelectedValue = MappedTouchAction.SwipeDown;
                    MappingAssert(!directionalEditor.Wizard.HasPosition, "Changing direction reused old coordinates.");
                    WizardNext(directionalEditor);
                    ((Button)directionalEditor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WizardNext(directionalEditor); WizardNext(directionalEditor);
                    MappingAssert(saved?.Action == MappedTouchAction.SwipeDown, "Selected swipe direction was not saved.");
                }
                finally { DiscardWizard(directionalEditor); }
                var originalEditor = new KeyboardMappingEditorWindow(null, settings.Mappings, _ => null,
                    callback => { capture = callback; return null; }, () => { },
                    (entry, replace) => { saved = entry; replaced = replace; return null; })
                    { Owner = manager, ShowInTaskbar = false };
                originalEditor.Show();
                try
                {
                    capture!(MappingTestKey);
                    KeyboardCall(originalEditor, "OnEditOriginalClick", originalEditor, new RoutedEventArgs());
                    WizardNext(originalEditor);
                    ((ListBox)originalEditor.FindName("ActionBox")).SelectedValue = MappedTouchAction.HoldUntilRelease;
                    MappingAssert(originalEditor.Wizard.Steps.Count == 4 && originalEditor.Wizard.HasPosition,
                        "Hold unnecessarily requires parameters or discards the selected point.");
                    WizardTo(originalEditor, MappingWizardStep.Confirmation); WizardNext(originalEditor);
                    MappingAssert(saved?.Id == settings.Mappings[0].Id && saved.Action == MappedTouchAction.HoldUntilRelease && replaced is null,
                        "Edit-original did not retain original identity.");
                }
                finally { DiscardWizard(originalEditor); }
                var row = manager.Rows[0];
                var rowToggle = new CheckBox { DataContext = row, IsChecked = false };
                KeyboardCall(manager, "OnRowEnabledClick", rowToggle, new RoutedEventArgs());
                MappingAssert(!settings.Mappings[0].Enabled, "Per-row disable was not persisted.");
                manager.Dispatcher.BeginInvoke(() =>
                {
                    var prompt = app.Windows.OfType<AppPromptWindow>().Single();
                    KeyboardCall(prompt, "OnConfirmClick", prompt, new RoutedEventArgs());
                });
                KeyboardCall(manager, "OnDeleteClick", new Button { DataContext = manager.Rows[0] }, new RoutedEventArgs());
                MappingAssert(settings.Mappings.Count == 1 && manager.Rows.Count == 1, "Delete was not saved/reflected in the list.");
            }
            finally { manager.Close(); }
        }
        Console.WriteLine("PASS real WPF manager/editor, capture, toggle persistence, conflict cancel/replace, themes/languages and resizing; PNG renders saved.");
    }

    private static void TestMappingHookAndEntry(App app)
    {
        var snapshot = app.UpdateSettings.Clone();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var vm = (MainViewModel)KeyboardField(main, "_viewModel");
            vm.ManageKeyboardMappingCommand.Execute(null);
            var manager = app.Windows.OfType<KeyboardMappingWindow>().Single();
            MappingAssert(manager.IsVisible, "Settings command did not open mapping management.");
            manager.Dispatcher.BeginInvoke(() =>
            {
                var editor = app.Windows.OfType<KeyboardMappingEditorWindow>().Single();
                if (!editor.Wizard.Capturing)
                    ((Button)editor.FindName("CaptureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MappingAssert((nint)KeyboardField(main, "_keyboardHook") != 0, "Recording did not install the shared Windows hook.");
                // Exercise the actual native callback with an in-process packet.
                // No SendInput: injected input is intentionally rejected.
                var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
                try
                {
                    for (var offset = 0; offset < 24; offset += 4) System.Runtime.InteropServices.Marshal.WriteInt32(data, offset, 0);
                    System.Runtime.InteropServices.Marshal.WriteInt32(data, 0, MappingTestKey.VirtualKey);
                    System.Runtime.InteropServices.Marshal.WriteInt32(data, 4, MappingTestKey.ScanCode);
                    MappingAssert((nint)KeyboardCall(main, "KeyboardHookProcedure", 0, (nint)0x100, data)! == 1,
                        "Captured key down was not consumed.");
                    MappingAssert((nint)KeyboardCall(main, "KeyboardHookProcedure", 0, (nint)0x100, data)! == 1,
                        "Capture leaked an autorepeat to the editor.");
                    MappingAssert((nint)KeyboardCall(main, "KeyboardHookProcedure", 0, (nint)0x101, data)! == 1,
                        "Captured key release was not paired.");
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data); }
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                MappingAssert(((Button)editor.FindName("CaptureButton")).Content.ToString()!.Contains("J"),
                    "Native key data did not update the editor.");
                // Transport-free test of saving a confirmed position. Real
                // overlay mouse selection is covered by the interaction probe.
                KeyboardCall(editor, "LoadEntry", MappingEntry());
                // The hook test has no device frame. Navigation/preview gating
                // is covered separately with a deterministic frame provider.
                while (editor.Wizard.Current != MappingWizardStep.Confirmation) editor.Wizard.MoveNext();
                ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            KeyboardCall(manager, "OnAddClick", manager, new RoutedEventArgs());
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            MappingAssert(manager.Rows.Count == 1 && app.UpdateSettings.KeyboardMapping.Mappings.Single().Key == MappingTestKey,
                "Add did not persist real captured scan/VK data.");
            // Leaving mapping ownership drains its asynchronous handoff before
            // the shared hook can be removed.
            var hookDrain = Stopwatch.StartNew();
            while ((nint)KeyboardField(main, "_keyboardHook") != 0 && hookDrain.Elapsed < TimeSpan.FromSeconds(2))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
            var capture = (KeyboardMappingCapture)KeyboardField(main, "_mappingCapture");
            MappingAssert((nint)KeyboardField(main, "_keyboardHook") == 0,
                $"Recording left a hook active while mapping is off: mode={router.Mode}, requested={router.RequestedMode}, retired={router.HasRetiredKeys}, captureHeld={capture.HasHeldKeys}, captureWaiting={capture.Waiting}.");
            var toggle = (CheckBox)manager.FindName("EnabledBox");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MappingAssert((nint)KeyboardField(main, "_keyboardHook") != 0, "Enable did not install the shared hook.");
            MappingAssert((bool)KeyboardCall(main, "MappingFocusAllows")! == false, "Manager did not suspend execution.");
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MappingAssert((nint)KeyboardField(main, "_keyboardHook") == 0, "Disable left keyboard capture active.");
            manager.Close();
            Console.WriteLine("PASS settings entry, actual shared hook install/remove, native callback key recording/repeat/release, add persistence and settings pause.");
        }
        finally { CloseWorkspaceTestWindow(main); app.RestoreUpdateSettings(snapshot); }
    }

    private static void TestMappingFocusGuard()
    {
        var button = new Button { Content = "Keyboard mapping focus test" };
        var text = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(text);
        var window = new Window { Content = panel, Width = 360, Height = 160, ShowInTaskbar = false };
        using var guard = new KeyboardMappingFocusGuard();
        bool WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            return condition();
        }
        try
        {
            window.Show();
            window.Activate();
            button.Focus();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            MappingAssert(WaitFor(() => guard.Allows(handle)), "UI Automation did not allow known non-editable focus.");
            text.Focus();
            MappingAssert(WaitFor(() => !guard.Allows(handle)), "Text focus did not invalidate the cached mapping permission.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(450));
            MappingAssert(!guard.Allows(handle), "Background focus refresh re-enabled mapping in a text box.");
            button.Focus();
            MappingAssert(WaitFor(() => guard.Allows(handle)), "Mapping focus did not recover after leaving text input.");
            MappingAssert(!guard.Allows(0), "Unknown foreground was allowed.");
            Console.WriteLine("PASS real UI Automation focus guard, editable-focus invalidation, non-editable recovery and unknown-focus protection.");
        }
        finally { window.Close(); }
    }

    private static void CheckMappingButtons(Window window)
    {
        window.UpdateLayout();
        foreach (var button in ComponentVisuals<Button>(window).Where(b => b.IsVisible))
        {
            MappingAssert(button.ActualWidth > 0 && button.ActualHeight > 0, "Invisible mapping action button.");
            foreach (var text in ComponentVisuals<TextBlock>(button))
            {
                if (text.TextWrapping == TextWrapping.Wrap) continue;
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                MappingAssert(text.DesiredSize.Width <= button.ActualWidth + 2,
                    $"Button text clipped: {text.Text}, desired {text.DesiredSize.Width}, button {button.ActualWidth}");
            }
        }
    }
}
