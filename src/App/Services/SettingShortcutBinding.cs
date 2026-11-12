namespace IPhoneMirror.App.Services;

public enum PreviewShortcutSetting { Opacity, Topmost }
public enum SettingShortcutTrigger { Hold, Toggle }

// Values are percentages for opacity and 0/1 for boolean settings.
public sealed record SettingShortcutBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PreviewShortcutSetting Setting { get; init; }
    // Missing fields in older settings retain the original hold behavior.
    public SettingShortcutTrigger Trigger { get; init; }
    public int Modifiers { get; init; }
    public int VirtualKey { get; init; }
    public double Value { get; init; } = 100;
    internal KeyboardShortcut Shortcut => new((uint)Modifiers, (uint)VirtualKey);
    internal bool IsValid => Id != Guid.Empty && Enum.IsDefined(Setting) && Enum.IsDefined(Trigger) &&
        KeyboardShortcut.IsValid(Modifiers, VirtualKey) && double.IsFinite(Value) &&
        (Setting == PreviewShortcutSetting.Opacity ? Value is >= 10 and <= 100 : Value is 0 or 1);
}
