using System.Runtime.InteropServices;
using System.Text;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal static class KeyboardMappingKeys
{
    internal static string Display(MappedKey? key)
    {
        if (key is null) return LocalizationService.Get("MappingCaptureHint");
        var name = new StringBuilder(128);
        if (key.ScanCode != 0 && GetKeyNameText((key.ScanCode << 16) |
                (key.Extended ? 1 << 24 : 0), name, name.Capacity) != 0)
            return name.ToString();
        return new KeyboardShortcut(0, (uint)key.VirtualKey).DisplayText;
    }

    internal static int CurrentVirtualKey(MappedKey key)
    {
        var scan = (uint)key.ScanCode | (key.Extended ? 0xE000u : 0);
        var mapped = key.ScanCode == 0 ? 0 : MapVirtualKey(scan, 3);
        return mapped == 0 ? key.VirtualKey : (int)mapped;
    }

    internal static string? Conflict(MappedKey key, IEnumerable<KeyboardShortcut> shortcuts)
    {
        if (key.Validate() is { } error) return error;
        var current = CurrentVirtualKey(key);
        // Local full-screen and refresh commands precede ordinary input. Escape
        // remains bindable outside full screen and is runtime-gated inside it.
        if (key.VirtualKey is 0x74 or 0x7A || current is 0x74 or 0x7A)
            return "MappingShortcutConflict";
        if (shortcuts.Any(s => s.IsBound && s.Modifiers == 0 &&
                (s.VirtualKey == key.VirtualKey || s.VirtualKey == current)))
            return "MappingShortcutConflict";
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder text, int size);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
