using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSettingShortcutTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var manager = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (IDictionary)KeyboardField(manager, "_windows");
        var constructor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
        NativePreviewWindow Preview(string title, bool managed = true, Func<bool>? readAudio = null, Action<bool>? writeAudio = null)
        {
            var parameters = constructor.GetParameters();
            var values = parameters.Select(parameter => parameter.DefaultValue).ToArray();
            object[] required = [390u, 844u, title, (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            if (managed) values[Array.FindIndex(parameters, parameter => parameter.Name == "managedContent")] =
                new Border { Background = Brushes.CornflowerBlue };
            values[Array.FindIndex(parameters, parameter => parameter.Name == "isAudioEnabled")] = readAudio;
            values[Array.FindIndex(parameters, parameter => parameter.Name == "setAudioEnabled")] = writeAudio;
            return (NativePreviewWindow)constructor.Invoke(values);
        }
        var firstAudio = true;
        using var first = Preview("Settings shortcut A", readAudio: () => firstAudio, writeAudio: enabled => firstAudio = enabled);
        using var second = Preview("Settings shortcut B");
        using var unavailable = Preview("Settings shortcut unavailable", managed: false);
        using (var positioned = Preview("Position-only preview", managed: false))
            TestIndependentWindowLayout(unavailable, positioned);
        windows["setting-test-a"] = first;
        windows["setting-test-b"] = second;
        nint foreground = first.Handle;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        var opacity = new SettingShortcutBinding { VirtualKey = 0x75, Value = 85 }; // F6
        var mouse = new SettingShortcutBinding { VirtualKey = (int)KeyboardShortcut.MouseXButton1, Value = 100 };
        var topmost = new SettingShortcutBinding { Setting = PreviewShortcutSetting.Topmost, VirtualKey = 0x76, Value = 1 };
        var hover = new SettingShortcutBinding { Setting = PreviewShortcutSetting.OpaqueOnHover, VirtualKey = 0x77, Value = 1 };
        var frame = new SettingShortcutBinding { Setting = PreviewShortcutSetting.PhoneFrame, VirtualKey = 0x79, Value = 0, Trigger = SettingShortcutTrigger.Toggle };
        app.UpdateSettings.SettingShortcuts = [opacity, mouse, topmost, hover, frame];
        var keys = (IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardCall(main, "GetConfiguredShortcuts")!;
        KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        bool Key(int vk, bool down) => router.RouteEvent(new(vk, 0, false), down,
            (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { });
        double Applied(NativePreviewWindow preview) => (double)KeyboardField(preview, "_appliedOpacity");
        try
        {
            KeyboardCall(first, "SetWindowOpacity", .2);
            InteractionAssert(Key(0x75, true) && Applied(first) == .85 && Applied(second) == 1,
                "A held key must change only its originating preview without reverse control.");
            Key(0x75, true);
            InteractionAssert((bool)KeyboardCall(main, "ProcessSettingMouseButton", (int)KeyboardShortcut.MouseXButton1, true)! && Applied(first) == 1,
                "The mouse shortcut must apply the second opacity level.");
            Key(0x75, false);
            InteractionAssert(Applied(first) == 1, "Releasing an older hold must preserve the newer hold.");
            Key(0x75, true);
            InteractionAssert(Applied(first) == .85, "A re-press must become newest even after a removed dictionary slot is reused.");
            Key(0x75, false);
            InteractionAssert(Applied(first) == 1, "Releasing the newest re-press must restore the held mouse value.");
            KeyboardCall(main, "ProcessSettingMouseButton", (int)KeyboardShortcut.MouseXButton1, false);
            InteractionAssert(Applied(first) == .2 && (double)KeyboardField(first, "_windowOpacity") == .2,
                "Final release must restore the unchanged baseline.");

            Key(0x75, true);
            KeyboardCall(first, "SetWindowOpacity", .35);
            Key(0x75, false);
            InteractionAssert(Applied(first) == .35, "A baseline edit while held must survive release.");
            KeyboardCall(first, "SetOpaqueOnHover", true);
            ((System.Windows.Threading.DispatcherTimer)KeyboardField(first, "_hoverOpacityTimer")).Stop();
            KeyboardCall(first, "UpdateHoverState", true);
            Key(0x75, true);
            InteractionAssert(Applied(first) == .85, "An explicit temporary value must take precedence over hover.");
            Key(0x75, false);
            InteractionAssert(Applied(first) == 1, "Release must restore the current hover policy.");
            KeyboardCall(first, "SetOpaqueOnHover", false);
            var hoverTimer = (System.Windows.Threading.DispatcherTimer)KeyboardField(first, "_hoverOpacityTimer");
            InteractionAssert(Key(hover.VirtualKey, true) && hoverTimer.IsEnabled && !(bool)KeyboardField(first, "_opaqueOnHover"),
                "Holding the hover shortcut must activate only the temporary mouse tracking policy.");
            KeyboardCall(first, "UpdateHoverState", true);
            InteractionAssert(Applied(first) == 1 && Applied(second) == 1, "A temporary hover policy must make the hovered preview opaque.");
            Key(0x75, true);
            InteractionAssert(Applied(first) == .85, "An explicit opacity shortcut must take precedence over the hover policy.");
            Key(0x75, false);
            InteractionAssert(Applied(first) == 1, "Releasing an opacity override must restore the held hover policy.");
            Key(hover.VirtualKey, false);
            InteractionAssert(!hoverTimer.IsEnabled && Applied(first) == .35,
                "Releasing the hover shortcut must stop mouse tracking and restore opacity.");
            InteractionAssert(first.BeginTemporarySetting(frame) is null,
                "A preview without a compatible frame must not retain a frame override.");
            Key(0x75, true);
            KeyboardCall(main, "OnIndependentKeyboardFocusChanged", "setting-test-a", first.Handle, false);
            InteractionAssert(Applied(first) == .35, "Losing focus must restore opacity even without a control session.");
            Key(0x75, false);

            Key(0x76, true);
            var topmostMenu = (System.Windows.Controls.MenuItem)KeyboardField(first, "_topMostItem");
            var nativeStyles = (nint)typeof(NativePreviewWindow).GetMethod("GetWindowLongPtrW",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [first.Handle, -20])!;
            InteractionAssert((nativeStyles.ToInt64() & 8) != 0 && topmostMenu.IsChecked && !topmostMenu.IsEnabled,
                "Temporary topmost must set the native style and update the menu state.");
            Key(0x76, false);
            nativeStyles = (nint)typeof(NativePreviewWindow).GetMethod("GetWindowLongPtrW",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [first.Handle, -20])!;
            InteractionAssert((nativeStyles.ToInt64() & 8) == 0 && !topmostMenu.IsChecked && topmostMenu.IsEnabled,
                "Release must restore both the HWND and the menu state.");
            InteractionAssert(!(bool)KeyboardField(first, "_isTopMost"), "Temporary topmost must not overwrite the baseline.");

            foreground = second.Handle;
            Key(0x75, true);
            InteractionAssert(Applied(first) == .35 && Applied(second) == .85, "A fresh press must target the second preview.");
            router.ReleaseAllPressedKeys();
            InteractionAssert(Applied(second) == 1, "Input handoff must restore held settings.");
            Key(0x75, false);
            foreground = 0;
            InteractionAssert(!Key(0x75, true), "Background apps must keep their own keys.");
            Key(0x75, false);
            InteractionAssert(unavailable.BeginTemporarySetting(opacity) is null && Applied(unavailable) == 1,
                "A failed rendering apply must not retain a temporary override.");

            foreground = first.Handle;
            app.IsUiPreviewMode = false;
            try
            {
                KeyboardCall(main, "ReconcileSettingMouseHook");
                InteractionAssert((nint)KeyboardField(main, "_settingMouseHook") != 0,
                    "Windows refused the settings mouse hook.");
                UnfixMouse(0x80, 0, 0, 1, 0); // XBUTTON1 down through the Windows hook chain.
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(Applied(first) == 1, "A real Windows side-button down did not apply the temporary value.");
                UnfixMouse(0x100, 0, 0, 1, 0);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(Applied(first) == .35, "A real Windows side-button up did not restore the baseline.");
            }
            finally
            {
                UnfixMouse(0x100, 0, 0, 1, 0);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                app.IsUiPreviewMode = true;
                KeyboardCall(main, "ReconcileSettingMouseHook");
            }

            TestSettingShortcutToggle(main, app, first, second, router, keys,
                value => foreground = value, Applied, Key);
            TestProjectionShortcutActions(main, app, first, second, keys, value => foreground = value, Key, () => firstAudio);
            TestProjectionShortcutScreenshot((MainViewModel)KeyboardField(main, "_viewModel"), output);
            TestSettingShortcutPersistence(app, main, keys, output);
            TestSettingShortcutEditor(app, keys, output);
            Console.WriteLine("PASS settings shortcuts: keyboard/mouse holds, repeats, nesting, hover, latest baseline, focus, handoff, isolation, failure rollback, persistence and five-language UI.");
            return 0;
        }
        finally
        {
            router.ReleaseAllPressedKeys();
            windows.Clear();
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static void TestProjectionShortcutScreenshot(MainViewModel vm, string output)
    {
        const string deviceA = "shortcut-frame-a", deviceB = "shortcut-frame-b";
        var core = (NativeCore)KeyboardField(vm, "_core");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        using var first = core.CreateExternalSession(deviceA, false, 0).Handle!;
        using var second = core.CreateExternalSession(deviceB, false, 0).Handle!;
        try
        {
            var pixels = Enumerable.Repeat((byte)128, 64 * 96 * 3 / 2).ToArray();
            core.PushExternalFrame(first, 1, 64, 96, pixels);
            core.PushExternalFrame(second, 1, 96, 64, pixels);
            sessions.Set(new DeviceCaptureState { Udid = deviceA, Handle = first });
            sessions.Set(new DeviceCaptureState { Udid = deviceB, Handle = second });
            var screenshot = vm.CreateDeviceScreenshotCapture(deviceB);
            InteractionAssert(screenshot() is { Width: 96, Height: 64 } &&
                vm.CreateDeviceScreenshotCapture(deviceA)() is { Width: 64, Height: 96 }, "Screenshots must read the requested preview's frame.");
            ScreenshotService.CapturePng(screenshot, Path.Combine(output, "device-b.png"));
            sessions.Remove(deviceB);
            InteractionAssert(screenshot() is null, "A closed preview must not return a frame or fall back to a different device.");
        }
        finally
        {
            sessions.Remove(deviceA);
            sessions.Remove(deviceB);
        }
    }

    private static void TestProjectionShortcutActions(MainWindow main, App app, NativePreviewWindow first, NativePreviewWindow second,
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> keys, Action<nint> foreground, Func<int, bool, bool> key, Func<bool> audio)
    {
        var previous = app.UpdateSettings.SettingShortcuts;
        app.UpdateSettings.SettingShortcuts = new[] { PreviewShortcutSetting.FullScreen, PreviewShortcutSetting.MuteWindow,
            PreviewShortcutSetting.RotateLeft, PreviewShortcutSetting.RotateRight, PreviewShortcutSetting.FixWindow }
            .Zip(new[] { 0x75, 0x76, 0x77, 0x79, 0x74 }, (setting, virtualKey) =>
                new SettingShortcutBinding { Setting = setting, Trigger = SettingShortcutTrigger.Toggle, VirtualKey = virtualKey, Value = 0 }).ToList();
        KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
        try
        {
            foreground(first.Handle);
            void Press(int vk)
            {
                InteractionAssert(key(vk, true), "The projection action must consume its shortcut.");
                key(vk, true); key(vk, false);
                DrainDispatcher();
            }
            Press(0x75);
            InteractionAssert(first.IsFullScreen && !second.IsFullScreen, "Full screen must target only the foreground preview.");
            Press(0x75);
            InteractionAssert(!first.IsFullScreen, "The next press must leave full screen.");
            Press(0x76);
            InteractionAssert(!audio(), "The mute shortcut must mute the foreground preview exactly once.");
            Press(0x76);
            InteractionAssert(audio(), "The next press must restore sound.");
            Press(0x77);
            InteractionAssert((int)KeyboardField(first, "_rotation") == 3 && (int)KeyboardField(second, "_rotation") == 0,
                "Rotate left must affect only the foreground preview.");
            Press(0x79);
            InteractionAssert((int)KeyboardField(first, "_rotation") == 0, "Rotate right must undo a single left rotation.");
            Press(0x74);
            InteractionAssert((bool)KeyboardField(first, "_isFixed") && !(bool)KeyboardField(second, "_isFixed") &&
                !(bool)KeyboardField(first, "_isTopMost"), "Fixing a window with a shortcut must lock just that window.");
            Press(0x74);
            InteractionAssert(!(bool)KeyboardField(first, "_isFixed"), "The next press must unlock the window.");
            key(0x77, true); key(0x77, false);
            foreground(second.Handle);
            DrainDispatcher();
            InteractionAssert((int)KeyboardField(first, "_rotation") == 0 && (int)KeyboardField(second, "_rotation") == 0,
                "A queued shortcut must be discarded when focus changes.");
        }
        finally
        {
            app.UpdateSettings.SettingShortcuts = previous;
            KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
        }
    }

    private static void TestIndependentWindowLayout(NativePreviewWindow preview, NativePreviewWindow positioned)
    {
        var preset = new IndependentWindowLayout(50, 80, 432, 654, true);
        InteractionAssert((bool)KeyboardCall(preview, "ApplyInitialLayout", preset)! &&
            (bool)KeyboardField(preview, "_isFixed"), "The window did not apply its saved size and lock before showing.");
        void AssertBounds()
        {
            InteractionAssert(UnfixGetWindowRect(preview.Handle, out var bounds) &&
                bounds.Left == 50 && bounds.Top == 80 && bounds.Right - bounds.Left == 432 &&
                bounds.Bottom - bounds.Top == 654, "The saved position or size was replaced by automatic layout.");
        }
        AssertBounds();
        preview.SetSourceDimensions(844, 390);
        DrainDispatcher();
        AssertBounds();
        InteractionAssert((bool)KeyboardCall(preview, "ApplyInitialLayout", preset with { X = -900000, Y = -900000 })! &&
            UnfixGetWindowRect(preview.Handle, out var restored) && restored.Left > -900000 && restored.Top > -900000,
            "An unavailable display left the preview unreachable.");
        InteractionAssert((bool)KeyboardCall(positioned, "ApplyInitialLayout", new IndependentWindowLayout(20, 40))!,
            "The position-only preset did not apply.");
        InteractionAssert(UnfixGetWindowRect(positioned.Handle, out var before),
            "The positioned preview has no bounds.");
        positioned.SetSourceDimensions(844, 390);
        DrainDispatcher();
        InteractionAssert(UnfixGetWindowRect(positioned.Handle, out var after) &&
            after.Left == before.Left && after.Top == before.Top && after.Right - after.Left != before.Right - before.Left,
            "Automatic resizing must honor the top-left anchor of the position-only preset.");
    }

    private static void TestSettingShortcutToggle(MainWindow main, App app, NativePreviewWindow first,
        NativePreviewWindow second, KeyboardInputRouter router,
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> keys, Action<nint> foreground,
        Func<NativePreviewWindow, double> applied, Func<int, bool, bool> key)
    {
        var previous = app.UpdateSettings.SettingShortcuts;
        var toggle = new SettingShortcutBinding { VirtualKey = 0x75, Value = 80, Trigger = SettingShortcutTrigger.Toggle };
        var mouse = new SettingShortcutBinding { VirtualKey = (int)KeyboardShortcut.MouseXButton1, Value = 60,
            Trigger = SettingShortcutTrigger.Toggle };
        app.UpdateSettings.SettingShortcuts = [toggle, mouse];
        KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
        try
        {
            foreground(first.Handle);
            key(0x75, true); key(0x75, true); key(0x75, false);
            InteractionAssert(applied(first) == .8, "Toggle must persist after key-up and must ignore key repeat.");
            KeyboardCall(main, "OnIndependentKeyboardFocusChanged", "setting-test-a", first.Handle, false);
            router.ReleaseAllPressedKeys();
            InteractionAssert(applied(first) == .8, "Toggle must persist across focus loss and input handoff.");
            key(0x75, true); key(0x75, false);
            InteractionAssert(applied(first) == .35, "A second complete key press must restore the original opacity.");
            for (var click = 0; click < 2; click++)
            {
                InteractionAssert((bool)KeyboardCall(main, "ProcessSettingMouseButton", mouse.VirtualKey, true)!,
                    "Toggle mouse down was not consumed.");
                KeyboardCall(main, "ProcessSettingMouseButton", mouse.VirtualKey, false);
                InteractionAssert(applied(first) == (click == 0 ? .6 : .35),
                    "Mouse toggle must apply and restore on separate clicks, not on release.");
            }
            key(0x75, true); key(0x75, false);
            foreground(second.Handle);
            KeyboardCall(main, "ObserveKeyboardForeground");
            InteractionAssert(applied(first) == .8 && applied(second) == 1, "Changing foreground must not move or reset a toggle.");
            key(0x75, true); key(0x75, false);
            InteractionAssert(applied(first) == .35 && applied(second) == .8,
                "A fresh toggle on another window must restore the previous window and target the new one.");
            KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
            InteractionAssert(applied(second) == 1, "Reconfiguring shortcuts must remove toggled overrides.");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<SettingShortcutBinding>("{\"VirtualKey\":117,\"Value\":80}")!;
            InteractionAssert(legacy.Trigger == SettingShortcutTrigger.Hold && legacy.IsValid,
                "Older settings without Trigger must retain hold behavior.");
            InteractionAssert(!(toggle with { Trigger = (SettingShortcutTrigger)999 }).IsValid, "An invalid trigger was accepted.");
            Console.WriteLine("PASS toggle: keyboard/mouse clicks, no key-repeat toggle, persistent focus/handoff, second-press restore, cross-window isolation and legacy hold migration.");
        }
        finally
        {
            router.ReleaseAllPressedKeys();
            app.UpdateSettings.SettingShortcuts = previous;
            KeyboardCall(main, "ConfigureKeyboardShortcuts", keys);
        }
    }

    private static void TestSettingShortcutPersistence(App app, MainWindow main,
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> keys, string output)
    {
        var store = new UpdateSettingsStore(Path.GetFullPath(Path.Combine(output, "settings.json")));
        SetKeyboardField(app, "_settingsStore", store);
        store.Save(app.UpdateSettings);
        var loaded = store.Load();
        InteractionAssert(loaded.SettingShortcuts.SequenceEqual(app.UpdateSettings.SettingShortcuts), "Setting shortcuts did not roundtrip.");
        var clone = loaded.Clone();
        clone.SettingShortcuts.Clear();
        InteractionAssert(loaded.SettingShortcuts.Count == 5, "Cloning shared the list.");
        var duplicate = new SettingShortcutBinding { VirtualKey = (int)keys[BluetoothShortcutAction.BluetoothControl].VirtualKey };
        InteractionAssert(KeyboardCall(main, "ApplyShortcutSettings", keys, new[] { duplicate }) is string,
            "A duplicate existing shortcut was accepted.");
        var blocked = Path.Combine(output, "blocked");
        File.WriteAllText(blocked, "blocked");
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(blocked), "settings.json")));
        var before = app.UpdateSettings.SettingShortcuts.ToArray();
        app.IsUiPreviewMode = false;
        // Avoid hook installation during this deliberate disk-failure test.
        SetKeyboardField(main, "_mappingClosing", true);
        try
        {
            var next = new[] { new SettingShortcutBinding { VirtualKey = 0x77, Value = 90 } };
            InteractionAssert(Equals(KeyboardCall(main, "ApplyShortcutSettings", keys, next),
                IPhoneMirror.App.Localization.LocalizationService.Get("ShortcutSettingsSaveFailed")) &&
                app.UpdateSettings.SettingShortcuts.SequenceEqual(before), "Failed save did not roll back shortcut settings.");
            SetKeyboardField(app, "_settingsStore", store);
            InteractionAssert(KeyboardCall(main, "ApplyShortcutSettings", keys, next) is null &&
                store.Load().SettingShortcuts.SequenceEqual(next), "Successful retry did not persist the new settings shortcut.");
            app.UpdateSettings.SettingShortcuts = before.ToList();
        }
        finally
        {
            app.IsUiPreviewMode = true;
            SetKeyboardField(main, "_mappingClosing", false);
            SetKeyboardField(app, "_settingsStore", store);
        }
    }

    private static void TestSettingShortcutEditor(App app,
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> keys, string output)
    {
        foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            SetAuditCulture(culture);
            ThemeService.Apply(theme);
            var previewRows = new[]
            {
                new SettingShortcutBinding { VirtualKey = (int)KeyboardShortcut.MouseXButton1, Value = 85, Trigger = SettingShortcutTrigger.Toggle },
                new SettingShortcutBinding { VirtualKey = (int)KeyboardShortcut.MouseXButton2, Value = 100, Trigger = SettingShortcutTrigger.Hold },
            };
            var editor = new ShortcutSettingsWindow(keys, _ => null, previewRows);
            try
            {
                editor.Show();
                DrainDispatcher();
                var row = editor.SettingRows[0];
                InteractionAssert(!editor.TryAssignSettingShortcut(row, keys[BluetoothShortcutAction.BluetoothControl]),
                    "The editor accepted an existing action's key.");
                InteractionAssert(editor.TryAssignSettingShortcut(row, new(KeyboardShortcut.Control, 0x75)), "The editor rejected a fresh key.");
                var legacy = editor.Rows.Single(item => item.Action == BluetoothShortcutAction.Home);
                InteractionAssert(!editor.TryAssignShortcut(legacy, row.Shortcut), "An existing action stole a settings shortcut.");
                InteractionAssert(row.SettingLabels.Count == Enum.GetValues<PreviewShortcutSetting>().Length &&
                    row.SettingLabels[4] != "SettingShortcutsRecording" &&
                    row.SettingLabels[(int)PreviewShortcutSetting.FixWindow] != "IndependentWindowFix",
                    "Every preview setting must have a localized label.");
                foreach (var setting in new[] { PreviewShortcutSetting.Topmost, PreviewShortcutSetting.OpaqueOnHover, PreviewShortcutSetting.PhoneFrame })
                {
                    row.SettingIndex = (int)setting;
                    editor.UpdateLayout();
                    InteractionAssert(row.BooleanVisibility == Visibility.Visible && row.OpacityVisibility == Visibility.Collapsed &&
                        row.ToBinding() is { Value: 1, IsValid: true }, "A boolean setting must start enabled and show its checkbox.");
                    row.BooleanValue = false;
                    InteractionAssert(row.ToBinding() is { Value: 0, IsValid: true }, "A boolean setting must save its disabled target.");
                }
                for (var index = (int)PreviewShortcutSetting.Recording; index < row.SettingLabels.Count; index++)
                {
                    row.SettingIndex = index;
                    editor.UpdateLayout();
                    row.TriggerIndex = (int)SettingShortcutTrigger.Hold;
                    InteractionAssert(row.SettingControlsVisibility == Visibility.Collapsed &&
                        row.ToBinding() is { Value: 0, Trigger: SettingShortcutTrigger.Toggle, IsValid: true },
                        "A press-only action must not expose or persist temporary setting values.");
                }
                row.SettingIndex = 0;
                editor.UpdateLayout();
                InteractionAssert(row.ToBinding().IsValid, "A hidden boolean checkbox corrupted the opacity target.");
                editor.TryAssignSettingShortcut(row, new(0, KeyboardShortcut.MouseXButton1));
                row.Value = 85;
                foreach (var scroll in FindVisualDescendants<ScrollViewer>(editor)) scroll.ScrollToEnd();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                SaveWindowRender(editor, Path.Combine(output, $"shortcuts-{culture}-{theme}.png"));
                var heading = (TextBlock)editor.FindName("MoreSettingsHeading");
                heading.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                SaveWindowRender(editor, Path.Combine(output, $"cards-{culture}-{theme}.png"));
            }
            finally { editor.Close(); }
        }
    }
}
