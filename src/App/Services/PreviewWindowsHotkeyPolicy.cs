using System.Windows.Input;

namespace IPhoneMirror.App.Services;

internal enum WindowsHotkeyDisposition { Default, Allow, Block }

internal sealed class PreviewWindowsHotkeyPolicy
{
    private readonly Dictionary<KeyboardShortcut, WindowsHotkeyDisposition> _rules = [];
    private readonly bool[] _modifierPrefixes = new bool[16];

    internal bool HasRules => _rules.Count != 0;
    internal bool HasModifierPrefix(uint modifiers) => modifiers is > 0 and < 16 && _modifierPrefixes[modifiers];

    internal WindowsHotkeyDisposition Match(int key, uint modifiers) =>
        _rules.GetValueOrDefault(new(modifiers, (uint)key));

    internal WindowsHotkeyDisposition Standalone(int key) =>
        key is 0x5B or 0x5C ? Match(0x5B, 0) : WindowsHotkeyDisposition.Default;

    internal static PreviewWindowsHotkeyPolicy? Parse(string? allow, string? block, out string? invalid)
    {
        var policy = new PreviewWindowsHotkeyPolicy();
        invalid = policy.Read(allow, WindowsHotkeyDisposition.Allow) ??
            policy.Read(block, WindowsHotkeyDisposition.Block);
        return invalid is null ? policy : null;
    }

    private string? Read(string? text, WindowsHotkeyDisposition disposition)
    {
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseChord(line, out var chord) || !_rules.TryAdd(chord, disposition)) return line;
            for (uint prefix = 1; prefix < _modifierPrefixes.Length; prefix++)
                if ((prefix & chord.Modifiers) == prefix) _modifierPrefixes[prefix] = true;
        }
        return null;
    }

    private static bool TryParseChord(string line, out KeyboardShortcut chord)
    {
        chord = default;
        var parts = line.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyboardShortcut.Control,
                "ALT" => KeyboardShortcut.Alt,
                "SHIFT" => KeyboardShortcut.Shift,
                "WIN" or "WINDOWS" => KeyboardShortcut.Windows,
                _ => 0u,
            };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        var name = parts[^1];
        if (modifiers == 0 && name.ToUpperInvariant() is "WIN" or "WINDOWS" or "LWIN" or "RWIN")
        {
            chord = new(0, 0x5B);
            return true;
        }
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;
        if (name.Equals("Esc", StringComparison.OrdinalIgnoreCase)) name = "Escape";
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || name.Contains(',') ||
            !Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return false;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || KeyboardShortcutRecognizer.Modifier(virtualKey) != 0) return false;
        chord = new(modifiers, (uint)virtualKey);
        return true;
    }
}
