using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWindowsHotkeyCaptureTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        LocalizationService.SetLanguage("zh-CN");
        ThemeService.Apply(AppTheme.Light);
        var escaped = new List<int>();
        // Install behind production to prove system chords never escape the editor.
        ReviewHookProc observer = (code, message, pointer) =>
        {
            if (code < 0) return ReviewCallNextHook(0, code, message, pointer);
            escaped.Add(Marshal.ReadInt32(pointer));
            return 1;
        };
        var observerHook = ReviewSetHook(13, observer, ReviewGetModule(null), 0);
        InteractionAssert(observerHook != 0, "Cannot install the shell input observer.");
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        var editor = new ShortcutSettingsWindow(new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(), _ => null,
            applySettings: (_, _, rules) =>
            {
                var policy = PreviewWindowsHotkeyPolicy.Parse(rules?.Allow, rules?.Block, out var invalid);
                if (policy is null) return invalid;
                store.Save(new UpdateSettings { PreviewWindowsHotkeyAllowList = rules!.Value.Allow,
                    PreviewWindowsHotkeyBlockList = rules.Value.Block });
                return null;
            });
        try
        {
            editor.Show();
            ActivateMappingTestWindow(editor);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            var allow = (TextBox)editor.FindName("PreviewHotkeysAllow");
            var block = (TextBox)editor.FindName("PreviewHotkeysBlock");
            ((ScrollViewer)editor.FindName("ShortcutListScrollViewer")).ScrollToEnd();
            allow.Focus();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            InteractionAssert(allow.IsKeyboardFocused && (nint)KeyboardField(editor, "_ruleKeyboardHook") != 0,
                "Rule capture has no keyboard focus or hook.");
            InteractionAssert(MappingGetForeground() == (nint)KeyboardField(editor, "_ruleWindowHandle"),
                "Rule capture window is not the native foreground window.");
            escaped.Clear();
            SendReviewKeys([(0x5B, true), (0x5B, false)]);
            SendReviewKeys([(0x5B, true), (0x45, true), (0x45, false), (0x5B, false)]);
            SendReviewKeys([(0xA4, true), (0x09, true), (0x09, false), (0xA4, false)]);
            SendReviewKeys([(0xA2, true), (0xA0, true), (0x53, true), (0x53, true),
                (0x53, false), (0xA0, false), (0xA2, false)]);
            SendReviewKeys([(0x5B, true), (0x5B, false)]); // duplicate
            InteractionAssert(allow.Text.Replace("\r", "") == "Win\nWin+E\nAlt+Tab\nCtrl+Shift+S",
                $"Incorrect recorded hotkeys: {allow.Text}; escaped: {string.Join(',', escaped)}");
            InteractionAssert(escaped.Count == 0, $"A recorded hotkey reached Windows or the owner's hook: {string.Join(',', escaped)}.");
            block.Focus();
            SendReviewKeys([(0x70, true), (0x70, false)]);
            InteractionAssert(block.Text == "F1" && escaped.Count == 0, "Capture did not follow focus to the block list.");
            // Losing focus during a held Win must cancel standalone capture and drain key-up.
            SendReviewKeys([(0x5B, true)]);
            var save = (Button)editor.FindName("SaveButton");
            save.Focus();
            SendReviewKeys([(0x5B, false)]);
            InteractionAssert(block.Text == "F1" && escaped.Count == 0, "Focus loss recorded Win or leaked its release.");
            SendReviewKeys([(0x5B, true), (0x5B, false)]);
            InteractionAssert(escaped.SequenceEqual([0x5B, 0x5B]), "Capture intercepted keys outside the two rule boxes.");
            SaveWindowRender(editor, Path.Combine(output, "windows-hotkey-capture.png"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(!editor.IsVisible && (nint)KeyboardField(editor, "_ruleKeyboardHook") == 0,
                "Saving did not release the capture hook.");
            var saved = store.Load();
            InteractionAssert(saved.PreviewWindowsHotkeyAllowList == allow.Text && saved.PreviewWindowsHotkeyBlockList == "F1",
                "Recorded rules were not persisted.");
            var reopened = new ShortcutSettingsWindow(new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(), _ => null,
                hotkeys: (saved.PreviewWindowsHotkeyAllowList, saved.PreviewWindowsHotkeyBlockList));
            try
            {
                InteractionAssert(((TextBox)reopened.FindName("PreviewHotkeysAllow")).Text == allow.Text,
                    "Reopening lost captured rules.");
            }
            finally { reopened.Close(); }
            Console.WriteLine("Windows hotkey capture passed: Win, Win+E, Alt+Tab, Ctrl+Shift+S, repeat/duplicate suppression, F1, focus scope, cleanup and persistence.");
            return 0;
        }
        finally
        {
            if (editor.IsVisible) editor.Close();
            ReviewUnhook(observerHook);
            GC.KeepAlive(observer);
            app.Shutdown();
        }
    }
}
