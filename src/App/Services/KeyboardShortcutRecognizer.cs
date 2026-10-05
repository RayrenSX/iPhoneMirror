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
    {
        foreach (var binding in _bindings)
            if (binding.Keys.VirtualKey == virtualKey && binding.Keys.Modifiers == modifiers && binding.IsAvailable())
                return binding;
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
