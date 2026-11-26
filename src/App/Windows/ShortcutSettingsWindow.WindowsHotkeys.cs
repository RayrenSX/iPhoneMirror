using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class ShortcutSettingsWindow
{
    private delegate nint RuleKeyboardProc(int code, nint message, nint data);
    private RuleKeyboardProc? _ruleKeyboardProc;
    private nint _ruleKeyboardHook;
    private nint _ruleWindowHandle;
    private readonly HashSet<int> _rulePressed = [];
    private readonly HashSet<int> _ruleSuppressed = [];
    private TextBox? _standaloneWinTarget;

    private void InitializeWindowsHotkeyCapture()
    {
        PreviewHotkeysAllow.GotKeyboardFocus += (_, _) => StatusText = string.Empty;
        PreviewHotkeysBlock.GotKeyboardFocus += (_, _) => StatusText = string.Empty;
        PreviewHotkeysAllow.LostKeyboardFocus += (_, _) => _standaloneWinTarget = null;
        PreviewHotkeysBlock.LostKeyboardFocus += (_, _) => _standaloneWinTarget = null;
        // A WPF key handler cannot stop shell shortcuts such as Win and Alt+Tab.
        // Install this after the owner's hook, and intercept only in these boxes.
        Loaded += (_, _) =>
        {
            if (_ruleKeyboardHook != 0) return;
            _ruleWindowHandle = new WindowInteropHelper(this).Handle;
            _ruleKeyboardProc = CaptureWindowsHotkey;
            _ruleKeyboardHook = RuleSetWindowsHookEx(13, _ruleKeyboardProc, RuleGetModuleHandle(null), 0);
            if (_ruleKeyboardHook == 0)
                DiagnosticLogger.Warning("shortcuts", "rule_capture_hook_failed",
                    ("error", Marshal.GetLastWin32Error()));
        };
        Deactivated += (_, _) => _standaloneWinTarget = null;
        Closed += (_, _) =>
        {
            if (_ruleKeyboardHook != 0) RuleUnhookWindowsHookEx(_ruleKeyboardHook);
            _ruleKeyboardHook = 0;
            _rulePressed.Clear();
            _ruleSuppressed.Clear();
            _standaloneWinTarget = null;
        };
    }

    private nint CaptureWindowsHotkey(int code, nint message, nint data)
    {
        if (code < 0 || (message != 0x100 && message != 0x104 && message != 0x101 && message != 0x105))
            return RuleCallNextHookEx(0, code, message, data);
        var key = Marshal.ReadInt32(data);
        var down = message == 0x100 || message == 0x104;
        var target = RuleGetForegroundWindow() == _ruleWindowHandle ? Keyboard.FocusedElement as TextBox : null;
        if (target != PreviewHotkeysAllow && target != PreviewHotkeysBlock) target = null;
        var handled = ProcessWindowsHotkeyCapture(target, key, down);
        return handled ? 1 : RuleCallNextHookEx(0, code, message, data);
    }

    internal bool ProcessWindowsHotkeyCapture(TextBox? target, int key, bool down)
    {
        if (!down)
        {
            _rulePressed.Remove(key);
            if (!_ruleSuppressed.Remove(key)) return false;
            if (key is 0x5B or 0x5C && _standaloneWinTarget == target && target is not null &&
                !_rulePressed.Any(k => KeyboardShortcutRecognizer.Modifier(k) == KeyboardShortcut.Windows))
                AppendWindowsHotkey(target, "Win");
            if (key is 0x5B or 0x5C) _standaloneWinTarget = null;
            return true;
        }
        if (target is null)
        {
            _standaloneWinTarget = null;
            return _ruleSuppressed.Contains(key);
        }
        var first = _rulePressed.Add(key);
        var modifier = KeyboardShortcutRecognizer.Modifier(key);
        var modifiers = _rulePressed.Aggregate(0u, (value, held) => value | KeyboardShortcutRecognizer.Modifier(held));
        if (modifier != 0)
        {
            if (first && modifier == KeyboardShortcut.Windows && modifiers == KeyboardShortcut.Windows)
                _standaloneWinTarget = target;
            else if (modifier != KeyboardShortcut.Windows) _standaloneWinTarget = null;
            // Shift alone still supports typing rule names and selecting text.
            if (modifiers == KeyboardShortcut.Shift) return false;
            _ruleSuppressed.Add(key);
            return true;
        }
        _standaloneWinTarget = null;
        // Keep ordinary text, navigation, deletion and paste via the context menu.
        // Modified chords and function keys are recorded instead of executed.
        if ((modifiers & (KeyboardShortcut.Control | KeyboardShortcut.Alt | KeyboardShortcut.Windows)) == 0 &&
            key is not (>= 0x70 and <= 0x87)) return false;
        _ruleSuppressed.Add(key);
        if (!first) return true;
        var parts = new List<string>();
        if ((modifiers & KeyboardShortcut.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & KeyboardShortcut.Alt) != 0) parts.Add("Alt");
        if ((modifiers & KeyboardShortcut.Shift) != 0) parts.Add("Shift");
        if ((modifiers & KeyboardShortcut.Windows) != 0) parts.Add("Win");
        var name = KeyInterop.KeyFromVirtualKey(key).ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) name = name[1..];
        parts.Add(name);
        var text = string.Join('+', parts);
        if (PreviewWindowsHotkeyPolicy.Parse(text, null, out _) is not null)
            AppendWindowsHotkey(target, text);
        return true;
    }

    private void AppendWindowsHotkey(TextBox target, string chord)
    {
        if (!target.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(chord, StringComparer.OrdinalIgnoreCase))
        {
            var text = target.Text.TrimEnd();
            text = text.Length == 0 ? chord : text + Environment.NewLine + chord;
            if (text.Length > target.MaxLength) return;
            target.Text = text;
        }
        target.CaretIndex = target.Text.Length;
        target.ScrollToEnd();
        StatusText = string.Empty;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint RuleSetWindowsHookEx(int type, RuleKeyboardProc callback, nint module, uint thread);
    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
    private static extern bool RuleUnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint RuleCallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint RuleGetForegroundWindow();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint RuleGetModuleHandle(string? name);
}
