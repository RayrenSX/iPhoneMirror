namespace IPhoneMirror.App.Services;

// No transport or mapping dependencies. Rebuilt only when settings change.
internal sealed record RoutedShortcut(KeyboardShortcut Keys, string Name,
    Func<bool> IsAvailable, Action Execute);

internal sealed class KeyboardShortcutRecognizer
{
    private RoutedShortcut[] _bindings = [];
    internal void Configure(IEnumerable<RoutedShortcut> bindings) =>
        _bindings = bindings.Where(b => b.Keys.IsBound).ToArray();
    internal RoutedShortcut? Match(int virtualKey, uint modifiers)
        => Match(virtualKey, modifiers, out _);

    internal RoutedShortcut? Match(int virtualKey, uint modifiers, out string reason)
    {
        reason = "unbound_key";
        foreach (var binding in _bindings)
        {
            if (binding.Keys.VirtualKey != virtualKey) continue;
            if (binding.Keys.Modifiers != modifiers)
            {
                if (reason == "unbound_key") reason = "modifiers_mismatch";
                continue;
            }
            // Evaluate availability exactly once; diagnostics must not change
            // precedence or run a stateful guard a second time.
            if (!binding.IsAvailable()) { reason = "shortcut_unavailable"; continue; }
            reason = "shortcut_matched";
            return binding;
        }
        return null;
    }
    internal bool IsCandidate(uint modifiers)
    {
        if (modifiers == 0) return false;
        foreach (var binding in _bindings)
            if ((binding.Keys.Modifiers & modifiers) == modifiers && binding.IsAvailable()) return true;
        return false;
    }
    internal static uint Modifier(int key) => key switch
    {
        0xA2 or 0xA3 or 0x11 => KeyboardShortcut.Control,
        0xA0 or 0xA1 or 0x10 => KeyboardShortcut.Shift,
        0xA4 or 0xA5 or 0x12 => KeyboardShortcut.Alt,
        0x5B or 0x5C => 8, // Win is deliberately excluded by current shortcut settings
        _ => 0,
    };
}
