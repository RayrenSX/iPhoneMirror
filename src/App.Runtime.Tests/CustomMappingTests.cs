using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static KeyboardMappingEntry JoystickEntry() => new()
    {
        Key = new(0x57, 0x11, false), Action = MappedTouchAction.Joystick, DeviceCoordinates = true,
        Joystick = new() { Down = new(0x53, 0x1F, false), Left = new(0x41, 0x1E, false), Right = new(0x44, 0x20, false) },
    };
    private static KeyboardMappingEntry DragEntry() => new()
    {
        InputKind = MappingInputKind.MouseButton, MouseButton = 1, Action = MappedTouchAction.RelativeDrag,
        DeviceCoordinates = true,
    };
    private static int RunCustomMappingTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            TestCustomMappingSettings();
            TestContinuousMappingGeometry();
            AwaitMapping(TestContinuousControlsAsync());
            AwaitMapping(TestMappingAcknowledgementsAsync());
            TestCustomMappingUi(app, output);
            Console.WriteLine("PASS custom mapping: profile migration/isolation, input validation, joystick/drag lifetimes, bounded mouse backlog, physical release, ACK ordering, existing UI integration.");
            return 0;
        }
        finally { app.Shutdown(); }
    }
    private static void TestCustomMappingSettings()
    {
        var joystick = JoystickEntry(); var drag = DragEntry();
        var cycle = new KeyboardMappingEntry { InputKind = MappingInputKind.WheelUp, Action = MappedTouchAction.CycleTargets,
            Targets = [new(.2, .3), new(.7, .8)] };
        var settings = new KeyboardMappingSettings { Enabled = true, Mappings = [joystick, drag, cycle] };
        MappingAssert(settings.Validate() is null, "General controls did not validate.");
        var copy = settings.Clone(); copy.Mappings[2].Targets[0] = new(.9, .9);
        MappingAssert(settings.Mappings[2].Targets[0] == new MappingPoint(.2, .3), "Profile clone aliases targets.");
        var second = settings.Selected.Clone(); second.Id = Guid.NewGuid(); second.Name = "Other application";
        settings.Profiles.Add(second); settings.SelectedProfileId = second.Id;
        var restored = JsonSerializer.Deserialize<KeyboardMappingSettings>(JsonSerializer.Serialize(settings))!;
        MappingAssert(restored.Enabled && restored.Selected.Id == second.Id && restored.Profiles.Count == 2 &&
            restored.Mappings.Zip(settings.Mappings).All(p => p.First.ContentEquals(p.Second)), "Profile roundtrip lost controls or active profile.");
        var legacy = JsonSerializer.Deserialize<KeyboardMappingSettings>("{\"SchemaVersion\":1,\"Enabled\":true,\"Mappings\":[" + JsonSerializer.Serialize(MappingEntry()) + "]}")!;
        MappingAssert(legacy.Enabled && !legacy.HadInvalidEntries && legacy.Profiles.Count == 1 && legacy.Mappings.Count == 1 &&
            legacy.SchemaVersion == 2, "Legacy mappings were not migrated.");
        var damaged = JsonSerializer.Deserialize<KeyboardMappingSettings>("{\"Enabled\":true,\"Mappings\":[" + JsonSerializer.Serialize(MappingEntry()) + ",\"broken\",{\"Joystick\":null}]}")!;
        MappingAssert(!damaged.Enabled && damaged.HadInvalidEntries && damaged.Mappings.Count == 1, "One malformed mapping destroyed valid entries.");
        MappingAssert(joystick.ConflictsWith(MappingEntry() with { Key = joystick.Joystick.Left }), "Direction key conflict was missed.");
        MappingAssert(!drag.ConflictsWith(joystick), "Mouse and keyboard namespaces collided.");
        MappingAssert(!MappingEntry().ConflictsWith(MappingEntry() with { Modifiers = 2 }), "Distinct key chords collided.");
        MappingAssert((cycle with { Action = MappedTouchAction.HoldUntilRelease }).Validate() == "MappingPulseNeedsTimedAction", "Wheel hold accepted.");
        MappingAssert((joystick with { Joystick = joystick.Joystick with { Left = joystick.Key } }).Validate() is not null, "Duplicate direction keys accepted.");
        MappingAssert((drag with { RelativeDrag = drag.RelativeDrag with { SensitivityX = double.NaN } }).Validate() is not null, "NaN sensitivity accepted.");
        var wizard = new KeyboardMappingWizardState(null, [], _ => null);
        wizard.SetKey(joystick.Key!); wizard.MoveNext(); wizard.SelectAction(MappedTouchAction.Joystick); wizard.MoveNext();
        MappingAssert(wizard.Current == MappingWizardStep.Parameters && !wizard.CanNext, "Missing direction keys were accepted.");
        wizard.SetDirectionKey(1, joystick.Joystick.Down!); wizard.SetDirectionKey(2, joystick.Joystick.Left!); wizard.SetDirectionKey(3, joystick.Joystick.Right!);
        MappingAssert(wizard.CanNext && wizard.MoveNext(), "Completed direction keys did not advance.");
        wizard.SetPosition(joystick); wizard.MoveNext();
        MappingAssert(wizard.BuildEntry()?.Validate() is null, "Joystick wizard could not save.");
        wizard = new(cycle, [cycle], _ => null);
        MappingAssert(!wizard.IsDirty && wizard.BuildEntry()?.Targets.Length == 2, "Cycle edit lost positions or started dirty.");
        wizard.RemoveTarget(); MappingAssert(wizard.IsDirty && cycle.Targets.Length == 2, "Cycle draft changed saved entry.");
        wizard = new(null, [], _ => null); wizard.SetKey(MappingTestKey); wizard.MoveNext(); wizard.SelectAction(MappedTouchAction.ReleasePointer);
        wizard.MoveNext(); MappingAssert(wizard.Current == MappingWizardStep.Confirmation && wizard.BuildEntry() is not null, "Release pointer incorrectly requires a phone position.");
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(1500);
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private static void TestContinuousMappingGeometry()
    {
        foreach (var (width, height) in new[] { (1000u, 2000u), (2000u, 1000u), (1536u, 2048u) })
        foreach (var rotation in Enumerable.Range(0, 4))
        foreach (var direction in Enum.GetValues<BluetoothMouseDirection>())
        foreach (var reverseX in new[] { false, true })
        foreach (var reverseY in new[] { false, true })
        foreach (var input in new[] { (.1, 0d), (0d, -.1), (.1 / Math.Sqrt(2), .1 / Math.Sqrt(2)) })
        {
            var offset = BluetoothMouseOrientationMapper.MapShortSideOffset(input.Item1, input.Item2,
                width, height, rotation, direction, direction, reverseX, reverseY);
            var center = BluetoothMouseOrientationMapper.MapNormalized(.5, .5,
                width, height, rotation, direction, direction, reverseX, reverseY);
            var preview = BluetoothMouseOrientationMapper.UnmapNormalized(center.X + offset.X, center.Y + offset.Y,
                width, height, rotation, direction, direction, reverseX, reverseY);
            var shortSide = Math.Min(width, height);
            var dx = (preview.X - .5) * width / shortSide;
            var dy = (preview.Y - .5) * height / shortSide;
            MappingAssert(Math.Abs(dx - input.Item1) < 1e-9 && Math.Abs(dy - input.Item2) < 1e-9,
                "Continuous control radius changed with preview rotation or orientation.");
        }
    }
    private static async Task TestContinuousControlsAsync()
    {
        var engine = new KeyboardMappingContinuousEngine();
        var errors = new ConcurrentQueue<Exception>(); engine.Failed += errors.Enqueue;
        var events = new ConcurrentQueue<(string Action, double X, double Y)>();
        var route = new MappedTouchRoute("device", () => true, (a, x, y, _) => { events.Enqueue((a, x, y)); return Task.CompletedTask; }, (x, y) => (x, y));
        var entry = JoystickEntry();
        engine.Input(entry, 0, true, () => route); engine.Input(entry, 3, true, () => route);
        await UntilAsync(() => events.Any(p => p.Action == "move"));
        var diagonal = events.Last(p => p.Action == "move");
        MappingAssert(Math.Abs(diagonal.X - .5 - .1 / Math.Sqrt(2)) < .0001 &&
            Math.Abs(diagonal.Y - .5 + .1 / Math.Sqrt(2)) < .0001, "Diagonal movement exceeded circular radius.");
        engine.Input(entry, 1, true, () => route); engine.Input(entry, 2, true, () => route);
        await UntilAsync(() => events.Last() is { Action: "move", X: .5, Y: .5 });
        engine.Cancel(); await engine.Completion;
        MappingAssert(events.Last().Action == "up", "Joystick cancel did not release.");

        events.Clear(); var physicalDown = true;
        engine.Input(entry, 0, true, () => route, _ => Volatile.Read(ref physicalDown));
        await UntilAsync(() => events.Any(p => p.Action == "move"));
        physicalDown = false; // no dispatcher/key-up event
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        MappingAssert(events.Last().Action == "up", "Physical release reconciliation depended on UI dispatch.");

        events.Clear(); var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = route with { SendAsync = async (a, x, y, _) =>
        { events.Enqueue((a, x, y)); if (a == "move") { entered.TrySetResult(); await blocked.Task; } } };
        var drag = DragEntry(); engine.Input(drag, 0, true, () => slow);
        engine.Move(100, 0); await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10000; i++) engine.Move(50, 1);
        MappingAssert(events.Count(p => p.Action == "move") == 1, "Mouse packets queued concurrent sends.");
        engine.Input(drag, 0, false, () => slow); blocked.SetResult();
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        MappingAssert(events.Count(p => p.Action == "move") == 1 && events.Last().Action == "up", "Mouse backlog replayed after release.");

        events.Clear(); var toggle = drag with { Id = Guid.NewGuid(), RelativeDrag = drag.RelativeDrag with { Toggle = true } };
        engine.Input(toggle, 0, true, () => route, _ => false); engine.Input(toggle, 0, false, () => route);
        await Task.Delay(60); MappingAssert(engine.HasRelativeDrag, "Toggle stopped on physical release.");
        engine.Input(toggle, 0, true, () => route); await engine.Completion;
        MappingAssert(events.Last().Action == "up", "Toggle off did not release.");

        events.Clear(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowUp = route with { SendAsync = async (a, x, y, _) => { events.Enqueue((a, x, y)); if (a == "up") await release.Task; } };
        engine.Input(entry, 0, true, () => slowUp); await UntilAsync(() => !events.IsEmpty);
        engine.Input(entry, 0, false, () => slowUp); engine.Input(entry, 3, true, () => slowUp);
        release.SetResult(); await UntilAsync(() => events.Count(p => p.Action == "down") == 2);
        engine.Cancel(); await engine.Completion;
        MappingAssert(events.Count(p => p.Action == "up") == 2, "Rapid repress lost its new lifetime.");
        MappingAssert(errors.IsEmpty, string.Join(",", errors.Select(e => e.Message)));
    }
    private static async Task TestMappingAcknowledgementsAsync()
    {
        using var packets = new MemoryStream(); using var writer = new StreamWriter(packets, leaveOpen: true);
        var bridge = new DirectUsbInputBridge();
        SetKeyboardField(bridge, "_stdin", writer); SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        try
        {
            await bridge.SendTouchBatchAsync([new(10, "down", .5, .5)], 0, 0, requireAcknowledgement: true);
            throw new Exception("Old bridge accepted continuous controls without acknowledgement support.");
        }
        catch (InvalidOperationException error) when (error.Message == "MappingBridgeUpdateRequired") { }
        MappingAssert(packets.Length == 0, "Unsupported bridge received a down.");
        SetKeyboardField(bridge, "<SupportsTouchAcknowledgements>k__BackingField", true);
        var down = bridge.SendTouchBatchAsync([new(10, "down", .5, .5)], 0, 0, requireAcceptance: true, requireAcknowledgement: true);
        var frame = MappingFrames(packets).Single(); var seq = frame.GetProperty("seq").GetInt64();
        MappingAssert(!down.IsCompleted, "Writer returned before touch acknowledgement.");
        KeyboardCall(bridge, "HandleLine", JsonSerializer.Serialize(new { @event = "touch_ack", seq, generation = 99 }));
        MappingAssert(!down.IsCompleted, "Stale generation acknowledged new touch.");
        var up = bridge.SendTouchBatchAsync([new(10, "up", .5, .5)], 0, 0);
        MappingAssert(MappingFrames(packets).Count == 1, "Release overtook the unacknowledged down.");
        KeyboardCall(bridge, "HandleLine", JsonSerializer.Serialize(new { @event = "touch_ack", seq, generation = 0 }));
        await Task.WhenAll(down, up);
        MappingAssert(MappingFrames(packets).Count == 2, "Acknowledged down did not drain its release.");
    }
    private static void TestCustomMappingUi(App app, string output)
    {
        foreach (var language in new[] { "zh-CN", "en-US" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            typeof(LocalizationService).GetMethod("ApplyLanguage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [language, false, false]);
            ThemeService.Apply(theme);
            var settings = new KeyboardMappingSettings { Mappings = [JoystickEntry(), DragEntry()] };
            var manager = new KeyboardMappingWindow(settings, next => { settings = next.Clone(); return null; }, _ => null,
                _ => null, () => { }, () => "MappingNoDevice") { ShowInTaskbar = false };
            app.MainWindow = manager; manager.Show();
            try
            {
                KeyboardCall(manager, "OnProfileCommand", new MenuItem { Tag = "copy" }, new RoutedEventArgs());
                MappingAssert(settings.Profiles.Count == 2 && settings.Mappings.Count == 2, "Duplicate profile UI failed.");
                ((TextBox)manager.FindName("ProfileNameBox")).Text = "Custom controls";
                KeyboardCall(manager, "OnProfileCommand", new Button { Tag = "rename" }, new RoutedEventArgs());
                MappingAssert(settings.Selected.Name == "Custom controls", "Rename profile UI failed.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                SaveWindowRender(manager, Path.Combine(output, $"custom-manager-{language}-{theme}.png"));
                manager.Width = 560; manager.Height = 560; manager.UpdateLayout(); CheckMappingButtons(manager);
                SaveWindowRender(manager, Path.Combine(output, $"custom-manager-small-{language}-{theme}.png"));
                foreach (var entry in settings.Mappings)
                {
                    var editor = new KeyboardMappingEditorWindow(entry, settings.Mappings, _ => null, _ => null, () => { }, (_, _) => null,
                        previewSurface: WizardSurface) { Owner = manager, ShowInTaskbar = false };
                    editor.Show();
                    try
                    {
                        WizardTo(editor, MappingWizardStep.Parameters);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
                        SaveWindowRender(editor, Path.Combine(output, $"custom-{entry.Action}-{language}-{theme}.png"));
                        editor.Width = 480; editor.Height = 560; editor.UpdateLayout(); CheckMappingButtons(editor);
                        SaveWindowRender(editor, Path.Combine(output, $"custom-{entry.Action}-small-{language}-{theme}.png"));
                    }
                    finally { DiscardWizard(editor); }
                }
            }
            finally { manager.Close(); }
        }
    }
}
