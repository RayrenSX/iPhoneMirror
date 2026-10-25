using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestFullScreenKeyEditorGuard(MainWindow window, string udid, string context)
    {
        // F11/Esc have their own foreground check. Every other local shortcut
        // and device typing must keep the shared editor guard, including in
        // device full screen. Match() evaluates the production bindings only;
        // nothing is executed and no key reaches the desktop or the phone.
        var router = (KeyboardInputRouter)KeyboardField(window, "_keyboardRouter");
        var shell = (Panel)window.FindName("AppShell");
        var main = new WindowInteropHelper(window).Handle;
        var foregroundProvider = KeyboardField(window, "_keyboardForegroundWindow");
        var focusProvider = KeyboardField(window, "_keyboardFocusedWindow");
        var previousTitle = window.Title;
        var previousShowInTaskbar = window.ShowInTaskbar;
        var editor = new TextBox { Width = 160, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var button = new Button { Width = 80, Height = 32, Content = "Panel button",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var ordinary = new (int Key, uint Modifiers, string Name)[]
        {
            (0x74, 0, "RefreshDevices"),
            (0x52, KeyboardShortcut.Control, "RefreshPreview"),
            (0x50, KeyboardShortcut.Control | KeyboardShortcut.Shift, "PreviewWindow"),
            (0x4C, KeyboardShortcut.Control, "Diagnostics"),
            (0x4D, KeyboardShortcut.Control, "Audio"),
            (0x53, KeyboardShortcut.Control, "Screenshot"),
        };
        string? Match(int key, uint modifiers) => router.Shortcuts.Match(key, modifiers)?.Name;
        string[] OrdinaryAvailable() => ordinary.Where(s => Match(s.Key, s.Modifiers) == s.Name)
            .Select(s => s.Name).ToArray();
        bool Typing() => (bool)KeyboardCall(window, "CanForwardControlKeyboard", udid, main)!;
        void State(bool fullScreen, bool settingsPanel, UIElement? focus)
        {
            SetKeyboardField(window, "_isFullScreen", fullScreen);
            SetKeyboardField(window, "_isSettingsPanelVisible", settingsPanel);
            if (focus is null) Keyboard.ClearFocus();
            else InteractionAssert(focus.Focus() && ReferenceEquals(Keyboard.FocusedElement, focus),
                $"{context}: the fixture could not focus {focus.GetType().Name}.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        }
        void Blocked(string scenario)
        {
            var leaked = OrdinaryAvailable();
            InteractionAssert(leaked.Length == 0,
                $"{context}: {scenario} let ordinary shortcuts bypass the editor guard: {string.Join(", ", leaked)}.");
            InteractionAssert(!Typing(), $"{context}: {scenario} forwarded keyboard input to the device.");
        }

        shell.Children.Add(editor);
        shell.Children.Add(button);
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => main));
        SetKeyboardField(window, "_keyboardFocusedWindow", (Func<nint>)(() => main));
        window.ShowInTaskbar = true;
        window.Title = "iPhoneMirror full-screen key guard regression";
        try
        {
            ActivateMappingTestWindow(window);
            InteractionAssert(!((ViewModels.MainViewModel)KeyboardField(window, "_viewModel")).IsMediaCastSelected,
                $"{context}: the fixture must select a device, not a media cast.");

            State(fullScreen: false, settingsPanel: false, focus: null);
            InteractionAssert(OrdinaryAvailable().Length == ordinary.Length && Typing() &&
                Match(0x7A, 0) == "FullScreen",
                $"{context}: without editor focus, local shortcuts and device typing must be available.");

            State(fullScreen: false, settingsPanel: false, focus: editor);
            Blocked("a focused text entry");
            InteractionAssert(Match(0x7A, 0) is null, $"{context}: a focused text entry lost F11.");

            State(fullScreen: true, settingsPanel: false, focus: editor);
            Blocked("a focused text entry in device full screen");
            InteractionAssert(Match(0x7A, 0) == "FullScreen" && Match(0x1B, 0) == "ExitFullScreen",
                $"{context}: device full screen kept F11/Esc for a hidden text entry.");

            State(fullScreen: false, settingsPanel: true, focus: button);
            Blocked("the settings panel");
            InteractionAssert(Match(0x7A, 0) == "FullScreen",
                $"{context}: the settings panel swallowed F11.");

            State(fullScreen: true, settingsPanel: true, focus: null);
            Blocked("the settings panel in device full screen");
            InteractionAssert(Match(0x7A, 0) == "FullScreen" && Match(0x1B, 0) == "ExitFullScreen",
                $"{context}: the settings panel left no keyboard way out of device full screen.");
            Console.WriteLine($"{context}: F11/Esc full-screen exception and text-entry shortcut/typing guards passed.");
        }
        finally
        {
            SetKeyboardField(window, "_isFullScreen", false);
            SetKeyboardField(window, "_isSettingsPanelVisible", false);
            Keyboard.ClearFocus();
            shell.Children.Remove(editor);
            shell.Children.Remove(button);
            SetKeyboardField(window, "_keyboardForegroundWindow", foregroundProvider);
            SetKeyboardField(window, "_keyboardFocusedWindow", focusProvider);
            window.Title = previousTitle;
            window.ShowInTaskbar = previousShowInTaskbar;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        }
    }
}
