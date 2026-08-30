using System.Globalization;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Services;

/// <summary>Represents one user-configurable Windows global hotkey.</summary>
internal readonly record struct KeyboardShortcut(uint Modifiers, uint VirtualKey)
{
    internal const uint Alt = 0x0001;
    internal const uint Control = 0x0002;
    internal const uint Shift = 0x0004;
    internal const uint NoRepeat = 0x4000;
    internal static KeyboardShortcut Default { get; } = new(0, 0x78); // F9
    internal static KeyboardShortcut BossKeyDefault { get; } = new(Control | Alt, 0x42); // Ctrl+Alt+B
    internal static KeyboardShortcut Unbound { get; } = new(0, 0);
    internal bool IsBound => VirtualKey != 0;

    internal uint RegistrationModifiers => Modifiers | NoRepeat;

    internal static KeyboardShortcut FromSettings(UpdateSettings settings) =>
        IsValid(settings.BluetoothControlShortcutModifiers,
            settings.BluetoothControlShortcutVirtualKey)
            ? new((uint)settings.BluetoothControlShortcutModifiers,
                (uint)settings.BluetoothControlShortcutVirtualKey)
            : Default;

    internal static KeyboardShortcut FromSettings(UpdateSettings settings,
        BluetoothShortcutAction action) => action switch
        {
            BluetoothShortcutAction.ReverseControl => FromSettings(settings),
            BluetoothShortcutAction.ControlCenter => FromStoredSettings(
                settings.BluetoothControlCenterShortcutModifiers,
                settings.BluetoothControlCenterShortcutVirtualKey, Unbound),
            BluetoothShortcutAction.NotificationCenter => FromStoredSettings(
                settings.BluetoothNotificationCenterShortcutModifiers,
                settings.BluetoothNotificationCenterShortcutVirtualKey, Unbound),
            BluetoothShortcutAction.AppSwitcher => FromStoredSettings(
                settings.BluetoothAppSwitcherShortcutModifiers,
                settings.BluetoothAppSwitcherShortcutVirtualKey, Unbound),
            BluetoothShortcutAction.Home => FromStoredSettings(
                settings.BluetoothHomeShortcutModifiers,
                settings.BluetoothHomeShortcutVirtualKey, Unbound),
            BluetoothShortcutAction.BossKey => FromStoredSettings(
                settings.BluetoothBossShortcutModifiers,
                settings.BluetoothBossShortcutVirtualKey, BossKeyDefault),
            BluetoothShortcutAction.Dock => FromStoredSettings(
                settings.BluetoothDockShortcutModifiers,
                settings.BluetoothDockShortcutVirtualKey, Unbound),
            BluetoothShortcutAction.Siri => FromStoredSettings(
                settings.BluetoothSiriShortcutModifiers,
                settings.BluetoothSiriShortcutVirtualKey, Unbound),
            _ => Default,
        };

    private static KeyboardShortcut FromStoredSettings(int modifiers, int virtualKey,
        KeyboardShortcut fallback) => IsValid(modifiers, virtualKey)
            ? new((uint)modifiers, (uint)virtualKey) : fallback;

    internal static bool TryCreate(Key key, ModifierKeys modifiers,
        out KeyboardShortcut shortcut)
    {
        shortcut = default;
        if (key is Key.None or Key.DeadCharProcessed or Key.F12 ||
            IsModifierKey(key) || modifiers.HasFlag(ModifierKeys.Windows))
            return false;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;

        var normalizedModifiers = ToNativeModifiers(modifiers);
        if (normalizedModifiers == 0 && key is not Key.Escape and < Key.F1 or > Key.F11)
            return false;

        shortcut = new KeyboardShortcut(normalizedModifiers, (uint)virtualKey);
        return true;
    }

    internal bool Matches(Key key, ModifierKeys modifiers) =>
        TryCreate(key, modifiers, out var shortcut) && shortcut == this;

    internal bool MatchesVirtualKey(int virtualKey, bool controlPressed,
        bool altPressed, bool shiftPressed) =>
        VirtualKey == (uint)virtualKey &&
        ((Modifiers & Control) != 0) == controlPressed &&
        ((Modifiers & Alt) != 0) == altPressed &&
        ((Modifiers & Shift) != 0) == shiftPressed;

    internal string DisplayText
    {
        get
        {
            if (!IsBound) return LocalizationService.Get("ShortcutSettingsUnbound");
            var parts = new List<string>(4);
            if ((Modifiers & Control) != 0)
                parts.Add(LocalizationService.Get("ShortcutModifierControl"));
            if ((Modifiers & Alt) != 0)
                parts.Add(LocalizationService.Get("ShortcutModifierAlt"));
            if ((Modifiers & Shift) != 0)
                parts.Add(LocalizationService.Get("ShortcutModifierShift"));
            parts.Add(FormatKey());
            return string.Join(" + ", parts);
        }
    }

    internal static bool IsValid(int modifiers, int virtualKey) =>
        (modifiers == 0 && virtualKey == 0) ||
        (modifiers is >= 0 and <= (int)(Alt | Control | Shift) &&
        virtualKey is > 0 and <= 0xFE &&
        TryCreate(KeyInterop.KeyFromVirtualKey(virtualKey),
            ToModifierKeys((uint)modifiers), out _));

    internal static KeyboardShortcut DefaultFor(BluetoothShortcutAction action) => action switch
    {
        BluetoothShortcutAction.ReverseControl => Default,
        BluetoothShortcutAction.BossKey => BossKeyDefault,
        _ => Unbound,
    };

    internal static bool HaveUniqueBoundValues(
        IEnumerable<KeyboardShortcut> shortcuts)
    {
        var bound = shortcuts.Where(shortcut => shortcut.IsBound).ToArray();
        return bound.Distinct().Count() == bound.Length;
    }

    private string FormatKey()
    {
        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        if (key is >= Key.A and <= Key.Z)
            return key.ToString();
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString(CultureInfo.InvariantCulture);
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return LocalizationService.Format("ShortcutKeyNumPadFormat", key - Key.NumPad0);
        return new KeyConverter().ConvertToString(null, CultureInfo.CurrentCulture, key)
            ?? VirtualKey.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftAlt or Key.RightAlt or
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= Shift;
        return result;
    }

    private static ModifierKeys ToModifierKeys(uint modifiers)
    {
        var result = ModifierKeys.None;
        if ((modifiers & Control) != 0) result |= ModifierKeys.Control;
        if ((modifiers & Alt) != 0) result |= ModifierKeys.Alt;
        if ((modifiers & Shift) != 0) result |= ModifierKeys.Shift;
        return result;
    }
}

internal enum BluetoothShortcutAction
{
    ReverseControl,
    ControlCenter,
    NotificationCenter,
    AppSwitcher,
    Home,
    BossKey,
    Dock,
    Siri,
}
