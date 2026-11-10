using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
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
        NativePreviewWindow Preview(string title, bool managed = true)
        {
            var parameters = constructor.GetParameters();
            var values = parameters.Select(parameter => parameter.DefaultValue).ToArray();
            object[] required = [390u, 844u, title, (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            if (managed) values[Array.FindIndex(parameters, parameter => parameter.Name == "managedContent")] =
                new Border { Background = Brushes.CornflowerBlue };
            return (NativePreviewWindow)constructor.Invoke(values);
        }
        using var first = Preview("Settings shortcut A");
        using var second = Preview("Settings shortcut B");
        using var unavailable = Preview("Settings shortcut unavailable", managed: false);
        windows["setting-test-a"] = first;
        windows["setting-test-b"] = second;
        nint foreground = first.Handle;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        var opacity = new SettingShortcutBinding { VirtualKey = 0x75, Value = 85 }; // F6
        var mouse = new SettingShortcutBinding { VirtualKey = (int)KeyboardShortcut.MouseXButton1, Value = 100 };
        var topmost = new SettingShortcutBinding { Setting = PreviewShortcutSetting.Topmost, VirtualKey = 0x76, Value = 1 };
        app.UpdateSettings.SettingShortcuts = [opacity, mouse, topmost];
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
            Key(0x75, true);
            KeyboardCall(main, "OnIndependentKeyboardFocusChanged", "setting-test-a", first.Handle, false);
            InteractionAssert(Applied(first) == .35, "Losing focus must restore opacity even without a control session.");
            Key(0x75, false);

            Key(0x76, true);
            var nativeStyles = (nint)typeof(NativePreviewWindow).GetMethod("GetWindowLongPtrW",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [first.Handle, -20])!;
            InteractionAssert((nativeStyles.ToInt64() & 8) != 0,
                "Temporary topmost must set the native WS_EX_TOPMOST style.");
            Key(0x76, false);
            nativeStyles = (nint)typeof(NativePreviewWindow).GetMethod("GetWindowLongPtrW",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [first.Handle, -20])!;
            InteractionAssert((nativeStyles.ToInt64() & 8) == 0, "Release must remove temporary topmost from the HWND.");
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
        InteractionAssert(loaded.SettingShortcuts.Count == 3, "Cloning shared the list.");
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
            var editor = new ShortcutSettingsWindow(keys, _ => null, app.UpdateSettings.SettingShortcuts);
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
                row.SettingIndex = 1;
                editor.UpdateLayout();
                InteractionAssert(row.ToBinding().IsValid, "A hidden opacity slider corrupted a boolean target.");
                row.BooleanValue = false;
                row.SettingIndex = 0;
                editor.UpdateLayout();
                InteractionAssert(row.ToBinding().IsValid, "A hidden boolean checkbox corrupted the opacity target.");
                editor.TryAssignSettingShortcut(row, new(0, 0x75));
                foreach (var scroll in FindVisualDescendants<ScrollViewer>(editor)) scroll.ScrollToEnd();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                SaveWindowRender(editor, Path.Combine(output, $"shortcuts-{culture}-{theme}.png"));
            }
            finally { editor.Close(); }
        }
    }
}
